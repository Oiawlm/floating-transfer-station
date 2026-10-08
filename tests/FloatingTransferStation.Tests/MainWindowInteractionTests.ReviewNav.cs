using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 复盘日期导航按钮（1.22.0）：chevron 矢量图标的墨迹居中是契约。字符 Content 的
/// ContentPresenter 居中的是行框（ascent/descent 空白）与字宽而非字形墨迹，符号必然
/// 偏离按钮中心；矢量几何按最终尺寸绘制且墨迹对称于画布中心，Path 不设 Stretch 时
/// 元素布局尺寸=含描边墨迹盒，ContentPresenter 居中的就是墨迹本身。本测试把「墨迹
/// 中心与按钮中心偏差 ≤0.5 DIP」锁成断言：布局层断言图标盒中心=按钮中心；墨迹层
/// 单独渲染图标并按 4× 采样扫描非透明像素求包围盒中心（含描边、消像素量化，不依赖
/// WPF 内部几何换算）。Focusable=True 的键盘可达语义一并锁定（样式注释声明的回归依赖）。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public void ReviewNav_ChevronInkStaysCenteredInTheNavButtons()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        using var store = new LocalStore(paths, new AtomicTextWriter());
        var settings = WindowSettings.Default with
        {
            ReviewMigrationVersion = DailyReviewMigration.CurrentVersion,
            CategoryNames = WindowSettings.Default.WithCategoryName(BoardCategory.Reference, "复盘").CategoryNames
        };
        var window = CreateWindow(new BoardService(), store, settings, new DefaultCaptureCategoryState());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            InvokePrivateTask(window, "LoadReviewDateAsync", DateOnly.FromDateTime(DateTime.Now));
            CompleteLayout(window);

            AssertNavChevronInkCentered((Button)window.FindName("ReviewPreviousButton"));
            AssertNavChevronInkCentered((Button)window.FindName("ReviewNextButton"));
        }
        finally
        {
            GetPrivateField<IDailyReviewStore>(window, "_dailyReviews")?.StopWatching();
            CloseWindowWithoutSaving(window);
        }
    }

    private static void AssertNavChevronInkCentered(Button button)
    {
        Assert.IsTrue(button.Focusable, "导航按钮必须保持可聚焦（键盘可达与复盘失焦回归依赖）");
        var icon = button.Content as System.Windows.Shapes.Path;
        Assert.IsNotNull(icon);

        // 布局层：图标盒（含描边墨迹盒）中心 = 按钮中心（ContentPresenter 双向居中）。
        var iconCenterInButton = icon.TransformToAncestor(button).Transform(
            new Point(icon.ActualWidth / 2, icon.ActualHeight / 2));
        Assert.AreEqual(
            button.ActualWidth / 2, iconCenterInButton.X, 0.25,
            "图标盒未水平居中于按钮");
        Assert.AreEqual(
            button.ActualHeight / 2, iconCenterInButton.Y, 0.25,
            "图标盒未垂直居中于按钮");

        // 墨迹层：单独渲染图标（Path 无背景，画布上只有 chevron 墨迹），4× 采样消
        // 像素量化，扫描非透明像素求墨迹包围盒，断言其中心与图标盒中心偏差 ≤0.5 DIP。
        const double SampleScale = 4d;
        var pixelWidth = (int)Math.Ceiling(icon.ActualWidth * SampleScale);
        var pixelHeight = (int)Math.Ceiling(icon.ActualHeight * SampleScale);
        var bitmap = new RenderTargetBitmap(
            pixelWidth, pixelHeight, 96 * SampleScale, 96 * SampleScale, PixelFormats.Pbgra32);
        bitmap.Render(icon);
        var stride = pixelWidth * 4;
        var pixels = new byte[stride * pixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        var minX = (double)pixelWidth;
        var minY = (double)pixelHeight;
        var maxX = -1d;
        var maxY = -1d;
        for (var y = 0; y < pixelHeight; y++)
        {
            for (var x = 0; x < pixelWidth; x++)
            {
                if (pixels[(y * stride) + (x * 4) + 3] == 0)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        Assert.IsTrue(maxX >= 0, "chevron 未渲染任何墨迹");
        var inkCenterX = ((minX + maxX) / 2) / SampleScale;
        var inkCenterY = ((minY + maxY) / 2) / SampleScale;
        Assert.AreEqual(
            icon.ActualWidth / 2, inkCenterX, 0.5,
            $"{button.Name} chevron 墨迹未水平居中（墨迹中心 {inkCenterX}，盒中心 {icon.ActualWidth / 2}）");
        Assert.AreEqual(
            icon.ActualHeight / 2, inkCenterY, 0.5,
            $"{button.Name} chevron 墨迹未垂直居中（墨迹中心 {inkCenterY}，盒中心 {icon.ActualHeight / 2}）");
    }
}
