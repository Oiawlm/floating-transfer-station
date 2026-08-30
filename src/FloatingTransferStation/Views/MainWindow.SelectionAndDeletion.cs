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
    public static RoutedUICommand BatchPinCommand { get; } = new(
        "批量置顶或取消置顶",
        nameof(BatchPinCommand),
        typeof(MainWindow),
        new InputGestureCollection
        {
            new KeyGesture(Key.P, ModifierKeys.Control)
        });

    private bool _isBatchPinPending;
    private long _selectionClearVersion;

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

        var selectedBefore = CaptureSelectedItemIds();
        var selectionClearVersion = _selectionClearVersion;
        var category = item.Category;
        var offset = CurrentScrollOffset();
        await _mutations.SetPinnedAsync([item.Id], !item.IsPinned);
        await Dispatcher.InvokeAsync(
            () =>
            {
                if (selectionClearVersion == _selectionClearVersion)
                {
                    RestoreSelection(selectedBefore);
                }

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
        e.CanExecute =
            BoardList is not null &&
            BoardList.SelectedItems.Count > 0 &&
            !_isClosing &&
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
            _viewModel.ActivePanel is not { Items.Count: > 0 })
        {
            return;
        }

        BoardList.SelectAll();
        e.Handled = true;
    }

    private async Task ApplyBatchPinSelectionAsync()
    {
        if (_isClosing ||
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
        var selectionClearVersion = _selectionClearVersion;
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

                    if (selectionClearVersion == _selectionClearVersion)
                    {
                        RestoreSelection(selectedBefore);
                    }

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
        HeaderActions.Opacity = visible ? 1d : 0d;
        HeaderActions.IsHitTestVisible = visible;
    }

    private async void ResetWindowButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var work = CurrentWorkArea();
        _settings = _settings.ResetToDefault(work.Width, work.Height);
        ApplyPlacement(WindowController.Expanded(work, _settings));
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
        if (_isClosing || _viewModel.ActivePanel is not { } activePanel)
        {
            return;
        }

        var targetCategory = activePanel.Category;
        var selectedBefore = CaptureSelectedItemIds();
        if (selectedBefore.Length > 0)
        {
            await DeleteSelectedItemsAsync(selectedBefore, targetCategory);
            return;
        }

        var success = await _mutations.ClearCategoryAsync(targetCategory);
        await Dispatcher.InvokeAsync(
            () =>
            {
                if (success)
                {
                    BoardList.UnselectAll();
                }
                else if (_viewModel.ActivePanel?.Category == targetCategory)
                {
                    RestoreSelection(selectedBefore);
                }
            },
            DispatcherPriority.Send);
    }

    private async Task DeleteSelectedItemsAsync(
        Guid[] selectedBefore,
        BoardCategory targetCategory)
    {
        if (selectedBefore.Length == 0)
        {
            return;
        }

        var success = await _mutations.DeleteManyAsync(selectedBefore);
        await Dispatcher.InvokeAsync(
            () =>
            {
                if (_viewModel.ActivePanel?.Category != targetCategory)
                {
                    return;
                }

                if (success)
                {
                    BoardList.UnselectAll();
                }
                else
                {
                    RestoreSelection(selectedBefore);
                }
            },
            DispatcherPriority.Send);
    }

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape &&
            !_isClosing &&
            Keyboard.FocusedElement is not TextBoxBase &&
            _viewModel.IsPanelExpanded &&
            BoardList.SelectedItems.Count > 0)
        {
            _selectionClearVersion++;
            BoardList.UnselectAll();
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
        await DeleteSelectedItemsAsync(selected, activePanel.Category);
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
