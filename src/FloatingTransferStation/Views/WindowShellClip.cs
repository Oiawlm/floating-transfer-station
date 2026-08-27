using System.Windows;
using System.Windows.Media;

namespace FloatingTransferStation.Views;

internal static class WindowShellClip
{
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
        var squareRightEdge = new RectangleGeometry(
            new Rect(width - boundedRadius, 0d, boundedRadius, height));
        var clip = new CombinedGeometry(
            GeometryCombineMode.Union,
            roundedShell,
            squareRightEdge);
        clip.Freeze();
        return clip;
    }
}
