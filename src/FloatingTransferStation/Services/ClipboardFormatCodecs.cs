using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;

namespace FloatingTransferStation.Services;

/// <summary>
/// DataObject→Win32 剪贴板格式的纯物化编解码（2026-11-16 剪贴板访问层收口，R3）：
/// 全部格式落 HGLOBAL 字节序列（GMEM_MOVEABLE 由调用方包裹），无 Stream 支撑格式、
/// 无延迟渲染——写完成即零渲染义务（进程被杀不悬任何人，内容天然存续＝
/// copy:true 等价语义）。编码/解码同文件成对，供往返测试锁定字节形状。
/// </summary>
internal static class ClipboardFormatCodecs
{
    internal const int BitmapInfoHeaderBytes = 40;
    internal const int BitmapV5HeaderBytes = 124;

    /// <summary>CF_UNICODETEXT：UTF-16 文本＋双 NUL 终止（官方允许、消费面最广的形状）。</summary>
    public static byte[] EncodeUnicodeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = new byte[(text.Length + 2) * 2];
        Encoding.Unicode.GetBytes(text, bytes);
        return bytes;
    }

    public static string DecodeUnicodeText(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var text = Encoding.Unicode.GetString(bytes);
        var terminator = text.IndexOf('\0');
        return terminator < 0 ? text : text[..terminator];
    }

    /// <summary>CF_HDROP：DROPFILES（20B，宽字符）＋逐 NUL 终止路径表＋表尾附加 NUL。</summary>
    public static byte[] EncodeFileDrop(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var builder = new StringBuilder();
        foreach (var path in paths)
        {
            builder.Append(path).Append('\0');
        }

        builder.Append('\0');
        var list = Encoding.Unicode.GetBytes(builder.ToString());
        var bytes = new byte[20 + list.Length];
        // DROPFILES：pFiles＝文件表偏移（宽字符恒 20）；pt/fNC 置零＝剪贴板语义；fWide＝1。
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), 20);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16, 4), 1);
        list.CopyTo(bytes, 20);
        return bytes;
    }

    public static string[] DecodeFileDrop(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < 20)
        {
            return [];
        }

        var offset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, 4));
        var wide = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16, 4)) != 0;
        if (offset <= 0 || offset > bytes.Length || !wide)
        {
            return [];
        }

        var list = Encoding.Unicode.GetString(bytes, offset, bytes.Length - offset);
        var paths = list.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return [.. paths.Where(path => !string.IsNullOrWhiteSpace(path))];
    }

    /// <summary>内部标记（单条）：UTF-16 字符串＋NUL 终止——读取侧只用格式在场判定
    /// （GetDataPresent(autoConvert:false)），字节形状与 WPF 落板形状一致。</summary>
    public static byte[] EncodeStringMarker(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = new byte[(value.Length + 1) * 2];
        Encoding.Unicode.GetBytes(value, bytes);
        return bytes;
    }

    public static string DecodeStringMarker(byte[] bytes) => DecodeUnicodeText(bytes);

    /// <summary>内部标记（批量）：逐 NUL 分隔的 UTF-16 串表＋表尾附加 NUL。</summary>
    public static byte[] EncodeStringListMarker(string[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var builder = new StringBuilder();
        foreach (var value in values)
        {
            builder.Append(value).Append('\0');
        }

        builder.Append('\0');
        return Encoding.Unicode.GetBytes(builder.ToString());
    }

    public static string[] DecodeStringListMarker(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var list = Encoding.Unicode.GetString(bytes);
        return [.. list.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
    }

    /// <summary>Preferred DropEffect：4 字节 DWORD（DROPEFFECT）——Shell 语义，原
    /// MemoryStream 支撑的等值物化（Stream 支撑格式清零）。</summary>
    public static byte[] EncodeDropEffect(int dropEffect) =>
        BitConverter.GetBytes(dropEffect);

    public static int DecodeDropEffect(byte[] bytes) =>
        bytes is { Length: >= 4 } ? BinaryPrimitives.ReadInt32LittleEndian(bytes) : 0;

    /// <summary>CF_DIB：BITMAPINFOHEADER（40B，BI_RGB 32bpp）＋bottom-up BGRA 像素。
    /// WPF CopyPixels 产出 top-down 行序，DIB 正高度＝bottom-up，逐行翻面。</summary>
    public static byte[] EncodeDib(BitmapSource source)
    {
        var pixels = CopyBgraPixels(source, out var width, out var height, out var stride);
        var bytes = new byte[BitmapInfoHeaderBytes + pixels.Length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), BitmapInfoHeaderBytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, 4), height);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(12, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(14, 2), 32);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20, 4), pixels.Length);
        FlipRowsTopDownToBottomUp(pixels, bytes.AsSpan(BitmapInfoHeaderBytes), height, stride);
        return bytes;
    }

    /// <summary>CF_DIBV5：BITMAPV5HEADER（124B，BI_BITFIELDS＋显式 BGRA 掩码＋sRGB）
    /// ＋bottom-up BGRA 像素——现代消费面（Office/新 Win32 应用）首选形状。</summary>
    public static byte[] EncodeDibV5(BitmapSource source)
    {
        var pixels = CopyBgraPixels(source, out var width, out var height, out var stride);
        var bytes = new byte[BitmapV5HeaderBytes + pixels.Length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), BitmapV5HeaderBytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, 4), height);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(12, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(14, 2), 32);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16, 4), 3);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20, 4), pixels.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40, 4), 0x00FF0000);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(44, 4), 0x0000FF00);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(48, 4), 0x000000FF);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(52, 4), unchecked((int)0xFF000000));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(56, 4), 0x73524742);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(108, 4), 4);
        FlipRowsTopDownToBottomUp(pixels, bytes.AsSpan(BitmapV5HeaderBytes), height, stride);
        return bytes;
    }

    /// <summary>按 DIB 头解出宽高（往返断言与诊断用，不解码像素）。</summary>
    public static (int Width, int Height) DecodeDibDimensions(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < BitmapInfoHeaderBytes)
        {
            return (0, 0);
        }

        return (
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4)));
    }

    private static byte[] CopyBgraPixels(BitmapSource source, out int width, out int height, out int stride)
    {
        var converted = source.Format == System.Windows.Media.PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        width = converted.PixelWidth;
        height = converted.PixelHeight;
        stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static void FlipRowsTopDownToBottomUp(byte[] topDown, Span<byte> destination, int height, int stride)
    {
        for (var row = 0; row < height; row++)
        {
            topDown.AsSpan(row * stride, stride).CopyTo(destination.Slice((height - 1 - row) * stride, stride));
        }
    }
}
