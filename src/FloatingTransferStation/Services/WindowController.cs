using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public readonly record struct WorkArea(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
}

public readonly record struct WindowPlacement(double Left, double Top, double Width, double Height);

/// <summary>
/// 物理像素矩形（与 <see cref="MonitorBounds"/>、Win32 坐标同系）。peek 几何
/// （收缩目标、把手命中重验）全部在物理像素上进行：GetCursorPos 物理坐标直比，
/// DIP→物理换算只在入口经 CompositionTarget 一次完成。
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
        // 收起把手行号按显示顺序（1.25.0 标签顺序可定制）而非目录默认序。
        var displayOrder = settings.DisplayOrder;
        var rowHeight = normalized.WindowHeight / displayOrder.Count;
        var rowIndex = 0;
        while (displayOrder[rowIndex] != defaultCategory)
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
    /// 边缘 peek 的目标矩形（物理像素，R12 契约 #17）：整窗同尺寸纯平移收缩，
    /// 绝大部分移出隐藏时刻所在工作区右缘，屏内仅保留左侧把手。可测不变量：
    /// 结果矩形与工作区的交集宽恰等于把手宽（handleWidthPx，调用方经
    /// CompositionTarget 由 DIP 一次换算），Top 与尺寸不变。把手宽大于窗口宽的
    /// 退化输入下整个窗口留在屏内（交集宽＝窗口宽，钳制为更小者）。
    /// </summary>
    public static PhysicalRectangle EdgePeekPlacement(
        PhysicalRectangle window,
        PhysicalRectangle workArea,
        int handleWidthPx)
    {
        var inScreenWidth = Math.Min(handleWidthPx, window.Width);
        var peekLeft = workArea.Right - inScreenWidth;
        return window with { Left = peekLeft, Right = peekLeft + window.Width };
    }
}
