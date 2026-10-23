using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

/// <summary>
/// 单个发现候选/待确认条目（#45 启动·自动发现）：发现源产出的最小启动描述，
/// 与 <see cref="LauncherItem"/> 的持久化字段一一对应。确认制（用户方向定案）：
/// 发现项进待确认清单而非自动上屏——安装器常产生卸载器/帮助等多个快捷方式，
/// 直灌会污染 200 项上限且不可控。<see cref="Arguments"/> 语义对齐采纳管线
/// （文件/AUMID 采纳经 ImportLauncherFiles/ImportInstalledApps，均不带参数），
/// 生产源恒传 null；差分键＝与 LauncherCatalog 同源的规范化 (TargetPath, Arguments)。
/// </summary>
public sealed record LauncherDiscoveryEntry(
    LauncherKind Kind,
    string TargetPath,
    string? Arguments,
    string DisplayName,
    DateTimeOffset FirstSeenAtUtc);

/// <summary>launcher-discoveries.json 信封（#45）：{ version, pending, dismissedKeys }。</summary>
public sealed record LauncherDiscoveryStoreData(
    int Version,
    List<LauncherDiscoveryEntry> Pending,
    List<string> DismissedKeys)
{
    public const int CurrentVersion = 1;

    public static LauncherDiscoveryStoreData Empty { get; } = new(CurrentVersion, [], []);
}

/// <summary>
/// 发现清单存储接口（#45）：整集合读写。pending/dismissed 均为派生数据（重扫可
/// 重建），损坏/未知版本回落空清单即可——与 launchers.json 的用户数据只读停用
/// 不同，这里不存在不可再生资产。写入走 AtomicTextWriter 同一原子管线（#1/#5）。
/// </summary>
public interface ILauncherDiscoveryStore
{
    Task<LauncherDiscoveryStoreData> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(LauncherDiscoveryStoreData data, CancellationToken cancellationToken = default);
}
