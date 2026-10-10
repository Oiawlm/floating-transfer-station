using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class AutoCleanupScheduleTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void GetExpiryCutoff_IsExactlyNowMinusCleanupInterval()
    {
        Assert.AreEqual(
            Now - TimeSpan.FromHours(24),
            AutoCleanupSchedule.GetExpiryCutoff(Now));
    }

    [TestMethod]
    public void CleanupInterval_IsExactlyTwentyFourHoursAndFixed()
    {
        Assert.AreEqual(TimeSpan.FromHours(24), AutoCleanupSchedule.CleanupInterval);
    }

    [TestMethod]
    public void SweepCategories_IsExactlyTheThreeContentCategoriesWithoutReview()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                BoardCategory.CustomerOriginal,
                BoardCategory.Prompt,
                BoardCategory.Inbox
            },
            AutoCleanupSchedule.SweepCategories.ToArray());
        Assert.IsFalse(
            AutoCleanupSchedule.SweepCategories.Contains(DailyReviewMigration.ReviewCategory),
            "复盘复用的分类绝不能进入清扫范围。");
    }
}
