using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatingTransferStation.Launcher;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Views;

/// <summary>
/// 启动·自动发现窗口接线（#45）：Core 差分服务（LauncherDiscoveryService，注入
/// 候选源）＋LauncherDiscoveryMonitor（每监视根一个 FSW＋500ms 防抖聚合）＋
/// UI 线程 DispatcherTimer 30 分钟兜底全量重扫＋启动后延迟首扫（避开启动忙时）；
/// 重扫单飞守卫照 AutoCleanup 范式。扫描重活承载在图标管线 STA 专线程（Shell COM
/// 必须 STA，复用既有线程＝不开新常驻线程）。设置开关/文件夹变更即时重建监视源
/// （契约 #5 唯一管线 ApplyPreferences）。确认制入口＝工具行「发现 N」按钮（仅
/// pending&gt;0 可见，恒定几何预留格），采纳喂既有 ImportLauncherFilesAsync/
/// ImportInstalledAppsAsync 归一化管线（不另造导入逻辑），「全部忽略」→ dismissed。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>兜底全量重扫节奏（FSW 事件丢失/休眠漂移由它收敛）。</summary>
    private static readonly TimeSpan LauncherDiscoveryFallbackInterval = TimeSpan.FromMinutes(30);

    /// <summary>启动后延迟首扫：避开启动忙时（装载/图标提取/更新检查）。</summary>
    private static readonly TimeSpan LauncherDiscoveryFirstScanDelay = TimeSpan.FromSeconds(10);

    private LauncherDiscoveryService? _launcherDiscovery;
    private LauncherDiscoveryMonitor? _launcherDiscoveryMonitor;
    private Func<Task<IReadOnlyList<LauncherDiscoveryEntry>>>? _launcherDiscoverySource;
    private DispatcherTimer? _launcherDiscoveryFallbackTimer;
    private DispatcherTimer? _launcherDiscoveryFirstScanTimer;
    private bool _isLauncherDiscoveryScanInFlight;

    /// <summary>AppsFolder 差分源（默认每次直调真实枚举——绝不进选择器的会话缓存，
    /// 否则会话内 Store 新装发现不了；测试可注入假源）。</summary>
    private Func<IReadOnlyList<InstalledApp>> _launcherDiscoveryAppsSource = InstalledAppsEnumerator.Enumerate;

    /// <summary>发现清单选择器显示（测试缝：同 _showPickerDialog）。</summary>
    internal Func<MultiItemPickerWindow, bool?> _showDiscoveryPickerDialog = static picker => picker.ShowDialog();

    /// <summary>启动装载完成后的初始化（图标 STA 线程就绪为前提；QuickLaunch 装载
    /// 尾段调用）。装载失败不调用 → 本会话发现面静默缺位（按钮永不出现）。</summary>
    private async Task InitializeLauncherDiscoveryAsync(ILauncherDiscoveryStore discoveryStore)
    {
        var discovery = await LauncherDiscoveryService.LoadAsync(
            discoveryStore,
            ShowStatus,
            action => Dispatcher.Invoke(action));
        await Dispatcher.InvokeAsync(() =>
        {
            _launcherDiscovery = discovery;
            discovery.PendingChanged += (_, _) => UpdateLauncherDiscoveryButton();
            // ??＝尊重测试缝先于初始化完成的注入（初始化是异步块，seam 可能先到）。
            _launcherDiscoverySource ??= BuildLauncherDiscoverySource;
            _launcherDiscoveryMonitor = new LauncherDiscoveryMonitor(Dispatcher, RunLauncherDiscoveryScan);
            _launcherDiscoveryFallbackTimer = new DispatcherTimer
            {
                Interval = LauncherDiscoveryFallbackInterval
            };
            _launcherDiscoveryFallbackTimer.Tick += (_, _) => RunLauncherDiscoveryScan();
            _launcherDiscoveryFirstScanTimer = new DispatcherTimer
            {
                Interval = LauncherDiscoveryFirstScanDelay
            };
            _launcherDiscoveryFirstScanTimer.Tick += (_, _) =>
            {
                _launcherDiscoveryFirstScanTimer?.Stop();
                RunLauncherDiscoveryScan();
            };

            UpdateLauncherDiscoveryButton();
            if (_preferences.LauncherDiscoveryEnabled)
            {
                // 监视立即布防（首扫延迟窗内的新装由 FSW 防抖补扫）；首扫延迟启动。
                _launcherDiscoveryMonitor.Restart(CurrentLauncherDiscoveryFolders(_preferences));
                _launcherDiscoveryFirstScanTimer.Start();
                _launcherDiscoveryFallbackTimer.Start();
            }
        });
    }

    /// <summary>设置应用（ApplyPreferences 唯一管线调用；构造期发现面未就绪时 no-op，
    /// 初始化尾段自行布防）。开关/文件夹变更＝即时重建监视源；运行中变更触发立即
    /// 重扫（契约 #5 即时生效）。</summary>
    private void ApplyLauncherDiscoveryPreference(AppPreferences preferences)
    {
        if (_launcherDiscovery is null || _launcherDiscoveryMonitor is null)
        {
            return;
        }

        if (!preferences.LauncherDiscoveryEnabled)
        {
            _launcherDiscoveryMonitor.Stop();
            _launcherDiscoveryFirstScanTimer?.Stop();
            _launcherDiscoveryFallbackTimer?.Stop();
            UpdateLauncherDiscoveryButton();
            return;
        }

        _launcherDiscoveryMonitor.Restart(CurrentLauncherDiscoveryFolders(preferences));
        _launcherDiscoveryFallbackTimer?.Start();
        RunLauncherDiscoveryScan();
    }

    /// <summary>重扫单入口漏斗：关闭/未就绪/开关关/在途 → 放弃（单飞守卫照
    /// AutoCleanup 范式）；经 TrackPendingOperation 追踪（关闭序列会等待在途扫描）。</summary>
    private void RunLauncherDiscoveryScan()
    {
        if (_isClosing ||
            _launcherDiscovery is null ||
            _launcherDiscoverySource is null ||
            _launchers is null ||
            !_preferences.LauncherDiscoveryEnabled ||
            _isLauncherDiscoveryScanInFlight)
        {
            return;
        }

        _isLauncherDiscoveryScanInFlight = true;
        TrackPendingOperation(ScanLauncherDiscoverySafelyAsync());
    }

    private async Task ScanLauncherDiscoverySafelyAsync()
    {
        try
        {
            var discovery = _launcherDiscovery;
            var source = _launcherDiscoverySource;
            var catalog = _launchers;
            if (discovery is null || source is null || catalog is null)
            {
                return;
            }

            await discovery.RescanAsync(source, [.. catalog.Items], DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 差分幂等：本次放弃零成本，30 分钟兜底重扫自然重试；保存失败状态条
            // 已由服务层承载。
        }
        finally
        {
            _isLauncherDiscoveryScanInFlight = false;
        }
    }

    /// <summary>生产候选源：三个发现源全程承载在图标 STA 专线程（.lnk 解析与
    /// AppsFolder 枚举同为 Shell COM；文件枚举同乘一趟，不开新线程）。</summary>
    private async Task<IReadOnlyList<LauncherDiscoveryEntry>> BuildLauncherDiscoverySource()
    {
        var folders = CurrentLauncherDiscoveryFolders(_preferences);
        var appsSource = _launcherDiscoveryAppsSource;
        var icons = _launcherIcons ?? throw new InvalidOperationException("发现扫描依赖图标 STA 线程。");
        return await icons.InvokeOnStaAsync(
            () => LauncherDiscoveryScanner.Scan(folders, appsSource, DateTimeOffset.UtcNow));
    }

    /// <summary>「发现 N」按钮唯一更新点：仅 pending&gt;0 ∧ 开关开启可见；文案携带
    /// 计数。隐藏走可见性塌缩，工具行预留格恒定（＋添加零漂移）。</summary>
    private void UpdateLauncherDiscoveryButton()
    {
        if (LauncherDiscoveryButton is null)
        {
            return;
        }

        var pending = _launcherDiscovery?.PendingCount ?? 0;
        LauncherDiscoveryButton.SetCurrentValue(
            ContentControl.ContentProperty,
            pending > 0 ? $"发现 {pending}" : null);
        LauncherDiscoveryButton.SetCurrentValue(
            VisibilityProperty,
            pending > 0 && _preferences.LauncherDiscoveryEnabled ? Visibility.Visible : Visibility.Collapsed);
    }

    private void LauncherDiscoveryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosing ||
            _launchers is null ||
            _launchers.IsReadOnlyFromNewerVersion ||
            _launcherDiscovery is not { PendingCount: > 0 } discovery)
        {
            return;
        }

        var pending = discovery.PendingSnapshot();
        var catalogKeys = _launchers.Items.Select(LauncherCatalog.DedupKey).ToHashSet(StringComparer.Ordinal);
        var rows = pending
            .Select(entry => new MultiItemPickerWindow.PickerRow(
                entry,
                entry.DisplayName,
                entry.TargetPath,
                alreadyAdded: catalogKeys.Contains(LauncherCatalog.DedupKeyFor(entry.TargetPath, entry.Arguments))))
            .ToList();
        var picker = new MultiItemPickerWindow(
            CurrentTheme,
            "发现的新应用",
            "搜索应用",
            "当前没有待确认的发现项",
            rows,
            bulkActionText: "全部忽略")
        { Owner = this };
        _panelState.BeginOverlayInteraction(PanelOverlayReason.ModalDialog);
        try
        {
            if (_showDiscoveryPickerDialog(picker) != true)
            {
                return;
            }

            if (picker.BulkActionChosen)
            {
                TrackPendingOperation(discovery.DismissAllAsync());
                return;
            }

            if (picker.Selection.OfType<LauncherDiscoveryEntry>().ToList() is not { Count: > 0 } selection)
            {
                return;
            }

            TrackPendingOperation(AdoptLauncherDiscoveriesAsync(discovery, selection));
        }
        finally
        {
            EndOverlayInteractionAndReconcile(PanelOverlayReason.ModalDialog);
        }
    }

    /// <summary>采纳：按 Kind 分流喂入既有导入管线（Application=文件路径→文件导入；
    /// PackagedApp=AUMID→已安装应用导入——同一归一化/去重/上限/落位/状态条路径，
    /// 不另造导入逻辑），完成后从 pending 移除（进入启动目录的事实抑制重发现）。</summary>
    private async Task AdoptLauncherDiscoveriesAsync(
        LauncherDiscoveryService discovery,
        IReadOnlyList<LauncherDiscoveryEntry> selection)
    {
        var files = selection
            .Where(entry => entry.Kind == LauncherKind.Application)
            .Select(entry => entry.TargetPath)
            .ToList();
        var apps = selection
            .Where(entry => entry.Kind == LauncherKind.PackagedApp)
            .Select(entry => new InstalledApp(entry.TargetPath, entry.DisplayName))
            .ToList();
        if (files.Count > 0)
        {
            await ImportLauncherFilesAsync(files, skippedUnsupportedCount: 0);
        }

        if (apps.Count > 0)
        {
            await ImportInstalledAppsAsync(apps);
        }

        await discovery.AdoptAsync(selection);
    }

    /// <summary>监视文件夹偏好清洗：空白剔除＋大小写不敏感去重（保序）。</summary>
    private static IReadOnlyList<string> CurrentLauncherDiscoveryFolders(AppPreferences preferences) =>
        preferences.LauncherDiscoveryFolders is { Count: > 0 } folders
            ? folders
                .Where(folder => !string.IsNullOrWhiteSpace(folder))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

    internal void UseLauncherDiscoverySourceForTests(
        Func<Task<IReadOnlyList<LauncherDiscoveryEntry>>>? source) =>
        _launcherDiscoverySource = source ?? BuildLauncherDiscoverySource;

    /// <summary>测试直驱重扫漏斗（与 FSW 防抖/30 分钟兜底/设置变更同一入口）。</summary>
    internal void RunLauncherDiscoveryScanForTests() => RunLauncherDiscoveryScan();

    internal void UseLauncherDiscoveryAppsSourceForTests(Func<IReadOnlyList<InstalledApp>> source) =>
        _launcherDiscoveryAppsSource = source;

    internal LauncherDiscoveryService? LauncherDiscoveryForTests() => _launcherDiscovery;

    private void DisposeLauncherDiscovery()
    {
        _launcherDiscoveryFallbackTimer?.Stop();
        _launcherDiscoveryFirstScanTimer?.Stop();
        _launcherDiscoveryMonitor?.Dispose();
        _launcherDiscoveryMonitor = null;
    }
}
