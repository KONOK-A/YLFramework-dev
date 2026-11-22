#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param([Parameter(Mandatory)][string] $TemporaryRoot)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Common/Workspace.ps1')
$implementation = Join-Path $PSScriptRoot '../Common/ResourceMigration.ps1'
if ([IO.File]::Exists($implementation)) { . $implementation }
. (Join-Path $PSScriptRoot '../ResourceMigrationHost.ps1')
$TemporaryRoot = Get-YLTestPath $TemporaryRoot
if (-not [IO.Directory]::Exists($TemporaryRoot)) { throw 'TemporaryRoot must exist.' }
$sandbox = Join-Path $TemporaryRoot ('yl-resource-migration-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($sandbox)
$failures = [Collections.Generic.List[string]]::new()
$script:cases = 0

function Assert-That([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Rejected([scriptblock] $Action, [string] $Pattern) {
    try { $null = & $Action }
    catch {
        if ($_.Exception -is [Management.Automation.CommandNotFoundException] -or
            $_.Exception.Message -notmatch $Pattern) { throw }
        return
    }
    throw "Expected rejection matching: $Pattern"
}

function Test-Case([string] $Name, [scriptblock] $Body) {
    $script:cases++
    try { & $Body; "PASS: $Name" }
    catch { $failures.Add("${Name}: $($_.Exception.Message)`n$($_.ScriptStackTrace)"); "FAIL: ${Name}: $($_.Exception.Message)`n$($_.ScriptStackTrace)" }
}

function New-Fixture {
    $root = Join-Path $sandbox ([Guid]::NewGuid().ToString('N'))
    $assets = Join-Path $root 'Assets/Catalog'
    $null = [IO.Directory]::CreateDirectory($assets)
    [IO.File]::WriteAllBytes((Join-Path $assets 'entry.bytes'), [byte[]]@(0, 1, 255, 10))
    [IO.File]::WriteAllText((Join-Path $assets 'entry.bytes.meta'), "fileFormatVersion: 2`nguid: 11111111111111111111111111111111`n")
    [IO.File]::WriteAllText((Join-Path $root 'Assets/Catalog.meta'), "fileFormatVersion: 2`nguid: 22222222222222222222222222222222`nfolderAsset: yes`n")
    return [pscustomobject]@{
        root = $root
        asset = Join-Path $assets 'entry.bytes'
        destination = Join-Path $root 'LocalArchives/ResourceLegacy/test'
    }
}

function New-PreviewFixture {
    param([switch] $WithConsumer)
    $f = New-Fixture
    $project = 'Assets/Catalog/Project.asset'
    [IO.File]::WriteAllText((Join-Path $f.root $project), 'legacy project bytes')
    [IO.File]::WriteAllText((Join-Path $f.root ($project + '.meta')), "fileFormatVersion: 2`nguid: 33333333333333333333333333333333`n")
    if ($WithConsumer) {
        [IO.File]::WriteAllText((Join-Path $f.root 'Assets/Catalog/Consumer.asset'), 'original consumer bytes with unrelated fields')
        [IO.File]::WriteAllText((Join-Path $f.root 'Assets/Catalog/Consumer.asset.meta'), "guid: 44444444444444444444444444444444`n")
    }
    $null = New-YLResourceSnapshot -Repository $f.root -Destination $f.destination -InputPaths @('Assets/Catalog') -SourceCommit ('a' * 40)
    $asset = @{
        path = 'Assets/Catalog/entry.bytes'; guid = '11111111111111111111111111111111'
        sha256 = (Get-FileHash -LiteralPath $f.asset).Hash; objects = @(); dependencies = @('Assets/Catalog/entry.bytes')
    }
    $projectAsset = @{
        path = $project; guid = '33333333333333333333333333333333'
        sha256 = (Get-FileHash -LiteralPath (Join-Path $f.root $project)).Hash; objects = @(); dependencies = @($project)
    }
    $definition = @{
        guid = $projectAsset.guid; localId = 11400000; assetPath = $project; projectId = 'test-project'
        type = 'YLFramework.Editor.Resource.ResourceProject, YLFramework.Editor'; status = 'Resolved'
        properties = @{ compression = @{ propertyType = 'Enum'; type = 'ResourceCompression'; value = 0 }; contentEncryption = @{ propertyType = 'Enum'; type = 'ResourceContentEncryption'; value = 0 } }
        assets = @(@{
            path = $asset.path; guid = $asset.guid; localId = 4900000; type = 'UnityEngine.TextAsset, UnityEngine.CoreModule'
            address = 'test-project/data/entry'; legacyPath = 'data/entry'; aliases = @('test-project/old-alias')
            group = 'main'; packing = 'Together'; selectionGuid = '22222222222222222222222222222222'
            selectionPath = 'Assets/Catalog'; subAssets = @()
        }); fingerprintPaths = @($project, $asset.path)
    }
    $projectAsset.objects = @($definition)
    $inventory = @{
        schemaVersion = 1; purpose = 'YLResource.LegacyInventory'; hostId = ('b' * 32); inputHash = ('c' * 64)
        snapshotManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $f.destination 'snapshot.json')).Hash.ToLowerInvariant()
        unityVersion = '6000.3.27f1'; assets = @($asset, $projectAsset); projects = @($definition)
    }
    if ($WithConsumer) {
        $inventory.assets += @{
            path = 'Assets/Catalog/Consumer.asset'; guid = ('4' * 32); sha256 = (Get-FileHash -LiteralPath (Join-Path $f.root 'Assets/Catalog/Consumer.asset')).Hash.ToLowerInvariant()
            objects = @(@{
                guid = ('4' * 32); localId = 11400000; targetPrefabId = 0; assetPath = 'Assets/Catalog/Consumer.asset'
                type = 'Fixture.Consumer, Fixture'; properties = @{ address = @{ propertyType = 'String'; type = 'string'; value = 'test-project/old-alias' } }
            })
        }
    }
    $evidence = Join-Path $f.destination 'Inventory/test'
    $null = [IO.Directory]::CreateDirectory($evidence)
    $inventoryPath = Join-Path $evidence 'inventory.json'
    Write-YLTestJson -Path $inventoryPath -Value $inventory
    Write-YLTestJson -Path (Join-Path $evidence 'audit.json') -Value @{
        purpose = 'YLResource.LegacyInventoryAudit'; status = 'Verified'; assets = $inventory.assets.Count; objects = $(if ($WithConsumer) { 2 } else { 1 })
        sourceGuidsPreserved = $true; inventorySha256 = (Get-FileHash -LiteralPath $inventoryPath).Hash.ToLowerInvariant()
        projects = @(@{ path = $project; projectId = 'test-project'; status = 'Resolved'; assets = 1; issue = $null })
    }
    return @{
        fixture = $f; inventoryPath = $inventoryPath; inventory = $inventory
        arguments = @{
            Repository = $f.root; Snapshot = $f.destination; Inventory = $inventoryPath
            InventorySha256 = (Get-FileHash -LiteralPath $inventoryPath).Hash.ToLowerInvariant()
            ProjectGuids = @($projectAsset.guid); SettingsPath = 'Assets/AddressableAssetsData/AddressableAssetSettings.asset'
            AllowedRoots = @('Assets/Catalog')
        }
    }
}

function New-ApplyFixture {
    $f = New-PreviewFixture
    $artifact = Join-Path $sandbox ('apply-evidence-' + [Guid]::NewGuid().ToString('N'))
    $null = [IO.Directory]::CreateDirectory($artifact)
    $projectPath = 'Assets/Catalog/Project.asset'
    $retiredGuid = '3' * 32
    $retirement = @($projectPath, ($projectPath + '.meta') | ForEach-Object {
        @{ path = $_; originalSha256 = Get-YLResourceMigrationHash (Join-Path $f.fixture.root $_); guid = $retiredGuid; kind = 'Project' }
    })
    $operations = @(
        [ordered]@{ path = 'Assets/Catalog/entry.bytes'; action = 'Modify'; expectedAbsent = $false; inputSha256 = (Get-FileHash -LiteralPath $f.fixture.asset).Hash.ToLowerInvariant(); outputSha256 = $null; oldGuid = $null; newGuid = $null },
        [ordered]@{ path = 'Assets/AddressableAssetsData/New.asset'; action = 'Add'; expectedAbsent = $true; inputSha256 = $null; outputSha256 = $null; oldGuid = $null; newGuid = $null },
        (New-YLCandidateRetirementDeletes @{ retirement = @{ entries = $retirement } })
    )
    $operations = @($operations | ForEach-Object { $_ })
    foreach ($operation in $operations) {
        if ($operation.action -ceq 'Delete') { continue }
        $path = Join-Path $artifact ('Output/' + $operation.path)
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
        $content = if ($operation.path.EndsWith('.asset')) { "%YAML 1.1`n--- !u!114 &1`nMonoBehaviour:`n  m_Name: Converted`n" } else { 'converted ' + $operation.path }
        [IO.File]::WriteAllText($path, $content)
        $operation.outputSha256 = (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()
    }
    $data = [ordered]@{
        schemaVersion = 1; purpose = 'YLResource.AddressablesMigrationManifest'; repository = $f.fixture.root; previewSha256 = ('d' * 64)
        hostId = ('a' * 32); inputHash = ('b' * 64); unityVersion = '6000.3.27f1'; addressablesVersion = '2.10.3'
        inputs = @(@{ path = 'Assets/Catalog/entry.bytes.meta'; sha256 = (Get-FileHash -LiteralPath ($f.fixture.asset + '.meta')).Hash.ToLowerInvariant(); guid = ('1' * 32) })
        folders = @(); operations = $operations
        retirement = @{ entries = $retirement; deleteEnabled = $true }
        outputDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $operations -Depth 40 -Compress)))).ToLowerInvariant()
    }
    $manifest = Join-Path $artifact 'migration.json'
    Write-YLTestJson -Path $manifest -Value $data
    $manifestSha = Get-YLResourceMigrationHash $manifest
    $validation = Join-Path $artifact 'fixture-validation.json'
    $nativeProof = Join-Path $artifact 'native-proof.json'
    Write-YLTestJson -Path $nativeProof -Value @{
        schemaVersion = 1; purpose = 'YLResource.CandidateNativeProof'; status = 'Passed'; runtimeVerified = $true
        hostId = ('c' * 32); resultId = ('d' * 32); inputHash = ('e' * 64); manifestSha256 = $manifestSha
        subjectHostId = $data.hostId; subjectInputHash = $data.inputHash
        previewSha256 = $data.previewSha256; outputDigest = $data.outputDigest
        unityVersion = $data.unityVersion; addressablesVersion = $data.addressablesVersion; addressablesSource = 'Registry'
        checks = @('OfficialContentLoad', 'ConsumerColdRead', 'ForeignSettingsPreserved'); cleanupVerified = $true; returnedToEditMode = $true
        retired = @($retirement | ForEach-Object { @{ path = $_.path; guid = $_.guid; kind = $_.kind; originalSha256 = $_.originalSha256; absent = $true; passed = $true } })
    }
    # A file-operation unit fixture, not a claim that Unity was run by this test.
    Write-YLTestJson -Path $validation -Value @{
        schemaVersion = 1; purpose = 'YLResource.AddressablesMigrationValidation'; status = 'Passed'; previewSha256 = $data.previewSha256
        manifestSha256 = $manifestSha; hostId = $data.hostId; inputHash = $data.inputHash
        unityVersion = $data.unityVersion; addressablesVersion = $data.addressablesVersion
        validationHostId = ('c' * 32); validationResultId = ('d' * 32); validationInputHash = ('e' * 64)
        nativeProof = $nativeProof; nativeProofSha256 = Get-YLResourceMigrationHash $nativeProof
        outputDigest = $data.outputDigest; runtimeVerified = $true; checks = @('OfficialContentLoad', 'ConsumerColdRead', 'ForeignSettingsPreserved')
        process = @{ status = 'Exited'; observedExit = $true; exitCode = 0; timedOut = $false; cancelled = $false; observerError = $null; cleanupError = $null; forcedCleanup = $false; streamsDrained = $true; jobAssigned = $true; activeProcesses = 0 }
    }
    return @{
        fixture = $f.fixture; artifact = $artifact; data = $data
        arguments = @{
            Repository = $f.fixture.root; Manifest = $manifest; ManifestSha256 = (Get-FileHash -LiteralPath $manifest).Hash.ToLowerInvariant()
            ValidationReceipt = $validation; ValidationSha256 = (Get-FileHash -LiteralPath $validation).Hash.ToLowerInvariant()
            ReceiptDirectory = (Join-Path $artifact 'Receipts'); ExclusiveWindowConfirmed = $true
        }
    }
}

