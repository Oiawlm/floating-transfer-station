using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 「文字卡 AI 整理」（1.41.0，契约 #29）的窗口层守护：AI 钮可见性矩阵
/// （六条件＝备注钮七条件减「无备注」，备注与 AI 正交）、追加行几何
/// （短卡高度不变、截断卡悬停高度稳定、追加行塌缩语义）、真实输入链
/// （Win32 点击→忙态→替换生效→真实键盘 Ctrl+Z 恢复原文，契约 #13）、
/// 无 Key 零请求、在途改文放弃、合成/UIA 分支。DeepSeek 经假 HttpMessageHandler
/// 全离线（照 DeepSeekClientTests 模式），不携带任何真实密钥。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    private static Button CardAiButtonOf(ListBoxItem container) =>
        FindDescendants<Button>(container).Single(button => button.Name == "CardOperationAiOrganizeButton");

    private static FrameworkElement? CardAiBusyIconOf(ListBoxItem container) =>
        FindDescendants<FrameworkElement>(container)
            .FirstOrDefault(element => element.Name == "CardAiBusyIcon");

    [STATestMethod]
    public void CardAi_ButtonVisibilityMatrix()
    {
        // 契约 #29 入口七条件（与备注同门槛减「无备注」）：正文截断 ∧ 卡片悬停 ∧
        // 无正文编辑 ∧ 无备注编辑 ∧ 文字卡 ∧ 非搜索态；有备注照常出现（正交）。
        using var directory = new TestDirectory();
        var board = new BoardService();
        var longItem = board.AddText(LongCardText, BoardCategory.Inbox);
        var notedItem = board.AddText(LongCardText + "\n备", BoardCategory.Inbox);
        notedItem.Note = "已有备注";
        var shortItem = board.AddText("短卡字", BoardCategory.Inbox);
        var imageItem = AddImageCard(directory, board, "ai-matrix.png");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var longContainer = ContainerOf(window, longItem);
            var notedContainer = ContainerOf(window, notedItem);
            var shortContainer = ContainerOf(window, shortItem);
            var imageContainer = ContainerOf(window, imageItem);
            window.Topmost = true;
            CompleteLayout(window);
            var aiOnLong = CardAiButtonOf(longContainer);
            var aiOnNoted = CardAiButtonOf(notedContainer);
            var aiOnShort = CardAiButtonOf(shortContainer);
            var aiOnImage = CardAiButtonOf(imageContainer);
            var savedCursor = SaveCursorPosition();
            // 真实光标是跨测试的全局状态：先移离贴右缘的窗口（屏幕左上角），
            // 才能断言「未悬停」基线（上一测试遗留的悬停位会让 IsMouseOver 为真）。
            Assert.IsTrue(
                SetCursorPos(10, 10),
                "基线断言前必须把真实光标移离窗口。");
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(120));
            Assert.IsFalse(FadeAnimation.GetIsActive(aiOnLong), "未悬停时 AI 钮必须不可见。");
            Assert.AreEqual(Visibility.Collapsed, aiOnLong.Visibility, "未悬停时 AI 钮必须塌缩。");

            try
            {
                // 截断文字卡悬停：激活且可命中。
                HoverRealAt(window, longContainer, new Point(24, longContainer.ActualHeight * 0.8));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(120));
                Assert.IsTrue(FadeAnimation.GetIsActive(aiOnLong), "截断文字卡悬停必须激活 AI 钮。");
                Assert.IsTrue(aiOnLong.IsHitTestVisible);
                Assert.AreEqual(Visibility.Visible, aiOnLong.Visibility);
                Assert.IsTrue(aiOnLong.IsEnabled, "按钮恒不禁用（忙态防点击穿透，#19 先例）。");

                // 有备注的截断卡：照常出现（备注与 AI 正交，不含「无备注」条件）。
                HoverRealAt(window, notedContainer, new Point(24, notedContainer.ActualHeight * 0.8));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
                Assert.IsTrue(FadeAnimation.GetIsActive(aiOnNoted), "有备注的截断卡悬停必须照常出现 AI 钮。");

                // 短卡/图片卡：塌缩。
                HoverRealAt(window, shortContainer, new Point(24, shortContainer.ActualHeight * 0.8));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
                Assert.IsFalse(FadeAnimation.GetIsActive(aiOnShort), "短卡不得出现 AI 钮。");
                Assert.AreEqual(Visibility.Collapsed, aiOnShort.Visibility);
                HoverRealAt(window, imageContainer, new Point(24, imageContainer.ActualHeight * 0.8));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
                Assert.IsFalse(FadeAnimation.GetIsActive(aiOnImage), "图片卡不得出现 AI 钮。");
                Assert.AreEqual(Visibility.Collapsed, aiOnImage.Visibility);

                // 正文编辑中隐藏。
                HoverRealAt(window, longContainer, new Point(24, longContainer.ActualHeight * 0.8));
                RaiseCardDoubleClick(ZoneHitSource(window, longItem, CardGestureZones.ContentZone));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
                Assert.IsTrue(longItem.IsEditing, "前置条件：正文编辑已开启。");
                Assert.IsFalse(FadeAnimation.GetIsActive(aiOnLong), "正文编辑中必须隐藏 AI 钮。");
                Assert.AreEqual(Visibility.Collapsed, aiOnLong.Visibility);
                window.CommitCardTextForTest();

                // 备注编辑中隐藏。
                RaiseNoteStripClick(window, longItem, "CardOperationNoteButton");
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(120));
                Assert.IsTrue(longItem.IsEditingNote, "前置条件：备注编辑已开启。");
                HoverRealAt(window, longContainer, new Point(24, longContainer.ActualHeight * 0.8));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
                Assert.IsFalse(FadeAnimation.GetIsActive(aiOnLong), "备注编辑中必须隐藏 AI 钮。");
                Assert.AreEqual(Visibility.Collapsed, aiOnLong.Visibility);
                window.CommitCardTextForTest();

                // 搜索态隐藏（搜索视图会重建列表容器，重取容器与按钮）。
                InvokePrivate(window, "EnterSearchMode");
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(120));
                var searchContainer = ContainerOf(window, longItem);
                var searchAiButton = CardAiButtonOf(searchContainer);
                HoverRealAt(window, searchContainer, new Point(24, searchContainer.ActualHeight * 0.8));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
                Assert.IsFalse(FadeAnimation.GetIsActive(searchAiButton), "搜索态必须隐藏 AI 钮。");
                Assert.AreEqual(Visibility.Collapsed, searchAiButton.Visibility);
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardAi_ShortCardKeepsBaselineHeightAndAppendRowCollapses()
    {
        // 契约 #16/#19 修订：追加行网格只在截断文字卡显形——短卡整行塌缩，
        // 卡片高度不受追加行存在牵动（若追加行泄漏进布局，短卡最低高度会从
        // 64 抬到 98＋边距）。
        using var directory = new TestDirectory();
        var board = new BoardService();
        var shortItem = board.AddText("短", BoardCategory.Inbox);
        var longItem = board.AddText(LongCardText, BoardCategory.Inbox);
        var imageItem = AddImageCard(directory, board, "ai-height.png");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var shortContainer = ContainerOf(window, shortItem);
            var longContainer = ContainerOf(window, longItem);
            var imageContainer = ContainerOf(window, imageItem);
            window.Topmost = true;
            CompleteLayout(window);

            var shortAppendRow = (Grid)CardAiButtonOf(shortContainer).Parent;
            var longAppendRow = (Grid)CardAiButtonOf(longContainer).Parent;
            var imageAppendRow = (Grid)CardAiButtonOf(imageContainer).Parent;
            Assert.AreEqual(Visibility.Collapsed, shortAppendRow.Visibility, "短卡的追加行必须整行塌缩。");
            Assert.AreEqual(Visibility.Collapsed, imageAppendRow.Visibility, "图片卡的追加行必须整行塌缩。");
            Assert.AreEqual(Visibility.Visible, longAppendRow.Visibility, "截断文字卡的追加行常在（显隐由截断布局事实驱动，不随悬停派生）。");

            // 基线口径：追加行塌缩时操作列高度恒为基座 64（12+64+12=88 为卡内网格
            // 期望高，与既有基线一致）；截断卡追加行展开后操作列 98（64+4+30），
            // 仍不高于正文钳制 100——任何卡高度零跳变、短卡下限不变。
            var shortOperationsStack = (StackPanel)shortAppendRow.Parent;
            var longOperationsStack = (StackPanel)longAppendRow.Parent;
            Assert.AreEqual(64d, shortOperationsStack.DesiredSize.Height, 0.5, "短卡的操作列期望高度必须只有基座 64（追加行零布局占地）。");
            Assert.AreEqual(98d, longOperationsStack.DesiredSize.Height, 0.5, "截断卡的操作列期望高度应为基座 64＋间隔 4＋追加行 30。");
            Assert.IsTrue(
                shortContainer.ActualHeight < longContainer.ActualHeight,
                "短卡高度必须低于截断卡（追加行只在截断卡显形）。");

            // 悬停只控制行内按钮显隐，追加行自身常在：截断卡高度零跳变。
            var heightBefore = longContainer.ActualHeight;
            var savedCursor = SaveCursorPosition();
            try
            {
                HoverRealAt(window, longContainer, new Point(24, longContainer.ActualHeight * 0.8));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
                Assert.IsTrue(
                    FadeAnimation.GetIsActive(CardAiButtonOf(longContainer)),
                    "前置条件：悬停已触发 AI 钮淡入（验证的是它出现时的高度稳定性）。");
                Assert.AreEqual(
                    heightBefore,
                    longContainer.ActualHeight,
                    0.5,
                    "悬停出现 AI 钮不得改变卡片高度（追加行只在截断卡显形，正文恒钳 100 ≥ 98）。");
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardAi_RealInputClickOrganizesAndRealKeyboardCtrlZRestores()
    {
        // 契约 #13+#29 端到端：真实输入点击 AI 钮（完整输入管线）→忙态→
        // 替换正文生效→真实键盘 Ctrl+Z（SendInput 硬件级注入，真实修饰键状态）
        // 恢复原文。全链路经假 handler 离线完成。
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText(LongCardText, BoardCategory.Inbox);
        var handler = new FakeOrganizerHandler(
            """{"id":"1","model":"deepseek-flash","choices":[{"index":0,"message":{"role":"assistant","content":"整理后的完整正文"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""",
            delayUntilReleased: true);
        var window = CreateCardAiWindow(directory, board, handler);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            window.Topmost = true;
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var button = CardAiButtonOf(container);
            var savedCursor = SaveCursorPosition();

            try
            {
                // 悬停揭示 AI 钮（截断卡），真实点击（Win32 消息直驱，契约 #13
                // 口径；按钮命中路径由按钮命令承接，Up 不经手势层置 Handled）。
                HoverRealAt(window, container, new Point(24, container.ActualHeight * 0.8));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(150));
                Assert.IsTrue(
                    FadeAnimation.GetIsActive(button) && button.IsHitTestVisible,
                    "前置条件：悬停已揭示 AI 钮。");
                var clickPoint = button.TranslatePoint(
                    new Point(button.ActualWidth / 2, button.ActualHeight / 2),
                    container);
                ClickRealAt(window, container, clickPoint, requireGestureHandled: false, hoverSettleMilliseconds: 120);

                // 忙态：IsAiOrganizing、忙态弧线可见、状态条在途文案。
                PumpUntil(window, () => item.IsAiOrganizing, "真实点击必须进入整理在途态。");
                var busyIcon = CardAiBusyIconOf(container);
                Assert.IsNotNull(busyIcon);
                Assert.AreEqual(Visibility.Visible, busyIcon.Visibility, "忙态弧线图标必须可见。");
                Assert.AreEqual("正在整理这张卡片…", viewModel.StatusText);

                // 释放应答：替换生效、忙态退出、状态条完成文案。
                handler.Release();
                PumpUntil(window, () => !item.IsAiOrganizing, "整理完成后忙态必须退出。");
                Assert.AreEqual("整理后的完整正文", board.FindItem(item.Id)!.Text, "整理结果必须直接替换正文。");
                Assert.AreEqual(Visibility.Collapsed, busyIcon.Visibility, "忙态结束后弧线图标必须塌缩。");
                Assert.AreEqual("整理完成，Ctrl + Z 可恢复原文。", viewModel.StatusText);
                Assert.AreEqual(1, handler.CallCount);

                // 真实键盘 Ctrl+Z：先把窗口带到前台（Windows 前台锁定下 Activate
                // 可能被拒；真实鼠标点击是既有先例的激活路径，点击点落在卡片左半
                // 无选择语义区），再注入真实修饰键状态（SendInput 硬件级，WPF 才能
                // 读到 Control 修饰键）。
                var activationPoint = container.PointToScreen(new Point(12, container.ActualHeight * 0.5));
                Assert.IsTrue(
                    SetCursorPos((int)Math.Round(activationPoint.X), (int)Math.Round(activationPoint.Y)),
                    "激活点击的光标落位必须成功。");
                ClickBySendInput(window);
                PressCtrlZBySendInput(window);
                PumpUntil(window, () => item.Text == LongCardText, "真实键盘 Ctrl+Z 必须恢复替换前的原文。");
                Assert.AreEqual("已恢复卡片原文。", viewModel.StatusText);
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardAi_NoKeyShowsStatusWithoutAnyRequest()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText(LongCardText, BoardCategory.Inbox);
        var handler = new FakeOrganizerHandler("""{"choices":[]}""");
        var window = CreateCardAiWindow(directory, board, handler, plaintextKey: null);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            RaiseAiButtonClick(window, item);

            Assert.AreEqual("尚未配置 DeepSeek API Key：设置 → AI 设置。", viewModel.StatusText);
            Assert.AreEqual(0, handler.CallCount, "无 Key 必须零请求。");
            Assert.IsFalse(item.IsAiOrganizing);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardAi_InFlightManualTextChangeAbandonsResult()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText(LongCardText, BoardCategory.Inbox);
        var handler = new FakeOrganizerHandler(
            """{"id":"1","model":"deepseek-flash","choices":[{"index":0,"message":{"role":"assistant","content":"迟到的整理结果"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""",
            delayUntilReleased: true);
        var window = CreateCardAiWindow(directory, board, handler);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            RaiseAiButtonClick(window, item);
            PumpUntil(window, () => item.IsAiOrganizing, "前置条件：整理在途。");

            // 在途期间用户手工改文：返回结果不得覆盖新状态。
            board.UpdateText(item.Id, "手工改过的文本");
            handler.Release();
            PumpUntil(window, () => !item.IsAiOrganizing, "在途会话必须结束。");

            Assert.AreEqual("手工改过的文本", board.FindItem(item.Id)!.Text, "并发守卫必须放弃整理结果。");
            Assert.AreEqual("卡片内容已变化，整理结果已放弃。", viewModel.StatusText);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardAi_SyntheticClickEventRoutesThroughButtonClick()
    {
        // 合成/UIA 路径（BoardList_ButtonClick 的 AiOrganize 分支）可达。
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText(LongCardText, BoardCategory.Inbox);
        var handler = new FakeOrganizerHandler(
            """{"id":"1","model":"deepseek-flash","choices":[{"index":0,"message":{"role":"assistant","content":"合成路径整理结果"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""");
        var window = CreateCardAiWindow(directory, board, handler);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            var button = CardAiButtonOf(ContainerOf(window, item));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

            PumpUntil(window, () => board.FindItem(item.Id)!.Text == "合成路径整理结果", "合成分支必须完成整理替换。");
            Assert.AreEqual(1, handler.CallCount);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    /// <summary>手势层合成入口：在 AI 钮上发出一对预览按下/抬起（照备注钮同法）。</summary>
    private static void RaiseAiButtonClick(MainWindow window, BoardItem item)
    {
        var button = CardAiButtonOf(ContainerOf(window, item));
        RaiseCardPressAndRelease(button, NextIsolatedClickTimestamp());
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(120));
    }

    /// <summary>
    /// 构建带离线 DeepSeek 管线的测试窗口：假 Key 保护器（按配置返回明文）、
    /// 真 AiTextOrganizer 挂假 handler 客户端（全离线，不携带真实密钥）。
    /// </summary>
    private static MainWindow CreateCardAiWindow(
        TestDirectory directory,
        BoardService board,
        FakeOrganizerHandler handler,
        string? plaintextKey = "sk-test-fake-key")
    {
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var operationGate = new BoardOperationGate();
        MainWindow? window = null;
        void ShowStatus(string message) => window?.ShowStatus(message);
        var normalizer = new ImageNormalizer(store.ImagesDirectory);
        var clipboard = new ClipboardCaptureService(
            new NeverReadClipboardReader(),
            normalizer,
            board,
            store,
            ShowStatus,
            operationGate: operationGate);
        var preferences = plaintextKey is null
            ? AppPreferences.Default
            : AppPreferences.Default with { DeepSeekApiKeyProtected = "fake-cipher" };
        window = new MainWindow(
            board,
            store,
            WindowSettings.Default,
            clipboard,
            new BoardMutationService(board, store, ShowStatus, operationGate),
            new DragPayloadService(),
            new ExternalDropPayloadReader(new WindowsDataImageReader()),
            new ExternalDropImportService(normalizer, board, store, ShowStatus, operationGate),
            preferences: preferences,
            secretProtector: new FakeCardAiSecretProtector(plaintextKey),
            deepSeekClient: new DeepSeekClient(handler),
            aiTextOrganizer: new AiTextOrganizer(new DeepSeekClient(handler)));
        return window;
    }

    private sealed class FakeCardAiSecretProtector(string? key) : ISecretProtector
    {
        public string Protect(string plaintext) => "fake-cipher";

        public string? Unprotect(string ciphertext) => key;
    }

    /// <summary>
    /// 记录请求数并返回固定应答体的假 handler；delayUntilReleased 时挂起应答
    /// 直到 Release（供在途/忙态用例观察中间态）。全程不发起真实网络。
    /// </summary>
    private sealed class FakeOrganizerHandler(string responseBody, bool delayUntilReleased = false)
        : HttpMessageHandler
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (delayUntilReleased)
            {
                await _release.Task.WaitAsync(cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    // ---- 真实键盘（契约 #13）：SendInput 硬件级注入 Ctrl+Z ----
    // SendInput 设置真实异步修饰键状态，WPF 的 KeyboardDevice.Modifiers 才能
    // 读到 Control；要求目标窗口已是前台（前置的真实点击已激活本窗口）。

    [DllImport("user32.dll", EntryPoint = "SendInput")]
    private static extern uint SendKeyboardInput(uint inputCount, KeyboardInput[] inputs, int size);

    /// <summary>INPUT 的键盘变体：数组步长必须等于 sizeof(INPUT)（联合体按最大
    /// 成员 MOUSEINPUT 计 40 字节，x64），否则 SendInput 以参数无效拒绝注入。</summary>
    [StructLayout(LayoutKind.Sequential, Size = 40)]
    private struct KeyboardInput
    {
        public uint Type;
        public KeyboardHardwareInput Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHardwareInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    private const uint InputKeyboard = 1;
    private const uint KeyEventfKeyup = 0x0002;
    private const ushort VkControl = 0x11;
    private const ushort VkZ = 0x5A;

    private static void PressCtrlZBySendInput(MainWindow window)
    {
        var inputs = new KeyboardInput[4];
        inputs[0].Type = InputKeyboard;
        inputs[0].Ki.VirtualKey = VkControl;
        inputs[1].Type = InputKeyboard;
        inputs[1].Ki.VirtualKey = VkZ;
        inputs[2].Type = InputKeyboard;
        inputs[2].Ki.VirtualKey = VkZ;
        inputs[2].Ki.Flags = KeyEventfKeyup;
        inputs[3].Type = InputKeyboard;
        inputs[3].Ki.VirtualKey = VkControl;
        inputs[3].Ki.Flags = KeyEventfKeyup;
        Assert.AreEqual(
            4u,
            SendKeyboardInput(4, inputs, Marshal.SizeOf<KeyboardInput>()),
            "真实键盘注入必须成功。");
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
    }
}
