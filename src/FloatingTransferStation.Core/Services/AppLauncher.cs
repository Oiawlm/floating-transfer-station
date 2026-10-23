using System.ComponentModel;
using System.Diagnostics;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

/// <summary>启动结果状态（#30）：Launched=确认启动（成功脉冲）；Dispatched=请求已派发、
/// 结果未知（AUMID 限定回退，不播成功脉冲，收起按成功路径放行）；Failed=明确失败。</summary>
public enum LauncherLaunchStatus
{
    Launched,
    Dispatched,
    Failed
}

/// <summary>启动结果（#30）：失败携带用户可读原因（状态条前缀「无法启动：」）；
/// SuggestReenumerate=应用未注册类错误（0x80270254/0x80070490），触发打包应用重枚举。</summary>
public sealed record LauncherLaunchResult(
    LauncherLaunchStatus Status,
    string? FailureMessage = null,
    bool SuggestReenumerate = false)
{
    public static LauncherLaunchResult Launched { get; } = new(LauncherLaunchStatus.Launched);

    public static LauncherLaunchResult Dispatched { get; } = new(LauncherLaunchStatus.Dispatched);
}

/// <summary>打包应用激活结果（COM 实现在主 WPF 工程，Core 只见此形状）：
/// Activated=拿到 pid；失败时携带 HRESULT（AppLauncher 决定回退或明确失败）。</summary>
public sealed record PackagedAppActivationResult(bool Activated, int? ProcessId, int Hresult)
{
    public static PackagedAppActivationResult Success(int processId) => new(true, processId, 0);

    public static PackagedAppActivationResult Failure(int hresult) => new(false, null, hresult);
}

/// <summary>
/// 应用启动器（#30，Issue #162）：业务编排在 Core（UI-free），进程/COM 实现可注入
/// （UpdateService 先例）。exe 与 .lnk 都以 UseShellExecute 启动目标本体（.lnk 覆盖
/// MSI 广告快捷方式）；不 WaitForExit、不默认 Verb=runas；文件类目标点击前
/// File.Exists 预检。AUMID 激活失败仅对限定错误类别回退 explorer.exe
/// shell:AppsFolder（回退=已派发不显成功）；明确失败（未注册/拒绝/取消）不回退。
/// 系统位置（#30 扩展）直接派发注册表 CLSID 解析名，失败限定回退 explorer shell:
/// 别名（回收站/此电脑）；非注册表目标不回退。
/// </summary>
public sealed class AppLauncher
{
    /// <summary>
    /// AUMID 激活的限定回退类别（WASDK 打包应用已知不兼容类）：这些 HRESULT 下
    /// explorer.exe shell:AppsFolder 派发仍可行；明确失败类（未注册 0x80270254/
    /// 0x80070490、拒绝访问 0x80070005、取消 0x800704C7/1223）不回退、走失败反馈。
    /// </summary>
    private static readonly HashSet<int> PackagedActivationFallbackHresults = new(
    [
        unchecked((int)0x80070002), // ERROR_FILE_NOT_FOUND 映射：部分 WASDK 别名应用激活层找不到入口
        unchecked((int)0x80070003), // ERROR_PATH_NOT_FOUND 映射
        unchecked((int)0x80004005), // E_FAIL
        unchecked((int)0x8007000D), // ERROR_INVALID_DATA
        unchecked((int)0x8000FFFF)  // E_UNEXPECTED
    ]);

    private readonly Func<ProcessStartInfo, bool> _processStarter;
    private readonly Func<string, string?, PackagedAppActivationResult>? _packagedActivator;

    public AppLauncher(
        Func<ProcessStartInfo, bool>? processStarter = null,
        Func<string, string?, PackagedAppActivationResult>? packagedActivator = null)
    {
        _processStarter = processStarter ?? StartWithShellExecute;
        _packagedActivator = packagedActivator;
    }

    public LauncherLaunchResult Launch(LauncherItem item)
    {
        try
        {
            return item.Kind switch
            {
                LauncherKind.Application => LaunchApplication(item),
                LauncherKind.PackagedApp => LaunchPackagedApp(item),
                LauncherKind.Url => LaunchUrl(item),
                LauncherKind.ShellLocation => LaunchShellLocation(item),
                _ => throw new ArgumentOutOfRangeException(nameof(item), item.Kind, null)
            };
        }
        catch (Win32Exception exception)
        {
            return new LauncherLaunchResult(
                LauncherLaunchStatus.Failed,
                DescribeError(exception.NativeErrorCode));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or PlatformNotSupportedException or FileNotFoundException)
        {
            return new LauncherLaunchResult(
                LauncherLaunchStatus.Failed,
                exception.Message.Length > 0 ? exception.Message : "无法启动。");
        }
    }

