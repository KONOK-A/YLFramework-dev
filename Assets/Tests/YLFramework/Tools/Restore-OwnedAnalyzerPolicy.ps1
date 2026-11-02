#requires -Version 7.6
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $AnalyzerRoot,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9-]+$')][string] $PolicyRun,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9-]+$')][string] $Run,
    [switch] $Apply
)
Set-StrictMode -Version 3.0
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($AnalyzerRoot)
$ownerPath=Join-Path $root 'owner.json'
$owner=Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
$policy=Get-Content -LiteralPath (Join-Path $root ($PolicyRun+'.json')) -Raw | ConvertFrom-Json
if ($owner.Purpose -cne 'Stage9-owned-analyzer-inputs' -or -not $policy.PolicyWritten -or $policy.Project -cne $owner.Project -or
    $policy.Unity -cne $owner.Unity -or $policy.PackageSha256 -cne $owner.PackageSha256 -or
    (Get-FileHash -LiteralPath $ownerPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $policy.OwnerSha256 -or
    $policy.RuleSet -cnotin @('Assets/Assembly-CSharp.ruleset','Assets/AnalyzerConsumers/Analyzer.Acceptance.Consumer.ruleset')) { throw 'Exact immutable policy/owner identity is required.' }
$path=Join-Path $owner.Project $policy.RuleSet
foreach ($leaf in @($root,$path)) {
    for ($parent=$leaf; $parent; $parent=[IO.Path]::GetDirectoryName($parent)) {
        if ((Test-Path -LiteralPath $parent) -and (([IO.File]::GetAttributes($parent) -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw 'Linked policy path.' }
    }
}
$input=@($owner.Inputs | Where-Object Path -CEQ $policy.RuleSet)
if ($input.Count -ne 1 -or $input[0].Sha256 -cne $policy.BeforeSha256 -or
    (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $(if ($Apply) {$policy.BeforeSha256} else {$policy.AfterSha256}) -or
    (Get-FileHash -LiteralPath ($path+'.meta') -Algorithm SHA256).Hash.ToLowerInvariant() -cne $policy.MetaSha256) { throw 'Never overwrite an independently modified policy.' }
$archive=Join-Path $root ($PolicyRun+'-after.ruleset')
if (-not $Apply) {
    if (Test-Path -LiteralPath $archive) {
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -cne $policy.AfterSha256) { throw 'Unknown policy archive.' }
    } else { [IO.File]::Copy($path,$archive) }
}
$bytes=if ($Apply) {[IO.File]::ReadAllBytes($archive)} else {[Convert]::FromBase64String($policy.BeforeBytesBase64)}
$expected=if ($Apply) {$policy.AfterSha256} else {$policy.BeforeSha256}
$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
if ($hash -cne $expected) { throw 'Policy transition bytes are invalid.' }
$receipt=Join-Path $root ($Run+'.json')
if (Test-Path -LiteralPath $receipt) { throw 'Never overwrite policy recovery evidence.' }
[IO.File]::WriteAllBytes($path,$bytes)
$actual=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($receipt,(@{Restored=(-not $Apply -and $actual -ceq $policy.BeforeSha256); Applied=($Apply -and $actual -ceq $policy.AfterSha256); RuleSet=$path; SourcePolicy=$PolicyRun; Sha256=$actual;
    SourceGuidPreserved=$true; CompilerRecoveryVerified=$false; Project=$owner.Project} | ConvertTo-Json))
if ($actual -cne $expected) { throw 'Policy bytes did not transition exactly.' }
[pscustomobject]@{Restored=(-not $Apply); Applied=[bool]$Apply; RuleSet=$path; Receipt=$receipt; CompilerRecoveryVerified=$false}
