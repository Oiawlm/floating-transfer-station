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
    }

    /// <summary>空态/网格切换与工具行状态（编辑钮无项目禁用 40% 透明，切片 3 启用编辑）。</summary>
    private void UpdateLauncherSurfaceState()
    {
        if (!IsLauncherSurfaceReady())
        {
            return;
        }

        var isEmpty = _launchers.Items.Count == 0;
        LauncherEmptyState.SetCurrentValue(VisibilityProperty, isEmpty ? Visibility.Visible : Visibility.Collapsed);
        LauncherScroll.SetCurrentValue(VisibilityProperty, isEmpty ? Visibility.Collapsed : Visibility.Visible);
        LauncherEditButton.SetCurrentValue(IsEnabledProperty, !isEmpty && !_launchers.IsReadOnlyFromNewerVersion);
        LauncherEditButton.SetCurrentValue(OpacityProperty, isEmpty ? 0.4 : 1.0);
        if (_launcherFocusedIndex >= _launchers.Items.Count)
        {
            _launcherFocusedIndex = _launchers.Items.Count - 1;
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

    /// <summary>收起态落地共用尾段（入场重播标记重置 + 几何交接）。</summary>
    private void CompletePanelCollapseHandoff()
    {
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
            default:
                return false;
        }
    }

    private bool HandleLauncherEditKey(KeyEventArgs e) => false; // 切片 3：编辑态键盘层级。

    private bool IsLauncherEditModeActive() => false; // 切片 3：编辑模式状态。

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

    private void LauncherEditButton_Click(object sender, RoutedEventArgs e)
    {
        // 切片 3：进入编辑模式（改名/删除/重排/键盘层级②）。
    }

    private void DisposeQuickLaunch()
    {
        _launchCollapseTimer.Stop();
        _launcherIcons?.Dispose();
        _launcherIcons = null;
    }
}
