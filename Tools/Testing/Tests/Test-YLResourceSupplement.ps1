#requires -Version 7.6
[CmdletBinding()]
param([Parameter(Mandatory)][string] $TemporaryRoot)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../ResourceMigrationHost.ps1')
$TemporaryRoot = Get-YLTestPath $TemporaryRoot
if (-not [IO.Directory]::Exists($TemporaryRoot)) { throw 'TemporaryRoot must exist.' }
$sandbox = Join-Path $TemporaryRoot ('yl-supplement-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($sandbox)
$script:cases = 0
$failures = [Collections.Generic.List[string]]::new()
function Assert-That([bool] $Value, [string] $Message) { if (-not $Value) { throw $Message } }
function Assert-Rejected([scriptblock] $Body, [string] $Pattern) {
    try { $null = & $Body } catch { if ($_.Exception -is [Management.Automation.CommandNotFoundException] -or $_.Exception.Message -notmatch $Pattern) { throw }; return }
    throw "Expected rejection: $Pattern"
}
function Test-Case([string] $Name, [scriptblock] $Body) {
    $script:cases++
    try { & $Body; "PASS: $Name" } catch { $failures.Add("${Name}: $($_.Exception.Message)`n$($_.ScriptStackTrace)"); "FAIL: $($failures[$failures.Count - 1])" }
}
function Save-Json([string] $Path, [object] $Value) { [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 60)) }
function New-SupplementFixture {
    $repo = Join-Path $sandbox ([Guid]::NewGuid().ToString('N'))
    $null = [IO.Directory]::CreateDirectory((Join-Path $repo 'Assets/Catalog/Atlases'))
    $specs = @{
        'Assets/Catalog/entry.bytes' = @('1', 'original entry'); 'Assets/Catalog/Project.asset' = @('3', 'original project')
        'Assets/Catalog/Config.asset' = @('4', 'original configuration'); 'Assets/Catalog/Atlases/old.spriteatlasv2' = @('5', 'original atlas')
        'Assets/Catalog/Negative.asset' = @('7', 'intentionally invalid'); 'Assets/Catalog/Recipe.asset' = @('8', 'original recipe')
        'Assets/Catalog/AtlasProject.asset' = @('9', 'original atlas project'); 'Assets/Catalog/other.bytes' = @('a', 'original other')
        'Assets/Catalog/source.png' = @('b', 'source sprite bytes')
    }
    foreach ($path in $specs.Keys) {
        [IO.File]::WriteAllText((Join-Path $repo $path), $specs[$path][1])
        [IO.File]::WriteAllText((Join-Path $repo ($path + '.meta')), "guid: $($specs[$path][0] * 32)`n")
    }
    [IO.File]::WriteAllText((Join-Path $repo 'Assets/Catalog.meta'), "guid: $('2' * 32)`nfolderAsset: yes`n")
    [IO.File]::WriteAllText((Join-Path $repo 'Assets/Catalog/Atlases.meta'), "guid: $('c' * 32)`nfolderAsset: yes`n")
    $snapshot = Join-Path $repo 'LocalArchives/ResourceLegacy/test'
    $snap = New-YLResourceSnapshot -Repository $repo -Destination $snapshot -InputPaths @('Assets/Catalog') -SourceCommit ('d' * 40)
    $archive = @{}
    foreach ($row in $snap.files) { $archive[$row.path] = $row.sha256 }
    $enumProperties = @{ compression = @{ propertyType = 'Enum'; type = 'ResourceCompression'; value = 0 }; contentEncryption = @{ propertyType = 'Enum'; type = 'ResourceContentEncryption'; value = 0 } }
    $base = @{ guid = ('3' * 32); localId = 11400000; assetPath = 'Assets/Catalog/Project.asset'; type = 'YLFramework.Editor.Resource.ResourceProject, YLFramework.Editor'; projectId = 'base'; status = 'Resolved'; properties = $enumProperties.Clone(); fingerprintPaths = @('Assets/Catalog/Project.asset', 'Assets/Catalog/entry.bytes') }
    $base.assets = @(@{ path = 'Assets/Catalog/entry.bytes'; guid = ('1' * 32); localId = 4900000; type = 'UnityEngine.TextAsset, UnityEngine.CoreModule'; address = 'base/data/entry'; legacyPath = 'data/entry'; aliases = @(); group = 'main'; packing = 'Together'; selectionGuid = ('1' * 32); selectionPath = 'Assets/Catalog/entry.bytes'; subAssets = @() })
    $atlas = @{ guid = ('9' * 32); localId = 11400000; assetPath = 'Assets/Catalog/AtlasProject.asset'; type = $base.type; projectId = 'atlas'; status = 'RejectedLegacyDefinition'; error = 'Automatic Atlases require generation/update before dependency analysis.'; assets = @(); properties = $enumProperties.Clone() }
    $atlas.properties.automaticAtlases = @{ propertyType = 'ObjectReference'; value = @{ guid = ('4' * 32); localId = 11400000; assetPath = 'Assets/Catalog/Config.asset'; isNull = $false } }
    $negative = @{ guid = ('7' * 32); localId = 11400000; assetPath = 'Assets/Catalog/Negative.asset'; type = $base.type; projectId = 'negative'; status = 'RejectedLegacyDefinition'; error = 'Group has no valid assets.'; assets = @(); properties = $enumProperties.Clone() }
    $config = @{ guid = ('4' * 32); localId = 11400000; assetPath = 'Assets/Catalog/Config.asset'; type = 'YLFramework.Editor.Resource.ResourceAtlasConfiguration, YLFramework.Editor'; properties = @{} }
    $recipe = @{ guid = ('8' * 32); localId = 11400000; assetPath = 'Assets/Catalog/Recipe.asset'; type = 'YLFramework.Editor.Resource.ResourceBuildRecipe, YLFramework.Editor'; properties = @{ project = @{ propertyType = 'ObjectReference'; value = @{ guid = ('9' * 32); localId = 11400000; assetPath = $atlas.assetPath } } } }
    $objects = @($base, $atlas, $negative, $config, $recipe)
    $assets = @($specs.Keys | ForEach-Object { $p = $_; @{ path = $p; guid = ($specs[$p][0] * 32); sha256 = $archive[$p]; dependencies = @($p); objects = @($objects | Where-Object { $_.assetPath -ceq $p }) } })
    $inv = @{ schemaVersion = 1; purpose = 'YLResource.LegacyInventory'; hostId = ('e' * 32); inputHash = ('f' * 64); snapshotManifestSha256 = (Get-YLResourceMigrationHash (Join-Path $snapshot 'snapshot.json')); unityVersion = '6000.3.27f1'; assets = $assets; projects = @($base, $atlas, $negative) }
    $invDir = Join-Path $snapshot 'Inventory/fixture'
    $null = [IO.Directory]::CreateDirectory($invDir)
    $invPath = Join-Path $invDir 'inventory.json'
    Write-YLTestJson -Path $invPath -Value $inv
    $invSha = Get-YLResourceMigrationHash $invPath
    Write-YLTestJson -Path (Join-Path $invDir 'audit.json') -Value @{ purpose = 'YLResource.LegacyInventoryAudit'; status = 'Verified'; sourceGuidsPreserved = $true; inventorySha256 = $invSha; assets = $assets.Count; objects = $objects.Count; projects = @($inv.projects | ForEach-Object { @{ path = $_.assetPath; projectId = $_.projectId; status = $_.status; assets = $_.assets.Count } }) }
    $workspace = Initialize-YLTestWorkspace -WorkspaceRoot (Join-Path $sandbox ('evidence-' + [Guid]::NewGuid().ToString('N'))) -Repository $repo
    $hostRecord = New-YLTestDirectory -Workspace $workspace -Kind Host -Key 'OldAtlasFixture'
    $run = New-YLTestDirectory -Workspace $workspace -Kind Result -Key 'ResourceLegacyAtlasGenerate'
    $root = $run.path
    $inputHash = 'e' * 64
    Write-YLTestJson -Path (Join-Path $hostRecord.path '.yl-inputs.json') -Value @{ hostId = $hostRecord.id; inputHash = $inputHash }
    $stageFiles = @($snap.files | ForEach-Object {
        $saved = Join-Path $root ('Original/' + $_.path); $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($saved)); [IO.File]::Copy((Join-Path $repo $_.path), $saved)
        @{ path = $_.path; originalSha256 = $_.sha256; stagedSha256 = $_.sha256; overlay = $false }
    })
    $derivedAssets = @()
    $changes = @()
    $finalFiles = @($snap.files | ForEach-Object { @{ path = $_.path; finalSha256 = $_.sha256 } })
    foreach ($spec in @(@('old', '5'), @('new', '6'))) {
        $path = 'Assets/Catalog/Atlases/' + $spec[0] + '.spriteatlasv2'
        $binding = @{ path = $path; guid = ($spec[1] * 32); canonical = ('atlas/atlases/' + $spec[0]); group = 'atlases'; spriteCount = 1; sourceSpriteCount = 1; sprites = @(@{ name = 'sprite'; sourcePath = 'Assets/Catalog/source.png'; sourceGuid = ('b' * 32); sourceLocalId = 21300000; lookup = 'SpriteAtlas.GetSprite(exactSourceName)'; found = $true; rectWidth = 4; rectHeight = 4; isPersistentAtlasSubassetIdentity = $false }) }
        foreach ($relative in @($path, ($path + '.meta'))) {
            $saved = Join-Path $root ('GeneratedAfterGeneration/' + $relative); $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($saved))
            [IO.File]::WriteAllText($saved, $(if ($relative.EndsWith('.meta')) { "fileFormatVersion: 2`nguid: $($spec[1] * 32)`n" } else { 'generated atlas ' + $spec[0] }))
            $sha = Get-YLResourceMigrationHash $saved
            $changes += @{ path = $relative; action = $(if ($archive.ContainsKey($relative)) { 'Modify' } else { 'Add' }); originalSourceSha256 = $archive[$relative]; beforePhaseSha256 = $archive[$relative]; afterPhaseSha256 = $sha; phase = 'GeneratedAfterGeneration'; preservedAfterBytes = ('GeneratedAfterGeneration/' + $relative) }
            $finalSaved = Join-Path $root ('FinalAfterProcess/' + $relative); $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($finalSaved)); [IO.File]::Copy($saved, $finalSaved)
            $finalFiles = @($finalFiles | Where-Object { $_.path -cne $relative }) + @{ path = $relative; finalSha256 = $sha }
        }
        $derivedAssets += @{ path = $path; guid = ($spec[1] * 32); localId = 100100200; type = 'UnityEngine.U2D.SpriteAtlas, UnityEngine.CoreModule'; canonical = $binding.canonical; legacyPath = ('atlases/' + $spec[0]); aliases = @(); group = 'atlases'; packing = 'Together'; selectionGuid = ($spec[1] * 32); selectionPath = $path; isGeneratedAtlas = $true; sourceOriginalSha256 = $archive[$path]; sourceOriginalMetaSha256 = $archive[$path + '.meta']; expectedAbsentInOriginalSnapshot = -not $archive.ContainsKey($path); originalGuid = $(if ($spec[0] -ceq 'old') { '5' * 32 } else { $null }); originalGuidPreserved = $(if ($spec[0] -ceq 'old') { $true } else { $null }); derivedSha256 = (Get-YLResourceMigrationHash (Join-Path $root ('GeneratedAfterGeneration/' + $path))); derivedMetaSha256 = (Get-YLResourceMigrationHash (Join-Path $root ('GeneratedAfterGeneration/' + $path + '.meta'))); publicSubassets = @(); oldEditorAtlasBinding = $binding }
    }
    $ordinary = @{ path = 'Assets/Catalog/other.bytes'; guid = ('a' * 32); localId = 4900000; type = $base.assets[0].type; canonical = 'atlas/data/other'; legacyPath = 'data/other'; aliases = @('atlas/old-other'); group = 'main'; packing = 'Together'; selectionGuid = ('a' * 32); selectionPath = 'Assets/Catalog/other.bytes'; isGeneratedAtlas = $false; sourceOriginalSha256 = $archive['Assets/Catalog/other.bytes']; sourceOriginalMetaSha256 = $archive['Assets/Catalog/other.bytes.meta']; expectedAbsentInOriginalSnapshot = $false; originalGuid = ('a' * 32); originalGuidPreserved = $true; derivedSha256 = $archive['Assets/Catalog/other.bytes']; derivedMetaSha256 = $archive['Assets/Catalog/other.bytes.meta']; publicSubassets = @(); oldEditorAtlasBinding = $null }
    $derivedAssets += $ordinary
    $selections = @(@{ group = 'main'; packing = 'Together'; selectionGuid = ('a' * 32); selectionPath = $ordinary.path; expanded = @(@{ path = $ordinary.path; guid = $ordinary.guid; localId = $ordinary.localId; canonical = $ordinary.canonical; aliases = $ordinary.aliases }) })
    $plan = @{ directory = 'Assets/Catalog/Atlases'; needsGeneration = $false; hasStaleOutputs = $false; changes = @($derivedAssets | Where-Object { $_.isGeneratedAtlas } | ForEach-Object { @{ kind = 'Current'; ruleId = $_.legacyPath; key = $_.legacyPath; path = $_.path; guid = $_.guid; group = $_.group; address = $_.legacyPath; inputHash = ('d' * 64); sources = @('Assets/Catalog/source.png') } }) }
    $sourceFact = @{ projectGuid = $atlas.guid; projectId = $atlas.projectId; projectPath = $atlas.assetPath; projectOriginal = @{ path = $atlas.assetPath; originalSha256 = $archive[$atlas.assetPath] }; projectMeta = @{ path = ($atlas.assetPath + '.meta'); originalSha256 = $archive[$atlas.assetPath + '.meta'] }; configurationGuid = $config.guid; configurationPath = $config.assetPath; configurationOriginal = @{ path = $config.assetPath; originalSha256 = $archive[$config.assetPath] }; configurationMeta = @{ path = ($config.assetPath + '.meta'); originalSha256 = $archive[$config.assetPath + '.meta'] }; rules = @(); sourceDirectories = @(); originalOutputs = @() }
    $frozen = @(@{ guid = $negative.guid; path = $negative.assetPath; status = $negative.status; reason = $negative.error; original = @{ path = $negative.assetPath; originalSha256 = $archive[$negative.assetPath] }; originalMeta = @{ path = ($negative.assetPath + '.meta'); originalSha256 = $archive[$negative.assetPath + '.meta'] }; policy = 'Frozen negative retirement evidence; never repaired or generated.' })
    $projectIndex = @{ projectGuid = $atlas.guid; projectId = $atlas.projectId; projectPath = $atlas.assetPath; sourceProjectSha256 = $archive[$atlas.assetPath]; sourceProjectMetaSha256 = $archive[$atlas.assetPath + '.meta']; configurationPath = $config.assetPath; configurationGuid = $config.guid; sourceConfigurationSha256 = $archive[$config.assetPath]; derivedConfigurationSha256 = $archive[$config.assetPath]; ownershipVerifiedByArchivedPreview = $true; originalRules = @(); sourceDirectories = @(); nonAtlasSelections = $selections; executedPlan = $plan; currentPlan = $plan; assets = $derivedAssets }
    $nativeAssets = @($derivedAssets | ForEach-Object { @{ path = $_.path; guid = $_.guid; localId = $_.localId; type = $_.type; address = $_.canonical; legacyPath = $_.legacyPath; aliases = $_.aliases; group = $_.group; packing = $_.packing; selectionGuid = $_.selectionGuid; selectionPath = $_.selectionPath; subAssets = @() } })
    $nativeProject = @{ projectGuid = $atlas.guid; projectId = $atlas.projectId; projectPath = $atlas.assetPath; status = 'GeneratedAndResolved'; beforePlan = $plan; executedPlan = $plan; afterPlan = $plan; nonAtlasSelections = $selections; derivedProject = @{ guid = $atlas.guid; localId = $atlas.localId; assetPath = $atlas.assetPath; projectId = $atlas.projectId; status = 'Resolved'; assets = $nativeAssets; fingerprintPaths = @($atlas.assetPath, $ordinary.path) }; oldEditorAtlasBindings = @($derivedAssets | Where-Object { $_.isGeneratedAtlas } | ForEach-Object { $_.oldEditorAtlasBinding }); publicSubassets = @() }
    Write-YLTestJson -Path (Join-Path $root 'stage-manifest.json') -Value @{ schemaVersion = 1; purpose = 'YLResource.LegacyAtlasStage'; files = $stageFiles; originalSnapshotManifestSha256 = $inv.snapshotManifestSha256; inputHash = $inputHash }
    Write-YLTestJson -Path (Join-Path $root 'original-facts.json') -Value @{ schemaVersion = 1; purpose = 'YLResource.LegacyAtlasOriginalFacts'; snapshotManifestSha256 = $inv.snapshotManifestSha256; inventorySha256 = $invSha; projects = @($sourceFact); frozenNegatives = $frozen; explicitSpritePackerMode = 5 }
    Write-YLTestJson -Path (Join-Path $root 'atlas-evidence.json') -Value @{ schemaVersion = 1; purpose = 'YLResource.LegacyAtlasDerivedEvidence'; status = 'GeneratedAndResolved'; applied = $false; officialRuntimeVerified = $false; hostId = $hostRecord.id; inputHash = $inputHash; snapshotManifestSha256 = $inv.snapshotManifestSha256; inventorySha256 = $invSha; unityVersion = '6000.3.27f1'; spritePackerModeValue = 5; activeBuildTarget = 'StandaloneWindows64'; projects = @($nativeProject); frozenNegatives = @($negative); importChanges = @(); generationChanges = $changes }
    $finalChanges = @($changes | ForEach-Object { @{ path = $_.path; action = $_.action; originalSourceSha256 = $_.originalSourceSha256; stagedSha256 = $_.beforePhaseSha256; finalSha256 = $_.afterPhaseSha256 } })
    Write-YLTestJson -Path (Join-Path $root 'process-files.json') -Value @{ schemaVersion = 1; purpose = 'YLResource.LegacyAtlasProcessFiles'; applied = $false; finalFiles = $finalFiles; changes = $finalChanges }
    $process = @{ purpose = 'YLTesting.Process'; schemaVersion = 1; status = 'Exited'; observedExit = $true; exitCode = 0; timedOut = $false; cancelled = $false; forcedCleanup = $false; observerError = $null; cleanupError = $null; streamsDrained = $true; jobAssigned = $true; activeProcesses = 0; invocationId = ('f' * 32) }
    Write-YLTestJson -Path (Join-Path $root 'process.log.exit.json') -Value $process
    Write-YLTestJson -Path (Join-Path $root 'summary.json') -Value @{ schemaVersion = 1; purpose = 'YLResource.LegacyAtlasEvidenceRun'; status = 'GeneratedAndResolved'; action = 'Generate'; result = $root; host = $hostRecord.path; hostId = $hostRecord.id; process = $process; originalArchiveUnchanged = $true; applied = $false; snapshotManifestSha256 = $inv.snapshotManifestSha256 }
    Write-YLTestJson -Path (Join-Path $root 'request.json') -Value @{ schemaVersion = 1; purpose = 'YLResource.LegacyAtlasEvidence'; action = 'Generate'; output = $root; sourceRepository = $repo; workspaceId = $workspace.id; hostId = $hostRecord.id; resultId = $run.id; inputHash = $inputHash; projectGuids = @($atlas.guid); negativeGuids = @($negative.guid); expectedUnityVersion = '6000.3.27f1'; expectedSpritePackerMode = 5; snapshotManifestSha256 = $inv.snapshotManifestSha256; inventorySha256 = $invSha; stageManifestSha256 = (Get-YLResourceMigrationHash (Join-Path $root 'stage-manifest.json')) }
    $index = @{ schemaVersion = 1; purpose = 'YLResource.LegacyAtlasRecoveryIndex'; status = 'DerivedFourAtlasProjectsResolved'; applied = $false; officialRuntimeVerified = $false; snapshotManifestSha256 = $inv.snapshotManifestSha256; inventorySha256 = $invSha; nativeInputHash = $inputHash; exactToolInputsStillMatch = $true; projects = @($projectIndex); frozenNegatives = $frozen; evidenceSha256 = (Get-YLResourceMigrationHash (Join-Path $root 'atlas-evidence.json')); stageManifestSha256 = (Get-YLResourceMigrationHash (Join-Path $root 'stage-manifest.json')); processFilesSha256 = (Get-YLResourceMigrationHash (Join-Path $root 'process-files.json')) }
    $indexPath = Join-Path $root 'recovery-index.json'
    Write-YLTestJson -Path $indexPath -Value $index
    return @{ repo = $repo; snapshot = $snapshot; inventory = $inv; root = $root; index = $index; indexPath = $indexPath; arguments = @{ Repository = $repo; Snapshot = $snapshot; Inventory = $invPath; InventorySha256 = $invSha; ProjectGuids = @($base.guid, $atlas.guid); SettingsPath = 'Assets/AddressableAssetsData/AddressableAssetSettings.asset'; AllowedRoots = @('Assets/Catalog'); SupplementPath = $indexPath; SupplementSha256 = (Get-YLResourceMigrationHash $indexPath); RetireProjectGuids = @($negative.guid) } }
}

