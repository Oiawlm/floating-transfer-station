using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;

namespace FloatingTransferStation.Views;

/// <summary>
/// 分类内子分组·形态①按钮轨（实验性，契约 #57 修订）：悬停 chip＝按槽过滤
/// （对齐分类轨「悬停即进入」展开态先例，零防抖；守卫链＝改名会话中/拖拽在途/
/// 外部拖放轨道可见/搜索激活任一不进入——搜索压倒沿用 #37）、单击 chip＝设该
/// 分类收集落点子分组（照「单击标签＝设默认接收分类」先例，原单击过滤 toggle
/// 退役；搜索态单击＝仅设默认——不退搜索不动过滤）、双击 chip＝改名（照
/// <see cref="MainWindow.CategoryEditing"/> 全套会话机器）、拖卡片到 chip＝批量
/// 归属该槽（照「拖卡入标签=移动分类」先例）。过滤只经 BoardList.Items.Filter
/// 单点（ApplyBoardListFilter，契约 #37 扩展）；退出＝Esc/切分类/模式切换/悬停
/// 其他 chip（含「全部」）——槽过滤空态无 CTA，引导由空态副文案承载（契约 #33
/// 2026-11-08 修订）。轨首恒居「全部」伪 chip（契约 #60，2026-11-08 起结构性
/// 恒在）：悬停＝进入全量视图（Filter=null＋全 chip 激活态，与根态同列表）、
/// 不参与改名/拖放归属/默认环，退出沿既有三路径＋悬停其他 chip；实验期无取消
/// 归属手势（拖到其他 chip 可覆盖，README 与契约登记）。
/// 点击切换实验开关（契约 #62，默认关；偏好 SubgroupRailClickSwitchEnabled）
/// 开启＝手势重路由，过滤状态机与全部消费点零改动：悬停任何 chip＝纯视觉
///（既有 hover 层＋描边，驻留 300ms 计数/手势 ToolTip），裸单击槽 chip＝toggle
/// 过滤（再点取消，搜索态冻结无操作），Ctrl+单击＝设收集落点（两态共用），
/// 单击「全部」＝全量态/再点回根；关态＝现状逐字保留。
/// </summary>
public partial class MainWindow
{
    private readonly HashSet<TextBox> _activeSubgroupNameCompositions = [];

    /// <summary>当前 chip 过滤槽（null=无过滤）；搜索激活期间冻结（让位裁决）。</summary>
    private int? _subgroupFilterSlot;

    /// <summary>「全部」伪 chip 全量过滤态（契约 #57 修订）：与 <see cref="_subgroupFilterSlot"/>
    /// 互斥——全量视图＝根态同列表（Filter=null），仅以全 chip 激活态区分。</summary>
    private bool _isSubgroupAllFilterActive;

    /// <summary>chip 过滤生效谓词（槽过滤或全量过滤）且 ChipRail 模式（Off/GroupedView
    /// 无过滤态）。internal＝锁定测试直达（拖放/Ctrl+A/收起抑制/Esc 的守卫前置）。</summary>
    internal bool IsSubgroupFilterActive =>
        (_subgroupFilterSlot is not null || _isSubgroupAllFilterActive) &&
        _viewModel.SubgroupDisplay == SubgroupDisplayMode.ChipRail;

    private bool IsSubgroupNameEditActive() =>
        _viewModel.SubgroupChips.Any(chip => chip.IsEditingName);

    private void InitializeSubgroupNameEditing()
    {
        AddHandler(
            TextCompositionManager.PreviewTextInputStartEvent,
            new TextCompositionEventHandler(SubgroupChipNameEditor_CompositionStartedOrUpdated),
            true);
        AddHandler(
            TextCompositionManager.PreviewTextInputUpdateEvent,
            new TextCompositionEventHandler(SubgroupChipNameEditor_CompositionStartedOrUpdated),
            true);
        AddHandler(
            TextCompositionManager.PreviewTextInputEvent,
            new TextCompositionEventHandler(SubgroupChipNameEditor_CompositionCompleted),
            true);
    }

