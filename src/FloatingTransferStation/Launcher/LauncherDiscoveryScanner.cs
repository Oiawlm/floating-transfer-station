using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Launcher;

/// <summary>
/// 启动·自动发现扫描适配器（#45）：把三个发现源映射为 Core 候选——
/// ①开始菜单（用户＋公共两根，递归 .lnk，业界主流主源）；②用户指定文件夹
/// （.lnk 全收；.exe 经启发式黑名单过滤——安装器常产卸载器/更新器等非主程序
/// 可执行文件）；③AppsFolder AUMID 差分（Store 新装，复用 InstalledAppsEnumerator
/// 经注入源**每次直调**——选择器的会话缓存不得进发现链路，否则会话内新装发现不了）。
/// 本扫描承载全部 Shell COM（.lnk 解析经 ShellLinkHelper、AppsFolder 枚举），
/// 由调用方派发到图标管线 STA 专线程执行（LauncherIconService.InvokeOnStaAsync）。
/// 候选＝.lnk/.exe 路径本体或 AUMID（采纳＝喂既有导入管线，与手动添加同一形态）。
/// </summary>
internal static class LauncherDiscoveryScanner
{
    /// <summary>非主程序 exe 文件名黑名单前缀（RESEARCH §5，Flow Launcher/PowerToys
    /// Run 同款启发式）：卸载器/安装器/更新器/崩溃报告器/宿主辅助进程/提权垫片。</summary>
    private static readonly string[] AuxiliaryExecutablePrefixes =
        ["uninstall", "unins", "setup", "update", "updater", "crashpad", "helper", "elevate"];

    internal static IReadOnlyList<LauncherDiscoveryEntry> Scan(
        IReadOnlyList<string> customFolders,
        Func<IReadOnlyList<InstalledApp>> appsSource,
        DateTimeOffset nowUtc)
    {
        var candidates = new List<LauncherDiscoveryEntry>();
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in StartMenuRoots())
        {
            CollectShortcutFiles(root, customFolder: false, nowUtc, seenTargets, candidates);
        }

        foreach (var folder in customFolders)
        {
            CollectShortcutFiles(folder, customFolder: true, nowUtc, seenTargets, candidates);
        }

        foreach (var app in appsSource())
        {
            if (string.IsNullOrWhiteSpace(app.Aumid) ||
                !seenTargets.Add(app.Aumid))
            {
                continue;
            }

            candidates.Add(new LauncherDiscoveryEntry(
                LauncherKind.PackagedApp,
                app.Aumid,
                Arguments: null,
                app.DisplayName,
                nowUtc));
        }

        return candidates;
    }

    /// <summary>开始菜单两根（用户＋公共）；环境缺失/重复（同一路径两常量）去重。</summary>
    private static IEnumerable<string> StartMenuRoots()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (!string.IsNullOrWhiteSpace(root) && seen.Add(root))
            {
                yield return root;
            }
        }
    }

    /// <summary>递归收集一个根下的候选：.lnk 全收（显示名=描述/文件名，经
    /// ShellLinkHelper 解析——STA 承载）；自定义文件夹追加 .exe（黑名单过滤）。</summary>
    private static void CollectShortcutFiles(
        string root,
        bool customFolder,
        DateTimeOffset nowUtc,
        HashSet<string> seenTargets,
        List<LauncherDiscoveryEntry> candidates)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }

        CollectByExtension(root, "*.lnk", includeAll: true, nowUtc, seenTargets, candidates);
        if (customFolder)
        {
            CollectByExtension(root, "*.exe", includeAll: false, nowUtc, seenTargets, candidates);
        }
    }

    private static void CollectByExtension(
        string root,
        string pattern,
        bool includeAll,
        DateTimeOffset nowUtc,
        HashSet<string> seenTargets,
        List<LauncherDiscoveryEntry> candidates)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return;
        }

        // 枚举是延迟执行的：子目录不可访问/重解析点异常在 MoveNext 上抛——逐批
        // 容错（已收集部分保留，剩余跳过；30 分钟兜底重扫再收敛）。
        using var enumerator = files.GetEnumerator();
        while (true)
        {
            string file;
            try
            {
                if (!enumerator.MoveNext())
                {
                    break;
                }

                file = enumerator.Current;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                break;
            }

            if (!includeAll && IsAuxiliaryExecutable(file))
            {
                continue;
            }

            if (!seenTargets.Add(file))
            {
                continue;
            }

            // 显示名与手动导入同源（.lnk 描述优先，回退文件名去扩展，≤64 截断）。
            candidates.Add(new LauncherDiscoveryEntry(
                LauncherKind.Application,
                file,
                Arguments: null,
                ShellLinkHelper.DefaultDisplayName(file),
                nowUtc));
        }
    }

    /// <summary>黑名单判定按文件名（去扩展）前缀、大小写不敏感。</summary>
    private static bool IsAuxiliaryExecutable(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        foreach (var prefix in AuxiliaryExecutablePrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
