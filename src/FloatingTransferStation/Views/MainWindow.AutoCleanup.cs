using System.Windows;
using System.Windows.Threading;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Views;

/// <summary>
/// 自动清理（逐卡 TTL 语义；保留时长 2026-10-14 起为用户可选档位，默认 24 小时）：
/// 每张非置顶卡片入库满所选档位的保留时长即删（改短档位后超龄存量卡在下次检查时
/// 追溯清理），置顶与复盘内容绝不触碰。触发 = 板装载成功后的启动补跑 + 常驻
/// 1 小时巡检计时器（节奏与档位无关）；休眠、重启与时钟校正下的延迟由此收敛为
/// 最长约 1 小时。安全前置条件收敛在漏斗内部，不靠调用点自觉：板未装载成功绝不
/// 清扫（看不见内容时不删除）、在途不重入、开关关闭即返回。档位与开关同样在每次
/// 检查时从 <c>_preferences</c> 现读（契约 #5 即时生效；改设置不在瞬间触发清扫）。
/// 清扫走既有删除路径：单次原子保存、单批撤销栈（可 Ctrl+Z，仅本次运行内）、
/// 图片文件按撤销栈既有生命周期；TTL 判定天然幂等，无需「上次运行」记账
/// （1.23.0 的间隔清扫记账已删）。
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
    /// 自动清理单入口漏斗：装载闸门 → 在途守卫 → 开关 → 清扫到期卡。时间显式传入
    /// （默认 UTC now），测试可传确定值；TTL 判定幂等，清扫成功无需任何记账落盘。
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
            var cutoff = AutoCleanupSchedule.GetExpiryCutoff(
                nowUtc ?? DateTimeOffset.UtcNow,
                _preferences.AutoCleanupRetention);
            var outcome = await _mutations.ClearNonPinnedAsync(AutoCleanupSchedule.SweepCategories, cutoff);
            if (!outcome.Saved)
            {
                // 服务层已提示并整批恢复;下个巡检周期自然重试。
                return;
            }

            if (outcome.RemovedCount > 0)
            {
                ShowStatus($"已自动清理 {outcome.RemovedCount} 张到期卡片，可 Ctrl+Z 撤销（仅本次运行内）");
            }
        }
        finally
        {
            _isAutoCleanupCheckInFlight = false;
        }
    }
}
