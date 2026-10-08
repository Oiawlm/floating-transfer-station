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
/// 空白取消、保存失败还原、图片卡与搜索态不进入编辑。1.17.0 起编辑入口是
/// 手势层左区双击（真实预览事件序列驱动），不再依赖 Control.MouseDoubleClick。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    /// <summary>经由手势层进入编辑：左区双击（真实预览按下/抬起序列）。</summary>
    private static void EnterCardEditingWithDoubleClick(MainWindow window, BoardItem item) =>
        RaiseCardDoubleClick(ZoneHitSource(window, item, CardGestureZones.ContentZone));

    private static ListBoxItem ContainerOf(MainWindow window, BoardItem item)
    {
        var list = (ListBox)window.FindName("BoardList");
        var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(item);
        Assert.IsNotNull(container);
        return container;
    }

    /// <summary>
    /// 1.22.0 起编辑器在卡片模板内原位切换（覆盖层已删除）：按条目容器查找
    /// 编辑 TextBox（模板内唯一 TextBox，可见性由条目 IsEditing 驱动）。
    /// </summary>
    private static TextBox EditorInput(MainWindow window, BoardItem item)
    {
        var editor = FindDescendants<TextBox>(ContainerOf(window, item)).Single();
        Assert.IsNotNull(editor);
        return editor;
    }

    /// <summary>断言没有任何卡片编辑器处于可见状态（图片卡/搜索态不进入编辑）。</summary>
    private static void AssertNoVisibleCardEditor(MainWindow window)
    {
        var list = (ListBox)window.FindName("BoardList");
        Assert.IsFalse(
            FindDescendants<TextBox>(list).Any(editor => editor.IsVisible),
            "不应有任何卡片编辑器处于可见状态。");
    }

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

            EnterCardEditingWithDoubleClick(window, item);
            CompleteLayout(window);

            Assert.IsTrue(EditorInput(window, item).IsVisible);
            Assert.AreEqual("原始内容", EditorInput(window, item).Text);
            Assert.IsTrue(EditorInput(window, item).IsKeyboardFocused);

            EditorInput(window, item).Text = "编辑后的内容";
            EditorInput(window, item).RaiseEvent(new KeyEventArgs(
                Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window)!,
                Environment.TickCount,
                Key.Enter)
            {
                RoutedEvent = Keyboard.KeyDownEvent,
                Source = EditorInput(window, item)
            });
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            CompleteLayout(window);

            Assert.AreEqual("编辑后的内容", board.FindItem(item.Id)!.Text);
            Assert.AreEqual(1, store.SaveCount);
            AssertNoVisibleCardEditor(window);
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
            EnterCardEditingWithDoubleClick(window, item);
            EditorInput(window, item).Text = "不应保存的修改";
            EditorInput(window, item).RaiseEvent(new KeyEventArgs(
                Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window)!,
                Environment.TickCount,
                Key.Escape)
            {
                RoutedEvent = Keyboard.KeyDownEvent,
                Source = EditorInput(window, item)
            });
            CompleteLayout(window);

            Assert.AreEqual("保持不变", board.FindItem(item.Id)!.Text);
            Assert.AreEqual(0, store.SaveCount);
            AssertNoVisibleCardEditor(window);
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
            EnterCardEditingWithDoubleClick(window, item);
            EditorInput(window, item).Text = "   ";
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
            EnterCardEditingWithDoubleClick(window, item);
            EditorInput(window, item).Text = "保存会失败的修改";
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

            RaiseCardDoubleClick(ZoneHitSource(window, image, CardGestureZones.ContentZone));
            CompleteLayout(window);
            AssertNoVisibleCardEditor(window);

            window.EnterSearchMode();
            CompleteLayout(window);
            RaiseCardDoubleClick(ZoneHitSource(window, text, CardGestureZones.ContentZone));
            CompleteLayout(window);
            AssertNoVisibleCardEditor(window);
            window.ExitSearchMode();
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardEditing_DoubleClickWhileEditingDoesNotReenter()
    {
        using var directory = new TestDirectory();
        var store = new RecordingCardEditStore(directory.Root);
        var board = new BoardService();
        var item = board.AddText("已在编辑", BoardCategory.Inbox);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            EnterCardEditingWithDoubleClick(window, item);
            CompleteLayout(window);
            EditorInput(window, item).Text = "手动改动";

            // 已有编辑时再次双击（同一张卡）：编辑表面吞掉手势（编辑器持有该
            // 按下、手势层不标记已处理），不重置种子文本。原始预览事件模拟
            // 双击（不经由断言 Handled 的助手——编辑卡的按下有意不被认领）。
            var zone = ZoneHitSource(window, item, CardGestureZones.ContentZone);
            var firstClick = NextIsolatedClickTimestamp();
            zone.RaiseEvent(NewMouseButtonEventArgs(Mouse.PreviewMouseDownEvent, zone, firstClick));
            zone.RaiseEvent(NewMouseButtonEventArgs(Mouse.PreviewMouseUpEvent, zone, firstClick + 5));
            var secondClick = NextPairedClickTimestamp();
            zone.RaiseEvent(NewMouseButtonEventArgs(Mouse.PreviewMouseDownEvent, zone, secondClick));
            zone.RaiseEvent(NewMouseButtonEventArgs(Mouse.PreviewMouseUpEvent, zone, secondClick + 5));
            CompleteLayout(window);

            Assert.IsTrue(EditorInput(window, item).IsVisible);
            Assert.AreEqual("手动改动", EditorInput(window, item).Text, "编辑中的双击不得重置编辑器内容。");
            Assert.IsTrue(EditorInput(window, item).IsKeyboardFocused);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardEditing_EditorIsInPlaceInsideTheCardContainer()
    {
        // 1.22.0 架构守护：编辑器在卡片 DataTemplate 内原位切换（旧覆盖层实现的
        // 编辑器宿主是列表兄弟元素，本用例的祖先断言必然失败）——编辑 TextBox
        // 的可视祖先包含其 ListBoxItem 容器；显示态 TextBlock 同刻让位；键入的
        // 草稿只存在于条目 DraftText，显示（PreviewText）与持久化（Text）在提交
        // 前零泄漏。
        using var directory = new TestDirectory();
        var store = new RecordingCardEditStore(directory.Root);
        var board = new BoardService();
        var item = board.AddText("原位编辑的卡片", BoardCategory.Inbox);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            var container = ContainerOf(window, item);
            var heightBefore = container.ActualHeight;
            EnterCardEditingWithDoubleClick(window, item);
            CompleteLayout(window);

            var editor = FindDescendants<TextBox>(container).Single();
            Assert.IsTrue(
                container.IsAncestorOf(editor),
                "编辑器必须在卡片容器内原位出现（禁止浮层回归）。");
            Assert.IsTrue(editor.IsVisible);
            Assert.AreEqual(
                heightBefore,
                container.ActualHeight,
                0.5,
                "进入编辑不得改变卡片高度（编辑态以同度量盖写，非浮层/不塌缩）。");
            Assert.AreEqual(
                "原位编辑的卡片",
                editor.Text,
                "编辑器草稿必须种子为全文而非有界预览。");

            var display = FindDescendants<TextBlock>(container)
                .Single(candidate => ReferenceEquals(candidate.DataContext, item));
            Assert.AreEqual(
                Visibility.Visible,
                display.Visibility,
                "编辑期间显示态保持可见（高度恒定不塌缩），由同度量的编辑器以卡片底色盖写。");
            Assert.AreEqual(
                display.Margin,
                editor.Margin,
                "编辑器与显示文本必须同宽度同位置（同样的操作列避让）。");

            editor.Text = "键入中的草稿";
            CompleteLayout(window);

            Assert.AreEqual(
                "原位编辑的卡片",
                board.FindItem(item.Id)!.Text,
                "提交前草稿不得进入持久化文本。");
            Assert.AreEqual(
                "原位编辑的卡片",
                display.Text,
                "提交前草稿不得泄漏到显示文本。");
            Assert.AreEqual(0, store.SaveCount, "提交前不得发生持久化。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardEditing_CloseWithOpenEditorFlushesBeforeBoardGateSeals()
    {
        using var directory = new TestDirectory();
        var store = new RecordingCardEditStore(directory.Root);
        var board = new BoardService();
        var item = board.AddText("关闭前仍在编辑", BoardCategory.Inbox);
        var window = CreateWindow(board, store, WindowSettings.Default);
        var hasClosed = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            EnterCardEditingWithDoubleClick(window, item);
            CompleteLayout(window);
            EditorInput(window, item).Text = "关闭时冲刷的编辑";
            Assert.IsTrue(EditorInput(window, item).IsKeyboardFocused, "前置条件：编辑器仍打开并持有焦点。");

            // 关闭序列必须在操作门封门前冲刷编辑：修复前窗口销毁期的失焦回调
            // 在封门后再注册操作，抛出"Board operations are closed."。
            CloseWindow(window);
            hasClosed = true;

            Assert.AreEqual("关闭时冲刷的编辑", board.FindItem(item.Id)!.Text);
            Assert.AreEqual(
                "关闭时冲刷的编辑",
                store.LastPersistedSnapshot!.Items.Single(persisted => persisted.Id == item.Id).Text,
                "编辑内容必须随最终保存落盘。");
        }
        finally
        {
            if (!hasClosed)
            {
                CloseWindow(window);
            }
        }
    }

    private sealed class RecordingCardEditStore : IBoardStore
    {
        public RecordingCardEditStore(string root) => ImagesDirectory = Path.Combine(root, "images");

        public bool FailSave { get; set; }
        public int SaveCount { get; private set; }
        public int SaveAttempts { get; private set; }
        public BoardSnapshot? LastPersistedSnapshot { get; private set; }
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
            LastPersistedSnapshot = snapshot;
            return Task.CompletedTask;
        }

        public Task<WindowSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(WindowSettings.Default);

        public Task SaveSettingsAsync(WindowSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public bool TryDeleteImage(string? absolutePath) => true;
    }
}
