using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;

namespace FloatingTransferStation.Tests;

public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public void ClipboardCapture_CorruptLargestImageDisplaysSavedFallbackWithoutStatus()
    {
        using var directory = new TestDirectory();
        var largerPath = Path.Combine(directory.Root, "larger.png");
        var usablePath = Path.Combine(directory.Root, "usable.png");
        WritePng(largerPath, 640, 360);
        WritePng(usablePath, 320, 180);
        var damaged = File.ReadAllBytes(largerPath)[..33];
        Assert.AreEqual(640, SixLabors.ImageSharp.Image.Identify(damaged).Width);
        Assert.ThrowsExactly<SixLabors.ImageSharp.InvalidImageContentException>(
            () => SixLabors.ImageSharp.Image.Load(damaged));
        var reader = new SingleClipboardReader(new ClipboardSnapshot(501, null, [], null,
        [
            ClipboardImageCandidate.FromEncoded("image/png", damaged),
            ClipboardImageCandidate.FromEncoded("PNG", File.ReadAllBytes(usablePath))
        ]));
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var board = new BoardService();
        var category = new DefaultCaptureCategoryState();
        category.Set(BoardCategory.Reference);
        var window = CreateWindow(board, store, new WindowSettings(400, 440, 0), category,
            new ImageNormalizer(store.ImagesDirectory), reader);

        try
        {
            // Keep this synthetic capture independent of operating-system clipboard notifications.
            var sourceInitialized = GetPrivateMethod("MainWindow_SourceInitialized");
            Assert.IsNotNull(sourceInitialized);
            window.SourceInitialized -= sourceInitialized.CreateDelegate<EventHandler>(window);
            window.ClientAreaAnimationsEnabled = false;
            window.ShowActivated = false;
            window.Topmost = false;
            ExpandCategory(window, BoardCategory.Reference);
            window.Left = -10000;
            window.Top = -10000;
            window.Show();
            Assert.IsNull(GetPrivateField<object?>(window, "_windowSource"));
            var capture = window.Dispatcher.InvokeAsync(() =>
                GetPrivateField<ClipboardCaptureService>(window, "_clipboardCapture")
                    .HandleClipboardUpdateAsync()).Task.Unwrap();
            PumpDispatcherUntil(window.Dispatcher, capture);
            CompleteLayout(window);

            var item = board.Items(BoardCategory.Reference).Single();
            Assert.AreEqual(1, board.CreateSnapshot().Items.Count);
            Assert.AreEqual(BoardItemKind.Image, item.Kind);
            Assert.IsTrue(File.Exists(item.ImageAbsolutePath));
            Assert.AreEqual(1, Directory.GetFiles(paths.ImagesDirectory).Length);
            var persisted = store.LoadBoardAsync();
            PumpDispatcherUntil(window.Dispatcher, persisted);
            Assert.AreEqual(item.Id, persisted.Result.Items.Single().Id);
            using var image = SixLabors.ImageSharp.Image.Load(item.ImageAbsolutePath!);
            Assert.AreEqual(320, image.Width);
            Assert.AreEqual(180, image.Height);
            var viewModel = (MainWindowViewModel)window.DataContext;
            Assert.AreEqual(BoardCategory.Reference, viewModel.ActivePanel!.Category);
            Assert.AreEqual(string.Empty, viewModel.StatusText);
            Assert.AreEqual(Visibility.Collapsed, ((Border)window.FindName("StatusOverlay")).Visibility);
            var list = (ListBox)window.FindName("BoardList");
            Assert.AreEqual(1, list.Items.Count);
            Assert.AreSame(item, list.Items[0]);
            var thumbnail = FindDescendants<Image>(list).Single(candidate => candidate.IsVisible);
            Assert.IsInstanceOfType<BitmapSource>(thumbnail.Source);
            Assert.IsTrue(thumbnail.ActualWidth > 0 && thumbnail.ActualHeight > 0);
            SaveVisualEvidence((Border)window.FindName("WindowShell"),
                "clipboard-image-fallback.png", "FTS_CLIPBOARD_IMAGE_FALLBACK_EVIDENCE_DIR");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }
}
