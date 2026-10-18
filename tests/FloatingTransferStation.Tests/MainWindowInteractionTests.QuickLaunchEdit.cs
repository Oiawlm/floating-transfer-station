using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 快捷启动编辑模式与删除安全网（#30，Issue #162）切片 3：编辑模式进出
/// （工具钮/F2/Esc/点空白/切标签/收起）、就地改名（预填全选/Enter/Esc/空回退/失败还原）、
/// × 删除 + 4s 单条撤销提示条事务（新删替换旧记、原下标重插、保存失败可重试、
/// 切标签/收起销毁）、拖拽重排（阈值 4 DIP）、键盘层级②。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    private static Button? FindLauncherUndoButton(MainWindow window)
    {
        var toast = (Border)window.FindName("LauncherUndoToast")!;
        return FindDescendant<Button>(toast);
    }

    // 2026-10-18 契约 #30 修订（Issue #186）：编辑模式移除（D3）——原 EnterLauncherEdit
    // 助手与 × 删除钮断言随之退场；改名/删除/拖拽由常态手势承载。测试进入改名会话走
    // 键盘 F2（D6 语义路径），拖拽按压点取启动区（图标格）中心（名称带不发起拖拽）。

    private static void InvokeLauncherRenameViaF2(MainWindow window)
    {
        window.RaiseEvent(NewKeyEventArgs(window, Key.F2));
    }

    private static bool IsLauncherToastVisible(MainWindow window) =>
        ((Border)window.FindName("LauncherUndoToast")!).Visibility == Visibility.Visible ||
        ((Border)window.FindName("LauncherUndoToast")!).Opacity > 0;

    [STATestMethod]
    public void QuickLaunch_Selection_ClearViaEscBlankAndTabSwitch()
    {
        // 契约 #30 修订改写（2026-10-18，Issue #186；原编辑模式进出旅程）：编辑模式
        // 移除后，模式化会话由「多选会话」承接——清空路径=Esc/点空白/切标签（D5），
        // 勾选钮与 Ctrl 单击建立选择。
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var first = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Application, app.Path, "甲", DateTimeOffset.UtcNow);
        var second = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Application, app.Path + "x", "乙", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, first, second);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();
            var liveFirst = catalog.Items.Single(item => item.Id == first.Id);
            var liveSecond = catalog.Items.Single(item => item.Id == second.Id);

            // 勾选钮建立选择（两个）。
            SelectLauncherTileViaButton(window, liveFirst);
            SelectLauncherTileViaButton(window, liveSecond);
            Assert.AreEqual(2, LauncherSelectedCountForTests(window), "勾选钮建立两选。");
            Assert.AreEqual(Visibility.Visible, ((System.Windows.Controls.Button)window.FindName("DeleteContentButton")).Visibility, "选择非空=垃圾桶可见。");

            // Esc 清空选择。
            window.RaiseEvent(NewKeyEventArgs(window, Key.Escape));
            CompleteLayout(window);
            Assert.AreEqual(0, LauncherSelectedCountForTests(window), "Esc 清空选择。");
            Assert.AreEqual(Visibility.Collapsed, ((System.Windows.Controls.Button)window.FindName("DeleteContentButton")).Visibility, "清空后垃圾桶隐藏。");

            // 再建立后点表面空白清空。
            SelectLauncherTileViaButton(window, liveFirst);
            var host = (Border)window.FindName("LauncherContentHost");
            host.RaiseEvent(NewMouseButtonEventArgs(
                UIElement.MouseLeftButtonDownEvent,
                host));
            CompleteLayout(window);
            Assert.AreEqual(0, LauncherSelectedCountForTests(window), "点空白清空选择。");

            // 再建立后切标签清空。
            SelectLauncherTileViaButton(window, liveFirst);
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            Assert.AreEqual(0, LauncherSelectedCountForTests(window), "切标签清空选择。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    private static void SelectLauncherTileViaButton(MainWindow window, LauncherItem item)
    {
        var tile = FindLauncherTile(window, item);
        Assert.IsNotNull(tile);
        var button = FindDescendant<Button>(tile) is { Name: "TileSelectButton" } selectButton
            ? selectButton
            : throw new AssertFailedException("勾选钮存在于瓦片模板。");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }

    private static int LauncherSelectedCountForTests(MainWindow window) =>
        window.LaunchersForTests().Items.Count(entry => entry.IsSelected);

    [STATestMethod]
    public void QuickLaunch_SelectionButton_VisibilityIsManagedAndHitWithinTile()
    {
        // 契约 #30 修订改写（2026-10-18，Issue #186；原 × 删除钮显隐守护 B1）：多选
        // 勾选钮承接 WCAG 命中区守护——基态隐（Opacity 0＋IsHitTestVisible=false），
        // 悬停/选中/键盘聚焦可见（Visible 语义经绑定承载），命中区 ≥24×24 且完全
        // 落在瓦片矩形内（无负 Margin 外溢，#13 族）。
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var first = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Application, app.Path, "甲", DateTimeOffset.UtcNow);
        var second = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Application, app.Path + "x", "乙", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, first, second);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();

            foreach (var item in catalog.Items)
            {
                var tile = FindLauncherTile(window, item);
                Assert.IsNotNull(tile);
                var button = FindDescendant<Button>(tile) is { Name: "TileSelectButton" } selectButton
                    ? selectButton
                    : throw new AssertFailedException("勾选钮存在于瓦片模板。");

                // 命中区几何（WCAG 2.5.8）：≥24×24 且完全落在瓦片矩形内。
                Assert.IsTrue(button.ActualWidth >= 24 && button.ActualHeight >= 24,
                    $"勾选钮命中区 {button.ActualWidth:0.#}×{button.ActualHeight:0.#} 必须 ≥24×24。");
                var topLeft = button.TranslatePoint(new Point(0, 0), tile);
                var hitRect = new Rect(topLeft, new Size(button.ActualWidth, button.ActualHeight));
                var tileRect = new Rect(0, 0, tile.ActualWidth, tile.ActualHeight);
                Assert.IsTrue(
                    tileRect.Contains(hitRect),
                    $"勾选钮命中区必须完全落在瓦片内（hit={hitRect} tile={tileRect}）。");
            }

            // 选中态常显：选择非空后按钮 IsHitTestVisible=true（绑定面承载，动效关闭
            // 下 Opacity 即时到位）。
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            var liveFirst = catalog.Items.Single(candidate => candidate.Id == first.Id);
            SelectLauncherTileViaButton(window, liveFirst);
            CompleteLayout(window);
            var selectedTile = FindLauncherTile(window, liveFirst);
            var selectedButton = FindDescendant<Button>(selectedTile!) is { Name: "TileSelectButton" } b
                ? b
                : throw new AssertFailedException("选中瓦片勾选钮存在。");
            Assert.IsTrue(selectedButton.IsHitTestVisible, "选中态勾选钮可命中（常显语义）。");
            Assert.IsTrue(selectedButton.Opacity > 0, "选中态勾选钮可见。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_RealInput_KeyboardDeleteAndUndoRestores()
    {
        // 契约 #30 修订改写（2026-10-18，Issue #186；原 × 删除钮真输入旅程）：删除
        // 入口键盘直达（D6：Delete=删焦点项），撤销仍走真实输入管线（SendInput 点击
        // 提示条「撤销」，契约 #13 端到端守护保留）。
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
            var middle = catalog.Items[1];
            window.Topmost = true;
            CompleteLayout(window);
            Keyboard.ClearFocus();

            // 焦点第二项（Right 从无焦点聚焦第 1 项）→ Delete 删除：撤销提示条出现。
            window.RaiseEvent(NewKeyEventArgs(window, Key.Right));
            window.RaiseEvent(NewKeyEventArgs(window, Key.Delete));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(400));
            CompleteLayout(window);

            Assert.AreEqual(2, catalog.Items.Count, "键盘 Delete 后删除生效。");
            Assert.IsFalse(catalog.Items.Contains(middle), "被删条目离开集合。");
            Assert.IsTrue(IsLauncherToastVisible(window), "撤销提示条出现。");
            Assert.AreEqual(
                $"已删除「{middle.DisplayName}」",
                ((TextBlock)window.FindName("LauncherUndoToastText")).Text);

            // 撤销走真实输入：点击提示条内「撤销」按原下标恢复。
            var toast = (Border)window.FindName("LauncherUndoToast")!;
            var undo = FindLauncherUndoButton(window);
            Assert.IsNotNull(undo);
            var undoCenterInToast = undo.TranslatePoint(
                new Point(undo.ActualWidth / 2, undo.ActualHeight / 2),
                toast);
            HoverRealAt(window, toast, undoCenterInToast);
            ClickBySendInput(window);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(400));
            CompleteLayout(window);

            Assert.AreEqual(3, catalog.Items.Count, "真实点击撤销后恢复。");
            Assert.AreEqual(1, catalog.Items.IndexOf(middle), "按原下标重插。");
            Assert.IsFalse(IsLauncherToastVisible(window), "撤销后提示条隐藏。");
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_RenameViaNameBand_CommitCancelAndEmptyFallback()
    {
        // 契约 #30 修订改写（2026-10-18，Issue #186；原「编辑态单击进入改名」）：
        // 名称带双击=就地改名（DoubleClickWindow 系统时限，合成双击=同瓦片同位置
        // 两击）；提交/取消/空回退语义与 Explicit 绑定修复保持。
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var first = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Application, app.Path, "原名", DateTimeOffset.UtcNow);
        var second = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Application, app.Path + "x", "乙", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, first, second);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();
            var liveFirst = catalog.Items.Single(item => item.Id == first.Id);

            // 名称带双击=就地改名：编辑器预填全选（第一击不启动不选择，第二击进会话）。
            var tile = FindLauncherTile(window, liveFirst);
            Assert.IsNotNull(tile);
            DoubleClickLauncherNameBand(tile);
            CompleteLayout(window);
            var editor = FindDescendant<TextBox>(tile!);
            Assert.IsNotNull(editor, "就地改名编辑器出现。");
            Assert.IsTrue(editor.IsKeyboardFocusWithin);
            Assert.AreEqual("原名", editor.Text);
            Assert.AreEqual("原名".Length, editor.SelectionLength, "进入编辑预填全选。");

            // Enter 提交：改名经串行协议持久化（DraftName 为显式绑定源，直接写模型侧）。
            liveFirst.DraftName = "新名字";
            editor.RaiseEvent(NewKeyEventArgs(window, Key.Enter));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
            CompleteLayout(window);
            Assert.AreEqual("新名字", catalog.Items.Single(item => item.Id == first.Id).DisplayName);
            var persisted = store.LoadAsync().GetAwaiter().GetResult();
            Assert.AreEqual(
                "新名字",
                persisted.Items.Single(item => item.Id == first.Id).DisplayName,
                "改名持久化到 launchers.json。");

            // Esc 取消：不改名。
            tile = FindLauncherTile(window, catalog.Items.Single(item => item.Id == first.Id));
            DoubleClickLauncherNameBand(tile!);
            CompleteLayout(window);
            editor = FindDescendant<TextBox>(tile!);
            liveFirst.DraftName = "不该出现";
            editor!.RaiseEvent(NewKeyEventArgs(window, Key.Escape));
            CompleteLayout(window);
            Assert.AreEqual(
                "新名字",
                catalog.Items.Single(item => item.Id == first.Id).DisplayName,
                "Esc 取消改名。");

            // 空提交=回退原名。
            tile = FindLauncherTile(window, catalog.Items.Single(item => item.Id == first.Id));
            DoubleClickLauncherNameBand(tile!);
            CompleteLayout(window);
            editor = FindDescendant<TextBox>(tile!);
            liveFirst.DraftName = "   ";
            editor!.RaiseEvent(NewKeyEventArgs(window, Key.Enter));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
            Assert.AreEqual(
                "新名字",
                catalog.Items.Single(item => item.Id == first.Id).DisplayName,
                "空/空白提交=回退原名。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    /// <summary>名称带合成双击（两击同位置、时限内）。MouseLeftButtonDown/Up 是
    /// Direct 路由事件，必须从瓦片根（handler 宿主）RaiseEvent；Source 参数传名称带
    /// ——手势层经 OriginalSource 可视树路径解析出 NameZone，与真实命中同构。</summary>
    private static void DoubleClickLauncherNameBand(Border tile)
    {
        var band = FindDescendants<Border>(tile)
            .First(candidate => candidate.Tag as string == CardGestureZones.LauncherNameZone);
        tile.RaiseEvent(NewMouseButtonEventArgs(UIElement.MouseLeftButtonDownEvent, band, timestamp: 100));
        tile.RaiseEvent(NewMouseButtonEventArgs(UIElement.MouseLeftButtonUpEvent, band, timestamp: 110));
        tile.RaiseEvent(NewMouseButtonEventArgs(UIElement.MouseLeftButtonDownEvent, band, timestamp: 200));
        tile.RaiseEvent(NewMouseButtonEventArgs(UIElement.MouseLeftButtonUpEvent, band, timestamp: 210));
    }

    [STATestMethod]
    public void QuickLaunch_EditMode_DeleteShowsToastAndUndoRestoresAtOriginalIndex()
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
            var middle = catalog.Items[1];

            // 删除入口=键盘 Delete 焦点项（编辑模式移除，2026-10-18 契约修订）。
            window.RaiseEvent(NewKeyEventArgs(window, Key.Right));
            window.RaiseEvent(NewKeyEventArgs(window, Key.Delete));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(250));
            CompleteLayout(window);

            Assert.AreEqual(2, catalog.Items.Count, "删除后 2 项。");
            Assert.IsTrue(IsLauncherToastVisible(window), "撤销提示条出现。");
            Assert.AreEqual(
                $"已删除「{middle.DisplayName}」",
                ((TextBlock)window.FindName("LauncherUndoToastText")).Text);

            var undo = FindLauncherUndoButton(window);
            Assert.IsNotNull(undo);
            undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, undo));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(250));
            CompleteLayout(window);

            Assert.AreEqual(3, catalog.Items.Count, "撤销恢复。");
            Assert.AreEqual(1, catalog.Items.IndexOf(middle), "按原下标重插（min(原下标, 当前数)）。");
            Assert.IsFalse(IsLauncherToastVisible(window), "撤销后提示条隐藏。");

            // 4s 超时销毁记录：再次删除后推进计时器 tick，撤销不可再恢复
            // （恢复后焦点仍钳在 middle 下标，键盘 Delete 直达）。
            window.RaiseEvent(NewKeyEventArgs(window, Key.Delete));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(250));
            InvokePrivate(window, "LauncherUndoTimer_Tick", null, EventArgs.Empty);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
            Assert.IsFalse(IsLauncherToastVisible(window), "4s 超时提示条销毁。");
            undo = FindLauncherUndoButton(window);
            undo?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, undo!));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(250));
            Assert.AreEqual(2, catalog.Items.Count, "超时后撤销不再恢复。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_UndoToast_NewDeleteReplacesOldRecordAndDestroysOnTabSwitch()
    {
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var items = Enumerable.Range(0, 2)
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
            var first = catalog.Items[0];
            var second = catalog.Items[1];
            Keyboard.ClearFocus();

            // 删除入口=键盘 Delete 焦点项（编辑模式移除，2026-10-18 契约修订）；
            // Left 从无焦点聚焦第 0 项删 first；焦点钳制后再 Delete 删 second。
            window.RaiseEvent(NewKeyEventArgs(window, Key.Left));
            window.RaiseEvent(NewKeyEventArgs(window, Key.Delete));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));

            // 新删除替换旧记录：提示条文案换为第二个，旧的不可再恢复。
            window.RaiseEvent(NewKeyEventArgs(window, Key.Delete));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(250));
            CompleteLayout(window);
            Assert.AreEqual(0, catalog.Items.Count, "两删后清空。");
            Assert.AreEqual(
                $"已删除「{second.DisplayName}」",
                ((TextBlock)window.FindName("LauncherUndoToastText")).Text,
                "新删除替换旧记录（计时重置）。");

            // 切标签：提示条与记录一起销毁。
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
            Assert.IsFalse(IsLauncherToastVisible(window), "切标签销毁提示条与记录。");

            var undo = FindLauncherUndoButton(window);
            undo?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, undo!));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
            Assert.AreEqual(0, catalog.Items.Count, "销毁后撤销不可再恢复。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_Keyboard_DeleteAndF2Rename()
    {
        // 契约 #30 修订改写（2026-10-18，Issue #186；原「编辑模式键盘族」）：常态键盘
        // 语义——F2=改名焦点项（不再进编辑模式）、Delete=删焦点项（带撤销）、
        // 方向键导航、Enter=启动（另测）。
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var first = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Application, app.Path, "甲", DateTimeOffset.UtcNow);
        var second = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Application, app.Path + "x", "乙", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, first, second);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();
            Keyboard.ClearFocus();

            // 键盘 Delete=删焦点瓦片（方向键初始化焦点：Left 从无焦点聚焦第 0 项；
            // 不弹模态，撤销条兜底）。
            window.RaiseEvent(NewKeyEventArgs(window, Key.Left));
            window.RaiseEvent(NewKeyEventArgs(window, Key.Delete));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(250));
            CompleteLayout(window);
            Assert.AreEqual(1, catalog.Items.Count, $"常态 Delete 删焦点瓦片（focus after Right={GetPrivateField<int>(window, "_launcherFocusedIndex")}）。");
            Assert.IsFalse(
                catalog.Items.Any(item => item.Id == first.Id),
                $"删除的是焦点项（remaining={string.Join(",", catalog.Items.Select(entry => entry.DisplayName))}）。");
            Assert.IsTrue(IsLauncherToastVisible(window), "删除有撤销提示条兜底（无模态）。");

            // 键盘 F2=改名焦点项（进入既有改名会话，无编辑模式）。
            window.RaiseEvent(NewKeyEventArgs(window, Key.F2));
            CompleteLayout(window);
            var remaining = catalog.Items.Single();
            Assert.IsTrue(
                remaining.IsEditingName,
                $"前置：F2 已进入改名（DraftName={remaining.DraftName}；focus={GetPrivateField<int>(window, "_launcherFocusedIndex")}；focused={Keyboard.FocusedElement?.GetType().Name ?? "null"}）。");
            var focusedTile = FindLauncherTile(window, remaining);
            Assert.IsNotNull(focusedTile);
            var editor = FindDescendant<TextBox>(focusedTile);
            Assert.AreEqual("TileNameEditor", editor?.Name);
            Assert.IsNotNull(editor, "键盘 F2 进入改名编辑器。");
            Assert.IsTrue(editor.IsKeyboardFocusWithin, "前置：编辑器持有焦点。");
            remaining.DraftName = "改名了";
            Assert.AreEqual("改名了", editor.Text, "前置：DraftName 绑定回显。");
            editor.RaiseEvent(NewKeyEventArgs(window, Key.Enter));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
            Assert.IsFalse(remaining.IsEditingName, "前置：Enter 已终结改名会话。");
            Assert.AreEqual(
                "改名了",
                catalog.Items.Single().DisplayName,
                "键盘改名提交。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_EditMode_DragFollowIsDirectAssignmentWithoutAnimation()
    {
        // F3 守护（#30 本义「拖动瓦片跟随指针」）：动效开启档的跟随必须是直接
        // 赋值——变换值==累计位移（叠加接管基值）且无动画在身（IsAnimated==
        // false）；追赶动画=恒定空间滞后=粘滞感的根因。归位/让位动画不受影响。
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var items = Enumerable.Range(0, 6)
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
            // 展开期动效关闭（入场交错是 async void，测试宿主无同步上下文），
            // 测量段显式开启——直接赋值两档统一，开启档是用户默认。
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();
            var dragged = catalog.Items[0];

            CompleteLayout(window);
            window.Resources[SystemParameters.ClientAreaAnimationKey] = true;

            var draggedTile = FindLauncherTile(window, dragged);
            Assert.IsNotNull(draggedTile);
            var centerInWindow = draggedTile.TranslatePoint(
                new Point(draggedTile.ActualWidth / 2, draggedTile.ActualHeight / 2),
                window);
            var pressScreen = window.PointToScreen(centerInWindow);
            SetCursorPos((int)pressScreen.X, (int)pressScreen.Y);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));
            draggedTile.RaiseEvent(NewMouseButtonEventArgs(
                UIElement.MouseLeftButtonDownEvent,
                draggedTile));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));

            // 第一步越过阈值：拖动会话建立（接管基值=接管时刻的当前有效值）。
            var first = new Point(centerInWindow.X + 30, centerInWindow.Y + 12);
            var screen = window.PointToScreen(first);
            SetCursorPos((int)screen.X, (int)screen.Y);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
            draggedTile.RaiseEvent(NewMouseEventArgs(
                UIElement.PreviewMouseMoveEvent,
                draggedTile));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));

            var translate = GetLauncherTileTranslate(draggedTile);
            Assert.IsNotNull(translate, "前置：瓦片平移变换存在。");
            // 基值与位移都取自真实光标坐标（SetCursorPos 物理像素取整，GetPosition
            // 返回的实际 DIP 坐标才是处理器看到的输入）。
            var origin = new Point(translate.X, translate.Y);
            var firstPosition = Mouse.GetPosition(window);

            // 后续每步：值==基值+累计位移且 IsAnimated==false（无追赶动画）。
            for (var step = 1; step <= 6; step++)
            {
                var target = new Point(
                    centerInWindow.X + 30 + step * 11,
                    centerInWindow.Y + 12 + (step % 3) * 9);
                screen = window.PointToScreen(target);
                SetCursorPos((int)screen.X, (int)screen.Y);
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(12));
                var actual = Mouse.GetPosition(window);
                draggedTile.RaiseEvent(NewMouseEventArgs(
                    UIElement.PreviewMouseMoveEvent,
                    draggedTile));
                var xSource = DependencyPropertyHelper.GetValueSource(
                    translate, System.Windows.Media.TranslateTransform.XProperty);
                var ySource = DependencyPropertyHelper.GetValueSource(
                    translate, System.Windows.Media.TranslateTransform.YProperty);
                Assert.IsFalse(xSource.IsAnimated, $"第 {step} 步 X 不应有动画在身。");
                Assert.IsFalse(ySource.IsAnimated, $"第 {step} 步 Y 不应有动画在身。");
                Assert.AreEqual(
                    origin.X + (actual.X - firstPosition.X),
                    translate.X,
                    0.01,
                    $"第 {step} 步 X==基值+累计位移。");
                Assert.AreEqual(
                    origin.Y + (actual.Y - firstPosition.Y),
                    translate.Y,
                    0.01,
                    $"第 {step} 步 Y==基值+累计位移。");
            }

            // 抬起取消（回到原位无目标重排）：归位动画保留（167ms 位移回零）。
            draggedTile.ReleaseMouseCapture();
            InvokePrivate(window, "EndLauncherTileDrag", false);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(120));
            var settling = DependencyPropertyHelper.GetValueSource(
                translate, System.Windows.Media.TranslateTransform.XProperty);
            Assert.IsTrue(
                settling.IsAnimated || Math.Abs(translate.X - origin.X) < 1.5,
                "取消后归位动画仍在（或已完成回零）。");
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindowWithoutSaving(window);
        }
    }

    private static System.Windows.Media.TranslateTransform? GetLauncherTileTranslate(Border tile)
    {
        var group = tile.RenderTransform as System.Windows.Media.TransformGroup;
        return group?.Children.OfType<System.Windows.Media.TranslateTransform>().FirstOrDefault();
    }

    private static System.Windows.Media.ScaleTransform? GetLauncherTileScale(Border tile)
    {
        var group = tile.RenderTransform as System.Windows.Media.TransformGroup;
        return group?.Children.OfType<System.Windows.Media.ScaleTransform>().FirstOrDefault();
    }

    [STATestMethod]
    public void QuickLaunch_EditMode_DragMovesDoNotTraverseVisualTree()
    {
        // F4 守护：拖动期间的 MouseMove 命中不查视觉树（起拖一次解析缓存；每步
        // 纯几何扫描）——MouseMove 处理成本与瓦片数解耦的结构证明。
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var items = Enumerable.Range(0, 12)
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
            var dragged = catalog.Items[0];

            CompleteLayout(window);
            window.Topmost = true;
            CompleteLayout(window);

            var draggedTile = FindLauncherTile(window, dragged);
            Assert.IsNotNull(draggedTile);
            var centerInWindow = draggedTile.TranslatePoint(
                new Point(draggedTile.ActualWidth / 2, draggedTile.ActualHeight / 2),
                window);
            var screen = window.PointToScreen(centerInWindow);
            SetCursorPos((int)screen.X, (int)screen.Y);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));
            draggedTile.RaiseEvent(NewMouseButtonEventArgs(
                UIElement.MouseLeftButtonDownEvent,
                draggedTile));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));

            // 第一步建立拖动会话（缓存构建允许查树），其后 40 步零查找。
            screen = window.PointToScreen(new Point(centerInWindow.X + 30, centerInWindow.Y + 12));
            SetCursorPos((int)screen.X, (int)screen.Y);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
            draggedTile.RaiseEvent(NewMouseEventArgs(
                UIElement.PreviewMouseMoveEvent,
                draggedTile));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));

            var lookupsBefore = window.LauncherTileLookupCountForTests;
            for (var step = 0; step < 40; step++)
            {
                screen = window.PointToScreen(new Point(
                    centerInWindow.X + 30 + (step % 5) * 10,
                    centerInWindow.Y + 12 + (step / 5) * 9));
                SetCursorPos((int)screen.X, (int)screen.Y);
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(6));
                draggedTile.RaiseEvent(NewMouseEventArgs(
                    UIElement.PreviewMouseMoveEvent,
                    draggedTile));
            }

            Assert.AreEqual(
                lookupsBefore,
                window.LauncherTileLookupCountForTests,
                "拖动期间每步命中必须零视觉树查找（缓存命中，成本与瓦片数解耦）。");

            draggedTile.ReleaseMouseCapture();
            InvokePrivate(window, "EndLauncherTileDrag", false);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(60));
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_EditMode_DragReorderPublishesSingleMoveAndKeepsContainers()
    {
        // F5 窗口层守护（#30 修订，Issue #176）：拖拽重排的 UI 发布=恰一条 Move
        // 通知（无 Reset），容器实例跨重排保持（无全量重建）。
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
        var savedCursor = SaveCursorPosition();

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();
            var dragged = catalog.Items[0];
            var target = catalog.Items[3];

            CompleteLayout(window);
            window.Topmost = true;
            CompleteLayout(window);

            var draggedTile = FindLauncherTile(window, dragged);
            var targetTile = FindLauncherTile(window, target);
            Assert.IsNotNull(draggedTile);
            Assert.IsNotNull(targetTile);
            var grid = (ItemsControl)window.FindName("LauncherTileGrid")!;
            // 锚定具体条目（同下标容器在重排后天然换主；「保持」指同一 ITEM 的
            // 容器实例不变）。
            var anchored = catalog.Items[2];
            var containerBefore = grid.ItemContainerGenerator.ContainerFromIndex(2);

            var changes = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
            System.Collections.Specialized.NotifyCollectionChangedEventHandler onChange = (_, args) =>
                changes.Add(args.Action);
            catalog.Items.CollectionChanged += onChange;
            try
            {
                var press = draggedTile.PointToScreen(new Point(
                    draggedTile.ActualWidth / 2, draggedTile.ActualHeight / 2));
                SetCursorPos((int)press.X, (int)press.Y);
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));
                draggedTile.RaiseEvent(NewMouseButtonEventArgs(
                    UIElement.MouseLeftButtonDownEvent, draggedTile));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));
                var move = targetTile.PointToScreen(new Point(
                    targetTile.ActualWidth / 2, targetTile.ActualHeight / 2));
                SetCursorPos((int)move.X, (int)move.Y);
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));
                draggedTile.RaiseEvent(NewMouseEventArgs(
                    UIElement.PreviewMouseMoveEvent, draggedTile));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(60));
                draggedTile.RaiseEvent(NewMouseButtonEventArgs(
                    UIElement.MouseLeftButtonUpEvent, draggedTile, timestamp: 61));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            }
            finally
            {
                catalog.Items.CollectionChanged -= onChange;
            }

            CompleteLayout(window);
            Assert.AreEqual(3, catalog.Items.IndexOf(dragged), "重排提交生效。");
            CollectionAssert.AreEqual(
                new[] { System.Collections.Specialized.NotifyCollectionChangedAction.Move },
                changes,
                "拖拽重排的 UI 发布恰一条 Move 通知（无 Reset/Add/Remove）。");
            Assert.IsTrue(
                ReferenceEquals(
                    containerBefore,
                    grid.ItemContainerGenerator.ContainerFromIndex(catalog.Items.IndexOf(anchored))),
                "同一条目的容器实例跨重排保持（单 Move 的容器复用语义）。");
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_EditMode_DragCacheTracksLayoutSlotsNotTransientOffsets()
    {
        // T7 BLOCKER 2 守护（Issue #176）：命中缓存矩形必须锚定**布局槽**（不含
        // RenderTransform）——链式拖拽时上一轮让位动画的 FLIP 残差不得污染下一轮
        // 的落点判定；拖动中悬停抬升（-2px）同样不进落点槽。
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

            CompleteLayout(window);

            // 第一轮拖拽提交：松手后让位动画路径已走过（减动效下为立即重置；本
            // 测试再显式给非被拖瓦片挂一个 FLIP 残差变换，模拟动画中途的视觉
            // 偏移——缓存若取含 RenderTransform 的矩形就会带残差）。
            var firstTile = FindLauncherTile(window, catalog.Items[0]);
            Assert.IsNotNull(firstTile);
            var press = firstTile.PointToScreen(new Point(
                firstTile.ActualWidth / 2, firstTile.ActualHeight / 2));
            SetCursorPos((int)press.X, (int)press.Y);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));
            firstTile.RaiseEvent(NewMouseButtonEventArgs(
                UIElement.MouseLeftButtonDownEvent, firstTile));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));
            var targetTile = FindLauncherTile(window, catalog.Items[3]);
            Assert.IsNotNull(targetTile);
            var drop = targetTile.PointToScreen(new Point(
                targetTile.ActualWidth / 2, targetTile.ActualHeight / 2));
            SetCursorPos((int)drop.X, (int)drop.Y);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));
            firstTile.RaiseEvent(NewMouseEventArgs(
                UIElement.PreviewMouseMoveEvent, firstTile));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(60));
            firstTile.RaiseEvent(NewMouseButtonEventArgs(
                UIElement.MouseLeftButtonUpEvent, firstTile, timestamp: 91));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
            CompleteLayout(window);

            // 给若干瓦片挂 FLIP 残差（模拟让位动画在途的 RenderTransform 偏移）。
            var grid = (ItemsControl)window.FindName("LauncherTileGrid")!;
            var panel = FindDescendants<LauncherTilePanel>(grid).First();
            foreach (var (_, tile) in GetLauncherSnapshotForTests(window))
            {
                var translate = GetLauncherTileTranslate(tile);
                if (translate is not null)
                {
                    translate.SetCurrentValue(
                        System.Windows.Media.TranslateTransform.XProperty, -92d);
                }
            }

            CompleteLayout(window);

            // 第二轮拖拽立即起拖：缓存矩形应等于布局槽（残差 -92 不得进入）。
            var secondTile = FindLauncherTile(window, catalog.Items[1]);
            Assert.IsNotNull(secondTile);
            press = secondTile.PointToScreen(new Point(
                secondTile.ActualWidth / 2, secondTile.ActualHeight / 2));
            SetCursorPos((int)press.X, (int)press.Y);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));
            secondTile.RaiseEvent(NewMouseButtonEventArgs(
                UIElement.MouseLeftButtonDownEvent, secondTile));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));
            var movePos = secondTile.PointToScreen(new Point(
                secondTile.ActualWidth / 2 + 40, secondTile.ActualHeight / 2 + 8));
            SetCursorPos((int)movePos.X, (int)movePos.Y);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
            secondTile.RaiseEvent(NewMouseEventArgs(
                UIElement.PreviewMouseMoveEvent, secondTile));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));

            var slots = GetPrivateField<
                List<(int ItemsIndex, Guid Id, Border Tile, Rect WindowRect)>>(
                window,
                "_launcherDragHitSlots");
            Assert.IsNotNull(slots);
            Assert.IsTrue(slots.Count >= 3, "前置：缓存槽已建立。");
            var panelToWindow = panel.TransformToAncestor(window);
            foreach (var slot in slots)
            {
                var container = grid.ItemContainerGenerator.ContainerFromIndex(slot.ItemsIndex);
                var layoutSlot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot((FrameworkElement)container);
                var expected = panelToWindow.Transform(new Point(layoutSlot.X, layoutSlot.Y));
                Assert.AreEqual(
                    expected.X,
                    slot.WindowRect.X,
                    0.5,
                    $"缓存槽 {slot.ItemsIndex} X==布局槽（FLIP 残差不入缓存）。");
                Assert.AreEqual(
                    expected.Y,
                    slot.WindowRect.Y,
                    0.5,
                    $"缓存槽 {slot.ItemsIndex} Y==布局槽（FLIP 残差不入缓存）。");
                Assert.AreEqual(layoutSlot.Width, slot.WindowRect.Width, 0.5);
                Assert.AreEqual(layoutSlot.Height, slot.WindowRect.Height, 0.5);
            }

            secondTile.ReleaseMouseCapture();
            InvokePrivate(window, "EndLauncherTileDrag", false);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(60));
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindowWithoutSaving(window);
        }
    }

    private static List<(LauncherItem Item, Border Tile)> GetLauncherSnapshotForTests(MainWindow window) =>
        (List<(LauncherItem, Border)>)typeof(MainWindow)
            .GetMethod("SnapshotLauncherTiles", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, null)!;

    [STATestMethod]
    public void QuickLaunch_EditMode_DragCacheRebuildsWhenCollectionChangesMidDrag()
    {
        // T7 BLOCKER 1 守护（Issue #176）：拖动在途的集合变更（保存失败回滚的逆
        // Move、或菜单移动等旁路突变）会改变下标与布局——命中缓存必须即时重建，
        // 其后移动的落点判定按新序生效。
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

            CompleteLayout(window);

            // 起拖 1 号瓦片（不松手）。
            var draggedTile = FindLauncherTile(window, catalog.Items[1]);
            Assert.IsNotNull(draggedTile);
            var press = draggedTile.PointToScreen(new Point(
                draggedTile.ActualWidth / 2, draggedTile.ActualHeight / 2));
            SetCursorPos((int)press.X, (int)press.Y);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));
            draggedTile.RaiseEvent(NewMouseButtonEventArgs(
                UIElement.MouseLeftButtonDownEvent, draggedTile));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));
            var movePos = draggedTile.PointToScreen(new Point(
                draggedTile.ActualWidth / 2 + 30, draggedTile.ActualHeight / 2 + 6));
            SetCursorPos((int)movePos.X, (int)movePos.Y);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
            draggedTile.RaiseEvent(NewMouseEventArgs(
                UIElement.PreviewMouseMoveEvent, draggedTile));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));
            Assert.IsNotNull(GetPrivateField<Guid?>(window, "_launcherDraggingId"), "前置：拖动会话在途。");

            // 拖动在途的旁路集合变更：菜单路径把 3 号移到 0（真实目录突变）。
            var moved = catalog.Items[3];
            var mutation = catalog.MoveAsync(moved.Id, 0);
            PumpDispatcherUntil(window.Dispatcher, mutation);
            CompleteLayout(window);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));

            // 缓存已按新序重建：0 号槽现在承载原 3 号条目。
            var slots = GetPrivateField<
                List<(int ItemsIndex, Guid Id, Border Tile, Rect WindowRect)>>(
                window,
                "_launcherDragHitSlots");
            Assert.IsNotNull(slots);
            Assert.AreEqual(
                moved.Id,
                slots.Single(slot => slot.ItemsIndex == 0).Id,
                "集合变更后缓存下标按新序重建。");

            // 其后的移动落点按新序判定（0 号槽位置命中 → 目标下标 0）。
            var grid = (ItemsControl)window.FindName("LauncherTileGrid")!;
            var panel = FindDescendants<LauncherTilePanel>(grid).First();
            var container0 = grid.ItemContainerGenerator.ContainerFromIndex(0);
            var slot0 = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(
                (FrameworkElement)container0);
            var slot0InWindow = panel.TransformToAncestor(window).Transform(
                new Point(slot0.X + slot0.Width / 2, slot0.Y + slot0.Height / 2));
            var screen = window.PointToScreen(slot0InWindow);
            SetCursorPos((int)screen.X, (int)screen.Y);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
            draggedTile.RaiseEvent(NewMouseEventArgs(
                UIElement.PreviewMouseMoveEvent, draggedTile));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));
            Assert.AreEqual(
                0,
                GetPrivateField<int>(window, "_launcherDropTargetIndex"),
                "重建后的落点判定按新序命中 0 号槽。");

            draggedTile.ReleaseMouseCapture();
            InvokePrivate(window, "EndLauncherTileDrag", false);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(60));
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_EditMode_DragReorderPersistsOrder()
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
            var dragged = catalog.Items[0];
            var target = catalog.Items[2];

            CompleteLayout(window);

            var draggedTile = FindLauncherTile(window, dragged);
            var targetTile = FindLauncherTile(window, target);
            Assert.IsNotNull(draggedTile);
            Assert.IsNotNull(targetTile);

            // 真实光标承载合成事件的坐标（GetPosition 查询主输入设备=真实光标位置）：
            // 按下于拖动瓦片中心 → 光标移到目标瓦片中心（位移 >4 DIP 越过阈值且命中目标）
            // → Move → 抬起提交重排。
            var pressPoint = draggedTile.PointToScreen(new Point(
                draggedTile.ActualWidth / 2,
                draggedTile.ActualHeight / 2));
            SetCursorPos((int)pressPoint.X, (int)pressPoint.Y);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));
            draggedTile.RaiseEvent(NewMouseButtonEventArgs(
                UIElement.MouseLeftButtonDownEvent,
                draggedTile));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));

            var targetPoint = targetTile.PointToScreen(new Point(
                targetTile.ActualWidth / 2,
                targetTile.ActualHeight / 2));
            SetCursorPos((int)targetPoint.X, (int)targetPoint.Y);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(40));
            draggedTile.RaiseEvent(NewMouseEventArgs(
                UIElement.PreviewMouseMoveEvent,
                draggedTile));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(60));
            draggedTile.RaiseEvent(NewMouseButtonEventArgs(
                UIElement.MouseLeftButtonUpEvent,
                draggedTile,
                timestamp: 21));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            CompleteLayout(window);

            Assert.AreEqual(2, catalog.Items.IndexOf(dragged), "拖拽重排：拖动项落到目标下标。");
            var persisted = store.LoadAsync().GetAwaiter().GetResult();
            Assert.AreEqual(
                2,
                persisted.Items.Select(item => item.Id).ToList().IndexOf(dragged.Id),
                "重排持久化（数组顺序即唯一真相）。");
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindowWithoutSaving(window);
        }
    }
}
