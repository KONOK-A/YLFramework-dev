#requires -Version 7.6
#requires -PSEdition Core

function Get-YLFrozenPerformanceScenarios {
    foreach ($path in 'plain', 'cipher') {
        "performance-$path-cold"
        foreach ($sample in 1..3) { "performance-$path-warm-$sample" }
    }
}

function Test-YLFrozenMetricInteger([object] $Value) {
    return $Value -is [int] -or $Value -is [long]
}

function Assert-YLFrozenPerformanceMetrics {
    param([Collections.IDictionary] $Sample, [bool] $Encrypted, [bool] $Warm)
    if ($null -eq $Sample -or $Sample['concurrent'] -isnot [bool] -or -not $Sample['concurrent']) { throw 'Incomplete paired performance observation.' }
    foreach ($name in 'elapsedMs','startGateMs','startGateMainThreadCpuMs','mainThreadCpuMs','largestObservedFrameMs') {
        $value = $Sample[$name]
        if (($value -isnot [double] -and -not (Test-YLFrozenMetricInteger $value)) -or -not [double]::IsFinite($value) -or $value -lt 0) {
            throw "Missing or invalid timing metric: $name"
        }
    }
    if ($Sample['elapsedMs'] -le 0) { throw 'Missing material-ready elapsed time.' }
    foreach ($name in 'cacheLogicalBytesAtCompletion','processPrivateBefore','processPrivatePeak','processWorkingSetBefore','processWorkingSetPeak','fixturePayloadBytes') {
        if (-not (Test-YLFrozenMetricInteger $Sample[$name]) -or $Sample[$name] -le 0) { throw "Missing or invalid size metric: $name" }
    }
    foreach ($name in 'cacheLogicalPeak','engineAssetReportedBytes','startGateCipherHashBytes','loadCipherHashBytes') {
        if (-not (Test-YLFrozenMetricInteger $Sample[$name])) { throw "Missing or invalid read metrics: $name" }
    }
    if ($Sample['enginePeakMemory'] -cne 'unavailable' -or
        ($Sample['engineAssetReportedBytes'] -ne -1 -and $Sample['engineAssetReportedBytes'] -le 0) -or
        $Sample['processPrivatePeak'] -lt $Sample['processPrivateBefore'] -or $Sample['processWorkingSetPeak'] -lt $Sample['processWorkingSetBefore'] -or
        $Sample['fixturePayloadBytes'] -ne (136L * 1MB) -or $Sample['bundles'] -isnot [array] -or $Sample['bundles'].Count -ne 2) {
        throw 'Incomplete memory or concurrent Bundle evidence.'
    }

    [long] $cipherBytes = 0
    foreach ($bundle in $Sample['bundles']) {
        if ($bundle -isnot [Collections.IDictionary]) { throw 'Missing Bundle read metrics.' }
        foreach ($name in 'clearBytes','cipherBytes','readPlain','readCipher','pages','payloadBufferBound','engineReadPlain','engineReadCipher',
            'preAuthenticationPlainBytes','preAuthenticationCipherBytes','unityReadBufferBytes') {
            if (-not (Test-YLFrozenMetricInteger $bundle[$name])) { throw "Missing or invalid Bundle read metrics: $name" }
        }
        if ($bundle['cipherBytes'] -lt $bundle['clearBytes'] -or $bundle['clearBytes'] -le 0) { throw 'Invalid physical Bundle lengths.' }
        $cipherBytes += $bundle['cipherBytes']
        if ($Encrypted) {
            if ($bundle['preAuthenticationPlainBytes'] -ne $bundle['clearBytes'] -or $bundle['preAuthenticationCipherBytes'] -le 0 -or
                $bundle['engineReadPlain'] -le 0 -or $bundle['engineReadCipher'] -le 0 -or $bundle['pages'] -le 0 -or
                $bundle['readPlain'] -ne ($bundle['preAuthenticationPlainBytes'] + $bundle['engineReadPlain']) -or
                $bundle['readCipher'] -ne ($bundle['preAuthenticationCipherBytes'] + $bundle['engineReadCipher']) -or
                $bundle['payloadBufferBound'] -le 0 -or $bundle['payloadBufferBound'] -gt 4MB -or $bundle['unityReadBufferBytes'] -ne 32KB) {
                throw 'Native Bundle read metrics do not separate authentication and Unity reads or exceed the buffer budget.'
            }
        } else {
            foreach ($name in 'readPlain','readCipher','pages','payloadBufferBound','engineReadPlain','engineReadCipher',
                'preAuthenticationPlainBytes','preAuthenticationCipherBytes','unityReadBufferBytes') {
                if ($bundle[$name] -ne -1) { throw "The opaque engine control must mark unavailable read metrics: $name" }
            }
        }
    }
    if ($Encrypted) {
        $scanBytes = if ($Warm) { $cipherBytes } else { 0L }
        if ($Sample['bundleReadKind'] -cne 'native-measured' -or $Sample['startGateKind'] -cne 'frozen-catalog-and-cipher-cache' -or
            $Sample['startGateCipherHashBytes'] -ne $scanBytes -or $Sample['loadCipherHashBytes'] -lt (2 * $cipherBytes) -or
            $Sample['cacheLogicalPeak'] -ne $cipherBytes -or $Sample['cacheLogicalBytesAtCompletion'] -ne $cipherBytes) {
            throw 'Incomplete managed cipher hash metrics or dedicated cache size evidence.'
        }
    } elseif ($Sample['bundleReadKind'] -cne 'engine-opaque' -or $Sample['startGateKind'] -cne 'ordinary-unity-cache-readiness' -or
        $Sample['startGateCipherHashBytes'] -ne -1 -or $Sample['loadCipherHashBytes'] -ne -1 -or $Sample['cacheLogicalPeak'] -ne -1) {
        throw 'Unexpected opaque engine read metrics or cache peak evidence.'
    }
}

