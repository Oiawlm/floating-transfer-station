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
        // F7 守护（Issue #186 拉伸填充修订；取代 Issue #176 的「Margin 镜像节距 92」
        // 断言——契约 #30 修订留痕）：瓦片几何唯一计算来源=LauncherGridMath（数值
        // 唯一来源=DesignTokens），承载=LauncherTilePanel 自行排版。布局事实断言：
        // 瓦片高=token、瓦片宽=LauncherGridMath.TileWidth(视口, 列数)、相邻瓦片水平
        // 节距=瓦片宽+沟道、面板布局宽=视口宽（拉伸填充无死区）、列数=公式值。
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
            var viewport = window.LauncherScroll.ViewportWidth;
            Assert.IsTrue(
                double.IsFinite(viewport) && viewport > 0,
                $"前置：展开态视口宽有效（实际 {viewport}）。");
            var columns = LauncherGridMath.ColumnCount(viewport);
            var expectedTileWidth = LauncherGridMath.TileWidth(viewport, columns);
            var firstTile = FindLauncherTile(window, catalog.Items[0]);
            Assert.IsNotNull(firstTile);
            Assert.AreEqual(DesignTokens.LauncherTileHeightPx, firstTile.ActualHeight, 0.01, "瓦片高=token。");
            Assert.AreEqual(expectedTileWidth, firstTile.ActualWidth, 0.05, "瓦片宽=LauncherGridMath 摊分（拉伸填充）。");
            Assert.AreEqual(
                0,
                firstTile.Margin.Left + firstTile.Margin.Right,
                0.01,
                "瓦片根无自带 Margin（沟道由面板排版间距承载）。");
            if (FindLauncherTile(window, catalog.Items[1]) is { } secondTile)
            {
                Assert.AreEqual(
                    expectedTileWidth + DesignTokens.LauncherTileGutterPx,
                    secondTile.TranslatePoint(new Point(), window).X -
                        firstTile.TranslatePoint(new Point(), window).X,
                    0.5,
                    "相邻瓦片节距=瓦片宽+沟道（布局事实）。");
            }

            var panel = FindDescendant<LauncherTilePanel>(window.LauncherTileGrid);
            Assert.IsNotNull(panel, "ItemsPanel=LauncherTilePanel。");
            Assert.AreEqual(columns, panel.EffectiveColumnCount, 0, "面板生效列数=公式值。");
            Assert.AreEqual(viewport, panel.ActualWidth, 0.5, "面板布局宽=视口（拉伸填充无死区）。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void DesignTokens_DefaultInterfaceFontIsMirroredByWpfFontResource()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            var family = window.FindResource("InterfaceFontFamily") as FontFamily;
            Assert.IsNotNull(family, "InterfaceFontFamily 资源必须存在。");
            Assert.AreEqual(
                DesignTokens.InterfaceFontFamily,
                family.Source,
                "XAML 默认字体资源必须逐字镜像 DesignTokens.InterfaceFontFamily（默认字体串唯一来源）。");
            // 公式与现状互锁：默认字体（Segoe UI / Segoe UI Variable 实测 LineSpacing
            // 同为 1.330078）的推导行高必须等于 CardTextLineHeight 字面量 20。
            Assert.AreEqual(
                InterfaceFontManager.DeriveCardTextLineHeight(family),
                (double)window.FindResource("CardTextLineHeight"),
                "默认字体的推导行高必须与 CardTextLineHeight 现状 20 一致。");
            // 行数上限倍率资源与既有几何一致（默认 100=5×20、40=2×20）。
            Assert.AreEqual(
                20d * DesignTokens.CardTextMaxVisibleLines,
                (double)window.FindResource("CardTextPreviewMaxHeight"));
            Assert.AreEqual(
                20d * DesignTokens.CardNoteMaxVisibleLines,
                (double)window.FindResource("CardNoteMaxHeight"));
        }
        finally
        {
            CloseWindow(window);
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
