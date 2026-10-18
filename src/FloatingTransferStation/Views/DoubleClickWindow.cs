using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace FloatingTransferStation.Views;

/// <summary>
/// 系统双击窗判定（#30 修订 2026-10-18 自 MainWindow.CardGestures.cs 抽取共享，
/// Issue #186）：时限取系统 GetDoubleClickTime、按下位置容差取 SM_CX/CYDOUBLECLK
/// 并按窗口 DPI 换算成 DIP——尊重用户的鼠标配置，卡片左半就地编辑与启动瓦片
/// 名称带改名共用同一判定口径（同一时间窗内同位两击才算双击意图）。
/// </summary>
internal static class DoubleClickWindow
{
    public const int SmCxDblClk = 36;
    public const int SmCyDblClk = 37;

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>两击（按下时刻/位置）是否落在同一双击窗内：系统时限内且按下位置
    /// 在系统容差内。容差是物理像素、位置是 DIP：按来源窗口 DPI 换算一次再比较
    /// （150%/200% 缩放下不失真）。</summary>
    public static bool Contains(int lastTimestamp, Point lastPosition, int timestamp, Point position, Visual dpiSource)
    {
        var delta = (long)timestamp - lastTimestamp;
        var dpi = VisualTreeHelper.GetDpi(dpiSource);
        var toleranceX = GetSystemMetrics(SmCxDblClk) / dpi.DpiScaleX;
        var toleranceY = GetSystemMetrics(SmCyDblClk) / dpi.DpiScaleY;
        return delta >= 0 &&
            delta <= GetDoubleClickTime() &&
            Math.Abs(position.X - lastPosition.X) <= toleranceX &&
            Math.Abs(position.Y - lastPosition.Y) <= toleranceY;
    }
}
