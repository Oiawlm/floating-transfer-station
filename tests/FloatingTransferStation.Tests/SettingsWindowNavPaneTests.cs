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
/// 设置窗口双栏外壳（契约 #24）：左导航 5 组的冻结顺序、SelectionItem 语义、
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

    private static readonly string[] PageNames =
        ["AppearancePage", "PanelPage", "GeneralPage", "DataPage", "AiPage"];

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

    [STATestMethod]
    public void NavPane_AllPagesRenderAcrossBothThemes()
    {
        var (window, settings) = OpenSettings();
        try
        {
            var nav = AssertNavList(settings);
            var items = nav.Children.OfType<RadioButton>().ToArray();
            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                window.ApplyPreferences(window.CurrentPreferences with { ThemeMode = theme });
                CompleteLayout(settings);
                for (var index = 0; index < items.Length; index++)
                {
                    items[index].IsChecked = true;
                    CompleteLayout(settings);
                    var anchor = (FrameworkElement)settings.FindName(FrozenPages[index].AnchorName)!;
                    Assert.IsTrue(
                        anchor.IsVisible,
                        $"{theme} 主题下第 {index} 页（{FrozenPages[index].AutomationName}）锚定控件必须可见。");
                    Assert.IsTrue(items[index].IsChecked == true);
                }
            }
        }
        finally
        {
            window.ApplyPreferences(window.CurrentPreferences with { ThemeMode = ThemePreference.FollowSystem });
            CloseAll(window, settings);
        }
    }

    [STATestMethod]
    public void NavPane_PageSwitchWithAnimationsDisabledIsInstant()
    {
        var preferences = AppPreferences.Default with { AnimationsEnabled = false };
        var (window, settings) = OpenSettings(preferences);
        try
        {
            CompleteLayout(settings);
            var nav = AssertNavList(settings);
            var items = nav.Children.OfType<RadioButton>().ToArray();
            Keyboard.Focus(items[0]);
            SendNavKey(nav, Key.Down);
            var anchor = (FrameworkElement)settings.FindName(FrozenPages[1].AnchorName)!;
            Assert.IsTrue(anchor.IsVisible, "动效关闭时页切换必须即时可见（无淡入等待）。");
            Assert.AreEqual(
                1d,
                items[1].Opacity,
                0.001,
                "动效关闭路径不得留下中间透明度。");
            var page = (FrameworkElement)settings.FindName("PanelPage")!;
            Assert.AreEqual(1d, page.Opacity, 0.001, "动效关闭时入场页透明度必须直接就位。");
        }
        finally
        {
            CloseAll(window, settings);
        }
    }

    [STATestMethod]
    public void NavPane_DownArrowWrapsForwardFromLastItemAndFocusFollowsSelection()
    {
        var (window, settings) = OpenSettings();
        try
        {
            CompleteLayout(settings);
            var nav = AssertNavList(settings);
            var items = nav.Children.OfType<RadioButton>().ToArray();
            Keyboard.Focus(items[^1]);
            SendNavKey(nav, Key.End);
            SendNavKey(nav, Key.Down);
            Assert.IsTrue(items[0].IsChecked == true, "末项按 ↓ 应正向回绕到首项。");
            Assert.IsTrue(((FrameworkElement)settings.FindName(FrozenPages[0].AnchorName)!).IsVisible, "正向回绕须同步切页。");
            Assert.AreEqual(
                items[0],
                Keyboard.FocusedElement,
                "键盘焦点必须跟随选中项（roving 语义）。");
        }
        finally
        {
            CloseAll(window, settings);
        }
    }

    [STATestMethod]
    public void NavPane_PagesAreMutuallyExclusiveWithSingleSharedViewport()
    {
        var (window, settings) = OpenSettings();
        try
        {
            CompleteLayout(settings);
            var nav = AssertNavList(settings);
            var items = nav.Children.OfType<RadioButton>().ToArray();
            var pages = PageNames.Select(name => (FrameworkElement)settings.FindName(name)!).ToArray();

            var scroll = settings.FindName("SettingsScrollHost") as ScrollViewer;
            Assert.IsNotNull(scroll, "右列必须有共享滚动视口 SettingsScrollHost。");
            var pagePanel = (FrameworkElement)((FrameworkElement)settings.FindName(PageNames[0])!).Parent;
            var nestedScrollViewers = FindDescendants<ScrollViewer>(pagePanel).Count();
            Assert.AreEqual(
                0,
                nestedScrollViewers,
                "内容页内不得再包 ScrollViewer（共享视口唯一，页内套滚动是漂移信号）。");

            for (var index = 0; index < items.Length; index++)
            {
                items[index].IsChecked = true;
                CompleteLayout(settings);
                for (var page = 0; page < pages.Length; page++)
                {
                    Assert.AreEqual(
                        page == index ? Visibility.Visible : Visibility.Collapsed,
                        pages[page].Visibility,
                        $"选中第 {index} 页时第 {page} 页可见性必须互斥（Collapsed 而非 Hidden）。");
                }
            }
        }
        finally
        {
            CloseAll(window, settings);
        }
    }

    [STATestMethod]
    public void NavPane_InitialPageIsPlacedWithoutAnimationAndSwitchAnimatesOpacityOnly()
    {
        // 构造期直接就位（动效默认开）：首屏 Opacity==1、无变换残留。
        var (window, settings) = OpenSettings();
        try
        {
            CompleteLayout(settings);
            var appearance = (FrameworkElement)settings.FindName(PageNames[0])!;
            Assert.AreEqual(1d, appearance.Opacity, 0.001, "构造期首屏必须直接就位（无淡入中间态）。");
            Assert.IsTrue(
                appearance.RenderTransform is null
                || appearance.RenderTransform.Value == System.Windows.Media.Matrix.Identity,
                "构造期首屏不得残留变换。");
            Assert.IsTrue(
                appearance.LayoutTransform is null
                || appearance.LayoutTransform == System.Windows.Media.Transform.Identity,
                "构造期首屏不得残留变换。");

            // 动效开切页：可见性即时生效（UIA/命中不等动画收尾）、只动 Opacity（无变换动画）。
            var nav = AssertNavList(settings);
            var items = nav.Children.OfType<RadioButton>().ToArray();
            Keyboard.Focus(items[0]);
            SendNavKey(nav, Key.Down);
            var panel = (FrameworkElement)settings.FindName(PageNames[1])!;
            Assert.IsTrue(panel.IsVisible, "动效开时入场页可见性必须即时生效。");
            Assert.IsTrue(
                panel.RenderTransform is null
                || panel.RenderTransform.Value == System.Windows.Media.Matrix.Identity,
                "页切换只允许 Opacity 动画，不得引入变换动画。");
        }
        finally
        {
            CloseAll(window, settings);
        }
    }

    [STATestMethod]
    public void NavPane_SwitchingPagesResetsSharedScrollOffset()
    {
        var (window, settings) = OpenSettings();
        try
        {
            CompleteLayout(settings);
            var scroll = settings.FindName("SettingsScrollHost") as ScrollViewer;
            Assert.IsNotNull(scroll);
            // 面板页在矮窗口下才溢出：收缩到最小高后滚到底，切走再切回，偏移必须归零。
            settings.Height = 460;
            var panelNav = (RadioButton)settings.FindName("SettingsNavPanel");
            panelNav.IsChecked = true;
            CompleteLayout(settings);
            Assert.IsTrue(scroll.ScrollableHeight > 0, "前提：面板页在最小高下可滚动。");
            scroll.ScrollToEnd();
            CompleteLayout(settings);
            Assert.IsTrue(scroll.VerticalOffset > 0);

            var generalNav = (RadioButton)settings.FindName("SettingsNavGeneral");
            generalNav.IsChecked = true;
            CompleteLayout(settings);
            generalNav.IsChecked = true;
            var backNav = (RadioButton)settings.FindName("SettingsNavPanel");
            backNav.IsChecked = true;
            CompleteLayout(settings);
            Assert.AreEqual(
                0d,
                scroll.VerticalOffset,
                0.01,
                "共享视口的滚动偏移不得跨页残留。");
        }
        finally
        {
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

    private static (MainWindow Window, SettingsWindow Settings) OpenSettings(AppPreferences? preferences = null)
    {
        var directory = new TestDirectory();
        var store = new RecordingPreferencesStore();
        var startup = new FakeStartupManager();
        var window = CreateWindow(directory, store, startup, preferences);
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

    private static IEnumerable<T> FindDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindDescendants<T>(child))
            {
                yield return descendant;
            }
        }
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
