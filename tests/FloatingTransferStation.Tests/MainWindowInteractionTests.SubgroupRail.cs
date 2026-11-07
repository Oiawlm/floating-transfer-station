using System.Reflection;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
/// 悬停过滤＋守卫链/单击设默认环/互斥/改名/拖卡归属/过滤态行为）、「全部」伪
/// chip 聚合视图（契约 #57 修订：开关默认关/恒居首五列/悬停全量/粘性与四路径
/// 退出/搜索压倒/禁令继承/空态走 BoardEmptyState/单击无操作/不参与槽序与归属/
/// 开关关闭清过滤/GroupedView 与 Off 零呈现）、形态②视觉分组（进入分区/边界
/// 复用 divider 宿主/组头/置顶不分组/纯重排）、模式切换零残留、撤销不回归与
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
        WindowSettings? settings = null,
        bool subgroupAllChipEnabled = false)
    {
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        return CreateWindow(
            board,
            store,
            settings ?? WindowSettings.Default,
            new DefaultCaptureCategoryState(),
            new ImageNormalizer(store.ImagesDirectory),
            preferences: AppPreferences.Default with
            {
                SubgroupDisplay = mode,
                SubgroupAllChipEnabled = subgroupAllChipEnabled
            });
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

    /// <summary>合成悬停进入（照 DragAndDrop.cs :501 MouseEnter 合成先例）：契约 #57
    /// 修订的进入驱动——悬停即按槽过滤。</summary>
    private static void HoverChip(MainWindow window, int slot)
    {
        var chip = FindSubgroupChip(window, slot);
        chip.RaiseEvent(NewMouseEventArgs(UIElement.MouseEnterEvent, chip));
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
            // 按槽寻位断言（契约 #57 修订）：集合顺序＝激活分类槽序，不再假设恒等序。
            Assert.AreEqual("待分类", chips.Single(chip => chip.Slot == 0).DisplayName);
            Assert.AreEqual("急件", chips.Single(chip => chip.Slot == 1).DisplayName, "自定义名经逐槽解析喂入。");
            Assert.AreEqual("不重要", chips.Single(chip => chip.Slot == 2).DisplayName);
            Assert.AreEqual("待办", chips.Single(chip => chip.Slot == 3).DisplayName);
            foreach (var chip in chips)
            {
                Assert.AreEqual(
                    DefaultMarkerState.Inactive,
                    chip.DefaultMarkerState,
                    "装载期无默认子分组＝全部灰环（轨可见时每 chip 恒有环，#56 修订）。");
            }

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
    public void Subgroup_ChipHoverFiltersBySlot()
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

            HoverChip(window, 1);

            Assert.IsTrue(window.IsSubgroupFilterActive);
            Assert.IsTrue(viewModel.SubgroupChips[1].IsFilterActive, "激活 chip 选中态。");
            CollectionAssert.AreEquivalent(
                new[] { "甲", "乙" },
                list.Items.OfType<BoardItem>().Select(item => item.Text).ToArray(),
                "chip 悬停按槽过滤（只经 Items.Filter 单点）。");
            Assert.AreEqual(3, viewModel.ActivePanel!.Items.Count, "源集合不得被过滤改动。");
            Assert.AreEqual(0, list.SelectedItems.Count, "进入过滤视图清空选择（#37 口径）。");

            // 过滤退出＝引导条「清空」按钮（原「再点同 chip 取消」随单击过滤退役）。
            ((Button)window.FindName("SearchClearFilterButton"))!
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            CompleteLayout(window);

            Assert.IsFalse(window.IsSubgroupFilterActive);
            Assert.IsFalse(viewModel.SubgroupChips[1].IsFilterActive);
            Assert.AreEqual(3, list.Items.OfType<BoardItem>().Count(), "清空筛选＝取消过滤还原。");
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

            HoverChip(window, 1);
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
    public void Subgroup_ChipClickDuringSearchSetsDefaultWithoutTouchingSearch()
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

            Assert.IsTrue(viewModel.IsSearchActive, "搜索态 chip 单击＝仅设默认（不退搜索，#37 修订）。");
            Assert.AreEqual(
                Visibility.Collapsed,
                ((StackPanel)window.FindName("HeaderActions")).Visibility,
                "搜索条保持展开（头部操作区仍让位）。");
            Assert.IsFalse(window.IsSubgroupFilterActive, "搜索态单击不进过滤。");
            Assert.IsFalse(viewModel.SubgroupChips[2].IsFilterActive, "chip 选中态不因单击新增。");
            CollectionAssert.AreEquivalent(
                new[] { "苹果一", "苹果二" },
                list!.Items.OfType<BoardItem>().Select(item => item.Text).ToArray(),
                "搜索结果不受单击影响（两卡都命中关键词，过滤仍由搜索谓词承担）。");
            Assert.AreEqual(
                DefaultMarkerState.Active,
                viewModel.SubgroupChips[2].DefaultMarkerState,
                "单击已设默认子分组（待分类＝装载期默认接收，紫）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_ChipHoverSwitchesFilterWhenSlidingToAnotherChip()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("甲", BoardCategory.Inbox).Subgroup = 1;
        board.AddText("乙", BoardCategory.Inbox).Subgroup = 2;
        board.AddText("丙", BoardCategory.Inbox);
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.ChipRail);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var viewModel = (MainWindowViewModel)window.DataContext;

            HoverChip(window, 1);
            CollectionAssert.AreEquivalent(
                new[] { "甲" },
                list!.Items.OfType<BoardItem>().Select(item => item.Text).ToArray(),
                "悬停即进入该槽。");

            // 滑到其他 chip＝直接换组（对齐分类轨「途经即切」，无需先退出）。
            HoverChip(window, 2);
            CollectionAssert.AreEquivalent(
                new[] { "乙" },
                list.Items.OfType<BoardItem>().Select(item => item.Text).ToArray(),
                "滑到其他 chip 即切换过滤。");
            Assert.IsFalse(viewModel.SubgroupChips[1].IsFilterActive, "旧槽选中态让位。");
            Assert.IsTrue(viewModel.SubgroupChips[2].IsFilterActive);
            Assert.AreEqual(3, viewModel.ActivePanel!.Items.Count, "源集合不动。");

            // 滑回已过滤槽＝幂等（不重复清选择/重过滤）。
            var keptSelection = list.SelectedItems;
            HoverChip(window, 2);
            Assert.AreSame(keptSelection, list.SelectedItems, "同槽幂等。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_ChipHoverBlockedDuringNameEdit()
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
            Assert.IsTrue(viewModel.SubgroupChips[1].IsEditingName, "前置：改名会话中。");

            HoverChip(window, 2);

            Assert.IsFalse(window.IsSubgroupFilterActive, "改名会话中悬停不进入过滤。");
            Assert.IsFalse(viewModel.SubgroupChips[2].IsFilterActive);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_ChipHoverBlockedDuringCardDrag()
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
            var viewModel = (MainWindowViewModel)window.DataContext;

            InvokePrivate(window, "BeginPanelDrag");
            HoverChip(window, 1);

            Assert.IsFalse(window.IsSubgroupFilterActive, "拖拽在途悬停不进入过滤。");
            Assert.IsFalse(viewModel.SubgroupChips[1].IsFilterActive);

            // 拖拽会话结束后悬停恢复生效（守卫只挡在途）。
            InvokePrivate(window, "EndPanelDrag");
            CompleteLayout(window);
            HoverChip(window, 1);
            Assert.IsTrue(window.IsSubgroupFilterActive, "会话结束悬停即进入。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_ChipHoverBlockedWhileExternalDropRailVisible()
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
            var viewModel = (MainWindowViewModel)window.DataContext;

            // 外部拖入轨道可见（照 ExternalRail 先例：外部数据 DragEnter 换轨）。
            var data = new DataObject(DataFormats.UnicodeText, "外部文本");
            var tab = FindCategoryTab(
                window,
                viewModel.Categories.Single(category =>
                    category.Category == BoardCategory.CustomerOriginal));
            tab.RaiseEvent(NewDragEventArgs(data, DragDrop.DragEnterEvent, tab));
            CompleteLayout(window);
            Assert.IsTrue(viewModel.IsExternalDropRailVisible, "前置：外部拖放轨道在场。");

            var chip = FindSubgroupChip(window, 1);
            chip.RaiseEvent(NewMouseEventArgs(UIElement.MouseEnterEvent, chip));
            CompleteLayout(window);

            Assert.IsFalse(window.IsSubgroupFilterActive, "外部拖放轨道在场悬停不进入过滤。");
            Assert.IsFalse(viewModel.SubgroupChips[1].IsFilterActive);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_ChipHoverBlockedWhileSearchActive()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("苹果一", BoardCategory.Inbox).Subgroup = 1;
        board.AddText("苹果二", BoardCategory.Inbox).Subgroup = 1;
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

            HoverChip(window, 1);

            Assert.IsFalse(window.IsSubgroupFilterActive, "搜索态悬停不进入过滤（#37 搜索压倒）。");
            Assert.IsFalse(viewModel.SubgroupChips[1].IsFilterActive, "搜索态不新增 chip 选中态。");
            Assert.IsTrue(viewModel.IsSearchActive, "悬停不退出搜索。");
            CollectionAssert.AreEquivalent(
                new[] { "苹果一", "苹果二" },
                list!.Items.OfType<BoardItem>().Select(item => item.Text).ToArray(),
                "搜索结果不受悬停影响。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_ChipClickSetsDefaultSubgroupPerCategoryAndMovesRing()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("甲", BoardCategory.Inbox);
        board.AddText("乙", BoardCategory.CustomerOriginal);
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.ChipRail);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            ClickChip(window, 1);
            Assert.AreEqual(DefaultMarkerState.Active, viewModel.SubgroupChips[1].DefaultMarkerState, "单击设默认环（紫＝有效默认落点）。");
            Assert.AreEqual(DefaultMarkerState.Inactive, viewModel.SubgroupChips[3].DefaultMarkerState, "其余 chip 灰环（具备能力非当前）。");

            ClickChip(window, 3);
            Assert.AreEqual(DefaultMarkerState.Inactive, viewModel.SubgroupChips[1].DefaultMarkerState, "再单击他槽＝紫环移动。");
            Assert.AreEqual(DefaultMarkerState.Active, viewModel.SubgroupChips[3].DefaultMarkerState);

            // 同槽再单击＝幂等（差分语义，环保持）。
            ClickChip(window, 3);
            Assert.AreEqual(DefaultMarkerState.Active, viewModel.SubgroupChips[3].DefaultMarkerState);

            // 每分类独立一份：重要分类非默认接收＝全灰（默认记忆仍各自保存，
            // 切回恢复）；重要分类设默认后仍灰（紫 iff 激活分类=默认接收分类）。
            ExpandCategory(window, BoardCategory.CustomerOriginal);
            CompleteLayout(window);
            foreach (var chip in viewModel.SubgroupChips)
            {
                Assert.AreEqual(
                    DefaultMarkerState.Inactive,
                    chip.DefaultMarkerState,
                    "重要分类非默认接收＝全部灰环（用户示例）。");
            }

            ClickChip(window, 2);
            Assert.AreEqual(
                DefaultMarkerState.Inactive,
                viewModel.SubgroupChips[2].DefaultMarkerState,
                "重要分类独立设默认（会话态记忆）但环仍灰。");

            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            Assert.AreEqual(
                DefaultMarkerState.Active,
                viewModel.SubgroupChips[3].DefaultMarkerState,
                "切回待分类恢复其紫环（每分类独立）。");
            Assert.AreEqual(DefaultMarkerState.Inactive, viewModel.SubgroupChips[2].DefaultMarkerState);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_DefaultSubgroupRingReusesCategoryMarkerStyle()
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
            var chipHost = FindSubgroupChip(window, 1);

            // 环＝复用 #56 默认接收分类单圆环样式（同实例、同几何规格）；环态与
            // 用色由样式 DefaultMarkerState 触发器单点驱动（模板级触发器已退役）。
            var markerStyle = (Style)window.FindResource("CategoryDefaultMarkerStyle");
            var marker = FindDescendants<System.Windows.Shapes.Path>(chipHost)
                .Single(candidate => ReferenceEquals(candidate.Style, markerStyle));
            var markerData = marker.Data.ToString();
            StringAssert.Contains(markerData, "A8,8");
            Assert.AreEqual(1.25d, marker.StrokeThickness);
            Assert.AreEqual(10d, marker.Width);
            Assert.AreEqual(10d, marker.Height);
            Assert.AreEqual(1, Panel.GetZIndex(marker), "环在文字之上（照分类标记层序）。");
            Assert.AreEqual(1d, marker.Opacity, "未设默认＝灰环（Inactive 恒有环，#56 修订）。");
            Assert.AreSame(
                window.FindResource("SecondaryTextBrush"),
                marker.Stroke,
                "灰环描边＝SecondaryTextBrush（复用非新 token，双主题整本替换自适应）。");

            ClickChip(window, 1);

            Assert.AreEqual(DefaultMarkerState.Active, viewModel.SubgroupChips[1].DefaultMarkerState);
            Assert.AreEqual(1d, marker.Opacity, "设默认后环保持可见。");
            Assert.AreSame(
                window.FindResource("AccentBrush"),
                marker.Stroke,
                "有效默认落点描边＝AccentBrush 主题自适应紫。");
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

            // 改名会话中悬停不进入过滤（守卫链，契约 #57 修订）。
            HoverChip(window, 2);
            Assert.IsFalse(window.IsSubgroupFilterActive, "改名会话中 MouseEnter 不进入过滤。");
            Assert.IsFalse(viewModel.SubgroupChips[2].IsFilterActive);

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

            // 拖拽在途守卫：拖卡滑过其他 chip 的 MouseEnter 不得切换过滤（契约 #57 修订）。
            InvokePrivate(window, "BeginPanelDrag");
            var passOverChip = FindSubgroupChip(window, 1);
            passOverChip.RaiseEvent(NewMouseEventArgs(UIElement.MouseEnterEvent, passOverChip));
            CompleteLayout(window);
            Assert.IsFalse(window.IsSubgroupFilterActive, "拖拽在途 MouseEnter 不进入过滤。");
            Assert.IsFalse(viewModel.SubgroupChips[1].IsFilterActive);
            InvokePrivate(window, "EndPanelDrag");
            CompleteLayout(window);

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

            HoverChip(window, 1);
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

            // 退出过滤＝引导条「清空」按钮（单击 toggle 已退役）。
            ((Button)window.FindName("SearchClearFilterButton"))!
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            CompleteLayout(window);
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

            HoverChip(window, 3);

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
    public void Subgroup_ChipFilterOnEmptyPanelShowsHintWithoutBoardEmptyStateOverlap()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        // 面板本就空：内容桶空态已可见（BoardEmptyState），此时悬停 chip 进入过滤视图。
        // 修复前进入路径只重评 SearchEmptyHint 的过滤空视图、从不重评
        // BoardEmptyState——两元素同格（Grid.Row=2）叠加两段空态文字。
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.ChipRail);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var hint = (Border)window.FindName("SearchEmptyHint");
            var boardEmpty = (StackPanel)window.FindName("BoardEmptyState");
            Assert.AreEqual(
                Visibility.Visible,
                boardEmpty!.Visibility,
                "前置：空面板内容桶空态在场。");

            HoverChip(window, 1);

            Assert.AreEqual(
                Visibility.Visible,
                hint!.Visibility,
                "过滤空视图复用 SearchEmptyHint（「该分组暂无卡片」）。");
            Assert.AreEqual(
                Visibility.Collapsed,
                boardEmpty.Visibility,
                "两空态同格互斥：chip 过滤接管后内容桶空态必须退位（修复前叠加）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_ChipFilterCancelOnEmptyPanelRestoresBoardEmptyState()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.ChipRail);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var hint = (Border)window.FindName("SearchEmptyHint");
            var boardEmpty = (StackPanel)window.FindName("BoardEmptyState");

            HoverChip(window, 1);
            ((Button)window.FindName("SearchClearFilterButton"))!
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            CompleteLayout(window);

            Assert.IsFalse(window.IsSubgroupFilterActive, "前置：引导条「清空」＝取消过滤。");
            Assert.AreEqual(
                Visibility.Collapsed,
                hint!.Visibility,
                "取消过滤后 SearchEmptyHint 退位。");
            Assert.AreEqual(
                Visibility.Visible,
                boardEmpty!.Visibility,
                "退位方向守卫：内容桶空态恢复在场（空面板回归原空态）。");
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

            HoverChip(window, 1);
            Assert.IsTrue(window.IsSubgroupFilterActive);
            // 先设默认子分组（单击 chip 2）：模式切换不清理收集落点记忆（会话态）。
            ClickChip(window, 2);
            Assert.AreEqual(DefaultMarkerState.Active, viewModel.SubgroupChips[2].DefaultMarkerState);

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

            // Off→ChipRail：轨出现，过滤保持清除，收集落点环不受模式切换影响。
            window.ApplyPreferences(
                window.CurrentPreferences with { SubgroupDisplay = SubgroupDisplayMode.ChipRail });
            CompleteLayout(window);
            Assert.AreEqual(Visibility.Visible, FindSubgroupRail(window).Visibility);
            Assert.IsFalse(window.IsSubgroupFilterActive);
            Assert.IsNull(list.Items.Filter);
            Assert.AreEqual(
                DefaultMarkerState.Active,
                viewModel.SubgroupChips[2].DefaultMarkerState,
                "切回 ChipRail 默认紫环仍正确。");
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

                // 按钮轨：头行下固定高四槽＋收集落点环（单击设默认后右上角圆环）。
                window.ApplyPreferences(
                    window.CurrentPreferences with { SubgroupDisplay = SubgroupDisplayMode.ChipRail });
                CompleteLayout(window);
                Assert.AreEqual(Visibility.Visible, FindSubgroupRail(window).Visibility);
                ClickChip(window, 1);
                CompleteLayout(window);
                Assert.AreEqual(
                    DefaultMarkerState.Active,
                    ((MainWindowViewModel)window.DataContext).SubgroupChips[1].DefaultMarkerState,
                    "证据帧必须含收集落点紫环。");
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

    /// <summary>环语法断言单点（契约 #56 修订）：轨可见时每 chip 恒有环——
    /// purpleSlot 为 null 时全灰，否则该槽紫、余灰（描边经样式触发器单点换色）。</summary>
    private static void AssertChipRingGrammar(MainWindow window, int? purpleSlot)
    {
        var markerStyle = (Style)window.FindResource("CategoryDefaultMarkerStyle");
        var accent = window.FindResource("AccentBrush");
        var secondary = window.FindResource("SecondaryTextBrush");
        for (var slot = 0; slot < 4; slot++)
        {
            var marker = FindDescendants<System.Windows.Shapes.Path>(FindSubgroupChip(window, slot))
                .Single(candidate => ReferenceEquals(candidate.Style, markerStyle));
            Assert.AreEqual(1d, marker.Opacity, $"槽 {slot} 恒有环（具备落点能力）。");
            Assert.AreSame(
                slot == purpleSlot ? accent : secondary,
                marker.Stroke,
                $"槽 {slot} 环色＝{(slot == purpleSlot ? "紫（Active＝有效默认落点）" : "灰（Inactive＝非默认）")}。");
        }
    }

    [STATestMethod]
    public void Subgroup_RingGrammarPurpleOnlyOnDefaultCaptureCategoryDefaultSlot()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("甲", BoardCategory.Inbox);
        board.AddText("乙", BoardCategory.CustomerOriginal);
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.ChipRail);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            // 重要分类预置默认槽 2（未激活不呈环）；待分类（装载期默认接收）设槽 1。
            Assert.IsTrue(viewModel.SetDefaultSubgroup(BoardCategory.CustomerOriginal, 2));
            ClickChip(window, 1);
            AssertChipRingGrammar(window, purpleSlot: 1);

            // 默认接收迁移到重要分类：激活轨（待分类）即刻原紫变灰（紫 iff
            // 激活分类=默认接收分类）。
            Assert.IsTrue(viewModel.SetDefaultCaptureCategory(BoardCategory.CustomerOriginal));
            AssertChipRingGrammar(window, purpleSlot: null);

            // 激活迁到新默认接收分类：其默认槽亮紫、余灰。
            ExpandCategory(window, BoardCategory.CustomerOriginal);
            CompleteLayout(window);
            AssertChipRingGrammar(window, purpleSlot: 2);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_RingGrammarAllGrayWhenCategoryNotDefaultCapture()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("乙", BoardCategory.CustomerOriginal);
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.ChipRail);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.CustomerOriginal);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            // 用户示例（2026-11-07）：重要非默认接收——即使设了默认子分组也全灰。
            Assert.AreEqual(BoardCategory.Inbox, viewModel.DefaultCapturePanel.Category, "装载期默认接收＝待分类。");
            ClickChip(window, 2);
            Assert.AreEqual(
                DefaultMarkerState.Inactive,
                viewModel.SubgroupChips.Single(chip => chip.Slot == 2).DefaultMarkerState,
                "非默认接收分类：设默认后环仍灰。");
            AssertChipRingGrammar(window, purpleSlot: null);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_RingGrammarEvidenceBothThemes()
    {
        // 契约 #56 修订证据：浅/深 ×（默认分类轨紫 1 灰 3／非默认分类轨全灰／
        // 主标签紫灰对比）。环境变量未设置时跳过（CI 无证据目录即 Inconclusive，
        // 不阻塞质量门）。
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("FTS_RING_EVIDENCE_DIR")))
        {
            Assert.Inconclusive("未设置 FTS_RING_EVIDENCE_DIR，跳过发布证据产出。");
        }

        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("甲", BoardCategory.Inbox).Subgroup = 1;
        board.AddText("乙", BoardCategory.Inbox).Subgroup = 2;
        board.AddText("丙", BoardCategory.CustomerOriginal).Subgroup = 1;
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.ChipRail);

        try
        {
            window.Show();
            window.Topmost = true;
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var shell = (Border)window.FindName("WindowShell");
            var viewModel = (MainWindowViewModel)window.DataContext;
            var markerStyle = (Style)window.FindResource("CategoryDefaultMarkerStyle");

            foreach (var theme in new[] { DesignTheme.Light, DesignTheme.Dark })
            {
                DesignThemeManager.Apply(window, theme);
                CompleteLayout(window);
                var themeName = theme == DesignTheme.Light ? "light" : "dark";

                // 默认分类轨（装载期默认接收＝待分类）：设默认槽后紫 1 灰 3。
                ClickChip(window, 1);
                CompleteLayout(window);
                AssertChipRingGrammar(window, purpleSlot: 1);
                SaveVisualEvidence(
                    shell!, $"evidence-a1-rail-defaultcapture-{themeName}.png", "FTS_RING_EVIDENCE_DIR");

                // 非默认分类轨（重要）：全灰（用户示例）。
                ExpandCategory(window, BoardCategory.CustomerOriginal);
                CompleteLayout(window);
                AssertChipRingGrammar(window, purpleSlot: null);
                SaveVisualEvidence(
                    shell!, $"evidence-a1-rail-nondefault-{themeName}.png", "FTS_RING_EVIDENCE_DIR");

                // 主标签紫/灰对比：默认接收标签紫、另一内容桶灰、记录/启动无环。
                var accent = window.FindResource("AccentBrush");
                var secondary = window.FindResource("SecondaryTextBrush");
                foreach (var category in viewModel.Categories)
                {
                    var marker = FindDescendants<System.Windows.Shapes.Path>(FindCategoryTab(window, category))
                        .Single(candidate => ReferenceEquals(candidate.Style, markerStyle));
                    if (!BoardCategoryCatalog.IsContentBucket(category.Category))
                    {
                        Assert.AreEqual(0d, marker.Opacity, "记录/启动无环（永不具备落点能力）。");
                        continue;
                    }

                    Assert.AreEqual(1d, marker.Opacity, "内容桶主标签恒有环。");
                    Assert.AreSame(
                        category.IsDefaultCapture ? accent : secondary,
                        marker.Stroke,
                        category.IsDefaultCapture ? "默认接收主标签＝紫。" : "另一内容桶主标签＝灰。");
                }

                SaveVisualEvidence(
                    shell!, $"evidence-a1-maintabs-{themeName}.png", "FTS_RING_EVIDENCE_DIR");

                // 下一主题迭代回到默认分类轨。
                ExpandCategory(window, BoardCategory.Inbox);
                CompleteLayout(window);
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    // ---- 「全部」伪 chip（契约 #57 修订，实验开关默认关）：聚合视图窗口级回归 ----

    /// <summary>伪 chip 宿主 Border 判别照 FindSubgroupChip 先例：模板内层共享
    /// DataContext，只有外层宿主 Border 带 Tag（伪 chip 身份＝目录常量「全部」，
    /// 无槽号）。</summary>
    private static Border FindSubgroupAllChip(MainWindow window) =>
        FindDescendants<Border>(FindSubgroupRail(window)).Single(candidate =>
            candidate.DataContext is SubgroupAllChipViewModel &&
            Equals(candidate.Tag, SubgroupCatalog.AllChipName));

    /// <summary>合成悬停进入「全部」（照 HoverChip 合成先例）。</summary>
    private static void HoverAllChip(MainWindow window)
    {
        var chip = FindSubgroupAllChip(window);
        chip.RaiseEvent(NewMouseEventArgs(UIElement.MouseEnterEvent, chip));
        CompleteLayout(window);
    }

    private static UniformGrid FindSubgroupRailGrid(MainWindow window) =>
        FindDescendants<UniformGrid>(FindSubgroupRail(window)).Single();

    [STATestMethod]
    public void Subgroup_AllChipHiddenByDefaultAndRailStaysFourColumns()
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

            Assert.IsFalse(viewModel.SubgroupAllChipEnabled, "「全部」伪 chip 实验开关默认关。");
            Assert.AreEqual(4, viewModel.SubgroupRailItems.Count, "轨投影＝4 槽 chip（现状逐值）。");
            Assert.IsTrue(
                viewModel.SubgroupRailItems.All(item => item is SubgroupChipViewModel),
                "默认无伪 chip 进入投影。");
            Assert.AreEqual(4, viewModel.SubgroupRailColumnCount, "列数投影保持 4。");
            Assert.AreEqual(4, FindSubgroupRailGrid(window).Columns, "UniformGrid 四列（现状逐值）。");
            Assert.AreEqual(4, viewModel.SubgroupChips.Count, "槽 chip 集不受开关影响。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_AllChipAppearsFirstAsFifthColumnWhenEnabled()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("内容", BoardCategory.Inbox);
        var window = CreateSubgroupWindow(
            directory,
            board,
            SubgroupDisplayMode.ChipRail,
            subgroupAllChipEnabled: true);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            Assert.IsTrue(viewModel.SubgroupAllChipEnabled);
            Assert.AreEqual(5, viewModel.SubgroupRailItems.Count, "开启后投影＝「全部」＋4 槽。");
            Assert.IsInstanceOfType(
                viewModel.SubgroupRailItems[0],
                typeof(SubgroupAllChipViewModel),
                "「全部」恒居首枚（不参与槽序）。");
            Assert.AreEqual(
                SubgroupCatalog.AllChipName,
                viewModel.SubgroupAllChip.DisplayName,
                "显示名＝目录常量「全部」。");
            Assert.AreEqual(5, viewModel.SubgroupRailColumnCount, "列数投影 4→5。");
            Assert.AreEqual(5, FindSubgroupRailGrid(window).Columns, "UniformGrid 五列等分。");
            Assert.AreEqual(
                SubgroupCatalog.AllChipName,
                FindDescendants<TextBlock>(FindSubgroupAllChip(window)).Single().Text,
                "视觉树首枚渲染「全部」。");
            // 轨道高/面板宽零变化（不为内容加宽结构，#54/#16 精神）。
            Assert.AreEqual(
                WindowSettings.SubgroupRailHeight,
                FindSubgroupRail(window).Height,
                "轨高仍＝结构常量。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_AllChipHoverShowsAllItemsIncludingUngrouped()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("甲", BoardCategory.Inbox).Subgroup = 1;
        board.AddText("乙", BoardCategory.Inbox).Subgroup = 2;
        board.AddText("丙", BoardCategory.Inbox);
        var window = CreateSubgroupWindow(
            directory,
            board,
            SubgroupDisplayMode.ChipRail,
            subgroupAllChipEnabled: true);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var viewModel = (MainWindowViewModel)window.DataContext;

            HoverAllChip(window);

            Assert.IsTrue(window.IsSubgroupFilterActive, "全量态计入过滤谓词（禁令单点继承）。");
            Assert.IsNull(list!.Items.Filter, "全量视图＝根态同列表（Filter=null）。");
            CollectionAssert.AreEquivalent(
                new[] { "甲", "乙", "丙" },
                list.Items.OfType<BoardItem>().Select(item => item.Text).ToArray(),
                "该分类全部卡可见（含未分组丙——R1 缺口修复）。");
            Assert.AreEqual(3, viewModel.ActivePanel!.Items.Count, "源集合不动。");
            Assert.IsTrue(viewModel.SubgroupAllChip.IsFilterActive, "全 chip 激活态。");
            foreach (var chip in viewModel.SubgroupChips)
            {
                Assert.IsFalse(chip.IsFilterActive, "槽 chip 选中态全灭（互斥）。");
            }
            Assert.AreEqual(0, list.SelectedItems.Count, "进入全量视图清空选择（#37 口径）。");

            // 滑到槽 chip＝换组（全量态让位槽过滤，互斥）。
            HoverChip(window, 1);
            Assert.IsFalse(viewModel.SubgroupAllChip.IsFilterActive);
            CollectionAssert.AreEquivalent(
                new[] { "甲" },
                list.Items.OfType<BoardItem>().Select(item => item.Text).ToArray());

            // 滑回「全部」＝回到全量（互斥反向）。
            HoverAllChip(window);
            Assert.IsTrue(window.IsSubgroupFilterActive);
            Assert.IsNull(list.Items.Filter);
            Assert.AreEqual(3, list.Items.OfType<BoardItem>().Count());
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_AllChipFilterIsStickyUntilClearedViaHintBar()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("甲", BoardCategory.Inbox).Subgroup = 1;
        board.AddText("丙", BoardCategory.Inbox);
        var window = CreateSubgroupWindow(
            directory,
            board,
            SubgroupDisplayMode.ChipRail,
            subgroupAllChipEnabled: true);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var viewModel = (MainWindowViewModel)window.DataContext;

            HoverAllChip(window);
            Assert.IsTrue(window.IsSubgroupFilterActive);

            // 粘性：无 MouseLeave 行为，悬停移开后过滤保持（沿槽过滤同口径）。
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            CompleteLayout(window);
            Assert.IsTrue(window.IsSubgroupFilterActive, "全量过滤粘性（悬停离开不退出）。");
            Assert.IsTrue(viewModel.SubgroupAllChip.IsFilterActive);
            Assert.AreEqual(2, list!.Items.OfType<BoardItem>().Count());

            // 引导条「清空」＝既有退出路径（零新增退出方式）。
            ((Button)window.FindName("SearchClearFilterButton"))!
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            CompleteLayout(window);

            Assert.IsFalse(window.IsSubgroupFilterActive);
            Assert.IsFalse(viewModel.SubgroupAllChip.IsFilterActive, "退出清全 chip 激活态。");
            Assert.IsNull(list.Items.Filter);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_AllChipFilterIsStickyUntilClearedByEscape()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("甲", BoardCategory.Inbox).Subgroup = 1;
        var window = CreateSubgroupWindow(
            directory,
            board,
            SubgroupDisplayMode.ChipRail,
            subgroupAllChipEnabled: true);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            HoverAllChip(window);
            Assert.IsTrue(window.IsSubgroupFilterActive, "前置：全量态。");

            window.RaiseEvent(NewKeyEventArgs(window, Key.Escape));
            CompleteLayout(window);

            Assert.IsFalse(window.IsSubgroupFilterActive, "Esc＝取消全量过滤（既有路径）。");
            Assert.IsFalse(viewModel.SubgroupAllChip.IsFilterActive);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_AllChipFilterIsClearedWhenSwitchingCategory()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("甲", BoardCategory.Inbox).Subgroup = 1;
        board.AddText("乙", BoardCategory.CustomerOriginal);
        var window = CreateSubgroupWindow(
            directory,
            board,
            SubgroupDisplayMode.ChipRail,
            subgroupAllChipEnabled: true);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            HoverAllChip(window);
            Assert.IsTrue(window.IsSubgroupFilterActive, "前置：全量态。");

            ExpandCategory(window, BoardCategory.CustomerOriginal);
            CompleteLayout(window);

            Assert.IsFalse(window.IsSubgroupFilterActive, "切分类清全量过滤（既有路径）。");
            Assert.IsFalse(viewModel.SubgroupAllChip.IsFilterActive);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_AllChipFilterIsClearedWhenSwitchingMode()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("甲", BoardCategory.Inbox).Subgroup = 1;
        var window = CreateSubgroupWindow(
            directory,
            board,
            SubgroupDisplayMode.ChipRail,
            subgroupAllChipEnabled: true);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            HoverAllChip(window);
            Assert.IsTrue(window.IsSubgroupFilterActive, "前置：全量态。");

            window.ApplyPreferences(
                window.CurrentPreferences with { SubgroupDisplay = SubgroupDisplayMode.Off });
            CompleteLayout(window);

            Assert.IsFalse(window.IsSubgroupFilterActive, "模式切换清全量过滤（既有路径）。");
            Assert.IsFalse(viewModel.SubgroupAllChip.IsFilterActive);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_AllChipFilterYieldsToSearchAndRestoresAfterSearchExit()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("苹果一", BoardCategory.Inbox).Subgroup = 1;
        board.AddText("香蕉二", BoardCategory.Inbox).Subgroup = 1;
        var window = CreateSubgroupWindow(
            directory,
            board,
            SubgroupDisplayMode.ChipRail,
            subgroupAllChipEnabled: true);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var viewModel = (MainWindowViewModel)window.DataContext;

            HoverAllChip(window);
            Assert.IsTrue(window.IsSubgroupFilterActive);

            // 搜索激活＝搜索谓词压倒（#37），全量态冻结让位、激活态保留。
            window.EnterSearchMode();
            ((TextBox)window.FindName("SearchInput"))!.Text = "苹果";
            CompleteLayout(window);

            Assert.IsTrue(viewModel.IsSearchActive);
            Assert.IsTrue(viewModel.SubgroupAllChip.IsFilterActive, "让位期间全 chip 激活态保留。");
            CollectionAssert.AreEquivalent(
                new[] { "苹果一" },
                list!.Items.OfType<BoardItem>().Select(item => item.Text).ToArray(),
                "搜索谓词压倒全量视图。");

            window.ExitSearchMode();
            CompleteLayout(window);

            Assert.IsFalse(viewModel.IsSearchActive);
            Assert.IsTrue(window.IsSubgroupFilterActive, "退出搜索自动恢复全量态。");
            Assert.IsNull(list.Items.Filter, "恢复＝Filter=null（根态同列表）。");
            Assert.AreEqual(2, list.Items.OfType<BoardItem>().Count());
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_AllChipFilterKeepsDragAndCtrlABannedAndSuppressesCollapse()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("甲", BoardCategory.Inbox).Subgroup = 1;
        board.AddText("乙", BoardCategory.Inbox);
        var window = CreateSubgroupWindow(
            directory,
            board,
            SubgroupDisplayMode.ChipRail,
            subgroupAllChipEnabled: true);

        try
        {
            window.Show();
            window.ClientAreaAnimationsEnabled = false;
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var viewModel = (MainWindowViewModel)window.DataContext;

            HoverAllChip(window);
            Assert.IsTrue(window.IsSubgroupFilterActive, "前置：全量态谓词为真。");

            // Ctrl+A 沿 #37 口径禁用（谓词单点扩容自动继承）。
            ApplicationCommands.SelectAll.Execute(null, list!);
            CompleteLayout(window);
            Assert.AreEqual(0, list.SelectedItems.Count, "全量态 Ctrl+A 不得选择。");

            // 自动收起抑制：全量态 Root_MouseLeave 后 450ms 仍展开。
            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(450));
            CompleteLayout(window);
            Assert.IsTrue(viewModel.IsPanelExpanded, "全量态抑制自动收起。");

            // 退出全量态＝禁令解除（引导条清空）。
            ((Button)window.FindName("SearchClearFilterButton"))!
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            CompleteLayout(window);
            Assert.IsFalse(window.IsSubgroupFilterActive);
            ApplicationCommands.SelectAll.Execute(null, list);
            CompleteLayout(window);
            Assert.AreEqual(2, list.SelectedItems.Count, "退出后 Ctrl+A 恢复。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_AllChipFilterEmptyCategoryShowsBoardEmptyStateNotGroupHint()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        // 空分类＋全量态：内容与根态等价——走 BoardEmptyState（#33 修订口径），
        // 不让位「该分组暂无卡片」（那是槽过滤空结果的语义）。
        var window = CreateSubgroupWindow(
            directory,
            board,
            SubgroupDisplayMode.ChipRail,
            subgroupAllChipEnabled: true);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var hint = (Border)window.FindName("SearchEmptyHint");
            var boardEmpty = (StackPanel)window.FindName("BoardEmptyState");

            HoverAllChip(window);

            Assert.AreEqual(
                Visibility.Visible,
                boardEmpty!.Visibility,
                "全量态空分类走 BoardEmptyState（与根态内容等价）。");
            Assert.AreEqual(
                Visibility.Collapsed,
                hint!.Visibility,
                "不让位「该分组暂无卡片」（仅槽过滤让位）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_AllChipClickIsNoOpAndDoubleClickDoesNotRename()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("甲", BoardCategory.Inbox);
        var window = CreateSubgroupWindow(
            directory,
            board,
            SubgroupDisplayMode.ChipRail,
            subgroupAllChipEnabled: true);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var allHost = FindSubgroupAllChip(window);

            HoverAllChip(window);
            Assert.IsTrue(window.IsSubgroupFilterActive, "前置：全量态。");

            // 单击＝无操作（无落点语义，不发明交互）：过滤态不翻转。
            allHost.RaiseEvent(NewMouseButtonEventArgs(
                UIElement.MouseLeftButtonUpEvent,
                allHost));
            CompleteLayout(window);
            Assert.IsTrue(window.IsSubgroupFilterActive, "单击不退出全量态。");
            Assert.AreEqual(
                DefaultMarkerState.Inactive,
                viewModel.SubgroupChips[1].DefaultMarkerState,
                "单击不设默认落点（伪 chip 无槽身份）。");

            // 双击＝无改名会话（模板无编辑器，会话机器不触及）。
            var doubleClick = NewMouseButtonEventArgs(
                UIElement.MouseLeftButtonDownEvent,
                allHost);
            typeof(MouseButtonEventArgs)
                .GetField("_count", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(doubleClick, 2);
            allHost.RaiseEvent(doubleClick);
            CompleteLayout(window);

            Assert.AreEqual(0, FindDescendants<TextBox>(allHost).Count(), "伪 chip 无编辑器。");
            Assert.AreEqual(
                SubgroupCatalog.AllChipName,
                viewModel.SubgroupAllChip.DisplayName,
                "显示名恒定「全部」（无改名）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_AllChipExcludedFromSlotOrderAndDropAssignment()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var first = board.AddText("甲", BoardCategory.Inbox);
        var second = board.AddText("乙", BoardCategory.Inbox);
        var settings = WindowSettings.Default
            .WithSubgroupOrder(BoardCategory.Inbox, [3, 1, 0, 2]);
        var window = CreateSubgroupWindow(
            directory,
            board,
            SubgroupDisplayMode.ChipRail,
            settings,
            subgroupAllChipEnabled: true);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var list = (ListBox)window.FindName("BoardList");
            var allHost = FindSubgroupAllChip(window);
            var data = new DragPayloadService().BuildInternalBatch([first, second]);

            // 槽序只辖槽 chip：伪 chip 恒居首、槽序按分类清单生效。
            CollectionAssert.AreEqual(
                new[] { 3, 1, 0, 2 },
                viewModel.SubgroupChips.Select(chip => chip.Slot).ToArray(),
                "槽序按分类清单（校验零改）。");
            Assert.IsInstanceOfType(viewModel.SubgroupRailItems[0], typeof(SubgroupAllChipViewModel));
            CollectionAssert.AreEqual(
                new[] { 3, 1, 0, 2 },
                viewModel.SubgroupRailItems
                    .Skip(1)
                    .Cast<SubgroupChipViewModel>()
                    .Select(chip => chip.Slot)
                    .ToArray(),
                "投影槽段＝槽序（伪 chip 不参与）。");

            // 伪 chip 槽号不进入槽序校验：带 4 元素的非法序仍被拒（Count==SlotCount）。
            viewModel.ApplySubgroupOrder(BoardCategory.Inbox, [0, 1, 2, 3, 3]);
            CollectionAssert.AreEqual(
                new[] { 3, 1, 0, 2 },
                viewModel.SubgroupChips.Select(chip => chip.Slot).ToArray(),
                "非法序（数量≠SlotCount）拒绝重排。");

            // 拖卡到「全部」＝无归属（模板无拖放处理器，祖先无兜底 Drop；无变更即无落盘）。
            allHost.RaiseEvent(NewDragEventArgs(data, DragDrop.DragEnterEvent, allHost));
            allHost.RaiseEvent(NewDragEventArgs(data, DragDrop.DropEvent, allHost));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            CompleteLayout(window);

            Assert.IsNull(first.Subgroup, "拖卡到「全部」不得归属。");
            Assert.IsNull(second.Subgroup);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_AllChipToggleOffWhileActiveClearsFilter()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("甲", BoardCategory.Inbox).Subgroup = 1;
        var window = CreateSubgroupWindow(
            directory,
            board,
            SubgroupDisplayMode.ChipRail,
            subgroupAllChipEnabled: true);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var viewModel = (MainWindowViewModel)window.DataContext;

            HoverAllChip(window);
            Assert.IsTrue(window.IsSubgroupFilterActive, "前置：全量态。");
            Assert.IsNull(list!.Items.Filter);

            // 开→关（全量过滤激活时）＝先清过滤零残留（照模式切换先例）。
            window.ApplyPreferences(
                window.CurrentPreferences with { SubgroupAllChipEnabled = false });
            CompleteLayout(window);

            Assert.IsFalse(window.IsSubgroupFilterActive, "开关关闭清全量过滤。");
            Assert.IsFalse(viewModel.SubgroupAllChip.IsFilterActive);
            Assert.IsFalse(viewModel.SubgroupAllChipEnabled);
            Assert.AreEqual(4, viewModel.SubgroupRailItems.Count, "轨投影回落 4 槽。");
            Assert.AreEqual(4, viewModel.SubgroupRailColumnCount);
            Assert.AreEqual(4, FindSubgroupRailGrid(window).Columns, "布局回落四列。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_AllChipZeroPresenceInGroupedViewAndOff()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("甲", BoardCategory.Inbox).Subgroup = 1;
        var window = CreateSubgroupWindow(
            directory,
            board,
            SubgroupDisplayMode.Off,
            subgroupAllChipEnabled: true);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            // Off：轨整行塌缩（开关不影响 Off 等价性）。
            Assert.IsFalse(((MainWindowViewModel)window.DataContext).IsSubgroupRailVisible);
            Assert.AreEqual(Visibility.Collapsed, FindSubgroupRail(window).Visibility);
            Assert.IsFalse(window.IsSubgroupFilterActive, "Off 无过滤态（谓词含 ChipRail 判）。");

            window.ApplyPreferences(
                window.CurrentPreferences with { SubgroupDisplay = SubgroupDisplayMode.GroupedView });
            InvokePrivateTask(window, "DrainPendingOperationsAsync");
            CompleteLayout(window);

            // GroupedView：视觉分组本身已是聚合呈现，轨零呈现。
            Assert.AreEqual(Visibility.Collapsed, FindSubgroupRail(window).Visibility);
            Assert.IsFalse(window.IsSubgroupFilterActive, "GroupedView 无过滤态。");
            Assert.IsNull(((ListBox)window.FindName("BoardList"))!.Items.Filter);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Subgroup_AllChipEvidenceBothThemes()
    {
        // 契约 #57 修订证据：浅/深双主题五列布局（「全部」恒居首）＋全量激活帧
        //（未分组卡可见）。环境变量未设置时跳过（CI 无证据目录即 Inconclusive）。
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
        var window = CreateSubgroupWindow(
            directory,
            board,
            SubgroupDisplayMode.Off,
            subgroupAllChipEnabled: true);

        try
        {
            window.Show();
            window.Topmost = true;
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var shell = (Border)window.FindName("WindowShell");
            var viewModel = (MainWindowViewModel)window.DataContext;

            foreach (var theme in new[] { DesignTheme.Light, DesignTheme.Dark })
            {
                DesignThemeManager.Apply(window, theme);
                CompleteLayout(window);
                var themeName = theme == DesignTheme.Light ? "light" : "dark";

                // 五列布局帧（开关开启＋ChipRail）。
                window.ApplyPreferences(window.CurrentPreferences with
                {
                    SubgroupDisplay = SubgroupDisplayMode.ChipRail,
                    SubgroupAllChipEnabled = true
                });
                CompleteLayout(window);
                Assert.AreEqual(5, FindSubgroupRailGrid(window).Columns, "证据帧必须含五列布局。");
                SaveVisualEvidence(shell!, $"subgroup-all-five-columns-{themeName}.png", "FTS_SUBGROUP_EVIDENCE_DIR");

                // 全量激活帧（悬停进入，未分组卡可见）。
                HoverAllChip(window);
                Assert.IsTrue(window.IsSubgroupFilterActive, "证据帧必须含全量激活态。");
                Assert.AreEqual(3, ((ListBox)window.FindName("BoardList"))!.Items.OfType<BoardItem>().Count());
                SaveVisualEvidence(shell!, $"subgroup-all-active-{themeName}.png", "FTS_SUBGROUP_EVIDENCE_DIR");
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }
}
