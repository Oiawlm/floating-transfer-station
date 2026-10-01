using System.Buffers.Binary;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public sealed class ClipboardPayloadReader(WindowsDataImageReader? imageReader = null)
{
    private readonly WindowsDataImageReader _imageReader = imageReader ?? new WindowsDataImageReader();

    public ClipboardSnapshot ReadStable(Func<IDataObject?> getDataObject, Func<uint> getSequenceNumber)
    {
        var before = getSequenceNumber();
        var snapshot = Read(getDataObject(), before);
        if (before != getSequenceNumber())
        {
            throw new ExternalException("Clipboard changed while its data was being read.");
        }

        return snapshot;
    }

    public ClipboardSnapshot Read(IDataObject? data, uint sequenceNumber)
    {
        if (data is null || IsInternalDelivery(data) || !AllowsHistory(data))
        {
            return new ClipboardSnapshot(sequenceNumber, null, [], null);
        }

        var candidates = _imageReader.ReadCandidates(data);
        var bitmap = candidates.FirstOrDefault(candidate => candidate.IsBitmap)?.Bitmap;
        var encoded = candidates.Where(candidate => !candidate.IsBitmap).ToArray();
        IReadOnlyList<string> files = [];
        if (data.GetDataPresent(DataFormats.FileDrop, autoConvert: true))
        {
            files = data.GetData(DataFormats.FileDrop, autoConvert: true) switch
            {
                string[] paths => paths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray(),
                StringCollection paths => paths.Cast<string>().Where(path => !string.IsNullOrWhiteSpace(path)).ToArray(),
                _ => []
            };
        }

        var text = data.GetDataPresent(DataFormats.UnicodeText, autoConvert: true)
            ? data.GetData(DataFormats.UnicodeText, autoConvert: true) as string
            : null;
        return new ClipboardSnapshot(sequenceNumber, bitmap, files, text, encoded);
    }

    /// <summary>
    /// 应用自身的卡片复制携带内部条目格式（与拖出负载同源）:这类剪贴板内容是
    /// 交付副本而非新采集,自动收集必须跳过,否则复制旧卡片会凭空生成重复条目
    /// （条目通常早已超出与最近采集的 5 秒去重窗口）。
    /// </summary>
    private static bool IsInternalDelivery(IDataObject data)
    {
        try
        {
            return data.GetDataPresent(DragPayloadService.InternalItemIdFormat, autoConvert: false) ||
                data.GetDataPresent(DragPayloadService.InternalItemIdsFormat, autoConvert: false);
        }
        catch
        {
            // 无法确认格式时按非内部内容处理,保留正常采集路径。
            return false;
        }
    }

    private static bool AllowsHistory(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing", autoConvert: false))
            {
                return false;
            }

            if (!data.GetDataPresent("CanIncludeInClipboardHistory", autoConvert: false))
            {
                return true;
            }

            return data.GetData("CanIncludeInClipboardHistory", autoConvert: false) switch
            {
                byte[] bytes when bytes.Length >= sizeof(uint) => BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 1,
                Stream stream => ReadHistoryFlag(stream) == 1,
                uint value => value == 1,
                int value => value == 1,
                _ => false
            };
        }
        catch
        {
            // If an advertised privacy flag cannot be read, do not retain the content.
            return false;
        }
    }

    private static uint ReadHistoryFlag(Stream stream)
    {
        var position = stream.CanSeek ? stream.Position : 0;
        try
        {
            if (stream.CanSeek)
            {
                stream.Position = 0;
            }

            Span<byte> value = stackalloc byte[sizeof(uint)];
            stream.ReadExactly(value);
            return BinaryPrimitives.ReadUInt32LittleEndian(value);
        }
        finally
        {
            if (stream.CanSeek)
            {
                stream.Position = position;
            }
        }
    }
}
