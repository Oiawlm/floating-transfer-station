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
        Assert.IsTrue(state.Set(BoardCategory.Reference));
        Assert.AreEqual(BoardCategory.Reference, state.Current);
        Assert.IsFalse(state.Set(BoardCategory.Reference));
        Assert.AreEqual(BoardCategory.Reference, state.Current);
    }

    [TestMethod]
    public void Set_RejectsUndefinedCategoryWithoutChangingCurrentValue()
    {
        var state = new DefaultCaptureCategoryState();
        state.Set(BoardCategory.Prompt);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => state.Set((BoardCategory)99));
        Assert.AreEqual(BoardCategory.Prompt, state.Current);
    }
}
