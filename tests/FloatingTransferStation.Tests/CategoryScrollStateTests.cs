using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

[TestClass]
[TestCategory("Adversarial")]
public sealed class CategoryScrollStateTests
{
    [TestMethod]
    public void NewState_StartsEveryCategoryAtZero()
    {
        var state = new CategoryScrollState();

        foreach (var category in BoardCategoryCatalog.Ordered)
        {
            Assert.AreEqual(0d, state.GetClamped(category, 100));
        }
    }

    [TestMethod]
    public void Save_KeepsOffsetsIndependentByCategory()
    {
        var state = new CategoryScrollState();
        var expected = new Dictionary<BoardCategory, double>
        {
            [BoardCategory.CustomerOriginal] = 11,
            [BoardCategory.Reference] = 22,
            [BoardCategory.Prompt] = 33,
            [BoardCategory.Inbox] = 44
        };

        foreach (var (category, offset) in expected)
        {
            state.Save(category, offset);
        }

        foreach (var (category, offset) in expected)
        {
            Assert.AreEqual(offset, state.GetClamped(category, 100));
        }
    }

    [TestMethod]
    public void GetClamped_LimitsSavedOffsetToCurrentScrollableHeight()
    {
        var state = new CategoryScrollState();
        state.Save(BoardCategory.Reference, 75);

        Assert.AreEqual(40d, state.GetClamped(BoardCategory.Reference, 40));
        Assert.AreEqual(75d, state.GetClamped(BoardCategory.Reference, 100));
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    [DataRow(-1d)]
    public void Save_NormalizesNonFiniteAndNegativeOffsetsToZero(double offset)
    {
        var state = new CategoryScrollState();

        state.Save(BoardCategory.Prompt, offset);

        Assert.AreEqual(0d, state.GetClamped(BoardCategory.Prompt, 100));
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    [DataRow(0d)]
    [DataRow(-1d)]
    public void GetClamped_ReturnsZeroForNonFiniteOrNonPositiveHeight(double scrollableHeight)
    {
        var state = new CategoryScrollState();
        state.Save(BoardCategory.Inbox, 25);

        Assert.AreEqual(0d, state.GetClamped(BoardCategory.Inbox, scrollableHeight));
    }

    [TestMethod]
    public void Operations_RejectUndefinedCategory()
    {
        var state = new CategoryScrollState();
        var invalid = (BoardCategory)99;

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => state.Save(invalid, 10));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => state.GetClamped(invalid, 100));
    }

    [TestMethod]
    public void NewInstance_DoesNotInheritSavedOffsets()
    {
        var first = new CategoryScrollState();
        first.Save(BoardCategory.CustomerOriginal, 60);

        var second = new CategoryScrollState();

        Assert.AreEqual(60d, first.GetClamped(BoardCategory.CustomerOriginal, 100));
        Assert.AreEqual(0d, second.GetClamped(BoardCategory.CustomerOriginal, 100));
    }
}
