using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

/// <summary>
/// 启动·自动发现核心服务（#45）：对注入的候选源做差分——候选的 DedupKey（与
/// LauncherCatalog 同一规范化 (TargetPath, Arguments)）不在「启动目录 ∪ pending ∪
/// dismissed」三集合 → 新 pending（pending 自身按键去重，防重扫重复入列）。确认制：
/// 采纳＝从 pending 移除（进入启动目录的事实由下次重扫的差分排除集承载）；忽略＝
/// 入 dismissed（FIFO 上限 <see cref="DismissedCapacity"/>，超出驱逐最旧）。持久化
/// launcher-discoveries.json（LocalStore 分部，原子写契约 #1）。突变协议照
/// LauncherCatalog：信号量横跨「乐观应用到内存 → 后台保存 → 成功确认 / 失败还原＋
/// 状态条」，UI 通知经注入的封送委托回绑定线程。候选源在门外枚举（IO/COM 慢活不
/// 占门），门内只做纯差分与持久化。本服务不枚举任何 Shell/文件系统——发现源是
/// 注入的 <c>Func&lt;Task&lt;IReadOnlyList&lt;LauncherDiscoveryEntry&gt;&gt;&gt;</c>，
/// 生产适配器在 WPF 工程（Launcher/），测试注入假源（UpdateService 模式，契约 #26）。
/// </summary>
public sealed class LauncherDiscoveryService
{
    /// <summary>忽略清单容量：超出按 FIFO 驱逐最旧键（被驱逐者下次重扫会重新入列）。</summary>
    public const int DismissedCapacity = 500;

    private readonly ILauncherDiscoveryStore _store;
    private readonly Action<string> _showStatus;
    private readonly Action<Action> _marshal;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private List<LauncherDiscoveryEntry> _pending = [];
    private List<string> _dismissed = [];

    private LauncherDiscoveryService(
        ILauncherDiscoveryStore store,
        Action<string> showStatus,
        Action<Action> marshal)
    {
        _store = store;
        _showStatus = showStatus;
        _marshal = marshal;
    }

    /// <summary>待确认条目数（按钮「发现 N」的唯一来源；int 读原子，UI 线程读安全）。</summary>
    public int PendingCount => _pending.Count;

    /// <summary>待确认清单快照（UI 打开选择器时的行数据；跨线程读，返回副本）。</summary>
    public IReadOnlyList<LauncherDiscoveryEntry> PendingSnapshot() => [.. _pending];

    /// <summary>待确认集合变化（增/删/忽略后；经封送委托回绑定线程）。</summary>
    public event EventHandler? PendingChanged;

    public static async Task<LauncherDiscoveryService> LoadAsync(
        ILauncherDiscoveryStore store,
        Action<string> showStatus,
        Action<Action>? marshalToPublisher = null,
        CancellationToken cancellationToken = default)
    {
        var service = new LauncherDiscoveryService(store, showStatus, marshalToPublisher ?? (action => action()));
        var data = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        service._pending = [.. data.Pending];
        service._dismissed = [.. data.DismissedKeys];
        return service;
    }

