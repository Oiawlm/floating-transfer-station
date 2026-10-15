using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FloatingTransferStation.Services;

/// <summary>DeepSeek chat completion 的一条消息（OpenAI 兼容格式）。</summary>
public sealed record DeepSeekChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

/// <summary>
/// DeepSeek 思维链开关，对应请求体 <c>thinking</c> 对象的 <c>type</c> 字段。
/// 服务端默认 enabled 且 reasoning_effort 默认 high，轻量调用应显式 Disabled。
/// </summary>
public sealed record DeepSeekThinking(
    [property: JsonPropertyName("type")] string Type)
{
    public static readonly DeepSeekThinking Enabled = new("enabled");
    public static readonly DeepSeekThinking Disabled = new("disabled");
}

/// <summary>
/// DeepSeek chat completion 请求体。maxTokens/temperature/thinking 为 null 时
/// 整个字段不发送（跟随服务端默认）。
/// </summary>
public sealed record DeepSeekChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<DeepSeekChatMessage> Messages,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens = null,
    [property: JsonPropertyName("temperature")] double? Temperature = null,
    [property: JsonPropertyName("thinking")] DeepSeekThinking? Thinking = null);

/// <summary>DeepSeek 用量统计（token 数）。</summary>
public sealed record DeepSeekUsage(int PromptTokens, int CompletionTokens, int TotalTokens);

/// <summary>DeepSeek 调用失败类别；用户可读文案见各失败构造点的中文消息。</summary>
public enum DeepSeekErrorKind
{
    None = 0,
    EmptyApiKey,
    InvalidApiKey,
    InsufficientBalance,
    RateLimited,
    Timeout,
    ServerError,
    NetworkError,
    Unknown
}

/// <summary>
/// 一次 DeepSeek chat completion 的结果：成功时填充 Content/FinishReason/Usage/Model，
/// 失败时 IsSuccess=false 并给出错误类别与用户可读中文消息（不含密钥）。
/// </summary>
public sealed record DeepSeekChatResult
{
    public bool IsSuccess { get; init; }
    public string? Content { get; init; }
    public string? FinishReason { get; init; }
    public DeepSeekUsage? Usage { get; init; }
    public string? Model { get; init; }
    public DeepSeekErrorKind ErrorKind { get; init; }
    public string? ErrorMessage { get; init; }

    public static DeepSeekChatResult Ok(
        string? content,
        string? finishReason,
        DeepSeekUsage? usage,
        string? model) => new()
        {
            IsSuccess = true,
            Content = content,
            FinishReason = finishReason,
            Usage = usage,
            Model = model
        };

    public static DeepSeekChatResult Fail(DeepSeekErrorKind kind, string message) => new()
    {
        IsSuccess = false,
        ErrorKind = kind,
        ErrorMessage = message
    };
}

/// <summary>「测试连接」的结果：中文文案就地反馈，成功含模型名与耗时。</summary>
public sealed record DeepSeekConnectionTestResult(
    bool IsSuccess,
    string? Model,
    TimeSpan Elapsed,
    string Message);

/// <summary>
/// DeepSeek 官方国内 API（https://api.deepseek.com）的 HTTP 客户端，仓库首个网络消费者。
/// 无状态：API Key 按调用参数传入，设置变更即生效；模型恒为 <see cref="DefaultModel"/>
/// （deepseek-flash，生产代码唯一模型字面量点，契约 #28），客户端不持有任何配置。
/// HttpClient 在实例内复用（组合根只创建一个实例）；测试可注入假 HttpMessageHandler，
/// 全程不真联网。明文 Key 只经内存传递，绝不写入日志、异常消息或任何持久化。
/// </summary>
public sealed class DeepSeekClient
{
    private const string ChatCompletionsUrl = "https://api.deepseek.com/chat/completions";
    private const string ModelsUrl = "https://api.deepseek.com/models";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>唯一产品模型（deepseek-flash）：线上请求的 model 字段恒等于此值。</summary>
    public const string DefaultModel = "deepseek-flash";

    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;

    public DeepSeekClient()
        : this(new SocketsHttpHandler
        {
            ConnectTimeout = ConnectTimeout,
            // 长驻桌面进程：定期轮换池化连接，网络切换后不连到过期 DNS。
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // API 客户端不跟随重定向：POST 重定向会丢 Authorization 头，3xx 应原样暴露。
            AllowAutoRedirect = false
        })
    {
    }

