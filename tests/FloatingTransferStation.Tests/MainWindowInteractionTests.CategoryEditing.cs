using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;

namespace FloatingTransferStation.Tests;

public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public void CategoryTab_NameEditingKeepsDefaultMarkerAboveTheEditor()
    {
        using var directory = new TestDirectory();
        var state = new DefaultCaptureCategoryState();
        var window = CreateWindow(directory, new BoardService(), state);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var customer = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);
            var customerTab = FindCategoryTab(window, customer);

            customer.BeginNameEdit();
            CompleteLayout(window);

            Assert.IsTrue(customer.IsEditingName);
            Assert.IsTrue(FindDescendants<TextBox>(customerTab).Single().IsVisible);
            var markerStyle = (Style)window.FindResource("CategoryDefaultMarkerStyle");
            var marker = FindDescendants<TextBlock>(customerTab)
                .Single(candidate => ReferenceEquals(candidate.Style, markerStyle));
            Assert.AreEqual("◎", marker.Text);
            Assert.AreEqual(customer.IsDefaultCapture ? 1d : 0d, marker.Opacity);
            Assert.IsTrue(
                Panel.GetZIndex(marker) > Panel.GetZIndex(FindDescendants<TextBox>(customerTab).Single()));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CategoryNameSave_UpdatesOnlySettingsAndTheSharedDisplayName()
    {
        using var directory = new TestDirectory();
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(
            new BoardService(),
            store,
            WindowSettings.Default,
            new DefaultCaptureCategoryState());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.CustomerOriginal);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var customer = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);

            InvokePrivateTask(window, "SaveCategoryNameAsync", customer, "客户");

            Assert.AreEqual("客户", customer.DisplayName);
            Assert.AreEqual("客户", viewModel.ActivePanel!.DisplayName);
            Assert.AreEqual("客户", store.LastSavedSettings!.CategoryName(
                BoardCategory.CustomerOriginal));
            Assert.IsNull(store.LastPersistedSnapshot);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CategoryNameSaveFailure_RestoresNameAndKeepsDefaultMarker()
    {
        using var directory = new TestDirectory();
        var store = new RecordingBoardStore(directory.Root)
        {
            SettingsSaveFailure = new IOException("Injected settings failure.")
        };
        var state = new DefaultCaptureCategoryState();
        state.Set(BoardCategory.CustomerOriginal);
        var window = CreateWindow(new BoardService(), store, WindowSettings.Default, state);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.CustomerOriginal);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var customer = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);

            InvokePrivateTask(window, "SaveCategoryNameAsync", customer, "客户");

            Assert.AreEqual("客户原图", customer.DisplayName);
            Assert.AreEqual("分类名称未保存，已恢复原名称。", viewModel.StatusText);
            Assert.IsTrue(customer.IsDefaultCapture);
            Assert.AreEqual(BoardCategory.CustomerOriginal, state.Current);
            store.SettingsSaveFailure = null;
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void CategoryNameEditing_PointerLeaveDoesNotCollapseOrSaveDraft()
    {
        using var directory = new TestDirectory();
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(
            new BoardService(),
            store,
            WindowSettings.Default,
            new DefaultCaptureCategoryState());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.CustomerOriginal);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var customer = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);
            var customerTab = FindCategoryTab(window, customer);

            customer.BeginNameEdit();
            customer.DraftName = "T";
            CompleteLayout(window);
            var editor = FindDescendants<TextBox>(customerTab).Single();
            Assert.IsTrue(editor.Focus());

            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            var collapseTimer = GetPrivateField<DispatcherTimer>(window, "_collapseTimer");
            Assert.IsFalse(collapseTimer.IsEnabled);
            InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
            CompleteLayout(window);

            Assert.IsTrue(viewModel.IsPanelExpanded);
            Assert.IsTrue(customer.IsEditingName);
            Assert.AreEqual("客户原图", customer.DisplayName);
            Assert.AreEqual("T", customer.DraftName);
            Assert.IsNull(store.LastSavedSettings);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CategoryNameEditor_DefersSourceUpdatesUntilCommitForImeComposition()
    {
        using var directory = new TestDirectory();
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(
            new BoardService(),
            store,
            WindowSettings.Default,
            new DefaultCaptureCategoryState());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.CustomerOriginal);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var customer = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);
            var customerTab = FindCategoryTab(window, customer);

            customer.BeginNameEdit();
            CompleteLayout(window);
            var editor = FindDescendants<TextBox>(customerTab).Single();
            var binding = BindingOperations.GetBinding(editor, TextBox.TextProperty);
            Assert.IsNotNull(binding);
            Assert.AreEqual(UpdateSourceTrigger.Explicit, binding.UpdateSourceTrigger);

            editor.Text = "这是";
            editor.CaretIndex = editor.Text.Length;
            CompleteLayout(window);

            Assert.AreEqual("客户原图", customer.DraftName);
            Assert.AreEqual(editor.Text.Length, editor.CaretIndex);

            InvokePrivate(window, "CommitCategoryNameEdit", customer, editor.Text);
            InvokePrivateTask(window, "DrainPendingOperationsAsync");

            Assert.AreEqual("这是", customer.DisplayName);
            Assert.AreEqual(
                "这是",
                store.LastSavedSettings!.CategoryName(BoardCategory.CustomerOriginal));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CategoryNameEditor_DoesNotTruncateActiveImeCompositionText()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.CustomerOriginal);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var customer = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);
            var customerTab = FindCategoryTab(window, customer);

            customer.BeginNameEdit();
            CompleteLayout(window);
            var editor = FindDescendants<TextBox>(customerTab).Single();
            editor.RaiseEvent(NewTextCompositionEventArgs(
                TextCompositionManager.PreviewTextInputStartEvent,
                editor,
                "zheshide"));

            editor.Text = "zheshide";
            Assert.AreEqual("zheshide", editor.Text);

            editor.Text = "这是的";
            editor.CaretIndex = editor.Text.Length;
            editor.RaiseEvent(NewTextCompositionEventArgs(
                TextCompositionManager.PreviewTextInputEvent,
                editor,
                "这是的"));
            CompleteLayout(window);

            Assert.AreEqual("这是的", editor.Text);
            Assert.AreEqual(editor.Text.Length, editor.CaretIndex);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CategoryNameEditor_EnterDuringImeCompositionDoesNotSaveTheLabel()
    {
        using var directory = new TestDirectory();
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(
            new BoardService(),
            store,
            WindowSettings.Default,
            new DefaultCaptureCategoryState());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.CustomerOriginal);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var customer = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);
            var customerTab = FindCategoryTab(window, customer);

            customer.BeginNameEdit();
            CompleteLayout(window);
            var editor = FindDescendants<TextBox>(customerTab).Single();
            editor.RaiseEvent(NewTextCompositionEventArgs(
                TextCompositionManager.PreviewTextInputStartEvent,
                editor,
                "zhe"));
            editor.Text = "zhe";
            var source = PresentationSource.FromVisual(editor);
            Assert.IsNotNull(source);
            var enter = new KeyEventArgs(
                Keyboard.PrimaryDevice,
                source,
                0,
                Key.Enter)
            {
                RoutedEvent = Keyboard.KeyDownEvent,
                Source = editor
            };

            InvokePrivate(window, "CategoryNameEditor_KeyDown", editor, enter);

            Assert.IsFalse(enter.Handled);
            Assert.IsTrue(customer.IsEditingName);
            Assert.AreEqual("客户原图", customer.DisplayName);
            Assert.IsNull(store.LastSavedSettings);
        }
        finally
        {
            CloseWindow(window);
        }
    }

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
    [DataRow(ModifierKeys.Control)]
    [DataRow(ModifierKeys.Shift)]
    [DataRow(ModifierKeys.Alt)]
    public void F2Rename_ModifiedShortcutIsIgnored(ModifierKeys modifiers)
    {
        using var directory = new TestDirectory();
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(new BoardService(), store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            Keyboard.ClearFocus();
            var rename = NewModifiedKeyEventArgs(window, Key.F2, modifiers);

            window.RaiseEvent(rename);
            CompleteLayout(window);

            var viewModel = (MainWindowViewModel)window.DataContext;
            Assert.IsFalse(rename.Handled);
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
            rename.Source = editor;

            editor.RaiseEvent(rename);
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
    public void F2Rename_DoubleClickKeepsFocusedSelectedEditorBehavior()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var active = viewModel.ActivePanel!;
            var tab = FindCategoryTab(window, active);
            var doubleClick = NewMouseButtonEventArgs(
                UIElement.MouseLeftButtonDownEvent,
                tab);
            typeof(MouseButtonEventArgs)
                .GetField("_count", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(doubleClick, 2);

            tab.RaiseEvent(doubleClick);
            CompleteLayout(window);

            var editor = FindDescendants<TextBox>(tab).Single();
            Assert.IsTrue(doubleClick.Handled);
            Assert.IsTrue(active.IsEditingName);
            Assert.IsTrue(editor.IsKeyboardFocusWithin);
            Assert.AreEqual(0, editor.SelectionStart);
            Assert.AreEqual(active.DisplayName.Length, editor.SelectionLength);
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
}
