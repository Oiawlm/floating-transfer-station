using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class DefaultCaptureCategoryStateTests
{
    [TestMethod]
    public void InitialAndRepeatedSelection_KeepExactlyOneValidCurrentCategory()
    {
        var state = new DefaultCaptureCategoryState();

        Assert.AreEqual(BoardCategory.Inbox, state.Current);
        Assert.IsTrue(state.Set(BoardCategory.CustomerOriginal));
        Assert.AreEqual(BoardCategory.CustomerOriginal, state.Current);
        Assert.IsFalse(state.Set(BoardCategory.CustomerOriginal));
        Assert.AreEqual(BoardCategory.CustomerOriginal, state.Current);
    }

    [TestMethod]
    public void Set_RejectsUndefinedCategoryWithoutChangingCurrentValue()
    {
        var state = new DefaultCaptureCategoryState();
        state.Set(BoardCategory.CustomerOriginal);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => state.Set((BoardCategory)99));
        Assert.AreEqual(BoardCategory.CustomerOriginal, state.Current);
    }

    [TestMethod]
    public void Set_RejectsNonContentBucketRolesWithoutChangingCurrentValue()
    {
        // #30 纵深守卫（Issue #162）：默认接收只能落在内容桶——复盘与快捷启动
        // 标签绝不能成为剪贴板落点（UI 点击另有角色闸）。
        var state = new DefaultCaptureCategoryState();
        state.Set(BoardCategory.CustomerOriginal);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => state.Set(DailyReviewMigration.ReviewCategory));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => state.Set(QuickLaunchMigration.LauncherCategory));
        Assert.AreEqual(BoardCategory.CustomerOriginal, state.Current);
    }
}
