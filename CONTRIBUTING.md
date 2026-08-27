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

项目支持 Windows 10/11 64 位，使用 .NET 10、WPF、MSTest 和 Inno Setup。仓库脚本会把开发工具准备在不提交的 `.tools/` 中。

```powershell
& .\scripts\bootstrap-dotnet.ps1
& .\.tools\dotnet\dotnet.exe restore FloatingTransferStation.slnx
& .\.tools\dotnet\dotnet.exe format FloatingTransferStation.slnx --verify-no-changes --no-restore
& .\.tools\dotnet\dotnet.exe test FloatingTransferStation.slnx -c Release --no-restore
& .\.tools\dotnet\dotnet.exe build FloatingTransferStation.slnx -c Release --no-restore -warnaserror
```

生成安装包时运行：

```powershell
& .\scripts\build-release.ps1
```

## 提交 PR 前

- 一个 PR 只解决一个清楚的问题，避免顺手重构无关代码。
- 修复先证明失败路径；行为变化补充与风险相称的回归测试。
- 保留现有原子保存、批内顺序、置顶分区、拖放源数据和安全卸载语义。
- 运行格式验证、Release 全量测试和严格构建，并在 PR 中记录结果。
- UI 或交互变化附真实截图或录屏；不要使用生成图冒充产品现场。
- 同步更新受影响的 README、路线图、架构说明或更新记录。
- 不提交 `.tools/`、`artifacts/`、`TestResults/`、用户数据、凭据或本机桌面截图。

## 评审与合并

维护者会优先检查用户结果、兼容性、失败恢复和测试证据。PR 必须通过 GitHub 质量门并解决评审对话后才能合并；仓库采用 MIT License，提交即表示你有权按该许可证贡献这些改动。
