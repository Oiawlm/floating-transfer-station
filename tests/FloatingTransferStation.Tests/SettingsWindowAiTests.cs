using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 设置窗口「AI 设置」节（1.27.0 DeepSeek 接入地基）的交互守护：
/// Key 失焦保存密文（断言不含明文）、空输入清除、显示/隐藏切换不落盘明文、
/// 模型切换即时生效、测试连接就地文案、解密失败降级。明文 Key 只用假值。
/// </summary>
[TestClass]
public sealed class SettingsWindowAiTests
{
    private const string PlainFakeKey = "sk-test-ui-fake-key";

    private static MainWindow CreateWindow(
        TestDirectory directory,
        RecordingPreferencesStore preferencesStore,
        ISecretProtector secretProtector,
        DeepSeekClient? deepSeekClient = null,
        PluginCatalog? pluginCatalog = null)
    {
        var board = new BoardService();
        var store = new RecordingSettingsBoardStore(directory.Root);
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
            preferences: preferencesStore.Seeded,
            preferencesStore: preferencesStore,
            startupManager: new FakeStartupManager(),
            dataDirectory: directory.Root,
            pluginCatalog: pluginCatalog,
            secretProtector: secretProtector,
            deepSeekClient: deepSeekClient ?? new DeepSeekClient(new StubHttpMessageHandler()));
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
        // 双栏布局（契约 #24）：AI 节独立成页，可见性断言与证据截图先导航到「AI」页。
        var aiNav = settings.FindName("SettingsNavAi") as RadioButton;
        Assert.IsNotNull(aiNav, "设置窗必须有「AI」导航项。");
        aiNav.IsChecked = true;
        return settings;
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
            throw new TimeoutException("等待 AI 设置交互结果超时（5 秒）。");
        }
    }

    private static PasswordBox KeyBox(Window settings) =>
        (PasswordBox)settings.FindName("DeepSeekKeyBox");

    private static TextBox PlainBox(Window settings) =>
        (TextBox)settings.FindName("DeepSeekKeyPlainBox");

    private static ComboBox ModelComboBox(Window settings) =>
        (ComboBox)settings.FindName("DeepSeekModelComboBox");

    private static TextBlock StatusText(Window settings) =>
        (TextBlock)settings.FindName("DeepSeekStatusText");

    [STATestMethod]
    public void KeyInput_LostFocus_PersistsCipherWithoutPlaintext()
    {
        using var directory = new TestDirectory();
        var protector = new FakeSecretProtector();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore, protector);
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);

            var keyBox = KeyBox(settings);
            keyBox.Focus();
            keyBox.Password = PlainFakeKey;
            Keyboard.Focus(ModelComboBox(settings));
            PumpUntil(settings.Dispatcher, () => preferencesStore.LastSaved is not null);

            var saved = preferencesStore.LastSaved!;
            Assert.IsNotNull(saved.DeepSeekApiKeyProtected, "失焦后必须保存密文。");
            Assert.AreNotEqual(PlainFakeKey, saved.DeepSeekApiKeyProtected);
            Assert.IsFalse(
                saved.DeepSeekApiKeyProtected.Contains(PlainFakeKey, StringComparison.Ordinal),
                "存储值必须不含明文。");
            Assert.AreEqual(
                PlainFakeKey,
                protector.Unprotect(saved.DeepSeekApiKeyProtected!),
                "保存的密文必须能由保护器还原为明文。");
            Assert.AreEqual(
                saved.DeepSeekApiKeyProtected,
                window.CurrentPreferences.DeepSeekApiKeyProtected,
                "内存偏好与持久化值一致。");
        }
        finally
        {
            CloseQuietly(window);
        }
    }

    [STATestMethod]
    public void EmptyKeyInput_LostFocus_ClearsStoredKeyToNull()
    {
        using var directory = new TestDirectory();
        var protector = new FakeSecretProtector();
        var preferencesStore = new RecordingPreferencesStore
        {
            Seeded = AppPreferences.Default with
            {
                DeepSeekApiKeyProtected = protector.Protect(PlainFakeKey)
            }
        };
        var window = CreateWindow(directory, preferencesStore, protector);
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            Assert.AreEqual(PlainFakeKey, KeyBox(settings).Password, "存量密文应解密回填。");

            var keyBox = KeyBox(settings);
            keyBox.Focus();
            keyBox.Password = string.Empty;
            Keyboard.Focus(ModelComboBox(settings));
            PumpUntil(settings.Dispatcher, () => preferencesStore.LastSaved is not null);

            Assert.IsNull(
                preferencesStore.LastSaved!.DeepSeekApiKeyProtected,
                "空输入失焦必须清除已存密钥（存 null）。");
            Assert.IsNull(window.CurrentPreferences.DeepSeekApiKeyProtected);
        }
        finally
        {
            CloseQuietly(window);
        }
    }

    [STATestMethod]
    public void VisibilityToggle_SyncsContentBetweenBoxesAndNeverPersistsPlaintext()
    {
        using var directory = new TestDirectory();
        var protector = new FakeSecretProtector();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore, protector);
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            var keyBox = KeyBox(settings);
            var plainBox = PlainBox(settings);
            var toggle = (Button)settings.FindName("DeepSeekKeyVisibilityButton");

            keyBox.Password = PlainFakeKey;
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            CompleteLayout(settings);

            Assert.AreEqual(Visibility.Collapsed, keyBox.Visibility);
            Assert.AreEqual(Visibility.Visible, plainBox.Visibility);
            Assert.AreEqual("隐藏", toggle.Content);
            Assert.AreEqual(PlainFakeKey, plainBox.Text, "切换到明文框必须保留输入内容。");
            Assert.IsNull(preferencesStore.LastSaved, "显示/隐藏切换本身不得触发持久化（更不得落明文）。");

            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            CompleteLayout(settings);

            Assert.AreEqual(Visibility.Visible, keyBox.Visibility);
            Assert.AreEqual(Visibility.Collapsed, plainBox.Visibility);
            Assert.AreEqual("显示", toggle.Content);
            Assert.AreEqual(PlainFakeKey, keyBox.Password, "切回掩码框必须保留输入内容。");
            Assert.IsNull(preferencesStore.LastSaved);
        }
        finally
        {
            CloseQuietly(window);
        }
    }

    [STATestMethod]
    public void StoredKey_FillsMaskedBoxAndKeepsPlainTextBoxHidden()
    {
        using var directory = new TestDirectory();
        var protector = new FakeSecretProtector();
        var preferencesStore = new RecordingPreferencesStore
        {
            Seeded = AppPreferences.Default with
            {
                DeepSeekApiKeyProtected = protector.Protect(PlainFakeKey)
            }
        };
        var window = CreateWindow(directory, preferencesStore, protector);
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);

            Assert.IsInstanceOfType(KeyBox(settings), typeof(PasswordBox), "Key 输入必须是掩码 PasswordBox。");
            Assert.AreEqual(PlainFakeKey, KeyBox(settings).Password);
            Assert.AreEqual(Visibility.Collapsed, PlainBox(settings).Visibility);
            Assert.IsNull(preferencesStore.LastSaved, "打开设置不得触发保存。");
        }
        finally
        {
            CloseQuietly(window);
        }
    }

    [STATestMethod]
    public void UndecryptableStoredKey_ShowsEmptyBoxWithRedecomposeHint()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore
        {
            Seeded = AppPreferences.Default with { DeepSeekApiKeyProtected = "broken-cipher" }
        };
        var window = CreateWindow(directory, preferencesStore, new FakeSecretProtector());
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);

            Assert.AreEqual(string.Empty, KeyBox(settings).Password, "解密失败必须按未配置处理（Key 框留空）。");
            PumpUntil(
                settings.Dispatcher,
                () => StatusText(settings).Visibility == Visibility.Visible);
            StringAssert.Contains(StatusText(settings).Text, "API Key 无法解密");
            StringAssert.Contains(StatusText(settings).Text, "重新填写");
            Assert.IsNull(preferencesStore.LastSaved, "解密失败不得写入任何偏好（不丢其余偏好）。");
            Assert.IsTrue(window.CurrentPreferences.AutoCleanupEnabled, "其余偏好不受影响。");
        }
        finally
        {
            CloseQuietly(window);
        }
    }

    [STATestMethod]
    public void ModelSwitch_AppliesImmediatelyAndPersists()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore, new FakeSecretProtector());
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            var comboBox = ModelComboBox(settings);
            Assert.AreEqual("deepseek-flash", window.CurrentPreferences.DeepSeekModel);

            comboBox.SelectedIndex = 1;
            PumpUntil(settings.Dispatcher, () => preferencesStore.LastSaved is not null);

            Assert.AreEqual("deepseek-v4-pro", window.CurrentPreferences.DeepSeekModel);
            Assert.AreEqual(
                "deepseek-v4-pro",
                preferencesStore.LastSaved!.DeepSeekModel,
                "模型切换必须即时持久化（契约 #5）。");
        }
        finally
        {
            CloseQuietly(window);
        }
    }

    [STATestMethod]
    public void ModelHint_ShowsOfficialDisplayNameMapping()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new RecordingPreferencesStore(), new FakeSecretProtector());
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            RevealAiSection(settings);
            var hint = (TextBlock)settings.FindName("DeepSeekModelHintText");

            StringAssert.Contains(hint.Text, "deepseek-flash（DeepSeek V4.1 Flash）");
            StringAssert.Contains(hint.Text, "deepseek-v4-pro（DeepSeek V4 Pro）");
        }
        finally
        {
            CloseQuietly(window);
        }
    }

    [STATestMethod]
    public void TestConnection_WithUnsavedNewKeyInput_TestsCurrentBoxContentAndShowsSuccess()
    {
        using var directory = new TestDirectory();
        var protector = new FakeSecretProtector();
        var preferencesStore = new RecordingPreferencesStore();
        var handler = new StubHttpMessageHandler();
        handler.Respond = (_, _) => HandlerResponse(200, SuccessBody("deepseek-flash"));
        var window = CreateWindow(
            directory,
            preferencesStore,
            protector,
            new DeepSeekClient(handler));
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            // 新输入尚未失焦保存：测试连接必须直接测当前输入框内容。
            KeyBox(settings).Password = PlainFakeKey;

            ((Button)settings.FindName("DeepSeekTestButton")).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(settings.Dispatcher, () => StatusText(settings).Visibility == Visibility.Visible);

            StringAssert.Contains(StatusText(settings).Text, "连接成功");
            StringAssert.Contains(StatusText(settings).Text, "deepseek-flash");
            StringAssert.Contains(StatusText(settings).Text, "耗时");
            Assert.IsNotNull(handler.LastRequest, "必须真实发起（假 handler 捕获的）请求。");
            Assert.AreEqual("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
            Assert.AreEqual(PlainFakeKey, handler.LastRequest.Headers.Authorization.Parameter);
        }
        finally
        {
            CloseQuietly(window);
        }
    }

    [STATestMethod]
    public void TestConnection_WithInvalidKey_ShowsMappedChineseFailure()
    {
        using var directory = new TestDirectory();
        var protector = new FakeSecretProtector();
        var preferencesStore = new RecordingPreferencesStore();
        var handler = new StubHttpMessageHandler();
        handler.Respond = (_, _) => HandlerResponse(401, """{"error":{"message":"auth failed"}}""");
        var window = CreateWindow(
            directory,
            preferencesStore,
            protector,
            new DeepSeekClient(handler));
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            KeyBox(settings).Password = PlainFakeKey;

            ((Button)settings.FindName("DeepSeekTestButton")).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(settings.Dispatcher, () => StatusText(settings).Visibility == Visibility.Visible);

            Assert.AreEqual("API Key 无效。", StatusText(settings).Text);
        }
        finally
        {
            CloseQuietly(window);
        }
    }

    [STATestMethod]
    public void TestConnection_WithEmptyKey_ShowsLocalValidationWithoutRequest()
    {
        using var directory = new TestDirectory();
        var protector = new FakeSecretProtector();
        var preferencesStore = new RecordingPreferencesStore();
        var handler = new StubHttpMessageHandler();
        handler.Respond = (_, _) => throw new AssertFailedException("空 key 不得发起请求。");
        var window = CreateWindow(
            directory,
            preferencesStore,
            protector,
            new DeepSeekClient(handler));
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);

            ((Button)settings.FindName("DeepSeekTestButton")).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(settings.Dispatcher, () => StatusText(settings).Visibility == Visibility.Visible);

            Assert.AreEqual("请先填写 API Key。", StatusText(settings).Text);
            Assert.IsNull(handler.LastRequest);
            var testButton = (Button)settings.FindName("DeepSeekTestButton");
            Assert.IsTrue(testButton.IsEnabled, "测试结束后按钮必须恢复可用。");
            Assert.AreEqual("测试连接", testButton.Content);
        }
        finally
        {
            CloseQuietly(window);
        }
    }

    /// <summary>1.27.0 发布证据（FTS_RELEASE_127_EVIDENCE_DIR 设置时产出真实 WPF
    /// 渲染截图）：浅/深主题设置窗口（AI 设置节滚入视野、Key 掩码态）与测试连接
    /// 成功/失败就地文案。Key 只用假值且截图呈现为掩码圆点。</summary>
    [STATestMethod]
    public void Evidence_CaptureRelease127AiScreens()
    {
        var evidenceDirectory = Environment.GetEnvironmentVariable("FTS_RELEASE_127_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(evidenceDirectory))
        {
            Assert.Inconclusive("未设置 FTS_RELEASE_127_EVIDENCE_DIR，跳过发布证据产出。");
        }

        var handler = new StubHttpMessageHandler();
        var protector = new FakeSecretProtector();
        var preferencesStore = new RecordingPreferencesStore
        {
            Seeded = AppPreferences.Default with
            {
                DeepSeekApiKeyProtected = protector.Protect(PlainFakeKey)
            }
        };
        using var directory = new TestDirectory();
        var window = CreateWindow(
            directory,
            preferencesStore,
            protector,
            new DeepSeekClient(handler),
            CreateEmptyPluginCatalog(directory.Root));
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            RevealAiSection(settings);
            SaveWindowShell(settings, evidenceDirectory!, "settings-window.png");

            DesignThemeManager.Apply(settings, DesignTheme.Dark);
            RevealAiSection(settings);
            SaveWindowShell(settings, evidenceDirectory!, "settings-ai-dark.png");
            DesignThemeManager.Apply(settings, DesignTheme.Light);
            RevealAiSection(settings);

            handler.Respond = (_, _) => HandlerResponse(200, SuccessBody("deepseek-flash"));
            ((Button)settings.FindName("DeepSeekTestButton")).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(settings.Dispatcher, () => StatusText(settings).Visibility == Visibility.Visible);
            StatusText(settings).BringIntoView();
            CompleteLayout(settings);
            SaveWindowShell(settings, evidenceDirectory!, "settings-ai-test-success.png");

            StatusText(settings).Visibility = Visibility.Collapsed;
            handler.Respond = (_, _) => HandlerResponse(401, """{"error":{"message":"auth failed"}}""");
            ((Button)settings.FindName("DeepSeekTestButton")).RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(
                settings.Dispatcher,
                () => StatusText(settings).Visibility == Visibility.Visible &&
                    StatusText(settings).Text == "API Key 无效。");
            StatusText(settings).BringIntoView();
            CompleteLayout(settings);
            SaveWindowShell(settings, evidenceDirectory!, "settings-ai-test-failure.png");
        }
        finally
        {
            CloseQuietly(window);
        }
    }

    /// <summary>设置窗限高滚动：把 AI 设置节滚入视野（测试按钮贴视口下缘可见）。</summary>
    private static void RevealAiSection(Window settings)
    {
        ((Button)settings.FindName("DeepSeekTestButton")).BringIntoView();
        CompleteLayout(settings);
    }

    private static PluginCatalog CreateEmptyPluginCatalog(string root)
    {
        var catalog = new PluginCatalog(
            AppPaths.FromDataDirectory(root),
            builtInPluginsDirectory: null,
            new AtomicTextWriter());
        catalog.LoadAsync().GetAwaiter().GetResult();
        return catalog;
    }

    private static void SaveWindowShell(Window settings, string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        var shell = (Border)settings.FindName("WindowShell")!;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(shell.ActualWidth),
            (int)Math.Ceiling(shell.ActualHeight),
            96,
            96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(shell);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, fileName));
        encoder.Save(stream);
    }

    private static HttpResponseMessage HandlerResponse(int statusCode, string body) =>
        new((System.Net.HttpStatusCode)statusCode)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };

    private static string SuccessBody(string model) => """
        {"id":"1","model":"DEEPSEEK_MODEL","choices":[{"index":0,"message":{"role":"assistant","content":"pong"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
        """.Replace("DEEPSEEK_MODEL", model);

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

    private static void CloseQuietly(MainWindow window)
    {
        var field = typeof(MainWindow).GetField(
            "_settingsWindow",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field!.GetValue(window) is Window settings)
        {
            settings.Close();
        }

        window.Close();
    }

    /// <summary>可逆假保护器：密文 = "enc:" + base64(明文)，非该形状一律判为解密失败。</summary>
    private sealed class FakeSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) =>
            "enc:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));

        public string? Unprotect(string ciphertext)
        {
            if (!ciphertext.StartsWith("enc:", StringComparison.Ordinal))
            {
                return null;
            }

            try
            {
                return System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(ciphertext["enc:".Length..]));
            }
            catch (FormatException)
            {
                return null;
            }
        }
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

    private sealed class IdleClipboardReader : IClipboardReader
    {
        public Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClipboardSnapshot(0, null, [], null));
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

    /// <summary>捕获请求并按 Respond 应答的假 handler；默认拒绝任何请求。</summary>
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>? Respond { get; set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var respond = Respond
                ?? throw new AssertFailedException("该用例不应发起请求。");
            return respond(request, cancellationToken);
        }
    }
}
