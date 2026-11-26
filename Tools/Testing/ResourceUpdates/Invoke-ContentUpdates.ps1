#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param(
    [string] $WorkspaceRoot = $env:YL_TEST_WORKSPACE,
    [string] $Unity2022,
    [string] $Unity6,
    [ValidateSet('Unity2022-Plain-IL2CPP', 'Unity6-Plain-IL2CPP', 'Unity2022-Encrypted-Mono', 'Unity2022-Encrypted-IL2CPP',
        'Unity6-Encrypted-Mono', 'Unity6-Encrypted-IL2CPP', IgnoreCase = $false)]
    [string[]] $Cell,
    [switch] $CacheUsage,
    [switch] $LargeDownload,
    [switch] $SpaceFaults,
    [switch] $ResumeContracts,
    [switch] $ExpectTuned,
    [string] $CompareBaselineHost
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The content update matrix requires Windows.' }
if ($CompareBaselineHost -and (-not $LargeDownload -or -not $ExpectTuned -or -not $Cell -or $Cell.Count -ne 1)) {
    throw 'A paired transfer replay requires -LargeDownload -ExpectTuned and one cell.'
}
if ($ResumeContracts -and (-not $ExpectTuned -or -not $Cell -or @($Cell | Where-Object { $_ -notlike '*-Plain-*' }).Count -ne 0)) {
    throw 'Resume contract testing requires -ExpectTuned and explicit ordinary cells.'
}

. (Join-Path $PSScriptRoot '../UnityHost.ps1')
. (Join-Path $PSScriptRoot '../Common/Process.ps1')
. (Join-Path $PSScriptRoot '../Common/Results.ps1')
. (Join-Path $PSScriptRoot '../Common/FrozenEncryptionInputs.ps1')
. (Join-Path $PSScriptRoot '../Common/FrozenPlayerEvidence.ps1')
. (Join-Path $PSScriptRoot 'ContentUpdateBuilds.ps1')
. (Join-Path $PSScriptRoot 'ContentUpdateScenarios.ps1')

function Save-UpdateSummary {
    Write-UpdateProgress (Join-Path $result.path 'summary.json') $summary
}

$repository = Get-YLTestPath ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')))
$configuration = Read-YLPackageEvidenceJson (Join-Path $repository 'Tools/Testing/TestSuites.json')
$workspace = Initialize-YLTestWorkspace -WorkspaceRoot $WorkspaceRoot -Repository $repository
$resultKey = if ($SpaceFaults) { 'ResourceSpaceFaults' } elseif ($LargeDownload) { 'ResourceLargeDownload' }
    elseif ($ResumeContracts) { 'ResourceResumeContracts' } elseif ($CacheUsage) { 'ResourceCacheUsage' } else { 'ResourceUpdates' }
$result = New-YLTestDirectory -Workspace $workspace -Kind Result -Key $resultKey
$resultLease = Lock-YLTestDirectory -Workspace $workspace -Path $result.path
$contexts = [Collections.Generic.List[object]]::new()
$matrix = @('Unity2022-Plain-IL2CPP', 'Unity6-Plain-IL2CPP', 'Unity2022-Encrypted-Mono', 'Unity2022-Encrypted-IL2CPP',
    'Unity6-Encrypted-Mono', 'Unity6-Encrypted-IL2CPP')
$editors = @{ Unity2022 = $Unity2022; Unity6 = $Unity6 }
$versions = @{ Unity2022 = '2022.3.62f3'; Unity6 = '6000.3.27f1' }
$summary = [ordered]@{ schemaVersion = 1; purpose = 'YLResource.Win64Release.ContentUpdates'; status = 'Failed'; results = $result.path
    workspace = $workspace.root; allMatrixCells = (@($matrix | Where-Object { $Cell -and $_ -cnotin $Cell }).Count -eq 0)
    startedUtc = [DateTime]::UtcNow.ToString('o'); allASealedUtc = $null; endedUtc = $null; cells = [ordered]@{} }

