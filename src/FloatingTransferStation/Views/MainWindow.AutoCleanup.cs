using System.Windows;
using System.Windows.Threading;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Views;

/// <summary>
/// 自动清理（1.23.0）：每 24 小时清理各内容分类的非置顶卡片（置顶与复盘内容
/// 绝不触碰）。调度 = 偏好里的 UTC 绝对时间戳（<see cref="AutoCleanupSchedule"/>）
/// + 常驻 1 小时巡检计时器 + 板装载成功后的启动补跑；休眠、重启与时钟校正下的
/// 漂移由此收敛为最大 1 小时延迟。安全前置条件收敛在漏斗内部，不靠调用点自觉：
/// 板未装载成功绝不清扫（看不见内容时不删除）、在途不重入、开关关闭即返回。
/// 清扫走既有删除路径：单次原子保存、单批撤销栈、图片文件按撤销栈既有生命周期。
/// </summary>
public partial class MainWindow : Window
{
    private static readonly TimeSpan AutoCleanupCheckInterval = TimeSpan.FromHours(1);

    private readonly DispatcherTimer _autoCleanupCheckTimer = new()
    {
        Interval = AutoCleanupCheckInterval
    };
    private bool _boardLoadSucceeded;
    private bool _isAutoCleanupCheckInFlight;

    private void InitializeAutoCleanup()
    {
        _autoCleanupCheckTimer.Tick += (_, _) => RunAutoCleanupCheck();
    }

    /// <summary>
    /// 板面后台装载成功的回调（App 装载成功路径经 Dispatcher 调用）：置装载闸门
    /// 并立即补跑一次巡检。装载失败路径不调用 → 本会话绝不清扫。
    /// </summary>
    internal void OnBoardLoadCompleted()
    {
        _boardLoadSucceeded = true;
        RunAutoCleanupCheck();
    }

    private void RunAutoCleanupCheck()
    {
        // 经 TrackPendingOperation 追踪:关闭序列 DrainPendingOperationsAsync 会等它,
        // 操作门封死后不会出现新的清扫操作。
        TrackPendingOperation(CheckAutoCleanupSafelyAsync());
    }

    private async Task CheckAutoCleanupSafelyAsync()
    {
        try
        {
            await CheckAutoCleanupAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 巡检整体异常安全:保存失败已由服务层状态条兜底,门封死等关闭竞态按
            // 「静默放弃本次」处理,不留未观察异常。
        }
    }

    /// <summary>
    /// 自动清理单入口漏斗：装载闸门 → 在途守卫 → 开关 → 周期分派。时间显式传入
    /// （默认 UTC now），测试可传确定值。记账与清扫共用内存权威偏好副本，持久化
    /// 失败不回滚内存时间戳（本会话不重扫，下次启动自然重试）。
    /// </summary>
    internal async Task CheckAutoCleanupAsync(DateTimeOffset? nowUtc = null)
    {
        if (_isClosing ||
            !_boardLoadSucceeded ||
            _isAutoCleanupCheckInFlight ||
            !_preferences.AutoCleanupEnabled)
        {
            return;
        }

        _isAutoCleanupCheckInFlight = true;
        try
        {
            var now = nowUtc ?? DateTimeOffset.UtcNow;
            switch (AutoCleanupSchedule.Evaluate(_preferences.AutoCleanupLastRunAtUtc, now))
            {
                case AutoCleanupDecision.EstablishBaseline:
                    await RecordAutoCleanupRunAsync(now);
                    break;
                case AutoCleanupDecision.Wait:
                    break;
                case AutoCleanupDecision.Due:
                    var outcome = await _mutations.ClearNonPinnedAsync(
                        AutoCleanupSchedule.SweepCategories);
                    if (!outcome.Saved)
                    {
                        // 服务层已提示并整批恢复;不记账,下个巡检周期自然重试。
                        break;
                    }

                    if (outcome.RemovedCount > 0)
                    {
                        ShowStatus($"已自动清理非置顶内容 {outcome.RemovedCount} 项（可 Ctrl+Z 撤销）");
                    }

                    await RecordAutoCleanupRunAsync(now);
                    break;
            }
        }
        finally
        {
            _isAutoCleanupCheckInFlight = false;
        }
    }

    /// <summary>
    /// 记账并等持久化完成：漏斗任务本身被 TrackPendingOperation 追踪，关闭序列
    /// Drain 因此必然等到时间戳落盘（或其失败提示）才封门。内存权威先行——持久化
    /// 失败不回滚内存时间戳，本会话不会每小时重扫，下次启动自然重试。
    /// </summary>
    private async Task RecordAutoCleanupRunAsync(DateTimeOffset nowUtc)
    {
        _preferences = _preferences with { AutoCleanupLastRunAtUtc = nowUtc };
        await PersistPreferencesAsync(_preferences);
    }
}
