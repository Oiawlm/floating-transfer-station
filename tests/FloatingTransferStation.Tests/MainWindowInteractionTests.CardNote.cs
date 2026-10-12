using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FloatingTransferStation.Controls;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 长卡片备注（1.28.0，契约 #22）交互守护：添加门槛=渲染布局事实（正文截断，
/// TrimAwareTextBlock.IsTruncated）、编辑入口无门槛（已有备注常显随时可点）、
/// 空白提交=删除、备注是元数据（不参与选择/复制/拖出/搜索）、备注编辑复用
/// 正文编辑会话机器（Enter/Esc/失焦/锚点终结/编辑持焦面板不收起）、原子持久化
/// 与失败还原、右键语义零改动、#16 避让零争用。2026-10-12 起（Issue #121）：
/// 编辑器行0 备注行原位同格盖写（图标可见性统一规则、结构化对齐、有界长高
/// 回落）与进入编辑光标定位到点击处（无点位回退文末）。指针路径用 Win32
/// 消息直驱（契约 #13 口径）；持久化用 LocalStore 真实文件往返。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    /// <summary>8 行正文（换行定行，行数与宽度无关）：自然高度 160 &gt; 限高 100。</summary>
    private const string LongCardText = "字\n字\n字\n字\n字\n字\n字\n字";

    private static TextBox CardNoteEditorOf(ListBoxItem container) =>
        FindDescendants<TextBox>(container).Single(editor => editor.Name == "CardNoteEditor");

    private static Button CardNoteStripButtonOf(ListBoxItem container) =>
        FindDescendants<Button>(container).Single(button => button.Name == "CardOperationNoteButton");

    private static Button CardNoteEntryButtonOf(ListBoxItem container) =>
        FindDescendants<Button>(container).Single(button => button.Name == "CardNoteEntryButton");

    /// <summary>备注显示行（在流、纯展示）。</summary>
    private static FrameworkElement NoteRowOf(ListBoxItem container) =>
        FindDescendants<FrameworkElement>(container).Single(element => element.Name == "NoteDisplayRow");

    /// <summary>手势层合成入口：在备注交互条按钮上发出一对预览按下/抬起。</summary>
    private static void RaiseNoteStripClick(MainWindow window, BoardItem item, string buttonName)
    {
        var button = FindDescendants<Button>(ContainerOf(window, item))
            .Single(candidate => candidate.Name == buttonName);
        RaiseCardPressAndRelease(button, NextIsolatedClickTimestamp());
    }

    /// <summary>真实光标悬停（无点击）：强制位移预热后移入目标点并泵到容器悬停。
    /// 不在此处断言成败——悬停落空的瞬时偏差由 HoverRealUntil 的重试（重新预热
    /// 位移再移入）吸收，直接调用方依赖后续状态断言兜底。</summary>
    private static void HoverRealAt(
        MainWindow window,
        FrameworkElement container,
        Point positionInContainer)
    {
        var screen = container.PointToScreen(positionInContainer);
        Assert.IsTrue(
            SetCursorPos((int)Math.Round(screen.X) + 7, (int)Math.Round(screen.Y) + 5),
            "真实光标必须可移动（强制位移预热）。");
        Assert.IsTrue(
            SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y)),
            "真实光标必须可移入目标点。");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!container.IsMouseOver && DateTime.UtcNow < deadline)
        {
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
        }
    }

    /// <summary>泵 dispatcher 直到断言条件成立（备注保存是异步落盘）。</summary>
    private static void PumpUntil(MainWindow window, Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
        }

        Assert.IsTrue(condition(), message);
    }

    /// <summary>
    /// 按显示元素的实际字体设置量测每字符宽度（同款 Display 格式化模式），
    /// 供点击定位用例推导目标字位——不依赖「汉字宽=字号」的假定（T7 评审
    /// 20261012-204651-6pi：期望索引必须来自实测量测而非推算）。
    /// </summary>
    private static double MeasuredCharWidth(TextBlock display, string text)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            display.FlowDirection,
            new Typeface(display.FontFamily, display.FontStyle, display.FontWeight, display.FontStretch),
            display.FontSize,
            Brushes.Transparent,
            null,
            TextOptions.GetTextFormattingMode(display),
            VisualTreeHelper.GetDpi(display).DpiScaleX);
        return formatted.Width / text.Length;
    }

    private static bool IsPanelEditHoldActive(MainWindow window) =>
        typeof(MainWindow)
            .GetMethod("IsPanelEditHoldActive", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null) as bool? == true;

    private static void RaiseCardEditorKey(MainWindow window, TextBox editor, Key key) =>
        editor.RaiseEvent(new KeyEventArgs(
            Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(window)!,
            Environment.TickCount,
            key)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
            Source = editor
        });

    [STATestMethod]
    public void CardNote_StripEntry_OnlyOnTruncatedTextCardsOnHover()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var longItem = board.AddText(LongCardText, BoardCategory.Inbox);
        var shortItem = board.AddText("短卡字", BoardCategory.Inbox);
        var imageItem = AddImageCard(directory, board, "strip-entry.png");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var longContainer = ContainerOf(window, longItem);
            var shortContainer = ContainerOf(window, shortItem);
            var imageContainer = ContainerOf(window, imageItem);
            window.Topmost = true;
            CompleteLayout(window);
            var stripOnLong = CardNoteStripButtonOf(longContainer);
            var stripOnShort = CardNoteStripButtonOf(shortContainer);
            var stripOnImage = CardNoteStripButtonOf(imageContainer);
            Assert.IsFalse(FadeAnimation.GetIsActive(stripOnLong), "未悬停时添加备注按钮必须不可见。");
            Assert.AreEqual(
                Visibility.Collapsed, stripOnLong.Visibility,
                "未悬停时按钮必须塌缩（格子固定预留、可见性塌缩）。");
            var savedCursor = SaveCursorPosition();

            try
            {
                // 截断文字卡悬停：右侧添加备注按钮激活且可命中（真实输入可达）。
                HoverRealAt(window, longContainer, new Point(24, longContainer.ActualHeight * 0.8));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(120));
                Assert.IsTrue(FadeAnimation.GetIsActive(stripOnLong), "截断文字卡悬停必须激活添加备注按钮。");
                Assert.IsTrue(stripOnLong.IsHitTestVisible, "可见的添加备注按钮必须可命中。");
                Assert.AreEqual(Visibility.Visible, stripOnLong.Visibility);
                Assert.AreEqual("添加备注", stripOnLong.ToolTip);
                SaveVisualEvidence(
                    (Border)window.FindName("WindowShell"),
                    "note-strip-entry.png",
                    "FTS_NOTE_EVIDENCE_DIR");

                // 短卡悬停：内容完整可见，无添加入口（按钮塌缩）。
                HoverRealAt(window, shortContainer, new Point(24, shortContainer.ActualHeight * 0.8));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
                Assert.IsFalse(FadeAnimation.GetIsActive(stripOnShort), "短卡不得出现添加备注入口。");
                Assert.IsFalse(stripOnShort.IsHitTestVisible);
                Assert.AreEqual(Visibility.Collapsed, stripOnShort.Visibility, "短卡的添加备注按钮必须塌缩。");
                SaveVisualEvidence(
                    (Border)window.FindName("WindowShell"),
                    "note-short-card-no-entry.png",
                    "FTS_NOTE_EVIDENCE_DIR");

                // 图片卡悬停：无备注入口（备注仅文字卡）。
                HoverRealAt(window, imageContainer, new Point(24, imageContainer.ActualHeight * 0.8));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
                Assert.IsFalse(FadeAnimation.GetIsActive(stripOnImage), "图片卡不得出现添加备注入口。");
                Assert.AreEqual(Visibility.Collapsed, stripOnImage.Visibility);

                // 正文编辑中隐藏备注入口（双向互斥）。
                HoverRealAt(window, longContainer, new Point(24, longContainer.ActualHeight * 0.8));
                RaiseCardDoubleClick(ZoneHitSource(window, longItem, CardGestureZones.ContentZone));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
                Assert.IsTrue(longItem.IsEditing, "前置条件：正文编辑已开启。");
                Assert.IsFalse(FadeAnimation.GetIsActive(stripOnLong), "正文编辑中必须隐藏添加备注按钮。");
                Assert.AreEqual(Visibility.Collapsed, stripOnLong.Visibility);
                window.CommitCardTextForTest();

                // 备注编辑中同样隐藏（IsEditingNote 条件）。
                RaiseNoteStripClick(window, longItem, "CardOperationNoteButton");
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(120));
                Assert.IsTrue(longItem.IsEditingNote, "前置条件：备注编辑已开启。");
                HoverRealAt(window, longContainer, new Point(24, longContainer.ActualHeight * 0.8));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
                Assert.IsFalse(FadeAnimation.GetIsActive(stripOnLong), "备注编辑中必须隐藏添加备注按钮。");
                Assert.AreEqual(Visibility.Collapsed, stripOnLong.Visibility);
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardNote_HoverDoesNotChangeCardHeight()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText(LongCardText, BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            window.Topmost = true;
            CompleteLayout(window);
            var savedCursor = SaveCursorPosition();

            try
            {
                var heightBefore = container.ActualHeight;
                HoverRealAt(window, container, new Point(24, container.ActualHeight * 0.8));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(150));
                Assert.IsTrue(
                    FadeAnimation.GetIsActive(CardNoteStripButtonOf(container)),
                    "前置条件：悬停已触发添加备注按钮（验证的是它出现时的高度稳定性）。");

                Assert.AreEqual(
                    heightBefore,
                    container.ActualHeight,
                    "悬停出现添加备注按钮不得改变卡片高度（按钮出现在既有操作条列内，向左生长不参与纵向排版）。");
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardNote_RealInputClickEntersEditingWithoutChangingSelection()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("短卡字", BoardCategory.Inbox);
        item.Note = "已有备注";
        var other = board.AddText("另一张短卡", BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            window.Topmost = true;
            CompleteLayout(window);
            var entryButton = CardNoteEntryButtonOf(container);
            Assert.IsTrue(entryButton.IsHitTestVisible, "已有备注的点击面必须可命中（编辑入口无门槛）。");
            var clickPoint = entryButton.TranslatePoint(
                new Point(entryButton.ActualWidth / 2, entryButton.ActualHeight / 2),
                container);
            var savedCursor = SaveCursorPosition();

            try
            {
                // 端到端：Win32 消息直驱点击备注点击面（命中层之上的透明按钮），
                // 进入备注编辑；左键绝不 toggle 选中。
                ClickRealAt(window, container, clickPoint, hoverSettleMilliseconds: 120);
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }

            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
            CompleteLayout(window);
            var editor = CardNoteEditorOf(container);
            Assert.IsTrue(item.IsEditingNote, "真实输入点击备注点击面必须进入备注编辑。");
            Assert.IsTrue(editor.IsVisible);
            Assert.IsTrue(editor.IsKeyboardFocused, "备注编辑器必须持有键盘焦点。");
            Assert.AreEqual("已有备注", editor.Text, "打开编辑必须预填现备注（防失焦即误删）。");
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "note-editing.png",
                "FTS_NOTE_EVIDENCE_DIR");
            CollectionAssert.AreEqual(
                Array.Empty<BoardItem>(),
                ((ListBox)window.FindName("BoardList")).SelectedItems.Cast<BoardItem>().ToArray(),
                "备注交互条左键绝不改变选择。");
            Assert.IsFalse(other.IsEditingNote);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardNote_EnterCommitsEscapeCancelsFocusLossCommits()
    {
        using var directory = new TestDirectory();
        var store = new RecordingCardEditStore(directory.Root);
        var board = new BoardService();
        var enterItem = board.AddText("第一张短卡", BoardCategory.Inbox);
        enterItem.Note = "进入备注";
        var escapeItem = board.AddText("第二张短卡", BoardCategory.Inbox);
        escapeItem.Note = "取消备注";
        var focusItem = board.AddText("第三张短卡", BoardCategory.Inbox);
        focusItem.Note = "失焦备注";
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            // Enter 提交（预填现值后修改）。
            RaiseNoteStripClick(window, enterItem, "CardNoteEntryButton");
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
            CompleteLayout(window);
            var editor = CardNoteEditorOf(ContainerOf(window, enterItem));
            Assert.IsTrue(editor.IsKeyboardFocused, "前置条件：备注编辑器持焦。");
            Assert.AreEqual("进入备注", editor.Text, "必须预填现备注。");
            editor.Text = "进入提交的备注";
            RaiseCardEditorKey(window, editor, Key.Enter);
            PumpUntil(window, () => enterItem.Note == "进入提交的备注", "Enter 必须提交备注并落盘。");
            Assert.IsFalse(enterItem.IsEditingNote, "提交后必须退出编辑态。");
            Assert.IsNull(enterItem.DraftNote, "非编辑态不得残留草稿。");

            // Esc 取消（草稿丢弃，现备注保留）。
            RaiseNoteStripClick(window, escapeItem, "CardNoteEntryButton");
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
            CompleteLayout(window);
            var escapeEditor = CardNoteEditorOf(ContainerOf(window, escapeItem));
            escapeEditor.Text = "不应保存的备注";
            RaiseCardEditorKey(window, escapeEditor, Key.Escape);
            PumpUntil(window, () => !escapeItem.IsEditingNote, "Esc 必须退出编辑态。");
            Assert.AreEqual("取消备注", escapeItem.Note, "Esc 取消不得改动现备注。");

            // 失焦提交（焦点离开本窗口才终结——窗口内焦点迁移保持会话）。
            RaiseNoteStripClick(window, focusItem, "CardNoteEntryButton");
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
            CompleteLayout(window);
            var focusEditor = CardNoteEditorOf(ContainerOf(window, focusItem));
            focusEditor.Text = "失焦提交的备注";
            // 焦点去向=窗外元素（用户点别的应用的焦点形态）：NewFocus 非本窗口
            // 祖先，LostKeyboardFocus 处理器走提交分支。
            focusEditor.RaiseEvent(new KeyboardFocusChangedEventArgs(
                Keyboard.PrimaryDevice,
                Environment.TickCount,
                focusEditor,
                new Window())
            {
                RoutedEvent = Keyboard.LostKeyboardFocusEvent,
                Source = focusEditor
            });
            PumpUntil(window, () => focusItem.Note == "失焦提交的备注", "失焦必须提交备注。");
            Assert.IsFalse(focusItem.IsEditingNote);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardNote_EmptyCommitRemovesExistingNote()
    {
        using var directory = new TestDirectory();
        var store = new RecordingCardEditStore(directory.Root);
        var board = new BoardService();
        var item = board.AddText(LongCardText, BoardCategory.Inbox);
        item.Note = "待删除备注";
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            // 空白提交=删除已有备注（无二次确认，Esc 是后悔通道）。
            RaiseNoteStripClick(window, item, "CardNoteEntryButton");
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
            CompleteLayout(window);
            var editor = CardNoteEditorOf(ContainerOf(window, item));
            editor.Text = "   ";
            RaiseCardEditorKey(window, editor, Key.Enter);
            PumpUntil(window, () => item.Note is null, "空白提交必须删除已有备注。");
            Assert.IsFalse(item.IsEditingNote);

            // 无备注时空白提交=无操作：经右侧添加备注按钮（悬停揭示的真实路径）
            // 再次进入编辑，直接 Enter——等值短路不落盘、无状态提示。
            var container = ContainerOf(window, item);
            window.Topmost = true;
            CompleteLayout(window);
            var strip = CardNoteStripButtonOf(container);
            var savedCursor = SaveCursorPosition();
            try
            {
                HoverRealAt(window, container, new Point(24, container.ActualHeight * 0.8));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(150));
                Assert.IsTrue(FadeAnimation.GetIsActive(strip), "前置条件：无备注截断卡悬停出现添加备注按钮。");
                var stripPoint = strip.TranslatePoint(
                    new Point(strip.ActualWidth / 2, strip.ActualHeight / 2),
                    container);
                ClickRealAt(window, container, stripPoint, hoverSettleMilliseconds: 120);
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
                CompleteLayout(window);
                Assert.IsTrue(item.IsEditingNote, "真实点击添加备注按钮必须再次进入备注编辑。");
                var editorAgain = CardNoteEditorOf(container);
                RaiseCardEditorKey(window, editorAgain, Key.Enter);
                PumpUntil(window, () => !item.IsEditingNote, "空白提交后必须退出编辑态。");
                Assert.IsNull(item.Note, "无备注时空白提交必须保持无备注。");
                Assert.AreEqual(string.Empty, StatusOf(window), "备注删除/无操作不得产生状态条噪音。");
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardNote_SaveFailureRestoresNote()
    {
        using var directory = new TestDirectory();
        var store = new RecordingCardEditStore(directory.Root) { FailSave = true };
        var board = new BoardService();
        var item = board.AddText("短卡字", BoardCategory.Inbox);
        item.Note = "原备注";
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            RaiseNoteStripClick(window, item, "CardNoteEntryButton");
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
            CompleteLayout(window);
            var editor = CardNoteEditorOf(ContainerOf(window, item));
            editor.Text = "会失败的备注";
            window.CommitCardTextForTest();
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            CompleteLayout(window);

            Assert.AreEqual("原备注", item.Note, "保存失败必须还原原备注。");
            Assert.IsFalse(item.IsEditingNote);
            StringAssert.Contains(StatusOf(window), "备注未保存，已还原原备注。");
            store.FailSave = false;
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardNote_EditingKeepsPanelOpen()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("短卡字", BoardCategory.Inbox);
        item.Note = "持焦备注";
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            RaiseNoteStripClick(window, item, "CardNoteEntryButton");
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
            CompleteLayout(window);
            Assert.IsTrue(CardNoteEditorOf(ContainerOf(window, item)).IsKeyboardFocused);

            // 契约 #7 的保持机制输入：备注编辑持焦期间编辑保持原因激活。
            Assert.IsTrue(
                IsPanelEditHoldActive(window),
                "备注编辑持焦期间面板不得自动收起（编辑保持原因必须激活）。");
            Assert.IsTrue(((MainWindowViewModel)window.DataContext).IsPanelExpanded);

            window.CommitCardTextForTest();
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
            Assert.IsFalse(
                IsPanelEditHoldActive(window),
                "备注会话终结后编辑保持原因必须释放。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardNote_RecycleDuringEditingTerminatesSession()
    {
        using var directory = new TestDirectory();
        var store = new RecordingCardEditStore(directory.Root);
        var board = new BoardService();
        AddScrollableItems(board, BoardCategory.Inbox);
        var anchor = board.Items(BoardCategory.Inbox).ToArray()[1];
        anchor.Note = "滚动锚点备注";
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewer = FindDescendant<ScrollViewer>((ListBox)window.FindName("BoardList"));
            Assert.IsNotNull(viewer);
            Assert.IsTrue(viewer.ScrollableHeight > 0, "前置条件：列表可滚动。");

            RaiseNoteStripClick(window, anchor, "CardNoteEntryButton");
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
            CompleteLayout(window);
            var editor = CardNoteEditorOf(ContainerOf(window, anchor));
            Assert.IsTrue(editor.IsKeyboardFocused, "前置条件：备注编辑器持焦。");
            editor.Text = "滚动后提交的备注";

            // 锚点仍在视口：会话原地保留（两步滚动镜像正文编辑的既有形态）。
            ScrollTo(window, viewer, Math.Min(60d, viewer.ScrollableHeight));
            Assert.IsTrue(
                CardNoteEditorOf(ContainerOf(window, anchor)).IsVisible,
                "锚点仍在视口时滚动不得终结备注会话。");

            // 锚点滚出虚拟化窗口：容器被回收 → 会话终结并提交（与正文编辑同一
            // 锚点判定机器）。虚拟化对未实现容器的高度是估算值：offset 爬升随
            // 实现范围逐步推进，迭代滚动直到收敛（锚点在第 2 张卡，大幅滚动即
            // 进入回收窗口）。
            for (var attempt = 0; attempt < 4; attempt++)
            {
                viewer.ScrollToVerticalOffset(viewer.ScrollableHeight);
                CompleteLayout(window);
            }

            Assert.IsTrue(
                viewer.VerticalOffset >= 1000d,
                $"必须大幅滚离锚点（容器进入回收窗口）：offset={viewer.VerticalOffset:0.#}。");
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
            AssertNoVisibleCardEditor(window);
            Assert.IsFalse(anchor.IsEditingNote, "锚点滚出视口必须终结备注会话。");
            Assert.AreEqual(
                "滚动后提交的备注",
                board.FindItem(anchor.Id)!.Note,
                "锚点滚出终结会话时必须提交备注草稿。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardNote_RightClickSemanticsUnchanged()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("短卡字", BoardCategory.Inbox);
        item.Note = "右键语义备注";
        var window = CreateWindow(directory, board);
        var copied = new List<DataObject>();
        window.ClipboardWriterOverride = dataObject =>
        {
            copied.Add(dataObject);
            return true;
        };

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            window.Topmost = true;
            CompleteLayout(window);
            var savedCursor = SaveCursorPosition();

            try
            {
                // 右键备注行左半（点击面所在处）：左半右键无操作——既不复制、
                // 也绝不进入备注编辑（备注带判定只作用于左键）。
                ClickRealRightAt(
                    window,
                    container,
                    new Point(container.ActualWidth * 0.25, 22d));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(120));
                Assert.IsEmpty(copied, "左半右键不得复制。");
                Assert.IsFalse(item.IsEditingNote, "右键不得进入备注编辑。");

                // 右键右半（命中层右列）：复制这一张（语义零改动）。选点沿用
                // CardHalves 约定（右半区内、避开按钮条与 12px 底部环带）。
                var operationsZone = HalvesZoneOf(container, CardGestureZones.OperationsZone);
                var zoneOrigin = operationsZone.TranslatePoint(new Point(), container);
                ClickRealRightAt(
                    window,
                    container,
                    new Point(
                        zoneOrigin.X + operationsZone.ActualWidth / 2,
                        zoneOrigin.Y + operationsZone.ActualHeight * 0.6));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(120));
                Assert.HasCount(1, copied, "右半右键复制语义不得被备注功能改变。");
                Assert.IsFalse(item.IsEditingNote);
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardNote_CommittedStripShownEvenWhenContentShortens()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText(LongCardText, BoardCategory.Inbox);
        item.Note = "会保留的备注";
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            var noteRow = NoteRowOf(container);
            Assert.AreEqual(Visibility.Visible, noteRow.Visibility, "前置条件：备注行常显。");

            // 正文后来变短（不再截断）：备注行仍常显可编辑——备注是用户数据。
            Assert.IsTrue(
                board.UpdateText(item.Id, "短了"),
                "前置条件：正文缩短成功。");
            CompleteLayout(window);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(120));
            Assert.AreEqual(Visibility.Visible, noteRow.Visibility, "正文变短后备注行必须仍然常显。");
            Assert.AreEqual("会保留的备注", item.Note);
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "note-row-light.png",
                "FTS_NOTE_EVIDENCE_DIR");
            DesignThemeManager.Apply(window, DesignTheme.Dark);
            CompleteLayout(window);
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "note-row-dark.png",
                "FTS_NOTE_EVIDENCE_DIR");
            DesignThemeManager.Apply(window, DesignTheme.Light);
            Assert.IsFalse(
                FindDescendants<TrimAwareTextBlock>(container)
                    .Single(candidate => ReferenceEquals(candidate.DataContext, item))
                    .IsTruncated,
                "前置核验：正文此时已不截断（编辑入口无门槛的对照面）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardNote_PinnedAndSelectedCardsKeepNoteRow()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("短卡字", BoardCategory.Inbox);
        item.Note = "置顶选中共存备注";
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            item.IsPinned = true;
            ((ListBox)window.FindName("BoardList")).SelectedItems.Add(item);
            CompleteLayout(window);

            Assert.AreEqual(Visibility.Visible, NoteRowOf(container).Visibility, "置顶/选中卡片的备注行必须常显。");
            Assert.IsTrue(
                CardNoteEntryButtonOf(container).IsHitTestVisible,
                "选中卡片的备注编辑入口无门槛（点击面仍可命中）。");
            Assert.IsTrue(
                FadeAnimation.GetIsActive(FindDescendants<Border>(container).Single(layer => layer.Name == "CardSelectedFillLayer")),
                "前置核验：选中着色处于激活态（与备注行共存）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardNote_StripEntry_SelectedTruncatedCardStillShowsOnHover()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText(LongCardText, BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            ((ListBox)window.FindName("BoardList")).SelectedItems.Add(item);
            window.Topmost = true;
            CompleteLayout(window);
            var savedCursor = SaveCursorPosition();

            try
            {
                HoverRealAt(window, container, new Point(24, container.ActualHeight * 0.8));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(150));
                Assert.IsTrue(
                    FadeAnimation.GetIsActive(CardNoteStripButtonOf(container)),
                    "选中的截断卡悬停仍需出现添加备注按钮（右侧按钮组与选中着色共存）。");
                Assert.IsTrue(
                    FadeAnimation.GetIsActive(FindDescendants<Border>(container).Single(layer => layer.Name == "CardSelectedFillLayer")),
                    "前置核验：选中着色处于激活态。");
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardNote_SearchActiveHidesNoteEntries()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var longItem = board.AddText(LongCardText, BoardCategory.Inbox);
        var notedItem = board.AddText("短卡字", BoardCategory.Inbox);
        notedItem.Note = "搜索态备注";
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            window.EnterSearchMode();
            ((TextBox)window.FindName("SearchInput")).Text = "字";
            CompleteLayout(window);
            Assert.IsTrue(((MainWindowViewModel)window.DataContext).IsSearchActive, "前置条件：搜索态。");
            var longContainer = ContainerOf(window, longItem);
            var notedContainer = ContainerOf(window, notedItem);
            window.Topmost = true;
            CompleteLayout(window);
            var savedCursor = SaveCursorPosition();

            try
            {
                HoverRealAt(window, longContainer, new Point(24, longContainer.ActualHeight * 0.8));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(150));
                var stripOnLong = CardNoteStripButtonOf(longContainer);
                Assert.IsFalse(
                    FadeAnimation.GetIsActive(stripOnLong),
                    "搜索态不得出现添加备注入口（搜索态不进入编辑的同族守卫）。");
                Assert.IsFalse(stripOnLong.IsHitTestVisible);
                Assert.AreEqual(Visibility.Collapsed, stripOnLong.Visibility);
                Assert.IsFalse(
                    CardNoteEntryButtonOf(notedContainer).IsHitTestVisible,
                    "搜索态隐藏备注点击面（点击会无响应的死入口不得出现）。");
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardOperationAvoidance_NoteRowReservesOperationInset()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText(LongCardText, BoardCategory.Inbox);
        item.Note = "第一行备注足够长会换到第二行继续显示省略号机制";
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            var noteRow = NoteRowOf(container);
            var pinButton = FindDescendants<Button>(container).Single(candidate =>
                Equals(candidate.CommandParameter, CardGestureZones.TogglePinCommand));

            // #16 零像素争用扩展：备注行与右上操作按钮条共用同一派生内缩来源。
            Assert.AreEqual(
                (Thickness)window.FindResource("CardTextReservedRightInset"),
                noteRow.Margin,
                "备注行右让位必须复用 CardTextReservedRightInset（不得发明第二个数值）。");
            var noteRowRight = noteRow.TranslatePoint(
                new Point(noteRow.ActualWidth, 0),
                container).X;
            var buttonColumnLeft = pinButton.TranslatePoint(new Point(0, 0), container).X;
            Assert.IsTrue(
                noteRowRight <= buttonColumnLeft + 0.5,
                $"备注行右缘（{noteRowRight:0.#}）不得侵入右上操作按钮条（左缘 {buttonColumnLeft:0.#}，零像素争用）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardNote_SyntheticButtonClickEntersEditingWithoutSelection()
    {
        // UIA/合成路径（BoardList_ButtonClick 的 NoteStrip 分支）与手势层真实
        // 输入路径共用 BeginCardNoteEditing（契约 #13 的合成可达侧）。
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("短卡字", BoardCategory.Inbox);
        item.Note = "合成路径备注";
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            var entryButton = CardNoteEntryButtonOf(container);
            entryButton.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
                entryButton));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
            CompleteLayout(window);

            Assert.IsTrue(item.IsEditingNote, "合成 Click 必须经 NoteStrip 分支进入备注编辑。");
            Assert.IsTrue(CardNoteEditorOf(container).IsKeyboardFocused);
            CollectionAssert.AreEqual(
                Array.Empty<BoardItem>(),
                ((ListBox)window.FindName("BoardList")).SelectedItems.Cast<BoardItem>().ToArray(),
                "备注编辑入口不得改变选择。");

            // 1.29.0 扩展：右侧操作条添加备注钮同走 NoteStrip 参数路由（UIA/合成
            // 路径按 CommandParameter 分派，与位置无关，几何零取样点）。
            var stripItem = board.AddText(LongCardText, BoardCategory.Inbox);
            CompleteLayout(window);
            var stripButton = CardNoteStripButtonOf(ContainerOf(window, stripItem));
            stripButton.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
                stripButton));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
            CompleteLayout(window);

            Assert.IsTrue(stripItem.IsEditingNote, "合成 Click 右侧添加备注按钮必须经 NoteStrip 分支进入备注编辑。");
            CollectionAssert.AreEqual(
                Array.Empty<BoardItem>(),
                ((ListBox)window.FindName("BoardList")).SelectedItems.Cast<BoardItem>().ToArray(),
                "右侧添加备注按钮不得改变选择。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardNote_RealInputClickOnStripButtonEntersEditingWithoutChangingSelection()
    {
        // 契约 #13 口径：右侧添加备注按钮必须经完整输入管线（Win32 消息直驱）
        // 真实可点，左键绝不 toggle 选中（OperationsNote 会话语义天然继承）。
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText(LongCardText, BoardCategory.Inbox);
        var other = board.AddText("另一张短卡", BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            window.Topmost = true;
            CompleteLayout(window);
            var stripButton = CardNoteStripButtonOf(container);
            var savedCursor = SaveCursorPosition();

            try
            {
                // 悬停揭示右侧添加备注按钮（截断卡），真实点击进入备注编辑。
                HoverRealAt(window, container, new Point(24, container.ActualHeight * 0.8));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(150));
                Assert.IsTrue(
                    FadeAnimation.GetIsActive(stripButton) && stripButton.IsHitTestVisible,
                    "前置条件：悬停揭示添加备注按钮。");
                var clickPoint = stripButton.TranslatePoint(
                    new Point(stripButton.ActualWidth / 2, stripButton.ActualHeight / 2),
                    container);
                ClickRealAt(window, container, clickPoint, hoverSettleMilliseconds: 120);
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }

            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
            CompleteLayout(window);
            var editor = CardNoteEditorOf(container);
            Assert.IsTrue(item.IsEditingNote, "真实输入点击右侧添加备注按钮必须进入备注编辑。");
            Assert.IsTrue(editor.IsVisible);
            Assert.IsTrue(editor.IsKeyboardFocused, "备注编辑器必须持有键盘焦点。");
            Assert.AreEqual(string.Empty, editor.Text, "无备注卡打开编辑预填空值。");
            CollectionAssert.AreEqual(
                Array.Empty<BoardItem>(),
                ((ListBox)window.FindName("BoardList")).SelectedItems.Cast<BoardItem>().ToArray(),
                "右侧添加备注按钮左键绝不改变选择。");
            Assert.IsFalse(other.IsEditingNote);
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "note-strip-click-editing.png",
                "FTS_NOTE_EVIDENCE_DIR");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardNote_EditorOverlaysNoteRowInPlaceWithIconVisible()
    {
        // 编辑器移回备注行原位（2026-10-12，Issue #121）：编辑态下 CardNoteEditor
        // 位于行0 两列 Grid 的列1、与备注显示 TextBlock 同格且声明序在后
        // （不透明盖写，取代 1.28.0 的行1 盖写正文落位）；图标可见（
        // Note 非空 ∨ IsEditingNote 统一规则）；编辑框左缘与文字列结构化对齐。
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("短卡字", BoardCategory.Inbox);
        item.Note = "原位备注";
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            RaiseNoteStripClick(window, item, "CardNoteEntryButton");
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            var noteRow = (Grid)NoteRowOf(container);
            var editor = CardNoteEditorOf(container);
            var display = noteRow.Children.OfType<TextBlock>().Single();

            Assert.IsTrue(item.IsEditingNote, "前置条件：备注编辑已开启。");
            Assert.IsTrue(editor.IsVisible);
            Assert.AreEqual(0, Grid.GetRow(noteRow), "备注行必须处于卡片网格行0（正文区为行1）。");
            Assert.AreEqual(1, Grid.GetColumn(editor), "编辑器必须位于行0 两列 Grid 的列1（内容列，原位）。");
            Assert.AreEqual(
                1,
                Grid.GetColumn(display),
                "备注显示文字与编辑器必须同列（列1）。");
            Assert.IsTrue(
                noteRow.Children.IndexOf(editor) > noteRow.Children.IndexOf(display),
                "编辑器必须声明在显示 TextBlock 之后（同格渲染在上）。");
            Assert.IsNotNull(editor.Background, "编辑器必须有不透明衬底（同格盖写先例）。");
            Assert.AreEqual(
                window.FindResource("CardBrush"),
                editor.Background,
                "编辑器衬底必须就是卡片底色 CardBrush（同格盖写不透明，显示文字不得透出）。");
            Assert.AreEqual(
                new Thickness(0),
                editor.Padding,
                "编辑器零内边距（文字起笔即左缘，对齐不得有隐藏偏移）。");
            Assert.AreEqual(
                new Thickness(0),
                editor.BorderThickness,
                "编辑器零边框。");
            Assert.AreEqual(
                Visibility.Visible,
                display.Visibility,
                "编辑期间显示态保持可见（不透明盖写，非显隐互斥切换）。");

            var icon = FindDescendants<Viewbox>(noteRow).Single();
            Assert.IsTrue(icon.IsVisible, "编辑已有备注时备注图标必须保持可见（统一可见性规则）。");
            var editorOrigin = editor.TranslatePoint(new Point(0, 0), container);
            var displayOrigin = display.TranslatePoint(new Point(0, 0), container);
            Assert.AreEqual(
                displayOrigin.X,
                editorOrigin.X,
                0.5,
                "编辑框左缘必须与备注文字同列像素对齐（结构化对齐，随图标列宽度派生）。");
            Assert.AreEqual(
                displayOrigin.Y,
                editorOrigin.Y,
                0.5,
                "编辑框与备注文字同格顶部对齐（VerticalAlignment=Top）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardNote_EmptyNoteEditingExpandsRowBoundedThenFallsBack()
    {
        // D3/D6 同一规则：无备注卡进入备注编辑（添加备注钮入口），编辑器同样
        // 出现在行0 备注位，行0 有界长高（≤2 行/40px）；空白提交（无备注空白
        // =无操作）后回落塌缩、卡片高度还原——无「有/无备注」条件分叉。
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText(LongCardText, BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            var noteRow = NoteRowOf(container);
            var heightBefore = container.ActualHeight;
            Assert.AreEqual(Visibility.Collapsed, noteRow.Visibility, "前置条件：无备注卡行0塌缩。");

            RaiseNoteStripClick(window, item, "CardOperationNoteButton");
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
            CompleteLayout(window);
            var editor = CardNoteEditorOf(container);

            Assert.IsTrue(item.IsEditingNote, "前置条件：备注编辑已开启。");
            Assert.IsTrue(editor.IsVisible);
            Assert.AreEqual(
                Visibility.Visible,
                noteRow.Visibility,
                "无备注卡编辑期行0必须展开（与编辑已有备注同一规则）。");
            Assert.IsTrue(
                FindDescendants<Viewbox>(noteRow).Single().IsVisible,
                "无备注卡编辑期图标同样可见（统一可见性规则）。");
            var growth = container.ActualHeight - heightBefore;
            Assert.IsTrue(growth > 0, "行0 展开必须撑高卡片。");
            Assert.IsTrue(growth <= 40.5, $"行0 长高必须有界（≤40 DIP）：实际 {growth:0.#}。");

            RaiseCardEditorKey(window, editor, Key.Enter);
            PumpUntil(window, () => !item.IsEditingNote, "空白提交必须退出编辑态（无备注空白=无操作）。");
            CompleteLayout(window);
            Assert.IsNull(item.Note);
            Assert.AreEqual(
                Visibility.Collapsed,
                NoteRowOf(container).Visibility,
                "编辑结束后行0必须回落塌缩。");
            Assert.AreEqual(
                heightBefore,
                container.ActualHeight,
                0.5,
                "编辑结束后卡片高度必须回落原值。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardNote_RealClickPlacesCaretNearClickPoint()
    {
        // D4：真实输入点击备注进入编辑，光标定位到点击处（第 5 字附近，±1 字
        // 容差吸收字体度量差）；Win32 消息携带的按下位经手势层传入编辑器，
        // GetCharacterIndexFromPoint 换算 CaretIndex。
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("短卡字", BoardCategory.Inbox);
        item.Note = "一二三四五六七八九十";
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            window.Topmost = true;
            CompleteLayout(window);
            var display = ((Grid)NoteRowOf(container)).Children.OfType<TextBlock>().Single();
            var textOrigin = display.TranslatePoint(new Point(0, 0), container);
            // 备注字号 12：按显示元素实际字体量测每字符宽度（T7 评审：不依赖
            // 「汉字宽=字号」假定），点位取第 5 字（索引 4）中部。
            var charWidth = MeasuredCharWidth(display, item.Note);
            var clickPoint = new Point(textOrigin.X + 4.5 * charWidth, textOrigin.Y + 10);
            var savedCursor = SaveCursorPosition();

            try
            {
                ClickRealAt(window, container, clickPoint, hoverSettleMilliseconds: 120);
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }

            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
            CompleteLayout(window);
            var editor = CardNoteEditorOf(container);
            Assert.IsTrue(item.IsEditingNote, "前置条件：真实点击进入备注编辑。");
            Assert.IsTrue(editor.IsKeyboardFocused, "备注编辑器必须持有键盘焦点。");
            Assert.IsTrue(
                Math.Abs(editor.CaretIndex - 4) <= 1,
                $"光标必须命中点击处（索引 4 附近）：实际 CaretIndex={editor.CaretIndex}。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardNote_SyntheticEntryPlacesCaretAtTextEnd()
    {
        // D4 无点位回退：合成/UIA 路径（BoardList_ButtonClick 的 NoteStrip 分支）
        // 传 null 点位，光标回退文末（取代旧 SelectAll 语义）。
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("短卡字", BoardCategory.Inbox);
        item.Note = "文末回退备注";
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            var entryButton = CardNoteEntryButtonOf(container);
            entryButton.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
                entryButton));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
            CompleteLayout(window);
            var editor = CardNoteEditorOf(container);

            Assert.IsTrue(item.IsEditingNote, "前置条件：合成路径进入备注编辑。");
            Assert.IsTrue(editor.IsKeyboardFocused);
            Assert.AreEqual(
                editor.Text.Length,
                editor.CaretIndex,
                "合成路径无点位来源，光标必须回退文末。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardOperationMatrix_NoteButtonRow()
    {
        // 契约 #19 扩展：备注钮行——图片卡恒无（Kind 门控）；已置顶 ∧ 未选中 ∧
        // 悬停照常揭示（与置顶/删除钮共存）；备注编辑期塌缩（IsEditingNote 门）。
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText(LongCardText, BoardCategory.Inbox);
        var imageItem = AddImageCard(directory, board, "note-matrix.png");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            var imageContainer = ContainerOf(window, imageItem);
            window.Topmost = true;
            CompleteLayout(window);
            var stripButton = CardNoteStripButtonOf(container);
            var imageStrip = CardNoteStripButtonOf(imageContainer);
            var savedCursor = SaveCursorPosition();

            try
            {
                // 图片卡：Kind 门控——悬停也不出现。
                HoverRealAt(window, imageContainer, new Point(24, imageContainer.ActualHeight * 0.8));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(120));
                Assert.IsFalse(FadeAnimation.GetIsActive(imageStrip), "图片卡悬停不得出现添加备注按钮。");
                Assert.AreEqual(Visibility.Collapsed, imageStrip.Visibility);

                // 已置顶 ∧ 未选中 ∧ 悬停：备注钮照常揭示。
                item.IsPinned = true;
                CompleteLayout(window);
                HoverRealAt(window, container, new Point(24, container.ActualHeight * 0.8));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(120));
                Assert.IsTrue(FadeAnimation.GetIsActive(stripButton), "已置顶未选中卡片悬停时添加备注按钮照常揭示。");
                Assert.AreEqual(Visibility.Visible, stripButton.Visibility);

                // 备注编辑期塌缩（真实路径经新按钮进入编辑后，按钮随即隐藏）。
                RaiseNoteStripClick(window, item, "CardOperationNoteButton");
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(120));
                Assert.IsTrue(item.IsEditingNote, "前置条件：备注编辑已开启。");
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
                Assert.AreEqual(Visibility.Collapsed, stripButton.Visibility, "备注编辑期添加备注按钮必须塌缩。");
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardNote_PersistsAcrossSaveAndLoad()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var board = new BoardService();
        var text = board.AddText("持久化文本", BoardCategory.Inbox);
        text.Note = "会话备注";
        var store = new LocalStore(paths, new AtomicTextWriter());

        store.SaveBoardAsync(board.CreateSnapshot()).GetAwaiter().GetResult();
        var reloaded = new LocalStore(paths, new AtomicTextWriter())
            .LoadBoardAsync()
            .GetAwaiter()
            .GetResult();
        var loaded = reloaded.Items.Single(candidate => candidate.Id == text.Id);
        Assert.AreEqual("会话备注", loaded.Note, "备注必须随 board.json 原子往返。");
        Assert.IsNull(loaded.DraftNote, "会话草稿不得持久化。");
        Assert.IsFalse(loaded.IsEditingNote, "会话状态不得持久化。");
    }

    [STATestMethod]
    public void CardNote_MissingFieldLoadsAsNull()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var board = new BoardService();
        var text = board.AddText("旧版本写入", BoardCategory.Inbox);
        text.Note = "将被剥掉的备注";
        var store = new LocalStore(paths, new AtomicTextWriter());
        store.SaveBoardAsync(board.CreateSnapshot()).GetAwaiter().GetResult();

        // 模拟旧版本写入的 board.json：剥掉 note 成员（缺字段取默认的既有约定）。
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(paths.BoardFile))!;
        node["items"]!.AsArray().Single()!.AsObject().Remove("note");
        File.WriteAllText(paths.BoardFile, node.ToJsonString());

        var reloaded = new LocalStore(paths, new AtomicTextWriter())
            .LoadBoardAsync()
            .GetAwaiter()
            .GetResult();
        var loaded = reloaded.Items.Single(candidate => candidate.Id == text.Id);
        Assert.IsNull(loaded.Note, "旧文件缺 note 字段必须读为无备注（免迁移）。");
        Assert.AreEqual("旧版本写入", loaded.Text, "其余字段不受影响。");
    }
}
