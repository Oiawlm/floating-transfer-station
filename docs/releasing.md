# 发布指南

日常功能或修复先记录在 `CHANGELOG.md` 的“未发布”区。普通 `build-release.ps1` 用于验证并同步生成 Windows 安装包、Apple Silicon 与 Intel 两个 macOS 候选包，允许该区有内容；只有显式 `-ForRelease` 才要求该区清空。两个平台始终读取同一份 `version.txt`，不能单独提升 Mac 版本。

1. 确定待发布版本，将对应条目归入新的版本段落。只在根目录 `version.txt` 设置生产版本；MSBuild、ProductIdentity、Inno 和打包脚本从它取得版本。同步 README 和 PROJECT_GUIDE 中的发布说明与安装包名称，历史设计和旧版本记录不改写。
2. 在准备发布的代码上完成依赖还原和格式验证（命令见 CONTRIBUTING.md；WPF 设计时格式工具先预构建 Core 的 Debug 程序集），再执行包含 Release 全量测试的打包入口；无需在同一份未变化代码上先重复跑一次全量测试：

   ```powershell
   & .\scripts\build-release.ps1 -ForRelease
   ```

3. 执行 `dotnet build FloatingTransferStation.slnx -c Release --no-restore -warnaserror`，检查真实交互截图、安装包版本与 SHA256，并完成所需的隔离安装态验证。打包成功仅证明编译与自动检查通过；公开发布、上传资产和更新远端标签是后续独立操作。

打包入口依次验证工具路径、Inno 清理行为、安装脚本约束和 Release 全量测试，然后生成 Windows 自包含程序与安装包，再执行 Mac 打包。任一平台失败则入口失败；已有 Windows 安装包不会因为 Mac 构建失败而删除。Inno 清理测试只在 `TestResults/installer-cleanup-*` 合成目录中执行生产清理函数；不调用产品安装或卸载事件。失败时保留证据，成功时默认清理，单独复查可运行：

```powershell
& .\scripts\bootstrap-inno.ps1
& .\scripts\test-installer-cleanup.ps1 -KeepArtifacts
```

CI 并行运行 Windows、Apple Silicon 和 Intel macOS 三个分支。Windows 分支先安装 SDK、还原依赖、预构建共享核心供 WPF 设计时加载并验证格式，再通过下面的入口执行测试与打包，最后进行严格构建；`-WindowsOnly` 只用于这个已经有独立 Mac 检查的分支：

```powershell
& .\scripts\build-release.ps1 -DotnetPath (Get-Command dotnet).Source -WindowsOnly
```

CI 同时启用已有跨分类回归的截图输出，将四张使用合成内容的 WPF 控件截图上传为 `category-switch-evidence` 构建附件，保留 30 天。评审时可在 PR 中链接该附件；未生成任何截图时，上传检查会失败。本机截图和构建附件都不纳入源码提交。

审计修复的删除防重入与外部拖入重展开回归使用 `FTS_AUDIT_REMEDIATION_EVIDENCE_DIR` 输出真实 WPF 控件截图。图片均为合成内容，使用与既有截图相同的保留策略。

剪贴板图片回退回归通过合成读取器、真实图片归一化与本地存储，将损坏大图后的有效小图显示在 WPF 窗口中。CI 通过 `FTS_CLIPBOARD_IMAGE_FALLBACK_EVIDENCE_DIR` 输出截图，保存为 `clipboard-image-fallback-evidence` 附件，同样保留 30 天且缺图失败；测试不读取系统剪贴板或真实用户内容。

分类名称回归使用 `FTS_CATEGORY_NAMES_EVIDENCE_DIR` 输出新默认名称与自定义名称的真实 WPF 截图，保存为 `category-names-evidence` 附件，保留 30 天且缺图失败。

连续范围选择回归使用 `FTS_RANGE_SELECTION_EVIDENCE_DIR` 输出 Shift 点击前后的真实 WPF 截图，保存为 `range-selection-evidence` 附件，同样保留 30 天且缺图失败；使用合成卡片验证连续选择、置顶边界与选择数量反馈。

窗口过渡回归使用 `FTS_VISUAL_TRANSITIONS_EVIDENCE_DIR` 输出收起交接被重新展开或外部拖入中断后的真实 WPF 截图，保存为 `visual-transitions-evidence` 附件，同样保留 30 天且缺图失败；使用合成内容验证窗口尺寸、面板状态与命中区域一致。

CI 还在一次性的 GitHub-hosted Windows 虚机运行 `scripts/test-installed-lifecycle.ps1`，使用真实候选安装包验证安装、原地更新、程序迁址和卸载：检查自启和数据目录登记、更新后的数据哈希（含保存分类名称的 `settings.json`）、旧卸载器拒绝操作，以及卸载后同级无关文件完整保留。缺少归属登记的旧版直接更改程序目录应被阻止；原地更新补齐登记后才允许迁址。日志和断言保存在 `installed-lifecycle-evidence` 附件中。

该脚本拒绝在本机或持久化 self-hosted runner 运行；不应伪造环境变量绕过守卫。本地验证使用原生合成清理和注册表替身，程序迁址端到端场景须由一次性 CI 实际执行后才能标为通过。脚本不自动操作数据目录向导，因此不声称覆盖真实数据跨目录迁移。

各平台检查通过后，CI 保存对应的候选包附件：Windows 为 `release-candidate`，Mac 为 `release-candidate-osx-arm64` 和 `release-candidate-osx-x64`。只有汇总 `quality`（检查名称仍为“格式、测试与构建”）成功才算整次质量门通过；它使用 `always()` 检查三个分支结果，任一分支失败、取消或跳过都会失败。单个平台附件存在不代表已完成同步验证。正式发布应使用合并后 main 提交对应的成功运行产物，记录运行链接与 SHA256；发布标签指向同一提交，避免上传未经过该次验证的重建包。

