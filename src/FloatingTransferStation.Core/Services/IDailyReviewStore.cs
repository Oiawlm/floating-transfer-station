using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public interface IDailyReviewStore : IDisposable
{
    string ReviewsDirectory { get; }

    event EventHandler<DailyReviewFileChangedEventArgs>? Changed;

    Task<DailyReviewDocument> LoadAsync(
        DateOnly date,
        CancellationToken cancellationToken = default);

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
