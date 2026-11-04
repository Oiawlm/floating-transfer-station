namespace FloatingTransferStation.Services;

public interface IDataDirectorySettings
{
    string? ReadDataDirectory();
}

/// <summary>
/// 受管数据根的形状规则（单一事实来源，Core 层）：唯一受管形状＝
/// <c>&lt;所选&gt;/悬浮中转站</c>——用户所选目录 <c>D</c> 直下的「悬浮中转站」文件夹
/// 本身即数据根，全部数据文件（board/settings/preferences/images/reviews/plugins…）
/// 直接放在里面，插件目录结构性内嵌为 <c>数据根\plugins</c>。
/// 两个历史形状只作「待迁移旧形」识别（读取与迁移源，绝不再产生）：
/// <list type="bullet">
/// <item>旧直下形状 <c>&lt;所选&gt;/Data</c>。</item>
/// <item>旧嵌套形状 <c>&lt;所选&gt;/悬浮中转站/Data</c>。</item>
/// </list>
/// 启动时检测到旧形登记即经既有原子管线迁到同父下「悬浮中转站」文件夹；迁移失败
/// 保旧形状继续运行。该形状同时是「安全卸载边界」的删除守卫基础——应用与卸载器
/// 只删除登记过且形状属实的目录，两侧判定必须保持一致（安装器侧对应
/// IsManagedDataDirectory）。
/// </summary>
public static class DataDirectorySettings
{
    private const string LegacyDirectDataLeaf = "Data";
    private const string ProbeFilePrefix = ".write-probe-";

    /// <summary>
    /// 规范化并校验唯一受管形状 <c>&lt;所选&gt;/悬浮中转站</c>：完全限定、非设备路径、
    /// 叶子名恰为产品名、父目录为具名目录（非盘符根/UNC 根）。形状不符返回 null。
    /// 与安装器 IsManagedDataDirectory 的判定保持一致。旧形状登记不在此接受，
    /// 用 <see cref="NormalizeRegisteredDataDirectory"/>。
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

            var full = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
            var parent = Directory.GetParent(full);
            if (parent is null || !IsProductLeaf(Path.GetFileName(full)) || IsRootDirectory(parent.FullName))
            {
                // 父＝用户所选目录：具名目录才可作数据根的父（C:\悬浮中转站 拒绝），
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
    /// 识别两个历史形状（待迁移旧形）：<c>&lt;所选&gt;/Data</c>（所选目录非根）与
    /// <c>&lt;所选&gt;/悬浮中转站/Data</c>（所选目录非根）。仅用于旧登记读取与
    /// 迁移源；新代码不得再产生这两种形状。
    /// </summary>
    public static string? NormalizeLegacyDataDirectory(string? candidate)
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

            var full = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetFileName(full), LegacyDirectDataLeaf, PathComparison))
            {
                return null;
            }

            var parent = Directory.GetParent(full);
            if (parent is null)
            {
                return null;
            }

            if (IsProductLeaf(parent.Name))
            {
                // 旧嵌套形状：Data 的父是「悬浮中转站」中间层，且所选目录（中间层
                // 的上级）不能是文件系统根——与安装器侧拒绝对齐。
                return parent.Parent is null || IsRootDirectory(parent.Parent.FullName)
                    ? null
                    : full;
            }

            // 旧直下形状：所选目录不能是文件系统根。
            return IsRootDirectory(parent.FullName) ? null : full;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// 登记读取的统一入口：唯一受管形状优先，两个旧形状作为「待迁移旧形」接受，
    /// 其余返回 null（调用方回退默认位置）。
    /// </summary>
    public static string? NormalizeRegisteredDataDirectory(string? candidate) =>
        NormalizeManagedDataDirectory(candidate) ?? NormalizeLegacyDataDirectory(candidate);

    /// <summary>
    /// 由用户所选目录构造数据根 <c>&lt;所选&gt;/悬浮中转站</c>。先校验后构造：
    /// 所选目录形状非法属于调用方编程错误，直接抛 <see cref="ArgumentException"/>，
    /// 不返回 null 掩盖。
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
                ProductIdentity.DisplayName))
            .TrimEnd(Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// 由数据根反推用户所选目录（登记 DataParentDirectory 的语义，三形状同义）：
    /// 受管形状剥掉「悬浮中转站」叶子；旧直下形状剥掉 <c>Data</c>；旧嵌套形状剥掉
    /// <c>Data</c> 与「悬浮中转站」中间层。入参应为已通过
    /// <see cref="NormalizeRegisteredDataDirectory"/> 的路径。
    /// </summary>
    public static string GetDataParentDirectory(string dataDirectory)
    {
        var normalized = NormalizeRegisteredDataDirectory(dataDirectory)
            ?? throw new ArgumentException("路径不是有效的受管数据目录。", nameof(dataDirectory));
        var parent = Directory.GetParent(normalized)!.FullName;
        return IsProductLeaf(Path.GetFileName(parent))
            ? Directory.GetParent(parent)!.FullName
            : parent;
    }

    /// <summary>路径是否为两个历史形状之一（待迁移旧形）。</summary>
    public static bool IsLegacyManagedDataDirectory(string dataDirectory) =>
        NormalizeLegacyDataDirectory(dataDirectory) is not null;

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

    private static bool IsProductLeaf(string leafName) =>
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
