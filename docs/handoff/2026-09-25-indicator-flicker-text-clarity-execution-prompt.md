# 悬浮中转站 · 拖拽指示条闪烁 + 卡片文字清晰度 · 新窗口执行提示词（2026-09-25）

> **使用方法**：在本仓库根目录（`D:\Download\Agent Vault\悬浮中转站`，main 分支，v1.11.2 已发布）打开新的 ZCode（GLM-5.3）窗口，把本提示词全文粘贴为首条消息，并以 **Goal/执行模式** 运行。本提示词携带 2026-09-25 规划窗口的定稿结论（根因已定位、方案已评审），无需重新规划；执行中遇到未覆盖且影响契约/公开行为的决策点，记入待裁决清单并选风险更小的做法继续，不阻塞等待。

## 背景

你是悬浮中转站仓库（GitHub `Oiawlm/floating-transfer-station`）的维护执行代理。规划窗口已完成：两个用户报障的根因定位（逐行代码取证 + wbx 外部双评审校验）、修复方案定稿、验收标准制定；并把「卡片内容编辑 + Markdown」方向补记入候选池（PR #56，合并状态以仓库为准）。本提示词未入库，由你按轨道三落盘。

技术栈：.NET 10；Windows 端 WPF（Windows 11 为目标，PerMonitorV2 DPI）+ Inno Setup；Mac 端 Avalonia（**按 2026-09-23 指令暂停一切开发**，仅保留 CI 回归）；共享 `FloatingTransferStation.Core`；MSTest；本机 `.tools/dotnet` 与 Inno 就绪。你有 wbx 外部算力桥（全局 AGENTS.md 有用法，先 doctor 再用；适合：独立测试模块草稿、文档段落、双方案对比；产物必须逐行审查后才进交付物）。

两个修复相互独立，各出一个 `fix/` PR；都合入 main 后一起出 **1.11.3**（修复=patch）。

## 规划窗口结论（不要重新发散）

### Bug A：分类内拖动排序时插入指示条闪烁

**用户报障**：图片标签有两个置顶项；按住下方项往上拖，置顶区边界附近的强调色横条随鼠标移动不停闪（显示↔隐藏交替）；往下拖同样间歇性闪烁；悬停列表空白区时横条位置跳变。

**根因链**（`Views/MainWindow.DragAndDrop.cs:511` `BoardList_PreviewDragOver` → `Views/MainWindow.xaml.cs:512` `GetBoardDropLocation` → `Core/Services/BoardService.cs:551` `IsSameCategoryTargetValid`）：
1. 插入索引由「命中哪个 ListBoxItem + 上/下半区」得出；非置顶拖动要求 `index >= pinnedCount`、置顶拖动要求 `index <= pinnedCount`，非法即 `HideInsertionIndicator()` + `Effects=None`。合法/非法分界恰落在置顶边界行的中线，±1px 抖动即交替隐藏/显示（隐藏=`Collapsed`+淡出、显示=淡入），肉眼即闪烁。两个拖动方向互为镜像。
2. 光标落在列表上下 Padding（`BoardListStyle` Padding="0,4"）、末项下方空白或竖直滚动条上时，`OriginalSource` 不属任何 ListBoxItem 子树 → 回退分支直接取 `index=Items.Count`、指示条跳到可视末尾——非置顶拖动瞬移，置顶拖动再叠加非法→隐藏。（卡片之间不触发：卡片 Margin 在 ListBoxItem 内，项模板背景 Transparent 可命中。）
3. 现有测试 `PinnedDrag_CrossRegionHidesInsertionIndicatorInBothDirections`（`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.DragAndDrop.cs:1426`）把「跨区=隐藏+None」锁定为契约——**本次有意变更该契约**，须改写测试并在 CHANGELOG 说明。`MixedPinBatch_*` 锁定的混合置顶批量语义不变。

