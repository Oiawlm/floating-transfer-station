using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FloatingTransferStation.Design;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;

namespace FloatingTransferStation.Views;

public partial class MainWindow : Window
{
    public static readonly DependencyProperty ClientAreaAnimationsEnabledProperty =
        DependencyProperty.Register(
            nameof(ClientAreaAnimationsEnabled),
            typeof(bool),
            typeof(MainWindow),
            new FrameworkPropertyMetadata(true, OnClientAreaAnimationsEnabledChanged));

    private static readonly TimeSpan ExpandContentAnimationDuration =
        TimeSpan.FromMilliseconds(DesignTokens.PanelExpandContentMs);
    private static readonly TimeSpan SwitchContentAnimationDuration =
        TimeSpan.FromMilliseconds(DesignTokens.PanelSwitchContentMs);
    private static readonly TimeSpan ReducedMotionContentAnimationDuration =
        TimeSpan.FromMilliseconds(DesignTokens.ReducedMotionFadeMs);
    private static readonly TimeSpan CategoryRevealAnimationDuration =
        TimeSpan.FromMilliseconds(DesignTokens.CategoryRevealMs);
    private static readonly TimeSpan PanelCollapseExitAnimationDuration =
        TimeSpan.FromMilliseconds(DesignTokens.PanelCollapseExitMs);
    private const double CategoryRevealOffset = DesignTokens.ContentEntranceOffsetPx;
    private const double PanelCollapseExitOffset = DesignTokens.CollapseExitOffsetPx;
    private static readonly HandoffBehavior CategoryRevealAnimationHandoffBehavior =
        HandoffBehavior.SnapshotAndReplace;

    private readonly IBoardStore _store;
    private readonly BoardService _board;
    private readonly ClipboardCaptureService _clipboardCapture;
    private readonly BoardMutationService _mutations;
    private readonly DragPayloadService _dragPayload;
    private readonly ExternalDropPayloadReader _externalDropPayloadReader;
    private readonly ExternalDropImportService _externalDropImportService;
    private readonly PanelStateMachine _panelState = new();
    private readonly CategoryScrollState _scrollState = new();
    private readonly DispatcherTimer _expandIntentTimer;
    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly MainWindowViewModel _viewModel;
    private readonly object _pendingOperationsLock = new();
    private readonly HashSet<Task> _pendingOperations = [];
    private readonly SemaphoreSlim _settingsSaveGate = new(1, 1);
    private readonly SemaphoreSlim _preferencesSaveGate = new(1, 1);
    private readonly IPreferencesStore? _preferencesStore;
    private readonly IStartupManager _startupManager;
    private readonly PluginCatalog? _pluginCatalog;
    private readonly IGlobalHotkeySource _globalHotkeySource;
    private readonly string _dataDirectory;
    private readonly Func<Window, double>? _rightEdgeBleedProvider;
    private AppPreferences _preferences = AppPreferences.Default;
    private SettingsWindow? _settingsWindow;
    private double _rightEdgeBleed;
    private System.Windows.Interop.HwndSource? _windowSource;
    private (int Left, int Top, int Width, int Height)? _placementTargetRectangle;
    private CancellationTokenSource _windowOperationCancellation = new();
    private DesignTheme _activeDesignTheme = DesignTheme.Light;
    private bool _micaApplied;
    private IDataObject? _externalDragData;
    private ExternalDropPayload? _externalDragPayload;
    private Point _dragStart;
    private BoardItem? _dragItem;
    private bool _dragThresholdCrossed;
    private ModifierKeys _selectionModifiers;
    private WindowSettings _settings;
    private long _externalDragSurfaceVersion;
    private int _scrollRestoreVersion;
    private bool _isClosing;
    private bool _allowClose;
    private bool _globalHotkeyRegistered;

    public bool ClientAreaAnimationsEnabled
    {
        get => (bool)GetValue(ClientAreaAnimationsEnabledProperty);
        set => SetValue(ClientAreaAnimationsEnabledProperty, value);
    }

    private static void OnClientAreaAnimationsEnabledChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is MainWindow window && eventArgs.NewValue is false)
        {
            window.CancelPanelCollapseExit();
            window.StopPanelContentAnimation();
            window.StopCategoryRevealAnimations();
            window.StopCardEntranceAnimations();
        }
    }

    public MainWindow(
        BoardService board,
        IBoardStore store,
        WindowSettings settings,
        ClipboardCaptureService clipboardCapture,
        BoardMutationService mutations,
        DragPayloadService dragPayload,
        ExternalDropPayloadReader externalDropPayloadReader,
        ExternalDropImportService externalDropImportService,
        DefaultCaptureCategoryState? defaultCaptureCategory = null,
        IDailyReviewStore? dailyReviewStore = null,
        AppPreferences? preferences = null,
        IPreferencesStore? preferencesStore = null,
        IStartupManager? startupManager = null,
        string? dataDirectory = null,
        Func<Window, double>? rightEdgeBleedProvider = null,
        PluginCatalog? pluginCatalog = null,
        IGlobalHotkeySource? globalHotkeySource = null)
    {
        InitializeComponent();
        _preferences = preferences ?? AppPreferences.Default;
        _preferencesStore = preferencesStore;
        _startupManager = startupManager ?? new WindowsStartupManager();
        _pluginCatalog = pluginCatalog;
        _globalHotkeySource = globalHotkeySource ?? new Win32GlobalHotkeySource();
        _dataDirectory = dataDirectory ?? string.Empty;
        _activeDesignTheme = ResolveTheme(_preferences.ThemeMode);
        DesignThemeManager.Apply(this, _activeDesignTheme);
        ApplyAnimationsPreference(_preferences.AnimationsEnabled);
        SetResourceReference(
            ClientAreaAnimationsEnabledProperty,
            SystemParameters.ClientAreaAnimationKey);
        _store = store;
        _board = board;
        _clipboardCapture = clipboardCapture;
        _mutations = mutations;
        _dragPayload = dragPayload;
        _externalDropPayloadReader = externalDropPayloadReader;
        _externalDropImportService = externalDropImportService;
        _rightEdgeBleedProvider = rightEdgeBleedProvider;
        _rightEdgeBleed = rightEdgeBleedProvider?.Invoke(this) ?? 0d;
        ApplyRightEdgeBleedInset();
        _dailyReviews = dailyReviewStore ?? store as IDailyReviewStore;
        var work = CurrentWorkArea();
        _settings = settings.Normalize(work.Width, work.Height);
        _viewModel = new MainWindowViewModel(board, _settings, defaultCaptureCategory);
        DataContext = _viewModel;
        _expandIntentTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _expandIntentTimer.Tick += ExpandIntentTimer_Tick;
        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _collapseTimer.Tick += CollapseTimer_Tick;
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _statusTimer.Tick += StatusTimer_Tick;
        InitializeDailyReviewEditing();
        InitializeCategoryNameEditing();
        InitializePanelTextEditing();
        SourceInitialized += MainWindow_SourceInitialized;

        ApplyPlacement(WindowController.Collapsed(
            work,
            _settings,
            _viewModel.DefaultCapturePanel.Category,
            _rightEdgeBleed));
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            _dailyReviews?.Dispose();
            Application.Current?.Shutdown();
        };
    }

    public void ShowStatus(string message)
    {
        _viewModel.ShowStatus(message);
        UpdateStatusPresentation();
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void StatusTimer_Tick(object? sender, EventArgs e)
    {
        _statusTimer.Stop();
        _viewModel.ClearStatus();
        CompactStatusPopup.IsOpen = false;
    }

    private void UpdateStatusPresentation()
    {
        CompactStatusPopup.IsOpen =
            !_viewModel.IsPanelExpanded &&
            !string.IsNullOrWhiteSpace(_viewModel.StatusText);
    }

    /// <summary>
    /// 全局快捷键唤起：展开面板并定位到当前默认接收分类；已展开或拖放轨道
    /// 可见时保持现状，避免打断正在进行的交互。
    /// </summary>
    internal void OnGlobalHotkeyPressed()
    {
        if (_isClosing ||
            _viewModel.IsPanelExpanded ||
            _viewModel.IsExternalDropRailVisible)
        {
            return;
        }

        var category = _viewModel.DefaultCapturePanel.Category;
        _panelState.Switch(category);
        ActivatePanel(category);
        ApplyPlacement(WindowController.Expanded(CurrentWorkArea(), _settings, _rightEdgeBleed));
        _viewModel.SetPanelExpanded(true);
        UpdateStatusPresentation();
        CategoryRail.UpdateLayout();
        RestoreScrollOffset(category);
        AnimatePanelContent(isCategorySwitch: false);
    }

    /// <summary>
    /// 注册/注销全局热键。开启时窗口句柄未就绪或组合键被占用返回 false；
    /// 关闭总是成功（注销已注册的热键并清理标记）。
    /// </summary>
    internal bool TrySetGlobalHotkey(bool enable)
    {
        if (!enable)
        {
            if (_globalHotkeyRegistered && _windowSource?.Handle is { } handle)
            {
                _globalHotkeySource.Unregister(handle, Win32GlobalHotkeySource.HotkeyId);
            }

            _globalHotkeyRegistered = false;
            return true;
        }

        if (_globalHotkeyRegistered)
        {
            return true;
        }

        if (_windowSource?.Handle is not { } readyHandle || readyHandle == 0)
        {
            return false;
        }

        if (!_globalHotkeySource.TryRegister(readyHandle, Win32GlobalHotkeySource.HotkeyId))
        {
            return false;
        }

        _globalHotkeyRegistered = true;
        return true;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
        if (_windowSource is null)
        {
            ShowStatus("窗口初始化未完成，请重新打开悬浮中转站。");
            return;
        }

        _windowSource.AddHook(WndProc);
        RefreshEdgeBleed();
        ApplyWindowMaterial();
        if (!NativeMethods.AddClipboardFormatListener(_windowSource.Handle))
        {
            ShowStatus("剪贴板监听未启动，请重新打开悬浮中转站。");
        }

        // 偏好开启但注册失败时不改写偏好：状态条提示，下次启动自动重试。
        if (_preferences.GlobalHotkeyEnabled && !TrySetGlobalHotkey(enable: true))
        {
            ShowStatus("全局快捷键注册失败，可能被其他软件占用。");
        }
    }

    private void ApplyWindowMaterial()
    {
        _micaApplied = DwmWindowEffects.TryApplyMaterial(
            _windowSource?.Handle ?? 0,
            _activeDesignTheme == DesignTheme.Dark);
        if (!_micaApplied && WindowShell is not null)
        {
            // 材质不可用（旧系统）时回退到不透明壳，避免透明像素露出黑底。
            WindowShell.Background =
                TryFindResource("WindowShellOpaqueBrush") as System.Windows.Media.Brush
                ?? WindowShell.Background;
        }
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_placementTargetRectangle is { } target && message == NativeMethods.WmWindowPosChanging)
        {
            ClampWindowPosChangingToPlacement(lParam, target);
        }

        if (!_isClosing && message == NativeMethods.WmClipboardUpdate)
        {
            StartClipboardCapture();
        }

        if (!_isClosing &&
            message == NativeMethods.WmHotKey &&
            wParam == Win32GlobalHotkeySource.HotkeyId)
        {
            Dispatcher.BeginInvoke(OnGlobalHotkeyPressed);
            handled = true;
        }

        if (!_isClosing && message == NativeMethods.WmSettingChange)
        {
            // 任务栏停靠侧等改变工作区的系统设置只广播 WM_SETTINGCHANGE（不伴随
            // WM_DISPLAYCHANGE），右缘裁切判定会停在旧值；任何系统参数变化都重估。
            Dispatcher.BeginInvoke(RefreshEdgeBleed);

            if (System.Runtime.InteropServices.Marshal.PtrToStringUni(lParam)
                    is { Length: > 0 } section &&
                section.Contains("ImmersiveColorSet", StringComparison.Ordinal))
            {
                var detected = DesignThemeManager.DetectSystemTheme();
                Dispatcher.BeginInvoke(() =>
                {
                    // 用户强制浅色/深色时不再跟随系统变化；跟随系统模式（含预览覆盖）保持原行为。
                    if (_preferences.ThemeMode != ThemePreference.FollowSystem)
                    {
                        return;
                    }

                    _activeDesignTheme = detected;
                    DesignThemeManager.Apply(this, detected);
                    DwmWindowEffects.UpdateImmersiveDarkMode(
                        _windowSource?.Handle ?? 0,
                        detected == DesignTheme.Dark);
                    _settingsWindow?.ApplyTheme(detected);
                });
            }
        }

        if (!_isClosing && message == NativeMethods.WmDisplayChange)
        {
            Dispatcher.BeginInvoke(RefreshEdgeBleed);
        }

        return 0;
    }

    /// <summary>
    /// 迁移守卫：把系统即将应用的中间矩形改写为终态矩形。矩形已等于终态时改写为幂等
    /// no-op；清掉 NOMOVE/NOSIZE 后 x/y/cx/cy 才会被系统采纳。
    /// </summary>
    private static void ClampWindowPosChangingToPlacement(
        nint lParam,
        (int Left, int Top, int Width, int Height) target)
    {
        var position = System.Runtime.InteropServices.Marshal
            .PtrToStructure<NativeMethods.WindowPosition>(lParam);
        position.X = target.Left;
        position.Y = target.Top;
        position.Width = target.Width;
        position.Height = target.Height;
        position.Flags &= ~(NativeMethods.SwpNoMove | NativeMethods.SwpNoSize);
        System.Runtime.InteropServices.Marshal.StructureToPtr(position, lParam, false);
    }

    private void StartClipboardCapture()
    {
        TrackPendingOperation(CaptureClipboardSafelyAsync(
            _windowOperationCancellation.Token));
    }

    private void TrackPendingOperation(Task operation)
    {
        lock (_pendingOperationsLock)
        {
            _pendingOperations.Add(operation);
        }

        _ = RemovePendingOperationWhenCompletedAsync(operation);
    }

    private async Task RemovePendingOperationWhenCompletedAsync(Task operation)
    {
        try
        {
            await operation;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // The operation-specific safe wrapper owns user-visible failure reporting.
        }
        finally
        {
            lock (_pendingOperationsLock)
            {
                _pendingOperations.Remove(operation);
            }
        }
    }

    private async Task CaptureClipboardSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _clipboardCapture.HandleClipboardUpdateAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            ShowStatus("本次剪贴板内容未处理，请重新复制。");
        }
    }

    private Guid[] CaptureSelectedItemIds() =>
        BoardList.SelectedItems
            .OfType<BoardItem>()
            .Select(item => item.Id)
            .ToArray();

    private Guid? GetCategoryMoveScrollTargetId(IReadOnlyCollection<Guid> itemIds)
    {
        if (_viewModel.ActivePanel is not { } panel)
        {
            return null;
        }

        var selected = itemIds.ToHashSet();
        var ordered = panel.Items
            .Where(item => selected.Contains(item.Id))
            .ToArray();
        if (ordered.Length != selected.Count)
        {
            return null;
        }

        return ordered.FirstOrDefault(item => item.IsPinned)?.Id ?? ordered[0].Id;
    }

    private void RestoreSelection(IReadOnlyCollection<Guid> selectedIds)
    {
        BoardList.UnselectAll();
        if (selectedIds.Count == 0)
        {
            return;
        }

        var selected = selectedIds.ToHashSet();
        foreach (var item in BoardList.Items.OfType<BoardItem>())
        {
            if (selected.Contains(item.Id))
            {
                BoardList.SelectedItems.Add(item);
            }
        }
    }

    private void ActivateCategoryAfterBatchMove(BoardCategory category, Guid scrollTargetId)
    {
        SaveCurrentScrollOffset();
        _panelState.Switch(category);
        ActivatePanel(category);
        _viewModel.SetPanelExpanded(true);
        ApplyPlacement(WindowController.Expanded(CurrentWorkArea(), _settings, _rightEdgeBleed));
        UpdateStatusPresentation();
        ScrollItemToTop(category, scrollTargetId);
        AnimatePanelContent(isCategorySwitch: true);
    }

    private void ScrollItemToTop(BoardCategory category, Guid itemId)
    {
        var version = ++_scrollRestoreVersion;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (version != _scrollRestoreVersion ||
                    _viewModel.ActivePanel?.Category != category ||
                    FindDescendant<ScrollViewer>(BoardList) is not { } viewer ||
                    BoardList.Items.OfType<BoardItem>()
                        .SingleOrDefault(item => item.Id == itemId) is not { } item)
                {
                    return;
                }

                BoardList.ScrollIntoView(item);
                BoardList.UpdateLayout();
                if (BoardList.Items.IndexOf(item) == 0)
                {
                    viewer.ScrollToVerticalOffset(0d);
                    _scrollState.Save(category, 0d);
                    return;
                }

                if (BoardList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container)
                {
                    return;
                }

                var itemY = container.TranslatePoint(new Point(), BoardList).Y;
                viewer.ScrollToVerticalOffset(Math.Clamp(
                    viewer.VerticalOffset + itemY - BoardList.Padding.Top,
                    0d,
                    viewer.ScrollableHeight));
                _scrollState.Save(category, viewer.VerticalOffset);
            }));
    }

    private (int InsertionIndex, double IndicatorY) GetBoardDropLocation(
        DragEventArgs e,
        CategoryViewModel panel,
        IReadOnlyCollection<Guid> dragItemIds)
    {
        var spans = BuildRealizedItemSpans(panel);
        var slot = InsertionSlotResolver.Resolve(spans, e.GetPosition(BoardList).Y);
        var pinnedCount = panel.Items.TakeWhile(item => item.IsPinned).Count();
        var dragBatch = panel.Items
            .Where(item => dragItemIds.Contains(item.Id))
            .ToArray();
        var pinStates = dragBatch.Select(item => item.IsPinned).Distinct().ToArray();
        if (pinStates.Length != 1)
        {
            // 混合置顶批量（或负载与本面板脱节）不参与钳制，由 CanMoveMany 决定隐藏。
            return (slot.InsertionIndex, ClampIndicatorY(slot.IndicatorEdgeY));
        }

        var insertionIndex = InsertionSlotResolver.ClampInsertionIndex(
            slot.InsertionIndex,
            pinnedCount,
            pinStates[0]);
        var indicatorEdgeY = InsertionSlotResolver.IndicatorEdgeY(spans, insertionIndex);
        return (insertionIndex, ClampIndicatorY(indicatorEdgeY));
    }

    /// <summary>
    /// 枚举当前已实现的列表条目在 BoardList 坐标系的边缘矩形。每次拖动事件重新
    /// 计算（DragOver 为 Input 优先级，滚动后布局可能陈旧；回收容器身份会变），
    /// 条目索引取 Items 的真实索引，不假设首个/末个已实现条目对应 0/总数。
    /// </summary>
    private List<InsertionItemSpan> BuildRealizedItemSpans(CategoryViewModel panel)
    {
        var spans = new List<InsertionItemSpan>();
        if (FindDescendant<VirtualizingStackPanel>(BoardList) is not { } itemsHost)
        {
            return spans;
        }

        for (var index = 0; index < itemsHost.Children.Count; index++)
        {
            if (itemsHost.Children[index] is not ListBoxItem container ||
                container.DataContext is not BoardItem item ||
                !container.IsVisible)
            {
                continue;
            }

            var topEdge = container.TranslatePoint(new Point(), BoardList).Y;
            spans.Add(new InsertionItemSpan(
                panel.Items.IndexOf(item),
                topEdge,
                topEdge + container.ActualHeight));
        }

        return spans;
    }



}
