# 实施计划：展开闪现修复（窗口几何原子化）（2026-09-23）

设计规格：[2026-09-23-placement-atomicity-design.md](../specs/2026-09-23-placement-atomicity-design.md)。交接提示词：[docs/handoff/2026-09-23-placement-atomicity-execution-prompt.md](../../handoff/2026-09-23-placement-atomicity-execution-prompt.md)。

## 轨道一：失败先行测试

- [ ] 在 `MainWindowInteractionTests.VisualLifecycle.cs` 新增三个 STA 测试（复用 `GetWindowRect` + `LocationChanged`/`SizeChanged` 观察手法）：
  - 展开迁移（`ExpandCategory`）：观察矩形 ∈ {初始, 终态}，四边各 ≤1px。
  - 收起迁移（出场动画完成、交接应用几何）：同上。
  - 外部拖入轨道揭示（`CategoryRail` 放置，DragEnter 收起态 tab）：同上。
- [ ] 在当前代码上运行确认失败（现实现产生中间矩形），保存失败输出（不入库）。
- [ ] 既有指针包含、收起防闪烁、EdgeBleed、拖放、像素契约测试原样通过。

## 轨道二：原子几何实现

- [ ] `NativeMethods.cs`：新增 `SetWindowPos` P/Invoke 与 `SWP_NOZORDER`/`SWP_NOACTIVATE`/`SWP_NOMOVE`/`SWP_NOSIZE` 常量、`WM_WINDOWPOSCHANGING` 与 `WINDOWPOS` 结构。
- [ ] `MainWindow.Lifecycle.cs`：`ApplyPlacement` HWND 就绪时走原子路径（一次 `SetWindowPos` 到终态 + 锚定一致取整 + 守卫下对齐四个 DP）；HWND 未就绪保留纯 DP 顺序路径（原逻辑改名留存）。
- [ ] `MainWindow.xaml.cs` `WndProc`：对齐守卫拦截 `WM_WINDOWPOSCHANGING` 并改写为目标矩形。
- [ ] 自查：收起路径仍只依赖既有交接；`WindowShell_SizeChanged` 圆角裁剪恰好触发；DPI 取整不产生右缘 1px 漂移。
- [ ] 轨道一测试转绿；VisualLifecycle / EdgeBleed / DragAndDrop 全选集通过。

## 轨道三：证据与文档

- [ ] 本机取证：修复前先录含中间态的基线；修复后 60fps 录屏 ≥30 次切换（含快速连切与指针滞留变体），逐帧差分复验为零（截图/录屏向用户展示，不入库）。
- [ ] `docs/design.md`：防闪烁/几何节补记「迁移原子性」契约。
- [ ] `CHANGELOG.md` 归入 1.8.1；`ROADMAP.md` 如有对应条目同步；`docs/README.md` 登记本设计与计划。

## 收尾

- [ ] `version.txt` → 1.8.1。
- [ ] 质量门：Core Debug 预构建 → `dotnet format --verify-no-changes` → Release 全量测试 → 严格 Release 构建 0 警告。
- [ ] 任务分支 → PR → CI「格式、测试与构建」全绿（含 Apple Silicon 回归；Mac 零开发，如因本次改动失败仅做恢复绿色的最小修复）→ 合入 main。
- [ ] 统一发行 v1.8.1：`build-release.ps1 -ForRelease` → 同一 GitHub Release 上传 Windows 安装包、`osx-arm64` zip（如实标注未公证测试版）、`SHA256SUMS.txt`；README 下载入口同步。
- [ ] 本机安装：优雅关闭运行实例 → 静默原地更新 → 重启 → 验证版本 1.8.1 与自启，向用户展示验证结果与轨道三取证。

## 记录

-（执行中按阶段补记）
