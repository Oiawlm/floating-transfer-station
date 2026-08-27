using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class BatchDragPlannerTests
{
    [TestMethod]
    public void Create_SelectedOriginReturnsAllSelectedInSourceOrder()
    {
        var items = Items("top", "middle", "bottom");

        var plan = BatchDragPlanner.Create(
            items,
            [items[2].Id, items[0].Id],
            items[2].Id);

        CollectionAssert.AreEqual(
            new[] { items[0].Id, items[2].Id },
            plan.Items.Select(item => item.Id).ToArray());
        Assert.IsFalse(plan.ClearExistingSelection);
    }

    [TestMethod]
    public void Create_UnselectedOriginReturnsOnlyOriginAndClearsOldSelection()
    {
        var items = Items("top", "middle", "bottom");

        var plan = BatchDragPlanner.Create(
            items,
            [items[0].Id, items[2].Id],
            items[1].Id);

        CollectionAssert.AreEqual(
            new[] { items[1].Id },
            plan.Items.Select(item => item.Id).ToArray());
        Assert.IsTrue(plan.ClearExistingSelection);
    }

    [TestMethod]
    public void Create_UnselectedOriginWithoutOldSelectionDoesNotRequestClear()
    {
        var items = Items("top", "bottom");

        var plan = BatchDragPlanner.Create(items, [], items[0].Id);

        CollectionAssert.AreEqual(
            new[] { items[0].Id },
            plan.Items.Select(item => item.Id).ToArray());
        Assert.IsFalse(plan.ClearExistingSelection);
    }

    [TestMethod]
    public void Create_MissingOriginIsRejected()
    {
        var items = Items("top");

        Assert.ThrowsExactly<KeyNotFoundException>(() =>
            BatchDragPlanner.Create(items, [], Guid.NewGuid()));
    }

    private static BoardItem[] Items(params string[] labels) =>
        labels.Select((label, index) => BoardItem.CreateText(
            label,
            Guid.Parse($"00000000-0000-0000-0000-{index + 1:000000000000}"),
            DateTimeOffset.UnixEpoch.AddSeconds(index))).ToArray();
}
