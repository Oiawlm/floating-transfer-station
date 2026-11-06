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
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExternalRail_FromExpandedPanelCanHoverOpenAgain(bool dropOnCategory)
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("外部拖入结束后仍可展开");
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var state = GetPrivateField<PanelStateMachine>(window, "_panelState");
            var tab = FindCategoryTab(window, viewModel.DefaultCapturePanel);
            var data = new DataObject(DataFormats.UnicodeText, "新的合成拖入内容");
            tab.RaiseEvent(NewDragEventArgs(data, DragDrop.DragEnterEvent, tab));
            CompleteLayout(window);

            Assert.IsTrue(viewModel.IsExternalDropRailVisible);
            Assert.IsFalse(state.IsExpanded, "External rail entry must collapse the state machine with the view.");
            if (dropOnCategory)
            {
                tab.RaiseEvent(NewDragEventArgs(data, DragDrop.DropEvent, tab));
                PumpDispatcherUntil(window.Dispatcher, store.SaveCompleted.Task);
            }
            else
            {
                tab.RaiseEvent(NewDragEventArgs(data, DragDrop.DragLeaveEvent, tab));
            }

            CompleteLayout(window);
            Assert.IsFalse(viewModel.IsExternalDropRailVisible);
            Assert.IsFalse(viewModel.IsPanelExpanded);
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            Assert.IsTrue(state.IsExpanded);
            Assert.IsTrue(viewModel.IsPanelExpanded);
            Assert.AreEqual(WindowSettings.Default.PanelWidth + WindowSettings.TabWidth, window.Width);
            Assert.AreEqual(BoardCategory.Inbox, viewModel.ActivePanel!.Category);
            InvokePrivate(window, "StopPanelContentAnimation");
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "external-rail-hover-reopened.png",
                "FTS_AUDIT_REMEDIATION_EVIDENCE_DIR");
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
            DpiGeometryAssert.AreEqual(WindowSettings.TabWidth, window.Width, window, PixelQuantization.Single, "轨道态窗口宽＝标签宽。");
            DpiGeometryAssert.AreEqual(WindowSettings.Default.WindowHeight, window.Height, window, PixelQuantization.Single, "轨道态窗口高。");
            DpiGeometryAssert.AreEqual(
                SystemParameters.WorkArea.Top + WindowSettings.Default.Top,
                window.Top,
                window,
                PixelQuantization.Single,
                "轨道态窗口顶＝工作区顶＋默认偏移。");
            DpiGeometryAssert.AreEqualScreen(collapsedBounds.X, railBounds.X, PixelQuantization.ScreenPair, "进入轨道后默认标签屏幕 X 恒定。");
            DpiGeometryAssert.AreEqualScreen(collapsedBounds.Y, railBounds.Y, PixelQuantization.ScreenPair, "进入轨道后默认标签屏幕 Y 恒定。");
            DpiGeometryAssert.AreEqual(collapsedBounds.Width, railBounds.Width, window, PixelQuantization.Pair, "进入轨道后默认标签宽恒定（两次独立测量）。");
            DpiGeometryAssert.AreEqual(collapsedBounds.Height, railBounds.Height, window, PixelQuantization.Pair, "进入轨道后默认标签高恒定（两次独立测量）。");
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

            foreach (var category in viewModel.Categories.Where(
                         candidate => !candidate.IsDefaultCapture &&
                             BoardCategoryCatalog.IsContentBucket(candidate.Category)))
            {
                var next = FindCategoryTab(window, category);
                current.RaiseEvent(NewDragEventArgs(data, DragDrop.DragLeaveEvent, current));

                Assert.IsTrue(viewModel.IsExternalDropRailVisible);
                DpiGeometryAssert.AreEqual(WindowSettings.Default.WindowHeight, window.Height, window, PixelQuantization.Single, "轨道态窗口高（跨标签迁移中保持）。");
                // 契约 #51：标签高亮的撤离清理由保持窗复核承接（同步断言点后移到
                // 输入通道排空之后）；轨道本身的跨标签和解仍是输入优先级延迟复核。
                CompleteLayout(window);
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

            // 复盘与启动标签对内容拖入一律拒绝（#30 排除面）：Effects.None 且不设投放目标。
            foreach (var category in viewModel.Categories.Where(
                         candidate => !BoardCategoryCatalog.IsContentBucket(candidate.Category)))
            {
                var rejectedTab = FindCategoryTab(window, category);
                var rejectedEnter = NewDragEventArgs(data, DragDrop.DragEnterEvent, rejectedTab);
                rejectedTab.RaiseEvent(rejectedEnter);
                CompleteLayout(window);

                Assert.AreEqual(DragDropEffects.None, rejectedEnter.Effects);
                Assert.AreNotSame(
                    category,
                    viewModel.Categories.SingleOrDefault(candidate => candidate.IsDropTarget));
            }

            current.RaiseEvent(NewDragEventArgs(data, DragDrop.DragLeaveEvent, current));
            CompleteLayout(window);

            var rowHeight = WindowSettings.Default.WindowHeight /
                BoardCategoryCatalog.Ordered.Count;
            Assert.IsFalse(viewModel.IsExternalDropRailVisible);
            Assert.IsFalse(viewModel.Categories.Any(category => category.IsDropTarget));
            Assert.AreEqual(Visibility.Visible, collapsedHandle.Visibility);
            DpiGeometryAssert.AreEqual(WindowSettings.TabWidth, window.Width, window, PixelQuantization.Single, "轨道回落收起宽。");
            DpiGeometryAssert.AreEqual(rowHeight, window.Height, window, PixelQuantization.Single, "轨道回落收起高＝单行高。");
            DpiGeometryAssert.AreEqual(
                SystemParameters.WorkArea.Top + WindowSettings.Default.Top + (3 * rowHeight),
                window.Top,
                window,
                PixelQuantization.Single,
                "轨道回落收起顶＝工作区顶＋默认偏移＋行偏移。");
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
            DpiGeometryAssert.AreEqual(WindowSettings.Default.WindowHeight, window.Height, window, PixelQuantization.Single, "轨道态窗口高（间隙保持）。");

            var gapLeave = NewDragEventArgs(data, DragDrop.DragLeaveEvent, gapSurface);
            gapSurface.RaiseEvent(gapLeave);
            CompleteLayout(window);

            Assert.IsTrue(gapLeave.Handled);
            Assert.IsFalse(viewModel.IsExternalDropRailVisible);
            DpiGeometryAssert.AreEqual(
                WindowSettings.Default.WindowHeight / BoardCategoryCatalog.Ordered.Count,
                window.Height,
                window,
                PixelQuantization.Single,
                "间隙离开回落收起高＝单行高。");
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
            DpiGeometryAssert.AreEqual(expandedWidth, window.Width, window, PixelQuantization.Pair, "内部拖入不改变展开宽（两次独立测量）。");

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
            DpiGeometryAssert.AreEqual(WindowSettings.TabWidth, window.Width, window, PixelQuantization.Single, "拒绝后回落收起宽。");
            DpiGeometryAssert.AreEqual(
                WindowSettings.Default.WindowHeight / BoardCategoryCatalog.Ordered.Count,
                window.Height,
                window,
                PixelQuantization.Single,
                "拒绝后回落收起高＝单行高。");
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
            var customer = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);
            var customerTab = FindCategoryTab(window, customer);
            var data = new DataObject();
            data.SetData(DragPayloadService.InternalItemIdFormat, moved.Id.ToString("D"));
            var drop = NewDragEventArgs(data, DragDrop.DropEvent, customerTab);

            customerTab.RaiseEvent(drop);
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
            DpiGeometryAssert.AreEqual(WindowSettings.TabWidth, window.Width, window, PixelQuantization.Single, "收起宽＝标签宽。");
            DpiGeometryAssert.AreEqual(WindowSettings.Default.WindowHeight, window.Height, window, PixelQuantization.Single, "轨道高。");
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
            DpiGeometryAssert.AreEqual(
                WindowSettings.Default.WindowHeight / BoardCategoryCatalog.Ordered.Count,
                window.Height,
                window,
                PixelQuantization.Single,
                "不支持载荷保持收起高＝单行高。");

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
            DpiGeometryAssert.AreEqual(
                WindowSettings.Default.WindowHeight / BoardCategoryCatalog.Ordered.Count,
                window.Height,
                window,
                PixelQuantization.Single,
                "内部载荷保持收起高＝单行高。");
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
        state.Set(BoardCategory.Inbox);
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
            var customer = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);
            var customerTab = FindCategoryTab(window, customer);
            customerTab.RaiseEvent(NewDragEventArgs(data, DragDrop.DragOverEvent, customerTab));
            var drop = NewDragEventArgs(data, DragDrop.DropEvent, customerTab);

            customerTab.RaiseEvent(drop);
            PumpDispatcherUntil(window.Dispatcher, store.SaveCompleted.Task);
            CompleteLayout(window);

            Assert.AreEqual(DragDropEffects.Copy, drop.Effects);
            Assert.IsTrue(drop.Handled);
            Assert.AreEqual(BoardCategory.Inbox, state.Current);
            Assert.AreEqual("只进入提示词", board.Items(BoardCategory.CustomerOriginal).Single().Text);
            Assert.AreEqual(0, board.Items(BoardCategory.Inbox).Count);
            Assert.IsFalse(viewModel.IsExternalDropRailVisible);
            Assert.IsFalse(viewModel.IsPanelExpanded);
            DpiGeometryAssert.AreEqual(
                WindowSettings.Default.WindowHeight / BoardCategoryCatalog.Ordered.Count,
                window.Height,
                window,
                PixelQuantization.Single,
                "提示词槽位收起高＝单行高。");
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
            var customer = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);
            var customerTab = FindCategoryTab(window, customer);
            customerTab.RaiseEvent(NewDragEventArgs(data, DragDrop.DropEvent, customerTab));
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
            DpiGeometryAssert.AreEqual(WindowSettings.TabWidth, window.Width, window, PixelQuantization.Single, "导入失败回落收起宽。");
            DpiGeometryAssert.AreEqual(
                WindowSettings.Default.WindowHeight / BoardCategoryCatalog.Ordered.Count,
                window.Height,
                window,
                PixelQuantization.Single,
                "导入失败回落收起高＝单行高。");
            Assert.AreEqual(0, board.Items(BoardCategory.CustomerOriginal).Count);

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
            var customer = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);
            var customerTab = FindCategoryTab(window, customer);
            var rejected = NewDragEventArgs(
                new DataObject("unsupported/custom", new object()),
                DragDrop.DropEvent,
                customerTab);

            customerTab.RaiseEvent(rejected);
            CompleteLayout(window);

            Assert.AreEqual(DragDropEffects.None, rejected.Effects);
            Assert.IsTrue(rejected.Handled);
            Assert.IsFalse(viewModel.IsExternalDropRailVisible);
            Assert.IsFalse(viewModel.Categories.Any(category => category.IsDropTarget));
            Assert.AreEqual(0, store.SaveCount);
            Assert.AreEqual(0, board.Items(BoardCategory.CustomerOriginal).Count);
            DpiGeometryAssert.AreEqual(
                WindowSettings.Default.WindowHeight / BoardCategoryCatalog.Ordered.Count,
                window.Height,
                window,
                PixelQuantization.Single,
                "取消导入后收起高＝单行高。");
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
        var existing = board.AddText("existing", BoardCategory.CustomerOriginal);
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
            var customer = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);
            var customerTab = FindCategoryTab(window, customer);
            var enter = NewDragEventArgs(data, DragDrop.DragEnterEvent, customerTab);
            customerTab.RaiseEvent(enter);
            var drop = NewDragEventArgs(data, DragDrop.DropEvent, customerTab);

            customerTab.RaiseEvent(drop);
            PumpDispatcherUntil(window.Dispatcher, store.SaveCompleted.Task);
            CompleteLayout(window);

            Assert.AreEqual(DragDropEffects.Move, enter.Effects);
            Assert.AreEqual(DragDropEffects.Move, drop.Effects);
            Assert.IsFalse(viewModel.IsExternalDropRailVisible);
            CollectionAssert.AreEqual(
                new[] { moved.Id, existing.Id },
                board.Items(BoardCategory.CustomerOriginal).Select(item => item.Id).ToArray());
            Assert.AreEqual(BoardCategory.CustomerOriginal, viewModel.ActivePanel!.Category);
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
        var target = board.AddText("target", BoardCategory.CustomerOriginal);
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
            var customerTab = FindCategoryTab(
                window,
                viewModel.Categories.Single(panel => panel.Category == BoardCategory.CustomerOriginal));
            var drop = NewDragEventArgs(data, DragDrop.DropEvent, customerTab);

            customerTab.RaiseEvent(drop);
            CompleteLayout(window);

            Assert.AreEqual(DragDropEffects.None, drop.Effects);
            CollectionAssert.AreEqual(
                new[] { top.Id, bottom.Id },
                board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
            CollectionAssert.AreEqual(
                new[] { target.Id },
                board.Items(BoardCategory.CustomerOriginal).Select(item => item.Id).ToArray());
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
        board.AddText("target", BoardCategory.CustomerOriginal);
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
            var customerTab = FindCategoryTab(
                window,
                viewModel.Categories.Single(panel => panel.Category == BoardCategory.CustomerOriginal));
            var drop = NewDragEventArgs(data, DragDrop.DropEvent, customerTab);

            customerTab.RaiseEvent(drop);
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

    // ---- 图片拖出为文件（契约 #58，实验性默认关）：BuildInternalDragData 反射
    // 直调先例（上方两条）——窗内拖出经门控单点取两态载荷形状，UI 事件层零
    // 开关感知（开关字段只经偏好下发进服务）。----

    [STATestMethod]
    public void InternalDragData_DisabledImageDragKeepsInternalIdWithoutFileDrop()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "board-image.png");
        using (var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(
            2, 2, SixLabors.ImageSharp.Color.Green))
        {
            SixLabors.ImageSharp.ImageExtensions.SaveAsPng(image, path);
        }

        var board = new BoardService();
        var item = board.AddImage(Guid.NewGuid(), "images/board-image.png", path);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var method = GetPrivateMethod("BuildInternalDragData");
            Assert.IsNotNull(method);

            var data = method.Invoke(window, [item]) as IDataObject;

            Assert.IsNotNull(data);
            Assert.AreEqual(item.Id, new DragPayloadService().GetInternalItemId(data));
            Assert.IsFalse(data.GetDataPresent(DataFormats.FileDrop), "默认关＝窗内图片拖出不携带文件组。");
            Assert.IsFalse(data.GetDataPresent(DataFormats.Bitmap), "默认关＝窗内图片拖出不携带位图。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void InternalDragData_EnabledImageDragCarriesFileDropList()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "board-image.png");
        using (var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(
            2, 2, SixLabors.ImageSharp.Color.Green))
        {
            SixLabors.ImageSharp.ImageExtensions.SaveAsPng(image, path);
        }

        var board = new BoardService();
        var item = board.AddImage(Guid.NewGuid(), "images/board-image.png", path);
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(
            board,
            store,
            WindowSettings.Default,
            new DefaultCaptureCategoryState(),
            new ImageNormalizer(store.ImagesDirectory),
            preferences: AppPreferences.Default with { DragOutAsFileEnabled = true });

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var method = GetPrivateMethod("BuildInternalDragData");
            Assert.IsNotNull(method);

            var data = method.Invoke(window, [item]) as IDataObject;

            Assert.IsNotNull(data);
            Assert.AreEqual(item.Id, new DragPayloadService().GetInternalItemId(data));
            CollectionAssert.AreEqual(
                new[] { path },
                ((DataObject)data).GetFileDropList().Cast<string>().ToArray(),
                "开启＝窗内图片拖出携带文件组（既有能力恢复）。");
            Assert.IsTrue(data.GetDataPresent(DataFormats.Bitmap));
            Assert.IsTrue(
                data.GetDataPresent(DragPayloadService.PreferredDropEffectFormat),
                "开启＝附 Preferred DropEffect(Copy) 声明（#3 精神：防外部按 Move 删受管源）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void InternalDragData_TextDragUnaffectedByDragOutToggle()
    {
        using var directory = new TestDirectory();
        const string text = "拖出开关不影响文字";
        var method = GetPrivateMethod("BuildInternalDragData");
        Assert.IsNotNull(method);

        var offBoard = new BoardService();
        var offItem = offBoard.AddText(text);
        var offWindow = CreateWindow(directory, offBoard);
        try
        {
            offWindow.Show();
            ExpandCategory(offWindow, BoardCategory.Inbox);
            CompleteLayout(offWindow);

            var data = method.Invoke(offWindow, [offItem]) as IDataObject;

            Assert.IsNotNull(data);
            Assert.AreEqual(text, data.GetData(DataFormats.UnicodeText), "文字卡拖出两态不变。");
            Assert.IsFalse(data.GetDataPresent(DataFormats.FileDrop));
            Assert.IsFalse(data.GetDataPresent(DragPayloadService.PreferredDropEffectFormat));
        }
        finally
        {
            CloseWindow(offWindow);
        }

        var enabledStore = new RecordingBoardStore(directory.Root);
        var enabledBoard = new BoardService();
        var enabledItem = enabledBoard.AddText(text);
        var enabledWindow = CreateWindow(
            enabledBoard,
            enabledStore,
            WindowSettings.Default,
            new DefaultCaptureCategoryState(),
            new ImageNormalizer(enabledStore.ImagesDirectory),
            preferences: AppPreferences.Default with { DragOutAsFileEnabled = true });
        try
        {
            enabledWindow.Show();
            ExpandCategory(enabledWindow, BoardCategory.Inbox);
            CompleteLayout(enabledWindow);

            var data = method.Invoke(enabledWindow, [enabledItem]) as IDataObject;

            Assert.IsNotNull(data);
            Assert.AreEqual(text, data.GetData(DataFormats.UnicodeText), "开启态文字卡拖出仍只送文本。");
            Assert.IsFalse(data.GetDataPresent(DataFormats.FileDrop));
            Assert.IsFalse(data.GetDataPresent(DragPayloadService.PreferredDropEffectFormat));
        }
        finally
        {
            CloseWindow(enabledWindow);
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
            var second = viewModel.Categories[3];
            var firstTab = FindCategoryTab(window, first);
            var secondTab = FindCategoryTab(window, second);
            var internalData = new DragPayloadService().Build(item);

            firstTab.RaiseEvent(NewDragEventArgs(internalData, DragDrop.DragEnterEvent, firstTab));
            Assert.AreSame(first, viewModel.Categories.Single(category => category.IsDropTarget));

            secondTab.RaiseEvent(NewDragEventArgs(internalData, DragDrop.DragEnterEvent, secondTab));
            Assert.AreSame(second, viewModel.Categories.Single(category => category.IsDropTarget));

            // 契约 #51：撤离不立即清高亮——抖动窗口内 IsDropTarget 不回落；
            // 真离开经保持窗复核后一次清到位。
            secondTab.RaiseEvent(NewDragEventArgs(internalData, DragDrop.DragLeaveEvent, secondTab));
            Assert.AreSame(second, viewModel.Categories.Single(category => category.IsDropTarget));
            CompleteLayout(window);
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
        // 按显示顺序的反序添加，使显示为 [alpha, beta]；拖 alpha 悬停 beta 下半部
        //（真实移动槽——单卡板的全部槽位均 NoChange，#51 增补后同位不显示）。
        var beta = board.AddText("beta");
        var alpha = board.AddText("alpha");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var indicator = window.FindName("InsertionIndicator") as Border;
            Assert.IsNotNull(indicator);
            var betaContainer =
                (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(beta);
            Assert.IsNotNull(betaContainer);
            var internalData = new DragPayloadService().Build(alpha);

            var valid = NewDragEventArgs(
                internalData,
                DragDrop.PreviewDragOverEvent,
                betaContainer,
                new Point(12, (betaContainer.ActualHeight / 2) + 1));
            betaContainer.RaiseEvent(valid);

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
            // 撤离走保持窗复核（#51 ③同构）：断言时不立即隐藏，通道排空后清到位。
            CompleteLayout(window);
            Assert.AreEqual(Visibility.Collapsed, indicator.Visibility);

            list.RaiseEvent(NewDragEventArgs(
                internalData,
                DragDrop.PreviewDragOverEvent,
                betaContainer,
                new Point(12, (betaContainer.ActualHeight / 2) + 1)));
            list.RaiseEvent(NewDragEventArgs(
                internalData,
                DragDrop.PreviewDragLeaveEvent,
                list));
            CompleteLayout(window);
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
    public void PinnedBoundaryDrag_KeepsIndicatorStableWhileCursorJittersAroundTheRegionLine()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        // AddText 自普通区顶部插入，按显示顺序的反序添加，使显示为
        // [firstPinned, lastPinned, firstNormal, secondNormal]。
        var secondNormal = board.AddText("normal two");
        var firstNormal = board.AddText("normal one");
        var lastPinned = board.AddText("pinned two");
        var firstPinned = board.AddText("pinned one");
        board.SetPinnedMany([firstPinned.Id, lastPinned.Id], true);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var indicator = (Border)window.FindName("InsertionIndicator");
            var boundaryContainer =
                (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(lastPinned);
            Assert.IsNotNull(boundaryContainer);
            var expectedEdgeY = boundaryContainer.TranslatePoint(
                new Point(0, boundaryContainer.ActualHeight),
                list).Y;
            var dragData = new DragPayloadService().Build(secondNormal);
            var boundaryMidY = boundaryContainer.ActualHeight / 2;

            // 在置顶边界中线两侧 ±1 DIP 来回抖动：指示条必须持续可见、
            // 位置稳定停在钳制槽（第一个普通位），拖动效果保持 Move。
            for (var jitter = 0; jitter < 6; jitter++)
            {
                var cursorY = boundaryMidY + (jitter % 2 == 0 ? -1d : 1d);
                var over = NewDragEventArgs(
                    dragData,
                    DragDrop.PreviewDragOverEvent,
                    boundaryContainer,
                    new Point(8, cursorY));
                boundaryContainer.RaiseEvent(over);

                Assert.AreEqual(DragDropEffects.Move, over.Effects, $"jitter {jitter}");
                Assert.AreEqual(Visibility.Visible, indicator.Visibility, $"jitter {jitter}");
                Assert.AreEqual(expectedEdgeY, Canvas.GetTop(indicator), 0.5, $"jitter {jitter}");
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void NormalCardMidpointDrag_KeepsIndicatorStableWhileCursorJittersAroundTheCenterLine()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        // 按显示顺序的反序添加，使显示为 [top, middle, bottom]（三张普通卡）。
        // #51 增补（2026-11-03）：两卡时 bottom 中线两侧含 NoChange 槽（拖 top 时
        // 槽 1＝原位，同位不显示），中线迟滞用三卡把两侧都落在真实移动上——
        // 被拖卡自身 NoChange 区的显示语义由 SamePositionDrag_* 锁定。
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
            var indicator = (Border)window.FindName("InsertionIndicator");
            var bottomContainer =
                (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(bottom);
            Assert.IsNotNull(bottomContainer);
            var topEdgeY = bottomContainer.TranslatePoint(new Point(), list).Y;
            var bottomEdgeY = bottomContainer.TranslatePoint(
                new Point(0, bottomContainer.ActualHeight),
                list).Y;
            var dragData = new DragPayloadService().Build(top);
            var midY = bottomContainer.ActualHeight / 2;
            var hysteresis = InsertionSlotResolver.SlotFlipHysteresisDip;

            // 首次判定落在中线之上：槽位＝bottom 之前，指示线贴 bottom 上缘。
            var initial = NewDragEventArgs(
                dragData,
                DragDrop.PreviewDragOverEvent,
                bottomContainer,
                new Point(8, midY - 1));
            bottomContainer.RaiseEvent(initial);
            Assert.AreEqual(DragDropEffects.Move, initial.Effects);
            Assert.AreEqual(Visibility.Visible, indicator.Visibility);
            Assert.AreEqual(topEdgeY, Canvas.GetTop(indicator), 0.5);

            // 中线两侧 ±1–2 DIP 来回抖动：指示线钉在首次判定的槽位边缘，
            // 不随翻转边界跳变；可见性与拖动效果全程稳定（显示/隐藏不交替）。
            for (var jitter = 0; jitter < 8; jitter++)
            {
                var amplitude = jitter % 2 == 0 ? 1d : 2d;
                var cursorY = midY + (jitter % 4 < 2 ? amplitude : -amplitude);
                var over = NewDragEventArgs(
                    dragData,
                    DragDrop.PreviewDragOverEvent,
                    bottomContainer,
                    new Point(8, cursorY));
                bottomContainer.RaiseEvent(over);

                Assert.AreEqual(DragDropEffects.Move, over.Effects, $"jitter {jitter}");
                Assert.AreEqual(Visibility.Visible, indicator.Visibility, $"jitter {jitter}");
                Assert.AreEqual(topEdgeY, Canvas.GetTop(indicator), 0.5, $"jitter {jitter}");
            }

            // 果断越过中线超出迟滞半径：翻转即时跟随到末尾槽（迟滞不是锁死）。
            var decisive = NewDragEventArgs(
                dragData,
                DragDrop.PreviewDragOverEvent,
                bottomContainer,
                new Point(8, midY + hysteresis + 2));
            bottomContainer.RaiseEvent(decisive);
            Assert.AreEqual(DragDropEffects.Move, decisive.Effects);
            Assert.AreEqual(Visibility.Visible, indicator.Visibility);
            Assert.AreEqual(bottomEdgeY, Canvas.GetTop(indicator), 0.5);

            // 翻转后的中线附近小幅回抖：对称维持新槽位。
            var backJitter = NewDragEventArgs(
                dragData,
                DragDrop.PreviewDragOverEvent,
                bottomContainer,
                new Point(8, midY + 2));
            bottomContainer.RaiseEvent(backJitter);
            Assert.AreEqual(DragDropEffects.Move, backJitter.Effects);
            Assert.AreEqual(bottomEdgeY, Canvas.GetTop(indicator), 0.5);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void CategoryDropFeedback_JitteredEnterOverLeaveSequenceKeepsTargetWithoutFalseDip(
        bool clientAreaAnimations)
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("drag source");
        var window = CreateWindow(directory, board);
        // 两档都覆盖：动效开启（差分＝零重启动画）与系统「减弱动效」（直接达终态，
        // 契约 #41——迟滞与差分在减动效下同样正确）。
        window.Resources[SystemParameters.ClientAreaAnimationKey] = clientAreaAnimations;

        try
        {
            window.Show();
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var target = viewModel.Categories[0];
            var tab = FindCategoryTab(window, target);
            var rail = (Border)window.FindName("CategoryRail");
            var gapSurface = rail.Child as ItemsControl;
            Assert.IsNotNull(gapSurface);
            var internalData = new DragPayloadService().Build(item);

            tab.RaiseEvent(NewDragEventArgs(internalData, DragDrop.DragEnterEvent, tab));
            Assert.AreSame(target, viewModel.Categories.Single(category => category.IsDropTarget));

            // DragEnter/Over/Leave 抖动序列（标签边缘手抖、间隙瞬时穿越）：
            // IsDropTarget 不得出现 false 回落——撤离只进入保持窗复核，断言事件
            // 立即差分生效并作废挂起复核；稳态序列零属性翻转、零动画重启（契约 #51）。
            for (var round = 0; round < 4; round++)
            {
                tab.RaiseEvent(NewDragEventArgs(internalData, DragDrop.DragLeaveEvent, tab));
                Assert.AreSame(
                    target,
                    viewModel.Categories.Single(category => category.IsDropTarget),
                    $"leave {round}");

                tab.RaiseEvent(NewDragEventArgs(internalData, DragDrop.DragOverEvent, tab));
                Assert.AreSame(
                    target,
                    viewModel.Categories.Single(category => category.IsDropTarget),
                    $"over {round}");

                gapSurface.RaiseEvent(NewDragEventArgs(
                    internalData,
                    DragDrop.DragEnterEvent,
                    gapSurface));
                Assert.AreSame(
                    target,
                    viewModel.Categories.Single(category => category.IsDropTarget),
                    $"gap {round}");

                tab.RaiseEvent(NewDragEventArgs(internalData, DragDrop.DragEnterEvent, tab));
                Assert.AreSame(
                    target,
                    viewModel.Categories.Single(category => category.IsDropTarget),
                    $"re-enter {round}");
            }

            // 真离开（末次事件为撤离且保持窗内无再断言）：复核期满后一次清到位，
            // 撤离语义与既有一致——不闪，但也不会残留。
            tab.RaiseEvent(NewDragEventArgs(internalData, DragDrop.DragLeaveEvent, tab));
            Assert.AreSame(target, viewModel.Categories.Single(category => category.IsDropTarget));
            CompleteLayout(window);
            Assert.IsFalse(viewModel.Categories.Any(category => category.IsDropTarget));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void MidpointJitterDrop_LandsAtTheHeldSlotShownByTheIndicator()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        // 按显示顺序的反序添加，使显示为 [top, middle, bottom]（三张普通卡）。
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
            var indicator = (Border)window.FindName("InsertionIndicator");
            var bottomContainer =
                (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(bottom);
            Assert.IsNotNull(bottomContainer);
            var bottomTopEdgeY = bottomContainer.TranslatePoint(new Point(), list).Y;
            var dragData = new DragPayloadService().Build(top);
            var midY = bottomContainer.ActualHeight / 2;

            // 建立迟滞真值：中线之上判定 held=2（bottom 之前，真实移动）。
            var over = NewDragEventArgs(
                dragData,
                DragDrop.PreviewDragOverEvent,
                bottomContainer,
                new Point(8, midY - 1));
            bottomContainer.RaiseEvent(over);
            Assert.AreEqual(DragDropEffects.Move, over.Effects);
            Assert.AreEqual(bottomTopEdgeY, Canvas.GetTop(indicator), 0.5);

            // 迟滞带内松手（raw=3 末尾槽、held=2）：所见即所落（契约 #51）——
            // 落点必须是指示线所示 held 槽（[middle, top, bottom]），
            // 而不是无迟滞解析的 raw 槽（那会得到 [middle, bottom, top]）。
            var drop = NewDragEventArgs(
                dragData,
                DragDrop.PreviewDropEvent,
                bottomContainer,
                new Point(8, midY + 2));
            bottomContainer.RaiseEvent(drop);
            PumpDispatcherUntil(window.Dispatcher, store.SaveCompleted.Task);
            CompleteLayout(window);

            Assert.AreEqual(DragDropEffects.Move, drop.Effects);
            Assert.AreEqual(1, store.SaveCount);
            CollectionAssert.AreEqual(
                new[] { middle.Id, top.Id, bottom.Id },
                board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
            Assert.AreEqual(Visibility.Collapsed, indicator.Visibility);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void SamePositionDrag_HidesIndicatorAcrossEntireNoChangeZone()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        // 用户场景（2026-11-03 需求 4）：显示 [above, dragged, below]，拖 dragged——
        // 从上一张下半部到下一张上缘的整段悬停，MoveMany 判定均为 NoChange
        //（落点＝原位），指示线必须整段不显示（契约 #51 增补）。
        var below = board.AddText("below");
        var dragged = board.AddText("dragged");
        var above = board.AddText("above");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var indicator = (Border)window.FindName("InsertionIndicator");
            var aboveContainer =
                (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(above);
            var draggedContainer =
                (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(dragged);
            var belowContainer =
                (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(below);
            Assert.IsNotNull(aboveContainer);
            Assert.IsNotNull(draggedContainer);
            Assert.IsNotNull(belowContainer);
            var aboveMid = aboveContainer.ActualHeight / 2;
            var draggedMid = draggedContainer.ActualHeight / 2;
            var aboveTopEdgeY = aboveContainer.TranslatePoint(new Point(), list).Y;
            var dragData = new DragPayloadService().Build(dragged);

            // NoChange 区内逐点悬停：指示线全程不可见，拖动效果保持 Move
            //（同位松手仍是被允许的放置，只是无视觉反馈）。
            var zoneHovers = new (ListBoxItem container, double y, string label)[]
            {
                (aboveContainer, aboveMid + 4, "上一张下半部"),
                (aboveContainer, aboveContainer.ActualHeight - 2, "上一张底部"),
                (draggedContainer, 2, "自身上缘"),
                (draggedContainer, draggedMid - 1, "自身中线之上"),
                (draggedContainer, draggedMid + 8, "自身中线之下（越过迟滞带）"),
                (belowContainer, 2, "下一张上缘"),
            };
            foreach (var (container, y, label) in zoneHovers)
            {
                var over = NewDragEventArgs(
                    dragData,
                    DragDrop.PreviewDragOverEvent,
                    container,
                    new Point(8, y));
                container.RaiseEvent(over);

                Assert.AreEqual(DragDropEffects.Move, over.Effects, label);
                Assert.AreEqual(Visibility.Collapsed, indicator.Visibility, label);
                Assert.IsFalse(FadeAnimation.GetIsActive(indicator), label);
            }

            // 果断越出 NoChange 区（上一张中线之上）：真实移动，指示线出现并贴上一张上缘。
            var outOfZone = NewDragEventArgs(
                dragData,
                DragDrop.PreviewDragOverEvent,
                aboveContainer,
                new Point(8, aboveMid - 8));
            aboveContainer.RaiseEvent(outOfZone);
            Assert.AreEqual(DragDropEffects.Move, outOfZone.Effects);
            Assert.AreEqual(Visibility.Visible, indicator.Visibility);
            Assert.AreEqual(aboveTopEdgeY, Canvas.GetTop(indicator), 0.5);

            // 回到 NoChange 区（上一张中线之下果断越过）：指示线重新隐藏。
            var backIntoZone = NewDragEventArgs(
                dragData,
                DragDrop.PreviewDragOverEvent,
                aboveContainer,
                new Point(8, aboveMid + 8));
            aboveContainer.RaiseEvent(backIntoZone);
            Assert.AreEqual(DragDropEffects.Move, backIntoZone.Effects);
            Assert.AreEqual(Visibility.Collapsed, indicator.Visibility);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void SamePositionDrag_ZoneBoundaryJitterKeepsDisplayStable()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        // 显示 [above, dragged, below]，拖 dragged：above 中线＝NoChange 区上边界
        //（其上＝真实移动显示，其下＝同位隐藏）——同位/异位切换边界必须纳入迟滞
        //（契约 #51 增补）：边界 ±1–2 DIP 抖动不产生显示状态交替。
        var below = board.AddText("below");
        var dragged = board.AddText("dragged");
        var above = board.AddText("above");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var indicator = (Border)window.FindName("InsertionIndicator");
            var aboveContainer =
                (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(above);
            Assert.IsNotNull(aboveContainer);
            var aboveMid = aboveContainer.ActualHeight / 2;
            var dragData = new DragPayloadService().Build(dragged);

            // 建立异位真值：above 中线之上果断悬停——真实移动，显示。
            aboveContainer.RaiseEvent(NewDragEventArgs(
                dragData,
                DragDrop.PreviewDragOverEvent,
                aboveContainer,
                new Point(8, aboveMid - 8)));
            Assert.AreEqual(Visibility.Visible, indicator.Visibility);

            // 边界两侧 ±1–2 DIP 抖动（raw 槽位跨过 above 中线）：held 槽位钉在
            // 异位侧，显示不闪不换。
            for (var jitter = 0; jitter < 6; jitter++)
            {
                var offset = jitter % 2 == 0 ? -2d : 1d;
                aboveContainer.RaiseEvent(NewDragEventArgs(
                    dragData,
                    DragDrop.PreviewDragOverEvent,
                    aboveContainer,
                    new Point(8, aboveMid + offset)));
                Assert.AreEqual(
                    Visibility.Visible,
                    indicator.Visibility,
                    $"jitter {jitter}（{offset:+0;-0} DIP）");
            }

            // 果断越入 NoChange 区：单次干净切换到隐藏；再抖动不回闪。
            aboveContainer.RaiseEvent(NewDragEventArgs(
                dragData,
                DragDrop.PreviewDragOverEvent,
                aboveContainer,
                new Point(8, aboveMid + 8)));
            Assert.AreEqual(Visibility.Collapsed, indicator.Visibility);
            for (var jitter = 0; jitter < 4; jitter++)
            {
                var offset = jitter % 2 == 0 ? 1d : -2d;
                aboveContainer.RaiseEvent(NewDragEventArgs(
                    dragData,
                    DragDrop.PreviewDragOverEvent,
                    aboveContainer,
                    new Point(8, aboveMid + offset)));
                Assert.AreEqual(
                    Visibility.Collapsed,
                    indicator.Visibility,
                    $"zone jitter {jitter}（{offset:+0;-0} DIP）");
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void SamePositionDrag_DropInZoneKeepsOrderAndSkipsSave()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        // 显示 [above, dragged, below]；同位区松手：MoveMany→NoChange——顺序不变、
        // 选择恢复、不落盘；指示线全程不可见（Drop 语义零变化，#51 增补只改视觉）。
        var below = board.AddText("below");
        var dragged = board.AddText("dragged");
        var above = board.AddText("above");
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var indicator = (Border)window.FindName("InsertionIndicator");
            var aboveContainer =
                (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(above);
            Assert.IsNotNull(aboveContainer);
            var aboveMid = aboveContainer.ActualHeight / 2;
            var dragData = new DragPayloadService().Build(dragged);
            list.SelectedItems.Add(dragged);

            var over = NewDragEventArgs(
                dragData,
                DragDrop.PreviewDragOverEvent,
                aboveContainer,
                new Point(8, aboveMid + 4));
            aboveContainer.RaiseEvent(over);
            Assert.AreEqual(DragDropEffects.Move, over.Effects);
            Assert.AreEqual(Visibility.Collapsed, indicator.Visibility);

            var drop = NewDragEventArgs(
                dragData,
                DragDrop.PreviewDropEvent,
                aboveContainer,
                new Point(8, aboveMid + 4));
            aboveContainer.RaiseEvent(drop);
            CompleteLayout(window);

            Assert.AreEqual(DragDropEffects.Move, drop.Effects);
            CollectionAssert.AreEqual(
                new[] { above.Id, dragged.Id, below.Id },
                board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
            CollectionAssert.AreEqual(
                new[] { dragged },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            Assert.AreEqual(0, store.SaveCount);
            Assert.AreEqual(Visibility.Collapsed, indicator.Visibility);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void InsertionIndicatorWithdraw_LeaveEnterChurnKeepsLineWithoutFalsePulse()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        // 显示 [above, dragged]，拖 dragged 悬停 above 上缘＝真实移动（显示）；
        // DragLeave→DragOver 边缘往返（列表边缘手抖）：撤离走 120ms 保持窗复核
        //（契约 #51 ③同构）——窗内指示线持续可见、IsActive 不翻转（零明灭脉冲），
        // 真离开复核期满一次清到位。
        var dragged = board.AddText("dragged");
        var above = board.AddText("above");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var indicator = (Border)window.FindName("InsertionIndicator");
            var aboveContainer =
                (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(above);
            Assert.IsNotNull(aboveContainer);
            var dragData = new DragPayloadService().Build(dragged);

            void HoverAbove(double y) => aboveContainer.RaiseEvent(NewDragEventArgs(
                dragData,
                DragDrop.PreviewDragOverEvent,
                aboveContainer,
                new Point(8, y)));

            HoverAbove(4);
            Assert.AreEqual(Visibility.Visible, indicator.Visibility);
            Assert.IsTrue(FadeAnimation.GetIsActive(indicator));

            // 边缘往返抖动：每轮 Leave 后泵 30ms（<120ms 保持窗）再 Over 回来。
            for (var round = 0; round < 3; round++)
            {
                list.RaiseEvent(NewDragEventArgs(
                    dragData,
                    DragDrop.PreviewDragLeaveEvent,
                    list));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(30));
                Assert.AreEqual(Visibility.Visible, indicator.Visibility, $"leave {round}");
                Assert.IsTrue(FadeAnimation.GetIsActive(indicator), $"leave {round}");

                HoverAbove(4);
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(30));
                Assert.AreEqual(Visibility.Visible, indicator.Visibility, $"re-enter {round}");
                Assert.IsTrue(FadeAnimation.GetIsActive(indicator), $"re-enter {round}");
            }

            // 真离开（保持窗内无再断言）：复核期满一次清到位，不残留。
            list.RaiseEvent(NewDragEventArgs(
                dragData,
                DragDrop.PreviewDragLeaveEvent,
                list));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(250));
            Assert.AreEqual(Visibility.Collapsed, indicator.Visibility);
            Assert.IsFalse(FadeAnimation.GetIsActive(indicator));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void BoardListBlankHover_ResolvesInsertionGeometricallyWithoutJumpingToTheEnd()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        // 按显示顺序的反序添加，使显示为 [top, second, third, bottom]；拖 second——
        // 原两卡板的全部槽位、以及「拖首/尾卡」时首项上方或中部空白均落 NoChange 槽
        //（#51 增补后同位不显示）；四卡＋拖第 2 张使两处空白悬停（槽 0 与槽 3）都是
        // 真实移动，几何解析断言不变。
        var bottom = board.AddText("bottom");
        var third = board.AddText("third");
        var second = board.AddText("second");
        var top = board.AddText("top");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var indicator = (Border)window.FindName("InsertionIndicator");
            var topContainer = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(top);
            var bottomContainer =
                (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(bottom);
            Assert.IsNotNull(topContainer);
            Assert.IsNotNull(bottomContainer);
            var topEdgeY = topContainer.TranslatePoint(new Point(), list).Y;
            var bottomTopY = bottomContainer.TranslatePoint(new Point(), list).Y;
            var dragData = new DragPayloadService().Build(second);

            // 光标落在列表顶部 Padding（不属任何卡片子树）：槽位必须是首项上方，
            // 而不是回退跳到列表末尾。
            var paddingHover = NewDragEventArgs(
                dragData,
                DragDrop.PreviewDragOverEvent,
                list,
                new Point(12, 2));
            list.RaiseEvent(paddingHover);

            Assert.AreEqual(DragDropEffects.Move, paddingHover.Effects);
            Assert.AreEqual(Visibility.Visible, indicator.Visibility);
            Assert.AreEqual(topEdgeY, Canvas.GetTop(indicator), 0.5);

            // 光标悬停在列表中部空白（如竖直滚动条所在列、命中目标不是卡片）：
            // 槽位跟随光标几何，落在下张卡片上方，而不是跳到末尾。
            var midListHover = NewDragEventArgs(
                dragData,
                DragDrop.PreviewDragOverEvent,
                list,
                new Point(12, bottomTopY + bottomContainer.ActualHeight / 2 - 1));
            list.RaiseEvent(midListHover);

            Assert.AreEqual(DragDropEffects.Move, midListHover.Effects);
            Assert.AreEqual(Visibility.Visible, indicator.Visibility);
            Assert.AreEqual(bottomTopY, Canvas.GetTop(indicator), 0.5);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void PinnedDrag_CrossRegionShowsClampedInsertionSlotInBothDirections()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        // 显示 [pinnedOne, pinnedTwo, normalOne, normalTwo]（#51 增补后单置顶/单普通
        // 板的钳制槽即原位＝NoChange 不显示；两置顶＋两普通使两侧钳制槽都是真实移动）。
        var normalTwo = board.AddText("normal two");
        var normalOne = board.AddText("normal one");
        var pinnedTwo = board.AddText("pinned two");
        var pinnedOne = board.AddText("pinned one");
        board.SetPinnedMany([pinnedOne.Id, pinnedTwo.Id], true);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var indicator = (Border)window.FindName("InsertionIndicator");
            var normalTwoContainer =
                (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(normalTwo);
            var pinnedOneContainer =
                (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(pinnedOne);
            var pinnedTwoContainer =
                (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(pinnedTwo);
            Assert.IsNotNull(normalTwoContainer);
            Assert.IsNotNull(pinnedOneContainer);
            Assert.IsNotNull(pinnedTwoContainer);
            var regionEdgeY = pinnedTwoContainer.TranslatePoint(
                new Point(0, pinnedTwoContainer.ActualHeight),
                list).Y;

            // 置顶项（pinnedOne）拖进普通区下缘：钳制回 pinnedCount 槽＝pinnedTwo
            // 之后——真实移动，指示条停在分区边界。
            var pinnedIntoNormal = NewDragEventArgs(
                new DragPayloadService().Build(pinnedOne),
                DragDrop.PreviewDragOverEvent,
                normalTwoContainer,
                new Point(0, normalTwoContainer.ActualHeight));
            normalTwoContainer.RaiseEvent(pinnedIntoNormal);
            Assert.AreEqual(DragDropEffects.Move, pinnedIntoNormal.Effects);
            Assert.AreEqual(Visibility.Visible, indicator.Visibility);
            Assert.AreEqual(regionEdgeY, Canvas.GetTop(indicator), 0.5);

            // 普通项（normalTwo）拖进置顶区上缘：钳制到 pinnedCount 槽＝normalOne
            // 之前——真实移动，同样停在分区边界。
            var normalIntoPinned = NewDragEventArgs(
                new DragPayloadService().Build(normalTwo),
                DragDrop.PreviewDragOverEvent,
                pinnedOneContainer,
                new Point());
            pinnedOneContainer.RaiseEvent(normalIntoPinned);
            Assert.AreEqual(DragDropEffects.Move, normalIntoPinned.Effects);
            Assert.AreEqual(Visibility.Visible, indicator.Visibility);
            Assert.AreEqual(regionEdgeY, Canvas.GetTop(indicator), 0.5);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void PinnedBoundaryDrag_DropsAtTheClampedIndicatorSlot()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        // 按显示顺序的反序添加，使显示为 [firstPinned, lastPinned, firstNormal, secondNormal]。
        var secondNormal = board.AddText("normal two");
        var firstNormal = board.AddText("normal one");
        var lastPinned = board.AddText("pinned two");
        var firstPinned = board.AddText("pinned one");
        board.SetPinnedMany([firstPinned.Id, lastPinned.Id], true);
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var indicator = (Border)window.FindName("InsertionIndicator");
            var boundaryContainer =
                (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(lastPinned);
            Assert.IsNotNull(boundaryContainer);
            var expectedEdgeY = boundaryContainer.TranslatePoint(
                new Point(0, boundaryContainer.ActualHeight),
                list).Y;
            var dragData = new DragPayloadService().Build(secondNormal);
            var dropPoint = new Point(8, 1);

            var over = NewDragEventArgs(
                dragData,
                DragDrop.PreviewDragOverEvent,
                boundaryContainer,
                dropPoint);
            boundaryContainer.RaiseEvent(over);
            Assert.AreEqual(DragDropEffects.Move, over.Effects);
            Assert.AreEqual(Visibility.Visible, indicator.Visibility);
            Assert.AreEqual(expectedEdgeY, Canvas.GetTop(indicator), 0.5);

            var drop = NewDragEventArgs(
                dragData,
                DragDrop.PreviewDropEvent,
                boundaryContainer,
                dropPoint);
            boundaryContainer.RaiseEvent(drop);
            PumpDispatcherUntil(window.Dispatcher, store.SaveCompleted.Task);
            CompleteLayout(window);

            Assert.AreEqual(DragDropEffects.Move, drop.Effects);
            Assert.AreEqual(1, store.SaveCount);
            CollectionAssert.AreEqual(
                new[] { firstPinned.Id, lastPinned.Id, secondNormal.Id, firstNormal.Id },
                board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
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
        var existingNormal = board.AddText("existing normal", BoardCategory.CustomerOriginal);
        var existingPinned = board.AddText("existing pinned", BoardCategory.CustomerOriginal);
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
            var customerTab = FindCategoryTab(
                window,
                viewModel.Categories.Single(panel => panel.Category == BoardCategory.CustomerOriginal));
            var drop = NewDragEventArgs(data, DragDrop.DropEvent, customerTab);
            customerTab.RaiseEvent(drop);
            PumpDispatcherUntil(window.Dispatcher, store.SaveCompleted.Task);
            CompleteLayout(window);

            CollectionAssert.AreEqual(
                new[] { movingPinned.Id, existingPinned.Id, movingNormal.Id, existingNormal.Id },
                board.Items(BoardCategory.CustomerOriginal).Select(item => item.Id).ToArray());
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
                BoardCategory.CustomerOriginal))
            .ToArray();
        var targetPins = Enumerable.Range(0, 16)
            .Select(index => board.AddText(
                $"pin {index}: {new string('x', 120)}",
                BoardCategory.CustomerOriginal))
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
            var customerTab = FindCategoryTab(
                window,
                viewModel.Categories.Single(panel => panel.Category == BoardCategory.CustomerOriginal));
            var drop = NewDragEventArgs(
                new DragPayloadService().Build(moving),
                DragDrop.DropEvent,
                customerTab);

            customerTab.RaiseEvent(drop);
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
