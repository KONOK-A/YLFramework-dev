#requires -Version 7.6
#requires -PSEdition Core

function Get-UpdateTextSha256([string] $Text) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant()
}

function New-UpdateMaterial([string] $Ordinary = [Guid]::NewGuid().ToString('N'), [switch] $TransferProof, [switch] $BulkTransfer) {
    $asset = [Guid]::NewGuid().ToString('N')
    # Keep the actual fixture Bundle large enough for the intercepted body to flush into the receiver's staging file.
    $dependency = if ($BulkTransfer) { [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(8MB)) }
        elseif ($TransferProof) { [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(384KB)) }
        else { [Guid]::NewGuid().ToString('N') }
    return [ordered]@{
        asset = $asset; dependency = $dependency; ordinary = $Ordinary
        assetSha256 = Get-UpdateTextSha256 $asset; dependencySha256 = Get-UpdateTextSha256 $dependency
        ordinaryDependencySha256 = Get-UpdateTextSha256 $Ordinary
    }
}

function Assert-UpdateMaterial([object] $Observed, [object] $Expected, [string] $Label) {
    foreach ($field in @('assetSha256', 'dependencySha256', 'ordinaryDependencySha256')) {
        if ($null -eq $Observed -or $Observed[$field] -cne $Expected[$field]) {
            throw "$Label actual material differs in $field."
        }
    }
}

function Test-UpdateEndpoint([object] $Context) {
    $uri = [Uri]$Context.server.Prefix
    $client = [Net.Sockets.TcpClient]::new()
    try {
        try {
            $client.Connect($uri.Host, $uri.Port)
            return $true
        } catch {
            if ($_.Exception.GetBaseException() -isnot [Net.Sockets.SocketException]) { throw }
            return $false
        }
    } finally { $client.Dispose() }
}

function Set-UpdateOnline([object] $Context, [bool] $Online) {
    if ($Context.online -eq $Online) { return }
    if ($Online) { $Context.server.Resume() }
    else { $Context.server.Pause() }
    $Context.online = $Online
    if ((Test-UpdateEndpoint $Context) -ne $Online) { throw 'The actual content endpoint availability did not change.' }
}

function Invoke-UpdateRun {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object] $Context,
        [Parameter(Mandatory)][string] $Name,
        [ValidateSet('read', 'prepare', 'cleanup', 'rollback', 'retry', 'optional-cold', 'optional-prepare', 'optional-read',
            'scope-guard', 'missing-state')][string] $Operation = 'read',
        [string[]] $Outcome,
        [string] $Rejection,
        [string[]] $Boot = @('Active'),
        [string] $BootError,
        [ValidateSet('', 'locator', 'transform', 'concurrent')][string] $Preexisting = '',
        [switch] $LegacyCache,
        [switch] $Retained,
        [switch] $SceneAtlas,
        [ValidateSet('A', 'B', 'S', 'C')][string] $Active = 'A',
        [ValidateSet('A', 'B', 'S', 'C')][string] $Highest = 'A',
        [ValidateSet('', 'A', 'B', 'S')][string] $Previous = '',
        [switch] $Advance,
        [ValidateSet('normal', 'missing', 'wrong')][string] $K1 = 'normal',
        [ValidateSet('normal', 'missing', 'wrong')][string] $K2 = 'normal',
        [ValidateSet('', 'transfer', 'before-commit', 'after-commit', 'before-verified-publish', 'before-checkpoint-record')][string] $Stop = '',
        [string] $StopArtifactSha256,
        [switch] $ObserveBeforeReady,
        [switch] $ObserveTransfers,
        [string] $SpaceFaultRoute,
        [long] $SpaceBeforeBytes,
        [long] $SpaceWriteLimit,
        [string] $TransferCancelRoute,
        [long] $TransferCancelAfterBytes,
        [string] $ReclaimTestPart,
        [long] $SpaceAfterReclaimBytes,
        [string] $InspectStagingRoute,
        [string] $InspectStagingPath,
        [string] $HoldRejectedCheckpoint
    )
    $Context.report.phase = $Name
    Write-Host "$($Context.id) $Name"
    $Context.perRun++
    $prefix = Join-Path $Context.results ('{0:D2}-{1}' -f $Context.perRun, $Name)
    $requestPath = "$prefix-request.json"
    $resultPath = "$prefix-result.json"
    $stopMarker = "$prefix-marker.json"
    $k1File = switch ($K1) {
        normal { $Context.keys.k1Path }
        wrong { $Context.keys.wrongKeyPath }
        missing { '' }
    }
    $k2File = switch ($K2) {
        normal { $Context.keys.k2Path }
        wrong { $Context.keys.wrongKeyPath }
        missing { '' }
    }
    $legacyCatalog = ''
    if ($LegacyCache) {
        $manifest = $Context.versions.valid.manifest
        $catalog = $manifest.artifacts | Where-Object { $_.route -ceq $manifest.catalogRoute }
        $legacyCatalog = $Context.versions.artifacts[([Uri]$catalog.source).AbsolutePath].path
    }
    $request = [ordered]@{
        resultPath = $resultPath; userDataPath = $Context.userData; baseUrl = $Context.server.Prefix
        announcementRoute = 'announcement.json'; operation = $Operation
        stopAt = $(if ($ObserveBeforeReady) { 'before-ready' } elseif ($Stop -cne 'transfer') { $Stop } else { '' })
        stopMarkerPath = $stopMarker; encrypted = $Context.encrypted; k1Path = $k1File; k2Path = $k2File
        oldAddress = $Context.oldAddress; newAddress = $Context.newAddress; preexisting = $Preexisting
        legacyCacheCatalog = $legacyCatalog
    }
    if ($ObserveBeforeReady) { $request['releaseMarkerPath'] = "$prefix-release" }
    if ($StopArtifactSha256) {
        $stopArtifact = @($Context.versions.valid.manifest.artifacts | Where-Object sha256 -CEQ $StopArtifactSha256)
        if ($stopArtifact.Count -ne 1) { throw 'The forced checkpoint stop must bind to one expected artifact.' }
        $request['stopArtifactSha256'] = $StopArtifactSha256
        $request['stopArtifactLength'] = $stopArtifact[0].length
    }
    if ($ObserveTransfers) { $request['observeTransfers'] = $true }
    if ($ReclaimTestPart) {
        $request['reclaimTestPart'] = Get-UpdateOwnedPath $Context.userData $ReclaimTestPart -File
        $request['spaceBeforeBytes'] = $SpaceBeforeBytes
        $request['spaceAfterReclaimBytes'] = $SpaceAfterReclaimBytes
    }
    if ($InspectStagingRoute) { $request['inspectStagingRoute'] = $InspectStagingRoute }
    if ($InspectStagingPath) { $request['inspectStagingPath'] = Get-UpdateOwnedPath $Context.userData $InspectStagingPath -File }
    if ($HoldRejectedCheckpoint) { $request['holdRejectedCheckpoint'] = $HoldRejectedCheckpoint }
    if ($TransferCancelRoute) {
        $request['transferCancelRoute'] = $TransferCancelRoute
        $request['transferCancelAfterBytes'] = $TransferCancelAfterBytes
    }
    if ($SpaceFaultRoute) {
        $request['spaceFaultRoute'] = $SpaceFaultRoute
        $request['spaceBeforeBytes'] = $SpaceBeforeBytes
        $request['spaceWriteLimit'] = $SpaceWriteLimit
    }
    if ($Operation.StartsWith('optional-', [StringComparison]::Ordinal)) {
        $build = if ($Active -ceq 'C') { $Context.updateC } else { $Context.update }
        if ($Retained) {
            $request['retainedAddress'] = $build.retainedAddress
            $request['retainedRoutes'] = @($build.retainedRoutes)
        } else {
            $request['optionalAddress'] = $build.optionalAddress
            $request['optionalRoutes'] = @($build.optionalRoutes)
        }
    }
    if ($SceneAtlas) {
        foreach ($field in @('sceneAddress', 'sceneRootName', 'sceneObjectName', 'atlasAddress', 'atlasSpriteName')) {
            $request[$field] = $Context.baseline[$field]
            if (-not $request[$field]) { throw "Baseline fixture is missing $field." }
        }
    }
    Write-YLTestJson -Path $requestPath -Value $request
    $reachable = Test-UpdateEndpoint $Context
    if ($reachable -ne $Context.online) { throw 'The content service reachability contradicts the requested state.' }
    $preceding = @($Context.server.CaptureRoundAsync().GetAwaiter().GetResult())
    $start = New-UpdateProcess $Context.exe $Context.userData @('-batchmode', '-logFile', "$prefix-game.log", '-ylContentUpdateRequest', $requestPath)
    $processArguments = @{ StartInfo = $start; Log = "$prefix-process.log"; DescendantGraceMilliseconds = 45000
        TimeoutMilliseconds = $(if ($ObserveTransfers) { 440000 } else { 180000 }) }
    if ($ObserveBeforeReady) {
        $processArguments['KillWhen'] = {
            param($childId)
            if (-not [IO.File]::Exists($stopMarker) -or [IO.File]::Exists($request.releaseMarkerPath)) { return $null }
            try {
                $marker = Read-YLPackageEvidenceJson $stopMarker
                if ($marker.pid -ne $childId -or $marker.point -cne 'before-ready') { throw 'Wrong before-ready marker.' }
                $published = Get-UpdateOwnedPath $Context.userData $marker.path -File
                $target = @( $Context.versions.valid.manifest.artifacts | Where-Object {
                    $_.sha256 -ceq [IO.Path]::GetFileNameWithoutExtension($published) -and -not (Test-UpdateInstalledArtifact $_)
                } )
                if ($target.Count -lt 1 -or [IO.File]::Exists((Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($published))) 'journal'))) {
                    throw 'The gate did not hold the first publication of a signed candidate before journal creation.'
                }
                if ((Get-YLFrozenFileSha256 $published) -cne $target[0].sha256) { throw 'The object at the gate differed from the signed artifact.' }
                $observed = Invoke-UpdateCacheReader $Context 'while-writer-before-ready' $Context.userData
                if ($observed.pid -eq $childId -or $observed.generation -ne $marker.generation -or
                    $observed.activeDigest -cne $marker.activeDigest -or -not (Get-Process -Id $childId -ErrorAction SilentlyContinue)) {
                    throw 'The query did not observe the same installation generation while the writer was alive.'
                }
                $fileName = 'objects/' + [IO.Path]::GetFileName($published)
                $object = @($observed.cacheAfter.files | Where-Object relativePath -CEQ $fileName)
                if ($object.Count -ne 1 -or -not $object[0].ownershipConfirmed -or
                    $object[0].bytes -ne $target[0].length -or $object[0].disposition -cne 'Undetermined' -or
                    $observed.cacheAfter.cleanupCandidates.certainty -cne 'Unknown') {
                    throw 'The concurrent publication was not reported with its actual size and undecided retention.'
                }
                $Context.report['beforeReadyObservation'] = @{ writer = $marker; inspector = $observed.pid; object = $object[0] }
            } finally {
                [IO.File]::WriteAllText($request.releaseMarkerPath, 'observed')
            }
            return $null
        }
    } elseif ($Stop -eq 'transfer') {
        $transferRoute = '/' + $Context.update.transferRoute.TrimStart('/')
        $stagedBefore = @(Get-YLTestTreeFiles $Context.userData | Where-Object { [IO.Path]::GetFileName($_).StartsWith('.stage-') })
        $Context.server.InterruptNextUpdateTransfer($transferRoute)
        $processArguments['KillWhen'] = {
            param($childId)
            $requests = @($Context.server.Snapshot() | Select-Object -Skip $preceding.Count)
            $rows = @($requests | Where-Object { $_.target -ceq $transferRoute -and $_.held -and -not $_.completed -and $_.bytes -gt 0 })
            if ($rows.Count -eq 1) {
                $staged = @(Get-YLTestTreeFiles $Context.userData | Where-Object {
                    [IO.Path]::GetFileName($_).StartsWith('.stage-') -and $_ -cnotin $stagedBefore -and
                    [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($_)) -ceq 'objects'
                })
                if ($staged.Count -eq 1) {
                    $stagingBytes = [IO.FileInfo]::new($staged[0]).Length
                    if ($stagingBytes -gt 0) {
                        return @{ pid = $childId; point = 'transfer'; route = $rows[0].target; bytes = $rows[0].bytes
                            stagingPath = $staged[0]; stagingBytes = $stagingBytes }
                    }
                }
            }
            return $null
        }
    } elseif ($Stop) {
        $stateBefore = if ($Stop -ceq 'before-commit') { Get-UpdateStoreFile $Context 'state' } else { $null }
        $stateBeforeHash = if ($stateBefore) { Get-YLFrozenFileSha256 $stateBefore } else { $null }
        $processArguments['KillWhen'] = {
            param($childId)
            if (-not [IO.File]::Exists($stopMarker)) { return $null }
            $marker = Read-YLPackageEvidenceJson $stopMarker
            if ($marker.pid -ne $childId -or $marker.point -cne $Stop) { throw 'Wrong Player stop marker.' }
            if ($Stop -ceq 'before-commit') {
                $temporaryState = @(Get-UpdateStateTemporaries $Context)
                if ($temporaryState.Count -ne 1 -or [IO.FileInfo]::new($temporaryState[0]).Length -le 32 -or
                    (Get-YLFrozenFileSha256 $stateBefore) -cne $stateBeforeHash) {
                    throw 'The precommit marker did not leave one durable temporary record and the original committed state.'
                }
                $marker['temporaryStatePath'] = $temporaryState[0]
                $marker['temporaryStateSha256'] = Get-YLFrozenFileSha256 $temporaryState[0]
                $marker['committedStateSha256'] = $stateBeforeHash
            } elseif ($Stop -cin @('before-verified-publish', 'before-checkpoint-record')) {
                $part = Get-UpdateOwnedPath $Context.userData $marker.path -File
                $beforeRecord = $Stop -ceq 'before-checkpoint-record'
                $artifact = $stopArtifact[0]
                $checkpoint = Read-UpdateCheckpoint $Context $artifact -AllowUncommitted:$beforeRecord
                if ($checkpoint.path -cne $part -or $checkpoint.partBytes -ne $artifact.length -or
                    $marker.offset -ne $artifact.length -or [IO.File]::Exists($checkpoint.published) -or
                    ($beforeRecord -and $checkpoint.offset -ge $artifact.length) -or
                    (-not $beforeRecord -and $checkpoint.offset -ne $artifact.length)) {
                    throw 'The Player did not reach the requested fully written checkpoint boundary.'
                }
                $checkpoint['markerSha256'] = Get-YLFrozenFileSha256 $checkpoint.marker
                $marker['checkpoint'] = $checkpoint
            }
            return $marker
        }
    }
    $process = Invoke-YLTestProcess @processArguments
    $row = [ordered]@{
        name = $Name; operation = $Operation; k1 = $K1; k2 = $K2; expectedStop = $Stop
        online = $reachable; process = $process; results = $null; requests = @(); status = 'Failed'
    }
    $Context.report.runs.Add($row)
    $progressPath = Join-Path $Context.results 'runs.json'
    Write-UpdateProgress $progressPath $Context.report.runs
    $row.requests = @($Context.server.CaptureRoundAsync().GetAwaiter().GetResult() | Select-Object -Skip $preceding.Count)
    if (-not $reachable -and $row.requests.Count -ne 0) { throw 'An offline Player reached the stopped content service.' }
    if ($reachable -and $Operation -ceq 'prepare' -and
        -not @($row.requests | Where-Object { $_.target -ceq '/announcement.json' -and $_.status -eq 200 -and $_.completed }).Count) {
        throw 'The Player did not receive the planned online announcement.'
    }
    if ($Stop) {
        if ($process.status -cne 'ObservationKill' -or -not $process.observedExit -or $process.exitCode -eq 0 -or
            $process.forcedCleanup -or -not $process.streamsDrained -or $process.observerError -or $process.cleanupError -or
            -not $process.jobAssigned -or $process.activeProcesses -ne 0 -or $process.timedOut -or $process.cancelled -or
            $process.killObservation.pid -ne $process.pid -or $process.killObservation.point -cne $Stop -or
            [IO.File]::Exists($resultPath)) {
            throw "The exact Player was not forcibly terminated at $Stop with complete exit evidence."
        }
        if ($Stop -ceq 'transfer') {
            $held = @($row.requests | Where-Object { $_.target -ceq $transferRoute -and $_.held })
            $artifact = $Context.versions.artifacts[$transferRoute]
            if ($held.Count -ne 1 -or $held[0].completed -or $held[0].bytes -ne $process.killObservation.bytes -or
                $held[0].bytes -ge $artifact.bytes -or $process.killObservation.stagingBytes -gt $held[0].bytes) {
                throw 'The forced transfer stop did not preserve a partial required response.'
            }
        } elseif ($Stop -ceq 'before-commit') {
            $temporary = $process.killObservation.temporaryStatePath
            if ((Get-YLFrozenFileSha256 $temporary) -cne $process.killObservation.temporaryStateSha256 -or
                (Get-YLFrozenFileSha256 $stateBefore) -cne $stateBeforeHash) {
                throw 'Terminating the Player before the swap did not preserve the observed temporary and committed records.'
            }
            $Context.report['precommitTemporary'] = $process.killObservation
        } elseif ($Stop -cin @('before-verified-publish', 'before-checkpoint-record')) {
            $full = $process.killObservation.checkpoint
            if ((Get-YLFrozenFileSha256 $full.path) -cne $full.sha256 -or [IO.File]::Exists($full.published) -or
                (Get-YLFrozenFileSha256 $full.marker) -cne $full.markerSha256 -or
                [YLTesting.UpdateFileEvidence]::Read($full.path).Key -cne $full.fileIdentity) {
                throw 'The stage must survive process death before the published artifact exists.'
            }
        }
        $row.status = 'Passed'
        Write-UpdateProgress $progressPath $Context.report.runs
        return
    }
    if ([IO.File]::Exists($resultPath)) { $row.results = Read-YLPackageEvidenceJson $resultPath }
    $observed = $row.results
    try { Assert-YLTestProcessSucceeded $process } catch {
        $detail = if ($null -ne $observed -and $observed['error']) { ($observed['error'] -split '\r?\n', 2)[0] } else { $process.status }
        throw "Player round $Name failed: $detail"
    }
    if ($null -eq $observed -or $observed.status -cne 'Passed' -or $observed.pid -ne $process.pid) {
        throw 'The Player did not report material and state observations for its actual process.'
    }
    Assert-YLFrozenPlayerBackend -Row $observed -Expected $Context.backend
    if ($observed.bootStatus -cnotin $Boot -or ($Outcome -and $observed.operationStatus -cnotin $Outcome)) {
        throw "Unexpected Player operation outcome: boot=$($observed.bootStatus), operation=$($observed.operationStatus)."
    }
    if ($Rejection -and -not ([string]$observed.operationDetail).StartsWith($Rejection + ' ', [StringComparison]::Ordinal)) {
        throw "Unexpected announcement rejection category: $($observed.operationDetail)."
    }
    if ($BootError -and $observed.bootError -cne $BootError) {
        throw "Unexpected candidate startup failure category: $($observed.bootError)."
    }
    if ($LegacyCache -and $observed.legacyCacheSha256 -cne $Context.update.catalogArtifactSha256) {
        throw 'The Player did not prove the existing official cache contained a real but irrelevant B Catalog.'
    }
    if ($Operation -ceq 'missing-state') {
        if ($observed.bootDetail -cnotlike '*committed resource content state is missing*') {
            throw 'The startup rejection did not identify the missing committed record.'
        }
        $row.status = 'Passed'
        Write-UpdateProgress $progressPath $Context.report.runs
        return $observed
    }
    $sequence = @{ A = 10; B = 11; S = 13; C = 14 }
    $digests = @{ A = $Context.baseline.baselineDigest }
    if ($null -ne $Context.versions -and $Context.versions.Contains('valid')) {
        $digests['B'] = $Context.versions.valid.digest
        $digests['S'] = $Context.versions.successor.digest
        if ($Context.versions.Contains('cValid')) { $digests['C'] = $Context.versions.cValid.digest }
    }
    $previousSequence = if ($Previous) { $sequence[$Previous] } else { 0 }
    $previousDigest = if ($Previous) { $digests[$Previous] } else { $null }
    if ($observed.activeSequence -ne $sequence[$Active] -or $observed.activeDigest -cne $digests[$Active] -or
        $observed.highestSequence -ne $sequence[$Highest] -or $observed.highestDigest -cne $digests[$Highest] -or
        $observed.previousSequence -ne $previousSequence -or [string]$observed.previousDigest -cne [string]$previousDigest) {
        throw 'The persisted active, previous or highest sequence/digest differs.'
    }
    if ($observed.generation -isnot [long] -or $observed.generation -lt 1 -or
        ($null -ne $Context.generation -and $observed.generation -ne ($Context.generation + [int][bool]$Advance))) {
        throw 'The actual commit generation did not match the expected transition.'
    }
    $Context.generation = $observed.generation
    if ($Preexisting -cin @('locator', 'transform')) {
        $row.status = 'Passed'
        Write-UpdateProgress $progressPath $Context.report.runs
        return $observed
    }
    $old = switch ($Active) {
        A { $Context.materialA }
        C { $Context.materialReplacementC }
        default { $Context.materialReplacement }
    }
    $new = if ($Active -ceq 'C') { $Context.materialNewC } else { $Context.materialNew }
    Assert-UpdateMaterial $observed.old $old 'Same-address asset'
    if ($observed.new.found -isnot [bool] -or $observed.new.found -ne ($Active -cne 'A')) {
        throw 'The exact new chapter address visibility differs.'
    }
    if ($Active -cne 'A') { Assert-UpdateMaterial $observed.new $new 'Chapter asset' }
    if ($Operation -cin @('prepare', 'optional-prepare')) {
        $held = if ($Operation -ceq 'optional-prepare' -and $Active -cne 'A') { $new } else { $old }
        Assert-UpdateMaterial $observed.held.before $held 'Held lease before prepare'
        Assert-UpdateMaterial $observed.held.after $held 'Held lease after prepare'
        if ($observed.held.newFound -isnot [bool] -or $observed.held.newFound -ne ($Active -cne 'A')) {
            throw 'The live session gained or lost the exact new chapter address during prepare.'
        }
        if ($Context.encrypted -and $Operation -ceq 'prepare' -and
            ($observed.prepareKeyRents -ne 0 -or $observed.prepareMountAttempts -ne 0 -or $observed.prepareKeyRequests -ne 0)) {
            throw 'The encrypted preparation acquired a content key or mounted a bundle.'
        }
    }
    if ($Operation.StartsWith('optional-', [StringComparison]::Ordinal)) {
        Assert-UpdateOptionalRun $Context $row $Active -Retained:$Retained
    }
    if ($Operation -ceq 'scope-guard') {
        $guard = $observed.scopeGuard
        if ($guard.error -cne 'ConfigurationConflict' -or $guard.pendingWorkBefore -le 0 -or $guard.pendingWorkAfter -ne 0 -or
            $guard.closed -ne $true -or $guard.stopped -ne $true) {
            throw 'A late foreign Addressables change did not reject the new scope request and drain the legitimate work.'
        }
        Assert-UpdateMaterial $guard.control $old 'Control scope'
    }
    if ($SceneAtlas) { Assert-UpdateSceneAtlas $Context $observed }
    if ($Context.encrypted -and ($observed.startupMountAttempts -ne 0 -or $observed.keyRents -ne $observed.keyReturns -or
        $observed.authenticatedBundles -le 0 -or $observed.mountAttempts -le 0 -or
        ($Active -ceq 'A' -and $observed.key1Requests -le 0) -or ($Active -cne 'A' -and $observed.key2Requests -le 0))) {
        throw 'The actual encrypted session did not authenticate, mount and release its owned key material in the expected order.'
    }
    if ($BootError -and (($K1 -cne 'normal' -and $observed.key1Requests -le 0) -or
        ($K2 -cne 'normal' -and $observed.key2Requests -le 0))) {
        throw 'The encrypted key failure did not request the intended fixture key identity.'
    }
    $row.status = 'Passed'
    Write-UpdateProgress $progressPath $Context.report.runs
    return $observed
}

