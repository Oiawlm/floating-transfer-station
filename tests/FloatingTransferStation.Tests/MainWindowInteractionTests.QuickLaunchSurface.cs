using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FloatingTransferStation.Launcher;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 快捷启动瓦片表面（#30，Issue #162）切片 2：真实输入单击一次启动 + 500ms 重入门闩、
/// 排队收起有效性重验（失败取消收起保展开）、再展开抑制解除条件=指针离开后重入、
/// 混合拖入分流与状态条、启动失败灰态、键盘查看态、200 上限、两类入场动画互斥、
/// 证据目录四场景（浅/深×310/640、添加/启动失败、迁移前后、调序+轨/把手）。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    private sealed class RecordingLauncherStarter
    {
        public List<ProcessStartInfo> Started { get; } = [];
        public Func<ProcessStartInfo, bool> Respond { get; set; } = _ => true;

        public bool Invoke(ProcessStartInfo info)
        {
            Started.Add(info);
            return Respond(info);
        }
    }

    private sealed class StubAppFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"fts-ql-{Guid.NewGuid():N}.exe");

        public StubAppFile() => File.WriteAllText(Path, "stub");

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
        }
    }

    private static MainWindow CreateLauncherWindow(
        TestDirectory directory,
        BoardService board,
        LocalStore store,
        RecordingLauncherStarter starter,
        WindowSettings? settings = null,
        AppLauncher? appLauncher = null)
    {
        var window = CreateWindow(
            board,
            store,
            settings ?? WindowSettings.Default,
            null,
            new ImageNormalizer(store.ImagesDirectory),
            launcherStore: store,
            appLauncher: appLauncher ?? new AppLauncher(starter.Invoke));
        return window;
    }

    private static MainWindow CreateLauncherWindowSync(
        TestDirectory directory,
        BoardService board,
        LocalStore store,
        RecordingLauncherStarter starter,
        params LauncherItem[] items)
    {
        if (items.Length > 0)
        {
            // 纯 Task.Run 磁盘写：STA 阻塞等待安全（不需要回到 STA 线程）。
            store.SaveAsync(new LauncherStoreData(LauncherStoreData.CurrentVersion, [.. items]))
                .GetAwaiter().GetResult();
        }

        var window = CreateLauncherWindow(directory, board, store, starter);
        PumpUntilCondition(
            window.Dispatcher,
            () => window.LaunchersForTests() is { } catalog && catalog.Items.Count >= items.Length);
        return window;
    }

    private static MainWindow CreateLauncherWindowSync(
        TestDirectory directory,
        BoardService board,
        LocalStore store,
        RecordingLauncherStarter starter,
        AppLauncher appLauncher,
        params LauncherItem[] items)
    {
        if (items.Length > 0)
        {
            store.SaveAsync(new LauncherStoreData(LauncherStoreData.CurrentVersion, [.. items]))
                .GetAwaiter().GetResult();
        }

        var window = CreateLauncherWindow(directory, board, store, starter, appLauncher: appLauncher);
        PumpUntilCondition(
            window.Dispatcher,
            () => window.LaunchersForTests() is { } catalog && catalog.Items.Count >= items.Length);
        return window;
    }

    private static void PumpUntilCondition(Dispatcher dispatcher, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            PumpDispatcherFor(dispatcher, TimeSpan.FromMilliseconds(20));
        }
    }

    private static Border? FindLauncherTile(MainWindow window, LauncherItem item)
    {
        var catalog = window.LaunchersForTests();
        var index = catalog?.Items.IndexOf(item) ?? -1;
        if (index < 0)
        {
            return null;
        }

        // 纯 ItemsControl 的容器是 ContentPresenter：瓦片根 Border 是其模板子元素。
        var container = ((ItemsControl)window.FindName("LauncherTileGrid")!)
            .ItemContainerGenerator.ContainerFromIndex(index);
        return (container as Border) ?? FindDescendant<Border>(container);
    }

    // ---- 真实输入（契约 #13）：单击一次有效请求 + 500ms 重入门闩 ----

    [STATestMethod]
    public void QuickLaunch_RealInputClick_LaunchesExactlyOnceAndLatchHolds()
    {
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var item = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Application, app.Path, "应用", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, item);

        var savedCursor = SaveCursorPosition();

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var tile = FindLauncherTile(window, item);
            Assert.IsNotNull(tile);
            var tileCenter = new Point(tile.ActualWidth / 2, tile.ActualHeight / 2);

            // 真实输入管线（Win32 消息直驱，ClickRealAt 自带光标保存/投递/重试）。
            ClickRealAt(window, tile, tileCenter, requireGestureHandled: false);
            CompleteLayout(window);

            Assert.AreEqual(1, starter.Started.Count, "真实输入单击=一次有效启动请求。");
            Assert.AreEqual(app.Path, starter.Started[0].FileName);

            // 500ms 重入门闩：立即第二次真实点击不得重复派发。
            ClickRealAt(window, tile, tileCenter, requireGestureHandled: false);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(450));
            CompleteLayout(window);

            Assert.AreEqual(1, starter.Started.Count, "500ms 门闩内第二击不重复派发。");
            Assert.IsFalse(
                ((MainWindowViewModel)window.DataContext).IsPanelExpanded,
                "启动成功后经 250ms 排队收起（有效性重验通过）。");
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindowWithoutSaving(window);
        }
    }

    // ---- 排队收起有效性重验：更新的失败启动取消收起保展开 ----

    [STATestMethod]
    public void QuickLaunch_QueuedCollapse_NewerFailureCancelsCollapse()
    {
        using var directory = new TestDirectory();
        using var goodApp = new StubAppFile();
        using var badApp = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var good = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Application, goodApp.Path, "会成功", DateTimeOffset.UtcNow);
        var bad = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Application, badApp.Path, "会失败", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, good, bad);


        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            // 成功启动（收起排队中，动效关闭=收起同步几何转换，但排队收起走 250ms 定时器）。
            starter.Respond = info => info.FileName != badApp.Path;
            var goodTile = FindLauncherTile(window, good);
            Assert.IsNotNull(goodTile);
            goodTile.RaiseEvent(NewMouseButtonEventArgs(UIElement.MouseLeftButtonDownEvent, goodTile));
            goodTile.RaiseEvent(NewMouseButtonEventArgs(
                UIElement.MouseLeftButtonUpEvent,
                goodTile,
                timestamp: 5));
            CompleteLayout(window);
            Assert.AreEqual(1, starter.Started.Count);

            // 等待期内发生更新的失败启动：取消收起、保展开反馈。
            starter.Respond = _ => throw new Win32Exception(5);
            var badTile = FindLauncherTile(window, bad);
            Assert.IsNotNull(badTile);
            badTile.RaiseEvent(NewMouseButtonEventArgs(UIElement.MouseLeftButtonDownEvent, badTile));
            badTile.RaiseEvent(NewMouseButtonEventArgs(
                UIElement.MouseLeftButtonUpEvent,
                badTile,
                timestamp: 10));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(450));
            CompleteLayout(window);

            Assert.IsTrue(viewModel.IsPanelExpanded, "更新的失败启动取消在途收起，保展开反馈。");
            Assert.IsTrue(
                window.LaunchersForTests().Items.Single(entry => entry.Id == bad.Id).LaunchFailed,
                "失败瓦片进入灰态（会话标记，按 Id 取装载后的活动实例断言）。");
            StringAssert.StartsWith(viewModel.StatusText, "无法启动：拒绝访问");
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "quicklaunch-launch-failure.png",
                "FTS_QUICKLAUNCH_INTERACT_EVIDENCE_DIR");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    // ---- 启动失败：不删项、不显成功、不收起 ----

    [STATestMethod]
    public void QuickLaunch_LaunchFailure_KeepsItemOpenAndGrayWithoutSuccess()
    {
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter { Respond = _ => throw new Win32Exception(2) };
        var item = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Application, app.Path, "失败应用", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, item);


        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            var tile = FindLauncherTile(window, item);
            Assert.IsNotNull(tile);
            tile.RaiseEvent(NewMouseButtonEventArgs(UIElement.MouseLeftButtonDownEvent, tile));
            tile.RaiseEvent(NewMouseButtonEventArgs(UIElement.MouseLeftButtonUpEvent, tile, timestamp: 3));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(400));
            CompleteLayout(window);

            Assert.AreEqual(1, starter.Started.Count, "点击发起过一次真实请求。");
            Assert.AreEqual(1, window.LaunchersForTests().Items.Count, "失败不删项。");
            Assert.IsTrue(viewModel.IsPanelExpanded, "失败不收起。");
            Assert.IsTrue(
                window.LaunchersForTests().Items.Single(entry => entry.Id == item.Id).LaunchFailed,
                "失败灰态标记（按 Id 取装载后的活动实例断言）。");
            Assert.AreEqual($"无法启动：{AppLauncher.DescribeError(2)}", viewModel.StatusText);
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    // ---- 混合拖入分流：.exe/.lnk 导入、其余跳过并计数、状态条一次性汇报 ----

    [STATestMethod]
    public void QuickLaunch_MixedFileDropOnSurface_ImportsSupportedFilesOnly()
    {
        using var directory = new TestDirectory();
        var exePath = Path.Combine(directory.Root, "工具.exe");
        var lnkPath = Path.Combine(directory.Root, "快捷.lnk");
        var txtPath = Path.Combine(directory.Root, "说明.txt");
        File.WriteAllText(exePath, "stub");
        File.WriteAllText(lnkPath, "stub");
        File.WriteAllText(txtPath, "notes");
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var window = CreateLauncherWindowSync(directory, board, store, starter);


        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var host = (Border)window.FindName("LauncherContentHost");
            Assert.IsNotNull(host);
            var files = new StringCollection { exePath, txtPath, lnkPath };
            var data = new DataObject();
            data.SetFileDropList(files);

            host.RaiseEvent(NewDragEventArgs(data, DragDrop.DragEnterEvent, host));
            CompleteLayout(window);
            Assert.AreEqual(
                Visibility.Visible,
                ((TextBlock)window.FindName("LauncherDropHint")).Visibility,
                "放置态显示「松开以添加」。");

            var drop = NewDragEventArgs(data, DragDrop.DropEvent, host);
            host.RaiseEvent(drop);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            CompleteLayout(window);

            Assert.AreEqual(DragDropEffects.Copy, drop.Effects);
            var items = window.LaunchersForTests().Items;
            Assert.AreEqual(2, items.Count, "仅 .exe/.lnk 导入。");
            Assert.AreEqual("工具", items[0].DisplayName, "默认名=文件名去扩展。");
            Assert.AreEqual(
                "添加了 2 项，跳过 1 个不支持的文件。",
                ((MainWindowViewModel)window.DataContext).StatusText);
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "quicklaunch-added-tiles.png",
                "FTS_QUICKLAUNCH_INTERACT_EVIDENCE_DIR");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    // ---- 再展开抑制：解除条件唯一=指针离开收起前面板原区域后重新进入 ----

    [STATestMethod]
    public void QuickLaunch_ReexpandSuppression_HoldsUntilPointerLeavesThenReenters()
    {
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var item = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Application, app.Path, "应用", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, item);

        var savedCursor = SaveCursorPosition();

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            // 指针停留在面板内 → 启动成功收起（抑制窗激活）。
            var windowPosition = tileWindowCenter(window);
            SetCursorPos(windowPosition.X, windowPosition.Y);
            var tile = FindLauncherTile(window, item);
            Assert.IsNotNull(tile);
            tile.RaiseEvent(NewMouseButtonEventArgs(UIElement.MouseLeftButtonDownEvent, tile));
            tile.RaiseEvent(NewMouseButtonEventArgs(UIElement.MouseLeftButtonUpEvent, tile, timestamp: 3));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(400));
            CompleteLayout(window);

            Assert.IsFalse(viewModel.IsPanelExpanded, "启动成功后排队收起（启动收起不受指针在内抑制——那正是再展开抑制窗的存在理由）。");

            // 指针仍在收起前面板原区域内：悬停不展开（抑制窗）。
            SetCursorPos(windowPosition.X, windowPosition.Y);
            EnterCategory(window, QuickLaunchMigration.LauncherCategory);
            InvokePrivate(window, "ExpandIntentTimer_Tick", null, EventArgs.Empty);
            CompleteLayout(window);
            Assert.IsFalse(viewModel.IsPanelExpanded, "指针停留期间保持抑制。");

            // 指针离开原区域 → 抑制解除；再进入 → 悬停展开。
            SetCursorPos(40, 40);
            EnterCategory(window, QuickLaunchMigration.LauncherCategory);
            InvokePrivate(window, "ExpandIntentTimer_Tick", null, EventArgs.Empty);
            CompleteLayout(window);
            Assert.IsTrue(viewModel.IsPanelExpanded, "指针离开后重新进入即恢复悬停展开。");
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindowWithoutSaving(window);
        }
    }

    private static NativePoint tileWindowCenter(MainWindow window)
    {
        var position = window.PointToScreen(new Point(window.Width / 2, window.Height / 2));
        return new NativePoint((int)position.X, (int)position.Y);
    }

    // ---- 键盘查看态：Enter 启动、方向键导航、Delete 无操作 ----

    [STATestMethod]
    public void QuickLaunch_Keyboard_ViewModeEnterLaunchesFocusedTile()
    {
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        using var secondApp = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var first = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Application, app.Path, "甲", DateTimeOffset.UtcNow);
        var second = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Application, secondApp.Path, "乙", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, first, second);


        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            Keyboard.ClearFocus();
            var catalog = window.LaunchersForTests();
            Assert.AreEqual(2, catalog.Items.Count, "前置：启动项已装载。");
            var secondTile = FindLauncherTile(window, catalog.Items[1]);
            Assert.IsNotNull(secondTile, "前置：乙瓦片已生成。");

            // 右移聚焦 → Enter 启动（门闩外）。
            window.RaiseEvent(NewKeyEventArgs(window, Key.Right));
            window.RaiseEvent(NewKeyEventArgs(window, Key.Enter));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(60));

            Assert.AreEqual(1, starter.Started.Count, "键盘 Enter=启动聚焦瓦片（一次有效请求）。");

            // 查看态 Delete 无操作：不删除、已处理。
            window.RaiseEvent(NewKeyEventArgs(window, Key.Delete));
            CompleteLayout(window);

            Assert.AreEqual(2, window.LaunchersForTests().Items.Count, "查看态 Delete 无操作。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    // ---- 200 上限：窗口层状态条拒绝 ----

    [STATestMethod]
    public void QuickLaunch_DropBeyondLimit_ReportsTwoHundredCap()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var seeded = Enumerable.Range(0, LauncherCatalog.MaxItems)
            .Select(index => new LauncherItem(
                Guid.NewGuid(),
                LauncherKind.Application,
                Path.Combine(directory.Root, $"no{index}.exe"),
                $"应用{index}",
                DateTimeOffset.UtcNow))
            .ToArray();
        store.SaveAsync(new LauncherStoreData(LauncherStoreData.CurrentVersion, [.. seeded])).Wait();
        var extra = Path.Combine(directory.Root, "extra.exe");
        File.WriteAllText(extra, "stub");
        var starter = new RecordingLauncherStarter();
        var window = CreateLauncherWindowSync(directory, new BoardService(), store, starter, []);


        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var host = (Border)window.FindName("LauncherContentHost");
            var data = new DataObject();
            data.SetFileDropList(new StringCollection { extra });

            host!.RaiseEvent(NewDragEventArgs(data, DragDrop.DropEvent, host));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            CompleteLayout(window);

            Assert.AreEqual(LauncherCatalog.MaxItems, window.LaunchersForTests().Items.Count);
            Assert.AreEqual(
                $"启动项已达上限（{LauncherCatalog.MaxItems}）",
                ((MainWindowViewModel)window.DataContext).StatusText);
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    // ---- 两类入场是不同事件（§五 M1/M21）：新增落位仅真正新增；排序不触发 ----

    [STATestMethod]
    public void QuickLaunch_LandingAnimation_PlaysOnlyForTrulyNewTiles()
    {
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var first = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Application, app.Path, "甲", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, first);


        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = true;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();

            // 新增走窗口导入路径（落位动画属窗口编排，M21 仅真正新增）。
            var secondPath = Path.Combine(directory.Root, "乙.exe");
            File.WriteAllText(secondPath, "stub");
            var host = (Border)window.FindName("LauncherContentHost");
            var data = new DataObject();
            data.SetFileDropList(new System.Collections.Specialized.StringCollection { secondPath });
            host!.RaiseEvent(NewDragEventArgs(data, DragDrop.DropEvent, host));
            PumpUntilCondition(window.Dispatcher, () => catalog.Items.Count == 2);
            CompleteLayout(window);

            var added = catalog.Items.Single(entry => entry.TargetPath == secondPath);
            var addedTile = FindLauncherTile(window, added);
            Assert.IsNotNull(addedTile);
            Assert.IsTrue(
                addedTile.HasAnimatedProperties,
                "真正新增的瓦片播放落位动画（M21）。");

            // 重排：容器重建但任何瓦片都不播落位动画（不是新增事件）。
            var moveTask = catalog.MoveAsync(added.Id, 0);
            PumpDispatcherUntil(window.Dispatcher, moveTask);
            CompleteLayout(window);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(50));
            foreach (var entry in catalog.Items)
            {
                var tile = FindLauncherTile(window, entry);
                Assert.IsNotNull(tile);
                Assert.IsFalse(
                    tile.HasAnimatedProperties,
                    $"重排不触发落位动画（{entry.DisplayName}）。");
            }
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    // ---- 证据目录（§六.3）：浅/深主题 × 310/640 宽表面截图 ----

    [STATestMethod]
    public void QuickLaunch_Evidence_SurfaceBothThemesBothWidths()
    {
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var items = Enumerable.Range(0, 7)
            .Select(index => new LauncherItem(
                Guid.NewGuid(),
                LauncherKind.Application,
                $"{app.Path}.{index}",
                $"示例应用{index}",
                DateTimeOffset.UtcNow))
            .ToArray();
        var window = CreateLauncherWindowSync(directory, board, store, starter, items);


        try
        {
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            window.Show();
            foreach (var (theme, themeName) in new[] { (DesignTheme.Light, "light"), (DesignTheme.Dark, "dark") })
            {
                foreach (var (width, widthName) in new[]
                         {
                             (WindowSettings.MinPanelWidth, "310"),
                             (WindowSettings.MaxPanelWidth, "640")
                         })
                {
                    ApplySettingsWidth(window, width);
                    DesignThemeManager.Apply(window, theme);
                    ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
                    CompleteLayout(window);
                    SaveVisualEvidence(
                        (Border)window.FindName("WindowShell"),
                        $"quicklaunch-surface-{themeName}-{widthName}.png",
                        "FTS_QUICKLAUNCH_SURFACE_EVIDENCE_DIR");
                }
            }

            DesignThemeManager.Apply(window, DesignTheme.Light);
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    // ---- README 文档截图（docs/images）：真实系统应用 + 真实 Shell 图标，
    // 经 FTS_QUICKLAUNCH_DOCS_DIR 门控本地生成后拷入仓库（与 hero/panel-anatomy 同为真实渲染管线）。 ----

    [STATestMethod]
    public void QuickLaunch_DocsScreenshots_SurfaceAndEditMode()
    {
        var outputDirectory = Environment.GetEnvironmentVariable("FTS_QUICKLAUNCH_DOCS_DIR");
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            Assert.Inconclusive("设置 FTS_QUICKLAUNCH_DOCS_DIR 后本地生成 README 截图。");
            return;
        }

        Directory.CreateDirectory(outputDirectory);
        using var directory = new TestDirectory();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var realApps = new[]
        {
            (Path: @"C:\Windows\System32\notepad.exe", Name: "记事本"),
            (Path: @"C:\Windows\System32\calc.exe", Name: "计算器"),
            (Path: @"C:\Windows\explorer.exe", Name: "文件资源管理器"),
            (Path: @"C:\Windows\System32\cmd.exe", Name: "命令提示符"),
            (Path: @"C:\Windows\System32\control.exe", Name: "控制面板"),
            (Path: @"C:\Program Files\Internet Explorer\iexplore.exe", Name: "Internet Explorer"),
            (Path: @"C:\Windows\System32\taskmgr.exe", Name: "任务管理器")
        };
        var items = realApps
            .Where(app => File.Exists(app.Path))
            .Select(app => new LauncherItem(
                Guid.NewGuid(),
                LauncherKind.Application,
                app.Path,
                app.Name,
                DateTimeOffset.UtcNow))
            .ToArray();
        var window = CreateLauncherWindowSync(directory, board, store, starter, items);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            Assert.AreEqual(items.Length, window.LaunchersForTests().Items.Count, "前置：启动项已装载。");
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);

            // 等待真实图标回填（内存缓存命中后瓦片 Image.Source 非空）。
            PumpUntilCondition(
                window.Dispatcher,
                () =>
                {
                    CompleteLayout(window);
                    return window.LaunchersForTests().Items.All(item =>
                        FindLauncherTile(window, item) is { } tile &&
                        FindDescendant<Image>(tile)?.Source is not null);
                });
            CompleteLayout(window);

            SaveWindowShot(window, Path.Combine(outputDirectory, "quicklaunch-surface-light.png"));
            DesignThemeManager.Apply(window, DesignTheme.Dark);
            CompleteLayout(window);
            SaveWindowShot(window, Path.Combine(outputDirectory, "quicklaunch-surface-dark.png"));
            DesignThemeManager.Apply(window, DesignTheme.Light);

            // 编辑模式 + 撤销提示条：删除一个瓦片后截图。
            Assert.IsGreaterThan(1, window.LaunchersForTests().Items.Count, "前置：瓦片充足。");
            InvokePrivate(window, "EnterLauncherEditMode");
            var victim = window.LaunchersForTests().Items[1];
            ClickLauncherDeleteButtonForDocs(window, victim);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            CompleteLayout(window);
            SaveWindowShot(window, Path.Combine(outputDirectory, "quicklaunch-edit-mode.png"));

            // ＋ 两入口菜单（切片 4）：Popup 渲染在独立视觉树（PopupRoot），
            // 截图取菜单面板本体（Popup.Child）。
            InvokePrivate(window, "ExitLauncherEditMode");
            CompleteLayout(window);
            window.LauncherAddButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, window.LauncherAddButton));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
            CompleteLayout(window);
            SaveVisualShot((FrameworkElement)window.LauncherAddMenu.Child, Path.Combine(outputDirectory, "quicklaunch-add-menu.png"));
            window.LauncherAddMenu.IsOpen = false;

            // 已安装应用选择器（切片 4）：真实枚举前 12 项，非模态显示后截图（勾选两行）。
            var installed = InstalledAppsEnumerator.Enumerate().Take(12).ToList();
            if (installed.Count > 0)
            {
                window.UseInstalledAppsSourceForTests(() => installed);
                window._showPickerDialog = picker =>
                {
                    picker.Show();
                    picker.Resources[SystemParameters.ClientAreaAnimationKey] = false;
                    CompleteLayout(picker);
                    for (var i = 0; i < Math.Min(2, picker.PickerList.Items.Count); i++)
                    {
                        CheckPickerRowForDocs(picker, i);
                    }

                    CompleteLayout(picker);
                    SaveVisualShot(
                        (FrameworkElement)picker.Content,
                        Path.Combine(outputDirectory, "quicklaunch-picker.png"));
                    picker.Close();
                    return false;
                };
                window.LauncherAddInstalledMenuButton.RaiseEvent(new RoutedEventArgs(
                    Button.ClickEvent, window.LauncherAddInstalledMenuButton));
                CompleteLayout(window);
            }
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    private static void CheckPickerRowForDocs(InstalledAppPickerWindow picker, int index)
    {
        var container = picker.PickerList.ItemContainerGenerator.ContainerFromIndex(index);
        var checkBox = FindDescendant<CheckBox>(container);
        Assert.IsNotNull(checkBox);
        checkBox.IsChecked = true;
    }

    private static void SaveVisualShot(FrameworkElement element, string filePath)
    {
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(element.ActualWidth),
            (int)Math.Ceiling(element.ActualHeight),
            96,
            96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(filePath);
        encoder.Save(stream);
    }

    private static void ClickLauncherDeleteButtonForDocs(MainWindow window, LauncherItem item)
    {
        var tile = FindLauncherTile(window, item);
        Assert.IsNotNull(tile);
        var button = FindDescendant<Button>(tile);
        Assert.IsNotNull(button);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }

    private static void SaveWindowShot(MainWindow window, string filePath)
    {
        var shell = (Border)window.FindName("WindowShell")!;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(shell.ActualWidth),
            (int)Math.Ceiling(shell.ActualHeight),
            96,
            96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(shell);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(filePath);
        encoder.Save(stream);
    }

    private static void ApplySettingsWidth(MainWindow window, double panelWidth)
    {
        // 表面宽度证据（§六.3）：直接设展开几何宽度（面板宽 + 标签轨 58），
        // 与 WindowController.Expanded 的宽度口径一致。
        window.Width = panelWidth + WindowSettings.TabWidth;
        CompleteLayout(window);
    }

    // ---- 证据目录（§六.3）：迁移前后 ----

    [STATestMethod]
    public void QuickLaunch_Evidence_MigrationBeforeAndAfter()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("存量提示词一", BoardCategory.Prompt);
        board.AddText("存量提示词二", BoardCategory.Prompt);
        board.AddText("待分类原有内容");
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        // 旧默认名快照（升级用户形态）：版本戳 0、Prompt 名仍为「文本」。
        var settings = WindowSettings.Default
            .WithCategoryName(BoardCategory.Prompt, "文本") with
        {
            ReviewMigrationVersion = DailyReviewMigration.CurrentVersion,
            CategoryNameMigrationVersion = CategoryNameMigration.CurrentVersion
        };
        var window = CreateWindow(
            board,
            store,
            settings,
            null,
            new ImageNormalizer(store.ImagesDirectory),
            launcherStore: store,
            appLauncher: new AppLauncher(starter.Invoke));

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "quicklaunch-migration-before.png",
                "FTS_QUICKLAUNCH_MIGRATION_EVIDENCE_DIR");

            // 走生产迁移链（板面装载门内同款）：存量卡收编 Inbox + 名称归位「启动」+ 戳。
            var outcome = new QuickLaunchMigration(store, store)
                .EnsureAsync(board, settings, AppPreferences.Default).GetAwaiter().GetResult();
            window.AdoptMigratedSettings(outcome.Settings);
            CompleteLayout(window);
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "quicklaunch-migration-after.png",
                "FTS_QUICKLAUNCH_MIGRATION_EVIDENCE_DIR");

            Assert.AreEqual("启动", outcome.Settings.CategoryName(BoardCategory.Prompt));
            Assert.AreEqual(0, board.Items(BoardCategory.Prompt).Count, "迁移后 Prompt 桶清空。");
            Assert.AreEqual(3, board.Items(BoardCategory.Inbox).Count, "收编守恒（存量 2 + 待分类 1）。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    // ---- 证据目录（§六.3）：调序后标签轨与收起把手 ----

    [STATestMethod]
    public void QuickLaunch_Evidence_CustomOrderRailAndCollapsedHandle()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var customOrder = new[]
        {
            BoardCategory.Inbox,
            BoardCategory.CustomerOriginal,
            BoardCategory.Prompt,
            BoardCategory.Reference
        };
        var settings = WindowSettings.Default
            .WithCategoryOrder(customOrder) with
        {
            ReviewMigrationVersion = DailyReviewMigration.CurrentVersion,
            CategoryNameMigrationVersion = CategoryNameMigration.CurrentVersion
        };
        var settings2 = settings.WithCategoryName(BoardCategory.Prompt, "启动");
        var window = CreateWindow(
            board,
            store,
            settings2,
            null,
            new ImageNormalizer(store.ImagesDirectory),
            launcherStore: store,
            appLauncher: new AppLauncher(starter.Invoke));

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            CollectionAssert.AreEqual(
                customOrder,
                viewModel.Categories.Select(category => category.Category).ToArray(),
                "前置：标签轨按自定义序渲染。");
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "quicklaunch-order-rail.png",
                "FTS_QUICKLAUNCH_ORDER_EVIDENCE_DIR");

            WithClientAreaAnimationsDisabled(
                window,
                () =>
                {
                    InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
                    InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
                    CompleteLayout(window);
                });
            CompleteLayout(window);
            var handle = (ContentControl)window.FindName("CollapsedCategoryHandle");
            Assert.IsNotNull(handle);
            Assert.AreEqual(BoardCategory.Inbox, viewModel.DefaultCapturePanel!.Category);
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "quicklaunch-order-collapsed-handle.png",
                "FTS_QUICKLAUNCH_ORDER_EVIDENCE_DIR");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    // ---- 覆盖交互收起抑制（#30 回归修复，Issue #162 双 bug）----
    // ＋菜单与模态对话框是独立 HWND：指针移入即触发主窗口假 MouseLeave，
    // 收起链（250ms 到点重验）必须被「覆盖交互 hold」抑制，全部释放后按指针
    // 几何位置重估（在内维持展开/不在恢复收起节奏）。

    [STATestMethod]
    public void QuickLaunch_OverlaySuppression_MenuOpenSurvivesPointerLeaveAndCollapseTick()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var window = CreateLauncherWindowSync(directory, board, store, starter);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            // 真实路径打开＋菜单（工具行＋），随后指针移向菜单=假 MouseLeave+250ms 到点。
            window.LauncherAddButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent, window.LauncherAddButton));
            CompleteLayout(window);
            Assert.IsTrue(window.LauncherAddMenu.IsOpen, "前置：＋菜单已打开。");

            WithClientAreaAnimationsDisabled(
                window,
                () =>
                {
                    InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
                    InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
                    CompleteLayout(window);
                });

            Assert.IsTrue(window.LauncherAddMenu.IsOpen, "菜单打开期间收起链不得关闭菜单。");
            Assert.IsTrue(viewModel.IsPanelExpanded, "菜单打开期间鼠标离开收起链不得收回面板。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    // ---- 瓦片网格宽度输入（#30 回归修复）：宽度只来自容器视口，禁自引用回写 ----
    // 首次带数据布局发生在窄轨（58 DIP 收起几何）时，网格不得把自身渲染宽钉死成
    // 本地 Width；展开至宽视口后必须按视口恢复列槽宽度并保持瓦片可见。

    [STATestMethod]
    public void QuickLaunch_GridWidth_FirstLayoutAtNarrowTrackRecoversAfterExpand()
    {
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var window = CreateLauncherWindowSync(directory, board, store, starter);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);

            // 收起（真实路径）→ 数据在收起态到达（窄轨首布局，Width 被钉死 0=用户
            // 报告的「添加后一片空白」）→ 再展开必须恢复。
            CollapseForSetup(window);
            InvokePrivateTask(window, "ImportLauncherFilesAsync", new[] { app.Path }, 0);
            CompleteLayout(window);

            // 再展开（真实路径：悬停意图提交→宽几何）：网格必须按视口恢复列槽宽度。
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);

            Assert.IsTrue(
                window.LauncherTileGrid.ActualWidth >= 3 * 92 - 8,
                $"展开至宽视口后瓦片网格宽度必须恢复（实际 {window.LauncherTileGrid.ActualWidth}）。");
            Assert.AreEqual(1, window.LaunchersForTests().Items.Count);
            Assert.IsNotNull(FindLauncherTile(window, window.LaunchersForTests().Items[0]), "瓦片容器已生成。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_OverlaySuppression_DialogOpenSurvivesLeaveAndResumesAfterCloseOutside()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var window = CreateLauncherWindowSync(directory, board, store, starter);
        window.UseInstalledAppsSourceForTests(() => [FakeInstalledApp("app.new1", "新应用一")]);

        var savedCursor = SaveCursorPosition();

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            SetCursorPos(40, 40);
            window._showPickerDialog = picker =>
            {
                // 模态对话框打开期间（指针已离开主窗口）：假 MouseLeave+250ms 到点。
                picker.Show();
                WithClientAreaAnimationsDisabled(
                    window,
                    () =>
                    {
                        InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
                        InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
                        CompleteLayout(window);
                    });
                Assert.IsTrue(
                    viewModel.IsPanelExpanded,
                    "模态对话框打开期间鼠标离开收起链不得收回面板。");
                picker.Close();
                return false;
            };

            window.LauncherAddInstalledMenuButton.RaiseEvent(new RoutedEventArgs(
                Button.ClickEvent, window.LauncherAddInstalledMenuButton));

            // 对话框已关闭且指针在窗口外：释放重估恢复收起节奏（250ms 到点提交）。
            Assert.IsTrue(viewModel.IsPanelExpanded, "对话框刚关闭、收起计时未到期前保持展开。");
            WithClientAreaAnimationsDisabled(
                window,
                () =>
                {
                    InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
                    CompleteLayout(window);
                });
            Assert.IsFalse(viewModel.IsPanelExpanded, "指针在外、覆盖交互全部结束→按既有节奏收起。");
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_OverlaySuppression_MenuToDialogHandoffKeepsPanelOpen()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var window = CreateLauncherWindowSync(directory, board, store, starter);
        window.UseInstalledAppsSourceForTests(() => [FakeInstalledApp("app.new1", "新应用一")]);

        var savedCursor = SaveCursorPosition();

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            SetCursorPos(40, 40);

            // ＋ 打开菜单（Menu hold），经菜单入口交接给选择器对话框（ModalDialog hold）。
            window.LauncherAddButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent, window.LauncherAddButton));
            CompleteLayout(window);
            Assert.IsTrue(window.LauncherAddMenu.IsOpen);
            window._showPickerDialog = picker =>
            {
                picker.Show();
                WithClientAreaAnimationsDisabled(
                    window,
                    () =>
                    {
                        InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
                        InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
                        CompleteLayout(window);
                    });
                Assert.IsTrue(
                    viewModel.IsPanelExpanded,
                    "菜单→对话框交接期间（菜单已关、对话框已开）收起链不得收回面板。");
                picker.Close();
                return false;
            };

            window.LauncherAddInstalledMenuButton.RaiseEvent(new RoutedEventArgs(
                Button.ClickEvent, window.LauncherAddInstalledMenuButton));

            Assert.IsFalse(window.LauncherAddMenu.IsOpen, "入口点击后菜单关闭（既有行为不变）。");
            Assert.IsTrue(viewModel.IsPanelExpanded, "交接全程保持展开。");
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_OverlaySuppression_DialogCloseWithPointerInsideKeepsPanelExpanded()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var window = CreateLauncherWindowSync(directory, board, store, starter);
        window.UseInstalledAppsSourceForTests(() => [FakeInstalledApp("app.new1", "新应用一")]);

        var savedCursor = SaveCursorPosition();

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var inside = tileWindowCenter(window);
            SetCursorPos(inside.X, inside.Y);
            window._showPickerDialog = picker =>
            {
                picker.Show();
                WithClientAreaAnimationsDisabled(
                    window,
                    () =>
                    {
                        InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
                        InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
                        CompleteLayout(window);
                    });
                picker.Close();
                return false;
            };

            window.LauncherAddInstalledMenuButton.RaiseEvent(new RoutedEventArgs(
                Button.ClickEvent, window.LauncherAddInstalledMenuButton));

            // 对话框已关闭且指针在窗口内：释放重估维持展开，不得重启收起。
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(450));
            CompleteLayout(window);
            Assert.IsTrue(viewModel.IsPanelExpanded, "指针在内→覆盖交互释放后维持展开。");
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_OverlaySuppression_MenuCloseWithPointerOutsideResumesCollapse()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var window = CreateLauncherWindowSync(directory, board, store, starter);

        var savedCursor = SaveCursorPosition();

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            SetCursorPos(40, 40);

            window.LauncherAddButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent, window.LauncherAddButton));
            CompleteLayout(window);
            Assert.IsTrue(window.LauncherAddMenu.IsOpen);

            // 菜单外点关闭（StaysOpen=False 的正常关闭路径）且指针在窗口外：
            // 释放重估恢复收起节奏。
            window.LauncherAddMenu.IsOpen = false;
            WithClientAreaAnimationsDisabled(
                window,
                () =>
                {
                    InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
                    CompleteLayout(window);
                });
            Assert.IsFalse(viewModel.IsPanelExpanded, "菜单外点关闭且指针在外→恢复收起节奏。");
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_OverlaySuppression_CollapsedTailMenuCloseReentryStaysCollapsed()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var window = CreateLauncherWindowSync(directory, board, store, starter);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            // 先真实收起，再在收起态开/关菜单（收起尾段关菜单触发的 Closed 再入）：
            // 释放重估不得反向展开、不得重启收起计时。
            CollapseForSetup(window);
            window.LauncherAddButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent, window.LauncherAddButton));
            CompleteLayout(window);
            Assert.IsTrue(window.LauncherAddMenu.IsOpen, "前置：收起态菜单可打开。");

            window.LauncherAddMenu.IsOpen = false;
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(450));
            CompleteLayout(window);

            Assert.IsFalse(window.LauncherAddMenu.IsOpen);
            Assert.IsFalse(viewModel.IsPanelExpanded, "Closed 再入路径天然幂等：不得反向展开。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_OverlaySuppression_EdgeHideArmedWithMenuOpenTickCommitsNothing()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var window = CreateLauncherWindowSync(directory, board, store, starter);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            ToggleEdgeHide(window);
            Assert.IsTrue(EdgeHideState(window).IsArmed, "前置：贴边隐藏已待命。");
            window.LauncherAddButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent, window.LauncherAddButton));
            CompleteLayout(window);
            Assert.IsTrue(window.LauncherAddMenu.IsOpen);

            WithClientAreaAnimationsDisabled(
                window,
                () =>
                {
                    InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
                    InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
                    CompleteLayout(window);
                });

            Assert.IsFalse(EdgeHideState(window).IsDocked, "菜单打开期间 tick 不得提交贴边隐藏。");
            Assert.IsTrue(viewModel.IsPanelExpanded, "菜单打开期间面板保持展开。");
            Assert.IsTrue(window.LauncherAddMenu.IsOpen);
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_OverlaySuppression_QueuedLaunchCollapseCancelsWhenOverlayActive()
    {
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var item = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Application, app.Path, "应用", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, item);

        var savedCursor = SaveCursorPosition();

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            // 启动成功排队收起（250ms）→ 等待期内打开＋菜单 → 到期重验取消收起。
            var tile = FindLauncherTile(window, item);
            Assert.IsNotNull(tile);
            tile.RaiseEvent(NewMouseButtonEventArgs(UIElement.MouseLeftButtonDownEvent, tile));
            tile.RaiseEvent(NewMouseButtonEventArgs(UIElement.MouseLeftButtonUpEvent, tile, timestamp: 3));
            window.LauncherAddButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent, window.LauncherAddButton));
            CompleteLayout(window);
            Assert.IsTrue(window.LauncherAddMenu.IsOpen);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(450));
            CompleteLayout(window);

            Assert.IsTrue(viewModel.IsPanelExpanded, "启动排队收起遇覆盖交互（＋菜单）必须取消。");
            Assert.IsTrue(window.LauncherAddMenu.IsOpen);

            // 菜单关闭且指针在外 → 恢复收起节奏。
            SetCursorPos(40, 40);
            window.LauncherAddMenu.IsOpen = false;
            WithClientAreaAnimationsDisabled(
                window,
                () =>
                {
                    InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
                    CompleteLayout(window);
                });
            Assert.IsFalse(viewModel.IsPanelExpanded);
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_OverlaySuppression_PickerAddFlowKeepsPanelOpenUntilTilesVisible()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var window = CreateLauncherWindowSync(directory, board, store, starter);
        window.UseInstalledAppsSourceForTests(() => [FakeInstalledApp("app.new1", "新应用一")]);

        var savedCursor = SaveCursorPosition();

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            SetCursorPos(40, 40);

            // 用户报告时序（Issue #162）：＋ 菜单 → 已安装应用… → 对话框期间指针在外。
            window.LauncherAddButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent, window.LauncherAddButton));
            CompleteLayout(window);
            Assert.IsTrue(window.LauncherAddMenu.IsOpen);
            window._showPickerDialog = picker =>
            {
                picker.Show();
                picker.Resources[SystemParameters.ClientAreaAnimationKey] = false;
                WithClientAreaAnimationsDisabled(
                    window,
                    () =>
                    {
                        InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
                        InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
                        CompleteLayout(window);
                    });
                Assert.IsTrue(viewModel.IsPanelExpanded, "对话框期间面板不得收回（bug ①）。");
                for (var i = 0; i < picker.PickerList.Items.Count; i++)
                {
                    CheckPickerRow(picker, i);
                }

                ClickPickerConfirm(picker);
                return true;
            };

            window.LauncherAddInstalledMenuButton.RaiseEvent(new RoutedEventArgs(
                Button.ClickEvent, window.LauncherAddInstalledMenuButton));
            PumpUntilCondition(
                window.Dispatcher,
                () => window.LaunchersForTests().Items.Count == 1);
            CompleteLayout(window);
            Assert.AreEqual(1, window.LaunchersForTests().Items.Count, "确认后导入 1 项。");

            // 对话框已关、指针在外：按既有节奏收起（非 bug，是正常离开收起）。
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(450));
            CompleteLayout(window);
            Assert.IsFalse(viewModel.IsPanelExpanded, "覆盖交互结束后指针在外→按节奏收起。");

            // 再展开：瓦片可见、网格宽度按视口恢复（bug ②不得复发）。
            SetCursorPos(40, 40);
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            Assert.IsTrue(window.LauncherTileGrid.ActualWidth >= 3 * 92 - 8,
                $"再展开后网格宽度按视口恢复（实际 {window.LauncherTileGrid.ActualWidth}）。");
            Assert.IsNotNull(
                FindLauncherTile(window, window.LaunchersForTests().Items[0]),
                "再展开后瓦片可见。");
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_GridWidth_EmptyToDataSwitchAtWideViewportLaysOutColumns()
    {
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var window = CreateLauncherWindowSync(directory, board, store, starter);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            Assert.AreEqual(0, window.LaunchersForTests().Items.Count, "前置：空态。");

            InvokePrivateTask(window, "ImportLauncherFilesAsync", new[] { app.Path }, 0);
            CompleteLayout(window);

            Assert.AreEqual(1, window.LaunchersForTests().Items.Count);
            Assert.IsTrue(window.LauncherTileGrid.ActualWidth >= 3 * 92 - 8,
                $"空态→带数据切换后网格按视口布局（实际 {window.LauncherTileGrid.ActualWidth}）。");
            Assert.IsNotNull(FindLauncherTile(window, window.LaunchersForTests().Items[0]));
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_GridWidth_CollapseReexpandCycleKeepsTilesVisible()
    {
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var item = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Application, app.Path, "应用", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, item);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);

            CollapseForSetup(window);
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);

            Assert.IsTrue(window.LauncherTileGrid.ActualWidth >= 3 * 92 - 8,
                $"收起→再展开循环后网格宽度按视口恢复（实际 {window.LauncherTileGrid.ActualWidth}）。");
            Assert.IsNotNull(FindLauncherTile(window, item), "收起→再展开循环后瓦片可见。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_GridWidth_LaunchCollapseReexpandKeepsTilesVisible()
    {
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var item = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Application, app.Path, "应用", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, item);

        var savedCursor = SaveCursorPosition();

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            var tile = FindLauncherTile(window, item);
            Assert.IsNotNull(tile);
            tile.RaiseEvent(NewMouseButtonEventArgs(UIElement.MouseLeftButtonDownEvent, tile));
            tile.RaiseEvent(NewMouseButtonEventArgs(UIElement.MouseLeftButtonUpEvent, tile, timestamp: 3));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(450));
            CompleteLayout(window);
            Assert.IsFalse(viewModel.IsPanelExpanded, "前置：启动成功后排队收起。");

            SetCursorPos(40, 40);
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);

            Assert.IsTrue(window.LauncherTileGrid.ActualWidth >= 3 * 92 - 8,
                $"启动→收起→再展开后网格宽度按视口恢复（实际 {window.LauncherTileGrid.ActualWidth}）。");
            Assert.IsNotNull(FindLauncherTile(window, item), "启动→收起→再展开后瓦片可见。");
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindowWithoutSaving(window);
        }
    }
}
