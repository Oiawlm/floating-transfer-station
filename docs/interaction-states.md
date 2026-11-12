# 交互状态规范（interaction states）

> **定位**：全仓交互状态（hover/pressed/禁用/focus 与状态视觉的图层结构）的唯一专题规范。`docs/design.md` §三「按钮与交互状态」与 §五·B「层级」保留总览地位，本文是其正文纵深；动效时长/缓动/编排见 `docs/motion.md`，两文互不复制数值权威。
>
> **数值声明**：本文出现的全部数值均为**示例值**（基线 v1.72.0，commit `bba9b4e`，2026-11-12 抽取）。数值消费的规范路径是 token 文件 **`tokens/motion.tokens.json`**（动效与交互状态数值统一收口于同一 token 文件）；该文件建仓前，当前实现的数值唯一声明点是 `src/FloatingTransferStation.Core/Design/DesignTokens.cs`（WPF 资源镜像＋STA 锁定测试互锁）。本文不立第二数值权威，表中「Token」列即 token 文件内键名。
>
> **溯源**：正文内嵌 fts 溯源锚点（`文件:行`，均相对仓库根），文末附索引表；锚点按上列基线 commit 抽查真实。交互状态面由 `tests/FloatingTransferStation.Tests/MainWindowInteractionTests.ButtonStates.cs` 锁定。

## 一、类型学（控件类型全景）

全仓控件收敛为下表类型——新控件必须先在表中找归属（复用既有类型的整行范式），无归属才提新类型并登记本表。**hover 双轨/pressed 范式/禁用档的选型规则见 §二/§三。**

| 类型 | Hover | Pressed | Disabled |
|---|---|---|---|
| 头部图标钮（含危险钮） | 层淡入 HoverSurface＋HoverBorder | 层 ActiveCategory 83ms | 0.4 |
| 通用文字钮/折叠 ToggleButton | 瞬切换底＋换边 | 瞬切 ActiveCategory | 0.4 |
| 卡片操作钮网格（选择/删除/置顶/备注/AI/插件） | 层淡入＋图标转 Accent | 层 ActiveCategory | 不禁用（矩阵隐藏＋守卫） |
| 勾选钮（卡/瓦片/选择器 CheckBox） | 环换 Accent 167ms | —（即时选中反馈） | 无禁用 |
| 过滤 chip（toggle） | 瞬切换底＋换边 | 瞬切 ActiveCategory | 0.4 |
| 设置开关（自绘 CheckBox） | 仅描边 | — | 0.4 |
| 设置导航/分段 RadioButton | 瞬切 HoverSurface | — | 无 |
| ComboBox（本体/Item） | 本体仅换边；Item 换底 | —（开弹层即反馈） | 代码 IsEnabled |
| 菜单项 | IsHighlighted→HoverSurface | 顶层换 ActiveCategory | 0.4 |
| 自绘 chip/「全部」chip/分类标签 | hover 层淡入＋边框瞬切 | — | 无 |
| 启动瓦片 | −2px 位移 167ms（TileHoverLiftPx） | 0.96 缩放 83ms（TilePressScale） | 无禁用 |
| 微信号复制行（伪按钮） | HoverSurface 瞬切底 | — | — |
| peek 把手（窗口级命中面） | 提亮＋Accent 指示 167ms；60ms 意图事务提交展开 | —（命中面非钮） | 无 |
| QR 缩略钮 | 仅换边 | Opacity 0.85（孤例） | 无 |
| TextBox/PasswordBox | — | — | 0.4＋Accent 聚焦描边 |
| 滚动条 thumb | 加宽 4→6＋换色 167ms | — | — |
| 设置卡（整卡灰化） | —（卡非钮） | — | 0.6（CardDisabledOpacity） |

**命中区规格**（按类型登记，不 token 化——几何规格属结构值非设计 token）：图标钮 30×30（命中层语义宽；图标 14.5 或 16 两制）；文字钮高 30（对话框覆写 32、退出钮 34）；下拉 32；导航项 34；分段 26。

## 二、双轨三范式（hover 与 pressed 的选型规则）

### hover 双轨

「双轨」＝**动画轨 vs 瞬切轨**：悬停反馈要么由独立图层交叉淡入承载（动画轨），要么由属性触发器直换承载（瞬切轨）；不存在第三种承载机制。瞬切轨内按「改什么」分三支系：

