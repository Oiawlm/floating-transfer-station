# 设计规格：右缘边缘裁切与设置首版（2026-09-23）

本规格承接 2026-09-23 规划窗口的定稿结论（交接提示词见 `docs/handoff/2026-09-23-settings-and-right-edge-execution-prompt.md`），范围仅限其中已定决策；未覆盖的新决策仍需先回到用户。

## 问题

1. **右缘圆角别扭**：窗口恒贴屏幕右缘（`WindowController` 三种放置的 Left 均 = `workArea.Right - width`），1.7.0 起 DWM 四角统一 8px 圆角后，贴边侧（右侧）上下两个圆角悬在屏幕边缘，观感与「贴边悬浮」语言冲突。
2. **无设置入口**：主题（跟随系统/浅色/深色）、动效开关、开机自启三项用户偏好没有任何 UI；数据目录、版本与退出入口也无处集中查看。

## 方案一：右缘边缘裁切（Windows）

### 已定决策（不再发散）

- **边缘裁切（edge bleed）**：窗口右缘越出屏幕 8px，DWM 圆角整体落在屏外——可见右缘平直、左缘保留圆角。
- 依据：Win11 官方几何规范「与其他直边相交的直边不圆角（0px）」「窗口贴靠/最大化时不保留圆角」（learn.microsoft.com/windows/apps/design/signature-experiences/geometry）；`DWMWA_WINDOW_CORNER_PREFERENCE` 四角一体、无逐角选项。
- 已评估否决：四角全直（DONOTROUND）、浮离右缘留缝、维持现状；`SetWindowRgn` 破坏 DWM 合成面与 Mica 且边缘锯齿，**禁止使用**。

### 结构

- **Core**：`WindowSettings` 增加 `EdgeBleed` 常量 = `Design.DesignTokens.DwmCornerRadius`（8）。结构性尺寸权威保持在 `WindowSettings`（docs/design.md 既定分工）。纯新增，不影响 Mac。
- **`WindowController`**：`Collapsed/Expanded/CategoryRail` 各增加 `double edgeBleed = 0` 参数（默认 0 保持现契约）。行为：窗口宽度加裁切量；`Left = workArea.Right - 可见宽度`（窗口右缘越出工作区右缘一个裁切量）；可见宽度按「可见宽度 ≤ 工作区宽度」钳制（在 Normalize 之后由控制器收口，防极窄屏左缘出屏）。保持纯函数。
- **`ScreenEdgeGeometry`（新 Windows 服务，public）**：P/Invoke `MonitorFromPoint`/`GetMonitorInfo`（`EnumDisplayMonitors` 判邻接）。仅当所在显示器「工作区右缘 == 显示器右缘」且右侧无紧邻显示器时返回可裁切量；右贴任务栏（工作区右缘 < 显示器右缘）或邻接显示器时回退 0（现状贴齐）。纯判定方法与原生探测分离，判定可用伪造矩形单测，不依赖真机多屏。
- **`MainWindow`**：新增可选构造参数（右缘裁切提供器，默认 null = 0，既有 STA 测试与假实现不受影响；App 生产入口传入 `ScreenEdgeGeometry`）。`DockRight()`、`WidthThumb_DragDelta`（`Width = PanelWidth + TabWidth + 裁切量`）、构造收起放置、展开/收起/批量移动/重置共 6 处 `WindowController` 调用全部传入当前裁切量。`WndProc` 新增 `WM_DISPLAYCHANGE`：重估裁切量并按当前面板状态重新贴齐。
- **内容层不动**：`WindowShellClip`/`DwmWindowEffects`/样式全不改（右描边 0、头部右上直角本就契合平直右缘）。Mica、深浅主题、拖动缩放、收起防闪烁、命中测试语义全部保持。

### 验证

- 纯函数：裁切 8 时「可见右缘 `Left+Width-8 == workArea.Right`、窗口右缘 == `workArea.Right+8`」；裁切 0 保持旧断言；三种放置 × 两档。
- 回退判定：伪造显示器/工作区矩形（任务栏贴右、邻接显示器、正常单屏）。
- STA：展开/收起几何含裁切量、轨道 58px 完整可见；既有 `ExpandedPlacement_KeepsTheStationaryRailPointerInsideEveryGeometryUpdate`、`Collapse_HidesExpandedSurfaceBeforeMovingToTheCollapsedRow`、像素契约 `WindowShell_ClipsAllCornersToTheDwmRadiusWithShellTint`/`CollapsedHandle_ClipsRailBackgroundToDwmRoundedCorners`/`WindowShell_UsesGlassChromeWithoutLayeredTransparency` 原样通过（内容层契约不变正是本方案的验证要点）。
- 本机：`FTS_PREVIEW_DATA_DIR` 隔离 + `FTS_PREVIEW_THEME` 两主题，贴边状态 浅/深 × 展开/收起 真实截图 + 展开收起录屏（证据不入库）。

