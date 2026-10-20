namespace FloatingTransferStation.Services;

/// <summary>
/// favicon 负缓存（会话态内存组件，Core 承载以便离线测试）：TTL 只抑制会话内重复
/// 触发；命中不得刷新失败时间（防永久延期）；容量有界（超限淘汰最旧）。键=规范化
/// 目标的 IconKey（非 origin——同站不同页互不牵连）；取消/条目删除不记录。
/// </summary>
public sealed class FaviconNegativeCache
{
    /// <summary>容量上限：200 瓦片上限的 2.5 倍余量，超限淘汰最旧失败记录。</summary>
    public const int Capacity = 512;

    private readonly TimeSpan _timeToLive;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _failures = [];

    public FaviconNegativeCache(TimeSpan? timeToLive = null)
    {
        _timeToLive = timeToLive ?? TimeSpan.FromMinutes(10);
    }

    /// <summary>命中=true 且不刷新失败时间；过期条目惰性清除。</summary>
    public bool IsNegative(string iconKey, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_failures.TryGetValue(iconKey, out var failedAt))
            {
                return false;
            }

            if (now - failedAt >= _timeToLive)
            {
                _failures.Remove(iconKey);
                return false;
            }

            return true;
        }
    }

    /// <summary>记录失败（已存在时保留原失败时间——命中不刷新）。</summary>
    public void Record(string iconKey, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_failures.TryGetValue(iconKey, out var existing) && now - existing < _timeToLive)
            {
                return;
            }

            if (_failures.Count >= Capacity)
            {
                var oldest = _failures.MinBy(pair => pair.Value.Ticks).Key;
                _failures.Remove(oldest);
            }

            _failures[iconKey] = now;
        }
    }

    /// <summary>显式失效（编辑目标/重置 once 标记时对旧键调用）。</summary>
    public void Remove(string iconKey)
    {
        lock (_gate)
        {
            _failures.Remove(iconKey);
        }
    }
}
