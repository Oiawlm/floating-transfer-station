using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

/// <summary>
/// 一次性分类名迁移（v1 1.25.0 / v2 2026-11-03 W7 默认名通用化）。老版本任何一次
/// 改名都会经 <see cref="WindowSettings.WithCategoryName"/> 把当时全部默认名写成显式
/// 快照，因此只改 <see cref="BoardCategoryCatalog"/> 默认值对存量用户无效，必须改写
/// 存量快照。v1：把 Prompt 槽位已固化的旧默认名「文本2」改写为当时的默认名「启动」。
/// v2：默认显示名通用化——存量快照中值恰为旧默认名「图片」「文本1」「启动」或维护者
/// 历史自定义名「复盘」（用户 2026-11-03 明确要求一并更新）时，映射到对应新默认名；
/// 其他自定义名一律保留不动。版本守卫保证只跑一次，保存失败保持原状、下次启动重试，
/// 不阻塞板面装载。
/// </summary>
public sealed class CategoryNameMigration
{
    public const int CurrentVersion = 2;
    public const string LegacyPromptDisplayName = "文本2";

    private readonly IBoardStore _store;

    // v2 映射表：仅列值发生变化的三个分类（待分类不变、不入表）；「复盘」是维护者
    // 本机对 Reference 的历史自定义名，随旧默认名「文本1」一并映射到「记录」；
    // Prompt 槽位含 v1 遗留名「文本2」（<1 用户直升 v2 时一次到位）。
    private static readonly (BoardCategory Category, string[] LegacyNames)[] LegacyDefaultNames =
    {
        (BoardCategory.CustomerOriginal, new[] { "图片" }),
        (BoardCategory.Reference, new[] { "文本1", "复盘" }),
        (BoardCategory.Prompt, new[] { LegacyPromptDisplayName, "启动" })
    };

    public CategoryNameMigration(IBoardStore store)
    {
        _store = store;
    }

    public async Task<WindowSettings> EnsureAsync(
        WindowSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.CategoryNameMigrationVersion >= CurrentVersion)
        {
            return settings;
        }

        var nextSettings = settings;
        foreach (var (category, legacyNames) in LegacyDefaultNames)
        {
            if (legacyNames.Contains(settings.CategoryName(category)))
            {
                nextSettings = nextSettings.WithCategoryName(
                    category,
                    BoardCategoryCatalog.DisplayName(category));
            }
        }

        nextSettings = nextSettings with { CategoryNameMigrationVersion = CurrentVersion };

        try
        {
            await _store.SaveSettingsAsync(nextSettings, cancellationToken).ConfigureAwait(false);
            return nextSettings;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return settings;
        }
    }
}
