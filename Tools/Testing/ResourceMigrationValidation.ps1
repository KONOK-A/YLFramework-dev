#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param(
    [switch] $SelfTest,
    [string] $CandidateDirectory,
    [string] $CandidateHost,
    [string] $Repository,
    [string] $ManifestSha256,
    [string] $ApplyReceiptDirectory,
    [string] $WorkspaceRoot,
    [string] $Unity,
    [switch] $PrepareOnly,
    [switch] $FunctionsOnly,
    [string] $PreparedRequest,
    [int] $TimeoutSeconds = 1800
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'UnityHost.ps1')
. (Join-Path $PSScriptRoot 'Common/Process.ps1')
. (Join-Path $PSScriptRoot 'Common/Results.ps1')
. (Join-Path $PSScriptRoot 'Common/ResourceMigration.ps1')

function Read-YLCandidateJson([string] $Path) {
    return [IO.File]::ReadAllText((Get-YLTestPath $Path)) | ConvertFrom-Json -AsHashtable -DateKind String
}

function Get-YLCandidateFilesIdentity([Collections.IDictionary] $Files) {
    $rows = @($Files.GetEnumerator() | Sort-Object Key -CaseSensitive | ForEach-Object { [ordered]@{ path = $_.Key; sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($_.Value)).ToLowerInvariant() } })
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $rows -Depth 8 -Compress)))).ToLowerInvariant()
}

function Invoke-YLCandidateEditorProcess([Collections.IDictionary] $Request, [string] $RequestPath, [string] $Entry) {
    $start = [Diagnostics.ProcessStartInfo]::new($Request.unity)
    $start.UseShellExecute = $false; $start.WorkingDirectory = $Request.host
    foreach ($argument in @('-batchmode', '-nographics', '-projectPath', $Request.host, '-buildTarget', 'StandaloneWindows64', '-executeMethod', ('YLFramework.Testing.ResourceAddressablesCandidateValidation.' + $Entry), '-ylCandidateValidationRequest', $RequestPath, '-logFile', (Join-Path $Request.result 'unity.log'))) { $start.ArgumentList.Add($argument) }
    return Invoke-YLTestProcess -StartInfo $start -Log (Join-Path $Request.result 'process.log') -TimeoutMilliseconds ($TimeoutSeconds * 1000)
}

function Assert-YLOriginalSettingsProof([Collections.IDictionary] $Proof, [Collections.IDictionary] $Request, [string] $RequestSha256) {
    if ($Proof.schemaVersion -ne 1 -or $Proof.purpose -cne 'YLResource.OriginalSettingsFootprint' -or $Proof.status -cne 'Captured' -or $Proof.mode -cne 'OriginalSettings' -or
        $Proof.runtimeVerified -isnot [bool] -or $Proof.runtimeVerified -or $Proof.requestSha256 -cne $RequestSha256 -or
        $Proof.unityVersion -cne $Request.expectedUnityVersion -or $Proof.addressablesVersion -cne $Request.expectedAddressablesVersion -or $Proof.addressablesSource -cne 'Registry') { throw 'Original Settings were not captured from the exact owned cold input.' }
    foreach ($name in @('hostId','resultId','inputHash','previewSha256','manifestSha256','outputDigest','subjectHostId','subjectInputHash')) {
        if ($Proof[$name] -cne $Request[$name]) { throw "Foreign original Settings capture: $name" }
    }
    if ($Proof.footprint.schemaVersion -ne 1 -or -not $Proof.footprint.settings.identity.guid -or -not $Proof.footprint.nodes.Count -or -not $Proof.footprint.files.Count -or -not $Proof.footprint.registration.Contains('fields')) { throw 'Incomplete original serialized Settings/registration footprint.' }
}

