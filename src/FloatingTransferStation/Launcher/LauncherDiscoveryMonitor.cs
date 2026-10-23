using System.IO;
using System.Windows.Threading;

namespace FloatingTransferStation.Launcher;

/// <summary>
/// 启动·自动发现文件监视（#45）：每监视根一个 FileSystemWatcher（开始菜单两根看
/// *.lnk；自定义文件夹加看 *.exe；IncludeSubdirectories；InternalBufferSize 调大，
/// Error 事件兜底触发重扫）＋约 500ms 防抖聚合（照 LocalStore.DailyReviews 的去抖
/// 形态，聚合为单一 UI 线程 DispatcherTimer——事件静默 500ms 后触发一次重扫）。
/// 不开新常驻线程：FSW 事件线程池→封送 UI 重启防抖表；重扫由窗口层 Task 链后台化。
/// 监视根集合随设置变更整体重建（Restart）。
/// </summary>
internal sealed class LauncherDiscoveryMonitor : IDisposable
{
    private static readonly TimeSpan RescanDebounceInterval = TimeSpan.FromMilliseconds(500);

    private readonly Dispatcher _uiDispatcher;
    private readonly Action _triggerRescan;
    private readonly DispatcherTimer _debounceTimer;
    private readonly object _watchersLock = new();
    private List<FileSystemWatcher> _watchers = [];

    public LauncherDiscoveryMonitor(Dispatcher uiDispatcher, Action triggerRescan)
    {
        _uiDispatcher = uiDispatcher;
        _triggerRescan = triggerRescan;
        _debounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = RescanDebounceInterval
        };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            _triggerRescan();
        };
    }

    /// <summary>按当前监视根重建（设置变更/开关切换即整组换装）：开始菜单两根恒在，
    /// 自定义文件夹跟随偏好；不存在/不可访问的根静默跳过（扫描端同样容忍）。</summary>
    public void Restart(IReadOnlyList<string> customFolders)
    {
        var roots = new List<(string Path, bool IncludeExecutables)>();
        foreach (var root in StartMenuRoots())
        {
            roots.Add((root, IncludeExecutables: false));
        }

        var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in customFolders)
        {
            if (!string.IsNullOrWhiteSpace(folder) && seenFolders.Add(folder))
            {
                roots.Add((folder, IncludeExecutables: true));
            }
        }

        List<FileSystemWatcher> fresh = [];
        foreach (var (path, includeExecutables) in roots)
        {
            if (!Directory.Exists(path))
            {
                continue;
            }

            try
            {
                var watcher = new FileSystemWatcher(path)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    EnableRaisingEvents = true
                };
                watcher.Filters.Add("*.lnk");
                if (includeExecutables)
                {
                    watcher.Filters.Add("*.exe");
                }

                watcher.Created += OnWatcherActivity;
                watcher.Changed += OnWatcherActivity;
                watcher.Deleted += OnWatcherActivity;
                watcher.Renamed += OnWatcherActivity;
                watcher.Error += OnWatcherActivity;
                fresh.Add(watcher);
            }
            catch (Exception exception) when (
                exception is IOException or ArgumentException or NotSupportedException)
            {
                // 根不可监听（权限/形状）：跳过该根，其余照常；30 分钟兜底重扫兜底。
            }
        }

        List<FileSystemWatcher> old;
        lock (_watchersLock)
        {
            old = _watchers;
            _watchers = fresh;
        }

        foreach (var watcher in old)
        {
            DisposeWatcher(watcher);
        }
    }

    public void Stop()
    {
        List<FileSystemWatcher> old;
        lock (_watchersLock)
        {
            old = _watchers;
            _watchers = [];
        }

        foreach (var watcher in old)
        {
            DisposeWatcher(watcher);
        }

        _uiDispatcher.BeginInvoke(() => _debounceTimer.Stop());
    }

    public void Dispose() => Stop();

    /// <summary>开始菜单两根（与扫描器同源）。</summary>
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

    /// <summary>任何监视事件（含 Error 兜底）＝有变化可能性：封送 UI 重启防抖表，
    /// 静默期后触发一次重扫（差分幂等，误触发零成本）。单一 (object, EventArgs)
    /// 签名经参数逆变绑定五类事件（Created/Changed/Deleted/Renamed/Error）。</summary>
    private void OnWatcherActivity(object sender, EventArgs e) =>
        _uiDispatcher.BeginInvoke(() =>
        {
            // Stop 对运行中的 DispatcherTimer 是空操作；重启=完整新一轮静默期。
            _debounceTimer.Stop();
            _debounceTimer.Start();
        });

    private void DisposeWatcher(FileSystemWatcher watcher)
    {
        watcher.EnableRaisingEvents = false;
        watcher.Created -= OnWatcherActivity;
        watcher.Changed -= OnWatcherActivity;
        watcher.Deleted -= OnWatcherActivity;
        watcher.Renamed -= OnWatcherActivity;
        watcher.Error -= OnWatcherActivity;
        watcher.Dispose();
    }
}
