[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$bootstrapScript = Join-Path $PSScriptRoot 'bootstrap-inno.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $bootstrapScript,
    [ref]$tokens,
    [ref]$parseErrors)
if ($parseErrors.Count -ne 0) {
    throw "bootstrap-inno.ps1 has $($parseErrors.Count) parse error(s)."
}

$functions = @($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Resolve-PhysicalToolsRoot'
}, $true))
if ($functions.Count -ne 1) {
    throw "Expected exactly one Resolve-PhysicalToolsRoot function, found $($functions.Count)."
}

. ([scriptblock]::Create($functions[0].Extent.Text))

$testResultsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'TestResults'))
$contractRoot = Join-Path $testResultsRoot "bootstrap-inno-path-$([guid]::NewGuid().ToString('N'))"
$normalRoot = Join-Path $contractRoot 'physical-tools'
$junctionRoot = Join-Path $contractRoot 'junction-tools'

try {
    New-Item -ItemType Directory -Force -Path $normalRoot | Out-Null
    $resolvedNormal = Resolve-PhysicalToolsRoot $normalRoot
    if ($resolvedNormal -ne [System.IO.Path]::GetFullPath($normalRoot)) {
        throw "Normal directory resolved incorrectly: $resolvedNormal"
    }

    New-Item -ItemType Junction -Path $junctionRoot -Target $normalRoot | Out-Null
    $resolvedJunction = Resolve-PhysicalToolsRoot $junctionRoot
    if ($resolvedJunction -ne [System.IO.Path]::GetFullPath($normalRoot)) {
        throw "Junction resolved incorrectly: $resolvedJunction"
    }

    Write-Host 'bootstrap-inno path contract passed: normal=1, junction=1.'
}
finally {
    $resolvedContractRoot = [System.IO.Path]::GetFullPath($contractRoot)
    $allowedPrefix = $testResultsRoot.TrimEnd('\') + '\'
    if ($resolvedContractRoot.StartsWith($allowedPrefix, [System.StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedContractRoot)) {
        Remove-Item -LiteralPath $resolvedContractRoot -Recurse -Force
    }
}
