using System.Diagnostics;
using System.Text.Json;

namespace FloatingTransferStation.Tests;

/// <summary>机器准入网关语义测试（契约 #53 锁定）。全部用 GUID 名互斥体与假探针——
/// 严禁触碰真实命名互斥（Local\FloatingTransferStation.UiTests / .App）：程序集网关持锁中，
/// 触碰会自锁死整个测试宿主（仿 LifecycleTests.Runtime 的 GUID 模式）。互斥持有方一律
/// 放在独立线程（同线程重入会立即可取，测不出等待语义）。</summary>
[TestClass]
public sealed class MachineAdmissionGateTests
{
    private static MachineAdmissionPolicy Policy(
        TimeSpan? suiteWaitLimit = null,
        TimeSpan? idleWaitLimit = null,
        bool skipIdle = true) => new()
        {
            MutexName = @"Local\FloatingTransferStation.Tests." + Guid.NewGuid().ToString("N"),
            ProductMutexName = @"Local\FloatingTransferStation.Tests." + Guid.NewGuid().ToString("N"),
            SuiteWaitLimit = suiteWaitLimit ?? TimeSpan.FromSeconds(5),
            IdleMinimum = TimeSpan.FromSeconds(180),
            IdleWaitLimit = idleWaitLimit ?? TimeSpan.FromSeconds(5),
            PollInterval = TimeSpan.FromMilliseconds(30),
            SkipIdleAdmission = skipIdle,
        };

    private static MachineAdmissionKernel Kernel(
        MachineAdmissionPolicy policy,
        string registrationDirectory,
        IIdleProbe? idle = null,
        IForeignSuiteProbe? foreignSuites = null,
        IInstalledInstanceProbe? installedInstance = null) => new(
        new SystemMutexProbe(),
        idle ?? new FakeIdleProbe(),
        foreignSuites ?? new FakeForeignSuiteProbe(),
        installedInstance ?? new FakeInstanceProbe(running: false),
        registrationDirectory,
        policy);

    /// <summary>在独立线程持有 GUID 互斥 <paramref name="hold"/> 后释放；Set 事件保证持锁
    /// 先于内核等待成立。</summary>
    private static (EventWaitHandle Held, Thread Thread) HoldMutexThenRelease(string name, TimeSpan hold)
    {
        var held = new ManualResetEvent(false);
        var thread = new Thread(() =>
        {
            using var mutex = new Mutex(initiallyOwned: true, name);
            held.Set();
            Thread.Sleep(hold);
            mutex.ReleaseMutex();
        });
        thread.Start();
        held.WaitOne();
        return (held, thread);
    }

    /// <summary>在独立线程持有 GUID 互斥直至线程退出（不 ReleaseMutex → abandoned）。</summary>
    private static Thread HoldMutexUntilThreadExit(string name)
    {
        var held = new ManualResetEvent(false);
        var thread = new Thread(() =>
        {
            using var mutex = new Mutex(initiallyOwned: true, name);
            held.Set();
            Thread.Sleep(100);
        });
        thread.Start();
        held.WaitOne();
        thread.Join();
        return thread;
    }

    private static string OwnRegistrationPath(string directory)
        => Path.Combine(directory, $"{Environment.ProcessId}.json");

