namespace FloatingTransferStation.Models;

public enum ThemePreference
{
    FollowSystem,
    Light,
    Dark
}

/// <summary>
/// 用户偏好首版：主题模式（跟随系统/浅色/深色）与动效开关。
/// 持久化为数据目录下的 preferences.json（原子写 + 备份回退）；
/// 旧安装没有该文件时全部取默认值。
/// </summary>
public sealed record AppPreferences(
    ThemePreference ThemeMode = ThemePreference.FollowSystem,
    bool AnimationsEnabled = true)
{
    public static AppPreferences Default { get; } = new();
}
