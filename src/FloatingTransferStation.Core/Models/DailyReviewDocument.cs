namespace FloatingTransferStation.Models;

public sealed record DailyReviewDocument(
    DateOnly Date,
    string Content,
    bool Exists,
    string ContentHash);
