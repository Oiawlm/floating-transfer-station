# Esc 取消选择快捷键实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让用户在当前展开分类中按 `Esc` 取消全部卡片选择，同时保留文本编辑与折叠态的既有语义。

**Architecture:** 在 `MainWindow_PreviewKeyDown` 的现有窗口键盘边界中优先处理 `Esc`，满足状态守卫时递增选择取消版本并调用 `BoardList.UnselectAll()`；选择变化继续通过既有 `SelectionChanged` 链路刷新界面。单卡和批量置顶只在取消版本未变化时恢复排序过程中丢失的选择，避免慢保存覆盖更晚的取消意图；功能不引入服务、持久化或新命令类型。

**Tech Stack:** .NET 10、WPF、MSTest STA 交互测试、PowerShell 质量门

---

## 文件职责

- `tests/FloatingTransferStation.Tests/MainWindowInteractionTests.cs`：锁定真实窗口、焦点和折叠状态下的快捷键行为。
- `src/FloatingTransferStation/Views/MainWindow.SelectionAndDeletion.cs`：实现窗口级 `Esc` 选择取消守卫。
- `tests/FloatingTransferStation.Tests/LifecycleTests.cs`：锁定公开文档中的快捷键说明。
- `README.md`、`CHANGELOG.md`、`ROADMAP.md`、`PROJECT_GUIDE.md`：记录用户操作、未发布功能、最近完成项和当前稳定版本。

### Task 1: 用真实 WPF 事件定义选择取消契约

**Files:**
- Modify: `tests/FloatingTransferStation.Tests/MainWindowInteractionTests.cs`

- [ ] **Step 1: 写展开态失败测试**

```csharp
[STATestMethod]
public void Escape_ClearsExpandedSelectionWithoutSaving()
{
    using var directory = new TestDirectory();
    var board = new BoardService();
    var first = board.AddText("first");
    var second = board.AddText("second");
    var store = new RecordingBoardStore(directory.Root);
    var window = CreateWindow(board, store, WindowSettings.Default);

    try
    {
        window.Show();
        ExpandCategory(window, BoardCategory.Inbox);
        CompleteLayout(window);
        var list = (ListBox)window.FindName("BoardList");
        list.SelectedItems.Add(first);
        list.SelectedItems.Add(second);
        InvokePrivate(window, "SetHeaderActionsVisible", true);
        CompleteLayout(window);
        var shell = (Border)window.FindName("WindowShell");
        SaveVisualEvidence(shell, "before-escape.png");
        var escape = NewKeyEventArgs(window, Key.Escape);

        window.RaiseEvent(escape);
        CompleteLayout(window);
        SaveVisualEvidence(shell, "after-escape.png");

        Assert.IsTrue(escape.Handled);
        Assert.AreEqual(0, list.SelectedItems.Count);
        Assert.AreEqual(0, store.SaveCount);
        Assert.AreEqual(
            Visibility.Collapsed,
            ((Border)window.FindName("SelectedCountBadge")).Visibility);
        Assert.AreEqual(
            Visibility.Collapsed,
            ((Button)window.FindName("BatchPinButton")).Visibility);
    }
    finally
    {
        CloseWindow(window);
    }
}
```

- [ ] **Step 2: 写文本编辑、折叠、空选择和关闭态边界测试**

```csharp
[STATestMethod]
public void Escape_CategoryNameEditorFocusPreservesCardSelection()
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
        var escape = NewKeyEventArgs(window, Key.Escape);

        InvokePrivate(window, "MainWindow_PreviewKeyDown", window, escape);
        CompleteLayout(window);

        Assert.IsFalse(escape.Handled);
        CollectionAssert.AreEqual(
            new[] { selected },
            list.SelectedItems.Cast<BoardItem>().ToArray());
        Assert.AreEqual(0, store.SaveCount);
    }
    finally
    {
        CloseWindow(window);
    }
}

[STATestMethod]
public void Escape_CollapsedPanelPreservesHiddenSelection()
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
        InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
        InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
        CompleteLayout(window);
        var escape = NewKeyEventArgs(window, Key.Escape);

        window.RaiseEvent(escape);
        CompleteLayout(window);

        Assert.IsFalse(escape.Handled);
        CollectionAssert.AreEqual(
            new[] { selected },
            list.SelectedItems.Cast<BoardItem>().ToArray());
        Assert.AreEqual(0, store.SaveCount);
    }
    finally
    {
        CloseWindow(window);
    }
}

private static void SaveVisualEvidence(FrameworkElement visual, string fileName)
{
    var directory = Environment.GetEnvironmentVariable("FTS_CLEAR_SELECTION_EVIDENCE_DIR");
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

文本编辑测试先在真实编辑器上路由 `Keyboard.PreviewKeyDownEvent`，断言窗口没有吞键，再路由 `Keyboard.KeyDownEvent`，断言编辑器取消改名且卡片选择不变。另用独立测试断言空选择不吞键、关闭保存期间不改变选择或增加保存次数。

- [ ] **Step 3: 用阻塞存储复现异步选择恢复竞态**

单卡与批量置顶分别使用 `BlockingFirstSuccessfulSaveBoardStore`，在 `FirstSaveStarted` 后触发 `Esc`，释放保存并额外泵送 Dispatcher 100 ms，最终断言选择仍为 0：

```csharp
window.RaiseEvent(NewKeyEventArgs(window, Key.Escape));
Assert.AreEqual(0, list.SelectedItems.Count);
store.ReleaseFirstSave();
PumpDispatcherUntil(window.Dispatcher, store.FirstSaveCompleted.Task);
PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
CompleteLayout(window);
Assert.AreEqual(0, list.SelectedItems.Count);
```

- [ ] **Step 4: 运行测试并确认 RED**

```powershell
& .\.tools\dotnet\dotnet.exe test tests\FloatingTransferStation.Tests\FloatingTransferStation.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~Escape_"
```

预期：初始展开态测试失败在 `escape.Handled == false`；实现基础快捷键后，两个慢保存测试失败在保存完成后选择从 0 恢复为 2。测试不得因编译或测试装配错误失败。

### Task 2: 实现最小窗口快捷键逻辑

**Files:**
- Modify: `src/FloatingTransferStation/Views/MainWindow.SelectionAndDeletion.cs`

- [ ] **Step 1: 在现有预览键处理器顶部加入最小守卫**

```csharp
private long _selectionClearVersion;

