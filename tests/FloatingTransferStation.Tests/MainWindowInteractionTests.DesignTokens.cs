using System.Windows;
using System.Windows.Media;
using FloatingTransferStation.Design;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public void DesignTokens_AreMirroredByWpfBrushResources()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        DesignThemeManager.Apply(window, DesignTheme.Light);

        try
        {
            AssertTokenBrush(window, "WindowShellBrush", DesignTokens.WindowShellTintHex);
            AssertTokenBrush(window, "WindowShellOpaqueBrush", DesignTokens.WindowShellHex);
            AssertTokenBrush(window, "TabRailBrush", DesignTokens.TabRailHex);
            AssertTokenBrush(window, "CardBrush", DesignTokens.CardHex);
            AssertTokenBrush(window, "BorderBrush", DesignTokens.BorderHex);
            AssertTokenBrush(window, "PrimaryTextBrush", DesignTokens.PrimaryTextHex);
            AssertTokenBrush(window, "SecondaryTextBrush", DesignTokens.SecondaryTextHex);
            AssertTokenBrush(window, "AccentBrush", DesignTokens.AccentHex);
            AssertTokenBrush(window, "DangerBrush", DesignTokens.DangerHex);
            AssertTokenBrush(window, "HeaderBrush", DesignTokens.HeaderHex);
            AssertTokenBrush(window, "HoverSurfaceBrush", DesignTokens.HoverSurfaceHex);
            AssertTokenBrush(window, "HoverBorderBrush", DesignTokens.HoverBorderHex);
            AssertTokenBrush(window, "SelectedCardBrush", DesignTokens.SelectedCardHex);
            AssertTokenBrush(window, "SelectedCardBorderBrush", DesignTokens.SelectedCardBorderHex);
            AssertTokenBrush(window, "ActiveCategoryBrush", DesignTokens.ActiveCategoryHex);
            AssertTokenBrush(window, "DropTargetBrush", DesignTokens.DropTargetHex);
            AssertTokenBrush(window, "ScrollThumbBrush", DesignTokens.ScrollThumbHex);
            AssertTokenBrush(window, "ScrollThumbHoverBrush", DesignTokens.ScrollThumbHoverHex);
            AssertTokenBrush(window, "StatusSurfaceBrush", DesignTokens.StatusSurfaceHex);
            AssertTokenBrush(window, "OnAccentBrush", DesignTokens.OnAccentHex);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void DesignTokens_AreMirroredByWpfCornerRadiusResources()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            Assert.AreEqual(
                new CornerRadius(DesignTokens.DwmCornerRadius),
                (CornerRadius)window.FindResource("ShellCornerRadius"));
            Assert.AreEqual(
                new CornerRadius(DesignTokens.CardCornerRadius),
                (CornerRadius)window.FindResource("CardCornerRadius"));
            Assert.AreEqual(
                new CornerRadius(DesignTokens.ControlCornerRadius),
                (CornerRadius)window.FindResource("HeaderActionCornerRadius"));
            Assert.AreEqual(
                new CornerRadius(DesignTokens.PinButtonCornerRadius),
                (CornerRadius)window.FindResource("PinButtonCornerRadius"));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void WindowAnimationDurations_AreSourcedFromDesignTokens()
    {
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(DesignTokens.PanelExpandContentMs),
            GetPrivateStaticField<TimeSpan>("ExpandContentAnimationDuration"));
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(DesignTokens.PanelSwitchContentMs),
            GetPrivateStaticField<TimeSpan>("SwitchContentAnimationDuration"));
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(DesignTokens.ReducedMotionFadeMs),
            GetPrivateStaticField<TimeSpan>("ReducedMotionContentAnimationDuration"));
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(DesignTokens.CategoryRevealMs),
            GetPrivateStaticField<TimeSpan>("CategoryRevealAnimationDuration"));
        Assert.AreEqual(
            DesignTokens.ContentEntranceOffsetPx,
            GetPrivateStaticField<double>("CategoryRevealOffset"));
    }

    [STATestMethod]
    public void DesignTokens_LauncherTileGeometryMirrorsTokens()
    {
        // F7 守护（Issue #176 视觉一致性）：瓦片几何唯一数值来源=DesignTokens——
        // 瓦片 84×100 与节距 92（宽+沟道 8）经 x:Static 同源引用；XAML Margin 4×4
        // 恰为沟道的一半（两侧合计=沟道）。三处硬编码漂移（XAML 84、代码 84+8、
        // 焦点导航 /92）由此锁定。
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var items = Enumerable.Range(0, 4)
            .Select(index => new LauncherItem(
                Guid.NewGuid(),
                LauncherKind.Application,
                $"{app.Path}.{index}",
                $"应用{index}",
                DateTimeOffset.UtcNow))
            .ToArray();
        var window = CreateLauncherWindowSync(directory, board, store, starter, items);

        try
        {
            window.Show();
            // 入场交错动画是 async void＋Task.Delay：测试宿主无同步上下文会让续体
            // 落线程池（生产 Dispatcher 同步上下文不受影响），关闭以短路。
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();
            var firstTile = FindLauncherTile(window, catalog.Items[0]);
            Assert.IsNotNull(firstTile);
            Assert.AreEqual(DesignTokens.LauncherTileWidthPx, firstTile.ActualWidth, 0.01, "瓦片宽=token。");
            Assert.AreEqual(DesignTokens.LauncherTileHeightPx, firstTile.ActualHeight, 0.01, "瓦片高=token。");
            Assert.AreEqual(
                DesignTokens.LauncherTileGutterPx,
                firstTile.Margin.Left + firstTile.Margin.Right,
                0.01,
                "瓦片左右 Margin 合计=沟道 token。");
            Assert.AreEqual(
                DesignTokens.LauncherTileWidthPx + DesignTokens.LauncherTileGutterPx,
                DesignTokens.LauncherTilePitchPx,
                0.01,
                "节距=宽+沟道（列数公式与网格宽度的唯一来源）。");
            if (FindLauncherTile(window, catalog.Items[1]) is { } secondTile)
            {
                Assert.AreEqual(
                    DesignTokens.LauncherTilePitchPx,
                    secondTile.TranslatePoint(new Point(), window).X -
                        firstTile.TranslatePoint(new Point(), window).X,
                    0.5,
                    "相邻瓦片节距=token（布局事实）。");
            }
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    private static void AssertTokenBrush(MainWindow window, string resourceKey, string tokenHex)
    {
        var brush = window.FindResource(resourceKey) as SolidColorBrush;
        Assert.IsNotNull(brush, resourceKey);
        Assert.AreEqual(ParseTokenColor(tokenHex), brush.Color, resourceKey);
    }

    private static Color ParseTokenColor(string tokenHex)
    {
        var value = Convert.ToInt32(tokenHex[1..], 16);
        if (tokenHex.Length == 9)
        {
            return Color.FromArgb(
                (byte)(value >> 24),
                (byte)(value >> 16),
                (byte)(value >> 8),
                (byte)value);
        }

        return Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }
}
