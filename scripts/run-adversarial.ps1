[CmdletBinding()]
param(
    [ValidateSet('Full', 'Clipboard', 'Storage', 'Interaction', 'Lifecycle')]
    [string]$Scope = 'Full'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'bootstrap-dotnet.ps1') | Out-Host
$dotnet = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
$solution = Join-Path $repoRoot 'FloatingTransferStation.slnx'
$results = Join-Path $repoRoot 'TestResults'
New-Item -ItemType Directory -Force -Path $results | Out-Null

$filters = @{
    Full = 'TestCategory=Adversarial'
    Clipboard = 'TestCategory=Adversarial&FullyQualifiedName~Clipboard'
    Storage = 'TestCategory=Adversarial&(FullyQualifiedName~LocalStore|FullyQualifiedName~ImageNormalizer)'
    Interaction = 'TestCategory=Adversarial&(FullyQualifiedName~Board|FullyQualifiedName~Panel|FullyQualifiedName~Drag|FullyQualifiedName~MainWindow|FullyQualifiedName~CategoryScroll|FullyQualifiedName~ImageThumbnail|FullyQualifiedName~ExternalDrop|FullyQualifiedName~WindowController)'
    Lifecycle = 'TestCategory=Adversarial&FullyQualifiedName~Lifecycle'
}

# LogFilePrefix（替代旧 LogFileName 固定名）使每次运行写独立时间戳 trx；运行前清理同前缀
# 旧文件防累积——只删「本次运行开始时刻之前」修改的文件（契约 #53）：同检出并发跑动的
# 另一个套件刚写出的新 trx 不在清理面内，固定名覆写与互删新 trx 的窗口一并消灭。
$runStartedUtc = [DateTime]::UtcNow
Get-ChildItem $results -Filter "adversarial-$Scope*.trx" -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTimeUtc -lt $runStartedUtc } |
    Remove-Item -Force -ErrorAction SilentlyContinue

& $dotnet test $solution `
    -c Release `
    --filter $filters[$Scope] `
    --results-directory $results `
    --logger "trx;LogFilePrefix=adversarial-$Scope"
if ($LASTEXITCODE -ne 0) {
    throw "Adversarial test scope '$Scope' failed."
}
