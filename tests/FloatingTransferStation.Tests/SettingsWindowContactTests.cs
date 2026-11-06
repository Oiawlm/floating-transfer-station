using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 设置窗页脚「关于」区联系方式（W5 2026-11-05；W3 2026-11-05 交互矩阵与
/// 缩放入场）：微信号文本与二维码位图全部来自 ProductIdentity 单一来源
///（页脚/放大层共用同一冻结位图实例）；「复制」经可注入剪贴板写入缝落盘
///（harness 密封缺省＝响亮失败 stub，契约 #53 口径——未显式声明写意图的
/// 测试一旦触达真实剪贴板即失败），反馈为短文案瞬时翻转；放大层（设置窗
/// 内遮罩，不新建窗口）从缩略图单击打开、左/右键单击遮罩任意处或 Esc 关闭
///（Esc 只关层不关窗），层内附一致复制入口；微信号两态行左右键皆复制
///（放大态复制不关层），动效开时 QR 卡自缩略图源位置缩放入场、关层自当下态
/// 原路缩回源位置（放大/收回共用同一进度 p 插值路径：可中断、动态 From、
/// 终态恒等零残留），动效关＝变换恒等终态即定格。
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
    public void FooterCopy_WritesWeChatIdThroughSeamWithTransientFeedback()
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
            var copy = (Button)settings.FindName("WeChatCopyButton")!;

            copy.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.IsNotNull(captured, "复制必须经写入缝（不触碰系统剪贴板）。");
            Assert.AreEqual(
                ProductIdentity.WeChatId,
                (string)captured.GetData(DataFormats.UnicodeText)!,
                "剪贴板负载必须是 ProductIdentity.WeChatId 全文。");
            Assert.AreEqual("已复制", copy.Content, "复制成功给短文案瞬时反馈。");
            PumpUntil(settings.Dispatcher, () => Equals(copy.Content, "复制"));
            Assert.AreEqual("复制", copy.Content, "反馈延迟复位为常态文案。");

            settings.ClipboardWriterOverride = _ => false;
            copy.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.AreEqual("复制失败", copy.Content, "写入失败给失败反馈，不假装成功。");
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
            Assert.AreEqual(
                Visibility.Visible,
                ((Button)settings.FindName("WeChatCopyLargeButton")!).Visibility,
                "放大层内必须有一致复制入口。");

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

    [STATestMethod]
    public void QrOverlay_CopyEntrySharesTheSeamAndFeedback()
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

            DataObject? captured = null;
            settings.ClipboardWriterOverride = data =>
            {
                captured = data;
                return true;
            };
            var largeCopy = (Button)settings.FindName("WeChatCopyLargeButton")!;
            largeCopy.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.IsNotNull(captured, "放大层复制入口与页脚共用同一写入缝。");
            Assert.AreEqual(ProductIdentity.WeChatId, (string)captured.GetData(DataFormats.UnicodeText)!);
            Assert.AreEqual("已复制", largeCopy.Content, "放大层复制反馈就地在触发按钮上。");
            Assert.AreEqual(
                Visibility.Visible,
                ((Grid)settings.FindName("WeChatQrOverlay")!).Visibility,
                "复制按钮的抬起被 Button 消费，不得顺带关闭放大层。");
        }
        finally
        {
            CloseAll(window);
        }
    }

    /// <summary>W5 联系方式证据（FTS_W5_EVIDENCE_DIR 设置时产出真实 WPF 渲染
    /// 截图）：浅/深主题设置窗页脚关于区（联系方式行＋复制按钮＋二维码缩略图）、
    /// 复制反馈态与二维码放大层。复制走写入缝，不触碰系统剪贴板。</summary>
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

            ((Button)settings.FindName("WeChatCopyButton")!).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            CompleteLayout(settings);
            SaveFooterCrop(settings, evidenceDirectory!, "footer-contact-copied-light.png");

            var overlay = (Grid)settings.FindName("WeChatQrOverlay")!;
            ((Button)settings.FindName("WeChatQrButton")!).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(settings.Dispatcher, () => overlay.Opacity == 1d && overlay.Visibility == Visibility.Visible);
            SaveWindowShell(settings, evidenceDirectory!, "qr-overlay-light.png");

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
            PumpUntil(settings.Dispatcher, () => overlay.Opacity == 1d && overlay.Visibility == Visibility.Visible);
            SaveWindowShell(settings, evidenceDirectory!, "qr-overlay-dark.png");
        }
        finally
        {
            CloseAll(window);
        }
    }

    /// <summary>W3 缩放过渡证据（FTS_W5_EVIDENCE_DIR 设置时产出真实 WPF 渲染
    /// 截图）：浅/深主题放大过程多帧胶片（各≥6 帧覆盖 0→400ms 新入场时长，含
    /// t=0 起始与终态）＋收回过程多帧胶片（开→动画满→关，各≥6 帧含 t=0 与
    /// 终态塌缩帧）＋入场中途反向收回胶片（各≥6 帧）＋动效关开/关即时定格。
    /// 复制走写入缝，不触碰系统剪贴板。</summary>
    [STATestMethod]
    public void Evidence_CaptureW3ZoomFrames()
    {
        var evidenceDirectory = Environment.GetEnvironmentVariable("FTS_W5_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(evidenceDirectory))
        {
            Assert.Inconclusive("未设置 FTS_W5_EVIDENCE_DIR，跳过 W3 缩放过渡证据产出。");
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

            CaptureQrZoomFilm(settings, overlay, evidenceDirectory!, "zoom-light");
            CaptureQrZoomExitFilm(settings, overlay, evidenceDirectory!, "zoom-exit-light");
            CaptureQrZoomReverseFilm(settings, overlay, evidenceDirectory!, "zoom-reverse-light");
            settings.ApplyTheme(DesignTheme.Dark);
            CompleteLayout(settings);
            CaptureQrZoomFilm(settings, overlay, evidenceDirectory!, "zoom-dark");
            CaptureQrZoomExitFilm(settings, overlay, evidenceDirectory!, "zoom-exit-dark");
            CaptureQrZoomReverseFilm(settings, overlay, evidenceDirectory!, "zoom-reverse-dark");
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
            SaveWindowShell(settings, evidenceDirectory!, "zoom-disabled-final-light.png");

            RaisePointUp(overlay, UIElement.MouseLeftButtonUpEvent);
            CompleteLayout(settings);
            Assert.AreEqual(Visibility.Collapsed, overlay.Visibility, "动效关＝关闭即时塌缩。");
            SaveWindowShell(settings, evidenceDirectory!, "zoom-disabled-closed-light.png");
        }
        finally
        {
            CloseAll(disabledWindow);
        }
    }

    /// <summary>放大过程多帧胶片：起始帧（先泵 1ms 让动画时钟起步——同步渲染
    /// 会取挂钟前的基值，实时应用的首个合成帧同样先走时钟 tick，起始帧须等价）
    /// ＋40ms 步进×12 帧覆盖 400ms 入场全程＋收尾终态帧。</summary>
    private static void CaptureQrZoomFilm(
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

        PumpUntil(settings.Dispatcher, () => overlay.Opacity == 1d && overlay.Visibility == Visibility.Visible);
        SaveWindowShell(settings, directory, $"{prefix}-final.png");
    }

    /// <summary>收回过程多帧胶片：先泵过入场全程到恒等定格，单击关层后起始帧
    ///（泵 1ms 同口径）＋40ms 步进×8 帧覆盖 250ms 收回全程＋终态塌缩帧。</summary>
    private static void CaptureQrZoomExitFilm(
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

    /// <summary>入场中途反向收回胶片：泵入入场约 120ms 后单击关层，起始帧
    ///（泵 1ms 同口径）＋40ms 步进×8 帧覆盖从当下态续收的飞行段＋终态塌缩帧。</summary>
    private static void CaptureQrZoomReverseFilm(
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
    //      2026-11-06：放大/收回共用插值路径的缩放过渡动画）----

    [STATestMethod]
    public void QrThumb_RightClickCopiesWeChatIdWithoutOpeningOverlay()
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

            Assert.IsNotNull(captured, "右键缩略图必须经写入缝复制微信号。");
            Assert.AreEqual(ProductIdentity.WeChatId, (string)captured.GetData(DataFormats.UnicodeText)!);
            Assert.AreEqual(
                "已复制",
                ((Button)settings.FindName("WeChatCopyButton")!).Content,
                "反馈就地在页脚复制按钮（最近复制按钮翻转，零 toast）。");
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
            var copy = (Button)settings.FindName("WeChatCopyButton")!;

            RaisePointUp(footerRow, UIElement.MouseLeftButtonUpEvent);
            Assert.IsNotNull(captured, "页脚微信号行左键必须经写入缝复制。");
            Assert.AreEqual("已复制", copy.Content, "左键复制反馈就地在相邻复制按钮。");

            RaisePointUp(footerRow, UIElement.MouseRightButtonUpEvent);
            Assert.AreEqual(2, captureCount, "右键与左键同一处理器服两事件，都走写入缝。");
            Assert.AreEqual(
                ProductIdentity.WeChatId,
                (string)captured!.GetData(DataFormats.UnicodeText)!,
                "两键负载同为 ProductIdentity.WeChatId 全文。");
            Assert.AreEqual("已复制", copy.Content, "右键复制反馈同位。");
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
            var largeCopy = (Button)settings.FindName("WeChatCopyLargeButton")!;

            RaisePointUp(largeRow, UIElement.MouseLeftButtonUpEvent);
            Assert.IsNotNull(captured, "放大态微信号行左键必须经写入缝复制。");
            Assert.AreEqual("已复制", largeCopy.Content, "放大态反馈就在放大层复制按钮。");
            Assert.AreEqual(Visibility.Visible, overlay.Visibility, "左键复制不得关层（e.Handled 阻断冒泡）。");

            RaisePointUp(largeRow, UIElement.MouseRightButtonUpEvent);
            Assert.AreEqual(2, captureCount, "右键与左键同一处理器服两事件。");
            Assert.AreEqual(ProductIdentity.WeChatId, (string)captured!.GetData(DataFormats.UnicodeText)!);
            Assert.AreEqual("已复制", largeCopy.Content, "右键复制反馈同位。");
            Assert.AreEqual(
                Visibility.Visible,
                overlay.Visibility,
                "右键复制同样不得关层（与复制按钮被 Button 消费同型）。");
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
            Assert.AreEqual(1d, scale.ScaleX, "动效关＝跳过缩放（ScaleX 恒等）。");
            Assert.AreEqual(1d, scale.ScaleY, "动效关＝跳过缩放（ScaleY 恒等）。");
            Assert.AreEqual(0d, translate.X, "动效关＝跳过缩放（TranslateX 恒等）。");
            Assert.AreEqual(0d, translate.Y, "动效关＝跳过缩放（TranslateY 恒等）。");
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void QrOverlay_EscapeDuringEntranceStartsAnimatedExit()
    {
        // 动效开：打开后不等缩放入场完成立即 Esc——动画不阻断命中，层立即进入
        // 关闭路径，且收回走动画（动态 From 续收）而非瞬回恒等（2026-11-06：
        // 收回从瞬时切换改为共用路径的缩放动画）。
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
            // 泵入入场约 1/4 处（400ms 入场）再 Esc：缩放已明显离开源尺寸，
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
                FloatingTransferStation.Views.FadeAnimation.GetIsActive(overlay),
                "Esc 后层立即进入关闭路径（IsActive 即刻翻 false）。");
            // 收回途中采样：ScaleX 仍在飞行区间且单调递减（非瞬回恒等）。
            var samples = SampleScaleDuringExit(settings, scale, pumpMs: 160);
            Assert.IsTrue(
                samples.Count >= 3,
                $"采样器必须在收回途中取到值（时钟在泵内推进；采样数={samples.Count}）。");
            Assert.IsTrue(
                samples.All(value => value > 0.2d && value < 0.99d),
                $"收回中途必须处于非恒等飞行态（未瞬回；采样={FormatSamples(samples)}）。");
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
            // 泵过入场全程（400ms 入场＋余量）到恒等定格。
            PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(600));
            Assert.AreEqual(1d, scale.ScaleX, "入场完成应处于恒等态（收回起点）。");

            RaisePointUp(overlay, UIElement.MouseLeftButtonUpEvent);
            var samples = SampleScaleDuringExit(settings, scale, pumpMs: 160);
            Assert.IsTrue(
                samples.Count >= 3,
                $"采样器必须在收回途中取到值（采样数={samples.Count}）。");
            Assert.IsTrue(
                samples.All(value => value > 0.19d && value < 0.99d),
                $"收回中途必须非恒等（动画在飞；采样={FormatSamples(samples)}）。");
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
        // 动效开：缩放入场期间以调度器采样器收中途值（PushFrame 长泵内条件
        // 只在首尾被求值，中途态必须由 Timer 采样捕获），泵过 400ms 入场全程
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

    private static void RaisePointUp(UIElement target, RoutedEvent routedEvent)
    {
        var button = routedEvent == UIElement.MouseRightButtonUpEvent ? MouseButton.Right : MouseButton.Left;
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button)
        {
            RoutedEvent = routedEvent,
            Source = target
        });
    }

    /// <summary>QR 卡缩放过渡变换（构造函数建组：先 Scale 后 Translate，顺序冻结）。</summary>
    private static (ScaleTransform Scale, TranslateTransform Translate) QrZoomTransforms(SettingsWindow settings)
    {
        var card = (Border)settings.FindName("WeChatQrLargeCard")!;
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
