using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class DeepSeekClientTests
{
    private const string ApiKey = "sk-test-fake-key";

    private static string SuccessBody(string model = "deepseek-flash") => """
        {"id":"1","model":"DEEPSEEK_MODEL","choices":[{"index":0,"message":{"role":"assistant","content":"pong"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
        """.Replace("DEEPSEEK_MODEL", model);

    private static DeepSeekChatRequest PingRequest(string model = "deepseek-flash") => new(
        model,
        [new DeepSeekChatMessage("user", "ping")]);

    private static FakeHttpMessageHandler RespondWith(HttpStatusCode status, string body) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }));

    [TestMethod]
    public async Task ChatAsync_WithEmptyApiKey_FailsLocallyWithoutSendingRequest()
    {
        var handler = new FakeHttpMessageHandler(
            (_, _) => throw new AssertFailedException("空 key 不得发起请求。"));
        var client = new DeepSeekClient(handler);

        var result = await client.ChatAsync(PingRequest(), "  ");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DeepSeekErrorKind.EmptyApiKey, result.ErrorKind);
        Assert.AreEqual("请先填写 API Key。", result.ErrorMessage);
        Assert.AreEqual(0, handler.CallCount);
    }

    [TestMethod]
    public async Task ChatAsync_Success_ParsesContentFinishReasonUsageAndModel()
    {
        var handler = RespondWith(HttpStatusCode.OK, SuccessBody());
        var client = new DeepSeekClient(handler);

        var result = await client.ChatAsync(PingRequest(), ApiKey);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("pong", result.Content);
        Assert.AreEqual("stop", result.FinishReason);
        Assert.IsNotNull(result.Usage);
        Assert.AreEqual(1, result.Usage.PromptTokens);
        Assert.AreEqual(1, result.Usage.CompletionTokens);
        Assert.AreEqual(2, result.Usage.TotalTokens);
        Assert.AreEqual("deepseek-flash", result.Model);
        Assert.AreEqual(DeepSeekErrorKind.None, result.ErrorKind);
    }

    [TestMethod]
    public async Task ChatAsync_SendsBearerAuthToOfficialEndpointAndSerializesRequest()
    {
        var handler = RespondWith(HttpStatusCode.OK, SuccessBody());
        var client = new DeepSeekClient(handler);

        await client.ChatAsync(
            new DeepSeekChatRequest(
                DeepSeekClient.DefaultModel,
                [new DeepSeekChatMessage("user", "ping")],
                MaxTokens: 16,
                Thinking: DeepSeekThinking.Disabled),
            ApiKey);

        var request = handler.LastRequest;
        Assert.IsNotNull(request);
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("https://api.deepseek.com/chat/completions", request.RequestUri!.ToString());
        Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
        Assert.AreEqual(ApiKey, request.Headers.Authorization.Parameter);
        var body = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        Assert.AreEqual(DeepSeekClient.DefaultModel, body.GetProperty("model").GetString());
        Assert.AreEqual(16, body.GetProperty("max_tokens").GetInt32());
        Assert.AreEqual("disabled", body.GetProperty("thinking").GetProperty("type").GetString());
        Assert.AreEqual("user", body.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.AreEqual("ping", body.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [TestMethod]
    public async Task ChatAsync_OptionalFieldsLeftNull_AreOmittedFromBody()
    {
        var handler = RespondWith(HttpStatusCode.OK, SuccessBody());
        var client = new DeepSeekClient(handler);

        await client.ChatAsync(PingRequest(), ApiKey);

        var body = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        Assert.IsFalse(body.TryGetProperty("max_tokens", out _));
        Assert.IsFalse(body.TryGetProperty("temperature", out _));
        Assert.IsFalse(body.TryGetProperty("thinking", out _));
    }

    [TestMethod]
    public async Task ChatAsync_MapsOfficialErrorCodesToChineseMessages()
    {
        var cases = new (HttpStatusCode Status, string Body, DeepSeekErrorKind Kind, string Message)[]
        {
            (HttpStatusCode.Unauthorized, """{"error":{"message":"auth failed"}}""",
                DeepSeekErrorKind.InvalidApiKey, "API Key 无效。"),
            (HttpStatusCode.PaymentRequired, """{"error":{"message":"insufficient balance"}}""",
                DeepSeekErrorKind.InsufficientBalance, "余额不足，请前往 platform.deepseek.com 充值。"),
            (HttpStatusCode.TooManyRequests, """{"error":{"message":"rate limit"}}""",
                DeepSeekErrorKind.RateLimited, "请求过于频繁，请稍后再试。"),
            (HttpStatusCode.InternalServerError, """{"error":{"message":"server fault"}}""",
                DeepSeekErrorKind.ServerError, "DeepSeek 服务暂时不可用。"),
            (HttpStatusCode.ServiceUnavailable, "",
                DeepSeekErrorKind.ServerError, "DeepSeek 服务暂时不可用。"),
        };
        foreach (var (status, body, kind, message) in cases)
        {
            var handler = RespondWith(status, body);
            var client = new DeepSeekClient(handler);

            var result = await client.ChatAsync(PingRequest(), ApiKey);

            Assert.IsFalse(result.IsSuccess, $"HTTP {(int)status} 不应成功。");
            Assert.AreEqual(kind, result.ErrorKind, $"HTTP {(int)status} 错误类别不符。");
            Assert.AreEqual(message, result.ErrorMessage, $"HTTP {(int)status} 文案不符。");
        }
    }

    [TestMethod]
    public async Task ChatAsync_OtherErrorStatus_ReportsStatusCodeWithServerDetail()
    {
        var handler = RespondWith(HttpStatusCode.BadRequest, """{"error":{"message":"格式错误"}}""");
        var client = new DeepSeekClient(handler);

        var result = await client.ChatAsync(PingRequest(), ApiKey);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DeepSeekErrorKind.Unknown, result.ErrorKind);
        Assert.AreEqual("请求失败（HTTP 400）：格式错误", result.ErrorMessage);
    }

    [TestMethod]
    public async Task ChatAsync_MalformedSuccessBody_FailsAsUnknown()
    {
        var handler = RespondWith(HttpStatusCode.OK, """{"nope":true}""");
        var client = new DeepSeekClient(handler);

        var result = await client.ChatAsync(PingRequest(), ApiKey);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DeepSeekErrorKind.Unknown, result.ErrorKind);
        Assert.AreEqual("DeepSeek 返回了无法解析的响应。", result.ErrorMessage);
    }

    [TestMethod]
    public async Task ChatAsync_TimeoutWithoutCallerCancellation_MapsToTimeout()
    {
        var handler = new FakeHttpMessageHandler((_, _) => throw new TaskCanceledException());
        var client = new DeepSeekClient(handler);

        var result = await client.ChatAsync(PingRequest(), ApiKey);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DeepSeekErrorKind.Timeout, result.ErrorKind);
        Assert.AreEqual("连接超时，请检查网络。", result.ErrorMessage);
    }

    [TestMethod]
    public async Task ChatAsync_ConnectTimeoutAsRequestException_MapsToTimeout()
    {
        // 连接超时在部分运行时路径以「内层 TimeoutException 的请求异常」呈现。
        var handler = new FakeHttpMessageHandler(
            (_, _) => throw new HttpRequestException("connect timed out", new TimeoutException()));
        var client = new DeepSeekClient(handler);

        var result = await client.ChatAsync(PingRequest(), ApiKey);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DeepSeekErrorKind.Timeout, result.ErrorKind);
        Assert.AreEqual("连接超时，请检查网络。", result.ErrorMessage);
    }

    [TestMethod]
    public async Task ChatAsync_EmptyChoicesArray_FailsAsUnknownInsteadOfCrashing()
    {
        var handler = RespondWith(
            HttpStatusCode.OK,
            """{"choices":[],"usage":{"prompt_tokens":1,"completion_tokens":0,"total_tokens":1}}""");
        var client = new DeepSeekClient(handler);

        var result = await client.ChatAsync(PingRequest(), ApiKey);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DeepSeekErrorKind.Unknown, result.ErrorKind);
        Assert.AreEqual("DeepSeek 返回了无法解析的响应。", result.ErrorMessage);
    }

    [TestMethod]
    public async Task ChatAsync_CallerCancellation_PropagatesInsteadOfMapping()
    {
        var handler = new FakeHttpMessageHandler(
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        var client = new DeepSeekClient(handler);
        using var cancellation = new CancellationTokenSource();
        var task = client.ChatAsync(PingRequest(), ApiKey, cancellation.Token);
        await cancellation.CancelAsync();

        // Task.Delay 随令牌取消抛出的是 TaskCanceledException（OCE 子类），客户端原样上抛。
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => task);
    }

    [TestMethod]
    public async Task ChatAsync_NetworkFailure_MapsToNetworkError()
    {
        var handler = new FakeHttpMessageHandler(
            (_, _) => throw new HttpRequestException("connection refused"));
        var client = new DeepSeekClient(handler);

        var result = await client.ChatAsync(PingRequest(), ApiKey);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DeepSeekErrorKind.NetworkError, result.ErrorKind);
        Assert.AreEqual("无法连接 DeepSeek 服务，请检查网络。", result.ErrorMessage);
        Assert.IsFalse(result.ErrorMessage!.Contains(ApiKey), "错误消息不得包含 API Key。");
    }

    [TestMethod]
    public async Task TestConnectionAsync_WithEmptyKey_FailsLocallyWithoutRequest()
    {
        var handler = new FakeHttpMessageHandler(
            (_, _) => throw new AssertFailedException("空 key 不得发起请求。"));
        var client = new DeepSeekClient(handler);

        var result = await client.TestConnectionAsync("  ");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("请先填写 API Key。", result.Message);
        Assert.AreEqual(0, handler.CallCount);
    }

    [TestMethod]
    public async Task TestConnectionAsync_Success_ProbesModelsWithGetRequest()
    {
        // 探活改 GET /models（契约 #28 修订）：无生成、无请求体、零 token 消耗；
        // 成功文案仍含模型名（恒为 DefaultModel，模型唯一性不变）与耗时。
        var handler = RespondWith(HttpStatusCode.OK, ModelsBody());
        var client = new DeepSeekClient(handler);

        var result = await client.TestConnectionAsync(ApiKey);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(DeepSeekClient.DefaultModel, result.Model);
        Assert.IsTrue(result.Elapsed >= TimeSpan.Zero);
        StringAssert.Contains(result.Message, "连接成功");
        StringAssert.Contains(result.Message, DeepSeekClient.DefaultModel);
        StringAssert.Contains(result.Message, "耗时");
        var request = handler.LastRequest!;
        Assert.AreEqual(HttpMethod.Get, request.Method, "探活必须是 GET 请求。");
        Assert.AreEqual("https://api.deepseek.com/models", request.RequestUri!.ToString());
        Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
        Assert.AreEqual(ApiKey, request.Headers.Authorization.Parameter);
        Assert.IsNull(request.Content, "探活请求不得携带请求体（不得构造 chat completion）。");
    }

    [TestMethod]
    public async Task TestConnectionAsync_EmptyModelsList_StillSucceeds()
    {
        // 可达＋鉴权已由 200 证明；失败判据不得依赖列表内容（空目录也成功）。
        var handler = RespondWith(HttpStatusCode.OK, """{"object":"list","data":[]}""");
        var client = new DeepSeekClient(handler);

        var result = await client.TestConnectionAsync(ApiKey);

        Assert.IsTrue(result.IsSuccess);
        StringAssert.Contains(result.Message, "连接成功");
    }

    [TestMethod]
    public async Task TestConnectionAsync_MappedStatusFailures_SurfaceChineseMessages()
    {
        // 非 200 一律复用 ChatAsync 同一套错误映射：探活失败文案与生成侧一致；
        // 垃圾错误体（HTML/畸形 JSON/空体）不改变状态码分类——状态码决定类别，
        // 错误体只补充 detail（评审 BLOCKER：MapFailure 必须耐受垃圾响应）。
        var cases = new (HttpStatusCode Status, string Body, string Message)[]
        {
            (HttpStatusCode.Unauthorized, "<html>blocked</html>", "API Key 无效。"),
            (HttpStatusCode.PaymentRequired, """{"error":{"message":"insufficient balance"}}""",
                "余额不足，请前往 platform.deepseek.com 充值。"),
            (HttpStatusCode.TooManyRequests, """{"error":{"message":123}}""",
                "请求过于频繁，请稍后再试。"),
            (HttpStatusCode.ServiceUnavailable, "", "DeepSeek 服务暂时不可用。"),
            (HttpStatusCode.NotFound, """{"error":{"message":"not found"}}""",
                "请求失败（HTTP 404）：not found"),
        };
        foreach (var (status, body, message) in cases)
        {
            var handler = RespondWith(status, body);
            var client = new DeepSeekClient(handler);

            var result = await client.TestConnectionAsync(ApiKey);

            Assert.IsFalse(result.IsSuccess, $"HTTP {(int)status} 不应成功。");
            Assert.AreEqual(message, result.Message, $"HTTP {(int)status} 文案不符。");
        }
    }

    [TestMethod]
    public async Task TestConnectionAsync_NonStandardSuccessStatus_FailsWithStatusCode()
    {
        // 2xx 非常规码（201/204）是协议异常，不是探活成功——落到错误映射的
        // 「其它」臂并保留 HTTP 码，不得因 IsSuccessStatusCode 误判成功。
        var cases = new (HttpStatusCode Status, string Body)[]
        {
            (HttpStatusCode.Created, ModelsBody()),
            (HttpStatusCode.NoContent, ""),
        };
        foreach (var (status, body) in cases)
        {
            var handler = RespondWith(status, body);
            var client = new DeepSeekClient(handler);

            var result = await client.TestConnectionAsync(ApiKey);

            Assert.IsFalse(result.IsSuccess, $"HTTP {(int)status} 不应成功。");
            StringAssert.Contains(result.Message, $"HTTP {(int)status}");
        }
    }

    [TestMethod]
    public async Task TestConnectionAsync_Malformed200Body_FailsAsUnparseable()
    {
        // 200 但响应体形状不可解析（透明代理/劫持/网关错误页）：映射为无法解析，
        // 不崩溃；根非对象、data 非数组、空体同判。
        var bodies = new[] { "<html>proxy login</html>", "[]", """{"data":{}}""", "" };
        foreach (var body in bodies)
        {
            var handler = RespondWith(HttpStatusCode.OK, body);
            var client = new DeepSeekClient(handler);

            var result = await client.TestConnectionAsync(ApiKey);

            Assert.IsFalse(result.IsSuccess, $"响应体「{body}」不应成功。");
            Assert.AreEqual("DeepSeek 返回了无法解析的响应。", result.Message);
        }
    }

    [TestMethod]
    public async Task TestConnectionAsync_UnmappedNetworkException_MapsToNetworkError()
    {
        // 复现报障路径（层②）：映射外异常（原始 SocketException）曾穿透
        // TestConnectionAsync 到 UI 兜底「测试失败，请重试。」；客户端边界
        // 完备化后必须映射为带类别的中文结果。
        var handler = new FakeHttpMessageHandler((_, _) => throw new SocketException());
        var client = new DeepSeekClient(handler);

        var result = await client.TestConnectionAsync(ApiKey);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("无法连接 DeepSeek 服务，请检查网络。", result.Message);
    }

    [TestMethod]
    public async Task ChatAsync_ResponseBodyReadFailure_MapsToTypedChineseResult()
    {
        // 读体阶段异常同样在映射边界内：HttpClient 把响应正文读取失败包装为
        // HttpRequestException 呈现——按网络错误映射，不穿透。
        var handler = new FakeHttpMessageHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ThrowingContent()
            }));
        var client = new DeepSeekClient(handler);

        var result = await client.ChatAsync(PingRequest(), ApiKey);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DeepSeekErrorKind.NetworkError, result.ErrorKind);
        Assert.AreEqual("无法连接 DeepSeek 服务，请检查网络。", result.ErrorMessage);
    }

    [TestMethod]
    public async Task TestConnectionAsync_CallerCancellation_PropagatesInsteadOfMapping()
    {
        var handler = new FakeHttpMessageHandler(
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        var client = new DeepSeekClient(handler);
        using var cancellation = new CancellationTokenSource();
        var task = client.TestConnectionAsync(ApiKey, cancellation.Token);
        await cancellation.CancelAsync();

        // 调用方令牌取消原样上抛，不折叠为失败结果（与 ChatAsync 同一口径）。
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => task);
    }

    [TestMethod]
    public async Task ChatAsync_UnmappedNetworkException_MapsToNetworkError()
    {
        // 同上根因在生成链路（AI 整理同时受益）：映射外异常不再穿透。
        var handler = new FakeHttpMessageHandler((_, _) => throw new SocketException());
        var client = new DeepSeekClient(handler);

        var result = await client.ChatAsync(PingRequest(), ApiKey);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DeepSeekErrorKind.NetworkError, result.ErrorKind);
        Assert.AreEqual("无法连接 DeepSeek 服务，请检查网络。", result.ErrorMessage);
    }

    [TestMethod]
    public async Task ChatAsync_UnexpectedException_MapsToUnknownWithExceptionTypeName()
    {
        // 未预期异常带类型名供远程诊断；绝不携带 Key 或异常 Message 本身。
        var handler = new FakeHttpMessageHandler(
            (_, _) => throw new InvalidOperationException($"boom with {ApiKey} inside"));
        var client = new DeepSeekClient(handler);

        var result = await client.ChatAsync(PingRequest(), ApiKey);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(DeepSeekErrorKind.Unknown, result.ErrorKind);
        StringAssert.Contains(result.ErrorMessage, "InvalidOperationException");
        Assert.IsFalse(result.ErrorMessage!.Contains(ApiKey), "错误消息不得包含 API Key。");
    }

    [TestMethod]
    public async Task ChatAsync_KeyWithInvalidHeaderCharacters_MapsFailureWithoutLeakingKey()
    {
        // 含换行等非法头字符的 Key 在请求构造/发送阶段抛出映射外异常——曾穿透
        // 到 UI 兜底；边界完备化后映射为结果，且文案绝不回显 Key 本身。
        var handler = RespondWith(HttpStatusCode.OK, SuccessBody());
        var client = new DeepSeekClient(handler);

        var result = await client.ChatAsync(PingRequest(), "sk-bad\ning\x20key");

        Assert.IsFalse(result.IsSuccess, "非法 Key 不应探活成功。");
        Assert.IsFalse(result.ErrorMessage!.Contains("sk-bad"), "错误消息不得包含 API Key 片段。");
        Assert.IsFalse(result.ErrorMessage.Contains("ing key"), "错误消息不得包含 API Key 片段。");
    }

    private static string ModelsBody() => """
        {"object":"list","data":[{"id":"deepseek-flash","object":"model","owned_by":"deepseek"}]}
        """;

    /// <summary>响应头 200 但正文读取抛 IOException 的假内容。</summary>
    private sealed class ThrowingContent : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            throw new IOException("read failed");

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new IOException("read failed");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [TestMethod]
    public async Task TestConnectionAsync_Failure_SurfacesMappedChineseMessage()
    {
        var handler = RespondWith(HttpStatusCode.Unauthorized, "{}");
        var client = new DeepSeekClient(handler);

        var result = await client.TestConnectionAsync(ApiKey);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("API Key 无效。", result.Message);
    }

    /// <summary>
    /// 记录收到的请求并按给定应答函数回复的假 handler；不发起任何真实网络。
    /// 同步应答用 RespondWith 构造，超时/取消/网络异常用抛异常的应答函数构造。
    /// </summary>
    private sealed class FakeHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return await responder(request, cancellationToken);
        }
    }
}
