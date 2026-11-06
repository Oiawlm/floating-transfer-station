using System.Windows;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class DragPayloadServiceTests
{
    [STATestMethod]
    public void Build_TextProvidesUnicodeAndPlainTextWithoutCreatingTxtFile()
    {
        Assert.AreEqual(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
        var item = BoardItem.CreateText(
            "中文 prompt",
            Guid.Parse("00000000-0000-0000-0000-000000000201"),
            DateTimeOffset.UtcNow);
        var service = new DragPayloadService();

        var data = service.Build(item);

        Assert.AreEqual("中文 prompt", data.GetData(DataFormats.UnicodeText));
        Assert.AreEqual("中文 prompt", data.GetData(DataFormats.Text));
        Assert.IsFalse(data.GetDataPresent(DataFormats.FileDrop));
        Assert.AreEqual(item.Id, service.GetInternalItemId(data));
        Assert.AreEqual(BoardCategory.Inbox, item.Category);
    }

    [STATestMethod]
    public void Build_ImageProvidesFileDropAndBitmap()
    {
        Assert.AreEqual(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "image.png");
        using (var image = new Image<Rgba32>(2, 2, SixLabors.ImageSharp.Color.Green))
        {
            image.SaveAsPng(path);
        }
        var item = BoardItem.CreateImage(
            Guid.Parse("00000000-0000-0000-0000-000000000202"),
            "images/image.png",
            path,
            DateTimeOffset.UtcNow);
        var service = new DragPayloadService();

        var data = service.Build(item);

        CollectionAssert.AreEqual(new[] { path }, data.GetFileDropList().Cast<string>().ToArray());
        Assert.IsTrue(data.GetDataPresent(DataFormats.Bitmap));
        Assert.AreEqual(item.Id, service.GetInternalItemId(data));
        Assert.AreEqual(BoardCategory.Inbox, item.Category);
    }

    [STATestMethod]
    [TestCategory("Adversarial")]
    public void Build_MissingImageFailsBeforeDragStarts()
    {
        var item = BoardItem.CreateImage(
            Guid.Parse("00000000-0000-0000-0000-000000000203"),
            "images/missing.png",
            "C:/definitely-missing-floating-transfer-station.png",
            DateTimeOffset.UtcNow);

        Assert.ThrowsExactly<FileNotFoundException>(() => new DragPayloadService().Build(item));
    }

    [STATestMethod]
    [TestCategory("Adversarial")]
    public void Build_TextWithoutContentIsRejected()
    {
        var item = new BoardItem
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000204"),
            Kind = BoardItemKind.Text,
            Category = BoardCategory.Inbox,
            Order = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            Text = null
        };

        Assert.ThrowsExactly<InvalidDataException>(() => new DragPayloadService().Build(item));
    }

    [STATestMethod]
    public void BuildInternalBatch_ProvidesOnlyOrderedInternalIds()
    {
        var first = BoardItem.CreateText("first", Guid.NewGuid(), DateTimeOffset.UtcNow);
        var second = BoardItem.CreateText("second", Guid.NewGuid(), DateTimeOffset.UtcNow);
        var service = new DragPayloadService();

        var data = service.BuildInternalBatch([first, second]);

        CollectionAssert.AreEqual(
            new[] { first.Id, second.Id },
            service.GetInternalItemIds(data)!.ToArray());
        Assert.IsFalse(data.GetDataPresent(DataFormats.FileDrop));
        Assert.IsFalse(data.GetDataPresent(DataFormats.UnicodeText));
        Assert.IsFalse(data.GetDataPresent(DataFormats.Text));
        Assert.IsFalse(data.GetDataPresent(DataFormats.Bitmap));
    }

    [STATestMethod]
    public void BuildInternalBatch_ImageBatchProvidesOrderedFileDropWithoutBitmap()
    {
        using var directory = new TestDirectory();
        var first = CreateManagedImage(directory, "first.png");
        var second = CreateManagedImage(directory, "second.png");
        var service = new DragPayloadService();

        var data = service.BuildInternalBatch([second, first]);

        CollectionAssert.AreEqual(
            new[] { second.Id, first.Id },
            service.GetInternalItemIds(data)!.ToArray());
        CollectionAssert.AreEqual(
            new[] { second.ImageAbsolutePath!, first.ImageAbsolutePath! },
            data.GetFileDropList().Cast<string>().ToArray());
        Assert.IsFalse(data.GetDataPresent(DataFormats.Bitmap));
        Assert.IsFalse(data.GetDataPresent(DataFormats.UnicodeText));
        Assert.IsFalse(data.GetDataPresent(DataFormats.Text));
    }

    [STATestMethod]
    public void BuildInternalBatch_MixedBatchRemainsInternalOnly()
    {
        using var directory = new TestDirectory();
        var text = BoardItem.CreateText("prompt", Guid.NewGuid(), DateTimeOffset.UtcNow);
        var image = CreateManagedImage(directory, "mixed.png");
        var service = new DragPayloadService();

        var data = service.BuildInternalBatch([text, image]);

        CollectionAssert.AreEqual(
            new[] { text.Id, image.Id },
            service.GetInternalItemIds(data)!.ToArray());
        Assert.IsFalse(data.GetDataPresent(DataFormats.FileDrop));
        Assert.IsFalse(data.GetDataPresent(DataFormats.UnicodeText));
        Assert.IsFalse(data.GetDataPresent(DataFormats.Text));
        Assert.IsFalse(data.GetDataPresent(DataFormats.Bitmap));
    }

    [STATestMethod]
    public void BuildInternalBatch_MissingImageRejectsWholeImageBatch()
    {
        using var directory = new TestDirectory();
        var existing = CreateManagedImage(directory, "existing.png");
        var missing = BoardItem.CreateImage(
            Guid.NewGuid(),
            "images/missing.png",
            Path.Combine(directory.Root, "missing.png"),
            DateTimeOffset.UtcNow);

        Assert.ThrowsExactly<FileNotFoundException>(() =>
            new DragPayloadService().BuildInternalBatch([existing, missing]));
    }

    [STATestMethod]
    public void GetInternalItemIds_AcceptsExistingSingleItemPayload()
    {
        var item = BoardItem.CreateText("single", Guid.NewGuid(), DateTimeOffset.UtcNow);
        var service = new DragPayloadService();
        var data = service.Build(item);

        CollectionAssert.AreEqual(
            new[] { item.Id },
            service.GetInternalItemIds(data)!.ToArray());
    }

    [STATestMethod]
    public void GetInternalItemIds_RejectsMalformedOrDuplicateBatch()
    {
        var service = new DragPayloadService();
        var duplicate = Guid.NewGuid().ToString("D");
        var duplicateData = new DataObject();
        duplicateData.SetData(
            DragPayloadService.InternalItemIdsFormat,
            new[] { duplicate, duplicate });
        var malformedData = new DataObject();
        malformedData.SetData(
            DragPayloadService.InternalItemIdsFormat,
            new[] { Guid.NewGuid().ToString("D"), "not-a-guid" });

        Assert.IsNull(service.GetInternalItemIds(duplicateData));
        Assert.IsNull(service.GetInternalItemIds(malformedData));
    }

    [STATestMethod]
    public void BuildInternalBatch_RequiresAtLeastTwoUniqueItems()
    {
        var item = BoardItem.CreateText("single", Guid.NewGuid(), DateTimeOffset.UtcNow);
        var service = new DragPayloadService();

        Assert.ThrowsExactly<ArgumentException>(() => service.BuildInternalBatch([item]));
        Assert.ThrowsExactly<ArgumentException>(() => service.BuildInternalBatch([item, item]));
    }

    [STATestMethod]
    public void BuildClipboardPayload_SingleTextMatchesDragOutDelivery()
    {
        var item = BoardItem.CreateText("单条文字", Guid.NewGuid(), DateTimeOffset.UtcNow);
        var service = new DragPayloadService();

        var data = service.BuildClipboardPayload([item]);

        Assert.AreEqual("单条文字", data.GetData(DataFormats.UnicodeText));
        Assert.AreEqual("单条文字", data.GetData(DataFormats.Text));
        Assert.IsFalse(data.GetDataPresent(DataFormats.FileDrop));
        Assert.AreEqual(item.Id, service.GetInternalItemId(data));
    }

    [STATestMethod]
    public void BuildClipboardPayload_SingleImageMatchesDragOutDelivery()
    {
        using var directory = new TestDirectory();
        var item = CreateManagedImage(directory, "single.png");
        var service = new DragPayloadService();

        var data = service.BuildClipboardPayload([item]);

        CollectionAssert.AreEqual(
            new[] { item.ImageAbsolutePath! },
            data.GetFileDropList().Cast<string>().ToArray());
        Assert.IsTrue(data.GetDataPresent(DataFormats.Bitmap));
        Assert.AreEqual(item.Id, service.GetInternalItemId(data));
    }

    [STATestMethod]
    public void BuildClipboardPayload_MultipleTextsJoinInBoardOrderWithLineBreaks()
    {
        var pinned = BoardItem.CreateText("置顶文字", Guid.NewGuid(), DateTimeOffset.UtcNow);
        var first = BoardItem.CreateText("第一条", Guid.NewGuid(), DateTimeOffset.UtcNow);
        var second = BoardItem.CreateText("第二条", Guid.NewGuid(), DateTimeOffset.UtcNow);
        var service = new DragPayloadService();

        var data = service.BuildClipboardPayload([pinned, first, second]);

        Assert.AreEqual(
            $"置顶文字{Environment.NewLine}第一条{Environment.NewLine}第二条",
            data.GetData(DataFormats.UnicodeText));
        Assert.AreEqual(
            $"置顶文字{Environment.NewLine}第一条{Environment.NewLine}第二条",
            data.GetData(DataFormats.Text));
        Assert.IsFalse(data.GetDataPresent(DataFormats.FileDrop));
        CollectionAssert.AreEqual(
            new[] { pinned.Id, first.Id, second.Id },
            service.GetInternalItemIds(data)!.ToArray());
    }

    [STATestMethod]
    public void BuildClipboardPayload_ImageOnlyBatchProvidesOrderedFileDropList()
    {
        using var directory = new TestDirectory();
        var first = CreateManagedImage(directory, "first.png");
        var second = CreateManagedImage(directory, "second.png");
        var service = new DragPayloadService();

        var data = service.BuildClipboardPayload([second, first]);

        CollectionAssert.AreEqual(
            new[] { second.ImageAbsolutePath!, first.ImageAbsolutePath! },
            data.GetFileDropList().Cast<string>().ToArray());
        Assert.IsFalse(data.GetDataPresent(DataFormats.Bitmap));
        CollectionAssert.AreEqual(
            new[] { second.Id, first.Id },
            service.GetInternalItemIds(data)!.ToArray());
    }

    [STATestMethod]
    public void BuildClipboardPayload_MixedSelectionMergesTextAndImageFileGroup()
    {
        using var directory = new TestDirectory();
        var imageFirst = CreateManagedImage(directory, "mixed-first.png");
        var text = BoardItem.CreateText("混合中的文字", Guid.NewGuid(), DateTimeOffset.UtcNow);
        var imageSecond = CreateManagedImage(directory, "mixed-second.png");
        var service = new DragPayloadService();

        var data = service.BuildClipboardPayload([imageFirst, text, imageSecond]);

        Assert.AreEqual("混合中的文字", data.GetData(DataFormats.UnicodeText));
        CollectionAssert.AreEqual(
            new[] { imageFirst.ImageAbsolutePath!, imageSecond.ImageAbsolutePath! },
            data.GetFileDropList().Cast<string>().ToArray());
        CollectionAssert.AreEqual(
            new[] { imageFirst.Id, text.Id, imageSecond.Id },
            service.GetInternalItemIds(data)!.ToArray());
    }

    [STATestMethod]
    [TestCategory("Adversarial")]
    public void BuildClipboardPayload_MissingImageAndInvalidInputAreRejected()
    {
        using var directory = new TestDirectory();
        var existing = CreateManagedImage(directory, "existing.png");
        var missing = BoardItem.CreateImage(
            Guid.NewGuid(),
            "images/missing.png",
            Path.Combine(directory.Root, "missing.png"),
            DateTimeOffset.UtcNow);
        var single = BoardItem.CreateText("single", Guid.NewGuid(), DateTimeOffset.UtcNow);
        var service = new DragPayloadService();

        Assert.ThrowsExactly<FileNotFoundException>(() =>
            service.BuildClipboardPayload([existing, missing]));
        Assert.ThrowsExactly<ArgumentException>(() => service.BuildClipboardPayload([]));
        Assert.ThrowsExactly<ArgumentException>(() =>
            service.BuildClipboardPayload([single, single]));
    }

    // ---- 图片拖出为文件（契约 #58，实验性默认关）：门控矩阵两态载荷形状——
    // off＝图片卡/纯图片批剥离外部交付形态只留内部格式（且不校验缺图），on＝
    // 现状＋Preferred DropEffect(Copy)；文本卡、混合批与剪贴板路径两态不变。----

    [STATestMethod]
    public void BuildDrag_ImageDisabledKeepsInternalIdWithoutFileDropOrBitmap()
    {
        using var directory = new TestDirectory();
        var item = CreateManagedImage(directory, "gated.png");
        var service = new DragPayloadService();

        var data = service.BuildDrag(item);

        Assert.AreEqual(item.Id, service.GetInternalItemId(data));
        Assert.IsFalse(data.GetDataPresent(DataFormats.FileDrop), "默认关＝不向外部目标提供文件组。");
        Assert.IsFalse(data.GetDataPresent(DataFormats.Bitmap), "默认关＝不向外部目标提供位图。");
        Assert.IsFalse(
            data.GetDataPresent(DragPayloadService.PreferredDropEffectFormat),
            "默认关＝无 Preferred DropEffect 声明（无文件交付即无效果声明）。");
    }

    [STATestMethod]
    public void BuildDrag_ImageEnabledProvidesFileDropBitmapAndPreferredCopy()
    {
        using var directory = new TestDirectory();
        var item = CreateManagedImage(directory, "enabled.png");
        var service = new DragPayloadService { ExternalFilePayloadsEnabled = true };

        var data = service.BuildDrag(item);

        Assert.AreEqual(item.Id, service.GetInternalItemId(data));
        CollectionAssert.AreEqual(
            new[] { item.ImageAbsolutePath! },
            data.GetFileDropList().Cast<string>().ToArray());
        Assert.IsTrue(data.GetDataPresent(DataFormats.Bitmap));
        Assert.IsInstanceOfType(
            data.GetData(DragPayloadService.PreferredDropEffectFormat),
            typeof(MemoryStream));
        var bytes = ((MemoryStream)data.GetData(DragPayloadService.PreferredDropEffectFormat)!).ToArray();
        Assert.AreEqual(4, bytes.Length, "CFSTR_PREFERREDDROPEFFECT 必须是 4 字节 DROPEFFECT 流。");
        Assert.AreEqual(
            (int)DragDropEffects.Copy,
            BitConverter.ToInt32(bytes, 0),
            "效果值必须是 DROPEFFECT_COPY（防外部目标按 Move 删除受管源）。");
    }

    [STATestMethod]
    public void BuildDrag_TextDeliversTextInBothToggleStates()
    {
        var item = BoardItem.CreateText(
            "开关两态不变的文字",
            Guid.NewGuid(),
            DateTimeOffset.UtcNow);
        foreach (var service in new[]
                 {
                     new DragPayloadService(),
                     new DragPayloadService { ExternalFilePayloadsEnabled = true }
                 })
        {
            var data = service.BuildDrag(item);

            Assert.AreEqual("开关两态不变的文字", data.GetData(DataFormats.UnicodeText));
            Assert.AreEqual("开关两态不变的文字", data.GetData(DataFormats.Text));
            Assert.AreEqual(item.Id, service.GetInternalItemId(data));
            Assert.IsFalse(data.GetDataPresent(DataFormats.FileDrop));
            Assert.IsFalse(data.GetDataPresent(DataFormats.Bitmap));
        }
    }

    [STATestMethod]
    public void BuildDrag_DisabledSkipsMissingImageCheck()
    {
        var item = BoardItem.CreateImage(
            Guid.NewGuid(),
            "images/missing.png",
            "C:/definitely-missing-floating-transfer-station-gated.png",
            DateTimeOffset.UtcNow);
        var service = new DragPayloadService();

        var data = service.BuildDrag(item);

        Assert.AreEqual(item.Id, service.GetInternalItemId(data), "默认关＝剥离交付即剥离缺图校验，内部拖放可继续。");
        Assert.IsFalse(data.GetDataPresent(DataFormats.FileDrop));
    }

    [STATestMethod]
    public void BuildDrag_EnabledMissingImageFailsBeforeDragStarts()
    {
        var item = BoardItem.CreateImage(
            Guid.NewGuid(),
            "images/missing.png",
            "C:/definitely-missing-floating-transfer-station-enabled.png",
            DateTimeOffset.UtcNow);
        var service = new DragPayloadService { ExternalFilePayloadsEnabled = true };

        Assert.ThrowsExactly<FileNotFoundException>(() => service.BuildDrag(item));
    }

    [STATestMethod]
    public void BuildDragBatch_ImageBatchDisabledKeepsInternalIdsWithoutFileDrop()
    {
        using var directory = new TestDirectory();
        var existing = CreateManagedImage(directory, "batch-existing.png");
        var missing = BoardItem.CreateImage(
            Guid.NewGuid(),
            "images/missing.png",
            Path.Combine(directory.Root, "missing.png"),
            DateTimeOffset.UtcNow);
        var service = new DragPayloadService();

        var data = service.BuildDragBatch([existing, missing]);

        CollectionAssert.AreEqual(
            new[] { existing.Id, missing.Id },
            service.GetInternalItemIds(data)!.ToArray());
        Assert.IsFalse(data.GetDataPresent(DataFormats.FileDrop), "默认关＝纯图片批不提供文件组。");
        Assert.IsFalse(data.GetDataPresent(DataFormats.Bitmap));
        Assert.IsFalse(
            data.GetDataPresent(DragPayloadService.PreferredDropEffectFormat),
            "默认关＝批量也剥离缺图校验与效果声明（内部拖放可继续）。");
    }

    [STATestMethod]
    public void BuildDragBatch_ImageBatchEnabledProvidesOrderedFileDropAndPreferredCopy()
    {
        using var directory = new TestDirectory();
        var first = CreateManagedImage(directory, "batch-first.png");
        var second = CreateManagedImage(directory, "batch-second.png");
        var service = new DragPayloadService { ExternalFilePayloadsEnabled = true };

        var data = service.BuildDragBatch([second, first]);

        CollectionAssert.AreEqual(
            new[] { second.Id, first.Id },
            service.GetInternalItemIds(data)!.ToArray());
        CollectionAssert.AreEqual(
            new[] { second.ImageAbsolutePath!, first.ImageAbsolutePath! },
            data.GetFileDropList().Cast<string>().ToArray(),
            "文件组必须按板内顺序。");
        Assert.IsFalse(data.GetDataPresent(DataFormats.Bitmap), "批量拖出与现状同构：无位图。");
        var bytes = ((MemoryStream)data.GetData(DragPayloadService.PreferredDropEffectFormat)!).ToArray();
        Assert.AreEqual(4, bytes.Length);
        Assert.AreEqual((int)DragDropEffects.Copy, BitConverter.ToInt32(bytes, 0));
    }

    [STATestMethod]
    public void BuildDragBatch_MixedBatchStaysInternalOnlyInBothStates()
    {
        using var directory = new TestDirectory();
        var text = BoardItem.CreateText("混合中的文字", Guid.NewGuid(), DateTimeOffset.UtcNow);
        var image = CreateManagedImage(directory, "mixed-gated.png");
        foreach (var service in new[]
                 {
                     new DragPayloadService(),
                     new DragPayloadService { ExternalFilePayloadsEnabled = true }
                 })
        {
            var data = service.BuildDragBatch([text, image]);

            CollectionAssert.AreEqual(
                new[] { text.Id, image.Id },
                service.GetInternalItemIds(data)!.ToArray());
            Assert.IsFalse(data.GetDataPresent(DataFormats.FileDrop), "混合批两态均无外部格式（现状即无）。");
            Assert.IsFalse(data.GetDataPresent(DataFormats.UnicodeText));
            Assert.IsFalse(data.GetDataPresent(DataFormats.Bitmap));
            Assert.IsFalse(data.GetDataPresent(DragPayloadService.PreferredDropEffectFormat));
        }
    }

    private static BoardItem CreateManagedImage(TestDirectory directory, string fileName)
    {
        var path = Path.Combine(directory.Root, fileName);
        using (var image = new Image<Rgba32>(2, 2, SixLabors.ImageSharp.Color.Green))
        {
            image.SaveAsPng(path);
        }

        return BoardItem.CreateImage(
            Guid.NewGuid(),
            $"images/{fileName}",
            path,
            DateTimeOffset.UtcNow);
    }
}
