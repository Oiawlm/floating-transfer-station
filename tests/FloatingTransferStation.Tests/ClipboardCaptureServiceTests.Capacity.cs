using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using System.Runtime.InteropServices;

namespace FloatingTransferStation.Tests;

public sealed partial class ClipboardCaptureServiceTests
{
    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task HandleUpdate_KnownDuplicateSequenceDoesNotReadAnotherPayloadWhileBusy()
    {
        using var directory = new TestDirectory();
        var reader = new SequencedClipboardReader { Sequence = 901, Text = "same" };
        var board = new BoardService();
        var store = new BlockingFirstSaveStore(directory.Root);
        var service = new ClipboardCaptureService(
            reader, new FakeImageNormalizer(directory.Root), board, store, _ => { });
        var first = service.HandleClipboardUpdateAsync();
        await store.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var duplicates = Enumerable.Range(0, 32).Select(_ => service.HandleClipboardUpdateAsync()).ToArray();
        var readsWhileBusy = reader.ReadCount;
        store.ReleaseFirstSave();
        await Task.WhenAll(duplicates.Append(first)).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(1, readsWhileBusy, "Coalesce duplicate notifications before allocating their payloads.");
        Assert.HasCount(1, board.Items(BoardCategory.Inbox));
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task HandleUpdate_FullQueueRejectsBeforeReadingAndAcceptsRetryAfterDrain()
    {
        using var directory = new TestDirectory();
        var reader = new SequencedClipboardReader { Sequence = 902, Text = "first" };
        var board = new BoardService();
        var store = new BlockingFirstSaveStore(directory.Root);
        var messages = new List<string>();
        var service = new ClipboardCaptureService(
            reader, new FakeImageNormalizer(directory.Root), board, store, messages.Add,
            maximumPendingCaptures: 2);
        var first = service.HandleClipboardUpdateAsync();
        await store.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        reader.Sequence = 903;
        reader.Text = "second";
        var second = service.HandleClipboardUpdateAsync();
        reader.Sequence = 904;
        reader.Text = "retry";
        var rejected = service.HandleClipboardUpdateAsync();
        var readsWhileFull = reader.ReadCount;
        store.ReleaseFirstSave();
        await Task.WhenAll(first, second, rejected).WaitAsync(TimeSpan.FromSeconds(2));
        var beforeRetry = board.Items(BoardCategory.Inbox).Select(item => item.Text).ToArray();
        await service.HandleClipboardUpdateAsync();

        Assert.AreEqual(2, readsWhileFull);
        CollectionAssert.AreEqual(new[] { "second", "first" }, beforeRetry);
        Assert.AreEqual("retry", board.Items(BoardCategory.Inbox)[0].Text);
        Assert.HasCount(1, messages);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task HandleUpdate_PayloadBudgetRejectsOverflowWithoutConsumingItsSequence()
    {
        using var directory = new TestDirectory();
        var reader = new SequencedClipboardReader { Sequence = 905, Text = "first" };
        var board = new BoardService();
        var store = new BlockingFirstSaveStore(directory.Root);
        var messages = new List<string>();
        var service = new ClipboardCaptureService(
            reader, new FakeImageNormalizer(directory.Root), board, store, messages.Add,
            maximumPendingBytes: 12);
        var first = service.HandleClipboardUpdateAsync();
        await store.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        reader.Sequence = 906;
        reader.Text = "next";
        var rejected = service.HandleClipboardUpdateAsync();
        store.ReleaseFirstSave();
        await Task.WhenAll(first, rejected).WaitAsync(TimeSpan.FromSeconds(2));
        var countBeforeRetry = board.Items(BoardCategory.Inbox).Count;
        await service.HandleClipboardUpdateAsync();

        Assert.AreEqual(1, countBeforeRetry);
        Assert.AreEqual("next", board.Items(BoardCategory.Inbox)[0].Text);
        Assert.HasCount(1, messages);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task HandleUpdate_FailedReadReleasesSequenceAndQueueCapacity()
    {
        using var directory = new TestDirectory();
        var reader = new BusySequenceReader();
        var board = new BoardService();
        var messages = new List<string>();
        var service = new ClipboardCaptureService(
            reader, new FakeImageNormalizer(directory.Root), board,
            new FakeBoardStore(directory.Root), messages.Add,
            retryDelays: [], maximumPendingCaptures: 1);

        await service.HandleClipboardUpdateAsync();
        await service.HandleClipboardUpdateAsync();

        Assert.AreEqual(2, reader.ReadCount);
        Assert.AreEqual("after failure", board.Items(BoardCategory.Inbox).Single().Text);
        Assert.HasCount(1, messages);
    }

    private sealed class BusySequenceReader : IClipboardReader
    {
        public Exception FirstFailure { get; set; } = new ExternalException("synthetic busy clipboard");
        public int ReadCount { get; private set; }
        public uint? GetSequenceNumber() => 907;
        public Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        {
            if (++ReadCount == 1)
            {
                throw FirstFailure;
            }

            return Task.FromResult(new ClipboardSnapshot(907, null, [], "after failure"));
        }
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HandleUpdate_ExceptionOrCancellationReleasesCapacity(bool canceled)
    {
        using var directory = new TestDirectory();
        var reader = new BusySequenceReader
        {
            FirstFailure = canceled ? new OperationCanceledException() : new InvalidDataException("synthetic unreadable payload")
        };
        var board = new BoardService();
        var service = new ClipboardCaptureService(
            reader, new FakeImageNormalizer(directory.Root), board,
            new FakeBoardStore(directory.Root), _ => { }, maximumPendingCaptures: 1);

        if (canceled)
        {
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.HandleClipboardUpdateAsync());
        }
        else
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.HandleClipboardUpdateAsync());
        }

        await service.HandleClipboardUpdateAsync();
        Assert.AreEqual(2, reader.ReadCount);
        Assert.AreEqual("after failure", board.Items(BoardCategory.Inbox).Single().Text);
    }

    private sealed class SequencedClipboardReader : IClipboardReader
    {
        public uint Sequence { get; set; }
        public string Text { get; set; } = string.Empty;
        public int ReadCount { get; private set; }
        public uint? GetSequenceNumber() => Sequence;
        public Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult(new ClipboardSnapshot(Sequence, null, [], Text));
        }
    }
}
