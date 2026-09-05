using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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

public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public void WindowResources_AreScopedToTheLightShellAndRail()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            Assert.AreEqual(1, window.Resources.MergedDictionaries.Count);
            var source = window.Resources.MergedDictionaries[0].Source?.OriginalString;
            Assert.IsNotNull(source);
            StringAssert.EndsWith(
                source.Replace('\\', '/'),
                "Resources/MainWindowStyles.xaml");

            var shellBrush = (SolidColorBrush)window.FindResource("WindowShellBrush");
            var railBrush = (SolidColorBrush)window.FindResource("TabRailBrush");
            var cardBrush = (SolidColorBrush)window.FindResource("CardBrush");
            var shell = window.FindName("WindowShell") as Border;
            var rail = window.FindName("CategoryRail") as Border;

            Assert.AreEqual(Color.FromRgb(0xF7, 0xF8, 0xFA), shellBrush.Color);
            Assert.AreEqual(Color.FromRgb(0xEF, 0xF1, 0xF4), railBrush.Color);
            Assert.AreEqual(Colors.White, cardBrush.Color);
            Assert.IsNotNull(shell);
            Assert.IsNotNull(rail);
            Assert.AreSame(shellBrush, shell.Background);
            Assert.AreSame(railBrush, rail.Background);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ActiveCategoryFeedback_UsesLockedInterruptibleOpacityTransition()
    {
        var behavior = typeof(MainWindow).Assembly.GetType(
            "FloatingTransferStation.Views.CategoryFeedbackAnimation");
        Assert.IsNotNull(behavior);
        var duration = behavior.GetField(
            "TransitionDuration",
            BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        var handoff = behavior.GetField(
            "AnimationHandoffBehavior",
            BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        var factory = behavior.GetMethod(
            "CreateOpacityAnimation",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(factory);

        var animation = factory.Invoke(null, [0.25d, 1d]) as DoubleAnimation;

        Assert.IsNotNull(animation);
        Assert.AreEqual(TimeSpan.FromMilliseconds(120), duration);
        Assert.AreEqual(HandoffBehavior.SnapshotAndReplace, handoff);
        Assert.AreEqual(0.25d, animation.From);
        Assert.AreEqual(1d, animation.To);
        Assert.IsTrue(animation.Duration.HasTimeSpan);
        Assert.AreEqual(TimeSpan.FromMilliseconds(120), animation.Duration.TimeSpan);
        Assert.AreEqual(FillBehavior.Stop, animation.FillBehavior);
        var easing = animation.EasingFunction as CubicEase;
        Assert.IsNotNull(easing);
        Assert.AreEqual(EasingMode.EaseOut, easing.EasingMode);
    }

    [STATestMethod]
    public void ActiveCategoryFeedback_RemainsVisibleAfterEnabledAnimationCompletes()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = true;

        try
        {
            window.Show();
            CompleteLayout(window);
            ExpandCategory(window, BoardCategory.Inbox);
            var (layer, marker) = FindCategoryFeedback(window, BoardCategory.Inbox);

            Assert.IsTrue(layer.HasAnimatedProperties);
            Assert.IsTrue(marker.HasAnimatedProperties);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(180));

            Assert.AreEqual(1d, layer.Opacity, 0.001);
            Assert.AreEqual(1d, marker.Opacity, 0.001);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CategoryFeedback_WithImageContentStartsBeforeListLayout()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        for (var index = 0; index < 3; index++)
        {
            var path = Path.Combine(directory.Root, $"feedback-{index}.png");
            WritePng(path, width: 2, height: 2);
            board.AddImage(
                Guid.NewGuid(),
                $"images/feedback-{index}.png",
                path);
        }

        var window = CreateWindow(directory, board);
        window.Resources[SystemParameters.ClientAreaAnimationKey] = true;

        try
        {
            window.Show();
            CompleteLayout(window);
            EnterCategory(window, BoardCategory.Inbox);
            InvokePrivate(window, "ExpandIntentTimer_Tick", null, EventArgs.Empty);

            var (layer, marker) = FindCategoryFeedback(window, BoardCategory.Inbox);

            Assert.IsTrue(layer.HasAnimatedProperties);
            Assert.IsTrue(marker.HasAnimatedProperties);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ActiveCategoryFeedback_IsImmediateWhenSystemAnimationsAreDisabled()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var (layer, marker) = FindCategoryFeedback(window, BoardCategory.Inbox);

            Assert.IsFalse(layer.HasAnimatedProperties);
            Assert.IsFalse(marker.HasAnimatedProperties);
            Assert.AreEqual(1d, layer.Opacity);
            Assert.AreEqual(1d, marker.Opacity);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ActiveCategoryFeedback_ClearsRunningTransitionWhenSystemAnimationsAreDisabled()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = true;

        try
        {
            window.Show();
            CompleteLayout(window);
            var (layer, marker) = FindCategoryFeedback(window, BoardCategory.Inbox);
            ExpandCategory(window, BoardCategory.Inbox);
            Assert.IsTrue(layer.HasAnimatedProperties);
            Assert.IsTrue(marker.HasAnimatedProperties);

            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            CompleteLayout(window);

            Assert.IsFalse(layer.HasAnimatedProperties);
            Assert.IsFalse(marker.HasAnimatedProperties);
            Assert.AreEqual(1d, layer.Opacity);
            Assert.AreEqual(1d, marker.Opacity);

            window.Resources[SystemParameters.ClientAreaAnimationKey] = true;
            CompleteLayout(window);
            Assert.IsFalse(layer.HasAnimatedProperties);
            Assert.IsFalse(marker.HasAnimatedProperties);
            Assert.AreEqual(1d, layer.Opacity);
            Assert.AreEqual(1d, marker.Opacity);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CategoryTab_MouseClickSwitchesOnlyTheDefaultCaptureMarker()
    {
        using var directory = new TestDirectory();
        var state = new DefaultCaptureCategoryState();
        var window = CreateWindow(directory, new BoardService(), state);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var customer = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);
            var customerTab = FindCategoryTab(window, customer);

            customerTab.RaiseEvent(NewMouseButtonEventArgs(
                UIElement.MouseLeftButtonUpEvent,
                customerTab));
            CompleteLayout(window);

            Assert.AreEqual(BoardCategory.CustomerOriginal, state.Current);
            Assert.AreEqual(BoardCategory.Reference, viewModel.ActivePanel!.Category);
            Assert.AreEqual(1, viewModel.Categories.Count(category => category.IsDefaultCapture));
            Assert.AreSame(customer, viewModel.DefaultCapturePanel);

            var markerStyle = (Style)window.FindResource("CategoryDefaultMarkerStyle");
            foreach (var category in viewModel.Categories)
            {
                var tab = FindCategoryTab(window, category);
                var marker = FindDescendants<TextBlock>(tab)
                    .Single(candidate => ReferenceEquals(candidate.Style, markerStyle));
                Assert.AreEqual("◎", marker.Text);
                Assert.AreEqual(category.IsDefaultCapture ? 1d : 0d, marker.Opacity);
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void WindowShell_RendersTransparentLeftCornersAndSquareRightEdge()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            var panel = (Grid)window.FindName("PanelContentHost");
            panel.BeginAnimation(UIElement.OpacityProperty, null);
            panel.Opacity = 1d;
            CompleteLayout(window);

            var shell = (Border)window.FindName("WindowShell");
            var width = (int)Math.Ceiling(shell.ActualWidth);
            var height = (int)Math.Ceiling(shell.ActualHeight);
            var bitmap = new RenderTargetBitmap(
                width,
                height,
                96,
                96,
                PixelFormats.Pbgra32);
            bitmap.Render(shell);
            var stride = width * 4;
            var pixels = new byte[stride * height];
            bitmap.CopyPixels(pixels, stride, 0);

            byte AlphaAt(int x, int y) => pixels[(y * stride) + (x * 4) + 3];

            var topLeftAlpha = AlphaAt(2, 2);
            var bottomLeftAlpha = AlphaAt(2, height - 3);
            var topRightAlpha = AlphaAt(width - 3, 2);
            Assert.IsTrue(
                topLeftAlpha <= 16,
                $"The exposed top-left corner must stay transparent; alpha was {topLeftAlpha}.");
            Assert.IsTrue(
                bottomLeftAlpha <= 16,
                $"The exposed bottom-left corner must stay transparent; alpha was {bottomLeftAlpha}.");
            Assert.IsTrue(
                topRightAlpha >= 240,
                $"The docked right edge must remain square and opaque; alpha was {topRightAlpha}.");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void BoardList_UsesPixelScrollingWithRecyclingVirtualization()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var board = new BoardService();
        var normalizer = new ImageNormalizer(paths.ImagesDirectory);
        var operationGate = new BoardOperationGate();
        var clipboard = new ClipboardCaptureService(
            new NeverReadClipboardReader(),
            normalizer,
            board,
            store,
            _ => { },
            operationGate: operationGate);
        var window = new MainWindow(
            board,
            store,
            WindowSettings.Default,
            clipboard,
            new BoardMutationService(board, store, _ => { }, operationGate),
            new DragPayloadService(),
            new ExternalDropPayloadReader(new WindowsDataImageReader()),
            new ExternalDropImportService(
                normalizer,
                board,
                store,
                _ => { },
                operationGate));

        try
        {
            var list = (ListBox)window.FindName("BoardList");

            Assert.IsTrue(ScrollViewer.GetCanContentScroll(list));
            Assert.AreEqual(ScrollUnit.Pixel, VirtualizingPanel.GetScrollUnit(list));
            Assert.IsTrue(VirtualizingPanel.GetIsVirtualizing(list));
            Assert.AreEqual(
                VirtualizationMode.Recycling,
                VirtualizingPanel.GetVirtualizationMode(list));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Header_HasDedicatedDragAndHoverActionRegions()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var dragRegion = window.FindName("HeaderDragRegion") as Thumb;
            var actionRegion = window.FindName("HeaderActionRegion") as Border;
            var actions = window.FindName("HeaderActions") as StackPanel;
            var reset = window.FindName("ResetWindowButton") as Button;
            var delete = window.FindName("DeleteContentButton") as Button;

            Assert.IsNotNull(dragRegion);
            Assert.IsNotNull(actionRegion);
            Assert.IsNotNull(actions);
            Assert.IsNotNull(reset);
            Assert.IsNotNull(delete);
            Assert.AreEqual(112d, dragRegion.Width);
            Assert.AreEqual(Cursors.SizeNS, dragRegion.Cursor);
            Assert.AreEqual(Cursors.Arrow, actionRegion.Cursor);
            Assert.AreEqual(0, Grid.GetColumn(dragRegion));
            Assert.AreEqual(1, Grid.GetColumn(actionRegion));
            Assert.AreEqual(0d, actions.Opacity);
            Assert.IsFalse(actions.IsHitTestVisible);
            Assert.IsFalse(delete.IsEnabled);

            InvokePrivate(
                window,
                "HeaderActionRegion_MouseEnter",
                actionRegion,
                NewMouseEventArgs());

            Assert.AreEqual(1d, actions.Opacity);
            Assert.IsTrue(actions.IsHitTestVisible);

            InvokePrivate(
                window,
                "HeaderActionRegion_MouseLeave",
                actionRegion,
                NewMouseEventArgs());

            Assert.AreEqual(0d, actions.Opacity);
            Assert.IsFalse(actions.IsHitTestVisible);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Header_DragAndActionRegionsHaveNonOverlappingHitBounds()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            var dragRegion = (Thumb?)window.FindName("HeaderDragRegion");
            var actionRegion = (Border?)window.FindName("HeaderActionRegion");
            Assert.IsNotNull(dragRegion);
            Assert.IsNotNull(actionRegion);

            var dragBounds = dragRegion.TranslatePoint(new Point(), window);
            var actionBounds = actionRegion.TranslatePoint(new Point(), window);
            Assert.IsTrue(
                dragBounds.X + dragRegion.ActualWidth <= actionBounds.X + 0.01,
                $"Header regions overlap: drag right={dragBounds.X + dragRegion.ActualWidth}, action left={actionBounds.X}");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ResetAction_RestoresDefaultGeometryAndPreservesBoardViewState()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var prompt = board.AddText("保持内容", BoardCategory.Prompt);
        var store = new RecordingBoardStore(directory.Root);
        var customSettings = new WindowSettings(520, 410, 300);
        var window = CreateWindow(board, store, customSettings);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Prompt);
            CompleteLayout(window);
            var scrollState = GetPrivateField<CategoryScrollState>(window, "_scrollState");
            scrollState.Save(BoardCategory.Prompt, 87d);
            var reset = window.FindName("ResetWindowButton") as Button;
            Assert.IsNotNull(reset);

            reset.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            CompleteLayout(window);

            var workArea = SystemParameters.WorkArea;
            var expected = customSettings.ResetToDefault(workArea.Width, workArea.Height);
            var viewModel = (MainWindowViewModel)window.DataContext;
            Assert.AreEqual(expected.PanelWidth + WindowSettings.TabWidth, window.Width, 0.5);
            Assert.AreEqual(expected.WindowHeight, window.Height, 0.5);
            Assert.AreEqual(workArea.Top + expected.Top, window.Top, 0.5);
            Assert.AreEqual(workArea.Right - window.Width, window.Left, 0.5);
            Assert.AreEqual(BoardCategory.Prompt, viewModel.ActivePanel!.Category);
            Assert.AreSame(prompt, board.Items(BoardCategory.Prompt).Single());
            Assert.AreEqual(87d, scrollState.GetClamped(BoardCategory.Prompt, 1000d));
            Assert.AreEqual(expected, store.LastSavedSettings);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CategoryHover_WaitsForSixtyMillisecondIntentBeforeStableExpansion()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var settings = WindowSettings.Default;
        var window = CreateWindow(directory, board);

        try
        {
            var timer = GetPrivateField<DispatcherTimer>(window, "_expandIntentTimer");

            Assert.AreEqual(TimeSpan.FromMilliseconds(60), timer.Interval);
            Assert.AreEqual(WindowSettings.TabWidth, window.Width);

            EnterCategory(window, BoardCategory.Inbox);

            Assert.IsTrue(timer.IsEnabled);
            Assert.AreEqual(WindowSettings.TabWidth, window.Width);

            InvokePrivate(window, "ExpandIntentTimer_Tick", null, EventArgs.Empty);

            Assert.AreEqual(settings.PanelWidth + WindowSettings.TabWidth, window.Width);
            Assert.IsFalse(window.HasAnimatedProperties);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CollapsedHandle_ClipsRailBackgroundToTransparentLeftCornersAndSquareRightEdge()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            window.Show();
            CompleteLayout(window);
            var shell = (Border)window.FindName("WindowShell");
            var width = (int)Math.Ceiling(shell.ActualWidth);
            var height = (int)Math.Ceiling(shell.ActualHeight);
            var bitmap = new RenderTargetBitmap(
                width,
                height,
                96,
                96,
                PixelFormats.Pbgra32);
            bitmap.Render(shell);
            var stride = width * 4;
            var pixels = new byte[stride * height];
            bitmap.CopyPixels(pixels, stride, 0);

            byte AlphaAt(int x, int y) => pixels[(y * stride) + (x * 4) + 3];

            Assert.IsTrue(
                AlphaAt(2, 2) <= 16,
                $"Collapsed top-left exterior must be transparent; alpha was {AlphaAt(2, 2)}.");
            Assert.IsTrue(
                AlphaAt(2, height - 3) <= 16,
                $"Collapsed bottom-left exterior must be transparent; alpha was {AlphaAt(2, height - 3)}.");
            Assert.IsTrue(
                AlphaAt(width - 3, 2) >= 240,
                $"Collapsed docked right edge must stay square; alpha was {AlphaAt(width - 3, 2)}.");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CollapsedWindow_UsesOnlyTheDefaultCategoryNativeRowAndReleasesOtherRows()
    {
        using var directory = new TestDirectory();
        var state = new DefaultCaptureCategoryState();
        var window = CreateWindow(directory, new BoardService(), state);

        try
        {
            window.Show();
            CompleteLayout(window);
            var rowHeight = WindowSettings.Default.WindowHeight / BoardCategoryCatalog.Ordered.Count;
            var expectedTop = SystemParameters.WorkArea.Top + WindowSettings.Default.Top + (3 * rowHeight);
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;

            Assert.AreEqual(rowHeight, window.Height, 0.5);
            Assert.AreEqual(expectedTop, window.Top, 0.5);
            Assert.IsTrue(GetWindowRect(handle, out var collapsedBounds));

            for (var row = 0; row < 3; row++)
            {
                var point = new NativePoint(
                    collapsedBounds.Left + (collapsedBounds.Width / 2),
                    collapsedBounds.Top - ((3 - row) * collapsedBounds.Height) +
                    (collapsedBounds.Height / 2));
                Assert.AreNotEqual(
                    handle,
                    WindowFromPoint(point),
                    $"Hidden category row {row} must not belong to the topmost station window.");
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CollapsedWindow_ShowsDefaultHandleWithoutActiveFeedbackThenExpandsFullRail()
    {
        using var directory = new TestDirectory();
        var state = new DefaultCaptureCategoryState();
        var window = CreateWindow(directory, new BoardService(), state);

        try
        {
            window.Show();
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var collapsedHandle = window.FindName("CollapsedCategoryHandle") as ContentControl;
            var rail = (Border)window.FindName("CategoryRail");

            Assert.IsNotNull(collapsedHandle);
            Assert.AreEqual(Visibility.Visible, collapsedHandle.Visibility);
            Assert.AreEqual(Visibility.Hidden, rail.Visibility);
            Assert.AreSame(viewModel.DefaultCapturePanel, collapsedHandle.Content);
            Assert.IsFalse(viewModel.IsPanelExpanded);
            Assert.AreEqual(0, viewModel.Categories.Count(category => category.IsActive));
            var defaultMarkerStyle = (Style)window.FindResource("CategoryDefaultMarkerStyle");
            var activeLayerStyle = (Style)window.FindResource("CategoryActiveLayerStyle");
            var defaultMarker = FindDescendants<TextBlock>(collapsedHandle)
                .Single(candidate => ReferenceEquals(candidate.Style, defaultMarkerStyle));
            var activeLayer = FindDescendants<Border>(collapsedHandle)
                .Single(candidate => ReferenceEquals(candidate.Style, activeLayerStyle));
            Assert.AreEqual(1d, defaultMarker.Opacity);
            Assert.AreEqual(0d, activeLayer.Opacity);

            EnterCategory(window, BoardCategory.Inbox);
            InvokePrivate(window, "ExpandIntentTimer_Tick", null, EventArgs.Empty);
            CompleteLayout(window);

            Assert.AreEqual(Visibility.Hidden, collapsedHandle.Visibility);
            Assert.AreEqual(Visibility.Visible, rail.Visibility);
            Assert.IsTrue(viewModel.IsPanelExpanded);
            Assert.AreEqual(1, viewModel.Categories.Count(category => category.IsActive));
            Assert.AreEqual(WindowSettings.Default.WindowHeight, window.Height, 0.5);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Close_WaitsForBlockedExternalNormalizeAndCleanupBeforeClosed()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var store = new RecordingBoardStore(directory.Root);
        var normalizer = new BlockingImageNormalizer(store.ImagesDirectory);
        var window = CreateWindow(
            board,
            store,
            WindowSettings.Default,
            new DefaultCaptureCategoryState(),
            normalizer);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var normalizeReturnedWhenClosed = false;
        var imageCleanupCompletedWhenClosed = false;
        window.Closed += (_, _) =>
        {
            normalizeReturnedWhenClosed = normalizer.Returned.Task.IsCompleted;
            imageCleanupCompletedWhenClosed = store.ImageDeleted.Task.IsCompleted;
            closed.TrySetResult();
        };

        try
        {
            window.Show();
            CompleteLayout(window);
            var data = new DataObject();
            data.SetData("PNG", new byte[] { 0x89, 0x50, 0x4E, 0x47 });
            var collapsedTab = FindCollapsedCategoryTab(window);
            collapsedTab.RaiseEvent(NewDragEventArgs(data, DragDrop.DragEnterEvent, collapsedTab));
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var inboxTab = FindCategoryTab(window, viewModel.DefaultCapturePanel);

            inboxTab.RaiseEvent(NewDragEventArgs(data, DragDrop.DropEvent, inboxTab));
            PumpDispatcherUntil(window.Dispatcher, normalizer.Started.Task);

            window.Close();
            CompleteLayout(window);

            Assert.IsTrue(window.IsVisible, "The tracked external import must keep closing pending.");

            normalizer.Release();
            PumpDispatcherUntil(
                window.Dispatcher,
                Task.WhenAll(normalizer.Returned.Task, store.ImageDeleted.Task, closed.Task));

            Assert.IsTrue(normalizeReturnedWhenClosed);
            Assert.IsTrue(imageCleanupCompletedWhenClosed);
            Assert.IsNotNull(normalizer.StoredPath);
            Assert.IsFalse(File.Exists(normalizer.StoredPath));
            Assert.AreEqual(0, board.Items(BoardCategory.Inbox).Count);
        }
        finally
        {
            normalizer.Release();
            if (!normalizer.Returned.Task.IsCompleted || !store.ImageDeleted.Task.IsCompleted)
            {
                PumpDispatcherUntil(
                    window.Dispatcher,
                    Task.WhenAll(normalizer.Returned.Task, store.ImageDeleted.Task));
            }

            if (window.IsVisible)
            {
                CloseWindow(window);
            }
        }
    }

    [STATestMethod]
    public void FailedFinalSave_ReplacesCanceledTokenThenAcceptsExternalDropAndCloses()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var store = new FailingFirstSaveBoardStore(directory.Root);
        var window = CreateWindow(
            board,
            store,
            WindowSettings.Default,
            new DefaultCaptureCategoryState());
        var closeFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.IsEnabledChanged += (_, eventArgs) =>
        {
            if (eventArgs.NewValue is true)
            {
                closeFailed.TrySetResult();
            }
        };

        window.Show();
        window.Close();
        PumpDispatcherUntil(window.Dispatcher, store.FirstSaveStarted.Task);
        store.FailFirstSave();
        PumpDispatcherUntil(window.Dispatcher, closeFailed.Task);

        var data = new DataObject(DataFormats.UnicodeText, "关闭失败后继续拖入");
        var collapsedTab = FindCollapsedCategoryTab(window);
        collapsedTab.RaiseEvent(NewDragEventArgs(data, DragDrop.DragEnterEvent, collapsedTab));
        CompleteLayout(window);
        var viewModel = (MainWindowViewModel)window.DataContext;
        var prompt = viewModel.Categories.Single(
            category => category.Category == BoardCategory.Prompt);
        var promptTab = FindCategoryTab(window, prompt);
        var drop = NewDragEventArgs(data, DragDrop.DropEvent, promptTab);
        promptTab.RaiseEvent(drop);
        PumpDispatcherUntil(window.Dispatcher, store.SuccessfulSaveCompleted.Task);

        Assert.IsTrue(window.IsEnabled);
        Assert.AreEqual(DragDropEffects.Copy, drop.Effects);
        Assert.AreEqual("关闭失败后继续拖入", board.Items(BoardCategory.Prompt).Single().Text);
        Assert.AreEqual(
            "关闭失败后继续拖入",
            store.LastPersistedSnapshot!.Items.Single().Text);

        CloseWindow(window);
    }

    [STATestMethod]
    public void CollapsedWindow_MovesToTheNewDefaultCategoryRow()
    {
        using var directory = new TestDirectory();
        var state = new DefaultCaptureCategoryState();
        var window = CreateWindow(directory, new BoardService(), state);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            var viewModel = (MainWindowViewModel)window.DataContext;
            viewModel.SetDefaultCaptureCategory(BoardCategory.Reference);
            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
            CompleteLayout(window);

            var rowHeight = WindowSettings.Default.WindowHeight / BoardCategoryCatalog.Ordered.Count;
            var expectedTop = SystemParameters.WorkArea.Top + WindowSettings.Default.Top + rowHeight;
            Assert.AreEqual(expectedTop, window.Top, 0.5);
            Assert.AreEqual(rowHeight, window.Height, 0.5);
            Assert.AreEqual(BoardCategory.Reference, viewModel.DefaultCapturePanel.Category);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ExpandedPlacement_KeepsTheStationaryRailPointerInsideEveryGeometryUpdate()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            CompleteLayout(window);
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            Assert.IsTrue(GetWindowRect(handle, out var initialBounds));
            var stationaryPointer = (
                X: initialBounds.Left + (initialBounds.Width / 2),
                Y: initialBounds.Top + (initialBounds.Height / 2));
            var observedBounds = new List<NativeRect>();

            void RecordBounds(object? sender, EventArgs eventArgs)
            {
                Assert.IsTrue(GetWindowRect(handle, out var bounds));
                observedBounds.Add(bounds);
            }

            window.LocationChanged += RecordBounds;
            window.SizeChanged += RecordBounds;
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            window.LocationChanged -= RecordBounds;
            window.SizeChanged -= RecordBounds;

            Assert.IsNotEmpty(observedBounds);
            Assert.IsTrue(
                observedBounds.All(bounds => bounds.Contains(stationaryPointer.X, stationaryPointer.Y)),
                $"A stationary pointer over the collapsed rail left the window during: {string.Join("; ", observedBounds)}");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void BoardList_HasOneRecycledListAndNonLayoutInsertionIndicator()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            window.Show();
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var indicator = window.FindName("InsertionIndicator") as Border;
            Assert.IsNotNull(indicator);
            var indicatorParent = VisualTreeHelper.GetParent(indicator);

            Assert.AreEqual(1, FindDescendants<ListBox>(window).Count());
            Assert.AreEqual(2d, indicator.Height);
            Assert.IsFalse(indicator.IsHitTestVisible);
            Assert.AreEqual(Visibility.Collapsed, indicator.Visibility);
            Assert.IsInstanceOfType(indicatorParent, typeof(Canvas));
            Assert.AreEqual(Grid.GetRow(list), Grid.GetRow((Canvas)indicatorParent));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void TextCard_ReservesFixedPinAndSelectionColumns()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText(new string('x', 240));
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            var list = (ListBox)window.FindName("BoardList");
            var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(item);
            Assert.IsNotNull(container);
            var buttons = FindDescendants<Button>(container)
                .Where(candidate => ReferenceEquals(candidate.Tag, item))
                .ToArray();
            Assert.HasCount(2, buttons);
            var pinButton = buttons.Single(
                candidate => Equals(candidate.CommandParameter, "TogglePin"));
            var selectionButton = buttons.Single(
                candidate => Equals(candidate.CommandParameter, "ToggleSelection"));
            var contentGrid = pinButton.Parent as Grid;
            var text = FindDescendants<TextBlock>(container)
                .Single(candidate => candidate.Text == item.Text);

            Assert.IsFalse(buttons.Any(
                candidate => Equals(candidate.CommandParameter, "Delete")));
            Assert.AreEqual(30d, pinButton.Width);
            Assert.AreEqual(30d, pinButton.Height);
            Assert.AreEqual(0d, pinButton.Opacity);
            Assert.IsFalse(pinButton.IsHitTestVisible);
            Assert.AreEqual(Visibility.Visible, pinButton.Visibility);
            Assert.IsInstanceOfType(pinButton.Content, typeof(Viewbox));
            Assert.IsNotNull(FindDescendant<System.Windows.Shapes.Path>(pinButton));
            Assert.AreEqual(30d, selectionButton.Width);
            Assert.AreEqual(30d, selectionButton.Height);
            Assert.IsNotNull(contentGrid);
            Assert.AreEqual(1, Grid.GetColumn(pinButton));
            Assert.AreEqual(2, Grid.GetColumn(selectionButton));
            Assert.AreEqual(3, contentGrid.ColumnDefinitions.Count);
            Assert.AreEqual(new GridLength(30), contentGrid.ColumnDefinitions[1].Width);
            Assert.AreEqual(new GridLength(30), contentGrid.ColumnDefinitions[2].Width);
            Assert.AreEqual(14d, text.FontSize);
            Assert.AreEqual(18d, text.LineHeight);
            Assert.AreEqual(90d, text.MaxHeight);
            Assert.AreEqual(TextTrimming.CharacterEllipsis, text.TextTrimming);

            item.IsPinned = true;
            CompleteLayout(window);
            Assert.AreEqual(1d, pinButton.Opacity);
            Assert.IsTrue(pinButton.IsHitTestVisible);
            Assert.AreSame(window.FindResource("AccentBrush"), pinButton.Foreground);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardOperationColumns_DoNotMoveWhenPinBecomesVisible()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("stable actions");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            var list = (ListBox)window.FindName("BoardList");
            var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(item);
            Assert.IsNotNull(container);
            var pin = FindDescendants<Button>(container)
                .Single(candidate => Equals(candidate.CommandParameter, "TogglePin"));
            var selection = FindDescendants<Button>(container)
                .Single(candidate => Equals(candidate.CommandParameter, "ToggleSelection"));
            var contentGrid = (Grid?)pin.Parent;
            Assert.IsNotNull(contentGrid);

            var beforeGridWidth = contentGrid.ActualWidth;
            var beforeSelectionWidth = selection.ActualWidth;
            var beforeSelectionPosition = selection.TranslatePoint(new Point(), contentGrid);

            item.IsPinned = true;
            CompleteLayout(window);

            Assert.AreEqual(beforeGridWidth, contentGrid.ActualWidth, 0.01);
            Assert.AreEqual(beforeSelectionWidth, selection.ActualWidth, 0.01);
            Assert.AreEqual(beforeSelectionPosition.X, selection.TranslatePoint(new Point(), contentGrid).X, 0.01);
            Assert.AreEqual(beforeSelectionPosition.Y, selection.TranslatePoint(new Point(), contentGrid).Y, 0.01);
            Assert.AreEqual(1d, pin.Opacity);
            Assert.IsTrue(pin.IsHitTestVisible);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ImageCard_UsesThumbnailConverterAtFiveHundredTwelvePixelsWithoutLockingFile()
    {
        using var directory = new TestDirectory();
        var imagePath = Path.Combine(
            Path.GetTempPath(),
            $"悬浮中转站-ui-thumbnail-{Guid.NewGuid():N}.png");
        WritePng(imagePath, width: 1200, height: 600);
        var board = new BoardService();
        var item = board.AddImage(Guid.NewGuid(), "large.png", imagePath);
        var window = CreateWindow(directory, board);
        Image? image = null;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            var list = (ListBox)window.FindName("BoardList");
            var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(item);
            Assert.IsNotNull(container);
            image = FindDescendants<Image>(container).Single();
            var source = image.Source as BitmapSource;

            Assert.IsNotNull(source);
            var pixelWidth = source.PixelWidth;
            var lockError = TryDeleteFile(imagePath);

            Assert.IsNull(lockError, "The image binding must not retain a source-file handle.");
            Assert.IsTrue(pixelWidth <= 512);
            Assert.AreEqual(120d, image.Height);
            Assert.AreEqual(Stretch.Uniform, image.Stretch);
            Assert.IsFalse(File.Exists(imagePath));
        }
        finally
        {
            if (image is not null)
            {
                BindingOperations.ClearBinding(image, Image.SourceProperty);
                image.Source = null;
            }

            CloseWindow(window);
            _ = TryDeleteFile(imagePath);
        }
    }

    [STATestMethod]
    public void StatusOverlay_SharesTheListRowAndCardsDoNotRouteDelete()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("delete me");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var overlay = window.FindName("StatusOverlay") as Border;
            Assert.IsNotNull(overlay);
            var panel = (Grid)window.FindName("PanelContentHost");
            var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(item);
            Assert.IsNotNull(container);
            var cardButtons = FindDescendants<Button>(container)
                .Where(candidate => ReferenceEquals(candidate.Tag, item))
                .ToArray();
            var delete = window.FindName("DeleteContentButton") as Button;

            Assert.AreEqual(2, panel.RowDefinitions.Count);
            Assert.IsTrue(panel.RowDefinitions[1].Height.IsStar);
            Assert.AreEqual(Grid.GetRow(list), Grid.GetRow(overlay));
            Assert.AreEqual(Visibility.Collapsed, overlay.Visibility);
            Assert.IsFalse(overlay.IsHitTestVisible);
            Assert.IsFalse(cardButtons.Any(candidate =>
                Equals(candidate.CommandParameter, "Delete")));
            Assert.IsNotNull(delete);
            Assert.IsNotNull(GetPrivateMethod("BoardList_ButtonClick"));
            Assert.IsNull(GetPrivateMethod("DeleteButton_Click"));
            Assert.IsNull(GetPrivateMethod("ClearCategoryButton_Click"));
            Assert.AreSame(item, board.Items(BoardCategory.Inbox).Single());
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Close_DuringFailingDeleteWaitsForRollbackBeforePersistingFinalSnapshot()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("restore before close");
        var store = new FailingFirstSaveBoardStore(directory.Root);
        var operationGate = new BoardOperationGate();
        var clipboard = new ClipboardCaptureService(
            new NeverReadClipboardReader(),
            new ImageNormalizer(store.ImagesDirectory),
            board,
            store,
            _ => { },
            operationGate: operationGate);
        var mutations = new BoardMutationService(board, store, _ => { }, operationGate);
        var window = new MainWindow(
            board,
            store,
            WindowSettings.Default,
            clipboard,
            mutations,
            new DragPayloadService(),
            new ExternalDropPayloadReader(new WindowsDataImageReader()),
            new ExternalDropImportService(
                new ImageNormalizer(store.ImagesDirectory),
                board,
                store,
                _ => { },
                operationGate));
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();

        Task<bool>? delete = null;
        window.Dispatcher.BeginInvoke(new Action(() => delete = mutations.DeleteAsync(item.Id)));
        PumpDispatcherUntil(window.Dispatcher, store.FirstSaveStarted.Task);

        var firstCloseReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Dispatcher.BeginInvoke(new Action(() =>
        {
            window.Close(); // The first request is intentionally observed before final shutdown.
            firstCloseReturned.TrySetResult();
        }));
        PumpDispatcherUntil(window.Dispatcher, firstCloseReturned.Task);
        var firstCloseCompletedTheWindow = closed.Task.IsCompleted;

        store.FailFirstSave();
        Assert.IsNotNull(delete);
        PumpDispatcherUntil(window.Dispatcher, Task.WhenAll(delete, closed.Task));

        Assert.IsFalse(firstCloseCompletedTheWindow);
        Assert.IsFalse(delete.GetAwaiter().GetResult());
        CollectionAssert.AreEqual(
            board.CreateSnapshot().Items
                .Select(candidate => (candidate.Id, candidate.Category, candidate.Order))
                .ToArray(),
            store.LastPersistedSnapshot!.Items
                .Select(candidate => (candidate.Id, candidate.Category, candidate.Order))
                .ToArray());
    }

    [STATestMethod]
    public void Close_FinalSaveFailureRestoresInputAndAllowsNextMutationAndClose()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("move after failed close");
        var store = new FailingFirstSaveBoardStore(
            directory.Root,
            new InvalidOperationException("Injected unexpected final-write failure."));
        var operationGate = new BoardOperationGate();
        var reader = new CountingClipboardReader();
        var clipboard = new ClipboardCaptureService(
            reader,
            new ImageNormalizer(store.ImagesDirectory),
            board,
            store,
            _ => { },
            operationGate: operationGate);
        var mutations = new BoardMutationService(board, store, _ => { }, operationGate);
        var window = new MainWindow(
            board,
            store,
            WindowSettings.Default,
            clipboard,
            mutations,
            new DragPayloadService(),
            new ExternalDropPayloadReader(new WindowsDataImageReader()),
            new ExternalDropImportService(
                new ImageNormalizer(store.ImagesDirectory),
                board,
                store,
                _ => { },
                operationGate));
        var viewModel = (MainWindowViewModel)window.DataContext;
        var closeFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(MainWindowViewModel.StatusText) &&
                viewModel.StatusText == "退出前保存失败，悬浮中转站暂未关闭。")
            {
                closeFailed.TrySetResult();
            }
        };
        window.Show();

        var closeReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Dispatcher.BeginInvoke(new Action(() =>
        {
            window.Close();
            closeReturned.TrySetResult();
        }));
        PumpDispatcherUntil(window.Dispatcher, closeReturned.Task);
        Assert.IsFalse(window.IsEnabled);
        InvokePrivate(window, "WndProc", nint.Zero, 0x031D, nint.Zero, nint.Zero, false);
        var clipboardReadsDuringClose = reader.ReadCount;

        store.FailFirstSave();
        PumpDispatcherUntil(window.Dispatcher, closeFailed.Task);
        var inputRestored = window.IsEnabled;
        var compactStatus = (Popup)window.FindName("CompactStatusPopup");

        var move = mutations.MoveAsync(item.Id, BoardCategory.Prompt, 0);
        PumpDispatcherUntil(window.Dispatcher, move);
        CloseWindow(window);

        Assert.IsTrue(inputRestored);
        Assert.AreEqual(0, clipboardReadsDuringClose);
        Assert.IsTrue(compactStatus.IsOpen);
        Assert.IsTrue(move.GetAwaiter().GetResult());
        Assert.AreEqual(BoardCategory.Prompt, item.Category);
        Assert.AreEqual("退出前保存失败，悬浮中转站暂未关闭。", viewModel.StatusText);
        CollectionAssert.AreEqual(
            board.CreateSnapshot().Items
                .Select(candidate => (candidate.Id, candidate.Category, candidate.Order))
                .ToArray(),
            store.LastPersistedSnapshot!.Items
                .Select(candidate => (candidate.Id, candidate.Category, candidate.Order))
                .ToArray());
    }

    [STATestMethod]
    public void Close_WaitsForClipboardNormalizeAndCleanupBeforeClosed()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var store = new RecordingBoardStore(directory.Root);
        var normalizer = new BlockingImageNormalizer(store.ImagesDirectory);
        var operationGate = new BoardOperationGate();
        var clipboard = new ClipboardCaptureService(
            new SingleClipboardReader(
                new ClipboardSnapshot(
                    91,
                    null,
                    [],
                    null,
                    [ClipboardImageCandidate.FromEncoded("PNG", [0x89, 0x50])])),
            normalizer,
            board,
            store,
            _ => { },
            operationGate: operationGate);
        var window = new MainWindow(
            board,
            store,
            WindowSettings.Default,
            clipboard,
            new BoardMutationService(board, store, _ => { }, operationGate),
            new DragPayloadService(),
            new ExternalDropPayloadReader(new WindowsDataImageReader()),
            new ExternalDropImportService(
                normalizer,
                board,
                store,
                _ => { },
                operationGate));
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var normalizeReturnedWhenClosed = false;
        var imageCleanupCompletedWhenClosed = false;
        window.Closed += (_, _) =>
        {
            normalizeReturnedWhenClosed = normalizer.Returned.Task.IsCompleted;
            imageCleanupCompletedWhenClosed = store.ImageDeleted.Task.IsCompleted;
            closed.TrySetResult();
        };
        window.Show();

        InvokePrivate(window, "WndProc", nint.Zero, 0x031D, nint.Zero, nint.Zero, false);
        PumpDispatcherUntil(window.Dispatcher, normalizer.Started.Task);

        window.Close();
        Assert.IsTrue(window.IsVisible);

        normalizer.Release();
        PumpDispatcherUntil(
            window.Dispatcher,
            Task.WhenAll(normalizer.Returned.Task, store.ImageDeleted.Task, closed.Task));

        Assert.IsTrue(normalizeReturnedWhenClosed);
        Assert.IsTrue(imageCleanupCompletedWhenClosed);
        Assert.IsNotNull(normalizer.StoredPath);
        Assert.IsFalse(File.Exists(normalizer.StoredPath));
        Assert.AreEqual(0, board.Items(BoardCategory.Inbox).Count);
        Assert.AreEqual(1, store.SaveCount);
        CollectionAssert.AreEqual(
            board.CreateSnapshot().Items
                .Select(candidate => (candidate.Id, candidate.Category, candidate.Order))
                .ToArray(),
            store.LastPersistedSnapshot!.Items
                .Select(candidate => (candidate.Id, candidate.Category, candidate.Order))
                .ToArray());
    }

    [STATestMethod]
    public void Close_FinalSaveFailureReplacesCanceledClipboardTokenAndCapturesNextUpdate()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var store = new FailingFirstSaveBoardStore(directory.Root);
        var operationGate = new BoardOperationGate();
        var reader = new CancelThenTextClipboardReader();
        var clipboard = new ClipboardCaptureService(
            reader,
            new ImageNormalizer(store.ImagesDirectory),
            board,
            store,
            _ => { },
            operationGate: operationGate);
        var window = new MainWindow(
            board,
            store,
            WindowSettings.Default,
            clipboard,
            new BoardMutationService(board, store, _ => { }, operationGate),
            new DragPayloadService(),
            new ExternalDropPayloadReader(new WindowsDataImageReader()),
            new ExternalDropImportService(
                new ImageNormalizer(store.ImagesDirectory),
                board,
                store,
                _ => { },
                operationGate));
        var closeFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.IsEnabledChanged += (_, eventArgs) =>
        {
            if (eventArgs.NewValue is true)
            {
                closeFailed.TrySetResult();
            }
        };
        window.Show();

        InvokePrivate(window, "WndProc", nint.Zero, 0x031D, nint.Zero, nint.Zero, false);
        PumpDispatcherUntil(window.Dispatcher, reader.FirstReadStarted.Task);

        window.Close();
        PumpDispatcherUntil(window.Dispatcher, store.FirstSaveStarted.Task);
        var firstReadWasCanceledBeforeFinalSave = reader.FirstReadCanceled.Task.IsCompleted;

        store.FailFirstSave();
        PumpDispatcherUntil(window.Dispatcher, closeFailed.Task);
        InvokePrivate(window, "WndProc", nint.Zero, 0x031D, nint.Zero, nint.Zero, false);
        PumpDispatcherUntil(window.Dispatcher, store.SuccessfulSaveCompleted.Task);

        reader.ReleaseFirstRead();
        PumpDispatcherUntil(window.Dispatcher, reader.FirstReadFinished.Task);
        CloseWindow(window);

        Assert.IsTrue(reader.FirstTokenCanBeCanceled);
        Assert.IsTrue(firstReadWasCanceledBeforeFinalSave);
        Assert.IsTrue(reader.SecondTokenCanBeCanceled);
        Assert.IsFalse(reader.SecondTokenWasCanceled);
        Assert.AreEqual(2, reader.ReadCount);
        Assert.AreEqual("captured after failed close", board.Items(BoardCategory.Inbox).Single().Text);
        Assert.AreEqual(
            "captured after failed close",
            store.LastPersistedSnapshot!.Items.Single().Text);
    }

    [STATestMethod]
    public void CategoryHover_LeavingBeforeIntentTickKeepsWindowCollapsed()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            var timer = GetPrivateField<DispatcherTimer>(window, "_expandIntentTimer");
            var collapseTimer = GetPrivateField<DispatcherTimer>(window, "_collapseTimer");
            EnterCategory(window, BoardCategory.Reference);

            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());

            Assert.IsFalse(timer.IsEnabled);
            Assert.IsFalse(collapseTimer.IsEnabled);
            InvokePrivate(window, "ExpandIntentTimer_Tick", null, EventArgs.Empty);
            Assert.AreEqual(WindowSettings.TabWidth, window.Width);
            Assert.IsNull(((MainWindowViewModel)window.DataContext).ActivePanel);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CollapsedCategoryHover_LeavingTabForRailMarginBeforeIntentTickKeepsWindowCollapsed()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            window.Show();
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var category = viewModel.Categories.Single(
                candidate => candidate.Category == BoardCategory.Reference);
            var tab = FindCategoryTab(window, category);

            tab.RaiseEvent(NewMouseEventArgs(Mouse.MouseEnterEvent, tab));
            tab.RaiseEvent(NewMouseEventArgs(Mouse.MouseLeaveEvent, tab));
            InvokePrivate(window, "ExpandIntentTimer_Tick", null, EventArgs.Empty);

            Assert.AreEqual(WindowSettings.TabWidth, window.Width);
            Assert.IsNull(viewModel.ActivePanel);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ExpandedCategoryHover_SwitchesImmediatelyWithoutIntentTick()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            ExpandCategory(window, BoardCategory.Reference);

            EnterCategory(window, BoardCategory.Inbox);

            var timer = GetPrivateField<DispatcherTimer>(window, "_expandIntentTimer");
            var viewModel = (MainWindowViewModel)window.DataContext;
            Assert.IsFalse(timer.IsEnabled);
            Assert.AreEqual(BoardCategory.Inbox, viewModel.ActivePanel?.Category);
            Assert.AreEqual(
                WindowSettings.Default.PanelWidth + WindowSettings.TabWidth,
                window.Width);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ExpandedRailReveal_KeepsDefaultTabFixedAndAnimatesOnlyOtherTabs()
    {
        using var directory = new TestDirectory();
        var state = new DefaultCaptureCategoryState();
        var window = CreateWindow(directory, new BoardService(), state);
        window.Resources[SystemParameters.ClientAreaAnimationKey] = true;

        try
        {
            window.Show();
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var collapsedHandle = (ContentControl)window.FindName("CollapsedCategoryHandle");
            var collapsedDefaultTab = FindDescendants<Border>(collapsedHandle).Single(candidate =>
                ReferenceEquals(candidate.DataContext, viewModel.DefaultCapturePanel) &&
                Equals(candidate.Tag, viewModel.DefaultCapturePanel.Category));
            var collapsedDefaultBounds = ScreenBounds(collapsedDefaultTab);

            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            var expandedDefaultTab = FindCategoryTab(window, viewModel.DefaultCapturePanel);
            var expandedDefaultBounds = ScreenBounds(expandedDefaultTab);
            Assert.AreEqual(collapsedDefaultBounds.X, expandedDefaultBounds.X, 0.5);
            Assert.AreEqual(collapsedDefaultBounds.Y, expandedDefaultBounds.Y, 0.5);
            Assert.AreEqual(collapsedDefaultBounds.Width, expandedDefaultBounds.Width, 0.5);
            Assert.AreEqual(collapsedDefaultBounds.Height, expandedDefaultBounds.Height, 0.5);
            Assert.IsFalse(expandedDefaultTab.HasAnimatedProperties);

            foreach (var category in viewModel.Categories.Where(candidate => !candidate.IsDefaultCapture))
            {
                var tab = FindCategoryTab(window, category);
                var transform = tab.RenderTransform as TranslateTransform;
                Assert.IsNotNull(transform);
                Assert.IsTrue(tab.HasAnimatedProperties);
                Assert.IsTrue(transform.HasAnimatedProperties);
            }

            Assert.AreEqual(
                TimeSpan.FromMilliseconds(120),
                GetPrivateStaticField<TimeSpan>("CategoryRevealAnimationDuration"));
            Assert.AreEqual(6d, GetPrivateStaticField<double>("CategoryRevealOffset"));
            Assert.AreEqual(
                HandoffBehavior.SnapshotAndReplace,
                GetPrivateStaticField<HandoffBehavior>("CategoryRevealAnimationHandoffBehavior"));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ExpandedRailReveal_IsImmediateWhenSystemAnimationsAreDisabled()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            var viewModel = (MainWindowViewModel)window.DataContext;
            foreach (var category in viewModel.Categories)
            {
                var tab = FindCategoryTab(window, category);
                Assert.IsFalse(tab.HasAnimatedProperties);
                Assert.AreEqual(1d, tab.Opacity);
                if (tab.RenderTransform is TranslateTransform transform)
                {
                    Assert.IsFalse(transform.HasAnimatedProperties);
                    Assert.AreEqual(0d, transform.X);
                }
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void BeginPanelDrag_StopsRunningCategoryRevealAtTerminalValues()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = true;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var reference = viewModel.Categories.Single(
                candidate => candidate.Category == BoardCategory.Reference);
            var tab = FindCategoryTab(window, reference);
            var transform = tab.RenderTransform as TranslateTransform;
            Assert.IsNotNull(transform);
            Assert.IsTrue(tab.HasAnimatedProperties);
            Assert.IsTrue(transform.HasAnimatedProperties);

            InvokePrivate(window, "BeginPanelDrag");

            Assert.IsFalse(tab.HasAnimatedProperties);
            Assert.IsFalse(transform.HasAnimatedProperties);
            Assert.AreEqual(1d, tab.Opacity);
            Assert.AreEqual(0d, transform.X);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void RapidCollapsedCategoryHover_CommitsOnlyTheLatestCandidate()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            EnterCategory(window, BoardCategory.Reference);
            EnterCategory(window, BoardCategory.Inbox);

            InvokePrivate(window, "ExpandIntentTimer_Tick", null, EventArgs.Empty);

            var timer = GetPrivateField<DispatcherTimer>(window, "_expandIntentTimer");
            var viewModel = (MainWindowViewModel)window.DataContext;
            Assert.IsFalse(timer.IsEnabled);
            Assert.AreEqual(BoardCategory.Inbox, viewModel.ActivePanel?.Category);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void PanelContentAnimation_IsScopedToNamedChildWithLockedDurations()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            var host = window.FindName("PanelContentHost") as Grid;
            var transform = window.FindName("PanelContentTransform") as TranslateTransform;

            Assert.IsNotNull(host);
            Assert.IsNotNull(transform);
            Assert.AreSame(transform, host.RenderTransform);
            Assert.AreEqual(new Point(0.5, 0.5), host.RenderTransformOrigin);
            Assert.AreEqual(
                TimeSpan.FromMilliseconds(167),
                GetPrivateStaticField<TimeSpan>("ExpandContentAnimationDuration"));
            Assert.AreEqual(
                TimeSpan.FromMilliseconds(140),
                GetPrivateStaticField<TimeSpan>("SwitchContentAnimationDuration"));
            Assert.AreEqual(
                TimeSpan.FromMilliseconds(83),
                GetPrivateStaticField<TimeSpan>("ReducedMotionContentAnimationDuration"));

            ExpandCategory(window, BoardCategory.Reference);

            Assert.IsTrue(host.HasAnimatedProperties);
            Assert.IsFalse(window.HasAnimatedProperties);
            Assert.AreEqual(
                window.Width,
                window.GetAnimationBaseValue(FrameworkElement.WidthProperty));

            EnterCategory(window, BoardCategory.Inbox);

            Assert.IsTrue(host.HasAnimatedProperties);
            Assert.AreEqual(0d, transform.X, 0.01);
            Assert.IsFalse(window.HasAnimatedProperties);
            Assert.AreEqual(
                window.Width,
                window.GetAnimationBaseValue(FrameworkElement.WidthProperty));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void BeginPanelDrag_StopsRunningPanelContentAnimationAtTerminalValues()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            window.Show();
            var (host, transform) = FindPanelContent(window);
            StartPanelContentAnimation(host, transform);

            InvokePrivate(window, "BeginPanelDrag");

            AssertPanelContentAnimationStopped(host, transform);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void RootMouseEnter_StopsRunningPanelContentAnimationAtTerminalValues()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            window.Show();
            var (host, transform) = FindPanelContent(window);
            StartPanelContentAnimation(host, transform);

            InvokePrivate(window, "Root_MouseEnter", window, NewMouseEventArgs());

            AssertPanelContentAnimationStopped(host, transform);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void PanelContentAnimation_UsesWindowScopedClientAreaAnimationPreference()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = true;

        try
        {
            window.Show();
            CompleteLayout(window);
            var (host, transform) = FindPanelContent(window);

            InvokePrivate(window, "AnimatePanelContent", false);

            Assert.IsTrue(host.HasAnimatedProperties);
            Assert.IsTrue(transform.HasAnimatedProperties);

            InvokePrivate(window, "StopPanelContentAnimation");
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            CompleteLayout(window);
            InvokePrivate(window, "AnimatePanelContent", false);

            Assert.IsTrue(host.HasAnimatedProperties);
            Assert.IsFalse(transform.HasAnimatedProperties);
            Assert.AreEqual(0d, transform.X);
            Assert.IsTrue(
                GetPrivateStaticField<TimeSpan>("ReducedMotionContentAnimationDuration") <=
                TimeSpan.FromMilliseconds(83));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void DisablingClientAreaAnimations_StopsRunningPanelContentAnimationAtTerminalValues()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = true;

        try
        {
            window.Show();
            CompleteLayout(window);
            var (host, transform) = FindPanelContent(window);
            InvokePrivate(window, "AnimatePanelContent", false);
            Assert.IsTrue(window.ClientAreaAnimationsEnabled);
            Assert.IsTrue(host.HasAnimatedProperties);
            Assert.IsTrue(transform.HasAnimatedProperties);

            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            CompleteLayout(window);

            Assert.IsFalse(window.ClientAreaAnimationsEnabled);
            AssertPanelContentAnimationStopped(host, transform);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Collapse_KeepsPanelAndItemsSourceAndRestoresTheCategoryOffset()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        AddScrollableItems(board, BoardCategory.Reference);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);

            var viewModel = (MainWindowViewModel)window.DataContext;
            var panel = viewModel.ActivePanel;
            var list = (ListBox)window.FindName("BoardList");
            var itemsSource = list.ItemsSource;
            var viewer = FindDescendant<ScrollViewer>(list);

            Assert.IsNotNull(panel);
            Assert.IsNotNull(viewer);
            Assert.AreSame(panel.Items, itemsSource);
            Assert.IsTrue(viewer.ScrollableHeight >= 120, "Test data must make the real list scrollable.");
            ScrollTo(window, viewer, 120);

            var collapseTimer = GetPrivateField<DispatcherTimer>(window, "_collapseTimer");
            Assert.AreEqual(TimeSpan.FromMilliseconds(250), collapseTimer.Interval);

            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            Assert.IsTrue(collapseTimer.IsEnabled);
            InvokePrivate(window, "Root_MouseEnter", window, NewMouseEventArgs());
            Assert.IsFalse(collapseTimer.IsEnabled);
            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            Assert.IsTrue(collapseTimer.IsEnabled);
            InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
            CompleteLayout(window);

            Assert.AreEqual(WindowSettings.TabWidth, window.Width);
            Assert.AreSame(panel, viewModel.ActivePanel);
            Assert.AreSame(itemsSource, list.ItemsSource);

            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);

            Assert.AreEqual(120d, viewer.VerticalOffset, 0.5);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Collapse_HidesExpandedSurfaceBeforeMovingToTheCollapsedRow()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var shell = (Border)window.FindName("WindowShell");
            var expandedWidth = window.Width;
            var expandedHeight = window.Height;
            var expandedTop = window.Top;

            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);

            Assert.AreEqual(expandedWidth, window.Width, 0.5);
            Assert.AreEqual(expandedHeight, window.Height, 0.5);
            Assert.AreEqual(expandedTop, window.Top, 0.5);
            Assert.AreEqual(0d, shell.Opacity);
            Assert.IsFalse(shell.IsHitTestVisible);
            Assert.IsTrue(viewModel.IsPanelExpanded);

            CompleteLayout(window);

            Assert.AreEqual(WindowSettings.TabWidth, window.Width, 0.5);
            Assert.AreEqual(1d, shell.Opacity);
            Assert.IsTrue(shell.IsHitTestVisible);
            Assert.IsFalse(viewModel.IsPanelExpanded);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CategorySwitch_RestoresIndependentOffsetsOnTheSingleList()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        AddScrollableItems(board, BoardCategory.Reference);
        AddScrollableItems(board, BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);

            var list = (ListBox)window.FindName("BoardList");
            var viewer = FindDescendant<ScrollViewer>(list);
            Assert.IsNotNull(viewer);
            Assert.IsTrue(viewer.ScrollableHeight >= 220, "Reference items must be scrollable.");
            ScrollTo(window, viewer, 110);

            EnterCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            Assert.IsTrue(viewer.ScrollableHeight >= 220, "Inbox items must be scrollable.");
            ScrollTo(window, viewer, 210);

            EnterCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            Assert.AreEqual(110d, viewer.VerticalOffset, 0.5);

            EnterCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            Assert.AreEqual(210d, viewer.VerticalOffset, 0.5);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void PinnedBoundary_AddsGapOnlyBeforeFirstNormalCard()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var normalBottom = board.AddText("normal bottom");
        var normalTop = board.AddText("normal top");
        var pinned = board.AddText("pinned");
        board.SetPinnedMany([pinned.Id], true);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var cardStyle = window.FindResource("CardContainerStyle");
            Border Card(BoardItem item) => FindDescendants<Border>(
                    (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(item))
                .Single(candidate => ReferenceEquals(candidate.Style, cardStyle));

            Assert.AreEqual(new Thickness(12, 4, 12, 4), Card(pinned).Margin);
            Assert.AreEqual(new Thickness(12, 10, 12, 4), Card(normalTop).Margin);
            Assert.AreEqual(new Thickness(12, 4, 12, 4), Card(normalBottom).Margin);
        }
        finally
        {
            CloseWindow(window);
        }
    }
}
