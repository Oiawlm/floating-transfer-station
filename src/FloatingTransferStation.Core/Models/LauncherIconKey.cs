using System.Security.Cryptography;
using System.Text;

namespace FloatingTransferStation.Models;

/// <summary>
/// 启动项目标的规范化与图标缓存键（#30）：IconKey = SHA256(规范化 target)，
/// 文件名稳定（launcher-icons/{IconKey}.png）；同一目标（大小写/分隔符差异）同键。
/// </summary>
public static class LauncherIconKey
{
    /// <summary>目标规范化：文件路径取完整限定并去尾分隔符（Windows 大小写不敏感），
    /// AUMID/Url 去首尾空白。规范化结果同时是添加去重键的组成部分。</summary>
    public static string NormalizeTarget(string targetPath)
    {
        var trimmed = (targetPath ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (trimmed.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("shell:appsFolder\\", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return trimmed;
        }

        return OperatingSystem.IsWindows()
            ? normalized.ToUpperInvariant()
            : normalized;
    }

    /// <summary>图标缓存键（hex 小写）。空目标返回空串（调用方按无效处理，不落缓存）。</summary>
    public static string Compute(string targetPath)
    {
        var normalized = NormalizeTarget(targetPath);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        var hash = SHA256.HashData(Encoding.Unicode.GetBytes(normalized));
        return Convert.ToHexStringLower(hash);
    }
}
