using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;

namespace FloatingTransferStation.Views;

public partial class MainWindow : Window
{

    private void BoardList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<Button>(source) is not null)
        {
            _dragItem = null;
            _dragThresholdCrossed = false;
            _selectionTogglePending = false;
            return;
        }

        var container = FindAncestor<ListBoxItem>(source);
        if (container is null)
        {
            _dragItem = null;
            _dragThresholdCrossed = false;
            _selectionTogglePending = false;
            if (FindAncestor<ScrollBar>(source) is null)
            {
                BoardList.UnselectAll();
            }

            return;
        }

        _dragStart = e.GetPosition(this);
        _dragItem = container.DataContext as BoardItem;
        _dragThresholdCrossed = false;
        _selectionTogglePending = ShouldToggleSelection(
            Keyboard.Modifiers,
            dragThresholdCrossed: false);
        e.Handled = true;
    }

    private void BoardList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragItem is { } item &&
            ShouldToggleSelection(
                _selectionTogglePending ? ModifierKeys.Control : ModifierKeys.None,
                _dragThresholdCrossed))
        {
            ToggleSelection(item);
        }

        if (_dragItem is not null)
        {
            e.Handled = true;
        }

        _dragItem = null;
        _dragThresholdCrossed = false;
        _selectionTogglePending = false;
    }

    private void BoardList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem is null)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragThresholdCrossed = true;
        try
        {
            BeginPanelDrag();
            var data = BuildInternalDragData(_dragItem);
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        catch (FileNotFoundException)
        {
            ShowStatus("图片副本已缺失，无法拖出。");
        }
        finally
        {
            EndPanelDrag();
            _dragItem = null;
            _dragThresholdCrossed = false;
            _selectionTogglePending = false;
        }
    }

    private DataObject BuildInternalDragData(BoardItem dragItem)
    {
        if (_viewModel.ActivePanel is not { } panel)
        {
            throw new InvalidOperationException("No active board category is available for dragging.");
        }

        var selectedIds = BoardList.SelectedItems
            .OfType<BoardItem>()
            .Select(item => item.Id)
            .ToArray();
        var plan = BatchDragPlanner.Create(
            panel.Items.ToArray(),
            selectedIds,
            dragItem.Id);
        if (plan.ClearExistingSelection)
        {
            BoardList.UnselectAll();
        }

        return plan.Items.Count > 1
            ? _dragPayload.BuildInternalBatch(plan.Items)
            : _dragPayload.Build(plan.Items[0]);
    }

    private static bool ShouldToggleSelection(
        ModifierKeys modifiers,
        bool dragThresholdCrossed) =>
        modifiers.HasFlag(ModifierKeys.Control) && !dragThresholdCrossed;

    private void ToggleSelection(BoardItem item)
    {
        if (!BoardList.Items.Contains(item))
        {
            return;
        }

        if (BoardList.SelectedItems.Contains(item))
        {
            BoardList.SelectedItems.Remove(item);
        }
        else
        {
            BoardList.SelectedItems.Add(item);
        }
    }

    private void BoardList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var count = BoardList.SelectedItems.Count;
        SelectedCountText.Text = count.ToString(CultureInfo.InvariantCulture);
        SelectedCountBadge.Visibility = count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        var label = count > 0
            ? $"删除已选 {count} 项"
            : "清空当前分类";
        DeleteContentButton.ToolTip = label;
        AutomationProperties.SetName(DeleteContentButton, label);
    }

    private void BeginPanelDrag()
    {
        ClearExternalDragPayload();
        StopPanelContentAnimation();
        StopCategoryRevealAnimations();
        _expandIntentTimer.Stop();
        _collapseTimer.Stop();
        ClearDropFeedback();
        _panelState.BeginDrag();
    }

    private void EndPanelDrag()
    {
        ClearDropFeedback();
        _panelState.EndDrag();
        _expandIntentTimer.Stop();
        _collapseTimer.Stop();
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(ReconcileSurfaceAfterDrag));
    }

    private void ReconcileSurfaceAfterDrag()
    {
        if (IsMouseOver)
        {
            _panelState.EnterSurface();
            return;
        }

        _panelState.LeaveSurface();
        if (_panelState.IsExpanded)
        {
            _collapseTimer.Start();
        }
    }

    private void CategoryTab_DragEnter(object sender, DragEventArgs e) =>
        UpdateCategoryDropTarget(sender, e);

    private void CategoryTab_DragOver(object sender, DragEventArgs e) =>
        UpdateCategoryDropTarget(sender, e);

    private void CategoryRail_DragEnter(object sender, DragEventArgs e) =>
        UpdateExternalRailGap(e);

    private void CategoryRail_DragOver(object sender, DragEventArgs e) =>
        UpdateExternalRailGap(e);

    private void UpdateExternalRailGap(DragEventArgs e)
    {
        HideInsertionIndicator();
        ClearCategoryDropTargets();
        if (GetExternalDropPayload(e.Data) is not null)
        {
            _externalDragSurfaceVersion++;
            RevealExternalDropRail();
        }
        else
        {
            HideExternalDropRail();
        }

        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void UpdateCategoryDropTarget(object sender, DragEventArgs e)
    {
        HideInsertionIndicator();
        ClearCategoryDropTargets();
        if (sender is Border { DataContext: CategoryViewModel category } &&
            _dragPayload.GetInternalItemIds(e.Data) is { } itemIds &&
            _board.CanMoveManyToCategoryTop(itemIds, category.Category))
        {
            ClearExternalDragPayload();
            if (_viewModel.IsExternalDropRailVisible)
            {
                HideExternalDropRail();
            }

            SetCategoryDropTarget(category);
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        if (sender is Border { DataContext: CategoryViewModel externalCategory } &&
            GetExternalDropPayload(e.Data) is not null)
        {
            _externalDragSurfaceVersion++;
            RevealExternalDropRail();
            SetCategoryDropTarget(externalCategory);
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            return;
        }

        HideExternalDropRail();
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void RevealExternalDropRail()
    {
        StopPanelContentAnimation();
        StopCategoryRevealAnimations();
        _expandIntentTimer.Stop();
        _collapseTimer.Stop();
        _viewModel.SetExternalDropRailVisible(true);
        _viewModel.SetPanelExpanded(false);
        ApplyPlacement(WindowController.CategoryRail(CurrentWorkArea(), _settings));
        UpdateStatusPresentation();
    }

    private void HideExternalDropRail()
    {
        ClearDropFeedback();
        if (!_viewModel.IsExternalDropRailVisible)
        {
            return;
        }

        ApplyPlacement(WindowController.Collapsed(
            CurrentWorkArea(),
            _settings,
            _viewModel.DefaultCapturePanel.Category));
        _viewModel.SetExternalDropRailVisible(false);
        _viewModel.SetPanelExpanded(false);
        UpdateStatusPresentation();
    }

    private void CategoryTab_DragLeave(object sender, DragEventArgs e)
    {
        ClearCategoryDropTargets();
        ScheduleExternalDropRailLeaveReconciliation();
        e.Handled = true;
    }

    private void CategoryRail_DragLeave(object sender, DragEventArgs e)
    {
        ClearCategoryDropTargets();
        ScheduleExternalDropRailLeaveReconciliation();
        e.Handled = true;
    }

    private void CategoryRail_Drop(object sender, DragEventArgs e)
    {
        ClearExternalDragPayload();
        HideExternalDropRail();
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void ScheduleExternalDropRailLeaveReconciliation()
    {
        if (!_viewModel.IsExternalDropRailVisible)
        {
            ClearExternalDragPayload();
            return;
        }

        var surfaceVersion = _externalDragSurfaceVersion;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (surfaceVersion == _externalDragSurfaceVersion &&
                    _viewModel.IsExternalDropRailVisible)
                {
                    ClearExternalDragPayload();
                    HideExternalDropRail();
                }
            }));
    }

    private async void CategoryTab_Drop(object sender, DragEventArgs e)
    {
        ClearDropFeedback();
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (sender is not Border { DataContext: CategoryViewModel category })
        {
            ClearExternalDragPayload();
            HideExternalDropRail();
            return;
        }

        if (_dragPayload.GetInternalItemIds(e.Data) is { } itemIds)
        {
            ClearExternalDragPayload();
            HideExternalDropRail();
            if (!_board.CanMoveManyToCategoryTop(itemIds, category.Category))
            {
                return;
            }

            e.Effects = DragDropEffects.Move;
            var sourceCategory = _viewModel.ActivePanel?.Category;
            var selectedBefore = CaptureSelectedItemIds();
            var scrollTargetId = GetCategoryMoveScrollTargetId(itemIds);
            var result = await _mutations.MoveManyToCategoryTopAsync(
                itemIds,
                category.Category);
            await Dispatcher.InvokeAsync(
                () => ApplyCategoryBatchMoveResult(
                    result,
                    sourceCategory,
                    category.Category,
                    scrollTargetId,
                    selectedBefore,
                    e),
                DispatcherPriority.Send);

            return;
        }

        var payload = GetExternalDropPayload(e.Data);
        ClearExternalDragPayload();
        HideExternalDropRail();
        if (payload is not null)
        {
            e.Effects = DragDropEffects.Copy;
            var import = ImportExternalDropSafelyAsync(
                payload,
                category.Category,
                _windowOperationCancellation.Token);
            TrackPendingOperation(import);
            await import;

            return;
        }
    }

    private ExternalDropPayload? GetExternalDropPayload(IDataObject data)
    {
        if (!ReferenceEquals(_externalDragData, data))
        {
            _externalDragData = data;
            _externalDragPayload = _externalDropPayloadReader.Read(data);
        }

        return _externalDragPayload;
    }

    private void ClearExternalDragPayload()
    {
        _externalDragData = null;
        _externalDragPayload = null;
    }

    private async Task ImportExternalDropSafelyAsync(
        ExternalDropPayload payload,
        BoardCategory category,
        CancellationToken cancellationToken)
    {
        try
        {
            await _externalDropImportService.ImportAsync(payload, category, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            ShowStatus("拖入内容未保存，请重试。");
        }
    }

    private void BoardList_PreviewDragOver(object sender, DragEventArgs e)
    {
        ClearCategoryDropTargets();
        if (_viewModel.ActivePanel is not { } panel ||
            _dragPayload.GetInternalItemIds(e.Data) is not { } itemIds)
        {
            HideInsertionIndicator();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var location = GetBoardDropLocation(e, panel);
        if (!_board.CanMoveMany(itemIds, panel.Category, location.InsertionIndex))
        {
            HideInsertionIndicator();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        ShowInsertionIndicator(location.IndicatorY);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void BoardList_PreviewDragLeave(object sender, DragEventArgs e)
    {
        HideInsertionIndicator();
        e.Handled = true;
    }

    private async void BoardList_PreviewDrop(object sender, DragEventArgs e)
    {
        ClearDropFeedback();
        if (_viewModel.ActivePanel is not { } panel ||
            _dragPayload.GetInternalItemIds(e.Data) is not { } itemIds)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var location = GetBoardDropLocation(e, panel);
        if (!_board.CanMoveMany(itemIds, panel.Category, location.InsertionIndex))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        var selectedBefore = CaptureSelectedItemIds();
        var result = await _mutations.MoveManyAsync(
            itemIds,
            panel.Category,
            location.InsertionIndex);
        await Dispatcher.InvokeAsync(
            () => ApplyListBatchMoveResult(result, selectedBefore, e),
            DispatcherPriority.Send);
    }

    private void ApplyCategoryBatchMoveResult(
        BoardBatchMoveResult result,
        BoardCategory? sourceCategory,
        BoardCategory targetCategory,
        Guid? scrollTargetId,
        IReadOnlyCollection<Guid> selectedBefore,
        DragEventArgs e)
    {
        if (result is BoardBatchMoveResult.Failed or BoardBatchMoveResult.Invalid)
        {
            RestoreSelection(selectedBefore);
            e.Effects = DragDropEffects.None;
        }
        else if (result == BoardBatchMoveResult.NoChange)
        {
            RestoreSelection(selectedBefore);
        }
        else
        {
            BoardList.UnselectAll();
            if (sourceCategory != targetCategory && scrollTargetId is { } itemId)
            {
                ActivateCategoryAfterBatchMove(targetCategory, itemId);
            }
        }
    }

    private void ApplyListBatchMoveResult(
        BoardBatchMoveResult result,
        IReadOnlyCollection<Guid> selectedBefore,
        DragEventArgs e)
    {
        if (result is BoardBatchMoveResult.Failed or BoardBatchMoveResult.Invalid)
        {
            RestoreSelection(selectedBefore);
            e.Effects = DragDropEffects.None;
        }
        else if (result == BoardBatchMoveResult.NoChange)
        {
            RestoreSelection(selectedBefore);
        }
        else
        {
            BoardList.UnselectAll();
        }
    }

    private double GetVisibleListEndY()
    {
        if (FindDescendant<VirtualizingStackPanel>(BoardList) is not { } itemsHost)
        {
            return 0d;
        }

        var endY = 0d;
        for (var index = 0; index < itemsHost.Children.Count; index++)
        {
            if (itemsHost.Children[index] is not ListBoxItem item || !item.IsVisible)
            {
                continue;
            }

            var itemBottom = item.TranslatePoint(new Point(0, item.ActualHeight), BoardList).Y;
            endY = Math.Max(endY, itemBottom);
        }

        return endY;
    }

    private double ClampIndicatorY(double value)
    {
        var maxY = Math.Max(0d, BoardList.ActualHeight - InsertionIndicator.Height);
        return Math.Clamp(double.IsFinite(value) ? value : 0d, 0d, maxY);
    }

    private void ShowInsertionIndicator(double y)
    {
        Canvas.SetTop(InsertionIndicator, ClampIndicatorY(y));
        InsertionIndicator.Visibility = Visibility.Visible;
        InsertionIndicator.Opacity = 1d;
    }

    private void HideInsertionIndicator()
    {
        InsertionIndicator.Opacity = 0d;
        InsertionIndicator.Visibility = Visibility.Collapsed;
    }

    private void SetCategoryDropTarget(CategoryViewModel target)
    {
        foreach (var category in _viewModel.Categories)
        {
            category.IsDropTarget = ReferenceEquals(category, target);
        }
    }

    private void ClearCategoryDropTargets()
    {
        foreach (var category in _viewModel.Categories)
        {
            category.IsDropTarget = false;
        }
    }

    private void ClearDropFeedback()
    {
        ClearCategoryDropTargets();
        HideInsertionIndicator();
    }
}
