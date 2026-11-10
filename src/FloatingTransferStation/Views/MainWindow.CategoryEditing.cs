using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FloatingTransferStation.Models;
using FloatingTransferStation.ViewModels;

namespace FloatingTransferStation.Views;

/// <summary>
/// 分类名改名会话（共享协调器机器，契约 #49 修订）：会话状态机（Begin/限长/IME 组合守卫/
/// Enter 提交/Esc 取消/失焦提交）由 <see cref="InlineRenameSession{TSubject}"/> 承载，
/// 本面只注入 subject 参数（分类集/限长/查询缝）与乐观保存单点（SaveCategoryNameAsync）；
/// 事件入口沿 InitializeCardEditSessionTracking 先例做窗口级挂接（编辑器在 DataTemplate
/// 内，不经 XAML 特性散挂）。「点击编辑器外部＝提交」隧道与面板切换冲刷经协调器登记清单
/// 统一分发，不再各自感知分类面。
/// </summary>
public partial class MainWindow : Window
{
    private readonly HashSet<TextBox> _activeCategoryNameCompositions = [];

    private InlineRenameSession<CategoryViewModel> _categoryRenameSession = null!;

    private void InitializeCategoryNameEditing()
    {
        _categoryRenameSession = new InlineRenameSession<CategoryViewModel>(
            Dispatcher,
            _activeCategoryNameCompositions,
            subjects: () => _viewModel.Categories,
            isEditing: category => category.IsEditingName,
            findEditor: TryGetCategoryNameEditor,
            findDescendantEditor: FindDescendant<TextBox>,
            limitDraft: BoardCategoryCatalog.LimitDisplayName,
            beginSession: category => _viewModel.BeginCategoryNameEdit(category),
            endSession: (category, draftName) => _viewModel.EndCategoryNameEdit(category, draftName),
            cancelSession: category => _viewModel.CancelCategoryNameEdit(category),
            saveIfChanged: (category, name) =>
            {
                if (name == category.DisplayName)
                {
                    return;
                }

                TrackPendingOperation(SaveCategoryNameAsync(category, name));
            },
            afterSessionEnd: ReconcileSurfaceAfterEditing);
        AddHandler(
            Keyboard.KeyDownEvent,
            new KeyEventHandler(CategoryNameEditor_KeyDown));
        AddHandler(
            Keyboard.LostKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(CategoryNameEditor_LostKeyboardFocus));
        AddHandler(
            TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler(CategoryNameEditor_TextChanged));
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

    private void BeginCategoryNameEdit(CategoryViewModel category, Border tab) =>
        _categoryRenameSession.Begin(category, tab);

    private void CategoryNameEditor_TextChanged(object sender, TextChangedEventArgs e) =>
        _categoryRenameSession.HandleTextChanged(e);

    private void CategoryNameEditor_CompositionStartedOrUpdated(
        object sender,
        TextCompositionEventArgs e) =>
        _categoryRenameSession.HandleCompositionStartedOrUpdated(e);

    private void CategoryNameEditor_CompositionCompleted(
        object sender,
        TextCompositionEventArgs e) =>
        _categoryRenameSession.HandleCompositionCompleted(e);

    private void CategoryNameEditor_KeyDown(object sender, KeyEventArgs e) =>
        _categoryRenameSession.HandleKeyDown(e);

    private void CategoryNameEditor_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e) =>
        _categoryRenameSession.HandleLostKeyboardFocus(e);

    private void CommitCategoryNameEdit(CategoryViewModel category, string draftName) =>
        _categoryRenameSession.Commit(category, draftName);

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

    /// <summary>指定分类的名字编辑器（协调器查询缝）：编辑中的分类在标签栏/收起把手中的
    /// 名字编辑器。编辑器未实现（模板未挂）为 null——会话交回失焦等既有通道处置。</summary>
    private TextBox? TryGetCategoryNameEditor(CategoryViewModel category) =>
        FindCategoryNameEditor(CategoryRail, category) ??
        FindCategoryNameEditor(CollapsedCategoryHandle, category);

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
