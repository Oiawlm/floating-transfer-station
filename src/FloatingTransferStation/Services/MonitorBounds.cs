namespace FloatingTransferStation.Services;

/// <summary>一个显示器的完整边界与工作区（虚拟屏幕物理像素坐标）。</summary>
public readonly record struct MonitorBounds(int Left, int Top, int Right, int Bottom)
{
    internal static MonitorBounds FromNativeRect(NativeMethods.NativeRect bounds) =>
        new(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);

    /// <summary>
    /// 枚举全部显示器边界（虚拟屏幕物理像素）。贴边隐藏以此求「所有显示器
    /// 最右缘」；原生枚举失败返回空列表，调用方自行回退。
    /// </summary>
    public static IReadOnlyList<MonitorBounds> AllMonitors()
    {
        var monitors = new List<MonitorBounds>();
        return NativeMethods.EnumDisplayMonitors(
            0,
            0,
            (_, _, ref bounds, _) =>
            {
                monitors.Add(MonitorBounds.FromNativeRect(bounds));
                return true;
            },
            0)
            ? monitors
            : Array.Empty<MonitorBounds>();
    }
}
