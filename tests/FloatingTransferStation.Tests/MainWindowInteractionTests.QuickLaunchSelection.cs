using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 启动瓦片多选与批量删除（#30 修订 2026-10-18，Issue #186）：D5 语义族——
/// Ctrl/Shift/Ctrl+A 建 选、选择非空单击=toggle 且启动挂起（末项取消不启动）、
/// 勾选钮 toggle、头部垃圾桶=「删除已选 N 项」＋角标、整批撤销按原下标逆序重插、
/// Esc/点空白清空、选择期挂起改名与拖拽。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public void QuickLaunch_Selection_ButtonAndCtrlClickToggleWithAnchor()
    {
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var items = Enumerable.Range(0, 4)
            .Select(index => new LauncherItem(
                Guid.NewGuid(),
                LauncherKind.Application,
                $"{app.Path}.{index}",
                $"应用{index}",
                DateTimeOffset.UtcNow))
            .ToArray();
        var window = CreateLauncherWindowSync(directory, board, store, starter, items);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();
            // 真实输入前置：先真实点击选中首项（窗口随之成为前台，修饰键注入可达）。
            ForegroundActivateLauncherSurface(window);

            // 勾选钮 toggle（锚点更新）。
            SelectLauncherTileViaButton(window, catalog.Items[0]);
            Assert.AreEqual(1, LauncherSelectedCountForTests(window), "勾选钮选中第 0 项。");

            // Ctrl+单击第 2 项：逐击 toggle（叠加，锚点更新）。
            CtrlClickLauncherTile(window, catalog.Items[2]);
            Assert.AreEqual(2, LauncherSelectedCountForTests(window), "Ctrl 单击叠加第 2 项。");
            CtrlClickLauncherTile(window, catalog.Items[2]);
            Assert.AreEqual(1, LauncherSelectedCountForTests(window), "Ctrl 单击再击取消。");

            // Shift+单击第 3 项：锚点在第 0 项（勾选钮选中未撤销）→ 范围选 [0..3]。
            ShiftClickLauncherTile(window, catalog.Items[3]);
            Assert.AreEqual(4, LauncherSelectedCountForTests(window), "Shift 范围选锚点到目标。");

            // Ctrl+A：全选后再按清空（SendInput 硬件级组合键——产线读全局
            // Keyboard.Modifiers，与真实输入同源；独立旅程：修饰键连击有系统级
            // 串扰风险，前置真实点击重新前台化并安定）。
            ForegroundActivateLauncherSurface(window);
            PressCtrlABySendInput(window);
            Assert.AreEqual(4, LauncherSelectedCountForTests(window), "Ctrl+A 全选（已全选保持）。");
            PressCtrlABySendInput(window);
            Assert.AreEqual(0, LauncherSelectedCountForTests(window), "Ctrl+A 再按清空。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_Selection_NonEmptyClickTogglesAndSuspendsLaunch()
    {
        using var directory = new TestDirectory();
        var stubs = Enumerable.Range(0, 3).Select(_ => new StubAppFile()).ToArray();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var items = Enumerable.Range(0, 3)
            .Select(index => new LauncherItem(
                Guid.NewGuid(),
                LauncherKind.Application,
                stubs[index].Path,
                $"应用{index}",
                DateTimeOffset.UtcNow))
            .ToArray();
        var window = CreateLauncherWindowSync(directory, board, store, starter, items);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();

            // 选择非空：瓦片非控件区单击=toggle（启动挂起）。
            SelectLauncherTileViaButton(window, catalog.Items[0]);
            ClickLauncherTile(window, catalog.Items[1]);
            CompleteLayout(window);
            Assert.AreEqual(0, starter.Started.Count, "选择非空单击不启动。");
            Assert.IsTrue(catalog.Items[1].IsSelected, "选择非空单击=toggle 选中。");

            // 取消最后一项选择的单击不得顺带启动。
            ClickLauncherTile(window, catalog.Items[1]);
            ClickLauncherTile(window, catalog.Items[0]);
            CompleteLayout(window);
            Assert.AreEqual(0, LauncherSelectedCountForTests(window), "逐击取消清空选择。");
            Assert.AreEqual(0, starter.Started.Count, "末项取消的单击不启动。");

            // 选择清空后单击恢复启动语义（启动区单击——名称带单击=仅聚焦）。
            ClickLauncherLaunchZone(window, catalog.Items[2]);
            CompleteLayout(window);
            Assert.AreEqual(1, starter.Started.Count, "选择清空后单击恢复启动。");
        }
        finally
        {
            foreach (var stub in stubs)
            {
                stub.Dispose();
            }

            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_Selection_BatchDeleteWithWholeBatchUndo()
    {
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var items = Enumerable.Range(0, 5)
            .Select(index => new LauncherItem(
                Guid.NewGuid(),
                LauncherKind.Application,
                $"{app.Path}.{index}",
                $"应用{index}",
                DateTimeOffset.UtcNow))
            .ToArray();
        var window = CreateLauncherWindowSync(directory, board, store, starter, items);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();

            // 选第 1、3 项：头部按钮=「删除已选 2 项」＋角标可见。
            SelectLauncherTileViaButton(window, catalog.Items[1]);
            SelectLauncherTileViaButton(window, catalog.Items[3]);
            var trash = (Button)window.FindName("DeleteContentButton")!;
            Assert.AreEqual(Visibility.Visible, trash.Visibility, "选择非空=垃圾桶可见。");
            Assert.AreEqual("删除已选 2 项", AutomationProperties.GetName(trash), "头部按钮=删除已选 N 项。");
            Assert.IsTrue(trash.IsEnabled, "选择非空=垃圾桶可点（T7 后实机可达守护：IsEnabled 按表面由代码管理）。");
            Assert.AreEqual(Visibility.Visible, ((Border)window.FindName("SelectedCountBadge")).Visibility, "角标可见。");
            Assert.AreEqual("2", ((TextBlock)window.FindName("SelectedCountText")).Text, "角标计数=2。");

            // 垃圾桶点击=整批删除（单次事务链，提示条=「已删除 2 项」）。
            trash.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, trash));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(600));
            CompleteLayout(window);
            Assert.AreEqual(3, catalog.Items.Count, "整批删除生效。");
            Assert.IsFalse(catalog.Items.Any(entry => entry.Id == items[1].Id || entry.Id == items[3].Id), "被选条目离开集合。");
            Assert.AreEqual(
                "已删除 2 项",
                ((TextBlock)window.FindName("LauncherUndoToastText")).Text,
                "提示条=整批文案。");
            Assert.AreEqual(0, LauncherSelectedCountForTests(window), "删除后选择清空。");

            // 整批撤销：按原下标重插（1、3 回原位）。
            var undo = FindLauncherUndoButton(window);
            Assert.IsNotNull(undo);
            undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, undo));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(600));
            CompleteLayout(window);
            Assert.AreEqual(5, catalog.Items.Count, "整批撤销恢复 5 项。");
            Assert.AreEqual(1, catalog.Items.IndexOf(items[1]), "第 1 项回原下标。");
            Assert.AreEqual(3, catalog.Items.IndexOf(items[3]), "第 3 项回原下标。");
            var persisted = store.LoadAsync().GetAwaiter().GetResult();
            Assert.AreEqual(5, persisted.Items.Count, "整批撤销持久化。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_Selection_SuspendsRenameAndDragDuringSelection()
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

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();

            // 选择非空：F2 改名挂起（不进入会话）。
            SelectLauncherTileViaButton(window, catalog.Items[0]);
            window.RaiseEvent(NewKeyEventArgs(window, Key.F2));
            CompleteLayout(window);
            Assert.IsFalse(catalog.Items[0].IsEditingName, "选择期 F2 改名挂起。");

            // 选择非空：名称带双击改名挂起。
            var tile = FindLauncherTile(window, catalog.Items[1]);
            Assert.IsNotNull(tile);
            DoubleClickLauncherNameBand(tile);
            CompleteLayout(window);
            Assert.IsFalse(catalog.Items[1].IsEditingName, "选择期名称带双击改名挂起。");

            // 清空选择后 F2 恢复可用（方向键先初始化焦点——无焦点时 F2 目标为 -1）。
            window.RaiseEvent(NewKeyEventArgs(window, Key.Escape));
            CompleteLayout(window);
            window.RaiseEvent(NewKeyEventArgs(window, Key.Left));
            window.RaiseEvent(NewKeyEventArgs(window, Key.F2));
            CompleteLayout(window);
            Assert.IsTrue(catalog.Items[0].IsEditingName, "清空选择后 F2 改名恢复。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    private static void ClickLauncherTile(MainWindow window, LauncherItem item)
    {
        // 合成事件的 GetPosition 解析 Mouse.PrimaryDevice 全局位置（与真实输入
        // 同源）：先把真实光标置到名称带（不启动），位移 <4 DIP 判定才成立。
        var tile = FindLauncherTile(window, item);
        Assert.IsNotNull(tile);
        var point = tile.TranslatePoint(new Point(tile.ActualWidth / 2, tile.ActualHeight - 12), window);
        var screenPoint = window.PointToScreen(point);
        SetCursorPos((int)screenPoint.X, (int)screenPoint.Y);
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));
        tile.RaiseEvent(NewMouseButtonEventArgs(UIElement.MouseLeftButtonDownEvent, tile, timestamp: 300));
        tile.RaiseEvent(NewMouseButtonEventArgs(UIElement.MouseLeftButtonUpEvent, tile, timestamp: 310));
    }

    /// <summary>合成单击启动区（图标格中心）：清空选择后的启动语义路径。</summary>
    private static void ClickLauncherLaunchZone(MainWindow window, LauncherItem item)
    {
        var tile = FindLauncherTile(window, item);
        Assert.IsNotNull(tile);
        var zone = FindDescendants<System.Windows.Controls.Grid>(tile)
            .First(candidate => candidate.Tag as string == CardGestureZones.LauncherLaunchZone);
        var point = zone.TranslatePoint(new Point(zone.ActualWidth / 2, zone.ActualHeight / 2), window);
        var screenPoint = window.PointToScreen(point);
        SetCursorPos((int)screenPoint.X, (int)screenPoint.Y);
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));
        // Direct 路由事件必须从 handler 宿主（TileRoot）Raise；Source=启动区让
        // 分区解析命中 LaunchZone。
        tile.RaiseEvent(NewMouseButtonEventArgs(UIElement.MouseLeftButtonDownEvent, zone, timestamp: 300));
        tile.RaiseEvent(NewMouseButtonEventArgs(UIElement.MouseLeftButtonUpEvent, zone, timestamp: 310));
    }

    private static void CtrlClickLauncherTile(MainWindow window, LauncherItem item)
    {
        // Ctrl+单击语义读全局 Keyboard.Modifiers（与真实输入同源）：修饰键经
        // SendInput VK 硬件级注入（契约 #13；合成事件携带不了全局修饰态）。
        var tile = FindLauncherTile(window, item);
        Assert.IsNotNull(tile);
        var center = tile.TranslatePoint(new Point(tile.ActualWidth / 2, tile.ActualHeight - 12), window);
        var screen = window.PointToScreen(center);
        SetCursorPos((int)screen.X, (int)screen.Y);
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(60));
        HoldKeyBySendInput(window, VkControl, isDown: true);
        ClickBySendInput(window);
        HoldKeyBySendInput(window, VkControl, isDown: false);
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(120));
    }

    private static void ShiftClickLauncherTile(MainWindow window, LauncherItem item)
    {
        var tile = FindLauncherTile(window, item);
        Assert.IsNotNull(tile);
        var center = tile.TranslatePoint(new Point(tile.ActualWidth / 2, tile.ActualHeight - 12), window);
        var screen = window.PointToScreen(center);
        SetCursorPos((int)screen.X, (int)screen.Y);
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(60));
        HoldKeyBySendInput(window, VkShift, isDown: true);
        ClickBySendInput(window);
        HoldKeyBySendInput(window, VkShift, isDown: false);
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(120));
    }

    private const ushort VkShift = 0x10;

    /// <summary>SendInput 硬件级 Ctrl+A 组合键（CardAi PressCtrlZBySendInput 同法）。</summary>
    private static void PressCtrlABySendInput(MainWindow window)
    {
        var inputs = new KeyboardInput[4];
        inputs[0].Type = InputKeyboard;
        inputs[0].Ki.VirtualKey = VkControl;
        inputs[1].Type = InputKeyboard;
        inputs[1].Ki.VirtualKey = VkA;
        inputs[2].Type = InputKeyboard;
        inputs[2].Ki.VirtualKey = VkA;
        inputs[2].Ki.Flags = KeyEventfKeyup;
        inputs[3].Type = InputKeyboard;
        inputs[3].Ki.VirtualKey = VkControl;
        inputs[3].Ki.Flags = KeyEventfKeyup;
        Assert.AreEqual(
            4u,
            SendKeyboardInput(4, inputs, Marshal.SizeOf<KeyboardInput>()),
            "真实键盘注入必须成功。");
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
    }

    /// <summary>真实点击首个瓦片把窗口带到前台（修饰键 SendInput 要求目标窗口
    // 已是前台，CardAi 同约束）；点击点取名称带（不启动不选择，纯净前置）。</summary>
    private static void ForegroundActivateLauncherSurface(MainWindow window)
    {
        var tile = FindLauncherTile(window, window.LaunchersForTests().Items[0]);
        Assert.IsNotNull(tile, "前置：瓦片容器已生成。");
        window.Topmost = true;
        CompleteLayout(window);
        HoverRealAt(window, tile, new Point(tile.ActualWidth / 2, tile.ActualHeight - 12));
        ClickBySendInput(window);
        CompleteLayout(window);
    }

    // 真实键盘修饰键注入复用同类既有 SendKeyboardInput/KeyboardInput/
    // InputKeyboard/KeyEventfKeyup（partial class 共享成员）。

    /// <summary>按住/释放修饰键（SendInput VK 硬件级）。</summary>
    private static void HoldKeyBySendInput(MainWindow window, ushort virtualKey, bool isDown)
    {
        var inputs = new KeyboardInput[1];
        inputs[0].Type = InputKeyboard;
        inputs[0].Ki.VirtualKey = virtualKey;
        inputs[0].Ki.ScanCode = 0;
        inputs[0].Ki.Flags = isDown ? 0u : KeyEventfKeyup;
        Assert.AreEqual(
            1u,
            SendKeyboardInput(1, [inputs[0]], Marshal.SizeOf<KeyboardInput>()),
            "修饰键注入必须成功。");
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(60));
    }

    // ---- T7 评审修订（wbx job 20261018-211654-wxb，Issue #186）：评审意见回归锁定 ----

    [STATestMethod]
    public void QuickLaunch_Selection_T7_CtrlAKeepsAnchorForShiftRange()
    {
        // 评审意见 7：Ctrl+A 全选不清锚点（锚点恒为选中项）——全选后 Shift 单击
        // 仍从既有锚点建立范围（先清空再范围选）。
        using var directory = new TestDirectory();
        var stubs = Enumerable.Range(0, 4).Select(_ => new StubAppFile()).ToArray();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var items = Enumerable.Range(0, 4)
            .Select(index => new LauncherItem(
                Guid.NewGuid(), LauncherKind.Application, stubs[index].Path, $"应用{index}", DateTimeOffset.UtcNow))
            .ToArray();
        var window = CreateLauncherWindowSync(directory, board, store, starter, items);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();
            ForegroundActivateLauncherSurface(window);

            // 勾选第 0 项（锚点=0）→ Ctrl+A 全选 → Shift 单击第 3 项：范围 [0..3]。
            SelectLauncherTileViaButton(window, catalog.Items[0]);
            PressCtrlABySendInput(window);
            Assert.AreEqual(4, LauncherSelectedCountForTests(window), "前置：全选。");
            ShiftClickLauncherTile(window, catalog.Items[3]);
            Assert.AreEqual(4, LauncherSelectedCountForTests(window), "全选后 Shift 范围选仍覆盖锚点域。");
        }
        finally
        {
            foreach (var stub in stubs)
            {
                stub.Dispose();
            }

            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_Selection_T7_BatchUndoSameIndexRestoresOriginalOrder()
    {
        // 评审意见 1：连续删除同槽位（记录下标重复）后整批撤销按删除记录逆序重放，
        // 恢复原始顺序（B、C 而非 C、B）。
        using var directory = new TestDirectory();
        var stubs = Enumerable.Range(0, 3).Select(_ => new StubAppFile()).ToArray();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var items = Enumerable.Range(0, 3)
            .Select(index => new LauncherItem(
                Guid.NewGuid(), LauncherKind.Application, stubs[index].Path, $"应用{index}", DateTimeOffset.UtcNow))
            .ToArray();
        var window = CreateLauncherWindowSync(directory, board, store, starter, items);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();

            // 选第 1、2 项批量删除：两条记录的下标都是 1（删除时刻快照）。
            SelectLauncherTileViaButton(window, catalog.Items[1]);
            SelectLauncherTileViaButton(window, catalog.Items[2]);
            var trash = (System.Windows.Controls.Button)window.FindName("DeleteContentButton")!;
            trash.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, trash));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(600));
            CompleteLayout(window);
            Assert.AreEqual(1, catalog.Items.Count, "前置：批量删除生效。");

            // 整批撤销：恢复 2 项且保持原相对顺序（应用1 在前）。
            var undo = FindLauncherUndoButton(window);
            Assert.IsNotNull(undo);
            undo.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, undo));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(600));
            CompleteLayout(window);
            Assert.AreEqual(3, catalog.Items.Count, "整批撤销恢复 3 项。");
            Assert.AreEqual(items[1].Id, catalog.Items[1].Id, "同下标对按删除序恢复（先删 B 后 C → B 在前）。");
            Assert.AreEqual(items[2].Id, catalog.Items[2].Id, "C 在 B 后。");
        }
        finally
        {
            foreach (var stub in stubs)
            {
                stub.Dispose();
            }

            CloseWindowWithoutSaving(window);
        }
    }
}
