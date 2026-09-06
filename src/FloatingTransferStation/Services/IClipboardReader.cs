using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public interface IClipboardReader
{
    uint? GetSequenceNumber() => null;
    Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default);
}
