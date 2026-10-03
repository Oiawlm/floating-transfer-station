using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public interface IDailyReviewStore : IDisposable
{
    string ReviewsDirectory { get; }

    event EventHandler<DailyReviewFileChangedEventArgs>? Changed;

    Task<DailyReviewDocument> LoadAsync(
        DateOnly date,
        CancellationToken cancellationToken = default);

    /// <summary>列出已有复盘文件的日期。仅 macOS 复盘日期列表仍在消费；Windows 1.16.0 起日期导航为 ‹ › 单步切换，不再调用。</summary>
    Task<IReadOnlyList<DateOnly>> ListDatesAsync(
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        DateOnly date,
        string content,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        DateOnly date,
        CancellationToken cancellationToken = default);

    void StartWatching();

    void StopWatching();
}
