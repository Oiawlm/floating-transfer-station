using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public sealed partial class LocalStore
{
    private readonly SemaphoreSlim _launchersWriteGate = new(1, 1);
    private JsonSerializerOptions? _launcherJsonOptions;

    private JsonSerializerOptions LauncherJsonOptions
    {
        get
        {
            if (_launcherJsonOptions is null)
            {
                var options = new JsonSerializerOptions(_jsonOptions);
                options.Converters.Add(new LauncherItemJsonConverter());
                _launcherJsonOptions = options;
            }

            return _launcherJsonOptions;
        }
    }

    /// <summary>
    /// 读取 launchers.json（#30）：损坏文件 → .corrupt-* 快照 + .bak 回落（照 board.json）；
    /// 逐条清洗在转换器与列表层完成——未知 Kind 数值/字符串丢条、目标空或无效丢条、
    /// 重复 Id 取首条。版本高于当前时原样返回，由目录层进入只读停用（保持原文件，
    /// 绝不以空集合覆盖回写）。
    /// </summary>
    public async Task<LauncherStoreData> LoadAsync(CancellationToken cancellationToken = default)
    {
        var data = await LoadWithBackupAsync(
            _paths.LaunchersFile,
            () => LauncherStoreData.Empty,
            cancellationToken,
            LauncherJsonOptions);

        var cleaned = new List<LauncherItem>(data.Items.Count);
        var seenIds = new HashSet<Guid>();
        foreach (var item in data.Items)
        {
            if (item is null ||
                !seenIds.Add(item.Id) ||
                string.IsNullOrWhiteSpace(LauncherIconKey.NormalizeTarget(item.TargetPath)))
            {
                continue;
            }

            cleaned.Add(item);
        }

        return new LauncherStoreData(data.Version, cleaned);
    }

    public async Task SaveAsync(LauncherStoreData data, CancellationToken cancellationToken = default)
    {
        await _launchersWriteGate.WaitAsync(cancellationToken);
        try
        {
            await Task.Run(
                () => _writer.WriteAsync(
                    _paths.LaunchersFile,
                    JsonSerializer.Serialize(
                        data with { Version = LauncherStoreData.CurrentVersion },
                        LauncherJsonOptions),
                    cancellationToken),
                cancellationToken);
        }
        finally
        {
            _launchersWriteGate.Release();
        }
    }

    /// <summary>
    /// 启动项逐条转换器：读取端宽容（未知 Kind / 无效 Id / 空目标 → null，列表层过滤丢条，
    /// 其余成员缺失回落默认值）；写入端钉死 camelCase 形态与 Guid "N" 格式（#30 存储契约）。
    /// </summary>
    private sealed class LauncherItemJsonConverter : JsonConverter<LauncherItem>
    {
        public override LauncherItem? Read(
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
            if (!root.TryGetProperty("id", out var idElement) ||
                !Guid.TryParse(idElement.GetString(), out var id) ||
                id == Guid.Empty)
            {
                return null;
            }

            if (!root.TryGetProperty("kind", out var kindElement) ||
                !Enum.TryParse(kindElement.GetString(), out LauncherKind kind) ||
                !Enum.IsDefined(kind))
            {
                return null;
            }

            var targetPath = root.TryGetProperty("targetPath", out var targetElement)
                ? targetElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return null;
            }

            var displayName = root.TryGetProperty("displayName", out var nameElement)
                ? nameElement.GetString()
                : null;
            var createdAt = root.TryGetProperty("createdAt", out var createdElement) &&
                DateTimeOffset.TryParse(
                    createdElement.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed)
                ? parsed
                : DateTimeOffset.UnixEpoch;
            var arguments = root.TryGetProperty("arguments", out var argumentsElement) &&
                argumentsElement.ValueKind == JsonValueKind.String
                ? argumentsElement.GetString()
                : null;
            var workingDirectory = root.TryGetProperty("workingDirectory", out var workingDirectoryElement) &&
                workingDirectoryElement.ValueKind == JsonValueKind.String
                ? workingDirectoryElement.GetString()
                : null;
            var iconStamp = root.TryGetProperty("iconStamp", out var stampElement) &&
                stampElement.ValueKind == JsonValueKind.String
                ? stampElement.GetString()
                : null;

            return new LauncherItem(
                id,
                kind,
                targetPath,
                displayName ?? string.Empty,
                createdAt,
                arguments,
                workingDirectory,
                iconStamp);
        }

        public override void Write(Utf8JsonWriter writer, LauncherItem value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("id", value.Id.ToString("N"));
            writer.WriteString("kind", value.Kind.ToString());
            writer.WriteString("targetPath", value.TargetPath);
            if (value.Arguments is not null)
            {
                writer.WriteString("arguments", value.Arguments);
            }

            if (value.WorkingDirectory is not null)
            {
                writer.WriteString("workingDirectory", value.WorkingDirectory);
            }

            writer.WriteString("displayName", value.DisplayName);
            writer.WriteString("createdAt", value.CreatedAt);
            if (value.IconStamp is not null)
            {
                writer.WriteString("iconStamp", value.IconStamp);
            }

            writer.WriteEndObject();
        }
    }
}
