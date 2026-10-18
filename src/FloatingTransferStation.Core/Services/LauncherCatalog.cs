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

/// <summary>编辑目标结果（#30 修订 2026-10-18，Issue #186）：更新成功 / 规范化重复拒绝 / 失败（未知条目、只读停用、保存失败）。</summary>
public enum LauncherUpdateTargetOutcome
{
    Updated,
    Duplicate,
    Failed,
}

/// <summary>
/// 启动项目录（#30，Issue #162；发布语义修订 Issue #176）：唯一突变入口，内部一条
/// 串行突变链——信号量包住「乐观应用到内存与 Items（按操作类型的最小集合通知：
/// 重排=单条 Move 容器复用、增删=Insert/Remove、改名/图标戳=属性通知，实例同体）
/// → SaveAsync（后台落盘，原子写管线零改动）→ 成功即确认 / 失败按本突变的逆
/// 操作回滚＋状态条」全过程。**门横跨应用与回滚**：后继突变在整个保存往返期间
/// 排队等待，不存在「A 保存失败回滚时踩掉已在途的 B」的交错（乐观上屏＋失败
/// 回弹语义对齐契约 #21/Issue #153 先例；保存失败还原契约 #1/#5 不变）。整集合
/// 读写，无逐条 CRUD；数组顺序即唯一真相。会话态字段（改名草稿/失败标记）不
/// 落盘、不经本协议。200 为可执行产品边界（WrapPanel 不虚拟化的有界性以此
/// 承载）。整集合 Clear+Add 发布仅存于装载（LoadAsync）。版本高于当前的数据 →
/// 只读停用：显示空列表 + 状态条提示，本会话禁用一切写路径（绝不以空集合覆盖
/// 回写、保持原文件）。
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

    /// <summary>发布视图（UI 绑定唯一集合；未变条目复用实例与容器）。</summary>
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
        // 线程池，Items 通知与状态条必须回到绑定线程（CollectionView 线程亲和；
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

            if (added.Count > 0)
            {
                var previous = _items;
                _items = working;
                ApplyToItems(items =>
                {
                    foreach (var candidate in added)
                    {
                        items.Add(candidate);
                    }
                });
                if (!await SaveWorkingAsync(working, cancellationToken).ConfigureAwait(false))
                {
                    _items = previous;
                    ApplyToItems(items =>
                    {
                        // 追加的逆操作：从尾部按逆序移除（期间无其他突变——门跨全程）。
                        for (var index = 0; index < added.Count; index++)
                        {
                            items.RemoveAt(items.Count - 1);
                        }
                    });
                    return new LauncherAddOutcome(0, [], duplicates, overflow);
                }
            }

            return new LauncherAddOutcome(added.Count, added, duplicates, overflow);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>重命名（≤64 截断由模型承载）：属性通知即上屏（实例同体，无集合变更）；
    /// 保存失败还原原名＋状态条。</summary>
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
            if (!await SaveWorkingAsync(_items, cancellationToken).ConfigureAwait(false))
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

    /// <summary>移除：成功返回条目与原下标（撤销提示条事务用）；保存失败重插原下标＋状态条。</summary>
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
            var previous = _items;
            _items = working;
            ApplyToItems(items => items.RemoveAt(index));
            if (!await SaveWorkingAsync(working, cancellationToken).ConfigureAwait(false))
            {
                _items = previous;
                ApplyToItems(items => items.Insert(Math.Min(index, items.Count), removed));
                return null;
            }

            return new LauncherRemoveOutcome(removed, index);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>重排（编辑模式拖拽）：把 id 条目移动到 toIndex，数组顺序即唯一真相。
    /// 乐观上屏=单条 Move 通知（容器复用）；保存失败反向 Move 回滚＋状态条。</summary>
    public Task<bool> MoveAsync(Guid id, int toIndex, CancellationToken cancellationToken = default) =>
        MoveAsync(id, toIndex, relative: false, cancellationToken);

    /// <summary>相对重排（菜单上下移）：delta 相对**门内权威序**解析目标下标——
    /// 调用方传绝对下标会在乐观序上与在途回滚竞态（回滚后绝对下标指向错位）。</summary>
    public Task<bool> MoveByAsync(Guid id, int delta, CancellationToken cancellationToken = default) =>
        MoveAsync(id, delta, relative: true, cancellationToken);

    private async Task<bool> MoveAsync(
        Guid id,
        int indexOrDelta,
        bool relative,
        CancellationToken cancellationToken)
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

            // toIndex 在门内权威序上解析（钳制域 [0, Count-1]：移除后域=插入域，
            // ObservableCollection.Move 与 List.Insert 同域）。
            var toIndex = relative
                ? Math.Clamp(fromIndex + indexOrDelta, 0, _items.Count - 1)
                : indexOrDelta;
            var working = _items.ToList();
            var item = working[fromIndex];
            working.RemoveAt(fromIndex);
            var insertIndex = Math.Clamp(toIndex, 0, working.Count);
            working.Insert(insertIndex, item);
            if (working.Select((entry, index) => entry.Id)
                .SequenceEqual(_items.Select(entry => entry.Id)))
            {
                return false;
            }

            var previous = _items;
            _items = working;
            ApplyToItems(items => items.Move(fromIndex, insertIndex));
            if (!await SaveWorkingAsync(working, cancellationToken).ConfigureAwait(false))
            {
                _items = previous;
                ApplyToItems(items => items.Move(insertIndex, fromIndex));
                return false;
            }

            return true;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>撤销重插（#30 §三.7）：按原下标 min(index, 当前数) 重插被删条目
    /// （其后重排不回退；重新添加同目标与撤销互不影响——去重只在添加时判定）。</summary>
    public async Task<bool> RestoreAsync(
        LauncherItem item,
        int index,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            if (IsReadOnlyFromNewerVersion || _items.Any(entry => entry.Id == item.Id))
            {
                return false;
            }

            var working = _items.ToList();
            var insertIndex = Math.Clamp(index, 0, working.Count);
            working.Insert(insertIndex, item);
            var previous = _items;
            _items = working;
            ApplyToItems(items => items.Insert(insertIndex, item));
            if (!await SaveWorkingAsync(working, cancellationToken).ConfigureAwait(false))
            {
                _items = previous;
                ApplyToItems(items => items.RemoveAt(insertIndex));
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
    /// 图标戳推进（同一串行协议）：只在图标 PNG 成功发布后调用；属性通知即上屏，
    /// 保存失败静默还原（旧图继续有效，陈旧可接受）。
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
            if (!await SaveWorkingAsync(_items, cancellationToken).ConfigureAwait(false))
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

    /// <summary>
    /// 编辑目标（#30 修订 2026-10-18，Issue #186 D7；同一串行协议）：目标三元组
    /// 就地替换＋IconStamp 置空（图标管线自动重取），用户名保留；乐观上屏=属性
    /// 通知（实例同体，零集合变更，对齐 RenameAsync 先例），保存失败还原全部
    /// 旧值。规范化 (TargetPath, Arguments) 与**其他**条目重复 → Duplicate
    /// （不突变、不写盘——UI 层提示「已在启动列表中」）；目标未变=Updated 无写盘。
    /// </summary>
    public async Task<LauncherUpdateTargetOutcome> UpdateTargetAsync(
        Guid id,
        LauncherKind kind,
        string targetPath,
        string? arguments = null,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            if (IsReadOnlyFromNewerVersion || _items.FirstOrDefault(item => item.Id == id) is not { } item)
            {
                return LauncherUpdateTargetOutcome.Failed;
            }

            var candidate = new LauncherItem(
                id,
                kind,
                targetPath,
                item.DisplayName,
                item.CreatedAt,
                arguments,
                workingDirectory,
                item.IconStamp);
            if (_items.Any(entry => entry.Id != id && DedupKey(entry) == DedupKey(candidate)))
            {
                return LauncherUpdateTargetOutcome.Duplicate;
            }

            if (item.Kind == kind &&
                item.TargetPath == targetPath &&
                item.Arguments == arguments &&
                item.WorkingDirectory == workingDirectory)
            {
                return LauncherUpdateTargetOutcome.Updated;
            }

            var original = (item.Kind, item.TargetPath, item.Arguments, item.WorkingDirectory, item.IconStamp);
            item.AdoptEditedTarget(kind, targetPath, arguments, workingDirectory);
            if (!await SaveWorkingAsync(_items, cancellationToken).ConfigureAwait(false))
            {
                item.AdoptEditedTarget(original.Item1, original.Item2, original.Item3, original.Item4);
                if (item.IconStamp != original.Item5)
                {
                    item.AdoptPersistedIconStamp(original.Item5);
                }

                return LauncherUpdateTargetOutcome.Failed;
            }

            return LauncherUpdateTargetOutcome.Updated;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public LauncherItem? Find(Guid id) => _items.FirstOrDefault(item => item.Id == id);

    /// <summary>保存 working 快照；失败时状态条＋false（回滚由调用方按本突变的逆操作执行，
    /// 与应用同在门内——不存在后继突变可被踩掉）。</summary>
    private async Task<bool> SaveWorkingAsync(List<LauncherItem> working, CancellationToken cancellationToken)
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

        return true;
    }

    /// <summary>乐观应用 Items 侧的最小集合通知（封送到绑定线程）。</summary>
    private void ApplyToItems(Action<ObservableCollection<LauncherItem>> apply) => _marshal(() => apply(Items));

    private void ReportStatus(string message) => _marshal(() => _showStatus(message));

    /// <summary>装载发布（整表替换专用，LoadAsync）：Clear+Add 全量重建只在此处合法。</summary>
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
