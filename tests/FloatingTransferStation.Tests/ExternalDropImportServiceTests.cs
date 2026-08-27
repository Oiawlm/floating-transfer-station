using System.Windows;
using System.Windows.Media.Imaging;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FloatingTransferStation.Tests;

[TestClass]
[TestCategory("Adversarial")]
public sealed class ExternalDropImportServiceTests
{
    [TestMethod]
    public async Task ImportText_UsesExplicitCategoryWithoutChangingClipboardDefault()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var existing = board.AddText("existing", BoardCategory.Prompt);
        var store = new RecordingBoardStore(directory.Root);
        var messages = new List<string>();
        var defaultCategory = new DefaultCaptureCategoryState();
        defaultCategory.Set(BoardCategory.Reference);
        var service = CreateService(directory.Root, board, store, messages);

        var imported = await service.ImportAsync(
            new ExternalDropPayload.Text("new prompt"),
            BoardCategory.Prompt);

        Assert.IsTrue(imported);
        Assert.AreEqual(BoardCategory.Reference, defaultCategory.Current);
        CollectionAssert.AreEqual(
            new[] { "new prompt", existing.Text },
            board.Items(BoardCategory.Prompt).Select(item => item.Text).ToArray());
        Assert.AreEqual(1, store.SaveCount);
        Assert.IsEmpty(messages);
    }

    [TestMethod]
    public async Task ImportImageFiles_NormalizesAndCommitsSourceOrderInOneSave()
    {
        using var directory = new TestDirectory();
        var paths = CreateSourceFiles(directory.Root, "A.png", "B.jpg", "C.webp");
        var board = new BoardService();
        var existing = board.AddText("existing", BoardCategory.CustomerOriginal);
        var store = new RecordingBoardStore(directory.Root);
        var normalizer = new RecordingImageNormalizer(directory.Root);
        var messages = new List<string>();
        var service = CreateService(board, store, messages, normalizer);

        var imported = await service.ImportAsync(
            new ExternalDropPayload.ImageFiles(paths),
            BoardCategory.CustomerOriginal);

        Assert.IsTrue(imported);
        CollectionAssert.AreEqual(paths, normalizer.FileCalls);
        CollectionAssert.AreEqual(
            normalizer.Created.Select(image => image.Id).Append(existing.Id).ToArray(),
            board.Items(BoardCategory.CustomerOriginal).Select(item => item.Id).ToArray());
        Assert.AreEqual(1, store.SaveCount);
        CollectionAssert.AreEqual(
            board.CreateSnapshot().Items
                .Select(item => (item.Id, item.Category, item.Order))
                .ToArray(),
            store.LastPersistedSnapshot!.Items
                .Select(item => (item.Id, item.Category, item.Order))
                .ToArray());
        Assert.IsEmpty(messages);
    }

    [TestMethod]
    public async Task ImportImageCandidates_UsesClipboardNormalizerExactlyOnce()
    {
        using var directory = new TestDirectory();
        var candidate = ClipboardImageCandidate.FromEncoded("PNG", [0x89, 0x50, 0x4E, 0x47]);
        var board = new BoardService();
        var store = new RecordingBoardStore(directory.Root);
        var normalizer = new RecordingImageNormalizer(directory.Root);
        var service = CreateService(board, store, [], normalizer);

        var imported = await service.ImportAsync(
            new ExternalDropPayload.ImageCandidates([candidate]),
            BoardCategory.Reference);

        Assert.IsTrue(imported);
        Assert.AreEqual(1, normalizer.ClipboardCallCount);
        Assert.AreEqual(0, normalizer.BitmapCallCount);
        Assert.IsEmpty(normalizer.FileCalls);
        Assert.AreEqual(normalizer.Created.Single().Id, board.Items(BoardCategory.Reference).Single().Id);
        Assert.AreEqual(1, store.SaveCount);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task ImportImageFiles_NormalizationFailureCleansEarlierFilesWithoutBoardCommit()
    {
        using var directory = new TestDirectory();
        var paths = CreateSourceFiles(directory.Root, "A.png", "B.png", "C.png");
        var board = new BoardService();
        var existing = board.AddText("existing", BoardCategory.Reference);
        var store = new RecordingBoardStore(directory.Root);
        var normalizer = new RecordingImageNormalizer(directory.Root) { FailFileCall = 2 };
        var messages = new List<string>();
        var service = CreateService(board, store, messages, normalizer);

        var imported = await service.ImportAsync(
            new ExternalDropPayload.ImageFiles(paths),
            BoardCategory.Reference);

        Assert.IsFalse(imported);
        Assert.AreSame(existing, board.Items(BoardCategory.Reference).Single());
        Assert.AreEqual(0, store.SaveCount);
        AssertNoManagedImages(store.ImagesDirectory);
        CollectionAssert.AreEqual(new[] { "拖入内容未保存，请重试。" }, messages);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task ImportImageFiles_RejectsSourceThatBecomesAnimatedAfterPayloadSnapshot()
    {
        using var directory = new TestDirectory();
        var source = Path.Combine(directory.Root, "changed.gif");
        WriteGif(source, frameCount: 1);
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { source });
        var payload = new ExternalDropPayloadReader().Read(data);
        Assert.IsInstanceOfType<ExternalDropPayload.ImageFiles>(payload);
        WriteGif(source, frameCount: 2);
        var board = new BoardService();
        var store = new RecordingBoardStore(directory.Root);
        var messages = new List<string>();
        var service = CreateService(
            board,
            store,
            messages,
            new ImageNormalizer(store.ImagesDirectory));

        var imported = await service.ImportAsync(payload, BoardCategory.Reference);

        Assert.IsFalse(imported);
        Assert.AreEqual(0, board.Items(BoardCategory.Reference).Count);
        Assert.AreEqual(0, store.SaveCount);
        AssertNoManagedImages(store.ImagesDirectory);
        CollectionAssert.AreEqual(new[] { "拖入内容未保存，请重试。" }, messages);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task ImportImageFiles_SaveFailureRollsBackExactBoardAndDeletesWholeBatch()
    {
        using var directory = new TestDirectory();
        var paths = CreateSourceFiles(directory.Root, "A.png", "B.png", "C.png");
        var board = new BoardService();
        board.AddText("older", BoardCategory.Prompt);
        board.AddText("newer", BoardCategory.Prompt);
        var originalIds = board.Items(BoardCategory.Prompt).Select(item => item.Id).ToArray();
        var store = new RecordingBoardStore(directory.Root) { SaveFailure = new IOException("save failed") };
        var normalizer = new RecordingImageNormalizer(directory.Root);
        var messages = new List<string>();
        var service = CreateService(board, store, messages, normalizer);

        var imported = await service.ImportAsync(
            new ExternalDropPayload.ImageFiles(paths),
            BoardCategory.Prompt);

        Assert.IsFalse(imported);
        CollectionAssert.AreEqual(
            originalIds,
            board.Items(BoardCategory.Prompt).Select(item => item.Id).ToArray());
        Assert.AreEqual(1, store.SaveCount);
        AssertNoManagedImages(store.ImagesDirectory);
        CollectionAssert.AreEqual(
            normalizer.Created.Select(image => image.AbsolutePath).ToArray(),
            store.DeletedPaths.ToArray());
        CollectionAssert.AreEqual(new[] { "拖入内容未保存，请重试。" }, messages);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Adversarial")]
    public async Task ImportImageFiles_CleanupFailureTriesEveryPathAndReportsCombinedStatus(
        bool throwOnFirstDelete)
    {
        using var directory = new TestDirectory();
        var paths = CreateSourceFiles(directory.Root, "A.png", "B.png");
        var board = new BoardService();
        var store = new RecordingBoardStore(directory.Root)
        {
            SaveFailure = new IOException("save failed"),
            FailedDeleteCall = 1,
            ThrowDeleteFailure = throwOnFirstDelete
        };
        var normalizer = new RecordingImageNormalizer(directory.Root);
        var messages = new List<string>();
        var service = CreateService(board, store, messages, normalizer);

        var imported = await service.ImportAsync(
            new ExternalDropPayload.ImageFiles(paths),
            BoardCategory.Prompt);

        Assert.IsFalse(imported);
        Assert.AreEqual(0, board.Items(BoardCategory.Prompt).Count);
        CollectionAssert.AreEqual(
            normalizer.Created.Select(image => image.AbsolutePath).ToArray(),
            store.DeletedPaths.ToArray());
        CollectionAssert.AreEqual(
            new[] { "拖入内容未保存，且部分图片副本暂时无法删除。" },
            messages);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task ImportImageFiles_CanceledWhileWaitingForGateCleansFilesAndRethrows()
    {
        using var directory = new TestDirectory();
        var gate = new BoardOperationGate();
        var holderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHolder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = gate.RunAsync(async () =>
        {
            holderStarted.TrySetResult();
            await releaseHolder.Task;
            return true;
        });
        await holderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var paths = CreateSourceFiles(directory.Root, "A.png", "B.png");
        var board = new BoardService();
        var store = new RecordingBoardStore(directory.Root);
        var normalizer = new RecordingImageNormalizer(directory.Root, signalAfterFileCalls: paths.Length);
        var messages = new List<string>();
        var service = CreateService(board, store, messages, normalizer, gate);
        using var cancellation = new CancellationTokenSource();

        try
        {
            var importing = service.ImportAsync(
                new ExternalDropPayload.ImageFiles(paths),
                BoardCategory.Inbox,
                cancellation.Token);
            await normalizer.FileCheckpoint.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => importing);
            Assert.AreEqual(0, board.Items(BoardCategory.Inbox).Count);
            Assert.AreEqual(0, store.SaveCount);
            AssertNoManagedImages(store.ImagesDirectory);
            Assert.IsEmpty(messages);
        }
        finally
        {
            releaseHolder.TrySetResult();
            await holder;
        }
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task ImportImageFiles_CanceledCleanupFailureTriesEveryPathAndRethrowsWithoutStatus()
    {
        using var directory = new TestDirectory();
        var gate = new BoardOperationGate();
        var holderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHolder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = gate.RunAsync(async () =>
        {
            holderStarted.TrySetResult();
            await releaseHolder.Task;
            return true;
        });
        await holderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var paths = CreateSourceFiles(directory.Root, "A.png", "B.png");
        var board = new BoardService();
        var store = new RecordingBoardStore(directory.Root)
        {
            FailedDeleteCall = 1,
            ThrowDeleteFailure = true
        };
        var normalizer = new RecordingImageNormalizer(directory.Root, signalAfterFileCalls: paths.Length);
        var messages = new List<string>();
        var service = CreateService(board, store, messages, normalizer, gate);
        using var cancellation = new CancellationTokenSource();

        try
        {
            var importing = service.ImportAsync(
                new ExternalDropPayload.ImageFiles(paths),
                BoardCategory.Inbox,
                cancellation.Token);
            await normalizer.FileCheckpoint.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => importing);
            Assert.AreEqual(0, board.Items(BoardCategory.Inbox).Count);
            Assert.AreEqual(0, store.SaveCount);
            CollectionAssert.AreEqual(
                normalizer.Created.Select(image => image.AbsolutePath).ToArray(),
                store.DeletedPaths.ToArray());
            Assert.IsEmpty(messages);
        }
        finally
        {
            releaseHolder.TrySetResult();
            await holder;
        }
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task ImportImageFiles_SealedGateRejectsAndCleansWithoutThrowing()
    {
        using var directory = new TestDirectory();
        var gate = new BoardOperationGate();
        await gate.SealAndRunAsync(() => Task.CompletedTask);
        var paths = CreateSourceFiles(directory.Root, "A.png");
        var board = new BoardService();
        var store = new RecordingBoardStore(directory.Root);
        var normalizer = new RecordingImageNormalizer(directory.Root);
        var messages = new List<string>();
        var service = CreateService(board, store, messages, normalizer, gate);

        var imported = await service.ImportAsync(
            new ExternalDropPayload.ImageFiles(paths),
            BoardCategory.CustomerOriginal);

        Assert.IsFalse(imported);
        Assert.AreEqual(0, board.Items(BoardCategory.CustomerOriginal).Count);
        Assert.AreEqual(0, store.SaveCount);
        AssertNoManagedImages(store.ImagesDirectory);
        CollectionAssert.AreEqual(new[] { "拖入内容未保存，请重试。" }, messages);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task ImportImageFiles_SharedGateKeepsPersistedSnapshotEqualToRestoredBoard()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var existing = board.AddText("existing", BoardCategory.Inbox);
        var store = new FailingFirstSaveBoardStore(directory.Root);
        var gate = new BoardOperationGate();
        var messages = new List<string>();
        var mutations = new BoardMutationService(board, store, messages.Add, gate);
        var paths = CreateSourceFiles(directory.Root, "A.png");
        var normalizer = new RecordingImageNormalizer(directory.Root, signalAfterFileCalls: 1);
        var importer = CreateService(board, store, messages, normalizer, gate);

        var deleting = mutations.DeleteAsync(existing.Id);
        await store.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var importing = importer.ImportAsync(
            new ExternalDropPayload.ImageFiles(paths),
            BoardCategory.Reference);
        await normalizer.FileCheckpoint.Task.WaitAsync(TimeSpan.FromSeconds(5));

        store.FailFirstSave();

        Assert.IsFalse(await deleting);
        Assert.IsTrue(await importing);
        CollectionAssert.AreEqual(
            board.CreateSnapshot().Items
                .Select(item => (item.Id, item.Category, item.Order))
                .ToArray(),
            store.LastPersistedSnapshot!.Items
                .Select(item => (item.Id, item.Category, item.Order))
                .ToArray());
        Assert.AreSame(existing, board.Items(BoardCategory.Inbox).Single());
        Assert.AreEqual(normalizer.Created.Single().Id, board.Items(BoardCategory.Reference).Single().Id);
    }

    [TestMethod]
    public async Task Import_InvalidTargetCategoryFailsBeforeNormalization()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var store = new RecordingBoardStore(directory.Root);
        var normalizer = new RecordingImageNormalizer(directory.Root);
        var messages = new List<string>();
        var service = CreateService(board, store, messages, normalizer);

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => service.ImportAsync(
            new ExternalDropPayload.ImageFiles(["unused.png"]),
            (BoardCategory)99));

        Assert.IsEmpty(normalizer.FileCalls);
        Assert.AreEqual(0, store.SaveCount);
        Assert.IsEmpty(messages);
    }

    private static ExternalDropImportService CreateService(
        string root,
        BoardService board,
        IBoardStore store,
        List<string> messages) =>
        CreateService(board, store, messages, new RecordingImageNormalizer(root));

    private static ExternalDropImportService CreateService(
        BoardService board,
        IBoardStore store,
        List<string> messages,
        IImageNormalizer normalizer,
        BoardOperationGate? gate = null) =>
        new(normalizer, board, store, messages.Add, gate);

    private static string[] CreateSourceFiles(string root, params string[] names)
    {
        var sourceDirectory = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceDirectory);
        return names.Select(name =>
        {
            var path = Path.Combine(sourceDirectory, name);
            File.WriteAllBytes(path, [0x00]);
            return path;
        }).ToArray();
    }

    private static void WriteGif(string path, int frameCount)
    {
        using var image = new Image<Rgba32>(2, 2, SixLabors.ImageSharp.Color.Red);
        for (var frame = 1; frame < frameCount; frame++)
        {
            image.Frames.AddFrame(image.Frames.RootFrame);
        }

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        image.SaveAsGif(stream);
    }

    private static void AssertNoManagedImages(string imagesDirectory)
    {
        Assert.IsFalse(
            Directory.Exists(imagesDirectory) && Directory.EnumerateFiles(imagesDirectory).Any(),
            "A failed external import left a managed image file behind.");
    }

    private sealed class RecordingImageNormalizer(
        string root,
        int? signalAfterFileCalls = null) : IImageNormalizer
    {
        public int? FailFileCall { get; init; }
        public List<string> FileCalls { get; } = [];
        public int BitmapCallCount { get; private set; }
        public int ClipboardCallCount { get; private set; }
        public List<StoredImage> Created { get; } = [];
        public TaskCompletionSource FileCheckpoint { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<StoredImage> NormalizeFileAsync(
            string sourcePath,
            Guid? id = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileCalls.Add(sourcePath);
            if (FileCalls.Count == FailFileCall)
            {
                throw new IOException("Injected normalization failure.");
            }

            var stored = Create(id);
            if (FileCalls.Count == signalAfterFileCalls)
            {
                FileCheckpoint.TrySetResult();
            }

            return Task.FromResult(stored);
        }

        public Task<StoredImage> NormalizeStaticFileAsync(
            string sourcePath,
            Guid? id = null,
            CancellationToken cancellationToken = default) =>
            NormalizeFileAsync(sourcePath, id, cancellationToken);

        public Task<StoredImage> NormalizeBitmapAsync(
            BitmapSource bitmap,
            Guid? id = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BitmapCallCount++;
            return Task.FromResult(Create(id));
        }

        public Task<StoredImage> NormalizeClipboardAsync(
            IReadOnlyList<ClipboardImageCandidate> candidates,
            Guid? id = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClipboardCallCount++;
            return Task.FromResult(Create(id));
        }

        private StoredImage Create(Guid? requestedId)
        {
            var id = requestedId ?? Guid.NewGuid();
            var imagesDirectory = Path.Combine(root, "images");
            Directory.CreateDirectory(imagesDirectory);
            var absolutePath = Path.Combine(imagesDirectory, $"{id:N}.png");
            File.WriteAllBytes(absolutePath, [0x89, 0x50, 0x4E, 0x47]);
            var stored = new StoredImage(id, $"images/{id:N}.png", absolutePath);
            Created.Add(stored);
            return stored;
        }
    }

    private sealed class RecordingBoardStore(string root) : IBoardStore
    {
        public Exception? SaveFailure { get; init; }
        public int? FailedDeleteCall { get; init; }
        public bool ThrowDeleteFailure { get; init; }
        public int SaveCount { get; private set; }
        public BoardSnapshot? LastPersistedSnapshot { get; private set; }
        public List<string> DeletedPaths { get; } = [];
        public string ImagesDirectory { get; } = Path.Combine(root, "images");

        public Task<BoardSnapshot> LoadBoardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BoardSnapshot());

        public Task SaveBoardAsync(
            BoardSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            if (SaveFailure is not null)
            {
                throw SaveFailure;
            }

            LastPersistedSnapshot = snapshot;
            return Task.CompletedTask;
        }

        public Task<WindowSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(WindowSettings.Default);

        public Task SaveSettingsAsync(
            WindowSettings settings,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public bool TryDeleteImage(string? absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath))
            {
                return true;
            }

            DeletedPaths.Add(absolutePath);
            if (DeletedPaths.Count == FailedDeleteCall)
            {
                if (ThrowDeleteFailure)
                {
                    throw new IOException("Injected image cleanup failure.");
                }

                return false;
            }

            if (File.Exists(absolutePath))
            {
                File.Delete(absolutePath);
            }

            return true;
        }
    }
}
