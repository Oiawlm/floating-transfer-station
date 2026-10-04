using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Views;

/// <summary>
/// 卡片内容就地编辑（2026-09-25 用户补充方向「全部卡片内容可编辑」切片 1：文字卡）：
/// 双击文字卡在原位覆盖编辑器；Enter 保存、Esc 取消、失焦保存；空白视为取消
/// （不允许把内容编辑为空，与插件「不得清空」精神一致）。保存走原子持久化，
/// 失败还原原文本并提示。
/// 会话生命周期锚点有效性驱动（1.18.0 重构）：会话有效 ⟺ 锚点条目仍在当前
/// <c>ActivePanel.Items</c> 且容器已实现且覆盖层对准容器。焦点迁移（被手势层
/// 自己压制）不再是终结源，只剩窗口失活兜底；视图代际（换面板、过滤、锚点
/// 移除、锚点滚出虚拟化窗口）由 <see cref="RefreshCardEditSession"/> 单点终结，
/// 滚动/重排只重定位覆盖层跟随。
/// </summary>
public partial class MainWindow
{
    private Guid? _editingCardItemId;

    /// <summary>
    /// 滚动跟随挂载：用路由事件（handledEventsToo）接收列表内部 ScrollViewer
    /// 的滚动，不遍历模板找实例（虚拟化/模板演进都无感）。
    /// </summary>
    private void InitializeCardEditSessionTracking()
    {
        BoardList.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(BoardList_ScrollChanged),
            handledEventsToo: true);
    }

    private void BoardList_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // 垂直滚动改变锚点容器的可视位置：覆盖层重定位跟随；锚点滚出
        // 虚拟化窗口（容器被回收）时由刷新判定终结。范围/视口尺寸变化
        // （无位移的布局收缩）交给集合变化钩子，不在这里重复触发。
        if (e.VerticalChange != 0)
        {
            RefreshCardEditSession();
        }
    }

    private void CardTextEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitCardTextEditing(restoreFocusToList: true);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelCardTextEditing(restoreFocusToList: true);
        }
    }

    private void CardTextEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 焦点兜底终结（窗口失活、点击面板外控件）：会话有效性已由锚点刷新
        // 单点负责，这里只补齐锚点刷新覆盖不到的真实失焦；不回焦——焦点
        // 正移向用户选择的新目标。关闭序列已在操作门封门前冲刷过编辑；
        // 封门后的销毁期失焦不得再注册操作。
        if (_isClosing)
        {
            return;
        }

        CommitCardTextEditing();
    }

    /// <summary>
    /// 悬挂会话冲刷入口：提交仍打开的卡片编辑（空白同样视为取消）。
    /// 关闭序列与手势层按下共用。
    /// </summary>
    private void CommitPendingCardTextEditing()
    {
        if (_editingCardItemId is not null)
        {
            CommitCardTextEditing();
        }
    }

    /// <summary>
    /// 双击进入编辑：编辑器覆盖到卡片容器位置，种子为当前文本。已有会话时
    /// 先同步完成上一提交（接续编辑另一张卡），编辑器被所有卡复用，顺序必须
    /// 严格同步，防止新会话被旧会话的焦点回调提前提交。
    /// </summary>
    private void BeginCardTextEditing(BoardItem item, ListBoxItem container)
    {
        if (_isClosing ||
            _viewModel.IsSearchActive ||
            IsCategoryNameEditActive())
        {
            return;
        }

        if (_editingCardItemId is not null)
        {
            CommitCardTextEditing();
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

    /// <summary>
    /// 会话刷新（滚动、集合变化、搜索过滤共用）：锚点容器已实现 → 覆盖层
    /// 重定位跟随；锚点失效（条目不在当前面板、被过滤移出视图或容器被
    /// 虚拟化回收）→ 终结会话并提交。重定位优先、仅锚点失效才冲刷。
    /// </summary>
    private void RefreshCardEditSession()
    {
        if (_editingCardItemId is not { } itemId)
        {
            return;
        }

        if (FindEditingAnchorContainer(itemId) is { } container)
        {
            PositionCardEditorOver(container);
        }
        else
        {
            EndCardEditSessionForViewChange();
        }
    }

    /// <summary>
    /// 视图代际终结（换面板）：提交悬挂编辑并清理手势记忆，防跨视图幽灵
    /// 双击/幽灵选中。在搜索退出与焦点转移之前调用——它们的中间状态
    /// 不应干扰提交流径。
    /// </summary>
    private void EndCardEditSessionForViewChange()
    {
        if (_editingCardItemId is null)
        {
            return;
        }

        CommitCardTextEditing();
        _lastCardClick = null;
        ResetCardPressState();
    }

    /// <summary>锚点条目仍在当前面板且容器已实现（未被过滤/虚拟化回收）。</summary>
    private ListBoxItem? FindEditingAnchorContainer(Guid itemId) =>
        _viewModel.ActivePanel?.Items is { } items &&
        items.FirstOrDefault(item => item.Id == itemId) is { } anchor
            ? BoardList.ItemContainerGenerator.ContainerFromItem(anchor) as ListBoxItem
            : null;

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

    private async void CommitCardTextEditing(bool restoreFocusToList = false)
    {
        if (_editingCardItemId is not { } itemId)
        {
            return;
        }

        // 提交顺序锁定：先读文本，Hide 置 null 防二次提交（焦点回调重入），
        // 再注册持久化。UpdateItemTextAsync 的同步段（等值短路、内存更新、
        // 操作门注册）在返回前完成；关闭序列依赖这一点在封门前排队
        // （BoardOperationGate.SealAndRunAsync 会先排空已注册操作再执行
        // 最终保存），因此 async void 不丢最后一笔编辑。
        var newText = CardTextEditor.Text;
        HideCardTextEditor(restoreFocusToList);
        if (string.IsNullOrWhiteSpace(newText))
        {
            ShowStatus("内容不能为空，本次编辑已取消。");
            return;
        }

        await _mutations.UpdateItemTextAsync(itemId, newText);
    }

    /// <summary>测试缝:绕过键位事件直接提交当前编辑(与 Enter 路径同函数)。</summary>
    internal void CommitCardTextForTest() => CommitCardTextEditing();

    private void CancelCardTextEditing(bool restoreFocusToList = false)
    {
        if (_editingCardItemId is null)
        {
            return;
        }

        HideCardTextEditor(restoreFocusToList);
    }

    private void HideCardTextEditor(bool restoreFocusToList)
    {
        _editingCardItemId = null;
        CardTextEditorHost.Visibility = Visibility.Collapsed;
        // 显式结束（Enter/Esc）回焦列表；冲刷路径不抢焦点——焦点正移向
        // 用户选择的新目标控件，从那里夺回会打断输入。
        if (restoreFocusToList)
        {
            BoardList.Focus();
        }
    }
}
