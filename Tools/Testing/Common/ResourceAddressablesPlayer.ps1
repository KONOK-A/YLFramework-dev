#requires -Version 7.6
#requires -PSEdition Core
Set-StrictMode -Version 3.0
. (Join-Path $PSScriptRoot 'Workspace.ps1')
. (Join-Path $PSScriptRoot 'Process.ps1')
. (Join-Path $PSScriptRoot 'Results.ps1')
. (Join-Path $PSScriptRoot 'Packages.ps1')

function Assert-YLResourceAddressablesPlayerReport {
    param(
        [Parameter(Mandatory)][Collections.IDictionary] $Report,
        [Parameter(Mandatory)][Collections.IDictionary] $Build,
        [Parameter(Mandatory)][object] $Process,
        [Parameter(Mandatory)][Collections.IDictionary] $Quit,
        [Parameter(Mandatory)][ValidateRange(1, 4)][int] $Round
    )
    Assert-YLTestProcessSucceeded $Process
    foreach ($field in @('schemaVersion', 'pid', 'round', 'pointerBits', 'leaseCount', 'scopeCount',
        'requestCount', 'pendingWorkCount', 'drainingHandleCount', 'downloadBytesBefore', 'downloadBytesAfter')) {
        if ($Report[$field] -isnot [int] -and $Report[$field] -isnot [long]) { throw "Player report integer is invalid: $field" }
    }
    foreach ($field in @('isEditor', 'isBatchMode', 'cacheBefore', 'cacheAfter')) {
        if ($Report[$field] -isnot [bool]) { throw "Player report boolean is invalid: $field" }
    }
    if ($Report.schemaVersion -ne 1 -or $Report.purpose -cne 'YLResource.Addressables.PlayerRun' -or
        $Report.status -cne 'Passed' -or -not [string]::IsNullOrEmpty($Report.error) -or $Report.isEditor -or
        -not $Report.isBatchMode -or $Report.platform -cne 'WindowsPlayer' -or $Report.backend -cne 'IL2CPP' -or
        $Report.pointerBits -ne 64 -or $Report.pid -ne $Process.pid -or $Report.round -ne $Round -or
        $Report.runtimeAssembly -cne 'YLFramework.Runtime' -or $Report.probeAssembly -cne 'YLFramework.ResourceAddressables.Acceptance') {
        throw 'Actual Windows x64 IL2CPP Player identity or outcome is invalid.'
    }
    foreach ($field in @('buildToken', 'inputHash', 'contentHash', 'unityVersion', 'addressablesVersion', 'stripping', 'buildFlavor')) {
        if ($Report[$field] -isnot [string] -or $Report[$field] -cne $Build[$field]) { throw "Player build/input identity differs: $field" }
    }
    if ($Build.buildFlavor -cnotin @('Release', 'Development')) { throw 'Unknown Player build flavor.' }
    if ([string]::IsNullOrWhiteSpace($Report.graphicsDeviceType) -or
        ($Build.graphics -and $Report.graphicsDeviceType -ceq 'Null') -or
        (-not $Build.graphics -and $Report.graphicsDeviceType -cne 'Null')) { throw 'Actual Player graphics mode differs from the request.' }
    if ($Report.loadedAssemblies -isnot [Array] -or $Report.loadedAssemblies.Count -eq 0 -or
        @($Report.loadedAssemblies | Where-Object { $_ -match '(^UnityEditor(?:\.|$)|nunit|TestRunner|^YLFramework\..*Tests)' }).Count -ne 0 -or
        'YLFramework.Runtime' -cnotin $Report.loadedAssemblies -or $Report.probeAssembly -cnotin $Report.loadedAssemblies) {
        throw 'The Player loaded Editor/test assemblies or omitted its actual runtime/probe.'
    }
    if ($Report.cleanupErrors -isnot [Array] -or $Report.cleanupErrors.Count -ne 0) { throw 'The Player reported cleanup errors.' }
    if ($Quit.schemaVersion -ne 1 -or $Quit.purpose -cne 'YLResource.Addressables.PlayerQuit' -or
        $Quit.onApplicationQuit -isnot [bool] -or -not $Quit.onApplicationQuit -or $Quit.requestedExitCode -ne 0 -or
        $Quit.pid -ne $Process.pid -or $Quit.round -ne $Round) { throw 'The ordinary OnApplicationQuit receipt is missing or invalid.' }
    foreach ($field in @('buildToken', 'inputHash', 'contentHash')) {
        if ($Quit[$field] -cne $Build[$field]) { throw "The quit receipt has a different build/input identity: $field" }
    }
    if ($Round -gt 2) {
        Assert-YLResourceAddressablesPerformance -Report $Report -Round $Round
        return
    }
    $required = @('ScopeReadOnce', 'IndependentLease', 'StopBusy', 'FreshVsRent', 'AdditiveSceneClosed', 'OfficialCache', 'FinalOwnershipZero')
    if ($Report.checks -isnot [Array] -or $Report.checks.Count -ne $required.Count -or
        @($Report.checks | Select-Object -Unique).Count -ne $required.Count -or
        @($required | Where-Object { $_ -cnotin $Report.checks }).Count) { throw 'The Player did not execute every required assertion group.' }
    if ($Report.cacheBefore -ne ($Round -eq 2) -or -not $Report.cacheAfter -or
        ($Round -eq 1 -and $Report.downloadBytesBefore -le 0) -or ($Round -eq 2 -and $Report.downloadBytesBefore -ne 0) -or
        $Report.downloadBytesAfter -ne 0 -or [string]::IsNullOrWhiteSpace($Report.cachedBundleName) -or
        $Report.cachedBundleHash -cnotmatch '^[0-9a-f]{32}$' -or
        -not [string]::Equals((Get-YLTestPath $Report.cachePath), (Get-YLTestPath (Join-Path $Build.playerRoot 'Cache')), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Official first-load/cache-repeat evidence is invalid.'
    }
    foreach ($field in @('leaseCount', 'scopeCount', 'requestCount', 'pendingWorkCount', 'drainingHandleCount')) {
        if ($Report[$field] -ne 0) { throw "The Player retained YL ownership after StopAsync: $field" }
    }
}

function Assert-YLResourceAddressablesPerformance {
    param([Collections.IDictionary] $Report, [int] $Round)
    $bare = $Round -eq 3
    $release = $Report.buildFlavor -ceq 'Release'
    $data = $Report.performance
    if ($data -isnot [Collections.IDictionary] -or $data.schemaVersion -ne 1 -or
        $data.variant -cne $(if ($bare) { 'Bare' } else { 'YL' }) -or
        $data.sampleKind -cne $(if ($release) { 'ReleaseFrameTiming' } else { 'DevelopmentAllocation' }) -or
        $data.nativeRefcounts -cne 'Unsupported' -or $data.workloads -isnot [Array] -or
        $data.supportsRgba32 -isnot [bool] -or ($data.maxTextureSize -isnot [int] -and $data.maxTextureSize -isnot [long]) -or
        $Report.checks -isnot [Array] -or $Report.checks.Count -ne 0 -or $Report.cacheBefore -or $Report.cacheAfter -or
        $Report.downloadBytesBefore -ne 0 -or $Report.downloadBytesAfter -ne 0) { throw 'Invalid local measurement identity or capabilities.' }
    $residual = if ($bare) { -1 } else { 0 }
    foreach ($field in @('leaseCount', 'scopeCount', 'requestCount', 'pendingWorkCount', 'drainingHandleCount')) {
        if ($Report[$field] -ne $residual) { throw "Invalid final observed measurement ownership: $field" }
    }
    if ($data.finalCachedImages -ne $residual -or $Report.finalState -cne $(if ($bare) { 'WorkloadReleased' } else { 'Closed' })) {
        throw 'Measurement did not report the actual shutdown state.'
    }
    $expected = @{
        'first-local' = @(1, 1); 'warm-local' = @(24, 24); 'scope-churn' = @(24, 96); 'parallel-cancel' = @(8, 64)
        'label-batch' = @(12, 24); 'scene-roundtrip' = @(8, 8); 'instances-fresh' = @(24, 96); 'instances-repeat' = @(24, 96)
    }
    if (-not $bare) {
        $expected.Add('diagnostics-on', @(24, 96))
        if ($data.maxTextureSize -ge 32 -and $data.supportsRgba32) {
            $expected.Add('images-cache-on', @(16, 16)); $expected.Add('images-cache-off', @(16, 16))
        } elseif ([string]::IsNullOrWhiteSpace($data.imageUnavailableReason)) { throw 'Unsupported image evidence has no graphics capability reason.' }
    }
    foreach ($row in $data.workloads) {
        $name = $row.name
        if ($name -isnot [string] -or $name -cnotin $expected.Keys -or $row.status -cne 'Measured') { throw "Invalid or duplicate measured workload: $name" }
        $iterations, $delivered = $expected[$name]
        $expected.Remove($name)
        foreach ($field in @('iterations', 'delivered', 'loadCalls', 'ownersAcquired', 'ownersReleased', 'peakOwned', 'finalOwned',
            'canceledConsumers', 'canceledWhilePending', 'createdInstances', 'reusedInstances', 'destroyedInstances', 'peakLiveInstances',
            'finalInstances', 'peakIdle', 'imageCacheHits', 'diagnosticsSamples', 'frameSamples', 'gcFrameSamples', 'gcAllocationBytes')) {
            if (($row[$field] -isnot [int] -and $row[$field] -isnot [long]) -or ($field -cne 'gcAllocationBytes' -and $row[$field] -lt 0)) {
                throw "Invalid workload integer: $name/$field"
            }
        }
        $parallel = $name -ceq 'parallel-cancel'
        $instances = $name -clike 'instances-*'
        $pooled = -not $bare -and $name -ceq 'instances-repeat'
        $diagnostics = $name -ceq 'diagnostics-on'
        $cached = $name -ceq 'images-cache-on'
        $scoped = -not $bare -and $name -cin @('first-local', 'warm-local', 'scope-churn', 'diagnostics-on')
        $kind = if ($bare) { 'AddressablesHandle' } elseif ($scoped) { 'Scope' } elseif ($name -ceq 'scene-roundtrip') { 'SceneLease' }
            elseif ($name -ceq 'label-batch') { 'BatchLease' } else { 'AssetLease' }
        $owners = if ($parallel) { if ($bare) { 96 } else { 64 } } elseif ($instances) { 1 } else { $iterations }
        $calls = if ($parallel) { 96 } elseif ($scoped) { $delivered } else { $owners }
        $peak = if ($parallel) { if ($bare) { 12 } else { 8 } } else { 1 }
        if ($row.iterations -ne $iterations -or $row.delivered -ne $delivered -or $row.loadCalls -ne $calls -or
            $row.ownerKind -cne $kind -or $row.ownersAcquired -ne $owners -or $row.ownersReleased -ne $owners -or
            $row.peakOwned -ne $peak -or $row.finalOwned -ne 0 -or $row.finalInstances -ne 0 -or
            $row.canceledConsumers -ne $(if ($parallel) { 32 } else { 0 }) -or
            $row.canceledWhilePending -gt $row.canceledConsumers -or ($parallel -and $row.canceledWhilePending -eq 0) -or
            $row.createdInstances + $row.reusedInstances -ne $(if ($instances) { $delivered } else { 0 }) -or
            $row.destroyedInstances -ne $row.createdInstances -or $row.peakLiveInstances -ne $(if ($instances) { 4 } else { 0 }) -or
            ($pooled -and $row.reusedInstances -eq 0) -or (-not $pooled -and $row.reusedInstances -ne 0) -or
            $row.peakIdle -ne $(if ($pooled) { 4 } elseif ($cached) { 1 } else { 0 }) -or
            $row.imageCacheHits -ne $(if ($cached) { $iterations - 1 } else { 0 }) -or
            $row.diagnosticsSamples -ne $(if ($diagnostics) { $iterations } else { 0 })) { throw "Incomplete or inconsistent workload ownership: $name" }
        if ($row.frameSamples -lt 1 -or $row.frameSamples -gt 32768 -or $row.frameMs -isnot [Array]) { throw "Invalid frame window: $name" }
        if ($release) {
            if ($row.frameMs.Count -ne $row.frameSamples -or $row.gcStatus -cne 'NotCollectedRelease' -or
                $row.gcFrameSamples -ne 0 -or $row.gcAllocationBytes -ne -1) { throw "Release timing/GC evidence differs: $name" }
            [double[]] $sorted = foreach ($value in $row.frameMs) {
                if (($value -isnot [int] -and $value -isnot [long] -and $value -isnot [double]) -or
                    -not [double]::IsFinite($value) -or $value -le 0) { throw "Invalid actual frame duration: $name" }
                $value
            }
            [Array]::Sort($sorted)
            foreach ($percentile in @(50, 95, 99)) {
                $value = $row["p${percentile}Ms"]
                if (($value -isnot [int] -and $value -isnot [long] -and $value -isnot [double]) -or
                    $value -ne $sorted[[int][Math]::Ceiling($sorted.Length * $percentile / 100.0) - 1]) {
                    throw "Frame percentile does not match its actual samples: $name/p$percentile"
                }
            }
        } else {
            $gcStatus = if ($row.gcFrameSamples -eq 0) { 'Unavailable' } elseif ($row.gcFrameSamples -eq $row.frameSamples) { 'Sampled' } else { 'Partial' }
            if ($row.frameMs.Count -ne 0 -or $row.p50Ms -ne -1 -or $row.p95Ms -ne -1 -or $row.p99Ms -ne -1 -or
                $row.gcStatus -cne $gcStatus -or $row.gcFrameSamples -gt $row.frameSamples -or
                ($row.gcFrameSamples -eq 0 -and $row.gcAllocationBytes -ne -1) -or
                ($row.gcFrameSamples -gt 0 -and $row.gcAllocationBytes -lt 0)) { throw "Development allocation evidence differs: $name" }
        }
    }
    if ($expected.Count -ne 0) { throw 'Some comparable measurement workloads are missing.' }
}

function Get-YLResourceAddressablesContentIdentity {
    param([Parameter(Mandatory)][Collections.IDictionary] $Build)
    if ($Build.files -isnot [Array] -or $Build.files.Count -eq 0) { throw 'The official content inventory is empty.' }
    $rows = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $expected = @{
        aa = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        remote = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    }
    foreach ($file in $Build.files) {
        if ($file.area -cnotin @('aa', 'remote') -or $file.path -isnot [string] -or
            [string]::IsNullOrWhiteSpace($file.path) -or $file.path -match '[\\:\x00-\x1f]' -or
            $file.path.StartsWith('/') -or @($file.path.Split('/') | Where-Object { $_ -cin @('', '.', '..') }).Count -ne 0 -or
            $file.sha256 -cnotmatch '^[0-9a-f]{64}$' -or ($file.bytes -isnot [long] -and $file.bytes -isnot [int]) -or $file.bytes -lt 0) {
            throw 'Invalid or escaping official content inventory row.'
        }
        $root = if ($file.area -ceq 'aa') { $Build.streamingAssetsAa } else { $Build.remoteRoot }
        $path = Get-YLTestPath (Join-Path $root $file.path)
        if (-not [IO.File]::Exists($path) -or ([IO.FileInfo]$path).Length -ne $file.bytes -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.sha256) {
            throw "Actual built content bytes changed: $($file.area)/$($file.path)"
        }
        $key = $file.area + '/' + $file.path
        if ($rows.ContainsKey($key)) { throw 'Duplicate official content inventory row.' }
        $rows.Add($key, $file.sha256)
        $null = $expected[$file.area].Add($file.path)
    }
    foreach ($area in @('aa', 'remote')) {
        $root = Get-YLTestPath $(if ($area -ceq 'aa') { $Build.streamingAssetsAa } else { $Build.remoteRoot })
        $actual = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($path in Get-YLTestTreeFiles $root) {
            $null = $actual.Add([IO.Path]::GetRelativePath($root, $path).Replace('\', '/'))
        }
        if (-not $actual.SetEquals($expected[$area])) { throw "Unexpected or missing actual built content: $area" }
    }
    [string[]]$keys = @($rows.Keys); [Array]::Sort($keys, [StringComparer]::Ordinal)
    $canonical = [Text.StringBuilder]::new()
    foreach ($key in $keys) { $null = $canonical.Append($key).Append([char]0).Append($rows[$key]).Append("`n") }
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical.ToString()))).ToLowerInvariant()
    if ($hash -cne $Build.contentHash) { throw 'The actual official content fingerprint differs from the built Player.' }
    return $hash
}