if (e.Key == Key.Escape &&
    !_isClosing &&
    Keyboard.FocusedElement is not TextBoxBase &&
    _viewModel.IsPanelExpanded &&
    BoardList.SelectedItems.Count > 0)
{
    _selectionClearVersion++;
    BoardList.UnselectAll();
    e.Handled = true;
    return;
}
```

单卡和批量置顶在异步调用前捕获 `selectionClearVersion`，回到 Dispatcher 后只在版本不变时调用 `RestoreSelection(selectedBefore)`；`RestoreExplicitScrollOffset` 保持无条件执行。

- [ ] **Step 2: 运行目标测试并确认 GREEN**

```powershell
& .\.tools\dotnet\dotnet.exe test tests\FloatingTransferStation.Tests\FloatingTransferStation.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~Escape_"
```

预期：七个 `Escape_` 测试全部通过，输出没有警告或错误。

### Task 3: 更新公开契约并完成发布前验证

**Files:**
- Modify: `tests/FloatingTransferStation.Tests/LifecycleTests.cs`
- Modify: `README.md`
- Modify: `CHANGELOG.md`
- Modify: `ROADMAP.md`
- Modify: `PROJECT_GUIDE.md`

- [ ] **Step 1: 先扩展生命周期文档契约并确认 RED**

在 `PublicReleaseMaterials_DescribeInstallerLicenseRoadmapAndContribution` 的现有快捷键断言旁加入：

```csharp
StringAssert.Contains(readme, "`Esc`：取消当前分类的全部选择");
StringAssert.Contains(changelog, "`Esc` 取消当前分类全部选择");
```

运行：

```powershell
& .\.tools\dotnet\dotnet.exe test tests\FloatingTransferStation.Tests\FloatingTransferStation.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~PublicReleaseMaterials_DescribeInstallerLicenseRoadmapAndContribution"
```

预期：因 README 尚无 `Esc` 说明而失败。

- [ ] **Step 2: 写最小文档更新**

写入以下准确内容：

```markdown
README.md:
- `Esc`：取消当前分类的全部选择；正在编辑分类名称时仍然取消本次改名。

CHANGELOG.md / 未发布 / 新增:
- 支持用 `Esc` 取消当前分类全部选择，分类名称编辑框仍保留取消改名语义。

ROADMAP.md / 最近完成:
- 选择快捷键：`Ctrl + A` 选择当前分类全部内容，`Esc` 取消当前选择。

PROJECT_GUIDE.md:
当前稳定发布为 1.1.0。
```

- [ ] **Step 3: 生成真实界面证据**

运行带证据目录的目标测试：

```powershell
$env:FTS_CLEAR_SELECTION_EVIDENCE_DIR = Join-Path (Get-Location) 'artifacts\clear-selection-verification'
& .\.tools\dotnet\dotnet.exe test tests\FloatingTransferStation.Tests\FloatingTransferStation.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~Escape_ClearsExpandedSelectionWithoutSaving"
Remove-Item Env:\FTS_CLEAR_SELECTION_EVIDENCE_DIR
```

用本地图片查看工具检查 `before-escape.png` 和 `after-escape.png`：前者应有两张选中卡片、数量徽标和批量置顶按钮，后者应无这些选择反馈。目录保持被 `.gitignore` 忽略。

- [ ] **Step 4: 运行完整质量门**

```powershell
& .\.tools\dotnet\dotnet.exe format FloatingTransferStation.slnx --verify-no-changes --no-restore
& .\.tools\dotnet\dotnet.exe test FloatingTransferStation.slnx -c Release --no-restore
& .\.tools\dotnet\dotnet.exe build FloatingTransferStation.slnx -c Release --no-restore -warnaserror
git diff --check
git status --short --branch
```

预期：格式验证退出 0；Release 全量测试 0 失败；严格构建 0 warnings、0 errors；diff 检查通过；状态只包含计划内文件且不包含 `artifacts/`、`.tools/` 或 `TestResults/`。

- [ ] **Step 5: 提交并普通推送当前跟踪分支**

```powershell
git add README.md CHANGELOG.md ROADMAP.md PROJECT_GUIDE.md docs/superpowers/specs/2026-08-30-clear-selection-shortcut-design.md docs/superpowers/plans/2026-08-30-clear-selection-shortcut.md src/FloatingTransferStation/Views/MainWindow.SelectionAndDeletion.cs tests/FloatingTransferStation.Tests/MainWindowInteractionTests.cs tests/FloatingTransferStation.Tests/LifecycleTests.cs
git commit -m "feat: 支持 Esc 取消当前选择"
git pull --rebase origin main
git push origin main
```

预期：普通推送成功，`main` 与 `origin/main` 指向同一新提交；不使用 force push。

## 计划自检

- 设计中的展开态、文本编辑、折叠态、无持久化和公开文档要求均有对应测试或验证步骤。
- 文件路径、方法名和现有类型与当前代码一致。
- 计划不包含占位实现、无关重构或数据/拖放/卸载契约变化。
