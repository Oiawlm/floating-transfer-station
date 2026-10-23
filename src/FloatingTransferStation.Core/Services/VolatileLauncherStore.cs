using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

/// <summary>
/// 易失启动项存储（#30）：无盘内存实现——测试与无持久化注入场景的回退
/// （窗口直连构造/单元测试），行为契约与 LocalStore 相同（整集合读写）。
/// </summary>
public sealed class VolatileLauncherStore : ILauncherStore
{
    private readonly Lock _gate = new();
    private LauncherStoreData _data = LauncherStoreData.Empty;

    public Task<LauncherStoreData> LoadAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(new LauncherStoreData(_data.Version, [.. _data.Items]));
        }
    }

    public Task SaveAsync(LauncherStoreData data, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _data = new LauncherStoreData(data.Version, [.. data.Items]);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// 易失发现清单存储（#45）：无盘内存实现——测试与无持久化注入场景的回退，
/// 行为契约与 LocalStore 相同（整集合读写）。
/// </summary>
public sealed class VolatileLauncherDiscoveryStore : ILauncherDiscoveryStore
{
    private readonly Lock _gate = new();
    private LauncherDiscoveryStoreData _data = LauncherDiscoveryStoreData.Empty;

    public Task<LauncherDiscoveryStoreData> LoadAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(new LauncherDiscoveryStoreData(
                _data.Version,
                [.. _data.Pending],
                [.. _data.DismissedKeys]));
        }
    }

    public Task SaveAsync(LauncherDiscoveryStoreData data, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _data = new LauncherDiscoveryStoreData(data.Version, [.. data.Pending], [.. data.DismissedKeys]);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// 启动项文件导入助手（#30）：∈{*.exe,*.lnk} 为可导入，其余跳过并计数
/// （状态条一次性汇报「添加了 N 项，跳过 M 个不支持的文件，K 个已存在」）。
/// </summary>
public static class LauncherFileImport
{
    public static bool IsImportableFile(string path) =>
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);

    /// <summary>分流：返回可导入子集与跳过计数（保序）。</summary>
    public static (IReadOnlyList<string> Importable, int SkippedCount) Split(
        IEnumerable<string> files)
    {
        List<string> importable = [];
        var skipped = 0;
        foreach (var file in files)
        {
            if (IsImportableFile(file))
            {
                importable.Add(file);
            }
            else
            {
                skipped++;
            }
        }

        return (importable, skipped);
    }
}
