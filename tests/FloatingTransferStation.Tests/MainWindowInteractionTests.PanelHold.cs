using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

public sealed partial class MainWindowInteractionTests
{
    private static void TogglePanelHold(MainWindow window) =>
        InvokePrivate(
            window,
            "PanelHoldButton_Click",
            window.FindName("PanelHoldButton"),
            new RoutedEventArgs(Button.ClickEvent, window.FindName("PanelHoldButton")));

    [STATestMethod]
    public void PanelHold_KeepsPanelExpandedAcrossPointerLeaveUntilReleased()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        // 减弱动效路径下收起同步提交：固定期间 tick 必须完全不触发收起几何。
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var expandedWidth = window.Width;
            var expandedHeight = window.Height;
            var viewModel = (MainWindowViewModel)window.DataContext;

            TogglePanelHold(window);
            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
            CompleteLayout(window);

            Assert.IsTrue(viewModel.IsPanelExpanded, "固定期间指针离开不得收起面板。");
            Assert.AreEqual(expandedWidth, window.Width, 0.5);
            Assert.AreEqual(expandedHeight, window.Height, 0.5);
            Assert.AreEqual(BoardCategory.Inbox, viewModel.ActivePanel?.Category);

            // 解除时指针在面板外（测试窗口无真实指针）：按既有节奏启动收起定时器。
            TogglePanelHold(window);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(400));
            CompleteLayout(window);

            Assert.IsFalse(viewModel.IsPanelExpanded, "解除固定且指针在外，应按既有节奏收起。");
            Assert.AreEqual(WindowSettings.TabWidth, window.Width, 0.5);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void PanelHold_ReleaseWithPointerInsideKeepsPanelExpanded()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            var expandedWidth = window.Width;
            var viewModel = (MainWindowViewModel)window.DataContext;

            TogglePanelHold(window);
            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            TogglePanelHold(window);
            // 解除后指针回到面板内：表面保持，收起定时器即使行进也不得提交收起。
            InvokePrivate(window, "Root_MouseEnter", window, NewMouseEventArgs());
            InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(400));
            CompleteLayout(window);

            Assert.IsTrue(viewModel.IsPanelExpanded, "解除固定时指针在面板内，面板必须保持展开。");
            Assert.AreEqual(expandedWidth, window.Width, 0.5);
            Assert.AreEqual(BoardCategory.Reference, viewModel.ActivePanel?.Category);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void PanelHold_ButtonAccessibilityStateFollowsToggle()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var button = (Button)window.FindName("PanelHoldButton");
            Assert.IsNotNull(button);
            var accent = window.FindResource("AccentBrush");
            var secondary = window.FindResource("SecondaryTextBrush");

            Assert.AreEqual("保持展开，暂停自动收起", button.ToolTip);
            Assert.AreEqual(
                "保持展开，暂停自动收起",
                System.Windows.Automation.AutomationProperties.GetName(button));
            Assert.AreEqual(secondary, button.Foreground, "未固定态前景应与相邻头部按钮一致。");

            TogglePanelHold(window);
            Assert.AreEqual("已保持展开，点击恢复自动收起", button.ToolTip);
            Assert.AreEqual(
                "已保持展开，点击恢复自动收起",
                System.Windows.Automation.AutomationProperties.GetName(button));
            Assert.AreEqual(accent, button.Foreground, "固定态前景应切换为强调色。");

            TogglePanelHold(window);
            Assert.AreEqual("保持展开，暂停自动收起", button.ToolTip);
            Assert.AreEqual(secondary, button.Foreground);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void PanelHold_ExternalDropRailRestoresExpandedPanel()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            var expandedWidth = window.Width;
            var viewModel = (MainWindowViewModel)window.DataContext;

            TogglePanelHold(window);
            InvokePrivate(window, "RevealExternalDropRail");
            CompleteLayout(window);
            Assert.IsTrue(viewModel.IsExternalDropRailVisible);
            Assert.IsFalse(viewModel.IsPanelExpanded);
            Assert.AreEqual(WindowSettings.TabWidth, window.Width, 0.5);

            InvokePrivate(window, "HideExternalDropRail");
            CompleteLayout(window);

            Assert.IsFalse(viewModel.IsExternalDropRailVisible);
            Assert.IsTrue(viewModel.IsPanelExpanded, "拖放结束后固定若仍在，面板必须回展。");
            Assert.AreEqual(expandedWidth, window.Width, 0.5);
            Assert.AreEqual(BoardCategory.Reference, viewModel.ActivePanel?.Category);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void PanelHold_ExternalDropRailFallsBackToCollapsedWhenNotHeld()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            InvokePrivate(window, "RevealExternalDropRail");
            CompleteLayout(window);
            Assert.IsTrue(viewModel.IsExternalDropRailVisible);

            InvokePrivate(window, "HideExternalDropRail");
            CompleteLayout(window);

            Assert.IsFalse(viewModel.IsExternalDropRailVisible);
            Assert.IsFalse(viewModel.IsPanelExpanded, "未固定时拖放结束后仍回落收起态。");
            Assert.AreEqual(WindowSettings.TabWidth, window.Width, 0.5);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void PanelHold_GlobalHotkeyKeepsHeldPanelUnchanged()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            TogglePanelHold(window);
            InvokePrivate(window, "OnGlobalHotkeyPressed");
            CompleteLayout(window);

            Assert.IsTrue(viewModel.IsPanelExpanded, "固定态下全局热键不得改变面板状态。");
            Assert.AreEqual(BoardCategory.Inbox, viewModel.ActivePanel?.Category);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    /// <summary>视觉取证：真实 WPF 渲染管线输出保持开启/关闭两态的面板整窗截图（按需经证据目录环境变量开启）。</summary>
    [STATestMethod]
    public void PanelHold_HeldSurfaceRendersForEvidence()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("保持展开：鼠标移开后面板不再自动收起。");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var shell = (Border)window.FindName("WindowShell");
            var button = (Button)window.FindName("PanelHoldButton")!;
            Assert.AreEqual(Visibility.Visible, button.Visibility);

            // 头部操作区默认悬停才显示；取证前显式显示并等淡入动画完成。
            InvokePrivate(window, "SetHeaderActionsVisible", true);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            CompleteLayout(window);
            SaveVisualEvidence(shell, "panel-hold-inactive.png", "FTS_PANEL_HOLD_EVIDENCE_DIR");
            TogglePanelHold(window);
            CompleteLayout(window);
            SaveVisualEvidence(shell, "panel-hold-active.png", "FTS_PANEL_HOLD_EVIDENCE_DIR");
            InvokePrivate(window, "StopPanelContentAnimation");
            InvokePrivate(window, "StopCategoryRevealAnimations");
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(180));
        }
        finally
        {
            CloseWindow(window);
        }
    }
}
