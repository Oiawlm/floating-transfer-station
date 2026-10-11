# 发布指南

日常功能或修复先记录在 `CHANGELOG.md` 的“未发布”区。普通 `build-release.ps1` 用于验证并生成 Windows 安装包，允许该区有内容；只有显式 `-ForRelease` 才要求该区清空。生产版本只读取根目录 `version.txt`。2026-10-08 起 Mac 端永久停止开发并已从仓库移除；历史 Release（含 1.6.0 Intel Mac 包与最后的 Apple Silicon 测试包）原样保留，不再出 Mac 新包。

版本交付采用 main 中的代码和 GitHub Release：上传 Windows 安装包与 `SHA256SUMS.txt`，README 顶部提供下载入口。功能分支推送不替代这个交付结果。

**版本是发行属性，不是分支属性。**版本号、README「下载」段、GitHub Release 与 tag 只在发行时刻由「发行列车」从 main 统一赋值；功能分支永不触碰这些轨道。并行开发的竞态根因不是合并动作本身，而是多个分支都把“下一个版本”当作自己的属性、同时改写同一批轨道文件——把版本赋值和发行收尾从功能分支剥离、由发行列车串行完成，配合 PR 合并被 GitHub 天然串行化，竞态从流程上消除。

## 功能分支职责

功能分支只包含：

- 代码与测试。
- `CHANGELOG.md`「未发布」区的新条目。
- README 中描述功能本身的正文段落。

功能分支**永不修改**：

- `version.txt`——版本在发行列车切出时才赋值。
- README 的「下载」段（发行链接与安装包名称）——只在发行列车同步。

这两类文件出现合并冲突即流程违规信号：说明有并行发行，按「以远端 main 为准重放」处理（见 [CONTRIBUTING.md](../CONTRIBUTING.md)「并行开发与分支纪律」）。

## 发行列车

发行是从 main 串行出发的短分支流程（分支名 `release/vX.Y.Z`），一次只跑一列。四条互斥防线防止并行发行互踩：

1. **分支名即锁**：`release/vX.Y.Z` 推送时若远端已存在同名分支（推送被拒），即“被抢先”信号——禁止 force push；正确动作是基于最新 main 重放、版本顺延、重跑门禁。
2. **readiness 机器断言**：`build-release.ps1 -ForRelease` 会以 `-RequireVersionAhead` 调用 `scripts/test-release-readiness.ps1`，机器断言 `version.txt` 严格大于远端最新 `v*` tag，否则报错并给出版本顺延指引。该断言只在显式发行上下文运行，绝不进入常规 CI/quality 路径（否则发布后 main 上 `version.txt` 等于最新 tag 会永久红灯）。
3. **发布前复查**：创建 Release 与 tag 前再核一次 `gh release view vX.Y.Z` 不存在。
4. **轨道冲突兜底**：发行列车一律以远端 main 为准重放；`CHANGELOG.md`「未发布」区同一条目只能被一次发行消费（重放后未发布区为空且无本列车专属变更=取消发行）。互斥主防线是前三条；轨道冲突只作兜底信号，不当作锁依赖。

**冻结语义**：`release/vX.Y.Z` 分支切出即冻结本版内容；其后合入 main 的新「未发布」条目一律顺延下一版（重放时移回「未发布」区，不得静默并入或丢失）。

列车顺序（先核对后定版）：

1. `git fetch --tags` 并核对远端最新版本（tag 清单与 `gh release list`）；远端已 ≥ 意图版本则重放顺延。
2. 从最新 `origin/main` 切 `release/vX.Y.Z`，创建后立即推送（分支名即锁在推送时生效）。
3. 整理版本段：`CHANGELOG.md`「未发布」条目归入新版本段、`version.txt` 设生产版本、README「下载」段同步发布说明与安装包名称，并同步更新本地维护文档（`PROJECT_GUIDE.md`，不入库）；历史设计和旧版本记录不改写。
4. 在准备发布的代码上完成依赖还原和格式验证（命令见 CONTRIBUTING.md；WPF 设计时格式工具先预构建 Core 的 Debug 程序集），再执行包含 Release 全量测试的打包入口；无需在同一份未变化代码上先重复跑一次全量测试：

   ```powershell
   & .\scripts\build-release.ps1 -ForRelease
   ```

5. 执行严格构建 `dotnet build FloatingTransferStation.slnx -c Release --no-restore -warnaserror`，检查真实交互截图、安装包版本与 SHA256，并完成所需的隔离安装态验证。打包成功仅证明编译与自动检查通过；公开发布、上传资产和更新远端标签是后续独立操作。
6. PR 合并 main（合并前把最新 main 同步进分支，质量门“格式、测试与构建”绿）。
7. 正式资产取合并后 main 提交对应 CI 绿跑的 `release-candidate` 产物；列车分支上的 `-ForRelease` 产物仅作门禁证据，不直接上传。
8. 复查 `gh release view vX.Y.Z` 不存在后，发布 Release：tag 指向合并提交，上传安装包与 `SHA256SUMS.txt`，记录运行链接与 SHA256，避免上传未经过该次验证的重建包。
9. 装机：先核对目标版本**大于**本机已装版本（防旧覆盖新；已装版本可从卸载登记或安装目录 exe 的版本信息读取），再优雅关闭运行中的实例、静默原地更新、重启并验证版本与自启。装机动作全局串行——同一时刻只允许一个窗口执行，版本核对作兜底。
10. 归位：切 main、`pull --ff-only`、删除本地与远端 release 分支、`git fetch --prune --tags`。

