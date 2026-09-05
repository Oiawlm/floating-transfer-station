[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# The production installer writes HKCU and can stop the product process.
# Never run it on a developer machine or a persistent self-hosted runner.
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or
    $env:GITHUB_ACTIONS -cne 'true' -or
    $env:RUNNER_ENVIRONMENT -cne 'github-hosted') {
    throw 'Installed lifecycle verification requires a disposable GitHub-hosted Windows runner.'
}
if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP) -or
    -not [IO.Path]::IsPathFullyQualified($env:RUNNER_TEMP)) {
    throw 'RUNNER_TEMP must identify an absolute temporary directory.'
}
if (((Get-Item -LiteralPath $env:RUNNER_TEMP -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'RUNNER_TEMP must not be a directory link.'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$productName = '悬浮中转站'
$settingsKey = 'HKCU:\Software\FloatingTransferStation'
$startupKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{9F0E0B0F-4E4F-47C2-9E63-56847E509D50}_is1'
$legacyData = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "$productName\Data"
$startupProperties = Get-ItemProperty -LiteralPath $startupKey -ErrorAction SilentlyContinue
if ((Test-Path -LiteralPath $settingsKey) -or
    (Test-Path -LiteralPath $uninstallKey) -or
    (Test-Path -LiteralPath $legacyData) -or
    ($null -ne $startupProperties -and $startupProperties.PSObject.Properties.Name -contains $productName) -or
    @(Get-Process -Name $productName -ErrorAction SilentlyContinue).Count -ne 0) {
    throw 'The disposable runner already contains product state; refusing to overwrite it.'
}
$productMutex = $null
if ([Threading.Mutex]::TryOpenExisting('Local\FloatingTransferStation.App', [ref]$productMutex)) {
    $productMutex.Dispose()
    throw 'The product mutex already exists; refusing to run the installer.'
}

$installers = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'artifacts\installer') -Filter '*-Setup-*.exe' -File)
if ($installers.Count -ne 1) { throw 'Expected exactly one freshly built installer.' }
$installer = $installers[0]
$publishedExecutable = Join-Path $repoRoot "artifacts\publish\$productName.exe"
$expectedExecutableHash = (Get-FileHash -LiteralPath $publishedExecutable -Algorithm SHA256).Hash
$expectedVersion = $installer.VersionInfo.FileVersion.Trim()
$fixtureRoot = Join-Path ([IO.Path]::GetFullPath($env:RUNNER_TEMP)) ('fts-installed-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $fixtureRoot) { throw 'The unique fixture directory already exists.' }
$dataParent = Join-Path $fixtureRoot 'Custom content'
$installedDirectory = Join-Path $dataParent $productName
$dataDirectory = Join-Path $installedDirectory 'Data'
$installedExecutable = Join-Path $installedDirectory "$productName.exe"
$evidenceDirectory = Join-Path $repoRoot 'TestResults\installed-lifecycle'
New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $dataDirectory 'images') -Force | Out-Null

$assertions = [Collections.Generic.List[string]]::new()
function Assert-Condition([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
    $assertions.Add($message)
}

function Invoke-SetupProcess([string]$executable, [string]$arguments, [string]$stage) {
    $process = Start-Process -FilePath $executable -ArgumentList $arguments -WindowStyle Hidden -PassThru
    try {
        if (-not $process.WaitForExit(60000)) {
            $process.Kill($true)
            throw "$stage timed out; see the installer log."
        }
        Assert-Condition ($process.ExitCode -eq 0) "$stage must exit successfully (actual: $($process.ExitCode))."
    } finally {
        $process.Dispose()
    }
}

function Assert-FileHash([string]$path, [string]$expectedHash) {
    Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "File must remain: $path"
    Assert-Condition ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $expectedHash) "File contents must remain: $path"
}

function Assert-InstalledState {
    Assert-FileHash $installedExecutable $expectedExecutableHash
    $registration = Get-ItemProperty -LiteralPath $settingsKey
    Assert-Condition ($registration.DataDirectory -eq $dataDirectory) 'DataDirectory must point to the synthetic registered data.'
    Assert-Condition ($registration.DataParentDirectory -eq $dataParent) 'DataParentDirectory must match the synthetic parent.'
    $startup = Get-ItemPropertyValue -LiteralPath $startupKey -Name $productName
    Assert-Condition ($startup -ceq ('"' + $installedExecutable + '"')) 'Startup must quote the installed executable exactly.'
    $uninstall = Get-ItemProperty -LiteralPath $uninstallKey
    Assert-Condition ($uninstall.DisplayVersion -eq $expectedVersion) 'Windows uninstall metadata must match the installer version.'
    Assert-Condition (@(Get-Process -Name $productName -ErrorAction SilentlyContinue).Count -eq 0) 'Silent setup must not start the application.'
    foreach ($entry in $dataHashes.GetEnumerator()) { Assert-FileHash $entry.Key $entry.Value }
    foreach ($entry in $sentinelHashes.GetEnumerator()) { Assert-FileHash $entry.Key $entry.Value }
}

$boardPath = Join-Path $dataDirectory 'board.json'
$imagePath = Join-Path $dataDirectory 'images\synthetic.png'
[IO.File]::WriteAllText($boardPath, '{"schemaVersion":1,"items":[]}')
[IO.File]::WriteAllBytes($imagePath, [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aQ1sAAAAASUVORK5CYII='))
$dataHashes = @{}
foreach ($path in @($boardPath, $imagePath)) {
    $dataHashes[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}
$sentinelHashes = @{}
foreach ($path in @(
    (Join-Path $dataParent 'outer-sentinel.txt'),
    (Join-Path $installedDirectory 'user-notes.txt'),
    (Join-Path $installedDirectory 'unrelated-app.exe'),
    (Join-Path $installedDirectory 'Unrelated files\keep.txt')
)) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    [IO.File]::WriteAllText($path, 'Synthetic unrelated user file; must survive update and uninstall.')
    $sentinelHashes[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}

# Model an existing registered data location without installing another product version.
# Same-location preparation accepts this directory; no real user data is involved.
New-Item -Path $settingsKey -Force | Out-Null
New-ItemProperty -LiteralPath $settingsKey -Name DataDirectory -Value $dataDirectory -PropertyType String | Out-Null
New-ItemProperty -LiteralPath $settingsKey -Name DataParentDirectory -Value $dataParent -PropertyType String | Out-Null

$report = [ordered]@{
    Status = 'running'
    FixtureRoot = $fixtureRoot
    InstallerVersion = $expectedVersion
    InstallerSHA256 = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash
    VerifiedStages = @()
    Assertions = $assertions
    Scope = 'Real install, same-version same-directory reinstall, and uninstall with synthetic registered data; cross-directory migration cleanup is verified separately by native cleanup and source contracts.'
}
try {
    foreach ($stage in @('install', 'update')) {
        $logPath = Join-Path $evidenceDirectory "$stage.log"
        $arguments = '/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR="' + $installedDirectory + '" /LOG="' + $logPath + '"'
        Invoke-SetupProcess $installer.FullName $arguments $stage
        Assert-InstalledState
        $report.VerifiedStages += $stage
    }

    $uninstallers = @(Get-ChildItem -LiteralPath $installedDirectory -Filter 'unins*.exe' -File)
    Assert-Condition ($uninstallers.Count -eq 1) 'Exactly one product uninstaller must be installed.'
    $logPath = Join-Path $evidenceDirectory 'uninstall.log'
    Invoke-SetupProcess $uninstallers[0].FullName ('/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="' + $logPath + '"') 'uninstall'
    Assert-Condition (-not (Test-Path -LiteralPath $dataDirectory)) 'Uninstall must remove the registered Data directory.'
    Assert-Condition (-not (Test-Path -LiteralPath $installedExecutable)) 'Uninstall must remove the installed product executable.'
    Assert-Condition (-not (Test-Path -LiteralPath $uninstallKey)) 'Uninstall must remove its Windows registration.'
    $remainingSettings = Get-ItemProperty -LiteralPath $settingsKey -ErrorAction SilentlyContinue
    foreach ($name in @('DataDirectory', 'DataParentDirectory')) {
        Assert-Condition ($null -eq $remainingSettings -or $remainingSettings.PSObject.Properties.Name -notcontains $name) "Uninstall must remove $name registration."
    }
    $remainingStartup = Get-ItemProperty -LiteralPath $startupKey -ErrorAction SilentlyContinue
    Assert-Condition ($null -eq $remainingStartup -or $remainingStartup.PSObject.Properties.Name -notcontains $productName) 'Uninstall must remove startup registration.'
    foreach ($entry in $sentinelHashes.GetEnumerator()) { Assert-FileHash $entry.Key $entry.Value }
    $report.VerifiedStages += 'uninstall'
    $report.Status = 'passed'
    Write-Output "Installed lifecycle passed: 3 stages, $($assertions.Count) assertions."
} catch {
    $report.Status = 'failed'
    $report.Error = $_.Exception.Message
    throw
} finally {
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidenceDirectory 'result.json') -Encoding utf8
    # Preserve synthetic fixtures and logs for diagnosis; the hosted VM is disposable.
}
