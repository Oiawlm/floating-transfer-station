# 实施计划：右缘边缘裁切与设置首版（2026-09-23）

规格见 [2026-09-23 设计规格](../specs/2026-09-23-edge-bleed-and-settings-design.md)。执行提示词原文归档于 `docs/handoff/2026-09-23-settings-and-right-edge-execution-prompt.md`。

## 阶段 0：登记（本文件与规格、提示词归档、docs/README.md 索引）

## 阶段 1：轨道一 · 右缘边缘裁切

1. [ ] Core：`WindowSettings.EdgeBleed` 常量 = `DesignTokens.DwmCornerRadius`（先补失败测试）。
2. [ ] `WindowController` 三放置增 `edgeBleed = 0` 参数；宽度加裁切量、`Left = workArea.Right - 可见宽度`、可见宽度钳制 ≤ 工作区宽度。测试先行（三放置 × 裁切 0/8）。
3. [ ] `NativeMethods` 增 `WM_DISPLAYCHANGE`/`MonitorFromPoint`/`GetMonitorInfo`/`EnumDisplayMonitors`；新建 `ScreenEdgeGeometry`（public 纯判定 + 原生探测），回退判定单测先行（伪造矩形：任务栏贴右、邻接显示器、正常单屏、多屏右邻）。
4. [ ] `MainWindow` 增可选右缘裁切提供器参数（默认 null = 0）；6 处 `WindowController` 调用、`DockRight`、`WidthThumb_DragDelta` 传入裁切量；`WndProc` 增 `WM_DISPLAYCHANGE` 重估 + 按当前面板状态重新贴齐。STA 几何测试先行（展开/收起含裁切、轨道 58px 完整可见、可见右缘 == 工作区右缘）。
5. [ ] 跑既有回归：`ExpandedPlacement_KeepsTheStationaryRailPointerInsideEveryGeometryUpdate`、`Collapse_HidesExpandedSurfaceBeforeMovingToTheCollapsedRow`、三个像素契约测试原样通过。
6. [ ] computer-use 本机取证：浅/深 × 展开/收起 贴边截图 + 展开收起录屏，展示给用户（不入库）。
7. [ ] 提交（任务分支）。

## 阶段 2：轨道二 · 设置首版

1. [ ] Core：`ThemePreference` 枚举 + `AppPreferences` record + `IPreferencesStore` + `AppPaths.PreferencesFile` + `LocalStore` 实现（原子写 + .bak 回退 + 损坏保留）。测试先行：默认值、往返、无文件、损坏回退。
2. [ ] Core：`IStartupManager` 接口（查询/启用/禁用）。Mac 不接线（暂停令）。
3. [ ] Windows：`WindowsStartupManager`（HKCU Run，值名「悬浮中转站」，带引号 `Environment.ProcessPath`，与 .iss 完全同格式；仅显式调用时写）。假管理器测试先行。
4. [ ] `MainWindow` 接线：偏好构造参数 + 启动主题解析 + `WM_SETTINGCHANGE` 仅 FollowSystem 响应 + PreviewOverride 最高优先 + 强制主题同步沉浸式深色 + 动效本地资源覆盖/移除 + 偏好异步原子持久化。测试先行（主题解析、资源覆盖翻转）。
5. [ ] 齿轮按钮（HeaderActions 最左，Fluent 齿轮几何）+ `SettingsWindow`（Mica 壳复用、分节、立即生效、退出走主窗 Closing）。STA 测试先行：齿轮开窗、主题切换换字典 + 沉浸式深色、动效开关翻转、自启假管理器读写、退出触发既有保存关闭路径、两主题渲染证据（SaveVisualEvidence + CI 证据目录约定）。
6. [ ] computer-use 本机取证：设置窗浅/深截图 + 主题/动效/自启切换录屏，展示给用户（不入库）。
7. [ ] 提交。

## 阶段 3：轨道三 · 文档

1. [ ] `docs/design.md`：圆角表按平台分裂（Windows 左圆 8 + 右缘屏幕裁切/`EdgeBleed`；Mac 左圆 12 右直维持）；修正 ShellCornerRadius 过时行；动效/主题节补用户偏好覆盖。
2. [ ] `ROADMAP.md`（设置界面 → 最近完成，注明首版范围）、`README.md`（设置说明 + 自启被原地更新重置说明）、`CHANGELOG.md`（未发布区记录）。
3. [ ] 提交。

## 阶段 4：收尾（统一发行 v1.8.0）

1. [ ] `version.txt` → 1.8.0；CHANGELOG 归入 1.8.0 段；README/PROJECT_GUIDE 发布说明同步。
2. [ ] 质量门：Core Debug 预构建 → `dotnet format --verify-no-changes` → Release 全量测试 → 严格 Release 构建 0 警告。
3. [ ] 任务分支 → 推送 → PR → CI「格式、测试与构建」全绿（含 Apple Silicon 回归；若因本次改动失败仅做恢复绿色的最小修复）→ 合入 main。
4. [ ] `build-release.ps1 -ForRelease` → 同一 GitHub Release v1.8.0 上传 Windows 安装包 + `osx-arm64` zip（如实标注未公证测试版）+ `SHA256SUMS.txt`；记录运行链接与 SHA256；README 下载入口同步。
5. [ ] 本机安装：优雅关闭运行实例 → 静默原地更新 → 重启 → 验证版本 1.8.0 与自启 → 向用户展示验证结果。

## 完成标准

见规格「验证」与执行提示词「验收标准」；全部满足后本计划视为完成。
