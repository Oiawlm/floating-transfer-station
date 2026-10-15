using System.Collections.ObjectModel;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

/// <summary>添加结果（#30）：新增 / 已存在（去重命中，供滚动+脉冲）/ 超上限跳过计数。</summary>
public sealed record LauncherAddOutcome(
    int AddedCount,
    IReadOnlyList<LauncherItem> AddedItems,
    IReadOnlyList<LauncherItem> Duplicates,
    int OverflowSkippedCount)
{
    public static LauncherAddOutcome Empty { get; } = new(0, [], [], 0);
}

/// <summary>移除结果（#30）：被删条目与其原下标（撤销提示条按 min(原下标, 当前数) 重插）。</summary>
public sealed record LauncherRemoveOutcome(LauncherItem Item, int Index);

/// <summary>
/// 启动项目录（#30，Issue #162）：唯一突变入口，内部一条串行突变链——信号量包住
/// 「生成不可变快照 → SaveAsync(快照) → 成功发布到 Items / 失败还原内存原状＋状态条」
/// 全过程（不只包文件写），旧快照永不晚于新快照落盘。整集合读写，无逐条 CRUD；
/// 数组顺序即唯一真相。会话态字段（改名草稿/失败标记）不落盘、不经本协议。
/// 200 为可执行产品边界（WrapPanel 不虚拟化的有界性以此承载）。版本高于当前的
/// 数据 → 只读停用：显示空列表 + 状态条提示，本会话禁用一切写路径（绝不以空
/// 集合覆盖回写、保持原文件）。
/// </summary>
public sealed class LauncherCatalog
{
    /// <summary>数量上限（可执行产品边界；代码注释标注阈值常量，路线图预案不预建）。</summary>
    public const int MaxItems = 200;

    private readonly ILauncherStore _store;
    private readonly Action<string> _showStatus;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly Action<Action> _marshal;
    private List<LauncherItem> _items = [];

    private LauncherCatalog(
        ILauncherStore store,
        Action<string> showStatus,
        Action<Action> marshal)
    {
        _store = store;
        _showStatus = showStatus;
        _marshal = marshal;
        Items = [];
    }

    /// <summary>发布视图（UI 绑定唯一集合；整集合替换，未变条目复用实例）。</summary>
    public ObservableCollection<LauncherItem> Items { get; }

    public int Count => _items.Count;

    /// <summary>只读停用：launchers.json 版本来自更新的版本，本会话禁用一切写路径。</summary>
    public bool IsReadOnlyFromNewerVersion { get; private set; }

    public static async Task<LauncherCatalog> LoadAsync(
        ILauncherStore store,
        Action<string> showStatus,
        Action<Action>? marshalToPublisher = null,
        CancellationToken cancellationToken = default)
    {
        // 显式发布封送（UI 注入 Dispatcher.Invoke；缺省内联）：保存续体可能落到
        // 线程池，发布 Items 与状态条必须回到绑定线程（CollectionView 线程亲和；
        // 测试线程可能没有同步上下文，不依赖 SynchronizationContext 捕获）。
        var catalog = new LauncherCatalog(store, showStatus, marshalToPublisher ?? (action => action()));
        var data = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        catalog._items = data.Version > LauncherStoreData.CurrentVersion ? [] : data.Items;
        if (data.Version > LauncherStoreData.CurrentVersion)
        {
            catalog.IsReadOnlyFromNewerVersion = true;
            catalog.Publish([]);
            return catalog;
        }

        catalog.Publish(data.Items);
        return catalog;
    }

