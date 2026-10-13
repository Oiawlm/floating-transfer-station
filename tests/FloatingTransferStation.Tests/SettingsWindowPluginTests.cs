using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 设置窗口插件区块回归：插件行渲染、加载错误可见、
/// 开关立即生效并持久化，取证截图由 FTS_PLUGIN_SETTINGS_EVIDENCE_DIR 驱动。
/// </summary>
[TestClass]
public sealed class SettingsWindowPluginTests
{
    private static MainWindow CreateWindow(
        TestDirectory directory,
        RecordingPreferencesStore preferencesStore,
        PluginCatalog pluginCatalog,
        IBoardStore? store = null)
    {
        store ??= new RecordingSettingsBoardStore(directory.Root);
        var board = new BoardService();
        var normalizer = new ImageNormalizer(store.ImagesDirectory);
        var operationGate = new BoardOperationGate();
        MainWindow? window = null;
        void ShowStatus(string message) => window?.ShowStatus(message);
        var clipboard = new ClipboardCaptureService(
            new IdleClipboardReader(),
            normalizer,
            board,
            store,
            ShowStatus,
            operationGate: operationGate);
        window = new MainWindow(
            board,
            store,
            WindowSettings.Default,
            clipboard,
            new BoardMutationService(board, store, ShowStatus, operationGate),
            new DragPayloadService(),
            new ExternalDropPayloadReader(new WindowsDataImageReader()),
            new ExternalDropImportService(
                normalizer,
                board,
                store,
                ShowStatus,
                operationGate),
            preferences: null,
            preferencesStore: preferencesStore,
            startupManager: new FakeStartupManager(),
            dataDirectory: directory.Root,
            pluginCatalog: pluginCatalog);
        // 真实应用由 Application 启动安装该上下文；测试环境补装，
        // 使 async 事件处理器的续延回到 UI 线程而不是线程池。
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(window.Dispatcher));
        return window;
    }

    private static SettingsWindow OpenSettings(MainWindow window)
    {
        window.Show();
        var gear = window.FindName("SettingsButton") as Button;
        Assert.IsNotNull(gear);
        gear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, gear));
        var settings = window.Dispatcher.Invoke(() =>
        {
            var field = typeof(MainWindow).GetField(
                "_settingsWindow",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return (SettingsWindow?)field?.GetValue(window);
        });
        Assert.IsNotNull(settings);
        // 双栏布局（契约 #24）：插件节位于「数据与维护」页，证据截图与可见性依赖先导航。
        var dataNav = window.Dispatcher.Invoke(() =>
            settings.FindName("SettingsNavData") as RadioButton);
        Assert.IsNotNull(dataNav, "设置窗必须有「数据与维护」导航项。");
        window.Dispatcher.Invoke(() => dataNav.IsChecked = true);
        return settings;
    }

    private static void CompleteLayout(Window window)
    {
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }

    private static void PumpDispatcherUntil(Dispatcher dispatcher, Func<bool> condition)
    {
        var frame = new DispatcherFrame();
        var timedOut = false;
        var timeout = new DispatcherTimer(DispatcherPriority.Send, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        timeout.Tick += (_, _) =>
        {
            timedOut = true;
            timeout.Stop();
            frame.Continue = false;
        };
        var poller = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(20)
        };
        poller.Tick += (_, _) =>
        {
            if (condition())
            {
                poller.Stop();
                timeout.Stop();
                frame.Continue = false;
            }
        };
        timeout.Start();
        poller.Start();
        Dispatcher.PushFrame(frame);
        timeout.Stop();
        poller.Stop();
        if (timedOut)
        {
            throw new TimeoutException("The plugin toggle did not take effect within five seconds.");
        }
    }

    private static void CloseWindow(Window window)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler closedHandler = (_, _) => closed.TrySetResult();
        window.Closed += closedHandler;
        try
        {
            window.Close();
            if (!closed.Task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                window.Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
            }
        }
        finally
        {
            window.Closed -= closedHandler;
        }
    }

    private static void SaveVisualEvidence(Window window, string fileName)
    {
        var directory = Environment.GetEnvironmentVariable("FTS_PLUGIN_SETTINGS_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight),
            96,
            96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, fileName));
        encoder.Save(stream);
    }

    private static PluginCatalog CreateCatalog(TestDirectory directory, string? brokenPluginId = null)
    {
        var manifestDirectory = Path.Combine(directory.Root, "plugins", "demo-tidy");
        Directory.CreateDirectory(manifestDirectory);
        File.WriteAllText(
            Path.Combine(manifestDirectory, PluginCatalog.ManifestFileName),
            """
            {
              "id": "demo-tidy",
              "name": "演示整理",
              "version": "1.2.0",
              "description": "把 ok 替换为 好的",
              "kind": "text-cleaner",
              "textRules": [ { "pattern": "ok", "replacement": "好的" } ]
            }
            """);
        if (brokenPluginId is { } brokenId)
        {
            var brokenDirectory = Path.Combine(directory.Root, "plugins", brokenId);
            Directory.CreateDirectory(brokenDirectory);
            File.WriteAllText(
                Path.Combine(brokenDirectory, PluginCatalog.ManifestFileName),
                "{\"id\": \"broken one\"}");
        }

        var catalog = new PluginCatalog(
            AppPaths.FromDataDirectory(directory.Root),
            builtInPluginsDirectory: null,
            new AtomicTextWriter());
        catalog.LoadAsync().GetAwaiter().GetResult();
        return catalog;
    }

    [STATestMethod]
    public void PluginSection_ShowsNameVersionAndToggleOffByDefault()
    {
        using var directory = new TestDirectory();
        var catalog = CreateCatalog(directory);
        var window = CreateWindow(directory, new RecordingPreferencesStore(), catalog);
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);

            var panel = settings.FindName("PluginListPanel") as StackPanel;
            Assert.IsNotNull(panel);
            Assert.AreEqual(1, panel.Children.Count, "应当恰好渲染一个插件行。");
            var row = (Grid)((Border)panel.Children[0]).Child;
            var title = ((StackPanel)row.Children[0]).Children.OfType<TextBlock>().First().Text;
            StringAssert.Contains(title, "演示整理");
            StringAssert.Contains(title, "1.2.0");

            var toggle = row.Children.OfType<CheckBox>().Single();
            Assert.AreEqual("demo-tidy", toggle.Tag);
            Assert.IsFalse(toggle.IsChecked!.Value, "插件默认关闭。");

            var directoryText = settings.FindName("PluginDirectoryText") as TextBox;
            Assert.IsNotNull(directoryText);
            StringAssert.Contains(directoryText.Text, "plugins");
            SaveVisualEvidence(settings, "plugin-section-default.png");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void PluginToggle_EnablesPipelineImmediatelyAndPersists()
    {
        using var directory = new TestDirectory();
        var catalog = CreateCatalog(directory);
        var window = CreateWindow(directory, new RecordingPreferencesStore(), catalog);
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            var panel = (StackPanel)settings.FindName("PluginListPanel")!;
            var toggle = ((Grid)((Border)panel.Children[0]).Child).Children.OfType<CheckBox>().Single();

            toggle.IsChecked = true;
            PumpDispatcherUntil(
                settings.Dispatcher,
                () => catalog.TextCleaner.RuleCount == 1);

            Assert.AreEqual("好的", catalog.TextCleaner.Apply("ok"));
            SaveVisualEvidence(settings, "plugin-section-enabled.png");

            // 应用立即生效、异步持久化:重载断言前必须等到状态文件可重读为启用,
            // 否则慢盘上会读到尚未落盘的旧状态(间歇性失败两次,据此修复)。
            var paths = AppPaths.FromDataDirectory(directory.Root);
            PluginCatalog? persisted = null;
            PumpDispatcherUntil(
                settings.Dispatcher,
                () =>
                {
                    persisted = new PluginCatalog(paths, null, new AtomicTextWriter());
                    persisted.LoadAsync().GetAwaiter().GetResult();
                    return persisted.Entries.Single(entry => entry.Id == "demo-tidy").Enabled;
                });
            Assert.IsNotNull(persisted);

            toggle.IsChecked = false;
            PumpDispatcherUntil(
                settings.Dispatcher,
                () => catalog.TextCleaner.RuleCount == 0);
            // 等待禁用状态同样落盘:避免 TestDirectory 清理与原子写临时文件并发。
            PumpDispatcherUntil(
                settings.Dispatcher,
                () =>
                {
                    var probe = new PluginCatalog(paths, null, new AtomicTextWriter());
                    probe.LoadAsync().GetAwaiter().GetResult();
                    return !probe.Entries.Single(entry => entry.Id == "demo-tidy").Enabled;
                });
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void BrokenPlugin_ShowsLoadErrorAndDisabledToggle()
    {
        using var directory = new TestDirectory();
        var catalog = CreateCatalog(directory, brokenPluginId: "broken-one");
        var window = CreateWindow(directory, new RecordingPreferencesStore(), catalog);
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);

            var panel = (StackPanel)settings.FindName("PluginListPanel")!;
            Assert.AreEqual(2, panel.Children.Count);
            var brokenRow = panel.Children
                .OfType<Border>()
                .Select(surface => (Grid)surface.Child)
                .Single(row => row.Children.OfType<CheckBox>().Single().Tag as string != "demo-tidy");
            var toggle = brokenRow.Children.OfType<CheckBox>().Single();
            Assert.IsFalse(toggle.IsEnabled, "清单非法的插件不能启用。");

            var detail = ((StackPanel)brokenRow.Children[0]).Children.OfType<TextBlock>().Last().Text;
            StringAssert.Contains(detail, "id");
            SaveVisualEvidence(settings, "plugin-section-broken.png");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void DirectorySection_RendersInBothThemes()
    {
        AssertSettingsDirectoryEvidence(DesignTheme.Dark, "settings-directory-dark.png");
        AssertSettingsDirectoryEvidence(DesignTheme.Light, "settings-directory-light.png");
    }

    private static void AssertSettingsDirectoryEvidence(DesignTheme theme, string fileName)
    {
        using var directory = new TestDirectory();
        var catalog = CreateCatalog(directory);
        var window = CreateWindow(directory, new RecordingPreferencesStore(), catalog);
        try
        {
            var settings = OpenSettings(window);
            DesignThemeManager.Apply(settings, theme);
            CompleteLayout(settings);

            AssertDirectoryControlsVisible(settings);
            SaveVisualEvidence(settings, fileName);
        }
        finally
        {
            CloseLeftoverSettings(window);
        }
    }

    private static void AssertDirectoryControlsVisible(SettingsWindow settings)
    {
        Assert.AreEqual(Visibility.Visible, ((Button)settings.FindName("ChangeDataDirectoryButton")!).Visibility);
        Assert.AreEqual(Visibility.Visible, ((Button)settings.FindName("OpenDataDirectoryButton")!).Visibility);
        Assert.AreEqual(Visibility.Visible, ((Button)settings.FindName("ChangePluginDirectoryButton")!).Visibility);
        Assert.AreEqual(Visibility.Visible, ((Button)settings.FindName("ResetPluginDirectoryButton")!).Visibility);
    }

    private static void CloseLeftoverSettings(MainWindow window)
    {
        var field = typeof(MainWindow).GetField(
            "_settingsWindow",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field?.GetValue(window) is Window leftover)
        {
            CompleteLayout(leftover);
            leftover.Close();
        }
    }

    [STATestMethod]
    public void MissingCatalog_HidesDirectoryControlsAndExplains()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new RecordingPreferencesStore(), pluginCatalog: null!);
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);

            var help = settings.FindName("PluginHelpText") as TextBlock;
            Assert.IsNotNull(help);
            StringAssert.Contains(help.Text, "不可用");
            Assert.AreEqual(
                Visibility.Collapsed,
                ((Button)settings.FindName("OpenPluginDirectoryButton")!).Visibility);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void DirectoryControls_ShowChangeButtonsAndDefaultNote()
    {
        using var directory = new TestDirectory();
        var catalog = CreateCatalog(directory);
        var window = CreateWindow(directory, new RecordingPreferencesStore(), catalog);
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);

            // 数据目录区块：路径 + 更改 + 打开（1.16.0 起可更改，不再只读）。
            var changeData = settings.FindName("ChangeDataDirectoryButton") as Button;
            Assert.IsNotNull(changeData);
            Assert.AreEqual(Visibility.Visible, changeData.Visibility);

            // 插件目录区块：路径 + 更改 + 恢复默认 + 打开，默认时说明文字标明默认。
            Assert.AreEqual(
                Visibility.Visible,
                ((Button)settings.FindName("ChangePluginDirectoryButton")!).Visibility);
            Assert.AreEqual(
                Visibility.Visible,
                ((Button)settings.FindName("ResetPluginDirectoryButton")!).Visibility);
            var note = settings.FindName("PluginDirectoryNoteText") as TextBlock;
            Assert.IsNotNull(note);
            StringAssert.Contains(note.Text, "默认");
            StringAssert.Contains(note.Text, "数据目录");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void PluginRows_FollowLiveThemeSwitchWithoutRebuild()
    {
        // 契约 #24 修订锁定：插件行是代码构建的，画刷必须经动态资源引用解析——
        // 构建后活切主题，卡片底/描边/标题/描述跟随新字典；清单非法行描述
        // 恒走 DangerBrush（键随行状态、值随字典换装）。
        using var directory = new TestDirectory();
        var catalog = CreateCatalog(directory, brokenPluginId: "broken-one");
        var window = CreateWindow(directory, new RecordingPreferencesStore(), catalog);
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            AssertPluginRowBrushesFollowLiveThemeSwitch(settings);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    /// <summary>深色实时适配发布证据（FTS_PLUGIN_SETTINGS_EVIDENCE_DIR 设置时产出
    /// 真实 WPF 渲染截图）：「面板 → 标签顺序」节与「数据与维护 → 插件」区块各一张
    /// 深色截图，画面证明代码构建行画刷已跟随深色字典（Issue #140）。</summary>
    [STATestMethod]
    public void Evidence_CaptureDarkSectionsFollowTheme()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("FTS_PLUGIN_SETTINGS_EVIDENCE_DIR")))
        {
            Assert.Inconclusive("未设置 FTS_PLUGIN_SETTINGS_EVIDENCE_DIR，跳过发布证据产出。");
        }

        using var directory = new TestDirectory();
        var catalog = CreateCatalog(directory);
        var window = CreateWindow(directory, new RecordingPreferencesStore(), catalog);
        try
        {
            var settings = OpenSettings(window);
            DesignThemeManager.Apply(settings, DesignTheme.Dark);
            CompleteLayout(settings);

            var panelNav = (RadioButton)settings.FindName("SettingsNavPanel")!;
            panelNav.IsChecked = true;
            CompleteLayout(settings);
            ((Border)((StackPanel)settings.FindName("CategoryOrderList")!).Children[0]).BringIntoView();
            CompleteLayout(settings);
            SaveVisualEvidence(settings, "settings-category-order-dark.png");

            var dataNav = (RadioButton)settings.FindName("SettingsNavData")!;
            dataNav.IsChecked = true;
            CompleteLayout(settings);
            ((StackPanel)settings.FindName("PluginListPanel")!).Children.OfType<Border>().First().BringIntoView();
            CompleteLayout(settings);
            SaveVisualEvidence(settings, "settings-plugins-dark.png");
        }
        finally
        {
            CloseLeftoverSettings(window);
        }
    }

    /// <summary>对合法行与清单非法行断言两组主题的画刷跟随：与 FindResource 结果同例，且两主题实例互异。</summary>
    private static void AssertPluginRowBrushesFollowLiveThemeSwitch(SettingsWindow settings)
    {
        var panel = (StackPanel)settings.FindName("PluginListPanel")!;
        Assert.AreEqual(2, panel.Children.Count, "前置条件：一个合法插件行 + 一个清单非法插件行。");
        // 目录条目顺序不保证（既有 BrokenPlugin 用例同款判别）：按开关 Tag 区分两行。
        var surfaces = panel.Children.OfType<Border>().ToList();
        var validSurface = surfaces.Single(
            surface => ((Grid)surface.Child).Children.OfType<CheckBox>().Single().Tag as string == "demo-tidy");
        var brokenSurface = surfaces.Single(surface => surface != validSurface);
        var validText = ((Grid)validSurface.Child).Children.OfType<StackPanel>().First();
        var brokenText = ((Grid)brokenSurface.Child).Children.OfType<StackPanel>().First();
        var validTitle = validText.Children.OfType<TextBlock>().First();
        var validDetail = validText.Children.OfType<TextBlock>().Last();
        var brokenTitle = brokenText.Children.OfType<TextBlock>().First();
        var brokenDetail = brokenText.Children.OfType<TextBlock>().Last();

        var initial = CurrentThemeIsDark(settings) ? DesignTheme.Dark : DesignTheme.Light;
        var other = initial == DesignTheme.Dark ? DesignTheme.Light : DesignTheme.Dark;

        DesignThemeManager.Apply(settings, other);
        CompleteLayout(settings);
        var otherCard = (System.Windows.Media.Brush)settings.FindResource("CardBrush");
        var otherBorder = (System.Windows.Media.Brush)settings.FindResource("BorderBrush");
        var otherPrimary = (System.Windows.Media.Brush)settings.FindResource("PrimaryTextBrush");
        var otherSecondary = (System.Windows.Media.Brush)settings.FindResource("SecondaryTextBrush");
        var otherDanger = (System.Windows.Media.Brush)settings.FindResource("DangerBrush");
        Assert.AreSame(otherCard, validSurface.Background, $"切到 {other} 后合法行卡片底必须解析到新主题 CardBrush。");
        Assert.AreSame(otherBorder, validSurface.BorderBrush, $"切到 {other} 后合法行描边必须解析到新主题 BorderBrush。");
        Assert.AreSame(otherPrimary, validTitle.Foreground, $"切到 {other} 后插件标题必须解析到新主题 PrimaryTextBrush。");
        Assert.AreSame(otherSecondary, validDetail.Foreground, $"切到 {other} 后合法行描述必须解析到新主题 SecondaryTextBrush。");
        Assert.AreSame(otherCard, brokenSurface.Background, $"切到 {other} 后非法行卡片底必须解析到新主题 CardBrush。");
        Assert.AreSame(otherBorder, brokenSurface.BorderBrush, $"切到 {other} 后非法行描边必须解析到新主题 BorderBrush。");
        Assert.AreSame(otherPrimary, brokenTitle.Foreground, $"切到 {other} 后非法行标题必须解析到新主题 PrimaryTextBrush。");
        Assert.AreSame(otherDanger, brokenDetail.Foreground, $"切到 {other} 后非法行描述必须解析到新主题 DangerBrush。");

        DesignThemeManager.Apply(settings, initial);
        CompleteLayout(settings);
        var initialCard = (System.Windows.Media.Brush)settings.FindResource("CardBrush");
        var initialBorder = (System.Windows.Media.Brush)settings.FindResource("BorderBrush");
        var initialPrimary = (System.Windows.Media.Brush)settings.FindResource("PrimaryTextBrush");
        var initialSecondary = (System.Windows.Media.Brush)settings.FindResource("SecondaryTextBrush");
        var initialDanger = (System.Windows.Media.Brush)settings.FindResource("DangerBrush");
        Assert.AreNotSame(otherCard, initialCard, "两主题字典的画刷必须是不同实例（前置 sanity）。");
        Assert.AreSame(initialCard, validSurface.Background, $"切回 {initial} 后合法行卡片底必须跟随。");
        Assert.AreSame(initialBorder, validSurface.BorderBrush);
        Assert.AreSame(initialPrimary, validTitle.Foreground);
        Assert.AreSame(initialSecondary, validDetail.Foreground);
        Assert.AreSame(initialCard, brokenSurface.Background);
        Assert.AreSame(initialBorder, brokenSurface.BorderBrush);
        Assert.AreSame(initialPrimary, brokenTitle.Foreground);
        Assert.AreSame(initialDanger, brokenDetail.Foreground);
    }

    private static bool CurrentThemeIsDark(Window window) =>
        (window.Resources.MergedDictionaries[1].Source?.OriginalString ?? string.Empty)
            .Replace('\\', '/')
            .EndsWith("Resources/DesignTheme.Dark.xaml", StringComparison.Ordinal);

    private sealed class IdleClipboardReader : IClipboardReader
    {
        public Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClipboardSnapshot(0, null, [], null));
    }

    private sealed class RecordingPreferencesStore : IPreferencesStore
    {
        public Task<AppPreferences> LoadPreferencesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(AppPreferences.Default);

        public Task SavePreferencesAsync(
            AppPreferences preferences,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeStartupManager : IStartupManager
    {
        public bool Enabled { get; set; }

        public bool IsEnabled() => Enabled;

        public void Enable() => Enabled = true;

        public void Disable() => Enabled = false;
    }

    private sealed class RecordingSettingsBoardStore(string root) : IBoardStore
    {
        public string ImagesDirectory { get; } = Path.Combine(root, "images");

        public Task<BoardSnapshot> LoadBoardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BoardSnapshot());

        public Task SaveBoardAsync(BoardSnapshot snapshot, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<WindowSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(WindowSettings.Default);

        public Task SaveSettingsAsync(WindowSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public bool TryDeleteImage(string? absolutePath) => true;
    }
}
