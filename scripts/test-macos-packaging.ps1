[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $repoRoot 'installer/macos/package-functions.ps1')
$fixtureRoot = Join-Path $repoRoot ('TestResults/macos-packaging-' + [guid]::NewGuid().ToString('N'))
$bundle = Join-Path $fixtureRoot 'FloatingTransferStation.app'
$zipPath = Join-Path $fixtureRoot 'candidate.zip'
try {
    $version = (Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'version.txt')).Trim()
    Initialize-MacAppBundle $bundle $version
    $executable = Join-Path $bundle 'Contents/MacOS/FloatingTransferStation.Mac'
    [System.IO.File]::WriteAllBytes($executable, [byte[]]@(0xCF, 0xFA, 0xED, 0xFE, 0, 0, 0, 0))
    $looseDll = Join-Path $bundle 'Contents/MacOS/System.Diagnostics.Contracts.dll'
    [System.IO.File]::WriteAllText($looseDll, 'MZ synthetic managed PE file')
    $looseDllRejected = $false
    try { Get-MacBundleSigningPlan $bundle | Out-Null } catch {
        $looseDllRejected = $_.Exception.Message -like '*only regular Mach-O files*System.Diagnostics.Contracts.dll*'
    }
    if (-not $looseDllRejected) { throw 'The bundle accepted a loose managed DLL in the Apple code directory.' }
    Remove-Item -LiteralPath $looseDll
    $nativeLibrary = Join-Path $bundle 'Contents/MacOS/libAvaloniaNative.dylib'
    $helperExecutable = Join-Path $bundle 'Contents/MacOS/createdump'
    [System.IO.File]::WriteAllBytes($nativeLibrary, [byte[]]@(0xCF, 0xFA, 0xED, 0xFE, 0, 0, 0, 0))
    [System.IO.File]::WriteAllBytes($helperExecutable, [byte[]]@(0xCF, 0xFA, 0xED, 0xFE, 0, 0, 0, 0))
    $signingPlan = @(Get-MacBundleSigningPlan $bundle)
    if ($signingPlan.Count -ne 4 -or $signingPlan[2].Path -ne $executable -or $signingPlan[3].Path -ne $bundle -or
        $signingPlan[0].JitEntitlements -or $signingPlan[1].JitEntitlements -or
        -not $signingPlan[2].JitEntitlements -or -not $signingPlan[3].JitEntitlements) {
        throw 'Nested Mach-O files must be signed before the main executable and bundle; only the app receives JIT entitlements.'
    }
    [xml]$entitlements = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $repoRoot 'installer/macos/Entitlements.plist')
    if ($entitlements.SelectNodes('/plist/dict/key').Count -ne 1 -or
        $entitlements.SelectSingleNode('/plist/dict/key').InnerText -ne 'com.apple.security.cs.allow-jit' -or
        $entitlements.SelectSingleNode('/plist/dict/key/following-sibling::*[1]').Name -ne 'true') {
        throw 'Ad-hoc candidates require only the explicit JIT entitlement.'
    }
    $resourceName = ([string][char]0x4E2D) + ([char]0x6587) + ' fixture.txt'
    [System.IO.File]::WriteAllText((Join-Path $bundle ('Contents/Resources/' + $resourceName)), 'synthetic resource')
    [xml]$plist = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $bundle 'Contents/Info.plist')
    foreach ($key in @('CFBundleVersion', 'CFBundleShortVersionString')) {
        if ($plist.SelectSingleNode("/plist/dict/key[text()='$key']/following-sibling::*[1]").InnerText -ne $version) {
            throw "The plist does not use version.txt for $key."
        }
    }
    if ($plist.SelectSingleNode("/plist/dict/key[text()='CFBundleExecutable']/following-sibling::*[1]").InnerText -ne 'FloatingTransferStation.Mac') {
        throw 'The plist executable does not match the published apphost.'
    }
    $invalidVersionRejected = $false
    try { Initialize-MacAppBundle $bundle '1.2.3</string>' } catch { $invalidVersionRejected = $true }
    if (-not $invalidVersionRejected) { throw 'The bundle accepted an invalid version.' }
    New-MacAppZip $bundle $zipPath
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $hostEntry = $archive.GetEntry('FloatingTransferStation.app/Contents/MacOS/FloatingTransferStation.Mac')
        if ($null -eq $hostEntry -or (($hostEntry.ExternalAttributes -shr 16) -band 0xFFFF) -ne 0x81ED) {
            throw 'The ZIP does not preserve the apphost as a regular executable file (0755).'
        }
        $resourceEntry = $archive.GetEntry('FloatingTransferStation.app/Contents/Resources/' + $resourceName)
        if ($null -eq $resourceEntry) {
            throw 'The ZIP lost a Unicode resource path.'
        }
        if ((($resourceEntry.ExternalAttributes -shr 16) -band 0xFFFF) -ne 0x81A4) { throw 'Resource permissions must be 0644.' }
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName.Contains('\') -or -not $entry.FullName.StartsWith('FloatingTransferStation.app/')) {
                throw 'The ZIP contains a path outside the app bundle or Windows separators.'
            }
            $data = $entry.Open()
            try { $data.CopyTo([System.IO.Stream]::Null) } finally { $data.Dispose() }
        }
    } finally { $archive.Dispose() }
    $stream = [System.IO.File]::OpenRead($zipPath)
    $reader = [System.IO.BinaryReader]::new($stream)
    try {
        $stream.Position = $stream.Length - 12
        $count = $reader.ReadUInt16()
        $stream.Position += 4
        $stream.Position = $reader.ReadUInt32()
        for ($index = 0; $index -lt $count; $index++) {
            $offset = $stream.Position
            if ($reader.ReadUInt32() -ne 0x02014B50) { throw 'The ZIP central directory is malformed.' }
            $stream.Position++
            if ($reader.ReadByte() -ne 3) { throw 'The ZIP creator platform must be Unix even when built on Windows.' }
            $stream.Position = $offset + 28
            $nameLength = $reader.ReadUInt16()
            $extraLength = $reader.ReadUInt16()
            $commentLength = $reader.ReadUInt16()
            $stream.Position = $offset + 46 + $nameLength + $extraLength + $commentLength
        }
    } finally { $reader.Dispose(); $stream.Dispose() }
    Write-Host 'macOS packaging contract passed: native-only code directory, nested-first signing/JIT entitlement, version, plist executable, Unix ZIP creator/permissions, Unicode paths and readable entries.'
} finally {
    $resolvedFixture = [System.IO.Path]::GetFullPath($fixtureRoot)
    $allowedRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'TestResults')).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedFixture.StartsWith($allowedRoot, [System.StringComparison]::Ordinal)) { throw 'Refusing to clean a fixture outside TestResults.' }
    if (Test-Path -LiteralPath $resolvedFixture) { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force }
}
