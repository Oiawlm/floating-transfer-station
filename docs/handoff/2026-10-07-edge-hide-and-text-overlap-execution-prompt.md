# 悬浮中转站 · 卡片文字避让、一次性贴边隐藏与设计契约沉淀 · 执行提示词（2026-10-07）

> **使用方法**：在本仓库根目录（`D:\Download\Agent Vault\悬浮中转站`，main 分支，1.19.0 已发布）打开新的 ZCode（GLM-5.3）窗口，把本提示词全文粘贴为首条消息，并以 **Goal/执行模式** 运行。本提示词携带 2026-10-07 规划窗口的根因分析与已定决策（含 wbx 联网调研 job `20261007-165705-pwo` 与 T7 评审批判 job `20261007-171651-d5u` 后的定稿），无需重新规划；遇到未覆盖的决策点停下来问用户。

## 背景

你是悬浮中转站仓库（GitHub `Oiawlm/floating-transfer-station`）的维护执行代理。本次三项工作：① 修复卡片文字与右上角操作按钮视觉重合；② 顶部新增「一次性贴边隐藏」按钮；③ 把设计契约沉淀为独立规范文档。三项共用一个版本（1.20.0）与一次收尾。

技术栈：.NET 10；Windows 端 WPF（Windows 11 目标，PerMonitorV2 DPI）+ Inno Setup；Mac 端 Avalonia（**2026-09-23 起暂停开发，本次零 Mac 开发**，仅 Core 保持可编译、Apple Silicon CI 保持绿）；共享 `FloatingTransferStation.Core`；MSTest；本机 `.tools/dotnet` 与 Inno 就绪。

**总原则（用户红线）**：任何修改不得以打补丁方式实现——必须重构、调整或融合进既有代码逻辑；最终代码最简且完整实现需求。下面每项「定案」都是这一原则的具体化，不要绕开它们另起旁路。

## 规划窗口结论（第一性原理分析，不要重新发散）

### 任务一：文字与右上按钮重合 —— 根因与定案

**根因**：`Resources/MainWindowStyles.xaml` 卡片模板（844–979 行）按 1.18.0「对半分区」契约分两层——内容视觉层全宽（图片/文字横贯整卡），命中层两列 `*/*` 独立叠加（左半内容区/右半操作区），右半右上角承载置顶/选择按钮（30×30×2，`PinButtonStyle`/`SelectionButtonStyle`，`Opacity=0` 悬停淡入）。分层正确解决了**命中**歧义，但视觉层没有为按钮预留排他空间：悬停文字卡时，按钮恰好淡入在文字第一、二行的右上角之上。两个视觉元素声索同一批像素——这是布局所有权问题，不是渲染问题。

**定案（右侧预留，Gmail 式）**：
- 文字卡 `PreviewText` 的 Kind=Text 样式触发器增加**右侧内缩**：内缩量 = 操作按钮条在内容格中的实际右向占位（两列 `CardOperationColumnWidth` + 与文字的间隙），以**派生资源**引用既有 token（如 `CardTextReservedRightInset`），禁止在别处写死第二个数值。
- 命中层（`CardGestureZones` 叠加层）、左右对半手势语义、图片卡全幅叠放现状**零改动**——已核实命中层是独立叠加 Grid，与文字元素几何无关，内缩不触碰命中几何。
- 如实知晓代价：280px 最窄面板下文字每行约损失 ~68px（≈21%）；这是消除像素争用的确定代价，验收以「任何时刻零重合」为准。不采用「按钮移出文字流/移到卡片外」方案（违背 1.17.1–1.18.0 既定的角按钮契约，改动面更大）。
- **失败先行测试**：STA 渲染测试断言文字卡布局槽与两按钮边界**零交集**（悬停与非悬停两种可见态），先跑确认当前代码失败并保存输出；既有对半分区手势测试（`MainWindowInteractionTests.CardGestures.cs`/`CardHalves.cs`）原样通过。

### 任务二：一次性贴边隐藏 —— 根因与定案

