using System.Windows;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

/// <summary>
/// 设置窗口与主窗口之间的宿主契约：读取当前偏好、立即应用并异步持久化、
/// 提供自启管理与数据目录，以及触发主窗既有的保存退出序列（不得绕过冲刷语义）。
/// 1.16.0 起新增数据目录搬迁（预检 + 执行 + 自动重启）与插件目录偏好。
/// </summary>
public interface ISettingsHost
{
    Window HostWindow { get; }
    DesignTheme CurrentTheme { get; }
    AppPreferences CurrentPreferences { get; }
    IStartupManager StartupManager { get; }
    string DataDirectory { get; }
    PluginCatalog? PluginCatalog { get; }
    void ApplyPreferences(AppPreferences preferences);
    Task ApplyPluginEnabledAsync(string pluginId, bool enabled);
    bool TryApplyGlobalHotkeyPreference(bool enabled);
    void RequestApplicationExit();
    DataDirectoryChangePreview DescribeDataDirectoryChange(string targetParentDirectory);
    Task<DataDirectoryChangeResult> ChangeDataDirectoryAsync(
        string targetParentDirectory,
        IProgress<DataDirectoryCopyProgress>? progress = null,
        CancellationToken cancellationToken = default);
    string EffectivePluginsDirectory { get; }
    bool PluginsDirectoryIsDefault { get; }
    Task<string?> ApplyPluginsDirectoryAsync(string? directoryOverride);

    /// <summary>当前标签显示顺序（1.25.0）：设置页「标签顺序」节的渲染来源。</summary>
    IReadOnlyList<BoardCategory> CurrentDisplayOrder { get; }

    /// <summary>标签显示名（含用户自定义与迁移改名），设置页顺序列表按它呈现。</summary>
    string CategoryDisplayName(BoardCategory category);

    /// <summary>
    /// 采纳新的标签显示顺序：立即重排面板标签轨并异步原子持久化到 settings.json；
    /// 持久化失败经状态条提示并恢复原顺序。
    /// </summary>
    Task ApplyCategoryOrderAsync(IReadOnlyList<BoardCategory> displayOrder);
}
