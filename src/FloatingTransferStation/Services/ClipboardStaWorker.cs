using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace FloatingTransferStation.Services;

/// <summary>
/// 剪贴板专用 STA 后台线程（2026-11-16 锁死事故根治，R2）：一切 OLE 剪贴板调用
///（读/备份读/写）串行跑在本线程，UI 线程零直接调用——邻居属主挂死时挂住的本线程
/// 可被硬超时弃置（poison）并由新线程接管，fts UI 保持完全响应（H3 根因消除）。
/// 线程内创建 message-only 属主窗口（OpenClipboard 需要真实窗口——OpenClipboard(NULL)
/// ＋EmptyClipboard 会把 owner 置 NULL，后续 SetClipboardData 失败，MSDN）；等待期
/// 泵送 SEND 消息（MsgWaitForMultipleObjectsEx＋PeekMessage，.NET ClipboardCore 同法）
/// ——属主消息（WM_DESTROYCLIPBOARD 等）由本线程消化，不反向阻塞任何发送方。
/// </summary>
internal sealed class ClipboardStaWorker : IDisposable
{
    private readonly object _gate = new();
    private Worker? _current;
    private bool _disposed;

    /// <summary>
    /// 在 STA 线程上执行剪贴板操作并施加硬超时：超时＝当前线程弃置（标记 poison、
    /// 队列内未启动项按超时失败、后续操作换新线程）＋向调用方抛
    /// <see cref="ExternalException"/>（映射既有失败路径：采集有界重试/状态条提示）。
    /// 挂死线程不杀不等待（.NET 无安全 Thread.Abort）——它若最终返回，其结果由
    /// 已超时的等待方自然丢弃，无副作用。
    /// </summary>
    public Task<T> RunAsync<T>(Func<T> operation, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Worker worker;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            worker = EnsureWorkerLocked();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.Queue.Enqueue(() =>
        {
            try
            {
                completion.TrySetResult(operation());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        worker.WakeUp();
        return WaitWithTimeoutAsync(completion.Task, timeout, cancellationToken, worker);
    }

    /// <summary>同步形态（调用方为后台线程时使用；禁止在 UI 线程调用）。</summary>
    public T Run<T>(Func<T> operation, TimeSpan timeout) =>
        RunAsync(operation, timeout).GetAwaiter().GetResult();

    /// <summary>当前属主窗口句柄（0＝创建失败/竞态；写路径放弃并记录）。</summary>
    public nint OwnerWindowHandle
    {
        get
        {
            Worker worker;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                worker = EnsureWorkerLocked();
            }

            // 窗口在线程启动后立即创建（µs 级）；有界等待防竞态，等不到按 0 处理。
            for (var attempt = 0;
                worker.OwnerWindow == 0 && worker.Thread.IsAlive && attempt < 100;
                attempt++)
            {
                Thread.Sleep(1);
            }

            return worker.OwnerWindow;
        }
    }

    private async Task<T> WaitWithTimeoutAsync<T>(
        Task<T> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Worker worker)
    {
        try
        {
            await operation.WaitAsync(timeout, cancellationToken);
            return await operation;
        }
        catch (TimeoutException)
        {
            // 硬超时：弃置当前线程（含其属主窗口——零渲染义务下窗口消亡无数据损失），
            // 后续操作自动换新线程。返回超时异常供调用方记录占用者遥测。
            worker.IsPoisoned = true;
            throw CreateTimeoutException();
        }
    }

    private static ClipboardTimeoutException CreateTimeoutException() =>
        new("A clipboard operation timed out; the worker thread was abandoned.");

    private Worker EnsureWorkerLocked()
    {
        if (_current is { } worker && worker.Thread.IsAlive && !worker.IsPoisoned)
        {
            return worker;
        }

        _current?.RequestShutdown();
        return _current = new Worker();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _current?.RequestShutdown();
        }

        _current?.Thread.Join(500);
    }

    private sealed class Worker
    {
        private readonly ManualResetEventSlim _wake = new(false);
        private volatile bool _shutdownRequested;

        public readonly ConcurrentQueue<Action> Queue = [];
        public readonly Thread Thread;

        public volatile bool IsPoisoned;
        public volatile nint OwnerWindow;

        public Worker()
        {
            Thread = new Thread(Run)
            {
                Name = "FloatingTransferStation Clipboard STA",
                IsBackground = true,
            };
            Thread.SetApartmentState(System.Threading.ApartmentState.STA);
            Thread.Start();
        }

        public void WakeUp() => _wake.Set();

        public void RequestShutdown()
        {
            _shutdownRequested = true;
            _wake.Set();
        }

        private void Run()
        {
            NativeMethods.OleInitialize(0);
            try
            {
                OwnerWindow = NativeMethods.CreateWindowExW(
                    0,
                    "STATIC",
                    "FloatingTransferStation Clipboard Owner",
                    0,
                    0, 0, 0, 0,
                    NativeMethods.HwndMessage,
                    0, 0, 0);
                while (!_shutdownRequested)
                {
                    // 等待＝工作信号或 SEND 消息；先 Reset 后消费（消费期间新入队会再次
                    // 置位，下一轮立即醒，不丢唤醒）。
                    _wake.Reset();
                    NativeMethods.DispatchPendingSentMessages();
                    while (Queue.TryDequeue(out var item))
                    {
                        RunItem(item);
                    }

                    WaitHandle? wakeHandle = _wake.WaitHandle;
                    var handle = wakeHandle.SafeWaitHandle.DangerousGetHandle();
                    NativeMethods.MsgWaitForMultipleObjectsEx(
                        1,
                        handle,
                        unchecked((uint)Timeout.Infinite),
                        NativeMethods.QsSendMessage,
                        0);
                }
            }
            finally
            {
                if (OwnerWindow != 0)
                {
                    // DestroyWindow 必须由创建线程调用（本线程收尾）。
                    NativeMethods.DestroyWindow(OwnerWindow);
                    OwnerWindow = 0;
                }

                NativeMethods.OleUninitialize();
            }
        }

        private void RunItem(Action item)
        {
            if (IsPoisoned)
            {
                return;
            }

            // item 自带 try/catch 完成等待者；这里异常只可能是委托壳本身缺陷，
            // 静默防线程死亡（线程死亡会让后续操作重建，行为仍正确但多一次线程 churn）。
            try
            {
                item();
            }
            catch (Exception)
            {
            }
        }
    }
}