**需求语义**：新顶部按钮按一次 = 进入 armed；鼠标移开后整窗移出屏幕（完全不可见，比收起到轨道 tab 更彻底）；鼠标移回它原来的位置并短暂停留 → 窗口回到原位原形（自动展开）；随后自动解除，再次使用需再按一次。

**第一性原理**：窗口完全离屏后收不到任何鼠标事件，回位检测只有三条路——`GetCursorPos` 定时轮询（业界主流，~100ms；TrafficMonitor、winautohidev2、Electron 实践一致）、全局鼠标钩子（复杂且有摘钩风险）、留窥视条（违背「完全移出」诉求）。**定案：轮询，且仅 Docked 期间运行**。UX 调研：手动 armed 贴边隐藏有先例（Quicker、winautohidev2 Ctrl+←）但全是常驻态；「一次性、唤回即解除」是本产品定义的新语义，必须 armed 状态显式可见 + 多重找回兜底。

**已定架构决策（深度融合既有管线，禁止旁路补丁）**：
1. **状态机进 Core**（参照 `PanelStateMachine` 风格与测试密度）：`Idle ⇄ Armed`（按钮为**切换**语义：再按一次取消）`→ (指针离开且无抑制) → Docked → (回位命中且驻留 ≥200ms) → Idle`。会话级、不持久化（同 PanelHold）。
2. **触发融入既有收起节奏**：不新增触发器。armed 时，`Root_MouseLeave → _collapseTimer(250ms) → CollapseTimer_Tick` 管线不变；tick 处若 `_edgeHide.IsArmed && _panelState.WouldCollapse`（抑制条件**复用**：指针在内/拖拽/文本编辑/PanelHold 同样抑制贴边隐藏）→ 走贴边隐藏分支，否则既有收起流程原样。
3. **隐藏 = 同尺寸纯移动**：窗口保持展开态与当前尺寸**不收起**，`ApplyPlacement`（内部即一次 `SetWindowPos` 的 `TryApplyPlacementAtomically`）把整窗移到**虚拟屏最右缘之外**（沿用迁移原子性契约：一次迁移=一次矩形变更，纯移动无中间态）。面板状态、滚动位置、内容全部原样冻结。不走 `BeginCollapsedVisualHandoff`（那是为收起变形设计的，隐藏不需要）。
4. **纯几何进 `WindowController`**：`EdgeHidden(...)` 以显示器列表（`MonitorBounds`）为输入的新静态纯函数——目标矩形与**每一个**显示器矩形**零交集**为可测不变量（天然规避「右邻显示器接住滑出像素」的多显示器坑；无右邻时与「贴本屏右缘外移」等价）。单测用多显示器输入样例锁定。
5. **回位区 = 隐藏时刻的屏幕内可见矩形（展开矩形）外扩小容差**，并在隐藏时刻（此时 DPI 变换仍有效）**换算为物理像素保存**；轮询时 `GetCursorPos` 物理像素直接比对，**不做任何运行时 DPI 换算**（窗口离屏后其 CompositionTarget 变换不可信，这是规划评审确认的坑）。连续命中累计 **≥200ms** 才回位（过滤扫边误触；经验值来自调研）。如实知晓：右侧滚动条拖拽等驻留动作可能意外唤回——一次性语义把代价限制为一次，属可接受权衡，阈值做成常量便于调整。
6. **恢复 = 纯移动回原矩形**：一次 `SetWindowPos` 移回保存的屏幕内矩形；面板本就未收起，鼠标此刻在窗口内 → `Root_MouseEnter` 自然触发、既有展开/保持机制原样接管——「移回原位自动展开」由此成立，无需任何新展开逻辑。恢复后 disarm、停轮询、`ShowStatus` 简短提示。
7. **找回兜底（多重）**：a) 既有全局唤起热键（1.10.0）在 Docked 态触发 → 恢复+disarm；b) `WM_DISPLAYCHANGE`/`WM_SETTINGCHANGE` 显示器变化在 Docked 态 → 立即恢复+disarm（防找不到窗口）；c) armed 态按钮前景切 Accent（同 PanelHoldButton 激活语义，**头部按钮常显**——注意与卡片的悬停淡入按钮无关）+ ToolTip 更新，隐藏时 `ShowStatus("已移出屏幕，鼠标移回原位即可唤回。")`。
8. **armed 生命周期**：仅展开态有意义；若面板经其他路径离开展开态（如外部拖放收起）且未进入 Docked → 自动解除，不留悬置 armed。
9. **轮询**：`DispatcherTimer` ~100ms（常量），**仅 Docked 期间运行**；`GetCursorPos` 为 `NativeMethods` 新增 P/Invoke（沿用现有 internal static 风格与注释密度）。不采用哨兵窗方案（额外 HWND 与单实例/置顶分区交互复杂，轮询已有界）。
10. **按钮**：`HeaderActions` 内、`PanelHoldButton` 之后（同为「在场行为」组），第 6 个；图标遵守 14.5×14.5 + 1.5 描边规范（「箭头收入右缘」语汇，同 PanelHold/Search/Reset 的 Path 写法）；ToolTip 与 AutomationProperties 写明一次性语义（如「一次性移出屏幕：鼠标离开后收进屏幕边缘，移回原位自动展开」）。**不做窗口位移动画**（全仓放置迁移从不动画，保持一致；动画列为后续候选）。

