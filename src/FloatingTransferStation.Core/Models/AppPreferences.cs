namespace FloatingTransferStation.Models;

public enum ThemePreference
{
    FollowSystem,
    Light,
    Dark
}

/// <summary>垃圾桶按钮在无选择时的左键行为（1.15.0 起，默认只清非置顶）。</summary>
public enum TrashNoSelectionLeftClickAction
{
    ClearNonPinned = 0,
    ClearAll = 1
}

/// <summary>垃圾桶按钮在无选择时的右键行为（1.15.0 起，默认清空全部）。</summary>
public enum TrashNoSelectionRightClickAction
{
    ClearAll = 0,
    ClearNonPinned = 1,
    NoAction = 2
}

/// <summary>
/// 用户偏好首版：主题模式（跟随系统/浅色/深色）与动效开关；
/// 1.10.0 起新增全局快捷键唤起开关（默认关闭，仅用户显式开启时注册系统热键）；
/// 1.15.0 起新增卡片复制手势与垃圾桶按钮无选择行为（默认右键卡片复制、
/// Ctrl+C 复制选中、左键清空非置顶、右键清空全部）。
/// 持久化为数据目录下的 preferences.json（原子写 + 备份回退）；
/// 旧安装没有该文件或缺少新字段时全部取默认值。
/// </summary>
public sealed record AppPreferences(
    ThemePreference ThemeMode = ThemePreference.FollowSystem,
    bool AnimationsEnabled = true,
    bool GlobalHotkeyEnabled = false,
    bool RightClickCardCopyEnabled = true,
    bool CopySelectionWithCtrlCEnabled = true,
    TrashNoSelectionLeftClickAction TrashNoSelectionLeftClick = TrashNoSelectionLeftClickAction.ClearNonPinned,
    TrashNoSelectionRightClickAction TrashNoSelectionRightClick = TrashNoSelectionRightClickAction.ClearAll)
{
    public static AppPreferences Default { get; } = new();
}
