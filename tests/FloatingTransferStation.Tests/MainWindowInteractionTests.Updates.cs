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
/// 检查更新与静默自动更新的窗口层契约（契约 #26，2026-11-03 修订 Issue #275）：
/// 头部按钮行最左常驻更新按钮；双态（无更新=次级色「检查更新」，有更新=强调色
/// + tooltip/无障碍名携带新版本号）；任何检查（手动或自动）发现新版本即自动续接
/// 完整静默更新流（下载→校验→排空→静默安装→优雅退出，无第二击）；自动检查的
/// 失败完全静默、手动检查失败状态条提示一次；无更新手动点击状态条反馈；检查与
/// 下载全程单飞防重入；下载失败清理后保持可用态可重试（点亮态点击=重试完整
/// 更新流）；三触发（30 秒首检/24 小时周期/2 小时交互空闲）统一调度不重复检查
///（时钟注入）；设置开关即时生效与持久化（关=定时与空闲都不跑，手动不受影响）；
/// 新按钮继承头部悬停淡显与 #17 armed 常显；契约 #13——悬停淡显后 Win32 消息
/// 直驱真实点击可达。
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

    /// <summary>驱动一次调度定时器 tick（三触发统一后的到期评估入口）并等结算。</summary>
    private static void RunUpdateCheckTick(MainWindow window)
    {
        InvokePrivate(window, "UpdateCheckTimer_Tick");
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
    }

    /// <summary>等待状态条出现期望文案（点击后的 async void 续体经固定 300ms 泵
    /// 存在竞态——CI 2026-11-03 红实证：下载失败续体晚于泵窗，断言读到检查期
    /// 旧文案。谓词等待消除竞态；超时由调用方断言以原语义消息暴露）。</summary>
    private static void WaitForStatusText(MainWindow window, string expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (StatusTextOf(window) != expected && DateTime.UtcNow < deadline)
        {
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
        }
    }

    /// <summary>通用谓词等待（更新流经 Drain/安装/退出的 async 续体结算，泵窗竞态
    /// 同 WaitForStatusText；返回最终谓词值，由调用方断言携带语义消息）。</summary>
    private static bool WaitForCondition(MainWindow window, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
        }

        return condition();
    }

    /// <summary>可推进的假时钟：空闲/周期触发的注入时间源（契约 #26 修订，时间抽象注入）。</summary>
    private sealed class MutableClock
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Now;
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
    public void UpdateCheck_NewerVersionFound_AutoRunsSilentInstallFlow()
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
            var button = UpdateButtonOf(window);

            // 自动检查发现新版本：不待第二击，直接自动续接完整静默更新流。
            RunScheduledCheck(window);
            Assert.IsTrue(
                WaitForCondition(window, () => GetPrivateField<bool>(window, "_isClosing")),
                "自动检查发现新版本必须自动走完「下载→校验→排空→静默安装→优雅退出」。");

            Assert.AreEqual(
                $"发现新版本 {UpdateVersion}，点击更新",
                button.ToolTip,
                "更新流在途/完成后按钮保持点亮态（点亮由版本比较派生）。");
            Assert.AreSame(fake.CheckResult, fake.DownloadedCheck, "下载必须消费检查结果的精确资产定位。");
            Assert.AreEqual(
                GetPrivateStaticField<string>("UpdateDownloadDirectory"),
                fake.DownloadedDirectory,
                "安装包必须下载到专用 %TEMP% 子目录。");
            Assert.AreEqual(installerPath, fake.StartedInstallerPath);
            CollectionAssert.AreEqual(
                new[] { "check", "download", "install" },
                fake.EventLog,
                "自动检查发现新版必须自动续接「下载→（已校验）→静默安装」（无第二击，契约 #26 修订）。");
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
    public void UpdateCheck_AutoCheckFailure_IsCompletelySilent()
    {
        using var directory = new TestDirectory();
        var fake = new FakeUpdateService
        {
            CheckResult = UpdateCheckResult.Fail(UpdateErrorKind.RateLimited, "请求过于频繁，请稍后重试。")
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
            CheckResult = UpdateCheckResult.Fail(UpdateErrorKind.RateLimited, "请求过于频繁，请稍后重试。")
        };
        var window = CreateWindow(directory, new BoardService(), updateService: fake);

        try
        {
            window.Show();
            CompleteLayout(window);

            ClickUpdateButton(window);

            WaitForStatusText(window, "检查更新失败：请求过于频繁，请稍后重试。");
            Assert.AreEqual(
                "检查更新失败：请求过于频繁，请稍后重试。",
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
            RunUpdateCheckTick(window);
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
    public void UpdateButton_Click_NewerVersionFound_ChecksDownloadsInstallsAndExitsInOneAction()
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

            // 单击可用态按钮＝检查＋自动续接安装：原「点检测→再点安装」两步收敛为
            // 单动作（契约 #26 修订，无第二次点击）。
            ClickUpdateButton(window);
            Assert.IsTrue(
                WaitForCondition(window, () => GetPrivateField<bool>(window, "_isClosing")),
                "单次点击必须自动走完「检查→发现新版→下载→校验→排空→静默安装→优雅退出」。");

            CollectionAssert.AreEqual(
                new[] { "check", "download", "install" },
                fake.EventLog,
                "一次点击必须按「检查→下载→（已校验）→静默安装」顺序完成（校验在服务内，失败不会到达安装）。");
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

            // 自动检查发现新版 → 自动续接安装流 → 下载/校验失败。
            RunScheduledCheck(window);
            WaitForStatusText(window, "更新包校验失败，已放弃安装。");
            Assert.AreEqual(
                "更新包校验失败，已放弃安装。",
                StatusTextOf(window),
                "下载/校验失败必须状态条提示。");
            CollectionAssert.AreEqual(new[] { "check", "download" }, fake.EventLog, "校验失败绝不启动安装器。");

            // 回到可用态：按钮仍点亮、点击可重试（点亮态点击=重试完整更新流）。
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
    public void UpdateCheck_AutoCheckFailure_LogsDiagnosticsWithoutUiDisturbance()
    {
        // 契约 #26 修订②：自动检查失败 UI 仍完全静默，但失败必须落本地诊断日志
        // （<数据根>\logs\update.log——测试数据根即 directory.Root），可诊断不扰民。
        using var directory = new TestDirectory();
        var fake = new FakeUpdateService
        {
            CheckResult = UpdateCheckResult.Fail(UpdateErrorKind.RateLimited, "请求过于频繁，请稍后重试。")
        };
        var window = CreateWindow(directory, new BoardService(), updateService: fake);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            RunScheduledCheck(window);

            Assert.IsEmpty(StatusTextOf(window), "自动检查失败必须保持 UI 零打扰（无状态条/toast/弹窗）。");
            var logPath = Path.Combine(directory.Root, "logs", "update.log");
            Assert.IsTrue(File.Exists(logPath), "自动检查失败必须写诊断日志 update.log。");
            var logText = File.ReadAllText(logPath);
            StringAssert.Contains(logText, "|检查|失败|", "失败记录必须带阶段与 kind 字段。");
            StringAssert.Contains(logText, "请求过于频繁，请稍后重试。", "失败记录必须携带失败原因。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void UpdateButton_DownloadFailure_LogsStageRecordsToDiagnostics()
    {
        // 契约 #26 修订②：更新流各阶段成败与里程碑落诊断日志——下载失败用例断言
        // 「发现新版里程碑＋下载阶段失败」两记录齐全（沿既有假件注入模式，日志落测试目录）。
        using var directory = new TestDirectory();
        var fake = new FakeUpdateService { CheckResult = AvailableUpdate() };
        fake.DownloadResult = UpdateDownloadResult.Fail(UpdateErrorKind.HashMismatch, "更新包校验失败，已放弃安装。");
        var window = CreateWindow(directory, new BoardService(), updateService: fake);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            RunScheduledCheck(window);
            WaitForStatusText(window, "更新包校验失败，已放弃安装。");

            var logText = File.ReadAllText(Path.Combine(directory.Root, "logs", "update.log"));
            StringAssert.Contains(
                logText,
                $"|检查|里程碑|发现新版本 {UpdateVersion}",
                "发现新版本的里程碑必须记录。");
            StringAssert.Contains(logText, "|下载|失败|", "下载/校验失败必须记录下载阶段。");
            StringAssert.Contains(logText, "更新包校验失败，已放弃安装。", "下载失败记录必须携带失败原因。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void UpdateCheck_IdleTwoHours_TriggersOneCheckPerIdleEpisode()
    {
        using var directory = new TestDirectory();
        var fake = new FakeUpdateService();
        var clock = new MutableClock();
        var window = CreateWindow(
            directory,
            new BoardService(),
            updateService: fake,
            updateClock: () => clock.Now);

        try
        {
            window.Show();
            CompleteLayout(window);
            Assert.AreEqual(0, fake.CheckCallCount);

            // 无交互 3 小时（≥2 小时空闲阈值）：调度 tick 到期触发一次检查。
            clock.Now = clock.Now.AddHours(3);
            RunUpdateCheckTick(window);
            Assert.AreEqual(1, fake.CheckCallCount, "对本应用窗口 ≥2 小时无交互必须触发一次检查。");

            // 同一空闲段不再重复：继续空闲 1 小时再 tick，无第二次检查。
            clock.Now = clock.Now.AddHours(1);
            RunUpdateCheckTick(window);
            Assert.AreEqual(1, fake.CheckCallCount, "同一空闲段至多触发一次检查（空闲臂已被派发消费）。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void UpdateCheck_UserInteractionReArmsIdleTrigger()
    {
        using var directory = new TestDirectory();
        var fake = new FakeUpdateService();
        var clock = new MutableClock();
        var window = CreateWindow(
            directory,
            new BoardService(),
            updateService: fake,
            updateClock: () => clock.Now);

        try
        {
            window.Show();
            CompleteLayout(window);

            // 指针进入本应用窗口（既有挂点汇入的交互记账）：空闲段重新起算。
            InvokePrivate(window, "NoteUserInteraction");
            clock.Now = clock.Now.AddHours(2).AddMinutes(5);
            RunUpdateCheckTick(window);
            Assert.AreEqual(1, fake.CheckCallCount, "交互后 ≥2 小时无操作触发检查。");

            clock.Now = clock.Now.AddHours(1);
            RunUpdateCheckTick(window);
            Assert.AreEqual(1, fake.CheckCallCount, "空闲段已消费，持续空闲不重复检查。");

            // 新交互重挂空闲触发臂：再满 2 小时触发第二次检查。
            InvokePrivate(window, "NoteUserInteraction");
            clock.Now = clock.Now.AddHours(2).AddMinutes(5);
            RunUpdateCheckTick(window);
            Assert.AreEqual(2, fake.CheckCallCount, "新的用户交互必须重挂空闲触发臂。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void UpdateCheck_ThreeTriggersUnified_NoDuplicateChecksWhenBothDue()
    {
        using var directory = new TestDirectory();
        var fake = new FakeUpdateService();
        var clock = new MutableClock();
        var window = CreateWindow(
            directory,
            new BoardService(),
            updateService: fake,
            updateClock: () => clock.Now);

        try
        {
            window.Show();
            CompleteLayout(window);

            // 交互后推进 26 小时：周期到期与空闲到期均已过——一次 tick 只派发一次
            // 检查（三触发共用同一「下次检查到期」状态与派发入口，不互相踩）。
            InvokePrivate(window, "NoteUserInteraction");
            clock.Now = clock.Now.AddHours(26);
            RunUpdateCheckTick(window);
            Assert.AreEqual(1, fake.CheckCallCount, "多触发同时到期也只检查一次（统一到期状态）。");

            // 派发已同时推进周期并消费空闲臂：紧接的 tick 不得重复检查。
            RunUpdateCheckTick(window);
            Assert.AreEqual(1, fake.CheckCallCount, "三触发统一调度：派发统一结算，不重复检查。");
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
                Assert.IsFalse(timer.IsEnabled, "关闭开关必须立即停表（关=定时与空闲检查都不跑）。");
                Assert.IsNotNull(preferencesStore.LastSaved);
                Assert.IsFalse(preferencesStore.LastSaved.AutoUpdateCheckEnabled, "开关变化必须经 ApplyPreferences 持久化。");

                toggle.IsChecked = true;
                CompleteLayout(window);
                Assert.IsTrue(timer.IsEnabled, "重新开启必须恢复调度（开=恢复定时器）。");
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

    /// <summary>记录调用顺序的假更新服务：检查与下载均可挂起（单飞/证据验证），安装只记录。</summary>
    private sealed class FakeUpdateService : IUpdateService
    {
        public UpdateCheckResult CheckResult { get; set; } = UpdateCheckResult.UpToDate();
        public UpdateDownloadResult DownloadResult { get; set; } =
            UpdateDownloadResult.Fail(UpdateErrorKind.DownloadFailed, "未配置下载结果。");
        public Task<UpdateCheckResult>? HeldCheck { get; set; }
        public Task<UpdateDownloadResult>? HeldDownload { get; set; }
        public int CheckCallCount { get; private set; }
        public int DownloadCallCount { get; private set; }
        public string? DownloadedDirectory { get; private set; }
        public UpdateCheckResult? DownloadedCheck { get; private set; }
        public string? StartedInstallerPath { get; private set; }
        public string? StartedInstallerLogPath { get; private set; }
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
            return HeldDownload ?? Task.FromResult(DownloadResult);
        }

        public bool TryStartSilentInstaller(string installerPath, string? installerLogPath = null)
        {
            EventLog.Add("install");
            StartedInstallerPath = installerPath;
            StartedInstallerLogPath = installerLogPath;
            return true;
        }
    }

    /// <summary>
    /// 发布证据（FTS_UPDATE_EVIDENCE_DIR 设置时产出真实 WPF 渲染截图，契约 #26）：
    /// 头部「更新」按钮双态——常态（次级色「检查更新」）与发现新版本态（强调色点亮，
    /// 状态条显示自动下载进行中）。有更新态经 fake 服务回报真实走 AdoptCheckResult
    /// 采纳路径并自动续接更新流（下载挂起供截图）；渲染走 SaveVisualEvidence
    ///（RenderTargetBitmap 实拍窗口壳层）。
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
        var heldDownload = new TaskCompletionSource<UpdateDownloadResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fake.HeldDownload = heldDownload.Task;
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

            // 发现新版本态：强调色点亮＋自动更新流在途（下载挂起，状态条显示自动
            // 下载文案——新语义的证据画面，fake 服务回报后真实走检查采纳与续接）。
            InvokePrivate(window, "RunScheduledUpdateCheck");
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            Assert.IsNotNull(GetPrivateField<UpdateCheckResult>(window, "_availableUpdate"));
            Assert.IsTrue(
                StatusTextOf(window).Contains($"正在下载更新（{UpdateVersion}"),
                "证据画面必须呈现「发现新版本即自动下载」的新语义状态条。");
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "update-button-available.png",
                "FTS_UPDATE_EVIDENCE_DIR");
            heldDownload.SetResult(
                UpdateDownloadResult.Fail(UpdateErrorKind.DownloadFailed, "证据截图完成，释放挂起的下载。"));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }
}
