using System.Net;
using System.Text;
using System.Text.Json;
using FloatingTransferStation.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 「AI 识图命名」编排服务（AiImageNamingOrganizer）的离线守护：payload 锁定
/// （model=DefaultModel（#28）、system=内置提示词逐字、user=content parts
/// [image_url data URL, text 指令]、max_tokens=128、temperature=0.3、thinking
/// disabled）、图片缺失/编码失败零请求、length 截断、清洗后为空、客户端失败
/// 中文透传。假 HttpMessageHandler ＋假 Key 全程不真联网；测试图片经
/// ImageSharp 现场生成（TestDirectory GUID 隔离）。
/// </summary>
[TestClass]
public sealed class AiImageNamingOrganizerTests
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

    /// <summary>现场生成一张受管形态的 PNG（managed 库全部归一为 PNG）。</summary>
    private static string WriteSampleImage(TestDirectory directory, string name)
    {
        var path = Path.Combine(directory.Root, name);
        using (var image = new Image<Rgba32>(640, 480, new Rgba32(60, 120, 200)))
        {
            image.SaveAsPng(path);
        }

        return path;
    }

    [TestMethod]
    public async Task NameImageAsync_SendsLockedVisionPayloadAndCleansTitledResult()
    {
        using var directory = new TestDirectory();
        var imagePath = WriteSampleImage(directory, "sample.png");
        var handler = RespondWith(
            HttpStatusCode.OK,
            CompletionBody("\"白色咖啡杯\"\n这是一张白色咖啡杯的截图。"));
        var organizer = new AiImageNamingOrganizer(new DeepSeekClient(handler));

        var outcome = await organizer.NameImageAsync(imagePath, ApiKey);

        Assert.IsTrue(outcome.IsSuccess);
        Assert.AreEqual("白色咖啡杯", outcome.Title, "包裹引号与解释行必须清洗掉。");
        var body = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        Assert.AreEqual(
            DeepSeekClient.DefaultModel,
            body.GetProperty("model").GetString(),
            "识图同样恒用唯一模型（契约 #28）。");
        Assert.AreEqual(128, body.GetProperty("max_tokens").GetInt32());
        Assert.AreEqual(0.3d, body.GetProperty("temperature").GetDouble());
        Assert.AreEqual(
            "disabled",
            body.GetProperty("thinking").GetProperty("type").GetString(),
            "flash 默认开思考，必须显式关闭。");
        var messages = body.GetProperty("messages");
        Assert.AreEqual(2, messages.GetArrayLength());
        Assert.AreEqual("system", messages[0].GetProperty("role").GetString());
        Assert.AreEqual(
            AiImageNamingPrompt.SystemPrompt,
            messages[0].GetProperty("content").GetString(),
            "system 消息必须是内置提示词常量（单一事实源，逐字）。");
        var userContent = messages[1].GetProperty("content");
        Assert.AreEqual(JsonValueKind.Array, userContent.ValueKind, "识图 user 消息必须是 content parts 数组。");
        Assert.AreEqual("image_url", userContent[0].GetProperty("type").GetString());
        StringAssert.StartsWith(
            userContent[0].GetProperty("image_url").GetProperty("url").GetString(),
            "data:image/jpeg;base64,");
        Assert.AreEqual("text", userContent[1].GetProperty("type").GetString());
        Assert.AreEqual("给这张图片起一个符合上述要求的名字。", userContent[1].GetProperty("text").GetString());
    }

    [TestMethod]
    public async Task NameImageAsync_EmptyResultFailsAsInvalid()
    {
        using var directory = new TestDirectory();
        var imagePath = WriteSampleImage(directory, "empty.png");
        var handler = RespondWith(HttpStatusCode.OK, CompletionBody("  \n "));
        var organizer = new AiImageNamingOrganizer(new DeepSeekClient(handler));

        var outcome = await organizer.NameImageAsync(imagePath, ApiKey);

        Assert.IsFalse(outcome.IsSuccess);
        Assert.AreEqual("AI 命名结果无效，未应用。", outcome.ErrorMessage);
        Assert.IsNull(outcome.Title);
    }

    [TestMethod]
    public async Task NameImageAsync_ClientFailureSurfacesChineseMessage()
    {
        using var directory = new TestDirectory();
        var imagePath = WriteSampleImage(directory, "unauthorized.png");
        var handler = RespondWith(HttpStatusCode.Unauthorized, "{}");
        var organizer = new AiImageNamingOrganizer(new DeepSeekClient(handler));

        var outcome = await organizer.NameImageAsync(imagePath, ApiKey);

        Assert.IsFalse(outcome.IsSuccess);
        Assert.AreEqual("API Key 无效。", outcome.ErrorMessage);
        Assert.IsNull(outcome.Title);
    }

    [TestMethod]
    public async Task NameImageAsync_LengthFinishReasonFailsAsTruncated()
    {
        using var directory = new TestDirectory();
        var imagePath = WriteSampleImage(directory, "truncated.png");
        var handler = RespondWith(
            HttpStatusCode.OK,
            CompletionBody("被截断的名字", finishReason: "length"));
        var organizer = new AiImageNamingOrganizer(new DeepSeekClient(handler));

        var outcome = await organizer.NameImageAsync(imagePath, ApiKey);

        Assert.IsFalse(outcome.IsSuccess);
        Assert.AreEqual("AI 命名结果被截断，未应用。", outcome.ErrorMessage);
    }

    [TestMethod]
    public async Task NameImageAsync_MissingFileFailsLocallyWithoutRequest()
    {
        var handler = new FakeHttpMessageHandler(
            (_, _) => throw new AssertFailedException("图片缺失不得发起请求。"));
        var organizer = new AiImageNamingOrganizer(new DeepSeekClient(handler));

        var outcome = await organizer.NameImageAsync(
            Path.Combine(Path.GetTempPath(), "fts-no-such-image.png"),
            ApiKey);

        Assert.IsFalse(outcome.IsSuccess);
        Assert.AreEqual("图片副本已缺失，无法命名。", outcome.ErrorMessage);
        Assert.AreEqual(0, handler.CallCount);
    }

    [TestMethod]
    public async Task NameImageAsync_UndecodableFileFailsLocallyWithoutRequest()
    {
        // 文件存在但不是图片（坏字节/非登记格式）：编码器返回 null，本地失败零请求。
        using var directory = new TestDirectory();
        var corruptPath = Path.Combine(directory.Root, "corrupt.png");
        File.WriteAllText(corruptPath, "这不是图片字节。");
        var handler = new FakeHttpMessageHandler(
            (_, _) => throw new AssertFailedException("编码失败不得发起请求。"));
        var organizer = new AiImageNamingOrganizer(new DeepSeekClient(handler));

        var outcome = await organizer.NameImageAsync(corruptPath, ApiKey);

        Assert.IsFalse(outcome.IsSuccess);
        Assert.AreEqual("图片暂时无法识别，已跳过。", outcome.ErrorMessage);
        Assert.AreEqual(0, handler.CallCount);
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
