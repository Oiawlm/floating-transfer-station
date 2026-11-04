using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 左半裸单击送达的触发判定（2026-11-03 需求 6，契约 #36 第三递送通道/#14 修订）：
/// 单击延迟到双击窗口期满才触发；双击进编辑不送达；拖出优先于送达；修饰键单击
/// 不送达；图片卡与空白正文无行为。送达管线的序列与失败分支在
/// InputDeliveryServiceTests（Win32 接缝假件）；真实输入触发链路（指针判定部分）
/// 在 CardHalves_RealInputLeftHalfBareClickHasNoSelectionSemantics 与本文件。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    private const uint VirtualKeyControl = 0x11;

    /// <summary>真实按住 Ctrl（SendInput 硬件级注入，修饰键判定读真实键盘态）。</summary>
    private static void HoldRealControl(MainWindow window)
    {
        var inputs = new INPUT[1];
        inputs[0].Type = InputKeyboard;
        inputs[0].Mi.Dx = (int)VirtualKeyControl;
        Assert.AreEqual(1u, SendInput(1, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>()), "真实 Ctrl 按下注入必须成功。");
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(80));
    }

    /// <summary>释放真实 Ctrl。</summary>
    private static void ReleaseRealControl(MainWindow window)
    {
        var inputs = new INPUT[1];
        inputs[0].Type = InputKeyboard;
        inputs[0].Mi.Dx = (int)VirtualKeyControl;
        inputs[0].Mi.DwFlags = KeyEventfKeyup;
        Assert.AreEqual(1u, SendInput(1, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>()), "真实 Ctrl 抬起注入必须成功。");
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(80));
    }

    private static Point ContentZoneCenter(ListBoxItem container)
    {
        var contentZone = HalvesZoneOf(container, CardGestureZones.ContentZone);
        var zoneOrigin = contentZone.TranslatePoint(new Point(), container);
        return new Point(
            zoneOrigin.X + contentZone.ActualWidth / 2,
            zoneOrigin.Y + contentZone.ActualHeight * 0.6);
    }

    [STATestMethod]
    public void TextDelivery_DoubleClickOpensEditorInsteadOfDelivering()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("双击进编辑不送达", BoardCategory.Inbox);
        var delivery = new RecordingInputDeliveryNative();
        var window = CreateWindow(directory, board, inputDelivery: new InputDeliveryService(delivery));

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            // 合成双击（时间戳确定性配对，与既有编辑用例 RaiseCardDoubleClick 同
            // 一口径；真实输入的触发链路由 CardHalves 裸单击用例覆盖）：第一击
            // 的 Up 调度送达，第二击的 Down 取消挂起，其 Up 分发进入就地编辑
            //（双击优先于单击送达）。
            RaiseCardDoubleClick(ZoneHitSource(window, item, CardGestureZones.ContentZone));
            PumpDispatcherFor(
                window.Dispatcher,
                TimeSpan.FromMilliseconds(GetDoubleClickTime() + 150));

            Assert.IsTrue(item.IsEditing, "双击必须进入就地编辑（双击优先于送达）。");
            Assert.IsFalse(
                delivery.Calls.Contains("send-ctrl-v"),
                "双击不得触发送达。序列=" + string.Join(",", delivery.Calls));
            Assert.IsFalse(
                delivery.Calls.Contains("write-clipboard"),
                "双击不得写送达负载。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void TextDelivery_SecondPressCancelsPendingDelivery()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("第二击取消挂起", BoardCategory.Inbox);
        var delivery = new RecordingInputDeliveryNative();
        var window = CreateWindow(directory, board, inputDelivery: new InputDeliveryService(delivery));

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var hitSource = ZoneHitSource(window, item, CardGestureZones.ContentZone);

            // 单击（合成路径：Up 分发调度送达）→ 双击窗口内新的按压 Down →
            // 挂起送达被取消，窗口期满后不送达。
            hitSource.RaiseEvent(NewMouseButtonEventArgs(
                Mouse.PreviewMouseDownEvent,
                hitSource,
                NextIsolatedClickTimestamp()));
            hitSource.RaiseEvent(NewMouseButtonEventArgs(
                Mouse.PreviewMouseUpEvent,
                hitSource,
                NextIsolatedClickTimestamp()));
            hitSource.RaiseEvent(NewMouseButtonEventArgs(
                Mouse.PreviewMouseDownEvent,
                hitSource,
                NextIsolatedClickTimestamp()));
            PumpDispatcherFor(
                window.Dispatcher,
                TimeSpan.FromMilliseconds(GetDoubleClickTime() + 150));

            Assert.IsFalse(
                delivery.Calls.Contains("send-ctrl-v"),
                "窗口内出现新按压必须取消挂起送达。序列=" + string.Join(",", delivery.Calls));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void TextDelivery_DragPressDoesNotScheduleDelivery()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("拖出优先于送达", BoardCategory.Inbox);
        var delivery = new RecordingInputDeliveryNative();
        var window = CreateWindow(directory, board, inputDelivery: new InputDeliveryService(delivery));

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var hitSource = ZoneHitSource(window, item, CardGestureZones.ContentZone);

            // 超过拖拽阈值的按压会话（与 CardGestures_DragSuppresses… 同法）：
            // Up 不分发任何点击语义——不调度送达（拖出优先于送达）。
            var down = NewMouseButtonEventArgs(
                Mouse.PreviewMouseDownEvent,
                hitSource,
                NextIsolatedClickTimestamp());
            hitSource.RaiseEvent(down);
            typeof(MainWindow).GetField(
                "_dragThresholdCrossed",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(window, true);
            hitSource.RaiseEvent(NewMouseButtonEventArgs(
                Mouse.PreviewMouseUpEvent,
                hitSource,
                down.Timestamp + 5));
            PumpDispatcherFor(
                window.Dispatcher,
                TimeSpan.FromMilliseconds(GetDoubleClickTime() + 150));

            Assert.IsFalse(
                delivery.Calls.Contains("send-ctrl-v"),
                "成拖拽的按压不得送达。序列=" + string.Join(",", delivery.Calls));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void TextDelivery_ModifierClickDoesNotDeliver()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("修饰键单击不送达", BoardCategory.Inbox);
        var delivery = new RecordingInputDeliveryNative();
        var window = CreateWindow(directory, board, inputDelivery: new InputDeliveryService(delivery));

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            window.Topmost = true;
            CompleteLayout(window);
            var clickPoint = ContentZoneCenter(container);
            var savedCursor = SaveCursorPosition();

            try
            {
                // 真实 Ctrl 按住（修饰键判定读真实键盘态）＋真实单击：不送达。
                HoldRealControl(window);
                try
                {
                    ClickRealAt(window, container, clickPoint);
                }
                finally
                {
                    ReleaseRealControl(window);
                }

                PumpDispatcherFor(
                    window.Dispatcher,
                    TimeSpan.FromMilliseconds(GetDoubleClickTime() + 150));
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }

            Assert.IsFalse(
                delivery.Calls.Contains("send-ctrl-v"),
                "带修饰键的单击不得送达。序列=" + string.Join(",", delivery.Calls));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void TextDelivery_ImageCardBareClickDoesNotDeliver()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var imagePath = Path.Combine(directory.Root, "delivery-image.png");
        WritePng(imagePath, width: 24, height: 12);
        var image = board.AddImage(
            Guid.NewGuid(), "images/delivery-image.png", imagePath, BoardCategory.Inbox);
        var delivery = new RecordingInputDeliveryNative();
        var window = CreateWindow(directory, board, inputDelivery: new InputDeliveryService(delivery));

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            var hitSource = ZoneHitSource(window, image, CardGestureZones.ContentZone);
            hitSource.RaiseEvent(NewMouseButtonEventArgs(
                Mouse.PreviewMouseDownEvent,
                hitSource,
                NextIsolatedClickTimestamp()));
            hitSource.RaiseEvent(NewMouseButtonEventArgs(
                Mouse.PreviewMouseUpEvent,
                hitSource,
                NextIsolatedClickTimestamp()));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(30));
            PumpDispatcherFor(
                window.Dispatcher,
                TimeSpan.FromMilliseconds(GetDoubleClickTime() + 150));

            // 按压时刻的目标捕获（get-foreground）对任何卡面按压都会发生（含图片
            // 卡）——它不是送达动作；断言只看写负载与注入。
            Assert.IsFalse(
                delivery.Calls.Contains("write-clipboard") ||
                delivery.Calls.Contains("send-ctrl-v"),
                "图片卡的裸单击必须维持无行为（不调度送达）。序列=" +
                string.Join(",", delivery.Calls));
            // 空白正文卡不可达（BoardService.AddText 拒绝纯空白、编辑器空白提交
            //=取消），调度处的 IsNullOrWhiteSpace 守卫是纵深防御，无对应可达路径。
        }
        finally
        {
            CloseWindow(window);
        }
    }
}
