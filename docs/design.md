# 设计规范（Design Tokens）

> 唯一数值来源是共享核心 `src/FloatingTransferStation.Core/Design/DesignTokens.cs`。WPF 资源字典（`src/FloatingTransferStation/Resources/MainWindowStyles.xaml`）引用这些值；Windows 端 STA 测试（`MainWindowInteractionTests.DesignTokens.cs` 等）锁定 WPF 镜像与 Core 相等。本文是**映射叙述**（哪个场景用哪枚 token、为什么），不立第二数值权威。修改任何值必须同步本文件与 Core。
>
> 历史注记：2026-10-08 起 Mac 端已停止并从仓库移除，涉及另一端镜像的历史表述随之失效；带日期的历史记录不改写。2026-11-08 视觉语言体系化收敛（h1）把全文扩为六章：配色体系／动效全景／按钮与交互状态／圆角／排版与布局／动效原则与反例豁免。

设计基线为 Windows 11 Fluent（微软官方动效规范），参考调研见 `docs/research/2026-09-22-ui-motion-resources.md`（维护者本地资料，不入库）。

## 一、配色体系（色板与语义分工）

强调色为紫 `#6D5DFB` 及其派生色族（2026-09-22 决策：保留既有识别度）。**语义分工**：中性面（窗壳/轨道/头部/卡片底）只走灰阶层——`WindowShell→TabRail→Header/Card` 由浅入深的表面层级；`Accent` 只承担「选中/活动/指示/插入」语义（焦点描边、置顶指示、选中环、拖放目标、链接、成功反馈），不作装饰底色；`Danger` 只承担破坏性语义（清空/删除/退出/失败反馈）；`SecondaryText` 兼任次级图标前景与灰态环（#56 复用，非新 token）。文案反馈色规则：成功＝`AccentBrush`（DeepSeek 状态与插件试运行统一，2026-11-08）、失败＝`DangerBrush`、中性提示＝`SecondaryTextBrush`。

| Token | 值 | 用途 |
|---|---|---|
| WindowShellHex | `#F7F8FA` | 窗壳背景 |
| TabRailHex | `#EFF1F4` | 分类轨道背景 |
| CardHex | `#FFFFFF` | 卡片、编辑框背景 |
| BorderHex | `#E2E5EA` | 通用描边、窗壳描边 |
| PrimaryTextHex | `#1D1D1F` | 主文字 |
| SecondaryTextHex | `#5F6368` | 次文字、图标默认前景 |
| AccentHex | `#6D5DFB` | 强调色（焦点/置顶/选中/插入指示器） |
| DangerHex | `#D92D20` | 清空按钮 |
| HeaderHex | `#FBFBFC` | 头部表面、Compact 弹层 |
| HoverSurfaceHex | `#F4F5F7` | 悬停表面 |
| HoverBorderHex | `#C9CDD5` | 悬停描边 |
| SelectedCardHex | `#F3F1FF` | 选中卡片背景 |
| SelectedCardBorderHex | `#8B7CF6` | 选中卡片描边 |
| ActiveCategoryHex | `#E7E4FF` | 活动分类层、按下态 |
| DropTargetHex | `#EAE7FF` | 拖放目标层 |
| ScrollThumbHex | `#B8BDC7` | 滚动条滑块 |
| ScrollThumbHoverHex | `#969CA7` | 滑块悬停 |
| StatusSurfaceHex | `#FCFCFD` | 状态浮层背景 |
| OnAccentHex | `#FFFFFF` | 强调色上的前景（对勾、徽标文字） |

深色主题：跟随系统亮暗切换（`WM_SETTINGCHANGE` 的 `ImmersiveColorSet` + `AppsUseLightTheme` 注册表），按同键位提供第二套字典（`Resources/DesignTheme.Light.xaml` / `DesignTheme.Dark.xaml`，由 `DesignThemeManager` 整本替换）。1.8.0 起用户可在设置中强制浅色/深色（`AppPreferences.ThemeMode`）：强制模式不再响应系统亮暗广播；`FTS_PREVIEW_THEME` 预览覆盖仍为最高优先。

| Token | 浅色 | 深色 |
|---|---|---|
| WindowShellHex | `#F7F8FA` | `#202021` |
| TabRailHex | `#EFF1F4` | `#2B2B2C` |
| CardHex | `#FFFFFF` | `#2D2D2E` |
| BorderHex | `#E2E5EA` | `#414143` |
| PrimaryTextHex | `#1D1D1F` | `#FFFFFF` |
| SecondaryTextHex | `#5F6368` | `#C8C8CA` |
| AccentHex | `#6D5DFB` | `#9C8CFF` |
| DangerHex | `#D92D20` | `#FF8A7A` |
| HeaderHex | `#FBFBFC` | `#272728` |
| HoverSurfaceHex | `#F4F5F7` | `#343436` |
| HoverBorderHex | `#C9CDD5` | `#505053` |
| SelectedCardHex | `#F3F1FF` | `#343054` |
| SelectedCardBorderHex | `#8B7CF6` | `#7A6DF2` |
| ActiveCategoryHex | `#E7E4FF` | `#3A3560` |
| DropTargetHex | `#EAE7FF` | `#322E52` |
| ScrollThumbHex | `#B8BDC7` | `#505054` |
| ScrollThumbHoverHex | `#969CA7` | `#6E6E74` |
| StatusSurfaceHex | `#FCFCFD` | `#282829` |
| OnAccentHex | `#FFFFFF` | `#FFFFFF` |

