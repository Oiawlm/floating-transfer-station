namespace FloatingTransferStation.Services;

/// <summary>搬迁各环节的拒绝/失败原因，编排层据此给出用户可读提示。</summary>
public enum DataDirectoryRelocationRefusal
{
    SourceShapeInvalid,
    SourceMissing,
    TargetParentShapeInvalid,
    TargetParentNotWritable,
    UnchangedPath,
    TargetExists,
    TargetInsideCurrentParent,
    TargetInsideSourceData,
    InsufficientFreeSpace,
    StageFailed,
    VerificationFailed,
    PublishFailed
}

/// <summary>搬迁协议中某一步被拒绝或失败；消息为用户可读中文。</summary>
public sealed class DataDirectoryRelocationException : Exception
{
    public DataDirectoryRelocationRefusal Reason { get; }

    public DataDirectoryRelocationException(
        DataDirectoryRelocationRefusal reason,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }
}

/// <summary>一次受管数据目录搬迁的既定方案（Plan 的输出，后续步骤的输入）。</summary>
public sealed record DataDirectoryRelocationPlan(
    string SourceDataDirectory,
    string SourceParentDirectory,
    string TargetDataDirectory,
    string TargetParentDirectory,
    string StagingDirectory,
    long SourceSizeBytes);

/// <summary>暂存复制的进度快照。</summary>
public sealed record DataDirectoryCopyProgress(
    long CopiedBytes,
    long TotalBytes,
    int CopiedFiles,
    int TotalFiles);

/// <summary>
/// 应用内受管数据目录搬迁原语：与安装器 PrepareDataDirectoryMigration 同一协议——
/// 校验（形状/可写探针/目标卷空间/目标不存在）→ 复制到所选目录内的
/// <c>.migration-时间戳-序号</c> 暂存目录 → 校验（存在 + 文件长度一致）→
/// 同卷 <see cref="Directory.Move"/> 原子发布。发布前任一步失败：递归删除暂存目录；
/// 源目录与注册表不动。目标恒为新形状 <c>&lt;所选&gt;/Data</c>（契约 #47），源可为
/// 双形状任一；所选目录可预先存在且含其他内容（同级内容原样保留）。纯托管实现
/// （可单测、保留最后写入时间、空目录也创建、发布前可取消、发布后不可取消）。
/// </summary>
public sealed class DataDirectoryRelocator
{
    private const string StagingPrefix = ".migration-";
    private const long FreeSpaceMarginBytes = 64L * 1024 * 1024;
    private readonly TimeProvider _timeProvider;

    public DataDirectoryRelocator(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// 校验并制定搬迁方案（不复制、不改动源）。命中任一拒绝条件抛
    /// <see cref="DataDirectoryRelocationException"/>；探针会创建目标父目录本身。
    /// </summary>
    public DataDirectoryRelocationPlan Plan(string sourceDataDirectory, string targetParentDirectory)
    {
        var source = DataDirectorySettings.NormalizeManagedDataDirectory(sourceDataDirectory)
            ?? throw new DataDirectoryRelocationException(
                DataDirectoryRelocationRefusal.SourceShapeInvalid,
                "当前内容目录未通过受管形状校验，无法迁移。");
        var sourceParent = Directory.GetParent(source)!.FullName;
        if (!Directory.Exists(source))
        {
            throw new DataDirectoryRelocationException(
                DataDirectoryRelocationRefusal.SourceMissing,
                "当前内容目录不存在，无法迁移。");
        }

        if (!DataDirectorySettings.IsValidDataParentShape(targetParentDirectory))
        {
            throw new DataDirectoryRelocationException(
                DataDirectoryRelocationRefusal.TargetParentShapeInvalid,
                "所选文件夹必须是完整的磁盘路径，且不能是磁盘或共享根目录。");
        }

        var target = DataDirectorySettings.BuildDataDirectory(targetParentDirectory);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(target, source, comparison))
        {
            throw new DataDirectoryRelocationException(
                DataDirectoryRelocationRefusal.UnchangedPath,
                "所选位置就是当前内容目录，无需迁移。");
        }

        // 先判“目标父在源 Data 内”（自包含，复制进自己子树）：源 Data 必然也在
        // 其所在目录之内，先判更精确的自包含原因。
        if (DataDirectorySettings.IsPathWithin(targetParentDirectory, source, comparison))
        {
            throw new DataDirectoryRelocationException(
                DataDirectoryRelocationRefusal.TargetInsideSourceData,
                "不能把内容目录迁移到它自己的内部。");
        }

        if (DataDirectorySettings.IsPathWithin(target, sourceParent, comparison))
        {
            throw new DataDirectoryRelocationException(
                DataDirectoryRelocationRefusal.TargetInsideCurrentParent,
                "不能把内容目录迁移到它当前所在的目录之内。");
        }

        if (Directory.Exists(target))
        {
            throw new DataDirectoryRelocationException(
                DataDirectoryRelocationRefusal.TargetExists,
                $"目标位置已存在同名目录：{target}");
        }

        if (!DataDirectorySettings.TryProbeWritable(targetParentDirectory, out var probeFailure))
        {
            throw new DataDirectoryRelocationException(
                DataDirectoryRelocationRefusal.TargetParentNotWritable,
                probeFailure ?? "所选文件夹不可写。");
        }

        var sourceSize = MeasureSource(source);
        if (HasInsufficientFreeSpace(target, sourceSize))
        {
            throw new DataDirectoryRelocationException(
                DataDirectoryRelocationRefusal.InsufficientFreeSpace,
                "目标磁盘的可用空间不足。");
        }

        var staging = BuildStagingPath(targetParentDirectory);
        return new DataDirectoryRelocationPlan(
            source,
            sourceParent,
            target,
            NormalizeTargetParent(targetParentDirectory),
            staging,
            sourceSize);
    }

