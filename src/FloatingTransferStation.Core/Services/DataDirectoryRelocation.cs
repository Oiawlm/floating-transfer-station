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
    PublishFailed,
    /// <summary>
    /// 旧嵌套形状的就位迁移发布中断且自动恢复失败：旧根已让位、暂存副本完好，
    /// 数据零丢失；编排层不得丢弃暂存目录，保旧提示并等待下次启动重试。
    /// </summary>
    PublishRollbackFailed
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
/// <param name="SupersededSourceParentDirectory">
/// 就位迁移（旧嵌套形状 <c>&lt;所选&gt;/悬浮中转站/Data</c> → 目标＝源自身父目录）时，
/// 发布阶段整体让位的旧根去向（<c>&lt;所选&gt;/.fts-superseded-*</c>）；标准迁移为 null。
/// </param>
public sealed record DataDirectoryRelocationPlan(
    string SourceDataDirectory,
    string SourceParentDirectory,
    string TargetDataDirectory,
    string TargetParentDirectory,
    string StagingDirectory,
    long SourceSizeBytes,
    string? SupersededSourceParentDirectory = null);

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
/// 源目录与注册表不动。目标恒为唯一受管形状 <c>&lt;所选&gt;/悬浮中转站</c>，源可为
/// 受管形状或两个旧形状任一（旧形状＝待迁移旧形，启动归一与设置内变更同走本协议）；
/// 所选目录可预先存在且含其他内容（同级内容原样保留）。旧嵌套形状迁往同父目标时
/// 目标即源的「悬浮中转站」中间层：发布采用「旧根整体让位 → 暂存就位 → 让位副本
/// 交延迟清理」的就位序列，两步皆同卷原子改名，中断即反向恢复。纯托管实现
/// （可单测、保留最后写入时间、空目录也创建、发布前可取消、发布后不可取消）。
/// </summary>
public sealed class DataDirectoryRelocator
{
    private const string StagingPrefix = ".migration-";
    private const string SupersededPrefix = ".fts-superseded-";
    private const string LegacyDirectDataLeaf = "Data";
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
        var source = DataDirectorySettings.NormalizeRegisteredDataDirectory(sourceDataDirectory)
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

        // 先判“目标父在源数据目录内”（自包含，复制进自己子树）：源数据目录必在
        // 其所在目录之内，先判更精确的自包含原因。
        if (DataDirectorySettings.IsPathWithin(targetParentDirectory, source, comparison))
        {
            throw new DataDirectoryRelocationException(
                DataDirectoryRelocationRefusal.TargetInsideSourceData,
                "不能把内容目录迁移到它自己的内部。");
        }

        // 同父形状归一例外：所选目录与当前用户所选目录相同＝把旧形状迁到规范形
        // （<父>/Data 或 <父>/悬浮中转站/Data → <父>/悬浮中转站）。目标在源所在
        // 目录之内属预期，不按“迁入当前父目录”拒绝。
        var sourceDataParent = DataDirectorySettings.GetDataParentDirectory(source);
        var normalizedTargetParent = NormalizeTargetParent(targetParentDirectory);
        var isSameParentNormalization =
            string.Equals(normalizedTargetParent, sourceDataParent, comparison);
        if (!isSameParentNormalization &&
            DataDirectorySettings.IsPathWithin(target, sourceParent, comparison))
        {
            throw new DataDirectoryRelocationException(
                DataDirectoryRelocationRefusal.TargetInsideCurrentParent,
                "不能把内容目录迁移到它当前所在的目录之内。");
        }

