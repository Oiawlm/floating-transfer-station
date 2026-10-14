using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FloatingTransferStation.IconTool;

internal sealed record RenderedIconFrame(
    int Size,
    BitmapSource Bitmap,
    byte[] Pbgra32Pixels,
    byte[] Bgra32Pixels);

internal sealed record PreviewFile(string FileName, byte[] Bytes);

internal sealed class IconRenderer
{
    internal static IReadOnlyList<RenderedIconFrame> Render(IconSpec spec)
    {
        SolidColorBrush lineBrush = new(spec.Palette.Line);
        SolidColorBrush rectBrush = new(spec.Palette.Rect);
        lineBrush.Freeze();
        rectBrush.Freeze();

        List<RenderedIconFrame> frames = new(spec.Frames.Count);

        foreach (IconFrameGeometry geometry in spec.Frames)
        {
            DrawingVisual visual = new();

            using (DrawingContext context = visual.RenderOpen())
            {
                DrawShape(context, geometry.Rect, rectBrush);
                DrawShape(context, geometry.Line, lineBrush);
            }

            RenderTargetBitmap bitmap = new(
                geometry.Size,
                geometry.Size,
                96,
                96,
                PixelFormats.Pbgra32);

            bitmap.Render(visual);
            bitmap.Freeze();

            int stride = checked(geometry.Size * 4);
            byte[] premultiplied = new byte[checked(stride * geometry.Size)];
            bitmap.CopyPixels(premultiplied, stride, 0);

            frames.Add(new RenderedIconFrame(
                geometry.Size,
                bitmap,
                premultiplied,
                ConvertToStraightBgra(premultiplied)));
        }

        return frames.AsReadOnly();
    }

    internal static byte[] EncodePng(BitmapSource bitmap)
    {
        PngBitmapEncoder encoder = new()
        {
            Interlace = PngInterlaceOption.Off
        };

        // RenderTargetBitmap 无时间戳或外部元数据；直接交给 WPF 处理预乘 alpha。
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using MemoryStream stream = new();
        encoder.Save(stream);
        return stream.ToArray();
    }

    internal static IReadOnlyList<PreviewFile> CreatePreviews(
        IReadOnlyList<RenderedIconFrame> frames)
    {
        if (frames.Count == 0)
        {
            throw new ArgumentException("预览帧集不得为空。", nameof(frames));
        }

        RenderedIconFrame[] ordered = frames
            .OrderBy(frame => frame.Size)
            .ToArray();

        List<PreviewFile> files = new(ordered.Length + 1);

        foreach (RenderedIconFrame frame in ordered)
        {
            string name = "app-icon-" +
                frame.Size.ToString(CultureInfo.InvariantCulture) +
                ".png";

            files.Add(new PreviewFile(name, EncodePng(frame.Bitmap)));
        }

        files.Add(new PreviewFile(
            "preview-sheet.png",
            EncodePng(RenderPreviewSheet(ordered))));

        return files.AsReadOnly();
    }

    private static void DrawShape(
        DrawingContext context,
        ShapeGeometry shape,
        Brush brush)
    {
        RectangleGeometry rectangle = new(
            new Rect(shape.X, shape.Y, shape.W, shape.H),
            shape.R,
            shape.R);

        rectangle.Freeze();

        // 96 DPI 下整数坐标即像素边界；仅给整数边界添加吸附线，保留小数几何。
        GuidelineSet guidelines = new();
        AddIntegralGuideline(guidelines.GuidelinesX, shape.X);
        AddIntegralGuideline(guidelines.GuidelinesX, shape.X + shape.W);
        AddIntegralGuideline(guidelines.GuidelinesY, shape.Y);
        AddIntegralGuideline(guidelines.GuidelinesY, shape.Y + shape.H);
        guidelines.Freeze();

        context.PushGuidelineSet(guidelines);
        context.DrawGeometry(brush, null, rectangle);
        context.Pop();
    }

