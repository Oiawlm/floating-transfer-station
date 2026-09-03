using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;

namespace FloatingTransferStation.Tests;

public sealed partial class MainWindowInteractionTests
{
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
}
