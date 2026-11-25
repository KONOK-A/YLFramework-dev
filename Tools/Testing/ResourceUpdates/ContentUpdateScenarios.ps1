#requires -Version 7.6
#requires -PSEdition Core

function Get-UpdateTextSha256([string] $Text) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant()
}

function New-UpdateMaterial([string] $Ordinary = [Guid]::NewGuid().ToString('N'), [switch] $TransferProof) {
    $asset = [Guid]::NewGuid().ToString('N')
    # Keep the actual fixture Bundle large enough for the intercepted body to flush into the receiver's staging file.
    $dependency = if ($TransferProof) { [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(384KB)) }
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
        [ValidateSet('', 'transfer', 'before-commit', 'after-commit')][string] $Stop = ''
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
        stopAt = $(if ($Stop -cin @('before-commit', 'after-commit')) { $Stop } else { '' })
        stopMarkerPath = $stopMarker; encrypted = $Context.encrypted; k1Path = $k1File; k2Path = $k2File
        oldAddress = $Context.oldAddress; newAddress = $Context.newAddress; preexisting = $Preexisting
        legacyCacheCatalog = $legacyCatalog
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
    $processArguments = @{ StartInfo = $start; Log = "$prefix-process.log"; TimeoutMilliseconds = 180000 }
    if ($Stop -eq 'transfer') {
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

function Invoke-UpdateSuccessorScenarios([object] $Context) {
    $Context.report.phase = 'ContentOnlyBuildC'
    $Context.materialReplacementC = New-UpdateMaterial $Context.materialA.ordinary -TransferProof
    $Context.materialNewC = New-UpdateMaterial $Context.materialA.ordinary -TransferProof
    $Context.materialOptionalC = New-UpdateMaterial $Context.materialA.ordinary
    $Context.report['cFixtureCreatedUtc'] = [DateTime]::UtcNow.ToString('o')
    Write-Host "Building content C for $($Context.id) using the existing A installation"
    $Context.updateC = Invoke-UpdateEditorBuild $Context C
    Assert-UpdateInstallation $Context
    Register-UpdateSuccessor $Context

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
