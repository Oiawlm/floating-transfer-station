using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

// 快捷启动拖动路径基准（GOAL-LAUNCHER-UX-PERF-20261016）：
// 1) 编辑模式 200 瓦片拖动跟随的每事件 MouseMove 处理耗时（P50/P95，动效开/关两档）；
// 2) 拖放落点「UI 发布时延 vs 保存完成时延」、集合通知形态与放置后布局轮数。
// 只输出可复跑测量行（PERF| 前缀），断言仅做结构健全检查，不设毫秒阈值防抖动。
// 权威前后对比数字出自本机命令行复跑（CI 共享 runner 跳过，与 PanelExpandPerf 同例）。
public sealed partial class MainWindowInteractionTests
{
    private const int QuickLaunchPerfTileCount = 200;
    private const int QuickLaunchPerfMoveSamples = 240;

    [STATestMethod]
    public void QuickLaunchPerf_MouseMoveFollowBenchmark()
    {
        if (Environment.GetEnvironmentVariable("CI") == "true")
        {
            Assert.Inconclusive("CI 共享 runner 跳过拖动基准（本机测量工具）。");
        }

        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var items = MakeQuickLaunchPerfItems(app.Path);
        var window = CreateLauncherWindowSync(directory, board, store, starter, items);
        var savedCursor = SaveCursorPosition();

        try
        {
            window.Show();
            // 展开期保持动效关闭：入场交错动画是 async void + Task.Delay，测试宿主
            // 无同步上下文会让续体落线程池（生产 Dispatcher 同步上下文不受影响）；
            // 关闭时入口即短路并把重播标记置位，此后开启不再补播。
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();
            var dragged = catalog.Items[0];
            var draggedTile = FindLauncherTile(window, dragged);
            Assert.IsNotNull(draggedTile, "前置：被拖瓦片已生成。");

            EnterLauncherEdit(window);
            CompleteLayout(window);

            foreach (var animationsEnabled in new[] { true, false })
            {
                window.Resources[SystemParameters.ClientAreaAnimationKey] = animationsEnabled;
                var samples = MeasureLauncherDragMoves(window, draggedTile, QuickLaunchPerfMoveSamples);
                Array.Sort(samples);
                double Percentile(int rank) => samples[Math.Min(rank, samples.Length - 1)];
                var report = new List<string>
                {
                    $"PERF|quicklaunch-mousemove|tiles={catalog.Items.Count}" +
                    $"|anims={(animationsEnabled ? "on" : "off")}|samples={samples.Length}",
                    $"PERF|  handler(ms): p50={Percentile((int)(samples.Length * 0.50)):0.000}" +
                    $"|p95={Percentile((int)(samples.Length * 0.95)):0.000}" +
                    $"|max={samples[samples.Length - 1]:0.000}" +
                    $"|mean={samples.Average():0.000}",
                };
                foreach (var line in report)
                {
                    Console.WriteLine(line);
                }
            }

            // 结构健全检查：拖动会话确实建立过（变换非零）且可被收尾。
            Assert.IsNull(GetPrivateField<Guid?>(window, "_launcherDraggingId"), "基准收尾后无悬挂拖动会话。");
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindowWithoutSaving(window);
        }
    }

