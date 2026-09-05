[CmdletBinding()]
param(
    [switch]$ForRelease,
    [string]$DotnetPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnet = if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
} else {
    [System.IO.Path]::GetFullPath($DotnetPath)
}
$iscc = Join-Path $repoRoot '.tools\inno\ISCC.exe'
$publishDirectory = Join-Path $repoRoot 'artifacts\publish'
$installerDirectory = Join-Path $repoRoot 'artifacts\installer'
$solution = Join-Path $repoRoot 'FloatingTransferStation.slnx'
$project = Join-Path $repoRoot 'src\FloatingTransferStation\FloatingTransferStation.csproj'
$innoScripts = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'installer') -Filter '*.iss' -File)
if ($innoScripts.Count -ne 1) {
    throw "Expected exactly one Inno Setup script, found $($innoScripts.Count)."
}
$innoScript = $innoScripts[0].FullName

if ($ForRelease) {
    & (Join-Path $PSScriptRoot 'test-release-readiness.ps1') | Out-Host
}

function Reset-WorkspaceDirectory([string]$path) {
    $resolvedRoot = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\') + '\'
    $resolvedPath = [System.IO.Path]::GetFullPath($path)
    if (-not $resolvedPath.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset a directory outside the repository: $resolvedPath"
    }

    if (Test-Path -LiteralPath $resolvedPath) {
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $resolvedPath | Out-Null
}

& (Join-Path $PSScriptRoot 'test-bootstrap-inno-path-contract.ps1') | Out-Host
if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    & (Join-Path $PSScriptRoot 'bootstrap-dotnet.ps1') | Out-Host
}
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "The selected .NET executable does not exist: $dotnet"
}
& (Join-Path $PSScriptRoot 'bootstrap-inno.ps1') | Out-Host
& (Join-Path $PSScriptRoot 'test-installer-cleanup.ps1') -IsccPath $iscc | Out-Host

$innoText = Get-Content -Raw -Encoding UTF8 -LiteralPath $innoScript
if ($innoText -notmatch 'DisableDirPage=no' -or
    $innoText -match '\{autodesktop\}' -or
    $innoText -notmatch 'CurrentVersion\\Run' -or
    $innoText -notmatch 'CreateInputDirPage\(wpSelectDir' -or
    $innoText -notmatch 'PrepareToInstall' -or
    $innoText -notmatch 'CurStepChanged' -or
    $innoText -notmatch 'InitializeUninstall' -or
    $innoText -notmatch 'RegWriteStringValue\(HKCU' -or
    $innoText -notmatch 'DataDirectoryRegistryValue' -or
    $innoText -notmatch 'IsManagedDataDirectory' -or
    $innoText -notmatch 'DelTree') {
    throw 'Installer contract failed: configurable directories, no desktop shortcut, startup entry, dynamic registration, and safe cleanup are required.'
}
$uninstallWord = ([string][char]0x5378) + ([char]0x8F7D)
$uninstallShortcutPattern =
    '(?m)^[ \t]*Name:[ \t]*"\{autoprograms\}\\' +
    [regex]::Escape($uninstallWord) +
    '\{#MyAppName\}";[ \t]*Filename:[ \t]*"\{uninstallexe\}"[ \t]*\r?$'
if ($innoText -notmatch $uninstallShortcutPattern) {
    throw 'Installer contract failed: the Start menu uninstall shortcut is required.'
}
$recursiveDeleteMatches = [regex]::Matches(
    $innoText,
    '(?im)^[ \t]*Type:[ \t]*filesandordirs;[ \t]*Name:[ \t]*"([^"\r\n]+)"[ \t]*\r?$')
if ($recursiveDeleteMatches.Count -ne 0) {
    throw 'Installer contract failed: static recursive uninstall deletion is not allowed.'
}
$versionMatches = [regex]::Matches(
    $innoText,
    '(?m)^[ \t]*#define[ \t]+MyAppVersion[ \t]+"([^"\r\n]+)"[ \t]*\r?$')
if ($versionMatches.Count -ne 1) {
    throw "Installer contract failed: expected exactly one MyAppVersion definition, found $($versionMatches.Count)."
}
$version = $versionMatches[0].Groups[1].Value

& $dotnet test $solution -c Release
if ($LASTEXITCODE -ne 0) { throw 'Release tests failed.' }

Reset-WorkspaceDirectory $publishDirectory
Reset-WorkspaceDirectory $installerDirectory

& $dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }

& $iscc $innoScript
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }

$setups = @(Get-ChildItem -LiteralPath $installerDirectory -Filter "*-Setup-$version.exe" -File)
if ($setups.Count -ne 1) {
    throw "Expected exactly one release installer, found $($setups.Count)."
}
$setup = $setups[0].FullName

Write-Host "Release installer: $setup"
