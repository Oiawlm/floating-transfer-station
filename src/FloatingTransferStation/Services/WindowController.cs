using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public readonly record struct WorkArea(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
}

public readonly record struct WindowPlacement(double Left, double Top, double Width, double Height);

/// <summary>
/// 物理像素矩形（与 <see cref="MonitorBounds"/>、Win32 坐标同系）。贴边隐藏的
/// 回位判定全部在物理像素上进行：GetCursorPos 物理坐标直比，不做任何运行时
/// DPI 换算（窗口离屏后其 CompositionTarget 变换不可信）。
/// </summary>
public readonly record struct PhysicalRectangle(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;

    /// <summary>点包含：含左上、不含右下（与 Win32 矩形语义一致）。</summary>
    public bool Contains(int x, int y) =>
        x >= Left && x < Right && y >= Top && y < Bottom;
}

public static class WindowController
{
    /// <summary>
    /// 整窗移出屏幕时越过最右显示器右缘的余量（物理像素）：DWM 阴影越出窗口
    /// 矩形，只贴缘会在最右屏缘留下可见暗边，「完全移出」要求连阴影一起带走。
    /// </summary>
    public const int EdgeHideOffscreenClearancePx = 32;
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

        var width = VisibleWidth(WindowSettings.TabWidth, workArea);
        return new WindowPlacement(
            workArea.Right - width,
            workArea.Top + normalized.Top + (rowIndex * rowHeight),
            width,
            rowHeight);
    }

    public static WindowPlacement Expanded(WorkArea workArea, WindowSettings settings)
    {
        var normalized = settings.Normalize(workArea.Width, workArea.Height);
        var width = VisibleWidth(WindowSettings.TabWidth + normalized.PanelWidth, workArea);
        return new WindowPlacement(
            workArea.Right - width,
            workArea.Top + normalized.Top,
            width,
            normalized.WindowHeight);
    }

    public static WindowPlacement CategoryRail(WorkArea workArea, WindowSettings settings)
    {
        var normalized = settings.Normalize(workArea.Width, workArea.Height);
        var width = VisibleWidth(WindowSettings.TabWidth, workArea);
        return new WindowPlacement(
            workArea.Right - width,
            workArea.Top + normalized.Top,
            width,
            normalized.WindowHeight);
    }

    // 窗口完整落在工作区内（可见态外形恒定）：宽度永不把左缘推出工作区。
    private static double VisibleWidth(double desiredWidth, WorkArea workArea) =>
        Math.Min(Math.Max(0, desiredWidth), Math.Max(0, workArea.Width));

    /// <summary>
    /// 一次性贴边隐藏的目标矩形（物理像素）：整窗平移到所有显示器右缘之外，
    /// 尺寸与垂直位置不变。可测不变量：与每一个显示器矩形零交集——多显示器下
    /// 右邻屏接不住滑出像素；无右邻时与「贴本屏右缘外移」等价（另有阴影余量）。
    /// </summary>
    public static PhysicalRectangle EdgeHidden(
        PhysicalRectangle window,
        IReadOnlyList<MonitorBounds> monitors)
    {
        if (monitors.Count == 0)
        {
            throw new ArgumentException("贴边隐藏至少需要一个显示器。", nameof(monitors));
        }

        var offscreenLeft = monitors.Max(candidate => candidate.Right) +
            EdgeHideOffscreenClearancePx;
        return window with { Left = offscreenLeft, Right = offscreenLeft + window.Width };
    }

    /// <summary>
    /// 贴边隐藏的回位判定区（物理像素）：隐藏时刻的屏幕内可见矩形四向外扩
    /// 小容差（容许光标落点偏差）。光标物理坐标直比，驻留时长由调用方累积。
    /// </summary>
    public static PhysicalRectangle EdgeRecallZone(
        PhysicalRectangle visibleAtHide,
        int tolerance) =>
        new(
            visibleAtHide.Left - tolerance,
            visibleAtHide.Top - tolerance,
            visibleAtHide.Right + tolerance,
            visibleAtHide.Bottom + tolerance);
}