深色主题的卡片阴影为 `#000000`、透明度 0.35（浅色为 `#1D1D1F`、0.12）。

**配色豁免清单**（有意不进主题键，登记于 §六）：二维码白底码面（扫码可扫性是主题不变量而非调色板颜色——放大层与页脚缩略两宿主自 2026-11-09 f1 落轨起为**岛式呈现**：白底码面＋共享卡片阴影层（`CardShadowLayerStyle` 派生）以主题高程把码面「岛」抬出暗背景，浅深两主题均带高程、码面恒白；圆角走 ControlCornerRadius/CardCornerRadius 资源）；Mica 色调 `WindowShellTintHex/DarkHex`（材质层，非表面色）；IconTool 预览底色（独立工具非主题面）；结构性 `Transparent`（命中层/占位底，非颜色语义）。代码侧画刷一律 `DynamicResource`/`SetResourceReference` 活解析（契约 #28），禁止 `FindResource` 快照。

## 二、动效全景（时长、缓动与编排）

三档取自微软 `timing-and-easing` 官方表：**83ms**（微交互、按压、减弱动效纯淡入）/ **167ms**（悬停、快速反馈、标准入场）/ **250ms**（标准控件动画）。**新增场景不加档、复用既有时长档**（#30 起锚定原则）。

| 场景 | Token | 值 |
|---|---|---|
| 面板展开内容入场 | PanelExpandContentMs | 167 |
| 分类切换内容过渡 | PanelSwitchContentMs | 140 |
| 减弱动效纯淡入 | ReducedMotionFadeMs | 83 |
| 分类轨道揭示 | CategoryRevealMs | 120 |
| 面板收起出场 | PanelCollapseExitMs | 200 |
| 悬停反馈 | HoverFeedbackMs | 167 |
| 按压反馈 | PressFeedbackMs | 83 |
| 显隐淡入淡出（含分类标签反馈层） | FadeVisibilityMs | 120 |
| 删除反馈 | DeleteFeedbackMs | 120 |
| 新条目交错步长 | ItemEntranceStaggerMs | 40（区间 30–50 取中） |
| 拖放目标撤离保持窗（#51） | DropTargetWithdrawHoldMs | 120（覆盖手抖频率，真离开无感知迟滞） |
| 启动确认脉冲（#30） | LaunchConfirmMs | 250（＝SlowDurationMs，不加档；峰值缩放 LaunchConfirmPulseScale=1.06 于半程） |
| 启动项落位（#30） | ItemLandingMs | 250（＝SlowDurationMs，不加档） |
| 启动后排队收起延迟（#30） | LaunchCollapseDelayMs | 250（时序性延迟，减动效保留） |
| 单击启动重入门闩（#30） | LaunchReentryLatchMs | 500 |
| 瓦片入场交错步长/封顶/位移（#30） | TileEntranceStaggerMs / TileEntranceStaggerCap / TileEntranceOffsetPx | 40 / 8 / 8（位移区别于卡片 ContentEntranceOffsetPx 6） |
| 边缘 peek 收缩/展开（#17 peek 化，R12，2026-11-10 落轨） | 复用 NormalDurationMs / SlowDurationMs（不加档） | 167 / 250（收缩加速出场、展开减速入场，经唯一放置动画服务，可中断反转） |
| 边缘 peek 把手 hover 意图事务（#17，#42 同族） | EdgePeekExpandDwellMs | 60（布防→到期重验指针在内才提交展开） |
| 二维码容器过渡（独立组，契约 #24，不进三档） | QrZoomInMs/QrZoomOutMs/QrTextFadeInBeginMs/QrTextFadeInMs/QrTextFadeOutMs/QrThumbFadeMs/QrThumbRevealBeginMs/QrHandoverFadeMs | 450/250/270/180/90/150/180/70（后两枚＝收回交接：缩略图回显起点（95% 回程反解）与换手段时长，Issue #325） |

**缓动曲线与选择规则**——通用两条：入场（减速）`0,0,0,1`、出场（加速）`1,0,1,1`（WPF KeySpline；C# 侧 `FadeAnimation.EnterEasing/ExitEasing` 单点实例，全部动效经此消费，禁 `new CubicEase` 散布）；减弱动效 Linear。**缓动按语义选、不按数值方向**（2026-11-08 定则）：进入某视觉态（淡入、抬升、入场）＝减速曲线，离开（淡出、回落、出场）＝加速曲线——位移可正可负，「to<from」这类数值判据会把负向进入错配加速曲线（LiftAnimation 曾实测与宣称相反）。二维码遮罩纱收回与容器同用 `QrZoomOutKeySpline`（组内专用，经 `FadeAnimation.ExitEaseToken` 指定，不新增通用曲线；Issue #325）。

**编排与退化**：动画可中断可重定向（SnapshotAndReplace＋`FillBehavior.Stop`，契约 #41）；「减弱动效」（系统偏好或 `AnimationsEnabled=false`）全部退化为 83ms 纯淡入或立即定格终值（peek 全链直达终态）。**非线性动画两级先例**：容器级＝二维码放大（container transform：整卡含框飞行、方→矩形形变、文字后段浮现，独立时长组＋专用曲线，契约 #24）；**窗口级首例＝边缘 peek**（2026-11-10 c2 落轨：整窗同尺寸纯平移的连续受控迁移，经唯一放置动画服务 `WindowPlacementAnimator` 承载 §五·C 例外通道——时长/缓动全部复用既有 token，无新档无新曲线；语义与把手视觉见 §五·D）。

