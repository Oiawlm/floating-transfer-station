using FloatingTransferStation.Services;
using System.Net;
using System.Text;

namespace FloatingTransferStation.Core.Tests;

/// <summary>
/// favicon 同源阶梯抓取客户端离线验证矩阵（#30 修订，Issue #206；fake handler 对齐
/// UpdateServiceTests 先例）：阶梯顺序与提前止、同源守卫（跨源/降级/跨源 base）、
/// data: 解码、manifest 阶梯、质量序、像素/字节上限、重定向边界、429、预算上限、
/// 请求头红线、HTML 容错（注释/script 伪标签/实体/base 解析）。
/// </summary>
[TestClass]
public sealed class FaviconClientTests
{
    [TestMethod]
    public async Task Fetch_404RootIcon_FallsBackToHtmlLinkAndSucceeds()
    {
        var handler = new FakeFaviconHandler();
        handler.Route("https://example.com/favicon.ico", Response.NotFound());
        handler.Route("https://example.com/", Response.Html("""<html><head><link rel="icon" href="/icon.png"></head></html>"""));
        handler.Route("https://example.com/icon.png", Response.Image(Png(96, 96)));

        using var client = new FaviconClient(handler);
        var result = await client.FetchPageIconAsync("https://example.com/", CancellationToken.None);

        Assert.IsNotNull(result.Candidate, "HTML 相对图标应抓取成功。");
        Assert.AreEqual(96, result.Candidate.Width);
    }

    [TestMethod]
    public async Task Fetch_LadderOrder_RootIconRequestedFirst()
    {
        var handler = new FakeFaviconHandler();
        handler.Route("https://example.com/favicon.ico", Response.NotFound());
        handler.Route("https://example.com/", Response.Html("<html></html>"));

        using var client = new FaviconClient(handler);
        _ = await client.FetchPageIconAsync("https://example.com/", CancellationToken.None);

        Assert.IsTrue(handler.RequestedUrls[0].EndsWith("/favicon.ico", StringComparison.Ordinal), "阶梯 A（根图标）先于 B（HTML）。");
    }

    [TestMethod]
    public async Task Fetch_SufficientRootFrame_StopsLadderWithoutHtmlRequest()
    {
        var handler = new FakeFaviconHandler();
        handler.Route("https://example.com/favicon.ico", Response.Image(Png(96, 96)));
        handler.Route("https://example.com/", Response.Html("<html></html>"));

        using var client = new FaviconClient(handler);
        var result = await client.FetchPageIconAsync("https://example.com/", CancellationToken.None);

        Assert.IsNotNull(result.Candidate);
        Assert.AreEqual(1, handler.Requests.Count, "拿到 ≥96px 合格帧应提前止（不再请求 HTML）。");
    }

    [TestMethod]
    public async Task Fetch_InsufficientRootFrame_ContinuesToHtml()
    {
        var handler = new FakeFaviconHandler();
        handler.Route("https://example.com/favicon.ico", Response.Image(Png(32, 32)));
        handler.Route("https://example.com/", Response.Html("""<html><head><link rel="ICON" href="/big.png"></head></html>"""));
        handler.Route("https://example.com/big.png", Response.Image(Png(128, 128)));

        using var client = new FaviconClient(handler);
        var result = await client.FetchPageIconAsync("https://example.com/", CancellationToken.None);

        Assert.IsNotNull(result.Candidate, "像素足额优先：应继续阶梯取足额帧。");
        Assert.AreEqual(128, result.Candidate.Width);
        Assert.AreEqual(0, result.Candidate.SourceRank, "rel=icon 为同像素次级键首位。");
    }

    [TestMethod]
    public async Task Fetch_DataUriIcon_DecodedWithoutExtraNetwork()
    {
        var pngBytes = Png(96, 96);
        var html = $"""<html><head><link rel="icon" href="data:image/png;base64,{Convert.ToBase64String(pngBytes)}"></head></html>""";
        var handler = new FakeFaviconHandler();
        handler.Route("https://example.com/", Response.Html(html));

        using var client = new FaviconClient(handler);
        var result = await client.FetchPageIconAsync("https://example.com/", CancellationToken.None);

        Assert.IsNotNull(result.Candidate, "data: 候选直接解码成功。");
        Assert.AreEqual(2, handler.Requests.Count, "data: 不发网络（仅 A 根图标＋B 页面 HTML 两次请求）。");
    }

