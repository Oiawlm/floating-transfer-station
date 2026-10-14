using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatingTransferStation.Controls;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

// 面板展开显示路径基准（GOAL-PANEL-EXPAND-PERF-20261013）：
// 悬停→展开的「悬停→首帧/可交互」量化。只输出可复跑的测量行（PERF| 前缀），
// 断言仅做结构性健全检查（容器有界等），不设毫秒阈值防抖动。
// 测试宿主存在环境性持续渲染回调（收起态亦有），安静判定只看有意义事件
// （容器生成/滚动/解码回填），帧节奏与环境基线分别报告以自校准。
public sealed partial class MainWindowInteractionTests
{
    private const int PanelExpandPerfImageCount = 400;
    private const int PanelExpandPerfMixedTextCount = 50;
    private const int PanelExpandPerfTextCategoryCount = 450;

    [STATestMethod]
    public void PanelExpandPerf_ExpandFromHoverBenchmark()
    {
        // 基准是本机测量工具：CI 共享 runner 的慢速 I/O 会让九个重窗口生命周期
        // （大数据集生成＋逐窗口优雅关闭的落盘保存）偶发撞共享泵超时，测量数字
        // 在共享 runner 上也没有可比性。CI 环境跳过（与发布证据测试的环境跳过
        // 同例），本地照常全量运行；权威前后对比数字出自本机。
        if (Environment.GetEnvironmentVariable("CI") == "true")
        {
            Assert.Inconclusive("CI 共享 runner 跳过展开性能基准（本机测量工具）。");
        }

        using var directory = new TestDirectory();
        var imagesDirectory = Path.Combine(directory.Root, "images");
        Directory.CreateDirectory(imagesDirectory);
        var imagePaths = Enumerable.Range(0, PanelExpandPerfImageCount)
            .Select(index =>
            {
                // 三类纵横比均匀掺混（T7 评审：真实截图集含竖版手机图，过采样
                // 幅度随纵横比反向变化——竖版 0.67 在 512 宽下限下过采样最重）：
                // 0→1200×800(1.5:1)、1→800×1200(0.67:1 竖版)、2→2560×800(3.2:1 宽版)。
                var (width, height) = (index % 3) switch
                {
                    0 => (1200, 800),
                    1 => (800, 1200),
                    _ => (2560, 800),
                };
                var path = Path.Combine(imagesDirectory, $"perf-{index:D4}.png");
                WritePng(path, width, height);
                return path;
            })
            .ToArray();

        var board = new BoardService();
        // 图片分类（CustomerOriginal，即「图片」标签）：长文字垫底、图片在上——
        // 顶部视口与深偏移恢复区都是图片卡，两轮容器生成都会触发解码风暴。
        for (var index = 0; index < PanelExpandPerfMixedTextCount; index++)
        {
            board.AddText(
                $"图片分类混合长文 {index}：{new string('混', 1200)}",
                BoardCategory.CustomerOriginal);
        }

        foreach (var (path, index) in imagePaths.Select((path, index) => (path, index)))
        {
            board.AddImage(
                Guid.NewGuid(),
                $"images/perf-{index:D4}.png",
                path,
                BoardCategory.CustomerOriginal);
        }

        // 文本对照分类（Reference）：同条目总量、长文字卡。
        for (var index = 0; index < PanelExpandPerfTextCategoryCount; index++)
        {
            board.AddText(
                $"文本分类条目 {index}：{new string('文', 1200)}",
                BoardCategory.Reference);
        }

        for (var index = 0; index < 30; index++)
        {
            board.AddText($"切换垫板条目 {index}", BoardCategory.Prompt);
        }

        var report = new List<string>();
        var target = BoardCategory.CustomerOriginal;
        var textTarget = BoardCategory.Reference;

        // 场景 1/2 对照裁决 H1（深偏移恢复的第二轮生成）：同一冷图片分类，
        // 有/无已保存深偏移各测一次。
        RunPanelExpandScenario(directory, board, target, hasSavedOffset: true, cold: true,
            panelWidth: 360, realTick: false, report: report);
        RunPanelExpandScenario(directory, board, target, hasSavedOffset: false, cold: true,
            panelWidth: 360, realTick: false, report: report);
        // 冷/热对照（缓存命中率差异，裁决 H4 缓存容量贡献）。
        RunPanelExpandScenario(directory, board, target, hasSavedOffset: true, cold: false,
            panelWidth: 360, realTick: false, report: report);
        // 宽面板（640）暴露升桶二次解码（H4 子项）。
        RunPanelExpandScenario(directory, board, target, hasSavedOffset: true, cold: true,
            panelWidth: 640, realTick: false, report: report);
        // 文本分类对照（同条目总量、无解码）。
        RunPanelExpandScenario(directory, board, textTarget, hasSavedOffset: true, cold: true,
            panelWidth: 360, realTick: false, report: report);
        // 真实悬停路径（含 60ms 去抖）：图片 vs 文本的「悬停→首帧」端到端。
        RunPanelExpandScenario(directory, board, target, hasSavedOffset: true, cold: true,
            panelWidth: 360, realTick: true, report: report);
        RunPanelExpandScenario(directory, board, textTarget, hasSavedOffset: true, cold: true,
            panelWidth: 360, realTick: true, report: report);
        // T7 评审补测：同分类收起→再展开（用户最常见重复手势，无换绑）。
        RunPanelExpandScenario(directory, board, target, hasSavedOffset: true, cold: false,
            panelWidth: 360, realTick: false, report: report, sameCategory: true);
        // T7 评审补测：关闭界面动效 A/B（裁决淡入动画对含图子树的合成放大）。
        RunPanelExpandScenario(directory, board, target, hasSavedOffset: true, cold: true,
            panelWidth: 360, realTick: false, report: report, animationsOff: true);

        foreach (var line in report)
        {
            Console.WriteLine(line);
        }
    }

