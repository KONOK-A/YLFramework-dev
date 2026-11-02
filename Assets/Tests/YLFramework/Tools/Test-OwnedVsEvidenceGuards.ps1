#requires -Version 7.6
[CmdletBinding()]
param([Parameter(Mandatory)][string] $HostRoot,[Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]+$')][string] $Run)
Set-StrictMode -Version 3.0
$ErrorActionPreference='Stop'
$inputRoot=[IO.Path]::GetFullPath($HostRoot)
$inputRun=$Run
$checker=Join-Path $PSScriptRoot 'Test-OwnedVsBaselineEvidence.ps1'
if (-not (Test-Path -LiteralPath $checker)) { throw 'The owned VS native evidence checker is not available.' }
. $checker
$config=Get-Content -LiteralPath (Join-Path $inputRoot 'host.json') -Raw | ConvertFrom-Json -AsHashtable
$ready=Get-Content -LiteralPath (Join-Path $inputRoot 'ready.json') -Raw | ConvertFrom-Json -AsHashtable
$original=Get-Content -LiteralPath (Join-Path $inputRoot 'baseline-initial.json') -Raw | ConvertFrom-Json -AsHashtable
$results=[Collections.Generic.List[object]]::new()
foreach ($name in @('valid','empty','wrong-pid','wrong-project','wrong-level','missing-control','duplicate-family','unknown-message','false-completion','wrong-saved')) {
    $value=$original | ConvertTo-Json -Depth 30 | ConvertFrom-Json -AsHashtable
    switch ($name) {
        'empty' {$value.Result.ErrorItems=@()}
        'wrong-pid' {$value.Info.Pid++}
        'wrong-project' {$value.Result.ErrorItems[0].Project='unowned.csproj'}
        'wrong-level' {$value.Result.ErrorItems[0].Level=4}
        'missing-control' {$value.Result.ErrorItems=@($value.Result.ErrorItems | Where-Object { $_.Message -notlike '*AudioPlayRequest*' })}
        'duplicate-family' {$value.Result.ErrorItems[2].Message=$value.Result.ErrorItems[0].Message}
        'unknown-message' {$value.Result.ErrorItems[0].Message='unrelated compiler diagnostic'}
        'false-completion' {$value.Result.AnalysisCompletionKnown=$true}
        'wrong-saved' {$value.Result.Documents[0].Saved=$false}
    }
    $rejected=$false; $failure=$null
    try { Assert-OwnedVsSnapshot $value $config $ready @(8,9,10) @(8,9,10) $true $true }
    catch {$rejected=$true; $failure=$_.Exception.Message}
    $results.Add(@{Name=$name; Passed=($rejected -eq ($name -cne 'valid')); Failure=$failure})
}
$path=Join-Path $inputRoot ($inputRun+'.json')
if (Test-Path -LiteralPath $path) { throw 'Never overwrite a VS guard receipt.' }
[IO.File]::WriteAllText($path,(@{Passed=@($results | Where-Object { -not $_.Passed }).Count -eq 0; Cases=$results.ToArray()} | ConvertTo-Json -Depth 8))
$results | Select-Object Name,Passed | Format-Table
if (@($results | Where-Object { -not $_.Passed }).Count) { throw 'Owned VS evidence guards failed.' }
