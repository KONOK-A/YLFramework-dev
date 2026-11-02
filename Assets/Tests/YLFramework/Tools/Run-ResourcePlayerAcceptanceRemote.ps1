#requires -Version 7.6
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $EvidenceRoot,
    [string] $BuildName = 'C3',
    [ValidateSet('Security','Resume','Recovery','Encrypted','Encoding')][string] $Group = 'Security',
    [string] $RunPrefix = 'net-',
    [string] $StartAt = '',
    [string] $PriorPrefixes = '',
    [ValidateSet('Mono2x','IL2CPP')][string] $Backend = 'Mono2x',
    [switch] $CandidateValidation,
    [ValidateSet('','2022.3.62f3c1','6000.3.27f1')][string] $ExpectedUnityVersion = '',
    [string] $StorePrefix = ''
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($EvidenceRoot)
$invoke = Join-Path $PSScriptRoot 'Invoke-ResourcePlayerAcceptance.ps1'
if ($StorePrefix -and $StorePrefix -notmatch '^[a-z0-9-]+$') { throw 'Simple lowercase StorePrefix required.' }
$script:Skipping = -not [string]::IsNullOrEmpty($StartAt)
function Policy([string] $Mode, [string] $Run) {
    [IO.File]::WriteAllText((Join-Path $root 'server-mode.json'), (@{ mode=$Mode; run=$Run } | ConvertTo-Json -Compress))
}
function Run([string] $Id, [string] $Command, [string] $Store='remote-main', [int] $Expect=1, [int] $Target=2,
    [string] $Mode='valid', [string] $ErrorKind='', [string] $Trust='pin', [string] $Checkpoint='', [string] $Fault='', [string] $Key='valid') {
    if ($script:Skipping) { if ($Id -cne $StartAt) { return }; $script:Skipping = $false }
    $run = $RunPrefix + $Id
    $storeName = $StorePrefix + $Store
    Policy $Mode $run
    $contentMode = if ($Group -eq 'Encrypted') { 'encrypted' } else { 'none' }
    $reason = if ($Id -eq 'encoding') { 'Encoded release responses are not accepted.' } else { '' }
    & $invoke -EvidenceRoot $root -ExpectedUnityVersion $ExpectedUnityVersion -BuildName $BuildName -Backend $Backend -CandidateValidation:$CandidateValidation -RunId $run -Command $Command -StoreName $storeName -RemoteMounts -Expect $Expect -Target $Target -ErrorKind $ErrorKind -ErrorReason $reason -Trust $Trust -Checkpoint $Checkpoint -Fault $Fault -Mode $contentMode -Key $Key
    if ($Id -eq 'signature') {
        $result = Get-Content -LiteralPath (Join-Path $root ('Runs/' + $run + '/result.json')) -Raw | ConvertFrom-Json
        if ($result.Rejection -notlike '*Release signature rejected.*') { throw 'The actual RSA signature rejection was not exercised.' }
    }
    if ($Id -in @('old-sequence','conflicting-sequence') -or $Id.EndsWith('-floor')) {
        $result = Get-Content -LiteralPath (Join-Path $root ('Runs/' + $run + '/result.json')) -Raw | ConvertFrom-Json
        if ($result.Rejection -notlike '*Remote release is older than confirmed history*') { throw 'The actual persisted confirmed-sequence rejection was not exercised.' }
    }
}
function Requests([string] $Run) {
    $ids = @(@($RunPrefix) + @($PriorPrefixes -split ',') | ForEach-Object { $_ + $Run })
    @(Get-Content -LiteralPath (Join-Path $root 'server-requests.jsonl') | ForEach-Object { $_ | ConvertFrom-Json } |
        Where-Object { $_.PSObject.Properties.Name -contains 'run' -and $_.run -cin $ids })
}
if ($Group -eq 'Security') {
    Run 'active-restart' 'probe' -Expect 2
    Run 'untrusted-tls' 'remote-reject' 'remote-tls' -ErrorKind UpdateFailed -Trust default
    Run 'wrong-pin' 'remote-reject' 'remote-tls' -ErrorKind UpdateFailed -Trust wrong
    Run 'signature' 'remote-reject' -Expect 2 -Mode signature -ErrorKind UpdateFailed
    Run 'catalog-hash' 'remote-reject' -Expect 2 -Mode catalog-hash -ErrorKind UpdateFailed
    Run 'redirect' 'remote-reject' -Expect 2 -Mode redirect -ErrorKind UpdateFailed
    Run 'missing' 'remote-reject' -Expect 2 -Mode missing -ErrorKind UpdateFailed
    Run 'encoding' 'remote-reject' -Expect 2 -Mode encoded -ErrorKind UpdateFailed
    Run 'stall' 'remote-reject' -Expect 2 -Mode stall -ErrorKind UpdateFailed
    Run 'rollback' 'rollback' -Expect 2 -Target 1
    Run 'rollback-restart' 'probe'
    Run 'old-sequence' 'remote-reject' -Mode old -ErrorKind UpdateFailed
    Run 'conflicting-sequence' 'remote-reject' -Mode conflict -ErrorKind UpdateFailed
    Run 'same-confirmed-release' 'remote'
    Run 'transient-retry' 'remote' 'remote-retry' -Mode transient
    Run 'corrupt-body' 'remote-corrupt' 'remote-corrupt' -Mode corrupt -ErrorKind UpdateFailed
    Run 'corrupt-restart' 'probe' 'remote-corrupt'
    # 原Plan只持久化成功激活的最高序号；首次失败前没有confirmed，不虚构observed冻结策略。
    Run 'failed-confirmed-floor' 'remote-reject' -Expect 2 -Mode old -ErrorKind UpdateFailed
    Run 'after-failed-valid' 'remote' 'remote-corrupt'
    $redirects = Requests 'redirect'
    if ($redirects.Count -ne 1 -or $redirects[0].status -ne 302 -or $redirects[0].url -notlike '*/release.json') { throw 'Redirect refusal did not exercise the intended path.' }
    if (@(Requests 'missing').Count -ne 1) { throw 'Terminal 404 was retried.' }
    if (@(Requests 'transient-retry' | Where-Object status -EQ 503).Count -lt 1) { throw 'Real bounded transient retry not observed.' }
} elseif ($Group -eq 'Resume') {
    foreach ($case in @('range206','kill','ignore-range','changed-etag','bad-range','weak')) {
        if ($script:Skipping -and -not $StartAt.StartsWith($case + '-', [StringComparison]::Ordinal)) { continue }
        $resumeEvidence = Join-Path $root ('resume-' + $StorePrefix + $case + '-before.json')
        if ((Test-Path -LiteralPath $resumeEvidence) -and $StartAt -cne ($case + '-complete')) { throw 'Never overwrite prior resume evidence.' }
        $store = 'remote-r-' + $case
        $storeName = $StorePrefix + $store
        $firstMode = if ($case -eq 'weak') { 'weak' } else { 'valid' }
        $command = if ($case -eq 'kill') { 'remote-kill-partial' } else { 'remote-partial' }
        Run ($case + '-partial') $command $store -Mode $firstMode
        $first = Get-Content -LiteralPath (Join-Path $root ('Runs/' + $RunPrefix + $case + '-partial/result.json')) -Raw | ConvertFrom-Json
        $partPath = [IO.Path]::GetFullPath($first.PartialAtCancel.Path)
        if (-not $partPath.StartsWith((Join-Path $root ('Stores/' + $storeName)) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Partial ownership mismatch.' }
        $part = Get-Item -LiteralPath $partPath
        $metadata = Get-Content -LiteralPath ([IO.Path]::ChangeExtension($partPath,'.json')) -Raw | ConvertFrom-Json
        if ($part.Length -lt 1MB -or $part.Length -ge $metadata.Length -or $metadata.State -cne 'Partial') { throw 'The real incomplete partial did not survive the Player exit.' }
        $partialLength = $part.Length
        $snapshot = @{ Path=$part.FullName; Length=$partialLength; Sha256=(Get-FileHash -LiteralPath $part.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); Metadata=$metadata }
        $snapshotJson = $snapshot | ConvertTo-Json
        if (Test-Path -LiteralPath $resumeEvidence) {
            $previous = Get-Content -LiteralPath $resumeEvidence -Raw | ConvertFrom-Json
            if ($previous.Path -cne $snapshot.Path -or $previous.Length -ne $snapshot.Length -or $previous.Sha256 -cne $snapshot.Sha256 -or
                ($previous.Metadata | ConvertTo-Json -Depth 10 -Compress) -cne ($metadata | ConvertTo-Json -Depth 10 -Compress)) { throw 'Prior resume evidence differs from the actual partial.' }
        } else {
            [IO.File]::WriteAllText($resumeEvidence, $snapshotJson)
        }
        $secondMode = if ($case -in @('ignore-range','changed-etag','bad-range')) { $case } else { 'valid' }
        Run ($case + '-complete') 'remote' $store -Mode $secondMode
        $requests = Requests ($case + '-complete')
        $ranged = @($requests | Where-Object { $null -ne $_.range })
        if ($case -eq 'weak') {
            if ($ranged.Count -ne 0) { throw 'A weak validator was illegally used for resume.' }
        } else {
            if ($ranged.Count -ne 1 -or $ranged[0].range -cne "bytes=$partialLength-" -or $ranged[0].ifRange -notmatch '^"[^\r\n]+"$') { throw 'Cross-process resume did not bind the exact real partial and a strong If-Range.' }
            $expected = if ($case -in @('ignore-range','changed-etag')) { 200 } else { 206 }
            if ($ranged[0].status -ne $expected) { throw 'The intended range response was not exercised.' }
            if ($case -eq 'bad-range') {
                $file = $ranged[0].url
                if (@($requests | Where-Object { $_.url -ceq $file -and $_.status -eq 200 -and $null -eq $_.range }).Count -ne 1) { throw 'Bad Content-Range was not rejected then restarted from zero.' }
            }
        }
        Run ($case + '-active-restart') 'probe' $store -Expect 2
        "R810 actual cross-process partial $case verified at $partialLength bytes."
    }
} elseif ($Group -eq 'Encoding') {
    Run 'encoding' 'remote-reject' -Expect 2 -Mode encoded -ErrorKind UpdateFailed
} elseif ($Group -eq 'Encrypted') {
    Run 'encrypted-online' 'remote' 'remote-encrypted'
    Run 'encrypted-restart' 'probe' 'remote-encrypted' -Expect 2
    Run 'encrypted-missing' 'probe' 'remote-encrypted' -Expect 2 -Key missing
    Run 'encrypted-wrong' 'probe' 'remote-encrypted' -Expect 2 -Key wrong
    Run 'encrypted-valid' 'probe' 'remote-encrypted' -Expect 2
} else {
    Run 'prepare-only' 'remote-prepare' 'remote-prepared'
    Run 'prepared-restart' 'probe' 'remote-prepared'
    Run 'prepared-activate' 'activate' 'remote-prepared'
    Run 'prepared-active-restart' 'probe' 'remote-prepared' -Expect 2
    foreach ($point in @('BeforeCommitDecision','CommitDecided')) {
        $tag = $point.ToLowerInvariant(); $store = 'remote-k-' + $tag
        Run ($tag + '-kill') 'remote' $store -Checkpoint $point -Fault kill
        $expected = if ($point -eq 'CommitDecided') { 2 } else { 1 }
        Run ($tag + '-recover') 'probe' $store -Expect $expected
        $recovered = Get-Content -LiteralPath (Join-Path $root ('Runs/' + $RunPrefix + $tag + '-recover/result.json')) -Raw | ConvertFrom-Json
        foreach ($pointer in @($recovered.AfterDisk.PSObject.Properties | Where-Object Name -Like '*/active.json')) {
            $floors = if ($pointer.Value.PSObject.Properties.Name -contains 'ReleaseFloors') { @($pointer.Value.ReleaseFloors) } else { @() }
            if (($expected -eq 1 -and $floors.Count -ne 0) -or ($expected -eq 2 -and ($floors.Count -ne 2 -or @($floors | Where-Object Sequence -NE 2).Count -ne 0))) { throw 'Uncommitted/committed floor recovery mismatch.' }
        }
        if ($expected -eq 1) { Run ($tag + '-valid') 'remote' $store }
        Run ($tag + '-floor') 'remote-reject' $store -Expect 2 -Mode old -ErrorKind UpdateFailed
    }
}
Policy 'valid' 'idle'
"R810 remote $Group suite finished."
