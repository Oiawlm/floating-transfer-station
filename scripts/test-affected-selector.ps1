# 受影响测试选择器契约测试（判定序 v2）：内嵌场景矩阵，断言 run-affected-tests.ps1 的
# 「变更集→计划」决策语义（四层判定序/空选集两分/约定派生/词根派生/豁免边界/反向守卫/
# 噪声防线/-Scope 双模式）与一致性守卫行为。0 退出码＝全部通过；场景计数低于下限＝失败
# （Kind=Script 区域的恒非零语义由本断言承担）。历史回放条目（R01–R11）内联于本文件，
# 出处＝docs/research/DATA-AFFECTED-REPLAY-20261027.ps1（gitignored 本地档案，W1 审计
# 产物；内联是干净 clone 上可运行的必要条件，-ReplayDataPath 可对档案做漂移核对）。
# 与 test-bootstrap-*.ps1 同族的独立契约测试；点源复用选择器的数据与函数（单一事实源，无副本）。
# 协议出处：docs/design-contracts.md 契约「受影响测试选区协议」（#32）。
[CmdletBinding()]
param(
    [string]$DotnetPath,
    [string]$ReplayDataPath
)

$ErrorActionPreference = 'Stop'
$selectorScript = Join-Path $PSScriptRoot 'run-affected-tests.ps1'

# 点源加载：只取数据表与函数（$FTS_SELECTOR_DEFINE_ONLY 哨兵阻断主执行）。
$FTS_SELECTOR_DEFINE_ONLY = $true
. $selectorScript
$FTS_SELECTOR_DEFINE_ONLY = $false

