using System.Windows;
using System.Runtime.InteropServices;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

[TestClass]
[TestCategory("Adversarial")]
public sealed class ClipboardPayloadReaderTests
{
    [STATestMethod]
    public void Read_ExcludedContentIsSkippedBeforeAnyPayloadAccess()
    {
        var snapshot = new ClipboardPayloadReader().Read(new PrivateDataObject(), 81);

        Assert.AreEqual(81u, snapshot.SequenceNumber);
        Assert.IsNull(snapshot.Text);
        Assert.IsEmpty(snapshot.FilePaths);
        Assert.IsEmpty(snapshot.ImageCandidates);
    }

    [STATestMethod]
    public void Read_InternalSingleItemDeliveryIsSkippedWithoutReCapture()
    {
        var data = new DataObject();
        data.SetData(DragPayloadService.InternalItemIdFormat, Guid.NewGuid().ToString("D"));
        data.SetData(DataFormats.UnicodeText, "应用自身复制的卡片文字");

        var snapshot = new ClipboardPayloadReader().Read(data, 91);

        Assert.AreEqual(91u, snapshot.SequenceNumber);
        Assert.IsNull(snapshot.Text, "应用自身交付的卡片复制不得被自动收集重新入库。");
        Assert.IsEmpty(snapshot.FilePaths);
        Assert.IsEmpty(snapshot.ImageCandidates);
    }

    [STATestMethod]
    public void Read_InternalBatchDeliveryIsSkippedIncludingFileGroups()
    {
        var data = new DataObject();
        data.SetData(
            DragPayloadService.InternalItemIdsFormat,
            new[] { Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D") });
        data.SetFileDropList(new System.Collections.Specialized.StringCollection
        {
            @"C:\synthetic\first.png",
            @"C:\synthetic\second.png"
        });

        var snapshot = new ClipboardPayloadReader().Read(data, 92);

        Assert.IsNull(snapshot.Text);
        Assert.IsEmpty(snapshot.FilePaths);
        Assert.IsEmpty(snapshot.ImageCandidates);
    }

    [STATestMethod]
    public void Read_ExternalCopyWithoutInternalMarkerIsStillCaptured()
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, "外部应用的普通复制");

        Assert.AreEqual(
            "外部应用的普通复制",
            new ClipboardPayloadReader().Read(data, 93).Text);
    }

