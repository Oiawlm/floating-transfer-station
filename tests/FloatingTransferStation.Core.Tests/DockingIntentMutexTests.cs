using FloatingTransferStation.Services;

namespace FloatingTransferStation.Core.Tests;

/// <summary>
/// 贴边意图互斥（R2，契约 #42/#17 增补）：「保持展开」与「移出屏幕」构成互斥组、
/// 后激活者胜——挤出矩阵、同意图幂等、Clear 条件清除（晚到的被挤出者解除不误删
/// 新持有者）与 None 初始态。
/// </summary>
[TestClass]
public sealed class DockingIntentMutexTests
{
    [TestMethod]
    public void Initial_CurrentIsNone()
    {
        var mutex = new DockingIntentMutex();

        Assert.AreEqual(DockingIntent.None, mutex.Current, "初始无任何激活意图。");
    }

    [TestMethod]
    public void Request_FromNoneActivatesWithoutDisplacement()
    {
        var mutex = new DockingIntentMutex();

        Assert.AreEqual(DockingIntent.None, mutex.Request(DockingIntent.PanelHold), "空态激活无挤出。");
        Assert.AreEqual(DockingIntent.PanelHold, mutex.Current);

        var other = new DockingIntentMutex();
        Assert.AreEqual(DockingIntent.None, other.Request(DockingIntent.EdgeHide), "空态激活无挤出。");
        Assert.AreEqual(DockingIntent.EdgeHide, other.Current);
    }

    [TestMethod]
    public void Request_DisplacesTheOtherMemberLastActivationWins()
    {
        var holdThenEdge = new DockingIntentMutex();
        holdThenEdge.Request(DockingIntent.PanelHold);

        Assert.AreEqual(
            DockingIntent.PanelHold,
            holdThenEdge.Request(DockingIntent.EdgeHide),
            "激活贴边必须挤出保持展开。");
        Assert.AreEqual(DockingIntent.EdgeHide, holdThenEdge.Current);

        var edgeThenHold = new DockingIntentMutex();
        edgeThenHold.Request(DockingIntent.EdgeHide);

        Assert.AreEqual(
            DockingIntent.EdgeHide,
            edgeThenHold.Request(DockingIntent.PanelHold),
            "激活保持展开必须挤出贴边。");
        Assert.AreEqual(DockingIntent.PanelHold, edgeThenHold.Current);
    }

    [TestMethod]
    public void Request_SameIntentIsIdempotentAndKeepsHolder()
    {
        var mutex = new DockingIntentMutex();
        mutex.Request(DockingIntent.PanelHold);

        Assert.AreEqual(
            DockingIntent.None,
            mutex.Request(DockingIntent.PanelHold),
            "同意图再激活幂等，无挤出。");
        Assert.AreEqual(DockingIntent.PanelHold, mutex.Current);

        mutex.Request(DockingIntent.EdgeHide);
        Assert.AreEqual(
            DockingIntent.None,
            mutex.Request(DockingIntent.EdgeHide),
            "同意图再激活幂等，无挤出。");
        Assert.AreEqual(DockingIntent.EdgeHide, mutex.Current);
    }

    [TestMethod]
    public void Clear_OnlyReleasesWhenCallerIsCurrentHolder()
    {
        var mutex = new DockingIntentMutex();

        // 无持有者时解除是 no-op，不抛异常。
        mutex.Clear(DockingIntent.PanelHold);
        Assert.AreEqual(DockingIntent.None, mutex.Current);

        mutex.Request(DockingIntent.PanelHold);
        mutex.Clear(DockingIntent.PanelHold);
        Assert.AreEqual(DockingIntent.None, mutex.Current, "持有者解除回到空态。");

        // 解除后可重新激活。
        mutex.Request(DockingIntent.EdgeHide);
        Assert.AreEqual(DockingIntent.EdgeHide, mutex.Current);
    }

    [TestMethod]
    public void Clear_StaleReleaseOfDisplacedIntentDoesNotClobberNewHolder()
    {
        var mutex = new DockingIntentMutex();
        mutex.Request(DockingIntent.PanelHold);
        Assert.AreEqual(DockingIntent.PanelHold, mutex.Request(DockingIntent.EdgeHide));

        // 被挤出的保持展开的解除路径晚到：不得误删新持有者（贴边）。
        mutex.Clear(DockingIntent.PanelHold);
        Assert.AreEqual(DockingIntent.EdgeHide, mutex.Current, "晚到的旧意图解除不影响新持有者。");

        mutex.Clear(DockingIntent.EdgeHide);
        Assert.AreEqual(DockingIntent.None, mutex.Current, "新持有者自身解除才回到空态。");
    }

    [TestMethod]
    public void Request_And_Clear_RejectNoneAsAMember()
    {
        var mutex = new DockingIntentMutex();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => mutex.Request(DockingIntent.None),
            "空意图不是可激活成员。");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => mutex.Clear(DockingIntent.None),
            "空意图不是可解除成员。");
    }
}
