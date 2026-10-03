namespace FloatingTransferStation.Services;

public interface IDataDirectorySettings
{
    string? ReadDataDirectory();
}

/// <summary>
/// 受管数据目录的形状规则（单一事实来源，Core 层）：受管目录固定为
/// <c>&lt;任意父目录&gt;/悬浮中转站/Data</c>。该形状同时是「安全卸载边界」的
/// 删除守卫基础——应用与卸载器只删除登记过且形状属实的目录，两侧判定必须保持
/// 一致（安装器侧对应 IsManagedDataDirectory）。
/// </summary>
public static class DataDirectorySettings
{
    private const string ManagedDataLeaf = "Data";
    private const string ProbeFilePrefix = ".write-probe-";

    /// <summary>
    /// 规范化并校验受管数据目录路径：形状不符（相对路径、非受管叶子名、受管父
    /// 名称不符，或受管父的上级为文件系统根——盘符根/UNC 根）返回 null。
    /// 与安装器 IsManagedDataDirectory 的判定保持一致。
    /// </summary>
    public static string? NormalizeManagedDataDirectory(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        try
        {
            if (!Path.IsPathFullyQualified(candidate))
            {
                return null;
            }

            var comparison = PathComparison;
            var full = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
            var owner = Directory.GetParent(full);
            if (owner is null ||
                !string.Equals(Path.GetFileName(full), ManagedDataLeaf, comparison) ||
                !string.Equals(owner.Name, ProductIdentity.DisplayName, comparison))
            {
                return null;
            }

            // 受管父的上级是文件系统根（如 D:\悬浮中转站\Data 的 D:\）时不支持：
            // 与安装器 IsRootDirectory(ParentDirectory) 的拒绝对齐，避免两侧删除
            // 守卫出现应用认、卸载器不认的分叉。
            if (owner.Parent is null || IsRootDirectory(owner.Parent.FullName))
            {
                return null;
            }

            return full;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// 判断路径能否作为内容存储父目录：非空白、完全限定、非设备路径
    /// （<c>\\?\</c> / <c>\\.\</c>），且规范化后不等于自身的文件系统根
    /// （不能是盘符根或 UNC 根）。语义与安装器 ValidateDataParentDirectory 一致。
    /// </summary>
    public static bool IsValidDataParentShape(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            if (IsExtendedDevicePath(candidate) || !Path.IsPathFullyQualified(candidate))
            {
                return false;
            }

            var full = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
            return !IsRootDirectory(full);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// 由内容存储父目录构造受管数据目录 <c>&lt;父&gt;/悬浮中转站/Data</c>。
    /// 先校验后构造：父目录形状非法属于调用方编程错误，直接抛
    /// <see cref="ArgumentException"/>，不返回 null 掩盖。
    /// </summary>
    public static string BuildDataDirectory(string parentDirectory)
    {
        if (!IsValidDataParentShape(parentDirectory))
        {
            throw new ArgumentException(
                "内容存储父目录必须是有效的完整路径，且不能是磁盘或共享根目录。",
                nameof(parentDirectory));
        }

        return Path.GetFullPath(Path.Combine(
                NormalizeDirectoryPath(parentDirectory),
                ProductIdentity.DisplayName,
                ManagedDataLeaf))
            .TrimEnd(Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// 探测父目录可写性（对应安装器 ValidateDataParentDirectory 的探针语义）：
    /// 幂等创建父目录，写入唯一探针文件后立即删除。失败返回 false 并给出中文原因。
    /// 注意：探针会创建父目录本身（用户自选目录，保留无害）。
    /// </summary>
    public static bool TryProbeWritable(string parentDirectory, out string? failureReason)
    {
        failureReason = null;
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            failureReason = "内容存储父目录为空。";
            return false;
        }

        try
        {
            Directory.CreateDirectory(parentDirectory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            failureReason = $"无法创建或访问内容存储父目录：{exception.Message}";
            return false;
        }

        var probeName = $"{ProbeFilePrefix}{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..39];
        var probePath = Path.Combine(NormalizeDirectoryPath(parentDirectory), probeName);
        try
        {
            File.WriteAllText(probePath, ProductIdentity.DisplayName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failureReason = $"内容存储父目录不可写：{exception.Message}";
            return false;
        }

        try
        {
            File.Delete(probePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 探针残留不影响可写判定，交由后续清理。
        }

        return true;
    }

    /// <summary>candidate 是否位于 ancestor 之内（含相等）。两侧均为规范化完整路径时按前缀比较。</summary>
    public static bool IsPathWithin(string candidate, string ancestor, StringComparison? comparison = null)
    {
        var effective = comparison ?? PathComparison;
        try
        {
            var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
            var normalizedAncestor = Path.GetFullPath(ancestor).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(normalizedCandidate, normalizedAncestor, effective))
            {
                return true;
            }

            var prefix = normalizedAncestor + Path.DirectorySeparatorChar;
            return normalizedCandidate.StartsWith(prefix, effective);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static string NormalizeDirectoryPath(string value) =>
        Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);

    private static bool IsExtendedDevicePath(string candidate) =>
        OperatingSystem.IsWindows() &&
        (candidate.StartsWith(@"\\?\", StringComparison.Ordinal) ||
         candidate.StartsWith(@"\\.\", StringComparison.Ordinal));

    private static bool IsRootDirectory(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
            {
                return false;
            }

            return string.Equals(
                Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar),
                PathComparison);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
