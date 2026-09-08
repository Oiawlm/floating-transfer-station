using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using FloatingTransferStation.Mac.Services;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FloatingTransferStation.Mac.Tests.Ui;

[TestClass]
[DoNotParallelize]
[TestCategory("Headless")]
public sealed class MainWindowLifecycleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task CloseDuringInitialLoad_WaitsAndPreservesExistingContent()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApplication));
        await session.Dispatch(async () =>
        {
            using var directory = new UiTestDirectory();
            var original = await SeedAsync(directory.Paths);
            var originalJson = await File.ReadAllTextAsync(directory.Paths.BoardFile);
            var beforeLoad = NewCompletionSource();
            var writer = new ControlledAtomicTextWriter();
            var window = CreateWindow(directory.Paths, writer, beforeLoad.Task);
            var closed = ObserveClose(window);
            try
            {
                window.Show();
                window.Close();

                Assert.IsTrue(window.IsVisible, "Closing must wait for the initial board load.");
                Assert.IsFalse(closed.Task.IsCompleted);
                Assert.AreEqual(0, writer.WriteCount);
                Assert.AreEqual(originalJson, await File.ReadAllTextAsync(directory.Paths.BoardFile));

                beforeLoad.TrySetResult();
                await closed.Task.WaitAsync(Timeout);

                var loaded = await NewStore(directory.Paths).LoadBoardAsync();
                CollectionAssert.AreEqual(original.Select(item => item.Id).ToArray(), loaded.Items.Select(item => item.Id).ToArray());
                CollectionAssert.AreEqual(original.Select(item => item.Text).ToArray(), loaded.Items.Select(item => item.Text).ToArray());
                Assert.AreEqual("旧名", (await NewStore(directory.Paths).LoadSettingsAsync()).CategoryName(BoardCategory.Inbox));
            }
            finally
            {
                beforeLoad.TrySetResult();
                await CloseWindowAsync(window, closed.Task);
            }
            return true;
        }, CancellationToken.None);
    }

    [TestMethod]
    public async Task CloseDuringRename_WaitsForSaveAndKeepsNewCategoryName()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApplication));
        await session.Dispatch(async () =>
        {
            using var directory = new UiTestDirectory();
            await SeedAsync(directory.Paths);
            var writer = new ControlledAtomicTextWriter("settings.json");
            var window = CreateWindow(directory.Paths, writer);
            var closed = ObserveClose(window);
            try
            {
                window.Show();
                await WaitForInitializationAsync(window);
                window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F2 });
                var editor = Field<TextBox>(window, "_rename");
                Assert.IsTrue(editor.IsVisible);
                editor.Text = "新分类";
                editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                await writer.PausedWriteStarted.Task.WaitAsync(Timeout);

                window.Close();

                Assert.IsTrue(window.IsVisible, "Shutdown must wait for the pending rename transaction.");
                Assert.IsFalse(closed.Task.IsCompleted);
                Assert.AreEqual("旧名", (await NewStore(directory.Paths).LoadSettingsAsync()).CategoryName(BoardCategory.Inbox));

                writer.ReleasePausedWrite.TrySetResult();
                await closed.Task.WaitAsync(Timeout);

                Assert.AreEqual("新分类", (await NewStore(directory.Paths).LoadSettingsAsync()).CategoryName(BoardCategory.Inbox));
                Assert.AreEqual(3, (await NewStore(directory.Paths).LoadBoardAsync()).Items.Count);
            }
            finally
            {
                writer.ReleasePausedWrite.TrySetResult();
                await CloseWindowAsync(window, closed.Task);
            }
            return true;
        }, CancellationToken.None);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedDeletion_PreservesLatestSelectionAndRetryDeletesOnlySelectedItem(bool selectAnotherWhileSaving)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApplication));
        await session.Dispatch(async () =>
        {
            using var directory = new UiTestDirectory();
            var original = await SeedAsync(directory.Paths);
            var writer = new ControlledAtomicTextWriter("board.json", failPausedWrite: true);
            var window = CreateWindow(directory.Paths, writer);
            var closed = ObserveClose(window);
            Task deletion = Task.CompletedTask;
            try
            {
                window.Show();
                await WaitForInitializationAsync(window);
                var board = Field<BoardService>(window, "_board");
                var first = board.Items(BoardCategory.Inbox).Single(item => item.Id == original[0].Id);
                var alternate = board.Items(BoardCategory.Inbox).Single(item => item.Id == original[2].Id);
                SelectItem(window, first);

                deletion = InvokeTask(window, "DeleteAsync", true);
                await writer.PausedWriteStarted.Task.WaitAsync(Timeout);
                Assert.IsFalse(board.Items(BoardCategory.Inbox).Any(item => item.Id == first.Id));
                if (selectAnotherWhileSaving)
                {
                    SelectItem(window, alternate);
                }

                writer.ReleasePausedWrite.TrySetResult();
                await deletion.WaitAsync(Timeout);

                var expectedSelection = selectAnotherWhileSaving ? alternate.Id : first.Id;
                CollectionAssert.AreEquivalent(original.Select(item => item.Id).ToArray(), board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
                CollectionAssert.AreEquivalent(new[] { expectedSelection }, Field<SelectionState>(window, "_selection").Ids.ToArray());
                CollectionAssert.AreEquivalent(new[] { expectedSelection }, Field<ListBox>(window, "_list").SelectedItems!.Cast<BoardItem>().Select(item => item.Id).ToArray());
                Assert.AreEqual(3, (await NewStore(directory.Paths).LoadBoardAsync()).Items.Count);

                // The next header delete must not fall back to clearing the whole category.
                await InvokeTask(window, "DeleteAsync", true).WaitAsync(Timeout);
                var persisted = await NewStore(directory.Paths).LoadBoardAsync();
                CollectionAssert.AreEquivalent(original.Where(item => item.Id != expectedSelection).Select(item => item.Id).ToArray(), persisted.Items.Select(item => item.Id).ToArray());
            }
            finally
            {
                writer.ReleasePausedWrite.TrySetResult();
                await deletion.WaitAsync(Timeout);
                await CloseWindowAsync(window, closed.Task);
            }
            return true;
        }, CancellationToken.None);
    }

    private static MainWindow CreateWindow(AppPaths paths, IAtomicTextWriter writer, Task? beforeLoad = null) =>
        new(paths, writer: writer, beforeLoad: beforeLoad, pasteboardStateReader: new EmptyPasteboardStateReader());

    private static async Task WaitForInitializationAsync(MainWindow window) =>
        Assert.IsTrue(await Field<TaskCompletionSource<bool>>(window, "_initialized").Task.WaitAsync(Timeout));

    private static TaskCompletionSource ObserveClose(MainWindow window)
    {
        var closed = NewCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        return closed;
    }

    private static async Task CloseWindowAsync(MainWindow window, Task closed)
    {
        if (closed.IsCompleted) return;
        window.Close();
        await closed.WaitAsync(Timeout);
    }

    private static LocalStore NewStore(AppPaths paths) => new(paths, new AtomicTextWriter());

    private static async Task<BoardItem[]> SeedAsync(AppPaths paths)
    {
        var items = Enumerable.Range(0, 3).Select(index =>
        {
            var item = BoardItem.CreateText($"原有内容 {index + 1}", Guid.NewGuid(), DateTimeOffset.UtcNow);
            item.Order = index;
            return item;
        }).ToArray();
        var store = NewStore(paths);
        await store.SaveBoardAsync(new BoardSnapshot { Items = items.ToList() });
        await store.SaveSettingsAsync(WindowSettings.Default.WithCategoryName(BoardCategory.Inbox, "旧名"));
        return items;
    }

    // Reflection stays at the window boundary: these invoke the actual UI command and inspect
    // its selection, rather than replacing the production mutation or persistence behavior.
    private static T Field<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static Task InvokeTask(MainWindow window, string name, params object[] arguments) =>
        (Task)typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments)!;

    private static void SelectItem(MainWindow window, BoardItem item) =>
        typeof(MainWindow).GetMethod("SelectItem", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [item, false, false]);

    private static TaskCompletionSource NewCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class EmptyPasteboardStateReader : IPasteboardStateReader
    {
        public PasteboardState ReadState() => new(0, ["org.nspasteboard.ConcealedType"]);
    }

    private sealed class ControlledAtomicTextWriter(string? pausedFile = null, bool failPausedWrite = false) : IAtomicTextWriter
    {
        private readonly AtomicTextWriter _writer = new();
        private int _matchingWrites;
        private int _writeCount;
        public int WriteCount => Volatile.Read(ref _writeCount);
        public TaskCompletionSource PausedWriteStarted { get; } = NewCompletionSource();
        public TaskCompletionSource ReleasePausedWrite { get; } = NewCompletionSource();

        public async Task WriteAsync(string path, string content, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writeCount);
            if (Path.GetFileName(path) == pausedFile && Interlocked.Increment(ref _matchingWrites) == 1)
            {
                PausedWriteStarted.TrySetResult();
                await ReleasePausedWrite.Task.WaitAsync(cancellationToken);
                if (failPausedWrite) throw new IOException("Injected first-write failure.");
            }
            await _writer.WriteAsync(path, content, cancellationToken);
        }
    }

    private sealed class UiTestDirectory : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "FloatingTransferStation-ui-tests", Guid.NewGuid().ToString("N"));
        public AppPaths Paths => AppPaths.FromDataDirectory(_root);
        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