## 三、按钮与交互状态（类型学×五态）

全仓按钮收敛为下表类型（H/P/D/F 四态，可见性类按钮另有状态矩阵）。**hover 双轨**：①层淡入＝复合图标元素（头部图标钮/卡片操作钮/分类标签/chip——HoverLayer＋FadeAnimation 167ms）；②属性瞬切＝单表面轻控件（文字钮/过滤 chip/导航/分段/菜单项/瓦片名带，触发器直换）；③仅描边＝密集内容控件（ComboBox/QR 缩略钮/设置开关——只换 `HoverBorderBrush` 描边，不换底不位移）。**pressed 三范式**：换色 `ActiveCategoryBrush`（标准钮族）/缩放 `TilePressScale` 0.96（瓦片族）/透明度 0.85（QR 缩略，白底岛配套孤例）；轻量选择类（勾选钮/导航/分段/开关/下拉/chip）无按压态。**禁用两档**：控件族 `ControlDisabledOpacity` 0.4、整卡灰化 `CardDisabledOpacity` 0.6（#57 先例；钮叠加在卡内 ≈0.24 已裁定可接受）；禁用表达原则＝矩阵隐藏＋守卫优先、IsEnabled 置灰为辅（#19）。**Focus**：全仓 `Focusable=False`＋`FocusVisualStyle=null`（W6 治理），可见键盘焦点仅两处——文本输入框 Accent 描边（功能反馈保留）与瓦片勾选钮键盘聚焦显隐。

| 类型 | Hover | Pressed | Disabled |
|---|---|---|---|
| 头部图标钮（含危险钮） | 层淡入 HoverSurface+HoverBorder | 层 ActiveCategory 83ms | 0.4 |
| 通用文字钮/折叠 ToggleButton | 瞬切换底+换边 | 瞬切 ActiveCategory | 0.4 |
| 卡片操作钮网格（选择/删除/置顶/备注/AI/插件） | 层淡入+图标转 Accent | 层 ActiveCategory | 不禁用（矩阵隐藏＋守卫） |
| 勾选钮（卡/瓦片/选择器 CheckBox） | 环换 Accent 167ms | —（即时选中反馈） | 无禁用 |
| 过滤 chip（toggle） | 瞬切换底+换边 | 瞬切 ActiveCategory | 0.4 |
| 设置开关（自绘 CheckBox） | 仅描边（2026-11-08 补齐） | — | 0.4 |
| 设置导航/分段 RadioButton | 瞬切 HoverSurface | — | 无 |
| ComboBox（本体/Item） | 本体仅换边；Item 换底 | —（开弹层即反馈） | 代码 IsEnabled（未决） |
| 菜单项 | IsHighlighted→HoverSurface | 顶层换 ActiveCategory | 0.4 |
| 自绘 chip/「全部」chip/分类标签 | hover 层淡入+边框瞬切 | — | 无 |
| 启动瓦片 | −2px 位移 167ms（TileHoverLiftPx） | 0.96 缩放 83ms | 无禁用 |
| 微信号复制行（伪按钮） | HoverSurface 瞬切底（2026-11-08 补齐） | — | — |
| peek 把手（窗口级命中面，#17 peek 化） | 提亮＋Accent 指示 167ms；hover 60ms 意图事务提交展开（#42 同族） | —（命中面非钮） | 无 |
| QR 缩略钮 | 仅换边 | Opacity 0.85（孤例） | 无 |
| TextBox/PasswordBox | — | — | 0.4＋Accent 聚焦描边 |
| 滚动条 thumb | 加宽 4→6＋换色 167ms | — | — |
| 设置卡（整卡灰化） | —（卡非钮） | — | 0.6 |

**命中区规格**（按类型登记，不 token 化）：图标钮 30×30（命中层语义宽，图标 14.5 或 16 两制）；文字钮高 30（对话框覆写 32、退出钮 34）；下拉 32；导航项 34；分段 26；撤销钮 Padding=8,2 热区偏小列候选。

## 四、圆角（两档阶梯与登记例外）

两档阶梯（1.50.0 起收敛进 token，Issue #186 D9／2026-10-18，契约 #34）：**控件级 4／表面级 8**。**判定规则**（2026-11-08 成文）：输入/动作类控件（输入框、按钮、下拉、记录编辑框、行内编辑器、菜单行、类型 chip）＝4；承载内容的表面（卡片、瓦片、弹窗壳、主壳、标签容器、放大层）＝8。识别元素保留原值、逐个登记、不入阶梯。

| Token | 值 | 用途 |
|---|---|---|
| ControlCornerRadiusPx | 4 | 控件级（WPF 镜像资源 `ControlCornerRadius`） |
| SurfaceCornerRadiusPx | 8 | 表面级（WPF 镜像资源 `SurfaceCornerRadius`；`CardCornerRadius` 同源收敛=8） |
| DwmCornerRadius（Windows） | 8 | Windows 窗壳：DWM 四角圆角与内容层自裁剪同值（WPF 资源 `ShellCornerRadius` 镜像，不设第二个壳圆角 token）；可见态窗口完整落在工作区内，四角圆角恒定（2026-10-08 决策删除 1.8.0 的右缘越屏裁切，1.21.0 起） |

阶梯外登记值（识别元素/头部钮/徽章小件，数值零变化、2026-11-08 起全仓以资源引用消费）：