    /// <summary>存活助手进程（模拟「另一个在册套件」——登记清扫只保留活 pid 的文件）。</summary>
    private static Process StartHelperProcess()
        => Process.Start(new ProcessStartInfo("cmd.exe", "/c timeout /t 8 /nobreak")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;

    private static void WriteRegistration(string directory, int pid, string state, string root)
    {
        Directory.CreateDirectory(directory);
        var payload = new { pid, state, root, startedUtc = DateTime.UtcNow };
        File.WriteAllText(Path.Combine(directory, $"{pid}.json"), JsonSerializer.Serialize(payload));
    }

    // ---- 互斥语义 ----

    [TestMethod]
    public void Admission_WaitsForHeldMutexThenSucceeds()
    {
        using var directory = new TestDirectory();
        var policy = Policy();
        var (_, holder) = HoldMutexThenRelease(policy.MutexName, TimeSpan.FromMilliseconds(400));

        var stopwatch = Stopwatch.StartNew();
        using var result = Kernel(policy, directory.Root).TryAdmit();
        stopwatch.Stop();

        Assert.IsNull(result.FailureMessage);
        Assert.IsTrue(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(250), "必须真实等待持锁方释放后再取得");
        holder.Join();
    }

    [TestMethod]
    public void Admission_MutexTimeoutFailsWithRegistrationAttribution()
    {
        using var directory = new TestDirectory();
        using var otherSuite = StartHelperProcess();
        var policy = Policy(suiteWaitLimit: TimeSpan.FromMilliseconds(300));
        var (_, holder) = HoldMutexThenRelease(policy.MutexName, TimeSpan.FromSeconds(3));
        WriteRegistration(directory.Root, otherSuite.Id, "running", @"D:\fake-worktree");

        try
        {
            var result = Kernel(policy, directory.Root).TryAdmit();

            Assert.IsNull(result.Ticket);
            Assert.IsNotNull(result.FailureMessage);
            StringAssert.Contains(result.FailureMessage, "机器级互斥等待超时");
            StringAssert.Contains(result.FailureMessage, $"pid={otherSuite.Id}");
            StringAssert.Contains(result.FailureMessage, "clear-stale-ui-test-processes");
        }
        finally
        {
            holder.Join();
        }

        Assert.IsFalse(File.Exists(OwnRegistrationPath(directory.Root)), "失败路径必须删除自己的登记文件");
    }

    [TestMethod]
    public void Admission_TakesOverAbandonedMutex()
    {
        using var directory = new TestDirectory();
        var policy = Policy();
        HoldMutexUntilThreadExit(policy.MutexName);

        using var result = Kernel(policy, directory.Root).TryAdmit();

        Assert.IsNull(result.FailureMessage);
    }

    // ---- 外来套件语义 ----

    [TestMethod]
    public void Admission_WaitsForForeignSuiteThenSucceeds()
    {
        using var directory = new TestDirectory();
        var foreign = new FakeForeignSuiteProbe();
        var remaining = 2;
        foreign.Handler = self => Interlocked.Decrement(ref remaining) >= 0
            ? [new ForeignSuiteCandidate(777001, "testhost", @"D:\old-branch\testhost.exe")]
            : Array.Empty<ForeignSuiteCandidate>();

        using var result = Kernel(Policy(), directory.Root, foreignSuites: foreign).TryAdmit();

        Assert.IsNull(result.FailureMessage);
        Assert.AreEqual(3, foreign.Calls, "外来者退出后必须停止轮询");
    }

    [TestMethod]
    public void Admission_ForeignSuiteTimeoutFailsWithAttribution()
    {
        using var directory = new TestDirectory();
        var foreign = new FakeForeignSuiteProbe
        {
            Handler = self => [new ForeignSuiteCandidate(777002, "testhost", @"D:\old-branch\testhost.exe")],
        };

        var result = Kernel(Policy(suiteWaitLimit: TimeSpan.FromMilliseconds(300)), directory.Root, foreignSuites: foreign).TryAdmit();

        Assert.IsNull(result.Ticket);
        Assert.IsNotNull(result.FailureMessage);
        StringAssert.Contains(result.FailureMessage, "外来 UI 测试套件");
        StringAssert.Contains(result.FailureMessage, "pid=777002");
        StringAssert.Contains(result.FailureMessage, "clear-stale-ui-test-processes");
    }

    [TestMethod]
    public void Admission_ExcludesRegisteredSuitesFromForeignDetection()
    {
        // 死锁规避核心：已登记（含网关）的套件不算外来者——两个含网关套件交错启动
        // 只被互斥串行化，不会在「外来等待」上互等成环。登记 pid 必须是活进程
        //（创建即清扫死 pid 陈旧文件），起一个真实存活期覆盖本用例的助手进程。
        using var directory = new TestDirectory();
        using var helper = Process.Start(new ProcessStartInfo("cmd.exe", "/c timeout /t 8 /nobreak")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        Assert.IsNotNull(helper);
        WriteRegistration(directory.Root, helper.Id, "waiting", @"D:\sibling-worktree");
        var foreign = new FakeForeignSuiteProbe
        {
            Handler = self => [new ForeignSuiteCandidate(helper.Id, "testhost", @"D:\sibling-worktree\testhost.exe")],
        };

        var result = Kernel(
            Policy(suiteWaitLimit: TimeSpan.FromMilliseconds(400)),
            directory.Root,
            foreignSuites: foreign).TryAdmit();

        Assert.IsNull(result.FailureMessage);
        Assert.IsTrue(File.Exists(Path.Combine(directory.Root, $"{helper.Id}.json")), "别人的登记文件不得被误删");
    }

    // ---- 已装实例语义 ----

    [TestMethod]
    public void Admission_FailsFastWhenInstalledInstanceRunning()
    {
        using var directory = new TestDirectory();

        var result = Kernel(Policy(), directory.Root, installedInstance: new FakeInstanceProbe(running: true)).TryAdmit();

        Assert.IsNull(result.Ticket);
        Assert.IsNotNull(result.FailureMessage);
        StringAssert.Contains(result.FailureMessage, "已装实例");
        StringAssert.Contains(result.FailureMessage, "优雅关闭");
        Assert.IsFalse(File.Exists(OwnRegistrationPath(directory.Root)), "fail-fast 不得留下登记文件");
    }

    // ---- 空闲准入语义 ----

    [TestMethod]
    public void Admission_WaitsForIdleThenSucceeds()
    {
        using var directory = new TestDirectory();
        var idle = new FakeIdleProbe(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20))
        {
            Fallback = TimeSpan.FromSeconds(999),
        };

        using var result = Kernel(Policy(skipIdle: false), directory.Root, idle: idle).TryAdmit();

        Assert.IsNull(result.FailureMessage);
        Assert.IsTrue(idle.Calls >= 3, "忙时必须轮询等待而非立即失败");
    }

    [TestMethod]
    public void Admission_IdleTimeoutFailsWithAttribution()
    {
        using var directory = new TestDirectory();
        var idle = new FakeIdleProbe { Fallback = TimeSpan.FromSeconds(1) };

        var result = Kernel(
            Policy(idleWaitLimit: TimeSpan.FromMilliseconds(240), skipIdle: false),
            directory.Root,
            idle: idle).TryAdmit();

        Assert.IsNull(result.Ticket);
        Assert.IsNotNull(result.FailureMessage);
        StringAssert.Contains(result.FailureMessage, "宿主机持续人用");
        Assert.IsFalse(File.Exists(OwnRegistrationPath(directory.Root)));
    }

    [TestMethod]
    public void Admission_SkipsIdleProbeEntirelyOnCi()
    {
        using var directory = new TestDirectory();
        var idle = new FakeIdleProbe { Fallback = TimeSpan.FromSeconds(0), ForbidConsultation = true };

        using var result = Kernel(Policy(skipIdle: true), directory.Root, idle: idle).TryAdmit();

        Assert.IsNull(result.FailureMessage);
    }

    // ---- 登记生命周期语义 ----

    [TestMethod]
    public void Registration_MarksRunningAndTicketDisposeDeletesFile()
    {
        using var directory = new TestDirectory();

        var result = Kernel(Policy(), directory.Root).TryAdmit();
        using (result.Ticket!)
        {
            Assert.IsTrue(File.Exists(OwnRegistrationPath(directory.Root)), "准入成功后登记文件必须在场");
            StringAssert.Contains(File.ReadAllText(OwnRegistrationPath(directory.Root)), "\"running\"", "取得全部准入后状态必须翻为 running");
        }

        Assert.IsFalse(File.Exists(OwnRegistrationPath(directory.Root)), "票据释放后登记文件必须删除");
    }

    [TestMethod]
    public void Registration_SweepsStaleFilesForDeadPids()
    {
        using var directory = new TestDirectory();
        WriteRegistration(directory.Root, 999999998, "running", @"D:\gone-worktree");

        using (Kernel(Policy(), directory.Root).TryAdmit().Ticket!)
        {
            Assert.IsFalse(File.Exists(Path.Combine(directory.Root, "999999998.json")), "死 pid 的陈旧登记必须被机会式清扫");
        }
    }

    [TestMethod]
    public void Registration_AttributionListsOtherSuitesWithStateAndRoot()
    {
        using var directory = new TestDirectory();
        using var otherSuite = StartHelperProcess();
        WriteRegistration(directory.Root, otherSuite.Id, "running", @"D:\other-worktree");
        var policy = Policy(suiteWaitLimit: TimeSpan.FromMilliseconds(300));
        var (_, holder) = HoldMutexThenRelease(policy.MutexName, TimeSpan.FromSeconds(3));

        try
        {
            var result = Kernel(policy, directory.Root).TryAdmit();

            StringAssert.Contains(result.FailureMessage, $"pid={otherSuite.Id}");
            StringAssert.Contains(result.FailureMessage, "state=running");
            StringAssert.Contains(result.FailureMessage, "other-worktree");
        }
        finally
        {
            holder.Join();
        }
    }

    // ---- 假探针（观测原语；决策断言全部在被测内核上） ----

    private sealed class FakeIdleProbe(params TimeSpan[] sequence) : IIdleProbe
    {
        private readonly Queue<TimeSpan> _sequence = new(sequence);

        public TimeSpan Fallback { get; init; } = TimeSpan.FromMinutes(5);

        public bool ForbidConsultation { get; init; }

        public int Calls { get; private set; }

        public TimeSpan IdleElapsed
        {
            get
            {
                Assert.IsFalse(ForbidConsultation, "SkipIdleAdmission=true 时空闲探针不得被咨询（CI 零影响语义）");
                Calls++;
                return _sequence.Count > 0 ? _sequence.Dequeue() : Fallback;
            }
        }
    }

    private sealed class FakeForeignSuiteProbe : IForeignSuiteProbe
    {
        public Func<int, IReadOnlyList<ForeignSuiteCandidate>>? Handler { get; set; }

        public int Calls { get; private set; }

        public IReadOnlyList<ForeignSuiteCandidate> Enumerate(int selfPid)
        {
            Calls++;
            return Handler?.Invoke(selfPid) ?? [];
        }
    }

    private sealed class FakeInstanceProbe(bool running) : IInstalledInstanceProbe
    {
        public bool IsRunning() => running;
    }
}
