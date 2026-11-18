#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Snapshot', 'VerifySnapshot', 'Export', 'Preview', 'Apply', 'Verify')][string] $Action,
    [string] $Repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')),
    [string] $Snapshot,
    [string] $InputsFile,
    [string] $Inventory,
    [string] $InventorySha256,
    [string] $SupplementPath,
    [string] $SupplementSha256,
    [string] $SelectionFile,
    [string] $PreviewFile,
    [string] $CandidateInputsFile,
    [switch] $IncludeRetirement,
    [string] $Manifest,
    [string] $ManifestSha256,
    [string] $ValidationReceipt,
    [string] $ValidationSha256,
    [string] $ReceiptDirectory,
    [switch] $ExclusiveWindowConfirmed,
    [string] $LegacyExporter,
    [string] $LegacyExporterSha256,
    [string] $LegacyExporterMetaSha256,
    [switch] $CompareSource,
    [string] $Unity,
    [string] $WorkspaceRoot
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common/ResourceMigration.ps1')
$Repository = Get-YLTestPath $Repository
if ($IncludeRetirement -and ($Action -cne 'Preview' -or -not $Unity)) {
    throw 'IncludeRetirement only prepares a final candidate in a native Preview; it cannot write the source project.'
}

if ($Action -ceq 'Apply') {
    Invoke-YLResourceMigrationApply -Repository $Repository -Manifest $Manifest -ManifestSha256 $ManifestSha256 -ValidationReceipt $ValidationReceipt -ValidationSha256 $ValidationSha256 -ReceiptDirectory $ReceiptDirectory -ExclusiveWindowConfirmed:$ExclusiveWindowConfirmed | ConvertTo-Json -Depth 10
    return
}

