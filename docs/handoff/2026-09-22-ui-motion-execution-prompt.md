# 悬浮中转站 UI/动效优化 · 新窗口执行提示词（2026-09-22）

> **使用方法**：在本仓库根目录打开新的 GLM 5.3（ZCode）窗口，把本文件全文粘贴为首条消息。
> **推荐节奏**：先用 **Plan 模式** 完成现状核对与实施计划（产出 spec+plan 文档），经用户批准后切到 **Goal/执行模式** 逐项落地；每完成一个可独立验证的阶段就提交一次。

## 背景

你是悬浮中转站仓库（GitHub `Oiawlm/floating-transfer-station`）的维护执行代理。上一个规划窗口已完成资源调研与文档梳理，产出三份交接材料（应已提交在 `codex/ui-motion-research` 分支；若 `git log`/`git status` 都找不到，停止并告知用户，不要凭记忆继续）：

1. `docs/research/2026-09-22-ui-motion-resources.md` —— 资源调研与选型结论（必读）
2. `docs/research/2026-09-22-docs-review.md` —— 文档梳理报告与缺陷清单 D-1～D-10（必读）
3. `docs/handoff/2026-09-22-ui-motion-execution-prompt.md` —— 本提示词

技术栈：.NET 10；Windows 端 WPF（Win11 为目标），Mac 端 Avalonia 11.3（仅 Apple Silicon）；共享 `FloatingTransferStation.Core`；MSTest；本机 `.tools/dotnet` 与 Inno 已就绪。

## 总目标（一句话，可核对）

按资源调研确定的 Fluent 基线，完成 A 轨（文档体系落地修缮）与 B 轨（Windows 端界面与动效升级 + Mac 端同步），全部通过既有质量门，UI 变化附真实截图证据。

## 必读输入（动手前按序读完）

`AGENTS.md`（硬规则与常设授权，优先级最高）→ 上述三份交接材料 → `README.md`、`CONTRIBUTING.md`、`PROJECT_GUIDE.md`、`docs/architecture.md` → 界面代码：`src/FloatingTransferStation/Resources/MainWindowStyles.xaml`、`src/FloatingTransferStation/Views/MainWindow.xaml` 及 partial（含 `.VisualTransitions.cs`）、`Views/CategoryFeedbackAnimation.cs`、`src/FloatingTransferStation.Mac/MainWindow.cs`。

## A 轨：文档落地（先行，低风险，可独立提交）

按文档梳理报告执行：

- **D-3**：`docs/handoff/` 已由本提示词开实例，补一段目录用途说明并确认 `docs/README.md` 已登记。
- **D-4**：B 轨 S1 的交付物 `docs/design.md`（设计 token 规范）。
- **D-5**：`docs/architecture.md` 补一句双端视觉策略（共用 token、平台各自渲染）。
- D-1、D-2 已由规划窗口修复，核对无误即可。
- D-6（用户对 README 的本地未提交改动与 pelican_bicycle 两个文件）**不要动**，涉及提交时先征询用户。
- D-7、D-9 绑定下个版本发布流程，本次不做；D-8 在 A/B 完成后再评估是否迁移。

## B 轨：界面与动效升级（核心）

**设计基线**（依据与出处见资源调研 §三/§四）：时长三档 83/167/250ms；入场曲线 `KeySpline 0,0,0,1`、出场 `1,0,1,1`；面板展开/收起 250/200ms 不对称；列表入场交错 30–50ms；悬停反馈 83–167ms；动效可中断、不阻断命中测试；**必须保留对系统“减弱动效”的现有降级路径**。**默认不引入第三方 UI 依赖**（wpfui 仅作参考实现，引入需用户确认）；图标从微软 `fluentui-system-icons`（MIT）取几何重绘，不分发字体文件。

**分阶段改造点**（顺序执行，每阶段独立可验证、可提交）：

