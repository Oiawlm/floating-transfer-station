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

    /// <summary>
    /// 把主窗放到工作区低位并断言生效：OpenSettings 会先 Show 主窗，窗口 Top 必须
    /// 在显示并完成布局后再赋值，提前赋值会被窗口设置恢复与首帧布局覆盖。
    /// </summary>
    private static double PlaceOwnerLow(MainWindow window)
    {
        var work = SystemParameters.WorkArea;
        // 优先 600 DIP：初始放置缺陷在 Owner.Top 超过 work.Bottom-窗口实际高度
        // （本机约 24，注意不是修复前代码里的 44 常量）时复现，600 远超该阈值；
        // 矮工作区时退到下缘上方 10 DIP，保证 Owner 自身仍落在工作区内。
        var ownerTop = Math.Min(600, work.Bottom - 10);
        if (ownerTop < work.Top + 24)
        {
            Assert.Inconclusive($"工作区高 {work.Height} DIP 过小，无法构造有效的低位 Owner 前提。");
        }

        window.Top = ownerTop;
        CompleteLayout(window);
        Assert.AreEqual(
            ownerTop,
            window.Top,
            2.5,
            "主窗 Top 应保持在低位：设置窗初始放置以此为 Owner.Top。");
        return ownerTop;
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

    private static void SaveVisualEvidence(
        FrameworkElement visual,
        string fileName,
        string environmentVariable = "FTS_SETTINGS_EVIDENCE_DIR")
    {
        var directory = Environment.GetEnvironmentVariable(environmentVariable);
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
            Assert.AreEqual(
                "EdgeHideButton",
                buttons[1].Name,
                "一次性贴边隐藏紧随保持展开开关（1.20.0：同为「在场行为」组）。");
            Assert.AreEqual("SearchButton", buttons[2].Name, "搜索入口在齿轮左侧(2026-09-25 设计草案)。");
            Assert.AreEqual("SettingsButton", buttons[3].Name, "齿轮与红色清空按钮保持距离。");
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
            // 钳制上界用渲染定稿的 ActualHeight：Height 的 SizeToContent 写回值与
            // 最终渲染高度存在亚 DIP 差异，「整窗入屏」不变量以下缘不越 work.Bottom 为准。
            var work = SystemParameters.WorkArea;
            var lowestAllowedTop = Math.Max(work.Top, work.Bottom - settings.ActualHeight);
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
                settings.Top >= work.Top - 0.01 && settings.Top <= Math.Max(work.Top, work.Bottom - settings.ActualHeight) + 0.01,
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
    /// 设置窗初始放置回归（1.15.1 遗留缺口）：打开时窗口必须整窗落在工作区内，
    /// 不依赖任何用户交互。此前 SourceInitialized 按 work.Bottom-44 魔法常量放置
    /// （只保证顶部一条 44px 带可见），Owner.Top 超过 work.Bottom-窗口实际高度
    /// （本机约 24）时下缘即探出工作区；而 SizeChanged 安全网挂在 SourceInitialized
    /// 之后，尺寸事件早已全部发生、钳位从未生效（探针实测：Owner.Top=300 时下缘
    /// 1294.7 > 工作区 1018.7）。修复前失败：Owner.Top=600 时下缘 1576.4，探出
    /// 约 558 DIP。修复后的不变量对任意工作区高度都必须成立。
    /// </summary>
    [STATestMethod]
    public void SettingsWindow_OpensFullyInsideWorkAreaWithoutInteraction()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new RecordingPreferencesStore(), new FakeStartupManager());

        try
        {
            window.Show();
            CompleteLayout(window);
            PlaceOwnerLow(window);

            var settings = OpenSettings(window);
            CompleteLayout(settings);

            var work = SystemParameters.WorkArea;
            Assert.IsTrue(
                settings.Top >= work.Top - 0.5,
                $"设置窗上缘不得高于工作区，Top={settings.Top}，work.Top={work.Top}。");
            Assert.IsTrue(
                settings.Top + settings.ActualHeight <= work.Bottom + 0.5,
                $"设置窗下缘必须落在工作区内，下缘={settings.Top + settings.ActualHeight}，work.Bottom={work.Bottom}。");
            Assert.IsTrue(
                settings.Left >= work.Left - 0.5 && settings.Left + settings.ActualWidth <= work.Right + 0.5,
                $"设置窗水平方向必须落在工作区内，Left={settings.Left}，右缘={settings.Left + settings.ActualWidth}。");
            Assert.IsTrue(
                settings.ActualHeight <= settings.MaxHeight + 0.01,
                $"设置窗实际高度必须服从最大高度，ActualHeight={settings.ActualHeight}。");

            SaveVisualEvidence((Border)settings.FindName("WindowShell"), "settings-open-placement.png");

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
    /// 设置窗头部拖拽「平滑钳制」回归（1.15.1 遗留瞬移的收口）：窗口打开时若已探出
    /// 工作区下缘，头部一次带微小垂直抖动的点击会让 DragDelta 的越界钳位把 Top 一次
    /// 拉到钳制边界（探针实测：Top 300 → 24，跳变 276 DIP、占工作区 97.6%）。
    /// 拖后 Top 的变化量不得超过拖拽量与容差之和；窗口已贴工作区下缘时允许不动。
    /// </summary>
    [STATestMethod]
    public void SettingsWindow_HeaderDragDelta_ClampsSmoothlyWithoutTeleport()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new RecordingPreferencesStore(), new FakeStartupManager());

        try
        {
            window.Show();
            CompleteLayout(window);
            PlaceOwnerLow(window);

            var settings = OpenSettings(window);
            CompleteLayout(settings);
            var thumb = settings.FindName("HeaderDragRegion") as System.Windows.Controls.Primitives.Thumb;
            Assert.IsNotNull(thumb, "设置窗头部应有拖拽 Thumb。");

            var topBefore = settings.Top;
            thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(0, 2));
            CompleteLayout(settings);

            var work = SystemParameters.WorkArea;
            // 有向区间而非绝对变化量：正向小位移只允许 Top 上移拖拽量（或钳制不动），
            // 不得出现任何方向的远超拖拽量的跳变。窗口已贴工作区下缘时允许不动。
            Assert.IsTrue(
                settings.Top >= topBefore - 2.5 && settings.Top <= topBefore + 2 + 2.5,
                $"2 DIP 的垂直拖拽不得产生远超拖拽量的跳变，Top {topBefore} → {settings.Top}。");
            Assert.IsTrue(
                settings.Top >= work.Top - 0.5 && settings.Top + settings.ActualHeight <= work.Bottom + 0.5,
                $"拖拽后整窗必须仍落在工作区内，Top={settings.Top}，下缘={settings.Top + settings.ActualHeight}。");

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
    /// 设置窗限高与滚动回归：此前 SizeToContent=Height 无上限，内容高于屏幕时窗口
    /// 顶满整屏、下缘探出工作区且垂直拖不动。窗口高度必须受工作区钳制，
    /// 内容超出视口时必须可滚动到最底部（退出按钮）。
    /// </summary>
    [STATestMethod]
    public void SettingsWindow_ClampsHeightToWorkAreaAndScrollsOverflow()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new RecordingPreferencesStore(), new FakeStartupManager());

        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);

            var work = SystemParameters.WorkArea;
            Assert.IsTrue(
                settings.MaxHeight <= work.Height,
                $"设置窗最大高度不得超出工作区，MaxHeight={settings.MaxHeight}，工作区高={work.Height}。");
            var scroll = settings.FindName("SettingsScrollHost") as ScrollViewer;
            Assert.IsNotNull(scroll, "设置内容应包在 ScrollViewer 中。");
            Assert.AreEqual(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
            Assert.AreEqual(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
            // 上限必须落在 ScrollViewer 上：窗口 MaxHeight 只裁剪 HWND，滚动区
            // 拿不到受限视口，内容仍按全高排布而无法滚动。
            Assert.AreEqual(
                Math.Max(160d, work.Height - 24d - 46d),
                scroll.MaxHeight,
                0.01,
                "滚动区高度上限应按工作区推定。");
            Assert.IsTrue(
                settings.ActualHeight <= settings.MaxHeight + 0.01,
                $"设置窗实际高度必须服从最大高度，ActualHeight={settings.ActualHeight}。");

            // 模拟矮屏：限高后窗口收缩，内容（含退出按钮）滚动可达。
            settings.SizeToContent = SizeToContent.Manual;
            settings.MaxHeight = 320;
            scroll.MaxHeight = 274;
            settings.Height = 320;
            CompleteLayout(settings);

            Assert.IsTrue(
                Math.Abs(settings.ActualHeight - 320) <= 0.01,
                $"限高后窗口应收缩到 320，实际 ActualHeight={settings.ActualHeight}。");
            Assert.IsTrue(scroll.ScrollableHeight > 0, "内容超出视口时应出现纵向滚动量。");
            scroll.ScrollToVerticalOffset(scroll.ScrollableHeight / 2);
            CompleteLayout(settings);
            SaveVisualEvidence(
                (Border)settings.FindName("WindowShell"),
                "settings-scroll-limited.png");

            var exit = (Button)settings.FindName("ExitButton");
            scroll.ScrollToEnd();
            CompleteLayout(settings);

            Assert.IsTrue(scroll.VerticalOffset > 0, "滚动到底后应有实际滚动位移。");
            var exitBottom = exit.TransformToVisual(scroll).Transform(new Point()).Y + exit.ActualHeight;
            Assert.IsTrue(
                exitBottom <= scroll.ViewportHeight + 0.5,
                $"滚动到底后退出按钮应完整进入视口，按钮下缘 {exitBottom}，视口高 {scroll.ViewportHeight}。");

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

    [STATestMethod]
    public void CopyGestureToggles_ApplyImmediatelyAndPersist()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore, new FakeStartupManager());

        try
        {
            var settings = OpenSettings(window);
            var rightClick = settings.FindName("RightClickCopyToggle") as CheckBox;
            var ctrlC = settings.FindName("CtrlCCopyToggle") as CheckBox;
            Assert.IsNotNull(rightClick);
            Assert.IsNotNull(ctrlC);
            Assert.IsTrue(rightClick.IsChecked!.Value, "右键卡片复制默认开启。");
            Assert.IsTrue(ctrlC.IsChecked!.Value, "Ctrl+C 复制选中默认开启。");

            rightClick.IsChecked = false;
            CompleteLayout(window);

            Assert.IsFalse(window.CurrentPreferences.RightClickCardCopyEnabled);
            Assert.IsFalse(preferencesStore.LastSaved!.RightClickCardCopyEnabled, "改动必须立即落偏好文件。");

            ctrlC.IsChecked = false;
            CompleteLayout(window);

            Assert.IsFalse(window.CurrentPreferences.CopySelectionWithCtrlCEnabled);
            Assert.IsFalse(preferencesStore.LastSaved.CopySelectionWithCtrlCEnabled);

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
    public void TrashBehaviorComboBoxes_ApplyImmediatelyPersistAndUpdateButtonLabel()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore, new FakeStartupManager());

        try
        {
            var settings = OpenSettings(window);
            var left = settings.FindName("TrashLeftClickComboBox") as ComboBox;
            var right = settings.FindName("TrashRightClickComboBox") as ComboBox;
            Assert.IsNotNull(left);
            Assert.IsNotNull(right);
            Assert.AreEqual("清空非置顶", ((ComboBoxItem)left.SelectedItem).Content);
            Assert.AreEqual("清空全部", ((ComboBoxItem)right.SelectedItem).Content);
            var delete = window.FindName("DeleteContentButton") as Button;
            Assert.IsNotNull(delete);
            Assert.AreEqual("左键清空非置顶，右键清空全部", delete.ToolTip);
            SaveVisualEvidence(
                settings.FindName("WindowShell") as FrameworkElement ?? settings,
                "settings-copy-and-trash-sections.png",
                "FTS_DELIVERY_EVIDENCE_DIR");

            left.SelectedIndex = 1;
            CompleteLayout(window);

            Assert.AreEqual(
                TrashNoSelectionLeftClickAction.ClearAll,
                window.CurrentPreferences.TrashNoSelectionLeftClick);
            Assert.AreEqual(
                TrashNoSelectionLeftClickAction.ClearAll,
                preferencesStore.LastSaved!.TrashNoSelectionLeftClick);
            Assert.AreEqual("清空全部", delete.ToolTip, "双侧行为一致时合并为单一描述。");

            right.SelectedIndex = 2;
            CompleteLayout(window);

            Assert.AreEqual(
                TrashNoSelectionRightClickAction.NoAction,
                window.CurrentPreferences.TrashNoSelectionRightClick);
            Assert.AreEqual(
                TrashNoSelectionRightClickAction.NoAction,
                preferencesStore.LastSaved.TrashNoSelectionRightClick);
            Assert.AreEqual("左键清空全部，右键无操作", delete.ToolTip);

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
