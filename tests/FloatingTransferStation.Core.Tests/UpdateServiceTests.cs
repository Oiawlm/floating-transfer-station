using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using FloatingTransferStation;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// UpdateService 离线锁定测试（fake HttpMessageHandler，全程不真联网）：
/// ① 版本比较矩阵（v 前缀剥离、相等不算、严格大于才算、两段缺段、垃圾 tag、带后缀 tag）；
/// ② GitHub releases/latest 的 404/403/429/5xx/超时/坏 JSON/网络异常一律折叠为「无更新」
///    结果记录而非异常外泄；③ 资产按精确名匹配与缺失回退信号（AssetMissing + 发布页）；
/// ④ SHA256 校验通过/失败与失败清理（含同版本旧文件与半成品清理）；⑤
/// 「检查→下载→校验→可安装」状态流转。请求契约：显式 User-Agent 与 GitHub Accept 头
/// （缺 User-Agent 直接 403）、repo API 地址与 HTML 地址同源于 ProductIdentity。
/// </summary>
[TestClass]
public sealed class UpdateServiceTests
{
    private const string ReleaseApiUrl = "https://api.github.com/repos/Oiawlm/floating-transfer-station/releases/latest";
    private const string ReleaseHtmlUrl = "https://github.com/Oiawlm/floating-transfer-station/releases/tag/v99.0.0";
    private const string InstallerAssetName = "FloatingTransferStation-Setup-99.0.0.exe";
    private const string InstallerDownloadUrl = "https://github.com/Oiawlm/floating-transfer-station/releases/download/v99.0.0/" + InstallerAssetName;
    private const string ChecksumsDownloadUrl = "https://github.com/Oiawlm/floating-transfer-station/releases/download/v99.0.0/SHA256SUMS.txt";

    [TestMethod]
    public void IsNewerVersion_StripsVPrefixBeforeComparing()
    {
        Assert.IsTrue(UpdateService.IsNewerVersion("v99.0.0", "1.34.0"));
        Assert.IsTrue(UpdateService.IsNewerVersion("V99.0.0", "1.34.0"));
    }

    [TestMethod]
    public void IsNewerVersion_EqualVersion_IsNotAnUpdate()
    {
        Assert.IsFalse(UpdateService.IsNewerVersion("v1.34.0", "1.34.0"));
        Assert.IsFalse(UpdateService.IsNewerVersion("1.34.0", "1.34.0"));
    }

    [TestMethod]
    public void IsNewerVersion_OnlyStrictlyGreaterCounts()
    {
        Assert.IsTrue(UpdateService.IsNewerVersion("v1.34.1", "1.34.0"));
        Assert.IsFalse(UpdateService.IsNewerVersion("v1.33.9", "1.34.0"));
    }

    [TestMethod]
    public void IsNewerVersion_TwoSegmentTag_ParsesAsVersion()
    {
        // .NET Version 最少要求 major.minor：两段 tag 按 Version 语义（缺段=-1）参与比较，
        // 单段或零段不属于版本，一律无更新。
        Assert.IsTrue(UpdateService.IsNewerVersion("v99.0", "1.34.0"));
        Assert.IsFalse(UpdateService.IsNewerVersion("v1.0", "1.34.0"));
        Assert.IsFalse(UpdateService.IsNewerVersion("v99", "1.34.0"));
    }

    [TestMethod]
    public void IsNewerVersion_GarbageTag_IsNeverAnUpdate()
    {
        Assert.IsFalse(UpdateService.IsNewerVersion("not-a-version", "1.34.0"));
        Assert.IsFalse(UpdateService.IsNewerVersion("", "1.34.0"));
        Assert.IsFalse(UpdateService.IsNewerVersion(null, "1.34.0"));
    }

    [TestMethod]
    public void IsNewerVersion_PrereleaseSuffixedTag_IsNeverAnUpdate()
    {
        // Version 不解析预发布后缀；releases/latest 本就排除预发布，带后缀一律按无更新。
        Assert.IsFalse(UpdateService.IsNewerVersion("v99.0.0-beta.1", "1.34.0"));
    }

