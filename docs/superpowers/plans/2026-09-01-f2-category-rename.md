# F2 Category Rename Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让用户在面板展开时按 `F2` 进入当前分类的现有改名编辑器，同时保留双击、IME、保存与失败回滚契约。

**Architecture:** 在现有窗口 `PreviewKeyDown` 边界识别 `F2`，通过活动分类找到对应标签，再调用与双击入口共享的改名辅助方法。辅助方法只进入编辑态并异步聚焦、全选；现有 `CategoryNameEditor_*` 和 `SaveCategoryNameAsync` 继续独占输入、提交与持久化责任。

**Tech Stack:** .NET 10、C#、WPF、MSTest STA、PowerShell、Git

---

## File map

- Create `docs/superpowers/specs/2026-09-01-f2-category-rename-design.md`: 已确认的用户结果、边界与验证契约。
- Create `docs/superpowers/plans/2026-09-01-f2-category-rename.md`: 本实施计划与 RED/GREEN 证据记录。
- Modify `src/FloatingTransferStation/Views/MainWindow.VisualTransitions.cs`: 抽取双击与快捷键共享的开始改名、聚焦和全选路径。
- Modify `src/FloatingTransferStation/Views/MainWindow.SelectionAndDeletion.cs`: 在现有窗口键盘入口增加受守卫的 `F2` 分支。
- Modify `tests/FloatingTransferStation.Tests/MainWindowInteractionTests.cs`: 覆盖成功入口、折叠/编辑/关闭边界，并生成真实 UI 证据。
- Modify `tests/FloatingTransferStation.Tests/LifecycleTests.cs`: 锁定公开文档的快捷键说明。
- Modify `README.md`, `CHANGELOG.md`, `ROADMAP.md`: 记录用户可见行为和完成状态。

### Task 1: Lock the keyboard behavior with failing WPF tests

**Files:**
- Modify: `tests/FloatingTransferStation.Tests/MainWindowInteractionTests.cs`

- [ ] **Step 1: Add the expanded-panel failing test**

在现有分类改名测试旁新增真实 STA 测试。测试创建一个已选卡片，把默认接收分类设为非活动分类，展开 `Inbox` 后派发 `F2`：

```csharp
[STATestMethod]
public void F2Rename_ExpandedPanelEditsOnlyTheActiveCategoryWithoutSaving()
{
    using var directory = new TestDirectory();
    var board = new BoardService();
    var selected = board.AddText("selected");
    var store = new RecordingBoardStore(directory.Root);
    var defaultCategory = new DefaultCaptureCategoryState();
    defaultCategory.Set(BoardCategory.Reference);
    var window = CreateWindow(board, store, WindowSettings.Default, defaultCategory);

    try
    {
        window.Show();
        ExpandCategory(window, BoardCategory.Inbox);
        CompleteLayout(window);
        var viewModel = (MainWindowViewModel)window.DataContext;
        var active = viewModel.Categories.Single(
            category => category.Category == BoardCategory.Inbox);
        var list = (ListBox)window.FindName("BoardList");
        list.SelectedItems.Add(selected);
        Keyboard.ClearFocus();

        var rename = NewKeyEventArgs(window, Key.F2);
        window.RaiseEvent(rename);
        CompleteLayout(window);

        var editor = FindDescendants<TextBox>(FindCategoryTab(window, active)).Single();
        Assert.IsTrue(rename.Handled);
        Assert.IsTrue(active.IsEditingName);
        Assert.IsTrue(editor.IsKeyboardFocusWithin);
        Assert.AreEqual(0, editor.SelectionStart);
        Assert.AreEqual(active.DisplayName.Length, editor.SelectionLength);
        CollectionAssert.AreEqual(
            new[] { selected },
            list.SelectedItems.Cast<BoardItem>().ToArray());
        Assert.AreEqual(BoardCategory.Reference, defaultCategory.Current);
        Assert.AreEqual(0, store.SaveCount);
        Assert.IsNull(store.LastSavedSettings);

        SaveVisualEvidence(
            (Border)window.FindName("WindowShell"),
            "f2-category-rename.png",
            "FTS_F2_RENAME_EVIDENCE_DIR");
    }
    finally
    {
        CloseWindow(window);
    }
}
```

