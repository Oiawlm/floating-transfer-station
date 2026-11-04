using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 设置窗页脚「关于」区联系方式（W5 2026-11-05）：微信号文本与二维码位图
/// 全部来自 ProductIdentity 单一来源（页脚/放大层共用同一冻结位图实例）；
/// 「复制」经可注入剪贴板写入缝落盘（harness 密封缺省＝响亮失败 stub，
/// 契约 #53 口径——未显式声明写意图的测试一旦触达真实剪贴板即失败），
/// 反馈为短文案瞬时翻转；放大层（设置窗内遮罩，不新建窗口）从缩略图单击
/// 打开、单击遮罩任意处或 Esc 关闭（Esc 只关层不关窗），层内附一致复制入口。
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
