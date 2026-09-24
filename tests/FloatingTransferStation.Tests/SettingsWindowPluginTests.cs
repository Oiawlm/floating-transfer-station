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
            var row = (Grid)panel.Children[0];
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
            var toggle = ((Grid)panel.Children[0]).Children.OfType<CheckBox>().Single();

            toggle.IsChecked = true;
            PumpDispatcherUntil(
                settings.Dispatcher,
                () => catalog.TextCleaner.RuleCount == 1);

            Assert.AreEqual("好的", catalog.TextCleaner.Apply("ok"));
            SaveVisualEvidence(settings, "plugin-section-enabled.png");

            var paths = AppPaths.FromDataDirectory(directory.Root);
            var reloaded = new PluginCatalog(paths, null, new AtomicTextWriter());
            reloaded.LoadAsync().GetAwaiter().GetResult();
            Assert.IsTrue(reloaded.Entries.Single(entry => entry.Id == "demo-tidy").Enabled);

            toggle.IsChecked = false;
            PumpDispatcherUntil(
                settings.Dispatcher,
                () => catalog.TextCleaner.RuleCount == 0);
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
                .OfType<Grid>()
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
