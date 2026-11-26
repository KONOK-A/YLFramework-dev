#requires -Version 7.6
#requires -PSEdition Core

function Get-YLFrozenPerformanceScenarios {
    'performance-plain-cold'
    'performance-cipher-cold'
    foreach ($sample in 1..5) {
        $order = if ($sample % 2) { 'plain', 'cipher' } else { 'cipher', 'plain' }
        foreach ($path in $order) { "performance-$path-warm-$sample" }
    }
}

function Test-YLFrozenMetricInteger([object] $Value) {
    return $Value -is [int] -or $Value -is [long]
}

function Assert-YLFrozenReadyTiming([Collections.IDictionary] $Sample) {
    foreach ($name in 'elapsedMs','startGateMs','requestedAtMs','smallReadyAtMs','largeReadyAtMs','firstAssetMs','bothAssetsMs') {
        $value = $Sample[$name]
        if (($value -isnot [double] -and -not (Test-YLFrozenMetricInteger $value)) -or -not [double]::IsFinite($value) -or $value -lt 0) {
            throw "Missing or invalid timing metric: $name"
        }
    }
    if ($Sample['elapsedMs'] -le 0 -or $Sample['firstAssetMs'] -lt $Sample['requestedAtMs'] -or $Sample['requestedAtMs'] -lt $Sample['startGateMs'] -or
        $Sample['firstAssetMs'] -ne [Math]::Min($Sample['smallReadyAtMs'], $Sample['largeReadyAtMs']) -or
        $Sample['bothAssetsMs'] -ne [Math]::Max($Sample['smallReadyAtMs'], $Sample['largeReadyAtMs']) -or
        [Math]::Abs($Sample['bothAssetsMs'] - $Sample['requestedAtMs'] - $Sample['elapsedMs']) -gt 0.001) { throw 'Invalid material-ready timing.' }
    foreach ($name in 'preLoadMounted','preLoadKeys','preLoadAuthentications') {
        if (-not (Test-YLFrozenMetricInteger $Sample[$name]) -or $Sample[$name] -ne 0) { throw 'Content work occurred before the business request.' }
    }
}

