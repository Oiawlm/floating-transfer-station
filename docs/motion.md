# 动效规范（motion）

> **定位**：全仓动效的唯一专题规范——时长档位、缓动选择、编排、窗口级例外通道、减动效退化与评审原则。`docs/design.md` §二「动效全景」与 §六「动效原则」保留总览地位，本文是其正文纵深；两文互不复制数值权威，冲突时以数值源（下条）为准。
>
> **数值声明**：本文出现的全部数值均为**示例值**（基线 v1.72.0，commit `bba9b4e`，2026-11-12 抽取）。数值消费的规范路径是 token 文件 **`tokens/motion.tokens.json`**；该文件建仓前，当前实现的数值唯一声明点是 `src/FloatingTransferStation.Core/Design/DesignTokens.cs`（WPF 资源镜像＋STA 锁定测试互锁）。本文不立第二数值权威，表中「Token」列即 token 文件内键名，改动数值必须同步数值源与本文。
>
> **溯源**：正文内嵌 fts 溯源锚点（`文件:行`，均相对仓库根），文末附索引表；锚点按上列基线 commit 抽查真实。交互状态（hover/pressed/禁用/focus）的专题规范见 `docs/interaction-states.md`。

## 一、档位（时长阶梯）

三档取自微软 `timing-and-easing` 官方表（Win11 Fluent 基线，调研出处指针见 `docs/design.md` §二首段——维护者本地资料不入库，不再重复本地路径）：

| 档 | Token | 示例值 | 典型场景 |
|---|---|---|---|
| 快 | FastDurationMs | 83ms | 按压反馈、把手登场淡入、减动效纯淡入 |
| 中 | NormalDurationMs | 167ms | 悬停反馈、面板展开内容入场、peek 收缩出场（加速曲线） |
| 慢 | SlowDurationMs | 250ms | 标准控件动画、启动确认脉冲、新条目落位、peek 展开入场（减速曲线） |

三档之外的**登记场景值**（历史登记；新场景从三档起步，不为凑值引入新档）：

| 场景 | Token | 示例值 | 备注 |
|---|---|---|---|
| 面板切换内容过渡 | PanelSwitchContentMs | 140 | 快于展开档的轻过渡 |
| 分类轨道揭示 | CategoryRevealMs | 120 | |
| 显隐淡入淡出（含分类标签反馈层） | FadeVisibilityMs | 120 | |
| 删除反馈 | DeleteFeedbackMs | 120 | |
| 拖放目标撤离保持窗 | DropTargetWithdrawHoldMs | 120 | 覆盖手抖频率，真离开无感知迟滞（契约 #51） |
| 面板收起出场 | PanelCollapseExitMs | 200 | **不对称登记例外**：出场慢于此处一般规则，属收起节奏登记值（§二） |
| 边缘 peek 把手意图窗 | EdgePeekExpandDwellMs | 60 | 意图事务驻留重验（§三），非视觉档 |
| 启动后排队收起延迟 | LaunchCollapseDelayMs | 250 | 时序性延迟，减动效保留（§五） |
| 单击启动重入门闩 | LaunchReentryLatchMs | 500 | 防重复触发门闩，非视觉档 |
| 新条目交错步长 | ItemEntranceStaggerMs | 40 | 节奏值非档位，与封顶规则绑定（§三） |
| 瓦片入场交错/封顶/位移 | TileEntranceStaggerMs / TileEntranceStaggerCap / TileEntranceOffsetPx | 40 / 8 / 8 | 位移区别于卡片 ContentEntranceOffsetPx 6 |

**独立时长组**（契约化例外，不是加档后门）：容器级 container transform 一组专用——二维码放大/收回八枚 `QrZoomInMs / QrZoomOutMs / QrTextFadeInBeginMs / QrTextFadeInMs / QrTextFadeOutMs / QrThumbFadeMs / QrThumbRevealBeginMs / QrHandoverFadeMs`＝450/250/270/180/90/150/180/70（示例值），加两条组内专用曲线（§二）；不进三档、不复用三档、也不被三档外场景借用。登记锚点：`src/FloatingTransferStation.Core/Design/DesignTokens.cs:102`（组首枚）。

规则要点：

1. **新场景锚定既有档**：直接等值（启动确认＝慢档）或复用档＋既有曲线（peek 收缩＝中档＋加速、展开＝慢档＋减速），无新档无新曲线。锚点：`src/FloatingTransferStation/Views/MainWindow.EdgeHide.cs:144`（收缩 NormalDurationMs）、`MainWindow.EdgeHide.cs:317`（展开 SlowDurationMs）。
2. **独立时长组是极少数契约化例外**：全仓目前仅 QR 容器变换一组；新增独立组必须登记设计契约并给出外部数值依据。
3. **编排型时序值不属档位**：意图窗 60ms、chip 驻留 300ms、收起延迟 250ms、重入门闩 500ms 是时序编排非视觉档，登记于 design.md §六「行为计时器」例外表，各现场以注释锚定。

## 二、缓动（两条曲线，按语义选）

