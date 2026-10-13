namespace FloatingTransferStation.Services;

/// <summary>一次 AI 整理的结果：成功时填充 <see cref="OrganizedText"/>，失败时给出
/// 用户可读中文消息（不含密钥）。业务失败不抛异常，对齐
/// <see cref="DeepSeekChatResult"/> 风格。</summary>
public sealed record AiOrganizeOutcome(bool IsSuccess, string? OrganizedText, string? ErrorMessage)
{
    public static AiOrganizeOutcome Ok(string organizedText) => new(true, organizedText, null);

    public static AiOrganizeOutcome Fail(string errorMessage) => new(false, null, errorMessage);
}

/// <summary>
/// 「文字卡 AI 整理」的编排服务（1.27.0 DeepSeek 地基上的业务管线）：按内置
/// 提示词把长转写稿整理为可读文本。本地门槛（2 万字）拦截在前、零请求；请求
/// 模型恒为 <see cref="DeepSeekClient.DefaultModel"/>（deepseek-flash，契约 #28），
/// 非思考模式不传 max_tokens 默认仅 8K（长文必截断）故显式 32768、思考模式
/// temperature 失效且延迟/费用高故显式 Disabled、整理类低温保守取 0.3；结果经
/// 确定性规范化（Trim＋整体单围栏剥除）。无状态：Key 按调用参数传入，明文
/// 只经内存传递。
/// </summary>
public sealed class AiTextOrganizer
{
    /// <summary>2 万字上限按 UTF-16 码元计（代理对按 2 计，含大量 emoji 略保守的已知近似）。</summary>
    private const int MaxInputLength = 20_000;
    private const int MaxOutputTokens = 32_768;
    private const double OrganizeTemperature = 0.3;

    private readonly DeepSeekClient _client;

    public AiTextOrganizer(DeepSeekClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <summary>
    /// 整理一段全文。超长本地失败零请求；网络/鉴权失败透传客户端的中文消息；
    /// 结果被 <c>finish_reason=length</c> 截断或为空白判失败（呼应插件系统
    /// 「结果清空保留原文」红线）；成功返回规范化后的整理文本。
    /// </summary>
    public async Task<AiOrganizeOutcome> OrganizeAsync(
        string text,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxInputLength)
        {
            return AiOrganizeOutcome.Fail("这段文本超过 2 万字，暂不支持 AI 整理。");
        }

        var request = new DeepSeekChatRequest(
            DeepSeekClient.DefaultModel,
            [
                new DeepSeekChatMessage("system", AiOrganizePrompt.SystemPrompt),
                new DeepSeekChatMessage("user", text)
            ],
            MaxTokens: MaxOutputTokens,
            Temperature: OrganizeTemperature,
            Thinking: DeepSeekThinking.Disabled);
        var result = await _client.ChatAsync(request, apiKey, cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return AiOrganizeOutcome.Fail(result.ErrorMessage ?? "AI 整理失败。");
        }

        if (string.Equals(result.FinishReason, "length", StringComparison.Ordinal))
        {
            return AiOrganizeOutcome.Fail("整理结果被截断，未应用。");
        }

        var organized = Normalize(result.Content);
        return string.IsNullOrWhiteSpace(organized)
            ? AiOrganizeOutcome.Fail("整理结果为空，未应用。")
            : AiOrganizeOutcome.Ok(organized);
    }

    /// <summary>
    /// 输出契约的确定性实现（非启发式）：Trim 首尾空白；若整体被单个 Markdown
    /// 围栏包裹（首行 ``` 开头 ∧ 末行恰为 ```）则剥掉这一层围栏后再 Trim；只剩
    /// 围栏记号与空白的内容按空结果处理。已知边界（评审登记）：用户正文本身
    /// 整体恰为一个围栏块时同被剥除——确定性规则无法区分「模型包裹」与「正文
    /// 即围栏」，提示词已显式禁止围栏输出，剥除是输出契约的确定性代价。
    /// </summary>
    private static string Normalize(string? content)
    {
        var text = (content ?? string.Empty).Trim();
        if (text.Length > 0 && text.All(c => c == '`' || char.IsWhiteSpace(c)))
        {
            return string.Empty;
        }

        var lines = text.Split('\n');
        if (lines.Length >= 2 &&
            lines[0].StartsWith("```", StringComparison.Ordinal) &&
            lines[^1] == "```")
        {
            text = string.Join('\n', lines[1..^1]).Trim();
        }

        return text;
    }
}
