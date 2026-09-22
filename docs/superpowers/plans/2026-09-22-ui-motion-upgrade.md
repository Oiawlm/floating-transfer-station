# 界面与动效升级实施计划

> **For agentic workers:** 按执行顺序分阶段实施，每个行为先写失败测试再写最小实现，每阶段独立提交；保持 Windows 与 Mac 同步；不改变持久化与状态机契约；UI 变化附真实截图证据（不入库）。

**Goal:** 按 Fluent 基线完成两端设计 token 化、动效升级、层级、图标统一、深色模式与 Mac 同步；窗口材质先小样验证，经用户确认后再全量实施。

**Architecture:** 共享 Core 的 `DesignTokens` 是颜色/时长/曲线/圆角/间距的唯一数值源；Windows 用资源字典 + VisualStateManager + BeginAnimation 渲染，Mac 用常量 + Avalonia Transitions 渲染；`docs/design.md` 是人类可读规范并与跨端一致性测试互锁。

**Tech Stack:** .NET 10、WPF（Windows 11 目标）、Avalonia 11.3（Apple Silicon）、MSTest（STA + headless）、DWM interop（仅材质小样）。

## 执行顺序

1. A 轨文档修缮：`docs/handoff/` 目录用途说明（D-3）、架构说明补双端视觉策略一句（D-5）、本设计与计划登记 `docs/README.md` 索引；核对 D-1/D-2 已修复。
2. S1 设计 token 化：Core `DesignTokens`、WPF 资源字典与场景时长常量取值改造、Mac 常量引用、清理硬编码色（ReviewEditor `White`、StatusOverlay `#FCFCFD`）、产出 `docs/design.md`（D-4）、跨端一致性测试。
3. S2 动效升级，分三段提交：hover/显隐过渡（VSM 基建）→ 收起动画（保留视觉交接与状态机语义，补快速往复竞态回归）→ 选中/置顶/删除反馈、新条目交错入场、插入指示器入场。
4. S3 深度与层级：卡片阴影分层、hover 上浮、滚动条悬停展宽；悬浮窗壳外阴影与 S6 结论绑定。
5. S4 图标统一：按 fluentui-system-icons（MIT）几何重绘图钉/垃圾桶/恢复尺寸/对勾，统一线宽与端点。
6. S5 深色模式：双套 token 字典、系统跟随（`WM_SETTINGCHANGE` + `AppsUseLightTheme`）、Mac `RequestedThemeVariant` 跟随、两主题截图。
7. S6 材质小样：NativeMethods 增补 DWM 调用，弃用 `AllowsTransparency` 的窗口壳变体验证；展示证据后停下等用户决策，通过才全量实施，失败则保留分层窗口 + 阴影方案并如实记录。
8. S7 Mac 同步：token 落地、Avalonia Transitions、减弱动效尽力支持、headless 测试、Windows 上 Avalonia 预览截图、CI Apple Silicon 验证。
9. 全量质量门（Core Debug 预构建 → `dotnet format --verify-no-changes` → Release 全量测试 → 严格 Release 构建 0 警告），CHANGELOG「未发布」区逐阶段记录，推送分支并创建 PR 处理 CI。

## 关键验收

- 跨端 token 一致：WPF 资源、Core DesignTokens、Mac 常量三处数值相等，`docs/design.md` 同步。
- 动效符合基线参数（83/167/250ms 三档、进 `0,0,0,1` 出 `1,0,1,1`、收起 200ms 加速出场、交错 30–50ms），全部可中断且尊重系统减弱动效。
- 「收起视觉交接」防闪烁机制与 `PanelStateMachine` 语义不变，60/250ms 时序不变，快速展开/收起竞态有回归。
- 删除等数据命令恰好执行一次、顺序不变，动画只影响视觉层。
- 深浅色跟随系统，两主题各有真实截图；S6 有明确结论（实施或写明搁置理由）。
- 质量门与 Mac CI 通过；每阶段独立提交并附证据。
