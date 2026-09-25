<#
.SYNOPSIS
    Repository hygiene checks for floating-transfer-station.

.DESCRIPTION
    Two checks, both must pass (exit 0) or the script fails (exit 1):

      A. Root entry whitelist
         Every root-level entry tracked by git must be listed in
         $AllowedRootEntries below. Extra or missing entries are reported.

      B. README local link integrity
         Every relative Markdown link in README.md must resolve to an
         existing file. Remote links, anchors, images and mailto are skipped.

    All violations are collected before the script exits; it never stops at
    the first failure.

.PARAMETER RepoRoot
    Optional explicit repository root. Defaults to the parent of $PSScriptRoot
    (this script lives in <repo>/scripts/).

.NOTES
    Compatible with Windows PowerShell 5.1 and PowerShell 7+ (ASCII source,
    no BOM required). Requires git on PATH.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$RepoRoot
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# Root entries tracked by git that are allowed to exist (complete list).
$AllowedRootEntries = @(
    '.github'
    '.gitignore'
    'AGENTS.md'
    'CHANGELOG.md'
    'CODE_OF_CONDUCT.md'
    'CONTRIBUTING.md'
    'Directory.Build.props'
    'FloatingTransferStation.Mac.slnx'
    'FloatingTransferStation.slnx'
    'LICENSE'
    'PROJECT_GUIDE.md'
    'README.md'
    'ROADMAP.md'
    'SECURITY.md'
    'docs'
    'global.json'
    'installer'
    'scripts'
    'src'
    'tests'
    'version.txt'
)

$ReadmeRelativePath = 'README.md'
$violations = New-Object System.Collections.Generic.List[string]

function Add-Violation {
    param([Parameter(Mandatory = $true)][string]$Message)
    $violations.Add($Message)
}

