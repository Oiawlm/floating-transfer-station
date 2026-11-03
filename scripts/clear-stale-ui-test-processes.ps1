# 清点/清理本机残留的 UI 测试进程（flake 台账 A7 配套，2026-11-02）。
# 实证背景：崩溃/中断的 UI 套件会留下持有前台焦点或真实光标落点的 testhost
# 进程，干扰后续真输入类测试（2026-11-02 detail-polish 批 W1 报告登记）。
#
# 用法：
#   clear-stale-ui-test-processes.ps1            # 只清点（默认，不动手）
#   clear-stale-ui-test-processes.ps1 -Kill      # 清点并结束「超过 2 小时」的残留
#   clear-stale-ui-test-processes.ps1 -Kill -All # 结束全部命中的残留（任何年龄）
#
# 命中口径：testhost*.exe / VSTestRunner*.exe，且命令行含 FloatingTransferStation
# （限本仓库套件，不误伤其他项目）；-Kill 默认只结束启动超过 2 小时者（在跑的
# 正常套件不受影响——2 小时远超 UI 全量时长；确需全部结束用 -All 自担）。
param(
    [switch]$Kill,
    [switch]$All
)

$ErrorActionPreference = 'Stop'
$staleAge = [TimeSpan]::FromHours(2)

$candidates = Get-CimInstance Win32_Process |
    Where-Object {
        $PSItem.Name -like 'testhost*' -or $PSItem.Name -like 'VSTestRunner*'
    } |
    Where-Object { $PSItem.CommandLine -like '*FloatingTransferStation*' }

if ($candidates.Count -eq 0) {
    Write-Host '未发现本仓库残留 UI 测试进程。'
    return
}

$now = [DateTime]::Now
foreach ($process in $candidates) {
    $age = $now - $process.CreationDate
    $stale = $age -gt $staleAge
    $action = if ($Kill -and ($stale -or $All)) { 'KILL' } else { 'keep' }
    Write-Host ("{0,-5} pid={1,-8} age={2:hh\:mm\:ss} started={3:yyyy-MM-dd HH:mm:ss} {4}" -f `
        $action, $process.ProcessId, $age, $process.CreationDate, $process.CommandLine.Substring(0, [Math]::Min(120, $process.CommandLine.Length)))
    if ($action -eq 'KILL') {
        try { Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop } catch { Write-Warning "结束 pid=$($process.ProcessId) 失败：$($PSItem.Exception.Message)" }
    }
}

if (-not $Kill) {
    Write-Host '（只清点未动手；加 -Kill 结束超 2 小时残留，-Kill -All 结束全部命中项。）'
}
