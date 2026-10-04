using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 卡片左右对半分区（用户契约：左半双击编辑、右半单击选中/右键复制）与
/// 编辑会话锚点生命周期（1.18.0 重构）的契约测试。历史背景：1.17.0 用
/// 「内容列 + 最右 60px 操作列」表达分区，用户感知的右半边落在内容列，
/// 单击/右键无反应（Bug B）；编辑会话终结托付给被手势层自己禁用的焦点
/// 迁移，切类目后编辑框悬挂在页面上（Bug A）。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    /// <summary>命中层某分区根元素（模板 Tag 标记）。</summary>
    private static FrameworkElement HalvesZoneOf(ListBoxItem container, string zoneTag) =>
        ZoneSourceOf(container, zoneTag);

    /// <summary>卡片命中层的宿主 Grid（Margin=12 内层，两列 * /* 的父元素）。</summary>
    private static Grid HitLayerHostOf(ListBoxItem container) =>
        (Grid)HalvesZoneOf(container, CardGestureZones.ContentZone).Parent;

    [STATestMethod]
    public void CardHalves_HitLayerSplitsCardContentIntoEqualHalves()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("对半命中层", BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);

            // 用户契约是空间对半：命中层两列必须各占宿主的一半，与操作按钮
            // 位置无关（按钮只是右半内的子元素）。
            var contentZone = HalvesZoneOf(container, CardGestureZones.ContentZone);
            var operationsZone = HalvesZoneOf(container, CardGestureZones.OperationsZone);
            var host = HitLayerHostOf(container);
            Assert.AreSame(host, operationsZone.Parent, "左右命中面必须同宿主拼接。");

            Assert.AreEqual(
                host.ActualWidth / 2,
                contentZone.ActualWidth,
                0.5,
                "左半命中面必须是卡片内容区的一半。");
            Assert.AreEqual(
                host.ActualWidth / 2,
                operationsZone.ActualWidth,
                0.5,
                "右半命中面必须是卡片内容区的一半。");
            Assert.AreEqual(
                contentZone.TranslatePoint(new Point(), container).X +
                    contentZone.ActualWidth,
                operationsZone.TranslatePoint(new Point(), container).X,
                0.5,
                "左右命中面必须相邻拼接、共同覆盖卡片内容区。");

            // 操作按钮仍在右半的右上角（视觉位置不变），且是命中层子元素。
            var buttons = FindDescendants<System.Windows.Controls.Primitives.ButtonBase>(container)
                .Where(candidate => Equals(candidate.CommandParameter, "TogglePin") ||
                    Equals(candidate.CommandParameter, "ToggleSelection"))
                .ToArray();
            Assert.HasCount(2, buttons);
            var operationsBounds = new Rect(
                operationsZone.TranslatePoint(new Point(), container),
                new Size(operationsZone.ActualWidth, operationsZone.ActualHeight));
            foreach (var button in buttons)
            {
                var center = button.TranslatePoint(
                    new Point(button.ActualWidth / 2, button.ActualHeight / 2),
                    container);
                Assert.IsTrue(
                    operationsBounds.Contains(center),
                    "操作按钮必须落在右半命中面内。");
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardHalves_EditSessionFlushesWhenSwitchingCategory()
    {
        using var directory = new TestDirectory();
        var store = new RecordingCardEditStore(directory.Root);
        var board = new BoardService();
        var item = board.AddText("切类目前在编辑", BoardCategory.Inbox);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            EnterCardEditingWithDoubleClick(window, item);
            CompleteLayout(window);
            EditorInput(window).Text = "切类目时提交的编辑";
            Assert.AreEqual(
                Visibility.Visible,
                EditorHost(window).Visibility,
                "前置条件：编辑会话已打开。");

            // Bug A 复现：类目页签不夺键盘焦点，ActivatePanel 整体替换列表
            // 视图；旧实现的会话终结只认焦点迁移，编辑框悬挂在换页后的表面上。
            InvokePrivate(window, "ActivatePanel", BoardCategory.Reference);
            CompleteLayout(window);

            Assert.AreEqual(
                Visibility.Collapsed,
                EditorHost(window).Visibility,
                "切换类目必须终结编辑会话（编辑框不得悬挂）。");
            Assert.AreEqual(
                "切类目时提交的编辑",
                board.FindItem(item.Id)!.Text,
                "切类目冲刷必须提交已输入文字。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardHalves_RealInputRightHalfBareClickTogglesSelection()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("右半真实单击", BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            window.Topmost = true;
            CompleteLayout(window);

            // 右半中央（远离右上角按钮）：旧实现落在内容列，单击无反应（Bug B）。
            var operationsZone = HalvesZoneOf(container, CardGestureZones.OperationsZone);
            var zoneOrigin = operationsZone.TranslatePoint(new Point(), container);
            var clickPoint = new Point(
                zoneOrigin.X + operationsZone.ActualWidth / 2,
                zoneOrigin.Y + operationsZone.ActualHeight * 0.6);
            var savedCursor = SaveCursorPosition();

            try
            {
                ClickRealAt(window, container, clickPoint);

                var list = (ListBox)window.FindName("BoardList");
                CollectionAssert.AreEquivalent(
                    new[] { item },
                    list.SelectedItems.Cast<BoardItem>().ToArray(),
                    "卡片右半（远离按钮）的真实输入单击必须 toggle 选中。");

                // 再击取消。两击间拉开双击时限（同区快速两击是「净一次
                // toggle」的既有双击语义，独立两击才各自 toggle）；期间光标
                // 停留在点击点——中途移回原位会让 WPF 的光标位置同步滞后，
                // 第二击被 Spurious mouse event 防护丢弃。
                PumpDispatcherFor(
                    window.Dispatcher,
                    TimeSpan.FromMilliseconds(GetDoubleClickTime() + 100));
                ClickRealAt(window, container, clickPoint);

                CollectionAssert.AreEquivalent(
                    Array.Empty<BoardItem>(),
                    list.SelectedItems.Cast<BoardItem>().ToArray(),
                    "卡片右半再次单击必须取消选中。");
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardHalves_RealInputLeftHalfBareClickHasNoSelectionSemantics()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("左半真实单击", BoardCategory.Inbox);
        var other = board.AddText("已选的另一张", BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            window.Topmost = true;
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(other);

            // 左半中央（约 25% 卡宽处）：单击不得产生任何选择语义——
            // 左半只承载双击编辑与拖拽，既有已选条目保持不变。
            var contentZone = HalvesZoneOf(container, CardGestureZones.ContentZone);
            var zoneOrigin = contentZone.TranslatePoint(new Point(), container);
            var clickPoint = new Point(
                zoneOrigin.X + contentZone.ActualWidth / 2,
                zoneOrigin.Y + contentZone.ActualHeight * 0.6);
            var savedCursor = SaveCursorPosition();

            try
            {
                ClickRealAt(window, container, clickPoint);
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }

            CollectionAssert.AreEquivalent(
                new[] { other },
                list.SelectedItems.Cast<BoardItem>().ToArray(),
                "卡片左半的真实输入单击不得产生选择语义（既有已选保留）。");
            Assert.AreEqual(
                Visibility.Collapsed,
                EditorHost(window).Visibility,
                "左半单击不得进入编辑。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardHalves_EditSessionFlushesWhenSearchFiltersOutAnchor()
    {
        using var directory = new TestDirectory();
        var store = new RecordingCardEditStore(directory.Root);
        var board = new BoardService();
        var item = board.AddText("搜索过滤时的锚点", BoardCategory.Inbox);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            EnterCardEditingWithDoubleClick(window, item);
            EditorInput(window).Text = "过滤冲刷的编辑";

            // 关键词不匹配锚点：过滤把锚点移出视图（源集合不变），
            // 会话必须终结并提交，而不是悬挂在过滤后的列表上。
            window.EnterSearchMode();
            CompleteLayout(window);
            SearchInput(window).Text = "不存在的内容";
            CompleteLayout(window);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(50));

            Assert.AreEqual(Visibility.Collapsed, EditorHost(window).Visibility);
            Assert.AreEqual(
                "过滤冲刷的编辑",
                board.FindItem(item.Id)!.Text,
                "锚点被过滤移出视图必须提交已输入文字。");
            window.ExitSearchMode();
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardHalves_EditSessionCommitsWhenAnchorItemRemoved()
    {
        using var directory = new TestDirectory();
        var store = new RecordingCardEditStore(directory.Root);
        var board = new BoardService();
        var item = board.AddText("被移除的锚点", BoardCategory.Inbox);
        board.AddText("保留下来的卡", BoardCategory.Inbox);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            EnterCardEditingWithDoubleClick(window, item);
            EditorInput(window).Text = "锚点移除前输入的编辑";

            Assert.IsTrue(
                board.RemoveMany([item.Id]) is not null,
                "前置条件：锚点条目被移除。");
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(50));

            Assert.AreEqual(Visibility.Collapsed, EditorHost(window).Visibility);
            Assert.IsNull(
                board.FindItem(item.Id),
                "锚点条目已被移除，编辑内容无处落地是预期行为。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardHalves_ScrollRepositionsEditorAndFlushesWhenAnchorVirtualized()
    {
        using var directory = new TestDirectory();
        var store = new RecordingCardEditStore(directory.Root);
        var board = new BoardService();
        AddScrollableItems(board, BoardCategory.Inbox);
        var anchor = board.Items(BoardCategory.Inbox).ToArray()[1];
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewer = FindDescendant<ScrollViewer>((ListBox)window.FindName("BoardList"));
            Assert.IsNotNull(viewer);
            Assert.IsTrue(viewer.ScrollableHeight > 0, "前置条件：列表可滚动。");

            EnterCardEditingWithDoubleClick(window, anchor);
            EditorInput(window).Text = "滚动跟随的编辑";
            var marginBefore = EditorHost(window).Margin;

            // 锚点仍在视口：覆盖层重定位跟随（滚动即提交是 UX 回归）。
            ScrollTo(window, viewer, Math.Min(60d, viewer.ScrollableHeight));
            var marginAfterScroll = EditorHost(window).Margin;
            Assert.AreEqual(
                Visibility.Visible,
                EditorHost(window).Visibility,
                "锚点仍在视口时滚动不得终结会话。");
            Assert.AreNotEqual(
                marginBefore,
                marginAfterScroll,
                "覆盖层必须随滚动重定位到锚点容器的新位置。");

            // 锚点滚出虚拟化窗口：容器被回收 → 会话终结并提交。
            ScrollTo(window, viewer, viewer.ScrollableHeight);
            Assert.AreEqual(
                Visibility.Collapsed,
                EditorHost(window).Visibility,
                "锚点滚出视口（容器回收）必须终结会话。");
            Assert.AreEqual(
                "滚动跟随的编辑",
                board.FindItem(anchor.Id)!.Text,
                "锚点滚出终结会话时必须提交已输入文字。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardHalves_DoubleClickAnotherCardContinuesEditSession()
    {
        using var directory = new TestDirectory();
        var store = new RecordingCardEditStore(directory.Root);
        var board = new BoardService();
        var first = board.AddText("第一张的原始内容", BoardCategory.Inbox);
        var second = board.AddText("第二张的原始内容", BoardCategory.Inbox);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            EnterCardEditingWithDoubleClick(window, first);
            EditorInput(window).Text = "第一张的接续提交";

            // 接续：旧守卫 _editingCardItemId is not null 会静默拒绝（编辑
            // 功能卡死）。现在必须先提交上一会话、再在第二张上开新会话。
            EnterCardEditingWithDoubleClick(window, second);
            CompleteLayout(window);

            Assert.AreEqual(Visibility.Visible, EditorHost(window).Visibility);
            Assert.AreEqual(
                "第二张的原始内容",
                EditorInput(window).Text,
                "接续编辑的种子必须是新卡文本。");
            Assert.IsTrue(EditorInput(window).IsKeyboardFocused);
            Assert.AreEqual(
                "第一张的接续提交",
                board.FindItem(first.Id)!.Text,
                "接续开新会话前必须同步提交上一会话。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardHalves_BareClickOnAnotherCardFlushesThenDispatches()
    {
        using var directory = new TestDirectory();
        var store = new RecordingCardEditStore(directory.Root);
        var board = new BoardService();
        var edited = board.AddText("正在编辑的卡", BoardCategory.Inbox);
        var other = board.AddText("被单击的另一张", BoardCategory.Inbox);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            EnterCardEditingWithDoubleClick(window, edited);
            EditorInput(window).Text = "单击他卡冲刷的编辑";

            // 编辑中单击另一张卡的右半：先冲刷悬挂会话，再分发本次手势。
            ClickCard(window, other, ModifierKeys.None);
            CompleteLayout(window);

            Assert.AreEqual(
                "单击他卡冲刷的编辑",
                board.FindItem(edited.Id)!.Text,
                "单击他卡必须先提交悬挂编辑。");
            CollectionAssert.AreEquivalent(
                new[] { other },
                list.SelectedItems.Cast<BoardItem>().ToArray(),
                "冲刷后本次单击手势照常分发。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardHalves_LongPressReleaseThenQuickClickIsNotDoubleClick()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("长按伪双击", BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var hitSource = ZoneHitSource(window, item, CardGestureZones.ContentZone);

            // 系统双击语义按 Down 判定：第一击长按 2 秒后抬键，紧接一次快速
            // 单击——Up 时刻相距仅 55ms，旧实现判为双击（左半无故弹编辑器）。
            var holdStart = NextIsolatedClickTimestamp();
            hitSource.RaiseEvent(NewMouseButtonEventArgs(
                Mouse.PreviewMouseDownEvent, hitSource, holdStart));
            hitSource.RaiseEvent(NewMouseButtonEventArgs(
                Mouse.PreviewMouseUpEvent, hitSource, holdStart + 2000));
            var quickClick = holdStart + 2050;
            hitSource.RaiseEvent(NewMouseButtonEventArgs(
                Mouse.PreviewMouseDownEvent, hitSource, quickClick));
            hitSource.RaiseEvent(NewMouseButtonEventArgs(
                Mouse.PreviewMouseUpEvent, hitSource, quickClick + 5));
            CompleteLayout(window);

            Assert.AreEqual(
                Visibility.Collapsed,
                EditorHost(window).Visibility,
                "长按抬键+快速单击必须判为两次独立单击，不得进入编辑。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardHalves_CrossZoneClicksDispatchIndependently()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("跨区两击", BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var contentSource = ZoneHitSource(window, item, CardGestureZones.ContentZone);
            var operationsSource = ZoneHitSource(window, item, CardGestureZones.OperationsZone);

            // 右半单击 toggle，双击时限内的左半第二击是独立单击（无语义）：
            // 跨半两击不得串味成双击（旧实现左半第二击被吞、右→左会意外进编辑）。
            RaiseCardPressAndRelease(operationsSource, NextIsolatedClickTimestamp());
            RaiseCardPressAndRelease(contentSource, NextPairedClickTimestamp());
            CollectionAssert.AreEquivalent(
                new[] { item },
                list.SelectedItems.Cast<BoardItem>().ToArray(),
                "右半第一击的 toggle 必须保留。");
            Assert.AreEqual(
                Visibility.Collapsed,
                EditorHost(window).Visibility,
                "跨半两击不得判为双击进入编辑。");

            // 反向：左半单击（痕迹）+ 双击时限内右半第二击 → 独立 toggle。
            RaiseCardPressAndRelease(contentSource, NextIsolatedClickTimestamp());
            RaiseCardPressAndRelease(operationsSource, NextPairedClickTimestamp());
            CollectionAssert.AreEquivalent(
                Array.Empty<BoardItem>(),
                list.SelectedItems.Cast<BoardItem>().ToArray(),
                "时限内的右半第二击必须独立分发（取消选择）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardHalves_PinButtonRepeatedClicksToggleEachTime()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("连点置顶", BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            var pinButton = FindDescendants<System.Windows.Controls.Primitives.ButtonBase>(container)
                .Single(button => Equals(button.CommandParameter, "TogglePin"));

            // 置顶连点：每击都必须 toggle（旧实现第二击落入双击窗被吞，
            // 置顶→立刻取消置顶失效，按钮像坏的）。
            RaiseCardClick(pinButton);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            Assert.IsTrue(item.IsPinned, "第一次单击必须置顶。");

            RaiseCardClick(pinButton);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            Assert.IsFalse(item.IsPinned, "第二次单击必须取消置顶（不被双击窗吞掉）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardHalves_ButtonPressReleasingOutsideCancelsIntent()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("滑离取消", BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            var list = (ListBox)window.FindName("BoardList");
            var pinButton = FindDescendants<System.Windows.Controls.Primitives.ButtonBase>(container)
                .Single(button => Equals(button.CommandParameter, "TogglePin"));
            var cardSource = ZoneHitSource(window, item, CardGestureZones.ContentZone);

            // 按住按钮滑离卡片后抬键：按钮常规语义是取消意图（旧实现滑离
            // 仍触发）。
            pinButton.RaiseEvent(NewMouseButtonEventArgs(
                Mouse.PreviewMouseDownEvent,
                pinButton,
                NextIsolatedClickTimestamp()));
            cardSource.RaiseEvent(NewMouseButtonEventArgs(
                Mouse.PreviewMouseUpEvent,
                cardSource,
                NextPairedClickTimestamp()));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));

            Assert.IsFalse(item.IsPinned, "滑离释放不得触发置顶。");
            Assert.IsFalse(
                list.SelectedItems.Cast<BoardItem>().Any(),
                "滑离释放不得触发选择。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardHalves_ButtonPressMarksIndependentDragExcludedSession()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("按钮不起拖", BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            var pinButton = FindDescendants<System.Windows.Controls.Primitives.ButtonBase>(container)
                .Single(button => Equals(button.CommandParameter, "TogglePin"));

            pinButton.RaiseEvent(NewMouseButtonEventArgs(
                Mouse.PreviewMouseDownEvent,
                pinButton,
                NextIsolatedClickTimestamp()));

            // 按钮按压是独立会话：拖拽排除（BoardList_PreviewMouseMove 的
            // 早退条件）以命中按钮实例为唯一判定源——按住按钮移动不得
            // 拖出整张卡。
            var pressButton = GetPrivateField<System.Windows.Controls.Button?>(window, "_pressButton");
            Assert.AreSame(pinButton, pressButton, "按钮按压必须记录命中按钮（拖拽排除的判定源）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    private static TextBox SearchInput(MainWindow window) =>
        (TextBox)window.FindName("SearchInput");

    [STATestMethod]
    public void CardHalves_ScrollBarPressDoesNotFlushEditSession()
    {
        using var directory = new TestDirectory();
        var store = new RecordingCardEditStore(directory.Root);
        var board = new BoardService();
        AddScrollableItems(board, BoardCategory.Inbox);
        var anchor = board.Items(BoardCategory.Inbox).ToArray()[1];
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewer = FindDescendant<ScrollViewer>((ListBox)window.FindName("BoardList"));
            Assert.IsNotNull(viewer);
            Assert.IsTrue(viewer.ScrollableHeight > 0, "前置条件：列表可滚动。");

            EnterCardEditingWithDoubleClick(window, anchor);
            EditorInput(window).Text = "拖滚动条期间的编辑";

            // 拖动滚动条与滚轮同属滚动语义：会话跟随重定位，不因滚动条
            // 按下（焦点/手势层按下）而终结。
            var thumb = FindDescendant<System.Windows.Controls.Primitives.Thumb>(viewer);
            Assert.IsNotNull(thumb, "前置条件：滚动条 Thumb 已实现。");
            thumb.RaiseEvent(NewMouseButtonEventArgs(
                Mouse.PreviewMouseDownEvent,
                thumb,
                NextIsolatedClickTimestamp()));
            CompleteLayout(window);

            Assert.AreEqual(
                Visibility.Visible,
                EditorHost(window).Visibility,
                "滚动条按下不得终结编辑会话（滚动只跟随）。");
            Assert.AreEqual(
                "拖滚动条期间的编辑",
                EditorInput(window).Text,
                "会话保留时编辑内容不丢失。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardHalves_SearchFilterFlushesWithoutFocusMigration()
    {
        using var directory = new TestDirectory();
        var store = new RecordingCardEditStore(directory.Root);
        var board = new BoardService();
        var item = board.AddText("无焦点迁移的过滤锚点", BoardCategory.Inbox);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            EnterCardEditingWithDoubleClick(window, item);
            EditorInput(window).Text = "过滤钩子独立提交的编辑";
            var viewModel = (MainWindowViewModel)window.DataContext;

            // 绕过 EnterSearchMode 的清选择/聚焦搜索框（焦点路径也会终结
            // 会话），直接驱动过滤单点：证明 ApplySearchFilter 的锚点判定
            // 独立于焦点迁移。
            viewModel.EnterSearch();
            viewModel.SearchText = "不匹配的关键词";
            InvokePrivate(window, "ApplySearchFilter");
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(50));

            Assert.AreEqual(Visibility.Collapsed, EditorHost(window).Visibility);
            Assert.AreEqual(
                "过滤钩子独立提交的编辑",
                board.FindItem(item.Id)!.Text,
                "过滤钩子必须独立于焦点迁移终结并提交会话。");
            viewModel.ExitSearch();
            InvokePrivate(window, "ApplySearchFilter");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardHalves_FocusMigrationWithinWindowKeepsEditSession()
    {
        using var directory = new TestDirectory();
        var store = new RecordingCardEditStore(directory.Root);
        var board = new BoardService();
        var item = board.AddText("焦点在窗口内迁移", BoardCategory.Inbox);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            EnterCardEditingWithDoubleClick(window, item);
            EditorInput(window).Text = "焦点迁移期间的编辑";

            // 焦点移到窗口内其他控件（如列表本身）：锚点仍有效，会话不因
            // 窗口内焦点迁移而终结（IME 交互等瞬时夺焦同理）；终结只由
            // 锚点失效或显式 Enter/Esc 驱动。
            var list = (ListBox)window.FindName("BoardList");
            Assert.IsTrue(list.Focus(), "前置条件：列表可聚焦。");
            CompleteLayout(window);

            Assert.AreEqual(
                Visibility.Visible,
                EditorHost(window).Visibility,
                "窗口内焦点迁移不得终结编辑会话。");
            Assert.AreEqual(
                "焦点迁移期间的编辑",
                EditorInput(window).Text,
                "会话保留时编辑内容不丢失。");
            Assert.AreNotEqual(
                "焦点迁移期间的编辑",
                board.FindItem(item.Id)!.Text,
                "会话保留时不得提交。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardHalves_RealInputRightHalfRightClickCopiesToClipboard()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("右半右键复制", BoardCategory.Inbox);
        var window = CreateWindow(directory, board);
        DataObject? delivered = null;
        window.ClipboardWriterOverride = data =>
        {
            delivered = data;
            return true;
        };

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            window.Topmost = true;
            CompleteLayout(window);
            var operationsZone = HalvesZoneOf(container, CardGestureZones.OperationsZone);
            var zoneOrigin = operationsZone.TranslatePoint(new Point(), container);
            var clickPoint = new Point(
                zoneOrigin.X + operationsZone.ActualWidth / 2,
                zoneOrigin.Y + operationsZone.ActualHeight * 0.6);
            var savedCursor = SaveCursorPosition();

            try
            {
                ClickRealRightAt(window, container, clickPoint);
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }

            Assert.IsNotNull(delivered, "卡片右半的真实输入右键必须复制该卡。");
            Assert.IsTrue(
                delivered!.GetDataPresent(DataFormats.UnicodeText),
                "复制的负载必须携带文字内容。");
            var list = (ListBox)window.FindName("BoardList");
            Assert.IsFalse(
                list.SelectedItems.Cast<BoardItem>().Any(),
                "右键复制不得改变选择。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardHalves_RealInputLeftHalfDoubleClickOnImageCardDoesNothing()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var imagePath = Path.Combine(directory.Root, "halves-image.png");
        WritePng(imagePath, width: 24, height: 12);
        var image = board.AddImage(
            Guid.NewGuid(), "images/halves-image.png", imagePath, BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, image);
            window.Topmost = true;
            CompleteLayout(window);
            var contentZone = HalvesZoneOf(container, CardGestureZones.ContentZone);
            var zoneOrigin = contentZone.TranslatePoint(new Point(), container);
            var clickPoint = new Point(
                zoneOrigin.X + contentZone.ActualWidth / 2,
                zoneOrigin.Y + contentZone.ActualHeight / 2);
            var savedCursor = SaveCursorPosition();

            try
            {
                // 预热一击稳定几何后拉开双击窗，正式两击构成双击。
                ClickRealAt(window, container, clickPoint);
                PumpDispatcherFor(
                    window.Dispatcher,
                    TimeSpan.FromMilliseconds(GetDoubleClickTime() + 100));
                ClickRealAt(window, container, clickPoint);
                ClickRealAt(window, container, clickPoint);
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }

            Assert.AreEqual(
                Visibility.Collapsed,
                EditorHost(window).Visibility,
                "图片卡左半双击无操作（图片编辑是后续切片）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    private const int WmRightButtonDown = 0x0204;
    private const int WmRightButtonUp = 0x0205;

    /// <summary>
    /// 真实输入管线右键点击：与 ClickRealAt 同构，投递 Win32 右键消息
    /// （走完整 WPF 输入管线），等待右键预览抬起路由完成。
    /// </summary>
    private static void ClickRealRightAt(
        MainWindow window,
        FrameworkElement container,
        Point positionInContainer)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var screen = container.PointToScreen(positionInContainer);
        var client = new NativePoint((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
        Assert.IsTrue(ScreenToClient(hwnd, ref client), "屏幕点到客户区换算必须成功。");
        Assert.IsTrue(SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y)), "真实光标必须可移入窗口。");
        PumpUntilMouseOver(window);
        MouseButtonEventArgs? upArgs = null;
        var upSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MouseButtonEventHandler onUp = (_, e) =>
        {
            upArgs = e;
            upSeen.TrySetResult();
        };
        window.AddHandler(UIElement.PreviewMouseRightButtonUpEvent, onUp, true);
        try
        {
            var lParam = MakeLParam(client.X, client.Y);
            Assert.IsTrue(PostMessage(hwnd, WmRightButtonDown, 0x0002, lParam), "投递右键按下消息失败。");
            Assert.IsTrue(PostMessage(hwnd, WmRightButtonUp, 0, lParam), "投递右键抬起消息失败。");
            PumpDispatcherUntil(window.Dispatcher, upSeen.Task);
        }
        finally
        {
            window.RemoveHandler(UIElement.PreviewMouseRightButtonUpEvent, onUp);
        }

        Assert.IsNotNull(upArgs);
    }
}
