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
        try
        {
            var executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("The current executable path is unavailable.");
            _lifecycle = AppLifecycleService.CreateDefault(executablePath);
            if (!_lifecycle.TryStart())
            {
                Shutdown();
                return;
            }

            var paths = AppPaths.CreateDefault();
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
            try
            {
                _lifecycle.EnsureStartup(executablePath);
            }
            catch (Exception)
            {
                // The installer also owns this value; registration failure must not block the core workflow.
            }

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
                defaultCaptureCategory);
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
}
