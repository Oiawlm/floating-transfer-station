using System.Diagnostics;

namespace FloatingTransferStation.Services;

/// <summary>
/// 主窗在搬迁静默期提供的挂钩：复制/发布期间停止一切写入者（剪贴板采集、复盘
/// watcher、操作门封门并冲刷全部 store），失败后恢复运行，成功或重启失败后放行
/// 既有关闭路径（不再二次冲刷）。
/// </summary>
public interface IDataChangeQuiesceScope
{
    Task QuiesceAsync();
    void Resume();
    void PrepareForExit();
}

/// <summary>一次应用内数据目录搬迁的终态：失败留在原目录继续运行；其余状态要求退出应用。</summary>
public sealed record DataDirectoryChangeResult(
    bool ExitApplication,
    string? Error = null,
    DataDirectoryRelocationPlan? Plan = null)
{
    public static DataDirectoryChangeResult Failed(string error) => new(false, error);
}

/// <summary>
/// 应用内数据目录搬迁编排：与安装器 PrepareDataDirectoryMigration 同一协议——
/// 校验（形状/探针/卷空间/目标不存在）→ 暂存复制 → 校验 → 同卷原子发布 →
/// 原子更新 DataDirectory/DataParentDirectory 登记 → 写延迟清理标记 →
/// 以 --relocated 起子进程 → 旧实例退出。发布前任一步失败：清暂存、恢复运行、
/// 源目录与登记不动；登记成功后标记/重启失败不再回滚（登记已指向新目录），
/// 重启失败直接退出并提示手动启动。
/// </summary>
public sealed class DataDirectoryChangeService
{
    private readonly DataDirectoryRelocator _relocator;
    private readonly IDataDirectoryRegistration _registration;
    private readonly IAtomicTextWriter _writer;
    private readonly Func<string> _executablePathProvider;
    private readonly Func<ProcessStartInfo, bool>? _processStarter;

    public DataDirectoryChangeService(
        DataDirectoryRelocator relocator,
        IDataDirectoryRegistration registration,
        IAtomicTextWriter writer,
        Func<string> executablePathProvider,
        Func<ProcessStartInfo, bool>? processStarter = null)
    {
        _relocator = relocator;
        _registration = registration;
        _writer = writer;
        _executablePathProvider = executablePathProvider;
        _processStarter = processStarter;
    }

    public static DataDirectoryChangeService CreateDefault(IDataDirectoryRegistration registration) => new(
        new DataDirectoryRelocator(),
        registration,
        new AtomicTextWriter(),
        static () => Environment.ProcessPath ?? string.Empty);

    /// <summary>预检（不静默、不复制）：供设置窗在确认对话框中展示目标路径与数据体积。</summary>
    public DataDirectoryChangePreview Preview(string sourceDataDirectory, string targetParentDirectory)
    {
        try
        {
            var plan = _relocator.Plan(sourceDataDirectory, targetParentDirectory);
            return new DataDirectoryChangePreview(true, null, plan.SourceSizeBytes, plan.TargetDataDirectory);
        }
        catch (DataDirectoryRelocationException exception)
        {
            return new DataDirectoryChangePreview(false, exception.Message, 0, null);
        }
    }

    public async Task<DataDirectoryChangeResult> ChangeAsync(
        string sourceDataDirectory,
        string targetParentDirectory,
        IDataChangeQuiesceScope quiesce,
        IProgress<DataDirectoryCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        DataDirectoryRelocationPlan plan;
        try
        {
            plan = _relocator.Plan(sourceDataDirectory, targetParentDirectory);
        }
        catch (DataDirectoryRelocationException exception)
        {
            return DataDirectoryChangeResult.Failed(exception.Message);
        }

        try
        {
            await quiesce.QuiesceAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            quiesce.Resume();
            return DataDirectoryChangeResult.Failed("迁移前保存失败，本次未做任何改动。");
        }

        try
        {
            await _relocator.StageAsync(plan, progress, cancellationToken).ConfigureAwait(true);
            _relocator.VerifyStaged(plan);
            _relocator.Publish(plan);
        }
        catch (OperationCanceledException)
        {
            _relocator.Discard(plan);
            quiesce.Resume();
            return DataDirectoryChangeResult.Failed("已取消迁移，原目录未做任何改动。");
        }
        catch (DataDirectoryRelocationException exception)
        {
            _relocator.Discard(plan);
            quiesce.Resume();
            return DataDirectoryChangeResult.Failed($"{exception.Message}原目录未做任何改动。");
        }

        var targetParent = Path.GetDirectoryName(Path.GetDirectoryName(plan.TargetDataDirectory))!;
        if (!_registration.TryCommit(plan.TargetDataDirectory, targetParent))
        {
            RollbackPublishedTarget(plan);
            _relocator.Discard(plan);
            quiesce.Resume();
            return DataDirectoryChangeResult.Failed("无法登记新的内容位置，本次迁移已撤销，原目录未做改动。");
        }

        if (!await RelocationCleanupMarker.TryWriteAsync(
                plan.TargetDataDirectory, plan.SourceDataDirectory, _writer, cancellationToken).ConfigureAwait(true))
        {
            RollbackPublishedTarget(plan);
            _relocator.Discard(plan);
            _registration.TryCommit(plan.SourceDataDirectory, Path.GetDirectoryName(Path.GetDirectoryName(plan.SourceDataDirectory))!);
            quiesce.Resume();
            return DataDirectoryChangeResult.Failed("无法在新的内容位置写入迁移标记，本次迁移已撤销，原目录未做改动。");
        }

        quiesce.PrepareForExit();
        if (!TrySpawnRelaunch())
        {
            // 登记已指向新目录：不回滚、不分叉，直接退出并提示手动启动。
            return new DataDirectoryChangeResult(
                true,
                "内容已迁移并登记，但自动重启失败；请手动启动悬浮中转站。",
                plan);
        }

        return new DataDirectoryChangeResult(true, null, plan);
    }

    /// <summary>登记失败/标记失败的撤销：按形状守卫删除刚发布的目录，受管父空则一并移除。</summary>
    private void RollbackPublishedTarget(DataDirectoryRelocationPlan plan) =>
        DataDirectoryRelocator.TryDeleteManagedDataDirectory(plan.TargetDataDirectory, out _);

    private bool TrySpawnRelaunch()
    {
        try
        {
            var executablePath = _executablePathProvider();
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return false;
            }

            var startInfo = new ProcessStartInfo(executablePath)
            {
                Arguments = RelaunchArgument,
                UseShellExecute = false
            };
            if (_processStarter is not null)
            {
                return _processStarter(startInfo);
            }

            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            return false;
        }
    }

    public const string RelaunchArgument = "--relocated";
}

public sealed record DataDirectoryChangePreview(
    bool Valid,
    string? Refusal,
    long SizeBytes,
    string? TargetDataDirectory);
