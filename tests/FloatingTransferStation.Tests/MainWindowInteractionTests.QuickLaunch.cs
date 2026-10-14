using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 快捷启动标签（#30，Issue #162）切片 1 窗口级锁定：表面三态分发
/// （BoardList / 复盘 / 启动互斥 + 批量钮结构性排除）、默认接收角色闸
/// （点击标签不改变默认接收）、搜索冻结。迁移与排除面的 Core 断言见
/// QuickLaunchMigrationTests / AutoCleanupScheduleTests；瓦片表面行为
/// （添加/启动/编辑/动效）随切片 2/3 在本文件扩展。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public void QuickLaunch_SurfaceSwitching_DispatchesThreeStatesAndHidesBatchButtons()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("普通卡片");
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var settings = WindowSettings.Default with { ReviewMigrationVersion = DailyReviewMigration.CurrentVersion };
        var window = CreateWindow(board, store, settings);

        try
        {
            window.Show();
            CompleteLayout(window);

            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);

            Assert.AreEqual(
                Visibility.Visible,
                ((Border)window.FindName("LauncherContentHost")).Visibility,
                "启动标签激活时瓦片表面宿主可见。");
            Assert.AreEqual(
                Visibility.Collapsed,
                ((ListBox)window.FindName("BoardList")).Visibility);
            Assert.AreEqual(
                Visibility.Collapsed,
                ((Border)window.FindName("ReviewContentHost")).Visibility);
            Assert.AreEqual(
                Visibility.Collapsed,
                ((Button)window.FindName("BatchPinButton")).Visibility,
                "批量置顶按钮不属于启动表面（结构性排除）。");
            Assert.AreEqual(
                Visibility.Collapsed,
                ((Button)window.FindName("DeleteContentButton")).Visibility,
                "垃圾桶按钮不属于启动表面（结构性排除）。");

            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            Assert.AreEqual(Visibility.Collapsed, ((Border)window.FindName("LauncherContentHost")).Visibility);
            Assert.AreEqual(Visibility.Visible, ((ListBox)window.FindName("BoardList")).Visibility);

            ExpandCategory(window, DailyReviewMigration.ReviewCategory);
            CompleteLayout(window);

            Assert.AreEqual(Visibility.Visible, ((Border)window.FindName("ReviewContentHost")).Visibility);
            Assert.AreEqual(Visibility.Collapsed, ((Border)window.FindName("LauncherContentHost")).Visibility);
        }
        finally
        {
            GetPrivateField<IDailyReviewStore>(window, "_dailyReviews")?.StopWatching();
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_DefaultCaptureGate_LauncherAndReviewTabsCannotCapture()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var state = new DefaultCaptureCategoryState();
        var window = CreateWindow(directory, board, state);

        try
        {
            window.Show();
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            var launcherTab = FindCategoryTab(
                window,
                viewModel.Categories.Single(category => category.Category == QuickLaunchMigration.LauncherCategory));
            launcherTab.RaiseEvent(NewMouseButtonEventArgs(
                UIElement.MouseLeftButtonUpEvent,
                launcherTab));
            CompleteLayout(window);

            Assert.AreEqual(
                BoardCategory.Inbox,
                state.Current,
                "点击启动标签不得改变默认接收（角色闸）。");

            var reviewTab = FindCategoryTab(
                window,
                viewModel.Categories.Single(category => category.Category == DailyReviewMigration.ReviewCategory));
            reviewTab.RaiseEvent(NewMouseButtonEventArgs(
                UIElement.MouseLeftButtonUpEvent,
                reviewTab));
            CompleteLayout(window);

            Assert.AreEqual(BoardCategory.Inbox, state.Current, "点击复盘标签不得改变默认接收。");

            var customerTab = FindCategoryTab(
                window,
                viewModel.Categories.Single(category => category.Category == BoardCategory.CustomerOriginal));
            customerTab.RaiseEvent(NewMouseButtonEventArgs(
                UIElement.MouseLeftButtonUpEvent,
                customerTab));
            CompleteLayout(window);

            Assert.AreEqual(
                BoardCategory.CustomerOriginal,
                state.Current,
                "点击内容桶标签照常设置默认接收。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_SearchMode_FrozenOnLauncherSurface()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            CompleteLayout(window);

            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            window.EnterSearchMode();

            Assert.IsFalse(
                ((MainWindowViewModel)window.DataContext).IsSearchActive,
                "启动标签搜索冻结（与复盘同款排除面）。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }
}
