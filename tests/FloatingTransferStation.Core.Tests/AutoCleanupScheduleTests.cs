using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class AutoCleanupScheduleTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Evaluate_NullLastRun_EstablishesBaselineWithoutSweep()
    {
        Assert.AreEqual(
            AutoCleanupDecision.EstablishBaseline,
            AutoCleanupSchedule.Evaluate(null, Now));
    }

    [TestMethod]
    public void Evaluate_LastRunInFuture_ReanchorsInsteadOfSweeping()
    {
        // 时钟被回拨/NTP 校正导致时间戳在未来:必须重锚定,不能永久卡死也不能补扫。
        Assert.AreEqual(
            AutoCleanupDecision.EstablishBaseline,
            AutoCleanupSchedule.Evaluate(Now + TimeSpan.FromHours(1), Now));
    }

    [TestMethod]
    public void Evaluate_ExactlyAtInterval_IsDue()
    {
        Assert.AreEqual(
            AutoCleanupDecision.Due,
            AutoCleanupSchedule.Evaluate(Now - AutoCleanupSchedule.CleanupInterval, Now));
    }

    [TestMethod]
    public void Evaluate_JustBeforeInterval_Waits()
    {
        Assert.AreEqual(
            AutoCleanupDecision.Wait,
            AutoCleanupSchedule.Evaluate(
                Now - AutoCleanupSchedule.CleanupInterval + TimeSpan.FromSeconds(1),
                Now));
    }

    [TestMethod]
    public void Evaluate_BeyondInterval_IsDue()
    {
        Assert.AreEqual(
            AutoCleanupDecision.Due,
            AutoCleanupSchedule.Evaluate(Now - TimeSpan.FromHours(25), Now));
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

    [TestMethod]
    public void CleanupInterval_IsExactlyTwentyFourHours()
    {
        Assert.AreEqual(TimeSpan.FromHours(24), AutoCleanupSchedule.CleanupInterval);
    }
}
