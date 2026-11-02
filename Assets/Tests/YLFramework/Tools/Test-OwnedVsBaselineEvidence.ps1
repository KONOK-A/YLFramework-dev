#requires -Version 7.6
[CmdletBinding()]
param([string] $HostRoot,[ValidatePattern('^[a-z0-9-]+$')][string] $Run)
Set-StrictMode -Version 3.0
$ErrorActionPreference='Stop'
function Confirm-OwnedVs([bool] $Value,[string] $Reason) { if (-not $Value) { throw ('VS evidence: '+$Reason) } }
function Same-OwnedVsEvidencePath([string] $First,[string] $Second) {
    [string]::Equals([IO.Path]::GetFullPath($First),[IO.Path]::GetFullPath($Second),[StringComparison]::OrdinalIgnoreCase)
}
function Assert-OwnedVsSnapshot($Reply,$Config,$Ready,[int[]] $Custom,[int[]] $Default,[bool] $CustomSaved,[bool] $DefaultSaved) {
    Confirm-OwnedVs ($Reply.Ok -is [bool] -and $Reply.Ok) 'reply failed'
    foreach ($info in @($Reply.Info,$Reply.Result.Info)) {
        Confirm-OwnedVs ($info.Pid -eq $Ready.Pid -and $info.HostPid -eq $Ready.HostPid -and
            $info.Owned -eq $true -and $info.UserControl -eq $false -and $info.BuildInvokedByHarness -eq $false -and
            (Same-OwnedVsEvidencePath $info.ProjectRoot $Config.Project) -and (Same-OwnedVsEvidencePath $info.Solution $Config.Solution) -and
            (Same-OwnedVsEvidencePath $info.Executable $Config.VsExecutable)) 'snapshot process/project ownership'
    }
    $data=$Reply.Result
    Confirm-OwnedVs ($data.DiagnosticIdFieldAvailable -is [bool] -and -not $data.DiagnosticIdFieldAvailable -and
        $data.AnalysisCompletionKnown -is [bool] -and -not $data.AnalysisCompletionKnown) 'do not invent native ID/completion capabilities'
    Confirm-OwnedVs ($data.ErrorItems -is [array] -and $data.Documents -is [array] -and $data.Documents.Count -eq 2) 'snapshot schema'
    $paths=@((Join-Path $Config.Project 'Assets/AnalyzerConsumers/IdeDiagnosticProbe.cs'),(Join-Path $Config.Project 'Assets/IdeDefaultProbe.cs'))
    $projects=@('Analyzer.Acceptance.Consumer.csproj','Assembly-CSharp.csproj')
    $expected=@(@($Custom),@($Default)); $saved=@($CustomSaved,$DefaultSaved)
    for ($index=0; $index -lt 2; $index++) {
        $path=$paths[$index]
        $documents=@($data.Documents | Where-Object { Same-OwnedVsEvidencePath $_.File $path })
        Confirm-OwnedVs ($documents.Count -eq 1 -and $documents[0].Project -ceq $projects[$index] -and
            $documents[0].Saved -is [bool] -and $documents[0].Saved -eq $saved[$index]) 'document must be attached to its actual project and buffer state'
        $families=@(foreach ($item in $data.ErrorItems) {
            if (-not (Same-OwnedVsEvidencePath $item.File $path)) { continue }
            Confirm-OwnedVs ($item.Project -ceq $projects[$index] -and $item.Level -eq 2 -and $item.Line -gt 0 -and $item.Column -gt 0) 'wrong native diagnostic location/project/severity'
            if ($item.Message.StartsWith('Inject ',[StringComparison]::Ordinal)) { 8 }
            elseif ($item.Message.StartsWith('LoadView/LoadViewAsync ',[StringComparison]::Ordinal)) { 9 }
            elseif ($item.Message.StartsWith('非泛型 IEnumerator ',[StringComparison]::Ordinal)) { 10 }
            else { throw 'VS evidence: unknown native diagnostic family' }
        })
        Confirm-OwnedVs ((@($families | Sort-Object) -join ',') -ceq (@($expected[$index] | Sort-Object) -join ',')) 'exact diagnostic family set/control contrast'
    }
    Confirm-OwnedVs ($data.ErrorItems.Count -eq ($Custom.Count+$Default.Count)) 'unowned or excess diagnostic rows'
}
if ($MyInvocation.InvocationName -eq '.') { return }
Confirm-OwnedVs (-not [string]::IsNullOrWhiteSpace($HostRoot) -and -not [string]::IsNullOrWhiteSpace($Run)) 'explicit host root and new receipt identity required'
$root=[IO.Path]::GetFullPath($HostRoot)
$config=Get-Content -LiteralPath (Join-Path $root 'host.json') -Raw | ConvertFrom-Json -AsHashtable
$ready=Get-Content -LiteralPath (Join-Path $root 'ready.json') -Raw | ConvertFrom-Json -AsHashtable
$cleanup=Get-Content -LiteralPath (Join-Path $root 'cleanup.json') -Raw | ConvertFrom-Json -AsHashtable
Confirm-OwnedVs ($config.Purpose -ceq 'Stage9-owned-vs-host' -and (Same-OwnedVsEvidencePath $config.Root $root) -and
    $config.ProcessGuardSelfTest.Passed -eq $true -and $config.ProcessGuardSelfTest.Cases -eq 7) 'host preparation identity'
