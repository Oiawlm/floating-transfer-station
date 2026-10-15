using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

/// <summary>launchers.json 信封（#30）：{ version, items }。数组顺序即唯一真相。</summary>
public sealed record LauncherStoreData(int Version, List<LauncherItem> Items)
{
    public const int CurrentVersion = 1;

    public static LauncherStoreData Empty { get; } = new(CurrentVersion, []);
}
/// <summary>
/// 启动项存储接口（#30）：整集合读写；读取端做逐条清洗（重复 Id 取首条、目标空/无效
/// 丢条、未知 Kind 丢条），版本高于当前时原样上抛由目录层进入只读停用（保持原文件，
/// 绝不以空集合覆盖回写）。写入走 AtomicTextWriter 同一原子管线（契约 #1/#5）。
/// </summary>
public interface ILauncherStore
{
    Task<LauncherStoreData> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(LauncherStoreData data, CancellationToken cancellationToken = default);
}
