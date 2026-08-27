using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class ClipboardCaptureServiceTests
{
    [STATestMethod]
    public async Task HandleUpdate_ImageWinsOverTextAndEntersInboxTop()
    {
        Assert.AreEqual(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
        using var directory = new TestDirectory();
        var bitmap = BitmapSource.Create(
            1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4);
        bitmap.Freeze();
        var reader = new QueueClipboardReader(
            new ClipboardSnapshot(10, bitmap, [], "ignored text"));
        var board = new BoardService();
        var store = new FakeBoardStore(directory.Root);
        var normalizer = new FakeImageNormalizer(directory.Root);
        var service = new ClipboardCaptureService(reader, normalizer, board, store, _ => { });

        await service.HandleClipboardUpdateAsync();

        var item = board.Items(BoardCategory.Inbox).Single();
        Assert.AreEqual(BoardItemKind.Image, item.Kind);
        Assert.AreEqual(1, store.SaveCount);
    }

    [TestMethod]
    public async Task HandleUpdate_ImageFileWinsOverText()
    {
        using var directory = new TestDirectory();
        var source = Path.Combine(directory.Root, "source.jpg");
        await File.WriteAllBytesAsync(source, [0x00]);
        var board = new BoardService();
        var service = new ClipboardCaptureService(
            new QueueClipboardReader(new ClipboardSnapshot(11, null, [source], "ignored text")),
            new FakeImageNormalizer(directory.Root),
            board,
            new FakeBoardStore(directory.Root),
            _ => { });

        await service.HandleClipboardUpdateAsync();

        Assert.AreEqual(BoardItemKind.Image, board.Items(BoardCategory.Inbox).Single().Kind);
    }

    [TestMethod]
    public async Task HandleUpdate_FileListKeepsOnlyExistingSupportedImages()
    {
        using var directory = new TestDirectory();
        var image = Path.Combine(directory.Root, "source.WEBP");
        var document = Path.Combine(directory.Root, "notes.txt");
        var missingImage = Path.Combine(directory.Root, "missing.png");
        await File.WriteAllBytesAsync(image, [0x00]);
        await File.WriteAllTextAsync(document, "notes");
        var board = new BoardService();
        var service = new ClipboardCaptureService(
            new QueueClipboardReader(new ClipboardSnapshot(
                11_001,
                null,
                [document, missingImage, image],
                "ignored text")),
            new FakeImageNormalizer(directory.Root),
            board,
            new FakeBoardStore(directory.Root),
            _ => { });

        await service.HandleClipboardUpdateAsync();

        Assert.HasCount(1, board.Items(BoardCategory.Inbox));
        Assert.AreEqual(BoardItemKind.Image, board.Items(BoardCategory.Inbox).Single().Kind);
    }

    [TestMethod]
    public async Task HandleUpdate_TextEntersSelectedCategoryTop()
    {
        using var directory = new TestDirectory();
        var state = new DefaultCaptureCategoryState();
        state.Set(BoardCategory.Prompt);
        var board = new BoardService();
        board.AddText("older prompt", BoardCategory.Prompt);
        var service = new ClipboardCaptureService(
            new QueueClipboardReader(new ClipboardSnapshot(12, null, [], "new prompt")),
            new FakeImageNormalizer(directory.Root),
            board,
            new FakeBoardStore(directory.Root),
            _ => { },
            defaultCaptureCategory: state);

        await service.HandleClipboardUpdateAsync();

        CollectionAssert.AreEqual(
            new[] { "new prompt", "older prompt" },
            board.Items(BoardCategory.Prompt).Select(item => item.Text).ToArray());
        Assert.IsTrue(board.Items(BoardCategory.Inbox).Count == 0);
    }

    [STATestMethod]
    public async Task HandleUpdate_ClipboardImageEntersSelectedCategory()
    {
        using var directory = new TestDirectory();
        var bitmap = BitmapSource.Create(
            1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4);
        bitmap.Freeze();
        var state = new DefaultCaptureCategoryState();
        state.Set(BoardCategory.CustomerOriginal);
        var board = new BoardService();
        var service = new ClipboardCaptureService(
            new QueueClipboardReader(new ClipboardSnapshot(13, bitmap, [], null)),
            new FakeImageNormalizer(directory.Root),
            board,
            new FakeBoardStore(directory.Root),
            _ => { },
            defaultCaptureCategory: state);

        await service.HandleClipboardUpdateAsync();

        Assert.AreEqual(BoardItemKind.Image, board.Items(BoardCategory.CustomerOriginal).Single().Kind);
        Assert.IsTrue(board.Items(BoardCategory.Inbox).Count == 0);
    }

    [TestMethod]
    public async Task HandleUpdate_ImageFileEntersSelectedCategory()
    {
        using var directory = new TestDirectory();
        var source = Path.Combine(directory.Root, "reference.jpg");
        await File.WriteAllBytesAsync(source, [0x00]);
        var state = new DefaultCaptureCategoryState();
        state.Set(BoardCategory.Reference);
        var board = new BoardService();
        var service = new ClipboardCaptureService(
            new QueueClipboardReader(new ClipboardSnapshot(14, null, [source], null)),
            new FakeImageNormalizer(directory.Root),
            board,
            new FakeBoardStore(directory.Root),
            _ => { },
            defaultCaptureCategory: state);

        await service.HandleClipboardUpdateAsync();

        Assert.AreEqual(BoardItemKind.Image, board.Items(BoardCategory.Reference).Single().Kind);
        Assert.IsTrue(board.Items(BoardCategory.Inbox).Count == 0);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task HandleUpdate_FreezesCategoryAndProcessingOrderBeforeClipboardReadCompletes()
    {
        using var directory = new TestDirectory();
        var reader = new BlockingFirstClipboardReader();
        var state = new DefaultCaptureCategoryState();
        state.Set(BoardCategory.CustomerOriginal);
        var board = new BoardService();
        var service = new ClipboardCaptureService(
            reader,
            new FakeImageNormalizer(directory.Root),
            board,
            new FakeBoardStore(directory.Root),
            _ => { },
            defaultCaptureCategory: state);

        var first = service.HandleClipboardUpdateAsync();
        await reader.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        state.Set(BoardCategory.Reference);
        var second = service.HandleClipboardUpdateAsync();
        await reader.SecondReadFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsFalse(second.IsCompleted, "The second notification must retain its entry order while the first read is blocked.");
        reader.ReleaseFirstRead();
        await Task.WhenAll(first, second);

        Assert.AreEqual("first", board.Items(BoardCategory.CustomerOriginal).Single().Text);
        Assert.AreEqual("second", board.Items(BoardCategory.Reference).Single().Text);
        Assert.IsTrue(board.Items(BoardCategory.Inbox).Count == 0);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task HandleUpdate_SameSequenceIsIgnoredButNewSequenceWithSameTextIsCaptured()
    {
        using var directory = new TestDirectory();
        var reader = new QueueClipboardReader(
            new ClipboardSnapshot(20, null, [], "same"),
            new ClipboardSnapshot(20, null, [], "same"),
            new ClipboardSnapshot(21, null, [], "same"));
        var board = new BoardService();
        var store = new FakeBoardStore(directory.Root);
        var service = new ClipboardCaptureService(
            reader,
            new FakeImageNormalizer(directory.Root),
            board,
            store,
            _ => { });

        await service.HandleClipboardUpdateAsync();
        await service.HandleClipboardUpdateAsync();
        await service.HandleClipboardUpdateAsync();

        Assert.AreEqual(2, board.Items(BoardCategory.Inbox).Count);
        Assert.AreEqual(2, store.SaveCount);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task HandleUpdate_ConcurrentNotificationsForOneSequenceCreateOneBoardItem()
    {
        using var directory = new TestDirectory();
        var reader = new BlockingSameSequenceClipboardReader(
            new ClipboardSnapshot(21_001, null, [], "same sequence"));
        var board = new BoardService();
        var store = new FakeBoardStore(directory.Root);
        var service = new ClipboardCaptureService(
            reader,
            new FakeImageNormalizer(directory.Root),
            board,
            store,
            _ => { });

        var first = service.HandleClipboardUpdateAsync();
        await reader.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = service.HandleClipboardUpdateAsync();
        reader.ReleaseFirstRead();
        await Task.WhenAll(first, second);

        Assert.AreEqual(1, board.Items(BoardCategory.Inbox).Count);
        Assert.AreEqual(1, store.SaveCount);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task HandleUpdate_ClipboardBusyRetriesAndThenSucceeds()
    {
        using var directory = new TestDirectory();
        var reader = new BusyThenReadableClipboardReader(
            failures: 2,
            new ClipboardSnapshot(30, null, [], "after retry"));
        var board = new BoardService();
        var service = new ClipboardCaptureService(
            reader,
            new FakeImageNormalizer(directory.Root),
            board,
            new FakeBoardStore(directory.Root),
            _ => { },
            retryDelays: [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);

        await service.HandleClipboardUpdateAsync();

        Assert.AreEqual(3, reader.ReadCount);
        Assert.AreEqual("after retry", board.Items(BoardCategory.Inbox).Single().Text);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task HandleUpdate_ClipboardRemainsBusyReportsStatusWithoutChangingBoard()
    {
        using var directory = new TestDirectory();
        var messages = new List<string>();
        var reader = new AlwaysBusyClipboardReader();
        var board = new BoardService();
        var service = new ClipboardCaptureService(
            reader,
            new FakeImageNormalizer(directory.Root),
            board,
            new FakeBoardStore(directory.Root),
            messages.Add,
            retryDelays: [TimeSpan.Zero, TimeSpan.Zero]);

        await service.HandleClipboardUpdateAsync();

        Assert.AreEqual(3, reader.ReadCount);
        Assert.AreEqual(0, board.Items(BoardCategory.Inbox).Count);
        Assert.AreEqual("本次剪贴板内容暂时无法读取，请重新复制。", messages.Single());
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task HandleUpdate_SaveFailureRollsBackItemAndReportsStatus()
    {
        using var directory = new TestDirectory();
        var messages = new List<string>();
        var board = new BoardService();
        var store = new FakeBoardStore(directory.Root) { FailSaves = true };
        var service = new ClipboardCaptureService(
            new QueueClipboardReader(new ClipboardSnapshot(40, null, [], "will fail")),
            new FakeImageNormalizer(directory.Root),
            board,
            store,
            messages.Add);

        await service.HandleClipboardUpdateAsync();

        Assert.AreEqual(0, board.Items(BoardCategory.Inbox).Count);
        Assert.AreEqual("本次内容未保存，请重新复制。", messages.Single());
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task HandleUpdate_ImageSaveFailureRollsBackItemAndManagedFile()
    {
        using var directory = new TestDirectory();
        var source = Path.Combine(directory.Root, "source.png");
        await File.WriteAllBytesAsync(source, [0x00]);
        var messages = new List<string>();
        var board = new BoardService();
        var store = new FakeBoardStore(directory.Root) { FailSaves = true };
        var service = new ClipboardCaptureService(
            new QueueClipboardReader(new ClipboardSnapshot(41, null, [source], null)),
            new FakeImageNormalizer(directory.Root),
            board,
            store,
            messages.Add);

        await service.HandleClipboardUpdateAsync();

        Assert.AreEqual(0, board.Items(BoardCategory.Inbox).Count);
        Assert.IsFalse(Directory.Exists(store.ImagesDirectory) &&
                       Directory.EnumerateFiles(store.ImagesDirectory).Any());
        Assert.AreEqual("本次图片未保存，请重新复制。", messages.Single());
    }

    [TestMethod]
    public async Task HandleUpdate_NonImageFilesAndBlankTextAreIgnored()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var service = new ClipboardCaptureService(
            new QueueClipboardReader(new ClipboardSnapshot(50, null, ["C:/temp/file.pdf"], "   ")),
            new FakeImageNormalizer(directory.Root),
            board,
            new FakeBoardStore(directory.Root),
            _ => { });

        await service.HandleClipboardUpdateAsync();

        Assert.AreEqual(0, board.Items(BoardCategory.Inbox).Count);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task HandleUpdate_NonImageFileDropDoesNotFallBackToPathText()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var service = new ClipboardCaptureService(
            new QueueClipboardReader(new ClipboardSnapshot(
                51,
                null,
                ["C:/temp/file.pdf"],
                "C:/temp/file.pdf")),
            new FakeImageNormalizer(directory.Root),
            board,
            new FakeBoardStore(directory.Root),
            _ => { });

        await service.HandleClipboardUpdateAsync();

        Assert.AreEqual(0, board.Items(BoardCategory.Inbox).Count);
    }

    [TestMethod]
    public async Task HandleUpdate_ReadsLaterSnapshotsBeforeEarlierProcessingCompletes()
    {
        using var directory = new TestDirectory();
        var reader = new QueueClipboardReader(
            new ClipboardSnapshot(60, null, [], "first"),
            new ClipboardSnapshot(61, null, [], "second"),
            new ClipboardSnapshot(62, null, [], "third"));
        var board = new BoardService();
        var store = new BlockingFirstSaveStore(directory.Root);
        var service = new ClipboardCaptureService(
            reader,
            new FakeImageNormalizer(directory.Root),
            board,
            store,
            _ => { });

        var first = service.HandleClipboardUpdateAsync();
        await store.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = service.HandleClipboardUpdateAsync();
        var third = service.HandleClipboardUpdateAsync();

        try
        {
            await Task.Delay(50);
            Assert.AreEqual(3, reader.ReadCount);
        }
        finally
        {
            store.ReleaseFirstSave();
            await Task.WhenAll(first, second, third);
        }

        CollectionAssert.AreEqual(
            new[] { "third", "second", "first" },
            board.Items(BoardCategory.Inbox).Select(item => item.Text).ToArray());
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task HandleUpdate_SharedGateWithDeleteKeepsPersistedSnapshotMatchingRestoredBoard()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var existing = board.AddText("existing");
        var store = new FailingFirstSaveBoardStore(directory.Root);
        var operationGate = new BoardOperationGate();
        var mutations = new BoardMutationService(board, store, _ => { }, operationGate);
        var clipboard = new ClipboardCaptureService(
            new QueueClipboardReader(new ClipboardSnapshot(70, null, [], "captured")),
            new FakeImageNormalizer(directory.Root),
            board,
            store,
            _ => { },
            operationGate: operationGate);

        var delete = mutations.DeleteAsync(existing.Id);
        await store.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var capture = clipboard.HandleClipboardUpdateAsync();
        store.FailFirstSave();

        Assert.IsFalse(await delete);
        await capture;
        CollectionAssert.AreEqual(
            board.CreateSnapshot().Items.Select(item => item.Id).ToArray(),
            store.LastPersistedSnapshot!.Items.Select(item => item.Id).ToArray());
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task HandleUpdate_ImageCanceledWhileWaitingForGateDeletesNormalizedFileWithoutChangingBoard()
    {
        using var directory = new TestDirectory();
        var source = Path.Combine(directory.Root, "source.png");
        await File.WriteAllBytesAsync(source, [0x00]);
        var operationGate = new BoardOperationGate();
        var holderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHolder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = operationGate.RunAsync(async () =>
        {
            holderStarted.TrySetResult();
            await releaseHolder.Task;
            return true;
        });
        await holderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var board = new BoardService();
        var store = new FakeBoardStore(directory.Root);
        var clipboard = new ClipboardCaptureService(
            new QueueClipboardReader(new ClipboardSnapshot(71, null, [source], null)),
            new FakeImageNormalizer(directory.Root),
            board,
            store,
            _ => { },
            operationGate: operationGate);
        using var cancellation = new CancellationTokenSource();

        var capture = clipboard.HandleClipboardUpdateAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => capture);
        Assert.AreEqual(0, board.Items(BoardCategory.Inbox).Count);
        Assert.IsFalse(Directory.EnumerateFiles(store.ImagesDirectory).Any());

        releaseHolder.TrySetResult();
        await holder;
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task HandleUpdate_ImageRejectedBySealedGateDeletesNormalizedFileWithoutChangingBoard()
    {
        using var directory = new TestDirectory();
        var source = Path.Combine(directory.Root, "source.png");
        await File.WriteAllBytesAsync(source, [0x00]);
        var operationGate = new BoardOperationGate();
        await operationGate.SealAndRunAsync(() => Task.CompletedTask);
        var board = new BoardService();
        var store = new FakeBoardStore(directory.Root);
        var messages = new List<string>();
        var clipboard = new ClipboardCaptureService(
            new QueueClipboardReader(new ClipboardSnapshot(72, null, [source], null)),
            new FakeImageNormalizer(directory.Root),
            board,
            store,
            messages.Add,
            operationGate: operationGate);

        await clipboard.HandleClipboardUpdateAsync();

        Assert.AreEqual(0, board.Items(BoardCategory.Inbox).Count);
        Assert.IsFalse(Directory.EnumerateFiles(store.ImagesDirectory).Any());
        Assert.AreEqual("本次图片未保存，请重新复制。", messages.Single());
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task HandleUpdate_ImageCommitSharedGateWithDeleteKeepsPersistedSnapshotMatchingRestoredBoard()
    {
        using var directory = new TestDirectory();
        var source = Path.Combine(directory.Root, "source.png");
        await File.WriteAllBytesAsync(source, [0x00]);
        var board = new BoardService();
        var existing = board.AddText("existing");
        var store = new FailingFirstSaveBoardStore(directory.Root);
        var operationGate = new BoardOperationGate();
        var mutations = new BoardMutationService(board, store, _ => { }, operationGate);
        var clipboard = new ClipboardCaptureService(
            new QueueClipboardReader(new ClipboardSnapshot(72, null, [source], null)),
            new FakeImageNormalizer(directory.Root),
            board,
            store,
            _ => { },
            operationGate: operationGate);

        var delete = mutations.DeleteAsync(existing.Id);
        await store.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var capture = clipboard.HandleClipboardUpdateAsync();
        store.FailFirstSave();

        Assert.IsFalse(await delete);
        await capture;
        CollectionAssert.AreEqual(
            board.CreateSnapshot().Items.Select(item => item.Id).ToArray(),
            store.LastPersistedSnapshot!.Items.Select(item => item.Id).ToArray());
        Assert.AreEqual(1, board.CreateSnapshot().Items.Count(item => item.Kind == BoardItemKind.Image));
    }

    private sealed class QueueClipboardReader(params ClipboardSnapshot[] snapshots) : IClipboardReader
    {
        private readonly Queue<ClipboardSnapshot> _snapshots = new(snapshots);

        public int ReadCount { get; private set; }

        public Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult(_snapshots.Dequeue());
        }
    }

    private sealed class BlockingFirstClipboardReader : IClipboardReader
    {
        private readonly TaskCompletionSource _releaseFirstRead = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;

        public TaskCompletionSource FirstReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondReadFinished { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ClipboardSnapshot> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _readCount);
            if (call == 1)
            {
                FirstReadStarted.TrySetResult();
                await _releaseFirstRead.Task.WaitAsync(cancellationToken);
                return new ClipboardSnapshot(15, null, [], "first");
            }

            SecondReadFinished.TrySetResult();
            return new ClipboardSnapshot(16, null, [], "second");
        }

        public void ReleaseFirstRead() => _releaseFirstRead.TrySetResult();
    }

    private sealed class BlockingSameSequenceClipboardReader(
        ClipboardSnapshot snapshot) : IClipboardReader
    {
        private readonly TaskCompletionSource _releaseFirstRead = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;

        public TaskCompletionSource FirstReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ClipboardSnapshot> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _readCount) == 1)
            {
                FirstReadStarted.TrySetResult();
                await _releaseFirstRead.Task.WaitAsync(cancellationToken);
            }

            return snapshot;
        }

        public void ReleaseFirstRead() => _releaseFirstRead.TrySetResult();
    }

    private sealed class BusyThenReadableClipboardReader(
        int failures,
        ClipboardSnapshot snapshot) : IClipboardReader
    {
        public int ReadCount { get; private set; }

        public Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        {
            ReadCount++;
            if (ReadCount <= failures)
            {
                throw new ExternalException("Clipboard is busy.");
            }

            return Task.FromResult(snapshot);
        }
    }

    private sealed class AlwaysBusyClipboardReader : IClipboardReader
    {
        public int ReadCount { get; private set; }

        public Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        {
            ReadCount++;
            throw new ExternalException("Clipboard is busy.");
        }
    }

    private sealed class FakeImageNormalizer(string root) : IImageNormalizer
    {
        public Task<StoredImage> NormalizeFileAsync(
            string sourcePath,
            Guid? id = null,
            CancellationToken cancellationToken = default) => Create(id);

        public Task<StoredImage> NormalizeStaticFileAsync(
            string sourcePath,
            Guid? id = null,
            CancellationToken cancellationToken = default) => Create(id);

        public Task<StoredImage> NormalizeBitmapAsync(
            BitmapSource bitmap,
            Guid? id = null,
            CancellationToken cancellationToken = default) => Create(id);

        public Task<StoredImage> NormalizeClipboardAsync(
            IReadOnlyList<ClipboardImageCandidate> candidates,
            Guid? id = null,
            CancellationToken cancellationToken = default) => Create(id);

        private Task<StoredImage> Create(Guid? requestedId)
        {
            var id = requestedId ?? Guid.NewGuid();
            var images = Path.Combine(root, "images");
            Directory.CreateDirectory(images);
            var absolute = Path.Combine(images, $"{id:N}.png");
            File.WriteAllBytes(absolute, [0x89, 0x50, 0x4E, 0x47]);
            return Task.FromResult(new StoredImage(id, $"images/{id:N}.png", absolute));
        }
    }

    private sealed class FakeBoardStore(string root) : IBoardStore
    {
        public bool FailSaves { get; set; }
        public int SaveCount { get; private set; }
        public string ImagesDirectory { get; } = Path.Combine(root, "images");

        public Task<BoardSnapshot> LoadBoardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BoardSnapshot());

        public Task SaveBoardAsync(BoardSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            if (FailSaves)
            {
                throw new IOException("Injected save failure.");
            }

            SaveCount++;
            return Task.CompletedTask;
        }

        public Task<WindowSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(WindowSettings.Default);

        public Task SaveSettingsAsync(WindowSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public bool TryDeleteImage(string? absolutePath)
        {
            if (!string.IsNullOrWhiteSpace(absolutePath) && File.Exists(absolutePath))
            {
                File.Delete(absolutePath);
            }

            return true;
        }
    }

    private sealed class BlockingFirstSaveStore(string root) : IBoardStore
    {
        private readonly TaskCompletionSource _releaseFirstSave = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _saveCount;

        public TaskCompletionSource FirstSaveStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public string ImagesDirectory { get; } = Path.Combine(root, "images");

        public Task<BoardSnapshot> LoadBoardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BoardSnapshot());

        public async Task SaveBoardAsync(
            BoardSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _saveCount) != 1)
            {
                return;
            }

            FirstSaveStarted.TrySetResult();
            await _releaseFirstSave.Task.WaitAsync(cancellationToken);
        }

        public Task<WindowSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(WindowSettings.Default);

        public Task SaveSettingsAsync(
            WindowSettings settings,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public bool TryDeleteImage(string? absolutePath) => true;

        public void ReleaseFirstSave() => _releaseFirstSave.TrySetResult();
    }

}