foreach ($file in @(@{Name='OwnedVsDteHost.exe'; Sha256=$config.ExeSha256},@{Name='OwnedVsDteHost.cs'; Sha256=$config.SourceSha256})+@($config.Dependencies)) {
    Confirm-OwnedVs ((Get-FileHash -LiteralPath (Join-Path $root $file.Name) -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $file.Sha256) 'host source/executable/interop bytes changed'
}
Confirm-OwnedVs ($cleanup.Owned -eq $true -and $cleanup.SafeToQuit -eq $true -and $cleanup.QuitRequested -eq $true -and $cleanup.Pid -eq $ready.Pid) 'owned process cleanup was not requested safely'
foreach ($pidValue in @($ready.Pid,$ready.HostPid)) { Confirm-OwnedVs ($null -eq (Get-Process -Id $pidValue -ErrorAction SilentlyContinue)) 'owned process still running' }
$owner=Get-Content -LiteralPath (Join-Path $config.AnalyzerRoot 'owner.json') -Raw | ConvertFrom-Json -AsHashtable
Confirm-OwnedVs ($owner.Project -ceq $config.Project -and $owner.PackageSha256 -ceq $config.PackageSha256) 'Analyzer owner binding'
foreach ($input in $owner.Inputs) {
    $path=Join-Path $config.Project $input.Path
    Confirm-OwnedVs ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $input.Sha256 -and
        (Get-FileHash -LiteralPath ($path+'.meta') -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $input.MetaSha256) 'disk input changed during buffer-only comparison'
}
$cases=@(
    @{Name='baseline-initial'; C=@(8,9,10); D=@(8,9,10); CS=$true; DS=$true},
    @{Name='custom-inject-fixed-snapshot'; C=@(9,10); D=@(8,9,10); CS=$false; DS=$true},
    @{Name='both-inject-fixed-snapshot'; C=@(9,10); D=@(9,10); CS=$false; DS=$false},
    @{Name='custom-all-fixed-snapshot'; C=@(); D=@(9,10); CS=$false; DS=$false},
    @{Name='both-all-fixed-snapshot'; C=@(); D=@(); CS=$false; DS=$false},
    @{Name='reintroduced-both-snapshot'; C=@(8,9,10); D=@(8,9,10); CS=$false; DS=$false},
    @{Name='custom-pragma-snapshot'; C=@(9,10); D=@(8,9,10); CS=$false; DS=$false},
    @{Name='both-pragma-snapshot'; C=@(9,10); D=@(9,10); CS=$false; DS=$false},
    @{Name='restored-disk-snapshot'; C=@(8,9,10); D=@(8,9,10); CS=$true; DS=$true}
)
$checks=@(); $previous=[DateTimeOffset]::MinValue
foreach ($case in $cases) {
    $file=Join-Path $root ($case.Name+'.json')
    $reply=Get-Content -LiteralPath $file -Raw | ConvertFrom-Json -AsHashtable
    Assert-OwnedVsSnapshot $reply $config $ready $case.C $case.D $case.CS $case.DS
    $at=[DateTimeOffset]$reply.Result.CapturedUtc
    Confirm-OwnedVs ($at -gt $previous) 'snapshot chronology'
    $previous=$at
    $checks+=@{Name=$case.Name; Sha256=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(); CapturedUtc=$at.ToString('O'); CustomFamilies=$case.C; DefaultFamilies=$case.D}
}
$destination=Join-Path $root ($Run+'.json')
Confirm-OwnedVs (-not (Test-Path -LiteralPath $destination)) 'never overwrite VS evidence'
$result=@{Passed=$true; Scope='Native VS Warning baseline/buffer contrast/Warning pragma only; not Error policy, Unity Error recovery, IDE restart or Rider';
    Pid=$ready.Pid; HostPid=$ready.HostPid; Project=$config.Project; Checks=$checks; InferredDiagnosticFamilies=$true; RawDiagnosticIdFieldAvailable=$false;
    AnalysisCompletionBarrierAvailable=$false; BuildInvoked=$false; DiskInputsUnchanged=$true; SafeOwnedCleanupConfirmed=$true}
[IO.File]::WriteAllText($destination,($result | ConvertTo-Json -Depth 10))
[pscustomobject]@{Passed=$true; NativeSnapshots=$checks.Count; Pid=$ready.Pid; Receipt=$destination; FullIdeAcceptance=$false}
