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
        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes("# 今天\n\n完成了功能"),
            await File.ReadAllBytesAsync(Path.Combine(paths.ReviewsDirectory, "2026-09-14.md")));
    }

    [TestMethod]
    public async Task SaveWhitespace_DeletesTheDateFile()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        using IDailyReviewStore store = new LocalStore(paths, new AtomicTextWriter());
        var date = new DateOnly(2026, 9, 14);

        await store.SaveAsync(date, "内容");
        await store.SaveAsync(date, " \n\t ");

        Assert.IsFalse((await store.LoadAsync(date)).Exists);
        Assert.IsFalse(File.Exists(Path.Combine(paths.ReviewsDirectory, "2026-09-14.md")));
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

    [TestMethod]
    public async Task EnumerateRecordedDates_EmptyOrMissingDirectoryReturnsNone()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        using IDailyReviewStore store = new LocalStore(paths, new AtomicTextWriter());

        // 目录从未创建（未 StartWatching、未保存过）：空表而非异常。
        Assert.AreEqual(0, (await store.EnumerateRecordedDatesAsync()).Count);

        Directory.CreateDirectory(paths.ReviewsDirectory);
        Assert.AreEqual(0, (await store.EnumerateRecordedDatesAsync()).Count);
    }

    [TestMethod]
    public async Task EnumerateRecordedDates_SkipsBlankInvalidAndNonMarkdownFilesAndSortsAcrossYears()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        using IDailyReviewStore store = new LocalStore(paths, new AtomicTextWriter());
        Directory.CreateDirectory(paths.ReviewsDirectory);

        File.WriteAllText(Path.Combine(paths.ReviewsDirectory, "2026-11-02.md"), "今天的内容");
        File.WriteAllText(Path.Combine(paths.ReviewsDirectory, "2025-12-31.md"), "去年最后一天");
        File.WriteAllText(Path.Combine(paths.ReviewsDirectory, "2024-06-15.md"), "前年有记录");
        File.WriteAllText(Path.Combine(paths.ReviewsDirectory, "2026-11-01.md"), " \n\t ");   // 空白＝无记录
        File.WriteAllText(Path.Combine(paths.ReviewsDirectory, "2026-11-03.md"), "");         // 空文件＝无记录
        File.WriteAllText(Path.Combine(paths.ReviewsDirectory, "notes.md"), "非法文件名跳过"); // 非日期名
        File.WriteAllText(Path.Combine(paths.ReviewsDirectory, "2026-1-1.md"), "非法格式跳过"); // 非 yyyy-MM-dd
        File.WriteAllText(Path.Combine(paths.ReviewsDirectory, "2026-11-04.txt"), "非 .md 跳过");

        var dates = await store.EnumerateRecordedDatesAsync();

        // 跨年升序；空白/非法名/非法格式/非 md 全部不进表。
        CollectionAssert.AreEqual(
            new[] { new DateOnly(2024, 6, 15), new DateOnly(2025, 12, 31), new DateOnly(2026, 11, 2) },
            dates.ToArray());
    }

    [TestMethod]
    public async Task EnumerateRecordedDates_SharesTheExistsJudgmentWithLoadAsync()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        using IDailyReviewStore store = new LocalStore(paths, new AtomicTextWriter());
        Directory.CreateDirectory(paths.ReviewsDirectory);

        var recorded = new DateOnly(2026, 10, 30);
        var blank = new DateOnly(2026, 10, 31);
        File.WriteAllText(Path.Combine(paths.ReviewsDirectory, "2026-10-30.md"), "有记录");
        File.WriteAllText(Path.Combine(paths.ReviewsDirectory, "2026-10-31.md"), "　\n");

        var dates = await store.EnumerateRecordedDatesAsync();

        // 同源锁定：枚举进表的日期 LoadAsync Exists=true；被空白排除的日期 Exists=false。
        CollectionAssert.Contains(dates.ToArray(), recorded);
        CollectionAssert.DoesNotContain(dates.ToArray(), blank);
        Assert.IsTrue((await store.LoadAsync(recorded)).Exists);
        Assert.IsFalse((await store.LoadAsync(blank)).Exists);
    }

    [TestMethod]
    public async Task EnumerateRecordedDates_FollowsSaveAndDeleteLifecycle()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        using IDailyReviewStore store = new LocalStore(paths, new AtomicTextWriter());

        var first = new DateOnly(2026, 10, 28);
        var second = new DateOnly(2026, 10, 29);

        Assert.AreEqual(0, (await store.EnumerateRecordedDatesAsync()).Count);

        await store.SaveAsync(second, "第二条");
        await store.SaveAsync(first, "第一条");
        CollectionAssert.AreEqual(
            new[] { first, second },
            (await store.EnumerateRecordedDatesAsync()).ToArray());

        // 保存空白＝删文件（无记录）；显式删除同样出表。
        await store.SaveAsync(first, "  ");
        await store.DeleteAsync(second);
        Assert.AreEqual(0, (await store.EnumerateRecordedDatesAsync()).Count);
    }
}
