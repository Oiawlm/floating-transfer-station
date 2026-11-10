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
    // 置顶重申周期：足够短以在其他置顶窗口出现后很快恢复层级，足够长可忽略开销。
    // IME 输入敏感期（编辑器持焦/组合中）Tick 内的重申被抑制，编辑结束后至多
    // 一个周期（≤2 秒）自动恢复层级，定时器接线本身不感知编辑态。
    private static readonly TimeSpan TopmostReassertInterval = TimeSpan.FromSeconds(2);
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
    private readonly AiImageNamingScheduler _aiImageNamingScheduler;
    private readonly PanelStateMachine _panelState = new();
    private readonly CategoryScrollState _scrollState = new();
    private readonly DispatcherTimer _expandIntentTimer;
    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _topmostTimer;
    private readonly MainWindowViewModel _viewModel;
    private readonly object _pendingOperationsLock = new();
    private readonly HashSet<Task> _pendingOperations = [];
    private readonly SemaphoreSlim _settingsSaveGate = new(1, 1);
    private readonly SemaphoreSlim _preferencesSaveGate = new(1, 1);
    private readonly IPreferencesStore? _preferencesStore;
    private readonly IStartupManager _startupManager;
    private readonly PluginCatalog? _pluginCatalog;
    private readonly IGlobalHotkeySource _globalHotkeySource;
    private readonly ISecretProtector _secretProtector;
    private readonly DeepSeekClient _deepSeekClient;
    private readonly InputDeliveryService _inputDelivery;
    private readonly BoardOperationGate? _operationGate;
    private readonly DataDirectoryChangeService? _dataDirectoryChangeService;
    private readonly string _dataDirectory;
    private AppPreferences _preferences = AppPreferences.Default;
    private SettingsWindow? _settingsWindow;
    private System.Windows.Interop.HwndSource? _windowSource;
    private (int Left, int Top, int Width, int Height)? _placementTargetRectangle;
    private CancellationTokenSource _windowOperationCancellation = new();
    private DesignTheme _activeDesignTheme = DesignTheme.Light;
    private bool _micaApplied;
    private IDataObject? _externalDragData;
    private ExternalDropPayload? _externalDragPayload;
    private WindowSettings _settings;
    private long _externalDragSurfaceVersion;
    private int _scrollRestoreVersion;
    // 拖放反馈稳定态（契约 #51）：分类高亮当前已应用真值／撤离复核版本与最后断言
    // 时刻（保持窗判定）；列表插入槽位迟滞的上一有效槽位（拖拽会话内有效，
    // 会话边界由 ClearDropFeedback 复位）。
    private CategoryViewModel? _categoryDropTargetApplied;
    private long _categoryDropTargetWithdrawVersion;
    private long _categoryDropTargetAssertTick;
    private int? _heldInsertionSlotIndex;
    // 指示线显示真值（拖拽会话态，契约 #51 增补）：显/隐切换走「断言→撤离保持窗
    // 复核→终清」单一状态机，与分类高亮同构——边缘往返不再产生硬切换脉冲。
    private bool _insertionIndicatorShown;
    private long _insertionIndicatorWithdrawVersion;
    private long _insertionIndicatorAssertTick;
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
        PluginCatalog? pluginCatalog = null,
        IGlobalHotkeySource? globalHotkeySource = null,
        BoardOperationGate? operationGate = null,
        DataDirectoryChangeService? dataDirectoryChangeService = null,
        ISecretProtector? secretProtector = null,
        DeepSeekClient? deepSeekClient = null,
        AiTextOrganizer? aiTextOrganizer = null,
        IUpdateService? updateService = null,
        ILauncherStore? launcherStore = null,
        AppLauncher? appLauncher = null,
        ILauncherDiscoveryStore? launcherDiscoveryStore = null,
        InputDeliveryService? inputDelivery = null,
        Func<DateTimeOffset>? updateClock = null,
        DefaultSubgroupState? defaultSubgroup = null,
        AiImageNamingScheduler? aiImageNamingScheduler = null)
    {
        InitializeComponent();
        _preferences = preferences ?? AppPreferences.Default;
        _preferencesStore = preferencesStore;
        _startupManager = startupManager ?? new WindowsStartupManager();
        _pluginCatalog = pluginCatalog;
        _globalHotkeySource = globalHotkeySource ?? new Win32GlobalHotkeySource();
        // 1.27.0 DeepSeek 接入地基：保护器与客户端可注入（测试用假实现），
        // 未注入时用真实实现兜底（与 _startupManager/_globalHotkeySource 同法）。
        _secretProtector = secretProtector ?? new DpapiSecretProtector();
        _deepSeekClient = deepSeekClient ?? new DeepSeekClient();
        // 输入送达（需求 6）：可注入（测试传假 Win32 接缝）；未注入时用真实实现兜底
        //（与 _startupManager/_globalHotkeySource 同法）。
        _inputDelivery = inputDelivery ?? new InputDeliveryService();
        // AI 整理编排（1.41.0）复用同一客户端实例；组合根显式装配，测试可注入
        // 挂假 HttpMessageHandler 客户端的编排器（全离线）。
        _aiTextOrganizer = aiTextOrganizer ?? new AiTextOrganizer(_deepSeekClient);
        _updateService = updateService ?? new UpdateService();
        // 更新调度时钟可注入（契约 #26 修订：空闲触发的时间抽象，测试注入假时钟）；
        // 未注入时用真实时钟（与 updateService 同法：生产兜底、测试注入）。
        _updateClock = updateClock ?? (() => DateTimeOffset.Now);
        _updateScheduler = new UpdateCheckScheduler(
            UpdateFirstCheckDelay,
            UpdateCheckInterval,
            UpdateIdleCheckInterval,
            _updateClock);
        _operationGate = operationGate;
        _dataDirectoryChangeService = dataDirectoryChangeService;
        _dataDirectory = dataDirectory ?? string.Empty;
        _activeDesignTheme = ResolveTheme(_preferences.ThemeMode);
        DesignThemeManager.Apply(this, _activeDesignTheme);
        // 自定义字体全链路：构造期按偏好设定进程级当前字体并应用到本窗；
        // 后续偏好变化走 ApplyPreferences → InterfaceFontManager.ApplyToWindowTree。
        InterfaceFontManager.SetOverride(_preferences.InterfaceFont);
        InterfaceFontManager.Apply(this);
        ApplyAnimationsPreference(_preferences.AnimationsEnabled);
        ApplyPinnedDividerPreference(_preferences.PinnedDivider);
        SetResourceReference(
            ClientAreaAnimationsEnabledProperty,
            SystemParameters.ClientAreaAnimationKey);
        _store = store;
        _board = board;
        _clipboardCapture = clipboardCapture;
        _mutations = mutations;
        _dragPayload = dragPayload;
        // 图片拖出为文件实验开关（契约 #58）：构造段逐项下发偏好——启动不走
        // ApplyPreferences，漏本行＝重启后开关失效（沿 AnimationsPreference 先例）。
        _dragPayload.ExternalFilePayloadsEnabled = _preferences.DragOutAsFileEnabled;
        // AI 识图命名调度（c2）：组合根注入与两条采集管线共享的实例；测试未注入时
        // 用窗口自有依赖兜底（与 _aiTextOrganizer 同法：生产显式装配、测试注入）。
        _aiImageNamingScheduler = aiImageNamingScheduler ?? new AiImageNamingScheduler(
            new AiImageNamingOrganizer(_deepSeekClient),
            _board,
            _mutations,
            _secretProtector,
            () => _preferences,
            ShowStatus,
            TrackPendingOperation,
            () => _windowOperationCancellation.Token,
            Dispatcher);
        // AI 识图命名开关构造段镜像：启动不走 ApplyPreferences，漏本行＝重启后
        // 开关失效（沿上方 DragOut 镜像先例，#58 教训）。
        _aiImageNamingScheduler.Enabled = _preferences.AiImageNamingEnabled;
        _externalDropPayloadReader = externalDropPayloadReader;
        _externalDropImportService = externalDropImportService;
        _dailyReviews = dailyReviewStore ?? store as IDailyReviewStore;
        var work = CurrentWorkArea();
        _settings = settings.Normalize(work.Width, work.Height);
        _viewModel = new MainWindowViewModel(board, _settings, defaultCaptureCategory, defaultSubgroup);
        DataContext = _viewModel;
        _viewModel.ShortCardNotesEnabled = _preferences.ShortCardNotesEnabled;
        _viewModel.AiOrganizeEnabled = _preferences.AiOrganizeEnabled;
        // 子分组呈现模式（契约 #57）：启动只喂模式与名清单快照（Reindex 派生标记），
        // 不触发 GroupedView 进入分区——持久化 Order 已是启用时的分区结果，重启
        // 自动重整队会覆盖用户在 GroupedView 内拖出的有意新 run。
        _viewModel.SubgroupDisplay = _preferences.SubgroupDisplay;
        _board.SetSubgroupDisplay(_preferences.SubgroupDisplay, _settings);
        // 子分组轨点击切换实验开关（契约 #62）：构造段镜像——启动不走
        // ApplyPreferences，漏本行＝重启后开关失效（沿 DragOut 镜像先例，#58 教训）；
        // 默认关＝现状零行为变化。
        _viewModel.SubgroupRailClickSwitchEnabled = _preferences.SubgroupRailClickSwitchEnabled;
        // 插件目录态（Issue #240 插件钮可见性单一来源）：启动装载先于窗口构造
        //（App 组合根），构造期即同步；后续目录变化经设置路径刷新（见
        // MainWindow.Settings.cs）。
        SyncPluginActionsAvailability();
        // 面板悬停展开意图窗（#30 Issue #173）：60ms 场景值与 DesignTokens.EdgePeekExpandDwellMs
        // 同值异义（彼为贴边把手展开意图事务），禁止共用——独立保留字面量，豁免依据
        // docs/design.md §六「行为计时器」行（意图窗 60ms），复审轮 S3 注释豁免论证。
        _expandIntentTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _expandIntentTimer.Tick += ExpandIntentTimer_Tick;
        // 收起驻留（指针离面→到期收起）：250ms＝SlowDurationMs 收起节奏同源，复审轮
        // S3 裁决引 token（同值零变化；design.md §六豁免行同步除名）。
        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DesignTokens.SlowDurationMs) };
        _collapseTimer.Tick += CollapseTimer_Tick;
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _statusTimer.Tick += StatusTimer_Tick;
        _topmostTimer = new DispatcherTimer { Interval = TopmostReassertInterval };
        _topmostTimer.Tick += (_, _) => ReassertTopmost();
        _edgePeekExpandIntentTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(DesignTokens.EdgePeekExpandDwellMs),
        };
        _edgePeekExpandIntentTimer.Tick += EdgePeekExpandIntentTimer_Tick;
        InitializeDailyReviewEditing();
        InitializeAutoCleanup();
        InitializeUpdates();
        InitializeCategoryNameEditing();
        InitializeSubgroupNameEditing();
        InitializePanelTextEditing();
        InitializeCardEditSessionTracking();
        InitializeEditingCommit();
        InitializeBoardEmptyState();
        InitializeQuickLaunch(launcherStore, appLauncher, launcherDiscoveryStore);
        SourceInitialized += MainWindow_SourceInitialized;

        ApplyPlacement(WindowController.Collapsed(
            work,
            _settings,
            _viewModel.DefaultCapturePanel.Category));
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

    /// <summary>
    /// 采纳后台装载期间完成的设置迁移结果（记录迁移、分类默认名迁移）：同步退出保存用的
    /// 设置基准与全部分类显示名，使迁移无需在窗口构造前完成（启动先显示收起态）。
    /// </summary>
    internal void AdoptMigratedSettings(WindowSettings settings)
    {
        _settings = settings;
        foreach (var panel in _viewModel.Categories)
        {
            _viewModel.ApplyCategoryName(panel, settings.CategoryName(panel.Category));
        }
    }

    /// <summary>
    /// 采纳后台装载期间完成的首选项迁移结果（快捷启动版本戳，#30）：同步退出
    /// 保存用的首选项基准，防止后续任意一次偏好保存把已落盘的版本戳回退到旧值
    /// （戳回退会让一次性名称归位在下次启动重放、覆盖用户的启动标签改名）。
    /// </summary>
    internal void AdoptMigratedPreferences(AppPreferences preferences)
    {
        _preferences = preferences;
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
    /// 可见时保持现状，避免打断正在进行的交互。贴边隐藏离屏期间热键是找回
    /// 兜底：立即回原位并解除（面板本就展开，不重复展开序列）。
    /// </summary>
    internal void OnGlobalHotkeyPressed()
    {
        // 用户显式唤起时立即抬到置顶带最高层，不等周期重申。
        ReassertTopmost();
        if (_isClosing)
        {
            return;
        }

        if (_edgeHide.IsPeek)
        {
            RestoreFromEdgeHide();
            return;
        }

        if (_viewModel.IsPanelExpanded ||
            _viewModel.IsExternalDropRailVisible)
        {
            return;
        }

        var category = _viewModel.DefaultCapturePanel.Category;
        _panelState.Switch(category);
        ActivatePanel(category);
        ApplyPlacement(WindowController.Expanded(CurrentWorkArea(), _settings));
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
        ApplyWindowMaterial();
        StartClipboardListening();

        // 偏好开启但注册失败时不改写偏好：状态条提示，下次启动自动重试。
        if (_preferences.GlobalHotkeyEnabled && !TrySetGlobalHotkey(enable: true))
        {
            ShowStatus("全局快捷键注册失败，可能被其他软件占用。");
        }

        ReassertTopmost();
        _topmostTimer.Start();
        _autoCleanupCheckTimer.Start();
        // 自动检查更新（默认开）：30 秒首检 + 24 小时周期 + 2 小时交互空闲，三触发
        // 统一调度（契约 #26 修订，Issue #275）。
        ApplyUpdateCheckPreference(_preferences.AutoUpdateCheckEnabled);
    }

    /// <summary>注册剪贴板格式监听；已在监听时为 no-op。搬迁静默期停止后可由此恢复。</summary>
    private void StartClipboardListening()
    {
        var handle = _windowSource?.Handle ?? 0;
        if (handle == 0)
        {
            return;
        }

        if (!NativeMethods.AddClipboardFormatListener(handle))
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
            // 材质不可用（旧系统）时回退到不透明壳，避免透明像素露出黑底；
            // 动态资源引用让兜底随主题活切换跟随新字典，不定格旧主题实例。
            WindowShell.SetResourceReference(Border.BackgroundProperty, "WindowShellOpaqueBrush");
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
            // WM_DISPLAYCHANGE）；任何系统参数变化都重新贴齐，保证可见态窗口完整落在工作区内。
            Dispatcher.BeginInvoke(OnDisplayEnvironmentChanged);

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
            Dispatcher.BeginInvoke(OnDisplayEnvironmentChanged);
        }

        return 0;
    }

    /// <summary>
    /// 显示器/工作区环境变化（WM_DISPLAYCHANGE、WM_SETTINGCHANGE）：peek 态窗口
    /// 在新显示器布局下可能失去找回路径，立即展开恢复（防找不到窗口）——展开动画
    /// 随后的重贴齐应用会被 ReapplyCurrentPlacement 取消并落到新布局的展开放置；
    /// 工作区可能已改变，按当前面板状态重新贴齐，可见态外形不变。
    /// </summary>
    private void OnDisplayEnvironmentChanged()
    {
        if (_edgeHide.IsPeek)
        {
            RestoreFromEdgeHide();
        }

        ReapplyCurrentPlacement();
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

    /// <summary>当前窗口操作令牌（组合根闭包按次读取最新值；搬迁恢复会整体替换令牌源）。</summary>
    internal CancellationToken WindowOperationToken => _windowOperationCancellation.Token;

    internal void TrackPendingOperation(Task operation)
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
        ApplyPlacement(WindowController.Expanded(CurrentWorkArea(), _settings));
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
        var cursorY = e.GetPosition(BoardList).Y;
        var slot = InsertionSlotResolver.Resolve(spans, cursorY);
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

        var resolvedIndex = InsertionSlotResolver.ClampInsertionIndex(
            slot.InsertionIndex,
            pinnedCount,
            pinStates[0]);
        // 槽位迟滞（契约 #51）：翻转中线 ±SlotFlipHysteresisDip 带内的抖动维持上一
        // 有效槽位。指示线、CanMoveMany 与 Drop 共用同一有效槽位（视觉与落点一致）；
        // 带外位置与无迟滞解析完全相同，落点/钳制语义零变化。
        var insertionIndex = InsertionSlotResolver.ResolveHeldSlot(
            spans,
            cursorY,
            resolvedIndex,
            _heldInsertionSlotIndex);
        _heldInsertionSlotIndex = insertionIndex;
        var indicatorEdgeY = InsertionSlotResolver.IndicatorEdgeY(spans, insertionIndex);
        return (insertionIndex, ClampIndicatorY(indicatorEdgeY));
    }

    private void ResetInsertionSlotHysteresis() => _heldInsertionSlotIndex = null;

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
