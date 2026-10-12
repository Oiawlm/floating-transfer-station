using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 检查更新与一键更新的窗口层契约（契约 #26）：头部按钮行最左常驻更新按钮；
/// 双态（无更新=次级色「检查更新」，有更新=强调色 + tooltip/无障碍名携带新版本号）；
/// 自动检查的失败完全静默、发现新版本仅状态条提示一次；无更新手动点击状态条反馈、
/// 失败提示一次；检查与下载全程单飞防重入；可用态点击按「下载→校验→排空→静默
/// 安装→优雅退出」的锁定顺序触发；下载失败清理后保持可用态可重试；设置开关
/// 即时生效与持久化；新按钮继承头部悬停淡显与 #17 armed 常显；契约 #13——
/// 悬停淡显后 Win32 消息直驱真实点击可达。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    private const string UpdateVersion = "99.0.0";

    private static UpdateCheckResult AvailableUpdate() => UpdateCheckResult.UpdateAvailable(
        UpdateVersion,
        "https://github.com/Oiawlm/floating-transfer-station/releases/tag/v99.0.0",
        new UpdateAsset(
            $"FloatingTransferStation-Setup-{UpdateVersion}.exe",
            $"https://example.com/FloatingTransferStation-Setup-{UpdateVersion}.exe",
            123),
        new UpdateAsset(
            "SHA256SUMS.txt",
            "https://example.com/SHA256SUMS.txt",
            99));

    private static Button UpdateButtonOf(MainWindow window) =>
        (Button)window.FindName("UpdateButton")!;

    private static string StatusTextOf(MainWindow window) =>
        ((MainWindowViewModel)window.DataContext).StatusText;

    /// <summary>驱动一次定时检查 tick 并等检查结算（fake 服务同步完成）。</summary>
    private static void RunScheduledCheck(MainWindow window)
    {
        InvokePrivate(window, "RunScheduledUpdateCheck");
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
    }

    private static void ClickUpdateButton(MainWindow window)
    {
        // 在 Dispatcher 上触发 async void 点击处理器：测试宿主线程没有 WPF 同步
        // 上下文，直接调用会让 await 续体落在线程池并跨线程触碰 WPF 对象
        // （应用内由 DispatcherSynchronizationContext 自动派发，此处显式派发等价）。
        InvokeClickOnDispatcher(window);
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
    }

    private static void InvokeClickOnDispatcher(MainWindow window) =>
        window.Dispatcher.Invoke(
            () => InvokePrivate(
                window,
                "UpdateButton_Click",
                window.FindName("UpdateButton"),
                new RoutedEventArgs(Button.ClickEvent, window.FindName("UpdateButton"))));

    [STATestMethod]
    public void UpdateButton_IsFirstHeaderAction()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService(), updateService: new FakeUpdateService());

        try
        {
            var actions = (StackPanel)window.FindName("HeaderActions")!;
            Assert.AreSame(
                window.FindName("UpdateButton"),
                actions.Children[0],
                "更新按钮必须是头部按钮行的第一个子元素（最左常驻）。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void UpdateButton_DefaultState_IsCheckUpdatesInSecondaryColor()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService(), updateService: new FakeUpdateService());

        try
        {
            window.Show();
            CompleteLayout(window);
            var button = UpdateButtonOf(window);
            Assert.AreEqual("检查更新", button.ToolTip, "无更新时按钮必须处于手动检查语义。");
            Assert.AreEqual("检查更新", AutomationProperties.GetName(button));
            Assert.AreSame(
                window.TryFindResource("SecondaryTextBrush"),
                button.Foreground,
                "无更新态必须使用次级文字色。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void UpdateCheck_NewerVersionFound_LightsButtonAndPromptsOnceWhenPanelVisible()
    {
        using var directory = new TestDirectory();
        var fake = new FakeUpdateService { CheckResult = AvailableUpdate() };
        var window = CreateWindow(directory, new BoardService(), updateService: fake);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var button = UpdateButtonOf(window);

            RunScheduledCheck(window);

            Assert.AreEqual(
                $"发现新版本 {UpdateVersion}，点击更新",
                button.ToolTip,
                "有更新态 tooltip 必须携带新版本号。");
            Assert.AreEqual($"发现新版本 {UpdateVersion}，点击更新", AutomationProperties.GetName(button));
            Assert.AreSame(
                window.TryFindResource("AccentBrush"),
                button.Foreground,
                "有更新态必须点亮强调色（业界先例：着色提示是必要项）。");
            Assert.IsTrue(
                StatusTextOf(window).Contains(UpdateVersion),
                "面板可见时发现新版本必须状态条提示一次。");

            // 下一个检查周期再次确认同一版本：不再重复提示（提示疲劳防护）。
            var promptOnce = StatusTextOf(window);
            RunScheduledCheck(window);
            Assert.AreEqual(promptOnce, StatusTextOf(window), "同一版本重复检查不得重复提示。");
            Assert.AreEqual(2, fake.CheckCallCount);
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void UpdateCheck_AutoCheckFailure_IsCompletelySilent()
    {
        using var directory = new TestDirectory();
        var fake = new FakeUpdateService
        {
            CheckResult = UpdateCheckResult.Fail(UpdateErrorKind.RateLimited, "请求过于频繁，请稍后再试。")
        };
        var window = CreateWindow(directory, new BoardService(), updateService: fake);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            RunScheduledCheck(window);

            Assert.AreEqual(1, fake.CheckCallCount);
            Assert.IsEmpty(StatusTextOf(window), "自动检查的任何失败必须完全静默。");
            Assert.IsNull(GetPrivateField<UpdateCheckResult>(window, "_availableUpdate"));
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void UpdateButton_NoUpdateClick_ShowsUpToDateFeedback()
    {
        using var directory = new TestDirectory();
        var fake = new FakeUpdateService();
        var window = CreateWindow(directory, new BoardService(), updateService: fake);

        try
        {
            window.Show();
            CompleteLayout(window);

            ClickUpdateButton(window);

            Assert.AreEqual(1, fake.CheckCallCount);
            Assert.AreEqual("已是最新版本。", StatusTextOf(window), "无更新手动检查必须就地反馈。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void UpdateButton_ManualCheckFailure_ShowsStatusOnce()
    {
        using var directory = new TestDirectory();
        var fake = new FakeUpdateService
        {
            CheckResult = UpdateCheckResult.Fail(UpdateErrorKind.RateLimited, "请求过于频繁，请稍后再试。")
        };
        var window = CreateWindow(directory, new BoardService(), updateService: fake);

        try
        {
            window.Show();
            CompleteLayout(window);

            ClickUpdateButton(window);

            Assert.AreEqual(
                "检查更新失败：请求过于频繁，请稍后再试。",
                StatusTextOf(window),
                "手动检查失败必须经状态条提示一次。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void UpdateButton_SingleFlight_IgnoresClicksAndTicksWhileOperationInFlight()
    {
        using var directory = new TestDirectory();
        var fake = new FakeUpdateService();
        var held = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.HeldCheck = held.Task;
        var window = CreateWindow(directory, new BoardService(), updateService: fake);

        try
        {
            window.Show();
            CompleteLayout(window);

            InvokeClickOnDispatcher(window);
            Assert.AreEqual(1, fake.CheckCallCount, "在途检查必须恰有一次。");

            // 在途期间：再次点击与定时 tick 都不得重入。
            InvokeClickOnDispatcher(window);
            InvokePrivate(window, "RunScheduledUpdateCheck");
            Assert.AreEqual(1, fake.CheckCallCount, "检查与下载全程单飞防重入。");

            held.SetResult(UpdateCheckResult.UpToDate());
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            Assert.AreEqual("已是最新版本。", StatusTextOf(window));
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void UpdateButton_AvailableUpdateClick_RunsDownloadThenInstallThenGracefulExit()
    {
        using var directory = new TestDirectory();
        var fake = new FakeUpdateService { CheckResult = AvailableUpdate() };
        var installerPath = Path.Combine(directory.Root, $"FloatingTransferStation-Setup-{UpdateVersion}.exe");
        File.WriteAllText(installerPath, "installer");
        fake.DownloadResult = UpdateDownloadResult.Ok(installerPath);
        var window = CreateWindow(directory, new BoardService(), updateService: fake);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            // 先经自动检查进入可用态，再点击执行更新。
            RunScheduledCheck(window);
            ClickUpdateButton(window);

            CollectionAssert.AreEqual(
                new[] { "check", "download", "install" },
                fake.EventLog,
                "点击可用态按钮必须按「下载→（已校验）→静默安装」顺序执行（校验在服务内，失败不会到达安装）。");
            Assert.AreEqual(
                GetPrivateStaticField<string>("UpdateDownloadDirectory"),
                fake.DownloadedDirectory,
                "安装包必须下载到专用 %TEMP% 子目录。");
            Assert.AreSame(fake.CheckResult, fake.DownloadedCheck, "下载必须消费检查结果的精确资产定位。");
            Assert.AreEqual(installerPath, fake.StartedInstallerPath);
            Assert.IsTrue(
                GetPrivateField<bool>(window, "_isClosing"),
                "启动安装器后必须经既有优雅退出路径关闭（安装器查锁时应用须已退出）。");
        }
        finally
        {
            // 更新流程自身已走优雅退出路径：窗口可能已（或正在）关闭，仅未关闭时补关。
            if (window.IsLoaded)
            {
                CloseWindowWithoutSaving(window);
            }
        }
    }

    [STATestMethod]
    public void UpdateButton_DownloadFailure_StaysAvailableAndRetryable()
    {
        using var directory = new TestDirectory();
        var fake = new FakeUpdateService { CheckResult = AvailableUpdate() };
        fake.DownloadResult = UpdateDownloadResult.Fail(UpdateErrorKind.HashMismatch, "更新包校验失败，已放弃安装。");
        var window = CreateWindow(directory, new BoardService(), updateService: fake);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var button = UpdateButtonOf(window);

            RunScheduledCheck(window);
            ClickUpdateButton(window);

            Assert.AreEqual(
                "更新包校验失败，已放弃安装。",
                StatusTextOf(window),
                "下载/校验失败必须状态条提示。");
            CollectionAssert.AreEqual(new[] { "check", "download" }, fake.EventLog, "校验失败绝不启动安装器。");

            // 回到可用态：按钮仍点亮、点击可重试。
            Assert.IsNotNull(GetPrivateField<UpdateCheckResult>(window, "_availableUpdate"));
            Assert.AreEqual($"发现新版本 {UpdateVersion}，点击更新", button.ToolTip);
            ClickUpdateButton(window);
            Assert.AreEqual(2, fake.DownloadCallCount, "失败后按钮保持可用态可重试。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void UpdateButton_InheritsHeaderHoverRevealAndEdgeHideArmedAlwaysVisible()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService(), updateService: new FakeUpdateService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var actions = (StackPanel)window.FindName("HeaderActions")!;

            // 指针离开：头部按钮行淡隐且不可命中（新按钮同属宿主，随之隐藏）。
            InvokePrivate(window, "HeaderActionRegion_MouseLeave", window, NewMouseEventArgs());
            Assert.IsFalse(FadeAnimation.GetIsActive(actions), "头部按钮行必须随指针离开淡隐。");
            Assert.IsFalse(actions.IsHitTestVisible);

            // #17：贴边隐藏 armed 期间头部按钮行常显（新按钮自动继承）。
            InvokePrivate(
                window,
                "EdgeHideButton_Click",
                window.FindName("EdgeHideButton"),
                new RoutedEventArgs(Button.ClickEvent, window.FindName("EdgeHideButton")));
            InvokePrivate(window, "HeaderActionRegion_MouseLeave", window, NewMouseEventArgs());
            Assert.IsTrue(FadeAnimation.GetIsActive(actions), "armed 期间头部按钮行必须常显。");
            Assert.IsTrue(actions.IsHitTestVisible);
        }
        finally
        {
            RestoreAndClose(window);
        }
    }

    [STATestMethod]
    public void UpdateButton_RealInputClick_ReachesHandlerThroughHoverFadeIn()
    {
        using var directory = new TestDirectory();
        var fake = new FakeUpdateService();
        var window = CreateWindow(directory, new BoardService(), updateService: fake);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var button = UpdateButtonOf(window);
            window.Topmost = true;
            CompleteLayout(window);
            var savedCursor = SaveCursorPosition();

            try
            {
                // 契约 #13：悬停淡显后，真实硬件级点击（SendInput 完整输入管线）必须
                // 到达按钮并触发手动检查——非合成路由事件。
                var center = new Point(button.ActualWidth / 2, button.ActualHeight / 2);
                HoverRealAt(window, button, center);
                Assert.IsTrue(
                    ((StackPanel)window.FindName("HeaderActions")!).IsHitTestVisible,
                    "DIAG: 真实悬停必须先经 HeaderActionRegion_MouseEnter 点亮头部按钮行。");
                ClickBySendInput(window);

                Assert.AreEqual(1, fake.CheckCallCount, "真实点击必须触发手动检查。");
                Assert.AreEqual("已是最新版本。", StatusTextOf(window));
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void AutoUpdateCheckToggle_TakesEffectImmediatelyAndPersists()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateWindow(
            directory,
            new BoardService(),
            updateService: new FakeUpdateService(),
            preferencesStore: preferencesStore);

        try
        {
            window.Show();
            CompleteLayout(window);
            var timer = GetPrivateField<DispatcherTimer>(window, "_updateCheckTimer");
            Assert.IsTrue(timer.IsEnabled, "默认开启：启动后自动检查定时器必须运行。");
            Assert.AreEqual(TimeSpan.FromSeconds(30), timer.Interval, "首检节奏=启动 30 秒后。");

            var settings = OpenSettingsWindow(window);
            try
            {
                var toggle = (System.Windows.Controls.CheckBox)settings.FindName("AutoUpdateCheckToggle")!;
                Assert.IsTrue(toggle.IsChecked == true, "设置窗开关必须回显偏好默认开启。");

                toggle.IsChecked = false;
                CompleteLayout(window);
                Assert.IsFalse(timer.IsEnabled, "关闭开关必须立即停表（关=停定时器）。");
                Assert.IsNotNull(preferencesStore.LastSaved);
                Assert.IsFalse(preferencesStore.LastSaved.AutoUpdateCheckEnabled, "开关变化必须经 ApplyPreferences 持久化。");

                toggle.IsChecked = true;
                CompleteLayout(window);
                Assert.IsTrue(timer.IsEnabled, "重新开启必须恢复节奏（开=恢复定时器）。");
                Assert.IsTrue(preferencesStore.LastSaved!.AutoUpdateCheckEnabled);
            }
            finally
            {
                // 设置窗无关闭拦截：直接等 Closed，不走 MainWindow 专属的 _allowClose 通道。
                CloseWindow(settings);
            }
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    private static SettingsWindow OpenSettingsWindow(MainWindow window)
    {
        window.OpenSettings();
        var settings = window.GetType()
            .GetField("_settingsWindow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(window) as SettingsWindow;
        Assert.IsNotNull(settings, "打开设置窗口失败。");
        return settings;
    }

    // ---- 真实输入（契约 #13）：SendInput 硬件级注入 ----
    // 探针实测：PostMessage 直投窗内消息在头部按钮区不可达（既有设置按钮同样丢消息，
    // 卡片区可用）；SendInput 从 OS 输入硬件层注入、经完整 WPF 输入管线（含悬停淡显
    // 前置的 HeaderActionRegion_MouseEnter）到达按钮，是头部路径可用的最强真实输入。

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint inputCount, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public MOUSEINPUT Mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint DwFlags;
        public uint Time;
        public nint ExtraInfo;
    }

    private const uint MouseEventfLeftdown = 0x0002;
    private const uint MouseEventfLeftup = 0x0004;
    private const uint InputMouse = 0;

    /// <summary>在光标当前位置（已由 HoverRealAt 置于目标上）注入真实按下/抬起。</summary>
    private static void ClickBySendInput(MainWindow window)
    {
        var inputs = new INPUT[2];
        inputs[0].Type = InputMouse;
        inputs[0].Mi.DwFlags = MouseEventfLeftdown;
        inputs[1].Type = InputMouse;
        inputs[1].Mi.DwFlags = MouseEventfLeftup;
        Assert.AreEqual(2u, SendInput(2, inputs, Marshal.SizeOf<INPUT>()), "真实输入注入必须成功。");
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
    }

    /// <summary>记录偏好保存的假存储：本地自足（与既有测试文件各自持有同名假实现同法）。</summary>
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

    /// <summary>记录调用顺序的假更新服务：检查可挂起（单飞验证），下载/安装只记录。</summary>
    private sealed class FakeUpdateService : IUpdateService
    {
        public UpdateCheckResult CheckResult { get; set; } = UpdateCheckResult.UpToDate();
        public UpdateDownloadResult DownloadResult { get; set; } =
            UpdateDownloadResult.Fail(UpdateErrorKind.DownloadFailed, "未配置下载结果。");
        public Task<UpdateCheckResult>? HeldCheck { get; set; }
        public int CheckCallCount { get; private set; }
        public int DownloadCallCount { get; private set; }
        public string? DownloadedDirectory { get; private set; }
        public UpdateCheckResult? DownloadedCheck { get; private set; }
        public string? StartedInstallerPath { get; private set; }
        public List<string> EventLog { get; } = new();

        public Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
        {
            CheckCallCount++;
            EventLog.Add("check");
            return HeldCheck ?? Task.FromResult(CheckResult);
        }

        public Task<UpdateDownloadResult> DownloadUpdateAsync(
            UpdateCheckResult check,
            string downloadDirectory,
            CancellationToken cancellationToken = default)
        {
            DownloadCallCount++;
            EventLog.Add("download");
            DownloadedDirectory = downloadDirectory;
            DownloadedCheck = check;
            return Task.FromResult(DownloadResult);
        }

        public bool TryStartSilentInstaller(string installerPath)
        {
            EventLog.Add("install");
            StartedInstallerPath = installerPath;
            return true;
        }
    }

    /// <summary>
    /// 发布证据（FTS_UPDATE_EVIDENCE_DIR 设置时产出真实 WPF 渲染截图，契约 #26）：
    /// 头部「更新」按钮双态——常态（次级色「检查更新」）与发现新版本态（强调色点亮）。
    /// 有更新态经 fake 服务回报真实走 AdoptCheckResult 采纳路径；渲染走
    /// SaveVisualEvidence（RenderTargetBitmap 实拍窗口壳层）。
    /// </summary>
    [STATestMethod]
    public void Evidence_CaptureUpdateButtonDualStates()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("FTS_UPDATE_EVIDENCE_DIR")))
        {
            Assert.Inconclusive("未设置 FTS_UPDATE_EVIDENCE_DIR，跳过发布证据产出。");
        }

        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText(new string('更', 160), BoardCategory.Inbox);
        var fake = new FakeUpdateService { CheckResult = AvailableUpdate() };
        var window = CreateWindow(directory, board, updateService: fake);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            window.Topmost = true;
            CompleteLayout(window);
            // 头部按钮行默认悬停淡显：截图用真实显隐路径点亮（与 armed 常显同源），
            // 呈现用户悬停头部时看到的样子。
            InvokePrivate(window, "SetHeaderActionsVisible", true);
            CompleteLayout(window);

            // 常态：次级色「检查更新」。
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "update-button-default.png",
                "FTS_UPDATE_EVIDENCE_DIR");

            // 发现新版本态：强调色点亮（fake 服务回报可用更新，真实走检查采纳路径）。
            InvokePrivate(window, "RunScheduledUpdateCheck");
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            Assert.IsNotNull(GetPrivateField<UpdateCheckResult>(window, "_availableUpdate"));
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "update-button-available.png",
                "FTS_UPDATE_EVIDENCE_DIR");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }
}