function Assert-YLCandidateOperationScope([Collections.IDictionary] $Manifest, [Collections.IDictionary] $Preview) {
    $rewritePaths = @($Preview.rewrites | ForEach-Object { $_.path })
    $overlays = @{}
    if ($Preview.Contains('overlays')) { foreach ($row in @($Preview.overlays)) { if ($row) { $overlays.Add($row.path, $row) } } }
    $retirement = @{}
    if ($Preview.Contains('retirement') -and $Preview.retirement) { foreach ($row in $Preview.retirement.entries) { $retirement.Add($row.path, $row) } }
    $deleteEnabled = $Manifest.Contains('retirement') -and $Manifest.retirement -and $Manifest.retirement.deleteEnabled -eq $true
    if ($retirement.Count -or $Manifest['retirement']) {
        if (-not $Manifest['retirement']) { throw 'The manifest omitted the frozen retirement membership.' }
        if ($Manifest.retirement.entries.Count -ne $retirement.Count) { throw 'The final retirement membership changed.' }
        foreach ($row in $Manifest.retirement.entries) {
            if (-not $retirement.ContainsKey($row.path)) { throw 'The manifest introduced an unselected retirement.' }
            $frozen = $retirement[$row.path]
            foreach ($field in @('path', 'guid', 'kind', 'originalSha256', 'negative')) { if ($row[$field] -cne $frozen[$field]) { throw 'Frozen retirement identity changed.' } }
        }
    }
    Assert-YLResourceRetirementBatch $Manifest
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $seenOverlays = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $seenDeletes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $deleting = $false
    foreach ($operation in $Manifest.operations) {
        $path = [string]$operation.path
        if (-not $seen.Add($path)) { throw 'Duplicate final operation path.' }
        if ($operation.action -ceq 'Delete') {
            $deleting = $true
            if (-not $deleteEnabled -or -not $retirement.ContainsKey($path)) { throw 'Delete is not an enabled exact frozen retirement.' }
            $row = $retirement[$path]
            $oldGuid = if ($path.EndsWith('.meta')) { $row.guid } else { $null }
            if ($row.kind -cnotin @('Project', 'Recipe', 'AtlasConfig') -or $operation.inputSha256 -cne $row.originalSha256 -or
                $operation.oldGuid -cne $oldGuid -or $operation.retiredGuid -cne $row.guid -or $operation.newGuid -or
                $operation.outputSha256 -or $operation.expectedAbsent -ne $false -or $operation.fields.Count) { throw 'Delete SHA/GUID/kind or exact intent differs.' }
            $null = $seenDeletes.Add($path)
            continue
        }
        if ($deleting) { throw 'All Delete operations must follow Add/Modify.' }
        if ($overlays.ContainsKey($path)) {
            $row = $overlays[$path]
            $action = if ($row.expectedAbsent) { 'Add' } else { 'Modify' }
            $newGuid = if ($path.EndsWith('.meta')) { $row.guid } else { $null }
            $oldGuid = if ($action -ceq 'Modify') { $newGuid } else { $null }
            if ($operation.action -cne $action -or $operation.inputSha256 -cne $row.originalSha256 -or
                $operation.outputSha256 -cne $row.sha256 -or $operation.expectedAbsent -ne $row.expectedAbsent -or
                $operation.oldGuid -cne $oldGuid -or $operation.newGuid -cne $newGuid -or $operation.fields.Count) { throw 'Authenticated Atlas overlay SHA/GUID/intent differs.' }
            $null = $seenOverlays.Add($path)
            continue
        }
        $rewritten = $path -cin $rewritePaths -or ($path.EndsWith('.meta') -and $path.Substring(0, $path.Length - 5) -cin $rewritePaths)
        $allowed = $path -ceq ($Preview.settingsRoot + '.meta') -or $path.StartsWith($Preview.settingsRoot + '/', [StringComparison]::Ordinal) -or
            $path -ceq 'ProjectSettings/EditorBuildSettings.asset' -or $rewritten -or
            ($Preview.useAsDefault -and $path -cin @('Assets/AddressableAssetsData.meta', 'Assets/AddressableAssetsData/DefaultObject.asset', 'Assets/AddressableAssetsData/DefaultObject.asset.meta'))
        if (-not $allowed -or $operation.action -ceq 'Delete' -or
            ($operation.action -ceq 'Modify' -and $path.StartsWith($Preview.settingsRoot + '/', [StringComparison]::Ordinal) -and $path -cne $Preview.settingsPath)) {
            throw "The candidate modifies an unselected/foreign file: $path"
        }
    }
    if ($seenOverlays.Count -ne $overlays.Count -or ($deleteEnabled -and $seenDeletes.Count -ne $retirement.Count)) { throw 'The exact Atlas/retirement batch is incomplete.' }
}

function Assert-YLCandidateProvenance([Collections.IDictionary] $Preview) {
    if (-not $Preview.Contains('supplement') -or -not $Preview.supplement) { return }
    $inventory = Read-YLResourceMigrationInventory -Snapshot $Preview.snapshot -Inventory $Preview.inventory -InventorySha256 $Preview.inventorySha256
    $certificate = Read-YLResourceMigrationSupplement -Repository $Preview.repository -Snapshot $Preview.snapshot -InventoryData $inventory -InventorySha256 $Preview.inventorySha256 -SupplementPath $Preview.supplement.path -SupplementSha256 $Preview.supplement.sha256
    $selected = @($Preview.projects | ForEach-Object { $_.guid })
    $certified = @($certificate.overlays | Where-Object { $_.projectGuid -cin $selected })
    if ($certified.Count -ne @($Preview.overlays).Count) { throw 'Supplement Atlas overlay membership differs.' }
    foreach ($row in $Preview.overlays) {
        $matches = @($certified | Where-Object { $_.path -ceq $row.path })
        if ($matches.Count -ne 1) { throw 'Atlas overlay is not independently authenticated.' }
        foreach ($field in @('sha256', 'guid', 'originalSha256', 'expectedAbsent', 'payload', 'projectGuid')) {
            if ($matches[0][$field] -cne $row[$field]) { throw 'Atlas overlay provenance changed.' }
        }
        $null = Get-YLResourceSupplementFile $Preview.supplement.root $row.payload $row.sha256
    }
    foreach ($pin in $Preview.supplement.pins) { $null = Get-YLResourceSupplementFile $Preview.supplement.root $pin.path $pin.sha256 }
}

