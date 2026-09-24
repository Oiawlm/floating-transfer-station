using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using SixLabors.ImageSharp.PixelFormats;

namespace FloatingTransferStation.Tests;

public sealed partial class ClipboardCaptureServiceTests
{
    private const string DuplicateSuppressedMessage = "与最近收集的内容相同，未重复收集。";

    [TestMethod]
    public async Task HandleUpdate_IdenticalBackToBackImagesWithDifferentSequences_CreateOneItem()
    {
        using var directory = new TestDirectory();
        var encoded = await CreateEncodedImageAsync(3, 3, new Rgba32(30, 90, 160));
        var board = new BoardService();
        var store = new FakeBoardStore(directory.Root);
        var clock = new MutableCaptureClock();
        var messages = new List<string>();
        var service = new ClipboardCaptureService(
            new QueueClipboardReader(
                new ClipboardSnapshot(101, null, [], null, [ClipboardImageCandidate.FromEncoded("PNG", encoded)]),
                new ClipboardSnapshot(102, null, [], null, [ClipboardImageCandidate.FromEncoded("PNG", encoded)])),
            new ImageNormalizer(store.ImagesDirectory),
            board,
            store,
            messages.Add,
            deduplicationGate: new CaptureDeduplicationGate(clock: () => clock.Now));

        await service.HandleClipboardUpdateAsync();
        await service.HandleClipboardUpdateAsync();

        Assert.AreEqual(1, board.Items(BoardCategory.Inbox).Count);
        Assert.AreEqual(1, store.SaveCount);
        Assert.AreEqual(DuplicateSuppressedMessage, messages.Single());
        Assert.AreEqual(1, Directory.GetFiles(store.ImagesDirectory).Length);
    }

    [TestMethod]
    public async Task HandleUpdate_IdenticalFileAndClipboardImageRepresentations_CreateOneItem()
    {
        using var directory = new TestDirectory();
        var encoded = await CreateEncodedImageAsync(2, 2, new Rgba32(200, 120, 40));
        var sourcePath = Path.Combine(directory.Root, "wechat-source.png");
        await File.WriteAllBytesAsync(sourcePath, encoded);
        var board = new BoardService();
        var store = new FakeBoardStore(directory.Root);
        var clock = new MutableCaptureClock();
        var messages = new List<string>();
        var service = new ClipboardCaptureService(
            new QueueClipboardReader(
                new ClipboardSnapshot(110, null, [sourcePath], null),
                new ClipboardSnapshot(111, null, [], null, [ClipboardImageCandidate.FromEncoded("PNG", encoded)])),
            new ImageNormalizer(store.ImagesDirectory),
            board,
            store,
            messages.Add,
            deduplicationGate: new CaptureDeduplicationGate(clock: () => clock.Now));

        await service.HandleClipboardUpdateAsync();
        await service.HandleClipboardUpdateAsync();

        Assert.AreEqual(1, board.Items(BoardCategory.Inbox).Count);
        Assert.AreEqual(1, store.SaveCount);
        Assert.AreEqual(DuplicateSuppressedMessage, messages.Single());
        Assert.AreEqual(1, Directory.GetFiles(store.ImagesDirectory).Length);
    }

    [TestMethod]
    public async Task HandleUpdate_IdenticalBackToBackTextWithDifferentSequences_CreateOneItem()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var store = new FakeBoardStore(directory.Root);
        var clock = new MutableCaptureClock();
        var messages = new List<string>();
        var service = new ClipboardCaptureService(
            new QueueClipboardReader(
                new ClipboardSnapshot(120, null, [], "same text"),
                new ClipboardSnapshot(121, null, [], "same text")),
            new FakeImageNormalizer(directory.Root),
            board,
            store,
            messages.Add,
            deduplicationGate: new CaptureDeduplicationGate(clock: () => clock.Now));

        await service.HandleClipboardUpdateAsync();
        await service.HandleClipboardUpdateAsync();

