using System.Windows;
using System.Windows.Media.Imaging;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public sealed class WpfClipboardReader : IClipboardReader
{
    private readonly WindowsDataImageReader _imageReader;

    public WpfClipboardReader()
        : this(new WindowsDataImageReader())
    {
    }

    internal WpfClipboardReader(WindowsDataImageReader imageReader)
    {
        _imageReader = imageReader;
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

    private ClipboardSnapshot ReadNow()
    {
        BitmapSource? image = null;
        IReadOnlyList<string> files = [];
        string? text = null;
        var dataObject = Clipboard.GetDataObject();
        var imageCandidates = dataObject is null ? [] : _imageReader.ReadCandidates(dataObject);
        image = imageCandidates.FirstOrDefault(candidate => candidate.IsBitmap)?.Bitmap;
        var encodedImages = imageCandidates.Where(candidate => !candidate.IsBitmap).ToArray();

        if (Clipboard.ContainsFileDropList())
        {
            files = Clipboard.GetFileDropList().Cast<string>().ToArray();
        }

        if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
        {
            text = Clipboard.GetText(TextDataFormat.UnicodeText);
        }

        var sequence = NativeMethods.GetClipboardSequenceNumber();
        return new ClipboardSnapshot(sequence, image, files, text, encodedImages);
    }

}
