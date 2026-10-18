using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public enum PluginOrigin
{
    BuiltIn,
    User
}

/// <summary>
/// 一个插件目录的加载结果。清单非法时 Manifest 为空、LoadError 说明原因，
/// 仍保留在列表中让设置界面可以解释"为什么我的插件没有生效"。
/// </summary>
public sealed record PluginEntry(
    string Directory,
    PluginOrigin Origin,
    PluginManifest? Manifest,
    string? LoadError,
    bool Enabled)
{
    public string Id => Manifest?.Id ?? System.IO.Path.GetFileName(Directory);
}

/// <summary>已启用的动作插件条目：清单动作定义＋按当前设置值物化的执行管线快照。</summary>
public sealed record PluginActionHandle(
    PluginEntry Entry,
    PluginActionDefinition Action,
    PluginActionPipeline Pipeline)
{
    public string PluginId => Entry.Id;
    public string PluginName => Entry.Manifest!.Name;
}

/// <summary>
/// 插件目录与启用状态：扫描安装目录内建插件与数据目录用户插件，
/// 启用状态与动作设置值原子持久化到数据目录 plugins-state.json，默认全部禁用。
/// 用户目录中同 id 的插件覆盖内建版本，便于定制而不改安装目录。
/// </summary>
public sealed class PluginCatalog
{
    public const string ManifestFileName = "plugin.json";

    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _userPluginsDirectory;
    private readonly string? _builtInPluginsDirectory;
    private readonly string _stateFile;
    private readonly IAtomicTextWriter _writer;
    private readonly SemaphoreSlim _stateWriteGate = new(1, 1);

    private IReadOnlyList<PluginEntry> _entries = [];
    private HashSet<string> _enabledIds = new(StringComparer.Ordinal);

    // pluginId → actionId → key → 值（bool 以 "true"/"false" 字符串归一存储）。
    private Dictionary<string, Dictionary<string, Dictionary<string, string>>> _actionSettings = new(StringComparer.Ordinal);

    private volatile TextCleanerPipeline _textCleaner = TextCleanerPipeline.Empty;
    private volatile IReadOnlyList<PluginActionHandle> _enabledActions = [];

    public PluginCatalog(AppPaths paths, string? builtInPluginsDirectory, IAtomicTextWriter writer)
    {
        _userPluginsDirectory = paths.PluginsDirectory;
        _builtInPluginsDirectory = builtInPluginsDirectory;
        _stateFile = paths.PluginStateFile;
        _writer = writer;
    }

    /// <summary>当前活动的文本清理管线快照；采集线程只读，替换时整体换引用。</summary>
    public TextCleanerPipeline TextCleaner => _textCleaner;

    /// <summary>当前可用（启用且管线非空）的动作快照；启停/设置变化即整体换引用。</summary>
    public IReadOnlyList<PluginActionHandle> EnabledActions => _enabledActions;

    public IReadOnlyList<PluginEntry> Entries => _entries;

    public string UserPluginsDirectory => _userPluginsDirectory;

    /// <summary>
    /// 扫描插件目录并载入启用状态与动作设置。目录不存在视为"没有插件"，
    /// 状态文件损坏时回退为全部禁用，不阻塞启动；旧格式状态文件（只有 enabled）
    /// 正常加载，动作设置按清单声明补默认值。
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var entriesById = new Dictionary<string, PluginEntry>(StringComparer.Ordinal);
        var orderedEntries = new List<PluginEntry>();

        CollectDirectory(_builtInPluginsDirectory, PluginOrigin.BuiltIn, entriesById, orderedEntries);
        CollectDirectory(_userPluginsDirectory, PluginOrigin.User, entriesById, orderedEntries);

        // 库代码统一 ConfigureAwait(false)：调用方（含测试宿主）阻塞等待时不会死锁。
        var (enabled, actionSettings) = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        enabled.RemoveWhere(id => !entriesById.ContainsKey(id));
        _enabledIds = enabled;
        _actionSettings = actionSettings;

