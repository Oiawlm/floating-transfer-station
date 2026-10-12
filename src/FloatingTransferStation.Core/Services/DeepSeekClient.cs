using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FloatingTransferStation.Models;

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
/// 无状态：API Key 与模型都按调用参数传入，设置变更即生效，客户端不持有任何配置。
/// HttpClient 在实例内复用（组合根只创建一个实例）；测试可注入假 HttpMessageHandler，
/// 全程不真联网。明文 Key 只经内存传递，绝不写入日志、异常消息或任何持久化。
/// </summary>
public sealed class DeepSeekClient
{
    private const string ChatCompletionsUrl = "https://api.deepseek.com/chat/completions";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>官方当前轻量模型，也是设置页的默认选择（与 AppPreferences 默认值一致）。</summary>
    public const string DefaultModel = DeepSeekModels.Flash;

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
    /// HTTP 状态按官方错误码映射为中文结果（401/402/429/5xx/超时/网络/其它）。
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

        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsUrl)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(request, RequestJsonOptions),
                    Encoding.UTF8,
                    "application/json")
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await _httpClient
                .SendAsync(httpRequest, cancellationToken)
                .ConfigureAwait(false);
            var body = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? ParseSuccess(body)
                : MapFailure(response.StatusCode, body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // HttpClient.Timeout（30s）与 SocketsHttpHandler.ConnectTimeout（10s）都以
            // 取消异常呈现，此时外层令牌未取消，判为超时。
            return DeepSeekChatResult.Fail(DeepSeekErrorKind.Timeout, "连接超时，请检查网络。");
        }
        catch (HttpRequestException exception) when (exception.InnerException is TimeoutException)
        {
            // 连接超时在部分运行时路径以「内层 TimeoutException 的请求异常」呈现，同判超时。
            return DeepSeekChatResult.Fail(DeepSeekErrorKind.Timeout, "连接超时，请检查网络。");
        }
        catch (HttpRequestException)
        {
            return DeepSeekChatResult.Fail(DeepSeekErrorKind.NetworkError, "无法连接 DeepSeek 服务，请检查网络。");
        }
    }

    /// <summary>
    /// 测试连接：key 为空走本地校验；否则发送最小 chat completion
    /// （user「ping」、max_tokens=16、thinking disabled——thinking 默认开启且代价高），
    /// 成功结果含模型名与耗时，失败结果含映射后的中文文案。
    /// </summary>
    public async Task<DeepSeekConnectionTestResult> TestConnectionAsync(
        string apiKey,
        string? model,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new DeepSeekConnectionTestResult(
                false,
                null,
                TimeSpan.Zero,
                "请先填写 API Key。");
        }

        var request = new DeepSeekChatRequest(
            string.IsNullOrWhiteSpace(model) ? DefaultModel : model,
            [new DeepSeekChatMessage("user", "ping")],
            MaxTokens: 16,
            Thinking: DeepSeekThinking.Disabled);
        var result = await ChatAsync(request, apiKey, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        if (!result.IsSuccess)
        {
            return new DeepSeekConnectionTestResult(
                false,
                null,
                stopwatch.Elapsed,
                result.ErrorMessage ?? "连接失败。");
        }

        var succeededModel = string.IsNullOrWhiteSpace(result.Model) ? request.Model : result.Model;
        return new DeepSeekConnectionTestResult(
            true,
            succeededModel,
            stopwatch.Elapsed,
            $"连接成功（{succeededModel}，耗时 {FormatElapsed(stopwatch.Elapsed)}）。");
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
