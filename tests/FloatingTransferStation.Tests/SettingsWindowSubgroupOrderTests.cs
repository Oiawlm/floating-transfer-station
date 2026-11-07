using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatingTransferStation.Design;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 设置窗口「子分组顺序」节锁定测试（契约 #57 修订，<see
/// cref="SettingsWindowCategoryOrderTests"/> 的全套镜像，参数化差异＝两分类小节/
/// 行 Tag 槽号/提交按分类）：每分类四行按槽序渲染（名经名清单解析）；上移/下移
/// 提交即落 settings.json 并重排 chip 轨；拖拽提交换序；Esc 优先回弹不关窗；捕获
/// 丢失回弹；乐观提交（保存挂起期间行已目标序、失败回弹是唯一二次重建）；
/// Off 门控置灰＋模式切换解灰。
/// </summary>
[TestClass]
public sealed class SettingsWindowSubgroupOrderTests
{
    [STATestMethod]
    public void OrderSection_RendersPerCategoryRowsWithResolvedNamesAndBoundedMoveButtons()
    {
        using var directory = new TestDirectory();
        var inboxOrder = new[] { 3, 1, 0, 2 };
        var importantOrder = new[] { 2, 0, 3, 1 };
        var (window, store) = CreateContext(
            directory,
            s => s
                .WithSubgroupName(BoardCategory.Inbox, 1, "急件")
                .WithSubgroupOrder(BoardCategory.Inbox, inboxOrder)
                .WithSubgroupOrder(BoardCategory.CustomerOriginal, importantOrder));

        try
        {
            window.Show();
            var settings = OpenSettingsWindow(window);
            CompleteLayout(window);

            Assert.AreEqual(
                "待分类",
                GetSectionTitle(settings, BoardCategory.Inbox).Text,
                "小节标题＝分类显示名（重要/待分类两小节固定渲染）。");
            Assert.AreEqual(
                "重要",
                GetSectionTitle(settings, BoardCategory.CustomerOriginal).Text);

            var inboxRows = GetOrderRows(settings, BoardCategory.Inbox);
            CollectionAssert.AreEqual(
                inboxOrder,
                inboxRows.Select(row => (int)row.Tag).ToArray(),
                "Inbox 小节四行按该分类槽序渲染。");
            var importantRows = GetOrderRows(settings, BoardCategory.CustomerOriginal);
            CollectionAssert.AreEqual(
                importantOrder,
                importantRows.Select(row => (int)row.Tag).ToArray(),
                "重要小节四行按该分类槽序渲染（每分类独立）。");
            Assert.AreEqual(
                "急件",
                FindDescendants<TextBlock>(inboxRows[1])
                    .Single(t => AutomationProperties.GetName(t)!.StartsWith("分组")).Text,
                "行显示名经名清单逐槽解析喂入。");
            Assert.AreEqual(
                "待办",
                FindDescendants<TextBlock>(inboxRows[0])
                    .Single(t => AutomationProperties.GetName(t)!.StartsWith("分组")).Text,
                "默认槽名照目录解析。");

            var firstRow = inboxRows[0];
            Assert.AreEqual(
                false,
                FindDescendants<Button>(firstRow).Single(b => Equals(b.Content, "↑")).IsEnabled,
                "首行的上移按钮必须禁用。");
            var lastRow = inboxRows[^1];
            Assert.AreEqual(
                false,
                FindDescendants<Button>(lastRow).Single(b => Equals(b.Content, "↓")).IsEnabled,
                "末行的下移按钮必须禁用。");
            Assert.IsNotNull(
                FindDescendants<FrameworkElement>(firstRow)
                    .FirstOrDefault(element => AutomationProperties.GetName(element)!.StartsWith("拖动")),
                "拖拽把手必须有自动化名称。");
            _ = store;
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public async Task MoveUpButton_CommitsImmediatelyPersistsAndRerailsChips()
    {
        using var directory = new TestDirectory();
        var (window, store) = CreateContext(directory, s => s);

        try
        {
            window.Show();
            var viewModel = (MainWindowViewModel)window.DataContext;
            viewModel.Activate(viewModel.DefaultCapturePanel.Category);
            var settings = OpenSettingsWindow(window);
            CompleteLayout(window);

            var secondRow = GetOrderRows(settings, BoardCategory.Inbox)[1];
            FindDescendants<Button>(secondRow).Single(b => Equals(b.Content, "↑"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            // 默认序 [0,1,2,3]：第二行（槽 1）上移一位 → [1,0,2,3]。
            var expected = new[] { 1, 0, 2, 3 };
            await PumpUntilCompleted(window.Dispatcher);
            CompleteLayout(window);

            CollectionAssert.AreEqual(
                expected,
                viewModel.SubgroupChips.Select(chip => chip.Slot).ToArray(),
                "上移提交必须立即重排 chip 轨（激活分类）。");
            Assert.IsNotNull(store.LastSettings, "提交必须落 settings.json。");
            CollectionAssert.AreEqual(expected, store.LastSettings!.SubgroupOrder(BoardCategory.Inbox).ToArray());
            CollectionAssert.AreEqual(
                new[] { 0, 1, 2, 3 },
                store.LastSettings.SubgroupOrder(BoardCategory.CustomerOriginal).ToArray(),
                "另一分类槽序不受连坐。");
            var rows = GetOrderRows(settings, BoardCategory.Inbox);
            Assert.AreEqual(
                expected[0],
                (int)rows[0].Tag,
                "提交后行列表必须按新顺序重建。");
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public async Task MoveDownButton_IsEquivalenceOperationOfUp()
    {
        using var directory = new TestDirectory();
        var (window, store) = CreateContext(directory, s => s);

        try
        {
            window.Show();
            var settings = OpenSettingsWindow(window);
            CompleteLayout(window);

            var firstRow = GetOrderRows(settings, BoardCategory.Inbox)[0];
            FindDescendants<Button>(firstRow).Single(b => Equals(b.Content, "↓"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            // 首行下移一位与第二行上移一位等价 → [1,0,2,3]。
            var expected = new[] { 1, 0, 2, 3 };
            await PumpUntilCompleted(window.Dispatcher);

            Assert.IsNotNull(store.LastSettings);
            CollectionAssert.AreEqual(expected, store.LastSettings!.SubgroupOrder(BoardCategory.Inbox).ToArray());
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void DragCancelViaLostCapture_ReboundsWithoutCommitting()
    {
        using var directory = new TestDirectory();
        var (window, store) = CreateContext(directory, s => s);

        try
        {
            window.Show();
            var settings = OpenSettingsWindow(window);
            CompleteLayout(window);
            var originalOrder = ((MainWindowViewModel)window.DataContext).SubgroupChips
                .Select(chip => chip.Slot).ToArray();

            var handle = FindHandle(GetOrderRows(settings, BoardCategory.Inbox)[0]);
            handle.RaiseEvent(NewMouseButtonEventArgs(handle, UIElement.MouseLeftButtonDownEvent));
            Assert.IsTrue(handle.IsMouseCaptured, "前置条件：把手已捕获鼠标进入拖拽会话。");

            handle.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
            {
                RoutedEvent = UIElement.LostMouseCaptureEvent
            });
            CompleteLayout(window);

            Assert.IsNull(GetDragSession(settings), "捕获丢失必须结束拖拽会话。");
            Assert.AreEqual(0, store.SettingsSaveCount, "回弹不得提交任何顺序变更。");
            CollectionAssert.AreEqual(
                new[] { 0, 1, 2, 3 },
                GetOrderRows(settings, BoardCategory.Inbox).Select(row => (int)row.Tag).ToArray());
            CollectionAssert.AreEqual(
                originalOrder,
                ((MainWindowViewModel)window.DataContext).SubgroupChips.Select(chip => chip.Slot).ToArray());

            // 会话已结束：再次派发抬起不得产生任何效果。
            handle.RaiseEvent(NewMouseButtonEventArgs(handle, UIElement.MouseLeftButtonUpEvent));
            Assert.AreEqual(0, store.SettingsSaveCount);
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void EscapeDuringDrag_IsConsumedBySectionAndReboundsWithoutClosing()
    {
        using var directory = new TestDirectory();
        var (window, store) = CreateContext(directory, s => s);

        try
        {
            window.Show();
            var settings = OpenSettingsWindow(window);
            CompleteLayout(window);

            var handle = FindHandle(GetOrderRows(settings, BoardCategory.Inbox)[2]);
            handle.RaiseEvent(NewMouseButtonEventArgs(handle, UIElement.MouseLeftButtonDownEvent));
            Assert.IsTrue(handle.IsMouseCaptured);

            var escape = new KeyEventArgs(
                Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(settings)!,
                Environment.TickCount,
                Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
                Source = settings
            };
            settings.RaiseEvent(escape);
            CompleteLayout(window);

            Assert.IsTrue(escape.Handled, "拖拽会话中的 Esc 必须由顺序节优先消费。");
            Assert.IsTrue(settings.IsLoaded, "Esc 用于回弹拖拽，不得触发设置窗口关闭。");
            Assert.IsNull(GetDragSession(settings));
            Assert.AreEqual(0, store.SettingsSaveCount);
            CollectionAssert.AreEqual(
                new[] { 0, 1, 2, 3 },
                GetOrderRows(settings, BoardCategory.Inbox).Select(row => (int)row.Tag).ToArray());

            // 回弹后 Esc 恢复既有关窗语义。
            var escapeAfter = new KeyEventArgs(
                Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(settings)!,
                Environment.TickCount,
                Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
                Source = settings
            };
            settings.RaiseEvent(escapeAfter);
            Assert.IsTrue(escapeAfter.Handled, "回弹后 Esc 应回到既有关窗路径。");
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void DragCommit_RealInputMoveDownSwapsRowsAndPersists()
    {
        using var directory = new TestDirectory();
        var (window, store) = CreateContext(directory, s => s);

        try
        {
            window.Show();
            var settings = OpenSettingsWindow(window);
            CompleteLayout(window);

            var handle = FindHandle(GetOrderRows(settings, BoardCategory.Inbox)[0]);

            // 真实消息直驱拖拽（契约 #13 同源管线，与标签顺序节共用范式）。
            Assert.IsTrue(GetCursorPos(out var savedCursor), "测试环境必须支持读取真实光标。");
            try
            {
                RealInputDragFirstRowToIndex2(settings, handle);
                PumpUntilCompleted(window.Dispatcher).GetAwaiter().GetResult();
                CompleteLayout(window);
            }
            finally
            {
                SetCursorPos(savedCursor.X, savedCursor.Y);
            }

            var expected = new[] { 1, 2, 0, 3 };
            var viewModel = (MainWindowViewModel)window.DataContext;
            CollectionAssert.AreEqual(
                new[] { 0, 1, 2, 3 },
                viewModel.SubgroupChips.Select(chip => chip.Slot).ToArray(),
                "无激活面板时 chip 轨不重排（轨不可见，序随下次激活生效）。");
            Assert.IsNotNull(store.LastSettings);
            CollectionAssert.AreEqual(
                expected,
                store.LastSettings!.SubgroupOrder(BoardCategory.Inbox).ToArray(),
                "拖拽提交必须换序并落 settings.json。");
            CollectionAssert.AreEqual(
                expected,
                GetOrderRows(settings, BoardCategory.Inbox).Select(row => (int)row.Tag).ToArray());
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void DragCommit_WhileSavePending_RailAlreadyShowsTargetOrder()
    {
        // 乐观上屏锁定（镜像标签顺序节测试 A，Issue #153 同源语义）：真实输入拖拽
        // 松手后保存挂起未完成时，行序必须已是目标序（松手同一 UI 消息内重建），
        // 指示线隐藏、无半透明残留；放行保存后行序稳定、持久化序==目标序。
        using var directory = new TestDirectory();
        var (window, store) = CreateContext(directory, s => s, new GatedSettingsBoardStore(directory.Root));

        try
        {
            window.Show();
            var settings = OpenSettingsWindow(window);
            CompleteLayout(window);
            var handle = FindHandle(GetOrderRows(settings, BoardCategory.Inbox)[0]);
            var targetOrder = new[] { 1, 2, 0, 3 };

            store.HoldNextSave();
            try
            {
                Assert.IsTrue(GetCursorPos(out var savedCursor), "测试环境必须支持读取真实光标。");
                try
                {
                    RealInputDragFirstRowToIndex2(settings, handle);
                    PumpUntilCondition(settings.Dispatcher, () => store.SettingsSaveCount == 1);
                }
                finally
                {
                    SetCursorPos(savedCursor.X, savedCursor.Y);
                }

                AssertRowsInOrder(settings, targetOrder, "保存挂起期间行序必须已是目标序（乐观上屏）。");
                Assert.AreEqual(
                    Visibility.Collapsed,
                    ((FrameworkElement)settings.FindName("SubgroupOrderIndicatorInbox")!).Visibility,
                    "保存挂起期间插入指示线必须已隐藏。");
                Assert.IsTrue(
                    GetOrderRows(settings, BoardCategory.Inbox).All(row => row.Opacity == 1),
                    "保存挂起期间不得残留半透明拖起行。");

                store.ReleaseSave();
                PumpUntilCompleted(window.Dispatcher).GetAwaiter().GetResult();
                CompleteLayout(window);

                AssertRowsInOrder(settings, targetOrder, "保存完成后行序必须稳定在目标序（不得二次跳动）。");
                Assert.IsNotNull(store.LastSettings);
                CollectionAssert.AreEqual(
                    targetOrder,
                    store.LastSettings!.SubgroupOrder(BoardCategory.Inbox).ToArray(),
                    "放行后持久化序必须等于目标序。");
            }
            finally
            {
                // 断言失败（红路径）时放行残留挂起：关闭冲刷会等待 pending 提交与
                // 保存信号量，不放行会以清理超时掩盖断言失败。绿路径此调用无副作用。
                store.ReleaseSave();
            }
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void DragCommit_SaveFailureAfterPending_ReboundsRailAndHostOrder()
    {
        // 失败回弹锁定（镜像标签顺序节测试 B）：保存挂起期间行为乐观目标序；放行并
        // 令保存抛 IOException 后——行回弹原序（唯一允许的第二次重建）、宿主
        // CurrentSubgroupOrder 回原序、状态条提示出现。
        using var directory = new TestDirectory();
        var (window, store) = CreateContext(directory, s => s, new GatedSettingsBoardStore(directory.Root));

        try
        {
            window.Show();
            var settings = OpenSettingsWindow(window);
            CompleteLayout(window);
            var handle = FindHandle(GetOrderRows(settings, BoardCategory.Inbox)[0]);
            var originalOrder = new[] { 0, 1, 2, 3 };
            var targetOrder = new[] { 1, 2, 0, 3 };

            store.HoldNextSave();
            try
            {
                Assert.IsTrue(GetCursorPos(out var savedCursor), "测试环境必须支持读取真实光标。");
                try
                {
                    RealInputDragFirstRowToIndex2(settings, handle);
                    PumpUntilCondition(settings.Dispatcher, () => store.SettingsSaveCount == 1);
                }
                finally
                {
                    SetCursorPos(savedCursor.X, savedCursor.Y);
                }

                AssertRowsInOrder(settings, targetOrder, "前置：挂起期间行必须是乐观目标序。");

                store.ReleaseSave(new IOException("模拟 settings.json 写盘失败。"));
                PumpUntilCompleted(window.Dispatcher).GetAwaiter().GetResult();
                CompleteLayout(window);

                AssertRowsInOrder(settings, originalOrder, "保存失败后行必须回弹原序。");
                CollectionAssert.AreEqual(
                    originalOrder,
                    window.CurrentSubgroupOrder(BoardCategory.Inbox).ToArray(),
                    "保存失败后宿主槽序必须回滚原序。");
                Assert.IsTrue(
                    ((MainWindowViewModel)window.DataContext).StatusText.Contains("子分组顺序暂未保存。"),
                    "保存失败必须经状态条提示。");
            }
            finally
            {
                // 断言失败（红路径）时兜底放行并清除失败注入，理由同乐观上屏例。
                store.ReleaseSave();
            }
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void DragCommit_PureClickWithoutMove_DoesNotReorder()
    {
        // 回归锁定（镜像标签顺序节 T7）：落点初值必须等于起点——纯点击把手（无
        // 移动）松手不得换序，也不得写盘。
        using var directory = new TestDirectory();
        var (window, store) = CreateContext(directory, s => s);

        try
        {
            window.Show();
            var settings = OpenSettingsWindow(window);
            CompleteLayout(window);

            var handle = FindHandle(GetOrderRows(settings, BoardCategory.Inbox)[2]);
            handle.RaiseEvent(NewMouseButtonEventArgs(handle, UIElement.MouseLeftButtonDownEvent));
            Assert.IsTrue(handle.IsMouseCaptured);
            handle.RaiseEvent(NewMouseButtonEventArgs(handle, UIElement.MouseLeftButtonUpEvent));
            CompleteLayout(window);

            Assert.IsNull(GetDragSession(settings));
            Assert.AreEqual(0, store.SettingsSaveCount);
            CollectionAssert.AreEqual(
                new[] { 0, 1, 2, 3 },
                GetOrderRows(settings, BoardCategory.Inbox).Select(row => (int)row.Tag).ToArray());
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void OrderRows_FollowLiveThemeSwitchWithoutRebuild()
    {
        // 契约 #21 活切换锁定（镜像）：行画刷经动态资源引用解析——构建后活切主题，
        // 行背景/描边/文字与按钮画刷跟随新字典，无需重开窗口或重建行。
        using var directory = new TestDirectory();
        var (window, store) = CreateContext(directory, s => s);

        try
        {
            window.Show();
            var settings = OpenSettingsWindow(window);
            CompleteLayout(window);
            AssertOrderRowBrushesFollowLiveThemeSwitch(settings);

            // 提交重建行后同样成立（上移触发 PopulateSubgroupOrderSection 重建行）。
            var secondRow = GetOrderRows(settings, BoardCategory.Inbox)[1];
            FindDescendants<Button>(secondRow).Single(b => Equals(b.Content, "↑"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntilCompleted(window.Dispatcher).GetAwaiter().GetResult();
            CompleteLayout(window);
            AssertOrderRowBrushesFollowLiveThemeSwitch(settings);
            _ = store;
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void OrderSection_DisabledWhenSubgroupDisplayOffAndEnabledOnModeChange()
    {
        // 门控锁定（契约 #57 修订，D5）：Off＝整卡置灰非隐藏＋说明行切引导句；
        // 三态下拉切到按钮轨即解灰、说明行复原（即时生效后联动）。
        using var directory = new TestDirectory();
        var (window, store) = CreateContext(
            directory,
            s => s,
            preferences: AppPreferences.Default with { SubgroupDisplay = SubgroupDisplayMode.Off });

        try
        {
            window.Show();
            var settings = OpenSettingsWindow(window);
            CompleteLayout(window);
            var card = (Border)settings.FindName("SubgroupOrderCard")!;
            var note = (TextBlock)settings.FindName("SubgroupOrderNote")!;

            Assert.IsFalse(card.IsEnabled, "Off＝整卡置灰（非隐藏）。");
            Assert.AreEqual(
                0.6,
                card.Opacity,
                "Off＝整卡灰（共享样式禁用态）。");
            Assert.AreEqual(
                Visibility.Visible,
                card.Visibility,
                "置灰非隐藏：行仍渲染（结构在、交互禁）。");
            Assert.AreEqual(4, GetOrderRows(settings, BoardCategory.Inbox).Count);
            Assert.AreEqual(
                "先在 通用 → 实验性 开启「分类内子分组」。",
                note.Text,
                "置灰态说明行切为引导句。");

            var chipItem = SubgroupModeComboBoxItems(settings)
                .Single(item => item.Tag is SubgroupDisplayMode.ChipRail);
            SubgroupModeComboBoxOf(settings).SelectedItem = chipItem;
            CompleteLayout(window);

            Assert.IsTrue(card.IsEnabled, "切到按钮轨即解灰。");
            Assert.AreEqual(
                1.0,
                card.Opacity,
                "切到按钮轨即整卡恢复全不透明（触发器退出）。");
            Assert.AreEqual(
                "调整每个分类内四个分组的显示顺序。",
                note.Text,
                "解灰后说明行复原。");
            _ = store;
        }
        finally
        {
            CloseAll(window);
        }
    }

    /// <summary>共享卡片样式禁用态证据（FTS_DISABLED_CARD_EVIDENCE_DIR 设置时产出
    /// 真实 WPF 渲染截图）：「面板 → 子分组顺序」卡 Off 态整卡灰（浅/深主题）＋
    /// AI 页总开关关＝Key/整理提示词两卡整卡灰（置灰非隐藏，行与说明仍渲染）。</summary>
    [STATestMethod]
    public void Evidence_CaptureDisabledCardScreens()
    {
        var evidenceDirectory = Environment.GetEnvironmentVariable("FTS_DISABLED_CARD_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(evidenceDirectory))
        {
            Assert.Inconclusive("未设置 FTS_DISABLED_CARD_EVIDENCE_DIR，跳过禁用卡片证据产出。");
        }

        using var directory = new TestDirectory();
        var (window, store) = CreateContext(
            directory,
            s => s,
            preferences: AppPreferences.Default with
            {
                SubgroupDisplay = SubgroupDisplayMode.Off,
                AiOrganizeEnabled = false
            });

        try
        {
            window.Show();
            var settings = OpenSettingsWindow(window);
            CompleteLayout(window);

            CaptureSubgroupCardScene(settings, evidenceDirectory!, "subgroup-card-light.png");
            CaptureAiCardsScene(settings, evidenceDirectory!, "ai-cards-light.png");

            settings.ApplyTheme(DesignTheme.Dark);
            CompleteLayout(window);
            CaptureSubgroupCardScene(settings, evidenceDirectory!, "subgroup-card-dark.png");
            _ = store;
        }
        finally
        {
            CloseAll(window);
        }
    }

    /// <summary>导航到「面板」页并把「子分组顺序」卡滚入视野后截整窗（置灰断言
    /// 随证据帧同证）。页切换有 167ms 淡入（AnimatePageIn），切换后先泵过动画
    /// 时长再取帧，否则截到近透明的入场起点。</summary>
    private static void CaptureSubgroupCardScene(
        SettingsWindow settings,
        string directory,
        string fileName)
    {
        ((RadioButton)settings.FindName("SettingsNavPanel")!).IsChecked = true;
        PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(250));
        var card = (Border)settings.FindName("SubgroupOrderCard")!;
        card.BringIntoView();
        CompleteLayout(settings);
        Assert.IsFalse(card.IsEnabled, "Off＝整卡置灰（证据帧同证）。");
        SaveWindowShell(settings, directory, fileName);
    }

    /// <summary>导航到「AI」页并把 Key 卡滚入视野（Key/整理提示词两卡同帧入画）
    /// 后截整窗（总开关关＝两卡置灰随证据帧同证）；切换后先泵过页淡入时长。</summary>
    private static void CaptureAiCardsScene(
        SettingsWindow settings,
        string directory,
        string fileName)
    {
        ((RadioButton)settings.FindName("SettingsNavAi")!).IsChecked = true;
        PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(250));
        ((Border)settings.FindName("AiKeyCard")!).BringIntoView();
        CompleteLayout(settings);
        Assert.IsFalse(
            ((Border)settings.FindName("AiKeyCard")!).IsEnabled,
            "总开关关＝Key 卡置灰（证据帧同证）。");
        Assert.IsFalse(
            ((Border)settings.FindName("AiPromptCard")!).IsEnabled,
            "总开关关＝整理提示词卡置灰（证据帧同证）。");
        SaveWindowShell(settings, directory, fileName);
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
            PixelFormats.Pbgra32);
        bitmap.Render(shell);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, fileName));
        encoder.Save(stream);
    }

    /// <summary>真实消息直驱把首行拖到落点索引 2 后投递抬起消息（镜像标签顺序节
    /// 同源管线）；不等待提交链路排空，由调用方按需泵。</summary>
    private static void RealInputDragFirstRowToIndex2(SettingsWindow settings, FrameworkElement handle)
    {
        // 子分组顺序节在「面板」页第二张卡（下方），先把把手滚进共享视口——
        // 视口外的行收不到真实 Win32 按下消息（标签顺序节为首页卡无此需求）。
        handle.BringIntoView();
        CompleteLayout(settings);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(settings).Handle;
        var screen = handle.PointToScreen(
            new Point(handle.ActualWidth / 2, handle.ActualHeight / 2));

        Assert.IsTrue(SetCursorPos((int)Math.Round(screen.X) + 7, (int)Math.Round(screen.Y) + 5));
        Assert.IsTrue(SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y)));
        PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(120));

        var downClient = new NativePoint((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
        Assert.IsTrue(ScreenToClient(hwnd, ref downClient));
        Assert.IsTrue(PostMessage(
            hwnd,
            WmLeftButtonDown,
            MkLbutton,
            MakeLParam(downClient.X, downClient.Y)),
            "投递按下消息失败。");
        PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(120));
        Assert.IsTrue(handle.IsMouseCaptured, "真实按下后把手必须捕获鼠标进入拖拽会话。");

        var moveDownPhysical = (int)Math.Round(
            handle.PointToScreen(new Point(0, 75)).Y -
            handle.PointToScreen(new Point(0, 0)).Y);
        Assert.IsTrue(SetCursorPos((int)Math.Round(screen.X) + 7, (int)Math.Round(screen.Y) + 5));
        Assert.IsTrue(SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y) + moveDownPhysical));
        PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(150));
        Assert.AreEqual(2, GetSessionTargetIndex(settings), "真实移动必须驱动落点指示到第 2 行带。");

        var upClient = new NativePoint((int)Math.Round(screen.X), (int)Math.Round(screen.Y) + moveDownPhysical);
        Assert.IsTrue(ScreenToClient(hwnd, ref upClient));
        Assert.IsTrue(PostMessage(hwnd, WmLeftButtonUp, 0, MakeLParam(upClient.X, upClient.Y)));
    }

    private static void AssertRowsInOrder(
        SettingsWindow settings,
        IReadOnlyList<int> expected,
        string message) =>
        CollectionAssert.AreEqual(
            expected.ToArray(),
            GetOrderRows(settings, BoardCategory.Inbox).Select(row => (int)row.Tag).ToArray(),
            message);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    private const int WmLeftButtonDown = 0x0201;
    private const int WmLeftButtonUp = 0x0202;
    private const int MkLbutton = 0x0001;

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hWnd, int msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(nint hWnd, ref NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X;
        public int Y;
    }

    private static nint MakeLParam(int low, int high) =>
        (nint)((high << 16) | (low & 0xFFFF));

    private static MouseButtonEventArgs NewMouseButtonEventArgs(
        FrameworkElement source,
        RoutedEvent routedEvent) =>
        new(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = routedEvent,
            Source = source
        };

    private static List<Border> GetOrderRows(SettingsWindow settings, BoardCategory category)
    {
        var list = (StackPanel)settings.FindName($"SubgroupOrderList{category}")!;
        Assert.IsTrue(list.Children.Count == 4, "顺序列表必须恰好四行。");
        return list.Children.Cast<Border>().ToList();
    }

    private static TextBlock GetSectionTitle(SettingsWindow settings, BoardCategory category) =>
        (TextBlock)settings.FindName($"SubgroupOrderTitle{category}")!;

    private static ComboBox SubgroupModeComboBoxOf(SettingsWindow settings) =>
        (ComboBox)settings.FindName("SubgroupModeComboBox")!;

    private static List<ComboBoxItem> SubgroupModeComboBoxItems(SettingsWindow settings) =>
        SubgroupModeComboBoxOf(settings).Items.Cast<ComboBoxItem>().ToList();

    private static FrameworkElement FindHandle(Border row) =>
        FindDescendants<FrameworkElement>(row)
            .First(element => AutomationProperties.GetName(element)!.StartsWith("拖动"));

    private static object? GetDragSession(SettingsWindow settings)
    {
        var field = typeof(SettingsWindow).GetField(
            "_subgroupOrderDrag",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        return field.GetValue(settings);
    }

    private static int GetSessionTargetIndex(SettingsWindow settings)
    {
        var session = GetDragSession(settings);
        return session is null ? -1 : (int)session.GetType().GetProperty("TargetIndex")!.GetValue(session)!;
    }

    private static async Task PumpUntilCompleted(Dispatcher dispatcher)
    {
        // 让 fire-and-forget 的 async void 提交链路（含宿主保存与采纳）排空。
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(50);
            done.TrySetResult();
        });
        PumpUntil(dispatcher, done.Task);
    }

    /// <summary>对首行断言两组主题的画刷跟随：与 FindResource 结果同例，且两主题实例互异。</summary>
    private static void AssertOrderRowBrushesFollowLiveThemeSwitch(SettingsWindow settings)
    {
        var row = GetOrderRows(settings, BoardCategory.Inbox)[0];
        var label = FindDescendants<TextBlock>(row)
            .Single(t => AutomationProperties.GetName(t)!.StartsWith("分组"));
        var handleGlyph = FindDescendants<TextBlock>(row).Single(t => t.Text == "⋮⋮");
        var moveUp = FindDescendants<Button>(row).Single(b => Equals(b.Content, "↑"));

        var initial = CurrentThemeIsDark(settings) ? DesignTheme.Dark : DesignTheme.Light;
        var other = initial == DesignTheme.Dark ? DesignTheme.Light : DesignTheme.Dark;

        DesignThemeManager.Apply(settings, other);
        CompleteLayout(settings);
        var otherCard = (Brush)settings.FindResource("CardBrush");
        var otherBorder = (Brush)settings.FindResource("BorderBrush");
        var otherPrimary = (Brush)settings.FindResource("PrimaryTextBrush");
        var otherSecondary = (Brush)settings.FindResource("SecondaryTextBrush");
        Assert.AreSame(otherCard, row.Background, $"切到 {other} 后行背景必须解析到新主题 CardBrush。");
        Assert.AreSame(otherBorder, row.BorderBrush, $"切到 {other} 后行描边必须解析到新主题 BorderBrush。");
        Assert.AreSame(otherPrimary, label.Foreground, $"切到 {other} 后分组名必须解析到新主题 PrimaryTextBrush。");
        Assert.AreSame(otherSecondary, handleGlyph.Foreground, $"切到 {other} 后拖拽把手必须解析到新主题 SecondaryTextBrush。");
        Assert.AreSame(
            otherPrimary,
            moveUp.Foreground,
            $"切到 {other} 后上移按钮前景必须经 ThemedActionButtonStyle 的 DynamicResource 跟随。");

        DesignThemeManager.Apply(settings, initial);
        CompleteLayout(settings);
        var initialCard = (Brush)settings.FindResource("CardBrush");
        var initialBorder = (Brush)settings.FindResource("BorderBrush");
        var initialPrimary = (Brush)settings.FindResource("PrimaryTextBrush");
        var initialSecondary = (Brush)settings.FindResource("SecondaryTextBrush");
        Assert.AreNotSame(otherCard, initialCard, "两主题字典的画刷必须是不同实例（前置 sanity）。");
        Assert.AreSame(initialCard, row.Background, $"切回 {initial} 后行背景必须跟随。");
        Assert.AreSame(initialBorder, row.BorderBrush);
        Assert.AreSame(initialPrimary, label.Foreground);
        Assert.AreSame(initialSecondary, handleGlyph.Foreground);
        Assert.AreSame(initialPrimary, moveUp.Foreground);
    }

    private static bool CurrentThemeIsDark(Window window) =>
        (window.Resources.MergedDictionaries[1].Source?.OriginalString ?? string.Empty)
            .Replace('\\', '/')
            .EndsWith("Resources/DesignTheme.Dark.xaml", StringComparison.Ordinal);

    /// <summary>等待真实光标操作被 WPF 处理（WM_MOUSEMOVE 结算）。</summary>
    private static void PumpFor(Dispatcher dispatcher, TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = duration
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>泵 Dispatcher 直至条件成立或超时：用于等待真实输入消息链推进到
    /// 确定性状态（如保存链路已进入挂起），避免固定时长的竞态。</summary>
    private static void PumpUntilCondition(Dispatcher dispatcher, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            PumpFor(dispatcher, TimeSpan.FromMilliseconds(20));
        }

        Assert.IsTrue(condition(), "等待条件未在超时内满足。");
    }

    private static void PumpUntil(Dispatcher dispatcher, Task task)
    {
        if (task.IsCompleted)
        {
            task.GetAwaiter().GetResult();
            return;
        }

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

        task.GetAwaiter().GetResult();
    }

    private static (MainWindow Window, GatedSettingsBoardStore Store) CreateContext(
        TestDirectory directory,
        Func<WindowSettings, WindowSettings> configureSettings,
        GatedSettingsBoardStore? store = null,
        AppPreferences? preferences = null)
    {
        store ??= new GatedSettingsBoardStore(directory.Root);
        // 默认开按钮轨（门控开＝节可交互；置灰路径由门控例显式传 Off 覆盖）。
        preferences ??= AppPreferences.Default with { SubgroupDisplay = SubgroupDisplayMode.ChipRail };
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
        var settings = configureSettings(
            WindowSettings.Default with
            {
                ReviewMigrationVersion = DailyReviewMigration.CurrentVersion
            });
        window = new MainWindow(
            board,
            store,
            settings,
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
            dataDirectory: directory.Root,
            preferences: preferences);
        return (window, store);
    }

    private static SettingsWindow OpenSettingsWindow(MainWindow window)
    {
        var gear = window.FindName("SettingsButton") as Button;
        Assert.IsNotNull(gear);
        gear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var settings = GetPrivateField<SettingsWindow>(window, "_settingsWindow");
        Assert.IsNotNull(settings);
        // 双栏布局（契约 #24）：子分组顺序节位于「面板」页，拖拽与可见性断言须先导航。
        var panelNav = settings.FindName("SettingsNavPanel") as RadioButton;
        Assert.IsNotNull(panelNav, "设置窗必须有「面板」导航项。");
        panelNav.IsChecked = true;
        return settings;
    }

    private static T GetPrivateField<T>(MainWindow window, string fieldName)
    {
        var field = typeof(MainWindow).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        return (T)field.GetValue(window)!;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
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

    private static void CloseAll(MainWindow window)
    {
        (typeof(MainWindow).GetField("_settingsWindow", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window) as SettingsWindow)?.Close();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Close();
        PumpUntil(window.Dispatcher, closed.Task);
    }

    private sealed class IdleClipboardReader : IClipboardReader
    {
        public Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClipboardSnapshot(0, null, [], null));
    }
}
