using System.Text.Json;
using System.Text.Json.Serialization;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public sealed partial class LocalStore
{
    private readonly SemaphoreSlim _launcherDiscoveriesWriteGate = new(1, 1);
    private JsonSerializerOptions? _launcherDiscoveryJsonOptions;

    private JsonSerializerOptions LauncherDiscoveryJsonOptions
    {
        get
        {
            if (_launcherDiscoveryJsonOptions is null)
            {
                var options = new JsonSerializerOptions(_jsonOptions);
                options.Converters.Add(new LauncherDiscoveryEntryJsonConverter());
                _launcherDiscoveryJsonOptions = options;
            }

            return _launcherDiscoveryJsonOptions;
        }
    }

    // ILauncherStore.LoadAsync(CancellationToken) 已占用该签名（返回 launchers 信封）；
    // 发现清单走独立方法名 + 显式接口实现（C# 不允许仅返回类型不同的重载）。
    Task<LauncherDiscoveryStoreData> ILauncherDiscoveryStore.LoadAsync(CancellationToken cancellationToken) =>
        LoadLauncherDiscoveriesAsync(cancellationToken);

    Task ILauncherDiscoveryStore.SaveAsync(LauncherDiscoveryStoreData data, CancellationToken cancellationToken) =>
        SaveLauncherDiscoveriesAsync(data, cancellationToken);

    /// <summary>
    /// 读取 launcher-discoveries.json（#45）：损坏文件 → .corrupt-* 快照 + .bak 回落
    /// （照 board.json/launchers.json）；清单是重扫可重建的派生数据，未知版本回落
    /// 空清单（不存在只读停用——与 launchers.json 的用户数据口径不同）。读取端
    /// 逐条清洗：未知 Kind/空目标丢条、pending 按 DedupKey 去重、dismissed 键去重。
    /// </summary>
    public async Task<LauncherDiscoveryStoreData> LoadLauncherDiscoveriesAsync(
        CancellationToken cancellationToken = default)
    {
        var data = await LoadWithBackupAsync(
            _paths.LauncherDiscoveriesFile,
            () => LauncherDiscoveryStoreData.Empty,
            cancellationToken,
            LauncherDiscoveryJsonOptions).ConfigureAwait(false);
        if (data.Version != LauncherDiscoveryStoreData.CurrentVersion)
        {
            return LauncherDiscoveryStoreData.Empty;
        }

        var pending = new List<LauncherDiscoveryEntry>(data.Pending.Count);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in data.Pending)
        {
            if (entry is null ||
                !Enum.IsDefined(entry.Kind) ||
                string.IsNullOrWhiteSpace(entry.TargetPath) ||
                !seenKeys.Add(LauncherCatalog.DedupKeyFor(entry.TargetPath, entry.Arguments)))
            {
                continue;
            }

            pending.Add(entry);
        }

        var dismissed = data.DismissedKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new LauncherDiscoveryStoreData(LauncherDiscoveryStoreData.CurrentVersion, pending, dismissed);
    }

    public async Task SaveLauncherDiscoveriesAsync(
        LauncherDiscoveryStoreData data,
        CancellationToken cancellationToken = default)
    {
        await _launcherDiscoveriesWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(
                () => _writer.WriteAsync(
                    _paths.LauncherDiscoveriesFile,
                    JsonSerializer.Serialize(
                        data with { Version = LauncherDiscoveryStoreData.CurrentVersion },
                        LauncherDiscoveryJsonOptions),
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _launcherDiscoveriesWriteGate.Release();
        }
    }

    /// <summary>
    /// 发现条目逐条转换器（照 LauncherItemJsonConverter 先例）：读取端宽容——
    /// 未知 Kind / 空目标 → null（列表层过滤丢条），其余成员缺失回落默认值；
    /// 单条非法不毒化整份文件（dismissed 是持久的抑制记忆，整份回落会丢记忆）。
    /// 写入端钉死 camelCase 形态。
    /// </summary>
    private sealed class LauncherDiscoveryEntryJsonConverter : JsonConverter<LauncherDiscoveryEntry>
    {
        public override LauncherDiscoveryEntry? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (!root.TryGetProperty("kind", out var kindElement) ||
                !Enum.TryParse(kindElement.GetString(), out LauncherKind kind) ||
                !Enum.IsDefined(kind))
            {
                return null;
            }

            if (!root.TryGetProperty("targetPath", out var targetElement) ||
                string.IsNullOrWhiteSpace(targetElement.GetString()))
            {
                return null;
            }

            var displayName = root.TryGetProperty("displayName", out var nameElement)
                ? nameElement.GetString()
                : null;
            var arguments = root.TryGetProperty("arguments", out var argumentsElement) &&
                argumentsElement.ValueKind == JsonValueKind.String
                ? argumentsElement.GetString()
                : null;
            var firstSeen = root.TryGetProperty("firstSeenAtUtc", out var seenElement) &&
                DateTimeOffset.TryParse(seenElement.GetString(), out var parsed)
                ? parsed
                : DateTimeOffset.UnixEpoch;

            return new LauncherDiscoveryEntry(
                kind,
                targetElement.GetString()!,
                arguments,
                displayName ?? string.Empty,
                firstSeen);
        }

        public override void Write(Utf8JsonWriter writer, LauncherDiscoveryEntry value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", value.Kind.ToString());
            writer.WriteString("targetPath", value.TargetPath);
            if (value.Arguments is not null)
            {
                writer.WriteString("arguments", value.Arguments);
            }

            writer.WriteString("displayName", value.DisplayName);
            writer.WriteString("firstSeenAtUtc", value.FirstSeenAtUtc);
            writer.WriteEndObject();
        }
    }
}
