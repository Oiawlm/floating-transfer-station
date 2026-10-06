using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using FloatingTransferStation.Controls;
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

    [STATestMethod]
    public void BoardList_RealizesOnlyVisibleContainersForLargeCategories()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        for (var index = 0; index < 2000; index++)
        {
            board.AddText($"虚拟化条目 {index}");
        }

        var window = CreateWindow(directory, board);
        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");

            // 虚拟化 + Recycling 生效的直接证据：两千条目只有视口附近的少量容器被实现。
            var realizedContainers = FindDescendants<ListBoxItem>(list).Count();
            Assert.IsTrue(
                realizedContainers is > 0 and < 100,
                $"两千条目下实现的容器数应远小于条目总数，实际 {realizedContainers}。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void BatchStructuralChange_DoesNotResetScrollToTop()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var items = Enumerable.Range(0, 200)
            .Select(index => board.AddText($"滚动条目 {index}"))
            .ToArray();
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var viewer = FindDescendants<ScrollViewer>(list).Single();
            viewer.ScrollToVerticalOffset(800);
            CompleteLayout(window);
            Assert.IsTrue(viewer.VerticalOffset > 400, "前置：已滚动到中部。");

            // 单 Reset 的批量重排（置顶最底条目）后视口不得跳回顶部；
            // 旧实现 Clear+逐条 Add 会在 Count 归零时把偏移钳到 0。
            board.SetPinnedMany([items[0].Id], true);
            CompleteLayout(window);

            Assert.IsTrue(
                viewer.VerticalOffset > 400,
                $"批量结构变更后视口跳回顶部（offset={viewer.VerticalOffset}）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    private static void WriteCardPng(string path, int width, int height)
    {
        const int bytesPerPixel = 4;
        var stride = width * bytesPerPixel;
        var pixels = new byte[stride * height];
        Array.Fill(pixels, (byte)0x7F);
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    [STATestMethod]
    public void CardText_BindsBoundedPreviewInsteadOfFullText()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var longText = new string('长', 2000);
        board.AddText(longText);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var body = FindDescendants<TextBlock>(list)
                .Single(candidate => candidate.Text?.Length == 600);

            // 显示层排版只用有界预览（含换行重排），全文仍留在 Text 上供搜索/编辑/复制。
            Assert.AreEqual(longText[..600], body.Text);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardThumbnail_DecodesAtBucketedDisplayWidthAfterLayout()
    {
        using var directory = new TestDirectory();
        var imagePath = Path.Combine(directory.Root, "wide-card.png");
        WriteCardPng(imagePath, width: 1200, height: 600);
        var board = new BoardService();
        board.AddImage(Guid.NewGuid(), "images/wide-card.png", imagePath, BoardCategory.CustomerOriginal);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.CustomerOriginal);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var thumbnail = FindDescendants<AsyncThumbnailImage>(list).Single();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (thumbnail.Source is null && DateTime.UtcNow < deadline)
            {
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
                // 泵帧不必然驱动布局：可见性翻转后的重排需要显式 UpdateLayout。
                window.UpdateLayout();
            }

            var source = (BitmapSource?)thumbnail.Source;
            Assert.IsNotNull(source, "布局完成后应按显示宽度解码缩略图。");
            var scale = VisualTreeHelper.GetDpi(thumbnail).PixelsPerDip;
            var pixels = (int)Math.Ceiling(thumbnail.ActualWidth * Math.Max(1d, scale));
            // 解码宽 = max(基线 512, 显示宽×DPI 的 128px 上取桶)，上限 1024。
            var expected = Math.Max(512, Math.Min(((pixels + 127) / 128) * 128, 1024));
            Assert.AreEqual(expected, source.PixelWidth, "解码宽应贴合显示宽度×DPI 的桶（不低于基线）。");
            Assert.IsTrue(source.IsFrozen);
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
