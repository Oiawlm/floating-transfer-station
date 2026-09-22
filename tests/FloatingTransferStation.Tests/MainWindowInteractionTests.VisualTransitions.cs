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
        // 减弱动效路径下收起同步提交，覆盖「交接挂起期间重开」的原始竞态窗口。
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

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
        // 减弱动效路径下收起同步提交，覆盖「交接挂起期间外部拖入」的原始竞态窗口。
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

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

    [STATestMethod]
    public void CollapseExit_ReentryDuringExitCancelsPendingCollapseAndKeepsPanelExpanded()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("收起途中回到面板会取消收起。");
        var window = CreateWindow(directory, board);
        window.Resources[SystemParameters.ClientAreaAnimationKey] = true;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var shell = (Border)window.FindName("WindowShell");
            var content = (Grid)window.FindName("PanelContentHost");
            var viewModel = (MainWindowViewModel)window.DataContext;
            var expandedWidth = window.Width;

            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
            Assert.AreEqual(
                0d,
                content.GetAnimationBaseValue(UIElement.OpacityProperty));
            Assert.AreEqual(1d, shell.Opacity);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
            Assert.IsTrue(
                content.Opacity > 0d && content.Opacity < 1d,
                "出场动画应正在进行中。");

            InvokePrivate(window, "Root_MouseEnter", window, NewMouseEventArgs());
            CompleteLayout(window);

            Assert.AreEqual(1d, content.Opacity);
            Assert.AreEqual(
                1d,
                content.GetAnimationBaseValue(UIElement.OpacityProperty));
            Assert.IsTrue(viewModel.IsPanelExpanded);
            Assert.AreEqual(expandedWidth, window.Width, 0.5);
            Assert.AreEqual(1d, shell.Opacity);

            // 取消后再次离开并收起，仍然完整走完出场与交接。
            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(240));
            CompleteLayout(window);

            Assert.IsFalse(viewModel.IsPanelExpanded);
            Assert.AreEqual(WindowSettings.TabWidth, window.Width, 0.5);
            Assert.AreEqual(1d, shell.Opacity);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CollapseExit_IsImmediateWhenSystemAnimationsAreDisabled()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var shell = (Border)window.FindName("WindowShell");
            var content = (Grid)window.FindName("PanelContentHost");
            var viewModel = (MainWindowViewModel)window.DataContext;
            var expandedWidth = window.Width;
            var expandedHeight = window.Height;

            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);

            Assert.AreEqual(0d, shell.Opacity);
            Assert.IsFalse(shell.IsHitTestVisible);
            Assert.IsTrue(viewModel.IsPanelExpanded);
            Assert.AreEqual(expandedWidth, window.Width, 0.5);
            Assert.AreEqual(expandedHeight, window.Height, 0.5);

            CompleteLayout(window);

            Assert.IsFalse(viewModel.IsPanelExpanded);
            Assert.AreEqual(WindowSettings.TabWidth, window.Width, 0.5);
            Assert.AreEqual(1d, shell.Opacity);
        }
        finally
        {
            CloseWindow(window);
        }
    }
}
