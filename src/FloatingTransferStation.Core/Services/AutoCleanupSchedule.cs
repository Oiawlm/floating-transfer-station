using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

/// <summary>
/// 自动清理策略（1.25.0 起为逐卡 24 小时 TTL 语义）：纯逻辑、零依赖。
/// 每张非置顶卡片入库满 <see cref="CleanupInterval"/>（24 小时，固定值不配置）即到期，
/// 判据只看卡片自身 <see cref="BoardItem.CreatedAt"/> 与检查时刻的差——对给定时刻
/// 重复检查结果一致（幂等），且不依赖任何历史记录，因此没有「上次运行」记账与
/// 基线：1.23.0 的间隔清扫调度机制（<c>AutoCleanupLastRunAtUtc</c> + 建基线三态）
/// 随语义改版删除，旧 preferences.json 里残留的时间戳字段按未知成员忽略、不影响
/// 加载。到期比较点唯一落在 <see cref="BoardService.RemoveNonPinned"/> 的移除谓词
/// （截止时刻由 <see cref="GetExpiryCutoff"/> 推导），触发节奏仍是「装载成功补跑 +
/// 1 小时巡检」，休眠、重启与时钟校正下的延迟收敛为最长约 1 小时；存量超龄卡在
/// 升级后首次检查即删。
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

    /// <summary>到期截止时刻 = now − 24 小时：CreatedAt ≤ 截止即到期（边界含等于，
    /// 与既有「恰在 24 小时边界为到期」口径一致）；CreatedAt 在未来（时钟回拨、
    /// 导入时间戳漂移）年龄为负，自然不到期，无需特判。</summary>
    public static DateTimeOffset GetExpiryCutoff(DateTimeOffset nowUtc) => nowUtc - CleanupInterval;
}
