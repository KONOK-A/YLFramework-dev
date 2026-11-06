#requires -Version 7.6
#requires -PSEdition Core
. (Join-Path $PSScriptRoot 'Workspace.ps1')

function Get-YLTestInputIdentity {
    param(
        [string] $Repository,
        [string[]] $Paths,
        [string[]] $Toolchain = @(),
        [ValidateNotNullOrEmpty()][string] $Profile
    )
    $Repository = Get-YLTestPath $Repository
    if ($Paths.Count -eq 0) { throw 'An empty input set cannot identify a test selection.' }
    $sources = [Collections.Generic.SortedDictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($relative in $Paths) {
        if ([IO.Path]::IsPathRooted($relative)) { throw 'Source input paths must be repository-relative.' }
        $path = Get-YLTestPath (Join-Path $Repository $relative)
        if (-not $path.StartsWith($Repository + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Source input is outside its repository.' }
        $name = [IO.Path]::GetRelativePath($Repository, $path).Replace('\', '/')
        $stream = [IO.File]::OpenRead($path)
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
        finally { $stream.Dispose() }
        if ($sources.ContainsKey($name)) { throw "Duplicate source input: $name" }
        $sources.Add($name, $hash)
    }
    $tools = [Collections.Generic.SortedDictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($tool in $Toolchain) {
        $path = Get-YLTestPath -Path $tool -ReadOnly
        $stream = [IO.File]::OpenRead($path)
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
        finally { $stream.Dispose() }
        if ($tools.ContainsKey($path)) { throw "Duplicate toolchain input: $path" }
        $tools.Add($path, $hash)
    }
    $identity = [ordered]@{
        schemaVersion = 1; repository = $Repository; profile = $Profile
        files = @($sources.GetEnumerator() | ForEach-Object { [ordered]@{ path = $_.Key; sha256 = $_.Value } })
        toolchain = @($tools.GetEnumerator() | ForEach-Object { [ordered]@{ path = $_.Key; sha256 = $_.Value } })
    }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $identity -Depth 10 -Compress))
    $identity['sha256'] = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    return [pscustomobject]$identity
}

function New-YLTestSelection {
    param([object] $InputIdentity, [AllowEmptyCollection()][object[]] $Tests)
    if ($InputIdentity.schemaVersion -ne 1 -or $InputIdentity.sha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid input identity.' }
    if ($Tests.Count -eq 0) { throw 'An empty/zero discovered selection cannot run.' }
    $selected = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($test in $Tests) {
        if ($test.assembly -cnotmatch '^[A-Za-z0-9_.-]+$' -or [string]::IsNullOrWhiteSpace($test.fullName) -or
            $test.mode -cnotin @('Offline', 'EditMode', 'PlayMode')) { throw 'Invalid discovered test identity.' }
        if ($test.runState -cne 'Runnable') { throw "An unapproved skipped/non-runnable leaf was selected: $($test.fullName)" }
        $key = $test.mode + '|' + $test.assembly + '|' + $test.fullName
        if ($selected.ContainsKey($key)) { throw "Duplicate discovered test identity: $key" }
        $selected.Add($key, [pscustomobject]@{
            assembly = $test.assembly; fullName = $test.fullName; mode = $test.mode; runState = $test.runState
        })
    }
    return [pscustomobject][ordered]@{
        schemaVersion = 1; purpose = 'YLTesting.Selection'; inputIdentity = $InputIdentity
        discoveredUtc = [DateTime]::UtcNow.ToString('o'); tests = @($selected.Values)
    }
}

function Assert-YLTestSelection {
    param([object] $Selection, [object] $InputIdentity)
    if ($Selection.schemaVersion -ne 1 -or $Selection.purpose -cne 'YLTesting.Selection' -or
        $Selection.inputIdentity.sha256 -cne $InputIdentity.sha256 -or
        $Selection.inputIdentity.repository -ine $InputIdentity.repository -or
        $Selection.inputIdentity.profile -cne $InputIdentity.profile) {
        throw 'Stale test selection: current input/profile identity does not match discovery.'
    }
    $null = New-YLTestSelection -InputIdentity $InputIdentity -Tests $Selection.tests
}

function Assert-YLTestProcessSucceeded {
    param([object] $ProcessResult)
    $required = @('status', 'observedExit', 'exitCode', 'timedOut', 'cancelled', 'forcedCleanup',
        'observerError', 'cleanupError', 'jobAssigned', 'activeProcesses', 'streamsDrained')
    foreach ($name in $required) {
        if ($null -eq $ProcessResult -or $null -eq $ProcessResult.PSObject.Properties[$name]) { throw "Incomplete process outcome: $name" }
    }
    if ($ProcessResult.status -cne 'Exited' -or
        ($ProcessResult.exitCode -isnot [int] -and $ProcessResult.exitCode -isnot [long]) -or $ProcessResult.exitCode -ne 0 -or
        ($ProcessResult.activeProcesses -isnot [int] -and $ProcessResult.activeProcesses -isnot [uint32] -and $ProcessResult.activeProcesses -isnot [long]) -or
        $ProcessResult.activeProcesses -ne 0 -or -not [string]::IsNullOrEmpty($ProcessResult.observerError) -or
        -not [string]::IsNullOrEmpty($ProcessResult.cleanupError)) { throw 'The process has no ordinary successful exit/cleanup outcome.' }
    foreach ($name in @('observedExit', 'jobAssigned', 'streamsDrained')) {
        if ($ProcessResult.$name -isnot [bool] -or -not $ProcessResult.$name) { throw "Incomplete process observation: $name" }
    }
    foreach ($name in @('timedOut', 'cancelled', 'forcedCleanup')) {
        if ($ProcessResult.$name -isnot [bool] -or $ProcessResult.$name) { throw "Nonordinary process outcome: $name" }
    }
}

function Read-YLTestXml {
    param(
        [string] $Path,
        [object] $Selection,
        [object] $InputIdentity,
        [ValidateSet('Offline', 'EditMode', 'PlayMode')][string] $Mode,
        [object] $ProcessResult
    )
    Assert-YLTestSelection -Selection $Selection -InputIdentity $InputIdentity
    Assert-YLTestProcessSucceeded $ProcessResult
    $Path = Get-YLTestPath $Path
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 64MB
    $reader = $null
    try {
        $reader = [Xml.XmlReader]::Create($Path, $settings)
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
    } catch { throw "Invalid/incomplete test XML: $($_.Exception.Message)" }
    finally { if ($null -ne $reader) { $reader.Dispose() } }
    $root = $document.DocumentElement
    if ($null -eq $root -or $root.LocalName -cnotin @('test-run', 'test-suite') -or
        ($root.LocalName -ceq 'test-suite' -and $root.GetAttribute('type') -cnotin @('Assembly', 'TestSuite'))) { throw 'Unexpected test XML result root.' }
    if ($root.GetAttribute('result') -cne 'Passed') { throw 'The XML root result is not Passed.' }
    foreach ($suite in $document.SelectNodes('//*[local-name()="test-suite"]')) {
        if ($suite.GetAttribute('result') -cne 'Passed') { throw 'The XML contains a failed/non-passed suite result.' }
    }
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($test in $Selection.tests) {
        if ($test.mode -ceq $Mode) { $null = $expected.Add($test.assembly + '|' + $test.fullName) }
    }
    if ($expected.Count -eq 0) { throw "Zero selected leaves for mode: $Mode" }
    $actual = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($test in $document.SelectNodes('//*[local-name()="test-case"]')) {
        $name = $test.GetAttribute('fullname')
        if ([string]::IsNullOrWhiteSpace($name)) { throw 'Missing XML test identity.' }
        if ($test.GetAttribute('result') -cne 'Passed') { throw "Unapproved skip/failed test result: $name" }
        $assemblyNode = $test.ParentNode
        while ($null -ne $assemblyNode -and ($assemblyNode -isnot [Xml.XmlElement] -or $assemblyNode.GetAttribute('type') -cne 'Assembly')) {
            $assemblyNode = $assemblyNode.ParentNode
        }
        if ($null -eq $assemblyNode) { throw "Missing XML assembly identity: $name" }
        $assembly = [IO.Path]::GetFileName($assemblyNode.GetAttribute('name'))
        if ($assembly.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase) -or $assembly.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) {
            $assembly = $assembly.Substring(0, $assembly.Length - 4)
        }
        $key = $assembly + '|' + $name
        if (-not $actual.Add($key)) { throw "Duplicate XML test identity: $key" }
    }
    if ($actual.Count -eq 0) { throw 'Zero executed XML leaves.' }
    if (-not $expected.SetEquals($actual)) {
        $missing = @($expected | Where-Object { -not $actual.Contains($_) })
        $extra = @($actual | Where-Object { -not $expected.Contains($_) })
        throw "XML selection mismatch. Missing: $($missing -join ', '); extra: $($extra -join ', ')"
    }
    foreach ($counter in @('total', 'passed', 'failed', 'skipped', 'inconclusive')) {
        $number = 0
        if (-not [int]::TryParse($root.GetAttribute($counter), [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture, [ref]$number)) { throw "Invalid XML count: $counter" }
        $wanted = if ($counter -cin @('total', 'passed')) { $actual.Count } else { 0 }
        if ($number -ne $wanted) { throw "XML count mismatch: $counter=$number, expected $wanted" }
    }
    return [pscustomobject]@{ status = 'Passed'; total = $actual.Count; passed = $actual.Count; failed = 0; skipped = 0; mode = $Mode }
}

function Read-YLPlayerSmoke {
    param([string] $Path, [string] $ResultId, [string] $HostId, [string] $UnityVersion, [object] $ProcessResult,
        [ValidateSet('Mono', 'IL2CPP')][string] $Backend = 'Mono', [object] $HybridCLR)
    Assert-YLTestProcessSucceeded $ProcessResult
    $report = [IO.File]::ReadAllText((Get-YLTestPath $Path)) | ConvertFrom-Json -AsHashtable -DateKind String
    foreach ($name in @('purpose', 'status', 'resultId', 'hostId', 'unityVersion', 'backend', 'platform')) {
        if ($report[$name] -isnot [string]) { throw 'Player smoke string identity fields must not use coercible JSON types.' }
    }
    $stages = @('Started', 'ResourceLoaded', 'EventLifecycle', 'BusyOwnerProtected', 'ResourceReleased', 'Shutdown')
    if ($null -ne $HybridCLR) { $stages = @('Started', 'HybridCLRMetadata', 'HybridCLRModule', 'HybridCLRReflection', 'HybridCLRShutdown') }
    if ($report['schemaVersion'] -isnot [long] -or $report['schemaVersion'] -ne 1 -or $report['purpose'] -cne 'YLTesting.PlayerSmoke' -or
        $report['status'] -cne 'Passed' -or $report['resultId'] -cne $ResultId -or $report['hostId'] -cne $HostId -or
        $report['backend'] -cne $Backend -or
        $report['unityVersion'] -cne $UnityVersion -or $report['pid'] -isnot [long] -or $report['pid'] -ne $ProcessResult.pid -or
        $report['isEditor'] -isnot [bool] -or $report['isEditor'] -or $report['platform'] -cne 'WindowsPlayer' -or
        $report['frames'] -isnot [long] -or $report['frames'] -lt 1 -or
        $report['recordCount'] -isnot [long] -or $report['recordCount'] -ne 0 -or
        $report['leaseCount'] -isnot [long] -or $report['leaseCount'] -ne 0 -or
        $report['error'] -isnot [string] -or $report['error'].Length -ne 0 -or
        $report['stages'] -isnot [array] -or $report['stages'].Count -ne $stages.Count) {
        throw 'Player smoke identity, runtime state or behavior evidence is invalid.'
    }
    for ($i = 0; $i -lt $stages.Count; $i++) {
        if ($report['stages'][$i] -isnot [string] -or $report['stages'][$i] -cne $stages[$i]) { throw 'Player smoke behavior stages are incomplete or out of order.' }
    }
    if ($null -ne $HybridCLR) {
        $detail = $report['hybridClr']
        if ($detail -isnot [Collections.IDictionary]) { throw 'HybridCLR native evidence must be an object.' }
        foreach ($name in @('mode', 'codePackageId', 'nativeVariant', 'message', 'asyncResult', 'prefabMarker')) {
            if ($detail[$name] -isnot [string]) { throw 'HybridCLR string evidence must not use coercible JSON types.' }
        }
        if ($Backend -cne 'IL2CPP' -or $detail -isnot [Collections.IDictionary] -or $detail['mode'] -cne 'Native' -or
            $detail['isSimulation'] -isnot [bool] -or $detail['isSimulation'] -or
            $detail['codePackageId'] -cne $HybridCLR.codePackageId -or $detail['codePackageId'] -cnotmatch '^[0-9a-f]{64}$' -or
            $detail['nativeVariant'] -cne $HybridCLR.nativeVariant -or
            $detail['reflectionCount'] -isnot [long] -or $detail['reflectionCount'] -ne 3 -or
            $detail['message'] -cne 'v1: YL Resource configuration; dependency-count=2' -or
            $detail['asyncResult'] -cne 'v1-async' -or $detail['prefabMarker'] -cne 'v1-prefab' -or
            $detail['repeatedLoadReused'] -isnot [bool] -or -not $detail['repeatedLoadReused'] -or
            $detail['wrongBaseRejected'] -isnot [bool] -or -not $detail['wrongBaseRejected'] -or
            $report['graphicsDevice'] -isnot [string] -or $report['graphicsDevice'] -cin @('', 'Null')) {
            throw 'HybridCLR native identity, sample/reflection or lifecycle evidence is invalid.'
        }
        foreach ($name in @('metadataAssemblies', 'assemblies')) {
            $expected = [string[]]$HybridCLR.$name
            $actual = $detail[$name]
            if ($expected.Count -eq 0 -or $actual -isnot [array] -or $actual.Count -ne $expected.Count -or
                @($actual | Where-Object { $_ -isnot [string] -or $_.Length -eq 0 }).Count -ne 0 -or
                @($actual | Sort-Object -Unique).Count -ne $actual.Count -or
                -not [Collections.Generic.HashSet[string]]::new($expected, [StringComparer]::Ordinal).SetEquals([string[]]$actual)) {
                throw "HybridCLR exact $name evidence is incomplete, duplicate or foreign."
            }
        }
    }
    return [pscustomobject]$report
}

function Get-YLTestRotation {
    param(
        [object] $Workspace,
        [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$')][string] $Key
    )
    Assert-YLTestWorkspace $Workspace
    $group = Get-YLTestPath (Join-Path (Join-Path $Workspace.root 'Results') $Key)
    if (-not [IO.Directory]::Exists($group)) { return }
    $null = New-YLTestContainer -Workspace $Workspace -Path $group -Kind ResultGroup -Key $Key -ExistingOnly
    $plan = [Collections.Generic.List[object]]::new()
    $eligible = [Collections.Generic.List[object]]::new()
    foreach ($path in [IO.Directory]::EnumerateDirectories($group)) {
        try {
            $owner = Get-YLTestDirectory -Workspace $Workspace -Path $path
            if ($owner.kind -cne 'Result' -or $owner.key -cne $Key) { throw 'Not a result of this rotation group.' }
            $summaryPath = Get-YLTestPath (Join-Path $path 'summary.json')
            if (-not [IO.File]::Exists($summaryPath)) { throw 'Incomplete result: no terminal summary.' }
            $summary = [IO.File]::ReadAllText($summaryPath) | ConvertFrom-Json -AsHashtable -DateKind String
            foreach ($field in @('ownerId', 'status', 'action', 'endUtc')) {
                if ($summary[$field] -isnot [string]) { throw 'Summary identity/status fields must be strings.' }
            }
            $end = [DateTimeOffset]::MinValue
            if ($summary['schemaVersion'] -isnot [long] -or $summary['schemaVersion'] -ne 1 -or $summary['ownerId'] -cne $owner.id -or
                -not [DateTimeOffset]::TryParseExact($summary['endUtc'], 'o', [Globalization.CultureInfo]::InvariantCulture,
                    [Globalization.DateTimeStyles]::None, [ref]$end)) { throw 'Summary identity or terminal timestamp is invalid.' }
            $ordinary = $summary['status'] -ceq 'Passed' -and $summary['action'] -ceq 'Run'
            $completed = $summary['status'] -ceq 'Completed' -and $summary['action'] -cin @('Discover', 'Compile', 'Check')
            if (-not $ordinary -and -not $completed -and $summary['status'] -cnotin @('Failed', 'PrerequisiteNotMet', 'Accepted')) {
                throw 'Unknown/inconsistent summary outcome is not automatically rotated.'
            }
            $null = Remove-YLTestDirectory -Workspace $Workspace -Path $path
            $eligible.Add([pscustomobject]@{ path = $path; end = $end; ordinary = ($ordinary -or $completed) })
        } catch { $plan.Add([pscustomobject]@{ path = $path; canRemove = $false; reason = 'Excluded: ' + $_.Exception.Message }) }
    }
    $successes = 0
    $failures = 0
    foreach ($item in @($eligible | Sort-Object -Property @{ Expression = { $_.end }; Descending = $true }, 'path')) {
        if ($item.ordinary) { $successes++; $obsolete = $successes -gt 1 }
        else { $failures++; $obsolete = $failures -gt 3 }
        $reason = if ($obsolete) { 'Beyond ordinary-success=1 / nonordinary=3 retention.' } else { 'Within retention window.' }
        $plan.Add([pscustomobject]@{ path = $item.path; canRemove = $obsolete; reason = $reason })
    }
    return $plan.ToArray()
}

function Invoke-YLTestRotation {
    param([object] $Workspace, [string] $Key)
    foreach ($candidate in @(Get-YLTestRotation -Workspace $Workspace -Key $Key)) {
        if ($candidate.canRemove) {
            Remove-YLTestDirectory -Workspace $Workspace -Path $candidate.path -Apply
        }
    }
}