    private void RunPanelExpandScenario(
        TestDirectory directory,
        BoardService board,
        BoardCategory target,
        bool hasSavedOffset,
        bool cold,
        double panelWidth,
        bool realTick,
        List<string> report,
        bool sameCategory = false,
        bool animationsOff = false)
    {
        var settings = WindowSettings.Default with { PanelWidth = panelWidth };
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var window = CreateWindow(board, store, settings);
        PanelExpandProbeSet? probes = null;
        try
        {
            window.Show();
            CompleteLayout(window);

            if (hasSavedOffset)
            {
                // 目标分类先展开滚到深偏移再收起：写入保存偏移，并预热/实例化容器。
                ExpandCategory(window, target);
                CompleteLayout(window);
                var viewer = FindDescendant<ScrollViewer>((ListBox)window.FindName("BoardList")!);
                if (viewer is not null && viewer.ScrollableHeight > 0)
                {
                    ScrollTo(window, viewer, viewer.ScrollableHeight * 0.8);
                }

                CollapseForSetup(window);
                if (!cold)
                {
                    // 热档：再走一遍展开（重解码可见区）后收起，缓存保留。
                    ExpandCategory(window, target);
                    CompleteLayout(window);
                    CollapseForSetup(window);
                }
            }

            if (!sameCategory)
            {
                // 切换到垫板分类并收起：测量目标从「非活动分类 + 收起态」悬停展开。
                ExpandCategory(window, BoardCategory.Prompt);
                CompleteLayout(window);
                CollapseForSetup(window);
            }

            if (cold)
            {
                foreach (var path in PanelExpandPerfDatasetImagePaths(board))
                {
                    AsyncThumbnailImage.SharedConverter.Invalidate(path);
                }
            }

            var gcBefore = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
            probes = PanelExpandProbeSet.Attach(window);
            PanelExpandMetrics? measured = null;
            if (animationsOff)
            {
                WithClientAreaAnimationsDisabled(
                    window,
                    () =>
                    {
                        measured = realTick
                            ? MeasureRealTickExpand(window, target, probes)
                            : MeasureDecomposedExpand(window, target, settings, probes);
                    });
            }
            else
            {
                measured = realTick
                    ? MeasureRealTickExpand(window, target, probes)
                    : MeasureDecomposedExpand(window, target, settings, probes);
            }

            var metrics = measured!;
            var gcAfter = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
            metrics.GcGen0 = gcAfter.Item1 - gcBefore.Item1;
            metrics.GcGen1 = gcAfter.Item2 - gcBefore.Item2;
            metrics.GcGen2 = gcAfter.Item3 - gcBefore.Item3;
            metrics.Scenario =
                $"{(target == BoardCategory.CustomerOriginal ? "image" : "text")}|" +
                $"w{panelWidth:0}|{(cold ? "cold" : "hot")}|{(hasSavedOffset ? "deep" : "top")}" +
                $"|{(realTick ? "realtick" : "decomposed")}" +
                $"{(sameCategory ? "|samecat" : "")}{(animationsOff ? "|animsoff" : "")}";
            report.AddRange(metrics.ToReportLines());
        }
        finally
        {
            probes?.Detach();
            CloseWindow(window);
        }
    }

