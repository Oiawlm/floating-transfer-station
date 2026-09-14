using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public sealed class DailyReviewMigration
{
    public const int CurrentVersion = 1;
    public const BoardCategory ReviewCategory = BoardCategory.Reference;

    private readonly IBoardStore _store;

    public DailyReviewMigration(IBoardStore store)
    {
        _store = store;
    }

    public async Task<WindowSettings> EnsureAsync(
        BoardService board,
        WindowSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(settings);

        var removed = board.RemoveCategory(ReviewCategory);
        var shouldSaveBoard = removed.Items.Count > 0;
        var shouldSaveSettings = settings.ReviewMigrationVersion < CurrentVersion;
        var nextSettings = shouldSaveSettings
            ? settings
                .WithCategoryName(ReviewCategory, "复盘") with
            {
                ReviewMigrationVersion = CurrentVersion
            }
            : settings;

        if (!shouldSaveBoard && !shouldSaveSettings)
        {
            return settings;
        }

        try
        {
            if (shouldSaveBoard)
            {
                await _store.SaveBoardAsync(board.CreateSnapshot(), cancellationToken).ConfigureAwait(false);
            }

            if (shouldSaveSettings)
            {
                await _store.SaveSettingsAsync(nextSettings, cancellationToken).ConfigureAwait(false);
            }

            return nextSettings;
        }
        catch
        {
            if (shouldSaveBoard)
            {
                board.Restore(removed);
                try
                {
                    await _store.SaveBoardAsync(board.CreateSnapshot(), cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // The original board remains available through board.json.bak when the
                    // atomic writer had already replaced the primary file.
                }
            }

            throw;
        }
    }
}