**已定修复方案**（第一性原理：插入槽位必须是光标 Y 的单调连续几何函数；分区非法用钳制表达，不隐藏；不引入滞回/防抖掩盖）：
- **几何槽位解析**：枚举已实现（realized）的 ListBoxItem 在 BoardList 坐标系的边缘矩形，按光标 Y 对相邻条目边缘做中点判定。纯函数放共享 Core（扩展 `DropInsertionCalculator` 或新建 `InsertionSlotResolver`，入参 `(itemIndex, topEdge, bottomEdge)[] + cursorY`，出参 `(insertionIndex, indicatorEdgeY)`），Mac 不调用新入口、行为不动。
- **分区钳制**：单一置顶状态批量拖动，显示与落点统一钳制（非置顶 `max(index, pinnedCount)`、置顶 `min(index, pinnedCount)`），指示条稳定停在钳制槽、`Effects` 恒为 Move（同时消除 OLE 光标 None↔Move 闪烁）；`PreviewDrop` 用同一钳制索引。
- **混合置顶批量**维持现状（不显示、None）。
- 实现坑（wbx 评审补充，务必处理）：每事件重算 `TranslatePoint`（DragOver 是 Input 优先级，滚动后布局陈旧；回收容器身份会变，禁止缓存矩形）；条目索引用 `Items.IndexOf(container.DataContext)` 真实索引，勿假设首/末 realized == 0/总数；索引语义为「移除被拖项之前」（与 `MoveMany` 的 `preRemovalIndex` 对齐）；空列表/无 realized 提前返回；指示条显示仍过 `ClampIndicatorY`；`BoardList_PreviewDragLeave` 离开列表隐藏保留；不改编 `MoveMany`/`IsSameCategoryTargetValid` 既有校验（钳制只在交互层）。

### Bug B：卡片正文文字发模糊

**用户报障**：图片/文本/待分类标签里的内容、剪贴板采集内容的字体「越来越模糊」。时间线：09-23 引入 Mica 壳（706db3c：`Window Background=Transparent` + `GlassFrameThickness=-1` + DWM Mica）；09-25 的 1.10.2 只给分类标签两个 12px 样式加了 `TextFormattingMode=Display`，其余文本未动。

**根因链**（按影响排序；DPI 感知已核实 PerMonitorV2、卡片底色本就不透明，均排除）：
1. **主因：`CardContainerStyle.Effect = CardShadowEffect`（DropShadowEffect）**（`Resources/MainWindowStyles.xaml:259` 起，阴影定义在 `DesignTheme.Light/Dark.xaml:24`）。WPF 中带 `Effect` 的元素把**整个子树**渲染进中间位图 → ClearType 强制关闭（灰度抗锯齿），高 DPI 下中间位图重采样进一步软化。每张卡正文都中招，与用户描述范围完全吻合。
2. **次因：窗口级 alpha 表面**（Mica 壳）——不在不透明底板上的文本自动灰度抗锯齿（解释 1.10.2 标签修复为何有效：Display 整数网格对齐）。
3. **加重项：正文 `FontSize=14` + `LineHeight=18`（BlockLineHeight）**——18 DIP 低于 Segoe UI/微软雅黑 14px 自然行高（约 18.6–19.6），中文上下缘视觉裁切 + Ideal 模式基线落在亚像素位置。
4. **瞬态项：`LiftAnimation` 悬停 -1 DIP**——125%/150% DPI 下非整数设备像素，仅悬停期模糊，本次不强修（观察后可记候选池）。

**已定修复方案**（先取证再动手）：
- **2a 主修**：卡片模板重构为「外层容器（承载 LiftAnimation 与卡片 Margin/`StartsNormalRegion` 触发器）+ 阴影层 Border（不透明 CardBrush 底 + DropShadowEffect + 同 CornerRadius、`IsHitTestVisible=False`、无内容）+ 内容层 Border（不透明 CardBrush 底、无 Effect、承载既有悬停/选中层与全部内容）」。文字直接渲染在不透明表面，ClearType 自动恢复；阴影形状与现状一致。
- **2b 小字号像素对齐（沿用 1.10.2 先例）**：卡片正文（14）、复盘编辑器（14，与正文同模式）、状态文本（12）、徽标计数（9）加 `TextOptions.TextFormattingMode=Display`；16px 标题保持 Ideal。**不**无差别强推 `TextRenderingMode=ClearType`——2a 后不透明底上 Auto 即 ClearType；深色下强推有彩边风险；已发布的标签样式不动。
- **2c 行高修正**：正文 `LineHeight` 18→20、`MaxHeight` 90→100（保持 5 行）；CHANGELOG 说明卡片高度 ±10 DIP 变化。
- **2d 证据门控可选**：若 400% 截图仍整体半像素发虚，面板内容根加 `UseLayoutRounding=True`；无收益则不加。

