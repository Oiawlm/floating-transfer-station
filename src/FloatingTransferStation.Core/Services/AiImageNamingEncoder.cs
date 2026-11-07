using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace FloatingTransferStation.Services;

/// <summary>
/// 「AI 识图命名」的图片缩略编码器：把受管图片编码为 DeepSeek 视觉输入用的
/// JPEG Base64 data URL。解码必经 <see cref="ImageInputLimits"/>（受限
/// Configuration 是不受信图片解码的唯一途径，契约 #50；TIFF 等未登记格式按
/// 内容魔数即拒），编码前/解码前容量闸全量生效（契约 #43）。长边等比缩至
/// ≤768px（ResizeMode.Max 只缩不放）——发送的是派生缩略副本而非原图，受管
/// 原图永不离开本机。任何 IO/解码失败返回 null 不抛：命名是尽力而为的后台
/// 加工，绝不阻塞调用方。
/// </summary>
public static class AiImageNamingEncoder
{
    private const int MaxLongEdge = 768;
    private const int JpegQuality = 80;
    private const int MaxEncodedBytes = 1_576_576; // 1.5 MiB 防御上限（编码后 JPEG 字节）。

    /// <summary>
    /// 读取受管图片并编码为 <c>data:image/jpeg;base64,…</c>。文件不存在/超容量
    /// 预算/未登记格式/解码失败/编码后超 1.5 MiB 均返回 null。
    /// </summary>
    public static string? EncodeToJpegDataUrl(string imageAbsolutePath)
    {
        try
        {
            // 先按已知文件长度过容量闸再读字节（#43「超预算不读取」），读后再
            // 对实际字节数防御复核。
            ImageInputLimits.Default.ValidateEncodedLength(new FileInfo(imageAbsolutePath).Length);
            var bytes = File.ReadAllBytes(imageAbsolutePath);
            ImageInputLimits.Default.ValidateEncodedLength(bytes.LongLength);
            using var image = Image.Load(ImageInputLimits.Default.DecodeFirstFrame, bytes);
            ImageInputLimits.Default.ValidateDimensions(image.Width, image.Height);
            image.Mutate(context => context.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new Size(MaxLongEdge, MaxLongEdge)
            }));
            using var stream = new MemoryStream();
            image.Save(stream, new JpegEncoder { Quality = JpegQuality });
            var encoded = stream.ToArray();
            return encoded.Length > MaxEncodedBytes
                ? null
                : $"data:image/jpeg;base64,{Convert.ToBase64String(encoded)}";
        }
        catch
        {
            // 不受信解码守卫照 ImageFileSupport.IsSupportedStaticImageFile 惯例整体
            // 兜住：解码/IO 的任何失败都按「暂无法识别」放弃，不上抛。
            return null;
        }
    }
}
