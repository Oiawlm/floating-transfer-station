using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Core.Tests;

/// <summary>
/// 卡片正文预览的派生契约：显示层只排版有界预览（成本随全文长度线性增长
/// 的 TextFormatter 排版是滚动卡顿来源），全文语义（搜索/编辑/复制/拖出）
/// 一律读 <see cref="BoardItem.Text"/>，预览不入持久化。
/// </summary>
[TestClass]
public sealed class BoardItemPreviewTextTests
{
    private const int PreviewLength = 600;

    [TestMethod]
    public void PreviewText_TruncatesBeyondBoundaryAndKeepsShortTexts()
    {
        var shortItem = BoardItem.CreateText("短文本", Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.AreEqual("短文本", shortItem.PreviewText);

        var longText = new string('长', PreviewLength + 500);
        var longItem = BoardItem.CreateText(longText, Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.AreEqual(PreviewLength, longItem.PreviewText!.Length);
        Assert.AreEqual(longText[..PreviewLength], longItem.PreviewText);
        Assert.AreEqual(longText, longItem.Text, "全文仍完整保留在 Text 上。");
    }

    [TestMethod]
    public void TextChange_RaisesNotificationsForTextAndPreviewText()
    {
        var item = BoardItem.CreateText(new string('甲', PreviewLength + 10), Guid.NewGuid(), DateTimeOffset.UtcNow);
        var raised = new List<string?>();
        ((INotifyPropertyChanged)item).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        item.Text = "替换后的短文本";

        CollectionAssert.AreEquivalent(new[] { nameof(BoardItem.Text), nameof(BoardItem.PreviewText) }, raised);
        Assert.AreEqual("替换后的短文本", item.PreviewText);

        raised.Clear();
        item.Text = item.Text;
        Assert.AreEqual(0, raised.Count, "同值赋值不得重复通知。");
    }

    [TestMethod]
    public void PreviewText_IsNotPersisted()
    {
        var item = BoardItem.CreateText(new string('乙', 700), Guid.NewGuid(), DateTimeOffset.UtcNow);
        var snapshot = new BoardSnapshot { Items = [item.CloneForSnapshot()] };

        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        });

        Assert.IsFalse(json.Contains("previewText", StringComparison.Ordinal), "预览是派生状态，不得进入持久化格式。");
        var roundTripped = JsonSerializer.Deserialize<BoardSnapshot>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        })!;
        Assert.AreEqual(item.Text, roundTripped.Items[0].Text);
        Assert.AreEqual(item.PreviewText, roundTripped.Items[0].PreviewText, "预览由全文重建，不依赖持久化。");
    }

    [TestMethod]
    public void IsAiOrganizing_RaisesNotificationAndStaysOutOfSnapshot()
    {
        var item = BoardItem.CreateText("在途整理的卡片", Guid.NewGuid(), DateTimeOffset.UtcNow);
        var raised = new List<string?>();
        ((INotifyPropertyChanged)item).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.IsFalse(item.IsAiOrganizing);
        item.BeginAiOrganize();
        Assert.IsTrue(item.IsAiOrganizing);
        item.EndAiOrganize();
        Assert.IsFalse(item.IsAiOrganizing);
        CollectionAssert.AreEqual(
            new[] { nameof(BoardItem.IsAiOrganizing), nameof(BoardItem.IsAiOrganizing) },
            raised,
            "Begin/End 各恰好通知一次。");

        var snapshot = item.CloneForSnapshot();
        Assert.IsFalse(
            JsonSerializer.Serialize(snapshot).Contains("isAiOrganizing", StringComparison.Ordinal),
            "AI 整理在途标志是会话状态，不得进入快照。");
    }

    [TestMethod]
    public void ImageTitle_NormalizesEmptyToNull_ClonesAndPersistsIntoSnapshot()
    {
        // 照 Note 免迁移口径：空串归一 null；CloneForSnapshot 复制；camelCase
        // 往返；旧 JSON 缺字段读 null（SchemaVersion 不动）。
        var item = BoardItem.CreateImage(
            Guid.NewGuid(), "images/a.png", @"C:\data\images\a.png", DateTimeOffset.UtcNow);
        Assert.IsNull(item.ImageTitle, "新卡未命名。");

        var raised = new List<string?>();
        ((INotifyPropertyChanged)item).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        item.ImageTitle = "白色咖啡杯";
        item.ImageTitle = string.Empty;
        CollectionAssert.AreEqual(
            new[] { nameof(BoardItem.ImageTitle), nameof(BoardItem.ImageTitle) },
            raised,
            "赋值与空串归一各恰好通知一次。");
        Assert.IsNull(item.ImageTitle, "空串一律归一为 null。");

        item.ImageTitle = "白色咖啡杯";
        var snapshot = item.CloneForSnapshot();
        Assert.AreEqual("白色咖啡杯", snapshot.ImageTitle, "快照克隆必须复制 ImageTitle。");

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };
        var json = JsonSerializer.Serialize(new BoardSnapshot { Items = [snapshot] }, options);
        StringAssert.Contains(json, "imageTitle", "标题按 camelCase 持久化。");
        var roundTripped = JsonSerializer.Deserialize<BoardSnapshot>(json, options)!;
        Assert.AreEqual("白色咖啡杯", roundTripped.Items[0].ImageTitle);

        var legacyJson = """
            {"schemaVersion":1,"items":[{"id":"0d2a1a67-63b8-4a8e-9a52-a54900b1c6f1","kind":"Image","category":"Inbox","order":0,"createdAt":"2026-11-07T00:00:00Z","imageRelativePath":"images/a.png"}]}
            """;
        var legacy = JsonSerializer.Deserialize<BoardSnapshot>(legacyJson, options)!;
        Assert.IsNull(legacy.Items[0].ImageTitle, "旧 JSON 缺 imageTitle 字段读为 null（免迁移）。");
    }

    [TestMethod]
    public void IsAiNaming_RaisesNotificationAndStaysOutOfSnapshot()
    {
        var item = BoardItem.CreateImage(
            Guid.NewGuid(), "images/a.png", @"C:\data\images\a.png", DateTimeOffset.UtcNow);
        var raised = new List<string?>();
        ((INotifyPropertyChanged)item).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.IsFalse(item.IsAiNaming);
        item.BeginAiNaming();
        Assert.IsTrue(item.IsAiNaming);
        item.EndAiNaming();
        Assert.IsFalse(item.IsAiNaming);
        CollectionAssert.AreEqual(
            new[] { nameof(BoardItem.IsAiNaming), nameof(BoardItem.IsAiNaming) },
            raised,
            "Begin/End 各恰好通知一次。");

        var snapshot = item.CloneForSnapshot();
        Assert.IsFalse(
            JsonSerializer.Serialize(snapshot).Contains("isAiNaming", StringComparison.Ordinal),
            "AI 命名在途标志是会话状态，不得进入快照。");
    }
}
