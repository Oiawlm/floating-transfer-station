# 悬浮中转站 · 设置入口 + 右缘圆角修正 · 新窗口执行提示词（2026-09-23）

> **使用方法**：在本仓库根目录（`D:\Download\Agent Vault\悬浮中转站`，main 分支，v1.7.0 已发布）打开新的 ZCode（GLM-5.3）窗口，把本文件全文粘贴为首条消息，并以 **Goal/执行模式** 运行。本提示词携带 2026-09-23 规划窗口的定稿结论，无需重新规划；遇到未覆盖的决策点停下来问用户。

## 背景

你是悬浮中转站仓库（GitHub `Oiawlm/floating-transfer-station`）的维护执行代理。上一个规划窗口已完成两个方向的代码实读、官方/社区调研与方案定稿。

技术栈：.NET 10；Windows 端 WPF（Windows 11 为目标）+ Inno Setup；Mac 端 Avalonia（**按 2026-09-23 指令全面暂停开发**，仅保留 CI 回归）；共享 `FloatingTransferStation.Core`；MSTest；本机 `.tools/dotnet` 与 Inno 就绪。

两个任务：
1. **右缘圆角修正**：窗口恒贴屏幕右缘（`WindowController` 三种放置的 Left 均 = `workArea.Right - width`，无 DockSide 概念），1.7.0 起 DWM 四角统一 8px 圆角后，贴边侧上下两个圆角观感别扭。
2. **设置入口（新功能首版）**：主题/动效/开机自启三项 + 数据目录展示 + 关于 + 退出入口。

## 已定决策（不要重新发散）

- **右缘方案 = 边缘裁切**：窗口右缘越出屏幕 8px，DWM 圆角整体落在屏外——可见右缘平直、左缘保留圆角。依据：Win11 官方几何规范「与其他直边相交的直边不圆角（0px）」「窗口贴靠/最大化时不保留圆角」（learn.microsoft.com/windows/apps/design/signature-experiences/geometry）；`DWMWA_WINDOW_CORNER_PREFERENCE` 四角一体、无逐角选项（learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute）；`SetWindowRgn` 会破坏 DWM 合成面与 Mica 且边缘锯齿，**禁止使用**。已评估否决：四角全直（DONOTROUND）、浮离右缘留缝、维持现状。
- **设置首版** = 主题（跟随系统/浅色/深色）+ 动效开关 + 开机自启开关；数据目录只读展示+打开、关于（版本/仓库链接）、退出按钮默认包含；剪贴板采集开关**不纳入**本版（留待 Issue 收集）。
- **入口形态** = 头部操作区齿轮按钮 + 独立设置小窗（复用 1.7.0 Mica 壳技术）。

（以上为规划窗口按用户未逐项作答时的推荐默认；若用户在本窗口另行指示，以用户最新指示为准。）

## 总目标（一句话，可核对）

完成右缘边缘裁切与设置首版（含 Core 偏好持久化与 Windows UI），全部通过既有质量门与 CI，按发布指南完成 v1.8.0 统一发行，并把新版静默安装到用户本机、验证版本与自启。

## 必读输入（动手前按序读完）

`AGENTS.md`（优先级最高）→ 本提示词 → `README.md`、`CONTRIBUTING.md`、`PROJECT_GUIDE.md`、`docs/design.md`、`docs/releasing.md`、`docs/handoff/README.md` → 源码：`src/FloatingTransferStation/Services/{DwmWindowEffects,NativeMethods,WindowController,DesignThemeManager,DataDirectorySettings}.cs`；`src/FloatingTransferStation.Core/Models/WindowSettings.cs`、`Core/Design/DesignTokens.cs`、`Core/Services/{LocalStore,AtomicTextWriter,AppPaths,IBoardStore}.cs`；`src/FloatingTransferStation/Views/MainWindow.xaml` 及 partial（`.xaml.cs`/`.Lifecycle.cs`/`.VisualTransitions.cs`/`.SelectionAndDeletion.cs`）、`Views/WindowShellClip.cs`、`Resources/{MainWindowStyles.xaml,DesignTheme.Light.xaml,DesignTheme.Dark.xaml}` → 测试：`tests/FloatingTransferStation.Tests/{MainWindowInteractionTests.cs 及 .VisualLifecycle/.DesignTokens/.DesignTheme partial、WindowControllerTests.cs、LifecycleTests.Installer.cs}` → `installer/悬浮中转站.iss`、`scripts/build-release.ps1`、`scripts/test-installed-lifecycle.ps1`。

## 轨道一：右缘边缘裁切（先行，可独立提交）

