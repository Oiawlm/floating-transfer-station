using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;

namespace FloatingTransferStation.Tests;

public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public void CardText_LocksDisplayFormattingForCrispSmallText()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("清晰的卡片正文");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var body = FindDescendants<TextBlock>(list)
                .Single(candidate => candidate.Text == "清晰的卡片正文");
            AssertHasDisplayFormatting(body, "卡片正文");

            var editor = (TextBox)window.FindName("ReviewEditor");
            AssertHasDisplayFormatting(editor, "复盘编辑器");

            var statusStyle = (Style)window.FindResource("StatusTextStyle");
            var statusFormatting = statusStyle.Setters.OfType<Setter>().SingleOrDefault(
                setter => setter.Property == System.Windows.Media.TextOptions.TextFormattingModeProperty);
            Assert.IsNotNull(statusFormatting, "StatusTextStyle 必须显式设置文本格式化模式。");
            Assert.AreEqual(
                System.Windows.Media.TextFormattingMode.Display,
                statusFormatting.Value,
                "状态文本的小字号必须按像素对齐(Display)保持清晰。");

            var badge = (TextBlock)window.FindName("SelectedCountText");
            AssertHasDisplayFormatting(badge, "已选计数徽标");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardTemplate_RendersTextOutsideTheShadowEffectSubtree()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("不在阴影位图里的文字");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var body = FindDescendants<TextBlock>(list)
                .Single(candidate => candidate.Text == "不在阴影位图里的文字");
            var shadowEffect = (Effect)window.FindResource("CardShadowEffect");

            // 文字到列表项之间的任何祖先都不得携带 Effect：带 Effect 的元素会把
            // 整个子树渲染进中间位图，ClearType 随之被强制关闭（模糊主因）。
            ListBoxItem? container = null;
            var walker = (DependencyObject?)body;
            while (walker is not null)
            {
                Assert.IsNull(
                    (walker as UIElement)?.Effect,
                    $"卡片文字的祖先 {walker.GetType().Name} 不得携带 Effect。");
                walker = VisualTreeHelper.GetParent(walker);
                if (walker is ListBoxItem found)
                {
                    container = found;
                    break;
                }
            }

            Assert.IsNotNull(container);

            // 阴影仍须存在：由独立的、不承载内容也不参与命中的阴影层表达，形状与卡片一致。
            var shadowLayer = FindDescendants<Border>(container).SingleOrDefault(border =>
                ReferenceEquals(border.Effect, shadowEffect));
            Assert.IsNotNull(shadowLayer, "卡片必须有独立阴影层承载 CardShadowEffect。");
            Assert.AreEqual(
                0,
                VisualTreeHelper.GetChildrenCount(shadowLayer),
                "阴影层不得承载任何内容。");
            Assert.IsFalse(shadowLayer.IsHitTestVisible, "阴影层不得参与命中测试。");
            Assert.IsInstanceOfType(
                shadowLayer.Background,
                typeof(SolidColorBrush),
                "阴影层底色必须是不透明画刷。");
            Assert.AreEqual(
                ((SolidColorBrush)window.FindResource("CardBrush")).Color,
                ((SolidColorBrush)shadowLayer.Background).Color,
                "阴影层底色与卡片底色一致，保证阴影形状与卡片一致。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardText_UsesNaturalLineHeightForFourteenPixelBody()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("行高修正后的卡片正文");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var body = FindDescendants<TextBlock>(list)
                .Single(candidate => candidate.Text == "行高修正后的卡片正文");

            Assert.AreEqual(20d, body.LineHeight, 0d, "正文行高应为 20 DIP（14px 字号的自然行高）。");
            Assert.AreEqual(
                LineStackingStrategy.BlockLineHeight,
                body.LineStackingStrategy,
                "正文行距语义应保持 BlockLineHeight。");
            Assert.AreEqual(100d, body.MaxHeight, 0d, "正文最大高度应为 100 DIP（5 行 × 20）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    private static void AssertHasDisplayFormatting(FrameworkElement element, string role)
    {
        // 断言生效值：带内联 Style 的元素（如卡片正文）BAML 可能不给本地值槽位，
        // 渲染上生效的 TextFormattingMode 才是要锁定的契约。
        Assert.AreEqual(
            System.Windows.Media.TextFormattingMode.Display,
            System.Windows.Media.TextOptions.GetTextFormattingMode(element),
            $"{role} 的小字号文本必须按像素对齐(Display)保持清晰。");
    }
}
