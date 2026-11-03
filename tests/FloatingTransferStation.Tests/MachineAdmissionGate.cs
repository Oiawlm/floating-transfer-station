using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>UI 测试套件机器准入网关内核（契约 #53，2026-11-03）。真实光标/前台/置顶带是
/// 全机共享资源：同桌面并发的 UI 套件会互相污染（2026-11-02 W3 时段 33 例无关失败、
/// W2 23:07 未取锁并发 6 例判废——见 flake 台账 A7）。本内核把「裸互斥＋人工纪律」收敛为
/// 单一机器准入网关，五步顺序锁定（决策 D3）：
/// ①已装实例 fail-fast（产品单实例锁可开＝实例在跑，Topmost 面板与真输入互踩）；
/// ②登记——%TEMP%\FloatingTransferStation\ui-suite-gate\&lt;pid&gt;.json 先登记后等锁，
/// 含网关的等待者因此被第④步排除，两个含网关套件交错启动不会互等成环（死锁规避）；
/// ③取命名互斥 Local\FloatingTransferStation.UiTests（唯一排他原语；有依据的等待上限＋
/// abandoned 接管）；④外来套件探测等待（不含网关的套件：旧分支/裸跑；有界等待、超时
/// 可归因）；⑤空闲准入（GetLastInputInfo ≥180s；CI 会话可能恒 0，CI 环境整体跳过）。
/// 决策内核可注入四个探针（互斥/空闲/进程/实例），语义测试用 GUID 名互斥体与假探针——
/// 严禁触碰真实命名互斥体（网关持锁中会自锁死，仿 LifecycleTests.Runtime 的 GUID 模式）。</summary>
internal sealed class MachineAdmissionKernel
{
    private readonly IMutexProbe _mutex;
    private readonly IIdleProbe _idle;
    private readonly IForeignSuiteProbe _foreignSuites;
    private readonly IInstalledInstanceProbe _installedInstance;
    private readonly string _registrationDirectory;
    private readonly MachineAdmissionPolicy _policy;

    public MachineAdmissionKernel(
        IMutexProbe mutex,
        IIdleProbe idle,
        IForeignSuiteProbe foreignSuites,
        IInstalledInstanceProbe installedInstance,
        string registrationDirectory,
        MachineAdmissionPolicy policy)
    {
        _mutex = mutex;
        _idle = idle;
        _foreignSuites = foreignSuites;
        _installedInstance = installedInstance;
        _registrationDirectory = registrationDirectory;
        _policy = policy;
    }

    /// <summary>登记目录默认位置：%TEMP% 下按产品前缀（机器级测试准入状态，
    /// PROJECT_GUIDE 临时产物分类的 %TEMP%\FloatingTransferStation\ 语义）。</summary>
    public static string DefaultRegistrationDirectory()
        => Path.Combine(Path.GetTempPath(), "FloatingTransferStation", "ui-suite-gate");

