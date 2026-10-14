using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Media;

namespace FloatingTransferStation.IconTool;

internal sealed record ShapeGeometry(
    double X,
    double Y,
    double W,
    double H,
    double R);

internal sealed record IconFrameGeometry(
    int Size,
    ShapeGeometry Line,
    ShapeGeometry Rect);

internal sealed record IconPalette(Color Line, Color Rect);

internal sealed class IconSpecValidationException : Exception
{
    internal IconSpecValidationException(string message)
        : base(message)
    {
    }
}

internal sealed class IconSpec
{
    internal IconPalette Palette { get; }

    internal IReadOnlyList<IconFrameGeometry> Frames { get; }

    private IconSpec(
        IconPalette palette,
        IReadOnlyList<IconFrameGeometry> frames)
    {
        Palette = palette;
        Frames = frames;
    }

    internal static IconSpec Load(string path)
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllBytes(path),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });

        JsonElement root = document.RootElement;

        // 假设：拒绝未知或重复字段，尺寸来源互斥，override 键使用规范十进制表示。
        RequireProperties(
            root,
            "$",
            "canvas",
            "palette",
            "master",
            "overrides",
            "derivedSizes",
            "frames");

        int canvas = ReadSize(root.GetProperty("canvas"), "$.canvas");
        if (canvas < 16)
        {
            throw Invalid("$.canvas", "必须在 16 至 256 之间。");
        }

        JsonElement paletteElement = root.GetProperty("palette");
        RequireProperties(paletteElement, "$.palette", "line", "rect");

        IconPalette palette = new(
            ReadColor(paletteElement.GetProperty("line"), "$.palette.line"),
            ReadColor(paletteElement.GetProperty("rect"), "$.palette.rect"));

        JsonElement masterElement = root.GetProperty("master");
        RequireProperties(masterElement, "$.master", "line", "rect");

        ShapeGeometry masterLine = ReadShapeObject(
            masterElement.GetProperty("line"),
            "$.master.line");

        ShapeGeometry masterRect = ReadShapeObject(
            masterElement.GetProperty("rect"),
            "$.master.rect");

        IconFrameGeometry master = new(canvas, masterLine, masterRect);
        ValidateGeometry(master, "$.master", isOverride: false);

        JsonElement overridesElement = root.GetProperty("overrides");
        RequireObject(overridesElement, "$.overrides");

        Dictionary<int, IconFrameGeometry> resolved = new()
        {
            [canvas] = master
        };

        HashSet<string> overrideKeys = new(StringComparer.Ordinal);

        foreach (JsonProperty property in overridesElement.EnumerateObject())
        {
            string context = $"$.overrides['{property.Name}']";

            if (!overrideKeys.Add(property.Name))
            {
                throw Invalid(context, "尺寸键不得重复。");
            }

            if (!int.TryParse(
                    property.Name,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int size) ||
                size < 1 ||
                size > 256 ||
                property.Name != size.ToString(CultureInfo.InvariantCulture))
            {
                throw Invalid(context, "尺寸键必须是 1 至 256 的规范正整数字符串。");
            }

            if (resolved.ContainsKey(size))
            {
                throw Invalid(context, "与 canvas 或其他几何来源冲突。");
            }

            RequireProperties(property.Value, context, "line", "rect");

            IconFrameGeometry frame = new(
                size,
                ReadShapeArray(
                    property.Value.GetProperty("line"),
                    $"{context}.line"),
                ReadShapeArray(
                    property.Value.GetProperty("rect"),
                    $"{context}.rect"));

            ValidateGeometry(frame, context, isOverride: true);
            resolved.Add(size, frame);
        }

        int[] derivedSizes = ReadSizes(
            root.GetProperty("derivedSizes"),
            "$.derivedSizes",
            requireNonempty: false);

        foreach (int size in derivedSizes)
        {
            string context =
                $"$.derivedSizes[{size.ToString(CultureInfo.InvariantCulture)}]";

            if (resolved.ContainsKey(size))
            {
                throw Invalid(context, "与 canvas、override 或其他派生尺寸冲突。");
            }

            IconFrameGeometry frame = Derive(size, canvas, master);
            ValidateGeometry(frame, context, isOverride: false);
            resolved.Add(size, frame);
        }

        int[] frameSizes = ReadSizes(
            root.GetProperty("frames"),
            "$.frames",
            requireNonempty: true);

        HashSet<int> frameSet = new(frameSizes);

        foreach (int size in frameSizes)
        {
            if (!resolved.ContainsKey(size))
            {
                throw Invalid(
                    "$.frames",
                    $"尺寸 {size.ToString(CultureInfo.InvariantCulture)} " +
                    "没有 master、override 或 derivedSizes 几何来源。");
            }
        }

        foreach (int size in resolved.Keys.OrderBy(value => value))
        {
            if (!frameSet.Contains(size))
            {
                throw Invalid(
                    "$.frames",
                    $"几何来源尺寸 {size.ToString(CultureInfo.InvariantCulture)} " +
                    "未包含在 frames 中。");
            }
        }

        Array.Sort(frameSizes);

        IconFrameGeometry[] snapshots = frameSizes
            .Select(size => resolved[size])
            .ToArray();

        return new IconSpec(palette, Array.AsReadOnly(snapshots));
    }

    private static IconFrameGeometry Derive(
        int size,
        int canvas,
        IconFrameGeometry master)
    {
        double scale = (double)size / canvas;

        double lineX = Round(master.Line.X * scale);
        double lineY = Round(master.Line.Y * scale);
        double lineW = Round(master.Line.W * scale);
        double lineH = Round(master.Line.H * scale);

        double rectX = Round(master.Rect.X * scale);
        double rectY = Round(master.Rect.Y * scale);
        double rectW = Round(master.Rect.W * scale);
        double rectH = Round(master.Rect.H * scale);

        lineY = Round((size - lineH) / 2.0);
        rectY = Round((size - rectH) / 2.0);
        rectX = lineX - rectW;

        return new IconFrameGeometry(
            size,
            new ShapeGeometry(lineX, lineY, lineW, lineH, lineW / 2.0),
            new ShapeGeometry(rectX, rectY, rectW, rectH, 0));
    }

    private static double Round(double value)
    {
        return Math.Round(value, MidpointRounding.AwayFromZero);
    }

    private static void ValidateGeometry(
        IconFrameGeometry frame,
        string context,
        bool isOverride)
    {
        ShapeGeometry line = frame.Line;
        ShapeGeometry rect = frame.Rect;
        int size = frame.Size;

        ValidateShape(line, size, $"{context}.line");
        ValidateShape(rect, size, $"{context}.rect");

        if (!(rect.W > 0 && rect.W < line.H))
        {
            throw Invalid(context, "必须满足 0 < rect.w < line.h。");
        }

        if (!(rect.H > rect.W))
        {
            throw Invalid(context, "必须满足 rect.h > rect.w。");
        }

        if (rect.X + rect.W != line.X)
        {
            throw Invalid(context, "必须满足 rect.x + rect.w == line.x。");
        }

        if (Math.Abs(2.0 * line.Y + line.H - size) > 1.0)
        {
            throw Invalid(context, "line 未满足垂直居中 ±1px。");
        }

        if (Math.Abs(2.0 * rect.Y + rect.H - size) > 1.0)
        {
            throw Invalid(context, "rect 未满足垂直居中 ±1px。");
        }

        if (rect.R != 0)
        {
            throw Invalid($"{context}.rect.r", "必须为 0。");
        }

        // 假设：master 使用胶囊端帽；方形端帽仅允许用于 16px override。
        bool squareCapAllowed = isOverride && size == 16;

        if (line.R != line.W / 2.0 &&
            !(squareCapAllowed && line.R == 0))
        {
            throw Invalid(
                $"{context}.line.r",
                squareCapAllowed
                    ? "必须为 0 或 line.w / 2。"
                    : "必须为 line.w / 2；方形端帽仅允许用于 16px override。");
        }

        if (line.R > line.H / 2.0)
        {
            throw Invalid(
                $"{context}.line.r",
                "半径不得超过 line.h / 2，以保证胶囊端帽不被 WPF 截断。");
        }
    }

    private static void ValidateShape(
        ShapeGeometry shape,
        int size,
        string context)
    {
        if (!double.IsFinite(shape.X) ||
            !double.IsFinite(shape.Y) ||
            !double.IsFinite(shape.W) ||
            !double.IsFinite(shape.H) ||
            !double.IsFinite(shape.R))
        {
            throw Invalid(context, "所有几何字段必须是有限数字。");
        }

        if (shape.W <= 0 || shape.H <= 0)
        {
            throw Invalid(context, "w 和 h 必须严格大于 0。");
        }

        if (shape.R < 0)
        {
            throw Invalid(context, "r 不得为负数。");
        }

        if (shape.X < 0 ||
            shape.Y < 0 ||
            shape.X + shape.W > size ||
            shape.Y + shape.H > size)
        {
            throw Invalid(
                context,
                $"四边必须位于 [0, {size.ToString(CultureInfo.InvariantCulture)}] 内。");
        }
    }

    private static ShapeGeometry ReadShapeObject(
        JsonElement element,
        string context)
    {
        RequireProperties(element, context, "x", "y", "w", "h", "r");

        return new ShapeGeometry(
            ReadNumber(element.GetProperty("x"), $"{context}.x"),
            ReadNumber(element.GetProperty("y"), $"{context}.y"),
            ReadNumber(element.GetProperty("w"), $"{context}.w"),
            ReadNumber(element.GetProperty("h"), $"{context}.h"),
            ReadNumber(element.GetProperty("r"), $"{context}.r"));
    }

    private static ShapeGeometry ReadShapeArray(
        JsonElement element,
        string context)
    {
        if (element.ValueKind != JsonValueKind.Array ||
            element.GetArrayLength() != 5)
        {
            throw Invalid(context, "必须是恰好包含 [x,y,w,h,r] 的五元素数组。");
        }

        return new ShapeGeometry(
            ReadNumber(element[0], $"{context}[0]"),
            ReadNumber(element[1], $"{context}[1]"),
            ReadNumber(element[2], $"{context}[2]"),
            ReadNumber(element[3], $"{context}[3]"),
            ReadNumber(element[4], $"{context}[4]"));
    }

    private static double ReadNumber(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Number ||
            !element.TryGetDouble(out double value) ||
            !double.IsFinite(value))
        {
            throw Invalid(context, "必须是有限数字。");
        }

        return value;
    }

    private static int ReadSize(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt32(out int size) ||
            size < 1 ||
            size > 256)
        {
            throw Invalid(context, "必须是 1 至 256 的整数。");
        }

        return size;
    }

    private static int[] ReadSizes(
        JsonElement element,
        string context,
        bool requireNonempty)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Invalid(context, "必须是整数数组。");
        }

        if (requireNonempty && element.GetArrayLength() == 0)
        {
            throw Invalid(context, "不得为空。");
        }

        List<int> values = new();
        HashSet<int> seen = new();
        int index = 0;

        foreach (JsonElement item in element.EnumerateArray())
        {
            int size = ReadSize(
                item,
                $"{context}[{index.ToString(CultureInfo.InvariantCulture)}]");

            if (!seen.Add(size))
            {
                throw Invalid(
                    context,
                    $"尺寸 {size.ToString(CultureInfo.InvariantCulture)} 不得重复。");
            }

            values.Add(size);
            index++;
        }

        return values.ToArray();
    }

    private static Color ReadColor(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw Invalid(context, "必须是 #RRGGBB 字符串。");
        }

        string? text = element.GetString();

        if (text is null ||
            text.Length != 7 ||
            text[0] != '#' ||
            !byte.TryParse(
                text.AsSpan(1, 2),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out byte red) ||
            !byte.TryParse(
                text.AsSpan(3, 2),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out byte green) ||
            !byte.TryParse(
                text.AsSpan(5, 2),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out byte blue))
        {
            throw Invalid(context, "必须是合法的六位十六进制颜色 #RRGGBB。");
        }

        return Color.FromRgb(red, green, blue);
    }

    private static void RequireObject(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(context, "必须是 JSON 对象。");
        }
    }

    private static void RequireProperties(
        JsonElement element,
        string context,
        params string[] expectedProperties)
    {
        RequireObject(element, context);

        HashSet<string> expected = new(
            expectedProperties,
            StringComparer.Ordinal);

        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw Invalid(context, $"字段 '{property.Name}' 不得重复。");
            }

            if (!expected.Contains(property.Name))
            {
                throw Invalid(context, $"未知字段 '{property.Name}'。");
            }
        }

        foreach (string name in expectedProperties)
        {
            if (!seen.Contains(name))
            {
                throw Invalid(context, $"缺少必需字段 '{name}'。");
            }
        }
    }

    private static IconSpecValidationException Invalid(
        string context,
        string message)
    {
        return new IconSpecValidationException($"{context}: {message}");
    }
}
