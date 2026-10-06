using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
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
    /// 会话跟踪挂载：用路由事件（handledEventsToo）接收列表内部 ScrollViewer
    /// 的滚动（不遍历模板找实例，虚拟化/模板演进都无感），并跟随窗口尺寸
    /// 变化刷新覆盖层位置。
    /// </summary>
    private void InitializeCardEditSessionTracking()
    {
        BoardList.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(BoardList_ScrollChanged),
            handledEventsToo: true);
        // 窗口缩放/贴边重排改变锚点容器在面板内的位置（无集合变化、滚动
        // 位移也可能为 0）：尺寸定稿后刷新一次，覆盖层保持对准容器。
        SizeChanged += (_, _) => RefreshCardEditSession();
    }

    private void BoardList_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // 垂直滚动改变锚点容器的可视位置：覆盖层重定位跟随；锚点滚出
        // 视口（容器被回收或落入虚拟化缓存区）时由刷新判定终结。延迟到
        // Loaded 优先级：ScrollChanged 时点容器回收/重排可能未定稿，同步
        // 判定会拿到中间态。范围/视口尺寸变化（无位移的布局收缩）不在
        // 这里触发，交给集合变化与窗口尺寸钩子。
        if (e.VerticalChange != 0)
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(RefreshCardEditSession));
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
        // 焦点兜底终结（切走应用、系统夺焦——新焦点离开本窗口）：会话有效性
        // 由锚点刷新单点负责，焦点在本窗口内迁移（IME 交互、点头部/搜索框
        // 等）不终结——那些路径要么有自己的视图钩子，要么锚点仍有效、编辑
        // 内容原地保留。不回焦——焦点正移向用户选择的新目标。关闭序列已在
        // 操作门封门前冲刷过编辑；封门后的销毁期失焦不得再注册操作。
        if (_isClosing ||
            e.NewFocus is DependencyObject newFocus && IsAncestorOf(newFocus))
        {
            return;
        }

        CommitCardTextEditing();
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
    /// 视图代际终结（换面板）：清理手势记忆并提交悬挂编辑。手势记忆与是否
    /// 在编辑无关——未编辑时切换视图同样要作废双击痕迹/按压会话，防跨视图
    /// 幽灵双击/幽灵选中。在搜索退出与焦点转移之前调用——它们的中间状态
    /// 不应干扰提交流径。
    /// </summary>
    private void EndCardEditSessionForViewChange()
    {
        ClearCardGestureMemory();
        CommitCardTextEditing();
    }

    /// <summary>视图代际清理：跨视图的双击痕迹与按压会话一律作废。</summary>
    private void ClearCardGestureMemory()
    {
        _lastCardClick = null;
        ResetCardPressState();
    }

    /// <summary>
    /// 锚点条目仍在当前面板、容器已实现且未被滚出视口。容器「已实现」
    /// 不等于「可见」：虚拟化缓存区的容器仍实现但锚点已滚出视口，覆盖层
    /// 无法对准容器（定位的 Y 夹 0 会钉在列表顶部压住首卡），按锚点失效
    /// 终结。
    /// </summary>
    private ListBoxItem? FindEditingAnchorContainer(Guid itemId)
    {
        if (_viewModel.ActivePanel?.Items is not { } items ||
            items.FirstOrDefault(item => item.Id == itemId) is not { } anchor ||
            BoardList.ItemContainerGenerator.ContainerFromItem(anchor)
                is not ListBoxItem container)
        {
            return null;
        }

        var top = container.TranslatePoint(new Point(0, 0), BoardList).Y;
        return double.IsNaN(top) ||
            top >= BoardList.ActualHeight ||
            top + container.ActualHeight <= 0
            ? null
            : container;
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
