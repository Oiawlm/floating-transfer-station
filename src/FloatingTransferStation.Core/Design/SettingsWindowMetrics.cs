namespace FloatingTransferStation.Design;

/// <summary>
/// 设置窗口冻结几何（契约 #24，Issue #106）的唯一声明点。
/// XAML（经 x:Static）、WM_GETMINMAXINFO 处理器与持久化恢复路径共同引用，
/// 禁止在任何位置出现第二份字面量；调整任何值必须同步修订契约 #24 与
/// SettingsWindowGeometryTests。
/// </summary>
public static class SettingsWindowMetrics
{
    /// <summary>首次运行（几何不可用回落）时的默认尺寸（DIP）。</summary>
    public const double DefaultWidth = 680;
    public const double DefaultHeight = 600;

    /// <summary>交互缩放下限（DIP）：声明于 Window 元素，同时是 WM_GETMINMAXINFO 最小轨道尺寸来源。</summary>
    public const double MinWidth = 560;
    public const double MinHeight = 460;

    /// <summary>交互缩放宽度上限（DIP）：只经 WM_GETMINMAXINFO 最大轨道尺寸强制；
    /// 不声明为 Window.MaxWidth——HWND 允许超宽（最大化/拖宽由系统路径约束），
    /// 由壳层铺满＋内容列居中保证无死区。</summary>
    public const double MaxWidth = 900;

    /// <summary>最大高冻结式的边距（DIP）：max(最小高, 所在工作区高 − 48)。</summary>
    public const double MaxHeightMargin = 48;

    /// <summary>内容列宽度上限（DIP）＝ 900 − 导航轨 148 − 内容水平边距 2×16；
    /// 内容列在此宽度内水平居中，任何窗口宽度（含最大化）下无死区。</summary>
    public const double ContentColumnMaxWidth = 720;
}
