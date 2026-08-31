# 项目指南

## 项目状态

悬浮中转站是一个活跃维护的 Windows 10/11 64 位 WPF 应用，使用 .NET 10、MSTest 和 Inno Setup。公开仓库为 `Oiawlm/floating-transfer-station`，当前稳定发布为 1.4.0。

## 主要目录

- `src/FloatingTransferStation/`：WPF 应用、窗口交互、模型与本地服务。
- `tests/FloatingTransferStation.Tests/`：单元、STA 窗口交互、生命周期和对抗性回归测试。
- `installer/`：Inno Setup 安装与安全卸载脚本。
- `scripts/`：本地 .NET/Inno 引导、质量门和 Release 构建入口。
- `docs/`：架构说明，以及已确认功能的设计和实施计划。

## 可复现命令

首次准备本地工具：

```powershell
& .\scripts\bootstrap-dotnet.ps1
& .\.tools\dotnet\dotnet.exe restore FloatingTransferStation.slnx
```

提交前质量门：

```powershell
& .\.tools\dotnet\dotnet.exe format FloatingTransferStation.slnx --verify-no-changes --no-restore
& .\.tools\dotnet\dotnet.exe test FloatingTransferStation.slnx -c Release --no-restore
& .\.tools\dotnet\dotnet.exe build FloatingTransferStation.slnx -c Release --no-restore -warnaserror
```

生成安装包：

```powershell
& .\scripts\build-release.ps1
```

WPF 交互测试使用 STA 和真实 Dispatcher。若全量运行中仅有布局或滚动测试偶发失败，先单独复跑原测试，再复跑全量测试；只有稳定复现并确定根因后才修改产品或测试。

## 必须保持的契约

- `LocalStore` 保持磁盘写入原子性；`BoardMutationService` 在保存失败时恢复对象、状态和精确顺序，窗口层恢复选择与滚动位置。
- 每个分类始终先置顶区、后普通区；批量操作保持源显示顺序。
- 内部批量拖放保持源数据、选择作用域和跨分类分区语义。
- 卸载只删除应用登记并管理的数据目录，不扩大到用户选择的父目录。
- UI 或交互变化必须补充自动回归，并提供真实运行截图或录屏。

## 仓库卫生

不要提交 `.tools/`、`.worktrees/`、`artifacts/`、`TestResults/`、`bin/`、`obj/`、用户内容、凭据或本机截图。发布规则与贡献边界分别以 `README.md`、`CONTRIBUTING.md`、`AGENTS.md` 和 `docs/architecture.md` 为准。
