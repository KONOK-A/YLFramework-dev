#requires -Version 7.6
#requires -PSEdition Core
. (Join-Path $PSScriptRoot 'Workspace.ps1')
. (Join-Path $PSScriptRoot 'Results.ps1')

function Get-YLResourceMigrationPath {
    param([string] $Root, [string] $RelativePath)
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath.Contains(':') -or $RelativePath -match '(^|[\\/])\.{1,2}([\\/]|$)') {
        throw 'A non-empty repository-relative path without traversal is required.'
    }
    $rootPath = Get-YLTestPath $Root
    $path = Get-YLTestPath (Join-Path $rootPath $RelativePath)
    if (-not $path.StartsWith($rootPath + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Migration path is outside its root.'
    }
    return $path
}

function Get-YLResourceSnapshotInputs {
    param([string] $Repository, [string[]] $InputPaths)
    $Repository = Get-YLTestPath $Repository
    if (-not [IO.Directory]::Exists($Repository)) { throw 'Missing source repository.' }
    if ($InputPaths.Count -eq 0) { throw 'Snapshot inputs cannot be empty.' }
    $files = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $directories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $pending = [Collections.Generic.Stack[string]]::new()
    foreach ($relative in $InputPaths) {
        if ($relative -match '^(?i:Library|Temp|obj|Logs|UserSettings|LocalArchives|\.git|\.kilo)([\\/]|$)') {
            throw "Excluded snapshot input: $relative"
        }
        $path = Get-YLResourceMigrationPath $Repository $relative
        if (-not [IO.Path]::Exists($path)) { throw "Missing snapshot input: $relative" }
        $pending.Push($path)
        for ($cursor = $path; $cursor -ine $Repository; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
            if ([IO.Directory]::Exists($cursor)) { $null = $directories.Add($cursor) }
            if ([IO.File]::Exists($cursor + '.meta')) { $null = $files.Add((Get-YLTestPath ($cursor + '.meta'))) }
        }
    }
    while ($pending.Count -gt 0) {
        $path = Get-YLTestPath ($pending.Pop())
        if ([IO.Directory]::Exists($path)) {
            $null = $directories.Add($path)
            foreach ($child in [IO.Directory]::EnumerateFileSystemEntries($path)) { $pending.Push($child) }
        } else { $null = $files.Add($path) }
    }
    foreach ($file in $files) {
        $leaf = [IO.Path]::GetFileName($file)
        if ($leaf -match '(?i)(\.(pfx|p12|pem|key|keystore|jks)$|\.private($|\.)|^\.env($|\.)|^(credentials|secrets)\.json$)') {
            throw "Sensitive snapshot input is refused: $([IO.Path]::GetRelativePath($Repository, $file))"
        }
    }
    return [pscustomobject]@{
        files = @($files | Sort-Object)
        directories = @($directories | Sort-Object)
    }
}

function New-YLResourceSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Repository,
        [Parameter(Mandatory)][string] $Destination,
        [Parameter(Mandatory)][string[]] $InputPaths,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $SourceCommit
    )
    $Repository = Get-YLTestPath $Repository
    $Destination = Get-YLTestPath $Destination
    $archiveRoot = Join-Path $Repository 'LocalArchives/ResourceLegacy'
    if (-not $Destination.StartsWith($archiveRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Snapshots must be below LocalArchives/ResourceLegacy, outside Unity and package inputs.'
    }
    if ([IO.Path]::Exists($Destination)) { throw "Preserve the existing snapshot destination: $Destination" }
    $inputs = Get-YLResourceSnapshotInputs -Repository $Repository -InputPaths $InputPaths
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Destination))
    $null = New-Item -ItemType Directory -Path $Destination -ErrorAction Stop
    $copyRoot = Join-Path $Destination 'Source'
    $null = [IO.Directory]::CreateDirectory($copyRoot)
    $folders = [Collections.Generic.List[string]]::new()
    foreach ($directory in $inputs.directories) {
        $relative = [IO.Path]::GetRelativePath($Repository, $directory).Replace('\', '/')
        $folders.Add($relative)
        $null = [IO.Directory]::CreateDirectory((Get-YLResourceMigrationPath $copyRoot $relative))
    }
    $entries = [Collections.Generic.List[object]]::new()
    foreach ($file in $inputs.files) {
        $sourcePath = Get-YLTestPath $file
        $relative = [IO.Path]::GetRelativePath($Repository, $sourcePath).Replace('\', '/')
        $target = Get-YLResourceMigrationPath $copyRoot $relative
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        $source = [IO.File]::Open($sourcePath, 'Open', 'Read', 'Read')
        try {
            $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($source)).ToLowerInvariant()
            $length = $source.Length
            $source.Position = 0
            $output = [IO.File]::Open($target, 'CreateNew', 'Write', 'None')
            try { $source.CopyTo($output); $output.Flush($true) }
            finally { $output.Dispose() }
            $entries.Add([ordered]@{ path = $relative; length = $length; sha256 = $hash })
        } finally { $source.Dispose() }
    }
    $manifest = [ordered]@{
        schemaVersion = 1
        purpose = 'YLResource.LegacySnapshot'
        repository = $Repository
        sourceCommit = $SourceCommit
        createdUtc = [DateTime]::UtcNow.ToString('o')
        inputPaths = @($InputPaths)
        directories = $folders.ToArray()
        files = $entries.ToArray()
    }
    Write-YLTestJson -Path (Join-Path $Destination 'snapshot.json') -Value $manifest
    $null = Test-YLResourceSnapshot -Path $Destination -CompareSource
    return [pscustomobject]$manifest
}

