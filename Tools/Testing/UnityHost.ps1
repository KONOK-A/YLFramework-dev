#requires -Version 7.6
#requires -PSEdition Core
. (Join-Path $PSScriptRoot 'Common/Packages.ps1')

function Get-YLNativeTestAssembly {
    param([Collections.IDictionary] $Assembly, [string[]] $PackageIds, [Collections.IDictionary] $Catalog)
    if ($Assembly['name'] -isnot [string] -or $Assembly['name'] -cnotmatch '\.Tests(?:\.|$)' -or $Assembly['references'] -isnot [array]) {
        throw 'Only declared test assembly metadata may be projected; product definitions are immutable.'
    }
    $known = @($Catalog['packages'] | ForEach-Object { $_['id'] })
    if (@($PackageIds | Where-Object { $_ -cnotin $known }).Count) { throw 'Unknown package in the native test closure.' }
    $absent = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($package in $Catalog['packages'] | Where-Object { $_['id'] -cnotin $PackageIds }) {
        foreach ($declaration in $package['assemblies']) { $null = $absent.Add($declaration['name']) }
    }
    if ('com.kano.ylframework.localization' -cnotin $PackageIds) {
        foreach ($name in @('Unity.Localization', 'Unity.Localization.Editor')) {
            $null = $absent.Add($name)
        }
    }
    if ('com.kano.ylframework' -cnotin $PackageIds -and 'com.kano.ylframework.localization' -cnotin $PackageIds) {
        foreach ($name in @('Unity.Addressables', 'Unity.Addressables.Editor', 'Unity.ResourceManager')) { $null = $absent.Add($name) }
    }
    $copy = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $Assembly -Depth 25) -AsHashtable -NoEnumerate
    $copy['references'] = @($Assembly['references'] | Where-Object { -not $absent.Contains($_) })
    return ,$copy
}

function Get-YLUnityAddressablesVersion([object] $EditorProfile) {
    $null = Get-YLUnityPackageInputs $EditorProfile
    $expected = if ($EditorProfile.version.StartsWith('2022.', [StringComparison]::Ordinal)) { '1.25.0' } else { '2.10.3' }
    if ($null -eq $EditorProfile.PSObject.Properties['addressables'] -or $EditorProfile.addressables -cne $expected) {
        throw 'The exact Editor profile must declare its verified Addressables version.'
    }
    return $expected
}

function Assert-YLUnityAddressablesVersion {
    param([string] $ExpectedVersion, [Collections.IDictionary] $Lock, [object] $Response)
    if (-not $Lock.Contains('dependencies') -or -not $Lock.dependencies.Contains('com.unity.addressables')) {
        throw 'Addressables is absent from the actual host package resolution.'
    }
    $entry = $Lock.dependencies['com.unity.addressables']
    if ($entry.version -cne $ExpectedVersion -or $entry.source -cne 'registry' -or
        $null -eq $Response.PSObject.Properties['addressablesVersion'] -or
        $null -eq $Response.PSObject.Properties['addressablesSource'] -or
        $Response.addressablesVersion -cne $ExpectedVersion -or $Response.addressablesSource -cne 'Registry') {
        throw 'Actual Addressables package version/source differs from the declared host profile.'
    }
    return [ordered]@{ requested = $ExpectedVersion; resolved = $entry.version; source = $entry.source; loaded = $Response.addressablesVersion }
}

function Get-YLUnityPackageInputs([object] $EditorProfile) {
    if ($null -eq $EditorProfile -or $null -eq $EditorProfile.PSObject.Properties['productVersion'] -or
        $null -eq $EditorProfile.PSObject.Properties['version'] -or $EditorProfile.productVersion -isnot [string] -or $EditorProfile.version -isnot [string]) {
        throw 'An exact declared Editor profile is required to select source Packages.'
    }
    $root = switch -CaseSensitive ($EditorProfile.productVersion) {
        '2022.3.62f3c1_1623fc0bbb97' { 'Tools/Testing/Profiles/Unity2022' }
        '2022.3.62f3_96770f904ca7' { 'Tools/Testing/Profiles/Unity2022' }
        '6000.3.27f1_672d562a42a7' { 'Packages' }
        default { throw "No source Packages layout is declared for Editor profile: $($EditorProfile.productVersion)" }
    }
    if ($EditorProfile.version -cne $EditorProfile.productVersion.Split('_')[0]) { throw 'The declared Editor profile version and ProductVersion do not match.' }
    return @("$root/manifest.json", "$root/packages-lock.json")
}