| Token | 值 | 用途 |
|---|---|---|
| HeaderActionCornerRadiusPx | 7 | 头部操作按钮与选中计数徽章（WPF 资源 `HeaderActionCornerRadius`／`SelectedCountBadgeCornerRadius`；2026-11-08 由 ControlCornerRadius 重命名对仗——旧名与 4 档资源同名交叉错位；2026-11-10 复审轮 S3 起同值阶梯外表面族并入资源消费：添加菜单弹层表面、分类激活层、状态覆层、紧凑状态弹层与设置左导航项表面，值零变化） |
| PinButtonCornerRadius | 6 | 置顶按钮、卡片图标钮族、瓦片右键菜单行 |
| CheckboxCornerRadius | 5 | 选择环 r5——卡片与瓦片勾选钮、选择器 CheckBox、类型 chip、菜单行共用（WPF 资源 `SelectionRingCornerRadius` 镜像） |

**派生规则例外**（不入表、成文即可）：胶囊/圆形＝高的一半（开关 Track 10/Thumb 7、警告角标 16×16 圆角 8）；线状件＝宽的一半（滑块悬停层 3、导航指示条 1.5、贴边 peek 把手竖条 1.5〔宽 3，#17〕、分隔线 0.5）；内层＝外层−描边（卡片填充层 7＝8−1、放置高亮内层 6＝8−2）；弹层小件与 chip 小件 6（下拉弹层；子分组 chip 容器族五处——`SubgroupChipContainerStyle`/`SubgroupChipActiveLayerStyle`/`SubgroupChipDropTargetLayerStyle` 三处 Setter＋槽 chip 与「全部」伪 chip 的 `ChipHoverLayer` 两处属性式，2026-11-10 复审轮 S3 点名登记，不引置顶钮 6 值 token〔语义族不相属〕）；头部表面左上 11（`HeaderSurfaceStyle` 的 `11,0,0,0`/设置窗头部 `11,11,0,0`）与滑块 2 是样式内既有字面量（废弃 token `HeaderSurfaceCornerRadius`/`ScrollThumbCornerRadius` 零引用残留已于 2026-11-08 删除，字面量按本条登记）。窗壳 `WindowChrome.CornerRadius=0`（圆角由 DWM 承载）为结构性豁免。

## 五、排版与布局（字号、行高、字重、间距与布局范式）

**字号十档全表**（7 基础＋3 阅读档；2026-11-08 起全部为正式档，值零变化）：9（徽标计数）／11（辅助说明、瓦片名、选择器副题）／12（次级正文：状态文本、备注、菜单行、提示行）／13（次级正文：设置行/导航/下拉/搜索 chip）／14（卡片正文 `CardTextFontSize`，显示/编辑两态同源）／16（标题档：窗标题、设置窗标题——2026-11-08 设置标题 15 归位 16）＋阅读档 20/17/15（H1–H3，`ReadingHeading*FontSize`；H4–H6＝正文＋SemiBold）。`TextFormattingMode=Display` 点名面：卡片正文/备注/记录编辑器（14、12）、状态文本（12）、徽标计数（9）、标签/chip 文字（12）——13/11 暂缓（渲染回归候选）。

**行高链**：正文行高 19＝`ceil(14×LineSpacing＋1)` 推导（资源 `CardTextLineHeight`，#25/#48）；瓦片名 16（12 号两行，模板内推导自洽）。**字重单档**：全仓仅 SemiBold 一档强调（标题/激活态/选中段），无 Bold（瓦片警告角标「!」2026-11-08 归位 SemiBold）、无 Light。

**间距节奏**（约定成文、不 token 化——Margin 语义异质，token 膨胀负收益）：元素横向间距 8、卡片水平内缩 12（`CardInsetPx`）、卡片纵向间距 4（`CardGutterPx`）、卡片内边距 12（`CardPaddingPx`；镜像 Thickness 资源 `CardRootMargin/CardRootBoundaryMargin/CardPadding` 三件 2026-11-08 接回）、标题缩进 24。结构性尺寸（轨道宽 58、面板宽 310–640、最小窗高 360 等）不属于设计 token，权威来源是 Core 的 `WindowSettings`：结构不为任何内容让步、禁止为内容加宽结构（滚动条不占布局宽 #23、卡片为操作钮预留 #16 同族；标签轨道 `TabWidth`＝58 即此原则的当前案例——两字/行自适应折行，禁按名长特判，契约 #54）。

**布局范式索引**（正文见各节）：三行面板（头部/内容/状态，§五·D 上下文）；卡片三层结构与列表（§五·B）；瓦片网格（拉伸填充单一几何源，DesignTokens 瓦片常量）；双栏设置窗（§五·E）；overlay 滚动视口（§五·B 末两条）；对话框壳（`DialogShellStyle`，DWM 圆角唯一来源）。

| Token | 值 | 用途 |
|---|---|---|
| ContentEntranceOffsetPx | 6 | 内容入场位移（面板展开/分类揭示） |
| CollapseExitOffsetPx | 24 | 面板收起出场向轨道方向的位移 |
| CardInsetPx / CardGutterPx / CardPaddingPx | 12 / 4 / 12 | 卡片水平内缩／纵向间距／内边距（镜像 Thickness 资源三件） |
| TilePressScale | 0.96 | 启动瓦片按压缩放（#30） |
| TileHoverLiftPx | -2 | 启动瓦片悬停抬升位移（#30；区别于卡片 CardHoverLiftPx −1） |
| CardHoverLiftPx | -1 | 卡片悬停抬升位移（`LiftAnimation.LiftedOffsetPx` 同源；与瓦片 −2 对仗） |
| LauncherTileViewportInsetPx | 8 | 启动瓦片视口顶/底内衬（W6 顶排圆角裁切修复 2026-11-03）：吸收悬停上浮 −2 与确认脉冲 1.06 上下各溢 ≈3 的 RenderTransform 溢出，护住首/末行圆角——内衬随内容滚动，恰在列表自然停位（停顶/停底）提供余量；面板拉伸填充几何不变 |

