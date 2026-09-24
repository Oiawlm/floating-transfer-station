namespace FloatingTransferStation.Services;

/// <summary>
/// Suppresses back-to-back clipboard captures whose content is identical to
/// the most recently accepted capture. Source applications (WeChat among them)
/// can publish one logical copy as several clipboard updates with distinct
/// sequence numbers; sequence-based deduplication cannot collapse those, so
/// this gate compares content fingerprints inside a short window instead.
/// Only the latest accepted fingerprint is remembered; batch imports and
/// external drops never consult it, and a failed save records nothing so the
/// next identical copy is retried.
/// </summary>
public sealed class CaptureDeduplicationGate
{
    internal static readonly TimeSpan DefaultDuplicateWindow = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _duplicateWindow;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _lock = new();
    private CapturedContentFingerprint? _lastAccepted;
    private DateTimeOffset _lastAcceptedAt;

    public CaptureDeduplicationGate(
        TimeSpan? duplicateWindow = null,
        Func<DateTimeOffset>? clock = null)
    {
        var window = duplicateWindow ?? DefaultDuplicateWindow;
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duplicateWindow), window, "The duplicate window must be positive.");
        }

        _duplicateWindow = window;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
    }

    public bool IsRecentDuplicate(CapturedContentFingerprint fingerprint)
    {
        var now = _clock();
        lock (_lock)
        {
            if (_lastAccepted is not { } lastAccepted || lastAccepted != fingerprint)
            {
                return false;
            }

            var age = now - _lastAcceptedAt;
            return age >= TimeSpan.Zero && age <= _duplicateWindow;
        }
    }

    public void RecordAccepted(CapturedContentFingerprint fingerprint)
    {
        var now = _clock();
        lock (_lock)
        {
            _lastAccepted = fingerprint;
            _lastAcceptedAt = now;
        }
    }
}
