# 分层测试协议选择器（判定序 v2，2026-10-27 约定派生重构）：
# 豁免 → L1 例外表（覆写/缩圈，命中优先于一切派生）→ L1 全量表（真全局文件，逐条打印
# [affected-full] 标记行）→ L2 约定派生（恒非零校验内联：命中 0 条即该规则失效、落 L3，
# 绝不静默空选）→ L3 未知回退全量（理由＋消除指引）。
# 语义唯一权威来源：docs/design-contracts.md 契约「受影响测试选区协议」（#32）；
# 本头注与 CONTRIBUTING.md 只作引用，不在别处维护第二份语义副本。
# 变更集双模式：-Scope Edit（基线 HEAD，本次编辑反馈）/-Scope Integrate（merge-base
# origin/main，整条分支集成，默认）；-Base 为原始基线覆盖（发行列车/回放用），与 -Scope
# 同给报错。-ChangeSet 显式注入变更集（契约测试/历史回放）。
# Core.Tests 整项目恒跑（强制基线）；除 -Full/回退外不运行 solution 级无 filter 的全量测试。
# 点源加载（先 $FTS_SELECTOR_DEFINE_ONLY=$true 再 . 本文件）只定义数据与函数，供契约测试复用。
[CmdletBinding()]
param(
    [string]$Base,
    [ValidateSet('Edit', 'Integrate')]
    [string]$Scope = 'Integrate',
    [string]$Areas,
    [switch]$Full,
    [switch]$Explain,
    [string[]]$ChangeSet,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$DotnetPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$appTestsDir = Join-Path $repoRoot 'tests\FloatingTransferStation.Tests'
$coreTestsDir = Join-Path $repoRoot 'tests\FloatingTransferStation.Core.Tests'

# ---- 豁免类：不参与构建产物且无契约断言的纯文档（白名单后缀；未知后缀不豁免）----
$exemptDocExtensions = @('.md', '.markdown', '.png', '.jpg', '.jpeg', '.gif', '.svg', '.webp')
# 根目录被 LifecycleTests.ReleaseMetadata 断言覆盖的文档（改动必须跑契约测试，不豁免）。
$assertedRootDocs = @('README.md', 'CONTRIBUTING.md', 'CHANGELOG.md', 'ROADMAP.md', 'LICENSE')

# ---- 变更集忽略清单（推导层目录前缀；与豁免类是两个语义）----
# 初始为空：已知运行时产物已由 .gitignore 兜住（.wbx/TestResults/.tools/artifacts 等）；
# 仅当产物无法 gitignore 时经评审加入。启动时防后门校验（Assert-ChangesetNoisePrefixes）。
# 清单外一切未知 untracked 路径照走 L3 全量（安全语义不破）。
$changesetNoisePrefixes = @()

# ---- L1 例外表（覆写/缩圈例外；一文件多命中＝区域并集。命中优先于一切派生与全量表）----
# 2026-10-27 判定序 v2 改造（W2 矩阵对表，docs/research/RESEARCH-AFFECTED-COVERAGE-20261027.md）：
# - 删除四条与约定派生等价的条目：Search/AutoCleanup/CardAi（R-AppPartial 桥接∪字面集合等价）、
#   TrimAwareTextBlock（R-AppStem filter 逐字相同）——由 L2 免登记接管；
# - SettingsWindow glob 收敛：SettingsWindow* 只留 .xaml 与零命中件，.xaml.cs/.CategoryOrder.cs
#   交 R-AppStem（派生 filter FQN~SettingsWindow 与旧条目逐字相同，零行为差）；
# - 新增登记（反向守卫 NONE=13 清零）：DesignTheme/Core.PluginActions/AppIcon（缩圈入表）。
$overrides = @(
    # 快捷启动（启动器）：瓦片/URL/选择器/图标/目录/存储/Shell 互操作/网格几何（取证 2026-10-19 缩圈）；
    # 2026-10-24 增 LauncherTilePanel 网格承载与 LauncherColumn 列数策略词根（设置窗交互旅程断言面板重排，属启动器消费面）。
    @{ Name = 'Launcher'; Filter = 'FullyQualifiedName~QuickLaunch|FullyQualifiedName~UrlInput|FullyQualifiedName~Discovery|FullyQualifiedName~LauncherColumn'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.QuickLaunch*.cs',
        'src/FloatingTransferStation/Views/MainWindow.LauncherDiscovery*.cs',
        'src/FloatingTransferStation/Views/LauncherTilePanel.cs',
        'src/FloatingTransferStation/Views/LauncherUrlInputWindow*', 'src/FloatingTransferStation/Views/MultiItemPickerWindow*',
        'src/FloatingTransferStation/Views/LauncherEditTargetWindow*', 'src/FloatingTransferStation/Launcher/*',
        'src/FloatingTransferStation/Services/ShellInterop.cs',
        'src/FloatingTransferStation.Core/Services/Launcher*.cs', 'src/FloatingTransferStation.Core/Services/VolatileLauncher*.cs',
        'src/FloatingTransferStation.Core/Services/ILauncherStore.cs', 'src/FloatingTransferStation.Core/Services/ILauncherDiscoveryStore.cs',
        'src/FloatingTransferStation.Core/Services/LocalStore.LauncherDiscoveries.cs',
        'src/FloatingTransferStation.Core/Design/LauncherGridMath.cs',
        'src/FloatingTransferStation.Core/Models/LauncherItem.cs', 'src/FloatingTransferStation.Core/Models/LauncherIconKey.cs') },
    # 设置窗口：xaml（无词根派生盲区）＋零命中缩圈件；SettingsWindow*.cs 由 R-AppStem 接管（filter 等价）。
    # Metrics 为取证缩圈：消费者仅设置窗族。
    @{ Name = 'SettingsWindow'; Filter = 'FullyQualifiedName~SettingsWindow'; Globs = @(
        'src/FloatingTransferStation/Views/SettingsWindow.xaml', 'src/FloatingTransferStation/Views/SettingsWindowPlacement.cs',
        'src/FloatingTransferStation/Views/WindowShellClip.cs',
        'src/FloatingTransferStation.Core/Design/SettingsWindowMetrics.cs') },
    # 生命周期：安装器、发行元数据/就绪断言（含其断言的 SettingsWindow.xaml）、自启、单实例、数据目录边界
    @{ Name = 'Lifecycle'; Filter = 'FullyQualifiedName~Lifecycle'; Globs = @(
        'installer/*', 'src/FloatingTransferStation/Services/WindowsStartupManager.cs',
        'src/FloatingTransferStation/Services/SingleInstanceGuard.cs', 'src/FloatingTransferStation/Services/AppLifecycleService.cs',
        'src/FloatingTransferStation/Services/WindowsDataDirectorySettings.cs', 'src/FloatingTransferStation.Core/ProductIdentity.cs',
        'src/FloatingTransferStation/Views/SettingsWindow.xaml') },
    # 公开发行材料（README/CONTRIBUTING/CHANGELOG/LICENSE/ROADMAP/version.txt 与 PR 模板）：
    # LifecycleTests.ReleaseMetadata 以内容/存在断言锁定这些文件的用户契约与协议记载
    # （PR 模板无断言，归本区是保守归类：过程材料变更跑发行元数据契约测试）。
    @{ Name = 'PublicReleaseMaterials'; Filter = 'FullyQualifiedName~ReleaseMetadata'; Globs = @(
        'README.md', 'CONTRIBUTING.md', 'CHANGELOG.md', 'LICENSE', 'ROADMAP.md', 'version.txt',
        '.github/PULL_REQUEST_TEMPLATE.md') },
    # 剪贴板与外部导入：采集/去重/容量/载荷/图片读取/拖放载荷/文本整理插件管线
    @{ Name = 'Clipboard'; Filter = 'FullyQualifiedName~Clipboard|FullyQualifiedName~ExternalDrop|FullyQualifiedName~WindowsDataImageReader'; Globs = @(
        'src/FloatingTransferStation/Services/Clipboard*.cs', 'src/FloatingTransferStation/Services/IClipboardReader.cs',
        'src/FloatingTransferStation/Services/WpfClipboardReader.cs', 'src/FloatingTransferStation/Services/WindowsDataImageReader.cs',
        'src/FloatingTransferStation/Services/ExternalDrop*.cs', 'src/FloatingTransferStation/Models/Clipboard*.cs',
        'src/FloatingTransferStation/Models/ExternalDropPayload.cs', 'src/FloatingTransferStation.Core/Services/CapturedContentFingerprint.cs',
        'src/FloatingTransferStation.Core/Services/StoredImage.cs', 'src/FloatingTransferStation.Core/Services/TextCleanerPipeline.cs') },
    # 拖放管线（含拖放载荷服务与插入槽解析——取证缩圈：专用测试类独占）
    @{ Name = 'Drag'; Filter = 'FullyQualifiedName~Drag|FullyQualifiedName~Drop|FullyQualifiedName~InsertionSlotResolver'; Globs = @(
        'src/FloatingTransferStation/Services/DragPayloadService.cs',
        'src/FloatingTransferStation.Core/Services/InsertionSlotResolver.cs') },
    # 卡片交互 partial 族（src partial → 测试区域；必要处放宽到共享会话机器的锁定测试）
    @{ Name = 'CardGestures'; Filter = 'FullyQualifiedName~CardGestures|FullyQualifiedName~CardHalves'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.CardGestures.cs', 'src/FloatingTransferStation/Views/CardGestureZones.cs') },
    @{ Name = 'CardTextEditing'; Filter = 'FullyQualifiedName~CardEditing|FullyQualifiedName~CardCaretAnchor|FullyQualifiedName~CardNote'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.CardTextEditing.cs') },
    @{ Name = 'CategoryEditing'; Filter = 'FullyQualifiedName~Category'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.CategoryEditing.cs', 'src/FloatingTransferStation/Views/CategoryFeedbackAnimation.cs') },
    # 点击编辑器外部＝提交共享协调（2026-10-26，契约 #49）：协调四处编辑位的提交
    # 触发通道——受影响面＝新矩阵＋四处编辑位既有锁定族（分类/卡片正文与备注/
    # 复盘编辑器含 Esc 族/瓦片改名）。
    @{ Name = 'EditingCommit'; Filter = 'FullyQualifiedName~EditingCommit|FullyQualifiedName~Category|FullyQualifiedName~CardEditing|FullyQualifiedName~CardNote|FullyQualifiedName~ReviewEditor|FullyQualifiedName~QuickLaunch_Rename'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.EditingCommit.cs') },
    # CtrlC 为取证修订：原 ClipboardDelivery 词根在测试方法名中不存在（欠选）；
    # LeftClickCopy/RightClick 为手势交换（Issue #216）后的方法名前缀（原 RightClickCopy 已随右键复制移除）
    @{ Name = 'ClipboardDelivery'; Filter = 'FullyQualifiedName~LeftClickCopy|FullyQualifiedName~RightClick|FullyQualifiedName~CtrlC'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.ClipboardDelivery.cs') },
    # Review 为取证修订：App 侧方法全为 Review* 前缀（DailyReview 词根仅存在于 Core.Tests 基线）
    @{ Name = 'DailyReview'; Filter = 'FullyQualifiedName~Review'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.DailyReview.cs', 'src/FloatingTransferStation.Core/Services/IDailyReviewStore.cs') },
    @{ Name = 'DataRelocation'; Filter = 'FullyQualifiedName~Relocation|FullyQualifiedName~DataDirectory'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.DataRelocation.cs') },
    @{ Name = 'EdgeHide'; Filter = 'FullyQualifiedName~EdgeHide|FullyQualifiedName~WindowController'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.EdgeHide.cs', 'src/FloatingTransferStation/Services/MonitorBounds.cs') },
    @{ Name = 'PanelHold'; Filter = 'FullyQualifiedName~PanelHold|FullyQualifiedName~PanelStateMachine'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.PanelHold.cs', 'src/FloatingTransferStation/Views/MainWindow.PanelTextEditing.cs') },
    # 置顶重申与 IME 抑制（2026-10-24）：置顶 z 序行为（Lifecycle.ReassertTopmost）与
    # 编辑/组合态判定（PanelTextEditing）的锁定测试族；PanelTextEditing 同时命中 PanelHold（并集）。
    @{ Name = 'TopmostImeSuppression'; Filter = 'FullyQualifiedName~Topmost'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.Lifecycle.cs', 'src/FloatingTransferStation/Views/MainWindow.PanelTextEditing.cs') },
    @{ Name = 'Selection'; Filter = 'FullyQualifiedName~Selection|FullyQualifiedName~Undo'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.SelectionAndDeletion.cs') },
    @{ Name = 'Settings'; Filter = 'FullyQualifiedName~SettingsWindow|FullyQualifiedName~Preferences'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.Settings.cs') },
    @{ Name = 'Updates'; Filter = 'FullyQualifiedName~Update'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.Updates.cs') },
    # 控件/转换器
    @{ Name = 'Thumbnail'; Filter = 'FullyQualifiedName~Thumbnail'; Globs = @(
        'src/FloatingTransferStation/Controls/AsyncThumbnailImage.cs', 'src/FloatingTransferStation/Converters/ImageThumbnailConverter.cs') },
    # Core 模型（一模型→消费面测试族）；ImageInputLimits 为取证缩圈（图片管线专用测试独占）
    @{ Name = 'Core.AppPreferences'; Filter = 'FullyQualifiedName~Preferences'; Globs = @(
        'src/FloatingTransferStation.Core/Models/AppPreferences.cs') },
    @{ Name = 'Core.BoardCategory'; Filter = 'FullyQualifiedName~BoardCategory|FullyQualifiedName~Category'; Globs = @(
        'src/FloatingTransferStation.Core/Models/BoardCategory.cs') },
    @{ Name = 'Core.BoardItem'; Filter = 'FullyQualifiedName~Board'; Globs = @(
        'src/FloatingTransferStation.Core/Models/BoardItem.cs', 'src/FloatingTransferStation.Core/Models/BoardItemKind.cs',
        'src/FloatingTransferStation.Core/Models/BoardSnapshot.cs') },
    # Review 词根补充：App 侧方法全为 Review* 前缀（评审修订 #3——solution 级运行也须选中 App 族）
    @{ Name = 'Core.DailyReviewDocument'; Filter = 'FullyQualifiedName~DailyReview|FullyQualifiedName~Review'; Globs = @(
        'src/FloatingTransferStation.Core/Models/DailyReviewDocument.cs', 'src/FloatingTransferStation.Core/Models/DailyReviewFileChangedEventArgs.cs') },
    @{ Name = 'Core.PluginManifest'; Filter = 'FullyQualifiedName~Plugin'; Globs = @(
        'src/FloatingTransferStation.Core/Models/PluginManifest.cs') },
    @{ Name = 'Core.WindowSettings'; Filter = 'FullyQualifiedName~CategoryOrder|FullyQualifiedName~WindowSettings|FullyQualifiedName~WindowController'; Globs = @(
        'src/FloatingTransferStation.Core/Models/WindowSettings.cs') },
    # Core 服务显式覆写（约定派生前的特例：文件 stem 不等于/窄于测试类名）
    @{ Name = 'Core.AiOrganizePrompt'; Filter = 'FullyQualifiedName~AiTextOrganizer'; Globs = @(
        'src/FloatingTransferStation.Core/Services/AiOrganizePrompt.cs') },
    # 原子写契约（实现＋接口，2026-10-27 F2 缩圈 §三.D）：接口消费面＝Core.Tests×5（恒跑基线已覆盖）
    # ＋App 侧 LocalStore 36＋DataDirectoryChange 5——有限族可枚举、无共享 harness 间接。
    @{ Name = 'Core.AtomicTextWriter'; Filter = 'FullyQualifiedName~LocalStore|FullyQualifiedName~DataDirectoryChange'; Globs = @(
        'src/FloatingTransferStation.Core/Services/AtomicTextWriter.cs', 'src/FloatingTransferStation.Core/Services/IAtomicTextWriter.cs') },
    @{ Name = 'Core.BoardItemCollection'; Filter = 'FullyQualifiedName~Board'; Globs = @(
        'src/FloatingTransferStation.Core/Services/BoardItemCollection.cs') },
    @{ Name = 'Core.DailyReviewMerge'; Filter = 'FullyQualifiedName~DailyReview|FullyQualifiedName~Review'; Globs = @(
        'src/FloatingTransferStation.Core/Services/DailyReviewMerge.cs') },
    @{ Name = 'Core.DataDirectory'; Filter = 'FullyQualifiedName~Relocation|FullyQualifiedName~DataDirectory'; Globs = @(
        'src/FloatingTransferStation.Core/Services/DataDirectoryCleanupProcessor.cs',
        'src/FloatingTransferStation.Core/Services/DataDirectoryRelocation.cs',
        'src/FloatingTransferStation.Core/Services/DataDirectorySettings.cs',
        'src/FloatingTransferStation.Core/Services/RelocationCleanupMarker.cs') },
    @{ Name = 'Core.ImageFileSupport'; Filter = 'FullyQualifiedName~Image'; Globs = @(
        'src/FloatingTransferStation.Core/Services/ImageFileSupport.cs', 'src/FloatingTransferStation.Core/Services/ImageInputLimits.cs') },
    @{ Name = 'Core.ManagedImagePath'; Filter = 'FullyQualifiedName~ImageNormalizer'; Globs = @(
        'src/FloatingTransferStation.Core/Services/ManagedImagePath.cs') },
    # 自启契约接口（2026-10-27 F2 缩圈 §三.D）：fake 注入缝消费面＝6 族并集
    # （AutoCleanup 31/Hotkey 12/SettingsWindow 108/PluginAction 31），无共享 harness 间接。
    @{ Name = 'Core.IStartupManager'; Filter = 'FullyQualifiedName~AutoCleanup|FullyQualifiedName~Hotkey|FullyQualifiedName~SettingsWindow|FullyQualifiedName~PluginAction'; Globs = @(
        'src/FloatingTransferStation.Core/Services/IStartupManager.cs') },
    # 密钥保护契约接口（2026-10-27 F2 缩圈 §三.D）：唯二直接消费族＝CardAi 11＋SettingsWindowAi 22
    # （实现 DpapiSecretProtector 与装配面由 SettingsWindow/SettingsWindowAi 词根覆盖）。
    @{ Name = 'Core.ISecretProtector'; Filter = 'FullyQualifiedName~CardAi|FullyQualifiedName~SettingsWindowAi'; Globs = @(
        'src/FloatingTransferStation.Core/Services/ISecretProtector.cs') },
    # 主题管理（2026-10-27 登记缩圈）：字面 0 命中的遗留未知路径，真实测试族＝主题证据/切换双锁
    @{ Name = 'DesignTheme'; Filter = 'FullyQualifiedName~ThemeEvidence|FullyQualifiedName~ThemeSwitch'; Globs = @(
        'src/FloatingTransferStation/Services/DesignThemeManager.cs') },
    # 插件动作管线（2026-10-27 登记缩圈，OQ-1 裁决）：Pipeline 字面 0 命中（旧泛型派生已腐烂）＋
    # 试用窗口三文件同族（真实测试族＝PluginActionsCoreTests＋PluginActionMenuTests；F2 复核覆盖面）
    @{ Name = 'Core.PluginActions'; Filter = 'FullyQualifiedName~PluginActions|FullyQualifiedName~PluginActionMenu'; Globs = @(
        'src/FloatingTransferStation.Core/Services/PluginActionPipeline.cs',
        'src/FloatingTransferStation/Views/PluginActionTrialWindow.xaml', 'src/FloatingTransferStation/Views/PluginActionTrialWindow.xaml.cs') },
    # 应用图标管线（2026-10-27 登记缩圈）：生成器工程与规格源（契约 #35 锁定面＝AppIconContractTests）
    @{ Name = 'AppIcon'; Filter = 'FullyQualifiedName~AppIcon'; Globs = @(
        'src/FloatingTransferStation.IconTool/*', 'src/FloatingTransferStation/Assets/AppIcon/*') },
    # 测试共享设施缩圈件（2026-10-27 F2 缩圈 §三.K）：成员 SetItemCreatedAt/SetAllItemsCreatedAt
    # 仅 3 消费文件（BoardService 40/BoardMutation 53/AutoCleanup 31），非跨族共享机器。
    @{ Name = 'BoardCreatedAt'; Filter = 'FullyQualifiedName~BoardService|FullyQualifiedName~BoardMutation|FullyQualifiedName~AutoCleanup'; Globs = @(
        'tests/FloatingTransferStation.Tests/BoardCreatedAtTestHelpers.cs') },
    # 对话框壳回退（2026-10-27 F2 缩圈 §三.O）：src 消费者恰 4 对话框壳（编辑目标/网址输入/
    # 多选器/插件试用窗），测试面＝QuickLaunch 104＋SettingsWindowPlugin 12。
    @{ Name = 'DialogShell'; Filter = 'FullyQualifiedName~QuickLaunch|FullyQualifiedName~SettingsWindowPlugin'; Globs = @(
        'src/FloatingTransferStation/Views/DialogShellFallback.cs') },
    # 选择器契约区域（2026-10-27 scripts 自噬消灭）：改选择器两文件不再全量，
    # 由本契约测试脚本承接（Kind=Script：Core 基线后子进程运行，非 0 退出＝失败；
    # 恒非零语义＝契约测试自身 0 场景即 exit 1）。
    @{ Name = 'SelectorContract'; Kind = 'Script'; Globs = @(
        'scripts/run-affected-tests.ps1', 'scripts/test-affected-selector.ps1'); Script = 'scripts/test-affected-selector.ps1' }
)

# ---- L1 全量表（真全局文件；逐条打印 [affected-full] 标记行与 category）----
# 2026-10-27 收缩（spec §三处置表）：删除 Core/Services/I*.cs 兜底（对 0 个现有跟踪文件改变
# 行为，未登记新接口从静默全量变为 L3 指引全量）；scripts/* glob 拆为显式清单（选择器两文件
# 已入例外表 SelectorContract，新脚本默认 L3 全量＋指引——先全量、稳定后缩圈）。
$globalFulls = @(
    @{ Reason = '共享窗口壳（主窗 XAML/代码后置/App 入口）'; Category = 'C5'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.xaml', 'src/FloatingTransferStation/Views/MainWindow.xaml.cs',
        'src/FloatingTransferStation/App.xaml', 'src/FloatingTransferStation/App.xaml.cs') },
    @{ Reason = '共享样式/主题资源字典（六窗口合并或运行时全局切换，取证 2026-10-19；F2 缩圈队列）'; Category = 'C5'; Globs = @(
        'src/FloatingTransferStation/Resources/*') },
    @{ Reason = '设置宿主接口——ApplyPreferences 等成员是跨区域测试驱动 API（取证 2026-10-19：设置/清理/选择/更新/热键/迁移八族直用；IImageNormalizer 拆条降级待 F2 消费面取证）'; Category = 'C5'; Globs = @(
        'src/FloatingTransferStation/Services/ISettingsHost.cs', 'src/FloatingTransferStation/Services/IImageNormalizer.cs') },
    @{ Reason = '跨区域存储/偏好契约接口（F2 §三.D KEEP-FULL：IBoardStore 19 文件跨 12+ 族含共享 fixture、IPreferencesStore 10 文件含共享 harness 构造参数→全交互族间接）'; Category = 'C5'; Globs = @(
        'src/FloatingTransferStation.Core/Services/IBoardStore.cs', 'src/FloatingTransferStation.Core/Services/IPreferencesStore.cs') },
    @{ Reason = 'Core 视图模型——全部交互测试的共享机器（取证 2026-10-19：MainWindowViewModel 被 40+ 测试文件直用）'; Category = 'C5'; Globs = @(
        'src/FloatingTransferStation.Core/ViewModels/*') },
    @{ Reason = '全局设计 token/设置窗几何数学——全部界面与动效的唯一数值来源（取证 2026-10-19；LauncherGridMath/SettingsWindowMetrics 实际由例外表前置接住，本条为防御纵深）'; Category = 'C5'; Globs = @(
        'src/FloatingTransferStation.Core/Design/*') },
    @{ Reason = '原生互操作/窗口效果——全域消费（取证 2026-10-19）'; Category = 'C5'; Globs = @(
        'src/FloatingTransferStation/Services/DwmWindowEffects.cs', 'src/FloatingTransferStation/Services/NativeMethods.cs') },
    @{ Reason = '构建/依赖/解决方案/CI 配置（解决方案级影响）'; Category = 'C5'; Globs = @(
        'Directory.Build.props', 'Directory.Packages.props', 'FloatingTransferStation.slnx', 'global.json', 'nuget.config',
        '.github/workflows/*') },
    @{ Reason = '窗口/卡片视觉与动画基础设施——测试方法名与源文件名不同族，保守回退全量（F2 缩圈取证队列）'; Category = 'C2'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.VisualTransitions.cs', 'src/FloatingTransferStation/Views/MainWindow.CardEntrance.cs',
        'src/FloatingTransferStation/Views/FadeAnimation.cs', 'src/FloatingTransferStation/Views/LiftAnimation.cs') },
    @{ Reason = '共享测试设施（F2 §三.K KEEP-FULL：TestDirectory 102 文件消费 70%、tests GlobalUsings 全工程隐式 using、MIT.cs 58 partial 共享壳、双 fixture 跨族联合消费 10 文件）'; Category = 'C5'; Globs = @(
        'tests/FloatingTransferStation.Tests/TestDirectory.cs', 'tests/FloatingTransferStation.Tests/GlobalUsings.cs',
        'tests/FloatingTransferStation.Tests/FailingFirstSaveBoardStore.cs',
        'tests/FloatingTransferStation.Tests/GatedSettingsBoardStore.cs', 'tests/FloatingTransferStation.Tests/MainWindowInteractionTests.cs') },
    @{ Reason = '构建/测试基础设施脚本（引导、打包、卫生检查、安装/发行契约链；显式清单——新脚本默认 L3 全量）'; Category = 'C5'; Globs = @(
        'scripts/bootstrap-dotnet.ps1', 'scripts/bootstrap-inno.ps1', 'scripts/build-release.ps1', 'scripts/check-repo-hygiene.ps1',
        'scripts/run-adversarial.ps1', 'scripts/test-bootstrap-inno-path-contract.ps1', 'scripts/test-bootstrap-tool-versions.ps1',
        'scripts/test-installed-lifecycle.ps1', 'scripts/test-installer-cleanup.ps1', 'scripts/test-release-readiness.ps1') },
    @{ Reason = '构建基建（F2 §三.S KEEP-FULL：AssemblyInfo＝ThemeInfo 全局资源查找、GlobalUsings＝全 App 隐式 using，结构性全域）；XAML 转换器（§三.Q：唯一消费者 MainWindowStyles.xaml 资源声明→全部窗口测试间接）'; Category = 'C5'; Globs = @(
        'src/FloatingTransferStation/AssemblyInfo.cs', 'src/FloatingTransferStation/GlobalUsings.cs',
        'src/FloatingTransferStation/Converters/BooleanOrConverter.cs', 'src/FloatingTransferStation/Converters/NonEmptyTextConverter.cs') }
)

# ---- App 交互测试词根表（单一事实源：文件词根 → 该文件测试方法名的首段前缀全集）----
# 用途四合一：① 测试文件变更的派生过滤器；② 一致性守卫（方法名偏离即报错）；
# ③ 守卫反向校验（表条目无对应方法＝死条目）；④ L2 R-AppPartial 词根桥接（src 侧派生
# 骑在本表上——登记动作从「改选择器映射」变成「写测试时本来就要做的登记」）。
# 新增/拆分测试文件必须同步登记本表。
$interactionTestPrefixes = @{
    'BoardDragOverPerf'      = @('BoardDragOverPerf')
    'BoardEmptyState'        = @('BoardEmptyState')
    'BtnGridEvidence'        = @('Evidence')
    'CardAi'                 = @('CardAi')
    'CardAiEvidence'         = @('Evidence')
    'CardCaretAnchor'        = @('CardCaretAnchor')
    'CardDeleteButton'       = @('CardDeleteButton', 'CardOperationMatrix')
    'CardEditing'            = @('CardEditing')
    'CardGestures'           = @('CardGestures')
    'CardHalves'             = @('CardHalves')
    'CardNote'               = @('CardNote', 'CardOperationAvoidance', 'CardOperationMatrix')
    'CardOperationAvoidance' = @('CardOperationStrip', 'CardText', 'CardTextReservedInset')
    'CardRendering'          = @('BatchStructuralChange', 'BoardList', 'CardTemplate', 'CardText', 'CardThumbnail')
    'CategoryEditing'        = @('CategoryNameEditing', 'CategoryNameEditor', 'CategoryNameSave', 'CategoryNameSaveFailure', 'CategoryTab', 'CategoryTabLabelAndEditor', 'F2Rename')
    'CategoryOrder'          = @('ApplyCategoryOrderAsync', 'CategoryRail')
    'ClipboardCapture'       = @('ClipboardCapture')
    'ClipboardDelivery'      = @('CtrlC', 'LeftClickCopy', 'RightClick')
    'DailyReview'            = @('ReviewCategory', 'ReviewEditing', 'ReviewEditor', 'ReviewToolbar', 'ReviewWithoutEditorFocus')
    'DesignTheme'            = @('ThemeEvidence', 'ThemeSwitch')
    'DesignTokens'           = @('DesignTokens', 'WindowAnimationDurations')
    'DragAndDrop'            = @('AnimatedImageFile', 'BoardInsertionFeedback', 'BoardListBlankHover', 'CategoryDropFeedback', 'CategoryHoverDuringInternalDragKeepsSourcePanelAndSelection', 'DragLifecycle', 'ExternalDropRailProjection', 'ExternalFileDrop', 'ExternalOrderedFileDrop', 'ExternalRail', 'ExternalTextDrop', 'FailedExternalImport', 'InternalBatchCategoryDrop', 'InternalBatchEquivalentListDropDoesNotSaveAndKeepsSelection', 'InternalBatchListDrop', 'InternalCategoryDrop', 'InternalDragData', 'InternalImageBatch', 'MixedPinBatch', 'NormalCategoryDrop', 'PinnedBoundaryDrag', 'PinnedDrag', 'RejectedExternalDrop', 'SupportedExternalText', 'UnsupportedExternalDrag', 'UnsupportedOrInternalData')
    'EdgeGeometry'           = @('CollapsedGeometry', 'DisplayChange', 'ExpandedGeometry', 'ResetAction', 'SettingChange', 'WidthResize')
    'EdgeHide'               = @('EdgeHide')
    'EditingCommit'          = @('EditingCommit')
    'ExpandPipeline'         = @('ColdExpandImageCategory', 'ExpandImageCategory', 'ExpandWithSavedDeepOffset')
    'LauncherDiscovery'      = @('Discovery')
    'HeaderIconMetrics'      = @('HeaderActionIcons')
    'InterfaceFont'          = @('FontEvidence')
    'MotionTransitions'      = @('CardLift', 'CardMotionLayers', 'Cards', 'DeleteCardFade', 'FadeAnimation', 'Icons', 'NewItems', 'PinnedCard', 'PinnedMove', 'ScrollThumb', 'StatusOverlay')
    'PanelExpandPerf'        = @('PanelExpandPerf')
    'PanelHold'              = @('PanelHold')
    'PinnedDivider'          = @('Evidence', 'PinnedDivider')
    'QuickLaunch'            = @('QuickLaunch')
    'QuickLaunchContextMenu' = @('QuickLaunch')
    'QuickLaunchEdit'        = @('QuickLaunch')
    'QuickLaunchEditTarget'  = @('QuickLaunch')
    'QuickLaunchEvidence'    = @('Evidence')
    'QuickLaunchFavicon'     = @('Favicon')
    'QuickLaunchIconLoop'    = @('IconLoop')
    'QuickLaunchIcons'       = @('LauncherIcon')
    'QuickLaunchPerf'        = @('QuickLaunchPerf')
    'QuickLaunchPicker'      = @('InstalledAppsEnumerator', 'InstalledPicker', 'QuickLaunch')
    'QuickLaunchRename'      = @('QuickLaunch')
    'QuickLaunchSelection'   = @('QuickLaunch')
    'QuickLaunchSurface'     = @('QuickLaunch')
    'QuickLaunchSystemLocation' = @('SystemLocation')
    'QuickLaunchUrl'         = @('QuickLaunch', 'UrlInput')
    'RangeSelection'         = @('RangeSelection')
    'ReviewEditorEscape'     = @('ReviewEditor')
    'ReviewNav'              = @('ReviewNav')
    'ScrollViewport'         = @('ScrollViewport')
    'Search'                 = @('Search')
    'SelectionAndDeletion'   = @('BatchPinButton', 'BatchPinCommand', 'BatchPinExecutionGuard', 'DeletePending', 'DeleteShortcut', 'EmptyListBackgroundClearsSelectionWithoutChangingPanelOrScroll', 'Escape', 'HeaderClear', 'HeaderDeleteFailureRestoresMixedSelectionAndSuccessDeletesOnlySelection', 'HeaderDeleteWithoutSelection', 'HeaderDeleteWithoutSelectionFailureRestoresCategoryAndNewItemStaysUnselected', 'PinButton', 'SelectAllCommand', 'SelectedCountBadge', 'SelectionButtonTogglesPersistentTwoSignalFeedback', 'SelectionSurvivesAutoCollapseButClearsOnUserCategorySwitch', 'Shortcut', 'TrashButton')
    'TopmostImeSuppression'  = @('TopmostReassert')
    'Undo'                   = @('DeleteThenUndoFromPanel', 'Undo')
    'Updates'                = @('AutoUpdateCheckToggle', 'Evidence', 'UpdateButton', 'UpdateCheck')
    'VisualLifecycle'        = @('ActiveCategoryFeedback', 'BeginPanelDrag', 'BoardList', 'CardOperationColumns', 'CategoryFeedback', 'CategoryHover', 'CategorySwitch', 'CategoryTab', 'Close', 'Collapse', 'CollapsedCategoryHover', 'CollapsedHandle', 'CollapsedPlacement', 'CollapsedWindow', 'DisablingClientAreaAnimations', 'ExpandedCategoryHover', 'ExpandedPlacement', 'ExpandedRailReveal', 'ExternalRailPlacement', 'FailedFinalSave', 'Header', 'ImageCard', 'PanelContentAnimation', 'PinnedBoundary', 'RapidCollapsedCategoryHover', 'ResetAction', 'RootMouseEnter', 'StatusOverlay', 'TextCard', 'Topmost', 'WindowResources', 'WindowShell')
    'VisualTransitions'      = @('CollapseExit', 'PendingCollapse')
}

function Test-ExemptDocument([string]$file) {
    if ($file -like 'docs/*') {
        return $exemptDocExtensions -contains [System.IO.Path]::GetExtension($file).ToLowerInvariant()
    }
    if ($file -notlike '*/*') {
        return ([System.IO.Path]::GetExtension($file).ToLowerInvariant() -eq '.md') -and ($assertedRootDocs -notcontains $file)
    }
    return $false
}

function Get-ExplicitAreaMatches([string]$file) {
    $areaMatches = @()
    foreach ($rule in $script:overrides) {
        foreach ($glob in $rule.Globs) {
            if ($file -like $glob) { $areaMatches += $rule; break }
        }
    }
    return $areaMatches
}

function Get-FullFallback([string]$file) {
    foreach ($rule in $script:globalFulls) {
        foreach ($glob in $rule.Globs) {
            if ($file -like $glob) { return $rule }
        }
    }
    return $null
}

function Get-FilterTokens([string]$filter) {
    return @($filter -split '\|' | ForEach-Object { $_ -replace '^FullyQualifiedName~', '' } | Where-Object { $_ })
}

function Test-FilterHitsAny([string]$filter, [string[]]$targets) {
    # 恒非零内联校验的快速路径：任一 token 命中即真（双重早退，供逐文件派生与反向守卫复用）。
    foreach ($token in (Get-FilterTokens $filter)) {
        foreach ($target in $targets) {
            if ($target -like "*$token*") { return $true }
        }
    }
    return $false
}

function Get-ConventionArea([string]$file, [string[]]$appInventory, [string[]]$coreInventory) {
    # L2 约定派生（尝试序固定：R-AppPartial → R-AppStem → R-CoreService/R-CoreModel）。
    # 每条内联恒非零校验：对静态清单命中 0 条即该规则对该文件失效（落 L3），绝不静默空选。
    # 派生 token 须 ^[A-Za-z0-9_]+$（MSTest filter 转义安全；不匹配＝视同 0 命中落 L3）。
    # Core.* 派生按两工程并集校验（W2 实证：Core 服务的同族测试可落在 App 工程，
    # 如 PanelStateMachineTests/DropInsertionCalculatorTests；与 Core.* 区域 solution 级运行口径一致）。
    if ($file -like 'src/FloatingTransferStation/Views/MainWindow.*.cs' -and
        $file -notlike 'src/FloatingTransferStation/Views/MainWindow.xaml.cs') {
        $area = ((Split-Path -Leaf $file) -replace '\.cs$', '') -replace '^MainWindow\.', ''
        if ($area -match '^[A-Za-z0-9_]+$') {
            # 词根桥接 ∪ 字面（扁平 OR，无分组括号——vstest filter 语法不支持分组）：
            # 取并集而非二选一：QuickLaunch 字面 94/桥接 93（字面多捕跨类用例，向「多跑」方向安全）；
            # DragAndDrop 字面 0/桥接 36（桥接必要性实证，spec 附录 A #5）。
            $tokens = @()
            $prefixes = $script:interactionTestPrefixes[$area]
            if ($prefixes) { $tokens += @($prefixes | ForEach-Object { "MainWindowInteractionTests.$_" }) }
            $tokens += $area
            $filter = ($tokens | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
            if (Test-FilterHitsAny $filter $appInventory) {
                return @{ Name = "App.$area"; Filter = $filter; Derived = 'R-AppPartial 约定派生（词根桥接∪字面）' }
            }
        }
        return $null
    }
    # R-AppStem：App 工程 Views/Services/Converters/Controls 四目录非 MainWindow.* 形态
    if (($file -like 'src/FloatingTransferStation/Views/*.cs' -or $file -like 'src/FloatingTransferStation/Services/*.cs' -or
         $file -like 'src/FloatingTransferStation/Converters/*.cs' -or $file -like 'src/FloatingTransferStation/Controls/*.cs') -and
        $file -notlike 'src/FloatingTransferStation/Views/MainWindow*') {
        $stem = (((Split-Path -Leaf $file) -replace '\.cs$', '') -split '\.')[0]
        if ($stem -match '^[A-Za-z0-9_]+$') {
            $filter = "FullyQualifiedName~$stem"
            if (Test-FilterHitsAny $filter $appInventory) {
                return @{ Name = "App.$stem"; Filter = $filter; Derived = 'R-AppStem 约定派生（字面）' }
            }
        }
        return $null
    }
    if ($file -like 'src/FloatingTransferStation.Core/Services/*.cs' -or $file -like 'src/FloatingTransferStation.Core/Models/*.cs') {
        $stem = (((Split-Path -Leaf $file) -replace '\.cs$', '') -split '\.')[0]
        if ($stem -match '^[A-Za-z0-9_]+$') {
            $filter = "FullyQualifiedName~$stem"
            if (Test-FilterHitsAny $filter (@($appInventory + $coreInventory))) {
                return @{ Name = "Core.$stem"; Filter = $filter; Derived = 'R-Core* 约定派生（一服务/模型一类，并集校验）' }
            }
        }
    }
    return $null
}

function Get-DerivedArea([string]$file) {
    # 测试文件词根派生：MainWindowInteractionTests.<Area>*.cs → FQN~MainWindowInteractionTests.<前缀> 并集
    # （类名.方法名边界形态，废除全族放宽；过滤器与一致性守卫同源＝$interactionTestPrefixes）。
    # 词根未登记＝计划层直接 throw（2026-10-27 加固：防绕过 CLI 守卫的静默窄跑）。
    if ($file -like 'tests/FloatingTransferStation.Tests/MainWindowInteractionTests.*.cs') {
        $root = ((Split-Path -Leaf $file) -replace '^MainWindowInteractionTests\.', '') -replace '\.cs$', ''
        $prefixes = $script:interactionTestPrefixes[$root]
        if (-not $prefixes) { throw "MainWindowInteractionTests.$root.cs 的词根未登记 `$interactionTestPrefixes——计划层拒绝静默窄跑；新增测试文件必须同步登记词根表。" }
        $filter = ($prefixes | ForEach-Object { "FullyQualifiedName~MainWindowInteractionTests.$_" }) -join '|'
        return @{ Name = "Tests.$root"; Filter = $filter; Derived = 'App 测试文件词根派生' }
    }
    if ($file -like 'tests/FloatingTransferStation.Tests/*.cs') {
        $stem = (((Split-Path -Leaf $file) -replace '\.cs$', '') -split '\.')[0]
        return @{ Name = "Tests.$stem"; Filter = "FullyQualifiedName~$stem"; Derived = 'App 测试文件泛型规则（独立类名）' }
    }
    if ($file -like 'tests/FloatingTransferStation.Core.Tests/*') {
        return @{ Name = 'Core.Tests'; Filter = $null; Derived = '强制基线恒跑覆盖' }
    }
    return $null
}

function Get-UnknownFallbackReason([string]$file) {
    # L3 未知回退全量：标记行＋按形态分派的消除指引（C1 未登记 / C3 疑运行时产物）。
    if ($file -like 'src/FloatingTransferStation/Views/MainWindow.*.cs' -and $file -notlike '*/MainWindow.xaml.cs') {
        $area = ((Split-Path -Leaf $file) -replace '\.cs$', '') -replace '^MainWindow\.', ''
        return "[affected-full] $file → 未命中映射（R-AppPartial 形态但词根/字面命中 0 条）——消除：为该功能补测试并在 `$interactionTestPrefixes 登记 $area 的前缀，或显式登记 L1 缩圈/全量理由 [category=C1]"
    }
    if ($file -like 'src/*' -or $file -like 'tests/*' -or $file -like 'scripts/*') {
        $stem = (((Split-Path -Leaf $file) -replace '\.[^.]*$', '') -split '\.')[0]
        return "[affected-full] $file → 未命中映射（约定派生 0 命中/无对应规则）——消除：补 $stem 对应测试、或登记 L1 区域映射、或登记全量表理由 [category=C1]"
    }
    return "[affected-full] $file → 未命中映射（未知路径，疑运行时产物）——消除：加入 .gitignore 或变更集忽略清单，勿登记映射 [category=C3]"
}

function Get-TestMethodNames([string]$path) {
    # 静态解析：含 TestMethod 的特性行（覆盖 [TestMethod()]/[STATestMethod]/[TestMethod, Timeout(..)]
    # 与特性同行签名），向下扫描至下一特性行或 25 行窗口；跳过 [Ignore]（不执行的方法不入清单）。
    $names = @()
    $lines = Get-Content -LiteralPath $path
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -notmatch '\[[^\]]*TestMethod[^\]]*\]') { continue }
        $ignored = $false
        $method = $null
        if ($lines[$i] -match '(?:public|internal)\s+(?:async\s+)?(?:Task|void)\s+([A-Za-z0-9_]+)\s*\(') {
            $method = $Matches[1] # 特性与签名同行（签名正则不会误匹配特性文本本身）
        }
        for ($j = $i + 1; $j -lt [Math]::Min($i + 25, $lines.Count); $j++) {
            if ($j -gt $i -and $lines[$j] -match '\[[^\]]*TestMethod[^\]]*\]') { break }
            if ($lines[$j] -match '^\s*\[Ignore') { $ignored = $true }
            if ($lines[$j] -match '(?:public|internal)\s+(?:async\s+)?(?:Task|void)\s+([A-Za-z0-9_]+)\s*\(') { $method = $Matches[1]; break }
        }
        if ($method -and -not $ignored) { $names += $method }
    }
    return $names
}

function Get-TestInventory([string]$projectDir) {
    # 清单条目「类名.方法名」（FQN 去 namespace 的子串等价物），用于过滤器恒非零静态校验。
    if (-not (Test-Path $projectDir)) { return @() }
    $inventory = @()
    foreach ($file in (Get-ChildItem $projectDir -Filter '*.cs' -File)) {
        $class = ($file.BaseName -split '\.')[0]
        foreach ($method in (Get-TestMethodNames $file.FullName)) { $inventory += "$class.$method" }
    }
    return $inventory
}

function Test-InteractionTestNaming([string]$testsDir) {
    # 一致性守卫：每个交互测试文件的方法名首段 ∈ 词根表；表条目无对应方法＝死条目。
    # 返回违规清单（'文件: 条目（说明）'）；空清单＝通过。词根表与派生过滤器同源（单一事实源）。
    $violations = @()
    $files = @(Get-ChildItem $testsDir -Filter 'MainWindowInteractionTests.*.cs' -File)
    $seenRoots = @{}
    foreach ($file in $files) {
        $root = ($file.Name -replace '^MainWindowInteractionTests\.', '') -replace '\.cs$', ''
        $seenRoots[$root] = $true
        $prefixes = $script:interactionTestPrefixes[$root]
        if (-not $prefixes) { $violations += "$($file.Name): 词根表未登记（新增测试文件须同步登记 `$interactionTestPrefixes）"; continue }
        $methods = @(Get-TestMethodNames $file.FullName)
        foreach ($method in $methods) {
            $segment = ($method -split '_')[0]
            if ($prefixes -notcontains $segment) { $violations += "$($file.Name): $method（首段 '$segment' 不在词根表）" }
        }
        foreach ($prefix in $prefixes) {
            if (-not (@($methods | Where-Object { ($_ -split '_')[0] -eq $prefix }).Count)) {
                $violations += "$($file.Name): 词根表死条目 '$prefix'（无对应测试方法）"
            }
        }
    }
    foreach ($root in @($script:interactionTestPrefixes.Keys)) {
        if (-not $seenRoots[$root]) { $violations += "词根表条目 '$root' 无对应测试文件（陈旧条目）" }
    }
    return $violations
}

function Test-TrackedSourceCoverage([string[]]$sourceFiles, [string[]]$appInventory, [string[]]$coreInventory) {
    # 反向守卫：每个跟踪 src .cs/.xaml ∈ L1 例外 ∪ 全量表 ∪ L2-可派生（三态完备，
    # 不允许任何跟踪文件落到 L3）。挂载点＝CLI 主执行计划前 fail fast；未跟踪新文件
    # 不经此守卫（走 L3 指引）——「先写源码后补测试」的开发流不被阻断，守卫只在
    # 文件被跟踪（提交）时点强制登记。失败输出三段式：文件 → 缺什么 → 怎么补（三选一）。
    $violations = @()
    foreach ($file in @($sourceFiles)) {
        $file = $file -replace '\\', '/'
        if (@(Get-ExplicitAreaMatches $file).Count) { continue }
        if (Get-FullFallback $file) { continue }
        if (Get-ConventionArea $file $appInventory $coreInventory) { continue }
        $stem = (((Split-Path -Leaf $file) -replace '\.[^.]*$', '') -split '\.')[0]
        $violations += "$file → 缺什么：例外表/全量表均未登记且四条约定派生规则全部 0 命中 → 怎么补（三选一）：补 $stem 同名/同族测试并（partial 形态）登记 `$interactionTestPrefixes 词根｜L1 缩圈登记（例 Filter＝FQN~$stem）｜全量表登记并写明理由"
    }
    return $violations
}

function Assert-ChangesetNoisePrefixes {
    # 防后门校验：忽略前缀必须是目录前缀（以 / 结尾，不得针对根文件），且不得覆盖产品树
    # （src/tests/scripts/installer/docs/.github——前缀命中产品树即 throw，启动时校验）。
    foreach ($prefix in @($script:changesetNoisePrefixes)) {
        if ($prefix -notmatch '/$') { throw "变更集忽略清单前缀 '$prefix' 必须是目录前缀（以 / 结尾）——不得针对根跟踪文件。" }
        if ($prefix -match '^(\.?github|src|tests|scripts|installer|docs)/') { throw "变更集忽略清单前缀 '$prefix' 覆盖产品树（src/tests/scripts/installer/docs/.github），禁止。" }
    }
}

function Test-ChangesetNoise([string]$file) {
    foreach ($prefix in @($script:changesetNoisePrefixes)) {
        if ($file -like "$prefix*") { return $true }
    }
    return $false
}

function Filter-ChangesetNoise([string[]]$files) {
    # 推导层过滤：命中忽略前缀的路径不进计划，逐条打印 [affected-noise] 行。
    $kept = @()
    foreach ($file in @($files)) {
        if (Test-ChangesetNoise $file) { Write-Host "[affected-noise] $file" } else { $kept += $file }
    }
    return $kept
}

function Assert-FilterSelectsTests([string]$areaName, [string]$filter, [string[]]$appInventory, [string[]]$coreInventory) {
    # 漂移检查：Core.* 区域走 solution 级（两工程任一命中即可）；其余区域跑 App 工程，必须命中 App。
    # 整组过滤器命中 0 条＝映射腐烂；任一词根在目标工程命中 0 条＝死词根。两者都 fail fast。
    $targets = if ($areaName -like 'Core.*') { $appInventory + $coreInventory } else { $appInventory }
    $tokens = Get-FilterTokens $filter
    $dead = @()
    $live = $false
    foreach ($token in $tokens) {
        $hit = @($targets | Where-Object { $_ -like "*$token*" }).Count -gt 0
        if ($hit) { $live = $true } else { $dead += $token }
    }
    if (-not $live) { throw "区域 $areaName 的过滤器命中 0 条测试（$filter）——映射已腐烂，请修正映射或登记词根表。" }
    if ($dead.Count) { throw "区域 $areaName 过滤器死词根：$($dead -join ', ')（在目标测试工程中命中 0 条）——请清理映射。" }
}

function Get-AffectedTestPlan([string[]]$changedFiles, [string[]]$appInventory, [string[]]$coreInventory, [switch]$ForceFull) {
    # 纯决策函数：变更集 → 计划（Mode: Full/Affected/Exempt）。判定序 v2（四层）：
    # 豁免 → L1 例外表（并集，命中优先于一切派生）→ L1 全量表（[affected-full] 标记行）
    # → L2 约定派生/测试文件派生（恒非零内联）→ L3 未知回退全量（理由＋消除指引）。
    # 清单必传：恒非零校验内联依赖真实测试清单，拒绝盲算。
    if ((-not $appInventory -or -not $appInventory.Count) -and (-not $coreInventory -or -not $coreInventory.Count)) {
        throw 'Get-AffectedTestPlan 需要 App/Core 真实测试清单（至少其一非空）——恒非零校验内联依赖清单，拒绝盲算。'
    }
    $selected = [ordered]@{}
    $fullReasons = [System.Collections.Generic.List[string]]::new()
    $exemptFiles = [System.Collections.Generic.List[string]]::new()
    foreach ($file in @($changedFiles | Where-Object { $_ })) {
        $file = $file -replace '\\', '/'
        if (Test-ExemptDocument $file) { $exemptFiles.Add($file); continue }
        $explicit = @(Get-ExplicitAreaMatches $file)
        if ($explicit.Count) {
            foreach ($match in $explicit) {
                if (-not $selected.Contains($match.Name)) {
                    $selected[$match.Name] = @{ Filters = [System.Collections.Generic.List[string]]::new(); Files = [System.Collections.Generic.List[string]]::new(); Script = $null }
                }
                if ($match.Filter -and -not $selected[$match.Name].Filters.Contains($match.Filter)) { $selected[$match.Name].Filters.Add($match.Filter) }
                if ($match.Script) { $selected[$match.Name].Script = $match.Script }
                $selected[$match.Name].Files.Add($file)
            }
            continue
        }
        $fullRule = Get-FullFallback $file
        if ($fullRule) { $fullReasons.Add("[affected-full] $file → $($fullRule.Reason) [category=$($fullRule.Category)]"); continue }
        $derived = Get-DerivedArea $file
        if (-not $derived -and $file -like 'src/*') { $derived = Get-ConventionArea $file $appInventory $coreInventory }
        if ($derived) {
            if (-not $selected.Contains($derived.Name)) {
                $selected[$derived.Name] = @{ Filters = [System.Collections.Generic.List[string]]::new(); Files = [System.Collections.Generic.List[string]]::new(); Script = $null }
            }
            if ($derived.Filter -and -not $selected[$derived.Name].Filters.Contains($derived.Filter)) { $selected[$derived.Name].Filters.Add($derived.Filter) }
            $selected[$derived.Name].Files.Add($file)
            continue
        }
        $fullReasons.Add((Get-UnknownFallbackReason $file))
    }
    $mode = 'Affected'
    if ($ForceFull -or $fullReasons.Count) { $mode = 'Full' }
    elseif (-not $selected.Count) { $mode = 'Exempt' }
    return [pscustomobject]@{ Mode = $mode; Selected = $selected; FullReasons = $fullReasons; ExemptFiles = $exemptFiles }
}

function Show-TestPlan($plan) {
    Write-Host "计划模式：$($plan.Mode)。"
    if ($plan.Mode -eq 'Full') {
        if ($plan.FullReasons.Count) {
            Write-Host '回退/强制全量。理由：'
            foreach ($reason in $plan.FullReasons) { Write-Host "  - $reason" }
        } else {
            Write-Host '-Full 强制全量。'
        }
    } elseif ($plan.Mode -eq 'Exempt') {
        if ($plan.ExemptFiles.Count) {
            Write-Host '纯文档变更（豁免类），未选任何测试：'
            foreach ($file in $plan.ExemptFiles) { Write-Host "  - $file" }
        } else {
            Write-Host '变更集为空（git 干净），未选任何测试。'
        }
        Write-Host '提示：仓库卫生检查（check-repo-hygiene.ps1）与 CI quality 权威全量门仍然兜底。'
    } else {
        foreach ($name in $plan.Selected.Keys) {
            $entry = $plan.Selected[$name]
            $entry.Filter = if ($entry.Filters.Count) { $entry.Filters -join '|' } else { $null }
            $filterText = if ($entry.Script) { "契约测试子进程：$($entry.Script)" }
            elseif ($entry.Filter) { $entry.Filter } else { '（强制基线覆盖，无附加 filter）' }
            Write-Host "区域 $name → filter=$filterText；理由：$($entry.Files -join ', ')"
        }
    }
}

function Invoke-TestRun([string]$Title, [string]$Project, [string]$Filter) {
    $prefix = 'affected-' + ($Title -replace '[^A-Za-z0-9._-]', '_')
    $results = Join-Path $repoRoot 'TestResults'
    # LogFilePrefix 使 solution 级运行的两个测试工程各写独立 TRX；运行前清理同名前缀旧文件防累积。
    Get-ChildItem $results -Filter "$prefix*.trx" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    $testArgs = @('test', $Project, '-c', $Configuration, '--no-build', '--results-directory', $results, '--logger', "trx;LogFilePrefix=$prefix")
    if ($Filter) { $testArgs += @('--filter', $Filter) }
    Write-Host "运行 $Title ..."
    & $DotnetPath @testArgs | Out-Host # dotnet 输出直送控制台，不进函数返回值（保持计数纯净）
    if ($LASTEXITCODE -ne 0) { throw "$Title 失败（dotnet 退出码 $LASTEXITCODE）。" }
    $trxFiles = @(Get-ChildItem $results -Filter "$prefix*.trx" -ErrorAction SilentlyContinue)
    if (-not $trxFiles.Count) { throw "$Title 未找到 TRX 结果（$prefix*.trx）。" }
    $total = 0; $passed = 0; $failed = 0
    foreach ($trx in $trxFiles) {
        $c = ([xml](Get-Content $trx.FullName)).TestRun.ResultSummary.Counters
        $total += [int]$c.total; $passed += [int]$c.passed; $failed += [int]$c.failed
    }
    if ($total -eq 0) { throw "$Title 选中 0 条测试——空选集视为失败，请检查映射或显式 -Areas/-Full。" }
    Write-Host "$Title 通过：total=$total passed=$passed failed=$failed。"
    return $total
}

if ($FTS_SELECTOR_DEFINE_ONLY) { return }

if ($PSBoundParameters.ContainsKey('Base') -and $PSBoundParameters.ContainsKey('Scope')) {
    # -Scope 有 param 默认值（恒非空），必须用 PSBoundParameters 判显式冲突——裸真值判断会误杀 -Base HEAD。
    throw '-Base（原始基线覆盖，发行列车/回放用）与 -Scope（Edit/Integrate 双模式）不得同时给出。'
}
Assert-ChangesetNoisePrefixes

if (-not $DotnetPath) {
    $local = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
    $DotnetPath = if (Test-Path $local) { $local } else { 'dotnet' }
}
$solution = Join-Path $repoRoot 'FloatingTransferStation.slnx'
$appTests = Join-Path $appTestsDir 'FloatingTransferStation.Tests.csproj'
$coreTests = Join-Path $coreTestsDir 'FloatingTransferStation.Core.Tests.csproj'

# 一致性守卫与过滤器漂移检查（计划前 fail fast）
$namingViolations = @(Test-InteractionTestNaming $appTestsDir)
if ($namingViolations.Count) {
    $namingViolations | ForEach-Object { Write-Host "一致性守卫违规：$_" }
    throw "交互测试命名一致性守卫发现 $($namingViolations.Count) 处违规（见上）。请修正方法名或登记词根表。"
}
$appInventory = @(Get-TestInventory $appTestsDir)
$coreInventory = @(Get-TestInventory $coreTestsDir)
foreach ($rule in $overrides) {
    if ($rule.Kind -eq 'Script') { continue } # 契约区域无 MSTest filter，恒非零由契约测试自身承担
    Assert-FilterSelectsTests $rule.Name $rule.Filter $appInventory $coreInventory
}

# 反向守卫（计划前 fail fast）：src/** 每个跟踪 .cs/.xaml 三态完备（例外表∪全量表∪可派生）
$trackedSources = @(& git -C $repoRoot -c core.quotepath=false ls-files -- src/)
if ($LASTEXITCODE -ne 0) { throw "git ls-files 失败（退出码 $LASTEXITCODE）；反向守卫无法枚举跟踪源文件。" }
$trackedSources = @($trackedSources | Where-Object { $_ -like 'src/*.cs' -or $_ -like 'src/*.xaml' })
$coverageViolations = @(Test-TrackedSourceCoverage $trackedSources $appInventory $coreInventory)
if ($coverageViolations.Count) {
    $coverageViolations | ForEach-Object { Write-Host "反向守卫违规：$_" }
    throw "跟踪源文件三态覆盖守卫发现 $($coverageViolations.Count) 处违规（见上：文件→缺什么→怎么补）。"
}

if ($PSBoundParameters.ContainsKey('ChangeSet')) {
    $changed = @($ChangeSet)
    Write-Host "变更集来自 -ChangeSet 参数（$($changed.Count) 个文件，契约测试/历史回放模式；显式空＝空变更集）。"
} elseif ($Full) {
    # -Full 强制全量：变更集仅用于打印理由，git 失败不阻断全量执行。
    $changed = @()
} else {
    if (-not $Base) {
        # -Scope 双模式（默认 Integrate＝整条分支集成语义；Edit＝本次编辑反馈）；
        # -Base 为原始基线覆盖（与 -Scope 同给已在上方报错）。
        if ($Scope -eq 'Edit') { $Base = 'HEAD' }
        else {
            $Base = (& git -C $repoRoot merge-base origin/main HEAD)
            if ($LASTEXITCODE -ne 0 -or -not $Base) { throw '无法解析 merge-base origin/main HEAD；请确认远端与当前分支。' }
            $Base = "$Base".Trim()
        }
    }
    # core.quotepath=false：非 ASCII 路径（如安装器 .iss）不输出 C 风格引号转义，防 glob 失配（E11 教训）。
    $diffFiles = @(& git -C $repoRoot -c core.quotepath=false diff --no-renames --name-only $Base)
    if ($LASTEXITCODE -ne 0) { throw "git diff $Base 失败（退出码 $LASTEXITCODE）；拒绝把 git 故障当空变更集处理。" }
    $untrackedFiles = @(& git -C $repoRoot -c core.quotepath=false ls-files --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw "git ls-files 失败（退出码 $LASTEXITCODE）；拒绝把 git 故障当空变更集处理。" }
    $changed = @($diffFiles + $untrackedFiles) | Where-Object { $_ } | Sort-Object -Unique
    Write-Host "基线 $Base（-Scope $Scope）；变更集 $($changed.Count) 个文件。"
}
$changed = @(Filter-ChangesetNoise $changed)

$plan = Get-AffectedTestPlan $changed $appInventory $coreInventory -ForceFull:$Full

if ($Areas) {
    $requested = @($Areas -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if (-not $requested) { throw "-Areas '$Areas' 解析后为空；请至少给出一个有效区域名。" }
    $known = @($overrides | ForEach-Object { $_.Name } | Sort-Object -Unique)
    $unknown = @($requested | Where-Object { $known -notcontains $_ })
    if ($unknown) { throw "未知区域：$($unknown -join ', ')。当前可选：$($known -join ', ')" }
    $selected = [ordered]@{}
    foreach ($rule in $overrides) {
        if ($requested -notcontains $rule.Name) { continue }
        if (-not $selected.Contains($rule.Name)) {
            $selected[$rule.Name] = @{ Filters = [System.Collections.Generic.List[string]]::new(); Files = [System.Collections.Generic.List[string]]::new(); Script = $null }
        }
        if ($rule.Filter -and -not $selected[$rule.Name].Filters.Contains($rule.Filter)) { $selected[$rule.Name].Filters.Add($rule.Filter) }
        if ($rule.Script) { $selected[$rule.Name].Script = $rule.Script }
    }
    foreach ($name in $selected.Keys) { $selected[$name].Files.Add('（-Areas 显式指定）') }
    $plan.Selected = $selected
    if ($plan.Mode -eq 'Full') {
        Write-Host '注意：变更集存在回退全量的文件，全量优先于 -Areas 显式选区（未知路径保护）。'
    } else {
        $plan.Mode = 'Affected' # 显式选区即执行意图：豁免类变更集不得吞掉 -Areas
    }
} else {
    # 派生区域过滤器同样须过漂移检查（词根表改动即时暴露死词根）。
    foreach ($name in @($plan.Selected.Keys)) {
        $entry = $plan.Selected[$name]
        if ($entry.Filters.Count) { Assert-FilterSelectsTests $name ($entry.Filters -join '|') $appInventory $coreInventory }
    }
}
Show-TestPlan $plan

if ($Explain) { Write-Host '-Explain：仅打印计划，不执行。'; exit 0 }

if ($plan.Mode -eq 'Full') {
    & $DotnetPath test $solution -c $Configuration --results-directory (Join-Path $repoRoot 'TestResults') --logger 'trx;LogFilePrefix=affected-full'
    exit $LASTEXITCODE
}
if ($plan.Mode -eq 'Exempt') {
    Write-Host '纯文档变更，未跑测试（exit 0）。'
    exit 0
}

& $DotnetPath build $coreTests -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $DotnetPath build $appTests -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$baselineTotal = Invoke-TestRun 'CoreBaseline' $coreTests $null
$areaTotals = @()
foreach ($name in $plan.Selected.Keys) {
    $entry = $plan.Selected[$name]
    if ($entry.Script) {
        # Kind=Script 区域（选择器契约测试）：Core 基线后子进程运行，非 0 退出＝失败；
        # 契约测试损坏到无法运行时同样非 0 退出——不可能静默绿灯。
        $scriptPath = Join-Path $repoRoot $entry.Script
        Write-Host "运行区域 $name（契约测试子进程：$($entry.Script)）..."
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $scriptPath | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "区域 $name（$($entry.Script)）失败（退出码 $LASTEXITCODE）。" }
        $areaTotals += '契约'
        continue
    }
    $entry.Filter = if ($entry.Filters.Count) { $entry.Filters -join '|' } else { $null }
    if (-not $entry.Filter) { continue }
    if ($name -like 'Core.*') {
        # Core 侧区域走 solution 级「带 filter」运行（覆盖 Core.Tests 与 App 两个测试工程）；
        # 契约只禁止 solution 级「无 filter」全量，此处带 filter 合规。
        $areaTotals += (Invoke-TestRun $name $solution $entry.Filter)
    } else {
        $areaTotals += (Invoke-TestRun $name $appTests $entry.Filter)
    }
}
Write-Host "强制基线 Core.Tests（$baselineTotal 条）＋受影响区域 $($plan.Selected.Keys -join ', ')（$($areaTotals -join ' + ') 条）；全量由 CI quality 承担。"
Write-Host '受影响范围测试全部通过。'
exit 0
