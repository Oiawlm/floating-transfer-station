using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
/// 双层箭头（« »跳到上/下一个有记录日期，2026-11 批次 W5）同范式锁定，另见文件末
/// 跳转状态机与可用性用例。
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

            AssertNavChevronInkCentered((Button)window.FindName("ReviewJumpPreviousButton"));
            AssertNavChevronInkCentered((Button)window.FindName("ReviewPreviousButton"));
            AssertNavChevronInkCentered((Button)window.FindName("ReviewNextButton"));
            AssertNavChevronInkCentered((Button)window.FindName("ReviewJumpNextButton"));
        }
        finally
        {
            GetPrivateField<IDailyReviewStore>(window, "_dailyReviews")?.StopWatching();
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void ReviewNav_JumpButtonsSkipBlankDaysToNearestRecordedDate()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        Directory.CreateDirectory(paths.ReviewsDirectory);
        var today = DateOnly.FromDateTime(DateTime.Now);
        void WriteReview(DateOnly date, string content) =>
            File.WriteAllText(Path.Combine(paths.ReviewsDirectory, $"{date:yyyy-MM-dd}.md"), content);
        WriteReview(today.AddDays(-5), "五天前的记录");
        WriteReview(today.AddDays(-3), " \n\t "); // 空白文件＝无记录：跳转目标必须跳过该日
        WriteReview(today.AddDays(-2), "前天的记录");
        WriteReview(today, "今天的记录");

        var window = CreateReviewNavWindow(directory);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);

            var previous = (Button)window.FindName("ReviewPreviousButton")!;
            var next = (Button)window.FindName("ReviewNextButton")!;

            // 今天：向前目标＝T-2（跳过空白 T-3 与无文件日，证明「空白≠记录」）；
            // 向后无目标（未来拒绝——即便今天有记录也不构成今天的向前目标）。
            WaitForReviewJumpAvailability(window, previousEnabled: true, nextEnabled: false);
            Assert.IsTrue(previous.IsEnabled, "单步左箭头可用性不因双层箭头改变（今天）");
            Assert.IsFalse(next.IsEnabled, "单步右箭头可用性不因双层箭头改变（今天）");

            ClickReviewButtonAndWaitForDate(window, (Button)window.FindName("ReviewJumpPreviousButton")!, FormatReviewDate(today.AddDays(-2)));
            // T-2：向前目标＝T-5（空白 T-3 不是目标）；向后目标＝今天（今天有记录，
            // 当前在其之前——Next 语义＝可跳到今天）。
            WaitForReviewJumpAvailability(window, previousEnabled: true, nextEnabled: true);

            ClickReviewButtonAndWaitForDate(window, (Button)window.FindName("ReviewJumpNextButton")!, "今天");
            WaitForReviewJumpAvailability(window, previousEnabled: true, nextEnabled: false);

            ClickReviewButtonAndWaitForDate(window, (Button)window.FindName("ReviewJumpPreviousButton")!, FormatReviewDate(today.AddDays(-2)));
            ClickReviewButtonAndWaitForDate(window, (Button)window.FindName("ReviewJumpPreviousButton")!, FormatReviewDate(today.AddDays(-5)));
            // T-5：更早无记录→左跳禁用；右跳目标＝T-2。
            WaitForReviewJumpAvailability(window, previousEnabled: false, nextEnabled: true);

            // 单步行为不变：T-5 单步退一天＝T-6（无记录也照停，不跳过）；再进一天回 T-5。
            ClickReviewButtonAndWaitForDate(window, previous, FormatReviewDate(today.AddDays(-6)));
            ClickReviewButtonAndWaitForDate(window, next, FormatReviewDate(today.AddDays(-5)));
        }
        finally
        {
            CloseReviewNavWindow(window);
        }
    }

    [STATestMethod]
    public void ReviewNav_JumpButtonsDisabledWithoutRecordedDates()
    {
        using var directory = new TestDirectory();
        var window = CreateReviewNavWindow(directory);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);

            // 无任何记录：两侧都无目标→双层箭头禁用；单步语义保持（Previous 可用）。
            WaitForReviewJumpAvailability(window, previousEnabled: false, nextEnabled: false);
            var previous = (Button)window.FindName("ReviewPreviousButton")!;
            var next = (Button)window.FindName("ReviewNextButton")!;
            Assert.IsTrue(previous.IsEnabled, "无记录不改变单步左箭头可用性");
            Assert.IsFalse(next.IsEnabled, "无记录不改变单步右箭头可用性");

            // 单步照常工作：退一天后仍无记录，双层箭头保持禁用（可用性并入同一刷新点）。
            ClickReviewButtonAndWaitForDate(
                window,
                previous,
                FormatReviewDate(DateOnly.FromDateTime(DateTime.Now).AddDays(-1)));
            WaitForReviewJumpAvailability(window, previousEnabled: false, nextEnabled: false);
            Assert.IsTrue(next.IsEnabled, "退一天后单步右箭头恢复可用（今天有目标）");
        }
        finally
        {
            CloseReviewNavWindow(window);
        }
    }

    // 线程宿主（W4 批次取证同源，2026-11-03）：MSTest STA 线程不装 Dispatcher 同步
    // 上下文（WPF 只在 Application 主线程安装），日期切换链 await LocalStore 后的
    // UI 写入依赖该上下文回分发器——小文件 IO 缓存命中同步完成时碰巧不触发，真实
    // 异步完成时续体落线程池即跨线程崩溃。仅在本文件用例期间安装与生产线程同语义
    // 的上下文，关窗恢复；全局安装会让其他用例死锁（CI run 41676803302 实证）。
    private static bool _reviewNavContextInstalled;

    private static MainWindow CreateReviewNavWindow(TestDirectory directory)
    {
        var window = CreateReviewWindow(directory);
        if (!_reviewNavContextInstalled && System.Threading.SynchronizationContext.Current is null)
        {
            System.Threading.SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(window.Dispatcher));
            _reviewNavContextInstalled = true;
        }

        return window;
    }

    private static void CloseReviewNavWindow(MainWindow window)
    {
        CloseReviewWindow(window);
        if (_reviewNavContextInstalled)
        {
            System.Threading.SynchronizationContext.SetSynchronizationContext(null);
            _reviewNavContextInstalled = false;
        }
    }

    private static string FormatReviewDate(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>泵分发器直到双层箭头可用性达到期望组合（5 秒上限，超时由断言暴露）。</summary>
    private static void WaitForReviewJumpAvailability(MainWindow window, bool previousEnabled, bool nextEnabled)
    {
        var jumpPrevious = (Button)window.FindName("ReviewJumpPreviousButton")!;
        var jumpNext = (Button)window.FindName("ReviewJumpNextButton")!;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while ((jumpPrevious.IsEnabled != previousEnabled || jumpNext.IsEnabled != nextEnabled)
               && DateTime.UtcNow < deadline)
        {
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
        }

        Assert.AreEqual(previousEnabled, jumpPrevious.IsEnabled, "双层左箭头可用性与期望不符");
        Assert.AreEqual(nextEnabled, jumpNext.IsEnabled, "双层右箭头可用性与期望不符");
    }

    private static void ClickReviewButtonAndWaitForDate(MainWindow window, Button button, string expectedStateText)
    {
        var state = (TextBlock)window.FindName("ReviewDateState")!;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (state.Text != expectedStateText && DateTime.UtcNow < deadline)
        {
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
        }

        Assert.AreEqual(expectedStateText, state.Text, $"{button.Name} 点击后日期状态未达期望");
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
