#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $CandidateReceipt,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$', Options = 'None')][string] $CandidateSha256,
    [Parameter(Mandatory)][string] $BaselineReceipt,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$', Options = 'None')][string] $BaselineSha256,
    [Parameter(Mandatory)][string] $Output,
    [string] $ControlledStartUtc,
    [string] $ControlledEndUtc
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ([bool]$ControlledStartUtc -ne [bool]$ControlledEndUtc) { throw 'Provide both UTC bounds for the same controlled serial measurement window, or neither.' }
. (Join-Path $PSScriptRoot '../Common/Packages.ps1')
. (Join-Path $PSScriptRoot '../Common/FrozenPerformanceEvidence.ps1')
$candidate = Read-YLPackageEvidenceJson -Path $CandidateReceipt -ExpectedSha256 $CandidateSha256
$baseline = Read-YLPackageEvidenceJson -Path $BaselineReceipt -ExpectedSha256 $BaselineSha256
$window = if ($ControlledStartUtc) { @{ startUtc = $ControlledStartUtc; endUtc = $ControlledEndUtc } }
$comparison = Get-YLFrozenRemoteStartupComparison -Candidate $candidate -Baseline $baseline -ControlledWindow $window
Write-YLTestJson -Path $Output -Value ([ordered]@{
    purpose = 'YLResource.FrozenDelivery.RemoteStartupComparison'
    candidate = (Get-YLTestPath $CandidateReceipt); candidateSha256 = $CandidateSha256
    baseline = (Get-YLTestPath $BaselineReceipt); baselineSha256 = $BaselineSha256
    result = $comparison
})
if ($comparison.status -cne 'Passed') { throw ('Startup comparison ' + $comparison.status + ': ' + ($comparison.reasons -join '; ') + '. See ' + $Output) }
"Startup comparison passed: $Output"
