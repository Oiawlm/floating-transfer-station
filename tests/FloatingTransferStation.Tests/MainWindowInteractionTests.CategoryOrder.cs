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
}
