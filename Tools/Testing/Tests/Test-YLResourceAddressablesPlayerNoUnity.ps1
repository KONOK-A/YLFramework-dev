#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param([Parameter(Mandatory)][string] $TemporaryRoot)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Common/Workspace.ps1')
$checks = 0
function Check([bool] $Condition, [string] $Reason) {
    if (-not $Condition) { throw $Reason }
    $script:checks++
}
function Reject([scriptblock] $Action, [string] $ExpectedMessage) {
    $rejected = $false
    try { & $Action | Out-Null } catch {
        if ($_.Exception -is [Management.Automation.CommandNotFoundException]) { throw }
        if ($ExpectedMessage -and -not $_.Exception.Message.Contains($ExpectedMessage, [StringComparison]::OrdinalIgnoreCase)) { throw }
        $rejected = $true
    }
    Check $rejected "The Player contract accepted a deliberately invalid fixture. Expected: $ExpectedMessage"
}
function Clone([object] $Value) {
    return ,(ConvertTo-Json -InputObject $Value -Depth 30 | ConvertFrom-Json -AsHashtable -NoEnumerate -DateKind String)
}

$helper = Join-Path $PSScriptRoot '../Common/ResourceAddressablesPlayer.ps1'
Check ([IO.File]::Exists($helper)) 'The official Addressables Player execution/validation contract is missing.'
. $helper
$sandbox = Join-Path (Get-YLTestPath $TemporaryRoot) ('yl-resource-player-contract-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($sandbox)
$manifest = @{
    buildToken = '1234567890abcdef1234567890abcdef'; inputHash = ('a' * 64); contentHash = ('b' * 64)
    unityVersion = '6000.3.27f1'; addressablesVersion = '2.10.3'; stripping = 'Medium'; graphics = $false; buildFlavor = 'Release'
    playerRoot = $sandbox
}
$process = [pscustomobject]@{
    status = 'Exited'; pid = 42123; observedExit = $true; exitCode = 0
    timedOut = $false; cancelled = $false; forcedCleanup = $false
    observerError = $null; cleanupError = $null; jobAssigned = $true; activeProcesses = 0; streamsDrained = $true
}
$report = @{
    schemaVersion = 1; purpose = 'YLResource.Addressables.PlayerRun'; status = 'Passed'; error = $null
    buildToken = $manifest.buildToken; inputHash = $manifest.inputHash; contentHash = $manifest.contentHash
    unityVersion = $manifest.unityVersion; addressablesVersion = $manifest.addressablesVersion
    stripping = 'Medium'; graphicsDeviceType = 'Null'; backend = 'IL2CPP'; pointerBits = 64; buildFlavor = 'Release'
    isEditor = $false; isBatchMode = $true; platform = 'WindowsPlayer'; round = 1; pid = $process.pid
    runtimeAssembly = 'YLFramework.Runtime'; probeAssembly = 'YLFramework.ResourceAddressables.Acceptance'
    loadedAssemblies = @('YLFramework.Runtime', 'YLFramework.ResourceAddressables.Acceptance', 'Unity.Addressables', 'Unity.ResourceManager')
    checks = @('ScopeReadOnce', 'IndependentLease', 'StopBusy', 'FreshVsRent', 'AdditiveSceneClosed', 'OfficialCache', 'FinalOwnershipZero')
    cachePath = (Join-Path $sandbox 'Cache'); cacheBefore = $false; cacheAfter = $true
    cachedBundleName = 'fixture_bundle'; cachedBundleHash = ('c' * 32); downloadBytesBefore = 123; downloadBytesAfter = 0
    leaseCount = 0; scopeCount = 0; requestCount = 0; pendingWorkCount = 0; drainingHandleCount = 0; cleanupErrors = @()
}
$quit = @{
    schemaVersion = 1; purpose = 'YLResource.Addressables.PlayerQuit'; buildToken = $manifest.buildToken
    inputHash = $manifest.inputHash; contentHash = $manifest.contentHash; round = 1; pid = $process.pid
    onApplicationQuit = $true; requestedExitCode = 0
}
$publicationBuild = Clone $manifest; $publicationBuild.publishedSourceSha256 = ('b' * 64)
$publicationReport = Clone $report; $publicationReport.publishedSourceSha256 = $publicationBuild.publishedSourceSha256
Reject { Assert-YLResourceAddressablesPlayerReport -Report $publicationReport -Build $publicationBuild -Process $process -Quit $quit -Round 1 }
$publicationReport.checks += 'PackedPublishedKeys'
Assert-YLResourceAddressablesPlayerReport -Report $publicationReport -Build $publicationBuild -Process $process -Quit $quit -Round 1
$checks++
$bad = Clone $publicationReport; $bad.publishedSourceSha256 = ('1' * 64)
Reject { Assert-YLResourceAddressablesPlayerReport -Report $bad -Build $publicationBuild -Process $process -Quit $quit -Round 1 }

# Synthetic report-reader cases only; the imported assets and ownership are measured in the Player.
$spriteBuild = Clone $publicationBuild
$spriteBuild.packedSpriteGuid = 'd247671078165aa478503bb19e310b94'
$spriteBuild.packedSpriteFixture = @{
    guid = $spriteBuild.packedSpriteGuid; address = 'acceptance/published/packed-sprite'; assetName = 'PackedSprite13x7'
    width = 13; height = 7; pixelsPerUnit = 37
}
$spriteReport = Clone $publicationReport
$spriteMarkers = @('PackedSpriteTypedOwnership', 'PackedSpriteBroadAmbiguity')
$spriteReport.checks += $spriteMarkers
$fixture = $spriteBuild.packedSpriteFixture
$named = "$($fixture.guid)[$($fixture.assetName)]"
$spriteReport.packedSprite = @{
    unityVersion = $spriteBuild.unityVersion; addressablesVersion = $spriteBuild.addressablesVersion
    guid = $fixture.guid; address = $fixture.address; assetName = $fixture.assetName; namedSpriteKey = $named
    retainedSpriteSurvives = $true; leasesBefore = 0; leasesAfter = 0; requestsAfter = 0; pendingWorkAfter = 0; drainingHandlesAfter = 0
    broad = @{
        key = $fixture.address; requestedType = 'UnityEngine.Object'; status = 'Failed'; errorKind = 'InvalidAddress'; leasesOnClaim = -1
        error = 'UnityEngine.Object matches UnityEngine.Sprite and UnityEngine.Texture2D. Specify a narrower asset type.'
    }
    queries = @(foreach ($key in @($fixture.address, $fixture.guid, $named)) {
        $types = if ($key -ceq $named) { 'Sprite' } else { 'Texture2D', 'Sprite', 'Object' }
        foreach ($type in $types) {
            $locations = if ($type -ceq 'Object') { 'Texture2D', 'Sprite' } else { $type }
            @{
                key = $key; requestedType = "UnityEngine.$type"; status = 'Succeeded'; count = @($locations).Count; distinctLocations = @($locations).Count
                locations = @($locations | ForEach-Object { @{ resourceType = "UnityEngine.$_" } })
            }
        }
    })
}
$index = 0
foreach ($field in @('texture', 'sprite', 'namedSprite')) {
    $type = if ($field -ceq 'texture') { 'UnityEngine.Texture2D' } else { 'UnityEngine.Sprite' }
    $spriteReport.packedSprite[$field] = @{
        key = $(if ($field -ceq 'namedSprite') { $named } else { $fixture.address }); requestedType = $type; assetType = $type
        status = 'Succeeded'; assetName = $fixture.assetName; leaseValid = $true; leasesOnClaim = ++$index
        instanceId = $(if ($field -ceq 'texture') { 1520 } else { 1522 }); textureInstanceId = 1520
        width = 13; height = 7; pixelsPerUnit = 37
    }
}
Assert-YLResourceAddressablesPlayerReport -Report $spriteReport -Build $spriteBuild -Process $process -Quit $quit -Round 1
$checks++
$alternate = Clone $spriteReport
$alternate.packedSprite.broad.error = 'Object is ambiguous (Sprite, Texture2D). Request a more specific type.'
Assert-YLResourceAddressablesPlayerReport -Report $alternate -Build $spriteBuild -Process $process -Quit $quit -Round 1
$checks++
foreach ($change in @(
    { param($r) $r['packedSprite'] = $null },
    { param($r) $r.checks = @($r.checks | Where-Object { $_ -cne 'PackedSpriteTypedOwnership' }) },
    { param($r) $r.checks = @($r.checks | Where-Object { $_ -cne 'PackedSpriteBroadAmbiguity' }) },
    { param($r) $r.checks = @($r.checks | Where-Object { $_ -cne 'OfficialCache' }) },
    { param($r) $r.packedSprite.guid = 'a' * 32 },
    { param($r) $r.packedSprite.texture.assetType = 'UnityEngine.Sprite' },
    { param($r) $r.packedSprite.queries[2].distinctLocations = 1 },
    { param($r) $r.packedSprite.queries[5].locations[1].resourceType = 'UnityEngine.Texture2D' },
    { param($r) $r.packedSprite.queries[5].distinctLocations = 1 },
    { param($r) $r.packedSprite.namedSprite.instanceId = 777 },
    { param($r) $r.packedSprite.sprite.textureInstanceId = 777 },
    { param($r) $r.packedSprite.retainedSpriteSurvives = 'true' },
    { param($r) $r.packedSprite.leasesAfter = 1 },
    { param($r) $r.packedSprite.broad.errorKind = 'LoadFailed' },
    { param($r) $r.packedSprite.broad.error = 'An asset key must resolve to exactly one location: acceptance/published/packed-sprite' },
    { param($r) $r.packedSprite.broad.error = 'Object matches Sprite and Texture2D.' },
    { param($r) $r.packedSprite.broad.error = 'Object matches Sprite. Choose a concrete type.' },
    { param($r) $r.packedSprite.broad.error = "Object matches Sprite and Texture2D.`n  at Choose a concrete type." }
)) {
    $bad = Clone $spriteReport; & $change $bad
    Reject { Assert-YLResourceAddressablesPlayerReport -Report $bad -Build $spriteBuild -Process $process -Quit $quit -Round 1 }
}

Assert-YLResourceAddressablesPlayerReport -Report $report -Build $manifest -Process $process -Quit $quit -Round 1
$checks++
foreach ($change in @(
    @{ field = 'status'; value = 'Failed' }, @{ field = 'inputHash'; value = ('d' * 64) },
    @{ field = 'contentHash'; value = ('d' * 64) }, @{ field = 'buildToken'; value = ('d' * 32) },
    @{ field = 'pid'; value = 1 }, @{ field = 'round'; value = 2 }, @{ field = 'isEditor'; value = $true },
    @{ field = 'backend'; value = 'Mono' }, @{ field = 'pointerBits'; value = 32 },
    @{ field = 'platform'; value = 'WindowsEditor' }, @{ field = 'stripping'; value = 'Low' }, @{ field = 'buildFlavor'; value = 'Development' },
    @{ field = 'addressablesVersion'; value = '1.25.0' }, @{ field = 'unityVersion'; value = '2022.3.62f3' },
    @{ field = 'cacheBefore'; value = $true }, @{ field = 'cacheAfter'; value = $false },
    @{ field = 'downloadBytesBefore'; value = 0 }, @{ field = 'downloadBytesAfter'; value = 1 },
    @{ field = 'leaseCount'; value = 1 }, @{ field = 'scopeCount'; value = 1 },
    @{ field = 'requestCount'; value = 1 }, @{ field = 'pendingWorkCount'; value = 1 },
    @{ field = 'drainingHandleCount'; value = 1 }, @{ field = 'cleanupErrors'; value = @('cleanup failure') },
    @{ field = 'checks'; value = @('ScopeReadOnce') }, @{ field = 'loadedAssemblies'; value = @('nunit.framework') }
)) {
    $bad = Clone $report; $bad[$change.field] = $change.value
    Reject { Assert-YLResourceAddressablesPlayerReport -Report $bad -Build $manifest -Process $process -Quit $quit -Round 1 }
}
foreach ($field in @('timedOut', 'cancelled', 'forcedCleanup')) {
    $bad = [pscustomobject](Clone $process); $bad.$field = $true
    Reject { Assert-YLResourceAddressablesPlayerReport -Report $report -Build $manifest -Process $bad -Quit $quit -Round 1 }
}
$bad = Clone $quit; $bad.onApplicationQuit = $false
Reject { Assert-YLResourceAddressablesPlayerReport -Report $report -Build $manifest -Process $process -Quit $bad -Round 1 }
$bad = Clone $quit; $bad.pid = 1
Reject { Assert-YLResourceAddressablesPlayerReport -Report $report -Build $manifest -Process $process -Quit $bad -Round 1 }
$second = Clone $report; $second.round = 2; $second.pid++; $second.cacheBefore = $true; $second.downloadBytesBefore = 0
$secondProcess = [pscustomobject](Clone $process); $secondProcess.pid++
$secondQuit = Clone $quit; $secondQuit.round = 2; $secondQuit.pid++
Assert-YLResourceAddressablesPlayerReport -Report $second -Build $manifest -Process $secondProcess -Quit $secondQuit -Round 2
$checks++
$second.cacheBefore = $false
Reject { Assert-YLResourceAddressablesPlayerReport -Report $second -Build $manifest -Process $secondProcess -Quit $secondQuit -Round 2 }

# Synthetic envelopes test the real report reader, not Unity timing or ownership.
function New-MeasurementReport([int] $Round) {
    $sample = Clone $report
    $sample.round = $Round; $sample.checks = @(); $sample.cacheBefore = $false; $sample.cacheAfter = $false
    $sample.downloadBytesBefore = 0; $sample.cachedBundleName = ''; $sample.cachedBundleHash = ''; $sample.cachePath = ''
    $bare = $Round -eq 3
    $sample.finalState = if ($bare) { 'WorkloadReleased' } else { 'Closed' }
    if ($bare) {
        foreach ($field in @('leaseCount', 'scopeCount', 'requestCount', 'pendingWorkCount', 'drainingHandleCount')) { $sample[$field] = -1 }
    }
    $sample.performance = @{
        schemaVersion = 1; variant = $(if ($bare) { 'Bare' } else { 'YL' }); sampleKind = 'ReleaseFrameTiming'
        nativeRefcounts = 'Unsupported'; maxTextureSize = 4096; supportsRgba32 = $true; finalCachedImages = $(if ($bare) { -1 } else { 0 })
        workloads = @(
            foreach ($spec in @(
                @('first-local', 1, 1), @('warm-local', 24, 24), @('scope-churn', 24, 96), @('parallel-cancel', 8, 64),
                @('label-batch', 12, 24), @('scene-roundtrip', 8, 8), @('instances-fresh', 24, 96), @('instances-repeat', 24, 96)
            ) + $(if (-not $bare) { ,@('diagnostics-on', 24, 96); ,@('images-cache-on', 16, 16); ,@('images-cache-off', 16, 16) })) {
                $name, $iterations, $delivered = $spec
                $instances = $name -like 'instances-*'
                $pooled = $name -ceq 'instances-repeat' -and -not $bare
                $owners = if ($name -ceq 'parallel-cancel') { if ($bare) { 96 } else { 64 } } elseif ($instances) { 1 } else { $iterations }
                $loads = if ($name -ceq 'parallel-cancel') { 96 } elseif (-not $bare -and $name -cin @('scope-churn', 'diagnostics-on')) { 96 } else { $owners }
                $ownerKind = if ($bare) { 'AddressablesHandle' } elseif ($name -cin @('first-local', 'warm-local', 'scope-churn', 'diagnostics-on')) { 'Scope' }
                    elseif ($name -ceq 'scene-roundtrip') { 'SceneLease' } elseif ($name -ceq 'label-batch') { 'BatchLease' } else { 'AssetLease' }
                @{
                    name = $name; iterations = $iterations; status = 'Measured'; ownerKind = $ownerKind
                    loadCalls = $loads; delivered = $delivered; ownersAcquired = $owners; ownersReleased = $owners
                    peakOwned = $(if ($name -ceq 'parallel-cancel') { if ($bare) { 12 } else { 8 } } else { 1 }); finalOwned = 0
                    canceledConsumers = $(if ($name -ceq 'parallel-cancel') { 32 } else { 0 })
                    canceledWhilePending = $(if ($name -ceq 'parallel-cancel') { 12 } else { 0 })
                    createdInstances = $(if ($pooled) { 4 } elseif ($instances) { 96 } else { 0 }); reusedInstances = $(if ($pooled) { 92 } else { 0 })
                    destroyedInstances = $(if ($pooled) { 4 } elseif ($instances) { 96 } else { 0 }); peakLiveInstances = $(if ($instances) { 4 } else { 0 }); finalInstances = 0
                    imageCacheHits = $(if ($name -ceq 'images-cache-on') { 15 } else { 0 })
                    peakIdle = $(if ($pooled) { 4 } elseif ($name -ceq 'images-cache-on') { 1 } else { 0 })
                    diagnosticsSamples = $(if ($name -ceq 'diagnostics-on') { 24 } else { 0 })
                    frameSamples = 3; frameMs = @(1.5, 2.25, 9.5); p50Ms = 2.25; p95Ms = 9.5; p99Ms = 9.5
                    gcStatus = 'NotCollectedRelease'; gcFrameSamples = 0; gcAllocationBytes = -1
                }
            }
        )
    }
    return $sample
}
foreach ($round in @(3, 4)) {
    $sample = New-MeasurementReport $round
    $sampleQuit = Clone $quit; $sampleQuit.round = $round
    Assert-YLResourceAddressablesPlayerReport -Report $sample -Build $manifest -Process $process -Quit $sampleQuit -Round $round
    $checks++
    $bad = Clone $sample; $bad.performance.workloads[0].p95Ms = 2.25
    Reject { Assert-YLResourceAddressablesPlayerReport -Report $bad -Build $manifest -Process $process -Quit $sampleQuit -Round $round }
    $bad = Clone $sample; $bad.performance.workloads[0].ownersReleased = 0
    Reject { Assert-YLResourceAddressablesPlayerReport -Report $bad -Build $manifest -Process $process -Quit $sampleQuit -Round $round }
    $bad = Clone $sample; $bad.performance.workloads[3].canceledWhilePending = 0
    Reject { Assert-YLResourceAddressablesPlayerReport -Report $bad -Build $manifest -Process $process -Quit $sampleQuit -Round $round }
    $bad = Clone $sample; $bad.performance.workloads[7].name = 'instances-fresh'
    Reject { Assert-YLResourceAddressablesPlayerReport -Report $bad -Build $manifest -Process $process -Quit $sampleQuit -Round $round }
}
$sample = New-MeasurementReport 4
$sampleQuit = Clone $quit; $sampleQuit.round = 4
$releaseWide = Clone $sample
$releaseRow = $releaseWide.performance.workloads[1]
$releaseRow.frameSamples = 5000; $releaseRow.frameMs = (,2.25) * 5000
$releaseRow.p50Ms = 2.25; $releaseRow.p95Ms = 2.25; $releaseRow.p99Ms = 2.25
Assert-YLResourceAddressablesPlayerReport -Report $releaseWide -Build $manifest -Process $process -Quit $sampleQuit -Round 4
$checks++
$releaseRow.frameSamples = 32769; $releaseRow.frameMs = (,2.25) * 32769
Reject { Assert-YLResourceAddressablesPlayerReport -Report $releaseWide -Build $manifest -Process $process -Quit $sampleQuit -Round 4 }
$devBuild = Clone $manifest; $devBuild.buildFlavor = 'Development'; $sample.buildFlavor = 'Development'
$sample.performance.sampleKind = 'DevelopmentAllocation'
foreach ($row in $sample.performance.workloads) {
    $row.frameMs = @(); $row.p50Ms = -1; $row.p95Ms = -1; $row.p99Ms = -1
    $row.gcStatus = 'Sampled'; $row.gcFrameSamples = 3; $row.gcAllocationBytes = 1536
}
$sample.performance.workloads[0].gcStatus = 'Unavailable'
$sample.performance.workloads[0].gcFrameSamples = 0; $sample.performance.workloads[0].gcAllocationBytes = -1
Assert-YLResourceAddressablesPlayerReport -Report $sample -Build $devBuild -Process $process -Quit $sampleQuit -Round 4
$checks++
$wide = Clone $sample
$wide.performance.workloads[1].frameSamples = 5000; $wide.performance.workloads[1].gcFrameSamples = 5000
Assert-YLResourceAddressablesPlayerReport -Report $wide -Build $devBuild -Process $process -Quit $sampleQuit -Round 4
$checks++
$wide.performance.workloads[1].frameSamples = 32769; $wide.performance.workloads[1].gcFrameSamples = 32769
Reject { Assert-YLResourceAddressablesPlayerReport -Report $wide -Build $devBuild -Process $process -Quit $sampleQuit -Round 4 }
$bad = Clone $sample; $bad.performance.workloads[0].gcAllocationBytes = 0
Reject { Assert-YLResourceAddressablesPlayerReport -Report $bad -Build $devBuild -Process $process -Quit $sampleQuit -Round 4 }
$bad = Clone $sample; $bad.performance.workloads[1].finalInstances = 1
Reject { Assert-YLResourceAddressablesPlayerReport -Report $bad -Build $devBuild -Process $process -Quit $sampleQuit -Round 4 }
$bad = Clone $sample; $bad.performance.workloads[1].frameMs = @(1.0, 2.0, 3.0)
Reject { Assert-YLResourceAddressablesPlayerReport -Report $bad -Build $devBuild -Process $process -Quit $sampleQuit -Round 4 }
$sample = New-MeasurementReport 4
$sample.performance.workloads = @($sample.performance.workloads | Where-Object { $_.name -cnotlike 'images-*' })
Reject { Assert-YLResourceAddressablesPlayerReport -Report $sample -Build $manifest -Process $process -Quit $sampleQuit -Round 4 }
$sample.performance.supportsRgba32 = $false
$sample.performance.imageUnavailableReason = 'Graphics device does not support the fixture.'
Assert-YLResourceAddressablesPlayerReport -Report $sample -Build $manifest -Process $process -Quit $sampleQuit -Round 4
$checks++

# Coherent synthetic tick receipts exercise the actual reader, not Unity cleanup or machine timing.
function New-ScopeCloseSample([int] $Count, [int[]] $Releases, [long[]] $ReleaseTicks) {
    $slices = @(for ($index = 0; $index -lt $Releases.Count; $index++) {
        @{ frame = 20 + $index; released = $Releases[$index]; releaseTicks = $ReleaseTicks[$index] }
    })
    return @{
        leases = $Count; distinctAssets = $Count; slices = $slices
        closeWallTicks = 300000L * $slices.Count; maxSliceReleaseTicks = [long]($ReleaseTicks | Measure-Object -Maximum).Maximum
    }
}
$scopeBuild = Clone $manifest; $scopeBuild.scopeCloseBenchmark = $true; $scopeBuild.graphics = $true
$scopeReport = Clone $report
$scopeReport.round = 5; $scopeReport.checks = @(); $scopeReport.finalState = 'Closed'; $scopeReport.graphicsDeviceType = 'Direct3D11'
$scopeReport.cacheBefore = $false; $scopeReport.cacheAfter = $false; $scopeReport.downloadBytesBefore = 0
$scopeReport.scopeClose = @{
    stopwatchFrequency = 10000000; vSyncCount = 0; targetFrameRate = -1
    processor = 'Fixture CPU'; graphicsDevice = 'Fixture GPU'
    cases = @(
        New-ScopeCloseSample 1 @(1) @(0)
        New-ScopeCloseSample 100 @(32, 32, 32, 4) @(3200, 3200, 3200, 400)
        New-ScopeCloseSample 1000 (((,32) * 31) + 8) (((,3200) * 31) + 800)
    )
}
$scopeQuit = Clone $quit; $scopeQuit.round = 5
Assert-YLResourceAddressablesPlayerReport -Report $scopeReport -Build $scopeBuild -Process $process -Quit $scopeQuit -Round 5
$checks++

# A slice may stop at the time limit before 32; a single release is not preemptible.
$timed = Clone $scopeReport
$timed.scopeClose.cases[1] = New-ScopeCloseSample 100 @(1, 3, 32, 32, 32) @(17000, 10000, 3200, 3200, 3200)
Assert-YLResourceAddressablesPlayerReport -Report $timed -Build $scopeBuild -Process $process -Quit $scopeQuit -Round 5
$checks++

foreach ($count in @(100, 1000)) {
    $index = if ($count -eq 100) { 1 } else { 2 }
    $bad = Clone $scopeReport
    $bad.scopeClose.cases[$index] = New-ScopeCloseSample $count ((,1) * $count) ((,100) * $count)
    Reject { Assert-YLResourceAddressablesPlayerReport -Report $bad -Build $scopeBuild -Process $process -Quit $scopeQuit -Round 5 } 'yielded before using either budget'
    $bad.scopeClose.cases[$index] = New-ScopeCloseSample $count @($count) @(10000)
    Reject { Assert-YLResourceAddressablesPlayerReport -Report $bad -Build $scopeBuild -Process $process -Quit $scopeQuit -Round 5 } 'exceeded 32 releases in one slice'
}
$bad = Clone $scopeReport
$bad.scopeClose.cases[1] = New-ScopeCloseSample 100 @(33, 32, 32, 3) @(3300, 3200, 3200, 300)
Reject { Assert-YLResourceAddressablesPlayerReport -Report $bad -Build $scopeBuild -Process $process -Quit $scopeQuit -Round 5 } 'exceeded 32 releases in one slice'
$bad = Clone $timed; $bad.scopeClose.cases[1].slices[1].releaseTicks = 9999
Reject { Assert-YLResourceAddressablesPlayerReport -Report $bad -Build $scopeBuild -Process $process -Quit $scopeQuit -Round 5 } 'yielded before using either budget'

foreach ($change in @(
    { param($r) $r.cases = @($r.cases[0], $r.cases[2]) },
    { param($r) $r.cases[1].distinctAssets = 1 },
    { param($r) $r.cases[1].slices[0].released = '32' },
    { param($r) $r.cases[1].slices[0].released = 0 },
    { param($r) $r.cases[1].slices[1].frame = $r.cases[1].slices[0].frame },
    { param($r) $r.cases[1].slices[1].frame++ },
    { param($r) $r.cases[1].slices[0].releaseTicks = -1 },
    { param($r) $r.cases[1].slices[3].released-- },
    { param($r) $r.cases[1].slices[3].released++ },
    { param($r) $r.cases[1].closeWallTicks = 9999 },
    { param($r) $r.cases[1].maxSliceReleaseTicks-- },
    { param($r) $r.cases[1]['slices'] = $null; $r.cases[1].maxFrameTicks = 3200 }
)) {
    $bad = Clone $scopeReport; & $change $bad.scopeClose
    Reject { Assert-YLResourceAddressablesPlayerReport -Report $bad -Build $scopeBuild -Process $process -Quit $scopeQuit -Round 5 } 'scope close'
}
$bad = Clone $scopeBuild; $bad.scopeCloseBenchmark = $false
Reject { Assert-YLResourceAddressablesPlayerReport -Report $scopeReport -Build $bad -Process $process -Quit $scopeQuit -Round 5 }

$serverSource = Join-Path $PSScriptRoot '../ResourceAddressablesContentServer.cs'
Check ([IO.File]::Exists($serverSource)) 'The owned loopback content fixture is missing.'
Add-Type -Path $serverSource
$content = Join-Path $sandbox 'Remote'; $null = [IO.Directory]::CreateDirectory($content)
$file = Join-Path $content 'bundle.bundle'; [IO.File]::WriteAllText($file, 'official-content-fixture')
$files = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
$files.Add('bundle.bundle', (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant())
$server = [YLFramework.Testing.ResourceAddressablesContentServer]::new($content, $manifest.buildToken, $files)
$client = [Net.Http.HttpClient]::new()
try {
    $text = $client.GetStringAsync($server.BaseUrl + 'bundle.bundle?round=1').GetAwaiter().GetResult()
    Check ($text -ceq 'official-content-fixture') 'The loopback server changed the built bytes.'
    $response = $client.GetAsync($server.BaseUrl + 'unknown.bundle?round=1').GetAwaiter().GetResult()
    Check ([int]$response.StatusCode -eq 404) 'The fixture served an unlisted file.'; $response.Dispose()
    $response = $client.GetAsync($server.BaseUrl.Replace($manifest.buildToken, 'unknown') + 'bundle.bundle?round=1').GetAwaiter().GetResult()
    Check ([int]$response.StatusCode -eq 404) 'The fixture accepted a foreign run token.'; $response.Dispose()
    [IO.File]::WriteAllText($file, 'concurrent-change')
    $response = $client.GetAsync($server.BaseUrl + 'bundle.bundle?round=1').GetAwaiter().GetResult()
    Check ([int]$response.StatusCode -eq 409) 'The fixture served bytes that no longer match the frozen build.'; $response.Dispose()
    Check (@($server.Snapshot() | Where-Object { $_.status -eq 200 -and $_.round -eq 1 -and $_.bytes -gt 0 }).Count -eq 1) 'Actual network evidence is missing.'
} finally { $client.Dispose(); $server.Dispose() }

Add-Type -Path (Join-Path $PSScriptRoot '../ResourceEncryption/FrozenLoopback.cs')
$heldServer = [YLFramework.Testing.FrozenLoopback]::new()
$heldClient = [Net.Http.HttpClient]::new()
$heldClient.Timeout = [TimeSpan]::FromSeconds(8)
try {
    Check ($null -ne $heldServer.GetType().GetMethod('HoldNextProtectedTransfer')) 'The frozen fixture cannot hold a protected GET in flight.'
    $heldDirectory = Join-Path $sandbox 'HeldGet'; $null = [IO.Directory]::CreateDirectory($heldDirectory)
    $heldServer.SetRoutes($content, $content, [string[]]@('bundle.bundle'), [string[]]@())
    $heldServer.HoldNextProtectedTransfer($heldDirectory)
    $heldGet = $heldClient.GetStringAsync($heldServer.Prefix + 'r/bundle.bundle')
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while (-not [IO.File]::Exists((Join-Path $heldDirectory 'in-flight')) -and $clock.Elapsed.TotalSeconds -lt 3) {
        Start-Sleep -Milliseconds 10
    }
    $pending = @($heldServer.Snapshot())
    Check ($pending.Count -eq 1 -and $pending[0].held -and $pending[0].bytes -gt 0 -and -not $heldGet.IsCompleted) 'The protected GET was not blocked after partial bytes.'
    [IO.File]::WriteAllText((Join-Path $heldDirectory 'release'), '')
    Check ($heldGet.GetAwaiter().GetResult() -ceq 'concurrent-change') 'The released protected GET did not deliver the full original response.'
    $finished = @($heldServer.Snapshot())
    Check ($finished.Count -eq 1 -and $finished[0].held -and $finished[0].bytes -eq ([IO.FileInfo]$file).Length) 'The held response was not accounted as one transfer.'
} finally { $heldClient.Dispose(); $heldServer.Dispose() }

$lateServer = [YLFramework.Testing.FrozenLoopback]::new()
$allowParsing = [Threading.Tasks.TaskCompletionSource[bool]]::new([Threading.Tasks.TaskCreationOptions]::RunContinuationsAsynchronously)
try {
    Check ($null -ne $lateServer.GetType().GetMethod('CaptureRoundAsync')) 'The frozen loopback cannot drain request handlers at the end of a Player round.'
    $lateServer.HoldNextRequestParsing($allowParsing.Task)
    $endpoint = [uri]::new($lateServer.Prefix)
    $lateClient = [Net.Sockets.TcpClient]::new()
    try {
        $lateClient.Connect($endpoint.Host, $endpoint.Port)
        $wire = [Text.Encoding]::ASCII.GetBytes("HEAD /unregistered HTTP/1.1`r`nHost: $($endpoint.Authority)`r`nConnection: close`r`n`r`n")
        $lateClient.GetStream().Write($wire, 0, $wire.Length)
        $lateClient.Client.Shutdown([Net.Sockets.SocketShutdown]::Send)
    } finally { $lateClient.Dispose() }
    $round = $lateServer.CaptureRoundAsync()
    Check (-not $round.IsCompleted) 'The round sealed before the late HEAD was parsed.'
    $allowParsing.SetResult($true)
    $requests = $round.GetAwaiter().GetResult()
    Check ($requests.Length -eq 1 -and $requests[0].method -ceq 'HEAD' -and $requests[0].target -ceq '/unregistered' -and $requests[0].status -eq 404) 'The late HEAD did not stay in the original Player round.'
    $quiet = $false
    try { [YLFramework.Testing.FrozenLoopback]::RequireQuiet($requests); $quiet = $true } catch { if ($_.Exception.Message -notmatch 'received 1 content request') { throw } }
    Check (-not $quiet) 'The zero-request gate admitted a late non-GET request.'
} finally { $null = $allowParsing.TrySetResult($true); $lateServer.Dispose() }

Check ($null -ne (Get-Command Get-YLResourceAddressablesContentIdentity -ErrorAction SilentlyContinue)) 'Frozen actual Player content validation is missing.'
$aa = Join-Path $sandbox 'aa'; $null = [IO.Directory]::CreateDirectory($aa)
$settings = Join-Path $aa 'settings.json'; [IO.File]::WriteAllText($settings, 'local-runtime-settings-fixture')
$frozen = @{
    streamingAssetsAa = $aa; remoteRoot = $content
    files = @(
        @{ area = 'aa'; path = 'settings.json'; sha256 = (Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash.ToLowerInvariant(); bytes = ([IO.FileInfo]$settings).Length },
        @{ area = 'remote'; path = 'bundle.bundle'; sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(); bytes = ([IO.FileInfo]$file).Length }
    )
}
$canonical = "aa/settings.json$([char]0)$($frozen.files[0].sha256)`nremote/bundle.bundle$([char]0)$($frozen.files[1].sha256)`n"
$frozen.contentHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical))).ToLowerInvariant()
Check ((Get-YLResourceAddressablesContentIdentity -Build $frozen) -ceq $frozen.contentHash) 'The actual-byte fingerprint differs from the Editor canonical contract.'
$bad = Clone $frozen; $bad.files[0].path = '../escape'
Reject { Get-YLResourceAddressablesContentIdentity -Build $bad }
$bad = Clone $frozen; $bad.files[0].bytes++
Reject { Get-YLResourceAddressablesContentIdentity -Build $bad }
$bad = Clone $frozen; $bad.contentHash = ('d' * 64)
Reject { Get-YLResourceAddressablesContentIdentity -Build $bad }
$unexpected = Join-Path $aa 'unknown'; [IO.File]::WriteAllText($unexpected, 'not in the official frozen build')
Reject { Get-YLResourceAddressablesContentIdentity -Build $frozen }
[IO.File]::Delete($unexpected)
[IO.File]::AppendAllText($file, 'changed-after-build')
Reject { Get-YLResourceAddressablesContentIdentity -Build $frozen }

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$runtimeSource = Join-Path $repository 'Assets/Tests/YLFramework/Acceptance/ResourceAddressables/ResourceAddressablesPlayerProbe.cs'
$editorSource = Join-Path $PSScriptRoot '../ResourceAddressablesPlayerEntry.cs'
$options = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default.WithLanguageVersion([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::CSharp9)
$editorOptions = $options.WithPreprocessorSymbols([string[]]@('UNITY_EDITOR'))
foreach ($source in @($runtimeSource, $editorSource, $serverSource)) {
    $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($source), $editorOptions)
    $errors = @($tree.GetDiagnostics() | Where-Object { $_.Severity -eq [Microsoft.CodeAnalysis.DiagnosticSeverity]::Error })
    Check ($errors.Count -eq 0) ("C# syntax errors: " + ($errors -join '; '))
}
$playerOptions = $options.WithPreprocessorSymbols([string[]]@('YL_RESOURCE_ADDRESSABLES_ACCEPTANCE', 'ENABLE_IL2CPP', 'ENABLE_CACHING'))
$tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($runtimeSource), $playerOptions)
Check (@($tree.GetDiagnostics() | Where-Object { $_.Severity -eq [Microsoft.CodeAnalysis.DiagnosticSeverity]::Error }).Count -eq 0) 'Player preprocessor syntax is invalid.'
$tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($runtimeSource), $options.WithPreprocessorSymbols([string[]]@()))
Check ($tree.GetRoot().Members.Count -eq 0) 'Acceptance source entered an ordinary production Player without its explicit define.'
$definition = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../ResourceAddressablesPlayer.Acceptance.asmdef')) | ConvertFrom-Json -AsHashtable
Check ($definition.name -ceq 'YLFramework.ResourceAddressables.Acceptance' -and
    @($definition.defineConstraints).Count -eq 1 -and $definition.defineConstraints[0] -ceq 'UNITY_EDITOR || YL_RESOURCE_ADDRESSABLES_ACCEPTANCE' -and
    @($definition.references | Where-Object { $_ -match 'Tests|NUnit|Editor' }).Count -eq 0) 'The acceptance assembly acquired test/Editor dependencies or lost its isolation define.'
