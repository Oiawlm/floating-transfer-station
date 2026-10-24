namespace FloatingTransferStation.Models;

public enum ThemePreference
{
    FollowSystem,
    Light,
    Dark
}

/// <summary>置顶区与普通区之间的分隔样式（默认实线；无 = 保持既有纯间距）。</summary>
public enum PinnedDividerStyle
{
    None = 0,
    Solid = 1,
    Dashed = 2,
    AccentBar = 3
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
/// 启动器列数策略（默认自适应 = 既有拉伸填充行为）：Adaptive 走
/// LauncherGridMath.ColumnCount(A) 随视口宽自动增减（clamp 3–6）；Fixed3–Fixed6
/// 固定列数不随宽度变化，瓦片宽仍由 LauncherGridMath.TileWidth 摊分（行满排
/// 无死区不变；窄视口下大列数会把瓦片压到 84 下限以下——用户显式选择的语义）。
/// JSON 沿字符串枚举惯例按名落盘、数值无持久化含义（不写显式值）；列数档位
/// 与 DesignTokens LauncherTileMin/MaxColumns 同域（3–6），不引入档外列数。
/// </summary>
public enum LauncherColumnStrategy
{
    Adaptive,
    Fixed3,
    Fixed4,
    Fixed5,
    Fixed6
}

/// <summary>自动清理保留时长档位（默认 24 小时；零值=默认档，其余成员按档位
/// 升序——不写显式值，JSON 沿字符串枚举惯例按名落盘，数值无持久化含义）。</summary>
public enum AutoCleanupRetention
{
    TwentyFourHours,
    TwoHours,
    FourHours,
    SixHours,
    EightHours,
    TenHours,
    TwelveHours
}

/// <summary>
/// 用户偏好首版：主题模式（跟随系统/浅色/深色）与动效开关；
/// 1.10.0 起新增全局快捷键唤起开关（默认关闭，仅用户显式开启时注册系统热键）；
/// 1.15.0 起新增卡片复制手势与垃圾桶按钮无选择行为（卡片右半左键复制、
/// Ctrl+C 复制选中、左键清空非置顶、右键清空全部）；2026-10-23 手势统一
/// 起右键复制偏好（1.15.0 引入、1.17.0 限于右半）随「右半左键＝复制、
/// 右键＝选择」交换整体移除（Issue #216，统一手势是定案、不设替代开关），
/// 旧文件残留的该字段按未知成员忽略、不影响加载；
/// 1.16.0 起新增插件目录覆盖（null = 默认数据目录下 plugins，插件目录是用户偏好
/// 而非受管数据契约；启用状态 plugins-state.json 仍留在数据目录）；
/// 1.23.0 起新增自动清理开关（默认开启）；1.25.0 起清理语义为逐卡 24 小时 TTL
/// （入库满期即删、判据幂等），原成对的调度记账时间戳字段随间隔清扫语义删除，
/// 旧文件残留的时间戳成员按未知成员忽略、不影响加载；
/// 1.27.0 起新增 DeepSeek 接入地基：DeepSeekApiKeyProtected 存 DPAPI（CurrentUser）
/// 加密后的 API Key 密文（base64；null = 未配置，明文绝不落盘）；模型恒为
/// DeepSeekClient.DefaultModel（deepseek-flash，契约 #28），随模型选择机制删除的
/// 旧 deepSeekModel 字段按未知成员忽略、不影响加载；
/// 设置窗口几何（双栏改造起）持久化为 WPF DIP 值（null = 首次运行用默认定位），
/// 旧配置文件缺字段免迁移加载，非法值（非有限数/非正尺寸/完全落在所有显示器之外）
/// 由窗口侧整组回落默认定位（契约 #24）。
/// 1.35.0 起新增自动检查更新开关（默认开启；关=停自动检查定时器，开=恢复节奏，
/// 手动检查不受影响），旧配置缺字段按未知成员回落默认开启。
/// 置顶分隔样式（默认实线）让置顶区与普通区之间的边界可见：旧用户升级后从纯间距
/// 变为实线（用户裁决「必须有标识」），「无」样式可回到升级前的纯间距观感。
/// 自动清理保留时长（默认 24 小时，2026-10-14 用户裁决档位 2/4/6/8/10/12/24 小时）
/// 取代 1.25.0 起的固定 24 小时：巡检节奏仍固定每小时、与档位无关，改短档位后
/// 已超龄的存量卡在下一次检查时一次性追溯清理；旧配置缺字段回落默认 24 小时，
/// 老用户升级零感知。
/// 快捷启动迁移版本戳（#30，Issue #162；0 = 未归位）：只门控一次性名称归位
/// （「文本」→「启动」）——戳 ≥1 后 settings.json 的 CategoryNames["Prompt"]
/// 是合法用户改名存储；每次启动的存量收编（阶段 1）不受戳门控，防降级泄漏。
/// 1.52.0 起新增 AI 整理提示词覆盖（null = 内置默认「语音转文字整理器」，
/// 纯空白按未配置处理；明文存储，提示词非机密，DPAPI 仅用于 API Key），
/// 旧配置缺字段免迁移回落 null；上限与生效语义见 AiOrganizePrompt。
/// 短卡片备注开关（#22 2026-10-22 修订，Issue #211；默认关 = 老用户升级零感知）：
/// 开启后备注添加入口对未截断的文字卡也出现（编辑入口本就无门槛），AI 整理钮
/// 的截断门槛不随本开关豁免（#29）；旧配置缺字段免迁移回落关闭。
/// 启动·自动发现（#45；默认开 = 老用户升级零感知）：监视开始菜单（用户＋公共）
/// 与 LauncherDiscoveryFolders 指定文件夹＋AppsFolder 差分，新装应用进「发现 N」
/// 待确认清单（确认制，绝不自动上屏）；关闭 = 停监听、发现入口永不出现。
/// 文件夹为 null/空 = 无自定义监视文件夹（旧配置缺字段免迁移回落）。
/// 启动器列数策略（默认自适应 = 现行为）：Adaptive 列数随面板宽自动增减，
/// Fixed3–Fixed6 固定列数；旧配置缺字段免迁移回落自适应，老用户升级零感知。
/// AI 整理总开关（默认开 = 老用户升级零感知）：关闭＝卡片 AI 整理钮不再出现、
/// 整理入口零动作（连无 Key 状态条提示也不弹）、设置页 Key/提示词/测试连接区
/// 置灰（已存 Key 与自定义提示词原样保留，只停用不删除）；只门控入口与设置面，
/// 不改 AI 服务、提示词与密钥存储逻辑。旧配置缺字段免迁移回落开启。
/// 持久化为数据目录下的 preferences.json（原子写 + 备份回退）；
/// 旧安装没有该文件或缺少新字段时全部取默认值。
/// </summary>
public sealed record AppPreferences(
    ThemePreference ThemeMode = ThemePreference.FollowSystem,
    bool AnimationsEnabled = true,
    bool GlobalHotkeyEnabled = false,
    bool CopySelectionWithCtrlCEnabled = true,
    TrashNoSelectionLeftClickAction TrashNoSelectionLeftClick = TrashNoSelectionLeftClickAction.ClearNonPinned,
    TrashNoSelectionRightClickAction TrashNoSelectionRightClick = TrashNoSelectionRightClickAction.ClearAll,
    string? PluginsDirectoryOverride = null,
    bool AutoCleanupEnabled = true,
    string? DeepSeekApiKeyProtected = null,
    double? SettingsWindowLeft = null,
    double? SettingsWindowTop = null,
    double? SettingsWindowWidth = null,
    double? SettingsWindowHeight = null,
    bool AutoUpdateCheckEnabled = true,
    PinnedDividerStyle PinnedDivider = PinnedDividerStyle.Solid,
    AutoCleanupRetention AutoCleanupRetention = AutoCleanupRetention.TwentyFourHours,
    int QuickLaunchMigrationVersion = 0,
    string? AiOrganizeSystemPromptOverride = null,
    bool ShortCardNotesEnabled = false,
    bool LauncherDiscoveryEnabled = true,
    IReadOnlyList<string>? LauncherDiscoveryFolders = null,
    LauncherColumnStrategy LauncherColumnStrategy = LauncherColumnStrategy.Adaptive,
    bool AiOrganizeEnabled = true)
{
    public static AppPreferences Default { get; } = new();
}
