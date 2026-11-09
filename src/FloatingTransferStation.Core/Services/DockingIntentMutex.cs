namespace FloatingTransferStation.Services;

/// <summary>贴边意图互斥组的成员：「保持展开」与「移出屏幕」（#42/#17）＋空态。</summary>
public enum DockingIntent
{
    None,
    PanelHold,
    EdgeHide,
}

/// <summary>
/// 贴边意图互斥（R2，2026-11-08）：「保持展开」（<see cref="PanelStateMachine"/>）与
/// 「移出屏幕」（<see cref="EdgeHideStateMachine"/>）构成互斥组、后激活者胜——激活
/// 任一方即解除另一方，两按钮任何时刻不得同时点亮（视觉态与功能态一致）。互斥是
/// 横切两台状态机的协调事实，塞进任一状态机都越界（PanelStateMachine 不应知贴边、
/// EdgeHideStateMachine 不应知保持），故独立单点持有：两激活入口与全部解除路径
/// （保持释放、再按取消、armed 自动解除、回位恢复）经本类裁决。会话级、不持久化。
/// peek 化（R12）后贴边意图的激活形态改变，但仍属本互斥组——peek 激活同样挤出
/// 保持展开。
/// </summary>
public sealed class DockingIntentMutex
{
    /// <summary>当前持有互斥组的意图；None＝组内无激活方。</summary>
    public DockingIntent Current { get; private set; } = DockingIntent.None;

    /// <summary>
    /// 激活 <paramref name="intent"/> 并返回被其挤出的旧意图（后激活者胜）：原为空态
    /// 或同意图幂等再激活时返回 <see cref="DockingIntent.None"/>（无挤出）。
    /// </summary>
    public DockingIntent Request(DockingIntent intent)
    {
        if (intent == DockingIntent.None)
        {
            throw new ArgumentOutOfRangeException(nameof(intent), "空意图不是可激活成员。");
        }

        var displaced = Current == intent ? DockingIntent.None : Current;
        Current = intent;
        return displaced;
    }

    /// <summary>
    /// 条件解除：仅当当前持有者恰为 <paramref name="intent"/> 时回到 None——被挤出
    /// 意图的解除路径晚到时（如互斥解除与自身释放竞态）不得误清新持有者的意图。
    /// </summary>
    public void Clear(DockingIntent intent)
    {
        if (intent == DockingIntent.None)
        {
            throw new ArgumentOutOfRangeException(nameof(intent), "空意图不是可解除成员。");
        }

        if (Current == intent)
        {
            Current = DockingIntent.None;
        }
    }
}
