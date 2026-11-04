using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatingTransferStation.Design;
using FloatingTransferStation.Launcher;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// W6 启动界面视觉修复包发布证据（批次 2026-11-03-parallel-ui-evolution，设置
/// FTS_W6_EVIDENCE_DIR 时产出真实 WPF 渲染截图）：①顶排圆角裁切（需求 8）——顶排/
/// 非顶排瓦片在常态/悬停/按压/确认脉冲四态 × 浅/深主题下实拍滚动视口（渲染包含
/// ScrollContentPresenter 裁剪，所见即用户所见；脉冲态取 1.06 峰值＋悬停上浮的
/// 最坏组合，经直接设置 RenderTransform 确定性定格，不受动画时序影响）；②「保持
/// 展开」按钮图标两态（需求 10——竖向矩形微缩图）。修复前/后对照＝同一测试在
/// 修复前后各跑一次（FTS_W6_EVIDENCE_TAG 区分文件名后缀）。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    private static string? W6EvidenceDirectory()
    {
        var directory = Environment.GetEnvironmentVariable("FTS_W6_EVIDENCE_DIR");
        return string.IsNullOrWhiteSpace(directory) ? null : directory;
    }

    private static string W6EvidenceTag() =>
        Environment.GetEnvironmentVariable("FTS_W6_EVIDENCE_TAG") is { Length: > 0 } tag ? tag : "capture";

    /// <summary>确定性定格瓦片变换状态：悬停＝抬升 −2；按压＝0.96；脉冲＝峰值 1.06
    /// （点击确认瞬间鼠标仍悬停，取抬升＋放大的最坏组合）。模板内 Freezable 可能被
    /// WPF 冻结——与产品 LauncherTileTransforms 同法：整组冻结即换新（Translate+Scale）。</summary>
    private static void ApplyW6TileState(Border tile, string state)
    {
        if (tile.RenderTransform is not { } existing ||
            existing.IsFrozen ||
            existing is not TransformGroup originalGroup ||
            originalGroup.Children.IsFrozen)
        {
            var group = new TransformGroup();
            group.Children.Add(new TranslateTransform());
            group.Children.Add(new ScaleTransform());
            tile.RenderTransform = group;
        }

        var current = (TransformGroup)tile.RenderTransform;
        var translate = current.Children.OfType<TranslateTransform>().First();
        var scale = current.Children.OfType<ScaleTransform>().First();
        translate.Y = 0;
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        switch (state)
        {
            case "hover":
                translate.Y = DesignTokens.TileHoverLiftPx;
                break;
            case "press":
                scale.ScaleX = DesignTokens.TilePressScale;
                scale.ScaleY = DesignTokens.TilePressScale;
                break;
            case "pulse":
                translate.Y = DesignTokens.TileHoverLiftPx;
                scale.ScaleX = 1.06;
                scale.ScaleY = 1.06;
                break;
        }
    }

    [STATestMethod]
    public void W6Evidence_CaptureLauncherTopRowStatesBothThemes()
    {
        if (W6EvidenceDirectory() is not { } directory)
        {
            Assert.Inconclusive("未设置 FTS_W6_EVIDENCE_DIR，跳过发布证据产出。");
            return;
        }

        using var testDirectory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(testDirectory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var items = Enumerable.Range(0, 7)
            .Select(index => new LauncherItem(
                Guid.NewGuid(),
                LauncherKind.Application,
                $"{app.Path}.{index}",
                $"应用{index}",
                DateTimeOffset.UtcNow))
            .ToArray();
        var window = CreateLauncherWindowSync(testDirectory, board, store, starter, items);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            var tag = W6EvidenceTag();

            foreach (var (theme, themeSuffix) in new[]
            {
                (DesignTheme.Light, "light"),
                (DesignTheme.Dark, "dark"),
            })
            {
                DesignThemeManager.Apply(window, theme);
                ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
                CompleteLayout(window);
                var catalog = window.LaunchersForTests();
                var columns = Math.Max(1, FindDescendant<LauncherTilePanel>(window.LauncherTileGrid)?.EffectiveColumnCount ?? 1);
                var topTile = FindLauncherTile(window, catalog.Items[0]);
                var belowTile = FindLauncherTile(window, catalog.Items[Math.Min(columns, catalog.Items.Count - 1)]);
                Assert.IsNotNull(topTile, "前置：顶排瓦片已生成。");
                Assert.IsNotNull(belowTile, "前置：次排瓦片已生成。");
                window.LauncherScroll.ScrollToTop();
                CompleteLayout(window);

                foreach (var state in new[] { "normal", "hover", "press", "pulse" })
                {
                    ApplyW6TileState(topTile, state);
                    ApplyW6TileState(belowTile, "normal");
                    CompleteLayout(window);
                    SaveVisualEvidence(
                        window.LauncherScroll,
                        $"w6-toprow-{state}-{themeSuffix}-{tag}.png",
                        "FTS_W6_EVIDENCE_DIR");
                }

                // 次排在同状态下的对照（沟道缓冲内的非顶排基准）。
                ApplyW6TileState(topTile, "normal");
                ApplyW6TileState(belowTile, "pulse");
                CompleteLayout(window);
                SaveVisualEvidence(
                    window.LauncherScroll,
                    $"w6-belowrow-pulse-{themeSuffix}-{tag}.png",
                    "FTS_W6_EVIDENCE_DIR");

                ApplyW6TileState(topTile, "normal");
                ApplyW6TileState(belowTile, "normal");
            }
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void W6Evidence_CapturePanelHoldIconTwoStatesBothThemes()
    {
        if (W6EvidenceDirectory() is not { } directory)
        {
            Assert.Inconclusive("未设置 FTS_W6_EVIDENCE_DIR，跳过发布证据产出。");
            return;
        }

        using var testDirectory = new TestDirectory();
        var window = CreateWindow(testDirectory, new BoardService());

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            // 展开面板后再拍：收起态下头部层透明（FadeAnimation 收起），按钮虽已布局
            // 但渲染全透明；头部按钮组另为悬停显现（SetHeaderActionsVisible＋
            // FadeAnimation，指针不在 HeaderActionRegion 内时 Opacity=0）——模拟
            // MouseEnter 揭示后再拍。
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            var tag = W6EvidenceTag();
            var button = (Button)window.FindName("PanelHoldButton");
            Assert.IsNotNull(button, "前置：保持展开按钮存在。");
            var actionRegion = (Border)window.FindName("HeaderActionRegion");
            Assert.IsNotNull(actionRegion, "前置：头部动作区存在。");
            InvokePrivate(window, "HeaderActionRegion_MouseEnter", actionRegion, NewMouseEventArgs());
            // 揭示走 FadeAnimation 167ms 动画（头部宿主样式未挂 AnimationsEnabled，
            // 不随 ClientAreaAnimationKey=false 即时定格）——泵过动画窗再拍。
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(250));
            CompleteLayout(window);
            Assert.AreEqual(
                1d,
                ((StackPanel)window.FindName("HeaderActions")).Opacity,
                0.01,
                "前置：头部按钮组已揭示（Opacity=1）。");

            foreach (var (theme, themeSuffix) in new[]
            {
                (DesignTheme.Light, "light"),
                (DesignTheme.Dark, "dark"),
            })
            {
                DesignThemeManager.Apply(window, theme);
                CompleteLayout(window);
                // 按钮级 RTB 实测产出空位图（头部宿主动画态下的 WPF 渲染怪癖），
                // 图标证据以壳层实拍承载（离线裁切放大核对几何）。
                SaveVisualEvidence(
                    (Border)window.FindName("WindowShell"),
                    $"w6-panelhold-shell-idle-{themeSuffix}-{tag}.png",
                    "FTS_W6_EVIDENCE_DIR");

                // 走真实 Click 路由事件（直接反射调处理器时裸 RoutedEventArgs 不能
                // 置 Handled）。
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
                CompleteLayout(window);
                SaveVisualEvidence(
                    (Border)window.FindName("WindowShell"),
                    $"w6-panelhold-shell-held-{themeSuffix}-{tag}.png",
                    "FTS_W6_EVIDENCE_DIR");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }
}
