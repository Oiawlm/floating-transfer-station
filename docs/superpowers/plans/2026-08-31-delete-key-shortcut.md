# Delete 键删除选择实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让标准 `Delete` 键复用现有安全删除路径删除当前选择，同时保留 `Backspace`、文本编辑和折叠/关闭边界。

**Architecture:** 在现有 `MainWindow_PreviewKeyDown` 窗口键盘边界中把删除按键集合从仅 `Key.Back` 扩为 `Key.Back` 与 `Key.Delete`；后续选择冻结、异步变更、原子保存、失败回滚和界面恢复完全复用现有路径。真实 WPF 测试以数据行覆盖两个按键，并把折叠和关闭守卫扩展到 `Delete`。

**Tech Stack:** .NET 10、WPF、MSTest STA 交互测试、PowerShell 质量门

---

## 文件职责

- `tests/FloatingTransferStation.Tests/MainWindowInteractionTests.cs`：定义真实窗口、键盘焦点、折叠/关闭下的删除快捷键行为，并按需输出界面证据。
- `src/FloatingTransferStation/Views/MainWindow.SelectionAndDeletion.cs`：把 `Delete` 纳入现有窗口删除快捷键守卫。
- `tests/FloatingTransferStation.Tests/LifecycleTests.cs`：锁定公开文档中的快捷键说明。
- `README.md`、`CHANGELOG.md`、`ROADMAP.md`：记录用户操作、未发布能力和最近完成项。

### Task 1: 用真实 WPF 事件定义 Delete 契约

**Files:**
- Modify: `tests/FloatingTransferStation.Tests/MainWindowInteractionTests.cs`

- [ ] **Step 1: 让选择删除测试覆盖两个按键并输出 Delete 前后证据**

把现有 `Backspace_DeletesSelectionButNeverClearsWithoutSelection` 改为：

```csharp
[STATestMethod]
[DataRow(Key.Back)]
[DataRow(Key.Delete)]
public void DeleteShortcut_DeletesSelectionButNeverClearsWithoutSelection(Key key)
{
    using var directory = new TestDirectory();
    var board = new BoardService();
    var keep = board.AddText("keep");
    var remove = board.AddText("remove");
    var store = new RecordingBoardStore(directory.Root);
    var window = CreateWindow(board, store, WindowSettings.Default);

    try
    {
        window.Show();
        ExpandCategory(window, BoardCategory.Inbox);
        CompleteLayout(window);
        var list = (ListBox)window.FindName("BoardList");
        list.SelectedItems.Add(remove);
        InvokePrivate(window, "SetHeaderActionsVisible", true);
        CompleteLayout(window);
        var shell = (Border)window.FindName("WindowShell");
        if (key == Key.Delete)
        {
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(250));
            CompleteLayout(window);
            SaveVisualEvidence(
                shell,
                "before-delete.png",
                "FTS_DELETE_SHORTCUT_EVIDENCE_DIR");
        }

        var deleteShortcut = NewKeyEventArgs(window, key);
        window.RaiseEvent(deleteShortcut);
        Assert.IsTrue(deleteShortcut.Handled);
        PumpDispatcherUntil(window.Dispatcher, store.SaveCompleted.Task);
        CompleteLayout(window);
        if (key == Key.Delete)
        {
            SaveVisualEvidence(
                shell,
                "after-delete.png",
                "FTS_DELETE_SHORTCUT_EVIDENCE_DIR");
        }

        CollectionAssert.AreEqual(
            new[] { keep.Id },
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
        var saves = store.SaveCount;
        var emptySelectionKey = NewKeyEventArgs(window, key);
        window.RaiseEvent(emptySelectionKey);
        CompleteLayout(window);
        Assert.IsFalse(emptySelectionKey.Handled);
        Assert.AreEqual(saves, store.SaveCount);
        Assert.AreSame(keep, board.Items(BoardCategory.Inbox).Single());
    }
    finally
    {
        CloseWindow(window);
    }
}
```

- [ ] **Step 2: 参数化文本编辑测试**

把现有文本焦点测试改为：

