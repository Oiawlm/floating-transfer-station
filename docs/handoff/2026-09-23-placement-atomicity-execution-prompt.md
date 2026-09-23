# 悬浮中转站 · 展开闪现修复（窗口几何原子化）· 新窗口执行提示词（2026-09-23）

> **使用方法**：在本仓库根目录（`D:\Download\Agent Vault\悬浮中转站`，main 分支，v1.8.0 已发布）打开新的 ZCode（GLM-5.3）窗口，把本提示词全文粘贴为首条消息，并以 **Goal/执行模式** 运行。本提示词携带 2026-09-23 规划窗口的定稿结论（含逐帧取证与根因定位），无需重新规划；遇到未覆盖的决策点停下来问用户。

## 背景

你是悬浮中转站仓库（GitHub `Oiawlm/floating-transfer-station`）的维护执行代理。上一个规划窗口已用逐帧差分分析完成用户报障的复现、定位与方案定稿。

技术栈：.NET 10；Windows 端 WPF（Windows 11 为目标，默认 PerMonitorV2 DPI）+ Inno Setup；Mac 端 Avalonia（**按 2026-09-23 指令全面暂停开发**，仅保留 CI 回归）；共享 `FloatingTransferStation.Core`；MSTest；本机 `.tools/dotnet` 与 Inno 就绪。

## 规划窗口结论（不要重新发散）

**用户报障**：录屏 `D:\0初中数学与物理\家教\2026-09-23 12-17-07.mp4`（2560×1600@60fps）中，约 5–6 秒处窗口"有一帧突然闪现"。

**逐帧取证结果**（60fps 差分 + 亮度分割，坐标为物理像素，本机约 160% DPI）：
- 全片 9 次展开/收起：展开 1.38/4.00/7.90s，收起 2.97/5.10/6.82/8.77s，均为干净的单帧跳变。
- **唯一异常在 5.917–5.933s（第 356 帧，恰一帧）**：面板以展开后的完整尺寸与内容渲染于 (948,1052)–(1596,1596)——Left 已是展开位、**Top 仍是收起 tab 行的顶部**，底边伸出屏幕被裁；正确终态为 (948,300)–(1596,1296)，收起 tab 为 (1508,1052)–(1596,1296)。下一帧立即跳到终态。
- 即：闪现帧 = 展开矩形中「新尺寸 + 旧 Top」的中间态被 DWM 合成器采样了一帧。

**根因**（`src/FloatingTransferStation/Views/MainWindow.Lifecycle.cs:94-121`）：
`ApplyPlacement` 把一次 move+resize 拆成 4 次独立 WPF 属性赋值（`Width`→`Height`→`Left`→`Top`），每次赋值各自触发一次 Win32 `SetWindowPos`。现顺序是为「悬停指针在每个中间矩形内不出窗」设计的（STA 测试 `ExpandedPlacement_KeepsTheStationaryRailPointerInsideEveryGeometryUpdate` 锁定该契约），但指针包含≠不可见：水平中间态越出右缘、被 1.8.0 边缘裁切区遮住，**竖直中间态（新尺寸+旧 Top）向下伸出约 340 DIP 落在屏内**。四赋值通常在同一合成间隔内完成，仅当 UI 线程在语句间被打断（GC/调度/录屏编码争抢）时被采样——小概率竞争，9 次中 1 次。收起方向无此问题是因为 `BeginCollapsedVisualHandoff` 用壳透明交接兜底；展开方向无等价物（tab 须全程可见，不能整体透明）。

## 已定决策（不要重新发散）

- **修复方式 = 原子矩形迁移**：对已创建 HWND 的窗口，用**一次** `SetWindowPos(hwnd, x, y, cx, cy, SWP_NOZORDER|SWP_NOACTIVATE)` 直接应用终态矩形（物理像素；DIP→px 经 `HwndSource.CompositionTarget.TransformToDevice` 或 `GetDpiForWindow`，取整须保证右缘锚定不偏移），随后再对齐 WPF 四个 DP 账本。两稳定矩形嵌套（共享右缘、tab 行 ⊂ 面板跨度），单次迁移天然满足指针包含契约。
- **DP 对齐策略需实证**：用既有 observed-bounds 测试基建（`LocationChanged`/`SizeChanged` 里 `GetWindowRect` 记录）验证对齐期间每个被推矩形都等于终态（no-op）。若 WPF 用陈旧 DP 混合值重推错误矩形，回退方案：对齐期间置标志位，在既有 `WndProc` 钩子里拦截与终态不符的 `WM_WINDOWPOSCHANGING` 并改写为目标矩形。禁止 `SetWindowRgn`。
- **窗口未创建时（构造期 `ApplyPlacement`）保留纯 DP 路径**：Show 之前无闪现风险。
- **不动**：`WindowController` 三个纯函数（几何数学正确，错在应用方式）、`PanelStateMachine` 语义、收起防闪烁交接、`WindowShellClip`/DWM 材质/样式、持久化契约。`WidthThumb/HeightThumb/HeaderThumb` 拖拽的增量语义保持（评估是否顺路走 `ApplyPlacement`，若改变行为则不改并说明）。
- **禁止用动效/延迟/隐藏窗口装饰性掩盖**；不引入新依赖。
- 版本：`version.txt` → **1.8.1**（缺陷修复，无功能新增）。