| 支系 | 对象 | 表现 | 判别 |
|---|---|---|---|
| ①层淡入（动画轨） | 复合图标元素（头部图标钮/卡片操作钮/分类标签/chip） | HoverLayer＋FadeAnimation 167ms（HoverFeedbackMs） | 模板**无** IsMouseOver 属性触发器，悬停由 `FadeAnimation.IsActive` 驱动 |
| ②属性瞬切 | 单表面轻控件（文字钮/过滤 chip/导航/分段/菜单项/瓦片名带） | 触发器直换 Background/BorderBrush，零时长 | 模板有 IsMouseOver Setter 直换底/边 |
| ③仅描边 | 密集内容控件（ComboBox/QR 缩略钮/设置开关） | 只换 `HoverBorderBrush` 描边，不换底不位移 | 触发器只动 BorderBrush |

判别的锁定测试：`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.ButtonStates.cs:105`（`ButtonStates_HoverAndPressedFollowSharedParadigms`——头部图标钮模板零瞬切触发器＋含 `HoverLayer` 且由 FadeAnimation 驱动＝层淡入支系；文字钮换底 HoverSurfaceBrush＋换边 HoverBorderBrush＝瞬切支系；开关 Track 仅描边）。层淡入的图层结构见 §五。

### pressed 三范式

按压反馈只有三种范式，按控件族落位：

| 范式 | 表现 | 对象 |
|---|---|---|
| 换色 | 瞬切/层切 `ActiveCategoryBrush`，83ms（PressFeedbackMs） | 标准钮族（文字钮/chip/头部钮/菜单顶层） |
| 缩放 | 0.96（TilePressScale）83ms | 瓦片族（启动瓦片） |
| 透明度 | 0.85 | QR 缩略钮（白底岛配套孤例） |

锚点：`src/FloatingTransferStation/Views/SettingsWindow.xaml:270-275`（QR 缩略钮模板：hover 仅换 HoverBorderBrush、IsPressed 只降 Surface Opacity 0.85——③支系＋透明度孤例同帧可见）；`src/FloatingTransferStation.Core/Design/DesignTokens.cs:133`（TilePressScale 0.96）。

**无按压态类**：轻量选择类（勾选钮/导航/分段/开关/下拉/chip）无按压态——选中/弹层本身即反馈，再叠按压是冗余刺激。判别原则：按压态只加给「按下即执行」的动作族；「按下即选择」的族用选中态表达。

## 三、禁用档（两档灰化与不可用性表达）

**禁用不透明度两档**（`src/FloatingTransferStation.Core/Design/DesignTokens.cs:209-210`）：

| 档 | Token | 示例值 | 对象 |
|---|---|---|---|
| 控件族 | ControlDisabledOpacity | 0.4 | 按钮族/输入框/菜单项等一切控件级禁用 |
| 整卡灰化 | CardDisabledOpacity | 0.6 | 设置卡等整表面禁用（#57 先例入 token） |

- 发现第三档（0.35/0.45/0.5 散档并存）就是漂移，归一两档；消费者一律 token 化引用（XAML `x:Static design:DesignTokens.ControlDisabledOpacity`，见 `src/FloatingTransferStation/Resources/MainWindowStyles.xaml:152` 等），禁止字面量。锁定测试：`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.ButtonStates.cs:45`（`ButtonStates_DisabledOpacityConsolidatesToControlAndCardTiers`）。
- **叠加规则**：钮（0.4）叠在整卡灰化（0.6）内 ≈0.24，已裁定可接受——不为此引入第三档或豁免链。

**不可用性表达原则**（比灰化更重要的一半）：**矩阵隐藏＋守卫优先，`IsEnabled` 置灰为辅**（契约 #19）。理由：禁用元素不可命中，点击穿透成误触——正确做法是入口守卫（点击时校验并给反馈）＋视觉徽章/灰态表达，元素保持可命中。卡片操作钮「不禁用」一行即此原则的落位：不可用时不灰化按钮，而是从操作矩阵隐藏或由守卫拦截。

## 四、Focus 矩阵

全仓默认：**非文本控件 `Focusable=False`＋`FocusVisualStyle=null`**（W6 治理）。锚点：`src/FloatingTransferStation/Resources/MainWindowStyles.xaml:218` 等全样式散点（FocusVisualStyle 显式置 Null）；容器一律不画焦点框。

可见键盘焦点全仓仅两处：

| 位置 | 表达 | 理由 |
|---|---|---|
| 文本输入框（TextBox/PasswordBox） | Accent 描边 | 输入位置是功能信息，必须可见 |
| 瓦片勾选钮 | 键盘聚焦显隐（悬停∨选中∨键盘聚焦三态或） | 键盘多选路径的存在性指示 |

锚点：`src/FloatingTransferStation/Resources/MainWindowStyles.xaml:1799`（瓦片勾选钮「悬停∨选中∨键盘聚焦可见」三态或触发器，#13 族）。