```csharp
[STATestMethod]
[DataRow(Key.Back)]
[DataRow(Key.Delete)]
public void DeleteShortcut_CategoryNameEditorFocusNeverDeletesSelectedCards(Key key)
{
    using var directory = new TestDirectory();
    var board = new BoardService();
    var selected = board.AddText("selected card");
    var store = new RecordingBoardStore(directory.Root);
    var window = CreateWindow(board, store, WindowSettings.Default);

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
        var deleteKey = NewKeyEventArgs(window, key);

        InvokePrivate(window, "MainWindow_PreviewKeyDown", window, deleteKey);
        CompleteLayout(window);

        Assert.IsFalse(deleteKey.Handled);
        Assert.AreSame(selected, board.Items(BoardCategory.Inbox).Single());
        Assert.AreEqual(0, store.SaveCount);
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

- [ ] **Step 3: 把折叠与关闭守卫扩展到 Delete**

给现有 `Escape_CollapsedPanelPreservesHiddenSelection` 增加 `[DataRow(Key.Escape)]`、`[DataRow(Key.Back)]` 与 `[DataRow(Key.Delete)]`，改名为 `Shortcut_CollapsedPanelPreservesHiddenSelection(Key key)`，并把事件创建改为：

```csharp
var shortcut = NewKeyEventArgs(window, key);
window.RaiseEvent(shortcut);
CompleteLayout(window);
Assert.IsFalse(shortcut.Handled);
```

给现有 `Escape_ClosingWindowDoesNotChangeSelection` 做同样参数化，改名为 `Shortcut_ClosingWindowDoesNotChangeSelection(Key key)`，并在关闭保存开始后执行相同的 `shortcut` 三行。其余选择、保存计数和关闭释放断言保持原样。

- [ ] **Step 4: 泛化截图辅助方法**

把现有辅助方法改为：

```csharp
private static void SaveVisualEvidence(
    FrameworkElement visual,
    string fileName,
    string environmentVariable = "FTS_CLEAR_SELECTION_EVIDENCE_DIR")
{
    var directory = Environment.GetEnvironmentVariable(environmentVariable);
    if (string.IsNullOrWhiteSpace(directory))
    {
        return;
    }

    Directory.CreateDirectory(directory);
    var bitmap = new RenderTargetBitmap(
        (int)Math.Ceiling(visual.ActualWidth),
        (int)Math.Ceiling(visual.ActualHeight),
        96,
        96,
        PixelFormats.Pbgra32);
    bitmap.Render(visual);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = File.Create(Path.Combine(directory, fileName));
    encoder.Save(stream);
}
```

- [ ] **Step 5: 运行相关测试并确认 RED**

```powershell
& .\.tools\dotnet\dotnet.exe test tests\FloatingTransferStation.Tests\FloatingTransferStation.Tests.csproj -c Release --no-restore --filter "Name~DeleteShortcut|Name~Shortcut_CollapsedPanel|Name~Shortcut_ClosingWindow"
```

预期：`Key.Back` 与 `Key.Delete` 的折叠态数据行暴露现有 `ActivePanel` 在收起后仍非空的缺口；`Key.Delete` 选择删除数据行另失败于现有产品代码没有处理该按键。其他数据行通过，不得因编译或测试装配错误失败。

### Task 2: 实现最小 Delete 键路由

**Files:**
- Modify: `src/FloatingTransferStation/Views/MainWindow.SelectionAndDeletion.cs`

- [ ] **Step 1: 扩展现有按键条件**

把删除分支开头改为：

```csharp
if ((e.Key != Key.Back && e.Key != Key.Delete) ||
    _isClosing ||
    Keyboard.FocusedElement is TextBoxBase ||
    !_viewModel.IsPanelExpanded ||
    _viewModel.ActivePanel is not { } activePanel)
{
    return;
}
```

不改变后续选择捕获、`e.Handled` 或 `DeleteSelectedItemsAsync`。

- [ ] **Step 2: 运行相关测试并确认 GREEN**

```powershell
& .\.tools\dotnet\dotnet.exe test tests\FloatingTransferStation.Tests\FloatingTransferStation.Tests.csproj -c Release --no-restore --filter "Name~DeleteShortcut|Name~Shortcut_CollapsedPanel|Name~Shortcut_ClosingWindow"
```

预期：10 个数据行全部通过，0 失败。

### Task 3: 锁定公开契约并更新文档

**Files:**
- Modify: `tests/FloatingTransferStation.Tests/LifecycleTests.cs`
- Modify: `README.md`
- Modify: `CHANGELOG.md`
- Modify: `ROADMAP.md`

- [ ] **Step 1: 先添加失败的文档契约**

在 `PublicReleaseMaterials_DescribeInstallerLicenseRoadmapAndContribution` 中加入：

```csharp
StringAssert.Contains(readme, "`Delete` 或 `Backspace`：只删除选中项");
StringAssert.Contains(changelog, "`Delete` 键删除当前选择");
StringAssert.Contains(roadmap, "`Delete` 删除当前选择");
```

- [ ] **Step 2: 运行生命周期测试并确认 RED**

```powershell
& .\.tools\dotnet\dotnet.exe test tests\FloatingTransferStation.Tests\FloatingTransferStation.Tests.csproj -c Release --no-restore --filter "Name=PublicReleaseMaterials_DescribeInstallerLicenseRoadmapAndContribution"
```

预期：失败于 README 尚未包含 `` `Delete` 或 `Backspace`：只删除选中项``。

- [ ] **Step 3: 更新公开文档**

README 把现有 `Backspace` 操作说明改为 `` `Delete` 或 `Backspace`：只删除选中项；正在编辑文字时仍然正常删字。``。CHANGELOG 的“未发布”下增加“新增”小节，记录“支持用 `Delete` 键删除当前选择；`Backspace` 保持兼容，分类名称编辑框仍保留原生文字删除语义。”ROADMAP 的“最近完成”增加“删除快捷键：`Delete` 删除当前选择，`Backspace` 保持兼容。”

- [ ] **Step 4: 运行生命周期测试并确认 GREEN**

```powershell
& .\.tools\dotnet\dotnet.exe test tests\FloatingTransferStation.Tests\FloatingTransferStation.Tests.csproj -c Release --no-restore --filter "Name=PublicReleaseMaterials_DescribeInstallerLicenseRoadmapAndContribution"
```

预期：测试通过，0 失败。

### Task 4: 生成界面证据并完成质量门

**Files:**
- Verify only: `artifacts/delete-shortcut-verification/before-delete.png`
- Verify only: `artifacts/delete-shortcut-verification/after-delete.png`

- [ ] **Step 1: 生成真实 WPF 前后截图**

```powershell
$env:FTS_DELETE_SHORTCUT_EVIDENCE_DIR = (Join-Path $PWD 'artifacts\delete-shortcut-verification')
& .\.tools\dotnet\dotnet.exe test tests\FloatingTransferStation.Tests\FloatingTransferStation.Tests.csproj -c Release --no-restore --filter "Name=DeleteShortcut_DeletesSelectionButNeverClearsWithoutSelection"
Remove-Item Env:FTS_DELETE_SHORTCUT_EVIDENCE_DIR
```

预期：两个数据行通过；目录内生成 `before-delete.png` 与 `after-delete.png`，且 `git status --short` 不显示这些被忽略的文件。

- [ ] **Step 2: 检查截图**

用本地图片查看工具检查两张 PNG：前图应同时显示保留卡片和具有选择反馈的待删卡片，后图应只显示保留卡片且选择反馈消失。截图不得纳入 Git。

- [ ] **Step 3: 运行完整质量门**

```powershell
& .\.tools\dotnet\dotnet.exe format FloatingTransferStation.slnx --verify-no-changes --no-restore
& .\.tools\dotnet\dotnet.exe test FloatingTransferStation.slnx -c Release --no-restore
& .\.tools\dotnet\dotnet.exe build FloatingTransferStation.slnx -c Release --no-restore -warnaserror
git diff --check
```

预期：格式验证退出 0；Release 全量测试 0 失败；严格构建 0 warnings、0 errors；`git diff --check` 退出 0。

- [ ] **Step 4: 复核范围**

```powershell
git status --short
git diff --stat
git diff -- src/FloatingTransferStation/Views/MainWindow.SelectionAndDeletion.cs tests/FloatingTransferStation.Tests/MainWindowInteractionTests.cs tests/FloatingTransferStation.Tests/LifecycleTests.cs README.md CHANGELOG.md ROADMAP.md
```

预期：只有设计、计划、产品一行守卫、相关测试与公开文档变化；不存在 `.tools/`、`artifacts/`、`TestResults/`、用户数据、凭据或本机截图。

## 计划自审

- 规格覆盖：选择删除、空选择、文本焦点、折叠、关闭、共享失败恢复、公开文档和真实截图均有对应验证。
- 占位扫描：计划没有待填内容、模糊的错误处理要求或未定义命令。
- 类型一致性：测试和产品均使用现有 `Key`、`MainWindow_PreviewKeyDown`、`DeleteSelectedItemsAsync`、`RecordingBoardStore` 与 `SaveVisualEvidence`；新增辅助参数是可选字符串，现有调用保持兼容。
