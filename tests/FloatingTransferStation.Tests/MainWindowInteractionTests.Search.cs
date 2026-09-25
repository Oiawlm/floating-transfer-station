using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 内容搜索切片 1(2026-09-25 设计草案)的窗口级回归:只读过滤视图、退出还原、
/// 类型 chip、空态反馈、收起抑制、切类退出、复盘排除与选择清理。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public void Search_FiltersCurrentCategoryAndExitRestoresAllItems()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("苹果新鲜到货", BoardCategory.Inbox);
        board.AddText("香蕉");
        board.AddText("苹果派做法", BoardCategory.Inbox);
        board.AddText("橙子", BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var list = (ListBox)window.FindName("BoardList");
            Assert.AreEqual(4, list.Items.OfType<BoardItem>().Count());

            window.EnterSearchMode();
            Assert.IsTrue(viewModel.IsSearchActive);
            var input = (TextBox)window.FindName("SearchInput");
            input.Text = "苹果";
            CompleteLayout(window);

            CollectionAssert.AreEqual(
                new[] { "苹果派做法", "苹果新鲜到货" },
                list.Items.OfType<BoardItem>().Select(item => item.Text).ToArray(),
                "关键词按大小写不敏感子串过滤当前分类。");
            Assert.AreEqual(4, viewModel.ActivePanel!.Items.Count, "源集合不得被过滤改动。");

            window.ExitSearchMode();
            CompleteLayout(window);

            Assert.IsFalse(viewModel.IsSearchActive);
            Assert.AreEqual(4, list.Items.OfType<BoardItem>().Count());
            Assert.AreEqual(string.Empty, viewModel.SearchText);
            Assert.AreEqual(
                Visibility.Visible,
                ((StackPanel)window.FindName("HeaderActions")).Visibility,
                "退出搜索后头部操作区应还原。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Search_TypeChipsFilterByKindAndBothChipsMeanAll()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("文字一", BoardCategory.Inbox);
        var imagePath = Path.Combine(directory.Root, "search-image.png");
        WritePng(imagePath, width: 32, height: 16);
        board.AddImage(Guid.NewGuid(), "images/search-image.png", imagePath, BoardCategory.Inbox);
        board.AddText("文字二", BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");

            window.EnterSearchMode();
            var textChip = (ToggleButton)window.FindName("SearchTextChip");
            textChip.IsChecked = true;
            textChip.RaiseEvent(new RoutedEventArgs(ToggleButton.ClickEvent));
            CompleteLayout(window);

            Assert.AreEqual(2, list.Items.OfType<BoardItem>().Count());
            Assert.IsTrue(list.Items.OfType<BoardItem>().All(item => item.Kind == BoardItemKind.Text));

            var imageChip = (ToggleButton)window.FindName("SearchImageChip");
            imageChip.IsChecked = true;
            imageChip.RaiseEvent(new RoutedEventArgs(ToggleButton.ClickEvent));
            CompleteLayout(window);

            Assert.AreEqual(3, list.Items.OfType<BoardItem>().Count(),
                "两个 chip 都选中时等价于全部类型。");

            textChip.IsChecked = false;
            textChip.RaiseEvent(new RoutedEventArgs(ToggleButton.ClickEvent));
            CompleteLayout(window);

            Assert.AreEqual(1, list.Items.OfType<BoardItem>().Count());
            Assert.IsTrue(list.Items.OfType<BoardItem>().All(item => item.Kind == BoardItemKind.Image));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Search_EmptyResultsShowHintAndClearFilterRestores()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("苹果", BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var hint = (Border)window.FindName("SearchEmptyHint");

            window.EnterSearchMode();
            var input = (TextBox)window.FindName("SearchInput");
            input.Text = "不存在的关键词";
            CompleteLayout(window);

            Assert.AreEqual(0, list.Items.OfType<BoardItem>().Count());
            Assert.AreEqual(Visibility.Visible, hint.Visibility);

            var clear = (Button)window.FindName("SearchClearFilterButton");
            clear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            CompleteLayout(window);

            Assert.AreEqual(1, list.Items.OfType<BoardItem>().Count(), "一键清空后还原完整列表。");
            Assert.AreEqual(Visibility.Collapsed, hint.Visibility);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Search_ActiveSuppressesAutoCollapseUntilExit()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("内容", BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            window.ClientAreaAnimationsEnabled = false;
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            window.EnterSearchMode();
            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(450));
            CompleteLayout(window);
            Assert.IsTrue(viewModel.IsPanelExpanded, "搜索态进行中不得自动收起。");

            window.ExitSearchMode();
            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(450));
            CompleteLayout(window);
            Assert.IsFalse(viewModel.IsPanelExpanded, "退出搜索后恢复既有收起节奏。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Search_ExitsOnCategorySwitchAndIgnoresReviewTab()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("苹果", BoardCategory.Inbox);
        board.AddText("参考", BoardCategory.Reference);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            window.EnterSearchMode();
            ((TextBox)window.FindName("SearchInput")).Text = "苹果";
            CompleteLayout(window);
            Assert.IsTrue(viewModel.IsSearchActive);

            InvokePrivate(window, "ActivatePanel", BoardCategory.Reference);
            CompleteLayout(window);

            Assert.IsFalse(viewModel.IsSearchActive, "切换分类必须退出搜索并还原列表。");
            Assert.AreEqual(1, ((ListBox)window.FindName("BoardList")).Items.OfType<BoardItem>().Count());

            InvokePrivate(window, "ActivatePanel", DailyReviewMigration.ReviewCategory);
            CompleteLayout(window);
            window.EnterSearchMode();
            Assert.IsFalse(viewModel.IsSearchActive, "复盘标签不参与搜索(冻结)。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Search_ClearsSelectionOnEnterAndKeepsPinnedOrderInResults()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var plain = board.AddText("普通苹果一", BoardCategory.Inbox);
        var pinned = board.AddText("置顶苹果二", BoardCategory.Inbox);
        board.AddText("普通其他", BoardCategory.Inbox);
        board.SetPinnedMany([pinned.Id], true);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(plain);
            Assert.AreEqual(1, list.SelectedItems.Count);

            window.EnterSearchMode();
            Assert.AreEqual(0, list.SelectedItems.Count, "进入搜索态清空选择。");

            ((TextBox)window.FindName("SearchInput")).Text = "苹果";
            CompleteLayout(window);

            var results = list.Items.OfType<BoardItem>().ToArray();
            Assert.AreEqual(2, results.Length);
            Assert.AreEqual(pinned.Id, results[0].Id, "过滤结果保持先置顶区后普通区。");
            Assert.AreEqual(plain.Id, results[1].Id);

            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "search-active.png",
                "FTS_SEARCH_EVIDENCE_DIR");
        }
        finally
        {
            CloseWindow(window);
        }
    }
}
