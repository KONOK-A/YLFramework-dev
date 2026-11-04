#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $WorkspaceRoot,
    [Parameter(Mandatory)][string] $ResultRoot
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Common/Workspace.ps1')
. (Join-Path $PSScriptRoot '../Common/Results.ps1')
. (Join-Path $PSScriptRoot '../Common/Packages.ps1')
. (Join-Path $PSScriptRoot '../UnityHost.ps1')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$workspace = Initialize-YLTestWorkspace -WorkspaceRoot $WorkspaceRoot -Repository $repository
$ResultRoot = Get-YLTestPath $ResultRoot
$resultOwner = Read-YLPackageEvidenceJson (Join-Path $ResultRoot '.yl-owner.json')
$result = New-YLTestContainer -Workspace $workspace -Path $ResultRoot -Kind Result -Key $resultOwner.key -ExistingOnly
$leases = [Collections.Generic.List[IDisposable]]::new()
try {
    $leases.Add((Lock-YLTestDirectory -Workspace $workspace -Path $result.path))
    $summary = Read-YLPackageEvidenceJson (Join-Path $result.path 'summary.json') | ConvertTo-Json -Depth 100 | ConvertFrom-Json -DateKind String
    if ($summary.ownerId -cne $result.id -or $summary.status -cne 'Passed' -or $summary.action -cne 'Run' -or $summary.suite -cne 'Special' -or
        $summary.module -cne 'MigrationFixtures' -or $summary.testResult.status -cne 'Passed' -or $summary.testResult.failed -ne 0 -or $summary.testResult.skipped -ne 0 -or
        $summary.testResult.total -ne 1 -or $summary.processes.Count -ne 2) { throw 'Only the complete passed fresh MigrationFixtures producer may be exported.' }
    foreach ($process in $summary.processes) { Assert-YLTestProcessSucceeded $process }
    $hostPath = Get-YLTestPath $summary.host.path
    $hostOwner = Read-YLPackageEvidenceJson (Join-Path $hostPath '.yl-owner.json')
    $hostRecord = New-YLTestContainer -Workspace $workspace -Path $hostPath -Kind Host -Key $hostOwner.key -ExistingOnly
    if ($hostRecord.id -cne $summary.host.id) { throw 'The producer host owner changed.' }
    $leases.Add((Lock-YLTestDirectory -Workspace $workspace -Path $hostRecord.path))
    Assert-YLUnityHostIdle $hostRecord
    $selection = [IO.File]::ReadAllText((Get-YLTestPath $summary.selection)) | ConvertFrom-Json -DateKind String
    $null = Read-YLTestXml -Path $summary.xml[0] -Selection $selection -InputIdentity $summary.inputIdentity -Mode EditMode -ProcessResult $summary.processes[-1]
    $relativeRoot = 'Assets/Tests/YLFramework/Fixtures/NamespaceMigration/AssemblySplitV1'
    $source = Get-YLTestPath (Join-Path $hostRecord.path $relativeRoot)
    $destination = Get-YLTestPath (Join-Path $repository $relativeRoot)
    if ([IO.Path]::Exists($destination) -or [IO.Path]::Exists($destination + '.meta')) { throw 'Never overwrite an existing migration fixture or its identity.' }
    if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($destination))) { throw 'Missing maintained fixture parent.' }
    $identity = Read-YLPackageEvidenceJson (Join-Path $source 'FixtureIdentity.json')
    if ($identity.schemaVersion -isnot [long] -or $identity.schemaVersion -ne 1 -or $identity.fixtureRoot -cne $relativeRoot -or
        $identity.captureKind -cne 'GeneratedCurrentLayoutBeforeAssemblySplit' -or $identity.sourceAssembly -cne 'YLFramework.Runtime' -or
        $identity.unityVersion -cne $summary.unity.actualVersion -or $identity.assets.Count -ne 14 -or $identity.workbooks.Count -ne 6) { throw 'Unexpected generated fixture identity.' }
    $declared = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in $identity.files) {
        Assert-YLPackageCatalogPath $file.path
        if (-not $declared.Add($file.path) -or $file.length -isnot [long] -or $file.length -lt 0 -or $file.sha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid/duplicate captured fixture file.' }
        $path = Get-YLTestPath (Join-Path $source $file.path)
        if (([IO.FileInfo]::new($path)).Length -ne $file.length -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.sha256) { throw "Captured fixture input changed: $($file.path)" }
    }
    $null = $declared.Add('FixtureIdentity.json'); $null = $declared.Add('FixtureIdentity.json.meta')
    $files = @(Get-YLTestTreeFiles $source)
    $actual = [string[]]@($files | ForEach-Object { [IO.Path]::GetRelativePath($source, $_).Replace('\', '/') })
    if ($actual.Count -ne $declared.Count -or -not $declared.SetEquals($actual) -or @($actual | Where-Object { $_ -match '(?i)\.(?:cs|asmdef|asmref|exe|dll)$' }).Count -ne 0) { throw 'Generated fixture closure contains missing, unexpected or executable inputs.' }
    $rootMeta = Get-YLTestPath ($source + '.meta')
    $null = [IO.Directory]::CreateDirectory($destination)
    foreach ($path in $files) {
        $relative = [IO.Path]::GetRelativePath($source, $path)
        $target = Join-Path $destination $relative
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        [IO.File]::Copy($path, $target)
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) { throw "Fixture export byte mismatch: $relative" }
    }
    [IO.File]::Copy($rootMeta, $destination + '.meta')
    Write-YLTestJson -Path (Join-Path $result.path 'fixture-export.json') -Value @{
        schemaVersion = 1; purpose = 'YLTesting.AssemblyMigrationFixtureExport'; sourceResultId = $result.id; hostId = $hostRecord.id
        sourceInputHash = $summary.inputIdentity.sha256; destination = $relativeRoot; capturedIdentitySha256 = (Get-FileHash -LiteralPath (Join-Path $destination 'FixtureIdentity.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        fileCount = $files.Count; assets = $identity.assets.Count; workbooks = $identity.workbooks.Count; catalogDigest = $identity.catalog.catalogDigest
    }
    "Exported verified current-layout fixture: $relativeRoot ($($files.Count) files; Unity $($identity.unityVersion))."
} finally { foreach ($lease in $leases) { $lease.Dispose() } }
