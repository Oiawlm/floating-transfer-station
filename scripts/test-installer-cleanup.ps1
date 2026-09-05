[CmdletBinding()]
param(
    [string]$IsccPath,
    [switch]$KeepArtifacts
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$installerScripts = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'installer') -Filter '*.iss' -File)
if ($installerScripts.Count -ne 1) {
    throw "Expected exactly one production Inno script, found $($installerScripts.Count)."
}
$installerScript = $installerScripts[0].FullName
if (-not $IsccPath) {
    $IsccPath = Join-Path $repoRoot '.tools\inno\ISCC.exe'
}
$IsccPath = (Get-Item -LiteralPath $IsccPath).FullName

function Assert-ChildPath([string]$Path, [string]$Parent) {
    $resolved = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $prefix = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing a path outside its test boundary: $resolved"
    }
    return $resolved
}

function Assert-NoReparsePoints([string]$Path) {
    $candidate = [System.IO.Path]::GetFullPath($Path)
    while ($candidate) {
        if (Test-Path -LiteralPath $candidate) {
            $item = Get-Item -LiteralPath $candidate -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing a test path through a reparse point: $candidate"
            }
        }
        $candidate = Split-Path -Parent $candidate
    }
}

function ConvertTo-PascalString([string]$Value) {
    return "'" + $Value.Replace("'", "''") + "'"
}

# Compile only the production path helpers and cleanup function. No production
# setup/uninstall events, registry operations, or installation sections run.
$source = Get-Content -LiteralPath $installerScript -Raw -Encoding UTF8
$constantSource = @()
$managedLeaves = @{}
foreach ($name in @('ManagedDataParentLeaf', 'ManagedDataLeaf')) {
    $matches = [regex]::Matches($source, "(?m)^[ \t]*$name[ \t]*=[ \t]*'((?:[^'\r\n]|'')*)';[ \t]*\r?$")
    if ($matches.Count -ne 1) {
        throw "Expected exactly one literal $name constant."
    }
    $leaf = $matches[0].Groups[1].Value.Replace("''", "'")
    if ($leaf -in @('', '.', '..') -or $leaf -ne $leaf.Trim() -or
        $leaf.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0) {
        throw "The $name constant is not a safe directory leaf."
    }
    $managedLeaves[$name] = $leaf
    $constantSource += $matches[0].Value.Trim()
}

$functionSource = foreach ($name in @(
    'IsExtendedDevicePath', 'IsFullyQualifiedPath', 'NormalizeDirectory',
    'IsRootDirectory', 'BuildDataDirectory', 'GetManagedDataParent',
    'GetDataParentDirectory', 'IsManagedDataDirectory', 'DeleteManagedDataDirectory'
)) {
    $matches = [regex]::Matches($source, "(?ms)^function[ \t]+$name\b.*?^end;[ \t]*(?=\r?$)")
    if ($matches.Count -ne 1) {
        throw "Expected exactly one complete production $name function."
    }
    $matches[0].Value
}