- **S1 设计 token 化**：颜色/圆角/时长/缓动/间距集中定义（清理 `ReviewEditor` 等硬编码色），产出 `docs/design.md`；WPF 资源字典与 Mac 常量共用同一组数值；解决 Windows 紫 `#6D5DFB` 与 Mac 青绿 `#327A72` 的分歧（色板定稿是决策点，Plan 阶段给用户选）。
- **S2 动效升级**：hover 颜色/边框/阴影加过渡（VisualStateManager + VisualTransition）；卡片选中、置顶、删除的反馈动画；新条目交错入场；StatusOverlay、选择框、图钉按钮显隐淡入淡出；拖放插入指示器出现动画；**收起动画**（现为硬切换，改为约 200ms 加速出场；必须保持现有“收起视觉交接”防闪烁机制与面板状态机语义，回归测试覆盖快速展开/收起竞态）。
- **S3 深度与层级**：卡片与悬浮窗阴影分层（elevation），hover 轻微上浮或 1.0→1.02 缩放；滚动条 hover 展宽过渡。
- **S4 图标统一**：按 Fluent System Icons 几何重绘图钉/垃圾桶/恢复尺寸/选择等 Path，统一线宽与端点风格。
- **S5 深色模式**：跟随系统亮暗；token 两套切换（评估 .NET 内置 `ThemeMode` 或手工切换，注意内置 Fluent 主题的设计器白屏坑）；合成截图证据在两种主题下各出一组。
- **S6 材质评估（独立决策点）**：Mica/亚克力与 `AllowsTransparency=True` 分层窗口存在冲突；先做窗口壳改造小样（弃用 AllowsTransparency，改 WindowChrome/DWM 圆角 + `DWMWA_SYSTEMBACKDROP_TYPE`，参考 wpfui `WindowBackdrop` 实现），验证通过并经用户确认后再全量实施；验证失败则保留分层窗口 + S3 阴影方案，并在 CHANGELOG/文档如实记录结论。
- **S7 Mac 端同步**：同一组 token 落地 Mac 界面（统一配色、补 Avalonia Transitions 的展开/收起/hover 动效），Windows 上跑 Avalonia 预览核对，CI Apple Silicon 验证；如实标注无 Mac 实机人工验收。

**底层代码顺带优化边界**：动画常量集中化、散落 Storyboard 收敛到 VisualStateManager、`NativeMethods.cs` 增补 DWM 调用（S6 用）。仅限为 UI 服务的最小重构，禁止大重写。

## 硬约束（红线，全文以 AGENTS.md/CONTRIBUTING.md 为准）

- 持久化原子性、置顶分区、批量顺序、拖放源数据、安全卸载边界等契约不可改变；改契约先开 Issue。
- Windows（Win11）与 Mac（Apple Silicon）同步维护，共用业务核心与 `version.txt`；只影响单端时说明原因并跑另一端适用回归。
- 每个行为改动先补失败测试再实现；UI/交互变化必须补自动回归，并用 computer-use 技能启动本机应用拍真实截图/录屏作为证据展示给用户（截图不入库）。
- 提交前质量门：Core Debug 预构建 → `dotnet format --verify-no-changes` → Release 全量测试 → 严格 Release 构建 0 警告（命令与顺序见 `PROJECT_GUIDE.md`）。
- 日常改动写入 `CHANGELOG.md`「未发布」区；同步更新受影响的 README/路线图/架构说明。
- 不提交 `.tools/`、`artifacts/`、`TestResults/`、用户内容、凭据、本机截图；不动用户的 README 本地改动与 pelican 文件。
- 常设授权：创建任务分支、普通推送；需要质量门时建 PR 并处理本次改动导致的 CI 失败。不含强推、凭据配置、仓库/全局设置变更。
- 新功能按仓库惯例先写 spec+plan（建议 `docs/superpowers/specs/2026-09-22-ui-motion-upgrade-design.md` 与 `docs/superpowers/plans/2026-09-22-ui-motion-upgrade.md`）并登记 `docs/README.md` 索引。

## 执行方式

1. **Plan 模式**：核对交接材料与仓库现状 → 写 B 轨 spec+plan（含每阶段验证方式与截图清单）→ 请用户批准计划，并定稿决策点（色板、是否引库、S6 是否实施、D-6 处理）。
2. 批准后切 **Goal/执行模式**：A 轨 → B 轨 S1→S7 顺序实施；小步提交（每阶段至少一次），每阶段跑相关测试选集，全部完成后跑全量质量门。
3. 任何决策点拿不准就停下来问用户；修复先复现，不臆测。

## 验收标准（Done when）

- A 轨各条落地，`docs/README.md` 索引与仓库实际一致。
- S1–S5、S7 完成：两端 token 一致、动效符合基线参数、深浅色跟随系统；S6 完成评估并有明确结论（实施或写明搁置理由）。
- 格式验证、Release 全量测试、严格构建全部通过（含 Mac CI 分支）；新动效有自动回归覆盖（含可中断性与减弱动效降级）。
- 每个 UI 阶段有真实截图证据已展示给用户；CHANGELOG「未发布」区已更新；工作已推送分支，PR 质量门通过或按用户指示合入。