回顾触发器：若实践中出现“同窗多 PR 反复重放/合并冲突抖动”，再评估 GitHub merge queue（需要仓库设置级变更，须另行授权；引入前不采用）。

## 构建入口

打包入口依次验证工具路径、Inno 清理行为、安装脚本约束和 Release 全量测试，然后生成 Windows 自包含程序与安装包。任一步失败则入口失败。Inno 清理测试只在 `TestResults/installer-cleanup-*` 合成目录中执行生产清理函数；不调用产品安装或卸载事件。失败时保留证据，成功时默认清理，单独复查可运行：

```powershell
& .\scripts\bootstrap-inno.ps1
& .\scripts\test-installer-cleanup.ps1 -KeepArtifacts
```

CI 的 Windows 分支先安装 SDK、还原依赖、预构建共享核心供 WPF 设计时加载并验证格式，再通过下面的入口执行测试与打包，最后进行严格构建：

```powershell
& .\scripts\build-release.ps1 -DotnetPath (Get-Command dotnet).Source
```

CI 同时启用已有跨分类回归的截图输出，将四张使用合成内容的 WPF 控件截图上传为 `category-switch-evidence` 构建附件，保留 30 天。评审时可在 PR 中链接该附件；未生成任何截图时，上传检查会失败。本机截图和构建附件都不纳入源码提交。

审计修复的删除防重入与外部拖入重展开回归使用 `FTS_AUDIT_REMEDIATION_EVIDENCE_DIR` 输出真实 WPF 控件截图。图片均为合成内容，使用与既有截图相同的保留策略。

剪贴板图片回退回归通过合成读取器、真实图片归一化与本地存储，将损坏大图后的有效小图显示在 WPF 窗口中。CI 通过 `FTS_CLIPBOARD_IMAGE_FALLBACK_EVIDENCE_DIR` 输出截图，保存为 `clipboard-image-fallback-evidence` 附件，同样保留 30 天且缺图失败；测试不读取系统剪贴板或真实用户内容。

分类名称回归使用 `FTS_CATEGORY_NAMES_EVIDENCE_DIR` 输出新默认名称与自定义名称的真实 WPF 截图，保存为 `category-names-evidence` 附件，保留 30 天且缺图失败。

连续范围选择回归使用 `FTS_RANGE_SELECTION_EVIDENCE_DIR` 输出 Shift 点击前后的真实 WPF 截图，保存为 `range-selection-evidence` 附件，同样保留 30 天且缺图失败；使用合成卡片验证连续选择、置顶边界与选择数量反馈。

窗口过渡回归使用 `FTS_VISUAL_TRANSITIONS_EVIDENCE_DIR` 输出收起交接被重新展开或外部拖入中断后的真实 WPF 截图，保存为 `visual-transitions-evidence` 附件，同样保留 30 天且缺图失败；使用合成内容验证窗口尺寸、面板状态与命中区域一致。

动效过渡回归使用 `FTS_MOTION_TRANSITIONS_EVIDENCE_DIR` 输出新内容交错入场与置顶重排不重演入场的真实 WPF 截图，保存为 `motion-transitions-evidence` 附件，同样保留 30 天且缺图失败；使用合成内容验证入场动画收窄语义（真正新入库的内容才播入场）。

CI 还在一次性的 GitHub-hosted Windows 虚机运行 `scripts/test-installed-lifecycle.ps1`，使用真实候选安装包验证安装、原地更新、程序迁址和卸载：检查自启和数据目录登记、更新后的数据哈希（含保存分类名称的 `settings.json`）、旧卸载器拒绝操作，以及卸载后同级无关文件完整保留。缺少归属登记的旧版直接更改程序目录应被阻止；原地更新补齐登记后才允许迁址。日志和断言保存在 `installed-lifecycle-evidence` 附件中。

该脚本拒绝在本机或持久化 self-hosted runner 运行；不应伪造环境变量绕过守卫。本地验证使用原生合成清理和注册表替身，程序迁址端到端场景须由一次性 CI 实际执行后才能标为通过。脚本不自动操作数据目录向导，因此不声称覆盖真实数据跨目录迁移。

质量门通过后，CI 保存 Windows 候选包附件 `release-candidate`。汇总 `quality`（检查名称仍为“格式、测试与构建”）成功才算整次质量门通过；它使用 `always()` 检查 Windows 分支结果，失败、取消或跳过都会失败。候选包附件存在不代表已完成同步验证。正式发布应使用合并后 main 提交对应的成功运行产物，记录运行链接与 SHA256；发布标签指向同一提交，避免上传未经过该次验证的重建包。

## 发布记录检查

单独检查发布记录可运行 `scripts/test-release-readiness.ps1`。已有待发布内容时它应失败，不应为通过检查而删除尚未发布的变更说明。传入 `-RequireVersionAhead` 时（仅由 `build-release.ps1 -ForRelease` 传入，单独运行默认不启用）还会断言 `version.txt` 严格大于远端最新 `v*` tag——远端已存在更高版本即“被抢先”，应基于最新 main 重放并顺延版本。
