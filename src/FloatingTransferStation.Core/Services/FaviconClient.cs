using System.Net;
using System.Net.Sockets;

namespace FloatingTransferStation.Services;

public enum FaviconFailureKind
{
    /// <summary>无失败（Candidate 非 null）。</summary>
    None = 0,

    /// <summary>输入非法（非 http/https 绝对地址）或阶梯全部候选淘汰。</summary>
    Exhausted = 1,

    /// <summary>网络层失败（DNS/连接/超时——首个请求即失败时无从区分候选）。</summary>
    Network = 2,

    /// <summary>429/Retry-After：停梯并交由调用方记负缓存。</summary>
    RateLimited = 3
}

/// <summary>选中的 favicon 候选：字节、格式与头部声明的内联尺寸（WIC 解码真相以此预筛）。</summary>
public sealed record FaviconCandidate(
    byte[] Bytes,
    FaviconImageFormat Format,
    int Width,
    int Height,
    int SourceRank);

/// <summary>抓取结果：Candidate=null 时 Failure 指明类别（None 不会与 null 同时出现）。</summary>
public sealed record FaviconFetchResult(FaviconCandidate? Candidate, FaviconFailureKind Failure)
{
    public static FaviconFetchResult Success(FaviconCandidate candidate) => new(candidate, FaviconFailureKind.None);

    public static FaviconFetchResult Fail(FaviconFailureKind failure) => new(null, failure);
}

/// <summary>
/// favicon 同源阶梯抓取客户端（#30 修订，Issue #206；契约新增条款）：只向目标页同源
/// （scheme+host+port；同主机 http→https 升级允许、https→http 降级拒绝）请求，绝不
/// 发第三方图标服务；不发 Cookie/凭据/Referer；UseCookies=false；显式产品 UA。
/// 阶梯：A /favicon.ico → B 页面 HTML link（用户输入的页面路径）→ C 页面声明的
/// manifest → D 惯例 manifest 路径；data: 候选直接入池不计网络；收集式（拿到像素
/// 足额帧才提前止）。重定向手动跟随（301/302/303/307/308、逐跳仅 http/https、每资源
/// ≤3 跳、逐跳重校同源）；每条目 HTTP ≤8、图片候选 ≤4、字节上限作用于解压后累计。
/// 公网目标的连接经 ConnectCallback 校验解析后 IP（拒绝 loopback/私网/链路本地，
/// 覆盖域名解析到内网的普通 SSRF 面；用户显式添加的内网主机字面量不受限；完整 DNS
/// rebinding 防护非本轮目标）。HttpMessageHandler 注入离线可测（#26/#28 先例）。
/// </summary>
public sealed class FaviconClient : IDisposable
{
    /// <summary>目标尺寸下限 T：48 槽 × 200% DPI；满足该值的帧像素足额。</summary>
    public const int TargetSize = 96;

    /// <summary>单帧像素上限（防压缩炸弹解码 OOM——解码前按头部尺寸预筛）。</summary>
    public const int MaxFramePixels = 4 * 1024 * 1024;

    public const int MaxImageBytes = 1024 * 1024;
    public const int MaxHtmlBytes = 512 * 1024;
    public const int MaxManifestBytes = 256 * 1024;
    public const int MaxRequestsPerEntry = 8;
    public const int MaxRedirectsPerResource = 3;
    public const int MaxImageCandidates = 4;
    public const int MaxIcoFramesInspected = 16;
    internal static readonly TimeSpan PerRequestTimeout = TimeSpan.FromSeconds(5);

    // 候选来源序（同像素次级键）：rel=icon > /favicon.ico > manifest(any) > apple-touch-icon。
    internal const int RankHtmlIcon = 0;
    internal const int RankRootFavicon = 1;
    internal const int RankManifest = 2;
    internal const int RankAppleTouch = 3;

    private static readonly string[] HtmlContentTypes = ["text/html", "application/xhtml+xml"];

    private readonly HttpClient _httpClient;
    private readonly HttpMessageHandler? _ownedHandler;

    /// <summary>
    /// 每请求执行资格注入点（每 origin 1 × 全局并发，由宿主调度器承载）：客户端在
    /// 发出每个网络请求前调用（含重定向跳），返回的 IAsyncDisposable 释放时归还。
    /// null=不序列化（测试直跑）。等待 origin 许可不占用条目预算——预算由调用方的
    /// entryBudget 承载、首个请求取得资格后才开始生效（链接令牌逐请求绑定）。
    /// </summary>
    public Func<Uri, CancellationToken, Task<IAsyncDisposable>>? OriginPermitFactory { get; set; }

