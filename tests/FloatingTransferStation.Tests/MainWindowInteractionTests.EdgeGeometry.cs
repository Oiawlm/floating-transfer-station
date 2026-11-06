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
    public void WidthResize_RapidDeltasInOneFrame_KeepTheRightEdgeDocked()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
        var savedCursor = SaveCursorPosition();

        try
        {
            window.Show();
            // 真实光标是跨测试的全局状态：拖宽会让左缘扫过屏幕中部，遗留光标可能
            // 落入窗口矩形并触发真实 MouseEnter/Leave 状态机，先移离窗口再断言。
            Assert.IsTrue(
                SetCursorPos(10, 10),
                "几何断言前必须把真实光标移离窗口。");
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var work = SystemParameters.WorkArea;

            // 快拖＝同一布局帧内连发多个 DragDelta、帧末才布局一次（高回报率鼠标
            // 一帧可投递多个增量）。贴边若读陈旧宽度，右缘空隙＝末帧宽度变化量。
            InvokePrivate(
                window,
                "WidthThumb_DragDelta",
                null,
                new DragDeltaEventArgs(-40, 0));
            InvokePrivate(
                window,
                "WidthThumb_DragDelta",
                null,
                new DragDeltaEventArgs(80, 0));
            CompleteLayout(window);

            var normalized = new WindowSettings(
                WindowSettings.Default.PanelWidth + 40 - 80,
                WindowSettings.Default.WindowHeight,
                WindowSettings.Default.Top).Normalize(work.Width, work.Height);
            var visibleWidth = normalized.PanelWidth + WindowSettings.TabWidth;
            DpiGeometryAssert.AreEqual(visibleWidth, window.Width, window, PixelQuantization.Single,
                "快拖净拖窄后可见宽＝归一化面板宽＋标签宽。");
            DpiGeometryAssert.AreEqual(work.Right, window.Left + window.Width, window, PixelQuantization.Pair,
                "同帧多 delta 后右缘仍贴齐工作区右缘（快拖无空隙）。");
            DpiGeometryAssert.IsAtLeast(window.Left, work.Left, window, PixelQuantization.Single, "窗口左缘不得推出工作区。");
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void WidthResize_ClampBoundaries_KeepTheRightEdgeDocked()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
        var savedCursor = SaveCursorPosition();

        try
        {
            window.Show();
            Assert.IsTrue(
                SetCursorPos(10, 10),
                "几何断言前必须把真实光标移离窗口。");
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var work = SystemParameters.WorkArea;

            // 一次连续快拖同时穿过上下钳制界：冲过上宽 → 回落、冲过下宽 → 回升。
            // 到界后目标宽度不再变化，贴边仍须按目标宽度（而非陈旧宽度）推导左缘。
            InvokePrivate(window, "WidthThumb_DragDelta", null, new DragDeltaEventArgs(-1000, 0));
            InvokePrivate(window, "WidthThumb_DragDelta", null, new DragDeltaEventArgs(80, 0));
            InvokePrivate(window, "WidthThumb_DragDelta", null, new DragDeltaEventArgs(1000, 0));
            InvokePrivate(window, "WidthThumb_DragDelta", null, new DragDeltaEventArgs(-40, 0));
            CompleteLayout(window);

            var expected = WindowSettings.Default;
            expected = (expected with { PanelWidth = expected.PanelWidth + 1000 }).Normalize(work.Width, work.Height);
            expected = (expected with { PanelWidth = expected.PanelWidth - 80 }).Normalize(work.Width, work.Height);
            expected = (expected with { PanelWidth = expected.PanelWidth - 1000 }).Normalize(work.Width, work.Height);
            expected = (expected with { PanelWidth = expected.PanelWidth + 40 }).Normalize(work.Width, work.Height);
            var visibleWidth = expected.PanelWidth + WindowSettings.TabWidth;
            DpiGeometryAssert.AreEqual(visibleWidth, window.Width, window, PixelQuantization.Single,
                "穿界快拖后可见宽＝归一化面板宽＋标签宽。");
            DpiGeometryAssert.AreEqual(work.Right, window.Left + window.Width, window, PixelQuantization.Pair,
                "钳制边界处右缘仍贴齐工作区右缘。");
            DpiGeometryAssert.IsAtLeast(window.Left, work.Left, window, PixelQuantization.Single, "窗口左缘不得推出工作区。");
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void WidthResize_DragCompletedDoesNotShiftTheDockedGeometry()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
        var savedCursor = SaveCursorPosition();

        try
        {
            window.Show();
            Assert.IsTrue(
                SetCursorPos(10, 10),
                "几何断言前必须把真实光标移离窗口。");
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var work = SystemParameters.WorkArea;

            InvokePrivate(
                window,
                "WidthThumb_DragDelta",
                null,
                new DragDeltaEventArgs(-40, 0));
            CompleteLayout(window);
            var leftBefore = window.Left;
            var topBefore = window.Top;
            var widthBefore = window.Width;
            var heightBefore = window.Height;

            InvokePrivate(
                window,
                "ResizeThumb_DragCompleted",
                null,
                new DragCompletedEventArgs(-40, 0, false));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            CompleteLayout(window);

            DpiGeometryAssert.AreEqual(leftBefore, window.Left, window, PixelQuantization.Single, "拖完松手后左缘不得二次移动。");
            DpiGeometryAssert.AreEqual(topBefore, window.Top, window, PixelQuantization.Single, "拖完松手后上缘不得二次移动。");
            DpiGeometryAssert.AreEqual(widthBefore, window.Width, window, PixelQuantization.Single, "拖完松手后宽度不得二次变化。");
            DpiGeometryAssert.AreEqual(heightBefore, window.Height, window, PixelQuantization.Single, "拖完松手后高度不得二次变化。");
            DpiGeometryAssert.AreEqual(work.Right, window.Left + window.Width, window, PixelQuantization.Pair,
                "松手存盘后右缘仍贴齐工作区右缘（无补偿性贴边）。");
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindow(window);
        }
    }

    /// <summary>
    /// 真实输入管线的快拖守护（契约 #13：合成路径可达不等于真实鼠标可达）：SendInput
    /// 硬件级注入按下→连续步进位移（中途反向模拟拖宽→拖窄往返）→抬起，逐步与端态
    /// 断言右缘贴齐——贴边左缘由目标宽度经原子通道推导（契约 #6，2026-11-06 修复），
    /// 与 WPF 对 Width 赋值的同步/延迟应用实现细节无关。
    /// </summary>
    [STATestMethod]
    public void WidthResize_RealInputFastDrag_KeepsRightEdgeDocked()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
        var savedCursor = SaveCursorPosition();

        try
        {
            window.Show();
            Assert.IsTrue(
                SetCursorPos(10, 10),
                "几何断言前必须把真实光标移离窗口。");
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var work = SystemParameters.WorkArea;

            var thumb = FindWidthResizeThumb(window);
            Assert.IsNotNull(thumb, "必须能找到左缘拖宽 Thumb（真实管线命中层）。");
            var deltaCount = 0;
            thumb.DragDelta += (_, _) => deltaCount++;

            // 按下点：左上角拖宽 Thumb（7×44 DIP 条带，窗口原点起）。真实光标先就位
            // （同 CardGestures 直驱模式），再 SendInput 真实按下（契约 #13：Thumb 拖拽
            // 会话需要真实 WM_LBUTTONDOWN 键态，PostMessage 补键态不被输入管线提升）。
            var screen = window.PointToScreen(new Point(3.5, Math.Min(22, thumb.ActualHeight * 0.5)));
            Assert.IsTrue(
                SetCursorPos((int)Math.Round(screen.X) + 7, (int)Math.Round(screen.Y) + 5),
                "真实光标必须可移动（强制位移预热）。");
            Assert.IsTrue(
                SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y)),
                "真实光标必须可移到 Thumb 上。");
            PumpUntilMouseOver(window);

            var down = new INPUT[1];
            down[0].Type = InputMouse;
            down[0].Mi.DwFlags = MouseEventfLeftdown;
            Assert.AreEqual(1u, SendInput(1, down, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>()), "真实按下注入必须成功。");
            try
            {
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(50));

                // 快拖：连续步进位移（每步 −30 物理像素、步间短泵窗；第 7 步起反向＋50
                // 模拟拖宽→拖窄快速往返），逐步断言拖拽全程右缘恒贴齐工作区右缘。
                var dragX = (int)Math.Round(screen.X);
                var dragY = (int)Math.Round(screen.Y);
                for (var step = 1; step <= 12; step++)
                {
                    dragX += step < 7 ? -30 : 50;
                    Assert.IsTrue(SetCursorPos(dragX, dragY), "真实光标必须跟随快拖轨迹。");
                    PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(15));
                    DpiGeometryAssert.AreEqual(work.Right, window.Left + window.Width, window, PixelQuantization.Pair,
                        $"快拖第 {step} 步后右缘仍贴齐工作区右缘。");
                }

                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(150));
            }
            finally
            {
                // 中途断言失败也必须抬起真实按键：注入态左键泄漏会污染后续真实输入测试。
                var up = new INPUT[1];
                up[0].Type = InputMouse;
                up[0].Mi.DwFlags = MouseEventfLeftup;
                Assert.AreEqual(1u, SendInput(1, up, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>()), "真实抬起注入必须成功。");
            }

            CompleteLayout(window);

            Assert.IsTrue(deltaCount > 0, "按下后必须产生 DragDelta（真实管线命中 Thumb）。");
            DpiGeometryAssert.AreEqual(work.Right, window.Left + window.Width, window, PixelQuantization.Pair,
                "真实输入快拖后右缘仍贴齐工作区右缘。");
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindow(window);
        }
    }

    /// <summary>视觉树中定位左缘拖宽 Thumb（SizeWE 光标是唯一定位特征）。</summary>
    private static System.Windows.Controls.Primitives.Thumb? FindWidthResizeThumb(MainWindow window)
    {
        System.Windows.Controls.Primitives.Thumb? found = null;
        void Visit(System.Windows.Media.Visual visual)
        {
            if (found is not null)
            {
                return;
            }

            if (visual is System.Windows.Controls.Primitives.Thumb
                {
                    Cursor: { } cursor
                } thumb &&
                cursor == System.Windows.Input.Cursors.SizeWE)
            {
                found = thumb;
                return;
            }

            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(visual); i++)
            {
                if (System.Windows.Media.VisualTreeHelper.GetChild(visual, i) is System.Windows.Media.Visual child)
                {
                    Visit(child);
                }
            }
        }

        Visit(window);
        return found;
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
