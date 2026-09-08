using FloatingTransferStation.Mac;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FloatingTransferStation.Mac.Tests;

[TestClass]
public sealed class SelectionStateTests
{
    [TestMethod]
    public void DeselectingAnchorMakesNextRangeStartAtTarget()
    {
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        var state = new SelectionState();
        state.Select(ids[0], ids, true, false);
        state.Select(ids[0], ids, true, false);
        state.Select(ids[2], ids, false, true);
        CollectionAssert.AreEquivalent(new[] { ids[2] }, state.Ids.ToArray());
    }

    [TestMethod]
    public void SelectAllClearsPreviousRangeAnchor()
    {
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        var state = new SelectionState();
        state.Select(ids[0], ids, true, false);
        state.SelectAll(ids);
        state.Select(ids[2], ids, false, true);
        CollectionAssert.AreEquivalent(new[] { ids[2] }, state.Ids.ToArray());
    }

    [TestMethod]
    public void RangeKeepsAnchorWhenEndpointChanges()
    {
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var state = new SelectionState();
        state.Select(ids[1], ids, true, false);
        state.Select(ids[4], ids, false, true);
        state.Select(ids[2], ids, false, true);
        CollectionAssert.AreEquivalent(ids[1..3], state.Ids.ToArray());
    }

    [TestMethod]
    public void AdditiveRangeKeepsExistingSelectionAndPrunesDeletedAnchor()
    {
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var state = new SelectionState();
        state.Select(ids[0], ids, true, false);
        state.Select(ids[2], ids, true, false);
        state.Select(ids[4], ids, true, true);
        CollectionAssert.AreEquivalent(new[] { ids[0], ids[2], ids[3], ids[4] }, state.Ids.ToArray());
        state.Prune(ids[3..]);
        state.Select(ids[3], ids[3..], false, true);
        CollectionAssert.AreEquivalent(new[] { ids[3] }, state.Ids.ToArray());
    }
}
