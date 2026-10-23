using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FloatingTransferStation.Design;
using FloatingTransferStation.Launcher;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;

namespace FloatingTransferStation.Views;

public partial class MainWindow
{
    private LauncherCatalog _launchers = null!;
    private AppLauncher _appLauncher = null!;
    private LauncherIconService? _launcherIcons;
    private DispatcherTimer _launchCollapseTimer = null!;
    private readonly Dictionary<Guid, DateTime> _lastLaunchAttemptPerItem = [];
    private readonly Dictionary<Guid, Border> _launcherTileContainers = [];
    private Guid? _launcherPressItemId;
    private System.Windows.Point _launcherPressStartPosition;
    private string? _launcherPressZone;
    private int _launcherFocusedIndex = -1;
    private int _expandSessionId;
    private int _launchCollapseSession;
    private bool _launcherEntrancePlayed;

    // 多选会话（#30 修订 2026-10-18，Issue #186）：权威状态=条目 IsSelected（绑定源），
    // 锚点=最近一次单选/Ctrl 选中的下标（Shift 范围选基准，语义对齐卡片 RangeSelection）。
    private Guid? _launcherSelectionAnchorId;

    // 名称带双击窗痕迹（DoubleClickWindow 共享判定）：同瓦片、时限内、容差内两击=改名。
    private (Guid ItemId, int Timestamp, System.Windows.Point Position)? _launcherLastClick;

    // 再展开抑制解除观测轮询（#30，Issue #173）：收起后仅把手接收鼠标消息，指针
    // 离开收起把手矩形只能靠全局轮询观测；节奏与贴边隐藏回位轮询一致（业界主流
    // ~100ms，TrafficMonitor、winautohidev2 等），仅抑制布防期间运行。
    private static readonly TimeSpan ReexpandSuppressionPollInterval = TimeSpan.FromMilliseconds(100);
    private DispatcherTimer _reexpandSuppressionPollTimer = null!;

