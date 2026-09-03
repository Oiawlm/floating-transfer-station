using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

[TestClass]
[TestCategory("Adversarial")]
public sealed partial class MainWindowInteractionTests
{
    private static MainWindow CreateWindow(
        TestDirectory directory,
        BoardService board,
        DefaultCaptureCategoryState? defaultCaptureCategory = null)
    {
        defaultCaptureCategory ??= new DefaultCaptureCategoryState();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        return CreateWindow(board, store, WindowSettings.Default, defaultCaptureCategory);
    }

    private static MainWindow CreateWindow(
        BoardService board,
        IBoardStore store,
        WindowSettings settings,
        DefaultCaptureCategoryState? defaultCaptureCategory = null)
        => CreateWindow(
            board,
            store,
            settings,
            defaultCaptureCategory,
            new ImageNormalizer(store.ImagesDirectory));

    private static MainWindow CreateWindow(
        BoardService board,
        IBoardStore store,
        WindowSettings settings,
        DefaultCaptureCategoryState? defaultCaptureCategory,
        IImageNormalizer normalizer)
    {
        defaultCaptureCategory ??= new DefaultCaptureCategoryState();
        var operationGate = new BoardOperationGate();
        MainWindow? window = null;
        void ShowStatus(string message) => window?.ShowStatus(message);
        var clipboard = new ClipboardCaptureService(
            new NeverReadClipboardReader(),
            normalizer,
            board,
            store,
            ShowStatus,
            operationGate: operationGate,
            defaultCaptureCategory: defaultCaptureCategory);
        window = new MainWindow(
            board,
            store,
            settings,
            clipboard,
            new BoardMutationService(board, store, ShowStatus, operationGate),
            new DragPayloadService(),
            new ExternalDropPayloadReader(new WindowsDataImageReader()),
            new ExternalDropImportService(
                normalizer,
                board,
                store,
                ShowStatus,
                operationGate),
            defaultCaptureCategory);
        return window;
    }

    private static (Border Layer, Border Marker) FindCategoryFeedback(
        MainWindow window,
        BoardCategory category)
    {
        var viewModel = (MainWindowViewModel)window.DataContext;
        var categoryViewModel = viewModel.Categories.Single(candidate => candidate.Category == category);
        var tab = FindCategoryTab(window, categoryViewModel);
        var layerStyle = (Style)window.FindResource("CategoryActiveLayerStyle");
        var markerStyle = (Style)window.FindResource("CategoryActiveMarkerStyle");
        var layer = FindDescendants<Border>(tab)
            .Single(candidate => ReferenceEquals(candidate.Style, layerStyle));
        var marker = FindDescendants<Border>(tab)
            .Single(candidate => ReferenceEquals(candidate.Style, markerStyle));
        return (layer, marker);
    }

    private static (Grid Host, TranslateTransform Transform) FindPanelContent(
        MainWindow window) =>
        ((Grid)window.FindName("PanelContentHost"),
         (TranslateTransform)window.FindName("PanelContentTransform"));