- [ ] **Step 2: Run the test and record RED**

Run:

```powershell
& .\.tools\dotnet\dotnet.exe test tests\FloatingTransferStation.Tests\FloatingTransferStation.Tests.csproj -c Release --no-restore --filter "Name~F2Rename_ExpandedPanelEditsOnlyTheActiveCategoryWithoutSaving" --logger "console;verbosity=minimal"
```

Expected: FAIL because `rename.Handled` is false and no category enters edit mode.

- [ ] **Step 3: Add edge-condition tests before production code**

Add these three tests:

```csharp
[STATestMethod]
public void F2Rename_CollapsedPanelIsIgnored()
{
    using var directory = new TestDirectory();
    var store = new RecordingBoardStore(directory.Root);
    var window = CreateWindow(new BoardService(), store, WindowSettings.Default);

    try
    {
        window.Show();
        CompleteLayout(window);
        Keyboard.ClearFocus();
        var rename = NewKeyEventArgs(window, Key.F2);

        window.RaiseEvent(rename);
        CompleteLayout(window);

        var viewModel = (MainWindowViewModel)window.DataContext;
        Assert.IsFalse(rename.Handled);
        Assert.IsFalse(viewModel.IsPanelExpanded);
        Assert.IsFalse(viewModel.Categories.Any(category => category.IsEditingName));
        Assert.IsNull(store.LastSavedSettings);
    }
    finally
    {
        CloseWindow(window);
    }
}

[STATestMethod]
public void F2Rename_ExistingTextEditorKeepsItsDraft()
{
    using var directory = new TestDirectory();
    var store = new RecordingBoardStore(directory.Root);
    var window = CreateWindow(new BoardService(), store, WindowSettings.Default);

    try
    {
        window.Show();
        ExpandCategory(window, BoardCategory.Inbox);
        CompleteLayout(window);
        var viewModel = (MainWindowViewModel)window.DataContext;
        var active = viewModel.ActivePanel!;
        active.BeginNameEdit();
        active.DraftName = "草稿";
        CompleteLayout(window);
        var editor = FindDescendants<TextBox>(FindCategoryTab(window, active)).Single();
        Assert.IsTrue(editor.Focus());
        Keyboard.Focus(editor);
        var rename = NewKeyEventArgs(window, Key.F2);

        InvokePrivate(window, "MainWindow_PreviewKeyDown", window, rename);
        CompleteLayout(window);

        Assert.IsFalse(rename.Handled);
        Assert.IsTrue(active.IsEditingName);
        Assert.AreEqual("草稿", active.DraftName);
        Assert.IsTrue(editor.IsKeyboardFocusWithin);
        Assert.IsNull(store.LastSavedSettings);
    }
    finally
    {
        CloseWindow(window);
    }
}

[STATestMethod]
public void F2Rename_ClosingWindowIsIgnored()
{
    using var directory = new TestDirectory();
    var store = new BlockingSettingsSaveBoardStore(directory.Root);
    var window = CreateWindow(new BoardService(), store, WindowSettings.Default);
    var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    window.Closed += (_, _) => closed.TrySetResult();
    var closeStarted = false;

    try
    {
        window.Show();
        ExpandCategory(window, BoardCategory.Inbox);
        CompleteLayout(window);
        Keyboard.ClearFocus();
        window.Close();
        closeStarted = true;
        PumpDispatcherUntil(window.Dispatcher, store.SettingsSaveStarted.Task);
        var rename = NewKeyEventArgs(window, Key.F2);

        InvokePrivate(window, "MainWindow_PreviewKeyDown", window, rename);

        var viewModel = (MainWindowViewModel)window.DataContext;
        Assert.IsFalse(rename.Handled);
        Assert.IsFalse(viewModel.Categories.Any(category => category.IsEditingName));
    }
    finally
    {
        if (closeStarted)
        {
            store.ReleaseSettingsSave();
            if (!closed.Task.IsCompleted)
            {
                PumpDispatcherUntil(window.Dispatcher, closed.Task);
            }
        }
        else
        {
            CloseWindow(window);
        }
    }
}
```

