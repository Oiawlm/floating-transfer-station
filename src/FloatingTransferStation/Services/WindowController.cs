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
        BoardCategory defaultCategory)
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

        return new WindowPlacement(
            workArea.Right - WindowSettings.TabWidth,
            workArea.Top + normalized.Top + (rowIndex * rowHeight),
            WindowSettings.TabWidth,
            rowHeight);
    }

    public static WindowPlacement Expanded(WorkArea workArea, WindowSettings settings)
    {
        var normalized = settings.Normalize(workArea.Width, workArea.Height);
        var width = WindowSettings.TabWidth + normalized.PanelWidth;
        return new WindowPlacement(
            workArea.Right - width,
            workArea.Top + normalized.Top,
            width,
            normalized.WindowHeight);
    }

    public static WindowPlacement CategoryRail(WorkArea workArea, WindowSettings settings)
    {
        var normalized = settings.Normalize(workArea.Width, workArea.Height);
        return new WindowPlacement(
            workArea.Right - WindowSettings.TabWidth,
            workArea.Top + normalized.Top,
            WindowSettings.TabWidth,
            normalized.WindowHeight);
    }
}
