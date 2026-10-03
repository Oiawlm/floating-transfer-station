using System.Globalization;
using System.Windows;
using System.Windows.Controls;
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
    public void ReviewCategory_ShowsEditorAndKeepsClipboardTargetUnchanged()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        using var store = new LocalStore(paths, new AtomicTextWriter());
        var board = new BoardService();
        var captureTarget = new DefaultCaptureCategoryState();
        captureTarget.Set(BoardCategory.Inbox);
        var settings = WindowSettings.Default with
        {
            ReviewMigrationVersion = DailyReviewMigration.CurrentVersion,
            CategoryNames = WindowSettings.Default.WithCategoryName(BoardCategory.Reference, "复盘").CategoryNames
        };
        var window = CreateWindow(board, store, settings, captureTarget);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);

            Assert.AreEqual(BoardCategory.Inbox, captureTarget.Current);
            Assert.AreEqual(Visibility.Visible, ((Border)window.FindName("ReviewContentHost")).Visibility);
            Assert.AreEqual(Visibility.Collapsed, ((ListBox)window.FindName("BoardList")).Visibility);
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "daily-review-expanded.png",
                "FTS_DAILY_REVIEW_EVIDENCE_DIR");

            var editor = (TextBox)window.FindName("ReviewEditor");
            editor.Text = "今天完成了一个功能";
            var save = window.Dispatcher.InvokeAsync(
                () => (Task)typeof(MainWindow).GetMethod(
                    "FlushReviewAsync",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null)!).Task.Unwrap();
            PumpDispatcherUntil(window.Dispatcher, save);

            var saved = store.LoadAsync(DateOnly.FromDateTime(DateTime.Now)).GetAwaiter().GetResult();
            Assert.AreEqual("今天完成了一个功能", saved.Content);
        }
        finally
        {
            GetPrivateField<IDailyReviewStore>(window, "_dailyReviews")?.StopWatching();
            CloseWindowWithoutSaving(window);
        }
    }

    private static void LoadReview(MainWindow window)
    {
        InvokePrivateTask(window, "LoadReviewDateAsync", DateOnly.FromDateTime(DateTime.Now));
    }

    private static MainWindow CreateReviewWindow(TestDirectory directory)
    {
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var settings = WindowSettings.Default with
        {
            ReviewMigrationVersion = DailyReviewMigration.CurrentVersion,
            CategoryNames = WindowSettings.Default.WithCategoryName(BoardCategory.Reference, "复盘").CategoryNames
        };
        return CreateWindow(new BoardService(), store, settings, new DefaultCaptureCategoryState());
    }

    private static void CloseReviewWindow(MainWindow window)
    {
        GetPrivateField<IDailyReviewStore>(window, "_dailyReviews")?.StopWatching();
        CloseWindowWithoutSaving(window);
    }

    [STATestMethod]
    public void ReviewEditing_PointerLeaveWhileEditorFocusedKeepsPanelExpanded()
    {
        using var directory = new TestDirectory();
        var window = CreateReviewWindow(directory);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var editor = (TextBox)window.FindName("ReviewEditor");
            Assert.IsNotNull(editor);
            CompleteLayout(window);
            Assert.IsTrue(editor.Focus());
            Assert.AreEqual(editor, Keyboard.FocusedElement);
            CompleteLayout(window);

            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            var collapseTimer = GetPrivateField<DispatcherTimer>(window, "_collapseTimer");
            Assert.IsFalse(collapseTimer.IsEnabled);
            WithClientAreaAnimationsDisabled(
                window,
                () =>
                {
                    InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
                    CompleteLayout(window);
                });

            Assert.IsTrue(viewModel.IsPanelExpanded);
        }
        finally
        {
            CloseReviewWindow(window);
        }
    }

    [STATestMethod]
    public void ReviewEditing_CollapseTimerFiredWhileEditorFocusedDoesNotCollapse()
    {
        using var directory = new TestDirectory();
        var window = CreateReviewWindow(directory);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var editor = (TextBox)window.FindName("ReviewEditor");
            Assert.IsNotNull(editor);
            Keyboard.ClearFocus();

            // 指针先离开（定时器开始倒计时），随后键盘导航让编辑器获得焦点。
            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            var collapseTimer = GetPrivateField<DispatcherTimer>(window, "_collapseTimer");
            Assert.IsTrue(collapseTimer.IsEnabled);
            CompleteLayout(window);
            Assert.IsTrue(editor.Focus());
            Assert.AreEqual(editor, Keyboard.FocusedElement);
            CompleteLayout(window);

            WithClientAreaAnimationsDisabled(
                window,
                () =>
                {
                    InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
                    CompleteLayout(window);
                });

            Assert.IsTrue(viewModel.IsPanelExpanded);
        }
        finally
        {
            CloseReviewWindow(window);
        }
    }

    [STATestMethod]
    public void ReviewEditing_EditorFocusLossRestoresCollapseWhenPointerOutside()
    {
        using var directory = new TestDirectory();
        var window = CreateReviewWindow(directory);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var editor = (TextBox)window.FindName("ReviewEditor");
            Assert.IsNotNull(editor);
            CompleteLayout(window);
            Assert.IsTrue(editor.Focus());
            Assert.AreEqual(editor, Keyboard.FocusedElement);
            CompleteLayout(window);

            // 抑制期间指针已离开面板；失焦后不会有 MouseLeave 补发，必须靠重估恢复收起。
            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            var collapseTimer = GetPrivateField<DispatcherTimer>(window, "_collapseTimer");
            Assert.IsFalse(collapseTimer.IsEnabled);
            var previousButton = (Button)window.FindName("ReviewPreviousButton");
            Assert.IsNotNull(previousButton);
            Assert.IsTrue(previousButton.Focus());
            Assert.AreEqual(previousButton, Keyboard.FocusedElement);
            CompleteLayout(window);
            Assert.IsTrue(collapseTimer.IsEnabled);

            WithClientAreaAnimationsDisabled(
                window,
                () =>
                {
                    InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
                    CompleteLayout(window);
                });

            Assert.IsFalse(viewModel.IsPanelExpanded);
        }
        finally
        {
            CloseReviewWindow(window);
        }
    }

    [STATestMethod]
    public void ReviewWithoutEditorFocus_PointerLeaveStillCollapses()
    {
        using var directory = new TestDirectory();
        var window = CreateReviewWindow(directory);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            Keyboard.ClearFocus();

            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            var collapseTimer = GetPrivateField<DispatcherTimer>(window, "_collapseTimer");
            Assert.IsTrue(collapseTimer.IsEnabled);
            WithClientAreaAnimationsDisabled(
                window,
                () =>
                {
                    InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
                    CompleteLayout(window);
                });

            Assert.IsFalse(viewModel.IsPanelExpanded);
        }
        finally
        {
            CloseReviewWindow(window);
        }
    }

    [STATestMethod]
    public void ReviewToolbar_DateDropdownRemovedAndStateStaysRight()
    {
        using var directory = new TestDirectory();
        var window = CreateReviewWindow(directory);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);

            // 1.16.0 起日期下拉已删除：日期导航只保留 ‹ › 单步切换。
            Assert.IsNull(window.FindName("ReviewDateComboBox"));

            var state = (TextBlock)window.FindName("ReviewDateState");
            Assert.IsNotNull(state);
            Assert.AreEqual("今天", state.Text);

            var previous = (Button)window.FindName("ReviewPreviousButton");
            var next = (Button)window.FindName("ReviewNextButton");
            Assert.IsTrue(previous.IsEnabled);
            Assert.IsFalse(next.IsEnabled);

            previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, previous));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (state.Text == "今天" && DateTime.UtcNow < deadline)
            {
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
            }

            Assert.AreEqual(
                DateOnly.FromDateTime(DateTime.Now).AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                state.Text);
            Assert.IsTrue(next.IsEnabled);
        }
        finally
        {
            CloseReviewWindow(window);
        }
    }

    [STATestMethod]
    public void ReviewEditor_BrushesFollowThemeDictionary()
    {
        AssertEditorBrushesFollowThemeDictionary(DesignTheme.Dark, "review-editor-dark.png");
        AssertEditorBrushesFollowThemeDictionary(DesignTheme.Light, "review-editor-light.png");
    }

    private static void AssertEditorBrushesFollowThemeDictionary(DesignTheme theme, string evidenceFileName)
    {
        using var directory = new TestDirectory();
        var window = CreateReviewWindow(directory);

        try
        {
            window.Show();
            DesignThemeManager.Apply(window, theme);
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);
            var editor = (TextBox)window.FindName("ReviewEditor");
            Assert.IsNotNull(editor);
            editor.Text = "深浅两套主题下都应可读的复盘文字。";
            CompleteLayout(window);

            // DynamicResource 必须解析为当前主题字典中的画刷实例（防写死常量，
            // 也防样式退回系统默认：深色下黑字黑底不可读）。
            Assert.AreSame(window.FindResource("PrimaryTextBrush"), editor.Foreground);
            Assert.AreSame(window.FindResource("PrimaryTextBrush"), editor.CaretBrush);
            Assert.AreSame(window.FindResource("AccentBrush"), editor.SelectionBrush);
            // SelectionTextBrush 有意不设：沿用系统默认「强调色选区 + 白字」语义。
            // 断言本地值未被固定（Aero2 主题默认给白字，断 null 会依赖系统实现细节）。
            Assert.AreEqual(
                DependencyProperty.UnsetValue,
                editor.ReadLocalValue(
                    System.Windows.Controls.Primitives.TextBoxBase.SelectionTextBrushProperty));

            var previous = (Button)window.FindName("ReviewPreviousButton");
            Assert.IsNotNull(previous);
            Assert.AreEqual(
                window.FindResource("PrimaryTextBrush"),
                previous.Foreground,
                "导航按钮前景必须来自主题字典，而不是 Aero2 系统画刷。");

            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                evidenceFileName,
                "FTS_DAILY_REVIEW_EVIDENCE_DIR");
        }
        finally
        {
            CloseReviewWindow(window);
        }
    }

    [STATestMethod]
    public void ReviewEditing_DeactivatedClearsStuckEditingHoldAndRestoresCollapse()
    {
        using var directory = new TestDirectory();
        var window = CreateReviewWindow(directory);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var editor = (TextBox)window.FindName("ReviewEditor");
            Assert.IsNotNull(editor);
            CompleteLayout(window);
            Assert.IsTrue(editor.Focus());
            CompleteLayout(window);

            // 抑制期间指针已离开面板。
            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            // 模拟 WM_KILLFOCUS 未走路由事件：保持原因卡在 true。
            Keyboard.ClearFocus();

            var state = GetPrivateField<PanelStateMachine>(window, "_panelState");
            Assert.IsTrue(state.IsTextEditingActive);
            InvokePrivate(window, "MainWindow_Deactivated", window, EventArgs.Empty);
            Assert.IsFalse(state.IsTextEditingActive);

            var collapseTimer = GetPrivateField<DispatcherTimer>(window, "_collapseTimer");
            Assert.IsTrue(collapseTimer.IsEnabled);
            WithClientAreaAnimationsDisabled(
                window,
                () =>
                {
                    InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
                    CompleteLayout(window);
                });

            Assert.IsFalse(viewModel.IsPanelExpanded);
        }
        finally
        {
            CloseReviewWindow(window);
        }
    }
}