    public FaviconClient(HttpMessageHandler? handler = null)
    {
        handler ??= CreateProductionHandler();
        _ownedHandler = handler;
        _httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            // 红线：绝不携带 Cookie/凭据/Referer。
            DefaultRequestHeaders =
            {
                UserAgent = { new System.Net.Http.Headers.ProductInfoHeaderValue("FloatingTransferStation", ProductIdentity.Version) }
            }
        };
    }

    public void Dispose()
    {
        _ownedHandler?.Dispose();
        _httpClient.Dispose();
    }

    private static SocketsHttpHandler CreateProductionHandler() => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        ConnectCallback = GuardPublicTargetConnection
    };

    /// <summary>
    /// 同源阶梯抓取入口。entryBudget：单条目总预算（宿主在取得首个执行资格后启动）；
    /// heldPermit：宿主取得的首个执行资格（覆盖首个请求的 origin，跨 origin 时在跳界
    /// 处释放换取新许可——同一时刻至多持有一个 origin 许可，结构上排除交叉死锁）。
    /// 取消（OperationCanceledException）向上抛出，由调用方区分「被取代/退出」与失败。
    /// </summary>
    public async Task<FaviconFetchResult> FetchPageIconAsync(
        string pageUrl, CancellationToken entryBudget, IAsyncDisposable? heldPermit = null)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) ||
            page.Scheme is not ("http" or "https"))
        {
            if (heldPermit is not null)
            {
                await heldPermit.DisposeAsync().ConfigureAwait(false);
            }

            return FaviconFetchResult.Fail(FaviconFailureKind.Exhausted);
        }

        var pageOrigin = new Uri($"{page.Scheme}://{page.IdnHost}:{page.Port}/");
        var context = new FetchContext(pageOrigin, entryBudget) { CurrentPermit = heldPermit, CurrentPermitOrigin = heldPermit is null ? null : OriginOf(pageOrigin) };
        var pool = new List<FaviconCandidate>();

        try
        {
            // A：惯例根图标（404 只淘汰该候选，继续阶梯）。
            await CollectAsync(pool, context, new Uri($"{pageOrigin.AbsoluteUri.TrimEnd('/')}/favicon.ico"), RankRootFavicon);
            if (HasSufficient(pool))
            {
                return FaviconFetchResult.Success(Select(pool));
            }

            // B：用户页面路径的 HTML link（保留 query、剥 fragment）。
            var builder = new UriBuilder(page) { Fragment = string.Empty };
            var (htmlLinks, manifestUrl) = await FetchHtmlLinksAsync(pool, context, builder.Uri);
            if (HasSufficient(pool))
            {
                return FaviconFetchResult.Success(Select(pool));
            }

            // C：页面声明的 manifest。
            if (manifestUrl is not null)
            {
                await CollectManifestAsync(pool, context, manifestUrl);
                if (HasSufficient(pool))
                {
                    return FaviconFetchResult.Success(Select(pool));
                }
            }

            // D：惯例 manifest 路径。
            foreach (var conventional in new[]
                     {
                         new Uri($"{pageOrigin.AbsoluteUri.TrimEnd('/')}/manifest.webmanifest"),
                         new Uri($"{pageOrigin.AbsoluteUri.TrimEnd('/')}/manifest.json")
                     })
            {
                await CollectManifestAsync(pool, context, conventional);
                if (HasSufficient(pool))
                {
                    return FaviconFetchResult.Success(Select(pool));
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or SocketException)
        {
            if (pool.Count == 0)
            {
                return FaviconFetchResult.Fail(FaviconFailureKind.Network);
            }
        }
        finally
        {
            if (context.CurrentPermit is { } remaining)
            {
                context.CurrentPermit = null;
                await remaining.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (context.RateLimited)
        {
            return FaviconFetchResult.Fail(FaviconFailureKind.RateLimited);
        }

        return pool.Count > 0
            ? FaviconFetchResult.Success(Select(pool))
            : FaviconFetchResult.Fail(context.MadeAnyRequest ? FaviconFailureKind.Exhausted : FaviconFailureKind.Network);
    }

    private static Uri OriginOf(Uri origin) => new($"{origin.Scheme}://{origin.IdnHost}:{origin.Port}/");

    // ---- 候选收集与选优 ----

    private sealed class FetchContext(Uri pageOrigin, CancellationToken entryBudget)
    {
        public Uri PageOrigin { get; } = pageOrigin;
        public CancellationToken EntryBudget { get; } = entryBudget;
        public int RequestCount { get; set; }
        public int HtmlCandidates { get; set; }
        public bool RateLimited { get; set; }
        public bool MadeAnyRequest { get; set; }

        /// <summary>当前持有的执行资格（同一时刻至多一个 origin——交叉死锁的结构性排除）。</summary>
        public IAsyncDisposable? CurrentPermit { get; set; }

        public Uri? CurrentPermitOrigin { get; set; }
    }

    /// <summary>确保当前许可覆盖请求 origin：跨 origin 先释放再申请（预算令牌绑定等待）。</summary>
    private async Task EnsurePermitAsync(FetchContext context, Uri requestTarget)
    {
        var requestOrigin = OriginOf(requestTarget);
        if (context.CurrentPermit is not null && context.CurrentPermitOrigin?.AbsoluteUri == requestOrigin.AbsoluteUri)
        {
            return;
        }

        if (context.CurrentPermit is not null)
        {
            var stale = context.CurrentPermit;
            context.CurrentPermit = null;
            context.CurrentPermitOrigin = null;
            await stale.DisposeAsync().ConfigureAwait(false);
        }

        if (OriginPermitFactory is { } factory)
        {
            context.CurrentPermit = await factory(requestOrigin, context.EntryBudget).ConfigureAwait(false);
            context.CurrentPermitOrigin = requestOrigin;
        }
    }

    private static bool HasSufficient(List<FaviconCandidate> pool) =>
        pool.Any(candidate => Math.Min(candidate.Width, candidate.Height) >= TargetSize);

    /// <summary>选优（#30 修订）：像素足额为主键——足额中最小档、不足中最大档；
    /// 同档像素积更小者；最后按来源序（rel=icon >/favicon.ico>manifest>apple-touch）。</summary>
    private static FaviconCandidate Select(List<FaviconCandidate> pool) => pool
        .OrderByDescending(candidate => Math.Min(candidate.Width, candidate.Height) >= TargetSize)
        .ThenBy(candidate => Math.Min(candidate.Width, candidate.Height) >= TargetSize
            ? Math.Min(candidate.Width, candidate.Height)
            : -Math.Min(candidate.Width, candidate.Height))
        .ThenBy(candidate => (long)candidate.Width * candidate.Height)
        .ThenBy(candidate => candidate.SourceRank)
        .First();

    private async Task CollectAsync(List<FaviconCandidate> pool, FetchContext context, Uri target, int rank)
    {
        if (pool.Count >= MaxImageCandidates || context.RequestCount >= MaxRequestsPerEntry)
        {
            return;
        }

        if (!IsAllowedTarget(context.PageOrigin, target))
        {
            return;
        }

        var download = await DownloadAsync(context, target, MaxImageBytes);
        if (download is null)
        {
            return;
        }

        AddCandidate(pool, context, download.Value.Bytes, rank);
    }

    private void AddCandidate(List<FaviconCandidate> pool, FetchContext context, byte[] bytes, int rank)
    {
        if (pool.Count >= MaxImageCandidates)
        {
            return;
        }

        var format = FaviconImageInfo.DetectFormat(bytes);
        if (format == FaviconImageFormat.Unknown)
        {
            return;
        }

        if (FaviconImageInfo.ReadIntrinsicSize(bytes, format) is not { } size)
        {
            return;
        }

        if ((long)size.Width * size.Height > MaxFramePixels)
        {
            return;
        }

        pool.Add(new FaviconCandidate(bytes, format, size.Width, size.Height, rank));
    }

    // ---- HTML 解析（HtmlAgilityPack；禁正则结构解析）----

    private async Task<(List<HtmlIconLink> Links, Uri? ManifestUrl)> FetchHtmlLinksAsync(
        List<FaviconCandidate> pool, FetchContext context, Uri pageUrl)
    {
        if (context.RequestCount >= MaxRequestsPerEntry)
        {
            return ([], null);
        }

        var download = await DownloadAsync(context, pageUrl, MaxHtmlBytes, htmlAllowed: true);
        if (download is null)
        {
            return ([], null);
        }

        var (links, manifestHref, finalUrl) = ParseHtmlLinks(download.Value.Bytes, download.Value.FinalUrl);
        foreach (var link in links)
        {
            if (pool.Count >= MaxImageCandidates)
            {
                break;
            }

            // 相对地址按最终响应 URL 解析；首个 <base href>（跨源时按真实基地址解析，
            // 越界候选由 IsAllowedTarget 拒绝——不是忽略 base）。
            if (!Uri.TryCreate(finalUrl, link.Href, out var absolute))
            {
                continue;
            }

            if (link.Href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                // data: 直接解码不发网络，同样计入候选数与字节上限。
                if (TryDecodeDataUri(link.Href, out var bytes))
                {
                    AddCandidate(pool, context, bytes, link.Rank);
                }

                continue;
            }

            await CollectAsync(pool, context, absolute, link.Rank);
        }

        var manifest = manifestHref is not null && Uri.TryCreate(finalUrl, manifestHref, out var manifestAbsolute)
            ? manifestAbsolute
            : null;
        return (links, manifest);
    }

    private sealed record HtmlIconLink(string Href, int Rank);

    private static (List<HtmlIconLink> Links, string? ManifestHref, Uri BaseUrl) ParseHtmlLinks(byte[] htmlBytes, Uri responseUrl)
    {
        // 容错解码（截断处不完整尾序列由解码器替换）；仅 HTML 文档进入本函数。
        var html = System.Text.Encoding.UTF8.GetString(htmlBytes);
        var document = new HtmlAgilityPack.HtmlDocument();
        document.LoadHtml(html);

        // 属性实体解码一次（HAP 原样返回属性串；WebUtility.HtmlDecode 承载解码）。
        static string Decoded(HtmlAgilityPack.HtmlNode node, string name, string fallback) =>
            System.Net.WebUtility.HtmlDecode(node.GetAttributeValue(name, fallback));

        var baseUrl = responseUrl;
        var baseNode = document.DocumentNode.SelectSingleNode("//base[@href]");
        if (baseNode is not null && Decoded(baseNode, "href", string.Empty) is { Length: > 0 } baseHref &&
            Uri.TryCreate(responseUrl, baseHref, out var resolvedBase))
        {
            baseUrl = resolvedBase;
        }

        var links = new List<HtmlIconLink>();
        string? manifestHref = null;
        var linkNodes = document.DocumentNode.SelectNodes("//link[@href]");
        if (linkNodes is not null)
        {
            foreach (var node in linkNodes)
            {
                var rel = Decoded(node, "rel", string.Empty);
                var href = Decoded(node, "href", string.Empty);
                if (rel.Length == 0 || href.Length == 0)
                {
                    continue;
                }

                // rel 按 ASCII 空白分词、大小写不敏感（禁 Contains("icon")）。
                var tokens = rel.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                var isManifest = tokens.Any(token => token.Equals("manifest", StringComparison.OrdinalIgnoreCase));
                if (isManifest && manifestHref is null)
                {
                    manifestHref = href;
                    continue;
                }

                var isIcon = tokens.Any(token => token.Equals("icon", StringComparison.OrdinalIgnoreCase) ||
                                                 token.Equals("shortcut icon", StringComparison.OrdinalIgnoreCase));
                var isAppleTouch = tokens.Any(token => token.Equals("apple-touch-icon", StringComparison.OrdinalIgnoreCase) ||
                                                       token.Equals("apple-touch-icon-precomposed", StringComparison.OrdinalIgnoreCase));
                if (isIcon)
                {
                    links.Add(new HtmlIconLink(href, RankHtmlIcon));
                }
                else if (isAppleTouch)
                {
                    links.Add(new HtmlIconLink(href, RankAppleTouch));
                }
            }
        }

        return (links, manifestHref, baseUrl);
    }

    private static bool TryDecodeDataUri(string href, out byte[] bytes)
    {
        bytes = [];
        const string prefix = "data:";
        if (href.Length <= prefix.Length || !href.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var comma = href.IndexOf(',', prefix.Length);
        if (comma < 0)
        {
            return false;
        }

        var metadata = href[prefix.Length..comma];
        var payload = href[(comma + 1)..];
        var isBase64 = metadata.Contains(";base64", StringComparison.OrdinalIgnoreCase);
        try
        {
            if (isBase64)
            {
                // 解码前按 base64 长度预判，拒绝明显超限的负载。
                if (payload.Length > MaxImageBytes * 4 / 3 + 4)
                {
                    return false;
                }

                bytes = Convert.FromBase64String(payload);
            }
            else
            {
                bytes = Uri.UnescapeDataString(payload) is { Length: > 0 } decoded
                    ? System.Text.Encoding.UTF8.GetBytes(decoded)
                    : [];
            }
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or UriFormatException)
        {
            return false;
        }

        return bytes.Length > 0 && bytes.Length <= MaxImageBytes;
    }

    // ---- manifest（JsonDocument，MaxDepth 有界）----

    private async Task CollectManifestAsync(List<FaviconCandidate> pool, FetchContext context, Uri manifestUrl)
    {
        if (pool.Count >= MaxImageCandidates || context.RequestCount >= MaxRequestsPerEntry)
        {
            return;
        }

        if (!IsAllowedTarget(context.PageOrigin, manifestUrl))
        {
            return;
        }

        var download = await DownloadAsync(context, manifestUrl, MaxManifestBytes);
        if (download is null)
        {
            return;
        }

        List<string> iconHrefs;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(
                System.Text.Encoding.UTF8.GetString(download.Value.Bytes),
                new System.Text.Json.JsonDocumentOptions { MaxDepth = 32 });
            if (!document.RootElement.TryGetProperty("icons", out var icons) || icons.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return;
            }

            iconHrefs = [];
            foreach (var icon in icons.EnumerateArray())
            {
                if (icon.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !icon.TryGetProperty("src", out var src) ||
                    src.ValueKind != System.Text.Json.JsonValueKind.String)
                {
                    continue;
                }

                // purpose 缺失按 any；仅 maskable/monochrome 跳过。
                if (icon.TryGetProperty("purpose", out var purpose) && purpose.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var purposes = purpose.GetString()?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];
                    var usable = purposes.Length == 0 ||
                                 purposes.Any(p => p.Equals("any", StringComparison.OrdinalIgnoreCase));
                    if (!usable)
                    {
                        continue;
                    }
                }

                iconHrefs.Add(src.GetString() ?? string.Empty);
            }
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or ArgumentException)
        {
            return;
        }

        foreach (var href in iconHrefs)
        {
            if (pool.Count >= MaxImageCandidates)
            {
                break;
            }

            if (Uri.TryCreate(download.Value.FinalUrl, href, out var absolute) &&
                IsAllowedTarget(context.PageOrigin, absolute))
            {
                await CollectAsync(pool, context, absolute, RankManifest);
            }
        }
    }

    // ---- 网络层：手动重定向 + 同源守卫 + 有界读取 ----

    private async Task<(byte[] Bytes, Uri FinalUrl)?> DownloadAsync(
        FetchContext context, Uri target, int byteLimit, bool htmlAllowed = false)
    {
        var current = target;
        for (var redirect = 0; redirect <= MaxRedirectsPerResource; redirect++)
        {
            if (context.RequestCount >= MaxRequestsPerEntry || context.RateLimited)
            {
                return null;
            }

            if (!IsAllowedTarget(context.PageOrigin, current))
            {
                return null;
            }

            context.RequestCount++;
            context.MadeAnyRequest = true;

            HttpResponseMessage? response = null;
            try
            {
                // 执行资格按请求 origin 对齐（跨 origin 跳界处先释放再申请）。
                await EnsurePermitAsync(context, current).ConfigureAwait(false);

                // 单请求超时=min(5s, 剩余条目预算)：linked CTS 逐请求绑定。
                using var perRequest = CancellationTokenSource.CreateLinkedTokenSource(context.EntryBudget);
                perRequest.CancelAfter(PerRequestTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, perRequest.Token)
                    .ConfigureAwait(false);

                if ((int)response.StatusCode == 429)
                {
                    context.RateLimited = true;
                    return null;
                }

                if ((int)response.StatusCode is >= 300 and < 400 &&
                    response.Headers.Location is { } location)
                {
                    if (redirect == MaxRedirectsPerResource)
                    {
                        return null;
                    }

                    // 逐跳仅 http/https；同源/升级校验在循环顶对下一跳执行。
                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (next.Scheme is not ("http" or "https"))
                    {
                        return null;
                    }

                    current = next;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                if (!htmlAllowed)
                {
                    var contentType = response.Content.Headers.ContentType?.MediaType;
                    // 图片候选按签名裁决；明文 HTML 冒充（soft-404）直接淘汰。
                    if (contentType is not null && HtmlContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
                    {
                        return null;
                    }
                }

                var bytes = await ReadBoundedAsync(response, byteLimit, perRequest.Token, htmlAllowed)
                    .ConfigureAwait(false);
                return bytes is null ? null : (bytes, current);
            }
            catch (OperationCanceledException)
            {
                if (context.EntryBudget.IsCancellationRequested)
                {
                    throw;
                }

                // 单请求超时＝该候选失败，继续阶梯。
                return null;
            }
            finally
            {
                response?.Dispose();
            }
        }

        return null;
    }

    /// <summary>
    /// 有界读取：上限作用于实际读到的字节（Content-Length 只作预判）、读流计数。
    /// 图片/manifest 超限＝候选失败；HTML 明确截断解析（截断处不完整尾序列由解码器
    /// 容错，解析继续——512KiB 前缀足以承载 link/base）。
    /// </summary>
    private static async Task<byte[]?> ReadBoundedAsync(
        HttpResponseMessage response, int byteLimit, CancellationToken token, bool truncateOverLimit)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[8192];
        using var output = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > byteLimit)
            {
                if (!truncateOverLimit)
                {
                    return null;
                }

                var remaining = (int)(byteLimit - output.Length);
                if (remaining > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, remaining), token).ConfigureAwait(false);
                }

                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }

        return output.ToArray();
    }

    // ---- 同源与 SSRF 守卫 ----

    /// <summary>同源（scheme+host+port）或同主机 http→https 升级；降级/跨源拒绝。</summary>
    internal static bool IsAllowedTarget(Uri pageOrigin, Uri candidate)
    {
        if (candidate.Scheme is not ("http" or "https"))
        {
            return false;
        }

        if (!string.Equals(candidate.IdnHost, pageOrigin.IdnHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (candidate.Scheme == pageOrigin.Scheme && candidate.Port == pageOrigin.Port)
        {
            return true;
        }

        // 同主机升级：http 页面 → https 候选（端口随 scheme 走）。
        return pageOrigin.Scheme == "http" && candidate.Scheme == "https";
    }

    /// <summary>
    /// 连接时校验解析后 IP：目标主机字面量本身是内网/loopback（用户显式添加的内网站点）
    /// 放行；否则解析到 loopback/私网/链路本地的连接一律拒绝（覆盖域名解析到内网的
    /// 普通 SSRF 面；完整 DNS rebinding 防护非本轮目标）。按主机字面量与解析地址分类，
    /// 覆盖 IPv4/IPv6/mapped IPv6 与非规范十进制编码。
    /// </summary>
    private static ValueTask<Stream> GuardPublicTargetConnection(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var connectHost = context.DnsEndPoint.Host;
        var requestHost = context.InitialRequestMessage.RequestUri?.IdnHost ?? connectHost;

        if (IsPrivateOrLoopbackLiteral(requestHost))
        {
            // 用户显式目标即内网：直连（origin 策略允许）。
            return ConnectDirectAsync(connectHost, context.DnsEndPoint.Port, cancellationToken);
        }

        if (IsPrivateOrLoopbackLiteral(connectHost))
        {
            // 公网目标解析到内网/loopback（含 2130706433 一类非规范编码）：拒绝。
            throw new HttpRequestException("公网目标的解析地址落在了内网/回环段，已拒绝连接。");
        }

        return ConnectDirectAsync(connectHost, context.DnsEndPoint.Port, cancellationToken);
    }

    private static async ValueTask<Stream> ConnectDirectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        socket.NoDelay = true;
        try
        {
            await socket.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>主机字面量的私网/回环/链路本地判定（IP 解析后含 mapped IPv6）。</summary>
    public static bool IsPrivateOrLoopbackLiteral(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(host, out var address))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.Equals(IPAddress.Loopback) ||
               address.Equals(IPAddress.IPv6Loopback) ||
               address.IsPrivateNetwork() ||
               address.IsLinkLocal();
    }
}

file static class IPAddressPrivateExtensions
{
    public static bool IsPrivateNetwork(this IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork &&
        (address.GetAddressBytes() is [10, ..] or [172, >= 16 and <= 31, ..] or [192, 168, ..]);

    public static bool IsLinkLocal(this IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork
            ? address.GetAddressBytes() is [169, 254, ..]
            : address.IsIPv6LinkLocal;
}