    private static void StartPanelContentAnimation(
        Grid host,
        TranslateTransform transform)
    {
        host.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0d, 1d, TimeSpan.FromSeconds(1)));
        transform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(6d, 0d, TimeSpan.FromSeconds(1)));
        Assert.IsTrue(host.HasAnimatedProperties);
        Assert.IsTrue(transform.HasAnimatedProperties);
    }

    private static void AssertPanelContentAnimationStopped(
        Grid host,
        TranslateTransform transform)
    {
        Assert.IsFalse(host.HasAnimatedProperties);
        Assert.IsFalse(transform.HasAnimatedProperties);
        Assert.AreEqual(1d, host.Opacity);
        Assert.AreEqual(0d, transform.X);
    }

    private static void AddScrollableItems(BoardService board, BoardCategory category)
    {
        for (var index = 0; index < 24; index++)
        {
            var item = board.AddText($"{category} {index}: {new string('x', 180)}");
            if (category != BoardCategory.Inbox)
            {
                board.Move(item.Id, category, board.Items(category).Count);
            }
        }
    }

    private static void EnterCategory(MainWindow window, BoardCategory category)
    {
        var viewModel = (MainWindowViewModel)window.DataContext;
        var panel = viewModel.Categories.Single(candidate => candidate.Category == category);
        InvokePrivate(
            window,
            "CategoryTab_MouseEnter",
            new Border { DataContext = panel },
            NewMouseEventArgs());
    }

    private static void ExpandCategory(MainWindow window, BoardCategory category)
    {
        EnterCategory(window, category);
        InvokePrivate(window, "ExpandIntentTimer_Tick", null, EventArgs.Empty);
    }

    private static MouseEventArgs NewMouseEventArgs() => new(Mouse.PrimaryDevice, 0);

    private static MouseEventArgs NewMouseEventArgs(
        RoutedEvent routedEvent,
        DependencyObject source) =>
        new(Mouse.PrimaryDevice, 0)
        {
            RoutedEvent = routedEvent,
            Source = source
        };

    private static MouseButtonEventArgs NewMouseButtonEventArgs(
        RoutedEvent routedEvent,
        DependencyObject source) =>
        new(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = routedEvent,
            Source = source
        };

    private static TextCompositionEventArgs NewTextCompositionEventArgs(
        RoutedEvent routedEvent,
        IInputElement source,
        string text) =>
        new(
            Keyboard.PrimaryDevice,
            new TextComposition(InputManager.Current, source, text))
        {
            RoutedEvent = routedEvent,
            Source = source
        };

    private static KeyEventArgs NewKeyEventArgs(Window window, Key key) =>
        new(
            Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(window)!,
            Environment.TickCount,
            key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
            Source = window
        };

    private static KeyEventArgs NewModifiedKeyEventArgs(
        Window window,
        Key key,
        ModifierKeys modifiers) =>
        new(
            new ModifierKeyboardDevice(modifiers),
            PresentationSource.FromVisual(window)!,
            Environment.TickCount,
            key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
            Source = window
        };

    private sealed class ModifierKeyboardDevice : KeyboardDevice
    {
        private readonly ModifierKeys _modifiers;

        public ModifierKeyboardDevice(ModifierKeys modifiers)
            : base(InputManager.Current)
        {
            _modifiers = modifiers;
        }

        protected override KeyStates GetKeyStatesFromSystem(Key key)
        {
            var isDown = key switch
            {
                Key.LeftCtrl or Key.RightCtrl => _modifiers.HasFlag(ModifierKeys.Control),
                Key.LeftShift or Key.RightShift => _modifiers.HasFlag(ModifierKeys.Shift),
                Key.LeftAlt or Key.RightAlt => _modifiers.HasFlag(ModifierKeys.Alt),
                Key.LWin or Key.RWin => _modifiers.HasFlag(ModifierKeys.Windows),
                _ => false
            };
            return isDown ? KeyStates.Down : KeyStates.None;
        }
    }

    private static void ScrollTo(Window window, ScrollViewer viewer, double offset)
    {
        viewer.ScrollToVerticalOffset(offset);
        CompleteLayout(window);
        Assert.AreEqual(offset, viewer.VerticalOffset, 0.5, "Scroll setup did not reach the requested offset.");
    }

    private static void CompleteLayout(Window window)
    {
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }

    private static void PumpDispatcherFor(Dispatcher dispatcher, TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send, dispatcher)
        {
            Interval = duration
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void CloseWindow(Window window)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler closedHandler = (_, _) => closed.TrySetResult();
        window.Closed += closedHandler;
        try
        {
            window.Close();
            PumpDispatcherUntil(window.Dispatcher, closed.Task);
        }
        finally
        {
            window.Closed -= closedHandler;
        }
    }

    private static void CloseWindowWithoutSaving(Window window)
    {
        var allowClose = typeof(MainWindow).GetField(
            "_allowClose",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(allowClose);
        allowClose.SetValue(window, true);
        CloseWindow(window);
    }

    private static void PumpDispatcherUntil(Dispatcher dispatcher, Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var timedOut = false;
            var timeout = new DispatcherTimer(DispatcherPriority.Send, dispatcher)
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            timeout.Tick += (_, _) =>
            {
                timedOut = true;
                timeout.Stop();
                frame.Continue = false;
            };
            _ = task.ContinueWith(
                _ => dispatcher.BeginInvoke(
                    DispatcherPriority.Send,
                    new Action(() => frame.Continue = false)),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            timeout.Start();
            Dispatcher.PushFrame(frame);
            timeout.Stop();
            if (timedOut && !task.IsCompleted)
            {
                throw new TimeoutException("The dispatcher operation did not complete within five seconds.");
            }
        }

        task.GetAwaiter().GetResult();
    }

    private static void InvokePrivate(MainWindow window, string methodName, params object?[] arguments)
    {
        var method = GetPrivateMethod(methodName);
        Assert.IsNotNull(method);
        method.Invoke(window, arguments);
    }

    private static void InvokePrivateTask(
        MainWindow window,
        string methodName,
        params object?[] arguments)
    {
        var method = GetPrivateMethod(methodName);
        Assert.IsNotNull(method);
        var task = method.Invoke(window, arguments) as Task;
        Assert.IsNotNull(task);
        PumpDispatcherUntil(window.Dispatcher, task);
    }

    private static MethodInfo? GetPrivateMethod(string methodName) =>
        typeof(MainWindow).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);

    private static T GetPrivateField<T>(MainWindow window, string fieldName)
    {
        var field = typeof(MainWindow).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        return (T)field.GetValue(window)!;
    }

    private static T GetPrivateStaticField<T>(string fieldName)
    {
        var field = typeof(MainWindow).GetField(
            fieldName,
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        return (T)field.GetValue(null)!;
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static Border FindCategoryTab(MainWindow window, CategoryViewModel category)
    {
        var rail = window.FindName("CategoryRail") as Border;
        Assert.IsNotNull(rail);
        return FindDescendants<Border>(rail).Single(candidate =>
            ReferenceEquals(candidate.DataContext, category) &&
            Equals(candidate.Tag, category.Category));
    }

    private static Border FindCollapsedCategoryTab(MainWindow window)
    {
        var viewModel = (MainWindowViewModel)window.DataContext;
        var handle = window.FindName("CollapsedCategoryHandle") as ContentControl;
        Assert.IsNotNull(handle);
        return FindDescendants<Border>(handle).Single(candidate =>
            ReferenceEquals(candidate.DataContext, viewModel.DefaultCapturePanel) &&
            Equals(candidate.Tag, viewModel.DefaultCapturePanel.Category));
    }

    private static Rect ScreenBounds(FrameworkElement element) =>
        new(element.PointToScreen(new Point()), new Size(element.ActualWidth, element.ActualHeight));

    private static DataObject CreateInternalDragData()
    {
        var data = new DataObject();
        data.SetData(DragPayloadService.InternalItemIdFormat, Guid.NewGuid().ToString("D"));
        return data;
    }

    private static DragEventArgs NewDragEventArgs(
        IDataObject data,
        RoutedEvent routedEvent,
        DependencyObject target) =>
        NewDragEventArgs(data, routedEvent, target, new Point());

    private static DragEventArgs NewDragEventArgs(
        IDataObject data,
        RoutedEvent routedEvent,
        DependencyObject target,
        Point position)
    {
        var arguments = new object[]
        {
            data,
            (DragDropKeyStates)0,
            DragDropEffects.Copy | DragDropEffects.Move,
            target,
            position
        };
        var eventArgs = (DragEventArgs?)Activator.CreateInstance(
            typeof(DragEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: arguments,
            culture: null);
        Assert.IsNotNull(eventArgs);
        eventArgs.RoutedEvent = routedEvent;
        return eventArgs;
    }

    private sealed class SingleReadFileDropDataObject(string[] paths) : IDataObject
    {
        private readonly DataObject _inner = CreateDataObject(paths);

        public int FileDropReadCount { get; private set; }

        public object? GetData(string format, bool autoConvert)
        {
            if (format == DataFormats.FileDrop && ++FileDropReadCount > 1)
            {
                throw new InvalidOperationException("FileDrop was read more than once.");
            }

            return _inner.GetData(format, autoConvert);
        }

        public object? GetData(string format) => GetData(format, autoConvert: true);
        public object? GetData(Type format) => _inner.GetData(format);
        public bool GetDataPresent(string format, bool autoConvert) =>
            _inner.GetDataPresent(format, autoConvert);
        public bool GetDataPresent(string format) => _inner.GetDataPresent(format);
        public bool GetDataPresent(Type format) => _inner.GetDataPresent(format);
        public string[] GetFormats(bool autoConvert) => _inner.GetFormats(autoConvert);
        public string[] GetFormats() => _inner.GetFormats();
        public void SetData(string format, object data, bool autoConvert) =>
            throw new NotSupportedException();
        public void SetData(string format, object data) => throw new NotSupportedException();
        public void SetData(Type format, object data) => throw new NotSupportedException();
        public void SetData(object data) => throw new NotSupportedException();

        private static DataObject CreateDataObject(string[] paths)
        {
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, paths);
            return data;
        }
    }

    private static void WritePng(string path, int width, int height)
    {
        const int bytesPerPixel = 4;
        var stride = width * bytesPerPixel;
        var pixels = new byte[stride * height];
        Array.Fill(pixels, (byte)0x7F);
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void SaveVisualEvidence(
        FrameworkElement visual,
        string fileName,
        string environmentVariable = "FTS_CLEAR_SELECTION_EVIDENCE_DIR")
    {
        var directory = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(visual.ActualWidth),
            (int)Math.Ceiling(visual.ActualHeight),
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, fileName));
        encoder.Save(stream);
    }

    private static void WriteAnimatedGif(string path)
    {
        using var image = new SixLabors.ImageSharp.Image<
            SixLabors.ImageSharp.PixelFormats.Rgba32>(
            2,
            2,
            SixLabors.ImageSharp.Color.Red);
        image.Frames.AddFrame(image.Frames.RootFrame);
        using var stream = File.Create(path);
        image.Save(stream, new SixLabors.ImageSharp.Formats.Gif.GifEncoder());
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rectangle);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;

        public bool Contains(int x, int y) =>
            x >= Left && x < Right && y >= Top && y < Bottom;

        public override string ToString() => $"{Left},{Top},{Width},{Height}";
    }

    private static IOException? TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return null;
        }
        catch (IOException exception)
        {
            return exception;
        }
    }

    private sealed class NeverReadClipboardReader : IClipboardReader
    {
        public Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Clipboard reader should not be used while inspecting the window.");
    }

    private sealed class CountingClipboardReader : IClipboardReader
    {
        public int ReadCount { get; private set; }

        public Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult(new ClipboardSnapshot(1, null, [], "must not capture"));
        }
    }

    private sealed class SingleClipboardReader(ClipboardSnapshot snapshot) : IClipboardReader
    {
        public Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class CancelThenTextClipboardReader : IClipboardReader
    {
        private readonly TaskCompletionSource _releaseFirstRead = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;

        public TaskCompletionSource FirstReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstReadCanceled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstReadFinished { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReadCount => _readCount;
        public bool FirstTokenCanBeCanceled { get; private set; }
        public bool SecondTokenCanBeCanceled { get; private set; }
        public bool SecondTokenWasCanceled { get; private set; }

        public async Task<ClipboardSnapshot> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _readCount) == 1)
            {
                FirstTokenCanBeCanceled = cancellationToken.CanBeCanceled;
                FirstReadStarted.TrySetResult();
                try
                {
                    var cancellation = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    var completed = await Task.WhenAny(_releaseFirstRead.Task, cancellation);
                    if (ReferenceEquals(completed, cancellation))
                    {
                        await cancellation;
                    }

                    return new ClipboardSnapshot(92, null, [], null);
                }
                catch (OperationCanceledException)
                {
                    FirstReadCanceled.TrySetResult();
                    throw;
                }
                finally
                {
                    FirstReadFinished.TrySetResult();
                }
            }

            SecondTokenCanBeCanceled = cancellationToken.CanBeCanceled;
            SecondTokenWasCanceled = cancellationToken.IsCancellationRequested;
            cancellationToken.ThrowIfCancellationRequested();
            return new ClipboardSnapshot(93, null, [], "captured after failed close");
        }

        public void ReleaseFirstRead() => _releaseFirstRead.TrySetResult();
    }

    private sealed class BlockingImageNormalizer(string imagesDirectory) : IImageNormalizer
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Returned { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public string? StoredPath { get; private set; }

        public Task<StoredImage> NormalizeFileAsync(
            string sourcePath,
            Guid? id = null,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("File normalization is not expected.");

        public Task<StoredImage> NormalizeStaticFileAsync(
            string sourcePath,
            Guid? id = null,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Static file normalization is not expected.");

        public Task<StoredImage> NormalizeBitmapAsync(
            BitmapSource bitmap,
            Guid? id = null,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Bitmap normalization is not expected.");

        public async Task<StoredImage> NormalizeClipboardAsync(
            IReadOnlyList<ClipboardImageCandidate> candidates,
            Guid? id = null,
            CancellationToken cancellationToken = default)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            Started.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            return await dispatcher.InvokeAsync(
                () =>
                {
                    var storedId = id ?? Guid.NewGuid();
                    Directory.CreateDirectory(imagesDirectory);
                    StoredPath = Path.Combine(imagesDirectory, $"{storedId:N}.png");
                    File.WriteAllBytes(StoredPath, [0x89, 0x50, 0x4E, 0x47]);
                    Returned.TrySetResult();
                    return new StoredImage(storedId, $"images/{storedId:N}.png", StoredPath);
                },
                DispatcherPriority.Background);
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class RecordingBoardStore(string root) : IBoardStore
    {
        public Exception? SaveFailure { get; set; }
        public Exception? SettingsSaveFailure { get; set; }
        public TaskCompletionSource ImageDeleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SaveCompleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public BoardSnapshot? LastPersistedSnapshot { get; private set; }
        public WindowSettings? LastSavedSettings { get; private set; }
        public int SaveCount { get; private set; }
        public string ImagesDirectory { get; } = Path.Combine(root, "images");

        public Task<BoardSnapshot> LoadBoardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BoardSnapshot());

        public Task SaveBoardAsync(
            BoardSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            if (SaveFailure is not null)
            {
                throw SaveFailure;
            }

            LastPersistedSnapshot = snapshot;
            SaveCount++;
            SaveCompleted.TrySetResult();
            return Task.CompletedTask;
        }

        public Task<WindowSettings> LoadSettingsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(WindowSettings.Default);

        public Task SaveSettingsAsync(
            WindowSettings settings,
            CancellationToken cancellationToken = default)
        {
            if (SettingsSaveFailure is not null)
            {
                throw SettingsSaveFailure;
            }

            LastSavedSettings = settings;
            return Task.CompletedTask;
        }

        public bool TryDeleteImage(string? absolutePath)
        {
            if (!string.IsNullOrWhiteSpace(absolutePath) && File.Exists(absolutePath))
            {
                File.Delete(absolutePath);
            }

            ImageDeleted.TrySetResult();
            return true;
        }
    }

    private sealed class BlockingSettingsSaveBoardStore(string root) : IBoardStore
    {
        private readonly TaskCompletionSource _releaseSettingsSave = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SettingsSaveStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public int BoardSaveCount { get; private set; }
        public int SettingsSaveCount { get; private set; }
        public string ImagesDirectory { get; } = Path.Combine(root, "images");

        public Task<BoardSnapshot> LoadBoardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BoardSnapshot());

        public Task SaveBoardAsync(
            BoardSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            BoardSaveCount++;
            return Task.CompletedTask;
        }

        public Task<WindowSettings> LoadSettingsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(WindowSettings.Default);

        public async Task SaveSettingsAsync(
            WindowSettings settings,
            CancellationToken cancellationToken = default)
        {
            SettingsSaveCount++;
            SettingsSaveStarted.TrySetResult();
            await _releaseSettingsSave.Task.WaitAsync(cancellationToken);
        }

        public bool TryDeleteImage(string? absolutePath) => true;

        public void ReleaseSettingsSave() => _releaseSettingsSave.TrySetResult();
    }

    private sealed class BlockingFirstSuccessfulSaveBoardStore(string root) : IBoardStore
    {
        private readonly TaskCompletionSource _releaseFirstSave = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _saveCount;

        public TaskCompletionSource FirstSaveStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstSaveCompleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public string ImagesDirectory { get; } = Path.Combine(root, "images");

        public Task<BoardSnapshot> LoadBoardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BoardSnapshot());

        public async Task SaveBoardAsync(
            BoardSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _saveCount) != 1)
            {
                return;
            }

            FirstSaveStarted.TrySetResult();
            await _releaseFirstSave.Task.WaitAsync(cancellationToken);
            FirstSaveCompleted.TrySetResult();
        }

        public Task<WindowSettings> LoadSettingsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(WindowSettings.Default);

        public Task SaveSettingsAsync(
            WindowSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public bool TryDeleteImage(string? absolutePath) => true;

        public void ReleaseFirstSave() => _releaseFirstSave.TrySetResult();
    }

    private sealed class UiThreadFailingFirstSaveBoardStore(string root) : IBoardStore
    {
        private readonly TaskCompletionSource _failFirstSave = new();
        private int _saveCount;

        public TaskCompletionSource FirstSaveStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public string ImagesDirectory { get; } = Path.Combine(root, "images");

        public Task<BoardSnapshot> LoadBoardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BoardSnapshot());

        public async Task SaveBoardAsync(
            BoardSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _saveCount) != 1)
            {
                return;
            }

            FirstSaveStarted.TrySetResult();
            await _failFirstSave.Task.WaitAsync(cancellationToken);
            throw new IOException("Injected first-save failure.");
        }

        public Task<WindowSettings> LoadSettingsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(WindowSettings.Default);

        public Task SaveSettingsAsync(
            WindowSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public bool TryDeleteImage(string? absolutePath) => true;

        public void FailFirstSave() => _failFirstSave.TrySetResult();
    }
}
