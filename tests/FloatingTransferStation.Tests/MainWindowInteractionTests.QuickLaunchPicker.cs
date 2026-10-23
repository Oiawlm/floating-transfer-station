using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FloatingTransferStation.Design;
using FloatingTransferStation.Launcher;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 快捷启动切片 4（#30）：已安装应用选择器——AppsFolder 真实枚举 smoke、
/// 选择器窗口搜索/勾选/确认/Esc、＋两入口菜单接线、假源添加流程（去重/状态条）、
/// 「应用未注册」失败的重枚举取图接线、持久化 AUMID 重启取图闭环。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    private static InstalledApp FakeInstalledApp(string aumid, string displayName) => new(aumid, displayName);

    /// <summary>泛化选择器（#45）的已安装应用装配：与生产调用点同一映射（Payload=InstalledApp）。</summary>
    private static MultiItemPickerWindow CreateInstalledPicker(
        DesignTheme theme,
        IReadOnlyList<InstalledApp> apps,
        ISet<string>? existingAumids = null)
    {
        existingAumids ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new MultiItemPickerWindow(
            theme,
            "添加已安装应用",
            "搜索应用",
            "未枚举到已安装应用",
            apps.Select(app => new MultiItemPickerWindow.PickerRow(
                app,
                app.DisplayName,
                subtitle: null,
                existingAumids.Contains(app.Aumid))).ToList());
    }

    private static void CheckPickerRow(MultiItemPickerWindow picker, int index)
    {
        var container = ((ItemsControl)picker.FindName("PickerList")!).ItemContainerGenerator.ContainerFromIndex(index);
        var checkBox = FindDescendant<CheckBox>(container);
        Assert.IsNotNull(checkBox, $"选择器第 {index} 行复选框已生成。");
        checkBox.IsChecked = true;
    }

    private static void ClickPickerConfirm(MultiItemPickerWindow picker) =>
        picker.PickerConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, picker.PickerConfirmButton));

    // ---- 真实 AppsFolder 枚举（smoke：非空、AUMID 唯一、显示名无重复、区域排序） ----

    [STATestMethod]
    public void InstalledAppsEnumerator_RealMachine_FiltersAndSorts()
    {
        var apps = InstalledAppsEnumerator.Enumerate();

        if (apps.Count == 0)
        {
            Assert.Inconclusive("本机未枚举到已安装应用（AppsFolder 为空）。");
        }

        foreach (var app in apps)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(app.Aumid), "AUMID 非空（无 AUMID 项已过滤）。");
            Assert.IsFalse(string.IsNullOrWhiteSpace(app.DisplayName), "显示名非空。");
        }

        CollectionAssert.AreEquivalent(
            apps.Select(app => app.Aumid).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            apps.Select(app => app.Aumid).ToList(),
            "AUMID 去重（同 AUMID 只留首条）。");
        CollectionAssert.AreEquivalent(
            apps.Select(app => app.DisplayName).Distinct(StringComparer.CurrentCulture).ToList(),
            apps.Select(app => app.DisplayName).ToList(),
            "重复别名已过滤。");

        for (var i = 1; i < apps.Count; i++)
        {
            Assert.IsTrue(
                string.Compare(apps[i - 1].DisplayName, apps[i].DisplayName, StringComparison.CurrentCulture) <= 0,
                $"按显示名区域感知排序（{apps[i - 1].DisplayName} ≤ {apps[i].DisplayName}）。");
        }
    }

    // ---- 选择器窗口（真实 WPF 非模态驱动） ----

    [STATestMethod]
    public void InstalledPicker_SearchFiltersAndKeepsCheckedState()
    {
        var apps = new[]
        {
            FakeInstalledApp("app.calc", "计算器"),
            FakeInstalledApp("app.note", "记事本"),
            FakeInstalledApp("app.center", "计算中心")
        };
        var picker = CreateInstalledPicker(DesignTheme.Light, apps);
        picker.Show();
        picker.Resources[SystemParameters.ClientAreaAnimationKey] = false;
        try
        {
            CompleteLayout(picker);
            Assert.AreEqual(3, picker.PickerList.Items.Count, "初始全量可见。");
            Assert.IsFalse(picker.PickerConfirmButton.IsEnabled, "未勾选时确认禁用。");

            picker.PickerSearchBox.Text = "计算";
            CompleteLayout(picker);
            Assert.AreEqual(2, picker.PickerList.Items.Count, "搜索过滤=包含匹配。");

            CheckPickerRow(picker, 0);
            Assert.AreEqual("添加（1）", picker.PickerConfirmButton.Content, "勾选后确认钮计数。");
            Assert.IsTrue(picker.PickerConfirmButton.IsEnabled);

            // 过滤不丢已选：清空搜索恢复全量，勾选保留。
            picker.PickerSearchBox.Text = string.Empty;
            CompleteLayout(picker);
            Assert.AreEqual(3, picker.PickerList.Items.Count);
            Assert.AreEqual("添加（1）", picker.PickerConfirmButton.Content, "过滤往返勾选保留。");

            // 无匹配空态。
            picker.PickerSearchBox.Text = "不存在的应用xyz";
            CompleteLayout(picker);
            Assert.AreEqual(0, picker.PickerList.Items.Count);
            Assert.AreEqual(Visibility.Visible, picker.PickerEmptyHint.Visibility, "无匹配提示可见。");
            Assert.AreEqual("没有匹配的应用", picker.PickerEmptyHint.Text);
        }
        finally
        {
            picker.Close();
        }
    }

    [STATestMethod]
    public void InstalledPicker_ConfirmReturnsCheckedAppsAndMarksExisting()
    {
        var apps = new[]
        {
            FakeInstalledApp("app.calc", "计算器"),
            FakeInstalledApp("app.note", "记事本"),
            FakeInstalledApp("app.old", "旧应用")
        };
        var picker = CreateInstalledPicker(
            DesignTheme.Light,
            apps,
            new HashSet<string>(["app.old"], StringComparer.OrdinalIgnoreCase));
        picker.Show();
        picker.Resources[SystemParameters.ClientAreaAnimationKey] = false;
        try
        {
            CompleteLayout(picker);
            Assert.AreEqual(3, picker.PickerList.Items.Count);

            CheckPickerRow(picker, 0);
            CheckPickerRow(picker, 2);
            ClickPickerConfirm(picker);
            CompleteLayout(picker);

            var selection = picker.Selection.OfType<InstalledApp>().ToList();
            Assert.AreEqual(2, selection.Count, "确认回传勾选清单（含已添加项——去重在添加路径收敛）。");
            Assert.AreEqual("app.calc", selection[0].Aumid);
            Assert.AreEqual("app.old", selection[1].Aumid);
        }
        finally
        {
            picker.Close();
        }
    }

    [STATestMethod]
    public void InstalledPicker_EscapeClosesWithEmptySelection()
    {
        var picker = CreateInstalledPicker(
            DesignTheme.Light, [FakeInstalledApp("app.calc", "计算器")]);
        picker.Show();
        picker.Resources[SystemParameters.ClientAreaAnimationKey] = false;
        try
        {
            CompleteLayout(picker);
            CheckPickerRow(picker, 0);
            picker.RaiseEvent(NewKeyEventArgs(picker, Key.Escape));
            CompleteLayout(picker);
            Assert.IsFalse(picker.IsLoaded, "Esc 关闭选择器。");
            Assert.AreEqual(0, picker.Selection.Count, "Esc=取消（空回传）。");        }
        finally
        {
            if (picker.IsLoaded)
            {
                picker.Close();
            }
        }
    }

    [STATestMethod]
    public void InstalledPicker_EmptySourceShowsEnumerationHint()
    {
        var picker = CreateInstalledPicker(DesignTheme.Light, []);
        picker.Show();
        picker.Resources[SystemParameters.ClientAreaAnimationKey] = false;
        try
        {
            CompleteLayout(picker);
            Assert.AreEqual(0, picker.PickerList.Items.Count);
            Assert.AreEqual("未枚举到已安装应用", picker.PickerEmptyHint.Text, "枚举为空与无匹配文案区分。");
        }
        finally
        {
            picker.Close();
        }
    }

    // ---- ＋ 两入口菜单与假源添加流程 ----

    [STATestMethod]
    public void QuickLaunch_AddMenu_InstalledPickerAddsPackagedApps()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var existing = new LauncherItem(
            Guid.NewGuid(), LauncherKind.PackagedApp, "app.existing", "已有应用", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, existing);
        window.UseInstalledAppsSourceForTests(() => new[]
        {
            FakeInstalledApp("app.existing", "已有应用"),
            FakeInstalledApp("app.new1", "新应用一"),
            FakeInstalledApp("app.new2", "新应用二")
        });
        window._showPickerDialog = picker =>
        {
            // 模拟模态确认路径：真实显示驱动容器生成，全选（含已存在项 → 添加路径去重收敛）。
            picker.Show();
            picker.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            CompleteLayout(picker);
            for (var i = 0; i < picker.PickerList.Items.Count; i++)
            {
                CheckPickerRow(picker, i);
            }

            ClickPickerConfirm(picker);
            return true;
        };

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);

            // ＋ 打开两入口菜单。
            window.LauncherAddButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, window.LauncherAddButton));
            CompleteLayout(window);
            Assert.IsTrue(window.LauncherAddMenu.IsOpen, "＋ 打开两入口菜单。");
            Assert.AreEqual("应用文件…", window.LauncherAddFileMenuButton.Content);
            Assert.AreEqual("已安装应用…", window.LauncherAddInstalledMenuButton.Content);

            // 已安装应用… → 选择器 → 全选确认。
            window.LauncherAddInstalledMenuButton.RaiseEvent(new RoutedEventArgs(
                Button.ClickEvent, window.LauncherAddInstalledMenuButton));
            Assert.IsFalse(window.LauncherAddMenu.IsOpen, "入口点击后菜单关闭。");

            PumpUntilCondition(
                window.Dispatcher,
                () => window.LaunchersForTests().Items.Count == 3);
            // 乐观上屏（Issue #176）：条目先于保存完成出现在集合；状态条汇报在
            // 导入续体（保存往返后）才发出——等它到齐再断言。
            var viewModel = (MainWindowViewModel)window.DataContext;
            PumpUntilCondition(
                window.Dispatcher,
                () => viewModel.StatusText == "添加了 2 项，1 个已存在。");
            CompleteLayout(window);

            var catalog = window.LaunchersForTests();
            Assert.AreEqual(3, catalog.Items.Count, "已有 1 + 新增 2。");
            var added = catalog.Items.Where(item => item.TargetPath is "app.new1" or "app.new2").ToList();
            Assert.AreEqual(2, added.Count);
            Assert.IsTrue(added.All(item => item.Kind == LauncherKind.PackagedApp), "选择器添加=PackagedApp（只存 AUMID）。");
            Assert.AreEqual("新应用一", added.Single(item => item.TargetPath == "app.new1").DisplayName);

            Assert.AreEqual("添加了 2 项，1 个已存在。", viewModel.StatusText, "多源合并汇报（零段省略）。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_AddMenu_InstalledEntryEmptySourceShowsStatus()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var window = CreateLauncherWindowSync(directory, board, store, starter);
        window.UseInstalledAppsSourceForTests(() => []);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);

            window.LauncherAddInstalledMenuButton.RaiseEvent(new RoutedEventArgs(
                Button.ClickEvent, window.LauncherAddInstalledMenuButton));
            CompleteLayout(window);

            Assert.AreEqual("未枚举到已安装应用。", ((MainWindowViewModel)window.DataContext).StatusText);
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    // ---- 「应用未注册」失败 → 重枚举取图接线（#30 §三.6/§四） ----

    [STATestMethod]
    public void QuickLaunch_NotRegisteredLaunchFailure_TriggersIconRefresh()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var launcher = new AppLauncher(
            starter.Invoke,
            packagedActivator: (_, _) => new PackagedAppActivationResult(false, null, unchecked((int)0x80270254)));
        var seeded = new LauncherItem(
            Guid.NewGuid(), LauncherKind.PackagedApp, "app.gone", "已卸载应用", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, launcher, seeded);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            // 目录装载的是持久化副本：断言以窗口内实例为准。
            var item = window.LaunchersForTests().Items.Single();
            var tile = FindLauncherTile(window, item);
            Assert.IsNotNull(tile);

            // 键盘路径触发启动（与查看态 Enter 启动同管线）。
            Keyboard.ClearFocus();
            window.RaiseEvent(NewKeyEventArgs(window, Key.Right));
            window.RaiseEvent(NewKeyEventArgs(window, Key.Enter));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
            CompleteLayout(window);

            var viewModel = (MainWindowViewModel)window.DataContext;
            Assert.IsTrue(item.LaunchFailed, "未注册失败进灰态。");
            StringAssert.StartsWith(viewModel.StatusText, "无法启动：应用未注册");

            var icons = GetPrivateField<LauncherIconService>(window, "_launcherIcons");
            Assert.IsNotNull(icons);
            Assert.IsTrue(
                icons.RefreshRequestsForTests.Contains(item.IconKey),
                "未注册失败触发重枚举取图（RequestRefresh）。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    // ---- 持久化 AUMID 重启取图闭环（#30 §六.1：枚举回退路径） ----

    [STATestMethod]
    public void QuickLaunch_PersistedAumid_IconExtractsAfterRestart()
    {
        var apps = InstalledAppsEnumerator.Enumerate();
        if (apps.Count == 0)
        {
            Assert.Inconclusive("本机未枚举到已安装应用（AUMID 取图闭环无法验证）。");
        }

        // 部分系统组件（如 ClickToDoApp）无可用图标属可接受降级（占位继续）——
        // 测试挑选一个可提取图标的应用：优先 WindowsTerminal（CI/本机稳定存在），
        // 逐个用提取链直接验证（HICON 出参），命中后销毁探测句柄。
        var candidates = apps
            .OrderBy(app => app.Aumid.StartsWith("Microsoft.WindowsTerminal", StringComparison.Ordinal) ? 0 : 1)
            .Take(12);
        string? chosenAumid = null;
        string? chosenName = null;
        foreach (var app in candidates)
        {
            var probe = InstalledAppsEnumerator.TryExtractIconForAumid(app.Aumid);
            if (probe != IntPtr.Zero)
            {
                FloatingTransferStation.Services.ShellInterop.DestroyIcon(probe);
                chosenAumid = app.Aumid;
                chosenName = app.DisplayName;
                break;
            }
        }

        if (chosenAumid is null)
        {
            Assert.Inconclusive("本机已安装应用均未产出图标（提取链环境不可用）。");
        }

        using var directory = new TestDirectory();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var item = new LauncherItem(
            Guid.NewGuid(), LauncherKind.PackagedApp, chosenAumid, chosenName!, DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, item);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);

            // 真实 Shell 图标管线（STA 专线程）：仅凭持久化 AUMID 提取（解析名失败时
            // 走 AppsFolder 枚举回退）。Shell COM 在 CI 上偶发慢，给足余量。
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            ImageSource? tileIcon = null;
            while (DateTime.UtcNow < deadline)
            {
                CompleteLayout(window);
                if (FindLauncherTile(window, item) is { } tile &&
                    FindDescendant<Image>(tile)?.Source is { } icon)
                {
                    tileIcon = icon;
                    break;
                }

                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(50));
            }

            Assert.IsNotNull(tileIcon, "重启后仅凭持久化 AUMID 完成取图（瓦片图标非占位）。"); ;
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }
}
