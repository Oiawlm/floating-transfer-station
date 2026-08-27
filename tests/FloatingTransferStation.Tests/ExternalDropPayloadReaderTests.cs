using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FloatingTransferStation.Tests;

[TestClass]
[TestCategory("Adversarial")]
public sealed class ExternalDropPayloadReaderTests
{
    [STATestMethod]
    [DataRow("5d17aef5-85c7-4819-a670-43943e0c1a52")]
    [DataRow("not-a-guid")]
    public void InternalFormatPresenceRejectsAppOwnedDataRegardlessOfIdentityValue(string identity)
    {
        var data = new DataObject();
        data.SetData(DragPayloadService.InternalItemIdFormat, identity);
        data.SetData(DataFormats.UnicodeText, "external text");
        var reader = new ExternalDropPayloadReader();

        Assert.IsFalse(reader.CanRead(data));
        Assert.IsNull(reader.Read(data));
    }

    [STATestMethod]
    [DataRow("photo.jpg", true)]
    [DataRow("photo.JPEG", true)]
    [DataRow("photo.png", true)]
    [DataRow("photo.BMP", true)]
    [DataRow("photo.gif", true)]
    [DataRow("photo.TIF", true)]
    [DataRow("photo.tiff", true)]
    [DataRow("photo.WEBP", true)]
    [DataRow("photo.pdf", false)]
    [DataRow("   ", false)]
    public void ImageFileSupportUsesFixedCaseInsensitiveExtensionSet(string path, bool expected)
    {
        Assert.AreEqual(expected, ImageFileSupport.IsSupported(path));
    }

    [STATestMethod]
    public void FileDropWithOnlyExistingImagesPreservesSourceOrder()
    {
        using var directory = new TestDirectory();
        var first = Path.Combine(directory.Root, "first.PNG");
        var second = Path.Combine(directory.Root, "second.webp");
        WriteImage(first);
        WriteImage(second);
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { second, first });
        data.SetData("PNG", new byte[] { 9, 9, 9 });
        data.SetData(DataFormats.UnicodeText, "ignored text");
        var reader = new ExternalDropPayloadReader();

        Assert.IsTrue(reader.CanRead(data));
        var payload = reader.Read(data);