1. Core：`WindowSettings` 增加 `EdgeBleed` 常量（= `Design.DesignTokens.DwmCornerRadius` 即 8；结构性尺寸权威在 `WindowSettings`，见 docs/design.md）。纯新增，不影响 Mac。
2. `WindowController.Collapsed/Expanded/CategoryRail` 增加边缘裁切量参数（默认 0 保持现契约；Windows 壳传入 8）：宽度加裁切量、`Left = workArea.Right - 可见宽度`（窗口右缘越出工作区右缘一个裁切量）。保持纯函数。
3. `MainWindow.Lifecycle.cs`：`DockRight()` 与 `WidthThumb_DragDelta` 同步（`Width = PanelWidth + TabWidth + 裁切量`）；可见宽度按「可见宽度 ≤ 工作区宽度」钳制（Normalize 之后在控制器层收口，避免极窄屏左缘出屏）。
4. 新增 Windows 服务（如 `ScreenEdgeGeometry`，P/Invoke `MonitorFromPoint`/`GetMonitorInfo`）：仅当工作区右缘 == 所在显示器右缘且右侧无紧邻显示器时返回可裁切；右贴任务栏/邻接显示器回退裁切量 0（现状贴齐）。`WndProc` 增加 `WM_DISPLAYCHANGE`：重估并重新贴齐。
5. 内容层**不动**：`WindowShellClip`/`DwmWindowEffects`/样式全不改（右描边 0、头部右上直角本就契合平直右缘）。Mica、深浅主题、拖动缩放、收起防闪烁、命中测试语义全部保持。
6. 测试（先失败后实现）：
   - `WindowControllerTests` 演进：裁切 8 时断言「可见右缘 `Left+Width-8 == workArea.Right`、窗口右缘 == `workArea.Right+8`」；裁切 0 保持旧断言；三种放置 × 两档。
   - 回退判定单测（伪造显示器/工作区矩形，不依赖真机多屏）。
   - STA：展开/收起几何含裁切量、轨道 58px 完整可见；既有 `ExpandedPlacement_KeepsTheStationaryRailPointerInsideEveryGeometryUpdate`、`Collapse_HidesExpandedSurfaceBeforeMovingToTheCollapsedRow`、像素契约测试 `WindowShell_ClipsAllCornersToTheDwmRadiusWithShellTint`/`CollapsedHandle_ClipsRailBackgroundToDwmRoundedCorners`/`WindowShell_UsesGlassChromeWithoutLayeredTransparency` 必须**原样通过**（内容层契约不变正是本方案的验证要点）。
7. 证据：computer-use 启动本机应用（`FTS_PREVIEW_DATA_DIR` 隔离 + `FTS_PREVIEW_THEME` 两主题），右缘贴边状态 浅/深 × 展开/收起 真实截图各一组 + 展开收起录屏，展示给用户，不入库。

## 轨道二：设置首版（Core 先行，UI 随后）

1. Core 偏好模型：`AppPreferences` record（`ThemeMode` 枚举 FollowSystem/Light/Dark + `bool AnimationsEnabled = true`），持久化为数据目录下 `preferences.json`：复用 `IAtomicTextWriter` 原子写与既有备份回退机制（参照 `LocalStore` 对 settings.json 的做法），`LocalStore` 增读写 API（接口演进不得破坏既有假实现，必要时默认接口成员或独立接口）；旧安装无此文件 → 全默认。**不改 `WindowSettings` 契约与 settings.json 形状。**
2. 主题接线：MainWindow 启动与 `WM_SETTINGCHANGE` 按 `ThemeMode` 解析（FollowSystem → `DetectSystemTheme`；强制 → 固定值且不再响应系统变化）；`DesignThemeManager.PreviewOverride` 仍最高优先（`FTS_PREVIEW_THEME` 取证机制不破坏）；切换时同步 `DWMWA_USE_IMMERSIVE_DARK_MODE`。
3. 动效接线：`AnimationsEnabled=false` → `window.Resources[SystemParameters.ClientAreaAnimationKey]=false`（本地覆盖系统键，测试基建已用同手法）；`=true` → 移除本地覆盖恢复跟随系统。完全复用现有减弱动效降级路径，不改任何动画实现。
4. 开机自启：Core 定义 `IStartupManager`（查询/启用/禁用），Windows 实现读写 HKCU `…\CurrentVersion\Run` 值（与 `installer/悬浮中转站.iss` 完全相同的值名与带引号路径格式）；安装器 .iss 与 `LifecycleTests.Installer` 契约**不动**；仅用户点击开关时写注册表（开发启动保持只读）。已知代价：原地更新会重置自启为开——写入 CHANGELOG 与 README 说明。开关显示状态读注册表（不持久化到 preferences.json）。
5. UI：
   - `MainWindow.xaml` 的 `HeaderActions` 增加齿轮按钮（`HeaderActionButtonStyle` 同构、Fluent 齿轮 Path、ToolTip/AutomationProperties「设置」；排列位置自行按 Fluent 惯例定，避免紧贴红色清空按钮）。
   - 新建 `SettingsWindow`（仓库首个二级窗口）：WindowChrome `GlassFrameThickness=-1` + `DwmWindowEffects` Mica + 壳样式与深浅主题字典复用，Topmost 与主窗一致，约 380px 宽，分节（官方设置指引：learn.microsoft.com/windows/apps/design/app-settings/guidelines-for-app-settings）：主题（下拉：跟随系统/浅色/深色）、动效（开关）、开机自启（开关）、数据目录（只读路径 + 打开按钮）、关于（应用名 + 版本（程序集信息版本，源自 version.txt）+ 仓库链接）+ **退出悬浮中转站**按钮（触发主窗既有 Closing 冲刷序列，复用保存语义，不得绕过）。改动立即生效并异步持久化，无确认按钮。
