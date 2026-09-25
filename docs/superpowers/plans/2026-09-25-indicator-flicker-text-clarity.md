# 实施计划：插入指示条闪烁 + 卡片文字清晰度（2026-09-25）

设计规格：[2026-09-25-indicator-flicker-text-clarity-design.md](../specs/2026-09-25-indicator-flicker-text-clarity-design.md)。交接提示词：[docs/handoff/2026-09-25-indicator-flicker-text-clarity-execution-prompt.md](../../handoff/2026-09-25-indicator-flicker-text-clarity-execution-prompt.md)。

## 轨道一：失败先行测试

- [x] Bug A · Core 单测（`InsertionSlotResolverTests`）：单调性（cursorY 递增索引不减）、相邻槽中点翻转、首项上方/末项下方/卡片间隙、`ClampInsertionIndex` 钳制边界（P=0、全部置顶、索引越界）、空输入提前返回。
- [x] Bug A · WPF 交互测试（`MainWindowInteractionTests.DragAndDrop.cs`，沿用 `NewDragEventArgs`+`PreviewDragOverEvent`）：
  - 闪烁复现：非置顶拖动在「最后一个置顶项上半区 ↔ 下半区」来回 ±1 DIP，断言指示条持续可见且位置稳定在钳制槽（当前代码因隐藏而失败）。
  - 跳变复现：光标在末项下方空白区/Padding，断言槽位与几何一致、不跳末尾（当前失败）。
  - 改写契约测试 `PinnedDrag_CrossRegionHidesInsertionIndicatorInBothDirections` → 跨区显示钳制槽、`Effects=Move`。
  - 置顶拖动向下越界钳制到 pinnedCount；混合批量行为不变；Drop 落点与指示条一致（既有断言适配）。
- [x] Bug B · 样式与模板结构锁定测试（1.10.2 模式）：2b 涉及的每个样式 `TextFormattingMode=Display`；卡片模板结构断言：内容层 Border 无 `Effect`、阴影层无子内容、正文 LineHeight=20 / MaxHeight=100。
- [x] 在当前代码上运行确认失败并保存失败输出（不入库）。

## 轨道二：实现（按已定方案，测试转绿）

- [x] Bug A 分支 `fix/nightly-20260925-insert-indicator-stability`：Core `InsertionSlotResolver` + WPF 层接线（`GetBoardDropLocation` 重写为几何枚举 + 钳制，删除回退分支跳末尾逻辑与 `GetVisibleListEndY`；`PreviewDragOver`/`PreviewDrop` 一致化）。
- [x] Bug B 分支 `fix/nightly-20260925-card-text-clarity`：模板重构（2a）+ 样式 Display（2b）+ 行高（2c）+ 视取证决定 2d。
- [x] 两分支各自小步提交、每步跑相关选集；无代码交集可先后推进。

## 轨道三：证据与文档

- [x] 真实取证（必做，不入库）：Bug B 修复前后 400% 放大截图（浅+深各一张文字卡）完成并量化对比（详见记录）；Bug A 真实拖动录屏多次尝试被本机活跃用户的真实鼠标输入覆盖（注入事件与其竞速失败，CUA 特权注入通道本会话已停用不可恢复），以 STA 闪烁/跳变复现测试为机械证据、如实记录待机器空闲时补录。
- [x] `CHANGELOG.md` 未发布区（含指示条契约变更说明、卡片高度 ±10 DIP 说明）；README「几个常用操作」拖动语义粗粒度描述不受影响、无需同步；`docs/design.md` 层级节补记卡片三层结构与文本像素对齐契约。

## 收尾

- [x] 质量门四连：Core Debug 预构建 → `format --verify-no-changes` → Release 全量测试（655+36+168 通过）→ 严格构建 0 警告；外加 `scripts/check-repo-hygiene.ps1` 通过。
- [x] 两 PR → CI「格式、测试与构建」+ Apple Silicon 全绿 → 合入 main → main 复跑 Release 测试。
- [x] 统一发行 v1.11.3：`CHANGELOG` 整理为 1.11.3、`version.txt` 同步 → `build-release.ps1 -ForRelease` 本地通过 → 同一 GitHub Release 上传 Windows 安装包、`osx-arm64` zip（如实标注未公证测试版）、`SHA256SUMS.txt`；README 下载入口同步。
- [x] 本机安装：优雅关闭运行实例 → 静默原地更新（退出码 0）→ 重启 → 验证版本 1.11.3.0 与自启正常。
- [x] 当晚切片记入 `docs/research/loop-2026-09.md`（含待裁决清单），提交推送。

