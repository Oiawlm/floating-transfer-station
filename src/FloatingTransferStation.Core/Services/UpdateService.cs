using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace FloatingTransferStation.Services;

/// <summary>更新流程失败类别；用户可读中文文案见各失败构造点（自动检查全程静默消费）。</summary>
public enum UpdateErrorKind
{
    None = 0,
    NetworkError,
    Timeout,
    RateLimited,
    ServerError,
    NotFound,
    BadResponse,
    AssetMissing,
    DownloadFailed,
    HashMismatch
}

/// <summary>GitHub Release 上的一个可下载资产。</summary>
public sealed record UpdateAsset(string Name, string DownloadUrl, long SizeBytes);

/// <summary>
/// 一次更新检查的结果：成功时 HasUpdate 表示是否有严格更新的正式版（AutoUpdateCheck 语义），
/// HasUpdate 且 Asset 缺失 = 新版存在但自动更新不可用（AssetMissing，附发布页走浏览器回退）；
/// 失败时 IsSuccess=false 并给出错误类别与中文消息。任何失败都不得以异常外泄。
/// </summary>
public sealed record UpdateCheckResult
{
    public bool IsSuccess { get; init; }
    public bool HasUpdate { get; init; }
    public string? LatestVersion { get; init; }
    public string? ReleaseUrl { get; init; }
    public UpdateAsset? Asset { get; init; }
    public UpdateAsset? Checksums { get; init; }
    public UpdateErrorKind ErrorKind { get; init; }
    public string? ErrorMessage { get; init; }

    public static UpdateCheckResult UpToDate() => new() { IsSuccess = true };

    public static UpdateCheckResult UpdateAvailable(
        string latestVersion,
        string? releaseUrl,
        UpdateAsset asset,
        UpdateAsset checksums) => new()
    {
        IsSuccess = true,
        HasUpdate = true,
        LatestVersion = latestVersion,
        ReleaseUrl = releaseUrl,
        Asset = asset,
        Checksums = checksums
    };

    public static UpdateCheckResult Fail(UpdateErrorKind kind, string message) => new()
    {
        IsSuccess = false,
        ErrorKind = kind,
        ErrorMessage = message
    };
}

/// <summary>
/// 一次更新下载的结果：成功时 InstallerPath 是已通过 SHA256 校验、可直接静默安装的
/// 本地安装包；失败时临时文件已清理，绝不返回未校验的路径。
/// </summary>
public sealed record UpdateDownloadResult
{
    public bool IsSuccess { get; init; }
    public string? InstallerPath { get; init; }
    public UpdateErrorKind ErrorKind { get; init; }
    public string? ErrorMessage { get; init; }

    public static UpdateDownloadResult Ok(string installerPath) => new()
    {
        IsSuccess = true,
        InstallerPath = installerPath
    };

    public static UpdateDownloadResult Fail(UpdateErrorKind kind, string message) => new()
    {
        ErrorKind = kind,
        ErrorMessage = message
    };
}

/// <summary>更新服务的界面侧契约：检查、下载校验、静默安装启动（界面只做接线，不写业务逻辑）。</summary>
public interface IUpdateService
{
    Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default);

    Task<UpdateDownloadResult> DownloadUpdateAsync(
        UpdateCheckResult check,
        string downloadDirectory,
        CancellationToken cancellationToken = default);

    bool TryStartSilentInstaller(string installerPath);
}