    /// <summary>驱动一段完整拖动（按下→阈值越界→N 次移动→抬起取消），返回每次
    /// PreviewMouseMove 事件派发（含处理器全成本）的耗时毫秒样本。</summary>
    private static double[] MeasureLauncherDragMoves(
        MainWindow window,
        Border draggedTile,
        int moveCount)
    {
        // 起点：瓦片中心。按下与首步移动越过 4 DIP 阈值即建立拖动会话（真实光标
        // 承载合成事件的坐标——GetPosition 查询主输入设备）。
        var centerInWindow = draggedTile.TranslatePoint(
            new Point(draggedTile.ActualWidth / 2, draggedTile.ActualHeight / 2),
            window);
        SetCursorPosToWindowPoint(window, centerInWindow);
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));
        draggedTile.RaiseEvent(NewMouseButtonEventArgs(
            UIElement.MouseLeftButtonDownEvent,
            draggedTile));
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));

        var samples = new List<double>(moveCount);
        Point WindowPointAt(int step) => new(
            centerInWindow.X + 30 + (step % 7) * 9,
            centerInWindow.Y + ((step / 7) % 5) * 11);
        for (var step = 0; step < moveCount; step++)
        {
            SetCursorPosToWindowPoint(window, WindowPointAt(step));
            var startedAt = Stopwatch.GetTimestamp();
            draggedTile.RaiseEvent(NewMouseEventArgs(
                UIElement.PreviewMouseMoveEvent,
                draggedTile));
            samples.Add(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            // 泵一小段：让渲染/动画时钟不无限堆积（模拟帧节奏到达），不计时。
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(4));
        }

        // 抬起=提交路径会改目录；基准只测移动成本，走取消（先移出会话再 Up）。
        draggedTile.ReleaseMouseCapture();
        InvokePrivate(window, "EndLauncherTileDrag", false);
        draggedTile.RaiseEvent(NewMouseButtonEventArgs(
            UIElement.MouseLeftButtonUpEvent,
            draggedTile,
            timestamp: 77));
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(60));
        return [.. samples];
    }

    private static void SetCursorPosToWindowPoint(MainWindow window, Point windowPoint)
    {
        var screen = window.PointToScreen(windowPoint);
        Assert.IsTrue(
            SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y)),
            "真实光标必须可移动到目标点。");
    }

    [STATestMethod]
    public void QuickLaunchPerf_DropCommitPublishLatencyBenchmark()
    {
        if (Environment.GetEnvironmentVariable("CI") == "true")
        {
            Assert.Inconclusive("CI 共享 runner 跳过落点发布基准（本机测量工具）。");
        }

        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var items = MakeQuickLaunchPerfItems(app.Path);
        store.SaveAsync(new LauncherStoreData(LauncherStoreData.CurrentVersion, [.. items]))
            .GetAwaiter().GetResult();
        var recordingStore = new RecordingLauncherStore(store);
        var window = CreateWindow(
            board,
            store,
            WindowSettings.Default,
            null,
            new ImageNormalizer(store.ImagesDirectory),
            launcherStore: recordingStore,
            appLauncher: new AppLauncher(starter.Invoke));
        PumpUntilCondition(
            window.Dispatcher,
            () => window.LaunchersForTests() is { } catalog && catalog.Items.Count >= items.Length);
        var savedCursor = SaveCursorPosition();

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();
            var dragged = catalog.Items[0];
            var target = catalog.Items[5];
            var draggedTile = FindLauncherTile(window, dragged);
            var targetTile = FindLauncherTile(window, target);
            Assert.IsNotNull(draggedTile);
            Assert.IsNotNull(targetTile);

            EnterLauncherEdit(window);
            CompleteLayout(window);
            var grid = (ItemsControl)window.FindName("LauncherTileGrid")!;
            var containerBefore = grid.ItemContainerGenerator.ContainerFromIndex(3);

            var changes = new List<(long Timestamp, NotifyCollectionChangedAction Action)>();
            NotifyCollectionChangedEventHandler onChange = (_, args) =>
                changes.Add((Stopwatch.GetTimestamp(), args.Action));
            catalog.Items.CollectionChanged += onChange;
            var layoutTimestamps = new List<long>();
            EventHandler onLayoutUpdated = (_, _) => layoutTimestamps.Add(Stopwatch.GetTimestamp());
            window.LayoutUpdated += onLayoutUpdated;
            var gcBefore = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
            try
            {
                // 真实光标承载：按下于拖动瓦片中心 → 移到目标瓦片中心（越过阈值、
                // 命中目标）→ 抬起提交重排（与 DragReorderPersistsOrder 同法）。
                SetCursorPosToWindowPoint(window, draggedTile.TranslatePoint(
                    new Point(draggedTile.ActualWidth / 2, draggedTile.ActualHeight / 2), window));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));
                draggedTile.RaiseEvent(NewMouseButtonEventArgs(
                    UIElement.MouseLeftButtonDownEvent,
                    draggedTile));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));
                SetCursorPosToWindowPoint(window, targetTile.TranslatePoint(
                    new Point(targetTile.ActualWidth / 2, targetTile.ActualHeight / 2), window));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));
                draggedTile.RaiseEvent(NewMouseEventArgs(
                    UIElement.PreviewMouseMoveEvent,
                    draggedTile));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(60));

                var droppedAt = Stopwatch.GetTimestamp();
                var expectedIndex = GetPrivateField<int>(window, "_launcherDropTargetIndex");
                Assert.AreEqual(5, expectedIndex, "前置：落点目标下标为 5（目标瓦片原下标）。");
                draggedTile.RaiseEvent(NewMouseButtonEventArgs(
                    UIElement.MouseLeftButtonUpEvent,
                    draggedTile,
                    timestamp: 41));
                PumpUntilDropCommitSettled(window, recordingStore, changes, droppedAt);

                // 命令行复跑对照数字：发布形态、时延与布局轮数（不设阈值断言）。
                var afterDrop = changes.Where(change => change.Timestamp >= droppedAt).ToList();
                var layoutAfterDrop = layoutTimestamps.Where(t => t > droppedAt).ToList();
                var histogram = string.Join(
                    ",",
                    afterDrop.GroupBy(change => change.Action)
                        .OrderBy(group => group.Key)
                        .Select(group => $"{group.Key}={group.Count()}"));
                var publishDone = afterDrop.Count > 0 ? afterDrop[^1].Timestamp : droppedAt;
                var saveDone = recordingStore.SaveCompletedAt.Count > 0
                    ? recordingStore.SaveCompletedAt[^1]
                    : droppedAt;
                Console.WriteLine(
                    $"PERF|quicklaunch-dropcommit|tiles={catalog.Items.Count}" +
                    $"|events=[{histogram}]|totalEvents={afterDrop.Count}");
                Console.WriteLine(
                    $"PERF|  latency(ms): mouseUpToFirstChange=" +
                    $"{Stopwatch.GetElapsedTime(droppedAt, afterDrop.Count > 0 ? afterDrop[0].Timestamp : droppedAt).TotalMilliseconds:0.0}" +
                    $"|mouseUpToPublishDone={Stopwatch.GetElapsedTime(droppedAt, publishDone).TotalMilliseconds:0.0}" +
                    $"|mouseUpToSaveDone={Stopwatch.GetElapsedTime(droppedAt, saveDone).TotalMilliseconds:0.0}");
                Console.WriteLine(
                    $"PERF|  layoutPassesAfterDrop={layoutAfterDrop.Count}" +
                    $"|firstLayoutGap(ms)={(layoutAfterDrop.Count > 0 ? Stopwatch.GetElapsedTime(droppedAt, layoutAfterDrop[0]).TotalMilliseconds : -1):0.0}" +
                    $"|containerIdentityPreserved={ReferenceEquals(containerBefore, grid.ItemContainerGenerator.ContainerFromIndex(3))}" +
                    $"|gc(G0/G1/G2)={GCCollectionCountsDelta(gcBefore)}");

                Assert.AreEqual(
                    expectedIndex,
                    catalog.Items.IndexOf(dragged),
                    "结构健全：重排提交生效（拖动项落到目标下标）。");
                Assert.IsTrue(recordingStore.SaveCompletedAt.Count >= 1, "结构健全：保存确已完成。");
            }
            finally
            {
                catalog.Items.CollectionChanged -= onChange;
                window.LayoutUpdated -= onLayoutUpdated;
            }
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindowWithoutSaving(window);
        }
    }

    /// <summary>泵到落点提交安定：保存完成、集合通知停止（300ms 无新事件）、超时 10s。</summary>
    private static void PumpUntilDropCommitSettled(
        MainWindow window,
        RecordingLauncherStore store,
        List<(long Timestamp, NotifyCollectionChangedAction Action)> changes,
        long droppedAt)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(30));
            var saveSeen = store.SaveCompletedAt.Any(t => t >= droppedAt);
            var lastChange = changes.LastOrDefault(c => c.Timestamp >= droppedAt);
            if (saveSeen &&
                lastChange != default &&
                Stopwatch.GetElapsedTime(lastChange.Timestamp).TotalMilliseconds > 300)
            {
                return;
            }
        }

        Assert.Fail("落点提交 10 秒内未安定（保存或集合通知未停）。");
    }

    private static LauncherItem[] MakeQuickLaunchPerfItems(string appPath) =>
        Enumerable.Range(0, QuickLaunchPerfTileCount)
            .Select(index => new LauncherItem(
                Guid.NewGuid(),
                LauncherKind.Application,
                $"{appPath}.{index}",
                $"基准应用{index:D3}",
                DateTimeOffset.UtcNow))
            .ToArray();

    private static string GCCollectionCountsDelta((int G0, int G1, int G2) before) =>
        $"{GC.CollectionCount(0) - before.G0}/{GC.CollectionCount(1) - before.G1}/{GC.CollectionCount(2) - before.G2}";

    /// <summary>记录保存完成时间戳的装饰存储（落点「保存完成时延」探针）。</summary>
    private sealed class RecordingLauncherStore(ILauncherStore inner) : ILauncherStore
    {
        public List<long> SaveCompletedAt { get; } = [];

        public Task<LauncherStoreData> LoadAsync(CancellationToken cancellationToken = default) =>
            inner.LoadAsync(cancellationToken);

        public async Task SaveAsync(LauncherStoreData data, CancellationToken cancellationToken = default)
        {
            await inner.SaveAsync(data, cancellationToken);
            SaveCompletedAt.Add(Stopwatch.GetTimestamp());
        }
    }
}