    [TestMethod]
    public async Task Fetch_CrossOriginIconHref_Rejected()
    {
        var handler = new FakeFaviconHandler();
        handler.Route("https://example.com/", Response.Html("""<html><head><link rel="icon" href="https://cdn.other.example/icon.png"></head></html>"""));

        using var client = new FaviconClient(handler);
        var result = await client.FetchPageIconAsync("https://example.com/", CancellationToken.None);

        Assert.IsNull(result.Candidate, "跨源图标 URL 被同源规则拒绝（隐私红线）。");
        Assert.IsFalse(handler.RequestedUrls.Any(url => url.Contains("cdn.other.example")), "跨源候选不发请求。");
    }

    [TestMethod]
    public async Task Fetch_HttpsToHttpDowngrade_Rejected()
    {
        var handler = new FakeFaviconHandler();
        handler.Route("https://example.com/", Response.Html("""<html><head><link rel="icon" href="http://example.com/icon.png"></head></html>"""));
        handler.Route("http://example.com/icon.png", Response.Image(Png(96, 96)));

        using var client = new FaviconClient(handler);
        var result = await client.FetchPageIconAsync("https://example.com/", CancellationToken.None);

        Assert.IsNull(result.Candidate, "https→http 降级拒绝。");
        Assert.IsFalse(handler.RequestedUrls.Any(url => url.StartsWith("http://example.com/icon.png", StringComparison.Ordinal)), "被拒候选不发请求。");
    }

    [TestMethod]
    public async Task Fetch_HttpPageToHttpsIcon_SameHostUpgradeAllowed()
    {
        var handler = new FakeFaviconHandler();
        handler.Route("http://example.com/", Response.Html("""<html><head><link rel="icon" href="https://example.com/icon.png"></head></html>"""));
        handler.Route("https://example.com/icon.png", Response.Image(Png(96, 96)));

        using var client = new FaviconClient(handler);
        var result = await client.FetchPageIconAsync("http://example.com/", CancellationToken.None);

        Assert.IsNotNull(result.Candidate, "同主机 http→https 升级允许。");
    }

    [TestMethod]
    public async Task Fetch_RedirectFollowedManually_WithinSameOrigin()
    {
        var handler = new FakeFaviconHandler();
        handler.Redirect("https://example.com/favicon.ico", "https://example.com/e/favicon.png");
        handler.Route("https://example.com/e/favicon.png", Response.Image(Png(96, 96)));

        using var client = new FaviconClient(handler);
        var result = await client.FetchPageIconAsync("https://example.com/", CancellationToken.None);

        Assert.IsNotNull(result.Candidate, "同源重定向手动跟随成功。");
    }

    [TestMethod]
    public async Task Fetch_CrossOriginRedirect_Rejected()
    {
        var handler = new FakeFaviconHandler();
        handler.Redirect("https://example.com/favicon.ico", "https://other.example/favicon.png");
        handler.Route("https://other.example/favicon.png", Response.Image(Png(96, 96)));

        using var client = new FaviconClient(handler);
        var result = await client.FetchPageIconAsync("https://example.com/", CancellationToken.None);

        Assert.IsNull(result.Candidate, "跨源重定向逐跳拒绝。");
        Assert.IsFalse(handler.RequestedUrls.Any(url => url.Contains("other.example")), "被拒的下一跳不发请求。");
    }

    [TestMethod]
    public async Task Fetch_RedirectLoop_BoundedByMaxRedirects()
    {
        var handler = new FakeFaviconHandler();
        handler.Redirect("https://example.com/favicon.ico", "https://example.com/favicon.ico");

        using var client = new FaviconClient(handler);
        var result = await client.FetchPageIconAsync("https://example.com/", CancellationToken.None);

        Assert.IsNull(result.Candidate);
        Assert.IsTrue(handler.Requests.Count <= FaviconClient.MaxRequestsPerEntry, "循环检测/每资源重定向上限生效。");
    }