6. 测试（先失败后实现）：Core 偏好默认值/往返持久化/无文件兼容；STA——齿轮开窗、主题切换立即换字典且沉浸式深色同步、动效开关翻转资源覆盖、自启假管理器读写、退出触发既有保存关闭路径、设置窗两主题渲染（`SaveVisualEvidence`）。
7. 证据：设置窗 浅/深 截图、主题/动效/自启切换录屏，展示给用户，不入库。

## 轨道三：文档与登记（与代码同步提交）

- **S0（执行第一步）**：按仓库惯例落盘 spec+plan（建议 `docs/superpowers/specs/2026-09-23-edge-bleed-and-settings-design.md` 与 `docs/superpowers/plans/2026-09-23-edge-bleed-and-settings.md`），登记 `docs/README.md` 索引；并把本提示词原文落盘 `docs/handoff/2026-09-23-settings-and-right-edge-execution-prompt.md` 一并提交。
- `docs/design.md`：圆角表按平台分裂（Windows：左缘圆角 8 + 右缘屏幕裁切语义与 `EdgeBleed`；Mac：左圆 12 右直，维持现状），修正「ShellCornerRadius 12 右贴边直角」过时行；动效/主题节补用户偏好覆盖说明。
- `ROADMAP.md`：设置界面从「准备研究」移入「最近完成」（首版，注明范围）；`README.md` 增设置说明（含自启被原地更新重置的说明）；`CHANGELOG.md` 归入 1.8.0 段。

## 收尾（必做，不可选）

1. 版本：`version.txt` → 1.8.0（功能+修正），CHANGELOG/README/PROJECT_GUIDE 发布说明同步。
2. 质量门：Core Debug 预构建 → `dotnet format --verify-no-changes` → Release 全量测试 → 严格 Release 构建 0 警告（命令与顺序见 PROJECT_GUIDE.md/CONTRIBUTING.md）。
3. 任务分支 → PR → CI「格式、测试与构建」全绿（含 Apple Silicon 验证：Mac 零开发，若因本次改动失败仅做恢复绿色的最小修复）→ 合入 main。
4. 统一发行 v1.8.0：`build-release.ps1 -ForRelease` → 同一 GitHub Release 上传 Windows 安装包、`osx-arm64` zip（既有 Mac 代码重建打包，如实标注未公证测试版）、`SHA256SUMS.txt`；记录运行链接与 SHA256；README 下载入口同步。
5. 本机安装：优雅关闭运行中的实例 → 静默原地更新 → 重启 → 验证版本号为 1.8.0、自启项正常，并向用户展示验证结果。

## 硬约束（红线，全文以 AGENTS.md/CONTRIBUTING.md 为准）

- 持久化原子性、置顶分区、批量顺序、拖放源数据、安全卸载边界、`PanelStateMachine` 语义不可改变；改契约先开 Issue。
- Mac 端零开发（暂停令）；Core 改动保持 Mac 可编译、CI 绿色。
- 每项行为改动先补失败测试再实现；UI 变化必须自动回归 + computer-use 真实截图/录屏证据（不入库）。
- 不提交 `.tools/`、`artifacts/`、`TestResults/`、用户内容、凭据、本机截图；不动 `.zcode/` 与用户本地文件。
- 常设授权：任务分支、普通推送、PR 与处理本次 CI 失败；不含强推、凭据配置、仓库/全局设置变更。
- 开发启动不改注册表；自启写注册表仅发生在用户点击开关时。

## 执行方式

Goal/执行模式直接开工：S0 spec+plan → 轨道一 → 轨道二 → 轨道三 → 收尾；小步提交（每阶段至少一次），每阶段跑相关测试选集，最后全量质量门。任何决策点拿不准或影响契约/公开行为的停下来问用户；修复先复现，不臆测。

## 验收标准（Done when）

- 右缘：贴边状态可见右缘平直、左缘圆角、Mica 与两主题正常，拖动/缩放/收起/防闪烁回归全过；裁切回退逻辑有测试。
- 设置：三项偏好立即生效并原子持久化，旧数据/升级兼容，退出入口走既有保存路径；齿轮入口有自动化回归。
- 质量门与 CI 全绿；v1.8.0 统一 Release 已发布（Windows + Apple Silicon 包 + SHA256SUMS）；本机已静默更新并验证版本与自启。
- 文档（design.md/README/ROADMAP/CHANGELOG/docs/README.md 索引）同步；每处 UI 变化有已向用户展示的真实截图/录屏证据。
/goal
