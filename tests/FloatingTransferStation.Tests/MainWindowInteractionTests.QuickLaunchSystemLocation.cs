using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatingTransferStation.Launcher;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 启动界面「系统位置」入口（#30 扩展）：＋菜单原生子菜单两项（回收站/此电脑）可见性、
/// 点击经注册表构造 ShellLocation 条目走既有添加协议（瓦片出现、状态条汇报）、重复
/// 添加由既有去重收敛（滚动＋脉冲＋「已在启动列表中」）、图标管线对 ShellLocation
/// 条目被发起（图标服务 LoadOverrideForTests 探针缝）。真实 CLSID 图标提取在 CI
/// Windows 环境由 IconLoop 家族覆盖真实提取路径。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public void SystemLocation_AddMenu_SubmenuShowsRegistryEntries()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var window = CreateLauncherWindowSync(directory, board, store, starter);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);

            window.LauncherAddButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, window.LauncherAddButton));
            CompleteLayout(window);
            Assert.IsTrue(window.LauncherAddMenu.IsOpen, "＋ 打开添加菜单。");
            Assert.IsTrue(window.LauncherAddSystemLocationMenuItem.IsVisible, "「系统位置」分组项随菜单可见。");
            Assert.AreEqual("系统位置", window.LauncherAddSystemLocationMenuItem.Header);

            // 子菜单展开（原生 IsSubmenuOpen → PART_Popup）；子项数据直引注册表单一来源。
            window.LauncherAddSystemLocationMenuItem.IsSubmenuOpen = true;
            CompleteLayout(window);
            Assert.IsTrue(window.LauncherAddSystemLocationRecycleBinItem.IsVisible, "回收站子项可见。");
            Assert.IsTrue(window.LauncherAddSystemLocationThisComputerItem.IsVisible, "此电脑子项可见。");
            Assert.AreSame(
                SystemLauncherLocations.RecycleBin,
                window.LauncherAddSystemLocationRecycleBinItem.DataContext,
                "回收站子项 DataContext=注册表条目（XAML 零 CLSID 字面量）。");
            Assert.AreSame(
                SystemLauncherLocations.ThisComputer,
                window.LauncherAddSystemLocationThisComputerItem.DataContext,
                "此电脑子项 DataContext=注册表条目。");
            Assert.AreEqual(
                SystemLauncherLocations.RecycleBin.DisplayName,
                window.LauncherAddSystemLocationRecycleBinItem.Header,
                "子项标题取注册表显示名。");
            Assert.AreEqual(
                SystemLauncherLocations.ThisComputer.DisplayName,
                window.LauncherAddSystemLocationThisComputerItem.Header,
                "子项标题取注册表显示名。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void SystemLocation_AddViaMenu_CreatesTileAndDuplicateConverges()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var window = CreateLauncherWindowSync(directory, board, store, starter);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            window.LauncherAddSystemLocationRecycleBinItem.RaiseEvent(
                new RoutedEventArgs(MenuItem.ClickEvent, window.LauncherAddSystemLocationRecycleBinItem));
            PumpUntilCondition(
                window.Dispatcher,
                () => window.LaunchersForTests().Items.Count == 1);
            PumpUntilCondition(
                window.Dispatcher,
                () => viewModel.StatusText == "添加了 1 项。");
            CompleteLayout(window);

            var catalog = window.LaunchersForTests();
            var item = catalog.Items.Single();
            Assert.AreEqual(LauncherKind.ShellLocation, item.Kind, "添加=ShellLocation 条目。");
            Assert.AreEqual(SystemLauncherLocations.RecycleBin.TargetPath, item.TargetPath, "目标=注册表 CLSID 解析名。");
            Assert.AreEqual("回收站", item.DisplayName);
            Assert.IsNotNull(FindLauncherTile(window, item), "瓦片出现。");

            // 重复添加：既有去重路径收敛（不新增、状态条「已在启动列表中」）。
            window.LauncherAddSystemLocationRecycleBinItem.RaiseEvent(
                new RoutedEventArgs(MenuItem.ClickEvent, window.LauncherAddSystemLocationRecycleBinItem));
            PumpUntilCondition(
                window.Dispatcher,
                () => viewModel.StatusText == "已在启动列表中");
            CompleteLayout(window);

            Assert.AreEqual(1, catalog.Items.Count, "重复添加不新增。");
            Assert.AreEqual("已在启动列表中", viewModel.StatusText, "既有去重汇报。");
            Assert.IsFalse(starter.Started.Any(), "添加路径绝不触发启动。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void SystemLocation_AddViaMenu_IconPipelineEngagedForShellLocation()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var window = CreateLauncherWindowSync(directory, board, store, starter);
        var extracted = new List<LauncherItem>();
        var placeholder = new WriteableBitmap(1, 1, 96, 96, PixelFormats.Pbgra32, null);
        placeholder.Freeze();

        try
        {
            var iconService = GetPrivateField<LauncherIconService>(window, "_launcherIcons");
            Assert.IsNotNull(iconService, "窗口图标服务已就绪。");
            iconService.LoadOverrideForTests = item =>
            {
                lock (extracted)
                {
                    extracted.Add(item);
                }

                return placeholder;
            };

            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);

            window.LauncherAddSystemLocationThisComputerItem.RaiseEvent(
                new RoutedEventArgs(MenuItem.ClickEvent, window.LauncherAddSystemLocationThisComputerItem));
            PumpUntilCondition(
                window.Dispatcher,
                () => window.LaunchersForTests().Items.Count == 1);

            var item = window.LaunchersForTests().Items.Single();
            var tileIcon = WaitLauncherTileIcon(window, item, TimeSpan.FromSeconds(10));
            Assert.IsNotNull(tileIcon, "图标管线应回填 ShellLocation 瓦片（占位被替换）。");
            lock (extracted)
            {
                var request = extracted.Single();
                Assert.AreEqual(LauncherKind.ShellLocation, request.Kind, "提取请求携带 ShellLocation Kind。");
                Assert.AreEqual(
                    SystemLauncherLocations.ThisComputer.TargetPath,
                    request.TargetPath,
                    "提取请求目标=注册表 CLSID 解析名。");
            }
        }
        finally
        {
            if (GetPrivateField<LauncherIconService>(window, "_launcherIcons") is { } service)
            {
                service.LoadOverrideForTests = null;
            }

            CloseWindowWithoutSaving(window);
        }
    }
}