    /// <summary>
    /// 批量添加：规范化 (TargetPath, Arguments) 去重（命中不新增，返回命中项供滚动+脉冲）；
    /// 第 200 项后拒绝并跳过超额部分（计数由调用方并入状态条汇报）。
    /// </summary>
    public async Task<LauncherAddOutcome> AddRangeAsync(
        IEnumerable<LauncherItem> candidates,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            if (IsReadOnlyFromNewerVersion)
            {
                ReportStatus("启动项数据来自更新的版本，已暂停修改。");
                return LauncherAddOutcome.Empty;
            }

            var working = _items.ToList();
            var knownKeys = working.Select(DedupKey).ToHashSet(StringComparer.Ordinal);
            var added = new List<LauncherItem>();
            var duplicates = new List<LauncherItem>();
            var overflow = 0;
            foreach (var candidate in candidates)
            {
                var key = DedupKey(candidate);
                if (knownKeys.Contains(key))
                {
                    duplicates.Add(working.First(item => DedupKey(item) == key));
                    continue;
                }

                if (working.Count >= MaxItems)
                {
                    overflow++;
                    continue;
                }

                working.Add(candidate);
                knownKeys.Add(key);
                added.Add(candidate);
            }

            if (added.Count > 0 && !await SaveAndPublishAsync(working, cancellationToken).ConfigureAwait(false))
            {
                return new LauncherAddOutcome(0, [], duplicates, overflow);
            }

            return new LauncherAddOutcome(added.Count, added, duplicates, overflow);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>重命名（≤64 截断由模型承载）：保存失败还原原名＋状态条。</summary>
    public async Task<bool> RenameAsync(Guid id, string displayName, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            if (IsReadOnlyFromNewerVersion || _items.FirstOrDefault(item => item.Id == id) is not { } item)
            {
                return false;
            }

            var originalName = item.DisplayName;
            item.AdoptPersistedName(displayName);
            if (!await SaveAndPublishAsync(_items.ToList(), cancellationToken).ConfigureAwait(false))
            {
                item.AdoptPersistedName(originalName);
                return false;
            }

            return true;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>移除：保存失败还原原集合＋状态条；成功返回条目与原下标（撤销提示条事务用）。</summary>
    public async Task<LauncherRemoveOutcome?> RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            if (IsReadOnlyFromNewerVersion)
            {
                ReportStatus("启动项数据来自更新的版本，已暂停修改。");
                return null;
            }

            var index = _items.FindIndex(item => item.Id == id);
            if (index < 0)
            {
                return null;
            }

            var working = _items.ToList();
            var removed = working[index];
            working.RemoveAt(index);
            if (!await SaveAndPublishAsync(working, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return new LauncherRemoveOutcome(removed, index);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>重排（编辑模式拖拽）：把 id 条目移动到 toIndex（钳制到合法范围），数组顺序即唯一真相。</summary>
    public async Task<bool> MoveAsync(Guid id, int toIndex, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            if (IsReadOnlyFromNewerVersion)
            {
                return false;
            }

            var fromIndex = _items.FindIndex(item => item.Id == id);
            if (fromIndex < 0)
            {
                return false;
            }

            var working = _items.ToList();
            var item = working[fromIndex];
            working.RemoveAt(fromIndex);
            working.Insert(Math.Clamp(toIndex, 0, working.Count), item);
            if (working.Select((entry, index) => entry.Id)
                .SequenceEqual(_items.Select(entry => entry.Id)))
            {
                return false;
            }

            if (!await SaveAndPublishAsync(working, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            return true;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// 图标戳推进（同一串行协议）：只在图标 PNG 成功发布后调用；保存失败静默还原
    /// （旧图继续有效，陈旧可接受）。
    /// </summary>
    public async Task<bool> UpdateIconStampAsync(Guid id, string? iconStamp, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            if (IsReadOnlyFromNewerVersion || _items.FirstOrDefault(item => item.Id == id) is not { } item)
            {
                return false;
            }

            if (item.IconStamp == iconStamp)
            {
                return true;
            }

            var originalStamp = item.IconStamp;
            item.AdoptPersistedIconStamp(iconStamp);
            if (!await SaveAndPublishAsync(_items.ToList(), cancellationToken).ConfigureAwait(false))
            {
                item.AdoptPersistedIconStamp(originalStamp);
                return false;
            }

            return true;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public LauncherItem? Find(Guid id) => _items.FirstOrDefault(item => item.Id == id);

    private async Task<bool> SaveAndPublishAsync(List<LauncherItem> working, CancellationToken cancellationToken)
    {
        var snapshot = working.Select(item => item.CloneForSnapshot()).ToList();
        try
        {
            await _store.SaveAsync(new LauncherStoreData(LauncherStoreData.CurrentVersion, snapshot), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportStatus("启动项未保存，已还原。");
            return false;
        }

        _items = working;
        Publish(working);
        return true;
    }

    private void ReportStatus(string message) => _marshal(() => _showStatus(message));

    private void Publish(List<LauncherItem> items) => _marshal(() => PublishInline(items));

    private void PublishInline(List<LauncherItem> items)
    {
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }
    }

    internal static string DedupKey(LauncherItem item) =>
        string.Join(
            '\u0001',
            LauncherIconKey.NormalizeTarget(item.TargetPath),
            item.Arguments ?? string.Empty);
}
