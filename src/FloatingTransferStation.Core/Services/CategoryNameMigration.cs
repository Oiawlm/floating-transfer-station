using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

/// <summary>
/// 1.25.0 一次性迁移：把 settings.json 中第三栏（Prompt）已固化的旧默认名快照
/// 「文本2」改写为新默认名「文本」。老版本任何一次改名都会经 <see cref="WindowSettings.WithCategoryName"/>
/// 把当时全部默认名写成显式快照，因此只改枚举默认值对存量用户无效，必须改写存量快照。
/// 仅当已存名恰为旧默认名时替换，用户自定义名一概不动；版本守卫保证只跑一次，
/// 保存失败保持原状、下次启动重试，不阻塞板面装载。
/// </summary>
public sealed class CategoryNameMigration
{
    public const int CurrentVersion = 1;
    public const string LegacyPromptDisplayName = "文本2";

    private readonly IBoardStore _store;

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

        var nextSettings = settings.CategoryName(BoardCategory.Prompt) == LegacyPromptDisplayName
            ? settings.WithCategoryName(BoardCategory.Prompt, BoardCategoryCatalog.DisplayName(BoardCategory.Prompt))
            : settings;
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
