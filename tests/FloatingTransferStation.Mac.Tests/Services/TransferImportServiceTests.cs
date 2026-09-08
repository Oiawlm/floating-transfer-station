using FloatingTransferStation.Mac.Services;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SixLabors.ImageSharp;

namespace FloatingTransferStation.Mac.Tests.Services;

[TestClass]
public sealed class TransferImportServiceTests
{
    [TestMethod]
    public async Task Batch_SkipsCorruptFilePreservesOrderAndPinnedPartitionInOneSave()
    {
        using var directory = new ServiceTestDirectory();
        var first = Path.Combine(directory.Root, "first.png");
        var broken = Path.Combine(directory.Root, "broken.png");
        var last = Path.Combine(directory.Root, "last.png");
        await File.WriteAllBytesAsync(first, await MacImageNormalizerTests.PngAsync(2, 2, Color.Red));
        await File.WriteAllBytesAsync(broken, [1, 2, 3]);
        await File.WriteAllBytesAsync(last, await MacImageNormalizerTests.PngAsync(3, 3, Color.Blue));
        var board = new BoardService();
        var existing = board.AddText("existing", BoardCategory.Inbox);
        var pinned = board.AddText("pinned", BoardCategory.Inbox);
        board.SetPinnedMany([pinned.Id], true);
        var store = new TestStore(directory.Images);
        var messages = new List<string>();
        var service = new TransferImportService(new MacImageNormalizer(directory.Images), board, store, messages.Add, new BoardOperationGate());

        Assert.IsTrue(await service.ImportAsync(new TransferPayload.ImageFiles([first, broken, last]), BoardCategory.Inbox));

        var items = board.Items(BoardCategory.Inbox);
        Assert.AreSame(pinned, items[0]);
        Assert.AreSame(existing, items[3]);
        using var firstResult = await Image.LoadAsync(items[1].ImageAbsolutePath!);
        using var lastResult = await Image.LoadAsync(items[2].ImageAbsolutePath!);
        Assert.AreEqual(2, firstResult.Width);
        Assert.AreEqual(3, lastResult.Width);
        Assert.AreEqual(1, store.SaveCount);
        Assert.HasCount(1, messages);
        Assert.IsTrue(File.Exists(first) && File.Exists(broken) && File.Exists(last));
    }

    [TestMethod]
    public async Task NotificationFailureAfterCommit_DoesNotDeleteSavedImages()
    {
        using var directory = new ServiceTestDirectory();
        var source = Path.Combine(directory.Root, "valid.png");
        await File.WriteAllBytesAsync(source, await MacImageNormalizerTests.PngAsync(2, 2, Color.Red));
        var board = new BoardService();
        var store = new TestStore(directory.Images);
        var service = new TransferImportService(new MacImageNormalizer(directory.Images), board, store,
            _ => throw new InvalidOperationException("notification unavailable"), new BoardOperationGate());

        Assert.IsTrue(await service.ImportAsync(
            new TransferPayload.ImageFiles([source, Path.Combine(directory.Root, "missing.png")]), BoardCategory.Inbox));

        Assert.AreEqual(1, store.SaveCount);
        Assert.IsTrue(File.Exists(board.Items(BoardCategory.Inbox).Single().ImageAbsolutePath));
        Assert.IsEmpty(store.Deleted);
    }

    [TestMethod]
    public async Task SaveFailure_RestoresExactBoardAndDeletesWholeBatch()
    {
        using var directory = new ServiceTestDirectory();
        var board = new BoardService();
        var original = board.AddText("before", BoardCategory.Reference);
        var store = new TestStore(directory.Images) { FailSave = true };
        var bytes = await MacImageNormalizerTests.PngAsync(2, 2, Color.Red);
        var service = new TransferImportService(new MacImageNormalizer(directory.Images), board, store, _ => { }, new BoardOperationGate());

        var saved = await service.ImportAsync(new TransferPayload.ImageBatch([
            new TransferPayload.ImageCandidates([bytes]), new TransferPayload.ImageCandidates([bytes])]), BoardCategory.Reference);

        Assert.IsFalse(saved);
        Assert.AreSame(original, board.Items(BoardCategory.Reference).Single());
        Assert.AreEqual(0, original.Order);
        Assert.IsEmpty(Directory.GetFiles(directory.Images));
        Assert.AreEqual(2, store.Deleted.Count);
    }