    private static void AddIntegralGuideline(
        DoubleCollection guidelines,
        double value)
    {
        if (value == Math.Truncate(value))
        {
            guidelines.Add(value);
        }
    }

    private static byte[] ConvertToStraightBgra(byte[] premultiplied)
    {
        if (premultiplied.Length % 4 != 0)
        {
            throw new ArgumentException(
                "Pbgra32 像素缓冲长度必须是 4 的倍数。",
                nameof(premultiplied));
        }

        byte[] straight = new byte[premultiplied.Length];

        for (int offset = 0; offset < premultiplied.Length; offset += 4)
        {
            byte alpha = premultiplied[offset + 3];

            if (alpha == 0)
            {
                // 新缓冲已清零，透明像素的 B、G、R、A 均保持 0。
                continue;
            }

            straight[offset + 3] = alpha;

            if (alpha == 255)
            {
                straight[offset] = premultiplied[offset];
                straight[offset + 1] = premultiplied[offset + 1];
                straight[offset + 2] = premultiplied[offset + 2];
                continue;
            }

            straight[offset] = Unpremultiply(premultiplied[offset], alpha);
            straight[offset + 1] = Unpremultiply(premultiplied[offset + 1], alpha);
            straight[offset + 2] = Unpremultiply(premultiplied[offset + 2], alpha);
        }

        return straight;
    }

    private static byte Unpremultiply(byte channel, byte alpha)
    {
        // 假设：反预乘 round 使用 AwayFromZero；非负整数运算可精确实现该舍入。
        int rounded = (channel * 255 + alpha / 2) / alpha;
        return (byte)Math.Min(255, rounded);
    }

    private static RenderTargetBitmap RenderPreviewSheet(
        IReadOnlyList<RenderedIconFrame> frames)
    {
        const int margin = 8;
        const int gap = 8;

        int totalIconWidth = frames.Sum(frame => frame.Size);
        int maximumSize = frames.Max(frame => frame.Size);

        int bandWidth = checked(
            margin * 2 + totalIconWidth + gap * (frames.Count - 1));

        int bandHeight = checked(maximumSize + margin * 2);
        int width = checked(bandWidth + margin * 2);
        int height = checked(bandHeight * 2 + margin * 3);

        SolidColorBrush lightBackground = new(Color.FromRgb(0xF7, 0xF7, 0xF7));
        SolidColorBrush darkBackground = new(Color.FromRgb(0x26, 0x26, 0x26));
        lightBackground.Freeze();
        darkBackground.Freeze();

        DrawingVisual visual = new();
        RenderOptions.SetBitmapScalingMode(
            visual,
            BitmapScalingMode.NearestNeighbor);

        using (DrawingContext context = visual.RenderOpen())
        {
            DrawPreviewBand(
                context,
                frames,
                margin,
                margin,
                bandWidth,
                bandHeight,
                margin,
                gap,
                lightBackground);

            DrawPreviewBand(
                context,
                frames,
                margin,
                margin * 2 + bandHeight,
                bandWidth,
                bandHeight,
                margin,
                gap,
                darkBackground);
        }

        RenderTargetBitmap bitmap = new(
            width,
            height,
            96,
            96,
            PixelFormats.Pbgra32);

        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void DrawPreviewBand(
        DrawingContext context,
        IReadOnlyList<RenderedIconFrame> frames,
        int bandX,
        int bandY,
        int bandWidth,
        int bandHeight,
        int margin,
        int gap,
        Brush background)
    {
        context.DrawRectangle(
            background,
            null,
            new Rect(bandX, bandY, bandWidth, bandHeight));

        int x = bandX + margin;

        foreach (RenderedIconFrame frame in frames)
        {
            // 假设：拼图居中允许最多半像素取整误差，以保证原生像素对齐。
            int y = bandY + (int)Math.Round(
                (bandHeight - frame.Size) / 2.0,
                MidpointRounding.AwayFromZero);

            context.DrawImage(
                frame.Bitmap,
                new Rect(x, y, frame.Size, frame.Size));

            x = checked(x + frame.Size + gap);
        }
    }
}