- [ ] **Step 4: Run the whole F2 test group and keep it RED**

Run:

```powershell
& .\.tools\dotnet\dotnet.exe test tests\FloatingTransferStation.Tests\FloatingTransferStation.Tests.csproj -c Release --no-restore --filter "Name~F2Rename_" --logger "console;verbosity=minimal"
```

Expected: boundary tests pass under existing guards or setup, while the expanded-panel test fails because `F2` has no implementation.

### Task 2: Implement the shared rename entry point

**Files:**
- Modify: `src/FloatingTransferStation/Views/MainWindow.VisualTransitions.cs:133-160`
- Modify: `src/FloatingTransferStation/Views/MainWindow.SelectionAndDeletion.cs:294-325`
- Test: `tests/FloatingTransferStation.Tests/MainWindowInteractionTests.cs`

- [ ] **Step 1: Extract the existing double-click body**

Change `CategoryTab_MouseLeftButtonDown` to pattern-match `tab` and call this helper:

```csharp
private void BeginCategoryNameEdit(CategoryViewModel category, Border tab)
{
    _viewModel.BeginCategoryNameEdit(category);
    Dispatcher.BeginInvoke(
        DispatcherPriority.Input,
        new Action(() =>
        {
            if (!category.IsEditingName ||
                FindDescendant<TextBox>(tab) is not { } editor)
            {
                return;
            }

            editor.Focus();
            editor.SelectAll();
        }));
}
```

The mouse handler keeps its current close/button/click-count/editor guards, calls `BeginCategoryNameEdit(category, tab)`, and sets `e.Handled = true`.

- [ ] **Step 2: Add the minimal F2 branch before selection/delete handling**

At the start of `MainWindow_PreviewKeyDown`, add:

```csharp
if (e.Key == Key.F2 &&
    e.KeyboardDevice.Modifiers == ModifierKeys.None &&
    !_isClosing &&
    Keyboard.FocusedElement is not TextBoxBase &&
    _viewModel.IsPanelExpanded &&
    !IsCategoryNameEditActive() &&
    _viewModel.ActivePanel is { } activePanel)
{
    var activeTab = CategoryTabs().SingleOrDefault(candidate =>
        ReferenceEquals(candidate.DataContext, activePanel));
    if (activeTab is not null)
    {
        BeginCategoryNameEdit(activePanel, activeTab);
        e.Handled = true;
        return;
    }
}
```

Do not change the existing `Escape`, `Backspace`, or `Delete` paths.

- [ ] **Step 3: Run F2 and existing rename interaction tests**

Run:

```powershell
& .\.tools\dotnet\dotnet.exe test tests\FloatingTransferStation.Tests\FloatingTransferStation.Tests.csproj -c Release --no-restore --filter "Name~F2Rename_|Name~CategoryName|Name~CategoryTab_NameEditing" --logger "console;verbosity=minimal"
```

Expected: all selected tests PASS, with zero product saves merely from entering edit mode.

### Task 3: Lock and update the public contract

**Files:**
- Modify: `tests/FloatingTransferStation.Tests/LifecycleTests.cs:222-254`
- Modify: `README.md`
- Modify: `CHANGELOG.md`
- Modify: `ROADMAP.md`

- [ ] **Step 1: Make the lifecycle contract fail first**

Add exact assertions to the existing public-material test before editing docs:

```csharp
StringAssert.Contains(readme, "`F2`：改名当前展开分类");
StringAssert.Contains(changelogSections["未发布"], "`F2` 改名当前展开分类");
StringAssert.Contains(roadmap, "`F2` 改名当前展开分类");
```

- [ ] **Step 2: Run the lifecycle test and record RED**

Run:

```powershell
& .\.tools\dotnet\dotnet.exe test tests\FloatingTransferStation.Tests\FloatingTransferStation.Tests.csproj -c Release --no-restore --filter "Name~PublicReleaseMaterials_DescribeInstallerLicenseRoadmapAndContribution" --logger "console;verbosity=minimal"
```

