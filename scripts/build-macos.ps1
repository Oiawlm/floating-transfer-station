[CmdletBinding()]
param(
    [ValidateSet('osx-arm64', 'osx-x64')]
    [string[]]$RuntimeIdentifier = @('osx-arm64', 'osx-x64'),
    [string]$DotnetPath,
    [switch]$ForRelease,
    [switch]$SmokeTest,
    [switch]$KeepAppBundle
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$onMac = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX)
$nativeRid = 'osx-' + [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
if ($SmokeTest -and (-not $onMac -or @($RuntimeIdentifier | Where-Object { $_ -ne $nativeRid }).Count -gt 0)) {
    throw '-SmokeTest requires macOS and exactly the native runtime identifier; no emulation is used.'
}
if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $dotnetFile = if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) { 'dotnet.exe' } else { 'dotnet' }
    $localDotnet = Join-Path $repoRoot ".tools/dotnet/$dotnetFile"
    $DotnetPath = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
}
$DotnetPath = [System.IO.Path]::GetFullPath($DotnetPath)
$version = (Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $repoRoot 'version.txt')).Trim()
if ($version -notmatch '\A[0-9]+\.[0-9]+\.[0-9]+\z') { throw 'version.txt must contain exactly one numeric major.minor.patch version.' }
if ($ForRelease) { & (Join-Path $PSScriptRoot 'test-release-readiness.ps1') | Out-Host }
& (Join-Path $PSScriptRoot 'test-macos-packaging.ps1') | Out-Host
. (Join-Path $repoRoot 'installer/macos/package-functions.ps1')

Push-Location -LiteralPath $repoRoot
try {
    foreach ($testProject in @('tests/FloatingTransferStation.Core.Tests/FloatingTransferStation.Core.Tests.csproj',
            'tests/FloatingTransferStation.Mac.Tests/FloatingTransferStation.Mac.Tests.csproj')) {
        & $DotnetPath test $testProject -c Release
        if ($LASTEXITCODE -ne 0) { throw "Release tests failed: $testProject" }
    }
    foreach ($rid in ($RuntimeIdentifier | Select-Object -Unique)) {
        $outputDirectory = Join-Path $repoRoot "artifacts/macos/$rid"
        $staging = Join-Path $outputDirectory ('staging-' + [guid]::NewGuid().ToString('N'))
        $bundle = Join-Path $staging 'FloatingTransferStation.app'
        $zipName = "FloatingTransferStation-$version-$rid.zip"
        $temporaryZip = Join-Path $staging $zipName
        $zipPath = Join-Path $outputDirectory $zipName
        Initialize-MacAppBundle $bundle $version
        try {
            & $DotnetPath publish 'src/FloatingTransferStation.Mac/FloatingTransferStation.Mac.csproj' `
                -c Release -r $rid --self-contained true -warnaserror `
                -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false `
                -p:PublishReadyToRun=false -p:PublishTrimmed=false `
                -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $bundle 'Contents/MacOS')
            if ($LASTEXITCODE -ne 0) { throw "Self-contained publish failed: $rid" }
            $executable = Join-Path $bundle 'Contents/MacOS/FloatingTransferStation.Mac'
            if (-not (Test-Path -LiteralPath $executable -PathType Leaf) -or -not (Test-MachOFile $executable)) {
                throw "The published apphost is missing or not a Mach-O executable: $rid"
            }
            $signingPlan = @(Get-MacBundleSigningPlan $bundle)
            $signing = 'unsigned-cross-build'
            if ($onMac) {
                & /usr/bin/plutil -lint (Join-Path $bundle 'Contents/Info.plist')
                if ($LASTEXITCODE -ne 0) { throw 'Info.plist validation failed.' }
                $entitlements = Join-Path $repoRoot 'installer/macos/Entitlements.plist'
                & /usr/bin/plutil -lint $entitlements
                if ($LASTEXITCODE -ne 0) { throw 'JIT entitlements validation failed.' }
                foreach ($target in $signingPlan) {
                    if (Test-Path -LiteralPath $target.Path -PathType Leaf) {
                        & /bin/chmod 755 $target.Path
                        if ($LASTEXITCODE -ne 0) { throw 'Could not set executable permissions.' }
                    }
                    $signArguments = @('--force', '--sign', '-', '--timestamp=none')
                    if ($target.JitEntitlements) { $signArguments += @('--entitlements', $entitlements) }
                    Write-Host "Ad-hoc signing: $($target.Path)"
                    & /usr/bin/codesign @signArguments $target.Path
                    if ($LASTEXITCODE -ne 0) { throw "Ad-hoc signing failed: $($target.Path)" }
                }
                & /usr/bin/codesign --verify --deep --strict --verbose=2 $bundle
                if ($LASTEXITCODE -ne 0) { throw 'App bundle signature verification failed.' }
                $signing = 'ad-hoc'
            }
            New-MacAppZip $bundle $temporaryZip
            if ($SmokeTest) {
                # Test the extracted ZIP, including Unix permissions and signed contents.
                $extracted = Join-Path $staging 'extracted'
                & /usr/bin/ditto -x -k $temporaryZip $extracted
                if ($LASTEXITCODE -ne 0) { throw 'Could not extract the candidate ZIP.' }
                & (Join-Path $PSScriptRoot 'test-macos-smoke.ps1') `
                    -AppPath (Join-Path $extracted 'FloatingTransferStation.app') `
                    -EvidenceDirectory (Join-Path $repoRoot "TestResults/macos-smoke/$rid/$([guid]::NewGuid().ToString('N'))")
            }
            Move-Item -LiteralPath $temporaryZip -Destination $zipPath -Force
            $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
            [System.IO.File]::WriteAllText("$zipPath.sha256", "$hash  $zipName`n")
            $metadata = [ordered]@{
                version = $version; runtimeIdentifier = $rid; signing = $signing; notarized = $false
                nativeSmokeTest = [bool]$SmokeTest; sha256 = $hash; bytes = (Get-Item -LiteralPath $zipPath).Length
            }
            $metadata | ConvertTo-Json | Set-Content -LiteralPath "$zipPath.json" -Encoding UTF8
            Write-Host ("macOS candidate: {0} ({1:N1} MiB; {2}; not notarized)" -f $zipPath, ($metadata.bytes / 1MB), $signing)
        } finally {
            $resolvedStaging = [System.IO.Path]::GetFullPath($staging)
            $allowedPrefix = [System.IO.Path]::GetFullPath($outputDirectory).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
            if (-not $resolvedStaging.StartsWith($allowedPrefix, [System.StringComparison]::Ordinal)) {
                throw "Refusing to clean outside the macOS output directory: $resolvedStaging"
            }
            if ($KeepAppBundle) { Write-Host "Retained temporary bundle: $resolvedStaging" }
            elseif (Test-Path -LiteralPath $resolvedStaging) { Remove-Item -LiteralPath $resolvedStaging -Recurse -Force }
        }
    }
} finally {
    Pop-Location
}
