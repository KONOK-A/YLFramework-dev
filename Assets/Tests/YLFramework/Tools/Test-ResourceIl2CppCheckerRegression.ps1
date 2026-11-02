#requires -Version 7.6
[CmdletBinding()]
param([Parameter(Mandatory)][string] $EvidenceRoot,[Parameter(Mandatory)][string] $RunId)
Set-StrictMode -Version 3.0
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($EvidenceRoot)
$carrier=(Get-Content -LiteralPath (Join-Path $root 'carrier-location.json') -Raw | ConvertFrom-Json).Carrier
$owner=Get-Content -LiteralPath (Join-Path $carrier 'r810-owner.json') -Raw | ConvertFrom-Json
if ($owner.Evidence -cne $root -or $RunId -notmatch '^[a-z0-9-]+$') { throw 'Owned checker probe required.' }
$probe=Join-Path $root ('checker-regression-' + $RunId)
if (Test-Path -LiteralPath $probe) { throw 'Never overwrite checker regression evidence.' }
$null=[IO.Directory]::CreateDirectory($probe)
$tokens=$null; $errors=$null
$ast=[System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-ResourceIl2CppAcceptance.ps1'),[ref]$tokens,[ref]$errors)
if (@($errors).Count) { throw 'The real checker has parse errors.' }
function LoadGuard([string] $Name) {
    $node=$ast.Find({param($item) $item -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $item.Name -ceq $Name},$true)
    if ($null -eq $node) { throw "Real guard missing: $Name" }
    $node.Body.GetScriptBlock()
}
$finalGuard=LoadGuard 'Assert-Il2CppFinalIdentity'
$scenarioGuard=LoadGuard 'Assert-Il2CppScenario'
$archiveGuard=LoadGuard 'Assert-Il2CppArchive'
function ReadJson([string] $Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
$r0=Join-Path $carrier 'Builds/R0'
$r0report=ReadJson (Join-Path $r0 'BuildReport.json')
$r0body=ReadJson (Join-Path $root 'Runs/ordinary-candidate-none/result.json')
$fixture=ReadJson (Join-Path $carrier 'Assets/StreamingAssets/r810-config.json')
$cases=[Collections.Generic.List[object]]::new()
function ExpectRejection([string] $Name,[scriptblock] $Guard,[object[]] $Arguments) {
    $errorText=$null
    try { & $Guard @Arguments } catch { $errorText=$_.Exception.Message }
    $cases.Add(@{ Name=$Name; Rejected=$null -ne $errorText; Reason=$errorText })
}
ExpectRejection 'candidate-cannot-stand-for-final' $finalGuard @($r0body,$r0report,$r0,(Join-Path $carrier 'Builds/R1'))
ExpectRejection 'candidate-rejected-even-at-designated-path' $finalGuard @($r0body,$r0report,$r0,$r0)
$throwRun=Join-Path $root 'Runs/v3-none-exception'
$kill=@{ Id='v3-none-kill-journalverified'; Command='update'; Store='local-none'; Mode='none'; Reason=''; Expect=1; Target=2; Key='valid'; ExpectedKeyError=''; ExpectedKill=$true; Checkpoint='JournalVerified'; Fault='kill'; After=2 }
ExpectRejection 'normal-throw-cannot-stand-for-strong-kill' $scenarioGuard @((ReadJson (Join-Path $throwRun 'result.json')),(ReadJson (Join-Path $throwRun 'process-result.json')),(ReadJson (Join-Path $throwRun 'process-start.json')),$kill,$root,$fixture)
$validRun=Join-Path $root 'Runs/v3-encrypted-prepared-restart'
$missing=@{ Id='v3-encrypted-missing-key'; Command='probe'; Store='local-encrypted'; Mode='encrypted'; Reason=''; Expect=1; Target=2; Key='missing'; ExpectedKeyError='ContentKeyUnavailable'; ExpectedKill=$false; Checkpoint=''; Fault=''; After=1 }
ExpectRejection 'valid-key-cannot-stand-for-missing-key' $scenarioGuard @((ReadJson (Join-Path $validRun 'result.json')),(ReadJson (Join-Path $validRun 'process-result.json')),(ReadJson (Join-Path $validRun 'process-start.json')),$missing,$root,$fixture)
$sparse=Join-Path $probe 'SparseArchive'; $null=[IO.Directory]::CreateDirectory($sparse)
[IO.File]::Copy((Join-Path $root 'BuildEvidence/C3/identity.json'),(Join-Path $sparse 'identity.json'))
ExpectRejection 'old-index-cannot-stand-for-missing-archive' $archiveGuard @($sparse,(ReadJson (Join-Path $carrier 'Builds/C3/BuildReport.json')))
$passed=@($cases | Where-Object { -not $_.Rejected }).Count -eq 0
$receipt=@{ ProbeOnly=$true; ActualPlayerAcceptance=$false; Passed=$passed; Cases=$cases.ToArray() }
[IO.File]::WriteAllText((Join-Path $probe 'result.json'),($receipt | ConvertTo-Json -Depth 6))
$receipt | ConvertTo-Json -Depth 6 -Compress
if (-not $passed) { throw 'The real checker accepted a mismatched final/scenario/archive fixture.' }
