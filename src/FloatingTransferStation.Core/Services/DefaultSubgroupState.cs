using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

/// <summary>
/// 默认收集落点子分组（契约 #57 修订，会话态）：镜像 <see cref="DefaultCaptureCategoryState"/>
/// ——不持久化、每内容桶分类独立一份、装载期无默认（null＝未指定组）；无 Clear
/// （无取消手势，单击换，镜像分类轨）。剪贴板捕获与外部拖入按「默认分类的默认
/// 子分组」落卡；未设默认时新卡落 null（未指定组居首，#57 既有断言）。
/// </summary>
public sealed class DefaultSubgroupState
{
    // 槽值 0-3，-1＝未设默认（null，0 是合法槽不可作缺省）；与分类轨同一读写口径：
    // 剪贴板后台线程读 Current、UI 线程 Set，按元素 Volatile/Interlocked 保证一致。
    private readonly int[] _current = [.. Enumerable.Repeat(-1, BoardCategoryCatalog.Ordered.Count)];

    public int? Current(BoardCategory category)
    {
        // 纵深守卫（镜像 Set）：非内容桶没有默认子分组语义（轨/环零呈现）。
        if (!BoardCategoryCatalog.IsDefined(category) ||
            !BoardCategoryCatalog.IsContentBucket(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        var slot = Volatile.Read(ref _current[(int)category]);
        return slot >= 0 ? slot : null;
    }

    public bool Set(BoardCategory category, int slot)
    {
        // 纵深守卫（#30 同法）：默认落点只落在内容桶的合法槽——UI 单击已有角色闸，
        // 本守卫拦截绕过 UI 的直改；同值返回 false（差分语义，镜像分类轨）。
        if (!BoardCategoryCatalog.IsDefined(category) ||
            !BoardCategoryCatalog.IsContentBucket(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        if (!SubgroupCatalog.IsValidSlot(slot))
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        return Interlocked.Exchange(ref _current[(int)category], slot) != slot;
    }
}