    private static string NormalizeTargetParent(string targetParentDirectory) =>
        Path.GetFullPath(targetParentDirectory).TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>
    /// 复制源目录到暂存目录（保留最后写入时间、空目录也创建、发布前可取消）。
    /// 失败或取消：递归删除暂存目录后抛出；源目录不动。
    /// </summary>
    public async Task StageAsync(
        DataDirectoryRelocationPlan plan,
        IProgress<DataDirectoryCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(plan.StagingDirectory))
        {
            throw new DataDirectoryRelocationException(
                DataDirectoryRelocationRefusal.StageFailed,
                "暂存目录已存在，无法开始复制。");
        }

        var (totalFiles, totalBytes) = CountEntries(plan.SourceDataDirectory);
        try
        {
            Directory.CreateDirectory(plan.TargetParentDirectory);
            await CopyDirectoryAsync(
                plan.SourceDataDirectory,
                plan.StagingDirectory,
                totalFiles,
                totalBytes,
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryDeleteTree(plan.StagingDirectory);
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            TryDeleteTree(plan.StagingDirectory);
            throw new DataDirectoryRelocationException(
                DataDirectoryRelocationRefusal.StageFailed,
                $"复制内容时失败：{exception.Message}",
                exception);
        }
    }

    /// <summary>
    /// 校验暂存目录与源一致：目录树逐级存在，文件存在且长度相等（比安装器只查
    /// 存在更强，防截断）。不匹配抛异常，调用方负责 Discard。
    /// </summary>
    public void VerifyStaged(DataDirectoryRelocationPlan plan)
    {
        if (!VerifyTree(plan.SourceDataDirectory, plan.StagingDirectory, out var mismatch))
        {
            throw new DataDirectoryRelocationException(
                DataDirectoryRelocationRefusal.VerificationFailed,
                $"迁移校验失败：{mismatch}");
        }
    }

    /// <summary>
    /// 原子发布：暂存目录与目标同卷（暂存建在所选目录内），Directory.Move 一次
    /// 改名到位。目标意外已存在或改名失败抛异常，暂存目录保留供重试或 Discard。
    /// </summary>
    public void Publish(DataDirectoryRelocationPlan plan)
    {
        if (!Directory.Exists(plan.StagingDirectory))
        {
            throw new DataDirectoryRelocationException(
                DataDirectoryRelocationRefusal.PublishFailed,
                "暂存目录不存在，无法发布。");
        }

        if (Directory.Exists(plan.TargetDataDirectory))
        {
            throw new DataDirectoryRelocationException(
                DataDirectoryRelocationRefusal.PublishFailed,
                $"目标位置已被占用：{plan.TargetDataDirectory}");
        }

        try
        {
            Directory.Move(plan.StagingDirectory, plan.TargetDataDirectory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new DataDirectoryRelocationException(
                DataDirectoryRelocationRefusal.PublishFailed,
                $"发布新内容目录失败：{exception.Message}",
                exception);
        }
    }

    /// <summary>
    /// 失败清理（幂等、尽力而为）：删暂存目录。所选目录是用户目录（契约 #47 新形状
    /// 下不再是应用创建的受管父），即使已空也绝不移除。
    /// </summary>
    public void Discard(DataDirectoryRelocationPlan plan)
    {
        TryDeleteTree(plan.StagingDirectory);
    }

    /// <summary>
    /// 按形状守卫删除受管数据目录（与卸载器 DeleteManagedDataDirectory 同构的安全
    /// 边界，契约 #47）：形状不符永不删除；递归删除仅限 <c>Data</c> 目录本身，
    /// 仅旧形状（嵌套「悬浮中转站」中间层）删除后尝试移除空的中间层；新形状
    /// 绝不触碰用户所选父目录。目录不存在视为成功。
    /// </summary>
    public static bool TryDeleteManagedDataDirectory(string dataDirectory, out string? failureReason)
    {
        failureReason = null;
        var normalized = DataDirectorySettings.NormalizeManagedDataDirectory(dataDirectory);
        if (normalized is null)
        {
            failureReason = "目录未通过受管形状校验，拒绝删除。";
            return false;
        }

        if (!Directory.Exists(normalized))
        {
            return true;
        }

        try
        {
            Directory.Delete(normalized, recursive: true);
            if (DataDirectorySettings.IsLegacyManagedDataDirectory(normalized))
            {
                var legacyParent = Directory.GetParent(normalized);
                legacyParent?.Delete();
            }

            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            failureReason = exception.Message;
            return false;
        }
    }

    private static long MeasureSource(string sourceDataDirectory)
    {
        long total = 0;
        var pending = new Queue<string>();
        pending.Enqueue(sourceDataDirectory);
        while (pending.Count > 0)
        {
            var directory = pending.Dequeue();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (Directory.Exists(entry))
                {
                    pending.Enqueue(entry);
                }
                else
                {
                    total += new FileInfo(entry).Length;
                }
            }
        }

        return total;
    }

