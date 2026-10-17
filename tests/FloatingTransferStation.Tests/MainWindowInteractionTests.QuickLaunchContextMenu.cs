using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 快捷启动瓦片右键上下文菜单（#30 修订，Issue #176）：查看态/编辑态右键出
/// 「删除/上移/下移/重命名」；四项全部复用既有事务与会话（删除=× 同一撤销事务、
/// 上移/下移=MoveAsync（WCAG 2.5.7 单指针替代）、重命名=编辑模式＋改名会话）。
/// 右键开菜单走 SendInput 硬件级注入（契约 #13；启动表面 PostMessage 直投按钮区
/// 不可达，同头部按钮区现象）；菜单项激活走 UIA Invoke（与卡片区「真实输入与
/// UIA 共用守卫」同口径）；Esc 经 SendInput 真实键盘。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    private const uint MouseEventfRightdown = 0x0008;
    private const uint MouseEventfRightup = 0x0010;
    private const uint VirtualKeyEscape = 0x1B;

    /// <summary>在光标当前位置注入真实右键按下/抬起。</summary>
    private static void RightClickBySendInput(MainWindow window)
    {
        var inputs = new INPUT[2];
        inputs[0].Type = InputMouse;
        inputs[0].Mi.DwFlags = MouseEventfRightdown;
        inputs[1].Type = InputMouse;
        inputs[1].Mi.DwFlags = MouseEventfRightup;
        Assert.AreEqual(2u, SendInput(2, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>()), "真实右键注入必须成功。");
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
    }

    /// <summary>注入真实 Esc 按键（菜单是独立 HWND，键盘先归菜单——只关菜单）。</summary>
    private static void PressEscapeBySendInput(MainWindow window)
    {
        var inputs = new INPUT[2];
        inputs[0].Type = InputKeyboard;
        inputs[0].Mi.Dx = (int)VirtualKeyEscape;
        inputs[1].Type = InputKeyboard;
        inputs[1].Mi.Dx = (int)VirtualKeyEscape;
        inputs[1].Mi.DwFlags = KeyEventfKeyup;
        Assert.AreEqual(2u, SendInput(2, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>()), "真实 Esc 注入必须成功。");
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
    }

    private static void InvokeMenuItem(MenuItem item)
    {
        var peer = new System.Windows.Automation.Peers.MenuItemAutomationPeer(item);
        var invoke = (System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(
            System.Windows.Automation.Peers.PatternInterface.Invoke);
        Assert.IsNotNull(invoke, "无子菜单项必须支持 UIA Invoke。");
        invoke.Invoke();
    }

    private static ContextMenu TileContextMenuOf(MainWindow window, LauncherItem item)
    {
        var tile = FindLauncherTile(window, item);
        Assert.IsNotNull(tile);
        var menu = tile.ContextMenu;
        Assert.IsNotNull(menu, "瓦片必须挂右键菜单。");
        return menu;
    }

    private static MenuItem MenuItemOf(ContextMenu menu, string name)
    {
        var item = menu.Items.OfType<MenuItem>().Single(entry => entry.Name == name);
        return item;
    }

    /// <summary>真实右键打开瓦片菜单（光标先悬停到瓦片中心）。</summary>
    private static ContextMenu OpenTileContextMenuByRealRightClick(
        MainWindow window,
        Border tile)
    {
        var centerInTile = new Point(tile.ActualWidth / 2, tile.ActualHeight / 2);
        HoverRealAt(window, tile, centerInTile, settleMilliseconds: 140);
        RightClickBySendInput(window);
        var menu = tile.ContextMenu!;
        Assert.IsTrue(menu.IsOpen, "真实右键必须打开瓦片上下文菜单。");
        return menu;
    }

    /// <summary>关闭菜单并等 Popup 的异步 Closed/焦点归还安定（连开两次菜单的
    /// 批跑场景里，紧接的第二次右键可能落在未完成收尾的 Popup 上）。</summary>
    private static void CloseTileContextMenuAndSettle(MainWindow window)
    {
        PressEscapeBySendInput(window);
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(220));
        PumpUntilOverlayAccountingCleared(window);
    }

    [STATestMethod]
    public void QuickLaunch_ContextMenu_RealRightClickOpensMenuWithoutLaunching()
    {
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var items = Enumerable.Range(0, 3)
            .Select(index => new LauncherItem(
                Guid.NewGuid(),
                LauncherKind.Application,
                $"{app.Path}.{index}",
                $"应用{index}",
                DateTimeOffset.UtcNow))
            .ToArray();
        var window = CreateLauncherWindowSync(directory, board, store, starter, items);
        var savedCursor = SaveCursorPosition();

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();
            var viewModel = (MainWindowViewModel)window.DataContext;
            window.Topmost = true;
            CompleteLayout(window);

            // 查看态真实右键：菜单打开，应用不启动（启动判定要求左键 PointerUp）。
            var tile = FindLauncherTile(window, catalog.Items[1]);
            Assert.IsNotNull(tile);
            var menu = OpenTileContextMenuByRealRightClick(window, tile);
            Assert.AreEqual(0, starter.Started.Count, "右键绝不启动应用。");
            Assert.IsTrue(menu.IsOpen, "菜单打开。");
            Assert.IsTrue(
                ((MainWindowViewModel)window.DataContext).IsPanelExpanded,
                "前置：菜单打开时面板保持展开。");

            // 端点禁用：中间瓦片上移/下移都可用。
            Assert.IsTrue(MenuItemOf(menu, "TileMenuMoveUp").IsEnabled, "中间瓦片上移可用。");
            Assert.IsTrue(MenuItemOf(menu, "TileMenuMoveDown").IsEnabled, "中间瓦片下移可用。");

            // 指针离开窗口：菜单不收起（ContextMenu 语义），面板经 Menu hold 不收起。
            SetCursorPos(
                (int)(SystemParameters.WorkArea.Left + 8),
                (int)(SystemParameters.WorkArea.Bottom - 8));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(600));
            Assert.IsTrue(menu.IsOpen, "指针离开不关菜单。");
            Assert.IsTrue(viewModel.IsPanelExpanded, "菜单打开期间面板不收起（覆盖交互 hold）。");

            // 真实 Esc 只关菜单：面板随后按指针几何恢复收起节奏（指针在窗外）。
            CloseTileContextMenuAndSettle(window);
            Assert.IsFalse(menu.IsOpen, "Esc 关闭菜单。");
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(700));
            Assert.IsFalse(viewModel.IsPanelExpanded, "菜单关闭＋指针在外：面板恢复收起。");
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_ContextMenu_EndpointsDisableOnFirstAndLastTiles()
    {
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var items = Enumerable.Range(0, 3)
            .Select(index => new LauncherItem(
                Guid.NewGuid(),
                LauncherKind.Application,
                $"{app.Path}.{index}",
                $"应用{index}",
                DateTimeOffset.UtcNow))
            .ToArray();
        var window = CreateLauncherWindowSync(directory, board, store, starter, items);
        var savedCursor = SaveCursorPosition();

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();
            window.Topmost = true;
            CompleteLayout(window);

            var first = FindLauncherTile(window, catalog.Items[0]);
            Assert.IsNotNull(first);
            var menu = OpenTileContextMenuByRealRightClick(window, first);
            Assert.IsFalse(MenuItemOf(menu, "TileMenuMoveUp").IsEnabled, "首瓦片上移端点禁用。");
            Assert.IsTrue(MenuItemOf(menu, "TileMenuMoveDown").IsEnabled, "首瓦片下移可用。");
            CloseTileContextMenuAndSettle(window);

            var last = FindLauncherTile(window, catalog.Items[^1]);
            Assert.IsNotNull(last);
            menu = OpenTileContextMenuByRealRightClick(window, last);
            Assert.IsTrue(MenuItemOf(menu, "TileMenuMoveUp").IsEnabled, "末瓦片上移可用。");
            Assert.IsFalse(MenuItemOf(menu, "TileMenuMoveDown").IsEnabled, "末瓦片下移端点禁用。");
            CloseTileContextMenuAndSettle(window);
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_ContextMenu_DeleteMoveAndRenameAllReuseExistingSessions()
    {
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var items = Enumerable.Range(0, 3)
            .Select(index => new LauncherItem(
                Guid.NewGuid(),
                LauncherKind.Application,
                $"{app.Path}.{index}",
                $"应用{index}",
                DateTimeOffset.UtcNow))
            .ToArray();
        var window = CreateLauncherWindowSync(directory, board, store, starter, items);
        var savedCursor = SaveCursorPosition();

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();
            window.Topmost = true;
            CompleteLayout(window);

            // 上移：查看态菜单把中间瓦片移到首位并持久化（WCAG 2.5.7）。
            var middleTile = FindLauncherTile(window, catalog.Items[1]);
            Assert.IsNotNull(middleTile);
            var menu = OpenTileContextMenuByRealRightClick(window, middleTile);
            InvokeMenuItem(MenuItemOf(menu, "TileMenuMoveUp"));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            CompleteLayout(window);
            Assert.AreEqual(0, catalog.Items.IndexOf(items[1]), "菜单上移把中间瓦片移到首位。");
            var persisted = store.LoadAsync().GetAwaiter().GetResult();
            Assert.AreEqual(
                0,
                persisted.Items.Select(entry => entry.Id).ToList().IndexOf(items[1].Id),
                "菜单上移持久化。");

            // 重命名：查看态进入编辑模式＋改名会话（预填全选、Enter 提交）。
            var firstTile = FindLauncherTile(window, catalog.Items[0]);
            Assert.IsNotNull(firstTile);
            menu = OpenTileContextMenuByRealRightClick(window, firstTile);
            InvokeMenuItem(MenuItemOf(menu, "TileMenuRename"));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
            CompleteLayout(window);
            Assert.IsTrue(GetPrivateField<bool>(window, "_launcherEditMode"), "菜单重命名进入编辑模式。");
            var renaming = catalog.Items[0];
            Assert.IsTrue(renaming.IsEditingName, "菜单重命名进入改名会话。");
            var editor = FindDescendant<TextBox>(firstTile);
            Assert.IsNotNull(editor);
            Assert.IsTrue(editor.IsKeyboardFocusWithin, "改名编辑器持有焦点。");
            Assert.AreEqual(renaming.DisplayName, editor.Text, "改名编辑器预填现名。");
            renaming.DraftName = "菜单改名";
            editor.RaiseEvent(NewKeyEventArgs(window, Key.Enter));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            Assert.AreEqual("菜单改名", catalog.Items[0].DisplayName, "菜单改名提交生效。");
            Assert.IsTrue(GetPrivateField<bool>(window, "_launcherEditMode"), "改名提交后保持编辑模式（改名是编辑态语义，提交不退出）。");

            // 删除：与 × 同一撤销事务（提示条＋原下标重插）。
            window.RaiseEvent(NewKeyEventArgs(window, Key.Escape));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
            CompleteLayout(window);
            var targetTile = FindLauncherTile(window, catalog.Items[1]);
            Assert.IsNotNull(targetTile);
            menu = OpenTileContextMenuByRealRightClick(window, targetTile);
            var targetBefore = catalog.Items[1];
            InvokeMenuItem(MenuItemOf(menu, "TileMenuDelete"));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            CompleteLayout(window);
            Assert.AreEqual(2, catalog.Items.Count, "菜单删除生效。");
            Assert.IsFalse(catalog.Items.Contains(targetBefore), "被删条目离开集合。");
            Assert.IsTrue(IsLauncherToastVisible(window), "菜单删除出撤销提示条（同一事务）。");
            var undo = FindLauncherUndoButton(window);
            Assert.IsNotNull(undo);
            undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, undo));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            Assert.AreEqual(3, catalog.Items.Count, "撤销恢复。");
            Assert.AreEqual(1, catalog.Items.IndexOf(targetBefore), "按原下标重插。");
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindowWithoutSaving(window);
        }
    }
}
