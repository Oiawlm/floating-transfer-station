[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$toolsRoot = Join-Path $repoRoot '.tools'
$dotnetRoot = Join-Path $toolsRoot 'dotnet'
$dotnetExe = Join-Path $dotnetRoot 'dotnet.exe'
$installScript = Join-Path $toolsRoot 'dotnet-install.ps1'

New-Item -ItemType Directory -Force -Path $toolsRoot | Out-Null

if (-not (Test-Path -LiteralPath $dotnetExe)) {
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

& $dotnetExe --version
if ($LASTEXITCODE -ne 0) {
    throw 'The repository-local .NET SDK could not be started.'
}