function Get-RepositoryRoot {
    if (-not [string]::IsNullOrWhiteSpace($RepoRoot)) {
        return [System.IO.Path]::GetFullPath($RepoRoot)
    }

    return [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
}

function Get-TrackedFiles {
    param([Parameter(Mandatory = $true)][string]$Root)

    # core.quotepath=false keeps non-ASCII paths readable (no \ooo escapes).
    $output = & git -C $Root -c core.quotepath=false ls-files
    if ($LASTEXITCODE -ne 0) {
        throw "git ls-files failed with exit code $LASTEXITCODE."
    }

    $files = New-Object System.Collections.Generic.List[string]
    foreach ($line in @($output)) {
        $text = [string]$line
        if (-not [string]::IsNullOrWhiteSpace($text)) {
            $files.Add($text.Trim())
        }
    }

    return $files.ToArray()
}

function Get-MarkdownLocalLinks {
    <#
        Returns objects { Line, Text, Target, PathPart } for relative links.
        Skips images ![..](..), scheme links (http/https/mailto), pure
        anchors (#..) and protocol-relative (//..) targets.
    #>
    param([Parameter(Mandatory = $true)][string]$MarkdownText)

    $results = New-Object System.Collections.Generic.List[object]
    $pattern = '(?<!!)\[([^\]]*)\]\(([^)]*)\)'

    foreach ($match in [regex]::Matches($MarkdownText, $pattern)) {
        $linkText = $match.Groups[1].Value
        $inner = $match.Groups[2].Value.Trim()
        if ($inner -eq '') { continue }

        if ($inner.StartsWith('<')) {
            $closeIndex = $inner.IndexOf('>')
            if ($closeIndex -lt 1) { continue }
            $target = $inner.Substring(1, $closeIndex - 1).Trim()
        }
        else {
            $target = ($inner -split '\s+')[0]
        }

        if ($target -eq '') { continue }
        if ($target.StartsWith('#')) { continue }
        if ($target.StartsWith('//')) { continue }
        if ($target -match '^[A-Za-z][A-Za-z0-9+.\-]*:') { continue }

        $pathPart = $target
        $hashIndex = $pathPart.IndexOf('#')
        if ($hashIndex -ge 0) { $pathPart = $pathPart.Substring(0, $hashIndex) }
        $queryIndex = $pathPart.IndexOf('?')
        if ($queryIndex -ge 0) { $pathPart = $pathPart.Substring(0, $queryIndex) }
        if ($pathPart -eq '') { continue }

        try {
            $pathPart = [System.Uri]::UnescapeDataString($pathPart)
        }
        catch {
            # Keep the raw path when it is not a valid escape sequence.
        }

        $lineNumber = ($MarkdownText.Substring(0, $match.Index) -split "`n").Count
        $results.Add([pscustomobject]@{
            Line = $lineNumber
            Text = $linkText
            Target = $target
            PathPart = $pathPart
        })
    }

    return $results.ToArray()
}

$repoRootPath = Get-RepositoryRoot

Write-Output '== Repository hygiene check =='
Write-Output ("Repo root : {0}" -f $repoRootPath)
Write-Output ''

# ---------------------------------------------------------------------------
# Check A: root entry whitelist
# ---------------------------------------------------------------------------

Write-Output '--- Check A: root entry whitelist ---'

$trackedFiles = @(Get-TrackedFiles -Root $repoRootPath)
$actualRootEntries = @(
    $trackedFiles |
        ForEach-Object { $_.Replace('\', '/') } |
        ForEach-Object { ($_ -split '/')[0] } |
        Where-Object { $_ -ne '' } |
        Sort-Object -Unique
)

$missingEntries = @($AllowedRootEntries | Where-Object { $actualRootEntries -cnotcontains $_ } | Sort-Object)
$extraEntries = @($actualRootEntries | Where-Object { $AllowedRootEntries -cnotcontains $_ } | Sort-Object)

if ($missingEntries.Count -eq 0 -and $extraEntries.Count -eq 0) {
    Write-Output ("[OK] Root entries match the whitelist ({0} entries)." -f $actualRootEntries.Count)
}
else {
    foreach ($entry in $extraEntries) {
        Write-Output ("[FAIL] Unexpected root entry (not whitelisted): {0}" -f $entry)
        Add-Violation ("Check A: unexpected root entry '{0}'." -f $entry)
    }

    foreach ($entry in $missingEntries) {
        Write-Output ("[FAIL] Whitelisted entry missing from repository: {0}" -f $entry)
        Add-Violation ("Check A: whitelisted entry '{0}' is missing." -f $entry)
    }
}

Write-Output ''

# ---------------------------------------------------------------------------
# Check B: README local link integrity
# ---------------------------------------------------------------------------

Write-Output ("--- Check B: {0} local links ---" -f $ReadmeRelativePath)

$readmePath = Join-Path -Path $repoRootPath -ChildPath $ReadmeRelativePath

if (-not (Test-Path -LiteralPath $readmePath -PathType Leaf)) {
    Write-Output ("[FAIL] {0} not found." -f $ReadmeRelativePath)
    Add-Violation ("Check B: {0} is missing." -f $ReadmeRelativePath)
}
else {
    $readmeDir = Split-Path -Parent $readmePath
    $markdownText = Get-Content -LiteralPath $readmePath -Raw -Encoding UTF8
    if ($null -eq $markdownText) { $markdownText = '' }

    $localLinks = @(Get-MarkdownLocalLinks -MarkdownText $markdownText)
    $brokenCount = 0

    foreach ($link in $localLinks) {
        $resolvedPath = $null
        try {
            $resolvedPath = [System.IO.Path]::GetFullPath(
                (Join-Path -Path $readmeDir -ChildPath ($link.PathPart.Replace('/', [System.IO.Path]::DirectorySeparatorChar))))
        }
        catch {
            Write-Output ("[FAIL] {0}:{1} link target is not a valid path: [{2}]({3})" -f `
                    $ReadmeRelativePath, $link.Line, $link.Text, $link.Target)
            Add-Violation ("Check B: {0}:{1} invalid link target '[{2}]({3})'." -f `
                    $ReadmeRelativePath, $link.Line, $link.Text, $link.Target)
            $brokenCount++
            continue
        }

        if (-not (Test-Path -LiteralPath $resolvedPath)) {
            Write-Output ("[FAIL] {0}:{1} link target does not exist: [{2}]({3})" -f `
                    $ReadmeRelativePath, $link.Line, $link.Text, $link.Target)
            Add-Violation ("Check B: {0}:{1} link target does not exist '[{2}]({3})'." -f `
                    $ReadmeRelativePath, $link.Line, $link.Text, $link.Target)
            $brokenCount++
        }
    }

    if ($brokenCount -eq 0) {
        Write-Output ("[OK] All {0} local links resolve to existing files." -f $localLinks.Count)
    }
}

Write-Output ''

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------

if ($violations.Count -gt 0) {
    Write-Output ("=== FAILED: {0} violation(s) ===" -f $violations.Count)
    for ($index = 0; $index -lt $violations.Count; $index++) {
        Write-Output ("  {0}. {1}" -f ($index + 1), $violations[$index])
    }

    exit 1
}

Write-Output '=== PASSED: repository hygiene checks ==='
exit 0
