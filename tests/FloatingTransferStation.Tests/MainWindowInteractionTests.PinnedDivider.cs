using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 置顶分区分隔样式（契约 #27）：分隔元素宿主在 ListBoxItem 容器模板行 0，只由
/// StartsNormalRegion 驱动可见性（Recycling 复用下触发器随 DataContext 重评），
/// 四态外观经窗口级槽位资源换装（ApplyPreferences 唯一管线）。锁定：仅边界容器
/// 可见、无置顶不显示、四态可观察属性、None 零渲染且边界卡几何与既有间距逐
/// DIP 一致、分隔不改变卡片宽与 viewport 几何（#15/#23 不受影响）。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    private const double DividerGeometryTolerance = 0.25;

    private static Line BoundaryDivider(MainWindow window, ListBox list, object item)
    {
        var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(item);
        Assert.IsNotNull(container, "目标卡片容器应已实现。");
        return (Line?)container.Template.FindName("PinnedDivider", container)
            ?? throw new AssertFailedException("容器模板必须携带 PinnedDivider 分隔元素。");
    }

    /// <summary>建一个「置顶锚点 + 3 条普通卡」的窗口并展开收件箱（边界在第二条）。</summary>
    private static MainWindow CreatePinnedDividerWindow(TestDirectory directory, out BoardService board)
    {
        board = new BoardService();
        var anchor = board.AddText("置顶锚点卡片");
        board.SetPinnedMany([anchor.Id], true);
        for (var index = 0; index < 3; index++)
        {
            board.AddText($"普通卡片 {index}");
        }

        return CreateWindow(directory, board);
    }

    private static object BoundaryItem(BoardService board) =>
        board.Items(BoardCategory.Inbox).First(candidate => candidate.StartsNormalRegion);

    [STATestMethod]
    public void PinnedDivider_AppearsOnlyOnTheBoundaryContainer()
    {
        using var directory = new TestDirectory();
        var window = CreatePinnedDividerWindow(directory, out var board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var items = board.Items(BoardCategory.Inbox);

            var boundary = BoundaryDivider(window, list, BoundaryItem(board));
            Assert.IsTrue(boundary.Visibility == Visibility.Visible, "边界容器（普通区首项）必须显示分隔。");
            Assert.AreEqual(1d, boundary.StrokeThickness, DividerGeometryTolerance, "默认样式为 1 DIP 实线。");
            Assert.AreSame(window.FindResource("PinnedDividerSolidStyle"), boundary.Style, "默认装槽样式必须是实线。");

            foreach (var item in items.Where(candidate => !candidate.StartsNormalRegion))
            {
                Assert.AreEqual(
                    Visibility.Collapsed,
                    BoundaryDivider(window, list, item).Visibility,
                    "置顶区容器不得显示分隔。");
            }

            foreach (var item in items.Where(candidate => !candidate.IsPinned && !candidate.StartsNormalRegion))
            {
                Assert.AreEqual(
                    Visibility.Collapsed,
                    BoundaryDivider(window, list, item).Visibility,
                    "非边界普通容器不得显示分隔。");
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void PinnedDivider_NoPinnedItemsShowsNothing()
    {
        using var directory = new TestDirectory();
        var window = CreatePinnedDividerWindow(directory, out var board);
        board.SetPinnedMany([board.Items(BoardCategory.Inbox).First(candidate => candidate.IsPinned).Id], false);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");

            foreach (var item in board.Items(BoardCategory.Inbox))
            {
                Assert.IsFalse(item.StartsNormalRegion, "前置条件：无置顶项时不存在边界标记。");
                var divider = BoundaryDivider(window, list, item);
                Assert.AreEqual(Visibility.Collapsed, divider.Visibility, "无置顶项时不得显示分隔。");
                Assert.AreEqual(0d, divider.ActualHeight, DividerGeometryTolerance, "分隔行不得占布局空间。");
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void PinnedDivider_StyleSwapAtRuntimeUpdatesRealizedContainers()
    {
        using var directory = new TestDirectory();
        var window = CreatePinnedDividerWindow(directory, out var board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var divider = BoundaryDivider(window, list, BoundaryItem(board));

            window.ApplyPreferences(AppPreferences.Default with { PinnedDivider = PinnedDividerStyle.Dashed });
            CompleteLayout(window);
            Assert.AreSame(window.FindResource("PinnedDividerDashedStyle"), divider.Style, "换装必须即时反映（无重启）。");
            Assert.IsNotNull(divider.StrokeDashArray, "虚线必须带 DashArray。");
            Assert.AreEqual(4d, divider.StrokeDashArray[0], DividerGeometryTolerance);
            Assert.AreEqual(3d, divider.StrokeDashArray[1], DividerGeometryTolerance);
            Assert.AreEqual(1d, divider.StrokeThickness, DividerGeometryTolerance);

            window.ApplyPreferences(AppPreferences.Default with { PinnedDivider = PinnedDividerStyle.AccentBar });
            CompleteLayout(window);
            Assert.AreSame(window.FindResource("PinnedDividerAccentBarStyle"), divider.Style, "换装必须即时反映（无重启）。");
            Assert.AreSame(window.FindResource("AccentBrush"), divider.Stroke, "强调条必须用 AccentBrush。");
            Assert.AreEqual(2d, divider.StrokeThickness, DividerGeometryTolerance, "强调条为 2 DIP。");

            window.ApplyPreferences(AppPreferences.Default with { PinnedDivider = PinnedDividerStyle.Solid });
            CompleteLayout(window);
            Assert.AreSame(window.FindResource("PinnedDividerSolidStyle"), divider.Style, "换装必须即时反映（无重启）。");
            Assert.IsTrue(
                divider.StrokeDashArray is null || divider.StrokeDashArray.Count == 0,
                "实线不得残留虚线 DashArray。");
            Assert.AreSame(window.FindResource("BorderBrush"), divider.Stroke, "实线用 BorderBrush。");

            window.ApplyPreferences(AppPreferences.Default with { PinnedDivider = PinnedDividerStyle.None });
            CompleteLayout(window);
            Assert.AreSame(window.FindResource("PinnedDividerNoneStyle"), divider.Style, "换装必须即时反映（无重启）。");
            Assert.AreEqual(
                Visibility.Visible,
                divider.Visibility,
                "None 态边界可见性仍由 StartsNormalRegion 驱动（触发器命中）。");
            Assert.AreEqual(0d, divider.Height, "None 态必须零高。");
            Assert.IsNull(divider.Stroke, "None 态必须空描边（触发器命中也不渲染）。");
            Assert.AreEqual(0d, divider.ActualHeight, DividerGeometryTolerance, "None 态不得占布局空间。");

            // 主题切换后 DynamicResource 必须重解析到新主题画刷（分隔跟随换肤）。
            window.ApplyPreferences(AppPreferences.Default with
            {
                PinnedDivider = PinnedDividerStyle.Solid,
                ThemeMode = ThemePreference.Dark
            });
            CompleteLayout(window);
            Assert.AreSame(window.FindResource("BorderBrush"), divider.Stroke, "主题切换后实线必须跟随新主题 BorderBrush。");
            window.ApplyPreferences(AppPreferences.Default with { ThemeMode = ThemePreference.FollowSystem });
            CompleteLayout(window);
            Assert.AreSame(window.FindResource("BorderBrush"), divider.Stroke, "回跟随系统主题后实线必须再次重解析。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void PinnedDivider_NoneKeepsBoundaryGeometryIdenticalToSpacingOnlyBaseline()
    {
        using var directory = new TestDirectory();
        var window = CreatePinnedDividerWindow(directory, out var board);
        window.ApplyPreferences(AppPreferences.Default with { PinnedDivider = PinnedDividerStyle.None });

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var boundary = BoundaryDivider(window, list, BoundaryItem(board));
            Assert.AreEqual(0d, boundary.ActualHeight, DividerGeometryTolerance, "None 态分隔行必须零高。");

            var cardRootStyle = (Style)window.FindResource("CardRootStyle");
            var boundaryCard = RealizedCardRoot(window, list, BoundaryItem(board));
            Assert.IsTrue(ReferenceEquals(boundaryCard.Style, cardRootStyle));
            Assert.AreEqual(
                new Thickness(12, 10, 12, 4),
                boundaryCard.Margin,
                "None 态边界卡外距必须与既有「仅间距」现状 12,10,12,4 逐 DIP 一致。");

            var normalItem = board.Items(BoardCategory.Inbox)
                .First(candidate => !candidate.IsPinned && !candidate.StartsNormalRegion);
            var normalCard = RealizedCardRoot(window, list, normalItem);
            Assert.AreEqual(
                new Thickness(12, 4, 12, 4),
                normalCard.Margin,
                "None 态非边界卡外距必须保持既有 12,4。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void PinnedDivider_DoesNotChangeCardWidthOrViewportGeometry()
    {
        using var directory = new TestDirectory();
        var window = CreatePinnedDividerWindow(directory, out var board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var presenter = FindDescendant<ScrollContentPresenter>(list);
            Assert.IsNotNull(presenter, "BoardList 必须有 ScrollContentPresenter。");
            var boundaryItem = BoundaryItem(board);
            var solidCard = RealizedCardRoot(window, list, boundaryItem);
            var solidWidth = solidCard.ActualWidth;
            var solidViewportWidth = presenter.ActualWidth;

            window.ApplyPreferences(AppPreferences.Default with { PinnedDivider = PinnedDividerStyle.None });
            CompleteLayout(window);
            var noneCard = RealizedCardRoot(window, list, boundaryItem);

            Assert.AreEqual(
                solidViewportWidth,
                presenter.ActualWidth,
                ScrollGeometryTolerance,
                "分隔样式切换不得改变 viewport 几何（#23 语义）。");
            Assert.AreEqual(
                solidWidth,
                noneCard.ActualWidth,
                ScrollGeometryTolerance,
                "分隔样式切换不得改变卡片宽度（卡片全宽拉伸不含分隔行）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    /// <summary>1.38.0 发布证据（FTS_PINNED_DIVIDER_EVIDENCE_DIR 设置时产出真实
    /// WPF 渲染截图，中性测试数据）：展开态面板四样式各一张 + 设置窗外观页
    /// 「置顶分隔」节一张。</summary>
    [STATestMethod]
    public void Evidence_CapturePinnedDividerScreens()
    {
        var evidenceDirectory = Environment.GetEnvironmentVariable("FTS_PINNED_DIVIDER_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(evidenceDirectory))
        {
            Assert.Inconclusive("未设置 FTS_PINNED_DIVIDER_EVIDENCE_DIR，跳过发布证据产出。");
        }

        using var directory = new TestDirectory();
        var window = CreateWindow(directory, BuildEvidenceBoard());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var shell = (Border)window.FindName("WindowShell")!;

            foreach (var (style, fileName) in new[]
                     {
                         (PinnedDividerStyle.None, "pinned-divider-none.png"),
                         (PinnedDividerStyle.Solid, "pinned-divider-solid.png"),
                         (PinnedDividerStyle.Dashed, "pinned-divider-dashed.png"),
                         (PinnedDividerStyle.AccentBar, "pinned-divider-accent-bar.png")
                     })
            {
                window.ApplyPreferences(AppPreferences.Default with { PinnedDivider = style });
                CompleteLayout(window);
                SaveShellVisual(shell, System.IO.Path.Combine(evidenceDirectory, fileName));
            }

            // 设置窗截图展示默认态（实线）：四样式循环结束后归位默认值。
            window.ApplyPreferences(AppPreferences.Default with { PinnedDivider = PinnedDividerStyle.Solid });
            window.OpenSettings();
            var settings = typeof(MainWindow)
                .GetField("_settingsWindow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(window) as SettingsWindow;
            Assert.IsNotNull(settings, "设置窗口应已打开。");
            try
            {
                CompleteLayout(window);
                var settingsShell = (Border)settings.FindName("WindowShell")!;
                SaveShellVisual(settingsShell, System.IO.Path.Combine(evidenceDirectory, "settings-pinned-divider-section.png"));
            }
            finally
            {
                settings.Close();
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    /// <summary>中性演示数据：两条置顶 + 四条普通文字卡。</summary>
    private static BoardService BuildEvidenceBoard()
    {
        var board = new BoardService();
        var pinned = board.AddText("置顶：本周发布清单");
        board.SetPinnedMany([pinned.Id], true);
        var pinnedSecond = board.AddText("置顶：面板改进想法备查");
        board.SetPinnedMany([pinnedSecond.Id], true);
        var labels = new[] { "普通卡片：剪贴板里的临时地址", "普通卡片：会议时间待确认", "普通卡片：示例文本条目一", "普通卡片：示例文本条目二" };
        foreach (var label in labels)
        {
            board.AddText(label);
        }

        return board;
    }

    private static void SaveShellVisual(Border shell, string filePath)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(filePath)!);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(shell.ActualWidth),
            (int)Math.Ceiling(shell.ActualHeight),
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(shell);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(filePath);
        encoder.Save(stream);
    }
}
