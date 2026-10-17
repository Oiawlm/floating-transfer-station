namespace FloatingTransferStation.Design;

/// <summary>
/// 设计 token 唯一数值来源。WPF 资源字典引用这些值。
/// 人类可读规范见 docs/design.md；修改任何值必须同步该文档。
/// </summary>
public static class DesignTokens
{
    // 颜色（浅色主题，#RRGGBB）。深色主题按同键位提供第二套值。
    public const string WindowShellHex = "#F7F8FA";
    public const string TabRailHex = "#EFF1F4";
    public const string CardHex = "#FFFFFF";
    public const string BorderHex = "#E2E5EA";
    public const string PrimaryTextHex = "#1D1D1F";
    public const string SecondaryTextHex = "#5F6368";
    public const string AccentHex = "#6D5DFB";
    public const string DangerHex = "#D92D20";
    public const string HeaderHex = "#FBFBFC";
    public const string HoverSurfaceHex = "#F4F5F7";
    public const string HoverBorderHex = "#C9CDD5";
    public const string SelectedCardHex = "#F3F1FF";
    public const string SelectedCardBorderHex = "#8B7CF6";
    public const string ActiveCategoryHex = "#E7E4FF";
    public const string DropTargetHex = "#EAE7FF";
    public const string ScrollThumbHex = "#B8BDC7";
    public const string ScrollThumbHoverHex = "#969CA7";
    public const string StatusSurfaceHex = "#FCFCFD";
    public const string OnAccentHex = "#FFFFFF";

    // Windows Mica 窗口壳：半透明表面浮在 DWM 材质之上；不透明值用于材质不可用时的回退。
    public const string WindowShellTintHex = "#CCF7F8FA";
    public const string WindowShellTintDarkHex = "#CC202021";

    // 颜色（深色主题，#RRGGBB）。
    public const string WindowShellDarkHex = "#202021";
    public const string TabRailDarkHex = "#2B2B2C";
    public const string CardDarkHex = "#2D2D2E";
    public const string BorderDarkHex = "#414143";
    public const string PrimaryTextDarkHex = "#FFFFFF";
    public const string SecondaryTextDarkHex = "#C8C8CA";
    public const string AccentDarkHex = "#9C8CFF";
    public const string DangerDarkHex = "#FF8A7A";
    public const string HeaderDarkHex = "#272728";
    public const string HoverSurfaceDarkHex = "#343436";
    public const string HoverBorderDarkHex = "#505053";
    public const string SelectedCardDarkHex = "#343054";
    public const string SelectedCardBorderDarkHex = "#7A6DF2";
    public const string ActiveCategoryDarkHex = "#3A3560";
    public const string DropTargetDarkHex = "#322E52";
    public const string ScrollThumbDarkHex = "#505054";
    public const string ScrollThumbHoverDarkHex = "#6E6E74";
    public const string StatusSurfaceDarkHex = "#282829";
    public const string OnAccentDarkHex = "#FFFFFF";

    // 动效时长三档（微软 Fluent 官方值）。
    public const int FastDurationMs = 83;
    public const int NormalDurationMs = 167;
    public const int SlowDurationMs = 250;

    // 场景时长。
    public const int PanelExpandContentMs = NormalDurationMs;
    public const int PanelSwitchContentMs = 140;
    public const int ReducedMotionFadeMs = FastDurationMs;
    public const int CategoryRevealMs = 120;
    public const int PanelCollapseExitMs = 200;
    public const int HoverFeedbackMs = NormalDurationMs;
    public const int PressFeedbackMs = FastDurationMs;
    public const int FadeVisibilityMs = 120;
    public const int DeleteFeedbackMs = 120;
    public const int ItemEntranceStaggerMs = 40;

    // 快捷启动（#30）：场景常量不加档——LaunchConfirmMs/ItemLandingMs 直接锚 SlowDurationMs。
    public const int LaunchConfirmMs = SlowDurationMs;
    public const int ItemLandingMs = SlowDurationMs;
    /// <summary>启动后排队收起延迟（r2-M5 有效性协议：到期重验抑制条件）。</summary>
    public const int LaunchCollapseDelayMs = SlowDurationMs;
    /// <summary>单击启动重入门闩（同一时间窗内第二击不重复派发）。</summary>
    public const int LaunchReentryLatchMs = 500;
    /// <summary>瓦片入场交错步进与封顶（M1：min(i,8)×40ms）。</summary>
    public const int TileEntranceStaggerMs = ItemEntranceStaggerMs;
    public const int TileEntranceStaggerCap = 8;
    /// <summary>瓦片按压缩放（#30 §五）。</summary>
    public const double TilePressScale = 0.96;
    /// <summary>瓦片悬停抬升位移（瓦片 −2px，区别于卡片 LiftedOffsetPx −1）。</summary>
    public const double TileHoverLiftPx = -2;

    // 缓动曲线（WPF KeySpline 控制点；入场为减速曲线，出场为加速曲线）。
    public const string EnterEasingKeySpline = "0,0,0,1";
    public const string ExitEasingKeySpline = "1,0,1,1";

    // 圆角。窗壳圆角权威=DwmCornerRadius（DWM 裁剪与内容层自裁剪同值），
    // XAML 资源 ShellCornerRadius 由锁定测试镜像它——不设第二个壳圆角 token。
    public const int DwmCornerRadius = 8;
    public const int HeaderSurfaceCornerRadius = 11;
    public const int CardCornerRadius = 8;
    public const int ControlCornerRadius = 7;
    public const int PinButtonCornerRadius = 6;
    public const int CheckboxCornerRadius = 5;
    public const int ScrollThumbCornerRadius = 2;

    // 间距与位移（像素）。
    public const double ContentEntranceOffsetPx = 6;
    public const double CollapseExitOffsetPx = 24;
    public const int CardInsetPx = 12;
    public const int CardGutterPx = 4;
    public const int CardPaddingPx = 12;

    // 快捷启动瓦片布局（#30 §五；XAML 与代码同源引用，Issue #176 视觉一致性）：
    // 瓦片 84×100、瓦片间沟道 8（XAML Margin 4×4=8/2 两侧）、节距=宽+沟道（列数
    // 公式 clamp(floor((Wc+8)/92),3,6) 与网格宽度 columns*pitch-8 的唯一数值来源）。
    public const double LauncherTileWidthPx = 84;
    public const double LauncherTileHeightPx = 100;
    public const int LauncherTileGutterPx = 8;
    public const double LauncherTilePitchPx = LauncherTileWidthPx + LauncherTileGutterPx;
}
