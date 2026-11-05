#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param([Parameter(Mandatory)][string] $TemporaryRoot)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Common/Workspace.ps1')
$checks = 0
$completed = $false
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
        $archive = Join-Path $root "$id-$version.tgz"
        [IO.File]::WriteAllText($archive, 'NoUnity protocol bytes only, not an installable or publishable release.')
        @{ id = $id; version = $version; dependencies = $deps; assetName = "$id-$version.tgz"; sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
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
    $roots = @('com.kano.ylframework.excel', 'com.kano.ylframework.gameplay')
    $arguments = @{ Workspace = $workspace; Source = $source; Unity = $exe; Editor = $editor; EntryPackage = $roots[0]; EntryPackages = $roots; Result = $run; TimeoutSeconds = 30 }
    Check ((Get-Command Invoke-YLRegistryLifecycle).Parameters.ContainsKey('EntryPackages')) 'Lifecycle must accept the explicit composite roots instead of the retired DomainExcel root.'
    foreach ($origin in @('File', 'GitRemote')) {
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

    $script:fault = ''; $script:scenarioEntry = $roots[0]; $script:scenarioRoots = $roots; $script:reload = $false; $script:legacy = $false; $script:guardsChecked = $false; $script:starts = [Collections.Generic.List[object]]::new()
    function Invoke-YLTestProcess {
        param([Diagnostics.ProcessStartInfo] $StartInfo, [string] $Log, [int] $TimeoutMilliseconds)
        $argv = [string[]]@($StartInfo.ArgumentList)
        Check ('-quit' -cnotin $argv -and '-batchmode' -cin $argv) 'An asynchronous lifecycle operation must not use -quit or an interactive Editor.'
        $requestPath = $argv[[Array]::IndexOf($argv, '-ylRegistryLifecycleRequest') + 1]
        $request = Read-YLPackageEvidenceJson $requestPath
        if ($script:legacy) { $request.Remove('entryPackages'); $request.Remove('verifyCombinations'); Save $requestPath $request }
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
                $fixtureIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                $fixturePending = [Collections.Generic.Queue[string]]::new([string[]]$script:scenarioRoots)
                while ($fixturePending.Count) {
                    $id = $fixturePending.Dequeue(); if (-not $fixtureIds.Add($id)) { continue }
                    if ($id -ceq 'com.code-philosophy.hybridclr') { continue }
                    foreach ($dependency in (Get-YLFixtureInternalDependencies -PackageId $id -Version $version).Keys) { $fixturePending.Enqueue($dependency) }
                    if ($id -ceq 'com.kano.ylframework.hybridclr') { $fixturePending.Enqueue('com.code-philosophy.hybridclr') }
                }
                $ids = @($fixtureIds | Sort-Object -CaseSensitive)
                $manifest = Read-YLPackageEvidenceJson $manifestPath
                $apis = @(foreach ($id in $ids) {
                    $api = [UnityEditor.PackageManager.PackageInfo]::new()
                    $api.name = $id; $api.version = $(if ($id -ceq 'com.code-philosophy.hybridclr') { '9.0.3' } else { '0.1.0-preview.1' })
                    $local = $request.packageOrigin -ceq 'LocalTarball' -and $id -cne 'com.code-philosophy.hybridclr'
                    $api.packageId = $id + '@' + $(if ($local) { $manifest.dependencies[$id] } else { $api.version })
                    $api.source = if ($local) { [UnityEditor.PackageManager.PackageSource]::LocalTarball } else { [UnityEditor.PackageManager.PackageSource]::Registry }; $api.isDirectDependency = $manifest.dependencies.Contains($id)
                    $api.resolvedPath = Join-Path $hostRoot ('Library/PackageCache/' + $id)
                    $null = [IO.Directory]::CreateDirectory($api.resolvedPath)
                    [IO.File]::WriteAllText((Join-Path $api.resolvedPath 'cached.bytes'), 'cache may survive removal')
                    if (-not $local) { $api.registry = [UnityEditor.PackageManager.RegistryInfo]::new(); $api.registry.url = 'https://package.openupm.com' }
                    $deps = if ($id -ceq 'com.code-philosophy.hybridclr') { @{} } else { Get-YLFixtureInternalDependencies -PackageId $id -Version $version }
                    if ($id -ceq 'com.kano.ylframework.hybridclr') { $deps['com.code-philosophy.hybridclr'] = '9.0.3' }
                    $api.dependencies = @($deps.GetEnumerator() | ForEach-Object { $d = [UnityEditor.PackageManager.DependencyInfo]::new(); $d.name = $_.Key; $d.version = $_.Value; $d })
                    $api
                })
                [UnityEditor.PackageManager.PackageInfo]::Registered = $apis
                $lock = @{ dependencies = @{} }
                foreach ($api in $apis) {
                    $deps = @{}; foreach ($d in $api.dependencies) { $deps[$d.name] = $d.version }
                    $lock.dependencies[$api.name] = @{ version = $(if ($api.source -eq [UnityEditor.PackageManager.PackageSource]::LocalTarball) { $manifest.dependencies[$api.name] } else { $api.version }); source = $(if ($api.source -eq [UnityEditor.PackageManager.PackageSource]::LocalTarball) { 'local-tarball' } else { 'registry' }); url = 'https://package.openupm.com'; dependencies = $deps }
                }
                $depths = @{}; $pending = [Collections.Generic.Queue[string]]::new()
                foreach ($id in $manifest.dependencies.Keys) { $depths[$id] = 0; $pending.Enqueue($id) }
                while ($pending.Count -gt 0) {
                    $id = $pending.Dequeue(); $lock.dependencies[$id].depth = $depths[$id]
                    foreach ($dependency in $lock.dependencies[$id].dependencies.Keys) {
                        if (-not $depths.ContainsKey($dependency)) { $depths[$dependency] = $depths[$id] + 1; $pending.Enqueue($dependency) }
                    }
                }
                Check ($depths.Count -eq $apis.Count) 'The observed fixture contains a package outside its declared dependency closure.'
                if ($script:scenarioRoots.Count -eq 2) {
                    Check ($depths.Count -eq 4 -and $depths['com.kano.ylframework.excel'] -eq 0 -and $depths['com.kano.ylframework.gameplay'] -eq 0 -and
                        $depths['com.kano.ylframework.data-assets'] -eq $(if ($request.packageOrigin -ceq 'Registry') { 1 } else { 0 })) 'ExcelGameplay must reach its four-package closure from both direct roots.'
                }
                Save $lockPath $lock
                if ($script:fault -ceq 'lock-depth') { $lock.dependencies[$script:scenarioEntry].depth = 7; Save $lockPath $lock }
                if ($script:fault -ceq 'transitive-lock-depth') { $lock.dependencies['com.kano.ylframework.data-assets'].depth = 7; Save $lockPath $lock }
                if ($script:fault -ceq 'missing-backend') { [UnityEditor.PackageManager.PackageInfo]::Registered = @($apis | Where-Object name -cne 'com.kano.ylframework.data-assets'); $lock.dependencies.Remove('com.kano.ylframework.data-assets'); Save $lockPath $lock }
                if ($script:fault -ceq 'missing-gameplay') { [UnityEditor.PackageManager.PackageInfo]::Registered = @($apis | Where-Object name -cne 'com.kano.ylframework.gameplay'); $lock.dependencies.Remove('com.kano.ylframework.gameplay'); Save $lockPath $lock }
                if ($script:fault -ceq 'gameplay-lock-depth') { $lock.dependencies['com.kano.ylframework.gameplay'].depth = 2; Save $lockPath $lock }
                if ($script:fault -ceq 'direct-dependency') { [UnityEditor.PackageManager.PackageInfo]::Registered[0].isDirectDependency = $true }
            }
            if ($script:fault -ceq 'warm-path-drift' -and $request.phase -ceq 'Reopen') { [UnityEditor.PackageManager.PackageInfo]::Registered[0].resolvedPath = Join-Path $request.cacheRoot 'Upm/ChangedIdentity' }
            if ($script:fault -ceq 'manifest-extra') { $m = Read-YLPackageEvidenceJson $manifestPath; $m.unapproved = $true; Save $manifestPath $m }
            if ($script:fault -ceq 'cache-environment') { [Environment]::SetEnvironmentVariable('UPM_CACHE_PATH', (Join-Path $root 'foreign-cache')) }
            [UnityEditor.PackageManager.Client]::Configure($manifestPath, $lockPath, ($script:fault -ceq 'remove-failed'), ($script:fault -ceq 'indirect-remains'))
            [UnityEditor.PackageManager.Client]::FakeRestore = $script:fault -ceq 'fake-restore'
            [UnityEditor.PackageManager.Client]::PreserveRemoval = $(if ($script:fault -ceq 'incomplete-remove') { 'com.kano.ylframework.data-assets' } else { '' })
            if ($script:fault -ceq 'request-only') { [UnityEditor.PackageManager.PackageInfo]::Registered = @(); Save $lockPath @{ dependencies = @{} } }
            $values = [object[]]::new(1); $values[0] = $argv
            if (-not $script:guardsChecked) {
                $requestBytes = [IO.File]::ReadAllBytes($requestPath)
                foreach ($field in $request.Keys) {
                    $bad = Copy-Value $request; $bad[$field] = $(if ($field -ceq 'verifyCombinations') { 'not-a-boolean' } else { $true })
                    try { Save $requestPath $bad; Reject { Call-Entry $begin $values } 'field|schema|string|integer|identity|request|array|root' }
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
                foreach ($invalid in @(@{ roots = @() }, @{ roots = @($roots[0], $roots[0]) }, @{ roots = @($roots[1], $roots[0]) }, @{ roots = @($roots[0], 'unknown') }, @{ roots = @($roots[0], 'com.kano.ylframework') })) {
                    $bad = Copy-Value $request; $bad.entryPackages = $invalid.roots
                    try { Save $requestPath $bad; Reject { Call-Entry $begin $values } 'root|entry|primary|composite' }
                    finally { [IO.File]::WriteAllBytes($requestPath, $requestBytes) }
                }
                $bad = Copy-Value $request; $bad.unapproved = 'unknown fields remain forbidden'
                try { Save $requestPath $bad; Reject { Call-Entry $begin $values } 'field|schema' }
                finally { [IO.File]::WriteAllBytes($requestPath, $requestBytes) }
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
                if ($script:reload -and ([UnityEditor.PackageManager.Client]::RemoveCalls + [UnityEditor.PackageManager.Client]::AddAndRemoveCalls) -gt 0 -and $i -eq 2) {
                    $statePath = Join-Path $request.output 'operation.json'; $stateBytes = [IO.File]::ReadAllBytes($statePath)
                    foreach ($field in @('requestSha256', 'hostId', 'resultId', 'cacheId', 'pid', 'unexpected', 'phase', 'entryPackages', 'removePackages', 'addPackages')) {
                        $badState = Read-YLPackageEvidenceJson $statePath
                        if ($field -cin @('entryPackages', 'removePackages', 'addPackages')) { $badState[$field] = @('wrong') }
                        else { $badState[$field] = $(if ($field -ceq 'pid') { 0 } elseif ($field -ceq 'requestSha256') { '0' * 64 } else { '0' * 32 }) }
                        try { Save $statePath $badState; Reject { Call-Entry $resume $values } 'state|bound|request|owner|field|array|target' }
                        finally { [IO.File]::WriteAllBytes($statePath, $stateBytes) }
                    }
                    if ($script:fault -cne 'reload-unapplied') { [UnityEditor.PackageManager.Client]::CompleteRemoval() }
                    [UnityEditor.EditorApplication]::Reset()
                    Call-Entry $resume $values
                }
            }
            Check ([UnityEditor.EditorApplication]::ExitCodes.Count -eq 1) 'The owned operation did not reach a terminal report/exit.'
            Check (([UnityEditor.PackageManager.Client]::RemoveCalls + [UnityEditor.PackageManager.Client]::AddAndRemoveCalls) -le 1) 'A lost asynchronous operation was reissued instead of observed.'
            if ($request.phase -ceq 'Inspect' -and $script:fault -cin @('report-pid', 'report-cache')) {
                $reportPath = Join-Path $request.output 'report.json'; $r = Read-YLPackageEvidenceJson $reportPath
                if ($script:fault -ceq 'report-pid') { $r.pid = $PID + 1 } else { $r.cacheEnvironment.UPM_CACHE_PATH = Join-Path $root 'not-owned' }
                Save $reportPath $r
            }
            if ($request.phase -ceq 'Remove' -and $script:fault -ceq 'operation-owner-report') {
                $reportPath = Join-Path $request.output 'report.json'; $r = Read-YLPackageEvidenceJson $reportPath; $r.removal.hostId = '0' * 32; Save $reportPath $r
            }
            if ($request.phase -ceq 'RestoreExcel' -and $script:fault -ceq 'operation-target-report') {
                $reportPath = Join-Path $request.output 'report.json'; $r = Read-YLPackageEvidenceJson $reportPath; $r.removal.addPackages = @('com.kano.ylframework.excel@wrong'); Save $reportPath $r
            }
            if ($request.phase -ceq 'Inspect' -and $script:fault -ceq 'joined-report-roots') {
                $reportPath = Join-Path $request.output 'report.json'; $r = Read-YLPackageEvidenceJson $reportPath
                $r.entryPackages = @(($request.entryPackages -join '|')); Save $reportPath $r
            }
            if (($request.phase -ceq 'RemoveExcel' -and $script:fault -cin @('joined-operation-roots', 'joined-operation-remove')) -or
                ($request.phase -ceq 'RestoreExcel' -and $script:fault -ceq 'joined-operation-add')) {
                $reportPath = Join-Path $request.output 'report.json'; $r = Read-YLPackageEvidenceJson $reportPath
                $field = if ($script:fault -ceq 'joined-operation-roots') { 'entryPackages' } elseif ($script:fault -ceq 'joined-operation-remove') { 'removePackages' } else { 'addPackages' }
                $r.removal[$field] = @(($r.removal[$field] -join '|'))
                Save $reportPath $r; Save (Join-Path $request.output 'operation.json') $r.removal
            }
            if ($request.phase -ceq 'VerifyGameplayOnly' -and $script:fault -ceq 'compiled-reference-leak') {
                $reportPath = Join-Path $request.output 'report.json'; $r = Read-YLPackageEvidenceJson $reportPath; $r.compilation.player[0].compiledAssemblyReferences = @('MiniExcel.dll'); Save $reportPath $r
            }
            if ($request.phase -ceq 'Inspect' -and $script:fault -cin @('neutral-receipt-drift', 'input-receipt-drift')) {
                $changedPath = if ($script:fault -ceq 'neutral-receipt-drift') { $request.neutralReceipt } else { Join-Path $request.hostRoot '.yl-inputs.json' }
                $changedReceipt = Read-YLPackageEvidenceJson $changedPath; $changedReceipt.unregistered = 'must not be adopted by the next request'; Save $changedPath $changedReceipt
            }
            if ($script:fault -ceq 'sentinel' -and $request.phase -ceq 'VerifyRemoved') { [IO.File]::AppendAllText((Join-Path $hostRoot 'Assets/RegistryLifecycleSentinels/user-state.bytes'), 'drift') }
            if ($script:fault -ceq 'sentinel-meta' -and $request.phase -ceq 'VerifyExcelOnly') { [IO.File]::AppendAllText((Join-Path $hostRoot 'Assets/RegistryLifecycleSentinels/user-state.bytes.meta'), 'drift') }
            $code = [UnityEditor.EditorApplication]::ExitCodes[0]
            return [pscustomobject]@{ status = $(if ($code -eq 0) { 'Exited' } else { 'NonZeroExit' }); pid = $PID; observedExit = $true; exitCode = $code
                timedOut = $false; cancelled = ($script:fault -ceq 'cancelled'); forcedCleanup = ($script:fault -ceq 'forced-cleanup'); observerError = $null; cleanupError = $null; jobAssigned = $true; activeProcesses = 0; streamsDrained = $true }
        } finally { foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) } }
    }
    $outcome = Invoke-YLRegistryLifecycle @arguments
    Check ($outcome.status -ceq 'Completed' -and $outcome.phases.Count -eq 4) "A complete lifecycle must return four independently observed phase results. $($outcome.error)"
    Check (($outcome.phases.phase -join ',') -ceq 'Inspect,Reopen,Remove,VerifyRemoved') 'Lifecycle phase ordering changed.'
    Check (@($outcome.phases.output | Sort-Object -Unique).Count -eq 4 -and $outcome.processes.Count -eq 4) 'Each lifecycle phase must have its own output and process observation.'
    foreach ($phase in $outcome.phases) {
        $report = Read-YLPackageEvidenceJson $phase.reportPath
        $lock = Read-YLPackageEvidenceJson (Join-Path $phase.output 'packages-lock.json')
        Check ($phase.status -ceq 'Completed' -and $phase.process.observedExit -and $phase.process.exitCode -eq 0 -and
            $report.status -ceq 'Completed' -and $report.phase -ceq $phase.phase -and
            $report.requestSha256 -ceq (Get-FileHash -LiteralPath (Join-Path $phase.output 'request.json') -Algorithm SHA256).Hash.ToLowerInvariant()) 'A completed lifecycle phase must retain its independently read report, request binding and normal exit.'
        if ($phase.phase -cin @('Inspect', 'Reopen')) {
            Check (($phase.graph.packages.name -join ',') -ceq 'com.kano.ylframework,com.kano.ylframework.data-assets,com.kano.ylframework.excel,com.kano.ylframework.gameplay' -and
                ($report.packages.name -join ',') -ceq ($phase.graph.packages.name -join ',')) 'Initial and warm phase receipts must retain the full observed ExcelGameplay closure.'
            Check ($lock.dependencies.Count -eq 4 -and $lock.dependencies['com.kano.ylframework.excel'].depth -eq 0 -and
                $lock.dependencies['com.kano.ylframework'].depth -eq 1 -and $lock.dependencies['com.kano.ylframework.data-assets'].depth -eq 1 -and
                $lock.dependencies['com.kano.ylframework.gameplay'].depth -eq 0) 'Initial and warm lock receipts must retain shortest distances from both roots.'
        } else {
            Check ($phase.graph.packages.Count -eq 0 -and $report.packages.Count -eq 0 -and $lock.dependencies.Count -eq 0) 'Remove and VerifyRemoved receipts must exclude both roots and their indirect fixture packages.'
        }
    }
    Check (@($starts.host | Sort-Object -Unique).Count -eq 1 -and @($starts.cache | Sort-Object -Unique).Count -eq 1) 'Warm reopen and uninstall must preserve their own host/cache.'
    Check ($outcome.coldInstallCount -eq 1 -and $outcome.neutralInputs.files.Count -gt 4 -and $outcome.sentinels.status -ceq 'Unchanged') 'Neutral producer receipts, sentinel hashes or cold/warm classification are missing.'
    $sentinel = @($outcome.sentinels.files | Where-Object { $_.path -ceq 'Assets/RegistryLifecycleSentinels/user-state.bytes' })
    $expectedSentinelHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]](0, 255, 13, 10, 65, 0, 66))).ToLowerInvariant()
    Check ($sentinel.Count -eq 1 -and $sentinel[0].beforeSha256 -ceq $expectedSentinelHash -and $sentinel[0].afterSha256 -ceq $expectedSentinelHash) 'Sentinel summary must record both frozen and actually re-read byte hashes.'
    Check ([IO.File]::Exists((Join-Path $outcome.host.path 'Library/PackageCache/com.kano.ylframework/cached.bytes'))) 'Cached files were incorrectly deleted to force uninstall success.'
    $asmdef = Read-YLPackageEvidenceJson (Join-Path $outcome.host.path 'Assets/RegistryLifecycle/Editor/RegistryLifecycle.asmdef')
    Check ($asmdef.references.Count -eq 0 -and $asmdef.overrideReferences -and $asmdef.precompiledReferences.Count -eq 0 -and ($asmdef.includePlatforms -join ',') -ceq 'Editor') 'The neutral probe inherited product/SDK/Json.NET references or entered Player compilation.'
    foreach ($id in $packages.id) {
        $script:scenarioEntry = $id; $script:scenarioRoots = @($id); $caseArguments = $arguments.Clone(); $caseArguments.EntryPackage = $id; $caseArguments.Remove('EntryPackages')
        $script:legacy = $id -ceq 'com.kano.ylframework.excel'
        $case = Invoke-YLRegistryLifecycle @caseArguments
        Check ($case.status -ceq 'Completed' -and $case.phases.Count -eq 4) "Frozen entry lifecycle failed for ${id}: $($case.error)"
        $script:legacy = $false
    }
    $script:scenarioEntry = $roots[0]; $script:scenarioRoots = $roots
    $baselineHost = $outcome.host.id
    $script:reload = $true; $starts.Clear()
    $reloaded = Invoke-YLRegistryLifecycle @arguments
    Check ($reloaded.status -ceq 'Completed' -and $reloaded.host.id -cne $baselineHost) "A second operation must allocate a new uniquely owned host. $($reloaded.error)"
    Check ($reloaded.phases[2].report.removal.completion -ceq 'ResolvedAfterReload' -and $reloaded.phases[2].report.removal.requestStatusObserved -is [bool] -and
        -not $reloaded.phases[2].report.removal.requestStatusObserved -and $reloaded.phases[2].report.removal.requestStatus -ceq '') 'A lost RemoveRequest must not be fabricated as observed Success.'
    $script:fault = 'reload-unapplied'
    $unapplied = Invoke-YLRegistryLifecycle @arguments
    Check ($unapplied.status -ceq 'Failed' -and $unapplied.error.Length -gt 0) 'Losing the request before UPM applied it must fail, not reissue or claim resolution.'
    $script:fault = ''
    $script:reload = $false
    foreach ($faultName in @('compile', 'request-only', 'remove-failed', 'indirect-remains', 'manifest-extra', 'sentinel', 'cache-environment', 'warm-path-drift', 'lock-depth', 'transitive-lock-depth', 'missing-backend', 'missing-gameplay', 'gameplay-lock-depth', 'direct-dependency', 'report-pid', 'report-cache', 'cancelled', 'forced-cleanup', 'operation-owner-report', 'neutral-receipt-drift', 'input-receipt-drift')) {
        $script:fault = $faultName
        $failed = Invoke-YLRegistryLifecycle @arguments
        Check ($failed.status -ceq 'Failed' -and $failed.error.Length -gt 0) "Lifecycle incorrectly accepted actual $faultName evidence."
        Check ($failed.phases.Count -gt 0 -and $failed.phases[-1].status -ceq 'Failed') "Failed $faultName phase evidence was dropped from the returned outcome."
        if ($faultName -ceq 'sentinel') { Check ($failed.sentinels.status -ceq 'ChangedOrUnavailable') 'A changed sentinel retained a stale Unchanged summary.' }
    }
    $script:fault = ''
    $combinationArguments = $arguments.Clone(); $combinationArguments.VerifyCombinations = $true
    foreach ($origin in @('Registry', 'LocalTarball')) {
        $combinationArguments.Source = if ($origin -ceq 'Registry') { $source } else { Get-YLRegistrySource -PackageSource LocalTarball -Arguments @{ ReleaseManifest = $releasePath } }
        $script:reload = $true
        $combination = Invoke-YLRegistryLifecycle @combinationArguments
        Check ($combination.status -ceq 'Completed') "Explicit $origin combinations failed: $($combination.error)"
        Check (($combination.phases.phase -join ',') -ceq 'Inspect,Reopen,RemoveGameplay,VerifyExcelOnly,RestoreGameplay,RemoveExcel,VerifyGameplayOnly,RestoreExcel,Remove,VerifyRemoved') 'Explicit combinations must execute both root removals/restorations and final removal.'
        $withRuntimeContracts = Copy-Value $combination.phases[0].report
        foreach ($mode in @('editor', 'player')) {
            $withRuntimeContracts.compilation[$mode] += @(@{ name = 'YLFramework.Runtime'; sourceFiles = @(
                'Packages/com.kano.ylframework/Runtime/Utilities/Excel/Attributes/ExcelColumnNameAttribute.cs',
                'Packages/com.kano.ylframework/Runtime/Utilities/Excel/ExcelFieldsType.cs'); assemblyReferences = @(); compiledAssemblyReferences = @() })
        }
        Assert-YLRegistryLifecycleCompilation -Report $withRuntimeContracts -Roots $roots
        $checks++
        foreach ($faultName in @('missing-adapter', 'missing-player-gameplay', 'empty-adapter-source', 'empty-gameplay-source', 'adapter-reference', 'player-source-leak', 'precompiled-type', 'extra-compilation-field')) {
            $badReport = Copy-Value $combination.phases[0].report
            switch ($faultName) {
                'missing-adapter' { $badReport.compilation.editor = @($badReport.compilation.editor | Where-Object name -CNE 'YLFramework.Domain.Excel.Editor') }
                'missing-player-gameplay' { $badReport.compilation.player = @($badReport.compilation.player | Where-Object name -CNE 'YLFramework.Skill') }
                'empty-adapter-source' { @($badReport.compilation.editor | Where-Object name -CEQ 'YLFramework.Domain.Excel.Editor')[0].sourceFiles = @() }
                'empty-gameplay-source' { $badReport.compilation.player[0].sourceFiles = @() }
                'adapter-reference' { @($badReport.compilation.editor | Where-Object name -CEQ 'YLFramework.Domain.Excel.Editor')[0].assemblyReferences = @('YLFramework.Excel.Editor') }
                'player-source-leak' { $badReport.compilation.player[0].sourceFiles += 'Packages/com.kano.ylframework.excel/Editor/Unexpected.cs' }
                'precompiled-type' { $badReport.compilation.editor[0].compiledAssemblyReferences = 'MiniExcel.dll' }
                'extra-compilation-field' { $badReport.compilation.player[0].unobserved = $true }
            }
            Reject { Assert-YLRegistryLifecycleCompilation -Report $badReport -Roots $roots } 'compilation|assembly|adapter|source|reference|field'
        }
        foreach ($phase in $combination.phases) {
            $names = @($phase.graph.packages | ForEach-Object name)
            switch -CaseSensitive ($phase.phase) {
                { $_ -cin @('RemoveGameplay', 'VerifyExcelOnly') } { Check (($names -join ',') -ceq 'com.kano.ylframework,com.kano.ylframework.data-assets,com.kano.ylframework.excel') 'Excel-only lost its actual backend or retained Gameplay.' }
                { $_ -cin @('RemoveExcel', 'VerifyGameplayOnly') } { Check (($names -join ',') -ceq 'com.kano.ylframework,com.kano.ylframework.gameplay') 'Gameplay-only retained Excel/DataAssets or lost main.' }
                { $_ -cin @('Remove', 'VerifyRemoved') } { Check ($names.Count -eq 0) 'Final artifact removal retained a direct/indirect package.' }
                default { Check (($names -join ',') -ceq 'com.kano.ylframework,com.kano.ylframework.data-assets,com.kano.ylframework.excel,com.kano.ylframework.gameplay') 'Restoration did not recover the actual four-package graph.' }
            }
            if ($phase.phase -cin @('RestoreExcel', 'RestoreGameplay')) {
                Check ($phase.report.removal.requestKind -ceq 'AddAndRemove' -and $phase.report.removal.completion -ceq 'ResolvedAfterReload') 'Restore must use a bound actual AddAndRemove, not rewrite or fake request Success after reload.'
                $restored = Read-YLPackageEvidenceJson (Join-Path $phase.output 'manifest.json')
                $original = Read-YLPackageEvidenceJson (Join-Path $combination.output 'manifest.before.json')
                Assert-YLRegistryLifecycleManifest -Actual $restored -Original $original; $checks++
                Check ((Get-FileHash -LiteralPath (Join-Path $phase.output 'manifest.json') -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath (Join-Path $combination.output 'manifest.before.json') -Algorithm SHA256).Hash) 'The restoration fixture must exercise semantic equality after actual UPM-style byte reserialization.'
            }
        }
        if ($origin -ceq 'LocalTarball') {
            $removeExcel = @($combination.phases | Where-Object phase -CEQ 'RemoveExcel')[0]
            $restoreExcel = @($combination.phases | Where-Object phase -CEQ 'RestoreExcel')[0]
            Check (($removeExcel.report.removal.removePackages -join ',') -ceq 'com.kano.ylframework.data-assets,com.kano.ylframework.excel') 'Tarball Excel removal must remove orphan direct DataAssets together, while preserving main.'
            Check ($restoreExcel.report.removal.addPackages.Count -eq 2 -and @($restoreExcel.report.removal.addPackages | Where-Object { $_ -notmatch '^file:.+\.tgz$' }).Count -eq 0) 'Tarball restore must add both exact original artifact locators in one operation.'
        }
        $script:reload = $false
        $combinationFaults = @('fake-restore', 'indirect-remains', 'operation-target-report', 'compiled-reference-leak', 'sentinel-meta', 'joined-report-roots', 'joined-operation-roots')
        if ($origin -ceq 'LocalTarball') { $combinationFaults += @('joined-operation-remove', 'joined-operation-add') }
        if ($origin -ceq 'LocalTarball') { $combinationFaults += 'incomplete-remove' }
        foreach ($faultName in $combinationFaults) {
            $script:fault = $faultName
            $failed = Invoke-YLRegistryLifecycle @combinationArguments
            Check ($failed.status -ceq 'Failed' -and $failed.error.Length -gt 0) "Combination accepted $origin $faultName evidence."
        }
        $script:fault = ''
    }
    $completed = $true
    "PASS: $checks Registry lifecycle NoUnity behavior contracts. NOT Unity compilation, installation or PassedRegistry."
} finally {
    if ($completed -and [IO.Directory]::Exists($root)) { $null = @(Get-YLTestTreeFiles $root); [IO.Directory]::Delete($root, $true) }
    elseif ([IO.Directory]::Exists($root)) { Write-Warning "Failed Registry lifecycle NoUnity fixtures retained: $root" }
}
