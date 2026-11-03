using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FloatingTransferStation.Tests;

/// <summary>UI 套件的机器级互斥（flake 台账 A7，2026-11-02）。真实光标/前台/剪贴板
/// 是全机共享资源：本仓库 UI 套件并发跑在同一桌面会互相污染——实证＝2026-11-02
/// detail-polish 批 W3 时段兄弟窗口并发全量致 33 例无关失败、W1 拥挤期真输入连败
/// ＋僵尸 testhost 持前台焦点（docs/handoff 报告登记）。本网关在 AssemblyInitialize
/// 以命名系统互斥体把「同机并发的 UI 套件」串行化：CI 单作业无竞争、零影响；本机
/// 第二个含本网关的套件等待而非互踩。等待上限 45 分钟（对齐批次提示词的人工锁
/// 协议口径），超时以可归因失败暴露（持锁方迟迟不还），不静默污染。持有方崩溃时
/// OS 回收互斥体（abandoned），下一获取方直接接管，无死锁。</summary>
[TestClass]
public static class UiSuiteMachineGate
{
    private const string MutexName = @"Local\FloatingTransferStation.UiTests";

    // 持有静态引用防 GC 终结器提前关闭句柄（会误触发 abandoned 语义）。
    private static Mutex? _held;

    [AssemblyInitialize]
    public static void Acquire(TestContext testContext)
    {
        _held = new Mutex(initiallyOwned: false, MutexName);
        try
        {
            var acquired = _held.WaitOne(TimeSpan.FromMinutes(45));
            if (!acquired)
            {
                _held.Dispose();
                _held = null;
                Assert.Fail(
                    "本机另一 UI 测试套件持有机器级互斥（FloatingTransferStation.UiTests）已超过 45 分钟："
                    + "真实光标/前台是全机共享资源，并发 UI 套件会互相污染。请先处置另一套件"
                    + "（正常运行完、或清理其残留 testhost）再重跑。");
            }
        }
        catch (AbandonedMutexException)
        {
            // 前一持有方异常退出（崩溃/被杀）遗留的 abandoned 互斥体＝已取得所有权。
        }

        testContext.WriteLine($"UI 套件机器级互斥已取得：{MutexName}（{DateTime.Now:HH:mm:ss}）");
    }

    [AssemblyCleanup]
    public static void Release()
    {
        // 不调用 ReleaseMutex（互斥体有线程亲和，Cleanup 线程与 Acquire 不同）：
        // 进程将退出，Dispose 关闭句柄即释放；此后同机下一套件正常获取。
        _held?.Dispose();
        _held = null;
    }
}
