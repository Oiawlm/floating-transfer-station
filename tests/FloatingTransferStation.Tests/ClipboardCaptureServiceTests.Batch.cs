using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FloatingTransferStation.Tests;

public sealed partial class ClipboardCaptureServiceTests
{
    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task HandleUpdate_FileBatchPreservesSourceOrderWithOneSave()
    {
        using var directory = new TestDirectory();
        var sources = await CreateBatchSourcesAsync(directory.Root);
        var board = new BoardService();
        var existing = board.AddText("existing");
        var store = new FakeBoardStore(directory.Root);
        var service = new ClipboardCaptureService(
            new QueueClipboardReader(new ClipboardSnapshot(800, null, sources, null)),
            new ImageNormalizer(store.ImagesDirectory), board, store, _ => { });

        await service.HandleClipboardUpdateAsync();

        var imported = board.Items(BoardCategory.Inbox).Where(item => item.Kind == BoardItemKind.Image);
        var colors = new List<byte>();
        foreach (var item in imported)
        {
            using var image = Image.Load<Rgba32>(item.ImageAbsolutePath!);
            colors.Add(image[0, 0].R);
        }

        CollectionAssert.AreEqual(new byte[] { 10, 20, 30 }, colors);
        Assert.AreSame(existing, board.Items(BoardCategory.Inbox).Last());
        Assert.AreEqual(1, store.SaveCount);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task HandleUpdate_FileBatchSkipsDamagedFileAndCommitsUsableFilesInOrder()
    {
        using var directory = new TestDirectory();
        var sources = await CreateBatchSourcesAsync(directory.Root);
        await File.WriteAllTextAsync(sources[1], "synthetic invalid image");
        var board = new BoardService();
        var store = new FakeBoardStore(directory.Root);
        var messages = new List<string>();
        var service = new ClipboardCaptureService(
            new QueueClipboardReader(new ClipboardSnapshot(801, null, sources, null)),
            new ImageNormalizer(store.ImagesDirectory), board, store, messages.Add);

        await service.HandleClipboardUpdateAsync();

        var colors = new List<byte>();
        foreach (var item in board.Items(BoardCategory.Inbox))
        {
            using var image = Image.Load<Rgba32>(item.ImageAbsolutePath!);
            colors.Add(image[0, 0].R);
        }

        CollectionAssert.AreEqual(new byte[] { 10, 30 }, colors);
        Assert.AreEqual(1, store.SaveCount);
        Assert.HasCount(1, messages);
        Assert.HasCount(2, Directory.GetFiles(store.ImagesDirectory));
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task HandleUpdate_FileBatchSaveFailureRestoresBoardAndRemovesAllStagedImages()
    {
        using var directory = new TestDirectory();
        var sources = await CreateBatchSourcesAsync(directory.Root);
        var board = new BoardService();
        var existing = board.AddText("keep");
        var store = new FakeBoardStore(directory.Root) { FailSaves = true };
        var messages = new List<string>();
        var service = new ClipboardCaptureService(
            new QueueClipboardReader(new ClipboardSnapshot(802, null, sources, null)),
            new ImageNormalizer(store.ImagesDirectory), board, store, messages.Add);

        await service.HandleClipboardUpdateAsync();

        Assert.AreSame(existing, board.Items(BoardCategory.Inbox).Single());
        Assert.AreEqual(0, existing.Order);
        Assert.IsEmpty(Directory.GetFiles(store.ImagesDirectory));
        Assert.HasCount(1, messages);
    }

    private static async Task<string[]> CreateBatchSourcesAsync(string root)
    {
        var paths = Enumerable.Range(1, 3).Select(index => Path.Combine(root, $"source-{index}.png")).ToArray();
        for (var index = 0; index < paths.Length; index++)
        {
            await File.WriteAllBytesAsync(paths[index],
                await CreateEncodedImageAsync(2, 2, new Rgba32((byte)((index + 1) * 10), 50, 80, 255)));
        }

        return paths;
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task HandleUpdate_FileBatchCanceledDuringNormalizationRemovesEarlierStagedImages()
    {
        using var directory = new TestDirectory();
        var sources = await CreateBatchSourcesAsync(directory.Root);
        var board = new BoardService();
        var keep = board.AddText("keep");
        var store = new FakeBoardStore(directory.Root);
        var service = new ClipboardCaptureService(
            new QueueClipboardReader(new ClipboardSnapshot(803, null, sources, null)),
            new CancelSecondFileNormalizer(directory.Root), board, store, _ => { });

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.HandleClipboardUpdateAsync());

        Assert.AreSame(keep, board.Items(BoardCategory.Inbox).Single());
        Assert.IsEmpty(Directory.GetFiles(store.ImagesDirectory));
        Assert.AreEqual(0, store.SaveCount);
    }

    private sealed class CancelSecondFileNormalizer(string root) : IImageNormalizer
    {
        private readonly FakeImageNormalizer _inner = new(root);
        private int _calls;
        public Task<StoredImage> NormalizeFileAsync(string sourcePath, Guid? id = null, CancellationToken cancellationToken = default) =>
            ++_calls == 2 ? throw new OperationCanceledException() : _inner.NormalizeFileAsync(sourcePath, id, cancellationToken);
        public Task<StoredImage> NormalizeStaticFileAsync(string sourcePath, Guid? id = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<StoredImage> NormalizeBitmapAsync(System.Windows.Media.Imaging.BitmapSource bitmap, Guid? id = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<StoredImage> NormalizeClipboardAsync(IReadOnlyList<ClipboardImageCandidate> candidates, Guid? id = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
