using System.Globalization;
using System.IO;
using System.Security;
using System.Text.Json;

namespace FloatingTransferStation.IconTool;

internal sealed class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            CliOptions options = ParseArguments(args);

            // 规格文件是唯一事实源：任何输出路径都不得指向它。
            if (SamePath(options.SpecPath, options.IcoPath))
            {
                throw new ArgumentException("--ico 输出路径不得与 --spec 规格文件相同。");
            }

            IconSpec spec = IconSpec.Load(options.SpecPath);

            if (options.PreviewDirectory is not null)
            {
                EnsureNoPreviewNameConflict(options, spec);
            }

            IReadOnlyList<RenderedIconFrame> frames = IconRenderer.Render(spec);
            byte[] icoBytes = IconWriter.Encode(frames);

            // 所有校验、渲染和编码均在创建目录或写文件之前完成。
            IReadOnlyList<PreviewFile> previews = options.PreviewDirectory is null
                ? Array.Empty<PreviewFile>()
                : IconRenderer.CreatePreviews(frames);

            string? icoDirectory = Path.GetDirectoryName(options.IcoPath);
            if (!string.IsNullOrEmpty(icoDirectory))
            {
                Directory.CreateDirectory(icoDirectory);
            }

            File.WriteAllBytes(options.IcoPath, icoBytes);

            if (options.PreviewDirectory is string previewDirectory)
            {
                Directory.CreateDirectory(previewDirectory);

                foreach (PreviewFile preview in previews)
                {
                    File.WriteAllBytes(
                        Path.Combine(previewDirectory, preview.FileName),
                        preview.Bytes);
                }
            }

            string sizes = string.Join(
                ", ",
                frames.Select(frame =>
                    frame.Size.ToString(CultureInfo.InvariantCulture)));

            Console.WriteLine(
                $"ICO 已生成：帧 [{sizes}]，" +
                $"{icoBytes.Length.ToString(CultureInfo.InvariantCulture)} 字节。");

            return 0;
        }
        catch (IconSpecValidationException exception)
        {
            Console.Error.WriteLine($"规格错误：{exception.Message}");
            return 1;
        }
        catch (JsonException exception)
        {
            Console.Error.WriteLine($"JSON 格式错误：{exception.Message}");
            return 1;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"参数或数据错误：{exception.Message}");
            return 1;
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"IO 错误：{exception.Message}");
            return 2;
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.Error.WriteLine($"IO 访问错误：{exception.Message}");
            return 2;
        }
        catch (SecurityException exception)
        {
            Console.Error.WriteLine($"IO 权限错误：{exception.Message}");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"生成失败：{exception.Message}");
            return 1;
        }
    }

    private static CliOptions ParseArguments(string[] args)
    {
        string? specPath = null;
        string? icoPath = null;
        string? previewDirectory = null;

        HashSet<string> seen = new(StringComparer.Ordinal);

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];

            if (argument != "--spec" &&
                argument != "--ico" &&
                argument != "--preview")
            {
                throw new ArgumentException($"未知参数：'{argument}'。");
            }

            if (!seen.Add(argument))
            {
                throw new ArgumentException($"参数 '{argument}' 不得重复。");
            }

            if (index + 1 >= args.Length ||
                string.IsNullOrWhiteSpace(args[index + 1]) ||
                args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"参数 '{argument}' 缺少路径值。");
            }

            string value = args[++index];

            switch (argument)
            {
                case "--spec":
                    specPath = value;
                    break;

                case "--ico":
                    icoPath = value;
                    break;

                case "--preview":
                    previewDirectory = value;
                    break;
            }
        }

        if (specPath is null || icoPath is null)
        {
            throw new ArgumentException(
                "必须提供 --spec <app-icon.json 路径> " +
                "--ico <输出 .ico 路径> [--preview <输出目录>]。");
        }

        return new CliOptions(
            Path.GetFullPath(specPath),
            Path.GetFullPath(icoPath),
            previewDirectory is null
                ? null
                : Path.GetFullPath(previewDirectory));
    }

    private static void EnsureNoPreviewNameConflict(CliOptions options, IconSpec spec)
    {
        IEnumerable<string> previewNames = spec.Frames
            .Select(frame => $"app-icon-{frame.Size.ToString(CultureInfo.InvariantCulture)}.png")
            .Append("preview-sheet.png");

        foreach (string name in previewNames)
        {
            string previewPath = Path.Combine(options.PreviewDirectory, name);

            if (SamePath(previewPath, options.SpecPath) ||
                SamePath(previewPath, options.IcoPath))
            {
                throw new ArgumentException(
                    $"--preview 产物 '{name}' 与 --spec 或 --ico 路径冲突。");
            }
        }
    }

    private static bool SamePath(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record CliOptions(
        string SpecPath,
        string IcoPath,
        string? PreviewDirectory);
}
