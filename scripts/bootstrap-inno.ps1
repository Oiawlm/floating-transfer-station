[CmdletBinding()]
param([switch]$VerifyOnly)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$toolsRoot = Join-Path $repoRoot '.tools'
$expectedCompilerVersion = '7.0.2'
$downloadUri = "https://github.com/jrsoftware/issrc/releases/download/is-$($expectedCompilerVersion.Replace('.', '_'))/innosetup-$expectedCompilerVersion-x64.exe"

function Assert-InnoCompilerVersion([string]$compilerPath, [string]$expectedVersion) {
    # File resources may omit the patch version. Query the compiler's own preprocessor.
    $probe = @'
#pragma message "FTS_INNO_VERSION=" + DecodeVer(Ver)
[Setup]
AppName=CompilerVersionProbe
AppVersion=0
CreateAppDir=no
Uninstallable=no
Output=no
OutputDir=.
'@
    $output = $probe | & $compilerPath '/O-' '-' 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "The Inno Setup compiler version probe failed. $output"
    }
    $versions = [regex]::Matches(($output | Out-String), 'FTS_INNO_VERSION=([0-9]+\.[0-9]+\.[0-9]+)')
    if ($versions.Count -ne 1 -or $versions[0].Groups[1].Value -ne $expectedVersion) {
        throw "Expected Inno Setup $expectedVersion; the installed compiler reported: $output"
    }
    return $versions[0].Groups[1].Value
}

function Resolve-PhysicalToolsRoot([string]$path) {
    $item = Get-Item -Force -LiteralPath $path
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) {
        return $item.FullName
    }

    $targets = @($item.Target)
    if ($targets.Count -ne 1 -or [string]::IsNullOrWhiteSpace($targets[0])) {
        throw "Unable to resolve repository-local tool directory: $($item.FullName)"
    }

    $target = [string]$targets[0]
    if (-not [System.IO.Path]::IsPathRooted($target)) {
        $target = Join-Path $item.Parent.FullName $target
    }
    return [System.IO.Path]::GetFullPath($target)
}

if (-not (Test-Path -LiteralPath $toolsRoot -PathType Container)) {
    if ($VerifyOnly) {
        throw 'The repository-local tool directory is missing; verification will not create or install it.'
    }
    New-Item -ItemType Directory -Force -Path $toolsRoot | Out-Null
}
$toolsRoot = Resolve-PhysicalToolsRoot $toolsRoot
$innoRoot = Join-Path $toolsRoot 'inno'
$iscc = Join-Path $innoRoot 'ISCC.exe'
$installer = Join-Path $toolsRoot "innosetup-$expectedCompilerVersion-x64.exe"

if (-not (Test-Path -LiteralPath $iscc)) {
    if ($VerifyOnly) {
        throw 'The repository-local Inno compiler is missing; verification will not download or install it.'
    }
    Invoke-WebRequest -UseBasicParsing -Uri $downloadUri -OutFile $installer
    $arguments = @(
        '/PORTABLE=1',
        '/VERYSILENT',
        '/CURRENTUSER',
        '/SUPPRESSMSGBOXES',
        '/NORESTART',
        '/SP-',
        "/DIR=`"$innoRoot`""
    )
    $process = Start-Process -FilePath $installer -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        throw "Inno Setup bootstrap failed with exit code $($process.ExitCode)."
    }
}

if (-not (Test-Path -LiteralPath $iscc)) {
    throw 'ISCC.exe was not created in the repository-local tool directory.'
}

$compilerVersion = Assert-InnoCompilerVersion $iscc $expectedCompilerVersion
Write-Host "Repository Inno Setup compiler: $compilerVersion."