function New-UpdateVerificationKey([object] $Context) {
    $key = Read-YLPackageEvidenceJson $Context.keys.signingPublicKeyPath
    $public = [Security.Cryptography.RSAParameters]::new()
    $public.Modulus = [Convert]::FromBase64String($key.Modulus)
    $public.Exponent = [Convert]::FromBase64String($key.Exponent)
    $rsa = [Security.Cryptography.RSA]::Create()
    try {
        $rsa.ImportParameters($public)
        return $rsa
    } catch {
        $rsa.Dispose()
        throw
    }
}

function Read-UpdateAnnouncement([string] $Path, [Security.Cryptography.RSA] $Rsa) {
    $envelope = Read-YLPackageEvidenceJson $Path
    $payload = [Convert]::FromBase64String($envelope.payload)
    $signature = [Convert]::FromBase64String($envelope.signature)
    $signatureOffset = 5 + $signature[4]
    if ($signature.Length -ne $signatureOffset + 384 -or [Text.Encoding]::ASCII.GetString($signature, 0, 4) -cne 'YLR1') {
        throw "Invalid fixture detached signature envelope: $Path"
    }
    $sha = [Security.Cryptography.SHA256]::HashData($payload)
    return [ordered]@{
        path = $Path
        manifest = ConvertFrom-Json -InputObject ([Text.UTF8Encoding]::new($false, $true).GetString($payload)) -AsHashtable -DateKind String
        digest = [Convert]::ToHexString($sha).ToLowerInvariant()
        keyId = [Text.Encoding]::ASCII.GetString($signature, 5, $signature[4])
        signedByFixture = $Rsa.VerifyHash($sha, [byte[]]($signature[$signatureOffset..($signature.Length - 1)]),
            [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    }
}

function Test-UpdateInstalledArtifact([object] $Artifact) {
    $Artifact.source.StartsWith('install:', [StringComparison]::Ordinal)
}

function Assert-UpdatePaths([string[]] $Expected, [string[]] $Actual, [string] $Label) {
    if ($Expected.Count -ne $Actual.Count -or
        (@($Expected | Sort-Object -CaseSensitive) -join "`n") -cne (@($Actual | Sort-Object -CaseSensitive) -join "`n")) {
        throw "$Label differs. Expected [$Expected], received [$Actual]."
    }
}

function Get-UpdateArtifactClosure([object] $Manifest, [string[]] $Roots) {
    $byRoute = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($artifact in $Manifest.artifacts) { $byRoute.Add($artifact.route, $artifact) }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $queue = [Collections.Generic.Queue[string]]::new()
    foreach ($route in $Roots) {
        if ($route) { $queue.Enqueue($route) }
    }
    while ($queue.Count -gt 0) {
        $route = $queue.Dequeue()
        if (-not $seen.Add($route)) { continue }
        $artifact = $byRoute[$route]
        if ($null -eq $artifact) { throw "Missing signed artifact $route." }
        $artifact
        foreach ($dependency in $artifact.dependencies) { $queue.Enqueue($dependency) }
    }
}

function Register-UpdateFileRoutes([object] $Context, [string] $ArtifactRoot, [object[]] $Entries) {
    $root = Get-UpdateOwnedPath $Context.host.path $ArtifactRoot
    $files = $Context.versions.artifacts
    foreach ($entry in $Entries) {
        $path = Get-UpdateOwnedPath $root $entry.path -File
        $relative = [IO.Path]::GetRelativePath($root, $path).Replace('\', '/')
        $route = '/' + $entry.route.TrimStart('/')
        if ($route -ceq '/announcement.json') { throw 'An immutable artifact cannot occupy the announcement endpoint.' }
        $artifact = @{ path = $path; bytes = [IO.FileInfo]::new($path).Length; sha256 = Get-YLFrozenFileSha256 $path }
        if ($files.ContainsKey($route)) {
            $existing = $files[$route]
            if ($existing.bytes -ne $artifact.bytes -or $existing.sha256 -cne $artifact.sha256) {
                throw "An immutable endpoint changed its published bytes: $route."
            }
            continue
        }
        $files.Add($route, $artifact)
        $Context.server.AddUpdateArtifact($root, $route, $relative)
    }
}

function Assert-UpdateManifestArtifacts([object] $Context, [object] $Announcement, [object] $Baseline) {
    foreach ($artifact in $Announcement.manifest.artifacts) {
        if (Test-UpdateInstalledArtifact $artifact) {
            if (-not @($Baseline.manifest.artifacts | Where-Object {
                $_.route -ceq $artifact.route -and $_.source -ceq $artifact.source -and
                $_.originReleaseId -ceq $artifact.originReleaseId -and $_.kind -ceq $artifact.kind -and
                $_.sha256 -ceq $artifact.sha256 -and $_.length -eq $artifact.length
            }).Count) { throw "The announcement uses an undeclared installed artifact: $($artifact.route)." }
            continue
        }
        $source = [Uri]$artifact.source
        $onCdn = $Context.versions.artifacts[$source.AbsolutePath]
        if (-not $source.AbsoluteUri.StartsWith($Context.server.Prefix, [StringComparison]::Ordinal) -or
            $null -eq $onCdn -or $onCdn.sha256 -cne $artifact.sha256 -or $onCdn.bytes -ne $artifact.length) {
            throw "The announcement does not bind actual published bytes: $($artifact.route)."
        }
    }
}

function Register-UpdateArtifacts([object] $Context) {
    if ($Context.versions.Contains('valid')) { return }
    $build = $Context.update
    $baseline = $Context.baseline
    if ($build.contentStateSha256 -cne $baseline.contentStateSha256 -or
        (Get-YLFrozenFileSha256 $baseline.contentStatePath) -cne $baseline.contentStateSha256) {
        throw 'The content-only build did not consume the same official A state archive.'
    }
    if ($build.newAddress -cne $Context.newAddress -or $build.newGuid -cnotmatch '^[0-9a-f]{32}$' -or
        $build.newDependencyGuid -cnotmatch '^[0-9a-f]{32}$' -or
        @($baseline.oldGuid, $baseline.oldDependencyGuid, $build.newGuid, $build.newDependencyGuid | Sort-Object -Unique).Count -ne 4) {
        throw 'The new chapter and its dependency are not distinct post-seal source assets.'
    }
    $layout = Get-UpdateOwnedPath $Context.host.path $build.buildLayoutPath -File
    if ([IO.FileInfo]::new($layout).Length -eq 0) { throw 'The B build layout is empty.' }
    $Context.report['updateBuildLayout'] = [ordered]@{ path = $layout; sha256 = Get-YLFrozenFileSha256 $layout }
    $artifactRoot = Get-UpdateOwnedPath $Context.host.path (Join-Path $Context.host.path 'B')
    Register-UpdateFileRoutes $Context $artifactRoot $build.files
    $rsa = New-UpdateVerificationKey $Context
    try {
        $a = $Context.report.baselineAnnouncement
        $expectedSequences = [ordered]@{ valid = 11; high = 12; stale = 10; conflict = 11; successor = 13 }
        $variants = @('valid', 'high', 'stale', 'conflict', 'tampered', 'unknown', 'wrongChannel', 'expired', 'successor')
        if ($Context.encrypted) { $variants += @('wrongCatalog', 'wrongCipher') }
        foreach ($variant in $variants) {
            $path = Get-UpdateOwnedPath $artifactRoot $build.announcements[$variant] -File
            $version = Read-UpdateAnnouncement $path $rsa
            if ($version.signedByFixture -ne ($variant -cnotin @('tampered', 'unknown'))) {
                throw "Unexpected signature verification result for $variant."
            }
            if ($expectedSequences.Contains($variant) -and $version.manifest.sequence -ne $expectedSequences[$variant]) {
                throw "Unexpected release sequence for $variant."
            }
            $version['artifactRoot'] = $artifactRoot
            $Context.versions[$variant] = $version
            Assert-UpdateManifestArtifacts $Context $version $a
        }
        $valid = $Context.versions.valid
        $transfer = '/' + $build.transferRoute.TrimStart('/')
        $catalog = @($valid.manifest.artifacts | Where-Object { $_.route -ceq $valid.manifest.catalogRoute })
        $dependencies = @($valid.manifest.artifacts | Where-Object { $_.sha256 -ceq $build.dependencyArtifactSha256 })
        $tampered = $Context.versions.tampered
        $alteredDependency = @($tampered.manifest.artifacts | Where-Object { $_.route -ceq $dependencies[0].route })
        if ($Context.versions.stale.digest -cne $a.digest -or $Context.versions.conflict.digest -ceq $valid.digest -or
            $Context.versions.unknown.keyId -ceq $a.keyId -or $Context.versions.wrongChannel.manifest.channel -ceq $a.manifest.channel -or
            [DateTimeOffset]::Parse($Context.versions.expired.manifest.expiresAtUtc) -ge [DateTimeOffset]::UtcNow -or
            $catalog.Count -ne 1 -or $catalog[0].kind -cne 'Catalog' -or $catalog[0].sha256 -cne $build.catalogArtifactSha256 -or
            $valid.manifest.catalogRoute -cne $a.manifest.catalogRoute -or $valid.manifest.settingsSha256 -cne $a.manifest.settingsSha256 -or
            $valid.keyId -cne $a.keyId -or $Context.versions.unknown.digest -cne $valid.digest -or
            $dependencies.Count -ne 1 -or $dependencies[0].source -cne ($Context.server.Prefix + $transfer.TrimStart('/')) -or
            $alteredDependency.Count -ne 1 -or $alteredDependency[0].sha256 -ceq $dependencies[0].sha256 -or
            $dependencies[0].route -cnotin @($valid.manifest.artifacts | ForEach-Object { $_.dependencies })) {
            throw 'The update variants do not target the intended sequence, issuer, expiration, channel or transfer.'
        }
        if ($Context.encrypted -and
            (-not @($valid.manifest.artifacts | Where-Object { $_.kind -ceq 'Bundle' }).Count -or
             -not @($valid.manifest.artifacts | Where-Object { $_.kind -ceq 'Ciphertext' }).Count)) {
            throw 'An encrypted update must include protected content and an actual ordinary dependency.'
        }
        $successor = $Context.versions.successor
        $validIdentity = ($valid.manifest | ConvertTo-Json -Depth 12 -Compress)
        $successorIdentity = ($successor.manifest | ConvertTo-Json -Depth 12 -Compress)
        if ($successor.digest -ceq $valid.digest -or
            $successorIdentity.Replace('"sequence":13,', '"sequence":11,') -cne $validIdentity) {
            throw 'The renewed B announcement must bind the same release, routes, and physical artifact bytes.'
        }
        Assert-UpdateOptionalInventory $Context $build $valid $Context.materialOptional
    } finally { $rsa.Dispose() }
    $Context.report['updateInventory'] = $Context.versions
    $Context.report['updateDigest'] = $Context.versions.valid.digest
}

function Publish-UpdateAnnouncement([object] $Context, [string] $Variant) {
    $version = $Context.versions[$Variant]
    $directory = $version.artifactRoot
    $relative = [IO.Path]::GetRelativePath($directory, $version.path).Replace('\', '/')
    $Context.server.PublishUpdateAnnouncement($directory, '/announcement.json', $relative)
}

function Assert-UpdateSceneAtlas([object] $Context, [object] $Observed) {
    $baseline = $Context.baseline
    $scene = $Observed.scene
    $atlas = $Observed.atlas
    $spriteAddress = $baseline.atlasAddress + '[' + $baseline.atlasSpriteName + ']'
    if ($scene.rootName -cne $baseline.sceneRootName -or $scene.textSha256 -cne $baseline.sceneTextSha256 -or
        $scene.unloaded -ne $true -or $atlas.spriteName -cne $baseline.atlasSpriteName -or
        $atlas.spriteAddress -cne $spriteAddress -or $atlas.width -ne $baseline.atlasWidth -or
        $atlas.height -ne $baseline.atlasHeight) {
        throw 'The offline installed scene or SpriteAtlas did not load its measured runtime content and release the scene.'
    }
}

function Assert-UpdateRetainedOrdinary([object] $Context, [object] $Build, [object] $Version, [object[]] $Startup) {
    $retained = @(Get-UpdateArtifactClosure $Version.manifest @($Build.retainedRoutes))
    $authoredPrefix = $Context.server.Prefix + $Version.manifest.releaseId + '/ordinary/'
    if ($retained.Count -ne 1 -or $retained[0].kind -cne 'Bundle' -or
        (Test-UpdateInstalledArtifact $retained[0]) -or $retained[0].route -cin @($Startup.route) -or
        $retained[0].source -cne $Build.retainedSource -or $retained[0].sha256 -cne $Build.retainedArtifactSha256 -or
        $Build.retainedAuthoredLoadPrefix -cne $authoredPrefix -or
        -not $retained[0].source.StartsWith(($Context.server.Prefix + 'A/ordinary/'), [StringComparison]::Ordinal) -or
        $retained[0].originReleaseId -cne 'A' -or $Build.retainedOriginReleaseId -cne 'A' -or
        $Build.retainedGuid -cnotmatch '^[0-9a-f]{32}$' -or $Build.retainedAssetSha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw 'The reusable A ordinary Bundle must retain its original optional CDN identity.'
    }
    $baseline = $Context.baseline
    foreach ($name in @('retainedAddress', 'retainedGuid', 'retainedAssetSha256', 'retainedArtifactSha256', 'retainedSource')) {
        if ($Build[$name] -cne $baseline[$name]) { throw "The reusable A ordinary Bundle changed in $name." }
    }
    Assert-UpdatePaths @($baseline.retainedRoutes) @($Build.retainedRoutes) 'Retained ordinary signed route'
    return $retained[0]
}

function Assert-UpdateOptionalInventory([object] $Context, [object] $Build, [object] $Version, [object] $Material) {
    $manifest = $Version.manifest
    $startup = @(Get-UpdateArtifactClosure $manifest (@($manifest.catalogRoute, $manifest.descriptionRoute) + $manifest.startupRoutes))
    $roots = @($Build.optionalRoutes)
    $closure = @(Get-UpdateArtifactClosure $manifest $roots)
    $remote = @($closure | Where-Object { -not (Test-UpdateInstalledArtifact $_) -and $_.route -cnotin $startup.route })
    $sourceGuids = @($Context.baseline.oldGuid, $Context.baseline.oldDependencyGuid, $Build.newGuid, $Build.newDependencyGuid,
        $Build.optionalGuid, $Build.optionalDependencyGuid)
    if ($Build.optionalAddress -cne $Context.optionalAddress -or $Build.optionalSourceAssetSha256 -cne $Material.assetSha256 -or
        $Build.optionalSourceDependencySha256 -cne $Material.dependencySha256 -or
        @($sourceGuids | Where-Object { $_ -cnotmatch '^[0-9a-f]{32}$' }).Count -ne 0 -or
        @($sourceGuids | Sort-Object -Unique).Count -ne 6 -or $roots.Count -ne 1 -or $remote.Count -lt 2 -or
        $roots[0] -cnotin $remote.route -or
        @($remote | Where-Object { $_.originReleaseId -cne $manifest.releaseId }).Count -ne 0 -or
        -not @($remote | Where-Object { $_.route -cnotin $roots }).Count) {
        throw 'The optional chapter must use distinct remote content and a transitive dependency outside startup.'
    }
    Assert-UpdatePaths $remote.route @($Build.optionalClosure) 'Optional remote signed closure'
    if ($Context.encrypted) { $null = Assert-UpdateRetainedOrdinary $Context $Build $Version $startup }
}

function Assert-UpdateOptionalRun([object] $Context, [object] $Row, [string] $Active, [switch] $Retained) {
    $version = switch ($Active) {
        S { $Context.versions.successor }
        C { $Context.versions.cValid }
        default { $Context.versions.valid }
    }
    $build = if ($Active -ceq 'C') { $Context.updateC } else { $Context.update }
    $roots = if ($Retained) { @($build.retainedRoutes) } else { @($build.optionalRoutes) }
    $manifest = $version.manifest
    $startup = @(Get-UpdateArtifactClosure $manifest (@($manifest.catalogRoute, $manifest.descriptionRoute) + $manifest.startupRoutes))
    $closure = @(Get-UpdateArtifactClosure $manifest $roots | Where-Object {
        -not (Test-UpdateInstalledArtifact $_) -and $_.route -cnotin $startup.route
    })
    $cdnPaths = @($closure | ForEach-Object { ([Uri]$_.source).AbsolutePath })
    [long]$bytes = 0
    foreach ($artifact in $closure) { $bytes += $artifact.length }
    $observed = $Row.results.optional
    Assert-UpdatePaths @($roots) @($observed.routes) 'Player optional root routes'
    Assert-UpdatePaths $closure.route @($observed.closure) 'Player optional signed dependency routes'
    if ($observed.manifestDigest -cne $version.digest -or $bytes -le 0 -or $observed.expectedSize -ne $bytes) {
        throw 'The optional estimate evidence does not bind the frozen manifest and its published bytes.'
    }
    if ($Row.operation -cin @('optional-cold', 'optional-prepare')) {
        if ($observed.sizeBefore -ne $bytes -or $observed.coldError -cnotin @('SourceUnavailable', 'LoadFailed') -or
            $observed.coldDownloadError -cne 'SourceUnavailable' -or -not $observed.coldDetail) {
            throw 'Unprepared optional content did not report its exact transfer size and reject the cold read and stock download.'
        }
    }
    if ($Row.operation -ceq 'optional-cold') {
        if ($Row.online -or $Row.requests.Count -ne 0) { throw 'The optional cold read must run offline.' }
        return
    }
    if ($Row.operation -ceq 'optional-prepare') {
        if (-not $Row.online -or $observed.sizeAfter -ne 0) {
            throw 'Optional preparation did not finish online with all required files available.'
        }
        if ($Context.encrypted -and ($observed.prepareKeyRequests -ne 0 -or
            $observed.prepareKeyRents -ne 0 -or $observed.prepareMountAttempts -ne 0)) {
            throw 'Encrypted optional preparation acquired keys or mounted a Bundle.'
        }
        Assert-UpdatePaths $cdnPaths @($Row.requests.target) 'Actual optional download paths'
        foreach ($request in $Row.requests) {
            if ($request.method -cne 'GET' -or $request.status -ne 200 -or -not $request.completed -or $request.held -or
                $request.bytes -ne $Context.versions.artifacts[$request.target].bytes) {
                throw "Optional preparation did not transfer the full signed object at $($request.target)."
            }
        }
    } elseif ($Row.online -or $Row.requests.Count -ne 0 -or $observed.sizeAfter -ne 0) {
        throw 'Warm optional content did not report zero bytes and read during an offline restart.'
    }
    if ($Retained) {
        if ($observed.assetSha256 -cne $build.retainedAssetSha256) { throw 'The reused A ordinary text did not match.' }
    } else {
        $expected = if ($Active -ceq 'C') { $Context.materialOptionalC } else { $Context.materialOptional }
        Assert-UpdateMaterial $observed.material $expected 'Optional chapter'
    }
}

function Get-UpdateStoreFile([object] $Context, [string] $Name) {
    $root = Join-Path $Context.userData 'YLResourceContent'
    $matches = @(Get-YLTestTreeFiles $root | Where-Object { [IO.Path]::GetFileName($_) -ceq $Name })
    if ($matches.Count -ne 1) { throw "Expected one owned content store file named $Name." }
    return $matches[0]
}

function Get-UpdateStateTemporaries([object] $Context) {
    $root = Join-Path $Context.userData 'YLResourceContent'
    Get-YLTestTreeFiles $root | Where-Object { [IO.Path]::GetFileName($_) -cmatch '^\.state-[0-9a-f]{32}\.tmp$' }
}

function Invoke-UpdateMissingStateScenario([object] $Context) {
    $originalData = $Context.userData
    $originalGeneration = $Context.generation
    $sourceJournal = Get-UpdateStoreFile $Context 'journal'
    $journalBytes = [IO.File]::ReadAllBytes($sourceJournal)
    $presenceFields = $journalBytes.Length - 32 - 4
    if ($presenceFields -le 4 -or [BitConverter]::ToInt32($journalBytes, 0) -ne 1 -or
        [BitConverter]::ToInt32($journalBytes, $presenceFields) -ne 0) {
        throw 'The successful postcommit recovery did not leave the original v1 empty journal.'
    }
    $journalHash = Get-YLFrozenFileSha256 $sourceJournal
    $Context.userData = Join-Path $Context.host.path 'MissingState'
    $Context.generation = $null
    try {
        $null = [IO.Directory]::CreateDirectory($Context.userData)
        $null = Invoke-UpdateRun $Context 'missing-state-control' -Boot Baseline
        $state = Get-UpdateStoreFile $Context 'state'
        $journal = Join-Path ([IO.Path]::GetDirectoryName($state)) 'journal'
        [IO.File]::Copy($sourceJournal, $journal)
        if ((Get-YLFrozenFileSha256 $journal) -cne $journalHash) { throw 'The isolated journal differs from the genuine crash recovery output.' }
        $Context.report['missingState'] = [ordered]@{
            userData = $Context.userData; removedState = $state; removedStateSha256 = Get-YLFrozenFileSha256 $state
            journal = $journal; sourceJournal = $sourceJournal; journalSha256 = $journalHash
        }
        [IO.File]::Delete($state)
        $null = Invoke-UpdateRun $Context 'reject-missing-committed-state' -Operation missing-state -Boot StateRejected -BootError LoadFailed -Outcome StateRejected
        if ([IO.File]::Exists($state) -or (Get-YLFrozenFileSha256 $journal) -cne $journalHash) {
            throw 'Rejected startup rewrote the missing state or its journal.'
        }
    } finally {
        $Context.userData = $originalData
        $Context.generation = $originalGeneration
    }
}

function Get-UpdateCachedArtifact([object] $Context, [string] $Sha256) {
    if ($Sha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'The cache lookup requires an exact artifact digest.' }
    $root = Join-Path $Context.userData 'YLResourceContent'
    $matches = @(Get-YLTestTreeFiles $root | Where-Object {
        [IO.Path]::GetFileNameWithoutExtension($_) -ceq $Sha256 -and [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($_)) -ceq 'objects'
    })
    if ($matches.Count -ne 1) { throw "Expected one exact persisted content artifact for $Sha256; received $($matches.Count)." }
    $path = Get-UpdateOwnedPath $Context.userData $matches[0] -File
    if ((Get-YLFrozenFileSha256 $path) -cne $Sha256) { throw "Owned cached object does not match $Sha256." }
    return $path
}

function Set-UpdateArtifactFault([object] $Context, [string] $Sha256, [switch] $Missing) {
    $path = Get-UpdateCachedArtifact $Context $Sha256
    if ($Missing) { [IO.File]::Delete($path) }
    else {
        $file = [IO.File]::Open($path, 'Open', 'ReadWrite', 'None')
        try {
            $byte = $file.ReadByte()
            if ($byte -lt 0) { throw 'Cannot corrupt an empty content artifact.' }
            $file.Position = 0
            $file.WriteByte([byte]($byte -bxor 1))
            $file.Flush($true)
        } finally { $file.Dispose() }
    }
    $fault = [ordered]@{ path = $path; originalSha256 = $Sha256; missing = [bool]$Missing
        changedSha256 = $(if (-not $Missing) { Get-YLFrozenFileSha256 $path }) }
    if (-not $Context.report.Contains('faults')) { $Context.report['faults'] = [Collections.Generic.List[object]]::new() }
    $Context.report.faults.Add($fault)
}

function Register-UpdateSuccessor([object] $Context) {
    $build = $Context.updateC
    $baseline = $Context.baseline
    $b = $Context.versions.successor
    if ($build.contentStateSha256 -cne $baseline.contentStateSha256 -or
        (Get-YLFrozenFileSha256 $baseline.contentStatePath) -cne $baseline.contentStateSha256 -or
        $build.catalogArtifactSha256 -ceq $Context.update.catalogArtifactSha256 -or
        $build.dependencyArtifactSha256 -ceq $Context.update.dependencyArtifactSha256 -or
        $build.newAddress -cne $Context.newAddress) {
        throw 'C must build distinct artifacts for the same chapter against the same official A archive.'
    }
    $layout = Get-UpdateOwnedPath $Context.host.path $build.buildLayoutPath -File
    if ([IO.FileInfo]::new($layout).Length -eq 0) { throw 'The C build layout is empty.' }
    $Context.report['updateCBuildLayout'] = [ordered]@{ path = $layout; sha256 = Get-YLFrozenFileSha256 $layout }
    $root = Join-Path $Context.host.path 'C'
    Register-UpdateFileRoutes $Context $root $build.files
    $rsa = New-UpdateVerificationKey $Context
    try {
        foreach ($variant in @('valid', 'orphan')) {
            $path = Get-UpdateOwnedPath $root $build.announcements[$variant] -File
            $version = Read-UpdateAnnouncement $path $rsa
            $manifest = $version.manifest
            if (-not $version.signedByFixture -or $manifest.sequence -ne 14 -or $manifest.releaseId -cne 'C' -or
                $manifest.catalogRoute -cne $b.manifest.catalogRoute -or
                $manifest.settingsSha256 -cne $b.manifest.settingsSha256 -or $version.keyId -cne $b.keyId -or
                $manifest.channel -cne $b.manifest.channel -or $version.digest -ceq $b.digest) {
                throw "The independently built C announcement $variant does not identify the intended release."
            }
            Assert-UpdateManifestArtifacts $Context $version $Context.report.baselineAnnouncement
            $version['artifactRoot'] = $root
            $Context.versions['c' + $variant] = $version
        }
    } finally { $rsa.Dispose() }
    $valid = $Context.versions.cValid
    $orphan = $Context.versions.cOrphan
    $startup = @(Get-UpdateArtifactClosure $valid.manifest (@($valid.manifest.catalogRoute, $valid.manifest.descriptionRoute) + $valid.manifest.startupRoutes))
    $transfer = @($startup | Where-Object { $_.sha256 -ceq $build.dependencyArtifactSha256 })
    $binding = @($startup | Where-Object { $_.route -ceq $valid.manifest.catalogRoute -and $_.sha256 -ceq $build.catalogArtifactSha256 })
    if ($transfer.Count -ne 1 -or $binding.Count -ne 1 -or
        $transfer[0].route -cnotin @($startup | ForEach-Object { $_.dependencies })) {
        throw 'C did not bind a new authoritative Catalog and a required, real transitive dependency.'
    }
    $orphanObject = @($orphan.manifest.artifacts | Where-Object { $_.route -ceq $transfer[0].route })
    $restValid = @($valid.manifest.artifacts | Where-Object { $_.route -cne $transfer[0].route }) | ConvertTo-Json -Depth 12 -Compress
    $restOrphan = @($orphan.manifest.artifacts | Where-Object { $_.route -cne $transfer[0].route }) | ConvertTo-Json -Depth 12 -Compress
    $orphanSource = $Context.server.Prefix + $build.orphanTransferRoute.TrimStart('/')
    if ($orphan.digest -ceq $valid.digest -or $orphanObject.Count -ne 1 -or $orphanObject[0].source -cne $orphanSource -or
        $orphanObject[0].sha256 -cne $build.orphanArtifactSha256 -or $orphanObject[0].sha256 -cin $valid.manifest.artifacts.sha256 -or
        $orphanObject[0].sha256 -cin $b.manifest.artifacts.sha256 -or $restOrphan -cne $restValid) {
        throw 'The test C announcement must stage one distinct real orphan object outside active C and retained B.'
    }
    Assert-UpdateOptionalInventory $Context $build $valid $Context.materialOptionalC
    $Context.report['updateCDigest'] = $valid.digest
    $Context.report['cOrphanDigest'] = $orphan.digest
}

function Build-UpdateSuccessor([object] $Context, [switch] $LargeOptionalChapter) {
    $Context.report.phase = 'ContentOnlyBuildC'
    $Context.materialReplacementC = New-UpdateMaterial $Context.materialA.ordinary -TransferProof
    $Context.materialNewC = New-UpdateMaterial $Context.materialA.ordinary -TransferProof
    $Context.materialOptionalC = New-UpdateMaterial $Context.materialA.ordinary -BulkTransfer:$LargeOptionalChapter
    $Context.report['cFixtureCreatedUtc'] = [DateTime]::UtcNow.ToString('o')
    Write-Host "Building content C for $($Context.id) using the existing A installation"
    $Context.updateC = Invoke-UpdateEditorBuild $Context C
    Assert-UpdateInstallation $Context
    Register-UpdateSuccessor $Context
}

function Invoke-UpdateSuccessorScenarios([object] $Context) {
    Build-UpdateSuccessor $Context

    Publish-UpdateAnnouncement $Context 'cOrphan'
    Set-UpdateOnline $Context $true
    $null = Invoke-UpdateRun $Context 'prepare-orphan-c-14' -Operation prepare -Outcome Prepared -Active S -Previous A -Highest S
    Set-UpdateOnline $Context $false
    $orphanObject = Get-UpdateCachedArtifact $Context $Context.updateC.orphanArtifactSha256
    $orphanManifest = Get-UpdateStoreFile $Context ($Context.versions.cOrphan.digest + '.json')
    Set-UpdateArtifactFault $Context $Context.updateC.catalogArtifactSha256 -Missing
    $null = Invoke-UpdateRun $Context 'reject-incomplete-c-14' -Boot ActivationRejected -Active S -Previous A -Highest S

    Publish-UpdateAnnouncement $Context 'cValid'
    Set-UpdateOnline $Context $true
    $null = Invoke-UpdateRun $Context 'prepare-c-14' -Operation prepare -Outcome Prepared -Active S -Previous A -Highest S
    Set-UpdateOnline $Context $false
    $null = Invoke-UpdateRun $Context 'activate-c-14-offline' -Boot Activated -Active C -Previous S -Highest C -Advance

    $b = $Context.versions.successor.manifest
    $c = $Context.versions.cValid.manifest
    $bOnly = @($b.artifacts | Where-Object { -not (Test-UpdateInstalledArtifact $_) -and $_.sha256 -cnotin $c.artifacts.sha256 })
    $requiredDependency = $Context.update.dependencyArtifactSha256
    $optionalB = @($b.artifacts | Where-Object { $_.route -cin $Context.update.optionalClosure })
    if ($requiredDependency -cnotin $bOnly.sha256 -or @($optionalB | Where-Object { $_.sha256 -cnotin $bOnly.sha256 }).Count -ne 0) {
        throw 'The previous B fixture must have exclusive required and optional dependency objects to retain.'
    }
    $cachedB = @(foreach ($artifact in $bOnly) {
        $path = Get-UpdateCachedArtifact $Context $artifact.sha256
        [ordered]@{ path = $path; sha256 = $artifact.sha256; route = $artifact.route }
    })
    $bManifest = Get-UpdateStoreFile $Context ($Context.versions.successor.digest + '.json')
    $bManifestHash = Get-YLFrozenFileSha256 $bManifest
    if ((Get-YLFrozenFileSha256 $orphanObject) -cne $Context.updateC.orphanArtifactSha256 -or
        -not [IO.File]::Exists($orphanManifest)) { throw 'The independent orphan did not survive until explicit cleanup.' }
    $cleanup = Invoke-UpdateRun $Context 'cleanup-c-retains-b' -Operation cleanup -Outcome CleanupComplete -Active C -Previous S -Highest C
    if ($cleanup.cleanupRemoved -lt 2 -or [IO.File]::Exists($orphanObject) -or [IO.File]::Exists($orphanManifest) -or
        (Get-YLFrozenFileSha256 $bManifest) -cne $bManifestHash) {
        throw 'Cleanup did not remove the real orphan bundle and announcement while retaining the published B announcement.'
    }
    foreach ($artifact in $cachedB) {
        if ((Get-YLFrozenFileSha256 $artifact.path) -cne $artifact.sha256) {
            throw "Cleanup removed or changed previous B content: $($artifact.route)."
        }
    }
    $Context.report['cleanupAfterC'] = [ordered]@{ removedObject = $orphanObject
        removedObjectSha256 = $Context.updateC.orphanArtifactSha256; removedManifest = $orphanManifest
        retainedBManifest = $bManifest; retainedBManifestSha256 = $bManifestHash; retainedBObjects = $cachedB }
    $null = Invoke-UpdateRun $Context 'queue-rollback-c-to-b' -Operation rollback -Outcome RollbackQueued -Active C -Previous S -Highest C
    $null = Invoke-UpdateRun $Context 'rollback-download-b-offline' -Boot RolledBack -Active S -Highest C -Advance
    $null = Invoke-UpdateRun $Context 'retained-b-optional-restart-offline' -Operation optional-read -Outcome OptionalRead -Active S -Highest C
}

function Register-UpdateSpaceCandidate([object] $Context) {
    $current = $Context.versions.cValid
    $manifest = ConvertTo-Json -InputObject $current.manifest -Depth 24 -Compress | ConvertFrom-Json -AsHashtable -DateKind String
    $manifest.sequence = 15
    $manifest.startupRoutes = @($manifest.startupRoutes) + @($Context.updateC.optionalRoutes)
    $payload = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $manifest -Depth 24 -Compress))
    $parameters = [Security.Cryptography.RSAParameters]::new()
    $encoded = Read-YLPackageEvidenceJson $Context.keys.signingPrivateKeyPath
    foreach ($field in @('Modulus', 'Exponent', 'D', 'P', 'Q', 'DP', 'DQ', 'InverseQ')) {
        $parameters.$field = [Convert]::FromBase64String($encoded[$field])
    }
    $signer = [Security.Cryptography.RSA]::Create()
    try {
        $signer.ImportParameters($parameters)
        $signature = $signer.SignData($payload, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $id = [Text.Encoding]::ASCII.GetBytes($current.keyId)
        $envelope = [byte[]]([Text.Encoding]::ASCII.GetBytes('YLR1') + [byte]$id.Length + $id + $signature)
        $path = Join-Path ([IO.Path]::GetDirectoryName($current.path)) 'space-candidate-15.json'
        Write-YLTestJson -Path $path -Value @{ payload = [Convert]::ToBase64String($payload); signature = [Convert]::ToBase64String($envelope) }
        $verified = Read-UpdateAnnouncement $path $signer
        if (-not $verified.signedByFixture -or $verified.digest -ceq $current.digest) { throw 'The test candidate failed signature verification.' }
        Assert-UpdateManifestArtifacts $Context $verified $Context.report.baselineAnnouncement
        $verified['artifactRoot'] = $current.artifactRoot
        $Context.versions['spaceCandidate'] = $verified
    } finally {
        $signer.Dispose()
        foreach ($field in @('D','P','Q','DP','DQ','InverseQ')) { [Array]::Clear($parameters.$field) }
    }
    $startup = Get-UpdateArtifactClosure $manifest $manifest.startupRoutes
    $large = $startup | Where-Object { $_.route -cin $Context.updateC.optionalClosure -and $_.length -ge 8MB } |
        Sort-Object length -Descending | Select-Object -First 1
    if ($null -eq $large) { throw 'The signed candidate did not add a large missing optional dependency to its startup closure.' }
    return $large
}

function Invoke-UpdateSpaceFaultScenarios([object] $Context) {
    Register-UpdateArtifacts $Context
    Build-UpdateSuccessor $Context -LargeOptionalChapter
    $large = Register-UpdateSpaceCandidate $Context
    $route = ([Uri]$large.source).AbsolutePath
    $baselineHashes = @($Context.report.baselineAnnouncement.manifest.artifacts.sha256)
    $currentHashes = @($Context.versions.cValid.manifest.artifacts.sha256)
    $downloadedB = $Context.versions.successor.manifest
    $mustDiffer = @($downloadedB.artifacts | Where-Object {
        $_.sha256 -ceq $Context.update.dependencyArtifactSha256 -or $_.route -cin $Context.update.optionalClosure
    })
    if (-not $Context.update.optionalClosure -or $mustDiffer.Count -lt 3 -or @($mustDiffer | Where-Object {
        (Test-UpdateInstalledArtifact $_) -or $_.sha256 -cin $baselineHashes -or $_.sha256 -cin $currentHashes
    }).Count -ne 0) {
        throw 'Downloaded B and its optional closure must supply unique offline dependencies.'
    }
    $originalData = $Context.userData
    $evidence = [ordered]@{ artifact = $large; candidateDigest = $Context.versions.spaceCandidate.digest; rounds = [ordered]@{} }
    try {
        foreach ($fault in @('before-prepare','during-write')) {
            Set-UpdateOnline $Context $false
            $Context.userData = Join-Path $originalData $fault
            $Context.generation = $null
            $null = [IO.Directory]::CreateDirectory($Context.userData)
            $null = Invoke-UpdateRun $Context "$fault-cold" -Boot Baseline
            Set-UpdateOnline $Context $true
            Publish-UpdateAnnouncement $Context 'successor'
            $null = Invoke-UpdateRun $Context "$fault-fetch-b13" -Operation prepare -Outcome Prepared
            Set-UpdateOnline $Context $false
            $null = Invoke-UpdateRun $Context "$fault-read-b13" -Boot Activated -Active S -Previous A -Highest S -Advance
            Set-UpdateOnline $Context $true
            $null = Invoke-UpdateRun $Context "$fault-b13-optional" -Operation optional-prepare -Outcome OptionalPrepared -Active S -Previous A -Highest S
            Publish-UpdateAnnouncement $Context 'cValid'
            $null = Invoke-UpdateRun $Context "$fault-fetch-c14" -Operation prepare -Outcome Prepared -Active S -Previous A -Highest S
            Set-UpdateOnline $Context $false
            $null = Invoke-UpdateRun $Context "$fault-read-c14" -Boot Activated -Active C -Previous S -Highest C -Advance
            $null = Invoke-UpdateRun $Context "$fault-stable-c14" -Active C -Previous S -Highest C
            $statePath = Get-UpdateStoreFile $Context 'state'
            $journalPath = Get-UpdateStoreFile $Context 'journal'
            $stateHash = Get-YLFrozenFileSha256 $statePath
            $journalHash = Get-YLFrozenFileSha256 $journalPath
            $bSpecific = @(foreach ($dependency in $mustDiffer) {
                Get-UpdateCachedArtifact $Context $dependency.sha256
            })
            $targetName = $large.sha256 + $(if ($large.kind -ceq 'Ciphertext') { '.cipher' } else { '.bundle' })
            $targetPath = Join-Path ([IO.Path]::GetDirectoryName($statePath)) ('objects/' + $targetName)
            if ([IO.File]::Exists($targetPath)) { throw 'The target dependency was already cached before injection.' }
            Publish-UpdateAnnouncement $Context 'spaceCandidate'
            Set-UpdateOnline $Context $true
            $quota = if ($fault -ceq 'before-prepare') { 0L } else { 64MB }
            $writeLimit = if ($fault -ceq 'before-prepare') { 0L } else { 1MB }
            $beforePreflight = $Context.tuned -and $fault -ceq 'before-prepare'
            $argsForFault = @{ Operation='prepare'; Outcome=$(if ($Context.tuned) { 'InsufficientStorage' } else { 'TransferFailed' }); Active='C'; Previous='S'; Highest='C'
                ObserveTransfers=$true; SpaceFaultRoute=$route; SpaceBeforeBytes=$quota; SpaceWriteLimit=$writeLimit }
            $observed = Invoke-UpdateRun $Context "$fault-failed-d15" @argsForFault
            $run = $Context.report.runs[$Context.report.runs.Count - 1]
            $targetRequest = @($run.requests | Where-Object target -CEQ $route)
            $received = @($observed.transferReads | Where-Object target -CEQ $route)
            if ($beforePreflight) {
                if (@($run.requests | Where-Object target -CNE '/announcement.json').Count -ne 0 -or $received.Count -ne 0 -or
                    $observed.spaceFaultHResult -ne 0) { throw 'An already insufficient volume reached artifact GET or writes.' }
            } elseif ($targetRequest.Count -ne 1 -or $received.Count -ne 1 -or $received[0].bytes -le $writeLimit -or
                $observed.spaceFaultHResult -ne -2147024784 -or $observed.spaceFaultStageBytes -gt $writeLimit -or
                ($writeLimit -gt 0 -and $observed.spaceFaultStageBytes -le 0)) {
                throw 'The transfer did not observe the injected disk full at the expected boundary.'
            }
            if ([IO.File]::Exists($targetPath) -or
                (Get-YLFrozenFileSha256 $statePath) -cne $stateHash -or (Get-YLFrozenFileSha256 $journalPath) -cne $journalHash) {
                throw 'The failed prepare modified the committed state, ready journal or target object.'
            }
            foreach ($path in $bSpecific) {
                if ((Get-YLFrozenFileSha256 $path) -cne [IO.Path]::GetFileNameWithoutExtension($path)) {
                    throw 'The failed prepare changed a published artifact required for B rollback.'
                }
            }
            Set-UpdateOnline $Context $false
            $c = Invoke-UpdateRun $Context "$fault-c14-offline-after" -Active C -Previous S -Highest C
            $null = Invoke-UpdateRun $Context "$fault-queue-b13" -Operation rollback -Outcome RollbackQueued -Active C -Previous S -Highest C
            $b = Invoke-UpdateRun $Context "$fault-b13-offline" -Boot RolledBack -Active S -Highest C -Advance
            $optional = Invoke-UpdateRun $Context "$fault-b13-optional-offline" -Operation optional-read -Outcome OptionalRead -Active S -Highest C
            $evidence.rounds[$fault] = @{ beforeBytes = $quota; writeLimit = $writeLimit; failed = $observed; requests = $targetRequest
                stateSha256 = $stateHash; journalSha256 = $journalHash; uniqueBArtifacts = $bSpecific; c = $c; b = $b; optionalB = $optional }
            Write-UpdateProgress (Join-Path $Context.results 'space-faults.json') $evidence
        }
    } finally { $Context.userData = $originalData }
}

function Invoke-UpdateScenarios([object] $Context) {
    $Context.report.phase = 'PublicationAudit'
    Register-UpdateArtifacts $Context
    $null = Invoke-UpdateRun $Context 'a-no-chapter-offline-with-legacy-catalog-cache' -LegacyCache
    Set-UpdateOnline $Context $true
    foreach ($variant in @('tampered', 'unknown', 'wrongChannel', 'expired')) {
        Publish-UpdateAnnouncement $Context $variant
        $null = Invoke-UpdateRun $Context "reject-$variant" -Operation prepare -Outcome Rejected -Rejection InvalidAnnouncement
    }
    Publish-UpdateAnnouncement $Context 'high'
    $null = Invoke-UpdateRun $Context 'prepare-12' -Operation prepare -Outcome Prepared -K2 missing
    Set-UpdateOnline $Context $false
    Set-UpdateArtifactFault $Context $Context.update.dependencyArtifactSha256 -Missing
    $null = Invoke-UpdateRun $Context 'reject-incomplete-12' -Boot ActivationRejected

    Publish-UpdateAnnouncement $Context 'valid'
    Set-UpdateOnline $Context $true
    $null = Invoke-UpdateRun $Context 'kill-partial-transfer-11' -Operation prepare -Stop transfer -K2 missing
    Set-UpdateOnline $Context $false
    $null = Invoke-UpdateRun $Context 'recover-a-after-transfer-kill'

    $faults = [ordered]@{ catalog = $Context.update.catalogArtifactSha256; dependency = $Context.update.dependencyArtifactSha256 }
    if ($Context.encrypted) { $faults['cipher'] = $Context.update.cipherArtifactSha256 }
    foreach ($fault in $faults.GetEnumerator()) {
        Set-UpdateOnline $Context $true
        $null = Invoke-UpdateRun $Context "prepare-11-for-$($fault.Key)-fault" -Operation prepare -Outcome Prepared -K2 missing
        Set-UpdateOnline $Context $false
        Set-UpdateArtifactFault $Context $fault.Value -Missing:($fault.Key -ceq 'dependency')
        $null = Invoke-UpdateRun $Context "reject-$($fault.Key)-fault" -Boot ActivationRejected
    }
    if ($Context.encrypted) {
        foreach ($variant in @('wrongCatalog', 'wrongCipher')) {
            Publish-UpdateAnnouncement $Context $variant
            Set-UpdateOnline $Context $true
            $null = Invoke-UpdateRun $Context "prepare-$variant" -Operation prepare -Outcome Prepared -K2 missing
            Set-UpdateOnline $Context $false
            $errorCategory = if ($variant -ceq 'wrongCatalog') { 'InvalidDataException' } else { 'ContentAuthenticationFailed' }
            $null = Invoke-UpdateRun $Context "verify-$variant" -Boot ActivationRejected -BootError $errorCategory
        }
    }

    Publish-UpdateAnnouncement $Context 'valid'
    Set-UpdateOnline $Context $true
    $null = Invoke-UpdateRun $Context 'prepare-11-before-commit' -Operation prepare -Outcome Prepared -K2 missing -Boot @('Active', 'ActivationRejected')
    Set-UpdateOnline $Context $false
    if ($Context.encrypted) {
        $null = Invoke-UpdateRun $Context 'k2-missing' -Boot ActivationRejected -BootError ContentKeyUnavailable -K2 missing
        $null = Invoke-UpdateRun $Context 'k2-wrong' -Boot ActivationRejected -BootError ContentAuthenticationFailed -K2 wrong
    }
    $null = Invoke-UpdateRun $Context 'kill-before-commit' -Stop before-commit
    $null = Invoke-UpdateRun $Context 'recover-a-after-precommit-kill' -Boot Interrupted
    $null = Invoke-UpdateRun $Context 'retry-local-11' -Operation retry -Outcome RetryQueued
    $null = Invoke-UpdateRun $Context 'kill-after-commit' -Stop after-commit
    $null = Invoke-UpdateRun $Context 'recover-b-after-postcommit-kill' -Active B -Previous A -Highest B -Advance
    Invoke-UpdateMissingStateScenario $Context
    $null = Invoke-UpdateRun $Context 'b-optional-cold-offline' -Operation optional-cold -Outcome OptionalCold -Active B -Previous A -Highest B

    Set-UpdateOnline $Context $true
    $null = Invoke-UpdateRun $Context 'b-optional-prepare-online' -Operation optional-prepare -Outcome OptionalPrepared -Active B -Previous A -Highest B
    if ($Context.encrypted) {
        $null = Invoke-UpdateRun $Context 'b-ordinary-a-origin-prepare-online' -Operation optional-prepare -Outcome OptionalPrepared -Active B -Previous A -Highest B -Retained -K1 missing
    }
    Set-UpdateOnline $Context $false
    $null = Invoke-UpdateRun $Context 'b-optional-restart-offline' -Operation optional-read -Outcome OptionalRead -Active B -Previous A -Highest B
    if ($Context.encrypted) {
        $null = Invoke-UpdateRun $Context 'b-ordinary-a-origin-restart-offline' -Operation optional-read -Outcome OptionalRead -Active B -Previous A -Highest B -Retained -K1 missing
    }
    Set-UpdateOnline $Context $true
    foreach ($variant in @('stale', 'conflict', 'valid')) {
        Publish-UpdateAnnouncement $Context $variant
        $outcome = if ($variant -ceq 'valid') { 'NoUpdate' } else { 'Rejected' }
        $rejection = @{ stale = 'Replay'; conflict = 'SequenceConflict'; valid = '' }[$variant]
        $null = Invoke-UpdateRun $Context "active-b-replay-$variant" -Operation prepare -Outcome $outcome -Rejection $rejection -Active B -Previous A -Highest B
    }
    Set-UpdateOnline $Context $false
    $highDigest = $Context.versions.high.digest
    $orphans = @(Get-YLTestTreeFiles $Context.userData | Where-Object {
        [IO.Path]::GetFileName($_) -ceq ($highDigest + '.json') -and
        [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($_)) -ceq 'versions'
    })
    if ($orphans.Count -ne 1) { throw 'The previously prepared higher release did not leave one owned orphan manifest.' }
    $temporary = $Context.report.precommitTemporary
    if ((Get-YLFrozenFileSha256 $temporary.temporaryStatePath) -cne $temporary.temporaryStateSha256) {
        throw 'The original crash record did not survive until explicit cleanup.'
    }
    $cleanup = Invoke-UpdateRun $Context 'cleanup-while-b' -Operation cleanup -Outcome CleanupComplete -Active B -Previous A -Highest B
    if ($cleanup.cleanupRemoved -lt 2 -or [IO.File]::Exists($orphans[0]) -or
        [IO.File]::Exists($temporary.temporaryStatePath) -or @(Get-UpdateStateTemporaries $Context).Count -ne 0) {
        throw 'Cleanup did not remove the actual unreferenced prepared manifest and the stale precommit record.'
    }
    $Context.report['precommitTemporaryRemoved'] = $temporary.temporaryStatePath
    $Context.report['cleanupOrphanRemoved'] = $orphans[0]
    if ($Context.encrypted) {
        foreach ($material in @('missing', 'wrong')) {
            $null = Invoke-UpdateRun $Context "queue-rollback-k1-$material" -Operation rollback -Outcome RollbackQueued -Active B -Previous A -Highest B -K1 $material
            $errorCategory = if ($material -ceq 'missing') { 'ContentKeyUnavailable' } else { 'ContentAuthenticationFailed' }
            $null = Invoke-UpdateRun $Context "reject-rollback-k1-$material" -Boot RollbackRejected -BootError $errorCategory -Active B -Previous A -Highest B -K1 $material
        }
    }
    $null = Invoke-UpdateRun $Context 'queue-rollback' -Operation rollback -Outcome RollbackQueued -Active B -Previous A -Highest B
    $null = Invoke-UpdateRun $Context 'rollback-a-offline' -Boot RolledBack -Highest B -Advance

    Set-UpdateOnline $Context $true
    Publish-UpdateAnnouncement $Context 'stale'
    $null = Invoke-UpdateRun $Context 'rolled-back-stale-10' -Operation prepare -Outcome Rejected -Rejection Replay -Highest B
    Publish-UpdateAnnouncement $Context 'valid'
    $null = Invoke-UpdateRun $Context 'rolled-back-idempotent-11' -Operation prepare -Outcome NoUpdate -Highest B
    Set-UpdateOnline $Context $false
    $null = Invoke-UpdateRun $Context 'rolled-back-a-restart-offline' -Highest B
    Publish-UpdateAnnouncement $Context 'successor'
    Set-UpdateOnline $Context $true
    $null = Invoke-UpdateRun $Context 'prepare-13' -Operation prepare -Outcome Prepared -Highest B -K2 missing
    Set-UpdateOnline $Context $false
    $null = Invoke-UpdateRun $Context 'activate-13-offline' -Boot Activated -Active S -Previous A -Highest S -Advance
    Invoke-UpdateSuccessorScenarios $Context
}

function Initialize-UpdateFileEvidence {
    if ('YLTesting.UpdateFileEvidence' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace YLTesting
{
    public static class UpdateFileEvidence
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Identity
        {
            public uint Attributes, CreatedLow, CreatedHigh, ReadLow, ReadHigh, WriteLow, WriteHigh;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
            public ulong FileId => ((ulong)IndexHigh << 32) | IndexLow;
            public ulong Created => ((ulong)CreatedHigh << 32) | CreatedLow;
            public string Key => $"{Volume:x8}:{FileId:x16}";
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle Open(string path, uint access, uint share, IntPtr security,
            uint mode, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Identity identity);

        private static SafeFileHandle OpenExisting(string path, uint access) =>
            Open(path, access, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);

        public static Identity Read(string path)
        {
            using var handle = OpenExisting(path, 0);
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var identity))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((identity.Attributes & 0x400) != 0)
                throw new InvalidOperationException("The cache evidence cannot follow a reparse point.");
            return identity;
        }

        public static int OpenStatus(string path, uint access)
        {
            using var handle = OpenExisting(path, access);
            return handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        }
    }
}
'@
}

function Invoke-UpdateCacheAclCheck([string] $Path, [scriptblock] $Observe, [switch] $DenyRead) {
    Initialize-UpdateFileEvidence
    $original = Get-Acl -LiteralPath $Path
    $restricted = Get-Acl -LiteralPath $Path
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { $sid = $identity.User } finally { $identity.Dispose() }
    $rights = if ($DenyRead) { [Security.AccessControl.FileSystemRights]::ReadData }
        else { [Security.AccessControl.FileSystemRights]'WriteData,AppendData,WriteAttributes,WriteExtendedAttributes,DeleteSubdirectoriesAndFiles,Delete' }
    $inheritance = if ($DenyRead) { 'None' } else { 'ContainerInherit,ObjectInherit' }
    $restricted.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, $rights, $inheritance, 'None', 'Deny'))
    try {
        Set-Acl -LiteralPath $Path -AclObject $restricted
        $access = if ($DenyRead) { 1 } else { 2 }
        if ([YLTesting.UpdateFileEvidence]::OpenStatus($Path, $access) -ne 5) {
            throw "The fixture ACL did not refuse the requested Win32 access: $Path"
        }
        return & $Observe
    } finally {
        Set-Acl -LiteralPath $Path -AclObject $original
    }
}