    [TestMethod]
    public async Task CheckForUpdateAsync_NewerRelease_ParsesVersionReleaseUrlAndExactAsset()
    {
        var assets = string.Join(
            ",",
            AssetEntry("FloatingTransferStation-Setup-98.0.0.exe", "https://example.com/wrong.exe", 1),
            AssetEntry(InstallerAssetName, InstallerDownloadUrl, 123),
            AssetEntry("SHA256SUMS.txt", ChecksumsDownloadUrl, 99));
        var handler = RespondWithRelease(ReleaseBody("v99.0.0", assets));
        var service = new UpdateService(handler);

        var result = await service.CheckForUpdateAsync();

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.IsTrue(result.HasUpdate);
        Assert.AreEqual("99.0.0", result.LatestVersion);
        Assert.AreEqual(ReleaseHtmlUrl, result.ReleaseUrl);
        Assert.IsNotNull(result.Asset);
        Assert.AreEqual(InstallerAssetName, result.Asset.Name);
        Assert.AreEqual(InstallerDownloadUrl, result.Asset.DownloadUrl);
        Assert.AreEqual(123, result.Asset.SizeBytes);
        Assert.IsNotNull(result.Checksums);
        Assert.AreEqual(ChecksumsDownloadUrl, result.Checksums.DownloadUrl);
        Assert.AreEqual(UpdateErrorKind.None, result.ErrorKind);
        Assert.AreEqual(1, handler.CallCount);
    }

    [TestMethod]
    public async Task CheckForUpdateAsync_SendsRequiredGitHubHeadersToConfiguredApiUrl()
    {
        var handler = RespondWithRelease(ReleaseBody("v0.0.1", "[]"));
        var service = new UpdateService(handler);

        await service.CheckForUpdateAsync();

        var request = handler.LastRequest;
        Assert.IsNotNull(request);
        Assert.AreEqual(ReleaseApiUrl, request.RequestUri!.ToString());
        Assert.IsNotNull(
            request.Headers.UserAgent.FirstOrDefault(agent =>
                string.Equals(agent.Product?.Name, "FloatingTransferStation", StringComparison.Ordinal)),
            "GitHub 要求显式 User-Agent（缺失直接 403）。");
        StringAssert.Contains(
            request.Headers.Accept.ToString(),
            "application/vnd.github+json");
    }

    [TestMethod]
    public async Task CheckForUpdateAsync_OlderRelease_ReportsUpToDate()
    {
        var service = new UpdateService(RespondWithRelease(ReleaseBody("v0.0.1", "[]")));

        var result = await service.CheckForUpdateAsync();

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(result.HasUpdate);
        Assert.IsNull(result.Asset);
    }