## 总目标（一句话，可核对）

消除展开/收起/轨道迁移中所有可见的窗口矩形中间态（一次迁移=一次矩形变更），以失败先行的 STA 测试锁定、真实录屏逐帧复验证，全部通过既有质量门与 CI，按发布指南完成 v1.8.1 统一发行并静默安装到用户本机、验证版本与自启。

## 必读输入（动手前按序读完）

`AGENTS.md`（优先级最高）→ 本提示词 → `README.md`、`CONTRIBUTING.md`、`PROJECT_GUIDE.md`、`docs/design.md`、`docs/releasing.md`、`docs/handoff/README.md` → 源码：`src/FloatingTransferStation/Views/MainWindow.Lifecycle.cs`（重点 `ApplyPlacement`/`CurrentWorkArea`/`DockRight`）、`Views/MainWindow.xaml.cs`、`Views/MainWindow.VisualTransitions.cs`（`ExpandIntentTimer_Tick`、`BeginCollapsedVisualHandoff` 家族）、`Views/MainWindow.xaml`（`WindowShell` 结构）、`Services/{NativeMethods,WindowController,ScreenEdgeGeometry,DwmWindowEffects}.cs` → 测试：`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.VisualLifecycle.cs`（`ExpandedPlacement_KeepsTheStationaryRailPointerInsideEveryGeometryUpdate`、`Collapse_HidesExpandedSurfaceBeforeMovingToTheCollapsedRow` 及 bounds 记录基建）、`WindowControllerTests.cs`、`MainWindowInteractionTests.EdgeBleed.cs` → `docs/superpowers/specs+plans/2026-09-23-edge-bleed-and-settings-*`（上轮上下文）。

## 轨道一：失败先行测试（S0 之后第一步）

1. 在 `MainWindowInteractionTests.VisualLifecycle.cs` 新增 STA 测试（复用既有 `GetWindowRect`/bounds 记录手法）：展开、收起、拖放轨道（`CategoryRail` 放置）三类迁移中，观察到的**每一个**窗口矩形必须等于初始或终态矩形之一（容差 ≤1px），断言消息列出全部观察值。先运行确认**在当前代码上失败**（现实现产生 4 个不同矩形）并保存失败输出。
2. 既有测试全部原样保持必须通过：指针包含、收起防闪烁、EdgeBleed 几何、拖放、像素契约（`WindowShell_ClipsAllCorners…` 等）。

## 轨道二：原子几何实现

1. `NativeMethods.cs`：新增 `SetWindowPos` P/Invoke 与 `SWP_NOZORDER`/`SWP_NOACTIVATE` 常量（沿用现有 internal static 风格与注释密度）。
2. `MainWindow.Lifecycle.cs` `ApplyPlacement`：保留开头 `CancelPanelCollapseExit`/`CancelCollapsedVisualHandoff`；HWND 就绪时走原子路径（一次 `SetWindowPos` 到终态，再按上述已定决策对齐 DP 并用轨道一测试实证 no-op，必要时启用 WndProc 拦截回退）；HWND 未就绪时保留现纯 DP 顺序路径。
3. DPI/取整：以 DIP 数学为权威、仅在调用边界换算一次；验证右缘锚定（含 8px 裁切）在换算后不产生 1px 漂移（可在 EdgeBleed 测试中断言物理右缘）。
4. 自查收起路径仍只依赖既有交接（不要求改动，但不得回归）；`WindowShell_SizeChanged` 圆角裁剪在新路径下仍恰好触发。
5. 运行轨道一测试转绿 + VisualLifecycle/EdgeBleed/DragAndDrop 全选集。