function Get-UpdateCacheInventory([string] $Root, [string] $ReadDeniedPath) {
    if (-not [IO.Directory]::Exists($Root)) { return }
    Initialize-UpdateFileEvidence
    $paths = @($Root) + @([IO.Directory]::EnumerateFileSystemEntries($Root, '*', [IO.SearchOption]::AllDirectories) | Sort-Object -CaseSensitive)
    foreach ($path in $paths) {
        $info = Get-Item -LiteralPath $path -Force
        $identity = [YLTesting.UpdateFileEvidence]::Read($path)
        $entry = [ordered]@{ path = $path; fileIdentity = $identity.Key; attributes = [string]$info.Attributes
            createdUtc = $info.CreationTimeUtc.Ticks; modifiedUtc = $info.LastWriteTimeUtc.Ticks }
        if ($info -is [IO.FileInfo]) {
            $entry['length'] = $info.Length
            if ($path -ieq $ReadDeniedPath) {
                $entry['readStatus'] = [YLTesting.UpdateFileEvidence]::OpenStatus($path, 1)
                if ($entry.readStatus -ne 5) { throw 'The denied cache fixture became readable during inventory.' }
            } elseif ($info.Name -cne 'writer.lock') { $entry['sha256'] = Get-YLFrozenFileSha256 $path }
        }
        $entry
    }
}

