using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 设置窗口双栏外壳（契约 #23）：左导航 5 组的冻结顺序、SelectionItem 语义、
/// ↑/↓/Home/End 键盘切换、Tab 可达性与双主题渲染。页锚定控件按冻结表：
/// 外观=ThemeComboBox、面板=CategoryOrderHost、通用=StartupToggle、
/// 数据与维护=AutoCleanupToggle、AI=DeepSeekKeyBox。
/// </summary>
[TestClass]
public sealed class SettingsWindowNavPaneTests
{
    private static readonly (string AutomationName, string AnchorName)[] FrozenPages =
    [
        ("外观", "ThemeComboBox"),
        ("面板", "CategoryOrderHost"),
        ("通用", "StartupToggle"),
        ("数据与维护", "AutoCleanupToggle"),
        ("AI", "DeepSeekKeyBox")
    ];

    [STATestMethod]
    public void NavPane_ExposesFiveItemsInFrozenOrderWithSelectionSemantics()
    {
        var (window, settings) = OpenSettings();
        try
        {
            CompleteLayout(settings);
            var nav = AssertNavList(settings);
            var items = nav.Children.OfType<RadioButton>().ToArray();
            Assert.AreEqual(FrozenPages.Length, items.Length, "导航项数量必须与冻结表一致。");
            for (var index = 0; index < FrozenPages.Length; index++)
            {
                Assert.AreEqual(
                    FrozenPages[index].AutomationName,
                    AutomationProperties.GetName(items[index]),
                    $"导航第 {index} 项必须按冻结表命名。");
                var provider = AssertSelectionItemProvider(items[index]);
                Assert.IsTrue(provider.IsSelected == (index == 0), $"仅首项（外观）应默认选中，第 {index} 项不符。");
            }
        }
        finally
        {
            CloseAll(window, settings);
        }
    }

    [STATestMethod]
    public void NavPane_DownArrowSwitchesSelectionAndPageAnchorVisibility()
    {
        var (window, settings) = OpenSettings();
        try
        {
            CompleteLayout(settings);
            var nav = AssertNavList(settings);
            var items = nav.Children.OfType<RadioButton>().ToArray();
            Keyboard.Focus(items[0]);
            Assert.IsTrue(((FrameworkElement)settings.FindName(FrozenPages[0].AnchorName)!).IsVisible);

            SendNavKey(nav, Key.Down);
            Assert.IsTrue(items[1].IsChecked == true, "↓ 应把选中推进到第二项。");
            Assert.IsFalse(((FrameworkElement)settings.FindName(FrozenPages[0].AnchorName)!).IsVisible);
            Assert.IsTrue(((FrameworkElement)settings.FindName(FrozenPages[1].AnchorName)!).IsVisible);

            SendNavKey(nav, Key.Down);
            Assert.IsTrue(items[2].IsChecked == true, "↓ 应继续推进到第三项。");
            Assert.IsTrue(((FrameworkElement)settings.FindName(FrozenPages[2].AnchorName)!).IsVisible);
        }
        finally
        {
            CloseAll(window, settings);
        }
    }

    [STATestMethod]
    public void NavPane_UpArrowWrapsBackwardsFromFirstItem()
    {
        var (window, settings) = OpenSettings();
        try
        {
            CompleteLayout(settings);
            var nav = AssertNavList(settings);
            var items = nav.Children.OfType<RadioButton>().ToArray();
            Keyboard.Focus(items[0]);
            SendNavKey(nav, Key.Up);
            Assert.IsTrue(items[^1].IsChecked == true, "首项按 ↑ 应循环回末项。");
            Assert.IsTrue(((FrameworkElement)settings.FindName(FrozenPages[^1].AnchorName)!).IsVisible);
        }
        finally
        {
            CloseAll(window, settings);
        }
    }