    private static IEnumerable<string> PanelExpandPerfDatasetImagePaths(BoardService board) =>
        board.Items(BoardCategory.CustomerOriginal)
            .Where(item => item.Kind == BoardItemKind.Image && item.ImageAbsolutePath is not null)
            .Select(item => item.ImageAbsolutePath!);

    /// <summary>
    /// 分阶段驱动展开 tick（与 <c>ExpandIntentTimer_Tick</c> 完全同序），逐阶段计时；
    /// 之后泵调度器直到安静，采集首帧/解码回填/帧节奏与结构计数。
    /// </summary>
    private PanelExpandMetrics MeasureDecomposedExpand(
        MainWindow window,
        BoardCategory category,
        WindowSettings settings,
        PanelExpandProbeSet probes)
    {
        var viewModel = (MainWindowViewModel)window.DataContext;
        var rail = (UIElement)window.FindName("CategoryRail")!;
        var workArea = SystemParameters.WorkArea;
        var area = new WorkArea(workArea.Left, workArea.Top, workArea.Width, workArea.Height);
        var metrics = new PanelExpandMetrics();

        probes.Reset();
        probes.SampleAmbientFrames(window);
        long previous = Stopwatch.GetTimestamp();
        double ElapsedSince(ref long from)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(from, now).TotalMilliseconds;
            from = now;
            return elapsed;
        }

        InvokePrivate(window, "ActivatePanel", category);
        metrics.MsActivatePanel = ElapsedSince(ref previous);

        InvokePrivate(window, "ApplyPlacement", WindowController.Expanded(area, settings));
        metrics.MsApplyPlacement = ElapsedSince(ref previous);

        viewModel.SetPanelExpanded(true);
        metrics.MsSetPanelExpanded = ElapsedSince(ref previous);

        InvokePrivate(window, "UpdateStatusPresentation");
        metrics.MsUpdateStatus = ElapsedSince(ref previous);

        probes.NewMeasurePeriod();
        rail.UpdateLayout();
        metrics.MsForceLayout = ElapsedSince(ref previous);
        metrics.RealizedAfterTick = FindDescendants<ListBoxItem>(
            (ListBox)window.FindName("BoardList")!).Count();

        var restoreStartedAt = Stopwatch.GetTimestamp();
        InvokePrivate(window, "RestoreScrollOffset", category);
        metrics.MsRestoreSync = Stopwatch.GetElapsedTime(restoreStartedAt).TotalMilliseconds;

        InvokePrivate(window, "AnimateRevealedCategoryTabs");
        metrics.MsRevealTabs = ElapsedSince(ref previous);

