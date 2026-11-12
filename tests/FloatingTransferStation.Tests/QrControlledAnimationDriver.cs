using System.Windows.Media.Animation;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// QR 过渡族受控动画帧源（契约 #24 采样基建修订，Issue #343）：经
/// TrackedAnimationClocks 登记表接管 QR 编排全部 AnimationClock——Freeze 冻结
/// 真实时钟（暂停全部活跃时钟；过渡触发与冻结同处一个调度器回合，渲染帧无
/// 从推进时钟，冻结恒落在动画起点），StepTo/StepThrough 以
/// SeekAlignedToLastTick 同步步进全部时钟并逐帧采样（动画属性值随 seek 立即
/// 同步更新——ClockController 官方语义，本机探针实证：新应用未首 tick 的
/// Stopped 时钟可 pause/seek 且定位精确、被替换的脱离时钟 seek 无副作用、
/// Filling 可向后 scrub）；冻结期新起的时钟（关中途再开/反向收回）在登记回调
/// 内即时暂停、epoch 记当下虚拟时刻。采样密度＝窗长/步长的构造性质，不再依赖
/// 宿主机性能（ManualPlacementAnimationClock 手动帧时钟同族先例：生产默认真实
/// 时钟，测试注入受控帧源）；曲线形状断言（时间轴对齐窗/无晚爆发/交接时机）
/// 原样复用，唯一变化是取值手段从环境依赖改为受控。
/// </summary>
internal sealed class QrControlledAnimationDriver : IDisposable
{
    /// <summary>受控采样步长（ms）：QR 采样族统一帧距——与遮罩纱速率断言的
    /// 10ms 归一单位一致；250ms 收回窗 25 帧、450ms 入场窗 45 帧，既有样本
    /// 数门槛（≥8/≥3/≥2）全部保有余量。</summary>
    public const int StepMs = 10;

    private readonly List<Entry> _entries = [];
    private readonly HashSet<AnimationClock> _captured = [];
    private readonly IDisposable _subscription;
    private bool _frozen;
    private bool _released;

    private sealed record Entry(
        IAnimatable Target,
        System.Windows.DependencyProperty Property,
        AnimationClock Clock,
        TimeSpan? BeginTime,
        long EpochMs);

    private QrControlledAnimationDriver() => _subscription = TrackedAnimationClocks.Subscribe(OnClockBegun);

    /// <summary>接管接下来的 QR 过渡（在首个过渡触发前 Attach）。</summary>
    public static QrControlledAnimationDriver Attach() => new();

    /// <summary>当下虚拟时刻（ms；冻结后只由 Step* 推进）。</summary>
    public long NowMs { get; private set; }

    private void OnClockBegun(TrackedAnimationClocks.TrackedClock tracked)
    {
        if (_released || !IsAffineToCurrentThread(tracked) || !_captured.Add(tracked.Clock))
        {
            return;
        }

        // 无条件捕获（新应用未首 tick 的时钟 state=Stopped，同样即时暂停——
        // 不暂停会被渲染线程随时激活，虚拟起点即失准）。
        tracked.Clock.Controller.Pause();
        _entries.Add(new Entry(tracked.Target, tracked.Property, tracked.Clock, tracked.BeginTime, NowMs));
    }

    /// <summary>冻结真实时钟：暂停当前全部登记时钟并接管（此后虚拟时间静止，
    /// 由 Step* 推进）。过渡触发（RaiseEvent）返回后立即调用。</summary>
    public void Freeze()
    {
        if (_frozen)
        {
            return;
        }

        _frozen = true;
        foreach (var tracked in TrackedAnimationClocks.Entries())
        {
            if (!IsAffineToCurrentThread(tracked) || !_captured.Add(tracked.Clock))
            {
                continue;
            }

            tracked.Clock.Controller.Pause();
            _entries.Add(new Entry(tracked.Target, tracked.Property, tracked.Clock, tracked.BeginTime, NowMs));
        }
    }

    /// <summary>登记目标是否归属当前线程：静态登记表跨测试累计，MSTest 每个
    /// 测试独立 STA 线程——他线程（前测窗口）的时钟 seek 会跨线程写属性
    ///（VerifyAccess 抛异常），一律不接管（留在原线程自生自灭，其元素随前测
    /// 窗口关闭而无断言面）。</summary>
    private static bool IsAffineToCurrentThread(TrackedAnimationClocks.TrackedClock tracked) =>
        tracked.Target is System.Windows.Threading.DispatcherObject dispatcherObject &&
        dispatcherObject.CheckAccess();

    /// <summary>推进到绝对虚拟时刻 timeMs（不采样）：驱动 Completed 回调与
    /// 终态卫生的确定性收尾（越过后可 Release 交还真实时钟）。</summary>
    public void StepTo(long timeMs) => SeekAll(timeMs);

    /// <summary>从当下虚拟时刻步进到 endMs（不含起点）：每步先把全部受控时钟
    /// seek 到目标时刻（属性值同步更新），再回调采样。</summary>
    public void StepThrough(long endMs, Action<long> onFrame)
    {
        for (var time = NowMs + StepMs; time <= endMs; time += StepMs)
        {
            SeekAll(time);
            onFrame(time);
        }
    }

    /// <summary>交还真实时钟：恢复（仍暂停的）受控时钟自然走完，供终态段
    /// PumpUntil 收尾；可重复调用，Dispose 隐含。</summary>
    public void Release()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        foreach (var entry in _entries)
        {
            if (entry.Clock.CurrentState != ClockState.Stopped)
            {
                entry.Clock.Controller.Resume();
            }
        }
    }

    public void Dispose()
    {
        Release();
        _subscription.Dispose();
    }

    private void SeekAll(long timeMs)
    {
        foreach (var entry in _entries)
        {
            // 现役过滤：SnapshotAndReplace 换手后的脱离时钟不得 seek——seek 会
            // 重放其已完成的 Completed 订阅（卫生回调清动画即误杀换上手的新
            // 时钟，本机探针实证）。origin=BeginTime 的 seek 偏移＝时间线位置−
            // BeginTime（根时钟 seek 直接映射进动画时长、时间线 BeginTime 被
            // 绕过——探针实证）；偏移为负＝延迟动画尚未到 BeginTime——跳过
            //（时钟停在起点前不产出、属性落基值；负偏移 seek 抛异常）。
            if (!TrackedAnimationClocks.IsCurrent(entry.Target, entry.Property, entry.Clock))
            {
                continue;
            }

            var beginMs = entry.BeginTime?.TotalMilliseconds ?? 0d;
            var offsetMs = (timeMs - entry.EpochMs) - beginMs;
            if (offsetMs < 0d)
            {
                continue;
            }

            entry.Clock.Controller.SeekAlignedToLastTick(
                TimeSpan.FromMilliseconds(offsetMs),
                TimeSeekOrigin.BeginTime);
        }

        NowMs = timeMs;
    }
}
