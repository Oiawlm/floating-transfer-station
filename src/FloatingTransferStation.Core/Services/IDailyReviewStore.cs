using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public interface IDailyReviewStore : IDisposable
{
    string ReviewsDirectory { get; }

    event EventHandler<DailyReviewFileChangedEventArgs>? Changed;

    Task<DailyReviewDocument> LoadAsync(
        DateOnly date,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 枚举「有文字记录」的日期（升序排序）：目录内文件名可解析为
    /// yyyy-MM-dd 且内容非空白——「有无记录」与 <see cref="LoadAsync"/> 的
    /// Exists 判定同源（空白文件＝无记录）。
    /// </summary>
    Task<IReadOnlyList<DateOnly>> EnumerateRecordedDatesAsync(
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