function Invoke-UpdateCacheReader([object] $Context, [string] $Name, [string] $Data, [string] $ReadDeniedPath) {
    $prefix = Join-Path $Context.results $Name
    $request = $prefix + '-request.json'
    $output = $prefix + '-result.json'
    $before = @(Get-UpdateCacheInventory $Data $ReadDeniedPath)
    $requestsBefore = $Context.server.Snapshot().Length
    $manifest = $Context.versions.valid.manifest
    $catalog = $manifest.artifacts | Where-Object route -CEQ $manifest.catalogRoute
    $realCatalog = $Context.versions.artifacts[([Uri]$catalog.source).AbsolutePath].path
    Write-YLTestJson -Path $request -Value @{
        resultPath = $output; userDataPath = $Data; baseUrl = $Context.server.Prefix; announcementRoute = 'announcement.json'
        operation = 'cache-inspect'; encrypted = $Context.encrypted; legacyCacheCatalog = $realCatalog
    }
    $start = New-UpdateProcess $Context.exe $Context.userData @('-batchmode', '-logFile', "$prefix-game.log", '-ylContentUpdateRequest', $request)
    $limits = @{ TimeoutMilliseconds = 70000; DescendantGraceMilliseconds = 45000 }
    $process = Invoke-YLTestProcess -StartInfo $start -Log "$prefix-process.log" @limits
    Write-YLTestJson -Path "$prefix-process.json" -Value $process
    Assert-YLTestProcessSucceeded $process
    $result = Read-YLPackageEvidenceJson $output
    $after = @(Get-UpdateCacheInventory $Data $ReadDeniedPath)
    $officialBefore = $result.officialCacheBefore
    $officialAfter = $result.officialCacheAfter
    if (-not $officialBefore.catalogPath.EndsWith([IO.Path]::GetExtension($realCatalog), [StringComparison]::Ordinal) -or
        [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($officialBefore.catalogPath)) -cne 'com.unity.addressables' -or
        $officialBefore.catalogSha256 -cne $catalog.sha256 -or
        $officialBefore.catalogFileId -cnotmatch '^[0-9a-f]{8}:[0-9a-f]{16}$' -or
        $officialBefore.markerFileId -cnotmatch '^[0-9a-f]{8}:[0-9a-f]{16}$' -or
        (ConvertTo-Json -Depth 4 -Compress -InputObject $officialBefore) -cne (ConvertTo-Json -Depth 4 -Compress -InputObject $officialAfter)) {
        throw 'The real official Catalog cache sentinel did not keep its file identities and contents.'
    }
    if ($result.status -cne 'Passed' -or $result.pid -ne $process.pid -or
        $result.operationStatus -cne 'Inspected' -or ($Context.encrypted -and ($result.prepareKeyRequests -ne 0 -or $result.mountAttempts -ne 0)) -or
        $Context.server.Snapshot().Length -ne $requestsBefore -or
        (ConvertTo-Json -Depth 6 -Compress -InputObject $before) -cne (ConvertTo-Json -Depth 6 -Compress -InputObject $after)) {
        throw "Cache inspection altered filesystem or network activity. See $output."
    }
    foreach ($property in @('owned','retained','cleanupCandidates','undetermined','files','issues')) {
        if ((ConvertTo-Json -Depth 6 -Compress -InputObject $result.cacheBefore[$property]) -cne
            (ConvertTo-Json -Depth 6 -Compress -InputObject $result.cacheAfter[$property])) {
            throw "Two consecutive cache queries disagreed on $property."
        }
    }
    Write-YLTestJson -Path "$prefix-inventory.json" -Value @{
        before = $before; after = $after; requests = 0; officialCacheBefore = $officialBefore; officialCacheAfter = $officialAfter
    }
    return $result
}

