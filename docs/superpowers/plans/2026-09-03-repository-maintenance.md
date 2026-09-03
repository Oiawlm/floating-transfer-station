# Repository Maintenance Implementation Plan

> **For agentic workers:** Use superpowers:subagent-driven-development or superpowers:executing-plans for the bounded tasks below. The user approved implementation of the completed repository audit on 2026-09-03. Independent file ownership permits parallel work; .NET builds and tests are coordinated by the primary agent.

**Goal:** 修复审查确认的四项行为问题，补齐交付检查，并完成有证据的局部组织与重复产物整理。

**Architecture:** 保留现有模型、业务变更、串行 gate、原子持久化及 WPF partial 分层。安装器独占开机自启登记；数据清理只递归删除已验证 Data；被忽略的剪贴板通知仍保持前驱依赖；异步选择恢复限定于原分类。

**Tech Stack:** .NET 10、WPF、MSTest、PowerShell、Inno Setup、GitHub Actions。

**Status:** 本地实施与验证已于 2026-09-04 完成，工作分支 `codex/repository-maintenance-20260903`，基线 `0db9f2f`。维护提交 `526f7a0` 已推送并进入[草稿 PR #12](https://github.com/Oiawlm/floating-transfer-station/pull/12)，评审和发布状态以后续 PR 与 Release 为准。AGENTS.md、旧工作树与唯一验证资料保留。

## Task 1: 剪贴板通知顺序

**Files:** `Services/ClipboardCaptureService.cs`, `ClipboardCaptureServiceTests.cs`。

- [x] 添加“旧图片阻塞 → 重复/不可读通知 → 新文字”回归；先确认原实现产生旧图在前的失败。
- [x] 保留通知入口冻结分类与提前读取，保证所有提前退出仍连接前驱；去重按处理顺序判断。
- [x] 运行 `dotnet test FloatingTransferStation.slnx -c Release --no-restore --filter FullyQualifiedName~ClipboardCaptureServiceTests`，验证正常、重复、读取失败与取消路径。

## Task 2: 跨分类选择与职责整理

**Files:** `Views/MainWindow.SelectionAndDeletion.cs`, `Views/MainWindow.VisualTransitions.cs`, `Views/MainWindow.xaml.cs`, 新的分类编辑 partial；`MainWindowInteractionTests*.cs`。

- [x] 用阻塞保存新增单项置顶与清空后切分类的失败回归，检查 B 的选择及删除提示。
- [x] 在完成回调加入与批量置顶一致的分类守卫：`if (_viewModel.ActivePanel?.Category != category) return;`；保留 Esc 版本守卫。
- [x] 仅迁移分类编辑事务与其状态到独立 partial；按交互职责拆分测试文件，保留类名以保持选集。
- [x] 运行完整窗口交互测试，并保存修复后真实窗口截图；不读取系统剪贴板或用户内容。

## Task 3: 安装清理边界

**Files:** `installer/悬浮中转站.iss`, 新的 `scripts/test-installer-cleanup.ps1`，现有安装契约测试。

- [x] 从生产安装脚本提取实际清理与路径函数，在 TestResults 的唯一隔离目录内编译/运行 Inno 测试入口；只执行合成目录操作，安装初始化结束即退出。
- [x] 先验证旧实现会删除 Data 同级哨兵，再改为 `DelTree(DataDirectory, True, True, True)`；成功后仅尝试 `RemoveDir(ManagedParent)`。
- [x] 验证同级文件/程序文件保留、空父目录移除、非法路径拒绝；复核迁移、回滚清理与卸载调用方。

## Task 4: 自启责任与日常/发布检查

**Files:** `App.xaml.cs`, `AppLifecycleService.cs`, `StartupRegistrationService.cs`, `LifecycleTests*.cs`, `CHANGELOG.md`, 发布检查脚本。

- [x] 保留审查中的自启失败探针证据；移除 App 的运行时自启登记与随之失去用途的实现，生命周期只负责单实例所有权。
- [x] 保留并更新真实互斥生命周期测试，验证安装脚本仍为默认/自定义安装目录写入正确的带引号自启命令。
- [x] 向未发布区添加本次修复记录，确认原文档测试失败；移除日常“未发布必须为空”的限制，增加明确发布准备检查。
- [x] 按职责拆分生命周期测试且保留类名；验证定向选集未减少。

## Task 5: 交付链与文档

**Files:** `.github/workflows/ci.yml`, `scripts/build-release.ps1`, `scripts/test-release-readiness.ps1`, `README.md`, `CONTRIBUTING.md`, `PROJECT_GUIDE.md`, `docs/architecture.md`, `docs/README.md`。

- [x] CI 执行路径契约、隔离清理契约与安装包编译；复用本地打包入口。
- [x] 常规打包允许未发布条目；显式 `-ForRelease` 打包必须先通过发布准备检查。
- [x] 文档说明当前职责、检查命令和已确认现场观察；历史设计/计划通过索引标明对应版本、提交和后续修订，保持原文。

## Task 6: 局部清理、复核与交付

- [x] 删除已核对无引用的 DefaultNames、BoardSnapshot.Empty、DangerHoverBrush、DangerPressedBrush；保留类型和样式文件。
- [x] 对已确认安装包重复副本重新核对保留副本及 SHA256，逐个去重并记录可恢复来源；只移除普通空目录，保留有内容或目录联接的旧工作树。
- [x] 运行格式验证、Release 全量测试、严格构建、隔离 Inno 清理测试和安装包编译；不执行真实安装/卸载。
- [x] 完成独立规格与代码审查，修复必要反馈；核对最终差异、文档链接、真实截图和本地清理记录。

所有命令使用仓内 `.tools/dotnet/dotnet.exe`。质量门为 `format --verify-no-changes --no-restore`、`test -c Release --no-restore`、`build -c Release --no-restore -warnaserror`。本计划在完成时写回实际结果，不将未经验证的手工步骤标记为完成。

## 完成证据与执行修正

- 剪贴板 4 项、跨分类选择 2 项回归均先在旧实现上失败，再随修复通过。窗口回归补齐真实 DispatcherSynchronizationContext 后能稳定观察完成回调。
- 最终打包入口完整运行：395/395 测试通过；Inno 清理 6 个原生案例、23 项断言通过；自包含程序和安装包编译成功。之后严格构建为 0 警告/0 错误，格式验证通过。
- 本地提交前再次运行格式验证、Release 全量测试和严格构建：395/395 通过、无跳过，构建 0 警告/0 错误，格式验证通过。提交前复核未再修改实现或放宽测试断言。
- 拆分保留原 123 个窗口测试、15 条 DataRow、54 个辅助成员；只新增 2 个窗口回归。同名 partial 保持原测试发现与选集。
- 拆分遗漏的 StringCollection 命名空间已在编译检查中发现并补回。一个未改动的悬停反馈测试曾在全量中失败，单独复跑及再次全量均通过，未改其断言。
- 发布准备的正常/未发布/缺失标题/重复标题用例通过；真实 `-ForRelease` 调用也正确拒绝了当前未发布记录。
- 宽泛的批量删除命令曾被自动审批拒绝。逐层检查发现 8 个“无文件”目录实际包含指向工具目录的联接，已从清理范围排除；随后以具体非递归路径成功删除 5 份重复包（211.75 MiB）和 1 个普通空目录。所有保留副本经 SHA256 回验。
- 原第 6 份重复包位于安装输出目录，已由本轮构建按既有流程重新生成，成为不同内容的开发包并保留。历史 Release 副本未改动。
- 剪贴板、安装/自启/交付链、窗口修复与机械迁移均完成独立规格和代码质量复核，无未解决的重要发现。
- 真实窗口截图、清理及恢复映射、成功打包输出和结果说明保存在不提交的 `artifacts/repository-maintenance-20260903/`。新包保留原版本元数据 1.4.1，仅作本地开发验证；未执行真实安装或卸载，未触发远端 CI。
- PR 交付时补充 CI 截图附件：复用 `FTS_CATEGORY_SWITCH_EVIDENCE_DIR`，上传四张合成内容的 WPF 回归截图并保留 30 天；源码中不增加图片或用户资料。前一条的“未触发远端 CI”是本地实施完成时的历史状态。
