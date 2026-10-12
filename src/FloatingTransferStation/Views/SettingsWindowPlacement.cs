using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FloatingTransferStation.Views;

/// <summary>
/// 设置窗口几何规则（契约 #24，Issue #106）：合法性校验、工作区钳制与显示器工作区查询。
/// 内部一律 DIP；仅在 Win32 边界按目标显示器有效 DPI 换算（物理像素 ↔ DIP）。
/// <see cref="WorkAreaResolverOverride"/> 是测试缝合点：默认走 Win32
/// （MonitorFromPoint/MonitorFromWindow + GetMonitorInfo + GetDpiForMonitor），
/// 测试注入假工作区模拟多屏 / 左侧副屏（负坐标）/ 显示器断开。
/// </summary>
internal static class SettingsWindowPlacement
{
    internal readonly record struct WorkArea(double Left, double Top, double Right, double Bottom)
    {
        public double Width => Right - Left;
        public double Height => Bottom - Top;
    }

    /// <summary>
    /// 显示器工作区查询缝合点：入参为持久化矩形中心（DIP），返回其所在显示器的
    /// 工作区（DIP）；返回 null = 查询不可用，调用方退主屏工作区。
    /// </summary>
    internal static Func<double, double, WorkArea?>? WorkAreaResolverOverride;

    /// <summary>虚拟桌面界缝合点（测试多屏布局用）：生产直接读 SystemParameters.VirtualScreen*。</summary>
    internal static Func<(double Left, double Top, double Width, double Height)>? VirtualScreenBoundsOverride;

    /// <summary>几何字段整组合法性：四个值齐备、全部有限、宽高为正。
    /// NaN/±∞/非正尺寸任一出现即整组作废（回落默认定位）。</summary>
    internal static bool IsValid(double? left, double? top, double? width, double? height) =>
        left is { } l && top is { } t && width is { } w && height is { } h &&
        double.IsFinite(l) && double.IsFinite(t) && double.IsFinite(w) && double.IsFinite(h) &&
        w > 0 && h > 0;

