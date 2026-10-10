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

    public IReadOnlyList<BoardCategory> CurrentDisplayOrder =>
        _viewModel.Categories.Select(panel => panel.Category).ToArray();

    public string CategoryDisplayName(BoardCategory category) => _settings.CategoryName(category);

    /// <summary>
    /// 采纳新的标签显示顺序：设置窗口「标签顺序」节提交（拖拽/上移下移）即重排
    /// 面板标签轨并走分类改名同一条 settings.json 原子保存链路（契约 #5 设置改动
    /// 即时生效并自动保存）；保存失败恢复内存原顺序并经状态条提示。
    /// </summary>
    public async Task ApplyCategoryOrderAsync(IReadOnlyList<BoardCategory> displayOrder)
    {
        ArgumentNullException.ThrowIfNull(displayOrder);
        var operation = ApplyCategoryOrderCoreAsync(displayOrder);
        TrackPendingOperation(operation);
        await operation;
    }

    private async Task ApplyCategoryOrderCoreAsync(IReadOnlyList<BoardCategory> displayOrder)
    {
        await _settingsSaveGate.WaitAsync();
        try
        {
            var originalOrder = _settings.CategoryOrder;
            _settings = _settings.WithCategoryOrder(displayOrder);
            try
            {
                await _store.SaveSettingsAsync(_settings);
                _viewModel.ApplyCategoryOrder(_settings.DisplayOrder);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _settings = _settings with { CategoryOrder = originalOrder };
                ShowStatus("标签顺序暂未保存。");
            }
        }
        finally
        {
            _settingsSaveGate.Release();
        }
    }

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

    /// <summary>立即应用偏好（主题/动效/全局快捷键/复制手势/垃圾桶行为）并异步原子持久化；持久化失败经状态条提示。</summary>
    public void ApplyPreferences(AppPreferences preferences)
    {
        _preferences = preferences;
        ApplyThemePreference(preferences.ThemeMode);
        ApplyAnimationsPreference(preferences.AnimationsEnabled);
        TrySetGlobalHotkey(preferences.GlobalHotkeyEnabled);
        UpdateDeleteButtonLabel();
        TrackPendingOperation(PersistPreferencesAsync(preferences));
    }

    /// <summary>
    /// 设置窗口的全局快捷键开关：开启时先注册系统热键，成功才落偏好并持久化；
    /// 关闭总是成功并立即注销。失败返回 false，由设置窗口提示并回显实际状态。
    /// </summary>
    public bool TryApplyGlobalHotkeyPreference(bool enabled)
    {
        if (!TrySetGlobalHotkey(enabled))
        {
            return false;
        }

        ApplyPreferences(_preferences with { GlobalHotkeyEnabled = enabled });
        return true;
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
