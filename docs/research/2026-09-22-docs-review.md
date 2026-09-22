# 项目文档梳理报告（2026-09-22）

> 交接材料之一。由规划窗口产出，供执行窗口落地。配套文件：
> [UI 动效与界面资源调研](2026-09-22-ui-motion-resources.md)、[新窗口执行提示词](../handoff/2026-09-22-ui-motion-execution-prompt.md)。

## 一、文档地图与职责判定

先从第一性原理明确每份文档的“唯一职责”：一个新窗口（人或代理）接到任务时，应当能沿「AGENTS.md → README → CONTRIBUTING/PROJECT_GUIDE → docs/ 索引 → 具体 spec/plan」找到全部上下文，且任何事实只有一个权威来源。按这个标准逐份过检：

| 文档 | 职责 | 评价 |
|---|---|---|
| `AGENTS.md` | 代理工作规则 + 常设授权 + 历史决策记录 | 内容准确、无冲突；但三种职责混排，见 D-8 |
| `README.md` | 产品门面：下载、能力、操作、限制 | 结构完整；有未提交本地改动（D-6）；平台支持口径待下版本收敛（D-7） |
| `CONTRIBUTING.md` | 人类贡献流程与质量门 | 完整，与 CI 检查名一致 |
| `PROJECT_GUIDE.md` | 可复现命令 + 必须保持的契约（内容沉淀） | 良好；命令与 `.tools/` 现状一致 |
| `ROADMAP.md` | 方向与最近完成 | 良好，平台范围约定与 AGENTS.md 一致 |
| `CHANGELOG.md` | 版本记录 | 「未发布」区已积累 1.6.0 后大量条目（D-9，属正常待发布状态） |
| `SECURITY.md` / `CODE_OF_CONDUCT.md` | 安全披露 / 社区准则 | 完整、够用 |
| `docs/README.md` | 文档索引 | 滞后于仓库实际内容（D-1，本次已修复） |
| `docs/architecture.md` | 架构、数据流、契约 | 有序号错误（D-2，本次已修复）；双端视觉策略未记载（D-5） |
| `docs/observations.md` | 未稳定复现的现场观察 | 良好，编号可追踪 |
| `docs/releasing.md` | 发布流程 | 内容详实；篇幅偏长但结构清晰，可接受 |
| `docs/superpowers/specs/*.md` | 已落地功能的设计决策记录 | 格式统一（目标/用户行为/数据与迁移/平台实现/验收） |
| `docs/superpowers/plans/*.md` | 实施计划（含 agentic workers 指引） | 格式统一（Goal/Architecture/执行顺序/关键验收） |

## 二、缺陷与优化清单

按优先级编号；标注「本次已修复」的条目由规划窗口直接落地，其余交给执行窗口。

- **D-1（已修复）** `docs/README.md` 索引表缺 1.6.0 后条目：缺「macOS 同步与统一发行」（`2026-09-08-macos.md` → `c6d2be1` / 1.6.0）和「每日复盘标签」（spec + plan → `33fc51d` / 未发布）两行；补入 `docs/research/` 交接材料说明。
- **D-2（已修复）** `docs/architecture.md`「核心数据流」步骤出现两个 `5.`，最后一个应为 `6.`。
- **D-3（待执行窗口）** 缺「新窗口交接」的标准入口：交接信息目前散在 AGENTS.md 常设授权 + 会话记忆里，跨窗口任务没有固定落点。建议建立 `docs/handoff/` 目录约定（本次执行提示词文件即为第一个实例），并在 `docs/README.md` 索引中登记；交接文档要素：背景、必读输入、目标、验证方式、边界。
- **D-4（待执行窗口，与 B 轨绑定）** 缺 UI 设计规范文档：颜色、圆角、动效时长、缓动、间距散落在 `MainWindowStyles.xaml` 与 Mac 端代码常量中，且两端不一致（Windows 强调色 `#6D5DFB` 紫、Mac `#327A72` 青绿）。建议 B 轨首个交付物为 `docs/design.md`（设计 token 与动效规范），代码侧同步集中定义，两端共用数值。
- **D-5（待执行窗口）** `docs/architecture.md` 未说明双端视觉层策略（统一 token 还是允许分平台差异），与 D-4 一并补充一句边界说明即可。
- **D-6（需用户决定）** 工作区存在未提交改动：`README.md` 增加了「独立 SVG 动画示例」段落，且有两个未跟踪文件 `pelican_bicycle.html` / `pelican_bicycle.svg`。属之前会话的产物，规划窗口未动。若保留，建议将示例段移至文档末尾或 `docs/`，避免打断产品主线叙事；由用户决定提交或移除。
- **D-7（挂起项提醒）** README 仍写「支持 Windows 10/11」「macOS 14+（Apple Silicon / Intel）」，与 AGENTS.md 已定方向（1.6.0 后仅以 Windows 11 为目标、Mac 仅 Apple Silicon）不一致。这属于「下一次准备版本时同步更新公开支持说明」的既定挂起项，执行窗口在下个版本发布流程中落实，不在平时顺手改动。
- **D-8（待执行窗口，保守处理）** AGENTS.md 兼具现行规则与带日期的历史授权记录，持续追加会稀释规则本身。建议后续把纯历史性授权记录迁至 `docs/decisions.md`（AGENTS.md 保留现役规则与授权总则）。改 AGENTS.md 必须小步、逐条、可回退，且不得改变任何现行授权语义。
- **D-9（下版本流程项）** CHANGELOG「未发布」区条目多，发布时按惯例整理为版本段落；与 B 轨成果一并归入下个版本。
- **D-10（低优先）** `docs/README.md` 功能表部分行的「规格」列用 Issue 链接、部分用 spec 文件，可在下次顺手统一口径；不影响使用。

## 三、执行窗口的工具与资源清单

执行窗口（Windows 11 本机）完成 B 轨所需资源核查结果：

- 本机 `.tools/dotnet`（.NET 10 SDK）与 `.tools/inno`（Inno Setup 7.0.2）已引导，可直接运行 `PROJECT_GUIDE.md` 的质量门命令。
- UI 变化的真实截图/录屏：使用 ZCode 的 computer-use 技能启动/操作本机应用并截图；截图作为验证证据展示给用户，不入库（AGENTS.md 仓库卫生要求）。
- PR 与 CI：`gh` CLI 可用；质量门检查名「格式、测试与构建」必须保持。
- Mac 端：本机无 Mac 实机，按 AGENTS.md 由 CI（Apple Silicon runner）验证 + 如实标注；不虚称实机验证。Windows 上可运行 Avalonia 预览（独立数据目录、自动采集关闭）做视觉核对。
- 不需要额外安装的软件/插件：现有技能集合（computer-use、截图判读）足够；无需 browser-use（无 Web UI）。

## 四、给执行窗口的落地顺序建议

1. 先做 A 轨（D-3、D-4、D-5 等文档修缮）：低风险、可独立提交。
2. 再做 B 轨（UI/动效优化）：按执行提示词中的设计基线与改造点清单实施，每步附真实截图与回归测试。
3. D-6 提交前征询用户；D-7、D-9 绑定下个版本发布流程；D-8 在 A、B 轨完成后再评估是否执行。
