using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FloatingTransferStation.Tests;

/// <summary>UI 套件机器准入网关（薄壳，2026-11-02 W2 落地、2026-11-03 重构为内核＋探针，
/// 契约 #53）。真实光标/前台/置顶带是全机共享资源：同桌面并发的 UI 套件会互相污染
/// （2026-11-02 W3 时段兄弟窗口并发全量 33 例无关失败、W2 23:07 未取锁并发 6 例判废）。
/// 一切准入决策集中在 <see cref="MachineAdmissionKernel"/>（五步：已装实例 fail-fast →
/// 登记先于等锁 → 取命名互斥 → 外来套件等待 → 空闲准入；死锁规避与超时归因见其注释），
/// 本壳只做生产装配（真实探针＋策略常量）与 MSTest 生命周期接线。CI 单作业：无竞争、
/// 无已装实例、空闲步整体跳过——零影响。</summary>
[TestClass]
public static class UiSuiteMachineGate
{
    private static MachineAdmissionTicket? _ticket;

    [AssemblyInitialize]
    public static void Acquire(TestContext testContext)
    {
        var policy = MachineAdmissionPolicy.Default(skipIdleAdmission: IsContinuousIntegration);
        var kernel = new MachineAdmissionKernel(
            new SystemMutexProbe(),
            new LastInputIdleProbe(),
            new LoadedModuleForeignSuiteProbe(),
            new ProductInstanceMutexProbe(),
            MachineAdmissionKernel.DefaultRegistrationDirectory(),
            policy);
        var result = kernel.TryAdmit(line => testContext.WriteLine(line));
        if (result.Ticket is null)
        {
            Assert.Fail(result.FailureMessage ?? "机器准入失败（无归因消息）。");
            return;
        }

        _ticket = result.Ticket;
    }

    [AssemblyCleanup]
    public static void Release() => _ticket?.Dispose();

    /// <summary>CI 环境判定（GITHUB_ACTIONS / CI 惯用变量）：GetLastInputInfo 在 CI/
    /// 自动登录会话可能恒 0，空闲准入整体跳过（消灭一类伪红，见契约 #53）。</summary>
    private static bool IsContinuousIntegration =>
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"
        || Environment.GetEnvironmentVariable("CI") == "true";
}
