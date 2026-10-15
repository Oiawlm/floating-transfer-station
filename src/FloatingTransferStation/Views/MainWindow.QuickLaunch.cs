using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
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
    private int _launcherFocusedIndex = -1;
    private bool _launcherDialogOpen;
    private int _expandSessionId;
    private int _launchCollapseSession;
    private bool _suppressReexpandAfterLaunch;
    private NativeRect _reexpandSuppressionRect;
    private bool _launcherEntrancePlayed;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly bool Contains(NativePoint point) =>
            point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;
    }

    private void InitializeQuickLaunch(ILauncherStore? launcherStore, AppLauncher? appLauncher)
    {
        _appLauncher = appLauncher ?? new AppLauncher(packagedActivator: new ShellPackagedAppActivator().Activate);
        _launchCollapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DesignTokens.LaunchCollapseDelayMs) };
        _launchCollapseTimer.Tick += LaunchCollapseTimer_Tick;
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
        TrackPendingOperation(LoadLaunchersAsync(launcherStore));
    }

    private async Task LoadLaunchersAsync(ILauncherStore? launcherStore)
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
            _launcherIcons = LauncherIconService.Start(_launchers, LauncherIconsDirectory(), Dispatcher);
            _launcherIcons.SweepOrphansOnce();
        });
    }

    private string LauncherIconsDirectory() =>
        Path.Combine(
            string.IsNullOrWhiteSpace(_dataDirectory) ? Path.GetTempPath() : _dataDirectory,
            "launcher-icons");

    internal LauncherCatalog LaunchersForTests() => _launchers;

    private bool IsLauncherActive() =>
        _viewModel.ActivePanel?.Category == QuickLaunchMigration.LauncherCategory;

    private bool IsLauncherSurfaceReady() => _launchers is not null;

    private void OnLauncherItemsChanged()
    {
        UpdateLauncherSurfaceState();
        // 目录权威计数（_items 在发布前置位）：PublishInline 的 Clear→Add 会让
        // CollectionChanged 在瞬时空集上触发，不得据此判「删空」。
        if (_launcherEditMode && _launchers.Count == 0)
        {
            // 删空最后瓦片：编辑模式无对象，自动退出（空态工具行编辑钮回到禁用）。
            ExitLauncherEditMode();
        }

        if (_launcherEditMode)
        {
            UpdateLauncherEditVisuals();
        }
    }

    /// <summary>空态/网格切换与工具行状态（编辑钮无项目禁用 40% 透明）。全部读目录
    /// 权威计数——PublishInline 的 Clear→Add 瞬态会让 Items.Count 闪 0（若据此钳制
    /// 焦点索引会被毒化为 -1）。</summary>
    private void UpdateLauncherSurfaceState()
    {
        if (!IsLauncherSurfaceReady())
        {
            return;
        }

        var isEmpty = _launchers.Count == 0;
        LauncherEmptyState.SetCurrentValue(VisibilityProperty, isEmpty ? Visibility.Visible : Visibility.Collapsed);
        LauncherScroll.SetCurrentValue(VisibilityProperty, isEmpty ? Visibility.Collapsed : Visibility.Visible);
        LauncherEditButton.SetCurrentValue(IsEnabledProperty, !isEmpty && !_launchers.IsReadOnlyFromNewerVersion);
        LauncherEditButton.SetCurrentValue(OpacityProperty, isEmpty ? 0.4 : 1.0);
        if (_launcherFocusedIndex >= _launchers.Count)
        {
            _launcherFocusedIndex = _launchers.Count - 1;
            ApplyLauncherFocusVisual();
        }
    }

    // ---- 添加（＋ 文件选择器，切片 2 单入口；已安装应用选择器切片 4 追加为两选项菜单）----

    private void LauncherAddButton_Click(object sender, RoutedEventArgs e)
    {
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
        _launcherDialogOpen = true;
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
            _launcherDialogOpen = false;
        }
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

        if (outcome.AddedCount == 0 && skippedTotal == 0 && outcome.OverflowSkippedCount == 0)
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

    // ---- 瓦片交互：单击启动（按下/抬起同瓦片、位移 <4 DIP、500ms 重入门闩）----

    private void LauncherTile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: LauncherItem item } tile)
        {
            return;
        }

        _launcherPressItemId = item.Id;
        _launcherPressStartPosition = e.GetPosition(this);
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
        _launcherPressItemId = null;
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
        _launcherFocusedIndex = IndexOfLauncherItem(item);
        ApplyLauncherFocusVisual();
        if (_launcherEditMode)
        {
            // 编辑态单击=就地改名（不启动）。
            BeginLauncherTileRename(item);
            return;
        }

        if (IsLauncherLaunchLatched(item))
        {
            return;
        }

        LaunchLauncherItem(item, tile);
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
                    // 应用未注册（0x80270254/0x80070490）：已安装应用重枚举在切片 4 接线。
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
            _launcherDialogOpen ||
            IsLauncherEditModeActive())
        {
            // 任一抑制条件在等待期出现即取消本次收起（失败启动在 LaunchLauncherItem
            // 内已直接停表＝更新的失败取消收起保展开反馈）。
            return;
        }

        RecordReexpandSuppression();
        // 强制提交：指针在内不是启动收起的抑制项（再展开抑制窗另行承载），而
        // PanelStateMachine.TryCollapse 会在指针在内时拒绝——此处跳过该检查直接落收起。
        _panelCollapseExitPending = false;
        StopCategoryRevealAnimations();
        _panelState.ForceCollapse();
        CompletePanelCollapseHandoff();
    }

    /// <summary>收起态落地共用尾段（入场重播标记重置 + 几何交接）；面板收起自动退出
    /// 编辑模式并销毁撤销提示条（#30 §四/§三.7）。</summary>
    private void CompletePanelCollapseHandoff()
    {
        ExitLauncherEditMode();
        DestroyLauncherUndoToast();
        ResetLauncherEntranceReplay();
        BeginCollapsedVisualHandoff(WindowController.Collapsed(
            CurrentWorkArea(),
            _settings,
            _viewModel.DefaultCapturePanel.Category));
    }

    /// <summary>再展开抑制窗：解除条件唯一=指针离开收起前面板原区域后重新进入
    /// （不做计时解除；指针停留期间保持抑制）。</summary>
    private void RecordReexpandSuppression()
    {
        _suppressReexpandAfterLaunch = true;
        if (_windowSource?.Handle is { } handle && GetWindowRect(handle, out var rect))
        {
            _reexpandSuppressionRect = rect;
        }
    }

    internal bool TryConsumeReexpandSuppression()
    {
        if (!_suppressReexpandAfterLaunch)
        {
            return false;
        }

        if (!GetCursorPos(out var cursor))
        {
            return true;
        }

        if (_reexpandSuppressionRect.Contains(cursor))
        {
            // 指针仍停留在收起前面板原区域内：保持抑制。
            return true;
        }

        _suppressReexpandAfterLaunch = false;
        return false;
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

    /// <summary>点空白退出编辑模式（#30 §四）：命中瓦片/工具行/提示条/滚动区内容的
    /// 按下不在此列（由各自控件消费），命中表面背景即退出。</summary>
    private void LauncherSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_launcherEditMode)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source &&
            (IsWithinLauncherTile(source) || source is ButtonBase or TextBox or ScrollBar))
        {
            return;
        }

        ExitLauncherEditMode();
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

    // ---- 瓦片网格几何：列数 clamp(floor((Wc+8)/92), 3, 6)，网格水平居中 ----

    private void LauncherTileGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 列数 clamp(floor((Wc+8)/92), 3, 6)（Wc=布局实测可用宽）；网格水平居中由
        // 限定宽度 + HorizontalAlignment=Center 承载。200 上限承载有界性（不预建虚拟化）。
        var tilePitch = 84 + 8;
        var availableWidth = Math.Max(0, e.NewSize.Width);
        var columns = Math.Clamp(
            (int)Math.Floor((availableWidth + 8) / tilePitch),
            3,
            6);
        LauncherTileGrid.Width = Math.Min(availableWidth, columns * tilePitch - 8);
    }

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

    // ---- 键盘层级（§一）：①改名编辑器原生 ②编辑态（切片 3）③查看态（Enter/Space 启动、
    // 方向键导航）④窗口级命令照复盘现状。每层消费后标记已处理。----

    /// <summary>返回 true=按键已被启动表面消费（调用方据此短路既有窗口级处理）。</summary>
    private bool HandleLauncherPreviewKey(KeyEventArgs e)
    {
        if (!IsLauncherActive() || _launchers is null || _launchers.Items.Count == 0)
        {
            if (IsLauncherActive() && e.Key is Key.Delete or Key.Enter or Key.Space)
            {
                // 空列表：启动表面消费启动/删除键（查看态 Delete 无操作）。
                return e.Key is Key.Enter or Key.Space or Key.Delete;
            }

            return false;
        }

        if (Keyboard.FocusedElement is TextBoxBase)
        {
            // 层级①：瓦片改名编辑器原生文本语义（IME 守卫照 #7/#22）。
            return false;
        }

        if (IsLauncherEditModeActive())
        {
            return HandleLauncherEditKey(e);
        }

        switch (e.Key)
        {
            case Key.Enter:
            case Key.Space:
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
                // 查看态 Delete 无操作（结构性排除，键盘层级③）。
                return true;
            case Key.F2:
                // 查看态 F2=进入编辑模式（#30 §四；优先于窗口级标签改名 F2）。
                EnterLauncherEditMode();
                return true;
            default:
                return false;
        }
    }

    private bool HandleLauncherEditKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Delete:
                if (_launcherFocusedIndex >= 0 && _launcherFocusedIndex < _launchers.Items.Count)
                {
                    TrackPendingOperation(DeleteLauncherItemAsync(_launchers.Items[_launcherFocusedIndex]));
                }

                return true;
            case Key.F2:
            case Key.Enter:
                if (_launcherFocusedIndex >= 0 && _launcherFocusedIndex < _launchers.Items.Count)
                {
                    BeginLauncherTileRename(_launchers.Items[_launcherFocusedIndex]);
                }

                return true;
            case Key.Escape:
                ExitLauncherEditMode();
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
            default:
                return false;
        }
    }

    private bool _launcherEditMode;

    private bool IsLauncherEditModeActive() => _launcherEditMode;

    private void MoveLauncherFocus(int deltaColumns, int deltaRows)
    {
        var columns = Math.Max(1, (int)Math.Floor((LauncherTileGrid.ActualWidth + 8) / 92));
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
        if (_launcherFocusedIndex < 0 || _launchers is null || _launcherFocusedIndex >= _launchers.Items.Count)
        {
            return;
        }

        if (FindLauncherTile(_launchers.Items[_launcherFocusedIndex]) is { } focused)
        {
            _launcherTileContainers[_launchers.Items[_launcherFocusedIndex].Id] = focused;
            focused.SetCurrentValue(Border.BorderBrushProperty, (Brush)FindResource("AccentBrush"));
            focused.BringIntoView();
        }
    }

    // ---- 编辑模式（#30 §四）：进入=工具行按钮 / F2；退出=完成钮 / Esc / 点空白 /
    // 切标签自动退出 / 面板收起自动退出。编辑态：瓦片常驻浅底 + 右上 × 删除钮（常驻）+
    // 单击=就地改名 + 拖拽重排（阈值 4 DIP）+ Delete 删焦点瓦片 + 方向键导航。----

    private void LauncherEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (_launcherEditMode)
        {
            ExitLauncherEditMode();
        }
        else
        {
            EnterLauncherEditMode();
        }
    }

    private void EnterLauncherEditMode()
    {
        if (_isClosing || _launchers is null || _launchers.Items.Count == 0 || _launcherEditMode)
        {
            return;
        }

        _launcherEditMode = true;
        LauncherEditButton.SetCurrentValue(ContentControl.ContentProperty, "完成");
        LauncherEditHint.SetCurrentValue(VisibilityProperty, Visibility.Visible);
        if (_launcherFocusedIndex < 0)
        {
            _launcherFocusedIndex = 0;
        }

        ApplyLauncherFocusVisual();
        UpdateLauncherEditVisuals();
    }

    private void ExitLauncherEditMode()
    {
        if (!_launcherEditMode)
        {
            return;
        }

        CommitLauncherTileRename();
        _launcherEditMode = false;
        LauncherEditButton.SetCurrentValue(ContentControl.ContentProperty, "编辑");
        LauncherEditHint.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
        EndLauncherTileDrag(apply: false);
        UpdateLauncherEditVisuals();
    }

    /// <summary>编辑态瓦片视觉：常驻浅底（Opacity 1/0）与 × 删除钮可见性（83ms 淡入
    /// 由 FadeAnimation 承载；多选 40ms 交错的视觉近似为同帧淡入——重排动画另有专门路径）。</summary>
    private void UpdateLauncherEditVisuals()
    {
        foreach (var item in _launchers?.Items ?? [])
        {
            if (FindLauncherTile(item) is not { } tile)
            {
                continue;
            }

            if (FindTemplatePart<Border>(tile, "TileEditLayer") is { } editLayer)
            {
                editLayer.SetCurrentValue(UIElement.OpacityProperty, _launcherEditMode ? 1d : 0d);
            }

            if (FindTemplatePart<Button>(tile, "TileDeleteButton") is { } deleteButton)
            {
                FadeAnimation.SetIsActive(deleteButton, _launcherEditMode);
            }
        }
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

    // ---- 就地改名（编辑态单击/F2/Enter）：预填全选、Enter 提交/Esc 取消/失焦提交、
    // 空提交=回退原名、IME 组合守卫（组合期的 Esc 属输入法操作不触发取消）。----

    private LauncherItem? _launcherRenamingItem;
    private bool _launcherRenameComposing;

    private void BeginLauncherTileRename(LauncherItem item)
    {
        if (!_launcherEditMode)
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

    // ---- 删除安全网（#30 §三.7）：单条最新删除记录 + 4s 撤销提示条事务。----

    private readonly DispatcherTimer _launcherUndoTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private LauncherRemoveOutcome? _launcherUndoRecord;

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

    /// <summary>提示条与记录随切标签/面板收起一起销毁。</summary>
    private void DestroyLauncherUndoToast()
    {
        _launcherUndoRecord = null;
        _launcherUndoTimer.Stop();
        FadeAnimation.SetIsActive(LauncherUndoToast, false);
    }

    private void LauncherUndoTimer_Tick(object? sender, EventArgs e)
    {
        _launcherUndoTimer.Stop();
        FadeAnimation.SetIsActive(LauncherUndoToast, false);
        _launcherUndoRecord = null;
    }

    private void LauncherTileDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LauncherItem item })
        {
            e.Handled = true;
            TrackPendingOperation(DeleteLauncherItemAsync(item));
        }
    }

    // ---- 拖拽重排（编辑态；阈值 4 DIP；查看态拖拽=无操作）：
    // 拾起=瓦片跟随指针；让位指示=目标瓦片 accent 描边；放置=单次 MoveAsync +
    // 邻位让位/归位动画（变更前位置缓存 → 变更后位移回零 167ms）。----

    private Guid? _launcherDraggingId;
    private System.Windows.Point _launcherDragStart;
    private int _launcherDropTargetIndex = -1;
    private Border? _launcherDropTargetTile;

    private void LauncherTile_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_launcherEditMode ||
            sender is not Border { DataContext: LauncherItem pressed } tile ||
            _launcherPressItemId != pressed.Id ||
            _launcherRenamingItem is not null)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (_launcherDraggingId is null)
        {
            var displacement = position - _launcherPressStartPosition;
            if (Math.Abs(displacement.X) < 4 && Math.Abs(displacement.Y) < 4)
            {
                return;
            }

            _launcherDraggingId = pressed.Id;
            _launcherDragStart = position;
            tile.CaptureMouse();
            AnimateLauncherPressScale(tile, pressed: false);
        }

        if (FindLauncherTile(pressed) is { } dragged)
        {
            var translate = LauncherTileTranslate(dragged);
            var delta = position - _launcherDragStart;
            if (!ClientAreaAnimationsEnabled)
            {
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                translate.SetCurrentValue(TranslateTransform.YProperty, delta.Y);
                translate.BeginAnimation(TranslateTransform.XProperty, null);
                translate.SetCurrentValue(TranslateTransform.XProperty, delta.X);
            }
            else
            {
                translate.BeginAnimation(
                    TranslateTransform.XProperty,
                    new DoubleAnimation(delta.X, TimeSpan.FromMilliseconds(DesignTokens.HoverFeedbackMs)));
                translate.BeginAnimation(
                    TranslateTransform.YProperty,
                    new DoubleAnimation(delta.Y, TimeSpan.FromMilliseconds(DesignTokens.HoverFeedbackMs)));
            }
        }

        UpdateLauncherDropTarget(position);
    }

    private void UpdateLauncherDropTarget(System.Windows.Point windowPosition)
    {
        if (_launcherDraggingId is not { } draggingId)
        {
            return;
        }

        Border? target = null;
        var targetIndex = -1;
        var index = 0;
        foreach (var item in _launchers.Items)
        {
            if (item.Id == draggingId)
            {
                index++;
                continue;
            }

            if (FindLauncherTile(item) is { } candidate &&
                IsPositionOverTile(candidate, windowPosition))
            {
                target = candidate;
                targetIndex = index;
                break;
            }

            index++;
        }

        if (!ReferenceEquals(_launcherDropTargetTile, target))
        {
            _launcherDropTargetTile?.SetCurrentValue(Border.BorderBrushProperty, Brushes.Transparent);
            _launcherDropTargetTile = target;
            target?.SetCurrentValue(Border.BorderBrushProperty, (Brush)FindResource("AccentBrush"));
        }

        _launcherDropTargetIndex = targetIndex;
    }

    private bool IsPositionOverTile(Border tile, System.Windows.Point windowPosition)
    {
        var position = tile.TransformToAncestor(this).Transform(new System.Windows.Point());
        return windowPosition.X >= position.X && windowPosition.X < position.X + tile.ActualWidth &&
            windowPosition.Y >= position.Y && windowPosition.Y < position.Y + tile.ActualHeight;
    }

    /// <summary>结束拖拽（放置=提交重排并播放邻位动画；取消=归位动画）。</summary>
    private void EndLauncherTileDrag(bool apply)
    {
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
            var move = _launchers.MoveAsync(item.Id, targetIndex);
            TrackPendingOperation(AnimateAfterLauncherMutationAsync(move));
        }
        else if (_launchers?.Find(draggingId) is { } returning)
        {
            ResetLauncherTileTransforms(returning);
        }
    }

    private async Task AnimateAfterLauncherMutationAsync(Task<bool> mutation)
    {
        var positionsBefore = CaptureLauncherTilePositions();
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

    private Dictionary<Guid, System.Windows.Point> CaptureLauncherTilePositions()
    {
        var positions = new Dictionary<Guid, System.Windows.Point>();
        foreach (var item in _launchers?.Items ?? [])
        {
            if (FindLauncherTile(item) is { } tile)
            {
                positions[item.Id] = tile.TranslatePoint(new System.Windows.Point(), this);
            }
        }

        return positions;
    }

    /// <summary>邻位让位/归位动画：重排（或撤销重插）后，位置变化的瓦片从旧位置
    /// 位移回零 167ms（自身跟随指针的平移同时清零=归位）。</summary>
    private void AnimateLauncherShifts(Dictionary<Guid, System.Windows.Point>? positionsBefore = null)
    {
        if (!ClientAreaAnimationsEnabled)
        {
            foreach (var item in _launchers?.Items ?? [])
            {
                ResetLauncherTileTransforms(item, immediate: true);
            }

            return;
        }

        foreach (var item in _launchers?.Items ?? [])
        {
            if (FindLauncherTile(item) is not { } tile)
            {
                continue;
            }

            var previous = positionsBefore?.GetValueOrDefault(item.Id);
            if (previous is { } origin)
            {
                var current = tile.TranslatePoint(new System.Windows.Point(), this);
                var deltaX = origin.X - current.X;
                var deltaY = origin.Y - current.Y;
                if (Math.Abs(deltaX) < 1 && Math.Abs(deltaY) < 1)
                {
                    ResetLauncherTileTransforms(item, immediate: true);
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
                ResetLauncherTileTransforms(item, immediate: true);
            }
        }
    }

    private void ResetLauncherTileTransforms(LauncherItem item, bool immediate = false)
    {
        if (FindLauncherTile(item) is not { } tile)
        {
            return;
        }

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
        _launcherIcons?.Dispose();
        _launcherIcons = null;
    }
}
