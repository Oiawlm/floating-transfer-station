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

& $dotnet test $solution `
    -c Release `
    --filter $filters[$Scope] `
    --results-directory $results `
    --logger "trx;LogFileName=adversarial-$Scope.trx"
if ($LASTEXITCODE -ne 0) {
    throw "Adversarial test scope '$Scope' failed."
}
