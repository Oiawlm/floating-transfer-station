using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.Views;
using ShapesPath = System.Windows.Shapes.Path;

namespace FloatingTransferStation.Tests;

public sealed partial class MainWindowInteractionTests
{
    /// <summary>
    /// 头部图标网格规范回归：图标（按钮内容的根可视化元素）中心必须落在按钮中心阈值内，
    /// 光学尺寸与其他头部图标一致；计数徽章出现时图标保持原位、徽章锚定右下角。
    /// 全部断言基于布局几何（TransformToAncestor），不依赖渲染时钟，Debug/Release 均确定。
    /// 修复前失败：垃圾桶图标因 Viewbox 钉在 20×20 内容格左上而偏 (-2,-2) DIP，
    /// 恢复按钮因 Viewbox 把 11×11 几何放大满格、描边同步放大导致光学超格。
    /// 描边家族（恢复默认、保持展开「贴缘面板」）只锁家族规范（14.5×14.5 + 1.5 描边、居中、
    /// 墨迹足印），不锁定 Path 字形——字形正确性由真实渲染截图取证承担。
    /// </summary>
    [STATestMethod]
    public void HeaderActionIcons_AreCenteredAndOpticallyConsistent()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            // 齿轮与垃圾桶：填充图标，墨迹足印 = 16×16 Viewbox。
            AssertFillIconCentered(window, "UpdateButton", expectedSize: 16d);
            AssertFillIconCentered(window, "SettingsButton", expectedSize: 16d);
            AssertFillIconCentered(window, "DeleteContentButton", expectedSize: 16d);

            // 描边家族（几何显式 14.5×14.5 + 1.5 描边 = 墨迹足印约 16 DIP）：
            // 恢复默认按钮与保持展开按钮（贴缘面板图标）同一规范。
            AssertStrokeIconCentered(window, "ResetWindowButton", "恢复按钮图标");
            AssertStrokeIconCentered(window, "PanelHoldButton", "保持展开按钮图标");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void HeaderActionIcons_StayCenteredWhenCountBadgeShown()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("icon-metrics-item");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            Assert.IsTrue(window.BoardList.Items.Count > 0, "看板需有内容使清空按钮处于可用态。");
            window.BoardList.SelectedItems.Add(window.BoardList.Items[0]!);
            CompleteLayout(window);

            var deleteButton = (Button)window.FindName("DeleteContentButton")!;
            var badge = (Border)window.FindName("SelectedCountBadge")!;
            Assert.AreEqual(Visibility.Visible, badge.Visibility, "选中内容后计数徽章应显示。");

            // 图标（内容格内的 16×16 Viewbox；徽章内部的文字 Viewbox 不是图标）保持居中，
            // 不因徽章出现而漂移。
            var icon = FindDescendants<Viewbox>(deleteButton).Single(IsIconViewbox);
            var iconRect = BoundsIn(icon, deleteButton);
            AssertCenteredWithin(iconRect, deleteButton, "清空按钮图标（徽章显示）", 1.0d);