    /// <summary>
    /// 重扫差分：候选源（门外枚举）× 当前启动目录 → 增量 pending。已进启动目录的
    /// pending 条目被修剪（用户经 ＋ 菜单手动添加同目标后，发现清单不再重复提示）；
    /// dismissed 不修剪——「忽略」是持久的抑制记忆，即使其后条目被添加又删除。
    /// 无变化零写盘。返回差分后的 pending 总数。
    /// </summary>
    public async Task<int> RescanAsync(
        Func<Task<IReadOnlyList<LauncherDiscoveryEntry>>> candidateSource,
        IReadOnlyList<LauncherItem> catalogItems,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var candidates = await candidateSource().ConfigureAwait(false);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalogKeys = catalogItems.Select(LauncherCatalog.DedupKey).ToHashSet(StringComparer.Ordinal);
            var pending = _pending.Where(entry => !catalogKeys.Contains(KeyOf(entry))).ToList();
            var knownKeys = pending.Select(KeyOf).ToHashSet(StringComparer.Ordinal);
            foreach (var candidate in candidates)
            {
                var key = KeyOf(candidate);
                if (key.Length == 0 ||
                    catalogKeys.Contains(key) ||
                    knownKeys.Contains(key) ||
                    _dismissed.Contains(key))
                {
                    continue;
                }

                pending.Add(candidate);
                knownKeys.Add(key);
            }

            if (SamePending(pending, _pending))
            {
                return pending.Count;
            }

            if (await SaveAsync(new LauncherDiscoveryStoreData(
                    LauncherDiscoveryStoreData.CurrentVersion,
                    pending,
                    [.. _dismissed]),
                    cancellationToken).ConfigureAwait(false))
            {
                _pending = pending;
                RaisePendingChanged();
            }

            return _pending.Count;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>采纳：从 pending 移除（不写 dismissed——条目已进启动目录，差分排除集
    /// 天然抑制重发现；保存失败还原＋状态条）。返回采纳后剩余 pending 数。</summary>
    public async Task<int> AdoptAsync(
        IReadOnlyList<LauncherDiscoveryEntry> entries,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var adoptedKeys = entries.Select(KeyOf).ToHashSet(StringComparer.Ordinal);
            var pending = _pending.Where(entry => !adoptedKeys.Contains(KeyOf(entry))).ToList();
            if (pending.Count == _pending.Count)
            {
                return pending.Count;
            }

            if (await SaveAsync(new LauncherDiscoveryStoreData(
                    LauncherDiscoveryStoreData.CurrentVersion,
                    pending,
                    [.. _dismissed]),
                    cancellationToken).ConfigureAwait(false))
            {
                _pending = pending;
                RaisePendingChanged();
            }

            return _pending.Count;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>忽略：pending → dismissed（FIFO 上限驱逐最旧；保存失败还原＋状态条）。
    /// 返回忽略后剩余 pending 数。</summary>
    public async Task<int> DismissAsync(
        IReadOnlyList<LauncherDiscoveryEntry> entries,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dismissedKeys = entries.Select(KeyOf).ToHashSet(StringComparer.Ordinal);
            var pending = _pending.Where(entry => !dismissedKeys.Contains(KeyOf(entry))).ToList();
            if (pending.Count == _pending.Count)
            {
                return pending.Count;
            }

            var dismissed = new List<string>(_dismissed);
            foreach (var key in _dismissed)
            {
                dismissedKeys.Remove(key);
            }

            dismissed.AddRange(dismissedKeys);
            if (dismissed.Count > DismissedCapacity)
            {
                dismissed.RemoveRange(0, dismissed.Count - DismissedCapacity);
            }

            if (await SaveAsync(new LauncherDiscoveryStoreData(
                    LauncherDiscoveryStoreData.CurrentVersion,
                    pending,
                    dismissed),
                    cancellationToken).ConfigureAwait(false))
            {
                _pending = pending;
                _dismissed = dismissed;
                RaisePendingChanged();
            }

            return _pending.Count;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>全部忽略（发现清单窗「全部忽略」）：pending 清空、全部键入 dismissed。</summary>
    public Task<int> DismissAllAsync(CancellationToken cancellationToken = default) =>
        DismissAsync(_pending, cancellationToken);

    private static string KeyOf(LauncherDiscoveryEntry entry) =>
        LauncherCatalog.DedupKeyFor(entry.TargetPath, entry.Arguments);

    private static bool SamePending(List<LauncherDiscoveryEntry> candidate, List<LauncherDiscoveryEntry> current)
    {
        if (candidate.Count != current.Count)
        {
            return false;
        }

        for (var index = 0; index < candidate.Count; index++)
        {
            if (!candidate[index].Equals(current[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>保存快照；失败时状态条＋false（还原由调用方保持旧引用承载——与
    /// 应用同在门内，不存在后继突变可被踩掉）。</summary>
    private async Task<bool> SaveAsync(LauncherDiscoveryStoreData data, CancellationToken cancellationToken)
    {
        try
        {
            await _store.SaveAsync(data, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportStatus("发现清单未保存，已还原。");
            return false;
        }

        return true;
    }

    private void RaisePendingChanged() => _marshal(() => PendingChanged?.Invoke(this, EventArgs.Empty));

    private void ReportStatus(string message) => _marshal(() => _showStatus(message));
}
