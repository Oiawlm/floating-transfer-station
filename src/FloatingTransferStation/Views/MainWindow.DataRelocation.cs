using System.Windows;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Views;

public partial class MainWindow
{
    private sealed class RelocationQuiesceScope(MainWindow window) : IDataChangeQuiesceScope
    {
        // 编排服务的续延可能落在线程池（暂存复制 ConfigureAwait(false) 之后），
        // 挂钩的三段都触碰 UI/句柄，必须整体封送回窗口调度器。
        public Task QuiesceAsync() =>
            window.Dispatcher.InvokeAsync(QuiesceCoreAsync).Task.Unwrap();

        public void Resume() => window.Dispatcher.Invoke(ResumeCore);

        public void PrepareForExit() =>
            window.Dispatcher.Invoke(() => window._allowClose = true);

        private async Task QuiesceCoreAsync()
        {
            var host = window;
            host.IsEnabled = false;
            // 与主窗 Closing 冲刷序列同构：先提交就地编辑（封门前），停全部写入者，
            // 冲刷看板/复盘/窗口设置并封闭操作门，随后保持静默直到退出或恢复。
            host._topmostTimer.Stop();
            host.CommitPendingCardTextEditing();
            _ = host.TrySetGlobalHotkey(false);
            var operationCancellation = host._windowOperationCancellation;
            operationCancellation.Cancel();
            host._reviewSaveTimer.Stop();
            host.TrackPendingOperation(host.FlushReviewAsync());
            await host.DrainPendingOperationsAsync();
            host._dailyReviews?.StopWatching();
            await host._mutations.SaveForShutdownAsync(() => host._store.SaveSettingsAsync(host._settings));
            host._mutations.DiscardUndoableDeletes();
            host.StopClipboardListening();
            operationCancellation.Dispose();
        }

        private void ResumeCore()
        {
            var host = window;
            host._operationGate?.Reopen();
            var cancellation = host._windowOperationCancellation;
            host._windowOperationCancellation = new CancellationTokenSource();
            cancellation.Dispose();
            host._windowSource ??= PresentationSource.FromVisual(host)
                as System.Windows.Interop.HwndSource;
            if (host._windowSource is not null && !host._windowSource.IsDisposed)
            {
                host._windowSource.AddHook(host.WndProc);
            }

            host.StartClipboardListening();
            if (host._preferences.GlobalHotkeyEnabled)
            {
                host.TrySetGlobalHotkey(enable: true);
            }

            if (host.IsReviewActive())
            {
                host._dailyReviews?.StartWatching();
            }

            host._topmostTimer.Start();
            host.IsEnabled = true;
        }
    }

    /// <summary>预检数据目录搬迁（不改动任何状态）：供设置窗确认对话框展示目标与体积。</summary>
    public DataDirectoryChangePreview DescribeDataDirectoryChange(string targetParentDirectory) =>
        _dataDirectoryChangeService is null
            ? new DataDirectoryChangePreview(false, "当前运行环境不支持更改数据目录。", 0, null)
            : _dataDirectoryChangeService.Preview(_dataDirectory, targetParentDirectory);

    /// <summary>
    /// 执行数据目录搬迁并自动重启。失败：恢复运行、返回原因，不做任何改动；
    /// 成功或重启失败：新实例已接管（或登记已指向新目录），主窗直接退出。
    /// </summary>
    public async Task<DataDirectoryChangeResult> ChangeDataDirectoryAsync(
        string targetParentDirectory,
        IProgress<DataDirectoryCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_dataDirectoryChangeService is null)
        {
            return DataDirectoryChangeResult.Failed("当前运行环境不支持更改数据目录。");
        }

        var result = await _dataDirectoryChangeService.ChangeAsync(
            _dataDirectory,
            targetParentDirectory,
            new RelocationQuiesceScope(this),
            progress,
            cancellationToken);
        if (result.ExitApplication)
        {
            // 数据已落盘且登记已指向新目录：走免冲刷关闭路径退出本实例。
            await Dispatcher.InvokeAsync(() => Close());
        }

        return result;
    }

    /// <summary>当前生效插件目录（覆盖或默认），供设置窗展示。</summary>
    public string EffectivePluginsDirectory =>
        _pluginCatalog?.UserPluginsDirectory ?? AppPaths.FromDataDirectory(_dataDirectory).PluginsDirectory;

    /// <summary>插件目录是否为默认（数据目录下 plugins）。</summary>
    public bool PluginsDirectoryIsDefault =>
        string.Equals(
            EffectivePluginsDirectory,
            AppPaths.FromDataDirectory(_dataDirectory).PluginsDirectory,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// 保存插件目录偏好（null = 恢复默认）：走既有偏好原子持久化，重启后生效；
    /// 目录落在数据目录之内或非法时拒绝并返回原因。
    /// </summary>
    public Task<string?> ApplyPluginsDirectoryAsync(string? directoryOverride)
    {
        if (directoryOverride is not null)
        {
            var probe = AppPaths.FromDataDirectory(_dataDirectory);
            var (paths, warning) = AppPaths.ResolvePluginsDirectoryOverride(probe, directoryOverride);
            if (warning is not null)
            {
                return Task.FromResult<string?>(warning);
            }

            directoryOverride = string.Equals(
                paths.PluginsDirectory,
                probe.PluginsDirectory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                ? null
                : paths.PluginsDirectory;
        }

        ApplyPreferences(_preferences with { PluginsDirectoryOverride = directoryOverride });
        return Task.FromResult<string?>(null);
    }
}
