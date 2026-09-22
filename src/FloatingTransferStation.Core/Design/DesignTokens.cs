namespace FloatingTransferStation.Design;

/// <summary>
/// 双端共用的设计 token 唯一数值来源。WPF 资源字典与 Mac 端常量镜像这些值，
/// 相等性由 Windows 端 STA 测试锁定（Mac 端直接引用本类常量，编译期一致）。
/// 人类可读规范见 docs/design.md；修改任何值必须同步该文档与两端镜像。
/// </summary>
public static class DesignTokens
{
    // 颜色（浅色主题，#RRGGBB）。深色主题字典在引入时按同键位补充。
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

    // 缓动曲线（WPF KeySpline 控制点；入场为减速曲线，出场为加速曲线）。
    public const string EnterEasingKeySpline = "0,0,0,1";
    public const string ExitEasingKeySpline = "1,0,1,1";

    // 圆角。
    public const int ShellCornerRadius = 12;
    public const int HeaderSurfaceCornerRadius = 11;
    public const int CardCornerRadius = 8;
    public const int ControlCornerRadius = 7;
    public const int PinButtonCornerRadius = 6;
    public const int CheckboxCornerRadius = 5;
    public const int ScrollThumbCornerRadius = 2;

    // 间距与位移（像素）。
    public const double ContentEntranceOffsetPx = 6;
    public const int CardInsetPx = 12;
    public const int CardGutterPx = 4;
    public const int CardPaddingPx = 12;
}
