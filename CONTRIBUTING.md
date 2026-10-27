# 参与贡献

谢谢你愿意改进悬浮中转站。这个项目欢迎 Bug 修复、文档完善、测试补充和经过讨论的新功能。

## 先说问题，再写方案

小而明确的修复可以直接提交 PR。下面这些变化请先开 Issue，对齐用户问题和边界后再实现：

- 新增主要交互或改变已有操作语义。
- 改变 `board.json`、图片目录或其他持久化格式。
- 改变删除、清空、迁移或卸载范围。
- 改变安装、更新、开机自启和单实例行为。
- 改变 Windows 剪贴板或拖放数据契约。

功能建议先说明真实场景和期望结果，不要求你提前设计完整技术方案。

## 开发环境

Windows 正式版使用 .NET 10、WPF、MSTest 和 Inno Setup，引用 `FloatingTransferStation.Core`。仓库脚本会把 Windows 开发工具准备在不提交的 `.tools/` 中。2026-10-08 起 Mac 端永久停止开发并已从仓库移除，历史 Release 与 tag 原样保留。

开发循环与本地推送前质量门（分层测试协议，2026-10-18 起；2026-10-27 判定序 v2：约定派生优先＋例外表覆写＋未知回退）：

```powershell
# 开发循环（改完代码后的快速反馈）：按变更集四层判定序选择计划——
# 豁免（纯文档）→ L1 例外表/全量表 → L2 约定派生（MainWindow.{Area}.cs 等命名约定自动可测）
# → L3 未知回退全量（[affected-full] 标记行＋消除指引）。
# Core.Tests 整项目恒跑作强制基线；区域选中 0 条测试按失败处理；跟踪源文件三态覆盖反向守卫 fail fast。
& .\scripts\run-affected-tests.ps1 -Scope Edit   # 只看本次编辑（基线 HEAD）

# 本地推送前质量门（五命令）：
& .\.tools\dotnet\dotnet.exe build src/FloatingTransferStation.Core/FloatingTransferStation.Core.csproj -c Debug --no-restore -warnaserror
& .\.tools\dotnet\dotnet.exe format FloatingTransferStation.slnx --verify-no-changes --no-restore
& .\scripts\check-repo-hygiene.ps1
& .\scripts\run-affected-tests.ps1 -Scope Integrate   # 整条分支变更集（merge-base，默认值；显式写出便于自文档化）
& .\.tools\dotnet\dotnet.exe build FloatingTransferStation.slnx -c Release --no-restore -warnaserror
```

分层测试协议的分工：本地负责「受影响范围反馈」，CI 的 `quality` 任务（“格式、测试与构建”）在 PR 上运行 Release 全量测试，是**权威全量门**——本地提交 PR 前不再要求跑全量 `dotnet test`，同一份代码不重复跑全量（与发行列车「无需在同一份未变化代码上重复跑全量」同一原则）。选择器自身带回归防线：App 交互测试文件按词根表派生类边界过滤器（src 侧约定派生同样骑在该表上，新功能登记＝写测试时登记词根），方法名偏离文件词根由一致性守卫报错；全部例外表过滤器对真实测试清单做恒非零漂移检查、派生规则恒非零校验内联；`scripts/test-affected-selector.ps1` 内嵌场景矩阵＋历史回放做选择器契约测试（0 退出码＝通过；改选择器两文件不再全量，由该契约测试区域承接）。选区语义的权威描述见[设计契约清单](docs/design-contracts.md)。三条纪律：

- 不得为省时跳过受影响测试：映射命中的区域必须全绿，失败先修复再提交。
- 空选集/未知路径必须回退全量（脚本自动执行并打印理由），不得把 0 条选中当通过。
- 纯文档/文档图片变更豁免跳过测试（仓库卫生检查与 CI 全量门仍然兜底），不得借豁免夹带代码变更。

格式工具以 Debug 设计时配置加载 WPF XAML，首次运行前需要上面的 Core Debug 预构建，让外部类型引用可解析。它不是运行或发布配置，Release 检查仍独立执行。

生成安装包时运行：

```powershell
& .\scripts\build-release.ps1
```

