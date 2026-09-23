using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

/// <summary>
/// 用户偏好（preferences.json）读写。独立于 IBoardStore，
/// 既有设置/看板假实现不需要感知偏好持久化。
/// </summary>
public interface IPreferencesStore
{
    Task<AppPreferences> LoadPreferencesAsync(CancellationToken cancellationToken = default);
    Task SavePreferencesAsync(AppPreferences preferences, CancellationToken cancellationToken = default);
}
