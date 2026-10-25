using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
            ReconcileSurfaceAfterEditing();
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
        ReconcileSurfaceAfterEditing();
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

    /// <summary>当前改名会话的编辑器（「点击编辑器外部＝提交」协调查询缝）：
    /// 编辑中的分类在标签栏/收起把手中的名字编辑器。改名会话不存在时为 null；
    /// 会话在而编辑器未实现（模板未挂）同样为 null——调用方据此跳过本次触发，
    /// 会话交回失焦等既有通道处置。</summary>
    private TextBox? TryGetActiveCategoryNameEditor()
    {
        var editingCategory = _viewModel.Categories.FirstOrDefault(
            category => category.IsEditingName);
        if (editingCategory is null)
        {
            return null;
        }

        return FindCategoryNameEditor(CategoryRail, editingCategory) ??
            FindCategoryNameEditor(CollapsedCategoryHandle, editingCategory);
    }

    /// <summary>按 DataContext 引用等值在子树内找该分类的名字编辑器（模板内
    /// TextBox 不带名，与 FindCardEditor 同法递归；两处宿主树都很小，无虚拟化）。</summary>
    private static TextBox? FindCategoryNameEditor(
        DependencyObject parent,
        CategoryViewModel category)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is TextBox { DataContext: CategoryViewModel candidate } &&
                ReferenceEquals(candidate, category))
            {
                return (TextBox)child;
            }

            if (FindCategoryNameEditor(child, category) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }
}