## 五·B、层级（Elevation）

- 卡片模板为三层结构（1.11.3 起）：根容器（`CardRootStyle`，承载外距与悬停上浮，无 Effect）+ 阴影层（`CardShadowLayerStyle`，与卡片同形状的不透明底 + 共享冻结 `CardShadowEffect`，不承载内容、不参与命中）+ 内容面（`CardContainerStyle`，不透明卡片底、描边与焦点态，**不得携带 Effect**）。带 `Effect` 的元素会把整个子树渲染进中间位图并强制关闭 ClearType（卡片正文发糊主因），阴影因此必须隔离在文字子树之外；小字号文本（卡片正文/记录编辑器 14、状态文本 12、徽标计数 9）按像素对齐渲染（`TextFormattingMode=Display`），16px 标题保持 Ideal。
- 卡片统一携带共享冻结的 `CardShadowEffect`（浅色 `#1D1D1F`/0.12、深色 `#000000`/0.35，模糊 8、深度 2、方向 270°），不逐卡片实例化以保证虚拟化列表性能。
- 卡片悬停时以渲染位移上浮 1px（`LiftAnimation.LiftedOffsetPx`＝`CardHoverLiftPx` −1，167ms 减速曲线抬升、加速曲线回落——按语义不按位移方向），不参与布局、不影响命中测试。
- 滚动条滑块悬停时经 6px 宽悬停层交叉淡入表现展宽（基宽 4px）。
- BoardList 滚动条为 overlay 布局（1.31.0 起）：列表作用域的 overlay ScrollViewer 模板（`OverlayScrollViewerStyle`，经 BoardList.Resources 作用域隐式样式挂接）让垂直滚动条与内容同格、右缘悬浮其上、不占布局宽——viewport 几何与滚动条可见性解耦，卡片几何不随滚动条出现/消失漂移（默认模板的 Auto 独立列 + 主题样式的系统滚动条宽度下限曾是漂移根因，条带 `MinWidth/MinHeight=0` 压掉该下限）。条带宽 8 落在卡片右缘外边距带 12 内、与卡片内容零遮挡；`Auto` 语义不变（需要时才出现），滑块、透明轨道与悬停展宽 token 全部继续有效。设计契约 #23。
- 记录编辑器滚动条同为 overlay 布局（文本输入表面，2026-11-06 起）：派生样式 `OverlayScrollTextBoxStyle`（基于 `ThemedTextBoxStyle`，仅重写模板把内容宿主直引 `OverlayScrollViewerStyle`）——打字途中滚动条出现/消失不改变排版宽，Wrap 全文不再重排；与基样式的模板差异是内边距从内容宿主外边距改为 Padding 透传（overlay 模板把 Padding 施加在内容台上、条带贴满宽右缘），条带宽 8 落在编辑器右内边距 12 带内、与文字零相交。设计契约 #23。

## 五·C、窗口材质（Mica，Windows 11）

- 窗口壳弃用 `AllowsTransparency` 分层透明，改用 `WindowChrome`（`GlassFrameThickness=-1` 整窗玻璃帧）承载 DWM 材质，经 `DWMWA_SYSTEMBACKDROP_TYPE` 应用 Mica；材质不可用的旧系统回退到不透明壳色。
- 圆角由 DWM（`DWMWA_WINDOW_CORNER_PREFERENCE=ROUND`）裁剪，内容层以 `DesignTokens.DwmCornerRadius = 8` 四角同步自裁剪，保证渲染位图与屏幕一致。可见态窗口完整落在工作区内，四角圆角在任务栏停靠侧、多显示器、显示器插拔与系统广播下恒定（2026-10-08 决策删除 1.8.0 的右缘越屏裁切与内容层右内缩，1.21.0 起）；`WM_DISPLAYCHANGE`/`WM_SETTINGCHANGE` 后按当前面板状态重新贴齐。
- 壳表面为半透明 Mica 色调（浅 `#CCF7F8FA`、深 `#CC202021`，`WindowShellTintHex` / `WindowShellTintDarkHex`），深浅切换时同步 `DWMWA_USE_IMMERSIVE_DARK_MODE`。
- **迁移原子性（1.8.1 起）**：一次面板状态迁移只允许一次窗口矩形变更。已创建 HWND 的窗口经单次 Win32 `SetWindowPos` 应用终态矩形（物理像素；两条锚定边各自取整后相减，右缘与底缘不因取整漂移），随后在 `WM_WINDOWPOSCHANGING` 守卫下对齐 WPF 尺寸/位置账本——守卫把对齐期间任何中间矩形改写回终态，陈旧 DP 混合值不会重推出可见中间态。构造期（HWND 未创建）保留纯属性路径。STA 契约：观察到的每个窗口矩形 ∈ {初始, 终态}（`*ChangesTheObservedWindowRectangleOnlyBetweenStableStates` 系列锁定）。禁止用 `SetWindowRgn` 或动效/延迟/隐藏窗口装饰性掩盖。唯一例外通道（2026-11-09 起，R12 peek）：经唯一放置动画服务 `WindowPlacementAnimator` 的连续迁移允许中间矩形——单一矩形权威（任何 `ApplyPlacement` 先取消动画到达终态）、逐帧单调、终态唯一、可中断可重定向（禁原点重放）、减动效直达；`PlacementAnimation_SingleAuthorityMonotonicTerminalUnique` 锁定。
- 本机预览与截图取证：设 `FTS_PREVIEW_DATA_DIR`（隔离数据目录、独立单实例锁）与可选 `FTS_PREVIEW_THEME=dark|light` 启动，不影响已安装应用。

