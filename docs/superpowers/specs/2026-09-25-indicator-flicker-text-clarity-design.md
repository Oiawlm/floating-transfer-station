# 设计规格：插入指示条闪烁 + 卡片文字清晰度（2026-09-25）

本规格承接 2026-09-25 规划窗口的定稿结论（根因取证与 wbx 外部双评审见 `docs/handoff/2026-09-25-indicator-flicker-text-clarity-execution-prompt.md`），范围仅限其中已定决策；两个修复相互独立，各出一个 `fix/` PR，都合入 main 后一起出 1.11.3（修复=patch）。

## 问题 A：分类内拖动排序时插入指示条闪烁

**用户报障**：图片标签有两个置顶项；按住下方项往上拖，置顶区边界附近的强调色横条随鼠标移动不停闪（显示↔隐藏交替）；往下拖同样间歇性闪烁；悬停列表空白区时横条位置跳变。

**根因**（`MainWindow.DragAndDrop.cs` `BoardList_PreviewDragOver` → `MainWindow.xaml.cs` `GetBoardDropLocation` → `BoardService.IsSameCategoryTargetValid`）：

1. 插入索引由「命中哪个 ListBoxItem + 上/下半区」得出；非置顶拖动要求 `index >= pinnedCount`、置顶拖动要求 `index <= pinnedCount`，非法即 `HideInsertionIndicator()` + `Effects=None`。合法/非法分界恰落在置顶边界行的中线，±1px 抖动即交替隐藏/显示（隐藏=`Collapsed`+淡出、显示=淡入），肉眼即闪烁；OLE 光标 None↔Move 同步交替。两个拖动方向互为镜像。
2. 光标落在列表上下 Padding（`BoardListStyle` Padding="0,4"）、末项下方空白或竖直滚动条上时，`OriginalSource` 不属任何 ListBoxItem 子树 → 回退分支直接取 `index=Items.Count`、指示条跳到可视末尾。非置顶拖动瞬移，置顶拖动再叠加非法→隐藏。

### 已定决策（不再发散）

第一性原理：**插入槽位必须是光标 Y 的单调连续几何函数；分区非法用钳制表达，不隐藏；不引入滞回/防抖掩盖。**

- **几何槽位解析**：枚举已实现（realized）的 ListBoxItem 在 BoardList 坐标系的边缘矩形，按光标 Y 对相邻条目中心做单调判定。纯函数放共享 Core（新建 `InsertionSlotResolver`，入参 `(itemIndex, topEdge, bottomEdge)[] + cursorY`，出参 `(insertionIndex, indicatorEdgeY)`）；既有 `DropInsertionCalculator` 与其测试保留不动，Mac 不调用新入口、行为不动。
- **分区钳制**：单一置顶状态批量拖动，显示与落点统一钳制（非置顶 `max(index, pinnedCount)`、置顶 `min(index, pinnedCount)`），指示条稳定停在钳制槽、`Effects` 恒为 Move（同时消除 OLE 光标闪烁）；`PreviewDrop` 用同一钳制索引。
- **混合置顶批量**维持现状（不显示、None）——`CanMoveMany`/`IsSameCategoryTargetValid` 既有校验不改，钳制只发生在交互层。
- **契约有意变更**：既有测试 `PinnedDrag_CrossRegionHidesInsertionIndicatorInBothDirections` 锁定的「跨区=隐藏+None」改为「跨区=钳制槽 + Move」，须改写测试并在 CHANGELOG 说明（属修复报障，已由用户报障与规划窗口定稿授权）。
- **实现要点**（wbx 评审补充）：每事件重算 `TranslatePoint`（DragOver 是 Input 优先级，滚动后布局陈旧；回收容器身份会变，禁止缓存矩形）；条目索引用 `Items.IndexOf(container.DataContext)` 真实索引，勿假设首/末 realized == 0/总数；索引语义为「移除被拖项之前」（与 `MoveMany` 的 `preRemovalIndex` 对齐）；空列表/无 realized 提前返回；指示条显示仍过 `ClampIndicatorY`；`BoardList_PreviewDragLeave` 离开列表隐藏保留；删除回退分支跳末尾逻辑（`GetVisibleListEndY`）。

## 问题 B：卡片正文文字发模糊

**用户报障**：图片/文本/待分类标签里的内容、剪贴板采集内容的字体「越来越模糊」。时间线：09-23 引入 Mica 壳（706db3c：`Window Background=Transparent` + `GlassFrameThickness=-1` + DWM Mica）；09-25 的 1.10.2 只给分类标签两个 12px 样式加了 `TextFormattingMode=Display`，其余文本未动。

