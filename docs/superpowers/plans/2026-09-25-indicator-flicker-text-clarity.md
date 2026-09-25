# 实施计划：插入指示条闪烁 + 卡片文字清晰度（2026-09-25）

设计规格：[2026-09-25-indicator-flicker-text-clarity-design.md](../specs/2026-09-25-indicator-flicker-text-clarity-design.md)。交接提示词：[docs/handoff/2026-09-25-indicator-flicker-text-clarity-execution-prompt.md](../../handoff/2026-09-25-indicator-flicker-text-clarity-execution-prompt.md)。

## 轨道一：失败先行测试

- [ ] Bug A · Core 单测（`InsertionSlotResolverTests`）：单调性（cursorY 递增索引不减）、相邻槽中点翻转、首项上方/末项下方/卡片间隙、`ClampInsertionIndex` 钳制边界（P=0、全部置顶、索引越界）、空输入提前返回。
- [ ] Bug A · WPF 交互测试（`MainWindowInteractionTests.DragAndDrop.cs`，沿用 `NewDragEventArgs`+`PreviewDragOverEvent`）：
  - 闪烁复现：非置顶拖动在「最后一个置顶项上半区 ↔ 下半区」来回 ±1 DIP，断言指示条持续可见且位置稳定在钳制槽（当前代码因隐藏而失败）。
  - 跳变复现：光标在末项下方空白区/Padding，断言槽位与几何一致、不跳末尾（当前失败）。
  - 改写契约测试 `PinnedDrag_CrossRegionHidesInsertionIndicatorInBothDirections` → 跨区显示钳制槽、`Effects=Move`。
  - 置顶拖动向下越界钳制到 pinnedCount；混合批量行为不变；Drop 落点与指示条一致（既有断言适配）。
- [ ] Bug B · 样式与模板结构锁定测试（1.10.2 模式）：2b 涉及的每个样式 `TextFormattingMode=Display`；卡片模板结构断言：内容层 Border 无 `Effect`、阴影层无子内容、正文 LineHeight=20 / MaxHeight=100。
- [ ] 在当前代码上运行确认失败并保存失败输出（不入库）。

## 轨道二：实现（按已定方案，测试转绿）

- [ ] Bug A 分支 `fix/nightly-20260925-insert-indicator-stability`：Core `InsertionSlotResolver` + WPF 层接线（`GetBoardDropLocation` 重写为几何枚举 + 钳制，删除回退分支跳末尾逻辑与 `GetVisibleListEndY`；`PreviewDragOver`/`PreviewDrop` 一致化）。
- [ ] Bug B 分支 `fix/nightly-20260925-card-text-clarity`：模板重构（2a）+ 样式 Display（2b）+ 行高（2c）+ 视取证决定 2d。
- [ ] 两分支各自小步提交、每步跑相关选集；无代码交集可先后推进。

## 轨道三：证据与文档

- [ ] 真实取证（必做，不入库）：`FTS_PREVIEW_DATA_DIR` 隔离实例——Bug A 修复前后四方向拖动录屏（置顶 2 项 + 下方若干项），修复后无闪烁；Bug B 修复前后 400% 放大截图（浅+深各一张文字卡），修复后浅色应见 ClearType 彩色镶边。
- [ ] `CHANGELOG.md` 未发布区（含指示条契约变更说明、卡片高度 ±10 DIP 说明）；README「几个常用操作」若拖动语义描述受影响则同步；`docs/design.md` 层级节补记卡片双层结构与插入指示条几何/钳制契约。

## 收尾

- [ ] 质量门四连：Core Debug 预构建 → `format --verify-no-changes` → Release 全量测试 → 严格构建 0 警告；外加 `scripts/check-repo-hygiene.ps1`。
- [ ] 两 PR → CI「格式、测试与构建」+ Apple Silicon 全绿 → 合入 main → main 复跑 Release 测试。
- [ ] 统一发行 v1.11.3：`CHANGELOG` 整理为 1.11.3、`version.txt` 同步 → `build-release.ps1 -ForRelease` → 同一 GitHub Release 上传 Windows 安装包、`osx-arm64` zip（如实标注未公证测试版）、`SHA256SUMS.txt`；README 下载入口同步。
- [ ] 本机安装：优雅关闭运行实例 → 静默原地更新 → 重启 → 验证版本 1.11.3 与自启正常，向用户展示验证结果与取证。
- [ ] 当晚切片记入 `docs/research/loop-2026-09.md`（含待裁决清单），提交推送。

## 记录

（执行窗口填写）
