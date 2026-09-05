[CmdletBinding()]
param(
    [string]$ChangelogPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'CHANGELOG.md')
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

Write-Host 'Release readiness passed: the unreleased section is empty.'