    [STATestMethod]
    [DataRow(0, false)]
    [DataRow(1, true)]
    [DataRow(2, false)]
    public void Read_HistoryDwordControlsAutomaticCapture(int value, bool expectedCapture)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, "synthetic private text");
        data.SetData("CanIncludeInClipboardHistory", BitConverter.GetBytes(value));

        var snapshot = new ClipboardPayloadReader().Read(data, 82);

        Assert.AreEqual(expectedCapture ? "synthetic private text" : null, snapshot.Text);
    }

    [STATestMethod]
    public void Read_HistoryStreamReadsFromStartAndRestoresSourcePosition()
    {
        using var flag = new MemoryStream([0, 0, 0, 0]);
        flag.Position = 3;
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, "synthetic private text");
        data.SetData("CanIncludeInClipboardHistory", flag);

        var snapshot = new ClipboardPayloadReader().Read(data, 83);

        Assert.IsNull(snapshot.Text);
        Assert.AreEqual(3L, flag.Position);
    }

    [STATestMethod]
    public void Read_MalformedHistoryFlagDoesNotCaptureContent()
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, "synthetic private text");
        data.SetData("CanIncludeInClipboardHistory", Array.Empty<byte>());

        Assert.IsNull(new ClipboardPayloadReader().Read(data, 84).Text);
    }

    [STATestMethod]
    public void Read_CloudOnlyExclusionDoesNotSuppressLocalCapture()
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, "ordinary text");
        data.SetData("CanUploadToCloudClipboard", new byte[4]);

        Assert.AreEqual("ordinary text", new ClipboardPayloadReader().Read(data, 85).Text);
    }

    [STATestMethod]
    public void Read_FileDropSkipsNullAndBlankEntriesBeforeSnapshotAccounting()
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new string[] { null!, "", " ", "synthetic.png" });

        CollectionAssert.AreEqual(
            new[] { "synthetic.png" },
            new ClipboardPayloadReader().Read(data, 87).FilePaths.ToArray());
    }

    [STATestMethod]
    public async Task Read_ExcludedSnapshotDoesNotCreateBoardOrBackupFiles()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var snapshot = new ClipboardPayloadReader().Read(new PrivateDataObject(), 86);
        var board = new BoardService();
        var service = new ClipboardCaptureService(
            new SnapshotReader(snapshot), new ImageNormalizer(paths.ImagesDirectory),
            board, new LocalStore(paths, new AtomicTextWriter()), _ => { });

        await service.HandleClipboardUpdateAsync();

        Assert.IsEmpty(board.CreateSnapshot().Items);
        Assert.IsFalse(File.Exists(paths.BoardFile));
        Assert.IsFalse(File.Exists(paths.BoardFile + ".bak"));
    }

    [STATestMethod]
    public void ReadStable_SequenceChangedDuringAcquisitionRequiresRetry()
    {
        uint sequence = 90;
        var data = new DataObject(DataFormats.UnicodeText, "old synthetic content");

        Assert.ThrowsExactly<ExternalException>(() => new ClipboardPayloadReader().ReadStable(
            () => { sequence = 91; return data; },
            () => sequence));
    }

    [STATestMethod]
    public async Task ReadStable_ChangedSequenceIsRetriedWithoutSuppressingNewContent()
    {
        using var directory = new TestDirectory();
        var reader = new ChangingClipboardReader();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var service = new ClipboardCaptureService(
            reader, new ImageNormalizer(paths.ImagesDirectory), board,
            new LocalStore(paths, new AtomicTextWriter()), _ => { }, retryDelays: [TimeSpan.Zero]);

        await service.HandleClipboardUpdateAsync();
        await service.HandleClipboardUpdateAsync();

        Assert.AreEqual("new content", board.Items(BoardCategory.Inbox).Single().Text);
        Assert.AreEqual(2, reader.ReadCount);
    }

    private sealed class ChangingClipboardReader : IClipboardReader
    {
        private uint _sequence = 92;
        public int ReadCount { get; private set; }
        public uint? GetSequenceNumber() => _sequence;
        public Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClipboardPayloadReader().ReadStable(
                () =>
                {
                    ReadCount++;
                    var data = new DataObject(DataFormats.UnicodeText, ReadCount == 1 ? "old content" : "new content");
                    _sequence = 93;
                    return data;
                },
                () => _sequence));
    }

    private sealed class SnapshotReader(ClipboardSnapshot snapshot) : IClipboardReader
    {
        public Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class PrivateDataObject : IDataObject
    {
        public object? GetData(string format, bool autoConvert) =>
            throw new InvalidOperationException("Private payload must not be requested.");
        public bool GetDataPresent(string format, bool autoConvert) =>
            format == "ExcludeClipboardContentFromMonitorProcessing"
                ? true
                : throw new InvalidOperationException("Private payload must not be inspected.");
        public object? GetData(string format) => GetData(format, true);
        public object? GetData(Type format) => throw new NotSupportedException();
        public bool GetDataPresent(string format) => GetDataPresent(format, true);
        public bool GetDataPresent(Type format) => throw new NotSupportedException();
        public string[] GetFormats() => throw new NotSupportedException();
        public string[] GetFormats(bool autoConvert) => throw new NotSupportedException();
        public void SetData(string format, object data, bool autoConvert) => throw new NotSupportedException();
        public void SetData(string format, object data) => throw new NotSupportedException();
        public void SetData(Type format, object data) => throw new NotSupportedException();
        public void SetData(object data) => throw new NotSupportedException();
    }
}
