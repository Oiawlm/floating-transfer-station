using System.Windows;
using System.Windows.Interop;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

/// <summary>一个显示器的完整边界与工作区（虚拟屏幕物理像素坐标）。</summary>
public readonly record struct MonitorBounds(int Left, int Top, int Right, int Bottom)
{
    internal static MonitorBounds FromNativeRect(NativeMethods.NativeRect bounds) =>
        new(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
}

/// <summary>
/// 判定贴右缘窗口能否做「边缘裁切」（窗口右缘越出屏幕一个 WindowSettings.EdgeBleed，
/// 让 DWM 圆角落在屏外）。仅当所在显示器的工作区右缘与显示器右缘重合、
/// 且右侧没有占据该边界的其他显示器时才可裁切；右贴任务栏或邻接显示器时回退 0（现状贴齐）。
/// 原生探测按位返回失败语义：任何一步失败都回退 0，不抛出。
/// </summary>
public static class ScreenEdgeGeometry
{
    public static double GetRightEdgeBleed(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        return handle == 0 ? 0d : GetRightEdgeBleed(handle);
    }

    /// <summary>纯判定：供单测以伪造显示器/工作区矩形驱动，不依赖真机多屏。</summary>
    public static double RightEdgeBleedFor(
        MonitorBounds monitor,
        MonitorBounds workArea,
        IReadOnlyList<MonitorBounds> allMonitors)
    {
        if (workArea.Right != monitor.Right)
        {
            // 任务栏（或停靠面板）占用右缘：越出部分会盖住它，回退贴齐。
            return 0d;
        }

        foreach (var other in allMonitors)
        {
            if (other == monitor)
            {
                continue;
            }

            // 邻接或重叠到右缘边界的显示器会承接越出的窗口像素，回退贴齐。
            if (other.Left <= monitor.Right && other.Right > monitor.Right)
            {
                return 0d;
            }
        }

        return WindowSettings.EdgeBleed;
    }

    private static double GetRightEdgeBleed(nint windowHandle)
    {
        try
        {
            var monitor = NativeMethods.MonitorFromWindow(
                windowHandle,
                NativeMethods.MonitorDefaultToNearest);
            if (monitor == 0)
            {
                return 0d;
            }

            var info = new NativeMethods.MonitorInfo
            {
                Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>()
            };
            if (!NativeMethods.GetMonitorInfoW(monitor, ref info))
            {
                return 0d;
            }

            var monitors = new List<MonitorBounds>();
            var currentBounds = MonitorBounds.FromNativeRect(info.Monitor);
            if (!NativeMethods.EnumDisplayMonitors(
                    0,
                    0,
                    (_, _, ref bounds, _) =>
                    {
                        monitors.Add(MonitorBounds.FromNativeRect(bounds));
                        return true;
                    },
                    0))
            {
                return 0d;
            }

            return RightEdgeBleedFor(
                currentBounds,
                MonitorBounds.FromNativeRect(info.Work),
                monitors);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or OutOfMemoryException)
        {
            // List/委托编组失败属于可恢复异常：保持贴齐现状。
            return 0d;
        }
    }
}
