using System.Reflection;
using System.Runtime.InteropServices;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Automation;
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

[TestClass]
[TestCategory("Adversarial")]
public sealed class MainWindowInteractionTests
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
    public void CategoryTab_NameEditingKeepsDefaultMarkerAboveTheEditor()
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

            customer.BeginNameEdit();
            CompleteLayout(window);

            Assert.IsTrue(customer.IsEditingName);
            Assert.IsTrue(FindDescendants<TextBox>(customerTab).Single().IsVisible);
            var markerStyle = (Style)window.FindResource("CategoryDefaultMarkerStyle");
            var marker = FindDescendants<TextBlock>(customerTab)
                .Single(candidate => ReferenceEquals(candidate.Style, markerStyle));
            Assert.AreEqual("◎", marker.Text);
            Assert.AreEqual(customer.IsDefaultCapture ? 1d : 0d, marker.Opacity);
            Assert.IsTrue(
                Panel.GetZIndex(marker) > Panel.GetZIndex(FindDescendants<TextBox>(customerTab).Single()));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CategoryNameSave_UpdatesOnlySettingsAndTheSharedDisplayName()
    {
        using var directory = new TestDirectory();
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(
            new BoardService(),
            store,
            WindowSettings.Default,
            new DefaultCaptureCategoryState());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.CustomerOriginal);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var customer = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);

            InvokePrivateTask(window, "SaveCategoryNameAsync", customer, "客户");

            Assert.AreEqual("客户", customer.DisplayName);
            Assert.AreEqual("客户", viewModel.ActivePanel!.DisplayName);
            Assert.AreEqual("客户", store.LastSavedSettings!.CategoryName(
                BoardCategory.CustomerOriginal));
            Assert.IsNull(store.LastPersistedSnapshot);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CategoryNameSaveFailure_RestoresNameAndKeepsDefaultMarker()
    {
        using var directory = new TestDirectory();
        var store = new RecordingBoardStore(directory.Root)
        {
            SettingsSaveFailure = new IOException("Injected settings failure.")
        };
        var state = new DefaultCaptureCategoryState();
        state.Set(BoardCategory.CustomerOriginal);
        var window = CreateWindow(new BoardService(), store, WindowSettings.Default, state);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.CustomerOriginal);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var customer = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);

            InvokePrivateTask(window, "SaveCategoryNameAsync", customer, "客户");

            Assert.AreEqual("客户原图", customer.DisplayName);
            Assert.AreEqual("分类名称未保存，已恢复原名称。", viewModel.StatusText);
            Assert.IsTrue(customer.IsDefaultCapture);
            Assert.AreEqual(BoardCategory.CustomerOriginal, state.Current);
            store.SettingsSaveFailure = null;
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void CategoryNameEditing_PointerLeaveDoesNotCollapseOrSaveDraft()
    {
        using var directory = new TestDirectory();
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(
            new BoardService(),
            store,
            WindowSettings.Default,
            new DefaultCaptureCategoryState());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.CustomerOriginal);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var customer = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);
            var customerTab = FindCategoryTab(window, customer);

            customer.BeginNameEdit();
            customer.DraftName = "T";
            CompleteLayout(window);
            var editor = FindDescendants<TextBox>(customerTab).Single();
            Assert.IsTrue(editor.Focus());

            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            var collapseTimer = GetPrivateField<DispatcherTimer>(window, "_collapseTimer");
            Assert.IsFalse(collapseTimer.IsEnabled);
            InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
            CompleteLayout(window);

            Assert.IsTrue(viewModel.IsPanelExpanded);
            Assert.IsTrue(customer.IsEditingName);
            Assert.AreEqual("客户原图", customer.DisplayName);
            Assert.AreEqual("T", customer.DraftName);
            Assert.IsNull(store.LastSavedSettings);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CategoryNameEditor_DefersSourceUpdatesUntilCommitForImeComposition()
    {
        using var directory = new TestDirectory();
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(
            new BoardService(),
            store,
            WindowSettings.Default,
            new DefaultCaptureCategoryState());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.CustomerOriginal);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var customer = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);
            var customerTab = FindCategoryTab(window, customer);

            customer.BeginNameEdit();
            CompleteLayout(window);
            var editor = FindDescendants<TextBox>(customerTab).Single();
            var binding = BindingOperations.GetBinding(editor, TextBox.TextProperty);
            Assert.IsNotNull(binding);
            Assert.AreEqual(UpdateSourceTrigger.Explicit, binding.UpdateSourceTrigger);

            editor.Text = "这是";
            editor.CaretIndex = editor.Text.Length;
            CompleteLayout(window);

            Assert.AreEqual("客户原图", customer.DraftName);
            Assert.AreEqual(editor.Text.Length, editor.CaretIndex);

            InvokePrivate(window, "CommitCategoryNameEdit", customer, editor.Text);
            InvokePrivateTask(window, "DrainPendingOperationsAsync");

            Assert.AreEqual("这是", customer.DisplayName);
            Assert.AreEqual(
                "这是",
                store.LastSavedSettings!.CategoryName(BoardCategory.CustomerOriginal));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CategoryNameEditor_DoesNotTruncateActiveImeCompositionText()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.CustomerOriginal);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var customer = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);
            var customerTab = FindCategoryTab(window, customer);

            customer.BeginNameEdit();
            CompleteLayout(window);
            var editor = FindDescendants<TextBox>(customerTab).Single();
            editor.RaiseEvent(NewTextCompositionEventArgs(
                TextCompositionManager.PreviewTextInputStartEvent,
                editor,
                "zheshide"));

            editor.Text = "zheshide";
            Assert.AreEqual("zheshide", editor.Text);

            editor.Text = "这是的";
            editor.CaretIndex = editor.Text.Length;
            editor.RaiseEvent(NewTextCompositionEventArgs(
                TextCompositionManager.PreviewTextInputEvent,
                editor,
                "这是的"));
            CompleteLayout(window);

            Assert.AreEqual("这是的", editor.Text);
            Assert.AreEqual(editor.Text.Length, editor.CaretIndex);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CategoryNameEditor_EnterDuringImeCompositionDoesNotSaveTheLabel()
    {
        using var directory = new TestDirectory();
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(
            new BoardService(),
            store,
            WindowSettings.Default,
            new DefaultCaptureCategoryState());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.CustomerOriginal);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var customer = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);
            var customerTab = FindCategoryTab(window, customer);

            customer.BeginNameEdit();
            CompleteLayout(window);
            var editor = FindDescendants<TextBox>(customerTab).Single();
            editor.RaiseEvent(NewTextCompositionEventArgs(
                TextCompositionManager.PreviewTextInputStartEvent,
                editor,
                "zhe"));
            editor.Text = "zhe";
            var source = PresentationSource.FromVisual(editor);
            Assert.IsNotNull(source);
            var enter = new KeyEventArgs(
                Keyboard.PrimaryDevice,
                source,
                0,
                Key.Enter)
            {
                RoutedEvent = Keyboard.KeyDownEvent,
                Source = editor
            };

            InvokePrivate(window, "CategoryNameEditor_KeyDown", editor, enter);

            Assert.IsFalse(enter.Handled);
            Assert.IsTrue(customer.IsEditingName);
            Assert.AreEqual("客户原图", customer.DisplayName);
            Assert.IsNull(store.LastSavedSettings);
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
    public void HeaderDeleteWithoutSelection_RemovesOnlyTheActiveCategoryWithoutConfirmation()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("提示词一", BoardCategory.Prompt);
        board.AddText("提示词二", BoardCategory.Prompt);
        var inbox = board.AddText("待分类保留");
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Prompt);
            CompleteLayout(window);
            var delete = window.FindName("DeleteContentButton") as Button;
            Assert.IsNotNull(delete);
            Assert.IsTrue(delete.IsEnabled);

            delete.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            CompleteLayout(window);

            Assert.AreEqual(0, board.Items(BoardCategory.Prompt).Count);
            Assert.AreSame(inbox, board.Items(BoardCategory.Inbox).Single());
            Assert.AreEqual(1, store.SaveCount);
            Assert.IsFalse(store.LastPersistedSnapshot!.Items.Any(
                item => item.Category == BoardCategory.Prompt));
            Assert.IsFalse(delete.IsEnabled);
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
    public void ExternalDropRailProjection_IsIndependentFromPanelAndCategoryState()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            window.Show();
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var rail = (Border)window.FindName("CategoryRail");
            var collapsedHandle = (ContentControl)window.FindName("CollapsedCategoryHandle");
            var panelContent = (Grid)window.FindName("PanelContentHost");
            viewModel.Activate(BoardCategory.Reference);
            var activePanel = viewModel.ActivePanel;
            var defaultCapturePanel = viewModel.DefaultCapturePanel;
            var activeStates = viewModel.Categories.Select(category => category.IsActive).ToArray();

            Assert.IsFalse(viewModel.IsExternalDropRailVisible);
            Assert.IsFalse(viewModel.IsCategoryRailVisible);
            Assert.IsTrue(viewModel.IsCollapsedCategoryHandleVisible);
            Assert.AreNotEqual(Visibility.Visible, panelContent.Visibility);
            Assert.AreNotEqual(Visibility.Visible, rail.Visibility);
            Assert.AreEqual(Visibility.Visible, collapsedHandle.Visibility);

            viewModel.SetExternalDropRailVisible(true);
            CompleteLayout(window);

            Assert.IsTrue(viewModel.IsExternalDropRailVisible);
            Assert.IsTrue(viewModel.IsCategoryRailVisible);
            Assert.IsFalse(viewModel.IsCollapsedCategoryHandleVisible);
            Assert.AreNotEqual(Visibility.Visible, panelContent.Visibility);
            Assert.AreEqual(Visibility.Visible, rail.Visibility);
            Assert.AreNotEqual(Visibility.Visible, collapsedHandle.Visibility);
            Assert.AreSame(activePanel, viewModel.ActivePanel);
            Assert.AreSame(defaultCapturePanel, viewModel.DefaultCapturePanel);
            Assert.IsFalse(viewModel.IsPanelExpanded);
            CollectionAssert.AreEqual(
                activeStates,
                viewModel.Categories.Select(category => category.IsActive).ToArray());

            viewModel.SetExternalDropRailVisible(false);
            viewModel.SetPanelExpanded(true);
            CompleteLayout(window);

            Assert.IsFalse(viewModel.IsExternalDropRailVisible);
            Assert.IsTrue(viewModel.IsCategoryRailVisible);
            Assert.IsFalse(viewModel.IsCollapsedCategoryHandleVisible);
            Assert.AreEqual(Visibility.Visible, panelContent.Visibility);
            Assert.AreEqual(Visibility.Visible, rail.Visibility);
            Assert.AreNotEqual(Visibility.Visible, collapsedHandle.Visibility);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void SupportedExternalText_EnteringCollapsedDefaultHandleRevealsStableRailOnlyTarget()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = true;

        try
        {
            window.Show();
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var collapsedHandle = (ContentControl)window.FindName("CollapsedCategoryHandle");
            var collapsedTab = FindCollapsedCategoryTab(window);
            var collapsedBounds = ScreenBounds(collapsedTab);
            var data = new DataObject(DataFormats.UnicodeText, "外部提示词");
            var dragEnter = NewDragEventArgs(
                data,
                DragDrop.DragEnterEvent,
                collapsedTab);

            collapsedTab.RaiseEvent(dragEnter);
            CompleteLayout(window);

            var rail = (Border)window.FindName("CategoryRail");
            var panel = (Grid)window.FindName("PanelContentHost");
            var insertion = (Border)window.FindName("InsertionIndicator");
            var defaultRailTab = FindCategoryTab(window, viewModel.DefaultCapturePanel);
            var railBounds = ScreenBounds(defaultRailTab);
            Assert.AreEqual(DragDropEffects.Copy, dragEnter.Effects);
            Assert.IsTrue(dragEnter.Handled);
            Assert.IsTrue(viewModel.IsExternalDropRailVisible);
            Assert.IsFalse(viewModel.IsPanelExpanded);
            Assert.AreEqual(Visibility.Visible, rail.Visibility);
            Assert.AreNotEqual(Visibility.Visible, collapsedHandle.Visibility);
            Assert.AreNotEqual(Visibility.Visible, panel.Visibility);
            Assert.AreEqual(WindowSettings.TabWidth, window.Width, 0.5);
            Assert.AreEqual(WindowSettings.Default.WindowHeight, window.Height, 0.5);
            Assert.AreEqual(
                SystemParameters.WorkArea.Top + WindowSettings.Default.Top,
                window.Top,
                0.5);
            Assert.AreEqual(collapsedBounds.X, railBounds.X, 0.5);
            Assert.AreEqual(collapsedBounds.Y, railBounds.Y, 0.5);
            Assert.AreEqual(collapsedBounds.Width, railBounds.Width, 0.5);
            Assert.AreEqual(collapsedBounds.Height, railBounds.Height, 0.5);
            Assert.AreSame(
                viewModel.DefaultCapturePanel,
                viewModel.Categories.Single(category => category.IsDropTarget));
            Assert.AreEqual(Visibility.Collapsed, insertion.Visibility);
            Assert.IsFalse(window.HasAnimatedProperties);
            foreach (var category in viewModel.Categories)
            {
                var tab = FindCategoryTab(window, category);
                Assert.IsFalse(tab.HasAnimatedProperties);
                if (tab.RenderTransform is TranslateTransform transform)
                {
                    Assert.IsFalse(transform.HasAnimatedProperties);
                }
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ExternalRail_CrossTabLeaveIsReconciledAndTrueLeaveRestoresCollapsedHandle()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            window.Show();
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var collapsedHandle = (ContentControl)window.FindName("CollapsedCategoryHandle");
            var data = new DataObject(DataFormats.UnicodeText, "跨标签拖动");
            var current = FindCollapsedCategoryTab(window);
            current.RaiseEvent(NewDragEventArgs(data, DragDrop.DragEnterEvent, current));
            CompleteLayout(window);

            foreach (var category in viewModel.Categories.Where(candidate => !candidate.IsDefaultCapture))
            {
                var next = FindCategoryTab(window, category);
                current.RaiseEvent(NewDragEventArgs(data, DragDrop.DragLeaveEvent, current));

                Assert.IsTrue(viewModel.IsExternalDropRailVisible);
                Assert.AreEqual(WindowSettings.Default.WindowHeight, window.Height, 0.5);
                Assert.IsFalse(viewModel.Categories.Any(candidate => candidate.IsDropTarget));

                var enter = NewDragEventArgs(data, DragDrop.DragEnterEvent, next);
                next.RaiseEvent(enter);
                CompleteLayout(window);

                Assert.AreEqual(DragDropEffects.Copy, enter.Effects);
                Assert.IsTrue(viewModel.IsExternalDropRailVisible);
                Assert.AreSame(
                    category,
                    viewModel.Categories.Single(candidate => candidate.IsDropTarget));
                current = next;
            }

            current.RaiseEvent(NewDragEventArgs(data, DragDrop.DragLeaveEvent, current));
            CompleteLayout(window);

            var rowHeight = WindowSettings.Default.WindowHeight /
                BoardCategoryCatalog.Ordered.Count;
            Assert.IsFalse(viewModel.IsExternalDropRailVisible);
            Assert.IsFalse(viewModel.Categories.Any(category => category.IsDropTarget));
            Assert.AreEqual(Visibility.Visible, collapsedHandle.Visibility);
            Assert.AreEqual(WindowSettings.TabWidth, window.Width, 0.5);
            Assert.AreEqual(rowHeight, window.Height, 0.5);
            Assert.AreEqual(
                SystemParameters.WorkArea.Top + WindowSettings.Default.Top + (3 * rowHeight),
                window.Top,
                0.5);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ExternalRail_GapEnterKeepsRailOpenThroughInputReconciliationUntilGapLeave()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            window.Show();
            CompleteLayout(window);
            var data = new DataObject(DataFormats.UnicodeText, "轨内空隙拖动");
            var collapsedTab = FindCollapsedCategoryTab(window);
            collapsedTab.RaiseEvent(NewDragEventArgs(data, DragDrop.DragEnterEvent, collapsedTab));
            CompleteLayout(window);

            var viewModel = (MainWindowViewModel)window.DataContext;
            var currentTab = FindCategoryTab(window, viewModel.DefaultCapturePanel);
            var rail = (Border)window.FindName("CategoryRail");
            var gapSurface = rail.Child as ItemsControl;
            Assert.IsNotNull(gapSurface);
            Assert.AreEqual(new Thickness(5), currentTab.Margin);

            currentTab.RaiseEvent(NewDragEventArgs(data, DragDrop.DragLeaveEvent, currentTab));
            var gapEnter = NewDragEventArgs(data, DragDrop.DragEnterEvent, gapSurface);
            gapSurface.RaiseEvent(gapEnter);
            CompleteLayout(window);

            Assert.IsTrue(
                viewModel.IsExternalDropRailVisible,
                "A supported drag over the rail gap must invalidate the tab-leave reconciliation.");
            Assert.IsTrue(gapEnter.Handled);
            Assert.AreEqual(DragDropEffects.None, gapEnter.Effects);
            Assert.IsFalse(viewModel.Categories.Any(category => category.IsDropTarget));
            Assert.IsFalse(viewModel.IsPanelExpanded);
            Assert.AreEqual(WindowSettings.Default.WindowHeight, window.Height, 0.5);

            var gapLeave = NewDragEventArgs(data, DragDrop.DragLeaveEvent, gapSurface);
            gapSurface.RaiseEvent(gapLeave);
            CompleteLayout(window);

            Assert.IsTrue(gapLeave.Handled);
            Assert.IsFalse(viewModel.IsExternalDropRailVisible);
            Assert.AreEqual(
                WindowSettings.Default.WindowHeight / BoardCategoryCatalog.Ordered.Count,
                window.Height,
                0.5);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void InternalImageBatch_CrossingRailToCategoryKeepsExpandedPanelOpen()
    {
        using var directory = new TestDirectory();
        var firstPath = Path.Combine(directory.Root, "first.png");
        var secondPath = Path.Combine(directory.Root, "second.png");
        WritePng(firstPath, width: 2, height: 2);
        WritePng(secondPath, width: 2, height: 2);
        var board = new BoardService();
        var first = board.AddImage(Guid.NewGuid(), "images/first.png", firstPath);
        var second = board.AddImage(Guid.NewGuid(), "images/second.png", secondPath);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var expandedWidth = window.Width;
            var viewModel = (MainWindowViewModel)window.DataContext;
            var rail = (Border)window.FindName("CategoryRail");
            var gapSurface = rail.Child as ItemsControl;
            Assert.IsNotNull(gapSurface);
            var data = new DragPayloadService().BuildInternalBatch([first, second]);

            var gapEnter = NewDragEventArgs(data, DragDrop.DragEnterEvent, gapSurface);
            gapSurface.RaiseEvent(gapEnter);
            CompleteLayout(window);

            Assert.IsTrue(gapEnter.Handled);
            Assert.AreEqual(DragDropEffects.None, gapEnter.Effects);
            Assert.IsFalse(viewModel.IsExternalDropRailVisible);
            Assert.IsTrue(viewModel.IsPanelExpanded);
            Assert.AreEqual(expandedWidth, window.Width, 0.5);

            var target = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);
            var targetTab = FindCategoryTab(window, target);
            var targetEnter = NewDragEventArgs(data, DragDrop.DragEnterEvent, targetTab);
            targetTab.RaiseEvent(targetEnter);
            CompleteLayout(window);

            Assert.AreEqual(DragDropEffects.Move, targetEnter.Effects);
            Assert.IsTrue(viewModel.IsPanelExpanded);
            Assert.AreSame(target, viewModel.Categories.Single(category => category.IsDropTarget));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ExternalRail_GapDropIsRejectedAndRestoresCollapsedHandle()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            CompleteLayout(window);
            var data = new DataObject(DataFormats.UnicodeText, "空隙不能接收");
            var collapsedTab = FindCollapsedCategoryTab(window);
            collapsedTab.RaiseEvent(NewDragEventArgs(data, DragDrop.DragEnterEvent, collapsedTab));
            CompleteLayout(window);

            var viewModel = (MainWindowViewModel)window.DataContext;
            var currentTab = FindCategoryTab(window, viewModel.DefaultCapturePanel);
            var rail = (Border)window.FindName("CategoryRail");
            var gapSurface = rail.Child as ItemsControl;
            Assert.IsNotNull(gapSurface);
            currentTab.RaiseEvent(NewDragEventArgs(data, DragDrop.DragLeaveEvent, currentTab));
            gapSurface.RaiseEvent(NewDragEventArgs(data, DragDrop.DragEnterEvent, gapSurface));
            CompleteLayout(window);
            Assert.IsTrue(viewModel.IsExternalDropRailVisible);

            var drop = NewDragEventArgs(data, DragDrop.DropEvent, gapSurface);
            gapSurface.RaiseEvent(drop);
            CompleteLayout(window);

            Assert.IsFalse(viewModel.IsExternalDropRailVisible);
            Assert.IsTrue(drop.Handled);
            Assert.AreEqual(DragDropEffects.None, drop.Effects);
            Assert.IsFalse(viewModel.Categories.Any(category => category.IsDropTarget));
            Assert.AreEqual(0, board.Items(BoardCategory.Inbox).Count);
            Assert.AreEqual(0, store.SaveCount);
            Assert.AreEqual(WindowSettings.TabWidth, window.Width, 0.5);
            Assert.AreEqual(
                WindowSettings.Default.WindowHeight / BoardCategoryCatalog.Ordered.Count,
                window.Height,
                0.5);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void InternalCategoryDrop_MarksEventHandledBeforeAsyncMoveCompletes()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var moved = board.AddText("async move", BoardCategory.Inbox);
        var store = new BlockingFirstSuccessfulSaveBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var prompt = viewModel.Categories.Single(
                category => category.Category == BoardCategory.Prompt);
            var promptTab = FindCategoryTab(window, prompt);
            var data = new DataObject();
            data.SetData(DragPayloadService.InternalItemIdFormat, moved.Id.ToString("D"));
            var drop = NewDragEventArgs(data, DragDrop.DropEvent, promptTab);

            promptTab.RaiseEvent(drop);
            PumpDispatcherUntil(window.Dispatcher, store.FirstSaveStarted.Task);

            Assert.IsTrue(
                drop.Handled,
                "The category target must stop bubbling before its asynchronous move awaits.");
            Assert.AreEqual(DragDropEffects.Move, drop.Effects);
            Assert.IsFalse(viewModel.IsExternalDropRailVisible);
        }
        finally
        {
            store.ReleaseFirstSave();
            PumpDispatcherUntil(window.Dispatcher, store.FirstSaveCompleted.Task);
            CompleteLayout(window);
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ExternalRail_TabMouseEnterCannotStartOrdinaryHoverExpansion()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            window.Show();
            CompleteLayout(window);
            var data = new DataObject(DataFormats.UnicodeText, "不展开内容页");
            var collapsedTab = FindCollapsedCategoryTab(window);
            collapsedTab.RaiseEvent(NewDragEventArgs(data, DragDrop.DragEnterEvent, collapsedTab));
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var reference = viewModel.Categories.Single(
                category => category.Category == BoardCategory.Reference);
            var referenceTab = FindCategoryTab(window, reference);

            referenceTab.RaiseEvent(NewMouseEventArgs(Mouse.MouseEnterEvent, referenceTab));
            InvokePrivate(window, "ExpandIntentTimer_Tick", null, EventArgs.Empty);

            Assert.IsTrue(viewModel.IsExternalDropRailVisible);
            Assert.IsFalse(viewModel.IsPanelExpanded);
            Assert.AreEqual(WindowSettings.TabWidth, window.Width, 0.5);
            Assert.AreEqual(WindowSettings.Default.WindowHeight, window.Height, 0.5);
            Assert.IsNull(viewModel.ActivePanel);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void UnsupportedOrInternalData_NeverEntersExternalRailState()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("drag source");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var collapsedTab = FindCollapsedCategoryTab(window);
            var unsupportedData = new DataObject("unsupported/custom", new object());
            var unsupported = NewDragEventArgs(
                unsupportedData,
                DragDrop.DragEnterEvent,
                collapsedTab);

            collapsedTab.RaiseEvent(unsupported);

            Assert.AreEqual(DragDropEffects.None, unsupported.Effects);
            Assert.IsFalse(viewModel.IsExternalDropRailVisible);
            Assert.AreEqual(
                WindowSettings.Default.WindowHeight / BoardCategoryCatalog.Ordered.Count,
                window.Height,
                0.5);

            var internalDrag = NewDragEventArgs(
                new DragPayloadService().Build(item),
                DragDrop.DragEnterEvent,
                collapsedTab);
            collapsedTab.RaiseEvent(internalDrag);

            Assert.AreEqual(DragDropEffects.Move, internalDrag.Effects);
            Assert.IsFalse(viewModel.IsExternalDropRailVisible);
            Assert.AreSame(
                viewModel.DefaultCapturePanel,
                viewModel.Categories.Single(category => category.IsDropTarget));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void UnsupportedExternalDrag_LeaveClearsSnapshotBeforeDataObjectIsReused()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            window.Show();
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var collapsedTab = FindCollapsedCategoryTab(window);
            var data = new DataObject("unsupported/custom", new object());
            var unsupported = NewDragEventArgs(data, DragDrop.DragEnterEvent, collapsedTab);
            collapsedTab.RaiseEvent(unsupported);

            Assert.AreEqual(DragDropEffects.None, unsupported.Effects);
            collapsedTab.RaiseEvent(NewDragEventArgs(
                data,
                DragDrop.DragLeaveEvent,
                collapsedTab));
            CompleteLayout(window);
            data.SetData(DataFormats.UnicodeText, "下一次拖动");
            var nextEnter = NewDragEventArgs(data, DragDrop.DragEnterEvent, collapsedTab);

            collapsedTab.RaiseEvent(nextEnter);

            Assert.AreEqual(DragDropEffects.Copy, nextEnter.Effects);
            Assert.IsTrue(viewModel.IsExternalDropRailVisible);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void AnimatedImageFile_DragEnterDoesNotRevealExternalRail()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "animated.gif");
        WriteAnimatedGif(path);
        var window = CreateWindow(directory, new BoardService());

        try
        {
            window.Show();
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var collapsedTab = FindCollapsedCategoryTab(window);
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { path });
            data.SetData(DataFormats.UnicodeText, "must not fall back");
            var dragEnter = NewDragEventArgs(
                data,
                DragDrop.DragEnterEvent,
                collapsedTab);

            collapsedTab.RaiseEvent(dragEnter);

            Assert.AreEqual(DragDropEffects.None, dragEnter.Effects);
            Assert.IsTrue(dragEnter.Handled);
            Assert.IsFalse(viewModel.IsExternalDropRailVisible);
            Assert.AreEqual(
                WindowSettings.Default.WindowHeight / BoardCategoryCatalog.Ordered.Count,
                window.Height,
                0.5);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ExternalTextDrop_ImportsDirectlyToTargetAndRestoresCollapsedDefaultHandle()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var store = new RecordingBoardStore(directory.Root);
        var state = new DefaultCaptureCategoryState();
        state.Set(BoardCategory.Reference);
        var window = CreateWindow(board, store, WindowSettings.Default, state);

        try
        {
            window.Show();
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var data = new DataObject(DataFormats.UnicodeText, "只进入提示词");
            var defaultTab = FindCollapsedCategoryTab(window);
            defaultTab.RaiseEvent(NewDragEventArgs(data, DragDrop.DragEnterEvent, defaultTab));
            CompleteLayout(window);
            var prompt = viewModel.Categories.Single(
                category => category.Category == BoardCategory.Prompt);
            var promptTab = FindCategoryTab(window, prompt);
            promptTab.RaiseEvent(NewDragEventArgs(data, DragDrop.DragOverEvent, promptTab));
            var drop = NewDragEventArgs(data, DragDrop.DropEvent, promptTab);

            promptTab.RaiseEvent(drop);
            PumpDispatcherUntil(window.Dispatcher, store.SaveCompleted.Task);
            CompleteLayout(window);

            Assert.AreEqual(DragDropEffects.Copy, drop.Effects);
            Assert.IsTrue(drop.Handled);
            Assert.AreEqual(BoardCategory.Reference, state.Current);
            Assert.AreEqual("只进入提示词", board.Items(BoardCategory.Prompt).Single().Text);
            Assert.AreEqual(0, board.Items(BoardCategory.Inbox).Count);
            Assert.IsFalse(viewModel.IsExternalDropRailVisible);
            Assert.IsFalse(viewModel.IsPanelExpanded);
            Assert.AreEqual(
                WindowSettings.Default.WindowHeight / BoardCategoryCatalog.Ordered.Count,
                window.Height,
                0.5);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ExternalOrderedFileDrop_CopiesWholeBatchToExplicitCategoryTop()
    {
        using var directory = new TestDirectory();
        var sources = new[]
        {
            Path.Combine(directory.Root, "A.png"),
            Path.Combine(directory.Root, "B.png"),
            Path.Combine(directory.Root, "C.png")
        };
        foreach (var source in sources)
        {
            WritePng(source, 8, 8);
        }

        var board = new BoardService();
        var existing = board.AddText("existing", BoardCategory.CustomerOriginal);
        var store = new RecordingBoardStore(directory.Root);
        var state = new DefaultCaptureCategoryState();
        var normalizer = new ImageNormalizer(store.ImagesDirectory);
        var window = CreateWindow(
            board,
            store,
            WindowSettings.Default,
            state,
            normalizer);
        var files = new StringCollection();
        files.AddRange(sources);
        var data = new DataObject();
        data.SetFileDropList(files);

        try
        {
            window.Show();
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var defaultTab = FindCollapsedCategoryTab(window);
            defaultTab.RaiseEvent(NewDragEventArgs(data, DragDrop.DragEnterEvent, defaultTab));
            CompleteLayout(window);
            var customer = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);
            var customerTab = FindCategoryTab(window, customer);
            customerTab.RaiseEvent(NewDragEventArgs(data, DragDrop.DragOverEvent, customerTab));
            var drop = NewDragEventArgs(data, DragDrop.DropEvent, customerTab);

            customerTab.RaiseEvent(drop);
            PumpDispatcherUntil(window.Dispatcher, store.SaveCompleted.Task);

            var items = board.Items(BoardCategory.CustomerOriginal).ToArray();
            Assert.AreEqual(DragDropEffects.Copy, drop.Effects);
            Assert.AreEqual(4, items.Length);
            Assert.IsTrue(items.Take(3).All(item => item.Kind == BoardItemKind.Image));
            Assert.AreSame(existing, items[3]);
            Assert.IsTrue(items.Take(3).All(item => File.Exists(item.ImageAbsolutePath)));
            Assert.AreEqual(BoardCategory.Inbox, state.Current);
            Assert.IsFalse(viewModel.IsExternalDropRailVisible);
            Assert.AreEqual(1, store.SaveCount);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ExternalFileDrop_IsSnapshottedOnceAcrossEnterOverAndDrop()
    {
        using var directory = new TestDirectory();
        var source = Path.Combine(directory.Root, "single-read.png");
        WritePng(source, 8, 8);
        var board = new BoardService();
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);
        var data = new SingleReadFileDropDataObject([source]);

        try
        {
            window.Show();
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var defaultTab = FindCollapsedCategoryTab(window);
            var enter = NewDragEventArgs(data, DragDrop.DragEnterEvent, defaultTab);

            defaultTab.RaiseEvent(enter);
            CompleteLayout(window);

            Assert.AreEqual(DragDropEffects.Copy, enter.Effects);
            Assert.AreEqual(1, data.FileDropReadCount);
            var customer = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);
            var customerTab = FindCategoryTab(window, customer);
            var over = NewDragEventArgs(data, DragDrop.DragOverEvent, customerTab);

            customerTab.RaiseEvent(over);

            Assert.AreEqual(DragDropEffects.Copy, over.Effects);
            Assert.AreEqual(
                1,
                data.FileDropReadCount,
                "DragOver must reuse the payload snapshot created for this drag session.");
            var drop = NewDragEventArgs(data, DragDrop.DropEvent, customerTab);
            customerTab.RaiseEvent(drop);
            PumpDispatcherUntil(window.Dispatcher, store.SaveCompleted.Task);

            Assert.AreEqual(DragDropEffects.Copy, drop.Effects);
            Assert.AreEqual(1, data.FileDropReadCount);
            Assert.AreEqual(1, store.SaveCount);
            Assert.AreEqual(1, board.Items(BoardCategory.CustomerOriginal).Count);
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
    public void FailedExternalImport_ShowsNonInteractiveCompactStatusWithoutChangingCollapsedGeometry()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var store = new RecordingBoardStore(directory.Root)
        {
            SaveFailure = new IOException("Injected external save failure.")
        };
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            CompleteLayout(window);
            var data = new DataObject(DataFormats.UnicodeText, "无法保存的拖入");
            var collapsedTab = FindCollapsedCategoryTab(window);
            collapsedTab.RaiseEvent(NewDragEventArgs(data, DragDrop.DragEnterEvent, collapsedTab));
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var prompt = viewModel.Categories.Single(
                category => category.Category == BoardCategory.Prompt);
            var promptTab = FindCategoryTab(window, prompt);
            promptTab.RaiseEvent(NewDragEventArgs(data, DragDrop.DropEvent, promptTab));
            CompleteLayout(window);

            var popup = window.FindName("CompactStatusPopup") as Popup;
            var rail = (Border)window.FindName("CategoryRail");
            var overlay = (Border)window.FindName("StatusOverlay");
            Assert.IsNotNull(popup);
            Assert.IsTrue(popup.IsOpen);
            Assert.AreSame(rail, popup.PlacementTarget);
            Assert.AreEqual(PlacementMode.Left, popup.Placement);
            Assert.IsFalse(popup.IsHitTestVisible);
            Assert.IsFalse(popup.Focusable);
            Assert.IsFalse(overlay.IsVisible);
            Assert.AreEqual("拖入内容未保存，请重试。", viewModel.StatusText);
            Assert.IsFalse(viewModel.IsExternalDropRailVisible);
            Assert.AreEqual(WindowSettings.TabWidth, window.Width, 0.5);
            Assert.AreEqual(
                WindowSettings.Default.WindowHeight / BoardCategoryCatalog.Ordered.Count,
                window.Height,
                0.5);
            Assert.AreEqual(0, board.Items(BoardCategory.Prompt).Count);

            InvokePrivate(window, "StatusTimer_Tick", null, EventArgs.Empty);

            Assert.IsFalse(popup.IsOpen);
            Assert.AreEqual(string.Empty, viewModel.StatusText);
        }
        finally
        {
            store.SaveFailure = null;
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void RejectedExternalDrop_ClearsTemporaryRailWithoutImporting()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            CompleteLayout(window);
            var supported = new DataObject(DataFormats.UnicodeText, "supported");
            var collapsedTab = FindCollapsedCategoryTab(window);
            collapsedTab.RaiseEvent(NewDragEventArgs(
                supported,
                DragDrop.DragEnterEvent,
                collapsedTab));
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var prompt = viewModel.Categories.Single(
                category => category.Category == BoardCategory.Prompt);
            var promptTab = FindCategoryTab(window, prompt);
            var rejected = NewDragEventArgs(
                new DataObject("unsupported/custom", new object()),
                DragDrop.DropEvent,
                promptTab);

            promptTab.RaiseEvent(rejected);
            CompleteLayout(window);

            Assert.AreEqual(DragDropEffects.None, rejected.Effects);
            Assert.IsTrue(rejected.Handled);
            Assert.IsFalse(viewModel.IsExternalDropRailVisible);
            Assert.IsFalse(viewModel.Categories.Any(category => category.IsDropTarget));
            Assert.AreEqual(0, store.SaveCount);
            Assert.AreEqual(0, board.Items(BoardCategory.Prompt).Count);
            Assert.AreEqual(
                WindowSettings.Default.WindowHeight / BoardCategoryCatalog.Ordered.Count,
                window.Height,
                0.5);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void InternalCategoryDrop_MovesToTopAndStaysOnTargetPanel()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var moved = board.AddText("move me", BoardCategory.Inbox);
        var existing = board.AddText("existing", BoardCategory.Prompt);
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);
        var data = new DataObject();
        data.SetData(DragPayloadService.InternalItemIdFormat, moved.Id.ToString("D"));

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var prompt = viewModel.Categories.Single(
                category => category.Category == BoardCategory.Prompt);
            var promptTab = FindCategoryTab(window, prompt);
            var enter = NewDragEventArgs(data, DragDrop.DragEnterEvent, promptTab);
            promptTab.RaiseEvent(enter);
            var drop = NewDragEventArgs(data, DragDrop.DropEvent, promptTab);

            promptTab.RaiseEvent(drop);
            PumpDispatcherUntil(window.Dispatcher, store.SaveCompleted.Task);
            CompleteLayout(window);

            Assert.AreEqual(DragDropEffects.Move, enter.Effects);
            Assert.AreEqual(DragDropEffects.Move, drop.Effects);
            Assert.IsFalse(viewModel.IsExternalDropRailVisible);
            CollectionAssert.AreEqual(
                new[] { moved.Id, existing.Id },
                board.Items(BoardCategory.Prompt).Select(item => item.Id).ToArray());
            Assert.AreEqual(BoardCategory.Prompt, viewModel.ActivePanel!.Category);
            Assert.IsTrue(viewModel.IsPanelExpanded);
        }
        finally
        {
            CloseWindow(window);
        }
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
    public void SelectedCountBadge_UsesStableCircleSizeForSingleAndMultipleSelection()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var first = board.AddText("first");
        var second = board.AddText("second");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            var list = (ListBox)window.FindName("BoardList");
            var badge = (Border?)window.FindName("SelectedCountBadge");
            var count = (TextBlock?)window.FindName("SelectedCountText");
            Assert.IsNotNull(badge);
            Assert.IsNotNull(count);

            list.SelectedItems.Add(first);
            CompleteLayout(window);
            Assert.AreEqual(14d, badge.Width);
            Assert.AreEqual(14d, badge.Height);
            Assert.AreEqual("1", count.Text);
            var singleSize = new Size(badge.ActualWidth, badge.ActualHeight);

            list.SelectedItems.Add(second);
            CompleteLayout(window);
            Assert.AreEqual(singleSize.Width, badge.ActualWidth, 0.01);
            Assert.AreEqual(singleSize.Height, badge.ActualHeight, 0.01);
            Assert.AreEqual("2", count.Text);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void InternalBatchCategoryDrop_MovesOnceInSourceOrderAndShowsTargetTop()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var image = board.AddImage(
            Guid.NewGuid(),
            "images/customer.png",
            Path.Combine(directory.Root, "customer.png"));
        var text = board.AddText("text");
        var existing = board.AddText("existing", BoardCategory.CustomerOriginal);
        for (var index = 0; index < 20; index++)
        {
            board.AddText($"target {index} {new string('x', 100)}", BoardCategory.CustomerOriginal);
        }

        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.CustomerOriginal);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var viewer = FindDescendant<ScrollViewer>(list);
            Assert.IsNotNull(viewer);
            ScrollTo(window, viewer, 120);
            EnterCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            list.SelectedItems.Add(image);
            list.SelectedItems.Add(text);
            var data = new DragPayloadService().BuildInternalBatch([text, image]);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var targetPanel = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);
            var targetTab = FindCategoryTab(window, targetPanel);
            var drop = NewDragEventArgs(data, DragDrop.DropEvent, targetTab);

            targetTab.RaiseEvent(drop);
            PumpDispatcherUntil(window.Dispatcher, store.SaveCompleted.Task);
            CompleteLayout(window);

            Assert.AreEqual(DragDropEffects.Move, drop.Effects);
            Assert.AreEqual(1, store.SaveCount);
            CollectionAssert.AreEqual(
                new[] { text.Id, image.Id },
                board.Items(BoardCategory.CustomerOriginal)
                    .Take(2)
                    .Select(item => item.Id)
                    .ToArray());
            Assert.AreSame(existing, board.Items(BoardCategory.CustomerOriginal).Last());
            Assert.AreEqual(BoardCategory.CustomerOriginal, viewModel.ActivePanel!.Category);
            Assert.AreEqual(0, list.SelectedItems.Count);
            Assert.AreEqual(0d, viewer.VerticalOffset, 0.5);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void InternalBatchCategoryDrop_SaveFailureRestoresOrderAndSelection()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var bottom = board.AddText("bottom");
        var top = board.AddText("top");
        var target = board.AddText("target", BoardCategory.Prompt);
        var store = new RecordingBoardStore(directory.Root)
        {
            SaveFailure = new IOException("Injected failure.")
        };
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(top);
            list.SelectedItems.Add(bottom);
            var data = new DragPayloadService().BuildInternalBatch([top, bottom]);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var promptTab = FindCategoryTab(
                window,
                viewModel.Categories.Single(panel => panel.Category == BoardCategory.Prompt));
            var drop = NewDragEventArgs(data, DragDrop.DropEvent, promptTab);

            promptTab.RaiseEvent(drop);
            CompleteLayout(window);

            Assert.AreEqual(DragDropEffects.None, drop.Effects);
            CollectionAssert.AreEqual(
                new[] { top.Id, bottom.Id },
                board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
            CollectionAssert.AreEqual(
                new[] { target.Id },
                board.Items(BoardCategory.Prompt).Select(item => item.Id).ToArray());
            CollectionAssert.AreEquivalent(
                new[] { top, bottom },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            Assert.AreEqual(BoardCategory.Inbox, viewModel.ActivePanel!.Category);
            Assert.AreEqual("移动未保存，内容已恢复到原位置。", viewModel.StatusText);
        }
        finally
        {
            store.SaveFailure = null;
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void InternalBatchCategoryDrop_SaveFailureRestoresScrollOffset()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        AddScrollableItems(board, BoardCategory.Inbox);
        var selectedTop = board.Items(BoardCategory.Inbox)[8];
        var selectedBottom = board.Items(BoardCategory.Inbox)[9];
        var before = board.Items(BoardCategory.Inbox).ToArray();
        board.AddText("target", BoardCategory.Prompt);
        var store = new UiThreadFailingFirstSaveBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var viewer = FindDescendant<ScrollViewer>(list);
            Assert.IsNotNull(viewer);
            ScrollTo(window, viewer, 120);
            var offset = viewer.VerticalOffset;
            Assert.IsGreaterThan(0d, offset);
            list.SelectedItems.Add(selectedTop);
            list.SelectedItems.Add(selectedBottom);
            var data = new DragPayloadService().BuildInternalBatch(
                [selectedTop, selectedBottom]);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var moveFailed = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            viewModel.PropertyChanged += (_, eventArgs) =>
            {
                if (eventArgs.PropertyName == nameof(MainWindowViewModel.StatusText) &&
                    viewModel.StatusText == "移动未保存，内容已恢复到原位置。")
                {
                    moveFailed.TrySetResult();
                }
            };
            var promptTab = FindCategoryTab(
                window,
                viewModel.Categories.Single(panel => panel.Category == BoardCategory.Prompt));
            var drop = NewDragEventArgs(data, DragDrop.DropEvent, promptTab);

            promptTab.RaiseEvent(drop);
            PumpDispatcherUntil(window.Dispatcher, store.FirstSaveStarted.Task);
            CompleteLayout(window);
            store.FailFirstSave();
            PumpDispatcherUntil(window.Dispatcher, moveFailed.Task);
            CompleteLayout(window);

            CollectionAssert.AreEqual(before, board.Items(BoardCategory.Inbox).ToArray());
            Assert.AreEqual(BoardCategory.Inbox, viewModel.ActivePanel!.Category);
            Assert.AreEqual(offset, viewer.VerticalOffset, 0.5);
        }
        finally
        {
            store.FailFirstSave();
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void InternalBatchListDrop_ReordersAsOneBlockAndClearsSelection()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var e = board.AddText("E");
        var d = board.AddText("D");
        var c = board.AddText("C");
        var b = board.AddText("B");
        var a = board.AddText("A");
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(b);
            list.SelectedItems.Add(d);
            var data = new DragPayloadService().BuildInternalBatch([b, d]);
            var target = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(e);
            Assert.IsNotNull(target);
            var over = NewDragEventArgs(data, DragDrop.PreviewDragOverEvent, target);
            target.RaiseEvent(over);
            var indicator = (Border)window.FindName("InsertionIndicator");
            Assert.AreEqual(DragDropEffects.Move, over.Effects);
            Assert.AreEqual(Visibility.Visible, indicator.Visibility);
            var drop = NewDragEventArgs(data, DragDrop.PreviewDropEvent, target);

            target.RaiseEvent(drop);
            PumpDispatcherUntil(window.Dispatcher, store.SaveCompleted.Task);
            CompleteLayout(window);

            Assert.AreEqual(1, store.SaveCount);
            CollectionAssert.AreEqual(
                new[] { a.Id, c.Id, b.Id, d.Id, e.Id },
                board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
            Assert.AreEqual(0, list.SelectedItems.Count);
            Assert.AreEqual(Visibility.Collapsed, indicator.Visibility);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void InternalBatchEquivalentListDropDoesNotSaveAndKeepsSelection()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var bottom = board.AddText("bottom");
        var middle = board.AddText("middle");
        var top = board.AddText("top");
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(top);
            list.SelectedItems.Add(middle);
            var data = new DragPayloadService().BuildInternalBatch([top, middle]);
            var target = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(bottom);
            Assert.IsNotNull(target);

            target.RaiseEvent(NewDragEventArgs(data, DragDrop.PreviewDropEvent, target));
            CompleteLayout(window);

            Assert.AreEqual(0, store.SaveCount);
            CollectionAssert.AreEqual(
                new[] { top.Id, middle.Id, bottom.Id },
                board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
            CollectionAssert.AreEquivalent(
                new[] { top, middle },
                list.SelectedItems.Cast<BoardItem>().ToArray());
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void HeaderDeleteFailureRestoresMixedSelectionAndSuccessDeletesOnlySelection()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var keep = board.AddText("keep");
        var selectedNormal = board.AddText("selected normal");
        var selectedPinned = board.AddText("selected pinned");
        board.SetPinnedMany([selectedPinned.Id], true);
        var store = new RecordingBoardStore(directory.Root)
        {
            SaveFailure = new IOException("Injected failure.")
        };
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(selectedPinned);
            list.SelectedItems.Add(selectedNormal);
            CompleteLayout(window);
            var delete = (Button?)window.FindName("DeleteContentButton");
            var badge = (Border?)window.FindName("SelectedCountBadge");
            var count = (TextBlock?)window.FindName("SelectedCountText");
            Assert.IsNotNull(delete);
            Assert.IsNotNull(badge);
            Assert.IsNotNull(count);
            Assert.AreEqual(Visibility.Visible, badge.Visibility);
            Assert.AreEqual("2", count.Text);
            Assert.IsFalse(badge.IsHitTestVisible);

            delete.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, delete));
            CompleteLayout(window);

            CollectionAssert.AreEqual(
                new[] { selectedPinned.Id, selectedNormal.Id, keep.Id },
                board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
            Assert.IsTrue(selectedPinned.IsPinned);
            CollectionAssert.AreEquivalent(
                new[] { selectedPinned, selectedNormal },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            Assert.AreEqual("2", count.Text);
            store.SaveFailure = null;
            delete.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, delete));
            PumpDispatcherUntil(window.Dispatcher, store.SaveCompleted.Task);
            CompleteLayout(window);

            CollectionAssert.AreEqual(
                new[] { keep.Id },
                board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
            Assert.AreEqual(0, list.SelectedItems.Count);
            Assert.AreEqual(Visibility.Collapsed, badge.Visibility);
            Assert.AreEqual(1, store.SaveCount);
        }
        finally
        {
            store.SaveFailure = null;
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void HeaderDeleteWithoutSelectionFailureRestoresCategoryAndNewItemStaysUnselected()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var second = board.AddText("second");
        var first = board.AddText("first");
        var store = new RecordingBoardStore(directory.Root)
        {
            SaveFailure = new IOException("Injected failure.")
        };
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var delete = (Button?)window.FindName("DeleteContentButton");
            Assert.IsNotNull(delete);
            delete.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, delete));
            CompleteLayout(window);

            CollectionAssert.AreEqual(
                new[] { first.Id, second.Id },
                board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
            Assert.AreEqual(0, list.SelectedItems.Count);
            var captured = board.AddText("new capture");
            CompleteLayout(window);
            Assert.IsFalse(list.SelectedItems.Contains(captured));
            Assert.AreEqual(0, list.SelectedItems.Count);
        }
        finally
        {
            store.SaveFailure = null;
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Backspace_DeletesSelectionButNeverClearsWithoutSelection()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var keep = board.AddText("keep");
        var remove = board.AddText("remove");
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(remove);

            window.RaiseEvent(NewKeyEventArgs(window, Key.Back));
            PumpDispatcherUntil(window.Dispatcher, store.SaveCompleted.Task);
            CompleteLayout(window);

            CollectionAssert.AreEqual(
                new[] { keep.Id },
                board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
            var saves = store.SaveCount;
            window.RaiseEvent(NewKeyEventArgs(window, Key.Back));
            CompleteLayout(window);
            Assert.AreEqual(saves, store.SaveCount);
            Assert.AreSame(keep, board.Items(BoardCategory.Inbox).Single());
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Backspace_CategoryNameEditorFocusNeverDeletesSelectedCards()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var selected = board.AddText("selected card");
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(selected);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var panel = viewModel.Categories.Single(
                category => category.Category == BoardCategory.Inbox);
            viewModel.BeginCategoryNameEdit(panel);
            CompleteLayout(window);
            var editor = FindDescendants<TextBox>(FindCategoryTab(window, panel)).Single();
            Assert.IsTrue(editor.Focus());
            Keyboard.Focus(editor);
            var backspace = NewKeyEventArgs(window, Key.Back);

            InvokePrivate(window, "MainWindow_PreviewKeyDown", window, backspace);
            CompleteLayout(window);

            Assert.IsFalse(backspace.Handled);
            Assert.AreSame(selected, board.Items(BoardCategory.Inbox).Single());
            Assert.AreEqual(0, store.SaveCount);
            CollectionAssert.AreEqual(
                new[] { selected },
                list.SelectedItems.Cast<BoardItem>().ToArray());
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void SelectionSurvivesAutoCollapseButClearsOnUserCategorySwitch()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var second = board.AddText("second");
        var first = board.AddText("first");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(first);
            list.SelectedItems.Add(second);

            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
            Assert.AreEqual(2, list.SelectedItems.Count);
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            Assert.AreEqual(2, list.SelectedItems.Count);

            EnterCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            Assert.AreEqual(0, list.SelectedItems.Count);
            Assert.AreEqual(
                BoardCategory.Reference,
                ((MainWindowViewModel)window.DataContext).ActivePanel!.Category);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void SelectionGestureRequiresControlAndNoDragThreshold()
    {
        var method = typeof(MainWindow).GetMethod(
            "ShouldToggleSelection",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(method);
        Assert.AreEqual(true, method.Invoke(null, [ModifierKeys.Control, false]));
        Assert.AreEqual(false, method.Invoke(null, [ModifierKeys.None, false]));
        Assert.AreEqual(false, method.Invoke(null, [ModifierKeys.Control, true]));
    }

    [STATestMethod]
    public void SelectionButtonTogglesPersistentTwoSignalFeedback()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("select me");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(item);
            Assert.IsNotNull(container);
            var button = FindDescendants<Button>(container)
                .Single(candidate => Equals(candidate.CommandParameter, "ToggleSelection"));

            Assert.AreEqual(0d, button.Opacity);
            Assert.IsFalse(button.IsHitTestVisible);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            CompleteLayout(window);

            Assert.IsTrue(container.IsSelected);
            Assert.AreEqual(1d, button.Opacity);
            Assert.IsTrue(button.IsHitTestVisible);
            var card = FindDescendants<Border>(container)
                .Single(candidate => ReferenceEquals(
                    candidate.Style,
                    window.FindResource("CardContainerStyle")));
            Assert.AreSame(window.FindResource("SelectedCardBorderBrush"), card.BorderBrush);
            Assert.AreSame(window.FindResource("SelectedCardBrush"), card.Background);

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            Assert.IsFalse(container.IsSelected);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void EmptyListBackgroundClearsSelectionWithoutChangingPanelOrScroll()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        AddScrollableItems(board, BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            Assert.AreEqual(SelectionMode.Multiple, list.SelectionMode);
            list.SelectedItems.Add(list.Items[0]);
            list.SelectedItems.Add(list.Items[1]);
            var viewer = FindDescendant<ScrollViewer>(list);
            Assert.IsNotNull(viewer);
            ScrollTo(window, viewer, 120);
            var before = viewer.VerticalOffset;

            InvokePrivate(
                window,
                "BoardList_PreviewMouseLeftButtonDown",
                list,
                NewMouseButtonEventArgs(Mouse.PreviewMouseDownEvent, list));

            Assert.AreEqual(0, list.SelectedItems.Count);
            Assert.AreEqual(
                BoardCategory.Inbox,
                ((MainWindowViewModel)window.DataContext).ActivePanel!.Category);
            Assert.AreEqual(before, viewer.VerticalOffset, 0.5);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void InternalDragData_SelectedOriginUsesAllSelectionInSourceOrder()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var bottom = board.AddText("bottom");
        var middle = board.AddText("middle");
        var top = board.AddText("top");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(bottom);
            list.SelectedItems.Add(top);
            var method = GetPrivateMethod("BuildInternalDragData");
            Assert.IsNotNull(method);

            var data = method.Invoke(window, [bottom]) as IDataObject;

            Assert.IsNotNull(data);
            CollectionAssert.AreEqual(
                new[] { top.Id, bottom.Id },
                new DragPayloadService().GetInternalItemIds(data)!.ToArray());
            Assert.AreEqual(2, list.SelectedItems.Count);
            Assert.IsFalse(data.GetDataPresent(DataFormats.UnicodeText));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void InternalDragData_UnselectedOriginUsesOnlyItAndClearsOldSelection()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var bottom = board.AddText("bottom");
        var middle = board.AddText("middle");
        var top = board.AddText("top");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(bottom);
            list.SelectedItems.Add(top);
            var method = GetPrivateMethod("BuildInternalDragData");
            Assert.IsNotNull(method);

            var data = method.Invoke(window, [middle]) as IDataObject;

            Assert.IsNotNull(data);
            CollectionAssert.AreEqual(
                new[] { middle.Id },
                new DragPayloadService().GetInternalItemIds(data)!.ToArray());
            Assert.AreEqual(0, list.SelectedItems.Count);
            Assert.AreEqual("middle", data.GetData(DataFormats.UnicodeText));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CategoryHoverDuringInternalDragKeepsSourcePanelAndSelection()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("selected");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(item);

            InvokePrivate(window, "BeginPanelDrag");
            EnterCategory(window, BoardCategory.Prompt);

            var viewModel = (MainWindowViewModel)window.DataContext;
            Assert.AreEqual(BoardCategory.Inbox, viewModel.ActivePanel!.Category);
            CollectionAssert.AreEqual(
                new[] { item },
                list.SelectedItems.Cast<BoardItem>().ToArray());
        }
        finally
        {
            InvokePrivate(window, "EndPanelDrag");
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
    public void CategoryDropFeedback_AllowsOnlyOneInternalTargetAndClearsOnLeave()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("drag source");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var first = viewModel.Categories[0];
            var second = viewModel.Categories[1];
            var firstTab = FindCategoryTab(window, first);
            var secondTab = FindCategoryTab(window, second);
            var internalData = new DragPayloadService().Build(item);

            firstTab.RaiseEvent(NewDragEventArgs(internalData, DragDrop.DragEnterEvent, firstTab));
            Assert.AreSame(first, viewModel.Categories.Single(category => category.IsDropTarget));

            secondTab.RaiseEvent(NewDragEventArgs(internalData, DragDrop.DragEnterEvent, secondTab));
            Assert.AreSame(second, viewModel.Categories.Single(category => category.IsDropTarget));

            secondTab.RaiseEvent(NewDragEventArgs(internalData, DragDrop.DragLeaveEvent, secondTab));
            Assert.IsFalse(viewModel.Categories.Any(category => category.IsDropTarget));

            var invalid = NewDragEventArgs(
                new DataObject("unsupported/custom", new object()),
                DragDrop.DragEnterEvent,
                firstTab);
            firstTab.RaiseEvent(invalid);
            Assert.AreEqual(DragDropEffects.None, invalid.Effects);
            Assert.IsFalse(viewModel.Categories.Any(category => category.IsDropTarget));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void BoardInsertionFeedback_ShowsOnlyForInternalDataAndStaysInsideList()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("drag source");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var indicator = window.FindName("InsertionIndicator") as Border;
            Assert.IsNotNull(indicator);
            var internalData = new DragPayloadService().Build(item);

            var valid = NewDragEventArgs(
                internalData,
                DragDrop.PreviewDragOverEvent,
                list);
            list.RaiseEvent(valid);

            Assert.AreEqual(DragDropEffects.Move, valid.Effects);
            Assert.AreEqual(Visibility.Visible, indicator.Visibility);
            Assert.AreEqual(1d, indicator.Opacity);
            Assert.IsTrue(Canvas.GetTop(indicator) >= 0d);
            Assert.IsTrue(
                Canvas.GetTop(indicator) <= Math.Max(0d, list.ActualHeight - indicator.Height));

            var invalid = NewDragEventArgs(
                new DataObject(DataFormats.UnicodeText, "external"),
                DragDrop.PreviewDragOverEvent,
                list);
            list.RaiseEvent(invalid);
            Assert.AreEqual(DragDropEffects.None, invalid.Effects);
            Assert.AreEqual(Visibility.Collapsed, indicator.Visibility);

            list.RaiseEvent(NewDragEventArgs(
                internalData,
                DragDrop.PreviewDragOverEvent,
                list));
            list.RaiseEvent(NewDragEventArgs(
                internalData,
                DragDrop.PreviewDragLeaveEvent,
                list));
            Assert.AreEqual(Visibility.Collapsed, indicator.Visibility);
        }
        finally
        {
            CloseWindow(window);
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
    public void DragLifecycle_StopsTimersKeepsPanelOpenAndDefersPointerReconciliation()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            ExpandCategory(window, BoardCategory.Reference);
            var expandTimer = GetPrivateField<DispatcherTimer>(window, "_expandIntentTimer");
            var collapseTimer = GetPrivateField<DispatcherTimer>(window, "_collapseTimer");

            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            expandTimer.Start();
            Assert.IsTrue(expandTimer.IsEnabled);
            Assert.IsTrue(collapseTimer.IsEnabled);

            InvokePrivate(window, "BeginPanelDrag");

            Assert.IsFalse(expandTimer.IsEnabled);
            Assert.IsFalse(collapseTimer.IsEnabled);

            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
            Assert.AreEqual(
                WindowSettings.Default.PanelWidth + WindowSettings.TabWidth,
                window.Width);

            InvokePrivate(window, "EndPanelDrag");

            Assert.IsFalse(collapseTimer.IsEnabled);
            CompleteLayout(window);
            Assert.IsTrue(collapseTimer.IsEnabled);
            InvokePrivate(window, "Root_MouseEnter", window, NewMouseEventArgs());
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

    [STATestMethod]
    public void BatchPinButton_TracksSelectionAndAdvertisesOneDeterministicAction()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var normal = board.AddText("normal");
        var pinned = board.AddText("pinned");
        board.SetPinnedMany([pinned.Id], true);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var batchPin = (Button?)window.FindName("BatchPinButton");
            Assert.IsNotNull(batchPin);
            Assert.AreEqual(Visibility.Collapsed, batchPin.Visibility);

            list.SelectedItems.Add(pinned);
            list.SelectedItems.Add(normal);
            CompleteLayout(window);

            Assert.AreEqual(Visibility.Visible, batchPin.Visibility);
            Assert.AreEqual("置顶已选 2 项", batchPin.ToolTip);
            Assert.AreEqual("置顶已选 2 项", AutomationProperties.GetName(batchPin));

            list.SelectedItems.Remove(normal);
            CompleteLayout(window);

            Assert.AreEqual("取消置顶已选 1 项", batchPin.ToolTip);
            Assert.AreEqual("取消置顶已选 1 项", AutomationProperties.GetName(batchPin));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void BatchPinCommand_CtrlPInvokesTheCurrentSelection()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var selectedBottom = board.AddText("selected bottom");
        var selectedTop = board.AddText("selected top");
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(selectedBottom);
            list.SelectedItems.Add(selectedTop);
            var batchPin = (Button?)window.FindName("BatchPinButton");
            Assert.IsNotNull(batchPin);
            Assert.AreEqual("Ctrl+P", AutomationProperties.GetAccessKey(batchPin));
            var commandProperty = typeof(MainWindow).GetProperty(
                "BatchPinCommand",
                BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(commandProperty);
            var command = commandProperty.GetValue(null) as RoutedUICommand;
            Assert.IsNotNull(command);
            Assert.IsTrue(command.InputGestures.OfType<KeyGesture>().Any(
                gesture => gesture.Key == Key.P &&
                           gesture.Modifiers == ModifierKeys.Control));
            Assert.IsTrue(command.CanExecute(null, window));

            command.Execute(null, window);
            PumpDispatcherUntil(window.Dispatcher, store.SaveCompleted.Task);
            CompleteLayout(window);

            Assert.IsTrue(selectedTop.IsPinned);
            Assert.IsTrue(selectedBottom.IsPinned);
            Assert.AreEqual(1, store.SaveCount);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void BatchPinButton_MixedSelectionPinsAllInSourceOrderAndPreservesSelectionAndScroll()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var keep = board.AddText("keep");
        var selectedBottom = board.AddText("selected bottom");
        var selectedTop = board.AddText("selected top");
        var existingPin = board.AddText("existing pin");
        board.SetPinnedMany([existingPin.Id], true);
        AddScrollableItems(board, BoardCategory.Inbox);
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var viewer = FindDescendant<ScrollViewer>(list);
            Assert.IsNotNull(viewer);
            ScrollTo(window, viewer, 120);
            var offset = viewer.VerticalOffset;
            list.SelectedItems.Add(existingPin);
            list.SelectedItems.Add(selectedBottom);
            list.SelectedItems.Add(selectedTop);
            var batchPin = (Button?)window.FindName("BatchPinButton");
            Assert.IsNotNull(batchPin);

            ((RoutedCommand)batchPin.Command).Execute(batchPin.CommandParameter, batchPin);
            PumpDispatcherUntil(window.Dispatcher, store.SaveCompleted.Task);
            CompleteLayout(window);

            Assert.IsTrue(existingPin.IsPinned);
            Assert.IsTrue(selectedTop.IsPinned);
            Assert.IsTrue(selectedBottom.IsPinned);
            Assert.IsFalse(keep.IsPinned);
            CollectionAssert.AreEqual(
                new[] { existingPin, selectedTop, selectedBottom },
                board.Items(BoardCategory.Inbox).Take(3).ToArray());
            Assert.AreEqual(1, store.SaveCount);
            CollectionAssert.AreEquivalent(
                new[] { existingPin, selectedTop, selectedBottom },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            Assert.AreEqual(offset, viewer.VerticalOffset, 0.5);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void BatchPinButton_AllPinnedSelectionUnpinsAsOneSourceOrderedBlock()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var normal = board.AddText("normal");
        var selectedLower = board.AddText("selected lower");
        var selectedUpper = board.AddText("selected upper");
        var keepPinned = board.AddText("keep pinned");
        board.SetPinnedMany([keepPinned.Id, selectedUpper.Id, selectedLower.Id], true);
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(selectedLower);
            list.SelectedItems.Add(selectedUpper);
            var batchPin = (Button?)window.FindName("BatchPinButton");
            Assert.IsNotNull(batchPin);

            ((RoutedCommand)batchPin.Command).Execute(batchPin.CommandParameter, batchPin);
            PumpDispatcherUntil(window.Dispatcher, store.SaveCompleted.Task);
            CompleteLayout(window);

            CollectionAssert.AreEqual(
                new[] { keepPinned, selectedUpper, selectedLower, normal },
                board.Items(BoardCategory.Inbox).ToArray());
            Assert.IsTrue(keepPinned.IsPinned);
            Assert.IsFalse(selectedUpper.IsPinned);
            Assert.IsFalse(selectedLower.IsPinned);
            CollectionAssert.AreEquivalent(
                new[] { selectedUpper, selectedLower },
                list.SelectedItems.Cast<BoardItem>().ToArray());
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void BatchPinButton_SlowSaveKeepsSelectionAndDeleteScope()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var selectedOther = board.AddText("selected other");
        var selectedTop = board.AddText("selected top");
        var store = new BlockingFirstSuccessfulSaveBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(selectedTop);
            list.SelectedItems.Add(selectedOther);
            var batchPin = (Button?)window.FindName("BatchPinButton");
            Assert.IsNotNull(batchPin);

            ((RoutedCommand)batchPin.Command).Execute(batchPin.CommandParameter, batchPin);
            PumpDispatcherUntil(window.Dispatcher, store.FirstSaveStarted.Task);
            CompleteLayout(window);

            CollectionAssert.AreEquivalent(
                new[] { selectedTop, selectedOther },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            Assert.AreEqual(
                Visibility.Visible,
                ((Border)window.FindName("SelectedCountBadge")).Visibility);
            Assert.AreEqual("2", ((TextBlock)window.FindName("SelectedCountText")).Text);
            Assert.AreEqual(
                "删除已选 2 项",
                ((Button)window.FindName("DeleteContentButton")).ToolTip);
            Assert.AreEqual(Visibility.Visible, batchPin.Visibility);
            Assert.IsFalse(batchPin.IsEnabled);
            Assert.AreEqual("正在保存 2 项置顶状态", batchPin.ToolTip);
            Assert.AreEqual(
                "正在保存 2 项置顶状态",
                AutomationProperties.GetName(batchPin));

            store.ReleaseFirstSave();
            PumpDispatcherUntil(window.Dispatcher, store.FirstSaveCompleted.Task);
            CompleteLayout(window);

            CollectionAssert.AreEquivalent(
                new[] { selectedTop, selectedOther },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            Assert.IsTrue(batchPin.IsEnabled);
            Assert.AreEqual("取消置顶已选 2 项", batchPin.ToolTip);
        }
        finally
        {
            store.ReleaseFirstSave();
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void BatchPinButton_SaveFailureRestoresStateOrderSelectionAndScroll()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        AddScrollableItems(board, BoardCategory.Inbox);
        var selectedTop = board.Items(BoardCategory.Inbox)[3];
        var selectedBottom = board.Items(BoardCategory.Inbox)[5];
        var before = board.Items(BoardCategory.Inbox).ToArray();
        var beforePinStates = before.Select(item => item.IsPinned).ToArray();
        var store = new RecordingBoardStore(directory.Root)
        {
            SaveFailure = new IOException("Injected failure.")
        };
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var viewer = FindDescendant<ScrollViewer>(list);
            Assert.IsNotNull(viewer);
            ScrollTo(window, viewer, 120);
            var offset = viewer.VerticalOffset;
            list.SelectedItems.Add(selectedTop);
            list.SelectedItems.Add(selectedBottom);
            var batchPin = (Button?)window.FindName("BatchPinButton");
            Assert.IsNotNull(batchPin);

            ((RoutedCommand)batchPin.Command).Execute(batchPin.CommandParameter, batchPin);
            CompleteLayout(window);

            CollectionAssert.AreEqual(before, board.Items(BoardCategory.Inbox).ToArray());
            CollectionAssert.AreEqual(
                beforePinStates,
                before.Select(item => item.IsPinned).ToArray());
            CollectionAssert.AreEquivalent(
                new[] { selectedTop, selectedBottom },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            Assert.AreEqual(offset, viewer.VerticalOffset, 0.5);
            Assert.AreEqual(
                "置顶状态未保存，内容已恢复。",
                ((MainWindowViewModel)window.DataContext).StatusText);
        }
        finally
        {
            store.SaveFailure = null;
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void PinButton_TogglesOnlyClickedItemAndPreservesSelectionAndScroll()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        AddScrollableItems(board, BoardCategory.Inbox);
        var clicked = board.Items(BoardCategory.Inbox)[3];
        var other = board.Items(BoardCategory.Inbox)[4];
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var viewer = FindDescendant<ScrollViewer>(list);
            Assert.IsNotNull(viewer);
            ScrollTo(window, viewer, 120);
            var offset = viewer.VerticalOffset;
            list.SelectedItems.Add(clicked);
            list.SelectedItems.Add(other);
            var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(clicked);
            Assert.IsNotNull(container);
            var pin = FindDescendants<Button>(container)
                .Single(button => Equals(button.CommandParameter, "TogglePin"));

            pin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, pin));
            PumpDispatcherUntil(window.Dispatcher, store.SaveCompleted.Task);
            CompleteLayout(window);

            Assert.IsTrue(clicked.IsPinned);
            Assert.IsFalse(other.IsPinned);
            Assert.AreSame(clicked, board.Items(BoardCategory.Inbox)[0]);
            CollectionAssert.AreEquivalent(
                new[] { clicked, other },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            Assert.AreEqual(offset, viewer.VerticalOffset, 0.5);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void PinButton_SlowSaveKeepsSelectionAndHeaderDeleteScope()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var unselected = board.AddText("unselected");
        var selectedOther = board.AddText("selected other");
        var clicked = board.AddText("clicked");
        var store = new BlockingFirstSuccessfulSaveBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(clicked);
            list.SelectedItems.Add(selectedOther);
            var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(clicked);
            Assert.IsNotNull(container);
            var pin = FindDescendants<Button>(container)
                .Single(button => Equals(button.CommandParameter, "TogglePin"));

            pin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, pin));
            PumpDispatcherUntil(window.Dispatcher, store.FirstSaveStarted.Task);
            CompleteLayout(window);

            CollectionAssert.AreEquivalent(
                new[] { clicked, selectedOther },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            Assert.AreEqual(
                Visibility.Visible,
                ((Border)window.FindName("SelectedCountBadge")).Visibility);
            Assert.AreEqual("2", ((TextBlock)window.FindName("SelectedCountText")).Text);
            var delete = (Button)window.FindName("DeleteContentButton");
            Assert.AreEqual("删除已选 2 项", delete.ToolTip);

            store.ReleaseFirstSave();
            PumpDispatcherUntil(window.Dispatcher, store.FirstSaveCompleted.Task);
            CompleteLayout(window);

            CollectionAssert.AreEquivalent(
                new[] { clicked, selectedOther },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            CollectionAssert.AreEquivalent(
                new[] { clicked, selectedOther, unselected },
                board.Items(BoardCategory.Inbox).ToArray());
        }
        finally
        {
            store.ReleaseFirstSave();
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void PinButton_SaveFailureRestoresStateOrderSelectionAndScroll()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        AddScrollableItems(board, BoardCategory.Inbox);
        var clicked = board.Items(BoardCategory.Inbox)[3];
        var other = board.Items(BoardCategory.Inbox)[4];
        var before = board.Items(BoardCategory.Inbox).ToArray();
        var store = new RecordingBoardStore(directory.Root)
        {
            SaveFailure = new IOException("Injected failure.")
        };
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var viewer = FindDescendant<ScrollViewer>(list);
            Assert.IsNotNull(viewer);
            ScrollTo(window, viewer, 120);
            var offset = viewer.VerticalOffset;
            list.SelectedItems.Add(clicked);
            list.SelectedItems.Add(other);
            var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(clicked);
            Assert.IsNotNull(container);
            var pin = FindDescendants<Button>(container)
                .Single(button => Equals(button.CommandParameter, "TogglePin"));

            pin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, pin));
            CompleteLayout(window);

            CollectionAssert.AreEqual(before, board.Items(BoardCategory.Inbox).ToArray());
            Assert.IsFalse(clicked.IsPinned);
            CollectionAssert.AreEquivalent(
                new[] { clicked, other },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            Assert.AreEqual(offset, viewer.VerticalOffset, 0.5);
            Assert.AreEqual(
                "置顶状态未保存，内容已恢复。",
                ((MainWindowViewModel)window.DataContext).StatusText);
        }
        finally
        {
            store.SaveFailure = null;
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

    [STATestMethod]
    public void PinnedDrag_CrossRegionHidesInsertionIndicatorInBothDirections()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var normal = board.AddText("normal");
        var pinned = board.AddText("pinned");
        board.SetPinnedMany([pinned.Id], true);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var indicator = (Border)window.FindName("InsertionIndicator");
            var normalContainer = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(normal);
            var pinnedContainer = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(pinned);
            Assert.IsNotNull(normalContainer);
            Assert.IsNotNull(pinnedContainer);

            var pinnedIntoNormal = NewDragEventArgs(
                new DragPayloadService().Build(pinned),
                DragDrop.PreviewDragOverEvent,
                normalContainer,
                new Point(0, normalContainer.ActualHeight));
            normalContainer.RaiseEvent(pinnedIntoNormal);
            Assert.AreEqual(DragDropEffects.None, pinnedIntoNormal.Effects);
            Assert.AreEqual(Visibility.Collapsed, indicator.Visibility);

            var normalIntoPinned = NewDragEventArgs(
                new DragPayloadService().Build(normal),
                DragDrop.PreviewDragOverEvent,
                pinnedContainer,
                new Point());
            pinnedContainer.RaiseEvent(normalIntoPinned);
            Assert.AreEqual(DragDropEffects.None, normalIntoPinned.Effects);
            Assert.AreEqual(Visibility.Collapsed, indicator.Visibility);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void MixedPinBatch_RejectsListReorderButPartitionsCrossCategoryDrop()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var movingNormal = board.AddText("moving normal");
        var movingPinned = board.AddText("moving pinned");
        board.SetPinnedMany([movingPinned.Id], true);
        var existingNormal = board.AddText("existing normal", BoardCategory.Reference);
        var existingPinned = board.AddText("existing pinned", BoardCategory.Reference);
        board.SetPinnedMany([existingPinned.Id], true);
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(movingPinned);
            list.SelectedItems.Add(movingNormal);
            var data = new DragPayloadService().BuildInternalBatch([movingPinned, movingNormal]);
            var targetContainer = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(movingNormal);
            Assert.IsNotNull(targetContainer);
            var over = NewDragEventArgs(data, DragDrop.PreviewDragOverEvent, targetContainer);

            targetContainer.RaiseEvent(over);

            Assert.AreEqual(DragDropEffects.None, over.Effects);
            Assert.AreEqual(
                Visibility.Collapsed,
                ((Border)window.FindName("InsertionIndicator")).Visibility);
            Assert.AreEqual(0, store.SaveCount);

            var viewModel = (MainWindowViewModel)window.DataContext;
            var referenceTab = FindCategoryTab(
                window,
                viewModel.Categories.Single(panel => panel.Category == BoardCategory.Reference));
            var drop = NewDragEventArgs(data, DragDrop.DropEvent, referenceTab);
            referenceTab.RaiseEvent(drop);
            PumpDispatcherUntil(window.Dispatcher, store.SaveCompleted.Task);
            CompleteLayout(window);

            CollectionAssert.AreEqual(
                new[] { movingPinned.Id, existingPinned.Id, movingNormal.Id, existingNormal.Id },
                board.Items(BoardCategory.Reference).Select(item => item.Id).ToArray());
            Assert.AreEqual(1, store.SaveCount);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void NormalCategoryDrop_WithManyTargetPinsScrollsMovedNormalToViewportTop()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var moving = board.AddText("moving normal");
        _ = Enumerable.Range(0, 10)
            .Select(index => board.AddText(
                $"normal {index}: {new string('x', 120)}",
                BoardCategory.Reference))
            .ToArray();
        var targetPins = Enumerable.Range(0, 16)
            .Select(index => board.AddText(
                $"pin {index}: {new string('x', 120)}",
                BoardCategory.Reference))
            .ToArray();
        board.SetPinnedMany(targetPins.Select(item => item.Id).ToArray(), true);
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var referenceTab = FindCategoryTab(
                window,
                viewModel.Categories.Single(panel => panel.Category == BoardCategory.Reference));
            var drop = NewDragEventArgs(
                new DragPayloadService().Build(moving),
                DragDrop.DropEvent,
                referenceTab);

            referenceTab.RaiseEvent(drop);
            PumpDispatcherUntil(window.Dispatcher, store.SaveCompleted.Task);
            CompleteLayout(window);

            var list = (ListBox)window.FindName("BoardList");
            var viewer = FindDescendant<ScrollViewer>(list);
            var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(moving);
            Assert.IsNotNull(viewer);
            Assert.IsNotNull(container);
            Assert.IsTrue(viewer.VerticalOffset > 0);
            Assert.AreEqual(
                list.Padding.Top,
                container.TranslatePoint(new Point(), list).Y,
                1.0);
        }
        finally
        {
            CloseWindow(window);
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
