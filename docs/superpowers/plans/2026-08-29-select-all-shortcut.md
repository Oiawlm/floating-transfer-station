# Ctrl+A 全选当前分类 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 `Ctrl+A` 在非文本编辑状态下一次选择当前分类的全部卡片，并保持分类名称编辑器的原生文字全选语义。

**Architecture:** 复用 WPF 的 `ApplicationCommands.SelectAll` 路由命令，在主窗口增加窗口级命令绑定，只在面板展开、窗口未关闭且当前分类非空时调用 `BoardList.SelectAll()`。现有选择变化处理器继续同步批量操作 UI；业务服务和持久化层不参与。

**Tech Stack:** .NET 10、WPF RoutedCommand、MSTest STA 交互测试、Markdown 公开文档。

---

### Task 1: 用 WPF 交互测试锁定全选和文本编辑边界

**Files:**
- Modify: `tests/FloatingTransferStation.Tests/MainWindowInteractionTests.cs`

- [ ] **Step 1: 写当前分类全选的失败测试**

在既有选择测试附近增加：

```csharp
[STATestMethod]
public void SelectAllCommand_CtrlASelectsEveryCurrentItemWithoutSaving()
{
    using var directory = new TestDirectory();
    var board = new BoardService();
    var bottom = board.AddText("bottom");
    var middle = board.AddText("middle");
    var top = board.AddText("top");
    board.AddText("other category", BoardCategory.Reference);
    var store = new RecordingBoardStore(directory.Root);
    var window = CreateWindow(board, store, WindowSettings.Default);

    try
    {
        window.Show();
        ExpandCategory(window, BoardCategory.Inbox);
        CompleteLayout(window);
        var list = (ListBox)window.FindName("BoardList");
        var badge = (Border?)window.FindName("SelectedCountBadge");
        var count = (TextBlock?)window.FindName("SelectedCountText");
        var batchPin = (Button?)window.FindName("BatchPinButton");
        Assert.IsNotNull(badge);
        Assert.IsNotNull(count);
        Assert.IsNotNull(batchPin);
        Assert.IsTrue(ApplicationCommands.SelectAll.InputGestures.OfType<KeyGesture>().Any(
            gesture => gesture.Key == Key.A &&
                       gesture.Modifiers == ModifierKeys.Control));
        Assert.IsTrue(ApplicationCommands.SelectAll.CanExecute(null, window));

        ApplicationCommands.SelectAll.Execute(null, window);
        CompleteLayout(window);

        CollectionAssert.AreEquivalent(
            new[] { top, middle, bottom },
            list.SelectedItems.Cast<BoardItem>().ToArray());
        Assert.AreEqual(Visibility.Visible, badge.Visibility);
        Assert.AreEqual("3", count.Text);
        Assert.AreEqual(Visibility.Visible, batchPin.Visibility);
        Assert.AreEqual(0, store.SaveCount);
    }
    finally
    {
        CloseWindow(window);
    }
}
```

- [ ] **Step 2: 写分类名称编辑器保留原生文字全选的回归测试**

增加：

```csharp
[STATestMethod]
public void SelectAllCommand_CategoryNameEditorSelectsTextWithoutExpandingCardSelection()
{
    using var directory = new TestDirectory();
    var board = new BoardService();
    var selected = board.AddText("selected");
    board.AddText("not selected");
    var window = CreateWindow(directory, board);

    try
    {
        window.Show();
        ExpandCategory(window, BoardCategory.Inbox);
        CompleteLayout(window);
        var list = (ListBox)window.FindName("BoardList");
        list.SelectedItems.Add(selected);
        var viewModel = (MainWindowViewModel)window.DataContext;
        var panel = viewModel.Categories.Single(
            category => category.Category == BoardCategory.Inbox);
        viewModel.BeginCategoryNameEdit(panel);
        CompleteLayout(window);
        var editor = FindDescendants<TextBox>(FindCategoryTab(window, panel)).Single();
        Assert.IsTrue(editor.Focus());
        Keyboard.Focus(editor);
        editor.CaretIndex = editor.Text.Length;
        editor.SelectionLength = 0;

        ApplicationCommands.SelectAll.Execute(null, editor);
        CompleteLayout(window);

        Assert.AreEqual(0, editor.SelectionStart);
        Assert.AreEqual(editor.Text.Length, editor.SelectionLength);
        CollectionAssert.AreEqual(
            new[] { selected },
            list.SelectedItems.Cast<BoardItem>().ToArray());
    }
    finally
    {
        CloseWindow(window);
    }
}
```