function Invoke-UpdateCacheScenarios([object] $Context) {
    Register-UpdateArtifacts $Context
    $missing = Join-Path $Context.userData 'unused'
    $empty = Invoke-UpdateCacheReader $Context 'missing-cache' $missing
    if ([IO.Directory]::Exists($missing) -or $empty.cacheAfter.owned.certainty -cne 'Exact' -or $empty.cacheAfter.owned.bytes -ne 0) {
        throw 'The missing cache was created or did not report a confirmed zero.'
    }
    $store = [IO.Path]::GetDirectoryName((Get-UpdateStoreFile $Context 'state'))
    $objects = Join-Path $store 'objects'
    $null = [IO.Directory]::CreateDirectory($objects)
    $unclaimedStage = Join-Path $objects ('.stage-' + [Guid]::NewGuid().ToString('N'))
    [IO.File]::WriteAllText($unclaimedStage, 'legacy staging')
    Initialize-UpdateFileEvidence
    $unclaimedIdentity = [YLTesting.UpdateFileEvidence]::Read($unclaimedStage).Key
    $unclaimedDigest = Get-YLFrozenFileSha256 $unclaimedStage
    $orphanText = [Guid]::NewGuid().ToString('N')
    $orphanDigest = Get-UpdateTextSha256 $orphanText
    $orphanPath = Join-Path $objects ($orphanDigest + '.bin')
    [IO.File]::WriteAllText($orphanPath, $orphanText)
    [IO.File]::WriteAllText((Join-Path $objects 'external-sentinel.txt'), 'external fixture')
    $cold = Invoke-UpdateCacheAclCheck $store {
        if ([YLTesting.UpdateFileEvidence]::OpenStatus($unclaimedStage, 2) -ne 5) {
            throw 'The cache directory ACL also needs to protect its existing staging file.'
        }
        Invoke-UpdateCacheReader $Context 'read-only-cache-with-unknown-stage' $Context.userData
    }
    $unclaimed = @($cold.cacheAfter.files | Where-Object relativePath -CEQ ('objects/' + [IO.Path]::GetFileName($unclaimedStage)))
    if ($unclaimed.Count -ne 1 -or $unclaimed[0].ownershipConfirmed -or $unclaimed[0].disposition -cne 'Undetermined' -or
        $unclaimed[0].bytes -ne 14 -or $cold.cacheAfter.cleanupCandidates.bytes -ne $orphanText.Length -or
        $cold.cacheAfter.cleanupCandidates.certainty -cne 'ObservedLowerBound') {
        throw 'The read-only query claimed an unverified stage or omitted the verified orphan from its lower bound.'
    }
    $future = @($Context.versions.valid.manifest.artifacts | Where-Object { $_.route -ceq $Context.update.optionalRoutes[0] })
    if ($future.Count -ne 1 -or (Test-UpdateInstalledArtifact $future[0])) { throw 'No future optional artifact for stale-report validation.' }
    $future = $future[0]
    $suffix = if ($future.kind -ceq 'Ciphertext') { '.cipher' } else { '.bundle' }
    $futureName = $future.sha256 + $suffix
    $futurePath = Join-Path $objects $futureName
    [IO.File]::Copy($Context.versions.artifacts[([Uri]$future.source).AbsolutePath].path, $futurePath, $false)
    $futureIdentity = [YLTesting.UpdateFileEvidence]::Read($futurePath).Key
    $denied = Invoke-UpdateCacheAclCheck $futurePath {
        Invoke-UpdateCacheReader $Context 'denied-file' $Context.userData $futurePath
    } -DenyRead
    $deniedFile = @($denied.cacheAfter.files | Where-Object relativePath -CEQ ('objects/' + $futureName))
    if ($deniedFile.Count -ne 1 -or $deniedFile[0].disposition -cne 'Undetermined' -or $deniedFile[0].ownershipConfirmed -or
        $denied.cacheAfter.cleanupCandidates.certainty -cne 'ObservedLowerBound' -or
        (Get-YLFrozenFileSha256 $futurePath) -cne $future.sha256 -or [YLTesting.UpdateFileEvidence]::Read($futurePath).Key -cne $futureIdentity) {
        throw 'Denied cache bytes or the actual restored file did not match the lower-bound proof.'
    }
    $oldReport = Invoke-UpdateCacheReader $Context 'stale-candidate' $Context.userData
    $candidate = @($oldReport.cacheAfter.files | Where-Object relativePath -CEQ ('objects/' + $futureName))
    if ($candidate.Count -ne 1 -or $candidate[0].disposition -cne 'CleanupCandidate') {
        throw 'The later referenced artifact was not a candidate at the earlier observation.'
    }
    Publish-UpdateAnnouncement $Context 'valid'
    Set-UpdateOnline $Context $true
    $transferRoute = '/' + $Context.update.transferRoute.TrimStart('/')
    $prepare = Invoke-UpdateRun $Context 'prepare-with-independent-inspector' -Operation prepare -Outcome Prepared -K2 missing -ObserveBeforeReady -InspectStagingRoute $transferRoute
    if ($null -eq $prepare.inFlightStage -or $prepare.inFlightStage.relativePath -cnotlike 'objects/.stage-*') {
        throw 'The signed transfer did not observe its actual ephemeral stage while writing.'
    }
    if (-not $Context.report.Contains('beforeReadyObservation')) { throw 'The independent observer did not reach the publication boundary.' }
    $ready = Invoke-UpdateCacheReader $Context 'after-ready' $Context.userData
    $fileName = $Context.report.beforeReadyObservation.object.relativePath
    $retained = @($ready.cacheAfter.files | Where-Object relativePath -CEQ $fileName)
    if ($retained.Count -ne 1 -or $retained[0].disposition -cne 'Retained' -or $retained[0].retention -cnotmatch 'ReadyVersion') {
        throw 'The same object did not acquire a ready-version retention reason.'
    }
    $manifestPath = Join-Path $store ('versions/' + $Context.versions.valid.digest + '.json')
    $validManifest = [IO.File]::ReadAllBytes($manifestPath)
    try {
        [IO.File]::WriteAllBytes($manifestPath, [byte[]]@(0xc3, 0x28))
        $corruptManifest = Invoke-UpdateCacheReader $Context 'invalid-utf8-ready-manifest' $Context.userData
        $invalid = @($corruptManifest.cacheAfter.files | Where-Object relativePath -CEQ ('versions/' + $Context.versions.valid.digest + '.json'))
        if ($invalid.Count -ne 1 -or $invalid[0].ownershipConfirmed -or $invalid[0].disposition -cne 'Undetermined' -or
            $corruptManifest.cacheAfter.owned.certainty -cne 'ObservedLowerBound' -or
            $corruptManifest.cacheAfter.cleanupCandidates.certainty -cne 'Unknown' -or
            $corruptManifest.cacheAfter.issues.Count -eq 0) {
            throw 'A damaged referenced UTF-8 manifest did not preserve available observations and explain the unknown references.'
        }
    } finally { [IO.File]::WriteAllBytes($manifestPath, $validManifest) }
    Set-UpdateOnline $Context $false
    $unowned = Join-Path $objects ('.resume-' + $future.sha256 + '.bundle.part')
    [IO.File]::WriteAllText($unowned, 'external staging-shaped file')
    $unownedIdentity = [YLTesting.UpdateFileEvidence]::Read($unowned).Key
    $unownedReport = Invoke-UpdateCacheReader $Context 'unknown-resume-stage' $Context.userData
    $unownedRow = @($unownedReport.cacheAfter.files | Where-Object relativePath -CEQ ('objects/' + [IO.Path]::GetFileName($unowned)))
    if ($unownedRow.Count -ne 1 -or $unownedRow[0].ownershipConfirmed -or $unownedRow[0].disposition -cne 'Undetermined') {
        throw 'An unproved resume file was treated as owned or a cleanup candidate.'
    }
    $expected = @{ Operation = 'cleanup'; Outcome = 'CleanupComplete'; Boot = 'Activated'; Active = 'B'; Previous = 'A'; Highest = 'B'; Advance = $true }
    $cleanup = Invoke-UpdateRun $Context 'cleanup-after-stale-report' @expected
    if ($cleanup.cleanupRemoved -lt 1 -or [IO.File]::Exists($orphanPath) -or
        [YLTesting.UpdateFileEvidence]::Read($unclaimedStage).Key -cne $unclaimedIdentity -or
        (Get-YLFrozenFileSha256 $unclaimedStage) -cne $unclaimedDigest -or
        (Get-YLFrozenFileSha256 $futurePath) -cne $future.sha256 -or
        [IO.File]::ReadAllText((Join-Path $objects 'external-sentinel.txt')) -cne 'external fixture' -or
        [IO.File]::ReadAllText($unowned) -cne 'external staging-shaped file' -or [YLTesting.UpdateFileEvidence]::Read($unowned).Key -cne $unownedIdentity) {
        throw 'Fresh cleanup did not protect the newly referenced artifact and the external sentinels.'
    }
    Flip-UpdateArtifactByte $futurePath
    try {
        $corrupt = Invoke-UpdateCacheReader $Context 'referenced-equal-length-wrong-bytes' $Context.userData
        $corruptRow = @($corrupt.cacheAfter.files | Where-Object relativePath -CEQ ('objects/' + $futureName))
        if ($corruptRow.Count -ne 1 -or $corruptRow[0].ownershipConfirmed -or $corruptRow[0].disposition -cne 'Undetermined' -or
            $corruptRow[0].bytes -ne $future.length -or $corrupt.cacheAfter.owned.certainty -ceq 'Exact') {
            throw 'The cold cache inspection claimed an equal-length replacement at a signed path.'
        }
    } finally { Flip-UpdateArtifactByte $futurePath }
}