    /// <summary>持久化矩形是否与虚拟桌面（全部显示器的并集）相交；
    /// 完全落在所有显示器之外 → 调用方整组回落默认定位。</summary>
    internal static bool IntersectsVirtualScreen(double left, double top, double width, double height)
    {
        var (vsLeft, vsTop, vsWidth, vsHeight) = VirtualScreenBoundsOverride?.Invoke()
            ?? (SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        return left + width > vsLeft
            && left < vsLeft + vsWidth
            && top + height > vsTop
            && top < vsTop + vsHeight;
    }

    /// <summary>
    /// 把持久化矩形归一到目标工作区内：尺寸先钳到 [min, max]，位置钳进工作区——
    /// 部分越界钳到最近工作区边界；负坐标在左侧副屏工作区内原样保留（不钳非负）。
    /// maxHeight 已由调用方含「矮屏抬到 minHeight」语义。
    /// </summary>
    internal static (double Left, double Top, double Width, double Height) Normalize(
        double left,
        double top,
        double width,
        double height,
        double minWidth,
        double maxWidth,
        double minHeight,
        double maxHeight,
        WorkArea workArea)
    {
        var normalizedWidth = Math.Clamp(width, minWidth, Math.Max(minWidth, maxWidth));
        var normalizedHeight = Math.Clamp(height, minHeight, Math.Max(minHeight, maxHeight));
        var normalizedLeft = Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - normalizedWidth));
        var normalizedTop = Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - normalizedHeight));
        return (normalizedLeft, normalizedTop, normalizedWidth, normalizedHeight);
    }

    /// <summary>
    /// 按目标点（DIP）解析所在显示器工作区（DIP）。首次换算用窗口当前 DPI 定位
    /// 显示器，再用该显示器有效 DPI 复核落点（跨 DPI 边界至多两轮收敛）；
    /// 无句柄或任何 Win32 环节失败返回 null（调用方退 SystemParameters.WorkArea，
    /// 与 GOAL §四.3「无句柄用主屏工作区」一致）。
    /// </summary>
    internal static WorkArea? QueryWorkArea(Window? window, double dipX, double dipY)
    {
        try
        {
            var handle = window is null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            var fromDevice = (PresentationSource.FromVisual(window) as HwndSource)
                ?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            var monitor = MonitorFromPoint(
                ToPhysicalPoint(dipX, dipY, fromDevice.M11, fromDevice.M22),
                MonitorDefaultToNearest);
            for (var attempt = 0; attempt < 2 && monitor != IntPtr.Zero; attempt++)
            {
                var info = CreateMonitorInfo();
                if (!GetMonitorInfo(monitor, ref info)
                    || GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out var dpiY) != 0)
                {
                    return null;
                }

                var work = new WorkArea(
                    info.RcWork.Left * 96d / dpiX,
                    info.RcWork.Top * 96d / dpiY,
                    info.RcWork.Right * 96d / dpiX,
                    info.RcWork.Bottom * 96d / dpiY);

                // 用显示器自身 DPI 复核落点：仍命中同一显示器即收敛，否则换屏重查。
                var recheck = MonitorFromPoint(ToPhysicalPoint(dipX, dipY, dpiX, dpiY), MonitorDefaultToNearest);
                if (recheck == monitor)
                {
                    return work;
                }

                monitor = recheck;
            }

            return null;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// 无边框窗口的系统最大化修正（T7 评审采纳项）：按窗口所在显示器工作区改写
    /// WM_GETMINMAXINFO 的最大化位置/尺寸（默认会超出工作区约一个 resize 边框的量），
    /// 并按窗口 DPI 写入最小/最大轨道尺寸（WPF 侧被 handled 后不再自行填写）。
    /// 返回 false = 环境不可用，交回 WPF 默认处理。
    /// </summary>
    internal static bool TryApplyMaximizeWorkArea(
        IntPtr hwnd,
        IntPtr lParam,
        double minWidth,
        double minHeight,
        double maxWidth,
        double maxHeight,
        double dpiScaleX,
        double dpiScaleY)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = CreateMonitorInfo();
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        mmi.PtMaxPosition.X = (short)(info.RcMonitor.Left - info.RcWork.Left);
        mmi.PtMaxPosition.Y = (short)(info.RcMonitor.Top - info.RcWork.Top);
        mmi.PtMaxSize.X = (short)(info.RcWork.Right - info.RcWork.Left);
        mmi.PtMaxSize.Y = (short)(info.RcWork.Bottom - info.RcWork.Top);
        mmi.PtMinTrackSize.X = ToTrackSize(minWidth, dpiScaleX);
        mmi.PtMinTrackSize.Y = ToTrackSize(minHeight, dpiScaleY);
        mmi.PtMaxTrackSize.X = ToTrackSize(maxWidth, dpiScaleX);
        mmi.PtMaxTrackSize.Y = ToTrackSize(maxHeight, dpiScaleY);
        Marshal.StructureToPtr(mmi, lParam, false);
        return true;
    }

    private static Pt ToPhysicalPoint(double dipX, double dipY, double dpiX, double dpiY) => new()
    {
        X = (int)Math.Round(dipX * dpiX / 96d),
        Y = (int)Math.Round(dipY * dpiY / 96d)
    };

    private static short ToTrackSize(double dip, double dpiScale) =>
        (short)Math.Clamp((long)Math.Round(dip * dpiScale), 0, short.MaxValue);

    private static MonitorInfoEx CreateMonitorInfo() =>
        new() { CbSize = Marshal.SizeOf<MonitorInfoEx>() };

    [StructLayout(LayoutKind.Sequential)]
    private struct Pt
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfoEx
    {
        public int CbSize;
        public Rect RcMonitor;
        public Rect RcWork;
        public uint DwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Points
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Points PtReserved;
        public Points PtMaxSize;
        public Points PtMaxPosition;
        public Points PtMinTrackSize;
        public Points PtMaxTrackSize;
    }

    private const uint MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Pt point, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int kind, out uint dpiX, out uint dpiY);
}