## 五·D、卡片文字避让与一次性贴边隐藏（1.20.0）

- **文字避让操作列（1.20.0 起；1.24.0 扩为三列，1.29.0 扩为四列，1.33.0 改右上两列网格）**：文字卡正文右侧为右上角操作按钮网格预留排他空间，任何时刻（悬停/非悬停）零像素争用。内缩量唯一来源是 WPF 派生资源 `CardTextReservedRightInset`（两列 `CardOperationColumnWidth` 30×2 + 4 DIP 间隙 = 64；1.20.0 两列横排为 64，1.24.0 三列 94，1.29.0 四列 124，1.33.0 随按钮区网格化回归 64），不得在别处写死第二个数值；操作按钮网格为固定两列 × 按钮行（列与按钮行绝对 30 DIP，按钮行间 4 DIP 独立间隔行，行优先序 选择|删除|置顶|备注，按钮隐藏时格子留空不漂移）；命中层对半分区与图片卡全幅不变。310px 最窄面板下每行文字约损失 25% 宽度（64/260），是消除像素争用的确定代价。
- **一次性贴边隐藏 peek 化（1.20.0 起；2026-11-09 R12 重设计）**：头部按钮（贴缘面板图标右侧、「箭头收入右缘」图标）切换语义；待命与 peek 期间按钮转强调色、头部操作区常显（鼠标离开恰是最需要看到待命指示的时刻）。收缩 = 整窗**同尺寸纯平移**到右缘 peek 态——绝大部分出屏、屏内仅留左侧把手（宽＝`EdgePeekHandleWidthPx`＝16 DIP，视觉宽＝命中宽＝屏内保留宽同值；窗口左缘自带 DWM 恒定圆角即把手左侧圆角，天然 signifier），面板保持展开、内容冻结；触发融入既有 250ms 收起节奏，抑制条件复用 `PanelStateMachine.WouldCollapse`。收缩 167ms 加速出场（`NormalDurationMs`＋Exit 缓动）、展开 250ms 减速入场（`SlowDurationMs`＋Enter 缓动），经唯一放置动画服务 `WindowPlacementAnimator` 连续受控迁移（§五·C 例外通道）；把手视觉 83ms 淡入登场、hover 提亮（167ms）＋Accent 指示。展开触发 = 把手 hover 意图事务（60ms 重验指针在内才提交，`EdgePeekExpandDwellMs`，#42 同族）；收缩途中指针追入把手/窗口＝同样经 60ms 事务从当前进度反转展开（禁原点重放），展开途中不中断；恢复即回原放置并自动解除（一次性），热键与显示器变化在 peek 态立即展开兜底；减动效全链直达终态。回位区轮询机制（100ms 轮询＋8px 容差驻留 ≥200ms）已随 peek 化删除。图标遵守 14.5×14.5 + 1.5 描边规范。行为契约全文见设计契约清单（维护者本地资料，不入库）第 12、17 条。
- **默认接收分类标记（2026-11-05 W2 重设计；2026-11-07 改双态环语法；2026-11-08 chip 环独占化）**：标签与子分组 chip 右上角标记为单段圆弧描边圆环（viewbox 24×24、半径 8 描边圆、无内圈无中心点），10×10 + 1.25 描边——自 2026-11-05 起**脱离**上一行的头部按钮图标 14.5×14.5 + 1.5 规范（全仓唯一例外，锁于 `CategoryDefaultMarkerStyle` 与 `MainWindowInteractionTests` 几何断言）；右上位置（`Margin="0,3,4,0"`）、`Panel.ZIndex=1` 层级语义不变。**双态环语法（主标签恒环双枚、chip 恰一枚独占环）**：紫＝当前有效默认落点（`AccentBrush`），灰＝`SecondaryTextBrush` 复用（非新 token），无环＝永不具备落点能力（记录/启动主标签）。主标签臂：内容桶恰两枚恒环——紫＝默认接收分类、灰＝另一内容桶（环兼任「落点能力」判别子，记录/启动 None 的对照）；chip 臂：轨可见时恰一枚独占环——chip＝其分类默认子分组（未设兜底显示序首槽、恒灰），其余槽 chip None；两宿主共享不变量「紫＝当前有效默认落点」，显隐与用色唯一驱动＝两宿主同名属性 `DefaultMarkerState` 的样式触发器单点。

## 五·E、设置窗口（双栏导航、冻结几何与几何持久化）

