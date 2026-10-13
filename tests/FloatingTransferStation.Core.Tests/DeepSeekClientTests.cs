using System.Net;
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
    public async Task TestConnectionAsync_Success_UsesClientDefaultModelWithLightweightRequest()
    {
        // 响应体模型显式取 DefaultModel：请求体断言才是锁定目标，响应侧不再隐式耦合。
        var handler = RespondWith(HttpStatusCode.OK, SuccessBody(DeepSeekClient.DefaultModel));
        var client = new DeepSeekClient(handler);

        var result = await client.TestConnectionAsync(ApiKey);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(DeepSeekClient.DefaultModel, result.Model);
        Assert.IsTrue(result.Elapsed >= TimeSpan.Zero);
        StringAssert.Contains(result.Message, "连接成功");
        StringAssert.Contains(result.Message, DeepSeekClient.DefaultModel);
        StringAssert.Contains(result.Message, "耗时");
        var body = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        Assert.AreEqual(
            DeepSeekClient.DefaultModel,
            body.GetProperty("model").GetString(),
            "请求体模型必须固定为客户端默认模型（契约 #28）。");
        Assert.AreEqual(16, body.GetProperty("max_tokens").GetInt32());
        Assert.AreEqual("disabled", body.GetProperty("thinking").GetProperty("type").GetString());
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
