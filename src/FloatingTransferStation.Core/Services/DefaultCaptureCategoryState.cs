using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public sealed class DefaultCaptureCategoryState
{
    private int _current = (int)BoardCategory.Inbox;

    public BoardCategory Current => (BoardCategory)Volatile.Read(ref _current);

    public bool Set(BoardCategory category)
    {
        // 纵深守卫（#30）：默认接收只能落在内容桶——UI 点击已有角色闸，本守卫拦截
        // 绕过 UI 的直改（复盘与启动标签绝不成为剪贴板落点）。
        if (!BoardCategoryCatalog.IsDefined(category) ||
            !BoardCategoryCatalog.IsContentBucket(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        return Interlocked.Exchange(ref _current, (int)category) != (int)category;
    }
}
