using System.Windows;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Views;

public partial class MainWindow : Window, ISettingsHost
{
    public Window HostWindow => this;

    public DesignTheme CurrentTheme => _activeDesignTheme;

    public AppPreferences CurrentPreferences => _preferences;

    public IStartupManager StartupManager => _startupManager;

    public string DataDirectory => _dataDirectory;

    public PluginCatalog? PluginCatalog => _pluginCatalog;

    /// <summary>
    /// 启用/禁用插件：立即重建采集管线并异步原子持久化；
    /// 持久化失败经状态条提示，界面开关状态保持本次选择。
    /// </summary>
    public async Task ApplyPluginEnabledAsync(string pluginId, bool enabled)
    {
        if (_pluginCatalog is null)
        {
            return;
        }

        var operation = ApplyPluginEnabledCoreAsync(pluginId, enabled);
        TrackPendingOperation(operation);
        await operation;
    }

    private async Task ApplyPluginEnabledCoreAsync(string pluginId, bool enabled)
    {
        var catalog = _pluginCatalog;
        if (catalog is null)
        {
            return;
        }

        try
        {
            await catalog.SetEnabledAsync(pluginId, enabled);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowStatus("插件设置暂未保存。");
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        OpenSettings();
    }

    /// <summary>打开设置窗口；已打开时置前。窗口随主窗主题同步，主窗关闭时一并关闭。</summary>
    public void OpenSettings()
    {
        if (_settingsWindow is { } existing)
        {
            existing.Activate();
            return;
        }

        var settings = new SettingsWindow(this) { Owner = this };
        settings.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = settings;
        settings.Show();
    }

    /// <summary>立即应用偏好（主题/动效）并异步原子持久化；持久化失败经状态条提示。</summary>
    public void ApplyPreferences(AppPreferences preferences)
    {
        _preferences = preferences;
        ApplyThemePreference(preferences.ThemeMode);
        ApplyAnimationsPreference(preferences.AnimationsEnabled);
        TrackPendingOperation(PersistPreferencesAsync(preferences));
    }

    /// <summary>触发主窗既有 Closing 冲刷序列（复盘冲刷、看板与窗口设置保存），不得绕过。</summary>
    public void RequestApplicationExit() => Close();

    private static DesignTheme ResolveTheme(ThemePreference themeMode) =>
        DesignThemeManager.PreviewOverride is { } preview
            ? preview
            : themeMode switch
            {
                ThemePreference.Light => DesignTheme.Light,
                ThemePreference.Dark => DesignTheme.Dark,
                _ => DesignThemeManager.DetectSystemTheme()
            };

    private void ApplyThemePreference(ThemePreference themeMode)
    {
        var theme = ResolveTheme(themeMode);
        _activeDesignTheme = theme;
        DesignThemeManager.Apply(this, theme);
        DwmWindowEffects.UpdateImmersiveDarkMode(
            _windowSource?.Handle ?? 0,
            theme == DesignTheme.Dark);
        _settingsWindow?.ApplyTheme(theme);
    }

    // false = 本地覆盖系统 ClientAreaAnimation 键（复用减弱动效降级路径）；
    // true = 移除覆盖恢复跟随系统。不改任何动画实现。
    private void ApplyAnimationsPreference(bool animationsEnabled)
    {
        if (animationsEnabled)
        {
            Resources.Remove(SystemParameters.ClientAreaAnimationKey);
        }
        else
        {
            Resources[SystemParameters.ClientAreaAnimationKey] = false;
        }
    }

    private async Task PersistPreferencesAsync(AppPreferences preferences)
    {
        if (_preferencesStore is null)
        {
            return;
        }

        try
        {
            await _preferencesSaveGate.WaitAsync();
            try
            {
                await _preferencesStore.SavePreferencesAsync(preferences);
            }
            finally
            {
                _preferencesSaveGate.Release();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowStatus("设置暂未保存。");
        }
    }
}
