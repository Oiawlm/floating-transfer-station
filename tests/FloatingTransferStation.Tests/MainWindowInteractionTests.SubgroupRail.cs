using System.Reflection;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Line = System.Windows.Shapes.Line;
using FloatingTransferStation.Design;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 分类内子分组（实验性，契约 #57）窗口级回归：Off 等价、形态①按钮轨（渲染/
/// 过滤/互斥/改名/拖卡归属/过滤态行为）、形态②视觉分组（进入分区/边界复用
/// divider 宿主/组头/置顶不分组/纯重排）、模式切换零残留、撤销不回归与
/// 双主题证据产出。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    private static readonly JsonSerializerOptions SubgroupStoreOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private static MainWindow CreateSubgroupWindow(
        TestDirectory directory,
        BoardService board,
        SubgroupDisplayMode mode,
        WindowSettings? settings = null)
    {
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        return CreateWindow(
            board,
            store,
            settings ?? WindowSettings.Default,
            new DefaultCaptureCategoryState(),
            new ImageNormalizer(store.ImagesDirectory),
            preferences: AppPreferences.Default with { SubgroupDisplay = mode });
    }

    private static Border FindSubgroupRail(MainWindow window)
    {
        var rail = window.FindName("SubgroupRail") as Border;
        Assert.IsNotNull(rail);
        return rail;
    }

    private static Border FindSubgroupChip(MainWindow window, int slot)
    {
        // Tag 判别照 FindCategoryTab 先例：chip 模板的内层层（hover/激活/拖放层）共享
        // DataContext，只有外层宿主 Border 带 Tag=Slot。
        return FindDescendants<Border>(FindSubgroupRail(window)).Single(candidate =>
            candidate.DataContext is SubgroupChipViewModel chip &&
            chip.Slot == slot &&
            Equals(candidate.Tag, slot));
    }

    private static void ClickChip(MainWindow window, int slot)
    {
        var chip = FindSubgroupChip(window, slot);
        chip.RaiseEvent(NewMouseButtonEventArgs(
            UIElement.MouseLeftButtonUpEvent,
            chip));
        CompleteLayout(window);
    }

    private static void DoubleClickChip(MainWindow window, int slot)
    {
        var chip = FindSubgroupChip(window, slot);
        var doubleClick = NewMouseButtonEventArgs(
            UIElement.MouseLeftButtonDownEvent,
            chip);
        typeof(MouseButtonEventArgs)
            .GetField("_count", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(doubleClick, 2);
        chip.RaiseEvent(doubleClick);
        CompleteLayout(window);
    }

    private static TextBox FindChipEditor(MainWindow window, int slot) =>
        FindDescendants<TextBox>(FindSubgroupChip(window, slot)).Single();

    /// <summary>chip 编辑器键入（照 RaiseCardEditorKey 先例）：常规 KeyDown 事件——
    /// 编辑器处理器挂在 KeyDown，不是 PreviewKeyDown。</summary>
    private static void RaiseChipEditorKey(MainWindow window, TextBox editor, Key key) =>
        editor.RaiseEvent(new KeyEventArgs(
            Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(window)!,
            Environment.TickCount,
            key)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
            Source = editor
        });

    private static BoardMutationService GetWindowMutations(MainWindow window) =>
        (BoardMutationService)typeof(MainWindow)
            .GetField("_mutations", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;

    private static void RunMutationsTask(
        MainWindow window,
        Func<BoardMutationService, Task> operation)
    {
        var task = operation(GetWindowMutations(window));
        PumpDispatcherUntil(window.Dispatcher, task);
        CompleteLayout(window);
    }

    private static T? ReadPersistedJson<T>(TestDirectory directory, string fileName)
    {
        var path = Path.Combine(directory.Root, fileName);
        Assert.IsTrue(File.Exists(path), $"持久化文件必须落盘：{fileName}");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), SubgroupStoreOptions);
    }

    [STATestMethod]
    public void Subgroup_OffModeKeepsRailCollapsedAndNoFilterResidue()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("一", BoardCategory.Inbox).Subgroup = 1;
        board.AddText("二", BoardCategory.Inbox).Subgroup = 1;
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.Off);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var viewModel = (MainWindowViewModel)window.DataContext;

            Assert.IsFalse(viewModel.IsSubgroupRailVisible);
            Assert.AreEqual(
                Visibility.Collapsed,
                FindSubgroupRail(window).Visibility,
                "Off＝轨整行塌缩零占位（BoardList 几何与现状逐 DIP 一致）。");
            Assert.IsNull(list!.Items.Filter, "Off 无过滤残留。");
            Assert.AreEqual(2, list.Items.OfType<BoardItem>().Count());
            foreach (var item in board.Items(BoardCategory.Inbox))
            {
                Assert.IsFalse(item.StartsSubgroupRegion, "Off 派生标记恒 false。");
                Assert.IsNull(item.SubgroupHeader);
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_ChipRailRendersFourChipsWithDefaultNames()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("内容", BoardCategory.Inbox);
        var settings = WindowSettings.Default
            .WithSubgroupName(BoardCategory.Inbox, 1, "急件");
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.ChipRail, settings);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var rail = FindSubgroupRail(window);

            Assert.IsTrue(viewModel.IsSubgroupRailVisible);
            Assert.AreEqual(Visibility.Visible, rail.Visibility);
            Assert.AreEqual(
                WindowSettings.SubgroupRailHeight,
                rail.Height,
                "轨高＝结构常量（不随过滤态/名称变化）。");
            var chips = viewModel.SubgroupChips;
            Assert.AreEqual(4, chips.Count, "固定 4 槽（不加不减）。");
            Assert.AreEqual("待分类", chips[0].DisplayName);
            Assert.AreEqual("急件", chips[1].DisplayName, "自定义名经逐槽解析喂入。");
            Assert.AreEqual("不重要", chips[2].DisplayName);
            Assert.AreEqual("待办", chips[3].DisplayName);

            // 复盘表面零呈现：切到复盘标签轨必须塌缩。
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            Assert.IsFalse(viewModel.IsSubgroupRailVisible, "复盘表面零呈现。");
            Assert.AreEqual(Visibility.Collapsed, rail.Visibility);

            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            Assert.AreEqual(Visibility.Visible, rail.Visibility);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_ChipClickFiltersBySlotAndSecondClickRestores()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("甲", BoardCategory.Inbox).Subgroup = 1;
        board.AddText("乙", BoardCategory.Inbox).Subgroup = 1;
        board.AddText("丙", BoardCategory.Inbox);
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.ChipRail);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var viewModel = (MainWindowViewModel)window.DataContext;
            Assert.AreEqual(3, list!.Items.OfType<BoardItem>().Count());

            ClickChip(window, 1);

            Assert.IsTrue(window.IsSubgroupFilterActive);
            Assert.IsTrue(viewModel.SubgroupChips[1].IsFilterActive, "激活 chip 选中态。");
            CollectionAssert.AreEquivalent(
                new[] { "甲", "乙" },
                list.Items.OfType<BoardItem>().Select(item => item.Text).ToArray(),
                "chip 单击按槽过滤（只经 Items.Filter 单点）。");
            Assert.AreEqual(3, viewModel.ActivePanel!.Items.Count, "源集合不得被过滤改动。");
            Assert.AreEqual(0, list.SelectedItems.Count, "进入过滤视图清空选择（#37 口径）。");

            ClickChip(window, 1);

            Assert.IsFalse(window.IsSubgroupFilterActive);
            Assert.IsFalse(viewModel.SubgroupChips[1].IsFilterActive);
            Assert.AreEqual(3, list.Items.OfType<BoardItem>().Count(), "再点激活 chip＝取消过滤还原。");
            Assert.IsNull(list.Items.Filter);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_ChipFilterYieldsToSearchAndRestoresAfterSearchExit()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("苹果一", BoardCategory.Inbox).Subgroup = 1;
        board.AddText("香蕉二", BoardCategory.Inbox).Subgroup = 1;
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.ChipRail);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var viewModel = (MainWindowViewModel)window.DataContext;

            ClickChip(window, 1);
            Assert.IsTrue(window.IsSubgroupFilterActive);

            // 搜索激活＝搜索谓词压倒，chip 过滤冻结让位（选中态保留）。
            window.EnterSearchMode();
            ((TextBox)window.FindName("SearchInput"))!.Text = "苹果";
            CompleteLayout(window);

            Assert.IsTrue(viewModel.IsSearchActive);
            Assert.IsTrue(viewModel.SubgroupChips[1].IsFilterActive, "让位期间 chip 选中态保留。");
            CollectionAssert.AreEquivalent(
                new[] { "苹果一" },
                list!.Items.OfType<BoardItem>().Select(item => item.Text).ToArray(),
                "搜索谓词压倒 chip 过滤。");

            window.ExitSearchMode();
            CompleteLayout(window);

            Assert.IsFalse(viewModel.IsSearchActive);
            Assert.IsTrue(window.IsSubgroupFilterActive, "退出搜索自动恢复 chip 过滤。");
            Assert.AreEqual(2, list.Items.OfType<BoardItem>().Count());
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_ChipClickDuringSearchExitsSearchThenFilters()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("苹果一", BoardCategory.Inbox).Subgroup = 2;
        board.AddText("苹果二", BoardCategory.Inbox);
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.ChipRail);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var viewModel = (MainWindowViewModel)window.DataContext;

            window.EnterSearchMode();
            ((TextBox)window.FindName("SearchInput"))!.Text = "苹果";
            CompleteLayout(window);
            Assert.IsTrue(viewModel.IsSearchActive);

            ClickChip(window, 2);

            Assert.IsFalse(viewModel.IsSearchActive, "搜索态下 chip 单击＝退出搜索（沿切分类先例）。");
            Assert.AreEqual(
                Visibility.Visible,
                ((StackPanel)window.FindName("HeaderActions")).Visibility,
                "头部操作区随搜索退出还原。");
            Assert.IsTrue(window.IsSubgroupFilterActive);
            CollectionAssert.AreEquivalent(
                new[] { "苹果一" },
                list!.Items.OfType<BoardItem>().Select(item => item.Text).ToArray());
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_ChipDoubleClickOpensRenameSessionWithImeGuardAndCap()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("内容", BoardCategory.Inbox);
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.ChipRail);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var chip = viewModel.SubgroupChips[1];

            DoubleClickChip(window, 1);

            Assert.IsTrue(chip.IsEditingName, "双击进入改名会话。");
            var editor = FindChipEditor(window, 1);
            Assert.AreEqual(
                SubgroupCatalog.MaxNameLength,
                editor.MaxLength,
                "上限 3 字由编辑器 MaxLength 单一来源承载。");
            Assert.IsTrue(editor.IsKeyboardFocusWithin);
            Assert.AreEqual(0, editor.SelectionStart);
            Assert.AreEqual(chip.DisplayName.Length, editor.SelectionLength, "进入即全选（照分类改名先例）。");

            // IME 组合守卫：组合期间的 Enter 不提交、不终结会话。
            editor.RaiseEvent(NewTextCompositionEventArgs(
                TextCompositionManager.PreviewTextInputStartEvent,
                editor,
                "重"));
            RaiseChipEditorKey(window, editor, Key.Enter);
            CompleteLayout(window);

            Assert.IsTrue(chip.IsEditingName, "组合期间 Enter 不提交、不终结会话。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_RenameCommitsOptimisticallyAndSurvivesReopen()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("内容", BoardCategory.Inbox);
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.ChipRail);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            DoubleClickChip(window, 1);
            var editor = FindChipEditor(window, 1);
            editor.Text = "要事";
            RaiseChipEditorKey(window, editor, Key.Enter);
            CompleteLayout(window);
            InvokePrivateTask(window, "DrainPendingOperationsAsync");

            Assert.IsFalse(viewModel.SubgroupChips[1].IsEditingName, "Enter 提交并终结会话。");
            Assert.AreEqual("要事", viewModel.SubgroupChips[1].DisplayName, "乐观上屏。");
            var persisted = ReadPersistedJson<WindowSettings>(directory, "settings.json");
            Assert.IsNotNull(persisted);
            Assert.AreEqual(
                "要事",
                persisted!.SubgroupName(BoardCategory.Inbox, 1),
                "改名经 save gate 原子落 settings.json（名清单单一事实源）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_DragCardOntoChipAssignsSlotAndPersists()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var first = board.AddText("甲", BoardCategory.Inbox);
        var second = board.AddText("乙", BoardCategory.Inbox);
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.ChipRail);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var chipHost = FindSubgroupChip(window, 2);
            var data = new DragPayloadService().BuildInternalBatch([first, second]);

            chipHost.RaiseEvent(NewDragEventArgs(data, DragDrop.DragEnterEvent, chipHost));
            CompleteLayout(window);
            Assert.IsTrue(viewModel.SubgroupChips[2].IsDropTarget, "拖入 chip 高亮。");

            chipHost.RaiseEvent(NewDragEventArgs(data, DragDrop.DropEvent, chipHost));
            // Drop 处理器内联启动变更管线（沿 CategoryTab_Drop 同法），泵调度器等落盘。
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(500));
            CompleteLayout(window);

            Assert.AreEqual(2, first.Subgroup, "拖卡到 chip＝批量归属该槽。");
            Assert.AreEqual(2, second.Subgroup);
            Assert.IsFalse(viewModel.SubgroupChips[2].IsDropTarget, "放置后高亮清除。");
            var persisted = ReadPersistedJson<BoardSnapshot>(directory, "board.json");
            Assert.IsNotNull(persisted);
            Assert.AreEqual(
                2,
                persisted!.Items.Count(item => item.Subgroup == 2),
                "归属随 board.json 持久化。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_ChipFilterDisablesDragAndCtrlAAndSuppressesAutoCollapse()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("甲", BoardCategory.Inbox).Subgroup = 1;
        board.AddText("乙", BoardCategory.Inbox);
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.ChipRail);

        try
        {
            window.Show();
            window.ClientAreaAnimationsEnabled = false;
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var viewModel = (MainWindowViewModel)window.DataContext;

            ClickChip(window, 1);
            Assert.IsTrue(window.IsSubgroupFilterActive, "拖放守卫前置＝过滤态谓词为真。");

            // Ctrl+A（沿 #37 口径禁用）：路由命令直达窗口 SelectAll 绑定。
            ApplicationCommands.SelectAll.Execute(null, list!);
            CompleteLayout(window);
            Assert.AreEqual(0, list.SelectedItems.Count, "过滤态 Ctrl+A 不得选择。");

            // 自动收起抑制：过滤态 Root_MouseLeave 后 450ms 仍展开。
            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(450));
            CompleteLayout(window);
            Assert.IsTrue(viewModel.IsPanelExpanded, "过滤态抑制自动收起。");

            ClickChip(window, 1);
            Assert.IsFalse(window.IsSubgroupFilterActive);
            ApplicationCommands.SelectAll.Execute(null, list);
            CompleteLayout(window);
            Assert.AreEqual(2, list.SelectedItems.Count, "取消过滤后 Ctrl+A 恢复。");

            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(450));
            CompleteLayout(window);
            Assert.IsFalse(viewModel.IsPanelExpanded, "退出过滤后恢复既有收起节奏。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_ChipFilterEmptyShowsHint()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("甲", BoardCategory.Inbox).Subgroup = 1;
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.ChipRail);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var hint = (Border)window.FindName("SearchEmptyHint");
            var hintTitle = (TextBlock)window.FindName("SearchEmptyHintTitle");
            var clearButton = (Button)window.FindName("SearchClearFilterButton");
            var list = (ListBox)window.FindName("BoardList");

            ClickChip(window, 3);

            Assert.AreEqual(Visibility.Visible, hint!.Visibility, "空结果复用 SearchEmptyHint。");
            Assert.AreEqual("该分组暂无卡片", hintTitle!.Text, "文案按来源切换。");
            Assert.AreEqual(0, list!.Items.OfType<BoardItem>().Count());

            clearButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            CompleteLayout(window);

            Assert.AreEqual(Visibility.Collapsed, hint.Visibility);
            Assert.AreEqual(1, list.Items.OfType<BoardItem>().Count(), "清空筛选取消 chip 过滤。");
            Assert.IsFalse(window.IsSubgroupFilterActive);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_GroupedEntryPartitionsAdjacentBySlotWithUnspecifiedFirst()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var unspecified = board.AddText("未指定", BoardCategory.Inbox);
        var slotOne = board.AddText("槽一", BoardCategory.Inbox);
        var slotTwo = board.AddText("槽二", BoardCategory.Inbox);
        var slotOneSecond = board.AddText("槽一又一", BoardCategory.Inbox);
        // 物理顺序（InsertAtTop 逆序）：槽一又一, 槽二, 槽一, 未指定。
        slotOne.Subgroup = 1;
        slotTwo.Subgroup = 2;
        slotOneSecond.Subgroup = 1;
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.Off);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            // →GroupedView＝触发进入分区（普通区 ≥2 不同槽且散布）。
            window.ApplyPreferences(
                window.CurrentPreferences with { SubgroupDisplay = SubgroupDisplayMode.GroupedView });
            InvokePrivateTask(window, "DrainPendingOperationsAsync");
            CompleteLayout(window);

            var order = board.Items(BoardCategory.Inbox).ToArray();
            Assert.AreEqual(4, order.Length);
            Assert.AreSame(unspecified, order[0], "未指定 null 居首。");
            Assert.AreSame(slotOneSecond, order[1], "槽 1 组内保持既有相对序（槽一又一物理在槽一前）。");
            Assert.AreSame(slotOne, order[2]);
            Assert.AreSame(slotTwo, order[3], "槽 2 殿后。");

            var persisted = ReadPersistedJson<BoardSnapshot>(directory, "board.json");
            Assert.IsNotNull(persisted);
            var persistedFirst = persisted!.Items
                .Where(item => item.Category == BoardCategory.Inbox)
                .OrderBy(item => item.Order)
                .First();
            Assert.AreEqual(
                unspecified.Id,
                persistedFirst.Id,
                "分区重写持久化 Order 并落盘（物理顺序即持久化顺序）。");

            // 派生边界：命名组首起组；未指定组首（无前邻）与组内不起。
            Assert.IsFalse(order[0].StartsSubgroupRegion);
            Assert.IsTrue(order[1].StartsSubgroupRegion);
            Assert.IsFalse(order[2].StartsSubgroupRegion);
            Assert.IsTrue(order[3].StartsSubgroupRegion);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_GroupBoundariesReuseDividerHostAndStyleSlots()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("槽一", BoardCategory.Inbox).Subgroup = 1;
        board.AddText("未指定", BoardCategory.Inbox);
        // InsertAtTop 逆序：物理 [未指定, 槽一]——槽一有前邻（未指定≠槽 1）才派生组边界。
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.GroupedView);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList")!;

            // 边界卡（槽一）容器：divider 与组头都宿主在容器模板行 0。
            // 2026-11-06 修订（#57）：组边界改用同宿主独立 SubgroupDivider＋专用
            // 恒可见样式（与「置顶分隔」偏好槽位解耦），改指涉对象不降强度。
            var boundaryItem = board.Items(BoardCategory.Inbox).Single(item => item.Subgroup == 1);
            var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(boundaryItem)!;
            Assert.IsNotNull(container);
            var divider = (Line?)container.Template!.FindName("SubgroupDivider", container);
            var header = (TextBlock?)container.Template!.FindName("SubgroupHeaderText", container);
            Assert.IsNotNull(divider, "组边界宿主在容器模板行 0 的独立 SubgroupDivider（禁第二套边界判定）。");
            Assert.IsNotNull(header);
            Assert.AreEqual(Visibility.Visible, divider.Visibility, "StartsSubgroupRegion 驱动 divider 可见。");
            Assert.AreEqual(Visibility.Visible, header.Visibility, "命名组显示组头。");
            Assert.AreEqual("重要", header.Text, "组头文本绑派生 SubgroupHeader。");
            Assert.AreSame(
                window.Resources["SubgroupDividerStyle"],
                divider.Style,
                "组边界用专用恒可见样式（与置顶分隔偏好槽位解耦，视觉语言同源）。");

            // 非边界卡（未指定）divider 塌缩。
            var plainItem = board.Items(BoardCategory.Inbox).Single(item => item.Subgroup is null);
            var plainContainer = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(plainItem)!;
            var plainDivider = (Line?)plainContainer.Template!.FindName("SubgroupDivider", plainContainer);
            Assert.AreEqual(Visibility.Collapsed, plainDivider!.Visibility);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_GroupDividerVisibleEvenWhenPinnedDividerPreferenceNone()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("槽一", BoardCategory.Inbox).Subgroup = 1;
        board.AddText("未指定", BoardCategory.Inbox);
        // InsertAtTop 逆序：物理 [未指定, 槽一]——槽一为组边界卡。
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.GroupedView);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            // 「置顶分隔＝无」偏好注入照既有置顶分隔样式用例口径（ApplyPreferences）；
            // 显式保住 GroupedView（Default 的 SubgroupDisplay 是 Off，重下发不得回退形态）。
            window.ApplyPreferences(AppPreferences.Default with
            {
                PinnedDivider = PinnedDividerStyle.None,
                SubgroupDisplay = SubgroupDisplayMode.GroupedView
            });
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList")!;

            // 组边界与置顶分隔偏好解耦（2026-11-06，#57）：置顶 divider 走 None 态
            // （零高+空描边）的同时，组边界 divider 恒可见——Stroke 非空且 1 DIP。
            var boundaryItem = board.Items(BoardCategory.Inbox).Single(item => item.Subgroup == 1);
            var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(boundaryItem)!;
            Assert.IsNotNull(container);
            var pinned = (Line?)container.Template!.FindName("PinnedDivider", container);
            var divider = (Line?)container.Template!.FindName("SubgroupDivider", container);
            Assert.IsNotNull(pinned);
            Assert.IsNotNull(divider);
            Assert.AreSame(
                window.FindResource("PinnedDividerNoneStyle"),
                window.Resources["PinnedDividerActiveStyle"],
                "前置：置顶分隔偏好＝无已换装槽位（连坐源就位）。");
            Assert.AreEqual(Visibility.Collapsed, pinned!.Visibility, "前置：本卡非置顶边界，置顶 divider 塌缩（StartsNormalRegion 未触发）。");
            Assert.AreEqual(Visibility.Visible, divider!.Visibility, "组边界不受置顶分隔偏好连坐（恒可见）。");
            Assert.IsNotNull(divider.Stroke, "组边界描边非空（BorderBrush 实线）。");
            Assert.AreSame(window.FindResource("BorderBrush"), divider.Stroke, "组边界用 BorderBrush（视觉语言与置顶分隔实线同源）。");
            Assert.AreEqual(1d, divider.Height, "组边界 1 DIP（同几何 token）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_GroupedViewZeroBoundaryShowsGuidance()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("甲", BoardCategory.Inbox);
        board.AddText("乙", BoardCategory.Inbox);
        // 全部未指派：GroupedView 无组边界渲染——引导条在场，告知归组方法。
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.GroupedView);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var guidance = (Border)window.FindName("GroupedViewGuidance")!;

            Assert.AreEqual(Visibility.Visible, guidance.Visibility, "零组边界（全未指派）＝引导条可见。");
            Assert.IsFalse(guidance.IsHitTestVisible, "引导条纯信息性，命中穿透回 BoardList。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_GroupedViewSingleSlotStillShowsGuidance()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("甲", BoardCategory.Inbox).Subgroup = 2;
        board.AddText("乙", BoardCategory.Inbox).Subgroup = 2;
        // 全部同槽：非置顶卡槽值并集恰 1 个＝仍零组边界渲染——引导条保持在场。
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.GroupedView);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var guidance = (Border)window.FindName("GroupedViewGuidance")!;

            Assert.AreEqual(Visibility.Visible, guidance.Visibility, "全部同槽（无边界）＝引导条可见。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_GroupedViewGuidanceHiddenWhenBoundariesForm()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("甲", BoardCategory.Inbox);
        board.AddText("乙", BoardCategory.Inbox);
        // 单槽零边界开局（进入分区为 no-op，零异步竞态）：引导条在场。
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.GroupedView);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var guidance = (Border)window.FindName("GroupedViewGuidance")!;
            Assert.AreEqual(Visibility.Visible, guidance.Visibility, "前置：零边界，引导条在场。");

            // 直改槽值（照 GroupedEntry 用例直改口径）再经模式单点重下发：Reindex
            // 同步派生 ≥2 槽边界，引导条随即退位（不与边界信息双显）。
            board.Items(BoardCategory.Inbox).Last().Subgroup = 2;
            window.ApplyPreferences(
                window.CurrentPreferences with { SubgroupDisplay = SubgroupDisplayMode.GroupedView });
            CompleteLayout(window);

            Assert.IsTrue(
                board.Items(BoardCategory.Inbox).Select(item => item.Subgroup).Distinct().Count() >= 2,
                "前置：≥2 槽（组边界已派生）。");
            Assert.AreEqual(
                Visibility.Collapsed,
                guidance.Visibility,
                "组边界渲染后引导条退位。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_GroupedViewGuidanceYieldsToEmptyPanelState()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        // 空面板：内容桶空态语义优先，引导条让位。
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.GroupedView);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var guidance = (Border)window.FindName("GroupedViewGuidance")!;
            var boardEmpty = (StackPanel)window.FindName("BoardEmptyState");

            Assert.AreEqual(
                Visibility.Visible,
                boardEmpty!.Visibility,
                "前置：空面板内容桶空态在场。");
            Assert.AreEqual(
                Visibility.Collapsed,
                guidance.Visibility,
                "面板空＝引导条让位 BoardEmptyState。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_GroupHeadersShowResolvedNamesSharedWithChipRail()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("槽一", BoardCategory.Inbox).Subgroup = 1;
        board.AddText("未指定", BoardCategory.Inbox);
        // InsertAtTop 逆序：物理 [未指定, 槽一]——槽一是命名组边界卡。
        var settings = WindowSettings.Default
            .WithSubgroupName(BoardCategory.Inbox, 1, "急件");
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.GroupedView, settings);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList")!;
            var boundaryItem = board.Items(BoardCategory.Inbox).Single(item => item.Subgroup == 1);
            var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(boundaryItem)!;
            var header = (TextBlock?)container.Template!.FindName("SubgroupHeaderText", container);

            Assert.AreEqual("急件", header!.Text, "组头按名清单逐槽解析。");
            Assert.AreEqual("急件", boundaryItem.SubgroupHeader);

            // 名清单与形态①共用：切 ChipRail 后轨名同一解析。
            window.ApplyPreferences(
                window.CurrentPreferences with { SubgroupDisplay = SubgroupDisplayMode.ChipRail });
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            Assert.AreEqual("急件", viewModel.SubgroupChips[1].DisplayName);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_PinnedRegionStaysUngrouped()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var pinned = board.AddText("置顶卡", BoardCategory.Inbox);
        board.SetPinnedMany([pinned.Id], true);
        pinned.Subgroup = 2;
        board.AddText("未指定", BoardCategory.Inbox);
        board.AddText("槽一", BoardCategory.Inbox).Subgroup = 1;
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.Off);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            window.ApplyPreferences(
                window.CurrentPreferences with { SubgroupDisplay = SubgroupDisplayMode.GroupedView });
            InvokePrivateTask(window, "DrainPendingOperationsAsync");
            CompleteLayout(window);

            var order = board.Items(BoardCategory.Inbox).ToArray();
            Assert.AreSame(pinned, order[0], "置顶区不参与分区（置顶优先）。");
            Assert.IsFalse(pinned.StartsSubgroupRegion, "置顶卡不派生组边界。");
            Assert.IsNull(pinned.SubgroupHeader, "置顶卡无组头（槽字段保留休眠）。");
            Assert.AreEqual(2, pinned.Subgroup, "槽字段保留。");

            var list = (ListBox)window.FindName("BoardList")!;
            var pinnedContainer = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(pinned)!;
            var header = (TextBlock?)pinnedContainer.Template!.FindName("SubgroupHeaderText", pinnedContainer);
            Assert.AreEqual(Visibility.Collapsed, header!.Visibility);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_GroupedDragIsPlainReorderWithoutAssignment()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("未指定", BoardCategory.Inbox);
        var slotOne = board.AddText("槽一", BoardCategory.Inbox);
        board.AddText("槽二", BoardCategory.Inbox).Subgroup = 2;
        slotOne.Subgroup = 1;
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.GroupedView);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            // 构造不触发分区：装载物理序＝AddText 逆序 [槽二(2), 槽一(1), 未指定(null)]。
            var before = board.Items(BoardCategory.Inbox).ToArray();
            Assert.AreEqual(3, before.Length);
            Assert.IsTrue(before[1].StartsSubgroupRegion, "槽一与前邻槽二不同槽即成边界（邻接派生）。");

            // 同分类拖放＝纯重排（MoveMany 管线零改动）：把槽一拖到未指定之后
            //（pre-removal 索引语义：目标 3＝移除后插到剩余两项之尾）。
            board.MoveMany([slotOne.Id], BoardCategory.Inbox, 3);
            CompleteLayout(window);

            Assert.AreEqual(1, slotOne.Subgroup, "拖出组不改槽字段（无组采用语义）。");
            var after = board.Items(BoardCategory.Inbox).ToArray();
            Assert.AreEqual(slotOne.Id, after[2].Id);
            Assert.IsTrue(slotOne.StartsSubgroupRegion, "拖出组即形成新 run（派生边界跟随物理邻接）。");
            Assert.AreEqual("重要", slotOne.SubgroupHeader, "新 run 的组头按自身槽解析。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_ModeSwitchClearsFilterAndRailWithoutResidue()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("甲", BoardCategory.Inbox).Subgroup = 1;
        board.AddText("乙", BoardCategory.Inbox);
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.ChipRail);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var viewModel = (MainWindowViewModel)window.DataContext;

            ClickChip(window, 1);
            Assert.IsTrue(window.IsSubgroupFilterActive);

            // ChipRail→Off：轨塌缩＋过滤清除＋Filter 重评估，无残留。
            window.ApplyPreferences(
                window.CurrentPreferences with { SubgroupDisplay = SubgroupDisplayMode.Off });
            CompleteLayout(window);

            Assert.AreEqual(Visibility.Collapsed, FindSubgroupRail(window).Visibility);
            Assert.IsFalse(window.IsSubgroupFilterActive);
            Assert.IsFalse(viewModel.SubgroupChips[1].IsFilterActive, "chip 选中态清零。");
            Assert.IsNull(list!.Items.Filter);
            Assert.AreEqual(2, list.Items.OfType<BoardItem>().Count());
            foreach (var item in board.Items(BoardCategory.Inbox))
            {
                Assert.IsFalse(item.StartsSubgroupRegion, "Off 派生标记清零。");
            }

            // Off→ChipRail：轨出现，过滤保持清除。
            window.ApplyPreferences(
                window.CurrentPreferences with { SubgroupDisplay = SubgroupDisplayMode.ChipRail });
            CompleteLayout(window);
            Assert.AreEqual(Visibility.Visible, FindSubgroupRail(window).Visibility);
            Assert.IsFalse(window.IsSubgroupFilterActive);
            Assert.IsNull(list.Items.Filter);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_AssignmentAndPartitionUndoDoesNotRegressExistingUndoPaths()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var first = board.AddText("甲", BoardCategory.Inbox);
        var second = board.AddText("乙", BoardCategory.Inbox);
        // 物理 [乙(1), 甲(null)]——分区才有实际重排可撤销。
        first.Subgroup = null;
        second.Subgroup = 1;
        var store = new SubgroupRecordingStore(directory.Root);
        var window = CreateWindow(
            board,
            new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter()),
            WindowSettings.Default,
            new DefaultCaptureCategoryState(),
            new ImageNormalizer(store.ImagesDirectory),
            preferences: AppPreferences.Default with { SubgroupDisplay = SubgroupDisplayMode.Off });

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            // 指派→撤销：槽恢复原值（可撤销批）。撤销链线程口径沿 Undo.cs 先例用同步
            // 完成 store（真实落盘持久化由指派/改名/分区用例以 LocalStore 锁定）。
            var mutations = new BoardMutationService(board, store, _ => { });
            RunMutationsTask(window, _ => mutations.AssignSubgroupAsync([first.Id, second.Id], 2));
            Assert.AreEqual(2, first.Subgroup);
            Assert.AreEqual(2, second.Subgroup);

            RunMutationsTask(window, _ => mutations.UndoLastAsync());
            Assert.IsNull(first.Subgroup, "撤销恢复指派前槽值（null 保持 null）。");
            Assert.AreEqual(1, second.Subgroup);

            // 分区→撤销：顺序恢复。
            RunMutationsTask(window, _ => mutations.PartitionSubgroupsAsync(BoardCategory.Inbox));
            var partitionedIds = board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray();
            Assert.AreEqual(2, board.Items(BoardCategory.Inbox).Count());
            RunMutationsTask(window, _ => mutations.UndoLastAsync());
            CollectionAssert.AreNotEqual(
                partitionedIds,
                board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray(),
                "撤销恢复分区前顺序。");

            // 既有删除撤销路径不回归：删除一批再撤销仍插回。
            RunMutationsTask(window, _ => mutations.DeleteManyAsync([first.Id]));
            Assert.IsNull(board.FindItem(first.Id));
            RunMutationsTask(window, _ => mutations.UndoLastAsync());
            Assert.IsNotNull(board.FindItem(first.Id), "既有删除撤销路径不回归。");
            Assert.AreEqual(6, store.SaveCount, "指派/撤销/分区/撤销/删除/撤销各落盘一次。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    /// <summary>撤销链测试用同步完成 store（照 Undo.cs RecordingUndoStore 先例），
    /// 并按同一口径计数落盘次数。</summary>
    private sealed class SubgroupRecordingStore(string root) : IBoardStore
    {
        public int SaveCount { get; private set; }

        public string ImagesDirectory { get; } = Path.Combine(root, "images");

        public Task<BoardSnapshot> LoadBoardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BoardSnapshot());

        public Task SaveBoardAsync(BoardSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task<WindowSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(WindowSettings.Default);

        public Task SaveSettingsAsync(WindowSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public bool TryDeleteImage(string? absolutePath) => true;
    }

    [STATestMethod]
    public void Subgroup_EvidenceBothThemes()
    {
        // 契约 #57 证据：浅/深 ×（关/按钮轨/视觉分组）六宫格——Off 轨零占位、
        // ChipRail 四槽固定高、GroupedView 组边界 divider＋组头。环境变量未设置
        // 时跳过（CI 无证据目录即 Inconclusive，不阻塞质量门）。
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("FTS_SUBGROUP_EVIDENCE_DIR")))
        {
            Assert.Inconclusive("未设置 FTS_SUBGROUP_EVIDENCE_DIR，跳过发布证据产出。");
        }

        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("未分组卡片", BoardCategory.Inbox);
        var urgentFirst = board.AddText("急件一：今天必须交付的稿子", BoardCategory.Inbox);
        var urgentSecond = board.AddText("急件二：回复合作确认", BoardCategory.Inbox);
        urgentFirst.Subgroup = 1;
        urgentSecond.Subgroup = 1;
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.Off);

        try
        {
            window.Show();
            window.Topmost = true;
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var shell = (Border)window.FindName("WindowShell");

            foreach (var theme in new[] { DesignTheme.Light, DesignTheme.Dark })
            {
                DesignThemeManager.Apply(window, theme);
                CompleteLayout(window);
                var themeName = theme == DesignTheme.Light ? "light" : "dark";

                // 关：轨塌缩零占位、无组边界（与现状逐 DIP 一致）。
                window.ApplyPreferences(
                    window.CurrentPreferences with { SubgroupDisplay = SubgroupDisplayMode.Off });
                CompleteLayout(window);
                Assert.AreEqual(Visibility.Collapsed, FindSubgroupRail(window).Visibility);
                SaveVisualEvidence(shell!, $"subgroup-off-{themeName}.png", "FTS_SUBGROUP_EVIDENCE_DIR");

                // 按钮轨：头行下固定高四槽。
                window.ApplyPreferences(
                    window.CurrentPreferences with { SubgroupDisplay = SubgroupDisplayMode.ChipRail });
                CompleteLayout(window);
                Assert.AreEqual(Visibility.Visible, FindSubgroupRail(window).Visibility);
                SaveVisualEvidence(shell!, $"subgroup-chiprail-{themeName}.png", "FTS_SUBGROUP_EVIDENCE_DIR");

                // 视觉分组：进入分区＋组边界 divider＋组头。
                window.ApplyPreferences(
                    window.CurrentPreferences with { SubgroupDisplay = SubgroupDisplayMode.GroupedView });
                InvokePrivateTask(window, "DrainPendingOperationsAsync");
                CompleteLayout(window);
                Assert.IsTrue(
                    board.Items(BoardCategory.Inbox).Any(item => item.StartsSubgroupRegion),
                    "视觉分组必须呈现组边界。");
                SaveVisualEvidence(shell!, $"subgroup-groupedview-{themeName}.png", "FTS_SUBGROUP_EVIDENCE_DIR");
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }
}
