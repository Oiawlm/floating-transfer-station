using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;

namespace FloatingTransferStation.Views;

/// <summary>
/// 内容搜索切片 1（2026-09-25 设计草案）：当前分类的只读过滤视图。
/// 头部放大镜或 Ctrl+F 进入；Esc / 关闭按钮退出，切换分类自动退出。
/// 过滤作用于 BoardList 的集合视图（Filter 谓词），源集合、顺序、置顶分区
/// 与持久化零改动；过滤期间禁用拖拽与 Ctrl+A（拖拽落点按过滤视图计算会错位）。
/// </summary>
public partial class MainWindow
{
    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        EnterSearchMode();
    }

    private void SearchCloseButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ExitSearchMode();
    }

    private void SearchClearButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ClearSearchConditions();
    }

    private void SearchClearFilterButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ClearSearchConditions();
        FocusSearchInput();
    }

    private void SearchInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        _viewModel.SearchText = SearchInput.Text;
        UpdateSearchInputHint();
        ApplySearchFilter();
    }

    private void SearchInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 输入框内按 Esc 直接退出搜索态（窗口级 Esc 分支因 TextBox 焦点不生效）。
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            ExitSearchMode();
        }
    }

    private void SearchChip_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var imagesOnly = SearchImageChip.IsChecked == true;
        var textOnly = SearchTextChip.IsChecked == true;
        // 两个 chip 都选或都不选 = 全部；恰好一个 = 该类型。
        _viewModel.SearchType = imagesOnly && !textOnly
            ? SearchTypeFilter.Images
            : textOnly && !imagesOnly
                ? SearchTypeFilter.TextOnly
                : SearchTypeFilter.All;
        ApplySearchFilter();
    }

    /// <summary>进入搜索态：仅板卡分类（复盘标签不参与，冻结）；清选择、应用过滤、聚焦输入框。</summary>
    internal void EnterSearchMode()
    {
        if (_isClosing ||
            !_viewModel.IsPanelExpanded ||
            _viewModel.IsSearchActive ||
            _viewModel.ActivePanel is not { } panel ||
            panel.Category == DailyReviewMigration.ReviewCategory)
        {
            return;
        }

        ClearUserSelection();
        _viewModel.EnterSearch();
        UpdateSearchPresentation();
        ApplySearchFilter();
        FocusSearchInput();
    }

    /// <summary>退出搜索态：清条件、还原列表视图与头部，恢复既有收起节奏。</summary>
    internal void ExitSearchMode()
    {
        if (!_viewModel.IsSearchActive)
        {
            return;
        }

        _viewModel.ExitSearch();
        ClearSearchControls();
        UpdateSearchPresentation();
        ApplySearchFilter();
        // 隐藏搜索条后把焦点交还列表:隐藏的输入框可能仍持有逻辑焦点并继续抑制收起。
        BoardList.Focus();
        ReconcileSurfaceAfterEditing();
    }

    private void ClearSearchConditions()
    {
        SearchInput.Clear();
        SearchImageChip.IsChecked = false;
        SearchTextChip.IsChecked = false;
        _viewModel.SearchType = SearchTypeFilter.All;
    }

    private void ClearSearchControls() => ClearSearchConditions();

    private void FocusSearchInput()
    {
        if (SearchInput.Focus())
        {
            SearchInput.CaretIndex = SearchInput.Text.Length;
        }
    }

    private void UpdateSearchInputHint() =>
        SearchInputHint.Visibility =
            string.IsNullOrEmpty(SearchInput.Text) ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateSearchPresentation()
    {
        var searching = _viewModel.IsSearchActive;
        SearchBar.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        HeaderActions.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
        if (searching)
        {
            UpdateSearchInputHint();
        }
    }

    /// <summary>
    /// 把当前条件应用到 BoardList 的集合视图：只读过滤，不改源集合。
    /// 条件为空（无关键词且类型=全部）时移除过滤还原完整列表。
    /// </summary>
    private void ApplySearchFilter()
    {
        if (_viewModel.ActivePanel is null)
        {
            return;
        }

        var keyword = _viewModel.SearchText.Trim();
        var type = _viewModel.SearchType;
        var active = _viewModel.IsSearchActive;
        if (!active || (keyword.Length == 0 && type == SearchTypeFilter.All))
        {
            BoardList.Items.Filter = null;
        }
        else
        {
            BoardList.Items.Filter = item => MatchesSearchFilter(item, keyword, type);
        }

        UpdateSearchEmptyHint();
    }

    private static bool MatchesSearchFilter(object item, string keyword, SearchTypeFilter type)
    {
        if (item is not BoardItem boardItem)
        {
            return false;
        }

        var kindOk = type switch
        {
            SearchTypeFilter.Images => boardItem.Kind == BoardItemKind.Image,
            SearchTypeFilter.TextOnly => boardItem.Kind == BoardItemKind.Text,
            _ => true
        };
        if (!kindOk)
        {
            return false;
        }

        // 关键词只匹配文字内容；图片卡片无文本，仅在无关键词时由类型筛选取舍。
        return keyword.Length == 0 ||
            (boardItem.Kind == BoardItemKind.Text &&
             boardItem.Text?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true);
    }

    private void UpdateSearchEmptyHint()
    {
        var visible = _viewModel.IsSearchActive && BoardList.Items.IsEmpty;
        SearchEmptyHint.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }
}
