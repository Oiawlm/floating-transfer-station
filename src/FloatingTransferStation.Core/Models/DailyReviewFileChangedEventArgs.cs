namespace FloatingTransferStation.Models;

public enum DailyReviewFileChangeKind
{
    Created,
    Changed,
    Deleted,
    Renamed,
    RescanRequired
}

public sealed class DailyReviewFileChangedEventArgs(
    DateOnly? date,
    DailyReviewFileChangeKind kind) : EventArgs
{
    public DateOnly? Date { get; } = date;
    public DailyReviewFileChangeKind Kind { get; } = kind;
}
