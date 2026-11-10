<#
.SYNOPSIS
    Repository hygiene checks for floating-transfer-station.

.DESCRIPTION
    Four checks, all must pass (exit 0) or the script fails (exit 1):

      A. Root entry whitelist
         Every root-level entry tracked by git must be listed in
         $AllowedRootEntries below. Extra or missing entries are reported.

      B. README local link integrity
         Every relative Markdown link in README.md must resolve to an
         existing file. Remote links, anchors, images and mailto are skipped.

      C. Forbidden local-path references (frozen baseline)
         Tracked text files must not reference gitignored maintainer-local
         paths (docs/research/, docs/observations.md, docs/improvement-loop.md,
         docs/handoff/, PROJECT_GUIDE, and the local image folder). Per-file
         occurrence counts (case-insensitive, summed over all patterns) must
         not exceed the frozen baseline measured on 2026-11-02 when the
         policy landed (design contract #52). Exemptions: .gitignore (the
         ignore list itself) and this script (its pattern table legitimately
         contains the strings).

      D. Design-contract locked test-name existence (local-only input)
         When the maintainer-local design contract list
         (docs/design-contracts.md, gitignored, absent in CI) exists, every
         backtick-quoted test-shaped identifier it references (exact name, or
         a `Prefix_*` wildcard matched by prefix) must resolve to a method
         declared under tests/. Tokens that merely look like tests but are
         documented non-test identifiers are listed in
         $ContractLockExemptIdentifiers below (frozen baseline, may only
         shrink). When the file is absent the check reports a skip.

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
    'CHANGELOG.md'
    'Directory.Build.props'
    'FloatingTransferStation.slnx'
    'LICENSE'
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

function Get-SubstringMatchCount {
    <#
        Counts non-overlapping, case-insensitive (ordinal) occurrences of
        $Needle in $Text. OrdinalIgnoreCase prevents trivial casing from
        evading Check C.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle
    )

    $count = 0
    $index = 0
    while ($true) {
        $index = $Text.IndexOf($Needle, $index, [System.StringComparison]::OrdinalIgnoreCase)
        if ($index -lt 0) { break }
        $count++
        $index += $Needle.Length
    }

    return $count
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
# Check C: forbidden local-path references (frozen baseline)
# ---------------------------------------------------------------------------

# Gitignored maintainer-local paths that tracked files must not reference
# (policy decided 2026-11-02, design contract #52). The last pattern is a
# non-ASCII directory name built from char codes so this script stays
# ASCII-only (Windows PowerShell 5.1 reads BOM-less scripts as ANSI).
$ForbiddenLocalPathPatterns = @(
    'docs/research/'
    'docs/observations.md'
    'docs/improvement-loop.md'
    'docs/handoff/'
    'PROJECT_GUIDE'
    ([string][char]0x56FE + [string][char]0x7247 + '/')
)

# Files exempt from Check C: the ignore list itself (it legitimately lists
# the local entries) and this script (its pattern table legitimately
# contains the strings).
$CheckCExemptFiles = @(
    '.gitignore'
    'scripts/check-repo-hygiene.ps1'
)

# Tracked-file extensions scanned as text; binary assets are skipped.
$CheckCTextExtensions = @(
    '.cs'
    '.csproj'
    '.editorconfig'
    '.gitignore'
    '.json'
    '.md'
    '.props'
    '.ps1'
    '.slnx'
    '.txt'
    '.xaml'
    '.yml'
)

# Frozen baseline: per-file occurrence counts (case-insensitive, summed over
# all patterns above) measured on 2026-11-02 when the policy landed. Counts
# may only go down. Lowering the table requires re-measuring and editing it
# in its own change with the measured counts stated in the commit message;
# tracked files absent from the table have a baseline of 0. Historical
# entries are frozen (annotations may be appended, wording is not rewritten).
$ForbiddenLocalPathBaseline = @{
    'CHANGELOG.md'                                                = 11
    'ROADMAP.md'                                                  = 4
    'docs/README.md'                                              = 1
    'docs/design.md'                                              = 1
    'docs/images/README.md'                                       = 1
    'docs/usage.md'                                               = 1
    'scripts/run-affected-tests.ps1'                              = 1
    'scripts/test-affected-selector.ps1'                          = 1
    'src/FloatingTransferStation.Core/Services/FaviconClient.cs'  = 1
    'src/FloatingTransferStation/Models/ExternalDropPayload.cs'   = 1
    'src/FloatingTransferStation/Resources/MainWindowStyles.xaml' = 1
    'src/FloatingTransferStation/Views/MainWindow.CardGestures.cs' = 1
    'src/FloatingTransferStation/Views/MainWindow.DragAndDrop.cs' = 1
    'src/FloatingTransferStation/Views/MainWindow.xaml'           = 1
    'tests/FloatingTransferStation.Tests/ExternalDropPayloadReaderTests.cs' = 1
    'tests/FloatingTransferStation.Tests/MainWindowInteractionTests.BtnGridEvidence.cs' = 1
    'tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardGestures.cs' = 1
}