function Flip-UpdateArtifactByte([string] $Path) {
    $stream = [IO.File]::Open($Path, 'Open', 'ReadWrite', 'None')
    try {
        $original = $stream.ReadByte()
        if ($original -lt 0) { throw 'The test requires nonempty artifact bytes.' }
        $stream.Position = 0
        $stream.WriteByte([byte]($original -bxor 1))
        $stream.Flush($true)
    } finally { $stream.Dispose() }
}

function Get-UpdateResumeFiles([object] $Context, [object] $Artifact) {
    $root = Join-Path ([IO.Path]::GetDirectoryName((Get-UpdateStoreFile $Context 'state'))) 'objects'
    $published = Join-Path $root ($Artifact.sha256 + '.bundle')
    $prefix = Join-Path $root ('.resume-' + [IO.Path]::GetFileName($published))
    return @{ published = $published; part = "$prefix.part"; marker = "$prefix.mark" }
}

function Read-UpdateCheckpoint([object] $Context, [object] $Artifact, [switch] $AllowUncommitted, [switch] $AllowRetired) {
    $paths = Get-UpdateResumeFiles $Context $Artifact
    $published = $paths.published
    $part = $paths.part
    $record = $paths.marker
    Initialize-UpdateFileEvidence
    $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($record), [Text.UTF8Encoding]::new($false, $true))
    try {
        $version = $reader.ReadInt32()
        $source = $reader.ReadString()
        $digest = $reader.ReadString()
        $length = $reader.ReadInt64()
        $baseOffsetPosition = $reader.BaseStream.Position
        $offset = $reader.ReadInt64()
        $tag = $reader.ReadString()
        $fileIdentity = $null
        if ($version -eq 2) {
            $fileId = $reader.ReadUInt64()
            $volumeSerial = $reader.ReadUInt32()
            $creation = $reader.ReadUInt64()
            $fileIdentity = [YLTesting.UpdateFileEvidence]::Read($part)
            if ($fileIdentity.FileId -ne $fileId -or $fileIdentity.Volume -ne $volumeSerial -or $fileIdentity.Created -ne $creation) {
                throw 'The persisted checkpoint does not identify the actual staged file.'
            }
        }
        $headerLength = $reader.BaseStream.Position
        $revision = 0L
        if ($version -eq 2) {
            if ($reader.BaseStream.Length - $headerLength -gt 96) { throw 'Unexpected checkpoint trailer length.' }
            for ($slot = 0; $slot -lt 2; $slot++) {
                $bytes = $reader.ReadBytes(48)
                if ($bytes.Length -ne 48) { break }
                $sequence = [BitConverter]::ToInt64($bytes, 0)
                $slotOffset = [BitConverter]::ToInt64($bytes, 8)
                $expectedHash = [Security.Cryptography.SHA256]::HashData([byte[]]$bytes[0..15])
                if ($sequence -gt $revision -and ($sequence - 1) % 2 -eq $slot -and
                    $slotOffset -ge -1 -and $slotOffset -le $length -and
                    [Convert]::ToHexString($expectedHash) -ceq [Convert]::ToHexString([byte[]]$bytes[16..47])) {
                    $revision = $sequence
                    $offset = $slotOffset
                }
            }
        }
        $partBytes = [IO.FileInfo]::new($part).Length
        $matchingPrefix = $offset -gt 0 -and $partBytes -eq $offset
        $pendingSlot = $AllowUncommitted -and $offset -ge 0 -and $offset -lt $partBytes -and $partBytes -eq $length
        $retired = $AllowRetired -and $offset -eq -1 -and $partBytes -eq $length -and $revision -gt 0
        if ($version -notin @(1,2) -or $source -cne $Artifact.source -or $digest -cne $Artifact.sha256 -or
            $length -ne $Artifact.length -or $offset -gt $length -or -not ($matchingPrefix -or $pendingSlot -or $retired) -or
            $tag -cne ('"' + $digest + '"') -or
            $reader.BaseStream.Position -ne $reader.BaseStream.Length) {
            throw 'The checkpoint is not bound to its durable prefix and signed identity.'
        }
        return [ordered]@{ path = $part; marker = $record; published = $published; source = $source
            sha256 = $digest; length = $length; offset = $offset; tag = $tag; version = $version
            revision = $revision; partBytes = $partBytes; baseOffsetPosition = $baseOffsetPosition; headerLength = $headerLength
            fileIdentity = $(if ($null -ne $fileIdentity) { $fileIdentity.Key }) }
    } finally { $reader.Dispose() }
}

function Convert-UpdateCheckpointToLegacy([object] $Context, [object] $Artifact, [object] $Checkpoint, [ValidateSet(1, 2)][int] $Version = 1) {
    if ($Checkpoint.version -ne 2) { throw 'The compatibility test requires an actual persisted v2 checkpoint.' }
    $stream = [IO.File]::Open($Checkpoint.marker, 'Open', 'ReadWrite', 'None')
    try {
        if ($stream.Length -le 24) { throw 'The persisted checkpoint has no identity tail to remove.' }
        $versionBytes = [BitConverter]::GetBytes($Version)
        $stream.Write($versionBytes, 0, $versionBytes.Length)
        $stream.Position = $Checkpoint.baseOffsetPosition
        $offsetBytes = [BitConverter]::GetBytes([long]$Checkpoint.offset)
        $stream.Write($offsetBytes, 0, $offsetBytes.Length)
        # The v1 layout shares the v2 fields before the file ID, volume serial and creation time.
        $stream.SetLength($Checkpoint.headerLength - $(if ($Version -eq 1) { 20 } else { 0 }))
        $stream.Flush($true)
    } finally { $stream.Dispose() }
    $legacy = Read-UpdateCheckpoint $Context $Artifact
    if ($legacy.version -ne $Version -or $legacy.offset -ne $Checkpoint.offset -or $legacy.revision -ne 0 -or
        ($Version -eq 1 -and $legacy.fileIdentity) -or
        ($Version -eq 2 -and $legacy.fileIdentity -cne $Checkpoint.fileIdentity)) {
        throw 'The rewritten record does not carry the expected original checkpoint layout.'
    }
    return $legacy
}

function Save-UpdateTransferMeasurement([object] $Context, [string] $Name, [string] $Target, [object] $Expected, [string] $Outcome,
        [long] $StartOffset = 0) {
    $run = $Context.report.runs[$Context.report.runs.Count - 1]
    $request = @($run.requests | Where-Object target -CEQ $Target)
    $status = if ($StartOffset -eq 0) { 200 } else { 206 }
    if ($request.Count -ne 1 -or $request[0].method -cne 'GET' -or $request[0].status -ne $status -or
        $request[0].declared -ne ($Expected.length - $StartOffset) -or
        $request[0].entity -cne ('"' + $Expected.sha256 + '"')) {
        throw "$Name did not measure the expected representation of the signed artifact."
    }
    if ($StartOffset -eq 0) {
        if ($request[0].range) { throw "$Name unexpectedly requested a range." }
    } elseif ($request[0].range -cne "bytes=$StartOffset-" -or $request[0].ifRange -cne ('"' + $Expected.sha256 + '"') -or
        $request[0].contentRange -cne "bytes $StartOffset-$($Expected.length - 1)/$($Expected.length)") {
        throw "$Name did not bind its remaining bytes to the expected range and strong ETag."
    }
    $receipt = $run.results
    $received = @($receipt.transferReads | Where-Object target -CEQ $Target)
    if ($receipt.operationStatus -cne $Outcome -or $received.Count -ne 1 -or
        $received[0].bytes -le 0 -or $received[0].bytes -gt $request[0].bytes) {
        throw "$Name is missing attributable client progress or expected termination."
    }
    $result = [ordered]@{
        name = $Name; termination = $receipt.operationStatus; detail = $receipt.operationDetail
        server = $request[0]; client = $received[0]; intervalStart = $StartOffset; intervalEnd = $StartOffset + $received[0].bytes
        unconfirmedBodyBytes = $request[0].bytes - $received[0].bytes
    }
    if ($Outcome -ceq 'Prepared') {
        $path = Get-UpdateCachedArtifact $Context $Expected.sha256
        if ($received[0].bytes -ne ($Expected.length - $StartOffset) -or [IO.FileInfo]::new($path).Length -ne $Expected.length -or
            (Get-YLFrozenFileSha256 $path) -cne $Expected.sha256) {
            throw "$Name did not publish the complete verified file."
        }
    }
    return $result
}

