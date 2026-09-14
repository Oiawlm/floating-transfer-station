using System.Text;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class DailyReviewStoreTests
{
    [TestMethod]
    public async Task SaveAndLoad_UsesDateMarkdownFileWithoutBom()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        using IDailyReviewStore store = new LocalStore(paths, new AtomicTextWriter());
        var date = new DateOnly(2026, 9, 14);

        await store.SaveAsync(date, "# 今天\n\n完成了功能");

        var loaded = await store.LoadAsync(date);
        Assert.AreEqual(date, loaded.Date);
        Assert.AreEqual("# 今天\n\n完成了功能", loaded.Content);
        Assert.IsTrue(loaded.Exists);
        CollectionAssert.AreEqual(new[] { date }, (await store.ListDatesAsync()).ToArray());
        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes("# 今天\n\n完成了功能"),
            await File.ReadAllBytesAsync(Path.Combine(paths.ReviewsDirectory, "2026-09-14.md")));
    }

    [TestMethod]
    public async Task SaveWhitespace_DeletesFileAndRemovesDateFromList()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        using IDailyReviewStore store = new LocalStore(paths, new AtomicTextWriter());
        var date = new DateOnly(2026, 9, 14);

        await store.SaveAsync(date, "内容");
        await store.SaveAsync(date, " \n\t ");

        Assert.IsFalse((await store.LoadAsync(date)).Exists);
        CollectionAssert.AreEqual(Array.Empty<DateOnly>(), (await store.ListDatesAsync()).ToArray());
        Assert.IsFalse(File.Exists(Path.Combine(paths.ReviewsDirectory, "2026-09-14.md")));
    }

    [TestMethod]
    public async Task ListDates_IgnoresInvalidNamesAndEmptyFilesAndSortsDescending()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        Directory.CreateDirectory(paths.ReviewsDirectory);
        await File.WriteAllTextAsync(Path.Combine(paths.ReviewsDirectory, "2026-09-10.md"), "早");
        await File.WriteAllTextAsync(Path.Combine(paths.ReviewsDirectory, "2026-09-12.md"), " ");
        await File.WriteAllTextAsync(Path.Combine(paths.ReviewsDirectory, "not-a-date.md"), "无效");
        await File.WriteAllTextAsync(Path.Combine(paths.ReviewsDirectory, "2026-09-11.txt"), "错误扩展名");
        using IDailyReviewStore store = new LocalStore(paths, new AtomicTextWriter());

        CollectionAssert.AreEqual(
            new[] { new DateOnly(2026, 9, 10) },
            (await store.ListDatesAsync()).ToArray());
    }

    [TestMethod]
    public void Merge_UsesUnchangedSideAndMarksOverlappingLineConflict()
    {
        Assert.AreEqual(
            "remote",
            DailyReviewMerge.Merge("base", "base", "remote").Content);
        Assert.AreEqual(
            "local",
            DailyReviewMerge.Merge("base", "local", "base").Content);

        var merged = DailyReviewMerge.Merge("同一行", "本地", "文件");

        Assert.IsTrue(merged.HasConflicts);
        StringAssert.Contains(merged.Content, "<<<<<<< 本地");
        StringAssert.Contains(merged.Content, "=======");
        StringAssert.Contains(merged.Content, ">>>>>>> 文件");
    }

    [TestMethod]
    public void Merge_CombinesNonOverlappingLineChanges()
    {
        var merged = DailyReviewMerge.Merge("a\nb\nc", "A\nb\nc", "a\nb\nC");

        Assert.IsFalse(merged.HasConflicts);
        Assert.AreEqual("A\nb\nC", merged.Content);
    }

    [TestMethod]
    public async Task Watching_RaisesExternalFileChangesAfterDebounce()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        using IDailyReviewStore store = new LocalStore(paths, new AtomicTextWriter());
        var observed = new TaskCompletionSource<DailyReviewFileChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += (_, args) => observed.TrySetResult(args);
        store.StartWatching();
        Directory.CreateDirectory(paths.ReviewsDirectory);

        await File.WriteAllTextAsync(Path.Combine(paths.ReviewsDirectory, "2026-09-14.md"), "来自 Obsidian");

        var change = await observed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(new DateOnly(2026, 9, 14), change.Date);
        Assert.AreNotEqual(DailyReviewFileChangeKind.RescanRequired, change.Kind);
    }

    [TestMethod]
    public async Task Watching_SuppressesTheStoreOwnAtomicWrite()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        using IDailyReviewStore store = new LocalStore(paths, new AtomicTextWriter());
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += (_, _) => observed.TrySetResult();
        store.StartWatching();

        await store.SaveAsync(new DateOnly(2026, 9, 14), "应用写入");
        await Task.Delay(400);

        Assert.IsFalse(observed.Task.IsCompleted);
    }

    [TestMethod]
    public void WindowSettings_DefaultsReviewMigrationToZero()
    {
        Assert.AreEqual(0, WindowSettings.Default.ReviewMigrationVersion);
    }

    [TestMethod]
    public void WindowSettings_ResetToDefault_PreservesReviewMigrationVersion()
    {
        var settings = WindowSettings.Default with { ReviewMigrationVersion = DailyReviewMigration.CurrentVersion };

        var reset = settings.ResetToDefault(1920, 1080);

        Assert.AreEqual(DailyReviewMigration.CurrentVersion, reset.ReviewMigrationVersion);
    }
}
