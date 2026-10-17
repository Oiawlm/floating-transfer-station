using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

// 内容卡片 DragOver 成本体检（GOAL-LAUNCHER-UX-PERF-20261016 F8）：板内批量拖拽
// 悬停期间每事件 DragOver 处理耗时（P50/P95）。只输出可复跑测量行（PERF| 前缀），
// 不设毫秒断言；「收敛或不动」的结论以数字为准（诚实数字铁律）。
public sealed partial class MainWindowInteractionTests
{
    private const int BoardDragOverPerfItemCount = 200;
    private const int BoardDragOverPerfSamples = 240;

    [STATestMethod]
    public void BoardDragOverPerf_InternalBatchHoverBenchmark()
    {
        if (Environment.GetEnvironmentVariable("CI") == "true")
        {
            Assert.Inconclusive("CI 共享 runner 跳过卡片 DragOver 基准（本机测量工具）。");
        }

        using var directory = new TestDirectory();
        var board = new BoardService();
        var dragged = board.AddText("被拖文字卡甲", BoardCategory.Inbox);
        board.AddText("被拖文字卡乙", BoardCategory.Inbox);
        for (var index = 0; index < BoardDragOverPerfItemCount; index++)
        {
            board.AddText($"待分类条目 {index}", BoardCategory.Inbox);
        }

        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var window = CreateWindow(
            board,
            store,
            WindowSettings.Default,
            null,
            new ImageNormalizer(store.ImagesDirectory),
            dataDirectory: paths.DataDirectory);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            var list = (ListBox)window.FindName("BoardList")!;
            var batchItems = board.Items(BoardCategory.Inbox).Take(2).ToList();
            var data = new DragPayloadService().BuildInternalBatch(batchItems);
            var enter = NewDragEventArgs(data, DragDrop.PreviewDragEnterEvent, list);
            list.RaiseEvent(enter);
            CompleteLayout(window);

            // 预热一轮再测：首事件带 LINQ/集合冷启动成本。
            for (var warmup = 0; warmup < 20; warmup++)
            {
                list.RaiseEvent(NewDragEventArgs(
                    data,
                    DragDrop.PreviewDragOverEvent,
                    list,
                    new Point(160, 40 + (warmup % 6) * 36)));
            }

            var samples = new List<double>(BoardDragOverPerfSamples);
            for (var step = 0; step < BoardDragOverPerfSamples; step++)
            {
                var arguments = NewDragEventArgs(
                    data,
                    DragDrop.PreviewDragOverEvent,
                    list,
                    new Point(160, 40 + (step % 12) * 36));
                var startedAt = Stopwatch.GetTimestamp();
                list.RaiseEvent(arguments);
                samples.Add(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            }

            samples.Sort();
            double Percentile(int rank) => samples[Math.Min(rank, samples.Count - 1)];
            Console.WriteLine(
                $"PERF|board-dragover|items={BoardDragOverPerfItemCount}|dragged=2" +
                $"|samples={samples.Count}");
            Console.WriteLine(
                $"PERF|  handler(ms): p50={Percentile((int)(samples.Count * 0.50)):0.000}" +
                $"|p95={Percentile((int)(samples.Count * 0.95)):0.000}" +
                $"|max={samples[^1]:0.000}|mean={samples.Average():0.000}");

            // 结构健全：事件被板面消费（Effects 判定路径确实走过）。
            var probe = NewDragEventArgs(data, DragDrop.PreviewDragOverEvent, list, new Point(160, 40));
            list.RaiseEvent(probe);
            Assert.IsTrue(probe.Handled, "前置：DragOver 走板面命中判定路径。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }
}
