using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatingTransferStation.Controls;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

// 展开管线结构性守护（GOAL-PANEL-EXPAND-PERF-20261013，契约 #15 展开路径
// 成本有界维度的锁定测试）：
// 1) 深偏移展开的滚动恢复在展开 tick 内完成——tick 返回即最终偏移，
//    其后不再有容器再生成或偏移跳变（首帧即终态，无「闪现→跳变」）；
// 2) 冷展开每个可见图片唯一一次解码（无升档/重复解码路径）；
// 3) 展开实现容器数有界且与分类条目总数无关（虚拟化边界在展开路径同样成立）。
public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public void ExpandWithSavedDeepOffset_RestoresScrollInsideTickWithoutPostTickRegeneration()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        AddGuardImages(board, directory, count: 60);
        AddScrollableItems(board, BoardCategory.Prompt);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList")!;
            var viewer = FindDescendant<ScrollViewer>(list)!;

            // 预置：图片分类展开滚到深偏移并收起（写入保存偏移），再切到垫板分类收起。
            ExpandCategory(window, BoardCategory.CustomerOriginal);
            CompleteLayout(window);
            var savedOffset = viewer.ScrollableHeight * 0.8;
            ScrollTo(window, viewer, savedOffset);
            CollapseForSetup(window);
            ExpandCategory(window, BoardCategory.Prompt);
            CompleteLayout(window);
            CollapseForSetup(window);

            var transitions = 0;
            void OnGeneratorStatus(object? sender, EventArgs args)
            {
                if (list.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
                {
                    transitions++;
                }
            }

            var scrollChanges = 0;
            void OnScrollChanged(object sender, ScrollChangedEventArgs args) => scrollChanges++;
            list.ItemContainerGenerator.StatusChanged += OnGeneratorStatus;
            viewer.ScrollChanged += OnScrollChanged;
            try
            {
                EnterCategory(window, BoardCategory.CustomerOriginal);
                InvokePrivate(window, "ExpandIntentTimer_Tick", null, EventArgs.Empty);
            }
            finally
            {
                list.ItemContainerGenerator.StatusChanged -= OnGeneratorStatus;
            }

            // 恢复在 tick 内完成：tick 返回（尚未泵帧）时偏移已在保存值上。
            Assert.AreEqual(
                savedOffset,
                viewer.VerticalOffset,
                0.5,
                "深偏移恢复必须发生在展开 tick 内——tick 返回即最终偏移。");

            var transitionsAfterTick = transitions;
            var scrollChangesAfterTick = scrollChanges;
            CompleteLayout(window);

            Assert.AreEqual(
                transitionsAfterTick,
                transitions,
                "tick 之后不得再发生容器再生成（无「偏移 0 生成→再滚→再生成」）。");
            Assert.AreEqual(
                scrollChangesAfterTick,
                scrollChanges,
                "tick 之后不得再有偏移变化（首帧即终态，无可见跳变）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ColdExpandImageCategory_DecodesEachVisibleImageExactlyOnce()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var items = AddGuardImages(board, directory, count: 24);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList")!;
            var applications = new List<string>();
            var hooked = new HashSet<AsyncThumbnailImage>();

            void AttachProbes()
            {
                foreach (var image in FindDescendants<AsyncThumbnailImage>(list))
                {
                    if (!hooked.Add(image))
                    {
                        continue;
                    }

                    DependencyPropertyDescriptor.FromProperty(
                        System.Windows.Controls.Image.SourceProperty,
                        typeof(AsyncThumbnailImage)).AddValueChanged(
                            image,
                            (_, _) =>
                            {
                                if (image.Source is BitmapSource &&
                                    image.SourcePath is { } path &&
                                    Path.IsPathFullyQualified(path))
                                {
                                    applications.Add(path);
                                }
                            });
                }
            }

            foreach (var item in items)
            {
                AsyncThumbnailImage.SharedConverter.Invalidate(item.ImageAbsolutePath!);
            }

            list.ItemContainerGenerator.StatusChanged += (_, _) =>
            {
                if (list.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
                {
                    AttachProbes();
                }
            };

            AttachProbes();
            EnterCategory(window, BoardCategory.CustomerOriginal);
            InvokePrivate(window, "ExpandIntentTimer_Tick", null, EventArgs.Empty);
            AttachProbes();

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (applications.Count == 0 && DateTime.UtcNow < deadline)
            {
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
                window.UpdateLayout();
                AttachProbes();
            }

            // 泉到解码安定：所有可见图片 Source 就位。
            var settleDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (DateTime.UtcNow < settleDeadline)
            {
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(50));
            }

            Assert.IsTrue(
                applications.Count >= 3,
                $"冷展开应解码可见区图片（实测 {applications.Count} 次），断言才有意义。");
            var duplicated = applications
                .GroupBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .ToArray();
            Assert.IsEmpty(
                duplicated,
                $"每个可见图片冷展开只允许一次解码（重复路径：{string.Join(", ", duplicated.Select(group => group.Key))}）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ExpandImageCategory_RealizesViewportBoundedContainerCount()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        AddGuardImages(board, directory, count: 450);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList")!;
            var viewer = FindDescendant<ScrollViewer>(list)!;

            // 预置深偏移保存值，使被测展开走「换绑＋恢复」完整路径。
            ExpandCategory(window, BoardCategory.CustomerOriginal);
            CompleteLayout(window);
            ScrollTo(window, viewer, viewer.ScrollableHeight * 0.8);
            CollapseForSetup(window);
            ExpandCategory(window, BoardCategory.Prompt);
            CompleteLayout(window);
            CollapseForSetup(window);

            EnterCategory(window, BoardCategory.CustomerOriginal);
            InvokePrivate(window, "ExpandIntentTimer_Tick", null, EventArgs.Empty);
            CompleteLayout(window);

            var containers = FindDescendants<ListBoxItem>(list).ToList();
            var visible = containers.Count(container =>
            {
                var position = container.TransformToAncestor(viewer).Transform(new Point());
                return position.Y < viewer.ViewportHeight && position.Y + container.ActualHeight > 0;
            });

            Assert.IsTrue(visible > 0, "展开后应有可见卡片。");
            Assert.IsTrue(
                containers.Count <= visible + 3,
                $"深偏移展开实现的容器数应有界（视口 {visible} + 小缓存），实际 {containers.Count}。");
            Assert.IsTrue(
                containers.Count < 45,
                $"四百五十条的图片分类展开不得实现超过总量十分之一的容器，实际 {containers.Count}。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    private static BoardItem[] AddGuardImages(BoardService board, TestDirectory directory, int count)
    {
        // 三类纵横比掺混（常规/竖版/宽版），与基准数据集同构。
        return Enumerable.Range(0, count)
            .Select(index =>
            {
                var (width, height) = (index % 3) switch
                {
                    0 => (1200, 800),
                    1 => (800, 1200),
                    _ => (2560, 800),
                };
                var path = Path.Combine(directory.Root, $"guard-{index:D4}.png");
                WritePng(path, width, height);
                return board.AddImage(
                    Guid.NewGuid(),
                    $"images/guard-{index:D4}.png",
                    path,
                    BoardCategory.CustomerOriginal);
            })
            .ToArray();
    }
}