**根因**（按影响排序；DPI 感知已核实 PerMonitorV2、卡片底色本就不透明，均排除）：

1. **主因：`CardContainerStyle.Effect = CardShadowEffect`（DropShadowEffect）**。WPF 中带 `Effect` 的元素把整个子树渲染进中间位图 → ClearType 强制关闭（灰度抗锯齿），高 DPI 下中间位图重采样进一步软化。每张卡正文都中招，与用户描述范围完全吻合。
2. **次因：窗口级 alpha 表面**（Mica 壳）——不在不透明底板上的文本自动灰度抗锯齿（解释 1.10.2 标签修复为何有效：Display 整数网格对齐）。
3. **加重项：正文 `FontSize=14` + `LineHeight=18`（BlockLineHeight）**——18 DIP 低于 Segoe UI/微软雅黑 14px 自然行高（约 18.6–19.6），中文上下缘视觉裁切 + Ideal 模式基线落在亚像素位置。
4. **瞬态项：`LiftAnimation` 悬停 -1 DIP**——125%/150% DPI 下非整数设备像素，仅悬停期模糊，本次不强修（记入候选池观察）。

### 已定决策（不再发散）

- **2a 主修（模板重构）**：卡片模板改为「外层容器（承载 LiftAnimation 与卡片 Margin/`StartsNormalRegion` 触发器，无 Effect）+ 阴影层 Border（不透明 CardBrush 底 + DropShadowEffect + 同 CornerRadius、`IsHitTestVisible=False`、无内容）+ 内容层 Border（不透明 CardBrush 底、无 Effect、承载既有悬停/选中层与全部内容）」。文字直接渲染在不透明表面，ClearType 自动恢复；阴影形状与现状一致。
- **2b 小字号像素对齐（沿用 1.10.2 先例）**：卡片正文（14）、复盘编辑器（14，与正文同模式）、状态文本（12）、徽标计数（9）加 `TextOptions.TextFormattingMode=Display`；16px 标题保持 Ideal。**不**无差别强推 `TextRenderingMode=ClearType`——2a 后不透明底上 Auto 即 ClearType；深色下强推有彩边风险；已发布的标签样式（1.10.2）不动。
- **2c 行高修正**：正文 `LineHeight` 18→20、`MaxHeight` 90→100（保持 5 行）；CHANGELOG 说明卡片高度 ±10 DIP 变化。
- **2d 证据门控可选**：若 400% 截图仍整体半像素发虚，面板内容根加 `UseLayoutRounding=True`；无收益则不加。

## 验证

- **失败先行（Bug A）**：Core 单测锁定几何解析器单调性（cursorY 递增索引不减）、相邻槽中点翻转、首项上方/末项下方/卡片间隙、钳制边界（P=0、全部置顶、索引越界、空输入）；WPF 交互测试复现闪烁（非置顶拖动在最后一个置顶项上下半区 ±1 DIP 来回，指示条持续可见且位置稳定在钳制槽）与跳变（末项下方空白区/Padding 槽位与几何一致）；改写跨区契约测试为钳制槽 + Move。
- **失败先行（Bug B）**：样式锁定测试（1.10.2 模式）断言 2b 涉及的每个样式 `TextFormattingMode=Display`；卡片模板结构断言：内容层 Border 无 `Effect`、阴影层无子内容。
- **既有测试**：`MixedPinBatch_*`、跨分类拖放、批量顺序、撤销语义、指针包含、像素契约等原样通过。
- **本机取证（不入库）**：`FTS_PREVIEW_DATA_DIR`（可选 `FTS_PREVIEW_THEME`）隔离实例——Bug A：修复前后各录一段四方向拖动（置顶 2 项 + 下方若干项），修复后无闪烁；Bug B：修复前后 400% 放大截图（浅+深各一张文字卡），修复后浅色主题应见 ClearType 彩色镶边且笔画锐利。

## 红线

- 持久化原子性、置顶分区、批量顺序、拖放源数据、安全卸载边界、`PanelStateMachine` 语义不可改变；`MoveMany`/`IsSameCategoryTargetValid` 既有校验不改（钳制只在交互层）。
- Mac 端零开发（2026-09-23 暂停令）；Core 新增代码保持 Mac 可编译、Mac 测试与 CI 绿色。
- 修复先复现、根因优先：禁止用动画、延迟、防抖计时器或重新隐藏等装饰手段掩盖闪烁。
- 每项行为改动先补失败测试再实现；UI 变化必须真实截图/录屏证据（不入库）。
