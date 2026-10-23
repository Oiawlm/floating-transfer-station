using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using FloatingTransferStation.Launcher;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 启动·自动发现窗口接线（#45）：「发现 N」按钮可见性随 pending 翻转与恒定几何
/// （＋添加零漂移）、采纳喂既有导入管线真加瓦片、全部忽略持久抑制、设置开关即时
/// 生效（停监视/按钮永不出现）、AppsFolder 差分直调枚举（缓存已热仍能发现新
/// AUMID）、FSW 防抖真实建 .lnk（临时目录＋宽容时窗）、设置窗文件夹管理（校验/
/// 持久化/移除）。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    /// <summary>可变假源：两次扫描间可追加候选（模拟「新装」）。</summary>
    private sealed class MutableDiscoverySource
    {
        public List<LauncherDiscoveryEntry> Entries { get; } = [];

        public Task<IReadOnlyList<LauncherDiscoveryEntry>> Resolve() =>
            Task.FromResult<IReadOnlyList<LauncherDiscoveryEntry>>([.. Entries]);
    }

    private static LauncherDiscoveryEntry DiscoveryEntry(
        string targetPath,
        string displayName = "发现的应用",
        LauncherKind kind = LauncherKind.Application) =>
        new(kind, targetPath, null, displayName, DateTimeOffset.UtcNow);

    private static void PumpUntilDiscoveryReady(MainWindow window) =>
        PumpUntilCondition(window.Dispatcher, () => window.LauncherDiscoveryForTests() is not null);

    private static Button DiscoveryBulkButton(MultiItemPickerWindow picker) => picker.PickerBulkActionButton;

    // ---- 按钮可见性＋恒定几何 ----

    [STATestMethod]
    public void Discovery_ButtonVisibilityFollowsPendingCountAndKeepsAddButtonGeometry()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var existing = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Application, @"C:\Apps\Existing.exe", "已有", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, existing);
        var source = new MutableDiscoverySource();
        source.Entries.Add(DiscoveryEntry(@"C:\Apps\Existing.exe", "已有变体"));
        source.Entries.Add(DiscoveryEntry(@"C:\Apps\New.exe", "新应用"));
        PumpUntilDiscoveryReady(window);
        window.UseLauncherDiscoverySourceForTests(source.Resolve);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);

            var toolbar = (UIElement)window.FindName("LauncherToolbar")!;
            var addButton = window.LauncherAddButton;
            Assert.AreEqual(
                Visibility.Collapsed,
                window.LauncherDiscoveryButton.Visibility,
                "pending=0 时按钮隐藏。");
            var addOriginBefore = addButton.TranslatePoint(new System.Windows.Point(0, 0), toolbar);

            window.RunLauncherDiscoveryScanForTests();
            PumpUntilCondition(
                window.Dispatcher,
                () => window.LauncherDiscoveryForTests()?.PendingCount == 1);
            CompleteLayout(window);

            Assert.AreEqual(Visibility.Visible, window.LauncherDiscoveryButton.Visibility, "pending>0 时按钮可见。");
            Assert.AreEqual("发现 1", window.LauncherDiscoveryButton.Content, "文案携带计数。");
            var addOriginAfter = addButton.TranslatePoint(new System.Windows.Point(0, 0), toolbar);
            Assert.AreEqual(
                addOriginBefore,
                addOriginAfter,
                "恒定几何：发现按钮显隐不改变＋添加位置（预留格，零漂移）。");

            // 目录里已有的条目（Existing.exe）绝不重复入列——按钮计数就是差分结果。
            Assert.AreEqual(
                @"C:\Apps\New.exe",
                window.LauncherDiscoveryForTests()!.PendingSnapshot().Single().TargetPath);
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    // ---- 采纳喂既有导入管线 ----

    [STATestMethod]
    public void Discovery_AdoptFeedsExistingImportPipelinesAndClearsPending()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var window = CreateLauncherWindowSync(directory, board, store, starter);
        var source = new MutableDiscoverySource();
        source.Entries.Add(DiscoveryEntry(@"C:\Apps\Found.lnk", "发现的应用"));
        source.Entries.Add(DiscoveryEntry("App!New", "新打包应用", LauncherKind.PackagedApp));
        PumpUntilDiscoveryReady(window);
        window.UseLauncherDiscoverySourceForTests(source.Resolve);
        window.RunLauncherDiscoveryScanForTests();
        PumpUntilCondition(window.Dispatcher, () => window.LauncherDiscoveryForTests()?.PendingCount == 2);

        window._showDiscoveryPickerDialog = picker =>
        {
            picker.Show();
            picker.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            CompleteLayout(picker);
            Assert.AreEqual("发现的新应用", picker.Title, "发现清单窗复用通用选择器。");
            Assert.AreEqual("全部忽略", DiscoveryBulkButton(picker).Content, "批量忽略动作在场。");
            for (var index = 0; index < picker.PickerList.Items.Count; index++)
            {
                var container = ((ItemsControl)picker.FindName("PickerList")!)
                    .ItemContainerGenerator.ContainerFromIndex(index);
                FindDescendant<CheckBox>(container)!.IsChecked = true;
            }

            picker.PickerConfirmButton.RaiseEvent(new RoutedEventArgs(
                Button.ClickEvent, picker.PickerConfirmButton));
            return true;
        };

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            Assert.AreEqual(Visibility.Visible, window.LauncherDiscoveryButton.Visibility);

            window.LauncherDiscoveryButton.RaiseEvent(new RoutedEventArgs(
                Button.ClickEvent, window.LauncherDiscoveryButton));

            PumpUntilCondition(
                window.Dispatcher,
                () => window.LaunchersForTests().Items.Count == 2 &&
                    window.LauncherDiscoveryForTests()?.PendingCount == 0);
            CompleteLayout(window);

            var catalog = window.LaunchersForTests();
            var fileItem = catalog.Items.Single(item => item.TargetPath == @"C:\Apps\Found.lnk");
            var aumidItem = catalog.Items.Single(item => item.TargetPath == "App!New");
            Assert.AreEqual(LauncherKind.Application, fileItem.Kind, "文件候选走既有文件导入管线。");
            // 文件导入显示名与发现源同源（ShellLinkHelper.DefaultDisplayName）——假
            // .lnk 解析失败回退文件名去扩展「Found」；真实 .lnk 两路径同为描述名。
            Assert.AreEqual("Found", fileItem.DisplayName);
            Assert.AreEqual(LauncherKind.PackagedApp, aumidItem.Kind, "AUMID 候选走既有已安装应用导入管线。");
            Assert.AreEqual(
                Visibility.Collapsed,
                window.LauncherDiscoveryButton.Visibility,
                "采纳清空 pending 后按钮隐藏。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    // ---- 全部忽略＝持久抑制 ----

    [STATestMethod]
    public void Discovery_DismissAllClearsPendingAndSuppressesRediscovery()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var window = CreateLauncherWindowSync(directory, board, store, starter);
        var source = new MutableDiscoverySource();
        source.Entries.Add(DiscoveryEntry(@"C:\Apps\Noise1.exe", "噪声一"));
        source.Entries.Add(DiscoveryEntry(@"C:\Apps\Noise2.exe", "噪声二"));
        PumpUntilDiscoveryReady(window);
        window.UseLauncherDiscoverySourceForTests(source.Resolve);
        window.RunLauncherDiscoveryScanForTests();
        PumpUntilCondition(window.Dispatcher, () => window.LauncherDiscoveryForTests()?.PendingCount == 2);

        window._showDiscoveryPickerDialog = picker =>
        {
            picker.Show();
            picker.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            CompleteLayout(picker);
            picker.PickerBulkActionButton.RaiseEvent(new RoutedEventArgs(
                Button.ClickEvent, picker.PickerBulkActionButton));
            return true;
        };

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);

            window.LauncherDiscoveryButton.RaiseEvent(new RoutedEventArgs(
                Button.ClickEvent, window.LauncherDiscoveryButton));

            PumpUntilCondition(
                window.Dispatcher,
                () => window.LauncherDiscoveryForTests()?.PendingCount == 0);
            CompleteLayout(window);
            Assert.AreEqual(Visibility.Collapsed, window.LauncherDiscoveryButton.Visibility);

            // 同源重扫：忽略键抑制重新入列（兜底重扫漏斗单独断言，不经 FSW）。
            window.RunLauncherDiscoveryScanForTests();
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            Assert.AreEqual(0, window.LauncherDiscoveryForTests()!.PendingCount, "忽略被持久记住。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    // ---- 设置开关即时生效三连：停监视＋按钮永不出现＋恢复即重扫 ----

    [STATestMethod]
    public void Discovery_PreferenceToggleStopsScansAndHidesButtonImmediately()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var preferencesStore = new RecordingPreferencesStore();
        var operationGate = new BoardOperationGate();
        var normalizer = new ImageNormalizer(store.ImagesDirectory);
        MainWindow? window = null;
        void ShowStatus(string message) => window!.ShowStatus(message);
        var clipboard = new ClipboardCaptureService(
            new NeverReadClipboardReader(), normalizer, board, store, ShowStatus, operationGate: operationGate);
        window = new MainWindow(
            board,
            store,
            WindowSettings.Default,
            clipboard,
            new BoardMutationService(board, store, ShowStatus, operationGate),
            new DragPayloadService(),
            new ExternalDropPayloadReader(new WindowsDataImageReader()),
            new ExternalDropImportService(normalizer, board, store, ShowStatus, operationGate),
            preferencesStore: preferencesStore,
            launcherStore: store);
        var source = new MutableDiscoverySource();
        source.Entries.Add(DiscoveryEntry(@"C:\Apps\A.exe", "A"));
        PumpUntilDiscoveryReady(window);
        window.UseLauncherDiscoverySourceForTests(source.Resolve);
        window.RunLauncherDiscoveryScanForTests();
        PumpUntilCondition(window.Dispatcher, () => window.LauncherDiscoveryForTests()?.PendingCount == 1);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            Assert.AreEqual(Visibility.Visible, window.LauncherDiscoveryButton.Visibility);

            // ① 关闭：按钮立即隐藏、漏斗立即拒扫。
            window.ApplyPreferences(window.CurrentPreferences with { LauncherDiscoveryEnabled = false });
            CompleteLayout(window);
            Assert.AreEqual(Visibility.Collapsed, window.LauncherDiscoveryButton.Visibility, "关闭即隐藏。");
            source.Entries.Add(DiscoveryEntry(@"C:\Apps\B.exe", "B"));
            window.RunLauncherDiscoveryScanForTests();
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            Assert.AreEqual(1, window.LauncherDiscoveryForTests()!.PendingCount, "关闭期间漏斗不再受理扫描。");
            Assert.IsFalse(preferencesStore.LastSaved!.LauncherDiscoveryEnabled, "关闭已原子持久化。");

            // ② 开启：立即重建监视源并重扫——追加候选即时入列。
            source.Entries.Add(DiscoveryEntry(@"C:\Apps\C.exe", "C"));
            window.ApplyPreferences(window.CurrentPreferences with { LauncherDiscoveryEnabled = true });
            PumpUntilCondition(
                window.Dispatcher,
                () => window.LauncherDiscoveryForTests()?.PendingCount == 3);
            CompleteLayout(window);
            Assert.AreEqual("发现 3", window.LauncherDiscoveryButton.Content, "开启即重扫（设置变更即重扫）。");
            Assert.IsTrue(preferencesStore.LastSaved.LauncherDiscoveryEnabled, "开启已持久化。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    // ---- AppsFolder 差分直调枚举：缓存已热后仍能发现新 AUMID ----

    [STATestMethod]
    public void Discovery_AppsFolderDiff_ReadsSourceFreshOnEveryScan()
    {
        var apps = new List<InstalledApp>
        {
            new("app.calc", "计算器")
        };
        // 选择器源先「热」一轮（会话缓存语义的调用方）——发现链路不经它。
        var pickerWarmSource = new Func<IReadOnlyList<InstalledApp>>(() => [.. apps]);

        // 扫描器每次直调注入源：模拟 Store 新装后第二次枚举多出 B。
        var scanned = LauncherDiscoveryScanner.Scan(
            [],
            pickerWarmSource,
            DateTimeOffset.UtcNow);
        Assert.AreEqual(1, scanned.Count(entry => entry.Kind == LauncherKind.PackagedApp));

        apps.Add(new InstalledApp("app.new", "新装应用"));
        var rescanned = LauncherDiscoveryScanner.Scan(
            [],
            pickerWarmSource,
            DateTimeOffset.UtcNow);

        Assert.AreEqual(
            "app.new",
            rescanned.Single(entry => entry.TargetPath == "app.new").TargetPath,
            "发现扫描每次直调枚举源——无会话缓存，新 AUMID 立即可见。");
    }

    // ---- FSW 防抖：临时目录真实建 .lnk＋宽容时窗（兜底重扫已单独断言） ----

    [STATestMethod]
    public void Discovery_FileWatcherDebounceDiscoversNewLnkInCustomFolder()
    {
        using var directory = new TestDirectory();
        var watchFolder = Path.Combine(directory.Root, "Apps");
        Directory.CreateDirectory(watchFolder);
        var board = new BoardService();
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var window = CreateLauncherWindowSync(directory, board, store, starter);
        // AppsFolder 归零降噪；开始菜单为真实枚举——断言用成员资格（不依赖绝对计数）。
        PumpUntilDiscoveryReady(window);
        window.UseLauncherDiscoveryAppsSourceForTests(() => []);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);

            // 设置变更路径：文件夹进偏好 → 监视源即时重建＋立即重扫（契约 #5）。
            window.ApplyPreferences(window.CurrentPreferences with
            {
                LauncherDiscoveryFolders = new List<string> { watchFolder }
            });
            PumpUntilCondition(
                window.Dispatcher,
                () => !window.LauncherDiscoveryForTests()!.PendingSnapshot().Any(
                    entry => entry.TargetPath.EndsWith("FreshInstall.lnk", StringComparison.OrdinalIgnoreCase)));

            var shortcutPath = Path.Combine(watchFolder, "FreshInstall.lnk");
            File.WriteAllText(shortcutPath, "not a real lnk (scanner takes path as-is)");

            // 宽容时窗：FSW 事件→500ms 防抖→重扫（差分幂等）。10s 预算防批跑抖动。
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                CompleteLayout(window);
                if (window.LauncherDiscoveryForTests()!.PendingSnapshot().Any(
                        entry => entry.TargetPath == shortcutPath))
                {
                    break;
                }

                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
            }

            Assert.IsTrue(
                window.LauncherDiscoveryForTests()!.PendingSnapshot().Any(
                    entry => entry.TargetPath == shortcutPath),
                "自定义文件夹新 .lnk 经 FSW 防抖进入待确认清单。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    // ---- 设置窗：面板页「启动·自动发现」节（开关/文件夹校验/持久化/移除） ----

    [STATestMethod]
    public void Discovery_SettingsSectionManagesFoldersAndPersistsImmediately()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var preferencesStore = new RecordingPreferencesStore();
        var operationGate = new BoardOperationGate();
        var normalizer = new ImageNormalizer(store.ImagesDirectory);
        MainWindow? window = null;
        void ShowStatus(string message) => window!.ShowStatus(message);
        var clipboard = new ClipboardCaptureService(
            new NeverReadClipboardReader(), normalizer, board, store, ShowStatus, operationGate: operationGate);
        window = new MainWindow(
            board,
            store,
            WindowSettings.Default,
            clipboard,
            new BoardMutationService(board, store, ShowStatus, operationGate),
            new DragPayloadService(),
            new ExternalDropPayloadReader(new WindowsDataImageReader()),
            new ExternalDropImportService(normalizer, board, store, ShowStatus, operationGate),
            preferencesStore: preferencesStore,
            launcherStore: store);
        PumpUntilDiscoveryReady(window);

        var watchFolder = Path.Combine(directory.Root, "Watch");
        Directory.CreateDirectory(watchFolder);

        try
        {
            window.Show();
            window.OpenSettings();
            var settings = GetPrivateField<SettingsWindow>(window, "_settingsWindow")!;
            try
            {
                // 导航到面板页。
                var panelNav = FindDescendants<RadioButton>(settings)
                    .Single(radio => AutomationProperties.GetName(radio) == "面板");
                panelNav.IsChecked = true;
                CompleteLayout(settings);

                var toggle = (CheckBox)settings.FindName("LauncherDiscoveryToggle")!;
                Assert.IsTrue(toggle.IsChecked!.Value, "默认开启。");

                // 校验拒绝：不存在路径 → 状态条提示、偏好不变。
                var input = (TextBox)settings.FindName("LauncherDiscoveryFolderInput")!;
                var add = (Button)settings.FindName("LauncherDiscoveryFolderAddButton")!;
                input.Text = Path.Combine(directory.Root, "不存在");
                add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, add));
                CompleteLayout(settings);
                var status = (TextBlock)settings.FindName("LauncherDiscoveryFolderStatus")!;
                Assert.AreEqual(Visibility.Visible, status.Visibility, "非法路径拒绝并提示。");
                Assert.IsNull(preferencesStore.LastSaved?.LauncherDiscoveryFolders, "非法路径不落偏好。");

                // 合法路径 → 立即持久化＋行渲染。
                input.Text = watchFolder;
                add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, add));
                CompleteLayout(settings);
                Assert.AreEqual(
                    watchFolder,
                    preferencesStore.LastSaved.LauncherDiscoveryFolders!.Single(),
                    "文件夹改动立即原子持久化。");
                var rows = (StackPanel)settings.FindName("LauncherDiscoveryFolderList")!;
                Assert.AreEqual(1, rows.Children.Count, "文件夹行渲染。");

                // 重复添加拒绝。
                input.Text = watchFolder;
                add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, add));
                CompleteLayout(settings);
                Assert.AreEqual(1, preferencesStore.LastSaved!.LauncherDiscoveryFolders!.Count, "重复路径拒绝。");

                // 移除 → 偏好清空。
                var remove = FindDescendants<Button>(rows).Single(button => Equals(button.Content, "移除"));
                remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, remove));
                CompleteLayout(settings);
                Assert.AreEqual(0, preferencesStore.LastSaved!.LauncherDiscoveryFolders!.Count, "移除立即持久化。");

                // 开关关闭 → 输入区置灰＋偏好持久化。
                toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
                Assert.IsFalse(preferencesStore.LastSaved.LauncherDiscoveryEnabled, "开关关闭立即持久化。");
                Assert.IsFalse(((Button)settings.FindName("LauncherDiscoveryFolderAddButton")!).IsEnabled, "关闭时输入区置灰。");
            }
            finally
            {
                settings.Close();
            }
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }
}
