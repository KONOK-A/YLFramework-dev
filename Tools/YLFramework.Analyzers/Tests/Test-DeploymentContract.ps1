#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param([string] $TemporaryRoot = [IO.Path]::GetTempPath())
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$entry = Join-Path $PSScriptRoot '../build.ps1'
$ast = [Management.Automation.Language.Parser]::ParseFile($entry, [ref]$null, [ref]$null)
foreach ($name in @('Assert-AnalyzerMeta', 'Assert-AnalyzerDeployment')) {
    $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name }, $true)
    if ($null -eq $function) { throw "The maintained build entry does not enforce $name." }
    . ([scriptblock]::Create($function.Extent.Text))
}
if (-not [IO.Directory]::Exists($TemporaryRoot)) { throw 'An existing temporary parent is required.' }
$root = Join-Path $TemporaryRoot ('yl-analyzer-deployment-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($root)
$built = Join-Path $root 'verified.dll'; $deployed = Join-Path $root 'deployed.dll'
$meta = "fileFormatVersion: 2`nguid: 471f81c4273ee66429677df44cbd6960`nlabels:`n- RoslynAnalyzer`nPluginImporter:`n  isExplicitlyReferenced: 1`n  platformData:`n  - first:`n      Any: `n    second:`n      enabled: 0`n"
$checks = 0
function Reject([scriptblock] $Action) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (-not $rejected) { throw 'A stale or invalid deployed Analyzer was accepted.' }
    $script:checks++
}
try {
    [IO.File]::WriteAllBytes($built, [byte[]]@(77,90,1,2,3))
    [IO.File]::Copy($built, $deployed)
    [IO.File]::WriteAllText($deployed + '.meta', $meta, [Text.UTF8Encoding]::new($false))
    Assert-AnalyzerDeployment -Built $built -Destination $deployed; $checks++
    foreach ($fault in @('stale', 'missing-dll', 'missing-meta', 'guid', 'label', 'reference', 'platform')) {
        [IO.File]::Copy($built, $deployed, $true)
        [IO.File]::WriteAllText($deployed + '.meta', $meta, [Text.UTF8Encoding]::new($false))
        switch ($fault) {
            'stale' { [IO.File]::WriteAllBytes($deployed, [byte[]]@(77,90,4,5,6)) }
            'missing-dll' { [IO.File]::Delete($deployed) }
            'missing-meta' { [IO.File]::Delete($deployed + '.meta') }
            'guid' { [IO.File]::WriteAllText($deployed + '.meta', $meta.Replace('471f81c4273ee66429677df44cbd6960', '00000000000000000000000000000000')) }
            'label' { [IO.File]::WriteAllText($deployed + '.meta', $meta.Replace('RoslynAnalyzer', 'roslynanalyzer')) }
            'reference' { [IO.File]::WriteAllText($deployed + '.meta', $meta.Replace('isExplicitlyReferenced: 1', 'isExplicitlyReferenced: 0')) }
            'platform' { [IO.File]::WriteAllText($deployed + '.meta', $meta.Replace('enabled: 0', 'enabled: 1')) }
        }
        $paths = @($built, $deployed, ($deployed + '.meta'))
        $before = @($paths | ForEach-Object { if ([IO.File]::Exists($_)) { (Get-FileHash -LiteralPath $_).Hash } else { 'missing' } })
        Reject { Assert-AnalyzerDeployment -Built $built -Destination $deployed }
        $after = @($paths | ForEach-Object { if ([IO.File]::Exists($_)) { (Get-FileHash -LiteralPath $_).Hash } else { 'missing' } })
        if (($before -join ',') -cne ($after -join ',')) { throw 'Read-only deployment verification modified its inputs.' }
        $checks++
    }
    "PASS: $checks Analyzer deployment identity and read-only contracts."
} finally { [IO.Directory]::Delete($root, $true) }