function Measure-UpdateRepeatedBytes([object[]] $Attempts) {
    $total = 0L
    $union = 0L
    $end = 0L
    foreach ($attempt in $Attempts | Sort-Object intervalStart,intervalEnd) {
        $total += $attempt.intervalEnd - $attempt.intervalStart
        $union += [Math]::Max(0L, $attempt.intervalEnd - [Math]::Max($end, $attempt.intervalStart))
        $end = [Math]::Max($end, $attempt.intervalEnd)
    }
    return $total - $union
}

function Invoke-UpdateSignedCipherIdentityScenario([object] $Context) {
    Register-UpdateArtifacts $Context
    Publish-UpdateAnnouncement $Context 'valid'
    $target = '/' + $Context.update.transferRoute.TrimStart('/')
    $artifacts = @($Context.versions.valid.manifest.artifacts | Where-Object {
        $_.kind -ceq 'Ciphertext' -and -not (Test-UpdateInstalledArtifact $_) -and ([Uri]$_.source).AbsolutePath -ceq $target
    })
    if ($artifacts.Count -ne 1) { throw 'The signed identity scenario needs a real Ciphertext artifact endpoint.' }
    $artifact = $artifacts[0]
    $priorData = $Context.userData
    $priorGeneration = $Context.generation
    try {
        Set-UpdateOnline $Context $false
        $Context.userData = Join-Path $priorData 'changed-signed-cipher'
        $Context.generation = $null
        $null = [IO.Directory]::CreateDirectory($Context.userData)
        $null = Invoke-UpdateRun $Context 'signed-cipher-identity-cold' -Boot Baseline
        $state = Get-UpdateStoreFile $Context 'state'
        $stateSha256 = Get-YLFrozenFileSha256 $state
        $store = [IO.Path]::GetDirectoryName($state)
        $published = Join-Path $store ('objects/' + $artifact.sha256 + '.cipher')
        $journal = Join-Path $store 'journal'
        $candidate = Join-Path $store ('versions/' + $Context.versions.valid.digest + '.json')
        $options = @{ Operation = 'prepare'; K2 = 'missing'; ObserveTransfers = $true }
        $wrongTag = 'f' * 64
        if ($wrongTag -ceq $artifact.sha256) { throw 'The fixture expected a different response entity.' }
        Set-UpdateOnline $Context $true
        $Context.server.ShapeTransfer($target, 6MB, 0, $wrongTag, 'changed')
        $result = Invoke-UpdateRun $Context 'signed-cipher-equal-length-wrong-bytes' @options -Outcome TransferFailed
        $lastRun = $Context.report.runs[$Context.report.runs.Count - 1]
        $request = @($lastRun.requests | Where-Object target -CEQ $target)
        $client = @($result.transferReads | Where-Object target -CEQ $target)
        if ($request.Count -ne 1 -or $request[0].method -cne 'GET' -or $request[0].range -or $request[0].status -ne 200 -or
            $request[0].entity -cne ('"' + $wrongTag + '"') -or $request[0].bytes -ne $artifact.length -or
            $client.Count -ne 1 -or $client[0].bytes -ne $artifact.length -or
            $result.operationDetail -cnotmatch 'signed length or SHA-256' -or [IO.File]::Exists($published) -or
            [IO.File]::Exists($candidate) -or [IO.File]::Exists($journal) -or
            (Get-YLFrozenFileSha256 $state) -cne $stateSha256) {
            throw 'The equal-length changed Ciphertext was not rejected before publish and candidate readiness.'
        }
        $Context.server.ShapeTransfer($target, 6MB, 0, $artifact.sha256)
        $null = Invoke-UpdateRun $Context 'signed-cipher-original-entity-retry' @options -Outcome Prepared
        $retry = Save-UpdateTransferMeasurement $Context 'signed-cipher-original-entity-retry' $target $artifact 'Prepared'
        $null = Invoke-UpdateRun $Context 'signed-cipher-identity-warm' -Boot Activated -Active B -Previous A -Highest B -Advance
        $warm = @($Context.report.runs[$Context.report.runs.Count - 1].requests | Where-Object target -CEQ $target)
        if ($warm.Count -ne 0) { throw 'The signed Ciphertext warm restart requested the verified bytes.' }
        Write-UpdateProgress (Join-Path $Context.results 'signed-cipher-identity.json') ([ordered]@{
            artifact = $artifact; signedAnnouncementDigest = $Context.versions.valid.digest; oldStateSha256 = $stateSha256
            rejectedRequest = $request[0]; rejectedClient = $client[0]; rejectionKind = $result.operationStatus
            rejection = $result.operationDetail; prepareKeyRequests = $result.prepareKeyRequests
            prepareMountAttempts = $result.prepareMountAttempts; retry = $retry; warmTargetRequests = $warm.Count
        })
    } finally {
        $Context.server.ClearTransferShape($target)
        $Context.userData = $priorData
        $Context.generation = $priorGeneration
    }
}

function Invoke-UpdateLargeDownloadScenarios([object] $Context) {
    Register-UpdateArtifacts $Context
    Publish-UpdateAnnouncement $Context 'valid'
    $target = '/' + $Context.update.transferRoute.TrimStart('/')
    $artifact = @($Context.versions.valid.manifest.artifacts | Where-Object {
        -not (Test-UpdateInstalledArtifact $_) -and ([Uri]$_.source).AbsolutePath -ceq $target
    })
    if ($artifact.Count -ne 1 -or $artifact[0].length -lt 8MB -or $artifact[0].kind -cne $(if ($Context.encrypted) { 'Ciphertext' } else { 'Bundle' })) {
        throw 'The large experiment must target the expected signed startup artifact.'
    }
    $artifact = $artifact[0]
    $originalData = $Context.userData
    $normalRate = 2MB
    $deadlineSeconds = 300
    $slowRate = [int64][Math]::Floor($artifact.length / ($deadlineSeconds + 28))
    $cut = [int64][Math]::Floor($artifact.length * 0.42)
    $resume = $Context.tuned -and -not $Context.encrypted
    $evidence = [ordered]@{
        target = $target; artifact = $artifact; announcementDigest = $Context.versions.valid.digest
        unity = $Context.editorVersion; addressables = $Context.addressables; backend = $Context.backend
        nominalDeadlineSeconds = $deadlineSeconds; normalBytesPerSecond = $normalRate; slowBytesPerSecond = $slowRate
        endpointCutAfterBytes = $cut; resume = $resume; rounds = [ordered]@{}
    }
    try {
        foreach ($round in @('within-deadline','slow-flow','cut-at-offset')) {
            Set-UpdateOnline $Context $false
            $Context.userData = Join-Path $originalData $round
            $Context.generation = $null
            $null = [IO.Directory]::CreateDirectory($Context.userData)
            $null = Invoke-UpdateRun $Context "$round-cold" -Boot Baseline
            Set-UpdateOnline $Context $true
            $attempts = [Collections.Generic.List[object]]::new()
            $expected = switch ($round) {
                'within-deadline' { 'Prepared' }
                'slow-flow' { if ($Context.tuned) { 'Prepared' } else { 'TransferTimeout' } }
                'cut-at-offset' { 'TransferFailed' }
            }
            $rate = if ($round -ceq 'slow-flow') { $slowRate } else { $normalRate }
            $stop = if ($round -ceq 'cut-at-offset') { $cut } else { 0 }
            $Context.server.ShapeTransfer($target, $rate, $stop, $artifact.sha256)
            $null = Invoke-UpdateRun $Context "$round-first" -Operation prepare -Outcome $expected -K2 missing -ObserveTransfers
            $first = Save-UpdateTransferMeasurement $Context "$round-first" $target $artifact $expected
            $attempts.Add($first)
            if ($round -ceq 'cut-at-offset' -and ($first.server.termination -cne 'EndpointTruncated' -or $first.client.bytes -ne $cut)) {
                throw 'The endpoint cutoff and actual client receipt did not match.'
            }
            if ($round -ceq 'slow-flow') {
                $duration = ([DateTimeOffset]::Parse($first.client.lastUtc) - [DateTimeOffset]::Parse($first.client.firstUtc)).TotalSeconds
                if ($Context.tuned) {
                    if ($duration -le $deadlineSeconds -or $first.client.bytes -ne $artifact.length -or $first.client.reads -lt 20) {
                        throw 'The tuned client did not complete with continuous progress across the original deadline.'
                    }
                } else {
                    if ($first.client.bytes -ge $artifact.length -or $duration -lt 290 -or $first.client.reads -lt 20) {
                        throw 'The slow-flow deadline was not reached while the client continued reading.'
                    }
                    $null = Invoke-UpdateRun $Context 'slow-flow-explicit-retry' -Operation prepare -Outcome TransferTimeout -K2 missing -ObserveTransfers
                    $attempts.Add((Save-UpdateTransferMeasurement $Context 'slow-flow-explicit-retry' $target $artifact 'TransferTimeout'))
                }
            }
            if ($expected -cne 'Prepared') {
                $start = 0L
                if ($resume) {
                    $checkpoint = Read-UpdateCheckpoint $Context $artifact
                    $start = $checkpoint.offset
                    if ($start -ne $cut -or [IO.File]::Exists($checkpoint.published)) {
                        throw 'The interrupted signed artifact did not preserve exactly the verified, flushed client prefix.'
                    }
                    $first['checkpoint'] = $checkpoint
                }
                $Context.server.ShapeTransfer($target, $normalRate, 0, $artifact.sha256)
                $null = Invoke-UpdateRun $Context "$round-restored-rate" -Operation prepare -Outcome Prepared -K2 missing -ObserveTransfers
                $attempts.Add((Save-UpdateTransferMeasurement $Context "$round-restored-rate" $target $artifact 'Prepared' $start))
            }
            $null = Invoke-UpdateRun $Context "$round-warm" -Boot Activated -Active B -Previous A -Highest B -Advance
            $warm = @($Context.report.runs[$Context.report.runs.Count - 1].requests | Where-Object target -CEQ $target)
            if ($warm.Count -ne 0) { throw 'The warm run requested the measured artifact.' }
            $repeated = Measure-UpdateRepeatedBytes $attempts.ToArray()
            if ($resume -and $repeated -ne 0) { throw 'The resumed preparation repeated signed body bytes.' }
            $evidence.rounds[$round] = @{ attempts = $attempts.ToArray(); repeatedBodyBytes = $repeated; warmBodyBytes = 0 }
            Write-UpdateProgress (Join-Path $Context.results 'large-download.json') $evidence
        }
    } finally {
        $Context.server.ClearTransferShape($target)
        $Context.userData = $originalData
    }
}

function Invoke-UpdatePairedTransferScenarios([object] $Context) {
    $baseline = $Context.replay
    Register-UpdateArtifacts $Context
    $baseline.update = $Context.update
    $baseline.versions = $Context.versions
    $sealed = Invoke-YLFrozenReadOnlyInstallation -OwnedHostRoot $baseline.host.path -InstallRoot $baseline.install -InstallDirectoryName P -RunPlayer {
        $baseline.online = $Context.online
        Invoke-UpdateLargeDownloadScenarios $baseline
    }
    $baseline.report['updateReadOnly'] = $sealed.proof
    Assert-UpdateInstallation $baseline
    $Context.online = $baseline.online
    Invoke-UpdateLargeDownloadScenarios $Context
    $before = Read-YLPackageEvidenceJson (Join-Path $baseline.results 'large-download.json')
    $after = Read-YLPackageEvidenceJson (Join-Path $Context.results 'large-download.json')
    if ($before.announcementDigest -cne $after.announcementDigest -or $before.artifact.sha256 -cne $after.artifact.sha256 -or
        $before.artifact.source -cne $after.artifact.source -or $before.artifact.length -ne $after.artifact.length -or
        $before.normalBytesPerSecond -ne $after.normalBytesPerSecond -or $before.slowBytesPerSecond -ne $after.slowBytesPerSecond -or
        $before.endpointCutAfterBytes -ne $after.endpointCutAfterBytes) {
        throw 'The players were not exercised on the same publication and fault schedule.'
    }
    $deltas = [ordered]@{}
    foreach ($round in $before.rounds.Keys) {
        $deltas[$round] = $before.rounds[$round].repeatedBodyBytes - $after.rounds[$round].repeatedBodyBytes
    }
    $comparison = [ordered]@{
        baselineInput = $baseline.report.inputHash; candidateInput = $Context.report.inputHash
        baselineInstall = $baseline.report.installationSha256; candidateInstall = $Context.report.installationSha256
        trustSha256 = $baseline.report.trustSha256; catalogSha256 = $baseline.report.catalogSha256
        commonAnnouncementDigest = $before.announcementDigest; artifact = $before.artifact
        repeatedBodyBytesSaved = $deltas
    }
    if ($Context.encrypted) { $comparison['nativePluginSha256'] = $baseline.report.nativePluginSha256 }
    Write-UpdateProgress (Join-Path $Context.results 'transfer-comparison.json') $comparison
}

function Invoke-UpdateOwnedPrefixReclaim([object] $Context, [string] $Target, [object] $Artifact, [object] $Checkpoint, [long] $Rate) {
    $options = @{ Operation = 'prepare'; ObserveTransfers = $true }
    if ($checkpoint.version -ne 2) { throw 'The reclaim scenario needs a persistently owned stage.' }
    $stream = [IO.File]::Open($checkpoint.path, 'Append', 'Write', 'None')
    try {
        # Leave a flushed byte ahead of the recorded offset, with the same on-disk file identity.
        $stream.WriteByte(0)
        $stream.Flush($true)
        $durable = $stream.Length
    } finally { $stream.Dispose() }
    if ([YLTesting.UpdateFileEvidence]::Read($checkpoint.path).Key -cne $checkpoint.fileIdentity -or
        $durable -ne ($checkpoint.offset + 1)) {
        throw 'The invalid prefix did not retain its original file identity.'
    }
    $quotaBefore = [long]$artifact.length - 1
    $quotaAfter = $quotaBefore + $durable
    $quota = @{ ReclaimTestPart = $checkpoint.path; SpaceBeforeBytes = $quotaBefore; SpaceAfterReclaimBytes = $quotaAfter }
    $Context.server.ShapeTransfer($target, $rate, 0, $artifact.sha256)
    $reclaimed = Invoke-UpdateRun $Context 'resume-reclaim-before-preflight' @options @quota -Outcome Prepared
    if ($reclaimed.spacePreflightChecks -lt 1 -or $reclaimed.spacePreflightSawOwnedPart -or
        $reclaimed.spaceFirstAvailableBytes -ne $quotaAfter) {
        throw 'Free space was measured before retiring the rejected owned prefix.'
    }
    return @{ durablePrefixBytes = $durable; constrainedBytes = $quotaBefore
        rereadBytes = $reclaimed.spaceFirstAvailableBytes; spaceChecks = $reclaimed.spacePreflightChecks
        fullRetry = (Save-UpdateTransferMeasurement $Context 'resume-reclaim-before-preflight' $target $artifact 'Prepared') }
}

