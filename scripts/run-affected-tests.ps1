# 分层测试协议选择器：按 merge-base 变更集选择受影响测试区域。
# 协议出处：CONTRIBUTING.md「开发环境」与「提交 PR 前」章节（分层测试协议，2026-10-18 起）。
# 契约：变更集=git diff <Base>（含已提交/暂存/未暂存）+ untracked；映射数据驱动可评审；
# Core.Tests 整项目恒跑（强制基线）；未命中映射/共享壳回退全量并打印理由；
# 区域选中 0 条测试=失败；除 -Full/回退外不运行 solution 级无 filter 的全量测试。
[CmdletBinding()]
param(
    [string]$Base,
    [string]$Areas,
    [switch]$Full,
    [switch]$Explain,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$DotnetPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $DotnetPath) {
    $local = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
    $DotnetPath = if (Test-Path $local) { $local } else { 'dotnet' }
}
$solution = Join-Path $repoRoot 'FloatingTransferStation.slnx'
$appTests = Join-Path $repoRoot 'tests\FloatingTransferStation.Tests\FloatingTransferStation.Tests.csproj'
$coreTests = Join-Path $repoRoot 'tests\FloatingTransferStation.Core.Tests\FloatingTransferStation.Core.Tests.csproj'

# ---- 区域规则（Glob 相对仓库根、正斜杠；首个命中生效；Name 相同的规则合并 filter 并集）----
# 快捷启动（启动器）：瓦片/URL/选择器/图标/目录/存储，Core 与 App 两侧同属一族。
$rules = @(
    @{ Name = 'Launcher'; Filter = 'FullyQualifiedName~Launcher|FullyQualifiedName~QuickLaunch'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.QuickLaunch*.cs',
        'src/FloatingTransferStation/Views/LauncherUrlInputWindow*',
        'src/FloatingTransferStation/Views/InstalledAppPickerWindow*',
        'src/FloatingTransferStation/Launcher/*',
        'src/FloatingTransferStation.Core/Services/Launcher*.cs',
        'src/FloatingTransferStation.Core/Services/VolatileLauncherStore.cs',
        'src/FloatingTransferStation.Core/Models/LauncherItem.cs',
        'src/FloatingTransferStation.Core/Models/LauncherIconKey.cs',
        'tests/FloatingTransferStation.Tests/MainWindowInteractionTests.QuickLaunch*.cs') },
    # 设置窗口（双栏/各节/几何/插件/AI/标签顺序）
    @{ Name = 'SettingsWindow'; Filter = 'FullyQualifiedName~SettingsWindow'; Globs = @(
        'src/FloatingTransferStation/Views/SettingsWindow*', 'src/FloatingTransferStation/Views/WindowShellClip.cs') },
    # 生命周期：安装器、发行元数据/就绪断言、自启、单实例、数据目录边界
    @{ Name = 'Lifecycle'; Filter = 'FullyQualifiedName~Lifecycle'; Globs = @(
        'installer/*', 'src/FloatingTransferStation/Services/WindowsStartupManager.cs',
        'src/FloatingTransferStation/Services/SingleInstanceGuard.cs', 'src/FloatingTransferStation/Services/AppLifecycleService.cs',
        'src/FloatingTransferStation/Services/WindowsDataDirectorySettings.cs', 'src/FloatingTransferStation.Core/ProductIdentity.cs') },
    # 公开发行材料（README/CONTRIBUTING/CHANGELOG/LICENSE/ROADMAP/version.txt）：
    # LifecycleTests.ReleaseMetadata 以内容断言锁定这些文件的用户契约与协议记载。
    @{ Name = 'PublicReleaseMaterials'; Filter = 'FullyQualifiedName~ReleaseMetadata'; Globs = @(
        'README.md', 'CONTRIBUTING.md', 'CHANGELOG.md', 'LICENSE', 'ROADMAP.md', 'version.txt') },
    # 剪贴板与外部导入：采集/去重/容量/载荷/图片读取/拖放载荷/文本整理插件管线
    @{ Name = 'Clipboard'; Filter = 'FullyQualifiedName~Clipboard|FullyQualifiedName~ExternalDrop|FullyQualifiedName~WindowsDataImageReader'; Globs = @(
        'src/FloatingTransferStation/Services/Clipboard*.cs', 'src/FloatingTransferStation/Services/IClipboardReader.cs',
        'src/FloatingTransferStation/Services/WpfClipboardReader.cs', 'src/FloatingTransferStation/Services/WindowsDataImageReader.cs',
        'src/FloatingTransferStation/Services/ExternalDrop*.cs', 'src/FloatingTransferStation/Models/Clipboard*.cs',
        'src/FloatingTransferStation/Models/ExternalDropPayload.cs', 'src/FloatingTransferStation.Core/Services/CapturedContentFingerprint.cs',
        'src/FloatingTransferStation.Core/Services/StoredImage.cs',
        'src/FloatingTransferStation.Core/Services/TextCleanerPipeline.cs') },
    # 拖放管线（含拖放载荷服务）
    @{ Name = 'Drag'; Filter = 'FullyQualifiedName~Drag|FullyQualifiedName~Drop'; Globs = @(
        'src/FloatingTransferStation/Services/DragPayloadService.cs') },
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
    @{ Name = 'ClipboardDelivery'; Filter = 'FullyQualifiedName~RightClickCopy|FullyQualifiedName~ClipboardDelivery'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.ClipboardDelivery.cs') },
    @{ Name = 'DailyReview'; Filter = 'FullyQualifiedName~DailyReview|FullyQualifiedName~Review'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.DailyReview.cs') },
    @{ Name = 'DataRelocation'; Filter = 'FullyQualifiedName~Relocation|FullyQualifiedName~DataDirectory'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.DataRelocation.cs') },
    @{ Name = 'EdgeHide'; Filter = 'FullyQualifiedName~EdgeHide'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.EdgeHide.cs') },
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
    # Core 模型（一模型→消费面测试族）
    @{ Name = 'Core.AppPreferences'; Filter = 'FullyQualifiedName~Preferences'; Globs = @(
        'src/FloatingTransferStation.Core/Models/AppPreferences.cs') },
    @{ Name = 'Core.BoardCategory'; Filter = 'FullyQualifiedName~BoardCategory|FullyQualifiedName~Category'; Globs = @(
        'src/FloatingTransferStation.Core/Models/BoardCategory.cs') },
    @{ Name = 'Core.BoardItem'; Filter = 'FullyQualifiedName~Board'; Globs = @(
        'src/FloatingTransferStation.Core/Models/BoardItem.cs', 'src/FloatingTransferStation.Core/Models/BoardItemKind.cs',
        'src/FloatingTransferStation.Core/Models/BoardSnapshot.cs') },
    @{ Name = 'Core.DailyReviewDocument'; Filter = 'FullyQualifiedName~DailyReview'; Globs = @(
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
    @{ Name = 'Core.DailyReviewMerge'; Filter = 'FullyQualifiedName~DailyReview'; Globs = @(
        'src/FloatingTransferStation.Core/Services/DailyReviewMerge.cs') },
    @{ Name = 'Core.DataDirectory'; Filter = 'FullyQualifiedName~Relocation|FullyQualifiedName~DataDirectory'; Globs = @(
        'src/FloatingTransferStation.Core/Services/DataDirectoryCleanupProcessor.cs',
        'src/FloatingTransferStation.Core/Services/DataDirectoryRelocation.cs',
        'src/FloatingTransferStation.Core/Services/DataDirectorySettings.cs',
        'src/FloatingTransferStation.Core/Services/RelocationCleanupMarker.cs') },
    @{ Name = 'Core.ImageFileSupport'; Filter = 'FullyQualifiedName~Image'; Globs = @(
        'src/FloatingTransferStation.Core/Services/ImageFileSupport.cs') },
    @{ Name = 'Core.ManagedImagePath'; Filter = 'FullyQualifiedName~ImageNormalizer'; Globs = @(
        'src/FloatingTransferStation.Core/Services/ManagedImagePath.cs') }
)

# ---- 强制回退全量的共享壳/宽影响面文件（理由打印）----
$fullRules = @(
    @{ Reason = '共享窗口壳（主窗 XAML/代码后置/App 入口）'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.xaml', 'src/FloatingTransferStation/Views/MainWindow.xaml.cs',
        'src/FloatingTransferStation/App.xaml', 'src/FloatingTransferStation/App.xaml.cs') },
    @{ Reason = '共享样式/主题资源字典'; Globs = @(
        'src/FloatingTransferStation/Resources/*') },
    @{ Reason = 'Core 契约接口/视图模型/设计 token，影响面广'; Globs = @(
        'src/FloatingTransferStation.Core/Services/I*.cs', 'src/FloatingTransferStation.Core/ViewModels/*',
        'src/FloatingTransferStation.Core/Design/*') },
    @{ Reason = '原生/壳层互操作与设置宿主接口，影响面广'; Globs = @(
        'src/FloatingTransferStation/Services/MonitorBounds.cs', 'src/FloatingTransferStation/Services/DwmWindowEffects.cs',
        'src/FloatingTransferStation/Services/ShellInterop.cs', 'src/FloatingTransferStation/Services/NativeMethods.cs',
        'src/FloatingTransferStation/Services/ISettingsHost.cs', 'src/FloatingTransferStation/Services/IImageNormalizer.cs') },
    @{ Reason = '构建/依赖/解决方案配置'; Globs = @(
        'Directory.Build.props', 'Directory.Packages.props', 'FloatingTransferStation.slnx', 'global.json', 'nuget.config') },
    @{ Reason = '窗口/卡片视觉与动画基础设施——测试方法名与源文件名不同族，保守回退全量'; Globs = @(
        'src/FloatingTransferStation/Views/MainWindow.VisualTransitions.cs', 'src/FloatingTransferStation/Views/MainWindow.CardEntrance.cs',
        'src/FloatingTransferStation/Views/FadeAnimation.cs', 'src/FloatingTransferStation/Views/LiftAnimation.cs') },
    @{ Reason = '共享测试设施'; Globs = @(
        'tests/FloatingTransferStation.Tests/TestDirectory.cs', 'tests/FloatingTransferStation.Tests/GlobalUsings.cs',
        'tests/FloatingTransferStation.Tests/BoardCreatedAtTestHelpers.cs', 'tests/FloatingTransferStation.Tests/FailingFirstSaveBoardStore.cs',
        'tests/FloatingTransferStation.Tests/GatedSettingsBoardStore.cs') }
)

function Get-MatchedRule([string]$file) {
    foreach ($rule in $script:rules) {
        foreach ($glob in $rule.Globs) {
            if ($file -like $glob) { return @{ Filter = $rule.Filter; Name = $rule.Name; Derived = $null } }
        }
    }
    foreach ($rule in $script:fullRules) {
        foreach ($glob in $rule.Globs) {
            if ($file -like $glob) { return @{ Full = $rule.Reason } }
        }
    }
    # 泛型规则：Core 服务一服务一类（FQN~stem，点号 partial 取首段；接口文件已由 fullRules 拦截）；
    # App 测试文件——partial 族共享同一类名、FQN 不含文件区域名，统一放宽到
    # FQN~MainWindowInteractionTests 全族（恒非零），独立类取 FQN~<类名>（类名必在 FQN 中，恒非零）。
    if ($file -like 'src/FloatingTransferStation.Core/Services/*.cs') {
        $stem = ((Split-Path -Leaf $file) -replace '\.cs$', '') -split '\.' | Select-Object -First 1
        return @{ Name = "Core.$stem"; Filter = "FullyQualifiedName~$stem"; Derived = 'Core 一服务一类泛型规则' }
    }
    if ($file -like 'tests/FloatingTransferStation.Tests/MainWindowInteractionTests.*.cs') {
        return @{ Name = 'Tests.MainWindowInteractionTests'; Filter = 'FullyQualifiedName~MainWindowInteractionTests'; Derived = 'App 测试文件泛型规则（partial 全族）' }
    }
    if ($file -like 'tests/FloatingTransferStation.Tests/*.cs') {
        $stem = ((Split-Path -Leaf $file) -replace '\.cs$', '') -split '\.' | Select-Object -First 1
        return @{ Name = "Tests.$stem"; Filter = "FullyQualifiedName~$stem"; Derived = 'App 测试文件泛型规则' }
    }
    if ($file -like 'tests/FloatingTransferStation.Core.Tests/*') {
        return @{ Name = 'Core.Tests'; Filter = $null; Derived = '强制基线恒跑覆盖' }
    }
    return @{ Full = '未命中映射' }
}

if (-not $Base) {
    $Base = (git -C $repoRoot merge-base origin/main HEAD).Trim()
    if (-not $Base) { throw '无法解析 merge-base origin/main HEAD；请确认远端与当前分支。' }
}
$changed = @(@(git -C $repoRoot diff --no-renames --name-only $Base) + @(git -C $repoRoot ls-files --others --exclude-standard)) |
    Where-Object { $_ } | ForEach-Object { $_ -replace '\\', '/' } | Sort-Object -Unique

Write-Host "基线 $Base；变更集 $($changed.Count) 个文件。"
$selected = [ordered]@{}
$fullReasons = [System.Collections.Generic.List[string]]::new()
foreach ($file in $changed) {
    $match = Get-MatchedRule $file
    if ($match.Full) { $fullReasons.Add("$file → $($match.Full)"); continue }
    $name = $match.Name
    if (-not $selected.Contains($name)) {
        $selected[$name] = @{ Filters = [System.Collections.Generic.List[string]]::new(); Files = [System.Collections.Generic.List[string]]::new() }
    }
    if ($match.Filter -and -not $selected[$name].Filters.Contains($match.Filter)) { $selected[$name].Filters.Add($match.Filter) }
    $selected[$name].Files.Add($file)
}

if ($Areas) {
    $requested = @($Areas -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
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
}

if ($Full) {
    Write-Host '计划：-Full 强制全量。'
    $planFull = $true
} elseif ($fullReasons) {
    if ($Areas) { Write-Host '注意：变更集存在未命中映射的文件，回退全量优先于 -Areas 显式选区（未知路径保护）。' }
    Write-Host '计划：回退全量。理由：'
    $fullReasons | ForEach-Object { Write-Host "  - $_" }
    $planFull = $true
} elseif (-not $selected.Count) {
    Write-Host '计划：空选集（变更集为空或未命中任何区域）→ 回退全量。'
    $planFull = $true
} else {
    foreach ($name in $selected.Keys) {
        $entry = $selected[$name]
        $entry.Filter = if ($entry.Filters.Count) { $entry.Filters -join '|' } else { $null }
        Write-Host "区域 $name → filter=$(if ($entry.Filter) { $entry.Filter } else { '（强制基线覆盖，无附加 filter）' })；理由：$($entry.Files -join ', ')"
    }
    $planFull = $false
}

if ($Explain) { Write-Host '-Explain：仅打印计划，不执行。'; exit 0 }

function Invoke-TestRun([string]$Title, [string]$Project, [string]$Filter) {
    $prefix = 'affected-' + (($Title -replace '[^A-Za-z0-9._-]', '_'))
    $results = Join-Path $repoRoot 'TestResults'
    # LogFilePrefix 使 solution 级运行的两个测试工程各写独立 TRX；运行前清理同名前缀旧文件防累积。
    Get-ChildItem $results -Filter "$prefix*.trx" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    $testArgs = @('test', $Project, '-c', $Configuration, '--no-build', '--results-directory', $results, '--logger', "trx;LogFilePrefix=$prefix")
    if ($Filter) { $testArgs += @('--filter', $Filter) }
    Write-Host "运行 $Title ..."
    & $DotnetPath @testArgs
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
}

if ($planFull) {
    & $DotnetPath test $solution -c $Configuration --results-directory (Join-Path $repoRoot 'TestResults') --logger 'trx;LogFilePrefix=affected-full'
    exit $LASTEXITCODE
}

& $DotnetPath build $coreTests -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $DotnetPath build $appTests -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Invoke-TestRun 'CoreBaseline' $coreTests $null
foreach ($name in $selected.Keys) {
    $filter = $selected[$name].Filter
    if (-not $filter) { continue }
    if ($name -like 'Core.*') {
        # Core 侧区域走 solution 级「带 filter」运行（覆盖 Core.Tests 与 App 两个测试工程）；
        # 契约只禁止 solution 级「无 filter」全量，此处带 filter 合规。
        Invoke-TestRun $name $solution $filter
    } else {
        Invoke-TestRun $name $appTests $filter
    }
}
Write-Host '受影响范围测试全部通过。'
exit 0
