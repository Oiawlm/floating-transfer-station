<#
.SYNOPSIS
    Repository hygiene checks for floating-transfer-station.

.DESCRIPTION
    Eight checks, all must pass (exit 0) or the script fails (exit 1):

      A. Root entry whitelist + scripts naming whitelist
         Every root-level entry tracked by git must be listed in
         $AllowedRootEntries below. Extra or missing entries are reported.
         Additionally every tracked scripts/*.ps1 must match the verb-prefix
         whitelist $AllowedScriptNamePattern (bootstrap-/build-/check-/
         clear-/run-/test-); the prefix table is hardcoded here on purpose
         so adding a prefix requires a reviewed change (R11 file charter,
         design contract #64).

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

      E. Untracked stray files (merge residue / editor backups)
         Untracked entries reported by git status whose base name ends in
         .orig/.rej/.bak/.tmp/.swp or starts with '~' fail the check.
         Gitignored directories never appear in git status output, so
         maintainer-local evidence files are not affected; a clean CI
         checkout has no untracked entries at all.

      F. Handoff root whitelist (local-only input)
         When the maintainer-local handoff directory (docs/handoff,
         gitignored, absent in CI) exists, its root may only contain the
         README, the archive directory, batch directories named
         <yyyy-mm-dd>-<slug>, the frozen anchor files referenced by tracked
         contracts, and the explicit legacy exceptions in
         $HandoffRootLegacyExceptions (may only shrink).

      G. Handoff batch index freshness (local-only input)
         Batch directories under docs/handoff that contain execution reports
         (report.md, or the multi-report form report-*.md) must be listed in
         the handoff README index within 14 days of the batch start date
         (parsed from the directory name; two legacy directories are
         exempt). Prevents the index from lagging behind again.

      H. Research root naming (local-only input)
         When the maintainer-local research directory (docs/research,
         gitignored, absent in CI) exists, root-level entries must follow
         the new-style naming <yyyy-mm-dd>-<slug>.<md|csv|ps1> or be listed
         in the frozen legacy exception table $ResearchLegacyExceptions
         (18 legacy items, may only shrink).

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

Write-Output '--- Check A: root entry whitelist + scripts naming whitelist ---'

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

# Verb-prefix whitelist for tracked scripts (R11 file charter, design
# contract #64). The prefix table is hardcoded here on purpose: a new prefix
# means editing this pattern in a reviewed change.
$AllowedScriptNamePattern = '^(bootstrap-|build-|check-|clear-|run-|test-)[a-z0-9-]+\.ps1$'

$scriptFiles = @(
    $trackedFiles |
        ForEach-Object { $_.Replace('\', '/') } |
        Where-Object { $_ -cmatch '^scripts/[^/]+\.ps1$' } |
        Sort-Object
)
$badScriptNames = @($scriptFiles | Where-Object { (Split-Path -Leaf $_) -cnotmatch $AllowedScriptNamePattern })

if ($badScriptNames.Count -eq 0) {
    Write-Output ("[OK] All {0} tracked scripts/*.ps1 match the prefix whitelist." -f $scriptFiles.Count)
}
else {
    foreach ($scriptFile in $badScriptNames) {
        Write-Output ("[FAIL] Script name outside the prefix whitelist (expected bootstrap-/build-/check-/clear-/run-/test- prefix): {0}" -f $scriptFile)
        Add-Violation ("Check A: script '{0}' does not match the scripts naming whitelist '{1}'." -f $scriptFile, $AllowedScriptNamePattern)
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
# Check E: untracked stray files (merge residue / editor backups)
# ---------------------------------------------------------------------------

# Untracked files that look like merge residue or editor backups. Gitignored
# directories never appear in git status output, so maintainer-local evidence
# files (e.g. handoff *.bak) are not affected (R11 file charter, design
# contract #64).
$UntrackedStrayExtensionPattern = '\.(orig|rej|bak|tmp|swp)$'

Write-Output '--- Check E: untracked stray files ---'

$statusOutput = & git -C $repoRootPath -c core.quotepath=false status --porcelain
if ($LASTEXITCODE -ne 0) {
    throw "git status failed with exit code $LASTEXITCODE."
}

$untrackedStrays = New-Object System.Collections.Generic.List[string]
foreach ($line in @($statusOutput)) {
    $text = [string]$line
    if ($text.Length -lt 4) { continue }
    if (-not $text.StartsWith('??')) { continue }

    $statusPath = $text.Substring(3).Trim()
    if ($statusPath.EndsWith('/')) { $statusPath = $statusPath.TrimEnd('/') }
    $baseName = Split-Path -Leaf $statusPath
    if ([string]::IsNullOrWhiteSpace($baseName)) { $baseName = $statusPath }

    if ($baseName -cmatch $UntrackedStrayExtensionPattern -or $baseName.StartsWith('~')) {
        $untrackedStrays.Add($statusPath)
    }
}

if ($untrackedStrays.Count -eq 0) {
    Write-Output '[OK] No untracked stray files (*.orig|*.rej|*.bak|*.tmp|*.swp|~*).'
}
else {
    foreach ($strayPath in $untrackedStrays) {
        Write-Output ("[FAIL] Untracked stray file (merge residue or editor backup): {0}" -f $strayPath)
        Add-Violation ("Check E: untracked stray file '{0}' matches the residue pattern (*.orig|*.rej|*.bak|*.tmp|*.swp|~*); delete it or gitignore its location." -f $strayPath)
    }
}

Write-Output ''

# ---------------------------------------------------------------------------
# Check F: handoff root whitelist (local-only input)
# ---------------------------------------------------------------------------

# The handoff directory is a gitignored maintainer-local area; CI and fresh
# clones do not have it, so the check self-skips when absent (same pattern as
# Check D). Root entries allowed: the README, the archive directory, batch
# directories named <yyyy-mm-dd>-<slug>, the frozen anchor files referenced
# by tracked contracts, and explicit legacy exceptions (may only shrink).
$HandoffRelativePath = 'docs/handoff'
$handoffRootPath = Join-Path -Path $repoRootPath -ChildPath ($HandoffRelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))

$HandoffRootAllowedFiles = @(
    'README.md'
    'RESEARCH-CATEGORYORDER-DROPFLICKER-20261014.md'
    'RESEARCH-LAUNCHER-ICON-FAVICON-20261020.md'
    'RESEARCH-LAUNCHER-UI-UNIFY-20261018.md'
    'RESEARCH-QUICKLAUNCH-URL-20261017.md'
)
$HandoffRootLegacyExceptions = @(
    'launcher-batch-20261023'
    '2026-10-24-batch10'
)
$HandoffBatchDirPattern = '^\d{4}-\d{2}-\d{2}-[a-z0-9-]+$'

Write-Output '--- Check F: handoff root whitelist ---'

if (-not (Test-Path -LiteralPath $handoffRootPath -PathType Container)) {
    Write-Output ("[SKIP] {0} not present (maintainer-local directory); nothing to check." -f $HandoffRelativePath)
}
else {
    $handoffViolations = 0
    foreach ($entry in (Get-ChildItem -LiteralPath $handoffRootPath | Sort-Object Name)) {
        if ($entry.PSIsContainer) {
            if ($entry.Name -ceq 'archive') { continue }
            if ($entry.Name -cin $HandoffRootLegacyExceptions) { continue }
            if ($entry.Name -cmatch $HandoffBatchDirPattern) { continue }
        }
        else {
            if ($HandoffRootAllowedFiles -ccontains $entry.Name) { continue }
        }

        Write-Output ("[FAIL] Unexpected entry at the handoff root (allowed: README.md, archive/, <yyyy-mm-dd>-<slug>/ batch dirs, frozen anchors, legacy exceptions): {0}" -f $entry.Name)
        Add-Violation ("Check F: unexpected handoff root entry '{0}/{1}' (stray file or non-conforming directory)." -f $HandoffRelativePath, $entry.Name)
        $handoffViolations++
    }

    if ($handoffViolations -eq 0) {
        Write-Output '[OK] Handoff root entries match the whitelist.'
    }
}

Write-Output ''

# ---------------------------------------------------------------------------
# Check G: handoff batch index freshness (local-only input)
# ---------------------------------------------------------------------------

# Batch directories containing execution reports (report.md, or the
# multi-report form report-*.md per the handoff README convention) must be
# listed in the handoff README index within 14 days of the batch start date
# (parsed from the directory name prefix, compared date-to-date so the
# result does not depend on the time of day). Two legacy directories are
# exempt (they predate the index). Prevents the index from lagging behind
# again (R11 file charter, design contract #64).
$HandoffIndexGraceDays = 14
$HandoffIndexExemptDirs = @(
    'launcher-batch-20261023'
    '2026-10-24-batch10'
)

Write-Output '--- Check G: handoff batch index freshness ---'

if (-not (Test-Path -LiteralPath $handoffRootPath -PathType Container)) {
    Write-Output ("[SKIP] {0} not present (maintainer-local directory); nothing to check." -f $HandoffRelativePath)
}
else {
    $handoffReadmePath = Join-Path -Path $handoffRootPath -ChildPath 'README.md'
    if (-not (Test-Path -LiteralPath $handoffReadmePath -PathType Leaf)) {
        Write-Output '[FAIL] docs/handoff/README.md not found (the batch index).'
        Add-Violation 'Check G: docs/handoff/README.md is missing (batch index not readable).'
    }
    else {
        $indexText = Get-Content -LiteralPath $handoffReadmePath -Raw -Encoding UTF8
        if ($null -eq $indexText) { $indexText = '' }

        $indexViolations = 0
        $checkedBatches = 0
        foreach ($entry in (Get-ChildItem -LiteralPath $handoffRootPath -Directory | Sort-Object Name)) {
            if ($entry.Name -cin $HandoffIndexExemptDirs) { continue }
            if ($entry.Name -cnotmatch $HandoffBatchDirPattern) { continue }

            $reportFiles = @(
                Get-ChildItem -LiteralPath $entry.FullName -File |
                    Where-Object { $_.Name -ceq 'report.md' -or $_.Name -clike 'report-*.md' }
            )
            if ($reportFiles.Count -eq 0) { continue }

            $batchDate = New-Object System.DateTime
            if (-not [System.DateTime]::TryParseExact($entry.Name.Substring(0, 10), 'yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::None, [ref]$batchDate)) {
                Write-Output ("[FAIL] Batch directory date prefix is not a valid date: {0}" -f $entry.Name)
                Add-Violation ("Check G: batch directory '{0}/{1}' has an unparseable date prefix." -f $HandoffRelativePath, $entry.Name)
                $indexViolations++
                continue
            }

            $ageDays = ((Get-Date).Date - $batchDate).TotalDays
            if ($ageDays -le $HandoffIndexGraceDays) { continue }

            $checkedBatches++
            if (-not $indexText.Contains($entry.Name)) {
                Write-Output ("[FAIL] Batch directory with execution report(s) older than {0} days is not listed in the handoff README index: {1}" -f $HandoffIndexGraceDays, $entry.Name)
                Add-Violation ("Check G: batch directory '{0}/{1}' contains an execution report (report.md / report-*.md), is older than {2} days, and is missing from the README batch index." -f $HandoffRelativePath, $entry.Name, $HandoffIndexGraceDays)
                $indexViolations++
            }
        }

        if ($indexViolations -eq 0) {
            Write-Output ("[OK] All {0} indexed-eligible batch directory(s) past the {1}-day grace period are listed in the index." -f $checkedBatches, $HandoffIndexGraceDays)
        }
    }
}

Write-Output ''

# ---------------------------------------------------------------------------
# Check H: research root naming (local-only input)
# ---------------------------------------------------------------------------

# Root-level entries of the gitignored research directory must follow the
# new-style naming <yyyy-mm-dd>-<slug>.<md|csv|ps1> or be listed in the
# frozen legacy exception table below (18 legacy items; may only shrink).
$ResearchRelativePath = 'docs/research'
$researchRootPath = Join-Path -Path $repoRootPath -ChildPath ($ResearchRelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))

$ResearchNewStylePattern = '^\d{4}-\d{2}-\d{2}-[a-z0-9-]+\.(md|csv|ps1)$'
$ResearchLegacyExceptions = @(
    'RESEARCH-AFFECTED-COVERAGE-20261027.md'
    'RESEARCH-AFFECTED-FULL-AUDIT-20261027.md'
    'RESEARCH-AFFECTED-NARROW-20261027.md'
    'RESEARCH-AFFECTED-SPEC-20261027.md'
    'RESEARCH-AI-TEST-CONNECTION-20261014.md'
    'RESEARCH-ARCH-REVIEW-20261028.md'
    'RESEARCH-LAUNCHER-UX-PERF-20261016.md'
    'RESEARCH-PANEL-EXPAND-PERF-20261013.md'
    'RESEARCH-QUICKLAUNCH-20261014.md'
    'DATA-AFFECTED-COVERAGE-MATRIX-20261027.csv'
    'DATA-AFFECTED-COVERAGE-PROBE-20261027.ps1'
    'DATA-AFFECTED-REPLAY-20261027.ps1'
    'loop-2026-09.md'
    'loop-2026-10.md'
    'loop-2026-11.md'
    'quicklaunch-brief-20261014.md'
    'quicklaunch-proposal-zcode-20261014.md'
    '_launcher-ux-perf-evidence'
)

Write-Output '--- Check H: research root naming ---'

if (-not (Test-Path -LiteralPath $researchRootPath -PathType Container)) {
    Write-Output ("[SKIP] {0} not present (maintainer-local directory); nothing to check." -f $ResearchRelativePath)
}
else {
    $researchViolations = 0
    foreach ($entry in (Get-ChildItem -LiteralPath $researchRootPath | Sort-Object Name)) {
        if ($entry.Name -cmatch $ResearchNewStylePattern) { continue }
        if ($ResearchLegacyExceptions -ccontains $entry.Name) { continue }

        Write-Output ("[FAIL] Research root entry outside the new-style naming <yyyy-mm-dd>-<slug>.(md|csv|ps1) and not in the legacy exception table: {0}" -f $entry.Name)
        Add-Violation ("Check H: research root entry '{0}/{1}' violates the naming whitelist and is not a frozen legacy exception." -f $ResearchRelativePath, $entry.Name)
        $researchViolations++
    }

    if ($researchViolations -eq 0) {
        Write-Output '[OK] Research root entries follow the new-style naming or the frozen legacy exception table.'
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
