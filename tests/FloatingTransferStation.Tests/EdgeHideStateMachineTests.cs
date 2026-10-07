using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 一次性贴边隐藏状态机契约（1.20.0）：Idle ⇄ Armed（按钮切换语义，再按取消）→
/// (指针离开且无抑制) → Docked → (回位/热键/显示器变化) → Idle。一次性：授权被
/// 消费后必须重新武装；抑制条件复用 PanelStateMachine.WouldCollapse 语义由调用方
/// 求值传入，不在状态机内另立一套。会话级、不持久化。
/// </summary>
[TestClass]
public sealed class EdgeHideStateMachineTests
{
    [TestMethod]
    public void ToggleArm_FromIdleEntersArmedAndReturnsTrue()
    {
        var state = new EdgeHideStateMachine();

        Assert.IsTrue(state.ToggleArm());
        Assert.IsTrue(state.IsArmed);
        Assert.IsFalse(state.IsDocked);
        Assert.AreEqual(EdgeHidePhase.Armed, state.Phase);
    }

    [TestMethod]
    public void ToggleArm_FromArmedCancelsToIdleAndReturnsFalse()
    {
        var state = new EdgeHideStateMachine();
        state.ToggleArm();

        Assert.IsFalse(state.ToggleArm());
        Assert.IsFalse(state.IsArmed);
        Assert.AreEqual(EdgeHidePhase.Idle, state.Phase);
    }

    [TestMethod]
    public void ToggleArm_FromDockedIsDefensiveNoOp()
    {
        var state = new EdgeHideStateMachine();
        state.ToggleArm();
        Assert.IsTrue(state.TryDock(collapseWouldCommit: true));

        // Docked 态窗口已离屏、按钮不可达；万一到达也不得把离屏误当取消。
        Assert.IsFalse(state.ToggleArm());
        Assert.IsTrue(state.IsDocked);
    }

    [TestMethod]
    public void TryDock_RequiresArmed()
    {
        var state = new EdgeHideStateMachine();

        Assert.IsFalse(state.TryDock(collapseWouldCommit: true));
        Assert.AreEqual(EdgeHidePhase.Idle, state.Phase);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void TryDock_RespectsCollapseSuppressionAndStaysArmed()
    {
        var state = new EdgeHideStateMachine();
        state.ToggleArm();

        // 抑制复用：指针在内/拖拽/文本编辑/PanelHold 任一成立（collapseWouldCommit=false）
        // 都不得隐藏；授权保留，等待下次离开重试。
        Assert.IsFalse(state.TryDock(collapseWouldCommit: false));
        Assert.IsTrue(state.IsArmed);

        Assert.IsTrue(state.TryDock(collapseWouldCommit: true));
        Assert.IsTrue(state.IsDocked);
    }

    [TestMethod]
    public void TryDock_ConsumesArm_OneShotSemantics()
    {
        var state = new EdgeHideStateMachine();
        state.ToggleArm();
        Assert.IsTrue(state.TryDock(collapseWouldCommit: true));

        // 一次性：回位解除后不重新武装不得再次隐藏。
        state.Reset();
        Assert.IsFalse(state.TryDock(collapseWouldCommit: true));
        Assert.AreEqual(EdgeHidePhase.Idle, state.Phase);
    }

    [TestMethod]
    public void Reset_FromArmedClearsPendingAuthorization()
    {
        var state = new EdgeHideStateMachine();
        state.ToggleArm();

        // 其他收起路径解除：面板经外部路径离开展开态时不留悬置 armed。
        state.Reset();

        Assert.IsFalse(state.IsArmed);
        Assert.IsFalse(state.TryDock(collapseWouldCommit: true));
    }

    [TestMethod]
    public void Reset_FromDockedDisarmsAfterRecall()
    {
        var state = new EdgeHideStateMachine();
        state.ToggleArm();
        state.TryDock(collapseWouldCommit: true);

        // Docked → 回位/热键/显示器变化 → 解除；再次使用需再按一次。
        state.Reset();

        Assert.IsFalse(state.IsDocked);
        Assert.IsFalse(state.IsArmed);
        Assert.IsTrue(state.ToggleArm(), "回位后可重新武装。");
    }

    [TestMethod]
    public void Reset_FromIdleIsIdempotent()
    {
        var state = new EdgeHideStateMachine();

        state.Reset();

        Assert.AreEqual(EdgeHidePhase.Idle, state.Phase);
    }
}
