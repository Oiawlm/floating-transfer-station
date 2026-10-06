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
}
