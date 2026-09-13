using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class DailyReviewMigrationTests
{
    [TestMethod]
    public async Task EnsureAsync_ClearsReferencePreservesOtherCategoriesAndSetsReviewName()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var board = new BoardService();
        var reviewItem = board.AddText("旧复盘卡片", BoardCategory.Reference);
        var promptItem = board.AddText("保留的提示词", BoardCategory.Prompt);
        promptItem.IsPinned = true;
        var store = new LocalStore(paths, new AtomicTextWriter());
        var settings = WindowSettings.Default;

        var migrated = await new DailyReviewMigration(store).EnsureAsync(board, settings);

        Assert.AreEqual(0, board.Items(BoardCategory.Reference).Count);
        Assert.AreEqual(promptItem.Id, board.Items(BoardCategory.Prompt).Single().Id);
        Assert.IsTrue(board.Items(BoardCategory.Prompt).Single().IsPinned);
        Assert.AreEqual("复盘", migrated.CategoryName(BoardCategory.Reference));
        Assert.AreEqual(DailyReviewMigration.CurrentVersion, migrated.ReviewMigrationVersion);
        Assert.IsTrue(File.Exists(paths.BoardFile));
        Assert.IsTrue(File.Exists(paths.SettingsFile));
        Assert.IsFalse(File.ReadAllText(paths.BoardFile).Contains(reviewItem.Text!, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task EnsureAsync_IsIdempotentWhenAlreadyMigrated()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var board = new BoardService();
        var item = board.AddText("保留", BoardCategory.Prompt);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var settings = WindowSettings.Default with
        {
            ReviewMigrationVersion = DailyReviewMigration.CurrentVersion,
            CategoryNames = new Dictionary<BoardCategory, string>
            {
                [BoardCategory.Reference] = "我的复盘"
            }
        };
        await store.SaveBoardAsync(board.CreateSnapshot());
        await store.SaveSettingsAsync(settings);

        var migrated = await new DailyReviewMigration(store).EnsureAsync(board, settings);

        Assert.AreEqual(settings, migrated);
        Assert.AreEqual(item.Id, board.Items(BoardCategory.Prompt).Single().Id);
    }

    [TestMethod]
    public async Task EnsureAsync_WhenSettingsSaveFails_RestoresBoardInMemoryAndOnDisk()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var board = new BoardService();
        var reviewItem = board.AddText("不能丢失", BoardCategory.Reference);
        var store = new LocalStore(paths, new FailingSettingsWriter());

        await Assert.ThrowsExactlyAsync<IOException>(() =>
            new DailyReviewMigration(store).EnsureAsync(board, WindowSettings.Default));

        Assert.AreEqual(reviewItem.Id, board.Items(BoardCategory.Reference).Single().Id);
        var persisted = await new LocalStore(paths, new AtomicTextWriter()).LoadBoardAsync();
        Assert.AreEqual(reviewItem.Id, persisted.Items.Single().Id);
    }

    private sealed class FailingSettingsWriter : IAtomicTextWriter
    {
        private readonly AtomicTextWriter _writer = new();

        public Task WriteAsync(string path, string content, CancellationToken cancellationToken = default) =>
            Path.GetFileName(path) == "settings.json"
                ? throw new IOException("Injected settings failure.")
                : _writer.WriteAsync(path, content, cancellationToken);
    }
}
