namespace FloatingTransferStation.Services;

/// <summary>一次 AI 识图命名的结果：成功时填充 <see cref="Title"/>，失败时给出
/// 用户可读中文消息（不含密钥）。业务失败不抛异常，对齐
/// <see cref="AiOrganizeOutcome"/> 风格。</summary>
public sealed record AiImageNamingOutcome(bool IsSuccess, string? Title, string? ErrorMessage)
{
    public static AiImageNamingOutcome Ok(string title) => new(true, title, null);

    public static AiImageNamingOutcome Fail(string errorMessage) => new(false, null, errorMessage);
}

/// <summary>
/// 「AI 识图命名」的编排服务（照 <see cref="AiTextOrganizer"/> 形态）：把受管
/// 图片缩略编码为 data URL，经 DeepSeek 视觉消息（content parts，deepseek-flash
/// 2026-08-21 起支持图片输入）请求一个描述性名字，再经 <see cref="FileNameSanitizer"/>
/// 清洗为可落盘基名。守卫（图片副本缺失/编码失败）拦截在前、零请求；请求模型
/// 恒为 <see cref="DeepSeekClient.DefaultModel"/>（契约 #28），命名类短输出取
/// max_tokens=128、低温保守 0.3、思考显式 Disabled（flash 默认开思考且思考下
/// temperature 失效）；结果被 finish_reason=length 截断或清洗后为空均判失败。
/// 无状态：Key 按调用参数传入，明文只经内存传递。
/// </summary>
public sealed class AiImageNamingOrganizer
{
    private const int MaxOutputTokens = 128;
    private const double NamingTemperature = 0.3;
    private const string UserInstruction = "给这张图片起一个符合上述要求的名字。";

    private readonly DeepSeekClient _client;

    public AiImageNamingOrganizer(DeepSeekClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <summary>
    /// 为一张受管图片命名。图片副本缺失或缩略编码失败均本地失败零请求；
    /// 网络/鉴权失败透传客户端的中文消息；成功返回清洗后的文件名基名。
    /// </summary>
    public async Task<AiImageNamingOutcome> NameImageAsync(
        string imageAbsolutePath,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(imageAbsolutePath))
        {
            return AiImageNamingOutcome.Fail("图片副本已缺失，无法命名。");
        }

        var dataUrl = AiImageNamingEncoder.EncodeToJpegDataUrl(imageAbsolutePath);
        if (dataUrl is null)
        {
            return AiImageNamingOutcome.Fail("图片暂时无法识别，已跳过。");
        }

        var request = new DeepSeekChatRequest(
            DeepSeekClient.DefaultModel,
            [
                new DeepSeekChatMessage("system", AiImageNamingPrompt.SystemPrompt),
                new DeepSeekChatMessage(
                    "user",
                    ContentParts:
                    [
                        DeepSeekContentPart.FromImageUrl(dataUrl),
                        DeepSeekContentPart.FromText(UserInstruction)
                    ])
            ],
            MaxTokens: MaxOutputTokens,
            Temperature: NamingTemperature,
            Thinking: DeepSeekThinking.Disabled);
        var result = await _client.ChatAsync(request, apiKey, cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return AiImageNamingOutcome.Fail(result.ErrorMessage ?? "AI 命名失败。");
        }

        if (string.Equals(result.FinishReason, "length", StringComparison.Ordinal))
        {
            return AiImageNamingOutcome.Fail("AI 命名结果被截断，未应用。");
        }

        var title = FileNameSanitizer.Sanitize(result.Content);
        return title is null
            ? AiImageNamingOutcome.Fail("AI 命名结果无效，未应用。")
            : AiImageNamingOutcome.Ok(title);
    }
}
