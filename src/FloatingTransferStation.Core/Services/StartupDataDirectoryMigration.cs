using System.Diagnostics;
using System.Text.Json;

namespace FloatingTransferStation.Services;

/// <summary>旧插件目录覆盖偏好的并入结果（迁移决策记录，供状态提示与批次报告）。</summary>
public sealed record LegacyPluginsOverrideMerge(
    string OverrideDirectory,
    int MergedEntries,
    int SkippedConflicts,
    string? IgnoredReason = null)
{
    public bool Merged => MergedEntries > 0;
}

/// <summary>启动归一的终态：退出（新实例已接管）或保旧运行（附状态条告警文案）。</summary>
public sealed record StartupDataDirectoryMigrationResult(
    bool ExitApplication,
    string? Warning,
    string? LegacyDataDirectory = null,
    LegacyPluginsOverrideMerge? OverrideMerge = null)
{
    public static StartupDataDirectoryMigrationResult None { get; } = new(false, null);
}

/// <summary>
/// 启动数据根归一：检测到旧形状登记（<c>&lt;所选&gt;/Data</c> 或
/// <c>&lt;所选&gt;/悬浮中转站/Data</c>）时，复用 <see cref="DataDirectoryChangeService"/>
/// 既有原子管线迁到同父下「悬浮中转站」文件夹（唯一受管形状）。不另写第二条
/// 迁移管线；策略硬要求：目标已存在非空/预检不过/任一步失败＝保旧形状继续运行
/// ＋状态条告警（不硬失败、不丢数据）；成功与设置内手动变更同路（--relocated
/// 自动重启）。启动时无任何写入者（窗口、采集、watcher 均未建），静默作用域为
/// 空实现即满足契约 #38 的静默语义。
/// </summary>
public static class StartupDataDirectoryMigration
{
    /// <summary>旧 preferences.json 中插件目录覆盖偏好键（字段已删除，仅迁移期读取）。</summary>
    private const string LegacyOverridePropertyName = "pluginsDirectoryOverride";

    public static async Task<StartupDataDirectoryMigrationResult> TryMigrateAsync(
        IDataDirectoryRegistration registration,
        Func<string>? executablePathProvider = null,
        Func<ProcessStartInfo, bool>? processStarter = null)
    {
        var legacy = DataDirectorySettings.NormalizeRegisteredDataDirectory(registration.ReadDataDirectory());
        if (legacy is null || !DataDirectorySettings.IsLegacyManagedDataDirectory(legacy))
        {
            return StartupDataDirectoryMigrationResult.None;
        }

        if (!Directory.Exists(legacy))
        {
            // 目录尚不存在（登记先行/数据未建）：本会话在旧路径上建文件，
            // 归一留给下次启动，不打扰。
            return StartupDataDirectoryMigrationResult.None;
        }

        var service = new DataDirectoryChangeService(
            new DataDirectoryRelocator(),
            registration,
            new AtomicTextWriter(),
            executablePathProvider ?? (static () => Environment.ProcessPath ?? string.Empty),
            processStarter);
        var targetParent = DataDirectorySettings.GetDataParentDirectory(legacy);
        var preview = service.Preview(legacy, targetParent);
        if (!preview.Valid)
        {
            return new StartupDataDirectoryMigrationResult(
                false,
                $"数据目录仍为旧布局，本次未能自动迁移：{preview.Refusal}",
                legacy);
        }

        // 旧插件目录覆盖指向数据目录之外且非空：迁移前并入数据根 plugins（同名
        // 冲突保留数据根版本），随迁移整体移动；空/无效/位于数据目录之内则忽略。
        var overrideMerge = TryMergeLegacyPluginsOverride(legacy);
        var result = await service.ChangeAsync(legacy, targetParent, StartupQuiesceScope.Instance)
            .ConfigureAwait(true);
        if (result.ExitApplication)
        {
            return new StartupDataDirectoryMigrationResult(true, result.Error, legacy, overrideMerge);
        }

        return new StartupDataDirectoryMigrationResult(
            false,
            $"数据目录仍为旧布局，本次未能自动迁移：{result.Error}",
            legacy,
            overrideMerge);
    }

    /// <summary>
    /// 把旧插件目录覆盖偏好的内容并入数据根 plugins：同名冲突保留数据根版本，
    /// 覆盖目录本身绝不删除。偏好字段已从 <see cref="Models.AppPreferences"/> 删除，
    /// 这里只按原始 JSON 读旧键（读到后不再写回）。
    /// </summary>
    internal static LegacyPluginsOverrideMerge? TryMergeLegacyPluginsOverride(string legacyDataDirectory)
    {
        var overrideDirectory = ReadLegacyOverrideDirectory(legacyDataDirectory);
        if (overrideDirectory is null)
        {
            return null;
        }

        var pluginsDirectory = Path.Combine(legacyDataDirectory, "plugins");
        var merged = 0;
        var conflicts = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(overrideDirectory))
        {
            var target = Path.Combine(pluginsDirectory, Path.GetFileName(entry));
            if (File.Exists(target) || Directory.Exists(target))
            {
                conflicts++;
                continue;
            }

            Directory.CreateDirectory(pluginsDirectory);
            if (Directory.Exists(entry))
            {
                CopyDirectory(entry, target);
                merged++;
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(entry, target, overwrite: false);
                merged++;
            }
        }

        return new LegacyPluginsOverrideMerge(overrideDirectory, merged, conflicts);
    }

    private static string? ReadLegacyOverrideDirectory(string legacyDataDirectory)
    {
        var preferencesPath = Path.Combine(legacyDataDirectory, "preferences.json");
        if (!File.Exists(preferencesPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(preferencesPath));
            if (!document.RootElement.TryGetProperty(LegacyOverridePropertyName, out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var candidate = value.GetString();
            if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate))
            {
                return null;
            }

            var normalized = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (DataDirectorySettings.IsPathWithin(normalized, legacyDataDirectory, comparison))
            {
                return null;
            }

            return Directory.Exists(normalized) && Directory.EnumerateFileSystemEntries(normalized).Any()
                ? normalized
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var targetChild = Path.Combine(target, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                CopyDirectory(entry, targetChild);
            }
            else
            {
                File.Copy(entry, targetChild, overwrite: false);
            }
        }
    }

    /// <summary>
    /// 启动归一的静默作用域：此刻窗口、剪贴板采集与记录 watcher 均未创建，
    /// 数据目录上没有任何写入者——静默承诺（复制/发布期间无并发写入）天然成立，
    /// 三段挂钩全部为空实现。失败路径同样无可恢复对象。
    /// </summary>
    private sealed class StartupQuiesceScope : IDataChangeQuiesceScope
    {
        public static StartupQuiesceScope Instance { get; } = new();

        public Task QuiesceAsync() => Task.CompletedTask;

        public void Resume()
        {
        }

        public void PrepareForExit()
        {
        }
    }
}
