using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 「AI 识图命名」后台调度器（AiImageNamingScheduler）的离线守护六场景：
/// 开关关＝TryEnqueue 零动作零请求、无 Key＝零请求＋状态条恰一次＋剩余清空、
/// 成功落板（ImageTitle 更新＋快照持久化＋IsAiNaming 终态 false）、整理期间
/// 卡被删＝放弃不落板、同卡重复入队＝会话内一次、排空轮次经 trackOperation
/// 挂入退出排空且完成。真 organizer＋DeepSeekClient 假 handler 注入＋假 Key
/// 字符串，全程零真联网；测试图片经 ImageSharp 现场生成（TestDirectory GUID
/// 隔离）；调度器持专用 STA Dispatcher 线程（板面状态与落板快照构建的线程
/// 契约与生产一致）。
/// </summary>
[TestClass]
public sealed class AiImageNamingSchedulerTests
{
    private const string ApiKey = "sk-test-fake-key";
    private const string NoKeyStatus = "尚未配置 DeepSeek API Key：设置 → AI 设置。";

    private static string CompletionBody(string content) =>
        """{"id":"1","model":"deepseek-flash","choices":[{"index":0,"message":{"role":"assistant","content":REPLACE_CONTENT},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}"""
            .Replace("REPLACE_CONTENT", JsonSerializer.Serialize(content));

