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

/// <summary>
/// 插件目录与启用状态：扫描安装目录内建插件与数据目录用户插件，
/// 启用状态原子持久化到数据目录 plugins-state.json，默认全部禁用。
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
    private volatile TextCleanerPipeline _textCleaner = TextCleanerPipeline.Empty;

    public PluginCatalog(AppPaths paths, string? builtInPluginsDirectory, IAtomicTextWriter writer)
    {
        _userPluginsDirectory = paths.PluginsDirectory;
        _builtInPluginsDirectory = builtInPluginsDirectory;
        _stateFile = paths.PluginStateFile;
        _writer = writer;
    }

    /// <summary>当前活动的文本清理管线快照；采集线程只读，替换时整体换引用。</summary>
    public TextCleanerPipeline TextCleaner => _textCleaner;

    public IReadOnlyList<PluginEntry> Entries => _entries;

    public string UserPluginsDirectory => _userPluginsDirectory;

    /// <summary>
    /// 扫描插件目录并载入启用状态。目录不存在视为"没有插件"，
    /// 状态文件损坏时回退为全部禁用，不阻塞启动。
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var entriesById = new Dictionary<string, PluginEntry>(StringComparer.Ordinal);
        var orderedEntries = new List<PluginEntry>();

        CollectDirectory(_builtInPluginsDirectory, PluginOrigin.BuiltIn, entriesById, orderedEntries);
        CollectDirectory(_userPluginsDirectory, PluginOrigin.User, entriesById, orderedEntries);

        // 库代码统一 ConfigureAwait(false)：调用方（含测试宿主）阻塞等待时不会死锁。
        _enabledIds = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        _enabledIds.RemoveWhere(id => !entriesById.ContainsKey(id));

        _entries = orderedEntries
            .Where(entry => entry.Manifest is not null || entry.LoadError is not null)
            .Select(entry => entry with { Enabled = entry.Manifest is not null && _enabledIds.Contains(entry.Id) })
            .OrderBy(entry => entry.Origin)
            .ThenBy(entry => entry.Id, StringComparer.Ordinal)
            .ToList();
        RebuildTextCleaner();
    }

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

        await _stateWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(
                () => _writer.WriteAsync(
                    _stateFile,
                    JsonSerializer.Serialize(new PluginStateSnapshot(updated), StateJsonOptions),
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stateWriteGate.Release();
        }
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

    private async Task<HashSet<string>> LoadStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_stateFile))
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            var json = await File.ReadAllTextAsync(_stateFile, cancellationToken).ConfigureAwait(false);
            var snapshot = JsonSerializer.Deserialize<PluginStateSnapshot>(json, StateJsonOptions);
            return snapshot?.Enabled is { Count: > 0 } enabled
                ? new HashSet<string>(enabled.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
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

    private sealed record PluginStateSnapshot(HashSet<string> Enabled);
}
