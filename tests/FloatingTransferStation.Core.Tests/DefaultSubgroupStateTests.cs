using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Core.Tests;

/// <summary>
/// 默认收集落点子分组状态（契约 #57 修订，会话态）：每内容桶分类独立、装载期
/// 无默认、纵深守卫与差分语义（镜像 DefaultCaptureCategoryState 先例）。
/// </summary>
[TestClass]
public sealed class DefaultSubgroupStateTests
{
    [TestMethod]
    public void Initial_PerCategoryNullUntilSet()
    {
        var state = new DefaultSubgroupState();

        Assert.IsNull(state.Current(BoardCategory.CustomerOriginal), "装载期重要分类无默认。");
        Assert.IsNull(state.Current(BoardCategory.Inbox), "装载期待分类无默认。");

        state.Set(BoardCategory.CustomerOriginal, 2);

        Assert.AreEqual(2, state.Current(BoardCategory.CustomerOriginal));
        Assert.IsNull(state.Current(BoardCategory.Inbox), "设置只影响该分类。");
    }

    [TestMethod]
    public void Set_RejectsNonContentBucketAndInvalidSlot()
    {
        var state = new DefaultSubgroupState();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => state.Set(BoardCategory.Reference, 1),
            "记录标签不得成为默认子分组落点。");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => state.Set(BoardCategory.Prompt, 1),
            "启动标签不得成为默认子分组落点。");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => state.Set((BoardCategory)99, 1),
            "未定义分类拒绝。");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => state.Set(BoardCategory.Inbox, -1),
            "负槽拒绝。");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => state.Set(BoardCategory.Inbox, SubgroupCatalog.SlotCount),
            "越界槽拒绝（固定 4 槽）。");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => state.Current((BoardCategory)99),
            "未定义分类读取拒绝（数组界守卫）。");
        Assert.IsNull(
            state.Current(BoardCategory.Reference),
            "定义但非内容桶＝无默认子分组语义，读取返回 null（调用方免前置角色闸）。");
    }

    [TestMethod]
    public void Set_SameValueReturnsFalse()
    {
        var state = new DefaultSubgroupState();

        Assert.IsTrue(state.Set(BoardCategory.Inbox, 1), "首次设置＝变更。");
        Assert.IsFalse(state.Set(BoardCategory.Inbox, 1), "同槽再设＝差分语义（未变）。");
        Assert.IsTrue(state.Set(BoardCategory.Inbox, 3), "换槽＝变更。");
        Assert.AreEqual(3, state.Current(BoardCategory.Inbox));
    }

    [TestMethod]
    public void Set_IndependentPerCategory()
    {
        var state = new DefaultSubgroupState();

        state.Set(BoardCategory.CustomerOriginal, 0);
        state.Set(BoardCategory.Inbox, 3);

        Assert.AreEqual(0, state.Current(BoardCategory.CustomerOriginal), "重要分类默认独立。");
        Assert.AreEqual(3, state.Current(BoardCategory.Inbox), "待分类默认独立。");

        state.Set(BoardCategory.CustomerOriginal, 2);
        Assert.AreEqual(3, state.Current(BoardCategory.Inbox), "互不影响。");
    }
}
