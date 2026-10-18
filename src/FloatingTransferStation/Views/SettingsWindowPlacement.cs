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

    /// <summary>
    /// 显示器信息缝合点（WM_GETMINMAXINFO 路径，按 hwnd 键）：返回 null 走真实查询。
    /// 供测试注入任务栏在左/上的假显示器，断言 PtMaxPosition = 工作区 − 显示器矩形。
    /// </summary>
    internal static Func<IntPtr, MonitorInfoEx?>? MonitorInfoResolverOverride;

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
    /// 无边框窗口的系统缩放与最大化修正（契约 #24）：按窗口所在显示器改写
    /// WM_GETMINMAXINFO——最大化位置/尺寸取工作区（无边框窗口默认会探出约一个
    /// resize 边框的量），并写入最小/最大交互轨道尺寸（[MinWidth, MaxWidth] ×
    /// [MinHeight, 工作区高 − MaxHeightMargin]，物理像素；WPF 侧被 handled 后
    /// 不再自行填写）。PtMaxPosition 语义是「最大化时窗口左上角相对显示器矩形
    /// 的偏移」＝工作区原点 − 显示器原点（任务栏在左/上时为正）。最大轨道高由
    /// 本方法查询到的显示器工作区直接推导（单次查询、窗口所在屏语义），不由
    /// 调用方传入。轨道尺寸是窗口管理器对一切尺寸变更（拖拽缩放、程序化
    /// SetWindowPos、贴靠与最大化）的统一钳制边界，因此系统最大化意图
    /// （WM_SYSCOMMAND SC_MAXIMIZE，由调用方以 <paramref name="isSystemMaximizing"/>
    /// 传入）时轨道上限让位给工作区尺寸——否则最大化会被 900/工作区−48 钳住，
    /// 重新制造 1.50.0 的死区。结构体必须与 Win32 MINMAXINFO 严格同构
    /// （5×POINT＝40 字节，POINT 为两个 32 位 LONG）——1.50.0 及之前用 short
    /// （每 POINT 4 字节、整体 20 字节）导致轨道尺寸字段从未写入、交互缩放
    /// 上下限失守。任一入参非有限（NaN/±∞）、DPI 非正或显示器查询失败 →
    /// 返回 false，交回 WPF 默认处理、缓冲区不动。
    /// </summary>
    internal static bool TryApplyMaximizeWorkArea(
        IntPtr hwnd,
        IntPtr lParam,
        double minWidth,
        double minHeight,
        double maxWidth,
        double dpiScaleX,
        double dpiScaleY,
        bool isSystemMaximizing)
    {
        if (!double.IsFinite(minWidth) || !double.IsFinite(minHeight) || !double.IsFinite(maxWidth) ||
            !double.IsFinite(dpiScaleX) || !double.IsFinite(dpiScaleY) ||
            dpiScaleX <= 0 || dpiScaleY <= 0)
        {
            return false;
        }

        var info = MonitorInfoResolverOverride?.Invoke(hwnd);
        if (info is null)
        {
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return false;
            }

            var queried = CreateMonitorInfo();
            if (!GetMonitorInfo(monitor, ref queried))
            {
                return false;
            }

            info = queried;
        }

        var minTrackX = ToTrackSize(minWidth, dpiScaleX);
        var minTrackY = ToTrackSize(minHeight, dpiScaleY);
        var maxTrackX = ToTrackSize(maxWidth, dpiScaleX);
        var maxTrackY = Math.Max(
            minTrackY,
            info.Value.RcWork.Bottom - info.Value.RcWork.Top -
            (int)Math.Round(Design.SettingsWindowMetrics.MaxHeightMargin * dpiScaleY));
        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        mmi.PtMaxPosition.X = info.Value.RcWork.Left - info.Value.RcMonitor.Left;
        mmi.PtMaxPosition.Y = info.Value.RcWork.Top - info.Value.RcMonitor.Top;
        mmi.PtMaxSize.X = info.Value.RcWork.Right - info.Value.RcWork.Left;
        mmi.PtMaxSize.Y = info.Value.RcWork.Bottom - info.Value.RcWork.Top;
        if (isSystemMaximizing)
        {
            // 最大化不受交互上限约束（轨道是系统级钳制边界）：轨道让位给工作区尺寸。
            maxTrackX = Math.Max(maxTrackX, mmi.PtMaxSize.X);
            maxTrackY = Math.Max(maxTrackY, mmi.PtMaxSize.Y);
        }

        mmi.PtMinTrackSize.X = minTrackX;
        mmi.PtMinTrackSize.Y = minTrackY;
        mmi.PtMaxTrackSize.X = maxTrackX;
        mmi.PtMaxTrackSize.Y = maxTrackY;
        Marshal.StructureToPtr(mmi, lParam, false);
        return true;
    }

    private static Pt ToPhysicalPoint(double dipX, double dipY, double dpiX, double dpiY) => new()
    {
        X = (int)Math.Round(dipX * dpiX / 96d),
        Y = (int)Math.Round(dipY * dpiY / 96d)
    };

    /// <summary>DIP → 物理像素轨道尺寸（MINMAXINFO 字段是 32 位 LONG，钳 [0, int.MaxValue]）。</summary>
    private static int ToTrackSize(double dip, double dpiScale) =>
        (int)Math.Clamp((long)Math.Round(dip * dpiScale), 0, int.MaxValue);

    private static MonitorInfoEx CreateMonitorInfo() =>
        new() { CbSize = Marshal.SizeOf<MonitorInfoEx>() };

    internal struct Pt
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    internal struct MonitorInfoEx
    {
        public int CbSize;
        public Rect RcMonitor;
        public Rect RcWork;
        public uint DwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    /// <summary>Win32 MINMAXINFO 镜像：5 个 POINT（int×2）＝40 字节，偏移 0/8/16/24/32。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MinMaxInfo
    {
        public Pt PtReserved;
        public Pt PtMaxSize;
        public Pt PtMaxPosition;
        public Pt PtMinTrackSize;
        public Pt PtMaxTrackSize;
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
