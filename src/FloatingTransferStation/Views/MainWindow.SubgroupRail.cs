using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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
/// 单点（ApplyBoardListFilter，契约 #37 扩展）；退出＝引导条「清空」/Esc/切分类/
/// 模式切换。实验开关开启时轨首恒居「全部」伪 chip（契约 #57 修订，2026-11-07）：
/// 悬停＝进入全量视图（Filter=null＋全 chip 激活态，与根态同列表）、单击无操作、
/// 不参与改名/拖放归属/默认环，退出沿既有四路径；实验期无取消归属手势（拖到
/// 其他 chip 可覆盖，README 与契约登记）。
/// </summary>
public partial class MainWindow
{
    private readonly HashSet<TextBox> _activeSubgroupNameCompositions = [];

    private InlineRenameSession<SubgroupChipViewModel> _subgroupRenameSession = null!;

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
        // chip 改名与分类改名共用共享改名会话协调器（契约 #57 修订⑤）：会话机器同构
        //（Begin/限长/IME 守卫/Enter/Esc/失焦），只有 subject 参数与乐观保存单点不同；
        // 「点外即提交」隧道与面板切换冲刷经协调器登记清单统一分发。
        _subgroupRenameSession = new InlineRenameSession<SubgroupChipViewModel>(
            Dispatcher,
            _activeSubgroupNameCompositions,
            subjects: () => _viewModel.SubgroupChips,
            isEditing: chip => chip.IsEditingName,
            findEditor: TryGetSubgroupNameEditor,
            findDescendantEditor: FindDescendant<TextBox>,
            limitDraft: SubgroupCatalog.LimitName,
            beginSession: chip => chip.BeginNameEdit(),
            endSession: (chip, draftName) => chip.EndNameEdit(draftName),
            cancelSession: chip => chip.CancelNameEdit(),
            saveIfChanged: (chip, name) =>
            {
                if (name == chip.DisplayName ||
                    _viewModel.ActivePanel is not { } panel)
                {
                    return;
                }

                TrackPendingOperation(SaveSubgroupNameAsync(panel.Category, chip.Slot, name));
            },
            afterSessionEnd: ReconcileSurfaceAfterEditing);
        AddHandler(
            Keyboard.KeyDownEvent,
            new KeyEventHandler(SubgroupChipNameEditor_KeyDown));
        AddHandler(
            Keyboard.LostKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(SubgroupChipNameEditor_LostKeyboardFocus));
        AddHandler(
            TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler(SubgroupChipNameEditor_TextChanged));
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
        // 单击＝设该分类的收集落点子分组（照分类轨「单击标签＝设默认接收分类」先例；
        // 搜索态单击＝仅设默认——不退搜索、不动过滤，#37 修订）。角色闸同置：
        // 记录/启动标签绝不成为落点，Core 侧 Set 守卫为纵深。
        if (_viewModel.ActivePanel is { } panel &&
            BoardCategoryCatalog.IsContentBucket(panel.Category))
        {
            _viewModel.SetDefaultSubgroup(panel.Category, chip.Slot);
        }
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

    // ---- 双击改名会话（共享协调器机器，契约 #57 修订⑤：与分类改名同一台会话机；
    //      事件入口窗口级挂接，编辑器在 DataTemplate 内不经 XAML 特性散挂） ----

    private void BeginSubgroupNameEdit(SubgroupChipViewModel chip, Border host) =>
        _subgroupRenameSession.Begin(chip, host);

    private void SubgroupChipNameEditor_TextChanged(object sender, TextChangedEventArgs e) =>
        _subgroupRenameSession.HandleTextChanged(e);

    private void SubgroupChipNameEditor_CompositionStartedOrUpdated(
        object sender,
        TextCompositionEventArgs e) =>
        _subgroupRenameSession.HandleCompositionStartedOrUpdated(e);

    private void SubgroupChipNameEditor_CompositionCompleted(
        object sender,
        TextCompositionEventArgs e) =>
        _subgroupRenameSession.HandleCompositionCompleted(e);

    private void SubgroupChipNameEditor_KeyDown(object sender, KeyEventArgs e) =>
        _subgroupRenameSession.HandleKeyDown(e);

    private void SubgroupChipNameEditor_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e) =>
        _subgroupRenameSession.HandleLostKeyboardFocus(e);

    private void CommitSubgroupNameEdit(SubgroupChipViewModel chip, string draftName) =>
        _subgroupRenameSession.Commit(chip, draftName);

    /// <summary>指定 chip 的名字编辑器（协调器查询缝）：轨内槽 chip 宿主树递归查找
    ///（chip≤5 无虚拟化；「全部」伪 chip 无编辑器，天然不在查找域）。</summary>
    private TextBox? TryGetSubgroupNameEditor(SubgroupChipViewModel chip) =>
        FindSubgroupNameEditor(SubgroupRail, chip);

    private static TextBox? FindSubgroupNameEditor(
        DependencyObject parent,
        SubgroupChipViewModel chip)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is TextBox { DataContext: SubgroupChipViewModel candidate } &&
                ReferenceEquals(candidate, chip))
            {
                return (TextBox)child;
            }

            if (FindSubgroupNameEditor(child, chip) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
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
    }
}
