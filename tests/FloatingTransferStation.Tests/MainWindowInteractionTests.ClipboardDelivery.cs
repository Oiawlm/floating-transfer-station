using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 卡片复制到剪贴板（交付第二通道）回归：右键复制单卡（不改选择、不删源、
/// 搜索过滤态可用）、Ctrl+C 复制选中（板内顺序合并、无选择不拦截、编辑态让路）、
/// 复制负载携带内部条目标记且自动采集跳过（不产生重复卡）。
/// 测试经 ClipboardWriterOverride 捕获负载，不读写系统剪贴板。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    private static MouseButtonEventArgs NewRightClickArgs(object source) =>
        new(Mouse.PrimaryDevice, 0, MouseButton.Right)
        {
            RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent,
            Source = source
        };

    /// <summary>右键卡片右区（操作列）：复制手势只属于右区。</summary>
    private static MouseButtonEventArgs NewRightClickArgsOnCard(
        MainWindow window,
        BoardItem item)
    {
        var hitSource = ZoneHitSource(window, item, CardGestureZones.OperationsZone);
        return NewRightClickArgs(hitSource);
    }

    private static string StatusOf(MainWindow window) =>
        ((MainWindowViewModel)window.DataContext).StatusText;

    private static BoardItem AddImageCard(TestDirectory directory, BoardService board, string fileName)
    {
        var path = Path.Combine(directory.Root, fileName);
        WritePng(path, 2, 2);
        return board.AddImage(Guid.NewGuid(), $"images/{fileName}", path, BoardCategory.Inbox);
    }

    [STATestMethod]
    public void RightClickCopy_CopiesTextCardWithoutTouchingSelectionOrBoard()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var first = board.AddText("第一条");
        var second = board.AddText("第二条");
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);
        DataObject? captured = null;
        window.ClipboardWriterOverride = data =>
        {
            captured = data;
            return true;
        };

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(second);

            list.RaiseEvent(NewRightClickArgsOnCard(window, first));
            CompleteLayout(window);

            Assert.IsNotNull(captured);
            Assert.AreEqual("第一条", captured.GetData(DataFormats.UnicodeText));
            Assert.AreEqual(first.Id, new DragPayloadService().GetInternalItemId(captured));
            CollectionAssert.AreEqual(
                new[] { second },
                list.SelectedItems.Cast<BoardItem>().ToArray(),
                "右键复制不得依赖或改变当前选择。");
            CollectionAssert.AreEqual(
                new[] { second, first },
                board.Items(BoardCategory.Inbox).ToArray(),
                "复制不得删除或移动源卡片。");
            Assert.AreEqual(0, store.SaveCount, "复制不得触发保存。");
            Assert.AreEqual("已复制 1 条文字到剪贴板", StatusOf(window));
            InvokePrivate(window, "SetHeaderActionsVisible", true);
            CompleteLayout(window);
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "right-click-copy-status.png",
                "FTS_DELIVERY_EVIDENCE_DIR");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void RightClickCopy_ImageCardDeliversFileDropAndBitmap()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var image = AddImageCard(directory, board, "right-click.png");
        var window = CreateWindow(directory, board);
        DataObject? captured = null;
        window.ClipboardWriterOverride = data =>
        {
            captured = data;
            return true;
        };

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");

            list.RaiseEvent(NewRightClickArgsOnCard(window, image));
            CompleteLayout(window);

            Assert.IsNotNull(captured);
            CollectionAssert.AreEqual(
                new[] { image.ImageAbsolutePath },
                captured.GetFileDropList().Cast<string>().ToArray());
            Assert.IsTrue(captured.GetDataPresent(DataFormats.Bitmap));
            Assert.AreEqual(image.Id, new DragPayloadService().GetInternalItemId(captured));
            Assert.AreEqual(1, board.Items(BoardCategory.Inbox).Count);
            Assert.AreEqual("已复制 1 张图片到剪贴板", StatusOf(window));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void RightClickCopy_DisabledPreferenceKeepsGestureIdle()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("不可复制");
        var window = CreateWindow(directory, board);
        window.ApplyPreferences(AppPreferences.Default with { RightClickCardCopyEnabled = false });
        DataObject? captured = null;
        window.ClipboardWriterOverride = data =>
        {
            captured = data;
            return true;
        };

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var args = NewRightClickArgsOnCard(window, item);

            list.RaiseEvent(args);
            CompleteLayout(window);

            Assert.IsFalse(args.Handled, "偏好关闭时右键不拦截。");
            Assert.IsNull(captured);
            Assert.IsEmpty(StatusOf(window));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void RightClickCopy_ContentZoneIsNotIntercepted()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("左区右键无操作");
        var window = CreateWindow(directory, board);
        DataObject? captured = null;
        window.ClipboardWriterOverride = data =>
        {
            captured = data;
            return true;
        };

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var args = NewRightClickArgs(
                ZoneHitSource(window, item, CardGestureZones.ContentZone));

            list.RaiseEvent(args);
            CompleteLayout(window);

            Assert.IsFalse(args.Handled, "左区右键无操作、不拦截（内容区不承载复制手势）。");
            Assert.IsNull(captured);
            Assert.IsEmpty(StatusOf(window));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void RightClickCopy_RemainsAvailableInSearchFilterMode()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var match = board.AddText("目标 needle 卡片");
        board.AddText("不匹配的其他卡片");
        var window = CreateWindow(directory, board);
        DataObject? captured = null;
        window.ClipboardWriterOverride = data =>
        {
            captured = data;
            return true;
        };

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            window.EnterSearchMode();
            var searchInput = (TextBox)window.FindName("SearchInput");
            searchInput.Text = "needle";
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(match);
            Assert.IsNotNull(container, "过滤后匹配卡片必须仍可右键复制。");

            list.RaiseEvent(NewRightClickArgsOnCard(window, match));
            CompleteLayout(window);

            Assert.IsNotNull(captured);
            Assert.AreEqual("目标 needle 卡片", captured.GetData(DataFormats.UnicodeText));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CtrlC_CopiesSelectionMergedInBoardOrder()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var normal = board.AddText("普通甲");
        var last = board.AddText("普通乙");
        var pinned = board.AddText("置顶文字");
        board.SetPinnedMany([pinned.Id], true);
        var window = CreateWindow(directory, board);
        DataObject? captured = null;
        window.ClipboardWriterOverride = data =>
        {
            captured = data;
            return true;
        };

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(last);
            list.SelectedItems.Add(pinned);
            CompleteLayout(window);
            var copyKey = NewModifiedKeyEventArgs(window, Key.C, ModifierKeys.Control);

            window.RaiseEvent(copyKey);
            CompleteLayout(window);

            Assert.IsTrue(copyKey.Handled);
            Assert.IsNotNull(captured);
            Assert.AreEqual(
                $"置顶文字{Environment.NewLine}普通乙",
                captured.GetData(DataFormats.UnicodeText),
                "多条文字按板内顺序（置顶区在前）合并。");
            Assert.IsEmpty(captured.GetFileDropList().Cast<string>());
            Assert.AreEqual("已复制 2 条文字到剪贴板", StatusOf(window));
            CollectionAssert.AreEquivalent(
                new[] { pinned, last },
                list.SelectedItems.Cast<BoardItem>().ToArray(),
                "复制后选择保持不变。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CtrlC_MixedSelectionMergesTextAndImageFileGroup()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var image = AddImageCard(directory, board, "mixed.png");
        var text = board.AddText("混合选择中的文字");
        var window = CreateWindow(directory, board);
        DataObject? captured = null;
        window.ClipboardWriterOverride = data =>
        {
            captured = data;
            return true;
        };

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(text);
            list.SelectedItems.Add(image);
            CompleteLayout(window);

            window.RaiseEvent(NewModifiedKeyEventArgs(window, Key.C, ModifierKeys.Control));
            CompleteLayout(window);

            Assert.IsNotNull(captured);
            Assert.AreEqual("混合选择中的文字", captured.GetData(DataFormats.UnicodeText));
            CollectionAssert.AreEqual(
                new[] { image.ImageAbsolutePath },
                captured.GetFileDropList().Cast<string>().ToArray());
            Assert.IsTrue(captured.GetDataPresent(DragPayloadService.InternalItemIdsFormat));
            Assert.AreEqual("已复制 1 条文字和 1 张图片到剪贴板", StatusOf(window));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CtrlC_WithoutSelectionLeavesClipboardAndKeyUnhandled()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("未选中的卡片");
        var window = CreateWindow(directory, board);
        DataObject? captured = null;
        window.ClipboardWriterOverride = data =>
        {
            captured = data;
            return true;
        };

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            Keyboard.ClearFocus();
            var copyKey = NewModifiedKeyEventArgs(window, Key.C, ModifierKeys.Control);

            window.RaiseEvent(copyKey);
            CompleteLayout(window);

            Assert.IsFalse(copyKey.Handled, "无选择时 Ctrl+C 不拦截、不改剪贴板。");
            Assert.IsNull(captured);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CtrlC_TextEditingFocusYieldsToTheEditor()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("编辑中的卡片");
        var window = CreateWindow(directory, board);
        DataObject? captured = null;
        window.ClipboardWriterOverride = data =>
        {
            captured = data;
            return true;
        };

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(item);
            Assert.IsNotNull(container);
            EnterCardEditingWithDoubleClick(window, item);
            CompleteLayout(window);
            var editor = (TextBox)window.FindName("CardTextEditor");
            Assert.IsTrue(editor.IsKeyboardFocused, "前置条件：文字编辑器持有键盘焦点。");
            editor.SelectionStart = 0;
            editor.SelectionLength = editor.Text.Length;
            var copyKey = NewModifiedKeyEventArgs(window, Key.C, ModifierKeys.Control);
            copyKey.Source = editor;

            InvokePrivate(window, "MainWindow_PreviewKeyDown", window, copyKey);
            CompleteLayout(window);

            Assert.IsFalse(copyKey.Handled, "编辑态 Ctrl+C 必须让路给文本编辑。");
            Assert.IsNull(captured);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CtrlC_DisabledPreferenceLeavesKeyUnhandled()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("选中也复制不了");
        var window = CreateWindow(directory, board);
        window.ApplyPreferences(AppPreferences.Default with { CopySelectionWithCtrlCEnabled = false });
        DataObject? captured = null;
        window.ClipboardWriterOverride = data =>
        {
            captured = data;
            return true;
        };

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(item);
            Keyboard.ClearFocus();
            var copyKey = NewModifiedKeyEventArgs(window, Key.C, ModifierKeys.Control);

            window.RaiseEvent(copyKey);
            CompleteLayout(window);

            Assert.IsFalse(copyKey.Handled);
            Assert.IsNull(captured);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void RightClickCopy_DoesNotTriggerAutomaticRecapture()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("复制后不能重复入库");
        var store = new RecordingBoardStore(directory.Root);
        DataObject? captured = null;
        // 模拟 WpfClipboardReader 的真实读路径：剪贴板更新后按捕获到的 DataObject 采集。
        var reader = new DelegateClipboardReader(
            () => new ClipboardPayloadReader().Read(captured, 601));
        var window = CreateWindow(
            board,
            store,
            WindowSettings.Default,
            null,
            new ImageNormalizer(store.ImagesDirectory),
            reader);
        window.ClipboardWriterOverride = data =>
        {
            captured = data;
            return true;
        };

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");

            list.RaiseEvent(NewRightClickArgsOnCard(window, item));
            CompleteLayout(window);
            Assert.IsNotNull(captured);
            Assert.AreEqual("已复制 1 条文字到剪贴板", StatusOf(window));

            // 应用自身复制引发的 WM_CLIPBOARDUPDATE：走与生产一致的采集入口。
            InvokePrivate(window, "StartClipboardCapture");
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            CompleteLayout(window);

            Assert.AreSame(
                item,
                board.Items(BoardCategory.Inbox).Single(),
                "复制自身的卡片内容不得被自动收集重新入库。");
            Assert.AreEqual(0, store.SaveCount);
            Assert.AreEqual(
                "已复制 1 条文字到剪贴板",
                StatusOf(window),
                "采集跳过不得产生去重或失败提示。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    private sealed class DelegateClipboardReader(Func<ClipboardSnapshot> read) : IClipboardReader
    {
        public Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(read());
    }
}
