using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class ScreenEdgeGeometryTests
{
    private static readonly MonitorBounds PrimaryMonitor = new(0, 0, 1920, 1040);
    private static readonly MonitorBounds PrimaryWorkAreaNoTaskbarEdge = new(0, 0, 1920, 1040);
    private static readonly MonitorBounds PrimaryWorkAreaWithRightTaskbar = new(0, 0, 1896, 1040);
    private static readonly MonitorBounds PrimaryWorkAreaWithBottomTaskbar = new(0, 0, 1920, 1000);

    [TestMethod]
    public void RightEdgeBleed_IsAllowedWhenTheWorkAreaTouchesTheMonitorEdgeWithoutANeighbor()
    {
        Assert.AreEqual(
            WindowSettings.EdgeBleed,
            ScreenEdgeGeometry.RightEdgeBleedFor(
                PrimaryMonitor,
                PrimaryWorkAreaNoTaskbarEdge,
                [PrimaryMonitor]));
    }

    [TestMethod]
    public void RightEdgeBleed_FallsBackToZeroWhenTheTaskbarOccupiesTheRightEdge()
    {
        Assert.AreEqual(
            0d,
            ScreenEdgeGeometry.RightEdgeBleedFor(
                PrimaryMonitor,
                PrimaryWorkAreaWithRightTaskbar,
                [PrimaryMonitor]));
    }

    [TestMethod]
    public void RightEdgeBleed_IsAllowedWhenTheTaskbarOnlyOccupiesOtherEdges()
    {
        Assert.AreEqual(
            WindowSettings.EdgeBleed,
            ScreenEdgeGeometry.RightEdgeBleedFor(
                PrimaryMonitor,
                PrimaryWorkAreaWithBottomTaskbar,
                [PrimaryMonitor]));
    }

    [TestMethod]
    public void RightEdgeBleed_FallsBackToZeroWhenAMonitorAbutsTheRightEdge()
    {
        var adjacentRightMonitor = new MonitorBounds(1920, 0, 3840, 1040);

        Assert.AreEqual(
            0d,
            ScreenEdgeGeometry.RightEdgeBleedFor(
                PrimaryMonitor,
                PrimaryWorkAreaNoTaskbarEdge,
                [PrimaryMonitor, adjacentRightMonitor]));
    }

    [TestMethod]
    public void RightEdgeBleed_IgnoresMonitorsThatDoNotTouchTheRightEdge()
    {
        var detachedRightMonitor = new MonitorBounds(1960, 0, 3880, 1040);
        var leftNeighborMonitor = new MonitorBounds(-1920, 0, 0, 1040);

        Assert.AreEqual(
            WindowSettings.EdgeBleed,
            ScreenEdgeGeometry.RightEdgeBleedFor(
                PrimaryMonitor,
                PrimaryWorkAreaNoTaskbarEdge,
                [leftNeighborMonitor, PrimaryMonitor, detachedRightMonitor]));
    }

    [TestMethod]
    public void RightEdgeBleed_IgnoresNeighborsStartingBeyondTheRightEdge()
    {
        var almostAdjacentMonitor = new MonitorBounds(1921, 0, 3841, 1040);

        Assert.AreEqual(
            WindowSettings.EdgeBleed,
            ScreenEdgeGeometry.RightEdgeBleedFor(
                PrimaryMonitor,
                PrimaryWorkAreaNoTaskbarEdge,
                [PrimaryMonitor, almostAdjacentMonitor]));
    }
}
