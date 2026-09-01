# Ctrl+P 批量置顶安全边界实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 阻止 `Ctrl+P` 在面板折叠或分类名称编辑期间修改隐藏选择，同时保持正常批量置顶路径不变。

**Architecture:** 继续使用现有 `BatchPinCommand` 与 `ApplyBatchPinSelectionAsync`。命令查询阶段拒绝不可见或文字编辑上下文，执行入口再次检查同一关键状态以覆盖路由期间的状态变化；业务变更、原子保存、失败回滚、选择恢复与滚动恢复均不另建路径。

**Tech Stack:** .NET 10、WPF RoutedUICommand、MSTest STA 交互测试、PowerShell 质量门

---

## 文件职责

- `tests/FloatingTransferStation.Tests/MainWindowInteractionTests.cs`：用真实窗口和命令路由锁定折叠、文字焦点与正常执行边界，并按需生成真实 WPF 证据。
- `src/FloatingTransferStation/Views/MainWindow.SelectionAndDeletion.cs`：统一 `BatchPinCommand` 的查询与执行守卫。
- `tests/FloatingTransferStation.Tests/LifecycleTests.cs`：锁定公开文档中的安全边界说明。
- `README.md`、`CHANGELOG.md`：说明用户可见行为与未发布可靠性修复。

### Task 1: 用真实 WPF 命令复现隐藏批量置顶

**Files:**
- Modify: `tests/FloatingTransferStation.Tests/MainWindowInteractionTests.cs`

- [ ] **Step 1: 添加折叠态失败回归**

在现有 `BatchPinCommand_CtrlPInvokesTheCurrentSelection` 附近新增 `BatchPinCommand_CollapsedPanelDoesNotChangeRetainedSelection`。测试创建一张未置顶卡片，展开并选择后通过 `Root_MouseLeave` 和 `CollapseTimer_Tick` 完成真实收起，确认选择仍在，再查询并执行 `BatchPinCommand`。最终断言 `CanExecute` 为 false、卡片仍未置顶、选择仍保留且 `RecordingBoardStore.SaveCount` 为 0。

- [ ] **Step 2: 添加文字编辑失败回归**

新增 `BatchPinCommand_CategoryNameEditorFocusDoesNotChangeSelection`。展开并选择卡片后调用 `BeginCategoryNameEdit`，把键盘焦点放到真实分类 `TextBox`，再查询并执行命令。断言 `CanExecute` 为 false、编辑状态仍存在、卡片未置顶、选择不变且保存次数为 0。

- [ ] **Step 3: 添加执行入口失败回归**

新增 `BatchPinExecutionGuard_InvalidContextDoesNotChangeSelection`，用 `DataRow(false)` 覆盖折叠态、`DataRow(true)` 覆盖分类名称编辑器焦点，并通过 `InvokePrivateTask` 直接调用 `ApplyBatchPinSelectionAsync`。两个数据行都断言卡片未置顶、选择不变且保存次数为 0，证明执行入口不能只依赖先前的命令查询。

- [ ] **Step 4: 运行目标测试并确认 RED**

```powershell
& .\.tools\dotnet\dotnet.exe test tests\FloatingTransferStation.Tests\FloatingTransferStation.Tests.csproj -c Release --no-restore --filter "Name~BatchPinCommand_|Name~BatchPinExecutionGuard_"
```

预期：正常展开测试通过；命令测试稳定失败于折叠态和文字焦点下仍可执行，直接执行的两个数据行稳定失败于卡片变为置顶，而不是编译、装配或测试初始化错误。

### Task 2: 实现最小命令守卫

**Files:**
- Modify: `src/FloatingTransferStation/Views/MainWindow.SelectionAndDeletion.cs`

- [ ] **Step 1: 收紧命令查询**

在 `BatchPinCommand_CanExecute` 开头沿用 `SelectAllCommand_CanExecute` 的文字焦点路由方式：焦点为 `TextBoxBase` 时直接返回，不把事件标为已处理。其余 `e.CanExecute` 条件增加 `_viewModel.IsPanelExpanded`，并保留选择非空、未关闭和未保存中的现有条件。

- [ ] **Step 2: 收紧实际执行入口**

在 `ApplyBatchPinSelectionAsync` 的早退条件中加入 `Keyboard.FocusedElement is TextBoxBase` 与 `!_viewModel.IsPanelExpanded`。不改变后续选择冻结、排序、保存、失败恢复、选择版本或滚动恢复代码。

- [ ] **Step 3: 运行目标测试并确认 GREEN**

```powershell
& .\.tools\dotnet\dotnet.exe test tests\FloatingTransferStation.Tests\FloatingTransferStation.Tests.csproj -c Release --no-restore --filter "Name~BatchPinCommand_|Name~BatchPinExecutionGuard_"
```

预期：正常展开、折叠态、文字编辑态和两个直接执行数据行全部通过，0 失败。

### Task 3: 锁定公开行为并生成界面证据

**Files:**
- Modify: `tests/FloatingTransferStation.Tests/MainWindowInteractionTests.cs`
- Modify: `tests/FloatingTransferStation.Tests/LifecycleTests.cs`
- Modify: `README.md`
- Modify: `CHANGELOG.md`
- Verify only: `artifacts/batch-pin-command-guard/before-collapse.png`
- Verify only: `artifacts/batch-pin-command-guard/after-reexpand.png`