## 总目标（一句话，可核对）

以失败先行的测试锁定两个缺陷（指示条在分区边界 ±1px 抖动下持续可见且位置稳定、空白区不跳变；卡片文字在 400% 放大下恢复 ClearType 级锐利），按已定方案实现转绿，通过全部既有质量门与 CI（含 Apple Silicon 回归），完成 v1.11.3 统一发行并静默安装到用户本机、验证版本与自启。

## 必读输入（动手前按序读完）

`AGENTS.md`（优先级最高）→ 本提示词 → `README.md`、`CONTRIBUTING.md`、`PROJECT_GUIDE.md`、`docs/improvement-loop.md`（冻结清单）、`docs/releasing.md`、`docs/handoff/README.md` → 源码：`Views/MainWindow.DragAndDrop.cs`、`Views/MainWindow.xaml.cs`（`GetBoardDropLocation`/`GetVisibleListEndY`/`ClampIndicatorY`）、`Core/Services/{DropInsertionCalculator,BoardService}.cs`、`Resources/MainWindowStyles.xaml`（`BoardListStyle`/`BoardListItemStyle`/`CardContainerStyle`/`DataTemplate DataType=BoardItem`）、`Resources/DesignTheme.Light.xaml`、`Views/FadeAnimation.cs`、`Views/LiftAnimation.cs`、`Services/DwmWindowEffects.cs`、`Views/MainWindow.xaml`（InsertionIndicator 350–365、WindowChrome 24–30）→ 测试：`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.DragAndDrop.cs`（`NewDragEventArgs` 手法、1426 行契约测试、`MixedPinBatch_*`）、`MainWindowInteractionTests.CategoryEditing.cs`（1.10.2 样式锁定测试模式）→ 上一轮同类交接：`docs/handoff/2026-09-23-placement-atomicity-execution-prompt.md`（收尾与取证惯例）。

## 轨道一：失败先行测试（S0 之后第一步）

**Bug A**：
1. Core 单测（几何解析器）：单调性（cursorY 递增索引不减）、相邻槽中点翻转、首项上方/末项下方/卡片间隙、钳制边界（P=0、全部置顶、索引越界、空输入）。
2. WPF 交互测试（沿用 `NewDragEventArgs`+`PreviewDragOverEvent`）：
   - **闪烁复现**：非置顶拖动在「最后一个置顶项上半区 ↔ 下半区」来回 ±1 DIP，断言指示条持续可见且位置稳定在钳制槽（当前代码因隐藏而失败）；
   - **跳变复现**：光标在末项下方空白区/Padding，断言槽位与几何一致、不跳末尾（当前失败）；
   - **改写契约测试** `PinnedDrag_CrossRegionHidesInsertionIndicatorInBothDirections` → 跨区显示钳制槽、`Effects=Move`；
   - 置顶拖动向下越界钳制到 pinnedCount；混合批量行为不变；Drop 落点与指示条一致（既有断言适配）。

**Bug B**：
3. 样式锁定测试（1.10.2 模式）：2b 涉及的每个样式 `TextFormattingMode=Display`；卡片模板结构断言：内容层 Border 无 `Effect`、阴影层无子内容。

## 轨道二：实现（按已定方案，测试转绿）

- Bug A 分支 `fix/nightly-20260925-insert-indicator-stability`：Core 几何解析器 + WPF 层接线（`GetBoardDropLocation` 重写为几何枚举 + 钳制，删除回退分支跳末尾逻辑；`PreviewDragOver`/`PreviewDrop` 一致化）。
- Bug B 分支 `fix/nightly-20260925-card-text-clarity`：模板重构（2a）+ 样式 Display（2b）+ 行高（2c）+ 视取证决定 2d。
- 两分支无代码交集可并行；每分支小步提交、每步跑相关选集。

## 轨道三：取证与文档（与代码同步提交）