- [ ] **Step 3: 增加空分类、收起和关闭中的负向测试**

增加 `SelectAllCommand_EmptyCategoryCannotExecuteOrSave`：Inbox 为空但 Reference 有内容，清除键盘焦点后断言命令不可执行，直接执行也不选择项目、不保存板或窗口设置。

增加 `SelectAllCommand_CollapsedPanelCannotExecuteOrSelectHiddenItems`：Inbox 有两项且预选一项，按真实 `Root_MouseLeave` 和 `CollapseTimer_Tick` 流程收起；断言命令不可执行，直接执行后仍精确保留原选择且没有保存。

增加 `SelectAllCommand_ClosingWindowCannotExecuteOrChangeSelection`：使用阻塞 `SaveSettingsAsync` 的 `BlockingSettingsSaveBoardStore` 启动真实 `window.Close()` 流程；当窗口已禁用且设置保存阻塞时，断言命令不可执行，直接执行也不改变选择或增加板/设置保存调用。释放保存并等待窗口关闭，确保测试不遗留窗口。

- [ ] **Step 4: 运行目标测试并确认红灯原因**

Run:

```powershell
& .\.tools\dotnet\dotnet.exe test tests\FloatingTransferStation.Tests\FloatingTransferStation.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SelectAllCommand_"
```

Expected: 首次实现前，`CtrlASelectsEveryCurrentItemWithoutSaving` 在 `ApplicationCommands.SelectAll.CanExecute(null, window)` 处失败，其余四项通过；基础绑定完成但尚未增加展开状态守卫时，只有收起面板测试因 `CanExecute` 实际为 `true` 而失败。

### Task 2: 为当前列表绑定 WPF 全选命令

**Files:**
- Modify: `src/FloatingTransferStation/Views/MainWindow.xaml`
- Modify: `src/FloatingTransferStation/Views/MainWindow.SelectionAndDeletion.cs`

- [ ] **Step 1: 在窗口命令绑定中复用 `ApplicationCommands.SelectAll`**

在现有 `BatchPinCommand` 绑定后增加：

```xml
<CommandBinding Command="ApplicationCommands.SelectAll"
                CanExecute="SelectAllCommand_CanExecute"
                Executed="SelectAllCommand_Executed" />
```

- [ ] **Step 2: 实现只作用于非文本焦点当前列表的命令处理**

在 `MainWindow.SelectionAndDeletion.cs` 增加：

```csharp
private void SelectAllCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
{
    if (Keyboard.FocusedElement is TextBoxBase)
    {
        return;
    }

    e.CanExecute =
        !_isClosing &&
        _viewModel.IsPanelExpanded &&
        _viewModel.ActivePanel is { Items.Count: > 0 };
    e.Handled = true;
}

private void SelectAllCommand_Executed(object sender, ExecutedRoutedEventArgs e)
{
    if (_isClosing ||
        Keyboard.FocusedElement is TextBoxBase ||
        !_viewModel.IsPanelExpanded ||
        _viewModel.ActivePanel is not { Items.Count: > 0 })
    {
        return;
    }

    BoardList.SelectAll();
    e.Handled = true;
}
```

- [ ] **Step 3: 复跑目标测试并确认绿灯**

Run:

```powershell
& .\.tools\dotnet\dotnet.exe test tests\FloatingTransferStation.Tests\FloatingTransferStation.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SelectAllCommand_"
```

Expected: 五个测试均通过；展开态选择全部内容，文本编辑器保留原生行为，空分类、收起面板和关闭中的命令不可执行且没有额外写入。

### Task 3: 锁定并更新公开操作说明

**Files:**
- Modify: `tests/FloatingTransferStation.Tests/LifecycleTests.cs`
- Modify: `README.md`
- Modify: `CHANGELOG.md`