- [ ] **Step 1: 先添加失败的文档契约**

在 `PublicReleaseMaterials_DescribeInstallerLicenseRoadmapAndContribution` 中要求 README 包含“`Ctrl + P` 只在面板展开且不在编辑分类名称时生效”，并要求 CHANGELOG 未发布区包含“面板收起或编辑分类名称时，`Ctrl + P` 不再修改保留选择”。

- [ ] **Step 2: 运行生命周期测试并确认 RED**

```powershell
& .\.tools\dotnet\dotnet.exe test tests\FloatingTransferStation.Tests\FloatingTransferStation.Tests.csproj -c Release --no-restore --filter "Name=PublicReleaseMaterials_DescribeInstallerLicenseRoadmapAndContribution"
```

预期：测试失败于 README 或 CHANGELOG 尚无新边界说明。

- [ ] **Step 3: 更新公开文档**

README 的批量置顶操作补充 `Ctrl+P` 只在面板展开且不在编辑分类名称时生效；CHANGELOG 的“未发布”下新增“可靠性”，记录折叠态和分类名称编辑态不会再触发隐藏批量置顶。不要改版本号或发布资料。

- [ ] **Step 4: 让折叠态测试按需输出真实截图**

折叠前显示标题栏操作并把 `WindowShell` 渲染为 `before-collapse.png`；执行被拒绝的命令后重新展开同一分类，再渲染 `after-reexpand.png`。仅在 `FTS_BATCH_PIN_GUARD_EVIDENCE_DIR` 非空时调用现有 `SaveVisualEvidence`，正常测试运行不产生文件。

- [ ] **Step 5: 运行文档与目标测试并确认 GREEN**

```powershell
& .\.tools\dotnet\dotnet.exe test tests\FloatingTransferStation.Tests\FloatingTransferStation.Tests.csproj -c Release --no-restore --filter "Name~BatchPinCommand_|Name~BatchPinExecutionGuard_|Name=PublicReleaseMaterials_DescribeInstallerLicenseRoadmapAndContribution"
```

预期：全部通过，0 失败。

- [ ] **Step 6: 生成并检查真实 WPF 证据**

```powershell
$env:FTS_BATCH_PIN_GUARD_EVIDENCE_DIR = Join-Path $PWD 'artifacts\batch-pin-command-guard'
& .\.tools\dotnet\dotnet.exe test tests\FloatingTransferStation.Tests\FloatingTransferStation.Tests.csproj -c Release --no-restore --filter "Name=BatchPinCommand_CollapsedPanelDoesNotChangeRetainedSelection"
Remove-Item Env:FTS_BATCH_PIN_GUARD_EVIDENCE_DIR
```

预期：测试通过并生成两张 PNG。前后图都显示同一张未置顶卡片处于选择状态，证明收起期间的 `Ctrl+P` 没有静默改变它；文件被 `.gitignore` 忽略且不进入提交。

### Task 4: 完成质量门、范围复核与推送

**Files:**
- Verify: all tracked changes

- [ ] **Step 1: 运行完整本地质量门**

```powershell
& .\.tools\dotnet\dotnet.exe format FloatingTransferStation.slnx --verify-no-changes --no-restore
& .\.tools\dotnet\dotnet.exe test FloatingTransferStation.slnx -c Release --no-restore
& .\.tools\dotnet\dotnet.exe build FloatingTransferStation.slnx -c Release --no-restore -warnaserror
git diff --check
```

预期：格式验证退出 0；Release 全量测试 0 失败；严格构建 0 warnings、0 errors；diff 检查退出 0。

- [ ] **Step 2: 复核改动范围**

```powershell
git status --short
git diff --stat
git diff -- src/FloatingTransferStation/Views/MainWindow.SelectionAndDeletion.cs tests/FloatingTransferStation.Tests/MainWindowInteractionTests.cs tests/FloatingTransferStation.Tests/LifecycleTests.cs README.md CHANGELOG.md
```

预期：只包含规格、计划、产品守卫、相关测试和两份公开文档；不包含 `.tools/`、`artifacts/`、`TestResults/`、用户内容、凭据或本机截图。

- [ ] **Step 3: 创建聚焦提交并普通推送**

```powershell
git add docs/superpowers/specs/2026-09-02-batch-pin-command-guard-design.md docs/superpowers/plans/2026-09-02-batch-pin-command-guard.md src/FloatingTransferStation/Views/MainWindow.SelectionAndDeletion.cs tests/FloatingTransferStation.Tests/MainWindowInteractionTests.cs tests/FloatingTransferStation.Tests/LifecycleTests.cs README.md CHANGELOG.md
git commit -m "fix: guard hidden batch pin shortcut"
git push origin main
```

预期：提交成功并普通推送到当前跟踪分支；不得 force-push 或绕过失败检查。

## 计划自审

- 规格覆盖：折叠保留选择、文字编辑焦点、正常命令、查询与执行双重守卫、公开说明、真实界面证据和完整质量门均有对应步骤。
- 占位扫描：不存在未决占位内容、模糊错误处理或未定义命令。
- 类型一致性：计划只使用现有 `BatchPinCommand`、`ApplyBatchPinSelectionAsync`、`TextBoxBase`、`RecordingBoardStore`、`SaveVisualEvidence` 和测试辅助方法，没有新增公共 API。
