# 实施计划：展开闪现修复（窗口几何原子化）（2026-09-23）

设计规格：[2026-09-23-placement-atomicity-design.md](../specs/2026-09-23-placement-atomicity-design.md)。交接提示词：[docs/handoff/2026-09-23-placement-atomicity-execution-prompt.md](../../handoff/2026-09-23-placement-atomicity-execution-prompt.md)。

## 轨道一：失败先行测试

- [x] 在 `MainWindowInteractionTests.VisualLifecycle.cs` 新增三个 STA 测试（复用 `GetWindowRect` + `LocationChanged`/`SizeChanged` 观察手法，另挂测试侧 `WM_WINDOWPOSCHANGED` 钩子覆盖纯尺寸中间态）：
  - 展开迁移（`ExpandCategory`）：观察矩形 ∈ {初始, 终态}，四边各 ≤1px。
  - 收起迁移（出场动画完成、交接应用几何）：同上。
  - 外部拖入轨道揭示（`CategoryRail` 放置，DragEnter 收起态 tab）：同上。
- [x] 在当前代码上运行确认失败（现实现产生中间矩形），保存失败输出（不入库）。
- [x] 既有指针包含、收起防闪烁、EdgeBleed、拖放、像素契约测试原样通过。

## 轨道二：原子几何实现

- [x] `NativeMethods.cs`：新增 `SetWindowPos` P/Invoke 与 `SWP_NOZORDER`/`SWP_NOACTIVATE`/`SWP_NOMOVE`/`SWP_NOSIZE` 常量、`WM_WINDOWPOSCHANGING` 与 `WINDOWPOS` 结构。
- [x] `MainWindow.Lifecycle.cs`：`ApplyPlacement` HWND 就绪时走原子路径（一次 `SetWindowPos` 到终态 + 锚定一致取整 + 守卫下对齐四个 DP）；HWND 未就绪保留纯 DP 顺序路径（原逻辑改名留存）。
- [x] `MainWindow.xaml.cs` `WndProc`：对齐守卫拦截 `WM_WINDOWPOSCHANGING` 并改写为目标矩形。
- [x] 自查：收起路径仍只依赖既有交接；`WindowShell_SizeChanged` 圆角裁剪恰好触发；DPI 取整不产生右缘 1px 漂移（原子性测试同时断言物理边缘与 DIP 账本换算一致）。
- [x] 轨道一测试转绿；VisualLifecycle / EdgeBleed / DragAndDrop 全选集通过（Windows 测试工程 568/568）。

## 轨道三：证据与文档

- [x] 本机取证：修复前基线（已安装 1.8.0，对应发布提交 53196c5）40 次切换 + 8×CPU 负载 60fps 录屏，逐帧差分检出 24 个中间矩形帧（含 1 帧与用户报障完全一致的「全宽面板+旧 Top+底边出屏」）；修复版同条件复验 0 违例帧（截图/录屏已向用户展示，不入库，存于本机临时目录）。
- [x] `docs/design.md`：防闪烁/几何节补记「迁移原子性」契约。
- [x] `CHANGELOG.md` 归入待发布段（发布时归 1.8.1）；`ROADMAP.md` 无对应条目（无闪烁/几何条目，无需同步）；`docs/README.md` 登记本设计与计划。
- 备注：取证期间短暂优雅关闭过用户运行实例（两实例同贴右缘会互相污染取证），完成后已原样重启。

## 收尾

- [ ] `version.txt` → 1.8.1。
- [ ] 质量门：Core Debug 预构建 → `dotnet format --verify-no-changes` → Release 全量测试 → 严格 Release 构建 0 警告。
- [ ] 任务分支 → PR → CI「格式、测试与构建」全绿（含 Apple Silicon 回归；Mac 零开发，如因本次改动失败仅做恢复绿色的最小修复）→ 合入 main。
- [ ] 统一发行 v1.8.1：`build-release.ps1 -ForRelease` → 同一 GitHub Release 上传 Windows 安装包、`osx-arm64` zip（如实标注未公证测试版）、`SHA256SUMS.txt`；README 下载入口同步。
- [ ] 本机安装：优雅关闭运行实例 → 静默原地更新 → 重启 → 验证版本 1.8.1 与自启，向用户展示验证结果与轨道三取证。

## 记录

- 2026-09-23 执行窗口：
  - 轨道一红灯输出存 `TestResults/placement-atomicity/track1-pre-fix-failures.txt`（不入库）：三个迁移各观察到 2–6 个中间矩形；轨道揭示迁移的纯尺寸中间态只有测试侧 `WM_WINDOWPOSCHANGED` 钩子能观察到，`LocationChanged` 不可见。
  - 轨道二实现要点：守卫常设（矩形相等时幂等 no-op），DP 对齐期间任何 `WM_WINDOWPOSCHANGING` 一律改写为终态并清除 `SWP_NOMOVE|SWP_NOSIZE`；一次迁移只产生一次 `WM_SIZE`，圆角裁剪恰好触发一次。
  - 收尾：PR #42 CI 三项全绿（Windows、Apple Silicon、汇总「格式、测试与构建」）合入 main（`68c47a5`）；`build-release.ps1 -ForRelease` 本地通过；v1.8.1 统一 Release 资产取自 main 提交对应成功运行（run 35822815284）：`FloatingTransferStation-Setup-1.8.1.exe`（SHA256 `f9795eeb…ec6e`）、`FloatingTransferStation-1.8.1-osx-arm64.zip`（`a66d7f0f…5273`，未公证测试版）、验证元数据与 `SHA256SUMS.txt`；本机已优雅关闭旧实例、静默原地更新并重启，验证 `D:\App\悬浮中转站\悬浮中转站.exe` 版本 1.8.1+68c47a5、HKCU Run 自启值与右缘贴边窗口均正常。
  - 取证方法：暗色壳 + `FTS_PREVIEW_DATA_DIR` 隔离启动，gdigrab 抓右缘 900×1600 条带，CFR 60fps；检测器取「从条带右缘向左的连续暗后缀 ≥30px 的行」为窗口矩形，双峰模式为稳定态，任一边相差 >25px 判违例（AA/Mica 微光与外来暗块抖动不算）。基线 24 违例帧 / 复验 0 违例帧。
