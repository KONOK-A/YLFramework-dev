#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param([string] $TemporaryRoot = [IO.Path]::GetTempPath())
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Common/Workspace.ps1')
$entry = Join-Path $PSScriptRoot 'Test-YLDistribution.ps1'
$ast = [Management.Automation.Language.Parser]::ParseFile($entry, [ref]$null, [ref]$null)
$audit = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Get-DistributionPackageContent' }, $true)
. ([scriptblock]::Create($audit.Extent.Text))
$root = Join-Path (Get-YLTestPath $TemporaryRoot) ('yl-manifest-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($root)
$checks = 0
function Save-Manifest([string] $Text) { [IO.File]::WriteAllText((Join-Path $root 'package.json'), $Text, [Text.UTF8Encoding]::new($false)) }
function Reject-Manifest([scriptblock] $Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'Untrusted UPM manifest metadata was accepted.' }
    $script:checks++
}
try {
    [IO.File]::WriteAllText((Join-Path $root 'LICENSE.md'), 'fixture MIT', [Text.UTF8Encoding]::new($false))
    Save-Manifest '{"name":"com.kano.ylframework","version":"0.1.0-preview.1","dependencies":{"com.unity.ugui":"1.0.0"}}'
    $baseline = (Get-DistributionPackageContent -Root $root).sha256
    Save-Manifest "{`r`n  `"dependencies`": {`"com.unity.ugui`":`"1.0.0`"},`r`n  `"version`": `"0.1.0-preview.1`",`r`n  `"name`": `"com.kano.ylframework`"`r`n}"
    if ((Get-DistributionPackageContent -Root $root).sha256 -cne $baseline) { throw 'Formatting or property order changed manifest semantics.' }; $checks++
    Save-Manifest '{"name":"com.kano.ylframework","version":"0.1.0-preview.1","dependencies":{"com.unity.ugui":"1.0.0"},"_fingerprint":"0123456789abcdef0123456789abcdef01234567"}'
    Reject-Manifest { Get-DistributionPackageContent -Root $root }
    if ((Get-DistributionPackageContent -Root $root -AllowManagedFingerprint).sha256 -cne $baseline) { throw 'A validated UPM-managed fingerprint changed public manifest semantics.' }; $checks++
    Save-Manifest '{"name":"com.kano.ylframework","version":"0.1.0-preview.1","_fingerprint":true}'
    Reject-Manifest { Get-DistributionPackageContent -Root $root -AllowManagedFingerprint }
    Save-Manifest '{"name":"com.kano.ylframework","version":"0.1.0-preview.2","dependencies":{"com.unity.ugui":"1.0.0"}}'
    if ((Get-DistributionPackageContent -Root $root).sha256 -ceq $baseline) { throw 'A changed package version was ignored.' }; $checks++
    Save-Manifest '{"name":"com.kano.ylframework","version":"0.1.0-preview.1","dependencies":{"com.unity.ugui":"2.0.0"}}'
    if ((Get-DistributionPackageContent -Root $root).sha256 -ceq $baseline) { throw 'A changed dependency was ignored.' }; $checks++
    Save-Manifest '{"name":"com.kano.ylframework","version":"0.1.0-preview.1","dependencies":{"com.unity.ugui":"1.0.0"}}'
    $code = Join-Path $root 'Example.cs'
    [IO.File]::WriteAllText($code, "// exact bytes`n", [Text.UTF8Encoding]::new($false))
    $lf = (Get-DistributionPackageContent -Root $root).sha256
    [IO.File]::WriteAllText($code, "// exact bytes`r`n", [Text.UTF8Encoding]::new($false))
    if ((Get-DistributionPackageContent -Root $root).sha256 -ceq $lf) { throw 'Ordinary package files must remain byte-exact.' }; $checks++
    Save-Manifest '{"name":"com.kano.ylframework","version":"0.1.0-preview.1","published":"2026-11-02T00:00:00Z"}'
    $utc = (Get-DistributionPackageContent -Root $root).sha256
    Save-Manifest '{"name":"com.kano.ylframework","version":"0.1.0-preview.1","published":"2026-11-02T08:00:00+08:00"}'
    if ((Get-DistributionPackageContent -Root $root).sha256 -ceq $utc) { throw 'JSON date-looking strings were incorrectly interpreted as equal timestamps.' }; $checks++
    Save-Manifest '{"name":"com.kano.ylframework","version":"0.1.0-preview.1","version":"0.1.0-preview.2"}'
    Reject-Manifest { Get-DistributionPackageContent -Root $root }
    "PASS: $checks manifest semantics, managed metadata and byte-exact payload contracts."
} finally { [IO.Directory]::Delete($root, $true) }
