using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

/// <summary>快捷启动迁移结果：阶段各自原子落盘成功后采纳的设置与首选项基准。</summary>
public sealed record QuickLaunchOutcome(WindowSettings Settings, AppPreferences Preferences);

/// <summary>
/// 快捷启动标签一次性迁移（#30，Issue #162）：「文本」标签（Prompt 槽位）让位给
/// 快捷启动，与 DailyReviewMigration/CategoryNameMigration 同构共存、不抽共享基类
/// （两个一次性历史事件无第三消费者）。链路 DailyReview → CategoryName → QuickLaunch，
/// 在板面装载门内执行（迁移完成前不开放捕获/清扫写入）。
/// 跨文件提交协议（每阶段独立幂等，部分成功靠下次启动收敛）：
/// 阶段 1 板面收编（最优先且最先落盘）：Prompt 桶非空 → 全量迁「待分类」
/// （保置顶分区 #2、保批内序）+ board.json 原子保存；失败 → Undo(BoardBatchMove)
/// 全还原、本次启动迁移中止、板面照常开放（数据原样）、下次启动重试。
/// 阶段 2 默认接收修复：默认接收是会话态（DefaultCaptureCategoryState 无持久化，
/// 装载期必为 Inbox），纵深守卫落在 Set 的角色校验与 MainWindowViewModel 构造的
/// 装载期修复，无磁盘动作。
/// 阶段 3 名称归位（版本戳门控的一次性事件）：QuickLaunchMigrationVersion &lt; 1 →
/// 名称覆写「启动」（settings.json）+ 版本戳（preferences.json），各自原子保存；
/// 失败非致命：显示名暂为旧名（角色不受影响），下次启动重试。
/// 幂等防降级泄漏：每次启动执行阶段 1 收编（降级期旧版写入 Prompt 的新卡在再升级
/// 时再收编），版本戳只门控阶段 3 的一次性名称归位——戳 ≥1 后
/// CategoryNames["Prompt"] 是合法用户改名存储。
/// </summary>
public sealed class QuickLaunchMigration
{
    public const int CurrentVersion = 1;
    public const BoardCategory LauncherCategory = BoardCategory.Prompt;

    private readonly IBoardStore _store;
    private readonly IPreferencesStore _preferencesStore;

    public QuickLaunchMigration(IBoardStore store, IPreferencesStore preferencesStore)
    {
        _store = store;
        _preferencesStore = preferencesStore;
    }

    public async Task<QuickLaunchOutcome> EnsureAsync(
        BoardService board,
        WindowSettings settings,
        AppPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(preferences);

        // 阶段 1：板面数据最优先且最先落盘。全量收编跨分类整批移动，
        // 置顶卡落 Inbox 置顶区、普通卡落普通区顶部，批内显示顺序保持。
        var promptIds = board.Items(LauncherCategory)
            .Select(item => item.Id)
            .ToArray();
        if (promptIds.Length > 0)
        {
            var move = board.MoveManyToCategoryTop(promptIds, BoardCategory.Inbox);
            if (!move.IsValid)
            {
                // 整桶收编的 ResolveBatch 必成（同源、全存在）；防御性中止，
                // 板面数据未动，下次启动重试。
                return new QuickLaunchOutcome(settings, preferences);
            }

            if (move.Changed)
            {
                try
                {
                    await _store.SaveBoardAsync(board.CreateSnapshot(), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    board.Undo(move);
                    try
                    {
                        await _store.SaveBoardAsync(board.CreateSnapshot(), cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // 原内容仍可经 board.json.bak 恢复（原子写未完成主文件替换）。
                    }

                    if (exception is IOException or UnauthorizedAccessException)
                    {
                        // 保存失败属可重试的非致命路径：本次启动迁移中止（阶段 3 不再
                        // 覆写名称），板面照常开放原数据，下次启动重试。
                        return new QuickLaunchOutcome(settings, preferences);
                    }

                    throw;
                }
            }
        }

        // 阶段 3：版本戳只门控一次性名称归位；覆写包含用户自定义名（迁移语义，
        // CHANGELOG 明示），戳成功后该键成为合法改名存储。
        if (preferences.QuickLaunchMigrationVersion >= CurrentVersion)
        {
            return new QuickLaunchOutcome(settings, preferences);
        }

        var nextSettings = settings.WithCategoryName(
            LauncherCategory,
            BoardCategoryCatalog.DisplayName(LauncherCategory));
        try
        {
            await _store.SaveSettingsAsync(nextSettings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new QuickLaunchOutcome(settings, preferences);
        }

        var nextPreferences = preferences with { QuickLaunchMigrationVersion = CurrentVersion };
        try
        {
            await _preferencesStore.SavePreferencesAsync(nextPreferences, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 名称已落盘、戳缺失：采纳带戳的内存基准，后续任意一次偏好保存即把戳
            // 带回盘上自愈；下次启动最多重放一次名称归位（幂等）。
            return new QuickLaunchOutcome(nextSettings, nextPreferences);
        }

        return new QuickLaunchOutcome(nextSettings, nextPreferences);
    }
}
