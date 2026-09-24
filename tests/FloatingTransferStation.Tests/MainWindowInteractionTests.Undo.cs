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

            Assert.IsFalse(await window.UndoLastDeleteFromPanelAsync());
            Assert.AreEqual(0, store.SaveCount, "空栈撤销不得保存。");

            var mutations = GetPrivateField<BoardMutationService>(window, "_mutations");
            Assert.IsTrue(await mutations.DeleteManyAsync([deleted.Id]));
            CollectionAssert.AreEqual(
                new[] { keep.Id },
                board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());

            Assert.IsTrue(await window.UndoLastDeleteFromPanelAsync());

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