判别原则：焦点视觉只加给「键盘操作真实到达」的元素；纯鼠标控件画焦点框是噪音，容器焦点框一律不画。新增键盘可达路径时先扩本矩阵并给出真实键盘驱动验证，不给纯装饰焦点框。

## 五、分层叠加（状态视觉的图层结构）

**总则：状态表达用独立图层交叉淡入，不换色、不重排。** 同一控件的状态视觉拆为各司其职的图层——填充层（HoverSurface 底）、环层（描边/选中环）、命中层（透明命中面）——状态切换＝目标层 Opacity 0↔1 交叉淡入（FadeAnimation，167ms 档），基线层几何与内容零变化。

1. **HoverLayer 结构**：模板内独立 `Border`（`x:Name="HoverLayer"`）承载悬停表面，与内容、描边分离；层淡入支系（§二①）全部经此结构。锚点：`src/FloatingTransferStation/Resources/MainWindowStyles.xaml:128`（头部钮模板 HoverLayer）、`MainWindowStyles.xaml:1382`（分类标签同构）、`MainWindowStyles.xaml:2025`（滚动条 ThumbHoverLayer——基宽 4px 悬停层 6px 交叉淡入表现展宽）。
2. **阴影隔离在文字子树之外**：带 `Effect` 的元素把整个子树渲染进中间位图并强制关闭 ClearType（小字发糊主因）。卡片因此为三层结构：根容器（`CardRootStyle`，外距与悬停上浮，无 Effect）＋阴影层（`CardShadowLayerStyle`，同形状不透明底＋共享冻结 `CardShadowEffect`，不承载内容、不参与命中）＋内容面（`CardContainerStyle`，不透明底/描边/焦点态，**不得携带 Effect**）。锚点：`src/FloatingTransferStation/Resources/MainWindowStyles.xaml:1243-1266`（三样式）与 `MainWindowStyles.xaml:2196-2202`（卡片模板三层消费）。阴影效果共享冻结单实例（浅 `#1D1D1F`/0.12、深 `#000000`/0.35），不逐元素实例化——虚拟化列表性能前提。
3. **位移不参与布局**：悬停上浮用渲染位移（卡片 −1px/瓦片 −2px，`LiftAnimation` 经 RenderTransform），不参与布局、不影响命中测试；数值同源锚点 `src/FloatingTransferStation/Views/LiftAnimation.cs:17`（`LiftedOffsetPx ＝ CardHoverLiftPx`）。缓动按语义选（抬升＝进入态减速/回落＝加速），见 `docs/motion.md` §二。
4. **按钮格子固定预留**：卡片操作钮网格列/行绝对尺寸，按钮隐藏时格子留空不漂移——状态变化不引起任何重排（§一命中区规格同一原则）。

## 附：fts 溯源锚点索引

| # | 锚点 | 内容 | 抽查 |
|---|---|---|---|
| 1 | `tests/FloatingTransferStation.Tests/MainWindowInteractionTests.ButtonStates.cs:105` | hover 双轨判别＋pressed 范式锁定测试 | ✅ 基线 commit 实测 |
| 2 | `tests/FloatingTransferStation.Tests/MainWindowInteractionTests.ButtonStates.cs:45` | 禁用两档归一锁定测试 | ✅ 基线 commit 实测 |
| 3 | `src/FloatingTransferStation.Core/Design/DesignTokens.cs:209-210` | ControlDisabledOpacity 0.4 / CardDisabledOpacity 0.6 | ✅ 基线 commit 实测 |
| 4 | `src/FloatingTransferStation/Resources/MainWindowStyles.xaml:128` | HoverLayer 独立图层（层淡入支系） | ✅ 基线 commit 实测 |
| 5 | `src/FloatingTransferStation/Resources/MainWindowStyles.xaml:1243-1266` | 卡片三层结构三样式 | ✅ 基线 commit 实测 |
| 6 | `src/FloatingTransferStation/Views/SettingsWindow.xaml:270-275` | QR 缩略钮：仅描边 hover＋0.85 按压孤例 | ✅ 基线 commit 实测 |
| 7 | `src/FloatingTransferStation/Resources/MainWindowStyles.xaml:1799` | 瓦片勾选钮键盘聚焦显隐（三态或） | ✅ 基线 commit 实测 |
| 8 | `src/FloatingTransferStation/Views/LiftAnimation.cs:17` | 悬停上浮渲染位移 token 同源 | ✅ 基线 commit 实测 |

> 锚点行号随代码演进漂移属正常；抽查以内容命中为准（样式名/触发器短语可 grep 复核）。数值以 `tokens/motion.tokens.json`（建仓前 `DesignTokens.cs`）为准，本文示例值不构成第二权威。