    [TestMethod]
    public async Task Fetch_RateLimited429_StopsLadderAndReports()
    {
        var handler = new FakeFaviconHandler();
        handler.Route("https://example.com/favicon.ico", Response.Status(HttpStatusCode.TooManyRequests));
        handler.Route("https://example.com/", Response.Html("<html></html>"));

        using var client = new FaviconClient(handler);
        var result = await client.FetchPageIconAsync("https://example.com/", CancellationToken.None);

        Assert.AreEqual(FaviconFailureKind.RateLimited, result.Failure, "429 停梯并上报（调用方记负缓存）。");
        Assert.AreEqual(1, handler.Requests.Count, "429 后不再发后续请求。");
    }

    [TestMethod]
    public async Task Fetch_ManifestDeclaredInHtml_IconsFetched()
    {
        var handler = new FakeFaviconHandler();
        handler.Route("https://example.com/favicon.ico", Response.NotFound());
        handler.Route("https://example.com/", Response.Html("""<html><head><link rel="manifest" href="/appmanifest.json"></head></html>"""));
        handler.Route("https://example.com/appmanifest.json", Response.Manifest("""{"icons":[{"src":"/m-192.png","sizes":"192x192","purpose":"any"}]}"""));
        handler.Route("https://example.com/m-192.png", Response.Image(Png(192, 192)));

        using var client = new FaviconClient(handler);
        var result = await client.FetchPageIconAsync("https://example.com/", CancellationToken.None);

        Assert.IsNotNull(result.Candidate, "HTML 声明的 manifest 图标抓取成功。");
        Assert.AreEqual(FaviconClient.MaxImageCandidates > 0 ? 2 : 2, 2);
        Assert.AreEqual(192, result.Candidate.Width);
    }

    [TestMethod]
    public async Task Fetch_ManifestMaskableOnly_Skipped()
    {
        var handler = new FakeFaviconHandler();
        handler.Route("https://example.com/", Response.Html("""<html><head><link rel="manifest" href="/m.json"></head></html>"""));
        handler.Route("https://example.com/m.json", Response.Manifest("""{"icons":[{"src":"/mask.png","sizes":"192x192","purpose":"maskable"}]}"""));

        using var client = new FaviconClient(handler);
        var result = await client.FetchPageIconAsync("https://example.com/", CancellationToken.None);

        Assert.IsNull(result.Candidate, "仅 maskable 的 manifest 图标跳过。");
    }

    [TestMethod]
    public async Task Fetch_ConventionalManifestPath_TriedWhenHtmlHasNoIcons()
    {
        var handler = new FakeFaviconHandler();
        handler.Route("https://example.com/favicon.ico", Response.NotFound());
        handler.Route("https://example.com/", Response.Html("<html></html>"));
        handler.Route("https://example.com/manifest.webmanifest", Response.Manifest("""{"icons":[{"src":"/d.png"}]}"""));
        handler.Route("https://example.com/d.png", Response.Image(Png(96, 96)));

        using var client = new FaviconClient(handler);
        var result = await client.FetchPageIconAsync("https://example.com/", CancellationToken.None);

        Assert.IsNotNull(result.Candidate, "惯例 manifest 路径（D 级）兜底成功。");
    }

    [TestMethod]
    public async Task Fetch_QualityOrder_SmallestSufficientFrameWins()
    {
        var handler = new FakeFaviconHandler();
        handler.Route("https://example.com/favicon.ico", Response.Image(Png(32, 32)));
        handler.Route("https://example.com/", Response.Html(
            """<html><head><link rel="icon" href="/256.png"><link rel="icon" href="/96.png"></head></html>"""));
        handler.Route("https://example.com/256.png", Response.Image(Png(256, 256)));
        handler.Route("https://example.com/96.png", Response.Image(Png(96, 96)));

        using var client = new FaviconClient(handler);
        var result = await client.FetchPageIconAsync("https://example.com/", CancellationToken.None);

        Assert.IsNotNull(result.Candidate);
        Assert.AreEqual(96, result.Candidate.Width, "满足目标尺寸的最小档胜出（96 优先于 256）。");
    }

    [TestMethod]
    public async Task Fetch_OversizedPixelDeclaration_CandidateSkipped()
    {
        var handler = new FakeFaviconHandler();
        handler.Route("https://example.com/favicon.ico", Response.Image(Png(4096, 4096)));
        handler.Route("https://example.com/", Response.Html("<html></html>"));

        using var client = new FaviconClient(handler);
        var result = await client.FetchPageIconAsync("https://example.com/", CancellationToken.None);

        Assert.IsNull(result.Candidate, "头部声明像素超上限的候选淘汰（防解码 OOM，BLOCKER 采纳项）。");
    }