## 记录

- 2026-09-25 执行窗口：
  - 失败先行输出存本机 `TestResults/indicator-stability/track1-wpf-pre-fix-failures.txt` 与 `TestResults/card-text-clarity/track1-pre-fix-failures.txt`（不入库）：Bug A 四个新 STA 测试全部失败于 None/隐藏；Bug B 三个新 STA 测试全部失败于 Effect 祖先/Display/行高断言。
  - Bug A 实现要点：`InsertionSlotResolver`（Core，纯函数）按条目竖直中线翻转解析槽位（中线归下半区，与 `ForTarget` 既有语义一致）；钳制后经 `CanMoveMany` 复核，混合置顶批量天然落入隐藏分支；测试共享进 Core.Tests 供 Apple Silicon CI 覆盖。
  - Bug B 实现要点：`CardRootStyle`（Grid 根：外距/分区上距/Lift）+ `CardShadowLayerStyle`（不透明底 + 共享冻结阴影，无内容不命中）+ `CardContainerStyle`（内容面，无 Effect）；2d（`UseLayoutRounding`）经 400% 取证判断无需启用（对比度已完全恢复）。
  - 既有测试适配：`PinnedBoundary_AddsGapOnlyBeforeFirstNormalCard`（外距断言移到根容器）、`Cards_CarrySharedFrozenElevationShadow`（阴影断言移到阴影层 + 内容面无 Effect）、`TextCard_ReservesFixedPinAndSelectionColumns`（行高 18→20、MaxHeight 90→100 契约更新）。
  - 真实取证（浅+深 400% 量化）：浅色主题下修复前文字墨迹 25 分位亮度 130、仅 2.1% 像素达到深墨（<80）——Effect 中间位图把笔画洗成中灰；修复后 P25=29（全对比度）、57.5% 深墨，笔画边缘干净。深色主题深墨占比 72.0%→88.8%。ClearType 彩色镶边未出现：整窗 Mica alpha 表面令全部文本维持灰度抗锯齿（规划「次因」，2a 恢复的是对比度与像素对齐；彻底恢复 RGB ClearType 需不透明窗口表面，属另一权衡，已记观察）。
  - Bug A 真实录屏受阻（如实）：本机用户正在使用电脑，注入鼠标位置与其真实输入持续竞速失败（拖拽启动即被抢占）；CUA 特权注入通道本会话已停用且不可重启。保留 bugA-before.mp4（85s@30fps）与 bugA-after.mp4（20s@60fps）尝试录像及 STA 闪烁复现测试（±1 DIP 抖动持续可见且位置稳定）作为证据；四方向拖动录屏待机器空闲时补录（已列入 loop 日志待裁决清单）。
  - CI 与发行：PR #57/#58/#59 全绿合入（#59 与 #58 的 CHANGELOG 冲突已解决）；PR #60 首轮因发布材料契约（CHANGELOG 必须保留空「未发布」小节）失败一次，补齐后全绿合入；main 门运行 36102357141 全绿，产物取自该运行；`build-release.ps1 -ForRelease` 本地通过。v1.11.3 统一发行：`FloatingTransferStation-Setup-1.11.3.exe`（SHA256 `5e3f9a21…36ac`）、`FloatingTransferStation-1.11.3-osx-arm64.zip`（`16a4859f…0d8`，ad-hoc 签名未公证、原生冒烟通过）、`SHA256SUMS.txt` 与 Mac 验证元数据 JSON。
  - 本机安装：优雅关闭用户实例（取证期间两实例贴右缘会互相污染，1.8.1 惯例）→ 静默原地更新 1.11.2→1.11.3+a04bfe9（退出码 0）→ 重启验证 PID/版本 1.11.3.0 与 HKCU Run 自启值正常。