$failures = [System.Collections.Generic.List[string]]::new()
$script:passedCount = 0
function Assert-Scenario([string]$name, [scriptblock]$body) {
    try {
        & $body
        $script:passedCount++
        Write-Host "PASS $name"
    } catch {
        $failures.Add("$name：$($_.Exception.Message)")
        Write-Host "FAIL $name：$($_.Exception.Message)"
    }
}
function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}
function Assert-Throws([scriptblock]$body, [string]$expectedSubstring, [string]$message) {
    try { & $body | Out-Null } catch {
        if ($_.Exception.Message -like "*$expectedSubstring*") { return }
        throw "$message（异常原因不符：$($_.Exception.Message)）"
    }
    throw "$message（未抛出任何异常）"
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$appTestsDir = Join-Path $repoRoot 'tests\FloatingTransferStation.Tests'
$coreTestsDir = Join-Path $repoRoot 'tests\FloatingTransferStation.Core.Tests'
$app = @(Get-TestInventory $appTestsDir)
$core = @(Get-TestInventory $coreTestsDir)

# ---- 历史回放基线（内联；出处 DATA-AFFECTED-REPLAY-20261027.ps1，条目注释锚 Evidence）----
# ExpectedMode＝新架构选择器口径预期；与 NewExpected 不同时 Deviation 给书面理由（PR 描述
# 与执行报告逐条收录）。可消灭类条目仍判 Full 的（R04/R07/R10/R11）均有 F2 队列/全量表
# 保留理由，无无理由例外。
$replayCases = @(
    @{ Id = 'R01'; Files = @('CHANGELOG.md', 'CONTRIBUTING.md', 'docs/design-contracts.md', 'scripts/run-affected-tests.ps1', 'tests/FloatingTransferStation.Tests/LifecycleTests.ReleaseMetadata.cs')
       NewExpected = 'Affected'; ExpectedMode = 'Affected'; Deviation = $null
       Evidence = 'observations.md:48 + improvement-loop.md:86 + 5822adb(PR#182)' },
    @{ Id = 'R02'; Files = @('README.md', 'docs/design-contracts.md', 'docs/images/settings-ai-prompt-dark.png', 'docs/images/settings-ai-prompt-light.png', 'docs/usage.md', 'tests/FloatingTransferStation.Tests/SettingsWindowAiTests.cs')
       NewExpected = 'Affected'; ExpectedMode = 'Affected'; Deviation = $null
       Evidence = 'observations.md:54 + design-contracts.md:328 + cf0dd31' },
    @{ Id = 'R03'; Files = @('src/FloatingTransferStation/Views/SettingsWindow.xaml', 'src/FloatingTransferStation/Views/SettingsWindow.xaml.cs', 'tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardAi.cs', 'tests/FloatingTransferStation.Tests/SettingsWindowAiTests.cs')
       NewExpected = 'Affected'; ExpectedMode = 'Affected'; Deviation = $null
       Evidence = 'observations.md:54 + 2026-10-19-affected-test-scope-report.md:5 + b03a748' },
    @{ Id = 'R04'; Files = @('CHANGELOG.md', 'docs/design-contracts.md', 'docs/images/settings-window-dark.png', 'docs/images/settings-window.png', 'scripts/run-affected-tests.ps1', 'src/FloatingTransferStation/Views/MainWindow.BoardEmptyState.cs', 'src/FloatingTransferStation/Views/MainWindow.DailyReview.cs', 'src/FloatingTransferStation/Views/MainWindow.Search.cs', 'src/FloatingTransferStation/Views/MainWindow.xaml', 'src/FloatingTransferStation/Views/MainWindow.xaml.cs', 'src/FloatingTransferStation/Views/SettingsWindow.xaml', 'tests/FloatingTransferStation.Tests/MainWindowInteractionTests.BoardEmptyState.cs', 'tests/FloatingTransferStation.Tests/SettingsWindowInteractionTests.cs')
       NewExpected = 'Affected'; ExpectedMode = 'Full'
       Deviation = '主窗壳二件（MainWindow.xaml/.xaml.cs）按 spec §三 #0 保留 C5（F2 缩圈队列）；C1/C2 面（BoardEmptyState partial、scripts 自噬）已消灭'
       Evidence = '2026-10-19-empty-state-settings-width-report.md:45 + f895fd0(PR#201) + 133d676' },
    @{ Id = 'R05'; Files = @('.github/PULL_REQUEST_TEMPLATE.md', 'CHANGELOG.md', 'CONTRIBUTING.md', 'README.md', 'docs/design-contracts.md', 'scripts/run-affected-tests.ps1', 'scripts/test-affected-selector.ps1')
       NewExpected = 'Affected'; ExpectedMode = 'Affected'; Deviation = $null
       Evidence = '2026-10-19-affected-test-scope-report.md:7 + a4fafbe(PR#199)' },
    @{ Id = 'R06'; Files = @('CHANGELOG.md', 'ROADMAP.md', 'docs/architecture.md', 'docs/design-contracts.md', 'tests/FloatingTransferStation.Tests/GlobalHotkeyInteractionTests.cs', 'tests/FloatingTransferStation.Tests/ImageInputLimitsTests.cs', 'tests/FloatingTransferStation.Tests/MainWindowInteractionTests.Search.cs', '.wbx/jobs/20261019-sample/task.txt')
       NewExpected = 'Affected'; ExpectedMode = 'Affected'; Deviation = $null
       Evidence = '2026-10-19-contract-consolidation-report.md:135,139,144 + 3c17f8e(PR#205)' },
    @{ Id = 'R07'; Files = @('CHANGELOG.md', 'README.md', 'docs/design-contracts.md', 'docs/usage.md', 'src/FloatingTransferStation.Core/Models/AppPreferences.cs', 'src/FloatingTransferStation.Core/ViewModels/MainWindowViewModel.cs', 'src/FloatingTransferStation/Resources/MainWindowStyles.xaml', 'src/FloatingTransferStation/Views/MainWindow.Settings.cs', 'src/FloatingTransferStation/Views/MainWindow.xaml.cs', 'src/FloatingTransferStation/Views/SettingsWindow.xaml', 'src/FloatingTransferStation/Views/SettingsWindow.xaml.cs', 'tests/FloatingTransferStation.Core.Tests/AppPreferencesTests.cs', 'tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardNote.cs', 'tests/FloatingTransferStation.Tests/MainWindowInteractionTests.DailyReview.cs', 'tests/FloatingTransferStation.Tests/SettingsWindowInteractionTests.cs', 'tests/FloatingTransferStation.Tests/SettingsWindowNavPaneTests.cs')
       NewExpected = 'Affected'; ExpectedMode = 'Full'
       Deviation = 'Resources/ViewModels/主窗壳三全量类保留 C5（F2 取证队列；W1 Note 明言「否则维持 Full」）'
       Evidence = '2026-10-22-short-card-note-report.md:39,64 + 182818a(PR#214)' },
    @{ Id = 'R08'; Files = @('CHANGELOG.md', 'README.md', 'version.txt')
       NewExpected = 'Full'; ExpectedMode = 'Affected'
       Deviation = 'NewExpected=Full 属 -ForRelease 发行门职责（非选择器退化）；选择器口径＝PublicReleaseMaterials（W1 Note 原文预判）'
       Evidence = '2026-10-22-integrate-release-report.md:35,89 + 8df6217(PR#215)' },
    @{ Id = 'R09'; Files = @('CHANGELOG.md', 'README.md', 'ROADMAP.md', 'docs/design-contracts.md', 'docs/images/card-gesture-swap-evidence/after-shift-click.png', 'docs/images/card-gesture-swap-evidence/before-shift-click.png', 'docs/images/card-gesture-swap-evidence/left-click-copy-status.png', 'docs/images/card-gesture-swap-evidence/plugin-actions-context-menu.png', 'docs/usage.md', 'scripts/run-affected-tests.ps1', 'src/FloatingTransferStation.Core/Models/AppPreferences.cs', 'src/FloatingTransferStation/Views/CardGestureZones.cs', 'src/FloatingTransferStation/Views/MainWindow.CardGestures.cs', 'src/FloatingTransferStation/Views/MainWindow.ClipboardDelivery.cs', 'src/FloatingTransferStation/Views/SettingsWindow.xaml', 'src/FloatingTransferStation/Views/SettingsWindow.xaml.cs', 'tests/FloatingTransferStation.Core.Tests/AppPreferencesTests.cs', 'tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardDeleteButton.cs', 'tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardGestures.cs', 'tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardHalves.cs', 'tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardNote.cs', 'tests/FloatingTransferStation.Tests/MainWindowInteractionTests.ClipboardDelivery.cs', 'tests/FloatingTransferStation.Tests/MainWindowInteractionTests.RangeSelection.cs', 'tests/FloatingTransferStation.Tests/PluginActionMenuTests.cs', 'tests/FloatingTransferStation.Tests/SettingsWindowInteractionTests.cs')
       NewExpected = 'Affected'; ExpectedMode = 'Affected'; Deviation = $null
       Evidence = '2026-10-23-launcher-batch-integrate-release-report.md:11,15,40 + 0942735(PR#219)' },
    @{ Id = 'R10'; Files = @('CHANGELOG.md', 'README.md', 'docs/design-contracts.md', 'docs/readme-style-guide.md', 'docs/usage.md', 'installer/悬浮中转站.iss', 'scripts/test-installer-cleanup.ps1', 'src/FloatingTransferStation.Core/Services/DataDirectoryRelocation.cs', 'src/FloatingTransferStation.Core/Services/DataDirectorySettings.cs', 'src/FloatingTransferStation/Services/DataDirectoryChangeService.cs', 'src/FloatingTransferStation/Views/SettingsWindow.xaml', 'src/FloatingTransferStation/Views/SettingsWindow.xaml.cs', 'tests/FloatingTransferStation.Core.Tests/DataDirectoryRelocationTests.cs', 'tests/FloatingTransferStation.Core.Tests/RelocationCleanupTests.cs', 'tests/FloatingTransferStation.Tests/DataDirectoryChangeServiceTests.cs', 'tests/FloatingTransferStation.Tests/LifecycleTests.Installer.cs', 'tests/FloatingTransferStation.Tests/MainWindowDataRelocationTests.cs')
       NewExpected = 'Affected'; ExpectedMode = 'Full'
       Deviation = 'scripts/test-installer-cleanup.ps1 属构建/安装链全量表保留（spec §四）；C1 面（DataDirectoryChangeService 未登记）已由 R-AppStem 消灭'
       Evidence = '2026-10-24-batch10/traces-159-train/pr-body-data-directory-shape.md:55 + 03f0bbc(PR#232)' },
    @{ Id = 'R11'; Files = @('CHANGELOG.md', 'README.md', 'docs/design-contracts.md', 'docs/design.md', 'docs/usage.md', 'scripts/run-affected-tests.ps1', 'src/FloatingTransferStation.Core/Design/DesignTokens.cs', 'src/FloatingTransferStation.Core/Models/AppPreferences.cs', 'src/FloatingTransferStation/Resources/MainWindowStyles.xaml', 'src/FloatingTransferStation/Services/InterfaceFontManager.cs', 'src/FloatingTransferStation/Views/LauncherEditTargetWindow.xaml', 'src/FloatingTransferStation/Views/LauncherEditTargetWindow.xaml.cs', 'src/FloatingTransferStation/Views/LauncherUrlInputWindow.xaml', 'src/FloatingTransferStation/Views/LauncherUrlInputWindow.xaml.cs', 'src/FloatingTransferStation/Views/MainWindow.Settings.cs', 'src/FloatingTransferStation/Views/MainWindow.xaml', 'src/FloatingTransferStation/Views/MainWindow.xaml.cs', 'src/FloatingTransferStation/Views/MultiItemPickerWindow.xaml', 'src/FloatingTransferStation/Views/MultiItemPickerWindow.xaml.cs', 'src/FloatingTransferStation/Views/PluginActionTrialWindow.xaml', 'src/FloatingTransferStation/Views/PluginActionTrialWindow.xaml.cs', 'src/FloatingTransferStation/Views/SettingsWindow.xaml', 'src/FloatingTransferStation/Views/SettingsWindow.xaml.cs', 'tests/FloatingTransferStation.Core.Tests/AppPreferencesTests.cs', 'tests/FloatingTransferStation.Tests/InterfaceFontManagerTests.cs', 'tests/FloatingTransferStation.Tests/MainWindowInteractionTests.DesignTokens.cs', 'tests/FloatingTransferStation.Tests/MainWindowInteractionTests.InterfaceFont.cs', 'tests/FloatingTransferStation.Tests/SettingsWindowInteractionTests.cs')
       NewExpected = 'Affected'; ExpectedMode = 'Full'
       Deviation = 'DesignTokens/Resources/主窗壳保留 C5（F2 队列）；C1 面（InterfaceFontManager、PluginActionTrialWindow 未登记）已消灭'
       Evidence = 'loop-2026-10.md:98-103 + 518ba76(PR#235) + b7735e3' }
)

# ---- 场景矩阵（旧 18 场景语义保留＋判定序 v2 扩容；验收表的机器化）----

Assert-Scenario '仅改 SettingsWindow.xaml.cs → App.SettingsWindow（R-AppStem，filter 与旧例外表逐字等价）' {
    $plan = Get-AffectedTestPlan @('src/FloatingTransferStation/Views/SettingsWindow.xaml.cs') $app $core
    Assert-True ($plan.Mode -eq 'Affected') "模式应为 Affected，实际 $($plan.Mode)"
    Assert-True ($plan.Selected.Contains('App.SettingsWindow')) '应派生 App.SettingsWindow 区域（glob 收敛后交 R-AppStem）'
    Assert-True ($plan.Selected.Count -eq 1) "应恰 1 个区域，实际 $($plan.Selected.Count)"
    $filter = $plan.Selected['App.SettingsWindow'].Filters -join '|'
    Assert-True ($filter -eq 'FullyQualifiedName~SettingsWindow') "filter 应与旧条目逐字相同，实际 $filter"
}

Assert-Scenario '设置改动＋design-contracts.md → 文档不扩区' {
    $plan = Get-AffectedTestPlan @('src/FloatingTransferStation/Views/SettingsWindow.xaml.cs', 'docs/design-contracts.md') $app $core
    Assert-True ($plan.Mode -eq 'Affected') "模式应为 Affected，实际 $($plan.Mode)"
    Assert-True ($plan.Selected.Count -eq 1) 'docs 文档不应新增区域'
    Assert-True ($plan.ExemptFiles -contains 'docs/design-contracts.md') 'design-contracts.md 应属豁免类'
}

Assert-Scenario '纯文档（docs/**.md＋png）→ 豁免跳过' {
    $plan = Get-AffectedTestPlan @('docs/usage.md', 'docs/images/shot.png') $app $core
    Assert-True ($plan.Mode -eq 'Exempt') "模式应为 Exempt，实际 $($plan.Mode)"
    Assert-True ($plan.ExemptFiles.Count -eq 2) "应豁免 2 个文件，实际 $($plan.ExemptFiles.Count)"
}

Assert-Scenario '仅改 MainWindowInteractionTests.CardAi.cs → 词根派生区域（非全族）' {
    $plan = Get-AffectedTestPlan @('tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardAi.cs') $app $core
    Assert-True ($plan.Mode -eq 'Affected') "模式应为 Affected，实际 $($plan.Mode)"
    $entry = $plan.Selected['Tests.CardAi']
    Assert-True ($null -ne $entry) '应派生 Tests.CardAi 区域'
    $filter = $entry.Filters -join '|'
    Assert-True ($filter -eq 'FullyQualifiedName~MainWindowInteractionTests.CardAi') "过滤器应为类边界词根形态，实际 $filter"
    Assert-True ($filter -ne 'FullyQualifiedName~MainWindowInteractionTests') '不得回退全族放宽'
}

Assert-Scenario 'QuickLaunchUrl 测试文件 → 词根表多前缀并集' {
    $plan = Get-AffectedTestPlan @('tests/FloatingTransferStation.Tests/MainWindowInteractionTests.QuickLaunchUrl.cs') $app $core
    $filter = $plan.Selected['Tests.QuickLaunchUrl'].Filters -join '|'
    Assert-True ($filter -like '*MainWindowInteractionTests.QuickLaunch*') "应含 QuickLaunch 词根，实际 $filter"
    Assert-True ($filter -like '*MainWindowInteractionTests.UrlInput*') "应含 UrlInput 词根，实际 $filter"
}

Assert-Scenario '改 ISettingsHost.cs → 全量＋[affected-full] 标记行＋category' {
    $plan = Get-AffectedTestPlan @('src/FloatingTransferStation/Services/ISettingsHost.cs') $app $core
    Assert-True ($plan.Mode -eq 'Full') "模式应为 Full，实际 $($plan.Mode)"
    Assert-True ($plan.FullReasons[0].StartsWith('[affected-full] src/FloatingTransferStation/Services/ISettingsHost.cs → ')) "应为 [affected-full] 标记行格式，实际 $($plan.FullReasons[0])"
    Assert-True ($plan.FullReasons[0] -like '*ISettingsHost.cs*跨区域*') "理由应含取证说明，实际 $($plan.FullReasons[0])"
    Assert-True ($plan.FullReasons[0].Contains('[category=C5]')) '应带 category=C5'
}

Assert-Scenario '改 App.xaml.cs → 全量（共享窗口壳）' {
    $plan = Get-AffectedTestPlan @('src/FloatingTransferStation/App.xaml.cs') $app $core
    Assert-True ($plan.Mode -eq 'Full') "模式应为 Full，实际 $($plan.Mode)"
    Assert-True ($plan.FullReasons[0] -like '*共享窗口壳*') '理由应为共享窗口壳'
}

Assert-Scenario '新增未知 .cs 路径 → 全量（未命中映射＋消除指引）' {
    $plan = Get-AffectedTestPlan @('src/FloatingTransferStation/Views/BrandNewWindow.cs') $app $core
    Assert-True ($plan.Mode -eq 'Full') "模式应为 Full，实际 $($plan.Mode)"
    Assert-True ($plan.FullReasons[0] -like '*未命中映射*') '理由应为未命中映射'
    Assert-True ($plan.FullReasons[0] -like '*消除：补 BrandNewWindow*') 'L3 应带消除指引（C1）'
    Assert-True ($plan.FullReasons[0].Contains('[category=C1]')) '未知源码路径 category=C1'
}

Assert-Scenario '空变更集 → 豁免跳过（git 干净）' {
    $plan = Get-AffectedTestPlan @() $app $core
    Assert-True ($plan.Mode -eq 'Exempt') "模式应为 Exempt，实际 $($plan.Mode)"
    Assert-True ($plan.ExemptFiles.Count -eq 0) '空变更集不应登记豁免文件'
}

Assert-Scenario '豁免边界：白名单后缀之外不豁免；根目录断言文档不豁免' {
    Assert-True (Test-ExemptDocument 'docs/guide.md') 'docs/*.md 应豁免'
    Assert-True (Test-ExemptDocument 'docs/images/a.png') 'docs 图片应豁免'
    Assert-True (-not (Test-ExemptDocument 'docs/tool.exe')) '未知后缀不得豁免'
    Assert-True (-not (Test-ExemptDocument 'README.md')) 'README 有契约断言，不豁免'
    Assert-True (-not (Test-ExemptDocument 'CONTRIBUTING.md')) 'CONTRIBUTING 有契约断言，不豁免'
    Assert-True (Test-ExemptDocument 'NOTES.md') '根目录无断言覆盖的 .md 应豁免'
    $plan = Get-AffectedTestPlan @('README.md') $app $core
    Assert-True ($plan.Mode -eq 'Affected' -and $plan.Selected.Contains('PublicReleaseMaterials')) 'README 应命中 PublicReleaseMaterials 区域'
    $plan2 = Get-AffectedTestPlan @('.github/PULL_REQUEST_TEMPLATE.md') $app $core
    Assert-True ($plan2.Mode -eq 'Affected' -and $plan2.Selected.Contains('PublicReleaseMaterials')) 'PR 模板应命中 PublicReleaseMaterials（保守归类，不再未知全量）'
}

Assert-Scenario '过滤器恒非零漂移检查：死词根 fail fast' {
    Assert-Throws { Assert-FilterSelectsTests 'X' 'FullyQualifiedName~NoSuchMethodAnywhere12345' @('A.B_C'), @('D.E_F') } '命中 0 条' '死过滤器应抛错'
    Assert-Throws { Assert-FilterSelectsTests 'X' 'FullyQualifiedName~Live1|FullyQualifiedName~Dead1' @('A.Live1_M'), @('D.E_F') } '死词根' '部分死词根应抛错'
    Assert-FilterSelectsTests 'X' 'FullyQualifiedName~Live1' @('A.Live1_M'), @() | Out-Null
}

function Invoke-SelectorCli([string]$argumentList) {
    # 子进程调用并整段捕获（含 stderr）；PS5.1 下 2>&1 遇 EAP=Stop 会把子进程 stderr 当异常抛，先放宽。
    $previousEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        return (& powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& '$selectorScript' $argumentList" 2>&1 |
            ForEach-Object { "$_" }) -join "`n"
    } finally {
        $ErrorActionPreference = $previousEap
    }
}

