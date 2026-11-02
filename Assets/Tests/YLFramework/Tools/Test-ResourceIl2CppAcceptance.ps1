#requires -Version 7.6
[CmdletBinding()]
param([Parameter(Mandatory)][string] $EvidenceRoot, [switch] $Final, [string] $ReceiptName = '')
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($EvidenceRoot)
$carrier = (Get-Content -LiteralPath (Join-Path $root 'carrier-location.json') -Raw | ConvertFrom-Json).Carrier
$owner = Get-Content -LiteralPath (Join-Path $carrier 'r810-owner.json') -Raw | ConvertFrom-Json
if ($owner.Evidence -cne $root -or $owner.Carrier -cne $carrier -or $owner.Purpose -cne 'Resource-8.10-owned-carrier') { throw 'Owned IL2CPP evidence mismatch.' }
function Assert-Il2CppFinalIdentity {
    param($Body,$Report,[string] $ActualBuild,[string] $ExpectedBuild)
    if (-not [IO.Path]::GetFullPath($ActualBuild).Equals([IO.Path]::GetFullPath($ExpectedBuild),[StringComparison]::OrdinalIgnoreCase)) { throw 'Final run used a different build, not the designated ordinary IL2CPP build.' }
    if ($Body.Development -or $Body.LoopbackPinCompiled -or $Report.CandidateValidation -or $Report.Development -or @($Report.ExtraDefines).Count -ne 0 -or
        $Report.Backend -cne 'IL2CPP' -or $Report.Result -cne 'Succeeded' -or $Report.Errors -ne 0) { throw 'Final IL2CPP must be successful non-Development with no candidate or acceptance overrides.' }
}
function Assert-Il2CppScenario {
    param($Body,$Receipt,$Start,$Row,[string] $Root,$Fixture)
    if (-not $Receipt.Passed -or $Receipt.TimedOut -or $Receipt.Pid -ne $Body.Pid -or $Receipt.Pid -ne $Start.Pid -or
        $Body.Backend -cne 'IL2CPP' -or $Body.Unity -cne '2022.3.62f3c1' -or $Body.Platform -cne 'WindowsPlayer' -or
        $Body.Command -cne $Row.Command -or $Body.Mode -cne $Row.Mode -or $Body.Store -cne (Join-Path $Root ('Stores/' + $Row.Store))) { throw "IL2CPP identity/result mismatch: $($Row.Id)" }
    if ([bool]$Receipt.ControlledKill -ne [bool]$Row.ExpectedKill) { throw "Wrong terminal kind for required scenario: $($Row.Id)" }
    $arguments=@{}; $cli=@($Start.Arguments)
    for ($i=0;$i -lt $cli.Count;$i++) {
        if ($cli[$i].StartsWith('--r810-',[StringComparison]::Ordinal)) {
            if ($i+1 -ge $cli.Count -or $arguments.ContainsKey($cli[$i])) { throw 'Malformed/duplicate recorded Player argument.' }
            $option=$cli[$i]; $arguments[$option]=[string]$cli[$i+1]; $i++
        }
    }
    foreach ($pair in @(@('--r810-command',$Row.Command),@('--r810-mode',$Row.Mode),@('--r810-store',$Body.Store),@('--r810-expect',[string]$Row.Expect),@('--r810-target',[string]$Row.Target),@('--r810-key',$Row.Key),@('--r810-backend','IL2CPP'))) {
        if (-not $arguments.ContainsKey($pair[0]) -or $arguments[$pair[0]] -cne $pair[1]) { throw "Recorded scenario parameter mismatch: $($Row.Id)/$($pair[0])" }
    }
    $checkpoint=if ($arguments.ContainsKey('--r810-checkpoint')) { $arguments['--r810-checkpoint'] } else { '' }
    $fault=if ($arguments.ContainsKey('--r810-fault')) { $arguments['--r810-fault'] } else { '' }
    if ($checkpoint -cne $Row.Checkpoint -or $fault -cne $Row.Fault) { throw 'The declared fault/checkpoint was not the required scenario.' }
    $keyRejected=@($Body.Steps | Where-Object Name -CEQ 'key-rejected')
    if ($Row.ExpectedKeyError) {
        if ($keyRejected.Count -ne 1 -or $keyRejected[0].Data.Error -cne $Row.ExpectedKeyError -or $Receipt.ExitCode -ne 0 -or -not $Body.Passed) { throw 'The required key refusal was not actually observed.' }
        return
    }
    if ($keyRejected.Count) { throw 'A valid-key scenario unexpectedly became a key rejection.' }
    $projects=if ($Row.Mode -eq 'encrypted') { @($Fixture.Encrypted) } else { @($Fixture.None) }
    foreach ($project in $projects) {
        $mounted=@($Body.Mounted | Where-Object ProjectId -CEQ $project.Id)
        if ($mounted.Count -ne 1 -or $mounted[0].Digest -cne $project.Digests[$Row.Expect-1]) { throw 'Initial mounted generation differs from the required version.' }
        if (-not $Row.ExpectedKill) {
            $after=@($Body.AfterMounted | Where-Object ProjectId -CEQ $project.Id)
            if ($after.Count -ne 1 -or $after[0].Digest -cne $project.Digests[$Row.After-1]) { throw 'Terminal mounted generation differs from the required version.' }
        }
    }
    if ($Row.ExpectedKill) {
        if ($Receipt.ExitCode -ne -1) { throw 'A required controlled kill has a different termination code.' }
        if ($Row.Checkpoint) {
            if ($Body.Checkpoint -cne $Row.Checkpoint) { throw 'The Player stopped at a different transaction checkpoint.' }
        } elseif ($Row.Command -ne 'remote-kill-partial' -or $Body.PartialAtCancel.Length -lt 1MB) { throw 'The required download-partial interruption was not exercised.' }
    } elseif ($Row.Fault -eq 'throw' -and @($Body.Steps | Where-Object Name -CEQ 'injected-exception-recovered').Count -ne 1) { throw 'The declared injected exception was not recovered.' }
}
function Assert-Il2CppArchive {
    param([string] $Archive,$Report,[string] $Build='')
    $identity=Get-Content -LiteralPath (Join-Path $Archive 'identity.json') -Raw | ConvertFrom-Json
    if (-not $identity.Passed -or $identity.SourceIdentitySha256 -cne $Report.SourceIdentitySha256) { throw 'Actual source/linker/C++ archive missing.' }
    $sourcePath=Join-Path $Archive 'source-identity.json'
    if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Report.SourceIdentitySha256) { throw 'Archived source index hash differs from the actual build.' }
    $sources=@(Get-Content -LiteralPath $sourcePath -Raw | ConvertFrom-Json)
    $managed=if ($identity.PSObject.Properties.Name -contains 'StrippedFiles') { @($identity.StrippedFiles) } else { @($identity.StrippedAssemblies) }
    foreach ($section in @(@{ Name='Sources'; Rows=$sources; Key='Path' },@{ Name='ManagedStripped'; Rows=$managed; Key='Name' },@{ Name='GeneratedCpp'; Rows=@($identity.GeneratedCppFiles); Key='Path' })) {
        $folder=Join-Path $Archive $section.Name
        $expected=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($file in $section.Rows) {
            $relative=([string]$file.($section.Key)).Replace('\','/')
            if ([IO.Path]::IsPathRooted($relative) -or @($relative.Split('/') | Where-Object { $_ -in @('.','..') }).Count -or -not $expected.Add($relative)) { throw 'Invalid or duplicate archive member.' }
            $path=Join-Path $folder $relative
            if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or ([IO.FileInfo]$path).Length -ne $file.Length -or
                (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.Sha256) { throw "Archived file missing or changed: $($section.Name)/$relative" }
        }
        $actual=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($file in Get-ChildItem -LiteralPath $folder -File -Recurse) { $null=$actual.Add([IO.Path]::GetRelativePath($folder,$file.FullName).Replace('\','/')) }
        if (-not $expected.SetEquals($actual)) { throw 'The actual archive file closure differs from its index.' }
    }
    if (-not $Build) { throw 'Native build ownership is required to verify archived native identities.' }
    foreach ($file in $identity.NativeArtifacts) {
        $native=Join-Path $Build $file.Path
        $recorded=@($Report.PlayerFiles | Where-Object Path -CEQ $file.Path)
        if ($recorded.Count -ne 1 -or $recorded[0].Sha256 -cne $file.Sha256 -or ([IO.FileInfo]$native).Length -ne $file.Length -or
            (Get-FileHash -LiteralPath $native -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.Sha256) { throw 'Archived native identity differs from the actual Player.' }
    }
}
$required = [Collections.Generic.List[object]]::new()
function RequireRun([string] $Id,[string] $Command,[string] $Store,[string] $Mode='none',[string] $Reason='') {
    $after=if ($Command -in @('update','busy','activate','remote')) { 2 } else { 1 }
    $required.Add(@{ Id=$Id; Command=$Command; Store=$Store; Mode=$Mode; Reason=$Reason; Expect=1; Target=2; After=$after; Key='valid'; ExpectedKeyError=''; ExpectedKill=$false; Checkpoint=''; Fault='' })
}
RequireRun 'candidate-none-02' 'offline' 'candidate-none-02'
RequireRun 'candidate-encrypted-02' 'offline' 'candidate-encrypted-02' encrypted
foreach ($mode in @('none','encrypted')) {
    foreach ($case in @('cancel|cancel','prepare|prepare','prepared-restart|probe','busy-activate|busy','active-restart|probe','rollback|rollback','rollback-restart|probe')) {
        $parts=$case.Split('|'); RequireRun ('v3-' + $mode + '-' + $parts[0]) $parts[1] ('local-' + $mode) $mode
    }
}
foreach ($name in @('missing-key','wrong-key','updated-missing-key','updated-wrong-key','updated-valid-key')) { RequireRun ('v3-encrypted-' + $name) 'probe' 'local-encrypted' encrypted }
RequireRun 'v3-encrypted-reactivate' 'update' 'local-encrypted' encrypted
RequireRun 'v3-encrypted-updated-restart' 'probe' 'local-encrypted' encrypted
RequireRun 'v3-none-exception' 'update' 'local-none'; RequireRun 'v3-none-exception-restart' 'probe' 'local-none'
RequireRun 'v3-none-corrupt-active-lkg' 'probe' 'local-none'
foreach ($point in @('journalverified','activewritten-r810-a','beforecommitdecision','commitdecided')) {
    RequireRun ('v3-none-kill-' + $point) 'update' 'local-none'; RequireRun ('v3-none-recover-' + $point) 'probe' 'local-none'
}
RequireRun 'remote-positive-02' 'remote' 'remote-main'
RequireRun 'il-active-restart' 'probe' 'remote-main'
foreach ($name in @('untrusted-tls','wrong-pin')) { RequireRun ('il-' + $name) 'remote-reject' 'remote-tls' -Reason 'ConnectionError' }
RequireRun 'il-signature' 'remote-reject' 'remote-main' -Reason 'Release signature rejected'
foreach ($name in @('catalog-hash','redirect','missing','stall')) { RequireRun ('il-' + $name) 'remote-reject' 'remote-main' }
RequireRun 'il-encoding' 'remote-reject' 'remote-main' -Reason 'Encoded release responses are not accepted.'
RequireRun 'il-rollback' 'rollback' 'remote-main'; RequireRun 'il-rollback-restart' 'probe' 'remote-main'
foreach ($name in @('old-sequence','conflicting-sequence','failed-confirmed-floor')) { RequireRun ('il-' + $name) 'remote-reject' 'remote-main' -Reason 'older than confirmed history' }
RequireRun 'il-same-confirmed-release' 'remote' 'remote-main'
RequireRun 'il-transient-retry' 'remote' 'remote-retry'
RequireRun 'il-corrupt-body' 'remote-corrupt' 'remote-corrupt'; RequireRun 'il-corrupt-restart' 'probe' 'remote-corrupt'
RequireRun 'il-after-failed-valid' 'remote' 'remote-corrupt'
foreach ($case in @('range206','kill','ignore-range','changed-etag','bad-range','weak')) {
    $command = if ($case -eq 'kill') { 'remote-kill-partial' } else { 'remote-partial' }
    RequireRun ('range-' + $case + '-partial') $command ('remote-r-' + $case)
    $complete=if ($case -eq 'kill') { 'range-kill-complete-02' } else { 'range-' + $case + '-complete' }
    RequireRun $complete 'remote' ('remote-r-' + $case)
    RequireRun ('range-' + $case + '-active-restart') 'probe' ('remote-r-' + $case)
}
foreach ($case in @('prepare-only|remote-prepare','prepared-restart|probe','prepared-activate|activate','prepared-active-restart|probe')) {
    $parts=$case.Split('|'); RequireRun ('recover-' + $parts[0]) $parts[1] 'remote-prepared'
}
foreach ($point in @('beforecommitdecision','commitdecided')) {
    RequireRun ('recover-' + $point + '-kill') 'remote' ('remote-k-' + $point)
    RequireRun ('recover-' + $point + '-recover') 'probe' ('remote-k-' + $point)
    RequireRun ('recover-' + $point + '-floor') 'remote-reject' ('remote-k-' + $point) -Reason 'older than confirmed history'
}
RequireRun 'recover-beforecommitdecision-valid' 'remote' 'remote-k-beforecommitdecision'
RequireRun 'enc-encrypted-online' 'remote' 'remote-encrypted' encrypted
foreach ($name in @('restart','missing','wrong','valid')) { RequireRun ('enc-encrypted-' + $name) 'probe' 'remote-encrypted' encrypted }
foreach ($prefix in @('ordinary-candidate') + $(if ($Final) { @('ordinary-final') } else { @() })) {
    RequireRun ($prefix + '-none') 'offline' ($prefix + '-none')
    RequireRun ($prefix + '-encrypted') 'offline' ($prefix + '-encrypted') encrypted
    RequireRun ($prefix + '-persistent') 'probe' 'remote-main'
    RequireRun ($prefix + '-tls') 'remote-reject' ($prefix + '-tls') -Reason 'ConnectionError'
}
function SetExpectedVersion([string[]] $Ids,[int] $Before,[int] $Target=2,[int] $After=-1) {
    foreach ($id in $Ids) {
        $row=@($required | Where-Object Id -CEQ $id)
        if ($row.Count -ne 1) { throw "Invalid required semantic mapping: $id" }
        $row[0].Expect=$Before; $row[0].Target=$Target
        $row[0].After=if ($After -ge 0) { $After } elseif ($row[0].Command -in @('update','busy','activate','remote')) { $Target } else { $Before }
    }
}
SetExpectedVersion @('v3-none-active-restart','v3-encrypted-active-restart','v3-encrypted-updated-restart','v3-encrypted-updated-missing-key','v3-encrypted-updated-wrong-key','v3-encrypted-updated-valid-key','v3-none-recover-commitdecided','il-active-restart','il-signature','il-catalog-hash','il-redirect','il-missing','il-stall','il-encoding','il-failed-confirmed-floor','recover-prepared-active-restart','recover-commitdecided-recover','recover-beforecommitdecision-floor','recover-commitdecided-floor','enc-encrypted-restart','enc-encrypted-missing','enc-encrypted-wrong','enc-encrypted-valid','ordinary-candidate-persistent') 2
if ($Final) { SetExpectedVersion @('ordinary-final-persistent') 2 }
foreach ($case in @('range206','kill','ignore-range','changed-etag','bad-range','weak')) { SetExpectedVersion @('range-' + $case + '-active-restart') 2 }
SetExpectedVersion @('v3-none-rollback','v3-encrypted-rollback','il-rollback') 2 1 1
foreach ($row in $required) {
    if ($row.Id -match '(?:missing-key|encrypted-missing)$') { $row.Key='missing'; $row.ExpectedKeyError='ContentKeyUnavailable' }
    if ($row.Id -match '(?:wrong-key|encrypted-wrong)$') { $row.Key='wrong'; $row.ExpectedKeyError='ContentAuthenticationFailed' }
    if ($row.Id -eq 'v3-none-exception') { $row.Checkpoint='BeforeCommitDecision'; $row.Fault='throw'; $row.After=1 }
}
foreach ($binding in @('v3-none-kill-journalverified|JournalVerified','v3-none-kill-activewritten-r810-a|ActiveWritten:r810-a','v3-none-kill-beforecommitdecision|BeforeCommitDecision','v3-none-kill-commitdecided|CommitDecided','recover-beforecommitdecision-kill|BeforeCommitDecision','recover-commitdecided-kill|CommitDecided')) {
    $parts=$binding.Split('|'); $row=@($required | Where-Object Id -CEQ $parts[0])[0]
    $row.ExpectedKill=$true; $row.Checkpoint=$parts[1]; $row.Fault='kill'
}
@($required | Where-Object Id -CEQ 'range-kill-partial')[0].ExpectedKill=$true
# 完整性先检查，缺组时不得靠已存在目录数或旧Mono汇总推断通过。
foreach ($row in $required) {
    foreach ($file in @('result.json','process-result.json','process-start.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $root ('Runs/' + $row.Id + '/' + $file)) -PathType Leaf)) { throw "Required IL2CPP run missing: $($row.Id)/$file" }
    }
}
$normal=0; $kills=0; $rejects=0; $builds=[Collections.Generic.HashSet[string]]::new()
$fixture=Get-Content -LiteralPath (Join-Path $carrier 'Assets/StreamingAssets/r810-config.json') -Raw | ConvertFrom-Json
foreach ($row in $required) {
    $run=Join-Path $root ('Runs/' + $row.Id)
    $body=Get-Content -LiteralPath (Join-Path $run 'result.json') -Raw | ConvertFrom-Json
    $receipt=Get-Content -LiteralPath (Join-Path $run 'process-result.json') -Raw | ConvertFrom-Json
    $start=Get-Content -LiteralPath (Join-Path $run 'process-start.json') -Raw | ConvertFrom-Json
    Assert-Il2CppScenario $body $receipt $start $row $root $fixture
    $build=Split-Path -Path $start.Executable -Parent
    if (-not $build.StartsWith((Join-Path $carrier 'Builds') + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unknown executable ownership.' }
    $null=$builds.Add($build)
    if ($receipt.ControlledKill) {
        $kills++
        if ($body.PSObject.Properties.Name -contains 'Checkpoint') {
            $journal=@($body.CheckpointDisk.PSObject.Properties | Where-Object Name -Like '*commit-journal.json')
            $phase=if ($body.Checkpoint -eq 'CommitDecided') { 'Committed' } else { 'Verified' }
            if ($journal.Count -ne 1 -or $journal[0].Value.Phase -cne $phase) { throw 'Wrong transaction interruption decision.' }
        } elseif ($body.Command -ne 'remote-kill-partial') { throw 'Undeclared strong-stop checkpoint.' }
        continue
    }
    $normal++
    if (-not $body.Passed -or $receipt.ExitCode -ne 0 -or $body.AfterStopBundles -ne 0 -or @($body.Steps | Where-Object Name -CEQ 'normal-shutdown-clean').Count -ne 1) { throw 'Normal IL2CPP shutdown failed.' }
    foreach ($field in @('LiveInputs','WorkCount','AllocatedBufferBytes','ReservedBufferBytes','RetainedCleanupOwners','ProcessCngProviderCount','ProcessCngKeyCount','ProcessCngReleaseFailures')) {
        if ($body.AfterStopContent.$field -ne 0) { throw "Content responsibility not returned: $($row.Id)/$field" }
    }
    if (@($body.AfterStopProcessCng.PSObject.Properties | Where-Object Value -NE 0).Count) { throw 'Actual process CNG owner counters are not zero.' }
    if ($body.PSObject.Properties.Name -contains 'AfterStopDiagnostics') {
        $summary=$body.AfterStopDiagnostics.Summary
        if ($summary.LeaseCount -ne 0 -or $summary.ProducerCount -ne 0 -or $summary.WaiterCount -ne 0 -or $summary.RunningBundleCount -ne 0 -or $summary.QueuedBundleCount -ne 0 -or $summary.NativeBundleLoadCount -ne $summary.NativeBundleUnloadCount) { throw 'Native graph or cumulative balance failed.' }
    }
    if ($row.Reason -and ($body.PSObject.Properties.Name -notcontains 'Rejection' -or -not $body.Rejection.Contains($row.Reason))) { throw "Wrong refusal reason: $($row.Id)" }
    if (($body.PSObject.Properties.Name -contains 'Rejection') -or @($body.Steps | Where-Object Name -CEQ 'key-rejected').Count) { $rejects++ }
    if ($body.Mode -eq 'encrypted' -and ($body.PSObject.Properties.Name -notcontains 'CngNativeModule' -or
        -not [IO.Path]::GetFullPath($body.CngNativeModule).Equals((Join-Path ([Environment]::SystemDirectory) 'bcrypt.dll'),[StringComparison]::OrdinalIgnoreCase))) { throw 'CNG did not use the System32 dependency.' }
    if ($row.Id.StartsWith('ordinary-') -and ($body.Development -or $body.LoopbackPinCompiled)) { throw 'Ordinary non-Development IL2CPP contains a test pin or is Development.' }
    if ($row.Id.StartsWith('ordinary-final-')) { Assert-Il2CppFinalIdentity $body (Get-Content -LiteralPath (Join-Path $build 'BuildReport.json') -Raw | ConvertFrom-Json) $build (Join-Path $carrier 'Builds/R1') }
    if ($body.PSObject.Properties.Name -contains 'AfterDisk') {
        foreach ($slot in @($body.AfterDisk.PSObject.Properties | Where-Object Name -Like '*/active.json')) {
            foreach ($project in $body.AfterMounted) {
                $match=@($slot.Value.Projects | Where-Object ProjectId -CEQ $project.ProjectId)
                if ($match.Count -ne 1 -or $match[0].CatalogDigest -cne $project.Digest) { throw 'Disk/memory generations differ.' }
            }
        }
    }
}
$requests=@(Get-Content -LiteralPath (Join-Path $root 'server-requests.jsonl') | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.PSObject.Properties.Name -contains 'run' })
foreach ($case in @('range206','kill','ignore-range','changed-etag','bad-range','weak')) {
    $part=Get-Content -LiteralPath (Join-Path $root ('resume-' + $case + '-before.json')) -Raw | ConvertFrom-Json
    if ($part.Length -lt 1MB -or $part.Length -ge $part.Metadata.Length -or $part.Metadata.State -cne 'Partial') { throw 'Not a real incomplete IL2CPP partial.' }
    $complete=if ($case -eq 'kill') { 'range-kill-complete-02' } else { 'range-' + $case + '-complete' }
    $attempts=@($requests | Where-Object run -CEQ $complete)
    $ranged=@($attempts | Where-Object { $null -ne $_.range })
    if ($case -eq 'weak') { if ($ranged.Count) { throw 'Weak ETag used for resume.' }; continue }
    $status=if ($case -in @('ignore-range','changed-etag')) { 200 } else { 206 }
    if ($ranged.Count -ne 1 -or $ranged[0].range -cne "bytes=$($part.Length)-" -or $ranged[0].ifRange -cne $part.Metadata.ETag -or $ranged[0].status -ne $status) { throw 'Actual Range/strong If-Range binding failed.' }
    if ($case -eq 'bad-range' -and @($attempts | Where-Object { $_.url -ceq $ranged[0].url -and $_.status -eq 200 -and $null -eq $_.range }).Count -ne 1) { throw 'Invalid Content-Range was not rejected and restarted.' }
}
foreach ($build in $builds) {
    $report=Get-Content -LiteralPath (Join-Path $build 'BuildReport.json') -Raw | ConvertFrom-Json
    if ($report.Result -cne 'Succeeded' -or $report.Errors -ne 0 -or $report.Backend -cne 'IL2CPP' -or $report.ManagedStripping -cne 'Low') { throw 'Actual IL2CPP build metadata mismatch.' }
    foreach ($path in @('GameAssembly.dll','ResourceAcceptance_Data/il2cpp_data/Metadata/global-metadata.dat')) {
        $expected=@($report.PlayerFiles | Where-Object Path -CEQ $path)
        if ($expected.Count -ne 1 -or (Get-FileHash -LiteralPath (Join-Path $build $path) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expected[0].Sha256) { throw 'Native IL2CPP artifact identity differs.' }
    }
    $input=[IO.File]::OpenRead((Join-Path $build 'GameAssembly.dll')); $pe=[System.Reflection.PortableExecutable.PEReader]::new($input)
    try { if ($pe.PEHeaders.CoffHeader.Machine -ne [System.Reflection.PortableExecutable.Machine]::Amd64) { throw 'Not an x64 native GameAssembly.' } }
    finally { $pe.Dispose(); $input.Dispose() }
    Assert-Il2CppArchive (Join-Path $root ('BuildEvidence/' + (Split-Path -Path $build -Leaf))) $report $build
}
$expectedFailures=@{
    'baseline-encrypted-red'='PlatformNotSupportedException: Encrypted Resource content requires'
    'candidate-encrypted'='BCryptOpenAlgorithmProvider NTSTATUS=0xC0000225'
    'none-cancel'='DriveInfo::GetDriveFormat'
    'encrypted-cancel'='DriveInfo::GetDriveFormat'
    'remote-positive'='DriveInfo::GetDriveFormat'
}
$preserved=[Collections.Generic.List[string]]::new(); $allNormal=0; $allKills=0; $monoRegression=0
$monoCases=@{ 'mono-regression-none'='none'; 'mono-regression-encrypted'='encrypted' }
foreach ($directory in Get-ChildItem -LiteralPath (Join-Path $root 'Runs') -Directory) {
    if ($directory.Name -eq 'range-kill-complete' -and (Test-Path -LiteralPath (Join-Path $directory.FullName 'infrastructure-interruption.json'))) {
        if ((Test-Path -LiteralPath (Join-Path $directory.FullName 'result.json')) -or (Test-Path -LiteralPath (Join-Path $directory.FullName 'process-result.json'))) { throw 'Interrupted invocation unexpectedly has a terminal result.' }
        continue
    }
    $body=Get-Content -LiteralPath (Join-Path $directory.FullName 'result.json') -Raw | ConvertFrom-Json
    $receipt=Get-Content -LiteralPath (Join-Path $directory.FullName 'process-result.json') -Raw | ConvertFrom-Json
    if ($body.Backend -ceq 'Mono2x') {
        if (-not $monoCases.ContainsKey($directory.Name) -or $body.Mode -cne $monoCases[$directory.Name] -or $body.Command -cne 'offline' -or
            -not $receipt.Passed -or $receipt.ControlledKill -or $receipt.TimedOut -or $receipt.ExitCode -ne 0 -or -not $body.Passed -or
            $body.Development -or $body.LoopbackPinCompiled -or $body.AfterStopBundles -ne 0 -or @($body.Steps | Where-Object Name -CEQ 'normal-shutdown-clean').Count -ne 1) { throw 'Mono directed regression evidence is not the declared ordinary offline run.' }
        $monoRegression++; continue
    }
    if ($body.Backend -cne 'IL2CPP') { throw 'Unknown backend in current evidence.' }
    if (-not $receipt.Passed) {
        if (-not $expectedFailures.ContainsKey($directory.Name) -or $body.Failure -notmatch $expectedFailures[$directory.Name]) { throw "Unresolved current IL2CPP failure: $($directory.Name)" }
        $preserved.Add($directory.Name); continue
    }
    if ($receipt.ControlledKill) { $allKills++ } else {
        if ($receipt.ExitCode -ne 0 -or -not $body.Passed) { throw 'Extra run has no valid terminal result.' }
        $allNormal++
    }
}
if ($Final -and $monoRegression -ne $monoCases.Count) { throw 'The two declared Mono directed regression results are missing.' }
$protected=Get-Content -LiteralPath (Join-Path $root 'protected-before.json') -Raw | ConvertFrom-Json
foreach ($file in $protected) {
    if ((Get-FileHash -LiteralPath (Join-Path $owner.Repository $file.Path) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.Sha256) { throw "Protected main input changed: $($file.Path)" }
}
$parsed=0; $metas=0
if ($Final) {
    $sourceFiles=@(Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object { ($_.Name -like '*ResourcePlayer*' -or $_.Name -like '*ResourceIl2Cpp*') -and -not $_.Name.EndsWith('.meta') })
    $guids=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($source in $sourceFiles) {
        $meta=Get-Content -LiteralPath ($source.FullName + '.meta') -Raw
        if ($meta -notmatch '(?m)^guid:\s*([0-9a-f]{32})\s*\r?$' -or -not $guids.Add($Matches[1])) { throw 'Acceptance asset/meta identity mismatch.' }
        $metas++
        if ($source.Extension -eq '.ps1') {
            $tokens=$null; $errors=$null
            $null=[System.Management.Automation.Language.Parser]::ParseFile($source.FullName,[ref]$tokens,[ref]$errors)
            if (@($errors).Count) { throw "Acceptance script syntax failed: $($source.Name)" }
            $parsed++
        }
    }
    & node --check (Join-Path $PSScriptRoot 'ResourcePlayerAcceptanceServer.cjs')
    if ($LASTEXITCODE -ne 0) { throw 'HTTPS fixture syntax failed.' }
    $editor=Get-Content -LiteralPath (Join-Path $root 'main-editor-il2cpp-final.json') -Raw | ConvertFrom-Json
    if ($editor.Playing -or $editor.Paused -or $editor.Compiling -or $editor.Updating -or $editor.CompilationFailed -or $editor.Dirty -or $editor.Authority -or $editor.Core -or $editor.Bundles -ne 0) { throw 'Recorded main Editor terminal state is not clean and idle.' }
    $export=Get-Content -LiteralPath (Join-Path $root 'export-verification.json') -Raw | ConvertFrom-Json
    if (-not $export.Passed -or $export.Backend -cne 'IL2CPP' -or $export.Build -cne 'R1' -or
        (Get-FileHash -LiteralPath $export.Package -Algorithm SHA256).Hash.ToLowerInvariant() -cne $export.Sha256) { throw 'Latest exact IL2CPP source export is missing or changed.' }
}
$name=if ($ReceiptName) { $ReceiptName } elseif ($Final) { 'il2cpp-final.json' } else { 'il2cpp-candidate.json' }
if ($name -notmatch '^[a-z0-9][a-z0-9-]*\.json$' -or (Test-Path -LiteralPath (Join-Path $root $name))) { throw 'A new simple receipt filename is required.' }
$summary=@{ Passed=$true; Final=$Final.IsPresent; RequiredCases=$required.Count; NormalProcesses=$normal; ControlledKills=$kills; RejectionsWithinNormal=$rejects; AllCurrentIl2CppNormalProcesses=$allNormal; AllCurrentIl2CppControlledKills=$allKills; MonoDirectedRegressionProcesses=$monoRegression; PreservedCurrentFailures=$preserved.ToArray(); ActualBuilds=@($builds); ProtectedFiles=@($protected).Count; ParsedPowerShell=$parsed; AssetMetaFiles=$metas; Utc=[DateTime]::UtcNow.ToString('O'); HistoricalMono13='Separate pending item; not counted or resolved by this profile' }
[IO.File]::WriteAllText((Join-Path $root $name),($summary | ConvertTo-Json -Depth 6))
$summary | ConvertTo-Json -Depth 6 -Compress
