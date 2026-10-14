using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 标签显示顺序（1.25.0）消费面锁定：面板标签轨与收起把手几何按显示顺序
/// （settings.DisplayOrder）渲染，不再假设目录默认序；重排只复用既有
/// CategoryViewModel 实例；宿主采纳即重排并落 settings.json（契约 #5 即时生效）。
/// 乐观提交契约（2026-10-14 修订，Issue #153）：宿主同步前缀在首个 await 前生效，
/// 保存失败 _settings 与 VM 双回滚。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public void CategoryRail_FollowsCustomDisplayOrderAndCollapsedRow()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        using var store = new LocalStore(paths, new AtomicTextWriter());
        var board = new BoardService();
        var customOrder = new[]
        {
            BoardCategory.Inbox,
            BoardCategory.CustomerOriginal,
            BoardCategory.Prompt,
            BoardCategory.Reference
        };
        var settings = WindowSettings.Default.WithCategoryOrder(customOrder);
        var capture = new DefaultCaptureCategoryState();
        capture.Set(BoardCategory.Prompt);
        var window = CreateWindow(board, store, settings, capture);

        try
        {
            window.Show();
            CompleteLayout(window);

            // 收起把手行号 = 默认接收分类在显示顺序中的位置（此处 Prompt=第 2 行）。
            var expectedRow = customOrder.IndexOf(BoardCategory.Prompt);
            Assert.AreEqual(
                SystemParameters.WorkArea.Top + WindowSettings.Default.Top + (expectedRow * 160),
                window.Top,
                0.5,
                "收起把手必须落在显示顺序对应的行。");

            ExpandCategory(window, BoardCategory.Prompt);
            CompleteLayout(window);

            var rail = FindDescendant<ItemsControl>(
                (Border)window.FindName("CategoryRail"))!;
            var renderedOrder = rail.Items.Cast<CategoryViewModel>()
                .Select(panel => panel.Category)
                .ToArray();
            CollectionAssert.AreEqual(customOrder, renderedOrder, "标签轨必须按显示顺序渲染。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void ApplyCategoryOrderAsync_RerailsPanelAndPersistsSettings()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        using var store = new LocalStore(paths, new AtomicTextWriter());
        var board = new BoardService();
        board.AddText("内容", BoardCategory.Inbox);
        var window = CreateWindow(board, store, WindowSettings.Default, new DefaultCaptureCategoryState());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var panelsBefore = viewModel.Categories.ToArray();
            var newOrder = new[]
            {
                BoardCategory.Reference,
                BoardCategory.Prompt,
                BoardCategory.CustomerOriginal,
                BoardCategory.Inbox
            };

            var applied = window.Dispatcher.InvokeAsync(
                () => window.ApplyCategoryOrderAsync(newOrder)).Task.Unwrap();
            PumpDispatcherUntil(window.Dispatcher, applied);
            CompleteLayout(window);

            CollectionAssert.AreEqual(
                newOrder,
                viewModel.Categories.Select(panel => panel.Category).ToArray(),
                "宿主采纳必须立即重排面板标签轨。");
            CollectionAssert.AreEquivalent(
                panelsBefore,
                viewModel.Categories.ToArray(),
                "重排必须复用既有 CategoryViewModel 实例。");

            // 同步阻塞取持久化结果：await 续体会掉到工作线程，测试清理必须留在
            // STA 线程（与既有用例 LoadAsync().GetAwaiter().GetResult() 同型）。
            var persisted = store.LoadSettingsAsync().GetAwaiter().GetResult();
            CollectionAssert.AreEqual(
                newOrder,
                persisted.DisplayOrder.ToArray(),
                "标签顺序必须随 settings.json 原子持久化。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void ApplyCategoryOrderAsync_SyncPrefixAppliesOrderAndFailureRollsBackBoth()
    {
        // 测试 C（宿主乐观契约锁定，Issue #153）：调用返回（同步前缀执行完）即已
        // 应用显示顺序与内存设置基准——不等待持久化完成（保存被门挂起时已生效）；
        // 保存失败 _settings 与 VM 双回滚并经状态条提示。锁定防将来重构无声回退
        // 同步前缀（同步前缀断裂＝乐观上屏回归为旧序帧）。
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new GatedSettingsBoardStore(directory.Root);
        var board = new BoardService();
        var capture = new DefaultCaptureCategoryState();
        capture.Set(BoardCategory.Prompt);
        var window = CreateWindow(board, store, WindowSettings.Default, capture);

        try
        {
            window.Show();
            CompleteLayout(window);
            var newOrder = new[]
            {
                BoardCategory.Reference,
                BoardCategory.Prompt,
                BoardCategory.CustomerOriginal,
                BoardCategory.Inbox
            };

            // 经 Dispatcher 发起（提交链路捕获调度器上下文，与既有宿主用例同型）；
            // InvokeAsync 任务完成＝委托返回＝同步前缀已执行，而保存仍被门挂起。
            store.HoldNextSave();
            var invoke = window.Dispatcher.InvokeAsync(() => window.ApplyCategoryOrderAsync(newOrder));
            PumpDispatcherUntil(window.Dispatcher, invoke.Task);
            var apply = invoke.Result;
            CollectionAssert.AreEqual(
                newOrder,
                window.CurrentDisplayOrder.ToArray(),
                "调用返回即必须已同步应用显示顺序（首个 await 前生效，保存仍挂起）。");
            CollectionAssert.AreEqual(
                newOrder,
                ReadHostSettings(window).DisplayOrder.ToArray(),
                "同步前缀必须一并更新内存设置基准（持久化前）。");

            store.ReleaseSave();
            PumpDispatcherUntil(window.Dispatcher, apply);

            var failureOrder = new[]
            {
                BoardCategory.Inbox,
                BoardCategory.Reference,
                BoardCategory.Prompt,
                BoardCategory.CustomerOriginal
            };
            store.HoldNextSave();
            var failingInvoke = window.Dispatcher.InvokeAsync(() => window.ApplyCategoryOrderAsync(failureOrder));
            PumpDispatcherUntil(window.Dispatcher, failingInvoke.Task);
            CollectionAssert.AreEqual(
                failureOrder,
                window.CurrentDisplayOrder.ToArray(),
                "前置：第二次提交同样同步生效。");
            var failing = failingInvoke.Result;
            store.ReleaseSave(new IOException("模拟 settings.json 写盘失败。"));
            PumpDispatcherUntil(window.Dispatcher, failing);

            CollectionAssert.AreEqual(
                newOrder,
                window.CurrentDisplayOrder.ToArray(),
                "保存失败必须回滚显示顺序到上一成功序。");
            CollectionAssert.AreEqual(
                newOrder,
                ReadHostSettings(window).DisplayOrder.ToArray(),
                "保存失败必须回滚内存设置基准（双回滚）。");
            Assert.IsTrue(
                ((MainWindowViewModel)window.DataContext).StatusText.Contains("标签顺序暂未保存。"),
                "保存失败必须经状态条提示。");
        }
        finally
        {
            store.ReleaseSave();
            CloseWindowWithoutSaving(window);
        }
    }

    private static WindowSettings ReadHostSettings(MainWindow window) =>
        (WindowSettings)typeof(MainWindow)
            .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;
}