Assert-Scenario '-Areas 显式选区不被豁免类变更集吞掉（评审 #1）' {
    # 纯文档变更集＋显式 -Areas → Affected（显式选区即执行意图）；经端到端 -Explain 验证打印。
    $output = Invoke-SelectorCli "-Explain -ChangeSet @('docs/x.md') -Areas SettingsWindow"
    Assert-True ($output -match '计划模式：Affected') "应打印 Affected 计划，实际：$output"
    Assert-True ($output -match '区域 SettingsWindow') '应列出 SettingsWindow 区域'
    # -Areas 解析为空（逗号/空白）→ 报错而非空跑
    $output2 = Invoke-SelectorCli "-Explain -Areas ', ,'"
    Assert-True ($output2 -match '解析后为空') "空 -Areas 应报错，实际：$output2"
}

Assert-Scenario '-ChangeSet 显式空数组＝空变更集（不回退 git 推导，评审 #5）' {
    $output = Invoke-SelectorCli '-Explain -ChangeSet @()'
    Assert-True ($output -match '显式空＝空变更集') "应注明显式空变更集，实际：$output"
    Assert-True ($output -match '计划模式：Exempt') '显式空变更集应为 Exempt'
}

Assert-Scenario '方法解析器变体：同行签名/[TestMethod()]/组合特性均识别（评审 #2）' {
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("selector-parser-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempDir | Out-Null
    try {
        $probe = Join-Path $tempDir 'ParserProbe.cs'
        @'
    [TestMethod] public void SameLineProbe_X() { }
    [TestMethod()]
    public void ParenProbe_X()
    {
    }
    [STATestMethod, Timeout(5000)]
    public void ComboProbe_X()
    {
    }
    [TestMethod]
    [Ignore]
    public void IgnoredProbe_X()
    {
    }
'@ | Set-Content -Path $probe -Encoding UTF8
        $names = @(Get-TestMethodNames $probe)
        Assert-True ($names -contains 'SameLineProbe_X') "同行签名应识别，实际：$($names -join ',')"
        Assert-True ($names -contains 'ParenProbe_X') "[TestMethod()] 应识别，实际：$($names -join ',')"
        Assert-True ($names -contains 'ComboProbe_X') "组合特性应识别，实际：$($names -join ',')"
        Assert-True ($names -notcontains 'IgnoredProbe_X') '[Ignore] 方法应跳过'
    } finally {
        Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
    }
}

Assert-Scenario '全仓例外表过滤器与派生词根在真实清单上恒非零（Script 区域跳过）' {
    Assert-True ($app.Count -gt 500) "App 清单异常（$($app.Count) 条）"
    Assert-True ($core.Count -gt 100) "Core 清单异常（$($core.Count) 条）"
    foreach ($rule in $overrides) {
        if ($rule.Kind -eq 'Script') { continue }
        Assert-FilterSelectsTests $rule.Name $rule.Filter $app $core
    }
    foreach ($root in $interactionTestPrefixes.Keys) {
        $filter = ($interactionTestPrefixes[$root] | ForEach-Object { "FullyQualifiedName~MainWindowInteractionTests.$_" }) -join '|'
        Assert-FilterSelectsTests "Tests.$root" $filter $app $core
    }
}

Assert-Scenario '一致性守卫：真实测试目录零违规' {
    $violations = @(Test-InteractionTestNaming $appTestsDir)
    Assert-True ($violations.Count -eq 0) "真实目录应零违规：$($violations -join '；')"
}

Assert-Scenario '一致性守卫：偏离词根的方法名报错（文件:方法）' {
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("selector-guard-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempDir | Out-Null
    try {
        $deviant = Join-Path $tempDir 'MainWindowInteractionTests.CardAi.cs'
        @'
    [STATestMethod]
    public void WrongPrefix_Method()
    {
    }
'@ | Set-Content -Path $deviant -Encoding UTF8
        $unregistered = Join-Path $tempDir 'MainWindowInteractionTests.Unregistered.cs'
        @'
    [TestMethod]
    public void Whatever_X()
    {
    }
'@ | Set-Content -Path $unregistered -Encoding UTF8
        $violations = @(Test-InteractionTestNaming $tempDir)
        Assert-True ($violations.Count -gt 0) '守卫应报违规'
        Assert-True (@($violations | Where-Object { $_ -like 'MainWindowInteractionTests.CardAi.cs: WrongPrefix_Method*' }).Count -eq 1) "应列出偏离方法（文件:方法），实际：$($violations -join '；')"
        Assert-True (@($violations | Where-Object { $_ -like 'MainWindowInteractionTests.Unregistered.cs: *未登记*' }).Count -eq 1) '应报词根表未登记'
    } finally {
        Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
    }
}

Assert-Scenario '端到端：选择器自身变更 → Affected[SelectorContract]（scripts 全量自噬消灭）' {
    # 真实子进程验证 CLI 契约（子脚本内 exit 会让管道捕获提前解栈，故用独立 powershell.exe 进程）。
    $output = Invoke-SelectorCli "-Explain -ChangeSet @('docs/e2e.md')"
    Assert-True ($LASTEXITCODE -eq 0) "豁免计划应 exit 0，实际 $LASTEXITCODE"
    Assert-True ($output -match '计划模式：Exempt') "子进程应打印豁免计划，实际：$output"
    $output2 = Invoke-SelectorCli "-Explain -ChangeSet @('scripts/run-affected-tests.ps1')"
    Assert-True ($output2 -match '计划模式：Affected') "选择器自身变更应为 Affected（契约测试承接），实际：$output2"
    Assert-True ($output2 -match '区域 SelectorContract') '应列出 SelectorContract 区域'
    Assert-True ($output2 -match '契约测试子进程：scripts/test-affected-selector.ps1') '应以契约测试子进程形态展示'
    $output3 = Invoke-SelectorCli "-Explain -ChangeSet @('scripts/test-installer-cleanup.ps1')"
    Assert-True ($output3 -match '计划模式：Full') '构建/安装链脚本应保持全量（显式清单）'
    Assert-True ($output3 -match '\[affected-full\] scripts/test-installer-cleanup.ps1') '全量理由应为 [affected-full] 标记行'
}

# ---- 判定序 v2 新场景 ----

Assert-Scenario '判定序：L1 例外表覆写优先于约定派生（Lifecycle 用 ~Topmost 而非 ~Lifecycle）' {
    $plan = Get-AffectedTestPlan @('src/FloatingTransferStation/Views/MainWindow.Lifecycle.cs') $app $core
    Assert-True ($plan.Mode -eq 'Affected') "模式应为 Affected，实际 $($plan.Mode)"
    Assert-True ($plan.Selected.Contains('TopmostImeSuppression')) 'L1 覆写（TopmostImeSuppression）应优先'
    Assert-True (-not $plan.Selected.Contains('App.Lifecycle')) '不得落 R-AppPartial 字面派生（欠选证明：字面 17 不含 Topmost 族）'
    $filter = $plan.Selected['TopmostImeSuppression'].Filters -join '|'
    Assert-True ($filter -eq 'FullyQualifiedName~Topmost') "filter 应为 L1 覆写值，实际 $filter"
}

Assert-Scenario 'R-AppPartial 词根桥接：MainWindow.DragAndDrop.cs → App.DragAndDrop（历史全量事件 R04 类根因消灭）' {
    $plan = Get-AffectedTestPlan @('src/FloatingTransferStation/Views/MainWindow.DragAndDrop.cs') $app $core
    Assert-True ($plan.Mode -eq 'Affected') "模式应为 Affected，实际 $($plan.Mode)"
    $entry = $plan.Selected['App.DragAndDrop']
    Assert-True ($null -ne $entry) '应派生 App.DragAndDrop（字面 0/桥接 36——桥接必要性实证）'
    $filter = $entry.Filters -join '|'
    Assert-True ($filter -like '*FullyQualifiedName~MainWindowInteractionTests.DragLifecycle*') "桥接段应含词根前缀 token，实际 $filter"
    Assert-True ($filter -like '*FullyQualifiedName~DragAndDrop') '并集应含字面 token'
    Assert-True ($filter -notlike '*(*' -and $filter -notlike '*)*') '扁平 OR，不得有分组括号（vstest 语法）'
}

Assert-Scenario 'R-AppPartial 选中集合断言（合成清单：桥接∪字面恰选中预期条目）' {
    $synthetic = @('MainWindowInteractionTests.DragLifecycle_A', 'MainWindowInteractionTests.QuickLaunch_X', 'WindowControllerTests.C', 'Z.DragAndDrop_W')
    $conv = Get-ConventionArea 'src/FloatingTransferStation/Views/MainWindow.DragAndDrop.cs' $synthetic @()
    Assert-True ($null -ne $conv) '合成清单上应可派生'
    $tokens = Get-FilterTokens $conv.Filter
    $hits = @($synthetic | Where-Object {
        $entry = $_
        @($tokens | Where-Object { $entry -like "*$_*" }).Count -gt 0
    })
    Assert-True ($hits.Count -eq 2) "应恰选中 2 条（桥接 1＋字面 1），实际 $($hits.Count)：$($hits -join ',')"
    Assert-True ($hits -contains 'MainWindowInteractionTests.DragLifecycle_A') '桥接命中应在选中集合'
    Assert-True ($hits -contains 'Z.DragAndDrop_W') '字面命中应在选中集合'
}

Assert-Scenario 'R-AppPartial 字面：MainWindow.PluginActions.cs → App.PluginActions' {
    $plan = Get-AffectedTestPlan @('src/FloatingTransferStation/Views/MainWindow.PluginActions.cs') $app $core
    Assert-True ($plan.Mode -eq 'Affected') "模式应为 Affected，实际 $($plan.Mode)"
    Assert-True ($plan.Selected.Contains('App.PluginActions')) '应派生 App.PluginActions（字面 5 命中）'
}

Assert-Scenario 'R-AppStem：WindowController 与 DataDirectoryChangeService 免登记接管（R10 C1 消灭）' {
    $plan = Get-AffectedTestPlan @('src/FloatingTransferStation/Services/WindowController.cs', 'src/FloatingTransferStation/Services/DataDirectoryChangeService.cs') $app $core
    Assert-True ($plan.Mode -eq 'Affected') "模式应为 Affected，实际 $($plan.Mode)"
    Assert-True ($plan.Selected.Contains('App.WindowController')) '应派生 App.WindowController'
    Assert-True ($plan.Selected.Contains('App.DataDirectoryChangeService')) '应派生 App.DataDirectoryChangeService（历史 E11 未知全量根因）'
}

Assert-Scenario 'R-CoreService 并集校验：Core 服务的同族测试可落在 App 工程' {
    $conv = Get-ConventionArea 'src/FloatingTransferStation.Core/Services/PanelStateMachine.cs' @('PanelStateMachineTests.Switch_ThenCollapse_X') @()
    Assert-True ($null -ne $conv) 'App 工程同族测试应计入命中（按单工程计会假报零命中）'
    Assert-True ($conv.Name -eq 'Core.PanelStateMachine') "区域名应为 Core.PanelStateMachine，实际 $($conv.Name)"
    $plan = Get-AffectedTestPlan @('src/FloatingTransferStation.Core/Services/LocalStore.cs') $app $core
    Assert-True ($plan.Selected.Contains('Core.LocalStore')) '现行泛型应正式化为 R-CoreService（36 命中）'
}

Assert-Scenario 'R-CoreModel：假想新模型字面 0 命中 → L3 指引；命中时派生 Core.{Stem}' {
    $conv = Get-ConventionArea 'src/FloatingTransferStation.Core/Models/BrandNewModel.cs' $app $core
    Assert-True ($null -eq $conv) '0 命中应不可派生（落 L3）'
    $conv2 = Get-ConventionArea 'src/FloatingTransferStation.Core/Models/BrandNewModel.cs' @('BrandNewModelTests.Load_X') @()
    Assert-True ($null -ne $conv2 -and $conv2.Name -eq 'Core.BrandNewModel') '补同名测试后应可派生（登记成本趋零）'
    $plan = Get-AffectedTestPlan @('src/FloatingTransferStation.Core/Models/BrandNewModel.cs') $app $core
    Assert-True ($plan.Mode -eq 'Full' -and $plan.FullReasons[0] -like '*BrandNewModel*') '假想新模型应全量＋指引'
}

Assert-Scenario 'SettingsWindow glob 收敛：零命中缩圈件仍由 L1 接住（不落 L3）' {
    $plan = Get-AffectedTestPlan @('src/FloatingTransferStation/Views/SettingsWindowPlacement.cs', 'src/FloatingTransferStation/Views/WindowShellClip.cs', 'src/FloatingTransferStation.Core/Design/SettingsWindowMetrics.cs') $app $core
    Assert-True ($plan.Mode -eq 'Affected') "模式应为 Affected，实际 $($plan.Mode)"
    Assert-True ($plan.Selected.Contains('SettingsWindow')) '缩圈件应仍命中 SettingsWindow 区域'
    $plan2 = Get-AffectedTestPlan @('src/FloatingTransferStation/Views/SettingsWindow.CategoryOrder.cs') $app $core
    Assert-True ($plan2.Selected.Contains('App.SettingsWindow')) 'SettingsWindow.CategoryOrder.cs 应交 R-AppStem（stem=SettingsWindow）'
}

Assert-Scenario '旧条目删除等价：MainWindow.Search.cs → App.Search（filter 逐字＝旧 Search 条目）' {
    $plan = Get-AffectedTestPlan @('src/FloatingTransferStation/Views/MainWindow.Search.cs') $app $core
    Assert-True ($plan.Selected.Contains('App.Search')) '应派生 App.Search（Search 例外表条目已删，W2 AUTO 等价）'
    $filter = $plan.Selected['App.Search'].Filters -join '|'
    Assert-True ($filter -like '*FullyQualifiedName~Search') "filter 应含字面 ~Search（与旧条目逐字同），实际 $filter"
    $plan2 = Get-AffectedTestPlan @('src/FloatingTransferStation/Views/MainWindow.AutoCleanup.cs', 'src/FloatingTransferStation/Views/MainWindow.CardAi.cs', 'src/FloatingTransferStation/Controls/TrimAwareTextBlock.cs') $app $core
    Assert-True ($plan2.Selected.Contains('App.AutoCleanup') -and $plan2.Selected.Contains('App.CardAi') -and $plan2.Selected.Contains('App.TrimAwareTextBlock')) 'AutoCleanup/CardAi/TrimAwareTextBlock 删除条目应全部由 L2 接管'
}

Assert-Scenario '迁移期登记：DesignTheme/Core.PluginActions/AppIcon（NONE=13 清零的 L1 部分）' {
    $plan = Get-AffectedTestPlan @('src/FloatingTransferStation/Services/DesignThemeManager.cs') $app $core
    Assert-True ($plan.Selected.Contains('DesignTheme')) 'DesignThemeManager 应命中 DesignTheme（ThemeEvidence|ThemeSwitch）'
    $plan2 = Get-AffectedTestPlan @('src/FloatingTransferStation.Core/Services/PluginActionPipeline.cs', 'src/FloatingTransferStation/Views/PluginActionTrialWindow.xaml', 'src/FloatingTransferStation/Views/PluginActionTrialWindow.xaml.cs') $app $core
    Assert-True ($plan2.Selected.Contains('Core.PluginActions')) 'OQ-1：Pipeline＋试用窗口三文件应同族登记 Core.PluginActions'
    $plan3 = Get-AffectedTestPlan @('src/FloatingTransferStation.IconTool/Program.cs', 'src/FloatingTransferStation/Assets/AppIcon/app-icon.json') $app $core
    Assert-True ($plan3.Selected.Contains('AppIcon')) 'IconTool 与图标规格源应命中 AppIcon（契约 #35 锁定面）'
    $plan4 = Get-AffectedTestPlan @('src/FloatingTransferStation/AssemblyInfo.cs', 'src/FloatingTransferStation/Converters/BooleanOrConverter.cs', 'src/FloatingTransferStation/Views/DialogShellFallback.cs') $app $core
    Assert-True ($plan4.Mode -eq 'Full' -and $plan4.FullReasons.Count -eq 3) '构建基建/无专测件应入全量表（NONE=13 清零的全量部分）'
}

Assert-Scenario 'F2 缩圈集成·K 族测试共享设施：BoardCreatedAtTestHelpers 精确区（不再全量；F2 §三.K）' {
    $plan = Get-AffectedTestPlan @('tests/FloatingTransferStation.Tests/BoardCreatedAtTestHelpers.cs') $app $core
    Assert-True ($plan.Mode -eq 'Affected') "时间戳测试助手应为 Affected，实际 $($plan.Mode)"
    Assert-True ($plan.Selected.Contains('BoardCreatedAt')) '应命中 BoardCreatedAt 区域（BoardService 40/BoardMutation 53/AutoCleanup 31 三族并集）'
    $filter = $plan.Selected['BoardCreatedAt'].Filters -join '|'
    Assert-True ($filter -like '*BoardService*' -and $filter -like '*BoardMutation*' -and $filter -like '*AutoCleanup*') "filter 应含三词根，实际 $filter"
    # KEEP-FULL 五件照旧全量（F2 §三.K：TestDirectory 102 文件/GlobalUsings 隐式 using/MIT.cs 共享壳/双 fixture 跨族）
    $plan2 = Get-AffectedTestPlan @('tests/FloatingTransferStation.Tests/TestDirectory.cs', 'tests/FloatingTransferStation.Tests/MainWindowInteractionTests.cs') $app $core
    Assert-True ($plan2.Mode -eq 'Full' -and $plan2.FullReasons.Count -eq 2) '共享壳五件应保留全量（F2 KEEP-FULL 实证）'
}

Assert-Scenario 'F2 缩圈集成·D 族跨区域接口三件：原子写/自启/密钥契约精确区（不再全量；F2 §三.D）' {
    $plan = Get-AffectedTestPlan @('src/FloatingTransferStation.Core/Services/IAtomicTextWriter.cs', 'src/FloatingTransferStation.Core/Services/AtomicTextWriter.cs') $app $core
    Assert-True ($plan.Mode -eq 'Affected') "原子写契约族应为 Affected，实际 $($plan.Mode)"
    Assert-True ($plan.Selected.Contains('Core.AtomicTextWriter')) 'IAtomicTextWriter 应并入 Core.AtomicTextWriter（实现＋接口同族）'
    $filter = $plan.Selected['Core.AtomicTextWriter'].Filters -join '|'
    Assert-True ($filter -like '*LocalStore*' -and $filter -like '*DataDirectoryChange*') "filter 应含 LocalStore|DataDirectoryChange 词根（36＋5 命中），实际 $filter"
    $plan2 = Get-AffectedTestPlan @('src/FloatingTransferStation.Core/Services/IStartupManager.cs') $app $core
    Assert-True ($plan2.Mode -eq 'Affected' -and $plan2.Selected.Contains('Core.IStartupManager')) '自启契约接口应精确区 Core.IStartupManager（6 族并集 31/12/108/31）'
    $plan3 = Get-AffectedTestPlan @('src/FloatingTransferStation.Core/Services/ISecretProtector.cs') $app $core
    Assert-True ($plan3.Mode -eq 'Affected' -and $plan3.Selected.Contains('Core.ISecretProtector')) '密钥保护契约接口应精确区 Core.ISecretProtector（CardAi 11＋SettingsWindowAi 22）'
    # KEEP-FULL 双件照旧全量（F2 §三.D：IBoardStore 19 文件跨 12+ 族、IPreferencesStore 含共享 harness 构造参数）
    $plan4 = Get-AffectedTestPlan @('src/FloatingTransferStation.Core/Services/IBoardStore.cs', 'src/FloatingTransferStation.Core/Services/IPreferencesStore.cs') $app $core
    Assert-True ($plan4.Mode -eq 'Full' -and $plan4.FullReasons.Count -eq 2) 'IBoardStore/IPreferencesStore 应保留全量（F2 KEEP-FULL 实证）'
}

Assert-Scenario 'L3 指引分派：partial 形态（C1）与运行时产物路径（C3）' {
    $plan = Get-AffectedTestPlan @('src/FloatingTransferStation/Views/MainWindow.BrandNew.cs') $app $core
    Assert-True ($plan.Mode -eq 'Full') '假想 MainWindow.BrandNew.cs 应全量'
    Assert-True ($plan.FullReasons[0] -like '*R-AppPartial 形态*' -and $plan.FullReasons[0] -like '*登记 BrandNew 的前缀*') 'partial 指引应含词根登记动作'
    Assert-True ($plan.FullReasons[0].Contains('[category=C1]')) 'partial 未登记 category=C1'
    $plan2 = Get-AffectedTestPlan @('.wbx/jobs/20261019-sample/task.txt') $app $core
    Assert-True ($plan2.Mode -eq 'Full') '清单外未知 untracked 路径应照走 L3（安全语义不破）'
    Assert-True ($plan2.FullReasons[0] -like '*疑运行时产物*' -and $plan2.FullReasons[0].Contains('[category=C3]')) '非产品树路径应给 C3 指引'
}

Assert-Scenario '词根未登记的交互测试文件 → 计划层直接 throw（加固：防绕过 CLI 守卫的静默窄跑）' {
    Assert-Throws { Get-AffectedTestPlan @('tests/FloatingTransferStation.Tests/MainWindowInteractionTests.NewFeature.cs') $app $core } '词根未登记' '计划层应拒绝未登记词根的测试文件'
}

Assert-Scenario '-Scope 双模式：与 -Base 同给报错；Edit 基线 HEAD；默认 Integrate＝merge-base' {
    $out = Invoke-SelectorCli '-Explain -Base HEAD -Scope Edit'
    Assert-True ($out -match '不得同时给出') "-Base＋-Scope 应报错，实际：$out"
    $out2 = Invoke-SelectorCli '-Explain -Scope Edit'
    Assert-True ($out2 -match '基线 HEAD（-Scope Edit）') "Edit 应解析基线 HEAD，实际：$out2"
    $out3 = Invoke-SelectorCli '-Explain'
    Assert-True ($out3 -match '基线 [0-9a-f]{7,40}（-Scope Integrate）') "默认应为 Integrate（merge-base），实际：$out3"
}

Assert-Scenario '反向守卫：真实仓库跟踪源文件零违规（三态完备）' {
    $tracked = @(& git -C $repoRoot -c core.quotepath=false ls-files -- src/)
    Assert-True ($LASTEXITCODE -eq 0) 'git ls-files 应成功'
    $tracked = @($tracked | Where-Object { $_ -like 'src/*.cs' -or $_ -like 'src/*.xaml' })
    Assert-True ($tracked.Count -ge 150) "跟踪源文件应 ≥150 个，实际 $($tracked.Count)"
    $violations = @(Test-TrackedSourceCoverage $tracked $app $core)
    Assert-True ($violations.Count -eq 0) "真实仓库应零违规：$($violations -join '；')"
}

Assert-Scenario '反向守卫：合成违规样例三段式输出（文件→缺什么→怎么补）' {
    $violations = @(Test-TrackedSourceCoverage @('src/FloatingTransferStation/Views/FakeUnregistered.cs') $app $core)
    Assert-True ($violations.Count -eq 1) "应报 1 处违规，实际 $($violations.Count)"
    Assert-True ($violations[0] -like 'src/FloatingTransferStation/Views/FakeUnregistered.cs → 缺什么*') '第一段应为文件→缺什么'
    Assert-True ($violations[0] -like '*怎么补（三选一）*') '第三段应为怎么补（三选一）'
    Assert-True ($violations[0] -like '*FQN~FakeUnregistered*') '应给出缩圈登记示例 filter'
}

Assert-Scenario '噪声防线：防后门校验（产品树前缀/非目录前缀 throw）' {
    try {
        $script:changesetNoisePrefixes = @('src/')
        Assert-Throws { Assert-ChangesetNoisePrefixes } '覆盖产品树' 'src/ 前缀应被拒'
        $script:changesetNoisePrefixes = @('.github/')
        Assert-Throws { Assert-ChangesetNoisePrefixes } '覆盖产品树' '.github/ 前缀应被拒'
        $script:changesetNoisePrefixes = @('README.md')
        Assert-Throws { Assert-ChangesetNoisePrefixes } '目录前缀' '非目录前缀（根文件）应被拒'
        $script:changesetNoisePrefixes = @('.preview-data/')
        Assert-ChangesetNoisePrefixes # 合法目录前缀应通过
    } finally {
        $script:changesetNoisePrefixes = @()
    }
}

Assert-Scenario '噪声防线：推导层过滤（[affected-noise] 行＋清单外仍走 L3）' {
    try {
        $script:changesetNoisePrefixes = @('.wbx/')
        Assert-True (Test-ChangesetNoise '.wbx/jobs/x/task.txt') '忽略谓词对 .wbx/ 路径应 true'
        Assert-True (-not (Test-ChangesetNoise 'src/FloatingTransferStation/App.xaml.cs')) '忽略谓词对 src/ 路径应 false'
        $kept = @(Filter-ChangesetNoise @('.wbx/jobs/x/task.txt', 'src/FloatingTransferStation/Views/SettingsWindow.xaml.cs'))
        Assert-True ($kept.Count -eq 1 -and $kept[0] -like 'src/*') '过滤后应只剩产品树文件'
    } finally {
        $script:changesetNoisePrefixes = @()
    }
}

# ---- 历史回放场景（W1 回放基线逐条；出处锚见各条 Evidence）----

foreach ($case in $replayCases) {
    $captured = $case
    Assert-Scenario ("历史回放 {0}：NewExpected={1}" -f $captured.Id, $captured.NewExpected) {
        $files = @($captured.Files)
        if ($captured.Id -eq 'R06') {
            # C3 条目：以忽略清单形态回放该轮未跟踪噪声（现代等价物＝gitignore 已挡住真实运行；
            # 显式注入时经推导层过滤）——spec §七.1 追加断言：谓词对 .wbx/ true、对 src/ false。
            try {
                $script:changesetNoisePrefixes = @('.wbx/')
                Assert-True (Test-ChangesetNoise '.wbx/jobs/20261019-sample/task.txt') '忽略谓词对 .wbx/ 路径应 true'
                Assert-True (-not (Test-ChangesetNoise 'src/FloatingTransferStation/Views/MainWindow.xaml.cs')) '忽略谓词对 src/ 路径应 false'
                $files = @(Filter-ChangesetNoise $files)
            } finally {
                $script:changesetNoisePrefixes = @()
            }
        }
        $plan = Get-AffectedTestPlan $files $app $core
        Assert-True ($plan.Mode -eq $captured.ExpectedMode) "选择器口径应为 $($captured.ExpectedMode)（NewExpected=$($captured.NewExpected)$(if ($captured.Deviation) { "；偏差理由：$($captured.Deviation)" } else { '' })），实际 $($plan.Mode)；Evidence：$($captured.Evidence)"
        if ($captured.ExpectedMode -eq 'Affected') {
            Assert-True ($plan.Selected.Count -ge 1) 'Affected 计划应选中至少一个区域'
        } else {
            Assert-True (@($plan.FullReasons | Where-Object { $_.StartsWith('[affected-full]') }).Count -ge 1) 'Full 计划应含 [affected-full] 标记行'
        }
        switch ($captured.Id) {
            'R01' {
                Assert-True ($plan.Selected.Contains('SelectorContract') -and $plan.Selected.Contains('PublicReleaseMaterials')) 'scripts 自噬应由 SelectorContract 承接'
            }
            'R03' {
                Assert-True ($plan.Selected.Contains('SettingsWindow') -and $plan.Selected.Contains('App.SettingsWindow')) 'xaml 走 L1、xaml.cs 走 R-AppStem'
            }
            'R04' {
                Assert-True (($plan.FullReasons -join ' ') -notlike '*BoardEmptyState*未命中*') 'C1 面（BoardEmptyState partial）应已消灭（R-AppPartial 桥接）'
                Assert-True (($plan.FullReasons -join ' ') -like '*MainWindow.xaml →*共享窗口壳*') '残余 Full 应仅为 C5 主窗壳'
            }
            'R05' {
                Assert-True ($plan.Selected.Contains('SelectorContract')) '选择器两文件应由 SelectorContract 承接（不再全量）'
            }
            'R07' {
                Assert-True (($plan.FullReasons -join ' ') -notlike '*未命中映射*') '全部文件应已登记/派生（残余 Full 仅为 C5 三全量类）'
                Assert-True (($plan.FullReasons -join ' ') -like '*Core 视图模型*') '残余 Full 应含 ViewModels（C5）'
            }
            'R08' {
                Assert-True ($plan.Selected.Contains('PublicReleaseMaterials')) '选择器口径＝PublicReleaseMaterials（发行门职责外的 Affected）'
            }
            'R09' {
                Assert-True ($plan.Selected.Contains('SelectorContract')) '词根表登记提交动 selector 的自噬应已消灭'
            }
            'R10' {
                Assert-True (($plan.FullReasons -join ' ') -like '*test-installer-cleanup.ps1*') '残余 Full 应仅为构建/安装链脚本（全量表保留）'
                Assert-True (($plan.FullReasons -join ' ') -notlike '*DataDirectoryChangeService*未命中*') 'C1 面（新 App 服务）应已由 R-AppStem 消灭'
                Assert-True ($plan.Selected.Contains('Lifecycle')) 'installer/*.iss 应命中 Lifecycle（非 ASCII 路径）'
            }
            'R11' {
                Assert-True ((($plan.FullReasons -join ' ') -notlike '*InterfaceFontManager*未命中*') -and (($plan.FullReasons -join ' ') -notlike '*PluginActionTrialWindow*未命中*')) 'C1 面（新服务/试用窗口）应已消灭'
                Assert-True ($plan.Selected.Contains('Core.PluginActions')) 'PluginActionTrialWindow 应命中登记条目 Core.PluginActions'
            }
        }
    }
}

# ---- 回放档案漂移核对（可选：-ReplayDataPath 指向 W1 本地档案；缺失即显式报错，不静默跳过）----
if ($ReplayDataPath) {
    Assert-Scenario '回放档案漂移核对（-ReplayDataPath）' {
        if (-not (Test-Path -LiteralPath $ReplayDataPath)) { throw "回放档案不存在：$ReplayDataPath（缺文件显式报错，不静默跳过）。" }
        $replay = $null
        . $ReplayDataPath
        Assert-True ($null -ne $replay -and @($replay).Count -eq $replayCases.Count) "档案条目数（$(@($replay).Count)）应与内联基线（$($replayCases.Count)）一致"
        foreach ($external in @($replay)) {
            $inline = @($replayCases | Where-Object { $_.Id -eq $external.Id })
            Assert-True ($inline.Count -eq 1) "档案条目 $($external.Id) 应存在于内联基线"
            $sameFiles = (@($external.Files | Sort-Object) -join ';') -eq (@($inline[0].Files | Sort-Object) -join ';')
            Assert-True ($sameFiles -and $external.NewExpected -eq $inline[0].NewExpected) "档案条目 $($external.Id) 的 Files/NewExpected 应与内联基线一致（防回放数据漂移）"
        }
    }
}

Write-Host ''
if ($failures.Count) {
    Write-Host "选择器契约测试失败（$($failures.Count)/$($failures.Count + $script:passedCount)）："
    foreach ($failure in $failures) { Write-Host "  - $failure" }
    exit 1
}
# 场景计数下限（Kind=Script 区域的恒非零承担：契约测试自身 0 场景/大幅缩水即 exit 1）
$minimumScenarios = 48
if ($script:passedCount -lt $minimumScenarios) {
    Write-Host "选择器契约测试失败：场景计数 $($script:passedCount) 低于下限 $minimumScenarios（场景缩水＝恒非零语义失守）。"
    exit 1
}
Write-Host "选择器契约测试全部通过（$script:passedCount 个场景，下限 $minimumScenarios）。"
exit 0
