using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

[TestClass]
[TestCategory("Adversarial")]
public sealed class AdversarialRegressionTests
{
    [TestMethod]
    public void LargeBoard_ReorderMaintainsContinuousOrderAndSingleMembership()
    {
        var snapshot = new BoardSnapshot
        {
            Items = Enumerable.Range(0, 5000)
                .Select(index => BoardItem.CreateText(
                    $"item-{index}",
                    Guid.NewGuid(),
                    DateTimeOffset.UnixEpoch.AddSeconds(index)))
                .ToList()
        };
        for (var index = 0; index < snapshot.Items.Count; index++)
        {
            snapshot.Items[index].Order = index;
        }
        var board = new BoardService();
        board.Restore(snapshot);
        var moved = board.Items(BoardCategory.Inbox)[2500];

        board.Move(moved.Id, BoardCategory.Reference, 0);

        Assert.AreEqual(4999, board.Items(BoardCategory.Inbox).Count);
        Assert.AreEqual(1, board.Items(BoardCategory.Reference).Count);
        CollectionAssert.AreEqual(
            Enumerable.Range(0, 4999).ToArray(),
            board.Items(BoardCategory.Inbox).Select(item => item.Order).ToArray());
        Assert.AreEqual(1, board.CreateSnapshot().Items.Count(item => item.Id == moved.Id));
    }

    [TestMethod]
    public async Task RapidClipboardNotifications_AreSerializedWithoutLosingUniqueSequences()
    {
        using var directory = new TestDirectory();
        var snapshots = Enumerable.Range(1, 100)
            .Select(index => new ClipboardSnapshot((uint)index, null, [], $"text-{index}"));
        var reader = new SerializedQueueReader(snapshots);
        var board = new BoardService();
        var store = new MemoryBoardStore(directory.Root);
        var service = new ClipboardCaptureService(
            reader,
            new NeverUsedImageNormalizer(),
            board,
            store,
            _ => { },
            retryDelays: [TimeSpan.Zero]);

        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => service.HandleClipboardUpdateAsync()));

        Assert.AreEqual(100, board.Items(BoardCategory.Inbox).Count);
        Assert.AreEqual(100, store.SaveCount);
        Assert.AreEqual(100, board.Items(BoardCategory.Inbox).Select(item => item.Text).Distinct().Count());
    }

    [TestMethod]
    public async Task LoadBoard_LeftoverTemporaryFileDoesNotReplaceLastCommittedBoard()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var board = new BoardSnapshot
        {
            Items = [BoardItem.CreateText("committed", Guid.NewGuid(), DateTimeOffset.UtcNow)]
        };
        await store.SaveBoardAsync(board);
        await File.WriteAllTextAsync(paths.BoardFile + ".tmp", "{ interrupted");

        var loaded = await store.LoadBoardAsync();

        Assert.AreEqual("committed", loaded.Items.Single().Text);
    }

    private sealed class SerializedQueueReader(IEnumerable<ClipboardSnapshot> snapshots) : IClipboardReader
    {
        private readonly Queue<ClipboardSnapshot> _queue = new(snapshots);

        public Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_queue.Dequeue());
    }

    private sealed class NeverUsedImageNormalizer : IImageNormalizer
    {
        public Task<StoredImage> NormalizeFileAsync(
            string sourcePath,
            Guid? id = null,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Image normalizer should not be called for text snapshots.");

        public Task<StoredImage> NormalizeStaticFileAsync(
            string sourcePath,
            Guid? id = null,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Image normalizer should not be called for text snapshots.");

        public Task<StoredImage> NormalizeBitmapAsync(
            System.Windows.Media.Imaging.BitmapSource bitmap,
            Guid? id = null,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Image normalizer should not be called for text snapshots.");

        public Task<StoredImage> NormalizeClipboardAsync(
            IReadOnlyList<ClipboardImageCandidate> candidates,
            Guid? id = null,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Image normalizer should not be called for text snapshots.");
    }

    private sealed class MemoryBoardStore(string root) : IBoardStore
    {
        public int SaveCount { get; private set; }
        public string ImagesDirectory { get; } = Path.Combine(root, "images");

        public Task<BoardSnapshot> LoadBoardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BoardSnapshot());

        public Task SaveBoardAsync(BoardSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task<WindowSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(WindowSettings.Default);

        public Task SaveSettingsAsync(WindowSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public bool TryDeleteImage(string? absolutePath) => true;
    }
}
