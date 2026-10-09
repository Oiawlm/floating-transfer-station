using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

/// <summary>自动清理巡检的分派结果。</summary>
public enum AutoCleanupDecision
{
    /// <summary>基线未建立（null）或时间戳在未来（时钟被回拨）：只记账不清扫。</summary>
    EstablishBaseline,

    /// <summary>距上次清扫不足一个周期：无动作。</summary>
    Wait,

    /// <summary>距上次清扫已满一个周期：执行清扫，成功后记账。</summary>
    Due
}

/// <summary>
/// 自动清理调度策略（1.23.0）：纯逻辑、零依赖，时间由调用方显式传入。
/// 到期判断基于 preferences.json 里的 UTC 绝对时间戳而非长定时器——进程重启、
/// 休眠与系统时钟校正都不会让清扫漂移或永不触发，补跑语义天然成立。
/// null 一律建立基线、绝不清扫：升级首启立即清空旧内容是静默不可逆删除事故，
/// 首扫必须发生在开启（含默认开启的升级）之后一个完整周期。
/// </summary>
public static class AutoCleanupSchedule
{
    public static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// 清扫范围 = 全部内容分类（BoardCategoryCatalog.Ordered）显式排除复盘分类。
    /// 单一权威来源是分类目录本身：未来新增内容分类默认纳入清扫，新增复盘类
    /// 分类必须在此显式排除（契约测试锁定清单内容）。
    /// </summary>
    public static IReadOnlyList<BoardCategory> SweepCategories { get; } =
        BoardCategoryCatalog.Ordered
            .Where(category => category != DailyReviewMigration.ReviewCategory)
            .ToArray();

    public static AutoCleanupDecision Evaluate(DateTimeOffset? lastRunAtUtc, DateTimeOffset nowUtc)
    {
        if (lastRunAtUtc is not { } lastRun || nowUtc < lastRun)
        {
            return AutoCleanupDecision.EstablishBaseline;
        }

        return nowUtc - lastRun < CleanupInterval
            ? AutoCleanupDecision.Wait
            : AutoCleanupDecision.Due;
    }
}
