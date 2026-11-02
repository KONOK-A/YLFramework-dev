#requires -Version 7.6
[CmdletBinding()]
param([Parameter(Mandatory)][string] $EvidenceRoot, [Parameter(Mandatory)][string] $RunId)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($EvidenceRoot)
$carrier = (Get-Content -LiteralPath (Join-Path $root 'carrier-location.json') -Raw | ConvertFrom-Json).Carrier
$owner = Get-Content -LiteralPath (Join-Path $carrier 'r810-owner.json') -Raw | ConvertFrom-Json
if ($owner.Evidence -cne $root -or $RunId -notmatch '^[a-z0-9-]+$') { throw 'Owned coverage probe required.' }
$probe = Join-Path $root ('coverage-probe-' + $RunId)
if (Test-Path -LiteralPath $probe) { throw 'Never overwrite coverage regression evidence.' }
$null = [IO.Directory]::CreateDirectory((Join-Path $probe 'Runs'))
foreach ($name in @('carrier-location.json','protected-before.json','server-requests.jsonl','export-verification.json')) { [IO.File]::Copy((Join-Path $root $name),(Join-Path $probe $name)) }
foreach ($file in Get-ChildItem -LiteralPath $root -Filter 'resume-*-before.json' -File) { [IO.File]::Copy($file.FullName,(Join-Path $probe $file.Name)) }
$null = [IO.Directory]::CreateDirectory((Join-Path $probe 'watcher-probe-fixed'))
[IO.File]::Copy((Join-Path $root 'watcher-probe-fixed/result.json'),(Join-Path $probe 'watcher-probe-fixed/result.json'))
# 这些是真实已通过原件的只读副本，但故意缺整个Security/Recovery/Encrypted更新范围。
foreach ($sourceRun in Get-ChildItem -LiteralPath (Join-Path $root 'Runs') -Directory) {
    $id = $sourceRun.Name
    if ($id -match '^(net(?:[2-5])?-|recover-|encnet)') { continue }
    $destination = Join-Path $probe ('Runs/' + $id); $null = [IO.Directory]::CreateDirectory($destination)
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root ('Runs/' + $id)) -Filter '*.json' -File) { [IO.File]::Copy($file.FullName,(Join-Path $destination $file.Name)) }
}
$PSNativeCommandUseErrorActionPreference = $false
$output = & pwsh -NoLogo -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot 'Test-ResourcePlayerAcceptanceEvidence.ps1') -EvidenceRoot $probe -Final 2>&1
$exit = $LASTEXITCODE
$text = $output -join "`n"
$passed = $exit -ne 0 -and $text.Contains('Required acceptance run missing:')
$receipt = @{ ProbeOnly=$true; ActualPlayerAcceptance=$false; Passed=$passed; GateExitCode=$exit; GateOutput=$text; MissingWholeGroups=@('Security','RemoteRecovery','EncryptedRemote') }
[IO.File]::WriteAllText((Join-Path $probe 'regression-result.json'),($receipt | ConvertTo-Json -Depth 5))
$receipt | ConvertTo-Json -Depth 5 -Compress
if (-not $passed) { throw 'Sparse coverage was not rejected for the required-case omission.' }
