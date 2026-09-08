using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public sealed class DefaultCaptureCategoryState
{
    private int _current = (int)BoardCategory.Inbox;

    public BoardCategory Current => (BoardCategory)Volatile.Read(ref _current);

    public bool Set(BoardCategory category)
    {
        if (!BoardCategoryCatalog.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        return Interlocked.Exchange(ref _current, (int)category) != (int)category;
    }
}