**失败先行测试**：Core 状态机单测（切换语义、一次性、抑制复用、其他收起路径解除、Docked→回位→disarm）；`EdgeHidden` 纯几何（零交集不变量、多屏样例）；回位区纯几何（外扩容差、物理像素）；STA 交互测试（armed+模拟离开+tick → 窗口矩形离屏且与显示器零交集、面板仍展开；模拟轮询驻留 ≥200ms → 恢复原矩形且 disarm；恢复后再次离开 → 仅普通轨道收起；Armed 再按 → 取消且此后离开仅普通收起）。

### 任务三：设计契约沉淀 —— 现状与定案

**现状**：三层记忆体系已存在——常驻层 `AGENTS.md`（红线）、活规范层 `docs/design.md`（视觉/动效规范）+ `docs/improvement-loop.md` 内嵌「设计冻结清单」（11 条行为契约）、历史层 `docs/superpowers/specs+plans` + `docs/README.md` 索引。**缺口**：规范性契约清单埋在夜间循环**流程文档**里（白天开发不必然读到）；AGENTS.md 必读链未引用它；契约条目无测试锚点；1.8.x 迁移原子性、1.17.1 真实输入、1.18.0 对半分区、1.19.0 性能根因等**后续契约未登记**。调研结论（agents.md 规范/Claude Code memory/分层记忆共识，见 job 底稿）：常驻层小而稳、只放指针；契约详情放活规范层独立文档；条目 = 编号断言 + 由来 + 锁定测试 + 关联决策记录。

**定案**：
1. 新建 `docs/design-contracts.md`（设计契约清单）：把 improvement-loop.md 冻结清单**迁出**为第一公民，每条升级为四字段——「断言（一句可判定的话）/由来（版本+一句根因背景）/锁定（测试名或文件；确实没有的如实标"无锁定测试（补测候选）"，不编造）/关联（spec/plan 链接）」；**补登**上述未登记契约；**新增**本次两条（文字避让操作列预留、一次性贴边隐藏语义含多重找回兜底）。
2. `improvement-loop.md` 原清单段改为指向新文档的指针（夜间循环规则中"冻结清单"名称保留、指向更新）。
3. `AGENTS.md` 首条工作规则加入 `docs/design-contracts.md` 必读（只加指针行，常驻层保持精简）。
4. `docs/README.md` 索引登记新文档。
5. 本次功能 spec+plan 照常落 `docs/superpowers/`（`2026-10-07-text-ops-avoidance-and-edge-hide-design.md` 及对应 plan），登记索引表。

## 总目标（一句话，可核对）

文字卡与操作按钮任何时刻零像素重合（测试锁定）；顶部第 6 个按钮实现「按一次→鼠标离开→整窗移出任何显示器（同尺寸纯移动）、鼠标移回原位驻留 ≥200ms→回到原位原形并自动解除」的一次性贴边隐藏（状态机+纯几何进 Core、融入既有收起节奏、多重找回兜底，测试锁定）；设计契约清单成为独立规范文档并被 AGENTS.md 必读链引用、新旧契约含测试锚点登记完整；全部通过既有质量门与 CI，完成 v1.20.0 统一发行并静默安装到用户本机。

