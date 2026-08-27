using System.Collections.Specialized;
using System.Windows;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public sealed class ExternalDropPayloadReader
{
    private readonly WindowsDataImageReader _imageReader;

    public ExternalDropPayloadReader(WindowsDataImageReader? imageReader = null)
    {
        _imageReader = imageReader ?? new WindowsDataImageReader();
    }

    public bool CanRead(IDataObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!HasNoInternalIdentity(data))
        {
            return false;
        }

        if (!TryGetDataPresent(data, DataFormats.FileDrop, autoConvert: true, out var hasFileDrop))
        {
            return false;
        }

        if (hasFileDrop)
        {
            return TryReadImageFiles(data, out _);
        }

        return _imageReader.CanRead(data) || CanReadText(data);
    }

    public ExternalDropPayload? Read(IDataObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!HasNoInternalIdentity(data))
        {
            return null;
        }

        if (!TryGetDataPresent(data, DataFormats.FileDrop, autoConvert: true, out var hasFileDrop))
        {
            return null;
        }

        if (hasFileDrop)
        {
            return TryReadImageFiles(data, out var paths)
                ? new ExternalDropPayload.ImageFiles(paths)
                : null;
        }

        var candidates = _imageReader.ReadCandidates(data);
        if (candidates.Count > 0)
        {
            return new ExternalDropPayload.ImageCandidates(candidates);
        }

        return ReadText(data);
    }

    private static bool HasNoInternalIdentity(IDataObject data) =>
        TryGetDataPresent(
            data,
            DragPayloadService.InternalItemIdFormat,
            autoConvert: false,
            out var hasSingleIdentity) &&
        !hasSingleIdentity &&
        TryGetDataPresent(
            data,
            DragPayloadService.InternalItemIdsFormat,
            autoConvert: false,
            out var hasBatchIdentity) &&
        !hasBatchIdentity;

    private static bool TryGetDataPresent(
        IDataObject data,
        string format,
        bool autoConvert,
        out bool isPresent)
    {
        try
        {
            isPresent = data.GetDataPresent(format, autoConvert);
            return true;
        }
        catch
        {
            isPresent = false;
            return false;
        }
    }

    private static bool TryReadImageFiles(IDataObject data, out string[] paths)
    {
        try
        {
            paths = data.GetData(DataFormats.FileDrop, autoConvert: true) switch
            {
                string[] array => array.ToArray(),
                StringCollection collection => collection.Cast<string>().ToArray(),
                IEnumerable<string> enumerable => enumerable.ToArray(),
                _ => []
            };

            return paths.Length > 0 && paths.All(ImageFileSupport.IsSupportedStaticImageFile);
        }
        catch
        {
            paths = [];
            return false;
        }
    }

    private static bool CanReadText(IDataObject data)
    {
        try
        {
            return TryReadText(data, DataFormats.UnicodeText, out _) ||
                TryReadText(data, DataFormats.Text, out _);
        }
        catch
        {
            return false;
        }
    }

    private static ExternalDropPayload.Text? ReadText(IDataObject data)
    {
        try
        {
            return TryReadText(data, DataFormats.UnicodeText, out var text) ||
                TryReadText(data, DataFormats.Text, out text)
                ? new ExternalDropPayload.Text(text)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryReadText(IDataObject data, string format, out string text)
    {
        text = string.Empty;
        if (!data.GetDataPresent(format, autoConvert: true) ||
            data.GetData(format, autoConvert: true) is not string value ||
            string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        text = value;
        return true;
    }
}
