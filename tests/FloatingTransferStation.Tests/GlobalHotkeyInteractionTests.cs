using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class GlobalHotkeyInteractionTests
{
    private const int WmHotKey = 0x0312;

    private static MainWindow CreateWindow(
        TestDirectory directory,
        RecordingHotkeyPreferencesStore preferencesStore,
        FakeGlobalHotkeySource hotkeySource,
        AppPreferences? preferences = null)
    {
        var store = new RecordingHotkeyBoardStore(directory.Root);
        var board = new BoardService();
        var normalizer = new ImageNormalizer(store.ImagesDirectory);
        var operationGate = new BoardOperationGate();
        MainWindow? window = null;
        void ShowStatus(string message) => window?.ShowStatus(message);
        var clipboard = new ClipboardCaptureService(
            new IdleHotkeyClipboardReader(),
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
            preferences: preferences,
            preferencesStore: preferencesStore,
            startupManager: new FakeHotkeyStartupManager(),
            dataDirectory: directory.Root,
            globalHotkeySource: hotkeySource);
        return window;
    }

    private static SettingsWindow OpenSettings(MainWindow window)
    {
        window.Show();
        var gear = window.FindName("SettingsButton") as Button;
        Assert.IsNotNull(gear);
        gear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var field = typeof(MainWindow).GetField(
            "_settingsWindow",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        var settings = (SettingsWindow?)field.GetValue(window);
        Assert.IsNotNull(settings);
        return settings;
    }

    private static MainWindowViewModel ViewModel(MainWindow window)
    {
        var field = typeof(MainWindow).GetField(
            "_viewModel",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        return (MainWindowViewModel)field.GetValue(window)!;
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
                var timeout = new DispatcherTimer(DispatcherPriority.Send, window.Dispatcher)
                {
                    Interval = TimeSpan.FromSeconds(5)
                };
                timeout.Tick += (_, _) =>
                {
                    timeout.Stop();
                    frame.Continue = false;
                };
                _ = closed.Task.ContinueWith(
                    _ => window.Dispatcher.BeginInvoke(
                        DispatcherPriority.Send,
                        new Action(() => frame.Continue = false)),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
                timeout.Start();
                Dispatcher.PushFrame(frame);
                timeout.Stop();
            }

            closed.Task.GetAwaiter().GetResult();
        }
        finally
        {
            window.Closed -= closedHandler;
        }
    }

    [STATestMethod]
    public void HotkeyPreference_OffByDefault_NeverRegistersAndToggleShowsOff()
    {
        using var directory = new TestDirectory();
        var hotkey = new FakeGlobalHotkeySource();
        var window = CreateWindow(directory, new RecordingHotkeyPreferencesStore(), hotkey);

        try
        {
            var settings = OpenSettings(window);
            var toggle = settings.FindName("GlobalHotkeyToggle") as CheckBox;
            Assert.IsNotNull(toggle);
            Assert.IsFalse(toggle.IsChecked!.Value, "默认关闭,不应注册任何系统热键。");
            Assert.AreEqual(0, hotkey.RegisterCalls);

            CloseWindow(settings);
        }
        finally
        {
            DesignThemeManager.PreviewOverride = null;
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void HotkeyToggle_OnRegistersAndPersists_OffUnregisters()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingHotkeyPreferencesStore();
        var hotkey = new FakeGlobalHotkeySource();
        var window = CreateWindow(directory, preferencesStore, hotkey);

        try
        {
            var settings = OpenSettings(window);
            var toggle = settings.FindName("GlobalHotkeyToggle") as CheckBox;
            Assert.IsNotNull(toggle);

            toggle.IsChecked = true;
            Assert.AreEqual(1, hotkey.RegisterCalls, "开启开关应立即注册系统热键。");
            Assert.IsTrue(toggle.IsChecked.Value);
            Assert.IsTrue(preferencesStore.LastSaved!.GlobalHotkeyEnabled);

            toggle.IsChecked = false;
            Assert.AreEqual(1, hotkey.UnregisterCalls, "关闭开关应立即注销系统热键。");
            Assert.IsFalse(toggle.IsChecked.Value);
            Assert.IsFalse(preferencesStore.LastSaved!.GlobalHotkeyEnabled);

            CloseWindow(settings);
        }
        finally
        {
            DesignThemeManager.PreviewOverride = null;
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void HotkeyToggle_RegistrationFailureRevertsToggleShowsStatusAndSkipsPreference()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingHotkeyPreferencesStore();
        var hotkey = new FakeGlobalHotkeySource { FailRegistration = true };
        var window = CreateWindow(directory, preferencesStore, hotkey);

        try
        {
            var settings = OpenSettings(window);
            var toggle = settings.FindName("GlobalHotkeyToggle") as CheckBox;
            Assert.IsNotNull(toggle);

            toggle.IsChecked = true;
            Assert.AreEqual(1, hotkey.RegisterCalls);
            Assert.IsFalse(toggle.IsChecked.Value, "注册失败的开启请求必须回退为关闭。");
            Assert.IsNull(preferencesStore.LastSaved, "注册失败不得把开启意图写入偏好。");

            var status = settings.FindName("GlobalHotkeyStatusText") as TextBlock;
            Assert.IsNotNull(status);
            Assert.AreEqual(Visibility.Visible, status.Visibility);
            StringAssert.Contains(status.Text, "全局快捷键注册失败");

            CloseWindow(settings);
        }
        finally
        {
            DesignThemeManager.PreviewOverride = null;
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void StartupWithHotkeyPreference_RegistersOnceAndUnregistersOnClose()
    {
        using var directory = new TestDirectory();
        var hotkey = new FakeGlobalHotkeySource();
        var window = CreateWindow(
            directory,
            new RecordingHotkeyPreferencesStore(),
            hotkey,
            preferences: new AppPreferences(GlobalHotkeyEnabled: true));

        try
        {
            window.Show();
            CompleteLayout(window);
            Assert.AreEqual(1, hotkey.RegisterCalls, "偏好开启时启动应注册系统热键。");
        }
        finally
        {
            DesignThemeManager.PreviewOverride = null;
            CloseWindow(window);
            Assert.AreEqual(1, hotkey.UnregisterCalls, "关闭窗口应注销已注册的系统热键。");
        }
    }

    [STATestMethod]
    public void HotkeyMessage_ExpandsPanelToDefaultCategory_IgnoringMismatchedIds()
    {
        using var directory = new TestDirectory();
        var hotkey = new FakeGlobalHotkeySource();
        var window = CreateWindow(
            directory,
            new RecordingHotkeyPreferencesStore(),
            hotkey,
            preferences: new AppPreferences(GlobalHotkeyEnabled: true));

        try
        {
            window.Show();
            CompleteLayout(window);
            var viewModel = ViewModel(window);
            Assert.IsFalse(viewModel.IsPanelExpanded);

            // 其他软件的热键 id 不得触发本应用。
            var wndProc = typeof(MainWindow).GetMethod(
                "WndProc",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(wndProc);
            _ = (nint)wndProc.Invoke(window, [nint.Zero, WmHotKey, (nint)999, nint.Zero, false])!;
            CompleteLayout(window);
            Assert.IsFalse(viewModel.IsPanelExpanded);

            _ = (nint)wndProc.Invoke(
                window,
                [nint.Zero, WmHotKey, (nint)Win32GlobalHotkeySource.HotkeyId, nint.Zero, false])!;
            CompleteLayout(window);
            Assert.IsTrue(viewModel.IsPanelExpanded, "本应用热键消息应唤起面板。");

            // 已展开时再次触发保持现状(幂等,不收起)。
            window.OnGlobalHotkeyPressed();
            CompleteLayout(window);
            Assert.IsTrue(viewModel.IsPanelExpanded);
        }
        finally
        {
            DesignThemeManager.PreviewOverride = null;
            CloseWindow(window);
        }
    }

    private sealed class IdleHotkeyClipboardReader : IClipboardReader
    {
        public Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClipboardSnapshot(0, null, [], null));
    }

    private sealed class FakeGlobalHotkeySource : IGlobalHotkeySource
    {
        public int RegisterCalls { get; private set; }
        public int UnregisterCalls { get; private set; }
        public bool FailRegistration { get; set; }

        public bool TryRegister(nint windowHandle, int hotkeyId)
        {
            RegisterCalls++;
            return !FailRegistration;
        }

        public void Unregister(nint windowHandle, int hotkeyId) => UnregisterCalls++;
    }

    private sealed class RecordingHotkeyPreferencesStore : IPreferencesStore
    {
        public AppPreferences? LastSaved { get; private set; }

        public Task<AppPreferences> LoadPreferencesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(AppPreferences.Default);

        public Task SavePreferencesAsync(
            AppPreferences preferences,
            CancellationToken cancellationToken = default)
        {
            LastSaved = preferences;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHotkeyStartupManager : IStartupManager
    {
        public bool IsEnabled() => false;

        public void Enable()
        {
        }

        public void Disable()
        {
        }
    }

    private sealed class RecordingHotkeyBoardStore(string root) : IBoardStore
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