    private static FakeHttpMessageHandler RespondWith(string content) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CompletionBody(content), Encoding.UTF8, "application/json")
        }));

    /// <summary>现场生成一张受管形态的 PNG（managed 库全部归一为 PNG）。</summary>
    private static BoardItem AddSampleImage(BoardService board, TestDirectory directory, string name)
    {
        var imagePath = Path.Combine(directory.Root, name);
        using (var image = new Image<Rgba32>(640, 480, new Rgba32(60, 120, 200)))
        {
            image.SaveAsPng(imagePath);
        }

        return board.AddImage(Guid.NewGuid(), $"images/{name}", imagePath);
    }

    private static AiImageNamingScheduler CreateScheduler(
        Dispatcher dispatcher,
        BoardService board,
        IBoardStore store,
        FakeHttpMessageHandler handler,
        List<string> messages,
        List<Task> trackedRounds,
        Func<AppPreferences> preferencesProvider) => new(
            new AiImageNamingOrganizer(new DeepSeekClient(handler)),
            board,
            new BoardMutationService(board, store, messages.Add),
            new PassthroughSecretProtector(),
            preferencesProvider,
            messages.Add,
            trackedRounds.Add,
            () => CancellationToken.None,
            dispatcher);

    [TestMethod]
    public void TryEnqueue_DisabledPerformsNoActionWithoutRequest()
    {
        using var directory = new TestDirectory();
        using var dispatcherThread = new StaDispatcherThread();
        var board = new BoardService();
        var item = AddSampleImage(board, directory, "disabled.png");
        var handler = new FakeHttpMessageHandler(
            (_, _) => throw new AssertFailedException("开关关闭不得发起任何请求。"));
        var messages = new List<string>();
        var trackedRounds = new List<Task>();
        var scheduler = CreateScheduler(
            dispatcherThread.Dispatcher,
            board,
            new RecordingBoardStore(directory.Root),
            handler,
            messages,
            trackedRounds,
            () => AppPreferences.Default with { DeepSeekApiKeyProtected = ApiKey });

        scheduler.TryEnqueue(item.Id);

        Assert.AreEqual(0, trackedRounds.Count, "开关关闭＝零动作：不得启动排空轮次。");
        Assert.AreEqual(0, handler.CallCount);
        Assert.AreEqual(0, messages.Count);
        Assert.IsNull(board.FindItem(item.Id)!.ImageTitle);
    }

    [TestMethod]
    public async Task DrainRound_WithoutKeyShowsStatusOnceClearsQueueWithoutRequest()
    {
        using var directory = new TestDirectory();
        using var dispatcherThread = new StaDispatcherThread();
        var board = new BoardService();
        var first = AddSampleImage(board, directory, "nokey-a.png");
        var second = AddSampleImage(board, directory, "nokey-b.png");
        var handler = new FakeHttpMessageHandler(
            (_, _) => throw new AssertFailedException("无 Key 不得发起任何请求。"));
        var messages = new List<string>();
        var trackedRounds = new List<Task>();
        // 偏好读取门：把首轮排空挡在读偏好处，保证第二张入队发生在同一轮内
        // （消除「首轮先完成、第二张另起一轮」的调度竞态，断言确定化）。
        var roundReachedPreferences = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRound = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = CreateScheduler(
            dispatcherThread.Dispatcher,
            board,
            new RecordingBoardStore(directory.Root),
            handler,
            messages,
            trackedRounds,
            () =>
            {
                roundReachedPreferences.TrySetResult();
                releaseRound.Task.Wait(TimeSpan.FromSeconds(30));
                return AppPreferences.Default;
            });

        scheduler.Enabled = true;
        scheduler.TryEnqueue(first.Id);
        await roundReachedPreferences.Task.WaitAsync(TimeSpan.FromSeconds(30));
        scheduler.TryEnqueue(second.Id);
        Assert.AreEqual(1, trackedRounds.Count, "两卡共一轮排空（在途轮次收编后续入队）。");

        releaseRound.TrySetResult();
        await trackedRounds[0].WaitAsync(TimeSpan.FromSeconds(30));

        Assert.AreEqual(0, handler.CallCount, "无 Key＝零请求（契约 #29 先例）。");
        CollectionAssert.AreEqual(
            new[] { NoKeyStatus },
            messages,
            "每轮状态条恰一次，不随卡数刷屏。");
        Assert.IsNull(board.FindItem(first.Id)!.ImageTitle, "剩余队列清空＝第二张不再尝试。");
        Assert.IsNull(board.FindItem(second.Id)!.ImageTitle);
        Assert.AreEqual(1, trackedRounds.Count, "清空后不再另起一轮。");
    }

    [TestMethod]
    public async Task DrainRound_NamesPersistsTitleAndClearsInFlightFlag()
    {
        using var directory = new TestDirectory();
        using var dispatcherThread = new StaDispatcherThread();
        var board = new BoardService();
        var item = AddSampleImage(board, directory, "success.png");
        var handler = RespondWith("\"蓝色山脉\"\n这是一张蓝色山脉的照片。");
        var messages = new List<string>();
        var trackedRounds = new List<Task>();
        var store = new RecordingBoardStore(directory.Root);
        var scheduler = CreateScheduler(
            dispatcherThread.Dispatcher,
            board,
            store,
            handler,
            messages,
            trackedRounds,
            () => AppPreferences.Default with { DeepSeekApiKeyProtected = ApiKey });

        scheduler.Enabled = true;
        scheduler.TryEnqueue(item.Id);
        await trackedRounds[0].WaitAsync(TimeSpan.FromSeconds(30));

        Assert.AreEqual(1, handler.CallCount);
        Assert.AreEqual("蓝色山脉", board.FindItem(item.Id)!.ImageTitle, "清洗后的标题必须落板。");
        Assert.IsFalse(board.FindItem(item.Id)!.IsAiNaming, "在途标志终态必须为 false。");
        Assert.AreEqual(
            "蓝色山脉",
            store.LastPersistedSnapshot!.Items.Single(stored => stored.Id == item.Id).ImageTitle,
            "标题必须经唯一落板管线随原子快照持久化。");
        CollectionAssert.AreEqual(new[] { "已为 1 张图片命名。" }, messages);
    }

    [TestMethod]
    public async Task DrainRound_ItemDeletedDuringNamingAbandonsWithoutLanding()
    {
        using var directory = new TestDirectory();
        using var dispatcherThread = new StaDispatcherThread();
        var board = new BoardService();
        var item = AddSampleImage(board, directory, "deleted.png");
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            // 命名在途卡被删：乐观并发守卫必须放弃结果、不落板。
            board.Remove(item.Id);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    CompletionBody("\"迟到的名字\""),
                    Encoding.UTF8,
                    "application/json")
            });
        });
        var messages = new List<string>();
        var trackedRounds = new List<Task>();
        var store = new RecordingBoardStore(directory.Root);
        var scheduler = CreateScheduler(
            dispatcherThread.Dispatcher,
            board,
            store,
            handler,
            messages,
            trackedRounds,
            () => AppPreferences.Default with { DeepSeekApiKeyProtected = ApiKey });

        scheduler.Enabled = true;
        scheduler.TryEnqueue(item.Id);
        await trackedRounds[0].WaitAsync(TimeSpan.FromSeconds(30));

        Assert.AreEqual(1, handler.CallCount);
        Assert.IsNull(board.FindItem(item.Id), "卡保持被删状态。");
        Assert.AreEqual(0, store.SaveCount, "放弃路径不得触发落板保存。");
        CollectionAssert.AreEqual(new[] { "图片已变化，命名结果已放弃。" }, messages);
    }

    [TestMethod]
    public async Task TryEnqueue_SameItemTwiceProcessesOncePerSession()
    {
        using var directory = new TestDirectory();
        using var dispatcherThread = new StaDispatcherThread();
        var board = new BoardService();
        var item = AddSampleImage(board, directory, "twice.png");
        var handler = RespondWith("\"重复卡\"");
        var messages = new List<string>();
        var trackedRounds = new List<Task>();
        var scheduler = CreateScheduler(
            dispatcherThread.Dispatcher,
            board,
            new RecordingBoardStore(directory.Root),
            handler,
            messages,
            trackedRounds,
            () => AppPreferences.Default with { DeepSeekApiKeyProtected = ApiKey });

        scheduler.Enabled = true;
        scheduler.TryEnqueue(item.Id);
        scheduler.TryEnqueue(item.Id);
        foreach (var round in trackedRounds)
        {
            await round.WaitAsync(TimeSpan.FromSeconds(30));
        }

        Assert.AreEqual(1, handler.CallCount, "会话级一次性尝试标记：同卡重入队只处理一次。");
        Assert.AreEqual("重复卡", board.FindItem(item.Id)!.ImageTitle);
        CollectionAssert.AreEqual(new[] { "已为 1 张图片命名。" }, messages);
    }

    [TestMethod]
    public async Task TryEnqueue_TracksDrainRoundWhichCompletes()
    {
        using var directory = new TestDirectory();
        using var dispatcherThread = new StaDispatcherThread();
        var board = new BoardService();
        var item = AddSampleImage(board, directory, "tracked.png");
        var handler = new FakeHttpMessageHandler(
            (_, _) => throw new AssertFailedException("无 Key 轮次不得发起任何请求。"));
        var messages = new List<string>();
        var trackedRounds = new List<Task>();
        var scheduler = CreateScheduler(
            dispatcherThread.Dispatcher,
            board,
            new RecordingBoardStore(directory.Root),
            handler,
            messages,
            trackedRounds,
            () => AppPreferences.Default);

        scheduler.Enabled = true;
        scheduler.TryEnqueue(item.Id);

        Assert.AreEqual(1, trackedRounds.Count, "排空轮次必须经 trackOperation 挂入退出排空。");
        await trackedRounds[0].WaitAsync(TimeSpan.FromSeconds(30));
        Assert.AreEqual(
            TaskStatus.RanToCompletion,
            trackedRounds[0].Status,
            "轮次任务必须正常完成（退出排空可等待）。");
    }

    /// <summary>记录请求并可编程应答的假 handler；不发起任何真实网络。</summary>
    private sealed class FakeHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return await responder(request, cancellationToken);
        }
    }

    private sealed class PassthroughSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public string? Unprotect(string ciphertext) => ciphertext;
    }

    private sealed class RecordingBoardStore(string root) : IBoardStore
    {
        public int SaveCount { get; private set; }
        public BoardSnapshot? LastPersistedSnapshot { get; private set; }
        public string ImagesDirectory { get; } = Path.Combine(root, "images");

        public Task<BoardSnapshot> LoadBoardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BoardSnapshot());

        public Task SaveBoardAsync(BoardSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            LastPersistedSnapshot = snapshot;
            return Task.CompletedTask;
        }

        public Task<WindowSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(WindowSettings.Default);

        public Task SaveSettingsAsync(WindowSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public bool TryDeleteImage(string? absolutePath) => true;
    }
}