## 必读输入（动手前按序读完）

`AGENTS.md`（优先级最高）→ 本提示词 → `README.md`、`CONTRIBUTING.md`、`PROJECT_GUIDE.md`、`docs/design.md`、`docs/releasing.md`、`docs/handoff/README.md`、`docs/improvement-loop.md`（冻结清单段）→ 源码：`src/FloatingTransferStation/Views/MainWindow.VisualTransitions.cs`（Root_MouseLeave/CollapseTimer_Tick/CommitPanelCollapse 家族）、`Views/MainWindow.PanelHold.cs`（头部按钮切换语义范本）、`Views/MainWindow.xaml.cs`（定时器字段）、`Views/MainWindow.xaml`（HeaderActions）、`Views/MainWindow.Lifecycle.cs`（ApplyPlacement/TryApplyPlacementAtomically/ReassertTopmost/WndProc）、`Resources/MainWindowStyles.xaml`（卡片模板 844–979、`CardOperationColumnWidth`、`PinButtonStyle`）、`Services/{WindowController,ScreenEdgeGeometry,NativeMethods}.cs`、`src/FloatingTransferStation.Core/Services/PanelStateMachine.cs` → 测试：`MainWindowInteractionTests.{CardGestures,CardHalves,CardRendering,VisualLifecycle}.cs`、`PanelStateMachineTests.cs`、`WindowControllerTests.cs` → 调研底稿：`~/.wbx/jobs/20261007-165705-pwo/`（贴边技术方案/贴边 UX 语义/代理记忆文档设计三份报告）与 `~/.wbx/jobs/20261007-171651-d5u/`（本提示词的 T7 评审批判）。

## WBX 桥使用要求（必须用，用户明确要求）

先 `node "C:\Users\Lenovo\.zcode\wbx-bridge\scripts\wbx.mjs" doctor`；ai/cn 任一绿即用，全红则如实告知并自己做。最低用量：
1. **T8 双份择优**：任务一 XAML 预留实现与任务二 Core 状态机骨架，各并行 2 份（不同 lane 同题），你逐行评审择优、留评审记录；产物只是草稿，集成前必须由你逐行审查改写为与既有风格融合的最终代码（派发结果不派发步骤）。
2. **T7 评审批判**：全部代码改动集成前，把 diff 要点 + 契约清单草稿打包给桥做一轮批判性评审（重点：与既有契约冲突、遗漏边界、补丁味），按结论修正后再进门禁。
3. 调研与提示词评审已由规划窗口完成（job 见上），不要重复外派同类任务。
4. 失败降级：连续 ≥2 失败或限流 → 停止外包自己完成并如实记录。

## 轨道

**S0（第一步）**：spec+plan 落盘（`docs/superpowers/specs/2026-10-07-text-ops-avoidance-and-edge-hide-design.md` 与 `docs/superpowers/plans/2026-10-07-text-ops-avoidance-and-edge-hide.md`），登记 `docs/README.md` 索引；本提示词原文已在 `docs/handoff/2026-10-07-edge-hide-and-text-overlap-execution-prompt.md`，随首个提交一并入库。

**轨道一：文字避让（失败先行）**
1. 新增 STA 渲染测试：文字卡布局槽与两按钮边界零交集（悬停/非悬停两态）；先跑确认当前代码失败并保存输出。
2. WBX T8 双份 → 择优 → 派生资源 + Kind=Text 触发器右内缩实现；既有 CardGestures/CardHalves/CardRendering 测试原样通过。
3. computer-use 实机截图取证：悬停长文字卡前后对比（不入库）。