            // 徽章锚定按钮右下象限且完整落在按钮内。
            var badgeRect = BoundsIn(badge, deleteButton);
            Assert.IsTrue(
                badgeRect.Right <= deleteButton.ActualWidth + 0.5,
                $"徽章右缘应在按钮内，实际 {badgeRect.Right:F1}。");
            Assert.IsTrue(
                badgeRect.Bottom <= deleteButton.ActualHeight + 0.5,
                $"徽章下缘应在按钮内，实际 {badgeRect.Bottom:F1}。");
            var badgeCenter = new Point(
                (badgeRect.Left + badgeRect.Right) / 2,
                (badgeRect.Top + badgeRect.Bottom) / 2);
            var buttonCenter = new Point(deleteButton.ActualWidth / 2, deleteButton.ActualHeight / 2);
            Assert.IsTrue(
                badgeCenter.X > buttonCenter.X && badgeCenter.Y > buttonCenter.Y,
                $"徽章应锚定右下象限，实际中心 {badgeCenter:F1}，按钮中心 {buttonCenter:F1}。");
            Assert.AreEqual(14d, badge.ActualWidth, 0.5, "徽章应保持 14×14 DIP。");
            Assert.AreEqual(14d, badge.ActualHeight, 0.5, "徽章应保持 14×14 DIP。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    private static void AssertFillIconCentered(
        MainWindow window,
        string buttonName,
        double expectedSize)
    {
        var button = window.FindName(buttonName) as Button;
        Assert.IsNotNull(button, $"找不到头部按钮 {buttonName}。");
        var icon = FindDescendants<Viewbox>(button!).Single(IsIconViewbox);
        var rect = BoundsIn(icon, button!);
        AssertCenteredWithin(rect, button!, buttonName, 1.0d);
        // 0.5 DIP 容差吸收高 DPI 显示器上的布局取整。
        Assert.AreEqual(expectedSize, rect.Width, 0.5, $"{buttonName} 图标宽度应约为 {expectedSize} DIP。");
        Assert.AreEqual(expectedSize, rect.Height, 0.5, $"{buttonName} 图标高度应约为 {expectedSize} DIP。");
    }

    private static void AssertStrokeIconCentered(MainWindow window, string buttonName, string label)
    {
        var button = window.FindName(buttonName) as Button;
        Assert.IsNotNull(button, $"找不到头部按钮 {buttonName}。");
        var path = FindDescendants<ShapesPath>(button!).Single();
        var rect = BoundsIn(path, button!);
        AssertCenteredWithin(rect, button!, label, 1.0d);
        // 家族规范声明：显式 14.5×14.5 元素框。Uniform 拉伸把「几何 + 1.5 描边」装进该框：
        // 渲染高度恒撑满 14.5（方形字形如恢复按钮宽度同为 14.5，窄字形如保持展开
        // 「贴缘面板」的宽度由字形宽高比决定），描边粗细不随拉伸缩放。
        Assert.AreEqual(14.5d, path.Width, 0.01, $"{label}元素框宽度应显式 14.5 DIP。");
        Assert.AreEqual(14.5d, path.Height, 0.01, $"{label}元素框高度应显式 14.5 DIP。");
        Assert.AreEqual(14.5d, path.ActualHeight, 0.1, $"{label}渲染高度应为 14.5 DIP。");
        Assert.IsTrue(
            path.ActualWidth is > 4d and <= 14.5d,
            $"{label}渲染宽度应在 4–14.5 DIP（Uniform 拉伸下由字形宽高比决定），实际 {path.ActualWidth:F1}。");
        Assert.AreEqual(1.5d, path.StrokeThickness, 0.01, $"{label}描边应保持 1.5 DIP。");
        var ink = Math.Max(rect.Width + path.StrokeThickness, rect.Height + path.StrokeThickness);
        Assert.IsTrue(
            ink is >= 13.5 and <= 17.5,
            $"{label}墨迹足印应在 13.5–17.5 DIP，实际 {ink:F1}。");
    }

    private static bool IsIconViewbox(Viewbox viewbox) =>
        Math.Abs(viewbox.Width - 16d) < 0.01 && Math.Abs(viewbox.Height - 16d) < 0.01;

    private static void AssertCenteredWithin(Rect iconRect, Button button, string label, double tolerance)
    {
        var centerOffset = new Point(
            (iconRect.Left + iconRect.Right) / 2 - button.ActualWidth / 2,
            (iconRect.Top + iconRect.Bottom) / 2 - button.ActualHeight / 2);
        Assert.IsTrue(
            Math.Abs(centerOffset.X) <= tolerance && Math.Abs(centerOffset.Y) <= tolerance,
            $"{label} 中心应落在按钮中心 {tolerance} DIP 内，实际偏移 ({centerOffset.X:F1},{centerOffset.Y:F1})。");
    }

    private static Rect BoundsIn(FrameworkElement element, FrameworkElement ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(new Point(0, 0), element.RenderSize));
}
