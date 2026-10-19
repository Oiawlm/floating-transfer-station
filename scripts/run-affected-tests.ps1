# 分层测试协议选择器：按变更集的三分语义（豁免/精确区域/全量）选择测试计划。
# 语义唯一权威来源：docs/design-contracts.md 契约「受影响测试选区协议」（#32）；
# 本头注与 CONTRIBUTING.md 只作引用，不在别处维护第二份语义副本。
# 两种模式：默认按 merge-base origin/main 取整条分支变更集（分支集成语义）；
# 开发循环的「本次编辑反馈」用 -Base HEAD。-ChangeSet 显式注入变更集（契约测试/历史回放）。
# Core.Tests 整项目恒跑（强制基线）；除 -Full/回退外不运行 solution 级无 filter 的全量测试。
# 点源加载（先 $FTS_SELECTOR_DEFINE_ONLY=$true 再 . 本文件）只定义数据与函数，供契约测试复用。
[CmdletBinding()]
param(
    [string]$Base,
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

# ---- 精确区域规则（Glob 相对仓库根、正斜杠；一文件可命中多规则＝区域并集）----
# 快捷启动（启动器）：瓦片/URL/选择器/图标/目录/存储/Shell 互操作/网格几何（取证 2026-10-19 缩圈）。
$rules = @(
    @{ Name = 'Launcher'; Filter = 'FullyQualifiedName~QuickLaunch|FullyQualifiedName~UrlInput'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.QuickLaunch*.cs',
        'src/FloatingTransferStation/Views/LauncherUrlInputWindow*', 'src/FloatingTransferStation/Views/InstalledAppPickerWindow*',
        'src/FloatingTransferStation/Views/LauncherEditTargetWindow*', 'src/FloatingTransferStation/Launcher/*',
        'src/FloatingTransferStation/Services/ShellInterop.cs',
        'src/FloatingTransferStation.Core/Services/Launcher*.cs', 'src/FloatingTransferStation.Core/Services/VolatileLauncherStore.cs',
        'src/FloatingTransferStation.Core/Services/ILauncherStore.cs', 'src/FloatingTransferStation.Core/Design/LauncherGridMath.cs',
        'src/FloatingTransferStation.Core/Models/LauncherItem.cs', 'src/FloatingTransferStation.Core/Models/LauncherIconKey.cs') },
    # 设置窗口（双栏/各节/几何/插件/AI/标签顺序；Metrics 为取证缩圈：消费者仅设置窗族）
    @{ Name = 'SettingsWindow'; Filter = 'FullyQualifiedName~SettingsWindow'; Globs = @(
        'src/FloatingTransferStation/Views/SettingsWindow*', 'src/FloatingTransferStation/Views/WindowShellClip.cs',
        'src/FloatingTransferStation.Core/Design/SettingsWindowMetrics.cs') },
    # 生命周期：安装器、发行元数据/就绪断言（含其断言的 SettingsWindow.xaml）、自启、单实例、数据目录边界
    @{ Name = 'Lifecycle'; Filter = 'FullyQualifiedName~Lifecycle'; Globs = @(
        'installer/*', 'src/FloatingTransferStation/Services/WindowsStartupManager.cs',
        'src/FloatingTransferStation/Services/SingleInstanceGuard.cs', 'src/FloatingTransferStation/Services/AppLifecycleService.cs',
        'src/FloatingTransferStation/Services/WindowsDataDirectorySettings.cs', 'src/FloatingTransferStation.Core/ProductIdentity.cs',
        'src/FloatingTransferStation/Views/SettingsWindow.xaml') },
    # 公开发行材料（README/CONTRIBUTING/CHANGELOG/LICENSE/ROADMAP/version.txt）：
    # LifecycleTests.ReleaseMetadata 以内容/存在断言锁定这些文件的用户契约与协议记载。
    @{ Name = 'PublicReleaseMaterials'; Filter = 'FullyQualifiedName~ReleaseMetadata'; Globs = @(
        'README.md', 'CONTRIBUTING.md', 'CHANGELOG.md', 'LICENSE', 'ROADMAP.md', 'version.txt') },
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
    @{ Name = 'AutoCleanup'; Filter = 'FullyQualifiedName~AutoCleanup'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.AutoCleanup.cs') },
    @{ Name = 'CardAi'; Filter = 'FullyQualifiedName~CardAi'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.CardAi.cs') },
    @{ Name = 'CardGestures'; Filter = 'FullyQualifiedName~CardGestures|FullyQualifiedName~CardHalves'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.CardGestures.cs', 'src/FloatingTransferStation/Views/CardGestureZones.cs') },
    @{ Name = 'CardTextEditing'; Filter = 'FullyQualifiedName~CardEditing|FullyQualifiedName~CardCaretAnchor|FullyQualifiedName~CardNote'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.CardTextEditing.cs') },
    @{ Name = 'CategoryEditing'; Filter = 'FullyQualifiedName~Category'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.CategoryEditing.cs', 'src/FloatingTransferStation/Views/CategoryFeedbackAnimation.cs') },
    # CtrlC 为取证修订：原 ClipboardDelivery 词根在测试方法名中不存在（欠选）
    @{ Name = 'ClipboardDelivery'; Filter = 'FullyQualifiedName~RightClickCopy|FullyQualifiedName~CtrlC'; Globs = @(
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
    @{ Name = 'Search'; Filter = 'FullyQualifiedName~Search'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.Search.cs') },
    @{ Name = 'Selection'; Filter = 'FullyQualifiedName~Selection|FullyQualifiedName~Undo'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.SelectionAndDeletion.cs') },
    @{ Name = 'Settings'; Filter = 'FullyQualifiedName~SettingsWindow|FullyQualifiedName~Preferences'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.Settings.cs') },
    @{ Name = 'Updates'; Filter = 'FullyQualifiedName~Update'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.Updates.cs') },
    # 控件/转换器
    @{ Name = 'TrimAwareTextBlock'; Filter = 'FullyQualifiedName~TrimAwareTextBlock'; Globs = @(
        'src/FloatingTransferStation/Controls/TrimAwareTextBlock.cs') },
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
    # Core 服务显式覆写（「一服务一类」泛型前的特例：文件 stem 不等于测试类名）
    @{ Name = 'Core.AiOrganizePrompt'; Filter = 'FullyQualifiedName~AiTextOrganizer'; Globs = @(
        'src/FloatingTransferStation.Core/Services/AiOrganizePrompt.cs') },
    @{ Name = 'Core.AtomicTextWriter'; Filter = 'FullyQualifiedName~LocalStore'; Globs = @(
        'src/FloatingTransferStation.Core/Services/AtomicTextWriter.cs') },
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
        'src/FloatingTransferStation.Core/Services/ManagedImagePath.cs') }
)

# ---- 全量类（保留但收缩；逐条打印触发文件与理由。判定顺序在精确区域之后：显式映射优先）----
$fullRules = @(
    @{ Reason = '共享窗口壳（主窗 XAML/代码后置/App 入口）'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.xaml', 'src/FloatingTransferStation/Views/MainWindow.xaml.cs',
        'src/FloatingTransferStation/App.xaml', 'src/FloatingTransferStation/App.xaml.cs') },
    @{ Reason = '共享样式/主题资源字典（六窗口合并或运行时全局切换，取证 2026-10-19）'; Globs = @(
        'src/FloatingTransferStation/Resources/*') },
    @{ Reason = '设置宿主接口——ApplyPreferences 等成员是跨区域测试驱动 API（取证 2026-10-19：设置/清理/选择/更新/热键/迁移八族直用）'; Globs = @(
        'src/FloatingTransferStation/Services/ISettingsHost.cs', 'src/FloatingTransferStation/Services/IImageNormalizer.cs') },
    @{ Reason = '跨区域 Core 接口（存储/偏好/密钥/自启契约的多屏共享机器，取证 2026-10-19）'; Globs = @(
        'src/FloatingTransferStation.Core/Services/IAtomicTextWriter.cs', 'src/FloatingTransferStation.Core/Services/IBoardStore.cs',
        'src/FloatingTransferStation.Core/Services/IPreferencesStore.cs', 'src/FloatingTransferStation.Core/Services/ISecretProtector.cs',
        'src/FloatingTransferStation.Core/Services/IStartupManager.cs') },
    @{ Reason = '未登记的新 Core 接口（I*.cs）——影响面未知，保守全量；缩圈须先入精确映射表'; Globs = @(
        'src/FloatingTransferStation.Core/Services/I*.cs') },
    @{ Reason = 'Core 视图模型——全部交互测试的共享机器（取证 2026-10-19：MainWindowViewModel 被 40+ 测试文件直用）'; Globs = @(
        'src/FloatingTransferStation.Core/ViewModels/*') },
    @{ Reason = '全局设计 token——全部界面与动效的唯一数值来源（取证 2026-10-19）'; Globs = @(
        'src/FloatingTransferStation.Core/Design/*') },
    @{ Reason = '原生互操作/窗口效果——全域消费（取证 2026-10-19）'; Globs = @(
        'src/FloatingTransferStation/Services/DwmWindowEffects.cs', 'src/FloatingTransferStation/Services/NativeMethods.cs') },
    @{ Reason = '构建/依赖/解决方案配置'; Globs = @(
        'Directory.Build.props', 'Directory.Packages.props', 'FloatingTransferStation.slnx', 'global.json', 'nuget.config') },
    @{ Reason = '窗口/卡片视觉与动画基础设施——测试方法名与源文件名不同族，保守回退全量'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.VisualTransitions.cs', 'src/FloatingTransferStation/Views/MainWindow.CardEntrance.cs',
        'src/FloatingTransferStation/Views/FadeAnimation.cs', 'src/FloatingTransferStation/Views/LiftAnimation.cs') },
    @{ Reason = '共享测试设施'; Globs = @(
        'tests/FloatingTransferStation.Tests/TestDirectory.cs', 'tests/FloatingTransferStation.Tests/GlobalUsings.cs',
        'tests/FloatingTransferStation.Tests/BoardCreatedAtTestHelpers.cs', 'tests/FloatingTransferStation.Tests/FailingFirstSaveBoardStore.cs',
        'tests/FloatingTransferStation.Tests/GatedSettingsBoardStore.cs', 'tests/FloatingTransferStation.Tests/MainWindowInteractionTests.cs') },
    @{ Reason = '构建/测试基础设施脚本（选择器、引导、打包、卫生检查）'; Globs = @(
        'scripts/*') }
)

# ---- App 交互测试词根表（单一事实源：文件词根 → 该文件测试方法名的首段前缀全集）----
# 用途三合一：① 测试文件变更的派生过滤器；② 一致性守卫（方法名偏离即报错）；
# ③ 守卫反向校验（表条目无对应方法＝死条目）。新增/拆分测试文件必须同步登记本表。
$interactionTestPrefixes = @{
    'BoardDragOverPerf'      = @('BoardDragOverPerf')
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
    'ClipboardDelivery'      = @('CtrlC', 'RightClickCopy')
    'DailyReview'            = @('ReviewCategory', 'ReviewEditing', 'ReviewEditor', 'ReviewToolbar', 'ReviewWithoutEditorFocus')
    'DesignTheme'            = @('ThemeEvidence', 'ThemeSwitch')
    'DesignTokens'           = @('DesignTokens', 'WindowAnimationDurations')
    'DragAndDrop'            = @('AnimatedImageFile', 'BoardInsertionFeedback', 'BoardListBlankHover', 'CategoryDropFeedback', 'CategoryHoverDuringInternalDragKeepsSourcePanelAndSelection', 'DragLifecycle', 'ExternalDropRailProjection', 'ExternalFileDrop', 'ExternalOrderedFileDrop', 'ExternalRail', 'ExternalTextDrop', 'FailedExternalImport', 'InternalBatchCategoryDrop', 'InternalBatchEquivalentListDropDoesNotSaveAndKeepsSelection', 'InternalBatchListDrop', 'InternalCategoryDrop', 'InternalDragData', 'InternalImageBatch', 'MixedPinBatch', 'NormalCategoryDrop', 'PinnedBoundaryDrag', 'PinnedDrag', 'RejectedExternalDrop', 'SupportedExternalText', 'UnsupportedExternalDrag', 'UnsupportedOrInternalData')
    'EdgeGeometry'           = @('CollapsedGeometry', 'DisplayChange', 'ExpandedGeometry', 'ResetAction', 'SettingChange', 'WidthResize')
    'EdgeHide'               = @('EdgeHide')
    'ExpandPipeline'         = @('ColdExpandImageCategory', 'ExpandImageCategory', 'ExpandWithSavedDeepOffset')
    'HeaderIconMetrics'      = @('HeaderActionIcons')
    'MotionTransitions'      = @('CardLift', 'CardMotionLayers', 'Cards', 'DeleteCardFade', 'FadeAnimation', 'Icons', 'NewItems', 'PinnedCard', 'PinnedMove', 'ScrollThumb', 'StatusOverlay')
    'PanelExpandPerf'        = @('PanelExpandPerf')
    'PanelHold'              = @('PanelHold')
    'PinnedDivider'          = @('Evidence', 'PinnedDivider')
    'QuickLaunch'            = @('QuickLaunch')
    'QuickLaunchContextMenu' = @('QuickLaunch')
    'QuickLaunchEdit'        = @('QuickLaunch')
    'QuickLaunchEditTarget'  = @('QuickLaunch')
    'QuickLaunchEvidence'    = @('Evidence')
    'QuickLaunchPerf'        = @('QuickLaunchPerf')
    'QuickLaunchPicker'      = @('InstalledAppsEnumerator', 'InstalledPicker', 'QuickLaunch')
    'QuickLaunchRename'      = @('QuickLaunch')
    'QuickLaunchSelection'   = @('QuickLaunch')
    'QuickLaunchSurface'     = @('QuickLaunch')
    'QuickLaunchUrl'         = @('QuickLaunch', 'UrlInput')
    'RangeSelection'         = @('RangeSelection')
    'ReviewEditorEscape'     = @('ReviewEditor')
    'ReviewNav'              = @('ReviewNav')
    'ScrollViewport'         = @('ScrollViewport')
    'Search'                 = @('Search')
    'SelectionAndDeletion'   = @('BatchPinButton', 'BatchPinCommand', 'BatchPinExecutionGuard', 'DeletePending', 'DeleteShortcut', 'EmptyListBackgroundClearsSelectionWithoutChangingPanelOrScroll', 'Escape', 'HeaderClear', 'HeaderDeleteFailureRestoresMixedSelectionAndSuccessDeletesOnlySelection', 'HeaderDeleteWithoutSelection', 'HeaderDeleteWithoutSelectionFailureRestoresCategoryAndNewItemStaysUnselected', 'PinButton', 'SelectAllCommand', 'SelectedCountBadge', 'SelectionButtonTogglesPersistentTwoSignalFeedback', 'SelectionSurvivesAutoCollapseButClearsOnUserCategorySwitch', 'Shortcut', 'TrashButton')
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
    foreach ($rule in $script:rules) {
        foreach ($glob in $rule.Globs) {
            if ($file -like $glob) { $areaMatches += @{ Name = $rule.Name; Filter = $rule.Filter }; break }
        }
    }
    return $areaMatches
}

function Get-FullFallback([string]$file) {
    foreach ($rule in $script:fullRules) {
        foreach ($glob in $rule.Globs) {
            if ($file -like $glob) { return $rule.Reason }
        }
    }
    return $null
}

function Get-DerivedArea([string]$file) {
    # 测试文件词根派生：MainWindowInteractionTests.<Area>*.cs → FQN~MainWindowInteractionTests.<前缀> 并集
    # （类名.方法名边界形态，废除全族放宽；过滤器与一致性守卫同源＝$interactionTestPrefixes）。
    if ($file -like 'tests/FloatingTransferStation.Tests/MainWindowInteractionTests.*.cs') {
        $root = ([System.IO.Path]::GetFileName($file) -replace '^MainWindowInteractionTests\.', '') -replace '\.cs$', ''
        $prefixes = $script:interactionTestPrefixes[$root]
        if (-not $prefixes) { return @{ Name = "Tests.$root"; Filter = $null; Derived = "词根表未登记：MainWindowInteractionTests.$root.cs" } }
        $filter = ($prefixes | ForEach-Object { "FullyQualifiedName~MainWindowInteractionTests.$_" }) -join '|'
        return @{ Name = "Tests.$root"; Filter = $filter; Derived = 'App 测试文件词根派生' }
    }
    if ($file -like 'tests/FloatingTransferStation.Tests/*.cs') {
        $stem = ((Split-Path -Leaf $file) -replace '\.cs$', '') -split '\.' | Select-Object -First 1
        return @{ Name = "Tests.$stem"; Filter = "FullyQualifiedName~$stem"; Derived = 'App 测试文件泛型规则（独立类名）' }
    }
    if ($file -like 'tests/FloatingTransferStation.Core.Tests/*') {
        return @{ Name = 'Core.Tests'; Filter = $null; Derived = '强制基线恒跑覆盖' }
    }
    if ($file -like 'src/FloatingTransferStation.Core/Services/*.cs') {
        $stem = ((Split-Path -Leaf $file) -replace '\.cs$', '') -split '\.' | Select-Object -First 1
        return @{ Name = "Core.$stem"; Filter = "FullyQualifiedName~$stem"; Derived = 'Core 一服务一类泛型规则' }
    }
    return $null
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

function Assert-FilterSelectsTests([string]$areaName, [string]$filter, [string[]]$appInventory, [string[]]$coreInventory) {
    # 漂移检查：Core.* 区域走 solution 级（两工程任一命中即可）；其余区域跑 App 工程，必须命中 App。
    # 整组过滤器命中 0 条＝映射腐烂；任一词根在目标工程命中 0 条＝死词根。两者都 fail fast。
    $targets = if ($areaName -like 'Core.*') { $appInventory + $coreInventory } else { $appInventory }
    $tokens = @($filter -split '\|' | ForEach-Object { $_ -replace '^FullyQualifiedName~', '' } | Where-Object { $_ })
    $dead = @()
    $live = $false
    foreach ($token in $tokens) {
        $hit = @($targets | Where-Object { $_ -like "*$token*" }).Count -gt 0
        if ($hit) { $live = $true } else { $dead += $token }
    }
    if (-not $live) { throw "区域 $areaName 的过滤器命中 0 条测试（$filter）——映射已腐烂，请修正映射或登记词根表。" }
    if ($dead.Count) { throw "区域 $areaName 过滤器死词根：$($dead -join ', ')（在目标测试工程中命中 0 条）——请清理映射。" }
}

function Get-AffectedTestPlan([string[]]$changedFiles, [switch]$ForceFull) {
    # 纯决策函数：变更集 → 计划（Mode: Full/Affected/Exempt）。判定序：豁免 → 精确区域（并集）
    # → 全量类（打印理由）→ 泛型/词根派生 → 未知路径全量。空选集两分见 Mode 判定。
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
                    $selected[$match.Name] = @{ Filters = [System.Collections.Generic.List[string]]::new(); Files = [System.Collections.Generic.List[string]]::new() }
                }
                if ($match.Filter -and -not $selected[$match.Name].Filters.Contains($match.Filter)) { $selected[$match.Name].Filters.Add($match.Filter) }
                $selected[$match.Name].Files.Add($file)
            }
            continue
        }
        $fullReason = Get-FullFallback $file
        if ($fullReason) { $fullReasons.Add("$file → $fullReason"); continue }
        $derived = Get-DerivedArea $file
        if ($derived) {
            if (-not $selected.Contains($derived.Name)) {
                $selected[$derived.Name] = @{ Filters = [System.Collections.Generic.List[string]]::new(); Files = [System.Collections.Generic.List[string]]::new() }
            }
            if ($derived.Filter -and -not $selected[$derived.Name].Filters.Contains($derived.Filter)) { $selected[$derived.Name].Filters.Add($derived.Filter) }
            $selected[$derived.Name].Files.Add($file)
            continue
        }
        $fullReasons.Add("$file → 未命中映射（未知路径回退全量）")
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
            Write-Host "区域 $name → filter=$(if ($entry.Filter) { $entry.Filter } else { '（强制基线覆盖，无附加 filter）' })；理由：$($entry.Files -join ', ')"
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
foreach ($rule in $rules) { Assert-FilterSelectsTests $rule.Name $rule.Filter $appInventory $coreInventory }

if ($PSBoundParameters.ContainsKey('ChangeSet')) {
    $changed = @($ChangeSet)
    Write-Host "变更集来自 -ChangeSet 参数（$($changed.Count) 个文件，契约测试/历史回放模式；显式空＝空变更集）。"
} elseif ($Full) {
    # -Full 强制全量：变更集仅用于打印理由，git 失败不阻断全量执行。
    $changed = @()
} else {
    if (-not $Base) {
        $Base = (git -C $repoRoot merge-base origin/main HEAD).Trim()
        if ($LASTEXITCODE -ne 0 -or -not $Base) { throw '无法解析 merge-base origin/main HEAD；请确认远端与当前分支。' }
    }
    $diffFiles = @(git -C $repoRoot diff --no-renames --name-only $Base)
    if ($LASTEXITCODE -ne 0) { throw "git diff $Base 失败（退出码 $LASTEXITCODE）；拒绝把 git 故障当空变更集处理。" }
    $untrackedFiles = @(git -C $repoRoot ls-files --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw "git ls-files 失败（退出码 $LASTEXITCODE）；拒绝把 git 故障当空变更集处理。" }
    $changed = @($diffFiles + $untrackedFiles) | Where-Object { $_ } | Sort-Object -Unique
    Write-Host "基线 $Base；变更集 $($changed.Count) 个文件。"
}

$plan = Get-AffectedTestPlan $changed -ForceFull:$Full

if ($Areas) {
    $requested = @($Areas -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if (-not $requested) { throw "-Areas '$Areas' 解析后为空；请至少给出一个有效区域名。" }
    $known = @($rules | ForEach-Object { $_.Name } | Sort-Object -Unique)
    $unknown = @($requested | Where-Object { $known -notcontains $_ })
    if ($unknown) { throw "未知区域：$($unknown -join ', ')。当前可选：$($known -join ', ')" }
    $selected = [ordered]@{}
    foreach ($rule in $rules) {
        if ($requested -notcontains $rule.Name) { continue }
        if (-not $selected.Contains($rule.Name)) {
            $selected[$rule.Name] = @{ Filters = [System.Collections.Generic.List[string]]::new(); Files = [System.Collections.Generic.List[string]]::new() }
        }
        if ($rule.Filter -and -not $selected[$rule.Name].Filters.Contains($rule.Filter)) { $selected[$rule.Name].Filters.Add($rule.Filter) }
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
