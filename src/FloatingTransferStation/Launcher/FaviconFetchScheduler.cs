using System.Collections.Concurrent;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Launcher;

/// <summary>
/// favicon 抓取执行资格调度器（#30 修订，Issue #206）：每 origin 1 × 全局并发 4。
/// 许可获取顺序＝先 origin 后全局（等待 origin 的任务不得占用全局执行槽）；释放顺序
/// 相反。同一时刻每条目至多持有一个 origin 许可——重定向跨 origin 时客户端先释放
/// 当前许可再申请新许可，结构上排除「A 持 origin1 等 origin2、B 反向」死锁。
/// 许可只覆盖网络请求（解码/落盘不占）。origin 键=scheme+host+port 小写。
/// </summary>
internal sealed class FaviconFetchScheduler : IDisposable
{
    public const int GlobalConcurrency = 4;

    private readonly SemaphoreSlim _global = new(GlobalConcurrency, GlobalConcurrency);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _origins = [];

    /// <summary>取得执行资格（origin→全局）；等待 origin 许可期间不占全局槽。
    /// 取消令牌=条目预算（首个请求取得资格前由调用方不启动预算）。</summary>
    public async Task<IAsyncDisposable> AcquireAsync(Uri origin, CancellationToken cancellationToken)
    {
        var originKey = $"{origin.Scheme}://{origin.IdnHost}:{origin.Port}";
        var originSemaphore = _origins.GetOrAdd(originKey, _ => new SemaphoreSlim(1, 1));
        await originSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _global.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            originSemaphore.Release();
            throw;
        }

        return new Permit(originSemaphore, _global);
    }

    public void Dispose()
    {
        _global.Dispose();
        foreach (var semaphore in _origins.Values)
        {
            semaphore.Dispose();
        }

        _origins.Clear();
    }

    private sealed class Permit(SemaphoreSlim origin, SemaphoreSlim global) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            global.Release();
            origin.Release();
            return ValueTask.CompletedTask;
        }
    }
}
