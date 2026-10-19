using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 内容桶空状态引导契约（#33，Issue #200）：待分类/图片为空时居中「图标＋标题＋
/// 副文案」引导，可见性单点＝UpdatePanelSurface 表面切换＋激活桶集合变化＋搜索
/// 进出让位；文案按 (BoardCategory, IsDefaultCapture) 二维矩阵派生（默认接收桶＝
/// 复制自动收集，非默认桶＝拖入收集）。空态元素为纯信息性：命中穿透回 BoardList，
/// 拖放/点击语义不变。
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

    private static StackPanel BoardEmptyStatePanel(MainWindow window) =>
        (StackPanel)window.FindName("BoardEmptyState");

    private static TextBlock BoardEmptyStateTitleText(MainWindow window) =>
        (TextBlock)window.FindName("BoardEmptyStateTitle");

    private static TextBlock BoardEmptyStateHintText(MainWindow window) =>
        (TextBlock)window.FindName("BoardEmptyStateHint");

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
    public void BoardEmptyState_CopyMatrixRejectsNonContentBucketCategories()
    {
        // 穷尽 switch、无兜底臂：非内容桶具名值进入矩阵属编程错误，必须立即失败
        // 而不是给出误导性空态文案（与 BoardCategoryCatalog.RoleOf 同口径）。
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => MainWindow.BoardEmptyStateCopy(DailyReviewMigration.ReviewCategory, true));
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
}
