using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FloatingTransferStation.Design;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;

namespace FloatingTransferStation.Views;

public partial class MainWindow : Window
{
    private void BoardList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        // 搜索/子分组过滤期间禁用拖拽：内部排序的落点按可见(已过滤)视图计算会得到错误的源索引，
        // 拖出语义也随视图收窄产生歧义（设计草案切片 1 明确禁用；chip 过滤沿 #37 口径，
        // 契约 #57——拖卡归属只经 chip 目标、不经列表内拖放）。
        // 按钮按压不起拖：按住置顶/选择按钮移动不应拖出整张卡（按钮有自己的意图）。
        if (_viewModel.IsSearchActive ||
            IsSubgroupFilterActive ||
            e.LeftButton != MouseButtonState.Pressed ||
            _pressItem is null ||
            _pressButton is not null)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _pressStartPosition.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _pressStartPosition.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragThresholdCrossed = true;
        // 拖拽不是点击：双击痕迹一并作废，拖拽结束后的快速连击按独立单击判定；
        // 挂起的左半裸单击送达同样取消（拖出优先于送达，需求 6）。
        _lastCardClick = null;
        CancelPendingTextDelivery();
        try
        {
            BeginPanelDrag();
            var data = BuildInternalDragData(_pressItem);
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        catch (FileNotFoundException)
        {
            ShowStatus("图片副本已缺失，无法拖出。");
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or COMException)
        {
            ShowStatus("无法拖出这些内容：" + exception.Message);
        }
        finally
        {
            EndPanelDrag();
            ResetCardPressState();
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
            ClearUserSelection();
        }

        return plan.Items.Count > 1
            ? _dragPayload.BuildInternalBatch(plan.Items)
            : _dragPayload.Build(plan.Items[0]);
    }

    private void ToggleSelection(BoardItem item)
    {
        if (_isClosing || !_viewModel.IsPanelExpanded || !BoardList.Items.Contains(item))
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

        _selectionAnchor = (item.Category, item.Id);
        RecordUserSelection();
    }

    private void SelectRange(BoardItem target, bool additive)
    {
        if (_isClosing ||
            !_viewModel.IsPanelExpanded ||
            _viewModel.ActivePanel is not { } panel ||
            !BoardList.Items.Contains(target))
        {
            return;
        }

        var items = panel.Items.ToArray();
        var targetIndex = Array.IndexOf(items, target);
        var anchorIndex = _selectionAnchor is { } anchor && anchor.Category == panel.Category
            ? Array.FindIndex(items, item => item.Id == anchor.Id && BoardList.SelectedItems.Contains(item))
            : -1;
        if (anchorIndex < 0)
        {
            anchorIndex = targetIndex;
            _selectionAnchor = (panel.Category, target.Id);
        }

        if (!additive)
        {
            BoardList.UnselectAll();
        }

        for (var index = Math.Min(anchorIndex, targetIndex); index <= Math.Max(anchorIndex, targetIndex); index++)
        {
            if (!BoardList.SelectedItems.Contains(items[index]))
            {
                BoardList.SelectedItems.Add(items[index]);
            }
        }

        RecordUserSelection();
    }

