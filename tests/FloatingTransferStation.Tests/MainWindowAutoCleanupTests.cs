using System.Windows;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 自动清理调度漏斗（1.23.0）：装载闸门、启动补跑建基线、到期清扫+状态条+记账、
/// 未到期/关闭/在途无动作，以及关闭序列停表。时间一律显式传参,不依赖真实时钟。
/// 漏斗与生产一致地驱动在窗口 Dispatcher 上（ShowStatus 触碰依赖属性）。
/// </summary>
[TestClass]
public sealed class MainWindowAutoCleanupTests
{
    [STATestMethod]
    public void CheckAutoCleanup_BeforeBoardLoadCompletes_DoesNotSweepAndSkipsBaseline()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        var now = DateTimeOffset.UtcNow;
        AddNonPinnedItems(window);

        InvokeCheck(window, now);

        Assert.IsNull(
            GetPreference(window).AutoCleanupLastRunAtUtc,
            "板未装载成功时连基线都不得建立,否则一次真实到期会被空板记账推迟 24 小时。");
        Assert.IsNull(preferencesStore.LastSaved);
        AssertNonPinnedItemsIntact(window);
    }

    [STATestMethod]
    public void CheckAutoCleanup_FirstRunAfterLoad_EstablishesBaselineWithoutSweep()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        AddNonPinnedItems(window);
        SetField(window, "_boardLoadSucceeded", true);
        var now = DateTimeOffset.UtcNow;

        InvokeCheck(window, now);

        Assert.AreEqual(
            now,
            GetPreference(window).AutoCleanupLastRunAtUtc,
            "null 基线只记账不清扫,首次清扫在开启 24 小时后。");
        Assert.AreEqual(now, preferencesStore.LastSaved!.AutoCleanupLastRunAtUtc);
        AssertNonPinnedItemsIntact(window);
        Assert.IsEmpty(((MainWindowViewModel)window.DataContext).StatusText);
    }

    [STATestMethod]
    public void OnBoardLoadCompleted_RunsBaselineCheckImmediately()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        AddNonPinnedItems(window);
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        RunOnDispatcher(window, window.OnBoardLoadCompleted);
        FlushDispatcher(window);
        FlushDispatcher(window);

        Assert.IsTrue(GetField<bool>(window, "_boardLoadSucceeded"));
        var baseline = GetPreference(window).AutoCleanupLastRunAtUtc;
        Assert.IsNotNull(baseline, "装载成功回调必须立即补跑一次漏斗并建立基线。");
        Assert.IsTrue(baseline >= before, "基线应为补跑时刻,不得早于回调前。");
        AssertNonPinnedItemsIntact(window);
    }

    [STATestMethod]
    public void CheckAutoCleanup_Due_SweepsShowsStatusAndRecordsOnce()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore, CreatePreferences(true, hoursAgo: 25));
        var now = DateTimeOffset.UtcNow;
        SetField(window, "_boardLoadSucceeded", true);
        var inboxPinned = AddNonPinnedItems(window);

        InvokeCheck(window, now);

        Assert.AreEqual(
            "已自动清理非置顶内容 3 项（可 Ctrl+Z 撤销）",
            ((MainWindowViewModel)window.DataContext).StatusText);
        Assert.AreEqual(now, GetPreference(window).AutoCleanupLastRunAtUtc);
        Assert.AreEqual(now, preferencesStore.LastSaved!.AutoCleanupLastRunAtUtc, "记账必须落盘。");
        Assert.AreEqual(1, GetStore(window).SaveCount, "清扫必须单次原子保存。");
        Assert.AreEqual(1, GetMutations(window).PendingUndoDeleteCount, "清扫必须单批可撤销。");
        Assert.AreSame(inboxPinned, GetBoard(window).Items(BoardCategory.Inbox).Single());
    }

    [STATestMethod]
    public void CheckAutoCleanup_NotDue_DoesNothing()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore, CreatePreferences(true, hoursAgo: 23));
        SetField(window, "_boardLoadSucceeded", true);
        AddNonPinnedItems(window);
        var now = DateTimeOffset.UtcNow;

        InvokeCheck(window, now);

        Assert.IsNull(preferencesStore.LastSaved);
        Assert.AreEqual(0, GetStore(window).SaveCount);
        AssertNonPinnedItemsIntact(window);
        Assert.IsEmpty(((MainWindowViewModel)window.DataContext).StatusText);
    }

    [STATestMethod]
    public void CheckAutoCleanup_Disabled_DoesNothingEvenWhenDue()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore, CreatePreferences(false, hoursAgo: 25));
        SetField(window, "_boardLoadSucceeded", true);
        AddNonPinnedItems(window);
        var now = DateTimeOffset.UtcNow;

        InvokeCheck(window, now);

        Assert.IsNull(preferencesStore.LastSaved);
        Assert.AreEqual(0, GetStore(window).SaveCount);
        AssertNonPinnedItemsIntact(window);
    }

    [STATestMethod]
    public void CheckAutoCleanup_AfterClosing_DoesNothing()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore, CreatePreferences(true, hoursAgo: 25));
        SetField(window, "_boardLoadSucceeded", true);
        SetField(window, "_isClosing", true);
        AddNonPinnedItems(window);
        var now = DateTimeOffset.UtcNow;

        InvokeCheck(window, now);

        Assert.IsNull(preferencesStore.LastSaved);
        Assert.AreEqual(0, GetStore(window).SaveCount);
        AssertNonPinnedItemsIntact(window);
    }

    [STATestMethod]
    public void CheckAutoCleanup_WhileAlreadyInFlight_SecondCallHasNoEffect()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore, CreatePreferences(true, hoursAgo: 25));
        SetField(window, "_boardLoadSucceeded", true);
        SetField(window, "_isAutoCleanupCheckInFlight", true);
        AddNonPinnedItems(window);
        var now = DateTimeOffset.UtcNow;

        InvokeCheck(window, now);

        Assert.IsNull(preferencesStore.LastSaved);
        Assert.AreEqual(0, GetStore(window).SaveCount);
        AssertNonPinnedItemsIntact(window);
    }

    [STATestMethod]
    public void CheckAutoCleanup_DueWithImageCard_KeepsImageFileForUndoLifecycle()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore, CreatePreferences(true, hoursAgo: 25));
        SetField(window, "_boardLoadSucceeded", true);
        var imagePath = Path.Combine(directory.Root, "sweep.png");
        File.WriteAllBytes(imagePath, [0x89, 0x50, 0x4E, 0x47]);
        var board = GetBoard(window);
        board.AddImage(Guid.NewGuid(), "images/sweep.png", imagePath, BoardCategory.Inbox);
        var now = DateTimeOffset.UtcNow;

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

    private static AppPreferences CreatePreferences(bool enabled, double hoursAgo) =>
        AppPreferences.Default with
        {
            AutoCleanupEnabled = enabled,
            AutoCleanupLastRunAtUtc = DateTimeOffset.UtcNow - TimeSpan.FromHours(hoursAgo)
        };

    private static BoardItem AddNonPinnedItems(MainWindow window)
    {
        var board = GetBoard(window);
        board.AddText("图片内容", BoardCategory.CustomerOriginal);
        board.AddText("文本2内容", BoardCategory.Prompt);
        var inboxPinned = board.AddText("置顶保留");
        board.SetPinnedMany([inboxPinned.Id], true);
        board.AddText("待分类内容");
        return inboxPinned;
    }

    private static void AssertNonPinnedItemsIntact(MainWindow window)
    {
        var board = GetBoard(window);
        Assert.AreEqual(1, board.Items(BoardCategory.CustomerOriginal).Count);
        Assert.AreEqual(1, board.Items(BoardCategory.Prompt).Count);
        Assert.AreEqual(2, board.Items(BoardCategory.Inbox).Count);
    }

    private static BoardService GetBoard(MainWindow window) =>
        GetField<BoardService>(window, "_board");

    private static FakeBoardStore GetStore(MainWindow window) =>
        (FakeBoardStore)GetField<IBoardStore>(window, "_store");

    private static AppPreferences GetPreference(MainWindow window) =>
        GetField<AppPreferences>(window, "_preferences");

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
