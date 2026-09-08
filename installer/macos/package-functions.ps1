# Shared packaging primitives. Loading this file performs no build or filesystem mutation.
function Initialize-MacAppBundle([string]$BundlePath, [string]$Version) {
    if ($Version -notmatch '\A[0-9]+\.[0-9]+\.[0-9]+\z') {
        throw 'The bundle version must be a numeric major.minor.patch version.'
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $BundlePath 'Contents/MacOS') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $BundlePath 'Contents/Resources') | Out-Null
    $template = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $PSScriptRoot 'Info.plist')
    [System.IO.File]::WriteAllText((Join-Path $BundlePath 'Contents/Info.plist'), $template.Replace('__VERSION__', $Version))
    [System.IO.File]::WriteAllText((Join-Path $BundlePath 'Contents/PkgInfo'), 'APPL????')
}

function Test-MachOFile([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $magic = New-Object byte[] 4
        if ($stream.Read($magic, 0, 4) -ne 4) { return $false }
        return [System.BitConverter]::ToString($magic) -in @(
            'FE-ED-FA-CE', 'CE-FA-ED-FE', 'FE-ED-FA-CF', 'CF-FA-ED-FE',
            'CA-FE-BA-BE', 'BE-BA-FE-CA', 'CA-FE-BA-BF', 'BF-BA-FE-CA')
    } finally {
        $stream.Dispose()
    }
}

function Get-MacBundleSigningPlan([string]$BundlePath) {
    $BundlePath = [System.IO.Path]::GetFullPath($BundlePath)
    $codeDirectory = Join-Path $BundlePath 'Contents/MacOS'
    $executable = Join-Path $codeDirectory 'FloatingTransferStation.Mac'
    $files = @(Get-ChildItem -LiteralPath $codeDirectory -Force | Sort-Object Name)
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw 'The macOS bundle is missing its main executable.'
    }
    foreach ($file in $files) {
        # Apple treats everything in this directory as nested code. Managed PE DLLs,
        # JSON, PDBs and resources must not be left beside the single-file apphost.
        if ($file.PSIsContainer -or ($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
            -not (Test-MachOFile $file.FullName)) {
            throw "Contents/MacOS must contain only regular Mach-O files; unexpected content: $($file.Name)"
        }
    }
    # Signing the main executable can cause codesign to inspect its enclosing bundle.
    # Every nested library/helper must therefore be signed before the main executable.
    foreach ($file in $files) {
        if ($file.FullName -ne $executable) {
            [pscustomobject]@{ Path = $file.FullName; JitEntitlements = $false }
        }
    }
    [pscustomobject]@{ Path = $executable; JitEntitlements = $true }
    [pscustomobject]@{ Path = $BundlePath; JitEntitlements = $true }
}

function New-MacAppZip([string]$BundlePath, [string]$ZipPath) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $bundle = Get-Item -LiteralPath $BundlePath
    $items = @($bundle) + @(Get-ChildItem -LiteralPath $BundlePath -Recurse -Force | Sort-Object FullName)
    if ($items.Count -ge 65535) { throw 'This packager does not support ZIP64 archives.' }
    $prefixLength = $bundle.Parent.FullName.TrimEnd([System.IO.Path]::DirectorySeparatorChar).Length + 1
    $stream = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::CreateNew)
    $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($item in $items) {
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Symlinks and reparse points are not accepted in the bundle: $($item.FullName)"
            }
            $name = $item.FullName.Substring($prefixLength).Replace('\', '/')
            if ($item.PSIsContainer) {
                $entry = $archive.CreateEntry($name + '/')
                $entry.ExternalAttributes = (0x41ED -shl 16) -bor 0x10 # directory, 0755
            } else {
                $entry = [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $archive, $item.FullName, $name, [System.IO.Compression.CompressionLevel]::Optimal)
                $mode = if (Test-MachOFile $item.FullName) { 0x81ED } else { 0x81A4 } # file, 0755 / 0644
                $entry.ExternalAttributes = $mode -shl 16
            }
        }
    } finally {
        $archive.Dispose()
        $stream.Dispose()
    }

    # .NET on Windows marks ZIP entries as DOS-created. Set the central directory's
    # creator OS to Unix so Archive Utility/unzip honor the explicit permission bits.
    $stream = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite)
    $reader = [System.IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -ge [uint32]::MaxValue) { throw 'This packager does not support ZIP64 archives.' }
        $stream.Position = $stream.Length - 22 # ZipArchive writes no archive comment.
        if ($reader.ReadUInt32() -ne 0x06054B50) { throw 'ZIP end-of-central-directory record was not found.' }
        $stream.Position += 6
        $count = $reader.ReadUInt16()
        $stream.Position += 4
        $centralOffset = $reader.ReadUInt32()
        if ($count -ne $items.Count) { throw 'ZIP entry count does not match the bundle.' }
        $stream.Position = $centralOffset
        for ($index = 0; $index -lt $count; $index++) {
            $entryOffset = $stream.Position
            if ($reader.ReadUInt32() -ne 0x02014B50) { throw 'Invalid ZIP central directory entry.' }
            $stream.Position = $entryOffset + 5
            $stream.WriteByte(3) # Unix
            $stream.Position = $entryOffset + 28
            $nameLength = $reader.ReadUInt16()
            $extraLength = $reader.ReadUInt16()
            $commentLength = $reader.ReadUInt16()
            $stream.Position = $entryOffset + 46 + $nameLength + $extraLength + $commentLength
        }
    } finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}
