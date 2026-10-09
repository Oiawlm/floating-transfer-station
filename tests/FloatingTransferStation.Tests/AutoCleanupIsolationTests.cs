using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class AutoCleanupIsolationTests
{
    [TestMethod]
    public async Task ClearNonPinned_SweepCategories_NeverTouchesReviewFilesOrReferenceEntries()
    {
        // 契约 #18 复盘隔离:走 LocalStore + BoardMutationService 全链路清扫,
        // reviews/yyyy-MM-dd.md 字节不变,Reference(复盘标签)条目原样保留。
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        using LocalStore store = new LocalStore(paths, new AtomicTextWriter());
        var board = new BoardService();
        board.AddText("图片内容", BoardCategory.CustomerOriginal);
        board.AddText("文本2内容", BoardCategory.Prompt);
        board.AddText("待分类内容", BoardCategory.Inbox);
        var referenceEntry = board.AddText("复盘标签条目", DailyReviewMigration.ReviewCategory);
        var reviewDate = new DateOnly(2026, 10, 9);
        await store.SaveAsync(reviewDate, "# 复盘\n\n今天的内容");
        var reviewPath = Path.Combine(paths.ReviewsDirectory, "2026-10-09.md");
        var reviewBytesBefore = await File.ReadAllBytesAsync(reviewPath);

        var mutations = new BoardMutationService(board, store, _ => { });
        var outcome = await mutations.ClearNonPinnedAsync(AutoCleanupSchedule.SweepCategories);

        Assert.IsTrue(outcome.Saved);
        Assert.AreEqual(3, outcome.RemovedCount);
        CollectionAssert.AreEqual(
            reviewBytesBefore,
            await File.ReadAllBytesAsync(reviewPath),
            "清扫不得改动复盘 Markdown 文件的任何字节。");
        Assert.AreSame(
            referenceEntry,
            board.Items(DailyReviewMigration.ReviewCategory).Single());
        var persisted = await store.LoadBoardAsync();
        Assert.IsTrue(persisted.Items.Any(item =>
            item.Category == DailyReviewMigration.ReviewCategory &&
            item.Text == "复盘标签条目"),
            "复盘标签条目必须仍持久化在 board.json。");
        Assert.IsFalse(persisted.Items.Any(item => item.Text == "图片内容"));
        Assert.IsFalse(persisted.Items.Any(item => item.Text == "待分类内容"));
    }
}
