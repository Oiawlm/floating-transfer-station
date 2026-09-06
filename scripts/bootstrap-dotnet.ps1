[CmdletBinding()]
param([switch]$VerifyOnly)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$toolsRoot = Join-Path $repoRoot '.tools'
$dotnetRoot = Join-Path $toolsRoot 'dotnet'
$dotnetExe = Join-Path $dotnetRoot 'dotnet.exe'
$installScript = Join-Path $toolsRoot 'dotnet-install.ps1'

function Get-RepositoryDotnetSdkVersion([string]$dotnetPath, [string]$repositoryRoot) {
    $sdk = (Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'global.json') | ConvertFrom-Json).sdk
    $previousErrorActionPreference = $ErrorActionPreference
    Push-Location -LiteralPath $repositoryRoot
    try {
        # The SDK resolver applies global.json, including its configured roll-forward policy.
        # Windows PowerShell otherwise turns redirected native stderr into a terminating error.
        $ErrorActionPreference = 'Continue'
        $versionOutput = & $dotnetPath --version 2>&1
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
        Pop-Location
    }
    if ($exitCode -ne 0) {
        throw "The .NET SDK does not satisfy global.json ($($sdk.version), rollForward=$($sdk.rollForward)). $versionOutput"
    }
    $resolvedVersion = ($versionOutput | Out-String).Trim()
    if ($resolvedVersion -notmatch '\A[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?\z') {
        throw "Unexpected .NET SDK version output: $resolvedVersion"
    }
    return $resolvedVersion
}

if (-not (Test-Path -LiteralPath $dotnetExe -PathType Leaf)) {
    if ($VerifyOnly) {
        throw 'The repository-local .NET SDK is missing; verification will not download or install it.'
    }
    New-Item -ItemType Directory -Force -Path $toolsRoot | Out-Null
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installScript
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installScript `
        -Channel '10.0' `
        -Architecture 'x64' `
        -InstallDir $dotnetRoot `
        -NoPath
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet-install.ps1 failed with exit code $LASTEXITCODE"
    }
}

$resolvedVersion = Get-RepositoryDotnetSdkVersion $dotnetExe $repoRoot
Write-Host "Repository .NET SDK: $resolvedVersion (global.json satisfied)."