try {
    Test-Case 'Supplement retains original inventory and creates source-pinned overlay plus frozen retirement' {
        $f = New-SupplementFixture; $arguments = $f.arguments
        $before = Get-YLResourceMigrationHash $arguments.Inventory
        $preview = New-YLResourceMigrationPreview @arguments
        Assert-That ($preview.status -ceq 'ReadyForCandidate' -and $preview.projects.Count -eq 2 -and $preview.entries.Count -eq 4) 'A recovered definition was counted without authenticated materialization.'
        Assert-That ($preview.overlays.Count -eq 4 -and @($preview.overlays | Where-Object { $_.expectedAbsent }).Count -eq 2) 'One new and one existing Atlas pair must yield exact Add/Modify overlays.'
        Assert-That ($preview.retirement.entries.Count -eq 10 -and $preview.retirement.negativeProjectGuids.Count -eq 1 -and $preview.retirement.deleteEnabled -eq $false) 'Project/Recipe/config/negative retirement was not explicit and frozen.'
        Assert-That ((Get-YLResourceMigrationHash $arguments.Inventory) -ceq $before -and $preview.projects[1].status -ceq 'RejectedLegacyDefinition') 'Derived status replaced the original T0 definition.'
        Assert-That (-not [IO.File]::Exists((Join-Path $f.repo 'Assets/Catalog/Atlases/new.spriteatlasv2'))) 'Preview applied a derived Atlas to source.'
    }
    Test-Case 'Supplement rejects wrong index SHA and evidence owner before using payloads' {
        $f = New-SupplementFixture; $arguments = $f.arguments; $arguments.SupplementSha256 = '0' * 64
        Assert-Rejected { New-YLResourceMigrationPreview @arguments } 'Supplement.*SHA|Supplement.*hash'
        $arguments.SupplementSha256 = Get-YLResourceMigrationHash $f.indexPath
        $ownerPath = Join-Path $f.root '.yl-owner.json'; $owner = [IO.File]::ReadAllText($ownerPath) | ConvertFrom-Json -AsHashtable; $owner.workspaceId = '0' * 32; Save-Json $ownerPath $owner
        Assert-Rejected { New-YLResourceMigrationPreview @arguments } 'owner|identity|workspace'
    }
    Test-Case 'Supplement refuses changed generated bytes and cannot read a traversal payload' {
        $f = New-SupplementFixture; $arguments = $f.arguments
        [IO.File]::WriteAllText((Join-Path $f.root 'GeneratedAfterGeneration/Assets/Catalog/Atlases/new.spriteatlasv2'), 'unknown output edit')
        Assert-Rejected { New-YLResourceMigrationPreview @arguments } 'SHA|hash|payload'
        $f = New-SupplementFixture; $arguments = $f.arguments
        $evidencePath = Join-Path $f.root 'atlas-evidence.json'; $data = [IO.File]::ReadAllText($evidencePath) | ConvertFrom-Json -AsHashtable; $data.generationChanges[0].preservedAfterBytes = '../outside'; Save-Json $evidencePath $data
        $f.index.evidenceSha256 = Get-YLResourceMigrationHash $evidencePath; Save-Json $f.indexPath $f.index; $arguments.SupplementSha256 = Get-YLResourceMigrationHash $f.indexPath
        Assert-Rejected { New-YLResourceMigrationPreview @arguments } 'path|payload|travers|relative|phase'
    }
    Test-Case 'Supplement cross-checks non-Atlas canonical and never turns negative selections into successes' {
        $f = New-SupplementFixture; $arguments = $f.arguments
        $f.index.projects[0].assets[2].canonical = 'atlas/guessed'; Save-Json $f.indexPath $f.index; $arguments.SupplementSha256 = Get-YLResourceMigrationHash $f.indexPath
        Assert-Rejected { New-YLResourceMigrationPreview @arguments } 'canonical|projection|asset|definition'
        $f = New-SupplementFixture; $arguments = $f.arguments; $arguments.ProjectGuids += ('7' * 32)
        $preview = New-YLResourceMigrationPreview @arguments
        Assert-That ($preview.status -ceq 'Blocked' -and $preview.entries.Count -eq 4 -and $preview.blockers.Count -eq 1) 'A frozen negative definition became an empty successful conversion.'
    }
    Test-Case 'Candidate projection preserves Source SHA and packages only exact Atlas pairs as Add/Modify' {
        $f = New-SupplementFixture; $arguments = $f.arguments
        $preview = New-YLResourceMigrationPreview @arguments
        $native = Get-YLResourceMigrationCandidatePreview $preview
        $oldPath = 'Assets/Catalog/Atlases/old.spriteatlasv2'
        $sourceRow = @($preview.inputs | Where-Object { $_.path -ceq $oldPath })[0]
        $candidateRow = @($native.inputs | Where-Object { $_.path -ceq $oldPath })[0]
        Assert-That ($sourceRow.sha256 -cne $candidateRow.sha256 -and $native.sourceInputs.Count -eq $preview.inputs.Count) 'Candidate projection replaced source authority instead of retaining it.'
        $workspace = Initialize-YLTestWorkspace -WorkspaceRoot (Join-Path $sandbox ('candidate-' + [Guid]::NewGuid().ToString('N'))) -Repository $f.repo
        $hostRecord = New-YLTestDirectory -Workspace $workspace -Kind Host -Key 'CandidateFixture'
        $result = New-YLTestDirectory -Workspace $workspace -Kind Result -Key 'CandidateFixture'
        $identity = Get-YLTestInputIdentity -Repository $f.repo -Paths @($preview.inputs.path) -Profile 'SupplementFixture'
        $files = [ordered]@{}
        foreach ($input in $preview.inputs) { $files[$input.path] = [IO.File]::ReadAllBytes((Join-Path $f.repo $input.path)) }
        foreach ($input in $preview.overlays) { $files[$input.path] = [IO.File]::ReadAllBytes((Get-YLResourceSupplementFile $preview.supplement.root $input.payload $input.sha256)) }
        Sync-YLUnityHost -HostRecord $hostRecord -Files $files -Identity $identity -EditorVersion '6000.3.27f1'
        $receiptSha = Get-YLResourceMigrationHash (Join-Path $hostRecord.path '.yl-inputs.json')
        $settings = Join-Path $hostRecord.path $preview.settingsPath
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($settings))
        [IO.File]::WriteAllText($settings, 'official settings fixture')
        [IO.File]::WriteAllText(($settings + '.meta'), "guid: $('d' * 32)`n")
        $manifestPath = New-YLResourceCandidateManifest -Preview $preview -PreviewSha256 ('e' * 64) -HostRecord $hostRecord -Result $result.path -SourceIdentity $identity -UnityVersion '6000.3.27f1' -AddressablesVersion '2.10.3'
        $manifest = Read-YLResourceMigrationManifest -Repository $f.repo -Manifest $manifestPath -ManifestSha256 (Get-YLResourceMigrationHash $manifestPath)
        $atlasOps = @($manifest.operations | Where-Object { $_.path -match '\.spriteatlasv2(\.meta)?$' })
        Assert-That ($atlasOps.Count -eq 4 -and @($atlasOps | Where-Object { $_.action -ceq 'Add' }).Count -eq 2 -and @($atlasOps | Where-Object { $_.action -ceq 'Modify' }).Count -eq 2) 'Atlas Add/Modify operation identity was incorrect.'
        Assert-That (@($manifest.operations | Where-Object { $_.path -match '(Config|Project|Recipe|Negative)\.asset' -or $_.action -ceq 'Delete' }).Count -eq 0) 'Derived author data or frozen retirement was applied automatically.'
        Assert-That ($manifest.retirement.entries.Count -eq 10 -and (Get-YLResourceMigrationHash (Join-Path $hostRecord.path '.yl-inputs.json')) -ceq $receiptSha) 'A source/input receipt was rewritten by packaging.'
    }
    Test-Case 'Supplement refuses Source drift, occupied new outputs and changed frozen evidence after Preview' {
        $f = New-SupplementFixture; $arguments = $f.arguments
        $preview = New-YLResourceMigrationPreview @arguments
        [IO.File]::WriteAllText((Join-Path $f.repo 'Assets/Catalog/Atlases/new.spriteatlasv2'), 'foreign new target')
        Assert-Rejected { Get-YLResourceMigrationCandidatePreview $preview } 'ExpectedAbsent'
        $f = New-SupplementFixture; $arguments = $f.arguments; $preview = New-YLResourceMigrationPreview @arguments
        [IO.File]::WriteAllText((Join-Path $f.repo 'Assets/Catalog/Atlases/old.spriteatlasv2'), 'edited original')
        Assert-Rejected { Get-YLResourceMigrationCandidatePreview $preview } 'Source|source|SHA'
        $f = New-SupplementFixture; $arguments = $f.arguments; $preview = New-YLResourceMigrationPreview @arguments
        [IO.File]::WriteAllText((Join-Path $f.root 'original-facts.json'), '{}')
        Assert-Rejected { Get-YLResourceMigrationCandidatePreview $preview } 'SHA|hash|Supplement'
    }
    Test-Case 'Supplement rejects a linked material directory rather than reading another root' {
        $f = New-SupplementFixture; $arguments = $f.arguments
        $directory = Join-Path $f.root 'GeneratedAfterGeneration'
        $target = Join-Path $sandbox ('foreign-' + [Guid]::NewGuid().ToString('N'))
        [IO.Directory]::Move($directory, $target)
        try {
            $null = New-Item -ItemType Junction -Path $directory -Target $target
            Assert-Rejected { New-YLResourceMigrationPreview @arguments } 'Linked|linked'
        } finally { if ([IO.Directory]::Exists($directory)) { [IO.Directory]::Delete($directory) } }
    }
    Test-Case 'Supplement cannot substitute derived SHA for original or accept a cancelled native proof' {
        $f = New-SupplementFixture; $arguments = $f.arguments
        $f.index.projects[0].assets[0].sourceOriginalSha256 = $f.index.projects[0].assets[0].derivedSha256
        Save-Json $f.indexPath $f.index; $arguments.SupplementSha256 = Get-YLResourceMigrationHash $f.indexPath
        Assert-Rejected { New-YLResourceMigrationPreview @arguments } 'original Source SHA'
        $f = New-SupplementFixture; $arguments = $f.arguments
        $exitPath = Join-Path $f.root 'process.log.exit.json'; $exit = [IO.File]::ReadAllText($exitPath) | ConvertFrom-Json -AsHashtable; $exit.cancelled = $true; Save-Json $exitPath $exit
        Assert-Rejected { New-YLResourceMigrationPreview @arguments } 'ordinary|cancel|process|outcome'
    }
    Test-Case 'Supplement CLI keeps negative retirement separate from the positive Project selection' {
        $f = New-SupplementFixture
        $selection = Join-Path $sandbox ('selection-' + [Guid]::NewGuid().ToString('N') + '.json')
        Write-YLTestJson -Path $selection -Value @{ schemaVersion = 1; projectGuids = $f.arguments.ProjectGuids; retireProjectGuids = $f.arguments.RetireProjectGuids; allowedRoots = $f.arguments.AllowedRoots; settingsPath = $f.arguments.SettingsPath }
        $json = & (Join-Path $PSScriptRoot '../Invoke-YLResourceMigration.ps1') -Action Preview -Repository $f.repo -Snapshot $f.snapshot -Inventory $f.arguments.Inventory -InventorySha256 $f.arguments.InventorySha256 -SelectionFile $selection -SupplementPath $f.indexPath -SupplementSha256 $f.arguments.SupplementSha256
        $preview = ($json -join [Environment]::NewLine) | ConvertFrom-Json -AsHashtable
        Assert-That ($preview.status -ceq 'ReadyForCandidate' -and $preview.entries.Count -eq 4 -and $preview.retirement.negativeProjectGuids[0] -ceq ('7' * 32)) 'CLI lost the explicit positive/retire separation.'
    }
} finally { if ([IO.Directory]::Exists($sandbox)) { [IO.Directory]::Delete($sandbox, $true) } }
if ($failures.Count) { throw ($failures -join [Environment]::NewLine) }
"Resource Supplement checks passed: $script:cases"
