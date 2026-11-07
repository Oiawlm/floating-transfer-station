using System.IO;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 子分组槽序消费面锁定（契约 #57 修订，照 <see cref="MainWindowInteractionTests.CategoryOrder"/>
/// 族镜像）：chip 轨按每分类槽序渲染、切分类换序；宿主采纳即重排并落 settings.json
/// （契约 #5 即时生效）；乐观提交契约——同步前缀首个 await 前生效（保存挂起期间
/// 已是目标序）、保存失败 _settings 与 VM 双回滚、旧提交失败不踩新提交且字段级
/// 回滚保留其他设置突变。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    /// <summary>槽序族建窗：显式 store（LocalStore 持久化断言／GatedSettingsBoardStore
    /// 门控断言）＋ChipRail 模式（轨可见）。</summary>
    private static MainWindow CreateSubgroupOrderWindow(
        TestDirectory directory,
        BoardService board,
        IBoardStore store) => CreateWindow(
        board,
        store,
        WindowSettings.Default,
        new DefaultCaptureCategoryState(),
        new ImageNormalizer(store.ImagesDirectory),
        dataDirectory: directory.Root,
        preferences: AppPreferences.Default with { SubgroupDisplay = SubgroupDisplayMode.ChipRail });

    [STATestMethod]
    public void SubgroupRail_FollowsCustomDisplayOrderPerCategory()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("内容", BoardCategory.Inbox);
        var settings = WindowSettings.Default
            .WithSubgroupName(BoardCategory.Inbox, 1, "急件")
            .WithSubgroupOrder(BoardCategory.Inbox, [3, 2, 1, 0])
            .WithSubgroupOrder(BoardCategory.CustomerOriginal, [1, 0, 2, 3]);
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.ChipRail, settings);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            CollectionAssert.AreEqual(
                new[] { 3, 2, 1, 0 },
                viewModel.SubgroupChips.Select(chip => chip.Slot).ToArray(),
                "chip 轨必须按该分类的自定义槽序渲染。");
            Assert.AreEqual(
                "急件",
                viewModel.SubgroupChips.Single(chip => chip.Slot == 1).DisplayName,
                "显示名随槽走（按槽寻位，不按轨位）。");

            // 切分类换序：重要的清单独立生效；切回待分类恢复原序。
            ExpandCategory(window, BoardCategory.CustomerOriginal);
            CompleteLayout(window);
            CollectionAssert.AreEqual(
                new[] { 1, 0, 2, 3 },
                viewModel.SubgroupChips.Select(chip => chip.Slot).ToArray(),
                "切分类必须换用该分类的槽序。");
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            CollectionAssert.AreEqual(
                new[] { 3, 2, 1, 0 },
                viewModel.SubgroupChips.Select(chip => chip.Slot).ToArray(),
                "切回原分类必须恢复该分类槽序。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ApplySubgroupOrderAsync_RerailsChipsAndPersistsSettings()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        using var store = new LocalStore(paths, new AtomicTextWriter());
        var board = new BoardService();
        board.AddText("内容", BoardCategory.Inbox);
        var window = CreateSubgroupOrderWindow(directory, board, store);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var chipsBefore = viewModel.SubgroupChips.ToArray();
            var newOrder = new[] { 2, 0, 3, 1 };

            var applied = window.Dispatcher.InvokeAsync(
                () => window.ApplySubgroupOrderAsync(BoardCategory.Inbox, newOrder)).Task.Unwrap();
            PumpDispatcherUntil(window.Dispatcher, applied);
            CompleteLayout(window);

            CollectionAssert.AreEqual(
                newOrder,
                viewModel.SubgroupChips.Select(chip => chip.Slot).ToArray(),
                "宿主采纳必须立即重排 chip 轨（激活分类）。");
            CollectionAssert.AreEquivalent(
                chipsBefore,
                viewModel.SubgroupChips.ToArray(),
                "重排必须复用既有 SubgroupChipViewModel 实例。");

            // 同步阻塞取持久化结果：await 续体会掉到工作线程，测试清理必须留在
            // STA 线程（与既有用例 LoadAsync().GetAwaiter().GetResult() 同型）。
            var persisted = store.LoadSettingsAsync().GetAwaiter().GetResult();
            CollectionAssert.AreEqual(
                newOrder,
                persisted.SubgroupOrder(BoardCategory.Inbox).ToArray(),
                "子分组槽序必须随 settings.json 原子持久化。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void ApplySubgroupOrderAsync_SyncPrefixAppliesOrderAndFailureRollsBackBoth()
    {
        // 宿主乐观契约锁定（镜像标签顺序测试 C）：调用返回（同步前缀执行完）即已
        // 应用槽序与内存设置基准——不等待持久化完成（保存被门挂起时已生效）；保存
        // 失败 _settings 与 VM 双回滚并经状态条提示。
        using var directory = new TestDirectory();
        var store = new GatedSettingsBoardStore(directory.Root);
        var board = new BoardService();
        var window = CreateSubgroupOrderWindow(directory, board, store);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var newOrder = new[] { 3, 1, 0, 2 };

            // 经 Dispatcher 发起（提交链路捕获调度器上下文，与既有宿主用例同型）；
            // InvokeAsync 任务完成＝委托返回＝同步前缀已执行，而保存仍被门挂起。
            store.HoldNextSave();
            var invoke = window.Dispatcher.InvokeAsync(
                () => window.ApplySubgroupOrderAsync(BoardCategory.Inbox, newOrder));
            PumpDispatcherUntil(window.Dispatcher, invoke.Task);
            var apply = invoke.Result;
            CollectionAssert.AreEqual(
                newOrder,
                window.CurrentSubgroupOrder(BoardCategory.Inbox).ToArray(),
                "调用返回即必须已同步应用槽序（首个 await 前生效，保存仍挂起）。");
            CollectionAssert.AreEqual(
                newOrder,
                ReadHostSettings(window).SubgroupOrder(BoardCategory.Inbox).ToArray(),
                "同步前缀必须一并更新内存设置基准（持久化前）。");

            store.ReleaseSave();
            PumpDispatcherUntil(window.Dispatcher, apply);

            var failureOrder = new[] { 0, 3, 2, 1 };
            store.HoldNextSave();
            var failingInvoke = window.Dispatcher.InvokeAsync(
                () => window.ApplySubgroupOrderAsync(BoardCategory.Inbox, failureOrder));
            PumpDispatcherUntil(window.Dispatcher, failingInvoke.Task);
            CollectionAssert.AreEqual(
                failureOrder,
                window.CurrentSubgroupOrder(BoardCategory.Inbox).ToArray(),
                "前置：第二次提交同样同步生效。");
            var failing = failingInvoke.Result;
            store.ReleaseSave(new IOException("模拟 settings.json 写盘失败。"));
            PumpDispatcherUntil(window.Dispatcher, failing);

            CollectionAssert.AreEqual(
                newOrder,
                window.CurrentSubgroupOrder(BoardCategory.Inbox).ToArray(),
                "保存失败必须回滚槽序到上一成功序。");
            CollectionAssert.AreEqual(
                newOrder,
                ReadHostSettings(window).SubgroupOrder(BoardCategory.Inbox).ToArray(),
                "保存失败必须回滚内存设置基准（双回滚）。");
            Assert.IsTrue(
                ((MainWindowViewModel)window.DataContext).StatusText.Contains("子分组顺序暂未保存。"),
                "保存失败必须经状态条提示。");
        }
        finally
        {
            store.ReleaseSave();
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void ApplySubgroupOrderAsync_OlderFailureDoesNotStompNewerCommit()
    {
        // 回滚守卫＋字段级回滚锁定（镜像标签顺序测试 D）：commit1 保存挂起期间
        // commit2 同步生效（门后排队）且发生不经保存信号量的设置突变（模拟宽高
        // 拖拽直改内存设置）→ commit1 失败落地——守卫拦截 VM 回滚（槽序保持
        // commit2 序，旧失败不得踩新提交），字段级回滚仅回写 SubgroupOrders（突变
        // 保留）→ commit2 随后保存成功，内存/显示/持久化三方一致。
        using var directory = new TestDirectory();
        var store = new GatedSettingsBoardStore(directory.Root);
        var board = new BoardService();
        var window = CreateSubgroupOrderWindow(directory, board, store);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var firstCommitOrder = new[] { 3, 1, 0, 2 };
            var secondCommitOrder = new[] { 2, 0, 3, 1 };

            store.HoldNextSave();
            var firstInvoke = window.Dispatcher.InvokeAsync(
                () => window.ApplySubgroupOrderAsync(BoardCategory.Inbox, firstCommitOrder));
            PumpDispatcherUntil(window.Dispatcher, firstInvoke.Task);
            Assert.AreEqual(1, store.SettingsSaveCount, "前置：commit1 已进入保存并被门挂起。");

            var secondInvoke = window.Dispatcher.InvokeAsync(
                () => window.ApplySubgroupOrderAsync(BoardCategory.Inbox, secondCommitOrder));
            PumpDispatcherUntil(window.Dispatcher, secondInvoke.Task);
            var second = secondInvoke.Result;
            CollectionAssert.AreEqual(
                secondCommitOrder,
                window.CurrentSubgroupOrder(BoardCategory.Inbox).ToArray(),
                "前置：commit2 乐观前缀已生效（commit1 仍挂起）。");

            // 模拟保存挂起期间不经信号量的设置突变（宽高拖拽直改内存设置）。
            var mutated = ReadHostSettings(window) with { PanelWidth = 512 };
            WriteHostSettings(window, mutated);

            store.ReleaseSave(new IOException("模拟 settings.json 写盘失败。"));
            PumpDispatcherUntil(window.Dispatcher, second);

            CollectionAssert.AreEqual(
                secondCommitOrder,
                window.CurrentSubgroupOrder(BoardCategory.Inbox).ToArray(),
                "commit1 失败必须被守卫拦截，槽序保持 commit2 序。");
            var finalSettings = ReadHostSettings(window);
            CollectionAssert.AreEqual(
                secondCommitOrder,
                finalSettings.SubgroupOrder(BoardCategory.Inbox).ToArray(),
                "commit2 随后保存成功，内存设置基准必须是 commit2 序。");
            Assert.AreEqual(
                512d,
                finalSettings.PanelWidth,
                "字段级回滚必须保留保存挂起期间的其他设置突变。");
            Assert.IsNotNull(store.LastSettings);
            CollectionAssert.AreEqual(
                secondCommitOrder,
                store.LastSettings!.SubgroupOrder(BoardCategory.Inbox).ToArray(),
                "持久化终态必须是 commit2 序。");
            Assert.AreEqual(512d, store.LastSettings.PanelWidth, "持久化终态必须携带挂起期间的突变。");
        }
        finally
        {
            store.ReleaseSave();
            CloseWindowWithoutSaving(window);
        }
    }
}
