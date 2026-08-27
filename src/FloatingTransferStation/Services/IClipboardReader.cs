using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public interface IClipboardReader
{
    Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default);
}
