using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public readonly record struct WorkArea(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
}

public readonly record struct WindowPlacement(double Left, double Top, double Width, double Height);

public static class WindowController
{
    public static WindowPlacement Collapsed(
        WorkArea workArea,
        WindowSettings settings,
        BoardCategory defaultCategory,
        double edgeBleed = 0)
    {
        if (!BoardCategoryCatalog.IsDefined(defaultCategory))
        {
            throw new ArgumentOutOfRangeException(nameof(defaultCategory));
        }

        var normalized = settings.Normalize(workArea.Width, workArea.Height);
        var rowHeight = normalized.WindowHeight / BoardCategoryCatalog.Ordered.Count;
        var rowIndex = 0;
        while (BoardCategoryCatalog.Ordered[rowIndex] != defaultCategory)
        {
            rowIndex++;
        }

        var visibleWidth = VisibleWidth(WindowSettings.TabWidth, workArea);
        return new WindowPlacement(
            workArea.Right - visibleWidth,
            workArea.Top + normalized.Top + (rowIndex * rowHeight),
            visibleWidth + SanitizedBleed(edgeBleed),
            rowHeight);
    }

    public static WindowPlacement Expanded(WorkArea workArea, WindowSettings settings, double edgeBleed = 0)
    {
        var normalized = settings.Normalize(workArea.Width, workArea.Height);
        var visibleWidth = VisibleWidth(
            WindowSettings.TabWidth + normalized.PanelWidth,
            workArea);
        return new WindowPlacement(
            workArea.Right - visibleWidth,
            workArea.Top + normalized.Top,
            visibleWidth + SanitizedBleed(edgeBleed),
            normalized.WindowHeight);
    }

    public static WindowPlacement CategoryRail(WorkArea workArea, WindowSettings settings, double edgeBleed = 0)
    {
        var normalized = settings.Normalize(workArea.Width, workArea.Height);
        var visibleWidth = VisibleWidth(WindowSettings.TabWidth, workArea);
        return new WindowPlacement(
            workArea.Right - visibleWidth,
            workArea.Top + normalized.Top,
            visibleWidth + SanitizedBleed(edgeBleed),
            normalized.WindowHeight);
    }

    // 可见宽度：窗口越出屏幕右缘的部分不占可见空间，且永远不把左缘推出工作区。
    private static double VisibleWidth(double desiredVisibleWidth, WorkArea workArea) =>
        Math.Min(Math.Max(0, desiredVisibleWidth), Math.Max(0, workArea.Width));

    private static double SanitizedBleed(double edgeBleed) => Math.Max(0, edgeBleed);
}
