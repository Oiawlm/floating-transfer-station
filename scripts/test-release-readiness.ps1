[CmdletBinding()]
param(
    [string]$ChangelogPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'CHANGELOG.md'),
    # Explicit release context only: build-release.ps1 passes this in -ForRelease runs.
    # Never enabled by default so steady-state main (version.txt == latest tag) stays green in CI.
    [switch]$RequireVersionAhead,
    [string]$VersionPath,
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'
$changelog = Get-Content -LiteralPath $ChangelogPath -Raw -Encoding UTF8
# Keep this script readable by Windows PowerShell 5.1 as well as pwsh.
$unreleasedHeading = -join ([char[]]@(0x672A, 0x53D1, 0x5E03))
$sections = [regex]::Matches(
    $changelog,
    '(?ms)^## ' + [regex]::Escape($unreleasedHeading) + '\r?\n(?<Body>.*?)(?=^## |\z)')
if ($sections.Count -ne 1) {
    throw 'Release readiness failed: CHANGELOG must have exactly one unreleased section.'
}
if (-not [string]::IsNullOrWhiteSpace($sections[0].Groups['Body'].Value)) {
    throw 'Release readiness failed: move unreleased entries into the intended release section first.'
}

if (-not $RequireVersionAhead) {
    Write-Host 'Release readiness passed: the unreleased section is empty.'
    return
}

# Version-ahead assertion (release train, see docs/releasing.md "发行列车").
if ([string]::IsNullOrWhiteSpace($VersionPath)) {
    $VersionPath = Join-Path (Split-Path -Parent $ChangelogPath) 'version.txt'
}
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $VersionPath
}

$localVersionText = (Get-Content -Raw -Encoding UTF8 -LiteralPath $VersionPath).Trim()
$localVersion = $null
if (-not [version]::TryParse($localVersionText, [ref]$localVersion)) {
    throw ('Release readiness failed: version.txt does not contain a parseable version: ' + $localVersionText)
}

# Fetch first, assert separately; a failed fetch must error out, never silently pass.
& git -C $RepoRoot fetch --tags | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Release readiness failed: git fetch --tags failed (see git output above); cannot verify the latest remote version tag.'
}
$tags = & git -C $RepoRoot tag -l 'v*' --sort=-v:refname
if ($LASTEXITCODE -ne 0) {
    throw 'Release readiness failed: git tag listing failed (see git output above).'
}

$latestTag = $null
$latestRemoteVersion = $null
foreach ($tag in @($tags)) {
    $name = ('' + $tag).Trim()
    if (-not $name.StartsWith('v')) { continue }
    $candidate = $null
    if (-not [version]::TryParse($name.Substring(1), [ref]$candidate)) {
        # Historical or prerelease tags that do not parse as pure versions cannot
        # represent a shipped release; skip them instead of failing the train.
        Write-Host ('Skipping tag that is not a plain numeric version: ' + $name)
        continue
    }
    $latestTag = $name
    $latestRemoteVersion = $candidate
    break
}

if ($null -eq $latestTag) {
    Write-Host 'Release readiness passed: no numeric v* tag exists on the remote; the version-ahead assertion has nothing to compare against.'
    return
}

Write-Host ('Latest remote version tag: ' + $latestTag)
if ($localVersion -le $latestRemoteVersion) {
    throw ('Release readiness failed: version.txt (' + $localVersion.ToString() + ') is not ahead of the latest remote tag ' + $latestTag + '. Another train already claimed this version: replay on the latest main and advance the version (see docs/releasing.md, the release-train section).')
}
Write-Host ('Release readiness passed: version.txt ' + $localVersion.ToString() + ' is strictly ahead of ' + $latestTag + '.')