        InvokePrivate(window, "AnimatePanelContent", false);
        metrics.MsTickTotal = metrics.MsActivatePanel + metrics.MsApplyPlacement +
            metrics.MsSetPanelExpanded + metrics.MsUpdateStatus + metrics.MsForceLayout +
            metrics.MsRestoreSync + metrics.MsRevealTabs + ElapsedSince(ref previous);
        PumpUntilQuiet(window, probes);

        metrics.MsRestoreQueuedToSettled = Stopwatch.GetElapsedTime(restoreStartedAt).TotalMilliseconds;
        probes.FillMetrics(metrics);
        return metrics;
    }

    /// <summary>真实悬停路径（60ms 去抖定时器 → tick）：测量「悬停→展开属性翻转→首帧」与整段安定。</summary>
    private PanelExpandMetrics MeasureRealTickExpand(
        MainWindow window,
        BoardCategory category,
        PanelExpandProbeSet probes)
    {
        var viewModel = (MainWindowViewModel)window.DataContext;
        var metrics = new PanelExpandMetrics();
        var hoverStartedAt = Stopwatch.GetTimestamp();
        var expandedFlippedAt = (long?)null;
        PropertyChangedEventHandler onViewModelChanged = (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.IsPanelExpanded) && viewModel.IsPanelExpanded)
            {
                expandedFlippedAt = Stopwatch.GetTimestamp();
            }
        };
        viewModel.PropertyChanged += onViewModelChanged;
        try
        {
            probes.Reset();
            probes.SampleAmbientFrames(window);
            probes.NewMeasurePeriod();
            EnterCategory(window, category);
            PumpUntilQuiet(window, probes);
        }
        finally
        {
            viewModel.PropertyChanged -= onViewModelChanged;
        }

        metrics.MsHoverToExpandedFlip =
            expandedFlippedAt is { } flipped
                ? Stopwatch.GetElapsedTime(hoverStartedAt, flipped).TotalMilliseconds
                : -1;
        metrics.MsHoverToFirstFrame = probes.FirstFrameAfter(expandedFlippedAt ?? hoverStartedAt) is { } frame
            ? Stopwatch.GetElapsedTime(hoverStartedAt, frame).TotalMilliseconds
            : -1;
        metrics.MsSettle = probes.LastMeaningfulEventAt is { } last
            ? Stopwatch.GetElapsedTime(hoverStartedAt, last).TotalMilliseconds
            : -1;
        probes.FillMetrics(metrics);
        return metrics;
    }

    /// <summary>泵调度器直到有意义事件安静（500ms 无容器生成/滚动/解码回填）或超时 10s。</summary>
    private static void PumpUntilQuiet(MainWindow window, PanelExpandProbeSet probes)
    {
        var startedAt = Stopwatch.GetTimestamp();
        while (true)
        {
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            if (Stopwatch.GetElapsedTime(probes.LastMeaningfulEventAt ?? startedAt).TotalMilliseconds > 500 ||
                Stopwatch.GetElapsedTime(startedAt) > TimeSpan.FromSeconds(10))
            {
                return;
            }

            Thread.Sleep(15);
        }
    }

    private sealed class PanelExpandMetrics
    {
        public string Scenario { get; set; } = "";
        public double Dpi { get; set; }
        public int ItemsInCategory { get; set; }
        public int GenerationRounds { get; set; }
        public int GenerationTransitionsAfterTick { get; set; }
        public int ScrollPositionChanges { get; set; }
        public int RealizedContainers { get; set; }
        public int RealizedAfterTick { get; set; }
        public int VisibleContainers { get; set; }
        public int RealizedImageCards { get; set; }
        public int SyncCacheHits { get; set; }
        public int AsyncSourceSets { get; set; }
        public int SourceClears { get; set; }
        public int DoubleDecodeImages { get; set; }
        public long DecodedPixelBytes { get; set; }
        public double MaxImageElementWidth { get; set; }
        public double MinImageElementWidth { get; set; }
        public double ViewportWidth { get; set; }
        public string DecodeWidthHistogram { get; set; } = "";
        public double MsActivatePanel { get; set; }
        public double MsApplyPlacement { get; set; }
        public double MsSetPanelExpanded { get; set; }
        public double MsUpdateStatus { get; set; }
        public double MsForceLayout { get; set; }
        public double MsRevealTabs { get; set; }
        public double MsTickTotal { get; set; }
        public double MsRestoreQueuedToSettled { get; set; }
        public double MsRestoreExecLatency { get; set; } = -1;
        public double MsRestoreSync { get; set; } = -1;
        public double MsHoverToExpandedFlip { get; set; } = -1;
        public double MsHoverToFirstFrame { get; set; } = -1;
        public double MsSettle { get; set; } = -1;
        public double AmbientMeanFrameGapMs { get; set; } = -1;
        public int WorkWindowFrames { get; set; }
        public double MsMaxWorkFrameGap { get; set; }
        public int WorkWindowLongGaps { get; set; }
        public int GcGen0 { get; set; }
        public int GcGen1 { get; set; }
        public int GcGen2 { get; set; }

        public List<string> ToReportLines()
        {
            List<string> lines =
            [
                $"PERF|scenario={Scenario}|dpi={Dpi:0.##}|items={ItemsInCategory}",
                $"PERF|  generationRounds={GenerationRounds}(afterTick={GenerationTransitionsAfterTick})" +
                $"|scrollChanges={ScrollPositionChanges}|realized={RealizedContainers}" +
                $"(afterTick={RealizedAfterTick})|visible={VisibleContainers}|imageCards={RealizedImageCards}",
                $"PERF|  decodes: syncHits={SyncCacheHits}|asyncSets={AsyncSourceSets}|clears={SourceClears}" +
                $"|doubleDecodeImages={DoubleDecodeImages}|pixelBytes={DecodedPixelBytes}" +
                $"|widths[{DecodeWidthHistogram}]|imageElementW={MinImageElementWidth:0.#}..{MaxImageElementWidth:0.#}|viewportW={ViewportWidth:0.#}",
                $"PERF|  stages(ms): activate={MsActivatePanel:0.0}|placement={MsApplyPlacement:0.0}" +
                $"|setExpanded={MsSetPanelExpanded:0.0}|status={MsUpdateStatus:0.0}" +
                $"|forceLayout={MsForceLayout:0.0}|revealTabs={MsRevealTabs:0.0}|tickTotal={MsTickTotal:0.0}",
                $"PERF|  restoreSync(ms)={MsRestoreSync:0.0}|restoreStartToSettled(ms)={MsRestoreQueuedToSettled:0.0}" +
                $"|hoverToFlip(ms)={MsHoverToExpandedFlip:0.0}|hoverToFirstFrame(ms)={MsHoverToFirstFrame:0.0}" +
                $"|settle(ms)={MsSettle:0.0}",
                $"PERF|  frames: ambientMeanGap(ms)={AmbientMeanFrameGapMs:0.0}" +
                $"|workWindow: frames={WorkWindowFrames}|maxGap(ms)={MsMaxWorkFrameGap:0.0}" +
                $"|gaps>32ms={WorkWindowLongGaps}|gc(G0/G1/G2)={GcGen0}/{GcGen1}/{GcGen2}",
            ];
            return lines;
        }
    }

    /// <summary>展开路径事件探针：容器生成轮数、滚动变化、解码 Source 翻转、渲染帧节奏。</summary>
    private sealed class PanelExpandProbeSet
    {
        private readonly MainWindow _window;
        private readonly ListBox _list;
        private readonly ItemContainerGenerator _generator;
        private readonly ScrollViewer? _viewer;
        private readonly List<long> _frameTimestamps = [];
        private readonly Dictionary<AsyncThumbnailImage, ImageSourceProbe> _imageProbes = [];
        private long? _measurePeriodStartedAt;
        private long? _ambientWindowStartedAt;
        private double _ambientMeanGap = -1;
        private long? _lastMeaningfulEventAt;
        private int _generationRounds;
        private int _generationTransitionsAfterMeasurePeriod;
        private int _scrollChanges;
        private double _lastOffset;

        private PanelExpandProbeSet(MainWindow window, ListBox list, ScrollViewer? viewer)
        {
            _window = window;
            _list = list;
            _generator = list.ItemContainerGenerator;
            _viewer = viewer;
            _lastOffset = viewer?.VerticalOffset ?? 0;
        }

        public long? LastMeaningfulEventAt => _lastMeaningfulEventAt;

        public int AttachedProbeCount => _imageProbes.Count;

        public static PanelExpandProbeSet Attach(MainWindow window)
        {
            var list = (ListBox)window.FindName("BoardList")!;
            var viewer = FindDescendant<ScrollViewer>(list);
            var probes = new PanelExpandProbeSet(window, list, viewer);
            probes.AttachCore();
            return probes;
        }

        private void AttachCore()
        {
            _generator.StatusChanged += OnGeneratorStatusChanged;
            if (_viewer is not null)
            {
                _viewer.ScrollChanged += OnScrollChanged;
            }

            CompositionTarget.Rendering += OnRendering;
            AttachMissingImageProbes();
        }

        public void Detach()
        {
            _generator.StatusChanged -= OnGeneratorStatusChanged;
            if (_viewer is not null)
            {
                _viewer.ScrollChanged -= OnScrollChanged;
            }

            CompositionTarget.Rendering -= OnRendering;
            foreach (var probe in _imageProbes.Values)
            {
                probe.Detach();
            }
        }

        public void Reset()
        {
            _generationRounds = 0;
            _generationTransitionsAfterMeasurePeriod = 0;
            _scrollChanges = 0;
            _lastOffset = _viewer?.VerticalOffset ?? 0;
            _frameTimestamps.Clear();
            _measurePeriodStartedAt = null;
            _lastMeaningfulEventAt = null;
            foreach (var probe in _imageProbes.Values)
            {
                probe.Reset();
            }
        }

        /// <summary>测量前采样环境帧节奏基线（校准宿主噪声用）。</summary>
        public void SampleAmbientFrames(MainWindow window)
        {
            _ambientWindowStartedAt = Stopwatch.GetTimestamp();
            var framesAtStart = _frameTimestamps.Count;
            var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(400);
            while (DateTime.UtcNow < deadline)
            {
                var frame = new DispatcherFrame();
                window.Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Thread.Sleep(15);
            }

            _ambientWindowStartedAt = null;
            var ambientFrames = _frameTimestamps.Skip(framesAtStart).ToList();
            if (ambientFrames.Count > 1)
            {
                var gaps = new List<double>();
                for (var index = 1; index < ambientFrames.Count; index++)
                {
                    gaps.Add(Stopwatch.GetElapsedTime(
                        ambientFrames[index - 1],
                        ambientFrames[index]).TotalMilliseconds);
                }

                _ambientMeanGap = gaps.Average();
            }
        }

        /// <summary>同步重活（强制布局/真实 tick）开始前调用：划定首帧与帧节奏的工作窗。</summary>
        public void NewMeasurePeriod()
        {
            _measurePeriodStartedAt = Stopwatch.GetTimestamp();
            _generationTransitionsAfterMeasurePeriod = 0;
            AttachMissingImageProbes();
        }

        public long? FirstFrameAfter(long startedAt)
        {
            var candidates = _frameTimestamps.Where(timestamp => timestamp >= startedAt).ToList();
            return candidates.Count > 0 ? candidates[0] : null;
        }

        public void FillMetrics(PanelExpandMetrics metrics)
        {
            var viewModel = (MainWindowViewModel)_window.DataContext;
            metrics.Dpi = VisualTreeHelper.GetDpi(_list).PixelsPerDip;
            metrics.ItemsInCategory = viewModel.ActivePanel?.Items.Count ?? 0;
            metrics.GenerationRounds = _generationRounds;
            metrics.GenerationTransitionsAfterTick = _generationTransitionsAfterMeasurePeriod;
            metrics.ScrollPositionChanges = _scrollChanges;

            var containers = FindDescendants<ListBoxItem>(_list).ToList();
            metrics.RealizedContainers = containers.Count;
            if (_viewer is not null && containers.Count > 0)
            {
                // TransformToAncestor 的结果已含滚动偏移：视口即 [0, ViewportHeight]。
                metrics.VisibleContainers = containers.Count(container =>
                {
                    var position = container.TransformToAncestor(_viewer).Transform(new Point());
                    return position.Y < _viewer.ViewportHeight &&
                        position.Y + container.ActualHeight > 0;
                });
            }

            foreach (var probe in _imageProbes.Values)
            {
                if (!probe.IsImageCard)
                {
                    continue;
                }

                metrics.RealizedImageCards++;
                metrics.SyncCacheHits += probe.SyncHits;
                metrics.AsyncSourceSets += probe.SourceSets - probe.SyncHits;
                metrics.SourceClears += probe.SourceClears;
                metrics.DecodedPixelBytes += probe.PixelBytes;
                metrics.MaxImageElementWidth = Math.Max(metrics.MaxImageElementWidth, probe.ElementWidth);
                metrics.MinImageElementWidth = metrics.MinImageElementWidth == 0
                    ? probe.ElementWidth
                    : Math.Min(metrics.MinImageElementWidth, probe.ElementWidth);
                if (probe.HasDecodedSamePathTwice)
                {
                    metrics.DoubleDecodeImages++;
                }
            }

            metrics.DecodeWidthHistogram = string.Join(
                ",",
                _imageProbes.Values
                    .Where(probe => probe.IsImageCard)
                    .SelectMany(probe => probe.AppliedPixelWidths)
                    .GroupBy(width => width)
                    .OrderBy(group => group.Key)
                    .Select(group => $"{group.Key}x{group.Count()}"));
            if (_viewer is not null)
            {
                metrics.ViewportWidth = _viewer.ViewportWidth;
            }

            metrics.AmbientMeanFrameGapMs = _ambientMeanGap;
            if (_measurePeriodStartedAt is { } started)
            {
                var workWindowEnd = started + (long)(TimeSpan.TicksPerMillisecond * 900);
                var workFrames = _frameTimestamps
                    .Where(timestamp => timestamp >= started && timestamp <= workWindowEnd)
                    .ToList();
                metrics.WorkWindowFrames = workFrames.Count;
                if (workFrames.Count > 1)
                {
                    var maxGap = 0d;
                    for (var index = 1; index < workFrames.Count; index++)
                    {
                        var gap = Stopwatch.GetElapsedTime(
                            workFrames[index - 1],
                            workFrames[index]).TotalMilliseconds;
                        maxGap = Math.Max(maxGap, gap);
                        if (gap > 32d)
                        {
                            metrics.WorkWindowLongGaps++;
                        }
                    }

                    metrics.MsMaxWorkFrameGap = maxGap;
                }
            }
        }

        /// <summary>把当前树上尚未挂探针的图片控件补挂（容器生成事件与测量期都会调用）。</summary>
        private void AttachMissingImageProbes()
        {
            foreach (var image in FindDescendants<AsyncThumbnailImage>(_list).ToList())
            {
                if (_imageProbes.ContainsKey(image))
                {
                    continue;
                }

                var probe = new ImageSourceProbe(this, image);
                probe.Attach();
                _imageProbes[image] = probe;
                // 挂载时 Source 已非空且从未见过翻转 = 绑定当刻的缓存命中同步设置。
                if (image.Source is not null)
                {
                    probe.RecordSyncHit(image.Source);
                }
            }
        }

        private void MarkMeaningfulEvent(long timestamp) => _lastMeaningfulEventAt = timestamp;

        private void OnGeneratorStatusChanged(object? sender, EventArgs args)
        {
            if (_generator.Status == GeneratorStatus.ContainersGenerated)
            {
                _generationRounds++;
                if (_measurePeriodStartedAt is not null)
                {
                    _generationTransitionsAfterMeasurePeriod++;
                }

                MarkMeaningfulEvent(Stopwatch.GetTimestamp());
                // 新容器可能在本轮生成：立刻补挂探针，别错过其解码回填。
                AttachMissingImageProbes();
            }
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs args)
        {
            if (Math.Abs(args.VerticalOffset - _lastOffset) > 0.5)
            {
                _scrollChanges++;
                _lastOffset = args.VerticalOffset;
                MarkMeaningfulEvent(Stopwatch.GetTimestamp());
            }
        }

        private void OnRendering(object? sender, EventArgs args)
        {
            _frameTimestamps.Add(Stopwatch.GetTimestamp());
        }

        private sealed class ImageSourceProbe(PanelExpandProbeSet parent, AsyncThumbnailImage image)
        {
            private readonly DependencyPropertyDescriptor _descriptor =
                DependencyPropertyDescriptor.FromProperty(
                    System.Windows.Controls.Image.SourceProperty,
                    typeof(AsyncThumbnailImage));

            private readonly AsyncThumbnailImage _image = image;
            private readonly List<string> _appliedPaths = [];
            private readonly List<int> _appliedWidths = [];
            private EventHandler? _handler;

            public bool IsImageCard => _image.SourcePath is not null &&
                Path.IsPathFullyQualified(_image.SourcePath);

            public double ElementWidth => _image.ActualWidth;

            public IEnumerable<int> AppliedPixelWidths => _appliedWidths;

            public bool HasDecodedSamePathTwice =>
                _appliedPaths.GroupBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Any(group => group.Count() >= 2);

            public int SourceSets { get; private set; }

            public int SourceClears { get; private set; }

            public int SyncHits { get; private set; }

            public long PixelBytes { get; private set; }

            public void Attach()
            {
                _handler = OnSourceChanged;
                _descriptor.AddValueChanged(_image, _handler);
            }

            public void Detach()
            {
                if (_handler is not null)
                {
                    _descriptor.RemoveValueChanged(_image, _handler);
                }
            }

            public void Reset()
            {
                SourceSets = 0;
                SourceClears = 0;
                SyncHits = 0;
                PixelBytes = 0;
                _appliedPaths.Clear();
                _appliedWidths.Clear();
            }

            public void RecordSyncHit(ImageSource source)
            {
                SyncHits++;
                SourceSets++;
                PixelBytes += PixelBytesOf(source);
                if (source is BitmapSource bitmap)
                {
                    _appliedWidths.Add(bitmap.PixelWidth);
                }

                RecordAppliedPath();
            }

            private void OnSourceChanged(object? sender, EventArgs args)
            {
                parent.MarkMeaningfulEvent(Stopwatch.GetTimestamp());
                if (_image.Source is null)
                {
                    SourceClears++;
                    return;
                }

                SourceSets++;
                PixelBytes += PixelBytesOf(_image.Source);
                if (_image.Source is BitmapSource applied)
                {
                    _appliedWidths.Add(applied.PixelWidth);
                }

                RecordAppliedPath();
            }

            private void RecordAppliedPath()
            {
                if (_image.SourcePath is { } path && Path.IsPathFullyQualified(path))
                {
                    _appliedPaths.Add(path);
                }
            }

            private static long PixelBytesOf(ImageSource source) => source is BitmapSource bitmap
                ? (long)bitmap.PixelWidth * bitmap.PixelHeight * Math.Max(32, bitmap.Format.BitsPerPixel) / 8
                : 0;
        }
    }
}
