namespace FloatingTransferStation.Services;

public interface IAtomicTextWriter
{
    Task WriteAsync(string path, string content, CancellationToken cancellationToken = default);
}