function Assert-YLCandidateNativeProof {
    param([Collections.IDictionary] $Proof, [Collections.IDictionary] $Request, [Collections.IDictionary] $Preview)
    if ($Proof.schemaVersion -ne 1 -or $Proof.purpose -cne 'YLResource.CandidateNativeProof' -or
        $Proof.status -cne 'Passed' -or $Proof.runtimeVerified -isnot [bool] -or -not $Proof.runtimeVerified -or
        -not $Proof.cleanupVerified -or -not $Proof.returnedToEditMode -or $Proof.addressablesSource -cne 'Registry') {
        throw 'Native validation did not prove completion, cleanup and return to Edit mode.'
    }
    foreach ($name in @('hostId', 'resultId', 'inputHash', 'previewSha256', 'outputDigest')) {
        if ($Proof[$name] -cne $Request[$name]) { throw "Foreign native proof identity: $name" }
    }
    if ($Proof.unityVersion -cne $Request.expectedUnityVersion -or $Proof.addressablesVersion -cne $Request.expectedAddressablesVersion) { throw 'The actual Unity/AA versions differ.' }
    $checks = [Collections.Generic.HashSet[string]]::new([string[]]$Proof.checks, [StringComparer]::Ordinal)
    if (-not $checks.SetEquals([string[]]@('OfficialContentLoad', 'ConsumerColdRead', 'ForeignSettingsPreserved')) -or $Proof.checks.Count -ne 3) { throw 'The three exact required checks were not proved.' }
    if ($Proof.loaded.Count -ne $Preview.entries.Count -or $Proof.consumers.Count -ne $Preview.consumers.Count) { throw 'The selected subject was not fully exercised.' }
    foreach ($entry in $Preview.entries) {
        $matches = @($Proof.loaded | Where-Object { $_.guid -ceq $entry.guid -and $_.address -ceq $entry.address -and $_.expectedType -ceq $entry.type -and $_.passed -eq $true })
        if ($matches.Count -ne 1) { throw 'A selected official Entry has missing/duplicate load evidence.' }
    }
    foreach ($consumer in $Preview.consumers) {
        $matches = @($Proof.consumers | Where-Object { $_.guid -ceq $consumer.guid -and $_.localId -eq $consumer.localId -and $_.passed -eq $true })
        if ($matches.Count -ne 1) { throw 'A selected consumer has missing/duplicate cold-read evidence.' }
    }
    if ($Request.Contains('retiredCount')) {
        if ($Proof.retired.Count -ne $Request.retiredCount -or $Proof.atlases.Count -ne $Request.atlasCount -or $Proof.projects.Count -ne $Preview.projects.Count) { throw 'Incomplete final project/Atlas/retirement evidence.' }
        if ($Request.retiredCount) { Assert-YLNativeRetirementProof $Proof $Preview.retirement.entries }
        foreach ($atlas in $Proof.atlases) {
            if ($atlas.passed -ne $true -or $atlas.clones.Count -eq 0 -or @($atlas.clones | Where-Object { $_.destroyed -ne $true }).Count -or
                @($Proof.loaded | Where-Object { $_.guid -ceq $atlas.guid -and $_.address -ceq $atlas.address -and $_.contentVerified -eq $true }).Count -ne 1) { throw 'An Atlas has no actual owned-clone/content proof.' }
        }
        if (@($Proof.projects | Where-Object { $_.passed -ne $true -or $_.entries -le 0 }).Count) { throw 'A selected project was not actually covered.' }
    }
    if ($Request.Contains('originalSettingsProof')) {
        Assert-YLTestProcessSucceeded $Request.originalSettingsProof.process
        if (-not $Proof.Contains('foreignSettings') -or $Proof.foreignSettings.existingSettingsVerified -ne $true -or
            $Proof.foreignSettings.originalProofSha256 -cne $Request.originalSettingsProof.sha256) { throw 'An existing Settings receipt has no exact original serialized preservation proof.' }
    }
}