- **信息架构**：设置窗口是独立浮动窗口（非主窗贴缘停靠形态）：标题栏 + 左导航 + 右内容页 + 页脚。左导航 5 组（外观/面板/通用/数据与维护/AI）为同组 RadioButton（风格同主窗分类轨：选中态 `ActiveCategoryBrush` 底 + 3px `AccentBrush` 指示条、圆角 7、悬停 `HoverSurfaceBrush`），原生携带 SelectionItem 自动化语义；↑/↓ 循环、Home/End 跳首末在预览隧道期消费（压掉 RadioButton 原生方向键导航，保证一次按键一步）；页脚承载关于（应用名+版本+GitHub 链接）与退出（Danger 描边），不作导航项。13 个设置节按冻结表迁入 5 个内容页，每节用既有卡片语言分组（`CardBrush`/`BorderBrush`/圆角 8），颜色一律走两本主题字典键；页切换只翻 `Visibility`，入场页可做 167ms 档 Opacity 淡入（可见性即时生效，动效关闭或构造期直接就位）。右列 `SettingsScrollHost` 是全部页共享的唯一滚动视口——单一 x:Name 实例是 namescope 唯一性约束下的必然形态，也使迁移锁（`IsEnabled=false`）语义与旧单列表格一致。
- **冻结几何**：`SizeToContent="Manual"` + `ResizeMode="CanResize"` + `WindowChrome.ResizeBorderThickness=6`（无边框窗口系统级 8 向缩放的正解；`NoResize` 会移除 WS_THICKFRAME 使缩放命中失效；`CaptionHeight=0` 恰好避免顶边命中冲突；DWM 圆角缩放时自动重算，禁止 `SetWindowRgn`）。默认 680×600 DIP、最小 560×460、最大宽 900、最大高 = 所在工作区高 − 48（`MaxHeightMargin=48`，矮屏抬到最小高——`MaxHeight<MinHeight` 在 WPF 是未定义行为）。窗口独立四边描边、标题栏双上角圆。内容溢出由共享视口滚动，页脚在滚动区外常驻可见。
- **几何持久化**：`AppPreferences` 可空 DIP 字段（`SettingsWindowLeft/Top/Width/Height`，null=默认定位），只走 `ISettingsHost.ApplyPreferences` 原子管线（契约 #5 同路）；落盘时机=操作结束（头部 Thumb `DragCompleted` 与系统缩放 `WM_EXITSIZEMOVE`，`WindowState==Normal` 才写，拖拽进行中零写盘）。恢复在 `SourceInitialized`：整组非法（NaN/±∞/非正尺寸）或完全落在所有显示器之外 → 回落 owner 相对默认定位；否则按矩形中心解析目标显示器（`MonitorFromPoint`+`GetMonitorInfo`+`GetDpiForMonitor`，内部一律 DIP、仅 Win32 边界按显示器有效 DPI 换算；无句柄/失败退主屏工作区），按其重算 MaxHeight 并把矩形归一——部分越界钳到最近工作区边界，负坐标在目标屏工作区内原样合法（左侧副屏不钳非负）。系统交互缩放期间（`WM_SIZEMOVE`）挂起位置钳制，`WM_EXITSIZEMOVE` 收口一次；`WM_GETMINMAXINFO` 按所在显示器工作区修正无边框最大化几何。标题栏拖拽 DragDelta 钳制维持主屏工作区语义（历史冻结）。行为契约全文见设计契约清单（维护者本地资料，不入库）第 23 条（记录 Issue #106）。

## 五·F、界面字体（token 化与自定义字体全链路，2026-10-25；2026-11-02 默认字体改微软雅黑链＋默认项语义合并）

- **默认字体唯一来源**：`DesignTokens.InterfaceFontFamily`（`"Microsoft YaHei UI, Microsoft YaHei"`，链首 UI 变体（Win11 必装、行距更紧凑）、缺失回退 Microsoft YaHei——链式族串 WPF 按链首计 `LineSpacing`（实测 1.27001953125）；2026-11-02 前为 `"Segoe UI Variable, Segoe UI"`（1.330078））。XAML 资源键 `InterfaceFontFamily`（`MainWindowStyles.xaml`）逐字镜像它并由锁定测试互锁；六个窗口根（主窗/设置/多选选择器/编辑目标/网址输入/插件试运行）与全部样式一律 `DynamicResource InterfaceFontFamily` 引用，全仓不得出现第二份界面字体字面量。**图标字体独立**：`Segoe Fluent Icons, Segoe MDL2 Assets` 是图标字形来源，不随用户字体变化。
- **自定义字体**：偏好 `InterfaceFont`（`preferences.json` 键 `interfaceFont`，null=默认）经 `InterfaceFontManager` 以**窗口级槽位资源**覆盖默认键（应用面区别于主题整本替换）：进程级当前字体由 `MainWindow.ApplyPreferences` 唯一管线喂值并应用到主窗＋全部存活后代窗口（Owner 链递归，`DynamicResource` 即时生效）；后建窗口在构造期（与 `DesignThemeManager.Apply` 同点）自应用。解析按系统字体清单校验：空白/未安装名字一律回落默认（WPF 对未知名字不抛错而给回退度量，靠清单校验保住「所见即所选」）。**不随应用分发字体文件**（§六第 6 条同源边界）；设置 → 外观「界面字体」下拉只列本机已安装字体（图标/符号字体排除），链首字体项与默认合并为唯一首项（显示名追加「（默认）」，本机形态「Microsoft YaHei UI（默认）」）即恢复默认——偏好 null 与显式链首名回显同一项，无双项并存。
- **行高由当前字体度量推导**：`CardTextLineHeight` 默认字面量 19 ＝ 默认字体链链首推导值 `ceil(CardTextFontSize 14 × LineSpacing 1.27001953125 ＋ CardTextExtraLeadingPx 1)`，公式常数唯一来源 `DesignTokens`（2026-11-02 改链前 Segoe UI 链推导 20）；自定义字体时同名窗口级槽位注入按该字体度量的推导值（更高 em 字体→更大行高，恒 ≥ 自然行高的整数上界——`BlockLineHeight` 语义下不裁切字形）。行数上限随行高等比：正文可见上限 `CardTextPreviewMaxHeight`＝行高×5（默认 95）、备注可见上限 `CardNoteMaxHeight`＝行高×2（默认 38），默认字体下与既有几何逐值一致。两态行距同源契约（#25）不变：四态消费者同引单一资源键，键值随字体推导。行为契约全文见设计契约清单（维护者本地资料，不入库）第 25（修订）、48 条。