1. **S0（执行第一步）**：按仓库惯例落盘 spec+plan（`docs/superpowers/specs/2026-09-25-indicator-flicker-text-clarity-design.md`、`docs/superpowers/plans/2026-09-25-indicator-flicker-text-clarity.md`），登记 `docs/README.md` 索引；本提示词原文落盘 `docs/handoff/2026-09-25-indicator-flicker-text-clarity-execution-prompt.md` 一并提交。
2. **真实取证（必做，不入库）**：`FTS_PREVIEW_DATA_DIR`（可选 `FTS_PREVIEW_THEME`）隔离实例——Bug A：修复前后各录一段四方向拖动（置顶 2 项 + 下方若干项），修复后无闪烁；Bug B：修复前后 400% 放大截图（浅+深各一张文字卡），修复后浅色主题应见 ClearType 彩色镶边且笔画锐利。截图/录屏向用户展示，不入库。
3. `CHANGELOG.md` 未发布区（含契约变更说明）；README「几个常用操作」若拖动语义描述受影响则同步；`docs/design.md` 如有对应小节补记。

## 收尾（必做，不可选；按 2026-09-23 常设授权）

1. 质量门四连（命令与顺序见 PROJECT_GUIDE.md/CONTRIBUTING.md）：Core Debug 预构建 → `format --verify-no-changes` → Release 全量测试 → 严格构建 0 警告；外加 `scripts/check-repo-hygiene.ps1`。
2. 两 PR → CI「格式、测试与构建」+ Apple Silicon 全绿 → 合入 main → main 复跑 Release 测试。Apple Silicon 因 Core 新增代码变红时仅做恢复绿色的最小修复。
3. 统一发行 v1.11.3：`CHANGELOG` 未发布区整理为 1.11.3、`version.txt` 同步 → `build-release.ps1 -ForRelease` → 同一 GitHub Release 上传 Windows 安装包、`osx-arm64` zip（如实标注未公证测试版）、`SHA256SUMS.txt`；README 下载入口同步。
4. 本机安装：优雅关闭运行中的实例 → 静默原地更新 → 重启 → 验证版本 1.11.3 与自启正常，向用户展示验证结果与轨道三取证。
5. 当晚切片记入 `docs/research/loop-2026-09.md`（含待裁决清单），提交推送。

## 硬约束（红线，全文以 AGENTS.md/CONTRIBUTING.md 为准）

- 持久化原子性、置顶分区、批量顺序、拖放源数据、安全卸载边界、`PanelStateMachine` 语义不可改变；改公开契约先开 Issue（本次指示条契约变更属修复报障、已由用户报障与规划窗口定稿授权，在 CHANGELOG 说明即可）。
- Mac 端零开发（暂停令）；Core 改动保持 Mac 可编译、Mac 测试与 CI 绿色。
- 每项行为改动先补失败测试再实现；UI 变化必须真实截图/录屏证据（禁生成图冒充；不入库）。
- 不提交 `.tools/`、`artifacts/`、`TestResults/`、用户内容、凭据或本机截图；不动 `.zcode/` 与用户本地文件。
- 常设授权：任务分支、普通推送、PR 与处理本次 CI 失败；不含强推、凭据配置、仓库/全局设置变更。
- 修复先复现、根因优先：禁止用动画、延迟、防抖计时器或重新隐藏等装饰手段掩盖闪烁。

## 执行方式

Goal/执行模式直接开工：S0 落盘 → 轨道一（失败测试，保存失败输出）→ 轨道二（实现转绿）→ 轨道三（取证与文档）→ 收尾；两个修复可先后或交替推进，各自成 PR。任何决策点拿不准或影响契约/公开行为的：选风险更小的做法、记待裁决、继续推进。

## 验收标准（Done when）

- [ ] Bug A：几何槽位 + 分区钳制上线；闪烁/跳变复现测试由红转绿；旧契约测试改写；四方向拖动录屏无闪烁；混合批量、跨分类拖放、批量顺序、撤销语义无回归。
- [ ] Bug B：阴影移出文字子树；小字号 Display；行高 ≥20；浅/深 400% 前后对比证明清晰度恢复（浅色见 ClearType 镶边）；样式与模板结构测试通过；断行/行数无异常。
- [ ] 两 PR 过完整质量门并合入 main；main Release 测试绿；Apple Silicon CI 绿。
- [ ] v1.11.3 统一发行（Windows + osx-arm64 同 Release + SHA256SUMS）；本机已更新到 1.11.3 并验证版本与自启。
- [ ] spec+plan、handoff 提示词、CHANGELOG/README/loop 日志齐备；`git status` 干净。
