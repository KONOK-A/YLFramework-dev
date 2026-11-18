#requires -Version 7.6
#requires -PSEdition Core
. (Join-Path $PSScriptRoot 'UnityHost.ps1')
. (Join-Path $PSScriptRoot 'Common/Process.ps1')
. (Join-Path $PSScriptRoot 'Common/Results.ps1')
. (Join-Path $PSScriptRoot 'Common/ResourceMigration.ps1')

function Invoke-YLResourceLegacyExport {
    [CmdletBinding()]
    param(
        [string] $Repository,
        [string] $Snapshot,
        [string] $Unity,
        [string] $WorkspaceRoot,
        [string] $LegacyExporter,
        [string] $LegacyExporterSha256,
        [string] $LegacyExporterMetaSha256,
        [int] $TimeoutSeconds = 1800
    )
    $Repository = Get-YLTestPath $Repository
    $Snapshot = Get-YLTestPath $Snapshot
    $Unity = Get-YLTestPath -Path $Unity -ReadOnly
    $verified = Test-YLResourceSnapshot -Path $Snapshot
    if ([string]::IsNullOrWhiteSpace($LegacyExporter) -or $LegacyExporterSha256 -cnotmatch '^[0-9a-f]{64}$' -or $LegacyExporterMetaSha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw 'Export requires an explicitly pinned preserved legacy exporter and metadata; the mutable current exporter is not a fallback.'
    }
    $LegacyExporter = Get-YLTestPath $LegacyExporter
    if (-not $LegacyExporter.StartsWith((Join-Path $Snapshot 'Inventory') + '\', [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($LegacyExporter) -cne 'ResourceLegacyExport.cs' -or
        (Get-YLResourceMigrationHash $LegacyExporter) -cne $LegacyExporterSha256 -or
        (Get-YLResourceMigrationHash ($LegacyExporter + '.meta')) -cne $LegacyExporterMetaSha256) {
        throw 'Export requires the preserved exact legacy exporter and metadata with explicit SHA pins, never the mutable current exporter.'
    }
    $snapshotData = [IO.File]::ReadAllText((Join-Path $Snapshot 'snapshot.json')) | ConvertFrom-Json -AsHashtable -DateKind String
    $source = Join-Path $Snapshot 'Source'
    $version = [IO.File]::ReadAllText((Join-Path $source 'ProjectSettings/ProjectVersion.txt'))
    if ($version -notmatch '(?m)^m_EditorVersion: (6000\.3\.27f1)\r?$') { throw 'Legacy export currently requires the captured Unity 6 profile.' }
    $editorVersion = $Matches[1]
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($Unity).ProductVersion -cne '6000.3.27f1_672d562a42a7') {
        throw 'The export Editor must match the exact captured revision.'
    }
    $toolPath = 'Assets/Tests/YLFramework/EditMode/Editor/Resource/ResourceLegacyExport.cs'
    $identity = Get-YLTestInputIdentity -Repository $Repository -Paths @(
        [IO.Path]::GetRelativePath($Repository, (Join-Path $Snapshot 'snapshot.json')),
        [IO.Path]::GetRelativePath($Repository, $LegacyExporter), [IO.Path]::GetRelativePath($Repository, ($LegacyExporter + '.meta')), 'Tools/Testing/ResourceMigrationHost.ps1'
    ) -Toolchain @($Unity) -Profile ('LegacyResourceExport-' + $verified.manifestSha256)
    $workspace = Initialize-YLTestWorkspace -WorkspaceRoot $WorkspaceRoot -Repository $Repository
    # Unity can normalize importer metadata. Preserve each attempt; never replace its managed-input receipt.
    $hostRecord = New-YLTestDirectory -Workspace $workspace -Kind Host -Key ('Legacy-' + $verified.manifestSha256.Substring(0, 8) + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $hostLease = Lock-YLTestDirectory -Workspace $workspace -Path $hostRecord.path
    $run = New-YLTestDirectory -Workspace $workspace -Kind Result -Key 'ResourceLegacyExport'
    $runLease = Lock-YLTestDirectory -Workspace $workspace -Path $run.path
    $summary = [ordered]@{
        schemaVersion = 1; purpose = 'YLResource.LegacyExport'; status = 'Failed'
        snapshot = $Snapshot; snapshotManifestSha256 = $verified.manifestSha256
        host = $hostRecord.path; hostId = $hostRecord.id; result = $run.path; inputHash = $identity.sha256
        process = $null; reason = $null; inventory = $null
    }
    try {
        Assert-YLUnityHostIdle $hostRecord
        [IO.File]::WriteAllText((Join-Path $run.path '.pinned'), 'Legacy migration inventory evidence.')
        $files = [ordered]@{}
        foreach ($entry in $snapshotData.files) {
            if ($entry.path.StartsWith('Assets/', [StringComparison]::Ordinal) -or
                $entry.path.StartsWith('PackageSources/', [StringComparison]::Ordinal) -or
                $entry.path -ceq 'ProjectSettings/TagManager.asset') {
                $files[$entry.path] = [IO.File]::ReadAllBytes((Get-YLResourceMigrationPath $source $entry.path))
            }
        }
        # The exporter is the only source overlay; archived product definitions remain unchanged.
        $files[$toolPath] = [IO.File]::ReadAllBytes($LegacyExporter)
        $files[$toolPath + '.meta'] = [IO.File]::ReadAllBytes($LegacyExporter + '.meta')
        $manifest = [IO.File]::ReadAllText((Join-Path $source 'Packages/manifest.json')) | ConvertFrom-Json -AsHashtable -DateKind String
        foreach ($id in @($manifest.dependencies.Keys)) {
            if ($id.StartsWith('com.ivanmurzak.', [StringComparison]::Ordinal) -or $id -ceq 'com.unity.cinemachine') {
                $manifest.dependencies.Remove($id)
            }
        }
        $locked = [IO.File]::ReadAllText((Join-Path $source 'Packages/packages-lock.json')) | ConvertFrom-Json -AsHashtable -DateKind String
        foreach ($id in @('com.unity.addressables', 'com.unity.nuget.newtonsoft-json', 'com.unity.test-framework')) {
            if ($locked.dependencies.Contains($id) -and $locked.dependencies[$id].source -ceq 'registry') {
                $manifest.dependencies[$id] = $locked.dependencies[$id].version
            }
        }
        $files['Packages/manifest.json'] = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $manifest -Depth 20))
        Sync-YLUnityHost -HostRecord $hostRecord -Files $files -Identity $identity -EditorVersion $editorVersion
        $requestPath = Join-Path $run.path 'request.json'
        Write-YLTestJson -Path $requestPath -Value ([ordered]@{
            schemaVersion = 1; purpose = 'YLResource.LegacyExport'; workspaceId = $workspace.id
            hostId = $hostRecord.id; resultId = $run.id; inputHash = $identity.sha256
            expectedUnityVersion = $editorVersion; output = $run.path
            snapshotManifestSha256 = $verified.manifestSha256
        })
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName = $Unity
        $start.WorkingDirectory = $hostRecord.path
        $start.UseShellExecute = $false
        foreach ($argument in @('-batchmode', '-nographics', '-projectPath', $hostRecord.path,
            '-executeMethod', 'YLFramework.Testing.ResourceLegacyExport.Execute',
            '-ylResourceExportRequest', $requestPath, '-logFile', (Join-Path $run.path 'unity.log'))) {
            $start.ArgumentList.Add($argument)
        }
        $summary.process = Invoke-YLTestProcess -StartInfo $start -Log (Join-Path $run.path 'process.log') -TimeoutMilliseconds ($TimeoutSeconds * 1000)
        Assert-YLTestProcessSucceeded $summary.process
        $inventoryPath = Join-Path $run.path 'inventory.json'
        $inventory = [IO.File]::ReadAllText($inventoryPath) | ConvertFrom-Json -AsHashtable -DateKind String
        if ($inventory.purpose -cne 'YLResource.LegacyInventory' -or $inventory.hostId -cne $hostRecord.id -or
            $inventory.inputHash -cne $identity.sha256 -or $inventory.unityVersion -cne $editorVersion -or
            $inventory.snapshotManifestSha256 -cne $verified.manifestSha256 -or $inventory.assets.Count -eq 0) {
            throw 'The native export inventory does not match this owned invocation.'
        }
        $null = Test-YLResourceSnapshot -Path $Snapshot
        $summary.inventory = $inventoryPath
        $summary['assetCount'] = $inventory.assets.Count
        $summary['projectCount'] = $inventory.projects.Count
        $summary.status = 'Exported'
    } catch {
        $summary.reason = $_.Exception.Message
        throw
    } finally {
        try { Write-YLTestJson -Path (Join-Path $run.path 'summary.json') -Value $summary }
        finally { $runLease.Dispose(); $hostLease.Dispose() }
        $summary | ConvertTo-Json -Depth 10
    }
}

