using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;

namespace FloatingTransferStation.Views;

/// <summary>
/// 检查更新与静默自动更新（契约 #26，2026-11-03 修订 Issue #275）：头部按钮行最左
/// 常驻「更新」按钮——任何检查（手动或自动）发现新版本即立即自动续接完整静默更新流
///（下载→SHA256 校验→排空保存管线→静默安装→优雅退出→安装器 [Run] 自动重启），
/// 无需第二击；无更新点击=手动检查就地反馈。自动检查节奏=单一「下次检查到期」
/// 调度状态：30 秒首检 + 24 小时周期 + 2 小时交互空闲三触发统一（Core
/// UpdateCheckScheduler 承载，交互记账汇入既有指针挂点），设置「通用」节可关
///（关=停调度、开=恢复）；自动检查的任何失败完全静默，手动检查失败经状态条
/// 提示一次。检查与下载全程单飞防重入；按钮状态唯一派生自版本比较结果，下载
/// 失败回到可用态仍点亮可重试。更新过程仅状态条反馈（下载/校验/安装进度提示），
/// 无弹窗、无 toast。
/// </summary>
public partial class MainWindow : Window
{
    private static readonly TimeSpan UpdateFirstCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);
    // 交互空闲触发阈值（用户 2026-11-03 决策，Issue #275）：对本应用窗口持续
    // ≥2 小时无鼠标进入/指针活动 → 本空闲段内触发一次检查（受单飞与既有节流约束）。
    private static readonly TimeSpan UpdateIdleCheckInterval = TimeSpan.FromHours(2);
    // 安装包落盘目录：更新专用的 %TEMP% 子目录；同版本旧文件与半成品的清理在
    // 服务侧下载前执行，这里只提供位置。
    private static readonly string UpdateDownloadDirectory =
        Path.Combine(Path.GetTempPath(), "FloatingTransferStation", "Update");

    private readonly IUpdateService _updateService;
    private readonly DispatcherTimer _updateCheckTimer = new();
    private readonly Func<DateTimeOffset> _updateClock;
    private readonly UpdateCheckScheduler _updateScheduler;
    private bool _isUpdateCheckInFlight;
    private bool _isUpdateDownloadInFlight;
    private bool _assetMissingBrowserOpened;
    private UpdateCheckResult? _availableUpdate;

    private void InitializeUpdates()
    {
        _updateCheckTimer.Tick += (_, _) => UpdateCheckTimer_Tick();
        // 初始态文案同样经双态派生（单一来源，XAML 只作占位）。
        UpdateUpdateButtonState();
    }

    /// <summary>
    /// 启动/恢复自动检查调度（偏好开关的唯一执行点，SourceInitialized 与
    /// ApplyPreferences 共用）：三触发共用同一「下次检查到期」状态——首检未派发过
    /// 则从 30 秒延迟首检开始，否则按周期/空闲到期恢复（关停期间错过的到期在
    /// 重开时立即补跑）；开=按到期重排定时器，关=停表。
    /// </summary>
    private void ApplyUpdateCheckPreference(bool enabled)
    {
        if (enabled)
        {
            if (_isClosing || _updateCheckTimer.IsEnabled)
            {
                return;
            }

            _updateScheduler.Activate();
            ScheduleNextUpdateCheck();
        }
        else
        {
            _updateCheckTimer.Stop();
        }
    }

    /// <summary>按调度状态把定时器重排到下一次「检查到期」（唯一到期状态，
    /// 无平行定时器；间隔整秒向上取整、过期到期按下限 1 秒立即重评）。</summary>
    private void ScheduleNextUpdateCheck()
    {
        _updateCheckTimer.Interval = _updateScheduler.NextDueDelay(_updateClock());
        _updateCheckTimer.Start();
    }

    /// <summary>
    /// 调度定时器 tick：先过单飞守卫（在途检查/下载挤掉时保留待办、按 30 秒节奏
    /// 补跑——不被丢进 24 小时），再核到期（空闲到期被交互后移时只重排不检查），
    /// 到期即派发一次检查。
    /// </summary>
    private void UpdateCheckTimer_Tick()
    {
        if (_isUpdateCheckInFlight || _isUpdateDownloadInFlight)
        {
            _updateCheckTimer.Interval = UpdateFirstCheckDelay;
            return;
        }

        if (!_updateScheduler.IsCheckDue(_updateClock()))
        {
            ScheduleNextUpdateCheck();
            return;
        }

        RunScheduledUpdateCheck();
    }

    /// <summary>派发一次自动检查（三触发统一的唯一执行入口）：通过单飞守卫后即把
    /// 周期推进 24 小时并消费本空闲段的空闲触发臂（三触发不重复检查），随后按新
    /// 到期重排定时器。</summary>
    private void RunScheduledUpdateCheck()
    {
        if (_isUpdateCheckInFlight || _isUpdateDownloadInFlight)
        {
            return;
        }

        _updateScheduler.NoteCheckDispatched();
        _isUpdateCheckInFlight = true;
        TrackPendingOperation(RunScheduledCheckSafelyAsync());
        ScheduleNextUpdateCheck();
    }

    private async Task RunScheduledCheckSafelyAsync()
    {
        try
        {
            var result = await _updateService.CheckForUpdateAsync(_windowOperationCancellation.Token);
            await Dispatcher.InvokeAsync(
                () => AdoptCheckResult(result, isManual: false));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // 自动检查的任何失败完全静默（结果记录外的一切异常同按静默降级处理）。
        }
        finally
        {
            _isUpdateCheckInFlight = false;
        }
    }

    /// <summary>
    /// 用户交互记账（空闲触发）：主窗与子表面既有的指针进入挂点（根 MouseEnter、
    /// 标签 MouseEnter、头部动作区、启动瓦片——与面板悬停展开模型融合，不设平行
    /// 钩子堆叠）统一汇入调度器：刷新「最后交互时间」并重挂空闲触发臂。交互只会
    /// 把空闲到期后移，故仅在调度运行中时按新到期重排定时器；偏好关闭时照常记账。
    /// </summary>
    private void NoteUserInteraction()
    {
        _updateScheduler.NoteUserInteraction();
        if (_updateCheckTimer.IsEnabled)
        {
            ScheduleNextUpdateCheck();
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_isClosing || _isUpdateCheckInFlight || _isUpdateDownloadInFlight)
        {
            return;
        }

        if (_availableUpdate is null)
        {
            // 无已知更新：单动作「检查＋自动续接安装」——检查发现新版即在采纳
            // 结果里直接进入完整静默更新流（契约 #26 修订，无第二击）。
            await RunManualCheckAsync();
        }
        else
        {
            // 已知更新（如下载失败后回到可用态）：单动作重试完整更新流。
            await RunUpdateFlowAsync();
        }
    }

    private async Task RunManualCheckAsync()
    {
        _isUpdateCheckInFlight = true;
        ShowStatus("正在检查更新…");
        try
        {
            var result = await _updateService.CheckForUpdateAsync(_windowOperationCancellation.Token);
            AdoptCheckResult(result, isManual: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // async void 的未知异常会击穿为进程崩溃：手动路径就地降级反馈。
            ShowStatus("检查更新失败，请稍后重试。");
        }
        finally
        {
            _isUpdateCheckInFlight = false;
        }
    }

    /// <summary>
    /// 检查结果采纳（契约 #26 2026-11-03 修订）：发现新版本 → 点亮按钮并立即自动
    /// 续接完整静默更新流（无第二击）；无更新/失败按手动与否决定是否提示，无更新
    /// 同时熄灭按钮（远端撤回发布时按钮状态仍由版本比较派生）。AssetMissing（精确
    /// 名资产或校验清单缺失）仅在手动检查时经状态条提示并回退发布页（自动检查的
    /// 任何失败完全静默，零操作不得弹浏览器）；浏览器回退每会话至多一次，防自动
    /// 节奏反复打扰。
    /// </summary>
    private void AdoptCheckResult(UpdateCheckResult result, bool isManual)
    {
        if (!result.IsSuccess)
        {
            if (isManual)
            {
                ShowStatus($"检查更新失败：{result.ErrorMessage}");
            }

            return;
        }

        if (!result.HasUpdate)
        {
            if (_availableUpdate is not null)
            {
                _availableUpdate = null;
                UpdateUpdateButtonState();
            }

            if (isManual)
            {
                ShowStatus("已是最新版本。");
            }

            return;
        }

        if (result.Asset is null || result.Checksums is null)
        {
            if (isManual)
            {
                ShowStatus(
                    $"新版本 {result.LatestVersion} 暂不支持自动更新，已在浏览器打开发布页，可手动下载安装。");
                if (!_assetMissingBrowserOpened)
                {
                    _assetMissingBrowserOpened = true;
                    OpenReleasePage(result.ReleaseUrl);
                }
            }

            return;
        }

        _availableUpdate = result;
        UpdateUpdateButtonState();
        // 检查与安装直连：任何检查发现新版本即自动进入完整静默更新流——每次发现
        // 严格更新的检查都重新尝试更新流（更新流自身单飞守卫，同版本在途不重入）。
        BeginUpdateFlow();
    }

    /// <summary>自动续接完整更新流的派发入口（检查采纳与按钮重试共用）：更新流在途
    /// 时单飞跳过；受控发射（不追踪进待排空集合——更新流自身的二次排空会 WhenAll
    /// 等待自身形成自等死锁；流内已完整捕获异常，关闭序列经 _windowOperationCancellation
    /// 取消在途下载）。RunUpdateFlowAsync 的同步段先置位在途标记再首个 await，
    /// 本入口不会并发进入两份。</summary>
    private void BeginUpdateFlow()
    {
        if (_isUpdateDownloadInFlight)
        {
            return;
        }

        _ = RunUpdateFlowAsync();
    }

    /// <summary>
    /// 完整静默更新流（原子顺序，锁定决策）：下载精确名资产 + SHA256 校验（服务侧完成，
    /// 失败即清理回可用态）→ 排空既有保存管线 → 启动静默安装器（脱钩进程）→
    /// 经既有优雅退出路径关闭 → 安装器覆盖安装后 [Run] 条目自动重启新版本。
    /// AppMutex 在安装器启动瞬间检查，故排空必须先行（应用尽快退出）；竞态万一
    /// 失守仅静默失败，按钮状态由版本比较派生、仍点亮可重试。
    /// </summary>
    private async Task RunUpdateFlowAsync()
    {
        if (_isClosing || _isUpdateDownloadInFlight || _availableUpdate is null)
        {
            return;
        }

        var update = _availableUpdate;
        var version = update.LatestVersion ?? "";
        _isUpdateDownloadInFlight = true;
        ShowStatus($"正在下载更新（{version}），完成后将自动安装并重启…");
        try
        {
            var download = await _updateService.DownloadUpdateAsync(
                update,
                UpdateDownloadDirectory,
                _windowOperationCancellation.Token);
            if (!download.IsSuccess || download.InstallerPath is null)
            {
                // 服务侧已清理临时文件；回到可用态（按钮仍点亮）由用户决定何时重试。
                ShowStatus(download.ErrorMessage ?? "更新下载失败，可稍后重试。");
                return;
            }

            ShowStatus("更新包已通过校验，正在保存内容并准备安装…");
            await DrainBeforeUpdateAsync();
            if (!_updateService.TryStartSilentInstaller(download.InstallerPath))
            {
                ShowStatus("无法启动更新安装程序，本次更新未执行，可稍后重试。");
                return;
            }

            RequestApplicationExit();
        }
        catch (OperationCanceledException)
        {
            // 关闭序列取消了下载：应用正在退出，无需也不应再提示。
        }
        catch (Exception)
        {
            // async void 的未知异常会击穿为进程崩溃：就地降级，按钮回到可用态可重试。
            ShowStatus("更新未完成，可稍后重试。");
        }
        finally
        {
            _isUpdateDownloadInFlight = false;
        }
    }

    /// <summary>
    /// 更新前的最小前置冲刷（与既有关闭序列同源）：提交在编辑内容、冲刷记录、
    /// 等待在途操作归零，使启动安装器之后的窗口关闭路径近乎瞬时完成，
    /// 把「安装器查锁时应用未退出」的竞态窗口压到最小。二次 Drain 收紧重入窗口：
    /// 排空期间到达的剪贴板采集等新入队操作也等待落定（不构造无界循环）。
    /// </summary>
    private async Task DrainBeforeUpdateAsync()
    {
        CommitCardTextEditing();
        TrackPendingOperation(FlushReviewAsync());
        await DrainPendingOperationsAsync();
        await DrainPendingOperationsAsync();
    }

    /// <summary>
    /// 按钮双态呈现（同贴边隐藏 armed 态的强调色机制）：无更新=次级文字色「检查更新」；
    /// 有更新=强调色 + tooltip/无障碍名携带新版本号。SetResourceReference 保持动态解析，
    /// 主题切换时自动跟随。点亮态下更新流在途时点击被单飞忽略；失败回到可用态后
    /// 点击=重试完整更新流。
    /// </summary>
    private void UpdateUpdateButtonState()
    {
        var hasUpdate = _availableUpdate?.Asset is not null;
        UpdateButton.SetResourceReference(
            Control.ForegroundProperty,
            hasUpdate ? "AccentBrush" : "SecondaryTextBrush");
        var label = hasUpdate
            ? $"发现新版本 {_availableUpdate!.LatestVersion}，点击更新"
            : "检查更新";
        UpdateButton.ToolTip = label;
        AutomationProperties.SetName(UpdateButton, label);
    }

    private static void OpenReleasePage(string? releaseUrl)
    {
        // 发布页地址来自远端 JSON：仅接受 https 绝对地址，其余一律回退仓库 releases 页
        // （杜绝被篡改响应诱导打开任意协议/本地路径）。
        var target = Uri.TryCreate(releaseUrl, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                ? uri.ToString()
                : ProductIdentity.GitHubRepoHtmlUrl + "/releases";
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 打不开浏览器时状态条提示已经给出，不再打断。
        }
    }
}