    [STATestMethod]
    public void NavPane_HomeAndEndJumpToFirstAndLast()
    {
        var (window, settings) = OpenSettings();
        try
        {
            CompleteLayout(settings);
            var nav = AssertNavList(settings);
            var items = nav.Children.OfType<RadioButton>().ToArray();
            Keyboard.Focus(items[0]);
            SendNavKey(nav, Key.End);
            Assert.IsTrue(items[^1].IsChecked == true, "End 应选中末项（AI）。");
            SendNavKey(nav, Key.Home);
            Assert.IsTrue(items[0].IsChecked == true, "Home 应回到首项（外观）。");
            Assert.IsTrue(((FrameworkElement)settings.FindName(FrozenPages[0].AnchorName)!).IsVisible);
        }
        finally
        {
            CloseAll(window, settings);
        }
    }

    [STATestMethod]
    public void NavPane_KeyboardTabReachesNavigation()
    {
        var (window, settings) = OpenSettings();
        try
        {
            CompleteLayout(settings);
            var nav = AssertNavList(settings);
            var items = nav.Children.OfType<RadioButton>().ToArray();
            settings.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            var reached = false;
            for (var stops = 0; stops < 12 && !reached; stops++)
            {
                if (Keyboard.FocusedElement is RadioButton radio && items.Contains(radio))
                {
                    reached = true;
                }
                else
                {
                    settings.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                }
            }

            Assert.IsTrue(reached, "Tab 序应可达左导航项（头部关闭钮 Focusable=False 不占 Tab）。");
        }
        finally
        {
            CloseAll(window, settings);
        }
    }

    [STATestMethod]
    public void NavPane_AutomationPeerSelectionItemPatternSwitchesPages()
    {
        var (window, settings) = OpenSettings();
        try
        {
            CompleteLayout(settings);
            var nav = AssertNavList(settings);
            var items = nav.Children.OfType<RadioButton>().ToArray();

            var provider = AssertSelectionItemProvider(items[3]);
            provider.Select();
            CompleteLayout(settings);
            Assert.IsTrue(items[3].IsChecked == true, "UIA Select() 应选中「数据与维护」。");
            Assert.IsTrue(((FrameworkElement)settings.FindName(FrozenPages[3].AnchorName)!).IsVisible);
            Assert.IsFalse(((FrameworkElement)settings.FindName(FrozenPages[0].AnchorName)!).IsVisible);
        }
        finally
        {
            CloseAll(window, settings);
        }
    }

    [STATestMethod]
    public void NavPane_BothThemesApplyWithNavPaneIntact()
    {
        var (window, settings) = OpenSettings();
        try
        {
            window.ApplyPreferences(window.CurrentPreferences with { ThemeMode = ThemePreference.Dark });
            CompleteLayout(settings);
            Assert.IsTrue(
                settings.Resources.MergedDictionaries[1].Source!.OriginalString.EndsWith(
                    "DesignTheme.Dark.xaml",
                    StringComparison.OrdinalIgnoreCase),
                "深色主题字典应整本替换生效。");
            var nav = AssertNavList(settings);
            Assert.IsTrue(nav.IsVisible, "深色下导航轨保持可见。");
            Assert.IsTrue(((FrameworkElement)settings.FindName(FrozenPages[0].AnchorName)!).IsVisible);

            window.ApplyPreferences(window.CurrentPreferences with { ThemeMode = ThemePreference.Light });
            CompleteLayout(settings);
            Assert.IsTrue(
                settings.Resources.MergedDictionaries[1].Source!.OriginalString.EndsWith(
                    "DesignTheme.Light.xaml",
                    StringComparison.OrdinalIgnoreCase),
                "浅色主题字典应整本替换生效。");
            Assert.IsTrue(((FrameworkElement)settings.FindName(FrozenPages[0].AnchorName)!).IsVisible);
        }
        finally
        {
            window.ApplyPreferences(window.CurrentPreferences with { ThemeMode = ThemePreference.FollowSystem });
            CloseAll(window, settings);
        }
    }

    // ---- 基建（按本测试套既有各文件自持 helper 的模式复制）----

