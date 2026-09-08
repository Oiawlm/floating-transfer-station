[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppPath,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [ValidateRange(5, 180)][int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX)) {
    throw 'The native macOS smoke test must run on macOS.'
}
$AppPath = [System.IO.Path]::GetFullPath($AppPath)
$EvidenceDirectory = [System.IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $EvidenceDirectory) { throw 'Use a fresh evidence directory so stale screenshots cannot pass the smoke test.' }
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
& /usr/bin/codesign --verify --deep --strict --verbose=2 $AppPath
if ($LASTEXITCODE -ne 0) { throw 'The extracted app signature is invalid.' }
$executable = Join-Path $AppPath 'Contents/MacOS/FloatingTransferStation.Mac'
& /bin/test -x $executable
if ($LASTEXITCODE -ne 0) { throw 'The ZIP did not preserve the apphost executable permission.' }
$startInfo = [System.Diagnostics.ProcessStartInfo]::new($executable)
$startInfo.UseShellExecute = $false
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.ArgumentList.Add('--smoke-test')
$startInfo.ArgumentList.Add($EvidenceDirectory)
$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $startInfo
try {
    if (-not $process.Start()) { throw 'Could not start the packaged macOS app.' }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill($true)
        $process.WaitForExit()
        throw "The macOS smoke test timed out after $TimeoutSeconds seconds."
    }
    if ($process.ExitCode -ne 0) { throw "The macOS app exited with code $($process.ExitCode)." }
    $completion = Join-Path $EvidenceDirectory 'smoke-complete.json'
    if (-not (Test-Path -LiteralPath $completion -PathType Leaf)) { throw 'The app did not write smoke-complete.json.' }
    $smokeResult = Get-Content -Raw -LiteralPath $completion | ConvertFrom-Json
    $nativeArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    if ($smokeResult.architecture -ne $nativeArchitecture) { throw 'The app did not run with the native machine architecture.' }
    $clipboardEvidence = Join-Path $EvidenceDirectory 'native-clipboard.json'
    if (-not (Test-Path -LiteralPath $clipboardEvidence -PathType Leaf)) { throw 'Native clipboard smoke evidence is missing.' }
    $clipboardResult = Get-Content -Raw -LiteralPath $clipboardEvidence | ConvertFrom-Json
    if ($clipboardResult.passed -ne $true) { throw 'Native clipboard smoke did not pass.' }
    if ($clipboardResult.architecture -ne $nativeArchitecture) { throw 'Native clipboard evidence has the wrong architecture.' }
    foreach ($check in @('text', 'private-marker', 'encoded-image', 'file-transfer')) {
        if ($check -notin $clipboardResult.checks) { throw "Native clipboard smoke did not cover $check." }
    }
    foreach ($name in @('expanded.png', 'collapsed.png')) {
        $path = Join-Path $EvidenceDirectory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "The app did not write $name." }
        $bytes = [System.IO.File]::ReadAllBytes($path)
        if ($bytes.Length -lt 33 -or [System.BitConverter]::ToString($bytes, 0, 8) -ne '89-50-4E-47-0D-0A-1A-0A') {
            throw "The app wrote an invalid PNG: $name"
        }
    }
    Write-Host "Native macOS bundle launch and screenshots passed: $EvidenceDirectory"
} finally {
    if ($stdout) { [System.IO.File]::WriteAllText((Join-Path $EvidenceDirectory 'stdout.log'), $stdout.GetAwaiter().GetResult()) }
    if ($stderr) { [System.IO.File]::WriteAllText((Join-Path $EvidenceDirectory 'stderr.log'), $stderr.GetAwaiter().GetResult()) }
    $process.Dispose()
}