单独检查发布记录可运行 `scripts/test-release-readiness.ps1`。已有待发布内容时它应失败，不应为通过检查而删除尚未发布的变更说明。

## macOS 候选包与验证边界

Mac 端使用 .NET 10 与 Avalonia，候选包最低系统版本为 macOS 14，分别提供 `osx-arm64`（Apple Silicon）和 `osx-x64`（Intel）。最低版本依据 [.NET 10 官方支持表](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)；CI 在 macOS 15 上验证，尚不等同于所有受支持系统版本的人工验收。

Windows 上可复用仓库已有 .NET SDK 单独生成两个 Mac 包，不安装 Xcode、Apple SDK 或 .NET workload：

```powershell
& .\scripts\build-macos.ps1
```

只重建一个架构时可加 `-RuntimeIdentifier osx-arm64` 或 `-RuntimeIdentifier osx-x64`；指定 SDK 时加 `-DotnetPath (Get-Command dotnet).Source`。脚本先执行共享核心和 Mac 的 Release 测试、打包契约测试，然后以自包含、单文件模式严格发布到 `.app/Contents/MacOS`：托管程序集与运行配置内嵌进 apphost，第三方原生库保留在旁边，不启用原生库运行时自解压。生成的 ZIP 内包含完整 `.app`，无需目标用户另装 .NET。ZIP 中显式写入 Unix 来源与权限，因此在 Windows 交叉构建也保留 apphost 和原生库的可执行位。

产物分别保存为：

```text
artifacts/macos/osx-arm64/FloatingTransferStation-<version>-osx-arm64.zip
artifacts/macos/osx-x64/FloatingTransferStation-<version>-osx-x64.zip
```

每份 ZIP 旁附 `.sha256` 与 `.json`，记录版本、架构、体积、签名方式和是否通过本机启动测试。Windows 交叉构建标记为 `unsigned-cross-build`、`nativeSmokeTest: false`。它能证明构建和包格式通过，不能证明 macOS 原生交互已经通过。Windows 的 `artifacts/publish`、`artifacts/installer` 与 Mac 输出隔离。

Mac CI 使用 [GitHub 官方标准 runner](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)：`macos-15` 对应 arm64，`macos-15-intel` 对应 Intel。每个架构原生运行共享核心和 Mac 测试，再执行：

```powershell
./scripts/build-macos.ps1 -RuntimeIdentifier osx-arm64 -DotnetPath (Get-Command dotnet).Source -SmokeTest
```

Intel runner 使用 `osx-x64`。打包契约要求 `Contents/MacOS` 仅含普通 Mach-O 文件，若再次出现松散托管 DLL、配置文件或符号链接则立即失败；这符合 [Apple 对代码目录的签名约束](https://developer.apple.com/library/archive/technotes/tn2206/)，避免将托管 DLL 当作未签名的嵌套代码。Mac 上先逐个签署原生库和辅助可执行文件，再签主程序和 `.app`；签名不使用 `--deep`，仅在最终递归验证时使用。主程序与包使用 `Entitlements.plist` 中的 `com.apple.security.cs.allow-jit`，依据 [Avalonia 单文件部署与 JIT 指南](https://docs.avaloniaui.net/docs/deployment/macos)；当前 ad-hoc 候选不启用 hardened runtime，不添加 Apple Events、调试或动态库校验豁免。

随后解压实际 ZIP，检查解压后的签名和执行权限，再启动包内程序的 `--smoke-test <输出目录>`。程序必须在 60 秒内以退出码 0 结束，写出 `smoke-complete.json`、`expanded.png` 和 `collapsed.png`；超时、缺图或缺完成标识均失败。截图来自真实 Avalonia 窗口，数据来自输出目录内的合成内容；证据保存在 `macos-smoke-<rid>` 附件 30 天。

Mac smoke 还必须生成 `native-clipboard.json`，实际经过 NSPasteboard 验证合成文字、隐私标记、原始编码图片和文件 URL，检查持久化及源图片不变。每次发布合成剪贴板内容都附唯一标记；只读取该次 generation，并只在所有权仍匹配时清理，普通应用运行不执行这些测试。CI 检查四项结果和原生 CPU 架构，不能仅凭窗口启动就声称剪贴板已通过。

目前没有配置 Apple Developer ID 证书或公证凭据。所有 Mac ZIP 都是**未经公证的候选包**；ad-hoc 签名仅用于候选程序的本机完整性与启动验证，不代表 Apple 开发者身份签名或 Gatekeeper 分发批准。不要将这些产物描述为已签名公证的正式发行版。公开发布前仍须另行完成并授权 Developer ID 签名、公证及下载后启动验证；脚本不会自动上传 Apple 或 GitHub。

## 磁盘占用

Mac 构建复用现有 SDK 与 NuGet 缓存，首次增加的主要内容是 Avalonia 包和两个 macOS runtime 包。脚本串行发布两个架构，并直接发布进临时 `.app`，避免再复制一份发布目录；默认生成 ZIP 后清理该次临时 `.app` 和解压测试副本，只保留 ZIP、校验值与元数据。`-KeepAppBundle` 可显式保留临时目录用于排障。编译产生的 `bin/obj` 与 NuGet 缓存会保留供下次复用，不会擅自清理旧 Windows 产物或全局缓存；日志报告每个 ZIP 的实际 MiB，便于按本次产物评估占用。首批本地候选包合计约 92 MiB，每个架构解压约 106–113 MiB；这不包括可复用的开发编译目录与 NuGet 缓存，后续以实际构建输出为准。
