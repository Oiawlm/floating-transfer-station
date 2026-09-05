using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public void HeaderDeleteWithoutSelection_RemovesOnlyTheActiveCategoryWithoutConfirmation()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("提示词一", BoardCategory.Prompt);
        board.AddText("提示词二", BoardCategory.Prompt);
        var inbox = board.AddText("待分类保留");
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Prompt);
            CompleteLayout(window);
            var delete = window.FindName("DeleteContentButton") as Button;
            Assert.IsNotNull(delete);
            Assert.IsTrue(delete.IsEnabled);

            delete.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            CompleteLayout(window);

            Assert.AreEqual(0, board.Items(BoardCategory.Prompt).Count);
            Assert.AreSame(inbox, board.Items(BoardCategory.Inbox).Single());
            Assert.AreEqual(1, store.SaveCount);
            Assert.IsFalse(store.LastPersistedSnapshot!.Items.Any(
                item => item.Category == BoardCategory.Prompt));
            Assert.IsFalse(delete.IsEnabled);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void SelectedCountBadge_UsesStableCircleSizeForSingleAndMultipleSelection()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var first = board.AddText("first");
        var second = board.AddText("second");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            var list = (ListBox)window.FindName("BoardList");
            var badge = (Border?)window.FindName("SelectedCountBadge");
            var count = (TextBlock?)window.FindName("SelectedCountText");
            Assert.IsNotNull(badge);
            Assert.IsNotNull(count);

            list.SelectedItems.Add(first);
            CompleteLayout(window);
            Assert.AreEqual(14d, badge.Width);
            Assert.AreEqual(14d, badge.Height);
            Assert.AreEqual("1", count.Text);
            var singleSize = new Size(badge.ActualWidth, badge.ActualHeight);

            list.SelectedItems.Add(second);
            CompleteLayout(window);
            Assert.AreEqual(singleSize.Width, badge.ActualWidth, 0.01);
            Assert.AreEqual(singleSize.Height, badge.ActualHeight, 0.01);
            Assert.AreEqual("2", count.Text);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void HeaderDeleteFailureRestoresMixedSelectionAndSuccessDeletesOnlySelection()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var keep = board.AddText("keep");
        var selectedNormal = board.AddText("selected normal");
        var selectedPinned = board.AddText("selected pinned");
        board.SetPinnedMany([selectedPinned.Id], true);
        var store = new RecordingBoardStore(directory.Root)
        {
            SaveFailure = new IOException("Injected failure.")
        };
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(selectedPinned);
            list.SelectedItems.Add(selectedNormal);
            CompleteLayout(window);
            var delete = (Button?)window.FindName("DeleteContentButton");
            var badge = (Border?)window.FindName("SelectedCountBadge");
            var count = (TextBlock?)window.FindName("SelectedCountText");
            Assert.IsNotNull(delete);
            Assert.IsNotNull(badge);
            Assert.IsNotNull(count);
            Assert.AreEqual(Visibility.Visible, badge.Visibility);
            Assert.AreEqual("2", count.Text);
            Assert.IsFalse(badge.IsHitTestVisible);

            delete.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, delete));
            CompleteLayout(window);

            CollectionAssert.AreEqual(
                new[] { selectedPinned.Id, selectedNormal.Id, keep.Id },
                board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
            Assert.IsTrue(selectedPinned.IsPinned);
            CollectionAssert.AreEquivalent(
                new[] { selectedPinned, selectedNormal },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            Assert.AreEqual("2", count.Text);
            store.SaveFailure = null;
            delete.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, delete));
            PumpDispatcherUntil(window.Dispatcher, store.SaveCompleted.Task);
            CompleteLayout(window);

            CollectionAssert.AreEqual(
                new[] { keep.Id },
                board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
            Assert.AreEqual(0, list.SelectedItems.Count);
            Assert.AreEqual(Visibility.Collapsed, badge.Visibility);
            Assert.AreEqual(1, store.SaveCount);
        }
        finally
        {
            store.SaveFailure = null;
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void HeaderDeleteWithoutSelectionFailureRestoresCategoryAndNewItemStaysUnselected()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var second = board.AddText("second");
        var first = board.AddText("first");
        var store = new RecordingBoardStore(directory.Root)
        {
            SaveFailure = new IOException("Injected failure.")
        };
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var delete = (Button?)window.FindName("DeleteContentButton");
            Assert.IsNotNull(delete);
            delete.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, delete));
            CompleteLayout(window);

            CollectionAssert.AreEqual(
                new[] { first.Id, second.Id },
                board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
            Assert.AreEqual(0, list.SelectedItems.Count);
            var captured = board.AddText("new capture");
            CompleteLayout(window);
            Assert.IsFalse(list.SelectedItems.Contains(captured));
            Assert.AreEqual(0, list.SelectedItems.Count);
        }
        finally
        {
            store.SaveFailure = null;
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void HeaderClear_SlowSaveAfterCategorySwitchPreservesCurrentSelectionAndDeleteScope()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("待分类中正在清空的内容");
        var selected = board.AddText("资料分类中已选的内容", BoardCategory.Reference);
        var keep = board.AddText("资料分类中保留的内容", BoardCategory.Reference);
        var store = new BlockingFirstSuccessfulSaveBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(window.Dispatcher));

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var delete = (Button)window.FindName("DeleteContentButton");

            delete.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, delete));
            PumpDispatcherUntil(window.Dispatcher, store.FirstSaveStarted.Task);
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            list.SelectedItems.Add(selected);
            InvokePrivate(window, "SetHeaderActionsVisible", true);
            CompleteLayout(window);

            CollectionAssert.AreEqual(
                new[] { selected },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            Assert.AreEqual("删除已选 1 项", delete.ToolTip);
            var shell = (Border)window.FindName("WindowShell");
            SaveVisualEvidence(
                shell,
                "clear-category-before-save-completes.png",
                "FTS_CATEGORY_SWITCH_EVIDENCE_DIR");

            store.ReleaseFirstSave();
            PumpDispatcherUntil(window.Dispatcher, store.FirstSaveCompleted.Task);
            CompleteLayout(window);
            SaveVisualEvidence(
                shell,
                "clear-category-after-save-completes.png",
                "FTS_CATEGORY_SWITCH_EVIDENCE_DIR");

            Assert.AreEqual(
                BoardCategory.Reference,
                ((MainWindowViewModel)window.DataContext).ActivePanel!.Category);
            CollectionAssert.AreEqual(
                new[] { selected },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            Assert.AreEqual("删除已选 1 项", delete.ToolTip);
            Assert.AreEqual("删除已选 1 项", AutomationProperties.GetName(delete));
            Assert.AreEqual(0, board.Items(BoardCategory.Inbox).Count);
            CollectionAssert.AreEquivalent(
                new[] { selected, keep },
                board.Items(BoardCategory.Reference).ToArray());
        }
        finally
        {
            store.ReleaseFirstSave();
            try
            {
                CloseWindow(window);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }
    }

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
            Assert.IsTrue(panel.IsEditingName);
            var previewEscape = NewKeyEventArgs(window, Key.Escape);
            previewEscape.Source = editor;

            editor.RaiseEvent(previewEscape);
            CompleteLayout(window);

            Assert.IsFalse(previewEscape.Handled);
            CollectionAssert.AreEqual(
                new[] { selected },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            Assert.AreEqual(0, store.SaveCount);

            var escape = new KeyEventArgs(
                Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(editor)!,
                Environment.TickCount,
                Key.Escape)
            {
                RoutedEvent = Keyboard.KeyDownEvent,
                Source = editor
            };

            editor.RaiseEvent(escape);
            CompleteLayout(window);

            Assert.IsTrue(escape.Handled);
            Assert.IsFalse(panel.IsEditingName);
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
    [DataRow(Key.Escape)]
    [DataRow(Key.Back)]
    [DataRow(Key.Delete)]
    public void Shortcut_CollapsedPanelPreservesHiddenSelection(Key key)
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
            var shortcut = NewKeyEventArgs(window, key);

            window.RaiseEvent(shortcut);
            CompleteLayout(window);

            Assert.IsFalse(shortcut.Handled);
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
    public void Escape_EmptySelectionIsNotHandledOrSaved()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("card");
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var escape = NewKeyEventArgs(window, Key.Escape);

            window.RaiseEvent(escape);
            CompleteLayout(window);

            Assert.IsFalse(escape.Handled);
            Assert.AreEqual(0, list.SelectedItems.Count);
            Assert.AreEqual(0, store.SaveCount);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    [DataRow(Key.Escape)]
    [DataRow(Key.Back)]
    [DataRow(Key.Delete)]
    public void Shortcut_ClosingWindowDoesNotChangeSelection(Key key)
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var selected = board.AddText("selected");
        var store = new BlockingSettingsSaveBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        var closeStarted = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(selected);
            Keyboard.ClearFocus();

            window.Close();
            closeStarted = true;
            PumpDispatcherUntil(window.Dispatcher, store.SettingsSaveStarted.Task);
            var boardSaveCount = store.BoardSaveCount;
            var settingsSaveCount = store.SettingsSaveCount;
            var shortcut = NewKeyEventArgs(window, key);

            window.RaiseEvent(shortcut);
            CompleteLayout(window);

            Assert.IsFalse(shortcut.Handled);
            CollectionAssert.AreEqual(
                new[] { selected },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            Assert.AreEqual(boardSaveCount, store.BoardSaveCount);
            Assert.AreEqual(settingsSaveCount, store.SettingsSaveCount);
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

    [STATestMethod]
    public void Escape_DuringSlowBatchPinIsNotUndoneWhenSaveCompletes()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var first = board.AddText("first");
        var second = board.AddText("second");
        var store = new BlockingFirstSuccessfulSaveBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(first);
            list.SelectedItems.Add(second);
            var batchPin = (Button)window.FindName("BatchPinButton");

            batchPin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, batchPin));
            PumpDispatcherUntil(window.Dispatcher, store.FirstSaveStarted.Task);
            var escape = NewKeyEventArgs(window, Key.Escape);
            window.RaiseEvent(escape);
            CompleteLayout(window);

            Assert.IsTrue(escape.Handled);
            Assert.AreEqual(0, list.SelectedItems.Count);

            store.ReleaseFirstSave();
            PumpDispatcherUntil(window.Dispatcher, store.FirstSaveCompleted.Task);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
            CompleteLayout(window);

            Assert.AreEqual(0, list.SelectedItems.Count);
            Assert.AreEqual(
                Visibility.Collapsed,
                ((Border)window.FindName("SelectedCountBadge")).Visibility);
            Assert.AreEqual(Visibility.Collapsed, batchPin.Visibility);
        }
        finally
        {
            store.ReleaseFirstSave();
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Escape_DuringSlowSinglePinIsNotUndoneWhenSaveCompletes()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var selectedOther = board.AddText("selected other");
        var clicked = board.AddText("clicked");
        var store = new BlockingFirstSuccessfulSaveBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(clicked);
            list.SelectedItems.Add(selectedOther);
            var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(clicked);
            Assert.IsNotNull(container);
            var pin = FindDescendants<Button>(container)
                .Single(button => Equals(button.CommandParameter, "TogglePin"));

            pin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, pin));
            PumpDispatcherUntil(window.Dispatcher, store.FirstSaveStarted.Task);
            var escape = NewKeyEventArgs(window, Key.Escape);
            window.RaiseEvent(escape);
            CompleteLayout(window);

            Assert.IsTrue(escape.Handled);
            Assert.AreEqual(0, list.SelectedItems.Count);

            store.ReleaseFirstSave();
            PumpDispatcherUntil(window.Dispatcher, store.FirstSaveCompleted.Task);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
            CompleteLayout(window);

            Assert.AreEqual(0, list.SelectedItems.Count);
            Assert.AreEqual(
                Visibility.Collapsed,
                ((Border)window.FindName("SelectedCountBadge")).Visibility);
            Assert.AreEqual(
                Visibility.Collapsed,
                ((Button)window.FindName("BatchPinButton")).Visibility);
        }
        finally
        {
            store.ReleaseFirstSave();
            CloseWindow(window);
        }
    }

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

    [STATestMethod]
    public void SelectAllCommand_EmptyCategoryCannotExecuteOrSave()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("reference", BoardCategory.Reference);
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            Keyboard.ClearFocus();

            Assert.IsFalse(ApplicationCommands.SelectAll.CanExecute(null, window));

            ApplicationCommands.SelectAll.Execute(null, window);
            CompleteLayout(window);

            Assert.AreEqual(0, list.SelectedItems.Count);
            Assert.AreEqual(0, store.SaveCount);
            Assert.IsNull(store.LastSavedSettings);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void SelectAllCommand_CollapsedPanelCannotExecuteOrSelectHiddenItems()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var selected = board.AddText("first");
        board.AddText("second");
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var viewModel = (MainWindowViewModel)window.DataContext;
            list.SelectedItems.Add(selected);
            Assert.IsTrue(viewModel.IsPanelExpanded);
            CollectionAssert.AreEqual(
                new[] { selected },
                list.SelectedItems.Cast<BoardItem>().ToArray());

            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
            CompleteLayout(window);

            Assert.IsFalse(viewModel.IsPanelExpanded);
            CollectionAssert.AreEqual(
                new[] { selected },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            Keyboard.ClearFocus();
            Assert.IsFalse(ApplicationCommands.SelectAll.CanExecute(null, window));

            ApplicationCommands.SelectAll.Execute(null, window);
            CompleteLayout(window);

            CollectionAssert.AreEqual(
                new[] { selected },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            Assert.AreEqual(0, store.SaveCount);
            Assert.IsNull(store.LastSavedSettings);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void SelectAllCommand_ClosingWindowCannotExecuteOrChangeSelection()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var selected = board.AddText("selected");
        board.AddText("not selected");
        var store = new BlockingSettingsSaveBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        var closeStarted = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(selected);
            Keyboard.ClearFocus();

            window.Close();
            closeStarted = true;
            PumpDispatcherUntil(window.Dispatcher, store.SettingsSaveStarted.Task);

            Assert.IsFalse(window.IsEnabled);
            Assert.IsFalse(ApplicationCommands.SelectAll.CanExecute(null, window));
            var boardSaveCount = store.BoardSaveCount;
            var settingsSaveCount = store.SettingsSaveCount;

            ApplicationCommands.SelectAll.Execute(null, window);
            CompleteLayout(window);

            CollectionAssert.AreEqual(
                new[] { selected },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            Assert.AreEqual(boardSaveCount, store.BoardSaveCount);
            Assert.AreEqual(settingsSaveCount, store.SettingsSaveCount);
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

    [STATestMethod]
    public void SelectionSurvivesAutoCollapseButClearsOnUserCategorySwitch()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var second = board.AddText("second");
        var first = board.AddText("first");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(first);
            list.SelectedItems.Add(second);

            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
            Assert.AreEqual(2, list.SelectedItems.Count);
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            Assert.AreEqual(2, list.SelectedItems.Count);

            EnterCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            Assert.AreEqual(0, list.SelectedItems.Count);
            Assert.AreEqual(
                BoardCategory.Reference,
                ((MainWindowViewModel)window.DataContext).ActivePanel!.Category);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void SelectionGestureRequiresControlAndNoDragThreshold()
    {
        var method = typeof(MainWindow).GetMethod(
            "ShouldToggleSelection",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(method);
        Assert.AreEqual(true, method.Invoke(null, [ModifierKeys.Control, false]));
        Assert.AreEqual(false, method.Invoke(null, [ModifierKeys.None, false]));
        Assert.AreEqual(false, method.Invoke(null, [ModifierKeys.Control, true]));
    }

    [STATestMethod]
    public void SelectionButtonTogglesPersistentTwoSignalFeedback()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("select me");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(item);
            Assert.IsNotNull(container);
            var button = FindDescendants<Button>(container)
                .Single(candidate => Equals(candidate.CommandParameter, "ToggleSelection"));

            Assert.AreEqual(0d, button.Opacity);
            Assert.IsFalse(button.IsHitTestVisible);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            CompleteLayout(window);

            Assert.IsTrue(container.IsSelected);
            Assert.AreEqual(1d, button.Opacity);
            Assert.IsTrue(button.IsHitTestVisible);
            var card = FindDescendants<Border>(container)
                .Single(candidate => ReferenceEquals(
                    candidate.Style,
                    window.FindResource("CardContainerStyle")));
            Assert.AreSame(window.FindResource("SelectedCardBorderBrush"), card.BorderBrush);
            Assert.AreSame(window.FindResource("SelectedCardBrush"), card.Background);

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            Assert.IsFalse(container.IsSelected);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void EmptyListBackgroundClearsSelectionWithoutChangingPanelOrScroll()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        AddScrollableItems(board, BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            Assert.AreEqual(SelectionMode.Multiple, list.SelectionMode);
            list.SelectedItems.Add(list.Items[0]);
            list.SelectedItems.Add(list.Items[1]);
            var viewer = FindDescendant<ScrollViewer>(list);
            Assert.IsNotNull(viewer);
            ScrollTo(window, viewer, 120);
            var before = viewer.VerticalOffset;

            InvokePrivate(
                window,
                "BoardList_PreviewMouseLeftButtonDown",
                list,
                NewMouseButtonEventArgs(Mouse.PreviewMouseDownEvent, list));

            Assert.AreEqual(0, list.SelectedItems.Count);
            Assert.AreEqual(
                BoardCategory.Inbox,
                ((MainWindowViewModel)window.DataContext).ActivePanel!.Category);
            Assert.AreEqual(before, viewer.VerticalOffset, 0.5);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void BatchPinButton_TracksSelectionAndAdvertisesOneDeterministicAction()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var normal = board.AddText("normal");
        var pinned = board.AddText("pinned");
        board.SetPinnedMany([pinned.Id], true);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var batchPin = (Button?)window.FindName("BatchPinButton");
            Assert.IsNotNull(batchPin);
            Assert.AreEqual(Visibility.Collapsed, batchPin.Visibility);

            list.SelectedItems.Add(pinned);
            list.SelectedItems.Add(normal);
            CompleteLayout(window);

            Assert.AreEqual(Visibility.Visible, batchPin.Visibility);
            Assert.AreEqual("置顶已选 2 项", batchPin.ToolTip);
            Assert.AreEqual("置顶已选 2 项", AutomationProperties.GetName(batchPin));

            list.SelectedItems.Remove(normal);
            CompleteLayout(window);

            Assert.AreEqual("取消置顶已选 1 项", batchPin.ToolTip);
            Assert.AreEqual("取消置顶已选 1 项", AutomationProperties.GetName(batchPin));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void BatchPinCommand_CtrlPInvokesTheCurrentSelection()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var selectedBottom = board.AddText("selected bottom");
        var selectedTop = board.AddText("selected top");
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(selectedBottom);
            list.SelectedItems.Add(selectedTop);
            var batchPin = (Button?)window.FindName("BatchPinButton");
            Assert.IsNotNull(batchPin);
            Assert.AreEqual("Ctrl+P", AutomationProperties.GetAccessKey(batchPin));
            var commandProperty = typeof(MainWindow).GetProperty(
                "BatchPinCommand",
                BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(commandProperty);
            var command = commandProperty.GetValue(null) as RoutedUICommand;
            Assert.IsNotNull(command);
            Assert.IsTrue(command.InputGestures.OfType<KeyGesture>().Any(
                gesture => gesture.Key == Key.P &&
                           gesture.Modifiers == ModifierKeys.Control));
            Assert.IsTrue(command.CanExecute(null, window));

            command.Execute(null, window);
            PumpDispatcherUntil(window.Dispatcher, store.SaveCompleted.Task);
            CompleteLayout(window);

            Assert.IsTrue(selectedTop.IsPinned);
            Assert.IsTrue(selectedBottom.IsPinned);
            Assert.AreEqual(1, store.SaveCount);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void BatchPinCommand_CollapsedPanelDoesNotChangeRetainedSelection()
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
            InvokePrivate(window, "SetHeaderActionsVisible", true);
            CompleteLayout(window);
            var shell = (Border)window.FindName("WindowShell");
            SaveVisualEvidence(
                shell,
                "before-collapse.png",
                "FTS_BATCH_PIN_GUARD_EVIDENCE_DIR");
            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            Assert.IsFalse(viewModel.IsPanelExpanded);
            CollectionAssert.AreEqual(
                new[] { selected },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            var canExecute = MainWindow.BatchPinCommand.CanExecute(null, window);
            MainWindow.BatchPinCommand.Execute(null, window);
            CompleteLayout(window);
            ExpandCategory(window, BoardCategory.Inbox);
            InvokePrivate(window, "SetHeaderActionsVisible", true);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(250));
            CompleteLayout(window);
            SaveVisualEvidence(
                shell,
                "after-reexpand.png",
                "FTS_BATCH_PIN_GUARD_EVIDENCE_DIR");

            Assert.IsFalse(canExecute);
            Assert.IsTrue(viewModel.IsPanelExpanded);
            Assert.IsFalse(selected.IsPinned);
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
    public void BatchPinCommand_CategoryNameEditorFocusDoesNotChangeSelection()
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
            Assert.IsTrue(editor.IsKeyboardFocusWithin);

            var canExecute = MainWindow.BatchPinCommand.CanExecute(null, editor);
            MainWindow.BatchPinCommand.Execute(null, editor);
            CompleteLayout(window);

            Assert.IsFalse(canExecute);
            Assert.IsTrue(panel.IsEditingName);
            Assert.IsFalse(selected.IsPinned);
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
    [DataRow(false)]
    [DataRow(true)]
    public void BatchPinExecutionGuard_InvalidContextDoesNotChangeSelection(bool isEditingName)
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
            if (isEditingName)
            {
                var panel = viewModel.Categories.Single(
                    category => category.Category == BoardCategory.Inbox);
                viewModel.BeginCategoryNameEdit(panel);
                CompleteLayout(window);
                var editor = FindDescendants<TextBox>(FindCategoryTab(window, panel)).Single();
                Assert.IsTrue(editor.Focus());
                Keyboard.Focus(editor);
                Assert.IsTrue(editor.IsKeyboardFocusWithin);
            }
            else
            {
                InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
                InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
                CompleteLayout(window);
                Assert.IsFalse(viewModel.IsPanelExpanded);
            }

            InvokePrivateTask(window, "ApplyBatchPinSelectionAsync");
            CompleteLayout(window);

            Assert.IsFalse(selected.IsPinned);
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
    public void BatchPinButton_MixedSelectionPinsAllInSourceOrderAndPreservesSelectionAndScroll()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var keep = board.AddText("keep");
        var selectedBottom = board.AddText("selected bottom");
        var selectedTop = board.AddText("selected top");
        var existingPin = board.AddText("existing pin");
        board.SetPinnedMany([existingPin.Id], true);
        AddScrollableItems(board, BoardCategory.Inbox);
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var viewer = FindDescendant<ScrollViewer>(list);
            Assert.IsNotNull(viewer);
            ScrollTo(window, viewer, 120);
            var offset = viewer.VerticalOffset;
            list.SelectedItems.Add(existingPin);
            list.SelectedItems.Add(selectedBottom);
            list.SelectedItems.Add(selectedTop);
            var batchPin = (Button?)window.FindName("BatchPinButton");
            Assert.IsNotNull(batchPin);

            batchPin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, batchPin));
            PumpDispatcherUntil(window.Dispatcher, store.SaveCompleted.Task);
            CompleteLayout(window);

            Assert.IsTrue(existingPin.IsPinned);
            Assert.IsTrue(selectedTop.IsPinned);
            Assert.IsTrue(selectedBottom.IsPinned);
            Assert.IsFalse(keep.IsPinned);
            CollectionAssert.AreEqual(
                new[] { existingPin, selectedTop, selectedBottom },
                board.Items(BoardCategory.Inbox).Take(3).ToArray());
            Assert.AreEqual(1, store.SaveCount);
            CollectionAssert.AreEquivalent(
                new[] { existingPin, selectedTop, selectedBottom },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            Assert.AreEqual(offset, viewer.VerticalOffset, 0.5);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void BatchPinButton_AllPinnedSelectionUnpinsAsOneSourceOrderedBlock()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var normal = board.AddText("normal");
        var selectedLower = board.AddText("selected lower");
        var selectedUpper = board.AddText("selected upper");
        var keepPinned = board.AddText("keep pinned");
        board.SetPinnedMany([keepPinned.Id, selectedUpper.Id, selectedLower.Id], true);
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(selectedLower);
            list.SelectedItems.Add(selectedUpper);
            var batchPin = (Button?)window.FindName("BatchPinButton");
            Assert.IsNotNull(batchPin);

            batchPin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, batchPin));
            PumpDispatcherUntil(window.Dispatcher, store.SaveCompleted.Task);
            CompleteLayout(window);

            CollectionAssert.AreEqual(
                new[] { keepPinned, selectedUpper, selectedLower, normal },
                board.Items(BoardCategory.Inbox).ToArray());
            Assert.IsTrue(keepPinned.IsPinned);
            Assert.IsFalse(selectedUpper.IsPinned);
            Assert.IsFalse(selectedLower.IsPinned);
            CollectionAssert.AreEquivalent(
                new[] { selectedUpper, selectedLower },
                list.SelectedItems.Cast<BoardItem>().ToArray());
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void BatchPinButton_SlowSaveKeepsSelectionAndDeleteScope()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var selectedOther = board.AddText("selected other");
        var selectedTop = board.AddText("selected top");
        var store = new BlockingFirstSuccessfulSaveBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(selectedTop);
            list.SelectedItems.Add(selectedOther);
            var batchPin = (Button?)window.FindName("BatchPinButton");
            Assert.IsNotNull(batchPin);

            batchPin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, batchPin));
            PumpDispatcherUntil(window.Dispatcher, store.FirstSaveStarted.Task);
            CompleteLayout(window);

            CollectionAssert.AreEquivalent(
                new[] { selectedTop, selectedOther },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            Assert.AreEqual(
                Visibility.Visible,
                ((Border)window.FindName("SelectedCountBadge")).Visibility);
            Assert.AreEqual("2", ((TextBlock)window.FindName("SelectedCountText")).Text);
            Assert.AreEqual(
                "删除已选 2 项",
                ((Button)window.FindName("DeleteContentButton")).ToolTip);
            Assert.AreEqual(Visibility.Visible, batchPin.Visibility);
            Assert.IsFalse(batchPin.IsEnabled);
            Assert.AreEqual("正在保存 2 项置顶状态", batchPin.ToolTip);
            Assert.AreEqual(
                "正在保存 2 项置顶状态",
                AutomationProperties.GetName(batchPin));
            var buttonReenabled = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            batchPin.IsEnabledChanged += (_, _) =>
            {
                if (batchPin.IsEnabled)
                {
                    buttonReenabled.TrySetResult();
                }
            };

            store.ReleaseFirstSave();
            PumpDispatcherUntil(
                window.Dispatcher,
                Task.WhenAll(store.FirstSaveCompleted.Task, buttonReenabled.Task));
            CompleteLayout(window);

            CollectionAssert.AreEquivalent(
                new[] { selectedTop, selectedOther },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            Assert.IsTrue(batchPin.IsEnabled);
            Assert.AreEqual("取消置顶已选 2 项", batchPin.ToolTip);
        }
        finally
        {
            store.ReleaseFirstSave();
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void BatchPinButton_SaveFailureRestoresStateOrderSelectionAndScroll()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        AddScrollableItems(board, BoardCategory.Inbox);
        var selectedTop = board.Items(BoardCategory.Inbox)[3];
        var selectedBottom = board.Items(BoardCategory.Inbox)[5];
        var before = board.Items(BoardCategory.Inbox).ToArray();
        var beforePinStates = before.Select(item => item.IsPinned).ToArray();
        var store = new RecordingBoardStore(directory.Root)
        {
            SaveFailure = new IOException("Injected failure.")
        };
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var viewer = FindDescendant<ScrollViewer>(list);
            Assert.IsNotNull(viewer);
            ScrollTo(window, viewer, 120);
            var offset = viewer.VerticalOffset;
            list.SelectedItems.Add(selectedTop);
            list.SelectedItems.Add(selectedBottom);
            var batchPin = (Button?)window.FindName("BatchPinButton");
            Assert.IsNotNull(batchPin);

            batchPin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, batchPin));
            CompleteLayout(window);

            CollectionAssert.AreEqual(before, board.Items(BoardCategory.Inbox).ToArray());
            CollectionAssert.AreEqual(
                beforePinStates,
                before.Select(item => item.IsPinned).ToArray());
            CollectionAssert.AreEquivalent(
                new[] { selectedTop, selectedBottom },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            Assert.AreEqual(offset, viewer.VerticalOffset, 0.5);
            Assert.AreEqual(
                "置顶状态未保存，内容已恢复。",
                ((MainWindowViewModel)window.DataContext).StatusText);
        }
        finally
        {
            store.SaveFailure = null;
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void PinButton_TogglesOnlyClickedItemAndPreservesSelectionAndScroll()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        AddScrollableItems(board, BoardCategory.Inbox);
        var clicked = board.Items(BoardCategory.Inbox)[3];
        var other = board.Items(BoardCategory.Inbox)[4];
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var viewer = FindDescendant<ScrollViewer>(list);
            Assert.IsNotNull(viewer);
            ScrollTo(window, viewer, 120);
            var offset = viewer.VerticalOffset;
            list.SelectedItems.Add(clicked);
            list.SelectedItems.Add(other);
            var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(clicked);
            Assert.IsNotNull(container);
            var pin = FindDescendants<Button>(container)
                .Single(button => Equals(button.CommandParameter, "TogglePin"));

            pin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, pin));
            PumpDispatcherUntil(window.Dispatcher, store.SaveCompleted.Task);
            CompleteLayout(window);

            Assert.IsTrue(clicked.IsPinned);
            Assert.IsFalse(other.IsPinned);
            Assert.AreSame(clicked, board.Items(BoardCategory.Inbox)[0]);
            CollectionAssert.AreEquivalent(
                new[] { clicked, other },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            Assert.AreEqual(offset, viewer.VerticalOffset, 0.5);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void PinButton_SlowSaveKeepsSelectionAndHeaderDeleteScope()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var unselected = board.AddText("unselected");
        var selectedOther = board.AddText("selected other");
        var clicked = board.AddText("clicked");
        var store = new BlockingFirstSuccessfulSaveBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(clicked);
            list.SelectedItems.Add(selectedOther);
            var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(clicked);
            Assert.IsNotNull(container);
            var pin = FindDescendants<Button>(container)
                .Single(button => Equals(button.CommandParameter, "TogglePin"));

            pin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, pin));
            PumpDispatcherUntil(window.Dispatcher, store.FirstSaveStarted.Task);
            CompleteLayout(window);

            CollectionAssert.AreEquivalent(
                new[] { clicked, selectedOther },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            Assert.AreEqual(
                Visibility.Visible,
                ((Border)window.FindName("SelectedCountBadge")).Visibility);
            Assert.AreEqual("2", ((TextBlock)window.FindName("SelectedCountText")).Text);
            var delete = (Button)window.FindName("DeleteContentButton");
            Assert.AreEqual("删除已选 2 项", delete.ToolTip);

            store.ReleaseFirstSave();
            PumpDispatcherUntil(window.Dispatcher, store.FirstSaveCompleted.Task);
            CompleteLayout(window);

            CollectionAssert.AreEquivalent(
                new[] { clicked, selectedOther },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            CollectionAssert.AreEquivalent(
                new[] { clicked, selectedOther, unselected },
                board.Items(BoardCategory.Inbox).ToArray());
        }
        finally
        {
            store.ReleaseFirstSave();
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void PinButton_SlowSaveAfterCategorySwitchPreservesCurrentSelectionAndDeleteScope()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var clicked = board.AddText("待分类中正在置顶的内容");
        var selected = board.AddText("资料分类中已选的内容", BoardCategory.Reference);
        var keep = board.AddText("资料分类中保留的内容", BoardCategory.Reference);
        var store = new BlockingFirstSuccessfulSaveBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(window.Dispatcher));

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(clicked);
            var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(clicked);
            Assert.IsNotNull(container);
            var pin = FindDescendants<Button>(container)
                .Single(button => Equals(button.CommandParameter, "TogglePin"));

            pin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, pin));
            PumpDispatcherUntil(window.Dispatcher, store.FirstSaveStarted.Task);
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            list.SelectedItems.Add(selected);
            InvokePrivate(window, "SetHeaderActionsVisible", true);
            CompleteLayout(window);
            var delete = (Button)window.FindName("DeleteContentButton");

            CollectionAssert.AreEqual(
                new[] { selected },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            Assert.AreEqual("删除已选 1 项", delete.ToolTip);
            var shell = (Border)window.FindName("WindowShell");
            SaveVisualEvidence(
                shell,
                "single-pin-before-save-completes.png",
                "FTS_CATEGORY_SWITCH_EVIDENCE_DIR");

            store.ReleaseFirstSave();
            PumpDispatcherUntil(window.Dispatcher, store.FirstSaveCompleted.Task);
            CompleteLayout(window);
            SaveVisualEvidence(
                shell,
                "single-pin-after-save-completes.png",
                "FTS_CATEGORY_SWITCH_EVIDENCE_DIR");

            Assert.AreEqual(
                BoardCategory.Reference,
                ((MainWindowViewModel)window.DataContext).ActivePanel!.Category);
            CollectionAssert.AreEqual(
                new[] { selected },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            Assert.AreEqual("删除已选 1 项", delete.ToolTip);
            Assert.AreEqual("删除已选 1 项", AutomationProperties.GetName(delete));
            Assert.IsTrue(clicked.IsPinned);
            CollectionAssert.AreEquivalent(
                new[] { selected, keep },
                board.Items(BoardCategory.Reference).ToArray());
        }
        finally
        {
            store.ReleaseFirstSave();
            try
            {
                CloseWindow(window);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }
    }

    [STATestMethod]
    public void PinButton_SaveFailureRestoresStateOrderSelectionAndScroll()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        AddScrollableItems(board, BoardCategory.Inbox);
        var clicked = board.Items(BoardCategory.Inbox)[3];
        var other = board.Items(BoardCategory.Inbox)[4];
        var before = board.Items(BoardCategory.Inbox).ToArray();
        var store = new RecordingBoardStore(directory.Root)
        {
            SaveFailure = new IOException("Injected failure.")
        };
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var viewer = FindDescendant<ScrollViewer>(list);
            Assert.IsNotNull(viewer);
            ScrollTo(window, viewer, 120);
            var offset = viewer.VerticalOffset;
            list.SelectedItems.Add(clicked);
            list.SelectedItems.Add(other);
            var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(clicked);
            Assert.IsNotNull(container);
            var pin = FindDescendants<Button>(container)
                .Single(button => Equals(button.CommandParameter, "TogglePin"));

            pin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, pin));
            CompleteLayout(window);

            CollectionAssert.AreEqual(before, board.Items(BoardCategory.Inbox).ToArray());
            Assert.IsFalse(clicked.IsPinned);
            CollectionAssert.AreEquivalent(
                new[] { clicked, other },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            Assert.AreEqual(offset, viewer.VerticalOffset, 0.5);
            Assert.AreEqual(
                "置顶状态未保存，内容已恢复。",
                ((MainWindowViewModel)window.DataContext).StatusText);
        }
        finally
        {
            store.SaveFailure = null;
            CloseWindow(window);
        }
    }
}
