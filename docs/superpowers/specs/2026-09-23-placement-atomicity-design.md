# 设计规格：展开闪现修复（窗口几何原子化）（2026-09-23）

本规格承接 2026-09-23 规划窗口的定稿结论（逐帧取证与根因定位见 `docs/handoff/2026-09-23-placement-atomicity-execution-prompt.md`），范围仅限其中已定决策；未覆盖的新决策仍需先回到用户。

## 问题

**用户报障**：录屏（2560×1600@60fps，本机约 160% DPI）中约 5–6 秒处，窗口展开时「有一帧突然闪现」。逐帧差分复验：全片 9 次展开/收起里唯一异常在第 356 帧（恰一帧），面板以展开后的完整尺寸与内容渲染于「新 Left + 旧 Top」的中间矩形（底边伸出屏幕被裁），下一帧立即跳到正确终态。即一次 move+resize 被拆成多次 Win32 `SetWindowPos`，其中一个可见中间态被 DWM 合成器采样了一帧（小概率竞争，9 次中 1 次）。

**根因**：`MainWindow.Lifecycle.cs` 的 `ApplyPlacement` 把一次迁移拆成 4 次独立 WPF 属性赋值（`Width`→`Height`→`Left`→`Top`，按「指针在每个中间矩形内不出窗」排序），每次赋值各自触发一次 WPF `UpdateWindowSettings` → `SetWindowPos`。水平中间态越出右缘被 1.8.0 边缘裁切区遮住，但**竖直中间态（新尺寸+旧 Top）向下伸出约 340 DIP 落在屏内**，故展开方向存在可见闪现窗口；收起方向因 `BeginCollapsedVisualHandoff` 壳透明交接而未见异常。

## 已定决策（不再发散）

- **修复方式 = 原子矩形迁移**：对已创建 HWND 的窗口，用**一次** `SetWindowPos(hwnd, x, y, cx, cy, SWP_NOZORDER|SWP_NOACTIVATE)` 直接应用终态矩形（物理像素），随后对齐 WPF 四个 DP 账本。两稳定矩形嵌套（共享右缘、tab 行 ⊂ 面板跨度），单次迁移天然满足指针包含契约。
- **DP 对齐需实证 no-op**：用 observed-bounds 测试基建验证对齐期间每个被推矩形都等于终态；若 WPF 用陈旧 DP 混合值重推错误矩形，则在既有 `WndProc` 钩子里拦截与终态不符的 `WM_WINDOWPOSCHANGING` 并改写为目标矩形（本规格将其实现为常设守卫：对齐期间一律钳制，矩形相等时天然 no-op）。
- **窗口未创建时（构造期 `ApplyPlacement`）保留纯 DP 路径**：Show 之前无闪现风险。
- **不动**：`WindowController` 三个纯函数、`PanelStateMachine` 语义、收起防闪烁交接、`WindowShellClip`/DWM 材质/样式、持久化契约；`WidthThumb/HeightThumb/HeaderThumb` 拖拽保持直接增量赋值（拖拽是连续指针驱动的小步迁移，且 `DockRight` 依赖 `ActualWidth` 的既有时序，改走 `ApplyPlacement` 会改变行为，故不改）。
- **禁止** `SetWindowRgn`；禁止用动效/延迟/隐藏窗口装饰性掩盖；不引入新依赖。

## 结构

- **`NativeMethods`**：新增 `SetWindowPos` P/Invoke、`SWP_NOZORDER`/`SWP_NOACTIVATE`/`SWP_NOMOVE`/`SWP_NOSIZE` 常量、`WM_WINDOWPOSCHANGING`（0x0046）与 `WINDOWPOS` 结构（沿用现有 internal static 风格）。
- **`MainWindow.Lifecycle.cs` `ApplyPlacement`**：保留开头 `CancelPanelCollapseExit`/`CancelCollapsedVisualHandoff`；`_windowSource` 就绪（HWND 已创建、`CompositionTarget` 可用）时走原子路径，否则保留现纯 DP 顺序路径（原逻辑整体改名留存）。
- **原子路径**：
  1. DIP→物理换算一次、锚定一致取整：`left = round(L·sx)`、`right = round((L+W)·sx)`、`width = right-left`（Y 同理），保证右缘（含 8px 裁切）换算后不漂移；换算源 `HwndSource.CompositionTarget.TransformToDevice`。
  2. 置 `_placementGuard`（目标物理矩形）→ 一次 `SetWindowPos` 到终态 → 顺序写 `Width`/`Height`/`Left`/`Top` 四个 DP → 清守卫（try/finally）。守卫在 `WndProc` 中把对齐期间任何 `WM_WINDOWPOSCHANGING` 的矩形改写为终态（清除 `SWP_NOMOVE|SWP_NOSIZE`），使陈旧 DP 混合推矩形天然 no-op。
- **`WindowShell_SizeChanged`**：单次 `SetWindowPos` 只产生一次 `WM_SIZE`，圆角裁剪恰好触发一次，无需改动（由既有像素契约测试复验）。

## 验证

- **失败先行 STA（新增）**：展开、收起（出场动画完成后经交接）、外部拖入轨道揭示（`CategoryRail` 放置）三类迁移中，经 `LocationChanged`/`SizeChanged` 观察到的**每一个** `GetWindowRect` 矩形必须等于初始或终态矩形（四边各 ≤1px），断言消息列出全部观察值；当前实现产生 4 个不同矩形，须先失败。
- **既有测试原样通过**：指针包含（`ExpandedPlacement_KeepsTheStationaryRailPointerInsideEveryGeometryUpdate`）、收起防闪烁（`Collapse_HidesExpandedSurfaceBeforeMovingToTheCollapsedRow`）、EdgeBleed 几何、拖放、像素契约（`WindowShell_ClipsAllCorners…` 等）。
- **本机取证（不入库）**：`FTS_PREVIEW_DATA_DIR` 隔离启动，60fps 录屏 ≥30 次展开/收起（含快速连切与指针滞留变体），ffmpeg 抽帧 + 相邻帧差分 + 亮度分割复验：修复前基线可含中间矩形帧，修复后不存在任何「非初始非终态」窗口矩形帧。

## 红线

- 持久化原子性、置顶分区、批量顺序、拖放源数据、安全卸载边界、`PanelStateMachine` 语义不可改变。
- Mac 端零开发（2026-09-23 暂停令）；本次 Core 预计零改动，保持 Mac 可编译、CI 绿色。
- 修复先复现、根因优先；每项行为改动先补失败测试再实现。
