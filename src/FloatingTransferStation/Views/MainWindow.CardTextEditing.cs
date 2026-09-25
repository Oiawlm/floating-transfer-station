using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Views;

/// <summary>
/// 卡片内容就地编辑（2026-09-25 用户补充方向「全部卡片内容可编辑」切片 1：文字卡）：
/// 双击文字卡在原位覆盖编辑器；Enter 保存、Esc 取消、失焦保存；空白视为取消
/// （不允许把内容编辑为空，与插件「不得清空」精神一致）。保存走原子持久化，
/// 失败还原原文本并提示。编辑期间面板收起抑制与复盘编辑器同构（键盘焦点保持原因）。
/// </summary>
public partial class MainWindow
{
    private Guid? _editingCardItemId;

    private void BoardList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var container = FindAncestor<ListBoxItem>(source);
        if (container?.Content is not BoardItem { Kind: BoardItemKind.Text } item)
        {
            return;
        }

        e.Handled = true;
        BeginCardTextEditing(item, container);
    }

    private void CardTextEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitCardTextEditing();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelCardTextEditing();
        }
    }

    private void CardTextEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 焦点移出编辑器即提交（与分类改名失焦提交一致）。
        CommitCardTextEditing();
    }

    /// <summary>双击进入编辑：编辑器覆盖到卡片容器位置，种子为当前文本。</summary>
    private void BeginCardTextEditing(BoardItem item, ListBoxItem container)
    {
        if (_isClosing ||
            _viewModel.IsSearchActive ||
            IsCategoryNameEditActive() ||
            _editingCardItemId is not null)
        {
            return;
        }

        _editingCardItemId = item.Id;
        CardTextEditor.Text = item.Text ?? string.Empty;
        PositionCardEditorOver(container);
        CardTextEditorHost.Visibility = Visibility.Visible;
        UpdateLayout();
        if (CardTextEditor.Focus())
        {
            CardTextEditor.CaretIndex = CardTextEditor.Text.Length;
            CardTextEditor.SelectAll();
        }
    }

    private void PositionCardEditorOver(ListBoxItem container)
    {
        // 容器在列表坐标系中的位置换算到面板内容区(Grid.Row=1)。
        var position = container.TranslatePoint(new Point(0, 0), PanelContentHost);
        CardTextEditorHost.Margin = new Thickness(
            12 + position.X,
            Math.Max(0, position.Y),
            12,
            0);
        CardTextEditorHost.MinHeight = container.ActualHeight;
    }

    private async void CommitCardTextEditing()
    {
        if (_editingCardItemId is not { } itemId)
        {
            return;
        }

        var newText = CardTextEditor.Text;
        HideCardTextEditor();
        if (string.IsNullOrWhiteSpace(newText))
        {
            ShowStatus("内容不能为空，本次编辑已取消。");
            return;
        }

        await _mutations.UpdateItemTextAsync(itemId, newText);
    }

    /// <summary>测试缝:绕过键位事件直接提交当前编辑(与 Enter 路径同函数)。</summary>
    internal void CommitCardTextForTest() => CommitCardTextEditing();

    private void CancelCardTextEditing()
    {
        if (_editingCardItemId is null)
        {
            return;
        }

        HideCardTextEditor();
    }

    private void HideCardTextEditor()
    {
        _editingCardItemId = null;
        CardTextEditorHost.Visibility = Visibility.Collapsed;
        BoardList.Focus();
    }
}
