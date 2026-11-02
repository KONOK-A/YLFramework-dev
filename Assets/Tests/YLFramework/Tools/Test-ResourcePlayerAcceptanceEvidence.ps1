#requires -Version 7.6
[CmdletBinding()]
param([Parameter(Mandatory)][string] $EvidenceRoot, [switch] $Final, [string] $ReceiptName = '')
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($EvidenceRoot)
$requiredRows = [Collections.Generic.List[object]]::new()
if ($Final) {
    # 本批已批准的完整profile，不从现存目录反推覆盖率；新批须显式指定其等价映射。
    function Required([string] $Id, [string] $Command, [string] $Store, [string] $Mode='none', [string] $Reason='') {
        $requiredRows.Add(@{ Id=$Id; Command=$Command; Store=$Store; Mode=$Mode; Reason=$Reason })
    }
    Required 'offline-none-02' 'offline' 'none-offline'
    Required 'offline-enc' 'offline' 'enc-offline' encrypted
    Required 'original-gate' 'remote-original-gate' 'gate' -Reason 'Windows Editor'
    Required 'corrupt-active-lkg-02' 'probe' 'local-none'
    foreach ($mode in @('none','encrypted')) {
        foreach ($case in @('cancel|cancel','prepare|prepare','prepared-restart|probe','busy-activate|busy','active-restart|probe','rollback|rollback','rollback-restart|probe')) {
            $parts = $case.Split('|'); Required ($mode + '-' + $parts[0]) $parts[1] ('local-' + $mode) $mode
        }
    }
    foreach ($case in @('missing-key','wrong-key','updated-missing-key','updated-wrong-key','updated-valid-key')) { Required ('encrypted-' + $case) 'probe' 'local-encrypted' encrypted }
    Required 'encrypted-reactivate' 'update' 'local-encrypted' encrypted
    Required 'encrypted-updated-restart' 'probe' 'local-encrypted' encrypted
    Required 'none-exception' 'update' 'local-none'; Required 'none-exception-restart' 'probe' 'local-none'
    foreach ($point in @('journalverified','activewritten-r810-a','beforecommitdecision','commitdecided')) {
        Required ('none-kill-' + $point) 'update' 'local-none'; Required ('none-recover-' + $point) 'probe' 'local-none'
    }
    Required 'remote-positive-green-02' 'remote' 'remote-main'
    Required 'net-active-restart' 'probe' 'remote-main'
    foreach ($id in @('untrusted-tls','wrong-pin')) { Required ('net2-' + $id) 'remote-reject' 'remote-tls' -Reason 'HTTP 0, ConnectionError' }
    Required 'net3-signature' 'remote-reject' 'remote-main' -Reason 'Release signature rejected'
    foreach ($id in @('catalog-hash','redirect','missing','stall')) { Required ('net3-' + $id) 'remote-reject' 'remote-main' }
    Required 'review-green-encoding' 'remote-reject' 'remote-main' -Reason 'Encoded release responses are not accepted.'
    Required 'net3-rollback' 'rollback' 'remote-main'; Required 'net3-rollback-restart' 'probe' 'remote-main'
    foreach ($id in @('old-sequence','conflicting-sequence')) { Required ('net4-' + $id) 'remote-reject' 'remote-main' -Reason 'older than confirmed history' }
    Required 'net4-same-confirmed-release' 'remote' 'remote-main'
    Required 'net4-transient-retry' 'remote' 'remote-retry'
    Required 'net4-corrupt-body' 'remote-corrupt' 'remote-corrupt'; Required 'net4-corrupt-restart' 'probe' 'remote-corrupt'
    Required 'net5-failed-confirmed-floor' 'remote-reject' 'remote-main' -Reason 'older than confirmed history'
    Required 'net5-after-failed-valid' 'remote' 'remote-corrupt'
    foreach ($case in @('range206','kill','ignore-range','changed-etag','bad-range','weak')) {
        $partial = if ($case -eq 'kill') { 'remote-kill-partial' } else { 'remote-partial' }
        Required ('range-' + $case + '-partial') $partial ('remote-r-' + $case)
        Required ('range-' + $case + '-complete') 'remote' ('remote-r-' + $case)
        Required ('range-' + $case + '-active-restart') 'probe' ('remote-r-' + $case)
    }
    Required 'recover-prepare-only' 'remote-prepare' 'remote-prepared'
    Required 'recover-prepared-restart' 'probe' 'remote-prepared'
    Required 'recover-prepared-activate' 'activate' 'remote-prepared'
    Required 'recover-prepared-active-restart' 'probe' 'remote-prepared'
    foreach ($point in @('beforecommitdecision','commitdecided')) {
        Required ('recover-' + $point + '-kill') 'remote' ('remote-k-' + $point)
        Required ('recover-' + $point + '-recover') 'probe' ('remote-k-' + $point)
        Required ('recover-' + $point + '-floor') 'remote-reject' ('remote-k-' + $point) -Reason 'older than confirmed history'
    }
    Required 'recover-beforecommitdecision-valid' 'remote' 'remote-k-beforecommitdecision'
    Required 'encnet2-encrypted-online' 'remote' 'remote-encrypted' encrypted
    foreach ($id in @('restart','missing','wrong','valid')) { Required ('encnet2-encrypted-' + $id) 'probe' 'remote-encrypted' encrypted }
    Required 'production-default-tls' 'remote-reject' 'production-tls' -Reason 'HTTP 0, ConnectionError'
    Required 'production-offline' 'offline' 'production-offline'; Required 'production-encrypted' 'offline' 'production-encrypted' encrypted
    Required 'terminal-none-02' 'offline' 'terminal-none'; Required 'terminal-encrypted-02' 'offline' 'terminal-encrypted' encrypted
    Required 'terminal-persistent-02' 'probe' 'remote-main'
    Required 'release-offline' 'offline' 'release-none'; Required 'release-encrypted' 'offline' 'release-encrypted' encrypted
    Required 'release-persistent' 'probe' 'remote-main'
    foreach ($row in $requiredRows) {
        foreach ($name in @('result.json','process-result.json','process-start.json')) {
            if (-not (Test-Path -LiteralPath (Join-Path $root ('Runs/' + $row.Id + '/' + $name)) -PathType Leaf)) { throw "Required acceptance run missing: $($row.Id) / $name" }
        }
    }
    foreach ($row in $requiredRows) {
        $run = Join-Path $root ('Runs/' + $row.Id)
        $body = Get-Content -LiteralPath (Join-Path $run 'result.json') -Raw | ConvertFrom-Json
        $receipt = Get-Content -LiteralPath (Join-Path $run 'process-result.json') -Raw | ConvertFrom-Json
        $start = Get-Content -LiteralPath (Join-Path $run 'process-start.json') -Raw | ConvertFrom-Json
        if (-not $receipt.Passed -or $receipt.TimedOut -or $body.Pid -ne $receipt.Pid -or $body.Pid -ne $start.Pid -or
            $body.Command -cne $row.Command -or $body.Mode -cne $row.Mode -or $body.Store -cne (Join-Path $root ('Stores/' + $row.Store)) -or
            $body.Unity -cne '2022.3.62f3c1' -or $body.Platform -cne 'WindowsPlayer') { throw "Required acceptance identity/result mismatch: $($row.Id)" }
        if ($row.Reason -and ($body.PSObject.Properties.Name -notcontains 'Rejection' -or -not $body.Rejection.Contains($row.Reason))) { throw "Required rejection reason missing: $($row.Id)" }
    }
}
$runs = @(Get-ChildItem -LiteralPath (Join-Path $root 'Runs') -Directory)
$history = @{
    'offline-none-01'='ObjectDisposedException.*ResourceRequest'
    'none-corrupt-active-lkg'='JsonReaderException'
    'remote-positive-red'='currently validated only in Windows Editor'
    'net-untrusted-tls'='rejection kind mismatch.*HTTP 0, ConnectionError'
    'net2-signature'='rejection kind mismatch.*Release signature rejected'
    'net3-old-sequence'='rejection kind mismatch.*older than confirmed history'
    'net4-failed-observed-floor'='The negative release must be rejected'
    'encnet-encrypted-online'='System.IO.IOException: 无法删除要被替换的文件'
    'terminal-none'='Terminal shutdown must drain real leases'
    'terminal-encrypted'='Terminal shutdown must drain real leases'
    'terminal-persistent'='Terminal shutdown must drain real leases'
    'review-red-encoding'='Remote rejection reason mismatch.*ConnectionError'
}
$preserved = [Collections.Generic.List[string]]::new()
$interruptions = [Collections.Generic.List[string]]::new()
$normal = 0; $kills = 0; $negative = 0; $mixedCheckpoints = 0
$parsedScripts = 0; $metaFiles = 0
foreach ($run in $runs) {
    if ($run.Name -ceq 'remote-positive-green' -and (Test-Path -LiteralPath (Join-Path $run.FullName 'infrastructure-interruption.json'))) {
        if ((Test-Path -LiteralPath (Join-Path $run.FullName 'result.json')) -or (Test-Path -LiteralPath (Join-Path $run.FullName 'process-result.json'))) { throw 'Interrupted run unexpectedly acquired a terminal result.' }
        $interruptions.Add($run.Name); continue
    }
    $body = Get-Content -LiteralPath (Join-Path $run.FullName 'result.json') -Raw | ConvertFrom-Json
    $receipt = Get-Content -LiteralPath (Join-Path $run.FullName 'process-result.json') -Raw | ConvertFrom-Json
    if ($history.ContainsKey($run.Name) -and -not $body.Passed -and -not $receipt.Passed) {
        if ($body.Passed -or $receipt.Passed -or $body.Failure -notmatch ('(?s)' + $history[$run.Name])) { throw "Historical failure/RED identity changed: $($run.Name)" }
        $preserved.Add($run.Name)
        continue
    }
    if (-not $receipt.Passed -or $receipt.TimedOut) { throw "Player gate failed: $($run.Name)" }
    if ($receipt.ControlledKill) {
        $kills++
        if ($receipt.Pid -ne $body.Pid) { throw 'Owned process identity mismatch.' }
        if ($body.PSObject.Properties.Name -contains 'Checkpoint') {
            $journals = @($body.CheckpointDisk.PSObject.Properties | Where-Object Name -Like '*commit-journal.json')
            if ($journals.Count -ne 1) { throw 'Controlled transaction kill lacks its exact journal.' }
            $expected = if ($body.Checkpoint -eq 'CommitDecided') { 'Committed' } else { 'Verified' }
            if ($journals[0].Value.Phase -cne $expected) { throw 'Checkpoint journal decision mismatch.' }
            if ($body.Checkpoint -eq 'ActiveWritten:r810-a') {
                $pointers = @($body.CheckpointDisk.PSObject.Properties | Where-Object Name -Like '*/active.json')
                if (($pointers[0].Value.Projects | ConvertTo-Json -Compress) -ceq ($pointers[1].Value.Projects | ConvertTo-Json -Compress)) { throw 'The intended inter-mirror interrupted state was not exercised.' }
                $mixedCheckpoints++
            }
        }
        continue
    }
    $normal++
    if (-not $body.Passed -or $receipt.ExitCode -ne 0 -or $body.AfterStopBundles -ne 0 -or
        @($body.Steps | Where-Object Name -CEQ 'normal-shutdown-clean').Count -ne 1) { throw "Normal shutdown gate missing: $($run.Name)" }
    if ($body.PSObject.Properties.Name -contains 'AfterStopProcessCng' -and @($body.AfterStopProcessCng.PSObject.Properties | Where-Object Value -NE 0).Count) { throw 'Actual process CNG counters did not drain.' }
    if (@($body.Steps | Where-Object Name -CEQ 'key-rejected').Count) { $negative++; continue }
    if ($body.BeforeStop.Summary.LeaseCount -ne 0 -or @($body.BeforeStop.Scenes).Count -ne 0) { throw "Business responsibility was not returned before Stop: $($run.Name)" }
    if ($body.PSObject.Properties.Name -contains 'AfterStopDiagnostics') {
        $terminal = $body.AfterStopDiagnostics
        if ($terminal.Summary.LeaseCount -ne 0 -or $terminal.Summary.ProducerCount -ne 0 -or $terminal.Summary.WaiterCount -ne 0 -or $terminal.Summary.RunningBundleCount -ne 0 -or $terminal.Summary.QueuedBundleCount -ne 0 -or $terminal.Summary.NativeBundleLoadCount -ne $terminal.Summary.NativeBundleUnloadCount -or @($terminal.Assets).Count -ne 0 -or @($terminal.Bundles).Count -ne 0) { throw 'Terminal shutdown did not drain the actual native work.' }
    }
    if ($body.PSObject.Properties.Name -contains 'Rejection') { $negative++ }
    if ($body.PSObject.Properties.Name -notcontains 'AfterDisk') { continue }
    foreach ($pointer in @($body.AfterDisk.PSObject.Properties | Where-Object Name -Like '*/active.json')) {
        foreach ($project in $body.AfterMounted) {
            $disk = @($pointer.Value.Projects | Where-Object ProjectId -CEQ $project.ProjectId)
            if ($disk.Count -ne 1 -or $disk[0].CatalogDigest -cne $project.Digest) { throw "Disk/memory closure mismatch: $($run.Name) / $($project.ProjectId)" }
        }
    }
}
if ($kills -lt 4 -or $mixedCheckpoints -ne 1 -or $negative -lt 5) { throw 'Required local recovery/key gates are incomplete.' }
if ($Final) {
    $requests = @(Get-Content -LiteralPath (Join-Path $root 'server-requests.jsonl') | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.PSObject.Properties.Name -contains 'run' })
    $encoded = @($requests | Where-Object run -CEQ 'review-green-encoding')
    if ($encoded.Count -ne 1 -or $encoded[0].status -ne 200 -or $encoded[0].url -notlike '*/release.json' -or $encoded[0].contentEncoding -cne 'gzip' -or $encoded[0].wireLength -ge $encoded[0].decodedLength) { throw 'Valid non-identity response rejection was not exercised.' }
    foreach ($case in @('range206','kill','ignore-range','changed-etag','bad-range','weak')) {
        $partial = Get-Content -LiteralPath (Join-Path $root ('resume-' + $case + '-before.json')) -Raw | ConvertFrom-Json
        if ($partial.Length -lt 1MB -or $partial.Length -ge $partial.Metadata.Length -or $partial.Metadata.State -cne 'Partial') { throw 'Resume evidence is not an actual incomplete transfer.' }
        $actual = @($requests | Where-Object run -CEQ ('range-' + $case + '-complete'))
        $ranged = @($actual | Where-Object { $null -ne $_.range })
        if ($case -eq 'weak') { if ($ranged.Count) { throw 'Weak validator used for range.' } }
        else {
            $status = if ($case -in @('ignore-range','changed-etag')) { 200 } else { 206 }
            if ($ranged.Count -ne 1 -or $ranged[0].range -cne "bytes=$($partial.Length)-" -or $ranged[0].ifRange -cne $partial.Metadata.ETag -or $ranged[0].status -ne $status) { throw 'Strong If-Range/real offset binding differs.' }
            if ($case -eq 'bad-range' -and @($actual | Where-Object { $_.url -ceq $ranged[0].url -and $_.status -eq 200 -and $null -eq $_.range }).Count -ne 1) { throw 'Invalid Content-Range was not restarted.' }
        }
    }
    $export = Get-Content -LiteralPath (Join-Path $root 'export-verification.json') -Raw | ConvertFrom-Json
    if (-not $export.Passed) { throw 'Exact export/dependency/license validation is missing.' }
    $releaseExport = Get-Content -LiteralPath (Join-Path $root 'export-verification-release.json') -Raw | ConvertFrom-Json
    if (-not $releaseExport.Passed -or $releaseExport.Sha256 -cne $export.Sha256) { throw 'Release PE/dependency/license and unchanged export identity must be checked.' }
    $location = Get-Content -LiteralPath (Join-Path $root 'carrier-location.json') -Raw | ConvertFrom-Json
    $release = Get-Content -LiteralPath (Join-Path $location.Carrier 'Builds/R1/BuildReport.json') -Raw | ConvertFrom-Json
    if ($release.Result -cne 'Succeeded' -or $release.Errors -ne 0 -or $release.Development -or $release.Options -cne 'None') { throw 'Actual non-Development Player build is missing.' }
    foreach ($id in @('production-default-tls','production-offline','production-encrypted','release-offline','release-encrypted','release-persistent')) {
        $result = Get-Content -LiteralPath (Join-Path $root ('Runs/' + $id + '/result.json')) -Raw | ConvertFrom-Json
        if (-not $result.Passed -or $result.LoopbackPinCompiled) { throw 'Ordinary Player still contains a test pin or failed.' }
    }
    $probe = Get-Content -LiteralPath (Join-Path $root 'watcher-probe-fixed/result.json') -Raw | ConvertFrom-Json
    if (-not $probe.Passed -or $probe.Replacements -ne 10000 -or $probe.PositivePlaintextDetections -lt 1) { throw 'Observer race fix regression is unverified.' }
    foreach ($source in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*ResourcePlayer*.ps1' -File) {
        $tokens = $null; $errors = $null
        $null = [System.Management.Automation.Language.Parser]::ParseFile($source.FullName,[ref]$tokens,[ref]$errors)
        if (@($errors).Count) { throw "PowerShell parse failure: $($source.Name)" }
        $parsedScripts++
    }
    $guids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($source in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*ResourcePlayer*' -File | Where-Object { -not $_.Name.EndsWith('.meta') }) {
        $meta = Get-Content -LiteralPath ($source.FullName + '.meta') -Raw
        if ($meta -notmatch '(?m)^guid:\s*([0-9a-f]{32})\s*\r?$' -or -not $guids.Add($Matches[1])) { throw "New acceptance asset/meta identity failure: $($source.Name)" }
        $metaFiles++
    }
    & node --check (Join-Path $PSScriptRoot 'ResourcePlayerAcceptanceServer.cjs')
    if ($LASTEXITCODE -ne 0) { throw 'HTTPS fixture source syntax failed.' }
    $editor = Get-Content -LiteralPath (Join-Path $root 'main-editor-final.json') -Raw | ConvertFrom-Json
    if ($editor.Playing -or $editor.Paused -or $editor.Compiling -or $editor.Updating -or $editor.CompilationFailed -or $editor.Dirty -or $editor.AuthorityPresent -or $editor.CoreProviderPresent -or $editor.NativeBundles -ne 0) { throw 'Main Editor terminal state is not clean and idle.' }
}
$protected = Get-Content -LiteralPath (Join-Path $root 'protected-before.json') -Raw | ConvertFrom-Json
$carrier = (Get-Content -LiteralPath (Join-Path $root 'carrier-location.json') -Raw | ConvertFrom-Json).Carrier
$owner = Get-Content -LiteralPath (Join-Path $carrier 'r810-owner.json') -Raw | ConvertFrom-Json
foreach ($file in $protected) {
    if ((Get-FileHash -LiteralPath (Join-Path $owner.Repository $file.Path) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.Sha256) { throw "Protected input changed: $($file.Path)" }
}
$summary = @{ NormalProcesses=$normal; ControlledKills=$kills; NegativeProcesses=$negative; InterMirrorCheckpoint=$mixedCheckpoints;
    RequiredProfileCases=$requiredRows.Count; ParsedPowerShellScripts=$parsedScripts; VerifiedNewMetaFiles=$metaFiles; PreservedFailuresAndRed=$preserved.ToArray(); InterruptedWithoutResult=$interruptions.ToArray(); ProtectedFileCount=@($protected).Count; Final=$Final.IsPresent; Passed=$true; Utc=[DateTime]::UtcNow.ToString('O') }
$name = if ($ReceiptName) { $ReceiptName } elseif ($Final) { 'verification-final.json' } else { 'verification-local.json' }
if ($name -notmatch '^[a-z0-9][a-z0-9-]*\.json$') { throw 'Use a new simple receipt filename, not a path.' }
if (Test-Path -LiteralPath (Join-Path $root $name)) { throw 'Never overwrite a verification receipt.' }
[IO.File]::WriteAllText((Join-Path $root $name), ($summary | ConvertTo-Json -Depth 6))
$summary | ConvertTo-Json -Compress
