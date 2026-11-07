using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

/// <summary>
/// 一次性偏好迁移（契约 59，2026-11-07 用户拍板）：实验开关默认开启的存量收敛。
/// 「记录界面 Markdown 阅读视图」与「图片拖出为文件」自 2026-11-07 起默认开启
/// （构造默认翻 true）；全量快照持久化使存量用户的 preferences.json 已有自动
/// 落盘的显式 false（无法与主动关闭者区分，用户已接受该代价）——启动装载后
/// 统一置开并写版本戳。版本戳门控幂等：戳 ≥ <see cref="CurrentVersion"/> 直接
/// 返回入参不动偏好、零写盘——迁移后用户显式关闭永不复翻；旧 JSON 缺戳字段
/// 回落 0＝重放迁移（幂等收敛）。保存失败（IO/未授权）非致命：采纳带戳内存
/// 基准继续启动，盘上戳缺失由后续任意一次偏好保存自愈，下次启动重放。经
/// <see cref="IPreferencesStore"/> 唯一原子管线落盘（无平行保存路径）；在
/// App 装载后、主窗构造前执行＝功能面单一下发路径（「启动只读不写」的既有
/// 例外＝一次性版本戳迁移，QuickLaunchMigration 先例）。
/// </summary>
public sealed class ExperimentalDefaultsMigration
{
    public const int CurrentVersion = 1;

    private readonly IPreferencesStore _preferencesStore;

    public ExperimentalDefaultsMigration(IPreferencesStore preferencesStore) => _preferencesStore = preferencesStore;

    public async Task<AppPreferences> EnsureAsync(AppPreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (preferences.ExperimentalDefaultsMigrationVersion >= CurrentVersion) return preferences;
        var next = preferences with
        {
            ReviewMarkdownViewEnabled = true,
            DragOutAsFileEnabled = true,
            ExperimentalDefaultsMigrationVersion = CurrentVersion,
        };
        try
        {
            await _preferencesStore.SavePreferencesAsync(next, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 盘上戳缺失非致命：采纳带戳内存基准继续启动，后续任意一次偏好保存自愈；下次启动重放（幂等）。
        }

        return next;
    }
}
