# 项目指南

## 项目状态

悬浮中转站是一个活跃维护的 .NET 10 桌面应用，Windows 10/11 64 位正式版使用 WPF 和 Inno Setup，macOS 14+ 候选版使用 Avalonia 11.3.21；两端使用 MSTest 和同一共享核心。公开仓库为 `Oiawlm/floating-transfer-station`，当前源码版本为 1.5.1；正式安装包以 [Releases](https://github.com/Oiawlm/floating-transfer-station/releases) 为准。Mac 双架构源码与构建已加入，原生验收/签名公证尚未完成。

当前直接引用 `SixLabors.ImageSharp 3.1.12`。ImageSharp 4.x 的直接引用要求有效构建许可证；升级前须先解决许可，不自行申请或绕过密钥校验。依据见 [Six Labors 官方说明](https://sixlabors.com/posts/licence-enforcement-changes/)。

## 主要目录

- 生产版本只修改根目录 version.txt；程序集、产品标识、安装器和打包脚本共同读取该来源。

- `src/FloatingTransferStation/`：WPF 窗口和 Windows 系统集成。
- `src/FloatingTransferStation.Core/`：两端共享的模型、业务、图片输入限制与原子存储。
- `src/FloatingTransferStation.Mac/`：Mac Avalonia 界面和 NSPasteboard 适配；Windows 也可运行独立数据预览。
- `tests/FloatingTransferStation.Core.Tests/`、`tests/FloatingTransferStation.Mac.Tests/`：跨平台业务与 Mac 适配回归。
- `tests/FloatingTransferStation.Tests/`：单元、STA 窗口交互、生命周期和对抗性回归测试。
- `installer/`：Inno Setup 安装与安全卸载脚本。
- `scripts/`：本地 .NET/Inno 引导、质量门和 Release 构建入口。
- `docs/`：架构、发布指南、现场观察和历史设计/计划，统一入口为[文档索引](docs/README.md)。

## 可复现命令

首次准备本地工具：

已有工具可以分别运行 scripts/bootstrap-dotnet.ps1 -VerifyOnly 和 scripts/bootstrap-inno.ps1 -VerifyOnly，只核对版本，不下载或升级。SDK 按 global.json 的滚动策略解析，Inno 固定为 7.0.2。

```powershell
& .\scripts\bootstrap-dotnet.ps1
& .\.tools\dotnet\dotnet.exe restore FloatingTransferStation.slnx
```

提交前质量门：

```powershell
& .\.tools\dotnet\dotnet.exe build src/FloatingTransferStation.Core/FloatingTransferStation.Core.csproj -c Debug --no-restore -warnaserror
& .\.tools\dotnet\dotnet.exe format FloatingTransferStation.slnx --verify-no-changes --no-restore
& .\.tools\dotnet\dotnet.exe test FloatingTransferStation.slnx -c Release --no-restore
& .\.tools\dotnet\dotnet.exe build FloatingTransferStation.slnx -c Release --no-restore -warnaserror
```

同步生成 Windows 安装包和两个 Mac 候选 ZIP：

```powershell
& .\scripts\build-release.ps1
```

打包入口同时执行工具路径契约、Release 全量测试及隔离 Inno 清理行为测试。CI 通过 `-DotnetPath` 复用已安装的 SDK；本地默认使用 `.tools/dotnet`。`-ForRelease` 额外要求“未发布”区为空，日常构建不受此限制。

Mac 本地开发使用 `FloatingTransferStation.Mac.slnx`，单独打包使用 `scripts/build-macos.ps1`；Mac 原生执行 `-RuntimeIdentifier osx-arm64` 或 `osx-x64` 加 `-SmokeTest` 可验证包内启动并生成展开/收起截图。本机构建不能代替 Mac 实机验证，包旁 JSON 明确记录此状态。

定向检查可使用 `scripts/run-adversarial.ps1 -Scope Clipboard`、`-Scope Interaction` 或 `-Scope Lifecycle`。测试文件按职责拆分，但类名和既有选集保持稳定。

WPF 交互测试使用 STA 和真实 Dispatcher。若全量运行中仅有布局或滚动测试偶发失败，先单独复跑原测试，再复跑全量测试；只有稳定复现并确定根因后才修改产品或测试。

## 必须保持的契约

需要打包时按发布指南组合检查，复用打包入口中的全量测试，不必先额外执行同一份全量测试。1.4.4 的修复与输入容量边界见 [CHANGELOG](CHANGELOG.md#144) 和[架构说明](docs/architecture.md)。

- `LocalStore` 保持磁盘写入原子性；`BoardMutationService` 在保存失败时恢复对象、状态和精确顺序，窗口层恢复选择与滚动位置。
- 每个分类始终先置顶区、后普通区；批量操作保持源显示顺序。
- 内部批量拖放保持源数据、选择作用域和跨分类分区语义。
- 卸载只删除应用登记并管理的数据目录，不扩大到用户选择的父目录。
- 数据清理递归目标仅为已验证的 `Data`；上层目录仅在空目录时移除，同级文件必须保留。
- 应用只读取安装器登记的设置；开机自启由安装器写入，开发启动不改写注册表。
- 异步保存后的选择恢复只能作用于操作发起时的分类；用户已取消选择时不得重新选中。
- UI 或交互变化必须补充自动回归，并提供真实运行截图或录屏。

## 仓库卫生

不要提交 `.tools/`、`.worktrees/`、`artifacts/`、`TestResults/`、`bin/`、`obj/`、用户内容、凭据或本机截图。发布规则与贡献边界分别以 `README.md`、`CONTRIBUTING.md`、`AGENTS.md` 和 `docs/architecture.md` 为准。
