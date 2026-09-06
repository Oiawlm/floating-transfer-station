[CmdletBinding()]
param(
    [string]$DotnetPath,
    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $DotnetPath = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
}
if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    $IsccPath = Join-Path $repoRoot '.tools\inno\ISCC.exe'
}
$DotnetPath = [System.IO.Path]::GetFullPath($DotnetPath)
$IsccPath = [System.IO.Path]::GetFullPath($IsccPath)

foreach ($definition in @(
    @{ Script = 'bootstrap-dotnet.ps1'; Function = 'Get-RepositoryDotnetSdkVersion' },
    @{ Script = 'bootstrap-inno.ps1'; Function = 'Assert-InnoCompilerVersion' }
)) {
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $PSScriptRoot $definition.Script), [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) {
        throw "$($definition.Script) has parse errors."
    }
    $function = @($ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq $definition.Function
    }, $true))
    if ($function.Count -ne 1) {
        throw "Expected exactly one $($definition.Function) function."
    }
    # Import only the read-only checker, never the bootstrap download/install body.
    . ([scriptblock]::Create($function[0].Extent.Text))
}

$sdkVersion = Get-RepositoryDotnetSdkVersion $DotnetPath $repoRoot
$innoVersion = Assert-InnoCompilerVersion $IsccPath '7.0.2'
$fixtureRoot = Join-Path $repoRoot "TestResults\bootstrap-tool-versions-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
Set-Content -LiteralPath (Join-Path $fixtureRoot 'global.json') -Encoding UTF8 -Value @'
{"sdk":{"version":"99.0.100","rollForward":"disable","allowPrerelease":false}}
'@
$rejectedSdk = $false
try {
    Get-RepositoryDotnetSdkVersion $DotnetPath $fixtureRoot | Out-Null
} catch {
    $rejectedSdk = $_.Exception.Message -like '*does not satisfy global.json*'
}
if (-not $rejectedSdk) {
    throw 'The SDK checker accepted an unavailable SDK from a synthetic global.json.'
}
$rejectedCompiler = $false
try {
    Assert-InnoCompilerVersion $IsccPath '0.0.0' | Out-Null
} catch {
    $rejectedCompiler = $_.Exception.Message -like '*Expected Inno Setup 0.0.0*'
}
if (-not $rejectedCompiler) {
    throw 'The compiler checker accepted the wrong expected version.'
}
Write-Host "Bootstrap version contract passed: SDK=$sdkVersion, Inno=$innoVersion, incompatible SDK rejected, incorrect Inno version rejected."
