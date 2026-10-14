using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class AutoCleanupScheduleTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    /// <summary>档位 → 保留时长的独立期望表：枚举新增档位未进表即红（漏映射另有
    /// 编译期防线——GetRetentionDuration 的 switch 穷尽 + 质量门 -warnaserror）。</summary>
    private static readonly IReadOnlyDictionary<AutoCleanupRetention, TimeSpan> RetentionDurations =
        new Dictionary<AutoCleanupRetention, TimeSpan>
        {
            [AutoCleanupRetention.TwoHours] = TimeSpan.FromHours(2),
            [AutoCleanupRetention.FourHours] = TimeSpan.FromHours(4),
            [AutoCleanupRetention.SixHours] = TimeSpan.FromHours(6),
            [AutoCleanupRetention.EightHours] = TimeSpan.FromHours(8),
            [AutoCleanupRetention.TenHours] = TimeSpan.FromHours(10),
            [AutoCleanupRetention.TwelveHours] = TimeSpan.FromHours(12),
            [AutoCleanupRetention.TwentyFourHours] = TimeSpan.FromHours(24)
        };

    [TestMethod]
    public void GetRetentionDuration_MapsEveryTierToItsExactDuration()
    {
        CollectionAssert.AreEquivalent(
            RetentionDurations.Keys.ToList(),
            Enum.GetValues<AutoCleanupRetention>().ToList(),
            "期望表必须覆盖全部档位（新增档位必须同步补表与映射）。");
        foreach (var (retention, duration) in RetentionDurations)
        {
            Assert.AreEqual(
                duration,
                AutoCleanupSchedule.GetRetentionDuration(retention),
                $"档位 {retention} 的保留时长映射。");
        }
    }

    [TestMethod]
    public void DefaultTier_IsTwentyFourHours()
    {
        Assert.AreEqual(
            AutoCleanupRetention.TwentyFourHours,
            AppPreferences.Default.AutoCleanupRetention,
            "默认档位必须是 24 小时（老用户升级零感知）。");
        Assert.AreEqual(
            TimeSpan.FromHours(24),
            AutoCleanupSchedule.GetRetentionDuration(AppPreferences.Default.AutoCleanupRetention));
    }

    [TestMethod]
    public void GetExpiryCutoff_SubtractsTheGivenRetentionDuration()
    {
        foreach (var (retention, duration) in RetentionDurations)
        {
            Assert.AreEqual(
                Now - duration,
                AutoCleanupSchedule.GetExpiryCutoff(Now, retention),
                $"档位 {retention} 的到期截止时刻。");
        }
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