## 轨道三：证据与文档（与代码同步提交）

- **S0（执行第一步）**：按仓库惯例落盘 spec+plan（`docs/superpowers/specs/2026-09-23-placement-atomicity-design.md`、`docs/superpowers/plans/2026-09-23-placement-atomicity.md`），登记 `docs/README.md` 索引；本提示词原文落盘 `docs/handoff/2026-09-23-placement-atomicity-execution-prompt.md` 一并提交。
- **真实取证（必做，不入库）**：computer-use 启动本机应用（`FTS_PREVIEW_DATA_DIR` 隔离），60fps 录屏 ≥30 次展开/收起切换（含快速连切与指针滞留变体），用逐帧差分复验（ffmpeg 抽帧 + 相邻帧均值差 + 亮度分割定位窗口矩形，方法与规划窗口一致：异常帧应表现为「中间矩形」而非终态）；修复前先录一段含中间态的基线、修复后同法复验为零。截图/录屏向用户展示，不入库。
- `docs/design.md`：防闪烁/几何节补记「迁移原子性」契约（一次迁移=一次矩形变更 + 观察矩形 ∈ {初始,终态} 测试锁定）；`CHANGELOG.md` 归入 1.8.1；`ROADMAP.md` 如有对应条目同步；README 无用户可见行为变化则不动下载说明之外的内容。

## 收尾（必做，不可选）

1. 版本：`version.txt` → 1.8.1，CHANGELOG/发布说明同步。
2. 质量门：Core Debug 预构建 → `dotnet format --verify-no-changes` → Release 全量测试 → 严格 Release 构建 0 警告（命令与顺序见 PROJECT_GUIDE.md/CONTRIBUTING.md）。
3. 任务分支 → PR → CI「格式、测试与构建」全绿（含 Apple Silicon 验证：Mac 零开发，本次为 Windows 端修复，Core 预计零改动；若 CI 因本次改动失败仅做恢复绿色的最小修复）→ 合入 main。
4. 统一发行 v1.8.1：`build-release.ps1 -ForRelease` → 同一 GitHub Release 上传 Windows 安装包、`osx-arm64` zip（既有 Mac 代码重建打包，如实标注未公证测试版）、`SHA256SUMS.txt`；记录运行链接与 SHA256；README 下载入口同步。
5. 本机安装：优雅关闭运行中的实例 → 静默原地更新 → 重启 → 验证版本号为 1.8.1、自启项正常，并向用户展示验证结果与轨道三取证。

## 硬约束（红线，全文以 AGENTS.md/CONTRIBUTING.md 为准）

- 持久化原子性、置顶分区、批量顺序、拖放源数据、安全卸载边界、`PanelStateMachine` 语义不可改变；改契约先开 Issue。
- Mac 端零开发（暂停令）；Core 改动保持 Mac 可编译、CI 绿色。
- 每项行为改动先补失败测试再实现；UI 变化必须 computer-use 真实截图/录屏证据（不入库）。
- 不提交 `.tools/`、`artifacts/`、`TestResults/`、用户内容、凭据、本机截图；不动 `.zcode/` 与用户本地文件（含报障录屏原件，取证产物只放临时目录）。
- 常设授权：任务分支、普通推送、PR 与处理本次 CI 失败；不含强推、凭据配置、仓库/全局设置变更。
- 修复先复现、根因优先：禁止用动画、延迟、隐藏窗口或重新计时等装饰手段掩盖闪现。

## 执行方式

Goal/执行模式直接开工：S0 spec+plan → 轨道一（失败测试）→ 轨道二（实现转绿）→ 轨道三（取证与文档）→ 收尾；小步提交（每阶段至少一次），每阶段跑相关测试选集，最后全量质量门。任何决策点拿不准或影响契约/公开行为的停下来问用户。

## 验收标准（Done when）

- 轨道一新增测试从失败转绿，且既有指针包含、收起防闪烁、EdgeBleed、拖放、像素契约测试原样通过。
- 修复后 60fps 录屏 ≥30 次切换经逐帧差分复验：不存在任何「非初始非终态」的窗口矩形帧；取证已向用户展示。
- 质量门与 CI 全绿；v1.8.1 统一 Release 已发布（Windows + Apple Silicon 包 + SHA256SUMS）；本机已静默更新并验证版本与自启。
- 文档（design.md/CHANGELOG/docs 索引/spec+plan/handoff）同步落盘并提交。