function Assert-YLFrozenPerformanceMetrics {
    param([Collections.IDictionary] $Sample, [bool] $Encrypted, [bool] $Warm, [object[]] $Inputs, [string] $DeliveryLayout)
    if ($null -eq $Sample -or $Sample['concurrent'] -isnot [bool] -or -not $Sample['concurrent']) { throw 'Incomplete paired performance observation.' }
    if ($Sample['metricsVersion'] -ne 3 -or $Sample['deliveryLayout'] -cne $DeliveryLayout -or
        $Sample['entryPoint'] -cne 'Addressables.LoadAssetAsync<TextAsset>') { throw 'Unexpected performance measurement source or layout.' }
    Assert-YLFrozenReadyTiming $Sample
    foreach ($name in 'startGateMainThreadCpuMs','mainThreadCpuMs','largestObservedFrameMs','remotePrepareMs') {
        $value = $Sample[$name]
        if (($value -isnot [double] -and -not (Test-YLFrozenMetricInteger $value)) -or -not [double]::IsFinite($value) -or $value -lt 0) {
            throw "Missing or invalid timing metric: $name"
        }
    }
    if (($Sample['remotePrepareMs'] -gt 0) -ne ($Encrypted -and $DeliveryLayout -ceq 'Mixed') -or
        $Sample['requestedAtMs'] -lt ($Sample['startGateMs'] + $Sample['remotePrepareMs'])) { throw 'Unexpected explicit remote preparation timing.' }
    foreach ($name in 'processPrivateBefore','processPrivatePeak','processWorkingSetBefore','processWorkingSetPeak','fixturePayloadBytes') {
        if (-not (Test-YLFrozenMetricInteger $Sample[$name]) -or $Sample[$name] -le 0) { throw "Missing or invalid size metric: $name" }
    }
    foreach ($name in 'cacheLogicalBytesAtCompletion','cacheLogicalPeak','engineAssetReportedBytes','startGateCipherHashBytes','loadCipherHashBytes') {
        if (-not (Test-YLFrozenMetricInteger $Sample[$name])) { throw "Missing or invalid read metrics: $name" }
    }
    if ($Sample['enginePeakMemory'] -cne 'unavailable' -or
        ($Sample['engineAssetReportedBytes'] -ne -1 -and $Sample['engineAssetReportedBytes'] -le 0) -or
        $Sample['processPrivatePeak'] -lt $Sample['processPrivateBefore'] -or $Sample['processWorkingSetPeak'] -lt $Sample['processWorkingSetBefore'] -or
        $Sample['fixturePayloadBytes'] -ne (136L * 1MB) -or $Sample['bundles'] -isnot [array]) {
        throw 'Incomplete memory or concurrent Bundle evidence.'
    }
    $expectedBufferSource = if ($Encrypted) { 'source-bound' } else { 'unavailable' }
    if ($Sample['nativeBufferEvidence'] -cne $expectedBufferSource -or $Sample['cacheLogicalPeak'] -ne -1 -or $Sample['cacheLogicalBytesAtCompletion'] -lt 0) {
        throw 'Unexpected memory or logical-cache evidence source.'
    }
    if (-not $Encrypted) {
        if ($Sample['bundles'].Count -ne 0 -or $Sample['bundleReadKind'] -cne 'engine-opaque' -or $Sample['startGateKind'] -cne 'ordinary-unity-cache-readiness' -or
            $Sample['startGateCipherHashBytes'] -ne -1 -or $Sample['loadCipherHashBytes'] -ne -1) { throw 'Unexpected opaque engine read metrics.' }
        return
    }
    if ($Sample['bundles'].Count -ne 2) { throw 'Incomplete native Bundle evidence.' }

    [long] $cipherBytes = 0; [long] $remoteBytes = 0
    $failures = [Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt 2; $i++) {
        $bundle = $Sample['bundles'][$i]
        $inputBundle = $Inputs[$i]
        if ($bundle -isnot [Collections.IDictionary]) { throw 'Missing Bundle read metrics.' }
        foreach ($name in 'clearBytes','cipherBytes','clearSha256','cipherSha256') {
            if ($bundle[$name] -cne $inputBundle[$name]) { throw "Bundle identity differs: $name" }
        }
        foreach ($name in 'clearBytes','cipherBytes','readPlain','readCipher','pages','payloadBufferBound','engineReadPlain','engineReadCipher',
            'preAuthenticationPlainBytes','preAuthenticationCipherBytes','unityReadBufferBytes','officialDownloadBytes') {
            if (-not (Test-YLFrozenMetricInteger $bundle[$name])) { throw "Missing or invalid Bundle read metrics: $name" }
        }
        if ($bundle['cipherBytes'] -lt $bundle['clearBytes'] -or $bundle['clearBytes'] -le 0) { throw 'Invalid physical Bundle lengths.' }
        $cipherBytes += $bundle['cipherBytes']
        if ($inputBundle['mode'] -ceq 'RemoteHttps') { $remoteBytes += $bundle['cipherBytes'] }
        $expectedDownload = if ($Warm -or $inputBundle['mode'] -ceq 'Embedded') { 0L } else { $bundle['cipherBytes'] }
        if ($bundle['preAuthenticationPlainBytes'] -ne $bundle['clearBytes'] -or $bundle['preAuthenticationCipherBytes'] -le 0 -or
            $bundle['officialDownloadBytes'] -ne $expectedDownload -or
            $bundle['engineReadPlain'] -le 0 -or $bundle['engineReadCipher'] -le 0 -or $bundle['pages'] -le 0 -or
            $bundle['readPlain'] -ne ($bundle['preAuthenticationPlainBytes'] + $bundle['engineReadPlain']) -or
            $bundle['readCipher'] -ne ($bundle['preAuthenticationCipherBytes'] + $bundle['engineReadCipher']) -or
            $bundle['payloadBufferBound'] -le 0 -or $bundle['unityReadBufferBytes'] -ne 32KB) {
            throw 'Native Bundle read metrics do not separate authentication and Unity reads.'
        }
        if ($bundle['payloadBufferBound'] -gt 4MB) { $failures.Add("Bundle $i exceeds the 4 MiB buffer budget.") }
        # Each fixture spans several segments. The stream microbenchmark separately checks the second sequential pass.
        if ($bundle['preAuthenticationCipherBytes'] -lt $bundle['cipherBytes'] -or $bundle['preAuthenticationCipherBytes'] -gt 1.02 * $bundle['cipherBytes']) {
            $failures.Add("Bundle $i measured a short or repeated ciphertext read during pre-authentication.")
        }
        foreach ($name in 'cipherShaMs','authenticationMs') {
            $value = $bundle[$name]
            if (($value -isnot [double] -and -not (Test-YLFrozenMetricInteger $value)) -or -not [double]::IsFinite($value) -or $value -le 0 -or $value -gt $Sample['elapsedMs']) {
                throw "Invalid protected Bundle phase timing: $name"
            }
        }
    }
    $scanBytes = if ($Warm) { $remoteBytes } else { 0L }
    $loadBytes = if ($Warm) { $cipherBytes } else { $cipherBytes + $remoteBytes }
    if ($Sample['bundleReadKind'] -cne 'native-measured' -or $Sample['startGateKind'] -cne 'frozen-catalog-and-cipher-cache' -or
        $Sample['startGateCipherHashBytes'] -ne $scanBytes -or $Sample['loadCipherHashBytes'] -lt 0 -or
        $Sample['cacheLogicalBytesAtCompletion'] -ne $remoteBytes) {
        throw 'Incomplete managed cipher hash metrics or dedicated cache size evidence.'
    }
    if ($Sample['loadCipherHashBytes'] -ne $loadBytes) { $failures.Add('Unexpected ciphertext SHA read volume during asset loading.') }
    $failures.ToArray()
}