| 语义 | 曲线（WPF KeySpline / 通用 cubic-bezier） | 观感 |
|---|---|---|
| 进入某视觉态（淡入/抬升/入场/展开） | `0,0,0,1`（EnterEasingKeySpline，减速） | 快出缓停 |
| 离开某视觉态（淡出/回落/出场/收缩） | `1,0,1,1`（ExitEasingKeySpline，加速） | 缓出快收 |
| 减弱动效 | Linear（或直接定格终值） | §五 |

锚点：`src/FloatingTransferStation.Core/Design/DesignTokens.cs:140-141`（两条 spline token 字面串）；`src/FloatingTransferStation/Views/FadeAnimation.cs:17-18`（`EnterEasing/ExitEasing` 由 token 构建的 `SplineEase` 单点实例）。

1. **选择规则＝语义，不是数值方向**。判据是「目的地进入视觉态还是离开视觉态」。位移可正可负——「`to < from` 出场加速」只是**透明度语义**下恰好成立的判据（淡出数值必变小），位移抬升（Y 0→−1）数值上也变小，套用即错配加速曲线。fts `LiftAnimation` 曾实测与宣称相反，2026-11-08 修正为按目标态判别：`src/FloatingTransferStation/Views/LiftAnimation.cs:49`（`to == LiftedOffsetPx ? Enter : Exit`）；透明度场景的数值判据见 `src/FloatingTransferStation/Views/FadeAnimation.cs:98`（仅对透明度语义合法）。
2. **缓动单点实例消费**。全部动效经统一缓动工厂取用，`new CubicEase` 在 `src/` 归零；组内专用曲线经同一工厂的 token 参数指定（QR `QrZoomOutKeySpline` 经 `FadeAnimation.ExitEaseToken` 传入，不新增散布实例）。
3. **不对称原则**：出场一般快于入场（观感上收尾不拖）。登记例外：面板展开 167ms／收起 200ms——收起慢于展开，属收起节奏的场景登记值，不是新档位；例外按场景登记并标注，不泛化。

## 三、编排（时序、微交互与可中断性）

**可中断动画四要点**（全部动效的默认实现纪律）：

1. **新状态先复位终值**：`BeginAnimation(prop, null)` → `SetValue(prop, 终值)` → 再挂新动画；动画永不承载终值语义，用户不必等动画播完。
2. **`FillBehavior.Stop`**：动画结束回落到属性值（终值已由第 1 步写定），避免填充值与属性值双账本。注意登场基线：动画自 `Opacity=0` 登场时基线落在终值而非起点，否则 `Stop` 后回落 0 整体隐形（fts 把手登场淡入曾踩此回归）。
3. **`HandoffBehavior.SnapshotAndReplace`**：新动画直接替换在途动画，不叠加。锚点：`src/FloatingTransferStation/Views/FadeAnimation.cs:15`。
4. **隐藏回调防陈旧**：显隐管理为每次隐藏记代数（generation），完成回调校验代数与当前状态，陈旧回调不执行。

**入场事件区分**：表面展开的交错重播 ≠ 新增项落位动画——重排、图标回填、改名、撤销恢复一律不播入场动画；只有真正的新增（新卡、新瓦片）才播。交错步长 40ms 带封顶 `min(i, 8)`（TileEntranceStaggerCap）：第 9 项起同帧出现，长列表尾部不罚等待；瓦片入场另带 8px 纵向位移（区别于卡片 6px）。

**悬停意图事务**（hover 触发激活级动作时）：布防 timer → 到期**重验指针仍在目标内**才提交，离开即取消；迟到旧离开不取消已被取代的新意图。两例：把手展开 60ms（`src/FloatingTransferStation/Views/MainWindow.xaml.cs:252`，token `EdgePeekExpandDwellMs`）、chip 驻留计数 300ms（`src/FloatingTransferStation/Views/MainWindow.SubgroupRail.cs:28`）。防扫边误触由事务重验达成，不另设防抖层。结构性前提：悬停只做纯视觉预览，激活属点击——hover 直接触发激活级内容切换是反模式。

**容器级非线性先例（container transform，QR 放大）**：大位移/形变不走纯淡入。动画对象＝外层整卡（含框飞行、方→矩形形变）；文字后段浮现（≈60% 进度起）；缩略图防双影淡出。收回＝对齐落位回显：缩略图在容器接近落位尺寸后才回显，交接段两元素线性交叉、合成存在感恒≈1，无台阶/空窗/双影。独立时长组＋组内专用曲线（§一/§二），契约 #24 锚定。

## 四、例外通道（恒定几何的唯一窗口级例外）

**恒定几何是默认铁律**：窗口状态迁移一次矩形变更到位（单次 `SetWindowPos` 终态＋`WM_WINDOWPOSCHANGING` 守卫），观察到的每个窗口矩形 ∈ {初始, 终态}；禁止 `SetWindowRgn`，禁止用动效/延迟/隐藏窗口装饰性掩盖中间态。

**唯一例外通道**＝经唯一放置动画服务 `WindowPlacementAnimator` 的连续受控迁移（`src/FloatingTransferStation/Services/WindowPlacementAnimator.cs:59-65` 契约注释；首例＝边缘 peek 整窗同尺寸纯平移）。五条边界：

