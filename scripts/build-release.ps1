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
$version = (Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $repoRoot 'version.txt')).Trim()
if ($version -notmatch '\A[0-9]+\.[0-9]+\.[0-9]+\z') {
    throw 'version.txt must contain exactly one numeric major.minor.patch version.'
}

if ($ForRelease) {
    & (Join-Path $PSScriptRoot 'test-release-readiness.ps1') -RequireVersionAhead -RepoRoot $repoRoot | Out-Host
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
& (Join-Path $PSScriptRoot 'test-bootstrap-tool-versions.ps1') -DotnetPath $dotnet -IsccPath $iscc | Out-Host
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
# 发行资产命名单一权威（契约 #26 修订①）：产物出生即更新器精确名（UpdateService.
# InstallerAssetNamePrefix 派生），消灭发行侧人工改名环节；版本段由 version.txt 唯一赋值。
$outputBaseFilenamePattern = '(?m)^OutputBaseFilename=FloatingTransferStation-Setup-\{#MyAppVersion\}\s*$'
if ($innoText -notmatch $outputBaseFilenamePattern) {
    throw 'Installer contract failed: OutputBaseFilename must be exactly FloatingTransferStation-Setup-{#MyAppVersion} (single-source release asset naming).'
}
$recursiveDeleteMatches = [regex]::Matches(
    $innoText,
    '(?im)^[ \t]*Type:[ \t]*filesandordirs;[ \t]*Name:[ \t]*"([^"\r\n]+)"[ \t]*\r?$')
if ($recursiveDeleteMatches.Count -ne 0) {
    throw 'Installer contract failed: static recursive uninstall deletion is not allowed.'
}
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

# 产物断言＝更新器精确名（前缀与 UpdateService.InstallerAssetNamePrefix 同源；版本段与
# version.txt 同源）：名字错即构建失败，发行侧不再存在「改名后对齐」的人工环节。
$expectedSetupName = "FloatingTransferStation-Setup-$version.exe"
$setups = @(Get-ChildItem -LiteralPath $installerDirectory -Filter '*.exe' -File)
$actualSetupNames = @($setups | ForEach-Object { $_.Name })
if ($setups.Count -ne 1 -or $actualSetupNames[0] -cne $expectedSetupName) {
    throw "Expected exactly one release installer named '$expectedSetupName' (updater exact-name asset contract), found $($actualSetupNames.Count): $($actualSetupNames -join ', ')."
}
$setup = $setups[0].FullName

# SHA256SUMS 清单从实际产物派生（哈希与条目名都取自实际文件，零硬编码）；sha256sum 文本
# 格式＝<64 位小写十六进制><空格><*><文件名>＋结尾换行，与历史发行实物及更新器
# ParseChecksumLine 解析同构。清单与安装包同落 artifacts\installer\，随 CI 附件直传。
$manifestPath = Join-Path $installerDirectory 'SHA256SUMS.txt'
$setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
$expectedManifestLine = "$setupHash *$expectedSetupName"
Set-Content -LiteralPath $manifestPath -Value $expectedManifestLine -Encoding ascii
# 回读断言：清单逐字对齐实际产物的哈希与文件名（防写入漂移）。
$manifestText = Get-Content -Raw -LiteralPath $manifestPath
if ($manifestText.TrimEnd("`r", "`n") -cne $expectedManifestLine) {
    throw "SHA256SUMS manifest verification failed: expected '$expectedManifestLine', found '$($manifestText.TrimEnd("`r", "`n"))'."
}

Write-Host "Release installer: $setup"
Write-Host "Checksums manifest: $manifestPath"