/// <summary>承载调度器执行体的专用 STA Dispatcher 线程（用后 InvokeShutdown）；
/// 供调度器测试与管线触发测试共用（板面状态与落板快照构建的线程契约与生产一致）。</summary>
internal sealed class StaDispatcherThread : IDisposable
{
    private readonly Thread _thread;

    public StaDispatcherThread()
    {
        using var started = new ManualResetEventSlim(false);
        Dispatcher? dispatcher = null;
        _thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            started.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "AiImageNaming dispatcher test thread"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        started.Wait();
        Dispatcher = dispatcher!;
    }

    public Dispatcher Dispatcher { get; }

    public void Dispose() => Dispatcher.InvokeShutdown();
}

/// <summary>
/// 管线触发测试共用装配：Enabled=true＋无 Key 偏好——最快可断言的排空路径
/// （轮次以「尚未配置 DeepSeek API Key」状态条收尾、零请求），handler 对任何
/// 请求直接判败（零请求红线）。
/// </summary>
internal static class AiImageNamingTriggerHarness
{
    internal static AiImageNamingScheduler CreateEnabledNoKeyScheduler(
        Dispatcher dispatcher,
        BoardService board,
        IBoardStore store,
        List<string> messages,
        List<Task> trackedRounds)
    {
        var scheduler = new AiImageNamingScheduler(
            new AiImageNamingOrganizer(new DeepSeekClient(new FailingHandler())),
            board,
            new BoardMutationService(board, store, _ => { }),
            new PassthroughSecretProtector(),
            () => AppPreferences.Default,
            messages.Add,
            trackedRounds.Add,
            () => CancellationToken.None,
            dispatcher);
        scheduler.Enabled = true;
        return scheduler;
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new AssertFailedException("无 Key 轮次不得发起任何请求。");
    }

    private sealed class PassthroughSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public string? Unprotect(string ciphertext) => ciphertext;
    }
}
