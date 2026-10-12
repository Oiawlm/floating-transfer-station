using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// BoardList viewport 几何与滚动条可见性解耦契约：卡片几何不得随滚动条出现/消失而
/// 变化。默认 ScrollViewer 模板把垂直滚动条放进独立 Auto 列，条带出现/消失会让
/// viewport 在 W 与 W−8 间跳变——卡片（全宽拉伸）随之变窄/变宽，剩余置顶卡片相对
/// 视口的位置发生轻微位移（用户报障）。overlay 模板（单格叠加）让滚动条悬浮内容
/// 之上、永不占布局宽；本组测试同时锁定 viewport 宽、卡宽、卡位与滚动条可交互性。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    private const double ScrollGeometryTolerance = 0.5;

    private static (ScrollViewer Viewer, ScrollContentPresenter Presenter) BoardListScrollSurface(
        ListBox list)
    {
        var viewer = FindDescendant<ScrollViewer>(list);
        Assert.IsNotNull(viewer, "BoardList 内部必须存在 ScrollViewer。");
        var presenter = FindDescendant<ScrollContentPresenter>(viewer);
        Assert.IsNotNull(presenter, "ScrollViewer 模板必须实现 PART_ScrollContentPresenter。");
        return (viewer, presenter);
    }

    private static Grid RealizedCardRoot(
        MainWindow window,
        ListBox list,
        object item)
    {
        var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(item);
        Assert.IsNotNull(container, "目标卡片容器应已实现。");
        var cardRootStyle = (Style)window.FindResource("CardRootStyle");
        return FindDescendants<Grid>(container)
            .Single(candidate => ReferenceEquals(candidate.Style, cardRootStyle));
    }

    private static bool VerticalScrollBarVisible(ListBox list) =>
        FindDescendants<ScrollBar>(list).Any(candidate =>
            candidate.Orientation == Orientation.Vertical &&
            candidate.Visibility == Visibility.Visible);

    /// <summary>以「置顶锚点 + 24 条可滚动内容」建立滚动条在场的窗口。</summary>
    private static MainWindow CreateScrollableWindow(TestDirectory directory, out BoardService board)
    {
        board = new BoardService();
        var pinned = board.AddText("置顶锚点卡片");
        board.SetPinnedMany([pinned.Id], true);
        AddScrollableItems(board, BoardCategory.Inbox);
        return CreateWindow(directory, board);
    }

    [STATestMethod]
    public void ScrollViewport_WidthIsIndependentOfVerticalScrollBarVisibility()
    {
        using var directory = new TestDirectory();
        var window = CreateScrollableWindow(directory, out var board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var (viewer, _) = BoardListScrollSurface(list);
            Assert.IsTrue(viewer.ScrollableHeight > 0, "前置条件：内容可滚动。");
            Assert.IsTrue(VerticalScrollBarVisible(list), "前置条件：垂直滚动条在场。");
            var widthWithScrollBar = viewer.ViewportWidth;

            // 用户场景后半段：清空非置顶内容，滚动条离场。
            Assert.IsNotNull(board.RemoveNonPinned([BoardCategory.Inbox]));
            CompleteLayout(window);
            Assert.IsTrue(viewer.ScrollableHeight <= 0, "前置条件：清空后内容不可滚动。");
            Assert.IsFalse(VerticalScrollBarVisible(list), "前置条件：垂直滚动条离场。");

            Assert.AreEqual(
                widthWithScrollBar,
                viewer.ViewportWidth,
                ScrollGeometryTolerance,
                "viewport 宽度不得随滚动条出现/消失变化（两种状态下都应是全宽）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ScrollViewport_CardWidthIsIndependentOfVerticalScrollBarVisibility()
    {
        using var directory = new TestDirectory();
        var window = CreateScrollableWindow(directory, out var board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            Assert.IsTrue(VerticalScrollBarVisible(list), "前置条件：垂直滚动条在场。");
            var card = RealizedCardRoot(window, list, CardAnchorItem(board));
            var widthWithScrollBar = card.ActualWidth;

            Assert.IsNotNull(board.RemoveNonPinned([BoardCategory.Inbox]));
            CompleteLayout(window);
            Assert.IsFalse(VerticalScrollBarVisible(list), "前置条件：垂直滚动条离场。");

            var cardAfter = RealizedCardRoot(window, list, CardAnchorItem(board));
            Assert.AreEqual(
                widthWithScrollBar,
                cardAfter.ActualWidth,
                ScrollGeometryTolerance,
                "卡片宽度不得随滚动条出现而变窄、消失而变宽。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    private static object CardAnchorItem(BoardService board) =>
        board.Items(BoardCategory.Inbox).First(item => item.IsPinned);

    [STATestMethod]
    public void ScrollViewport_CardPositionIsIndependentOfVerticalScrollBarVisibility()
    {
        using var directory = new TestDirectory();
        var window = CreateScrollableWindow(directory, out var board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var (_, presenter) = BoardListScrollSurface(list);
            Assert.IsTrue(VerticalScrollBarVisible(list), "前置条件：垂直滚动条在场。");
            var card = RealizedCardRoot(window, list, CardAnchorItem(board));
            var topLeftBefore = card.TranslatePoint(new Point(0, 0), presenter);
            var topRightBefore = card.TranslatePoint(new Point(card.ActualWidth, 0), presenter);

            Assert.IsNotNull(board.RemoveNonPinned([BoardCategory.Inbox]));
            CompleteLayout(window);
            Assert.IsFalse(VerticalScrollBarVisible(list), "前置条件：垂直滚动条离场。");
            var cardAfter = RealizedCardRoot(window, list, CardAnchorItem(board));
            var topLeftAfter = cardAfter.TranslatePoint(new Point(0, 0), presenter);
            var topRightAfter = cardAfter.TranslatePoint(new Point(cardAfter.ActualWidth, 0), presenter);

            Assert.AreEqual(
                topLeftBefore.X, topLeftAfter.X, ScrollGeometryTolerance,
                "卡片左缘相对视口的横向位置不得漂移。");
            Assert.AreEqual(
                topLeftBefore.Y, topLeftAfter.Y, ScrollGeometryTolerance,
                "卡片顶缘相对视口的纵向位置不得漂移。");
            Assert.AreEqual(
                topRightBefore.X, topRightAfter.X, ScrollGeometryTolerance,
                "卡片右缘（右对齐操作按钮条的锚定缘）相对视口不得随滚动条出现/消失平移。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ScrollViewport_OverlayScrollBarRemainsInteractive()
    {
        using var directory = new TestDirectory();
        var window = CreateScrollableWindow(directory, out _);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var (viewer, _) = BoardListScrollSurface(list);
            Assert.IsTrue(viewer.ScrollableHeight > 0, "前置条件：内容可滚动。");
            var scrollBar = FindDescendants<ScrollBar>(list)
                .Single(candidate => candidate.Orientation == Orientation.Vertical &&
                    candidate.Visibility == Visibility.Visible);
            viewer.ScrollToVerticalOffset(viewer.ScrollableHeight);
            CompleteLayout(window);
            var offsetAtBottom = viewer.VerticalOffset;
            Assert.IsTrue(offsetAtBottom > 0, "前置条件：视口已滚动到底部。");

            // overlay 模板接管后 Track 必须仍然接线：点击上页按钮（Track 的
            // DecreaseRepeatButton）视口上移，防「样式替换后 Track 失联」。
            var repeatButtons = FindDescendants<RepeatButton>(scrollBar).ToArray();
            Assert.HasCount(2, repeatButtons, "滚动条 Track 的增/减 RepeatButton 必须已实现。");
            repeatButtons[0].RaiseEvent(NewMouseButtonEventArgs(
                UIElement.MouseLeftButtonDownEvent, repeatButtons[0]));
            repeatButtons[0].RaiseEvent(NewMouseButtonEventArgs(
                UIElement.MouseLeftButtonUpEvent, repeatButtons[0]));
            CompleteLayout(window);
            Assert.IsTrue(
                viewer.VerticalOffset < offsetAtBottom - 1,
                $"overlay 后滚动条仍可交互（上页前 {offsetAtBottom}，上页后 {viewer.VerticalOffset}）。");

            // 命中面契约：滚动条条带（8 DIP）落在卡片右缘外边距带（12 DIP）内，
            // 与每张已实现卡片及其右上操作按钮零像素相交。
            var strip = new Rect(
                scrollBar.TranslatePoint(new Point(), list),
                new Size(scrollBar.ActualWidth, scrollBar.ActualHeight));
            Assert.IsTrue(
                strip.Width > 0 && strip.Height > 0,
                "前置条件：滚动条条带已布局。");
            foreach (var container in FindDescendants<ListBoxItem>(list))
            {
                var cardRootStyle = (Style)window.FindResource("CardRootStyle");
                var card = FindDescendants<Grid>(container)
                    .Single(candidate => ReferenceEquals(candidate.Style, cardRootStyle));
                var cardRect = new Rect(
                    card.TranslatePoint(new Point(), list),
                    new Size(card.ActualWidth, card.ActualHeight));
                Assert.IsFalse(
                    cardRect.IntersectsWith(strip),
                    $"卡片矩形 {cardRect} 与滚动条条带 {strip} 相交：条带必须落在卡片外边距带内。");
                foreach (var button in CardOperationButtons(container))
                {
                    if (button.Visibility == Visibility.Collapsed)
                    {
                        continue;
                    }

                    var buttonRect = new Rect(
                        button.TranslatePoint(new Point(), list),
                        new Size(button.ActualWidth, button.ActualHeight));
                    Assert.IsFalse(
                        buttonRect.IntersectsWith(strip),
                        $"操作按钮矩形 {buttonRect} 与滚动条条带 {strip} 相交。");
                }
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }
}
