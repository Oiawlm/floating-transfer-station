using System.Windows;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public sealed class WpfClipboardReader : IClipboardReader
{
    private readonly ClipboardPayloadReader _payloadReader;

    public WpfClipboardReader()
        : this(new WindowsDataImageReader())
    {
    }

    internal WpfClipboardReader(WindowsDataImageReader imageReader)
    {
        _payloadReader = new ClipboardPayloadReader(imageReader);
    }

    public uint? GetSequenceNumber()
    {
        var sequence = NativeMethods.GetClipboardSequenceNumber();
        return sequence == 0 ? null : sequence;
    }

    public async Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var application = Application.Current
            ?? throw new InvalidOperationException("WPF application is not running.");
        var dispatcher = application.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            return await dispatcher.InvokeAsync(
                ReadNow,
                System.Windows.Threading.DispatcherPriority.Send,
                cancellationToken);
        }

        return ReadNow();
    }

    private ClipboardSnapshot ReadNow() =>
        _payloadReader.ReadStable(Clipboard.GetDataObject, NativeMethods.GetClipboardSequenceNumber);

}
