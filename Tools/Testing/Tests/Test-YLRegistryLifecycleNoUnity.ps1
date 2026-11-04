#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param([Parameter(Mandatory)][string] $TemporaryRoot)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Common/Workspace.ps1')
$checks = 0
function Check([bool] $Condition, [string] $Reason) { if (-not $Condition) { throw $Reason }; $script:checks++ }
function Reject([scriptblock] $Action, [string] $Pattern) {
    try { & $Action | Out-Null } catch {
        if ($_.Exception -is [Management.Automation.CommandNotFoundException] -or $_.Exception.Message -notmatch $Pattern) { throw }
        $script:checks++; return
    }
    throw "Expected refusal: $Pattern"
}
function Save([string] $Path, [object] $Value) {
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 40), [Text.UTF8Encoding]::new($false))
}
function Copy-Value([object] $Value) { return ,(ConvertTo-Json -InputObject $Value -Depth 40 | ConvertFrom-Json -AsHashtable -NoEnumerate -DateKind String) }
$common = Join-Path $PSScriptRoot '../Common/RegistryLifecycle.ps1'
Check ([IO.File]::Exists($common)) 'Lifecycle behavior is missing: no independent Registry lifecycle operation exists.'
. $common
Check ($null -ne (Get-Command Invoke-YLRegistryLifecycle -ErrorAction SilentlyContinue)) 'The shared lifecycle operation is missing.'
if (-not [IO.Directory]::Exists($TemporaryRoot)) { throw 'TemporaryRoot must be an explicit existing directory.' }
$root = Join-Path (Get-YLTestPath $TemporaryRoot) ('yl-lifecycle-contract-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($root)
try {
    $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
    $workspace = Initialize-YLTestWorkspace -WorkspaceRoot (Join-Path $root 'Workspace') -Repository $repository
    $run = New-YLTestDirectory -Workspace $workspace -Kind Result -Key LifecycleContracts
    $version = '0.1.0-preview.1'; $url = 'https://package.openupm.com'
    . (Join-Path $PSScriptRoot 'PackageCatalogFixtures.ps1')
    $packages = @(foreach ($id in @((Read-YLPackageCatalog).packages.id)) {
        $deps = Get-YLFixtureInternalDependencies -PackageId $id -Version $version
        if ($id.EndsWith('.hybridclr')) { $deps['com.code-philosophy.hybridclr'] = '9.0.3' }
        $line = 'package.json' + [char]0 + ('b' * 64) + "`n"
        @{ id = $id; version = $version; dependencies = $deps; assetName = "$id-$version.tgz"; sha256 = ('a' * 64)
            integrity = 'sha512-' + [Convert]::ToBase64String([byte[]]::new(64)); files = @(@{ path = 'package.json'; sha256 = ('a' * 64); contentSha256 = ('b' * 64) })
            payloadSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($line))).ToLowerInvariant() }
    })
    $releasePath = Join-Path $root 'release-manifest.json'
    # Synthetic protocol fixture only, not a claimed CI run or publishable release.
    Save $releasePath @{ schemaVersion = 1; sourceCommit = ('a' * 40); toolchain = @{ node = '22.23.3'; npm = '10.9.9'; powershell = '7.6.9' }
        producer = @{ kind = 'GitHubActions'; repository = 'KONOK-A/YLFramework-dev'; workflow = '.github/workflows/dotnet.yml'; runId = '1'; runAttempt = '1' }; packages = $packages }
    $source = Get-YLRegistrySource -PackageSource Registry -Arguments @{ RegistryUrl = $url; RegistryVersion = $version; ReleaseManifest = $releasePath }
    $exe = (Get-Process -Id $PID).Path
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
    # Only the external Editor/UPM boundary is substituted. Ownership, files, reports and the entry run for real.
    $editor = [pscustomobject]@{ version = '2022.3.62f3'; productVersion = $info.ProductVersion; fileVersion = "$($info.FileMajorPart).$($info.FileMinorPart).$($info.FileBuildPart)" }
    $arguments = @{ Workspace = $workspace; Source = $source; Unity = $exe; Editor = $editor; EntryPackage = 'com.kano.ylframework.domain-excel'; Result = $run; TimeoutSeconds = 30 }
    foreach ($origin in @('File', 'GitRemote', 'LocalTarball')) {
        $bad = $source.Clone(); $bad.packageOrigin = $origin
        $badArguments = $arguments.Clone(); $badArguments.Source = $bad
        Reject { Invoke-YLRegistryLifecycle @badArguments } 'Registry'
    }
    $references = @([IO.Directory]::EnumerateFiles((Join-Path $PSHOME 'ref'), '*.dll'))
    $null = Add-Type -Path @((Join-Path $PSScriptRoot '../RegistryLifecycleEntry.cs'), (Join-Path $PSScriptRoot 'RegistryLifecycleUnityStubs.cs')) -ReferencedAssemblies $references -CompilerOptions @('/define:UNITY_EDITOR', '/langversion:9.0')
    $entryType = [YLFramework.Testing.RegistryLifecycleEntry]
    $begin = $entryType.GetMethod('Begin', [Reflection.BindingFlags]'NonPublic,Static')
    $resume = $entryType.GetMethod('Resume', [Reflection.BindingFlags]'NonPublic,Static')
    function Call-Entry([Reflection.MethodInfo] $Method, [object[]] $Values) {
        try { $null = $Method.Invoke($null, $Values) } catch { throw $_.Exception.GetBaseException() }
    }
    Reject { [YLFramework.Testing.RegistryLifecycleEntry]::Execute() } 'request|explicit'
    Check ([UnityEditor.EditorApplication]::ExitCodes.Count -eq 0) 'An invocation without a request acquired exit authority.'

    $script:fault = ''; $script:scenarioEntry = 'com.kano.ylframework.domain-excel'; $script:reload = $false; $script:guardsChecked = $false; $script:starts = [Collections.Generic.List[object]]::new()
    function Invoke-YLTestProcess {
        param([Diagnostics.ProcessStartInfo] $StartInfo, [string] $Log, [int] $TimeoutMilliseconds)
        $argv = [string[]]@($StartInfo.ArgumentList)
        Check ('-quit' -cnotin $argv -and '-batchmode' -cin $argv) 'An asynchronous lifecycle operation must not use -quit or an interactive Editor.'
        $requestPath = $argv[[Array]::IndexOf($argv, '-ylRegistryLifecycleRequest') + 1]
        $request = Read-YLPackageEvidenceJson $requestPath
        $script:starts.Add(@{ host = $StartInfo.WorkingDirectory; cache = $StartInfo.Environment['UPM_CACHE_ROOT']; phase = $request.phase })
        [UnityEngine.Application]::dataPath = Join-Path $StartInfo.WorkingDirectory 'Assets'
        [UnityEngine.Application]::isBatchMode = $true
        [UnityEditor.EditorApplication]::applicationPath = $StartInfo.FileName.Replace('\', '/')
        [UnityEditor.EditorApplication]::Reset()
        [UnityEditor.EditorUtility]::scriptCompilationFailed = $script:fault -ceq 'compile'
        $saved = @{}
        foreach ($key in @('UPM_CACHE_ROOT', 'UPM_CACHE_PATH', 'UPM_NPM_CACHE_PATH')) {
            $saved[$key] = [Environment]::GetEnvironmentVariable($key)
            [Environment]::SetEnvironmentVariable($key, $StartInfo.Environment[$key])
        }
        try {
            $hostRoot = $StartInfo.WorkingDirectory
            $manifestPath = Join-Path $hostRoot 'Packages/manifest.json'
            $lockPath = Join-Path $hostRoot 'Packages/packages-lock.json'
            if ($request.phase -ceq 'Inspect') {
                # This literal fixture is independent of the request's expected closure.
                $ids = switch -CaseSensitive ($script:scenarioEntry) {
                    'com.kano.ylframework' { @('com.kano.ylframework') }
                    'com.kano.ylframework.data-assets' { @('com.kano.ylframework', 'com.kano.ylframework.data-assets') }
                    'com.kano.ylframework.excel' { @('com.kano.ylframework', 'com.kano.ylframework.data-assets', 'com.kano.ylframework.excel') }
                    'com.kano.ylframework.domain-excel' { @('com.kano.ylframework', 'com.kano.ylframework.data-assets', 'com.kano.ylframework.domain-excel', 'com.kano.ylframework.excel') }
                    'com.kano.ylframework.dotween' { @('com.kano.ylframework', 'com.kano.ylframework.dotween') }
                    'com.kano.ylframework.hybridclr' { @('com.kano.ylframework', 'com.kano.ylframework.hybridclr', 'com.code-philosophy.hybridclr') }
                    default { throw 'Unknown contract scenario.' }
                }
                $apis = @(foreach ($id in $ids) {
                    $api = [UnityEditor.PackageManager.PackageInfo]::new()
                    $api.name = $id; $api.version = $(if ($id -ceq 'com.code-philosophy.hybridclr') { '9.0.3' } else { '0.1.0-preview.1' }); $api.packageId = $id + '@' + $api.version
                    $api.source = [UnityEditor.PackageManager.PackageSource]::Registry; $api.isDirectDependency = $id -ceq $script:scenarioEntry
                    $api.resolvedPath = Join-Path $hostRoot ('Library/PackageCache/' + $id)
                    $null = [IO.Directory]::CreateDirectory($api.resolvedPath)
                    [IO.File]::WriteAllText((Join-Path $api.resolvedPath 'cached.bytes'), 'cache may survive removal')
                    $api.registry = [UnityEditor.PackageManager.RegistryInfo]::new(); $api.registry.url = 'https://package.openupm.com'
                    $deps = if ($id -ceq 'com.code-philosophy.hybridclr') { @{} } else { Get-YLFixtureInternalDependencies -PackageId $id -Version $version }
                    if ($id -ceq 'com.kano.ylframework.hybridclr') { $deps['com.code-philosophy.hybridclr'] = '9.0.3' }
                    $api.dependencies = @($deps.GetEnumerator() | ForEach-Object { $d = [UnityEditor.PackageManager.DependencyInfo]::new(); $d.name = $_.Key; $d.version = $_.Value; $d })
                    $api
                })
                [UnityEditor.PackageManager.PackageInfo]::Registered = $apis
                $lock = @{ dependencies = @{} }
                foreach ($api in $apis) {
                    $deps = @{}; foreach ($d in $api.dependencies) { $deps[$d.name] = $d.version }
                    $lock.dependencies[$api.name] = @{ version = $api.version; source = 'registry'; url = 'https://package.openupm.com'; dependencies = $deps }
                }
                $depths = @{ $script:scenarioEntry = 0 }; $pending = [Collections.Generic.Queue[string]]::new(); $pending.Enqueue($script:scenarioEntry)
                while ($pending.Count -gt 0) {
                    $id = $pending.Dequeue(); $lock.dependencies[$id].depth = $depths[$id]
                    foreach ($dependency in $lock.dependencies[$id].dependencies.Keys) {
                        if (-not $depths.ContainsKey($dependency)) { $depths[$dependency] = $depths[$id] + 1; $pending.Enqueue($dependency) }
                    }
                }
                Check ($depths.Count -eq $apis.Count) 'The observed fixture contains a package outside its declared dependency closure.'
                if ($script:scenarioEntry -ceq 'com.kano.ylframework.domain-excel') { Check ($depths['com.kano.ylframework.data-assets'] -eq 2) 'DomainExcel must reach DataAssets through its Excel backend.' }
                Save $lockPath $lock
                if ($script:fault -ceq 'lock-depth') { $lock.dependencies[$script:scenarioEntry].depth = 7; Save $lockPath $lock }
                if ($script:fault -ceq 'transitive-lock-depth') { $lock.dependencies['com.kano.ylframework.data-assets'].depth = 1; Save $lockPath $lock }
                if ($script:fault -ceq 'missing-backend') { [UnityEditor.PackageManager.PackageInfo]::Registered = @($apis | Where-Object name -cne 'com.kano.ylframework.data-assets'); $lock.dependencies.Remove('com.kano.ylframework.data-assets'); Save $lockPath $lock }
                if ($script:fault -ceq 'direct-dependency') { [UnityEditor.PackageManager.PackageInfo]::Registered[0].isDirectDependency = $true }
            }
            if ($script:fault -ceq 'warm-path-drift' -and $request.phase -ceq 'Reopen') { [UnityEditor.PackageManager.PackageInfo]::Registered[0].resolvedPath = Join-Path $request.cacheRoot 'Upm/ChangedIdentity' }
            if ($script:fault -ceq 'manifest-extra') { $m = Read-YLPackageEvidenceJson $manifestPath; $m.unapproved = $true; Save $manifestPath $m }
            if ($script:fault -ceq 'cache-environment') { [Environment]::SetEnvironmentVariable('UPM_CACHE_PATH', (Join-Path $root 'foreign-cache')) }
            [UnityEditor.PackageManager.Client]::Configure($manifestPath, $lockPath, ($script:fault -ceq 'remove-failed'), ($script:fault -ceq 'indirect-remains'))
            if ($script:fault -ceq 'request-only') { [UnityEditor.PackageManager.PackageInfo]::Registered = @(); Save $lockPath @{ dependencies = @{} } }
            $values = [object[]]::new(1); $values[0] = $argv
            if (-not $script:guardsChecked) {
                $requestBytes = [IO.File]::ReadAllBytes($requestPath)
                foreach ($field in $request.Keys) {
                    $bad = Copy-Value $request; $bad[$field] = $true
                    try { Save $requestPath $bad; Reject { Call-Entry $begin $values } 'field|schema|string|integer|identity|request' }
                    finally { [IO.File]::WriteAllBytes($requestPath, $requestBytes) }
                }
                foreach ($field in @('hostId', 'cacheId', 'resultId', 'workspaceId', 'inputHash', 'releaseManifestSha256', 'inputReceiptSha256', 'neutralReceiptSha256', 'originalManifestSha256')) {
                    $bad = Copy-Value $request; $bad[$field] = $(if ($field.EndsWith('Id')) { '0' * 32 } else { '0' * 64 })
                    try { Save $requestPath $bad; Reject { Call-Entry $begin $values } 'owner|identity|receipt|source|input|hash' }
                    finally { [IO.File]::WriteAllBytes($requestPath, $requestBytes) }
                }
                foreach ($field in @('output', 'hostRoot', 'cacheRoot', 'resultRoot', 'lifecycleRoot', 'neutralReceipt', 'releaseManifest', 'originalManifest')) {
                    $bad = Copy-Value $request; $bad[$field] = $root
                    try { Save $requestPath $bad; Reject { Call-Entry $begin $values } 'owner|path|output|file|directory|input|source' }
                    finally { [IO.File]::WriteAllBytes($requestPath, $requestBytes) }
                }
                foreach ($ownerPath in @((Join-Path $request.hostRoot '.yl-owner.json'), (Join-Path $request.cacheRoot '.yl-owner.json'), (Join-Path $request.resultRoot '.yl-owner.json'), (Join-Path $request.workspaceRoot '.yl-workspace.json'))) {
                    $originalOwner = [IO.File]::ReadAllBytes($ownerPath)
                    foreach ($field in @('schemaVersion', 'purpose', 'id')) {
                        $bad = Read-YLPackageEvidenceJson $ownerPath; $bad[$field] = $true
                        try { Save $ownerPath $bad; Reject { Call-Entry $begin $values } 'owner|field|integer|string' }
                        finally { [IO.File]::WriteAllBytes($ownerPath, $originalOwner) }
                    }
                }
                foreach ($invalidJson in @('{"schemaVersion":1,"schemaVersion":1}', ([Text.Encoding]::UTF8.GetString($requestBytes) + '{}'), '{"schemaVersion":1,}', '/*invalid*/{}')) {
                    try { [IO.File]::WriteAllText($requestPath, $invalidJson); Reject { Call-Entry $begin $values } 'JSON' }
                    finally { [IO.File]::WriteAllBytes($requestPath, $requestBytes) }
                }
                [UnityEngine.Application]::isBatchMode = $false
                Reject { Call-Entry $begin $values } 'batch|authority'
                [UnityEngine.Application]::isBatchMode = $true
                $quitValues = [object[]]::new(1); $quitValues[0] = [string[]]@($argv + '-quit')
                Reject { Call-Entry $begin $quitValues } 'batch|authority'
                $duplicate = [object[]]::new(1); $duplicate[0] = [string[]]@($argv + @('-ylRegistryLifecycleRequest', $requestPath))
                Reject { Call-Entry $begin $duplicate } 'unique|request'
                Check ([UnityEditor.EditorApplication]::ExitCodes.Count -eq 0 -and -not [IO.File]::Exists((Join-Path $request.output 'report.json')) -and [UnityEditor.PackageManager.Client]::RemoveCalls -eq 0) 'A rejected request gained exit/write/remove authority.'
                Call-Entry $begin $values
                $changedRequest = Copy-Value $request; $changedRequest.requestId = [Guid]::NewGuid().ToString('N')
                try { Save $requestPath $changedRequest; Reject { [UnityEditor.EditorApplication]::Pump() } 'request|identity|digest' }
                finally { [IO.File]::WriteAllBytes($requestPath, $requestBytes); [UnityEditor.EditorApplication]::Reset() }
                Check (-not [IO.File]::Exists((Join-Path $request.output 'report.json')) -and [UnityEditor.EditorApplication]::ExitCodes.Count -eq 0) 'A changed request retained report/exit authority.'
                $script:guardsChecked = $true
            }
            Call-Entry $begin $values
            for ($i = 0; $i -lt 30 -and [UnityEditor.EditorApplication]::ExitCodes.Count -eq 0; $i++) {
                [UnityEditor.EditorApplication]::Pump()
                if ($script:reload -and $request.phase -ceq 'Remove' -and [UnityEditor.PackageManager.Client]::RemoveCalls -gt 0 -and $i -eq 2) {
                    $statePath = Join-Path $request.output 'operation.json'; $stateBytes = [IO.File]::ReadAllBytes($statePath)
                    foreach ($field in @('requestSha256', 'hostId', 'resultId', 'cacheId', 'pid', 'unexpected')) {
                        $badState = Read-YLPackageEvidenceJson $statePath
                        $badState[$field] = $(if ($field -ceq 'pid') { 0 } elseif ($field -ceq 'requestSha256') { '0' * 64 } else { '0' * 32 })
                        try { Save $statePath $badState; Reject { Call-Entry $resume $values } 'state|bound|request|owner|field' }
                        finally { [IO.File]::WriteAllBytes($statePath, $stateBytes) }
                    }
                    [UnityEditor.PackageManager.Client]::CompleteRemoval()
                    [UnityEditor.EditorApplication]::Reset()
                    Call-Entry $resume $values
                }
            }
            Check ([UnityEditor.EditorApplication]::ExitCodes.Count -eq 1) 'The owned operation did not reach a terminal report/exit.'
            if ($request.phase -ceq 'Inspect' -and $script:fault -cin @('report-pid', 'report-cache')) {
                $reportPath = Join-Path $request.output 'report.json'; $r = Read-YLPackageEvidenceJson $reportPath
                if ($script:fault -ceq 'report-pid') { $r.pid = $PID + 1 } else { $r.cacheEnvironment.UPM_CACHE_PATH = Join-Path $root 'not-owned' }
                Save $reportPath $r
            }
            if ($request.phase -ceq 'Remove' -and $script:fault -ceq 'operation-owner-report') {
                $reportPath = Join-Path $request.output 'report.json'; $r = Read-YLPackageEvidenceJson $reportPath; $r.removal.hostId = '0' * 32; Save $reportPath $r
            }
            if ($request.phase -ceq 'Inspect' -and $script:fault -cin @('neutral-receipt-drift', 'input-receipt-drift')) {
                $changedPath = if ($script:fault -ceq 'neutral-receipt-drift') { $request.neutralReceipt } else { Join-Path $request.hostRoot '.yl-inputs.json' }
                $changedReceipt = Read-YLPackageEvidenceJson $changedPath; $changedReceipt.unregistered = 'must not be adopted by the next request'; Save $changedPath $changedReceipt
            }
            if ($script:fault -ceq 'sentinel' -and $request.phase -ceq 'VerifyRemoved') { [IO.File]::AppendAllText((Join-Path $hostRoot 'Assets/RegistryLifecycleSentinels/user-state.bytes'), 'drift') }
            $code = [UnityEditor.EditorApplication]::ExitCodes[0]
            return [pscustomobject]@{ status = $(if ($code -eq 0) { 'Exited' } else { 'NonZeroExit' }); pid = $PID; observedExit = $true; exitCode = $code
                timedOut = $false; cancelled = ($script:fault -ceq 'cancelled'); forcedCleanup = ($script:fault -ceq 'forced-cleanup'); observerError = $null; cleanupError = $null; jobAssigned = $true; activeProcesses = 0; streamsDrained = $true }
        } finally { foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) } }
    }
    $outcome = Invoke-YLRegistryLifecycle @arguments
    Check ($outcome.status -ceq 'Completed' -and $outcome.phases.Count -eq 4) "A complete lifecycle must return four independently observed phase results. $($outcome.error)"
    Check (($outcome.phases.phase -join ',') -ceq 'Inspect,Reopen,Remove,VerifyRemoved') 'Lifecycle phase ordering changed.'
    Check (@($starts.host | Sort-Object -Unique).Count -eq 1 -and @($starts.cache | Sort-Object -Unique).Count -eq 1) 'Warm reopen and uninstall must preserve their own host/cache.'
    Check ($outcome.coldInstallCount -eq 1 -and $outcome.neutralInputs.files.Count -gt 4 -and $outcome.sentinels.status -ceq 'Unchanged') 'Neutral producer receipts, sentinel hashes or cold/warm classification are missing.'
    $sentinel = @($outcome.sentinels.files | Where-Object { $_.path -ceq 'Assets/RegistryLifecycleSentinels/user-state.bytes' })
    $expectedSentinelHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]](0, 255, 13, 10, 65, 0, 66))).ToLowerInvariant()
    Check ($sentinel.Count -eq 1 -and $sentinel[0].beforeSha256 -ceq $expectedSentinelHash -and $sentinel[0].afterSha256 -ceq $expectedSentinelHash) 'Sentinel summary must record both frozen and actually re-read byte hashes.'
    Check ([IO.File]::Exists((Join-Path $outcome.host.path 'Library/PackageCache/com.kano.ylframework/cached.bytes'))) 'Cached files were incorrectly deleted to force uninstall success.'
    $asmdef = Read-YLPackageEvidenceJson (Join-Path $outcome.host.path 'Assets/RegistryLifecycle/Editor/RegistryLifecycle.asmdef')
    Check ($asmdef.references.Count -eq 0 -and $asmdef.overrideReferences -and $asmdef.precompiledReferences.Count -eq 0 -and ($asmdef.includePlatforms -join ',') -ceq 'Editor') 'The neutral probe inherited product/SDK/Json.NET references or entered Player compilation.'
    foreach ($id in @('com.kano.ylframework', 'com.kano.ylframework.data-assets', 'com.kano.ylframework.excel', 'com.kano.ylframework.dotween', 'com.kano.ylframework.hybridclr')) {
        $script:scenarioEntry = $id; $caseArguments = $arguments.Clone(); $caseArguments.EntryPackage = $id
        $case = Invoke-YLRegistryLifecycle @caseArguments
        Check ($case.status -ceq 'Completed' -and $case.phases.Count -eq 4) "Frozen entry lifecycle failed for ${id}: $($case.error)"
    }
    $script:scenarioEntry = 'com.kano.ylframework.domain-excel'
    $baselineHost = $outcome.host.id
    $script:reload = $true; $starts.Clear()
    $reloaded = Invoke-YLRegistryLifecycle @arguments
    Check ($reloaded.status -ceq 'Completed' -and $reloaded.host.id -cne $baselineHost) "A second operation must allocate a new uniquely owned host. $($reloaded.error)"
    Check ($reloaded.phases[2].report.removal.completion -ceq 'ResolvedAfterReload' -and $reloaded.phases[2].report.removal.requestStatusObserved -is [bool] -and
        -not $reloaded.phases[2].report.removal.requestStatusObserved -and $reloaded.phases[2].report.removal.requestStatus -ceq '') 'A lost RemoveRequest must not be fabricated as observed Success.'
    $script:reload = $false
    foreach ($faultName in @('compile', 'request-only', 'remove-failed', 'indirect-remains', 'manifest-extra', 'sentinel', 'cache-environment', 'warm-path-drift', 'lock-depth', 'transitive-lock-depth', 'missing-backend', 'direct-dependency', 'report-pid', 'report-cache', 'cancelled', 'forced-cleanup', 'operation-owner-report', 'neutral-receipt-drift', 'input-receipt-drift')) {
        $script:fault = $faultName
        $failed = Invoke-YLRegistryLifecycle @arguments
        Check ($failed.status -ceq 'Failed' -and $failed.error.Length -gt 0) "Lifecycle incorrectly accepted actual $faultName evidence."
        Check ($failed.phases.Count -gt 0 -and $failed.phases[-1].status -ceq 'Failed') "Failed $faultName phase evidence was dropped from the returned outcome."
        if ($faultName -ceq 'sentinel') { Check ($failed.sentinels.status -ceq 'ChangedOrUnavailable') 'A changed sentinel retained a stale Unchanged summary.' }
    }
    "PASS: $checks Registry lifecycle NoUnity behavior contracts. NOT Unity compilation, installation or PassedRegistry."
} finally {
    if ([IO.Directory]::Exists($root)) { $null = @(Get-YLTestTreeFiles $root); [IO.Directory]::Delete($root, $true) }
}