Expected: FAIL because the three public documents do not yet describe `F2`.

- [ ] **Step 3: Add the smallest accurate documentation updates**

- In `README.md`, update the category rename operation so double-click and `F2` are both discoverable, including the exact fragment `` `F2`：改名当前展开分类 ``.
- In `CHANGELOG.md` under `## 未发布`, add `### 新增` and a bullet stating that `` `F2` 改名当前展开分类 `` while the existing double-click and editor behavior remain unchanged.
- In `ROADMAP.md` under `## 最近完成`, add a bullet containing `` `F2` 改名当前展开分类 ``.

- [ ] **Step 4: Re-run the lifecycle contract**

Run the command from Step 2.

Expected: PASS.

### Task 4: Generate visual evidence and run completion gates

**Files:**
- Verify: `artifacts/f2-category-rename/f2-category-rename.png`
- Verify all modified files

- [ ] **Step 1: Generate ignored real WPF evidence**

Run:

```powershell
$f2Evidence = Join-Path $PWD 'artifacts\f2-category-rename'
$env:FTS_F2_RENAME_EVIDENCE_DIR = $f2Evidence
try {
    & .\.tools\dotnet\dotnet.exe test tests\FloatingTransferStation.Tests\FloatingTransferStation.Tests.csproj -c Release --no-restore --filter "Name~F2Rename_ExpandedPanelEditsOnlyTheActiveCategoryWithoutSaving" --logger "console;verbosity=minimal"
} finally {
    Remove-Item Env:FTS_F2_RENAME_EVIDENCE_DIR -ErrorAction SilentlyContinue
}
```

Expected: PASS and one PNG showing the active category name editor visibly focused with its text selected. Confirm `git check-ignore` reports the PNG as ignored and visually inspect it.

- [ ] **Step 2: Run all required local gates**

Run, stopping on the first non-zero exit:

```powershell
& .\.tools\dotnet\dotnet.exe format FloatingTransferStation.slnx --verify-no-changes --no-restore
& .\.tools\dotnet\dotnet.exe test FloatingTransferStation.slnx -c Release --no-restore --logger "console;verbosity=minimal"
& .\.tools\dotnet\dotnet.exe build FloatingTransferStation.slnx -c Release --no-restore -warnaserror --verbosity minimal
git diff --check
```

Expected: format exits 0, every Release test passes, strict build reports 0 warnings/0 errors, and `git diff --check` exits 0.

- [ ] **Step 3: Review the actual diff and repository hygiene**

Verify only the two design/plan files, two product files, two test files, and three public documents are changed. Confirm `.tools/`, `artifacts/`, `TestResults/`, user data, credentials, and screenshots are absent from tracked changes.

### Task 5: Commit, push, and verify the remote result

**Files:**
- Commit only the scoped files from Tasks 1-4

- [ ] **Step 1: Refresh the remote and ensure it has not advanced**

Run `git fetch --prune origin`, then require `git rev-list --left-right --count HEAD...origin/main` to report `0 0` before staging.

- [ ] **Step 2: Stage the scoped file list and validate it**

Use an explicit `git add --` list for the nine scoped files, then run `git diff --cached --check`, `git diff --cached --stat`, and `git diff --cached --name-only`.

- [ ] **Step 3: Create one verified feature commit**

Run:

```powershell
git commit -m "feat: 支持 F2 改名当前分类"
```

Expected: one commit containing only the verified feature, tests, design/plan, and public docs.

- [ ] **Step 4: Push without force and verify CI**

Run `git push origin main`, fetch again, and require local `HEAD` to equal `origin/main`. Find the GitHub Actions quality-gate run for the new commit and wait for a successful final conclusion; do not treat push success as CI success.

- [ ] **Step 5: Update automation memory**

Append the recovery evidence, chosen feature, RED/GREEN results, visual evidence path, complete local gate counts, commit SHA, push/CI state, clean final Git state, and current run time to `C:\Users\Lenovo\.codex\automations\automation\memory.md`.
