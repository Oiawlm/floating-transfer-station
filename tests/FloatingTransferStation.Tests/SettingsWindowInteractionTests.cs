using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class SettingsWindowInteractionTests
{
    private static MainWindow CreateWindow(
        TestDirectory directory,
        RecordingPreferencesStore preferencesStore,
        FakeStartupManager startupManager,
        AppPreferences? preferences = null,
        RecordingSettingsBoardStore? store = null)
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
            preferences: preferences,
            preferencesStore: preferencesStore,
            startupManager: startupManager,
            dataDirectory: directory.Root);
        return window;
    }

    private static SettingsWindow OpenSettings(MainWindow window)
    {
        window.Show();
        var gear = window.FindName("SettingsButton") as Button;
        Assert.IsNotNull(gear);
        gear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var settings = GetPrivateField<SettingsWindow>(window, "_settingsWindow");
        Assert.IsNotNull(settings);
        return settings;
    }

    private static void CloseLeftoverSettingsWindow(MainWindow window)
    {
        if (GetPrivateField<SettingsWindow?>(window, "_settingsWindow") is { } leftover)
        {
            leftover.Close();
        }
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
            PumpDispatcherUntil(window.Dispatcher, closed.Task);
        }
        finally
        {
            window.Closed -= closedHandler;
        }
    }

    private static void PumpDispatcherUntil(Dispatcher dispatcher, Task task)
    {
        if (!task.IsCompleted)
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
            _ = task.ContinueWith(
                _ => dispatcher.BeginInvoke(
                    DispatcherPriority.Send,
                    new Action(() => frame.Continue = false)),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            timeout.Start();
            Dispatcher.PushFrame(frame);
            timeout.Stop();
            if (timedOut && !task.IsCompleted)
            {
                throw new TimeoutException("The dispatcher operation did not complete within five seconds.");
            }
        }

        task.GetAwaiter().GetResult();
    }

    private static void InvokePrivate(MainWindow window, string methodName, params object?[] arguments)
    {
        var method = typeof(MainWindow).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        method.Invoke(window, arguments);
    }

    private static T GetPrivateField<T>(MainWindow window, string fieldName)
    {
        var field = typeof(MainWindow).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        return (T)field.GetValue(window)!;
    }

    private static void SaveVisualEvidence(FrameworkElement visual, string fileName)
    {
        var directory = Environment.GetEnvironmentVariable("FTS_SETTINGS_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(visual.ActualWidth),
            (int)Math.Ceiling(visual.ActualHeight),
            96,
            96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, fileName));
        encoder.Save(stream);
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

    [STATestMethod]
    public void SettingsGearButton_IsTheLeftmostHeaderActionAndOpensTheSettingsWindow()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(
            directory,
            new RecordingPreferencesStore(),
            new FakeStartupManager());

        try
        {
            window.Show();
            CompleteLayout(window);
            var gear = window.FindName("SettingsButton") as Button;
            Assert.IsNotNull(gear);
            Assert.AreEqual("设置", gear.ToolTip);
            Assert.AreEqual("设置", System.Windows.Automation.AutomationProperties.GetName(gear));
            var actions = (StackPanel)window.FindName("HeaderActions");
            var buttons = actions.Children.OfType<Button>().ToArray();
            Assert.AreEqual("PanelHoldButton", buttons[0].Name, "保持展开开关位于操作区最左。");
            Assert.AreEqual("SearchButton", buttons[1].Name, "搜索入口紧随保持展开开关(2026-09-25 设计草案:放大镜在齿轮左侧)。");
            Assert.AreEqual("SettingsButton", buttons[2].Name, "齿轮与红色清空按钮保持距离。");
            Assert.AreEqual("DeleteContentButton", buttons[^1].Name);

            gear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            CompleteLayout(window);

            var settings = GetPrivateField<SettingsWindow>(window, "_settingsWindow");
            Assert.IsNotNull(settings);
            Assert.AreEqual("设置", settings.Title);
            Assert.AreSame(window, settings.Owner);

            // 再次点击置前而不是开第二扇窗。
            gear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.AreSame(settings, GetPrivateField<SettingsWindow>(window, "_settingsWindow"));

            CloseWindow(settings);
        }
        finally
        {
            DesignThemeManager.PreviewOverride = null;
            CloseLeftoverSettingsWindow(window);
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ThemePreference_AppliesImmediatelyAndIgnoresSystemChangesWhenForced()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(
            directory,
            preferencesStore,
            new FakeStartupManager(),
            preferences: new AppPreferences(ThemePreference.Light, AnimationsEnabled: true));

        try
        {
            window.Show();
            CompleteLayout(window);
            Assert.AreEqual(
                DesignTheme.Light,
                GetPrivateField<DesignTheme>(window, "_activeDesignTheme"));
            StringAssert.EndsWith(
                (window.Resources.MergedDictionaries[1].Source?.OriginalString ?? string.Empty)
                    .Replace('\\', '/'),
                "Resources/DesignTheme.Light.xaml");

            var settings = OpenSettings(window);
            window.ApplyPreferences(new AppPreferences(ThemePreference.Dark, AnimationsEnabled: true));
            CompleteLayout(window);

            Assert.AreEqual(DesignTheme.Dark, GetPrivateField<DesignTheme>(window, "_activeDesignTheme"));
            StringAssert.EndsWith(
                (window.Resources.MergedDictionaries[1].Source?.OriginalString ?? string.Empty)
                    .Replace('\\', '/'),
                "Resources/DesignTheme.Dark.xaml");
            StringAssert.EndsWith(
                (settings.Resources.MergedDictionaries[1].Source?.OriginalString ?? string.Empty)
                    .Replace('\\', '/'),
                "Resources/DesignTheme.Dark.xaml",
                "设置窗应跟随主窗主题同步切换。");

            // 强制深色后，系统主题变化广播不得再改动主题。
            var lParam = Marshal.StringToHGlobalUni("ImmersiveColorSet");
            try
            {
                InvokePrivate(window, "WndProc", nint.Zero, 0x001A, nint.Zero, lParam, false);
                CompleteLayout(window);
            }
            finally
            {
                Marshal.FreeHGlobal(lParam);
            }

            Assert.AreEqual(DesignTheme.Dark, GetPrivateField<DesignTheme>(window, "_activeDesignTheme"));
            Assert.AreEqual(
                new AppPreferences(ThemePreference.Dark, AnimationsEnabled: true),
                preferencesStore.LastSaved);

            CloseWindow(settings);
        }
        finally
        {
            DesignThemeManager.PreviewOverride = null;
            CloseLeftoverSettingsWindow(window);
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ThemePreference_FollowSystemStillRespondsToSystemChanges()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(
            directory,
            new RecordingPreferencesStore(),
            new FakeStartupManager(),
            preferences: new AppPreferences(ThemePreference.FollowSystem, AnimationsEnabled: true));

        try
        {
            window.Show();
            CompleteLayout(window);

            // 预览覆盖使 DetectSystemTheme 的结果确定化，不依赖测试机的系统亮暗。
            DesignThemeManager.PreviewOverride = DesignTheme.Dark;
            var lParam = Marshal.StringToHGlobalUni("ImmersiveColorSet");
            try
            {
                InvokePrivate(window, "WndProc", nint.Zero, 0x001A, nint.Zero, lParam, false);
                CompleteLayout(window);
            }
            finally
            {
                Marshal.FreeHGlobal(lParam);
                DesignThemeManager.PreviewOverride = null;
            }

            Assert.AreEqual(DesignTheme.Dark, GetPrivateField<DesignTheme>(window, "_activeDesignTheme"));
            StringAssert.EndsWith(
                (window.Resources.MergedDictionaries[1].Source?.OriginalString ?? string.Empty)
                    .Replace('\\', '/'),
                "Resources/DesignTheme.Dark.xaml");
        }
        finally
        {
            DesignThemeManager.PreviewOverride = null;
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void AnimationsPreference_TogglesTheClientAreaAnimationOverride()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(
            directory,
            preferencesStore,
            new FakeStartupManager());

        try
        {
            window.Show();
            Assert.IsFalse(window.Resources.Contains(SystemParameters.ClientAreaAnimationKey));

            window.ApplyPreferences(new AppPreferences(ThemePreference.FollowSystem, AnimationsEnabled: false));
            Assert.AreEqual(false, window.Resources[SystemParameters.ClientAreaAnimationKey]);
            Assert.IsFalse(window.ClientAreaAnimationsEnabled);

            window.ApplyPreferences(new AppPreferences(ThemePreference.FollowSystem, AnimationsEnabled: true));
            Assert.IsFalse(window.Resources.Contains(SystemParameters.ClientAreaAnimationKey));
            Assert.AreEqual(
                new AppPreferences(ThemePreference.FollowSystem, AnimationsEnabled: true),
                preferencesStore.LastSaved);
        }
        finally
        {
            DesignThemeManager.PreviewOverride = null;
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void StartupToggle_ReadsAndWritesThroughTheStartupManager()
    {
        using var directory = new TestDirectory();
        var startup = new FakeStartupManager();
        var window = CreateWindow(directory, new RecordingPreferencesStore(), startup);

        try
        {
            var settings = OpenSettings(window);
            var toggle = settings.FindName("StartupToggle") as CheckBox;
            Assert.IsNotNull(toggle);
            Assert.IsFalse(toggle.IsChecked!.Value, "未注册自启时开关应显示关闭。");

            toggle.IsChecked = true;
            Assert.AreEqual(1, startup.EnableCount);
            Assert.IsTrue(toggle.IsChecked.Value);

            toggle.IsChecked = false;
            Assert.AreEqual(1, startup.DisableCount);
            Assert.IsFalse(toggle.IsChecked.Value);

            CloseWindow(settings);
        }
        finally
        {
            DesignThemeManager.PreviewOverride = null;
            CloseLeftoverSettingsWindow(window);
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ExitButton_TriggersTheMainWindowFlushAndClosePath()
    {
        using var directory = new TestDirectory();
        var store = new RecordingSettingsBoardStore(directory.Root);
        var window = CreateWindow(directory, new RecordingPreferencesStore(), new FakeStartupManager(), store: store);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();

        var settings = OpenSettings(window);
        var exit = settings.FindName("ExitButton") as Button;
        Assert.IsNotNull(exit);

        exit.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpDispatcherUntil(window.Dispatcher, closed.Task);

        Assert.IsTrue(store.SettingsSaveCount >= 1, "退出必须经过主窗既有冲刷序列保存窗口设置。");
        Assert.IsFalse(window.IsVisible);
    }

    [STATestMethod]
    public void SettingsWindow_RendersInBothThemes()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new RecordingPreferencesStore(), new FakeStartupManager());

        try
        {
            var settings = OpenSettings(window);
            DesignThemeManager.Apply(settings, DesignTheme.Light);
            settings.UpdateLayout();
            SaveVisualEvidence((Border)settings.FindName("WindowShell"), "settings-light.png");

            DesignThemeManager.Apply(settings, DesignTheme.Dark);
            settings.UpdateLayout();
            SaveVisualEvidence((Border)settings.FindName("WindowShell"), "settings-dark.png");

            CloseWindow(settings);
        }
        finally
        {
            DesignThemeManager.PreviewOverride = null;
            CloseLeftoverSettingsWindow(window);
            CloseWindow(window);
        }
    }

    /// <summary>
    /// 设置窗头部拖拽回归：头部 Thumb 的 DragDelta 必须移动窗口且限制在工作区内。
    /// 修复前失败：头部没有任何拖拽区，DragDelta 事件无人处理，窗口位置不变。
    /// </summary>
    [STATestMethod]
    public void HeaderDragRegion_MovesWindowWithinWorkArea()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new RecordingPreferencesStore(), new FakeStartupManager());

        try
        {
            var settings = OpenSettings(window);
            var thumb = settings.FindName("HeaderDragRegion") as System.Windows.Controls.Primitives.Thumb;
            Assert.IsNotNull(thumb, "设置窗头部应有拖拽 Thumb。");

            // 先把窗口拖到工作区左上角（远离右/下钳制边界），再做增量断言；
            // 容差吸收不同缩放的 DPI 物理像素取整；矮屏运行器上窗口可能占满
            // 工作区高度，垂直期望值取「目标位置与钳制上界」的较小者。
            var work = SystemParameters.WorkArea;
            var lowestAllowedTop = Math.Max(work.Top, work.Bottom - settings.Height);
            var dragToLeftTop = new System.Windows.Controls.Primitives.DragDeltaEventArgs(
                (work.Left + 16) - settings.Left,
                (work.Top + 16) - settings.Top);
            thumb.RaiseEvent(dragToLeftTop);
            CompleteLayout(settings);
            Assert.AreEqual(work.Left + 16, settings.Left, 2.5, "拖拽应把窗口移到指定水平位置。");
            Assert.AreEqual(
                Math.Min(work.Top + 16, lowestAllowedTop),
                settings.Top,
                2.5,
                "拖拽应把窗口移到指定垂直位置（或矮屏钳制上界）。");

            // 小增量精确作用到窗口位置。
            thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(60, 40));
            CompleteLayout(settings);
            Assert.AreEqual(work.Left + 76, settings.Left, 2.5, "水平拖拽增量应作用到窗口位置。");
            Assert.AreEqual(
                Math.Min(work.Top + 56, lowestAllowedTop),
                settings.Top,
                2.5,
                "垂直拖拽增量应作用到窗口位置（或矮屏钳制上界）。");

            // 越界增量被工作区钳制，而不是把窗口拖出屏幕。
            thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(1_000_000, 1_000_000));
            CompleteLayout(settings);
            Assert.IsTrue(
                settings.Left >= work.Left - 0.01 && settings.Left <= Math.Max(work.Left, work.Right - settings.Width) + 0.01,
                $"窗口左缘应钳制在工作区内，实际 Left={settings.Left}。");
            Assert.IsTrue(
                settings.Top >= work.Top - 0.01 && settings.Top <= Math.Max(work.Top, work.Bottom - settings.Height) + 0.01,
                $"窗口上缘应钳制在工作区内，实际 Top={settings.Top}。");

            CloseWindow(settings);
        }
        finally
        {
            DesignThemeManager.PreviewOverride = null;
            CloseLeftoverSettingsWindow(window);
            CloseWindow(window);
        }
    }

    /// <summary>
    /// 主题下拉鼠标路径回归：真实模板下点击区域必须命中 ToggleButton，
    /// 其 IsChecked 双向绑定驱动 IsDropDownOpen；Popup 自行关闭（StaysOpen=False）后状态必须同步回。
    /// 修复前失败：不透明 Surface 边框盖在 ToggleButton 上拦截命中，鼠标永远打不开下拉。
    /// </summary>
    [STATestMethod]
    public void ThemeComboBox_MousePathReachesToggleAndSyncsPopupState()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore, new FakeStartupManager());

        try
        {
            var settings = OpenSettings(window);
            var combo = settings.FindName("ThemeComboBox") as ComboBox;
            Assert.IsNotNull(combo);
            Assert.IsTrue(combo!.IsLoaded, "模板应已应用。");

            // 1) 命中测试：组合框中心点的最顶层可视命中必须落在 ToggleButton 子树内。
            var toggle = FindDescendants<ToggleButton>(combo).Single();
            var center = new System.Windows.Point(combo.ActualWidth / 2, combo.ActualHeight / 2);
            var hit = System.Windows.Media.VisualTreeHelper.HitTest(combo, center)?.VisualHit;
            var hitInsideToggle = false;
            for (var node = hit; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
            {
                if (ReferenceEquals(node, combo))
                {
                    break;
                }

                if (ReferenceEquals(node, toggle))
                {
                    hitInsideToggle = true;
                    break;
                }
            }

            Assert.IsTrue(
                hitInsideToggle,
                $"点击组合框中心应命中 ToggleButton（修复前被 Surface 边框拦截），实际命中 {hit?.GetType().Name}。");

            // 2) ToggleButton 的双向绑定驱动 IsDropDownOpen（等价鼠标按下翻转）。
            Assert.IsFalse(combo.IsDropDownOpen);
            toggle.IsChecked = true;
            Assert.IsTrue(combo.IsDropDownOpen, "ToggleButton 勾选应打开下拉。");

            // 3) Popup 因 StaysOpen=False 自行关闭时同步回 IsDropDownOpen（下次点击才不会变成空操作）。
            // Popup 不是 Visual，走模板命名部件查找。
            var popup = (Popup)combo.Template.FindName("PART_Popup", combo)!;
            Assert.IsNotNull(popup, "模板应声明 PART_Popup 部件。");
            popup.IsOpen = false;
            Assert.IsFalse(
                combo.IsDropDownOpen,
                "Popup 自行关闭后 IsDropDownOpen 应回落为 false。");
            Assert.IsFalse(toggle.IsChecked!.Value, "ToggleButton 勾选状态应随下拉关闭回落。");

            // 4) 沿真实控件路径选择深色并断言偏好落盘。
            combo.SelectedIndex = 2;
            Assert.AreEqual(ThemePreference.Dark, preferencesStore.LastSaved?.ThemeMode);
            Assert.AreEqual(2, combo.SelectedIndex);
            Assert.AreEqual(
                DesignTheme.Dark,
                GetPrivateField<DesignTheme>(window, "_activeDesignTheme"),
                "选择深色应立即应用主窗主题。");

            CloseWindow(settings);
        }
        finally
        {
            DesignThemeManager.PreviewOverride = null;
            CloseLeftoverSettingsWindow(window);
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