    [TestMethod]
    public async Task CheckForUpdateAsync_NewerReleaseWithoutExactAsset_SignalsFallbackWithReleaseUrl()
    {
        var assets = AssetEntry(
            "悬浮中转站-Setup-99.0.0.exe",
            "https://example.com/localized.exe",
            1);
        var service = new UpdateService(RespondWithRelease(ReleaseBody("v99.0.0", assets)));

        var result = await service.CheckForUpdateAsync();

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.HasUpdate);
        Assert.IsNull(result.Asset, "无精确名匹配资产时不得给出可下载资产。");
        Assert.AreEqual(UpdateErrorKind.AssetMissing, result.ErrorKind);
        Assert.AreEqual(ReleaseHtmlUrl, result.ReleaseUrl, "缺失回退必须携带发布页地址。");
    }

    [TestMethod]
    public async Task CheckForUpdateAsync_AllFailureModes_FoldIntoNoUpdateResultWithoutThrowing()
    {
        var cases = new (HttpStatusCode? Status, Exception? Thrown, string Body, UpdateErrorKind Kind)[]
        {
            (HttpStatusCode.NotFound, null, "{}", UpdateErrorKind.NotFound),
            (HttpStatusCode.Forbidden, null, "{}", UpdateErrorKind.RateLimited),
            (HttpStatusCode.TooManyRequests, null, "{}", UpdateErrorKind.RateLimited),
            (HttpStatusCode.InternalServerError, null, "{}", UpdateErrorKind.ServerError),
            (HttpStatusCode.ServiceUnavailable, null, "", UpdateErrorKind.ServerError),
            (null, new TaskCanceledException(), "", UpdateErrorKind.Timeout),
            (null, new HttpRequestException("socket"), "", UpdateErrorKind.NetworkError),
            (HttpStatusCode.OK, null, "not json", UpdateErrorKind.BadResponse),
            (HttpStatusCode.OK, null, """{"tag_name":}""", UpdateErrorKind.BadResponse),
        };
        foreach (var (status, thrown, body, kind) in cases)
        {
            var handler = new FakeHttpMessageHandler((_, _) => thrown is not null
                ? Task.FromException<HttpResponseMessage>(thrown)
                : Task.FromResult(new HttpResponseMessage(status!.Value)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                }));
            var service = new UpdateService(handler);

            var result = await service.CheckForUpdateAsync();

            Assert.IsFalse(result.IsSuccess, $"{kind} 不得误报成功。");
            Assert.IsFalse(result.HasUpdate, $"{kind} 必须折叠为无更新。");
            Assert.AreEqual(kind, result.ErrorKind);
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.ErrorMessage), $"{kind} 需要中文用户文案供手动检查反馈。");
        }
    }

    [TestMethod]
    public async Task DownloadUpdateAsync_VerifiedDownload_WritesInstallerToTargetDirectory()
    {
        var payload = Enumerable.Range(0, 2048).Select(index => (byte)(index % 251)).ToArray();
        var checksums = Sha256Sum(InstallerAssetName, payload) + "\n" + Sha256Sum("sha-other.txt", [1, 2, 3]);
        var (handler, service) = CreateVerifiedDownloadService(payload, checksums);
        using var directory = new TestDirectory();

        var check = await service.CheckForUpdateAsync();
        var download = await service.DownloadUpdateAsync(check, directory.Root);

        Assert.IsTrue(download.IsSuccess, download.ErrorMessage);
        Assert.AreEqual(UpdateErrorKind.None, download.ErrorKind);
        Assert.AreEqual(Path.Combine(directory.Root, InstallerAssetName), download.InstallerPath);
        Assert.IsTrue(File.Exists(download.InstallerPath));
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(download.InstallerPath!));
        Assert.AreEqual(3, handler.CallCount, "检查 + 资产 + SHA256SUMS.txt 共三个请求。");
    }

    [TestMethod]
    public async Task DownloadUpdateAsync_HashMismatch_FailsDeletesFileAndNeverReportsInstallable()
    {
        var (_, service) = CreateVerifiedDownloadService([9, 8, 7], Sha256Sum(InstallerAssetName, [1, 1, 1]));
        using var directory = new TestDirectory();

        var check = await service.CheckForUpdateAsync();
        var download = await service.DownloadUpdateAsync(check, directory.Root);

        Assert.IsFalse(download.IsSuccess);
        Assert.AreEqual(UpdateErrorKind.HashMismatch, download.ErrorKind);
        Assert.IsNull(download.InstallerPath);
        Assert.IsFalse(string.IsNullOrWhiteSpace(download.ErrorMessage));
        Assert.IsEmpty(Directory.GetFiles(directory.Root), "校验失败的安装包与半成品都必须被清理。");
    }

    [TestMethod]
    public async Task DownloadUpdateAsync_ChecksumLineMissing_FailsAndCleansUp()
    {
        var payload = new byte[] { 4, 5, 6 };
        var (_, service) = CreateVerifiedDownloadService(payload, Sha256Sum("unrelated-file.exe", payload));
        using var directory = new TestDirectory();

        var check = await service.CheckForUpdateAsync();
        var download = await service.DownloadUpdateAsync(check, directory.Root);

        Assert.IsFalse(download.IsSuccess);
        Assert.AreEqual(UpdateErrorKind.HashMismatch, download.ErrorKind);
        Assert.IsEmpty(Directory.GetFiles(directory.Root), "校验清单缺失条目时不得留下任何文件。");
    }

    [TestMethod]
    public async Task DownloadUpdateAsync_CleansStaleFilesForSameVersionBeforeDownloading()
    {
        var payload = new byte[] { 42, 42, 42 };
        var (handler, service) = CreateVerifiedDownloadService(payload, Sha256Sum(InstallerAssetName, payload));
        using var directory = new TestDirectory();
        var staleTarget = Path.Combine(directory.Root, InstallerAssetName);
        var stalePartial = staleTarget + ".download";
        await File.WriteAllBytesAsync(staleTarget, [1]);
        await File.WriteAllBytesAsync(stalePartial, [2]);

        var check = await service.CheckForUpdateAsync();
        var download = await service.DownloadUpdateAsync(check, directory.Root);

        Assert.IsTrue(download.IsSuccess, download.ErrorMessage);
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(staleTarget), "同版本旧文件必须先清理再落新包。");
        Assert.IsFalse(File.Exists(stalePartial), "残留半成品必须清理。");
        Assert.AreEqual(3, handler.CallCount);
    }

    [TestMethod]
    public async Task DownloadUpdateAsync_NetworkFailureDuringDownload_CleansUpAndFails()
    {
        var payload = new byte[] { 1 };
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.ToString() == ReleaseApiUrl)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        ReleaseBody(
                            "v99.0.0",
                            string.Join(
                                ",",
                                AssetEntry(InstallerAssetName, InstallerDownloadUrl, payload.Length),
                                AssetEntry("SHA256SUMS.txt", ChecksumsDownloadUrl, 99))),
                        Encoding.UTF8,
                        "application/json")
                });
            }

            if (request.RequestUri.ToString() == ChecksumsDownloadUrl)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Sha256Sum(InstallerAssetName, payload), Encoding.UTF8, "text/plain")
                });
            }

            return Task.FromException<HttpResponseMessage>(new HttpRequestException("connection reset"));
        });
        var service = new UpdateService(handler);
        using var directory = new TestDirectory();

        var check = await service.CheckForUpdateAsync();
        var download = await service.DownloadUpdateAsync(check, directory.Root);

        Assert.IsTrue(check.HasUpdate);
        Assert.IsFalse(download.IsSuccess);
        Assert.AreEqual(UpdateErrorKind.DownloadFailed, download.ErrorKind);
        Assert.IsEmpty(Directory.GetFiles(directory.Root), "下载失败不得留下半成品。");
    }

    [TestMethod]
    public void TryStartSilentInstaller_UsesSilentArgumentsAndRequiresExistingFile()
    {
        ProcessStartInfo? captured = null;
        var service = new UpdateService(
            new FakeHttpMessageHandler((_, _) => throw new AssertFailedException("启动安装器不得发起网络请求。")),
            startInfo =>
            {
                captured = startInfo;
                return true;
            });
        Assert.IsFalse(service.TryStartSilentInstaller("Z:\\nonexistent\\installer.exe"), "安装包不存在时不得启动进程。");

        using var directory = new TestDirectory();
        var installer = Path.Combine(directory.Root, InstallerAssetName);
        File.WriteAllText(installer, "installer");
        Assert.IsTrue(service.TryStartSilentInstaller(installer));

        Assert.IsNotNull(captured);
        Assert.AreEqual(installer, captured.FileName);
        Assert.AreEqual("/SILENT /SUPPRESSMSGBOXES /NORESTART", captured.Arguments);
        Assert.IsTrue(captured.UseShellExecute, "静默安装必须以脱钩外壳进程启动。");
    }

    [TestMethod]
    public async Task CheckForUpdateAsync_GarbageTag_ReportsUpToDate()
    {
        // 锁定决策 2：tag 解析失败/格式垃圾一律按「无更新」而非失败。
        var service = new UpdateService(RespondWithRelease(ReleaseBody("not-a-version", "[]")));

        var result = await service.CheckForUpdateAsync();

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(result.HasUpdate);
    }

    [TestMethod]
    public async Task CheckForUpdateAsync_MalformedAssetEntries_StillFindsTargetAsset()
    {
        // 畸形资产条目（缺字段）只跳过自身，不拖垮整个发布解析：目标资产与校验清单照常定位。
        var assets = string.Join(
            ",",
            """{"size":1}""",
            AssetEntry(InstallerAssetName, InstallerDownloadUrl, 123),
            """{"name":"SHA256SUMS.txt","size":99}""",
            AssetEntry("SHA256SUMS.txt", ChecksumsDownloadUrl, 99));
        var service = new UpdateService(RespondWithRelease(ReleaseBody("v99.0.0", assets)));

        var result = await service.CheckForUpdateAsync();

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.IsTrue(result.HasUpdate);
        Assert.IsNotNull(result.Asset);
        Assert.AreEqual(InstallerAssetName, result.Asset!.Name);
        Assert.IsNotNull(result.Checksums);
        Assert.AreEqual(ChecksumsDownloadUrl, result.Checksums!.DownloadUrl);
    }

    [TestMethod]
    public async Task DownloadUpdateAsync_CancellationRequested_CleansPartialAndPropagates()
    {
        var payload = new byte[] { 7, 7, 7 };
        var (_, service) = CreateVerifiedDownloadService(payload, Sha256Sum(InstallerAssetName, payload));
        using var directory = new TestDirectory();
        var check = await service.CheckForUpdateAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        OperationCanceledException? cancelled = null;
        try
        {
            await service.DownloadUpdateAsync(check, directory.Root, cts.Token);
        }
        catch (OperationCanceledException exception)
        {
            cancelled = exception;
        }

        Assert.IsNotNull(cancelled, "外层取消必须原样上抛（关闭序列依赖）。");
        Assert.IsEmpty(Directory.GetFiles(directory.Root), "取消路径同样必须清理半成品。");
    }

    // repo 地址单一来源的锁定不在此处：常量对字面量的断言是恒真同义反复（MSTEST0032），
    // 真实锁定由 CheckForUpdateAsync_SendsRequiredGitHubHeadersToConfiguredApiUrl
    // （服务实际请求 URL=预期 API 地址）与 LifecycleTests.RepoUrl_IsSingleSourcedFromProductIdentity
    // （设置窗 XAML 无仓库地址字面量）承担。

    private static FakeHttpMessageHandler RespondWithRelease(string releaseBody) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(releaseBody, Encoding.UTF8, "application/json")
        }));

    private static (FakeHttpMessageHandler Handler, UpdateService Service) CreateVerifiedDownloadService(
        byte[] payload,
        string checksums) =>
        CreateDownloadRoutingService((request) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (request.RequestUri!.ToString() == ChecksumsDownloadUrl)
            {
                response.Content = new StringContent(checksums, Encoding.UTF8, "text/plain");
            }
            else
            {
                response.Content = new ByteArrayContent(payload);
            }

            return response;
        });

    private static (FakeHttpMessageHandler Handler, UpdateService Service) CreateDownloadRoutingService(
        Func<HttpRequestMessage, HttpResponseMessage> downloadResponder)
    {
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.ToString() == ReleaseApiUrl)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        ReleaseBody(
                            "v99.0.0",
                            string.Join(
                                ",",
                                AssetEntry(InstallerAssetName, InstallerDownloadUrl, 123),
                                AssetEntry("SHA256SUMS.txt", ChecksumsDownloadUrl, 99))),
                        Encoding.UTF8,
                        "application/json")
                });
            }

            return Task.FromResult(downloadResponder(request));
        });
        return (handler, new UpdateService(handler));
    }

    private static string ReleaseBody(string tag, string assets) => $$"""
        {"tag_name":"{{tag}}","name":"悬浮中转站 {{tag}}","html_url":"{{ReleaseHtmlUrl}}","draft":false,"prerelease":false,"assets":[{{assets}}]}
        """;

    private static string AssetEntry(string name, string url, long size) =>
        $$"""{"name":"{{name}}","browser_download_url":"{{url}}","size":{{size}}}""";

    private static string Sha256Sum(string fileName, ReadOnlySpan<byte> payload) =>
        $"{Convert.ToHexString(SHA256.HashData(payload.ToArray())).ToLowerInvariant()}  {fileName}";

    /// <summary>记录收到的请求并按给定应答函数回复的假 handler；不发起任何真实网络。</summary>
    private sealed class FakeHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            return await responder(request, cancellationToken);
        }
    }
}
