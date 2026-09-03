# 发布指南

日常功能或修复先记录在 `CHANGELOG.md` 的“未发布”区。普通 `build-release.ps1` 用于验证和生成开发构建，允许该区有内容；只有显式 `-ForRelease` 才要求该区清空。

1. 确定待发布版本，将对应条目归入新的版本段落。同步项目 `.csproj`、`ProductIdentity.cs`、Inno `MyAppVersion`、`LifecycleTests.ReleaseMetadata.cs` 与 `LifecycleTests.Installer.cs` 中的版本约束，以及 README 和 PROJECT_GUIDE 中的版本与安装包名称。历史设计和旧版本记录不改写。
2. 在准备发布的代码上运行格式验证、Release 全量测试和严格构建，再执行：

   ```powershell
   & .\scripts\build-release.ps1 -ForRelease
   ```

3. 检查真实交互截图、安装包版本与 SHA256，并完成所需的隔离安装态验证。打包成功仅证明编译与自动检查通过；公开发布、上传资产和更新远端标签是后续独立操作。

打包入口依次验证工具路径、Inno 清理行为、安装脚本约束和 Release 全量测试，然后生成自包含程序与安装包。Inno 清理测试只在 `TestResults/installer-cleanup-*` 合成目录中执行生产清理函数；不调用产品安装或卸载事件。失败时保留证据，成功时默认清理，单独复查可运行：

```powershell
& .\scripts\bootstrap-inno.ps1
& .\scripts\test-installer-cleanup.ps1 -KeepArtifacts
```

CI 先安装 SDK、还原依赖和验证格式，再通过下面的入口执行测试与打包，最后进行严格构建：

```powershell
& .\scripts\build-release.ps1 -DotnetPath (Get-Command dotnet).Source
```

单独检查发布记录可运行 `scripts/test-release-readiness.ps1`。已有待发布内容时它应失败，不应为通过检查而删除尚未发布的变更说明。
