namespace FloatingTransferStation.Services;

/// <summary>一次性贴边隐藏的相位：待命（已按按钮）→ 已移出屏幕 → 回位解除。</summary>
public enum EdgeHidePhase
{
    Idle,
    Armed,
    Docked,
}

/// <summary>
/// 一次性贴边隐藏状态机（会话级、不持久化）。与 <see cref="PanelStateMachine"/> 分工：
/// 后者掌管面板展开/收起，本类只掌管「武装 → 移出屏幕 → 回位解除」的独立相位链；
/// 抑制条件不在此另立一套，由调用方以 PanelStateMachine.WouldCollapse 求值后经
/// TryDock 的 collapseWouldCommit 传入（指针在内/拖拽/文本编辑/PanelHold
/// 同样抑制贴边隐藏）。一次性语义：授权被消费（进入 Docked）或被取消后即失效，
/// 再次使用必须重新武装。
/// </summary>
public sealed class EdgeHideStateMachine
{
    private EdgeHidePhase _phase = EdgeHidePhase.Idle;

    public EdgeHidePhase Phase => _phase;
    public bool IsArmed => _phase == EdgeHidePhase.Armed;
    public bool IsDocked => _phase == EdgeHidePhase.Docked;

    /// <summary>
    /// 头部按钮切换语义：Idle→Armed 返回 true（武装）；Armed→Idle 返回 false
    /// （再按一次取消）。Docked 恒返回 false 且不变相位——窗口已离屏、按钮不可达，
    /// 防御性 no-op，避免离屏态被误当取消。
    /// </summary>
    public bool ToggleArm()
    {
        switch (_phase)
        {
            case EdgeHidePhase.Idle:
                _phase = EdgeHidePhase.Armed;
                return true;
            case EdgeHidePhase.Armed:
                _phase = EdgeHidePhase.Idle;
                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// 尝试提交隐藏：仅 Armed 且收起条件成立（复用 WouldCollapse 语义）时迁移
    /// Docked。被抑制时保持 Armed 返回 false，授权留待下次离开重试；授权在成功
    /// 迁移时被消费，一次性。
    /// </summary>
    public bool TryDock(bool collapseWouldCommit)
    {
        if (_phase != EdgeHidePhase.Armed || !collapseWouldCommit)
        {
            return false;
        }

        _phase = EdgeHidePhase.Docked;
        return true;
    }

    /// <summary>
    /// 回到 Idle 的唯一出口：再按取消、面板经其他路径离开展开态的自动解除、
    /// Docked 回位/热键/显示器变化恢复共用，保证 armed 生命周期不悬置。
    /// </summary>
    public void Reset() => _phase = EdgeHidePhase.Idle;
}
