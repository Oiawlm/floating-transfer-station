using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class DropInsertionCalculatorTests
{
    [TestMethod]
    [DataRow(4, 0, 100, 4)]
    [DataRow(4, 49.9, 100, 4)]
    [DataRow(4, 50, 100, 5)]
    [DataRow(4, 100, 100, 5)]
    [DataRow(4, 10, 0, 4)]
    public void ForTarget_UsesUpperAndLowerHalvesAsInsertionGaps(
        int targetIndex,
        double pointerY,
        double targetHeight,
        int expected)
    {
        var insertionIndex = DropInsertionCalculator.ForTarget(
            targetIndex,
            pointerY,
            targetHeight);

        Assert.AreEqual(expected, insertionIndex);
    }

    [TestMethod]
    public void ForEmptySpace_AppendsAfterLastItem()
    {
        Assert.AreEqual(7, DropInsertionCalculator.ForEmptySpace(7));
    }
}