    private void BoardList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var count = BoardList.SelectedItems.Count;
        SelectedCountText.Text = count.ToString(CultureInfo.InvariantCulture);
        SelectedCountBadge.Visibility = count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        FadeAnimation.SetIsActive(SelectedCountBadge, count > 0);
        UpdateDeleteButtonLabel();
        UpdateBatchPinButton();
    }

    private void BeginPanelDrag()
    {
        ClearExternalDragPayload();
        CancelPanelCollapseExit();
        StopPanelContentAnimation();
        StopCardEntranceAnimations();
        StopCategoryRevealAnimations();
        _expandIntentTimer.Stop();
        _collapseTimer.Stop();
        ClearDropFeedback();
        // 槽位迟滞是拖拽会话状态：会话开始即复位（上一会话的槽位不得渗入本次判定）。
        ResetInsertionSlotHysteresis();
        _panelState.BeginDrag();
    }

    private void EndPanelDrag()
    {
        ClearDropFeedback();
        // 会话结束同样复位：Drop 在 DoDragDrop 内发生，先于本方法消费迟滞真值
        //（所见即所落，契约 #51），复位必须留在会话边界而不是 ClearDropFeedback——
        // PreviewDrop 开头的终清若顺带复位，落点会退回无迟滞解析、与指示线背离。
        ResetInsertionSlotHysteresis();
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

        // 搜索态/chip 过滤态进行中不启动收起（与 Root_MouseLeave 的保持语义一致；
        // chip 过滤沿 #37 口径抑制自动收起，契约 #57）。
        _panelState.LeaveSurface();
        if (_panelState.IsExpanded && !_viewModel.IsSearchActive && !IsSubgroupFilterActive)
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
        WithdrawInsertionIndicator();
        WithdrawCategoryDropTarget();
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
        WithdrawInsertionIndicator();
        if (sender is Border { DataContext: CategoryViewModel tabCategory } &&
            !BoardCategoryCatalog.IsContentBucket(tabCategory.Category))
        {
            // 启动标签的文件拖入导入（#30 §七矩阵）：∈{*.exe,*.lnk} 给 Copy 效果；
            // 记录标签与不支持的文件组维持拒绝（Effects.None）。
            if (tabCategory.Category == QuickLaunchMigration.LauncherCategory &&
                ReadImportableLauncherFiles(e.Data).Count > 0)
            {
                ClearExternalDragPayload();
                ApplyCategoryDropTarget(tabCategory);
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }

            WithdrawCategoryDropTarget();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (sender is Border { DataContext: CategoryViewModel category } &&
            _dragPayload.GetInternalItemIds(e.Data) is { } itemIds &&
            _board.CanMoveManyToCategoryTop(itemIds, category.Category))
        {
            ClearExternalDragPayload();
            if (_viewModel.IsExternalDropRailVisible)
            {
                HideExternalDropRail();
            }

            ApplyCategoryDropTarget(category);
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        if (sender is Border { DataContext: CategoryViewModel externalCategory } &&
            GetExternalDropPayload(e.Data) is not null)
        {
            _externalDragSurfaceVersion++;
            RevealExternalDropRail();
            ApplyCategoryDropTarget(externalCategory);
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            return;
        }

        WithdrawCategoryDropTarget();
        HideExternalDropRail();
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void RevealExternalDropRail()
    {
        if (_viewModel.IsPanelExpanded)
        {
            SaveCurrentScrollOffset();
        }

        StopPanelContentAnimation();
        StopCategoryRevealAnimations();
        _expandIntentTimer.Stop();
        _collapseTimer.Stop();
        _panelState.CollapseForExternalDrop();
        // 贴边隐藏待命只在展开态有意义：外部拖放把面板换成轨道几何即解除，
        // 不留悬置 armed（未进入 Docked）。
        DisarmEdgeHide();
        _viewModel.SetExternalDropRailVisible(true);
        _viewModel.SetPanelExpanded(false);
        ApplyPlacement(WindowController.CategoryRail(CurrentWorkArea(), _settings));
        UpdateStatusPresentation();
    }

    private void HideExternalDropRail()
    {
        // 反馈清理由调用方表达（断言目标→差分应用；撤离→保持窗复核；Drop→终清），
        // 本方法只负责轨道几何回落，不再顺手清拖放反馈（契约 #51）。
        if (!_viewModel.IsExternalDropRailVisible)
        {
            return;
        }

        // 固定期间外部拖入只是临时换成轨道几何；会话结束后固定若仍在，直接回展而不是回落收起态。
        if (_panelState.IsPanelHoldActive && !_isClosing)
        {
            _viewModel.SetExternalDropRailVisible(false);
            RestoreHeldPanelAfterExternalDrop();
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
        WithdrawCategoryDropTarget();
        ScheduleExternalDropRailLeaveReconciliation();
        e.Handled = true;
    }

    private void CategoryRail_DragLeave(object sender, DragEventArgs e)
    {
        WithdrawCategoryDropTarget();
        ScheduleExternalDropRailLeaveReconciliation();
        e.Handled = true;
    }

    private void CategoryRail_Drop(object sender, DragEventArgs e)
    {
        ClearExternalDragPayload();
        ClearDropFeedback();
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

        if (sender is Border { DataContext: CategoryViewModel dropTabCategory } &&
            !BoardCategoryCatalog.IsContentBucket(dropTabCategory.Category))
        {
            // 启动标签的文件拖入导入（#30 §七矩阵）；其余（卡片/内容/记录目标）拒绝。
            if (dropTabCategory.Category == QuickLaunchMigration.LauncherCategory)
            {
                var importable = ReadImportableLauncherFiles(e.Data);
                if (importable.Count > 0)
                {
                    ClearExternalDragPayload();
                    HideExternalDropRail();
                    e.Effects = DragDropEffects.Copy;
                    var raw = ReadRawLauncherFiles(e.Data);
                    TrackPendingOperation(ImportLauncherFilesAsync(importable, raw.Count - importable.Count));
                    return;
                }
            }

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

        // 内容桶消费面只认图片/文本载荷（#30 r2-M1）：原始文件清单（FileList）的
        // 解释权归启动标签/表面（不经本缓存），内容桶对非图片文件组维持升级前拒绝。
        return _externalDragPayload is ExternalDropPayload.FileList ? null : _externalDragPayload;
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
        WithdrawCategoryDropTarget();
        if (_viewModel.ActivePanel is not { } panel ||
            _dragPayload.GetInternalItemIds(e.Data) is not { } itemIds)
        {
            WithdrawInsertionIndicator();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var location = GetBoardDropLocation(e, panel, itemIds);
        if (!_board.CanMoveMany(itemIds, panel.Category, location.InsertionIndex))
        {
            WithdrawInsertionIndicator();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        // 同位整区隐藏（契约 #51 增补）：NoChange 谓词按 held 槽位评估（与 Drop 的
        // NoChange 判定同一实现），落点＝原位置时指示线不显示；切换边界随 held
        // 槽位继承迟滞。Drop 语义零变化（同位松手仍走 MoveMany→NoChange→恢复选择）。
        if (_board.IsNoChangeMove(itemIds, panel.Category, location.InsertionIndex))
        {
            HideInsertionIndicatorForSamePosition();
        }
        else
        {
            ShowInsertionIndicator(location.IndicatorY);
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void BoardList_PreviewDragLeave(object sender, DragEventArgs e)
    {
        WithdrawInsertionIndicator();
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

        var location = GetBoardDropLocation(e, panel, itemIds);
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
            ClearUserSelection();
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
            ClearUserSelection();
        }
    }

    private double ClampIndicatorY(double value)
    {
        var maxY = Math.Max(0d, BoardList.ActualHeight - InsertionIndicator.Height);
        return Math.Clamp(double.IsFinite(value) ? value : 0d, 0d, maxY);
    }

    private void ShowInsertionIndicator(double y)
    {
        // 断言即真值（契约 #51）：作废挂起的撤离复核并记断言时刻——保持窗内的
        // 重断言不产生任何 IsActive 翻转（零淡入重启、零明灭脉冲）。
        _insertionIndicatorWithdrawVersion++;
        _insertionIndicatorAssertTick = Environment.TickCount64;
        _insertionIndicatorShown = true;
        Canvas.SetTop(InsertionIndicator, ClampIndicatorY(y));
        InsertionIndicator.Visibility = Visibility.Visible;
        FadeAnimation.SetIsActive(InsertionIndicator, true);
    }

    /// <summary>
    /// 同位隐藏（契约 #51 增补）：held 槽位（迟滞过滤后的真值）落入 NoChange 区
    /// ——落点＝原位置，指示线整区不显示。谓词按 held 槽位评估，同位/异位切换
    /// 边界因此天然继承 ±SlotFlipHysteresisDip 迟滞，原位附近来回进出不产生闪烁。
    /// </summary>
    private void HideInsertionIndicatorForSamePosition()
    {
        _insertionIndicatorWithdrawVersion++;
        _insertionIndicatorShown = false;
        FadeAnimation.SetIsActive(InsertionIndicator, false);
        InsertionIndicator.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 撤离（DragLeave／非内部载荷／非法槽位／轨道与标签面）不立即隐藏：进入
    /// <see cref="DesignTokens.DropTargetWithdrawHoldMs"/> 保持窗复核——窗口内任何
    /// 重断言作废复核（指示线持续可见、IsActive 不翻转），真离开一次清到位；
    /// 与分类高亮撤离语义同构（契约 #51 ③），消灭边缘往返的隐-现脉冲。
    /// </summary>
    private void WithdrawInsertionIndicator()
    {
        var version = ++_insertionIndicatorWithdrawVersion;
        if (!_insertionIndicatorShown)
        {
            // 从未断言（或已终清）：撤离语义下直接收敛到位——表面切换可能把元素
            // 置回 Visible（Opacity 0），不留「Visible 但透明」的悬置态。
            HideInsertionIndicator();
            return;
        }

        ScheduleInsertionIndicatorWithdrawCommit(version);
    }

    private void ScheduleInsertionIndicatorWithdrawCommit(long version)
    {
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (version != _insertionIndicatorWithdrawVersion)
                {
                    return;
                }

                if (Environment.TickCount64 - _insertionIndicatorAssertTick <
                    DesignTokens.DropTargetWithdrawHoldMs)
                {
                    // 保持窗未期满：还不能确认真离开，再等一个输入通道。
                    ScheduleInsertionIndicatorWithdrawCommit(version);
                    return;
                }

                HideInsertionIndicator();
            }));
    }

    private void HideInsertionIndicator()
    {
        _insertionIndicatorWithdrawVersion++;
        _insertionIndicatorShown = false;
        FadeAnimation.SetIsActive(InsertionIndicator, false);
        InsertionIndicator.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 拖放目标高亮的「状态真值＋差分应用」（契约 #51）：断言事件（Enter/Over 命中
    /// 有效目标）立即把全部分类一次性对齐到真值——属性值不变时底层 INPC 不触发，
    /// 稳态拖拽零重启动画；同时作废挂起的撤离复核（抖动中的重断言不回落）。
    /// </summary>
    private void ApplyCategoryDropTarget(CategoryViewModel? target)
    {
        _categoryDropTargetWithdrawVersion++;
        if (target is not null)
        {
            _categoryDropTargetAssertTick = Environment.TickCount64;
        }

        _categoryDropTargetApplied = target;
        foreach (var category in _viewModel.Categories)
        {
            category.IsDropTarget = ReferenceEquals(category, target);
        }
    }

    /// <summary>
    /// 撤离（DragLeave／tab 间隙与轨道死区）不立即清高亮：进入保持窗
    /// （<see cref="DesignTokens.DropTargetWithdrawHoldMs"/>）复核——窗口内任何
    /// 重新断言都会作废本次复核，手抖在标签边缘产生的清-设交替被整段吸收；
    /// 真离开（窗口内无再断言）才一次清到位，与既有的撤离语义保持一致。
    /// </summary>
    private void WithdrawCategoryDropTarget()
    {
        var version = ++_categoryDropTargetWithdrawVersion;
        if (_categoryDropTargetApplied is null)
        {
            return;
        }

        ScheduleCategoryDropTargetWithdrawCommit(version);
    }

    private void ScheduleCategoryDropTargetWithdrawCommit(long version)
    {
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (version != _categoryDropTargetWithdrawVersion)
                {
                    return;
                }

                if (Environment.TickCount64 - _categoryDropTargetAssertTick <
                    DesignTokens.DropTargetWithdrawHoldMs)
                {
                    // 保持窗未期满：还不能确认真离开，再等一个输入通道。
                    ScheduleCategoryDropTargetWithdrawCommit(version);
                    return;
                }

                ApplyCategoryDropTarget(null);
            }));
    }

    private void ClearDropFeedback()
    {
        ApplyCategoryDropTarget(null);
        HideInsertionIndicator();
    }
}