**轨道二：一次性贴边隐藏（失败先行）**
1. Core 失败先行单测：状态机（切换/一次性/抑制/解除）、`EdgeHidden` 纯几何（含多显示器样例）、回位区纯几何。
2. WBX T8 双份状态机骨架 → 择优 → 实现：Core 状态机与几何；Windows 端按钮（含图标/ToolTip/armed 视觉）、`CollapseTimer_Tick` 分支、`GetCursorPos` 轮询、热键与显示器变化兜底、`ShowStatus` 反馈。
3. STA 交互测试（见任务二测试清单）全部转绿。
4. computer-use 实机录屏取证：armed→离开→移出→移回驻留→原位原形展开→再次离开仅普通收起；热键找回、显示器变化兜底各录一段（不入库）。

**轨道三：契约沉淀**
按任务三定案完成 `docs/design-contracts.md` 迁出+补登+新增、improvement-loop.md 指针化、AGENTS.md 必读行、docs/README.md 索引；「锁定」字段宁缺毋滥，不编造测试名。WBX T7 评审契约清单草稿后定稿。

**轨道四：文档与版本**
`docs/design.md` 补记文字避让与贴边隐藏两节契约；README 功能说明与截图同步（UI 有新增按钮，属用户可见变化）；CHANGELOG 未发布区 → 1.20.0；ROADMAP 如有对应条目同步。

## 收尾（必做，不可选）

1. `version.txt` → **1.20.0**（新功能 minor）。
2. 质量门：Core Debug 预构建 → `dotnet format --verify-no-changes` → Release 全量测试 → 严格 Release 构建 0 警告（顺序与命令见 PROJECT_GUIDE.md/CONTRIBUTING.md）。
3. 任务分支 → PR → CI（含 Apple Silicon）全绿 → 合入 main；Core 新类型对 Mac 仅需可编译，不做任何 Mac 接线（暂停令）；CI 因本次改动失败仅做恢复绿色最小修复。
4. 统一发行 v1.20.0：`build-release.ps1 -ForRelease` → 同一 GitHub Release 上传 Windows 安装包、`osx-arm64` zip（如实标注未公证测试状态）、`SHA256SUMS.txt`；README 下载入口同步；记录链接与 SHA256。
5. 本机安装：优雅关闭运行实例 → 静默原地更新 → 重启 → 验证版本 1.20.0 与自启项，向用户展示验证结果与轨道取证。

## 硬约束（红线，全文以 AGENTS.md/CONTRIBUTING.md 为准）

- 不得打补丁：必须重构/融合进既有逻辑（触发融入收起节奏、几何进 WindowController、状态机进 Core 即此要求的具体体现）；最终代码最简且完整。
- 持久化原子性、置顶分区、批量顺序、拖放源数据、安全卸载边界、`PanelStateMachine` 语义、对半分区手势契约、迁移原子性（一次迁移=一次矩形变更）不可改变；改契约先开 Issue。
- Mac 端零开发（暂停令）；Core 改动保持 Mac 可编译、CI 绿。
- 每项行为改动先补失败测试再实现；UI 变化必须 computer-use 真实截图/录屏证据（不入库）。
- 不提交 `.tools/`、`artifacts/`、`TestResults/`、用户内容、凭据、本机截图。
- 常设授权：任务分支、普通推送、PR 与处理本次 CI 失败；不含强推、凭据配置、仓库/全局设置变更。

## 执行方式

Goal/执行模式直接开工：S0 → 轨道一 → 轨道二 → 轨道三 → 轨道四 → 收尾；小步提交（每轨道至少一次），每阶段跑相关测试选集，最后全量质量门。任何决策点拿不准或影响契约/公开行为的停下来问用户。

## 验收标准（Done when）

- 文字卡与操作按钮零交集测试从失败转绿，对半分区手势测试原样通过；悬停取证截图已展示。
- 贴边隐藏：按钮切换语义与一次性、抑制复用、离屏与所有显示器零交集（同尺寸纯移动）、回位驻留 ≥200ms 后回到原位原形并 disarm、热键/显示器变化兜底、其他收起路径解除 armed——全部有测试锁定；实机录屏取证已展示。
- `docs/design-contracts.md` 存在且含迁移+补登+新增契约（每条四字段齐全），improvement-loop.md/AGENTS.md/docs/README.md 三处指针更新。
- 质量门与 CI 全绿；v1.20.0 统一 Release 已发布；本机已静默更新并验证版本与自启。
