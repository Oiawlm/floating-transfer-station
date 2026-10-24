namespace FloatingTransferStation.Services;

public interface IDataDirectorySettings
{
    string? ReadDataDirectory();
}

/// <summary>
/// 受管数据目录的形状规则（单一事实来源，Core 层，契约 #47）：双形状并存，
/// 判别符＝<c>Data</c> 直接父目录的叶子名恰为产品名。
/// <list type="bullet">
/// <item>新形状（直下形状）<c>&lt;所选&gt;/Data</c>：本版本起所有新选择（安装器向导
/// 与设置窗）在所选目录直下创建并管理 <c>Data</c>。</item>
/// <item>旧形状（嵌套形状）<c>&lt;所选&gt;/悬浮中转站/Data</c>：仅存量登记继续有效，
/// 完全可读、可搬迁、可卸载，绝不自动强制搬迁。</item>
/// </list>
/// 该形状同时是「安全卸载边界」的删除守卫基础——应用与卸载器只删除登记过且形状
/// 属实的目录，两侧判定必须保持一致（安装器侧对应 IsManagedDataDirectory）。
/// </summary>
public static class DataDirectorySettings
{
    private const string ManagedDataLeaf = "Data";
    private const string ProbeFilePrefix = ".write-probe-";

    /// <summary>
    /// 规范化并校验受管数据目录路径（双形状，契约 #47）。形状不符（相对路径、
    /// 非 <c>Data</c> 叶子、父目录为文件系统根——盘符根/UNC 根；旧形状另要求
    /// 「悬浮中转站」中间层的父目录非根）返回 null。与安装器 IsManagedDataDirectory
    /// 的判定保持一致。
    /// </summary>
    public static string? NormalizeManagedDataDirectory(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        try
        {
            if (IsExtendedDevicePath(candidate) || !Path.IsPathFullyQualified(candidate))
            {
                return null;
            }

            var comparison = PathComparison;
            var full = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
            var parent = Directory.GetParent(full);
            if (parent is null || !string.Equals(Path.GetFileName(full), ManagedDataLeaf, comparison))
            {
                return null;
            }

            if (IsLegacyLeaf(parent.Name))
            {
                // 旧形状（嵌套形状）：Data 的父必须是「悬浮中转站」中间层，且中间层
                // 的上级不能是文件系统根——与安装器 IsRootDirectory(ParentDirectory)
                // 的拒绝对齐，避免两侧删除守卫出现应用认、卸载器不认的分叉。
                if (parent.Parent is null || IsRootDirectory(parent.Parent.FullName))
                {
                    return null;
                }
            }
            else if (IsRootDirectory(parent.FullName))
            {
                // 新形状（直下形状）：<所选> 不能是盘符根/UNC 根（D:\Data 拒绝），
                // 保证递归删除目标永远位于某个具名目录之内。
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
    /// 由用户所选目录构造受管数据目录 <c>&lt;所选&gt;/Data</c>（新形状，契约 #47）。
    /// 先校验后构造：所选目录形状非法属于调用方编程错误，直接抛
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
                ManagedDataLeaf))
            .TrimEnd(Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// 由受管数据目录反推用户所选目录（登记 DataParentDirectory 的语义，两形状同义）：
    /// 旧形状剥掉 <c>Data</c> 与「悬浮中转站」中间层，新形状只剥 <c>Data</c>。
    /// 入参应为已通过 <see cref="NormalizeManagedDataDirectory"/> 的路径。
    /// </summary>
    public static string GetDataParentDirectory(string dataDirectory)
    {
        var normalized = NormalizeManagedDataDirectory(dataDirectory)
            ?? throw new ArgumentException("路径不是有效的受管数据目录。", nameof(dataDirectory));
        var parent = Directory.GetParent(normalized)!.FullName;
        return IsLegacyLeaf(Path.GetFileName(parent))
            ? Directory.GetParent(parent)!.FullName
            : parent;
    }

    /// <summary>Data 的直接父目录是否为旧形状（嵌套形状）的「悬浮中转站」中间层。</summary>
    public static bool IsLegacyManagedDataDirectory(string dataDirectory)
    {
        var normalized = NormalizeManagedDataDirectory(dataDirectory);
        return normalized is not null &&
            IsLegacyLeaf(Path.GetFileName(Directory.GetParent(normalized)!.FullName));
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

            // 直接用未去尾的规范化路径判根："C:\" 与 "/" 是根，去掉尾分隔符会失真。
            var full = Path.GetFullPath(candidate);
            return !IsRootDirectory(full);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
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

    private static bool IsLegacyLeaf(string leafName) =>
        string.Equals(leafName, ProductIdentity.DisplayName, PathComparison);

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
            // 不去尾部分隔符：Windows 的 "C:\" 与 Unix 的 "/" 本身就是根，
            // 截去分隔符会让根目录变成 "C:"/""，根判定随之失真。
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            return !string.IsNullOrEmpty(root) &&
                string.Equals(full, root, PathComparison);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
