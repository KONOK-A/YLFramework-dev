#requires -Version 7.6
[CmdletBinding()]
param([Parameter(Mandatory)][string] $TemporaryRoot)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Common/Workspace.ps1')
if (-not [IO.Directory]::Exists($TemporaryRoot)) { throw 'An existing temporary parent is required.' }
$sandbox = Join-Path (Get-YLTestPath $TemporaryRoot) ('yl-distribution-entry-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($sandbox)
$repository = Join-Path $sandbox 'Repository'; $null = [IO.Directory]::CreateDirectory($repository)
$workspace = Initialize-YLTestWorkspace -WorkspaceRoot (Join-Path $sandbox 'Workspace') -Repository $repository
$hostRecord = New-YLTestDirectory -Workspace $workspace -Kind Host -Key Entry
$result = New-YLTestDirectory -Workspace $workspace -Kind Result -Key Entry
$checks = 0
try {
    $references = @([IO.Directory]::EnumerateFiles((Join-Path $PSHOME 'ref'), '*.dll')) + @([Newtonsoft.Json.Linq.JObject].Assembly.Location)
    $diagnostics = @(Add-Type -Path @((Join-Path $PSScriptRoot '../DistributionEntry.cs'), (Join-Path $PSScriptRoot 'DistributionUnityStubs.cs')) -ReferencedAssemblies $references -CompilerOptions @('/define:UNITY_EDITOR', '/langversion:9.0') -IgnoreWarnings 3>&1)
    foreach ($diagnostic in @($diagnostics | ForEach-Object { $_.ToString() } | Sort-Object -Unique)) { Write-Warning $diagnostic }
    $method = [YLFramework.Testing.DistributionEntry].GetMethod('ValidateInvocation', [Reflection.BindingFlags]'NonPublic,Static')
    if ($null -eq $method) { throw 'DistributionEntry must validate typed v2 ownership before it acquires process exit authority.' }
    [UnityEngine.Application]::dataPath = Join-Path $hostRecord.path 'Assets'
    $null = [IO.Directory]::CreateDirectory([UnityEngine.Application]::dataPath)
    $output = Join-Path $result.path 'Inspect'; $null = [IO.Directory]::CreateDirectory($output)
    $manifest = Join-Path $hostRecord.path 'Packages/manifest.json'; $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($manifest)); [IO.File]::WriteAllText($manifest, '{"dependencies":{}}')
    $valid = @{ schemaVersion = 2; hostId = $hostRecord.id; resultId = $result.id; resultRoot = $result.path; output = $output; unityVersion = '2022.3.62f3'
        phase = 'Inspect'; packageOrigin = 'File'; backend = 'Mono'; smoke = $false; hybridCLR = $false; manifestSha256 = (Get-FileHash -LiteralPath $manifest).Hash; packages = @(@{name = 'com.kano.ylframework'; version = '0.1.0-preview.1'}) }
    function Invoke-Validation([hashtable] $Value) {
        $request = [Newtonsoft.Json.Linq.JObject]::Parse((ConvertTo-Json -InputObject $Value -Depth 20))
        $parameters = [object[]]::new(1); $parameters[0] = $request
        try { $null = $method.Invoke($null, $parameters) } catch { throw $_.Exception.GetBaseException() }
    }
    Invoke-Validation $valid; $checks++
    foreach ($field in @('schemaVersion', 'hostId', 'resultId', 'resultRoot', 'output', 'unityVersion', 'phase', 'packageOrigin', 'backend', 'manifestSha256', 'packages')) {
        $invalid = $valid.Clone(); $invalid[$field] = $true
        $rejected = $false; try { Invoke-Validation $invalid } catch { if ($_.Exception -isnot [InvalidOperationException]) { throw }; $rejected = $true }
        if (-not $rejected) { throw "Boolean $field was accepted as a protocol identity." }; $checks++
    }
    foreach ($field in @('smoke', 'hybridCLR')) {
        $invalid = $valid.Clone(); $invalid[$field] = 'false'
        $rejected = $false; try { Invoke-Validation $invalid } catch { if ($_.Exception -isnot [InvalidOperationException]) { throw }; $rejected = $true }
        if (-not $rejected) { throw "String $field was accepted as a Boolean." }; $checks++
    }
    foreach ($field in @('name', 'version', 'root', 'contentSha256')) {
        $invalid = $valid.Clone(); $invalid.packages = @(@{name = 'com.kano.ylframework'; version = '0.1.0-preview.1'; root = $hostRecord.path; contentSha256 = ('a' * 64) }); $invalid.packages[0][$field] = $true
        $rejected = $false; try { Invoke-Validation $invalid } catch { if ($_.Exception -isnot [InvalidOperationException]) { throw }; $rejected = $true }
        if (-not $rejected) { throw "Boolean package request $field was accepted." }; $checks++
    }
    foreach ($field in @('purpose', 'kind', 'path', 'id', 'workspaceId', 'key', 'schemaVersion')) {
        $ownerPath = Join-Path $hostRecord.path '.yl-owner.json'; $original = [IO.File]::ReadAllText($ownerPath)
        try {
            $invalid = $original | ConvertFrom-Json -AsHashtable; $invalid[$field] = $true
            [IO.File]::WriteAllText($ownerPath, (ConvertTo-Json $invalid))
            $rejected = $false; try { Invoke-Validation $valid } catch { if ($_.Exception -isnot [InvalidOperationException]) { throw }; $rejected = $true }
            if (-not $rejected) { throw "Boolean owner $field was accepted." }; $checks++
        } finally { [IO.File]::WriteAllText($ownerPath, $original) }
    }
    foreach ($change in @(@{ phase = 'Export' }, @{ phase = 'Import' }, @{ schemaVersion = 1 }, @{ packageOrigin = 'GitRemote'; gitRevision = 'main' }, @{ resultId = ('0' * 32) }, @{ output = $repository })) {
        $invalid = $valid.Clone(); foreach ($entry in $change.GetEnumerator()) { $invalid[$entry.Key] = $entry.Value }
        $rejected = $false; try { Invoke-Validation $invalid } catch { if ($_.Exception -isnot [InvalidOperationException]) { throw }; $rejected = $true }
        if (-not $rejected) { throw 'Stale, retired or escaped invocation was accepted.' }; $checks++
    }
    [UnityEngine.Application]::isBatchMode = $false
    $rejected = $false; try { Invoke-Validation $valid } catch { if ($_.Exception -isnot [InvalidOperationException]) { throw }; $rejected = $true }
    if (-not $rejected) { throw 'Interactive invocation acquired exit authority.' }; $checks++
    [UnityEngine.Application]::isBatchMode = $true
    $packageRoot = Join-Path $hostRecord.path 'Staging/com.kano.ylframework'; $null = [IO.Directory]::CreateDirectory($packageRoot)
    [IO.File]::WriteAllText((Join-Path $packageRoot 'package.json'), '{"name":"com.kano.ylframework","version":"0.1.0-preview.1","dependencies":{}}')
    [IO.File]::WriteAllText((Join-Path $packageRoot 'LICENSE.md'), 'MIT License fixture')
    $locator = 'file:' + $packageRoot.Replace('\', '/')
    [IO.File]::WriteAllText($manifest, (ConvertTo-Json -InputObject @{ dependencies = @{ 'com.kano.ylframework' = $locator } }))
    $valid.manifestSha256 = (Get-FileHash -LiteralPath $manifest).Hash
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-YLDistribution.ps1'), [ref]$tokens, [ref]$errors)
    $audit = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Get-DistributionPackageContent' }, $true)
    . ([scriptblock]::Create($audit.Extent.Text))
    $digest = (Get-DistributionPackageContent -Root $packageRoot).sha256
    $valid.packages = @(@{ name = 'com.kano.ylframework'; version = '0.1.0-preview.1'; root = $packageRoot; contentSha256 = $digest })
    $package = [UnityEditor.PackageManager.PackageInfo]::new()
    $package.name = 'com.kano.ylframework'; $package.version = '0.1.0-preview.1'; $package.source = [UnityEditor.PackageManager.PackageSource]::Local
    $package.assetPath = 'Packages/com.kano.ylframework'; $package.resolvedPath = $packageRoot
    $package.packageId = $package.name + '@' + $locator
    [UnityEditor.PackageManager.PackageInfo]::Registered = @($package)
    $capture = [YLFramework.Testing.DistributionEntry].GetMethod('CapturePackages', [Reflection.BindingFlags]'NonPublic,Static')
    function Invoke-Capture {
        $parameters = [object[]]::new(1); $parameters[0] = [Newtonsoft.Json.Linq.JObject]::Parse((ConvertTo-Json -InputObject $valid -Depth 20))
        try { return ,$capture.Invoke($null, $parameters) } catch { throw $_.Exception.GetBaseException() }
    }
    $rows = Invoke-Capture
    if ([string]$rows[0]['contentSha256'] -cne $digest -or [string]$rows[0]['source'] -cne 'Local') { throw 'C# package content evidence differs from the controller identity.' }; $checks++
    foreach ($fault in @('source', 'version', 'assetPath', 'packageId', 'digest', 'missing', 'lfs', 'host-content')) {
        $originalVersion = $package.version; $originalSource = $package.source; $originalAssetPath = $package.assetPath
        try {
            switch ($fault) {
                'source' { $package.source = [UnityEditor.PackageManager.PackageSource]::Registry }
                'version' { $package.version = '1.0.0' }
                'assetPath' { $package.assetPath = 'Assets/Product' }
                'packageId' { $package.packageId = $package.name + '@https://unrelated.invalid/local.git#' + ('b' * 40) }
                'digest' { [IO.File]::AppendAllText((Join-Path $packageRoot 'LICENSE.md'), 'drift') }
                'missing' { [UnityEditor.PackageManager.PackageInfo]::Registered = @() }
                'lfs' { [IO.File]::WriteAllText((Join-Path $packageRoot 'Payload.dll'), 'version https://git-lfs.github.com/spec/v1') }
                'host-content' { $null = [IO.Directory]::CreateDirectory((Join-Path $packageRoot 'Settings')); [IO.File]::WriteAllText((Join-Path $packageRoot 'Settings/Host.json'), '{}') }
            }
            $rejected = $false; try { $null = Invoke-Capture } catch { if ($_.Exception -isnot [InvalidOperationException]) { throw }; $rejected = $true }
            if (-not $rejected) { throw "C# package capture accepted $fault." }; $checks++
        } finally {
            $package.version = $originalVersion; $package.source = $originalSource; $package.assetPath = $originalAssetPath
            $package.packageId = $package.name + '@' + $locator
            [UnityEditor.PackageManager.PackageInfo]::Registered = @($package)
            [IO.File]::WriteAllText((Join-Path $packageRoot 'LICENSE.md'), 'MIT License fixture')
            if ([IO.File]::Exists((Join-Path $packageRoot 'Payload.dll'))) { [IO.File]::Delete((Join-Path $packageRoot 'Payload.dll')) }
            if ([IO.Directory]::Exists((Join-Path $packageRoot 'Settings'))) { [IO.Directory]::Delete((Join-Path $packageRoot 'Settings'), $true) }
        }
    }
    $fileRequest = $valid
    $gitRevision = 'a' * 40
    $gitLocator = 'https://example.invalid/yl.git?path=/PackageSources/com.kano.ylframework#' + $gitRevision
    $valid = $valid.Clone(); $valid.packageOrigin = 'GitRemote'; $valid.gitRevision = $gitRevision; $valid.gitRepository = 'https://example.invalid/yl.git'
    [IO.File]::WriteAllText($manifest, (ConvertTo-Json -InputObject @{ dependencies = @{ 'com.kano.ylframework' = $gitLocator } }))
    $valid.manifestSha256 = (Get-FileHash -LiteralPath $manifest).Hash
    [IO.File]::WriteAllText((Join-Path $hostRecord.path 'Packages/packages-lock.json'),
        (ConvertTo-Json -Depth 10 -InputObject @{ dependencies = @{ 'com.kano.ylframework' = @{ source = 'git'; version = $gitLocator; hash = $gitRevision } } }))
    $package.source = [UnityEditor.PackageManager.PackageSource]::Git; $package.git = $null; $package.packageId = $package.name + '@' + $gitLocator
    $rows = Invoke-Capture
    if ([string]$rows[0]['revision'] -cne $gitRevision -or [string]$rows[0]['packageId'] -cne $package.packageId) { throw 'Cached Git API identity and actual lock revision were not recorded.' }; $checks++
    $package.packageId = $package.name + '@git+file:///unrelated.git#' + $gitRevision
    $rejected = $false; try { $null = Invoke-Capture } catch { if ($_.Exception -isnot [InvalidOperationException]) { throw }; $rejected = $true }
    if (-not $rejected) { throw 'A remote request accepted a local API packageId despite the remote lock.' }; $checks++
    $package.packageId = $package.name + '@' + $gitLocator
    $package.git = [UnityEditor.PackageManager.GitInfo]::new(); $package.git.hash = 'b' * 40; $package.git.revision = $gitRevision
    $rejected = $false; try { $null = Invoke-Capture } catch { if ($_.Exception -isnot [InvalidOperationException]) { throw }; $rejected = $true }
    if (-not $rejected) { throw 'Contradictory nonempty Git API revision was ignored.' }; $checks++
    $package.git = $null; $package.source = [UnityEditor.PackageManager.PackageSource]::Local; $package.packageId = $package.name + '@' + $locator
    $valid = $fileRequest
    [IO.File]::WriteAllText($manifest, (ConvertTo-Json -InputObject @{ dependencies = @{ 'com.kano.ylframework' = $locator } }))
    $writer = [YLFramework.Testing.DistributionEntry].GetMethod('WriteReport', [Reflection.BindingFlags]'NonPublic,Static')
    $parameters = [object[]]::new(2); $parameters[0] = [Newtonsoft.Json.Linq.JObject]::Parse((ConvertTo-Json -InputObject $valid -Depth 20)); $parameters[1] = @{ status = 'Passed' }
    $null = $writer.Invoke($null, $parameters)
    $report = [IO.File]::ReadAllText((Join-Path $output 'report.json')) | ConvertFrom-Json -DateKind String
    if ($report.schemaVersion -ne 2 -or $report.pid -ne $PID -or $report.packageOrigin -cne 'File' -or $report.packages[0].contentSha256 -cne $digest -or $report.resolvedDependencies[0].source -cne 'Local') { throw 'The real C# report writer did not emit the v2 actual package/process evidence.' }; $checks++
    "DistributionEntry NoUnity guards: $checks passed. NOT Unity compilation or execution."
} finally { [IO.Directory]::Delete($sandbox, $true) }
