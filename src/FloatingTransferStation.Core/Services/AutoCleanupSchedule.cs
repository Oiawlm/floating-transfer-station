using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

/// <summary>
/// 自动清理策略（逐卡 TTL 语义；2026-10-14 起保留时长为用户可选档位，默认
/// 24 小时）：纯逻辑、零依赖。每张非置顶卡片入库满所选档位的保留时长（
/// <see cref="GetRetentionDuration"/>）即到期，判据只看卡片自身
/// <see cref="BoardItem.CreatedAt"/> 与检查时刻的差——对给定时刻重复检查结果
/// 一致（幂等），且不依赖任何历史记录，因此没有「上次运行」记账与基线：
/// 1.23.0 的间隔清扫调度机制（<c>AutoCleanupLastRunAtUtc</c> + 建基线三态）
/// 随语义改版删除，旧 preferences.json 里残留的时间戳字段按未知成员忽略、
/// 不影响加载。到期比较点唯一落在 <see cref="BoardService.RemoveNonPinned"/>
/// 的移除谓词（截止时刻由 <see cref="GetExpiryCutoff"/> 按当前档位推导），
/// 触发节奏仍是「装载成功补跑 + 1 小时巡检」（节奏与档位无关），休眠、重启
/// 与时钟校正下的延迟收敛为最长约 1 小时；改短档位后已超龄的存量卡在下一
/// 次检查时一次性追溯清理，不在改设置的瞬间触发。
/// </summary>
public static class AutoCleanupSchedule
{
    /// <summary>
    /// 档位 → 保留时长的全仓唯一映射：switch 表达式穷尽全部命名成员、无兜底臂，
    /// 新增档位漏映射（CS8509）在质量门 -warnaserror 下即编译失败。CS8524（未命名
    /// 枚举值，仅手改 preferences.json 写入越界整数才会出现）显式豁免：该值让本
    /// 表达式抛 SwitchExpressionException，由巡检漏斗既有异常安全吞掉（跳过当次
    /// 清扫），设置窗 Tag 匹配则回落默认档——用户重选档位即写回合法值自愈。
    /// 除本映射与测试期望值外，生产代码任何位置不得再出现清理时长的小时数字面量。
    /// </summary>
#pragma warning disable CS8524 // switch 表达式不处理未命名枚举值（见上：漏映射 CS8509 仍为编译错误）
    public static TimeSpan GetRetentionDuration(AutoCleanupRetention retention) => retention switch
    {
        AutoCleanupRetention.TwoHours => TimeSpan.FromHours(2),
        AutoCleanupRetention.FourHours => TimeSpan.FromHours(4),
        AutoCleanupRetention.SixHours => TimeSpan.FromHours(6),
        AutoCleanupRetention.EightHours => TimeSpan.FromHours(8),
        AutoCleanupRetention.TenHours => TimeSpan.FromHours(10),
        AutoCleanupRetention.TwelveHours => TimeSpan.FromHours(12),
        AutoCleanupRetention.TwentyFourHours => TimeSpan.FromHours(24),
    };
#pragma warning restore CS8524

    /// <summary>
    /// 清扫范围 = 仅内容桶角色（BoardCategoryCatalog.Ordered 中
    /// <see cref="BoardCategoryCatalog.IsContentBucket"/> 为真的成员；#18 修订，
    /// Issue #162）：复盘与启动标签永不触碰（即使超龄、即使有残留内容）。
    /// 单一权威来源是分类目录的角色机制：未来新增内容桶默认纳入清扫，新增
    /// 复盘/启动类分类天然排除（契约测试锁定清单内容）。
    /// </summary>
    public static IReadOnlyList<BoardCategory> SweepCategories { get; } =
        BoardCategoryCatalog.Ordered
            .Where(BoardCategoryCatalog.IsContentBucket)
            .ToArray();

    /// <summary>到期截止时刻 = now − 所选档位的保留时长：CreatedAt ≤ 截止即到期
    /// （边界含等于，与既有「恰在保留时长边界为到期」口径一致）；CreatedAt 在
    /// 未来（时钟回拨、导入时间戳漂移）年龄为负，自然不到期，无需特判。</summary>
    public static DateTimeOffset GetExpiryCutoff(DateTimeOffset nowUtc, AutoCleanupRetention retention) =>
        nowUtc - GetRetentionDuration(retention);
}
