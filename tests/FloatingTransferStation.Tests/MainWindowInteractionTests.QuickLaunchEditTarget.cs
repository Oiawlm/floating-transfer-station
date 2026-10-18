using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 编辑目标全旅程＋对话框壳结构断言（#30 修订 2026-10-18，Issue #186 D7/D8）：
/// 右键菜单/Alt+Enter 入口、Url 归一化确认、Application 路径+高级区、PackagedApp
/// 预选、重复拒绝、目标持久化与用户名保留；两弹窗＋编辑目标对话框壳=不透明
/// DialogSurfaceBrush＋无 DropShadowEffect＋无 Margin=6 透明圈＋DWM 接入。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public void QuickLaunch_EditTarget_UrlJourneyUpdatesTargetAndKeepsName()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var item = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Url, "https://old.example.com/", "我的站点", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, item);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            Keyboard.ClearFocus();
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();
            var live = catalog.Items.Single();

            // 注入编辑目标对话框（非模态驱动并自动确认）。
            window._showEditTargetDialog = dialog =>
            {
                Assert.AreEqual("编辑网页地址", ((TextBlock)dialog.FindName("DialogTitle")!).Text);
                Assert.AreEqual("https://old.example.com/", ((TextBox)dialog.FindName("TargetInputBox")!).Text);
                ((TextBox)dialog.FindName("TargetInputBox")!).Text = "github.com";
                dialog.SaveButton_ClickForTests();
                return true;
            };

            window.RaiseEvent(NewKeyEventArgs(window, Key.Left));
            window.RaiseEvent(NewModifiedKeyEventArgs(window, Key.Enter, ModifierKeys.Alt));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(400));
            CompleteLayout(window);

            Assert.AreEqual("https://github.com/", live.TargetPath, "归一化规范形落模型。");
            Assert.AreEqual("我的站点", live.DisplayName, "用户名保留。");
            var persisted = store.LoadAsync().GetAwaiter().GetResult();
            Assert.AreEqual("https://github.com/", persisted.Items.Single().TargetPath, "目标持久化。");
            Assert.AreEqual("目标已更新。", ((MainWindowViewModel)window.DataContext).StatusText, "状态条确认。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_EditTarget_UrlDuplicateShowsStatusWithoutMutation()
    {
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var url = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Url, "https://a.example.com/", "站点A", DateTimeOffset.UtcNow);
        var dup = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Url, "https://b.example.com/", "站点B", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, url, dup);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            Keyboard.ClearFocus();
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();

            // 把 B 的目标改为 A 的规范形：UpdateTargetAsync 判定重复、状态条提示、
            // 双方目标都不变。
            window._showEditTargetDialog = dialog =>
            {
                ((TextBox)dialog.FindName("TargetInputBox")!).Text = "https://a.example.com/";
                dialog.SaveButton_ClickForTests();
                return true;
            };
            window.RaiseEvent(NewKeyEventArgs(window, Key.Right));
            window.RaiseEvent(NewModifiedKeyEventArgs(window, Key.Enter, ModifierKeys.Alt));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(600));
            CompleteLayout(window);

            Assert.AreEqual("https://a.example.com/", catalog.Items[0].TargetPath, "被指目标不变。");
            Assert.AreEqual("https://b.example.com/", catalog.Items[1].TargetPath, "拒绝不突变。");
            Assert.AreEqual("已在启动列表中", ((MainWindowViewModel)window.DataContext).StatusText, "重复=状态条提示。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_EditTarget_ApplicationAdvancedSectionAndArgs()
    {
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        using var secondApp = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var item = new LauncherItem(
            Guid.NewGuid(),
            LauncherKind.Application,
            app.Path,
            "应用",
            DateTimeOffset.UtcNow,
            arguments: "/old",
            workingDirectory: @"C:\Old");
        var window = CreateLauncherWindowSync(directory, board, store, starter, item);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            Keyboard.ClearFocus();
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();

            window._showEditTargetDialog = dialog =>
            {
                Assert.AreEqual("编辑应用目标", ((TextBlock)dialog.FindName("DialogTitle")!).Text);
                Assert.AreEqual(Visibility.Visible, ((Button)dialog.FindName("BrowseButton")!).Visibility, "Application=浏览…可见。");
                Assert.AreEqual(Visibility.Visible, ((StackPanel)dialog.FindName("AdvancedSection")!).Visibility, "高级区可见。");
                ((TextBox)dialog.FindName("TargetInputBox")!).Text = secondApp.Path;
                ((TextBox)dialog.FindName("ArgumentsBox")!).Text = "/new /flag";
                ((TextBox)dialog.FindName("WorkingDirectoryBox")!).Text = @"C:\New";
                dialog.SaveButton_ClickForTests();
                return true;
            };
            window.RaiseEvent(NewKeyEventArgs(window, Key.Left));
            window.RaiseEvent(NewModifiedKeyEventArgs(window, Key.Enter, ModifierKeys.Alt));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(400));
            CompleteLayout(window);

            var live = catalog.Items.Single();
            Assert.AreEqual(secondApp.Path, live.TargetPath, "目标路径更新。");
            Assert.AreEqual("/new /flag", live.Arguments, "参数更新。");
            Assert.AreEqual(@"C:\New", live.WorkingDirectory, "起始位置更新。");
            Assert.IsNull(live.IconStamp, "目标变更→IconStamp 置空→图标自动重取。");
            Assert.AreEqual("应用", live.DisplayName, "用户名保留。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_EditTarget_ContextMenuEntryOpensDialog()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var item = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Url, "https://example.com/", "示例", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, item);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();
            var tile = FindLauncherTile(window, catalog.Items[0]);
            Assert.IsNotNull(tile);
            var menu = tile.ContextMenu;
            Assert.IsNotNull(menu);

            // 终序断言（#30 修订）：打开/重命名/编辑目标…/上移/下移/分隔/删除。
            var headers = menu.Items.OfType<MenuItem>().Select(entry => (string)entry.Header).ToArray();
            CollectionAssert.AreEqual(
                new[] { "打开", "重命名", "编辑目标…", "上移", "下移", "删除" },
                headers,
                "右键菜单终序。");
            Assert.AreEqual(7, menu.Items.Count, "删除前有分组分隔线（Separator 计入 Items）。");

            var editTargetOpened = false;
            window._showEditTargetDialog = dialog =>
            {
                editTargetOpened = true;
                return false; // 取消路径：零突变。
            };
            var editMenuItem = menu.Items.OfType<MenuItem>().Single(entry => (string)entry.Header == "编辑目标…");
            menu.PlacementTarget = tile; // 处理器经 PlacementTarget 反查条目（真实打开时由 WPF 置位）。
            editMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, editMenuItem));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));

            Assert.IsTrue(editTargetOpened, "菜单「编辑目标…」打开对话框。");
            Assert.AreEqual(0, window.LaunchersForTests().Items.Count(entry => entry.TargetPath != "https://example.com/"), "取消路径零突变。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_DialogShells_StructureAssertions()
    {
        // D8 结构断言（契约修订锁定面）：不透明 DialogSurfaceBrush、壳样式 DialogShell
        // Style 引用、WindowChrome GlassFrameThickness=0、代码侧 DWM 接入成功；
        // grep 兜底（DropShadowEffect/透明圈 Margin="6" 零命中）在 P5 质量门执行。
        var urlDialog = new LauncherUrlInputWindow(DesignTheme.Light);
        var editDialog = new LauncherEditTargetWindow(
            DesignTheme.Light,
            new LauncherItem(Guid.NewGuid(), LauncherKind.Url, "https://x.example.com/", "x", DateTimeOffset.UtcNow));

        try
        {
            foreach (var dialog in new Window[] { urlDialog, editDialog })
            {
                Assert.AreEqual(
                    ((System.Windows.Media.SolidColorBrush)dialog.FindResource("DialogSurfaceBrush")).Color,
                    ((System.Windows.Media.SolidColorBrush)dialog.Background).Color,
                    $"{dialog.GetType().Name}: 窗背景=不透明 DialogSurfaceBrush。");
                var shell = (Border)dialog.Content;
                Assert.AreSame(dialog.FindResource("DialogShellStyle"), shell.Style, $"{dialog.GetType().Name}: 壳=DialogShellStyle 资源。");
                Assert.IsNull(shell.Effect, $"{dialog.GetType().Name}: 壳无手工阴影（DWM 唯一来源）。");
                Assert.AreEqual(new CornerRadius(8), shell.CornerRadius, $"{dialog.GetType().Name}: 壳圆角=表面级 8。");
                Assert.AreEqual(24d, shell.Padding.Top, $"{dialog.GetType().Name}: 壳内边距 24。");
            }
        }
        finally
        {
            urlDialog.Close();
            editDialog.Close();
        }
    }

    [STATestMethod]
    public void QuickLaunch_FailedTile_ToolTipShowsTargetAndContextMenuAvailable()
    {
        // D10：失败瓦片保持可修复入口——右键菜单可用（灰态不挡）、名称 ToolTip 含
        // 目标（域名/路径）。
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter { Respond = _ => false };
        var item = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Application, app.Path, "坏应用", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, item);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();
            var live = catalog.Items.Single();
            var tile = FindLauncherTile(window, live);
            Assert.IsNotNull(tile);

            // 单击启动（失败路径）：灰态置位。
            tile.RaiseEvent(NewMouseButtonEventArgs(UIElement.MouseLeftButtonDownEvent, tile, timestamp: 300));
            tile.RaiseEvent(NewMouseButtonEventArgs(UIElement.MouseLeftButtonUpEvent, tile, timestamp: 310));
            CompleteLayout(window);
            Assert.IsTrue(live.LaunchFailed, "前置：失败灰态置位。");

            // 右键菜单仍可用（灰态不挡右键菜单=可修复入口）。
            var menu = tile.ContextMenu;
            Assert.IsNotNull(menu, "失败瓦片右键菜单可用。");
            menu.PlacementTarget = tile;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(150));
            Assert.IsTrue(menu.IsOpen, "灰态菜单可打开（只读停用才整体抑制）。");
            menu.IsOpen = false;

            // 名称 ToolTip=「名称＋目标」（D10 名称辨认）。
            var nameText = FindDescendants<TextBlock>(tile).First(candidate => candidate.Name == "TileNameText");
            Assert.Contains(app.Path, (string)nameText.ToolTip, "ToolTip 含目标路径。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }
}
