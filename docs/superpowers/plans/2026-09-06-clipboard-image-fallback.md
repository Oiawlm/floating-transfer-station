# Clipboard Image Fallback Implementation Plan

> **For agentic workers:** Use focused implementation and independent specification/code reviews. The user authorized continuous execution; do not pause for routine implementation decisions.

**Goal:** 一个剪贴板图片表示损坏时，从同次复制的其他有效表示完成 PNG 导入。

**Architecture:** `ImageNormalizer` 保留 Identify 排序，按序尝试解码；只捕获明确坏图片异常，保存与取消失败直接传播。调用方、原子写入和通知顺序不变。同一 PR 包含向后兼容的 1.4.3 补丁版元数据，最终安装包只在合并后 main 重建。

**Tech Stack:** .NET 10、WPF、MSTest、ImageSharp 3.1.12。

## Task 1: 失败复现与边界回归

**Files:** `tests/FloatingTransferStation.Tests/ImageNormalizerTests.cs`，按需要补充 `ClipboardCaptureServiceTests.cs`。

- [x] 合成有效 PNG，截断为只有 PNG 签名/IHDR 的表示；先断言 Identify 能给出宽高且 Load 无法解码，再将其放在比有效候选更大的优先级，公共 `NormalizeClipboardAsync` 必须回退。
- [x] 运行新测试观察旧实现失败。覆盖编码与位图回退、最大有效候选优先、同面积位图优先、全部损坏、取消和目标不覆盖，使用真实图片字节及临时目录。

```powershell
& .\.tools\dotnet\dotnet.exe test FloatingTransferStation.slnx -c Release --no-restore --filter FullyQualifiedName~ImageNormalizerTests
```

## Task 2: 最小根因修复

**Files:** `src/FloatingTransferStation/Services/ImageNormalizer.cs`。

- [x] 将 `FirstOrDefault` 改为沿现有排序遍历；每次尝试前检查取消。位图继续 `SaveBitmap`；编码表示只在 `Image.Load` 的 `UnknownImageFormatException`/`InvalidImageContentException` 时跳过，成功加载后在 `using` 范围内执行现有首帧与 PNG 原子保存。
- [x] 提取的解码辅助函数仅返回可释放图片或不可用结果；原 `SaveEncodedImage` 改为接收已解码图片的保存函数。全部候选耗尽抛已有文本的 `InvalidDataException`，不得吞掉保存/取消错误。
- [x] 相关测试 GREEN，审查保存范围、文件清理、候选顺序、取消传播和旧调用方语义。

## Task 3: 文档、现场验证与交付

**Files:** README、CHANGELOG、PROJECT_GUIDE、`docs/README.md`、`docs/releasing.md`，本规格与计划；产品 `.csproj`、`ProductIdentity.cs`、Inno `MyAppVersion`、`LifecycleTests.ReleaseMetadata.cs` 和 `LifecycleTests.Installer.cs` 中的当前版本契约；窗口回归 `MainWindowInteractionTests.ClipboardCapture.cs`、现有测试工厂和 `.github/workflows/ci.yml` 的截图输出。

- [x] 记录剪贴板图片表示回退的用户行为，不改路线图为新 UI 功能；说明 ImageSharp 4.x 暂停原因，保留其 PR。
- [x] 真实 WPF 窗口显示本次合成回退导入结果，保存忽略目录中的截图；不访问系统剪贴板或真实用户内容。新增 STA 回归同时验证真实归一化、原子存储、重新读取和窗口绑定，CI 独立附件缺图失败。
- [x] 在本 PR 同步 1.4.3：先将现有三项版本/安装器/公开资料测试中的当前版本与下载资产期望改为 1.4.3，并在该版本 CHANGELOG 节锁定“继续尝试同次复制的其他有效表示”“不吞掉取消或写入错误”，观察旧版本 RED；再同步三处生产版本、README、项目指南和索引，归档两条回退记录，复跑 GREEN。保留所有旧发布历史及日常未发布记录语义。
- [x] 运行格式验证、Release 全量测试、严格构建与正式打包；独立规格和代码审查通过。
- 交付顺序：精确提交、推送、普通 PR 合并，确认 main CI。远端完成状态以本次 PR、对应质量门和 v1.4.3 Release 为准。

```powershell
& .\.tools\dotnet\dotnet.exe format FloatingTransferStation.slnx --verify-no-changes --no-restore
& .\scripts\build-release.ps1 -ForRelease
& .\.tools\dotnet\dotnet.exe build FloatingTransferStation.slnx -c Release --no-restore -warnaserror
git diff --check
```

- 发布步骤：在最终 main 重新执行 `build-release.ps1 -ForRelease`，确认 ProductVersion=`1.4.3+最终提交`、安装包 FileVersion=1.4.3，轻量 tag v1.4.3 只指向已通过 CI 的最终 main。发布唯一 `FloatingTransferStation-Setup-1.4.3.exe` 为正式 Latest，并在新目录下载回验本地/远端 digest/下载 SHA256 和长度；不上传合并前候选。

## 恢复状态

2026-09-06：从已发布且干净的 main `775e231` 创建 `.worktrees/clipboard-image-fallback`，分支 `codex/clipboard-image-fallback`。实际完成证据逐项写回本轮记录，不把候选路径或历史计划当成已验证结果。

根因复现：新增归一化回退用例在旧实现上 7 项失败、真实捕获回退用例 1 项失败；最小修复后归一化与捕获定向集 49/49 通过。版本元数据先观察 3 项旧版本失败，再同步为 1.4.3 后 3/3 通过。日志保存在忽略的 `artifacts/clipboard-image-fallback/`。

窗口证据：新增 STA 回归 1/1 通过，真实 `ClipboardCaptureService`、`ImageNormalizer` 和 `LocalStore` 将 640×360 损坏表示后的 320×180 有效表示保存并显示。截图已人工查看；独立规格、生产代码和 STA/CI 增量审查均无阻塞项。

提交前本地验证：完整格式检查通过；`build-release.ps1 -ForRelease` 通过 407/407 测试（跳过 0）、6 个原生清理案例/23 项断言、自包含发布与 Inno 安装包编译；严格 Release 构建 0 警告、0 错误。完整测试同时生成四张跨分类截图及一张图片回退截图。证据保存于忽略的 `artifacts/clipboard-image-fallback/final-local/`；候选包只作验证，发布资产按上述步骤从最终 main 重建。
