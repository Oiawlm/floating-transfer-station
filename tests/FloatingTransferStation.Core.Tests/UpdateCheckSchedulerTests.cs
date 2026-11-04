using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// UpdateCheckScheduler 离线锁定测试（契约 #26 2026-11-03 修订，Issue #275）：
/// 30 秒首检、24 小时周期、2 小时交互空闲三触发收敛为同一「下次检查到期」状态——
/// ① 首检到期=激活+30 秒；② 已派发检查把周期推进 24 小时并消费本空闲段的空闲臂
///（同一空闲段至多一次，三触发不重复检查）；③ 用户交互刷新「最后交互时间」并
/// 重挂空闲臂；④ 到期只取各触发原因最早者；⑤ 间隔整秒向上取整、过期到期按
/// 1 秒下限立即重评；⑥ 未激活时无到期。时钟注入，全程离线。
/// </summary>
[TestClass]
public sealed class UpdateCheckSchedulerTests
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PeriodicInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromHours(2);

    private static (UpdateCheckScheduler Scheduler, MutableClock Clock) Create()
    {
        var clock = new MutableClock();
        return (new UpdateCheckScheduler(FirstCheckDelay, PeriodicInterval, IdleInterval, () => clock.Now), clock);
    }

    private sealed class MutableClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);
    }

    [TestMethod]
    public void BeforeActivation_HasNoDueAndDelayThrows()
    {
        var (scheduler, clock) = Create();
        Assert.IsNull(scheduler.NextDueAt(), "未激活时无下次检查到期（调度尚未启动）。");
        Assert.IsFalse(scheduler.IsCheckDue(clock.Now));
        try
        {
            _ = scheduler.NextDueDelay(clock.Now);
            Assert.Fail("未激活时取定时器间隔必须抛 InvalidOperationException（接线错误就地暴露）。");
        }
        catch (InvalidOperationException)
        {
        }
    }

    [TestMethod]
    public void Activation_SchedulesFirstCheckAtThirtySeconds()
    {
        var (scheduler, clock) = Create();
        scheduler.Activate();
        Assert.AreEqual(
            clock.Now + FirstCheckDelay,
            scheduler.NextDueAt(),
            "首检到期=激活时刻+30 秒。");
        Assert.AreEqual(
            FirstCheckDelay,
            scheduler.NextDueDelay(clock.Now),
            "激活与首次重排同帧：首检间隔恰为 30 秒（无毫秒损耗）。");
        Assert.IsFalse(scheduler.IsCheckDue(clock.Now), "激活时刻本身未到期。");
    }

    [TestMethod]
    public void Activation_IsIdempotent_FirstCheckDueKept()
    {
        var (scheduler, clock) = Create();
        scheduler.Activate();
        clock.Now += TimeSpan.FromMinutes(5);
        scheduler.Activate();
        Assert.AreEqual(
            clock.Now - TimeSpan.FromMinutes(5) + FirstCheckDelay,
            scheduler.NextDueAt(),
            "重复激活幂等：首检到期不被第二次激活重置。");
    }

    [TestMethod]
    public void DispatchedCheck_AdvancesPeriodicByIntervalAndConsumesIdleArm()
    {
        var (scheduler, clock) = Create();
        scheduler.Activate();
        clock.Now += FirstCheckDelay;
        scheduler.NoteCheckDispatched();
        Assert.AreEqual(
            clock.Now + PeriodicInterval,
            scheduler.NextDueAt(),
            "派发后周期到期=派发时刻+24 小时（空闲臂被消费，不再有更早的空闲到期）。");
        Assert.IsFalse(scheduler.IsCheckDue(clock.Now), "派发刚发生，不得立即再次到期。");
    }

    [TestMethod]
    public void UserInteraction_RearmsIdleEpisode_FromInteractionMoment()
    {
        var (scheduler, clock) = Create();
        scheduler.Activate();
        clock.Now += FirstCheckDelay;
        scheduler.NoteCheckDispatched();

        clock.Now += TimeSpan.FromMinutes(30);
        scheduler.NoteUserInteraction();
        Assert.AreEqual(
            clock.Now + IdleInterval,
            scheduler.NextDueAt(),
            "交互重挂空闲臂：空闲到期=交互时刻+2 小时（早于 24 小时周期）。");
        Assert.IsFalse(scheduler.IsCheckDue(clock.Now + IdleInterval - TimeSpan.FromSeconds(1)));
        Assert.IsTrue(scheduler.IsCheckDue(clock.Now + IdleInterval), "空闲阈值计满即到期。");
    }

    [TestMethod]
    public void IdleEpisode_TriggersAtMostOneCheck_UntilNextInteraction()
    {
        var (scheduler, clock) = Create();
        scheduler.Activate();
        scheduler.NoteUserInteraction();
        clock.Now += IdleInterval + TimeSpan.FromHours(1);
        Assert.IsTrue(scheduler.IsCheckDue(clock.Now), "空闲 ≥2 小时到期。");
        scheduler.NoteCheckDispatched();
        Assert.IsFalse(scheduler.IsCheckDue(clock.Now), "本空闲段已检查：空闲臂被消费。");

        clock.Now += TimeSpan.FromHours(5);
        Assert.IsFalse(
            scheduler.IsCheckDue(clock.Now),
            "持续空闲不再重复触发（下次到期是 24 小时周期）。");
        Assert.AreEqual(
            clock.Now - TimeSpan.FromHours(5) + PeriodicInterval,
            scheduler.NextDueAt(),
            "下次到期=派发时刻+24 小时（空闲段检查也推进周期）。");
    }

    [TestMethod]
    public void NextDue_IsEarliestOfPeriodicAndIdle()
    {
        var (scheduler, clock) = Create();
        scheduler.Activate();
        clock.Now += FirstCheckDelay;
        scheduler.NoteCheckDispatched();

        // 周期还远（约 24 小时后），交互后空闲到期更早 → 取空闲。
        clock.Now += TimeSpan.FromHours(1);
        scheduler.NoteUserInteraction();
        Assert.AreEqual(clock.Now + IdleInterval, scheduler.NextDueAt());

        // 交互时刻逼近周期到期（空闲到期晚于周期）→ 取周期。
        clock.Now += TimeSpan.FromHours(21) + TimeSpan.FromMinutes(50);
        scheduler.NoteUserInteraction();
        Assert.AreEqual(
            clock.Now + PeriodicInterval - (TimeSpan.FromHours(22) + TimeSpan.FromMinutes(50)),
            scheduler.NextDueAt(),
            "空闲到期晚于周期到期时取周期到期。");
    }

    [TestMethod]
    public void ThreeTriggersUnified_SingleDispatchReschedulesBothCauses()
    {
        var (scheduler, clock) = Create();
        scheduler.Activate();
        scheduler.NoteUserInteraction();
        // 周期到期（激活+30 秒）与空闲到期（交互+2 小时）均已过期：一次派发统一结算。
        clock.Now += TimeSpan.FromHours(26);
        Assert.IsTrue(scheduler.IsCheckDue(clock.Now));
        scheduler.NoteCheckDispatched();
        Assert.IsFalse(
            scheduler.IsCheckDue(clock.Now),
            "三触发统一：一次派发同时推进周期并消费空闲臂，不产生第二次到期。");
        Assert.AreEqual(clock.Now + PeriodicInterval, scheduler.NextDueAt());
    }

    [TestMethod]
    public void OverdueDue_ClampsDelayToOneSecondFloor()
    {
        var (scheduler, clock) = Create();
        scheduler.Activate();
        clock.Now += TimeSpan.FromHours(1);
        Assert.AreEqual(
            TimeSpan.FromSeconds(1),
            scheduler.NextDueDelay(clock.Now),
            "已过期的到期按 1 秒下限立即重评（定时器间隔必须为正）。");
    }

    [TestMethod]
    public void SubSecondRemaining_RoundsUpToWholeSeconds()
    {
        var (scheduler, clock) = Create();
        scheduler.Activate();
        clock.Now += TimeSpan.FromSeconds(29.5);
        Assert.AreEqual(
            TimeSpan.FromSeconds(1),
            scheduler.NextDueDelay(clock.Now),
            "整秒向上取整：剩余 0.5 秒取 1 秒。");
        clock.Now -= TimeSpan.FromSeconds(29.4);
        Assert.AreEqual(
            FirstCheckDelay,
            scheduler.NextDueDelay(clock.Now),
            "剩余不足整秒的损耗向上补齐（29.9 秒 → 30 秒）。");
    }

    [TestMethod]
    public void Interaction_BeforeActivation_IsSupersededByActivationBaseline()
    {
        var (scheduler, clock) = Create();
        clock.Now += TimeSpan.FromHours(3);
        scheduler.NoteUserInteraction();
        scheduler.Activate();
        Assert.AreEqual(
            clock.Now + FirstCheckDelay,
            scheduler.NextDueAt(),
            "激活重建基准（交互/空闲段基准=激活时刻）：激活前的交互不产生更早到期。");
    }
}
