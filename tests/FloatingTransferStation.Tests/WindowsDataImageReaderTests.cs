using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class WindowsDataImageReaderTests
{
    private static readonly string[] EncodedFormats = ["PNG", "image/png", "JFIF", "image/jpeg"];

    [STATestMethod]
    public void ReadCandidates_CopiesAllSupportedEncodedRepresentationsInFormatOrder()
    {
        var data = new DataObject();
        data.SetData("PNG", new byte[] { 1 });
        data.SetData("image/png", new byte[] { 2 });
        data.SetData("JFIF", new byte[] { 3 });
        data.SetData("image/jpeg", new byte[] { 4 });

        var candidates = new WindowsDataImageReader().ReadCandidates(data);

        CollectionAssert.AreEqual(EncodedFormats, candidates.Select(candidate => candidate.Format).ToArray());
        CollectionAssert.AreEqual(
            new byte[] { 1, 2, 3, 4 },
            candidates.Select(candidate => candidate.EncodedBytes.Span[0]).ToArray());
    }

    [STATestMethod]
    public void CanRead_DoesNotConsumeEncodedStream()
    {
        using var stream = new MemoryStream([10, 20, 30, 40]);
        stream.Position = 2;
        var data = new DataObject();
        data.SetData("PNG", stream);

        Assert.IsTrue(new WindowsDataImageReader().CanRead(data));
        Assert.AreEqual(2, stream.Position);
    }

    [STATestMethod]
    public void ReadCandidates_CopiesWholeSeekableStreamAndRestoresPosition()
    {
        using var stream = new MemoryStream([10, 20, 30, 40]);
        stream.Position = 2;
        var data = new DataObject();
        data.SetData("PNG", stream);

        var candidate = new WindowsDataImageReader().ReadCandidates(data).Single();

        CollectionAssert.AreEqual(
            new byte[] { 10, 20, 30, 40 },
            candidate.EncodedBytes.ToArray());
        Assert.AreEqual(2, stream.Position);
    }

    [STATestMethod]
    public void ReadCandidates_SnapshotsAndFreezesBitmap()
    {
        var bitmap = BitmapSource.Create(
            1,
            1,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            new byte[] { 0, 0, 255, 255 },
            4);
        var data = new DataObject();
        data.SetData(DataFormats.Bitmap, bitmap);

        var candidate = new WindowsDataImageReader().ReadCandidates(data).Single();

        Assert.IsTrue(candidate.IsBitmap);
        Assert.IsNotNull(candidate.Bitmap);
        Assert.IsTrue(candidate.Bitmap.IsFrozen);
        Assert.AreNotSame(bitmap, candidate.Bitmap);
    }

    [STATestMethod]
    public void ReadCandidates_DeduplicatesOnlyIdenticalEncodedBytes()
    {
        var data = new DataObject();
        data.SetData("PNG", new byte[] { 1, 2, 3 });
        data.SetData("image/png", new byte[] { 1, 2, 3 });
        data.SetData("JFIF", new byte[] { 1, 2, 4 });

        var candidates = new WindowsDataImageReader().ReadCandidates(data);

        CollectionAssert.AreEqual(
            new[] { "PNG", "JFIF" },
            candidates.Select(candidate => candidate.Format).ToArray());
    }

    [STATestMethod]
    public void ReadCandidates_KeepsBitmapAlongsideIdenticalEncodedRepresentations()
    {
        var bitmap = BitmapSource.Create(
            1,
            1,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            new byte[] { 1, 2, 3, 4 },
            4);
        var data = new DataObject();
        data.SetData("PNG", new byte[] { 1, 2, 3 });
        data.SetData("image/png", new byte[] { 1, 2, 3 });
        data.SetData(DataFormats.Bitmap, bitmap);

        var candidates = new WindowsDataImageReader().ReadCandidates(data);

        Assert.HasCount(2, candidates);
        Assert.IsFalse(candidates[0].IsBitmap);
        Assert.IsTrue(candidates[1].IsBitmap);
    }

    [STATestMethod]
    public void AccessorFailures_ReturnUnsupportedAndNoCandidates()
    {
        var data = new ThrowingDataObject();
        var reader = new WindowsDataImageReader();

        Assert.IsFalse(reader.CanRead(data));
        Assert.IsEmpty(reader.ReadCandidates(data));
    }

    [STATestMethod]
    public void ReadCandidates_LaterAccessorFailureDiscardsEarlierCandidate()
    {
        var data = new SequencedImageDataObject(
            new Dictionary<string, object?>
            {
                ["PNG"] = new byte[] { 1, 2, 3 },
                ["image/png"] = new InvalidOperationException("foreign provider failed")
            });

        var candidates = new WindowsDataImageReader().ReadCandidates(data);

        Assert.IsEmpty(candidates);
    }

    [STATestMethod]
    public void ReadCandidates_LaterStreamFailureDiscardsEarlierCandidateAndRestoresPosition()
    {
        using var stream = new SeekableThrowingReadStream(length: 8, position: 3);
        var data = new SequencedImageDataObject(
            new Dictionary<string, object?>
            {
                ["PNG"] = new byte[] { 1, 2, 3 },
                ["image/png"] = stream
            });

        var candidates = new WindowsDataImageReader().ReadCandidates(data);

        Assert.IsEmpty(candidates);
        Assert.AreEqual(3, stream.Position);
    }

    [STATestMethod]
    public void FilesAndTextAreNotInspected()
    {
        var data = new ImageOnlyProbeDataObject();
        var reader = new WindowsDataImageReader();

        Assert.IsFalse(reader.CanRead(data));
        Assert.IsEmpty(reader.ReadCandidates(data));
        CollectionAssert.DoesNotContain(data.RequestedFormats, DataFormats.FileDrop);
        CollectionAssert.DoesNotContain(data.RequestedFormats, DataFormats.UnicodeText);
        CollectionAssert.DoesNotContain(data.RequestedFormats, DataFormats.Text);
    }

    private sealed class ThrowingDataObject : IDataObject
    {
        public object? GetData(string format, bool autoConvert) => throw new InvalidOperationException("hostile");
        public object? GetData(string format) => throw new InvalidOperationException("hostile");
        public object? GetData(Type format) => throw new InvalidOperationException("hostile");
        public bool GetDataPresent(string format, bool autoConvert) => throw new InvalidOperationException("hostile");
        public bool GetDataPresent(string format) => throw new InvalidOperationException("hostile");
        public bool GetDataPresent(Type format) => throw new InvalidOperationException("hostile");
        public string[] GetFormats(bool autoConvert) => throw new InvalidOperationException("hostile");
        public string[] GetFormats() => throw new InvalidOperationException("hostile");
        public void SetData(string format, object data, bool autoConvert) => throw new NotSupportedException();
        public void SetData(string format, object data) => throw new NotSupportedException();
        public void SetData(Type format, object data) => throw new NotSupportedException();
        public void SetData(object data) => throw new NotSupportedException();
    }

    private sealed class ImageOnlyProbeDataObject : IDataObject
    {
        public List<string> RequestedFormats { get; } = [];

        public object? GetData(string format, bool autoConvert)
        {
            RequestedFormats.Add(format);
            return null;
        }

        public object? GetData(string format) => GetData(format, autoConvert: true);
        public object? GetData(Type format) => GetData(format.FullName ?? format.Name, autoConvert: true);

        public bool GetDataPresent(string format, bool autoConvert)
        {
            RequestedFormats.Add(format);
            return false;
        }

        public bool GetDataPresent(string format) => GetDataPresent(format, autoConvert: true);
        public bool GetDataPresent(Type format) => GetDataPresent(format.FullName ?? format.Name, autoConvert: true);
        public string[] GetFormats(bool autoConvert) => [];
        public string[] GetFormats() => [];
        public void SetData(string format, object data, bool autoConvert) => throw new NotSupportedException();
        public void SetData(string format, object data) => throw new NotSupportedException();
        public void SetData(Type format, object data) => throw new NotSupportedException();
        public void SetData(object data) => throw new NotSupportedException();
    }

    private sealed class SequencedImageDataObject(IReadOnlyDictionary<string, object?> values) : IDataObject
    {
        public object? GetData(string format, bool autoConvert)
        {
            var value = values[format];
            return value is Exception exception ? throw exception : value;
        }

        public object? GetData(string format) => GetData(format, autoConvert: true);
        public object? GetData(Type format) => GetData(format.FullName ?? format.Name, autoConvert: true);
        public bool GetDataPresent(string format, bool autoConvert) => values.ContainsKey(format);
        public bool GetDataPresent(string format) => GetDataPresent(format, autoConvert: true);
        public bool GetDataPresent(Type format) => GetDataPresent(format.FullName ?? format.Name, autoConvert: true);
        public string[] GetFormats(bool autoConvert) => values.Keys.ToArray();
        public string[] GetFormats() => GetFormats(autoConvert: true);
        public void SetData(string format, object data, bool autoConvert) => throw new NotSupportedException();
        public void SetData(string format, object data) => throw new NotSupportedException();
        public void SetData(Type format, object data) => throw new NotSupportedException();
        public void SetData(object data) => throw new NotSupportedException();
    }

    private sealed class SeekableThrowingReadStream(long length, long position) : Stream
    {
        private long _position = position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _position = Math.Min(Length, _position + 1);
            throw new IOException("foreign stream failed during read");
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