    [TestMethod]
    public async Task NormalizationIoFailure_RollsBackPreparedImagesWithoutSaving()
    {
        using var directory = new ServiceTestDirectory();
        var board = new BoardService();
        var store = new TestStore(directory.Images);
        var normalizer = new FailingNormalizer(directory.Images);
        var service = new TransferImportService(normalizer, board, store, _ => { }, new BoardOperationGate());

        Assert.IsFalse(await service.ImportAsync(new TransferPayload.ImageFiles(["first.png", "second.png"]), BoardCategory.Inbox));

        Assert.IsEmpty(board.Items(BoardCategory.Inbox));
        Assert.AreEqual(0, store.SaveCount);
        Assert.IsEmpty(Directory.GetFiles(directory.Images));
    }

    [TestMethod]
    public async Task ShutdownGate_DrainsNormalizationAndCommitBeforeFinalSave()
    {
        using var directory = new ServiceTestDirectory();
        var board = new BoardService();
        var store = new TestStore(directory.Images);
        var gate = new BoardOperationGate();
        var normalizer = new PausedNormalizer(directory.Images);
        var service = new TransferImportService(normalizer, board, store, _ => { }, gate);
        var import = service.ImportAsync(new TransferPayload.ImageCandidates([new byte[] { 1 }]), BoardCategory.Inbox);
        await normalizer.Entered.Task;
        var finalSawItems = false;
        var shutdown = gate.SealAndRunAsync(() =>
        {
            finalSawItems = board.Items(BoardCategory.Inbox).Count == 1 && store.SaveCount == 1;
            return Task.CompletedTask;
        });
        Assert.IsFalse(shutdown.IsCompleted);

        normalizer.Release.SetResult();
        Assert.IsTrue(await import);
        await shutdown;
        Assert.IsTrue(finalSawItems);
    }

    [TestMethod]
    public async Task CancelledNormalization_CleansEarlierCopies()
    {
        using var directory = new ServiceTestDirectory();
        var board = new BoardService();
        var store = new TestStore(directory.Images);
        var normalizer = new FailingNormalizer(directory.Images) { CancelSecond = true };
        var service = new TransferImportService(normalizer, board, store, _ => { }, new BoardOperationGate());

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            service.ImportAsync(new TransferPayload.ImageFiles(["first.png", "second.png"]), BoardCategory.Inbox));

        Assert.IsEmpty(board.Items(BoardCategory.Inbox));
        Assert.IsEmpty(Directory.GetFiles(directory.Images));
        Assert.AreEqual(0, store.SaveCount);
    }

    private sealed class TestStore(string images) : IBoardStore
    {
        public string ImagesDirectory => images;
        public bool FailSave { get; init; }
        public int SaveCount { get; private set; }
        public List<string> Deleted { get; } = [];
        public Task<BoardSnapshot> LoadBoardAsync(CancellationToken cancellationToken = default) => Task.FromResult(new BoardSnapshot());
        public Task SaveBoardAsync(BoardSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return FailSave ? Task.FromException(new IOException("disk unavailable")) : Task.CompletedTask;
        }

        public Task<WindowSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(WindowSettings.Default);
        public Task SaveSettingsAsync(WindowSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool TryDeleteImage(string? path)
        {
            if (path is not null)
            {
                File.Delete(path);
                Deleted.Add(path);
            }

            return true;
        }
    }

    private sealed class FailingNormalizer(string images) : IMacImageNormalizer
    {
        private int _calls;
        public bool CancelSecond { get; init; }
        public async Task<StoredImage> NormalizeStaticFileAsync(string path, CancellationToken cancellationToken = default)
        {
            if (++_calls == 2)
            {
                if (CancelSecond)
                {
                    throw new OperationCanceledException();
                }

                throw new IOException("output disk failure");
            }

            return await CreateStoredAsync(images);
        }

        public Task<StoredImage> NormalizeCandidatesAsync(IReadOnlyList<ReadOnlyMemory<byte>> candidates,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class PausedNormalizer(string images) : IMacImageNormalizer
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<StoredImage> NormalizeStaticFileAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task<StoredImage> NormalizeCandidatesAsync(IReadOnlyList<ReadOnlyMemory<byte>> candidates,
            CancellationToken cancellationToken = default)
        {
            Entered.SetResult();
            await Release.Task;
            return await CreateStoredAsync(images);
        }
    }

    private static async Task<StoredImage> CreateStoredAsync(string images)
    {
        Directory.CreateDirectory(images);
        var id = Guid.NewGuid();
        var path = Path.Combine(images, $"{id:N}.png");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        return new StoredImage(id, $"images/{id:N}.png", path);
    }
}