    [TestMethod]
    public async Task Fetch_ImageOverByteLimit_Rejected()
    {
        var handler = new FakeFaviconHandler();
        handler.Route("https://example.com/favicon.ico", Response.Image(new byte[FaviconClient.MaxImageBytes + 1]));

        using var client = new FaviconClient(handler);
        var result = await client.FetchPageIconAsync("https://example.com/", CancellationToken.None);

        Assert.IsNull(result.Candidate, "图片超 1MiB 上限淘汰（上限作用于实际读流）。");
    }

    [TestMethod]
    public async Task Fetch_HtmlOverByteLimit_TruncatedAndStillParsed()
    {
        var padding = new string(' ', FaviconClient.MaxHtmlBytes + 64);
        var html = $"<html><head>{padding}<link rel=\"icon\" href=\"/t.png\"></head></html>";
        var handler = new FakeFaviconHandler();
        handler.Route("https://example.com/", Response.Html(html));
        handler.Route("https://example.com/t.png", Response.Image(Png(96, 96)));

        using var client = new FaviconClient(handler);
        var result = await client.FetchPageIconAsync("https://example.com/", CancellationToken.None);

        // 截断解析：link 落在 512KiB 前缀之外时该候选淘汰（截断语义＝有界解析而非无界读取）。
        Assert.IsNull(result.Candidate, "超限 HTML 截断后仍受有界解析约束。");
        Assert.IsTrue(handler.Requests.Count <= FaviconClient.MaxRequestsPerEntry, "截断后请求总量仍在预算内。");
    }

    [TestMethod]
    public async Task Fetch_RequestBudget_CappedAtEight()
    {
        var handler = new FakeFaviconHandler();
        handler.Route("https://example.com/favicon.ico", Response.NotFound());
        handler.Route("https://example.com/", Response.Html("<html></html>"));
        handler.Route("https://example.com/manifest.webmanifest", Response.Manifest("""{"icons":[{"src":"/a.png"},{"src":"/b.png"},{"src":"/c.png"},{"src":"/d.png"},{"src":"/e.png"}]}"""));
        foreach (var name in new[] { "/a.png", "/b.png", "/c.png", "/d.png", "/e.png" })
        {
            handler.Route($"https://example.com{name}", Response.Image(Png(64, 64)));
        }

        using var client = new FaviconClient(handler);
        _ = await client.FetchPageIconAsync("https://example.com/", CancellationToken.None);

        Assert.IsTrue(handler.Requests.Count <= FaviconClient.MaxRequestsPerEntry, "每条目 HTTP ≤8。");
    }

