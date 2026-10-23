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
            Assert.AreEqual("UpdateButton", buttons[0].Name, "更新按钮最左常驻（契约 #26：检查更新/一键更新双语义）。");
            Assert.AreEqual("PanelHoldButton", buttons[1].Name, "保持展开开关紧随更新按钮。");
            Assert.AreEqual(
                "EdgeHideButton",
                buttons[2].Name,
                "一次性贴边隐藏紧随保持展开开关（1.20.0：同为「在场行为」组）。");
            Assert.AreEqual("SearchButton", buttons[3].Name, "搜索入口在齿轮左侧(2026-09-25 设计草案)。");
            Assert.AreEqual("SettingsButton", buttons[4].Name, "齿轮与红色清空按钮保持距离。");
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
                settings.ActualHeight <= Math.Max(460d, work.Height - 48d) + 0.01,
                $"设置窗实际高度必须服从冻结最大高（工作区 − 48，矮屏抬到 460），ActualHeight={settings.ActualHeight}。");

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
    /// 设置窗五页卡片宽度统一（契约 #23 机制扩展，Issue #200）：SettingsScrollHost
    /// 挂 keyed OverlayScrollViewerStyle（单格叠加）后，滚动条出现/消失不再占布局宽
    /// ——五内容页在任何窗口高度下卡片宽度逐 DIP 相等；滚动条在场时条带为 8 DIP
    /// 瘦条、悬浮于内容之上，与卡片零相交，viewport 宽等于视口全宽（无独立布局列）。
    /// 修复前默认模板：溢出页出现布局滚动条 → 内容列窄一个滚动条宽，与不溢出页
    /// 卡片宽度不一致（BoardList 上的同款漂移，契约 #23 由来）。
    /// </summary>
    [STATestMethod]
    public void SettingsWindow_CardWidthUnifiedAcrossPagesAndScrollBarVisibility()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new RecordingPreferencesStore(), new FakeStartupManager());

        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);

            var scroll = (ScrollViewer)settings.FindName("SettingsScrollHost");
            Assert.IsNotNull(scroll, "设置内容应包在共享 ScrollViewer 视口中。");
            var contentHost = (Grid)settings.FindName("SettingsContentHost");
            var cardStyle = (Style)settings.FindResource("SettingsCardStyle");
            var navNames = new[]
            {
                "SettingsNavAppearance", "SettingsNavPanel", "SettingsNavGeneral",
                "SettingsNavData", "SettingsNavAi"
            };
            var pageNames = new[]
            {
                "AppearancePage", "PanelPage", "GeneralPage", "DataPage", "AiPage"
            };

            // 矮窗口（最小高 460）：面板页必溢出（既有测试实证），五页轮流度量。
            settings.Height = 460;
            CompleteLayout(settings);

            double unifiedWidth = 0;
            var anyOverflow = false;
            for (var index = 0; index < navNames.Length; index++)
            {
                ((RadioButton)settings.FindName(navNames[index])).IsChecked = true;
                CompleteLayout(settings);
                var page = (StackPanel)settings.FindName(pageNames[index]);
                var card = FindDescendants<Border>(page)
                    .First(candidate => ReferenceEquals(candidate.Style, cardStyle));
                Assert.IsTrue(
                    card.ActualWidth > 0,
                    $"第 {index + 1} 页卡片应已布局（Width={card.ActualWidth}）。");
                if (unifiedWidth == 0)
                {
                    unifiedWidth = card.ActualWidth;
                }
                else
                {
                    Assert.AreEqual(
                        unifiedWidth,
                        card.ActualWidth,
                        0.5,
                        $"五页卡片宽度必须统一：第 {index + 1} 页 {card.ActualWidth} vs 首页 {unifiedWidth}。");
                }

                Assert.AreEqual(
                    contentHost.ActualWidth,
                    card.ActualWidth,
                    0.5,
                    $"第 {index + 1} 页卡片必须铺满内容列（卡片 {card.ActualWidth} vs 列 {contentHost.ActualWidth}）：内容列不得随最宽卡片收窄。");

                if (scroll.ScrollableHeight > 0)
                {
                    anyOverflow = true;
                    Assert.AreEqual(
                        scroll.ActualWidth,
                        scroll.ViewportWidth,
                        0.5,
                        "滚动条在场时 viewport 宽必须等于视口全宽（单格叠加，无独立布局列）。");
                    var strip = FindDescendants<ScrollBar>(scroll).Single(candidate =>
                        candidate.Orientation == Orientation.Vertical &&
                        candidate.Visibility == Visibility.Visible);
                    Assert.AreEqual(
                        8d, strip.ActualWidth, 0.5,
                        "overlay 条带宽应为瘦条样式的 8 DIP。");
                    var cardRect = new Rect(
                        card.TranslatePoint(new System.Windows.Point(), scroll),
                        new Size(card.ActualWidth, card.ActualHeight));
                    var stripRect = new Rect(
                        strip.TranslatePoint(new System.Windows.Point(), scroll),
                        new Size(strip.ActualWidth, strip.ActualHeight));
                    Assert.IsFalse(
                        cardRect.IntersectsWith(stripRect),
                        $"卡片矩形 {cardRect} 与滚动条条带 {stripRect} 相交：条带必须落在内容列右边距带内。");
                }
            }

            Assert.IsTrue(
                anyOverflow,
                "前置：矮窗口下至少一页溢出（否则跨页等宽断言失去滚动条维度，测试无效）。");

            // 同页两态（面板页）：矮窗（滚动条在场）↔ 高窗（滚动条离场），卡宽与
            // viewport 宽逐 DIP 不变——滚动条可见性与几何解耦的直接断言。
            ((RadioButton)settings.FindName("SettingsNavPanel")).IsChecked = true;
            CompleteLayout(settings);
            var panelPage = (StackPanel)settings.FindName("PanelPage");
            var panelCard = FindDescendants<Border>(panelPage)
                .First(candidate => ReferenceEquals(candidate.Style, cardStyle));
            var widthWithScrollBar = panelCard.ActualWidth;
            var viewportWithScrollBar = scroll.ViewportWidth;
            Assert.IsTrue(
                scroll.ScrollableHeight > 0,
                "前置：矮窗口下面板页溢出、滚动条在场。");

            var work = SystemParameters.WorkArea;
            settings.Height = Math.Min(880d, Math.Max(460d, work.Height - 48d));
            CompleteLayout(settings);
            if (scroll.ScrollableHeight > 0)
            {
                Assert.Inconclusive(
                    $"本机工作区高 {work.Height} DIP 过小，880/工作区−48 下面板页仍溢出，无法构造滚动条离场态。");
            }

            var panelCardTall = FindDescendants<Border>(panelPage)
                .First(candidate => ReferenceEquals(candidate.Style, cardStyle));
            Assert.AreEqual(
                widthWithScrollBar,
                panelCardTall.ActualWidth,
                0.5,
                "同一页在滚动条在场/离场两种窗口高度下卡片宽度必须逐 DIP 相等。");
            Assert.AreEqual(
                viewportWithScrollBar,
                scroll.ViewportWidth,
                0.5,
                "viewport 宽不得随滚动条出现/消失变化。");
            SaveVisualEvidence(
                (Border)settings.FindName("WindowShell"),
                "settings-card-width-tall.png");

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
    /// 设置窗固定视口与滚动回归（契约 #24，Issue #106）：SizeToContent=Manual 后
    /// 窗口高度不再跟随内容；恢复/默认高度受「工作区 − 48、矮屏抬到 MinHeight(460)」
    /// 冻结式约束（经 WM_GETMINMAXINFO 轨道尺寸与恢复路径 Normalize 双路强制）；
    /// 内容溢出由共享视口滚动，页脚（退出按钮）在滚动区外保持常驻可见。
    /// </summary>
    [STATestMethod]
    public void SettingsWindow_FixedViewportScrollsOverflowAndKeepsFooterVisible()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new RecordingPreferencesStore(), new FakeStartupManager());

        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);

            var work = SystemParameters.WorkArea;
            var scroll = settings.FindName("SettingsScrollHost") as ScrollViewer;
            Assert.IsNotNull(scroll, "设置内容应包在共享 ScrollViewer 视口中。");
            Assert.AreEqual(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
            Assert.AreEqual(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
            Assert.IsTrue(
                settings.ActualHeight <= Math.Max(460d, work.Height - 48d) + 0.01,
                $"设置窗实际高度必须服从冻结最大高，ActualHeight={settings.ActualHeight}。");
            Assert.AreEqual(680d, settings.ActualWidth, 0.01, "默认宽度应为冻结值 680 DIP。");

            // 页脚在滚动区外：不滚动也完整可见。
            var exit = (Button)settings.FindName("ExitButton");
            Assert.IsTrue(exit.IsVisible, "页脚退出按钮必须常驻可见（不属于任何导航页）。");

            // 模拟矮屏：窗口收至最小高，视口收缩；面板页（标签顺序四行必渲染）必溢出。
            var panelNav = (RadioButton)settings.FindName("SettingsNavPanel");
            panelNav.IsChecked = true;
            CompleteLayout(settings);
            settings.Height = 460;
            CompleteLayout(settings);

            Assert.IsTrue(
                Math.Abs(settings.ActualHeight - 460) <= 0.01,
                $"矮屏下窗口应收缩到最小高 460，实际 ActualHeight={settings.ActualHeight}。");
            Assert.IsTrue(scroll.ScrollableHeight > 0, "内容超出视口时应出现纵向滚动量。");
            scroll.ScrollToEnd();
            CompleteLayout(settings);
            Assert.IsTrue(scroll.VerticalOffset > 0, "滚动到底后应有实际滚动位移。");
            Assert.IsTrue(exit.IsVisible, "内容滚动时页脚退出按钮仍必须可见。");
            SaveVisualEvidence(
                (Border)settings.FindName("WindowShell"),
                "settings-scroll-limited.png");

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
            // 手势统一（Issue #216）移除了右键复制开关：内容复制节只剩 Ctrl+C。
            Assert.IsNull(settings.FindName("RightClickCopyToggle"));
            var ctrlC = settings.FindName("CtrlCCopyToggle") as CheckBox;
            Assert.IsNotNull(ctrlC);
            Assert.IsTrue(ctrlC.IsChecked!.Value, "Ctrl+C 复制选中默认开启。");

            ctrlC.IsChecked = false;
            CompleteLayout(window);

            Assert.IsFalse(window.CurrentPreferences.CopySelectionWithCtrlCEnabled);
            Assert.IsFalse(preferencesStore.LastSaved!.CopySelectionWithCtrlCEnabled);

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
    public void ShortCardNotesToggle_AppliesImmediatelyPersistsAndSurvivesReopen()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore, new FakeStartupManager());

        try
        {
            var settings = OpenSettings(window);
            var toggle = settings.FindName("ShortCardNotesToggle") as CheckBox;
            Assert.IsNotNull(toggle);
            Assert.IsFalse(toggle!.IsChecked!.Value, "短卡备注开关默认关闭。");

            toggle.IsChecked = true;
            CompleteLayout(window);

            Assert.IsTrue(window.CurrentPreferences.ShortCardNotesEnabled, "改动必须立即生效。");
            Assert.IsTrue(preferencesStore.LastSaved!.ShortCardNotesEnabled, "改动必须立即落偏好文件。");
            Assert.IsTrue(
                ((MainWindowViewModel)window.DataContext).ShortCardNotesEnabled,
                "开关必须经 ApplyPreferences 喂值到窗口 VM（卡片触发器的消费源）。");

            // 重开设置窗：开关状态必须来自已保存偏好而非控件残留。
            CloseWindow(settings);
            var reopened = OpenSettings(window);
            var reopenedToggle = reopened.FindName("ShortCardNotesToggle") as CheckBox;
            Assert.IsNotNull(reopenedToggle);
            Assert.IsTrue(reopenedToggle!.IsChecked!.Value, "重开设置窗必须反映已保存的新值。");

            reopenedToggle.IsChecked = false;
            CompleteLayout(window);
            Assert.IsFalse(window.CurrentPreferences.ShortCardNotesEnabled);
            Assert.IsFalse(
                ((MainWindowViewModel)window.DataContext).ShortCardNotesEnabled,
                "关闭必须同样即时喂值回 VM。");
            CloseWindow(reopened);
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

    [STATestMethod]
    public void AutoCleanupToggle_DefaultsOnAndPersistsImmediately()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore, new FakeStartupManager());

        try
        {
            var settings = OpenSettings(window);
            var toggle = settings.FindName("AutoCleanupToggle") as CheckBox;
            Assert.IsNotNull(toggle);
            Assert.IsTrue(toggle.IsChecked!.Value, "自动清理默认开启（升级老用户同样默认开启）。");

            toggle.IsChecked = false;
            CompleteLayout(window);

            Assert.IsFalse(window.CurrentPreferences.AutoCleanupEnabled);
            Assert.IsFalse(preferencesStore.LastSaved!.AutoCleanupEnabled, "改动必须立即落偏好文件。");

            toggle.IsChecked = true;
            CompleteLayout(window);

            Assert.IsTrue(window.CurrentPreferences.AutoCleanupEnabled);
            Assert.IsTrue(preferencesStore.LastSaved.AutoCleanupEnabled);

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
    public void PinnedDividerComboBox_OffersFourBuiltInStylesDefaultingToSolid()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore, new FakeStartupManager());

        try
        {
            var settings = OpenSettings(window);
            var combo = settings.FindName("PinnedDividerComboBox") as ComboBox;
            Assert.IsNotNull(combo);
            var items = combo.Items.OfType<ComboBoxItem>().ToArray();
            CollectionAssert.AreEqual(
                new[] { "无", "实线", "虚线", "强调条" },
                items.Select(item => (string)item.Content!).ToArray(),
                "四个内置样式必须按冻结顺序呈现。");
            CollectionAssert.AreEqual(
                new[]
                {
                    PinnedDividerStyle.None,
                    PinnedDividerStyle.Solid,
                    PinnedDividerStyle.Dashed,
                    PinnedDividerStyle.AccentBar
                },
                items.Select(item => item.Tag).ToArray(),
                "选项 Tag 必须对应枚举值。");
            Assert.AreEqual("实线", ((ComboBoxItem)combo.SelectedItem).Content, "默认选中实线。");
            Assert.IsNull(preferencesStore.LastSaved, "构造期初始化守卫不得触发保存。");

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
    public void PinnedDividerComboBox_AppliesImmediatelyPersistsAndSurvivesReopen()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore
        {
            Seeded = AppPreferences.Default with { PinnedDivider = PinnedDividerStyle.Dashed }
        };
        var window = CreateWindow(
            directory,
            preferencesStore,
            new FakeStartupManager(),
            preferencesStore.Seeded);

        try
        {
            var settings = OpenSettings(window);
            var combo = settings.FindName("PinnedDividerComboBox") as ComboBox;
            Assert.IsNotNull(combo);
            Assert.AreEqual("虚线", ((ComboBoxItem)combo.SelectedItem).Content, "种子偏好必须回显到下拉。");

            combo.SelectedIndex = (int)PinnedDividerStyle.AccentBar;
            CompleteLayout(window);

            Assert.AreEqual(
                PinnedDividerStyle.AccentBar,
                window.CurrentPreferences.PinnedDivider,
                "改动必须立即生效。");
            Assert.AreEqual(
                PinnedDividerStyle.AccentBar,
                preferencesStore.LastSaved!.PinnedDivider,
                "改动必须立即落偏好文件。");

            // 重开设置窗：显示值必须来自已保存偏好而非控件残留。
            CloseWindow(settings);
            var reopened = OpenSettings(window);
            var reopenedCombo = reopened.FindName("PinnedDividerComboBox") as ComboBox;
            Assert.IsNotNull(reopenedCombo);
            Assert.AreEqual(
                "强调条",
                ((ComboBoxItem)reopenedCombo.SelectedItem).Content,
                "重开设置窗必须反映已保存的新值。");
            CloseWindow(reopened);
        }
        finally
        {
            DesignThemeManager.PreviewOverride = null;
            CloseLeftoverSettingsWindow(window);
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void AutoCleanupRetentionComboBox_OffersSevenTiersDefaultingToTwentyFourHours()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore, new FakeStartupManager());

        try
        {
            var settings = OpenSettings(window);
            var combo = settings.FindName("AutoCleanupRetentionComboBox") as ComboBox;
            Assert.IsNotNull(combo);
            var items = combo.Items.OfType<ComboBoxItem>().ToArray();
            CollectionAssert.AreEqual(
                new[] { "2 小时", "4 小时", "6 小时", "8 小时", "10 小时", "12 小时", "24 小时" },
                items.Select(item => (string)item.Content!).ToArray(),
                "七个档位必须按升序呈现，文案由唯一时长映射派生。");
            CollectionAssert.AreEqual(
                new[]
                {
                    AutoCleanupRetention.TwoHours,
                    AutoCleanupRetention.FourHours,
                    AutoCleanupRetention.SixHours,
                    AutoCleanupRetention.EightHours,
                    AutoCleanupRetention.TenHours,
                    AutoCleanupRetention.TwelveHours,
                    AutoCleanupRetention.TwentyFourHours
                },
                items.Select(item => item.Tag).ToArray(),
                "选项 Tag 必须对应枚举档位（枚举序≠显示序，匹配只认 Tag）。");
            Assert.AreEqual("24 小时", ((ComboBoxItem)combo.SelectedItem).Content, "默认选中 24 小时。");
            Assert.IsTrue(combo.IsEnabled, "总开关默认开启，下拉可用。");
            Assert.IsNull(preferencesStore.LastSaved, "构造期初始化守卫不得触发保存。");

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
    public void AutoCleanupRetentionComboBox_AppliesImmediatelyPersistsAndDisabledWithToggleOff()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore, new FakeStartupManager());

        try
        {
            var settings = OpenSettings(window);
            var combo = settings.FindName("AutoCleanupRetentionComboBox") as ComboBox;
            Assert.IsNotNull(combo);
            var toggle = settings.FindName("AutoCleanupToggle") as CheckBox;
            Assert.IsNotNull(toggle);

            combo.SelectedIndex = 0;
            CompleteLayout(window);

            Assert.AreEqual(
                AutoCleanupRetention.TwoHours,
                window.CurrentPreferences.AutoCleanupRetention,
                "档位改动必须立即生效。");
            Assert.AreEqual(
                AutoCleanupRetention.TwoHours,
                preferencesStore.LastSaved!.AutoCleanupRetention,
                "档位改动必须立即落偏好文件。");

            toggle.IsChecked = false;
            CompleteLayout(window);

            Assert.IsFalse(combo.IsEnabled, "总开关关闭时保留下拉置灰。");

            toggle.IsChecked = true;
            CompleteLayout(window);

            Assert.IsTrue(combo.IsEnabled, "总开关重新开启时下拉恢复可用。");

            // 重开设置窗：选中项必须来自已保存偏好，禁用态跟随总开关（先停在关闭态再关窗）。
            toggle.IsChecked = false;
            CompleteLayout(window);
            Assert.IsFalse(combo.IsEnabled);
            CloseWindow(settings);
            var reopened = OpenSettings(window);
            var reopenedCombo = reopened.FindName("AutoCleanupRetentionComboBox") as ComboBox;
            Assert.IsNotNull(reopenedCombo);
            Assert.AreEqual(
                "2 小时",
                ((ComboBoxItem)reopenedCombo.SelectedItem).Content,
                "重开设置窗必须反映已保存的档位。");
            Assert.IsFalse(reopenedCombo.IsEnabled, "总开关关闭状态下重开设置窗下拉同样置灰。");
            CloseWindow(reopened);
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
