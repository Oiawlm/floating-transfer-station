using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 契约 #6（2026-10-08 重写）：可见态（展开/收起/轨道）窗口矩形完整落在工作区内、
/// 外形四角圆角恒定；环境广播只触发重新贴齐，不改外形。右缘越屏裁切已删除，
/// 窗口宽度恒等于可见宽度，内容层不得再有右内缩。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public void CollapsedGeometry_StaysFullyInsideTheWorkArea()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            window.Show();
            CompleteLayout(window);
            var work = SystemParameters.WorkArea;
            var rowHeight = WindowSettings.Default.WindowHeight / BoardCategoryCatalog.Ordered.Count;

            Assert.AreEqual(rowHeight, window.Height, 0.5);
            Assert.AreEqual(WindowSettings.TabWidth, window.Width, 0.5);
            Assert.AreEqual(work.Right, window.Left + window.Width, 0.5);
            Assert.IsTrue(window.Left >= work.Left - 0.5, "窗口左缘不得推出工作区。");
            Assert.IsTrue(window.Top >= work.Top - 0.5, "窗口上缘不得推出工作区。");
            Assert.IsTrue(window.Top + window.Height <= work.Bottom + 0.5, "窗口下缘不得推出工作区。");

            var handle = window.FindName("CollapsedCategoryHandle") as ContentControl;
            Assert.IsNotNull(handle);
            Assert.AreEqual(WindowSettings.TabWidth, handle.ActualWidth, 0.5);
            var handleRightInWindow =
                handle.TranslatePoint(new Point(handle.ActualWidth, 0), window).X;
            Assert.AreEqual(window.Width, handleRightInWindow, 0.5,
                "内容层必须填满窗口右缘：不存在越屏的窗壳区域。");
            AssertNoRightInset(window);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ExpandedGeometry_StaysFullyInsideTheWorkArea()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var work = SystemParameters.WorkArea;
            var visibleWidth = WindowSettings.Default.PanelWidth + WindowSettings.TabWidth;

            Assert.AreEqual(visibleWidth, window.Width, 0.5);
            Assert.AreEqual(work.Right, window.Left + window.Width, 0.5);
            Assert.IsTrue(window.Left >= work.Left - 0.5, "窗口左缘不得推出工作区。");
            Assert.IsTrue(window.Top >= work.Top - 0.5, "窗口上缘不得推出工作区。");
            Assert.IsTrue(window.Top + window.Height <= work.Bottom + 0.5, "窗口下缘不得推出工作区。");

            var rail = (Border)window.FindName("CategoryRail");
            Assert.IsNotNull(rail);
            var railRightInWindow = rail.TranslatePoint(new Point(rail.ActualWidth, 0), window).X;
            Assert.AreEqual(window.Width, railRightInWindow, 0.5,
                "内容层必须填满窗口右缘：不存在越屏的窗壳区域。");

            var panel = window.FindName("PanelContentHost") as Grid;
            Assert.IsNotNull(panel);
            Assert.AreEqual(WindowSettings.Default.PanelWidth, panel.ActualWidth, 0.5);
            AssertNoRightInset(window);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void WidthResize_KeepsTheWindowInsideTheWorkArea()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var work = SystemParameters.WorkArea;

            InvokePrivate(
                window,
                "WidthThumb_DragDelta",
                null,
                new DragDeltaEventArgs(-40, 0));
            CompleteLayout(window);

            var normalized = new WindowSettings(
                WindowSettings.Default.PanelWidth + 40,
                WindowSettings.Default.WindowHeight,
                WindowSettings.Default.Top).Normalize(work.Width, work.Height);
            var visibleWidth = normalized.PanelWidth + WindowSettings.TabWidth;
            Assert.AreEqual(visibleWidth, window.Width, 0.5);
            Assert.AreEqual(work.Right, window.Left + window.Width, 0.5);
            Assert.IsTrue(window.Left >= work.Left - 0.5, "窗口左缘不得推出工作区。");
            AssertNoRightInset(window);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void DisplayChange_ReappliesPlacementAndKeepsTheGeometry()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var work = SystemParameters.WorkArea;
            var visibleWidth = WindowSettings.Default.PanelWidth + WindowSettings.TabWidth;
            Assert.AreEqual(visibleWidth, window.Width, 0.5);

            // 模拟环境漂移后窗口不在贴齐位：广播必须重新贴齐，外形与宽度不变。
            window.Left -= 120;
            CompleteLayout(window);
            Assert.AreEqual(work.Right - visibleWidth - 120, window.Left, 0.5);

            InvokePrivate(window, "WndProc", nint.Zero, 0x007E, nint.Zero, nint.Zero, false);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            CompleteLayout(window);

            Assert.AreEqual(visibleWidth, window.Width, 0.5, "显示器广播不得改变窗口外形。");
            Assert.AreEqual(work.Right - visibleWidth, window.Left, 0.5, "显示器广播必须重新贴齐工作区右缘。");
            AssertNoRightInset(window);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void SettingChange_WithAnySection_ReappliesPlacement()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var work = SystemParameters.WorkArea;
            var visibleWidth = WindowSettings.Default.PanelWidth + WindowSettings.TabWidth;

            window.Left -= 80;
            CompleteLayout(window);

            var lParam = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(string.Empty);
            try
            {
                InvokePrivate(window, "WndProc", nint.Zero, 0x001A, nint.Zero, lParam, false);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(lParam);
            }
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            CompleteLayout(window);

            Assert.AreEqual(visibleWidth, window.Width, 0.5, "系统设置广播不得改变窗口外形。");
            Assert.AreEqual(work.Right - visibleWidth, window.Left, 0.5,
                "WM_SETTINGCHANGE（工作区变化不伴随显示变更广播）必须重新贴齐工作区右缘。");
            AssertNoRightInset(window);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void DisplayChange_KeepsTheExternalDropRailGeometry()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            InvokePrivate(window, "RevealExternalDropRail");
            CompleteLayout(window);
            var work = SystemParameters.WorkArea;
            var normalized = WindowSettings.Default.Normalize(work.Width, work.Height);
            Assert.AreEqual(WindowSettings.TabWidth, window.Width, 0.5);
            Assert.AreEqual(normalized.WindowHeight, window.Height, 0.5,
                "前置条件：外部拖放把面板换成了轨道几何。");

            InvokePrivate(window, "WndProc", nint.Zero, 0x007E, nint.Zero, nint.Zero, false);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            CompleteLayout(window);

            Assert.AreEqual(WindowSettings.TabWidth, window.Width, 0.5,
                "广播后仍须保持轨道几何，不得误贴为收起几何。");
            Assert.AreEqual(normalized.WindowHeight, window.Height, 0.5);
            Assert.AreEqual(work.Right - WindowSettings.TabWidth, window.Left, 0.5);
            AssertNoRightInset(window);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ResetAction_KeepsTheWindowInsideTheWorkArea()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var reset = window.FindName("ResetWindowButton") as Button;
            Assert.IsNotNull(reset);

            reset.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            CompleteLayout(window);

            var work = SystemParameters.WorkArea;
            var expected = WindowSettings.Default.ResetToDefault(work.Width, work.Height);
            var visibleWidth = expected.PanelWidth + WindowSettings.TabWidth;
            Assert.AreEqual(visibleWidth, window.Width, 0.5);
            Assert.AreEqual(work.Right, window.Left + window.Width, 0.5);
            Assert.AreEqual(expected.WindowHeight, window.Height, 0.5);
            Assert.IsTrue(window.Left >= work.Left - 0.5, "窗口左缘不得推出工作区。");
            AssertNoRightInset(window);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    /// <summary>内容层不得有右内缩：该内缩曾是右缘越屏裁切的专属补偿，机制已删除。</summary>
    private static void AssertNoRightInset(MainWindow window)
    {
        var layoutRoot = window.FindName("LayoutRoot") as FrameworkElement;
        Assert.IsNotNull(layoutRoot);
        Assert.AreEqual(
            0d,
            layoutRoot.Margin.Right,
            0.01,
            "内容层不得按任何裁切量右内缩：可见态窗口完整落在工作区内。");
    }
}
