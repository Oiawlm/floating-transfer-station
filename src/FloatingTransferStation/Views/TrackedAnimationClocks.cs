using System.Windows;
using System.Windows.Media.Animation;

namespace FloatingTransferStation.Views;

/// <summary>
/// 动画时钟登记缝（契约 #24 采样基建修订，Issue #343）：QR 过渡族与遮罩纱
/// FadeAnimation 的动画应用统一经本缝——显式 <see cref="Timeline.CreateClock"/>
/// 后以 AnimationClock 重载应用，并按（目标, 属性）登记最新时钟
/// （SnapshotAndReplace 替换式更新）。WPF 时间线重载 BeginAnimation(dp,
/// timeline) 内部同样是 CreateClock 后应用，显式化生产行为逐位等价（Completed
/// 订阅挂在时间线上、随时钟分发，零变化）；登记只服务测试受控帧源
/// （QrControlledAnimationDriver 冻结＋SeekAlignedToLastTick 步进——动画推进
/// 与采样密度成为构造性质，不依赖宿主性能），无订阅者时登记表零读取，
/// 生产默认真实时钟驱动、动效时长/缓动/编排参数零变化。
/// </summary>
internal static class TrackedAnimationClocks
{
    /// <summary>登记条目：应用目标、动画属性、时钟句柄与时间线 BeginTime
    ///（受控步进的偏移归一：origin=BeginTime 的 seek 偏移＝时间线位置−BeginTime）。</summary>
    internal sealed record TrackedClock(
        IAnimatable Target,
        DependencyProperty Property,
        AnimationClock Clock,
        TimeSpan? BeginTime);

    private static readonly Dictionary<IAnimatable, Dictionary<DependencyProperty, AnimationClock>> ClocksByTarget = [];

    private static event Action<TrackedClock>? ClockBegun;

    /// <summary>应用并登记一条动画（QR 过渡族与 FadeAnimation 统一入口；与
    /// target.BeginAnimation(property, timeline, AnimationHandoffBehavior)
    /// 逐位等价——同 CreateClock 码路径，仅显式保留时钟句柄）。</summary>
    internal static void Begin(IAnimatable target, DependencyProperty property, AnimationTimeline timeline)
    {
        var beginTime = timeline.BeginTime;
        var clock = timeline.CreateClock();
        target.ApplyAnimationClock(property, clock, FadeAnimation.AnimationHandoffBehavior);
        if (!ClocksByTarget.TryGetValue(target, out var byProperty))
        {
            byProperty = [];
            ClocksByTarget.Add(target, byProperty);
        }

        byProperty[property] = clock;
        ClockBegun?.Invoke(new TrackedClock(target, property, clock, beginTime));
    }

    /// <summary>订阅接下来应用的登记时钟（受控帧源在冻结期即时暂停新时钟，
    /// 保持虚拟时间静止）。返回退订句柄。</summary>
    internal static IDisposable Subscribe(Action<TrackedClock> onClockBegun)
    {
        ClockBegun += onClockBegun;
        return new Subscription(() => ClockBegun -= onClockBegun);
    }

    /// <summary>当前登记快照（按目标分桶展开；读取方按目标线程亲和过滤）。</summary>
    internal static IReadOnlyList<TrackedClock> Entries() =>
        ClocksByTarget
            .SelectMany(bucket => bucket.Value.Select(
                pair => new TrackedClock(bucket.Key, pair.Key, pair.Value, BeginTimeOf(pair.Value))))
            .ToList();

    /// <summary>该时钟是否仍是（目标, 属性）的现役登记：SnapshotAndReplace 换手
    /// 后旧时钟不再是现役——对脱离时钟 seek 会重放其已完成的 Completed 订阅
    ///（本机探针实证：卫生回调随即清掉换上手的新时钟），受控帧源必须跳过。</summary>
    internal static bool IsCurrent(IAnimatable target, DependencyProperty property, AnimationClock clock) =>
        ClocksByTarget.TryGetValue(target, out var byProperty) &&
        byProperty.TryGetValue(property, out var current) &&
        ReferenceEquals(current, clock);

    private static TimeSpan? BeginTimeOf(AnimationClock clock) =>
        clock.Timeline is { } timeline ? timeline.BeginTime : null;

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
