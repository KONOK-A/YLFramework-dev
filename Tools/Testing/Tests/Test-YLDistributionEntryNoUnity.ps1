#requires -Version 7.6
[CmdletBinding()]
param([Parameter(Mandatory)][string] $TemporaryRoot)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Common/Packages.ps1')
. (Join-Path $PSScriptRoot 'PackageCatalogFixtures.ps1')
if (-not [IO.Directory]::Exists($TemporaryRoot)) { throw 'An existing temporary parent is required.' }
$sandbox = Join-Path (Get-YLTestPath $TemporaryRoot) ('yl-distribution-entry-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($sandbox)
$repository = Join-Path $sandbox 'Repository'; $null = [IO.Directory]::CreateDirectory($repository)
$workspace = Initialize-YLTestWorkspace -WorkspaceRoot (Join-Path $sandbox 'Workspace') -Repository $repository
$hostRecord = New-YLTestDirectory -Workspace $workspace -Kind Host -Key Entry
$result = New-YLTestDirectory -Workspace $workspace -Kind Result -Key Entry
$cache = New-YLTestDirectory -Workspace $workspace -Kind Cache -Key Entry
$checks = 0
try {
    $references = @([IO.Directory]::EnumerateFiles((Join-Path $PSHOME 'ref'), '*.dll')) + @([Newtonsoft.Json.Linq.JObject].Assembly.Location)
    $diagnostics = @(Add-Type -Path @((Join-Path $PSScriptRoot '../DistributionEntry.cs'), (Join-Path $PSScriptRoot '../PackageCatalogContract.cs'), (Join-Path $PSScriptRoot 'DistributionUnityStubs.cs')) -ReferencedAssemblies $references -CompilerOptions @('/define:UNITY_EDITOR', '/langversion:9.0') -IgnoreWarnings 3>&1)
    foreach ($diagnostic in @($diagnostics | ForEach-Object { $_.ToString() } | Sort-Object -Unique)) { Write-Warning $diagnostic }
    $method = [YLFramework.Testing.DistributionEntry].GetMethod('ValidateInvocation', [Reflection.BindingFlags]'NonPublic,Static')
    if ($null -eq $method) { throw 'DistributionEntry must validate typed v2 ownership before it acquires process exit authority.' }
    [UnityEngine.Application]::dataPath = (Join-Path $hostRecord.path 'Assets').Replace('\', '/')
    $null = [IO.Directory]::CreateDirectory([UnityEngine.Application]::dataPath)
    $output = Join-Path $result.path 'Inspect'; $null = [IO.Directory]::CreateDirectory($output)
    $manifest = Join-Path $hostRecord.path 'Packages/manifest.json'; $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($manifest)); [IO.File]::WriteAllText($manifest, '{"dependencies":{}}')
    $valid = @{ schemaVersion = 2; hostId = $hostRecord.id; resultId = $result.id; resultRoot = $result.path; output = $output; unityVersion = '2022.3.62f3'
        phase = 'Inspect'; packageOrigin = 'File'; backend = 'Mono'; smoke = $false; hybridCLR = $false; manifestSha256 = (Get-FileHash -LiteralPath $manifest).Hash; packages = @(@{name = 'com.kano.ylframework'; version = '0.1.0-preview.1'}) }
    $fixtureCatalog = Get-YLFixturePackageCatalog
    $valid.entryPackage = 'com.kano.ylframework'; $valid.inputHash = 'a' * 64
    $valid.catalogSha256 = Set-YLFixtureCatalogInput -HostRecord $hostRecord -Catalog $fixtureCatalog -InputHash $valid.inputHash
    function Invoke-Validation([hashtable] $Value) {
        $request = [Newtonsoft.Json.Linq.JObject]::Parse((ConvertTo-Json -InputObject $Value -Depth 20))
        $parameters = [object[]]::new(1); $parameters[0] = $request
        try { $null = $method.Invoke($null, $parameters) } catch { throw $_.Exception.GetBaseException() }
    }
    Invoke-Validation $valid; $checks++
    foreach ($field in @('catalogSha256', 'inputHash', 'entryPackage')) {
        $invalid = $valid.Clone(); $invalid.Remove($field)
        $rejected = $false; try { Invoke-Validation $invalid } catch { if ($_.Exception -isnot [InvalidOperationException]) { throw }; $rejected = $true }
        if (-not $rejected) { throw "Missing $field was accepted without an owned catalog/entry identity." }; $checks++
    }
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
    function Add-MainPayload([string] $Root) {
        $payload = [ordered]@{
            'Runtime/Demo.cs' = 'namespace YLFramework.NoUnity { public class RuntimeDemo {} }'
            'Runtime/Extra.cs' = 'namespace YLFramework.NoUnity { public class RuntimeExtra {} }'
            'Editor/Demo.cs' = 'namespace YLFramework.NoUnity { public class EditorDemo {} }'
            'Runtime/YLFramework.Runtime.asmdef' = '{"name":"YLFramework.Runtime"}'
            'Editor/YLFramework.Editor.asmdef' = '{"name":"YLFramework.Editor","includePlatforms":["Editor"]}'
            'Runtime/Core/Resources/YLFramework/Core/Canvas.prefab' = 'NoUnity Canvas prefab fixture'
            'Runtime/Core/Resources/YLFramework/Core/EventSystem.prefab' = 'NoUnity EventSystem prefab fixture'
            'Runtime/Systems/Action/Resources/ActionKit/ScreenTransitionCanvas.prefab' = 'NoUnity transition prefab fixture'
        }
        foreach ($relative in $payload.Keys) {
            $path = Join-Path $Root $relative; $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
            [IO.File]::WriteAllText($path, $payload[$relative])
            $guid = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($relative))).Substring(0, 32).ToLowerInvariant()
            [IO.File]::WriteAllText($path + '.meta', "fileFormatVersion: 2`nguid: $guid`n")
            $assetPath = 'Packages/com.kano.ylframework/' + $relative
            [UnityEditor.AssetDatabase]::Guids[$assetPath] = $guid
            if ($relative.EndsWith('.prefab')) { [UnityEditor.AssetDatabase]::Prefabs[$assetPath] = [UnityEngine.GameObject]::new($relative) }
        }
        foreach ($directory in @('Runtime', 'Editor')) {
            $guid = $(if ($directory -ceq 'Runtime') { '1' * 32 } else { '2' * 32 })
            [IO.File]::WriteAllText((Join-Path $Root ($directory + '.meta')), "fileFormatVersion: 2`nguid: $guid`nfolderAsset: yes`n")
            [UnityEditor.AssetDatabase]::Guids['Packages/com.kano.ylframework/' + $directory] = $guid
        }
    }
    Add-MainPayload $packageRoot
    $locator = 'file:' + $packageRoot.Replace('\', '/')
    [IO.File]::WriteAllText($manifest, (ConvertTo-Json -InputObject @{ dependencies = @{ 'com.kano.ylframework' = $locator } }))
    $valid.manifestSha256 = (Get-FileHash -LiteralPath $manifest).Hash
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-YLDistribution.ps1'), [ref]$tokens, [ref]$errors)
    $audit = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Get-DistributionPackageContent' }, $true)
    . ([scriptblock]::Create($audit.Extent.Text))
    $content = Get-DistributionPackageContent -Root $packageRoot; $digest = $content.sha256
    if ($content.files.Count -ne 20) { throw 'The complete main fixture must contain exactly 20 source/meta/resource/manifest/license files.' }
    $valid.packages = @(@{ name = 'com.kano.ylframework'; version = '0.1.0-preview.1'; root = $packageRoot; contentSha256 = $digest; files = $content.files })
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
    $packageRow = [YLFramework.Testing.DistributionEntry].GetMethod('PackageRow', [Reflection.BindingFlags]'NonPublic,Static')
    function Observe-Package([UnityEditor.PackageManager.PackageInfo] $Value) {
        try { return ,$packageRow.Invoke($null, [object[]]@($Value)) } catch { throw $_.Exception.GetBaseException() }
    }
    $ugui = [UnityEditor.PackageManager.PackageInfo]::new(); $ugui.name = 'com.unity.ugui'; $ugui.version = '2.0.0'; $ugui.source = [UnityEditor.PackageManager.PackageSource]::BuiltIn
    $ugui.packageId = $ugui.name + '@2.0.0'; $ugui.assetPath = 'Packages/' + $ugui.name
    $ugui.resolvedPath = Join-Path $hostRecord.path 'Library/PackageCache/com.unity.ugui@c0ef89c047f6'; $null = [IO.Directory]::CreateDirectory($ugui.resolvedPath)
    $uguiJson = '{"name":"com.unity.ugui","version":"2.0.0","dependencies":{},"_fingerprint":"c0ef89c047f65e553b3df5413cb945480f368138"}'
    $uguiManifest = Join-Path $ugui.resolvedPath 'package.json'; [IO.File]::WriteAllText($uguiManifest, $uguiJson)
    $observed = Observe-Package $ugui
    if ([string]$observed['source'] -cne 'BuiltIn' -or [string]$observed['fingerprint'] -cne 'c0ef89c047f65e553b3df5413cb945480f368138') { throw 'Unity 6 built-in cached UGUI fingerprint was not observed without changing source.' }; $checks++
    foreach ($invalid in @($true, $null, 40, ('b' * 39), ('b' * 41), ('x' * 40), (('b' * 40) + "`n"), @('b'), @{ value = 'b' })) {
        $j = $uguiJson | ConvertFrom-Json -AsHashtable; $j._fingerprint = $invalid
        [IO.File]::WriteAllText($uguiManifest, (ConvertTo-Json -Depth 10 -InputObject $j))
        $rejected = $false; try { $null = Observe-Package $ugui } catch { if ($_.Exception -isnot [InvalidOperationException]) { throw }; $rejected = $true }
        if (-not $rejected) { throw 'Malformed official built-in fingerprint was accepted.' }; $checks++
    }
    [IO.File]::WriteAllText($uguiManifest, $uguiJson)
    foreach ($source in @('Local', 'Embedded', 'Unknown')) {
        $ugui.source = [UnityEditor.PackageManager.PackageSource]::$source
        $rejected = $false; try { $null = Observe-Package $ugui } catch { if ($_.Exception -isnot [InvalidOperationException]) { throw }; $rejected = $true }
        if (-not $rejected) { throw 'An official name allowed fingerprint metadata from an unapproved source.' }; $checks++
    }
    $ugui.source = [UnityEditor.PackageManager.PackageSource]::BuiltIn
    foreach ($name in @('com.kano.ylframework', 'com.vendor.ugui', 'com.unityx.ugui')) {
        $ugui.name = $name
        $rejected = $false; try { $null = Observe-Package $ugui } catch { if ($_.Exception -isnot [InvalidOperationException]) { throw }; $rejected = $true }
        if (-not $rejected) { throw 'A non-official BuiltIn package inherited the official fingerprint exception.' }; $checks++
    }
    $ugui.name = 'com.unity.ugui'
    foreach ($fault in @('source', 'version', 'assetPath', 'packageId', 'digest', 'missing', 'lfs', 'host-content', 'fingerprint')) {
        $originalVersion = $package.version; $originalSource = $package.source; $originalAssetPath = $package.assetPath
        $originalJson = [IO.File]::ReadAllText((Join-Path $packageRoot 'package.json'))
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
                'fingerprint' { [IO.File]::WriteAllText((Join-Path $packageRoot 'package.json'), $originalJson.Substring(0, $originalJson.Length - 1) + ',"_fingerprint":"' + ('b' * 40) + '"}') }
            }
            $rejected = $false; try { $null = Invoke-Capture } catch { if ($_.Exception -isnot [InvalidOperationException]) { throw }; $rejected = $true }
            if (-not $rejected) { throw "C# package capture accepted $fault." }; $checks++
        } finally {
            $package.version = $originalVersion; $package.source = $originalSource; $package.assetPath = $originalAssetPath
            $package.packageId = $package.name + '@' + $locator
            [UnityEditor.PackageManager.PackageInfo]::Registered = @($package, $ugui)
            [IO.File]::WriteAllText((Join-Path $packageRoot 'LICENSE.md'), 'MIT License fixture')
            [IO.File]::WriteAllText((Join-Path $packageRoot 'package.json'), $originalJson)
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
    $originalJson = [IO.File]::ReadAllText((Join-Path $packageRoot 'package.json'))
    [IO.File]::WriteAllText((Join-Path $packageRoot 'package.json'), $originalJson.Substring(0, $originalJson.Length - 1) + ',"_fingerprint":"' + ('b' * 40) + '"}')
    $rows = Invoke-Capture
    if ([string]$rows[0]['fingerprint'] -cne ('b' * 40) -or [string]$rows[0]['contentSha256'] -cne $digest) { throw 'A valid Git managed fingerprint lost its observation or changed semantic content.' }; $checks++
    [IO.File]::WriteAllText((Join-Path $packageRoot 'package.json'), $originalJson)
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
    if ($report.resolvedDependencies.Count -ne 2 -or $report.resolvedDependencies[1].source -cne 'BuiltIn' -or
        $report.resolvedDependencies[1].fingerprint -cne 'c0ef89c047f65e553b3df5413cb945480f368138') { throw 'The full report lost actual built-in UGUI fingerprint/source evidence.' }; $checks++
    $failureResult = New-YLTestDirectory -Workspace $workspace -Kind Result -Key ReportFailure
    $failureRequest = $valid.Clone(); $failureRequest.resultId = $failureResult.id; $failureRequest.resultRoot = $failureResult.path
    $failureRequest.phase = 'Build'; $failureRequest.output = Join-Path $failureResult.path 'Build'; $null = [IO.Directory]::CreateDirectory($failureRequest.output)
    $failureRequest.manifestSha256 = (Get-FileHash -LiteralPath $manifest).Hash
    $failurePath = Join-Path $failureRequest.output 'report.json'
    $failureParameters = [object[]]@([Newtonsoft.Json.Linq.JObject]::Parse((ConvertTo-Json -Depth 30 -InputObject $failureRequest)), @{ status = 'Passed' })
    $invalidUguiJson = $uguiJson.Replace('c0ef89c047f65e553b3df5413cb945480f368138', 'invalid')
    [IO.File]::WriteAllText($uguiManifest, $invalidUguiJson)
    $rejected = $false
    try { $null = $writer.Invoke($null, $failureParameters) } catch { if ($_.Exception.GetBaseException() -isnot [InvalidOperationException]) { throw }; $rejected = $true }
    if (-not $rejected -or [IO.File]::Exists($failurePath)) { throw 'Serialization failure must leave no report file, not empty success evidence.' }; $checks++

    function Invoke-EntryRun([string[]] $Arguments) {
        $run = [YLFramework.Testing.DistributionEntry].GetMethod('Run', [Reflection.BindingFlags]'NonPublic,Static')
        if ($null -eq $run) { throw 'The real Execute argument-processing boundary is missing.' }
        $values = [object[]]::new(1); $values[0] = $Arguments
        try { $null = $run.Invoke($null, $values) } catch { throw $_.Exception.GetBaseException() }
    }
    function Reset-Observation {
        [UnityEditor.PackageManager.PackageInfo]::ReadCount = 0
        [UnityEngine.Debug]::LogCalls = 0; [UnityEngine.Debug]::LoggedException = $null
        [UnityEditor.EditorApplication]::ExitCalls = 0; [UnityEditor.EditorApplication]::LastExitCode = -1
    }
    $requestPath = Join-Path $failureRequest.output 'request.json'
    [IO.File]::WriteAllText($requestPath, (ConvertTo-Json -Depth 30 -InputObject $failureRequest))
    Reset-Observation
    try { [YLFramework.Testing.DistributionEntry]::Execute(); throw 'Missing CLI request acquired ownership.' }
    catch { if ($_.Exception.GetBaseException() -isnot [ArgumentException]) { throw } }
    if ([UnityEngine.Debug]::LogCalls -ne 0 -or [UnityEditor.EditorApplication]::ExitCalls -ne 0 -or [IO.File]::Exists($failurePath)) { throw 'Unowned Execute logged, exited or wrote a report.' }; $checks++
    foreach ($fault in @('interactive', 'foreign-owner')) {
        Reset-Observation
        $invalidRequest = $failureRequest.Clone()
        if ($fault -ceq 'interactive') { [UnityEngine.Application]::isBatchMode = $false } else { $invalidRequest.resultId = '0' * 32 }
        [IO.File]::WriteAllText($requestPath, (ConvertTo-Json -Depth 30 -InputObject $invalidRequest))
        try { Invoke-EntryRun @('-ylDistributionRequest', $requestPath); throw 'An unowned request was executed.' }
        catch { if ($_.Exception -isnot [InvalidOperationException]) { throw } }
        finally { [UnityEngine.Application]::isBatchMode = $true }
        if ([UnityEngine.Debug]::LogCalls -ne 0 -or [UnityEditor.EditorApplication]::ExitCalls -ne 0 -or
            [UnityEditor.PackageManager.PackageInfo]::ReadCount -ne 0 -or [IO.File]::Exists($failurePath)) { throw 'An invalid invocation gained reporting/observation/exit authority.' }; $checks++
    }
    [IO.File]::WriteAllText($requestPath, (ConvertTo-Json -Depth 30 -InputObject $failureRequest))
    try {
        [UnityEngine.Debug]::AllowOwnedLog = $true; [UnityEditor.EditorApplication]::AllowOwnedExit = $true
        Reset-Observation
        Invoke-EntryRun @('-ylDistributionRequest', $requestPath)
        $failed = [IO.File]::ReadAllText($failurePath) | ConvertFrom-Json -AsHashtable
        if ($failed.result.status -cne 'Failed' -or $failed.result.error -cnotlike '*Untrusted package-manager fingerprint metadata*' -or
            $failed.hostId -cne $failureRequest.hostId -or $failed.resultId -cne $failureRequest.resultId -or $failed.pid -ne $PID -or
            $failed.observationsUnavailable -isnot [bool] -or -not $failed.observationsUnavailable -or $failed.actualSource -cne 'Unavailable' -or
            $failed.packages.Count -ne 0 -or $failed.resolvedDependencies.Count -ne 0 -or $null -ne $failed.sdk -or
            [UnityEditor.PackageManager.PackageInfo]::ReadCount -ne 3 -or [UnityEditor.EditorApplication]::ExitCalls -ne 1 -or [UnityEditor.EditorApplication]::LastExitCode -ne 1 -or
            [UnityEngine.Debug]::LogCalls -ne 1 -or [UnityEngine.Debug]::LoggedException.Message -cne 'Untrusted package-manager fingerprint metadata.') { throw 'Owned failure lost the original error, re-observed packages or invented successful source evidence.' }; $checks++
        $retained = [IO.File]::ReadAllText($failurePath)
        Reset-Observation
        try { $null = $writer.Invoke($null, $failureParameters); throw 'Existing evidence was overwritten.' }
        catch { if ($_.Exception.GetBaseException() -isnot [IO.IOException]) { throw } }
        if ([IO.File]::ReadAllText($failurePath) -cne $retained -or [UnityEditor.PackageManager.PackageInfo]::ReadCount -ne 0) { throw 'Existing report protection re-observed or replaced evidence.' }; $checks++
        Reset-Observation
        try { Invoke-EntryRun @('-ylDistributionRequest', $requestPath); throw 'A repeated owned invocation reused an existing report.' }
        catch { if ($_.Exception -isnot [InvalidOperationException] -and $_.Exception -isnot [IO.IOException]) { throw } }
        if ([IO.File]::ReadAllText($failurePath) -cne $retained -or [UnityEditor.EditorApplication]::ExitCalls -ne 0 -or [UnityEngine.Debug]::LogCalls -ne 0) { throw 'Repeated invocation acquired authority or changed the previous failure.' }; $checks++
        [IO.File]::Delete($failurePath)
        Reset-Observation; [UnityEditor.PackageManager.PackageInfo]::FailObservation = $true
        Invoke-EntryRun @('-ylDistributionRequest', $requestPath)
        $failed = [IO.File]::ReadAllText($failurePath) | ConvertFrom-Json -AsHashtable
        if ($failed.result.status -cne 'Failed' -or $failed.result.error -cnotlike '*NoUnity PackageInfo observation unavailable*' -or
            -not $failed.observationsUnavailable -or $failed.actualSource -cne 'Unavailable' -or $failed.packages.Count -ne 0 -or $failed.resolvedDependencies.Count -ne 0 -or
            [UnityEditor.PackageManager.PackageInfo]::ReadCount -ne 1 -or [UnityEditor.EditorApplication]::ExitCalls -ne 1 -or [UnityEditor.EditorApplication]::LastExitCode -ne 1) { throw 'An unavailable initial observation was retried or replaced with requested package evidence.' }; $checks++
    } finally {
        [UnityEngine.Debug]::AllowOwnedLog = $false; [UnityEditor.EditorApplication]::AllowOwnedExit = $false; [UnityEditor.PackageManager.PackageInfo]::FailObservation = $false
        [IO.File]::WriteAllText($uguiManifest, $uguiJson)
    }
    function Save([string] $Path, [object] $Value) { [IO.File]::WriteAllText($Path, (ConvertTo-Json -Depth 30 -InputObject $Value), [Text.UTF8Encoding]::new($false)) }
    function Reject([scriptblock] $Action, [string] $Reason) {
        try { & $Action | Out-Null } catch {
            if ($_.Exception -isnot [InvalidOperationException] -and $_.Exception -isnot [Newtonsoft.Json.JsonReaderException]) { throw }
            $script:checks++; return
        }
        throw "DistributionEntry accepted: $Reason"
    }
    function New-PublishedFixture([string] $Origin, [string] $Suffix = 'excel') {
        $script:valid = $fileRequest.Clone(); $script:valid.packageOrigin = $Origin; $script:valid.cacheRoot = $cache.path
        [IO.File]::Delete((Join-Path $script:valid.output 'report.json'))
        $script:valid.entryPackage = if ($Suffix) { 'com.kano.ylframework.' + $Suffix } else { 'com.kano.ylframework' }; $script:valid.releaseManifestSha256 = 'c' * 64
        $fields = Get-YLRegistryRequestFields -Source @{ packageOrigin = $Origin; registryUrl = 'https://package.openupm.com'; registryVersion = '0.1.0-preview.1'; release = @{ sha256 = ('c' * 64) } } -EntryPackage $script:valid.entryPackage
        foreach ($field in $fields.GetEnumerator()) { $script:valid[$field.Key] = $field.Value }
        $script:publishedApis = @(); $expected = @(); $direct = @{}; $locks = @{}
        foreach ($id in @(@('com.kano.ylframework', $script:valid.entryPackage) | Sort-Object -Unique -CaseSensitive)) {
            $deps = @{}; $dependencyJson = '{}'
            if ($id -cne 'com.kano.ylframework') { $deps['com.kano.ylframework'] = '0.1.0-preview.1'; $dependencyJson = '{"com.kano.ylframework":"0.1.0-preview.1"}' }
            if ($Suffix -ceq 'hybridclr' -and $id -ceq $script:valid.entryPackage) { $deps['com.code-philosophy.hybridclr'] = '9.0.3'; $dependencyJson = '{"com.code-philosophy.hybridclr":"9.0.3","com.kano.ylframework":"0.1.0-preview.1"}' }
            $canonical = '{"dependencies":' + $dependencyJson + ',"name":"' + $id + '","version":"0.1.0-preview.1"}'
            $root = Join-Path $hostRecord.path "Library/PackageCache/$Origin-$id"; $null = [IO.Directory]::CreateDirectory($root)
            [IO.File]::WriteAllText((Join-Path $root 'package.json'), $canonical); [IO.File]::WriteAllText((Join-Path $root 'LICENSE.md'), 'fixture license')
            if ($id -ceq 'com.kano.ylframework') { Add-MainPayload $root }
            $inventory = Get-DistributionPackageContent -Root $root; $files = $inventory.files
            $expectedCount = if ($id -ceq 'com.kano.ylframework') { 20 } else { 2 }
            if ($files.Count -ne $expectedCount) { throw "The $Origin fixture inventory is incomplete: $id" }
            $locator = if ($Origin -ceq 'Registry') { '0.1.0-preview.1' } else { 'file:' + (Join-Path $sandbox ($id + '.tgz')).Replace('\', '/') }
            if ($Origin -ceq 'LocalTarball') { [IO.File]::WriteAllText($locator.Substring(5), 'locator fixture, not a verified tarball') }
            $expected += @{ name = $id; version = '0.1.0-preview.1'; root = $null; locator = $locator; dependencies = $deps; files = $files
                contentSha256 = $inventory.sha256 }
            $api = [UnityEditor.PackageManager.PackageInfo]::new(); $api.name = $id; $api.version = '0.1.0-preview.1'; $api.assetPath = 'Packages/' + $id; $api.resolvedPath = $root
            $api.source = [UnityEditor.PackageManager.PackageSource]::$Origin; $api.packageId = $id + '@' + $locator
            $api.isDirectDependency = $Origin -ceq 'LocalTarball' -or $id -ceq $script:valid.entryPackage
            if ($Origin -ceq 'Registry') { $api.registry = [UnityEditor.PackageManager.RegistryInfo]::new(); $api.registry.url = $script:valid.registryUrl }
            $api.dependencies = @($deps.Keys | ForEach-Object { $d = [UnityEditor.PackageManager.DependencyInfo]::new(); $d.name = $_; $d.version = $deps[$_]; $d })
            $script:publishedApis += $api
            if ($api.isDirectDependency) { $direct[$id] = $locator }
            $locks[$id] = @{ source = $(if ($Origin -ceq 'Registry') { 'registry' } else { 'local-tarball' }); version = $locator
                depth = $(if ($api.isDirectDependency) { 0 } else { 1 }); dependencies = $deps }
            if ($Origin -ceq 'Registry') { $locks[$id].url = $script:valid.registryUrl }
        }
        $script:valid.packages = $expected
        Save $manifest @{ dependencies = $direct }; Save (Join-Path $hostRecord.path 'Packages/packages-lock.json') @{ dependencies = $locks }
        $script:valid.manifestSha256 = (Get-FileHash -LiteralPath $manifest).Hash
        [UnityEditor.PackageManager.PackageInfo]::Registered = $script:publishedApis
    }
    foreach ($origin in @('Registry', 'LocalTarball')) {
        New-PublishedFixture $origin
        Invoke-Validation $valid
        $rows = Invoke-Capture
        if ($rows.Count -ne 2 -or [bool]$rows[0]['isDirectDependency'] -ne ($origin -ceq 'LocalTarball') -or
            [string]$rows[0]['contentSha256'] -cne $valid.packages[0].contentSha256 -or $rows[0]['dependencies'] -isnot [Newtonsoft.Json.Linq.JObject] -or
            [int]$rows[0]['verifiedFiles'] -ne 20 -or [int]$rows[1]['verifiedFiles'] -ne 2) { throw 'Published package capture omitted source/directness/content evidence or the complete fixture inventory.' }; $checks++
        if ($origin -ceq 'LocalTarball') {
            $publishedApis[0].packageId = $publishedApis[0].packageId.Replace('/', '\')
            $null = Invoke-Capture; $checks++
        }
        foreach ($fault in @('source', 'version', 'url', 'package-id', 'direct', 'manifest-direct', 'official-direct', 'sdk-direct', 'lock-source', 'lock-version', 'lock-url', 'lock-depth', 'lock-dependencies', 'api-dependencies', 'expected-dependencies', 'expected-content', 'expected-files', 'outside-root', 'source-copy', 'fingerprint', 'unknown-managed', 'trailing-json', 'duplicate-json', 'comment-json', 'trailing-comma', 'single-quote-json', 'sdk-payload')) {
            New-PublishedFixture $origin
            $api = $publishedApis[0]; $path = Join-Path $api.resolvedPath 'package.json'; $lockPath = Join-Path $hostRecord.path 'Packages/packages-lock.json'
            switch ($fault) {
                'source' { $api.source = [UnityEditor.PackageManager.PackageSource]::Git }
                'version' { $api.version = '0.1.0-preview.2' }
                'url' { if ($origin -ceq 'Registry') { $api.registry.url += '/wrong' } else { $api.packageId += '.wrong' } }
                'package-id' { $api.packageId = $api.name + '@file:C:/outside' }
                'direct' { $api.isDirectDependency = -not $api.isDirectDependency }
                'manifest-direct' { $m = [IO.File]::ReadAllText($manifest) | ConvertFrom-Json -AsHashtable; if ($origin -ceq 'Registry') { $m.dependencies[$api.name] = $api.version } else { $m.dependencies.Remove($api.name) }; Save $manifest $m }
                'official-direct' { $m = [IO.File]::ReadAllText($manifest) | ConvertFrom-Json -AsHashtable; $m.dependencies['com.unity.ugui'] = '1.0.0'; Save $manifest $m }
                'sdk-direct' { $m = [IO.File]::ReadAllText($manifest) | ConvertFrom-Json -AsHashtable; $m.dependencies['com.code-philosophy.hybridclr'] = '9.0.3'; Save $manifest $m }
                'api-dependencies' { $publishedApis[1].dependencies = @() }
                'expected-dependencies' { $valid.packages[1].dependencies = @{} }
                'expected-content' { $valid.packages[0].contentSha256 = 'd' * 64 }
                'expected-files' { $valid.packages[0].files[0].sha256 = 'd' * 64 }
                'outside-root' { $api.resolvedPath = $repository }
                'source-copy' { $null = [IO.Directory]::CreateDirectory((Join-Path $hostRecord.path 'PackageSources')) }
                'fingerprint' { $j = [IO.File]::ReadAllText($path) | ConvertFrom-Json -AsHashtable; $j._fingerprint = 'x' * 40; Save $path $j }
                'unknown-managed' { $j = [IO.File]::ReadAllText($path) | ConvertFrom-Json -AsHashtable; $j._other = 'x'; Save $path $j }
                'trailing-json' { [IO.File]::AppendAllText($path, '{}') }
                'duplicate-json' { [IO.File]::WriteAllText($path, '{"name":"x","name":"y"}') }
                'comment-json' { [IO.File]::WriteAllText($path, '/* not JSON */' + [IO.File]::ReadAllText($path)) }
                'trailing-comma' { $text = [IO.File]::ReadAllText($path); [IO.File]::WriteAllText($path, $text.Substring(0, $text.Length - 1) + ',}') }
                'single-quote-json' { [IO.File]::WriteAllText($path, [IO.File]::ReadAllText($path).Replace('"', "'")) }
                'sdk-payload' { [IO.File]::WriteAllText((Join-Path $publishedApis[1].resolvedPath 'DOTween.dll'), 'MZsdk') }
                default {
                    $l = [IO.File]::ReadAllText($lockPath) | ConvertFrom-Json -AsHashtable; $entry = $l.dependencies[$api.name]
                    switch ($fault) { 'lock-source' { $entry.source = 'local' } 'lock-version' { $entry.version = '0.1.0-preview.2' } 'lock-url' { if ($origin -ceq 'Registry') { $entry.url += '/wrong' } else { $entry.source = 'local' } } 'lock-depth' { $entry.depth = 7 } 'lock-dependencies' { $l.dependencies[$valid.entryPackage].dependencies = @{} } }
                    Save $lockPath $l
                }
            }
            Reject { Invoke-Capture } "$origin $fault"
            if ([IO.Directory]::Exists((Join-Path $hostRecord.path 'PackageSources'))) { [IO.Directory]::Delete((Join-Path $hostRecord.path 'PackageSources'), $true) }
            if ([IO.File]::Exists((Join-Path $publishedApis[1].resolvedPath 'DOTween.dll'))) { [IO.File]::Delete((Join-Path $publishedApis[1].resolvedPath 'DOTween.dll')) }
        }
        New-PublishedFixture $origin
        foreach ($field in @('entryPackage', 'releaseManifestSha256', 'registryUrl', 'registryVersion', 'cacheRoot')) {
            $r = $valid.Clone(); $r[$field] = $true; Reject { Invoke-Validation $r } "$origin typed $field"
        }
        $r = $valid.Clone(); $r.phase = 'HybridConfigure'; $r.hybridCLR = $true; $r.smoke = $true; $r.backend = 'IL2CPP'
        Reject { Invoke-Validation $r } "$origin must not enter the unrelated native HybridCLR installation workflow"
        $fingerprintLength = if ($origin -ceq 'LocalTarball') { 32 } else { 40 }
        $path = Join-Path $publishedApis[0].resolvedPath 'package.json'; $j = [IO.File]::ReadAllText($path) | ConvertFrom-Json -AsHashtable; $j._fingerprint = 'b' * $fingerprintLength; Save $path $j
        $rows = Invoke-Capture
        if ([string]$rows[0]['fingerprint'] -cne ('b' * $fingerprintLength) -or [string]$rows[0]['contentSha256'] -cne $valid.packages[0].contentSha256) { throw 'Managed fingerprint was not observed independently of semantic content.' }; $checks++
        $j._fingerprint = 'b' * $(if ($origin -ceq 'LocalTarball') { 40 } else { 32 }); Save $path $j
        Reject { Invoke-Capture } "$origin must not inherit the other source fingerprint format"
        $j._fingerprint = 'b' * $fingerprintLength; Save $path $j
        $reportPath = Join-Path $output 'report.json'; [IO.File]::Delete($reportPath)
        $oldEnvironment = @{}
        try {
            foreach ($name in @('UPM_CACHE_ROOT', 'UPM_CACHE_PATH', 'UPM_NPM_CACHE_PATH')) { $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name); [Environment]::SetEnvironmentVariable($name, (Join-Path $cache.path $name)) }
            $parameters[0] = [Newtonsoft.Json.Linq.JObject]::Parse((ConvertTo-Json -Depth 30 -InputObject $valid)); $null = $writer.Invoke($null, $parameters)
        } finally { foreach ($name in $oldEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $oldEnvironment[$name]) } }
        $report = [IO.File]::ReadAllText($reportPath) | ConvertFrom-Json -AsHashtable
        $row = $report.resolvedDependencies[0]
        if ($row.isDirectDependency -isnot [bool] -or $row.dependencies -isnot [Collections.IDictionary] -or $row.packageId -cne $publishedApis[0].packageId -or $row.resolvedPath -cne $publishedApis[0].resolvedPath -or
            $report.releaseManifestSha256 -cne $valid.releaseManifestSha256 -or $report.entryPackage -cne $valid.entryPackage) { throw 'The report lost published provenance or the actual full PackageInfo graph.' }; $checks++
        foreach ($name in $oldEnvironment.Keys) { if ($report.cacheEnvironment[$name] -cne (Join-Path $cache.path $name)) { throw "The report copied request data rather than actual process environment: $name" }; $checks++ }
    }
    New-PublishedFixture Registry dotween
    $dll = Join-Path $publishedApis[1].resolvedPath 'Unapproved.dll'; [IO.File]::WriteAllText($dll, 'MZrenamed SDK fixture')
    $valid.packages[1].files += @{ path = 'Unapproved.dll'; sha256 = (Get-FileHash -LiteralPath $dll).Hash.ToLowerInvariant() }
    $ordinalFiles = @($valid.packages[1].files[0], $valid.packages[1].files[2], $valid.packages[1].files[1])
    $lines = ($ordinalFiles | ForEach-Object { $_.path + "`0" + $_.sha256 + "`n" }) -join ''
    $valid.packages[1].contentSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($lines))).ToLowerInvariant()
    Reject { Invoke-Capture } 'A renamed SDK binary in an adapter is forbidden even with an internally consistent expected inventory'
    [IO.File]::Delete($dll)
    foreach ($origin in @('Registry', 'LocalTarball')) {
        New-PublishedFixture $origin hybridclr
        $valid.hybridCLR = $true
        $sdkHash = '6cadd24d101f4cbd00cd80190106f6621132d24d'
        $valid.sdk = @{ source = 'Registry'; version = '9.0.3'; registryUrl = 'https://package.openupm.com'; repositoryRevision = $sdkHash }
        $sdk = [UnityEditor.PackageManager.PackageInfo]::new(); $sdk.name = 'com.code-philosophy.hybridclr'; $sdk.version = '9.0.3'; $sdk.source = [UnityEditor.PackageManager.PackageSource]::Registry
        $sdk.registry = [UnityEditor.PackageManager.RegistryInfo]::new(); $sdk.registry.url = 'https://package.openupm.com'; $sdk.packageId = $sdk.name + '@9.0.3'; $sdk.assetPath = 'Packages/' + $sdk.name
        $sdk.resolvedPath = Join-Path $cache.path 'upm/hybridclr'; $null = [IO.Directory]::CreateDirectory($sdk.resolvedPath)
        Save (Join-Path $sdk.resolvedPath 'package.json') @{ name = $sdk.name; version = $sdk.version }
        $sdk.git = [UnityEditor.PackageManager.GitInfo]::new(); $sdk.git.hash = 'd' * 40
        $lockPath = Join-Path $hostRecord.path 'Packages/packages-lock.json'; $l = [IO.File]::ReadAllText($lockPath) | ConvertFrom-Json -AsHashtable
        $l.dependencies[$sdk.name] = @{ source = 'registry'; version = '9.0.3'; url = $sdk.registry.url; depth = 1; dependencies = @{} }; Save $lockPath $l
        [UnityEditor.PackageManager.PackageInfo]::Registered = @($publishedApis + $sdk)
        Invoke-Validation $valid; $null = Invoke-Capture
        $baselineLock = [IO.File]::ReadAllText($lockPath)
        foreach ($fault in @('sdk-source', 'sdk-version', 'sdk-url', 'sdk-id', 'sdk-direct', 'sdk-lock-source', 'sdk-lock-url', 'sdk-lock-version', 'sdk-lock-depth', 'sdk-lock-deps', 'sdk-manifest-direct', 'sdk-request-revision')) {
            try {
                switch ($fault) {
                    'sdk-source' { $sdk.source = [UnityEditor.PackageManager.PackageSource]::Git }
                    'sdk-version' { $sdk.version = '9.0.4' }
                    'sdk-url' { $sdk.registry.url = 'https://wrong.invalid' }
                    'sdk-id' { $sdk.packageId += '.wrong' }
                    'sdk-direct' { $sdk.isDirectDependency = $true }
                    'sdk-manifest-direct' { $m = [IO.File]::ReadAllText($manifest) | ConvertFrom-Json -AsHashtable; $m.dependencies[$sdk.name] = '9.0.3'; Save $manifest $m }
                    'sdk-request-revision' { $valid.sdk.repositoryRevision = 'b' * 40 }
                    default {
                        $l = $baselineLock | ConvertFrom-Json -AsHashtable; $entry = $l.dependencies[$sdk.name]
                        switch ($fault) { 'sdk-lock-source' { $entry.source = 'git' } 'sdk-lock-url' { $entry.url = 'https://wrong.invalid' } 'sdk-lock-version' { $entry.version = '9.0.4' } 'sdk-lock-depth' { $entry.depth = 0 } 'sdk-lock-deps' { $entry.dependencies['com.fake.sdk'] = '1.0.0' } }
                        Save $lockPath $l
                    }
                }
                Reject { Invoke-Capture } "$origin $fault"
            } finally {
                $sdk.source = [UnityEditor.PackageManager.PackageSource]::Registry; $sdk.version = '9.0.3'; $sdk.registry.url = 'https://package.openupm.com'; $sdk.packageId = $sdk.name + '@9.0.3'; $sdk.isDirectDependency = $false; $valid.sdk.repositoryRevision = $sdkHash
                [IO.File]::WriteAllText($lockPath, $baselineLock)
                $m = [IO.File]::ReadAllText($manifest) | ConvertFrom-Json -AsHashtable; $m.dependencies.Remove($sdk.name); Save $manifest $m
            }
        }
        $reportPath = Join-Path $output 'report.json'; [IO.File]::Delete($reportPath)
        $parameters[0] = [Newtonsoft.Json.Linq.JObject]::Parse((ConvertTo-Json -Depth 30 -InputObject $valid)); $null = $writer.Invoke($null, $parameters)
        $report = [IO.File]::ReadAllText($reportPath) | ConvertFrom-Json -AsHashtable
        if ($report.sdk.source -cne 'Registry' -or $report.sdk.repositoryRevision -cne $sdkHash -or $null -ne $report.sdk.revision -or
            $report.sdk.isDirectDependency -or $report.resolvedDependencies.Count -ne 3) { throw 'Distribution SDK evidence falsely claimed a Git checkout or lost Registry fields.' }; $checks++
    }
    $scriptAssemblies = Join-Path $hostRecord.path 'Library/ScriptAssemblies'; $null = [IO.Directory]::CreateDirectory($scriptAssemblies)
    $fixtureBuild = Join-Path $sandbox 'CompiledFixtures'; $null = [IO.Directory]::CreateDirectory($fixtureBuild)
    $runtimeDll = Join-Path $scriptAssemblies 'YLFramework.Runtime.dll'; $editorDll = Join-Path $scriptAssemblies 'YLFramework.Editor.dll'
    $editorBackup = Join-Path $fixtureBuild 'YLFramework.Editor.dll'
    $emptyDir = Join-Path $fixtureBuild 'Empty'; $null = [IO.Directory]::CreateDirectory($emptyDir)
    $emptyEditorDll = Join-Path $emptyDir 'YLFramework.Editor.dll'
    $fixtureReferences = [Microsoft.CodeAnalysis.MetadataReference[]]@($references | ForEach-Object { [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($_) })
    function Compile-Fixture([string] $Name, [string[]] $Sources, [string] $Path) {
        $options = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default.WithLanguageVersion([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::CSharp9)
        $trees = [Microsoft.CodeAnalysis.SyntaxTree[]]@($Sources | ForEach-Object { [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($_, $options) })
        $compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create($Name, $trees, $fixtureReferences,
            [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary))
        $stream = [IO.File]::Create($Path)
        try { $emitted = $compilation.Emit($stream); if (-not $emitted.Success) { throw ($emitted.Diagnostics -join "`n") } }
        finally { $stream.Dispose() }
    }
    Compile-Fixture 'YLFramework.Runtime' @([IO.File]::ReadAllText((Join-Path $packageRoot 'Runtime/Demo.cs')), [IO.File]::ReadAllText((Join-Path $packageRoot 'Runtime/Extra.cs'))) $runtimeDll
    Compile-Fixture 'YLFramework.Editor' @([IO.File]::ReadAllText((Join-Path $packageRoot 'Editor/Demo.cs'))) $editorBackup
    Compile-Fixture 'YLFramework.Editor' @("#if NEVER_ENABLED_NO_UNITY_FIXTURE`npublic class ExcludedEditorFixture {}`n#endif") $emptyEditorDll
    $runtimeElsewhere = Join-Path $fixtureBuild 'YLFramework.Runtime.dll'; [IO.File]::Copy($runtimeDll, $runtimeElsewhere)
    foreach ($retainType in @($false, $true)) {
        [IO.File]::Copy($editorBackup, $editorDll, $true)
        [YLFramework.Testing.NoUnity.FixtureAssemblies]::Load(@($runtimeDll, $editorDll))
        [YLFramework.Testing.NoUnity.FixtureAssemblies]::Retain($retainType)
        try {
            $unloaded = [YLFramework.Testing.NoUnity.FixtureAssemblies]::Unload()
            [GC]::Collect(); [GC]::WaitForPendingFinalizers(); [GC]::Collect()
            $blocked = $false
            try { [IO.File]::Copy($emptyEditorDll, $editorDll, $true) }
            catch { if ($_.Exception.GetBaseException() -isnot [IO.IOException]) { throw }; $blocked = $true }
            if (-not $blocked) { throw "A retained fixture reference (Type=$retainType) did not keep its DLL locked." }; $checks++
            Write-Verbose "Retained fixture reference (Type=$retainType) prevents DLL replacement after Unload and GC." -Verbose
            if ($unloaded -isnot [WeakReference]) { throw 'Fixture Unload must return weak evidence, not imply immediate DLL release.' }
            if ([YLFramework.Testing.NoUnity.FixtureAssemblies]::WaitForUnload($unloaded)) { throw 'A retained fixture reference was incorrectly reported as unloaded.' }; $checks++
        } finally { [YLFramework.Testing.NoUnity.FixtureAssemblies]::Release() }
        if (-not [YLFramework.Testing.NoUnity.FixtureAssemblies]::WaitForUnload($unloaded)) { throw 'Released fixture references still prevent context unload.' }; $checks++
        [IO.File]::Copy($emptyEditorDll, $editorDll, $true); $checks++
    }
    function Set-LoadedFixtures([string] $Mode = 'Both') {
        $unloaded = [YLFramework.Testing.NoUnity.FixtureAssemblies]::Unload()
        if (-not [YLFramework.Testing.NoUnity.FixtureAssemblies]::WaitForUnload($unloaded)) { throw 'The previous NoUnity fixture context is still alive; its DLLs cannot be replaced.' }
        [IO.File]::Copy($(if ($Mode -ceq 'EmptyEditor') { $emptyEditorDll } else { $editorBackup }), $editorDll, $true)
        [string[]]$dlls = switch ($Mode) {
            'RuntimeOnly' { @($runtimeDll) }
            'EditorOnly' { @($editorDll) }
            'RuntimeElsewhere' { @($runtimeElsewhere, $editorDll) }
            'EditorElsewhere' { @($runtimeDll, $editorBackup) }
            default { @($runtimeDll, $editorDll) }
        }
        [YLFramework.Testing.NoUnity.FixtureAssemblies]::Load($dlls)
    }
    Set-LoadedFixtures
    $fixtureNames = @([AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetName().Name } | Where-Object { $_ -cin @('YLFramework.Runtime', 'YLFramework.Editor') })
    if ($fixtureNames.Count -ne 2) { throw 'The NoUnity fixture must really load the two named .NET DLLs, not assume Unity compilation.' }
    function Set-InspectGraph([UnityEditor.PackageManager.PackageInfo] $Main) {
        $script:runtimeEditor = [UnityEditor.Compilation.Assembly]::new(); $script:runtimeEditor.name = 'YLFramework.Runtime'; $script:runtimeEditor.outputPath = $runtimeDll
        $script:runtimeEditor.sourceFiles = @('Packages/com.kano.ylframework/Runtime/Demo.cs', 'Packages/com.kano.ylframework/Runtime/Extra.cs')
        $script:mainEditor = [UnityEditor.Compilation.Assembly]::new(); $script:mainEditor.name = 'YLFramework.Editor'; $script:mainEditor.outputPath = $editorDll
        $script:mainEditor.sourceFiles = @('Packages/com.kano.ylframework/Editor/Demo.cs')
        $script:runtimePlayer = [UnityEditor.Compilation.Assembly]::new(); $script:runtimePlayer.name = 'YLFramework.Runtime'
        $script:runtimePlayer.sourceFiles = @((Join-Path $Main.resolvedPath 'Runtime/Demo.cs'), (Join-Path $Main.resolvedPath 'Runtime/Extra.cs'))
        [UnityEditor.Compilation.CompilationPipeline]::Editor = @($script:runtimeEditor, $script:mainEditor)
        [UnityEditor.Compilation.CompilationPipeline]::Player = @($script:runtimePlayer)
    }
    function Invoke-InspectCase([string] $ExpectedStatus, [string] $Label) {
        $caseResult = New-YLTestDirectory -Workspace $workspace -Kind Result -Key Graph
        $r = $valid.Clone(); $r.resultId = $caseResult.id; $r.resultRoot = $caseResult.path; $r.output = Join-Path $caseResult.path 'Inspect'; $r.phase = 'Inspect'
        $null = [IO.Directory]::CreateDirectory($r.output); $path = Join-Path $r.output 'request.json'; Save $path $r
        Reset-Observation
        try {
            [UnityEngine.Debug]::AllowOwnedLog = $true; [UnityEditor.EditorApplication]::AllowOwnedExit = $true
            Invoke-EntryRun @('-ylDistributionRequest', $path)
        } finally { [UnityEngine.Debug]::AllowOwnedLog = $false; [UnityEditor.EditorApplication]::AllowOwnedExit = $false }
        $observed = [IO.File]::ReadAllText((Join-Path $r.output 'report.json')) | ConvertFrom-Json -AsHashtable
        $expectedExit = if ($ExpectedStatus -ceq 'Passed') { 0 } else { 1 }
        if ($observed.result.status -cne $ExpectedStatus -or [UnityEditor.EditorApplication]::ExitCalls -ne 1 -or [UnityEditor.EditorApplication]::LastExitCode -ne $expectedExit) {
            throw "$Label expected $ExpectedStatus, actual=$($observed.result.status), error=$($observed.result['error'])"
        }
        if ($ExpectedStatus -ceq 'Passed') {
            $runtimeGraphs = @($observed.result.graph | Where-Object name -CEQ 'YLFramework.Runtime')
            $editorGraphs = @($observed.result.graph | Where-Object name -CEQ 'YLFramework.Editor')
            if ($runtimeGraphs.Count -ne 2 -or $runtimeGraphs[0].sourceFiles.Count -ne 2 -or $runtimeGraphs[1].sourceFiles.Count -ne 2 -or
                $editorGraphs.Count -ne 1 -or $editorGraphs[0].sourceFiles.Count -ne 1 -or $observed.result.prefabCount -ne 3 -or $observed.result.missingScripts -ne 0) { throw 'A passing Inspect lost the full 2 Runtime / 1 Editor sources or the three original resources.' }
        } elseif ([string]::IsNullOrWhiteSpace($observed.result.error) -or -not $observed.observationsUnavailable -or $observed.packages.Count -ne 0) { throw 'Graph failures must preserve the original honest failure report.' }
        $script:checks++
        return $observed
    }
    $graphFailures = [Collections.Generic.List[string]]::new(); $passedGraphs = [Collections.Generic.List[object]]::new()
    foreach ($origin in @('File', 'GitFixture', 'GitRemote', 'Registry', 'LocalTarball')) {
        if ($origin -cin @('Registry', 'LocalTarball')) {
            New-PublishedFixture $origin
            Set-LoadedFixtures; Set-InspectGraph $publishedApis[0]
            $null = Invoke-InspectCase Failed "$origin cannot accept an installed leaf without its declared assembly payload"
            New-PublishedFixture $origin ''
            $mainApi = $publishedApis[0]
        }
        else {
            $script:valid = $fileRequest.Clone(); $script:valid.packageOrigin = $origin
            $declaration = $locator
            $package.source = [UnityEditor.PackageManager.PackageSource]::Local; $package.packageId = $package.name + '@' + $declaration
            if ($origin -cne 'File') {
                $script:valid.gitRevision = $gitRevision; $script:valid.gitRepository = $(if ($origin -ceq 'GitFixture') { 'git+file:///owned-fixture.git' } else { 'https://example.invalid/yl.git' })
                $declaration = $script:valid.gitRepository + '?path=/PackageSources/com.kano.ylframework#' + $gitRevision
                $package.source = [UnityEditor.PackageManager.PackageSource]::Git; $package.packageId = $package.name + '@' + $declaration
                Save (Join-Path $hostRecord.path 'Packages/packages-lock.json') @{ dependencies = @{ 'com.kano.ylframework' = @{ source = 'git'; version = $declaration; hash = $gitRevision } } }
            }
            Save $manifest @{ dependencies = @{ 'com.kano.ylframework' = $declaration } }; $script:valid.manifestSha256 = (Get-FileHash -LiteralPath $manifest).Hash
            [UnityEditor.PackageManager.PackageInfo]::Registered = @($package, $ugui); $mainApi = $package
        }
        Set-LoadedFixtures; Set-InspectGraph $mainApi
        $passedGraphs.Add((Invoke-InspectCase Passed "$origin complete formal graph"))
        foreach ($fault in @('player-runtime-empty', 'player-runtime-lost-source', 'editor-runtime-lost-source', 'editor-missing', 'editor-empty', 'runtime-moved-to-editor-only-asmdef', 'frozen-and-compiled-drop-runtime-source', 'editor-in-player', 'editor-source-in-other-player-asmdef', 'duplicate-runtime-source', 'runtime-not-loaded', 'editor-not-loaded', 'editor-condition-excludes-all-types', 'runtime-loaded-from-wrong-file', 'editor-loaded-from-wrong-file', 'missing-output', 'wrong-output', 'outside-source', 'missing-physical-source', 'test-assembly', 'runtime-test-reference', 'guid-drift', 'prefab-missing-script', 'missing-Canvas', 'missing-EventSystem', 'missing-ScreenTransitionCanvas')) {
            Set-InspectGraph $mainApi
            $mode = switch ($fault) { 'runtime-not-loaded' { 'EditorOnly' } 'editor-not-loaded' { 'RuntimeOnly' } 'editor-condition-excludes-all-types' { 'EmptyEditor' } 'runtime-loaded-from-wrong-file' { 'RuntimeElsewhere' } 'editor-loaded-from-wrong-file' { 'EditorElsewhere' } default { 'Both' } }
            Set-LoadedFixtures $mode
            $guidAsset = 'Packages/com.kano.ylframework/Editor/Demo.cs'; $originalGuid = [UnityEditor.AssetDatabase]::Guids[$guidAsset]
            $prefabAsset = 'Packages/com.kano.ylframework/Runtime/Core/Resources/YLFramework/Core/Canvas.prefab'
            $originalExpected = $valid.packages; $missingPrefab = $null; $prefabBytes = $null
            switch ($fault) {
                'player-runtime-empty' { $runtimePlayer.sourceFiles = @() }
                'player-runtime-lost-source' { $runtimePlayer.sourceFiles = @($runtimePlayer.sourceFiles[0]) }
                'editor-runtime-lost-source' { $runtimeEditor.sourceFiles = @($runtimeEditor.sourceFiles[0]) }
                'editor-missing' { [UnityEditor.Compilation.CompilationPipeline]::Editor = @($runtimeEditor) }
                'editor-empty' { $mainEditor.sourceFiles = @() }
                'runtime-moved-to-editor-only-asmdef' {
                    $runtimeEditor.sourceFiles = @($runtimeEditor.sourceFiles[0]); $runtimePlayer.sourceFiles = @($runtimePlayer.sourceFiles[0])
                    $split = [UnityEditor.Compilation.Assembly]::new(); $split.name = 'RuntimeChild.EditorOnly'; $split.sourceFiles = @('Packages/com.kano.ylframework/Runtime/Extra.cs')
                    [UnityEditor.Compilation.CompilationPipeline]::Editor = @($runtimeEditor, $mainEditor, $split)
                }
                'frozen-and-compiled-drop-runtime-source' {
                    $partial = $valid.packages[0].Clone(); $partial.files = @($partial.files | Where-Object path -CNE 'Runtime/Extra.cs')
                    $valid.packages = @($partial) + @($valid.packages | Select-Object -Skip 1)
                    $runtimeEditor.sourceFiles = @($runtimeEditor.sourceFiles[0]); $runtimePlayer.sourceFiles = @($runtimePlayer.sourceFiles[0])
                }
                'editor-in-player' { [UnityEditor.Compilation.CompilationPipeline]::Player = @($runtimePlayer, $mainEditor) }
                'editor-source-in-other-player-asmdef' { $alias = [UnityEditor.Compilation.Assembly]::new(); $alias.name = 'RenamedPlayer'; $alias.sourceFiles = @($mainEditor.sourceFiles); [UnityEditor.Compilation.CompilationPipeline]::Player = @($runtimePlayer, $alias) }
                'duplicate-runtime-source' { $runtimePlayer.sourceFiles = @($runtimePlayer.sourceFiles[0], $runtimePlayer.sourceFiles[0], $runtimePlayer.sourceFiles[1]) }
                'missing-output' { $mainEditor.outputPath = Join-Path $scriptAssemblies 'Missing.dll' }
                'wrong-output' { $mainEditor.outputPath = $editorBackup }
                'outside-source' { $runtimePlayer.sourceFiles = @((Join-Path $sandbox 'outside.cs'), $runtimePlayer.sourceFiles[1]) }
                'missing-physical-source' { $runtimePlayer.sourceFiles = @((Join-Path $mainApi.resolvedPath 'Runtime/Missing.cs'), $runtimePlayer.sourceFiles[1]) }
                'test-assembly' { $test = [UnityEditor.Compilation.Assembly]::new(); $test.name = 'Some.Tests'; [UnityEditor.Compilation.CompilationPipeline]::Editor = @($runtimeEditor, $mainEditor, $test) }
                'runtime-test-reference' { $runtimePlayer.compiledAssemblyReferences = @('nunit.framework.dll') }
                'guid-drift' { [UnityEditor.AssetDatabase]::Guids[$guidAsset] = '0' * 32 }
                'prefab-missing-script' { [UnityEditor.AssetDatabase]::Prefabs[$prefabAsset].MissingScripts = 1 }
                default {
                    if ($fault.StartsWith('missing-')) {
                        $relative = switch ($fault) { 'missing-Canvas' { 'Runtime/Core/Resources/YLFramework/Core/Canvas.prefab' } 'missing-EventSystem' { 'Runtime/Core/Resources/YLFramework/Core/EventSystem.prefab' } 'missing-ScreenTransitionCanvas' { 'Runtime/Systems/Action/Resources/ActionKit/ScreenTransitionCanvas.prefab' } }
                        $missingPrefab = Join-Path $mainApi.resolvedPath $relative; $prefabBytes = [IO.File]::ReadAllBytes($missingPrefab); [IO.File]::Delete($missingPrefab)
                        $inventory = Get-DistributionPackageContent -Root $mainApi.resolvedPath
                        if ($inventory.files.Count -ne 19) { throw 'The omitted-resource negative must still freeze every other fixture file.' }
                        $partial = $valid.packages[0].Clone(); $partial.files = $inventory.files; $partial.contentSha256 = $inventory.sha256
                        $valid.packages = @($partial) + @($valid.packages | Select-Object -Skip 1)
                    }
                }
            }
            try { $null = Invoke-InspectCase Failed "$origin $fault" }
            catch { $graphFailures.Add("$origin $fault : $($_.Exception.Message)") }
            finally {
                [UnityEditor.AssetDatabase]::Guids[$guidAsset] = $originalGuid; [UnityEditor.AssetDatabase]::Prefabs[$prefabAsset].MissingScripts = 0; $valid.packages = $originalExpected
                if ($null -ne $missingPrefab) { [IO.File]::WriteAllBytes($missingPrefab, $prefabBytes) }
            }
        }
        Set-LoadedFixtures; Set-InspectGraph $mainApi
        if ($origin -cin @('File', 'GitFixture', 'GitRemote')) {
            $originalExpected = $valid.packages
            try {
                $legacy = $valid.packages[0].Clone(); $legacy.Remove('files'); $valid.packages = @($legacy)
                $passedGraphs.Add((Invoke-InspectCase Passed "$origin legacy digest-only frozen inventory"))
            } finally { $valid.packages = $originalExpected }
        }
    }
    if ($graphFailures.Count -ne 0) { throw "Inspect graph contracts failed ($($graphFailures.Count)):`n$($graphFailures -join "`n")" }
    foreach ($observed in $passedGraphs) {
        $required = @($observed.result.requiredAssemblies)
        $runtime = @($required | Where-Object name -CEQ 'YLFramework.Runtime'); $editor = @($required | Where-Object name -CEQ 'YLFramework.Editor')
        if ($required.Count -ne 2 -or $runtime.Count -ne 1 -or $editor.Count -ne 1 -or
            $runtime[0].sourceFiles.Count -ne 2 -or $runtime[0].resolvedSources.Count -ne 2 -or $runtime[0].playerSourceFiles.Count -ne 2 -or $runtime[0].playerResolvedSources.Count -ne 2 -or $runtime[0].loadedTypeCount -ne 2 -or
            $editor[0].sourceFiles.Count -ne 1 -or $editor[0].resolvedSources.Count -ne 1 -or $editor[0].playerSourceFiles.Count -ne 0 -or $editor[0].loadedTypeCount -ne 1 -or
            $runtime[0].loadedPath -cne $runtimeDll -or $editor[0].loadedPath -cne $editorDll) { throw 'The report did not retain the exact actually observed Editor/Runtime/Player source sets and real loaded .NET fixture paths/types.' }; $checks++
    }
    "DistributionEntry NoUnity guards: $checks passed. NOT Unity compilation or execution."
} finally {
    if ($null -ne ('YLFramework.Testing.NoUnity.FixtureAssemblies' -as [type])) {
        [YLFramework.Testing.NoUnity.FixtureAssemblies]::Release()
        $unloaded = [YLFramework.Testing.NoUnity.FixtureAssemblies]::Unload()
        if (-not [YLFramework.Testing.NoUnity.FixtureAssemblies]::WaitForUnload($unloaded)) { throw 'The final NoUnity fixture context is still alive; its host cannot be deleted.' }
    }
    [IO.Directory]::Delete($sandbox, $true)
}