function Test-YLResourceSnapshot {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $Path, [switch] $CompareSource)
    $Path = Get-YLTestPath $Path
    $manifestPath = Get-YLTestPath (Join-Path $Path 'snapshot.json')
    $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json -AsHashtable -DateKind String
    if ($manifest.schemaVersion -ne 1 -or $manifest.purpose -cne 'YLResource.LegacySnapshot' -or
        $manifest.sourceCommit -cnotmatch '^[0-9a-f]{40}$') { throw 'Unknown snapshot identity.' }
    $repository = Get-YLTestPath $manifest.repository
    if (-not $Path.StartsWith((Join-Path $repository 'LocalArchives/ResourceLegacy') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Snapshot location is outside its recorded archive root.'
    }
    $copyRoot = Get-YLTestPath (Join-Path $Path 'Source')
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [long]$bytes = 0
    foreach ($entry in $manifest.files) {
        $file = Get-YLResourceMigrationPath $copyRoot $entry.path
        if (-not $expected.Add($file)) { throw 'Duplicate snapshot file.' }
        if ($entry.sha256 -cnotmatch '^[0-9a-f]{64}$' -or $entry.length -lt 0 -or -not [IO.File]::Exists($file)) {
            throw "Missing or invalid snapshot content: $($entry.path)"
        }
        if (([IO.FileInfo]::new($file)).Length -ne $entry.length -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine $entry.sha256) {
            throw "Snapshot content/hash mismatch: $($entry.path)"
        }
        $bytes += $entry.length
        if ($CompareSource) {
            $source = Get-YLResourceMigrationPath $repository $entry.path
            if (-not [IO.File]::Exists($source) -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ine $entry.sha256) {
                throw "Snapshot source changed: $($entry.path)"
            }
        }
    }
    $actual = @(Get-YLTestTreeFiles $copyRoot)
    if ($actual.Count -ne $expected.Count) { throw 'Snapshot file set contains missing or unrecorded files.' }
    foreach ($file in $actual) { if (-not $expected.Contains($file)) { throw 'Unexpected snapshot file.' } }
    foreach ($relative in $manifest.directories) {
        if (-not [IO.Directory]::Exists((Get-YLResourceMigrationPath $copyRoot $relative))) { throw 'Missing snapshot directory.' }
    }
    if ($CompareSource) {
        $current = Get-YLResourceSnapshotInputs -Repository $repository -InputPaths $manifest.inputPaths
        $sourceFiles = @($current.files | ForEach-Object { [IO.Path]::GetRelativePath($repository, $_).Replace('\', '/') })
        if (@(Compare-Object -ReferenceObject @($manifest.files.path) -DifferenceObject $sourceFiles).Count -ne 0) {
            throw 'Snapshot source file set changed.'
        }
        $sourceFolders = @($current.directories | ForEach-Object { [IO.Path]::GetRelativePath($repository, $_).Replace('\', '/') })
        if (@(Compare-Object -ReferenceObject @($manifest.directories) -DifferenceObject $sourceFolders).Count -ne 0) {
            throw 'Snapshot source directory set changed.'
        }
    }
    return [pscustomobject]@{
        status = 'Verified'; path = $Path; sourceCommit = $manifest.sourceCommit
        fileCount = $expected.Count; bytes = $bytes; comparedSource = [bool]$CompareSource
        manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Get-YLResourceMigrationHash([string] $Path) {
    $Path = Get-YLTestPath $Path
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant()
}

function Get-YLResourceMigrationGuid([string] $Path) {
    $Path = Get-YLTestPath $Path
    $matches = [regex]::Matches([IO.File]::ReadAllText($Path), '(?m)^guid: ([0-9a-f]{32})\r?$')
    if ($matches.Count -ne 1) { throw "Invalid or ambiguous metadata GUID: $Path" }
    return $matches[0].Groups[1].Value
}

function Read-YLResourceMigrationInventory {
    param([string] $Snapshot, [string] $Inventory, [string] $InventorySha256)
    $verified = Test-YLResourceSnapshot -Path $Snapshot
    $Snapshot = Get-YLTestPath $Snapshot
    $Inventory = Get-YLTestPath $Inventory
    if (-not $Inventory.StartsWith((Join-Path $Snapshot 'Inventory') + '\', [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($Inventory) -cne 'inventory.json') { throw 'Use a preserved inventory below this snapshot Inventory directory.' }
    if ($InventorySha256 -cnotmatch '^[0-9a-f]{64}$' -or (Get-YLResourceMigrationHash $Inventory) -cne $InventorySha256) {
        throw 'Certified inventory SHA/hash mismatch.'
    }
    $data = [IO.File]::ReadAllText($Inventory) | ConvertFrom-Json -AsHashtable -DateKind String
    $audit = [IO.File]::ReadAllText((Join-Path ([IO.Path]::GetDirectoryName($Inventory)) 'audit.json')) | ConvertFrom-Json -AsHashtable -DateKind String
    if ($data.schemaVersion -ne 1 -or $data.purpose -cne 'YLResource.LegacyInventory' -or
        $data.snapshotManifestSha256 -cne $verified.manifestSha256 -or
        $data.hostId -cnotmatch '^[0-9a-f]{32}$' -or $data.inputHash -cnotmatch '^[0-9a-f]{64}$' -or
        $audit.purpose -cne 'YLResource.LegacyInventoryAudit' -or $audit.status -cne 'Verified' -or
        $audit.sourceGuidsPreserved -ne $true -or $audit.inventorySha256 -cne $InventorySha256 -or
        $audit.assets -ne $data.assets.Count -or $audit.projects.Count -ne $data.projects.Count -or
        $audit.objects -ne @($data.assets | ForEach-Object { if ($_.Contains('objects')) { $_.objects } }).Count) {
        throw 'Inventory audit/schema/snapshot identity mismatch.'
    }
    foreach ($project in $data.projects) {
        $rows = @($audit.projects | Where-Object { $_.path -ceq $project.assetPath -and $_.projectId -ceq $project.projectId })
        if ($rows.Count -ne 1 -or $rows[0].status -cne $project.status -or $rows[0].assets -ne $project.assets.Count) {
            throw 'Inventory project audit mismatch.'
        }
    }
    return $data
}

function Get-YLResourceSupplementFile {
    param([string] $Root, [string] $RelativePath, [string] $Sha256)
    if ($RelativePath.Contains('\') -or $RelativePath.Contains('//') -or $RelativePath.EndsWith('/')) { throw 'Supplement payload requires a canonical relative path.' }
    $path = Get-YLResourceMigrationPath $Root $RelativePath
    if (-not [IO.File]::Exists($path) -or ($Sha256 -and ($Sha256 -cnotmatch '^[0-9a-f]{64}$' -or (Get-YLResourceMigrationHash $path) -cne $Sha256))) { throw "Supplement payload SHA mismatch: $RelativePath" }
    return $path
}

function Read-YLResourceMigrationSupplement {
    param([string] $Repository, [string] $Snapshot, [Collections.IDictionary] $InventoryData, [string] $InventorySha256, [string] $SupplementPath, [string] $SupplementSha256)
    $SupplementPath = Get-YLTestPath $SupplementPath
    if ([IO.Path]::GetFileName($SupplementPath) -cne 'recovery-index.json' -or $SupplementSha256 -cnotmatch '^[0-9a-f]{64}$' -or (Get-YLResourceMigrationHash $SupplementPath) -cne $SupplementSha256) { throw 'Supplement index SHA/hash or filename mismatch.' }
    $root = [IO.Path]::GetDirectoryName($SupplementPath)
    $workspaceRoot = [IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($root)))
    $workspace = [pscustomobject]([IO.File]::ReadAllText((Get-YLTestPath (Join-Path $workspaceRoot '.yl-workspace.json'))) | ConvertFrom-Json -AsHashtable)
    if ($workspace.repository -ine $Repository -or $workspace.root -ine $workspaceRoot) { throw 'Supplement workspace identity mismatch.' }
    $owner = Get-YLTestDirectory -Workspace $workspace -Path $root
    if ($owner.kind -cne 'Result' -or $owner.key -cne 'ResourceLegacyAtlasGenerate') { throw 'Supplement is not an owned legacy Atlas evidence result.' }
    $pins = [Collections.Generic.SortedDictionary[string, string]]::new([StringComparer]::Ordinal)
    function Read-SupplementJson([string] $Name, [string] $ExpectedHash = '') {
        $path = Get-YLResourceSupplementFile $root $Name $ExpectedHash
        $hash = Get-YLResourceMigrationHash $path
        $pins[$Name] = $hash
        return [IO.File]::ReadAllText($path) | ConvertFrom-Json -AsHashtable -DateKind String
    }
    $index = Read-SupplementJson 'recovery-index.json' $SupplementSha256
    $native = Read-SupplementJson 'atlas-evidence.json' $index.evidenceSha256
    $stage = Read-SupplementJson 'stage-manifest.json' $index.stageManifestSha256
    $process = Read-SupplementJson 'process-files.json' $index.processFilesSha256
    $facts = Read-SupplementJson 'original-facts.json'
    $request = Read-SupplementJson 'request.json'
    $summary = Read-SupplementJson 'summary.json'
    $exit = Read-SupplementJson 'process.log.exit.json'
    $hostPath = Get-YLTestPath $summary.host
    $hostOwner = Get-YLTestDirectory -Workspace $workspace -Path $hostPath
    $hostInputs = [IO.File]::ReadAllText((Get-YLTestPath (Join-Path $hostPath '.yl-inputs.json'))) | ConvertFrom-Json -AsHashtable
    Assert-YLTestProcessSucceeded ([pscustomobject]$summary.process)
    Assert-YLTestProcessSucceeded ([pscustomobject]$exit)
    if ($index.schemaVersion -ne 1 -or $index.purpose -cne 'YLResource.LegacyAtlasRecoveryIndex' -or $index.status -cne 'DerivedFourAtlasProjectsResolved' -or $index.applied -ne $false -or $index.officialRuntimeVerified -ne $false -or
        $index.snapshotManifestSha256 -cne $InventoryData.snapshotManifestSha256 -or $index.inventorySha256 -cne $InventorySha256 -or $index.exactToolInputsStillMatch -ne $true -or
        $native.schemaVersion -ne 1 -or $native.purpose -cne 'YLResource.LegacyAtlasDerivedEvidence' -or $native.status -cne 'GeneratedAndResolved' -or $native.applied -ne $false -or $native.officialRuntimeVerified -ne $false -or $native.snapshotManifestSha256 -cne $index.snapshotManifestSha256 -or $native.inventorySha256 -cne $InventorySha256 -or
        $facts.purpose -cne 'YLResource.LegacyAtlasOriginalFacts' -or $facts.snapshotManifestSha256 -cne $index.snapshotManifestSha256 -or $facts.inventorySha256 -cne $InventorySha256 -or
        $stage.purpose -cne 'YLResource.LegacyAtlasStage' -or $stage.originalSnapshotManifestSha256 -cne $index.snapshotManifestSha256 -or $stage.inputHash -cne $index.nativeInputHash -or
        $process.purpose -cne 'YLResource.LegacyAtlasProcessFiles' -or $process.applied -ne $false -or $summary.status -cne 'GeneratedAndResolved' -or $summary.originalArchiveUnchanged -ne $true -or $summary.applied -ne $false -or
        $request.purpose -cne 'YLResource.LegacyAtlasEvidence' -or $request.action -cne 'Generate' -or $request.sourceRepository -ine $Repository -or $request.output -ine $root -or $summary.result -ine $root -or
        $owner.id -cne $request.resultId -or $workspace.id -cne $request.workspaceId -or $hostOwner.kind -cne 'Host' -or $hostOwner.id -cne $request.hostId -or $hostOwner.id -cne $native.hostId -or $hostOwner.id -cne $summary.hostId -or
        $hostInputs.hostId -cne $hostOwner.id -or $hostInputs.inputHash -cne $index.nativeInputHash -or $native.inputHash -cne $index.nativeInputHash -or $request.inputHash -cne $index.nativeInputHash -or
        $request.stageManifestSha256 -cne $index.stageManifestSha256 -or $request.inventorySha256 -cne $InventorySha256 -or $request.snapshotManifestSha256 -cne $index.snapshotManifestSha256 -or
        $summary.process.invocationId -cne $exit.invocationId -or $native.spritePackerModeValue -ne 5 -or $request.expectedSpritePackerMode -ne 5 -or $native.activeBuildTarget -cne 'StandaloneWindows64') { throw 'Supplement native evidence/schema/owner identity mismatch.' }
    $snapshotData = [IO.File]::ReadAllText((Join-Path $Snapshot 'snapshot.json')) | ConvertFrom-Json -AsHashtable
    $original = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($row in $snapshotData.files) { $original.Add($row.path, $row) }
    function Assert-SupplementOriginal([string] $Path, [object] $Hash) {
        $null = Get-YLResourceMigrationPath $Repository $Path
        if ($original.ContainsKey($Path)) { if ($Hash -cne $original[$Path].sha256) { throw "Supplement original Source SHA was replaced: $Path" } }
        elseif ($null -ne $Hash) { throw "Supplement claims unarchived original Source SHA: $Path" }
    }
    function Check-OriginalFact([object] $Node) {
        if ($Node -is [Collections.IDictionary]) {
            if ($Node.Contains('originalSha256') -and $Node.Contains('path')) { Assert-SupplementOriginal $Node.path $Node.originalSha256 }
            foreach ($value in $Node.Values) { Check-OriginalFact $value }
        } elseif ($Node -is [array]) { foreach ($value in $Node) { Check-OriginalFact $value } }
    }
    Check-OriginalFact $facts
    $staged = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    $beforeGeneration = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($file in $stage.files) {
        Assert-SupplementOriginal $file.path $file.originalSha256
        if ($file.stagedSha256 -cnotmatch '^[0-9a-f]{64}$' -or $file.overlay -ne ($file.originalSha256 -cne $file.stagedSha256)) { throw 'Invalid Supplement staged SHA/overlay provenance.' }
        $staged.Add($file.path, $file)
        $beforeGeneration.Add($file.path, $file.stagedSha256)
        if ($file.originalSha256) { $null = Get-YLResourceSupplementFile $root ('Original/' + $file.path) $file.originalSha256 }
    }
    $phases = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $normalizations = [Collections.Generic.List[object]]::new()
    foreach ($phase in @(@{ name = 'ImportedBeforeGeneration'; rows = @($native.importChanges) }, @{ name = 'GeneratedAfterGeneration'; rows = @($native.generationChanges) })) {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($row in $phase.rows) {
            $null = Get-YLResourceMigrationPath $Repository $row.path
            if (-not $seen.Add($row.path)) { throw 'Duplicate Supplement phase path.' }
            $expectedBefore = if ($beforeGeneration.ContainsKey($row.path)) { $beforeGeneration[$row.path] } else { $null }
            $stagedOriginal = if ($staged.ContainsKey($row.path)) { $staged[$row.path].originalSha256 } else { $null }
            if ($row.originalSourceSha256 -cne $stagedOriginal) { throw 'Supplement phase original/staged provenance mismatch.' }
            if ($row.phase -cne $phase.name -or $row.beforePhaseSha256 -cne $expectedBefore -or $row.preservedAfterBytes -cne ($phase.name + '/' + $row.path) -or $row.action -cnotin @('Add', 'Modify', 'Delete')) { throw 'Supplement phase/path/before hash mismatch.' }
            if ($row.afterPhaseSha256) {
                $payload = Get-YLResourceSupplementFile $root $row.preservedAfterBytes $row.afterPhaseSha256
                $pins[$row.preservedAfterBytes] = $row.afterPhaseSha256
                $beforeGeneration[$row.path] = $row.afterPhaseSha256
                if ($phase.name -ceq 'ImportedBeforeGeneration' -and $row.path.StartsWith('Assets/', [StringComparison]::Ordinal) -and $row.path.EndsWith('.meta', [StringComparison]::Ordinal) -and $original.ContainsKey($row.path)) {
                    $guid = Get-YLResourceMigrationGuid $payload
                    if ($guid -cne (Get-YLResourceMigrationGuid (Get-YLResourceSupplementFile $root ('Original/' + $row.path) $original[$row.path].sha256))) { throw 'Supplement importer changed an original GUID.' }
                    $normalizations.Add(@{ path = $row.path; sha256 = $row.afterPhaseSha256; guid = $guid; payload = $row.preservedAfterBytes; originalSha256 = $row.originalSourceSha256 })
                }
            } else { $null = $beforeGeneration.Remove($row.path) }
            if ($phase.name -ceq 'GeneratedAfterGeneration') { $phases.Add($row.path, $row) }
        }
    }
    $final = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($row in $process.finalFiles) { $null = Get-YLResourceMigrationPath $Repository $row.path; if ($row.finalSha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid Supplement final SHA.' }; $final.Add($row.path, $row.finalSha256) }
    $changed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($row in $process.changes) {
        if (-not $changed.Add($row.path)) { throw 'Duplicate Supplement process change.' }
        $stagedOriginal = if ($staged.ContainsKey($row.path)) { $staged[$row.path].originalSha256 } else { $null }
        if ($row.originalSourceSha256 -cne $stagedOriginal) { throw 'Supplement process original/staged provenance mismatch.' }
        $old = if ($staged.ContainsKey($row.path)) { $staged[$row.path].stagedSha256 } else { $null }
        $after = if ($final.ContainsKey($row.path)) { $final[$row.path] } else { $null }
        if ($row.stagedSha256 -cne $old -or $row.finalSha256 -cne $after) { throw 'Supplement process original/staged/final mismatch.' }
        if ($after) { $null = Get-YLResourceSupplementFile $root ('FinalAfterProcess/' + $row.path) $after; $pins['FinalAfterProcess/' + $row.path] = $after }
    }
    foreach ($path in $final.Keys) {
        if (-not $changed.Contains($path) -and (-not $staged.ContainsKey($path) -or $staged[$path].stagedSha256 -cne $final[$path])) { throw 'Unrecorded Supplement final file change.' }
    }
    foreach ($path in $staged.Keys) { if (-not $final.ContainsKey($path) -and -not $changed.Contains($path)) { throw 'Unrecorded Supplement process deletion.' } }
    $projects = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $overlays = [Collections.Generic.List[object]]::new()
    $allObjects = @($InventoryData.assets | ForEach-Object { if ($_.Contains('objects')) { $_.objects } })
    foreach ($project in $index.projects) {
        $base = @($InventoryData.projects | Where-Object { $_.guid -ceq $project.projectGuid })
        $fact = @($facts.projects | Where-Object { $_.projectGuid -ceq $project.projectGuid })
        $proof = @($native.projects | Where-Object { $_.projectGuid -ceq $project.projectGuid })
        if ($base.Count -ne 1 -or $fact.Count -ne 1 -or $proof.Count -ne 1) { throw 'Supplement Project identity is not unique in all evidence.' }
        $base = $base[0]; $fact = $fact[0]; $proof = $proof[0]
        if ($base.status -cne 'RejectedLegacyDefinition' -or $base.error -notmatch 'Automatic Atlases' -or $base.assetPath -cne $project.projectPath -or $base.projectId -cne $project.projectId -or
            $proof.status -cne 'GeneratedAndResolved' -or $proof.derivedProject.status -cne 'Resolved' -or $proof.afterPlan.needsGeneration -ne $false -or $project.currentPlan.needsGeneration -ne $false -or
            $proof.derivedProject.guid -cne $base.guid -or $proof.derivedProject.localId -ne $base.localId -or $proof.derivedProject.assetPath -cne $base.assetPath -or $project.ownershipVerifiedByArchivedPreview -ne $true) { throw 'Supplement cannot replace an original Project definition or its rejection.' }
        Assert-SupplementOriginal $base.assetPath $project.sourceProjectSha256
        Assert-SupplementOriginal ($base.assetPath + '.meta') $project.sourceProjectMetaSha256
        Assert-SupplementOriginal $project.configurationPath $project.sourceConfigurationSha256
        $configuration = @($allObjects | Where-Object { $_.guid -ceq $project.configurationGuid -and $_.assetPath -ceq $project.configurationPath -and $_.type.StartsWith('YLFramework.Editor.Resource.ResourceAtlasConfiguration,', [StringComparison]::Ordinal) })
        if ($configuration.Count -ne 1 -or $base.properties.automaticAtlases.value.guid -cne $project.configurationGuid -or $fact.configurationPath -cne $project.configurationPath -or
            (ConvertTo-Json -InputObject $project.originalRules -Depth 30 -Compress) -cne (ConvertTo-Json -InputObject $fact.rules -Depth 30 -Compress) -or
            (ConvertTo-Json -InputObject $project.nonAtlasSelections -Depth 30 -Compress) -cne (ConvertTo-Json -InputObject $proof.nonAtlasSelections -Depth 30 -Compress)) { throw 'Supplement original rules/selection projection mismatch.' }
        if ($fact.projectPath -cne $base.assetPath -or $fact.projectId -cne $base.projectId -or $fact.configurationGuid -cne $project.configurationGuid -or $project.derivedConfigurationSha256 -cne $final[$project.configurationPath]) { throw 'Supplement original/derived author identity mismatch.' }
        $config = $configuration[0]
        if ($config.properties.Contains('rules')) {
            if ($fact.rules.Count -ne $config.properties.rules.arraySize) { throw 'Supplement original rule count changed.' }
            for ($i = 0; $i -lt $fact.rules.Count; $i++) {
                $prefix = 'rules.Array.data[' + $i + ']'
                foreach ($name in @('id', 'groupId', 'address', 'grouping', 'allowRotation', 'tightPacking', 'padding', 'maxTextureSize', 'readable', 'mipMaps', 'srgb', 'filterMode', 'compression')) {
                    if ((ConvertTo-Json -InputObject $config.properties[$prefix + '.' + $name].value -Compress) -cne (ConvertTo-Json -InputObject $fact.rules[$i][$name] -Compress)) { throw 'Supplement changed an original Atlas rule field.' }
                }
                if ($fact.rules[$i].sources.Count -ne $config.properties[$prefix + '.sources'].arraySize) { throw 'Supplement original rule sources changed.' }
                for ($j = 0; $j -lt $fact.rules[$i].sources.Count; $j++) {
                    $reference = $config.properties[$prefix + '.sources.Array.data[' + $j + ']'].value
                    $source = $fact.rules[$i].sources[$j]
                    if ($source.guid -cne $reference.guid -or $source.localId -ne $reference.localId -or $source.path -cne $reference.assetPath) { throw 'Supplement changed original Atlas source identity.' }
                }
            }
        }
        if ($config.properties.Contains('outputs')) {
            if ($fact.originalOutputs.Count -ne $config.properties.outputs.arraySize) { throw 'Supplement original output records changed.' }
            for ($i = 0; $i -lt $fact.originalOutputs.Count; $i++) {
                foreach ($name in @('key', 'fileName', 'guid', 'inputHash', 'assetHash', 'metaHash')) {
                    if ($fact.originalOutputs[$i][$name] -cne $config.properties['outputs.Array.data[' + $i + '].' + $name].value) { throw 'Supplement changed an original ownership output stamp.' }
                }
            }
        }
        if ($config.properties.Contains('projectGuid')) {
            foreach ($name in @('projectGuid', 'generationParentGuid', 'directoryGuid', 'configurationGuid', 'configurationMetaHash', 'directoryMetaHash')) {
                if ($fact.ownership[$name] -cne $config.properties[$name].value) { throw 'Supplement changed an original ownership binding.' }
            }
        }
        $ordinary = @($proof.nonAtlasSelections | ForEach-Object { $_.expanded })
        $rows = [Collections.Generic.List[object]]::new()
        foreach ($asset in $project.assets) {
            $exported = @($proof.derivedProject.assets | Where-Object { $_.path -ceq $asset.path -and $_.guid -ceq $asset.guid -and $_.localId -eq $asset.localId })
            if ($exported.Count -ne 1 -or $exported[0].address -cne $asset.canonical -or $exported[0].legacyPath -cne $asset.legacyPath -or $exported[0].type -cne $asset.type -or $exported[0].group -cne $asset.group -or $exported[0].packing -cne $asset.packing -or $exported[0].selectionGuid -cne $asset.selectionGuid -or $exported[0].selectionPath -cne $asset.selectionPath -or
                (@($exported[0].aliases) -join "`n") -cne (@($asset.aliases) -join "`n")) { throw 'Supplement asset/canonical projection mismatch.' }
            Assert-SupplementOriginal $asset.path $asset.sourceOriginalSha256
            Assert-SupplementOriginal ($asset.path + '.meta') $asset.sourceOriginalMetaSha256
            if (-not $final.ContainsKey($asset.path) -or $final[$asset.path] -cne $asset.derivedSha256 -or $final[$asset.path + '.meta'] -cne $asset.derivedMetaSha256 -or $asset.expectedAbsentInOriginalSnapshot -ne (-not $original.ContainsKey($asset.path))) { throw 'Supplement asset original/derived/ExpectedAbsent mismatch.' }
            if ($asset.isGeneratedAtlas) {
                $bindings = @($proof.oldEditorAtlasBindings | Where-Object { $_.guid -ceq $asset.guid -and $_.path -ceq $asset.path -and $_.canonical -ceq $asset.canonical })
                if ($bindings.Count -ne 1 -or $bindings[0].spriteCount -ne $bindings[0].sourceSpriteCount -or $bindings[0].sprites.Count -eq 0 -or @($bindings[0].sprites | Where-Object { $_.found -ne $true -or $_.lookup -cne 'SpriteAtlas.GetSprite(exactSourceName)' -or $_.isPersistentAtlasSubassetIdentity -ne $false }).Count) { throw 'Supplement has no exact original GetSprite binding proof.' }
                foreach ($relative in @($asset.path, ($asset.path + '.meta'))) {
                    if (-not $phases.ContainsKey($relative) -or $phases[$relative].afterPhaseSha256 -cne $final[$relative]) { throw 'Atlas payload is not the exact certified GeneratedAfterGeneration material.' }
                    $originalGuid = if ($asset.sourceOriginalMetaSha256) { Get-YLResourceMigrationGuid (Get-YLResourceSupplementFile $root ('Original/' + $asset.path + '.meta') $asset.sourceOriginalMetaSha256) } else { $null }
                    $newGuid = Get-YLResourceMigrationGuid (Get-YLResourceSupplementFile $root ('GeneratedAfterGeneration/' + $asset.path + '.meta') $asset.derivedMetaSha256)
                    if ($newGuid -cne $asset.guid -or ($originalGuid -and $originalGuid -cne $newGuid)) { throw 'Supplement Atlas changed original GUID identity.' }
                    $overlays.Add([ordered]@{ path = $relative; sha256 = $final[$relative]; guid = $(if ($relative.EndsWith('.meta')) { $newGuid } else { $null }); originalSha256 = $(if ($original.ContainsKey($relative)) { $original[$relative].sha256 } else { $null }); expectedAbsent = -not $original.ContainsKey($relative); payload = 'GeneratedAfterGeneration/' + $relative; projectGuid = $base.guid })
                }
            } elseif (@($ordinary | Where-Object { $_.path -ceq $asset.path -and $_.guid -ceq $asset.guid -and $_.localId -eq $asset.localId -and $_.canonical -ceq $asset.canonical }).Count -ne 1) { throw 'Non-Atlas canonical was not cross-checked against original native expansion.' }
            $row = $exported[0].Clone()
            if ($asset.type.StartsWith('UnityEngine.U2D.SpriteAtlas,', [StringComparison]::Ordinal)) { $row.subAssets = @($asset.publicSubassets) }
            $rows.Add($row)
        }
        if ($rows.Count -ne $proof.derivedProject.assets.Count -or $ordinary.Count -ne @($project.assets | Where-Object { -not $_.isGeneratedAtlas }).Count) { throw 'Incomplete Supplement Project asset projection.' }
        $projects.Add($base.guid, @{ assets = $rows.ToArray(); fingerprintPaths = @($proof.derivedProject.fingerprintPaths); configuration = $configuration[0] })
    }
    if ($projects.Count -ne $native.projects.Count -or $projects.Count -ne $facts.projects.Count -or $projects.Count -ne $request.projectGuids.Count) { throw 'Supplement selected Project set is incomplete.' }
    foreach ($negative in $index.frozenNegatives) {
        $base = @($InventoryData.projects | Where-Object { $_.guid -ceq $negative.guid -and $_.assetPath -ceq $negative.path })
        $observed = @($native.frozenNegatives | Where-Object { $_.guid -ceq $negative.guid })
        if ($base.Count -ne 1 -or $observed.Count -ne 1 -or $observed[0].status -cne 'RejectedLegacyDefinition' -or $base[0].status -cne 'RejectedLegacyDefinition' -or $base[0].error -cne $negative.reason -or $negative.guid -cnotin $request.negativeGuids -or $projects.ContainsKey($negative.guid)) { throw 'Uncertified negative retirement definition.' }
        foreach ($path in @($negative.path, ($negative.path + '.meta'))) { if ($final[$path] -cne $original[$path].sha256) { throw 'A frozen negative original changed during generation.' } }
    }
    if ((ConvertTo-Json -InputObject $index.frozenNegatives -Depth 30 -Compress) -cne (ConvertTo-Json -InputObject $facts.frozenNegatives -Depth 30 -Compress)) { throw 'Negative original facts mismatch.' }
    return @{ path = $SupplementPath; sha256 = $SupplementSha256; root = $root; hostId = $hostOwner.id; resultId = $owner.id; pins = @($pins.Keys | ForEach-Object { @{ path = $_; sha256 = $pins[$_] } }); projects = $projects; overlays = $overlays.ToArray(); normalizations = $normalizations.ToArray(); negatives = @($index.frozenNegatives) }
}

function New-YLResourceMigrationPreview {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Repository,
        [Parameter(Mandatory)][string] $Snapshot,
        [Parameter(Mandatory)][string] $Inventory,
        [Parameter(Mandatory)][string] $InventorySha256,
        [Parameter(Mandatory)][string[]] $ProjectGuids,
        [Parameter(Mandatory)][string] $SettingsPath,
        [Parameter(Mandatory)][string[]] $AllowedRoots,
        [object[]] $ConsumerFields = @(),
        [string] $SupplementPath,
        [string] $SupplementSha256,
        [string[]] $RetireProjectGuids = @()
    )
    $Repository = Get-YLTestPath $Repository
    $Snapshot = Get-YLTestPath $Snapshot
    $data = Read-YLResourceMigrationInventory -Snapshot $Snapshot -Inventory $Inventory -InventorySha256 $InventorySha256
    $supplement = if ($SupplementPath) { Read-YLResourceMigrationSupplement -Repository $Repository -Snapshot $Snapshot -InventoryData $data -InventorySha256 $InventorySha256 -SupplementPath $SupplementPath -SupplementSha256 $SupplementSha256 } else { $null }
    if (-not $SupplementPath -and $SupplementSha256) { throw 'Supplement SHA requires an explicit SupplementPath.' }
    if ($ProjectGuids.Count -eq 0 -or $AllowedRoots.Count -eq 0) { throw 'Explicit selected Project GUIDs and approved asset roots are required.' }
    foreach ($root in $AllowedRoots) {
        $null = Get-YLResourceMigrationPath $Repository $root
        if ($root -cnotmatch '^Assets/[^\\]+$' -or $root.EndsWith('/')) { throw 'Approved roots must be explicit canonical Assets paths.' }
    }
    $settingsPhysical = Get-YLResourceMigrationPath $Repository $SettingsPath
    if ($SettingsPath -cnotmatch '^Assets/.+\.asset$' -or $SettingsPath.Contains('\')) { throw 'An exact official Settings asset path is required.' }
    $snapshotData = [IO.File]::ReadAllText((Join-Path $Snapshot 'snapshot.json')) | ConvertFrom-Json -AsHashtable -DateKind String
    if ((Get-YLTestPath $snapshotData.repository) -ine $Repository) { throw 'Preview repository differs from the preserved snapshot source.' }
    $archived = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($item in $snapshotData.files) { $archived.Add($item.path, $item) }
    $assets = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($item in $data.assets) { $assets.Add($item.path, $item) }
    $inputs = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    function Add-MigrationInput([string] $Relative, [switch] $Official) {
        $path = Get-YLResourceMigrationPath $Repository $Relative
        if ($inputs.ContainsKey($Relative)) { return }
        if (-not $Official -and -not @($AllowedRoots | Where-Object { $Relative -ceq $_ -or $Relative.StartsWith($_ + '/', [StringComparison]::Ordinal) -or $Relative -ceq ($_ + '.meta') }).Count) {
            throw "Input is outside approved migration scope: $Relative"
        }
        if (-not [IO.File]::Exists($path)) { throw "Missing migration source: $Relative" }
        $hash = Get-YLResourceMigrationHash $path
        if (-not $Official -and (-not $archived.ContainsKey($Relative) -or $archived[$Relative].sha256 -ine $hash)) {
            throw "Migration source SHA changed or was not archived: $Relative"
        }
        $guid = if ($Relative.EndsWith('.meta', [StringComparison]::Ordinal)) { Get-YLResourceMigrationGuid $path } else { $null }
        $inputs.Add($Relative, [ordered]@{ path = $Relative; sha256 = $hash; guid = $guid; official = [bool]$Official })
    }
    $entries = [Collections.Generic.List[object]]::new()
    $aliases = [Collections.Generic.List[object]]::new()
    $blockers = [Collections.Generic.List[object]]::new()
    $projects = [Collections.Generic.List[object]]::new()
    $selected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $keys = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $guids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $folders = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $activeOverlays = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($projectGuid in $ProjectGuids) {
        if ($projectGuid -cnotmatch '^[0-9a-f]{32}$' -or -not $selected.Add($projectGuid)) { throw 'Invalid or duplicate selected Project GUID.' }
        $matches = @($data.projects | Where-Object { $_.guid -ceq $projectGuid })
        if ($matches.Count -ne 1) { throw "Unknown selected Project GUID: $projectGuid" }
        $project = $matches[0]
        $recovered = if ($supplement -and $supplement.projects.ContainsKey($projectGuid)) { $supplement.projects[$projectGuid] } else { $null }
        $projectAssets = @(if ($recovered) { $recovered.assets } else { $project.assets })
        Add-MigrationInput $project.assetPath
        Add-MigrationInput ($project.assetPath + '.meta')
        if ((Get-YLResourceMigrationGuid (Get-YLResourceMigrationPath $Repository ($project.assetPath + '.meta'))) -cne $projectGuid) { throw 'Project GUID mismatch.' }
        $projects.Add([ordered]@{ guid = $projectGuid; localId = $project.localId; path = $project.assetPath; type = $project.type; projectId = $project.projectId; status = $project.status; candidateResolution = $(if ($recovered) { 'AuthenticatedAtlasSupplement' } else { $project.status }) })
        if ((-not $recovered -and $project.status -cne 'Resolved') -or $projectAssets.Count -eq 0) {
            $reason = if ($project.Contains('error')) { $project.error } else { 'The certified Project has no expanded assets.' }
            $blockers.Add([ordered]@{ path = $project.assetPath; code = 'LegacyDefinitionRejected'; reason = $reason })
            continue
        }
        if ($project.projectId -cnotmatch '^[A-Za-z0-9_.-]+$') { throw 'Invalid canonical projectId.' }
        if (-not $project.properties.Contains('compression') -or $project.properties.compression.propertyType -cne 'Enum' -or
            $project.properties.compression.value -notin @(0, 1, 2) -or -not $project.properties.Contains('contentEncryption') -or
            $project.properties.contentEncryption.propertyType -cne 'Enum') { throw 'The exported Project compression/content-encoding schema is incomplete or unsupported.' }
        if ($project.properties.contentEncryption.value -ne 0) {
            $blockers.Add([ordered]@{ path = $project.assetPath; code = 'LegacyContentEncoding'; reason = 'Legacy content encryption has no equivalent enabled official pipeline. An explicit new-baseline decision is required; no encryption is silently removed.' })
            continue
        }
        $compression = @('Lz4', 'Lzma', 'Uncompressed')[[int]$project.properties.compression.value]
        foreach ($asset in $projectAssets) {
            $overlayPair = @()
            if ($recovered) { $overlayPair = @($supplement.overlays | Where-Object { $_.projectGuid -ceq $projectGuid -and ($_.path -ceq $asset.path -or $_.path -ceq ($asset.path + '.meta')) }) }
            $generated = $overlayPair.Count -eq 2
            if ($asset.guid -cnotmatch '^[0-9a-f]{32}$' -or $asset.localId -isnot [ValueType] -or $asset.localId -eq 0 -or
                (-not $generated -and (-not $assets.ContainsKey($asset.path) -or $assets[$asset.path].guid -cne $asset.guid))) { throw 'Invalid exported asset GUID/localId/path identity.' }
            $canonical = $project.projectId + '/' + $asset.legacyPath
            if ($asset.address -cne $canonical -or $canonical -match '[\\\[\]]|(^|/)\.{1,2}(/|$)|//|\s$' -or $canonical.EndsWith('/')) { throw 'Invalid canonical project/path address.' }
            if ($keys.ContainsKey($canonical) -or -not $guids.Add($asset.guid)) { throw "Conflicting address or multiply-owned source GUID: $canonical" }
            if ($asset.packing -cnotin @('Together', 'PerAsset') -or [string]::IsNullOrWhiteSpace($asset.group)) { throw 'Unsupported legacy group packing intent.' }
            $keys.Add($canonical, $canonical)
            if ($generated) {
                foreach ($overlay in $overlayPair) {
                    if (-not @($AllowedRoots | Where-Object { $overlay.path.StartsWith($_ + '/', [StringComparison]::Ordinal) }).Count) { throw 'Supplement output is outside approved migration scope.' }
                    if ($overlay.expectedAbsent) {
                        if ([IO.Path]::Exists((Get-YLResourceMigrationPath $Repository $overlay.path))) { throw "Supplement ExpectedAbsent conflict: $($overlay.path)" }
                    } else { Add-MigrationInput $overlay.path }
                    $activeOverlays.Add($overlay.path, $overlay)
                }
            } else {
                Add-MigrationInput $asset.path
                Add-MigrationInput ($asset.path + '.meta')
                if ((Get-YLResourceMigrationGuid (Get-YLResourceMigrationPath $Repository ($asset.path + '.meta'))) -cne $asset.guid -or
                    $assets[$asset.path].sha256 -ine $inputs[$asset.path].sha256) { throw 'Exported source GUID/SHA mismatch.' }
            }
            if (-not $generated -and $assets[$asset.path].Contains('dependencies')) {
                foreach ($dependency in $assets[$asset.path].dependencies) {
                    if (-not $dependency.StartsWith('Assets/', [StringComparison]::Ordinal) -or $dependency.EndsWith('.cs', [StringComparison]::OrdinalIgnoreCase)) { continue }
                    if (-not [IO.Directory]::Exists((Get-YLResourceMigrationPath $Repository $dependency))) { Add-MigrationInput $dependency }
                    Add-MigrationInput ($dependency + '.meta')
                    if (-not $assets.ContainsKey($dependency) -or (Get-YLResourceMigrationGuid (Get-YLResourceMigrationPath $Repository ($dependency + '.meta'))) -cne $assets[$dependency].guid) {
                        throw "A serialized dependency GUID is missing or changed: $dependency"
                    }
                }
            }
            # Folder membership was expanded by the archived engine, not rediscovered using current defaults.
            $selectionPath = Get-YLResourceMigrationPath $Repository $asset.selectionPath
            if (-not $generated) {
                Add-MigrationInput ($asset.selectionPath + '.meta')
                if ((Get-YLResourceMigrationGuid ($selectionPath + '.meta')) -cne $asset.selectionGuid) { throw 'Folder/selection GUID changed.' }
            }
            if ([IO.Directory]::Exists($selectionPath) -and $folders.Add($asset.selectionPath)) {
                $actual = @(Get-YLTestTreeFiles $selectionPath | ForEach-Object { [IO.Path]::GetRelativePath($Repository, $_).Replace('\', '/') })
                $expected = @($archived.Keys | Where-Object { $_.StartsWith($asset.selectionPath + '/', [StringComparison]::Ordinal) })
                if (-not [Collections.Generic.HashSet[string]]::new([string[]]$expected, [StringComparer]::Ordinal).SetEquals([string[]]$actual)) {
                    throw "Folder selection membership changed since export: $($asset.selectionPath)"
                }
                foreach ($member in $actual) { Add-MigrationInput $member }
            }
            if ($asset.group -cnotmatch '^[A-Za-z0-9_.-]+$') { throw 'A legacy group requires an explicit safe official name.' }
            $entries.Add([ordered]@{
                path = $asset.path; guid = $asset.guid; localId = $asset.localId; type = $asset.type; address = $canonical
                group = 'YLResource_' + $project.projectId + '__' + $asset.group; label = $project.projectId + '/' + $asset.group
                packing = $asset.packing; compression = $compression; selectionGuid = $asset.selectionGuid; selectionPath = $asset.selectionPath; subAssets = @($asset.subAssets)
            })
            foreach ($alias in $asset.aliases) {
                if ([string]::IsNullOrWhiteSpace($alias) -or $keys.ContainsKey($alias)) { throw "Conflicting legacy alias: $alias" }
                $keys.Add($alias, $canonical)
                $aliases.Add([ordered]@{ legacy = $alias; canonical = $canonical; guid = $asset.guid })
            }
        }
        $fingerprints = if ($recovered) { @($recovered.fingerprintPaths) } else { @($project.fingerprintPaths) }
        foreach ($path in $fingerprints) {
            if ($activeOverlays.ContainsKey($path)) { continue }
            if ([IO.Directory]::Exists((Get-YLResourceMigrationPath $Repository $path))) { Add-MigrationInput ($path + '.meta') }
            else { Add-MigrationInput $path; Add-MigrationInput ($path + '.meta') }
        }
    }
    $retire = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    $retireIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($guid in $RetireProjectGuids) {
        if (-not $supplement -or $guid -cnotin @($supplement.negatives | ForEach-Object { $_.guid }) -or -not $retireIds.Add($guid)) { throw 'Retire Project requires a unique explicitly certified negative definition.' }
    }
    $authorObjects = @($data.assets | ForEach-Object { if ($_.Contains('objects')) { $_.objects } })
    foreach ($obj in $authorObjects) {
        $kind = $null
        if ($obj.type.StartsWith('YLFramework.Editor.Resource.ResourceProject,', [StringComparison]::Ordinal) -and ($selected.Contains($obj.guid) -or $retireIds.Contains($obj.guid))) { $kind = 'Project' }
        elseif ($obj.type.StartsWith('YLFramework.Editor.Resource.ResourceBuildRecipe,', [StringComparison]::Ordinal) -and $obj.properties.Contains('project') -and ($selected.Contains($obj.properties.project.value.guid) -or $retireIds.Contains($obj.properties.project.value.guid))) { $kind = 'Recipe' }
        elseif ($obj.type.StartsWith('YLFramework.Editor.Resource.ResourceAtlasConfiguration,', [StringComparison]::Ordinal) -and @($data.projects | Where-Object { $selected.Contains($_.guid) -and $_.properties.Contains('automaticAtlases') -and $_.properties.automaticAtlases.value.Contains('guid') -and $_.properties.automaticAtlases.value.guid -ceq $obj.guid }).Count) { $kind = 'AtlasConfig' }
        if (-not $kind) { continue }
        foreach ($path in @($obj.assetPath, ($obj.assetPath + '.meta'))) {
            Add-MigrationInput $path
            if (-not $retire.ContainsKey($path)) { $retire.Add($path, [ordered]@{ path = $path; guid = $obj.guid; originalSha256 = $inputs[$path].sha256; kind = $kind; negative = $retireIds.Contains($obj.guid); status = 'FrozenAwaitingRuntimeValidation' }) }
        }
    }
    $rewrites = [Collections.Generic.List[object]]::new()
    $consumers = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($field in $ConsumerFields) {
        $objects = @($data.assets | ForEach-Object { if ($_.Contains('objects')) { $_.objects } } | Where-Object { $_.guid -ceq $field.guid -and $_.localId -eq $field.localId })
        if ($objects.Count -ne 1 -or $field.propertyPath -notmatch '^[A-Za-z_][A-Za-z0-9_.\[\]]*$') { throw 'Unknown or ambiguous consumer object/field identity.' }
        $consumer = $objects[0]
        if ($consumer.type.StartsWith('YLFramework.Editor.Resource.', [StringComparison]::Ordinal)) { throw 'Legacy author DTO fields are not runtime consumer rewrites.' }
        if (-not $consumer.properties.Contains($field.propertyPath)) { throw 'Consumer property is missing from the certified export.' }
        $property = $consumer.properties[$field.propertyPath]
        if ($property.propertyType -cne 'String' -or -not $keys.ContainsKey($property.value)) { throw 'Consumer field does not contain a certified resource key.' }
        Add-MigrationInput $consumer.assetPath
        Add-MigrationInput ($consumer.assetPath + '.meta')
        $consumerKey = $consumer.guid + '/' + $consumer.localId
        if (-not $consumers.ContainsKey($consumerKey)) { $consumers.Add($consumerKey, $consumer) }
        if (@($rewrites | Where-Object { $_.guid -ceq $consumer.guid -and $_.localId -eq $consumer.localId -and $_.propertyPath -ceq $field.propertyPath }).Count) { throw 'Duplicate selected consumer field.' }
        $rewrites.Add([ordered]@{
            guid = $consumer.guid; localId = $consumer.localId; path = $consumer.assetPath; type = $consumer.type
            propertyPath = $field.propertyPath; propertyType = $property.propertyType
            oldValue = $property.value; newValue = $keys[$property.value]
        })
    }
    foreach ($consumer in @($data.assets | ForEach-Object { if ($_.Contains('objects')) { $_.objects } })) {
        if ($consumer.type.StartsWith('YLFramework.Editor.Resource.', [StringComparison]::Ordinal)) { continue }
        $consumerKey = $consumer.guid + '/' + $consumer.localId
        if ($inputs.ContainsKey($consumer.assetPath) -and -not $consumers.ContainsKey($consumerKey)) {
            $consumers.Add($consumerKey, $consumer)
        }
        foreach ($propertyPath in $consumer.properties.Keys) {
            $property = $consumer.properties[$propertyPath]
            if ($property.propertyType -cne 'String' -or $property.value -isnot [string] -or -not $keys.ContainsKey($property.value) -or $keys[$property.value] -ceq $property.value) { continue }
            if (-not @($rewrites | Where-Object { $_.guid -ceq $consumer.guid -and $_.localId -eq $consumer.localId -and $_.propertyPath -ceq $propertyPath }).Count) {
                $blockers.Add([ordered]@{ path = $consumer.assetPath; code = 'UnselectedAliasConsumer'; reason = "Explicit consumer field selection required: $($consumer.guid)/$($consumer.localId)/$propertyPath" })
            }
        }
    }
    $settingsRoot = [IO.Path]::GetDirectoryName($SettingsPath).Replace('\', '/')
    $settingsDirectory = Get-YLResourceMigrationPath $Repository $settingsRoot
    if ([IO.Directory]::Exists($settingsDirectory)) {
        foreach ($path in @(Get-YLTestTreeFiles $settingsDirectory)) {
            Add-MigrationInput ([IO.Path]::GetRelativePath($Repository, $path).Replace('\', '/')) -Official
        }
        if ([IO.File]::Exists($settingsDirectory + '.meta')) { Add-MigrationInput ($settingsRoot + '.meta') -Official }
    }
    $defaultRoot = Join-Path $Repository 'Assets/AddressableAssetsData'
    if ([IO.Directory]::Exists($defaultRoot)) {
        foreach ($path in @(Get-YLTestTreeFiles $defaultRoot)) { Add-MigrationInput ([IO.Path]::GetRelativePath($Repository, $path).Replace('\', '/')) -Official }
        if ([IO.File]::Exists($defaultRoot + '.meta')) { Add-MigrationInput 'Assets/AddressableAssetsData.meta' -Official }
    }
    if ([IO.File]::Exists((Join-Path $Repository 'ProjectSettings/EditorBuildSettings.asset'))) { Add-MigrationInput 'ProjectSettings/EditorBuildSettings.asset' -Official }
    foreach ($entry in $entries) {
        if ($keys.ContainsKey($entry.label)) { throw "Collection Label conflicts with a single-asset key: $($entry.label)" }
        if (@($entries | Where-Object { $_.group -ceq $entry.group -and ($_.label -cne $entry.label -or $_.packing -cne $entry.packing -or $_.compression -cne $entry.compression) }).Count) { throw 'Official Group name/packing/compression collision.' }
    }
    return [ordered]@{
        schemaVersion = 1; purpose = 'YLResource.AddressablesMigrationPreview'; previewId = [Guid]::NewGuid().ToString('N')
        status = $(if ($blockers.Count) { 'Blocked' } else { 'ReadyForCandidate' }); candidateValidated = $false
        repository = $Repository; snapshot = $Snapshot; sourceCommit = $snapshotData.sourceCommit
        snapshotManifestSha256 = $data.snapshotManifestSha256; inventory = (Get-YLTestPath $Inventory); inventorySha256 = $InventorySha256
        settingsPath = $SettingsPath; settingsRoot = $settingsRoot; settingsExpectedAbsent = -not [IO.File]::Exists($settingsPhysical); useAsDefault = $true
        disableCatalogUpdateOnStartup = $true; allowedRoots = @($AllowedRoots); projects = $projects.ToArray()
        entries = $entries.ToArray(); aliases = $aliases.ToArray(); rewrites = $rewrites.ToArray(); consumers = @($consumers.Values)
        inputs = @($inputs.Values); folders = @($folders); blockers = $blockers.ToArray()
        overlays = @($activeOverlays.Values)
        candidateOnlyInputs = $(if ($supplement) { @($supplement.normalizations | Where-Object { $inputs.ContainsKey($_.path) -and -not $activeOverlays.ContainsKey($_.path) }) } else { @() })
        supplement = $(if ($supplement) { @{ path = $supplement.path; sha256 = $supplement.sha256; root = $supplement.root; hostId = $supplement.hostId; resultId = $supplement.resultId; pins = $supplement.pins } } else { $null })
        retirement = @{ entries = @($retire.Values); negativeProjectGuids = @($RetireProjectGuids); deleteEnabled = $false; status = 'FrozenAwaitingRuntimeValidation' }
        limitations = @('Atlas inputs without archived expansion remain blocked.', 'Alias mappings are migration-only evidence, never runtime aliases or Labels.', 'Candidate validation is not Apply or post-Apply cold verification.')
    }
}

function Read-YLPinnedResourceMigrationJson {
    param([string] $Path, [string] $Sha256, [string] $Description)
    if ([string]::IsNullOrWhiteSpace($Path) -or $Sha256 -cnotmatch '^[0-9a-f]{64}$') { throw "A pinned $Description is required." }
    $bytes = [IO.File]::ReadAllBytes((Get-YLTestPath $Path))
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() -cne $Sha256) { throw "$Description SHA mismatch." }
    $data = [Text.UTF8Encoding]::new($false, $true).GetString($bytes).TrimStart([char]0xfeff) | ConvertFrom-Json -AsHashtable -DateKind String -NoEnumerate
    if ($data -isnot [Collections.IDictionary]) { throw "Invalid $Description JSON object." }
    return $data
}

function Assert-YLResourceMigrationValidation {
    param([Collections.IDictionary] $Manifest, [string] $ManifestSha256, [string] $ValidationReceipt, [string] $ValidationSha256)
    if ($Manifest['retirement'] -and $Manifest.retirement.entries.Count -and -not $Manifest.retirement.deleteEnabled) {
        throw 'This is a conversion-only batch; selected retirement requires a final frozen manifest and its own native validation.'
    }
    $validation = Read-YLPinnedResourceMigrationJson $ValidationReceipt $ValidationSha256 'native candidate validation receipt'
    if ($ManifestSha256 -cnotmatch '^[0-9a-f]{64}$' -or $validation.manifestSha256 -cne $ManifestSha256 -or
        $validation.purpose -cne 'YLResource.AddressablesMigrationValidation') {
        throw 'Candidate validation does not prove this exact frozen manifest.'
    }
    foreach ($identity in @(@('hostId', '^[0-9a-f]{32}$'), @('inputHash', '^[0-9a-f]{64}$'),
        @('unityVersion', '^\d+\.\d+\.\d+[abfp]\d+$'), @('addressablesVersion', '^\d+\.\d+\.\d+$'))) {
        $key = $identity[0]
        if ($Manifest[$key] -isnot [string] -or $Manifest[$key] -cnotmatch $identity[1] -or $validation[$key] -cne $Manifest[$key]) {
            throw "Candidate validation subject identity differs: $key. Regenerate and validate an unbound legacy candidate."
        }
    }
    $proofPath = Join-Path ([IO.Path]::GetDirectoryName((Get-YLTestPath $ValidationReceipt))) 'native-proof.json'
    if ($validation.nativeProof -isnot [string] -or (Get-YLTestPath $validation.nativeProof) -ine $proofPath) {
        throw 'The native proof must be beside the pinned validation receipt.'
    }
    $proof = Read-YLPinnedResourceMigrationJson $proofPath $validation.nativeProofSha256 'native proof'
    if ($proof.purpose -cne 'YLResource.CandidateNativeProof' -or $proof.addressablesSource -cne 'Registry' -or
        $proof.cleanupVerified -isnot [bool] -or -not $proof.cleanupVerified -or $proof.returnedToEditMode -isnot [bool] -or -not $proof.returnedToEditMode -or
        $proof.subjectHostId -cne $Manifest.hostId -or $proof.subjectInputHash -cne $Manifest.inputHash -or
        $proof.unityVersion -cne $Manifest.unityVersion -or $proof.addressablesVersion -cne $Manifest.addressablesVersion -or
        $proof.hostId -cnotmatch '^[0-9a-f]{32}$' -or $proof.hostId -ceq $Manifest.hostId -or $proof.hostId -cne $validation.validationHostId -or
        $proof.resultId -cnotmatch '^[0-9a-f]{32}$' -or $proof.resultId -cne $validation.validationResultId -or
        $proof.inputHash -cnotmatch '^[0-9a-f]{64}$' -or $proof.inputHash -cne $validation.validationInputHash) {
        throw 'The native proof does not establish an independent validation host, exact profile and successful cleanup.'
    }
    $required = @('OfficialContentLoad', 'ConsumerColdRead', 'ForeignSettingsPreserved')
    foreach ($document in @($validation, $proof)) {
        if ($document.schemaVersion -isnot [long] -or $document.schemaVersion -ne 1 -or $document.status -cne 'Passed' -or
            $document.runtimeVerified -isnot [bool] -or -not $document.runtimeVerified -or $document.manifestSha256 -cne $ManifestSha256 -or
            $document.previewSha256 -cne $Manifest.previewSha256 -or $document.outputDigest -cne $Manifest.outputDigest -or
            $document.checks -isnot [array] -or $document.checks.Count -ne $required.Count -or
            @($required | Where-Object { $_ -cnotin $document.checks }).Count -gt 0) {
            throw 'Candidate validation does not prove this exact frozen output and required native checks.'
        }
    }
    if ($Manifest['retirement'] -and $Manifest.retirement.deleteEnabled) {
        Assert-YLNativeRetirementProof $proof $Manifest.retirement.entries
    }
    Assert-YLTestProcessSucceeded ([pscustomobject]$validation.process)
}

function Assert-YLResourcePreviewSource {
    param([Collections.IDictionary] $Preview)
    foreach ($input in $Preview.inputs) {
        $path = Get-YLResourceMigrationPath $Preview.repository $input.path
        if (-not [IO.File]::Exists($path) -or (Get-YLResourceMigrationHash $path) -cne $input.sha256 -or
            ($input.guid -and (Get-YLResourceMigrationGuid $path) -cne $input.guid)) { throw "Frozen Preview source SHA/GUID changed: $($input.path)" }
    }
    if ($Preview.Contains('supplement') -and $Preview.supplement) {
        if ((Get-YLResourceMigrationHash $Preview.supplement.path) -cne $Preview.supplement.sha256 -or [IO.Path]::GetDirectoryName((Get-YLTestPath $Preview.supplement.path)) -ine $Preview.supplement.root) { throw 'Frozen Supplement identity changed.' }
        foreach ($pin in $Preview.supplement.pins) { $null = Get-YLResourceSupplementFile $Preview.supplement.root $pin.path $pin.sha256 }
        foreach ($overlay in $Preview.overlays) {
            $path = Get-YLResourceMigrationPath $Preview.repository $overlay.path
            if ($overlay.expectedAbsent -and [IO.Path]::Exists($path)) { throw "Frozen Supplement ExpectedAbsent conflict: $($overlay.path)" }
            $null = Get-YLResourceSupplementFile $Preview.supplement.root $overlay.payload $overlay.sha256
        }
    }
    foreach ($folder in $Preview.folders) {
        $expected = @($Preview.inputs | Where-Object { $_.path.StartsWith($folder + '/', [StringComparison]::Ordinal) } | ForEach-Object { $_.path })
        $actual = @(Get-YLTestTreeFiles (Get-YLResourceMigrationPath $Preview.repository $folder) | ForEach-Object { [IO.Path]::GetRelativePath($Preview.repository, $_).Replace('\', '/') })
        if (-not [Collections.Generic.HashSet[string]]::new([string[]]$expected, [StringComparer]::Ordinal).SetEquals([string[]]$actual)) { throw "Frozen Preview source Folder membership changed: $folder" }
    }
}

function Get-YLResourceMigrationCandidatePreview {
    param([Collections.IDictionary] $Preview)
    Assert-YLResourcePreviewSource $Preview
    $native = (ConvertTo-Json -InputObject $Preview -Depth 60) | ConvertFrom-Json -AsHashtable -DateKind String
    if (-not $Preview.Contains('supplement') -or -not $Preview.supplement) { return $native }
    $native['sourceInputs'] = @($Preview.inputs)
    $inputs = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($input in $Preview.inputs) { $inputs.Add($input.path, $input) }
    foreach ($input in @($Preview.candidateOnlyInputs) + @($Preview.overlays)) {
        if ($null -eq $input) { continue }
        $null = Get-YLResourceSupplementFile $Preview.supplement.root $input.payload $input.sha256
        $inputs[$input.path] = @{ path = $input.path; sha256 = $input.sha256; guid = $input.guid; provenance = 'AuthenticatedSupplementCandidateMaterial'; originalSha256 = $input.originalSha256 }
    }
    $native.inputs = @($inputs.Values)
    return $native
}

function New-YLCandidateRetirementDeletes([Collections.IDictionary] $Preview) {
    foreach ($row in $Preview.retirement.entries | Sort-Object path -CaseSensitive) {
        [ordered]@{ path = $row.path; action = 'Delete'; expectedAbsent = $false; inputSha256 = $row.originalSha256; outputSha256 = $null; oldGuid = $(if ($row.path.EndsWith('.meta', [StringComparison]::Ordinal)) { $row.guid } else { $null }); newGuid = $null; retiredGuid = $row.guid; fields = @() }
    }
}

function Assert-YLResourceRetirementBatch([Collections.IDictionary] $Manifest) {
    $deletes = @($Manifest.operations | Where-Object action -CEQ Delete)
    if (-not $Manifest.Contains('retirement') -or -not $Manifest.retirement) {
        if ($deletes.Count) { throw 'Delete requires an enabled frozen author retirement set.' }
        return
    }
    if ($Manifest.retirement.deleteEnabled -isnot [bool] -or $Manifest.retirement.entries -isnot [array]) { throw 'Invalid frozen retirement declaration.' }
    if (-not $Manifest.retirement.deleteEnabled) {
        if ($deletes.Count) { throw 'Delete requires an enabled frozen author retirement set.' }
        return
    }
    if (-not $deletes.Count -or $deletes.Count -ne $Manifest.retirement.entries.Count) { throw 'The exact frozen retirement batch is incomplete.' }
    $rows = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($row in $Manifest.retirement.entries) {
        if ($row.path -cnotmatch '^Assets/.+\.asset(?:\.meta)?$' -or $row.guid -cnotmatch '^[0-9a-f]{32}$' -or
            $row.originalSha256 -cnotmatch '^[0-9a-f]{64}$' -or $row.kind -cnotin @('Project', 'Recipe', 'AtlasConfig') -or
            -not $rows.TryAdd($row.path, $row)) { throw 'Invalid or duplicate frozen author retirement entry.' }
    }
    $guids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($operation in $deletes) {
        $row = $null
        if (-not $rows.TryGetValue($operation.path, [ref]$row) -or $row.path -cne $operation.path -or
            $row.originalSha256 -cne $operation.inputSha256 -or $row.guid -cne $operation.retiredGuid -or
            $operation.expectedAbsent -isnot [bool] -or $operation.expectedAbsent -or $operation.newGuid -or $operation.outputSha256) {
            throw 'Delete is outside the original SHA/GUID-pinned author retirement set.'
        }
        $meta = $row.path.EndsWith('.meta', [StringComparison]::Ordinal)
        $pairedPath = if ($meta) { $row.path.Substring(0, $row.path.Length - 5) } else { $row.path + '.meta' }
        if (-not $rows.ContainsKey($pairedPath) -or $rows[$pairedPath].path -cne $pairedPath -or
            $rows[$pairedPath].guid -cne $row.guid -or $rows[$pairedPath].kind -cne $row.kind -or
            ($meta -and $operation.oldGuid -cne $row.guid) -or (-not $meta -and ($operation.oldGuid -or -not $guids.Add($row.guid)))) {
            throw "The frozen retirement asset and metadata identity is incomplete or ambiguous: $($row.path)"
        }
    }
}

function Assert-YLNativeRetirementProof([Collections.IDictionary] $Proof, [object[]] $Entries) {
    if ($Proof['retired'] -isnot [array] -or $Proof.retired.Count -ne $Entries.Count) { throw 'The native retirement evidence is incomplete.' }
    foreach ($entry in $Entries) {
        $matches = @($Proof.retired | Where-Object { $entry.path -ceq $_.path -and $entry.originalSha256 -ceq $_.originalSha256 -and
                $entry.guid -ceq $_.guid -and $entry.kind -ceq $_.kind -and $entry['negative'] -ceq $_['negative'] -and
                $_.absent -is [bool] -and $_.absent -and $_.passed -is [bool] -and $_.passed })
        if ($matches.Count -ne 1) { throw "Missing, mismatched or duplicate native retirement evidence: $($entry.path)" }
    }
}

function Get-YLResourceRetirementReferenceState {
    param([string] $Repository, [Collections.IDictionary] $Manifest, [string] $ProjectedOutput)
    $deletes = @($Manifest.operations | Where-Object action -CEQ Delete)
    if (-not $deletes.Count) { return }
    Assert-YLResourceRetirementBatch $Manifest
    $retired = [Collections.Generic.HashSet[string]]::new([string[]]$deletes.path, [StringComparer]::OrdinalIgnoreCase)
    $guids = @($deletes.retiredGuid | Sort-Object -Unique)
    # Match literal GUIDs (including string-valued AssetReferences), not only Unity YAML's `guid:` field.
    $guidPattern = [regex]::new(($guids -join '|'), [Text.RegularExpressions.RegexOptions]'IgnoreCase, CultureInvariant')
    $roots = [Collections.Generic.List[string]]::new()
    foreach ($root in @('Assets', 'ProjectSettings', 'Packages', 'PackageSources')) {
        $path = Get-YLResourceMigrationPath $Repository $root
        if ([IO.Directory]::Exists($path)) { $roots.Add($path) }
        elseif ($root -ceq 'Assets' -or [IO.Path]::Exists($path)) { throw "Cannot inspect retirement reference root: $root" }
    }
    # Both declarations and the lock can introduce a mutable local package. Paths outside the project
    # cannot be covered by its exclusive window; require an ordinary, contained local directory.
    $packagePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $packageManifest = Get-YLResourceMigrationPath $Repository 'Packages/manifest.json'
    $packageLock = Get-YLResourceMigrationPath $Repository 'Packages/packages-lock.json'
    if ([IO.File]::Exists($packageManifest) -or [IO.File]::Exists($packageLock)) {
        if (-not [IO.File]::Exists($packageManifest) -or -not [IO.File]::Exists($packageLock)) { throw 'Cannot inspect retirement references without the package manifest and lock.' }
        $declared = [IO.File]::ReadAllText($packageManifest) | ConvertFrom-Json -AsHashtable
        $locked = [IO.File]::ReadAllText($packageLock) | ConvertFrom-Json -AsHashtable
        if ($declared.dependencies -isnot [Collections.IDictionary] -or $locked.dependencies -isnot [Collections.IDictionary]) { throw 'Cannot inspect the retirement package graph.' }
        foreach ($specifier in $declared.dependencies.Values) {
            if ($specifier.StartsWith('file:', [StringComparison]::OrdinalIgnoreCase)) { $null = $packagePaths.Add($specifier.Substring(5)) }
        }
        foreach ($entry in $locked.dependencies.GetEnumerator()) {
            $row = $entry.Value
            if ($row.source -ceq 'local' -and $row.version.StartsWith('file:', [StringComparison]::OrdinalIgnoreCase)) { $null = $packagePaths.Add($row.version.Substring(5)) }
            elseif ($row.source -ceq 'git' -and $row.hash -cmatch '^[0-9a-f]{40}$') { continue }
            elseif ($row.source -cnotin @('embedded', 'registry', 'builtin')) { throw "Cannot inspect an unpinned or unsupported retirement package source: $($entry.Key)" }
        }
    }
    foreach ($specifier in $packagePaths) {
        $relative = [Uri]::UnescapeDataString($specifier)
        if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':')) { throw 'Cannot inspect an external local package for retirement.' }
        $path = Get-YLTestPath ([IO.Path]::GetFullPath((Join-Path (Join-Path $Repository 'Packages') $relative)))
        if (-not $path.StartsWith($Repository + '\', [StringComparison]::OrdinalIgnoreCase) -or
            -not [IO.File]::Exists((Join-Path $path 'package.json'))) { throw 'Cannot inspect an external, missing or opaque local package for retirement.' }
        $roots.Add($path)
    }

    $files = [Collections.Generic.SortedDictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($root in $roots) {
        foreach ($file in Get-YLTestTreeFiles $root) {
            $relative = [IO.Path]::GetRelativePath($Repository, $file).Replace('\', '/')
            if (-not $retired.Contains($relative)) { $files[$relative] = $file }
        }
    }
    $outputs = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    if ($ProjectedOutput) {
        foreach ($op in $Manifest.operations) {
            if ($op.action -ceq 'Delete') { continue }
            $files[$op.path] = Get-YLResourceMigrationPath $ProjectedOutput $op.path
            $outputs.Add($op.path, $op.outputSha256)
        }
    }
    $pins = [Collections.Generic.List[string]]::new()
    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    $opaqueNonSerialized = @('.png', '.jpg', '.jpeg', '.wav', '.dll', '.pdb', '.mdb')
    $unitySerialized = @('.asset', '.unity', '.prefab', '.mat', '.anim', '.controller', '.overridecontroller', '.mixer', '.spriteatlas', '.spriteatlasv2', '.playable', '.preset', '.rendertexture', '.physicmaterial', '.physicsmaterial2d', '.mask', '.guiskin', '.lighting')
    foreach ($entry in $files.GetEnumerator()) {
        $relative = $entry.Key
        if ([IO.Path]::GetFileName($relative) -match '(?i)(\.(pfx|p12|pem|key|keystore|jks)$|\.private($|\.)|^\.env($|\.)|^(credentials|secrets)\.json$)') {
            throw "Cannot inspect a sensitive retirement input: $relative"
        }
        $bytes = [IO.File]::ReadAllBytes($entry.Value)
        $sha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        if ($outputs.ContainsKey($relative) -and $sha -cne $outputs[$relative]) { throw "Frozen candidate output SHA differs during retirement inspection: $relative" }
        $extension = [IO.Path]::GetExtension($relative).ToLowerInvariant()
        $text = $null
        try { $text = $utf8.GetString($bytes).TrimStart([char]0xfeff) }
        catch [Text.DecoderFallbackException] {
            if ($extension -cnotin $opaqueNonSerialized) { throw "Cannot inspect an opaque retirement reference input: $relative" }
        }
        if ($null -eq $text) {
            # Built-in media and managed plugin bytes do not store native Unity PPtrs; their .meta still gets scanned.
            $text = [Text.Encoding]::Latin1.GetString($bytes)
        } elseif ($relative -ieq 'ProjectSettings/XRSettings.asset' -and -not $text.StartsWith('%YAML ', [StringComparison]::Ordinal)) {
            # The Editor can serialize this specific settings asset as parallel JSON string arrays.
            $unsupported = "Cannot inspect an unsupported XR settings format: $relative"
            try { $document = [Text.Json.JsonDocument]::Parse($text) }
            catch [Text.Json.JsonException] { throw $unsupported }
            try {
                $settings = $document.RootElement
                if ($settings.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw $unsupported }
                $fields = @($settings.EnumerateObject())
                if ($fields.Count -ne 2 -or 'm_SettingKeys' -cnotin $fields.Name -or 'm_SettingValues' -cnotin $fields.Name) { throw $unsupported }
                $keys = $settings.GetProperty('m_SettingKeys')
                $values = $settings.GetProperty('m_SettingValues')
                if ($keys.ValueKind -ne [Text.Json.JsonValueKind]::Array -or $values.ValueKind -ne [Text.Json.JsonValueKind]::Array -or
                    $keys.GetArrayLength() -ne $values.GetArrayLength()) { throw $unsupported }
                foreach ($item in (@($keys.EnumerateArray()) + @($values.EnumerateArray()))) {
                    if ($item.ValueKind -ne [Text.Json.JsonValueKind]::String) { throw $unsupported }
                    if ($guidPattern.IsMatch($item.GetString())) { throw "A retired GUID is still present in the surviving reference input: $relative" }
                }
            } finally { $document.Dispose() }
        } elseif (($extension -cin $unitySerialized -and -not $text.StartsWith('%YAML ', [StringComparison]::Ordinal)) -or
            ($extension -cnotin $opaqueNonSerialized -and $text -match '[\x00-\x08\x0B\x0C\x0E-\x1F]')) {
            throw "Cannot inspect a non-text Unity retirement reference input: $relative"
        }
        if ($guidPattern.IsMatch($text)) { throw "A retired GUID is still present in the surviving reference input: $relative" }
        $pins.Add($relative + '=' + $sha)
    }
    return [ordered]@{
        scope = 'LocalProjectSerializedInputs'; fileCount = $pins.Count
        sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($pins -join "`n")))).ToLowerInvariant()
    }
}

function Read-YLResourceMigrationManifest {
    param([string] $Repository, [string] $Manifest, [string] $ManifestSha256)
    $data = Read-YLPinnedResourceMigrationJson $Manifest $ManifestSha256 'frozen migration manifest'
    if ($data.schemaVersion -ne 1 -or $data.purpose -cne 'YLResource.AddressablesMigrationManifest' -or
        (Get-YLTestPath $data.repository) -ine (Get-YLTestPath $Repository) -or $data.previewSha256 -cnotmatch '^[0-9a-f]{64}$' -or
        $data.outputDigest -cnotmatch '^[0-9a-f]{64}$' -or $data.operations.Count -eq 0) { throw 'Invalid migration manifest identity or empty operations.' }
    $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject @($data.operations) -Depth 40 -Compress)))).ToLowerInvariant()
    if ($digest -cne $data.outputDigest) { throw 'Frozen operation/output digest mismatch.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $deleting = $false
    foreach ($operation in $data.operations) {
        $null = Get-YLResourceMigrationPath $Repository $operation.path
        if (-not $seen.Add($operation.path) -or $operation.action -cnotin @('Add', 'Modify', 'Delete') -or
            ($operation.action -ceq 'Add' -and ($operation.expectedAbsent -ne $true -or $null -ne $operation.inputSha256)) -or
            ($operation.action -cne 'Add' -and $operation.inputSha256 -cnotmatch '^[0-9a-f]{64}$') -or
            ($operation.action -cne 'Delete' -and $operation.outputSha256 -cnotmatch '^[0-9a-f]{64}$')) { throw 'Invalid, duplicate or ambiguous migration file operation.' }
        if ($deleting -and $operation.action -cne 'Delete') { throw 'All deletion operations must follow additions/modifications.' }
        if ($operation.action -ceq 'Delete') { $deleting = $true }
        if ($operation.path.EndsWith('.meta', [StringComparison]::Ordinal)) {
            if (($operation.action -cne 'Add' -and $operation.oldGuid -cnotmatch '^[0-9a-f]{32}$') -or
                ($operation.action -cne 'Delete' -and $operation.newGuid -cnotmatch '^[0-9a-f]{32}$') -or
                ($operation.action -ceq 'Modify' -and $operation.oldGuid -cne $operation.newGuid)) { throw 'Original metadata GUIDs must be preserved; new metadata requires an explicit new identity.' }
        }
    }
    Assert-YLResourceRetirementBatch $data
    return $data
}

function Assert-YLResourceMigrationFileState {
    param([string] $Root, [object] $Operation, [switch] $Output)
    $path = Get-YLResourceMigrationPath $Root $Operation.path
    $absent = if ($Output) { $Operation.action -ceq 'Delete' } else { $Operation.action -ceq 'Add' }
    if ($absent) {
        if ([IO.Path]::Exists($path)) { throw "ExpectedAbsent conflict; preserve the existing target: $($Operation.path)" }
        return
    }
    $hash = if ($Output) { $Operation.outputSha256 } else { $Operation.inputSha256 }
    $guid = if ($Output) { $Operation.newGuid } else { $Operation.oldGuid }
    if (-not [IO.File]::Exists($path) -or (Get-YLResourceMigrationHash $path) -cne $hash) { throw "Source/output SHA conflict: $($Operation.path)" }
    if ($guid -and (Get-YLResourceMigrationGuid $path) -cne $guid) { throw "Metadata GUID conflict: $($Operation.path)" }
}

function Assert-YLResourceMigrationApplyState {
    param([string] $Repository, [object] $Manifest, [string] $ReceiptDirectory, [string] $ManifestSha256)
    $operations = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $completed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $pending = $false
    for ($index = 0; $index -lt $Manifest.operations.Count; $index++) {
        $operation = $Manifest.operations[$index]
        $operations.Add($operation.path, $operation)
        $receiptPath = Join-Path $ReceiptDirectory ('{0:D6}.json' -f $index)
        if ([IO.File]::Exists($receiptPath)) {
            $receipt = [IO.File]::ReadAllText((Get-YLTestPath $receiptPath)) | ConvertFrom-Json -AsHashtable -DateKind String
            if ($pending -or $receipt.purpose -cne 'YLResource.MigrationFileApplied' -or $receipt.manifestSha256 -cne $ManifestSha256 -or
                $receipt.index -ne $index -or $receipt.path -cne $operation.path -or $receipt.action -cne $operation.action -or
                $receipt.outputSha256 -cne $operation.outputSha256 -or $receipt.newGuid -cne $operation.newGuid -or
                $receipt.verified -isnot [bool] -or -not $receipt.verified) { throw 'Invalid, foreign or non-prefix completion receipt.' }
            Assert-YLResourceMigrationFileState -Root $Repository -Operation $operation -Output
            $null = $completed.Add($operation.path)
        } else {
            $pending = $true
            Assert-YLResourceMigrationFileState -Root $Repository -Operation $operation
        }
    }
    foreach ($input in $Manifest.inputs) {
        if ($operations.ContainsKey($input.path)) { continue }
        $path = Get-YLResourceMigrationPath $Repository $input.path
        if (-not [IO.File]::Exists($path) -or (Get-YLResourceMigrationHash $path) -cne $input.sha256 -or
            ($input.guid -and (Get-YLResourceMigrationGuid $path) -cne $input.guid)) { throw "Unchanged migration source SHA/GUID conflict: $($input.path)" }
    }
    foreach ($folder in $Manifest.folders) {
        $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($input in $Manifest.inputs) { if ($input.path.StartsWith($folder + '/', [StringComparison]::Ordinal)) { $null = $expected.Add($input.path) } }
        foreach ($operation in $Manifest.operations) {
            if (-not $operation.path.StartsWith($folder + '/', [StringComparison]::Ordinal) -or -not $completed.Contains($operation.path)) { continue }
            if ($operation.action -ceq 'Delete') { $null = $expected.Remove($operation.path) } else { $null = $expected.Add($operation.path) }
        }
        $actual = @(Get-YLTestTreeFiles (Get-YLResourceMigrationPath $Repository $folder) | ForEach-Object { [IO.Path]::GetRelativePath($Repository, $_).Replace('\', '/') })
        if (-not $expected.SetEquals([string[]]$actual)) { throw "Source Folder membership conflict: $folder" }
    }
    $newGuids = @($Manifest.operations | Where-Object { $_.action -ceq 'Add' -and $_.newGuid })
    if ($newGuids.Count) {
        foreach ($path in @(Get-YLTestTreeFiles (Join-Path $Repository 'Assets'))) {
            if (-not $path.EndsWith('.meta', [StringComparison]::Ordinal)) { continue }
            $guid = Get-YLResourceMigrationGuid $path
            $relative = [IO.Path]::GetRelativePath($Repository, $path).Replace('\', '/')
            if (@($newGuids | Where-Object { $_.newGuid -ceq $guid -and $_.path -cne $relative }).Count) { throw "Added metadata GUID now conflicts with another source asset: $relative" }
        }
    }
    return ,$completed
}

function Get-YLResourceMigrationFileReceiptsHash([string] $ReceiptDirectory, [int] $Count) {
    $hashes = @(for ($index = 0; $index -lt $Count; $index++) {
        Get-YLResourceMigrationHash (Join-Path $ReceiptDirectory ('{0:D6}.json' -f $index))
    })
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($hashes -join "`n")))).ToLowerInvariant()
}

function Get-YLResourceMigrationAppliedState {
    param([string] $Repository, [Collections.IDictionary] $Manifest, [string] $ReceiptDirectory, [string] $ManifestSha256)
    $Repository = Get-YLTestPath $Repository
    if ($Manifest['retirement'] -and $Manifest.retirement.entries.Count -and -not $Manifest.retirement.deleteEnabled) {
        throw 'Cold Verify requires the final frozen batch, not a conversion-only manifest.'
    }
    $ReceiptDirectory = Get-YLTestPath $ReceiptDirectory
    $ownerPath = Get-YLTestPath (Join-Path $ReceiptDirectory 'owner.json')
    if (-not [IO.File]::Exists($ownerPath)) { throw 'Post-Apply receipt ownership is required.' }
    $owner = [IO.File]::ReadAllText($ownerPath) | ConvertFrom-Json -AsHashtable -DateKind String
    if ($owner.purpose -cne 'YLResource.MigrationApplyReceipts' -or $owner.repository -ine $Repository -or
        $owner.manifestSha256 -cne $ManifestSha256 -or $Manifest.repository -ine $Repository) { throw 'Post-Apply receipt ownership mismatch.' }
    $completed = Assert-YLResourceMigrationApplyState -Repository $Repository -Manifest $Manifest -ReceiptDirectory $ReceiptDirectory -ManifestSha256 $ManifestSha256
    if ($completed.Count -ne $Manifest.operations.Count) { throw 'Verify requires the entire frozen batch to be applied with verified per-file receipts.' }
    $references = Get-YLResourceRetirementReferenceState $Repository $Manifest
    $fileReceiptsHash = Get-YLResourceMigrationFileReceiptsHash $ReceiptDirectory $completed.Count
    $batch = $null
    foreach ($path in ([IO.Directory]::EnumerateFiles($ReceiptDirectory, 'attempt-*.json') | Sort-Object -CaseSensitive)) {
        if ([IO.Path]::GetFileName($path) -cnotmatch '^attempt-[0-9a-f]{32}\.json$') { continue }
        $sha = Get-YLResourceMigrationHash $path
        $attempt = Read-YLPinnedResourceMigrationJson $path $sha 'Apply attempt'
        if ($attempt['schemaVersion'] -ne 1 -or $attempt['purpose'] -cne 'YLResource.MigrationApply' -or
            $attempt['status'] -cne 'AppliedAwaitingColdVerify' -or $attempt['conflict'] -or $attempt['reason'] -or
            $attempt['repository'] -ine $Repository -or $attempt['manifestSha256'] -cne $ManifestSha256 -or
            $attempt['outputDigest'] -cne $Manifest.outputDigest -or $attempt['completed'] -ne $completed.Count -or
            $attempt['fileReceiptsSha256'] -cne $fileReceiptsHash) { continue }
        $appliedReferences = $attempt['retirementReferences']
        if (($references -and (-not $appliedReferences -or $appliedReferences.scope -cne $references.scope -or
                $appliedReferences.fileCount -ne $references.fileCount -or $appliedReferences.sha256 -cne $references.sha256)) -or
            (-not $references -and $appliedReferences)) { continue }
        $batch = [ordered]@{ path = $path; sha256 = $sha; fileReceiptsSha256 = $fileReceiptsHash }
        break
    }
    if (-not $batch) { throw 'Cold Verify requires a successful Apply batch receipt matching the manifest, per-file receipts and current retirement reference inputs.' }

    $hashes = [Collections.Generic.SortedDictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($input in $Manifest.inputs) { $hashes.Add($input.path, $input.sha256) }
    $originals = [ordered]@{}
    foreach ($operation in $Manifest.operations) {
        if ($operation.action -cne 'Add') {
            $originals.Add($operation.path, (Read-VerifiedMigrationBytes $ReceiptDirectory ('Original/' + $operation.path) $operation.inputSha256 'preserved original'))
        }
        if ($operation.action -ceq 'Delete') { $null = $hashes.Remove($operation.path) }
        else { $hashes[$operation.path] = $operation.outputSha256 }
    }
    $files = [ordered]@{}
    foreach ($entry in $hashes.GetEnumerator()) {
        $files.Add($entry.Key, (Read-VerifiedMigrationBytes $Repository $entry.Key $entry.Value 'applied file'))
    }
    return [pscustomobject]@{ files = $files; originals = $originals; retirementReferences = $references; batchReceipt = $batch }
}

function Read-VerifiedMigrationBytes {
    param([string] $Root, [string] $RelativePath, [string] $Sha256, [string] $Description)
    $path = Get-YLResourceMigrationPath $Root $RelativePath
    if (-not [IO.File]::Exists($path)) { throw "Missing ${Description}: $RelativePath" }
    $bytes = [IO.File]::ReadAllBytes($path)
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() -cne $Sha256) {
        throw "The $Description SHA differs: $RelativePath"
    }
    return ,$bytes
}

function Open-YLResourceMigrationDeletion {
    param([string] $Path, [string] $Sha256, [string] $Guid)
    if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) { throw 'Handle-bound migration Delete supports Windows only.' }
    $Path = Get-YLTestPath $Path
    if (-not ('YLFramework.Testing.ResourceMigrationDeletion' -as [type])) {
        Add-Type -Path (Join-Path $PSScriptRoot 'ResourceMigrationDeletion.cs') -ErrorAction Stop
    }
    return [YLFramework.Testing.ResourceMigrationDeletion]::OpenVerified($Path, $Sha256, $Guid)
}

function Invoke-YLResourceMigrationApply {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Repository,
        [Parameter(Mandatory)][string] $Manifest,
        [string] $ManifestSha256,
        [string] $ValidationReceipt,
        [string] $ValidationSha256,
        [string] $ReceiptDirectory,
        [switch] $ExclusiveWindowConfirmed
    )
    if (-not $ExclusiveWindowConfirmed) { throw 'Apply requires an explicitly confirmed exclusive window excluding Unity, user edits, importers, generators and other concurrent writers.' }
    $Repository = Get-YLTestPath $Repository
    $Manifest = Get-YLTestPath $Manifest
    $data = Read-YLResourceMigrationManifest -Repository $Repository -Manifest $Manifest -ManifestSha256 $ManifestSha256
    Assert-YLResourceMigrationValidation -Manifest $data -ManifestSha256 $ManifestSha256 -ValidationReceipt $ValidationReceipt -ValidationSha256 $ValidationSha256
    $ReceiptDirectory = Get-YLTestPath $ReceiptDirectory
    $artifactRoot = [IO.Path]::GetDirectoryName($Manifest)
    if (-not $ReceiptDirectory.StartsWith($artifactRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $ReceiptDirectory.StartsWith($Repository + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Apply receipts must be below the external frozen artifact directory, never inside the source project.' }
    $mutexKey = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Repository.ToLowerInvariant())))
    $mutex = [Threading.Mutex]::new($false, ('Local\YLResourceMigration-' + $mutexKey))
    $locked = $false
    $receiptOwned = $false
    $summary = [ordered]@{ schemaVersion = 1; purpose = 'YLResource.MigrationApply'; repository = $Repository; manifestSha256 = $ManifestSha256
        outputDigest = $data.outputDigest; status = 'Failed'; completed = 0; conflict = $null }
    try {
        try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
        if (-not $locked) { throw 'Another migration Apply is already using this source project.' }
        if ([IO.File]::Exists((Join-Path $Repository 'Temp/UnityLockfile'))) { throw 'A source Unity lock exists; close writers explicitly before Apply.' }
        if ([IO.Directory]::Exists($ReceiptDirectory) -and -not [IO.File]::Exists((Join-Path $ReceiptDirectory 'owner.json'))) { throw 'Refuse to adopt an unknown receipt directory.' }
        if (-not [IO.Directory]::Exists($ReceiptDirectory)) {
            $null = [IO.Directory]::CreateDirectory($ReceiptDirectory)
            Write-YLTestJson -Path (Join-Path $ReceiptDirectory 'owner.json') -Value @{ purpose = 'YLResource.MigrationApplyReceipts'; repository = $Repository; manifestSha256 = $ManifestSha256 }
        }
        $owner = [IO.File]::ReadAllText((Join-Path $ReceiptDirectory 'owner.json')) | ConvertFrom-Json -AsHashtable -DateKind String
        if ($owner.purpose -cne 'YLResource.MigrationApplyReceipts' -or $owner.repository -ine $Repository -or $owner.manifestSha256 -cne $ManifestSha256) { throw 'Apply receipt ownership mismatch.' }
        $receiptOwned = $true
        $completed = Assert-YLResourceMigrationApplyState -Repository $Repository -Manifest $data -ReceiptDirectory $ReceiptDirectory -ManifestSha256 $ManifestSha256
        $references = Get-YLResourceRetirementReferenceState $Repository $data (Join-Path $artifactRoot 'Output')
        $retirementChecked = $false
        for ($index = 0; $index -lt $data.operations.Count; $index++) {
            $operation = $data.operations[$index]
            $summary.conflict = $operation.path
            # A batch preflight is not a write permit. Recheck all frozen inputs and this item's state now.
            $completed = Assert-YLResourceMigrationApplyState -Repository $Repository -Manifest $data -ReceiptDirectory $ReceiptDirectory -ManifestSha256 $ManifestSha256
            if ($completed.Contains($operation.path)) { continue }
            if ($operation.action -ceq 'Delete' -and -not $retirementChecked) {
                $current = Get-YLResourceRetirementReferenceState $Repository $data
                if ($current.sha256 -cne $references.sha256) { throw 'Retirement reference inputs changed before deletion.' }
                $retirementChecked = $true
            }
            $path = Get-YLResourceMigrationPath $Repository $operation.path
            $bytes = $null
            if ($operation.action -cne 'Delete') {
                $payload = Get-YLResourceMigrationPath $artifactRoot ('Output/' + $operation.path)
                $bytes = [IO.File]::ReadAllBytes($payload)
                if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() -cne $operation.outputSha256) { throw "Frozen candidate output changed: $($operation.path)" }
            }
            Assert-YLResourceMigrationFileState -Root $Repository -Operation $operation
            $deletion = $null
            if ($operation.action -ceq 'Add') {
                $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
                # CreateNew also rejects a file created after the immediate ExpectedAbsent check.
                $stream = [IO.File]::Open($path, 'CreateNew', 'ReadWrite', 'None')
            } elseif ($operation.action -ceq 'Delete') {
                $deletion = Open-YLResourceMigrationDeletion -Path $path -Sha256 $operation.inputSha256 -Guid $operation.oldGuid
                $stream = $deletion.Stream
            } else { $stream = [IO.File]::Open($path, 'Open', 'ReadWrite', 'None') }
            try {
                if ($operation.action -cne 'Add') {
                    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
                    if ($hash -cne $operation.inputSha256) { throw "Source changed before the write handle was acquired: $($operation.path)" }
                    $stream.Position = 0
                    $backup = Get-YLResourceMigrationPath $ReceiptDirectory ('Original/' + $operation.path)
                    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($backup))
                    if ([IO.File]::Exists($backup)) {
                        if ((Get-YLResourceMigrationHash $backup) -cne $operation.inputSha256) { throw 'Preserved original bytes changed.' }
                    } else {
                        $original = [IO.File]::Open($backup, 'CreateNew', 'Write', 'None')
                        try { $stream.CopyTo($original); $original.Flush($true) } finally { $original.Dispose() }
                    }
                    $stream.Position = 0
                }
                if ($operation.action -ceq 'Delete') { $deletion.MarkForDeletion() }
                else {
                    $stream.SetLength(0)
                    $stream.Write($bytes, 0, $bytes.Length)
                    $stream.Flush($true)
                    $stream.Position = 0
                    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() -cne $operation.outputSha256) { throw 'Write-handle output SHA verification failed.' }
                }
            } finally {
                if ($deletion) { $deletion.Dispose() } else { $stream.Dispose() }
            }
            if ($deletion) { $deletion.VerifyFinalAbsence() }
            Assert-YLResourceMigrationFileState -Root $Repository -Operation $operation -Output
            Write-YLTestJson -Path (Join-Path $ReceiptDirectory ('{0:D6}.json' -f $index)) -Value ([ordered]@{
                purpose = 'YLResource.MigrationFileApplied'; manifestSha256 = $ManifestSha256; index = $index; path = $operation.path
                action = $operation.action; outputSha256 = $operation.outputSha256; newGuid = $operation.newGuid; verified = $true; completedUtc = [DateTime]::UtcNow.ToString('o')
            })
            $summary.completed = $completed.Count + 1
        }
        $completed = Assert-YLResourceMigrationApplyState -Repository $Repository -Manifest $data -ReceiptDirectory $ReceiptDirectory -ManifestSha256 $ManifestSha256
        if ($references) {
            $current = Get-YLResourceRetirementReferenceState $Repository $data
            if ($current.sha256 -cne $references.sha256) { throw 'Retirement reference inputs changed during Apply.' }
            $summary['retirementReferences'] = $current
        }
        $summary.completed = $completed.Count
        $summary['fileReceiptsSha256'] = Get-YLResourceMigrationFileReceiptsHash $ReceiptDirectory $completed.Count
        $summary.status = 'AppliedAwaitingColdVerify'
        $summary.conflict = $null
    } catch {
        $summary['reason'] = $_.Exception.Message
        throw
    } finally {
        try {
            if ($receiptOwned -and $locked) {
                Write-YLTestJson -Path (Join-Path $ReceiptDirectory ('attempt-' + [Guid]::NewGuid().ToString('N') + '.json')) -Value $summary
            }
        } finally {
            if ($locked) { $mutex.ReleaseMutex() }
            $mutex.Dispose()
        }
    }
    return [pscustomobject]$summary
}