function Get-YLFrozenPerformanceSummary {
    param([object[]] $Runs, [Collections.IDictionary] $Build, [string] $DeliveryLayout)
    $expected = @(Get-YLFrozenPerformanceScenarios)
    if ($Runs.Count -ne $expected.Count) { throw 'Performance acceptance requires twelve independent Player samples.' }
    if ($Build.performanceInputs.Count -ne 2 -or $Build.plainCatalogSha256 -ceq $Build.shippedCatalogSha256 -or
        @($Build.input, $Build.plainCatalogSha256, $Build.shippedCatalogSha256 | Where-Object { $_ -cnotmatch '^[0-9a-f]{64}$' }).Count) {
        throw 'Incomplete paired build identity.'
    }
    $expectedModes = switch -CaseSensitive ($DeliveryLayout) {
        'Remote' { 'RemoteHttps,RemoteHttps' }
        'Mixed' { 'Embedded,RemoteHttps' }
        'Embedded' { 'Embedded,Embedded' }
        default { throw 'Unrecognized performance layout.' }
    }
    if (($Build.performanceInputs.mode -join ',') -cne $expectedModes -or $Build.performanceInputs[0].clearBytes -lt 8MB -or
        $Build.performanceInputs[1].clearBytes -lt 128MB -or @($Build.performanceInputs | Where-Object { $_.clearSha256 -cnotmatch '^[0-9a-f]{64}$' -or $_.cipherSha256 -cnotmatch '^[0-9a-f]{64}$' }).Count) {
        throw 'Unexpected performance fixture size, identity or mode.'
    }
    $pids = [Collections.Generic.HashSet[int]]::new()
    $failures = [Collections.Generic.List[string]]::new()
    $samples = @(for ($i = 0; $i -lt $expected.Count; $i++) {
        $run = $Runs[$i]
        $processId = $run.process.pid
        $sample = $run.observed.performance
        $requests = @($run.requests)
        if (-not (Test-YLFrozenMetricInteger $processId) -or $processId -le 0 -or $processId -gt [int]::MaxValue -or -not $pids.Add($processId)) {
            throw 'Each performance sample requires an independent Player PID.'
        }
        if ($run.scenario -cne $expected[$i] -or $sample -isnot [Collections.IDictionary] -or $sample['mode'] -cne $expected[$i] -or
            $run.observed.sentinel -isnot [bool] -or -not $run.observed.sentinel) { throw 'Incomplete paired performance observation.' }
        $encrypted = $run.scenario -clike 'performance-cipher-*'
        $catalog = if ($encrypted) { $Build.shippedCatalogSha256 } else { $Build.plainCatalogSha256 }
        if ($run.observed.inputHash -cne $Build.input -or $run.observed.catalogSha256 -cne $catalog) { throw 'Wrong input or Catalog identity for a paired sample.' }
        $warm = $i -ge 2
        if ($warm -and $requests.Count -ne 0) { throw 'The online warm performance sample received a content request.' }
        foreach ($failure in @(Assert-YLFrozenPerformanceMetrics -Sample $sample -Encrypted $encrypted -Warm $warm -Inputs $Build.performanceInputs -DeliveryLayout $DeliveryLayout)) {
            $failures.Add("$($run.scenario): $failure")
        }
        [long] $endpointBytes = 0
        foreach ($request in $requests) { $endpointBytes += $request.bytes }
        [ordered]@{ scenario = $run.scenario; pid = $processId; endpointBytes = $endpointBytes; requestCount = $requests.Count;
            loadMs = $sample.elapsedMs; startGateMs = $sample.startGateMs; firstAssetMs = $sample.firstAssetMs; bothAssetsMs = $sample.bothAssetsMs }
    })
    $result = [ordered]@{ metricsVersion = 3; deliveryLayout = $DeliveryLayout; order = $expected }
    foreach ($path in 'plain', 'cipher') {
        $warm = @($samples | Where-Object scenario -CLike "performance-$path-warm-*")
        $stats = [ordered]@{ samples = $warm }
        foreach ($metric in 'loadMs','startGateMs','firstAssetMs','bothAssetsMs') {
            $v = @($warm | ForEach-Object { $_[$metric] } | Sort-Object)
            $stats[$metric] = @{ min = $v[0]; median = $v[2]; max = $v[4] }
        }
        $result[$path] = [ordered]@{ cold = @($samples | Where-Object scenario -CEQ "performance-$path-cold")[0]; warm = $stats }
    }
    if ($result.cipher.warm.loadMs.median -gt 1000) { $failures.Add('The median warm paired load exceeds 1000 ms.') }
    $result['passed'] = $failures.Count -eq 0
    $result['budgetFailures'] = $failures.ToArray()
    $result['historicalStartupComparison'] = if ($DeliveryLayout -ceq 'Remote') { 'NotCompared' } else { 'NotApplicable' }
    return $result
}

