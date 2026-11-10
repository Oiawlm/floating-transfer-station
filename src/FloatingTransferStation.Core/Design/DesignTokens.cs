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
    /// <summary>拖放目标撤离保持窗（ms）：标签高亮被 DragLeave/轨内死区撤销后，
    /// 经此窗口无重新断言才一次清到位——窗口内 Enter/Over 抖动零回落（契约 #51）。
    /// 取 120 覆盖手抖频率（≥4Hz 的断言间隔都低于它），又不至于让真离开的
    /// 高亮消退有可感知迟滞。</summary>
    public const int DropTargetWithdrawHoldMs = 120;
    /// <summary>边缘 peek 把手屏内保留宽（DIP，R12 契约 #17，Issue #353）：整窗同尺寸
    /// 平移收缩后与工作区交集宽恰等于此值——视觉宽＝命中宽＝屏内保留宽同值（Mica
    /// 不透明窗无透明命中区，三值解耦会在屏内露出内容切边）；窗口左缘自带 DWM 恒定
    /// 圆角即把手左侧圆角（#6 豁免口径见 #17 替换文本）。8-16 经验区间取上沿：全高
    /// 竖条好命中。XAML 把手层宽度是本值的镜像（锁定测试互锁）。DIP→物理换算由
    /// 调用方经 CompositionTarget 一次完成。</summary>
    public const int EdgePeekHandleWidthPx = 16;
    /// <summary>边缘 peek 把手 hover 展开的意图事务时长（ms，R12 契约 #17，Issue #353）：
    /// 布防 → timer → 到期重验指针仍在窗口/把手内才提交展开——悬停意图事务复用 #42
    /// 既有先例（把手悬停展开同族），防扫边误触由事务重验达成，不另设防抖层。</summary>
    public const int EdgePeekExpandDwellMs = 60;
    /// <summary>二维码放大层容器级 container transform（实验性，2026-11-06 工作流
    /// B；2026-11-07 容器级重调）：时长与缓动独立成组——不并入 83/167/250 三档
    /// 体系、也不加第四档（档位制红线），仅本场景专用。口径＝容器级「从哪来到
    /// 哪去」：动画对象为外层整卡容器（含框飞行、方→矩形形变、文字后段浮现、
    /// 缩略图防双影淡出）。数值源：入场曲线 (0.32,0.72,0,1)＝iOS 导航/照片缩放
    /// 转场的非官方测量近似（ionic-framework#19789）；收回曲线 (0.25,0.1,0.25,1)
    /// ＝Apple 公开 kCAMediaTimingFunctionDefault（developer.apple.com/documentation/
    /// quartzcore/camediatimingfunction）；时长与文字编排＝Material container
    /// transform/fade-through（m3.material.io/styles/motion/transitions、
    /// vocab.design/container-transform）：入场 450ms（长档下限）、文字 270ms 起
    /// 180ms 淡入（≈60% 进度、落位时可读）、收回文字 90ms 先隐。实验期以真实
    /// 录屏迭代微调（入场 [380,520]ms、文字起淡 55–70% 进度、收回 [200,320]ms、
    /// 控制点 ±0.05），终值回写本组与契约 #24（批次 2026-11-06-consistency-batch
    /// 工作流 B；2026-11-08 关闭回退重编排补收回交接两常量，批次
    /// 2026-11-07-multi-feature-batch d1）。</summary>
    public const int QrZoomInMs = 450;
    public const int QrZoomOutMs = 250;
    public const string QrZoomInKeySpline = "0.32,0.72,0,1";
    public const string QrZoomOutKeySpline = "0.25,0.1,0.25,1";
    public const int QrTextFadeInBeginMs = 270;
    public const int QrTextFadeInMs = 180;
    public const int QrTextFadeOutMs = 90;
    /// <summary>缩略图入场防双影淡出时长（开层期缩略图自当下值隐没；收回回显
    /// 时点由 QrThumbRevealBeginMs/QrHandoverFadeMs 编排，不用本常量）。</summary>
    public const int QrThumbFadeMs = 150;
    /// <summary>缩略图收回回显起点（ms，自关层起）：QrZoomOutKeySpline 反解 95%
    /// 回程＝x≈0.722×QrZoomOutMs≈180.5ms，取整 180——容器已 ≈1.05× 落位尺寸，
    /// 此前缩略图持隐（对齐落位，防飞行中段双影闪现）。</summary>
    public const int QrThumbRevealBeginMs = 180;
    /// <summary>收回交接段时长（ms）＝QrZoomOutMs−QrThumbRevealBeginMs：卡片
    /// 1→0 与缩略图 0→1 同窗线性交叉，落位点合成存在感恒≈1（无台阶/空窗/双影）。</summary>
    public const int QrHandoverFadeMs = QrZoomOutMs - QrThumbRevealBeginMs;

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

    // 界面字体（2026-10-25 自定义字体全链路；2026-11-02 默认改微软雅黑链）：
    // 默认字体串唯一来源，XAML 资源 InterfaceFontFamily 镜像它（锁定测试互锁）；
    // 链首 Microsoft YaHei UI（Win11 必装、行距更紧凑），回退 Microsoft YaHei
    // （链式回退保证缺失时不断字）。用户自定义经偏好 InterfaceFont（null=默认）
    // 由 InterfaceFontManager 以窗口级槽位覆盖，图标字体（Segoe Fluent Icons）
    // 不随用户字体变化；不随应用分发字体文件。
    public const string InterfaceFontFamily = "Microsoft YaHei UI, Microsoft YaHei";
    /// <summary>卡片正文标称字号（px）：行高推导的字号输入（正文显示/编辑两态共用）。</summary>
    public const double CardTextFontSize = 14;
    /// <summary>行高额外行距（px）：行高推导公式 ceil(CardTextFontSize×LineSpacing＋
    /// 此值) 的常数。默认字体链取链首度量（WPF 对链式族串按链首计 LineSpacing）：
    /// Microsoft YaHei UI 实测 1.27001953125 → ceil(14×1.27001953125＋1)=19，
    /// 与既有 CardTextLineHeight 字面量 19 精确一致（锁定测试互锁：默认字体推导值
    /// =资源现状值；2026-11-02 前 Segoe UI 链推导 20，随默认字体改链同步）。</summary>
    public const double CardTextExtraLeadingPx = 1;
    /// <summary>正文可见行数上限（CardTextPreviewMaxHeight=行高×此值；默认字体 5×19=95）。</summary>
    public const int CardTextMaxVisibleLines = 5;
    /// <summary>备注可见行数上限（CardNoteMaxHeight=行高×此值；默认字体 2×19=38）。</summary>
    public const int CardNoteMaxVisibleLines = 2;

    // 记录阅读视图（实验性，2026-11-03）：排版 token——正文字号沿用 CardTextFontSize
    //（不设第二份 14）；标题字号为阅读档位阶梯（H1–H3 显式 token，H4–H6＝正文字号
    // 加 SemiBold 权重区分）；颜色不新增——全部走主题字典语义键（正文/引用＝
    // PrimaryTextBrush，链接＝AccentBrush，代码块底＝HoverSurfaceBrush，描边＝
    // BorderBrush），浅深两主题随整本替换即时切换。代码字体是独立功能来源
    //（同图标字体先例，不随用户界面字体变化；Win11 必装 Cascadia Mono，回退
    // Consolas），不违反 #48「界面字体字面量唯一来源」——它不是界面字体。
    public const double ReadingHeading1FontSize = 20;
    public const double ReadingHeading2FontSize = 17;
    public const double ReadingHeading3FontSize = 15;
    public const string ReadingCodeFontFamily = "Cascadia Mono, Consolas";
    /// <summary>阅读视图块间垂直间距（段落/代码块/引用/列表/分隔线共用）。</summary>
    public const double ReadingBlockGapPx = 6;
    /// <summary>代码块内边距（背景与描边围出的呼吸空间）。</summary>
    public const double ReadingCodePaddingPx = 8;
    /// <summary>块引用左缘竖条宽度（BorderBrush）。</summary>
    public const double ReadingQuoteBarPx = 3;

    // 圆角。窗壳圆角权威=DwmCornerRadius（DWM 裁剪与内容层自裁剪同值），
    // XAML 资源 ShellCornerRadius 由锁定测试镜像它——不设第二个壳圆角 token。
    // 两档阶梯（#30 修订 2026-10-18，Issue #186 D9）：控件级 4（输入框/按钮/下拉/
    // 记录编辑框/行内编辑器）、表面级 8（卡/瓦片/弹窗壳/主壳——CardCornerRadius
    // 同源收敛）；识别元素（选择环 r5 等）保留原值不入阶梯。
    public const int DwmCornerRadius = 8;
    public const int HeaderSurfaceCornerRadius = 11;
    public const int ControlCornerRadiusPx = 4;
    public const int SurfaceCornerRadiusPx = 8;
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

    // 快捷启动瓦片布局（#30 §五；2026-10-18 修订＝拉伸填充单一几何源，Issue #186）：
    // 瓦片高 100、沟道 8、宽 84=下限（域内构造保证）、节距=下限宽+沟道=92（最小单元格，
    // n 列最小视口=n×84+(n−1)×8 恰在此处瓦片宽=下限）。列数/瓦片宽唯一计算来源=
    // LauncherGridMath（列数 clamp(floor((A+8)/92),3,6)、瓦片宽=(A−8(n−1))/n、行满排
    // 总宽精确等于视口）；网格承载=LauncherTilePanel 自行排版，不再有「公式限宽＋
    // WrapPanel 再量化」双口径与 ScrollChanged 自引用回写（Issue #162 回归结构消灭）。
    public const double LauncherTileWidthPx = 84;
    public const double LauncherTileHeightPx = 100;
    public const int LauncherTileGutterPx = 8;
    public const double LauncherTilePitchPx = LauncherTileWidthPx + LauncherTileGutterPx;
    public const int LauncherTileMinColumns = 3;
    public const int LauncherTileMaxColumns = 6;
    /// <summary>启动瓦片视口顶/底内衬（px，W6 顶排圆角裁切修复 2026-11-03，需求 8）：
    /// 瓦片动画全为 RenderTransform（悬停上浮 −2、确认脉冲 1.06 放大上下各溢 ≈3），
    /// 溢出越过滚动视口缘即被 ScrollContentPresenter 裁剪——首行/末行没有行间沟道
    /// 缓冲，顶/底圆角被切（其余行有 8px 沟道吸收）。LauncherTilePanel 在内容顶/底
    /// 各留此内衬（随内容滚动，恰在列表自然停位提供余量）；取值 ≥ 上浮 2＋脉冲 3＋
    /// 描边余量，取整对齐沟道 8 的节奏。面板宽仍=视口（拉伸填充不变，#23/#30 不回退）。</summary>
    public const int LauncherTileViewportInsetPx = 8;
}