        string? supersededSourceParent = null;
        if (Directory.Exists(target))
        {
            if (IsEmptyDirectory(target))
            {
                // 用户预建的空「悬浮中转站」文件夹不阻塞迁移：发布前删除空目标。
            }
            else if (IsHoistEligible(source, target, comparison))
            {
                // 就位迁移：目标＝源自身父目录（旧嵌套形状中间层）且中间层内只有
                // 源 Data。发布时旧根整体让位到 SupersededSourceParent。
                supersededSourceParent = BuildSupersededPath(normalizedTargetParent);
            }
            else
            {
                throw new DataDirectoryRelocationException(
                    DataDirectoryRelocationRefusal.TargetExists,
                    $"目标位置已存在同名目录：{target}");
            }
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

        var staging = BuildStagingPath(normalizedTargetParent);
        return new DataDirectoryRelocationPlan(
            source,
            sourceParent,
            target,
            normalizedTargetParent,
            staging,
            sourceSize,
            supersededSourceParent);
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
    /// 改名到位。目标意外被占用（非空）或改名失败抛异常，暂存目录保留供重试或
    /// Discard。目标为空目录时先删除空目标再改名。就位迁移（Superseded 计划）走
    /// “旧根整体让位 → 暂存就位”序列：任一步失败即反向恢复，恢复也失败时抛
    /// <see cref="DataDirectoryRelocationRefusal.PublishRollbackFailed"/>（数据在
    /// 暂存目录与让位副本中双保险，调用方不得丢弃暂存）。
    /// </summary>
    public void Publish(DataDirectoryRelocationPlan plan)
    {
        if (!Directory.Exists(plan.StagingDirectory))
        {
            throw new DataDirectoryRelocationException(
                DataDirectoryRelocationRefusal.PublishFailed,
                "暂存目录不存在，无法发布。");
        }

        if (plan.SupersededSourceParentDirectory is { } superseded)
        {
            PublishHoist(plan, superseded);
            return;
        }

        if (Directory.Exists(plan.TargetDataDirectory))
        {
            if (!IsEmptyDirectory(plan.TargetDataDirectory))
            {
                throw new DataDirectoryRelocationException(
                    DataDirectoryRelocationRefusal.PublishFailed,
                    $"目标位置已被占用：{plan.TargetDataDirectory}");
            }

            try
            {
                Directory.Delete(plan.TargetDataDirectory);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                throw new DataDirectoryRelocationException(
                    DataDirectoryRelocationRefusal.PublishFailed,
                    $"目标位置已被占用：{plan.TargetDataDirectory}",
                    exception);
            }
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
    /// 就位迁移发布：旧根（源自身父目录＝目标）整体让位到 superseded，再让暂存
    /// 就位。两步皆为同卷原子改名；第二步失败时反向恢复第一步，恢复失败＝数据
    /// 双保险在手，抛 RollbackFailed 交上层保旧重试。
    /// </summary>
    private static void PublishHoist(DataDirectoryRelocationPlan plan, string superseded)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!Directory.Exists(plan.TargetDataDirectory) ||
            !ContainsOnlyChild(plan.TargetDataDirectory, plan.SourceDataDirectory, comparison))
        {
            throw new DataDirectoryRelocationException(
                DataDirectoryRelocationRefusal.PublishFailed,
                "旧目录结构已变化，无法完成就位迁移。");
        }

        if (Directory.Exists(superseded) || File.Exists(superseded))
        {
            throw new DataDirectoryRelocationException(
                DataDirectoryRelocationRefusal.PublishFailed,
                "迁移让位目录已被占用，无法发布。");
        }

        try
        {
            Directory.Move(plan.TargetDataDirectory, superseded);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new DataDirectoryRelocationException(
                DataDirectoryRelocationRefusal.PublishFailed,
                $"迁移旧目录让位失败：{exception.Message}",
                exception);
        }

        try
        {
            Directory.Move(plan.StagingDirectory, plan.TargetDataDirectory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            if (!TryRestoreMovedDirectory(superseded, plan.TargetDataDirectory))
            {
                throw new DataDirectoryRelocationException(
                    DataDirectoryRelocationRefusal.PublishRollbackFailed,
                    $"迁移发布中断且自动恢复失败，数据已完整保留于 {plan.StagingDirectory} 与 {superseded}，请重启应用重试。",
                    exception);
            }

            throw new DataDirectoryRelocationException(
                DataDirectoryRelocationRefusal.PublishFailed,
                $"发布新内容目录失败：{exception.Message}",
                exception);
        }
    }

    /// <summary>
    /// 失败清理（幂等、尽力而为）：删暂存目录。所选目录是用户目录，即使已空也
    /// 绝不移除。就位迁移的让位副本不在此列（见 TryDeleteManagedDataDirectory）。
    /// </summary>
    public void Discard(DataDirectoryRelocationPlan plan)
    {
        TryDeleteTree(plan.StagingDirectory);
    }

    /// <summary>
    /// 延迟清理与回滚的删除目标：标准迁移＝源数据目录；就位迁移＝让位副本内的
    /// <c>Data</c>（删除守卫按旧直下形状放行，空的 .fts-superseded 包装一并移除）。
    /// </summary>
    public static string GetRelocationCleanupTarget(DataDirectoryRelocationPlan plan) =>
        plan.SupersededSourceParentDirectory is { } superseded
            ? Path.Combine(superseded, LegacyDirectDataLeaf)
            : plan.SourceDataDirectory;

    /// <summary>
    /// 发布后、登记/标记写入失败时的撤销：按形状守卫删除刚发布的目录；就位迁移
    /// 再把让位副本原路移回（源目录恢复原状）。
    /// </summary>
    public static bool TryRollbackPublish(DataDirectoryRelocationPlan plan, out string? failureReason)
    {
        var deleted = TryDeleteManagedDataDirectory(plan.TargetDataDirectory, out failureReason);
        if (plan.SupersededSourceParentDirectory is { } superseded && Directory.Exists(superseded))
        {
            if (!TryRestoreMovedDirectory(superseded, plan.TargetDataDirectory))
            {
                failureReason ??= $"无法恢复旧目录：{superseded}";
                return false;
            }
        }

        return deleted;
    }

    /// <summary>
    /// 按形状守卫删除受管数据目录（与卸载器 DeleteManagedDataDirectory 同构的安全
    /// 边界）：形状不符永不删除。删除范围：受管形状（<c>&lt;所选&gt;/悬浮中转站</c>）
    /// 递归删除数据根本身，绝不触碰用户所选父目录；旧形状（叶子 <c>Data</c>）只删
    /// <c>Data</c>，旧嵌套形状删除后尝试移除空的「悬浮中转站」中间层，就位迁移的
    /// 空包装目录（<c>.fts-superseded-*</c>，应用创建）同理移除。目录不存在视为成功。
    /// </summary>
    public static bool TryDeleteManagedDataDirectory(string dataDirectory, out string? failureReason)
    {
        failureReason = null;
        var normalized = DataDirectorySettings.NormalizeRegisteredDataDirectory(dataDirectory);
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
            TryRemoveAppCreatedWrapper(normalized);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            failureReason = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// 旧形状 Data 删除后的应用创建层收尾：旧嵌套形状的「悬浮中转站」中间层与
    /// 就位迁移的 <c>.fts-superseded-*</c> 包装目录，目录已空才移除（用户所选父
    /// 目录与任何非空目录绝不触碰）。
    /// </summary>
    private static void TryRemoveAppCreatedWrapper(string deletedDataDirectory)
    {
        try
        {
            var parent = Directory.GetParent(deletedDataDirectory);
            if (parent is null || !Directory.Exists(parent.FullName))
            {
                return;
            }

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var parentLeaf = Path.GetFileName(parent.FullName);
            var isLegacyMiddleLayer =
                string.Equals(parentLeaf, ProductIdentity.DisplayName, comparison);
            var isSupersededWrapper = parentLeaf.StartsWith(SupersededPrefix, StringComparison.Ordinal);
            if (isLegacyMiddleLayer || isSupersededWrapper)
            {
                parent.Delete();
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // 非空或被占用：保留无害，交由下次清理或用户手动处理。
        }
    }

    private static bool IsHoistEligible(string source, string target, StringComparison comparison)
    {
        // 就位迁移仅限旧嵌套形状：目标恰为源的「悬浮中转站」中间层（源自身父目录）
        // 且中间层内只有源 Data——旧根整体让位后中间层路径即新数据根。
        if (!DataDirectorySettings.IsLegacyManagedDataDirectory(source))
        {
            return false;
        }

        var sourceParent = Directory.GetParent(source)!.FullName;
        return string.Equals(target, sourceParent, comparison) &&
            ContainsOnlyChild(target, source, comparison);
    }

    private static bool ContainsOnlyChild(string directory, string expectedChild, StringComparison comparison)
    {
        try
        {
            string? only = null;
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (only is not null)
                {
                    return false;
                }

                only = entry;
            }

            return only is not null &&
                Directory.Exists(only) &&
                string.Equals(only, expectedChild, comparison);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsEmptyDirectory(string directory)
    {
        try
        {
            return !Directory.EnumerateFileSystemEntries(directory).Any();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>把让位/移位目录尽力移回原路径（瞬时占用重试数次）；成功与否如实返回。</summary>
    private static bool TryRestoreMovedDirectory(string movedDirectory, string originalPath)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (!Directory.Exists(originalPath))
                {
                    Directory.Move(movedDirectory, originalPath);
                }

                return true;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == 3)
                {
                    return false;
                }

                Thread.Sleep(50 * attempt);
            }
        }

        return false;
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

    private string BuildSupersededPath(string targetManagedParent)
    {
        var stamp = _timeProvider.GetUtcNow();
        for (var attempt = 1; attempt <= 100; attempt++)
        {
            var candidate = Path.Combine(
                targetManagedParent,
                $"{SupersededPrefix}{stamp:yyyyMMddHHmmssfff}-{attempt}");
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                return candidate;
            }

            stamp = stamp.AddMilliseconds(1);
        }

        throw new DataDirectoryRelocationException(
            DataDirectoryRelocationRefusal.StageFailed,
            "无法分配迁移让位目录名。");
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