/// <summary>
/// GitHub Releases 更新服务：releases/latest 检查、严格大于的版本比较、精确名资产定位、
/// 下载 + SHA256 校验、静默安装器启动。无状态（检查与下载按调用独立），HttpClient 在实例内
/// 复用；测试可注入假 HttpMessageHandler 与进程启动器，全程不真联网、不真安装。
/// 铁律：下载内容只信 SHA256SUMS.txt 校验通过后的资产，校验失败即丢弃绝不执行；
/// 版本比较 tag 剥 v 前缀后 Version 严格大于才算更新（相等、解析失败一律无更新）；
/// 任何失败折叠为结果记录而非异常外泄（自动检查静默降级的机制基础）。
/// </summary>
public sealed class UpdateService : IUpdateService
{
    // 安装资产在 GitHub Release 上的稳定精确名（README 元数据测试锁定同名约定）；
    // SHA256SUMS.txt 是唯一信任锚（sha256sum 格式：<64位十六进制><空白><文件名>）。
    private const string InstallerAssetNamePrefix = "FloatingTransferStation-Setup-";
    private const string ChecksumsFileName = "SHA256SUMS.txt";
    private const string InstallerAssetNameSuffix = ".exe";
    private const string DownloadPartialSuffix = ".download";
    public const string SilentInstallArguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);
    // 检查是小响应（30s 兜底），下载是大安装包（给足 10 分钟；超时静默失败可重试）。
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);
    private const long MaxChecksumsBytes = 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly Func<ProcessStartInfo, bool>? _processStarter;

    public UpdateService()
        : this(new SocketsHttpHandler
        {
            ConnectTimeout = ConnectTimeout,
            // 长驻桌面进程：定期轮换池化连接，网络切换后不连到过期 DNS。
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // 不跟随重定向：更新源异动应原样暴露，不静默换源。
            AllowAutoRedirect = false
        })
    {
    }

    public UpdateService(HttpMessageHandler handler, Func<ProcessStartInfo, bool>? processStarter = null)
    {
        _processStarter = processStarter;
        _httpClient = new HttpClient(handler)
        {
            // 逐请求以链接 CTS 施加差异化的检查/下载预算；客户端级不设总闸。
            Timeout = Timeout.InfiniteTimeSpan
        };
        // GitHub API 硬性要求显式 User-Agent（.NET 默认不发，缺失直接 403）。
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"FloatingTransferStation/{ProductIdentity.Version}");
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    /// <summary>
    /// 版本比较唯一入口：tag 剥 v/V 前缀后与本地版本做 <see cref="Version"/> 严格大于比较；
    /// 相等、更旧、两段以上缺段按 Version 语义、垃圾 tag 与带预发布后缀的 tag 一律「无更新」。
    /// </summary>
    public static bool IsNewerVersion(string? remoteTag, string localVersion) =>
        TryNormalizeVersion(remoteTag, out var remote) &&
        Version.TryParse(localVersion, out var local) &&
        remote > local;

    /// <summary>
    /// 检查 GitHub 上的最新正式发布（releases/latest 自动排除草稿与预发布）：
    /// 成功且本地更旧时定位精确名安装资产与校验清单；资产或校验清单缺失返回
    /// AssetMissing（HasUpdate 仍为真，附发布页地址供浏览器回退）；
    /// 404/403/429/5xx/超时/坏 JSON/网络异常一律折叠为失败结果。
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ProductIdentity.GitHubLatestReleaseApiUrl);
            using var timeoutCts = new CancellationTokenSource(CheckTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCts.Token);
            using var response = await _httpClient
                .SendAsync(request, linkedCts.Token)
                .ConfigureAwait(false);
            var body = await response.Content
                .ReadAsStringAsync(linkedCts.Token)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? ParseRelease(body, ProductIdentity.Version)
                : MapCheckFailure(response.StatusCode);
        }
        catch (Exception exception) when (IsRequestFailure(exception, cancellationToken, out var kind))
        {
            return kind switch
            {
                UpdateErrorKind.Timeout => UpdateCheckResult.Fail(UpdateErrorKind.Timeout, "检查更新超时，请稍后再试。"),
                UpdateErrorKind.NetworkError => UpdateCheckResult.Fail(UpdateErrorKind.NetworkError, "无法连接更新服务，请检查网络。"),
                _ => UpdateCheckResult.Fail(kind, "检查更新失败。")
            };
        }
    }

    /// <summary>
    /// 下载并校验检查结果中的安装资产：精确名落盘到目标目录（先清理同版本旧文件与
    /// 半成品）→ 下载 SHA256SUMS.txt → 哈希比对 → 通过才改名就位；任何失败清理
    /// 临时文件，绝不返回未校验的路径。
    /// </summary>
    public async Task<UpdateDownloadResult> DownloadUpdateAsync(
        UpdateCheckResult check,
        string downloadDirectory,
        CancellationToken cancellationToken = default)
    {
        if (check is not
            {
                IsSuccess: true,
                HasUpdate: true,
                Asset: { } asset,
                Checksums: { } checksums
            })
        {
            return UpdateDownloadResult.Fail(UpdateErrorKind.DownloadFailed, "没有可下载的更新包，请重新检查更新。");
        }

        Directory.CreateDirectory(downloadDirectory);
        var targetPath = Path.Combine(downloadDirectory, asset.Name);
        var partialPath = targetPath + DownloadPartialSuffix;
        TryDelete(targetPath);
        TryDelete(partialPath);
        try
        {
            await DownloadToFileAsync(asset.DownloadUrl, partialPath, cancellationToken).ConfigureAwait(false);
            var checksumsText = await DownloadTextAsync(
                checksums.DownloadUrl,
                MaxChecksumsBytes,
                cancellationToken).ConfigureAwait(false);
            // 清单缺失条目与清单本身异常同判：拿不到可信校验值就绝不安装。
            var expectedHash = checksumsText is null ? null : ParseChecksumLine(checksumsText, asset.Name);
            var actualHash = await ComputeSha256Async(partialPath, cancellationToken).ConfigureAwait(false);
            if (expectedHash is null)
            {
                TryDelete(partialPath);
                return UpdateDownloadResult.Fail(
                    UpdateErrorKind.HashMismatch,
                    $"校验清单中缺少 {asset.Name} 的校验值，已放弃安装。");
            }

            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(partialPath);
                return UpdateDownloadResult.Fail(UpdateErrorKind.HashMismatch, "更新包校验失败，已放弃安装。");
            }

            File.Move(partialPath, targetPath);
            return UpdateDownloadResult.Ok(targetPath);
        }
        catch (Exception exception) when (IsRequestFailure(exception, cancellationToken, out var kind))
        {
            TryDelete(partialPath);
            return kind switch
            {
                UpdateErrorKind.Timeout => UpdateDownloadResult.Fail(UpdateErrorKind.Timeout, "更新下载超时，请稍后重试。"),
                _ => UpdateDownloadResult.Fail(UpdateErrorKind.DownloadFailed, "更新下载失败，请检查网络后重试。")
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(partialPath);
            return UpdateDownloadResult.Fail(UpdateErrorKind.DownloadFailed, "更新下载失败：无法写入本地临时目录。");
        }
    }

    /// <summary>
    /// 以静默参数启动已通过校验的安装包（脱钩进程）。时序由调用方保证：启动前必须
    /// 已排空保存管线，启动后应用经既有优雅退出路径关闭——安装器检查互斥锁时应用
    /// 必须已退出（AppMutex 静默失守即更新静默失败）。
    /// </summary>
    public bool TryStartSilentInstaller(string installerPath)
    {
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo(installerPath)
        {
            Arguments = SilentInstallArguments,
            UseShellExecute = true
        };
        if (_processStarter is not null)
        {
            return _processStarter(startInfo);
        }

        try
        {
            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>超时/限流/服务不可用等统一映射；类别与文案集中此处，失败不静默吞没。</summary>
    private static UpdateCheckResult MapCheckFailure(HttpStatusCode statusCode) =>
        (int)statusCode switch
        {
            404 => UpdateCheckResult.Fail(UpdateErrorKind.NotFound, "更新服务上暂无正式发布。"),
            403 or 429 => UpdateCheckResult.Fail(UpdateErrorKind.RateLimited, "检查更新请求过于频繁，请稍后再试。"),
            >= 500 => UpdateCheckResult.Fail(UpdateErrorKind.ServerError, "更新服务暂时不可用。"),
            _ => UpdateCheckResult.Fail(UpdateErrorKind.BadResponse, $"检查更新失败（HTTP {(int)statusCode}）。")
        };

    private static UpdateCheckResult ParseRelease(string body, string localVersion)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var tag = root.GetProperty("tag_name").GetString();
            string? releaseUrl = root.TryGetProperty("html_url", out var urlElement)
                ? urlElement.GetString()
                : null;
            if (!TryNormalizeVersion(tag, out var remoteVersion))
            {
                return UpdateCheckResult.Fail(UpdateErrorKind.BadResponse, "更新服务返回了无法解析的版本号。");
            }

            var versionText = tag!.Trim();
            if (versionText.StartsWith('v') || versionText.StartsWith('V'))
            {
                versionText = versionText[1..];
            }

            if (!Version.TryParse(localVersion, out var localVersionValue) || remoteVersion <= localVersionValue)
            {
                return UpdateCheckResult.UpToDate();
            }

            UpdateAsset? asset = null;
            UpdateAsset? checksums = null;
            if (root.TryGetProperty("assets", out var assetsElement) &&
                assetsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in assetsElement.EnumerateArray())
                {
                    var name = entry.GetProperty("name").GetString();
                    var downloadUrl = entry.GetProperty("browser_download_url").GetString();
                    long size = entry.TryGetProperty("size", out var sizeElement) &&
                        sizeElement.TryGetInt64(out var parsedSize)
                            ? parsedSize
                            : 0;
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(downloadUrl))
                    {
                        continue;
                    }

                    if (string.Equals(
                            name,
                            InstallerAssetNamePrefix + versionText + InstallerAssetNameSuffix,
                            StringComparison.Ordinal))
                    {
                        asset = new UpdateAsset(name, downloadUrl, size);
                    }
                    else if (string.Equals(name, ChecksumsFileName, StringComparison.Ordinal))
                    {
                        checksums = new UpdateAsset(name, downloadUrl, size);
                    }
                }
            }

            if (asset is null || checksums is null)
            {
                // 精确名资产或校验清单缺失：自动更新不可用，交由界面走发布页回退（不静默吞掉）。
                return new UpdateCheckResult
                {
                    IsSuccess = true,
                    HasUpdate = true,
                    LatestVersion = versionText,
                    ReleaseUrl = releaseUrl,
                    ErrorKind = UpdateErrorKind.AssetMissing,
                    ErrorMessage = $"新版本 {versionText} 未提供可校验的自动更新安装包。"
                };
            }

            return UpdateCheckResult.UpdateAvailable(versionText, releaseUrl, asset, checksums);
        }
        catch (Exception exception) when (
            exception is JsonException
                or KeyNotFoundException
                or InvalidOperationException
                or FormatException)
        {
            return UpdateCheckResult.Fail(UpdateErrorKind.BadResponse, "更新服务返回了无法解析的响应。");
        }
    }

    private static bool TryNormalizeVersion(string? tag, [NotNullWhen(true)] out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var candidate = tag.Trim();
        if (candidate.StartsWith('v') || candidate.StartsWith('V'))
        {
            candidate = candidate[1..];
        }

        return Version.TryParse(candidate, out version);
    }

    /// <summary>
    /// 请求失败统一判定：外层令牌取消原样上抛（关闭序列等真实取消）；
    /// 其余按「超时/网络」折叠——30s 检查与 10 分钟下载预算都以取消异常呈现。
    /// </summary>
    private static bool IsRequestFailure(
        Exception exception,
        CancellationToken cancellationToken,
        out UpdateErrorKind kind)
    {
        if (exception is OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                kind = default;
                return false;
            }

            kind = UpdateErrorKind.Timeout;
            return true;
        }

        if (exception is HttpRequestException)
        {
            kind = UpdateErrorKind.NetworkError;
            return true;
        }

        kind = default;
        return false;
    }

    private static string? ParseChecksumLine(string checksumsText, string assetName)
    {
        foreach (var rawLine in checksumsText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length <= 64)
            {
                continue;
            }

            var hash = line[..64];
            if (!hash.All(Uri.IsHexDigit))
            {
                continue;
            }

            var name = line[64..].TrimStart(' ', '\t', '*');
            if (string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase))
            {
                return hash;
            }
        }

        return null;
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 残留文件不阻塞更新：下次下载前的清理会再试。
        }
    }

    private async Task DownloadToFileAsync(
        string url,
        string targetPath,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var timeoutCts = new CancellationTokenSource(DownloadTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content
            .ReadAsStreamAsync(linkedCts.Token)
            .ConfigureAwait(false);
        await using var target = new FileStream(
            targetPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        await source.CopyToAsync(target, linkedCts.Token).ConfigureAwait(false);
    }

    private async Task<string?> DownloadTextAsync(
        string url,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var timeoutCts = new CancellationTokenSource(DownloadTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, linkedCts.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength;
        if (length is { } knownLength && knownLength > maxBytes)
        {
            // 异常膨胀的清单不可信：按无校验值处理（调用方折叠为 HashMismatch）。
            return null;
        }

        return await response.Content
            .ReadAsStringAsync(linkedCts.Token)
            .ConfigureAwait(false);
    }
}