function Get-YLHostManagedPath([object] $HostRecord, [string] $Relative) {
    if ([IO.Path]::IsPathRooted($Relative)) { throw 'Host input names must be relative.' }
    $path = Get-YLTestPath (Join-Path $HostRecord.path $Relative)
    if (-not $path.StartsWith($HostRecord.path + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Host input is outside the owned host.' }
    return $path
}

function Get-YLPackedCatalogProfile([string] $Module, [object] $EditorProfile) {
    if ($Module -cnotin @('ResourcePackedCatalog', 'ResourcePackedCatalogBinary')) { return }
    $addressables = Get-YLUnityAddressablesVersion $EditorProfile
    $legacyBinary = $Module -ceq 'ResourcePackedCatalogBinary'
    if ($legacyBinary -and $addressables -cne '1.25.0') { throw 'ResourcePackedCatalogBinary requires a pinned Unity 2022 Editor and Registry Addressables 1.25.0.' }
    $define = if ($legacyBinary) { 'ENABLE_BINARY_CATALOG' } else { '' }
    $symbolIdentity = if ($legacyBinary) { $define } else { 'None' }
    $encoding = if ($addressables -ceq '1.25.0' -and -not $legacyBinary) { 'json' } else { 'binary' }
    return [pscustomobject]@{ define = $define; expectedEncoding = $encoding; keySuffix = "-$Module-Win64-Defines$symbolIdentity-$encoding" }
}

function Initialize-YLPackedBinarySettings([object] $HostRecord) {
    $settings = Get-YLHostManagedPath $HostRecord 'ProjectSettings/ProjectSettings.asset'
    if (-not [IO.File]::Exists($settings)) {
        if ([IO.Path]::Exists((Get-YLHostManagedPath $HostRecord 'Library'))) { throw 'Refusing to reseed a compiled host without its original ENABLE_BINARY_CATALOG settings.' }
        # Player settings apply to Editor and runtime assemblies. Unity 2022's csc.rsp does not cover Editor scripts.
        $seed = [Text.Encoding]::UTF8.GetBytes("%YAML 1.1`n%TAG !u! tag:unity3d.com,2011:`n--- !u!129 &1`nPlayerSettings:`n  m_ObjectHideFlags: 0`n  serializedVersion: 26`n  scriptingDefineSymbols:`n    Standalone: ENABLE_BINARY_CATALOG`n")
        $stream = [IO.File]::Open($settings, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        try { $stream.Write($seed, 0, $seed.Length) } finally { $stream.Dispose() }
    }
    $text = [IO.File]::ReadAllText($settings)
    if ($text -cnotmatch '(?m)^  scriptingDefineSymbols:\r?\n    Standalone: ENABLE_BINARY_CATALOG\r?$') {
        throw 'Owned binary host lost its exact Standalone ENABLE_BINARY_CATALOG settings; preserving the existing file.'
    }
    return $settings
}

function Read-YLPackedCatalogEncoding([string] $Log, [object] $Packed, [string] $AddressablesVersion) {
    $legacyBinary = $Packed.define -ceq 'ENABLE_BINARY_CATALOG'
    $extension = if ($Packed.expectedEncoding -ceq 'binary') { '.bin' } else { '.json' }
    $catalogs = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($line in [IO.File]::ReadAllLines($Log)) {
        if (-not $line.StartsWith('[PackedContract] Addressables ', [StringComparison]::Ordinal)) { continue }
        if ($line -cnotmatch '^\[PackedContract\] Addressables (?<version>\d+\.\d+\.\d+), ENABLE_BINARY_CATALOG=(?<binary>True|False), ENABLE_JSON_CATALOG=False, runtime catalog=(?<catalog>.+)$' -or
            $Matches.version -cne $AddressablesVersion -or $Matches.binary -cne $legacyBinary.ToString() -or
            [IO.Path]::GetExtension($Matches.catalog) -cne $extension) {
            throw 'Packed catalog encoding or compiled test symbols contradict the pinned profile.'
        }
        $null = $catalogs.Add($Matches.catalog)
    }
    if ($catalogs.Count -eq 0) { throw 'Missing Packed catalog encoding in the Unity test log.' }
    return [ordered]@{ encoding = $Packed.expectedEncoding; catalogs = @($catalogs); log = $Log
        logSha256 = (Get-FileHash -LiteralPath $Log -Algorithm SHA256).Hash.ToLowerInvariant() }
}

function Assert-YLUnityResponseIdentity {
    param([object] $Response, [object] $Request, [string] $UnityVersion)
    $expectedStatus = switch -CaseSensitive ($Request.action) {
        'Discover' { 'Completed' }
        'Run' { 'Passed' }
        'PrepareSamples' { 'Prepared' }
        default { throw 'Unknown native request action.' }
    }
    foreach ($field in @('hostId', 'inputHash', 'status', 'unityVersion')) {
        if ($Response.$field -isnot [string]) { throw 'Host response identity fields must be strings.' }
    }
    if ($Response.schemaVersion -isnot [long] -or $Response.schemaVersion -ne 1 -or
        $Response.hostId -cne $Request.hostId -or $Response.inputHash -cne $Request.inputHash -or
        $Response.status -cne $expectedStatus -or $Response.unityVersion -cne $UnityVersion) {
        throw 'Host response identity mismatch.'
    }
}

function Assert-YLUnitySelectionModes {
    param([object] $Selection, [object] $Definition, [string] $Suite)
    $expected = if ($Suite -ceq 'Special') {
        if ('specialModes' -cin $Definition.PSObject.Properties.Name) { @($Definition.specialModes) } else { @('EditMode') }
    } else {
        if ('modes' -cin $Definition.PSObject.Properties.Name) { @($Definition.modes) } else { @('EditMode', 'PlayMode') }
    }
    $expected = @($expected)
    $actual = @($Selection.tests.mode | Sort-Object -Unique)
    if ($actual.Count -ne $expected.Count -or
        -not [Collections.Generic.HashSet[string]]::new([string[]]$expected, [StringComparer]::Ordinal).SetEquals([string[]]$actual)) {
        throw 'Actual discovered execution modes differ from the declared profile; an entire mode may be missing.'
    }
}

function Assert-YLUnityHostIdle([object] $HostRecord) {
    foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'")) {
        if ($process.CommandLine -and $process.CommandLine -match '(?i)(?:^|\s)-projectPath\s+(?:"(?<quoted>[^"]+)"|(?<plain>\S+))') {
            $project = if ($Matches['quoted']) { $Matches['quoted'] } else { $Matches['plain'] }
            if ([IO.Path]::GetFullPath($project).TrimEnd('\', '/') -ieq $HostRecord.path) { throw 'The owned host is already open in an Editor; it will not be stopped or modified.' }
        }
    }
    $lockPath = Get-YLHostManagedPath $HostRecord 'Temp/UnityLockfile'
    if ([IO.File]::Exists($lockPath)) {
        $probe = [IO.File]::Open($lockPath, 'Open', 'ReadWrite', 'None')
        $probe.Dispose()
    }
}

function Start-YLPlayerRetention {
    param($HostRecord, $Run, [switch] $FormalCandidate, [switch] $KeepArtifacts)
    $context = [ordered]@{ schemaVersion = 1; host = $HostRecord.path; hostId = $HostRecord.id; resultId = $Run.id
        formalCandidate = [bool]$FormalCandidate; keepArtifacts = [bool]$KeepArtifacts }
    Write-YLTestJson -Path (Join-Path $Run.path 'host-context.json') -Value $context
    if ($FormalCandidate -or $KeepArtifacts) {
        foreach ($dir in $HostRecord.path,$Run.path) { Write-YLTestJson -Path (Join-Path $dir '.pinned') -Value $context }
    }
}

function Complete-YLPlayerRun {
    param($Workspace, $HostRecord, $Run, [IO.FileStream] $HostLease, [Collections.IDictionary] $Summary, [switch] $KeepArtifacts)
    $owned = Get-YLTestDirectory $Workspace $HostRecord.path
    $result = Get-YLTestDirectory $Workspace $Run.path
    $file, $prefix = switch -CaseSensitive ($result.key) {
        'ResourceAAIL2CPP' { 'execution.json', 'RAP' }
        'FrozenEncryption' { 'frozen.json', 'RFD' }
        { $_ -cin 'NativeDeliveryP0','NativeDeliveryFinal' } { 'p0-c.json', 'RNC' }
        default { throw 'Not a one-shot Player result group.' }
    }
    if ($owned.kind -cne 'Host' -or $result.kind -cne 'Result' -or $owned.key -cnotmatch "^${prefix}[0-9a-f]{8}$" -or
        $owned.id -cne $HostRecord.id -or $result.id -cne $Run.id -or $Summary['host'] -ine $owned.path -or
        $Summary['hostId'] -cne $owned.id -or $Summary['resultId'] -cne $result.id) { throw 'Player host/result identity mismatch.' }

    # Preserve the run result before cleanup, but publish a terminal pass only after retention succeeds.
    Write-YLTestJson -Path (Join-Path $result.path 'run-outcome.json') -Value $Summary
    if ($null -eq $HostLease -or -not $HostLease.CanWrite -or $HostLease.Name -ine (Join-Path $owned.path '.lease')) {
        throw 'Player finalization requires the original host lease.'
    }
    $reason = if ($KeepArtifacts) { 'KeepArtifacts' } elseif ($Summary['formalCandidate']) { 'FormalCandidate' }
        elseif ($Summary['status'] -isnot [string] -or $Summary['status'] -cne 'Passed') { 'NotPassed' }
        elseif ([IO.Path]::Exists((Join-Path $owned.path '.pinned')) -or [IO.Path]::Exists((Join-Path $result.path '.pinned'))) { 'ExistingPin' }
        else { 'PassedDiagnostic' }
    $retention = [ordered]@{ schemaVersion = 1; host = $owned.path; hostId = $owned.id; resultId = $result.id; reason = $reason; hostStatus = 'Pinned' }
    try {
        if ($reason -cne 'PassedDiagnostic') {
            foreach ($path in $owned.path, $result.path) {
                $pin = Get-YLTestPath (Join-Path $path '.pinned')
                if (-not [IO.File]::Exists($pin)) { Write-YLTestJson -Path $pin -Value $retention }
            }
        } else {
            Assert-YLUnityHostIdle $owned
            $retention['evidenceSha256'] = [ordered]@{}
            foreach ($relative in '.yl-inputs.json','Packages/manifest.json','Packages/packages-lock.json','fixture.json','Private/audit.json') {
                $source = Get-YLHostManagedPath $owned $relative
                if (-not [IO.File]::Exists($source)) { continue }
                $copy = Get-YLTestPath (Join-Path $result.path "host-evidence/$relative")
                $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($copy))
                [IO.File]::Copy($source, $copy, $false)
                $hash = (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash
                if ($hash -cne (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash) { throw "Host evidence copy changed: $relative" }
                $retention.evidenceSha256[$relative] = $hash
            }
            $retention.hostStatus = 'RemovalPending'
            Write-YLTestJson -Path (Join-Path $result.path 'retirement-intent.json') -Value $retention
            $removed = Remove-YLTestDirectory -Workspace $Workspace -Path $owned.path -Apply -ExpectedId $owned.id -HeldLease $HostLease
            $retention.hostStatus = $removed.status
        }
    } catch {
        $retention.hostStatus = 'CleanupFailed'
        $retention['error'] = $_.Exception.Message
        $Summary['runStatus'] = $Summary['status']
        $Summary['status'] = 'Failed'
        $Summary['retentionError'] = $_.Exception.Message
        throw
    } finally {
        Write-YLTestJson -Path (Join-Path $result.path 'retention.json') -Value $retention
        Write-YLTestJson -Path (Join-Path $result.path $file) -Value $Summary
    }
}

function Sync-YLUnityHost([object] $HostRecord, [Collections.IDictionary] $Files, [object] $Identity, [string] $EditorVersion) {
    $statePath = Get-YLHostManagedPath $HostRecord '.yl-inputs.json'
    $previous = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    if ([IO.File]::Exists($statePath)) {
        $state = [IO.File]::ReadAllText($statePath) | ConvertFrom-Json -DateKind String
        if ($state.schemaVersion -isnot [long] -or $state.schemaVersion -ne 1 -or $state.purpose -isnot [string] -or
            $state.purpose -cne 'YLTesting.HostInputs' -or $state.hostId -isnot [string] -or $state.hostId -cne $HostRecord.id) { throw 'Host input receipt identity is invalid.' }
        foreach ($item in $state.files) {
            if ($item.path -isnot [string] -or $item.sha256 -isnot [string]) { throw 'Host input receipt paths and hashes must be strings.' }
            $previous.Add($item.path, $item.sha256)
        }
    }
    $importedSamples = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $sampleReceiptPath = Get-YLHostManagedPath $HostRecord '.yl-samples.json'
    if ([IO.File]::Exists($sampleReceiptPath)) {
        $samples = [IO.File]::ReadAllText($sampleReceiptPath) | ConvertFrom-Json -AsHashtable -DateKind String
        if ($samples['schemaVersion'] -isnot [long] -or $samples['schemaVersion'] -ne 1 -or
            $samples['purpose'] -isnot [string] -or $samples['purpose'] -cne 'YLTesting.Samples' -or
            $samples['hostId'] -isnot [string] -or $samples['hostId'] -cne $HostRecord.id -or
            $samples['inputHash'] -isnot [string] -or $samples['inputHash'] -cne $Identity.sha256 -or
            $samples['files'] -isnot [Collections.IList] -or $samples['files'].Count -eq 0) { throw 'Imported sample ownership does not match this exact host input.' }
        foreach ($sample in $samples['files']) {
            if ($sample -isnot [Collections.IDictionary] -or $sample['path'] -isnot [string] -or
                -not $sample['path'].StartsWith('Assets/Samples/', [StringComparison]::Ordinal) -or
                $sample['path'] -match '(?:^|/)\.\.?(?:/|$)' -or
                $sample['sha256'] -isnot [string] -or $sample['sha256'] -cnotmatch '^[0-9a-fA-F]{64}$') { throw 'Invalid imported sample file identity.' }
            $samplePath = Get-YLHostManagedPath $HostRecord $sample['path']
            if (-not [IO.File]::Exists($samplePath) -or
                -not [string]::Equals((Get-FileHash -LiteralPath $samplePath -Algorithm SHA256).Hash, $sample['sha256'], [StringComparison]::OrdinalIgnoreCase)) { throw "Imported sample was changed or removed; preserve it instead of overwriting: $($sample['path'])" }
            $importedSamples.Add($sample['path'], $sample['sha256'])
        }
    }
    $assetRoot = Get-YLHostManagedPath $HostRecord 'Assets'
    if ([IO.Directory]::Exists($assetRoot)) {
        foreach ($path in @(Get-YLTestTreeFiles $assetRoot)) {
            $relative = [IO.Path]::GetRelativePath($HostRecord.path, $path).Replace('\', '/')
            if ($relative.StartsWith('Assets/Samples/', [StringComparison]::Ordinal) -and
                -not $previous.ContainsKey($relative) -and -not $Files.Contains($relative) -and -not $importedSamples.ContainsKey($relative)) {
                throw "Unowned sample content must not be adopted or overwritten: $relative"
            }
            if ([IO.Path]::GetExtension($path) -cin @('.cs', '.asmdef', '.asmref', '.dll', '.rsp') -and
                -not $previous.ContainsKey($relative) -and -not $Files.Contains($relative) -and -not $importedSamples.ContainsKey($relative)) {
                throw "Unmanaged source input is present in the reusable host: $relative"
            }
        }
    }
    $manifest = [Collections.Generic.List[object]]::new()
    foreach ($entry in $Files.GetEnumerator()) {
        $path = Get-YLHostManagedPath $HostRecord $entry.Key
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$entry.Value))
        $existing = if ([IO.File]::Exists($path)) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash } else { $null }
        if ($existing -and $existing -cne $hash -and (-not $previous.ContainsKey($entry.Key) -or $existing -cne $previous[$entry.Key])) {
            throw "Host input was changed outside the producer; preserving it: $($entry.Key)"
        }
        if ($existing -cne $hash) {
            $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
            $temporary = $path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
            [IO.File]::WriteAllBytes($temporary, [byte[]]$entry.Value)
            [IO.File]::Move($temporary, $path, $true)
        }
        $manifest.Add([ordered]@{ path = $entry.Key; sha256 = $hash })
    }
    foreach ($relative in $previous.Keys) {
        if ($Files.Contains($relative)) { continue }
        $path = Get-YLHostManagedPath $HostRecord $relative
        if (-not [IO.File]::Exists($path)) { continue }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $previous[$relative]) { throw "Removed host input was changed externally; preserving it: $relative" }
        if ($path.EndsWith('.meta', [StringComparison]::OrdinalIgnoreCase) -and [IO.Directory]::Exists($path.Substring(0, $path.Length - 5))) { continue }
        [IO.File]::Delete($path)
    }
    $versionPath = Get-YLHostManagedPath $HostRecord 'ProjectSettings/ProjectVersion.txt'
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($versionPath))
    if ([IO.File]::Exists($versionPath)) {
        if (-not [IO.File]::ReadAllLines($versionPath).Contains("m_EditorVersion: $EditorVersion")) { throw 'The reusable host has a different Unity project version.' }
    } else { [IO.File]::WriteAllText($versionPath, "m_EditorVersion: $EditorVersion`n", [Text.UTF8Encoding]::new($false)) }
    $state = [ordered]@{ schemaVersion = 1; purpose = 'YLTesting.HostInputs'; hostId = $HostRecord.id; inputHash = $Identity.sha256; files = $manifest.ToArray() }
    $temporary = $statePath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    Write-YLTestJson -Path $temporary -Value $state
    [IO.File]::Move($temporary, $statePath, $true)
}

function Invoke-YLUnitySuite {
    param(
        [string] $Repository, [object] $Definition, [object] $EditorProfile,
        [string] $Unity, [string] $WorkspaceRoot, [string] $Action, [string] $Suite, [string] $Module,
        [int] $TimeoutSeconds, [string] $Selection, [string] $DOTweenArchive, [bool] $KeepArtifacts, [Collections.IDictionary] $Summary
    )
    $packed = Get-YLPackedCatalogProfile -Module $Module -EditorProfile $EditorProfile
    $sources = Get-SelectedSources -Native -EditorProfile $EditorProfile
    $sessionScene = 'prepareSessionScene' -cin $Definition.PSObject.Properties.Name -and $Definition.prepareSessionScene
    $desktop = $Suite -ceq 'Special' -and 'interactiveDesktop' -cin $Definition.PSObject.Properties.Name -and $Definition.interactiveDesktop
    $nativeSdk = if ('nativeSdk' -cin $Definition.PSObject.Properties.Name) { $Definition.nativeSdk } else { '' }
    if ($nativeSdk -isnot [string] -or $nativeSdk -cnotin @('', 'DOTween', 'HybridCLR')) { throw 'Unsupported native SDK; only explicit DOTween and HybridCLR profiles are accepted.' }
    $dotween = $nativeSdk -ceq 'DOTween'
    $hybridClr = $nativeSdk -ceq 'HybridCLR'
    $hybridSdk = $null
    $nativeTools = @($Unity)
    if ($dotween) {
        if ($Definition.nativeSdk -isnot [string] -or $Definition.nativeSdk -cne 'DOTween' -or $Module -cne 'DOTween' -or
            $Definition.nativeLayout -cne 'Framework' -or [string]::IsNullOrWhiteSpace($DOTweenArchive)) { throw 'PrerequisiteNotMet: DOTween native behavior requires its explicit approved Standard archive in the Framework module.' }
        $DOTweenArchive = Get-YLTestPath -Path $DOTweenArchive -ReadOnly
        $nativeTools += $DOTweenArchive
    } elseif ($DOTweenArchive) { throw 'An ordinary native module cannot deploy a DOTween SDK.' }
    if ($hybridClr) {
        if ($Module -cnotin @('HybridCLR', 'ResourceAddressablesHybridCLR') -or $Suite -cne 'Daily' -or $Definition.nativeLayout -cne 'Framework' -or
            ('com.kano.ylframework.hybridclr' -cnotin @($Definition.packageInputs))) { throw 'PrerequisiteNotMet: HybridCLR SDK requires its explicit Daily Framework integration module and package.' }
        $hybridSdk = Get-YLIntegrationHybridSdk -Repository $Repository
    }
    $profile = "Unity-$($EditorProfile.version)-UTF$($EditorProfile.testFramework)-UGUI$($EditorProfile.ugui)-$Suite-$($Definition.nativeLayout)-Graphics$($Definition.graphics)-IsolatedModes"
    $addressablesVersion = if ($Definition.nativeLayout -ceq 'Framework') { Get-YLUnityAddressablesVersion $EditorProfile } else { $null }
    if ($addressablesVersion) { $profile += '-AA' + $addressablesVersion }
    if ($packed) { $profile += $packed.keySuffix }
    if ($sessionScene) { $profile += '-SessionScenes' }
    if ($desktop) { $profile += '-Desktop' }
    if ($dotween) { $profile += '-ApprovedDOTween-InitializeOnce' }
    if ($hybridClr) { $profile += '-ApprovedHybridCLR-Git' }
    $identity = Get-YLTestInputIdentity -Repository $Repository -Paths $sources.inputs -Toolchain $nativeTools -Profile $profile
    $workspace = Initialize-YLTestWorkspace -WorkspaceRoot $WorkspaceRoot -Repository $Repository
    $repositoryKey = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Repository.ToLowerInvariant()))).Substring(0, 12)
    $hostKey = "Unity-$($EditorProfile.version)-$Suite-$Module-$repositoryKey"
    if ($addressablesVersion) { $hostKey += '-AA' + $addressablesVersion }
    if ($packed) { $hostKey += $packed.keySuffix }
    if ($dotween) { $hostKey += '-SdkInitializeOnce' }
    if ($hybridClr) { $hostKey += '-HybridClrGitSdk' }
    $hasSamples = 'sampleInputs' -cin $Definition.PSObject.Properties.Name -and @($Definition.sampleInputs).Count -gt 0
    if ($hasSamples) { $hostKey += '-Samples-' + $identity.sha256 }
    if ($sessionScene) { $hostKey += '-SessionScenes' }
    if ($desktop) { $hostKey += '-Desktop' }
    $fullHostKey = $hostKey
    $shortHost = $hasSamples -or ('shortHostKey' -cin $Definition.PSObject.Properties.Name -and $Definition.shortHostKey)
    if ($shortHost) {
        $keyHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($hostKey)))
        $hostKey = 'R' + $keyHash.Substring(0, 6)
    }
    $hostRecord = New-YLTestDirectory -Workspace $workspace -Kind Host -Key $hostKey
    $hostLease = Lock-YLTestDirectory -Workspace $workspace -Path $hostRecord.path
    $run = $null
    $runLease = $null
    $Summary['profile'] = $profile
    $Summary['host'] = [ordered]@{ path = $hostRecord.path; id = $hostRecord.id }
    $Summary['unity'] = [ordered]@{ executable = $Unity; requestedVersion = $EditorProfile.version; actualVersion = $null; execution = 'Unity Editor EditMode/PlayMode' }
    try {
        if ($shortHost) {
            $keyReceipt = Join-Path $hostRecord.path '.yl-host-key.json'
            if ([IO.File]::Exists($keyReceipt)) {
                $receipt = [IO.File]::ReadAllText($keyReceipt) | ConvertFrom-Json -DateKind String
                if ($receipt.hostId -isnot [string] -or $receipt.fullKey -isnot [string] -or
                    $receipt.hostId -cne $hostRecord.id -or $receipt.fullKey -cne $fullHostKey) { throw 'The short host key collided with a different exact identity.' }
            } else {
                if ([IO.Directory]::Exists((Join-Path $hostRecord.path 'Assets'))) { throw 'Do not adopt a short host without its original key receipt.' }
                Write-YLTestJson -Path $keyReceipt -Value @{ hostId = $hostRecord.id; fullKey = $fullHostKey }
            }
        }
        Assert-YLUnityHostIdle $hostRecord
        $run = New-YLTestDirectory -Workspace $workspace -Kind Result -Key "$Action-$Suite-$Module-$($EditorProfile.version)"
        $runLease = Lock-YLTestDirectory -Workspace $workspace -Path $run.path
        $Summary['ownerId'] = $run.id
        $Summary['summary'] = Join-Path $run.path 'summary.json'
        $Summary['status'] = 'Failed'
        if ($KeepArtifacts) { [IO.File]::WriteAllText((Join-Path $run.path '.pinned'), 'Explicit KeepArtifacts retention.') }
        $sdk = $null
        if ($dotween) {
            $sdk = Get-YLIntegrationDotweenSdk -Repository $Repository -Archive $DOTweenArchive -OwnedSdkRoot (Join-Path $run.path 'ApprovedSdk')
            $Summary['sdk'] = @{ mode = 'Required'; version = $sdk.version; archiveSha256 = $sdk.archiveSha256; assemblySha256 = $sdk.assemblySha256; sdkDeployed = $true; defineEnabled = $true }
        }
        if ($hybridClr) {
            $Summary['sdk'] = @{ source = 'Git'; version = $hybridSdk.version; declaration = $hybridSdk.declaration; revision = $hybridSdk.revision; nativeInstallation = 'NotRun' }
        }
        $ids = if ('packageInputs' -cin $Definition.PSObject.Properties.Name) { @($Definition.packageInputs) } else { @('com.kano.ylframework') }
        $productPackages = @(Get-YLUpmPackages -Repository $Repository -Ids $ids)
        $catalog = Read-YLPackageCatalog
        $files = [ordered]@{}
        foreach ($relative in $sources.inputs) {
            if ((-not $relative.StartsWith('Assets/', [StringComparison]::Ordinal) -and
                -not $relative.StartsWith('PackageSources/', [StringComparison]::Ordinal)) -or
                ($Definition.nativeLayout -ceq 'Slice' -and $relative -cin $Definition.assemblyMetadata)) { continue }
            $target = $relative
            if ($Definition.nativeLayout -ceq 'Slice' -and $relative.StartsWith('PackageSources/', [StringComparison]::Ordinal)) {
                $prefix = 'PackageSources/com.kano.ylframework/'
                if (-not $relative.StartsWith($prefix, [StringComparison]::Ordinal)) { throw 'A source-slice host only supports explicitly selected main-package inputs.' }
                $target = 'Assets/YLFramework/' + $relative.Substring($prefix.Length)
            }
            $files[$target] = [IO.File]::ReadAllBytes((Join-Path $Repository $relative))
            if ($Definition.nativeLayout -ceq 'Framework' -and $relative.StartsWith('Assets/Tests/', [StringComparison]::Ordinal) -and $relative.EndsWith('.asmdef', [StringComparison]::Ordinal)) {
                $assembly = [Text.Encoding]::UTF8.GetString($files[$target]) | ConvertFrom-Json -AsHashtable -DateKind String
                if ($assembly['name'] -is [string] -and $assembly['name'] -cmatch '\.Tests(?:\.|$)') {
                    $projected = Get-YLNativeTestAssembly -Assembly $assembly -PackageIds @($productPackages.id) -Catalog $catalog
                    $files[$target] = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $projected -Depth 25))
                }
            }
        }
        foreach ($relative in @($Definition.assemblyMetadata | Where-Object { $Definition.nativeLayout -ceq 'Slice' })) {
            $assembly = [IO.File]::ReadAllText((Join-Path $Repository $relative)) | ConvertFrom-Json -AsHashtable -DateKind String
            $assembly['overrideReferences'] = $true
            $assembly['autoReferenced'] = $false
            if ($relative.Contains('/Runtime/')) {
                $assembly['references'] = @('UnityEngine.UI')
                $assembly['precompiledReferences'] = @()
            } else {
                $assembly['references'] = @('YLFramework.Runtime', 'UnityEngine.UI', 'UnityEngine.TestRunner')
                if ($relative.Contains('/EditMode/')) { $assembly['references'] += 'UnityEditor.TestRunner' }
                $assembly['precompiledReferences'] = @('nunit.framework.dll')
                $null = $assembly.Remove('optionalUnityReferences')
                $assembly['defineConstraints'] = @('UNITY_INCLUDE_TESTS')
            }
            $target = $relative
            if ($relative.StartsWith('PackageSources/com.kano.ylframework/', [StringComparison]::Ordinal)) { $target = 'Assets/YLFramework/' + $relative.Substring('PackageSources/com.kano.ylframework/'.Length) }
            $files[$target] = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $assembly -Depth 15))
        }
        $dependencies = [ordered]@{
            'com.unity.test-framework' = $EditorProfile.testFramework
            'com.unity.ugui' = $EditorProfile.ugui
            'com.unity.modules.jsonserialize' = '1.0.0'
        }
        if ($Definition.nativeLayout -ceq 'Framework') {
            $dependencies['com.unity.addressables'] = $addressablesVersion
            $packageInputs = @(Get-YLUnityPackageInputs -EditorProfile $EditorProfile)
            $projectManifest = [IO.File]::ReadAllText((Join-Path $Repository $packageInputs[0])) | ConvertFrom-Json -DateKind String
            foreach ($dependency in $projectManifest.dependencies.PSObject.Properties | Where-Object { $_.Name.StartsWith('com.unity.modules.', [StringComparison]::Ordinal) }) {
                $dependencies[$dependency.Name] = $dependency.Value
            }
            $projectLock = [IO.File]::ReadAllText((Join-Path $Repository $packageInputs[1])) | ConvertFrom-Json -DateKind String
            $dependencies['com.unity.nuget.newtonsoft-json'] = $projectLock.dependencies.'com.unity.nuget.newtonsoft-json'.version
            foreach ($id in @(if ('nativeUnityPackages' -cin $Definition.PSObject.Properties.Name) { $Definition.nativeUnityPackages })) {
                if ($id -isnot [string] -or $id -cne 'com.unity.inputsystem') { throw 'Unsupported native Unity package; only the explicit Input System consumer is approved.' }
                $declared = $projectManifest.dependencies.$id
                $locked = $projectLock.dependencies.$id
                if ($declared -isnot [string] -or $declared -cnotmatch '^\d+\.\d+\.\d+$' -or
                    $locked.source -isnot [string] -or $locked.source -cne 'registry' -or
                    $locked.version -isnot [string] -or $locked.version -cne $declared) { throw "PrerequisiteNotMet: native Unity package declaration and target lock must match: $id" }
                $dependencies[$id] = $declared
            }
        }
        $manifest = @{ dependencies = $dependencies }
        foreach ($package in $productPackages) {
            $manifest.dependencies[$package.id] = 'file:../' + $package.relative
        }
        if ($hybridClr) { $manifest.dependencies['com.code-philosophy.hybridclr'] = $hybridSdk.declaration }
        $files['Packages/manifest.json'] = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $manifest -Depth 8))
        if ($dotween) {
            foreach ($relative in $sdk.files.Keys) { $files['Assets/Plugins/Demigiant/DOTween/' + $relative] = [IO.File]::ReadAllBytes((Join-Path $sdk.root $relative)) }
        }
        Sync-YLUnityHost -HostRecord $hostRecord -Files $files -Identity $identity -EditorVersion $EditorProfile.version
        if ($packed) {
            $Summary.host['fullKey'] = $fullHostKey
            $Summary['packedCatalog'] = [ordered]@{
                category = $Definition.category; buildTarget = 'Win64'; expectedEncoding = $packed.expectedEncoding
                scriptingDefines = @(if ($packed.define) { $packed.define }); settings = $null; evidence = $null
            }
            if ($packed.define) { $Summary.packedCatalog.settings = Initialize-YLPackedBinarySettings -HostRecord $hostRecord }
        }
        if ($dotween) {
            $settings = Get-YLHostManagedPath $hostRecord 'ProjectSettings/ProjectSettings.asset'
            $created = -not [IO.File]::Exists($settings)
            if ($created) {
                # Initial settings become Unity-managed output, not a fixed source file to overwrite after every run.
                $stream = [IO.File]::Open($settings, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
                try {
                    $seed = [Text.Encoding]::UTF8.GetBytes("%YAML 1.1`n%TAG !u! tag:unity3d.com,2011:`n--- !u!129 &1`nPlayerSettings:`n  m_ObjectHideFlags: 0`n  serializedVersion: 26`n  scriptingDefineSymbols:`n    Standalone: DOTWEEN`n")
                    $stream.Write($seed, 0, $seed.Length)
                } finally { $stream.Dispose() }
            }
            $Summary['sdkInitialization'] = @{ settings = $settings; created = $created; lifecycle = 'InitializeOnceThenUnityManaged' }
        }

        function Invoke-NativeOperation([string] $Operation, [object[]] $Tests = @(), [string] $Mode = '') {
            $output = Join-Path $run.path $(if ($Mode) { "$Operation-$Mode" } else { $Operation })
            $null = [IO.Directory]::CreateDirectory($output)
            $request = [ordered]@{
                schemaVersion = 1; hostId = $hostRecord.id; workspaceId = $workspace.id; resultId = $run.id
                expectedUnityVersion = $EditorProfile.version; inputHash = $identity.sha256
                action = $Operation; category = $Definition.category; assemblies = @($Definition.assemblies)
                samples = @(if ('sampleInputs' -cin $Definition.PSObject.Properties.Name) { $Definition.sampleInputs })
                excludedCategories = @($Definition.excludedCategories); sortingLayer = $Definition.sortingLayer; output = $output; tests = $Tests
                disableDomainReload = ($Suite -ceq 'Special' -and 'disableDomainReload' -cin $Definition.PSObject.Properties.Name -and $Definition.disableDomainReload)
                prepareSceneTemplates = ('prepareSceneTemplates' -cin $Definition.PSObject.Properties.Name -and $Definition.prepareSceneTemplates)
                prepareSessionScene = ($sessionScene -and $Mode -cne 'PlayMode')
                prepareAddressablesContent = ('prepareAddressablesContent' -cin $Definition.PSObject.Properties.Name -and $Definition.prepareAddressablesContent)
                prepareAddressablesConsumerContent = ('prepareAddressablesConsumerContent' -cin $Definition.PSObject.Properties.Name -and $Definition.prepareAddressablesConsumerContent)
                prepareCoreContent = ('prepareCoreContent' -cin $Definition.PSObject.Properties.Name -and $Definition.prepareCoreContent)
                prepareLocalizationContent = ('prepareLocalizationContent' -cin $Definition.PSObject.Properties.Name -and $Definition.prepareLocalizationContent)
                interactiveDesktop = $desktop
            }
            $requestPath = Join-Path $output 'request.json'
            Write-YLTestJson -Path $requestPath -Value $request
            $unityLog = Join-Path $output 'unity.log'
            try {
                $arguments = @(if ($desktop) { '-ylTestInteractive' } else { '-batchmode' })
                if (-not $Definition.graphics) { $arguments += '-nographics' }
                if ($packed) {
                    if ($packed.define) { $null = Initialize-YLPackedBinarySettings -HostRecord $hostRecord }
                    $arguments += @('-buildTarget', 'Win64')
                }
                $arguments += @('-projectPath', $hostRecord.path, '-executeMethod', 'YLFramework.Testing.Editor.YLTestEntry.Execute',
                    '-ylTestRequest', $requestPath, '-logFile', $unityLog)
                $fact = Invoke-TestingProcess -Exe $Unity -Arguments $arguments
                $response = [IO.File]::ReadAllText((Join-Path $output 'response.json')) | ConvertFrom-Json -DateKind String
                Assert-YLUnityResponseIdentity -Response $response -Request $request -UnityVersion $EditorProfile.version
                if ($hybridClr) { $null = Get-YLIntegrationHybridSdk -Repository $hostRecord.path }
                $Summary.unity.actualVersion = $response.unityVersion
                if ($addressablesVersion) {
                    $actualLock = [IO.File]::ReadAllText((Join-Path $hostRecord.path 'Packages/packages-lock.json')) | ConvertFrom-Json -AsHashtable -DateKind String
                    $Summary['addressables'] = Assert-YLUnityAddressablesVersion -ExpectedVersion $addressablesVersion -Lock $actualLock -Response $response
                }
                if ($response.isBatchMode -isnot [bool] -or $response.isBatchMode -eq $desktop) { throw 'The actual Editor desktop/batch mode differs from the explicit host profile.' }
                $Summary.unity['isBatchMode'] = $response.isBatchMode
                $Summary.unity['testFrameworkVersion'] = $response.testFrameworkVersion
                $Summary['runtimeReferences'] = @($response.runtimeReferences)
                if ($Operation -ceq 'Discover') {
                    $Summary['excludedCategories'] = @($Definition.excludedCategories)
                    $Summary['excludedTests'] = @($response.excludedTests)
                    $Summary['graphics'] = [bool]$Definition.graphics
                }
                return [pscustomobject]@{ response = $response; process = $fact; output = $output; log = $unityLog }
            } finally {
                if ($Operation -ceq 'Run') {
                    $Summary['xml'] = @($Summary.xml + @(@('EditMode', 'PlayMode') | ForEach-Object { Join-Path $output "results-$_.xml" } | Where-Object { [IO.File]::Exists($_) }) | Select-Object -Unique)
                }
            }
        }

        if ('sampleInputs' -cin $Definition.PSObject.Properties.Name -and @($Definition.sampleInputs).Count -gt 0) {
            $prepared = Invoke-NativeOperation -Operation PrepareSamples
            if ($prepared.response.status -cne 'Prepared') { throw 'The declared package samples were not prepared.' }
            $sampleRoot = Get-YLHostManagedPath $hostRecord 'Assets/Samples'
            $sampleFiles = @(Get-YLTestTreeFiles $sampleRoot | ForEach-Object {
                @{ path = [IO.Path]::GetRelativePath($hostRecord.path, $_).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
            })
            if ($sampleFiles.Count -eq 0) { throw 'No files were imported for the declared samples.' }
            $sampleReceiptPath = Join-Path $hostRecord.path '.yl-samples.json'
            if ([IO.File]::Exists($sampleReceiptPath)) {
                $recorded = [IO.File]::ReadAllText($sampleReceiptPath) | ConvertFrom-Json -AsHashtable -DateKind String
                $recordedFiles = @{}
                foreach ($item in $recorded['files']) { $recordedFiles.Add($item['path'], $item['sha256']) }
                if ($recordedFiles.Count -ne $sampleFiles.Count) { throw 'A reused sample import changed its owned file set.' }
                foreach ($item in $sampleFiles) {
                    if (-not $recordedFiles.ContainsKey($item.path) -or $recordedFiles[$item.path] -cne $item.sha256) { throw 'A reused sample import changed its owned content.' }
                }
            } else {
                Write-YLTestJson -Path $sampleReceiptPath -Value @{
                    schemaVersion = 1; purpose = 'YLTesting.Samples'; hostId = $hostRecord.id; inputHash = $identity.sha256; files = $sampleFiles
                }
            }
        }
        $discovered = Invoke-NativeOperation -Operation Discover
        $fresh = Get-SelectedSources -Native -EditorProfile $EditorProfile
        $sourceRecheck = Get-YLTestInputIdentity -Repository $Repository -Paths $fresh.inputs -Toolchain $nativeTools -Profile $profile
        if ($sourceRecheck.sha256 -cne $identity.sha256) { throw 'Source inputs drifted during Unity discovery.' }
        $toolFiles = @($nativeTools) + @((Join-Path $hostRecord.path 'Packages/manifest.json'), (Join-Path $hostRecord.path 'Packages/packages-lock.json')) + @($discovered.response.compiledInputs)
        $effective = Get-YLTestInputIdentity -Repository $Repository -Paths $fresh.inputs -Toolchain $toolFiles -Profile $profile
        $actualSelection = New-YLTestSelection -InputIdentity $effective -Tests $discovered.response.tests
        Assert-YLUnitySelectionModes -Selection $actualSelection -Definition $Definition -Suite $Suite
        $Summary['inputIdentity'] = $effective
        if ($Selection) {
            $saved = [IO.File]::ReadAllText((Get-YLTestPath $Selection)) | ConvertFrom-Json -DateKind String
            Assert-YLTestSelection -Selection $saved -InputIdentity $effective
            $savedKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($test in $saved.tests) { $null = $savedKeys.Add($test.mode + '|' + $test.assembly + '|' + $test.fullName) }
            $currentKeys = [string[]]@($actualSelection.tests | ForEach-Object { $_.mode + '|' + $_.assembly + '|' + $_.fullName })
            if (-not $savedKeys.SetEquals($currentKeys)) { throw 'Saved selection does not match current category discovery.' }
        }
        $Summary['selection'] = Join-Path $run.path 'selection.json'
        Write-YLTestJson -Path $Summary.selection -Value $actualSelection
        if ($Action -ceq 'Run') {
            $modes = @(foreach ($mode in @($actualSelection.tests.mode | Sort-Object -Unique)) {
                $modeTests = @($actualSelection.tests | Where-Object { $_.mode -ceq $mode })
                $execution = Invoke-NativeOperation -Operation Run -Tests $modeTests -Mode $mode
                if ($execution.response.status -cne 'Passed') { throw 'Native execution was not an ordinary test pass.' }
                $fresh = Get-SelectedSources -Native -EditorProfile $EditorProfile
                $currentTools = @($nativeTools) + @((Join-Path $hostRecord.path 'Packages/manifest.json'), (Join-Path $hostRecord.path 'Packages/packages-lock.json')) + @($execution.response.compiledInputs)
                $current = Get-YLTestInputIdentity -Repository $Repository -Paths $fresh.inputs -Toolchain $currentTools -Profile $profile
                if ($current.sha256 -cne $effective.sha256) {
                    $Summary['inputDrift'] = @{
                        mode = $mode
                        expectedHash = $effective.sha256; actualHash = $current.sha256
                        expectedProfile = $effective.profile; actualProfile = $current.profile
                        expectedRepository = $effective.repository; actualRepository = $current.repository
                        sources = @(Compare-Object -ReferenceObject @($effective.files | ForEach-Object { [pscustomobject]$_ }) -DifferenceObject @($current.files | ForEach-Object { [pscustomobject]$_ }) -Property path, sha256 -CaseSensitive | Select-Object path, sha256, SideIndicator)
                        toolchain = @(Compare-Object -ReferenceObject @($effective.toolchain | ForEach-Object { [pscustomobject]$_ }) -DifferenceObject @($current.toolchain | ForEach-Object { [pscustomobject]$_ }) -Property path, sha256 -CaseSensitive | Select-Object path, sha256, SideIndicator)
                    }
                }
                $result = Read-YLTestXml -Path (Join-Path $execution.output "results-$mode.xml") -Selection $actualSelection -InputIdentity $current -Mode $mode -ProcessResult $execution.process
                if ($packed) { $Summary.packedCatalog.evidence = Read-YLPackedCatalogEncoding -Log $execution.log -Packed $packed -AddressablesVersion $addressablesVersion }
                $result
            })
            $total = ($modes | Measure-Object -Property total -Sum).Sum
            $Summary['testResult'] = [ordered]@{ status = 'Passed'; total = $total; passed = $total; failed = 0; skipped = 0; modes = @($modes) }
        }
        if ($dotween) {
            $null = Get-YLIntegrationDotweenSdk -Repository $Repository -Archive $DOTweenArchive -OwnedSdkRoot $sdk.root
            $null = Get-YLIntegrationDotweenSdk -Repository $Repository -Archive $DOTweenArchive -OwnedSdkRoot (Join-Path $hostRecord.path 'Assets/Plugins/Demigiant/DOTween')
        }
        $Summary['status'] = if ($Action -ceq 'Run') { 'Passed' } else { 'Completed' }
    } catch {
        $Summary['reason'] = $_.Exception.Message
        throw
    } finally {
        try {
            if ($null -ne $run) {
                $Summary['endUtc'] = [DateTime]::UtcNow.ToString('o')
                Write-YLTestJson -Path $Summary.summary -Value $Summary
            }
        } finally {
            if ($null -ne $runLease) { $runLease.Dispose() }
            $hostLease.Dispose()
        }
        if ($null -ne $run) {
            $null = Invoke-YLTestRotation -Workspace $workspace -Key $run.key
        }
    }
}