## 五·G、记录阅读视图排版（Markdown 渲染，实验性，2026-11-03）

实验开关 `ReviewMarkdownViewEnabled`（2026-11-07 起默认开启；存量显式 false 经一次性偏好迁移统一收敛）开启后记录表面的只读 Markdown 阅读视图（类 Obsidian 阅读体验；子集与降级语义见设计契约 #4 增补）。排版数值唯一来源＝`DesignTokens`（人类可读规范即本节）：

| Token | 值 | 用途 |
|---|---|---|
| ReadingHeading1FontSize | 20 | H1 字号 |
| ReadingHeading2FontSize | 17 | H2 字号 |
| ReadingHeading3FontSize | 15 | H3 字号（H4–H6＝正文字号＋SemiBold 权重，不设第二份 14） |
| ReadingCodeFontFamily | `Cascadia Mono, Consolas` | 代码块/行内代码字体——独立功能来源（同图标字体先例，不随用户界面字体变化；Win11 必装链首，缺失回退 Consolas） |
| ReadingBlockGapPx | 6 | 块间垂直间距（段落/代码块/引用/列表/分隔线共用） |
| ReadingCodePaddingPx | 8 | 代码块内边距 |
| ReadingQuoteBarPx | 3 | 块引用左缘竖条宽 |

- **字号链路**：正文沿用 `CardTextFontSize`（14，卡片正文同源）；标题阶梯只有 H1–H3 三档显式 token。**颜色零新增**：正文/引用文字＝`PrimaryTextBrush`、次级说明＝`SecondaryTextBrush`、链接＝`AccentBrush`（下划线）、代码块底与行内代码底＝`HoverSurfaceBrush`、代码块描边与引用竖条/分隔线＝`BorderBrush`——全部经 DynamicResource 引用主题字典，浅深两主题整本替换即时切换（锁定测试断言解析为字典实例）。
- **交互**：阅读/编辑分段切换（RadioButton 分段、选中 `ActiveCategoryBrush` 底）只在实验开关开启且记录表面激活时可见；软换行按 Obsidian 惯例渲染为换行；链接仅 http/https 可点击（UseShellExecute 开默认浏览器），不加载远程图片。

## 六、动效原则（评审基准）与反例豁免清单

动效六原则（评审基准）：

1. 进入减速、出场加速，退出比进入快；面板展开 167ms / 收起 200ms 不对称。
2. 动效可中断、可重定向：新动画或停止必须先复位终值；不得让用户等动画播完。
3. 动效不阻断命中测试；不作为传达信息的唯一方式。
4. 系统开启「减弱动效」或用户在设置中关闭动效（`AppPreferences.AnimationsEnabled=false`，本地覆盖同一系统键）时，全部退化为 83ms 纯淡入或立即定格终值；恢复开启即移除覆盖、重新跟随系统。
5. 收起动画不得破坏「收起视觉交接」防闪烁机制与面板状态机语义。
6. 图标取自 fluentui-system-icons（MIT）几何重绘，不随应用分发字体文件。

**反例与豁免清单**（有意不收编项总表；新增例外必须登记进本表并关联契约，禁止静默新增字面量）：

| 项 | 内容 | 依据 |
|---|---|---|
| 配色豁免 | QR 白底码面（f1 落轨后岛式：主题阴影高程＋恒白码面，2026-11-09 实况）、Mica tint、IconTool 预览底色、结构性 Transparent | §一配色豁免清单 |
| 圆角派生 | 胶囊/线状/内层−描边三类派生值与头部表面 11、滑块 2 | §四派生规则 |
| 行为计时器 | 意图窗 60ms、状态 4s、反馈复位 1.6s、AI 旋转 1.2s、失败闪烁 1/0.2、PopupAnimation=Fade（系统）、拖拽线性归位、瓦片落位 0.8→1.0 幅值（前三项 2026-11-10 复审轮 S3 起在各现场以注释锚定本行；收起驻留 250ms 已除名——引 `SlowDurationMs` 同源） | 时序编排/场景值非视觉档 |
| 字号/图标尺寸 | 图标字形 28/36/16、瓦片勾选钮图标 16（脱离 14.5 规范已注记）、瓦片名 LineHeight 16 | 图标尺寸独立于文本字号档 |
| Thickness 重复值 | `0,0,8,0`、`24,0,0,0` 等 | 间距节奏约定（§五），Margin 语义异质不 token 化 |
| Hyperlink | 原生下划线 | 平台惯例 |
| 焦点视觉 | 容器不画焦点框（W6 治理） | §三 Focus 矩阵；补键盘可见态为未决项（归 G 簇） |
| 文案字面量 | 重复文案与同义不同文对 | 非视觉语言六面，归 G 簇（种子 #4） |