function Get-YLFrozenPerformanceSummary {
    param([object[]] $Runs)
    $expected = @(Get-YLFrozenPerformanceScenarios)
    if ($Runs.Count -ne $expected.Count) { throw 'Performance acceptance requires eight independent Player samples.' }
    $pids = [Collections.Generic.HashSet[int]]::new()
    $result = [ordered]@{}
    for ($i = 0; $i -lt $expected.Count; $i += 4) {
        $path = if ($i -eq 0) { 'plain' } else { 'cipher' }
        $samples = @(for ($j = $i; $j -lt ($i + 4); $j++) {
            $run = $Runs[$j]
            $processId = $run.process.pid
            $sample = $run.observed.performance
            $requests = @($run.requests)
            if (-not (Test-YLFrozenMetricInteger $processId) -or $processId -le 0 -or $processId -gt [int]::MaxValue -or -not $pids.Add($processId)) {
                throw 'Each performance sample requires an independent Player PID.'
            }
            if ($run.scenario -cne $expected[$j] -or $sample -isnot [Collections.IDictionary] -or $sample['mode'] -cne $expected[$j] -or
                $run.observed.sentinel -isnot [bool] -or -not $run.observed.sentinel) { throw 'Incomplete paired performance observation.' }
            $warmSample = $j -gt $i
            if ($warmSample -and $requests.Count -ne 0) { throw 'The online warm performance sample received a content request.' }
            Assert-YLFrozenPerformanceMetrics -Sample $sample -Encrypted ($path -ceq 'cipher') -Warm $warmSample
            [long] $endpointBytes = 0
            foreach ($request in $requests) { $endpointBytes += $request.bytes }
            [ordered]@{ scenario = $run.scenario; pid = $processId; endpointBytes = $endpointBytes; requestCount = $requests.Count;
                loadMs = $sample.elapsedMs; startGateMs = $sample.startGateMs }
        })
        $warm = $samples[1..3]
        $loads = @($warm.loadMs | Sort-Object)
        $gates = @($warm.startGateMs | Sort-Object)
        $result[$path] = [ordered]@{ cold = $samples[0]; warm = [ordered]@{ samples = $warm;
            loadMs = @{ min = $loads[0]; median = $loads[1]; max = $loads[2] };
            startGateMs = @{ min = $gates[0]; median = $gates[1]; max = $gates[2] } } }
    }
    return $result
}