function Test-YLCandidateProofContract {
    $hash = 'a' * 64
    $request = @{ hostId = 'h'; resultId = 'r'; inputHash = $hash; previewSha256 = $hash; outputDigest = $hash; expectedUnityVersion = '6000.3.27f1'; expectedAddressablesVersion = '2.10.3' }
    $preview = @{ entries = @(@{ guid = 'g'; address = 'p/key'; type = 'UnityEngine.TextAsset' }); consumers = @(@{ guid = 'c'; localId = 1 }) }
    $valid = @{
        schemaVersion = 1; purpose = 'YLResource.CandidateNativeProof'; status = 'Passed'; runtimeVerified = $true
        hostId = 'h'; resultId = 'r'; inputHash = $hash; previewSha256 = $hash; outputDigest = $hash
        unityVersion = '6000.3.27f1'; addressablesVersion = '2.10.3'; addressablesSource = 'Registry'
        checks = @('OfficialContentLoad', 'ConsumerColdRead', 'ForeignSettingsPreserved')
        loaded = @(@{ guid = 'g'; address = 'p/key'; expectedType = 'UnityEngine.TextAsset'; passed = $true })
        consumers = @(@{ guid = 'c'; localId = 1; passed = $true }); cleanupVerified = $true; returnedToEditMode = $true
    }
    Assert-YLCandidateNativeProof $valid $request $preview
    $mutations = @(
        { param($p) $p.status = 'Failed' }, { param($p) $p.runtimeVerified = $false },
        { param($p) $p.previewSha256 = 'b' * 64 }, { param($p) $p.outputDigest = 'b' * 64 },
        { param($p) $p.hostId = 'foreign' }, { param($p) $p.inputHash = 'b' * 64 },
        { param($p) $p.unityVersion = '2022.3.62f3' }, { param($p) $p.addressablesSource = 'Embedded' },
        { param($p) $p.checks = @('OfficialContentLoad') }, { param($p) $p.loaded = @() },
        { param($p) $p.consumers[0].passed = $false }, { param($p) $p.cleanupVerified = $false },
        { param($p) $p.returnedToEditMode = $false }
    )
    foreach ($mutation in $mutations) {
        $copy = ConvertFrom-Json (ConvertTo-Json $valid -Depth 20) -AsHashtable
        & $mutation $copy
        $rejected = $false
        try { Assert-YLCandidateNativeProof $copy $request $preview } catch { $rejected = $true }
        if (-not $rejected) { throw 'An incomplete or foreign synthetic proof was accepted.' }
    }
    $scopedPreview = @{ settingsRoot = 'Assets/Official'; settingsPath = 'Assets/Official/Settings.asset'; useAsDefault = $true; rewrites = @() }
    Assert-YLCandidateOperationScope @{ operations = @(@{ path = 'Assets/Official/Settings.asset'; action = 'Add' }) } $scopedPreview
    foreach ($operation in @(@{ path = 'Assets/Foreign.asset'; action = 'Add' }, @{ path = 'Assets/Official/Group.asset'; action = 'Modify' })) {
        $rejected = $false
        try { Assert-YLCandidateOperationScope @{ operations = @($operation) } $scopedPreview } catch { $rejected = $true }
        if (-not $rejected) { throw 'An unselected/foreign operation was accepted.' }
    }
    $finalCases = 0
    if ($CandidateDirectory) {
        $realPreview = Read-YLCandidateJson (Join-Path $CandidateDirectory 'preview.json')
        $final = Read-YLCandidateJson (Join-Path $CandidateDirectory 'migration.json')
        $deletes = @($realPreview.retirement.entries | ForEach-Object {
            [ordered]@{ path = $_.path; action = 'Delete'; expectedAbsent = $false; inputSha256 = $_.originalSha256; outputSha256 = $null; oldGuid = $(if ($_.path.EndsWith('.meta')) { $_.guid } else { $null }); newGuid = $null; retiredGuid = $_.guid; fields = @() }
        })
        $final.operations = @($final.operations) + $deletes
        $final.retirement.deleteEnabled = $true
        Assert-YLCandidateOperationScope $final $realPreview
        $badBatches = @(
            { param($m) $m.operations = @($m.operations | Where-Object { $_.path -cne $m.retirement.entries[0].path }) },
            { param($m) $m.operations[-1].inputSha256 = '0' * 64 },
            { param($m) $m.operations[-1].oldGuid = $null },
            { param($m) $m.operations += @{ path = 'Assets/Foreign.asset'; action = 'Delete'; inputSha256 = '0' * 64; oldGuid = $null; fields = @() } },
            { param($m) $m.retirement.deleteEnabled = $false },
            { param($m) $m.operations = @($m.operations[-1]) + @($m.operations[0..($m.operations.Count - 2)]) },
            { param($m) $m.operations = @($m.operations | Where-Object { $_.path -cne $realPreview.overlays[0].path }) },
            { param($m) ($m.operations | Where-Object { $_.path -ceq $realPreview.overlays[0].path }).outputSha256 = '0' * 64 }
        )
        foreach ($change in $badBatches) {
            $copy = ConvertFrom-Json (ConvertTo-Json $final -Depth 80) -AsHashtable -DateKind String
            & $change $copy
            $rejected = $false
            try { Assert-YLCandidateOperationScope $copy $realPreview } catch { $rejected = $true }
            if (-not $rejected) { throw 'A changed/incomplete final retirement or Atlas batch was accepted.' }
        }
        $finalCases = 1 + $badBatches.Count
    }
    $originalRequest = @{ hostId='original-h';resultId='original-r';inputHash=$hash;previewSha256=$hash;manifestSha256=$hash;outputDigest=$hash;subjectHostId='subject';subjectInputHash=$hash;expectedUnityVersion='6000.3.27f1';expectedAddressablesVersion='2.10.3' }
    $originalProof = @{ schemaVersion=1;purpose='YLResource.OriginalSettingsFootprint';status='Captured';mode='OriginalSettings';runtimeVerified=$false;requestSha256=$hash;unityVersion='6000.3.27f1';addressablesVersion='2.10.3';addressablesSource='Registry';footprint=@{schemaVersion=1;settings=@{identity=@{guid='captured-unit-identity'}};nodes=@(@{});files=@(@{});registration=@{fields=@{}}} }
    foreach ($key in @('hostId','resultId','inputHash','previewSha256','manifestSha256','outputDigest','subjectHostId','subjectInputHash')) { $originalProof[$key]=$originalRequest[$key] }
    Assert-YLOriginalSettingsProof $originalProof $originalRequest $hash
    $badOriginal = @(
        {param($p)$p.status='Passed'}, {param($p)$p.runtimeVerified=$true}, {param($p)$p.inputHash='changed'},
        {param($p)$p.requestSha256='changed'}, {param($p)$p.footprint.nodes=@()}, {param($p)$p.footprint.files=@()},
        {param($p)$null=$p.footprint.registration.Remove('fields')}, {param($p)$p.addressablesVersion='1.25.0'}
    )
    foreach ($change in $badOriginal) {
        $copy=ConvertFrom-Json (ConvertTo-Json $originalProof -Depth 20) -AsHashtable
        & $change $copy
        $rejected=$false
        try {Assert-YLOriginalSettingsProof $copy $originalRequest $hash} catch {$rejected=$true}
        if (-not $rejected) {throw 'An unpinned/incomplete original Settings proof was accepted.'}
    }
    [pscustomobject]@{ purpose = 'YLResource.CandidateProofContractTests'; passed = 5 + $mutations.Count + $finalCases + $badOriginal.Count; runtimeVerified = $false }
}