$testResultsRoot = Assert-ChildPath (Join-Path $repoRoot 'TestResults') $repoRoot
$fixtureId = [guid]::NewGuid().ToString('N')
$fixtureRoot = Assert-ChildPath (Join-Path $testResultsRoot "installer-cleanup-$fixtureId") $testResultsRoot
Assert-NoReparsePoints $fixtureRoot
if (Test-Path -LiteralPath $fixtureRoot) {
    throw "The unique fixture path already exists: $fixtureRoot"
}
[System.IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
Write-Host "Installer cleanup fixture: $fixtureRoot"

$passed = $false
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
try {
    $runtimeTemp = Join-Path $fixtureRoot 'runtime-temp'
    [System.IO.Directory]::CreateDirectory($runtimeTemp) | Out-Null
    $env:TEMP = $runtimeTemp
    $env:TMP = $runtimeTemp
    $parentLeaf = $managedLeaves.ManagedDataParentLeaf
    $dataLeaf = $managedLeaves.ManagedDataLeaf
    $managedSuffix = "$parentLeaf\$dataLeaf"
    $cases = @(
        @{ Name = 'siblings'; RelativePath = "cases\siblings\$managedSuffix"; Expected = 'True' }
        @{ Name = 'empty-parent'; RelativePath = "cases\empty-parent\$managedSuffix"; Expected = 'True' }
        @{ Name = 'normalized'; RelativePath = "cases\normalized\$managedSuffix\..\$dataLeaf\"; Expected = 'True' }
        @{ Name = 'unmanaged-leaf'; RelativePath = "cases\unmanaged-leaf\$parentLeaf\Other"; Expected = 'False' }
        @{ Name = 'unmanaged-parent'; RelativePath = "cases\unmanaged-parent\Other\$dataLeaf"; Expected = 'False' }
        # DelTree reports False for a missing target; do not add a new success
        # contract for absence. The sibling still must survive this call.
        @{ Name = 'missing-data'; RelativePath = "cases\missing-data\$managedSuffix"; Expected = 'False' }
    )
    $preservedFiles = @(
        'fixture.marker'
        "cases\siblings\$parentLeaf\user-notes.txt"
        "cases\siblings\$parentLeaf\app.exe"
        "cases\siblings\$parentLeaf\UnrelatedFiles\keep.txt"
        'cases\siblings\outer-sentinel.txt'
        'cases\empty-parent\outer-sentinel.txt'
        "cases\normalized\$parentLeaf\keep.txt"
        "cases\unmanaged-leaf\$parentLeaf\Other\keep.txt"
        "cases\unmanaged-leaf\$managedSuffix\board.json"
        "cases\unmanaged-parent\Other\$dataLeaf\keep.txt"
        "cases\unmanaged-parent\Other\app.exe"
        "cases\missing-data\$parentLeaf\keep.txt"
    )
    $managedFiles = @(
        "cases\siblings\$managedSuffix\board.json"
        "cases\siblings\$managedSuffix\Images\image.bin"
        "cases\empty-parent\$managedSuffix\Images\image.bin"
        "cases\normalized\$managedSuffix\board.json"
    )
    foreach ($relativePath in ($preservedFiles + $managedFiles)) {
        $path = Assert-ChildPath (Join-Path $fixtureRoot $relativePath) $fixtureRoot
        [System.IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null
        [System.IO.File]::WriteAllText($path, "synthetic:$fixtureId`n$relativePath")
    }

    $caseCalls = foreach ($case in $cases) {
        # Check both the requested path and the old implementation's parent
        # deletion target before emitting any executable test calls.
        $inputPath = Join-Path $fixtureRoot $case.RelativePath
        $resolvedPath = Assert-ChildPath $inputPath $fixtureRoot
        Assert-ChildPath (Split-Path -Parent $resolvedPath) $fixtureRoot | Out-Null
        '    RunCleanupCase(' + (ConvertTo-PascalString $case.Name) + ', ' +
            (ConvertTo-PascalString $inputPath) + ');'
    }
    $receiptPath = Join-Path $fixtureRoot 'native-results.txt'
    $fixtureScript = Join-Path $fixtureRoot 'cleanup-behavior.iss'
    $harness = @"
[Setup]
AppId=FloatingTransferStation.CleanupTest.$fixtureId
AppName=Installer cleanup behavior test
AppVersion=1.0
PrivilegesRequired=lowest
CreateAppDir=no
Uninstallable=no
CreateUninstallRegKey=no
UsePreviousAppDir=no
DisableProgramGroupPage=yes
CloseApplications=no
RestartApplications=no
UseSetupLdr=no
OutputDir=.
OutputBaseFilename=cleanup-behavior
Compression=none

[Code]
const
  FixtureRoot = $(ConvertTo-PascalString $fixtureRoot);
  ReceiptPath = $(ConvertTo-PascalString $receiptPath);
  $($constantSource -join "`n  ")

$($functionSource -join "`n`n")

procedure RequireFixtureTarget(const DataDirectory: String);
var
  Normalized: String;
  ParentDirectory: String;
  Prefix: String;
begin
  Normalized := RemoveBackslashUnlessRoot(ExpandFileName(Trim(DataDirectory)));
  ParentDirectory := RemoveBackslashUnlessRoot(ExtractFilePath(Normalized));
  Prefix := AddBackslash(FixtureRoot);
  if (CompareText(Copy(Normalized, 1, Length(Prefix)), Prefix) <> 0) or
    (CompareText(Copy(ParentDirectory, 1, Length(Prefix)), Prefix) <> 0) then
    RaiseException('Cleanup target or its parent escaped the fixture.');
end;

procedure RunCleanupCase(const Name, DataDirectory: String);
var
  Outcome: String;
begin
  RequireFixtureTarget(DataDirectory);
  if DeleteManagedDataDirectory(DataDirectory) then
    Outcome := 'True'
  else
    Outcome := 'False';
  if not SaveStringToFile(ReceiptPath, Name + '=' + Outcome + #13#10, True) then
    RaiseException('Cannot write the native test result.');
end;

function InitializeSetup: Boolean;
begin
  Result := False;
  try
    if not FileExists(AddBackslash(FixtureRoot) + 'fixture.marker') then
      RaiseException('The synthetic fixture marker is missing.');
$($caseCalls -join "`n")
    SaveStringToFile(ReceiptPath, 'COMPLETED' + #13#10, True);
  except
    SaveStringToFile(ReceiptPath, 'ERROR=' + GetExceptionMessage + #13#10, True);
  end;
end;
"@
    [System.IO.File]::WriteAllText($fixtureScript, $harness, [System.Text.UTF8Encoding]::new($true))
    (Get-FileHash -LiteralPath $installerScript -Algorithm SHA256).Hash |
        Set-Content -LiteralPath (Join-Path $fixtureRoot 'production-source.sha256')

    $compiler = Start-Process -FilePath $IsccPath -ArgumentList @('/Q', ('"' + $fixtureScript + '"')) `
        -WorkingDirectory $fixtureRoot -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $fixtureRoot 'compiler.log') `
        -RedirectStandardError (Join-Path $fixtureRoot 'compiler-errors.log')
    if (-not $compiler.WaitForExit(30000)) {
        $compiler.Kill()
        throw 'The isolated Inno test compilation timed out.'
    }
    if ($compiler.ExitCode -ne 0) {
        throw "The isolated Inno test failed to compile (exit $($compiler.ExitCode)); see compiler-errors.log."
    }

    $testExecutable = Assert-ChildPath (Join-Path $fixtureRoot 'cleanup-behavior.exe') $fixtureRoot
    $process = Start-Process -FilePath $testExecutable `
        -ArgumentList @('/SP-', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
            ('/LOG="' + (Join-Path $fixtureRoot 'native.log') + '"')) `
        -WorkingDirectory $fixtureRoot -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(30000)) {
        $process.Kill()
        throw 'The isolated Inno cleanup test timed out.'
    }
    # InitializeSetup deliberately returns False, so exit 1 is expected. The
    # receipt plus independent filesystem assertions decide whether tests pass.
    if ($process.ExitCode -ne 1) {
        throw "Expected initialization-only exit 1, got $($process.ExitCode)."
    }
    $receipt = @(Get-Content -LiteralPath $receiptPath)
    $assertions = [System.Collections.Generic.List[string]]::new()
    $failures = [System.Collections.Generic.List[string]]::new()
    function Record-Assertion([bool]$Condition, [string]$Description) {
        if ($Condition) {
            $assertions.Add("PASS $Description")
        }
        else {
            $assertions.Add("FAIL $Description")
            $failures.Add($Description)
        }
    }
    Record-Assertion ($receipt.Count -eq ($cases.Count + 1) -and $receipt[-1] -eq 'COMPLETED') `
        'all native cleanup cases completed'
    foreach ($case in $cases) {
        Record-Assertion ($receipt -contains "$($case.Name)=$($case.Expected)") `
            "$($case.Name): cleanup returned $($case.Expected)"
    }
    foreach ($relativePath in $preservedFiles) {
        $path = Join-Path $fixtureRoot $relativePath
        $preserved = [System.IO.File]::Exists($path) -and
            [System.IO.File]::ReadAllText($path) -ceq "synthetic:$fixtureId`n$relativePath"
        Record-Assertion $preserved "preserved unchanged: $relativePath"
    }
    foreach ($relativePath in @(
        "cases\siblings\$managedSuffix", "cases\empty-parent\$parentLeaf",
        "cases\normalized\$managedSuffix", "cases\missing-data\$managedSuffix"
    )) {
        Record-Assertion (-not (Test-Path -LiteralPath (Join-Path $fixtureRoot $relativePath))) `
            "removed or absent: $relativePath"
    }
    $assertions | Set-Content -LiteralPath (Join-Path $fixtureRoot 'assertions.txt') -Encoding UTF8
    if ($failures.Count -ne 0) {
        throw "Installer cleanup regression failed ($($failures.Count) assertions):`n$($failures -join "`n")"
    }
    $passed = $true
    Write-Host "Installer cleanup behavior passed: $($cases.Count) native cases, $($assertions.Count) assertions."
}
finally {
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    if ($passed -and -not $KeepArtifacts) {
        $cleanupPath = Assert-ChildPath $fixtureRoot $testResultsRoot
        if ((Split-Path -Leaf $cleanupPath) -ne "installer-cleanup-$fixtureId") {
            throw "Refusing to remove an unexpected fixture: $cleanupPath"
        }
        Assert-NoReparsePoints $cleanupPath
        foreach ($item in Get-ChildItem -LiteralPath $cleanupPath -Recurse -Force) {
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to remove a fixture containing a reparse point: $($item.FullName)"
            }
        }
        Remove-Item -LiteralPath $cleanupPath -Recurse -Force
    }
    else {
        Write-Host "Installer cleanup evidence retained: $fixtureRoot"
    }
}