    /// <summary>执行五步准入。成功返回持票（互斥持有＋running 登记，Dispose 即释放全部）；
    /// 失败返回可归因消息（登记文件在所有失败路径上由本方法删除）。</summary>
    public MachineAdmissionResult TryAdmit(Action<string>? log = null)
    {
        // ① 已装实例 fail-fast：CI 上天然不存在＝无操作。
        if (_installedInstance.IsRunning())
        {
            return MachineAdmissionResult.Fail(
                "本机已装实例正在运行（产品单实例锁 " + _policy.ProductMutexName + " 可打开）。"
                + "Topmost 常驻面板会与真实光标/前台测试互踩。请先优雅关闭已装实例"
                + "（用其正常退出路径，不要强杀），再重跑本套件。");
        }

        // ② 先登记后等锁（死锁规避关键：含网关的等待者对第④步可见并排除）。
        var registration = SuiteRegistration.CreateWaiting(_registrationDirectory);
        var lease = _mutex.OpenOrCreate(_policy.MutexName);
        var admitted = false;
        try
        {
            // ③ 取互斥：等待上限有实测依据（见 MachineAdmissionPolicy.SuiteWaitLimit 注释）；
            // 前持有方崩溃遗留的 abandoned 互斥体＝已取得所有权，直接接管。
            var acquired = false;
            try
            {
                acquired = lease.WaitOne(_policy.SuiteWaitLimit);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                var holders = registration.DescribeOthers();
                return MachineAdmissionResult.Fail(
                    "机器级互斥等待超时（" + _policy.SuiteWaitLimit + "）：另一 UI 测试套件迟迟不还锁。"
                    + (holders.Length > 0
                        ? "登记目录中的其他套件：" + holders + "。"
                        : "登记目录无其他套件登记（持有方为无登记的进程或登记丢失）。")
                    + "请核对上述套件是否仍在正常运行；僵尸残留用 scripts/clear-stale-ui-test-processes.ps1 清点并按其口径处置后重跑。");
            }

            // ④ 外来套件探测等待：命中＝不含网关的套件（旧分支/裸跑）。有界等待其退出，
            // 超时可归因失败（不静默污染）。已登记（含网关）的套件在内核侧排除——
            // 死锁规避：交错启动的含网关套件一旦登记即不再被视为外来者；轮询每轮重读
            // 登记目录，超时判定前再做一次终读，消除毫秒级登记竞态。
            var foreignDeadline = DateTime.UtcNow + _policy.SuiteWaitLimit;
            while (true)
            {
                var foreigners = EnumerateUnregisteredForeignSuites(registration);
                if (foreigners.Count == 0)
                {
                    break;
                }

                if (DateTime.UtcNow >= foreignDeadline
                    && EnumerateUnregisteredForeignSuites(registration).Count > 0)
                {
                    return MachineAdmissionResult.Fail(
                        "外来 UI 测试套件（不含本网关：旧分支/裸跑的 testhost）仍在运行，等待超时（"
                        + _policy.SuiteWaitLimit + "）："
                        + string.Join("；", foreigners.Select(f => $"pid={f.Pid}({f.Name})"))
                        + "。真实光标/前台是全机共享资源，并发会互相污染。请等其自然结束，"
                        + "或用 scripts/clear-stale-ui-test-processes.ps1 清点并按其口径处置僵尸后重跑。");
                }

                log?.Invoke("等待外来 UI 测试套件退出：" + string.Join(",", foreigners.Select(f => f.Pid)));
                Sleep(_policy.PollInterval);
            }

            // ⑤ 空闲准入：宿主机持续人用时真实输入测试会与人工操作互踩。CI 环境整体跳过
            //（GetLastInputInfo 在 CI/自动登录会话可能恒 0，跳过即消灭一类伪红）。
            if (!_policy.SkipIdleAdmission)
            {
                var idleDeadline = DateTime.UtcNow + _policy.IdleWaitLimit;
                while (_idle.IdleElapsed < _policy.IdleMinimum)
                {
                    if (DateTime.UtcNow >= idleDeadline)
                    {
                        return MachineAdmissionResult.Fail(
                            "宿主机持续人用（GetLastInputInfo 空闲始终 <" + _policy.IdleMinimum.TotalSeconds.ToString("0")
                            + "s，已等待 " + _policy.IdleWaitLimit + "）：真实输入测试会与人工操作互踩。"
                            + "请暂停使用本机后稍后重跑。");
                    }

                    Sleep(_policy.PollInterval);
                }
            }

            registration.MarkRunning();
            admitted = true;
            log?.Invoke($"机器准入完成：互斥={_policy.MutexName} pid={Environment.ProcessId}");
            return MachineAdmissionResult.Admitted(new MachineAdmissionTicket(lease, registration));
        }
        finally
        {
            if (!admitted)
            {
                // 一切失败路径（含异常）统一收尾：释放租约、删自己的登记文件，
                // 不留陈旧登记污染后续套件的归因与排除。
                lease.Dispose();
                registration.Delete();
            }
        }
    }

    private static void Sleep(TimeSpan interval) => Thread.Sleep(interval);

    /// <summary>外来候选＝探针观测 ∧ 未登记（登记排除属于准入决策，收在内核侧可测化）。</summary>
    private IReadOnlyList<ForeignSuiteCandidate> EnumerateUnregisteredForeignSuites(SuiteRegistration registration)
    {
        var registered = registration.OtherRegisteredPids();
        return _foreignSuites
            .Enumerate(Environment.ProcessId)
            .Where(candidate => !registered.Contains(candidate.Pid))
            .ToList();
    }
}

/// <summary>准入结果：要么持票（成功），要么携带可归因失败消息（Assert.Fail 用）。
/// Dispose 转发给票据（using 持有结果即持有全部准入资源）。</summary>
internal sealed class MachineAdmissionResult : IDisposable
{
    public MachineAdmissionTicket? Ticket { get; private init; }
    public string? FailureMessage { get; private init; }

    public static MachineAdmissionResult Admitted(MachineAdmissionTicket ticket) => new() { Ticket = ticket };
    public static MachineAdmissionResult Fail(string message) => new() { FailureMessage = message };

    public void Dispose() => Ticket?.Dispose();
}

/// <summary>准入票：持有互斥租约与 running 登记。Dispose 关闭互斥句柄（进程将退出，
/// 关句柄即释放——互斥体有线程亲和，不跨线程调用 ReleaseMutex）并删除自己的登记文件。</summary>
internal sealed class MachineAdmissionTicket : IDisposable
{
    private IMutexLease? _lease;
    private SuiteRegistration? _registration;