该入口运行 Release 全量测试、工具路径契约、隔离的 Inno 清理行为测试，并生成 Windows 安装包。CI 的质量门执行同一入口；最终“格式、测试与构建”等待其通过。清理测试只操作 `TestResults` 中新建的合成目录，不执行产品安装或卸载。

后续功能和修复以 Windows 11 为目标：业务规则放共享核心，系统接口在 Windows 项目适配；共同修改 `version.txt`，不维护分叉版本号。

日常改动写入 `CHANGELOG.md` 的“未发布”区。准备正式发布时，先整理目标版本记录，再运行 `build-release.ps1 -ForRelease`；完整流程见[发布指南](docs/releasing.md)。

## 并行开发与分支纪律

多个窗口/会话并行开发时，按下面的纪律工作。原则：**版本是发行属性，不是分支属性**——版本号、README「下载」段、GitHub Release 与 tag 只由发布指南定义的「发行列车」从 main 串行赋值，功能分支永不触碰。

- **分支命名**：`<类型>/<slug>`（如 `feat/note-entry-strip-button`、`fix/undo-anchor`），**不带版本号**。版本在发行时才赋值，分支名里写版本会诱导把版本当作分支属性。
- **创建即推送**：分支创建后立即 `git push -u origin <分支>`，此后每个提交都推送。会话中断（窗口被关、机器断电）不丢任何工作。
- **合并走 PR**：合并进 main 必须经 PR 与 `quality` 质量门；**合并前把最新 `origin/main` 同步进分支**（merge 或 rebase 由你决定）。PR 合并被 GitHub 串行化，这是并行窗口之间唯一的合并点。
- **轨道文件冲突约定**：`CHANGELOG.md`「未发布」区冲突=**保留双方条目**（两类改动都该被发行列车消费）；`version.txt` 与 README「下载」段冲突=说明有并行发行，按「以远端 main 为准重放」处理（功能分支本不该改这两个文件——出现即流程违规信号）。
- **合并后归位**：切回 main、`git pull --ff-only`、删除本地与远端功能分支、`git fetch --prune --tags`。
- **工作区隔离**：并行执行窗口**默认 git worktree 兄弟目录**（`../悬浮中转站-worktrees/<slug>/`；gitignored 本地指令文档不随 worktree 复制，创建后须手动拷入）；独立完整 clone 留给需要完全隔离 refs/config/stash 的环境；任何情况**不得共享同一个 checkout**（分支切换互踩、暂存区互相污染）。worktree 创建模板、三角色分工、冲突面申报与串行集成编排见[多窗口并行开发协议](docs/multi-window.md)。

## 提交 PR 前

- 一个 PR 只解决一个清楚的问题，避免顺手重构无关代码。
- 修复先证明失败路径；行为变化补充与风险相称的回归测试。
- 保留现有原子保存、批内顺序、置顶分区、拖放源数据和安全卸载语义。
- 运行格式验证、受影响范围分层测试（`scripts/run-affected-tests.ps1`）、仓库卫生检查和严格构建，并在 PR 中记录结果；Release 全量由 CI `quality` 权威门承担，本地不重复全量。
- CI 的 `quality` 任务名“格式、测试与构建”是 `main` 分支保护要求的检查名称；扩充安装包等检查时保留该名称，避免检查通过但 PR 仍无法合并。
- UI 或交互变化附真实截图或录屏；不要使用生成图冒充产品现场。
- 同步更新受影响的 README、路线图、架构说明或更新记录；README 改动须叠加 [README 风格指南](docs/readme-style-guide.md) 的项目增量规则，并按其文末公开元规范指引执行通用写作方法论。
- 拆分测试文件时保留测试发现与定向选集；当前窗口交互、生命周期测试采用同名 partial 类，避免因文件拆分漏跑测试。
- 不提交 `.tools/`、`artifacts/`、`TestResults/`、用户数据、凭据或本机桌面截图。

## 评审与合并

维护者会优先检查用户结果、兼容性、失败恢复和测试证据。PR 必须通过 GitHub 质量门并解决评审对话后才能合并；仓库采用 MIT License，提交即表示你有权按该许可证贡献这些改动。
