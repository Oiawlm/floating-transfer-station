using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using SixLabors.ImageSharp.PixelFormats;

namespace FloatingTransferStation.Tests;

/// <summary>
/// AI 识图命名管线触发（c2）：剪贴板采集管线只经「提交成功→TryEnqueue」一点
/// 触发——图片提交成功触发一轮排空、文字提交不触发、去重抑制（suppressed）的
/// 提交不触发。共用 AiImageNamingTriggerHarness（Enabled=true＋无 Key＝最快
/// 可断言路径，零请求红线由假 handler 判败承载）。
/// </summary>
public sealed partial class ClipboardCaptureServiceTests
{
    [TestMethod]
    public async Task HandleUpdate_ImageCommitTriggersAiNamingEnqueue()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var store = new FakeBoardStore(directory.Root);
        var messages = new List<string>();
        var trackedRounds = new List<Task>();
        using var dispatcherThread = new StaDispatcherThread();
        var scheduler = AiImageNamingTriggerHarness.CreateEnabledNoKeyScheduler(
            dispatcherThread.Dispatcher, board, store, messages, trackedRounds);
        var service = new ClipboardCaptureService(
            new QueueClipboardReader(new ClipboardSnapshot(
                130, null, [], null, [ClipboardImageCandidate.FromEncoded("PNG", [0x89, 0x50, 0x4E, 0x47])])),
            new FakeImageNormalizer(directory.Root),
            board,
            store,
            _ => { },
            aiImageNaming: scheduler);

        await service.HandleClipboardUpdateAsync();

        Assert.AreEqual(1, trackedRounds.Count, "图片提交成功必须触发 AI 命名入队。");
        await trackedRounds[0].WaitAsync(TimeSpan.FromSeconds(30));
        CollectionAssert.AreEqual(
            new[] { "尚未配置 DeepSeek API Key：设置 → AI 设置。" },
            messages,
            "触发后的排空轮次按无 Key 路径收尾（状态条恰一次、零请求）。");
    }

    [TestMethod]
    public async Task HandleUpdate_TextCommitDoesNotTriggerAiNamingEnqueue()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var store = new FakeBoardStore(directory.Root);
        var messages = new List<string>();
        var trackedRounds = new List<Task>();
        using var dispatcherThread = new StaDispatcherThread();
        var scheduler = AiImageNamingTriggerHarness.CreateEnabledNoKeyScheduler(
            dispatcherThread.Dispatcher, board, store, messages, trackedRounds);
        var service = new ClipboardCaptureService(
            new QueueClipboardReader(new ClipboardSnapshot(131, null, [], "文字内容")),
            new FakeImageNormalizer(directory.Root),
            board,
            store,
            _ => { },
            aiImageNaming: scheduler);

        await service.HandleClipboardUpdateAsync();

        Assert.AreEqual(1, board.Items(BoardCategory.Inbox).Count, "文字卡正常入库。");
        Assert.AreEqual(0, trackedRounds.Count, "文字提交不得触发 AI 命名入队。");
        Assert.AreEqual(0, messages.Count);
    }

    [TestMethod]
    public async Task HandleUpdate_DuplicateSuppressedCommitDoesNotTriggerAiNamingEnqueue()
    {
        using var directory = new TestDirectory();
        var encoded = await CreateEncodedImageAsync(3, 3, new Rgba32(30, 90, 160));
        var board = new BoardService();
        var store = new FakeBoardStore(directory.Root);
        var clock = new MutableCaptureClock();
        var pipelineMessages = new List<string>();
        var schedulerMessages = new List<string>();
        var trackedRounds = new List<Task>();
        using var dispatcherThread = new StaDispatcherThread();
        var scheduler = AiImageNamingTriggerHarness.CreateEnabledNoKeyScheduler(
            dispatcherThread.Dispatcher, board, store, schedulerMessages, trackedRounds);
        var service = new ClipboardCaptureService(
            new QueueClipboardReader(
                new ClipboardSnapshot(132, null, [], null, [ClipboardImageCandidate.FromEncoded("PNG", encoded)]),
                new ClipboardSnapshot(133, null, [], null, [ClipboardImageCandidate.FromEncoded("PNG", encoded)])),
            new ImageNormalizer(store.ImagesDirectory),
            board,
            store,
            pipelineMessages.Add,
            deduplicationGate: new CaptureDeduplicationGate(clock: () => clock.Now),
            aiImageNaming: scheduler);

        await service.HandleClipboardUpdateAsync();
        var roundsAfterFirstCommit = trackedRounds.Count;
        await service.HandleClipboardUpdateAsync();

        Assert.AreEqual(1, board.Items(BoardCategory.Inbox).Count, "同内容第二次采集被去重抑制。");
        Assert.AreEqual(
            DuplicateSuppressedMessage,
            pipelineMessages.Single(),
            "前置：第二次提交确实走了去重抑制路径。");
        Assert.AreEqual(
            roundsAfterFirstCommit,
            trackedRounds.Count,
            "去重抑制（未提交成功）的采集不得触发 AI 命名入队。");
    }
}
