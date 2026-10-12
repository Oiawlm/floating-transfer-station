using System.Windows;
using System.Windows.Controls;
using FloatingTransferStation.Design;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 1.30.0 发布证据（FTS_NOTE_EVIDENCE_DIR 设置时产出真实 WPF 渲染截图，Issue #103）：
/// 图片/文本/待分类三个分类标签页的卡片悬停态——右上角固定两列操作网格
/// （选择|删除|置顶|备注）随悬停淡入的实拍。悬停走真实光标路径（契约 #13），
/// 渲染走 SaveVisualEvidence（RenderTargetBitmap 实拍窗口壳层）。复用
/// FTS_NOTE_EVIDENCE_DIR 单一来源，不新增 CI 环境变量。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public void Evidence_CaptureBtnGridHoverLayouts()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("FTS_NOTE_EVIDENCE_DIR")))
        {
            Assert.Inconclusive("未设置 FTS_NOTE_EVIDENCE_DIR，跳过发布证据产出。");
        }

        using var directory = new TestDirectory();
        var board = new BoardService();
        var imagePath = Path.Combine(directory.Root, "btngrid-evidence.png");
        WritePng(imagePath, 320, 180);
        board.AddImage(Guid.NewGuid(), "images/btngrid-evidence.png", imagePath, BoardCategory.CustomerOriginal);
        board.AddText(new string('网', 160), BoardCategory.Prompt);
        board.AddText(new string('格', 160), BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            window.Topmost = true;
            CompleteLayout(window);
            foreach (var (category, fileName) in new[]
            {
                (BoardCategory.CustomerOriginal, "btngrid-tab-image-hover.png"),
                (BoardCategory.Prompt, "btngrid-tab-text-hover.png"),
                (BoardCategory.Inbox, "btngrid-tab-inbox-hover.png"),
            })
            {
                ExpandCategory(window, category);
                CompleteLayout(window);
                var list = (ListBox)window.FindName("BoardList");
                var container = FindDescendants<ListBoxItem>(list).First();
                HoverRealAt(window, container, new Point(24, container.ActualHeight * 0.8));
                PumpDispatcherFor(
                    window.Dispatcher,
                    TimeSpan.FromMilliseconds(DesignTokens.FadeVisibilityMs + 120));
                SaveVisualEvidence(
                    (Border)window.FindName("WindowShell"),
                    fileName,
                    "FTS_NOTE_EVIDENCE_DIR");
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }
}
