using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatingTransferStation.Design;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 设置窗页脚「关于」区联系方式（W5 2026-11-05；W3 2026-11-05 交互矩阵；
/// 2026-11-06 右键二维码＝复制图片、复制按钮删除；2026-11-07 容器级过渡；
/// 2026-11-08 关闭回退「对齐落位」重编排，Issue #325）：微信号文本与二维码
/// 位图全部来自 ProductIdentity 单一来源（页脚/放大层共用同一冻结位图实例）；
/// 复制（微信号文本与二维码图片）均经可注入剪贴板写入缝落盘（harness 密封
/// 缺省＝响亮失败 stub，契约 #53 口径），反馈为触发侧微信号 Run 文本瞬时翻转；
/// 放大层（设置窗内遮罩，不新建窗口）从缩略图单击打开、左/右键单击遮罩空白处
/// 或 Esc 关闭（Esc 只关层不关窗）；右键二维码（缩略图/放大图一致）＝复制
/// 二维码图片且不弹层/不关层；微信号两态行左右键皆复制微信号文本（放大态复制
/// 不关层）。结构拆分（2026-11-08）：根 Grid 只承载布局＋命中＋Visibility，
/// 遮罩纱 WeChatQrScrim（FadeAnimation 含收回曲线 ExitEaseToken）与自持
/// Opacity 的卡片兄弟——动效开时外层整卡容器自缩略图按钮含框源位置飞入落位
///（容器级 container transform：方→矩形形变、文字三元素落位后段浮现、缩略图
/// 防双影淡出）、关层自当下态原路飞回源位置且收回编排「对齐落位」（卡片全程
/// 可见、缩略图持隐至 95% 回程后同窗线性换手——阈值断言 A-E 锁定，消除原位
/// 闪现），单一完成点收口层生命周期；退化路径纯淡出（禁瞬时恒等）；动效关＝
/// 对象定格终态。
/// </summary>
[TestClass]
public sealed class SettingsWindowContactTests
{
    private static MainWindow CreateWindow(
        TestDirectory directory,
        RecordingPreferencesStore preferencesStore,
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
            startupManager: new FakeStartupManager(),
            dataDirectory: directory.Root);
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
        var settings = (SettingsWindow?)field!.GetValue(window);
        Assert.IsNotNull(settings);
        // harness 密封缺省（契约 #53 口径）：未显式声明写意图的测试一旦触达
        // 真实剪贴板即响亮失败；需要断言负载的测试在测试体内覆写本缺省。
        settings.ClipboardWriterOverride ??= RejectUndeclaredRealClipboardWrite;
        return settings;
    }

    private static bool RejectUndeclaredRealClipboardWrite(DataObject data) =>
        throw new AssertFailedException(
            $"测试未声明剪贴板写意图却触发了真实写入路径（负载：{data.GetData(DataFormats.UnicodeText)}）；"
            + "请在测试体内显式设置 settings.ClipboardWriterOverride。");

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

    private static void PumpUntil(Dispatcher dispatcher, Func<bool> condition)
    {
        if (!condition())
        {
            var frame = new DispatcherFrame();
            var timeout = new DispatcherTimer(DispatcherPriority.Send, dispatcher)
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            timeout.Tick += (_, _) =>
            {
                timeout.Stop();
                frame.Continue = false;
            };
            timeout.Start();
            while (!condition() && frame.Continue)
            {
                Dispatcher.PushFrame(frame);
            }

            timeout.Stop();
        }

        if (!condition())
        {
            throw new TimeoutException("等待联系方式交互结果超时（5 秒）。");
        }
    }

    private static void CloseAll(MainWindow window)
    {
        var field = typeof(MainWindow).GetField(
            "_settingsWindow",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field!.GetValue(window) is SettingsWindow leftover)
        {
            leftover.Close();
        }

        window.Close();
    }

    [STATestMethod]
    public void FooterContact_BindsWeChatIdAndQrFromProductIdentity()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);

            var idRun = (Run)settings.FindName("WeChatIdRun")!;
            Assert.AreEqual(
                ProductIdentity.WeChatId,
                idRun.Text,
                "页脚微信号必须来自 ProductIdentity 单一来源（XAML/视图层零字面量副本）。");
            var largeIdRun = (Run)settings.FindName("WeChatIdLargeRun")!;
            Assert.AreEqual(ProductIdentity.WeChatId, largeIdRun.Text, "放大层微信号与页脚同源一致。");

            var thumb = (Image)settings.FindName("WeChatQrThumb")!;
            var large = (Image)settings.FindName("WeChatQrLarge")!;
            Assert.IsInstanceOfType<BitmapImage>(thumb.Source);
            Assert.IsNotNull(thumb.Source, "缩略图必须加载 ProductIdentity.WeChatQrComponentUri 指定的位图。");
            Assert.AreSame(thumb.Source, large.Source, "缩略图与放大层共用同一冻结位图实例（不重复实例化）。");
            Assert.IsTrue(((BitmapImage)thumb.Source).IsFrozen, "二维码位图必须冻结共享。");

            var overlay = (Grid)settings.FindName("WeChatQrOverlay")!;
            Assert.AreEqual(Visibility.Collapsed, overlay.Visibility, "放大层初始必须塌缩（不占布局、不残留命中）。");

            var thumbButton = (Button)settings.FindName("WeChatQrButton")!;
            Assert.AreEqual(
                "放大微信二维码",
                thumbButton.GetValue(AutomationProperties.NameProperty),
                "缩略图按钮必须携带 UIA 名称。");
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void QrOverlay_OpensFromThumbAndClosesOnAnyClick()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        // 动效关闭：FadeAnimation 立即切换显隐（关闭终态可确定性断言）。
        var window = CreateWindow(
            directory,
            preferencesStore,
            preferencesStore.Seeded with { AnimationsEnabled = false });
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            var overlay = (Grid)settings.FindName("WeChatQrOverlay")!;
            Assert.AreEqual(Visibility.Collapsed, overlay.Visibility);

            ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.AreEqual(Visibility.Visible, overlay.Visibility, "单击缩略图立即打开放大层（可见性即时生效）。");
            CompleteLayout(settings);
            Assert.IsInstanceOfType<BitmapImage>(((Image)settings.FindName("WeChatQrLarge")!).Source);
            // 结构断言（2026-11-08 遮罩纱拆分，Issue #325）：根 Grid 不再挂
            // Opacity 淡出（恒 1），遮罩纱承载 FadeAnimation 且出场曲线 token
            // ＝QrZoomOutKeySpline（与容器收回同曲线）；ExitEaseToken 默认
            // null＝未指定的既有消费者零行为变化。
            var scrim = (Border)settings.FindName("WeChatQrScrim")!;
            Assert.AreEqual(1d, overlay.Opacity, "结构拆分后根 Grid 无 Opacity 编排（恒 1）。");
            Assert.IsNull(
                FadeAnimation.GetExitEaseToken(overlay),
                "根 Grid 未指定 ExitEaseToken（附加属性默认 null，既有消费者零波及）。");
            Assert.AreEqual(
                DesignTokens.QrZoomOutKeySpline,
                FadeAnimation.GetExitEaseToken(scrim),
                "遮罩纱出场曲线 token＝QrZoomOutKeySpline（与容器收回同曲线同时长）。");
            Assert.AreEqual(
                1,
                ((System.Windows.Controls.Panel)((TextBlock)settings.FindName("WeChatIdFooterBlock")!).Parent).Children.Count,
                "页脚微信号行是所在行唯一子元素（2026-11-06 复制按钮已删除，文本复制入口＝微信号行）。");
            Assert.IsNotNull(
                settings.FindName("WeChatIdLargeBlock"),
                "放大态文本复制入口（微信号行）必须存在。");
            Assert.AreEqual(
                1,
                ((System.Windows.Controls.Panel)((TextBlock)settings.FindName("WeChatIdLargeBlock")!).Parent).Children.Count,
                "放大态微信号行是所在行唯一子元素（放大层复制按钮同裁决已删除）。");

            overlay.RaiseEvent(new MouseButtonEventArgs(
                Mouse.PrimaryDevice,
                Environment.TickCount,
                MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent,
                Source = overlay
            });
            Assert.AreEqual(Visibility.Collapsed, overlay.Visibility, "单击遮罩任意处关闭放大层。");
        }
        finally
        {
            CloseAll(window);
        }
    }

    /// <summary>W5 联系方式证据（FTS_W5_EVIDENCE_DIR 设置时产出真实 WPF 渲染
    /// 截图）：浅/深主题设置窗页脚关于区（联系方式行＋二维码缩略图，无按钮态）、
    /// 微信号行复制反馈态、缩略图右键复制反馈态、放大层与放大图右键复制反馈态
    ///（层不关同证）、动效关开/关即时定格。复制走写入缝，不触碰系统剪贴板。</summary>
    [STATestMethod]
    public void Evidence_CaptureW5ContactScreens()
    {
        var evidenceDirectory = Environment.GetEnvironmentVariable("FTS_W5_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(evidenceDirectory))
        {
            Assert.Inconclusive("未设置 FTS_W5_EVIDENCE_DIR，跳过联系方式证据产出。");
        }

        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        try
        {
            var settings = OpenSettings(window);
            settings.ClipboardWriterOverride = _ => true;
            CompleteLayout(settings);

            SaveWindowShell(settings, evidenceDirectory!, "settings-footer-light.png");
            SaveFooterCrop(settings, evidenceDirectory!, "footer-contact-light.png");

            // 复制反馈态改行触发（2026-11-06：复制按钮删除，反馈就地翻转微信号 Run）。
            RaisePointUp((TextBlock)settings.FindName("WeChatIdFooterBlock")!, UIElement.MouseLeftButtonUpEvent);
            CompleteLayout(settings);
            SaveFooterCrop(settings, evidenceDirectory!, "footer-contact-copied-light.png");
            PumpUntil(settings.Dispatcher, () =>
                ((Run)settings.FindName("WeChatIdRun")!).Text == ProductIdentity.WeChatId);

            // 缩略图右键复制二维码图片 → 页脚 Run 反馈（2026-11-06 新交互证据）。
            RaisePointUp((Button)settings.FindName("WeChatQrButton")!, UIElement.MouseRightButtonUpEvent);
            CompleteLayout(settings);
            SaveFooterCrop(settings, evidenceDirectory!, "footer-contact-qrcopied-light.png");
            PumpUntil(settings.Dispatcher, () =>
                ((Run)settings.FindName("WeChatIdRun")!).Text == ProductIdentity.WeChatId);

            var overlay = (Grid)settings.FindName("WeChatQrOverlay")!;
            var scrim = (Border)settings.FindName("WeChatQrScrim")!;
            ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(settings.Dispatcher, () => scrim.Opacity == 1d && overlay.Visibility == Visibility.Visible);
            PumpUntilQrTextShown(settings);
            SaveWindowShell(settings, evidenceDirectory!, "qr-overlay-light.png");

            // 放大图右键复制 → 放大态 Run 反馈且层不关（2026-11-06 新交互证据）。
            // 右键处理器同步翻转 Run 且不动透明度；UpdateLayout 只跑布局不泵
            // 调度器（动画时钟完成处理不落地、1.6s 反馈复位不触发），翻转文本
            // 与满帧层同帧入画——PushFrame 泵会拍到时钟收尾透明帧或复位常态帧。
            RaisePointUp((Border)settings.FindName("WeChatQrLargeCard")!, UIElement.MouseRightButtonUpEvent);
            settings.UpdateLayout();
            SaveWindowShell(settings, evidenceDirectory!, "qr-overlay-qrcopied-light.png");
            Assert.AreEqual(
                Visibility.Visible,
                overlay.Visibility,
                "放大图右键复制不关层（证据帧同证）。");
            Assert.AreEqual(
                "已复制",
                ((Run)settings.FindName("WeChatIdLargeRun")!).Text,
                "证据帧同证：放大态 Run 已翻「已复制」。");
            CompleteLayout(settings);
            PumpUntil(settings.Dispatcher, () =>
                ((Run)settings.FindName("WeChatIdLargeRun")!).Text == ProductIdentity.WeChatId);
            PumpUntil(settings.Dispatcher, () => scrim.Opacity == 1d && overlay.Visibility == Visibility.Visible);

            overlay.RaiseEvent(new MouseButtonEventArgs(
                Mouse.PrimaryDevice,
                Environment.TickCount,
                MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent,
                Source = overlay
            });
            PumpUntil(settings.Dispatcher, () => overlay.Visibility == Visibility.Collapsed);

            settings.ApplyTheme(DesignTheme.Dark);
            CompleteLayout(settings);
            SaveWindowShell(settings, evidenceDirectory!, "settings-footer-dark.png");
            SaveFooterCrop(settings, evidenceDirectory!, "footer-contact-dark.png");

            ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(settings.Dispatcher, () => scrim.Opacity == 1d && overlay.Visibility == Visibility.Visible);
            PumpUntilQrTextShown(settings);
            SaveWindowShell(settings, evidenceDirectory!, "qr-overlay-dark.png");
        }
        finally
        {
            CloseAll(window);
        }

        // 动效关开/关即时定格（2026-11-06）：偏好动效关的独立窗口。
        using var disabledDirectory = new TestDirectory();
        var disabledStore = new RecordingPreferencesStore();
        var disabledWindow = CreateWindow(
            disabledDirectory,
            disabledStore,
            disabledStore.Seeded with { AnimationsEnabled = false });
        try
        {
            var settings = OpenSettings(disabledWindow);
            CompleteLayout(settings);
            var overlay = (Grid)settings.FindName("WeChatQrOverlay")!;
            ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            CompleteLayout(settings);
            Assert.AreEqual(Visibility.Visible, overlay.Visibility, "动效关＝打开即时可见。");
            SaveWindowShell(settings, evidenceDirectory!, "contact-disabled-open-light.png");

            RaisePointUp(overlay, UIElement.MouseLeftButtonUpEvent);
            CompleteLayout(settings);
            Assert.AreEqual(Visibility.Collapsed, overlay.Visibility, "动效关＝关闭即时塌缩。");
            SaveWindowShell(settings, evidenceDirectory!, "contact-disabled-closed-light.png");
        }
        finally
        {
            CloseAll(disabledWindow);
        }
    }

    /// <summary>W3→容器级过渡证据（FTS_W5_EVIDENCE_DIR 设置时产出真实 WPF 渲染
    /// 截图）：浅/深主题放大过程多帧胶片（各≥6 帧覆盖 0→450ms 容器入场全程
    /// 含文字 270ms 起浮现段，含 t=0 起始与终态）＋收回过程多帧胶片（开→动画满
    /// →关，各≥6 帧覆盖 0→250ms 收回全程含文字 90ms 先隐段，含 t=0 与终态塌缩
    /// 帧）＋收回交接段细粒度胶片（2026-11-08 关闭回退重编排：175ms 起 10ms
    /// 步进×7 帧覆盖卡片 1→0 与缩略图 0→1 同窗线性换手）＋入场中途反向收回胶片
    /// （各≥6 帧）＋动效关开/关即时定格。**泵步长局限**：PushFrame 泵受调度器
    /// 计时粒度（≈15.6ms）限制，胶片不是帧级证明——16-33ms 单帧闪现的逐帧
    /// 核查以 ≥60fps 真实录屏为准（evidence-d1-*.mp4），本胶片仅作编排形态
    /// 对照。复制走写入缝，不触碰系统剪贴板。</summary>
    [STATestMethod]
    public void Evidence_CaptureQrContainerFrames()
    {
        var evidenceDirectory = Environment.GetEnvironmentVariable("FTS_W5_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(evidenceDirectory))
        {
            Assert.Inconclusive("未设置 FTS_W5_EVIDENCE_DIR，跳过容器级过渡证据产出。");
        }

        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        try
        {
            var settings = OpenSettings(window);
            settings.ClipboardWriterOverride = _ => true;
            CompleteLayout(settings);
            var overlay = (Grid)settings.FindName("WeChatQrOverlay")!;

            CaptureQrContainerFilm(settings, overlay, evidenceDirectory!, "container-light");
            CaptureQrContainerExitFilm(settings, overlay, evidenceDirectory!, "container-exit-light");
            CaptureQrContainerHandoverFilm(settings, overlay, evidenceDirectory!, "container-handover-light");
            CaptureQrContainerReverseFilm(settings, overlay, evidenceDirectory!, "container-reverse-light");
            settings.ApplyTheme(DesignTheme.Dark);
            CompleteLayout(settings);
            CaptureQrContainerFilm(settings, overlay, evidenceDirectory!, "container-dark");
            CaptureQrContainerExitFilm(settings, overlay, evidenceDirectory!, "container-exit-dark");
            CaptureQrContainerHandoverFilm(settings, overlay, evidenceDirectory!, "container-handover-dark");
            CaptureQrContainerReverseFilm(settings, overlay, evidenceDirectory!, "container-reverse-dark");
        }
        finally
        {
            CloseAll(window);
        }

        // 动效关开/关即时定格：偏好动效关的独立窗口。
        using var disabledDirectory = new TestDirectory();
        var disabledStore = new RecordingPreferencesStore();
        var disabledWindow = CreateWindow(
            disabledDirectory,
            disabledStore,
            disabledStore.Seeded with { AnimationsEnabled = false });
        try
        {
            var settings = OpenSettings(disabledWindow);
            CompleteLayout(settings);
            var overlay = (Grid)settings.FindName("WeChatQrOverlay")!;
            ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            CompleteLayout(settings);
            Assert.AreEqual(Visibility.Visible, overlay.Visibility, "动效关＝打开即时可见。");
            SaveWindowShell(settings, evidenceDirectory!, "container-disabled-final-light.png");

            RaisePointUp(overlay, UIElement.MouseLeftButtonUpEvent);
            CompleteLayout(settings);
            Assert.AreEqual(Visibility.Collapsed, overlay.Visibility, "动效关＝关闭即时塌缩。");
            SaveWindowShell(settings, evidenceDirectory!, "container-disabled-closed-light.png");
        }
        finally
        {
            CloseAll(disabledWindow);
        }
    }

    /// <summary>放大过程多帧胶片：起始帧（先泵 1ms 让动画时钟起步——同步渲染
    /// 会取挂钟前的基值，实时应用的首个合成帧同样先走时钟 tick，起始帧须等价）
    /// ＋40ms 步进×12 帧覆盖 450ms 容器入场全程（280ms 步进起可对照文字浮现）
    /// ＋收尾终态帧。</summary>
    private static void CaptureQrContainerFilm(
        SettingsWindow settings,
        Grid overlay,
        string directory,
        string prefix)
    {
        ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
            new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(1));
        SaveWindowShell(settings, directory, $"{prefix}-frame-00.png");
        for (var step = 1; step <= 12; step++)
        {
            PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(40));
            SaveWindowShell(settings, directory, $"{prefix}-frame-{step:00}.png");
        }

        var scrim = (Border)settings.FindName("WeChatQrScrim")!;
        PumpUntil(settings.Dispatcher, () => scrim.Opacity == 1d && overlay.Visibility == Visibility.Visible);
        SaveWindowShell(settings, directory, $"{prefix}-final.png");
    }

    /// <summary>收回过程多帧胶片：先泵过入场全程到恒等定格，单击关层后起始帧
    ///（泵 1ms 同口径）＋40ms 步进×8 帧覆盖 250ms 收回全程（文字 90ms 先隐、
    /// 卡片全程可见可对照；泵步长局限见类头注记——帧级证明交真实录屏）＋终态
    /// 塌缩帧。</summary>
    private static void CaptureQrContainerExitFilm(
        SettingsWindow settings,
        Grid overlay,
        string directory,
        string prefix)
    {
        ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
            new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(600));
        RaisePointUp(overlay, UIElement.MouseLeftButtonUpEvent);
        PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(1));
        SaveWindowShell(settings, directory, $"{prefix}-frame-00.png");
        for (var step = 1; step <= 8; step++)
        {
            PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(40));
            SaveWindowShell(settings, directory, $"{prefix}-frame-{step:00}.png");
        }

        PumpUntil(settings.Dispatcher, () => overlay.Visibility == Visibility.Collapsed);
        SaveWindowShell(settings, directory, $"{prefix}-collapsed.png");
    }

    /// <summary>收回交接段细粒度胶片（2026-11-08 关闭回退重编排）：关层后先泵
    /// 175ms（持稳段末＝交接窗前），再以 10ms 步进×7 帧覆盖 175→245ms 卡片
    /// 1→0 与缩略图 0→1 同窗线性换手（QrThumbRevealBeginMs=180 起）；泵步长
    /// 局限见类头注记——帧级证明交真实录屏。</summary>
    private static void CaptureQrContainerHandoverFilm(
        SettingsWindow settings,
        Grid overlay,
        string directory,
        string prefix)
    {
        ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
            new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(600));
        RaisePointUp(overlay, UIElement.MouseLeftButtonUpEvent);
        PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(175));
        SaveWindowShell(settings, directory, $"{prefix}-frame-00.png");
        for (var step = 1; step <= 7; step++)
        {
            PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(10));
            SaveWindowShell(settings, directory, $"{prefix}-frame-{step:00}.png");
        }

        PumpUntil(settings.Dispatcher, () => overlay.Visibility == Visibility.Collapsed);
        SaveWindowShell(settings, directory, $"{prefix}-collapsed.png");
    }

    /// <summary>入场中途反向收回胶片：泵入入场约 120ms 后单击关层，起始帧
    ///（泵 1ms 同口径）＋40ms 步进×8 帧覆盖从当下态续收的飞行段＋终态塌缩帧。</summary>
    private static void CaptureQrContainerReverseFilm(
        SettingsWindow settings,
        Grid overlay,
        string directory,
        string prefix)
    {
        ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
            new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(120));
        RaisePointUp(overlay, UIElement.MouseLeftButtonUpEvent);
        PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(1));
        SaveWindowShell(settings, directory, $"{prefix}-frame-00.png");
        for (var step = 1; step <= 8; step++)
        {
            PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(40));
            SaveWindowShell(settings, directory, $"{prefix}-frame-{step:00}.png");
        }

        PumpUntil(settings.Dispatcher, () => overlay.Visibility == Visibility.Collapsed);
        SaveWindowShell(settings, directory, $"{prefix}-collapsed.png");
    }

    private static void CloseQrOverlay(Grid overlay)
    {
        RaisePointUp(overlay, UIElement.MouseLeftButtonUpEvent);
        PumpUntil(overlay.Dispatcher, () => overlay.Visibility == Visibility.Collapsed);
    }

    /// <summary>泵调度器固定时长（帧间推进动画时钟，供胶片步进采样）。</summary>
    private static void PumpFor(Dispatcher dispatcher, TimeSpan delay)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>泵到容器级过渡文字三元素全部落位（延迟淡入 270→450ms 完成，
    /// Completed 卫生置基值 1）：证据截图须拍全显层而非飞行中层。</summary>
    private static void PumpUntilQrTextShown(SettingsWindow settings)
    {
        PumpUntil(settings.Dispatcher, () =>
            ((System.Windows.UIElement)settings.FindName("WeChatQrTitle")!).Opacity == 1d &&
            ((System.Windows.UIElement)settings.FindName("WeChatIdLargeBlock")!).Opacity == 1d &&
            ((System.Windows.UIElement)settings.FindName("WeChatQrFootnote")!).Opacity == 1d);
    }

    private static void SaveWindowShell(Window settings, string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        var shell = (Border)settings.FindName("WindowShell")!;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(shell.ActualWidth),
            (int)Math.Ceiling(shell.ActualHeight),
            96,
            96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(shell);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, fileName));
        encoder.Save(stream);
    }

    /// <summary>页脚带裁剪：整窗渲染后取底部页脚区（页脚是壳层最后一行）。
    /// 直接渲染页脚子视觉会得到空内容（RenderTargetBitmap 对深层子视觉的行为），
    /// 故从整窗位图按页脚实际高度从底裁剪。</summary>
    private static void SaveFooterCrop(Window settings, string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        var shell = (Border)settings.FindName("WindowShell")!;
        var footer = (FrameworkElement)settings.FindName("SettingsFooter")!;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(shell.ActualWidth),
            (int)Math.Ceiling(shell.ActualHeight),
            96,
            96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(shell);
        var crop = new CroppedBitmap(
            bitmap,
            new System.Windows.Int32Rect(
                0,
                (int)Math.Ceiling(shell.ActualHeight - footer.ActualHeight),
                (int)Math.Ceiling(shell.ActualWidth),
                (int)Math.Ceiling(footer.ActualHeight)));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(crop));
        using var stream = File.Create(Path.Combine(directory, fileName));
        encoder.Save(stream);
    }

    [STATestMethod]
    public void QrOverlay_EscapeClosesLayerWithoutClosingWindow()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(
            directory,
            preferencesStore,
            preferencesStore.Seeded with { AnimationsEnabled = false });
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            var overlay = (Grid)settings.FindName("WeChatQrOverlay")!;
            ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.AreEqual(Visibility.Visible, overlay.Visibility);

            var escape = new KeyEventArgs(
                Keyboard.PrimaryDevice,
                System.Windows.PresentationSource.FromVisual(settings)!,
                Environment.TickCount,
                Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
                Source = settings
            };
            settings.RaiseEvent(escape);

            Assert.IsTrue(escape.Handled, "Esc 在放大层打开时必须被层消费。");
            Assert.AreEqual(Visibility.Collapsed, overlay.Visibility, "Esc 关闭放大层。");
            Assert.IsTrue(settings.IsVisible, "Esc 只关层，不得顺带关闭设置窗。");
        }
        finally
        {
            CloseAll(window);
        }
    }

    // ---- W3 2026-11-05：二维码/微信号交互矩阵与缩放入场（契约 #24 W3 修订；
    //      2026-11-06 修订：右键二维码＝复制二维码图片、复制按钮删除；放大/收回共用插值路径的缩放过渡动画；
    //      2026-11-07 修订：容器级 container transform——动画对象＝外层整卡、文字后段浮现、缩略图防双影）----

    [STATestMethod]
    public void QrThumb_RightClickCopiesQrImageWithoutOpeningOverlay()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            DataObject? captured = null;
            settings.ClipboardWriterOverride = data =>
            {
                captured = data;
                return true;
            };

            RaisePointUp((Button)settings.FindName("WeChatQrButton")!, UIElement.MouseRightButtonUpEvent);

            Assert.IsNotNull(captured, "右键缩略图必须经写入缝复制二维码图片。");
            Assert.IsNull(
                captured.GetData(DataFormats.UnicodeText),
                "右键二维码不再复制微信号文本（2026-11-06 矩阵改裁决，文本复制入口＝微信号行）。");
            Assert.AreSame(
                ((Image)settings.FindName("WeChatQrThumb")!).Source,
                captured.GetData(DataFormats.Bitmap),
                "剪贴板负载必须是缩略图/放大层共用的同一冻结位图实例。");
            Assert.AreEqual(
                "已复制",
                ((Run)settings.FindName("WeChatIdRun")!).Text,
                "反馈就地翻转页脚微信号 Run（触发侧宿主，零 toast）。");
            Assert.AreEqual(
                Visibility.Collapsed,
                ((Grid)settings.FindName("WeChatQrOverlay")!).Visibility,
                "右键复制不得顺带打开放大层（不放大是矩阵裁决）。");
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void FooterWeChatId_LeftAndRightClickCopyThroughSeam()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            DataObject? captured = null;
            var captureCount = 0;
            settings.ClipboardWriterOverride = data =>
            {
                captured = data;
                captureCount++;
                return true;
            };
            var footerRow = (TextBlock)settings.FindName("WeChatIdFooterBlock")!;
            var footerRun = (Run)settings.FindName("WeChatIdRun")!;

            RaisePointUp(footerRow, UIElement.MouseLeftButtonUpEvent);
            Assert.IsNotNull(captured, "页脚微信号行左键必须经写入缝复制。");
            Assert.AreEqual("已复制", footerRun.Text, "左键复制反馈就地翻转本行微信号 Run。");

            RaisePointUp(footerRow, UIElement.MouseRightButtonUpEvent);
            Assert.AreEqual(2, captureCount, "右键与左键同一处理器服两事件，都走写入缝。");
            Assert.AreEqual(
                ProductIdentity.WeChatId,
                (string)captured!.GetData(DataFormats.UnicodeText)!,
                "两键负载同为 ProductIdentity.WeChatId 全文。");
            Assert.AreEqual("已复制", footerRun.Text, "右键复制反馈同位。");
            PumpUntil(settings.Dispatcher, () => footerRun.Text == ProductIdentity.WeChatId);
            Assert.AreEqual(ProductIdentity.WeChatId, footerRun.Text, "反馈延迟复位回单一来源微信号。");

            // 失败态（并入旧 FooterCopy_WritesWeChatIdThroughSeamWithTransientFeedback）：
            // 缝返 false 翻「复制失败」，不假装成功。
            settings.ClipboardWriterOverride = _ => false;
            RaisePointUp(footerRow, UIElement.MouseRightButtonUpEvent);
            Assert.AreEqual("复制失败", footerRun.Text, "写入失败给失败反馈，不假装成功。");
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void OverlayWeChatId_LeftAndRightClickCopyWithoutClosingLayer()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(
            directory,
            preferencesStore,
            preferencesStore.Seeded with { AnimationsEnabled = false });
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            var overlay = (Grid)settings.FindName("WeChatQrOverlay")!;

            DataObject? captured = null;
            var captureCount = 0;
            settings.ClipboardWriterOverride = data =>
            {
                captured = data;
                captureCount++;
                return true;
            };
            var largeRow = (TextBlock)settings.FindName("WeChatIdLargeBlock")!;
            var largeRun = (Run)settings.FindName("WeChatIdLargeRun")!;

            RaisePointUp(largeRow, UIElement.MouseLeftButtonUpEvent);
            Assert.IsNotNull(captured, "放大态微信号行左键必须经写入缝复制。");
            Assert.AreEqual(
                ProductIdentity.WeChatId,
                (string)captured.GetData(DataFormats.UnicodeText)!,
                "放大层复制入口与页脚共用同一写入缝与负载（2026-11-06 并入旧层内复制一致性断言）。");
            Assert.AreEqual("已复制", largeRun.Text, "放大态反馈就地翻转本行微信号 Run。");
            Assert.AreEqual(Visibility.Visible, overlay.Visibility, "左键复制不得关层（e.Handled 阻断冒泡）。");

            RaisePointUp(largeRow, UIElement.MouseRightButtonUpEvent);
            Assert.AreEqual(2, captureCount, "右键与左键同一处理器服两事件。");
            Assert.AreEqual(ProductIdentity.WeChatId, (string)captured!.GetData(DataFormats.UnicodeText)!);
            Assert.AreEqual("已复制", largeRun.Text, "右键复制反馈同位。");
            Assert.AreEqual(
                Visibility.Visible,
                overlay.Visibility,
                "右键复制同样不得关层（e.Handled 阻断冒泡）。");
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void OverlayQrImage_RightClickCopiesImageWithoutClosingLayer()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(
            directory,
            preferencesStore,
            preferencesStore.Seeded with { AnimationsEnabled = false });
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            var overlay = (Grid)settings.FindName("WeChatQrOverlay")!;

            DataObject? captured = null;
            settings.ClipboardWriterOverride = data =>
            {
                captured = data;
                return true;
            };

            RaisePointUp((Border)settings.FindName("WeChatQrLargeCard")!, UIElement.MouseRightButtonUpEvent);

            Assert.IsNotNull(captured, "右键放大图必须经写入缝复制二维码图片。");
            Assert.IsNull(captured.GetData(DataFormats.UnicodeText), "放大图右键＝复制图片，不带文本负载。");
            Assert.AreSame(
                ((Image)settings.FindName("WeChatQrLarge")!).Source,
                captured.GetData(DataFormats.Bitmap),
                "放大层右键负载与页脚缩略图同一冻结位图实例（两处右键一致）。");
            Assert.AreEqual(
                "已复制",
                ((Run)settings.FindName("WeChatIdLargeRun")!).Text,
                "反馈就地翻转放大态微信号 Run。");
            Assert.AreEqual(
                Visibility.Visible,
                overlay.Visibility,
                "放大图右键复制不得关层（e.Handled 阻断冒泡到遮罩关层）。");
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void QrOverlay_RightClickOnBlankAreaClosesLayer()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(
            directory,
            preferencesStore,
            preferencesStore.Seeded with { AnimationsEnabled = false });
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            var overlay = (Grid)settings.FindName("WeChatQrOverlay")!;
            ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.AreEqual(Visibility.Visible, overlay.Visibility);

            RaisePointUp(overlay, UIElement.MouseRightButtonUpEvent);

            Assert.AreEqual(
                Visibility.Collapsed,
                overlay.Visibility,
                "遮罩空白处右键＝关层（与左键成「任意单击关闭」对称语义）。");
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void QrOverlay_OpenWithAnimationsDisabledLeavesTransformIdentity()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(
            directory,
            preferencesStore,
            preferencesStore.Seeded with { AnimationsEnabled = false });
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            CompleteLayout(settings);

            var (scale, translate) = QrZoomTransforms(settings);
            Assert.AreEqual(1d, scale.ScaleX, "动效关＝跳过过渡（ScaleX 恒等）。");
            Assert.AreEqual(1d, scale.ScaleY, "动效关＝跳过过渡（ScaleY 恒等）。");
            Assert.AreEqual(0d, translate.X, "动效关＝跳过过渡（TranslateX 恒等）。");
            Assert.AreEqual(0d, translate.Y, "动效关＝跳过过渡（TranslateY 恒等）。");
            // 容器级过渡新增：文字三元素与缩略图同帧定格全显（容器含文字子树，
            // 文字基值 XAML 为 0、动效关路径必须显式置 1 才可见）。
            foreach (var name in new[] { "WeChatQrTitle", "WeChatIdLargeBlock", "WeChatQrFootnote" })
            {
                Assert.AreEqual(
                    1d,
                    ((System.Windows.UIElement)settings.FindName(name)!).Opacity,
                    $"动效关＝文字元素 {name} 定格全显。");
            }

            Assert.AreEqual(
                1d,
                ((System.Windows.UIElement)settings.FindName("WeChatQrButton")!).Opacity,
                "动效关＝缩略图定格全显（无双影淡出编排）。");
            // 结构拆分后卡片自持 Opacity 加入定格面（2026-11-08）：动效关路径
            // 经 ResetQrTransitionState 定格 1（层可见即全显）。
            Assert.AreEqual(
                1d,
                ((System.Windows.UIElement)settings.FindName("WeChatQrOverlayCard")!).Opacity,
                "动效关＝卡片自持不透明度定格全显。");
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void QrOverlay_EscapeDuringEntranceStartsAnimatedExit()
    {
        // 动效开：打开后不等容器入场完成立即 Esc——动画不阻断命中，层立即进入
        // 关闭路径，且收回走动画（动态 From 续收）而非瞬回恒等（2026-11-06：
        // 收回从瞬时切换改为共用路径的过渡动画）。
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            var overlay = (Grid)settings.FindName("WeChatQrOverlay")!;
            var (scale, translate) = QrZoomTransforms(settings);
            ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            // 泵入入场约 1/4 处（450ms 入场）再 Esc：缩放已明显离开源尺寸，
            // 收回才有可观测的飞行段（过早打断 From≈To 无动画可采样）。
            PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(100));

            var escape = new KeyEventArgs(
                Keyboard.PrimaryDevice,
                System.Windows.PresentationSource.FromVisual(settings)!,
                Environment.TickCount,
                Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
                Source = settings
            };
            settings.RaiseEvent(escape);

            Assert.IsTrue(escape.Handled, "缩放入场进行中 Esc 仍被层立即消费。");
            Assert.IsFalse(
                FadeAnimation.GetIsActive((Border)settings.FindName("WeChatQrScrim")!),
                "Esc 后层立即进入关闭路径（遮罩纱 IsActive 即刻翻 false）。");
            // 收回途中采样：ScaleX 仍在飞行区间且单调递减（非瞬回恒等）。
            var samples = SampleScaleDuringExit(settings, scale, pumpMs: 160);
            Assert.IsTrue(
                samples.Count >= 3,
                $"采样器必须在收回途中取到值（时钟在泵内推进；采样数={samples.Count}）。");
            Assert.IsTrue(
                samples.All(value => value > 0.19d && value < 0.99d),
                $"收回中途必须处于非恒等飞行态（未瞬回；下界＝按钮含框/容器宽比≈0.19；采样={FormatSamples(samples)}）。");
            Assert.IsTrue(
                samples.Zip(samples.Skip(1), (a, b) => b <= a + 1e-6).All(pair => pair),
                $"收回途中 ScaleX 必须单调递减（动画续收；采样={FormatSamples(samples)}）。");

            PumpUntil(settings.Dispatcher, () => overlay.Visibility == Visibility.Collapsed);
            Assert.AreEqual(Visibility.Collapsed, overlay.Visibility, "Esc 在动画中途立即关层。");
            AssertQrTransformIdentity(scale, translate, "收回完成后四属性恒等（零残留）。");
            Assert.IsTrue(settings.IsVisible, "Esc 只关层，不得顺带关闭设置窗。");
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void QrOverlay_ExitZoomAnimatesTowardSourceAndEndsAtIdentity()
    {
        // 动效开：入场动画满（恒等定格）后单击关层——收回自当下恒等态动画回
        // 当下实测源矩形（共用同一插值路径原路返回），收途中非恒等递减，
        // 终态塌缩＋四属性恒等（HoldEnd 终值经 Completed 生命周期复位）。
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            var overlay = (Grid)settings.FindName("WeChatQrOverlay")!;
            var (scale, translate) = QrZoomTransforms(settings);
            ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            // 泵过入场全程（450ms 入场＋余量）到恒等定格。
            PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(600));
            Assert.AreEqual(1d, scale.ScaleX, "入场完成应处于恒等态（收回起点）。");

            RaisePointUp(overlay, UIElement.MouseLeftButtonUpEvent);
            var samples = SampleScaleDuringExit(settings, scale, pumpMs: 160);
            Assert.IsTrue(
                samples.Count >= 3,
                $"采样器必须在收回途中取到值（采样数={samples.Count}）。");
            Assert.IsTrue(
                samples.All(value => value > 0.19d && value < 0.99d),
                $"收回中途必须非恒等（动画在飞；下界＝按钮含框/容器宽比≈0.19；采样={FormatSamples(samples)}）。");
            Assert.IsTrue(
                samples.Zip(samples.Skip(1), (a, b) => b <= a + 1e-6).All(pair => pair),
                $"收回途中 ScaleX 必须单调递减（原路缩回；采样={FormatSamples(samples)}）。");

            PumpUntil(settings.Dispatcher, () => overlay.Visibility == Visibility.Collapsed);
            Assert.AreEqual(Visibility.Collapsed, overlay.Visibility, "收回完成＝层塌缩。");
            AssertQrTransformIdentity(scale, translate, "收回完成态四属性恒等（零终态残留）。");
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void QrOverlay_ReverseDuringEntranceContinuesFromCurrentTransform()
    {
        // 动效开：入场中途（≈120ms）单击遮罩反向收回——新动画以当下插值态为
        // 动态 From（SnapshotAndReplace 替换式中断），首采样仍在飞行区间未瞬跳
        //（既不跳回源尺寸也不跳到恒等），最终恒等塌缩。
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            var overlay = (Grid)settings.FindName("WeChatQrOverlay")!;
            var (scale, translate) = QrZoomTransforms(settings);
            ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(120));
            var reversalSample = scale.ScaleX;
            Assert.IsTrue(
                reversalSample > 0.2d && reversalSample < 0.99d,
                $"入场中途应处于非恒等飞行态（实测 ScaleX={reversalSample:0.###}）。");

            RaisePointUp(overlay, UIElement.MouseLeftButtonUpEvent);
            var samples = SampleScaleDuringExit(settings, scale, pumpMs: 120);
            Assert.IsTrue(
                samples.Count >= 2,
                $"反向收回途中必须取到采样（采样数={samples.Count}）。");
            Assert.IsTrue(
                samples[0] > 0.2d && samples[0] < 0.99d,
                $"反向收回首采样必须仍在飞行区间（动态 From 续收未瞬跳；采样={FormatSamples(samples)}）。");
            Assert.IsTrue(
                samples.Zip(samples.Skip(1), (a, b) => b <= a + 1e-6).All(pair => pair),
                $"反向收回途中 ScaleX 必须递减（从当下态继续收；采样={FormatSamples(samples)}）。");

            PumpUntil(settings.Dispatcher, () => overlay.Visibility == Visibility.Collapsed);
            Assert.AreEqual(Visibility.Collapsed, overlay.Visibility, "反向收回完成＝层塌缩。");
            AssertQrTransformIdentity(scale, translate, "反向收回完成态四属性恒等。");
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void QrOverlay_CloseWithAnimationsDisabledCollapsesImmediatelyAtIdentity()
    {
        // 动效关：关层不走缩放路径——立即塌缩＋四属性恒等（即时定格语义）。
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(
            directory,
            preferencesStore,
            preferencesStore.Seeded with { AnimationsEnabled = false });
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            var overlay = (Grid)settings.FindName("WeChatQrOverlay")!;
            var (scale, translate) = QrZoomTransforms(settings);
            ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.AreEqual(Visibility.Visible, overlay.Visibility);

            RaisePointUp(overlay, UIElement.MouseLeftButtonUpEvent);

            Assert.AreEqual(
                Visibility.Collapsed,
                overlay.Visibility,
                "动效关＝关层立即塌缩（无收回动画、无淡出等待）。");
            AssertQrTransformIdentity(scale, translate, "动效关＝塌缩即恒等（变换零残留）。");
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void QrOverlay_ZoomCompletesAtIdentityTransform()
    {
        // 动效开：容器入场期间以调度器采样器收中途值（PushFrame 长泵内条件
        // 只在首尾被求值，中途态必须由 Timer 采样捕获），泵过 450ms 入场全程
        // 后断言 Scale/Translate 回恒等基值（FillBehavior.Stop，零残留）。
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            var (scale, translate) = QrZoomTransforms(settings);
            var samples = new List<double>();
            var sampler = new DispatcherTimer(DispatcherPriority.Render, settings.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(10)
            };
            sampler.Tick += (_, _) => samples.Add(scale.ScaleX);

            sampler.Start();
            ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(600));
            sampler.Stop();

            Assert.IsTrue(
                samples.Count > 0,
                "采样器必须在入场期间取到值（时钟在泵内推进）。");
            Assert.IsTrue(
                samples.Any(value => value > 0.1d && value < 0.99d),
                $"入场中途必须采样到非恒等缩放态（动画确实在飞；采样={string.Join(",", samples.Select(v => v.ToString("0.###")))}）。");
            AssertQrTransformIdentity(scale, translate, "入场收尾必须回到恒等（无残留变换）。");
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void QrOverlay_ContainerEntranceStartsAtThumbFrameGeometry()
    {
        // 动效开（2026-11-07 容器级几何断言）：入场起飞段采样≈按钮含框/容器外接
        // 矩形比例（from＝页脚缩略图按钮含框外接矩形，ScaleX/Y 各自独立），
        // |sX−sY| 显著非零＝方→矩形形变由 ScaleX≠ScaleY 结构性承载；终态恒等。
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            var card = (Border)settings.FindName("WeChatQrOverlayCard")!;
            var button = (Button)settings.FindName("WeChatQrButton")!;
            var (scale, translate) = QrZoomTransforms(settings);

            ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            // 期望比例必须在开层之后取——容器首次开层前 Collapsed 不参与布局
            //（ActualWidth=0）；开层路径 UpdateLayout 后尺寸即定稿。
            var expectedScaleX = button.ActualWidth / card.ActualWidth;
            var expectedScaleY = button.ActualHeight / card.ActualHeight;
            // 起飞窗采样：Render 优先级 Timer 捕获入场早段（PushFrame 长泵内裸读
            // 只见动画首尾提交值，中途态必须由 Timer 采样捕获——ZoomCompletes 口
            // 径）；取 sX 落入「期望比例＋(0, 0.11]」的首个样本为贴地窗，比例断言
            // 窗口化以吸收采样时钟抖动。
            var fromSample = (Sx: 0d, Sy: 0d);
            var captured = false;
            var sampler = new DispatcherTimer(DispatcherPriority.Render, settings.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(5)
            };
            sampler.Tick += (_, _) =>
            {
                if (!captured &&
                    scale.ScaleX > expectedScaleX && scale.ScaleX <= expectedScaleX + 0.11)
                {
                    fromSample = (scale.ScaleX, scale.ScaleY);
                    captured = true;
                    sampler.Stop();
                }
            };
            sampler.Start();
            PumpUntil(settings.Dispatcher, () => captured);
            sampler.Stop();

            Assert.IsTrue(
                captured,
                "入场早段必须被采样捕获（sX 须落入期望比例＋(0, 0.11] 窗口）。");
            Assert.IsTrue(
                fromSample.Sx >= expectedScaleX - 0.005,
                $"贴地窗 sX 必须≥按钮含框宽/容器宽（实测 {fromSample.Sx:0.###}，期望≈{expectedScaleX:0.###}）。");
            Assert.IsTrue(
                fromSample.Sy >= expectedScaleY - 0.005 &&
                fromSample.Sy <= expectedScaleY + 0.13,
                $"贴地窗 sY 必须≈按钮含框高/容器高（实测 {fromSample.Sy:0.###}，期望≈{expectedScaleY:0.###}）。");
            Assert.IsTrue(
                Math.Abs(fromSample.Sx - fromSample.Sy) > 0.015,
                $"from 几何必须非等比（方→矩形形变；实测 sX={fromSample.Sx:0.###}、sY={fromSample.Sy:0.###}）。");

            PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(600));
            AssertQrTransformIdentity(scale, translate, "容器入场收尾回到恒等（终态零残留）。");
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void QrOverlay_TextFadesInAfterContainerDeparture()
    {
        // 动效开（2026-11-07 文字编排时序断言）：飞行早段文字三元素仍为基值 0
        // 且容器在飞；270ms 起延迟淡入（总进度≈60%）后段可见；落位后文字定格
        // 基值 1（HoldEnd＋Completed 卫生）。容器尺寸布局恒定，文字只动 Opacity。
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            var title = (System.Windows.UIElement)settings.FindName("WeChatQrTitle")!;
            var idRow = (System.Windows.UIElement)settings.FindName("WeChatIdLargeBlock")!;
            var footnote = (System.Windows.UIElement)settings.FindName("WeChatQrFootnote")!;
            var (scale, translate) = QrZoomTransforms(settings);
            ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));

            // t≈120ms：容器已离位在飞，文字三元素未起步（基值 0）。
            PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(120));
            Assert.AreEqual(0d, title.Opacity, "t≈120ms 标题仍为基值 0（未随容器缩放呈现）。");
            Assert.AreEqual(0d, idRow.Opacity, "t≈120ms 微信号行仍为基值 0。");
            Assert.AreEqual(0d, footnote.Opacity, "t≈120ms 尾注仍为基值 0。");
            Assert.IsTrue(
                scale.ScaleX > 0.2d && scale.ScaleX < 0.99d,
                $"t≈120ms 容器必须在飞（实测 ScaleX={scale.ScaleX:0.###}）。");

            // t≈340ms：文字延迟淡入已起步（270ms 起 180ms）。
            PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(220));
            Assert.IsTrue(title.Opacity > 0d, $"t≈340ms 标题应已开始浮现（实测 {title.Opacity:0.###}）。");
            Assert.IsTrue(idRow.Opacity > 0d, $"t≈340ms 微信号行应已开始浮现（实测 {idRow.Opacity:0.###}）。");
            Assert.IsTrue(footnote.Opacity > 0d, $"t≈340ms 尾注应已开始浮现（实测 {footnote.Opacity:0.###}）。");

            // 落位后（>450ms＋文字尾 450ms）：文字定格 1、容器恒等。
            PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(400));
            Assert.AreEqual(1d, title.Opacity, "落位后标题定格基值 1。");
            Assert.AreEqual(1d, idRow.Opacity, "落位后微信号行定格基值 1。");
            Assert.AreEqual(1d, footnote.Opacity, "落位后尾注定格基值 1。");
            AssertQrTransformIdentity(scale, translate, "落位后容器恒等。");
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void QrOverlay_ExitHidesTextBeforeContainerLands()
    {
        // 动效开（2026-11-07 收回编排时序断言）：关层早段文字先隐（90ms 快速
        // 淡出，显式 From 当下值）且容器同时收回在飞；落位后文字 0、缩略图 1
        //（防双影回显交接）。
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            var overlay = (Grid)settings.FindName("WeChatQrOverlay")!;
            var title = (System.Windows.UIElement)settings.FindName("WeChatQrTitle")!;
            var idRow = (System.Windows.UIElement)settings.FindName("WeChatIdLargeBlock")!;
            var footnote = (System.Windows.UIElement)settings.FindName("WeChatQrFootnote")!;
            var thumb = (System.Windows.UIElement)settings.FindName("WeChatQrButton")!;
            var (scale, translate) = QrZoomTransforms(settings);
            ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(600));
            Assert.AreEqual(1d, title.Opacity, "前置：落位后标题应为 1。");

            RaisePointUp(overlay, UIElement.MouseLeftButtonUpEvent);
            // 关层早段：Render 优先级 Timer 捕获收回全程四元组（文字三元素＋容器
            // scale；裸条件轮询只见动画首尾提交值，中途态必须由 Timer 采样捕获）。
            var samples = new List<(double Scale, double Title, double IdRow, double Footnote)>();
            var sampler = new DispatcherTimer(DispatcherPriority.Render, settings.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(5)
            };
            sampler.Tick += (_, _) =>
                samples.Add((scale.ScaleX, title.Opacity, idRow.Opacity, footnote.Opacity));
            sampler.Start();
            PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(200));
            sampler.Stop();
            Assert.IsTrue(
                samples.Count >= 10,
                $"收回段必须被采样捕获（采样数={samples.Count}）。");
            // 容器在飞段＝scale 未回恒等的样本（关层进行中变换只从恒等向源比例
            // 递减、HoldEnd 定格非恒等终值，恒等只在动画前或 Completed 复位后）。
            var flight = samples.Where(p => p.Scale < 0.99d).ToList();
            Assert.IsTrue(
                flight.Count >= 3,
                $"容器收回飞行段必须被采样捕获（飞行采样={flight.Count}，全样本={samples.Count}）。");
            Assert.IsTrue(
                flight.Any(p => p.Title is > 0d and < 1d),
                $"文字必须先于容器落位淡出且经过中间态（飞行段标题采样={FormatSamples(flight.Select(p => p.Title).ToList())}）。");
            Assert.IsTrue(
                flight.Any(p => p.IdRow < 1d) && flight.Any(p => p.Footnote < 1d),
                "微信号行与尾注须与标题同步先隐。");
            Assert.IsTrue(
                flight.First().Title >= flight.Last().Title,
                $"飞行段标题单调下降（首={flight.First().Title:0.###}、末={flight.Last().Title:0.###}）。");

            PumpUntil(settings.Dispatcher, () => overlay.Visibility == Visibility.Collapsed);
            Assert.AreEqual(0d, title.Opacity, "收回完成后标题基值 0（层关静止态）。");
            Assert.AreEqual(0d, idRow.Opacity, "收回完成后微信号行基值 0。");
            Assert.AreEqual(0d, footnote.Opacity, "收回完成后尾注基值 0。");
            Assert.AreEqual(1d, thumb.Opacity, "收回完成后缩略图回显 1（防双影终态）。");
            AssertQrTransformIdentity(scale, translate, "收回完成后容器恒等。");
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void QrOverlay_ReopenDuringExitRequeuesTextAndContinuesContainer()
    {
        // 动效开（2026-11-07 中断重排语义断言，Q5）：关中途再开——容器层自当下
        // 插值态续走（首采样仍在飞行区间、不瞬跳回源矩形也不瞬跳恒等），文字层
        // 不续走、自基值 0 按开层方向重排队（延迟淡入）。
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            var overlay = (Grid)settings.FindName("WeChatQrOverlay")!;
            var title = (System.Windows.UIElement)settings.FindName("WeChatQrTitle")!;
            var (scale, translate) = QrZoomTransforms(settings);
            ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(600));

            RaisePointUp(overlay, UIElement.MouseLeftButtonUpEvent);
            // 关中途再开：Render 优先级 Timer 捕获收回中段首个落入 (0.35, 0.70)
            // 的样本，并在**同一调度器批次内立刻再开**（再开若等到长泵返回，收回
            // 动画可能已 Completed 复位恒等——续走断言的时序前提即「收回在飞时
            // 再开」）；随后同采样器捕获再开入场首段提交值。
            var midExitSample = 0d;
            var reopened = false;
            var reopenSamples = new List<double>();
            var reopenTitleSamples = new List<double>();
            var sampler = new DispatcherTimer(DispatcherPriority.Render, settings.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(5)
            };
            sampler.Tick += (_, _) =>
            {
                if (!reopened)
                {
                    if (scale.ScaleX is > 0.35d and < 0.7d)
                    {
                        midExitSample = scale.ScaleX;
                        ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
                            new RoutedEventArgs(ButtonBase.ClickEvent));
                        reopened = true;
                    }

                    return;
                }

                if (reopenSamples.Count < 3)
                {
                    // 再开后首段采样（远早于文字淡入 BeginTime 270ms）：容器值进
                    // 续走判定，标题值进「自 0 重排队」判定。
                    reopenSamples.Add(scale.ScaleX);
                    reopenTitleSamples.Add(title.Opacity);
                }
            };
            sampler.Start();
            PumpUntil(settings.Dispatcher, () => reopened && reopenSamples.Count >= 3);
            sampler.Stop();
            Assert.IsTrue(
                midExitSample > 0.3d && midExitSample < 0.99d,
                $"关中途应处于非恒等飞行态（实测 ScaleX={midExitSample:0.###}）。");
            Assert.AreEqual(Visibility.Visible, overlay.Visibility, "再开＝层立即回到打开路径。");
            Assert.IsTrue(
                reopenSamples.Count >= 1,
                $"再开入场必须被采样捕获（采样数={reopenSamples.Count}）。");
            var reopenSample = reopenSamples[0];
            Assert.IsTrue(
                Math.Abs(reopenSample - midExitSample) < 0.08d && reopenSample > 0.3d,
                $"再开首采样必须自当下态续走（不瞬跳回源矩形≈0.19 也不瞬跳恒等；" +
                $"关中途={midExitSample:0.###}、再开首采样={reopenSample:0.###}）。");
            Assert.IsTrue(
                reopenTitleSamples.Count > 0 && reopenTitleSamples.All(v => v == 0d),
                $"文字不续走：再开后首段标题须仍为基值 0（重排队、延迟淡入未起步；" +
                $"采样={FormatSamples(reopenTitleSamples)}）。");

            PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(600));
            AssertQrTransformIdentity(scale, translate, "续走入场完成＝容器恒等。");
            Assert.AreEqual(1d, title.Opacity, "重排队文字落位后定格 1。");
        }
        finally
        {
            CloseAll(window);
        }
    }

    // ---- d1 2026-11-08：关闭回退「对齐落位」重编排阈值断言（report-plan-d.md
    //      §三.8 A-E；契约 #24 修订，Issue #325）——时间轴由容器几何反解，
    //      消除挂钟与采样抖动（10ms 网格确定性采样，不真起渲染帧循环）----

    [STATestMethod]
    public void QrOverlay_ExitThumbStaysHiddenUntilContainerNearlyHome()
    {
        // A 防提前回显：容器回程进度 p<0.85（仍≈1.6× 落位尺寸）的任意采样点，
        // 缩略图 Opacity≤0.10——缩略图回显必须等到容器接近落位（对齐落位），
        // 不得在飞行中段半显与飞行大卡同屏双影（根因 R1）。
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            var overlay = (Grid)settings.FindName("WeChatQrOverlay")!;
            var scrim = (Border)settings.FindName("WeChatQrScrim")!;
            var button = (Button)settings.FindName("WeChatQrButton")!;
            var card = (Border)settings.FindName("WeChatQrOverlayCard")!;
            var (scale, _) = QrZoomTransforms(settings);
            ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(600));
            // 收回终末比例＝页脚缩略图按钮含框宽/容器宽（纯平移链路宽度直通；
            // 必须开层后测——容器 Collapsed 不参与布局）。
            var landingScale = button.ActualWidth / card.ActualWidth;

            RaisePointUp(overlay, UIElement.MouseLeftButtonUpEvent);
            var samples = SampleQrExitChoreography(settings, scale, scrim, 260);
            Assert.IsTrue(
                samples.Count >= 8,
                $"收回段必须被采样捕获（采样数={samples.Count}）。");
            var early = samples
                .Select(s => QrExitProgressOf(s.ScaleX, landingScale))
                .Where(p => p < 0.85d)
                .ToList();
            Assert.IsTrue(
                early.Count >= 2,
                "必须采到容器未近落位的早段样本（防空洞通过）。");
            foreach (var (sample, progress) in samples.Zip(
                samples.Select(s => QrExitProgressOf(s.ScaleX, landingScale))))
            {
                if (progress < 0.85d)
                {
                    Assert.IsTrue(
                        sample.ThumbOpacity <= 0.10d,
                        $"A 防提前回显：容器回程 p={progress:0.###}（<0.85，t≈{QrExitProgressToMs(progress):0}ms）"
                        + $"时缩略图 Opacity={sample.ThumbOpacity:0.###}，不得 >0.10（飞行中段双影）。");
                }
            }

            PumpUntil(settings.Dispatcher, () => overlay.Visibility == Visibility.Collapsed);
            Assert.AreEqual(
                1d,
                ((System.Windows.UIElement)settings.FindName("WeChatQrButton")!).Opacity,
                "收回完成后缩略图回显终态 1。");
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void QrOverlay_ExitMaskFadesWithoutLateBurst()
    {
        // C 无末段骤曝：收回期遮罩纱任意相邻采样的每 10ms 降幅≤0.095（率≤
        // 0.0095/ms；report-plan-d.md 草案值 0.09，按其 §六.4 授权在 ±0.02 内
        // 上调贴线余量——QrZoomOutKeySpline 峰值斜率≈0.0905/10ms），且 t=200ms
        // 时遮罩纱≤0.06——遮罩淡出须与容器同用减速曲线（末端水平），不得在
        // 末段斜率骤增让整窗亮度跳变（根因 R2）。
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            var overlay = (Grid)settings.FindName("WeChatQrOverlay")!;
            var scrim = (Border)settings.FindName("WeChatQrScrim")!;
            var button = (Button)settings.FindName("WeChatQrButton")!;
            var card = (Border)settings.FindName("WeChatQrOverlayCard")!;
            var (scale, _) = QrZoomTransforms(settings);
            ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(600));
            var landingScale = button.ActualWidth / card.ActualWidth;

            RaisePointUp(overlay, UIElement.MouseLeftButtonUpEvent);
            var samples = SampleQrExitChoreography(settings, scale, scrim, 260);
            Assert.IsTrue(
                samples.Count >= 8,
                $"收回段必须被采样捕获（采样数={samples.Count}）。");
            var timeline = QrExitTimeline(samples, landingScale);
            for (var index = 1; index < timeline.Count; index++)
            {
                var (previousTime, previousSample) = timeline[index - 1];
                var (time, sample) = timeline[index];
                if (time - previousTime < 1d)
                {
                    continue;
                }

                var ratePerMs = (previousSample.MaskOpacity - sample.MaskOpacity) / (time - previousTime);
                Assert.IsTrue(
                    ratePerMs <= 0.0095d,
                    $"C 无末段骤曝：相邻采样遮罩纱降幅率={ratePerMs:0.0000}/ms（t≈{previousTime:0}→{time:0}ms，"
                    + $"折算 10ms Δ={ratePerMs * 10d:0.000}），不得超过 0.095/10ms。");
            }

            var maskAt200 = QrInterpAtTimeline(timeline, 200d, s => s.MaskOpacity);
            Assert.IsTrue(
                maskAt200 <= 0.06d,
                $"C 无末段骤曝：t=200ms 遮罩纱插值={maskAt200:0.000}，须 ≤0.06（末端水平无骤塌）。");

            PumpUntil(settings.Dispatcher, () => overlay.Visibility == Visibility.Collapsed);
            Assert.AreEqual(0d, scrim.Opacity, "收回完成后遮罩纱归零。");
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void QrOverlay_ExitHandoverCrossfadeAlignsAtLanding()
    {
        // B 回显对齐＋D 交接合成不塌＋E 飞行可见（report-plan-d.md §三.8）：
        // B＝t=200ms 缩略图 ∈[0.25,0.45]（线性 (200−180)/70≈0.286，防回显被
        // 推迟到不可见）；D＝t∈[185,245] 卡片＋缩略图 ≥0.95（同窗线性交叉，
        // 落位点合成存在感恒≈1）；E＝t∈[0,150] 卡片 ≥0.90（防「早溶解」退化，
        // 飞行期卡片全程可见）。
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            var overlay = (Grid)settings.FindName("WeChatQrOverlay")!;
            var scrim = (Border)settings.FindName("WeChatQrScrim")!;
            var button = (Button)settings.FindName("WeChatQrButton")!;
            var card = (Border)settings.FindName("WeChatQrOverlayCard")!;
            var (scale, _) = QrZoomTransforms(settings);
            ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(600));
            var landingScale = button.ActualWidth / card.ActualWidth;

            RaisePointUp(overlay, UIElement.MouseLeftButtonUpEvent);
            var samples = SampleQrExitChoreography(settings, scale, scrim, 260);
            Assert.IsTrue(
                samples.Count >= 8,
                $"收回段必须被采样捕获（采样数={samples.Count}）。");
            var timeline = QrExitTimeline(samples, landingScale);

            var thumbAt200 = QrInterpAtTimeline(timeline, 200d, s => s.ThumbOpacity);
            Assert.IsTrue(
                thumbAt200 is >= 0.25d and <= 0.45d,
                $"B 回显对齐：t=200ms 缩略图插值={thumbAt200:0.000}，须 ∈[0.25,0.45]"
                + "（QrThumbRevealBeginMs 起线性回显，不得提前或推迟到不可见）。");

            Assert.IsTrue(
                timeline.Count(pair => pair.TimeMs is >= 185d and <= 245d) >= 3,
                "D 必须采到交接段样本（t∈[185,245]ms）。");
            foreach (var (time, sample) in timeline.Where(pair => pair.TimeMs is >= 185d and <= 245d))
            {
                Assert.IsTrue(
                    sample.CardOpacity + sample.ThumbOpacity >= 0.95d,
                    $"D 交接合成不塌：t={time:0}ms 卡片＋缩略图={sample.CardOpacity + sample.ThumbOpacity:0.000}"
                    + "（同窗线性交叉，落位点合成存在感不得塌）。");
            }

            Assert.IsTrue(
                timeline.Count(pair => pair.TimeMs <= 150d) >= 2,
                "E 必须采到飞行段样本（t≤150ms）。");
            foreach (var (time, sample) in timeline.Where(pair => pair.TimeMs <= 150d))
            {
                Assert.IsTrue(
                    sample.CardOpacity >= 0.90d,
                    $"E 飞行可见：t={time:0}ms 卡片 Opacity={sample.CardOpacity:0.000}，须 ≥0.90"
                    + "（卡片不得随遮罩在飞行中段早溶解）。");
            }

            PumpUntil(settings.Dispatcher, () => overlay.Visibility == Visibility.Collapsed);
            Assert.AreEqual(
                1d,
                ((System.Windows.UIElement)settings.FindName("WeChatQrButton")!).Opacity,
                "B 终态：收回完成后缩略图回显 1。");
            Assert.AreEqual(
                0d,
                ((System.Windows.UIElement)settings.FindName("WeChatQrOverlayCard")!).Opacity,
                "收回完成后卡片随层隐没（终态基值 0）。");
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void QrOverlay_DegradedExitFadesInPlaceWithoutIdentitySnap()
    {
        // R4 退化路径（Measure 求值不可达＝动效开但几何非法，构造＝页脚容器
        // Collapsed＋UpdateLayout 使缩略图按钮零尺寸）：纯淡出——容器快照当下值
        // 定基后清动画（定格在飞、禁瞬时归恒等＝居中放大位原位闪现的单点故障）、
        // 卡片线性淡出 QrZoomOutMs、层不提前塌缩，终态与正常路径逐值一致。
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore);
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            var overlay = (Grid)settings.FindName("WeChatQrOverlay")!;
            var footer = (FrameworkElement)settings.FindName("SettingsFooter")!;
            var card = (System.Windows.UIElement)settings.FindName("WeChatQrOverlayCard")!;
            var thumb = (System.Windows.UIElement)settings.FindName("WeChatQrButton")!;
            var (scale, translate) = QrZoomTransforms(settings);
            ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            // 泵入入场约 120ms：容器离源在飞（非恒等），退化收回才有可观测的
            // 「定格在飞」段。
            PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(120));
            Assert.IsTrue(
                scale.ScaleX is > 0.2d and < 0.99d,
                $"前置：入场中途应处于非恒等飞行态（实测 ScaleX={scale.ScaleX:0.###}）。");
            footer.Visibility = Visibility.Collapsed;
            settings.UpdateLayout();

            RaisePointUp(overlay, UIElement.MouseLeftButtonUpEvent);
            PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(100));
            Assert.AreEqual(
                Visibility.Visible,
                overlay.Visibility,
                "退化收回＝纯淡出（QrZoomOutMs 内层不提前塌缩）。");
            var frozenScale = scale.ScaleX;
            Assert.IsTrue(
                frozenScale is > 0.2d and < 0.99d,
                $"退化收回禁瞬时恒等：容器须定格在飞（实测 ScaleX={frozenScale:0.###}）。");
            Assert.IsTrue(
                card.Opacity is > 0.3d and < 0.8d,
                $"退化收回卡片线性淡出中段（t≈100ms 期望≈0.6，实测 {card.Opacity:0.000}）。");

            PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(80));
            Assert.AreEqual(
                frozenScale,
                scale.ScaleX,
                1e-9,
                "退化收回容器定格（快照定基，不再续飞也不归恒等）。");
            Assert.AreEqual(
                Visibility.Visible,
                overlay.Visibility,
                "退化收回未满 QrZoomOutMs 层保持可见（时长与正常路径一致）。");

            PumpUntil(settings.Dispatcher, () => overlay.Visibility == Visibility.Collapsed);
            Assert.AreEqual(Visibility.Collapsed, overlay.Visibility, "退化收回完成＝层塌缩（卡片淡出 Completed 完成点）。");
            AssertQrTransformIdentity(scale, translate, "退化收回终态容器恒等（与正常路径逐值一致）。");
            Assert.AreEqual(0d, card.Opacity, "退化收回终态卡片 0。");
            Assert.AreEqual(1d, thumb.Opacity, "退化收回终态缩略图回显 1。");
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void QrOverlay_ExitClearsThumbResidueFromDisabledOpenSession()
    {
        // R5 清除段（动效跨会话残留）：动效关开层（缩略图定格 1）→ 动效开 →
        // 关层——收回关键帧清除段须把残留 1 压到 0（90ms 内）并持隐至
        // QrThumbRevealBeginMs（180ms 前 ≤0.10），不得全程 1 在遮罩下待命。
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(
            directory,
            preferencesStore,
            preferencesStore.Seeded with { AnimationsEnabled = false });
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            var overlay = (Grid)settings.FindName("WeChatQrOverlay")!;
            var scrim = (Border)settings.FindName("WeChatQrScrim")!;
            var button = (Button)settings.FindName("WeChatQrButton")!;
            var card = (Border)settings.FindName("WeChatQrOverlayCard")!;
            var (scale, _) = QrZoomTransforms(settings);
            ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            CompleteLayout(settings);
            Assert.AreEqual(
                1d,
                ((System.Windows.UIElement)settings.FindName("WeChatQrButton")!).Opacity,
                "前置：动效关开层缩略图定格 1（残留复现前提）。");
            // 动效开关走真实 UI 路径（Checked → ApplyPreferences）翻转运行中偏好。
            ((CheckBox)settings.FindName("AnimationsToggle")!).IsChecked = true;

            RaisePointUp(overlay, UIElement.MouseLeftButtonUpEvent);
            var samples = SampleQrExitChoreography(settings, scale, scrim, 260);
            Assert.IsTrue(
                samples.Count >= 8,
                $"收回段必须被采样捕获（采样数={samples.Count}）。");
            Assert.IsTrue(
                samples.Any(s => s.ScaleX < 0.99d),
                "动效开态关层＝容器收回动画在飞（翻转生效）。");
            var landingScale = button.ActualWidth / card.ActualWidth;
            var timeline = QrExitTimeline(samples, landingScale);
            Assert.IsTrue(
                timeline.Count(pair => pair.TimeMs is >= 95d and <= 178d) >= 2,
                "清除段＋持隐段必须被采样覆盖（t∈[95,178]ms）。");
            foreach (var (time, sample) in timeline.Where(pair => pair.TimeMs is >= 95d and <= 178d))
            {
                Assert.IsTrue(
                    sample.ThumbOpacity <= 0.10d,
                    $"R5 清除段：t={time:0}ms 缩略图 Opacity={sample.ThumbOpacity:0.000}，须 ≤0.10"
                    + "（残留 1 须在 90ms 清除段内压到 0 并持隐至交接）。");
            }

            PumpUntil(settings.Dispatcher, () => overlay.Visibility == Visibility.Collapsed);
            Assert.AreEqual(
                1d,
                ((System.Windows.UIElement)settings.FindName("WeChatQrButton")!).Opacity,
                "收回完成后缩略图回显终态 1。");
        }
        finally
        {
            CloseAll(window);
        }
    }

    [TestMethod]
    public void QrRestoreHandover_ConstantsInterlock()
    {
        // 常量互锁（#34 单一源）：QrThumbRevealBeginMs 锚定 QrZoomOutKeySpline
        // 95% 回程反解（x≈0.722×QrZoomOutMs），允许 [0.71,0.73]×QrZoomOutMs 带
        //（防实验期改 QrZoomOutMs 时回显时点漂移）；交接段时长恒等差值
        // QrZoomOutMs−QrThumbRevealBeginMs（常量以差值表达式声明，锁未来字面量改写）。
        // 经局部变量中转：绕开 MSTEST0032 对「常量 vs 常量」的恒真折叠告警——
        // 这把锁防的是未来改动把回显时点漂离 95% 回程锚（同 CategoryRailLabelTests 惯例）。
        double revealBeginMs = DesignTokens.QrThumbRevealBeginMs;
        double zoomOutMs = DesignTokens.QrZoomOutMs;
        double handoverFadeMs = DesignTokens.QrHandoverFadeMs;
        Assert.IsTrue(
            revealBeginMs >= (0.71d * zoomOutMs) && revealBeginMs <= (0.73d * zoomOutMs),
            $"QrThumbRevealBeginMs={revealBeginMs} 须落在 [0.71,0.73]×QrZoomOutMs"
            + $"（={0.71d * zoomOutMs:0.#}~{0.73d * zoomOutMs:0.#}，95% 回程反解锚）。");
        Assert.AreEqual(
            zoomOutMs - revealBeginMs,
            handoverFadeMs,
            "QrHandoverFadeMs 恒等于 QrZoomOutMs−QrThumbRevealBeginMs（交接段差值恒等）。");
    }

    /// <summary>收回编排采样四元组：容器 ScaleX＋缩略图/遮罩/卡片 Opacity 同步采样。</summary>
    private sealed record QrExitChoreographySample(
        double ScaleX,
        double ThumbOpacity,
        double MaskOpacity,
        double CardOpacity);

    /// <summary>收回编排多属性确定性采样：Render 优先级 10ms DispatcherTimer 在
    /// PushFrame 泵内同步采四属性（同 ZoomCompletes 口径、不真起渲染帧循环）；
    /// 收回启动前的前置采样（ScaleX 恒等基值）剥除，同 SampleScaleDuringExit。
    /// 时间轴不经挂钟——由容器进度反解（QrExitProgressToMs）。</summary>
    private static List<QrExitChoreographySample> SampleQrExitChoreography(
        SettingsWindow settings,
        ScaleTransform scale,
        UIElement mask,
        int pumpMs)
    {
        var thumb = (System.Windows.UIElement)settings.FindName("WeChatQrButton")!;
        var card = (System.Windows.UIElement)settings.FindName("WeChatQrOverlayCard")!;
        var samples = new List<QrExitChoreographySample>();
        var sampler = new DispatcherTimer(DispatcherPriority.Render, settings.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(10)
        };
        sampler.Tick += (_, _) => samples.Add(new QrExitChoreographySample(
            scale.ScaleX, thumb.Opacity, mask.Opacity, card.Opacity));
        sampler.Start();
        PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(pumpMs));
        sampler.Stop();
        while (samples.Count > 0 && samples[0].ScaleX >= 0.999d)
        {
            samples.RemoveAt(0);
        }

        return samples;
    }

    /// <summary>容器收回进度 p＝(1−ScaleX)/(1−终末比例)（起点恒等 1、终点落位比例
    /// landingScale 的线性映射，与 QrZoomOutKeySpline 施于时间的口径一致）。</summary>
    private static double QrExitProgressOf(double scaleX, double landingScale) =>
        Math.Clamp((1d - scaleX) / (1d - landingScale), 0d, 1d);

    /// <summary>容器收回进度 p→动画时间（ms）：QrZoomOutKeySpline 反解（曲线单调，
    /// 二分 40 次）——采样时间轴由容器几何反解，消除挂钟/采样抖动。</summary>
    private static double QrExitProgressToMs(double progress)
    {
        var easing = SplineEase.FromToken(DesignTokens.QrZoomOutKeySpline);
        var low = 0d;
        var high = 1d;
        for (var index = 0; index < 40; index++)
        {
            var mid = (low + high) / 2d;
            if (easing.Ease(mid) < progress)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return (low + high) / 2d * DesignTokens.QrZoomOutMs;
    }

    /// <summary>采样时间轴：每样本配反解时间（升序；同段线性关键帧上插值精确）。</summary>
    private static List<(double TimeMs, QrExitChoreographySample Sample)> QrExitTimeline(
        List<QrExitChoreographySample> samples,
        double landingScale) =>
        samples.Select(s => (QrExitProgressToMs(QrExitProgressOf(s.ScaleX, landingScale)), s)).ToList();

    /// <summary>时间轴上取 tMs 处的线性插值（括号样本同关键帧段内插值精确；
    /// 越界取端值）。</summary>
    private static double QrInterpAtTimeline(
        List<(double TimeMs, QrExitChoreographySample Sample)> timeline,
        double tMs,
        Func<QrExitChoreographySample, double> pick)
    {
        if (tMs <= timeline[0].TimeMs)
        {
            return pick(timeline[0].Sample);
        }

        for (var index = 1; index < timeline.Count; index++)
        {
            if (timeline[index].TimeMs >= tMs)
            {
                var (previousTime, previousSample) = timeline[index - 1];
                var (time, sample) = timeline[index];
                if (time <= previousTime)
                {
                    return pick(sample);
                }

                var weight = (tMs - previousTime) / (time - previousTime);
                return pick(previousSample) + ((pick(sample) - pick(previousSample)) * weight);
            }
        }

        return pick(timeline[^1].Sample);
    }

    private static void RaisePointUp(UIElement target, RoutedEvent routedEvent)
    {
        var button = routedEvent == UIElement.MouseRightButtonUpEvent ? MouseButton.Right : MouseButton.Left;
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button)
        {
            RoutedEvent = routedEvent,
            Source = target
        });
    }

    /// <summary>QR 容器级过渡变换（构造函数建组挂外层容器：先 Scale 后 Translate，
    /// 顺序冻结）。</summary>
    private static (ScaleTransform Scale, TranslateTransform Translate) QrZoomTransforms(SettingsWindow settings)
    {
        var card = (Border)settings.FindName("WeChatQrOverlayCard")!;
        var group = (TransformGroup)card.RenderTransform!;
        return ((ScaleTransform)group.Children[0], (TranslateTransform)group.Children[1]);
    }

    /// <summary>收回途中 ScaleX 采样：Render 优先级 Timer 每 10ms 采一次、泵
    /// pumpMs 后停（收回动画飞行段的当下插值态捕获，同 ZoomCompletes 口径）；
    /// 收回启动后首个时钟 tick 之前读到的是恒等基值，先剥掉这类前置采样。</summary>
    private static List<double> SampleScaleDuringExit(
        SettingsWindow settings,
        ScaleTransform scale,
        int pumpMs)
    {
        var samples = new List<double>();
        var sampler = new DispatcherTimer(DispatcherPriority.Render, settings.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(10)
        };
        sampler.Tick += (_, _) => samples.Add(scale.ScaleX);
        sampler.Start();
        PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(pumpMs));
        sampler.Stop();
        while (samples.Count > 0 && samples[0] >= 0.999d)
        {
            samples.RemoveAt(0);
        }

        return samples;
    }

    private static string FormatSamples(List<double> samples) =>
        string.Join(",", samples.Select(v => v.ToString("0.###")));

    /// <summary>终态恒等断言（入场 FillBehavior.Stop 与收回 Completed 复位共用口径）。</summary>
    private static void AssertQrTransformIdentity(
        ScaleTransform scale,
        TranslateTransform translate,
        string message)
    {
        Assert.AreEqual(1d, scale.ScaleX, $"{message}（ScaleX）。");
        Assert.AreEqual(1d, scale.ScaleY, $"{message}（ScaleY）。");
        Assert.AreEqual(0d, translate.X, $"{message}（TranslateX）。");
        Assert.AreEqual(0d, translate.Y, $"{message}（TranslateY）。");
    }

    // ---- 基建（按本测试套既有各文件自持 helper 的模式复制）----

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
