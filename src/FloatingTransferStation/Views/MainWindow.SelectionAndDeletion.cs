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
using FloatingTransferStation.Design;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;

namespace FloatingTransferStation.Views;

public partial class MainWindow : Window
{
    public static RoutedUICommand BatchPinCommand { get; } = new(
        "批量置顶或取消置顶",
        nameof(BatchPinCommand),
        typeof(MainWindow),
        new InputGestureCollection
        {
            new KeyGesture(Key.P, ModifierKeys.Control)
        });

    private bool _isBatchPinPending;
    private bool _isDeletePending;
    private long _selectionChangeVersion;
    private (BoardCategory Category, Guid Id)? _selectionAnchor;
    private (BoardCategory Category, Guid[] Ids)? _latestUserSelection;

    private void ClearUserSelection()
    {
        _selectionAnchor = null;
        BoardList.UnselectAll();
        RecordUserSelection();
    }

    private void ActivatePanel(BoardCategory category)
    {
        if (IsReviewActive() && category != DailyReviewMigration.ReviewCategory)
        {
            TrackPendingOperation(FlushReviewAsync());
        }

        // 搜索作用于单一分类:切换分类即退出搜索并还原列表(设计草案切片 1)。
        if (_viewModel.IsSearchActive && _viewModel.ActivePanel?.Category != category)
        {
            ExitSearchMode();
        }

        if (_viewModel.ActivePanel?.Category != category)
        {
            ClearUserSelection();
        }

        _viewModel.Activate(category);
        if (_viewModel.ActivePanel is { } activePanel)
        {
            AttachCardEntrance(activePanel.Items);
        }

        UpdateReviewSurface();
    }

    private void RecordUserSelection()
    {
        _selectionChangeVersion++;
        _latestUserSelection = _viewModel.ActivePanel is { } panel
            ? (panel.Category, CaptureSelectedItemIds())
            : null;
    }

    private void RestoreSelectionAfterSave(IReadOnlyCollection<Guid> originalIds, long selectionVersion)
    {
        if (selectionVersion == _selectionChangeVersion)
        {
            RestoreSelection(originalIds);
        }
        else if (_latestUserSelection is { } latest && latest.Category == _viewModel.ActivePanel?.Category)
        {
            // A failed mutation may rebuild the collection, so restore the user's
            // latest explicit selection rather than the now-empty ListBox selection.
            RestoreSelection(latest.Ids);
        }
    }

