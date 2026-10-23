using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 空状态引导契约（#33，Issue #200；2026-10-24 复盘扩展）：待分类/图片为空时居中
/// 「图标＋标题＋副文案」引导，可见性单点＝UpdatePanelSurface 表面切换＋激活桶
/// 集合变化＋搜索进出让位；文案按 (BoardCategory, IsDefaultCapture) 二维矩阵派生
/// （默认接收桶＝复制自动收集，非默认桶＝拖入收集；复盘臂供复盘表面空态）。空态
/// 元素为纯信息性：命中穿透回 BoardList/编辑器，拖放/点击/输入语义不变。复盘空态
/// 生命周期：当日无内容→可见；输入第一个字符→立即隐藏；切换日期→按装载内容重评估。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    private const string InboxDefaultCaptureHint =
        "复制的文字或图片会自动收集到这里，也可以直接拖入";
    private const string InboxNonDefaultCaptureHint =
        "把文字或图片拖到这里即可收集";
    private const string CustomerOriginalDefaultCaptureHint =
        "复制的图片或文字会自动收集到这里，也可以直接拖入";
    private const string CustomerOriginalNonDefaultCaptureHint =
        "把图片或文字拖到这里即可收集";
    private const string ReviewEmptyStateTitleCopy =
        "还没有复盘内容";
    private const string ReviewEmptyStateHintCopy =
        "直接输入即可开始记录，内容会自动保存";

    private static StackPanel BoardEmptyStatePanel(MainWindow window) =>
        (StackPanel)window.FindName("BoardEmptyState");

    private static TextBlock BoardEmptyStateTitleText(MainWindow window) =>
        (TextBlock)window.FindName("BoardEmptyStateTitle");

    private static TextBlock BoardEmptyStateHintText(MainWindow window) =>
        (TextBlock)window.FindName("BoardEmptyStateHint");

    private static StackPanel ReviewEmptyStatePanel(MainWindow window) =>
        (StackPanel)window.FindName("ReviewEmptyState");

    private static TextBlock ReviewEmptyStateTitleText(MainWindow window) =>
        (TextBlock)window.FindName("ReviewEmptyStateTitle");

    private static TextBlock ReviewEmptyStateHintText(MainWindow window) =>
        (TextBlock)window.FindName("ReviewEmptyStateHint");

    /// <summary>复盘异步方法须经 Dispatcher.InvokeAsync 调用（既有 ReviewCategory_*
    /// flush 用例同法）：裸测试线程无 SynchronizationContext，await 续体会跳线程池，
    /// 碰 ReviewEditor 即 VerifyAccess 崩溃；dispatcher 回调内调用则续体回贴 UI 线程。</summary>
    private static void InvokeReviewTaskOnDispatcher(
        MainWindow window,
        string methodName,
        params object?[] arguments)
    {
        var method = typeof(MainWindow).GetMethod(
            methodName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        var task = window.Dispatcher.InvokeAsync(() => (Task)method.Invoke(window, arguments)!).Task.Unwrap();
        PumpDispatcherUntil(window.Dispatcher, task);
    }

    [TestMethod]
    public void BoardEmptyState_CopyMatrixDerivesByCategoryAndDefaultCapture()
    {
        Assert.AreEqual(
            ("还没有内容", InboxDefaultCaptureHint),
            MainWindow.BoardEmptyStateCopy(BoardCategory.Inbox, true),
            "(待分类, 默认接收) 桶应引导复制自动收集。");
        Assert.AreEqual(
            ("还没有内容", InboxNonDefaultCaptureHint),
            MainWindow.BoardEmptyStateCopy(BoardCategory.Inbox, false),
            "(待分类, 非默认接收) 桶应引导拖入收集。");
        Assert.AreEqual(
            ("还没有内容", CustomerOriginalDefaultCaptureHint),
            MainWindow.BoardEmptyStateCopy(BoardCategory.CustomerOriginal, true),
            "(图片, 默认接收) 桶应引导复制自动收集。");
        Assert.AreEqual(
            ("还没有内容", CustomerOriginalNonDefaultCaptureHint),
            MainWindow.BoardEmptyStateCopy(BoardCategory.CustomerOriginal, false),
            "(图片, 非默认接收) 桶应引导拖入收集。");
    }

    [TestMethod]
    public void BoardEmptyState_CopyMatrixDerivesReviewSurfaceEntry()
    {
        // 复盘臂：第二维（默认接收布尔）与复盘无关，两个取值必须给出同一文案——
        // 复盘的进入路径是直接输入，无自动收集/拖入之分。
        Assert.AreEqual(
            (ReviewEmptyStateTitleCopy, ReviewEmptyStateHintCopy),
            MainWindow.BoardEmptyStateCopy(DailyReviewMigration.ReviewCategory, true),
            "复盘空态标题应指出无内容，副文案应指出直接输入与自动保存（true 臂）。");
        Assert.AreEqual(
            (ReviewEmptyStateTitleCopy, ReviewEmptyStateHintCopy),
            MainWindow.BoardEmptyStateCopy(DailyReviewMigration.ReviewCategory, false),
            "复盘空态两臂文案一致（第二维与复盘无关）。");
    }

    [TestMethod]
    public void BoardEmptyState_CopyMatrixRejectsLauncherCategory()
    {
        // 穷尽 switch、无兜底臂：启动具名值（唯一无空态文案语义的分类——启动表面
        // 空态归 LauncherEmptyState 专属承载）进入矩阵属编程错误，必须立即失败
        // 而不是给出误导性空态文案（与 BoardCategoryCatalog.RoleOf 同口径）。
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => MainWindow.BoardEmptyStateCopy(QuickLaunchMigration.LauncherCategory, false));
    }

    [STATestMethod]
    public void BoardEmptyState_TracksEmptyBucketAcrossItemLifecycle()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            Assert.AreEqual(
                Visibility.Visible,
                BoardEmptyStatePanel(window).Visibility,
                "空待分类桶（默认接收）应显示空态引导。");
            Assert.AreEqual("还没有内容", BoardEmptyStateTitleText(window).Text);
            Assert.AreEqual(InboxDefaultCaptureHint, BoardEmptyStateHintText(window).Text);

            var item = board.AddText("首条内容");
            CompleteLayout(window);
            Assert.AreEqual(
                Visibility.Collapsed,
                BoardEmptyStatePanel(window).Visibility,
                "内容到达后空态应消失（集合变化驱动，无需表面切换）。");

            Assert.IsNotNull(board.RemoveNonPinned([BoardCategory.Inbox]));
            CompleteLayout(window);
            Assert.AreEqual(
                Visibility.Visible,
                BoardEmptyStatePanel(window).Visibility,
                "清空后空态应回归。");
            Assert.AreEqual(
                "首条内容",
                item.Text,
                "前置：操作的是同一条内容（防误清其他分类）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void BoardEmptyState_CopySwitchesWhenDefaultCaptureMoves()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            Assert.AreEqual(InboxDefaultCaptureHint, BoardEmptyStateHintText(window).Text);

            var viewModel = (MainWindowViewModel)window.DataContext;
            Assert.IsTrue(
                viewModel.SetDefaultCaptureCategory(BoardCategory.CustomerOriginal),
                "前置：默认接收迁往图片桶。");
            CompleteLayout(window);
            Assert.AreEqual(
                InboxNonDefaultCaptureHint,
                BoardEmptyStateHintText(window).Text,
                "默认接收迁走后，原默认桶文案应从「自动收集」换为「拖入收集」（仍停留该桶）。");

            ExpandCategory(window, BoardCategory.CustomerOriginal);
            CompleteLayout(window);
            Assert.AreEqual(
                Visibility.Visible,
                BoardEmptyStatePanel(window).Visibility,
                "空图片桶（新默认接收）应显示空态。");
            Assert.AreEqual(
                CustomerOriginalDefaultCaptureHint,
                BoardEmptyStateHintText(window).Text,
                "新默认接收桶文案应为「自动收集」变体。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void BoardEmptyState_HiddenOnReviewAndLauncherSurfaces()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            Assert.AreEqual(
                Visibility.Collapsed,
                BoardEmptyStatePanel(window).Visibility,
                "启动表面有专属空态（LauncherEmptyState），内容桶空态不得串面。");
            Assert.AreEqual(
                Visibility.Visible,
                ((StackPanel)window.FindName("LauncherEmptyState")).Visibility,
                "前置：启动空态在场（两空态互斥对照）。");

            ExpandCategory(window, DailyReviewMigration.ReviewCategory);
            CompleteLayout(window);
            Assert.AreEqual(
                Visibility.Collapsed,
                BoardEmptyStatePanel(window).Visibility,
                "复盘表面是记录面，不显示内容桶空态。");

            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            Assert.AreEqual(
                Visibility.Visible,
                BoardEmptyStatePanel(window).Visibility,
                "回到内容桶表面空态应恢复。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void BoardEmptyState_YieldsToSearchAndReturnsOnExit()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            Assert.AreEqual(Visibility.Visible, BoardEmptyStatePanel(window).Visibility);

            window.EnterSearchMode();
            CompleteLayout(window);
            Assert.AreEqual(
                Visibility.Collapsed,
                BoardEmptyStatePanel(window).Visibility,
                "搜索态的空集语义由 SearchEmptyHint 承载，内容桶空态应让位。");
            Assert.AreEqual(
                Visibility.Visible,
                ((Border)window.FindName("SearchEmptyHint")).Visibility,
                "前置：搜索空态接管（空桶进搜索＝无匹配）。");

            window.ExitSearchMode();
            CompleteLayout(window);
            Assert.AreEqual(
                Visibility.Visible,
                BoardEmptyStatePanel(window).Visibility,
                "退出搜索后空态应回归。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void BoardEmptyState_BackgroundCollectionChangeMarshalsToUiThread()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            // 激活「图片」桶：待分类集合无绑定视图，唯一会被跨线程集合变化触碰的
            // 监听者就是空态处理器——修复前这里直接跨线程访问 UI 元素。
            ExpandCategory(window, BoardCategory.CustomerOriginal);
            CompleteLayout(window);
            Assert.AreEqual(
                Visibility.Visible,
                BoardEmptyStatePanel(window).Visibility,
                "前置：空图片桶空态在场。");

            // 外部拖入导入路径的线程模型：落板回调在线程池线程触发
            // CollectionChanged（CI 实证曾崩掉测试宿主）。空态更新必须封送回
            // UI 线程——Wait() 在修复前会把跨线程 InvalidOperationException
            // 以 AggregateException 形式抛回本测试。
            var background = Task.Run(() => board.AddText("后台导入内容"));
            background.Wait();
            CompleteLayout(window);

            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            Assert.AreEqual(
                Visibility.Collapsed,
                BoardEmptyStatePanel(window).Visibility,
                "后台到达的内容驱动空态消失（封送后照常生效）。");
            Assert.IsTrue(
                board.Items(BoardCategory.Inbox).Any(item => item.Text == "后台导入内容"),
                "前置：内容确实落进待分类桶。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void BoardEmptyState_DoesNotInterceptBoardListHitTesting()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var emptyState = BoardEmptyStatePanel(window);
            Assert.IsFalse(
                emptyState.IsHitTestVisible,
                "空态是纯信息性元素，必须整体退出命中测试（拖放/点击穿透回 BoardList）。");

            // 空态几何中心处的真实命中必须归属 BoardList 子树：内容桶的拖放
            // （PreviewDragOver/Drop）挂在 BoardList 上，兄弟层覆盖不得截获。
            var list = (ListBox)window.FindName("BoardList");
            var center = emptyState.TranslatePoint(
                new Point(emptyState.ActualWidth / 2, emptyState.ActualHeight / 2),
                window);
            var hit = window.InputHitTest(center);
            Assert.IsNotNull(hit, "空态中心必须命中元素。");
            var walker = (DependencyObject?)hit;
            var reachesList = false;
            while (walker is not null)
            {
                if (ReferenceEquals(walker, list))
                {
                    reachesList = true;
                    break;
                }

                walker = VisualTreeHelper.GetParent(walker);
            }

            Assert.IsTrue(
                reachesList,
                $"空态中心命中了 {hit.GetType().Name}：命中链必须抵达 BoardList（拖放生命线）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void BoardEmptyState_ReviewEmptyStateLifecycleAcrossTypingAndDateSwitch()
    {
        using var directory = new TestDirectory();
        var window = CreateReviewWindow(directory);

        try
        {
            window.Show();
            ExpandCategory(window, DailyReviewMigration.ReviewCategory);
            CompleteLayout(window);
            // 装载今天（TestDirectory 内无复盘文件 → 当日无内容）。
            LoadReview(window);
            CompleteLayout(window);

            Assert.AreEqual(
                Visibility.Visible,
                ReviewEmptyStatePanel(window).Visibility,
                "复盘当日无内容应显示空态引导。");
            Assert.AreEqual(ReviewEmptyStateTitleCopy, ReviewEmptyStateTitleText(window).Text);
            Assert.AreEqual(ReviewEmptyStateHintCopy, ReviewEmptyStateHintText(window).Text);
            Assert.IsFalse(
                ReviewEmptyStatePanel(window).IsHitTestVisible,
                "复盘空态是纯信息性元素，必须整体退出命中测试（点击穿透到编辑器）。");

            // 输入第一个字符：TextChanged 同步驱动更新点，不做任何布局等待——
            // 断言紧随赋值，证明确即隐藏。
            var editor = (TextBox)window.FindName("ReviewEditor");
            editor.Text = "今";
            Assert.AreEqual(
                Visibility.Collapsed,
                ReviewEmptyStatePanel(window).Visibility,
                "输入第一个字符应立即隐藏空态（不等自动保存）。");

            // 切换日期 → 重评估：昨天有内容 → 隐藏（可见性跟随装载内容，不是静态门闩）。
            var today = DateOnly.FromDateTime(DateTime.Now);
            var reviews = GetPrivateField<IDailyReviewStore>(window, "_dailyReviews");
            reviews.SaveAsync(today.AddDays(-1), "昨天的复盘内容").GetAwaiter().GetResult();
            InvokeReviewTaskOnDispatcher(window, "SwitchReviewDateAsync", today.AddDays(-1));
            CompleteLayout(window);
            Assert.AreEqual(
                Visibility.Collapsed,
                ReviewEmptyStatePanel(window).Visibility,
                "切换到有内容的日期应保持隐藏（按装载内容重评估）。");

            // 再切到前天（无内容）→ 空态回归。
            InvokeReviewTaskOnDispatcher(window, "SwitchReviewDateAsync", today.AddDays(-2));
            CompleteLayout(window);
            Assert.AreEqual(
                Visibility.Visible,
                ReviewEmptyStatePanel(window).Visibility,
                "切换到无内容的日期空态应回归。");
        }
        finally
        {
            CloseReviewWindow(window);
        }
    }

    [STATestMethod]
    public void BoardEmptyState_ReviewEmptyStateEvidenceBothThemes()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("FTS_REVIEW_EMPTY_STATE_EVIDENCE_DIR")))
        {
            Assert.Inconclusive("未设置 FTS_REVIEW_EMPTY_STATE_EVIDENCE_DIR，跳过发布证据产出。");
        }

        using var directory = new TestDirectory();
        var window = CreateReviewWindow(directory);

        try
        {
            window.Show();
            window.Topmost = true;
            ExpandCategory(window, DailyReviewMigration.ReviewCategory);
            CompleteLayout(window);
            LoadReview(window);

            DesignThemeManager.Apply(window, DesignTheme.Light);
            CompleteLayout(window);
            Assert.AreEqual(
                Visibility.Visible,
                ReviewEmptyStatePanel(window).Visibility,
                "前置：浅色主题空态在场。");
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "review-empty-state-light.png",
                "FTS_REVIEW_EMPTY_STATE_EVIDENCE_DIR");

            DesignThemeManager.Apply(window, DesignTheme.Dark);
            CompleteLayout(window);
            Assert.AreEqual(
                Visibility.Visible,
                ReviewEmptyStatePanel(window).Visibility,
                "前置：深色主题空态在场（同键位换装，可见性不随主题变化）。");
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "review-empty-state-dark.png",
                "FTS_REVIEW_EMPTY_STATE_EVIDENCE_DIR");
        }
        finally
        {
            CloseReviewWindow(window);
        }
    }
}