foreach ($script in @($PSCommandPath, $helper)) {
    $tokens = $null; $errors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile($script, [ref]$tokens, [ref]$errors)
    Check ($errors.Count -eq 0) ("PowerShell syntax errors: " + ($errors -join '; '))
}
. (Join-Path $PSScriptRoot '../UnityHost.ps1')
$workspace = Initialize-YLTestWorkspace -WorkspaceRoot (Join-Path $sandbox 'Workspace') -Repository $repository
$ownedHost = New-YLTestDirectory -Workspace $workspace -Kind Host -Key PlayerContract
$ownedResult = New-YLTestDirectory -Workspace $workspace -Kind Result -Key PlayerContract
$failedBuild = Join-Path $ownedResult.path 'build.json'
Write-YLTestJson -Path $failedBuild -Value @{ schemaVersion = 1; purpose = 'YLResource.Addressables.PlayerBuild'; status = 'Failed' }
$rejected = $false
try {
    Invoke-YLResourceAddressablesPlayerRuns -Workspace $workspace -HostRecord $ownedHost -Result $ownedResult -BuildProcess $process `
        -InputIdentity ([pscustomobject]@{ sha256 = $manifest.inputHash }) -BuildReport $failedBuild `
        -UnityVersion $manifest.unityVersion -AddressablesVersion $manifest.addressablesVersion | Out-Null
} catch {
    Check ($_.Exception.Message -match 'actual IL2CPP build report') 'The real owned Workspace/Host/Result bridge failed before reaching its build-result gate.'
    $rejected = $true
}
Check $rejected 'A failed owned build fixture reached Player launch.'
$failedSummary = [IO.File]::ReadAllText((Join-Path $ownedResult.path 'player-summary.json')) | ConvertFrom-Json -AsHashtable
Check ($failedSummary.status -ceq 'Failed' -and $failedSummary.runs.Count -eq 0) 'Refused builds must retain honest failed/no-run evidence.'

Write-YLTestJson -Path (Join-Path $sandbox 'tool-results.json') -Value @{
    purpose = 'YLTesting.ResourceAddressablesPlayer.NoUnity'; status = 'Passed'; checks = $checks
    unityBuild = 'NotRun'; il2cppPlayer = 'NotRun'; fixtureRoot = $sandbox
}
"Passed $checks pure tooling checks. Unity/IL2CPP Player not run. Artifacts: $sandbox"