    public DeepSeekClient(HttpMessageHandler handler)
    {
        _httpClient = new HttpClient(handler)
        {
            Timeout = RequestTimeout,
            MaxResponseContentBufferSize = 10 * 1024 * 1024
        };
    }

    /// <summary>
    /// 发起一次 chat completion。key 为空不发请求，直接返回本地校验失败；
    /// HTTP 非 200 按官方错误码映射为中文结果（401/402/429/5xx/其它带 detail），
    /// 网络阶段任何非取消异常同样映射为带类别的中文结果，不向调用方穿透
    /// （调用方令牌取消除外，原样上抛）。
    /// </summary>
    public async Task<DeepSeekChatResult> ChatAsync(
        DeepSeekChatRequest request,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return DeepSeekChatResult.Fail(DeepSeekErrorKind.EmptyApiKey, "请先填写 API Key。");
        }

        // 请求 DTO 序列化在映射边界之外：自家编程错误以未处理异常原样上抛。
        var json = JsonSerializer.Serialize(request, RequestJsonOptions);
        var result = await SendAndMapAsync(
            () => new HttpRequestMessage(HttpMethod.Post, ChatCompletionsUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            },
            apiKey,
            cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? ParseSuccess(result.Content!) : result;
    }

    /// <summary>
    /// 测试连接：key 为空走本地校验；否则向官方模型列表端点发 GET /models
    /// 探活（零生成、零 token 消耗）——成败只锚定可达＋鉴权，不锚定生成侧
    /// 语义；成功文案仍含模型名（恒为 <see cref="DefaultModel"/>，契约 #28）
    /// 与耗时，失败复用与 <see cref="ChatAsync"/> 同一套中文映射。
    /// </summary>
    public async Task<DeepSeekConnectionTestResult> TestConnectionAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new DeepSeekConnectionTestResult(
                false,
                null,
                TimeSpan.Zero,
                "请先填写 API Key。");
        }

        var stopwatch = Stopwatch.StartNew();
        var result = await SendAndMapAsync(
            () => new HttpRequestMessage(HttpMethod.Get, ModelsUrl),
            apiKey,
            cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        if (!result.IsSuccess)
        {
            return new DeepSeekConnectionTestResult(
                false,
                null,
                stopwatch.Elapsed,
                result.ErrorMessage ?? "连接失败。");
        }

        // 200 已证明可达＋鉴权；模型列表只验证顶层形状可解析（空目录也成功），
        // 失败判据不得依赖列表内容——否则把探活重新耦合回服务端目录语义。
        return TryParseModelsList(result.Content!)
            ? new DeepSeekConnectionTestResult(
                true,
                DefaultModel,
                stopwatch.Elapsed,
                $"连接成功（{DefaultModel}，耗时 {FormatElapsed(stopwatch.Elapsed)}）。")
            : new DeepSeekConnectionTestResult(
                false,
                null,
                stopwatch.Elapsed,
                "DeepSeek 返回了无法解析的响应。");
    }

    /// <summary>
    /// 网络阶段统一出口：请求构造、鉴权头设置、发送与响应体读取（默认
    /// ResponseContentRead，HttpClient 超时覆盖全程）都在映射边界内——HTTP 200
    /// 时 Content 携带响应体原文交调用方按各自协议解析，其余状态码（含 2xx 非常规
    /// 码）一律经 <see cref="MapFailure"/>（状态码决定类别，错误体只补充 detail）；
    /// 网络阶段任何非取消异常经 <see cref="MapException"/> 映射，调用方令牌取消
    /// 原样上抛。鉴权头在本出口设置：工厂只构造请求形状，赋值抛异常（如 Key 含
    /// 非法头字符）时请求已由 using 接管释放。
    /// </summary>
    private async Task<DeepSeekChatResult> SendAndMapAsync(
        Func<HttpRequestMessage> requestFactory,
        string apiKey,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = requestFactory();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await _httpClient
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            var body = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK
                ? DeepSeekChatResult.Ok(body, null, null, null)
                : MapFailure(response.StatusCode, body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return MapException(exception);
        }
    }

    /// <summary>
    /// 网络阶段异常的统一映射（调用方令牌取消已在此之前原样上抛）。本客户端
    /// 只访问固定官方端点且 HttpClient 自管，这里是容错边界而非错误分类学：
    /// 未预期类型一律 Unknown 并携带类型名供远程诊断，绝不包含 Key 或 URL。
    /// </summary>
    private static DeepSeekChatResult MapException(Exception exception) => exception switch
    {
        // HttpClient.Timeout（30s）与 SocketsHttpHandler.ConnectTimeout（10s）都以
        // 取消异常呈现，此时外层令牌未取消，判为超时。
        OperationCanceledException => DeepSeekChatResult.Fail(
            DeepSeekErrorKind.Timeout, "连接超时，请检查网络。"),
        // 连接超时在部分运行时路径以「内层 TimeoutException 的请求异常」呈现，同判超时。
        HttpRequestException { InnerException: TimeoutException } => DeepSeekChatResult.Fail(
            DeepSeekErrorKind.Timeout, "连接超时，请检查网络。"),
        HttpRequestException => DeepSeekChatResult.Fail(
            DeepSeekErrorKind.NetworkError, "无法连接 DeepSeek 服务，请检查网络。"),
        SocketException => DeepSeekChatResult.Fail(
            DeepSeekErrorKind.NetworkError, "无法连接 DeepSeek 服务，请检查网络。"),
        _ => DeepSeekChatResult.Fail(
            DeepSeekErrorKind.Unknown,
            $"连接 DeepSeek 时出现未预期的错误（{exception.GetType().Name}）。")
    };

    /// <summary>
    /// 校验 /models 响应的顶层形状（JSON 对象含 data 数组成员）。只验形状不验
    /// 内容——可达与鉴权已由 HTTP 200 证明。
    /// </summary>
    private static bool TryParseModelsList(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static DeepSeekChatResult ParseSuccess(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var choice = root.GetProperty("choices")[0];
            var content = choice.GetProperty("message").GetProperty("content").GetString();
            var finishReason = choice.TryGetProperty("finish_reason", out var finish)
                ? finish.GetString()
                : null;
            string? model = root.TryGetProperty("model", out var modelElement)
                ? modelElement.GetString()
                : null;
            DeepSeekUsage? usage = root.TryGetProperty("usage", out var usageElement)
                ? new DeepSeekUsage(
                    usageElement.GetProperty("prompt_tokens").GetInt32(),
                    usageElement.GetProperty("completion_tokens").GetInt32(),
                    usageElement.GetProperty("total_tokens").GetInt32())
                : null;
            return DeepSeekChatResult.Ok(content, finishReason, usage, model);
        }
        catch (Exception exception) when (
            exception is JsonException
                or KeyNotFoundException
                or ArgumentOutOfRangeException
                or InvalidOperationException
                or IndexOutOfRangeException)
        {
            return DeepSeekChatResult.Fail(DeepSeekErrorKind.Unknown, "DeepSeek 返回了无法解析的响应。");
        }
    }

    private static DeepSeekChatResult MapFailure(HttpStatusCode statusCode, string body)
    {
        var detail = TryExtractErrorMessage(body);
        return (int)statusCode switch
        {
            401 => DeepSeekChatResult.Fail(DeepSeekErrorKind.InvalidApiKey, "API Key 无效。"),
            402 => DeepSeekChatResult.Fail(
                DeepSeekErrorKind.InsufficientBalance,
                "余额不足，请前往 platform.deepseek.com 充值。"),
            429 => DeepSeekChatResult.Fail(DeepSeekErrorKind.RateLimited, "请求过于频繁，请稍后再试。"),
            >= 500 => DeepSeekChatResult.Fail(DeepSeekErrorKind.ServerError, "DeepSeek 服务暂时不可用。"),
            _ => DeepSeekChatResult.Fail(
                DeepSeekErrorKind.Unknown,
                $"请求失败（HTTP {(int)statusCode}）：{detail ?? "无详细信息。"}")
        };
    }

    private static string? TryExtractErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.GetProperty("error").GetProperty("message").GetString();
        }
        catch (Exception exception) when (
            exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalSeconds < 1
            ? $"{elapsed.TotalMilliseconds:F0} 毫秒"
            : $"{elapsed.TotalSeconds:F1} 秒";
}
