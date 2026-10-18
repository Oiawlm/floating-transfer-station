using FloatingTransferStation.Services;

namespace FloatingTransferStation.Services;

/// <summary>
/// Windows 11 DWM 窗口效果：Mica 材质背景、系统圆角与沉浸式深色标题。
/// 全部调用按位返回 HRESULT，失败时由调用方回退到不透明壳（材质不可用的旧系统）。
/// </summary>
internal static class DwmWindowEffects
{
    /// <summary>
    /// 对话框壳圆角（#30 修订 2026-10-18，Issue #186 D8）：只设圆角偏好与深浅标题
    /// （不动系统背板——对话框不使用 Mica/Acrylic，不透明 DialogSurfaceBrush 承载
    /// 表面）；系统阴影随圆角偏好自动生效。失败返回 false（调用方回退直角+边框，
    /// 可用性兜底）。
    /// </summary>
    public static bool TryApplyDialogShell(nint hwnd, bool darkMode)
    {
        if (hwnd == 0)
        {
            return false;
        }

        var size = sizeof(int);
        var dark = darkMode ? 1 : 0;
        var corner = NativeMethods.DwmcwpRound;
        var cornerResult = NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DwmwaWindowCornerPreference, ref corner, size);
        _ = NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DwmwaUseImmersiveDarkMode, ref dark, size);
        return cornerResult == 0;
    }

    public static bool TryApplyMaterial(nint hwnd, bool darkMode)
    {
        if (hwnd == 0)
        {
            return false;
        }

        var size = sizeof(int);
        var dark = darkMode ? 1 : 0;
        var corner = NativeMethods.DwmcwpRound;
        var backdrop = NativeMethods.DwmbtMica;
        _ = NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DwmwaUseImmersiveDarkMode, ref dark, size);
        _ = NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DwmwaWindowCornerPreference, ref corner, size);
        var backdropResult = NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DwmwaSystemBackdropType, ref backdrop, size);
        return backdropResult == 0;
    }

    public static void UpdateImmersiveDarkMode(nint hwnd, bool darkMode)
    {
        if (hwnd == 0)
        {
            return;
        }

        var dark = darkMode ? 1 : 0;
        _ = NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
    }
}
