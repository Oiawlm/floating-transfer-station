using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;
using FloatingTransferStation;
using FloatingTransferStation.Design;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Views;

/// <summary>
/// 设置首版：主题（跟随系统/浅色/深色）、动效、开机自启、数据目录与关于。
/// 所有改动立即经宿主生效并异步原子持久化，没有确认按钮；
/// 退出按钮走主窗既有 Closing 冲刷序列。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly ISettingsHost _host;
    private bool _isSynchronizingControls;
    private bool _micaApplied;
    private bool _isChangingDataDirectory;
    private bool _isDeepSeekKeyVisible;
    private bool _isTestingDeepSeekConnection;

    // ---- 页脚关于区联系方式（W5 2026-11-05；2026-11-06 复制按钮删除，
    //      反馈位改就微信号 Run 文本翻转）----

    private const string WeChatCopiedFeedbackText = "已复制";
    private const string WeChatCopyFailedFeedbackText = "复制失败";
    private static readonly TimeSpan WeChatCopyFeedbackRevertDelay = TimeSpan.FromSeconds(1.6);

    private DispatcherTimer? _wechatCopyFeedbackTimer;

    // QR 容器级过渡变换（W3 2026-11-05；2026-11-06 升级容器级）：构造函数内
    // 建组挂到 WeChatQrOverlayCard（外层整卡容器）。
    private ScaleTransform? _qrZoomScale;
    private TranslateTransform? _qrZoomTranslate;
    /// <summary>QR 层生命周期代数（同 FadeAnimation.HideGeneration 口径）：每次
    /// 开层自增——单一完成点的回调（容器 translateY Completed／退化卡片淡出
    /// Completed）只对发起那次关层的代数生效，替换式中断后残留的旧时钟回调
    /// （含已入队未分发的 Completed）不得塌缩重开后的层。</summary>
    private int _qrLifecycleGeneration;

    // QR 过渡文字/缩略图不透明度终态口径：文字关闭静止基值 0（XAML 同款）与
    // 全显定格 1；缩略图终态恒 1（ResetQrTransitionState 固定写全显）。
    private const double QrTextClosedOpacity = 0d;
    private const double QrTextShownOpacity = 1d;

    /// <summary>
    /// 剪贴板写入缝（同 MainWindow.ClipboardDelivery 约定）：测试注入捕获
    /// 函数以断言负载，不触碰系统剪贴板；null 时走真实 Clipboard.SetDataObject。
    /// </summary>
    internal Func<DataObject, bool>? ClipboardWriterOverride { get; set; }

    public SettingsWindow(ISettingsHost host)
    {
        _host = host;
        InitializeComponent();
        DesignThemeManager.Apply(this, host.CurrentTheme);
        // 构造期应用进程级当前界面字体（偏好 InterfaceFont；null=默认），与主题应用同点。
        InterfaceFontManager.Apply(this);

        // 固定视口（契约 #24，Issue #106）：窗口高度不再跟随内容（SizeToContent=Manual），
        // 溢出由 SettingsScrollHost 滚动。窗口不声明 MaxWidth/MaxHeight（1.50.0 教训：
        // 属性会在 HWND 超宽/最大化时把根视觉钳出死区）；交互缩放区间与最大化几何
        // 全部经 WM_GETMINMAXINFO 轨道尺寸强制（SettingsWindowPlacement），恢复
        // 持久化几何时（SourceInitialized）再由 Normalize 按目标显示器工作区钳制。

        _isSynchronizingControls = true;
        ThemeComboBox.ItemsSource = new[]
        {
            new ComboBoxItem { Content = "跟随系统", Tag = ThemePreference.FollowSystem },
            new ComboBoxItem { Content = "浅色", Tag = ThemePreference.Light },
            new ComboBoxItem { Content = "深色", Tag = ThemePreference.Dark }
        };
        ThemeComboBox.SelectedIndex = (int)host.CurrentPreferences.ThemeMode;
        PinnedDividerComboBox.ItemsSource = new[]
        {
            new ComboBoxItem { Content = "无", Tag = PinnedDividerStyle.None },
            new ComboBoxItem { Content = "实线", Tag = PinnedDividerStyle.Solid },
            new ComboBoxItem { Content = "虚线", Tag = PinnedDividerStyle.Dashed },
            new ComboBoxItem { Content = "强调条", Tag = PinnedDividerStyle.AccentBar }
        };
        PinnedDividerComboBox.SelectedIndex = (int)host.CurrentPreferences.PinnedDivider;
        PopulateInterfaceFontOptions(host.CurrentPreferences.InterfaceFont);
        AnimationsToggle.IsChecked = host.CurrentPreferences.AnimationsEnabled;
        StartupToggle.IsChecked = host.StartupManager.IsEnabled();
        GlobalHotkeyToggle.IsChecked = host.CurrentPreferences.GlobalHotkeyEnabled;
        AutoUpdateCheckToggle.IsChecked = host.CurrentPreferences.AutoUpdateCheckEnabled;
        CtrlCCopyToggle.IsChecked = host.CurrentPreferences.CopySelectionWithCtrlCEnabled;
        ShortCardNotesToggle.IsChecked = host.CurrentPreferences.ShortCardNotesEnabled;
        LauncherDiscoveryToggle.IsChecked = host.CurrentPreferences.LauncherDiscoveryEnabled;
        AutoCleanupToggle.IsChecked = host.CurrentPreferences.AutoCleanupEnabled;
        AiOrganizeEnabledToggle.IsChecked = host.CurrentPreferences.AiOrganizeEnabled;
        // AI 识图命名（#60，实验性默认关，独立于 AI 整理开关）：构造同步段回显。
        AiImageNamingToggle.IsChecked = host.CurrentPreferences.AiImageNamingEnabled;
        ReviewMarkdownViewToggle.IsChecked = host.CurrentPreferences.ReviewMarkdownViewEnabled;
        // 图片拖出为文件（契约 #58 实验性第三项）：构造同步段回显。
        DragOutAsFileToggle.IsChecked = host.CurrentPreferences.DragOutAsFileEnabled;
        // 子分组轨点击切换（契约 #62 实验性第四项，默认关）：构造同步段回显。
        SubgroupRailClickSwitchToggle.IsChecked = host.CurrentPreferences.SubgroupRailClickSwitchEnabled;
        // 子分组三态（契约 #57 实验性第二项）：枚举设置一律 ComboBox（仓库先例），
        /// 选中项按 Tag 匹配、不依赖下标。
        var subgroupItems = new[]
        {
            new ComboBoxItem { Content = "关闭", Tag = SubgroupDisplayMode.Off },
            new ComboBoxItem { Content = "按钮轨", Tag = SubgroupDisplayMode.ChipRail },
            new ComboBoxItem { Content = "视觉分组", Tag = SubgroupDisplayMode.GroupedView }
        };
        SubgroupModeComboBox.ItemsSource = subgroupItems;
        SubgroupModeComboBox.SelectedItem = subgroupItems.Single(item =>
            item.Tag is SubgroupDisplayMode mode &&
            mode == host.CurrentPreferences.SubgroupDisplay);
        SetAiOrganizeAreasAvailability(
            host.CurrentPreferences.AiOrganizeEnabled,
            host.CurrentPreferences.AiImageNamingEnabled);
        // 档位显示文案由唯一映射派生（全仓小时数字面量只允许出现在映射与测试期望），
        // 枚举序≠显示序（零值是默认档 24h），选中项一律按 Tag 匹配、不依赖下标；
        // 总开关关闭时下拉置灰（档位仍照常持久化）。
        var retentionItems = new[]
        {
            AutoCleanupRetention.TwoHours,
            AutoCleanupRetention.FourHours,
            AutoCleanupRetention.SixHours,
            AutoCleanupRetention.EightHours,
            AutoCleanupRetention.TenHours,
            AutoCleanupRetention.TwelveHours,
            AutoCleanupRetention.TwentyFourHours
        }.Select(tier => new ComboBoxItem
        {
            Content = $"{AutoCleanupSchedule.GetRetentionDuration(tier).TotalHours:0} 小时",
            Tag = tier
        }).ToArray();
        AutoCleanupRetentionComboBox.ItemsSource = retentionItems;
        AutoCleanupRetentionComboBox.SelectedItem =
            retentionItems.FirstOrDefault(item =>
                item.Tag is AutoCleanupRetention tier &&
                tier == host.CurrentPreferences.AutoCleanupRetention)
            ?? retentionItems.Single(item =>
                item.Tag is AutoCleanupRetention tier &&
                tier == AutoCleanupRetention.TwentyFourHours);
        AutoCleanupRetentionComboBox.IsEnabled = host.CurrentPreferences.AutoCleanupEnabled;
        TrashLeftClickComboBox.ItemsSource = new[]
        {
            new ComboBoxItem { Content = "清空非置顶", Tag = TrashNoSelectionLeftClickAction.ClearNonPinned },
            new ComboBoxItem { Content = "清空全部", Tag = TrashNoSelectionLeftClickAction.ClearAll }
        };
        TrashLeftClickComboBox.SelectedItem =
            ((ComboBoxItem[])TrashLeftClickComboBox.ItemsSource)[(int)host.CurrentPreferences.TrashNoSelectionLeftClick];
        TrashRightClickComboBox.ItemsSource = new[]
        {
            new ComboBoxItem { Content = "清空全部", Tag = TrashNoSelectionRightClickAction.ClearAll },
            new ComboBoxItem { Content = "清空非置顶", Tag = TrashNoSelectionRightClickAction.ClearNonPinned },
            new ComboBoxItem { Content = "无操作", Tag = TrashNoSelectionRightClickAction.NoAction }
        };
        TrashRightClickComboBox.SelectedItem = ((ComboBoxItem[])TrashRightClickComboBox.ItemsSource)
            .Single(item => Equals(item.Tag, host.CurrentPreferences.TrashNoSelectionRightClick));
        // 启动器列数策略（契约 #30 修订）：Tag=策略枚举按值匹配回显，不依赖下标；
        // 档位清单与 LauncherColumnStrategy 成员同域（自适应＋固定 3–6）。
        LauncherColumnStrategyComboBox.ItemsSource = new[]
        {
            new ComboBoxItem { Content = "自适应", Tag = LauncherColumnStrategy.Adaptive },
            new ComboBoxItem { Content = "固定 3 列", Tag = LauncherColumnStrategy.Fixed3 },
            new ComboBoxItem { Content = "固定 4 列", Tag = LauncherColumnStrategy.Fixed4 },
            new ComboBoxItem { Content = "固定 5 列", Tag = LauncherColumnStrategy.Fixed5 },
            new ComboBoxItem { Content = "固定 6 列", Tag = LauncherColumnStrategy.Fixed6 }
        };
        LauncherColumnStrategyComboBox.SelectedItem =
            ((ComboBoxItem[])LauncherColumnStrategyComboBox.ItemsSource)
                .Single(item => Equals(item.Tag, host.CurrentPreferences.LauncherColumnStrategy));

        // AI 设置（1.27.0）：仅配置 API Key；模型恒为 DeepSeekClient.DefaultModel
        // （deepseek-flash，契约 #28），无选择 UI。
        // 存量 Key 解密后入框（明文只在内存）；解密失败按未配置降级并就地提示。
        var storedDeepSeekKey = host.CurrentPreferences.DeepSeekApiKeyProtected;
        if (!string.IsNullOrEmpty(storedDeepSeekKey))
        {
            var decryptedKey = _host.SecretProtector.Unprotect(storedDeepSeekKey);
            if (decryptedKey is not null)
            {
                DeepSeekKeyBox.Password = decryptedKey;
            }
            else
            {
                ShowDeepSeekStatus("API Key 无法解密，请重新填写。");
            }
        }

        // 整理提示词（1.52.0）：null 态不预填默认全文（保住「是否默认」判定
        // 基线），状态标签随回填显式当前口径。
        AiOrganizePromptBox.Text =
            host.CurrentPreferences.AiOrganizeSystemPromptOverride ?? string.Empty;

        _isSynchronizingControls = false;

        RebuildLauncherDiscoveryFolderRows();
        SetLauncherDiscoveryInputAvailability(host.CurrentPreferences.LauncherDiscoveryEnabled);

        DataDirectoryText.Text = host.DataDirectory;
        AppNameRun.Text = ProductIdentity.DisplayName;
        VersionRun.Text = $"版本 {ProductIdentity.Version}";
        // repo 地址单一来源（契约 #26）：页脚链接与更新服务共用 ProductIdentity 常量，
        // XAML 不再维护字面量。
        RepoLink.NavigateUri = new Uri(ProductIdentity.GitHubRepoHtmlUrl);
        // 联系方式同一口径（W5 2026-11-05）：微信号文本两处视图与二维码位图
        //（页脚缩略图＋放大层共用同一冻结实例）全部取自 ProductIdentity。
        WeChatIdRun.Text = ProductIdentity.WeChatId;
        WeChatIdLargeRun.Text = ProductIdentity.WeChatId;
        // 二维码位图用「pack 绝对 URI＋显式程序集限定」加载：程序集名（悬浮中转站）
        // 从承载程序集运行时推导（不硬编码第二份），路径唯一来源 ProductIdentity；
        // 显式限定使设置窗在应用进程与测试宿主（入口非本程序集）下同样可解析——
        // 相对 URI 在纯代码路径会被当文件路径解析（实测渲染期失败即把 Source 置空）。
        var weChatQrSource = new BitmapImage(new Uri(
            $"pack://application:,,,/{typeof(SettingsWindow).Assembly.GetName().Name};component{ProductIdentity.WeChatQrResourcePath}",
            UriKind.Absolute));
        weChatQrSource.Freeze();
        WeChatQrThumb.Source = weChatQrSource;
        WeChatQrLarge.Source = weChatQrSource;
        // QR 容器级过渡（2026-11-06 升级）的渲染变换：TransformGroup（先 Scale 后
        // Translate）代码后置构造与驱动、挂外层整卡容器 WeChatQrOverlayCard（框随
        // 二维码一起飞、方→矩形形变，文字子树飞行期 Opacity=0 不呈现）；遮罩布局
        // 一次到位恒定，动画只动渲染变换与文字/缩略图 Opacity、不改布局矩形。
        _qrZoomScale = new ScaleTransform();
        _qrZoomTranslate = new TranslateTransform();
        WeChatQrOverlayCard.RenderTransform = new TransformGroup
        {
            Children = { _qrZoomScale, _qrZoomTranslate }
        };
        PopulatePluginSection();
        PopulateCategoryOrderSection();
        // 子分组顺序节（契约 #57 修订）：构造回显＋按当前三态门控（Off＝置灰）。
        PopulateSubgroupOrderSection();
        SetSubgroupOrderAvailability(host.CurrentPreferences.SubgroupDisplay);
        Owner = host.HostWindow;
        // 安全网钳位必须挂在构造函数：SizeToContent 的测量与尺寸变化事件在
        // SourceInitialized 之前就已全部发生（探针实测 Loaded 先于 SourceInitialized），
        // 挂晚了整个初始放置过程没有任何钳位（1.15.1 缺陷）；此后尺寸再变化
        // （如运行时改缩放）仍由它持续兜底。
        SizeChanged += SettingsWindow_SizeChanged;
        SourceInitialized += SettingsWindow_SourceInitialized;
    }

    /// <summary>主窗主题变化时同步设置窗的主题字典与沉浸式深色。</summary>
    internal void ApplyTheme(DesignTheme theme)
    {
        DesignThemeManager.Apply(this, theme);
        DwmWindowEffects.UpdateImmersiveDarkMode(
            new System.Windows.Interop.WindowInteropHelper(this).Handle,
            theme == DesignTheme.Dark);
    }

    // ---- 页脚关于区联系方式（W5 2026-11-05；W3 2026-11-05 交互矩阵＋缩放入场；
    //      2026-11-06 右键二维码＝复制图片、复制按钮删除）：微信号展示、复制、
    //      二维码放大层 ----

    private void WeChatQrButton_Click(object sender, RoutedEventArgs e) => ShowWeChatQrOverlay();

    /// <summary>右键缩略二维码＝复制二维码图片（不放大）：与页脚微信号行共用
    /// 反馈位（页脚微信号 Run），复制走图片写入缝。</summary>
    private void WeChatQrButton_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowWeChatCopyFeedback(WeChatIdRun, WriteWeChatQrImageToClipboard());
    }

    /// <summary>页脚微信号行左/右键＝复制微信号文本：同一处理器服两事件，
    /// 反馈就地翻转本行微信号 Run（2026-11-06 起文本复制唯一入口即本行）。</summary>
    private void WeChatIdFooterBlock_PointButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowWeChatCopyFeedback(WeChatIdRun, WriteWeChatIdToClipboard());
    }

    /// <summary>放大态微信号行左/右键＝复制且不关层：e.Handled 阻断冒泡到遮罩
    /// 关层（同 QR 卡右键复制被消费的形态），反馈就地翻转本行微信号 Run。</summary>
    private void WeChatIdLargeBlock_PointButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowWeChatCopyFeedback(WeChatIdLargeRun, WriteWeChatIdToClipboard());
    }

    /// <summary>右键放大图＝复制二维码图片且不关层：与缩略图右键同一缝（负载
    /// 同为冻结位图），e.Handled 阻断冒泡到遮罩关层，反馈就地翻转放大态
    /// 微信号 Run。</summary>
    private void WeChatQrLargeCard_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowWeChatCopyFeedback(WeChatIdLargeRun, WriteWeChatQrImageToClipboard());
    }

    private void WeChatQrOverlay_PointButtonUpCloses(object sender, MouseButtonEventArgs e)
    {
        // 左/右键单击遮罩、左键单击卡片＝关闭（「任意单击关闭」对称语义）；
        // QR 卡右键的抬起被复制图片处理器 e.Handled 消费、放大态微信号行的抬起
        // 被其处理器 e.Handled 消费，都不会冒泡到这里；关闭走 FadeAnimation 统一显隐管理。
        e.Handled = true;
        HideWeChatQrOverlay();
    }

    private void ShowWeChatQrOverlay()
    {
        var animationsEnabled = _host.CurrentPreferences.AnimationsEnabled;
        // 根 Grid 只承载布局＋命中＋Visibility（结构拆分后无 Opacity 编排）；
        // 显隐淡入淡出＝遮罩纱的 FadeAnimation，层可见性由编排自身即时开
        //（关闭时经单一完成点收口，见 HideWeChatQrOverlay）。
        WeChatQrOverlay.Visibility = Visibility.Visible;
        // 开层自增生命周期代数：作废关层途中残留的旧时钟完成回调（再开不被
        // 掉队回调塌缩——同 FadeAnimation.HideGeneration 的防掉队口径）。
        _qrLifecycleGeneration++;
        FadeAnimation.SetAnimationsEnabled(WeChatQrScrim, animationsEnabled);
        // 收回曾把遮罩纱淡出改锚缩放同步时长（Hide），Show 开头复位回轻档 83ms
        //（既有 DP、ApplyState 读取，零新机制）——入场淡入保持快速档。
        FadeAnimation.SetDurationMs(WeChatQrScrim, DesignTokens.FastDurationMs);
        FadeAnimation.SetIsActive(WeChatQrScrim, true);
        if (animationsEnabled)
        {
            // 容器级过渡与遮罩纱淡入同帧挂上（遮罩纱起点 0、From 即时生效，
            // 无满尺寸闪帧）；动效关＝跳过编排（对象定格全显终态）。
            PlayQrZoomEntrance();
        }
        else
        {
            ResetQrTransitionState(QrTextShownOpacity);
        }
    }

    private void HideWeChatQrOverlay()
    {
        var animationsEnabled = _host.CurrentPreferences.AnimationsEnabled;
        FadeAnimation.SetAnimationsEnabled(WeChatQrScrim, animationsEnabled);
        if (animationsEnabled)
        {
            // 遮罩纱与容器整层一个节奏收回：时长复位**先于** Measure 判定（R4——
            // 退化路径不再沿用上次 Show 的过期 83ms），出场曲线由 XAML
            // ExitEaseToken＝QrZoomOutKeySpline（同容器减速曲线、末端水平，
            // 消除末段斜率骤曝 R2）。
            FadeAnimation.SetDurationMs(WeChatQrScrim, DesignTokens.QrZoomOutMs);
            if (MeasureQrSourceRect() is { } sourceRect)
            {
                // 正常路径：收回＝从当下变换态动画回**当下实测**源矩形（「从哪来
                // 回哪去」；动态 From 是结构性质——SnapshotAndReplace 替换式中断，
                // 可随时被再次交互替换）。编排反向重排（Q5：文字层与缩略图层不
                // 续走）：文字先隐（显式 From 当下值→0 快速淡出）、卡片全程可见
                // 飞回（收回关键帧 90ms 归位后持稳 1）、缩略图先清隐再于
                // QrThumbRevealBeginMs（95% 回程）起与卡片同窗线性换手（对齐落位）。
                AnimateQrZoom(
                    CurrentQrVisualRect(),
                    sourceRect,
                    FadeAnimation.QrZoomOutEasing,
                    DesignTokens.QrZoomOutMs,
                    FillBehavior.HoldEnd);
                PlayQrTextExit();
                PlayQrCardExit();
                PlayQrThumbExit();
            }
            else
            {
                // 退化路径（R4，求值不可达）：纯淡出——容器快照当下值定基后清动画
                //（定格在飞、禁瞬时恒等）、卡片自当下值线性淡出，卡片淡出
                // Completed＝退化路径完成点；终态与正常路径逐值一致。
                PlayQrDegradedExit();
            }

            FadeAnimation.SetIsActive(WeChatQrScrim, false);
        }
        else
        {
            // 动效关＝即时塌缩：对象定格关闭静止基值（容器恒等、文字 0、卡片 0、
            // 缩略图 1），遮罩纱即时归零（FadeAnimation 即时分支），层即时收起。
            ResetQrTransitionState(QrTextClosedOpacity);
            FadeAnimation.SetIsActive(WeChatQrScrim, false);
            WeChatQrOverlay.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 放大层容器级入场：共用过渡路径 AnimateQrZoom——from＝当下实测缩略图按钮
    /// 含框源矩形（容器飞行中断被再开时＝当下渲染矩形续走，同收回动态 From 口径
    /// 不瞬跳）、to＝恒等布局矩形、入场缓动/时长（实验性独立常量组）、
    /// FillBehavior.Stop（终态回恒等基值零残留）。文字三元素延迟淡入与缩略图
    /// 防双影淡出随同帧编排（§3.2 时序表）。求值不可达或几何非法时静默退化为
    /// 三对象定格全显，不 crash。不做「移除再挂」式复位——可中断＝新动画替换
    ///（SnapshotAndReplace）。
    /// </summary>
    private void PlayQrZoomEntrance()
    {
        WeChatQrOverlay.UpdateLayout();
        var inFlight = IsQrContainerInFlight();
        if (!inFlight && MeasureQrSourceRect() is not { } source)
        {
            ResetQrTransitionState(QrTextShownOpacity);
            return;
        }

        var fromRect = inFlight ? CurrentQrVisualRect() : source;
        PlayQrTextEntrance();
        PlayQrCardEntrance();
        PlayQrThumbEntrance();
        var identityCard = new Rect(0, 0, WeChatQrOverlayCard.ActualWidth, WeChatQrOverlayCard.ActualHeight);
        AnimateQrZoom(
            fromRect,
            identityCard,
            FadeAnimation.QrZoomInEasing,
            DesignTokens.QrZoomInMs,
            FillBehavior.Stop);
    }

    /// <summary>卡片入场编排（自持不透明度，结构拆分后与遮罩纱兄弟）：显式 From＝
    /// 当下值→1、83ms、EnterEasing——与遮罩纱淡入同曲线同时长（兄弟不相乘、无
    /// 双重变淡，入场视觉与旧根淡入等价；From＝当下值＝收回途中再开自当下可见度
    /// 续走，不瞬跳）；fill=Stop 终态落基值 1（动效关开层与退化开层经
    /// ResetQrTransitionState 定格 1）。</summary>
    private void PlayQrCardEntrance()
    {
        var current = WeChatQrOverlayCard.Opacity;
        WeChatQrOverlayCard.BeginAnimation(UIElement.OpacityProperty, null);
        WeChatQrOverlayCard.SetValue(UIElement.OpacityProperty, QrTextShownOpacity);
        WeChatQrOverlayCard.BeginAnimation(
            UIElement.OpacityProperty,
            CreateQrOpacityAnimation(current, QrTextShownOpacity, DesignTokens.FastDurationMs, FadeAnimation.EnterEasing),
            FadeAnimation.AnimationHandoffBehavior);
    }

    /// <summary>文字三元素（标题/微信号行/尾注）开层编排（Q5：不续走，按开层
    /// 方向重排队）：基值卫生清动画置 0（防上一会话定格残留 1 闪现）后自 0 延迟
    /// 淡入——BeginTime 等待期显示基值 0 无闪现（代码级延迟动画先例
    /// MainWindow.CardEntrance），fill=HoldEnd 定格 1、Completed 卫生清动画置
    /// 基值 1；被替换中断的旧时钟不触发 Completed（卫生只在自然完成时落位）。</summary>
    private void PlayQrTextEntrance()
    {
        foreach (var text in QrTextElements())
        {
            text.BeginAnimation(UIElement.OpacityProperty, null);
            text.SetValue(UIElement.OpacityProperty, QrTextClosedOpacity);
            var animation = new DoubleAnimation(
                QrTextClosedOpacity, QrTextShownOpacity, TimeSpan.FromMilliseconds(DesignTokens.QrTextFadeInMs))
            {
                BeginTime = TimeSpan.FromMilliseconds(DesignTokens.QrTextFadeInBeginMs),
                EasingFunction = FadeAnimation.EnterEasing,
                FillBehavior = FillBehavior.HoldEnd,
            };
            animation.Completed += (_, _) =>
            {
                text.BeginAnimation(UIElement.OpacityProperty, null);
                text.SetValue(UIElement.OpacityProperty, QrTextShownOpacity);
            };
            text.BeginAnimation(UIElement.OpacityProperty, animation, FadeAnimation.AnimationHandoffBehavior);
        }
    }

    /// <summary>文字三元素关层编排（Q5：显式 From＝当下实测 Opacity，省略 From
    /// 会自基值起＝瞬跳）：基值卫生置关闭静止值 0，fill=Stop 落回基值——先于
    /// 容器落位隐没（§3.2 时序表「先隐」）。</summary>
    private void PlayQrTextExit()
    {
        foreach (var text in QrTextElements())
        {
            var current = text.Opacity;
            text.BeginAnimation(UIElement.OpacityProperty, null);
            text.SetValue(UIElement.OpacityProperty, QrTextClosedOpacity);
            text.BeginAnimation(
                UIElement.OpacityProperty,
                CreateQrOpacityAnimation(current, QrTextClosedOpacity, DesignTokens.QrTextFadeOutMs, FadeAnimation.ExitEasing),
                FadeAnimation.AnimationHandoffBehavior);
        }
    }

    /// <summary>缩略图开层防双影编排：基值卫生置 0（层开期间保持隐藏），显式
    /// From＝当下值→0 淡出（再开中断回显时自当下值继续隐没，不瞬跳）。</summary>
    private void PlayQrThumbEntrance()
    {
        var current = WeChatQrButton.Opacity;
        WeChatQrButton.BeginAnimation(UIElement.OpacityProperty, null);
        WeChatQrButton.SetValue(UIElement.OpacityProperty, QrTextClosedOpacity);
        WeChatQrButton.BeginAnimation(
            UIElement.OpacityProperty,
            CreateQrOpacityAnimation(current, QrTextClosedOpacity, DesignTokens.QrThumbFadeMs, FadeAnimation.EnterEasing),
            FadeAnimation.AnimationHandoffBehavior);
    }

    /// <summary>卡片收回编排（自持不透明度，收回关键帧全线性）：[0:当下值]→
    /// [90ms:1]（归位段，时长复用 QrTextFadeOutMs＝与文字先隐同族）→[180ms:1]
    ///（持稳：飞行期卡片全程可见——「回退动画看得见地回到原位」）→[250ms:0]
    ///（交接段：与缩略图回显同窗线性换手）；HoldEnd 定格、终态卫生经单一完成点
    /// 回基值 1（中断由 SnapshotAndReplace 天然安全——被替换动画不触发回调）。</summary>
    private void PlayQrCardExit()
    {
        var current = WeChatQrOverlayCard.Opacity;
        WeChatQrOverlayCard.BeginAnimation(UIElement.OpacityProperty, null);
        WeChatQrOverlayCard.SetValue(UIElement.OpacityProperty, QrTextShownOpacity);
        WeChatQrOverlayCard.BeginAnimation(
            UIElement.OpacityProperty,
            CreateQrExitKeyFrames(current, QrTextShownOpacity, QrTextClosedOpacity),
            FadeAnimation.AnimationHandoffBehavior);
    }

    /// <summary>缩略图收回编排（收回关键帧全线性，与卡片同时间轴）：[0:当下值]→
    /// [90ms:0]（清除段：杀中途半显值贯穿 R3 与动效关会话残留 1 的 R5）→
    /// [180ms:0]（持隐至 QrThumbRevealBeginMs＝95% 回程，容器已≈落位才交接）→
    /// [250ms:1]（回显段：与卡片 1→0 同窗线性交叉，落位点合成存在感恒≈1——
    /// 无台阶、无空窗、无双影）。</summary>
    private void PlayQrThumbExit()
    {
        var current = WeChatQrButton.Opacity;
        WeChatQrButton.BeginAnimation(UIElement.OpacityProperty, null);
        WeChatQrButton.SetValue(UIElement.OpacityProperty, QrTextShownOpacity);
        WeChatQrButton.BeginAnimation(
            UIElement.OpacityProperty,
            CreateQrExitKeyFrames(current, QrTextClosedOpacity, QrTextShownOpacity),
            FadeAnimation.AnimationHandoffBehavior);
    }

    /// <summary>收回两段式关键帧（全线性，时间轴＝QrTextFadeOutMs→
    /// QrThumbRevealBeginMs→QrZoomOutMs）：首键＝当下值续走，中段持稳值与末段
    /// 换手值随调用方（卡片持稳 1/换手 0；缩略图持隐 0/换手 1）。</summary>
    private static DoubleAnimationUsingKeyFrames CreateQrExitKeyFrames(double current, double holdValue, double endValue)
    {
        var frames = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(current, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(holdValue, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(DesignTokens.QrTextFadeOutMs))));
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(holdValue, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(DesignTokens.QrThumbRevealBeginMs))));
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(endValue, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(DesignTokens.QrZoomOutMs))));
        return frames;
    }

    /// <summary>退化路径收回（R4，Measure 求值不可达＝动效开但几何非法）：纯淡出
    /// ——容器**快照当下值定基后清动画**（定格在飞、禁瞬时归恒等——居中放大位
    /// 原位闪现的单点故障）；卡片自当下值线性淡出 QrZoomOutMs（其 Completed＝
    /// 退化路径单一完成点：层塌缩＋终态卫生含容器显式归恒等，与正常路径逐值
    /// 一致）；缩略图同窗线性归 1、文字先隐复用正常路径编排；遮罩纱同时长
    /// 淡出（Hide 已设时长与 ExitEaseToken）。再开时容器按飞行续走判定自
    /// 快照基值继续（IsQrContainerInFlight），不瞬跳。</summary>
    private void PlayQrDegradedExit()
    {
        var scaleX = _qrZoomScale!.ScaleX;
        var scaleY = _qrZoomScale.ScaleY;
        var translateX = _qrZoomTranslate!.X;
        var translateY = _qrZoomTranslate.Y;
        _qrZoomScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _qrZoomScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _qrZoomTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        _qrZoomTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        _qrZoomScale.ScaleX = scaleX;
        _qrZoomScale.ScaleY = scaleY;
        _qrZoomTranslate.X = translateX;
        _qrZoomTranslate.Y = translateY;

        PlayQrTextExit();
        var cardCurrent = WeChatQrOverlayCard.Opacity;
        WeChatQrOverlayCard.BeginAnimation(UIElement.OpacityProperty, null);
        WeChatQrOverlayCard.SetValue(UIElement.OpacityProperty, QrTextClosedOpacity);
        var cardFade = new DoubleAnimation(cardCurrent, QrTextClosedOpacity, TimeSpan.FromMilliseconds(DesignTokens.QrZoomOutMs))
        {
            FillBehavior = FillBehavior.HoldEnd
        };
        // 完成点带代数守卫（同正常路径口径）：退化收回途中再开＝卡片淡出被替换，
        // 掉队回调不得塌缩重开后的层。
        var generation = _qrLifecycleGeneration;
        cardFade.Completed += (_, _) =>
        {
            if (generation != _qrLifecycleGeneration)
            {
                return;
            }

            WeChatQrOverlay.Visibility = Visibility.Collapsed;
            ResetQrTransitionState(QrTextClosedOpacity);
        };
        WeChatQrOverlayCard.BeginAnimation(UIElement.OpacityProperty, cardFade, FadeAnimation.AnimationHandoffBehavior);

        var thumbCurrent = WeChatQrButton.Opacity;
        WeChatQrButton.BeginAnimation(UIElement.OpacityProperty, null);
        WeChatQrButton.SetValue(UIElement.OpacityProperty, QrTextShownOpacity);
        WeChatQrButton.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(thumbCurrent, QrTextShownOpacity, TimeSpan.FromMilliseconds(DesignTokens.QrZoomOutMs))
            {
                FillBehavior = FillBehavior.HoldEnd
            },
            FadeAnimation.AnimationHandoffBehavior);
    }

    /// <summary>文字三元素快照（标题/微信号行/尾注：容器级过渡的延迟浮现面）。</summary>
    private UIElement[] QrTextElements() => [WeChatQrTitle, WeChatIdLargeBlock, WeChatQrFootnote];

    /// <summary>容器四属性是否偏离恒等基值（＝有过渡动画在飞或定格未复位）：
    /// 入场 From 选择的续走判据——飞行中再开（含退化收回的飞行态快照定基）自
    /// 当下渲染矩形继续，静止态自缩略图源矩形起飞。基值只经构造、终态卫生与
    /// 退化快照写入，逐值精确比较可靠。</summary>
    private bool IsQrContainerInFlight() =>
        _qrZoomScale!.ScaleX != 1d || _qrZoomScale.ScaleY != 1d ||
        _qrZoomTranslate!.X != 0d || _qrZoomTranslate.Y != 0d;

    /// <summary>当下实测页脚缩略图按钮（含框，模板根即 Surface 框）在容器布局
    /// 坐标系中的源矩形（不缓存：窗口滚动/移动后收回仍回当下位置）。测量以遮罩
    /// 为共同坐标基、再换回容器布局原点——**剥掉容器自身 RenderTransform 的当下
    /// 缩放态**（TransformToVisual 会穿过容器 RT，动画进行中直接测量会被自身
    /// 动画放大），测量不受当下动画污染；求值不可达或几何非法返回 null（调用方
    /// 静默退化）。</summary>
    private Rect? MeasureQrSourceRect()
    {
        try
        {
            var buttonInOverlay = WeChatQrButton.TransformToVisual(WeChatQrOverlay).TransformBounds(
                new Rect(0, 0, WeChatQrButton.ActualWidth, WeChatQrButton.ActualHeight));
            // 容器布局原点（RT 无关）＝含 RT 的容器→遮罩原点 − RT 平移分量
            //（容器 (0,0) 绕原点缩放下只随 Translate 移动；遮罩/页脚两条链路都是
            // 纯平移布局、无缩放变换，尺寸直通不变；容器含文字子树不影响自身测量）。
            var containerOriginWithTransform = WeChatQrOverlayCard.TransformToVisual(WeChatQrOverlay).Transform(
                new Point(0, 0));
            var source = new Rect(
                buttonInOverlay.X - (containerOriginWithTransform.X - _qrZoomTranslate!.X),
                buttonInOverlay.Y - (containerOriginWithTransform.Y - _qrZoomTranslate!.Y),
                buttonInOverlay.Width,
                buttonInOverlay.Height);
            return source.Width > 0d && source.Height > 0d &&
                   double.IsFinite(source.X) && double.IsFinite(source.Y)
                ? source
                : (Rect?)null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>当下渲染矩形＝变换反推 Rect(tX, tY, cardW·sX, cardH·sY)——动画
    /// 中读取即当前插值态（收回的动态 From 与续走入场共用），零动画时即恒等布局
    /// 矩形。</summary>
    private Rect CurrentQrVisualRect() => new(
        _qrZoomTranslate!.X,
        _qrZoomTranslate.Y,
        WeChatQrOverlayCard.ActualWidth * _qrZoomScale!.ScaleX,
        WeChatQrOverlayCard.ActualHeight * _qrZoomScale.ScaleY);

    /// <summary>
    /// QR 容器级过渡共用插值（W3 附录 A 起；2026-11-06 升级动画对象＝外层整卡
    /// 容器）：「视觉矩形 from→to」同一进度 p 驱动——四条显式 From/To
    /// DoubleAnimation（先 Scale 后 Translate）同 duration＋同缓动＋同帧启动，
    /// 矩形插值是结构性质（ScaleX≠ScaleY 承载方→矩形形变、原路返回不靠两套动画
    /// 凑巧对称）；两轴**中心对齐**（tX=r.Center.X−sX·cardW/2）——方→高矩形双向
    /// 生长比单轴底对齐自然，且与 CurrentQrVisualRect 反推严格互逆（端点兼容）；
    /// 可中断＝SnapshotAndReplace 新动画替换；fill=Stop 终态回恒等基值零残留，
    /// fill=HoldEnd 终值定格、Completed 生命周期复位（ResetQrTransitionState——
    /// 遮罩此时已不可见，复位是终态卫生，不是中断手段）。求值不可达或几何非法
    /// 静默退化（不动画），不 crash。
    /// </summary>
    private void AnimateQrZoom(Rect fromRect, Rect toRect, SplineEase easing, int durationMs, FillBehavior fill)
    {
        var cardWidth = WeChatQrOverlayCard.ActualWidth;
        var cardHeight = WeChatQrOverlayCard.ActualHeight;
        var fromScaleX = cardWidth > 0d ? fromRect.Width / cardWidth : 0d;
        var fromScaleY = cardHeight > 0d ? fromRect.Height / cardHeight : 0d;
        var toScaleX = cardWidth > 0d ? toRect.Width / cardWidth : 0d;
        var toScaleY = cardHeight > 0d ? toRect.Height / cardHeight : 0d;
        if (cardWidth <= 0d || cardHeight <= 0d ||
            fromRect.Width <= 0d || fromRect.Height <= 0d ||
            toRect.Width <= 0d || toRect.Height <= 0d ||
            !double.IsFinite(fromRect.X) || !double.IsFinite(fromRect.Y) ||
            !double.IsFinite(toRect.X) || !double.IsFinite(toRect.Y) ||
            !double.IsFinite(fromScaleX) || !double.IsFinite(fromScaleY) ||
            !double.IsFinite(toScaleX) || !double.IsFinite(toScaleY))
        {
            return;
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(durationMs));
        // 两轴中心对齐（Q4）：tX=r.Center.X−sX·cardW/2（WPF Rect 无 Center 属性，
        // 手工取中点）——方→高矩形双向生长比单轴对齐自然，且与
        // CurrentQrVisualRect 反推严格互逆（端点兼容）。
        var translateXAnimation = CreateQrZoomAnimation(
            fromRect.X + (fromRect.Width - (fromScaleX * cardWidth)) / 2,
            toRect.X + (toRect.Width - (toScaleX * cardWidth)) / 2,
            duration,
            easing,
            fill);
        var translateYAnimation = CreateQrZoomAnimation(
            fromRect.Y + (fromRect.Height - (fromScaleY * cardHeight)) / 2,
            toRect.Y + (toRect.Height - (toScaleY * cardHeight)) / 2,
            duration,
            easing,
            fill);
        if (fill == FillBehavior.HoldEnd)
        {
            // 单一完成点（R6 结构性消除）：收回四条动画同 duration/同帧启动，容器
            // translateY Completed＝正常路径唯一生命周期载体——同一分发帧内层塌缩
            // ＋终态卫生（卡片/缩略图关键帧同时长同帧完成，错帧闪窗被结构性关闭）。
            // 代数守卫：SnapshotAndReplace 被替换的旧时钟不触发 Completed，但已
            // 入队未分发的回调仍可能掉队到达——再开自增代数后本回调须作废
            //（同 FadeAnimation.HideGeneration 的防掉队口径，重开不被塌缩）。
            var generation = _qrLifecycleGeneration;
            translateYAnimation.Completed += (_, _) =>
            {
                if (generation != _qrLifecycleGeneration)
                {
                    return;
                }

                WeChatQrOverlay.Visibility = Visibility.Collapsed;
                ResetQrTransitionState(QrTextClosedOpacity);
            };
        }
        else
        {
            // fill=Stop 终态回恒等基值：显式归一（退化路径可能残留飞行态本地基值，
            // 依赖「基值未被改写」的隐式假设不再成立——入场完成必须定格恒等）。
            _qrZoomScale!.ScaleX = 1d;
            _qrZoomScale.ScaleY = 1d;
            _qrZoomTranslate!.X = 0d;
            _qrZoomTranslate.Y = 0d;
        }

        _qrZoomScale!.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            CreateQrZoomAnimation(fromScaleX, toScaleX, duration, easing, fill),
            FadeAnimation.AnimationHandoffBehavior);
        _qrZoomScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            CreateQrZoomAnimation(fromScaleY, toScaleY, duration, easing, fill),
            FadeAnimation.AnimationHandoffBehavior);
        _qrZoomTranslate!.BeginAnimation(
            TranslateTransform.XProperty,
            translateXAnimation,
            FadeAnimation.AnimationHandoffBehavior);
        _qrZoomTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            translateYAnimation,
            FadeAnimation.AnimationHandoffBehavior);
    }

    /// <summary>全对象终态卫生（原 ResetQrZoomTransform 扩展更名）：容器四属性
    /// 清动画后**显式归恒等基值**（退化路径曾把容器定格在飞行态本地基值，清动画
    /// 后不得依赖基值未漂移的隐式假设），文字三元素与卡片清动画定格 textOpacity
    ///（卡片随层可见性：关闭终态 0＝层隐藏即隐、再开入场自 0 淡入；动效关/退化
    /// 开层 1＝全显），缩略图恒定格 1（关闭静止态＝页脚回显）；仅终态卫生，
    /// 不作中断手段。</summary>
    private void ResetQrTransitionState(double textOpacity)
    {
        if (_qrZoomScale is null || _qrZoomTranslate is null)
        {
            return;
        }

        _qrZoomScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _qrZoomScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _qrZoomTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        _qrZoomTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        _qrZoomScale.ScaleX = 1d;
        _qrZoomScale.ScaleY = 1d;
        _qrZoomTranslate.X = 0d;
        _qrZoomTranslate.Y = 0d;
        foreach (var text in QrTextElements())
        {
            text.BeginAnimation(UIElement.OpacityProperty, null);
            text.SetValue(UIElement.OpacityProperty, textOpacity);
        }

        WeChatQrOverlayCard.BeginAnimation(UIElement.OpacityProperty, null);
        WeChatQrOverlayCard.SetValue(UIElement.OpacityProperty, textOpacity);

        WeChatQrButton.BeginAnimation(UIElement.OpacityProperty, null);
        WeChatQrButton.SetValue(UIElement.OpacityProperty, QrTextShownOpacity);
    }

    private static DoubleAnimation CreateQrZoomAnimation(double from, double to, Duration duration, SplineEase easing, FillBehavior fill) =>
        new(from, to, duration)
        {
            EasingFunction = easing,
            FillBehavior = fill
        };

    private static DoubleAnimation CreateQrOpacityAnimation(double from, double to, int durationMs, SplineEase easing, FillBehavior fill = FillBehavior.Stop) =>
        new(from, to, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = easing,
            FillBehavior = fill
        };

    private bool WriteWeChatIdToClipboard()
    {
        try
        {
            var data = new DataObject(DataFormats.UnicodeText, ProductIdentity.WeChatId);
            if (ClipboardWriterOverride is { } writer)
            {
                return writer(data);
            }

            Clipboard.SetDataObject(data, true);
            return true;
        }
        catch (Exception exception) when (
            exception is COMException or ExternalException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// 复制二维码图片：剪贴板负载＝缩略图与放大层共用的同一冻结位图实例
    ///（DataObject.SetImage，同 DragPayloadService/InputDeliveryService 先例），
    /// 经同一剪贴板写入缝落剪贴板、同异常捕获（失败如实反馈）。
    /// </summary>
    private bool WriteWeChatQrImageToClipboard()
    {
        if (WeChatQrThumb.Source is not BitmapSource bitmap)
        {
            return false;
        }

        try
        {
            var data = new DataObject();
            data.SetImage(bitmap);
            if (ClipboardWriterOverride is { } writer)
            {
                return writer(data);
            }

            Clipboard.SetDataObject(data, true);
            return true;
        }
        catch (Exception exception) when (
            exception is COMException or ExternalException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// 复制反馈（轻档）：触发侧微信号 Run 文本瞬时翻转（已复制/复制失败），
    /// 延迟复位回 ProductIdentity.WeChatId 单一来源；计时器复位路径可被下一次
    /// 点击覆盖，不产生并发复位。
    /// </summary>
    private void ShowWeChatCopyFeedback(Run feedbackRun, bool succeeded)
    {
        feedbackRun.Text = succeeded ? WeChatCopiedFeedbackText : WeChatCopyFailedFeedbackText;
        _wechatCopyFeedbackTimer?.Stop();
        _wechatCopyFeedbackTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = WeChatCopyFeedbackRevertDelay
        };
        _wechatCopyFeedbackTimer.Tick += (_, _) =>
        {
            _wechatCopyFeedbackTimer.Stop();
            feedbackRun.Text = ProductIdentity.WeChatId;
        };
        _wechatCopyFeedbackTimer.Start();
    }

    // ---- 双栏导航（契约 #24，Issue #106）----

    private static readonly string[] PageNames =
        ["AppearancePage", "PanelPage", "GeneralPage", "DataPage", "AiPage"];

    private void SettingsNavItem_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string pageName })
        {
            SelectSettingsPage(pageName);
        }
    }

    private void SelectSettingsPage(string pageName)
    {
        foreach (var name in PageNames)
        {
            // InitializeComponent 解析中途首项 Checked 已触发，此时后续页尚未解析；
            // 各页初始可见性已在 XAML 声明，此处只补运行期切换。
            if (FindName(name) is not StackPanel page)
            {
                continue;
            }

            var selected = string.Equals(name, pageName, StringComparison.Ordinal);
            page.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            if (selected)
            {
                AnimatePageIn(page);
            }
        }

        // 共享视口的滚动偏移不跨页残留（切页回到页首）；构造期首项 Checked 时视口尚未解析。
        SettingsScrollHost?.ScrollToHome();
    }

    /// <summary>
    /// 页切换淡入（167ms 档，契约 #24 设计语言）：只动 Opacity，可见性已即时生效，
    /// UIA/命中不依赖动画收尾；「界面动效」关闭或构造期首项 Checked（未加载）直接就位。
    /// </summary>
    private void AnimatePageIn(StackPanel page)
    {
        page.BeginAnimation(UIElement.OpacityProperty, null);
        page.Opacity = 1d;
        if (!IsLoaded || !_host.CurrentPreferences.AnimationsEnabled)
        {
            return;
        }

        page.BeginAnimation(
            UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(
                0d,
                1d,
                TimeSpan.FromMilliseconds(FloatingTransferStation.Design.DesignTokens.NormalDurationMs)));
    }

    /// <summary>
    /// ↑/↓/Home/End 确定性切换导航项（契约 #24 键盘判据）。在隧道期消费，
    /// 压掉 RadioButton 的原生方向键导航，保证一次按键只前进一步。
    /// </summary>
    private void SettingsNavList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var offset = e.Key switch
        {
            Key.Up => -1,
            Key.Down => 1,
            Key.Home => int.MinValue,
            Key.End => int.MaxValue,
            _ => 0
        };
        if (offset == 0)
        {
            return;
        }

        e.Handled = true;
        var items = SettingsNavList.Children.OfType<RadioButton>().ToArray();
        if (items.Length == 0)
        {
            return;
        }

        var current = Array.FindIndex(items, item => item.IsChecked == true);
        if (current < 0)
        {
            current = 0;
        }

        var next = offset switch
        {
            int.MinValue => 0,
            int.MaxValue => items.Length - 1,
            _ => (current + offset + items.Length) % items.Length
        };
        items[next].IsChecked = true;
        items[next].Focus();
    }

    private void SettingsWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _micaApplied = DwmWindowEffects.TryApplyMaterial(
            handle,
            _host.CurrentTheme == DesignTheme.Dark);
        if (!_micaApplied)
        {
            ApplyWindowShellOpaqueFallback();
        }

        // 系统级缩放/最大化（契约 #24，Issue #106）：WM_EXITSIZEMOVE 是「结束才落盘」
        // 的唯一可靠信号，WM_GETMINMAXINFO 修正无边框窗口的最大化几何。
        if (System.Windows.Interop.HwndSource.FromHwnd(handle) is { } source)
        {
            source.AddHook(WndProc);
        }

        RestorePlacementOrDefault();
    }

    /// <summary>
    /// 材质不可用（旧系统）时回退到不透明壳，避免透明像素露出黑底；
    /// 动态资源引用让兜底随主题活切换跟随新字典（对齐全仓 DynamicResource 语义），
    /// 不定格在兜底瞬间的旧主题实例（探针实证一次性赋值会过期）。
    /// </summary>
    private void ApplyWindowShellOpaqueFallback() =>
        WindowShell.SetResourceReference(Border.BackgroundProperty, "WindowShellOpaqueBrush");

    // ---- 几何持久化与恢复（契约 #24，Issue #106）----

    private bool _isInSystemSizing;

    /// <summary>
    /// 系统最大化意图（WM_SYSCOMMAND SC_MAXIMIZE → WM_SIZE 收口）：轨道尺寸是窗口
    /// 管理器对一切尺寸变更的统一钳制边界，最大化时须让位给工作区（见 Placement）。
    /// </summary>
    private bool _isSystemMaximizing;

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmSize = 0x0005;
        const int WmSysCommand = 0x0112;
        const int WmGetMinMaxInfo = 0x0024;
        const int WmEnterSizeMove = 0x0231;
        const int WmExitSizeMove = 0x0232;
        const int ScMaximize = 0xF030;
        const int SizeRestored = 0;
        switch (message)
        {
            case WmSize:
                if (wParam.ToInt64() == SizeRestored)
                {
                    _isSystemMaximizing = false;
                }

                break;
            case WmSysCommand:
                // 只观察不消费：让系统/WPF 正常处理命令本身。最大化期间保持让位
                //（WPF/系统可能多次重申最大化矩形），其他命令（恢复/移动等）即清除。
                _isSystemMaximizing = (wParam.ToInt64() & 0xFFF0) == ScMaximize;
                break;
            case WmEnterSizeMove:
                _isInSystemSizing = true;
                break;
            case WmExitSizeMove:
                _isInSystemSizing = false;
                ClampIntoCurrentWorkArea();
                CommitPlacement();
                break;
            case WmGetMinMaxInfo:
                // 冻结数值唯一来源 Design.SettingsWindowMetrics；最大轨道高由
                // Placement 按窗口所在显示器工作区推导（40 字节结构体，契约 #24）。
                // 最大化意图双检测：系统路径（Win+Up 等）经 WM_SYSCOMMAND SC_MAXIMIZE
                //（WPF 在 WM_SIZE 后才更新 WindowState，须靠标志）；WPF WindowState
                // 赋值路径（如测试）在消息到达前 DP 已是 Maximized，直接可见。
                var maximizeIntent = _isSystemMaximizing || WindowState == WindowState.Maximized;
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
                handled = SettingsWindowPlacement.TryApplyMaximizeWorkArea(
                    hwnd,
                    lParam,
                    FloatingTransferStation.Design.SettingsWindowMetrics.MinWidth,
                    FloatingTransferStation.Design.SettingsWindowMetrics.MinHeight,
                    FloatingTransferStation.Design.SettingsWindowMetrics.MaxWidth,
                    dpi.PixelsPerInchX / 96d,
                    dpi.PixelsPerInchY / 96d,
                    maximizeIntent);
                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 恢复持久化几何（契约 #24 §四.3）：整组非法（缺失/NaN/±∞/非正尺寸）或完全落在
    /// 所有显示器之外 → 回落 owner 相对默认定位；否则按持久化矩形中心解析目标显示器，
    /// 以其工作区按冻结式（工作区高 − 48，矮屏抬到最小高）归一矩形（部分越界钳入、
    /// 负坐标合法）后应用。
    /// </summary>
    private void RestorePlacementOrDefault()
    {
        var preferences = _host.CurrentPreferences;
        var (left, top, width, height) = (
            preferences.SettingsWindowLeft,
            preferences.SettingsWindowTop,
            preferences.SettingsWindowWidth,
            preferences.SettingsWindowHeight);
        if (!SettingsWindowPlacement.IsValid(left, top, width, height)
            || !SettingsWindowPlacement.IntersectsVirtualScreen(left!.Value, top!.Value, width!.Value, height!.Value))
        {
            PlaceAtDefaultPosition();
            return;
        }

        var workArea = ResolveWorkAreaFor(left!.Value + width!.Value / 2, top!.Value + height!.Value / 2);
        var maxHeight = Math.Max(
            FloatingTransferStation.Design.SettingsWindowMetrics.MinHeight,
            workArea.Height - FloatingTransferStation.Design.SettingsWindowMetrics.MaxHeightMargin);
        var rect = SettingsWindowPlacement.Normalize(
            left.Value, top.Value, width.Value, height.Value,
            FloatingTransferStation.Design.SettingsWindowMetrics.MinWidth,
            FloatingTransferStation.Design.SettingsWindowMetrics.MaxWidth,
            FloatingTransferStation.Design.SettingsWindowMetrics.MinHeight,
            maxHeight,
            workArea);
        Width = rect.Width;
        Height = rect.Height;
        Left = rect.Left;
        Top = rect.Top;
    }

    /// <summary>owner 相对默认定位（首次运行或几何不可用的回落路径）。</summary>
    private void PlaceAtDefaultPosition()
    {
        var work = SystemParameters.WorkArea;
        if (Owner is { } owner)
        {
            Left = Math.Max(work.Left, Math.Min(owner.Left - Width - 12, work.Right - Width));
            Top = Math.Clamp(owner.Top, work.Top, Math.Max(work.Top, work.Bottom - ActualHeight));
            return;
        }

        // Owner 不可用的理论边界：主屏工作区居中。
        Left = work.Left + Math.Max(0, (work.Width - Width) / 2);
        Top = work.Top + Math.Max(0, (work.Height - ActualHeight) / 2);
    }

    private SettingsWindowPlacement.WorkArea ResolveWorkAreaFor(double dipX, double dipY)
    {
        if (SettingsWindowPlacement.WorkAreaResolverOverride is { } resolver)
        {
            return resolver(dipX, dipY) ?? PrimaryWorkArea();
        }

        return SettingsWindowPlacement.QueryWorkArea(this, dipX, dipY) ?? PrimaryWorkArea();
    }

    private static SettingsWindowPlacement.WorkArea PrimaryWorkArea() => new(
        SystemParameters.WorkArea.Left,
        SystemParameters.WorkArea.Top,
        SystemParameters.WorkArea.Right,
        SystemParameters.WorkArea.Bottom);

    /// <summary>把当前位置钳进所在显示器工作区（恢复/SizeChanged/系统缩放结束共用，
    /// 不留第二套钳制）。</summary>
    private void ClampIntoCurrentWorkArea()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top))
        {
            return;
        }

        var workArea = ResolveWorkAreaFor(Left + (double.IsNaN(ActualWidth) ? 0 : ActualWidth / 2),
            Top + (double.IsNaN(ActualHeight) ? 0 : ActualHeight / 2));
        Top = Math.Clamp(Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - ActualHeight));
        Left = Math.Clamp(Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - ActualWidth));
    }

    /// <summary>
    /// 拖动/系统缩放结束（Normal 态）把几何写回偏好：复用 ApplyPreferences 原子管线
    /// （契约 #5 同路）；最大化/最小化状态不落盘（恢复的是 Normal 几何）。
    /// </summary>
    private void CommitPlacement()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with
        {
            SettingsWindowLeft = Left,
            SettingsWindowTop = Top,
            SettingsWindowWidth = ActualWidth,
            SettingsWindowHeight = ActualHeight
        });
    }

    private void HeaderDragRegion_DragCompleted(object sender, DragCompletedEventArgs e) => CommitPlacement();

    /// <summary>
    /// 尺寸定稿或打开后再变化（如运行时改缩放、显示器断开）时，把窗口钳回所在
    /// 显示器工作区（与恢复共用同一钳制）。探针实测 SizeToContent 的测量与尺寸事件
    /// 先于 SourceInitialized 发生，该处理器必须在构造函数挂接才覆盖初始放置；
    /// 系统交互缩放期间（WM_SIZEMOVE）不逐帧回写位置，由 EXITSIZEMOVE 收口一次。
    /// </summary>
    private void SettingsWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isInSystemSizing)
        {
            return;
        }

        ClampIntoCurrentWorkArea();
    }

    private void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _categoryOrderDrag is not null)
        {
            // 拖拽会话中的 Esc 属「标签顺序」节：回弹原序不提交，优先于关窗路径消费。
            e.Handled = true;
            CancelCategoryOrderDrag();
            return;
        }

        if (e.Key == Key.Escape && _subgroupOrderDrag is not null)
        {
            // 拖拽会话中的 Esc 属「子分组顺序」节：回弹原序不提交，与「标签顺序」
            // 节会话并列同序（先于关窗路径消费）。
            e.Handled = true;
            CancelSubgroupOrderDrag();
            return;
        }

        if (e.Key == Key.Escape && WeChatQrOverlay.Visibility == Visibility.Visible)
        {
            // 二维码放大层打开时的 Esc 只关层（与「单击任意处关闭」同语义），
            // 不顺带关闭设置窗。
            e.Handled = true;
            HideWeChatQrOverlay();
            return;
        }

        if (e.Key == Key.Escape && !_isChangingDataDirectory)
        {
            e.Handled = true;
            Close();
        }
    }

    /// <summary>头部拖拽移动窗口；与主窗一致限制在工作区内。</summary>
    private void HeaderDragRegion_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var work = SystemParameters.WorkArea;
        // 垂直钳制用渲染定稿的 ActualHeight 而非 Height：Height 依赖 .NET 10 把
        // SizeToContent 定稿值写回的行为，写回缺失时 NaN 会让上界钳制静默失效。
        Left = Math.Clamp(Left + e.HorizontalChange, work.Left, Math.Max(work.Left, work.Right - Width));
        Top = Math.Clamp(Top + e.VerticalChange, work.Top, Math.Max(work.Top, work.Bottom - ActualHeight));
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!_isChangingDataDirectory)
        {
            Close();
        }
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizingControls ||
            ThemeComboBox.SelectedItem is not ComboBoxItem { Tag: ThemePreference mode })
        {
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with { ThemeMode = mode });
    }

    private void InterfaceFontComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizingControls ||
            InterfaceFontComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with
        {
            InterfaceFont = (string?)item.Tag
        });
    }

    /// <summary>默认项标注后缀：追加在链首字体显示名之后（如
    /// 「Microsoft YaHei UI（默认）」；族名无本地化中文名的环境即为该形态）。</summary>
    private const string InterfaceFontDefaultSuffix = "（默认）";

    /// <summary>
    /// 界面字体选项（2026-11-02 默认项语义合并）：默认不再是无名首项——系统字体
    /// 清单中与默认链链首（InterfaceFontManager.DefaultFamilyHeadName，即微软雅黑
    /// UI）同名的那一项升级为唯一默认项，置于首位、显示名追加「（默认）」、
    /// Tag=null（→ 偏好 InterfaceFont=null）；效果：偏好 null 或显式链首名回显到
    /// 同一项，默认字体在列表中有名有形、无双项并存。其余为本机已安装字体族
    /// （Source 串即偏好存储值，解析按同一清单校验），按当前 UI 文化的显示名排序；
    /// 图标/符号字体排除（选作界面字体无意义）。存疑名字（字体已卸载）不在清单，
    /// 选中回落默认项——偏好原值保留，待用户下次选择覆盖。链首字体不在本机清单
    /// 时回落抽象「默认」首项（保住默认可选中；支持的目标系统不缺链首）。
    /// </summary>
    private void PopulateInterfaceFontOptions(string? interfaceFont)
    {
        var defaultHead = InterfaceFontManager.DefaultFamilyHeadName();
        var families = Fonts.SystemFontFamilies
            .Where(family => !IsSymbolFont(family.Source))
            .OrderBy(FontDisplayName, StringComparer.CurrentCulture)
            .ToArray();
        var headFamily = Array.Find(families, family =>
            string.Equals(family.Source, defaultHead, StringComparison.OrdinalIgnoreCase));
        var items = new List<ComboBoxItem>(families.Length);
        if (headFamily is null)
        {
            items.Add(new() { Content = "默认", Tag = null });
        }
        else
        {
            items.Add(new()
            {
                Content = FontDisplayName(headFamily) + InterfaceFontDefaultSuffix,
                Tag = null
            });
        }
        items.AddRange(families
            .Where(family => !string.Equals(family.Source, defaultHead, StringComparison.OrdinalIgnoreCase))
            .Select(family => new ComboBoxItem
            {
                Content = FontDisplayName(family),
                Tag = family.Source
            }));
        InterfaceFontComboBox.ItemsSource = items;
        InterfaceFontComboBox.SelectedItem =
            items.FirstOrDefault(item => (string?)item.Tag == interfaceFont)
            ?? items[0];
    }

    private static bool IsSymbolFont(string source) =>
        source is "Segoe Fluent Icons" or "Segoe MDL2 Assets" or "Webdings"
            or "Wingdings" or "Wingdings 2" or "Wingdings 3";

    private static string FontDisplayName(FontFamily family)
    {
        // FamilyNames 键是 XmlLanguage：按当前 UI 文化取本地化名（无则回落源名）。
        var language = XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag);
        return family.FamilyNames.TryGetValue(language, out var localized)
            ? localized
            : family.Source;
    }

    private void PinnedDividerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizingControls ||
            PinnedDividerComboBox.SelectedItem is not ComboBoxItem { Tag: PinnedDividerStyle style })
        {
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with { PinnedDivider = style });
    }

    private void AnimationsToggle_Checked(object sender, RoutedEventArgs e) =>
        ApplyAnimationsFromToggle(true);

    private void AnimationsToggle_Unchecked(object sender, RoutedEventArgs e) =>
        ApplyAnimationsFromToggle(false);

    private void ApplyAnimationsFromToggle(bool animationsEnabled)
    {
        if (_isSynchronizingControls)
        {
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with
        {
            AnimationsEnabled = animationsEnabled
        });
    }

    private void StartupToggle_Checked(object sender, RoutedEventArgs e) =>
        ApplyStartupFromToggle(enable: true);

    private void StartupToggle_Unchecked(object sender, RoutedEventArgs e) =>
        ApplyStartupFromToggle(enable: false);

    private void ApplyStartupFromToggle(bool enable)
    {
        if (_isSynchronizingControls)
        {
            return;
        }

        try
        {
            if (enable)
            {
                _host.StartupManager.Enable();
            }
            else
            {
                _host.StartupManager.Disable();
            }

            StartupStatusText.Visibility = Visibility.Collapsed;
            StartupStatusText.Text = string.Empty;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            StartupStatusText.Text = "自启设置暂未写入注册表，请稍后重试。";
            StartupStatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            _isSynchronizingControls = true;
            StartupToggle.IsChecked = _host.StartupManager.IsEnabled();
            _isSynchronizingControls = false;
        }
    }

    private void GlobalHotkeyToggle_Checked(object sender, RoutedEventArgs e) =>
        ApplyGlobalHotkeyFromToggle(enable: true);

    private void GlobalHotkeyToggle_Unchecked(object sender, RoutedEventArgs e) =>
        ApplyGlobalHotkeyFromToggle(enable: false);

    private void ApplyGlobalHotkeyFromToggle(bool enable)
    {
        if (_isSynchronizingControls)
        {
            return;
        }

        if (!_host.TryApplyGlobalHotkeyPreference(enable))
        {
            GlobalHotkeyStatusText.Text = "全局快捷键注册失败，可能被其他软件占用。";
            GlobalHotkeyStatusText.Visibility = Visibility.Visible;
        }
        else
        {
            GlobalHotkeyStatusText.Text = string.Empty;
            GlobalHotkeyStatusText.Visibility = Visibility.Collapsed;
        }

        // 回显实际状态：注册失败的开启请求不落偏好，开关退回关闭。
        _isSynchronizingControls = true;
        GlobalHotkeyToggle.IsChecked = _host.CurrentPreferences.GlobalHotkeyEnabled;
        _isSynchronizingControls = false;
    }

    private void CtrlCCopyToggle_Checked(object sender, RoutedEventArgs e) =>
        ApplyCtrlCCopyPreference(enabled: true);

    private void CtrlCCopyToggle_Unchecked(object sender, RoutedEventArgs e) =>
        ApplyCtrlCCopyPreference(enabled: false);

    private void ApplyCtrlCCopyPreference(bool enabled)
    {
        if (_isSynchronizingControls)
        {
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with
        {
            CopySelectionWithCtrlCEnabled = enabled
        });
    }

    private void ShortCardNotesToggle_Checked(object sender, RoutedEventArgs e) =>
        ApplyShortCardNotesPreference(enabled: true);

    private void ShortCardNotesToggle_Unchecked(object sender, RoutedEventArgs e) =>
        ApplyShortCardNotesPreference(enabled: false);

    private void ApplyShortCardNotesPreference(bool enabled)
    {
        if (_isSynchronizingControls)
        {
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with
        {
            ShortCardNotesEnabled = enabled
        });
    }

    // ---- 启动·自动发现（#45）：开关＋监视文件夹管理，全部经 ApplyPreferences
    // 唯一管线即时生效（契约 #5）——主窗在管线内重建监视源并立即重扫。----

    private void LauncherDiscoveryToggle_Checked(object sender, RoutedEventArgs e) =>
        ApplyLauncherDiscoveryPreference(enabled: true);

    private void LauncherDiscoveryToggle_Unchecked(object sender, RoutedEventArgs e) =>
        ApplyLauncherDiscoveryPreference(enabled: false);

    private void ApplyLauncherDiscoveryPreference(bool enabled)
    {
        if (_isSynchronizingControls)
        {
            return;
        }

        SetLauncherDiscoveryInputAvailability(enabled);
        _host.ApplyPreferences(_host.CurrentPreferences with
        {
            LauncherDiscoveryEnabled = enabled
        });
    }

    /// <summary>当前监视文件夹列表快照（null 偏好＝空列表）。</summary>
    private List<string> CurrentLauncherDiscoveryFolders() =>
        _host.CurrentPreferences.LauncherDiscoveryFolders?.ToList() ?? [];

    /// <summary>文件夹行构建（代码构建行，画刷经动态资源引用——主题活切换跟随，
    /// 契约 #24/#28 同族）。移除钮直接提交偏好。</summary>
    private void RebuildLauncherDiscoveryFolderRows()
    {
        LauncherDiscoveryFolderList.Children.Clear();
        foreach (var folder in CurrentLauncherDiscoveryFolders())
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                continue;
            }

            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var path = new TextBlock
            {
                Text = folder,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = folder
            };
            path.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
            Grid.SetColumn(path, 0);
            var remove = new Button
            {
                Content = "移除",
                FontSize = 12,
                Height = 28,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)FindResource("ThemedActionButtonStyle")
            };
            System.Windows.Automation.AutomationProperties.SetName(remove, $"移除监视文件夹 {folder}");
            remove.Click += (_, _) => ApplyLauncherDiscoveryFolders(
                CurrentLauncherDiscoveryFolders()
                    .Where(entry => !string.Equals(entry, folder, StringComparison.OrdinalIgnoreCase))
                    .ToList());
            Grid.SetColumn(remove, 1);
            row.Children.Add(path);
            row.Children.Add(remove);
            LauncherDiscoveryFolderList.Children.Add(row);
        }
    }

    private void LauncherDiscoveryFolderBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择监视文件夹" };
        if (dialog.ShowDialog(this) == true)
        {
            LauncherDiscoveryFolderInput.Text = dialog.FolderName;
        }
    }

    private void LauncherDiscoveryFolderAdd_Click(object sender, RoutedEventArgs e)
    {
        var raw = LauncherDiscoveryFolderInput.Text.Trim();
        string? candidate = null;
        string? error = null;
        if (raw.Length == 0)
        {
            error = "请输入或选择一个文件夹路径。";
        }
        else if (!Path.IsPathFullyQualified(raw))
        {
            error = $"不是完整路径：{raw}";
        }
        else if (!Directory.Exists(raw))
        {
            error = $"文件夹不存在：{raw}";
        }
        else
        {
            try
            {
                candidate = Path.GetFullPath(raw).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException)
            {
                error = $"路径无效：{raw}";
            }
        }

        if (error is null && candidate is not null)
        {
            if (CurrentLauncherDiscoveryFolders().Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                error = "该文件夹已在监视列表中。";
            }
        }

        if (error is not null || candidate is null)
        {
            ShowLauncherDiscoveryFolderStatus(error ?? "路径无效。");
            return;
        }

        ApplyLauncherDiscoveryFolders([.. CurrentLauncherDiscoveryFolders(), candidate]);
        LauncherDiscoveryFolderInput.Text = string.Empty;
        ClearLauncherDiscoveryFolderStatus();
    }

    private void ApplyLauncherDiscoveryFolders(List<string> folders)
    {
        _host.ApplyPreferences(_host.CurrentPreferences with
        {
            LauncherDiscoveryFolders = folders
        });
        RebuildLauncherDiscoveryFolderRows();
    }

    private void SetLauncherDiscoveryInputAvailability(bool enabled)
    {
        LauncherDiscoveryFolderInput.IsEnabled = enabled;
        LauncherDiscoveryFolderBrowseButton.IsEnabled = enabled;
        LauncherDiscoveryFolderAddButton.IsEnabled = enabled;
        LauncherDiscoveryFolderList.IsEnabled = enabled;
        foreach (var child in LauncherDiscoveryFolderList.Children.OfType<Grid>())
        {
            foreach (var element in child.Children.OfType<UIElement>())
            {
                element.IsEnabled = enabled;
            }
        }
    }

    private void ShowLauncherDiscoveryFolderStatus(string message)
    {
        LauncherDiscoveryFolderStatus.Text = message;
        LauncherDiscoveryFolderStatus.Visibility = Visibility.Visible;
    }

    private void ClearLauncherDiscoveryFolderStatus()
    {
        LauncherDiscoveryFolderStatus.Text = string.Empty;
        LauncherDiscoveryFolderStatus.Visibility = Visibility.Collapsed;
    }

    private void AutoCleanupToggle_Checked(object sender, RoutedEventArgs e) =>
        ApplyAutoCleanupPreference(enabled: true);

    private void AutoCleanupToggle_Unchecked(object sender, RoutedEventArgs e) =>
        ApplyAutoCleanupPreference(enabled: false);

    private void ApplyAutoCleanupPreference(bool enabled)
    {
        if (_isSynchronizingControls)
        {
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with
        {
            AutoCleanupEnabled = enabled
        });
        // 总开关关闭时保留时长下拉置灰；开启恢复（窗口每次打开都重建，初值在构造同步块）。
        AutoCleanupRetentionComboBox.IsEnabled = enabled;
    }

    private void AutoCleanupRetentionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizingControls ||
            AutoCleanupRetentionComboBox.SelectedItem is not ComboBoxItem { Tag: AutoCleanupRetention retention })
        {
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with { AutoCleanupRetention = retention });
    }

    private void AutoUpdateCheckToggle_Checked(object sender, RoutedEventArgs e) =>
        ApplyAutoUpdateCheckPreference(enabled: true);

    private void AutoUpdateCheckToggle_Unchecked(object sender, RoutedEventArgs e) =>
        ApplyAutoUpdateCheckPreference(enabled: false);

    private void ApplyAutoUpdateCheckPreference(bool enabled)
    {
        if (_isSynchronizingControls)
        {
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with
        {
            AutoUpdateCheckEnabled = enabled
        });
    }

    private void TrashLeftClickComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizingControls ||
            TrashLeftClickComboBox.SelectedItem is not ComboBoxItem { Tag: TrashNoSelectionLeftClickAction action })
        {
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with { TrashNoSelectionLeftClick = action });
    }

    private void TrashRightClickComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizingControls ||
            TrashRightClickComboBox.SelectedItem is not ComboBoxItem { Tag: TrashNoSelectionRightClickAction action })
        {
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with { TrashNoSelectionRightClick = action });
    }

    // ---- 启动器列数策略（契约 #30 修订）：经 ApplyPreferences 唯一管线即时生效——
    // 主窗在管线内把策略推给瓦片面板并原子持久化。----

    private void LauncherColumnStrategyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizingControls ||
            LauncherColumnStrategyComboBox.SelectedItem is not ComboBoxItem { Tag: LauncherColumnStrategy strategy })
        {
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with { LauncherColumnStrategy = strategy });
    }

    // ---- AI 设置（1.27.0，DeepSeek 接入地基）----

    private void DeepSeekKeyBox_LostFocus(object sender, RoutedEventArgs e) => CommitDeepSeekKey();

    private void DeepSeekKeyPlainBox_LostFocus(object sender, RoutedEventArgs e) => CommitDeepSeekKey();

    // ---- AI 整理总开关：关＝Key 卡与整理提示词卡整卡置灰（IsEnabled 链式作用于
    // 输入框、显示/隐藏、测试连接、门户链接与提示词编辑），已存 Key 与自定义提示词
    // 原样保留（只停用不删除）；持久化经 ApplyPreferences 唯一管线即时生效
    // （契约 #5），主窗在管线内同步喂 VM——卡片 AI 钮与整理入口随之消失/恢复。
    // 开关状态在构造同步段回显（重开窗口即见上次选择）。----

    private void AiOrganizeEnabledToggle_Checked(object sender, RoutedEventArgs e) =>
        ApplyAiOrganizeEnabledPreference(enabled: true);

    private void AiOrganizeEnabledToggle_Unchecked(object sender, RoutedEventArgs e) =>
        ApplyAiOrganizeEnabledPreference(enabled: false);

    private void ApplyAiOrganizeEnabledPreference(bool enabled)
    {
        if (_isSynchronizingControls)
        {
            return;
        }

        SetAiOrganizeAreasAvailability(enabled, _host.CurrentPreferences.AiImageNamingEnabled);
        _host.ApplyPreferences(_host.CurrentPreferences with { AiOrganizeEnabled = enabled });
    }

    // ---- AI 识图命名（契约 #61，实验性默认关，独立于 AI 整理开关）：经
    // ApplyPreferences 唯一管线即时生效＋原子持久化（契约 #5 同路；调度器门控
    // 与翻转补扫由 MainWindow 侧 c2 管线承接）；开关状态在构造同步段回显。----

    private void AiImageNamingToggle_Checked(object sender, RoutedEventArgs e) =>
        ApplyAiImageNamingPreference(enabled: true);

    private void AiImageNamingToggle_Unchecked(object sender, RoutedEventArgs e) =>
        ApplyAiImageNamingPreference(enabled: false);

    private void ApplyAiImageNamingPreference(bool enabled)
    {
        if (_isSynchronizingControls)
        {
            return;
        }

        SetAiOrganizeAreasAvailability(_host.CurrentPreferences.AiOrganizeEnabled, enabled);
        _host.ApplyPreferences(_host.CurrentPreferences with { AiImageNamingEnabled = enabled });
    }

    // ---- 实验性（首个先例，2026-11-03）：记录 Markdown 阅读视图。经 ApplyPreferences
    // 唯一管线即时生效＋原子持久化（契约 #5 同路）；开关状态在构造同步段回显。----

    private void ReviewMarkdownViewToggle_Checked(object sender, RoutedEventArgs e) =>
        ApplyReviewMarkdownViewPreference(enabled: true);

    private void ReviewMarkdownViewToggle_Unchecked(object sender, RoutedEventArgs e) =>
        ApplyReviewMarkdownViewPreference(enabled: false);

    private void ApplyReviewMarkdownViewPreference(bool enabled)
    {
        if (_isSynchronizingControls)
        {
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with { ReviewMarkdownViewEnabled = enabled });
    }

    // ---- 实验性第三项（契约 #58）：图片拖出为文件。经 ApplyPreferences 唯一
    // 管线即时生效＋原子持久化（契约 #5 同路）；开关状态在构造同步段回显。----

    private void DragOutAsFileToggle_Checked(object sender, RoutedEventArgs e) =>
        ApplyDragOutAsFilePreference(enabled: true);

    private void DragOutAsFileToggle_Unchecked(object sender, RoutedEventArgs e) =>
        ApplyDragOutAsFilePreference(enabled: false);

    private void ApplyDragOutAsFilePreference(bool enabled)
    {
        if (_isSynchronizingControls)
        {
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with { DragOutAsFileEnabled = enabled });
    }

    // ---- 实验性第四项（契约 #62）：子分组轨点击切换（悬停误触治理）。经
    // ApplyPreferences 唯一管线即时生效＋原子持久化（契约 #5 同路）；开关状态
    // 在构造同步段回显。----

    private void SubgroupRailClickSwitchToggle_Checked(object sender, RoutedEventArgs e) =>
        ApplySubgroupRailClickSwitchPreference(enabled: true);

    private void SubgroupRailClickSwitchToggle_Unchecked(object sender, RoutedEventArgs e) =>
        ApplySubgroupRailClickSwitchPreference(enabled: false);

    private void ApplySubgroupRailClickSwitchPreference(bool enabled)
    {
        if (_isSynchronizingControls)
        {
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with { SubgroupRailClickSwitchEnabled = enabled });
    }

    // ---- 实验性第二项（契约 #57）：分类内子分组三态。经 ApplyPreferences 唯一
    // 管线即时生效＋原子持久化（契约 #5 同路）；选中态在构造同步段按 Tag 回显。----

    private void SubgroupModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizingControls ||
            SubgroupModeComboBox.SelectedItem is not ComboBoxItem { Tag: SubgroupDisplayMode mode })
        {
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with { SubgroupDisplay = mode });
        // 顺序节门控联动（契约 #57 修订）：即时生效后同步置灰/解灰。
        SetSubgroupOrderAvailability(mode);
    }

    /// <summary>
    /// AI 页置灰联动（契约 #29 修订，2026-11-07）：Key 是两 AI 功能的共享资源，
    /// 任一启用即可填写；整理提示词只服务 AI 整理；识图命名卡恒可用（自身开关
    /// 独立于整理总开关）。两开关事件与构造回显都会在此刷新。
    /// </summary>
    private void SetAiOrganizeAreasAvailability(bool organizeEnabled, bool imageNamingEnabled)
    {
        AiKeyCard.IsEnabled = organizeEnabled || imageNamingEnabled;
        AiPromptCard.IsEnabled = organizeEnabled;
    }

    /// <summary>
    /// Key 的生效点是失焦（对齐卡片就地编辑失焦提交先例）：非空提交密文，空输入清除（存 null）；
    /// 明文只在内存中出现，存储侧只落密文。加密失败（DPAPI 异常）就地提示且不落任何值。
    /// </summary>
    private void CommitDeepSeekKey()
    {
        if (_isSynchronizingControls)
        {
            return;
        }

        var plaintext = ReadDeepSeekKeyInput();
        string? protectedKey;
        try
        {
            protectedKey = string.IsNullOrEmpty(plaintext)
                ? null
                : _host.SecretProtector.Protect(plaintext);
        }
        catch (Exception exception) when (exception is CryptographicException or IOException)
        {
            ShowDeepSeekStatus("API Key 加密失败，本次未保存，请重试。");
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with { DeepSeekApiKeyProtected = protectedKey });
    }

    private void DeepSeekKeyVisibilityButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_isDeepSeekKeyVisible)
        {
            DeepSeekKeyBox.Password = DeepSeekKeyPlainBox.Text;
            DeepSeekKeyPlainBox.Visibility = Visibility.Collapsed;
            DeepSeekKeyBox.Visibility = Visibility.Visible;
            DeepSeekKeyVisibilityButton.Content = "显示";
        }
        else
        {
            DeepSeekKeyPlainBox.Text = DeepSeekKeyBox.Password;
            DeepSeekKeyBox.Visibility = Visibility.Collapsed;
            DeepSeekKeyPlainBox.Visibility = Visibility.Visible;
            DeepSeekKeyVisibilityButton.Content = "隐藏";
        }

        _isDeepSeekKeyVisible = !_isDeepSeekKeyVisible;
    }

    private async void DeepSeekTestButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_isTestingDeepSeekConnection)
        {
            return;
        }

        _isTestingDeepSeekConnection = true;
        DeepSeekTestButton.IsEnabled = false;
        DeepSeekTestButton.Content = "测试中…";
        try
        {
            // 直接测当前输入框内容（含未保存的新输入）；探活为 GET /models
            // （零 token 消耗），成功文案模型名恒为客户端默认（deepseek-flash，
            // 契约 #28），无选择状态。
            var result = await _host.TestDeepSeekConnectionAsync(ReadDeepSeekKeyInput());
            ShowDeepSeekStatus(result.Message, isError: !result.IsSuccess);
        }
        catch (OperationCanceledException)
        {
            // 宿主/客户端未来若引入可取消令牌：取消不按失败反馈，只恢复按钮。
        }
        catch (Exception)
        {
            // 纯崩溃护栏：客户端边界已把网络阶段全部映射为结果，常规失败不经过
            // 这里；仅防 async void 的未知异常击穿为进程崩溃，不带任何请求细节。
            ShowDeepSeekStatus("测试失败，请重试。");
        }
        finally
        {
            _isTestingDeepSeekConnection = false;
            DeepSeekTestButton.IsEnabled = true;
            DeepSeekTestButton.Content = "测试连接";
        }
    }

    private string ReadDeepSeekKeyInput() => (_isDeepSeekKeyVisible
        ? DeepSeekKeyPlainBox.Text
        : DeepSeekKeyBox.Password).Trim();

    private void ShowDeepSeekStatus(string message, bool isError = true)
    {
        DeepSeekStatusText.Text = message;
        DeepSeekStatusText.Foreground = (System.Windows.Media.Brush)FindResource(
            isError ? "DangerBrush" : "AccentBrush");
        DeepSeekStatusText.Visibility = Visibility.Visible;
    }

    // ---- AI 整理提示词（1.52.0，契约 #29 修订）----

    private void AiOrganizePromptBox_LostFocus(object sender, RoutedEventArgs e) =>
        CommitAiOrganizePrompt();

    private void AiOrganizePromptBox_TextChanged(object sender, TextChangedEventArgs e) =>
        UpdateAiOrganizePromptState();

    private void AiOrganizePromptFillDefaultButton_Click(object sender, RoutedEventArgs e)
    {
        // 点击已先触发编辑框失焦提交（若持有焦点）；以默认全文为微调起点填入
        // 框内——填入≠生效，保存点仍是下一次失焦，用户可先改再保存。
        e.Handled = true;
        AiOrganizePromptBox.Text = AiOrganizePrompt.SystemPrompt;
        AiOrganizePromptBox.CaretIndex = AiOrganizePromptBox.Text.Length;
    }

    private void AiOrganizePromptResetButton_Click(object sender, RoutedEventArgs e)
    {
        // 点击已先触发失焦提交（即便刚保存了旧值也会被下一步覆盖）：清空并
        // 立即提交 null——「恢复默认」不需要再失焦一次才生效，最终态恒为 null。
        e.Handled = true;
        AiOrganizePromptBox.Text = string.Empty;
        CommitAiOrganizePrompt();
    }

    /// <summary>
    /// 生效点是失焦（对齐 Key 框失焦提交先例）：Trim 后空白＝null（内置默认，
    /// 绝不发送空白 system）；超长（1 万字）就地红字提示且不保存；否则保存
    /// Trim 后文本。生效值未变化时跳过提交（按钮可重复点击，不产生无谓写盘）。
    /// </summary>
    private void CommitAiOrganizePrompt()
    {
        if (_isSynchronizingControls)
        {
            return;
        }

        var trimmed = AiOrganizePromptBox.Text.Trim();
        if (trimmed.Length > AiOrganizePrompt.MaxOverrideLength)
        {
            AiOrganizePromptErrorText.Text = "整理提示词超过 1 万字，未保存。";
            AiOrganizePromptErrorText.Visibility = Visibility.Visible;
            return;
        }

        AiOrganizePromptErrorText.Visibility = Visibility.Collapsed;
        var effective = trimmed.Length == 0 ? null : trimmed;
        if (string.Equals(
                effective,
                _host.CurrentPreferences.AiOrganizeSystemPromptOverride,
                StringComparison.Ordinal))
        {
            return;
        }

        _host.ApplyPreferences(
            _host.CurrentPreferences with { AiOrganizeSystemPromptOverride = effective });
    }

    /// <summary>状态显式可见（禁止静默回退）：空框＝内置默认，非空＝已自定义。</summary>
    private void UpdateAiOrganizePromptState() =>
        AiOrganizePromptStateText.Text = string.IsNullOrWhiteSpace(AiOrganizePromptBox.Text)
            ? "当前使用内置默认。"
            : "已自定义。";

    private void OpenDataDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (string.IsNullOrWhiteSpace(_host.DataDirectory))
        {
            return;
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(_host.DataDirectory)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ShowDataDirectoryStatus("无法打开数据目录。");
        }
    }

    /// <summary>
    /// 更改数据目录：选文件夹 → 预检（形状/可写/卷空间/目标不存在）→ 确认对话框
    /// （源→目标、数据体积、自动重启、旧目录处置）→ 迁移并自动重启。迁移期间
    /// 锁定设置内容并显示进度，失败恢复原状并提示。
    /// </summary>
    private async void ChangeDataDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_isChangingDataDirectory)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择新位置（将在其中创建「悬浮中转站」文件夹）"
        };
        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            return;
        }

        var preview = _host.DescribeDataDirectoryChange(dialog.FolderName);
        if (!preview.Valid || preview.TargetDataDirectory is null)
        {
            ShowDataDirectoryStatus(preview.Refusal ?? "无法使用所选位置。");
            return;
        }

        var confirmed = MessageBox.Show(
            this,
            $"内容将迁移到新位置：\n\n旧位置：{_host.DataDirectory}\n新位置：{preview.TargetDataDirectory}\n共约 {FormatByteSize(preview.SizeBytes)}\n\n迁移完成后应用会自动重启；旧位置确认安全后，会在下次启动时自动清理。\n\n现在开始？",
            "更改内容存储位置",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirmed != MessageBoxResult.Yes)
        {
            return;
        }

        _isChangingDataDirectory = true;
        SettingsScrollHost.IsEnabled = false;
        ShowDataDirectoryStatus("正在准备迁移…");
        try
        {
            var progress = new Progress<DataDirectoryCopyProgress>(update =>
            {
                if (IsLoaded)
                {
                    ShowDataDirectoryStatus(
                        $"正在迁移内容… {FormatByteSize(update.CopiedBytes)} / {FormatByteSize(update.TotalBytes)}");
                }
            });
            var result = await _host.ChangeDataDirectoryAsync(dialog.FolderName, progress);
            if (result.ExitApplication)
            {
                if (result.Error is not null)
                {
                    MessageBox.Show(
                        this,
                        result.Error,
                        ProductIdentity.DisplayName,
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                return;
            }

            ShowDataDirectoryStatus(result.Error ?? "迁移未完成，原目录未做改动。");
        }
        finally
        {
            _isChangingDataDirectory = false;
            if (IsLoaded)
            {
                SettingsScrollHost.IsEnabled = true;
                HideDataDirectoryStatus();
            }
        }
    }

    private void ShowDataDirectoryStatus(string message)
    {
        DataDirectoryStatusText.Text = message;
        DataDirectoryStatusText.Visibility = Visibility.Visible;
    }

    private void HideDataDirectoryStatus()
    {
        DataDirectoryStatusText.Text = string.Empty;
        DataDirectoryStatusText.Visibility = Visibility.Collapsed;
    }

    private static string FormatByteSize(long bytes) => bytes switch
    {
        < 0 => "0 B",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:F0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024):F1} MB",
        _ => $"{bytes / (1024d * 1024 * 1024):F2} GB"
    };

    /// <summary>
    /// 填充插件区块：每个插件一行（名称/版本 + 描述或加载错误 + 启用开关）。
    /// 清单非法的插件保留在列表中并显示原因，开关禁用。
    /// </summary>
    private void PopulatePluginSection()
    {
        var catalog = _host.PluginCatalog;
        if (catalog is null)
        {
            PluginDirectoryText.Text = string.Empty;
            OpenPluginDirectoryButton.Visibility = Visibility.Collapsed;
            PluginDirectoryNoteText.Visibility = Visibility.Collapsed;
            PluginHelpText.Text = "插件系统在本次运行中不可用。";
            return;
        }

        // 插件目录结构性内嵌为数据根 plugins（随数据目录一起移动），只读展示。
        PluginDirectoryText.Text = catalog.UserPluginsDirectory;
        foreach (var entry in catalog.Entries)
        {
            PluginListPanel.Children.Add(CreatePluginRow(entry));
        }

        if (catalog.Entries.Count == 0)
        {
            PluginHelpText.Text = "未发现插件。把插件文件夹放入下方目录，点「重新扫描」即可。";
            return;
        }

        PluginHelpText.Text = "插件默认关闭，启用立即生效；同名用户插件覆盖内建版本。";
    }

    private Border CreatePluginRow(PluginEntry entry)
    {
        var content = new StackPanel();

        var row = new Grid { Background = System.Windows.Media.Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 卡片行语言与「标签顺序」行同族：Card 底 + Border 描边 + 圆角 4，颜色走主题键；
        // 画刷经动态资源引用解析（对齐全仓 DynamicResource 语义），主题活切换即跟随。
        var surface = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Child = content
        };
        surface.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        surface.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        var title = entry.Manifest is { } manifest
            ? $"{manifest.Name}  {manifest.Version}"
            : $"{System.IO.Path.GetFileName(entry.Directory)}（无法加载）";
        var detail = entry.Manifest is { } valid
            ? (valid.Description ?? (valid.Kind == PluginKind.TextCleaner ? "文本整理插件" : "文本动作插件"))
            : entry.LoadError ?? "清单无效。";

        var textPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) };
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal };
        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        titleBlock.SetResourceReference(TextElement.ForegroundProperty, "PrimaryTextBrush");
        titleLine.Children.Add(titleBlock);
        // 来源徽章（1.51.0）：内置/用户覆盖；ToolTip=插件实际目录（含用户覆盖路径）。
        var badge = new TextBlock
        {
            Text = entry.Origin == PluginOrigin.User ? "用户覆盖" : "内置",
            FontSize = 11,
            Margin = new Thickness(6, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = entry.Directory
        };
        badge.SetResourceReference(TextElement.ForegroundProperty, "SecondaryTextBrush");
        titleLine.Children.Add(badge);
        textPanel.Children.Add(titleLine);
        var detailBlock = new TextBlock
        {
            Text = detail,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        // 描述键随行状态选定（清单非法=DangerBrush），值经动态引用随字典换装。
        detailBlock.SetResourceReference(
            TextElement.ForegroundProperty,
            entry.Manifest is null ? "DangerBrush" : "SecondaryTextBrush");
        textPanel.Children.Add(detailBlock);
        Grid.SetColumn(textPanel, 0);
        row.Children.Add(textPanel);

        var toggle = new CheckBox
        {
            Style = TryFindResource("ToggleSwitchStyle") as Style,
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = entry.Enabled,
            IsEnabled = entry.Manifest is not null,
            Tag = entry.Id
        };
        System.Windows.Automation.AutomationProperties.SetName(
            toggle,
            $"{title}插件开关");
        toggle.Checked += PluginToggle_StateChanged;
        toggle.Unchecked += PluginToggle_StateChanged;
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);

        content.Children.Add(row);
        if (entry.Manifest is { Kind: PluginKind.TextActions } actionsManifest)
        {
            content.Children.Add(BuildPluginActionsPanel(actionsManifest, entry));
        }

        return surface;
    }

    /// <summary>
    /// text-actions 插件行的动作区：逐动作标题＋选型摘要＋声明式设置字段（改动
    /// 即时经宿主原子持久化并重建管线）＋「试运行…」入口（诊断对话框复用正式
    /// 加载器口径）。所有画刷走动态资源，主题活切换即跟随。
    /// </summary>
    private FrameworkElement BuildPluginActionsPanel(PluginManifest manifest, PluginEntry entry)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var action in manifest.Actions!)
        {
            var actionPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

            var headerLine = new Grid { Background = System.Windows.Media.Brushes.Transparent };
            headerLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var actionTitle = new TextBlock
            {
                Text = $"动作：{action.Title}（{(action.Selection == PluginActionSelection.Multi ? "多选" : "单选")}）",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            actionTitle.SetResourceReference(TextElement.ForegroundProperty, "SecondaryTextBrush");
            Grid.SetColumn(actionTitle, 0);
            headerLine.Children.Add(actionTitle);
            var trialButton = new Button
            {
                Style = TryFindResource("ThemedActionButtonStyle") as Style,
                Content = "试运行…",
                FontSize = 11,
                Padding = new Thickness(8, 2, 8, 2),
                ToolTip = "用当前设置试跑该动作（不改卡片内容）",
                Tag = (entry.Id, action.Id)
            };
            System.Windows.Automation.AutomationProperties.SetName(trialButton, $"{action.Title}试运行"); trialButton.Click += PluginActionTrialButton_Click;
            Grid.SetColumn(trialButton, 1);
            headerLine.Children.Add(trialButton);
            actionPanel.Children.Add(headerLine);

            foreach (var setting in action.Settings)
            {
                actionPanel.Children.Add(BuildPluginSettingRow(entry, action, setting));
            }

            panel.Children.Add(actionPanel);
        }

        return panel;
    }

    /// <summary>单个声明式设置字段的标签＋控件行；值经宿主按插件 id 原子持久化。</summary>
    private FrameworkElement BuildPluginSettingRow(PluginEntry entry, PluginActionDefinition action, PluginActionSetting setting)
    {
        var line = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var overrides = _host.PluginCatalog?.GetActionSettingOverrides(entry.Id, action.Id)
            ?? new Dictionary<string, string>();
        var currentValue = action.ResolveSettingValue(overrides, setting.Key);

        var label = new TextBlock
        {
            Text = setting.Label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        label.SetResourceReference(TextElement.ForegroundProperty, "PrimaryTextBrush");
        Grid.SetColumn(label, 0);
        line.Children.Add(label);

        FrameworkElement control;
        switch (setting.Type)
        {
            case PluginSettingType.Bool:
                {
                    var toggle = new CheckBox
                    {
                        Style = TryFindResource("ToggleSwitchStyle") as Style,
                        IsChecked = string.Equals(currentValue, "true", StringComparison.Ordinal),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Tag = (entry.Id, action.Id, setting.Key)
                    };
                    toggle.Checked += PluginSetting_ValueChanged;
                    toggle.Unchecked += PluginSetting_ValueChanged;
                    control = toggle;
                    break;
                }

            case PluginSettingType.Choice:
                {
                    var combo = new ComboBox
                    {
                        Style = TryFindResource("ThemedComboBoxStyle") as Style,
                        ItemsSource = setting.Options,
                        MinWidth = 140,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Tag = (entry.Id, action.Id, setting.Key)
                    };
                    combo.SelectedItem = setting.Options.FirstOrDefault(option => option == currentValue) ?? setting.Options[0];
                    combo.SelectionChanged += PluginSetting_ValueChanged;
                    control = combo;
                    break;
                }

            default:
                {
                    var box = new TextBox
                    {
                        Text = currentValue,
                        MaxLength = 200,
                        MinWidth = 140,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Style = TryFindResource("ThemedTextBoxStyle") as Style,
                        Tag = (entry.Id, action.Id, setting.Key)
                    };
                    box.LostFocus += PluginSetting_ValueChanged;
                    control = box;
                    break;
                }
        }

        Grid.SetColumn(control, 1);
        line.Children.Add(control);
        return line;
    }

    private async void PluginSetting_ValueChanged(object sender, RoutedEventArgs e)
    {
        if (_isSynchronizingControls ||
            sender is not FrameworkElement { Tag: ValueTuple<string, string, string> tag })
        {
            return;
        }

        var (pluginId, actionId, key) = tag;
        var value = sender switch
        {
            CheckBox toggle => (toggle.IsChecked == true).ToString().ToLowerInvariant(),
            ComboBox combo => combo.SelectedItem as string ?? string.Empty,
            TextBox box => box.Text,
            _ => string.Empty
        };

        try
        {
            await _host.SetPluginActionSettingAsync(pluginId, actionId, key, value);
            PluginStatusText.Text = string.Empty;
            PluginStatusText.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            PluginStatusText.Text = "插件设置暂未保存，请稍后重试。";
            PluginStatusText.Visibility = Visibility.Visible;
        }
    }

    private void PluginActionTrialButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: ValueTuple<string, string> tag })
        {
            return;
        }

        var (pluginId, actionId) = tag;
        var trial = new PluginActionTrialWindow(_host, pluginId, actionId);
        trial.Owner = this;
        trial.ShowDialog();
    }

    private async void RescanPluginsButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_host.PluginCatalog is null)
        {
            return;
        }

        RescanPluginsButton.IsEnabled = false;
        try
        {
            await _host.RescanPluginsAsync();
            PluginListPanel.Children.Clear();
            PopulatePluginSection();
            PluginStatusText.Text = string.Empty;
            PluginStatusText.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            PluginStatusText.Text = "重新扫描失败，请稍后重试。";
            PluginStatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            RescanPluginsButton.IsEnabled = true;
        }
    }

    private async void PluginToggle_StateChanged(object sender, RoutedEventArgs e)
    {
        if (_isSynchronizingControls ||
            sender is not CheckBox { Tag: string pluginId } toggle)
        {
            return;
        }

        // await 前读取开关状态，续延不再依赖控件访问。
        var enabled = toggle.IsChecked == true;
        try
        {
            await _host.ApplyPluginEnabledAsync(pluginId, enabled);
            PluginStatusText.Text = string.Empty;
            PluginStatusText.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            PluginStatusText.Text = "插件设置暂未保存，请稍后重试。";
            PluginStatusText.Visibility = Visibility.Visible;
        }
    }

    private void OpenPluginDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var directory = _host.PluginCatalog?.UserPluginsDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            using var _ = Process.Start(new ProcessStartInfo(directory)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            PluginStatusText.Text = "无法打开插件目录。";
            PluginStatusText.Visibility = Visibility.Visible;
        }
    }

    private void RepoLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 打不开浏览器时保持链接可见即可，不再打扰。
        }
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _host.RequestApplicationExit();
    }

    private void WindowShell_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        WindowShell.Clip = WindowShellClip.Create(
            e.NewSize.Width,
            e.NewSize.Height,
            FloatingTransferStation.Design.DesignTokens.DwmCornerRadius);
    }
}