    private async void BoardList_ButtonClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not Button { Tag: BoardItem item } button)
        {
            return;
        }

        e.Handled = true;
        if (Equals(button.CommandParameter, "ToggleSelection"))
        {
            ToggleSelection(item);
            return;
        }

        if (!Equals(button.CommandParameter, "TogglePin"))
        {
            return;
        }

        await ToggleCardPinAsync(item);
    }

    /// <summary>
    /// 单卡置顶/取消置顶（含选择与滚动位置恢复）。由两条入口共用：
    /// BoardList_ButtonClick（合成与 UIA 自动化路径）与卡片手势层
    /// （真实输入路径，见 MainWindow.CardGestures.cs）。
    /// </summary>
    private async Task ToggleCardPinAsync(BoardItem item)
    {
        var selectedBefore = CaptureSelectedItemIds();
        var selectionVersion = _selectionChangeVersion;
        var category = item.Category;
        var offset = CurrentScrollOffset();
        await _mutations.SetPinnedAsync([item.Id], !item.IsPinned);
        await Dispatcher.InvokeAsync(
            () =>
            {
                if (_viewModel.ActivePanel?.Category != category)
                {
                    return;
                }

                RestoreSelectionAfterSave(selectedBefore, selectionVersion);

                RestoreExplicitScrollOffset(category, offset);
            },
            DispatcherPriority.Send);
    }

    private void UpdateBatchPinButton()
    {
        var selected = BoardList.SelectedItems.OfType<BoardItem>().ToArray();
        BatchPinButton.Visibility = selected.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        BatchPinButton.IsEnabled = selected.Length > 0 && !_isBatchPinPending;
        if (selected.Length == 0)
        {
            CommandManager.InvalidateRequerySuggested();
            return;
        }

        var label = _isBatchPinPending
            ? $"正在保存 {selected.Length} 项置顶状态"
            : $"{(selected.All(item => item.IsPinned) ? "取消置顶" : "置顶")}已选 {selected.Length} 项";
        BatchPinButton.ToolTip = label;
        AutomationProperties.SetName(BatchPinButton, label);
        CommandManager.InvalidateRequerySuggested();
    }

    private async void BatchPinButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await ApplyBatchPinSelectionAsync();
    }

    private void BatchPinCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase)
        {
            return;
        }

        e.CanExecute =
            BoardList is not null &&
            BoardList.SelectedItems.Count > 0 &&
            !_isClosing &&
            _viewModel.IsPanelExpanded &&
            !_isBatchPinPending;
        e.Handled = true;
    }

    private async void BatchPinCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        await ApplyBatchPinSelectionAsync();
    }

    private void SelectAllCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase)
        {
            return;
        }

        e.CanExecute =
            !_isClosing &&
            _viewModel.IsPanelExpanded &&
            _viewModel.ActivePanel is { Items.Count: > 0 };
        e.Handled = true;
    }

    private void SelectAllCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_isClosing ||
            Keyboard.FocusedElement is TextBoxBase ||
            !_viewModel.IsPanelExpanded ||
            _viewModel.IsSearchActive ||
            _viewModel.ActivePanel is not { Items.Count: > 0 })
        {
            return;
        }

        _selectionAnchor = null;
        BoardList.SelectAll();
        RecordUserSelection();
        e.Handled = true;
    }

    private async Task ApplyBatchPinSelectionAsync()
    {
        if (_isClosing ||
            Keyboard.FocusedElement is TextBoxBase ||
            !_viewModel.IsPanelExpanded ||
            _isBatchPinPending ||
            _viewModel.ActivePanel is not { } activePanel)
        {
            return;
        }

        var selectedBefore = CaptureSelectedItemIds();
        if (selectedBefore.Length == 0)
        {
            return;
        }

        var selectedSet = selectedBefore.ToHashSet();
        var selectedIds = activePanel.Items
            .Where(item => selectedSet.Contains(item.Id))
            .Select(item => item.Id)
            .ToArray();
        if (selectedIds.Length != selectedBefore.Length)
        {
            return;
        }

        var isPinned = activePanel.Items
            .Where(item => selectedSet.Contains(item.Id))
            .Any(item => !item.IsPinned);
        var category = activePanel.Category;
        var offset = CurrentScrollOffset();
        var selectionVersion = _selectionChangeVersion;
        _isBatchPinPending = true;
        UpdateBatchPinButton();
        try
        {
            await _mutations.SetPinnedAsync(selectedIds, isPinned);
            await Dispatcher.InvokeAsync(
                () =>
                {
                    if (_viewModel.ActivePanel?.Category != category)
                    {
                        return;
                    }

                    RestoreSelectionAfterSave(selectedBefore, selectionVersion);

                    RestoreExplicitScrollOffset(category, offset);
                },
                DispatcherPriority.Send);
        }
        finally
        {
            await Dispatcher.InvokeAsync(
                () =>
                {
                    _isBatchPinPending = false;
                    UpdateBatchPinButton();
                },
                DispatcherPriority.Send);
        }
    }

    private void HeaderActionRegion_MouseEnter(object sender, MouseEventArgs e) =>
        SetHeaderActionsVisible(true);

    private void HeaderActionRegion_MouseLeave(object sender, MouseEventArgs e) =>
        SetHeaderActionsVisible(false);

    private void SetHeaderActionsVisible(bool visible)
    {
        FadeAnimation.SetIsActive(HeaderActions, visible);
        HeaderActions.IsHitTestVisible = visible;
    }

    private async void ResetWindowButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var work = CurrentWorkArea();
        _settings = _settings.ResetToDefault(work.Width, work.Height);
        ApplyPlacement(WindowController.Expanded(work, _settings, _rightEdgeBleed));
        try
        {
            await SaveSettingsAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowStatus("窗口设置暂未保存。");
        }
    }

    private async void DeleteContentButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await DeleteFromTrashButtonAsync(rightClick: false);
    }

    /// <summary>
    /// 垃圾桶按钮双交互（1.15.0 起）：有选择时左/右键都删除已选（与现状一致）；
    /// 无选择时按偏好执行左/右键各自的清空行为（默认左=清空非置顶、右=清空全部）。
    /// </summary>
    private async void DeleteContentButton_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        await DeleteFromTrashButtonAsync(rightClick: true);
    }

    private async Task DeleteFromTrashButtonAsync(bool rightClick)
    {
        if (_isClosing || _isDeletePending || _viewModel.ActivePanel is not { } activePanel)
        {
            return;
        }

        var selectedBefore = CaptureSelectedItemIds();
        if (selectedBefore.Length > 0)
        {
            await DeleteContentAsync(selectedBefore, activePanel.Category);
            return;
        }

        var behavior = rightClick
            ? _preferences.TrashNoSelectionRightClick
            : MapLeftClickTrashBehavior(_preferences.TrashNoSelectionLeftClick);
        if (behavior == TrashNoSelectionRightClickAction.NoAction)
        {
            return;
        }

        await DeleteContentAsync(
            [],
            activePanel.Category,
            behavior == TrashNoSelectionRightClickAction.ClearAll
                ? TrashClearMode.All
                : TrashClearMode.NonPinned);
    }

    private static TrashNoSelectionRightClickAction MapLeftClickTrashBehavior(
        TrashNoSelectionLeftClickAction leftClick) =>
        leftClick == TrashNoSelectionLeftClickAction.ClearAll
            ? TrashNoSelectionRightClickAction.ClearAll
            : TrashNoSelectionRightClickAction.ClearNonPinned;

    /// <summary>无选择时垃圾桶左/右键清空范围的内部表示（测试经反射传参）。</summary>
    internal enum TrashClearMode
    {
        None,
        All,
        NonPinned
    }

    /// <summary>
    /// 面板持有键盘焦点时 Ctrl+Z 的撤销入口(内部方法供 STA 测试直达);
    /// 状态提示由 BoardMutationService 统一负责。
    /// </summary>
    internal Task<bool> UndoLastDeleteFromPanelAsync() => _mutations.UndoLastDeleteAsync();

    /// <summary>
    /// 垃圾桶按钮的可访问名称与提示:有选择时始终是「删除已选 N 项」;
    /// 无选择时按偏好描述左/右键各自的清空行为（两侧行为一致时合并为单一描述）。
    /// </summary>
    private void UpdateDeleteButtonLabel()
    {
        var label = DescribeTrashButtonAction(BoardList.SelectedItems.Count);
        DeleteContentButton.ToolTip = label;
        AutomationProperties.SetName(DeleteContentButton, label);
    }

    private string DescribeTrashButtonAction(int selectedCount)
    {
        if (selectedCount > 0)
        {
            return $"删除已选 {selectedCount} 项";
        }

        var left = DescribeTrashClearScope(_preferences.TrashNoSelectionLeftClick ==
            TrashNoSelectionLeftClickAction.ClearNonPinned);
        var right = _preferences.TrashNoSelectionRightClick switch
        {
            TrashNoSelectionRightClickAction.ClearNonPinned =>
                DescribeTrashClearScope(nonPinned: true),
            TrashNoSelectionRightClickAction.NoAction => "无操作",
            _ => DescribeTrashClearScope(nonPinned: false)
        };
        return left == right ? left : $"左键{left}，右键{right}";
    }

    private static string DescribeTrashClearScope(bool nonPinned) =>
        nonPinned ? "清空非置顶" : "清空全部";

    private async Task DeleteContentAsync(
        Guid[] selectedBefore,
        BoardCategory targetCategory,
        TrashClearMode clearMode = TrashClearMode.None)
    {
        if (_isClosing ||
            _isDeletePending ||
            (selectedBefore.Length == 0 && clearMode == TrashClearMode.None))
        {
            return;
        }

        // 清空非置顶前先取被清范围：既是状态提示的数量，也是空操作短路条件。
        var nonPinnedIds = clearMode == TrashClearMode.NonPinned
            ? _board.Items(targetCategory)
                .Where(item => !item.IsPinned)
                .Select(item => item.Id)
                .ToArray()
            : [];
        if (clearMode == TrashClearMode.NonPinned && nonPinnedIds.Length == 0)
        {
            ShowStatus("当前分类没有非置顶内容可清空。");
            return;
        }

        var clearAllCount = clearMode == TrashClearMode.All
            ? _board.Items(targetCategory).Count
            : 0;
        _isDeletePending = true;
        var selectionVersion = _selectionChangeVersion;
        DeleteContentButton.IsEnabled = false;
        BeginDeletedCardFade(
            clearMode == TrashClearMode.NonPinned ? nonPinnedIds : selectedBefore,
            targetCategory,
            clearMode);
        try
        {
            bool success;
            string? completionStatus = null;
            if (selectedBefore.Length > 0)
            {
                success = await _mutations.DeleteManyAsync(selectedBefore);
            }
            else if (clearMode == TrashClearMode.NonPinned)
            {
                success = await _mutations.ClearNonPinnedAsync(targetCategory);
                if (success)
                {
                    completionStatus = $"已清空非置顶 {nonPinnedIds.Length} 项（可 Ctrl+Z 撤销）";
                }
            }
            else
            {
                success = await _mutations.ClearCategoryAsync(targetCategory);
                if (success)
                {
                    completionStatus = $"已清空全部 {clearAllCount} 项（可 Ctrl+Z 撤销）";
                }
            }

            if (completionStatus is { } status)
            {
                ShowStatus(status);
            }

            await Dispatcher.InvokeAsync(
                () =>
                {
                    if (_viewModel.ActivePanel?.Category != targetCategory)
                    {
                        return;
                    }

                    RestoreSelectionAfterSave(success ? [] : selectedBefore, selectionVersion);
                },
                DispatcherPriority.Send);
        }
        finally
        {
            await Dispatcher.InvokeAsync(
                () =>
                {
                    _isDeletePending = false;
                    DeleteContentButton.ClearValue(IsEnabledProperty);
                    RestoreDeletedCardFade();
                },
                DispatcherPriority.Send);
        }
    }

    private void BeginDeletedCardFade(
        Guid[] fadeIds,
        BoardCategory targetCategory,
        TrashClearMode clearMode)
    {
        // 反馈淡出与数据管线并行执行，不延迟任何数据操作；
        // 保存失败回滚或容器被虚拟化复用后由 RestoreDeletedCardFade 复位。
        if (!ClientAreaAnimationsEnabled ||
            _viewModel.ActivePanel?.Category != targetCategory)
        {
            return;
        }

        HashSet<Guid>? ids = clearMode switch
        {
            // 清空全部淡出整个列表；其余路径精确淡出受影响条目。
            TrashClearMode.All => null,
            TrashClearMode.NonPinned => [.. fadeIds],
            _ => fadeIds.Length > 0 ? [.. fadeIds] : []
        };
        var fade = new DoubleAnimation(1d, 0d, TimeSpan.FromMilliseconds(DesignTokens.DeleteFeedbackMs))
        {
            EasingFunction = FadeAnimation.ExitEasing,
            FillBehavior = FillBehavior.Stop,
        };

        for (var index = 0; index < BoardList.Items.Count; index++)
        {
            if (BoardList.ItemContainerGenerator.ContainerFromIndex(index)
                is not ListBoxItem container ||
                container.DataContext is not BoardItem item ||
                ids is not null && !ids.Contains(item.Id))
            {
                continue;
            }

            container.BeginAnimation(UIElement.OpacityProperty, null);
            container.SetValue(UIElement.OpacityProperty, 0d);
            container.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }

    private void RestoreDeletedCardFade()
    {
        for (var index = 0; index < BoardList.Items.Count; index++)
        {
            if (BoardList.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem container)
            {
                continue;
            }

            container.BeginAnimation(UIElement.OpacityProperty, null);
            container.SetValue(UIElement.OpacityProperty, 1d);
        }
    }

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 &&
            e.KeyboardDevice.Modifiers == ModifierKeys.None &&
            !_isClosing &&
            Keyboard.FocusedElement is not TextBoxBase &&
            _viewModel.IsPanelExpanded &&
            !IsCategoryNameEditActive() &&
            _viewModel.ActivePanel is { } renamePanel)
        {
            var activeTab = CategoryTabs().SingleOrDefault(candidate =>
                ReferenceEquals(candidate.DataContext, renamePanel));
            if (activeTab is not null)
            {
                BeginCategoryNameEdit(renamePanel, activeTab);
                e.Handled = true;
                return;
            }
        }

        // Ctrl+F 进入搜索(面板展开且无文本编辑焦点);Esc 在搜索态优先退出搜索。
        if (e.Key == Key.F &&
            e.KeyboardDevice.Modifiers == ModifierKeys.Control &&
            !_isClosing &&
            Keyboard.FocusedElement is not TextBoxBase &&
            _viewModel.IsPanelExpanded)
        {
            e.Handled = true;
            EnterSearchMode();
            return;
        }

        if (e.Key == Key.Escape &&
            !_isClosing &&
            _viewModel.IsSearchActive)
        {
            e.Handled = true;
            ExitSearchMode();
            return;
        }

        if (e.Key == Key.Z &&
            e.KeyboardDevice.Modifiers == ModifierKeys.Control &&
            !_isClosing &&
            Keyboard.FocusedElement is not TextBoxBase &&
            _viewModel.IsPanelExpanded)
        {
            e.Handled = true;
            await UndoLastDeleteFromPanelAsync();
            return;
        }

        // Ctrl+C 复制选中(交付第二通道):编辑态让路由给 TextBox,无选择时不拦截。
        if (TryCopySelectionWithCtrlC(e))
        {
            return;
        }

        if (e.Key == Key.Escape &&
            !_isClosing &&
            Keyboard.FocusedElement is not TextBoxBase &&
            _viewModel.IsPanelExpanded &&
            (BoardList.SelectedItems.Count > 0 || _isDeletePending))
        {
            ClearUserSelection();
            e.Handled = true;
            return;
        }

        if ((e.Key != Key.Back && e.Key != Key.Delete) ||
            _isClosing ||
            Keyboard.FocusedElement is TextBoxBase ||
            !_viewModel.IsPanelExpanded ||
            _viewModel.ActivePanel is not { } activePanel)
        {
            return;
        }

        var selected = CaptureSelectedItemIds();
        if (selected.Length == 0)
        {
            return;
        }

        e.Handled = true;
        await DeleteContentAsync(selected, activePanel.Category);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
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
}
