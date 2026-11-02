#requires -Version 7.6
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $TargetsPath,
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9_-]+$')][string] $Run
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$inputTargetsPath = [IO.Path]::GetFullPath($TargetsPath)
$inputRun = $Run
$targets = Get-Content -LiteralPath $inputTargetsPath -Raw | ConvertFrom-Json -AsHashtable
$output = Join-Path $targets.EvidenceRoot ('VerificationGuards/' + $Run + '.json')
if (-not (Test-Path -LiteralPath $targets.EvidenceRoot -PathType Container) -or (Test-Path -LiteralPath $output)) { throw 'Fresh guard output under an existing evidence root is required.' }
$results = [Collections.Generic.List[object]]::new()
function Save-GuardResults {
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
    [IO.File]::WriteAllText($output, (@{ Passed=@($results | Where-Object { -not $_.Passed }).Count -eq 0; Cases=$results.ToArray();
        TargetsSha256=(Get-FileHash -LiteralPath $inputTargetsPath -Algorithm SHA256).Hash.ToLowerInvariant() } | ConvertTo-Json -Depth 8))
}
$checker = Join-Path $PSScriptRoot 'Test-ResourceCompatibilityEvidence.ps1'
if (-not (Test-Path -LiteralPath $checker -PathType Leaf)) {
    $results.Add(@{ Name='checker-is-available'; Passed=$false; Failure='The required compatibility checker does not exist.' })
    Save-GuardResults
    throw 'The required compatibility checker does not exist.'
}
. $checker
function Check-Guard([string] $Name, [scriptblock] $Action, [string] $Expected = '') {
    $failure = $null
    $passed = $false
    try {
        $null = & $Action
        $passed = $Expected -ceq ''
        if (-not $passed) { $failure = 'Expected rejection was not raised.' }
    } catch {
        $failure = $_.Exception.Message
        $passed = $Expected -cne '' -and $failure.Contains('Compatibility: ' + $Expected, [StringComparison]::Ordinal)
    }
    $results.Add(@{ Name=$Name; Passed=$passed; Failure=$failure })
}
function Clone-GuardData($Value) { $Value | ConvertTo-Json -Depth 90 -Compress | ConvertFrom-Json -AsHashtable }
Check-Guard 'complete-target-map' { Assert-ResourceCompatibilityTargets $targets }
Check-Guard 'missing-profile' { $bad=Clone-GuardData $targets; $bad.Profiles=@($bad.Profiles)[0..1]; Assert-ResourceCompatibilityTargets $bad } 'profiles'
Check-Guard 'duplicate-role' { $bad=Clone-GuardData $targets; $bad.Profiles[1].Role=$bad.Profiles[0].Role; Assert-ResourceCompatibilityTargets $bad } 'profiles'
Check-Guard 'nonexact-unity' { $bad=Clone-GuardData $targets; $bad.UnityVersion='6000.3.27f2'; Assert-ResourceCompatibilityTargets $bad } 'unity'
Check-Guard 'candidate-mono' { $bad=Clone-GuardData $targets; $bad.Profiles[0].CandidateValidation=$true; Assert-ResourceCompatibilityTargets $bad } 'profile policy'
$cases = @(Get-ResourceCompatibilityCases $targets)
Check-Guard 'exact-matrix-cardinality' {
    if ($cases.Count -ne 140 -or @($cases | Where-Object Kill).Count -ne 10 -or @($cases.Id | Sort-Object -Unique).Count -ne 140) { throw 'Matrix is incomplete or duplicated.' }
}
$requests = @([IO.File]::ReadAllLines((Join-Path $targets.EvidenceRoot 'server-requests.jsonl')) | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable })
$fixtures = @{}
foreach ($id in @('mdev-seed1', 'idev-encrypted-wrong', 'r1-none-kill-journalverified', 'idev-encrypted-restart')) {
    $case = @($cases | Where-Object Id -CEQ $id)[0]
    $profile = @($targets.Profiles | Where-Object Role -CEQ $case.Role)[0]
    $path = Join-Path $targets.EvidenceRoot ('Runs/' + $id)
    $body = Read-ResourceCompatibilityJson (Join-Path $path 'result.json')
    $context = @{
        Targets=$targets; Profile=$profile
        Config=(Read-ResourceCompatibilityJson (Join-Path $targets.Carrier ('Builds/' + $profile.Build + '/ResourceAcceptance_Data/StreamingAssets/r810-config.json')))
        RuntimeIdentity=$body.RuntimeAssembly
        Requests=@($requests | Where-Object { $_.ContainsKey('run') -and $_.run -ceq $id })
        CheckpointPid=if ($case.Kill) { [int][IO.File]::ReadAllText((Join-Path $path 'result.json.checkpoint')) } else { $null }
        Observations=if ($case.Mode -ceq 'encrypted') { @([IO.File]::ReadAllLines((Join-Path $path 'writes.jsonl')) | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable }) } else { @() }
        PlayerLog=[IO.File]::ReadAllText((Join-Path $path 'Player.log'))
    }
    $fixtures[$id] = @{ Case=$case; Context=$context; Body=$body; Start=(Read-ResourceCompatibilityJson (Join-Path $path 'process-start.json')); Exit=(Read-ResourceCompatibilityJson (Join-Path $path 'process-result.json')) }
}
function Check-RunGuard([string] $Name, [string] $Id, [scriptblock] $Mutate, [string] $Expected = '') {
    $data = Clone-GuardData $fixtures[$Id]
    $null = & $Mutate $data
    Check-Guard $Name { Assert-ResourceCompatibilityRun -Case $data.Case -Context $data.Context -Body $data.Body -Start $data.Start -Exit $data.Exit } $Expected
}
Check-RunGuard 'normal-real-evidence' 'mdev-seed1' { param($x) }
Check-RunGuard 'kill-real-evidence' 'r1-none-kill-journalverified' { param($x) }
Check-RunGuard 'key-rejection-real-evidence' 'idev-encrypted-wrong' { param($x) }
Check-RunGuard 'normal-exit-code' 'mdev-seed1' { param($x) $x.Exit.ExitCode=2 } 'normal exit'
Check-RunGuard 'normal-passed-false' 'mdev-seed1' { param($x) $x.Body.Passed=$false } 'normal result'
Check-RunGuard 'normal-passed-string' 'mdev-seed1' { param($x) $x.Body.Passed='true' } 'normal result'
Check-RunGuard 'body-missing' 'mdev-seed1' { param($x) $x.Body=$null } 'result'
Check-RunGuard 'sparse-true-result' 'mdev-seed1' { param($x) $x.Body.Steps=@() } 'steps'
Check-RunGuard 'timeout' 'mdev-seed1' { param($x) $x.Exit.TimedOut=$true } 'process'
Check-RunGuard 'pid-mismatch' 'mdev-seed1' { param($x) $x.Body.Pid++ } 'pid'
Check-RunGuard 'wrong-unity' 'mdev-seed1' { param($x) $x.Body.Unity='2022.3.62f3c1' } 'runtime identity'
Check-RunGuard 'wrong-build-guid' 'mdev-seed1' { param($x) $x.Body.BuildGuid='00000000000000000000000000000000' } 'runtime identity'
Check-RunGuard 'wrong-build-hash' 'mdev-seed1' { param($x) $x.Start.BuildReportSha256=('0' * 64) } 'build identity'
Check-RunGuard 'wrong-runtime-mvid' 'mdev-seed1' { param($x) $x.Body.RuntimeAssembly='00000000-0000-0000-0000-000000000000' } 'runtime identity'
Check-RunGuard 'macro-mismatch' 'mdev-seed1' { param($x) $x.Body.AcceptanceTestsCompiled=$false } 'macros'
Check-RunGuard 'graphics-null' 'mdev-seed1' { param($x) $x.Body.Graphics='Null' } 'execution context'
Check-RunGuard 'wrong-audio-claim' 'mdev-seed1' { param($x) $x.Body.AudioPlayback='DspVerified' } 'execution context'
Check-RunGuard 'command-mismatch' 'mdev-seed1' { param($x) $x.Body.Command='probe' } 'command'
Check-RunGuard 'argument-mismatch' 'mdev-seed1' { param($x) $i=[array]::IndexOf($x.Start.Arguments,'--r810-target'); $x.Start.Arguments[$i+1]='1' } 'arguments'
Check-RunGuard 'native-responsibility' 'mdev-seed1' { param($x) $x.Body.AfterStopBundles=1 } 'cleanup'
Check-RunGuard 'missing-cleanup-counter' 'mdev-seed1' { param($x) $x.Body.AfterStopContent.Remove('LiveInputs') } 'cleanup'
Check-RunGuard 'string-cleanup-counter' 'mdev-seed1' { param($x) $x.Body.AfterStopContent.LiveInputs='0' } 'cleanup'
Check-RunGuard 'boolean-cleanup-counter' 'mdev-seed1' { param($x) $x.Body.AfterStopContent.LiveInputs=$false } 'cleanup'
Check-RunGuard 'wrong-consumed-generation' 'mdev-seed1' { param($x) $x.Body.Steps[1].Data.Version=1 } 'consume'
Check-RunGuard 'http-missing' 'mdev-seed1' { param($x) $x.Context.Requests=@() } 'http'
Check-RunGuard 'kill-marker-mismatch' 'r1-none-kill-journalverified' { param($x) $x.Context.CheckpointPid++ } 'checkpoint'
Check-RunGuard 'kill-not-controlled' 'r1-none-kill-journalverified' { param($x) $x.Exit.ControlledKill=$false } 'kill'
Check-RunGuard 'kill-fake-normal-result' 'r1-none-kill-journalverified' { param($x) $x.Body.Passed=$true } 'kill'
Check-RunGuard 'key-rejected-as-unsupported' 'idev-encrypted-wrong' { param($x) $x.Body.Steps[0].Data.Error='Unsupported' } 'key rejection'
Check-RunGuard 'watcher-plaintext' 'idev-encrypted-wrong' { param($x) $x.Exit.WriteObservation.Plaintext=1 } 'observation'
Check-RunGuard 'watcher-errors' 'idev-encrypted-wrong' { param($x) $x.Exit.WriteObservation.Errors=1 } 'observation'
Check-RunGuard 'watcher-missing' 'idev-encrypted-wrong' { param($x) $x.Exit.WriteObservation=$null } 'observation'
Check-RunGuard 'watcher-dropped-short-read' 'idev-encrypted-restart' {
    param($x)
    $x.Context.Observations=@($x.Context.Observations | Where-Object { -not ($null -ne $_.Header -and $_.Header.Length -eq 0) })
} 'observation counters'
Check-Guard 'watcher-zero-byte-header-is-counted' {
    $x=Clone-GuardData $fixtures['idev-encrypted-restart']
    $result=Assert-ResourceCompatibilityRun -Case $x.Case -Context $x.Context -Body $x.Body -Start $x.Start -Exit $x.Exit
    if ($result.ObservationCountersMatch -ne $true -or $result.RecordedProbeMisses -ne 2) { throw 'A zero-byte probe was not counted as a miss.' }
}
$fixtureRoot=Join-Path $targets.EvidenceRoot ('VerificationGuardFixtures/'+$inputRun)
if (Test-Path -LiteralPath $fixtureRoot) { throw 'Never overwrite a file-set guard fixture.' }
foreach ($name in @('valid','unexpected')) {
    $directory=Join-Path $fixtureRoot $name
    $null=[IO.Directory]::CreateDirectory($directory)
    [IO.File]::WriteAllBytes((Join-Path $directory 'input.bin'),[byte[]]@(1,2,3))
}
$file=@{Path='input.bin'; Length=3; Sha256=(Get-FileHash -LiteralPath (Join-Path $fixtureRoot 'valid/input.bin') -Algorithm SHA256).Hash.ToLowerInvariant()}
[IO.File]::WriteAllBytes((Join-Path $fixtureRoot 'unexpected/rogue.bin'),[byte[]]@(4))
Check-Guard 'exact-file-set' { Assert-ResourceCompatibilityFiles (Join-Path $fixtureRoot 'valid') @($file) -Exact }
Check-Guard 'unregistered-artifact' { Assert-ResourceCompatibilityFiles (Join-Path $fixtureRoot 'unexpected') @($file) -Exact } 'artifact closure'
Check-Guard 'changed-artifact-bytes' { $bad=Clone-GuardData $file; $bad.Sha256=('0'*64); Assert-ResourceCompatibilityFiles (Join-Path $fixtureRoot 'valid') @($bad) } 'artifact bytes'
Check-Guard 'artifact-path-escape' { $bad=Clone-GuardData $file; $bad.Path='../outside.bin'; Assert-ResourceCompatibilityFiles (Join-Path $fixtureRoot 'valid') @($bad) } 'path'
Check-Guard 'resume-retains-all-request-groups' {
    $profile=@($targets.Profiles | Where-Object Role -CEQ 'MonoDevelopment')[0]
    Assert-ResourceCompatibilityResume $targets $profile $requests
}
Save-GuardResults
$results | Select-Object Name,Passed | Format-Table
if (@($results | Where-Object { -not $_.Passed }).Count) { throw 'Compatibility evidence guard regression failed.' }
