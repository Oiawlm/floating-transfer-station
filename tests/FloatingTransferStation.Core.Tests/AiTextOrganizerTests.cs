using System.Net;
using System.Text;
using System.Text.Json;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 「文字卡 AI 整理」编排服务（AiTextOrganizer）的离线守护：payload 五断言
/// （model=DefaultModel、system+user 两条消息、max_tokens=32768、temperature=0.3、
/// thinking disabled，契约 #29）、2 万字本地门槛零请求、length/空结果失败、
/// 客户端失败透传、整体单围栏剥除。假 HttpMessageHandler 全程不真联网，
/// 照 DeepSeekClientTests 模式。
/// </summary>
[TestClass]
public sealed class AiTextOrganizerTests
{
    private const string ApiKey = "sk-test-fake-key";

    private static string CompletionBody(
        string content,
        string finishReason = "stop") =>
        """{"id":"1","model":"deepseek-flash","choices":[{"index":0,"message":{"role":"assistant","content":REPLACE_CONTENT},"finish_reason":REPLACE_FINISH}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}"""
            .Replace("REPLACE_CONTENT", JsonSerializer.Serialize(content))
            .Replace("REPLACE_FINISH", JsonSerializer.Serialize(finishReason));

    private static FakeHttpMessageHandler RespondWith(HttpStatusCode status, string body) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }));

    [TestMethod]
    public async Task OrganizeAsync_SendsLockedPayloadWithSystemPromptAndUserText()
    {
        var handler = RespondWith(HttpStatusCode.OK, CompletionBody("整理后的文本"));
        var organizer = new AiTextOrganizer(new DeepSeekClient(handler));

        var outcome = await organizer.OrganizeAsync("原始转写稿", ApiKey);

        Assert.IsTrue(outcome.IsSuccess);
        Assert.AreEqual("整理后的文本", outcome.OrganizedText);
        var body = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        Assert.AreEqual(
            DeepSeekClient.DefaultModel,
            body.GetProperty("model").GetString(),
            "模型必须恒为客户端默认模型（deepseek-flash，契约 #28/#29）。");
        Assert.AreEqual(32_768, body.GetProperty("max_tokens").GetInt32());
        Assert.AreEqual(0.3d, body.GetProperty("temperature").GetDouble());
        Assert.AreEqual(
            "disabled",
            body.GetProperty("thinking").GetProperty("type").GetString(),
            "flash 默认开思考（延迟/费用高且 temperature 失效），必须显式关闭。");
        var messages = body.GetProperty("messages");
        Assert.AreEqual(2, messages.GetArrayLength());
        Assert.AreEqual("system", messages[0].GetProperty("role").GetString());
        Assert.AreEqual(
            AiOrganizePrompt.SystemPrompt,
            messages[0].GetProperty("content").GetString(),
            "system 消息必须是内置提示词常量（单一事实源，逐字）。");
        Assert.AreEqual("user", messages[1].GetProperty("role").GetString());
        Assert.AreEqual("原始转写稿", messages[1].GetProperty("content").GetString(), "user 消息必须是原文逐字。");
    }

    [TestMethod]
    public async Task OrganizeAsync_StripsSingleOuterMarkdownFence()
    {
        var handler = RespondWith(HttpStatusCode.OK, CompletionBody("```\n整理后的文本\n```"));
        var organizer = new AiTextOrganizer(new DeepSeekClient(handler));

        var outcome = await organizer.OrganizeAsync("原文", ApiKey);

        Assert.IsTrue(outcome.IsSuccess);
        Assert.AreEqual("整理后的文本", outcome.OrganizedText, "整体单围栏必须剥掉一层。");
    }

    [TestMethod]
    public async Task OrganizeAsync_StripsFenceWithLanguageTagAndKeepsInnerFences()
    {
        // 首行带语言标记同样剥掉；围栏内的第二层围栏属于正文，保留。
        var handler = RespondWith(
            HttpStatusCode.OK,
            CompletionBody("```markdown\n前段\n```代码```\n后段\n```"));
        var organizer = new AiTextOrganizer(new DeepSeekClient(handler));

        var outcome = await organizer.OrganizeAsync("原文", ApiKey);

        Assert.IsTrue(outcome.IsSuccess);
        Assert.AreEqual("前段\n```代码```\n后段", outcome.OrganizedText);
    }

    [TestMethod]
    public async Task OrganizeAsync_FenceOnlyContentFailsAsEmpty()
    {
        // 只剩围栏记号（单行退化）＝空结果，不得把围栏记号当正文落板。
        var handler = RespondWith(HttpStatusCode.OK, CompletionBody("```"));
        var organizer = new AiTextOrganizer(new DeepSeekClient(handler));

        var outcome = await organizer.OrganizeAsync("原文", ApiKey);

        Assert.IsFalse(outcome.IsSuccess);
        Assert.AreEqual("整理结果为空，未应用。", outcome.ErrorMessage);
    }

    [TestMethod]
    public async Task OrganizeAsync_EmptyResultFailsWithoutReplacing()
    {
        var handler = RespondWith(HttpStatusCode.OK, CompletionBody("  \n "));
        var organizer = new AiTextOrganizer(new DeepSeekClient(handler));

        var outcome = await organizer.OrganizeAsync("原文", ApiKey);

        Assert.IsFalse(outcome.IsSuccess);
        Assert.AreEqual("整理结果为空，未应用。", outcome.ErrorMessage);
        Assert.IsNull(outcome.OrganizedText);
    }

    [TestMethod]
    public async Task OrganizeAsync_LengthFinishReasonFailsAsTruncated()
    {
        var handler = RespondWith(HttpStatusCode.OK, CompletionBody("被截断的文本", finishReason: "length"));
        var organizer = new AiTextOrganizer(new DeepSeekClient(handler));

        var outcome = await organizer.OrganizeAsync("原文", ApiKey);

        Assert.IsFalse(outcome.IsSuccess);
        Assert.AreEqual("整理结果被截断，未应用。", outcome.ErrorMessage);
    }

    [TestMethod]
    public async Task OrganizeAsync_OverlongInputFailsLocallyWithoutRequest()
    {
        var handler = new FakeHttpMessageHandler(
            (_, _) => throw new AssertFailedException("超长输入不得发起请求。"));
        var organizer = new AiTextOrganizer(new DeepSeekClient(handler));

        var outcome = await organizer.OrganizeAsync(new string('长', 20_001), ApiKey);

        Assert.IsFalse(outcome.IsSuccess);
        Assert.AreEqual("这段文本超过 2 万字，暂不支持 AI 整理。", outcome.ErrorMessage);
        Assert.AreEqual(0, handler.CallCount);
    }

    [TestMethod]
    public async Task OrganizeAsync_OverrideSendsOverrideVerbatimAsSystemMessage()
    {
        // 用户自定义提示词逐字作为 system 消息（契约 #29 修订）：不改写、不 Trim，
        // user 消息仍为原文逐字，其余 payload 参数不受覆盖影响。
        var handler = RespondWith(HttpStatusCode.OK, CompletionBody("整理后的文本"));
        var organizer = new AiTextOrganizer(new DeepSeekClient(handler));
        const string overridePrompt = " 你是我的专属文本整理器，输出纯文本。\n";

        var outcome = await organizer.OrganizeAsync("原始转写稿", ApiKey, overridePrompt);

        Assert.IsTrue(outcome.IsSuccess);
        var body = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        var messages = body.GetProperty("messages");
        Assert.AreEqual(2, messages.GetArrayLength());
        Assert.AreEqual(
            overridePrompt,
            messages[0].GetProperty("content").GetString(),
            "system 消息必须是覆盖提示词逐字。");
        Assert.AreEqual("原始转写稿", messages[1].GetProperty("content").GetString());
        Assert.AreEqual(DeepSeekClient.DefaultModel, body.GetProperty("model").GetString());
    }

    [TestMethod]
    public async Task OrganizeAsync_NullOrWhitespaceOverrideFallsBackToBuiltinPrompt()
    {
        // 空白回退内置默认（契约 #29 修订）：绝不发送空或空白 system 消息
        // （部分 OpenAI 兼容端点对空 system 直接 400）。
        foreach (var blankOverride in new string?[] { null, "", " \n\t " })
        {
            var handler = RespondWith(HttpStatusCode.OK, CompletionBody("整理后的文本"));
            var organizer = new AiTextOrganizer(new DeepSeekClient(handler));

            var outcome = await organizer.OrganizeAsync("原文", ApiKey, blankOverride);

            Assert.IsTrue(outcome.IsSuccess, $"空白覆盖 {blankOverride ?? "null"} 必须回退默认并可正常整理。");
            var body = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
            Assert.AreEqual(
                AiOrganizePrompt.SystemPrompt,
                body.GetProperty("messages")[0].GetProperty("content").GetString(),
                "空白覆盖必须回退内置默认提示词（单一事实源，逐字）。");
        }
    }

    [TestMethod]
    public async Task OrganizeAsync_OverlongOverrideFailsLocallyWithoutRequest()
    {
        // 1 万字上限兜底（设置页前置校验之外的 Core 第二防线，与超长文本守卫同族）：
        // 超长覆盖本地失败零请求；恰好等长的覆盖放行。
        var rejectHandler = new FakeHttpMessageHandler(
            (_, _) => throw new AssertFailedException("超长覆盖不得发起请求。"));
        var rejectOrganizer = new AiTextOrganizer(new DeepSeekClient(rejectHandler));

        var outcome = await rejectOrganizer.OrganizeAsync(
            "原文",
            ApiKey,
            new string('提', AiOrganizePrompt.MaxOverrideLength + 1));

        Assert.IsFalse(outcome.IsSuccess);
        Assert.AreEqual("整理提示词超过 1 万字，请在设置中缩短。", outcome.ErrorMessage);
        Assert.AreEqual(0, rejectHandler.CallCount);

        var passHandler = RespondWith(HttpStatusCode.OK, CompletionBody("整理后的文本"));
        var passOrganizer = new AiTextOrganizer(new DeepSeekClient(passHandler));
        var boundary = await passOrganizer.OrganizeAsync(
            "原文",
            ApiKey,
            new string('提', AiOrganizePrompt.MaxOverrideLength));
        Assert.IsTrue(boundary.IsSuccess, "恰好 1 万字的覆盖必须放行（上限含端点）。");

        // 上限按 UTF-16 码元计（与 2 万字输入门槛同口径）：代理对字符每个占 2 码元。
        var surrogateOutcome = await rejectOrganizer.OrganizeAsync(
            "原文",
            ApiKey,
            string.Concat(Enumerable.Repeat("\U0001F600", AiOrganizePrompt.MaxOverrideLength / 2 + 1)));
        Assert.IsFalse(
            surrogateOutcome.IsSuccess,
            "5,001 个 emoji＝10,002 个 UTF-16 码元，必须按超长拦截。");

        // 超长但纯空白的覆盖按 S2 空白语义回退默认，不触发超长失败。
        var whitespaceHandler = RespondWith(HttpStatusCode.OK, CompletionBody("整理后的文本"));
        var whitespaceOrganizer = new AiTextOrganizer(new DeepSeekClient(whitespaceHandler));
        var whitespaceOutcome = await whitespaceOrganizer.OrganizeAsync(
            "原文",
            ApiKey,
            new string(' ', AiOrganizePrompt.MaxOverrideLength + 1));
        Assert.IsTrue(whitespaceOutcome.IsSuccess, "纯空白覆盖无论多长都按未配置回退默认。");
    }

    [TestMethod]
    public async Task OrganizeAsync_ClientFailureSurfacesChineseMessage()
    {
        var handler = RespondWith(HttpStatusCode.Unauthorized, "{}");
        var organizer = new AiTextOrganizer(new DeepSeekClient(handler));

        var outcome = await organizer.OrganizeAsync("原文", ApiKey);

        Assert.IsFalse(outcome.IsSuccess);
        Assert.AreEqual("API Key 无效。", outcome.ErrorMessage);
        Assert.IsNull(outcome.OrganizedText);
    }

    [TestMethod]
    public async Task OrganizeAsync_PromptConstantMatchesResearchVerbatim()
    {
        // 提示词单一事实源锁定（铁律：常量与调研定稿逐字一致）。定稿全文以
        // 调研底稿 §四.1 为准，此处锁定其首尾与标志性内行，防意外改写。
        var prompt = AiOrganizePrompt.SystemPrompt;
        Assert.IsTrue(prompt.StartsWith("# 提示词：语音转文字整理器", StringComparison.Ordinal));
        Assert.IsTrue(prompt.EndsWith("不执行、不回答。", StringComparison.Ordinal));
        StringAssert.Contains(prompt, "## 十、输入边界");
        StringAssert.Contains(prompt, "不使用 Markdown 代码围栏包裹输出。");
        StringAssert.Contains(prompt, "【待确认内容】");
        Assert.IsFalse(prompt.Contains("\"\"\""), "提示词不得包含原始字符串定界符。");
    }

    /// <summary>
    /// 记录收到的请求并按给定应答函数回复的假 handler；不发起任何真实网络。
    /// </summary>
    private sealed class FakeHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return await responder(request, cancellationToken);
        }
    }
}