    private static MainWindow CreateWindow(
        TestDirectory directory,
        RecordingPreferencesStore preferencesStore,
        FakeStartupManager startupManager,
        AppPreferences? preferences = null)
    {
        var store = new RecordingSettingsBoardStore(directory.Root);
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
            preferences: preferences,
            preferencesStore: preferencesStore,
            startupManager: startupManager,
            dataDirectory: directory.Root);
        return window;
    }

    private static (MainWindow Window, SettingsWindow Settings) OpenSettings()
    {
        var directory = new TestDirectory();
        var store = new RecordingPreferencesStore();
        var startup = new FakeStartupManager();
        var window = CreateWindow(directory, store, startup);
        window.Show();
        var gear = window.FindName("SettingsButton") as Button;
        Assert.IsNotNull(gear);
        gear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var field = typeof(MainWindow).GetField("_settingsWindow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        var settings = (SettingsWindow?)field.GetValue(window);
        Assert.IsNotNull(settings);
        return (window, settings);
    }

    private static StackPanel AssertNavList(SettingsWindow settings)
    {
        var nav = settings.FindName("SettingsNavList") as StackPanel;
        Assert.IsNotNull(nav, "设置窗必须有左导航容器 SettingsNavList。");
        return nav;
    }

    private static ISelectionItemProvider AssertSelectionItemProvider(RadioButton item)
    {
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(item);
        Assert.IsNotNull(peer, "导航项必须有 AutomationPeer（§四.5 UIA 判据）。");
        var provider = peer.GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider;
        Assert.IsNotNull(provider, "导航项必须暴露 SelectionItem 语义。");
        return provider;
    }

    private static void SendNavKey(StackPanel nav, Key key)
    {
        var source = PresentationSource.FromDependencyObject(nav);
        Assert.IsNotNull(source, "导航容器必须已接入呈现源（窗口已显示）。");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = UIElement.PreviewKeyDownEvent
        };
        nav.RaiseEvent(args);
        Assert.IsTrue(args.Handled, $"{key} 应被导航容器消费（确定性键盘切换）。");
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

    private static void CloseAll(MainWindow window, SettingsWindow settings)
    {
        settings.Close();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler handler = (_, _) => closed.TrySetResult();
        window.Closed += handler;
        try
        {
            window.Close();
        }
        finally
        {
            window.Closed -= handler;
        }
    }

    private sealed class IdleClipboardReader : IClipboardReader
    {
        public Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClipboardSnapshot(0, null, [], null));
    }

    private sealed class RecordingPreferencesStore : IPreferencesStore
    {
        public AppPreferences? LastSaved { get; private set; }
        public AppPreferences Seeded { get; set; } = AppPreferences.Default;

        public Task<AppPreferences> LoadPreferencesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Seeded);

        public Task SavePreferencesAsync(
            AppPreferences preferences,
            CancellationToken cancellationToken = default)
        {
            LastSaved = preferences;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStartupManager : IStartupManager
    {
        public int EnableCount { get; private set; }
        public int DisableCount { get; private set; }
        public bool Enabled { get; set; }

        public bool IsEnabled() => Enabled;

        public void Enable()
        {
            EnableCount++;
            Enabled = true;
        }

        public void Disable()
        {
            DisableCount++;
            Enabled = false;
        }
    }

    private sealed class RecordingSettingsBoardStore(string root) : IBoardStore
    {
        public int SettingsSaveCount { get; private set; }
        public string ImagesDirectory { get; } = Path.Combine(root, "images");

        public Task<BoardSnapshot> LoadBoardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BoardSnapshot());

        public Task SaveBoardAsync(BoardSnapshot snapshot, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<WindowSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(WindowSettings.Default);

        public Task SaveSettingsAsync(WindowSettings settings, CancellationToken cancellationToken = default)
        {
            SettingsSaveCount++;
            return Task.CompletedTask;
        }

        public bool TryDeleteImage(string? absolutePath) => true;
    }
}