1. **单一矩形权威**：动画期间几何权威归动画服务；任何外部放置入口（`ApplyPlacement` 族）必须先 `Cancel`（一次 `SetWindowPos` 到达动画终态）再应用新放置（`WindowPlacementAnimator.cs:156-161`）。
2. **逐帧单调**：缓动单调 ⇒ 物理域取整后非严格单调，无来回抖动。
3. **终态唯一**：首帧立即应用、末帧精确落在终态；完成回调在终态落位后发出。
4. **可中断可重定向**：`Redirect` 捕获当前实时进度（按时间戳采样，非末帧值）为新起点，时长按剩余距离比例重映射、封顶满时长，**禁原点重放**（`WindowPlacementAnimator.cs:113-118`）——收缩途中指针追入把手即从当前进度反转展开。
5. **减动效直达**：减弱动效全链跳到终态（§五），例外通道不豁免减动效。

例外必须契约化登记而非散布：STA 锁定测试 `tests/FloatingTransferStation.Tests/MainWindowInteractionTests.VisualLifecycle.cs:1031`（`PlacementAnimation_SingleAuthorityMonotonicTerminalUnique`）＋可注入时钟（生产＝渲染帧时钟，测试＝`ManualPlacementAnimationClock.cs` 手动推帧）让动画可逐帧断言。时长/缓动全部复用既有 token（peek 收缩 167ms 加速出场、展开 250ms 减速入场），例外通道不产生新档新曲线。

## 五、减动效（统一退化口径）

1. **触发同一开关**：系统「减弱动效」或用户在设置关闭动效（`AppPreferences.AnimationsEnabled`）——两者同键：用户覆盖直接写进 `SystemParameters.ClientAreaAnimationKey` 资源，全部动效消费同一开关。锚点：`src/FloatingTransferStation/Views/MainWindow.Settings.cs:432-436`（关闭＝注入 `false` 覆盖；恢复＝`Resources.Remove` 交还系统）。
2. **退化规则**：运动型动效取消（保留状态终值）→ 83ms 纯淡入（`ReducedMotionFadeMs`＝快档）或立即定格终值；缓动退化为 Linear。**时序性延迟保留**（如启动后 250ms 排队收起是时序编排非动效）；窗口级动画（§四）全链直达终态。
3. **恢复即移除覆盖**：重新跟随系统键，不留残余状态。

## 六、原则（评审基准）

1. 进入减速、出场加速，退出比进入快；登记例外（面板 167/200 不对称）按场景标注，不泛化不加档。
2. 动效可中断、可重定向：新动画必须先复位终值；不得让用户等动画播完。
3. 动效不阻断命中测试；不作为传达信息的唯一方式。
4. 减弱动效统一退化口径（纯淡入或定格）；时序性延迟保留。
5. 状态迁移的视觉交接防闪烁机制（收起交接等）不被动画破坏。
6. 图标取自 fluentui-system-icons（MIT）几何重绘，不随应用分发字体文件。

评审口径：新动效逐条对照 §一档位表（无第四档）、§二曲线表（无数值判据）、§三四要点（可中断）、§四（不新增例外通道）、§五（有退化路径）；任一不满足即回设计。

## 附：fts 溯源锚点索引

| # | 锚点 | 内容 | 抽查 |
|---|---|---|---|
| 1 | `src/FloatingTransferStation.Core/Design/DesignTokens.cs:56-58` | 三档时长 83/167/250 | ✅ 基线 commit 实测 |
| 2 | `src/FloatingTransferStation.Core/Design/DesignTokens.cs:140-141` | 两条缓动 spline 字面串 | ✅ 基线 commit 实测 |
| 3 | `src/FloatingTransferStation/Views/FadeAnimation.cs:15-18` | SnapshotAndReplace＋缓动单点实例 | ✅ 基线 commit 实测 |
| 4 | `src/FloatingTransferStation/Views/LiftAnimation.cs:49` | 缓动按语义判别（非数值方向） | ✅ 基线 commit 实测 |
| 5 | `src/FloatingTransferStation/Services/WindowPlacementAnimator.cs:59-65` | 例外通道契约注释（单一权威/单调/终态唯一/重定向） | ✅ 基线 commit 实测 |
| 6 | `tests/FloatingTransferStation.Tests/MainWindowInteractionTests.VisualLifecycle.cs:1031` | 例外通道 STA 锁定测试 | ✅ 基线 commit 实测 |
| 7 | `src/FloatingTransferStation/Views/MainWindow.Settings.cs:432-436` | 减动效同键覆盖/移除 | ✅ 基线 commit 实测 |
| 8 | `src/FloatingTransferStation/Views/MainWindow.EdgeHide.cs:144` | peek 收缩＝NormalDurationMs（锚定不加档） | ✅ 基线 commit 实测 |

> 锚点行号随代码演进漂移属正常；抽查以内容命中为准（token 名/契约短语可 grep 复核）。数值以 `tokens/motion.tokens.json`（建仓前 `DesignTokens.cs`）为准，本文示例值不构成第二权威。
