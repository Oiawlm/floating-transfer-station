using System.Windows;
using FloatingTransferStation.Services;
using FloatingTransferStation.Views;

namespace FloatingTransferStation;

public partial class App : Application
{
    // --relocated：搬迁后自动重启的实例。旧实例先写登记再退本进程，互斥锁短暂
    // 仍被旧实例持有，因此仅此模式允许限时重试等待锁释放；普通双启动保持立即失败。
    private static readonly TimeSpan RelocatedMutexRetryTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RelocatedMutexRetryInterval = TimeSpan.FromMilliseconds(200);

    private AppLifecycleService? _lifecycle;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"发生未处理的错误：{args.Exception.Message}",
                ProductIdentity.DisplayName,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            args.Handled = true;
        };
        try
        {
            // FTS_PREVIEW_DATA_DIR / FTS_PREVIEW_THEME 仅供本机预览与截图取证：
            // 用隔离数据目录启动，并可强制亮/暗主题，绝不触碰已安装应用的数据。
            var previewDataDirectory = Environment.GetEnvironmentVariable("FTS_PREVIEW_DATA_DIR");
            var previewTheme = Environment.GetEnvironmentVariable("FTS_PREVIEW_THEME");
            var isRelaunchedAfterRelocation = e.Args.Contains(DataDirectoryChangeService.RelaunchArgument);

            _lifecycle = new AppLifecycleService();
            var lifecycleStarted = string.IsNullOrWhiteSpace(previewDataDirectory)
                ? TryStartWithRelocationRetry(isRelaunchedAfterRelocation)
                : _lifecycle.TryStart(PreviewMutexName(previewDataDirectory));
            if (!lifecycleStarted)
            {
                if (isRelaunchedAfterRelocation)
                {
                    MessageBox.Show(
                        "迁移后自动启动等待超时，请手动启动悬浮中转站。",
                        ProductIdentity.DisplayName,
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                Shutdown();
                return;
            }

            var paths = string.IsNullOrWhiteSpace(previewDataDirectory)
                ? AppPaths.CreateDefault(new WindowsDataDirectorySettings())
                : AppPaths.ForTests(previewDataDirectory);
            if (string.Equals(previewTheme, "dark", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(previewTheme, "light", StringComparison.OrdinalIgnoreCase))
            {
                DesignThemeManager.PreviewOverride = previewTheme == "dark"
                    ? DesignTheme.Dark
                    : DesignTheme.Light;
            }

            // 装配方向（无循环依赖）：注册表数据目录 → preferences.json → 插件目录覆盖。
            // 偏好存放在数据目录内，插件目录真相跟随偏好而非登记，故先读偏好再建目录树。
            var store = new LocalStore(paths, new AtomicTextWriter());
            var preferences = await store.LoadPreferencesAsync();
            var (effectivePaths, pluginsDirectoryWarning) = AppPaths.ResolvePluginsDirectoryOverride(
                paths,
                preferences.PluginsDirectoryOverride);
            paths = effectivePaths;
            if (pluginsDirectoryWarning is not null)
            {
                preferences = preferences with { PluginsDirectoryOverride = null };
            }

            var board = new BoardService();
            var snapshot = await store.LoadBoardAsync();
            var normalizer = new ImageNormalizer(paths.ImagesDirectory);
            await normalizer.RepairStoredImagesOnceAsync(
                snapshot.Items
                    .Where(item => item.Kind == Models.BoardItemKind.Image)
                    .Select(item => item.ImageAbsolutePath!)
                    .Where(path => !string.IsNullOrWhiteSpace(path)));
            board.Restore(snapshot);
            var settings = await store.LoadSettingsAsync();
            settings = await new DailyReviewMigration(store).EnsureAsync(board, settings);
            var pluginCatalog = new PluginCatalog(
                paths,
                Path.Combine(AppContext.BaseDirectory, "plugins"),
                new AtomicTextWriter());
            await pluginCatalog.LoadAsync();
            MainWindow? window = null;
            void ShowStatus(string message) => window?.ShowStatus(message);
            var boardOperationGate = new BoardOperationGate();
            var defaultCaptureCategory = new DefaultCaptureCategoryState();
            var captureDeduplicationGate = new CaptureDeduplicationGate();
            var windowsDataImageReader = new WindowsDataImageReader();
            var externalDropPayloadReader = new ExternalDropPayloadReader(
                windowsDataImageReader);
            var clipboardCapture = new ClipboardCaptureService(
                new WpfClipboardReader(windowsDataImageReader),
                normalizer,
                board,
                store,
                ShowStatus,
                operationGate: boardOperationGate,
                defaultCaptureCategory: defaultCaptureCategory,
                pluginCatalog: pluginCatalog,
                deduplicationGate: captureDeduplicationGate);
            var mutations = new BoardMutationService(board, store, ShowStatus, boardOperationGate);
            var externalDropImport = new ExternalDropImportService(
                normalizer,
                board,
                store,
                ShowStatus,
                boardOperationGate,
                pluginCatalog);
            window = new MainWindow(
                board,
                store,
                settings,
                clipboardCapture,
                mutations,
                new DragPayloadService(),
                externalDropPayloadReader,
                externalDropImport,
                defaultCaptureCategory,
                preferences: preferences,
                preferencesStore: store,
                startupManager: new WindowsStartupManager(),
                dataDirectory: paths.DataDirectory,
                rightEdgeBleedProvider: ScreenEdgeGeometry.GetRightEdgeBleed,
                pluginCatalog: pluginCatalog,
                operationGate: boardOperationGate,
                dataDirectoryChangeService: string.IsNullOrWhiteSpace(previewDataDirectory)
                    ? DataDirectoryChangeService.CreateDefault(new WindowsDataDirectorySettings())
                    : null);
            MainWindow = window;
            window.Show();
            if (pluginsDirectoryWarning is not null)
            {
                window.ShowStatus(pluginsDirectoryWarning);
            }

            // 搬迁后的延迟清理：此刻已在新目录拿到单实例锁，按形状守卫删除旧受管目录。
            // 删除可能涉及大量文件，放后台执行，结果经状态条提示。
            _ = Task.Run(() => DataDirectoryCleanupProcessor.Run(paths.DataDirectory))
                .ContinueWith(
                    cleanup =>
                    {
                        if (cleanup.Result is { } notice)
                        {
                            window.ShowStatus(notice);
                        }
                    },
                    TaskScheduler.FromCurrentSynchronizationContext());
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"悬浮中转站启动失败：{exception.Message}",
                ProductIdentity.DisplayName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private bool TryStartWithRelocationRetry(bool isRelaunchedAfterRelocation)
    {
        if (!isRelaunchedAfterRelocation)
        {
            return _lifecycle!.TryStart();
        }

        var deadline = DateTime.UtcNow + RelocatedMutexRetryTimeout;
        while (true)
        {
            if (_lifecycle!.TryStart())
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            Thread.Sleep(RelocatedMutexRetryInterval);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _lifecycle?.Dispose();
        base.OnExit(e);
    }

    private static string PreviewMutexName(string previewDataDirectory)
    {
        // 预览实例按数据目录独立加锁，不与已安装应用的单实例互斥争夺。
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(previewDataDirectory)))[..16];
        return $"{FloatingTransferStation.Services.SingleInstanceGuard.ApplicationMutexName}.preview.{hash}";
    }
}