if ($FunctionsOnly) { return }
if ($SelfTest) { Test-YLCandidateProofContract; return }

$toolRepository = Get-YLTestPath ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')))
$workspace = Initialize-YLTestWorkspace -WorkspaceRoot $WorkspaceRoot -Repository $toolRepository
$repository = if ($Repository) { Get-YLTestPath $Repository } else { $toolRepository }
if ($repository -ine $toolRepository -and (Get-YLTestDirectory $workspace $repository).kind -cne 'Host') { throw 'A separate migration source must be a tool-owned Host.' }
$verifyApplied = -not [string]::IsNullOrWhiteSpace($ApplyReceiptDirectory)
if ($verifyApplied -and ($PreparedRequest -or $PrepareOnly -or $CandidateHost -or -not $ManifestSha256 -or -not $Unity)) {
    throw 'Cold Verify needs an exact manifest pin, Apply receipts and Unity; it cannot resume a candidate overlay request.'
}
$hostLease = $null
$sourceLease = $null
$result = $null
$summary = [ordered]@{ schemaVersion = 1; purpose = $(if ($verifyApplied) { 'YLResource.ColdVerifyRun' } else { 'YLResource.CandidateValidationRun' }); status = 'NotRun'; reason = $null; runtimeVerified = $false }
try {
    if (-not $PreparedRequest) {
        $candidateRoot = Get-YLTestPath $CandidateDirectory
        if ((Get-YLTestDirectory $workspace $candidateRoot).kind -cne 'Result') { throw 'The frozen migration must be an owned result.' }
        $manifestPath = Join-Path $candidateRoot 'migration.json'
        $manifestSha = if ($ManifestSha256) { $ManifestSha256 } else { Get-YLResourceMigrationHash $manifestPath }
        $manifest = Read-YLResourceMigrationManifest -Repository $repository -Manifest $manifestPath -ManifestSha256 $manifestSha
        $previewPath = Join-Path $candidateRoot 'preview.json'
        $preview = Read-YLPinnedResourceMigrationJson $previewPath $manifest.previewSha256 'original Preview'
        if ($preview.schemaVersion -ne 1 -or $preview.purpose -cne 'YLResource.AddressablesMigrationPreview' -or $preview.status -cne 'ReadyForCandidate' -or $preview.blockers.Count) { throw 'An unblocked frozen Preview is required.' }
        Assert-YLCandidateProvenance $preview
        Assert-YLCandidateOperationScope $manifest $preview
        $candidate = Read-YLCandidateJson (Join-Path $candidateRoot 'candidate.json')
        $previewSha = $manifest.previewSha256
        if ($candidate.previewSha256 -cne $previewSha -or $candidate.status -cne 'CandidatePrepared' -or
            $candidate.previewId -cne $preview.previewId -or $manifest.previewId -cne $preview.previewId -or
            $manifest.hostId -cne $candidate.hostId -or $manifest.inputHash -cne $candidate.inputHash -or
            $manifest.unityVersion -cne $candidate.unityVersion -or $manifest.addressablesVersion -cne $candidate.addressablesVersion) { throw 'The frozen candidate identity/output is not intact.' }
        if ($preview.settingsExpectedAbsent -isnot [bool]) { throw 'The Preview must explicitly declare the original Settings state.' }
        if (-not $Unity) { $Unity = (Read-YLCandidateJson (Join-Path $candidateRoot 'summary.json')).process.exe }
        $Unity = Get-YLTestPath $Unity -ReadOnly
        $catalog = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'TestSuites.json')) | ConvertFrom-Json
        $profile = Get-YLTestEditorProfile @($catalog.editors) ([Diagnostics.FileVersionInfo]::GetVersionInfo($Unity))
        if ($profile.version -cne $candidate.unityVersion -or $profile.addressables -cne $candidate.addressablesVersion) { throw 'Use the exact candidate Unity/Addressables profile.' }
        # Keep Mono/UPM paths below Windows MAX_PATH without links or renamed candidate assets.
        $hostRecord = New-YLTestDirectory $workspace Host ('CV-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
        $hostLease = Lock-YLTestDirectory $workspace $hostRecord.path
        $result = New-YLTestDirectory $workspace Result $(if ($verifyApplied) { 'ResourceMigrationColdVerify' } else { 'ResourceCandidateValidation' })
        $files = [Collections.Generic.Dictionary[string, byte[]]]::new([StringComparer]::OrdinalIgnoreCase)
        $operations = @{}
        foreach ($operation in $manifest.operations) { $operations.Add($operation.path, $operation) }
        $retiredPreimages = [Collections.Generic.List[object]]::new()
        $applied = $null
        if ($verifyApplied) {
            $applied = Get-YLResourceMigrationAppliedState -Repository $repository -Manifest $manifest -ReceiptDirectory $ApplyReceiptDirectory -ManifestSha256 $manifestSha
            foreach ($file in $applied.files.GetEnumerator()) { $files.Add($file.Key, $file.Value) }
            foreach ($operation in @($manifest.operations | Where-Object action -CEQ Delete)) {
                $backup = Get-YLResourceMigrationPath $result.path ('RetiredBefore/' + $operation.path)
                $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($backup))
                [IO.File]::WriteAllBytes($backup, $applied.originals[$operation.path])
                $retiredPreimages.Add($operation)
            }
        } else {
            $source = Get-YLTestDirectory -Workspace $workspace -Path $CandidateHost
            if ($source.kind -cne 'Host') { throw 'The source candidate is not an owned host.' }
            $sourceLease = Lock-YLTestDirectory $workspace $source.path
            Assert-YLUnityHostIdle $source
            $oldInputs = Read-YLCandidateJson (Join-Path $source.path '.yl-inputs.json')
            if ($oldInputs.hostId -cne $source.id -or $candidate.hostId -cne $source.id -or $candidate.inputHash -cne $oldInputs.inputHash) { throw 'The candidate host differs from the exact frozen subject.' }
            $unused = Join-Path $result.path 'NotApplied'
            $null = Assert-YLResourceMigrationApplyState -Repository $repository -Manifest $manifest -ReceiptDirectory $unused -ManifestSha256 $manifestSha
            foreach ($input in $oldInputs.files) {
                $path = [string]$input.path
                if ($operations.ContainsKey($path)) {
                    $operation = $operations[$path]
                    if ($operation.action -ceq 'Delete') {
                        if ($input.sha256.ToLowerInvariant() -cne $operation.inputSha256) { throw "The host's retirement input identity differs: $path" }
                        $bytes = Read-VerifiedMigrationBytes $source.path $path $operation.inputSha256 'frozen retirement preimage'
                        $backup = Get-YLResourceMigrationPath $result.path ('RetiredBefore/' + $path)
                        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($backup))
                        [IO.File]::WriteAllBytes($backup, $bytes)
                        $retiredPreimages.Add($operation)
                        continue
                    }
                    $bytes = Read-VerifiedMigrationBytes $candidateRoot ('Output/' + $path) $operation.outputSha256 'candidate output'
                } else {
                    $bytes = Read-VerifiedMigrationBytes $source.path $path $input.sha256.ToLowerInvariant() 'compiler/input file'
                }
                $files.Add($path, $bytes)
            }
            foreach ($operation in $manifest.operations) {
                Assert-YLResourceMigrationFileState -Root (Join-Path $candidateRoot 'Output') -Operation $operation -Output
                if ($operation.action -ceq 'Delete') {
                    if (@($retiredPreimages | Where-Object { $_.path -ceq $operation.path }).Count -ne 1 -or $files.ContainsKey($operation.path)) { throw 'Retirement was not an exact verified preimage removal from the validation overlay.' }
                } elseif (-not $files.ContainsKey($operation.path)) {
                    $files.Add($operation.path, (Read-VerifiedMigrationBytes $candidateRoot ('Output/' + $operation.path) $operation.outputSha256 'candidate output'))
                }
            }
            $sourceLease.Dispose(); $sourceLease = $null
        }
        if ($preview.Contains('supplement') -and $preview.supplement) {
            if (-not $files.ContainsKey('ProjectSettings/EditorSettings.asset') -or
                [Text.Encoding]::UTF8.GetString($files['ProjectSettings/EditorSettings.asset']) -cnotmatch '(?m)^  m_SpritePackerMode: 5\r?$') { throw 'The exact frozen EditorSettings SpriteAtlasV2 input is required; never use a default/rewritten setting.' }
        }
        # The original host/receipt are immutable. Only this fresh host gets the candidate overlay and validator.
        $validator = Join-Path $toolRepository 'Assets/Tests/YLFramework/EditMode/Editor/Resource/ResourceAddressablesCandidateValidation.cs'
        $files.Add('Assets/YLCandidateValidation/Editor/ResourceAddressablesCandidateValidation.cs', [IO.File]::ReadAllBytes($validator))
        $assembly = @{ name = 'YLFramework.ResourceCandidateValidation.Editor'; references = @('YLFramework.Runtime', 'Unity.Addressables', 'Unity.Addressables.Editor', 'Unity.ResourceManager'); includePlatforms = @('Editor'); overrideReferences = $true; precompiledReferences = @('Newtonsoft.Json.dll'); autoReferenced = $true; noEngineReferences = $false }
        $files.Add('Assets/YLCandidateValidation/Editor/YLFramework.ResourceCandidateValidation.Editor.asmdef', [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json $assembly -Depth 10)))
        $identity = Get-YLCandidateFilesIdentity $files
        Sync-YLUnityHost $hostRecord $files ([pscustomobject]@{ sha256 = $identity }) $profile.version
        [IO.File]::Copy($previewPath, (Join-Path $result.path 'preview.json'))
        [IO.File]::Copy($manifestPath, (Join-Path $result.path 'migration.json'))
        $atlasCount = @($preview.entries | Where-Object { $_.type.StartsWith('UnityEngine.U2D.SpriteAtlas,', [StringComparison]::Ordinal) }).Count
        if ($preview.Contains('supplement') -and $preview.supplement) {
            [IO.File]::Copy($preview.supplement.path, (Join-Path $result.path 'recovery-index.json'))
        }
        $baseline = Join-Path $result.path 'Original'
        foreach ($input in $preview.inputs | Where-Object { $_.Contains('official') -and $_.official -eq $true }) {
            $bytes = if ($verifyApplied -and $applied.originals.Contains($input.path)) {
                $applied.originals[$input.path]
            } else { Read-VerifiedMigrationBytes $repository $input.path $input.sha256 'original official configuration' }
            $to = Get-YLResourceMigrationPath $baseline $input.path
            $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($to))
            [IO.File]::WriteAllBytes($to, $bytes)
        }
        $request = @{
            schemaVersion = 1; purpose = 'YLResource.CandidateValidationRequest'; host = $hostRecord.path; hostId = $hostRecord.id
            result = $result.path; resultId = $result.id; inputHash = $identity; previewSha256 = $previewSha
            manifestSha256 = $manifestSha; outputDigest = $manifest.outputDigest
            subjectHostId = $candidate.hostId; subjectInputHash = $candidate.inputHash
            mode = $(if ($verifyApplied) { 'AppliedSource' } else { 'CandidateOverlay' })
            expectedUnityVersion = $profile.version; expectedAddressablesVersion = $profile.addressables; unity = $Unity
            producerSha256 = Get-YLResourceMigrationHash $PSCommandPath
            retiredCount = $retiredPreimages.Count; atlasCount = $atlasCount
            recoveryIndexSha256 = $(if ($preview.Contains('supplement') -and $preview.supplement) { $preview.supplement.sha256 } else { $null })
        }
        if ($verifyApplied) { $request['appliedRepository'] = $repository; $request['applyReceipts'] = $ApplyReceiptDirectory }
        if (-not $preview.settingsExpectedAbsent) {
            # Build a separate original graph. Never rewrite the converted subject, its input
            # receipt, or the already staged target. Original bytes must still match their pins.
            $originalFiles = [Collections.Generic.Dictionary[string, byte[]]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($file in $files.GetEnumerator()) { $originalFiles.Add($file.Key, $file.Value) }
            foreach ($operation in $manifest.operations) {
                if ($operation.action -ceq 'Add') { $null = $originalFiles.Remove($operation.path); continue }
                $originalFiles[$operation.path] = if ($verifyApplied) {
                    $bytes = $applied.originals[$operation.path]
                    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() -cne $operation.inputSha256) { throw 'An original applied Settings/compiler preimage changed.' }
                    $bytes
                } else { Read-VerifiedMigrationBytes $repository $operation.path $operation.inputSha256 'original Settings/compiler graph' }
            }
            $originalHost = New-YLTestDirectory $workspace Host ('OS-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
            $originalResult = New-YLTestDirectory $workspace Result 'ResourceOriginalSettings'
            $originalLease = Lock-YLTestDirectory $workspace $originalHost.path
            try {
                $originalIdentity = Get-YLCandidateFilesIdentity $originalFiles
                Sync-YLUnityHost $originalHost $originalFiles ([pscustomobject]@{ sha256 = $originalIdentity }) $profile.version
                [IO.File]::Copy($previewPath, (Join-Path $originalResult.path 'preview.json'))
                [IO.File]::Copy($manifestPath, (Join-Path $originalResult.path 'migration.json'))
                $originalRequest = ConvertFrom-Json (ConvertTo-Json $request -Depth 20) -AsHashtable -DateKind String
                $originalRequest.host = $originalHost.path; $originalRequest.hostId = $originalHost.id
                $originalRequest.result = $originalResult.path; $originalRequest.resultId = $originalResult.id
                $originalRequest.inputHash = $originalIdentity; $originalRequest.mode = 'OriginalSettings'
                $originalPath = Join-Path $originalResult.path 'request.json'
                Write-YLTestJson $originalPath $originalRequest
                $request['originalSettings'] = @{ request = $originalPath; requestSha256 = Get-YLResourceMigrationHash $originalPath; host = $originalHost.path; hostId = $originalHost.id; inputHash = $originalIdentity; result = $originalResult.path; resultId = $originalResult.id }
            } finally { $originalLease.Dispose() }
        }
        $PreparedRequest = Join-Path $result.path 'request.json'
        Write-YLTestJson $PreparedRequest $request
    } else {
        $PreparedRequest = Get-YLTestPath $PreparedRequest
        $request = Read-YLCandidateJson $PreparedRequest
        $hostRecord = Get-YLTestDirectory $workspace $request.host
        $result = Get-YLTestDirectory $workspace $request.result
        if ($request.mode -cne 'CandidateOverlay' -or $request.hostId -cne $hostRecord.id -or $request.resultId -cne $result.id -or $request.producerSha256 -cne (Get-YLResourceMigrationHash $PSCommandPath)) { throw 'The prepared candidate validation request/producer changed.' }
        $hostLease = Lock-YLTestDirectory $workspace $hostRecord.path
        $preview = Read-YLPinnedResourceMigrationJson (Join-Path $result.path 'preview.json') $request.previewSha256 'original Preview'
        $manifest = Read-YLResourceMigrationManifest -Repository $repository -Manifest (Join-Path $result.path 'migration.json') -ManifestSha256 $request.manifestSha256
    }
    $summary.host = $hostRecord.path
    $summary.result = $result.path
    $summary.request = $PreparedRequest
    if ($PrepareOnly) { $summary.status = 'Prepared'; return }
    if ($request.Contains('originalSettings')) {
        $original = $request.originalSettings
        $originalHost = Get-YLTestDirectory $workspace $original.host
        $originalResult = Get-YLTestDirectory $workspace $original.result
        if ($originalHost.id -cne $original.hostId -or $originalResult.id -cne $original.resultId) { throw 'Original Settings host/result ownership changed.' }
        $originalRequest = Read-YLPinnedResourceMigrationJson $original.request $original.requestSha256 'original Settings request'
        if ($originalRequest.mode -cne 'OriginalSettings' -or $originalRequest.hostId -cne $original.hostId -or $originalRequest.inputHash -cne $original.inputHash) { throw 'Original Settings input identity changed.' }
        $originalLease = Lock-YLTestDirectory $workspace $originalHost.path
        try {
            Assert-YLUnityHostIdle $originalHost
            $originalProcess = Invoke-YLCandidateEditorProcess $originalRequest $original.request 'CaptureOriginalSettings'
            $summary.originalSettingsProcess = $originalProcess
            Assert-YLTestProcessSucceeded $originalProcess
            $originalProofPath = Join-Path $original.result 'original-settings.json'
            $originalProof = Read-YLCandidateJson $originalProofPath
            Assert-YLOriginalSettingsProof $originalProof $originalRequest $original.requestSha256
            $request['originalSettingsProof'] = @{ path = $originalProofPath; sha256 = Get-YLResourceMigrationHash $originalProofPath; hostId = $original.hostId; resultId = $original.resultId; inputHash = $original.inputHash; requestSha256 = $original.requestSha256; process = $originalProcess }
            # The prepared request is immutable; link actual capture evidence in a new execution request.
            $PreparedRequest = Join-Path $result.path 'execution-request.json'
            Write-YLTestJson $PreparedRequest $request
            $summary.request = $PreparedRequest
        } finally { $originalLease.Dispose() }
    }
    Assert-YLUnityHostIdle $hostRecord
    $process = Invoke-YLCandidateEditorProcess $request $PreparedRequest 'Execute'
    $summary.process = $process
    Assert-YLTestProcessSucceeded $process
    $proofPath = Join-Path $result.path 'native-proof.json'
    $proof = Read-YLCandidateJson $proofPath
    Assert-YLCandidateOperationScope $manifest $preview
    $finalRetirement = $manifest['retirement'] -and $manifest.retirement.deleteEnabled
    $conversionOnly = $manifest['retirement'] -and $manifest.retirement.entries.Count -gt 0 -and -not $finalRetirement
    if ($request.retiredCount -ne @($manifest.operations | Where-Object action -CEQ Delete).Count) {
        throw 'The native request does not target the exact frozen retirement membership.'
    }
    Assert-YLCandidateNativeProof $proof $request $preview
    foreach ($key in @('mode', 'manifestSha256', 'subjectHostId', 'subjectInputHash')) {
        if ($proof[$key] -cne $request[$key]) { throw "Native validation evidence has a foreign $key." }
    }
    if ($verifyApplied) {
        $rechecked = Get-YLResourceMigrationAppliedState -Repository $repository -Manifest $manifest -ReceiptDirectory $ApplyReceiptDirectory -ManifestSha256 $manifestSha
        if ($applied.batchReceipt.path -cne $rechecked.batchReceipt.path -or $applied.batchReceipt.sha256 -cne $rechecked.batchReceipt.sha256) {
            throw 'The verified Apply batch receipt changed while the cold host was running.'
        }
        if ($finalRetirement -and $applied.retirementReferences.sha256 -cne $rechecked.retirementReferences.sha256) {
            throw 'Retirement reference inputs changed while the cold host was running.'
        }
    }
    $validationStatus = if ($conversionOnly) { 'ConversionValidated' } else { 'Passed' }
    $receipt = @{
        schemaVersion = 1; purpose = $(if ($verifyApplied) { 'YLResource.AddressablesMigrationColdVerify' } else { 'YLResource.AddressablesMigrationValidation' }); status = $validationStatus; runtimeVerified = $true
        previewSha256 = $manifest.previewSha256; outputDigest = $manifest.outputDigest
        # These two schema fields bind the candidate subject, not the separate execution host.
        hostId = $request.subjectHostId; inputHash = $request.subjectInputHash
        unityVersion = $proof.unityVersion; addressablesVersion = $proof.addressablesVersion
        checks = $proof.checks; process = $process
        validationHost = $hostRecord.path; validationHostId = $hostRecord.id; validationResultId = $request.resultId; validationInputHash = $request.inputHash
        nativeProof = $proofPath; nativeProofSha256 = Get-YLResourceMigrationHash $proofPath
        manifestSha256 = $request.manifestSha256; producerSha256 = $request.producerSha256; mode = $request.mode
    }
    $receiptPath = Join-Path $result.path 'receipt.json'
    if ($verifyApplied) {
        $receipt['appliedRepository'] = $repository; $receipt['applyReceipts'] = $ApplyReceiptDirectory; $receipt['applyBatchReceipt'] = $rechecked.batchReceipt
        if ($finalRetirement) { $receipt['retirementReferences'] = $rechecked.retirementReferences }
    }
    if ($request.Contains('originalSettingsProof')) { $receipt['originalSettingsProof'] = $request.originalSettingsProof }
    Write-YLTestJson $receiptPath $receipt
    if (-not $verifyApplied -and -not $conversionOnly) {
        Assert-YLResourceMigrationValidation -Manifest $manifest -ManifestSha256 $request.manifestSha256 -ValidationReceipt $receiptPath -ValidationSha256 (Get-YLResourceMigrationHash $receiptPath)
    }
    $summary.status = $validationStatus; $summary.runtimeVerified = $true; $summary.receipt = $receiptPath
} catch {
    $summary.status = 'Failed'; $summary.reason = $_.Exception.Message
    throw
} finally {
    if ($hostLease) { $hostLease.Dispose() }
    if ($sourceLease) { $sourceLease.Dispose() }
    if ($result -and -not $PrepareOnly) { Write-YLTestJson (Join-Path $result.path 'summary.json') $summary }
    $summary | ConvertTo-Json -Depth 12
}