        Assert.IsInstanceOfType<ExternalDropPayload.ImageFiles>(payload);
        CollectionAssert.AreEqual(
            new[] { second, first },
            ((ExternalDropPayload.ImageFiles)payload).Paths.ToArray());
    }

    [STATestMethod]
    public void FileDropAcceptsStringCollectionShape()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "image.jpg");
        WriteImage(path);
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new StringCollection { path });
        var reader = new ExternalDropPayloadReader();

        var payload = reader.Read(data);

        Assert.IsInstanceOfType<ExternalDropPayload.ImageFiles>(payload);
        CollectionAssert.AreEqual(
            new[] { path },
            ((ExternalDropPayload.ImageFiles)payload).Paths.ToArray());
    }

    [STATestMethod]
    public void FileDropAcceptsSafelySnapshotableStringEnumerable()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "image.tiff");
        WriteImage(path);
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new List<string> { path });

        var payload = new ExternalDropPayloadReader().Read(data);

        Assert.IsInstanceOfType<ExternalDropPayload.ImageFiles>(payload);
        CollectionAssert.AreEqual(
            new[] { path },
            ((ExternalDropPayload.ImageFiles)payload).Paths.ToArray());
    }

    [STATestMethod]
    [DataRow("jpg")]
    [DataRow("png")]
    [DataRow("bmp")]
    [DataRow("webp")]
    [DataRow("tiff")]
    public void CommonSingleFrameImageFileDropIsAccepted(string extension)
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, $"static.{extension}");
        WriteImage(path);
        var data = new DataObject(DataFormats.FileDrop, new[] { path });
        var reader = new ExternalDropPayloadReader();

        Assert.IsTrue(reader.CanRead(data));
        Assert.IsInstanceOfType<ExternalDropPayload.ImageFiles>(reader.Read(data));
    }

    [STATestMethod]
    public void StaticGifFileDropIsAccepted()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "static.gif");
        WriteImage(path);
        var data = new DataObject(DataFormats.FileDrop, new[] { path });
        var reader = new ExternalDropPayloadReader();

        Assert.IsTrue(reader.CanRead(data));
        var payload = reader.Read(data);

        Assert.IsInstanceOfType<ExternalDropPayload.ImageFiles>(payload);
        CollectionAssert.AreEqual(
            new[] { path },
            ((ExternalDropPayload.ImageFiles)payload).Paths.ToArray());
    }

    [STATestMethod]
    [DataRow("gif")]
    [DataRow("png")]
    [DataRow("webp")]
    [DataRow("tiff")]
    public void FileDropBatchContainingMultiFrameImageIsRejectedWithoutFallback(string extension)
    {
        using var directory = new TestDirectory();
        var staticPath = Path.Combine(directory.Root, "static.png");
        var multiFramePath = Path.Combine(directory.Root, $"multi-frame.{extension}");
        WriteImage(staticPath);
        WriteImage(multiFramePath, frameCount: 2);
        using (var decoded = Image.Load(multiFramePath))
        {
            Assert.AreEqual(2, decoded.Frames.Count, "The fixture must contain two decoded frames.");
        }

        if (!extension.Equals("png", StringComparison.OrdinalIgnoreCase))
        {
            var information = Image.Identify(multiFramePath);
            Assert.AreEqual(
                2,
                information.FrameMetadataCollection.Count,
                "The fixture must expose two frames through metadata identification.");
        }

        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { staticPath, multiFramePath });
        data.SetData("PNG", new byte[] { 1, 2, 3 });
        data.SetData(DataFormats.UnicodeText, "must not fall back");
        var reader = new ExternalDropPayloadReader();

        var canRead = reader.CanRead(data);
        var payload = reader.Read(data);

        Assert.AreEqual(
            (CanRead: false, PayloadIsNull: true),
            (CanRead: canRead, PayloadIsNull: payload is null));
    }

    [STATestMethod]
    public void CorruptSupportedExtensionFileDropIsRejectedWithoutFallback()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "corrupt.png");
        File.WriteAllBytes(path, [1, 2, 3]);
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { path });
        data.SetData(DataFormats.UnicodeText, "must not fall back");
        var reader = new ExternalDropPayloadReader();

        var canRead = reader.CanRead(data);
        var payload = reader.Read(data);

        Assert.AreEqual(
            (CanRead: false, PayloadIsNull: true),
            (CanRead: canRead, PayloadIsNull: payload is null));
    }

    [STATestMethod]
    public void EmptyFileDropIsRejected()
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, Array.Empty<string>());
        var reader = new ExternalDropPayloadReader();

        Assert.IsFalse(reader.CanRead(data));
        Assert.IsNull(reader.Read(data));
    }

    [STATestMethod]
    public void MixedFileDropRejectsWholeDataObjectWithoutImageOrTextFallback()
    {
        using var directory = new TestDirectory();
        var image = Path.Combine(directory.Root, "image.png");
        var textFile = Path.Combine(directory.Root, "notes.txt");
        WriteImage(image);
        File.WriteAllText(textFile, "notes");
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { image, textFile });
        data.SetData("PNG", new byte[] { 1, 2, 3 });
        data.SetData(DataFormats.UnicodeText, image);
        var reader = new ExternalDropPayloadReader();

        Assert.IsFalse(reader.CanRead(data));
        Assert.IsNull(reader.Read(data));
    }

    [STATestMethod]
    public void FolderFileDropIsRejectedWithoutFallback()
    {
        using var directory = new TestDirectory();
        var folder = Path.Combine(directory.Root, "folder.png");
        Directory.CreateDirectory(folder);
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { folder });
        data.SetData("PNG", new byte[] { 1, 2, 3 });
        data.SetData(DataFormats.UnicodeText, folder);
        var reader = new ExternalDropPayloadReader();

        Assert.IsFalse(reader.CanRead(data));
        Assert.IsNull(reader.Read(data));
    }

    [STATestMethod]
    public void MissingImageFileDropIsRejectedWithoutFallback()
    {
        using var directory = new TestDirectory();
        var missing = Path.Combine(directory.Root, "missing.jpeg");
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { missing });
        data.SetData("PNG", new byte[] { 1, 2, 3 });
        data.SetData(DataFormats.UnicodeText, missing);
        var reader = new ExternalDropPayloadReader();

        Assert.IsFalse(reader.CanRead(data));
        Assert.IsNull(reader.Read(data));
    }

    [STATestMethod]
    public void HostilePresenceAccessorReturnsUnsupported()
    {
        var reader = new ExternalDropPayloadReader();
        var data = new ThrowingDataObject();

        Assert.IsFalse(reader.CanRead(data));
        Assert.IsNull(reader.Read(data));
    }

    [STATestMethod]
    public void AdvertisedFileDropSnapshotFailuresRejectWithoutFallback()
    {
        var reader = new ExternalDropPayloadReader();
        IDataObject[] dataObjects =
        [
            new FailingFileDropDataObject(failDuringEnumeration: false),
            new FailingFileDropDataObject(failDuringEnumeration: true)
        ];

        foreach (var data in dataObjects)
        {
            Assert.IsFalse(reader.CanRead(data));
            Assert.IsNull(reader.Read(data));
        }
    }

    [STATestMethod]
    public void FileDropPresenceFailureRejectsWithoutFallback()
    {
        var data = new FileDropPresenceThrowingDataObject();
        var reader = new ExternalDropPayloadReader();

        Assert.IsFalse(reader.CanRead(data));
        Assert.IsNull(reader.Read(data));
    }

    [STATestMethod]
    public void ImageCandidatesWinOverAttachedText()
    {
        var data = new DataObject();
        data.SetData("PNG", new byte[] { 1, 2, 3 });
        data.SetData(DataFormats.UnicodeText, "ignored text");
        var reader = new ExternalDropPayloadReader();

        Assert.IsTrue(reader.CanRead(data));
        var payload = reader.Read(data);

        Assert.IsInstanceOfType<ExternalDropPayload.ImageCandidates>(payload);
        var candidates = ((ExternalDropPayload.ImageCandidates)payload).Candidates;
        Assert.HasCount(1, candidates);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, candidates[0].EncodedBytes.ToArray());
    }

    [STATestMethod]
    public void CanReadDoesNotConsumeOneShotEncodedDataAndReadSnapshotsOnce()
    {
        using var stream = new MemoryStream([10, 20, 30, 40]);
        stream.Position = 2;
        var data = new OneShotEncodedImageDataObject(stream);
        var reader = new ExternalDropPayloadReader();

        Assert.IsTrue(reader.CanRead(data));
        Assert.AreEqual(2, stream.Position);

        var payload = reader.Read(data);

        Assert.IsInstanceOfType<ExternalDropPayload.ImageCandidates>(payload);
        var candidate = ((ExternalDropPayload.ImageCandidates)payload).Candidates.Single();
        CollectionAssert.AreEqual(new byte[] { 10, 20, 30, 40 }, candidate.EncodedBytes.ToArray());
        Assert.AreEqual(2, stream.Position);
    }

    [STATestMethod]
    public void NonblankUnicodeTextWinsOverText()
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, "Unicode value");
        data.SetData(DataFormats.Text, "ANSI value");
        var reader = new ExternalDropPayloadReader();

        Assert.IsTrue(reader.CanRead(data));
        var payload = reader.Read(data);

        Assert.IsInstanceOfType<ExternalDropPayload.Text>(payload);
        Assert.AreEqual("Unicode value", ((ExternalDropPayload.Text)payload).Value);
    }

    [STATestMethod]
    public void BlankTextIsRejected()
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, " \r\n\t");
        data.SetData(DataFormats.Text, "   ");
        var reader = new ExternalDropPayloadReader();

        Assert.IsFalse(reader.CanRead(data));
        Assert.IsNull(reader.Read(data));
    }

    [STATestMethod]
    public void TextFallsBackWhenUnicodeTextIsBlank()
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, "   ");
        data.SetData(DataFormats.Text, "fallback value");
        var reader = new ExternalDropPayloadReader();

        Assert.IsTrue(reader.CanRead(data));
        var payload = reader.Read(data);

        Assert.IsInstanceOfType<ExternalDropPayload.Text>(payload);
        Assert.AreEqual("fallback value", ((ExternalDropPayload.Text)payload).Value);
    }

    [STATestMethod]
    public void SelectedTextAccessorFailureReturnsUnsupportedWithoutFallback()
    {
        var data = new ThrowingSelectedTextDataObject();
        var reader = new ExternalDropPayloadReader();

        Assert.IsFalse(reader.CanRead(data));
        Assert.IsNull(reader.Read(data));
    }

    [STATestMethod]
    public void OptionalImageFailureFallsBackToText()
    {
        var data = new FailingOptionalImageDataObject();
        var reader = new ExternalDropPayloadReader();

        Assert.IsTrue(reader.CanRead(data));
        var payload = reader.Read(data);

        Assert.IsInstanceOfType<ExternalDropPayload.Text>(payload);
        Assert.AreEqual("fallback text", ((ExternalDropPayload.Text)payload).Value);
    }

    private static void WriteImage(string path, int frameCount = 1)
    {
        using var image = new Image<Rgba32>(2, 2, Color.Red);
        for (var frame = 1; frame < frameCount; frame++)
        {
            image.Frames.AddFrame(image.Frames.RootFrame);
        }

        image.Save(path);
    }

    private sealed class ThrowingDataObject : IDataObject
    {
        public object? GetData(string format, bool autoConvert) =>
            throw new InvalidOperationException("hostile");

        public object? GetData(string format) => throw new InvalidOperationException("hostile");
        public object? GetData(Type format) => throw new InvalidOperationException("hostile");

        public bool GetDataPresent(string format, bool autoConvert) =>
            throw new InvalidOperationException("hostile");

        public bool GetDataPresent(string format) => throw new InvalidOperationException("hostile");
        public bool GetDataPresent(Type format) => throw new InvalidOperationException("hostile");
        public string[] GetFormats(bool autoConvert) => throw new InvalidOperationException("hostile");
        public string[] GetFormats() => throw new InvalidOperationException("hostile");
        public void SetData(string format, object data, bool autoConvert) => throw new NotSupportedException();
        public void SetData(string format, object data) => throw new NotSupportedException();
        public void SetData(Type format, object data) => throw new NotSupportedException();
        public void SetData(object data) => throw new NotSupportedException();
    }

    private sealed class FailingFileDropDataObject(bool failDuringEnumeration) : IDataObject
    {
        public object? GetData(string format, bool autoConvert)
        {
            if (format == DataFormats.FileDrop)
            {
                return failDuringEnumeration
                    ? new ThrowingStringEnumerable()
                    : throw new InvalidOperationException("foreign file-drop provider failed");
            }

            if (format == "PNG")
            {
                return new byte[] { 1, 2, 3 };
            }

            return format == DataFormats.UnicodeText ? "fallback text" : null;
        }

        public object? GetData(string format) => GetData(format, autoConvert: true);
        public object? GetData(Type format) => GetData(format.FullName ?? format.Name, autoConvert: true);

        public bool GetDataPresent(string format, bool autoConvert) =>
            format == DataFormats.FileDrop ||
            format == "PNG" ||
            format == DataFormats.UnicodeText;

        public bool GetDataPresent(string format) => GetDataPresent(format, autoConvert: true);
        public bool GetDataPresent(Type format) => GetDataPresent(format.FullName ?? format.Name, autoConvert: true);
        public string[] GetFormats(bool autoConvert) => [DataFormats.FileDrop, "PNG", DataFormats.UnicodeText];
        public string[] GetFormats() => GetFormats(autoConvert: true);
        public void SetData(string format, object data, bool autoConvert) => throw new NotSupportedException();
        public void SetData(string format, object data) => throw new NotSupportedException();
        public void SetData(Type format, object data) => throw new NotSupportedException();
        public void SetData(object data) => throw new NotSupportedException();
    }

    private sealed class FileDropPresenceThrowingDataObject : IDataObject
    {
        public object? GetData(string format, bool autoConvert) => format switch
        {
            "PNG" => new byte[] { 1, 2, 3 },
            _ => format == DataFormats.UnicodeText ? "must not fall back" : null
        };

        public object? GetData(string format) => GetData(format, autoConvert: true);
        public object? GetData(Type format) => GetData(format.FullName ?? format.Name, autoConvert: true);

        public bool GetDataPresent(string format, bool autoConvert)
        {
            if (format == DataFormats.FileDrop)
            {
                throw new InvalidOperationException("foreign file-drop inspection failed");
            }

            return format == "PNG" || format == DataFormats.UnicodeText;
        }

        public bool GetDataPresent(string format) => GetDataPresent(format, autoConvert: true);
        public bool GetDataPresent(Type format) => GetDataPresent(format.FullName ?? format.Name, autoConvert: true);
        public string[] GetFormats(bool autoConvert) => [DataFormats.FileDrop, "PNG", DataFormats.UnicodeText];
        public string[] GetFormats() => GetFormats(autoConvert: true);
        public void SetData(string format, object data, bool autoConvert) => throw new NotSupportedException();
        public void SetData(string format, object data) => throw new NotSupportedException();
        public void SetData(Type format, object data) => throw new NotSupportedException();
        public void SetData(object data) => throw new NotSupportedException();
    }

    private sealed class OneShotEncodedImageDataObject(Stream stream) : IDataObject
    {
        private bool _retrieved;

        public object? GetData(string format, bool autoConvert)
        {
            if (format != "PNG")
            {
                return null;
            }

            if (_retrieved)
            {
                throw new InvalidOperationException("encoded image was retrieved more than once");
            }

            _retrieved = true;
            return stream;
        }

        public object? GetData(string format) => GetData(format, autoConvert: true);
        public object? GetData(Type format) => GetData(format.FullName ?? format.Name, autoConvert: true);
        public bool GetDataPresent(string format, bool autoConvert) => format == "PNG";
        public bool GetDataPresent(string format) => GetDataPresent(format, autoConvert: true);
        public bool GetDataPresent(Type format) => GetDataPresent(format.FullName ?? format.Name, autoConvert: true);
        public string[] GetFormats(bool autoConvert) => ["PNG"];
        public string[] GetFormats() => GetFormats(autoConvert: true);
        public void SetData(string format, object data, bool autoConvert) => throw new NotSupportedException();
        public void SetData(string format, object data) => throw new NotSupportedException();
        public void SetData(Type format, object data) => throw new NotSupportedException();
        public void SetData(object data) => throw new NotSupportedException();
    }

    private sealed class ThrowingSelectedTextDataObject : IDataObject
    {
        public object? GetData(string format, bool autoConvert)
        {
            if (format == DataFormats.UnicodeText)
            {
                throw new InvalidOperationException("selected text provider failed");
            }

            return format == DataFormats.Text ? "must not fall back" : null;
        }

        public object? GetData(string format) => GetData(format, autoConvert: true);
        public object? GetData(Type format) => GetData(format.FullName ?? format.Name, autoConvert: true);

        public bool GetDataPresent(string format, bool autoConvert) =>
            format == DataFormats.UnicodeText || format == DataFormats.Text;

        public bool GetDataPresent(string format) => GetDataPresent(format, autoConvert: true);
        public bool GetDataPresent(Type format) => GetDataPresent(format.FullName ?? format.Name, autoConvert: true);
        public string[] GetFormats(bool autoConvert) => [DataFormats.UnicodeText, DataFormats.Text];
        public string[] GetFormats() => GetFormats(autoConvert: true);
        public void SetData(string format, object data, bool autoConvert) => throw new NotSupportedException();
        public void SetData(string format, object data) => throw new NotSupportedException();
        public void SetData(Type format, object data) => throw new NotSupportedException();
        public void SetData(object data) => throw new NotSupportedException();
    }

    private sealed class FailingOptionalImageDataObject : IDataObject
    {
        public object? GetData(string format, bool autoConvert)
        {
            if (format == "PNG")
            {
                throw new InvalidOperationException("optional image provider failed");
            }

            return format == DataFormats.UnicodeText ? "fallback text" : null;
        }

        public object? GetData(string format) => GetData(format, autoConvert: true);
        public object? GetData(Type format) => GetData(format.FullName ?? format.Name, autoConvert: true);

        public bool GetDataPresent(string format, bool autoConvert) =>
            format == "PNG" || format == DataFormats.UnicodeText;

        public bool GetDataPresent(string format) => GetDataPresent(format, autoConvert: true);
        public bool GetDataPresent(Type format) => GetDataPresent(format.FullName ?? format.Name, autoConvert: true);
        public string[] GetFormats(bool autoConvert) => ["PNG", DataFormats.UnicodeText];
        public string[] GetFormats() => GetFormats(autoConvert: true);
        public void SetData(string format, object data, bool autoConvert) => throw new NotSupportedException();
        public void SetData(string format, object data) => throw new NotSupportedException();
        public void SetData(Type format, object data) => throw new NotSupportedException();
        public void SetData(object data) => throw new NotSupportedException();
    }

    private sealed class ThrowingStringEnumerable : IEnumerable<string>
    {
        public IEnumerator<string> GetEnumerator() =>
            throw new InvalidOperationException("foreign enumeration failed");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
