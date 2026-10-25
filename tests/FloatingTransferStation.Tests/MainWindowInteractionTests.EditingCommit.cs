using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 「点击编辑器外部＝提交」共享协调（MainWindow.EditingCommit.cs）的 STA 测试矩阵：
/// 四处编辑位（分类名/瓦片名/卡片正文/卡片备注）「编辑中→点击面板空白区→已保存」
/// 合成事件路径＋卡片正文一处真实 SendInput 点击路径；复盘编辑器点击面板空白
/// 立即冲刷（不等 700ms 防抖）；IME 组合期点击外部组合文本不丢失（既有组合标记
/// 技法）；Esc 取消语义、拖拽起手不误触、真实键盘导航（F2/Esc）零回归；协调器
/// 不吞事件（e.Handled 不置位——按压继续原旅程）。既有提交/取消语义本身不变，
/// 只补触发通道；独立对话框类编辑窗不在此列。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    /// <summary>标签栏空白（面板空白区的代表命中源：改名编辑器之外、列表之外——
    /// 本通道新增覆盖的按压区，不与列表内既有冲刷路径重叠）。</summary>
    private static System.Windows.Controls.Border PanelBlankRail(MainWindow window) =>
        (System.Windows.Controls.Border)window.FindName("CategoryRail")!;

    private static System.Windows.Controls.Border LauncherBlankHost(MainWindow window) =>
        (System.Windows.Controls.Border)window.FindName("LauncherContentHost")!;

    /// <summary>在指定元素上发出一次合成预览左键按下（隧道自窗口根起，触达
    /// EditingCommit 协调器；不抬起、不置 Handled——由调用方断言旅程行为）。
    /// 事件用 <see cref="Mouse.PreviewMouseDownEvent"/>（真隧道，且逐节点联动
    /// 调用 PreviewMouseLeftButtonDown 处理器——仓内手势层合成先例同款）：
    /// UIElement.PreviewMouseLeftButtonDownEvent 注册为 Direct，合成 RaiseEvent
    /// 只触达命中元素自身、到不了窗口级挂接。</summary>
    private static MouseButtonEventArgs RaisePanelBlankPress(UIElement blankSource)
    {
        var press = NewMouseButtonEventArgs(
            Mouse.PreviewMouseDownEvent,
            blankSource);
        blankSource.RaiseEvent(press);
        return press;
    }

    /// <summary>同 <see cref="RaisePanelBlankPress"/>，但经 dispatcher 操作派发
    /// （真实输入的等价形态：真实按压本就是分发器消息，回调执行期间
    /// SynchronizationContext.Current 为 DispatcherSynchronizationContext——
    /// 复盘冲刷等「await 后回访 UI」的路径依赖它把延续送回分发器线程；
    /// STA 测试线程直发没有该上下文，延续会落到线程池并跨线程访问 UI）。</summary>
    private static MouseButtonEventArgs RaisePanelBlankPressViaDispatcher(
        MainWindow window,
        UIElement blankSource)
    {
        MouseButtonEventArgs? press = null;
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(
            DispatcherPriority.Send,
            new Action(() =>
            {
                try
                {
                    press = RaisePanelBlankPress(blankSource);
                }
                finally
                {
                    frame.Continue = false;
                }
            }));
        Dispatcher.PushFrame(frame);
        return press!;
    }

    // ---- 四处编辑位：编辑中→点击面板空白→已保存（合成事件路径） ----

    [STATestMethod]
    public void EditingCommit_CategoryName_PanelBlankPressCommitsDraft()
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
            var inbox = viewModel.Categories.Single(
                category => category.Category == BoardCategory.Inbox);
            inbox.BeginNameEdit();
            CompleteLayout(window);
            var editor = FindDescendants<TextBox>(FindCategoryTab(window, inbox)).Single();
            editor.Focus();
            editor.Text = "空白提交";
            CompleteLayout(window);
            SaveVisualEvidence(
                (System.Windows.Controls.Border)window.FindName("WindowShell")!,
                "01-editing-commit-category-editing.png",
                "FTS_EDITING_COMMIT_EVIDENCE_DIR");

            var press = RaisePanelBlankPress(PanelBlankRail(window));
            CompleteLayout(window);

            Assert.IsFalse(press.Handled, "协调器只补触发通道，不吞按压事件。");
            Assert.IsFalse(inbox.IsEditingName, "点击面板空白必须终结改名会话。");
            InvokePrivateTask(window, "DrainPendingOperationsAsync");
            Assert.AreEqual("空白提交", inbox.DisplayName);
            SaveVisualEvidence(
                (System.Windows.Controls.Border)window.FindName("WindowShell")!,
                "02-editing-commit-category-committed.png",
                "FTS_EDITING_COMMIT_EVIDENCE_DIR");
            Assert.AreEqual(
                "空白提交",
                store.LastSavedSettings!.CategoryName(BoardCategory.Inbox),
                "点击面板空白的提交必须持久化分类名。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void EditingCommit_TileName_PanelBlankPressCommitsDraft()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var item = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Url, "https://example.com/", "example.com", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, item);

        try
        {
            window.Show();
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();
            var tile = FindLauncherTile(window, catalog.Items.Single());
            Assert.IsNotNull(tile);
            InvokePrivate(window, "BeginLauncherTileRename", catalog.Items.Single());
            CompleteLayout(window);
            var editor = FindDescendants<TextBox>(tile).Single();
            editor.Text = "面板空白提交的瓦片名";

            RaisePanelBlankPress(LauncherBlankHost(window));
            CompleteLayout(window);
            InvokePrivateTask(window, "DrainPendingOperationsAsync");

            Assert.IsNull(
                GetPrivateField<LauncherItem?>(window, "_launcherRenamingItem"),
                "点击面板空白必须终结改名会话。");
            Assert.AreEqual(
                "面板空白提交的瓦片名",
                catalog.Items.Single().DisplayName,
                "点击面板空白的提交必须更新瓦片名。");
            var persisted = store.LoadAsync().GetAwaiter().GetResult();
            Assert.AreEqual(
                "面板空白提交的瓦片名",
                persisted.Items.Single().DisplayName,
                "点击面板空白的提交必须持久化瓦片名。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void EditingCommit_CardText_PanelBlankPressCommitsDraft()
    {
        using var directory = new TestDirectory();
        var store = new RecordingCardEditStore(directory.Root);
        var board = new BoardService();
        var item = board.AddText("原始正文", BoardCategory.Inbox);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            EnterCardEditingWithDoubleClick(window, item);
            CompleteLayout(window);
            var editor = EditorInput(window, item);
            editor.Text = "面板空白提交的正文";

            // 命中源=标签栏空白：列表之外的按压此前没有任何冲刷通道（本通道
            // 新增覆盖面）；编辑卡容器内按压仍由卡片手势层按契约 #22 管辖。
            RaisePanelBlankPress(PanelBlankRail(window));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
            CompleteLayout(window);
            InvokePrivateTask(window, "DrainPendingOperationsAsync");

            Assert.AreEqual(
                "面板空白提交的正文",
                board.FindItem(item.Id)!.Text,
                "点击面板空白必须提交卡片正文。");
            AssertNoVisibleCardEditor(window);
            Assert.AreEqual(1, store.SaveCount, "提交必须走原子持久化。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void EditingCommit_CardNote_PanelBlankPressCommitsDraft()
    {
        using var directory = new TestDirectory();
        var store = new RecordingCardEditStore(directory.Root);
        var board = new BoardService();
        var item = board.AddText("带备注的卡", BoardCategory.Inbox);
        item.Note = "旧备注";
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
            Assert.IsTrue(item.IsEditingNote);
            editor.Text = "面板空白提交的备注";

            RaisePanelBlankPress(PanelBlankRail(window));
            PumpUntil(window, () => item.Note == "面板空白提交的备注", "点击面板空白必须提交卡片备注。");
            CompleteLayout(window);
            InvokePrivateTask(window, "DrainPendingOperationsAsync");

            Assert.IsFalse(item.IsEditingNote, "提交后必须退出备注编辑态。");
            Assert.AreEqual(1, store.SaveCount, "备注提交必须走与正文同一条原子持久化。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    // ---- 真实 SendInput 点击路径（契约 #13 口径：硬件级注入经完整输入管线） ----

    [STATestMethod]
    public void EditingCommit_CardText_RealClickPanelBlankCommits()
    {
        using var directory = new TestDirectory();
        var store = new RecordingCardEditStore(directory.Root);
        var board = new BoardService();
        var item = board.AddText("真实点击前的正文", BoardCategory.Inbox);
        var window = CreateWindow(board, store, WindowSettings.Default);
        var savedCursor = SaveCursorPosition();

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            window.Topmost = true;
            CompleteLayout(window);

            EnterCardEditingWithDoubleClick(window, item);
            CompleteLayout(window);
            var editor = EditorInput(window, item);
            editor.Text = "真实点击提交的正文";

            // 真实光标停靠列表空白区（单张短卡之下、避开右缘滚动条），注入真实
            // 按下/抬起：预览隧道先触达协调器提交编辑，再走完原生按压旅程。
            var list = (ListBox)window.FindName("BoardList");
            HoverRealAt(
                window,
                list,
                new Point(list.ActualWidth / 2 - 30, list.ActualHeight - 12));
            ClickBySendInput(window);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            CompleteLayout(window);
            InvokePrivateTask(window, "DrainPendingOperationsAsync");

            Assert.AreEqual(
                "真实点击提交的正文",
                board.FindItem(item.Id)!.Text,
                "真实点击面板空白必须提交卡片正文。");
            AssertNoVisibleCardEditor(window);
            SaveVisualEvidence(
                (System.Windows.Controls.Border)window.FindName("WindowShell")!,
                "03-editing-commit-card-real-click-committed.png",
                "FTS_EDITING_COMMIT_EVIDENCE_DIR");
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindowWithoutSaving(window);
        }
    }

    // ---- 复盘编辑器：点击面板空白→立即冲刷保存（不等 700ms 防抖） ----

    [STATestMethod]
    public void EditingCommit_ReviewEditor_PanelBlankPressFlushesWithoutDebounce()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        using var store = new LocalStore(paths, new AtomicTextWriter());
        var board = new BoardService();
        var settings = WindowSettings.Default with
        {
            ReviewMigrationVersion = DailyReviewMigration.CurrentVersion,
            CategoryNames = WindowSettings.Default.WithCategoryName(BoardCategory.Reference, "复盘").CategoryNames
        };
        var window = CreateWindow(board, store, settings, new DefaultCaptureCategoryState());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);
            var editor = (TextBox)window.FindName("ReviewEditor");
            Assert.IsNotNull(editor);

            editor.Text = "点击空白立即保存的复盘";
            CompleteLayout(window);
            var saveTimer = GetPrivateField<DispatcherTimer>(window, "_reviewSaveTimer");
            Assert.IsTrue(saveTimer.IsEnabled, "前置条件：输入后 700ms 防抖计时器在计时。");

            RaisePanelBlankPressViaDispatcher(
                window,
                (UIElement)window.FindName("ReviewContentHost")!);

            Assert.IsFalse(
                saveTimer.IsEnabled,
                "点击面板空白必须立即冲刷（停表），而不是等 700ms 防抖到点。");
            InvokePrivateTask(window, "DrainPendingOperationsAsync");
            var saved = store.LoadAsync(DateOnly.FromDateTime(DateTime.Now)).GetAwaiter().GetResult();
            Assert.AreEqual(
                "点击空白立即保存的复盘",
                saved.Content,
                "冲刷必须已落盘（未等防抖）。");
            Assert.IsTrue(
                ((TextBlock)window.FindName("ReviewStatus")!).Text.StartsWith("已保存", StringComparison.Ordinal),
                "冲刷完成后状态条必须报告已保存。");
        }
        finally
        {
            GetPrivateField<IDailyReviewStore>(window, "_dailyReviews")?.StopWatching();
            CloseWindowWithoutSaving(window);
        }
    }

    // ---- IME 组合期点击外部：组合文本不丢失（既有组合标记技法） ----

    [STATestMethod]
    public void EditingCommit_CategoryName_CompositionTextSurvivesPanelBlankCommit()
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
            var inbox = viewModel.Categories.Single(
                category => category.Category == BoardCategory.Inbox);
            inbox.BeginNameEdit();
            CompleteLayout(window);
            var editor = FindDescendants<TextBox>(FindCategoryTab(window, inbox)).Single();

            // 组合进行中（标记技法）：组合期文本已在编辑器 Text 里（WPF 实况）。
            editor.RaiseEvent(NewTextCompositionEventArgs(
                TextCompositionManager.PreviewTextInputStartEvent,
                editor,
                "zuhe"));
            editor.Text = "组合文字";
            Assert.IsTrue(
                GetPrivateField<HashSet<TextBox>>(window, "_activeCategoryNameCompositions")
                    .Contains(editor),
                "前置条件：组合标记已置位。");

            RaisePanelBlankPress(PanelBlankRail(window));
            CompleteLayout(window);
            InvokePrivateTask(window, "DrainPendingOperationsAsync");

            Assert.AreEqual(
                "组合文字",
                inbox.DisplayName,
                "组合期点击外部必须把组合文本一并保存，不得丢失。");
            Assert.AreEqual(
                "组合文字",
                store.LastSavedSettings!.CategoryName(BoardCategory.Inbox));
            Assert.IsFalse(
                GetPrivateField<HashSet<TextBox>>(window, "_activeCategoryNameCompositions")
                    .Contains(editor),
                "会话终结后组合标记必须清理（防下次 Enter/Esc 失效）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void EditingCommit_CardText_CompositionTextSurvivesPanelBlankCommit()
    {
        using var directory = new TestDirectory();
        var store = new RecordingCardEditStore(directory.Root);
        var board = new BoardService();
        var item = board.AddText("组合前的正文", BoardCategory.Inbox);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            EnterCardEditingWithDoubleClick(window, item);
            CompleteLayout(window);
            var editor = EditorInput(window, item);
            editor.RaiseEvent(NewTextCompositionEventArgs(
                TextCompositionManager.PreviewTextInputStartEvent,
                editor,
                "zuhe"));
            editor.Text = "组合中的正文";
            Assert.IsTrue(
                GetPrivateField<HashSet<BoardItem>>(window, "_activeCardTextCompositions")
                    .Contains(item),
                "前置条件：组合标记已置位。");

            RaisePanelBlankPress(PanelBlankRail(window));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
            CompleteLayout(window);

            Assert.AreEqual(
                "组合中的正文",
                board.FindItem(item.Id)!.Text,
                "组合期点击外部必须把组合文本一并提交，不得丢失。");
            Assert.IsFalse(
                GetPrivateField<HashSet<BoardItem>>(window, "_activeCardTextCompositions")
                    .Contains(item),
                "会话终结后组合标记必须随提交清理（契约 #22 语义）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void EditingCommit_TileName_CompositionTextSurvivesPanelBlankCommit()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var item = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Url, "https://example.org/", "example.org", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, item);

        try
        {
            window.Show();
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();
            var tile = FindLauncherTile(window, catalog.Items.Single());
            Assert.IsNotNull(tile);
            InvokePrivate(window, "BeginLauncherTileRename", catalog.Items.Single());
            CompleteLayout(window);
            var editor = FindDescendants<TextBox>(tile).Single();

            editor.RaiseEvent(NewTextCompositionEventArgs(
                TextCompositionManager.PreviewTextInputStartEvent,
                editor,
                "zuhe"));
            editor.Text = "组合中的瓦片名";
            Assert.IsTrue(
                GetPrivateField<bool>(window, "_launcherRenameComposing"),
                "前置条件：组合标记已置位。");

            RaisePanelBlankPress(LauncherBlankHost(window));
            CompleteLayout(window);
            InvokePrivateTask(window, "DrainPendingOperationsAsync");

            Assert.AreEqual(
                "组合中的瓦片名",
                catalog.Items.Single().DisplayName,
                "组合期点击外部必须把组合文本一并保存（Explicit 绑定推回取到即时文本）。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void EditingCommit_ReviewEditor_CompositionTextFlushedOnPanelBlankPress()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        using var store = new LocalStore(paths, new AtomicTextWriter());
        var board = new BoardService();
        var settings = WindowSettings.Default with
        {
            ReviewMigrationVersion = DailyReviewMigration.CurrentVersion,
            CategoryNames = WindowSettings.Default.WithCategoryName(BoardCategory.Reference, "复盘").CategoryNames
        };
        var window = CreateWindow(board, store, settings, new DefaultCaptureCategoryState());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);
            var editor = (TextBox)window.FindName("ReviewEditor");
            Assert.IsNotNull(editor);

            editor.RaiseEvent(NewTextCompositionEventArgs(
                TextCompositionManager.PreviewTextInputStartEvent,
                editor,
                "zuhe"));
            editor.Text = "组合中的复盘";

            RaisePanelBlankPressViaDispatcher(
                window,
                (UIElement)window.FindName("ReviewContentHost")!);
            InvokePrivateTask(window, "DrainPendingOperationsAsync");

            var saved = store.LoadAsync(DateOnly.FromDateTime(DateTime.Now)).GetAwaiter().GetResult();
            Assert.AreEqual(
                "组合中的复盘",
                saved.Content,
                "组合期点击外部的冲刷必须包含组合文本，不得丢失。");
        }
        finally
        {
            GetPrivateField<IDailyReviewStore>(window, "_dailyReviews")?.StopWatching();
            CloseWindowWithoutSaving(window);
        }
    }

    // ---- 编辑器内/既有管辖面：不误触 ----

    [STATestMethod]
    public void EditingCommit_PressInsideEditorKeepsSession()
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
            var inbox = viewModel.Categories.Single(
                category => category.Category == BoardCategory.Inbox);
            inbox.BeginNameEdit();
            CompleteLayout(window);
            var editor = FindDescendants<TextBox>(FindCategoryTab(window, inbox)).Single();
            editor.Text = "器内草稿";

            RaisePanelBlankPress(editor);
            CompleteLayout(window);

            Assert.IsTrue(inbox.IsEditingName, "编辑器内的按压交给编辑器（放光标/选择），不触发提交。");
            Assert.IsNull(store.LastSavedSettings, "编辑器内的按压不得提交。");

            // 反向证明同一会话可被面板空白按压提交（排除「会话本就提交不了」的假绿）。
            RaisePanelBlankPress(PanelBlankRail(window));
            CompleteLayout(window);
            Assert.IsFalse(inbox.IsEditingName);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    // ---- Esc 取消语义（既有语义零回归） ----

    [STATestMethod]
    public void EditingCommit_EscCancelSemanticsUnchangedAfterPointerChannel()
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
            var inbox = viewModel.Categories.Single(
                category => category.Category == BoardCategory.Inbox);

            // 先经新通道提交一次（点击面板空白）。
            inbox.BeginNameEdit();
            CompleteLayout(window);
            var editor = FindDescendants<TextBox>(FindCategoryTab(window, inbox)).Single();
            editor.Text = "通道提交";
            RaisePanelBlankPress(PanelBlankRail(window));
            CompleteLayout(window);
            InvokePrivateTask(window, "DrainPendingOperationsAsync");
            Assert.AreEqual("通道提交", inbox.DisplayName);

            // 新会话按 Esc：取消语义原样（草稿丢弃、名称保持）。
            inbox.BeginNameEdit();
            CompleteLayout(window);
            editor = FindDescendants<TextBox>(FindCategoryTab(window, inbox)).Single();
            editor.Text = "取消候选";
            var escape = new KeyEventArgs(
                Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window)!,
                Environment.TickCount,
                Key.Escape)
            {
                RoutedEvent = Keyboard.KeyDownEvent,
                Source = editor
            };
            InvokePrivate(window, "CategoryNameEditor_KeyDown", editor, escape);
            CompleteLayout(window);

            Assert.IsTrue(escape.Handled, "Esc 必须仍被改名编辑器消费。");
            Assert.IsFalse(inbox.IsEditingName, "Esc 仍退出编辑态。");
            Assert.AreEqual("通道提交", inbox.DisplayName, "Esc 取消不得改动已提交名称。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    // ---- 拖拽起手不误触（提交后按压仍按普通按压接管、拖拽可发起） ----

    [STATestMethod]
    public void EditingCommit_CardEditing_PressOtherCardCommitsAndKeepsGesturePress()
    {
        using var directory = new TestDirectory();
        var store = new RecordingCardEditStore(directory.Root);
        var board = new BoardService();
        var editing = board.AddText("编辑中的卡", BoardCategory.Inbox);
        var other = board.AddText("另一张卡", BoardCategory.Inbox);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            EnterCardEditingWithDoubleClick(window, editing);
            CompleteLayout(window);
            EditorInput(window, editing).Text = "按压前提交的正文";

            // 在另一张卡的左半（拖拽发起区）按压：协调器先提交，手势层照常
            // 接管按压（Handled=true＝普通按压旅程，拖拽起手不误触）。
            var otherZone = ZoneHitSource(window, other, CardGestureZones.ContentZone);
            var down = NewMouseButtonEventArgs(
                Mouse.PreviewMouseDownEvent,
                otherZone,
                NextIsolatedClickTimestamp());
            otherZone.RaiseEvent(down);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
            CompleteLayout(window);

            Assert.AreEqual(
                "按压前提交的正文",
                board.FindItem(editing.Id)!.Text,
                "按压另一张卡必须先提交编辑中的卡。");
            Assert.IsTrue(down.Handled, "提交后的按压仍须由手势层按普通按压接管。");
            otherZone.RaiseEvent(NewMouseButtonEventArgs(
                Mouse.PreviewMouseUpEvent,
                otherZone,
                NextIsolatedClickTimestamp()));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void EditingCommit_TileRename_PressOtherTileCommitsAndDragStillStarts()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var seededRenamed = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Url, "https://example.com/", "改名前", DateTimeOffset.UtcNow);
        var seededDragged = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Url, "https://example.org/", "另一瓦片", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, seededRenamed, seededDragged);
        var savedCursor = SaveCursorPosition();

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();
            var renamed = catalog.Items.Single(entry => entry.Id == seededRenamed.Id);
            var dragged = catalog.Items.Single(entry => entry.Id == seededDragged.Id);
            window.Topmost = true;
            CompleteLayout(window);

            InvokePrivate(window, "BeginLauncherTileRename", renamed);
            CompleteLayout(window);
            var renameTile = FindLauncherTile(window, renamed);
            Assert.IsNotNull(renameTile);
            var renameEditor = FindDescendants<TextBox>(renameTile).Single();
            renameEditor.Text = "改名后";

            // 真实路由形态：预览按下（隧道，协调器在此提交改名）→ 冒泡按下（瓦片
            // 记录按压）→ 位移 >4 DIP 的移动（拖拽起手）。
            var dragTile = FindLauncherTile(window, dragged);
            Assert.IsNotNull(dragTile);
            var pressPoint = dragTile.PointToScreen(new Point(
                dragTile.ActualWidth / 2,
                dragTile.ActualHeight / 2));
            SetCursorPos((int)pressPoint.X, (int)pressPoint.Y);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));
            Assert.AreEqual("改名后", renameEditor.Text, "按压前编辑器文本保持新名。");
            dragTile.RaiseEvent(NewMouseButtonEventArgs(
                Mouse.PreviewMouseDownEvent, dragTile));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));
            Assert.IsNull(
                GetPrivateField<LauncherItem?>(window, "_launcherRenamingItem"),
                "预览按下必须已终结改名会话。");
            Assert.AreEqual("改名后", renamed.DraftName, "提交推回的草稿必须是新名。");
            dragTile.RaiseEvent(NewMouseButtonEventArgs(
                UIElement.MouseLeftButtonDownEvent, dragTile));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));
            InvokePrivateTask(window, "DrainPendingOperationsAsync");

            Assert.AreEqual(
                "改名后",
                catalog.Items.Single(entry => entry.Id == renamed.Id).DisplayName,
                "按压另一瓦片（预览隧道）必须先提交改名。");
            Assert.AreEqual(
                dragged.Id,
                GetPrivateField<Guid?>(window, "_launcherPressItemId"),
                "提交后的按压必须照常记录瓦片按压会话。");

            var movePoint = dragTile.PointToScreen(new Point(
                dragTile.ActualWidth / 2 + 30,
                dragTile.ActualHeight / 2 + 6));
            SetCursorPos((int)movePoint.X, (int)movePoint.Y);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
            dragTile.RaiseEvent(NewMouseEventArgs(
                UIElement.PreviewMouseMoveEvent, dragTile));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));

            Assert.AreEqual(
                dragged.Id,
                GetPrivateField<Guid?>(window, "_launcherDraggingId"),
                "改名提交后另一瓦片的拖拽起手不得被误触（照常发起）。");
            dragTile.ReleaseMouseCapture();
            InvokePrivate(window, "EndLauncherTileDrag", false);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(60));
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindowWithoutSaving(window);
        }
    }

    // ---- 真实键盘导航零回归（F2/Esc 旅程在新通道并存下原样） ----

    [STATestMethod]
    public void EditingCommit_KeyboardF2AndEscStillWorkAfterPointerCommit()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var item = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Url, "https://example.com/", "键盘瓦片", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, item);

        try
        {
            window.Show();
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();
            InvokePrivate(window, "BeginLauncherTileRename", catalog.Items.Single());
            CompleteLayout(window);
            var tile = FindLauncherTile(window, catalog.Items.Single());
            Assert.IsNotNull(tile);
            var editor = FindDescendants<TextBox>(tile).Single();
            editor.Text = "键盘旅程提交的瓦片名";

            RaisePanelBlankPress(LauncherBlankHost(window));
            CompleteLayout(window);
            InvokePrivateTask(window, "DrainPendingOperationsAsync");
            Assert.AreEqual("键盘旅程提交的瓦片名", catalog.Items.Single().DisplayName);

            // F2 经窗口级键盘处理重新进入改名（焦点项不变，键盘导航零回归）。
            window.RaiseEvent(NewKeyEventArgs(window, Key.F2));
            CompleteLayout(window);
            Assert.IsTrue(catalog.Items.Single().IsEditingName, "F2 仍可进入改名。");
            editor = FindDescendants<TextBox>(tile).Single();
            Assert.IsTrue(editor.IsVisible, "F2 进入后编辑器可见。");

            // Esc 仍取消（编辑器层短路，草稿丢弃）。
            editor.RaiseEvent(new KeyEventArgs(
                Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window)!,
                Environment.TickCount,
                Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
                Source = editor
            });
            CompleteLayout(window);

            Assert.IsFalse(catalog.Items.Single().IsEditingName, "Esc 仍取消改名会话。");
            Assert.AreEqual(
                "键盘旅程提交的瓦片名",
                catalog.Items.Single().DisplayName,
                "Esc 取消不得改动已提交名称。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }
}
