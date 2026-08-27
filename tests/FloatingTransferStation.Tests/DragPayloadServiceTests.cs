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
