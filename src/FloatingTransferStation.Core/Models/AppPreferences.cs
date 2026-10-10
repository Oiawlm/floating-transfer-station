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
/// Ctrl+C 复制选中、左键清空非置顶、右键清空全部）；
/// 1.16.0 起新增插件目录覆盖（null = 默认数据目录下 plugins，插件目录是用户偏好
/// 而非受管数据契约；启用状态 plugins-state.json 仍留在数据目录）；
/// 1.23.0 起新增自动清理开关（默认开启）；1.25.0 起清理语义为逐卡 24 小时 TTL
/// （入库满期即删、判据幂等），原成对的调度记账时间戳字段随间隔清扫语义删除，
/// 旧文件残留的时间戳成员按未知成员忽略、不影响加载。
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
    TrashNoSelectionRightClickAction TrashNoSelectionRightClick = TrashNoSelectionRightClickAction.ClearAll,
    string? PluginsDirectoryOverride = null,
    bool AutoCleanupEnabled = true)
{
    public static AppPreferences Default { get; } = new();
}