function Invoke-YLResourceAddressablesPlayerRuns {
    param(
        [Parameter(Mandatory)][object] $Workspace,
        [Parameter(Mandatory)][object] $HostRecord,
        [Parameter(Mandatory)][object] $Result,
        [Parameter(Mandatory)][object] $InputIdentity,
        [Parameter(Mandatory)][object] $BuildProcess,
        [Parameter(Mandatory)][string] $BuildReport,
        [Parameter(Mandatory)][string] $UnityVersion,
        [Parameter(Mandatory)][string] $AddressablesVersion,
        [ValidateRange(10, 1800)][int] $TimeoutSeconds = 180,
        [ValidateSet('Release', 'Development')][string] $BuildFlavor = 'Release'
    )
    $ownedHost = Get-YLTestDirectory -Workspace $Workspace -Path $HostRecord.path
    $ownedResult = Get-YLTestDirectory -Workspace $Workspace -Path $Result.path
    if ($ownedHost.kind -cne 'Host' -or $ownedHost.id -cne $HostRecord.id -or
        $ownedResult.kind -cne 'Result' -or $ownedResult.id -cne $Result.id) { throw 'The existing Host/Result identities differ.' }
    $BuildReport = Get-YLTestPath $BuildReport
    $resultPrefix = $Result.path.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $BuildReport.StartsWith($resultPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'The build report must belong to this owned Result.' }
    $summaryPath = Join-Path $Result.path 'player-summary.json'
    if ([IO.File]::Exists($summaryPath)) { throw 'Player evidence output already exists; use a new result/build.' }
    $summary = [ordered]@{
        schemaVersion = 1; purpose = 'YLResource.Addressables.PlayerAcceptance'; status = 'Failed'
        inputIdentity = $InputIdentity; buildReport = $BuildReport; buildProcess = $BuildProcess
        runs = @(); measurements = @(); error = $null; serverCleanupError = $null
    }
    $server = $null
    try {
        Assert-YLTestProcessSucceeded $BuildProcess
        if (-not (Get-Command Assert-YLUnityHostIdle -ErrorAction SilentlyContinue)) {
            throw 'Load the existing UnityHost.ps1 before launching a Player; its host-idle contract is required.'
        }
        Assert-YLUnityHostIdle $HostRecord
        $build = Read-YLPackageEvidenceJson $BuildReport
        if ($build.schemaVersion -ne 1 -or $build.purpose -cne 'YLResource.Addressables.PlayerBuild' -or $build.status -cne 'Built' -or
            $build.backend -cne 'IL2CPP' -or $build.target -cne 'StandaloneWindows64' -or $build.buildResult -cne 'Succeeded' -or
            $build.buildErrors -ne 0 -or $build.pid -ne $BuildProcess.pid -or $build.unityVersion -cne $UnityVersion -or
            $build.addressablesVersion -cne $AddressablesVersion -or $build.hostId -cne $HostRecord.id -or $build.resultId -cne $Result.id -or
            $build.workspaceId -cne $Workspace.id -or $build.inputHash -cne $InputIdentity.sha256 -or
            $build.buildToken -cnotmatch '^[0-9a-f]{32}$' -or $build.graphics -isnot [bool] -or
            $build.buildFlavor -cne $BuildFlavor) { throw 'The actual IL2CPP build report does not match this owned invocation.' }
        $hostInputs = Read-YLPackageEvidenceJson (Join-Path $HostRecord.path '.yl-inputs.json')
        if ($hostInputs.inputHash -cne $InputIdentity.sha256 -or $hostInputs.hostId -cne $HostRecord.id) { throw 'The prepared host input fingerprint changed.' }
        foreach ($file in $hostInputs.files) {
            $path = Get-YLTestPath (Join-Path $HostRecord.path $file.path)
            if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $file.sha256) { throw 'A frozen host input changed after the build.' }
        }
        $expectedRoot = Join-Path $HostRecord.path ('P/' + $build.buildToken.Substring(0, 8))
        $expectedRemote = Join-Path $HostRecord.path ('RA/' + $build.buildToken.Substring(0, 8) + '/Remote')
        foreach ($pair in @(
            @($build.playerRoot, $expectedRoot), @($build.player, (Join-Path $expectedRoot 'P.exe')),
            @($build.remoteRoot, $expectedRemote), @($build.streamingAssetsAa, (Join-Path $expectedRoot 'P_Data/StreamingAssets/aa'))
        )) {
            if (-not [string]::Equals((Get-YLTestPath $pair[0]), [IO.Path]::GetFullPath($pair[1]), [StringComparison]::OrdinalIgnoreCase)) {
                throw 'The build points outside its owned generated output locations.'
            }
        }
        $request = Get-YLTestPath $build.requestPath
        if (-not $request.StartsWith($resultPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            (Get-FileHash -LiteralPath $request -Algorithm SHA256).Hash.ToLowerInvariant() -cne $build.requestSha256) { throw 'The original owned build request changed.' }
        $binaryFiles = @(
            @{ path = $build.player; hash = $build.playerSha256 },
            @{ path = (Join-Path $build.playerRoot 'GameAssembly.dll'); hash = $build.gameAssemblySha256 },
            @{ path = (Join-Path $build.playerRoot 'P_Data/il2cpp_data/Metadata/global-metadata.dat'); hash = $build.metadataSha256 }
        )
        $manifest = Read-YLPackageEvidenceJson (Join-Path $build.playerRoot '.yl-resource-addressables-player.json')
        foreach ($field in @('buildToken', 'inputHash', 'contentHash', 'unityVersion', 'addressablesVersion', 'stripping', 'buildFlavor')) {
            if ($manifest[$field] -cne $build[$field]) { throw "The actual Player identity differs from its build report: $field" }
        }
        if (-not ('YLFramework.Testing.ResourceAddressablesContentServer' -as [type])) {
            Add-Type -Path (Join-Path $PSScriptRoot '../ResourceAddressablesContentServer.cs')
        }
        $null = Get-YLResourceAddressablesContentIdentity -Build $build
        $files = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
        foreach ($file in $build.files | Where-Object { $_.area -ceq 'remote' }) { $files.Add($file.path, $file.sha256) }
        $server = [YLFramework.Testing.ResourceAddressablesContentServer]::new($build.remoteRoot, $build.buildToken, $files)
        $first = $null
        $cacheRequests = 0
        for ($round = 1; $round -le 4; $round++) {
            Assert-YLUnityHostIdle $HostRecord
            $null = Get-YLResourceAddressablesContentIdentity -Build $build
            foreach ($binary in $binaryFiles) {
                $path = Get-YLTestPath $binary.path
                if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $binary.hash) { throw 'A native Player binary changed between build and launch.' }
            }
            $native = Join-Path $build.playerRoot "run-$round.json"
            $quitPath = $native + '.quit.json'
            $log = Join-Path $Result.path "player-$round.log"
            foreach ($path in @($native, $quitPath, $log, (Join-Path $Result.path "player-$round.json"), (Join-Path $Result.path "player-$round-quit.json"))) {
                if ([IO.Path]::Exists($path)) { throw 'Unknown or previous Player evidence will not be overwritten.' }
            }
            $start = [Diagnostics.ProcessStartInfo]::new($build.player)
            $start.UseShellExecute = $false; $start.WorkingDirectory = $build.playerRoot
            $start.ArgumentList.Add('-batchmode')
            if (-not $build.graphics) { $start.ArgumentList.Add('-nographics') }
            foreach ($argument in @('-ylResourceAddressablesPlayerResult', $native, '-ylResourceAddressablesPlayerRound', [string]$round,
                '-ylResourceAddressablesContentBaseUrl', $server.BaseUrl, '-logFile', $log)) { $start.ArgumentList.Add($argument) }
            $outcome = Invoke-YLTestProcess -StartInfo $start -Log (Join-Path $Result.path "player-$round-process.log") -TimeoutMilliseconds ($TimeoutSeconds * 1000)
            if ($round -le 2) { $summary.runs += @{ round = $round; process = $outcome } }
            else {
                $summary.measurements += @{ round = $round; variant = $(if ($round -eq 3) { 'Bare' } else { 'YL' })
                    process = $outcome; report = (Join-Path $Result.path "player-$round.json") }
            }
            Write-YLTestJson -Path (Join-Path $Result.path "player-$round-process.json") -Value $outcome
            if ([IO.File]::Exists($native)) { [IO.File]::Copy((Get-YLTestPath $native), (Join-Path $Result.path "player-$round.json"), $false) }
            if ([IO.File]::Exists($quitPath)) { [IO.File]::Copy((Get-YLTestPath $quitPath), (Join-Path $Result.path "player-$round-quit.json"), $false) }
            Assert-YLTestProcessSucceeded $outcome
            $report = Read-YLPackageEvidenceJson $native; $quit = Read-YLPackageEvidenceJson $quitPath
            Assert-YLResourceAddressablesPlayerReport -Report $report -Build $build -Process $outcome -Quit $quit -Round $round
            if ($round -eq 1) { $first = $report }
            elseif ($round -eq 2) {
                if ($first.cachedBundleName -cne $report.cachedBundleName -or $first.cachedBundleHash -cne $report.cachedBundleHash) {
                    throw 'The second process did not read the same official bundle version.'
                }
                $requests = @($server.Snapshot())
                if (@($requests | Where-Object { $_.round -eq 1 -and $_.status -eq 200 -and $_.bytes -gt 0 }).Count -eq 0 -or
                    @($requests | Where-Object { $_.round -eq 2 -and $_.bytes -gt 0 }).Count -ne 0) { throw 'Actual loopback transfers did not match first-download/repeated-cache behavior.' }
                $cacheRequests = $requests.Count
            }
            elseif ($server.Snapshot().Count -ne $cacheRequests) {
                throw 'Local measurements must not use the cold-download/restart loopback content.'
            }
            $null = Get-YLResourceAddressablesContentIdentity -Build $build
        }
        $summary.status = 'Passed'
        return [pscustomobject]$summary
    } catch { $summary.error = $_.Exception.ToString(); throw }
    finally {
        if ($null -ne $server) {
            try { $server.Dispose() }
            catch { $summary.status = 'Failed'; $summary.serverCleanupError = $_.Exception.ToString() }
            Write-YLTestJson -Path (Join-Path $Result.path 'player-content-http.json') -Value @($server.Snapshot())
        }
        Write-YLTestJson -Path $summaryPath -Value $summary
        if ($summary.serverCleanupError) { throw $summary.serverCleanupError }
    }
}
