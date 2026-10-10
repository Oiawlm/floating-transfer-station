using System.Windows;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 自动清理漏斗（1.25.0，逐卡 24 小时 TTL 语义）：装载闸门、装载补跑与巡检即删
/// 存量超龄卡（边界含等于）、未满期/未来时间戳保留、复盘条目永不触碰、关闭/在途/
/// 开关关闭无动作，以及关闭序列停表。时间一律显式传参,不依赖真实时钟。
/// 漏斗与生产一致地驱动在窗口 Dispatcher 上（ShowStatus 触碰依赖属性）。
/// </summary>
[TestClass]
public sealed class MainWindowAutoCleanupTests
{
    [STATestMethod]
    public void CheckAutoCleanup_BeforeBoardLoadCompletes_DoesNotSweep()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        var now = DateTimeOffset.UtcNow;
        var board = GetBoard(window);
        AddNonPinnedItems(board);
        board.AgeAllItems(now, hoursOld: 25);

        InvokeCheck(window, now);

        Assert.IsNull(preferencesStore.LastSaved, "TTL 清扫幂等，无记账落盘。");
        Assert.AreEqual(0, GetStore(window).SaveCount);
        AssertNonPinnedItemsIntact(board);
    }

    [STATestMethod]
    public void CheckAutoCleanup_FirstRunAfterLoad_SweepsExpiredCardsImmediately()
    {
        // 存量立即生效：升级后首次检查即删除所有入库已超 24h 的非置顶卡（用户裁决）。
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        var board = GetBoard(window);
        AddNonPinnedItems(board);
        board.AgeAllItems(DateTimeOffset.UtcNow, hoursOld: 25);
        SetField(window, "_boardLoadSucceeded", true);
        var now = DateTimeOffset.UtcNow;

        InvokeCheck(window, now);

        Assert.AreEqual(
            "已自动清理 3 张到期卡片，可 Ctrl+Z 撤销（仅本次运行内）",
            ((MainWindowViewModel)window.DataContext).StatusText);
        Assert.IsNull(preferencesStore.LastSaved, "TTL 判定幂等，清扫成功无需记账落盘。");
        Assert.AreEqual(1, GetStore(window).SaveCount, "清扫必须单次原子保存。");
        Assert.AreEqual(1, GetMutations(window).PendingUndoDeleteCount, "清扫必须单批可撤销。");
        Assert.AreEqual(0, board.Items(BoardCategory.CustomerOriginal).Count);
        Assert.AreEqual(0, board.Items(BoardCategory.Prompt).Count);
        Assert.AreEqual(1, board.Items(BoardCategory.Inbox).Count, "置顶卡超龄也绝不清理。");
    }

    [STATestMethod]
    public void OnBoardLoadCompleted_SweepsExpiredCardsImmediately()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        var board = GetBoard(window);
        AddNonPinnedItems(board);
        board.AgeAllItems(DateTimeOffset.UtcNow, hoursOld: 25);
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        RunOnDispatcher(window, window.OnBoardLoadCompleted);
        FlushDispatcher(window);
        FlushDispatcher(window);

        Assert.IsTrue(GetField<bool>(window, "_boardLoadSucceeded"));
        Assert.AreEqual(0, board.Items(BoardCategory.CustomerOriginal).Count);
        Assert.AreEqual(0, board.Items(BoardCategory.Prompt).Count);
        Assert.AreEqual(1, board.Items(BoardCategory.Inbox).Count);
        Assert.IsTrue(
            ((MainWindowViewModel)window.DataContext).StatusText.Contains("到期卡片"),
            "装载补跑必须真实执行清扫并提示。");
    }

    [STATestMethod]
    public void CheckAutoCleanup_CardsYoungerThan24h_AreKept()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        SetField(window, "_boardLoadSucceeded", true);
        var board = GetBoard(window);
        AddNonPinnedItems(board);
        var now = DateTimeOffset.UtcNow;

        InvokeCheck(window, now);

        Assert.IsNull(preferencesStore.LastSaved);
        Assert.AreEqual(0, GetStore(window).SaveCount);
        AssertNonPinnedItemsIntact(board);
        Assert.IsEmpty(((MainWindowViewModel)window.DataContext).StatusText);
    }

    [STATestMethod]
    public void CheckAutoCleanup_ExactlyAt24hBoundary_IsExpired()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        var board = GetBoard(window);
        AddNonPinnedItems(board);
        SetField(window, "_boardLoadSucceeded", true);
        var now = DateTimeOffset.UtcNow;
        board.SetAllItemsCreatedAt(now - AutoCleanupSchedule.CleanupInterval);

        InvokeCheck(window, now);

        Assert.AreEqual(0, board.Items(BoardCategory.CustomerOriginal).Count, "恰满 24h 即到期（边界含等于）。");
        Assert.AreEqual(0, board.Items(BoardCategory.Prompt).Count);
        Assert.AreEqual(1, board.Items(BoardCategory.Inbox).Count);
    }

    [STATestMethod]
    public void CheckAutoCleanup_FutureCreatedAt_IsKept()
    {
        // 时钟回拨/NTP 校正导致卡片时间戳在未来：年龄为负，自然不到期。
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        var board = GetBoard(window);
        AddNonPinnedItems(board);
        SetField(window, "_boardLoadSucceeded", true);
        var now = DateTimeOffset.UtcNow;
        board.SetAllItemsCreatedAt(now + TimeSpan.FromHours(2));

        InvokeCheck(window, now);

        Assert.AreEqual(0, GetStore(window).SaveCount);
        AssertNonPinnedItemsIntact(board);
    }

    [STATestMethod]
    public void CheckAutoCleanup_ExpiredReviewEntries_AreNeverSwept()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        var board = GetBoard(window);
        var reviewEntry = board.AddText("复盘标签条目", DailyReviewMigration.ReviewCategory);
        AddNonPinnedItems(board);
        board.AgeAllItems(DateTimeOffset.UtcNow, hoursOld: 25);
        SetField(window, "_boardLoadSucceeded", true);
        var now = DateTimeOffset.UtcNow;

        InvokeCheck(window, now);

        // 做旧经快照回合重建条目实例，按 Id 断言而非实例同一。
        CollectionAssert.AreEqual(
            new[] { reviewEntry.Id },
            board.Items(DailyReviewMigration.ReviewCategory).Select(item => item.Id).ToArray(),
            "复盘复用的 Reference 分类不在清扫范围（SweepCategories 结构性排除）。");
    }

    [STATestMethod]
    public void CheckAutoCleanup_Disabled_DoesNothing()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore, CreatePreferences(enabled: false));
        var board = GetBoard(window);
        AddNonPinnedItems(board);
        board.AgeAllItems(DateTimeOffset.UtcNow, hoursOld: 25);
        SetField(window, "_boardLoadSucceeded", true);
        var now = DateTimeOffset.UtcNow;

        InvokeCheck(window, now);

        Assert.IsNull(preferencesStore.LastSaved);
        Assert.AreEqual(0, GetStore(window).SaveCount);
        AssertNonPinnedItemsIntact(board);
    }

    [STATestMethod]
    public void CheckAutoCleanup_AfterClosing_DoesNothing()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        var board = GetBoard(window);
        AddNonPinnedItems(board);
        board.AgeAllItems(DateTimeOffset.UtcNow, hoursOld: 25);
        SetField(window, "_boardLoadSucceeded", true);
        SetField(window, "_isClosing", true);
        var now = DateTimeOffset.UtcNow;

        InvokeCheck(window, now);

        Assert.IsNull(preferencesStore.LastSaved);
        Assert.AreEqual(0, GetStore(window).SaveCount);
        AssertNonPinnedItemsIntact(board);
    }

    [STATestMethod]
    public void CheckAutoCleanup_WhileAlreadyInFlight_SecondCallHasNoEffect()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        var board = GetBoard(window);
        AddNonPinnedItems(board);
        board.AgeAllItems(DateTimeOffset.UtcNow, hoursOld: 25);
        SetField(window, "_boardLoadSucceeded", true);
        SetField(window, "_isAutoCleanupCheckInFlight", true);
        var now = DateTimeOffset.UtcNow;

        InvokeCheck(window, now);

        Assert.IsNull(preferencesStore.LastSaved);
        Assert.AreEqual(0, GetStore(window).SaveCount);
        AssertNonPinnedItemsIntact(board);
    }

    [STATestMethod]
    public void CheckAutoCleanup_ExpiredImageCard_KeepsImageFileForUndoLifecycle()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        SetField(window, "_boardLoadSucceeded", true);
        var imagePath = Path.Combine(directory.Root, "sweep.png");
        File.WriteAllBytes(imagePath, [0x89, 0x50, 0x4E, 0x47]);
        var board = GetBoard(window);
        board.AddImage(Guid.NewGuid(), "images/sweep.png", imagePath, BoardCategory.Inbox);
        var now = DateTimeOffset.UtcNow;
        board.AgeAllItems(now, hoursOld: 25);

        InvokeCheck(window, now);

        Assert.AreEqual(1, GetStore(window).SaveCount);
        Assert.IsTrue(
            File.Exists(imagePath),
            "清扫批次进入撤销栈期间图片文件必须保留(既有撤销栈生命周期,不立即删)。");

        GetMutations(window).DiscardUndoableDeletes();

        Assert.IsFalse(File.Exists(imagePath), "丢弃撤销栈后图片文件按既有规则清理。");
    }

    [STATestMethod]
    public void MainWindowClosing_StopsAutoCleanupCheckTimer()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);

        try
        {
            window.Show();
            CompleteLayout(window);
            Assert.IsTrue(
                GetTimer(window).IsEnabled,
                "SourceInitialized 后自动清理巡检计时器应随头部计时器一起启动。");
        }
        finally
        {
            CloseWindow(window);
        }

        Assert.IsFalse(
            GetTimer(window).IsEnabled,
            "Closing 序列必须停掉自动清理巡检计时器。");
    }

    /// <summary>在窗口 Dispatcher 上驱动漏斗到完成:生产入口(tick/装载回调)都在
    /// UI 线程,ShowStatus 触碰依赖属性必须在同一线程续体上执行。PumpUntil 等
    /// 的是漏斗任务本身,不是 InvokeAsync 的同步段。</summary>
    private static void InvokeCheck(MainWindow window, DateTimeOffset now)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = window.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await window.CheckAutoCleanupAsync(now);
                done.TrySetResult();
            }
            catch (Exception exception)
            {
                done.TrySetException(exception);
            }
        });
        PumpUntil(window.Dispatcher, done.Task);
    }

    private static void RunOnDispatcher(MainWindow window, Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = window.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                action();
                done.TrySetResult();
            }
            catch (Exception exception)
            {
                done.TrySetException(exception);
            }
        });
        PumpUntil(window.Dispatcher, done.Task);
    }

    /// <summary>放掉一个空操作让此前排队的 Dispatcher 续体先跑完(用于
    /// fire-and-forget 链路收尾后再断言)。</summary>
    private static void FlushDispatcher(MainWindow window)
    {
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = window.Dispatcher.InvokeAsync(() => flushed.TrySetResult());
        PumpUntil(window.Dispatcher, flushed.Task);
    }

    private static void PumpUntil(Dispatcher dispatcher, Task task)
    {
        if (task.IsCompleted)
        {
            task.GetAwaiter().GetResult();
            return;
        }

        var frame = new DispatcherFrame();
        var timedOut = false;
        var timeout = new DispatcherTimer(DispatcherPriority.Send, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        timeout.Tick += (_, _) =>
        {
            timedOut = true;
            timeout.Stop();
            frame.Continue = false;
        };
        _ = task.ContinueWith(
            _ => dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(() => frame.Continue = false)),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
        timeout.Start();
        Dispatcher.PushFrame(frame);
        timeout.Stop();
        if (timedOut && !task.IsCompleted)
        {
            throw new TimeoutException("The dispatcher operation did not complete within five seconds.");
        }

        task.GetAwaiter().GetResult();
    }

    private static MainWindow CreateWindow(
        TestDirectory directory,
        RecordingPreferencesStore preferencesStore,
        AppPreferences? preferences = null)
    {
        var store = new FakeBoardStore(directory.Root);
        var board = new BoardService();
        var normalizer = new ImageNormalizer(store.ImagesDirectory);
        var operationGate = new BoardOperationGate();
        MainWindow? window = null;
        void ShowStatus(string message) => window?.ShowStatus(message);
        var clipboard = new ClipboardCaptureService(
            new IdleClipboardReader(),
            normalizer,
            board,
            store,
            ShowStatus,
            operationGate: operationGate);
        window = new MainWindow(
            board,
            store,
            WindowSettings.Default,
            clipboard,
            new BoardMutationService(board, store, ShowStatus, operationGate),
            new DragPayloadService(),
            new ExternalDropPayloadReader(new WindowsDataImageReader()),
            new ExternalDropImportService(
                normalizer,
                board,
                store,
                ShowStatus,
                operationGate),
            preferences: preferences ?? AppPreferences.Default,
            preferencesStore: preferencesStore,
            startupManager: new FakeStartupManager(),
            dataDirectory: directory.Root);
        return window;
    }

    private static AppPreferences CreatePreferences(bool enabled) =>
        AppPreferences.Default with { AutoCleanupEnabled = enabled };

    private static BoardItem AddNonPinnedItems(BoardService board)
    {
        board.AddText("图片内容", BoardCategory.CustomerOriginal);
        board.AddText("文本内容", BoardCategory.Prompt);
        var inboxPinned = board.AddText("置顶保留");
        board.SetPinnedMany([inboxPinned.Id], true);
        board.AddText("待分类内容");
        return inboxPinned;
    }

    private static void AssertNonPinnedItemsIntact(BoardService board)
    {
        Assert.AreEqual(1, board.Items(BoardCategory.CustomerOriginal).Count);
        Assert.AreEqual(1, board.Items(BoardCategory.Prompt).Count);
        Assert.AreEqual(2, board.Items(BoardCategory.Inbox).Count);
    }

    private static BoardService GetBoard(MainWindow window) =>
        GetField<BoardService>(window, "_board");

    private static FakeBoardStore GetStore(MainWindow window) =>
        (FakeBoardStore)GetField<IBoardStore>(window, "_store");

    private static DispatcherTimer GetTimer(MainWindow window) =>
        GetField<DispatcherTimer>(window, "_autoCleanupCheckTimer");

    private static BoardMutationService GetMutations(MainWindow window) =>
        GetField<BoardMutationService>(window, "_mutations");

    private static T GetField<T>(MainWindow window, string fieldName)
    {
        var field = typeof(MainWindow).GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        return (T)field.GetValue(window)!;
    }

    private static void SetField(MainWindow window, string fieldName, object value)
    {
        var field = typeof(MainWindow).GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        field.SetValue(window, value);
    }

    private static void CompleteLayout(Window window)
    {
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }

    private static void CloseWindow(Window window)
    {
        PumpUntil(window.Dispatcher, CloseAndGetClosedTask(window));
    }

    private static Task CloseAndGetClosedTask(Window window)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Close();
        return closed.Task;
    }

    private sealed class IdleClipboardReader : IClipboardReader
    {
        public Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClipboardSnapshot(0, null, [], null));
    }

    private sealed class FakeStartupManager : IStartupManager
    {
        public bool IsEnabled() => false;

        public void Enable()
        {
        }

        public void Disable()
        {
        }
    }

    private sealed class RecordingPreferencesStore : IPreferencesStore
    {
        public AppPreferences? LastSaved { get; private set; }

        public Task<AppPreferences> LoadPreferencesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(AppPreferences.Default);

        public Task SavePreferencesAsync(
            AppPreferences preferences,
            CancellationToken cancellationToken = default)
        {
            LastSaved = preferences;
            return Task.CompletedTask;
        }
    }

    /// <summary>板存储假仓:记录保存次数并真正删除图片文件,支撑撤销栈生命周期断言。</summary>
    private sealed class FakeBoardStore(string root) : IBoardStore
    {
        public int SaveCount { get; private set; }

        public string ImagesDirectory { get; } = Path.Combine(root, "images");

        public Task<BoardSnapshot> LoadBoardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BoardSnapshot());

        public Task SaveBoardAsync(BoardSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task<WindowSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(WindowSettings.Default);

        public Task SaveSettingsAsync(WindowSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public bool TryDeleteImage(string? absolutePath)
        {
            if (!string.IsNullOrWhiteSpace(absolutePath) && File.Exists(absolutePath))
            {
                File.Delete(absolutePath);
            }

            return true;
        }
    }
}
