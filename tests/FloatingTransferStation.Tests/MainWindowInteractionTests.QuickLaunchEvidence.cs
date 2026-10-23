using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatingTransferStation.Design;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 快捷启动发布证据（FTS_QUICKLAUNCH_EVIDENCE_DIR 设置时产出真实 WPF 渲染截图，
/// GOAL-LAUNCHER-UX-PERF-20261016，Issue #176）：编辑模式瓦片 × 常驻（浅/深）与
/// 查看态右键菜单（浅/深）。渲染走 RenderTargetBitmap 实拍窗口壳层（菜单为独立
/// Popup 视觉树，单独实拍其可视树）。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public void Evidence_CaptureQuickLaunchEditModeTiles()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("FTS_QUICKLAUNCH_EVIDENCE_DIR")))
        {
            Assert.Inconclusive("未设置 FTS_QUICKLAUNCH_EVIDENCE_DIR，跳过发布证据产出。");
        }

        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
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
        var window = CreateLauncherWindowSync(directory, board, store, starter, items);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

            foreach (var (theme, suffix) in new[]
            {
                (DesignTheme.Light, "light"),
                (DesignTheme.Dark, "dark"),
            })
            {
                DesignThemeManager.Apply(window, theme);
                ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
                CompleteLayout(window);

                // 查看态。
                SaveVisualEvidence(
                    (Border)window.FindName("WindowShell"),
                    $"quicklaunch-view-{suffix}.png",
                    "FTS_QUICKLAUNCH_EVIDENCE_DIR");

                // 查看态右键菜单（独立 Popup 视觉树，单独实拍）。
                // 2026-10-18 契约修订（Issue #186）：编辑模式移除，编辑态证据段退场。
                CompleteLayout(window);
                var tile = FindLauncherTile(window, window.LaunchersForTests().Items[1]);
                Assert.IsNotNull(tile);
                var menu = tile.ContextMenu!;
                menu.PlacementTarget = tile;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                menu.IsOpen = true;
                CompleteLayout(window);
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
                SaveContextMenuEvidence(
                    menu,
                    $"quicklaunch-contextmenu-{suffix}.png");
                menu.IsOpen = false;
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
            }
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    /// <summary>实拍上下文菜单视觉树（打开态的独立 Popup 子树）。</summary>
    private static void SaveContextMenuEvidence(ContextMenu menu, string fileName)
    {
        var directory = Environment.GetEnvironmentVariable("FTS_QUICKLAUNCH_EVIDENCE_DIR")!;
        Directory.CreateDirectory(directory);
        menu.UpdateLayout();
        var width = Math.Max(menu.ActualWidth, 120d);
        var height = Math.Max(menu.ActualHeight, 120d);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width),
            (int)Math.Ceiling(height),
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(menu);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, fileName));
        encoder.Save(stream);
    }

    /// <summary>
    /// 启动器列数策略发布证据（契约 #30 修订，2026-10-24）：固定 4 列瓦片网格
    /// ×浅/深主题实拍——窗口以固定 4 列偏好构造，瓦片宽=摊分公式，主题切换即拍。
    /// </summary>
    [STATestMethod]
    public void Evidence_CaptureLauncherFixedColumnsBothThemes()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("FTS_QUICKLAUNCH_EVIDENCE_DIR")))
        {
            Assert.Inconclusive("未设置 FTS_QUICKLAUNCH_EVIDENCE_DIR，跳过发布证据产出。");
        }

        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var items = Enumerable.Range(0, 9)
            .Select(index => new LauncherItem(
                Guid.NewGuid(),
                LauncherKind.Application,
                $"{app.Path}.{index}",
                $"应用{index}",
                DateTimeOffset.UtcNow))
            .ToArray();
        store.SaveAsync(new LauncherStoreData(LauncherStoreData.CurrentVersion, [.. items]))
            .GetAwaiter().GetResult();
        var window = CreateLauncherWindow(
            directory,
            board,
            store,
            starter,
            AppPreferences.Default with { LauncherColumnStrategy = LauncherColumnStrategy.Fixed4 });

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

            foreach (var (theme, suffix) in new[]
            {
                (DesignTheme.Light, "light"),
                (DesignTheme.Dark, "dark"),
            })
            {
                DesignThemeManager.Apply(window, theme);
                ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
                CompleteLayout(window);

                var panel = FindDescendant<LauncherTilePanel>(window.LauncherTileGrid);
                Assert.IsNotNull(panel);
                Assert.AreEqual(4, panel.EffectiveColumnCount, 0, $"实拍前置：{suffix} 主题下固定 4 列生效。");
                SaveVisualEvidence(
                    (Border)window.FindName("WindowShell"),
                    $"launcher-fixed4-{suffix}.png",
                    "FTS_QUICKLAUNCH_EVIDENCE_DIR");
            }
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }
}
