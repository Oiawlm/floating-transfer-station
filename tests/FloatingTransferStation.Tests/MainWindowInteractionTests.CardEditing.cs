using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 卡片内容就地编辑(用户补充方向切片 1)的回归:双击进入、Enter 提交、Esc 取消、
/// 空白取消、保存失败还原、图片卡与搜索态不进入编辑。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    private static MouseButtonEventArgs NewDoubleClickArgs(object source) =>
        new(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Control.MouseDoubleClickEvent,
            Source = source
        };

    private static ListBoxItem ContainerOf(MainWindow window, BoardItem item)
    {
        var list = (ListBox)window.FindName("BoardList");
        var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(item);
        Assert.IsNotNull(container);
        return container;
    }

    private static Border EditorHost(MainWindow window) =>
        (Border)window.FindName("CardTextEditorHost");

    private static TextBox EditorInput(MainWindow window) =>
        (TextBox)window.FindName("CardTextEditor");

    [STATestMethod]
    public void CardEditing_DoubleClickEnterCommitsAndPersists()
    {
        using var directory = new TestDirectory();
        var store = new RecordingCardEditStore(directory.Root);
        var board = new BoardService();
        var item = board.AddText("原始内容", BoardCategory.Inbox);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            var list = (ListBox)window.FindName("BoardList");

            // MouseDoubleClick 是直接路由事件:提升到挂接处理的 BoardList,Source 指向卡片容器。
            list.RaiseEvent(NewDoubleClickArgs(container));
            CompleteLayout(window);

            Assert.AreEqual(Visibility.Visible, EditorHost(window).Visibility);
            Assert.AreEqual("原始内容", EditorInput(window).Text);
            Assert.IsTrue(EditorInput(window).IsKeyboardFocused);

            EditorInput(window).Text = "编辑后的内容";
            EditorInput(window).RaiseEvent(new KeyEventArgs(
                Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window)!,
                Environment.TickCount,
                Key.Enter)
            {
                RoutedEvent = UIElement.PreviewKeyDownEvent,
                Source = EditorInput(window)
            });
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            CompleteLayout(window);

            Assert.AreEqual("编辑后的内容", board.FindItem(item.Id)!.Text);
            Assert.AreEqual(1, store.SaveCount);
            Assert.AreEqual(Visibility.Collapsed, EditorHost(window).Visibility);
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "card-edit-committed.png",
                "FTS_CARD_EDIT_EVIDENCE_DIR");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardEditing_EscapeCancelsWithoutSaving()
    {
        using var directory = new TestDirectory();
        var store = new RecordingCardEditStore(directory.Root);
        var board = new BoardService();
        var item = board.AddText("保持不变", BoardCategory.Inbox);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            var list = (ListBox)window.FindName("BoardList");

            // MouseDoubleClick 是直接路由事件:提升到挂接处理的 BoardList,Source 指向卡片容器。
            list.RaiseEvent(NewDoubleClickArgs(container));
            EditorInput(window).Text = "不应保存的修改";
            EditorInput(window).RaiseEvent(new KeyEventArgs(
                Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window)!,
                Environment.TickCount,
                Key.Escape)
            {
                RoutedEvent = UIElement.PreviewKeyDownEvent,
                Source = EditorInput(window)
            });
            CompleteLayout(window);

            Assert.AreEqual("保持不变", board.FindItem(item.Id)!.Text);
            Assert.AreEqual(0, store.SaveCount);
            Assert.AreEqual(Visibility.Collapsed, EditorHost(window).Visibility);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardEditing_WhitespaceCommitCancelsWithStatus()
    {
        using var directory = new TestDirectory();
        var store = new RecordingCardEditStore(directory.Root);
        var board = new BoardService();
        var item = board.AddText("内容还在", BoardCategory.Inbox);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            var list = (ListBox)window.FindName("BoardList");

            // MouseDoubleClick 是直接路由事件:提升到挂接处理的 BoardList,Source 指向卡片容器。
            list.RaiseEvent(NewDoubleClickArgs(container));
            EditorInput(window).Text = "   ";
            window.CommitCardTextForTest();
            CompleteLayout(window);

            Assert.AreEqual("内容还在", board.FindItem(item.Id)!.Text, "空白编辑必须取消而非清空内容。");
            Assert.AreEqual(0, store.SaveCount);
            Assert.AreEqual(
                "内容不能为空，本次编辑已取消。",
                ((MainWindowViewModel)window.DataContext).StatusText);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardEditing_SaveFailureRestoresOriginalText()
    {
        using var directory = new TestDirectory();
        var store = new RecordingCardEditStore(directory.Root) { FailSave = true };
        var board = new BoardService();
        var item = board.AddText("失败回滚文本", BoardCategory.Inbox);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            var list = (ListBox)window.FindName("BoardList");

            // MouseDoubleClick 是直接路由事件:提升到挂接处理的 BoardList,Source 指向卡片容器。
            list.RaiseEvent(NewDoubleClickArgs(container));
            EditorInput(window).Text = "保存会失败的修改";
            window.CommitCardTextForTest();
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            CompleteLayout(window);

            Assert.AreEqual("失败回滚文本", board.FindItem(item.Id)!.Text, "保存失败必须还原原文本。");

            // 窗口关闭序列也会保存:放开失败让 CloseWindow 走完。
            store.FailSave = false;
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardEditing_ImageCardAndSearchActiveDoNotOpenEditor()
    {
        using var directory = new TestDirectory();
        var store = new RecordingCardEditStore(directory.Root);
        var board = new BoardService();
        var imagePath = Path.Combine(directory.Root, "card-edit.png");
        WritePng(imagePath, width: 24, height: 12);
        var image = board.AddImage(Guid.NewGuid(), "images/card-edit.png", imagePath, BoardCategory.Inbox);
        var text = board.AddText("文字卡", BoardCategory.Inbox);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            var list = (ListBox)window.FindName("BoardList");
            list.RaiseEvent(NewDoubleClickArgs(ContainerOf(window, image)));
            CompleteLayout(window);
            Assert.AreEqual(Visibility.Collapsed, EditorHost(window).Visibility, "图片卡不进入文本编辑。");

            window.EnterSearchMode();
            CompleteLayout(window);
            list.RaiseEvent(NewDoubleClickArgs(ContainerOf(window, text)));
            CompleteLayout(window);
            Assert.AreEqual(Visibility.Collapsed, EditorHost(window).Visibility, "搜索态不进入编辑。");
            window.ExitSearchMode();
        }
        finally
        {
            CloseWindow(window);
        }
    }

    private sealed class RecordingCardEditStore : IBoardStore
    {
        public RecordingCardEditStore(string root) => ImagesDirectory = Path.Combine(root, "images");

        public bool FailSave { get; set; }
        public int SaveCount { get; private set; }
        public int SaveAttempts { get; private set; }
        public string ImagesDirectory { get; }

        public Task<BoardSnapshot> LoadBoardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BoardSnapshot());

        public Task SaveBoardAsync(BoardSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            if (FailSave)
            {
                throw new IOException("Injected save failure.");
            }

            SaveCount++;
            return Task.CompletedTask;
        }

        public Task<WindowSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(WindowSettings.Default);

        public Task SaveSettingsAsync(WindowSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public bool TryDeleteImage(string? absolutePath) => true;
    }
}