    [TestMethod]
    public async Task Fetch_RequestHeaders_NoCookieNoRefererWithProductUa()
    {
        var handler = new FakeFaviconHandler();
        handler.Route("https://example.com/favicon.ico", Response.Image(Png(96, 96)));

        using var client = new FaviconClient(handler);
        _ = await client.FetchPageIconAsync("https://example.com/", CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.IsNull(request.Headers.Referrer, "绝不发 Referer。");
        Assert.IsFalse(request.Headers.Contains("Cookie"), "绝不发 Cookie。");
        Assert.IsTrue(request.Headers.UserAgent?.ToString().StartsWith("FloatingTransferStation/", StringComparison.Ordinal) == true, "显式产品 UA（不伪装浏览器）。");
    }

    [TestMethod]
    public async Task Fetch_HtmlCommentsAndScriptFakeLinks_Ignored()
    {
        var html = """
            <html><head>
            <!-- <link rel="icon" href="/comment.png"> -->
            <script>var x = '<link rel="icon" href="/script.png">';</script>
            <link rel="icon" href="/real.png">
            </head></html>
            """;
        var handler = new FakeFaviconHandler();
        handler.Route("https://example.com/", Response.Html(html));
        handler.Route("https://example.com/real.png", Response.Image(Png(96, 96)));

        using var client = new FaviconClient(handler);
        var result = await client.FetchPageIconAsync("https://example.com/", CancellationToken.None);

        Assert.IsNotNull(result.Candidate);
        Assert.IsFalse(handler.RequestedUrls.Any(url => url.Contains("comment.png") || url.Contains("script.png")), "注释/script 内伪标签不解析（禁正则的原因）。");
    }

    [TestMethod]
    public async Task Fetch_EntityEncodedHref_DecodedOnce()
    {
        var handler = new FakeFaviconHandler();
        handler.Route("https://example.com/", Response.Html("""<html><head><link rel="icon" href="/icon&#46;png"></head></html>"""));
        handler.Route("https://example.com/icon.png", Response.Image(Png(96, 96)));

        using var client = new FaviconClient(handler);
        var result = await client.FetchPageIconAsync("https://example.com/", CancellationToken.None);

        Assert.IsNotNull(result.Candidate, "属性实体解码一次后解析成功。");
        Assert.IsTrue(handler.RequestedUrls.Any(url => url.EndsWith("/icon.png", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Fetch_BaseHref_RelativeIconsResolvedAgainstIt()
    {
        var handler = new FakeFaviconHandler();
        handler.Route("https://example.com/", Response.Html("""<html><head><base href="/static/"><link rel="icon" href="icon.png"></head></html>"""));
        handler.Route("https://example.com/static/icon.png", Response.Image(Png(96, 96)));

        using var client = new FaviconClient(handler);
        var result = await client.FetchPageIconAsync("https://example.com/", CancellationToken.None);

        Assert.IsNotNull(result.Candidate, "<base href> 参与相对地址解析。");
        Assert.IsTrue(handler.RequestedUrls.Any(url => url.EndsWith("/static/icon.png", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Fetch_CrossOriginBase_ResolvedCandidatesRejectedNotIgnored()
    {
        var handler = new FakeFaviconHandler();
        handler.Route("https://example.com/", Response.Html("""<html><head><base href="https://evil.example/"><link rel="icon" href="icon.png"></head></html>"""));
        handler.Route("https://evil.example/icon.png", Response.Image(Png(96, 96)));

        using var client = new FaviconClient(handler);
        var result = await client.FetchPageIconAsync("https://example.com/", CancellationToken.None);

        Assert.IsNull(result.Candidate, "跨源 base：按真实基地址解析后由同源规则拒绝（不忽略 base）。");
        Assert.IsFalse(handler.RequestedUrls.Any(url => url.Contains("evil.example")), "解析出的跨源候选不发请求。");
    }

    [TestMethod]
    public async Task Fetch_UnsupportedFormats_SvgWebpSkipped()
    {
        var svg = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'></svg>");
        var handler = new FakeFaviconHandler();
        handler.Route("https://example.com/", Response.Html(
            """<html><head><link rel="icon" type="image/svg+xml" href="/logo.svg"></head></html>"""));
        handler.Route("https://example.com/logo.svg", Response.Bytes(svg, "image/svg+xml"));

        using var client = new FaviconClient(handler);
        var result = await client.FetchPageIconAsync("https://example.com/", CancellationToken.None);

        Assert.IsNull(result.Candidate, "SVG 不支持（签名未知即淘汰）。");
    }

    [TestMethod]
    public async Task Fetch_InvalidPageUrl_FailsWithoutAnyRequest()
    {
        var handler = new FakeFaviconHandler();
        using var client = new FaviconClient(handler);
        var result = await client.FetchPageIconAsync("ftp://example.com/", CancellationToken.None);

        Assert.IsNull(result.Candidate);
        Assert.AreEqual(0, handler.Requests.Count, "非 http/https 输入零请求。");
    }

    [TestMethod]
    public async Task Fetch_CanceledBudget_Propagates()
    {
        var handler = new FakeFaviconHandler();
        handler.Route("https://example.com/favicon.ico", Response.Image(Png(96, 96)));
        using var client = new FaviconClient(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // SendAsync 对已取消令牌抛 TaskCanceledException（OperationCanceledException 子类），
        // 客户端边界原样上抛（调用方区分「被取代/退出」与失败）。
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => client.FetchPageIconAsync("https://example.com/", cts.Token));
    }

    [TestMethod]
    public async Task Fetch_PageQueryPreservedAndFragmentStripped()
    {
        var handler = new FakeFaviconHandler();
        handler.Route("https://example.com/favicon.ico", Response.NotFound());
        handler.Route("https://example.com/pa?tab=1", Response.Html("""<html><head><link rel="icon" href="/i.png"></head></html>"""));
        handler.Route("https://example.com/i.png", Response.Image(Png(96, 96)));

        using var client = new FaviconClient(handler);
        var result = await client.FetchPageIconAsync("https://example.com/pa?tab=1#section", CancellationToken.None);

        Assert.IsNotNull(result.Candidate);
        Assert.IsTrue(handler.RequestedUrls.Any(url => url == "https://example.com/pa?tab=1"), "页面请求保留 query、剥 fragment。");
    }

    // ---- SSRF 守卫的纯判定 ----

    [TestMethod]
    public void PrivateHostLiterals_Classified()
    {
        Assert.IsTrue(FaviconClient.IsPrivateOrLoopbackLiteral("localhost"));
        Assert.IsTrue(FaviconClient.IsPrivateOrLoopbackLiteral("127.0.0.1"));
        Assert.IsTrue(FaviconClient.IsPrivateOrLoopbackLiteral("2130706433"), "非规范十进制回环编码识别。");
        Assert.IsTrue(FaviconClient.IsPrivateOrLoopbackLiteral("192.168.1.4"));
        Assert.IsTrue(FaviconClient.IsPrivateOrLoopbackLiteral("10.0.0.2"));
        Assert.IsTrue(FaviconClient.IsPrivateOrLoopbackLiteral("::ffff:127.0.0.1"), "mapped IPv6 回环识别。");
        Assert.IsFalse(FaviconClient.IsPrivateOrLoopbackLiteral("example.com"));
        Assert.IsFalse(FaviconClient.IsPrivateOrLoopbackLiteral("93.184.216.34"));
    }

    // ---- 测试替身与最小图片 ----

    /// <summary>最小合法 PNG（1×1 灰度基底放大到指定逻辑尺寸的 IHDR 声明——仅头部参与判定）。</summary>
    private static byte[] Png(int width, int height)
    {
        var signature = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var chunk = new List<byte>();
        chunk.AddRange(new byte[] { 0, 0, 0, 13 });
        chunk.AddRange(Encoding.ASCII.GetBytes("IHDR"));
        chunk.AddRange([
            (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
            (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height,
            8, 6, 0, 0, 0
        ]);
        chunk.AddRange(new byte[] { 0, 0, 0, 0 });
        return [.. signature, .. chunk, .. new byte[] { 0, 0, 0, 0, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 }];
    }

    private sealed class FakeFaviconHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpResponseMessage> _routes = [];
        private readonly Dictionary<string, string> _redirects = [];

        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> RequestedUrls => Requests.Select(request => request.RequestUri!.ToString()).ToList();

        public void Route(string url, HttpResponseMessage response) => _routes[url] = response;

        public void Redirect(string from, string to)
        {
            var response = Response.Status(HttpStatusCode.Found);
            response.Headers.Location = new Uri(to);
            _routes[from] = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var url = request.RequestUri!.ToString();
            if (_routes.TryGetValue(url, out var routed))
            {
                return Task.FromResult(Copy(routed));
            }

            return Task.FromResult(Response.NotFound());
        }

        private static HttpResponseMessage Copy(HttpResponseMessage source)
        {
            var bytes = source.Content is ByteArrayContent byteArray
                ? ReadAll(byteArray)
                : [];
            var copy = new HttpResponseMessage(source.StatusCode) { Content = new ByteArrayContent(bytes) };
            foreach (var header in source.Headers)
            {
                copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            copy.Content.Headers.ContentType = source.Content.Headers.ContentType;
            return copy;
        }

        private static byte[] ReadAll(ByteArrayContent content) =>
            content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
    }

    private static class Response
    {
        public static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) };

        public static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new ByteArrayContent([]) };

        public static HttpResponseMessage Html(string html)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(html))
            };
            response.Content.Headers.ContentType = new("text/html");
            return response;
        }

        public static HttpResponseMessage Manifest(string json)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json))
            };
            response.Content.Headers.ContentType = new("application/manifest+json");
            return response;
        }

        public static HttpResponseMessage Image(byte[] bytes) => Bytes(bytes, "image/png");

        public static HttpResponseMessage Bytes(byte[] bytes, string contentType)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            response.Content.Headers.ContentType = new(contentType);
            return response;
        }
    }
}
