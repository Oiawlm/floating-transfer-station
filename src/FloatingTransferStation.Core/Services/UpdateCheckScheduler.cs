namespace FloatingTransferStation.Services;

/// <summary>
/// 更新检查的统一调度状态（契约 #26 2026-11-03 修订，Issue #275）：30 秒首检、
/// 24 小时周期与 2 小时交互空闲三触发收敛为同一「下次检查到期」状态——不设平行
/// 定时器，到期只取各触发原因的最早者；任何一次已派发的检查都把周期推进一个
/// 周期并消费当前空闲段的空闲触发臂（同一空闲段至多触发一次检查，三触发不会
/// 互相踩出重复检查；用户交互刷新「最后交互时间」并重挂空闲臂）。时钟经构造
/// 注入（离线可测）；本类型只算「何时到期」，不做检查本身——检查执行与单飞
/// 守卫在窗口侧（<see cref="IUpdateService"/> 负责检查与安装原语）。
/// </summary>
public sealed class UpdateCheckScheduler
{
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _firstCheckDelay;
    private readonly TimeSpan _periodicInterval;
    private readonly TimeSpan _idleInterval;

    private DateTimeOffset? _activatedAt;
    private DateTimeOffset _lastInteractionAt;
    private DateTimeOffset _nextPeriodicCheckAt;
    private bool _idleCheckArmed = true;

    public UpdateCheckScheduler(
        TimeSpan firstCheckDelay,
        TimeSpan periodicInterval,
        TimeSpan idleInterval,
        Func<DateTimeOffset>? clock = null)
    {
        _firstCheckDelay = firstCheckDelay;
        _periodicInterval = periodicInterval;
        _idleInterval = idleInterval;
        _clock = clock ?? (static () => DateTimeOffset.Now);
    }

    /// <summary>调度激活（偏好开启时的唯一入口；幂等）：交互与空闲段基准=激活时刻，
    /// 首检到期=激活 + 首检延迟（30 秒首检语义的唯一承载点）。</summary>
    public void Activate()
    {
        if (_activatedAt is not null)
        {
            return;
        }

        var now = _clock();
        _activatedAt = now;
        _lastInteractionAt = now;
        _nextPeriodicCheckAt = now + _firstCheckDelay;
        _idleCheckArmed = true;
    }

    /// <summary>用户交互记账：刷新「最后交互时间」并重挂空闲触发臂——新空闲段
    /// 从本次交互重新计满空闲阈值；偏好关闭期间照常记账（重开时基准新鲜）。</summary>
    public void NoteUserInteraction()
    {
        _lastInteractionAt = _clock();
        _idleCheckArmed = true;
    }

    /// <summary>一次检查已派发：周期到期推进一个周期；本空闲段的空闲触发臂即被
    /// 消费（任何触发原因的检查都算「这个空闲段已经查过」，交互后才重新武装）。</summary>
    public void NoteCheckDispatched()
    {
        var now = _clock();
        _nextPeriodicCheckAt = now + _periodicInterval;
        _idleCheckArmed = false;
    }

    /// <summary>下次检查到期时刻＝周期到期与空闲到期（若武装）的最早者；
    /// 未激活时无到期（调度尚未启动）。</summary>
    public DateTimeOffset? NextDueAt() =>
        _activatedAt is null
            ? null
            : _idleCheckArmed && _lastInteractionAt + _idleInterval < _nextPeriodicCheckAt
                ? _lastInteractionAt + _idleInterval
                : _nextPeriodicCheckAt;

    public bool IsCheckDue(DateTimeOffset now) => NextDueAt() is { } due && due <= now;

    /// <summary>到下一次检查的定时器间隔：整秒向上取整（调度重排不损失毫秒进度），
    /// 已过期的到期按下限 1 秒立即重评（DispatcherTimer 间隔必须为正）。</summary>
    public TimeSpan NextDueDelay(DateTimeOffset now)
    {
        var due = NextDueAt() ?? throw new InvalidOperationException("更新调度未激活，没有下次检查到期。");
        var delay = due - now;
        return delay <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(1)
            : TimeSpan.FromSeconds(Math.Ceiling(delay.TotalSeconds));
    }
}
