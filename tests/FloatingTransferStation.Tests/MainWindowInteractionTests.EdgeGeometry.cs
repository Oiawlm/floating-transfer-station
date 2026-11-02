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

            DpiGeometryAssert.AreEqual(rowHeight, window.Height, window, PixelQuantization.Single, "收起高＝行高。");
            DpiGeometryAssert.AreEqual(WindowSettings.TabWidth, window.Width, window, PixelQuantization.Single, "收起宽＝标签宽。");
            DpiGeometryAssert.AreEqual(work.Right, window.Left + window.Width, window, PixelQuantization.Pair,
                "收起矩形右缘贴齐工作区右缘（Left＋Width 两次量化）。");
            DpiGeometryAssert.IsAtLeast(window.Left, work.Left, window, PixelQuantization.Single, "窗口左缘不得推出工作区。");
            DpiGeometryAssert.IsAtLeast(window.Top, work.Top, window, PixelQuantization.Single, "窗口上缘不得推出工作区。");
            DpiGeometryAssert.IsAtMost(window.Top + window.Height, work.Bottom, window, PixelQuantization.Pair, "窗口下缘不得推出工作区。");

            var handle = window.FindName("CollapsedCategoryHandle") as ContentControl;
            Assert.IsNotNull(handle);
            DpiGeometryAssert.AreEqual(WindowSettings.TabWidth, handle.ActualWidth, window, PixelQuantization.Single, "把手宽＝标签宽。");
            var handleRightInWindow =
                handle.TranslatePoint(new Point(handle.ActualWidth, 0), window).X;
            DpiGeometryAssert.AreEqual(window.Width, handleRightInWindow, window, PixelQuantization.Single,
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

            DpiGeometryAssert.AreEqual(visibleWidth, window.Width, window, PixelQuantization.Single, "展开宽＝面板宽＋标签宽。");
            DpiGeometryAssert.AreEqual(work.Right, window.Left + window.Width, window, PixelQuantization.Pair,
                "展开矩形右缘贴齐工作区右缘（Left＋Width 两次量化）。");
            DpiGeometryAssert.IsAtLeast(window.Left, work.Left, window, PixelQuantization.Single, "窗口左缘不得推出工作区。");
            DpiGeometryAssert.IsAtLeast(window.Top, work.Top, window, PixelQuantization.Single, "窗口上缘不得推出工作区。");
            DpiGeometryAssert.IsAtMost(window.Top + window.Height, work.Bottom, window, PixelQuantization.Pair, "窗口下缘不得推出工作区。");

            var rail = (Border)window.FindName("CategoryRail");
            Assert.IsNotNull(rail);
            var railRightInWindow = rail.TranslatePoint(new Point(rail.ActualWidth, 0), window).X;
            DpiGeometryAssert.AreEqual(window.Width, railRightInWindow, window, PixelQuantization.Single,
                "内容层必须填满窗口右缘：不存在越屏的窗壳区域。");

            var panel = window.FindName("PanelContentHost") as Grid;
            Assert.IsNotNull(panel);
            DpiGeometryAssert.AreEqual(WindowSettings.Default.PanelWidth, panel.ActualWidth, window, PixelQuantization.Single, "面板宽＝面板冻结宽。");
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
            DpiGeometryAssert.AreEqual(visibleWidth, window.Width, window, PixelQuantization.Single, "拖宽后可见宽＝归一化面板宽＋标签宽。");
            DpiGeometryAssert.AreEqual(work.Right, window.Left + window.Width, window, PixelQuantization.Pair,
                "拖宽后右缘仍贴齐工作区右缘（Left＋Width 两次量化）。");
            DpiGeometryAssert.IsAtLeast(window.Left, work.Left, window, PixelQuantization.Single, "窗口左缘不得推出工作区。");
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
        var savedCursor = SaveCursorPosition();

        try
        {
            window.Show();
            // 真实光标是跨测试的全局状态：贴边窗口左移会令停在窗口矩形内的遗留
            // 光标触发真实 MouseLeave → 收起定时器在随后的泵窗内提交，广播处理
            // 便按「已收起」贴齐（CI 偶发 418→58）。先移离窗口再断言几何基线。
            Assert.IsTrue(
                SetCursorPos(10, 10),
                "几何断言前必须把真实光标移离窗口。");
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var work = SystemParameters.WorkArea;
            var visibleWidth = WindowSettings.Default.PanelWidth + WindowSettings.TabWidth;
            DpiGeometryAssert.AreEqual(visibleWidth, window.Width, window, PixelQuantization.Single, "广播前基线：展开宽。");

            // 模拟环境漂移后窗口不在贴齐位：广播必须重新贴齐，外形与宽度不变。
            window.Left -= 120;
            CompleteLayout(window);
            DpiGeometryAssert.AreEqual(work.Right - visibleWidth - 120, window.Left, window, PixelQuantization.Single, "人为左移 120 后的位置基线。");

            InvokePrivate(window, "WndProc", nint.Zero, 0x007E, nint.Zero, nint.Zero, false);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            CompleteLayout(window);

            DpiGeometryAssert.AreEqual(visibleWidth, window.Width, window, PixelQuantization.Single, "显示器广播不得改变窗口外形。");
            DpiGeometryAssert.AreEqual(work.Right - visibleWidth, window.Left, window, PixelQuantization.Single, "显示器广播必须重新贴齐工作区右缘。");
            AssertNoRightInset(window);
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void SettingChange_WithAnySection_ReappliesPlacement()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
        var savedCursor = SaveCursorPosition();

        try
        {
            window.Show();
            // 与 DisplayChange 用例同因：遗留的真实光标停在窗口矩形内时，窗口
            // 左移触发的真实 MouseLeave 会让广播按「已收起」贴齐。先移离窗口。
            Assert.IsTrue(
                SetCursorPos(10, 10),
                "几何断言前必须把真实光标移离窗口。");
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

            DpiGeometryAssert.AreEqual(visibleWidth, window.Width, window, PixelQuantization.Single, "系统设置广播不得改变窗口外形。");
            DpiGeometryAssert.AreEqual(work.Right - visibleWidth, window.Left, window, PixelQuantization.Single,
                "WM_SETTINGCHANGE（工作区变化不伴随显示变更广播）必须重新贴齐工作区右缘。");
            AssertNoRightInset(window);
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
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
            DpiGeometryAssert.AreEqual(WindowSettings.TabWidth, window.Width, window, PixelQuantization.Single, "轨道宽＝标签宽。");
            DpiGeometryAssert.AreEqual(normalized.WindowHeight, window.Height, window, PixelQuantization.Single,
                "前置条件：外部拖放把面板换成了轨道几何。");

            InvokePrivate(window, "WndProc", nint.Zero, 0x007E, nint.Zero, nint.Zero, false);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            CompleteLayout(window);

            DpiGeometryAssert.AreEqual(WindowSettings.TabWidth, window.Width, window, PixelQuantization.Single,
                "广播后仍须保持轨道几何，不得误贴为收起几何。");
            DpiGeometryAssert.AreEqual(normalized.WindowHeight, window.Height, window, PixelQuantization.Single, "广播后轨道高不变。");
            DpiGeometryAssert.AreEqual(work.Right - WindowSettings.TabWidth, window.Left, window, PixelQuantization.Single, "轨道态右缘贴齐工作区右缘。");
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
            DpiGeometryAssert.AreEqual(visibleWidth, window.Width, window, PixelQuantization.Single, "重置后可见宽。");
            DpiGeometryAssert.AreEqual(work.Right, window.Left + window.Width, window, PixelQuantization.Pair, "重置后右缘贴齐工作区右缘。");
            DpiGeometryAssert.AreEqual(expected.WindowHeight, window.Height, window, PixelQuantization.Single, "重置后窗口高。");
            DpiGeometryAssert.IsAtLeast(window.Left, work.Left, window, PixelQuantization.Single, "窗口左缘不得推出工作区。");
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
