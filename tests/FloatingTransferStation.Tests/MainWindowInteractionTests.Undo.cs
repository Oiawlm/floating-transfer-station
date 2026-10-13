using System.Windows;
using System.Windows.Controls;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public async Task DeleteThenUndoFromPanel_RestoresItemAndPersistsTwice()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var keep = board.AddText("保留");
        var deleted = board.AddText("将被删除");
        var store = new RecordingUndoStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            Assert.IsFalse(await window.UndoLastFromPanelAsync());
            Assert.AreEqual(0, store.SaveCount, "空栈撤销不得保存。");

            var mutations = GetPrivateField<BoardMutationService>(window, "_mutations");
            Assert.IsTrue(await mutations.DeleteManyAsync([deleted.Id]));
            CollectionAssert.AreEqual(
                new[] { keep.Id },
                board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());

            Assert.IsTrue(await window.UndoLastFromPanelAsync());

            CollectionAssert.AreEqual(
                new[] { deleted.Id, keep.Id },
                board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray(),
                "窗口撤销入口应恢复被删条目(回到删除时的原位,新增为置顶插入)。");
            Assert.AreEqual(2, store.SaveCount);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public async Task Undo_RestoresMostRecentDeleteWhenMultipleBatches()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var earlier = board.AddText("较早删除");
        var latest = board.AddText("最近删除");
        var store = new RecordingUndoStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            var mutations = GetPrivateField<BoardMutationService>(window, "_mutations");
            Assert.IsTrue(await mutations.DeleteManyAsync([earlier.Id]));
            Assert.IsTrue(await mutations.DeleteManyAsync([latest.Id]));

            Assert.IsTrue(await window.UndoLastFromPanelAsync(), "Ctrl+Z 必须恢复最近删除的一张。");
            CollectionAssert.AreEqual(
                new[] { latest.Id },
                board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
            Assert.AreEqual(3, store.SaveCount);

            Assert.IsTrue(await window.UndoLastFromPanelAsync());
            CollectionAssert.AreEqual(
                new[] { latest.Id, earlier.Id },
                board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray(),
                "再次撤销按后进先出恢复前一张,回到删除前布局。");
            Assert.AreEqual(4, store.SaveCount);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    private sealed class RecordingUndoStore : IBoardStore
    {
        public RecordingUndoStore(string root) => ImagesDirectory = Path.Combine(root, "images");

        public int SaveCount { get; private set; }
        public string ImagesDirectory { get; }

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
