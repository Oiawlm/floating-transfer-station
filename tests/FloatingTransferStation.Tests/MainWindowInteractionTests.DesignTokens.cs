using System.Windows;
using System.Windows.Controls;
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
                new CornerRadius(DesignTokens.HeaderActionCornerRadiusPx),
                (CornerRadius)window.FindResource("HeaderActionCornerRadius"));
            Assert.AreEqual(
                new CornerRadius(DesignTokens.PinButtonCornerRadius),
                (CornerRadius)window.FindResource("PinButtonCornerRadius"));
            // 2026-11-08 视觉语言收敛（A19/A20）：两档阶梯资源与全部阶梯外登记
            // 资源逐键镜像 token——资源字面量漂移即互锁失败。
            Assert.AreEqual(
                new CornerRadius(DesignTokens.ControlCornerRadiusPx),
                (CornerRadius)window.FindResource("ControlCornerRadius"),
                "ControlCornerRadius 资源必须镜像 ControlCornerRadiusPx（控件级阶梯）。");
            Assert.AreEqual(
                new CornerRadius(DesignTokens.SurfaceCornerRadiusPx),
                (CornerRadius)window.FindResource("SurfaceCornerRadius"),
                "SurfaceCornerRadius 资源必须镜像 SurfaceCornerRadiusPx（表面级阶梯）。");
            Assert.AreEqual(
                new CornerRadius(DesignTokens.HeaderActionCornerRadiusPx),
                (CornerRadius)window.FindResource("SelectedCountBadgeCornerRadius"),
                "选中计数徽章与头部操作钮同档（HeaderActionCornerRadiusPx）。");
            Assert.AreEqual(
                new CornerRadius(DesignTokens.CheckboxCornerRadius),
                (CornerRadius)window.FindResource("SelectionRingCornerRadius"),
                "SelectionRingCornerRadius 资源必须镜像 CheckboxCornerRadius（识别元素 r5）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void DesignTokens_CardFamilyThicknessResourcesMirrorTokens()
    {
        // 2026-11-08 视觉语言收敛（A10）：卡片族 Margin/Padding 镜像资源接回
        // DesignTokens 间距三 token（此前 token 零引用、实为字面量双口径）。
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            var root = (Thickness)window.FindResource("CardRootMargin");
            Assert.AreEqual(DesignTokens.CardInsetPx, root.Left, "卡根左距=CardInsetPx。");
            Assert.AreEqual(DesignTokens.CardInsetPx, root.Right, "卡根右距=CardInsetPx。");
            Assert.AreEqual(DesignTokens.CardGutterPx, root.Top, "卡根上距=CardGutterPx。");
            Assert.AreEqual(DesignTokens.CardGutterPx, root.Bottom, "卡根下距=CardGutterPx。");

            var boundary = (Thickness)window.FindResource("CardRootBoundaryMargin");
            Assert.AreEqual(DesignTokens.CardInsetPx, boundary.Left);
            Assert.AreEqual(DesignTokens.CardInsetPx, boundary.Right);
            Assert.AreEqual(10, boundary.Top, "边界卡上距 10＝置顶分隔呼吸空间（#27）组成部分，登记值。");
            Assert.AreEqual(DesignTokens.CardGutterPx, boundary.Bottom);

            var padding = (Thickness)window.FindResource("CardPadding");
            Assert.AreEqual(DesignTokens.CardPaddingPx, padding.Left);
            Assert.AreEqual(DesignTokens.CardPaddingPx, padding.Top);
            Assert.AreEqual(DesignTokens.CardPaddingPx, padding.Right);
            Assert.AreEqual(DesignTokens.CardPaddingPx, padding.Bottom);
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
            // 公式与现状互锁：默认字体链（Microsoft YaHei UI, Microsoft YaHei——
            // 链式族串按链首 Microsoft YaHei UI 实测 LineSpacing 1.27001953125 计
            // 度量）的推导行高必须等于 CardTextLineHeight 字面量 19（2026-11-02
            // 默认改微软雅黑链前 Segoe UI 链为 20）。
            Assert.AreEqual(
                InterfaceFontManager.DeriveCardTextLineHeight(family),
                (double)window.FindResource("CardTextLineHeight"),
                "默认字体的推导行高必须与 CardTextLineHeight 现状 19 一致。");
            // 行数上限倍率资源与既有几何一致（默认 95=5×19、38=2×19）。
            Assert.AreEqual(
                19d * DesignTokens.CardTextMaxVisibleLines,
                (double)window.FindResource("CardTextPreviewMaxHeight"));
            Assert.AreEqual(
                19d * DesignTokens.CardNoteMaxVisibleLines,
                (double)window.FindResource("CardNoteMaxHeight"));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void DesignTokens_RailColumnWidthMirrorsTheStructuralTabWidth()
    {
        // 设计契约 #54（2026-11-04 回归修复）：轨道列宽是 WindowSettings.TabWidth
        // 的 XAML 镜像——结构常量内容无关（58，禁止为内容加宽），第二列是唯一
        // 消费面，字面漂移即互锁失败。
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            var layoutRoot = (Grid)window.FindName("LayoutRoot");
            Assert.IsNotNull(layoutRoot, "LayoutRoot 必须存在。");
            Assert.HasCount(2, layoutRoot.ColumnDefinitions);
            Assert.AreEqual(
                new GridLength(WindowSettings.TabWidth),
                layoutRoot.ColumnDefinitions[1].Width,
                "轨道列宽必须逐值镜像 WindowSettings.TabWidth（58，内容无关结构常量）。");
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