- [ ] **Step 1: 先让生命周期测试要求新快捷键说明**

在 `PublicReleaseMaterials_DescribeInstallerLicenseRoadmapAndContribution` 增加：

```csharp
StringAssert.Contains(readme, "`Ctrl + A`：选择当前分类全部内容");
StringAssert.Contains(changelog, "`Ctrl + A` 选择当前分类全部内容");
```

- [ ] **Step 2: 运行生命周期测试并确认旧文档触发红灯**

Run:

```powershell
& .\.tools\dotnet\dotnet.exe test tests\FloatingTransferStation.Tests\FloatingTransferStation.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~PublicReleaseMaterials_DescribeInstallerLicenseRoadmapAndContribution"
```

Expected: FAIL，指出 README 或 CHANGELOG 尚不包含 `Ctrl + A` 说明。

- [ ] **Step 3: 更新 README 与 CHANGELOG**

README 的常用操作在多选说明后增加：

```markdown
- `Ctrl + A`：选择当前分类全部内容；正在编辑分类名称时仍然只会全选文字。
```

CHANGELOG 的未发布“新增”增加：

```markdown
- 支持用 `Ctrl + A` 选择当前分类全部内容，分类名称编辑框仍保留文字全选语义。
```

- [ ] **Step 4: 复跑生命周期与目标交互测试**

Run:

```powershell
& .\.tools\dotnet\dotnet.exe test tests\FloatingTransferStation.Tests\FloatingTransferStation.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SelectAllCommand_|FullyQualifiedName~PublicReleaseMaterials_DescribeInstallerLicenseRoadmapAndContribution"
```

Expected: 五个交互测试和一个生命周期测试，共六个目标测试全部通过。

### Task 4: 真实界面与完整质量门

**Files:**
- Verify only: `artifacts/select-all-verification.png`（不得提交）

- [ ] **Step 1: 运行格式验证**

```powershell
& .\.tools\dotnet\dotnet.exe format FloatingTransferStation.slnx --verify-no-changes --no-restore
```

Expected: exit code 0。

- [ ] **Step 2: 运行 Release 全量测试**

```powershell
& .\.tools\dotnet\dotnet.exe test FloatingTransferStation.slnx -c Release --no-restore
```

Expected: 0 失败、0 跳过。

- [ ] **Step 3: 运行严格 Release 构建**

```powershell
& .\.tools\dotnet\dotnet.exe build FloatingTransferStation.slnx -c Release --no-restore -warnaserror
```

Expected: 0 warnings, 0 errors。

- [ ] **Step 4: 生成并检查真实 WPF 截图**

启动真实 `MainWindow`，在隔离数据中放入至少三张卡片，执行 `ApplicationCommands.SelectAll`，把窗口渲染到 `artifacts/select-all-verification.png`。检查三张卡片都有选择反馈，标题栏数量徽标为 `3`，批量置顶按钮可见；确认截图不在 Git 提交范围。

- [ ] **Step 5: 检查差异、提交并推送**

```powershell
git diff --check
git status --short
git add README.md CHANGELOG.md docs/superpowers/specs/2026-08-29-select-all-shortcut-design.md docs/superpowers/plans/2026-08-29-select-all-shortcut.md src/FloatingTransferStation/Views/MainWindow.xaml src/FloatingTransferStation/Views/MainWindow.SelectionAndDeletion.cs tests/FloatingTransferStation.Tests/MainWindowInteractionTests.cs tests/FloatingTransferStation.Tests/LifecycleTests.cs
git commit -m "feat: select current category with Ctrl+A"
git push origin main
```

Expected: 提交仅包含计划内文件；普通推送成功且本地 `main` 与 `origin/main` 一致。

## 自检

- 设计中的当前分类范围、空分类无操作、文本编辑器原生语义、零持久化和既有选择反馈均有明确测试或验证步骤。
- XAML 事件名与 C# 方法签名一致；所有新增方法都由真实路由命令测试覆盖。
- 计划没有改变板内容、顺序、置顶分区、拖放载荷、保存或卸载契约。
- 已扫描计划：不存在 `TODO`、`TBD`、模糊错误处理或未定义 API。