    // ---- 悬停过滤 / 单击设默认 / 双击改名（照分类轨「MouseEnter 悬停即进入、
    // Up 承载单击、Down 承载双击」先例） ----

    private void SubgroupChip_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border { DataContext: SubgroupChipViewModel chip })
        {
            return;
        }

        // 交互记账挂在真实 MouseEnter 事件（照 CategoryTab_MouseEnter 先例，契约 #26）。
        NoteUserInteraction();
        // 点击切换开态（契约 #62，R13 悬停误触治理）：悬停＝纯视觉——既有 hover
        // 层与描边由模板 IsMouseOver 驱动，过滤切换退役为单击承载，此处路由早退
        //（驻留计数/手势提示由 ToolTip 承载，见 RefreshSubgroupChipHints）。
        if (_viewModel.SubgroupRailClickSwitchEnabled)
        {
            return;
        }

        // 守卫链（对齐 EvaluateHoverIntent 结构，不新造状态机）：改名会话中/拖拽在途
        //（拖卡滑过 chip 也触发 MouseEnter，必须挡）/外部拖放轨道可见/搜索激活
        //（#37 搜索压倒——悬停不进入、不退搜索）任一成立即不进入。
        if (_isClosing ||
            IsSubgroupNameEditActive() ||
            _panelState.IsDragInProgress ||
            _viewModel.IsExternalDropRailVisible ||
            _viewModel.IsSearchActive)
        {
            return;
        }

        // 同槽幂等：滑回已过滤 chip 不重复清选择/重过滤。
        if (_subgroupFilterSlot == chip.Slot)
        {
            return;
        }

        SetSubgroupFilter(chip);
    }

    private void SubgroupAllChip_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border { DataContext: SubgroupAllChipViewModel })
        {
            return;
        }

        // 交互记账挂在真实 MouseEnter 事件（照 SubgroupChip_MouseEnter 同法，契约 #26）。
        NoteUserInteraction();
        // 点击切换开态（契约 #62）：「全部」悬停同样退役为纯视觉，路由早退。
        if (_viewModel.SubgroupRailClickSwitchEnabled)
        {
            return;
        }

        // 守卫链逐字同槽 chip：改名会话中/拖拽在途/外部拖放轨道可见/搜索激活
        //（#37 搜索压倒——悬停不进入、不退搜索）任一成立即不进入。
        if (_isClosing ||
            IsSubgroupNameEditActive() ||
            _panelState.IsDragInProgress ||
            _viewModel.IsExternalDropRailVisible ||
            _viewModel.IsSearchActive)
        {
            return;
        }

        // 全量态幂等：滑回「全部」不重复清选择/重过滤。
        if (_isSubgroupAllFilterActive)
        {
            return;
        }

        SetSubgroupAllFilter();
    }

    private void SubgroupChip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isClosing ||
            e.ChangedButton != MouseButton.Left ||
            e.ClickCount < 2 ||
            FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null ||
            sender is not Border { DataContext: SubgroupChipViewModel chip } surface ||
            _viewModel.ActivePanel is not { } panel ||
            !BoardCategoryCatalog.IsContentBucket(panel.Category))
        {
            return;
        }

        BeginSubgroupNameEdit(chip, surface);
        e.Handled = true;
    }

    private void SubgroupChip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isClosing ||
            e.ChangedButton != MouseButton.Left ||
            FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null ||
            sender is not Border { DataContext: SubgroupChipViewModel chip } ||
            chip.IsEditingName)
        {
            return;
        }

        e.Handled = true;
        // Ctrl+单击（两态共用，修饰键手势族先例＝卡片 Ctrl/Ctrl+Shift 右键）＝设
        // 该分类的收集落点子分组（照「单击标签＝设默认接收分类」先例；搜索态可用
        // ——#37 压倒只冻结过滤，落点不连坐）。
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SetDefaultSubgroupFromChip(chip);
            return;
        }

        // 关态＝现状逐字保留：裸单击＝设收集落点（搜索态单击＝仅设默认——不退
        // 搜索、不动过滤，#37 修订）；开态＝裸单击 toggle 过滤（契约 #62）。
        if (_viewModel.SubgroupRailClickSwitchEnabled)
        {
            ToggleSubgroupFilterFromChip(chip);
        }
        else
        {
            SetDefaultSubgroupFromChip(chip);
        }
    }

    /// <summary>「全部」伪 chip 单击（契约 #62 开态；关态＝无操作，#60 原文）：
    /// 进入全量态（#60 语义原样：Filter=null＋全 chip 激活、粘性、拖放/Ctrl+A
    /// 禁令、收起抑制自动继承），全量态再点＝回根态；搜索压倒＝过滤冻结无操作。
    /// 「全部」无落点语义——修饰键不发明交互（Ctrl+单击＝无操作）。</summary>
    private void SubgroupAllChip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isClosing ||
            e.ChangedButton != MouseButton.Left ||
            !_viewModel.SubgroupRailClickSwitchEnabled)
        {
            return;
        }

        e.Handled = true;
        if (_viewModel.IsSearchActive ||
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }

        if (_isSubgroupAllFilterActive)
        {
            ClearSubgroupFilter(reapplyFilter: true);
            return;
        }

        SetSubgroupAllFilter();
    }

    /// <summary>设收集落点子分组（Ctrl+单击/关态裸单击共用；照「单击标签＝设默认
    /// 接收分类」先例）。角色闸同置：记录/启动标签绝不成为落点，Core 侧 Set 守卫
    /// 为纵深。</summary>
    private void SetDefaultSubgroupFromChip(SubgroupChipViewModel chip)
    {
        if (_viewModel.ActivePanel is { } panel &&
            BoardCategoryCatalog.IsContentBucket(panel.Category))
        {
            _viewModel.SetDefaultSubgroup(panel.Category, chip.Slot);
        }
    }

    /// <summary>开态裸单击＝toggle 过滤（契约 #62）：未激活→进入该槽过滤（清空
    /// 选择沿 #37、激活态双指示既有）；已激活→取消回根态（toggle 先例＝搜索类型
    /// chip「再点取消」）。搜索态＝过滤冻结无操作（#37 压倒）；过滤状态机
    /// （SetSubgroupFilter/ClearSubgroupFilter）零改动复用。</summary>
    private void ToggleSubgroupFilterFromChip(SubgroupChipViewModel chip)
    {
        if (_viewModel.IsSearchActive)
        {
            return;
        }

        if (_subgroupFilterSlot == chip.Slot)
        {
            ClearSubgroupFilter(reapplyFilter: true);
            return;
        }

        SetSubgroupFilter(chip);
    }

    private void SetSubgroupFilter(SubgroupChipViewModel chip)
    {
        if (_isClosing ||
            _viewModel.ActivePanel is not { } panel ||
            !BoardCategoryCatalog.IsContentBucket(panel.Category))
        {
            return;
        }

        _subgroupFilterSlot = chip.Slot;
        _isSubgroupAllFilterActive = false;
        ApplySubgroupFilterSelection();
        // 进入过滤视图＝清空选择（沿 #37 进入搜索口径；过滤视图的选择范围无意义）。
        ClearUserSelection();
        ApplyBoardListFilter();
    }

    /// <summary>进入「全部」全量过滤（契约 #57 修订）：清槽过滤态（互斥）、置全量态
    /// ——列表与根态同源（ApplyBoardListFilter 落 Filter=null），仅以全 chip 激活态
    /// 区分；进入清选择沿 #37 口径（照 <see cref="SetSubgroupFilter"/> 同构）。</summary>
    private void SetSubgroupAllFilter()
    {
        if (_isClosing ||
            _viewModel.ActivePanel is not { } panel ||
            !BoardCategoryCatalog.IsContentBucket(panel.Category))
        {
            return;
        }

        _subgroupFilterSlot = null;
        _isSubgroupAllFilterActive = true;
        ApplySubgroupFilterSelection();
        ClearUserSelection();
        ApplyBoardListFilter();
    }

    private void ApplySubgroupFilterSelection()
    {
        foreach (var chip in _viewModel.SubgroupChips)
        {
            chip.IsFilterActive = chip.Slot == _subgroupFilterSlot;
        }

        _viewModel.SubgroupAllChip.IsFilterActive = _isSubgroupAllFilterActive;
    }

    /// <summary>清除 chip 过滤（契约 #57）：Off/模式切换/切分类/开关关闭共用；reapply＝
    /// 立即重评 Filter 单点（旧面板视图上的 Filter 残留由调用方在面板切换前清）。</summary>
    private void ClearSubgroupFilter(bool reapplyFilter)
    {
        if (_subgroupFilterSlot is null && !_isSubgroupAllFilterActive)
        {
            return;
        }

        _subgroupFilterSlot = null;
        _isSubgroupAllFilterActive = false;
        ApplySubgroupFilterSelection();
        if (reapplyFilter)
        {
            ApplyBoardListFilter();
        }
    }

    /// <summary>chip 计数与驻留提示唯一喂数点（契约 #62）：计数＝激活分类该槽
    /// 全部卡（含置顶，读源集合）；提示文本按开关组稿——开态两行（名·计数＋手势
    /// 提示，模板 ToolTip 绑定 ToolTipText、InitialShowDelay=300），关态恒 null
    ///（现状零变化）。触发＝面板激活/改名提交（RefreshSubgroupChips 伴随点）、
    /// 既有集合变化点（ActivePanelItems_EmptyStateChanged）、拖卡指派
    ///（SubgroupChip_Drop）与开关翻转（ApplySubgroupRailClickSwitchPreference）；
    /// 非内容桶面板无轨无消费面直接返回。后台导入路径的集合变化可能非 UI 线程，
    /// 统一封送回 UI 线程（与 UpdateEmptyStates 同法）。</summary>
    private void RefreshSubgroupChipHints()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshSubgroupChipHints);
            return;
        }

        if (_viewModel.ActivePanel is not { } panel ||
            !BoardCategoryCatalog.IsContentBucket(panel.Category))
        {
            return;
        }

        var clickSwitch = _viewModel.SubgroupRailClickSwitchEnabled;
        foreach (var chip in _viewModel.SubgroupChips)
        {
            chip.CardCount = panel.Items.Count(item => item.Subgroup == chip.Slot);
            chip.ToolTipText = clickSwitch
                ? $"{chip.DisplayName} · {chip.CardCount} 张卡片\n单击查看 · Ctrl+单击设为收集落点 · 双击改名"
                : null;
        }

        _viewModel.SubgroupAllChip.CardCount = panel.Items.Count;
        _viewModel.SubgroupAllChip.ToolTipText = clickSwitch
            ? $"{_viewModel.SubgroupAllChip.DisplayName} · {_viewModel.SubgroupAllChip.CardCount} 张卡片\n单击查看全部"
            : null;
    }

    // ---- 视觉分组零指派引导（2026-11-06，契约 #57 形态②可用性扩展） ----

    /// <summary>GroupedViewGuidance 可见性唯一更新点：GroupedView 模式、激活面板为
    /// 内容桶、面板非空且非置顶卡的不同 Subgroup 值（含 null）≤1——＝零组边界
    /// 渲染（「视觉分组看不出分组」的真条件，覆盖全部未指派与全部同槽两情形）时
    /// 可见，告知先回「按钮轨」拖卡归组；面板空让位 BoardEmptyState（内容桶空态
    /// 语义优先），Off/ChipRail（按钮轨在场）恒塌缩——两种形态互斥分用 Row=1 行：
    /// ChipRail＝轨可见、GroupedView＝引导条可评估、Off 全塌缩。可见性统一
    /// SetCurrentValue；非 UI 线程进入统一封送（集合变化可能来自后台导入路径，
    /// 与空态单点同法）。触发路径并集：ApplySubgroupDisplayPreference（模式切换
    /// 必经）、激活桶集合 CollectionChanged（拖卡归属与 GroupedView 纯重排 Move
    /// 都会触发）、UpdatePanelSurface（表面切换）——归组只发生在 ChipRail（与
    /// 引导条不同屏），结构上不存在漏路径；同路径重复调用无害（纯求值幂等）。
    /// 谓词读源集合（Items.Count/Subgroup 值），不经绑定视图，无视图消化时序。</summary>
    private void UpdateGroupedViewGuidance()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateGroupedViewGuidance);
            return;
        }

        var panel = _viewModel.ActivePanel;
        var visible = panel is not null &&
            _viewModel.SubgroupDisplay == SubgroupDisplayMode.GroupedView &&
            BoardCategoryCatalog.IsContentBucket(panel.Category) &&
            panel.Items.Count > 0 &&
            panel.Items
                .Where(item => !item.IsPinned)
                .Select(item => item.Subgroup)
                .Distinct()
                .Count() <= 1;
        GroupedViewGuidance.SetCurrentValue(VisibilityProperty, visible ? Visibility.Visible : Visibility.Collapsed);
    }

    // ---- 双击改名会话（照 CategoryEditing 全套：草稿限长/IME 守卫/Enter/Esc/失焦提交） ----

    private void BeginSubgroupNameEdit(SubgroupChipViewModel chip, Border host)
    {
        chip.BeginNameEdit();
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (!chip.IsEditingName ||
                    FindDescendant<TextBox>(host) is not { } editor)
                {
                    return;
                }

                editor.Focus();
                editor.SelectAll();
            }));
    }

    private void SubgroupChipNameEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox editor ||
            editor.DataContext is not SubgroupChipViewModel chip ||
            !chip.IsEditingName ||
            _activeSubgroupNameCompositions.Contains(editor))
        {
            return;
        }

        // MaxLength 按 UTF-16 计数（代理对占 2），文本元素口径的限长在此归一。
        var limited = SubgroupCatalog.LimitName(editor.Text);
        if (limited == editor.Text)
        {
            return;
        }

        editor.Text = limited;
        editor.CaretIndex = editor.Text.Length;
    }

    private void SubgroupChipNameEditor_CompositionStartedOrUpdated(
        object sender,
        TextCompositionEventArgs e)
    {
        if (e.OriginalSource is TextBox
            {
                DataContext: SubgroupChipViewModel { IsEditingName: true }
            } editor)
        {
            _activeSubgroupNameCompositions.Add(editor);
        }
    }

    private void SubgroupChipNameEditor_CompositionCompleted(
        object sender,
        TextCompositionEventArgs e)
    {
        if (e.OriginalSource is TextBox editor)
        {
            _activeSubgroupNameCompositions.Remove(editor);
        }
    }

    private void SubgroupChipNameEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox editor ||
            editor.DataContext is not SubgroupChipViewModel chip)
        {
            return;
        }

        if (_activeSubgroupNameCompositions.Contains(editor))
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitSubgroupNameEdit(chip, editor.Text);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _activeSubgroupNameCompositions.Remove(editor);
            chip.CancelNameEdit();
            ReconcileSurfaceAfterEditing();
        }
    }

    private void SubgroupChipNameEditor_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: SubgroupChipViewModel chip } editor)
        {
            _activeSubgroupNameCompositions.Remove(editor);
            CommitSubgroupNameEdit(chip, editor.Text);
        }
    }

    private void CommitSubgroupNameEdit(SubgroupChipViewModel chip, string draftName)
    {
        if (!chip.IsEditingName)
        {
            return;
        }

        var name = chip.EndNameEdit(draftName);
        ReconcileSurfaceAfterEditing();
        if (name == chip.DisplayName ||
            _viewModel.ActivePanel is not { } panel)
        {
            return;
        }

        TrackPendingOperation(SaveSubgroupNameAsync(panel.Category, chip.Slot, name));
    }

    /// <summary>子分组槽名乐观提交（镜像 <see cref="SaveCategoryNameAsync"/>：save gate
    /// 原子写＋失败回弹＋状态条）；成功后刷新轨名与组头派生（名清单单一事实源仍在
    /// settings.json）。</summary>
    private async Task SaveSubgroupNameAsync(BoardCategory category, int slot, string name)
    {
        await _settingsSaveGate.WaitAsync();
        try
        {
            var originalName = _settings.SubgroupName(category, slot);
            _settings = _settings.WithSubgroupName(category, slot, name);
            try
            {
                await _store.SaveSettingsAsync(_settings);
                if (_viewModel.ActivePanel is { } panel &&
                    panel.Category == category)
                {
                    _viewModel.RefreshSubgroupChips(_settings, category);
                    RefreshSubgroupChipHints();
                }

                _board.SetSubgroupDisplay(_viewModel.SubgroupDisplay, _settings);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _settings = _settings.WithSubgroupName(category, slot, originalName);
                ShowStatus("子分组名称未保存，已恢复原名称。");
            }
        }
        finally
        {
            _settingsSaveGate.Release();
        }
    }

    // ---- 拖卡归属（本功能唯一指派手势；过滤激活时拖放已禁，指派只在无过滤态发生） ----

    private void SubgroupChip_DragEnter(object sender, DragEventArgs e) =>
        UpdateSubgroupChipDropTarget(sender, e);

    private void SubgroupChip_DragOver(object sender, DragEventArgs e) =>
        UpdateSubgroupChipDropTarget(sender, e);

    private void SubgroupChip_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border { DataContext: SubgroupChipViewModel chip })
        {
            chip.IsDropTarget = false;
        }

        e.Handled = true;
    }

    private void UpdateSubgroupChipDropTarget(object sender, DragEventArgs e)
    {
        WithdrawInsertionIndicator();
        WithdrawCategoryDropTarget();
        if (sender is Border { DataContext: SubgroupChipViewModel chip } &&
            _viewModel.ActivePanel is { } panel &&
            BoardCategoryCatalog.IsContentBucket(panel.Category) &&
            !IsSubgroupFilterActive &&
            _dragPayload.GetInternalItemIds(e.Data) is { })
        {
            ApplySubgroupChipDropTarget(chip);
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        ApplySubgroupChipDropTarget(null);
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void ApplySubgroupChipDropTarget(SubgroupChipViewModel? target)
    {
        foreach (var chip in _viewModel.SubgroupChips)
        {
            chip.IsDropTarget = ReferenceEquals(chip, target);
        }
    }

    private async void SubgroupChip_Drop(object sender, DragEventArgs e)
    {
        ClearDropFeedback();
        ApplySubgroupChipDropTarget(null);
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (sender is not Border { DataContext: SubgroupChipViewModel chip } ||
            _viewModel.ActivePanel is not { } panel ||
            !BoardCategoryCatalog.IsContentBucket(panel.Category) ||
            IsSubgroupFilterActive ||
            _dragPayload.GetInternalItemIds(e.Data) is not { } itemIds)
        {
            return;
        }

        e.Effects = DragDropEffects.Move;
        await _mutations.AssignSubgroupAsync(itemIds, chip.Slot);
        // 指派改变槽计数（契约 #62 驻留提示口径）：归属落板后同步喂数。
        RefreshSubgroupChipHints();
    }
}
