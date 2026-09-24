namespace FloatingTransferStation.Models;

public enum ThemePreference
{
    FollowSystem,
    Light,
    Dark
}

/// <summary>
/// 用户偏好首版：主题模式（跟随系统/浅色/深色）与动效开关；
/// 1.10.0 起新增全局快捷键唤起开关（默认关闭，仅用户显式开启时注册系统热键）。
/// 持久化为数据目录下的 preferences.json（原子写 + 备份回退）；
/// 旧安装没有该文件或缺少新字段时全部取默认值。
/// </summary>
public sealed record AppPreferences(
    ThemePreference ThemeMode = ThemePreference.FollowSystem,
    bool AnimationsEnabled = true,
    bool GlobalHotkeyEnabled = false)
{
    public static AppPreferences Default { get; } = new();
}