Write-Output '--- Check C: forbidden local-path references (frozen baseline) ---'

$checkCFilesWithReferences = 0
$checkCRequired = $false
$checkCBelowBaseline = 0

foreach ($trackedFile in $trackedFiles) {
    $normalizedPath = $trackedFile.Replace('\', '/')
    if ($CheckCExemptFiles -ccontains $normalizedPath) { continue }

    $extension = [System.IO.Path]::GetExtension($normalizedPath).ToLowerInvariant()
    if ($CheckCTextExtensions -notcontains $extension) { continue }

    $contentPath = Join-Path -Path $repoRootPath -ChildPath ($trackedFile.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
    $content = [System.IO.File]::ReadAllText($contentPath)

    $actualCount = 0
    foreach ($forbiddenPattern in $ForbiddenLocalPathPatterns) {
        $actualCount += Get-SubstringMatchCount -Text $content -Needle $forbiddenPattern
    }
    if ($actualCount -eq 0) { continue }

    $checkCFilesWithReferences++
    $allowedCount = 0
    if ($ForbiddenLocalPathBaseline.ContainsKey($normalizedPath)) {
        $allowedCount = $ForbiddenLocalPathBaseline[$normalizedPath]
    }

    if ($actualCount -gt $allowedCount) {
        $checkCRequired = $true
        Write-Output ("[FAIL] {0}: {1} forbidden local-path reference(s), frozen baseline allows {2}." -f $normalizedPath, $actualCount, $allowedCount)
        Add-Violation ("Check C: '{0}' has {1} forbidden local-path reference(s); the frozen baseline allows {2} (design contract #52; references may only decrease)." -f $normalizedPath, $actualCount, $allowedCount)
    }
    elseif ($actualCount -lt $allowedCount) {
        $checkCBelowBaseline++
        Write-Output ("[INFO] {0}: {1} reference(s), below frozen baseline {2} (baseline can be tightened)." -f $normalizedPath, $actualCount, $allowedCount)
    }
}

if (-not $checkCRequired) {
    Write-Output ("[OK] All local-path references are within the frozen baseline ({0} file(s) with references, {1} below baseline)." -f $checkCFilesWithReferences, $checkCBelowBaseline)
}

Write-Output ''

# ---------------------------------------------------------------------------
# Check D: design-contract locked test-name existence (local-only input)
# ---------------------------------------------------------------------------

# The design contract list is a gitignored maintainer-local document; CI and
# fresh clones do not have it, so the whole check self-skips when absent.
$ContractListRelativePath = 'docs/design-contracts.md'
$contractListPath = Join-Path -Path $repoRootPath -ChildPath ($ContractListRelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))

# Backtick-quoted tokens that look like test identifiers but are documented
# non-test identifiers. Frozen baseline: entries may only be removed, never
# added. Groups: Win32 messages/flags, WPF template parts, WPF code-behind
# event handlers (production code, not tests), evidence-gating environment
# variables read by tests.
$ContractLockExemptIdentifiers = @(
    'WM_DISPLAYCHANGE'
    'WM_SETTINGCHANGE'
    'WM_GETMINMAXINFO'
    'WM_EXITSIZEMOVE'
    'WM_HOTKEY'
    'SHGFI_ICON'
    'SHGFI_SYSICONINDEX'
    'DWMWA_SYSTEMBACKDROP_TYPE'
    'HWND_NOTOPMOST'
    'CFSTR_PREFERREDDROPEFFECT'
    'Win32_Product'
    'PART_*'
    'PART_ContentHost'
    'WidthThumb_DragDelta'
    'BoardList_PreviewMouseRightButtonUp'
    'BoardList_ButtonClick'
    'Root_PreviewLostKeyboardFocus'
    'CategoryTab_MouseLeftButtonUp'
    'CategoryTab_Drop'
    'CategoryTab_MouseEnter'
    'Root_MouseLeave'
    'ReviewEditor_TextChanged'
    'DailyReviews_Changed'
    'SearchClearFilterButton_Click'
    'SubgroupChip_MouseEnter'
    'FTS_W5_EVIDENCE_DIR'
    'FTS_REVIEW_EMPTY_STATE_EVIDENCE_DIR'
    'FTS_PREVIEW_THEME'
    'FTS_PREVIEW_DATA_DIR'
    'FTS_FONT_EVIDENCE_DIR'
    'FTS_RING_EVIDENCE_DIR'
    'FTS_SUBGROUP_EVIDENCE_DIR'
)

# A token participates in the check when it is PascalCase with one or more
# underscore-separated segments (the repository test naming convention), or a
# wildcard `Prefix_*` whose prefix is PascalCase.
$ContractTestShapePattern = '^[A-Z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+$'
$ContractTestPrefixShapePattern = '^[A-Z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)*$'
$ContractWildcardSuffix = '_*'

Write-Output '--- Check D: design-contract locked test-name existence ---'

if (-not (Test-Path -LiteralPath $contractListPath -PathType Leaf)) {
    Write-Output ("[SKIP] {0} not present (maintainer-local document); nothing to check." -f $ContractListRelativePath)
}
else {
    # Method universe: every test method declared under tests/, regardless of
    # runner attributes (a superset keeps the check free of false positives).
    $testMethodNames = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($trackedFile in $trackedFiles) {
        if ($trackedFile -notmatch '^tests/.*\.cs$') { continue }

        $testContentPath = Join-Path -Path $repoRootPath -ChildPath ($trackedFile.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        $testContent = [System.IO.File]::ReadAllText($testContentPath)
        foreach ($methodMatch in [regex]::Matches($testContent, '(?m)^\s*public\s+(?:async\s+)?(?:Task|void)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(')) {
            [void]$testMethodNames.Add($methodMatch.Groups[1].Value)
        }
    }

    $contractText = Get-Content -LiteralPath $contractListPath -Raw -Encoding UTF8
    $checkedCount = 0
    $missingLockReferences = New-Object System.Collections.Generic.List[string]
    $seenTokens = @{}
    foreach ($tokenMatch in [regex]::Matches($contractText, '`([^`\r\n]+)`')) {
        $token = $tokenMatch.Groups[1].Value.Trim()
        if ($token -eq '' -or $seenTokens.ContainsKey($token)) { continue }
        $seenTokens[$token] = $true

        $isWildcard = $token.EndsWith($ContractWildcardSuffix)
        $referenceName = $token
        if ($isWildcard) {
            $referenceName = $token.Substring(0, $token.Length - $ContractWildcardSuffix.Length)
            if ($referenceName -cnotmatch $ContractTestPrefixShapePattern) { continue }
        }
        elseif ($referenceName -cnotmatch $ContractTestShapePattern) {
            continue
        }

        if ($ContractLockExemptIdentifiers -ccontains $token) { continue }

        $resolved = $false
        if ($isWildcard) {
            foreach ($methodName in $testMethodNames) {
                if ($methodName.StartsWith($referenceName, [System.StringComparison]::Ordinal)) {
                    $resolved = $true
                    break
                }
            }
        }
        else {
            $resolved = $testMethodNames.Contains($referenceName)
        }

        $checkedCount++
        if (-not $resolved) {
            $missingLockReferences.Add($token)
        }
    }

    if ($missingLockReferences.Count -eq 0) {
        Write-Output ("[OK] All {0} distinct locked test identifier(s) resolve to methods under tests/." -f $checkedCount)
    }
    else {
        foreach ($token in $missingLockReferences) {
            Write-Output ("[FAIL] Locked test identifier does not exist under tests/: {0}" -f $token)
            Add-Violation ("Check D: design contract references test identifier '{0}' with no matching method under tests/ (stale lock field or renamed test; align the contract lock wording)." -f $token)
        }
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
