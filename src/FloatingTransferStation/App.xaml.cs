using System.Windows;
using FloatingTransferStation.Services;
using FloatingTransferStation.Views;

namespace FloatingTransferStation;

public partial class App : Application
{
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

            _lifecycle = new AppLifecycleService();
            var lifecycleStarted = string.IsNullOrWhiteSpace(previewDataDirectory)
                ? _lifecycle.TryStart()
                : _lifecycle.TryStart(PreviewMutexName(previewDataDirectory));
            if (!lifecycleStarted)
            {
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

            var store = new LocalStore(paths, new AtomicTextWriter());
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
            MainWindow? window = null;
            void ShowStatus(string message) => window?.ShowStatus(message);
            var boardOperationGate = new BoardOperationGate();
            var defaultCaptureCategory = new DefaultCaptureCategoryState();
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
                defaultCaptureCategory: defaultCaptureCategory);
            var mutations = new BoardMutationService(board, store, ShowStatus, boardOperationGate);
            var externalDropImport = new ExternalDropImportService(
                normalizer,
                board,
                store,
                ShowStatus,
                boardOperationGate);
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
                rightEdgeBleedProvider: ScreenEdgeGeometry.GetRightEdgeBleed);
            MainWindow = window;
            window.Show();
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