## 方案二：设置首版（Core 先行，UI 随后）

### 已定决策（不再发散）

- 首版范围 = 主题（跟随系统/浅色/深色）+ 动效开关 + 开机自启开关；数据目录只读展示 + 打开、关于（版本/仓库链接）、退出按钮默认包含；**剪贴板采集开关不纳入本版**（留待 Issue 收集）。
- 入口形态 = 头部操作区齿轮按钮 + 独立设置小窗（复用 1.7.0 Mica 壳技术）。

### 结构

- **Core 偏好模型**：`AppPreferences` record（`ThemeMode` 枚举 FollowSystem/Light/Dark + `bool AnimationsEnabled = true`），持久化为数据目录 `preferences.json`：复用 `IAtomicTextWriter` 原子写与既有备份回退（参照 settings.json 的做法）。`LocalStore` 增读写 API——通过**独立接口 `IPreferencesStore`**（不改动 `IBoardStore`，既有假实现零影响）；`AppPaths` 增 `PreferencesFile`。旧安装无此文件 → 全默认；**不改 `WindowSettings` 契约与 settings.json 形状**。
- **主题接线**：MainWindow 启动与 `WM_SETTINGCHANGE` 按 `ThemeMode` 解析（FollowSystem → `DetectSystemTheme`；强制 → 固定值且不再响应系统变化）；`DesignThemeManager.PreviewOverride` 仍最高优先（`FTS_PREVIEW_THEME` 取证机制不破坏）；切换同步 `DWMWA_USE_IMMERSIVE_DARK_MODE`。
- **动效接线**：`AnimationsEnabled=false` → `window.Resources[SystemParameters.ClientAreaAnimationKey]=false`（本地覆盖系统键）；`=true` → 移除本地覆盖恢复跟随系统。完全复用现有减弱动效降级路径，不改任何动画实现。
- **开机自启**：Core 定义 `IStartupManager`（查询/启用/禁用），Windows 实现读写 HKCU `…\CurrentVersion\Run`（值名 = 产品名「悬浮中转站」，数据 = 带引号当前进程路径，与 `installer/悬浮中转站.iss` 完全相同格式）；安装器 .iss 与 `LifecycleTests.Installer` 契约不动；仅用户点击开关时写注册表（开发启动保持只读）。已知代价：原地更新会重置自启为开（安装器总是重写该值）——写入 CHANGELOG 与 README。开关显示状态读注册表（不持久化到 preferences.json）。
- **UI**：
  - `MainWindow.xaml` 头部操作区最左加齿轮按钮（`HeaderActionButtonStyle` 同构、Fluent 齿轮 Path、ToolTip/AutomationProperties「设置」，与红色清空按钮保持距离）。
  - 新建 `SettingsWindow`（仓库首个二级窗口）：WindowChrome `GlassFrameThickness=-1` + `DwmWindowEffects` Mica + 壳样式与深浅主题字典复用，Topmost 与主窗一致，约 380px 宽，分节：主题下拉、动效开关、开机自启开关、数据目录（只读路径 + 打开）、关于（应用名 + 版本（源自 version.txt 的程序集版本）+ 仓库链接）+ **退出悬浮中转站**按钮（触发主窗既有 Closing 冲刷序列，复用保存语义，不得绕过）。改动立即生效并异步持久化，无确认按钮。

### 验证

- Core：偏好默认值/往返持久化/无文件兼容（复用 LocalStore 测试基建）。
- STA：齿轮开窗、主题切换立即换字典且沉浸式深色同步、动效开关翻转资源覆盖、自启假管理器读写、退出触发既有保存关闭路径、设置窗两主题渲染（SaveVisualEvidence）。
- 本机：设置窗 浅/深 截图、主题/动效/自启切换录屏（证据不入库）。

## 文档同步

- `docs/design.md`：圆角表按平台分裂（Windows：左缘圆角 8 + 右缘屏幕裁切语义与 `EdgeBleed`；Mac：左圆 12 右直，维持现状——Mac 处于暂停令下，本规格不触碰 Mac 代码）；修正「ShellCornerRadius 12 右贴边直角」过时行；动效/主题节补用户偏好覆盖说明。
- `ROADMAP.md`：设置界面移入「最近完成」（首版，注明范围）；`README.md` 增设置说明（含自启被原地更新重置）；`CHANGELOG.md` 归入 1.8.0 段。

## 红线

- 持久化原子性、置顶分区、批量顺序、拖放源数据、安全卸载边界、`PanelStateMachine` 语义不可改变。
- Mac 端零开发（2026-09-23 暂停令）；Core 改动保持 Mac 可编译、CI 绿色。
- 开发启动不改注册表；自启写注册表仅发生在用户点击开关时。
- 每项行为改动先补失败测试再实现。