    public MachineAdmissionTicket(IMutexLease lease, SuiteRegistration registration)
    {
        _lease = lease;
        _registration = registration;
    }

    public void Dispose()
    {
        _lease?.Dispose();
        _lease = null;
        _registration?.Delete();
        _registration = null;
    }
}

/// <summary>准入策略全部常量单一来源。测试注入小时限；生产用 <see cref="Default"/>。</summary>
internal sealed record MachineAdmissionPolicy
{
    /// <summary>套件等待预算（互斥等待与外来等待共用：两者都是「等另一套件跑完」）。
    /// 依据：2026-11-03 本机全量实测 5.92 分钟墙钟（1238 例 0 失败，门内：空闲 ≥180s、
    /// 已装实例优雅关闭；批次 evidence/p0-full-suite-duration.log）×2 排队深度 ≈ 12 分钟
    /// &lt; 45 分钟下限 → 取下限 45 分钟（与旧网关口径连续，实测覆盖 ≥7 个全量排队深度）。</summary>
    public static readonly TimeSpan DefaultSuiteWaitLimit = TimeSpan.FromMinutes(45);

    public static MachineAdmissionPolicy Default(bool skipIdleAdmission) => new()
    {
        MutexName = @"Local\FloatingTransferStation.UiTests",
        ProductMutexName = SingleInstanceGuard.ApplicationMutexName,
        SuiteWaitLimit = DefaultSuiteWaitLimit,
        IdleMinimum = TimeSpan.FromSeconds(180),
        IdleWaitLimit = TimeSpan.FromMinutes(15),
        PollInterval = TimeSpan.FromSeconds(2),
        SkipIdleAdmission = skipIdleAdmission,
    };

    public required string MutexName { get; init; }
    public required string ProductMutexName { get; init; }
    public required TimeSpan SuiteWaitLimit { get; init; }
    public required TimeSpan IdleMinimum { get; init; }
    public required TimeSpan IdleWaitLimit { get; init; }
    public required TimeSpan PollInterval { get; init; }
    public required bool SkipIdleAdmission { get; init; }
}

// ---- 四个探针接口（D4：决策内核可测化；生产实现＋测试假件各一组） ----

/// <summary>命名互斥探针。测试用 GUID 名（严禁真实网关名——持锁中会自锁死）。</summary>
internal interface IMutexProbe
{
    IMutexLease OpenOrCreate(string name);
}

internal interface IMutexLease : IDisposable
{
    /// <summary>等待取得所有权；AbandonedMutexException 由调用方按「已取得」处理。</summary>
    bool WaitOne(TimeSpan timeout);
}

internal sealed class SystemMutexProbe : IMutexProbe
{
    public IMutexLease OpenOrCreate(string name) => new SystemMutexLease(new Mutex(initiallyOwned: false, name));

    private sealed class SystemMutexLease : IMutexLease
    {
        // 持静态引用防 GC 终结器提前关闭句柄（会误触发 abandoned 语义）。
        private Mutex? _mutex;

        public SystemMutexLease(Mutex mutex) => _mutex = mutex;

        public bool WaitOne(TimeSpan timeout) => _mutex!.WaitOne(timeout);

        public void Dispose()
        {
            _mutex?.Dispose();
            _mutex = null;
        }
    }
}

/// <summary>宿主机空闲探针（GetLastInputInfo：会话作用域的最后一次物理输入）。</summary>
internal interface IIdleProbe
{
    TimeSpan IdleElapsed { get; }
}

internal sealed class LastInputIdleProbe : IIdleProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    public TimeSpan IdleElapsed
    {
        get
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            return GetLastInputInfo(ref info)
                ? TimeSpan.FromMilliseconds(Environment.TickCount64 - info.dwTime)
                : TimeSpan.Zero;
        }
    }
}

/// <summary>外来套件候选（不含网关的 App.Tests testhost）。</summary>
/// <param name="Pid">进程 id。</param>
/// <param name="Name">进程名。</param>
/// <param name="Evidence">命中证据（模块路径或不可读说明）。</param>
internal sealed record ForeignSuiteCandidate(int Pid, string Name, string Evidence);

/// <summary>外来套件探针：枚举本机套件 testhost（观测原语——登记排除属准入决策，在内核侧）。
/// 命中口径（D3-4，与 scripts/clear-stale-ui-test-processes.ps1 的进程名族同源）＝进程名
/// testhost*/VSTestRunner* ∧ 已加载模块含 FloatingTransferStation.Tests.dll（套件 dll 名
/// token 的进程内等价形：实测本机 vstest 派生 testhost 的命令行携带 --runtimeconfig/
/// --depsfile 而非字面 .dll，见批次 evidence/p0-enumeration-visibility.log；Core.Tests 的
/// 模块名为 FloatingTransferStation.Core.Tests.dll，token 不同天然排除）。</summary>
internal interface IForeignSuiteProbe
{
    IReadOnlyList<ForeignSuiteCandidate> Enumerate(int selfPid);
}

