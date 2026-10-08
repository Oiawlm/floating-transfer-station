using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Views;

/// <summary>
/// 卡片内容就地编辑（1.22.0 重构：编辑器移入卡片 DataTemplate，显示态与编辑态
/// 由条目级 <see cref="BoardItem.IsEditing"/> 原位切换）：Enter 保存、Esc 取消、
/// 失焦保存；空白视为取消（不允许把内容编辑为空，与插件「不得清空」精神一致）。
/// 保存走原子持久化，失败还原原文本并提示。草稿在条目模型上（双向绑定），容器
/// 被虚拟化回收后冲刷路径仍拿得到全文。会话生命周期由锚点有效性单点驱动：
/// 锚点条目仍在当前 <c>ActivePanel.Items</c> 且容器已实现且未滚出视口，否则终结
/// 并提交（换面板、过滤、集合变化、滚动、窗口尺寸变化共用同一判定）。
/// </summary>
public partial class MainWindow
{
    private Guid? _editingCardItemId;

    /// <summary>IME 组合中的编辑器：组合期 Enter/Esc 属输入法操作，不触发提交/取消。</summary>
    private readonly HashSet<BoardItem> _activeCardTextCompositions = [];

    /// <summary>
    /// 会话跟踪挂载：滚动/尺寸变化只做锚点有效性判定（无定位职责）；
    /// 编辑器键位与失焦经窗口级路由事件挂接（编辑器在 ResourceDictionary 的
    /// DataTemplate 内，不能声明 XAML 事件特性；按 OriginalSource 过滤编辑器）。
    /// </summary>
    private void InitializeCardEditSessionTracking()
    {
        BoardList.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(BoardList_ScrollChanged),
            handledEventsToo: true);
        AddHandler(
            Keyboard.KeyDownEvent,
            new KeyEventHandler(CardTextEditor_KeyDown));
        AddHandler(
            Keyboard.LostKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(CardTextEditor_LostKeyboardFocus));
        AddHandler(
            TextCompositionManager.PreviewTextInputStartEvent,
            new TextCompositionEventHandler(CardTextEditor_CompositionStartedOrUpdated),
            true);
        AddHandler(
            TextCompositionManager.PreviewTextInputUpdateEvent,
            new TextCompositionEventHandler(CardTextEditor_CompositionStartedOrUpdated),
            true);
        AddHandler(
            TextCompositionManager.PreviewTextInputEvent,
            new TextCompositionEventHandler(CardTextEditor_CompositionCompleted),
            true);
        // 窗口缩放/贴边重排可能把锚点容器挤出视口或挤出虚拟化窗口（无集合
        // 变化、滚动位移也可能为 0）：同样交给锚点判定单点终结。
        SizeChanged += (_, _) => RefreshCardEditSession();
    }

    private void BoardList_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // 垂直滚动可能让锚点滚出视口（容器被回收或落入虚拟化缓存区）。
        // 延迟到 Loaded 优先级：ScrollChanged 时点容器回收/重排可能未定稿，
        // 同步判定会拿到中间态。范围/视口尺寸变化交给集合变化与窗口尺寸钩子。
        if (e.VerticalChange != 0)
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(RefreshCardEditSession));
        }
    }

    private void CardTextEditor_CompositionStartedOrUpdated(
        object sender,
        TextCompositionEventArgs e)
    {
        if (e.OriginalSource is TextBox
            {
                DataContext: BoardItem { IsEditing: true } item
            })
        {
            _activeCardTextCompositions.Add(item);
        }
    }

    private void CardTextEditor_CompositionCompleted(
        object sender,
        TextCompositionEventArgs e)
    {
        if (e.OriginalSource is TextBox { DataContext: BoardItem item })
        {
            _activeCardTextCompositions.Remove(item);
        }
    }

    private void CardTextEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is not TextBox { DataContext: BoardItem item } ||
            !item.IsEditing ||
            _activeCardTextCompositions.Contains(item))
        {
            return;
        }

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
        // 焦点兜底终结（切走应用、系统夺焦——新焦点离开本窗口）：焦点在本窗口
        // 内迁移（IME 交互、点头部/搜索框/本卡置顶按钮等）不终结——编辑会话由
        // 条目状态承载，编辑内容原地保留。会话已在提交/取消中清理时（锚点为
        // null）此回调是折叠引发的重入，直接跳过。不回焦——焦点正移向用户
        // 选择的新目标。关闭序列已在操作门封门前冲刷过编辑；封门后的销毁期
        // 失焦不得再注册操作。
        if (e.OriginalSource is not TextBox { DataContext: BoardItem } ||
            _isClosing ||
            _editingCardItemId is null ||
            e.NewFocus is DependencyObject newFocus && IsAncestorOf(newFocus))
        {
            return;
        }

        CommitCardTextEditing();
    }

    /// <summary>
    /// 双击进入编辑：草稿种子为全文，可见性由条目状态驱动原位切换。已有会话时
    /// 先同步完成上一提交（接续编辑另一张卡），顺序必须严格同步，防止新会话被
    /// 旧会话的焦点回调提前提交。
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
        // 组合标记按会话重置：上次会话被中断残留的标记不得让本次 Enter/Esc 失效。
        _activeCardTextCompositions.Remove(item);
        item.BeginTextEdit();
        // 焦点在模板应用后注入（照分类改名范式）；失败不回滚——可见性已由
        // 条目状态驱动，稍后的点击仍可把焦点送入编辑器。
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (!item.IsEditing ||
                    FindDescendant<TextBox>(container) is not { } editor ||
                    !ReferenceEquals(editor.DataContext, item))
                {
                    return;
                }

                editor.Focus();
                editor.SelectAll();
            }));
    }

    /// <summary>
    /// 会话刷新（滚动、集合变化、搜索过滤、窗口尺寸共用）：锚点条目仍在当前
    /// 面板、容器已实现且未滚出视口 → 会话原地保留（无定位职责）；锚点失效
    /// → 终结会话并提交。
    /// </summary>
    private void RefreshCardEditSession()
    {
        if (_editingCardItemId is { } itemId && FindEditingAnchorContainer(itemId) is null)
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
    /// 不等于「可见」：虚拟化缓存区的容器仍实现但锚点已滚出视口，编辑器
    /// 对用户已不可达（README：滚动使编辑卡离开视图时自动提交），按锚点
    /// 失效终结。
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

    private async void CommitCardTextEditing(bool restoreFocusToList = false)
    {
        if (_editingCardItemId is not { } itemId)
        {
            return;
        }

        // 提交顺序锁定（T7 评审修正）：先取草稿，再清锚点防二次提交（折叠引发
        // 焦点回调重入时锚点已空、直接返回），最后 EndTextEdit 收起编辑态——
        // 草稿读取必须在 EndTextEdit 之前（它会清掉 DraftText）。IME 组合标记
        // 一并清理（组合中会话被终结时组合完成事件可能不再到来，残留会使同卡
        // 下次编辑的 Enter/Esc 失效）。编辑器折叠前先把焦点显式还给列表：折叠
        // 持有键盘焦点的元素时 WPF 会在列表内重选焦点并 BringIntoView（滚动
        // 跳变）；列表本身不承载滚动语义。焦点迁移与 EndTextEdit 的顺序不可换。
        // UpdateItemTextAsync 的同步段（等值短路、内存更新、操作门注册）在返回
        // 前完成；关闭序列依赖这一点在封门前排队（BoardOperationGate.
        // SealAndRunAsync 会先排空已注册操作再执行最终保存），因此 async void
        // 不丢最后一笔编辑。
        var editingItem = FindEditingItem(itemId);
        var newText = editingItem?.DraftText ?? string.Empty;
        var editorHoldsFocus = TryGetEditingCardContainer() is { } container &&
            FindDescendant<TextBox>(container) is { } editor &&
            editor.IsKeyboardFocused;
        _editingCardItemId = null;
        if (editingItem is not null)
        {
            _activeCardTextCompositions.Remove(editingItem);
        }

        if (restoreFocusToList || editorHoldsFocus)
        {
            BoardList.Focus();
        }

        editingItem?.EndTextEdit();
        if (editingItem is null)
        {
            // 锚点条目已不在板卡上（删除竞态）：会话直接结束，无内容可存。
            return;
        }

        if (string.IsNullOrWhiteSpace(newText))
        {
            ShowStatus("内容不能为空，本次编辑已取消。");
            return;
        }

        await _mutations.UpdateItemTextAsync(itemId, newText);
    }

    /// <summary>锚点条目优先取当前面板（虚拟化回收后仍是同一实例），跨面板兜底查全板；
    /// 删除竞态下兜底取容器上的条目实例（组合标记清理仍需该引用，Id 不符视为不存在）。</summary>
    private BoardItem? FindEditingItem(Guid itemId) =>
        FindEditingItemCore(itemId) ??
        (TryGetEditingCardContainerCore(itemId)?.DataContext is BoardItem candidate &&
            candidate.Id == itemId
                ? candidate
                : null);

    private BoardItem? FindEditingItemCore(Guid itemId) =>
        _viewModel.ActivePanel?.Items.FirstOrDefault(item => item.Id == itemId) ??
        _board.FindItem(itemId);

    /// <summary>编辑中卡片的容器（未编辑或锚点未实现时为 null）。手势层用它识别编辑表面。</summary>
    private ListBoxItem? TryGetEditingCardContainer() =>
        _editingCardItemId is { } itemId
            ? TryGetEditingCardContainerCore(itemId)
            : null;

    private ListBoxItem? TryGetEditingCardContainerCore(Guid itemId) =>
        FindEditingItemCore(itemId) is { } editingItem
            ? BoardList.ItemContainerGenerator.ContainerFromItem(editingItem) as ListBoxItem
            : null;

    /// <summary>测试缝:绕过键位事件直接提交当前编辑(与 Enter 路径同函数)。</summary>
    internal void CommitCardTextForTest() => CommitCardTextEditing();

    private void CancelCardTextEditing(bool restoreFocusToList = false)
    {
        if (_editingCardItemId is not { } itemId)
        {
            return;
        }

        var editingItem = FindEditingItem(itemId);
        var editorHoldsFocus = TryGetEditingCardContainer() is { } container &&
            FindDescendant<TextBox>(container) is { } editor &&
            editor.IsKeyboardFocused;
        _editingCardItemId = null;
        if (editingItem is not null)
        {
            _activeCardTextCompositions.Remove(editingItem);
        }

        if (restoreFocusToList || editorHoldsFocus)
        {
            BoardList.Focus();
        }

        editingItem?.EndTextEdit();
    }
}
