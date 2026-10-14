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
    /// 机密保护器（1.27.0）：设置页「AI 设置」节读写 DeepSeek API Key 密文时使用；
    /// 明文只在内存中出现，持久化侧只保存密文。
    /// </summary>
    ISecretProtector SecretProtector { get; }

    /// <summary>
    /// DeepSeek 连接测试（1.27.0）：以传入的 key 发起最小 chat completion（模型恒为
    /// DeepSeekClient.DefaultModel，按后台任务惯例追踪，退出冲刷会等待），结果含
    /// 就地反馈的中文文案。
    /// </summary>
    Task<DeepSeekConnectionTestResult> TestDeepSeekConnectionAsync(
        string apiKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 采纳新的标签显示顺序（乐观提交契约）：同步应用内存与显示顺序（首个 await
    /// 之前生效，调用方拿到任务时面板标签轨已是目标序）；返回任务完成＝
    /// settings.json 原子持久化完成；持久化失败回滚内存与显示顺序并经状态条提示。
    /// </summary>
    Task ApplyCategoryOrderAsync(IReadOnlyList<BoardCategory> displayOrder);
}