function Invoke-YLResourceMigrationCandidate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][Collections.IDictionary] $Preview,
        [Parameter(Mandatory)][string] $Unity,
        [Parameter(Mandatory)][string] $WorkspaceRoot,
        [Parameter(Mandatory)][string] $CandidateInputsFile,
        [ValidateSet('Preview', 'Verify')][string] $Action = 'Preview',
        [switch] $IncludeRetirement,
        [int] $TimeoutSeconds = 1800
    )
    if ($Preview.status -cne 'ReadyForCandidate' -or $Preview.blockers.Count -gt 0) { throw 'Blocked inputs cannot start a candidate host.' }
    if ($IncludeRetirement -and ($Action -cne 'Preview' -or -not $Preview.retirement.entries.Count)) { throw 'A final candidate requires an explicit frozen retirement set.' }
    $repository = Get-YLTestPath $Preview.repository
    Assert-YLResourcePreviewSource $Preview
    $Unity = Get-YLTestPath -Path $Unity -ReadOnly
    $CandidateInputsFile = Get-YLTestPath $CandidateInputsFile
    $selection = [IO.File]::ReadAllText($CandidateInputsFile) | ConvertFrom-Json -AsHashtable -DateKind String
    if ($selection.schemaVersion -ne 1 -or $selection.inputPaths -isnot [array] -or $selection.inputPaths.Count -eq 0) { throw 'CandidateInputsFile requires explicit schemaVersion 1 inputPaths for the current native compilation closure.' }
    $toolPath = 'Assets/Tests/YLFramework/EditMode/Editor/Resource/ResourceAddressablesMigration.cs'
    $paths = @($selection.inputPaths) + @($Preview.inputs.path) + @($toolPath, 'ProjectSettings/ProjectVersion.txt')
    $sources = Get-YLResourceSnapshotInputs -Repository $repository -InputPaths $paths
    $relativePaths = @($sources.files | ForEach-Object { [IO.Path]::GetRelativePath($repository, $_).Replace('\', '/') })
    foreach ($required in @('Packages/manifest.json', 'Packages/packages-lock.json')) {
        if ($required -cnotin $relativePaths) { throw "Candidate native inputs must explicitly include $required; no package graph is guessed." }
    }
    $packageLock = [IO.File]::ReadAllText((Join-Path $repository 'Packages/packages-lock.json')) | ConvertFrom-Json -AsHashtable -DateKind String
    if (-not $packageLock.dependencies.Contains('com.unity.addressables') -or
        $packageLock.dependencies['com.unity.addressables'].source -cne 'registry') { throw 'Candidate requires the exact registry Addressables resolution.' }
    $addressablesVersion = $packageLock.dependencies['com.unity.addressables'].version
    if ($addressablesVersion -cnotmatch '^\d+\.\d+\.\d+$') { throw 'The source Addressables lock version must be explicit.' }
    $versionText = [IO.File]::ReadAllText((Join-Path $repository 'ProjectSettings/ProjectVersion.txt'))
    if ($versionText -notmatch '(?m)^m_EditorVersion: ([0-9.]+[abfp][0-9]+)\r?$') { throw 'Cannot identify the current source Editor version.' }
    $editorVersion = $Matches[1]
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($Unity).ProductVersion -notlike ($editorVersion + '_*')) { throw 'Candidate Unity must match the source Editor build.' }
    $supplementHash = if ($Preview.Contains('supplement') -and $Preview.supplement) { $Preview.supplement.sha256 } else { '' }
    $identity = Get-YLTestInputIdentity -Repository $repository -Paths $relativePaths -Toolchain @($Unity) -Profile ('ResourceMigration-' + $Preview.previewId + '-' + $Action + $supplementHash)
    $workspace = Initialize-YLTestWorkspace -WorkspaceRoot $WorkspaceRoot -Repository $repository
    $hostRecord = New-YLTestDirectory -Workspace $workspace -Kind Host -Key ('Migration-' + [Guid]::NewGuid().ToString('N'))
    $hostLease = Lock-YLTestDirectory -Workspace $workspace -Path $hostRecord.path
    $run = New-YLTestDirectory -Workspace $workspace -Kind Result -Key ('ResourceMigration' + $Action)
    $runLease = Lock-YLTestDirectory -Workspace $workspace -Path $run.path
    $summary = [ordered]@{ schemaVersion = 1; purpose = 'YLResource.MigrationCandidateRun'; status = 'Failed'; host = $hostRecord.path; result = $run.path; process = $null; reason = $null; runtimeVerified = $false }
    try {
        Assert-YLUnityHostIdle $hostRecord
        [IO.File]::WriteAllText((Join-Path $run.path '.pinned'), 'Preserve migration input receipts, original bytes, outputs and validation evidence.')
        $files = [ordered]@{}
        foreach ($path in $relativePaths) {
            $files[$path] = [IO.File]::ReadAllBytes((Get-YLResourceMigrationPath $repository $path))
        }
        $nativePreview = Get-YLResourceMigrationCandidatePreview $Preview
        if ($supplementHash) {
            foreach ($input in @($Preview.candidateOnlyInputs) + @($Preview.overlays)) {
                if ($null -eq $input) { continue }
                $payload = Get-YLResourceSupplementFile $Preview.supplement.root $input.payload $input.sha256
                $files[$input.path] = [IO.File]::ReadAllBytes($payload)
            }
        }
        foreach ($directory in $sources.directories) {
            $relative = [IO.Path]::GetRelativePath($repository, $directory).Replace('\', '/')
            $null = [IO.Directory]::CreateDirectory((Get-YLResourceMigrationPath $hostRecord.path $relative))
        }
        Sync-YLUnityHost -HostRecord $hostRecord -Files $files -Identity $identity -EditorVersion $editorVersion
        $previewPath = Join-Path $run.path 'preview.json'
        Write-YLTestJson -Path $previewPath -Value $nativePreview
        $previewSha = Get-YLResourceMigrationHash $previewPath
        $requestPath = Join-Path $run.path 'request.json'
        Write-YLTestJson -Path $requestPath -Value ([ordered]@{
            schemaVersion = 1; purpose = 'YLResource.AddressablesMigration'; action = $Action; workspaceId = $workspace.id
            hostId = $hostRecord.id; resultId = $run.id; inputHash = $identity.sha256
            expectedUnityVersion = $editorVersion; expectedAddressablesVersion = $addressablesVersion; output = $run.path; previewSha256 = $previewSha
        })
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName = $Unity
        $start.WorkingDirectory = $hostRecord.path
        $start.UseShellExecute = $false
        foreach ($argument in @('-batchmode', '-nographics', '-projectPath', $hostRecord.path, '-executeMethod',
            'YLFramework.Testing.ResourceAddressablesMigration.Execute', '-ylResourceMigrationRequest', $requestPath,
            '-logFile', (Join-Path $run.path 'unity.log'))) { $start.ArgumentList.Add($argument) }
        $summary.process = Invoke-YLTestProcess -StartInfo $start -Log (Join-Path $run.path 'process.log') -TimeoutMilliseconds ($TimeoutSeconds * 1000)
        Assert-YLTestProcessSucceeded $summary.process
        $responsePath = Join-Path $run.path 'candidate.json'
        $response = [IO.File]::ReadAllText($responsePath) | ConvertFrom-Json -AsHashtable -DateKind String
        $expectedStatus = if ($Action -ceq 'Preview') { 'CandidatePrepared' } else { 'ConfigurationVerified' }
        if ($response.schemaVersion -ne 1 -or $response.purpose -cne 'YLResource.AddressablesMigrationCandidate' -or
            $response.hostId -cne $hostRecord.id -or $response.inputHash -cne $identity.sha256 -or
            $response.previewSha256 -cne $previewSha -or $response.previewId -cne $Preview.previewId -or
            $response.unityVersion -cne $editorVersion -or $response.addressablesVersion -cne $addressablesVersion -or
            $response.status -cne $expectedStatus -or $response.runtimeVerified -ne $false) { throw 'Candidate response does not match this owned invocation.' }
        $summary['candidate'] = $responsePath
        if ($Action -ceq 'Preview') {
            $manifest = New-YLResourceCandidateManifest -Preview $Preview -PreviewSha256 $previewSha -HostRecord $hostRecord -Result $run.path -SourceIdentity $identity -UnityVersion $response.unityVersion -AddressablesVersion $response.addressablesVersion -IncludeRetirement:$IncludeRetirement
            $summary['manifest'] = $manifest
            $summary['manifestSha256'] = Get-YLResourceMigrationHash $manifest
        }
        $summary.status = $expectedStatus
    } catch {
        $summary.status = 'Failed'
        $summary.reason = $_.Exception.Message
        throw
    } finally {
        try { Write-YLTestJson -Path (Join-Path $run.path 'summary.json') -Value $summary }
        finally { $runLease.Dispose(); $hostLease.Dispose() }
    }
    return [pscustomobject]$summary
}

function New-YLResourceCandidateManifest {
    param([Collections.IDictionary] $Preview, [string] $PreviewSha256, [object] $HostRecord, [string] $Result, [object] $SourceIdentity, [string] $UnityVersion, [string] $AddressablesVersion, [switch] $IncludeRetirement)
    if ($UnityVersion -cnotmatch '^\d+\.\d+\.\d+[abfp]\d+$' -or $AddressablesVersion -cnotmatch '^\d+\.\d+\.\d+$') { throw 'Candidate packaging requires the exact native Unity and Addressables profile.' }
    $repository = Get-YLTestPath $Preview.repository
    Assert-YLResourcePreviewSource $Preview
    $originals = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($input in $SourceIdentity.files) {
        $originals.Add($input.path, [ordered]@{ path = $input.path; sha256 = $input.sha256; guid = $(if ($input.path.EndsWith('.meta', [StringComparison]::Ordinal)) { Get-YLResourceMigrationGuid (Get-YLResourceMigrationPath $repository $input.path) } else { $null }) })
    }
    $rewritePaths = @($Preview.rewrites | ForEach-Object { $_.path })
    $overlayPaths = if ($Preview.Contains('overlays')) { @($Preview.overlays | ForEach-Object { $_.path }) } else { @() }
    function Test-MigrationOutputPath([string] $Relative) {
        return $Relative -ceq ($Preview.settingsRoot + '.meta') -or $Relative.StartsWith($Preview.settingsRoot + '/', [StringComparison]::Ordinal) -or
            ($Preview.useAsDefault -and ($Relative -ceq 'Assets/AddressableAssetsData.meta' -or $Relative -ceq 'Assets/AddressableAssetsData/DefaultObject.asset' -or $Relative -ceq 'Assets/AddressableAssetsData/DefaultObject.asset.meta')) -or
            $Relative -ceq 'ProjectSettings/EditorBuildSettings.asset' -or $Relative -cin $rewritePaths -or $Relative -cin $overlayPaths -or
            ($Relative.EndsWith('.meta', [StringComparison]::Ordinal) -and $Relative.Substring(0, $Relative.Length - 5) -cin $rewritePaths)
    }
    foreach ($input in $originals.Values) {
        if ((Get-YLResourceMigrationHash (Get-YLResourceMigrationPath $repository $input.path)) -cne $input.sha256) { throw "Source changed during candidate validation: $($input.path)" }
        if (-not (Test-MigrationOutputPath $input.path)) {
            $staged = Get-YLResourceMigrationPath $HostRecord.path $input.path
            $expected = $input.sha256
            if ($Preview.Contains('candidateOnlyInputs')) {
                $normalization = @($Preview.candidateOnlyInputs | Where-Object { $_ -and $_.path -ceq $input.path })
                if ($normalization.Count -eq 1) { $expected = $normalization[0].sha256 }
            }
            if (-not [IO.File]::Exists($staged) -or (Get-YLResourceMigrationHash $staged) -cne $expected) { throw "An unselected source was modified by candidate import: $($input.path)" }
        }
    }
    $paths = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    $settingsRoot = Get-YLResourceMigrationPath $HostRecord.path $Preview.settingsRoot
    foreach ($path in @(Get-YLTestTreeFiles $settingsRoot)) { $null = $paths.Add([IO.Path]::GetRelativePath($HostRecord.path, $path).Replace('\', '/')) }
    if ([IO.File]::Exists($settingsRoot + '.meta')) { $null = $paths.Add($Preview.settingsRoot + '.meta') }
    if ($Preview.useAsDefault) {
        foreach ($relative in @('Assets/AddressableAssetsData.meta', 'Assets/AddressableAssetsData/DefaultObject.asset', 'Assets/AddressableAssetsData/DefaultObject.asset.meta')) {
            if ([IO.File]::Exists((Join-Path $HostRecord.path $relative))) { $null = $paths.Add($relative) }
        }
    }
    if ([IO.File]::Exists((Join-Path $HostRecord.path 'ProjectSettings/EditorBuildSettings.asset'))) { $null = $paths.Add('ProjectSettings/EditorBuildSettings.asset') }
    foreach ($rewrite in $Preview.rewrites) { $null = $paths.Add($rewrite.path); $null = $paths.Add($rewrite.path + '.meta') }
    foreach ($relative in $overlayPaths) { $null = $paths.Add($relative) }
    $allSourceGuids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($path in @(Get-YLTestTreeFiles (Join-Path $repository 'Assets'))) {
        if ($path.EndsWith('.meta', [StringComparison]::Ordinal)) { $null = $allSourceGuids.Add((Get-YLResourceMigrationGuid $path)) }
    }
    $operations = [Collections.Generic.List[object]]::new()
    foreach ($relative in $paths) {
        if (-not (Test-MigrationOutputPath $relative)) { throw 'Candidate attempted an unselected output.' }
        $candidate = Get-YLResourceMigrationPath $HostRecord.path $relative
        $target = Get-YLResourceMigrationPath $repository $relative
        $outputSha = Get-YLResourceMigrationHash $candidate
        if ($relative -cin $overlayPaths) {
            $overlay = @($Preview.overlays | Where-Object { $_.path -ceq $relative })
            if ($overlay.Count -ne 1 -or $outputSha -cne $overlay[0].sha256) { throw 'Candidate Atlas differs from the certified generated payload.' }
            $null = Get-YLResourceSupplementFile $Preview.supplement.root $overlay[0].payload $outputSha
        }
        $input = if ($originals.ContainsKey($relative)) { $originals[$relative] } else { $null }
        if ($input -and $input.sha256 -ceq $outputSha) { continue }
        if ($input -and $relative.StartsWith($Preview.settingsRoot + '/', [StringComparison]::Ordinal) -and $relative -cne $Preview.settingsPath) {
            throw "An existing foreign official Settings/Group/Schema asset changed; preserve it rather than packaging the rewrite: $relative"
        }
        if (-not $input -and [IO.Path]::Exists($target)) { throw "Unrecorded existing target must not be overwritten: $relative" }
        $newGuid = if ($relative.EndsWith('.meta', [StringComparison]::Ordinal)) { Get-YLResourceMigrationGuid $candidate } else { $null }
        if ($input -and $input.guid -and $input.guid -cne $newGuid) { throw "Candidate changed an original metadata GUID: $relative" }
        if (-not $input -and $newGuid -and -not $allSourceGuids.Add($newGuid)) { throw "A new official asset GUID collides with existing source metadata: $relative" }
        $destination = Get-YLResourceMigrationPath $Result ('Output/' + $relative)
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        [IO.File]::Copy($candidate, $destination, $false)
        $operations.Add([ordered]@{
            path = $relative; action = $(if ($input) { 'Modify' } else { 'Add' }); expectedAbsent = -not [bool]$input
            inputSha256 = $(if ($input) { $input.sha256 } else { $null }); outputSha256 = $outputSha
            oldGuid = $(if ($input) { $input.guid } else { $null }); newGuid = $newGuid
            fields = @($Preview.rewrites | Where-Object { $_.path -ceq $relative })
        })
    }
    if ($operations.Count -eq 0) { throw 'No candidate differences were produced; do not report an empty conversion as migrated.' }
    $retirement = $Preview['retirement']
    if ($IncludeRetirement) {
        if (-not $retirement -or -not $retirement.entries.Count) { throw 'A final candidate requires an explicit frozen retirement set.' }
        foreach ($delete in New-YLCandidateRetirementDeletes $Preview) {
            if (-not $originals.ContainsKey($delete.path) -or $originals[$delete.path].sha256 -cne $delete.inputSha256) { throw "Retirement was not in the pinned native inputs: $($delete.path)" }
            Assert-YLResourceMigrationFileState -Root $repository -Operation $delete
            Assert-YLResourceMigrationFileState -Root $HostRecord.path -Operation $delete
            $operations.Add($delete)
        }
        $retirement = @{ deleteEnabled = $true; entries = $retirement.entries; negativeProjectGuids = $retirement.negativeProjectGuids; status = 'FrozenAwaitingRuntimeValidation' }
    }
    $operationArray = $operations.ToArray()
    $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $operationArray -Depth 40 -Compress)))).ToLowerInvariant()
    $manifestPath = Join-Path $Result 'migration.json'
    $manifest = [ordered]@{
        schemaVersion = 1; purpose = 'YLResource.AddressablesMigrationManifest'; status = 'AwaitingNativeRuntimeValidation'
        previewId = $Preview.previewId; previewSha256 = $PreviewSha256; repository = $repository
        hostId = $HostRecord.id; inputHash = $SourceIdentity.sha256; unityVersion = $UnityVersion; addressablesVersion = $AddressablesVersion
        sourceCommit = $Preview.sourceCommit; inventorySha256 = $Preview.inventorySha256; outputDigest = $digest
        inputs = @($originals.Values); folders = @($Preview.folders); operations = $operationArray
        supplement = $(if ($Preview.Contains('supplement')) { $Preview.supplement } else { $null })
        retirement = $retirement
        validationRequired = @('OfficialContentLoad', 'ConsumerColdRead', 'ForeignSettingsPreserved'); runtimeVerified = $false
    }
    Assert-YLResourceRetirementBatch $manifest
    Write-YLTestJson -Path $manifestPath -Value $manifest
    return $manifestPath
}