function Get-YLFrozenRemoteStartupComparison {
    param([Collections.IDictionary] $Candidate, [Collections.IDictionary] $Baseline, [Collections.IDictionary] $ControlledWindow)
    if ($Candidate.purpose -cnotin @('YLResource.FrozenDelivery.LocalFixture', 'YLResource.FrozenDelivery.LocalTarball') -or
        $Baseline.purpose -cne 'YLResource.FrozenDelivery.HistoricalRemoteControl' -or
        $Baseline.sourceCommit -cne '8a848d4eaf3854b8e5b35c76af2e287858be9a47' -or
        $Baseline.pluginSha256 -cne '963ba62687c2f895649923138083b78acff7bba470ce92ba90228c27e9a46f5c' -or
        $Candidate.metricsVersion -ne 3 -or $Baseline.metricsVersion -ne 2) { throw 'Unrecognized candidate or fixed historical measurement identity.' }
    foreach ($run in $Candidate, $Baseline) {
        if ($run.status -cne 'Passed' -or $run.mode -cne 'Performance' -or $run.deliveryLayout -cne 'Remote') {
            throw 'Startup comparison requires two completed Remote performance receipts.'
        }
    }
    $candidateTiming = Get-YLFrozenPerformanceSummary -Runs $Candidate.runs -Build $Candidate.build -DeliveryLayout Remote
    if (-not $candidateTiming.passed) { throw 'The candidate did not satisfy its own performance budgets.' }
    $reasons = [Collections.Generic.List[string]]::new()
    foreach ($name in 'profile','backend','addressables') {
        if ([string]::IsNullOrEmpty($Candidate[$name]) -or $Candidate[$name] -cne $Baseline[$name]) { $reasons.Add("Different Player profile: $name") }
    }
    foreach ($name in 'unity','stripping') {
        if ([string]::IsNullOrEmpty($Candidate.build[$name]) -or $Candidate.build[$name] -cne $Baseline.build[$name]) { $reasons.Add("Different Player build setting: $name") }
    }
    foreach ($name in 'os','processor','logicalProcessors','volume','fileSystem','runtime','timingOrigin') {
        if ([string]::IsNullOrEmpty($Candidate.performanceHost[$name]) -or $Candidate.performanceHost[$name] -cne $Baseline.performanceHost[$name]) {
            $reasons.Add("Different or unavailable host evidence: $name")
        }
    }
    if ($Candidate.performanceHost.osFileCache -cne 'not flushed' -or $Baseline.performanceHost.osFileCache -cnotin @('not flushed','not flushed or claimed cold')) {
        $reasons.Add('The OS file-cache conditions differ or were not recorded.')
    }
    $currentInputs = @($Candidate.build.performanceInputs | ForEach-Object { "$($_.clearBytes):$($_.clearSha256)" } | Sort-Object -CaseSensitive)
    $oldInputs = @($Baseline.performanceInputs | ForEach-Object { "$($_.plainLength):$($_.plainSha256)" } | Sort-Object -CaseSensitive)
    if ($oldInputs.Count -ne 2 -or ($currentInputs -join ',') -cne ($oldInputs -join ',')) { $reasons.Add('The physical clear Bundle lengths and SHA-256 differ.') }
    if ($Candidate.performanceHost.timingOrigin -cne 'SubsystemRegistration') { $reasons.Add('The startup timing origin is not SubsystemRegistration.') }
    if ($null -eq $ControlledWindow) { $reasons.Add('No shared controlled measurement window was supplied.') }
    else {
        $start = [DateTimeOffset]$ControlledWindow.startUtc
        $end = [DateTimeOffset]$ControlledWindow.endUtc
        if ($start.Offset -ne [TimeSpan]::Zero -or $end.Offset -ne [TimeSpan]::Zero -or $end -le $start) { throw 'The controlled window must be an increasing UTC interval.' }
    }
    $result = [ordered]@{ status = 'NotComparable'; reasons = @(); controlledWindow = $ControlledWindow; comparison = $null }
    if ($reasons.Count) { $result.reasons = $reasons.ToArray(); return $result }

    $oldScenarios = @('performance-cipher-cold') + @(1..5 | ForEach-Object { "performance-cipher-warm-$_" })
    if (($Baseline.runs.scenario -join ',') -cne ($oldScenarios -join ',')) { throw 'Expected the cold fill and five independent historical warm Player samples.' }
    $graphics = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $intervals = @(foreach ($receipt in $Candidate, $Baseline) {
        $pids = [Collections.Generic.HashSet[int]]::new()
        foreach ($sample in $receipt.runs) {
            $observed = $sample.observed; $process = $sample.process
            if (-not (Test-YLFrozenMetricInteger $process.pid) -or $process.pid -le 0 -or $process.pid -gt [int]::MaxValue -or -not $pids.Add($process.pid) -or
                $process.status -cne 'Exited' -or -not (Test-YLFrozenMetricInteger $process.exitCode) -or $process.exitCode -ne 0 -or
                $process.observedExit -ne $true -or $process.activeProcesses -ne 0 -or $process.timedOut -ne $false -or
                $observed.pid -ne $process.pid -or $observed.status -cne 'Passed' -or
                $observed.architecture -cne 'X64' -or $observed.backend -cne $receipt.backend -or $observed.unity -cne $receipt.build.unity -or
                $observed.graphics -cin @($null,'','Null') -or $observed.sentinel -isnot [bool] -or -not $observed.sentinel) { throw 'Startup comparison requires independent successful Win64 graphics Players.' }
            $null = $graphics.Add($observed.graphics)
            $begin = [DateTimeOffset]$process.startUtc; $finish = [DateTimeOffset]$process.endUtc
            if ($begin -ge $finish) { throw 'Invalid Player run interval.' }
            @{ start = $begin; end = $finish }
        }
    })
    if ($graphics.Count -ne 1) { $reasons.Add('Graphics devices differ.') }
    $previousEnd = $start
    foreach ($interval in ($intervals | Sort-Object { $_.start })) {
        if ($interval.start -lt $previousEnd -or $interval.end -gt $end) {
            $reasons.Add('Player processes overlapped or fell outside the shared controlled window.')
            break
        }
        $previousEnd = $interval.end
    }
    $old = @(foreach ($sample in $Baseline.runs) {
        $row = $sample.observed.performance
        if ($row.mode -cne $sample.scenario -or $row.metricsVersion -ne 2 -or $row.deliveryLayout -cne 'Remote' -or
            $row.entryPoint -cne 'Addressables.LoadAssetAsync<TextAsset>' -or $row.concurrent -isnot [bool] -or -not $row.concurrent -or
            $sample.observed.inputHash -cne $Baseline.build.input -or $sample.observed.catalogSha256 -cne $Baseline.build.shippedCatalogSha256) {
            throw 'Historical Player evidence did not bind to the expected timing entry and Catalog.'
        }
        Assert-YLFrozenReadyTiming $row
        $actual = @($row.bundles | ForEach-Object { "$($_.clearBytes):$($_.clearSha256):$($_.cipherBytes):$($_.cipherSha256)" } | Sort-Object -CaseSensitive)
        $expected = @($Baseline.performanceInputs | ForEach-Object { "$($_.plainLength):$($_.plainSha256):$($_.cipherLength):$($_.cipherSha256)" } | Sort-Object -CaseSensitive)
        if (($actual -join ',') -cne ($expected -join ',')) { throw 'Historical sample Bundle identity differs.' }
        if ($sample.scenario -cmatch '-warm-' -and @($sample.requests).Count -ne 0) { throw 'A historical warm sample issued a content request.' }
        $row
    })
    if ($reasons.Count) { $result.reasons = $reasons.ToArray(); return $result }
    $result.comparison = [ordered]@{}
    foreach ($metric in 'firstAssetMs','bothAssetsMs') {
        $ordered = @($old[1..5] | ForEach-Object { $_[$metric] } | Sort-Object)
        $actual = $candidateTiming.cipher.warm[$metric].median
        $result.comparison[$metric] = @{ baselineWarmMedianMs = $ordered[2]; candidateWarmMedianMs = $actual; differenceMs = $actual - $ordered[2] }
        if ($actual -gt $ordered[2]) { $reasons.Add("The warm median $metric regressed.") }
    }
    $result.status = if ($reasons.Count) { 'Regression' } else { 'Passed' }
    $result.reasons = $reasons.ToArray()
    return $result
}
