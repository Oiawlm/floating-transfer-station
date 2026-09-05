using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.ViewModels;

namespace FloatingTransferStation.Views;

public partial class MainWindow : Window
{
    private readonly HashSet<TextBox> _activeCategoryNameCompositions = [];

    private void InitializeCategoryNameEditing()
    {
        AddHandler(
            TextCompositionManager.PreviewTextInputStartEvent,
            new TextCompositionEventHandler(CategoryNameEditor_CompositionStartedOrUpdated),
            true);
        AddHandler(
            TextCompositionManager.PreviewTextInputUpdateEvent,
            new TextCompositionEventHandler(CategoryNameEditor_CompositionStartedOrUpdated),
            true);
        AddHandler(
            TextCompositionManager.PreviewTextInputEvent,
            new TextCompositionEventHandler(CategoryNameEditor_CompositionCompleted),
            true);
    }

    private void CategoryTab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isClosing ||
            e.ChangedButton != MouseButton.Left ||
            e.ClickCount < 2 ||
            FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null ||
            sender is not Border { DataContext: CategoryViewModel category } tab)
        {
            return;
        }

        BeginCategoryNameEdit(category, tab);
        e.Handled = true;
    }

    private void BeginCategoryNameEdit(CategoryViewModel category, Border tab)
    {
        _viewModel.BeginCategoryNameEdit(category);
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (!category.IsEditingName ||
                    FindDescendant<TextBox>(tab) is not { } editor)
                {
                    return;
                }

                editor.Focus();
                editor.SelectAll();
            }));
    }

    private void CategoryNameEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox editor ||
            editor.DataContext is not CategoryViewModel category ||
            !category.IsEditingName ||
            _activeCategoryNameCompositions.Contains(editor))
        {
            return;
        }

        var limited = BoardCategoryCatalog.LimitDisplayName(editor.Text);
        if (limited == editor.Text)
        {
            return;
        }

        editor.Text = limited;
        editor.CaretIndex = editor.Text.Length;
    }

    private void CategoryNameEditor_CompositionStartedOrUpdated(
        object sender,
        TextCompositionEventArgs e)
    {
        if (e.OriginalSource is TextBox
            {
                DataContext: CategoryViewModel { IsEditingName: true }
            } editor)
        {
            _activeCategoryNameCompositions.Add(editor);
        }
    }

    private void CategoryNameEditor_CompositionCompleted(
        object sender,
        TextCompositionEventArgs e)
    {
        if (e.OriginalSource is TextBox editor)
        {
            _activeCategoryNameCompositions.Remove(editor);
        }
    }

    private void CategoryNameEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox editor ||
            editor.DataContext is not CategoryViewModel category)
        {
            return;
        }

        if (_activeCategoryNameCompositions.Contains(editor))
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitCategoryNameEdit(category, editor.Text);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _activeCategoryNameCompositions.Remove(editor);
            _viewModel.CancelCategoryNameEdit(category);
            ReconcileSurfaceAfterCategoryNameEdit();
        }
    }

    private void CategoryNameEditor_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: CategoryViewModel category } editor)
        {
            _activeCategoryNameCompositions.Remove(editor);
            CommitCategoryNameEdit(category, editor.Text);
        }
    }

    private void CommitCategoryNameEdit(CategoryViewModel category, string draftName)
    {
        if (!category.IsEditingName)
        {
            return;
        }

        var name = _viewModel.EndCategoryNameEdit(category, draftName);
        ReconcileSurfaceAfterCategoryNameEdit();
        if (name == category.DisplayName)
        {
            return;
        }

        TrackPendingOperation(SaveCategoryNameAsync(category, name));
    }

    private async Task SaveCategoryNameAsync(CategoryViewModel category, string name)
    {
        await _settingsSaveGate.WaitAsync();
        try
        {
            var originalName = category.DisplayName;
            _settings = _settings.WithCategoryName(category.Category, name);
            try
            {
                await _store.SaveSettingsAsync(_settings);
                _viewModel.ApplyCategoryName(category, name);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _settings = _settings.WithCategoryName(category.Category, originalName);
                ShowStatus("分类名称未保存，已恢复原名称。");
            }
        }
        finally
        {
            _settingsSaveGate.Release();
        }
    }

    private bool IsCategoryNameEditActive() =>
        _viewModel.Categories.Any(category => category.IsEditingName);

    private void ReconcileSurfaceAfterCategoryNameEdit()
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
}
