using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;

namespace FloatingTransferStation.Tests;

public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public void PendingCollapse_DoesNotHideAReopenedCategory()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("快速移入分类后，内容面板保持展开。");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            var expandedWidth = window.Width;
            var expandedHeight = window.Height;
            var shell = (Border)window.FindName("WindowShell");

            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
            Assert.AreEqual(0d, shell.Opacity);

            window.Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(() => ExpandCategory(window, BoardCategory.Inbox)));
            CompleteLayout(window);

            var viewModel = (MainWindowViewModel)window.DataContext;
            Assert.IsTrue(viewModel.IsPanelExpanded);
            Assert.AreEqual(BoardCategory.Inbox, viewModel.ActivePanel?.Category);
            Assert.AreEqual(expandedWidth, window.Width, 0.5);
            Assert.AreEqual(expandedHeight, window.Height, 0.5);
            Assert.AreEqual(1d, shell.Opacity);
            Assert.IsTrue(shell.IsHitTestVisible);
            InvokePrivate(window, "StopPanelContentAnimation");
            InvokePrivate(window, "StopCategoryRevealAnimations");
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(180));
            SaveVisualEvidence(
                shell,
                "reopened-category.png",
                "FTS_VISUAL_TRANSITIONS_EVIDENCE_DIR");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void PendingCollapse_DoesNotTruncateAnExternalDropRail()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            var expandedHeight = window.Height;
            var expandedTop = window.Top;
            var shell = (Border)window.FindName("WindowShell");

            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
            Assert.AreEqual(0d, shell.Opacity);

            window.Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(() => InvokePrivate(window, "RevealExternalDropRail")));
            CompleteLayout(window);

            var viewModel = (MainWindowViewModel)window.DataContext;
            Assert.IsTrue(viewModel.IsExternalDropRailVisible);
            Assert.IsFalse(viewModel.IsPanelExpanded);
            Assert.AreEqual(WindowSettings.TabWidth, window.Width, 0.5);
            Assert.AreEqual(expandedHeight, window.Height, 0.5);
            Assert.AreEqual(expandedTop, window.Top, 0.5);
            Assert.AreEqual(1d, shell.Opacity);
            Assert.IsTrue(shell.IsHitTestVisible);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(180));
            SaveVisualEvidence(
                shell,
                "external-drop-rail.png",
                "FTS_VISUAL_TRANSITIONS_EVIDENCE_DIR");
        }
        finally
        {
            CloseWindow(window);
        }
    }
}
