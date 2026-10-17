using System.Windows;
using System.Windows.Controls;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 快捷启动发布证据（FTS_QUICKLAUNCH_EVIDENCE_DIR 设置时产出真实 WPF 渲染截图，
/// GOAL-LAUNCHER-UX-PERF-20261016）：编辑模式瓦片 × 常驻（浅/深）与查看态右键
/// 菜单。渲染走 SaveVisualEvidence（RenderTargetBitmap 实拍窗口壳层）。
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
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);

            // 查看态（浅色）。
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "quicklaunch-view-light.png",
                "FTS_QUICKLAUNCH_EVIDENCE_DIR");

            // 编辑模式（浅色）：右上 × 删除钮常驻是本批次修复对象。
            EnterLauncherEdit(window);
            CompleteLayout(window);
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "quicklaunch-edit-light.png",
                "FTS_QUICKLAUNCH_EVIDENCE_DIR");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }
}
