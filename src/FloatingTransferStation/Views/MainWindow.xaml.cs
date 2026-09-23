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
    private readonly string _dataDirectory;
    private readonly Func<Window, double>? _rightEdgeBleedProvider;
    private AppPreferences _preferences = AppPreferences.Default;
    private SettingsWindow? _settingsWindow;
    private double _rightEdgeBleed;
    private System.Windows.Interop.HwndSource? _windowSource;
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
        Func<Window, double>? rightEdgeBleedProvider = null)
    {
        InitializeComponent();
        _preferences = preferences ?? AppPreferences.Default;
        _preferencesStore = preferencesStore;
        _startupManager = startupManager ?? new WindowsStartupManager();
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
        if (!_isClosing && message == NativeMethods.WmClipboardUpdate)
        {
            StartClipboardCapture();
        }

        if (!_isClosing &&
            message == NativeMethods.WmSettingChange &&
            System.Runtime.InteropServices.Marshal.PtrToStringUni(lParam) is { Length: > 0 } section &&
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

        if (!_isClosing && message == NativeMethods.WmDisplayChange)
        {
            Dispatcher.BeginInvoke(RefreshEdgeBleed);
        }

        return 0;
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
        CategoryViewModel panel)
    {
        var targetContainer = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (targetContainer?.DataContext is BoardItem target)
        {
            var targetIndex = panel.Items.IndexOf(target);
            if (targetIndex >= 0)
            {
                var pointerY = e.GetPosition(targetContainer).Y;
                var insertionIndex = DropInsertionCalculator.ForTarget(
                    targetIndex,
                    pointerY,
                    targetContainer.ActualHeight);
                var edgeY = insertionIndex == targetIndex
                    ? 0d
                    : targetContainer.ActualHeight;
                var listY = targetContainer.TranslatePoint(new Point(0, edgeY), BoardList).Y;
                return (insertionIndex, ClampIndicatorY(listY));
            }
        }

        return (
            DropInsertionCalculator.ForEmptySpace(panel.Items.Count),
            ClampIndicatorY(GetVisibleListEndY()));
    }



}