try {
    if (-not ('YLFramework.Testing.FrozenLoopback' -as [type])) {
        Add-Type -Path (Join-Path $PSScriptRoot '../ResourceEncryption/FrozenLoopback.cs')
    }
    foreach ($id in $matrix) {
        if ($Cell -and $id -cnotin $Cell) { continue }
        $profile, $kind, $backend = $id.Split('-')
        $row = [ordered]@{ status = 'NotRun'; phase = 'Prerequisites'; error = $null; profile = $profile
            backend = $backend; buildFlavor = 'Release'; encrypted = $kind -ceq 'Encrypted'; host = $null; userData = $null
            results = $null; baselineEvidence = [ordered]@{}; runs = [Collections.Generic.List[object]]::new() }
        $summary.cells[$id] = $row
        try {
            $unity = $editors[$profile]
            if (-not $unity) { throw "PrerequisiteNotMet: supply -$profile with the $($versions[$profile]) Unity.exe." }
            $unity = Get-YLTestPath -Path $unity -ReadOnly
            if ([IO.Path]::GetFileName($unity) -ine 'Unity.exe' -or -not [IO.File]::Exists($unity)) {
                throw "PrerequisiteNotMet: Unity.exe does not exist at $unity."
            }
            $editor = [pscustomobject](Get-YLTestEditorProfile -Editors $configuration.editors -VersionInfo ([Diagnostics.FileVersionInfo]::GetVersionInfo($unity)))
            if ($editor.version -cne $versions[$profile]) { throw "PrerequisiteNotMet: $id requires Unity $($versions[$profile])." }
            $toolchain = @($unity) + @(Get-YLFrozenWin64Module -Unity $unity -Backend $backend)
            $row['unity'] = $unity
            $row['unityVersion'] = $editor.version
            $row['addressables'] = Get-YLUnityAddressablesVersion $editor
            $hostRecord = New-YLTestDirectory -Workspace $workspace -Kind Host -Key ('RU' + [Guid]::NewGuid().ToString('N').Substring(0, 8)) -NewOnly
            $cellResults = Join-Path $result.path $id
            $null = [IO.Directory]::CreateDirectory($cellResults)
            $context = [pscustomobject]@{
                id = $id; unity = $unity; editorVersion = $editor.version; addressables = $row.addressables; backend = $backend
                encrypted = $row.encrypted; tuned = [bool]$ExpectTuned; toolchain = $toolchain; install = Join-Path $hostRecord.path 'P'; host = $hostRecord
                results = $cellResults; report = $row; userData = Join-Path $hostRecord.path 'D'; lease = $null; server = $null
                oldAddress = 'baseline-' + [Guid]::NewGuid().ToString('N'); newAddress = ''; optionalAddress = ''; exe = $null
                keys = $null; materialA = New-UpdateMaterial; materialReplacement = $null; materialNew = $null; generation = $null
                materialOptional = $null; materialReplacementC = $null; materialNewC = $null; materialOptionalC = $null
                inventory = $null; baseline = $null; update = $null; updateC = $null; perRun = 0; online = $true; versions = $null; replay = $null
            }
            $contexts.Add($context)
            $context.lease = Lock-YLTestDirectory -Workspace $workspace -Path $hostRecord.path
            $context.server = [YLFramework.Testing.FrozenLoopback]::new()
            $context.server.Pause()
            $context.online = $false
            $row.host = $hostRecord.path
            $row.userData = $context.userData
            $row.results = $cellResults
            $row['hostId'] = $hostRecord.id
            $row.phase = 'BaselineBuild'
            Write-Host "Building sealed content A for $id in $($row.host)"
            Save-UpdateSummary
            $null = [IO.Directory]::CreateDirectory($context.userData)
            $context.keys = New-UpdateKeyMaterial $context
            $null = New-UpdateHostInputs $context $editor
            $context.baseline = Invoke-UpdateEditorBuild $context A
            Assert-UpdateBaseline $context $context.baseline
            $row.phase = 'BaselineOffline'
            $sealed = Invoke-YLFrozenReadOnlyInstallation -OwnedHostRoot $context.host.path -InstallRoot $context.install -InstallDirectoryName P -RunPlayer {
                $null = Invoke-UpdateRun $context 'baseline-offline' -Boot Baseline
                if (-not $context.encrypted) { $null = Invoke-UpdateRun $context 'baseline-scene-atlas-offline' -SceneAtlas }
                foreach ($conflict in @('locator', 'transform')) {
                    $null = Invoke-UpdateRun $context "reject-foreign-$conflict" -Boot GuardRejected -Outcome GuardRejected -Preexisting $conflict
                }
                $null = Invoke-UpdateRun $context 'reject-concurrent-initialization' -Outcome GuardRejected -Preexisting concurrent
                $null = Invoke-UpdateRun $context 'reject-untrusted-scope' -Operation scope-guard -Outcome ScopeGuarded
            }
            $row['baselineReadOnly'] = $sealed.proof
            $row['sealedUtc'] = [DateTime]::UtcNow.ToString('o')
            $row.status = 'BaselineSealed'
        } catch {
            $row.error = $_.Exception.Message
            $row.status = if ($row.error.StartsWith('PrerequisiteNotMet:', [StringComparison]::Ordinal)) { 'PrerequisiteNotMet' } else { 'Failed' }
            Write-Warning "$id $($row.phase): $($row.error)"
        }
        Save-UpdateSummary
    }
    if (@($summary.cells.Values | Where-Object { $_.status -cne 'BaselineSealed' }).Count -eq 0) {
        $summary.allASealedUtc = [DateTime]::UtcNow.ToString('o')
        Save-UpdateSummary
        foreach ($context in $contexts) {
            try {
                $context.report.phase = 'ContentOnlyBuild'
                Write-Host "Building content B for $($context.id) using the existing A installation"
                Save-UpdateSummary
                $context.newAddress = 'chapter-' + [Guid]::NewGuid().ToString('N')
                $context.optionalAddress = 'optional-' + [Guid]::NewGuid().ToString('N')
                $context.materialReplacement = New-UpdateMaterial $context.materialA.ordinary -TransferProof
                $context.materialNew = New-UpdateMaterial $context.materialA.ordinary -TransferProof -BulkTransfer:($LargeDownload -or $ResumeContracts)
                $context.materialOptional = New-UpdateMaterial $context.materialA.ordinary
                $context.report['bFixtureCreatedUtc'] = [DateTime]::UtcNow.ToString('o')
                $context.report['chapterAddress'] = $context.newAddress
                $context.report['optionalAddress'] = $context.optionalAddress
                if ($CompareBaselineHost) {
                    $context.report.phase = 'PairedBaselineBuild'
                    Save-UpdateSummary
                    $context.replay = New-UpdateReplayContext $context $CompareBaselineHost
                }
                $sealed = Invoke-YLFrozenReadOnlyInstallation -OwnedHostRoot $context.host.path -InstallRoot $context.install -InstallDirectoryName P -RunPlayer {
                    $context.update = Invoke-UpdateEditorBuild $context B
                    Assert-UpdateInstallation $context
                    if ($CacheUsage) { Invoke-UpdateCacheScenarios $context }
                    if ($context.encrypted -and $context.tuned -and ($CacheUsage -or $LargeDownload)) {
                        Invoke-UpdateSignedCipherIdentityScenario $context
                    }
                    if ($ResumeContracts) { Invoke-UpdateResumeContracts $context }
                    if ($SpaceFaults) { Invoke-UpdateSpaceFaultScenarios $context }
                    if ($LargeDownload) {
                        if ($null -ne $context.replay) { Invoke-UpdatePairedTransferScenarios $context }
                        else { Invoke-UpdateLargeDownloadScenarios $context }
                    }
                    if (-not ($CacheUsage -or $LargeDownload -or $SpaceFaults -or $ResumeContracts)) { Invoke-UpdateScenarios $context }
                }
                Assert-UpdateInstallation $context
                $context.report['updateReadOnly'] = $sealed.proof
                $context.report.phase = 'Complete'
                $context.report.status = 'Passed'
            } catch {
                $context.report.error = $_.Exception.Message
                $context.report.status = 'Failed'
                Write-Warning "$($context.id) $($context.report.phase): $($context.report.error)"
                try { Assert-UpdateInstallation $context }
                catch { $context.report['installationError'] = $_.Exception.Message }
            }
            Save-UpdateSummary
        }
    } else {
        foreach ($row in $summary.cells.Values | Where-Object { $_.status -ceq 'BaselineSealed' }) {
            $row.status = 'BlockedByBaseline'
            $row.error = 'The complete requested group of A installations must seal before creating any B or C fixture material.'
        }
    }
    if (@($summary.cells.Values | Where-Object { $_.status -cne 'Passed' }).Count -eq 0) { $summary.status = 'Passed' }
} finally {
    try {
        foreach ($context in $contexts) {
            try {
                try {
                    if ($null -ne $context.server) { $context.server.Dispose() }
                } finally {
                    if ($null -ne $context.keys) {
                        foreach ($name in @('signingPrivateKeyPath', 'unknownPrivateKeyPath', 'k1Path', 'k2Path', 'wrongKeyPath')) {
                            if ($context.keys[$name]) { [IO.File]::Delete((Get-UpdateOwnedPath $context.host.path $context.keys[$name])) }
                        }
                    }
                }
            } catch {
                $context.report['cleanupError'] = $_.Exception.Message
                $summary.status = 'Failed'
            } finally {
                if ($null -ne $context.replay) { $context.replay.lease.Dispose() }
                if ($null -ne $context.lease) { $context.lease.Dispose() }
            }
        }
        $summary.endedUtc = [DateTime]::UtcNow.ToString('o')
        Save-UpdateSummary
    } finally { $resultLease.Dispose() }
}

if ($summary.status -cne 'Passed') { throw "Content update acceptance failed. See $($result.path)\summary.json." }
"Content update acceptance passed. See $($result.path)\summary.json."