internal sealed class LoadedModuleForeignSuiteProbe : IForeignSuiteProbe
{
    internal const string SuiteAssemblyFileName = "FloatingTransferStation.Tests.dll";

    public IReadOnlyList<ForeignSuiteCandidate> Enumerate(int selfPid)
    {
        var candidates = new List<ForeignSuiteCandidate>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == selfPid)
                {
                    continue;
                }

                var name = process.ProcessName;
                if (!name.StartsWith("testhost", StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith("VSTestRunner", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    foreach (ProcessModule module in process.Modules)
                    {
                        if (string.Equals(module.ModuleName, SuiteAssemblyFileName, StringComparison.OrdinalIgnoreCase))
                        {
                            candidates.Add(new ForeignSuiteCandidate(process.Id, name, module.FileName ?? ""));
                            break;
                        }
                    }
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // 模块表不可读（权限/竞态退出）：保守计入并可归因——宁可等待不可漏检。
                    candidates.Add(new ForeignSuiteCandidate(process.Id, name, "(模块表不可读)"));
                }
            }
        }

        return candidates;
    }
}

/// <summary>已装实例探针：产品单实例锁可打开＝实例正在运行（只读探测，不获取所有权）。</summary>
internal interface IInstalledInstanceProbe
{
    bool IsRunning();
}

internal sealed class ProductInstanceMutexProbe : IInstalledInstanceProbe
{
    public bool IsRunning() => Mutex.TryOpenExisting(SingleInstanceGuard.ApplicationMutexName, out _);
}

/// <summary>登记目录条目：&lt;pid&gt;.json（pid 是语义键，取自文件名；JSON 负载只做归因展示，
/// 损坏不参与判定）。创建即清扫死 pid 陈旧文件（机会式，D3）。</summary>
internal sealed class SuiteRegistration
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public int Pid { get; }
    public string FilePath { get; }

    private SuiteRegistration(int pid, string filePath)
    {
        Pid = pid;
        FilePath = filePath;
    }

    public static SuiteRegistration CreateWaiting(string directory)
    {
        Directory.CreateDirectory(directory);
        SweepStaleFiles(directory);
        var pid = Environment.ProcessId;
        var filePath = Path.Combine(directory, $"{pid}.json");
        Write(filePath, new RegistrationPayload(pid, "waiting", FindWorktreeRoot(), DateTime.UtcNow));
        return new SuiteRegistration(pid, filePath);
    }

    /// <summary>其他在册套件 pid（不含自己）。文件名即键：&lt;pid&gt;.json。</summary>
    public IReadOnlySet<int> OtherRegisteredPids()
    {
        var pids = new HashSet<int>();
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(FilePath) ?? ".", "*.json"))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            if (int.TryParse(stem, out var pid) && pid != Pid)
            {
                pids.Add(pid);
            }
        }

        return pids;
    }

    /// <summary>归因描述（互斥超时用）：其他在册套件的 pid/状态/根目录。</summary>
    public string DescribeOthers()
    {
        var lines = new List<string>();
        foreach (var pid in OtherRegisteredPids())
        {
            var path = Path.Combine(Path.GetDirectoryName(FilePath) ?? ".", $"{pid}.json");
            try
            {
                var payload = JsonSerializer.Deserialize<RegistrationPayload>(File.ReadAllText(path), JsonOptions);
                lines.Add($"pid={pid} state={payload?.State ?? "?"} root={payload?.Root ?? "?"}");
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                lines.Add($"pid={pid} (登记不可读)");
            }
        }

        return string.Join("; ", lines);
    }

    public void MarkRunning()
        => Write(FilePath, new RegistrationPayload(Pid, "running", FindWorktreeRoot(), DateTime.UtcNow));

    public void Delete()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (IOException)
        {
            // 删除失败只留陈旧登记：由后续套件的机会式清扫收走，不影响准入正确性。
        }
    }

    private static void Write(string filePath, RegistrationPayload payload)
    {
        var temporary = filePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(payload, JsonOptions));
        File.Move(temporary, filePath, overwrite: true);
    }

    private static void SweepStaleFiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out var pid) || ProcessExists(pid))
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // 下一轮机会式清扫再收。
            }
        }
    }

    private static bool ProcessExists(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            process.Dispose();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string FindWorktreeRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                || File.Exists(Path.Combine(directory.FullName, "FloatingTransferStation.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return AppContext.BaseDirectory;
    }

    private sealed record RegistrationPayload(int Pid, string State, string Root, DateTime StartedUtc);
}