    private static (int Files, long Bytes) CountEntries(string sourceDataDirectory)
    {
        var files = 0;
        var bytes = 0L;
        var pending = new Queue<string>();
        pending.Enqueue(sourceDataDirectory);
        while (pending.Count > 0)
        {
            var directory = pending.Dequeue();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (Directory.Exists(entry))
                {
                    pending.Enqueue(entry);
                }
                else
                {
                    files++;
                    bytes += new FileInfo(entry).Length;
                }
            }
        }

        return (files, bytes);
    }

    private static bool HasInsufficientFreeSpace(string target, long sourceSize)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(target));
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                return false;
            }

            var drive = new DriveInfo(root);
            return !drive.IsReady || drive.AvailableFreeSpace < sourceSize + FreeSpaceMarginBytes;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            // 卷信息不可用（部分网络路径）：跳过空间检查，由复制阶段自行失败。
            return false;
        }
    }

    private string BuildStagingPath(string targetManagedParent)
    {
        var stamp = _timeProvider.GetUtcNow();
        for (var attempt = 1; attempt <= 100; attempt++)
        {
            var candidate = Path.Combine(
                targetManagedParent,
                $"{StagingPrefix}{stamp:yyyyMMddHHmmssfff}-{attempt}");
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                return candidate;
            }

            stamp = stamp.AddMilliseconds(1);
        }

        throw new DataDirectoryRelocationException(
            DataDirectoryRelocationRefusal.StageFailed,
            "无法分配暂存目录名。");
    }

    private static async Task CopyDirectoryAsync(
        string source,
        string target,
        int totalFiles,
        long totalBytes,
        IProgress<DataDirectoryCopyProgress>? progress,
        CancellationToken cancellationToken)
    {
        var directoryTimestamps = new List<(string Path, DateTime Timestamp)>();
        var copiedFiles = 0;
        var copiedBytes = 0L;
        await CopyCoreAsync(
            source,
            target,
            directoryTimestamps,
            fileBytes =>
            {
                copiedFiles++;
                copiedBytes += fileBytes;
                progress?.Report(new DataDirectoryCopyProgress(copiedBytes, totalBytes, copiedFiles, totalFiles));
            },
            cancellationToken).ConfigureAwait(false);

        // 目录时间戳自底向上设置（子目录先于父目录），避免父目录时间戳被后续写入冲掉。
        for (var index = directoryTimestamps.Count - 1; index >= 0; index--)
        {
            var (path, timestamp) = directoryTimestamps[index];
            Directory.SetLastWriteTimeUtc(path, timestamp);
        }
    }

    private static async Task CopyCoreAsync(
        string source,
        string target,
        List<(string Path, DateTime Timestamp)> directoryTimestamps,
        Action<long> onFileCopied,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(target);
        directoryTimestamps.Add((target, Directory.GetLastWriteTimeUtc(source)));
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);
            var targetChild = Path.Combine(target, name);
            if (Directory.Exists(entry))
            {
                await CopyCoreAsync(
                    entry,
                    targetChild,
                    directoryTimestamps,
                    onFileCopied,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var sourceTimestamp = File.GetLastWriteTimeUtc(entry);
                File.Copy(entry, targetChild, overwrite: false);
                File.SetLastWriteTimeUtc(targetChild, sourceTimestamp);
                onFileCopied(new FileInfo(entry).Length);
            }
        }
    }

    private static bool VerifyTree(string source, string target, out string? mismatch)
    {
        mismatch = null;
        if (!Directory.Exists(target))
        {
            mismatch = $"缺少目录 {target}";
            return false;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);
            var targetChild = Path.Combine(target, name);
            if (Directory.Exists(entry))
            {
                if (!VerifyTree(entry, targetChild, out mismatch))
                {
                    return false;
                }
            }
            else if (!File.Exists(targetChild))
            {
                mismatch = $"缺少文件 {targetChild}";
                return false;
            }
            else if (new FileInfo(entry).Length != new FileInfo(targetChild).Length)
            {
                mismatch = $"文件大小不一致 {targetChild}";
                return false;
            }
        }

        return true;
    }

    private static void TryDeleteTree(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败保留暂存目录：它位于所选目录内、名字唯一，不会阻塞源目录的
            // 任何后续迁移；需要时用户可手动清理。
        }
    }
}
