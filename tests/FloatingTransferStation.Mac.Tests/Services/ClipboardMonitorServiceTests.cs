using Avalonia.Input;
using FloatingTransferStation.Mac.Services;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FloatingTransferStation.Mac.Tests.Services;

[TestClass]
public sealed class ClipboardMonitorServiceTests
{
    [TestMethod]
    public async Task PrivateGeneration_NeverReadsClipboardContents()
    {
        using var directory = new ServiceTestDirectory();
        var board = new BoardService();
        var clipboardReads = 0;
        var monitor = Create(directory, board, () => { clipboardReads++; return Task.FromResult<IAsyncDataTransfer?>(null); },
            new StateReader(new PasteboardState(1, ["org.nspasteboard.ConcealedType"])));

        Assert.IsFalse(await monitor.CaptureNowAsync());

        Assert.AreEqual(0, clipboardReads);
        Assert.IsEmpty(board.CreateSnapshot().Items);
    }

    [TestMethod]
    public async Task OwnerChangesWhileReading_DiscardsStalePayload()
    {
        using var directory = new ServiceTestDirectory();
        var board = new BoardService();
        var clipboard = new SyntheticClipboard("old public text");
        var monitor = Create(directory, board, clipboard.ReadAsync, new StateReader(
            new PasteboardState(1, ["public.utf8-plain-text"]),
            new PasteboardState(2, ["public.utf8-plain-text"])));

        Assert.IsFalse(await monitor.CaptureNowAsync());

        Assert.IsEmpty(board.CreateSnapshot().Items);
        Assert.IsFalse(File.Exists(Path.Combine(directory.Root, "board.json")));
    }

    [TestMethod]
    public async Task StableGeneration_CommitsSelectedCategoryAndStopsFurtherCaptures()
    {
        using var directory = new ServiceTestDirectory();
        var board = new BoardService();
        var clipboard = new SyntheticClipboard("captured text");
        var monitor = Create(directory, board, clipboard.ReadAsync,
            new StateReader(new PasteboardState(1, ["public.utf8-plain-text"])));

        Assert.IsTrue(await monitor.CaptureNowAsync());
        await monitor.StopAsync();
        Assert.IsFalse(await monitor.CaptureNowAsync());

        Assert.AreEqual("captured text", board.Items(BoardCategory.Prompt).Single().Text);
        Assert.AreEqual(1, clipboard.ReadCount);
    }

    private static ClipboardMonitorService Create(ServiceTestDirectory directory, BoardService board,
        Func<Task<IAsyncDataTransfer?>> clipboard, IPasteboardStateReader state)
    {
        var store = new LocalStore(AppPaths.FromDataDirectory(directory.Root), new AtomicTextWriter());
        var importer = new TransferImportService(new MacImageNormalizer(directory.Images), board, store, _ => { }, new BoardOperationGate());
        return new ClipboardMonitorService(() => null, () => BoardCategory.Prompt, new AvaloniaTransferReader(), importer, _ => { }, state, clipboard);
    }

    private sealed class StateReader(params PasteboardState[] states) : IPasteboardStateReader
    {
        private int _index;
        public PasteboardState ReadState() => states[Math.Min(_index++, states.Length - 1)];
    }

    private sealed class SyntheticClipboard(string text)
    {
        public int ReadCount { get; private set; }
        public Task<IAsyncDataTransfer?> ReadAsync()
        {
            ReadCount++;
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(text));
            return Task.FromResult<IAsyncDataTransfer?>(data);
        }
    }
}