        _entries = orderedEntries
            .Where(entry => entry.Manifest is not null || entry.LoadError is not null)
            .Select(entry => entry with { Enabled = entry.Manifest is not null && _enabledIds.Contains(entry.Id) })
            .OrderBy(entry => entry.Origin)
            .ThenBy(entry => entry.Id, StringComparer.Ordinal)
            .ToList();
        RebuildTextCleaner();
        RebuildActionPipelines();
    }

    /// <summary>
    /// 重新扫描插件目录（设置区「重新扫描」入口）：重跑发现与状态载入并重建
    /// 全部管线快照；非法清单仍按「可见不可启用」保留并给出原因。
    /// </summary>
    public async Task RescanAsync(CancellationToken cancellationToken = default) => await LoadAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 启用或禁用一个插件：先更新内存并重建管线，再原子持久化；
    /// 持久化失败时抛 IOException/UnauthorizedAccessException，内存状态保持本次修改。
    /// </summary>
    public async Task SetEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        var entry = _entries.FirstOrDefault(candidate => candidate.Id == pluginId && candidate.Manifest is not null)
            ?? throw new KeyNotFoundException($"未发现可用的插件 {pluginId}。");

        var updated = new HashSet<string>(_enabledIds, StringComparer.Ordinal);
        if (enabled)
        {
            updated.Add(pluginId);
        }
        else
        {
            updated.Remove(pluginId);
        }

        _enabledIds = updated;
        _entries = _entries.Select(candidate =>
            candidate.Id == pluginId ? candidate with { Enabled = enabled } : candidate).ToList();
        RebuildTextCleaner();
        RebuildActionPipelines();

        await _stateWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(
                () => _writer.WriteAsync(
                    _stateFile,
                    JsonSerializer.Serialize(CreateStateSnapshot(), StateJsonOptions),
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stateWriteGate.Release();
        }
    }

    /// <summary>
    /// 写入一个动作设置值：更新内存、重建该插件动作管线（即时生效）并原子持久化；
    /// 持久化失败时抛 IOException/UnauthorizedAccessException，内存状态保持本次修改。
    /// 值只落已知插件/动作/声明 key，孤儿条目在保存时剔除。
    /// </summary>
    public async Task SetActionSettingValueAsync(
        string pluginId,
        string actionId,
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var entry = _entries.FirstOrDefault(candidate => candidate.Id == pluginId && candidate.Manifest is not null)
            ?? throw new KeyNotFoundException($"未发现可用的插件 {pluginId}。");
        var action = entry.Manifest!.Actions!.FirstOrDefault(candidate => candidate.Id == actionId)
            ?? throw new KeyNotFoundException($"插件 {pluginId} 未声明动作 {actionId}。");
        if (action.Settings.All(candidate => candidate.Key != key))
        {
            throw new KeyNotFoundException($"动作 {actionId} 未声明设置 {key}。");
        }

        if (!_actionSettings.TryGetValue(pluginId, out var byAction))
        {
            byAction = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            _actionSettings[pluginId] = byAction;
        }

        if (!byAction.TryGetValue(actionId, out var byKey))
        {
            byKey = new Dictionary<string, string>(StringComparer.Ordinal);
            byAction[actionId] = byKey;
        }

        byKey[key] = value;
        RebuildActionPipelines();

        await _stateWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(
                () => _writer.WriteAsync(
                    _stateFile,
                    JsonSerializer.Serialize(CreateStateSnapshot(), StateJsonOptions),
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stateWriteGate.Release();
        }
    }

    /// <summary>
    /// 解析某动作设置项的当前生效值（用户覆盖优先，缺省回落清单默认；
    /// 未知插件/动作返回声明默认值口径的空字典——UI 侧直接走 ResolveSettingValue）。
    /// </summary>
    public IReadOnlyDictionary<string, string> GetActionSettingOverrides(string pluginId, string actionId) =>
        _actionSettings.TryGetValue(pluginId, out var byAction) &&
        byAction.TryGetValue(actionId, out var byKey)
            ? byKey
            : new Dictionary<string, string>();

    /// <summary>
    /// 按清单动作与当前设置值构建执行管线（试运行复用正式加载器口径；每次调用
    /// 现算，反映最新设置）。返回 null = 该动作全部规则被 when 门控关闭。
    /// </summary>
    public PluginActionPipeline? TryBuildActionPipeline(string pluginId, string actionId)
    {
        var entry = _entries.FirstOrDefault(candidate => candidate.Id == pluginId && candidate.Manifest is not null);
        var action = entry?.Manifest?.Actions?.FirstOrDefault(candidate => candidate.Id == actionId);
        return action is null
            ? null
            : PluginActionPipeline.TryBuild(action, GetActionSettingOverrides(pluginId, actionId));
    }

    private PluginStateSnapshot CreateStateSnapshot()
    {
        // 只保存仍与当前清单匹配的设置值（孤儿剔除），bool 值以 "true"/"false" 归一。
        var persisted = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.Ordinal);
        foreach (var entry in _entries)
        {
            if (entry.Manifest?.Kind != PluginKind.TextActions ||
                !_actionSettings.TryGetValue(entry.Id, out var byAction))
            {
                continue;
            }

            var persistedActions = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var action in entry.Manifest.Actions!)
            {
                if (!byAction.TryGetValue(action.Id, out var byKey))
                {
                    continue;
                }

                var persistedValues = byKey
                    .Where(pair => action.Settings.Any(setting => setting.Key == pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                if (persistedValues.Count > 0)
                {
                    persistedActions[action.Id] = persistedValues;
                }
            }

            if (persistedActions.Count > 0)
            {
                persisted[entry.Id] = persistedActions;
            }
        }

        return new PluginStateSnapshot(new HashSet<string>(_enabledIds, StringComparer.Ordinal), persisted);
    }

    private void CollectDirectory(
        string? directory,
        PluginOrigin origin,
        Dictionary<string, PluginEntry> entriesById,
        List<PluginEntry> orderedEntries)
    {
        if (directory is null || !Directory.Exists(directory))
        {
            return;
        }

        foreach (var pluginDirectory in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
        {
            var manifestPath = Path.Combine(pluginDirectory, ManifestFileName);
            PluginManifest? manifest = null;
            var loadError = string.Empty;
            try
            {
                if (File.Exists(manifestPath))
                {
                    var json = File.ReadAllText(manifestPath);
                    if (!PluginManifest.TryParse(json, out manifest, out loadError))
                    {
                        manifest = null;
                    }
                }
                else
                {
                    loadError = $"目录中缺少 {ManifestFileName}。";
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException)
            {
                manifest = null;
                loadError = $"无法读取 {ManifestFileName}。";
            }

            var entry = new PluginEntry(
                pluginDirectory,
                origin,
                manifest,
                manifest is null ? loadError : null,
                Enabled: false);

            var id = entry.Id;
            if (entriesById.TryGetValue(id, out var existing))
            {
                if (origin == PluginOrigin.User && existing.Origin == PluginOrigin.BuiltIn)
                {
                    // 用户目录中的同 id 插件覆盖内建版本，便于定制而不改安装目录。
                    entriesById[id] = entry;
                    orderedEntries.Remove(existing);
                    orderedEntries.Add(entry);
                }

                continue;
            }

            entriesById.Add(id, entry);
            orderedEntries.Add(entry);
        }
    }

    private async Task<(HashSet<string> Enabled, Dictionary<string, Dictionary<string, Dictionary<string, string>>> ActionSettings)>
        LoadStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_stateFile))
            {
                return (new HashSet<string>(StringComparer.Ordinal), new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.Ordinal));
            }

            var json = await File.ReadAllTextAsync(_stateFile, cancellationToken).ConfigureAwait(false);
            var snapshot = JsonSerializer.Deserialize<PluginStateSnapshot>(json, StateJsonOptions);
            var enabled = snapshot?.Enabled is { Count: > 0 } ids
                ? new HashSet<string>(ids.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            var settings = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.Ordinal);
            if (snapshot?.ActionSettings is not null)
            {
                foreach (var (pluginId, byAction) in snapshot.ActionSettings)
                {
                    if (string.IsNullOrWhiteSpace(pluginId) || byAction is null)
                    {
                        continue;
                    }

                    var persistedActions = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                    foreach (var (actionId, byKey) in byAction)
                    {
                        if (string.IsNullOrWhiteSpace(actionId) || byKey is null)
                        {
                            continue;
                        }

                        persistedActions[actionId] = byKey
                            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
                            .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal);
                    }

                    settings[pluginId] = persistedActions;
                }
            }

            return (enabled, settings);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return (new HashSet<string>(StringComparer.Ordinal), new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.Ordinal));
        }
    }

    private void RebuildTextCleaner()
    {
        var rules = new List<(Regex Pattern, string Replacement)>();
        foreach (var entry in _entries)
        {
            if (!entry.Enabled || entry.Manifest is not { Kind: PluginKind.TextCleaner } manifest)
            {
                continue;
            }

            foreach (var rule in manifest.TextRules)
            {
                // Multiline 让 ^/$ 按行工作——文本整理规则的常规预期；
                // 平台默认，写入插件文档，作者无需逐条声明。
                rules.Add((
                    new Regex(
                        rule.Pattern,
                        RegexOptions.CultureInvariant | RegexOptions.Multiline,
                        PluginManifest.RuleMatchTimeout),
                    rule.Replacement));
            }
        }

        _textCleaner = new TextCleanerPipeline(rules);
    }

    /// <summary>重建启用动作的管线快照：按当前设置值物化 when 门控；
    /// 全部规则被门控关闭的动作不进入可用列表（菜单可见性由 UI 据此判定）。</summary>
    private void RebuildActionPipelines()
    {
        var handles = new List<PluginActionHandle>();
        foreach (var entry in _entries)
        {
            if (!entry.Enabled || entry.Manifest is not { Kind: PluginKind.TextActions } manifest)
            {
                continue;
            }

            foreach (var action in manifest.Actions!)
            {
                var overrides = GetActionSettingOverrides(entry.Id, action.Id);
                if (PluginActionPipeline.TryBuild(action, overrides) is not { } pipeline)
                {
                    continue;
                }

                handles.Add(new PluginActionHandle(entry, action, pipeline));
            }
        }

        _enabledActions = handles;
    }

    private sealed record PluginStateSnapshot(
        HashSet<string> Enabled,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>>? ActionSettings = null);
}