function Invoke-UpdateContinuation([object] $Context, [string] $Target, [object] $Artifact,
        [object] $Checkpoint, [long] $Rate, [string] $Fault, [object] $Observe) {
    $options = @{ Operation = 'prepare'; ObserveTransfers = $true }
    $busy = $fault -ceq 'bad-prefix-busy'
    if ($fault -ceq 'bad-prefix' -or $busy) { Flip-UpdateArtifactByte $checkpoint.path }
    if ($busy) { $options['HoldRejectedCheckpoint'] = $artifact.sha256 }
    if ($fault -ceq 'same-session-resume') {
        $options['InspectStagingRoute'] = $target
        $options['InspectStagingPath'] = $checkpoint.path
    }
    $behavior = switch ($fault) {
        'ignore-range' { 'ignore' }
        'changed-entity' { 'changed' }
        'bad-range' { 'wrong-range' }
        'short-range' { 'short-range' }
        'unsatisfiable' { 'unsatisfiable' }
        'bad-encoding' { 'bad-encoding' }
        'changed-range' { 'changed-range' }
        default { 'honor' }
    }
    $tag = if ($fault -ceq 'changed-entity') { 'f' * 64 } else { $artifact.sha256 }
    $Context.server.ShapeTransfer($target, $rate, 0, $tag, $behavior)
    $failure = $fault -cin @('bad-prefix','changed-entity','bad-prefix-busy')
    $second = Invoke-UpdateRun $Context "resume-$fault-reopen" @options -Outcome $(if ($failure) { 'TransferFailed' } else { 'Prepared' })
    $run = $Context.report.runs[$Context.report.runs.Count - 1]
    $gets = @($run.requests | Where-Object target -CEQ $target)
    $suffix = @($second.transferReads | Where-Object target -CEQ $target)
    if ($gets.Count -lt 1 -or $suffix.Count -ne 1 -or $gets[0].range -cne "bytes=$($checkpoint.offset)-" -or
        $gets[0].ifRange -cne $checkpoint.tag) { throw 'The restart did not negotiate the recorded HTTP range.' }
    switch ($fault) {
        { $_ -cin @('ignore-range','changed-entity') } {
            if ($gets.Count -ne 1 -or $gets[0].status -ne 200 -or $suffix[0].bytes -ne $artifact.length -or
                ($fault -ceq 'changed-entity' -and $gets[0].entity -ceq $checkpoint.tag)) {
                throw 'An ignored range or changed entity was not received from byte zero.'
            }
        }
        { $_ -cin @('bad-range','unsatisfiable','bad-encoding','changed-range') } {
            if ($gets.Count -ne 2 -or $gets[1].range -or $gets[1].status -ne 200 -or $suffix[0].bytes -ne $artifact.length) {
                throw 'The malformed continuation did not fall back once to the complete response.'
            }
            $observedFault = switch ($fault) {
                'bad-range' { $gets[0].status -eq 206 -and $gets[0].contentRange -clike "bytes $($checkpoint.offset - 1)-*" }
                'unsatisfiable' { $gets[0].status -eq 416 -and $gets[0].contentRange -ceq "bytes */$($artifact.length)" }
                'bad-encoding' { $gets[0].status -eq 206 -and $gets[0].contentEncoding -ceq 'gzip' }
                'changed-range' { $gets[0].status -eq 206 -and $gets[0].entity -ceq '"changed-entity"' }
            }
            if (-not $observedFault) { throw "The endpoint did not supply the intended continuation fault: $fault" }
        }
        'short-range' {
            $secondOffset = [int64][Math]::Floor($artifact.length / 2)
            if ($gets.Count -ne 2 -or $gets[0].status -ne 206 -or $gets[1].status -ne 206 -or
                $gets[1].range -cne "bytes=$secondOffset-" -or $suffix[0].bytes -ne ($artifact.length - $checkpoint.offset)) {
                throw 'A valid short segment did not advance to the next contiguous range.'
            }
        }
        default {
            if ($gets.Count -ne 1 -or $gets[0].status -ne 206 -or $suffix[0].bytes -ne ($artifact.length - $checkpoint.offset)) {
                throw 'The restarted Player did not receive exactly the remaining body.'
            }
        }
    }
    $observe['retryRequests'] = $gets
    $observe['retryClient'] = $suffix
    if ($fault -ceq 'same-session-resume') {
        $stage = $second.inFlightStage
        if ($null -eq $stage -or $stage.relativePath -cne ('objects/' + [IO.Path]::GetFileName($checkpoint.path)) -or
            -not $stage.ownershipConfirmed -or $stage.disposition -cne 'Retained' -or $stage.retention -cnotmatch 'Preparation') {
            throw 'The open resumed transfer did not report its proven in-flight prefix.'
        }
        $observe['inFlight'] = $stage
    }
    if ($failure) {
        if ($second.operationDetail -cnotmatch 'signed length or SHA-256' -or
            [IO.File]::Exists($checkpoint.published)) {
            throw 'The bad final SHA was not rejected before publication.'
        }
        $null = $options.Remove('HoldRejectedCheckpoint')
        $Context.server.ShapeTransfer($target, $rate, 0, $artifact.sha256)
        if ($busy) {
            if ($second.heldRejectedCheckpointBytes -ne $artifact.length) {
                throw 'The actual rejected part was not held open during checkpoint invalidation.'
            }
            $retired = Read-UpdateCheckpoint $Context $artifact -AllowRetired
            $held = [IO.File]::Open($retired.path, 'Open', 'Read', 'Read')
            try {
                $rejectedSha = Get-YLFrozenFileSha256 $retired.path
                if ($retired.fileIdentity -cne $checkpoint.fileIdentity -or $rejectedSha -ceq $artifact.sha256) {
                    throw 'The retired record no longer identifies the rejected staged file.'
                }
                $null = Invoke-UpdateRun $Context "resume-$fault-clean-retry" @options -Outcome Prepared
                if ([YLTesting.UpdateFileEvidence]::Read($retired.path).Key -cne $retired.fileIdentity -or
                    (Get-YLFrozenFileSha256 $retired.path) -cne $rejectedSha) {
                    throw 'Retry rewrote the held invalid part instead of receiving independently.'
                }
                $observe['retiredWhileHeld'] = $retired
                $observe['retiredPartSha256'] = $rejectedSha
            } finally { $held.Dispose() }
        } else {
            if ([IO.File]::Exists($checkpoint.path) -or [IO.File]::Exists($checkpoint.marker)) {
                throw 'The rejected unoccupied prefix and its checkpoint were not reclaimed.'
            }
            $null = Invoke-UpdateRun $Context "resume-$fault-clean-retry" @options -Outcome Prepared
        }
        $observe['cleanRetry'] = Save-UpdateTransferMeasurement $Context "resume-$fault-clean-retry" $target $artifact 'Prepared'
    }
}

function Invoke-UpdateLegacyPrefixReplacement([object] $Context, [string] $Target, [object] $Artifact,
        [object] $Checkpoint, [long] $Rate) {
    $legacy = Convert-UpdateCheckpointToLegacy $Context $Artifact $Checkpoint
    $replacement = Join-Path ([IO.Path]::GetDirectoryName($legacy.path)) 'foreign-prefix.fixture'
    [IO.File]::Copy($legacy.path, $replacement, $false)
    Flip-UpdateArtifactByte $replacement
    $external = [YLTesting.UpdateFileEvidence]::Read($replacement).Key
    $externalSha256 = Get-YLFrozenFileSha256 $replacement
    if ($external -ceq $Checkpoint.fileIdentity -or [IO.FileInfo]::new($replacement).Length -ne $legacy.offset -or
        $externalSha256 -ceq (Get-YLFrozenFileSha256 $legacy.path)) {
        throw 'The replacement must be different bytes on a different file identity, at the old prefix length.'
    }
    [IO.File]::Move($replacement, $legacy.path, $true)
    if ([YLTesting.UpdateFileEvidence]::Read($legacy.path).Key -cne $external) {
        throw 'The test did not place the replacement identity at the old checkpoint path.'
    }

    $Context.server.ShapeTransfer($Target, $Rate, 0, $Artifact.sha256)
    $null = Invoke-UpdateRun $Context 'resume-v1-foreign-replacement-full-retry' -Operation prepare -ObserveTransfers -Outcome Prepared
    $retry = Save-UpdateTransferMeasurement $Context 'resume-v1-foreign-replacement-full-retry' $Target $Artifact 'Prepared'
    if ([YLTesting.UpdateFileEvidence]::Read($legacy.path).Key -cne $external -or
        [IO.FileInfo]::new($legacy.path).Length -ne $legacy.offset -or
        (Get-YLFrozenFileSha256 $legacy.path) -cne $externalSha256) {
        throw 'Retry rewrote or removed the externally replaced legacy partial file.'
    }
    return @{ checkpoint = $legacy; originalFileIdentity = $Checkpoint.fileIdentity
        externalFileIdentity = $external; externalSha256 = $externalSha256; fullRetry = $retry }
}

function Invoke-UpdateResumeContracts([object] $Context) {
    if (-not $Context.tuned -or $Context.encrypted) { throw 'Resume contracts require the tuned ordinary transfer route.' }
    Register-UpdateArtifacts $Context
    Publish-UpdateAnnouncement $Context 'valid'
    $target = '/' + $Context.update.transferRoute.TrimStart('/')
    $artifact = @($Context.versions.valid.manifest.artifacts | Where-Object {
        $_.kind -ceq 'Bundle' -and -not (Test-UpdateInstalledArtifact $_) -and ([Uri]$_.source).AbsolutePath -ceq $target
    })
    if ($artifact.Count -ne 1) { throw 'The continuation contract needs one real signed Bundle endpoint.' }
    $artifact = $artifact[0]
    $originalData = $Context.userData
    $cut = [int64][Math]::Floor($artifact.length * 0.42)
    $rate = 6MB
    $evidence = [ordered]@{ artifact = $artifact; candidateDigest = $Context.versions.valid.digest; rounds = [ordered]@{} }
    try {
        $faults = @('complete-restart','uncommitted-record-restart','torn-record-restart','bad-prefix','bad-prefix-busy',
            'same-session-resume','ignore-range','changed-entity','bad-range','short-range','observer-cancel',
            'weak-etag','missing-etag','unsatisfiable','bad-encoding','changed-range','invalid-owned-low-space',
            'legacy-v1-complete','legacy-v1-foreign-prefix','legacy-v2-partial')
        $iteration = 0
        foreach ($fault in $faults) {
            Set-UpdateOnline $Context $false
            $Context.userData = Join-Path $originalData ('r' + (++$iteration))
            $Context.generation = $null
            $null = [IO.Directory]::CreateDirectory($Context.userData)
            $null = Invoke-UpdateRun $Context "resume-$fault-cold" -Boot Baseline
            Set-UpdateOnline $Context $true
            $options = @{ Operation = 'prepare'; ObserveTransfers = $true }
            $observe = [ordered]@{}
            if ($fault -cin @('complete-restart','uncommitted-record-restart','torn-record-restart','legacy-v1-complete')) {
                $Context.server.ShapeTransfer($target, $rate, 0, $artifact.sha256)
                $stop = if ($fault -cin @('uncommitted-record-restart', 'torn-record-restart')) { 'before-checkpoint-record' }
                    else { 'before-verified-publish' }
                $null = Invoke-UpdateRun $Context "resume-$fault-killed" @options -Stop $stop -StopArtifactSha256 $artifact.sha256
                $killed = $Context.report.runs[$Context.report.runs.Count - 1]
                $checkpoint = $killed.process.killObservation.checkpoint
                $transfers = @($killed.requests | Where-Object target -CEQ $target)
                if ($transfers.Count -ne 1 -or $transfers[0].bytes -ne $artifact.length) {
                    throw 'The forced publication stop did not receive a full signed representation.'
                }
                if ($fault -ceq 'legacy-v1-complete') {
                    $observe['legacyCheckpoint'] = Convert-UpdateCheckpointToLegacy $Context $artifact $checkpoint
                }
                if ($fault -ceq 'torn-record-restart') {
                    $stream = [IO.File]::Open($checkpoint.marker, 'Open', 'ReadWrite', 'None')
                    try {
                        $stream.Position = $checkpoint.headerLength + 48 * ($checkpoint.revision % 2)
                        $writer = [IO.BinaryWriter]::new($stream, [Text.Encoding]::UTF8, $true)
                        try {
                            $writer.Write([long]($checkpoint.revision + 1))
                            $writer.Write([long]$artifact.length)
                            $writer.Flush()
                            $stream.Flush($true)
                        } finally { $writer.Dispose() }
                    } finally { $stream.Dispose() }
                    $earlier = Read-UpdateCheckpoint $Context $artifact -AllowUncommitted
                    if ($earlier.revision -ne $checkpoint.revision -or $earlier.offset -ne $checkpoint.offset -or
                        $earlier.fileIdentity -cne $checkpoint.fileIdentity) {
                        throw 'The intentionally incomplete record did not leave the earlier verified slot.'
                    }
                    $observe['incompleteRecord'] = $earlier
                }
                $result = Invoke-UpdateRun $Context "resume-$fault-local-publication" @options -Outcome Prepared
                $run = $Context.report.runs[$Context.report.runs.Count - 1]
                if (@($run.requests | Where-Object target -CEQ $target).Count -ne 0 -or
                    @($result.transferReads | Where-Object target -CEQ $target).Count -ne 0 -or
                    (Get-YLFrozenFileSha256 $checkpoint.published) -cne $artifact.sha256 -or
                    [YLTesting.UpdateFileEvidence]::Read($checkpoint.published).Key -cne $checkpoint.fileIdentity -or
                    [IO.File]::Exists($checkpoint.path) -or [IO.File]::Exists($checkpoint.marker)) {
                    throw 'The full durable checkpoint did not publish locally with zero artifact requests.'
                }
                $observe['killedCheckpoint'] = $checkpoint
                $observe['localRequests'] = $run.requests
            } else {
                if ($fault -ceq 'observer-cancel') {
                    $Context.server.ShapeTransfer($target, $rate, 0, $artifact.sha256)
                    $stopAt = @{ TransferCancelRoute = $target; TransferCancelAfterBytes = $cut }
                    $result = Invoke-UpdateRun $Context 'resume-observer-canceled' @options @stopAt -Outcome TransferCanceled
                } else {
                    $cutBehavior = switch ($fault) {
                        'weak-etag' { 'weak' }
                        'missing-etag' { 'missing-etag' }
                        default { 'honor' }
                    }
                    $Context.server.ShapeTransfer($target, $rate, $cut, $artifact.sha256, $cutBehavior)
                    $result = Invoke-UpdateRun $Context "resume-$fault-prefix" @options -Outcome TransferFailed
                }
                $client = @($result.transferReads | Where-Object target -CEQ $target)
                if ($fault -cin @('weak-etag','missing-etag')) {
                    $firstRun = $Context.report.runs[$Context.report.runs.Count - 1]
                    $firstGet = @($firstRun.requests | Where-Object target -CEQ $target)
                    $tag = if ($fault -ceq 'weak-etag') { 'W/"' + $artifact.sha256 + '"' } else { $null }
                    $paths = Get-UpdateResumeFiles $Context $artifact
                    if ($firstGet.Count -ne 1 -or $firstGet[0].entity -cne $tag -or $firstGet[0].status -ne 200 -or
                        $client.Count -ne 1 -or $client[0].bytes -ne $cut -or
                        [IO.File]::Exists($paths.part) -or [IO.File]::Exists($paths.marker)) {
                        throw 'A cut entity without a strong validator left selectable resume state.'
                    }
                    $observe['initialRequest'] = $firstGet[0]
                    $observe['initialClient'] = $client[0]
                    $Context.server.ShapeTransfer($target, $rate, 0, $artifact.sha256)
                    $null = Invoke-UpdateRun $Context "resume-$fault-reopen" @options -Outcome Prepared
                    $observe['fullRetry'] = Save-UpdateTransferMeasurement $Context $fault $target $artifact 'Prepared'
                } else {
                    $checkpoint = Read-UpdateCheckpoint $Context $artifact
                    if ($client.Count -ne 1 -or $checkpoint.offset -gt $client[0].bytes -or
                        ($fault -cne 'observer-cancel' -and $checkpoint.offset -ne $cut)) {
                        throw 'The restart checkpoint does not match the observed client prefix.'
                    }
                    $observe['checkpoint'] = $checkpoint
                    if ($fault -ceq 'invalid-owned-low-space') {
                        $observe['reclaim'] = Invoke-UpdateOwnedPrefixReclaim $Context $target $artifact $checkpoint $rate
                    } elseif ($fault -ceq 'legacy-v1-foreign-prefix') {
                        $observe['legacyReplacement'] = Invoke-UpdateLegacyPrefixReplacement $Context $target $artifact $checkpoint $rate
                    } else {
                        if ($fault -ceq 'legacy-v2-partial') {
                            $checkpoint = Convert-UpdateCheckpointToLegacy $Context $artifact $checkpoint -Version 2
                            $observe['legacyCheckpoint'] = $checkpoint
                        }
                        Invoke-UpdateContinuation $Context $target $artifact $checkpoint $rate $fault $observe
                    }
                    if ((Get-YLFrozenFileSha256 $checkpoint.published) -cne $artifact.sha256) {
                        throw 'The fallback or continuation did not publish the signed artifact bytes.'
                    }
                }
            }
            $null = Invoke-UpdateRun $Context "resume-$fault-warm" -Boot Activated -Active B -Previous A -Highest B -Advance
            if (@($Context.report.runs[$Context.report.runs.Count - 1].requests | Where-Object target -CEQ $target).Count -ne 0) {
                throw 'The post-recovery warm restart requested the artifact.'
            }
            if ($fault -ceq 'bad-prefix-busy') {
                Set-UpdateOnline $Context $false
                $cleanup = Invoke-UpdateRun $Context 'resume-reclaim-released-rejected-prefix' -Operation cleanup -Outcome CleanupComplete -Active B -Previous A -Highest B
                $retired = $observe.retiredWhileHeld
                if ($cleanup.cleanupRemoved -lt 2 -or [IO.File]::Exists($retired.path) -or
                    [IO.File]::Exists($retired.marker) -or (Get-YLFrozenFileSha256 $retired.published) -cne $artifact.sha256) {
                    throw 'Fresh cleanup did not reclaim the released rejected pair while retaining the published artifact.'
                }
                $observe['removedAfterRelease'] = $cleanup.cleanupRemoved
            }
            $evidence.rounds[$fault] = $observe
            Write-UpdateProgress (Join-Path $Context.results 'resume-contracts.json') $evidence
        }
    } finally {
        $Context.server.ClearTransferShape($target)
        $Context.userData = $originalData
    }
}
