using System.Windows;
using Microsoft.Win32;

namespace FloatingTransferStation.Services;

public enum DesignTheme
{
    Light,
    Dark,
}

/// <summary>
/// 设计主题（浅色/深色）管理：整本替换窗口级主题资源字典实现切换，
/// 启动时按系统应用亮暗偏好初始化，并跟随 WM_SETTINGCHANGE 的系统主题变化。
/// </summary>
public static class DesignThemeManager
{
    private const string PersonalizeKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>仅供本机预览截图取证强制主题；生产路径保持 null（跟随系统）。</summary>
    public static DesignTheme? PreviewOverride { get; set; }

    public static void Apply(Window window, DesignTheme theme)
    {
        // 相对 pack URI（/程序集;component/…）显式指向本程序集内嵌资源，
        // 与入口程序集（应用或测试宿主）无关。
        var assemblyName = typeof(DesignThemeManager).Assembly.GetName().Name;
        var source = new Uri(
            $"/{assemblyName};component/Resources/DesignTheme.{(theme == DesignTheme.Dark ? "Dark" : "Light")}.xaml",
            UriKind.Relative);
        for (var index = 0; index < window.Resources.MergedDictionaries.Count; index++)
        {
            var original = window.Resources.MergedDictionaries[index].Source?.OriginalString;
            if (original is null ||
                !original.Replace('\\', '/').Contains("DesignTheme.", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(
                    original.Replace('\\', '/'),
                    source.OriginalString,
                    StringComparison.Ordinal))
            {
                return;
            }

            window.Resources.MergedDictionaries[index] = new ResourceDictionary { Source = source };
            return;
        }
    }

    public static DesignTheme DetectSystemTheme()
    {
        if (PreviewOverride is { } preview)
        {
            return preview;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            if (key?.GetValue("AppsUseLightTheme") is int value)
            {
                return value == 0 ? DesignTheme.Dark : DesignTheme.Light;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 读不到偏好时保持浅色默认。
        }

        return DesignTheme.Light;
    }
}