try {
    Test-Case 'Snapshot preserves binary content and adjacent folder/asset metadata' {
        $f = New-Fixture
        $snapshot = New-YLResourceSnapshot -Repository $f.root -Destination $f.destination -InputPaths @('Assets/Catalog/entry.bytes') -SourceCommit ('a' * 40)
        Assert-That (@($snapshot.files).Count -eq 3) 'The asset and both metadata files must be preserved.'
        Assert-That ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $f.destination 'Source/Assets/Catalog/entry.bytes'))) -ceq 'AAH/Cg==') 'Binary content changed.'
        Assert-That ([Convert]::ToBase64String([IO.File]::ReadAllBytes($f.asset)) -ceq 'AAH/Cg==') 'The source was changed.'
        Assert-That ($snapshot.sourceCommit -ceq ('a' * 40)) 'Source identity was lost.'
        $check = Test-YLResourceSnapshot -Path $f.destination -CompareSource
        Assert-That ($check.status -ceq 'Verified' -and $check.fileCount -eq 3) 'The real copied files were not verified.'
    }

    Test-Case 'Snapshot refuses existing destinations without changing their files' {
        $f = New-Fixture
        $null = [IO.Directory]::CreateDirectory($f.destination)
        $sentinel = Join-Path $f.destination 'keep.txt'
        [IO.File]::WriteAllText($sentinel, 'existing')
        Assert-Rejected { New-YLResourceSnapshot -Repository $f.root -Destination $f.destination -InputPaths @('Assets/Catalog') -SourceCommit ('a' * 40) } 'existing|exists'
        Assert-That ([IO.File]::ReadAllText($sentinel) -ceq 'existing') 'An existing archive was overwritten.'
    }

    Test-Case 'Snapshot rejects traversal, missing input, and Unity-scanned destinations before copying' {
        $f = New-Fixture
        Assert-Rejected { New-YLResourceSnapshot -Repository $f.root -Destination $f.destination -InputPaths @('../outside') -SourceCommit ('a' * 40) } 'relative|outside|Ambiguous'
        Assert-Rejected { New-YLResourceSnapshot -Repository $f.root -Destination $f.destination -InputPaths @('Assets/Absent') -SourceCommit ('a' * 40) } 'Missing|exist'
        Assert-Rejected { New-YLResourceSnapshot -Repository $f.root -Destination (Join-Path $f.root 'Assets/Archive') -InputPaths @('Assets/Catalog') -SourceCommit ('a' * 40) } 'LocalArchives|outside'
        Assert-That (-not [IO.Directory]::Exists($f.destination)) 'Rejected input created an archive.'
    }

    Test-Case 'Snapshot refuses sensitive inputs before creating output' {
        $f = New-Fixture
        [IO.File]::WriteAllText((Join-Path $f.root 'Assets/Catalog/test.pfx'), 'not-a-real-certificate')
        Assert-Rejected { New-YLResourceSnapshot -Repository $f.root -Destination $f.destination -InputPaths @('Assets/Catalog') -SourceCommit ('a' * 40) } 'Sensitive|sensitive'
        Assert-That (-not [IO.Directory]::Exists($f.destination)) 'Sensitive input was partially copied.'
    }

    Test-Case 'Verification detects archive corruption and source drift independently' {
        $f = New-Fixture
        $null = New-YLResourceSnapshot -Repository $f.root -Destination $f.destination -InputPaths @('Assets/Catalog') -SourceCommit ('a' * 40)
        [IO.File]::WriteAllText($f.asset, 'source-edited-later')
        Assert-That ((Test-YLResourceSnapshot -Path $f.destination).status -ceq 'Verified') 'An intact archive must remain independently verifiable.'
        Assert-Rejected { Test-YLResourceSnapshot -Path $f.destination -CompareSource } 'source|Source'
        [IO.File]::WriteAllText((Join-Path $f.destination 'Source/Assets/Catalog/entry.bytes'), 'corrupt')
        Assert-Rejected { Test-YLResourceSnapshot -Path $f.destination } 'hash|Hash|content|Content'
    }

    Test-Case 'Verification rejects unrecorded files and manifest path escapes' {
        $f = New-Fixture
        $null = New-YLResourceSnapshot -Repository $f.root -Destination $f.destination -InputPaths @('Assets/Catalog') -SourceCommit ('a' * 40)
        $extra = Join-Path $f.destination 'Source/unrecorded.txt'
        [IO.File]::WriteAllText($extra, 'unrecorded')
        Assert-Rejected { Test-YLResourceSnapshot -Path $f.destination } 'file set|unrecorded|Unexpected'
        [IO.File]::Delete($extra)
        $manifestPath = Join-Path $f.destination 'snapshot.json'
        $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json -AsHashtable -DateKind String
        $manifest.files[0].path = '../escape'
        [IO.File]::WriteAllText($manifestPath, (ConvertTo-Json -InputObject $manifest -Depth 20))
        Assert-Rejected { Test-YLResourceSnapshot -Path $f.destination } 'relative|outside|Ambiguous'
    }

    Test-Case 'Snapshot refuses linked input trees' {
        $f = New-Fixture
        $target = Join-Path $sandbox ('target-' + [Guid]::NewGuid().ToString('N'))
        $null = [IO.Directory]::CreateDirectory($target)
        $link = Join-Path $f.root 'Assets/Catalog/linked'
        try {
            $null = New-Item -ItemType Junction -Path $link -Target $target
            Assert-Rejected { New-YLResourceSnapshot -Repository $f.root -Destination $f.destination -InputPaths @('Assets/Catalog') -SourceCommit ('a' * 40) } 'Linked|linked'
            Assert-That (-not [IO.Directory]::Exists($f.destination)) 'A linked tree was copied.'
        } finally {
            if ([IO.Directory]::Exists($link)) { [IO.Directory]::Delete($link) }
        }
    }

    Test-Case 'Preview uses certified expanded folder entries and never writes source or alias catalog' {
        $f = New-PreviewFixture
        $arguments = $f.arguments
        $plan = New-YLResourceMigrationPreview @arguments
        Assert-That ($plan.status -ceq 'ReadyForCandidate') 'A valid preview must require native candidate validation, not claim migration.'
        Assert-That ($plan.entries.Count -eq 1 -and $plan.entries[0].address -ceq 'test-project/data/entry') 'Canonical project/path was lost.'
        Assert-That ($plan.entries[0].selectionPath -ceq 'Assets/Catalog' -and $plan.entries[0].localId -eq 4900000) 'The exported folder expansion or localId was replaced by a guess.'
        Assert-That ($plan.entries[0].compression -ceq 'Lz4') 'The certified legacy compression intent was silently replaced by an official default.'
        Assert-That ($plan.aliases[0].canonical -ceq 'test-project/data/entry') 'Alias evidence must be rewrite-only.'
        Assert-That (-not [IO.Directory]::Exists((Join-Path $f.fixture.root 'Assets/AddressableAssetsData'))) 'Preview modified the source project.'
        Assert-That ($plan.disableCatalogUpdateOnStartup -eq $true -and $plan.candidateValidated -eq $false) 'Catalog policy or validation status was fabricated.'
    }

    Test-Case 'Preview refuses stale inventory and source metadata drift' {
        $f = New-PreviewFixture
        $arguments = $f.arguments
        $f.arguments.InventorySha256 = '0' * 64
        Assert-Rejected { New-YLResourceMigrationPreview @arguments } 'inventory.*hash|inventory.*SHA'
        $f.arguments.InventorySha256 = (Get-FileHash -LiteralPath $f.inventoryPath).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText(($f.fixture.asset + '.meta'), "guid: 99999999999999999999999999999999`n")
        Assert-Rejected { New-YLResourceMigrationPreview @arguments } 'source|GUID|hash|SHA'
    }

    Test-Case 'Preview preserves Atlas and intentionally invalid project blockers without empty success' {
        $f = New-PreviewFixture
        $arguments = $f.arguments
        $f.inventory.projects[0].status = 'RejectedLegacyDefinition'
        $f.inventory.projects[0]['error'] = 'Automatic Atlases require generation/update before dependency analysis.'
        $f.inventory.projects[0].assets = @()
        [IO.File]::WriteAllText($f.inventoryPath, (ConvertTo-Json -InputObject $f.inventory -Depth 40))
        $auditPath = Join-Path ([IO.Path]::GetDirectoryName($f.inventoryPath)) 'audit.json'
        $audit = [IO.File]::ReadAllText($auditPath) | ConvertFrom-Json -AsHashtable
        $audit.inventorySha256 = (Get-FileHash -LiteralPath $f.inventoryPath).Hash.ToLowerInvariant()
        $audit.projects[0].status = 'RejectedLegacyDefinition'; $audit.projects[0].assets = 0
        [IO.File]::WriteAllText($auditPath, (ConvertTo-Json -InputObject $audit -Depth 40))
        $f.arguments.InventorySha256 = $audit.inventorySha256
        $plan = New-YLResourceMigrationPreview @arguments
        Assert-That ($plan.status -ceq 'Blocked' -and $plan.entries.Count -eq 0) 'Rejected legacy inputs must not become successful empty groups.'
        Assert-That ($plan.blockers[0].reason -match 'Automatic Atlases') 'The exact legacy failure reason was discarded.'
    }

    Test-Case 'Preview refuses unknown selected project and an unapproved source root' {
        $f = New-PreviewFixture
        $arguments = $f.arguments
        $f.arguments.ProjectGuids = @('9' * 32)
        Assert-Rejected { New-YLResourceMigrationPreview @arguments } 'Unknown.*Project|selected.*Project'
        $f.arguments.ProjectGuids = @('3' * 32)
        $f.arguments.AllowedRoots = @('Assets/Other')
        Assert-Rejected { New-YLResourceMigrationPreview @arguments } 'approved|allow|scope'
    }

    Test-Case 'Preview refuses changed folder membership even for a file absent from old expansion' {
        $f = New-PreviewFixture
        $arguments = $f.arguments
        [IO.File]::WriteAllText((Join-Path $f.fixture.root 'Assets/Catalog/new.json'), 'new unexported member')
        Assert-Rejected { New-YLResourceMigrationPreview @arguments } 'membership|unrecorded|source'
    }

    Test-Case 'Preview blocks unselected real alias consumers and only emits a certified exact-field rewrite' {
        $f = New-PreviewFixture -WithConsumer
        $arguments = $f.arguments
        $preview = New-YLResourceMigrationPreview @arguments
        Assert-That ($preview.status -ceq 'Blocked' -and $preview.blockers[0].code -ceq 'UnselectedAliasConsumer') 'An unconverted alias consumer was silently accepted.'
        $arguments.ConsumerFields = @(@{ guid = ('4' * 32); localId = 11400000; propertyPath = 'address' })
        $preview = New-YLResourceMigrationPreview @arguments
        Assert-That ($preview.status -ceq 'ReadyForCandidate' -and $preview.rewrites.Count -eq 1 -and $preview.consumers.Count -eq 1) 'An exact consumer field was not retained for native verification.'
        Assert-That ($preview.rewrites[0].oldValue -ceq 'test-project/old-alias' -and $preview.rewrites[0].newValue -ceq 'test-project/data/entry') 'The consumer alias was guessed instead of canonicalized from the exported identity.'
        Assert-That ([IO.File]::ReadAllText((Join-Path $f.fixture.root 'Assets/Catalog/Consumer.asset')) -ceq 'original consumer bytes with unrelated fields') 'Preview rewrote source consumer bytes.'
        $arguments.ConsumerFields[0].propertyPath = 'unknownField'
        Assert-Rejected { New-YLResourceMigrationPreview @arguments } 'property|field'
    }

    Test-Case 'Candidate refuses a source or Folder member changed after Preview before any Unity launch' {
        $f = New-PreviewFixture
        $arguments = $f.arguments
        $preview = New-YLResourceMigrationPreview @arguments
        [IO.File]::WriteAllText((Join-Path $f.fixture.root 'Assets/Catalog/late.bin'), 'new source after Preview')
        Assert-Rejected { Invoke-YLResourceMigrationCandidate -Preview $preview -Unity (Join-Path $sandbox 'not-unity.exe') -WorkspaceRoot (Join-Path $sandbox 'must-not-create') -CandidateInputsFile (Join-Path $sandbox 'missing-inputs.json') } 'Folder membership'
        [IO.File]::Delete((Join-Path $f.fixture.root 'Assets/Catalog/late.bin'))
        [IO.File]::WriteAllText($f.fixture.asset, 'edited after Preview')
        Assert-Rejected { Assert-YLResourcePreviewSource $preview } 'source SHA|source.*changed'
        Assert-That (-not [IO.Directory]::Exists((Join-Path $sandbox 'must-not-create'))) 'A stale Preview created a native host.'
    }

    Test-Case 'Apply requires native candidate proof and an explicit concurrent-writer exclusion window' {
        $f = New-PreviewFixture
        Assert-Rejected { Invoke-YLResourceMigrationApply -Repository $f.fixture.root -Manifest (Join-Path $f.fixture.root 'missing.json') } 'window|exclusive|concurrent'
    }

    Test-Case 'Apply rejects unvalidated candidates and refuses existing Add targets in the whole-batch preflight' {
        $f = New-ApplyFixture
        $arguments = $f.arguments
        $arguments.ValidationSha256 = '0' * 64
        Assert-Rejected { Invoke-YLResourceMigrationApply @arguments } 'validation'
        $arguments.ValidationSha256 = (Get-FileHash -LiteralPath $arguments.ValidationReceipt).Hash.ToLowerInvariant()
        $target = Join-Path $f.fixture.root 'Assets/AddressableAssetsData/New.asset'
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        [IO.File]::WriteAllText($target, 'foreign output')
        Assert-Rejected { Invoke-YLResourceMigrationApply @arguments } 'ExpectedAbsent|existing target'
        Assert-That ([IO.File]::ReadAllText($target) -ceq 'foreign output') 'A foreign Add target was overwritten.'
        Assert-That ([Convert]::ToBase64String([IO.File]::ReadAllBytes($f.fixture.asset)) -ceq 'AAH/Cg==') 'Preflight conflict allowed an earlier write.'
    }

    Test-Case 'Apply preserves originals, verifies every receipt and only resumes its proven completed prefix' {
        $f = New-ApplyFixture
        $arguments = $f.arguments
        $deletePath = Join-Path $f.fixture.root 'Assets/Catalog/Project.asset'
        $blockDelete = [IO.File]::Open($deletePath, 'Open', 'Read', 'Read')
        try { Assert-Rejected { Invoke-YLResourceMigrationApply @arguments } 'used|using|process|进程|拒绝|denied' }
        finally { $blockDelete.Dispose() }
        Assert-That ([IO.File]::Exists((Join-Path $arguments.ReceiptDirectory '000000.json'))) 'Verified Modify prefix receipt was lost.'
        Assert-That ([IO.File]::Exists((Join-Path $arguments.ReceiptDirectory '000001.json'))) 'Verified Add prefix receipt was lost.'
        Assert-That (-not [IO.File]::Exists((Join-Path $arguments.ReceiptDirectory '000002.json'))) 'A failed deletion received a fake completion receipt.'
        $result = Invoke-YLResourceMigrationApply @arguments
        Assert-That ($result.status -ceq 'AppliedAwaitingColdVerify' -and $result.completed -eq 4) 'A known partial prefix could not be resumed.'
        Assert-That (-not [IO.File]::Exists($deletePath)) 'The final authorized deletion did not occur.'
        Assert-That ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $arguments.ReceiptDirectory 'Original/Assets/Catalog/entry.bytes'))) -ceq 'AAH/Cg==') 'Original bytes were not retained.'
        [IO.File]::WriteAllText($f.fixture.asset, 'edited after completed Apply')
        Assert-Rejected { Invoke-YLResourceMigrationApply @arguments } 'SHA|conflict'
        Assert-That ([IO.File]::ReadAllText($f.fixture.asset) -ceq 'edited after completed Apply') 'Rerun overwrote an unknown edit.'
    }

    Test-Case 'Cold Verify stages only actual applied files and checks preserved originals, not candidate payloads' {
        $f = New-ApplyFixture
        $arguments = $f.arguments
        $capture = @{ Repository = $arguments.Repository; Manifest = $f.data; ReceiptDirectory = $arguments.ReceiptDirectory; ManifestSha256 = $arguments.ManifestSha256 }
        Assert-Rejected { Get-YLResourceMigrationAppliedState @capture } 'receipt|entire frozen batch'
        $null = Invoke-YLResourceMigrationApply @arguments
        $target = 'Assets/AddressableAssetsData/New.asset'
        $appliedBytes = [IO.File]::ReadAllText((Join-Path $f.fixture.root $target))
        [IO.File]::WriteAllText((Join-Path $f.artifact ('Output/' + $target)), 'not applied')
        $applied = Get-YLResourceMigrationAppliedState @capture
        Assert-That ($applied.files.Count -eq 3 -and [Text.Encoding]::UTF8.GetString($applied.files[$target]) -ceq $appliedBytes) 'Verify used the candidate payload instead of the applied bytes.'
        Assert-That ($applied.originals.Count -eq 3 -and -not $applied.files.Contains('Assets/Catalog/Project.asset')) 'Verify lost a preimage or staged a retired asset.'
        [IO.File]::WriteAllText((Join-Path $arguments.ReceiptDirectory 'Original/Assets/Catalog/entry.bytes'), 'different preimage')
        Assert-Rejected { Get-YLResourceMigrationAppliedState @capture } 'preserved original'
    }

    Test-Case 'Delete refuses a retired GUID in an unlisted scene before changing any source file' {
        $f = New-ApplyFixture
        $arguments = $f.arguments
        $foreign = Join-Path $f.fixture.root 'Assets/Outside.unity'
        [IO.File]::WriteAllText($foreign, "%YAML 1.1`n--- !u!114 &1`nMonoBehaviour:`n  config: {fileID: 11400000, guid: $('3' * 32), type: 2}`n")
        Assert-Rejected { Invoke-YLResourceMigrationApply @arguments } 'retired GUID.*Outside.unity'
        Assert-That ([IO.File]::Exists((Join-Path $f.fixture.root 'Assets/Catalog/Project.asset')) -and
            -not [IO.File]::Exists((Join-Path $arguments.ReceiptDirectory '000000.json'))) 'A reference outside the manifest permitted writes or retirement.'
    }

    Test-Case 'The native JSON XR Settings file is scanned and preserved byte-for-byte during retirement' {
        $f = New-ApplyFixture
        $arguments = $f.arguments
        $path = Join-Path $f.fixture.root 'ProjectSettings/XRSettings.asset'
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
        [IO.File]::WriteAllText($path, '{"m_SettingKeys":["VR Device Disabled","VR Device User Alert"],"m_SettingValues":["False","False"]}')
        $original = Get-YLResourceMigrationHash $path
        $applied = Invoke-YLResourceMigrationApply @arguments
        $verified = Get-YLResourceMigrationAppliedState -Repository $arguments.Repository -Manifest $f.data -ReceiptDirectory $arguments.ReceiptDirectory -ManifestSha256 $arguments.ManifestSha256
        Assert-That ($applied.status -ceq 'AppliedAwaitingColdVerify' -and $verified.retirementReferences.sha256 -ceq $applied.retirementReferences.sha256 -and
            (Get-YLResourceMigrationHash $path) -ceq $original) 'XR Settings were not preserved and bound to the retirement scan.'
    }

    Test-Case 'Retirement scans decoded XR values and rejects unproven JSON asset formats' {
        $f = New-ApplyFixture
        $arguments = $f.arguments
        $path = Join-Path $f.fixture.root 'ProjectSettings/XRSettings.asset'
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
        [IO.File]::WriteAllText($path, ('{"m_SettingKeys":["VR Device Disabled"],"m_SettingValues":["\u0033' + ('3' * 31) + '"]}'))
        Assert-Rejected { Invoke-YLResourceMigrationApply @arguments } 'retired GUID.*XRSettings\.asset'
        [IO.File]::WriteAllText($path, '{"m_SettingKeys":["VR Device Disabled"],"m_SettingValues":[false]}')
        Assert-Rejected { Invoke-YLResourceMigrationApply @arguments } 'cannot inspect.*XRSettings\.asset'
        [IO.File]::WriteAllText($path, '{"m_SettingKeys":["VR Device Disabled"],"m_SettingValues":["False"]}')
        [IO.File]::Copy($path, (Join-Path $f.fixture.root 'ProjectSettings/Unknown.asset'))
        Assert-Rejected { Invoke-YLResourceMigrationApply @arguments } 'cannot inspect.*Unknown\.asset'
        Assert-That (-not [IO.File]::Exists((Join-Path $arguments.ReceiptDirectory '000000.json'))) 'The unsafe XR or foreign asset permitted writes.'
    }

    Test-Case 'Cold Verify checks new references in project settings outside the applied file list' {
        $f = New-ApplyFixture
        $arguments = $f.arguments
        $null = Invoke-YLResourceMigrationApply @arguments
        $foreign = Join-Path $f.fixture.root 'ProjectSettings/Foreign.asset'
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($foreign))
        [IO.File]::WriteAllText($foreign, "%YAML 1.1`n--- !u!114 &1`nMonoBehaviour:`n  config: {fileID: 11400000, guid: $('3' * 32), type: 2}`n")
        Assert-Rejected {
            Get-YLResourceMigrationAppliedState -Repository $arguments.Repository -Manifest $f.data -ReceiptDirectory $arguments.ReceiptDirectory -ManifestSha256 $arguments.ManifestSha256
        } 'retired GUID.*Foreign.asset'
    }

    Test-Case 'Cold Verify requires a successful batch receipt even if every file has a receipt' {
        $f = New-ApplyFixture
        $arguments = $f.arguments
        $scan = (Get-Command Get-YLResourceRetirementReferenceState).ScriptBlock
        & {
            $observed = @{ scans = 0 }
            function Get-YLResourceRetirementReferenceState {
                param($Repository, $Manifest, $ProjectedOutput)
                if (++$observed.scans -eq 3) {
                    [IO.File]::WriteAllText((Join-Path $Repository 'Assets/Foreign.txt'), 'External edit during the final Apply check')
                }
                return & $scan @PSBoundParameters
            }
            Assert-Rejected { Invoke-YLResourceMigrationApply @arguments } 'reference inputs changed during Apply'
        }
        $fileReceipts = @([IO.Directory]::EnumerateFiles($arguments.ReceiptDirectory, '*.json') | Where-Object { [IO.Path]::GetFileName($_) -cmatch '^[0-9]{6}\.json$' })
        Assert-That ($fileReceipts.Count -eq $f.data.operations.Count) 'The final scope drift must happen after every per-file receipt exists.'
        Assert-Rejected {
            Get-YLResourceMigrationAppliedState -Repository $arguments.Repository -Manifest $f.data -ReceiptDirectory $arguments.ReceiptDirectory -ManifestSha256 $arguments.ManifestSha256
        } 'successful Apply batch receipt'
    }

    Test-Case 'Cold Verify binds surviving reference bytes to a successful Apply attempt' {
        $f = New-ApplyFixture
        $arguments = $f.arguments
        $capture = @{ Repository = $arguments.Repository; Manifest = $f.data; ReceiptDirectory = $arguments.ReceiptDirectory; ManifestSha256 = $arguments.ManifestSha256 }
        $outside = Join-Path $f.fixture.root 'Assets/Foreign.txt'
        [IO.File]::WriteAllText($outside, 'before Apply')
        $null = Invoke-YLResourceMigrationApply @arguments
        $verified = Get-YLResourceMigrationAppliedState @capture
        [IO.File]::WriteAllText($outside, 'different bytes, no retiring GUID')
        Assert-Rejected { Get-YLResourceMigrationAppliedState @capture } 'successful Apply batch receipt'
        $null = Invoke-YLResourceMigrationApply @arguments
        $reverified = Get-YLResourceMigrationAppliedState @capture
        Assert-That ($verified.retirementReferences.sha256 -cne $reverified.retirementReferences.sha256 -and
            [IO.File]::ReadAllText($outside) -ceq 'different bytes, no retiring GUID') 'A clean explicitly rechecked prefix did not obtain a new matching batch receipt.'
    }

    Test-Case 'Delete refuses an opaque serialized asset outside the manifest' {
        $f = New-ApplyFixture
        $arguments = $f.arguments
        [IO.File]::WriteAllBytes((Join-Path $f.fixture.root 'Assets/Opaque.asset'), [byte[]]@(0, 1, 255, 10))
        Assert-Rejected { Invoke-YLResourceMigrationApply @arguments } 'cannot inspect.*Opaque.asset'
    }

    Test-Case 'An enabled retirement cannot omit any frozen file or its metadata' {
        $f = New-ApplyFixture
        $f.data.operations = @($f.data.operations | Where-Object path -CNE 'Assets/Catalog/Project.asset.meta')
        $f.data.outputDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $f.data.operations -Depth 40 -Compress)))).ToLowerInvariant()
        $path = Join-Path $f.artifact 'incomplete.json'
        Write-YLTestJson $path $f.data
        Assert-Rejected { Read-YLResourceMigrationManifest $f.fixture.root $path (Get-YLResourceMigrationHash $path) } 'retirement.*incomplete|retirement.*metadata'
    }

    Test-Case 'Apply rechecks a pending Modify, Delete or metadata file after batch preflight' {
        foreach ($target in @('Assets/Catalog/entry.bytes', 'Assets/Catalog/Project.asset', 'Assets/Catalog/entry.bytes.meta')) {
            $f = New-ApplyFixture
            $arguments = $f.arguments
            $foreign = Join-Path $f.fixture.root $target
            $preflightImplementation = (Get-Command Assert-YLResourceMigrationApplyState).ScriptBlock
            & {
                $observer = @{ checked = 0 }
                function Assert-YLResourceMigrationApplyState {
                    param($Repository, $Manifest, $ReceiptDirectory, $ManifestSha256)
                    $completed = & $preflightImplementation @PSBoundParameters
                    if (++$observer.checked -eq 1) { [IO.File]::WriteAllText($foreign, 'new content after preflight') }
                    return ,$completed
                }
                Assert-Rejected { Invoke-YLResourceMigrationApply @arguments } 'SHA|GUID|conflict'
            }
            Assert-That ([IO.File]::ReadAllText($foreign) -ceq 'new content after preflight' -and
                -not [IO.File]::Exists((Join-Path $arguments.ReceiptDirectory '000000.json')) -and
                -not [IO.Path]::Exists((Join-Path $f.fixture.root 'Assets/AddressableAssetsData'))) 'A preflight conflict permitted a subsequent write or mutated unknown bytes.'
        }
    }

    Test-Case 'CreateNew preserves an Add target claimed after its immediate check and stops the remaining batch' {
        $f = New-ApplyFixture
        $arguments = $f.arguments
        $fileStateImplementation = (Get-Command Assert-YLResourceMigrationFileState).ScriptBlock
        $add = 'Assets/AddressableAssetsData/New.asset'
        & {
            $observer = @{ addChecks = 0 }
            function Assert-YLResourceMigrationFileState {
                param($Root, $Operation, [switch] $Output)
                & $fileStateImplementation @PSBoundParameters
                if (-not $Output -and $Root -ceq $f.fixture.root -and $Operation.path -ceq $add -and ++$observer.addChecks -eq 4) {
                    $path = Join-Path $Root $Operation.path
                    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
                    [IO.File]::WriteAllText($path, 'racing Add owner')
                }
            }
            Assert-Rejected { Invoke-YLResourceMigrationApply @arguments } 'already exists|已存在'
            Assert-That ($observer.addChecks -eq 4) 'The race did not happen at the actual Add handle acquisition.'
        }
        Assert-That ([IO.File]::ReadAllText((Join-Path $f.fixture.root $add)) -ceq 'racing Add owner' -and
            [IO.File]::Exists((Join-Path $arguments.ReceiptDirectory '000000.json')) -and
            -not [IO.File]::Exists((Join-Path $arguments.ReceiptDirectory '000001.json')) -and
            [IO.File]::Exists((Join-Path $f.fixture.root 'Assets/Catalog/Project.asset'))) 'The Add race lost the foreign file, proven prefix or remaining source.'
    }

    Test-Case 'Two real Apply processes cannot enter the same repository write window' {
        $f = New-ApplyFixture
        $arguments = $f.arguments
        $cli = Join-Path $PSScriptRoot '../Invoke-YLResourceMigration.ps1'
        $otherReceipts = Join-Path $f.artifact 'competing-receipts'
        $pwsh = Join-Path $PSHOME 'pwsh.exe'
        $preflightImplementation = (Get-Command Assert-YLResourceMigrationApplyState).ScriptBlock
        & {
            $observer = @{ ran = $false }
            function Assert-YLResourceMigrationApplyState {
                param($Repository, $Manifest, $ReceiptDirectory, $ManifestSha256)
                if (-not $observer.ran) {
                    $observer.ran = $true
                    $output = & $pwsh -NoLogo -NoProfile -NonInteractive -File $cli -Action Apply -Repository $Repository -Manifest $arguments.Manifest `
                        -ManifestSha256 $ManifestSha256 -ValidationReceipt $arguments.ValidationReceipt -ValidationSha256 $arguments.ValidationSha256 `
                        -ReceiptDirectory $otherReceipts -ExclusiveWindowConfirmed 2>&1
                    Assert-That ($LASTEXITCODE -ne 0 -and ($output -join "`n") -match 'Another migration Apply is already using') 'Two processes entered the same source mutex or the competing invocation failed elsewhere.'
                    Assert-That (-not [IO.Directory]::Exists($otherReceipts)) 'The second Apply acquired a receipt directory.'
                }
                & $preflightImplementation @PSBoundParameters
            }
            $result = Invoke-YLResourceMigrationApply @arguments
            Assert-That ($observer.ran -and $result.completed -eq 4) 'The first Apply did not complete.'
        }
    }

    Test-Case 'Apply rejects a cancelled or observer-failed candidate process even if its exit code is zero' {
        $f = New-ApplyFixture
        $arguments = $f.arguments
        $proof = [IO.File]::ReadAllText($arguments.ValidationReceipt) | ConvertFrom-Json -AsHashtable
        $proof.process.cancelled = $true
        [IO.File]::WriteAllText($arguments.ValidationReceipt, (ConvertTo-Json -InputObject $proof -Depth 40))
        $arguments.ValidationSha256 = Get-YLResourceMigrationHash $arguments.ValidationReceipt
        Assert-Rejected { Invoke-YLResourceMigrationApply @arguments } 'process|outcome|cancel|ordinary'
        Assert-That ([Convert]::ToBase64String([IO.File]::ReadAllBytes($f.fixture.asset)) -ceq 'AAH/Cg==') 'Invalid process proof permitted source writes.'
    }

    Test-Case 'Apply rejects a receipt for another manifest even when the output operations are identical' {
        $f = New-ApplyFixture
        $arguments = $f.arguments
        $f.data['sourceCommit'] = 'f' * 40
        $differentManifest = Join-Path $f.artifact 'different-manifest.json'
        Write-YLTestJson -Path $differentManifest -Value $f.data
        $arguments.Manifest = $differentManifest
        $arguments.ManifestSha256 = Get-YLResourceMigrationHash $differentManifest
        Assert-Rejected { Invoke-YLResourceMigrationApply @arguments } 'validation.*(manifest|identity|exact)'
        Assert-That (-not [IO.Directory]::Exists($arguments.ReceiptDirectory)) 'The wrong manifest created Apply receipts.'
    }

    Test-Case 'Apply refuses a conversion-only receipt even when all three native checks pass' {
        $f = New-ApplyFixture
        $f.data.retirement.deleteEnabled = $false
        $f.data.operations = @($f.data.operations | Where-Object action -CNE Delete)
        $f.data.outputDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $f.data.operations -Depth 40 -Compress)))).ToLowerInvariant()
        $path = Join-Path $f.artifact 'conversion.json'
        Write-YLTestJson $path $f.data
        $manifestSha = Get-YLResourceMigrationHash $path
        $arguments = $f.arguments
        $validation = [IO.File]::ReadAllText($arguments.ValidationReceipt) | ConvertFrom-Json -AsHashtable
        $native = [IO.File]::ReadAllText($validation.nativeProof) | ConvertFrom-Json -AsHashtable
        foreach ($document in @($native, $validation)) {
            $document.manifestSha256 = $manifestSha
            $document.outputDigest = $f.data.outputDigest
        }
        [IO.File]::WriteAllText($validation.nativeProof, (ConvertTo-Json $native -Depth 40))
        $validation.nativeProofSha256 = Get-YLResourceMigrationHash $validation.nativeProof
        [IO.File]::WriteAllText($arguments.ValidationReceipt, (ConvertTo-Json $validation -Depth 40))
        $arguments.Manifest = $path
        $arguments.ManifestSha256 = $manifestSha
        $arguments.ValidationSha256 = Get-YLResourceMigrationHash $arguments.ValidationReceipt
        Assert-Rejected { Invoke-YLResourceMigrationApply @arguments } 'conversion.only|selected retirement.*final'
        Assert-That (-not [IO.Directory]::Exists($arguments.ReceiptDirectory)) 'A conversion-only candidate opened an Apply window.'
    }

    Test-Case 'Apply rejects duplicate or missing native retirement evidence even with a valid proof SHA' {
        $f = New-ApplyFixture
        $arguments = $f.arguments
        $validation = [IO.File]::ReadAllText($arguments.ValidationReceipt) | ConvertFrom-Json -AsHashtable
        $native = [IO.File]::ReadAllText($validation.nativeProof) | ConvertFrom-Json -AsHashtable
        $row = @{ path = $f.data.retirement.entries[0].path; guid = $f.data.retirement.entries[0].guid
            kind = 'Project'; originalSha256 = $f.data.operations[2].inputSha256; absent = $true; passed = $true }
        $native['retired'] = @($row, $row)
        [IO.File]::WriteAllText($validation.nativeProof, (ConvertTo-Json $native -Depth 40))
        $validation.nativeProofSha256 = Get-YLResourceMigrationHash $validation.nativeProof
        [IO.File]::WriteAllText($arguments.ValidationReceipt, (ConvertTo-Json $validation -Depth 40))
        $arguments.ValidationSha256 = Get-YLResourceMigrationHash $arguments.ValidationReceipt
        Assert-Rejected { Invoke-YLResourceMigrationApply @arguments } 'native retirement.*incomplete|duplicate native retirement'
        Assert-That (-not [IO.Directory]::Exists($arguments.ReceiptDirectory)) 'Incomplete retirement evidence opened an Apply window.'
    }

    Test-Case 'Apply rejects foreign subject/profile/proof identities and malformed success fields before writing' {
        $changed = [ordered]@{
            hostId = ('0' * 32); inputHash = ('0' * 64); unityVersion = '2022.3.62f3'; addressablesVersion = '1.25.0'
            validationHostId = ('0' * 32); validationResultId = ('0' * 32); validationInputHash = ('0' * 64); nativeProofSha256 = ('0' * 64)
            runtimeVerified = 'true'; checks = @('OfficialContentLoad', 'ConsumerColdRead', 'ForeignSettingsPreserved', 'OfficialContentLoad')
        }
        foreach ($change in $changed.GetEnumerator()) {
            $f = New-ApplyFixture
            $arguments = $f.arguments
            $proof = [IO.File]::ReadAllText($arguments.ValidationReceipt) | ConvertFrom-Json -AsHashtable
            $proof[$change.Key] = $change.Value
            [IO.File]::WriteAllText($arguments.ValidationReceipt, (ConvertTo-Json -InputObject $proof -Depth 40))
            $arguments.ValidationSha256 = Get-YLResourceMigrationHash $arguments.ValidationReceipt
            Assert-Rejected { Invoke-YLResourceMigrationApply @arguments } 'validation|native proof'
            Assert-That (-not [IO.Directory]::Exists($arguments.ReceiptDirectory)) "A wrong $($change.Key) permitted source writes."
        }
    }

    Test-Case 'Apply refuses source metadata drift and unknown missing deletes instead of assuming completion' {
        $f = New-ApplyFixture
        $arguments = $f.arguments
        [IO.File]::WriteAllText(($f.fixture.asset + '.meta'), "guid: 99999999999999999999999999999999`n")
        Assert-Rejected { Invoke-YLResourceMigrationApply @arguments } 'SHA|GUID|conflict'
        $f = New-ApplyFixture
        $arguments = $f.arguments
        [IO.File]::Delete((Join-Path $f.fixture.root 'Assets/Catalog/Project.asset'))
        Assert-Rejected { Invoke-YLResourceMigrationApply @arguments } 'SHA|conflict'
        Assert-That (-not [IO.File]::Exists((Join-Path $arguments.ReceiptDirectory '000000.json'))) 'Unknown absent delete permitted earlier operations.'
    }
    Test-Case 'Owned candidate packaging freezes Add outputs without changing source or input receipts' {
        $f = New-PreviewFixture
        $arguments = $f.arguments
        $preview = New-YLResourceMigrationPreview @arguments
        $workspace = Initialize-YLTestWorkspace -WorkspaceRoot (Join-Path $sandbox ('workspace-' + [Guid]::NewGuid().ToString('N'))) -Repository $f.fixture.root
        $hostRecord = New-YLTestDirectory -Workspace $workspace -Kind Host -Key 'MigrationFixture'
        $result = New-YLTestDirectory -Workspace $workspace -Kind Result -Key 'MigrationFixture'
        $sources = Get-YLResourceSnapshotInputs -Repository $f.fixture.root -InputPaths @('Assets/Catalog')
        $paths = @($sources.files | ForEach-Object { [IO.Path]::GetRelativePath($f.fixture.root, $_).Replace('\', '/') })
        $identity = Get-YLTestInputIdentity -Repository $f.fixture.root -Paths $paths -Profile 'ResourceMigrationFixture'
        $files = [ordered]@{}
        foreach ($path in $paths) { $files[$path] = [IO.File]::ReadAllBytes((Join-Path $f.fixture.root $path)) }
        Sync-YLUnityHost -HostRecord $hostRecord -Files $files -Identity $identity -EditorVersion '6000.3.27f1'
        $inputReceipt = Get-YLResourceMigrationHash (Join-Path $hostRecord.path '.yl-inputs.json')
        $settings = Join-Path $hostRecord.path $preview.settingsPath
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($settings))
        [IO.File]::WriteAllText($settings, 'official configuration fixture')
        [IO.File]::WriteAllText(($settings + '.meta'), "guid: 55555555555555555555555555555555`n")
        [IO.File]::WriteAllText((Join-Path $hostRecord.path ($preview.settingsRoot + '.meta')), "guid: 66666666666666666666666666666666`n")
        $manifest = New-YLResourceCandidateManifest -Preview $preview -PreviewSha256 ('d' * 64) -HostRecord $hostRecord -Result $result.path -SourceIdentity $identity -UnityVersion '6000.3.27f1' -AddressablesVersion '2.10.3'
        $data = Read-YLResourceMigrationManifest -Repository $f.fixture.root -Manifest $manifest -ManifestSha256 (Get-YLResourceMigrationHash $manifest)
        Assert-That ($data.operations.Count -eq 3 -and @($data.operations | Where-Object { $_.action -cne 'Add' -or $_.expectedAbsent -ne $true }).Count -eq 0) 'The official output operation set was guessed or incomplete.'
        Assert-That ($data.status -ceq 'AwaitingNativeRuntimeValidation' -and $data.runtimeVerified -eq $false) 'Packaging fabricated native runtime validation.'
        Assert-That ($data.hostId -ceq $hostRecord.id -and $data.inputHash -ceq $identity.sha256 -and $data.unityVersion -ceq '6000.3.27f1' -and $data.addressablesVersion -ceq '2.10.3') 'Candidate packaging lost the exact subject profile.'
        Assert-That ((Get-YLResourceMigrationHash (Join-Path $hostRecord.path '.yl-inputs.json')) -ceq $inputReceipt) 'Candidate packaging forged a managed input receipt.'
        Assert-That (-not [IO.Directory]::Exists((Join-Path $f.fixture.root $preview.settingsRoot))) 'Packaging wrote the candidate into source.'
        $final = New-YLTestDirectory -Workspace $workspace -Kind Result -Key 'FinalMigrationFixture'
        $finalPath = New-YLResourceCandidateManifest -Preview $preview -PreviewSha256 ('d' * 64) -HostRecord $hostRecord -Result $final.path -SourceIdentity $identity -UnityVersion '6000.3.27f1' -AddressablesVersion '2.10.3' -IncludeRetirement
        $frozen = Read-YLResourceMigrationManifest -Repository $f.fixture.root -Manifest $finalPath -ManifestSha256 (Get-YLResourceMigrationHash $finalPath)
        Assert-That ($frozen.retirement.deleteEnabled -and -not $preview.retirement.deleteEnabled -and
            @($frozen.operations | Where-Object action -CEQ Delete).Count -eq $preview.retirement.entries.Count -and
            $frozen.runtimeVerified -eq $false) 'Preparing the final candidate did not freeze the exact retirement, or it altered Preview or claimed native proof.'
    }

    Test-Case 'CLI Preview is read-only and Export refuses an unpinned mutable legacy exporter before launching Unity' {
        $f = New-PreviewFixture
        $selection = Join-Path $sandbox ('selection-' + [Guid]::NewGuid().ToString('N') + '.json')
        $output = Join-Path $sandbox ('preview-' + [Guid]::NewGuid().ToString('N') + '.json')
        Write-YLTestJson -Path $selection -Value @{ schemaVersion = 1; projectGuids = @('3' * 32); allowedRoots = @('Assets/Catalog'); settingsPath = $f.arguments.SettingsPath }
        $entry = Join-Path $PSScriptRoot '../Invoke-YLResourceMigration.ps1'
        $json = & $entry -Action Preview -Repository $f.fixture.root -Snapshot $f.fixture.destination -Inventory $f.inventoryPath -InventorySha256 $f.arguments.InventorySha256 -SelectionFile $selection -PreviewFile $output
        $preview = ($json -join [Environment]::NewLine) | ConvertFrom-Json -AsHashtable
        Assert-That ($preview.status -ceq 'ReadyForCandidate' -and [IO.File]::Exists($output)) 'CLI did not expose the safe Preview contract.'
        Assert-That (-not [IO.Directory]::Exists((Join-Path $f.fixture.root $preview.settingsRoot))) 'CLI Preview wrote official configuration into source.'
        Assert-Rejected { Invoke-YLResourceLegacyExport -Repository $f.fixture.root -Snapshot $f.fixture.destination -Unity (Join-Path $sandbox 'not-unity.exe') -WorkspaceRoot (Join-Path $sandbox 'must-not-create') } 'preserved|exporter|pinned'
        Assert-That (-not [IO.Directory]::Exists((Join-Path $sandbox 'must-not-create'))) 'Export created a host before authenticating the archived exporter.'
    }
} finally {
    $null = Get-YLTestPath $sandbox
    [IO.Directory]::Delete($sandbox, $true)
}

"Resource migration checks: $($script:cases - $failures.Count)/$script:cases passed."
if ($failures.Count -gt 0) { throw ($failures -join [Environment]::NewLine) }
