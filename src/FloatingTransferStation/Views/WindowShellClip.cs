using System.Windows;
using System.Windows.Media;

namespace FloatingTransferStation.Views;

internal static class WindowShellClip
{
    /// <summary>
    /// 四角统一圆角裁剪，与 DWM（DWMWA_WINDOW_CORNER_PREFERENCE）的窗口圆角对齐；
    /// 内容层自裁剪保证渲染目标位图（测试证据）与屏幕呈现一致。
    /// </summary>
    public static Geometry Create(double width, double height, double radius)
    {
        if (!double.IsFinite(width) ||
            !double.IsFinite(height) ||
            !double.IsFinite(radius) ||
            width <= 0d ||
            height <= 0d)
        {
            return Geometry.Empty;
        }

        var boundedRadius = Math.Clamp(radius, 0d, Math.Min(width, height) / 2d);
        var roundedShell = new RectangleGeometry(
            new Rect(0d, 0d, width, height),
            boundedRadius,
            boundedRadius);
        roundedShell.Freeze();
        return roundedShell;
    }
}
