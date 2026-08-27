[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$toolsRoot = Join-Path $repoRoot '.tools'
$downloadUri = 'https://github.com/jrsoftware/issrc/releases/download/is-7_0_2/innosetup-7.0.2-x64.exe'

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

New-Item -ItemType Directory -Force -Path $toolsRoot | Out-Null
$toolsRoot = Resolve-PhysicalToolsRoot $toolsRoot
$innoRoot = Join-Path $toolsRoot 'inno'
$iscc = Join-Path $innoRoot 'ISCC.exe'
$installer = Join-Path $toolsRoot 'innosetup-7.0.2-x64.exe'

if (-not (Test-Path -LiteralPath $iscc)) {
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

& $iscc '/?'
