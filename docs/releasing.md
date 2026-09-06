# 发布指南

日常功能或修复先记录在 `CHANGELOG.md` 的“未发布”区。普通 `build-release.ps1` 用于验证和生成开发构建，允许该区有内容；只有显式 `-ForRelease` 才要求该区清空。

1. 确定待发布版本，将对应条目归入新的版本段落。只在根目录 `version.txt` 设置生产版本；MSBuild、ProductIdentity、Inno 和打包脚本从它取得版本。同步 README 和 PROJECT_GUIDE 中的发布说明与安装包名称，历史设计和旧版本记录不改写。
2. 在准备发布的代码上完成依赖还原和格式验证，再执行包含 Release 全量测试的打包入口；无需在同一份未变化代码上先重复跑一次全量测试：

   ```powershell
   & .\scripts\build-release.ps1 -ForRelease
   ```

3. 执行 `dotnet build FloatingTransferStation.slnx -c Release --no-restore -warnaserror`，检查真实交互截图、安装包版本与 SHA256，并完成所需的隔离安装态验证。打包成功仅证明编译与自动检查通过；公开发布、上传资产和更新远端标签是后续独立操作。

打包入口依次验证工具路径、Inno 清理行为、安装脚本约束和 Release 全量测试，然后生成自包含程序与安装包。Inno 清理测试只在 `TestResults/installer-cleanup-*` 合成目录中执行生产清理函数；不调用产品安装或卸载事件。失败时保留证据，成功时默认清理，单独复查可运行：

```powershell
& .\scripts\bootstrap-inno.ps1
& .\scripts\test-installer-cleanup.ps1 -KeepArtifacts
```

CI 先安装 SDK、还原依赖和验证格式，再通过下面的入口执行测试与打包，最后进行严格构建：

```powershell
& .\scripts\build-release.ps1 -DotnetPath (Get-Command dotnet).Source
```

CI 同时启用已有跨分类回归的截图输出，将四张使用合成内容的 WPF 控件截图上传为 `category-switch-evidence` 构建附件，保留 30 天。评审时可在 PR 中链接该附件；未生成任何截图时，上传检查会失败。本机截图和构建附件都不纳入源码提交。

审计修复的删除防重入与外部拖入重展开回归使用 `FTS_AUDIT_REMEDIATION_EVIDENCE_DIR` 输出真实 WPF 控件截图。图片均为合成内容，使用与既有截图相同的保留策略。

剪贴板图片回退回归通过合成读取器、真实图片归一化与本地存储，将损坏大图后的有效小图显示在 WPF 窗口中。CI 通过 `FTS_CLIPBOARD_IMAGE_FALLBACK_EVIDENCE_DIR` 输出截图，保存为 `clipboard-image-fallback-evidence` 附件，同样保留 30 天且缺图失败；测试不读取系统剪贴板或真实用户内容。

分类名称回归使用 `FTS_CATEGORY_NAMES_EVIDENCE_DIR` 输出新默认名称与自定义名称的真实 WPF 截图，保存为 `category-names-evidence` 附件，保留 30 天且缺图失败。

CI 还在一次性的 GitHub-hosted Windows 虚机运行 `scripts/test-installed-lifecycle.ps1`，使用真实候选安装包验证安装、原地更新、程序迁址和卸载：检查自启和数据目录登记、更新后的数据哈希（含保存分类名称的 `settings.json`）、旧卸载器拒绝操作，以及卸载后同级无关文件完整保留。缺少归属登记的旧版直接更改程序目录应被阻止；原地更新补齐登记后才允许迁址。日志和断言保存在 `installed-lifecycle-evidence` 附件中。

该脚本拒绝在本机或持久化 self-hosted runner 运行；不应伪造环境变量绕过守卫。本地验证使用原生合成清理和注册表替身，程序迁址端到端场景须由一次性 CI 实际执行后才能标为通过。脚本不自动操作数据目录向导，因此不声称覆盖真实数据跨目录迁移。

质量门全部通过后，CI 将实际经过安装验证的安装包保存为 release-candidate 附件。正式发布应使用合并后 main 提交对应的成功运行产物，记录运行链接与 SHA256；发布标签指向同一提交，避免上传未经过该次验证的重建包。

单独检查发布记录可运行 `scripts/test-release-readiness.ps1`。已有待发布内容时它应失败，不应为通过检查而删除尚未发布的变更说明。