        Assert.AreEqual(1, board.Items(BoardCategory.Inbox).Count);
        Assert.AreEqual(1, store.SaveCount);
        CollectionAssert.AreEqual(new[] { DuplicateSuppressedMessage }, messages);
    }

    [TestMethod]
    public async Task HandleUpdate_IdenticalTextAfterDuplicateWindow_IsCollectedAgain()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var store = new FakeBoardStore(directory.Root);
        var clock = new MutableCaptureClock();
        var service = new ClipboardCaptureService(
            new QueueClipboardReader(
                new ClipboardSnapshot(130, null, [], "same text"),
                new ClipboardSnapshot(131, null, [], "same text")),
            new FakeImageNormalizer(directory.Root),
            board,
            store,
            _ => { },
            deduplicationGate: new CaptureDeduplicationGate(clock: () => clock.Now));

        await service.HandleClipboardUpdateAsync();
        clock.Now = clock.Now.Add(TimeSpan.FromSeconds(6));
        await service.HandleClipboardUpdateAsync();

        Assert.AreEqual(2, board.Items(BoardCategory.Inbox).Count);
        Assert.AreEqual(2, store.SaveCount);
    }

    [TestMethod]
    public async Task HandleUpdate_DifferentTextWithinDuplicateWindow_BothCollected()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var store = new FakeBoardStore(directory.Root);
        var clock = new MutableCaptureClock();
        var service = new ClipboardCaptureService(
            new QueueClipboardReader(
                new ClipboardSnapshot(140, null, [], "first"),
                new ClipboardSnapshot(141, null, [], "second")),
            new FakeImageNormalizer(directory.Root),
            board,
            store,
            _ => { },
            deduplicationGate: new CaptureDeduplicationGate(clock: () => clock.Now));

        await service.HandleClipboardUpdateAsync();
        await service.HandleClipboardUpdateAsync();

        Assert.AreEqual(2, board.Items(BoardCategory.Inbox).Count);
        Assert.AreEqual(2, store.SaveCount);
    }

    [TestMethod]
    public async Task HandleUpdate_FailedSaveDoesNotRecordTextForDuplicateSuppression()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var store = new FakeBoardStore(directory.Root) { FailSaves = true };
        var clock = new MutableCaptureClock();
        var messages = new List<string>();
        var service = new ClipboardCaptureService(
            new QueueClipboardReader(
                new ClipboardSnapshot(150, null, [], "retry me"),
                new ClipboardSnapshot(151, null, [], "retry me")),
            new FakeImageNormalizer(directory.Root),
            board,
            store,
            messages.Add,
            deduplicationGate: new CaptureDeduplicationGate(clock: () => clock.Now));

        await service.HandleClipboardUpdateAsync();
        store.FailSaves = false;
        await service.HandleClipboardUpdateAsync();

        Assert.AreEqual(1, board.Items(BoardCategory.Inbox).Count);
        Assert.AreEqual(1, store.SaveCount);
        CollectionAssert.AreEqual(new[] { "本次内容未保存，请重新复制。" }, messages);
    }

    [TestMethod]
    public async Task HandleUpdate_IdenticalFilesInOneBatch_AreAllCollectedWithoutDeduplication()
    {
        using var directory = new TestDirectory();
        var encoded = await CreateEncodedImageAsync(2, 2, new Rgba32(10, 200, 90));
        var sourcePath = Path.Combine(directory.Root, "twice.png");
        await File.WriteAllBytesAsync(sourcePath, encoded);
        var board = new BoardService();
        var store = new FakeBoardStore(directory.Root);
        var clock = new MutableCaptureClock();
        var messages = new List<string>();
        var service = new ClipboardCaptureService(
            new QueueClipboardReader(new ClipboardSnapshot(160, null, [sourcePath, sourcePath], null)),
            new ImageNormalizer(store.ImagesDirectory),
            board,
            store,
            messages.Add,
            deduplicationGate: new CaptureDeduplicationGate(clock: () => clock.Now));

        await service.HandleClipboardUpdateAsync();

        Assert.AreEqual(2, board.Items(BoardCategory.Inbox).Count);
        Assert.AreEqual(1, store.SaveCount);
        Assert.IsEmpty(messages);
        Assert.AreEqual(2, Directory.GetFiles(store.ImagesDirectory).Length);
    }

    private sealed class MutableCaptureClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
