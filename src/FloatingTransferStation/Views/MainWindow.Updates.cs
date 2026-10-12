using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;

namespace FloatingTransferStation.Views;

/// <summary>
/// 检查更新与一键更新：头部按钮行最左常驻「更新」按钮，双语义——无更新=手动检查
/// （就地状态条反馈）；有更新=点亮强调色（tooltip 显示新版本号，点击执行
/// 下载→SHA256 校验→排空保存管线→静默安装→优雅退出，安装器 [Run] 条目自动重启新版本）。
/// 自动检查节奏：启动 30 秒后首检 + 每 24 小时一次（设置「通用」节可关，关=停表、
/// 开=恢复节奏）；自动检查的任何失败完全静默，手动检查失败经状态条提示一次。
/// 检查与下载全程单飞防重入；按钮状态唯一派生自版本比较结果，下载失败回到可用态
/// 仍点亮可重试。无弹窗、无 toast，发现新版本仅状态条提示一次（面板可见时）。
/// </summary>
public partial class MainWindow : Window
{
    private static readonly TimeSpan UpdateFirstCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);
    // 安装包落盘目录：更新专用的 %TEMP% 子目录；同版本旧文件与半成品的清理在
    // 服务侧下载前执行，这里只提供位置。
    private static readonly string UpdateDownloadDirectory =
        Path.Combine(Path.GetTempPath(), "FloatingTransferStation", "Update");

    private readonly IUpdateService _updateService;
    private readonly DispatcherTimer _updateCheckTimer = new() { Interval = UpdateFirstCheckDelay };
    private bool _initialUpdateCheckPending;
    private bool _isUpdateCheckInFlight;
    private bool _isUpdateDownloadInFlight;
    private bool _assetMissingBrowserOpened;
    private UpdateCheckResult? _availableUpdate;

    private void InitializeUpdates()
    {
        // 首检尚未发生：偏好开启时（无论何时开启）都从 30 秒延迟首检开始。
        _initialUpdateCheckPending = true;
        _updateCheckTimer.Tick += (_, _) => RunScheduledUpdateCheck();
        // 初始态文案同样经双态派生（单一来源，XAML 只作占位）。
        UpdateUpdateButtonState();
    }

    /// <summary>
    /// 启动/恢复自动检查定时器（偏好开关的唯一执行点，SourceInitialized 与
    /// ApplyPreferences 共用）：首检未跑过则从 30 秒延迟首检开始，否则恢复 24 小时节奏。
    /// </summary>
    private void ApplyUpdateCheckPreference(bool enabled)
    {
        if (enabled)
        {
            if (_isClosing || _updateCheckTimer.IsEnabled)
            {
                return;
            }

            _updateCheckTimer.Interval = _initialUpdateCheckPending
                ? UpdateFirstCheckDelay
                : UpdateCheckInterval;
            _updateCheckTimer.Start();
        }
        else
        {
            _updateCheckTimer.Stop();
        }
    }

    /// <summary>定时检查 tick：通过单飞守卫后才结算首检并切 24 小时节奏——首检被在途
    /// 操作挤掉时保留待办，下个 tick 仍按 30 秒节奏补跑（不被丢进 24 小时）。</summary>
    private void RunScheduledUpdateCheck()
    {
        if (_isUpdateCheckInFlight || _isUpdateDownloadInFlight)
        {
            return;
        }

        if (_initialUpdateCheckPending)
        {
            _initialUpdateCheckPending = false;
            _updateCheckTimer.Interval = UpdateCheckInterval;
        }

        _isUpdateCheckInFlight = true;
        TrackPendingOperation(RunScheduledCheckSafelyAsync());
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

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_isClosing || _isUpdateCheckInFlight || _isUpdateDownloadInFlight)
        {
            return;
        }

        if (_availableUpdate is null)
        {
            await RunManualCheckAsync();
        }
        else
        {
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
    /// 检查结果采纳：更新可用 → 点亮按钮并按需状态条提示一次（手动检查必然提示）；
    /// 无更新/失败按手动与否决定是否提示，无更新同时熄灭按钮（远端撤回发布时
    /// 按钮状态仍由版本比较派生）。AssetMissing（精确名资产或校验清单缺失）仅在
    /// 手动检查时经状态条提示并回退发布页（wbx 评审：自动检查的任何失败完全静默，
    /// 零操作不得弹浏览器）；浏览器回退每会话至多一次，防自动节奏反复打扰。
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

        var hadUpdate = _availableUpdate is not null;
        _availableUpdate = result;
        UpdateUpdateButtonState();
        if (!hadUpdate)
        {
            var panelVisible = ((MainWindowViewModel)DataContext).IsPanelExpanded;
            if (isManual || panelVisible)
            {
                ShowStatus($"发现新版本 {result.LatestVersion}，点击标题栏「更新」按钮即可安装。");
            }
        }
    }

    /// <summary>
    /// 一键更新（原子顺序，锁定决策）：下载精确名资产 + SHA256 校验（服务侧完成，
    /// 失败即清理回可用态）→ 排空既有保存管线 → 启动静默安装器（脱钩进程）→
    /// 经既有优雅退出路径关闭 → 安装器覆盖安装后 [Run] 条目自动重启新版本。
    /// AppMutex 在安装器启动瞬间检查，故排空必须先行（应用尽快退出）；竞态万一
    /// 失守仅静默失败，按钮状态由版本比较派生、仍点亮可重试。
    /// </summary>
    private async Task RunUpdateFlowAsync()
    {
        var update = _availableUpdate!;
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
    /// 更新前的最小前置冲刷（与既有关闭序列同源）：提交在编辑内容、冲刷复盘、
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
    /// 主题切换时自动跟随。
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
