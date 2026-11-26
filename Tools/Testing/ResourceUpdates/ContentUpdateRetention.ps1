#requires -Version 7.6
#requires -PSEdition Core

function Set-UpdateRetentionPin([string] $Directory, [object] $Evidence) {
    $path = Get-YLTestPath (Join-Path $Directory '.pinned')
    if (-not [IO.File]::Exists($path)) { Write-YLTestJson -Path $path -Value $Evidence }
}

function Start-UpdateHostRetention([object] $Context, [object] $Result, [bool] $KeepArtifacts) {
    $results = Get-UpdateOwnedPath $Result.path $Context.results
    $null = [IO.Directory]::CreateDirectory($results)
    $link = [ordered]@{ schemaVersion = 1; host = $Context.host.path; hostId = $Context.host.id
        resultId = $Result.id; keepArtifacts = $KeepArtifacts }
    Write-YLTestJson -Path (Join-Path $results 'host-context.json') -Value $link
    if ($KeepArtifacts) {
        Set-UpdateRetentionPin $Result.path $link
        Set-UpdateRetentionPin $Context.host.path $link
    }
}

function Save-UpdateHostEvidence([object] $Context, [bool] $Replay, [string] $Destination) {
    $root = $Context.host.path
    $files = @(@{ path = Join-Path $root 'Packages/manifest.json' })
    if ($Replay) {
        $build = $Context.report['buildReplay']
        $files += @{ path = $build['buildReportPath'] }
        $files += @{ path = $build['packageLockPath']; sha256 = $build['packageLockSha256'] }
        $files += @{ path = Join-Path $root '.yl-inputs.json' }
    } else {
        $files += @($Context.report.baselineEvidence.Values)
        $files += @{ path = $Context.report.buildA['baselineAnnouncementPath'] }
        foreach ($field in @('updateBuildLayout', 'updateCBuildLayout')) {
            if ($Context.report.Contains($field)) { $files += $Context.report[$field] }
        }
    }
    $hashes = [ordered]@{}
    foreach ($entry in $files) {
        $source = Get-UpdateOwnedPath $root $entry.path -File
        $relative = [IO.Path]::GetRelativePath($root, $source).Replace('\', '/')
        if ($relative -cnotmatch '^(?:(?:A|B|C|Replay|Packages)/|\.yl-inputs\.json$)') {
            throw "Unexpected host evidence path: $relative"
        }
        $hash = Get-YLFrozenFileSha256 $source
        if ($entry['sha256'] -and $hash -ine $entry['sha256']) { throw "Host evidence changed: $relative" }
        $copy = Get-UpdateOwnedPath $Context.results (Join-Path $Destination $relative)
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($copy))
        [IO.File]::Copy($source, $copy, $false)
        if ((Get-YLFrozenFileSha256 $copy) -cne $hash) { throw "Host evidence copy changed: $relative" }
        $hashes[$relative] = $hash
    }
    return $hashes
}

function Complete-UpdateHostRetention([object] $Workspace, [object] $Result, [object] $Context, [bool] $CanRetire) {
    $run = Get-YLTestDirectory $Workspace $Result.path
    $owned = Get-YLTestDirectory $Workspace $Context.host.path
    $results = Get-UpdateOwnedPath $run.path $Context.results
    $link = Read-YLPackageEvidenceJson (Get-YLTestPath (Join-Path $results 'host-context.json'))
    if ($link.schemaVersion -isnot [long] -or $link.schemaVersion -ne 1 -or $link.keepArtifacts -isnot [bool] -or
        $run.kind -cne 'Result' -or $run.id -cne $Result.id -or $run.id -cne $link.resultId -or
        $owned.kind -cne 'Host' -or $owned.key -cnotmatch '^R[UP][0-9a-f]{8}$' -or
        $owned.id -cne $Context.host.id -or $owned.id -cne $link.hostId -or $owned.path -ine $link.host) {
        throw 'Content update host/result identity mismatch.'
    }

    $reason = if ($link.keepArtifacts) { 'KeepArtifacts' } elseif (-not $CanRetire) { 'RunNotPassed' }
        elseif ([IO.Path]::Exists((Join-Path $owned.path '.pinned')) -or [IO.Path]::Exists((Join-Path $run.path '.pinned'))) {
            'ExistingPin'
        } else { 'PassedDiagnostic' }
    $retention = [ordered]@{ schemaVersion = 1; host = $owned.path; hostId = $owned.id; resultId = $run.id
        reason = $reason; hostStatus = 'Pinned' }
    try {
        if ($null -eq $Context.lease -or -not $Context.lease.CanWrite -or
            $Context.lease.Name -ine (Join-Path $owned.path '.lease')) {
            throw 'Content update finalization requires the original host lease.'
        }
        if ($reason -cne 'PassedDiagnostic') {
            Set-UpdateRetentionPin $run.path $retention
            Set-UpdateRetentionPin $owned.path $retention
        } else {
            Assert-YLUnityHostIdle $owned
            $replay = $owned.key.StartsWith('RP', [StringComparison]::Ordinal)
            $retention['evidencePath'] = Join-Path $results 'host-evidence'
            $retention['evidenceSha256'] = Save-UpdateHostEvidence $Context $replay $retention.evidencePath
            $retention.hostStatus = 'RemovalPending'
            Write-YLTestJson -Path (Join-Path $results 'retirement-intent.json') -Value $retention
            $arguments = @{ Workspace = $Workspace; Path = $owned.path; ExpectedId = $owned.id
                HeldLease = $Context.lease; Apply = $true }
            if (-not $replay) { $arguments['KeepEntries'] = @('.yl-inputs.json', 'PackageSources') }
            $removed = Remove-YLTestDirectory @arguments
            if ($removed.status -cnotin @('Pruned', 'Removed')) { throw 'Content update host cleanup was not applied.' }
            $retention.hostStatus = $removed.status
        }
    } catch {
        $retention.hostStatus = 'CleanupFailed'
        $retention['error'] = $_.Exception.Message
        throw
    } finally {
        $Context.report['retention'] = $retention
        Write-YLTestJson -Path (Join-Path $results 'retention.json') -Value $retention
    }
}