    private void InitializeQuickLaunch(
        ILauncherStore? launcherStore,
        AppLauncher? appLauncher,
        ILauncherDiscoveryStore? launcherDiscoveryStore)
    {
        _appLauncher = appLauncher ?? new AppLauncher(packagedActivator: new ShellPackagedAppActivator().Activate);
        _launchCollapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DesignTokens.LaunchCollapseDelayMs) };
        _launchCollapseTimer.Tick += LaunchCollapseTimer_Tick;
        _reexpandSuppressionPollTimer = new DispatcherTimer { Interval = ReexpandSuppressionPollInterval };
        _reexpandSuppressionPollTimer.Tick += ReexpandSuppressionPollTimer_Tick;
        _launcherUndoTimer.Tick += LauncherUndoTimer_Tick;
        // 瓦片改名编辑器的 IME 组合标记（组合期的 Esc 属输入法操作，不触发取消；与
        // 分类改名框同族守卫，#7/#22）。
        AddHandler(
            System.Windows.Input.TextCompositionManager.PreviewTextInputStartEvent,
            new TextCompositionEventHandler((_, args) => _launcherRenameComposing = true));
        AddHandler(
            System.Windows.Input.TextCompositionManager.PreviewTextInputUpdateEvent,
            new TextCompositionEventHandler((_, args) => _launcherRenameComposing = true));
        AddHandler(
            System.Windows.Input.TextCompositionManager.PreviewTextInputEvent,
            new TextCompositionEventHandler((_, args) => _launcherRenameComposing = false));
        TrackPendingOperation(LoadLaunchersAsync(launcherStore, launcherDiscoveryStore));
    }

    private async Task LoadLaunchersAsync(
        ILauncherStore? launcherStore,
        ILauncherDiscoveryStore? launcherDiscoveryStore)
    {
        var store = launcherStore ?? new VolatileLauncherStore();
        var catalog = await LauncherCatalog.LoadAsync(
            store,
            ShowStatus,
            action => Dispatcher.Invoke(action));
        await Dispatcher.InvokeAsync(() =>
        {
            _launchers = catalog;
            LauncherTileGrid.ItemsSource = _launchers.Items;
            _launchers.Items.CollectionChanged += (_, _) => OnLauncherItemsChanged();
            OnLauncherItemsChanged();
            if (_launchers.IsReadOnlyFromNewerVersion)
            {
                ShowStatus("启动项数据来自更新的版本，已暂停修改。");
            }

            // 孤儿清扫仅在装载成功后运行一次（此后新增 PNG 到下次启动才可能被清）。
            var faviconClient = FaviconClientFactoryForTests?.Invoke() ?? new FaviconClient();
            var faviconScheduler = new FaviconFetchScheduler();
            faviconClient.OriginPermitFactory = faviconScheduler.AcquireAsync;
            _launcherIcons = LauncherIconService.Start(_launchers, LauncherIconsDirectory(), Dispatcher, faviconClient, faviconScheduler);
            _launcherIcons.SweepOrphansOnce();
            // 启动重试轮：无图标 Url 条目静默重抓一轮（每条目每进程一次，2/s 节流）。
            _launcherIcons.RunStartupRetryRoundOnce();
        });

        // 发现面初始化（#45）：依赖目录与图标 STA 线程就绪；未注入存储时用易失
        // 回退（窗口直连构造/测试），生产行为不变。
        await InitializeLauncherDiscoveryAsync(
            launcherDiscoveryStore ?? new VolatileLauncherDiscoveryStore());
    }

    private string LauncherIconsDirectory() =>
        Path.Combine(
            string.IsNullOrWhiteSpace(_dataDirectory) ? Path.GetTempPath() : _dataDirectory,
            "launcher-icons");

    internal LauncherCatalog LaunchersForTests() => _launchers;

    /// <summary>仅供测试注入：favicon 客户端工厂（fake handler 离线验证；null=真实客户端）。</summary>
    internal static Func<FaviconClient>? FaviconClientFactoryForTests { get; set; }

    private bool IsLauncherActive() =>
        _viewModel.ActivePanel?.Category == QuickLaunchMigration.LauncherCategory;

    private bool IsLauncherSurfaceReady() => _launchers is not null;

    private void OnLauncherItemsChanged()
    {
        UpdateLauncherSurfaceState();
        // 目录权威计数（_items 在发布前置位）：装载的整表发布与失败回滚的逆操作
        // 都会改变集合——不得据此误判「删空」（PublishInline 的 Clear→Add 瞬态）。
        // 拖动在途时的集合变更（保存失败回滚的逆 Move、旁路突变）会改变下标与
        // 布局：落点命中槽在**布局落定后**重建（GetLayoutSlot 反映重排后的槽位
        // ——同步重建会拍到旧布局；Loaded 优先级恰在布局之后、下一次输入之前）。
        if (_launcherDraggingId is { } draggingId && _launcherDragHitSlots is not null)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (_launcherDraggingId == draggingId)
                {
                    RefreshLauncherDragHitSlots(draggingId);
                }
            });
        }

        // 选择集随集合变更收敛：被删条目移出选择（批量删除路径会先清空，此处
        // 兜底覆盖旁路突变），焦点索引钳制到当前计数域。
        if (_launchers.Count == 0)
        {
            ClearLauncherSelection();
        }
        else if (_launchers.Items.Any(item => item.IsSelected))
        {
            UpdateLauncherDeleteButtonForSelection();
        }

        if (_launcherFocusedIndex >= _launchers.Count)
        {
            _launcherFocusedIndex = _launchers.Count - 1;
            ApplyLauncherFocusVisual();
        }
    }

    /// <summary>空态/网格切换与工具行状态。全部读目录权威计数——PublishInline 的
    /// Clear→Add 瞬态会让 Items.Count 闪 0（若据此钳制焦点索引会被毒化为 -1）。</summary>
    private void UpdateLauncherSurfaceState()
    {
        if (!IsLauncherSurfaceReady())
        {
            return;
        }

        var isEmpty = _launchers.Count == 0;
        LauncherEmptyState.SetCurrentValue(VisibilityProperty, isEmpty ? Visibility.Visible : Visibility.Collapsed);
        LauncherScroll.SetCurrentValue(VisibilityProperty, isEmpty ? Visibility.Collapsed : Visibility.Visible);
        if (isEmpty)
        {
            ClearLauncherSelection();
        }

        if (_launcherFocusedIndex >= _launchers.Count)
        {
            _launcherFocusedIndex = _launchers.Count - 1;
            ApplyLauncherFocusVisual();
        }
    }

    // ---- 添加（＋ 两入口菜单，#30 切片 4：应用文件… / 已安装应用…）----

    /// <summary>已安装应用枚举源（默认 AppsFolder COM 枚举；测试可注入假源）。</summary>
    private Func<IReadOnlyList<InstalledApp>> _installedAppsSource = InstalledAppsEnumerator.Enumerate;

    /// <summary>选择器对话框显示（测试缝：可替换为非模态驱动并自动确认）。</summary>
    internal Func<MultiItemPickerWindow, bool?> _showPickerDialog = static picker => picker.ShowDialog();

    /// <summary>网页地址对话框显示（测试缝：同选择器缝）。</summary>
    internal Func<LauncherUrlInputWindow, bool?> _showUrlDialog = static dialog => dialog.ShowDialog();

    private void LauncherAddButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosing || _launchers is null || _launchers.IsReadOnlyFromNewerVersion)
        {
            return;
        }

        if (sender is not Button initiator)
        {
            return;
        }

        // 锚定发起按钮（工具行＋或空态 CTA）；首次打开前 Popup 子元素未进视觉树、
        // ActualWidth=0，先显式 Measure 取期望宽再算右对齐偏移。IsOpen=true 触发
        // Opened→BeginOverlayInteraction(Menu)，收起链随即被覆盖交互 hold 抑制。
        LauncherAddMenu.PlacementTarget = initiator;
        var menuChild = (FrameworkElement)LauncherAddMenu.Child;
        menuChild.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        LauncherAddMenu.HorizontalOffset = initiator.ActualWidth - menuChild.DesiredSize.Width;
        LauncherAddMenu.IsOpen = true;
    }

    private void LauncherAddMenu_Opened(object? sender, EventArgs e) =>
        _panelState.BeginOverlayInteraction(PanelOverlayReason.Menu);

    private void LauncherAddMenu_Closed(object? sender, EventArgs e) =>
        EndOverlayInteractionAndReconcile(PanelOverlayReason.Menu);

    private void LauncherAddFileMenu_Click(object sender, RoutedEventArgs e)
    {
        LauncherAddMenu.IsOpen = false;
        if (_isClosing || _launchers is null || _launchers.IsReadOnlyFromNewerVersion)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "添加应用",
            Filter = "应用程序|*.exe;*.lnk",
            Multiselect = true
        };
        _panelState.BeginOverlayInteraction(PanelOverlayReason.ModalDialog);
        try
        {
            if (dialog.ShowDialog(this) != true || dialog.FileNames is not { Length: > 0 } fileNames)
            {
                return;
            }

            TrackPendingOperation(ImportLauncherFilesAsync(fileNames, skippedUnsupportedCount: 0));
        }
        finally
        {
            EndOverlayInteractionAndReconcile(PanelOverlayReason.ModalDialog);
        }
    }

    /// <summary>已安装应用选择器（#30 切片 4）：打开时枚举 AppsFolder（会话内缓存），
    /// 多选确认后走目录添加协议（去重/上限/落位/状态条与文件导入同一汇报路径）。</summary>
    private void LauncherAddInstalledMenu_Click(object sender, RoutedEventArgs e)
    {
        LauncherAddMenu.IsOpen = false;
        if (_isClosing || _launchers is null || _launchers.IsReadOnlyFromNewerVersion)
        {
            return;
        }

        IReadOnlyList<InstalledApp> installed;
        try
        {
            installed = _installedAppsSource();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or COMException)
        {
            ShowStatus("无法枚举已安装应用。");
            return;
        }

        if (installed.Count == 0)
        {
            ShowStatus("未枚举到已安装应用。");
            return;
        }

        var existingAumids = _launchers.Items
            .Where(item => item.Kind == LauncherKind.PackagedApp)
            .Select(item => item.TargetPath)
            .ToHashSet(StringComparer.Ordinal);
        // 通用选择器（#45 泛化）：已安装应用映射喂入（Payload=InstalledApp）。
        var rows = installed
            .Select(app => new MultiItemPickerWindow.PickerRow(
                app,
                app.DisplayName,
                subtitle: null,
                existingAumids.Contains(app.Aumid)))
            .ToList();
        var picker = new MultiItemPickerWindow(
            CurrentTheme,
            "添加已安装应用",
            "搜索应用",
            "未枚举到已安装应用",
            rows)
        { Owner = this };
        _panelState.BeginOverlayInteraction(PanelOverlayReason.ModalDialog);
        try
        {
            if (_showPickerDialog(picker) != true ||
                picker.Selection.OfType<InstalledApp>().ToList() is not { Count: > 0 } selection)
            {
                return;
            }

            TrackPendingOperation(ImportInstalledAppsAsync(selection));
        }
        finally
        {
            EndOverlayInteractionAndReconcile(PanelOverlayReason.ModalDialog);
        }
    }

    /// <summary>网页地址入口（#30 修订，Issue #179）：输入对话框经 Core 归一化（唯一
    /// ingress，无协议补 https://、仅 http/https、失败就地内联报错），确认后创建 Url
    /// 条目走目录添加协议（去重/上限/落位/状态条与文件导入同一汇报路径）。</summary>
    private void LauncherAddUrlMenu_Click(object sender, RoutedEventArgs e)
    {
        LauncherAddMenu.IsOpen = false;
        if (_isClosing || _launchers is null || _launchers.IsReadOnlyFromNewerVersion)
        {
            return;
        }

        var dialog = new LauncherUrlInputWindow(CurrentTheme) { Owner = this };
        _panelState.BeginOverlayInteraction(PanelOverlayReason.ModalDialog);
        try
        {
            if (_showUrlDialog(dialog) != true || dialog.Result is not { Success: true } normalization)
            {
                return;
            }

            TrackPendingOperation(ImportLauncherUrlsAsync(normalization.Url, normalization.DefaultName));
        }
        finally
        {
            EndOverlayInteractionAndReconcile(PanelOverlayReason.ModalDialog);
        }
    }

    // ---- 覆盖交互收起抑制（#30 回归修复，Issue #162）：＋菜单/模态对话框是独立
    // HWND，指针移入即补发假 MouseLeave；打开期间收起链被状态机 hold 抑制，全部
    // 释放后按指针几何位置重估（与编辑保持的 ReconcileSurfaceAfterEditing 同形，
    // 但判据是几何包含而非 IsMouseOver——外来 HWND 捕获期间 IsMouseOver 不可靠）。----

    private void EndOverlayInteractionAndReconcile(PanelOverlayReason reason)
    {
        _panelState.EndOverlayInteraction(reason);
        ReconcileSurfaceAfterOverlayInteraction();
    }

    /// <summary>覆盖交互全部结束后的表面重估：指针仍在窗口内则维持展开，否则恢复
    /// 既有收起节奏。已收起时不重启计时也不反向展开——收起尾段关闭菜单触发的
    /// Closed 再入路径由此天然幂等。</summary>
    private void ReconcileSurfaceAfterOverlayInteraction()
    {
        if (_isClosing || _panelState.HasOverlayInteraction || !_panelState.IsExpanded)
        {
            return;
        }

        if (IsPointerInsideWindow())
        {
            _panelState.EnterSurface();
            return;
        }

        _panelState.LeaveSurface();
        // 重启=完整新一轮 250ms：DispatcherTimer.Start 对运行中的计时器是空操作，
        // 旧期限（离开瞬间起算）不得穿透覆盖交互的释放时刻继续倒计时。
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    /// <summary>指针几何包含测试：GetCursorPos+GetWindowRect 屏幕物理像素直比（与
    /// 再展开抑制窗同一坐标系，含左上不含右下）；取值失败按保守=在内处理。</summary>
    private bool IsPointerInsideWindow()
    {
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return true;
        }

        return _windowSource?.Handle is not { } handle ||
            !NativeMethods.GetWindowRect(handle, out var rect) ||
            (cursor.X >= rect.Left && cursor.X < rect.Right &&
             cursor.Y >= rect.Top && cursor.Y < rect.Bottom);
    }

    /// <summary>已安装应用导入：PackagedApp 条目（TargetPath=AUMID，永不存包名）＋
    /// 状态条一次性汇报＋落位动画（与文件导入同路径）。</summary>
    private async Task ImportInstalledAppsAsync(IReadOnlyList<InstalledApp> apps)
    {
        if (_launchers is null)
        {
            return;
        }

        var candidates = apps
            .Select(app => new LauncherItem(
                Guid.NewGuid(),
                LauncherKind.PackagedApp,
                app.Aumid,
                app.DisplayName,
                DateTimeOffset.Now))
            .ToList();

        var outcome = await _launchers.AddRangeAsync(candidates);
        // 保存续体可能落在无同步上下文的线程（测试环境）：UI 反馈显式回投 UI 线程。
        await Dispatcher.InvokeAsync(() =>
        {
            if (outcome.AddedCount > 0)
            {
                PlayLauncherLandingAsync(outcome.AddedItems);
            }

            foreach (var duplicate in outcome.Duplicates)
            {
                PulseLauncherDuplicate(duplicate);
            }

            ShowLauncherImportStatus(outcome, skippedTotal: 0);
        });
    }

    internal void UseInstalledAppsSourceForTests(Func<IReadOnlyList<InstalledApp>> source) =>
        _installedAppsSource = source;

    /// <summary>网页地址导入：Url 条目（TargetPath=规范形，默认名=域名）＋状态条一次性
    /// 汇报＋落位动画（与文件/选择器导入同路径）。</summary>
    private async Task ImportLauncherUrlsAsync(string url, string defaultName)
    {
        if (_launchers is null)
        {
            return;
        }

        var outcome = await _launchers.AddRangeAsync(
        [
            new LauncherItem(Guid.NewGuid(), LauncherKind.Url, url, defaultName, DateTimeOffset.Now)
        ]);
        // 保存续体可能落在无同步上下文的线程（测试环境）：UI 反馈显式回投 UI 线程。
        await Dispatcher.InvokeAsync(() =>
        {
            if (outcome.AddedCount > 0)
            {
                PlayLauncherLandingAsync(outcome.AddedItems);
            }

            foreach (var duplicate in outcome.Duplicates)
            {
                PulseLauncherDuplicate(duplicate);
            }

            ShowLauncherImportStatus(outcome, skippedTotal: 0);
        });
    }

    /// <summary>文件导入分流（.exe/.lnk 导入，其余跳过计数）＋状态条一次性汇报＋落位动画。</summary>
    private async Task ImportLauncherFilesAsync(IReadOnlyList<string> files, int skippedUnsupportedCount)
    {
        if (_launchers is null)
        {
            return;
        }

        var (importable, skipped) = LauncherFileImport.Split(files);
        var skippedTotal = skippedUnsupportedCount + skipped;
        var candidates = importable
            .Select(path => new LauncherItem(
                Guid.NewGuid(),
                LauncherKind.Application,
                path,
                ShellLinkHelper.DefaultDisplayName(path),
                DateTimeOffset.Now))
            .ToList();

        var outcome = await _launchers.AddRangeAsync(candidates);
        // 保存续体可能落在无同步上下文的线程（测试环境）：UI 反馈显式回投 UI 线程。
        await Dispatcher.InvokeAsync(() =>
        {
            if (outcome.AddedCount > 0)
            {
                PlayLauncherLandingAsync(outcome.AddedItems);
            }

            foreach (var duplicate in outcome.Duplicates)
            {
                PulseLauncherDuplicate(duplicate);
            }

            ShowLauncherImportStatus(outcome, skippedTotal);
        });
    }

    private void ShowLauncherImportStatus(LauncherAddOutcome outcome, int skippedTotal)
    {
        if (outcome.AddedCount == 0 && outcome.Duplicates.Count == 0 && outcome.OverflowSkippedCount > 0)
        {
            ShowStatus($"启动项已达上限（{LauncherCatalog.MaxItems}）");
            return;
        }

        // 全零结果（无新增/无重复/无跳过/无超额）静默；纯单条重复不在此列——
        // 契约 #30 断言其汇报「已在启动列表中」＋滚动至已有瓦片＋accent 脉冲。
        if (outcome.AddedCount == 0 && outcome.Duplicates.Count == 0 && skippedTotal == 0 &&
            outcome.OverflowSkippedCount == 0)
        {
            return;
        }

        if (outcome.AddedCount == 0 && outcome.Duplicates.Count == 1 && skippedTotal == 0 &&
            outcome.OverflowSkippedCount == 0)
        {
            ShowStatus("已在启动列表中");
            return;
        }

        // 「添加了 N 项，跳过 M 个不支持的文件，K 个已存在」（零段省略）。
        var segments = new List<string> { $"添加了 {outcome.AddedCount} 项" };
        if (skippedTotal > 0)
        {
            segments.Add($"跳过 {skippedTotal} 个不支持的文件");
        }

        if (outcome.Duplicates.Count > 0)
        {
            segments.Add($"{outcome.Duplicates.Count} 个已存在");
        }

        ShowStatus(string.Join("，", segments) + "。");
    }

    // ---- 瓦片分区手势（#30 修订 2026-10-18，Issue #186）：启动区单击=启动（500ms
    // 重入门闩保持）；名称带单击=聚焦、双击=就地改名；右上勾选钮 toggle 多选；
    // Ctrl+单击=toggle、Shift+单击=范围选；选择非空 ⇒ 瓦片非控件区单击=toggle 选择
    // （启动挂起）。区域判定沿可视树 Tag 路径（CardGestureZones，与卡片同机制族）。----

    private void LauncherTile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: LauncherItem item } tile)
        {
            return;
        }

        _launcherPressItemId = item.Id;
        _launcherPressStartPosition = e.GetPosition(this);
        _launcherPressZone = ResolveLauncherZone(e.OriginalSource as DependencyObject);
        AnimateLauncherPressScale(tile, pressed: true);
        e.Handled = true;
    }

    private void LauncherTile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: LauncherItem item } tile)
        {
            return;
        }

        var pressId = _launcherPressItemId;
        var pressZone = _launcherPressZone;
        _launcherPressItemId = null;
        _launcherPressZone = null;
        AnimateLauncherPressScale(tile, pressed: false);
        if (_launcherDraggingId is not null)
        {
            // 拖拽重排结束（放置=提交；瓦片仍捕获鼠标，需释放）。
            tile.ReleaseMouseCapture();
            EndLauncherTileDrag(apply: true);
            return;
        }

        var displacement = e.GetPosition(this) - _launcherPressStartPosition;
        var isClick = pressId == item.Id &&
            Math.Abs(displacement.X) < 4 &&
            Math.Abs(displacement.Y) < 4;
        if (!isClick)
        {
            return;
        }

        e.Handled = true;
        // 修饰键读输入设备全局态（MouseEventArgs 无自带 KeyboardDevice；真实输入
        // 与合成注入都在 Click/Up 分发前置位 Keyboard.Modifiers 即可达）。
        var modifiers = Keyboard.Modifiers;
        var hasSelection = LauncherSelectedCount() > 0;

        // 选择非空：瓦片非控件区（启动区/名称带）单击一律 toggle 选择（启动挂起；
        // 取消最后一项的这击不得顺带启动）。勾选钮自身经按钮 Click 路径（不进此分支）。
        if (hasSelection && modifiers == ModifierKeys.None)
        {
            ToggleLauncherSelection(item);
            return;
        }

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            // Ctrl+单击=逐击 toggle（对齐卡片 RangeSelection 语义，锚点更新）。
            _launcherFocusedIndex = IndexOfLauncherItem(item);
            ApplyLauncherFocusVisual();
            ToggleLauncherSelection(item);
            return;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            // Shift+单击=范围选（当前可见序=目录数组序；非叠加=先清空再范围选）。
            _launcherFocusedIndex = IndexOfLauncherItem(item);
            ApplyLauncherFocusVisual();
            SelectLauncherRange(item, additive: false);
            return;
        }

        _launcherFocusedIndex = IndexOfLauncherItem(item);
        ApplyLauncherFocusVisual();
        if (pressZone == CardGestureZones.LauncherNameZone)
        {
            // 名称带单击=不启动不选择（仅聚焦）；双击窗内的第二击=就地改名。
            if (_launcherLastClick is { } last &&
                last.ItemId == item.Id &&
                DoubleClickWindow.Contains(last.Timestamp, last.Position, e.Timestamp, _launcherPressStartPosition, this))
            {
                _launcherLastClick = null;
                BeginLauncherTileRename(item);
                return;
            }

            _launcherLastClick = (item.Id, e.Timestamp, _launcherPressStartPosition);
            return;
        }

        // 启动区（与瓦片余面）单击=启动瞬时＋500ms 重入门闩。
        if (IsLauncherLaunchLatched(item))
        {
            return;
        }

        LaunchLauncherItem(item, tile);
    }

    private void LauncherTile_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 右键不启动、不改选择（启动判定要求左键 PointerUp 同瓦片 <4 DIP，结构
        // 不可达；菜单由 TileRoot.ContextMenu 承载）。
    }

    /// <summary>沿命中元素向上的可视树路径取第一个瓦片分区标记；未遇到标记
    /// （瓦片内边距环带、容器自身）归启动区——贴边点击承载启动/拖拽，不误触名称带。</summary>
    private static string? ResolveLauncherZone(DependencyObject? source)
    {
        for (var current = source; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { Tag: string tag } &&
                (tag == CardGestureZones.LauncherLaunchZone || tag == CardGestureZones.LauncherNameZone))
            {
                return tag;
            }
        }

        return null;
    }

    /// <summary>500ms 重入门闩按瓦片计：同瓦片双击抖动不重复派发；不同瓦片的连击
    /// （如成功后立刻试另一个失败目标）是独立有效请求——失败取消收起路径由此可达。</summary>
    private bool IsLauncherLaunchLatched(LauncherItem item) =>
        _lastLaunchAttemptPerItem.TryGetValue(item.Id, out var lastAttempt) &&
        (DateTime.UtcNow - lastAttempt).TotalMilliseconds < DesignTokens.LaunchReentryLatchMs;

    private int IndexOfLauncherItem(LauncherItem item) => _launchers.Items.IndexOf(item);

    /// <summary>启动编排（#30）：成功=确认脉冲+排队收起；已派发=状态条「已尝试启动」+
    /// 收起放行（不播成功脉冲）；失败=描边闪烁+常显灰态+状态条（不收起，并取消在途收起）。</summary>
    private void LaunchLauncherItem(LauncherItem item, Border tile)
    {
        _lastLaunchAttemptPerItem[item.Id] = DateTime.UtcNow;
        var result = _appLauncher.Launch(item);
        switch (result.Status)
        {
            case LauncherLaunchStatus.Launched:
                item.LaunchFailed = false;
                PlayLauncherConfirmPulse(tile);
                ScheduleLaunchCollapse(item.Id);
                break;
            case LauncherLaunchStatus.Dispatched:
                item.LaunchFailed = false;
                ShowStatus("已尝试启动");
                ScheduleLaunchCollapse(item.Id);
                break;
            case LauncherLaunchStatus.Failed:
            default:
                // 失败不删项、不显成功、不收起；更新的失败启动取消在途收起保展开反馈。
                _launchCollapseTimer.Stop();
                item.LaunchFailed = true;
                PlayLauncherFailureFlash(tile);
                ShowStatus($"无法启动：{result.FailureMessage}");
                if (result.SuggestReenumerate)
                {
                    // 应用未注册（0x80270254/0x80070490）：重枚举取图（AUMID 条目
                    // stamp=null 恒走提取路径；失败保留最后成功 PNG，灰态由
                    // LaunchFailed 承载，#30 §三.6）。
                    RefreshLauncherIcon(item);
                }

                break;
        }
    }

    // ---- 排队收起（r2-M5 有效性协议）：250ms 后到期重验，任一抑制条件出现即取消 ----

    private void ScheduleLaunchCollapse(Guid itemId)
    {
        _launchCollapseSession = _expandSessionId;
        _launchCollapseTimer.Stop();
        _launchCollapseTimer.Start();
    }

    private void LaunchCollapseTimer_Tick(object? sender, EventArgs e)
    {
        _launchCollapseTimer.Stop();
        if (_isClosing ||
            !_viewModel.IsPanelExpanded ||
            _launchCollapseSession != _expandSessionId ||
            IsPanelEditHoldActive() ||
            _panelState.IsPanelHoldActive ||
            _panelState.HasOverlayInteraction)
        {
            // 任一抑制条件在等待期出现即取消本次收起（失败启动在 LaunchLauncherItem
            // 内已直接停表＝更新的失败取消收起保展开反馈）。改名编辑器持有焦点的
            // 抑制由 IsPanelEditHoldActive 族承载（#7 编辑保持；编辑模式已移除）。
            return;
        }

        // 单源几何快照（Issue #173）：布防判定与收起放置消费同一次 Collapsed 计算。
        var collapsedPlacement = WindowController.Collapsed(
            CurrentWorkArea(),
            _settings,
            _viewModel.DefaultCapturePanel.Category);
        ArmReexpandSuppressionForCollapsedHandle(collapsedPlacement);
        // 强制提交：指针在内不是启动收起的抑制项（再展开抑制窗另行承载），而
        // PanelStateMachine.TryCollapse 会在指针在内时拒绝——此处跳过该检查直接落收起。
        _panelCollapseExitPending = false;
        StopCategoryRevealAnimations();
        _panelState.ForceCollapse();
        CompletePanelCollapseHandoff(collapsedPlacement);
    }

    /// <summary>收起态落地共用尾段（入场重播标记重置 + 几何交接）；面板收起自动
    /// 提交改名会话并清空多选、销毁撤销提示条（#30 §四/§三.7；2026-10-18 起
    /// 「编辑模式自动退出」由「改名提交+选择清空」承接）；＋菜单随收起关闭。</summary>
    private void CompletePanelCollapseHandoff(WindowPlacement collapsedPlacement)
    {
        CommitLauncherTileRename();
        ClearLauncherSelection();
        DestroyLauncherUndoToast();
        LauncherAddMenu.IsOpen = false;
        ResetLauncherEntranceReplay();
        BeginCollapsedVisualHandoff(collapsedPlacement);
    }

    /// <summary>条件布防再展开抑制会话（#30，Issue #173）：观测区＝本次收起放置的
    /// 把手物理矩形（悬停意图与伪回弹威胁的唯一诞生地），与收起交接消费同一份单源
    /// 几何快照换算。指针在把手内→布防新会话（替换旧会话）；在外或读取/换算失败→
    /// 结束既有会话且不布防（fail-open：连发启动不得残留旧闩）。</summary>
    private void ArmReexpandSuppressionForCollapsedHandle(WindowPlacement collapsedPlacement)
    {
        if (ToPhysicalRectangle(collapsedPlacement) is not { } handleRect)
        {
            _panelState.EndReexpandSuppression();
            return;
        }

        if (NativeMethods.GetCursorPos(out var cursor) &&
            handleRect.Contains(cursor.X, cursor.Y))
        {
            _panelState.ArmReexpandSuppression(
                handleRect.Left,
                handleRect.Top,
                handleRect.Right,
                handleRect.Bottom);
            _reexpandSuppressionPollTimer.Start();
            return;
        }

        _panelState.EndReexpandSuppression();
    }

    /// <summary>DIP 放置→物理像素矩形（含左上不含右下）：两条锚定边各自取整后相减
    /// 得跨度，右/底缘换算后不因取整漂移——与 TryApplyPlacementAtomically 同一换算
    /// 口径（先例 EdgeHide「DIP 为权威、按 CompositionTarget 换算一次」）；窗口源
    /// 未就绪＝null（调用方 fail-open）。</summary>
    private PhysicalRectangle? ToPhysicalRectangle(WindowPlacement placement)
    {
        if (_windowSource?.CompositionTarget is not { } target)
        {
            return null;
        }

        var transform = target.TransformToDevice;
        return new PhysicalRectangle(
            (int)Math.Round(placement.Left * transform.M11),
            (int)Math.Round(placement.Top * transform.M22),
            (int)Math.Round((placement.Left + placement.Width) * transform.M11),
            (int)Math.Round((placement.Top + placement.Height) * transform.M22));
    }

    /// <summary>抑制离场观测轮询：仅布防期间运行（自守卫停表对齐贴边隐藏回位轮询
    /// 式样）；GetCursorPos 失败跳过本周期（失败保持抑制，不误解除）。解除的另一
    /// 途径是权威展开——Switch 清闩后本回调在下一周期自停。</summary>
    private void ReexpandSuppressionPollTimer_Tick(object? sender, EventArgs e)
    {
        if (_isClosing || !_panelState.IsReexpandSuppressed)
        {
            _reexpandSuppressionPollTimer.Stop();
            return;
        }

        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        if (_panelState.ObservePointer(cursor.X, cursor.Y))
        {
            _reexpandSuppressionPollTimer.Stop();
        }
    }

    /// <summary>收起时重置交错标记（M1 再展开重播；由 CommitPanelCollapse 调用）。</summary>
    internal void ResetLauncherEntranceReplay() => _launcherEntrancePlayed = false;

    // ---- 瓦片图标（内存缓存零 I/O；未命中经 STA 图标管线异步回填占位）----

    private void LauncherTileIcon_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image || image.DataContext is not LauncherItem item)
        {
            return;
        }

        RequestLauncherIcon(item, image);
    }

    private void RequestLauncherIcon(LauncherItem item, Image image)
    {
        if (_launcherIcons is null)
        {
            return;
        }

        var cached = _launcherIcons.TryGetCached(item.IconKey);
        if (cached is not null)
        {
            image.Tag = cached;
            return;
        }

        _launcherIcons.RequestIcon(item, icon =>
        {
            if (icon is not null && !_isClosing)
            {
                image.Tag = icon;
            }
        });
    }

    /// <summary>「应用未注册」失败后的重枚举取图：绕过缓存直发提取线程，回填瓦片。</summary>
    private void RefreshLauncherIcon(LauncherItem item)
    {
        if (_launcherIcons is null || _launcherTileContainers.GetValueOrDefault(item.Id) is not { } tile)
        {
            return;
        }

        var image = FindDescendant<Image>(tile);
        if (image is null)
        {
            return;
        }

        _launcherIcons.RequestRefresh(item, icon =>
        {
            if (icon is not null && !_isClosing)
            {
                image.Tag = icon;
            }
        });
    }

    // ---- 表面拖放：文件拖入分流（.exe/.lnk 导入，其余跳过并计数）----

    private void LauncherSurface_DragEnter(object sender, DragEventArgs e)
    {
        UpdateLauncherDropSurface(e);
    }

    private void LauncherSurface_DragOver(object sender, DragEventArgs e)
    {
        UpdateLauncherDropSurface(e);
    }

    private void LauncherSurface_DragLeave(object sender, DragEventArgs e)
    {
        SetLauncherDropHighlight(active: false);
    }

    /// <summary>点空白清空选择（#30 修订 2026-10-18，取代原「点空白退出编辑模式」）：
    /// 命中瓦片/工具行/提示条/滚动区内容的按下不在此列（由各自控件消费），命中
    /// 表面背景即清空多选（Esc 优先级=编辑器>选择，编辑器层先行）。</summary>
    private void LauncherSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (LauncherSelectedCount() == 0)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source &&
            (IsWithinLauncherTile(source) || source is ButtonBase or TextBox or ScrollBar))
        {
            return;
        }

        ClearLauncherSelection();
        e.Handled = true;
    }

    private static bool IsWithinLauncherTile(DependencyObject source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is Border { DataContext: LauncherItem })
            {
                return true;
            }

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private async void LauncherSurface_Drop(object sender, DragEventArgs e)
    {
        var importable = ReadImportableLauncherFiles(e.Data);
        SetLauncherDropHighlight(active: false);
        e.Effects = importable.Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (importable.Count == 0)
        {
            return;
        }

        var files = ReadRawLauncherFiles(e.Data);
        var skipped = files.Count - importable.Count;
        var import = ImportLauncherFilesAsync(importable, skipped);
        TrackPendingOperation(import);
        await import;
    }

    private void UpdateLauncherDropSurface(DragEventArgs e)
    {
        var importable = ReadImportableLauncherFiles(e.Data);
        SetLauncherDropHighlight(importable.Count > 0);
        e.Effects = importable.Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static IReadOnlyList<string> ReadRawLauncherFiles(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop, autoConvert: true))
            {
                return [];
            }

            return data.GetData(DataFormats.FileDrop, autoConvert: true) switch
            {
                string[] array => array,
                System.Collections.Specialized.StringCollection collection => collection.Cast<string>().ToArray(),
                _ => []
            };
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<string> ReadImportableLauncherFiles(IDataObject data)
    {
        var files = ReadRawLauncherFiles(data);
        return LauncherFileImport.Split(files).Importable;
    }

    /// <summary>外部拖入高亮 167ms（减动效＝即时终态）。</summary>
    private void SetLauncherDropHighlight(bool active)
    {
        var target = active ? 1d : 0d;
        LauncherDropHint.SetCurrentValue(VisibilityProperty, active ? Visibility.Visible : Visibility.Collapsed);
        if (!ClientAreaAnimationsEnabled)
        {
            LauncherDropHighlight.BeginAnimation(UIElement.OpacityProperty, null);
            LauncherDropHighlight.SetCurrentValue(UIElement.OpacityProperty, target);
            return;
        }

        LauncherDropHighlight.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(target, new Duration(TimeSpan.FromMilliseconds(DesignTokens.HoverFeedbackMs)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    // ---- 瓦片网格几何：单一几何源=LauncherTilePanel（拉伸填充，#30 修订 2026-10-18，
    // Issue #186）。列数/瓦片宽由面板在 Measure/Arrange 内经 LauncherGridMath 从视口
    // 宽现算；ScrollChanged 只保留拖拽命中槽刷新，不再回写网格宽度（自引用结构消灭，
    // 窄轨跳帧补算特例随之失去存在必要）。

    private void LauncherScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 拖动在途的滚动改变瓦片窗口坐标：落点命中槽即时重建（其余路径无需响应——
        // 几何随布局系统自动更新）。Wc=内容区视口实测宽的输入职责已内化到面板。
        if (_launcherDraggingId is { } draggingId && _launcherDragHitSlots is not null)
        {
            RefreshLauncherDragHitSlots(draggingId);
        }
    }

    /// <summary>瓦片网格面板（ItemsPanel）；焦点导航与拖拽槽共用的几何探针。</summary>
    private LauncherTilePanel? LauncherTilePanelRoot =>
        _launcherTilePanelRoot ??= Descendants<LauncherTilePanel>(LauncherTileGrid).FirstOrDefault();

    private LauncherTilePanel? _launcherTilePanelRoot;

    // ---- 动效（§五）：悬停/按压走变换，入场/落位/确认/失败为场景动画，全部减动效降级 ----

    private void LauncherTile_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border tile)
        {
            return;
        }

        AnimateLauncherHoverLift(tile, lifted: true);
    }

    private void LauncherTile_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Border tile)
        {
            return;
        }

        AnimateLauncherHoverLift(tile, lifted: false);
        AnimateLauncherPressScale(tile, pressed: false);
    }

    private static TransformGroup LauncherTileTransforms(Border tile)
    {
        // 模板内 Freezable 可能被 WPF 冻结（sealed）：冻结实例不可动画/设值，
        // 冻结即换新组（动画挂在不冻结的实例上）。
        if (tile.RenderTransform is not { } existing || existing.IsFrozen || existing is not TransformGroup group)
        {
            group = new TransformGroup();
            group.Children.Add(new TranslateTransform());
            group.Children.Add(new ScaleTransform());
            tile.RenderTransform = group;
        }

        return group;
    }

    private static TranslateTransform LauncherTileTranslate(Border tile)
    {
        var group = LauncherTileTransforms(tile);
        var translate = group.Children.OfType<TranslateTransform>().FirstOrDefault();
        if (translate is null || translate.IsFrozen)
        {
            var fresh = new TranslateTransform();
            ReplaceOrAppend(group, translate, fresh);
            translate = fresh;
        }

        return translate;
    }

    private static ScaleTransform LauncherTileScale(Border tile)
    {
        var group = LauncherTileTransforms(tile);
        var scale = group.Children.OfType<ScaleTransform>().FirstOrDefault();
        if (scale is null || scale.IsFrozen)
        {
            var fresh = new ScaleTransform();
            ReplaceOrAppend(group, scale, fresh);
            scale = fresh;
        }

        return scale;
    }

    private static void ReplaceOrAppend(TransformGroup group, Transform? existing, Transform fresh)
    {
        if (existing is null)
        {
            group.Children.Add(fresh);
        }
        else
        {
            group.Children[group.Children.IndexOf(existing)] = fresh;
        }
    }

    /// <summary>悬停 167ms/−2px（瓦片位移常量，区别于卡片 −1px）。</summary>
    private void AnimateLauncherHoverLift(Border tile, bool lifted)
    {
        var translate = LauncherTileTranslate(tile);
        if (translate is null)
        {
            return;
        }

        var target = lifted ? DesignTokens.TileHoverLiftPx : 0d;
        if (!ClientAreaAnimationsEnabled)
        {
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.SetCurrentValue(TranslateTransform.YProperty, target);
            return;
        }

        translate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(target, new Duration(TimeSpan.FromMilliseconds(DesignTokens.HoverFeedbackMs)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    /// <summary>按压 83ms/0.96。</summary>
    private void AnimateLauncherPressScale(Border tile, bool pressed)
    {
        var scale = LauncherTileScale(tile);
        if (scale is null)
        {
            return;
        }

        var target = pressed ? DesignTokens.TilePressScale : 1d;
        if (!ClientAreaAnimationsEnabled)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.SetCurrentValue(ScaleTransform.ScaleXProperty, target);
            scale.SetCurrentValue(ScaleTransform.ScaleYProperty, target);
            return;
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(DesignTokens.PressFeedbackMs));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(target, duration));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(target, duration));
    }

    /// <summary>启动确认 M7：250ms Scale 1→1.06→1 + accent 20% 闪 83ms/淡出 167ms（减动效=accent 闪即时）。</summary>
    private void PlayLauncherConfirmPulse(Border tile)
    {
        var scale = LauncherTileScale(tile);
        if (scale is not null && ClientAreaAnimationsEnabled)
        {
            var animation = new DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(TimeSpan.FromMilliseconds(DesignTokens.LaunchConfirmMs))
            };
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(1.06, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(125))));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(DesignTokens.LaunchConfirmMs))));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }

        if (!ClientAreaAnimationsEnabled)
        {
            return;
        }

        tile.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0.8, 1.0, new Duration(TimeSpan.FromMilliseconds(DesignTokens.NormalDurationMs))));
    }

    /// <summary>失败描边闪 2 次（83ms×2）后常显（DataTrigger 常显灰态接管）。</summary>
    private void PlayLauncherFailureFlash(Border tile)
    {
        if (!ClientAreaAnimationsEnabled)
        {
            return;
        }

        var flash = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(TimeSpan.FromMilliseconds(DesignTokens.PressFeedbackMs * 4))
        };
        var step = TimeSpan.FromMilliseconds(DesignTokens.PressFeedbackMs);
        flash.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(step)));
        flash.KeyFrames.Add(new EasingDoubleKeyFrame(0.2, KeyTime.FromTimeSpan(step * 2)));
        flash.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(step * 3)));
        flash.KeyFrames.Add(new EasingDoubleKeyFrame(0.2, KeyTime.FromTimeSpan(step * 4)));
        tile.BeginAnimation(UIElement.OpacityProperty, flash);
    }

    /// <summary>重复项脉冲 250ms（accent 20%）＋滚动至已有瓦片。</summary>
    private void PulseLauncherDuplicate(LauncherItem duplicate)
    {
        var tile = FindLauncherTile(duplicate);
        if (tile is null)
        {
            return;
        }

        tile.BringIntoView();
        if (!ClientAreaAnimationsEnabled)
        {
            return;
        }

        tile.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0.8, 1.0, new Duration(TimeSpan.FromMilliseconds(DesignTokens.ItemLandingMs))));
    }

    /// <summary>新增项落位动画 M21：仅真正新增（多选 40ms 交错）；排序/图标回填/改名/撤销恢复一律不触发。</summary>
    private async void PlayLauncherLandingAsync(IReadOnlyList<LauncherItem> added)
    {
        if (!ClientAreaAnimationsEnabled)
        {
            return;
        }

        for (var index = 0; index < added.Count; index++)
        {
            if (index > 0)
            {
                await Task.Delay(DesignTokens.TileEntranceStaggerMs);
            }

            if (_isClosing)
            {
                return;
            }

            var item = added[index];
            var tile = FindLauncherTile(item);
            if (tile is null)
            {
                continue;
            }

            var scale = LauncherTileScale(tile);
            if (scale is not null)
            {
                scale.BeginAnimation(
                    ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(0.8, 1.0, TimeSpan.FromMilliseconds(DesignTokens.ItemLandingMs)));
                scale.BeginAnimation(
                    ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(0.8, 1.0, TimeSpan.FromMilliseconds(DesignTokens.ItemLandingMs)));
            }

            tile.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(0, 1.0, TimeSpan.FromMilliseconds(DesignTokens.ItemLandingMs)));
        }
    }

    /// <summary>表面展开动画 M1：收起→展开进入启动标签时全部瓦片 167ms 交错入场
    /// （min(i,8)×40ms 封顶）；收起时重置交错标记（再展开重播）。已展开内切标签
    /// 的 140ms 整体淡入由 AnimatePanelContent 承载。</summary>
    private async void PlayLauncherSurfaceEntrance()
    {
        try
        {
            if (_launchers is null)
            {
                return;
            }

            if (!ClientAreaAnimationsEnabled || _launcherEntrancePlayed)
            {
                _launcherEntrancePlayed = true;
                return;
            }

            _launcherEntrancePlayed = true;
            for (var index = 0; index < _launchers.Items.Count; index++)
            {
                if (index > 0)
                {
                    await Task.Delay(DesignTokens.TileEntranceStaggerMs);
                }

                if (_isClosing)
                {
                    return;
                }

                var item = _launchers.Items[index];
                var tile = FindLauncherTile(item);
                if (tile is null)
                {
                    continue;
                }

                tile.BeginAnimation(
                    UIElement.OpacityProperty,
                    new DoubleAnimation(0, 1.0, TimeSpan.FromMilliseconds(DesignTokens.NormalDurationMs)));
                var translate = LauncherTileTranslate(tile);
                translate?.BeginAnimation(
                    TranslateTransform.YProperty,
                    new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(DesignTokens.NormalDurationMs)));
            }
        }
        finally
        {
            _launcherEntrancePlayed = true;
        }
    }

    private Border? FindLauncherTile(LauncherItem item)
    {
        // 拖动命中缓存的结构探针（契约守护用）：拖动期间的 MouseMove 不允许再
        // 走视觉树解析（缓存命中＝零查找），测试据此断言与瓦片数解耦。
        _launcherTileLookupCountForTests++;
        var index = IndexOfLauncherItem(item);
        if (index < 0)
        {
            return null;
        }

        // 纯 ItemsControl 的容器是 ContentPresenter：瓦片根 Border 是其模板子元素。
        return LauncherTileGrid.ItemContainerGenerator.ContainerFromIndex(index) is { } container
            ? (container as Border) ?? Descendants<Border>(container).FirstOrDefault()
            : null;
    }

    private int _launcherTileLookupCountForTests;

    /// <summary>测试探针：FindLauncherTile（视觉树解析）累计调用数。</summary>
    internal int LauncherTileLookupCountForTests => _launcherTileLookupCountForTests;

    private void ClearLauncherDragHitCache()
    {
        _launcherDraggingTile = null;
        _launcherDragHitSlots = null;
    }

    // ---- 键盘层级（#30 修订 2026-10-18，Issue #186）：①改名编辑器原生 → ②瓦片键盘
    // （Enter=启动、Space=toggle 选择、F2=改名、Alt+Enter=编辑目标、Delete=删
    // （选择非空→批量、否则焦点项，均带撤销）、Ctrl+A=全选/清空、Esc=编辑器>选择>
    // （点空白同语义）、方向键=网格导航）→ ③窗口级命令照复盘现状。每层消费后标记
    // 已处理。编辑模式（原层级②）已随 D3 移除——改名/删除/拖拽由常态手势承载。----

    /// <summary>返回 true=按键已被启动表面消费（调用方据此短路既有窗口级处理）。
    /// 面板收起态不消费（瓦片键盘语义只属于展开态；窗口级命令照常到达）。</summary>
    private bool HandleLauncherPreviewKey(KeyEventArgs e)
    {
        if (!IsLauncherActive() ||
            !_viewModel.IsPanelExpanded ||
            _launchers is null ||
            _launchers.Items.Count == 0)
        {
            if (IsLauncherActive() && _viewModel.IsPanelExpanded &&
                e.Key is Key.Delete or Key.Enter or Key.Space)
            {
                // 空列表：启动表面消费启动/删除键。
                return e.Key is Key.Enter or Key.Space or Key.Delete;
            }

            return false;
        }

        if (Keyboard.FocusedElement is TextBoxBase)
        {
            // 层级①：瓦片改名编辑器原生文本语义（IME 守卫照 #7/#22）。
            return false;
        }

        // 真实键盘按住 Alt 时 WPF 把 Key 报为 Key.System、实际键在 SystemKey
        // （T7 评审修订，Issue #186；合成事件构造 Key.Enter 不经此路径）。
        var effectiveKey = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (effectiveKey)
        {
            case Key.Enter when e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Alt):
                // Alt+Enter=编辑目标焦点项（D7 键盘路径，与右键菜单同入口）。
                if (_launcherFocusedIndex >= 0 && _launcherFocusedIndex < _launchers.Items.Count)
                {
                    BeginEditLauncherTarget(_launchers.Items[_launcherFocusedIndex]);
                }

                return true;
            case Key.Enter:
                if (_launcherFocusedIndex is var focus && focus >= 0 && focus < _launchers.Items.Count)
                {
                    var item = _launchers.Items[focus];
                    var tile = FindLauncherTile(item);
                    if (tile is not null && !IsLauncherLaunchLatched(item))
                    {
                        LaunchLauncherItem(item, tile);
                    }
                }

                return true;
            case Key.Space:
                // Space=toggle 焦点瓦片选择（#30 修订：原「Space=启动」随交互统一废止；
                // 启动仍由 Enter 与单击承载）。
                if (_launcherFocusedIndex >= 0 && _launcherFocusedIndex < _launchers.Items.Count)
                {
                    ToggleLauncherSelection(_launchers.Items[_launcherFocusedIndex]);
                }

                return true;
            case Key.Left:
                MoveLauncherFocus(-1, 0);
                return true;
            case Key.Right:
                MoveLauncherFocus(1, 0);
                return true;
            case Key.Up:
                MoveLauncherFocus(0, -1);
                return true;
            case Key.Down:
                MoveLauncherFocus(0, 1);
                return true;
            case Key.Delete:
                // Delete=删除：选择非空→整批（带撤销）；否则焦点项（#30 修订：
                // 原「查看态 Delete 无操作」废止——常态手势承载后删除键盘可达）。
                if (LauncherSelectedCount() > 0)
                {
                    TrackPendingOperation(DeleteSelectedLauncherItemsAsync());
                }
                else if (_launcherFocusedIndex >= 0 && _launcherFocusedIndex < _launchers.Items.Count)
                {
                    TrackPendingOperation(DeleteLauncherItemAsync(_launchers.Items[_launcherFocusedIndex]));
                }

                return true;
            case Key.A when e.KeyboardDevice.Modifiers == ModifierKeys.Control:
                // Ctrl+A=全选/再按清空（#30 修订：原「Ctrl+A 表面切换排除」随瓦片
                // 获得多选修订——排除面其余项不变）。锚点恒为选中项（T7 评审修订）：
                // 全选不清锚点，无效锚点补为焦点项，Shift 范围选基准不因全选失效。
                if (LauncherSelectedCount() == _launchers.Items.Count)
                {
                    ClearLauncherSelection();
                }
                else
                {
                    foreach (var entry in _launchers.Items)
                    {
                        entry.IsSelected = true;
                    }

                    if (_launcherSelectionAnchorId is not { } anchorId ||
                        _launchers.Items.FirstOrDefault(entry => entry.Id == anchorId)?.IsSelected != true)
                    {
                        var anchorIndex = _launcherFocusedIndex >= 0 ? _launcherFocusedIndex : 0;
                        _launcherSelectionAnchorId = _launchers.Items[anchorIndex].Id;
                    }

                    UpdateLauncherDeleteButtonForSelection();
                }

                return true;
            case Key.F2:
                // F2=改名焦点项（优先于窗口级标签改名 F2）。
                if (_launcherFocusedIndex >= 0 && _launcherFocusedIndex < _launchers.Items.Count)
                {
                    BeginLauncherTileRename(_launchers.Items[_launcherFocusedIndex]);
                }

                return true;
            case Key.Escape:
                // Esc=编辑器>选择（编辑器层在层级①已短路；此处清空多选）。
                if (LauncherSelectedCount() > 0)
                {
                    ClearLauncherSelection();
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    private void MoveLauncherFocus(int deltaColumns, int deltaRows)
    {
        // 列数与瓦片排版同一来源（LauncherTilePanel→LauncherGridMath），导航步长
        // 恒与真实布局一致（旧「网格宽/92 再量化」口径随双几何源一并消灭）。
        var columns = Math.Max(1, LauncherTilePanelRoot?.EffectiveColumnCount ?? 1);
        var current = _launcherFocusedIndex < 0 ? 0 : _launcherFocusedIndex;
        var target = current + deltaColumns + deltaRows * columns;
        _launcherFocusedIndex = Math.Clamp(target, 0, _launchers.Items.Count - 1);
        ApplyLauncherFocusVisual();
    }

    private void ApplyLauncherFocusVisual()
    {
        foreach (var (_, tile) in _launcherTileContainers)
        {
            tile.SetCurrentValue(Border.BorderBrushProperty, Brushes.Transparent);
        }

        _launcherTileContainers.Clear();
        // 焦点指示同步到勾选钮可见性绑定（IsKeyboardFocused 会话态，#13 族）。
        foreach (var entry in _launchers.Items)
        {
            entry.IsKeyboardFocused = false;
        }

        if (_launcherFocusedIndex < 0 || _launchers is null || _launcherFocusedIndex >= _launchers.Items.Count)
        {
            return;
        }

        var focusedItem = _launchers.Items[_launcherFocusedIndex];
        focusedItem.IsKeyboardFocused = true;
        if (FindLauncherTile(focusedItem) is { } focused)
        {
            _launcherTileContainers[focusedItem.Id] = focused;
            focused.SetCurrentValue(Border.BorderBrushProperty, (Brush)FindResource("AccentBrush"));
            focused.BringIntoView();
        }
    }

    // ---- 多选与批量删除（#30 修订 2026-10-18，Issue #186；对齐卡片范式）：
    // 权威状态=条目 IsSelected（绑定源）；勾选钮/Ctrl 单击=toggle（锚点更新）、
    // Shift 单击=范围选（目录数组序）、Ctrl+A=全选/清空；选择非空 ⇒ 单击语义切换
    // 为选择（启动挂起）、勾选钮常显（绑定面承载）、头部垃圾桶=「删除已选 N 项」
    // ＋角标（复用既有 DeleteContentButton/SelectedCountBadge 机制，修订 #30
    // 「启动表面结构性隐藏」条款；批量置顶钮保持隐藏）；Esc/点空白=清空；
    // 取消最后一项选择的单击不得顺带启动（单击分支先判选择）；选择期挂起改名
    // 与拖拽会话。----

    private int LauncherSelectedCount()
    {
        var count = 0;
        if (_launchers is not null)
        {
            foreach (var entry in _launchers.Items)
            {
                if (entry.IsSelected)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private void ToggleLauncherSelection(LauncherItem item)
    {
        item.IsSelected = !item.IsSelected;
        // 锚点跟随选择事实（对齐卡片 RangeSelection 的「锚点恒为选中项」读取
        // 前提）：建立选中=锚到本项；取消选中=锚点不得留在未选中项上——转移到
        // 任一仍选中项，无选中则清空（Shift 范围选的基准由此保持有效）。
        if (item.IsSelected)
        {
            _launcherSelectionAnchorId = item.Id;
        }
        else if (_launcherSelectionAnchorId == item.Id)
        {
            _launcherSelectionAnchorId = _launchers.Items.FirstOrDefault(entry => entry.IsSelected)?.Id;
        }

        UpdateLauncherDeleteButtonForSelection();
    }

    /// <summary>Shift+单击=范围选（锚点↔目标的目录数组序区间；非叠加=先清空；
    /// 无锚点时自锚——语义对齐卡片 RangeSelection）。</summary>
    private void SelectLauncherRange(LauncherItem target, bool additive)
    {
        var items = _launchers.Items;
        var targetIndex = items.IndexOf(target);
        var anchorIndex = _launcherSelectionAnchorId is { } anchorId
            ? items.IndexOf(items.FirstOrDefault(entry => entry.Id == anchorId)!)
            : -1;
        if (anchorIndex < 0 || items.ElementAtOrDefault(anchorIndex)?.IsSelected != true)
        {
            anchorIndex = targetIndex;
            _launcherSelectionAnchorId = target.Id;
        }

        if (!additive)
        {
            foreach (var entry in items)
            {
                entry.IsSelected = false;
            }
        }

        for (var index = Math.Min(anchorIndex, targetIndex); index <= Math.Max(anchorIndex, targetIndex); index++)
        {
            items[index].IsSelected = true;
        }

        UpdateLauncherDeleteButtonForSelection();
    }

    private void ClearLauncherSelection()
    {
        if (_launchers is null)
        {
            return;
        }

        foreach (var entry in _launchers.Items)
        {
            entry.IsSelected = false;
        }

        _launcherSelectionAnchorId = null;
        UpdateLauncherDeleteButtonForSelection();
    }

    /// <summary>头部垃圾桶随选择集刷新（复用内容桶既有按钮与角标机制）：有选择=
    /// 「删除已选 N 项」+角标可见+按钮可见；无选择=隐藏（启动表面无清空语义——
    /// 批量置顶钮保持隐藏，修订 #30「启动表面结构性隐藏」条款仅限删除钮选择态）。</summary>
    private void UpdateLauncherDeleteButtonForSelection()
    {
        if (_isClosing)
        {
            return;
        }

        var count = LauncherSelectedCount();
        if (count > 0)
        {
            var label = $"删除已选 {count} 项";
            DeleteContentButton.ToolTip = label;
            AutomationProperties.SetName(DeleteContentButton, label);
            SelectedCountText.Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            SelectedCountBadge.Visibility = Visibility.Visible;
            FadeAnimation.SetIsActive(SelectedCountBadge, true);
            DeleteContentButton.Visibility = Visibility.Visible;
            DeleteContentButton.IsEnabled = true;
        }
        else if (IsLauncherActive())
        {
            SelectedCountBadge.Visibility = Visibility.Collapsed;
            FadeAnimation.SetIsActive(SelectedCountBadge, false);
            DeleteContentButton.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>启动表面垃圾桶点击：选择非空=整批删除（带撤销）；无选择=无操作
    /// （按钮隐藏，防御直达）。经 DeleteFromTrashButtonAsync 按激活表面分流进入。</summary>
    private async Task DeleteFromLauncherTrashAsync()
    {
        if (LauncherSelectedCount() == 0)
        {
            return;
        }

        await DeleteSelectedLauncherItemsAsync();
    }

    private void LauncherTileSelectButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LauncherItem item })
        {
            e.Handled = true;
            ToggleLauncherSelection(item);
        }
    }

    /// <summary>批量删除（单次原子聚合为逐条删除事务）：每条走既有删除事务链
    /// （RemoveAsync 单条撤销记录被链尾覆盖为最后一条——整批撤销经聚合记录按
    /// 原下标逆序重插）。</summary>
    private async Task DeleteSelectedLauncherItemsAsync()
    {
        if (_launchers is null || _launcherRenamingItem is not null || _launcherDraggingId is not null)
        {
            return;
        }

        // 会话标记是权威（绑定源），顺序取目录数组序——撤销按原下标逆序重插。
        var selected = _launchers.Items.Where(entry => entry.IsSelected).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        ClearLauncherSelection();
        var removals = new List<LauncherRemoveOutcome>(selected.Count);
        foreach (var item in selected)
        {
            if (_launcherRenamingItem?.Id == item.Id)
            {
                CancelLauncherTileRename();
            }

            var outcome = await _launchers.RemoveAsync(item.Id);
            if (outcome is not null)
            {
                removals.Add(outcome);
            }
        }

        if (removals.Count == 0)
        {
            return;
        }

        await Dispatcher.InvokeAsync(() =>
        {
            _launcherUndoRecord = null;
            _launcherUndoBatch = removals;
            LauncherUndoToastText.SetCurrentValue(
                TextBlock.TextProperty,
                removals.Count == 1
                    ? $"已删除「{removals[0].Item.DisplayName}」"
                    : $"已删除 {removals.Count} 项");
            FadeAnimation.SetIsActive(LauncherUndoToast, true);
            _launcherUndoTimer.Stop();
            _launcherUndoTimer.Start();
            if (_launcherFocusedIndex >= _launchers.Items.Count)
            {
                _launcherFocusedIndex = _launchers.Items.Count - 1;
            }

            ApplyLauncherFocusVisual();
        });
    }

    private T? FindTemplatePart<T>(FrameworkElement tile, string name)
        where T : FrameworkElement
    {
        if (tile.Name == name && tile is T exact)
        {
            return exact;
        }

        return Descendants<T>(tile).FirstOrDefault(element => element.Name == name);
    }

    // ---- 就地改名（名称带双击/F2/菜单「重命名」）：预填全选、Enter 提交/Esc 取消/
    // 失焦提交、空提交=回退原名绝不删除、IME 组合守卫（组合期的 Esc 属输入法操作
    // 不触发取消）。选择非空或拖拽在途时挂起（D5：选择期挂起改名会话）。----

    private LauncherItem? _launcherRenamingItem;
    private bool _launcherRenameComposing;

    private void BeginLauncherTileRename(LauncherItem item)
    {
        if (_isClosing ||
            _launchers is null ||
            _launchers.IsReadOnlyFromNewerVersion ||
            LauncherSelectedCount() > 0 ||
            _launcherDraggingId is not null)
        {
            return;
        }

        CommitLauncherTileRename();
        item.DraftName = item.DisplayName;
        item.IsEditingName = true;
        _launcherRenamingItem = item;
        _launcherFocusedIndex = IndexOfLauncherItem(item);
        ApplyLauncherFocusVisual();
        UpdateLayout();
        if (FindLauncherTile(item) is not { } tile ||
            FindTemplatePart<TextBox>(tile, "TileNameEditor") is not { } editor)
        {
            return;
        }

        editor.SetCurrentValue(TextBox.VisibilityProperty, Visibility.Visible);
        if (FindTemplatePart<TextBlock>(tile, "TileNameText") is { } nameText)
        {
            nameText.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
        }

        editor.Focus();
        editor.SelectAll();
    }

    /// <summary>提交改名（空/空白=回退原名）；保存失败由目录还原原名+状态条。</summary>
    private void CommitLauncherTileRename()
    {
        if (_launcherRenamingItem is not { } item)
        {
            return;
        }

        // 编辑器绑定 UpdateSourceTrigger=Explicit（预填经 source→target 仍生效）：
        // 用户键入只在提交时刻显式推回模型侧草稿——这是「键入到不了模型」断链的修复点，
        // 与分类改名「提交读 editor.Text」同一意图（CategoryEditing 经参数传控件文本，
        // 此处 Commit 亦由 Begin 的先提交会话调用，统一经会话 item 反查编辑器）。
        // Begin 找不到编辑器的会话跳过推回，草稿保持预填旧名＝无操作。
        if (FindLauncherTile(item) is { } tile &&
            FindTemplatePart<TextBox>(tile, "TileNameEditor") is { } editor)
        {
            editor.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }

        _launcherRenamingItem = null;
        var draft = item.DraftName?.Trim();
        var nextName = string.IsNullOrEmpty(draft) ? item.DisplayName : draft;
        item.IsEditingName = false;
        RestoreRenameVisuals(item);
        if (!string.IsNullOrEmpty(draft) && nextName != item.DisplayName)
        {
            TrackPendingOperation(_launchers.RenameAsync(item.Id, nextName));
        }
    }

    private void CancelLauncherTileRename()
    {
        if (_launcherRenamingItem is not { } item)
        {
            return;
        }

        _launcherRenamingItem = null;
        item.IsEditingName = false;
        RestoreRenameVisuals(item);
    }

    private void RestoreRenameVisuals(LauncherItem item)
    {
        if (FindLauncherTile(item) is not { } tile)
        {
            return;
        }

        if (FindTemplatePart<TextBox>(tile, "TileNameEditor") is { } editor)
        {
            editor.SetCurrentValue(TextBox.VisibilityProperty, Visibility.Collapsed);
        }

        if (FindTemplatePart<TextBlock>(tile, "TileNameText") is { } nameText)
        {
            nameText.SetCurrentValue(VisibilityProperty, Visibility.Visible);
        }
    }

    private void LauncherTileNameEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox editor || _launcherRenamingItem is null)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_launcherRenameComposing)
            {
                // IME 组合期的 Esc=取消候选，属输入法操作，不触发改名取消。
                return;
            }

            e.Handled = true;
            CancelLauncherTileRename();
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (_launcherRenameComposing)
            {
                // IME 组合期的 Enter=确认候选，属输入法操作（与 Esc 分支及分类改名框
                // 同族守卫）：组合中的字串不得作为改名提交。
                return;
            }

            e.Handled = true;
            CommitLauncherTileRename();
            if (_launcherFocusedIndex >= 0 && _launcherFocusedIndex < _launchers.Items.Count)
            {
                FindLauncherTile(_launchers.Items[_launcherFocusedIndex])?.Focus();
            }
        }
    }

    private void LauncherTileNameEditor_LostFocus(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, Keyboard.FocusedElement) || _launcherRenamingItem is null)
        {
            return;
        }

        CommitLauncherTileRename();
    }

    // ---- 删除安全网（#30 §三.7；2026-10-18 扩展批量，Issue #186）：单条最新删除
    // 记录（单删）或整批记录（批删，按原下标逆序重插）+ 4s 撤销提示条事务；
    // 新删除替换旧记录（旧的不可再恢复，计时重置）。----

    private readonly DispatcherTimer _launcherUndoTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    /// <summary>撤销记录：单删=单条、批删=整批（#11 整批撤销在瓦片侧的承载）。</summary>
    private LauncherRemoveOutcome? _launcherUndoRecord;
    private IReadOnlyList<LauncherRemoveOutcome>? _launcherUndoBatch;

    private async Task DeleteLauncherItemAsync(LauncherItem item)
    {
        if (_launchers is null)
        {
            return;
        }

        if (_launcherRenamingItem?.Id == item.Id)
        {
            CancelLauncherTileRename();
        }

        var outcome = await _launchers.RemoveAsync(item.Id);
        if (outcome is null)
        {
            return;
        }

        await Dispatcher.InvokeAsync(() =>
        {
            // 新删除替换旧记录（旧的不可再恢复），计时重置 4s。
            _launcherUndoRecord = outcome;
            _launcherUndoBatch = null;
            LauncherUndoToastText.SetCurrentValue(
                TextBlock.TextProperty,
                $"已删除「{outcome.Item.DisplayName}」");
            FadeAnimation.SetIsActive(LauncherUndoToast, true);
            _launcherUndoTimer.Stop();
            _launcherUndoTimer.Start();
            if (_launcherFocusedIndex >= _launchers.Items.Count)
            {
                _launcherFocusedIndex = _launchers.Items.Count - 1;
            }

            ApplyLauncherFocusVisual();
        });
    }

    private async void LauncherUndoToast_Click(object sender, RoutedEventArgs e)
    {
        if (_launcherUndoBatch is { } batch)
        {
            await UndoLauncherBatchAsync(batch);
            return;
        }

        if (_launcherUndoRecord is not { } record)
        {
            return;
        }

        var restored = await _launchers.RestoreAsync(record.Item, Math.Min(record.Index, _launchers.Items.Count));
        if (!restored)
        {
            // 撤销保存失败：恢复「已删除」状态（记录保留）、提示条保留可重试至超时。
            return;
        }

        await Dispatcher.InvokeAsync(() =>
        {
            _launcherUndoRecord = null;
            _launcherUndoTimer.Stop();
            FadeAnimation.SetIsActive(LauncherUndoToast, false);
            AnimateLauncherShifts();
        });
    }

    /// <summary>整批撤销（T7 评审修订，Issue #186）：按**删除记录的逆序**逐条重插
    /// ——记录下标是删除时刻的快照（连续删除同槽位会重复），倒序重放即撤销栈语义，
    /// 相同下标的先删后恢复不会错序；min(原下标, 当前数) 与单条语义同源。恢复成功
    /// 的条目即从待恢复批次移除（部分保存失败后可重试剩余——已恢复条目不再阻塞）；
    /// 全部恢复完成才销毁提示条，失败保留剩余与提示条可重试。</summary>
    private async Task UndoLauncherBatchAsync(IReadOnlyList<LauncherRemoveOutcome> batch)
    {
        var pending = new List<LauncherRemoveOutcome>(batch);
        while (pending.Count > 0)
        {
            var record = pending[^1];
            var restored = await _launchers.RestoreAsync(record.Item, Math.Min(record.Index, _launchers.Items.Count));
            if (!restored)
            {
                // 保存失败：保留剩余（含本条）与提示条，可重试至超时。
                return;
            }

            pending.RemoveAt(pending.Count - 1);
            var snapshot = pending.ToArray();
            await Dispatcher.InvokeAsync(() => _launcherUndoBatch = snapshot);
        }

        await Dispatcher.InvokeAsync(() =>
        {
            _launcherUndoBatch = null;
            _launcherUndoRecord = null;
            _launcherUndoTimer.Stop();
            FadeAnimation.SetIsActive(LauncherUndoToast, false);
            AnimateLauncherShifts();
        });
    }

    /// <summary>提示条与记录随切标签/面板收起一起销毁。</summary>
    private void DestroyLauncherUndoToast()
    {
        _launcherUndoRecord = null;
        _launcherUndoBatch = null;
        _launcherUndoTimer.Stop();
        FadeAnimation.SetIsActive(LauncherUndoToast, false);
    }

    private void LauncherUndoTimer_Tick(object? sender, EventArgs e)
    {
        _launcherUndoTimer.Stop();
        FadeAnimation.SetIsActive(LauncherUndoToast, false);
        _launcherUndoRecord = null;
        _launcherUndoBatch = null;
    }

    private void LauncherTileDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LauncherItem item })
        {
            e.Handled = true;
            TrackPendingOperation(DeleteLauncherItemAsync(item));
        }
    }

    // ---- 瓦片右键上下文菜单（#30 修订，Issue #176；2026-10-18 终序，Issue #186）：
    // 打开/重命名/上移/下移四项复用既有事务与会话（删除/上移/下移走删除撤销事务
    // 与 MoveAsync；重命名直接进入既有改名会话——不再经编辑模式）；上移/下移＝
    // WCAG 2.5.7 拖动单指针替代（与契约 #21 标签顺序行同源）。菜单打开期间挂覆盖
    // 交互 Menu hold（不自动收起）；Esc 只关菜单（菜单是独立 HWND，键盘先归菜单）；
    // 右键不启动——启动判定要求左键 PointerUp 同瓦片 <4 DIP，结构上不可达。----

    private void LauncherTile_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Border { DataContext: LauncherItem item } tile ||
            tile.ContextMenu is not { } menu ||
            _launchers is null)
        {
            e.Handled = true;
            return;
        }

        if (_isClosing || _launchers.IsReadOnlyFromNewerVersion)
        {
            // 只读停用：一切写路径禁用（菜单是结构修改入口，整体不出）。
            e.Handled = true;
            return;
        }

        // Opening 时菜单模板尚未展开且 PlacementTarget 未赋值：菜单项经逻辑树
        //（Items）取，下标直接从宿主瓦片条目算。
        var index = IndexOfLauncherItem(item);
        if (LauncherMenuItem(menu, "TileMenuMoveUp") is { } up)
        {
            up.SetCurrentValue(IsEnabledProperty, index > 0);
        }

        if (LauncherMenuItem(menu, "TileMenuMoveDown") is { } down)
        {
            down.SetCurrentValue(IsEnabledProperty, index >= 0 && index < _launchers.Items.Count - 1);
        }
    }

    private static MenuItem? LauncherMenuItem(ContextMenu menu, string name) =>
        menu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Name == name);

    private LauncherItem? LauncherItemFromMenu(object? sender) =>
        (sender as FrameworkElement)?.Parent is ContextMenu { PlacementTarget: Border { DataContext: LauncherItem item } }
            ? item
            : null;

    private void LauncherTile_ContextMenuOpened(object? sender, EventArgs e) =>
        _panelState.BeginOverlayInteraction(PanelOverlayReason.Menu);

    private void LauncherTile_ContextMenuClosed(object? sender, EventArgs e) =>
        EndOverlayInteractionAndReconcile(PanelOverlayReason.Menu);

    private void LauncherTileMenuDelete_Click(object sender, RoutedEventArgs e)
    {
        if (LauncherItemFromMenu(sender) is { } item)
        {
            e.Handled = true;
            TrackPendingOperation(DeleteLauncherItemAsync(item));
        }
    }

    private void LauncherTileMenuMoveUp_Click(object sender, RoutedEventArgs e) =>
        MoveLauncherItemFromMenu(sender, e, -1);

    private void LauncherTileMenuMoveDown_Click(object sender, RoutedEventArgs e) =>
        MoveLauncherItemFromMenu(sender, e, +1);

    private void MoveLauncherItemFromMenu(object sender, RoutedEventArgs e, int delta)
    {
        if (LauncherItemFromMenu(sender) is not { } item || _launchers is null)
        {
            return;
        }

        e.Handled = true;
        // 相对移动（±1）：目标下标在目录门内的权威序上解析，菜单代码不计算
        // 绝对下标（乐观序上的门外下标会与在途回滚竞态）。
        var positionsBefore = CaptureLauncherTilePositions();
        TrackPendingOperation(AnimateAfterLauncherMutationAsync(
            _launchers.MoveByAsync(item.Id, delta),
            positionsBefore));
    }

    private void LauncherTileMenuRename_Click(object sender, RoutedEventArgs e)
    {
        if (LauncherItemFromMenu(sender) is not { } item)
        {
            return;
        }

        e.Handled = true;
        // 重命名直接进入既有改名会话（不另造第二套编辑器；编辑模式已移除，
        // 会话机器常态可用——挂起条件在 BeginLauncherTileRename 内统一承载）。
        BeginLauncherTileRename(item);
    }

    private void LauncherTileMenuOpen_Click(object sender, RoutedEventArgs e)
    {
        if (LauncherItemFromMenu(sender) is not { } item ||
            FindLauncherTile(item) is not { } tile ||
            IsLauncherLaunchLatched(item))
        {
            return;
        }

        e.Handled = true;
        LaunchLauncherItem(item, tile);
    }

    // ---- 编辑目标（#30 修订 2026-10-18，Issue #186 D7）：右键菜单「编辑目标…」/
    // Alt+Enter；对话框确认走目录 UpdateTargetAsync（既有突变协议：原子保存、失败
    // 还原+状态条；目标变更→IconStamp 置空→图标管线自动重取；用户名保留）；重复
    // 拒绝=状态条「已在启动列表中」（对话框不突变）。对话框打开挂覆盖交互
    // ModalDialog hold（与创建入口同一协议）。----

    /// <summary>编辑目标对话框显示（测试缝：同创建入口 _showUrlDialog 族）。</summary>
    internal Func<LauncherEditTargetWindow, bool?> _showEditTargetDialog = static dialog => dialog.ShowDialog();

    private void LauncherTileMenuEditTarget_Click(object sender, RoutedEventArgs e)
    {
        if (LauncherItemFromMenu(sender) is { } item)
        {
            e.Handled = true;
            BeginEditLauncherTarget(item);
        }
    }

    private void BeginEditLauncherTarget(LauncherItem item)
    {
        if (_isClosing ||
            _launchers is null ||
            _launchers.IsReadOnlyFromNewerVersion ||
            LauncherSelectedCount() > 0 ||
            _launcherDraggingId is not null)
        {
            return;
        }

        if (_launcherRenamingItem?.Id == item.Id)
        {
            CommitLauncherTileRename();
        }

        var dialog = new LauncherEditTargetWindow(CurrentTheme, item) { Owner = this };
        _panelState.BeginOverlayInteraction(PanelOverlayReason.ModalDialog);
        try
        {
            if (_showEditTargetDialog(dialog) != true || dialog.Result is not { } edited)
            {
                return;
            }

            TrackPendingOperation(ApplyLauncherTargetEditAsync(item.Id, edited));
        }
        finally
        {
            EndOverlayInteractionAndReconcile(PanelOverlayReason.ModalDialog);
        }
    }

    private async Task ApplyLauncherTargetEditAsync(Guid itemId, LauncherEditedTarget edited)
    {
        if (_launchers is null)
        {
            return;
        }

        var outcome = await _launchers.UpdateTargetAsync(
            itemId,
            edited.Kind,
            edited.TargetPath,
            edited.Arguments,
            edited.WorkingDirectory);
        await Dispatcher.InvokeAsync(() =>
        {
            switch (outcome)
            {
                case LauncherUpdateTargetOutcome.Updated:
                    ShowStatus("目标已更新。");
                    // 图标自动重取：IconKey 随目标变更（IconStamp=null 走提取路径）；
                    // 对在屏瓦片直发一次重取（对齐「应用未注册」重枚举先例）。
                    if (_launchers.Find(itemId) is { } updated)
                    {
                        RefreshLauncherIcon(updated);
                    }

                    break;
                case LauncherUpdateTargetOutcome.Duplicate:
                    ShowStatus("已在启动列表中");
                    break;
                default:
                    // 保存失败状态条由目录协议发出（「启动项未保存，已还原。」）。
                    break;
            }
        });
    }

    // ---- 拖拽重排（#30 修订 2026-10-18 起全时可用，Issue #186；取代「仅编辑态」）：
    // 阈值 4 DIP 不变（评审 #11 驳回项）；仅从启动区发起（名称带/勾选钮不发起）；
    // 选择非空或改名在途挂起。拾起=瓦片跟随指针；让位指示=目标瓦片 accent 描边；
    // 放置=单次 MoveAsync + 邻位让位/归位动画（变更前位置缓存 → 变更后位移回零
    // 167ms）。----

    private Guid? _launcherDraggingId;
    private System.Windows.Point _launcherDragStart;
    private int _launcherDropTargetIndex = -1;
    private Border? _launcherDropTargetTile;

    // 命中缓存（起拖一次解析）：被拖瓦片容器引用、接管基值、其余瓦片的**布局槽**
    // 矩形（LayoutInformation.GetLayoutSlot 经 LauncherTilePanel→窗口换算——不含任何
    // RenderTransform：链式拖拽时上一轮让位/归位动画的 FLIP 残差、悬停抬升都不
    // 会污染落点判定）。拖动期间不变量＝「布局不变」（捕获期间输入全归被拖瓦片、
    // 无重排）；滚动与集合变更（保存失败回滚在门内逆操作 Items）两类例外在
    // LauncherScroll_ScrollChanged / OnLauncherItemsChanged 里重建缓存；缓存随会话
    // 边界（EndLauncherTileDrag）失效。
    private Border? _launcherDraggingTile;
    private System.Windows.Point _launcherDragTranslateOrigin;
    private List<(int ItemsIndex, Guid Id, Border Tile, Rect WindowRect)>? _launcherDragHitSlots;

    private void LauncherTile_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Border { DataContext: LauncherItem pressed } tile ||
            _launcherPressItemId != pressed.Id ||
            _launcherRenamingItem is not null ||
            LauncherSelectedCount() > 0)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (_launcherDraggingId is null)
        {
            // 全时拖拽的发起约束：仅名称带/勾选钮不发起（未标记瓦片区按「未遇到
            // 标记归启动区」语义可拖——贴边环带承载拖拽，与卡片贴边语义同构）。
            if (_launcherPressZone == CardGestureZones.LauncherNameZone)
            {
                return;
            }

            var displacement = position - _launcherPressStartPosition;
            if (Math.Abs(displacement.X) < 4 && Math.Abs(displacement.Y) < 4)
            {
                return;
            }

            _launcherDraggingId = pressed.Id;
            _launcherDragStart = position;
            tile.CaptureMouse();
            AnimateLauncherPressScale(tile, pressed: false);
            BuildLauncherDragHitCache(pressed);
        }

        if (_launcherDraggingTile is { } dragged)
        {
            // 跟随＝输入的直接反映（#30 本义）：动效开/关两档统一为直接赋值。
            // 起拖时按「读当前值→清动画→设基值」接管进行中的动画（——动画期间
            // SetCurrentValue 不生效：依赖属性优先级动画>本地值），随后每步把累计
            // 位移加到接管基值上；167ms 动画只承担归位/让位这类有限时长的状态
            // 过渡，绝不追赶指针（追赶=恒定空间滞后=粘滞感）。每步先清一次动画
            // 是捕获期悬停重入的自愈防线（捕获下 MouseLeave 仍可能投递，悬停动画
            // 一旦重启会压过 SetCurrentValue）。
            var translate = LauncherTileTranslate(dragged);
            translate.BeginAnimation(TranslateTransform.XProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            var delta = position - _launcherDragStart;
            translate.SetCurrentValue(
                TranslateTransform.XProperty,
                _launcherDragTranslateOrigin.X + delta.X);
            translate.SetCurrentValue(
                TranslateTransform.YProperty,
                _launcherDragTranslateOrigin.Y + delta.Y);
        }

        UpdateLauncherDropTarget(position);
    }

    /// <summary>起拖一次解析命中缓存：被拖瓦片容器＋接管基值（取悬停静止值而非
    /// 抬升动画的瞬时值——放下后不重入悬停也能归位），其余瓦片的布局槽矩形。</summary>
    private void BuildLauncherDragHitCache(LauncherItem draggedItem)
    {
        _launcherDraggingTile = FindLauncherTile(draggedItem);
        if (_launcherDraggingTile is { } dragged)
        {
            var translate = LauncherTileTranslate(dragged);
            translate.BeginAnimation(TranslateTransform.XProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            // 悬停静止基值：IsMouseOver ? -2 : 0（抬升动画途中起拖不冻结瞬时值）。
            var baseX = 0d;
            var baseY = dragged.IsMouseOver ? DesignTokens.TileHoverLiftPx : 0d;
            translate.SetCurrentValue(TranslateTransform.XProperty, baseX);
            translate.SetCurrentValue(TranslateTransform.YProperty, baseY);
            _launcherDragTranslateOrigin = new System.Windows.Point(baseX, baseY);
        }

        RefreshLauncherDragHitSlots(draggedItem.Id);
    }

    /// <summary>按当前集合序重建落点命中槽（Items 下标＋布局槽→窗口矩形）。
    /// 拖动期间的两类例外（滚动改变窗口坐标、集合变更改变下标与布局——保存失败
    /// 回滚的逆操作 Move）都经此重建；仍是单遍 O(n)。</summary>
    private void RefreshLauncherDragHitSlots(Guid draggedId)
    {
        var panel = LauncherTilePanelRoot;
        if (panel is null)
        {
            _launcherDragHitSlots = [];
            return;
        }

        // 布局槽→窗口的换算用面板级变换一次（含滚动偏移；不含各瓦片 RenderTransform）。
        var panelToWindow = panel.TransformToAncestor(this);
        var generator = LauncherTileGrid.ItemContainerGenerator;
        var slots = new List<(int, Guid, Border, Rect)>(_launchers.Items.Count);
        var index = 0;
        foreach (var item in _launchers.Items)
        {
            if (item.Id != draggedId)
            {
                if (generator.ContainerFromIndex(index) is { } container &&
                    ((container as Border) ?? Descendants<Border>(container).FirstOrDefault()) is { } candidate)
                {
                    var slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(
                        (FrameworkElement)container);
                    var origin = panelToWindow.Transform(new System.Windows.Point(slot.X, slot.Y));
                    slots.Add((index, item.Id, candidate, new Rect(
                        origin,
                        new Size(slot.Width, slot.Height))));
                }
            }

            index++;
        }

        _launcherDragHitSlots = slots;
    }

    private void UpdateLauncherDropTarget(System.Windows.Point windowPosition)
    {
        if (_launcherDraggingId is null || _launcherDragHitSlots is not { } slots)
        {
            return;
        }

        Border? target = null;
        var targetIndex = -1;
        foreach (var slot in slots)
        {
            if (slot.WindowRect.Contains(windowPosition))
            {
                target = slot.Tile;
                targetIndex = slot.ItemsIndex;
                break;
            }
        }

        if (!ReferenceEquals(_launcherDropTargetTile, target))
        {
            _launcherDropTargetTile?.SetCurrentValue(Border.BorderBrushProperty, Brushes.Transparent);
            _launcherDropTargetTile = target;
            target?.SetCurrentValue(Border.BorderBrushProperty, (Brush)FindResource("AccentBrush"));
        }

        _launcherDropTargetIndex = targetIndex;
    }

    /// <summary>结束拖拽（放置=提交重排并播放邻位动画；取消=归位动画）；命中缓存
    /// 随会话边界失效。</summary>
    private void EndLauncherTileDrag(bool apply)
    {
        ClearLauncherDragHitCache();
        if (_launcherDraggingId is not { } draggingId)
        {
            _launcherDropTargetTile?.SetCurrentValue(Border.BorderBrushProperty, Brushes.Transparent);
            _launcherDropTargetTile = null;
            _launcherDropTargetIndex = -1;
            return;
        }

        _launcherDraggingId = null;
        _launcherDropTargetTile?.SetCurrentValue(Border.BorderBrushProperty, Brushes.Transparent);
        _launcherDropTargetTile = null;
        var targetIndex = _launcherDropTargetIndex;
        _launcherDropTargetIndex = -1;
        if (apply && targetIndex >= 0 && _launchers?.Find(draggingId) is { } item)
        {
            // 位置快照在突变调用之前取（乐观发布时 MoveAsync 返回即已应用，事后
            // 取会拍到新位）。
            var positionsBefore = CaptureLauncherTilePositions();
            var move = _launchers.MoveAsync(item.Id, targetIndex);
            TrackPendingOperation(AnimateAfterLauncherMutationAsync(move, positionsBefore));
        }
        else if (_launchers?.Find(draggingId) is { } returning)
        {
            ResetLauncherTileTransforms(returning);
            // 归位后按指针在场与否重放悬停评估：拖动期间接管基值取的是悬停静止
            // 值，指针未离开时归位到 0 会让瓦片比同排悬停瓦片低一截，直到下一次
            // 进入/离开才纠正——显式重评估把悬停态即时对齐。
            if (FindLauncherTile(returning) is { } tile)
            {
                AnimateLauncherHoverLift(tile, lifted: tile.IsMouseOver);
            }
        }
    }

    private async Task AnimateAfterLauncherMutationAsync(
        Task<bool> mutation,
        Dictionary<Guid, System.Windows.Point>? positionsBefore)
    {
        var changed = await mutation;
        await Dispatcher.InvokeAsync(() =>
        {
            if (changed)
            {
                AnimateLauncherShifts(positionsBefore);
            }
            else if (_launcherDraggingId is null)
            {
                // 保存失败：内存已还原——重置全部瓦片变换（归位）。
                foreach (var item in _launchers.Items)
                {
                    ResetLauncherTileTransforms(item);
                }
            }
        });
    }

    /// <summary>单遍容器快照（条目, 瓦片）：容器解析一次完成，供位置捕获/让位
    /// 动画/编辑态视觉共用（每条目逐次「IndexOf＋ContainerFromIndex＋树遍历」
    /// 的 O(n)² 模式收敛为单遍）。装载竞态下目录可能尚未就绪（旧代码同样容忍
    /// 空目录），空安全。</summary>
    private List<(LauncherItem Item, Border Tile)> SnapshotLauncherTiles()
    {
        var items = _launchers?.Items;
        if (items is null || items.Count == 0)
        {
            return [];
        }

        var snapshots = new List<(LauncherItem, Border)>(items.Count);
        var generator = LauncherTileGrid.ItemContainerGenerator;
        for (var index = 0; index < items.Count; index++)
        {
            if (generator.ContainerFromIndex(index) is { } container &&
                ((container as Border) ?? Descendants<Border>(container).FirstOrDefault()) is { } tile)
            {
                snapshots.Add((items[index], tile));
            }
        }

        return snapshots;
    }

    private Dictionary<Guid, System.Windows.Point> CaptureLauncherTilePositions()
    {
        var positions = new Dictionary<Guid, System.Windows.Point>();
        foreach (var (item, tile) in SnapshotLauncherTiles())
        {
            positions[item.Id] = tile.TranslatePoint(new System.Windows.Point(), this);
        }

        return positions;
    }

    /// <summary>邻位让位/归位动画：重排（或撤销重插）后，位置变化的瓦片从旧位置
    /// 位移回零 167ms（自身跟随指针的平移同时清零=归位）。FLIP 型：数据 Id 为
    /// 键、RenderTransform 逆偏移回零；容器经单遍快照解析。</summary>
    private void AnimateLauncherShifts(Dictionary<Guid, System.Windows.Point>? positionsBefore = null)
    {
        if (!ClientAreaAnimationsEnabled)
        {
            foreach (var (_, tile) in SnapshotLauncherTiles())
            {
                ResetLauncherTileTransforms(tile, immediate: true);
            }

            return;
        }

        foreach (var (item, tile) in SnapshotLauncherTiles())
        {
            var previous = positionsBefore?.GetValueOrDefault(item.Id);
            if (previous is { } origin)
            {
                var current = tile.TranslatePoint(new System.Windows.Point(), this);
                var deltaX = origin.X - current.X;
                var deltaY = origin.Y - current.Y;
                if (Math.Abs(deltaX) < 1 && Math.Abs(deltaY) < 1)
                {
                    ResetLauncherTileTransforms(tile, immediate: true);
                    continue;
                }

                var translate = LauncherTileTranslate(tile);
                translate.SetCurrentValue(TranslateTransform.XProperty, deltaX);
                translate.SetCurrentValue(TranslateTransform.YProperty, deltaY);
                translate.BeginAnimation(
                    TranslateTransform.XProperty,
                    new DoubleAnimation(0, TimeSpan.FromMilliseconds(DesignTokens.HoverFeedbackMs)));
                translate.BeginAnimation(
                    TranslateTransform.YProperty,
                    new DoubleAnimation(0, TimeSpan.FromMilliseconds(DesignTokens.HoverFeedbackMs)));
            }
            else
            {
                ResetLauncherTileTransforms(tile, immediate: true);
            }
        }
    }

    private void ResetLauncherTileTransforms(LauncherItem item, bool immediate = false)
    {
        if (FindLauncherTile(item) is { } tile)
        {
            ResetLauncherTileTransforms(tile, immediate);
        }
    }

    private void ResetLauncherTileTransforms(Border tile, bool immediate = false)
    {
        var translate = LauncherTileTranslate(tile);
        if (immediate || !ClientAreaAnimationsEnabled)
        {
            translate.BeginAnimation(TranslateTransform.XProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.SetCurrentValue(TranslateTransform.XProperty, 0d);
            translate.SetCurrentValue(TranslateTransform.YProperty, 0d);
            return;
        }

        translate.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(DesignTokens.HoverFeedbackMs)));
        translate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(DesignTokens.HoverFeedbackMs)));
    }

    private void LauncherTile_GiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        e.Handled = true;
    }

    private void LauncherTile_QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        e.Handled = true;
    }

    private void DisposeQuickLaunch()
    {
        _launchCollapseTimer.Stop();
        _reexpandSuppressionPollTimer.Stop();
        _launcherIcons?.Dispose();
        _launcherIcons = null;
    }
}
