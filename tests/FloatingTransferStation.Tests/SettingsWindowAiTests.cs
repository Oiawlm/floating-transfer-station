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
/// 设置窗口「AI 设置」节的交互守护：Key 失焦保存密文（断言不含明文）、空输入
/// 清除、显示/隐藏切换不落盘明文、无模型选择 UI（模型恒为
/// DeepSeekClient.DefaultModel，契约 #28）、测试连接就地文案、解密失败降级；
/// 整理提示词编辑卡（1.52.0，契约 #29 修订）——null 态空框＋状态显式、失焦
/// Trim 保存/清空存 null、恢复默认终态 null（含失焦先行次序幂等）、填入默认
/// 全文待失焦保存、超长前置校验不保存＋就地红字。明文 Key 只用假值。
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

    private static Button TestButton(Window settings) =>
        (Button)settings.FindName("DeepSeekTestButton");

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
            Keyboard.Focus((RadioButton)settings.FindName("SettingsNavAi")!);
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
            Keyboard.Focus((RadioButton)settings.FindName("SettingsNavAi")!);
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
    public void AiSection_HasNoModelSelector()
    {
        // 模型收敛（契约 #28）：AI 节只配置 API Key，无模型下拉与提示行；
        // 模型恒为 DeepSeekClient.DefaultModel，线上请求 model 字段由此唯一派生。
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new RecordingPreferencesStore(), new FakeSecretProtector());
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);

            Assert.IsNull(settings.FindName("DeepSeekModelComboBox"), "模型下拉必须随选择机制整体删除。");
            Assert.IsNull(settings.FindName("DeepSeekModelHintText"), "模型提示行必须随选择机制整体删除。");
            var aiPage = (StackPanel)settings.FindName("AiPage")!;
            Assert.AreEqual(
                0,
                FindDescendants<ComboBox>(aiPage).Count(),
                "AI 页不得残留任何 ComboBox（含单选项变体，那是补丁不是收敛）。");
        }
        finally
        {
            CloseQuietly(window);
        }
    }

    // ---- AI 整理总开关（设置页交互）----

    [STATestMethod]
    public void AiOrganizeToggle_OffGraysAreasAndPersists_RestoreOnReopensReflect()
    {
        // 契约 #29 修订：关＝Key 卡（输入框/显示切换/测试连接/门户链接）与整理
        // 提示词卡整卡置灰＋持久化；重开设置窗回显关闭态；开＝两卡恢复可用。
        // 已存 Key 与自定义提示词只停用不删除（不触碰存储逻辑）。
        using var directory = new TestDirectory();
        var protector = new FakeSecretProtector();
        var preferencesStore = new RecordingPreferencesStore
        {
            Seeded = AppPreferences.Default with
            {
                DeepSeekApiKeyProtected = protector.Protect(PlainFakeKey),
                AiOrganizeSystemPromptOverride = "自定义提示词保持原样。"
            }
        };
        var window = CreateWindow(directory, preferencesStore, protector);
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            var toggle = (CheckBox)settings.FindName("AiOrganizeEnabledToggle")!;
            var keyCard = (Border)settings.FindName("AiKeyCard")!;
            var promptCard = (Border)settings.FindName("AiPromptCard")!;
            Assert.IsTrue(toggle.IsChecked == true, "前置：总开关默认开。");
            Assert.IsTrue(keyCard.IsEnabled && promptCard.IsEnabled, "前置：两卡默认可用。");

            toggle.IsChecked = false;
            PumpUntil(settings.Dispatcher, () => preferencesStore.LastSaved is not null);
            Assert.IsFalse(keyCard.IsEnabled, "关＝Key 卡整卡置灰。");
            Assert.IsFalse(KeyBox(settings).IsEnabled, "关＝Key 输入框置灰。");
            Assert.IsFalse(TestButton(settings).IsEnabled, "关＝测试连接置灰。");
            Assert.IsFalse(promptCard.IsEnabled, "关＝整理提示词卡整卡置灰。");
            Assert.IsFalse(PromptBox(settings).IsEnabled, "关＝提示词编辑框置灰。");
            Assert.IsFalse(preferencesStore.LastSaved!.AiOrganizeEnabled, "关＝必须持久化。");
            Assert.IsNotNull(
                preferencesStore.LastSaved.DeepSeekApiKeyProtected,
                "关＝已存 Key 原样保留（只停用不删除）。");
            Assert.AreEqual(
                "自定义提示词保持原样。",
                preferencesStore.LastSaved.AiOrganizeSystemPromptOverride,
                "关＝自定义提示词原样保留。");

            settings.Close();
            var reopened = OpenSettings(window);
            CompleteLayout(reopened);
            var reopenedToggle = (CheckBox)reopened.FindName("AiOrganizeEnabledToggle")!;
            Assert.IsTrue(reopenedToggle.IsChecked == false, "重开设置窗必须回显关闭态。");
            Assert.IsFalse(
                ((Border)reopened.FindName("AiKeyCard")!).IsEnabled,
                "重开必须回显 Key 卡置灰。");
            Assert.IsFalse(
                ((Border)reopened.FindName("AiPromptCard")!).IsEnabled,
                "重开必须回显提示词卡置灰。");

            reopenedToggle.IsChecked = true;
            PumpUntil(
                reopened.Dispatcher,
                () => preferencesStore.LastSaved is not null &&
                    preferencesStore.LastSaved.AiOrganizeEnabled);
            Assert.IsTrue(
                ((Border)reopened.FindName("AiKeyCard")!).IsEnabled,
                "开＝Key 卡恢复可用。");
            Assert.IsTrue(
                ((Border)reopened.FindName("AiPromptCard")!).IsEnabled,
                "开＝提示词卡恢复可用。");
        }
        finally
        {
            CloseQuietly(window);
        }
    }

    /// <summary>AI 整理总开关关闭态证据（FTS_AI_EVIDENCE_DIR 设置时产出真实 WPF
    /// 渲染截图）：AI 设置页总开关关＝Key 卡与整理提示词卡整卡置灰。</summary>
    [STATestMethod]
    public void Evidence_CaptureAiOrganizeToggleOffScreens()
    {
        var evidenceDirectory = Environment.GetEnvironmentVariable("FTS_AI_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(evidenceDirectory))
        {
            Assert.Inconclusive("未设置 FTS_AI_EVIDENCE_DIR，跳过发布证据产出。");
        }

        var protector = new FakeSecretProtector();
        var preferencesStore = new RecordingPreferencesStore
        {
            Seeded = AppPreferences.Default with
            {
                DeepSeekApiKeyProtected = protector.Protect(PlainFakeKey),
                AiOrganizeEnabled = false
            }
        };
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, preferencesStore, protector);
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            RevealAiSection(settings);
            SaveWindowShell(settings, evidenceDirectory!, "settings-ai-toggle-off.png");
        }
        finally
        {
            CloseQuietly(window);
        }
    }

    // ---- 整理提示词编辑卡（1.52.0，契约 #29 修订）----

    private static TextBox PromptBox(Window settings) =>
        (TextBox)settings.FindName("AiOrganizePromptBox");

    private static TextBlock PromptStateText(Window settings) =>
        (TextBlock)settings.FindName("AiOrganizePromptStateText");

    private static TextBlock PromptErrorText(Window settings) =>
        (TextBlock)settings.FindName("AiOrganizePromptErrorText");

    private static Button PromptFillDefaultButton(Window settings) =>
        (Button)settings.FindName("AiOrganizePromptFillDefaultButton");

    private static Button PromptResetButton(Window settings) =>
        (Button)settings.FindName("AiOrganizePromptResetButton");

    /// <summary>让编辑框真实持有焦点后移走（真实失焦次序），并等待保存完成。</summary>
    private static void CommitPromptByFocusChange(
        Window settings,
        RecordingPreferencesStore preferencesStore,
        Func<bool> saved)
    {
        Keyboard.Focus((RadioButton)settings.FindName("SettingsNavAi")!);
        PumpUntil(settings.Dispatcher, saved);
        Assert.IsNotNull(preferencesStore.LastSaved);
    }

    [STATestMethod]
    public void StoredOverride_FillsBoxAndShowsCustomizedState()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore
        {
            Seeded = AppPreferences.Default with
            {
                AiOrganizeSystemPromptOverride = "我的自定义整理规则。"
            }
        };
        var window = CreateWindow(directory, preferencesStore, new FakeSecretProtector());
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);

            Assert.AreEqual("我的自定义整理规则。", PromptBox(settings).Text, "存量覆盖必须回填编辑框。");
            Assert.AreEqual("已自定义。", PromptStateText(settings).Text, "状态必须显式「已自定义」。");
            Assert.IsNull(preferencesStore.LastSaved, "打开设置不得触发保存。");
        }
        finally
        {
            CloseQuietly(window);
        }
    }

    [STATestMethod]
    public void NullOverride_ShowsEmptyBoxWithDefaultStateHint()
    {
        // S3：null 态不预填默认全文（保住「是否默认」判定基线），状态显式可见。
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new RecordingPreferencesStore(), new FakeSecretProtector());
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);

            var box = PromptBox(settings);
            Assert.AreEqual(string.Empty, box.Text, "默认（null）必须显示空框，绝不预填默认全文。");
            Assert.AreEqual("当前使用内置默认。", PromptStateText(settings).Text);
            Assert.IsTrue(box.AcceptsReturn, "编辑框必须接受多行输入。");
            Assert.AreEqual(TextWrapping.Wrap, box.TextWrapping);
        }
        finally
        {
            CloseQuietly(window);
        }
    }

    [STATestMethod]
    public void PromptEdit_LostFocus_PersistsTrimmedOverrideAndSwitchesState()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(directory, preferencesStore, new FakeSecretProtector());
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            var box = PromptBox(settings);
            Assert.AreEqual("当前使用内置默认。", PromptStateText(settings).Text, "前置条件：初始为默认态。");

            box.Focus();
            box.Text = "  你是我的专属整理器，只输出整理后的文字。  ";
            Assert.AreEqual("已自定义。", PromptStateText(settings).Text, "输入内容后状态标签必须切换。");
            CommitPromptByFocusChange(
                settings,
                preferencesStore,
                () => preferencesStore.LastSaved?.AiOrganizeSystemPromptOverride is not null);

            Assert.AreEqual(
                "你是我的专属整理器，只输出整理后的文字。",
                preferencesStore.LastSaved!.AiOrganizeSystemPromptOverride,
                "失焦必须保存 Trim 后文本。");
            Assert.AreEqual(
                preferencesStore.LastSaved.AiOrganizeSystemPromptOverride,
                window.CurrentPreferences.AiOrganizeSystemPromptOverride,
                "内存偏好与持久化值一致（下一次整理即生效，契约 #5）。");
        }
        finally
        {
            CloseQuietly(window);
        }
    }

    [STATestMethod]
    public void ClearedPrompt_LostFocus_SavesNull()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore
        {
            Seeded = AppPreferences.Default with { AiOrganizeSystemPromptOverride = "旧自定义提示词" }
        };
        var window = CreateWindow(directory, preferencesStore, new FakeSecretProtector());
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            var box = PromptBox(settings);
            box.Focus();
            box.Text = "   ";
            CommitPromptByFocusChange(
                settings,
                preferencesStore,
                () => preferencesStore.LastSaved is not null);

            Assert.IsNull(
                preferencesStore.LastSaved!.AiOrganizeSystemPromptOverride,
                "清空/纯空白失焦必须存 null（回退内置默认，绝不发送空白 system）。");
            Assert.AreEqual("当前使用内置默认。", PromptStateText(settings).Text);
        }
        finally
        {
            CloseQuietly(window);
        }
    }

    [STATestMethod]
    public void ResetButton_ClickAfterFocusLoss_FinalStateIsNull()
    {
        // ThemedActionButtonStyle 恒 Focusable=False：点击按钮不使编辑框失焦，
        // 恢复默认由按钮处理函数清空并直接提交 null，最终态恒为 null、无需再
        // 失焦一次；失焦先行提交旧值的次序（Tab 离开后点击）也被随后的 null
        // 提交覆盖——两种次序收敛同一终态。
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore
        {
            Seeded = AppPreferences.Default with { AiOrganizeSystemPromptOverride = "旧自定义提示词" }
        };
        var window = CreateWindow(directory, preferencesStore, new FakeSecretProtector());
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            var box = PromptBox(settings);
            var resetButton = PromptResetButton(settings);
            box.Focus();
            box.Text = "编辑到一半的提示词";
            resetButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(
                settings.Dispatcher,
                () => preferencesStore.LastSaved?.AiOrganizeSystemPromptOverride is null);

            Assert.AreEqual(string.Empty, box.Text, "恢复默认必须清空编辑框。");
            Assert.IsNull(window.CurrentPreferences.AiOrganizeSystemPromptOverride);
            Assert.AreEqual("当前使用内置默认。", PromptStateText(settings).Text);
            Assert.AreEqual(Visibility.Collapsed, PromptErrorText(settings).Visibility);

            // 失焦先行的次序：失焦提交新值，按钮点击随即覆盖为 null。
            box.Focus();
            box.Text = "再次自定义的提示词";
            Keyboard.Focus((RadioButton)settings.FindName("SettingsNavAi")!);
            PumpUntil(
                settings.Dispatcher,
                () => preferencesStore.LastSaved?.AiOrganizeSystemPromptOverride == "再次自定义的提示词");
            resetButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(
                settings.Dispatcher,
                () => preferencesStore.LastSaved?.AiOrganizeSystemPromptOverride is null);
            Assert.IsNull(window.CurrentPreferences.AiOrganizeSystemPromptOverride);

            // 幂等：重复点击不再产生新保存（提交函数值未变即早退）。
            var savedCount = preferencesStore.SaveCount;
            resetButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            CompleteLayout(settings);
            Assert.AreEqual(savedCount, preferencesStore.SaveCount, "重复恢复默认不得产生无谓写盘。");
        }
        finally
        {
            CloseQuietly(window);
        }
    }

    [STATestMethod]
    public void FillDefaultButton_PutsSystemPromptInBoxAwaitingFocusSave()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore
        {
            Seeded = AppPreferences.Default with { AiOrganizeSystemPromptOverride = "旧自定义提示词" }
        };
        var window = CreateWindow(directory, preferencesStore, new FakeSecretProtector());
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            var box = PromptBox(settings);
            var fillButton = PromptFillDefaultButton(settings);
            box.Focus();
            box.Text = "编辑到一半的提示词";
            fillButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.AreEqual(
                AiOrganizePrompt.SystemPrompt,
                box.Text,
                "填入默认全文必须把内置默认逐字填入编辑框（微调起点）。");
            Assert.AreEqual("已自定义。", PromptStateText(settings).Text);
            Assert.IsNull(
                preferencesStore.LastSaved,
                "填入≠生效：按钮不保存，保存点仍是编辑框下一次失焦。");

            // 用户离开编辑框（按钮不可夺焦，此处以导航项承接焦点）后提交全文。
            Keyboard.Focus((RadioButton)settings.FindName("SettingsNavAi")!);
            PumpUntil(
                settings.Dispatcher,
                () => preferencesStore.LastSaved?.AiOrganizeSystemPromptOverride == AiOrganizePrompt.SystemPrompt);
        }
        finally
        {
            CloseQuietly(window);
        }
    }

    [STATestMethod]
    public void OverlongPrompt_LostFocus_ShowsErrorAndKeepsPreference()
    {
        // 设置页前置校验（S5 第一防线）：超长不保存＋就地红字提示。
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore
        {
            Seeded = AppPreferences.Default with { AiOrganizeSystemPromptOverride = "旧自定义提示词" }
        };
        var window = CreateWindow(directory, preferencesStore, new FakeSecretProtector());
        try
        {
            var settings = OpenSettings(window);
            CompleteLayout(settings);
            var box = PromptBox(settings);
            box.Focus();
            box.Text = new string('提', AiOrganizePrompt.MaxOverrideLength + 1);
            Keyboard.Focus((RadioButton)settings.FindName("SettingsNavAi")!);
            CompleteLayout(settings);

            Assert.AreEqual(Visibility.Visible, PromptErrorText(settings).Visibility);
            StringAssert.Contains(PromptErrorText(settings).Text, "超过 1 万字");
            StringAssert.Contains(PromptErrorText(settings).Text, "未保存");
            Assert.AreEqual(
                "旧自定义提示词",
                window.CurrentPreferences.AiOrganizeSystemPromptOverride,
                "超长输入不得改动偏好。");

            // 缩短到限内再失焦：错误清除、新值保存（就地反馈可恢复）。
            box.Focus();
            box.Text = "缩短后的提示词";
            Keyboard.Focus((RadioButton)settings.FindName("SettingsNavAi")!);
            PumpUntil(
                settings.Dispatcher,
                () => preferencesStore.LastSaved?.AiOrganizeSystemPromptOverride == "缩短后的提示词");
            Assert.AreEqual(Visibility.Collapsed, PromptErrorText(settings).Visibility);
        }
        finally
        {
            CloseQuietly(window);
        }
    }

    [STATestMethod]
    public void TestConnection_ProbesModelsWithoutSelectionState()
    {
        // 探活改 GET /models（契约 #28 修订）：无请求体、无模型字段，与设置页
        // 当前状态无关（无选择状态可依）；成功文案模型名恒为 DefaultModel。
        using var directory = new TestDirectory();
        var protector = new FakeSecretProtector();
        var preferencesStore = new RecordingPreferencesStore();
        var handler = new StubHttpMessageHandler();
        handler.Respond = (_, _) => HandlerResponse(200, ModelsBody());
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

            TestButton(settings).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(settings.Dispatcher, () => StatusText(settings).Visibility == Visibility.Visible);

            StringAssert.Contains(StatusText(settings).Text, "连接成功");
            StringAssert.Contains(StatusText(settings).Text, DeepSeekClient.DefaultModel);
            var request = handler.LastRequest!;
            Assert.IsNotNull(request, "必须真实发起（假 handler 捕获的）请求。");
            Assert.AreEqual(HttpMethod.Get, request.Method, "探活必须是 GET /models。");
            Assert.AreEqual("https://api.deepseek.com/models", request.RequestUri!.ToString());
            Assert.IsNull(request.Content, "探活不得携带请求体（不得构造 chat completion）。");
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
        handler.Respond = (_, _) => HandlerResponse(200, ModelsBody());
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
    public void TestConnection_WithUnmappedNetworkException_ShowsMappedChineseFailure()
    {
        // 复现报障现象类别（层②）：映射外异常（原始 SocketException）曾穿透
        // 客户端到本 UI 的 catch(Exception) 兜底——朋友看到的正是兜底文案
        // 「测试失败，请重试。」且诊断信息全丢（现场异常类型未留存，此处注入
        // 的类型是缺口证明）；客户端边界完备化后显示精确映射文案，兜底退化为
        // 纯崩溃护栏。
        using var directory = new TestDirectory();
        var protector = new FakeSecretProtector();
        var preferencesStore = new RecordingPreferencesStore();
        var handler = new StubHttpMessageHandler();
        handler.Respond = (_, _) => throw new System.Net.Sockets.SocketException();
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

            Assert.AreEqual(
                "无法连接 DeepSeek 服务，请检查网络。",
                StatusText(settings).Text,
                "映射外异常必须由客户端边界映射为带类别的文案，不得落到 UI 兜底。");
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

            handler.Respond = (_, _) => HandlerResponse(200, ModelsBody());
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

    /// <summary>1.52.0 发布证据（FTS_RELEASE_152_EVIDENCE_DIR 设置时产出真实 WPF
    /// 渲染截图）：浅/深主题 AI 设置页（整理提示词新卡滚入视野、Key 掩码态）——
    /// 浅色拍默认态（空框＋内置默认状态行），深色拍自定义态（覆盖文本＋已自定义）。
    /// Key 只用假值且截图呈现为掩码圆点。</summary>
    [STATestMethod]
    public void Evidence_CaptureRelease152AiPromptScreens()
    {
        var evidenceDirectory = Environment.GetEnvironmentVariable("FTS_RELEASE_152_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(evidenceDirectory))
        {
            Assert.Inconclusive("未设置 FTS_RELEASE_152_EVIDENCE_DIR，跳过发布证据产出。");
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
            RevealAiPromptCard(settings);
            SaveWindowShell(settings, evidenceDirectory!, "settings-ai-prompt-light.png");

            DesignThemeManager.Apply(settings, DesignTheme.Dark);
            PromptBox(settings).Focus();
            PromptBox(settings).Text = "你是我的专属文本整理器：忠实原意，理顺口语跳跃，清理口误与重复，输出可直接使用的文字。";
            CompleteLayout(settings);
            RevealAiPromptCard(settings);
            SaveWindowShell(settings, evidenceDirectory!, "settings-ai-prompt-dark.png");
        }
        finally
        {
            CloseQuietly(window);
        }
    }

    /// <summary>把整理提示词卡滚入视野（状态行贴视口内可见），并完成布局。</summary>
    private static void RevealAiPromptCard(Window settings)
    {
        PromptResetButton(settings).BringIntoView();
        CompleteLayout(settings);
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

    private static string ModelsBody() => """
        {"object":"list","data":[{"id":"deepseek-flash","object":"model","owned_by":"deepseek"}]}
        """;

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

    private static IEnumerable<T> FindDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindDescendants<T>(child))
            {
                yield return nested;
            }
        }
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
        public int SaveCount { get; private set; }
        public AppPreferences? LastSaved { get; private set; }
        public AppPreferences Seeded { get; set; } = AppPreferences.Default;

        public Task<AppPreferences> LoadPreferencesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Seeded);

        public Task SavePreferencesAsync(
            AppPreferences preferences,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
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
