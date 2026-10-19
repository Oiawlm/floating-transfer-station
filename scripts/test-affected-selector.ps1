# 受影响测试选择器契约测试：内嵌场景矩阵，断言 run-affected-tests.ps1 的「变更集→计划」
# 决策语义（三分/空选集两分/词根派生/豁免边界）与一致性守卫行为。0 退出码＝全部通过。
# 与 test-bootstrap-*.ps1 同族的独立契约测试；点源复用选择器的数据与函数（单一事实源，无副本）。
# 协议出处：docs/design-contracts.md 契约「受影响测试选区协议」（#32）。
[CmdletBinding()]
param(
    [string]$DotnetPath
)

$ErrorActionPreference = 'Stop'
$selectorScript = Join-Path $PSScriptRoot 'run-affected-tests.ps1'

# 点源加载：只取数据表与函数（$DefineOnly 哨兵阻断主执行）。
$DefineOnly = $true
. $selectorScript
$DefineOnly = $false

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
function Assert-Throws([scriptblock]$body, [string]$message) {
    try { & $body | Out-Null } catch { return }
    throw $message
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$appTestsDir = Join-Path $repoRoot 'tests\FloatingTransferStation.Tests'
$coreTestsDir = Join-Path $repoRoot 'tests\FloatingTransferStation.Core.Tests'

# ---- 场景矩阵（Phase 4 验收表的机器化）----

Assert-Scenario '仅改 SettingsWindow.xaml.cs → SettingsWindow 区域，无全量' {
    $plan = Get-AffectedTestPlan @('src/FloatingTransferStation/Views/SettingsWindow.xaml.cs')
    Assert-True ($plan.Mode -eq 'Affected') "模式应为 Affected，实际 $($plan.Mode)"
    Assert-True ($plan.Selected.Contains('SettingsWindow')) '应命中 SettingsWindow 区域'
    Assert-True ($plan.Selected.Count -eq 1) "应恰 1 个区域，实际 $($plan.Selected.Count)"
}

Assert-Scenario '设置改动＋design-contracts.md → 文档不扩区' {
    $plan = Get-AffectedTestPlan @('src/FloatingTransferStation/Views/SettingsWindow.xaml.cs', 'docs/design-contracts.md')
    Assert-True ($plan.Mode -eq 'Affected') "模式应为 Affected，实际 $($plan.Mode)"
    Assert-True ($plan.Selected.Count -eq 1) 'docs 文档不应新增区域'
    Assert-True ($plan.ExemptFiles -contains 'docs/design-contracts.md') 'design-contracts.md 应属豁免类'
}

Assert-Scenario '纯文档（docs/**.md＋png）→ 豁免跳过' {
    $plan = Get-AffectedTestPlan @('docs/usage.md', 'docs/images/shot.png')
    Assert-True ($plan.Mode -eq 'Exempt') "模式应为 Exempt，实际 $($plan.Mode)"
    Assert-True ($plan.ExemptFiles.Count -eq 2) "应豁免 2 个文件，实际 $($plan.ExemptFiles.Count)"
}

Assert-Scenario '仅改 MainWindowInteractionTests.CardAi.cs → 词根派生区域（非全族）' {
    $plan = Get-AffectedTestPlan @('tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardAi.cs')
    Assert-True ($plan.Mode -eq 'Affected') "模式应为 Affected，实际 $($plan.Mode)"
    $entry = $plan.Selected['Tests.CardAi']
    Assert-True ($null -ne $entry) '应派生 Tests.CardAi 区域'
    $filter = $entry.Filters -join '|'
    Assert-True ($filter -eq 'FullyQualifiedName~MainWindowInteractionTests.CardAi') "过滤器应为类边界词根形态，实际 $filter"
    Assert-True ($filter -ne 'FullyQualifiedName~MainWindowInteractionTests') '不得回退全族放宽'
}

Assert-Scenario 'QuickLaunchUrl 测试文件 → 词根表多前缀并集' {
    $plan = Get-AffectedTestPlan @('tests/FloatingTransferStation.Tests/MainWindowInteractionTests.QuickLaunchUrl.cs')
    $filter = $plan.Selected['Tests.QuickLaunchUrl'].Filters -join '|'
    Assert-True ($filter -like '*MainWindowInteractionTests.QuickLaunch*') "应含 QuickLaunch 词根，实际 $filter"
    Assert-True ($filter -like '*MainWindowInteractionTests.UrlInput*') "应含 UrlInput 词根，实际 $filter"
}

Assert-Scenario '改 ISettingsHost.cs → 全量＋取证理由' {
    $plan = Get-AffectedTestPlan @('src/FloatingTransferStation/Services/ISettingsHost.cs')
    Assert-True ($plan.Mode -eq 'Full') "模式应为 Full，实际 $($plan.Mode)"
    Assert-True ($plan.FullReasons[0] -like '*ISettingsHost.cs*跨区域*') "理由应含取证说明，实际 $($plan.FullReasons[0])"
}

Assert-Scenario '改 App.xaml.cs → 全量（共享窗口壳）' {
    $plan = Get-AffectedTestPlan @('src/FloatingTransferStation/App.xaml.cs')
    Assert-True ($plan.Mode -eq 'Full') "模式应为 Full，实际 $($plan.Mode)"
    Assert-True ($plan.FullReasons[0] -like '*共享窗口壳*') '理由应为共享窗口壳'
}

Assert-Scenario '新增未知 .cs 路径 → 全量（未命中映射）' {
    $plan = Get-AffectedTestPlan @('src/FloatingTransferStation/Views/BrandNewWindow.cs')
    Assert-True ($plan.Mode -eq 'Full') "模式应为 Full，实际 $($plan.Mode)"
    Assert-True ($plan.FullReasons[0] -like '*未命中映射*') '理由应为未命中映射'
}

Assert-Scenario '空变更集 → 豁免跳过（git 干净）' {
    $plan = Get-AffectedTestPlan @()
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
    $plan = Get-AffectedTestPlan @('README.md')
    Assert-True ($plan.Mode -eq 'Affected' -and $plan.Selected.Contains('PublicReleaseMaterials')) 'README 应命中 PublicReleaseMaterials 区域'
}

Assert-Scenario '过滤器恒非零漂移检查：死词根 fail fast' {
    Assert-Throws { Assert-FilterSelectsTests 'X' 'FullyQualifiedName~NoSuchMethodAnywhere12345' @('A.B_C'), @('D.E_F') } '死过滤器应抛错'
    Assert-Throws { Assert-FilterSelectsTests 'X' 'FullyQualifiedName~Live1|FullyQualifiedName~Dead1' @('A.Live1_M'), @('D.E_F') } '部分死词根应抛错'
    Assert-FilterSelectsTests 'X' 'FullyQualifiedName~Live1' @('A.Live1_M'), @() | Out-Null
}

Assert-Scenario '全仓规则过滤器与派生词根在真实清单上恒非零' {
    $app = @(Get-TestInventory $appTestsDir)
    $core = @(Get-TestInventory $coreTestsDir)
    Assert-True ($app.Count -gt 500) "App 清单异常（$($app.Count) 条）"
    Assert-True ($core.Count -gt 100) "Core 清单异常（$($core.Count) 条）"
    foreach ($rule in $rules) { Assert-FilterSelectsTests $rule.Name $rule.Filter $app $core }
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

Assert-Scenario '端到端：-Explain -ChangeSet 子进程输出计划（参数接线）' {
    # 真实子进程验证 CLI 契约（子脚本内 exit 会让管道捕获提前解栈，故用 powershell.exe -File）。
    $output = (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $selectorScript -Explain -ChangeSet 'docs/e2e.md' 2>&1 |
        ForEach-Object { "$_" }) -join "`n"
    Assert-True ($LASTEXITCODE -eq 0) "豁免计划应 exit 0，实际 $LASTEXITCODE"
    Assert-True ($output -match '计划模式：Exempt') "子进程应打印豁免计划，实际：$output"
    $output2 = (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $selectorScript -Explain -ChangeSet 'scripts/run-affected-tests.ps1' 2>&1 |
        ForEach-Object { "$_" }) -join "`n"
    Assert-True ($output2 -match '计划模式：Full') '脚本自身变更应计划全量'
    Assert-True ($output2 -match '构建/测试基础设施脚本') '理由应为基础设施脚本'
}

Write-Host ''
if ($failures.Count) {
    Write-Host "选择器契约测试失败（$($failures.Count)/$($failures.Count + $script:passedCount)）："
    foreach ($failure in $failures) { Write-Host "  - $failure" }
    exit 1
}
Write-Host "选择器契约测试全部通过（$script:passedCount 个场景）。"
exit 0
