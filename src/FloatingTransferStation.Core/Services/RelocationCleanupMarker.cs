using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FloatingTransferStation.Services;

/// <summary>
/// 应用内数据目录搬迁的延迟清理标记：发布并登记新目录后，在新 Data 目录写入该标记
/// 记录旧受管数据目录；下一次在新目录成功启动（拿到单实例锁）后按形状守卫删除旧目录
/// 并清掉标记。禁止立即删旧（崩溃窗口丢数据），也禁止永久保留（迁回旧位置会被残留
/// 目录卡死）。损坏/缺失按"无事可做"处理，旧目录保留并提示用户手动处理。
/// </summary>
public static class RelocationCleanupMarker
{
    public const string FileName = ".fts-relocation-cleanup.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private sealed record MarkerDocument(
        [property: JsonPropertyName("oldDataDirectory")] string OldDataDirectory);

    public static async Task<bool> TryWriteAsync(
        string dataDirectory,
        string oldDataDirectory,
        IAtomicTextWriter writer,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var path = Path.Combine(dataDirectory, FileName);
            await writer.WriteAsync(
                path,
                JsonSerializer.Serialize(new MarkerDocument(oldDataDirectory), JsonOptions),
                cancellationToken).ConfigureAwait(false);
            return File.Exists(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>读取旧受管数据目录；标记不存在或损坏返回 false。</summary>
    public static bool TryRead(string? dataDirectory, [NotNullWhen(true)] out string? oldDataDirectory)
    {
        oldDataDirectory = null;
        if (dataDirectory is null)
        {
            return false;
        }

        try
        {
            var path = Path.Combine(dataDirectory, FileName);
            if (!File.Exists(path))
            {
                return false;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var document = JsonSerializer.Deserialize<MarkerDocument>(stream, JsonOptions);
            if (document?.OldDataDirectory is not { Length: > 0 } candidate)
            {
                return false;
            }

            oldDataDirectory = candidate;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    public static void TryDelete(string dataDirectory)
    {
        try
        {
            File.Delete(Path.Combine(dataDirectory, FileName));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 清不掉的标记下次启动再试；旧目录此刻已删，重试不会重复删除任何内容。
        }
    }
}