    private LauncherLaunchResult LaunchApplication(LauncherItem item)
    {
        if (!File.Exists(item.TargetPath))
        {
            return new LauncherLaunchResult(LauncherLaunchStatus.Failed, "目标不存在。");
        }

        var info = new ProcessStartInfo(item.TargetPath)
        {
            Arguments = item.Arguments ?? string.Empty,
            UseShellExecute = true
        };
        if (!string.IsNullOrWhiteSpace(item.WorkingDirectory))
        {
            info.WorkingDirectory = item.WorkingDirectory;
        }
        else if (Path.GetDirectoryName(item.TargetPath) is { Length: > 0 } directory)
        {
            info.WorkingDirectory = directory;
        }

        return _processStarter(info)
            ? LauncherLaunchResult.Launched
            : new LauncherLaunchResult(LauncherLaunchStatus.Failed, "无法启动。");
    }

    private LauncherLaunchResult LaunchPackagedApp(LauncherItem item)
    {
        if (_packagedActivator is null)
        {
            return new LauncherLaunchResult(LauncherLaunchStatus.Failed, "打包应用激活不可用。");
        }

        var aumid = item.TargetPath;
        var result = _packagedActivator(aumid, item.Arguments);
        if (result.Activated)
        {
            return LauncherLaunchResult.Launched;
        }

        if (IsApplicationNotRegistered(result.Hresult))
        {
            return new LauncherLaunchResult(
                LauncherLaunchStatus.Failed,
                "应用未注册（可能已卸载）。",
                SuggestReenumerate: true);
        }

        if (PackagedActivationFallbackHresults.Contains(result.Hresult))
        {
            // 限定回退：explorer.exe shell:AppsFolder 派发——请求已派发、结果未知，
            // 不播成功脉冲，状态条「已尝试启动」，收起按成功路径放行。
            var dispatched = DispatchViaExplorer($"shell:AppsFolder\\{aumid}");
            return dispatched
                ? LauncherLaunchResult.Dispatched
                : new LauncherLaunchResult(
                    LauncherLaunchStatus.Failed,
                    DescribeError(result.Hresult));
        }

        return new LauncherLaunchResult(LauncherLaunchStatus.Failed, DescribeError(result.Hresult));
    }

    private LauncherLaunchResult LaunchUrl(LauncherItem item)
    {
        // Url 条目不进文件路径规范化/失联预检（UseShellExecute 对 URL 天然有效）。
        var info = new ProcessStartInfo(item.TargetPath)
        {
            Arguments = item.Arguments ?? string.Empty,
            UseShellExecute = true
        };
        return _processStarter(info)
            ? LauncherLaunchResult.Launched
            : new LauncherLaunchResult(LauncherLaunchStatus.Failed, "无法打开。");
    }

    /// <summary>系统位置（#30 扩展）：UseShellExecute 直接派发 CLSID 解析名（无文件
    /// 预检、无工作目录）；失败限定回退注册表登记的 explorer shell: 别名——回退=已
    /// 派发不显成功（与 AUMID 回退同一语义）；非注册表目标不回退。</summary>
    private LauncherLaunchResult LaunchShellLocation(LauncherItem item)
    {
        var info = new ProcessStartInfo(item.TargetPath) { UseShellExecute = true };
        if (_processStarter(info))
        {
            return LauncherLaunchResult.Launched;
        }

        if (SystemLauncherLocations.TryGetExplorerFallbackArgument(item.TargetPath) is not { } fallback)
        {
            return new LauncherLaunchResult(LauncherLaunchStatus.Failed, "无法打开。");
        }

        return DispatchViaExplorer(fallback)
            ? LauncherLaunchResult.Dispatched
            : new LauncherLaunchResult(LauncherLaunchStatus.Failed, "无法打开。");
    }

    /// <summary>explorer.exe shell: 参数派发（打包应用与系统位置的限定回退唯一通道）；
    /// 打包应用绝不直接启动其 exe。</summary>
    internal bool DispatchViaExplorer(string shellArguments)
    {
        try
        {
            var info = new ProcessStartInfo("explorer.exe")
            {
                Arguments = shellArguments,
                UseShellExecute = true
            };
            return _processStarter(info);
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    internal static bool IsApplicationNotRegistered(int hresult) =>
        hresult == unchecked((int)0x80270254) || hresult == unchecked((int)0x80070490);

    /// <summary>错误文案映射（#30）：Win32 错误码/HRESULT → 中文短语；未映射带十六进制码。</summary>
    public static string DescribeError(int code) => code switch
    {
        2 or unchecked((int)0x80070002) => "目标不存在。",
        3 or unchecked((int)0x80070003) => "路径不存在。",
        5 or unchecked((int)0x80070005) => "拒绝访问。",
        740 or unchecked((int)0x800710E0) => "需要管理员权限。",
        1223 or unchecked((int)0x800704C7) => "已取消授权。",
        31 or 1155 => "没有找到能打开该地址的应用。",
        unchecked((int)0x80270254) or unchecked((int)0x80070490) => "应用未注册（可能已卸载）。",
        _ => $"错误 0x{code:X8}。"
    };

    private static bool StartWithShellExecute(ProcessStartInfo info)
    {
        using var process = Process.Start(info);
        return process is not null;
    }
}