if ($Action -ceq 'Verify') {
    if (-not $Unity -or -not $WorkspaceRoot -or -not $Manifest -or -not $ManifestSha256 -or -not $ReceiptDirectory) { throw 'Verify requires explicit Unity, WorkspaceRoot, frozen manifest and Apply receipts; it never opens the source project.' }
    $Manifest = Get-YLTestPath $Manifest
    if ([IO.Path]::GetFileName($Manifest) -cne 'migration.json') { throw 'Verify expects the owned frozen migration.json.' }
    & (Join-Path $PSScriptRoot 'ResourceMigrationValidation.ps1') -CandidateDirectory ([IO.Path]::GetDirectoryName($Manifest)) -Repository $Repository `
        -ManifestSha256 $ManifestSha256 -ApplyReceiptDirectory $ReceiptDirectory -WorkspaceRoot $WorkspaceRoot -Unity $Unity
    return
}

if ($Action -ceq 'Preview') {
    if ([string]::IsNullOrWhiteSpace($SelectionFile)) { throw 'Preview requires explicit SelectionFile projectGuids, allowedRoots and settingsPath; no external project is auto-selected.' }
    $selection = [IO.File]::ReadAllText((Get-YLTestPath $SelectionFile)) | ConvertFrom-Json -AsHashtable -DateKind String
    if ($selection.schemaVersion -ne 1 -or $selection.projectGuids -isnot [array] -or $selection.allowedRoots -isnot [array]) { throw 'SelectionFile requires schemaVersion 1 and explicit projectGuids/allowedRoots arrays.' }
    $fields = if ($selection.Contains('consumerFields')) { @($selection.consumerFields) } else { @() }
    $retire = if ($selection.Contains('retireProjectGuids')) { @($selection.retireProjectGuids) } else { @() }
    $preview = New-YLResourceMigrationPreview -Repository $Repository -Snapshot $Snapshot -Inventory $Inventory -InventorySha256 $InventorySha256 -ProjectGuids $selection.projectGuids -SettingsPath $selection.settingsPath -AllowedRoots $selection.allowedRoots -ConsumerFields $fields -SupplementPath $SupplementPath -SupplementSha256 $SupplementSha256 -RetireProjectGuids $retire
    if (-not [string]::IsNullOrWhiteSpace($PreviewFile)) {
        $PreviewFile = Get-YLTestPath $PreviewFile
        if ($PreviewFile.StartsWith($Repository + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Keep Preview artifacts outside the source repository.' }
        Write-YLTestJson -Path $PreviewFile -Value $preview
    }
    if (-not [string]::IsNullOrWhiteSpace($Unity)) {
        if ([string]::IsNullOrWhiteSpace($WorkspaceRoot) -or [string]::IsNullOrWhiteSpace($CandidateInputsFile)) { throw 'Native Preview requires owned WorkspaceRoot and explicit CandidateInputsFile.' }
        . (Join-Path $PSScriptRoot 'ResourceMigrationHost.ps1')
        Invoke-YLResourceMigrationCandidate -Preview $preview -Unity $Unity -WorkspaceRoot $WorkspaceRoot -CandidateInputsFile $CandidateInputsFile -IncludeRetirement:$IncludeRetirement | ConvertTo-Json -Depth 12
    } else { $preview | ConvertTo-Json -Depth 60 }
    return
}

$Snapshot = Get-YLTestPath $Snapshot

if ($Action -eq 'Export') {
    if ([string]::IsNullOrWhiteSpace($Unity) -or [string]::IsNullOrWhiteSpace($WorkspaceRoot)) {
        throw 'Export requires explicit Unity and owned WorkspaceRoot paths.'
    }
    . (Join-Path $PSScriptRoot 'ResourceMigrationHost.ps1')
    Invoke-YLResourceLegacyExport -Repository $Repository -Snapshot $Snapshot -Unity $Unity -WorkspaceRoot $WorkspaceRoot -LegacyExporter $LegacyExporter -LegacyExporterSha256 $LegacyExporterSha256 -LegacyExporterMetaSha256 $LegacyExporterMetaSha256
    return
}

if ($Action -eq 'VerifySnapshot') {
    Test-YLResourceSnapshot -Path $Snapshot -CompareSource:$CompareSource | ConvertTo-Json -Depth 10
    return
}

if ([string]::IsNullOrWhiteSpace($InputsFile)) { throw 'Snapshot requires an explicit InputsFile.' }
$InputsFile = Get-YLTestPath $InputsFile
$inputs = [IO.File]::ReadAllText($InputsFile) | ConvertFrom-Json -AsHashtable -DateKind String
if ($inputs.schemaVersion -ne 1 -or $inputs.inputPaths -isnot [array] -or $inputs.inputPaths.Count -eq 0) {
    throw 'InputsFile must declare schemaVersion 1 and non-empty inputPaths.'
}
$gitRoot = & git -C $Repository rev-parse --show-toplevel
if ($LASTEXITCODE -ne 0 -or (Get-YLTestPath $gitRoot) -ine $Repository) { throw 'Repository must be the actual Git root.' }
$relativeSnapshot = [IO.Path]::GetRelativePath($Repository, $Snapshot).Replace('\', '/')
& git -C $Repository check-ignore --quiet --no-index -- ($relativeSnapshot + '/snapshot.json')
if ($LASTEXITCODE -ne 0) { throw 'Configure the exact local archive exclusion before copying.' }
$tracked = @(& git -C $Repository ls-files -- 'LocalArchives/ResourceLegacy/')
if ($LASTEXITCODE -ne 0 -or $tracked.Count -gt 0) { throw 'Local research snapshots must not be tracked by Git.' }
$commit = & git -C $Repository rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve the source commit.' }
$statusBefore = @(& git -C $Repository status --porcelain=v1)
if ($LASTEXITCODE -ne 0) { throw 'Cannot record the source worktree state.' }

$null = New-YLResourceSnapshot -Repository $Repository -Destination $Snapshot -InputPaths $inputs.inputPaths -SourceCommit $commit
$patch = @(& git -C $Repository diff --no-ext-diff --binary HEAD -- @($inputs.inputPaths))
if ($LASTEXITCODE -ne 0) { throw 'Cannot record the selected source differences.' }
$statusAfter = @(& git -C $Repository status --porcelain=v1)
if ($LASTEXITCODE -ne 0) { throw 'Cannot recheck the source worktree state.' }
$commitAfter = & git -C $Repository rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $commitAfter -cne $commit -or
    ($statusBefore -join "`n") -cne ($statusAfter -join "`n")) {
    throw 'Source Git baseline changed during snapshot; preserve this snapshot and retry with a new destination.'
}
$verified = Test-YLResourceSnapshot -Path $Snapshot -CompareSource
Write-YLTestJson -Path (Join-Path $Snapshot 'baseline.json') -Value ([ordered]@{
    schemaVersion = 1
    sourceCommit = $commit
    worktreeStatus = $statusBefore
    inputManifest = $inputs
    inputManifestSha256 = (Get-FileHash -LiteralPath $InputsFile -Algorithm SHA256).Hash.ToLowerInvariant()
    snapshotManifestSha256 = $verified.manifestSha256
    powershell = $PSVersionTable.PSVersion.ToString()
    preservation = 'Source is a byte-for-byte local copy; restore only into a separate owned project. Preserve metadata. Resolve packages using the captured manifests, without importing this archive into Assets or PackageSources.'
})
$patchBytes = [Text.UTF8Encoding]::new($false).GetBytes(($patch -join "`n") + "`n")
$stream = [IO.File]::Open((Join-Path $Snapshot 'worktree.patch'), 'CreateNew', 'Write', 'None')
try { $stream.Write($patchBytes, 0, $patchBytes.Length); $stream.Flush($true) }
finally { $stream.Dispose() }
$verified | ConvertTo-Json -Depth 10
