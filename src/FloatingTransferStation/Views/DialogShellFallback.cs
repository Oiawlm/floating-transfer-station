using System.Windows;
using System.Windows.Controls;

namespace FloatingTransferStation.Views;

/// <summary>
/// 对话框壳直角兜底（#30 修订 2026-10-18，Issue #186 D8 坑清单）：DWM 圆角偏好
/// 设置失败的环境（远程会话/旧系统）下，壳 Border 固定圆角 8 会呈现「内容方角但
/// 边框圆角」的破相——失败时把壳圆角归零，回退为可用矩形（1px 边框完整）。
/// </summary>
internal static class DialogShellFallback
{
    public static void Apply(Window window, bool shellApplied)
    {
        if (shellApplied || window.Content is not Border shell)
        {
            return;
        }

        shell.CornerRadius = default;
    }
}
