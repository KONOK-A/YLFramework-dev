#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $WorkspaceRoot,
    [Parameter(Mandatory)][string] $Unity,
    [ValidateRange(1, 86400)][int] $TimeoutSeconds = 900
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Test-YLIntegrationPackages.ps1') -WorkspaceRoot $WorkspaceRoot -Unity $Unity -Integration Excel -TimeoutSeconds $TimeoutSeconds
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$configuration = [IO.File]::ReadAllText((Join-Path $repository 'Tools/Testing/TestSuites.json')) | ConvertFrom-Json -DateKind String
$Unity = Get-YLTestPath -Path $Unity -ReadOnly
$editor = Get-YLTestEditorProfile -Editors $configuration.editors -VersionInfo ([Diagnostics.FileVersionInfo]::GetVersionInfo($Unity))
$workspace = Initialize-YLTestWorkspace -WorkspaceRoot $WorkspaceRoot -Repository $repository
$run = New-YLTestDirectory -Workspace $workspace -Kind Result -Key "OptionalAdapters-$($editor.productVersion)"
$leases = [Collections.Generic.List[IDisposable]]::new()
$leases.Add((Lock-YLTestDirectory -Workspace $workspace -Path $run.path))
$summary = [ordered]@{ schemaVersion = 1; purpose = 'YLTesting.OptionalAdapterFixture'; ownerId = $run.id; action = 'Check'; status = 'Failed';
    reason = $null; endUtc = $null; unity = $editor.productVersion; cases = @(); processes = @();
    scope = 'Real isolated UPM fixture installation, Editor compilation and Player assembly graph. Not framework runtime, Player build, Registry or release acceptance.' }
function FixtureFile([string] $Root, [string] $Relative, [string] $Content) {
    $path = Join-Path $Root $Relative
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllText($path, $Content, [Text.UTF8Encoding]::new($false))
    $guid = [Guid]::NewGuid().ToString('N')
    $importer = if ($Relative.EndsWith('.cs')) { "MonoImporter:`n  externalObjects: {}`n  serializedVersion: 2`n  defaultReferences: []`n  executionOrder: 0`n  icon: {instanceID: 0}`n" }
        elseif ($Relative.EndsWith('.asmdef')) { "AssemblyDefinitionImporter:`n  externalObjects: {}`n" } else { "DefaultImporter:`n  externalObjects: {}`n" }
    [IO.File]::WriteAllText($path + '.meta', "fileFormatVersion: 2`nguid: $guid`n${importer}  userData:`n  assetBundleName:`n  assetBundleVariant:`n", [Text.UTF8Encoding]::new($false))
    $directory = [IO.Path]::GetDirectoryName($path)
    while (-not [string]::Equals($directory, $Root, [StringComparison]::OrdinalIgnoreCase)) {
        if (-not [IO.File]::Exists($directory + '.meta')) { [IO.File]::WriteAllText($directory + '.meta', ("fileFormatVersion: 2`nguid: " + [Guid]::NewGuid().ToString('N') + "`nfolderAsset: yes`nDefaultImporter:`n  externalObjects: {}`n  userData:`n  assetBundleName:`n  assetBundleVariant:`n"), [Text.UTF8Encoding]::new($false)) }
        $directory = [IO.Path]::GetDirectoryName($directory)
    }
}
try {
    $cache = New-YLTestDirectory -Workspace $workspace -Kind Cache -Key ('OA-' + $run.id)
    $leases.Add((Lock-YLTestDirectory -Workspace $workspace -Path $cache.path))
    $main = 'com.kano.ylframework'; $feature = 'com.kano.ylframework.fixture-feature'; $backend = 'com.kano.ylframework.fixture-backend'
    $catalog = @{ schemaVersion = 1; packages = @(
        @{ id = $main; requiredFiles = @(); samples = @(); assemblies = @(
            @{ name = 'YLFramework.Runtime'; path = 'Runtime/YLFramework.Runtime.asmdef'; player = $true; defineConstraints = @(); versionDefines = @() },
            @{ name = 'YLFramework.Editor'; path = 'Editor/YLFramework.Editor.asmdef'; player = $false; defineConstraints = @(); versionDefines = @() }) },
        @{ id = $feature; requiredFiles = @(); samples = @(); assemblies = @(
            @{ name = 'YLFramework.FixtureFeature'; path = 'Runtime/YLFramework.FixtureFeature.asmdef'; player = $true; defineConstraints = @(); versionDefines = @() },
            @{ name = 'YLFramework.FixtureAdapter'; path = 'Runtime/Integration/Backend/YLFramework.FixtureAdapter.asmdef'; player = $true;
                defineConstraints = @('YL_FIXTURE_BACKEND'); versionDefines = @(@{ name = $backend; expression = '0.0.0'; define = 'YL_FIXTURE_BACKEND' }) }) },
        @{ id = $backend; requiredFiles = @(); samples = @(); assemblies = @(
            @{ name = 'YLFramework.FixtureBackend'; path = 'Runtime/YLFramework.FixtureBackend.asmdef'; player = $true; defineConstraints = @(); versionDefines = @() }) }
    ) }
    $catalogPath = Join-Path $cache.path 'PackageCatalog.json'
    Write-YLTestJson -Path $catalogPath -Value $catalog
    $catalog = Read-YLPackageCatalog -Path $catalogPath
    $sourceRoot = Join-Path $cache.path 'Sources'
    $inventories = @{}; $roots = @{}
    foreach ($package in $catalog.packages) {
        $id = $package.id; $root = Join-Path $sourceRoot $id
        $null = [IO.Directory]::CreateDirectory($root); $roots[$id] = $root
        $deps = @{}; if ($id -ceq $feature) { $deps[$main] = '0.1.0-preview.1' }
        FixtureFile $root 'package.json' (ConvertTo-Json -Depth 10 -InputObject @{ name = $id; version = '0.1.0-preview.1'; displayName = 'YL Optional Adapter Test Fixture';
            description = 'Generated test fixture, never a framework distribution package.'; unity = '2022.3'; license = 'MIT'; dependencies = $deps })
        FixtureFile $root 'LICENSE.md' 'MIT License. Generated YLFramework test fixture.'
        foreach ($assembly in $package.assemblies) {
            $references = @(); if ($assembly.name -cne 'YLFramework.Runtime' -and $assembly.name -cne 'YLFramework.FixtureBackend') { $references += 'YLFramework.Runtime' }
            if ($assembly.name -ceq 'YLFramework.FixtureAdapter') { $references += @('YLFramework.FixtureFeature', 'YLFramework.FixtureBackend') }
            $definition = @{ name = $assembly.name; references = $references; includePlatforms = @(); excludePlatforms = @();
                defineConstraints = $assembly.defineConstraints; versionDefines = $assembly.versionDefines; autoReferenced = $false }
            if (-not $assembly.player) { $definition.includePlatforms = @('Editor') }
            FixtureFile $root $assembly.path (ConvertTo-Json -InputObject $definition -Depth 15)
            $code = switch -CaseSensitive ($assembly.name) {
                'YLFramework.Runtime' { 'namespace YLFixture.Core { public sealed class CoreMarker {} }' }
                'YLFramework.Editor' { 'namespace YLFixture.Core { public sealed class EditorMarker {} }' }
                'YLFramework.FixtureFeature' { 'namespace YLFixture.Feature { public sealed class FeatureMarker {} }' }
                'YLFramework.FixtureBackend' { 'namespace YLFixture.Backend { public static class BackendApi { public static int Read() => 41; } }' }
                'YLFramework.FixtureAdapter' { 'namespace YLFixture.Feature { public static class BackendAdapter { public static int Read() => YLFixture.Backend.BackendApi.Read() + 1; } }' }
                default { throw 'Unknown fixture source owner.' }
            }
            FixtureFile $root ($assembly.path.Substring(0, $assembly.path.LastIndexOf('/')) + '/Fixture.cs') $code
        }
        $inventories[$id] = Get-YLIntegrationPackageContent -Root $root -PackageId $id
    }
    $profileInputs = @(Get-YLUnityPackageInputs -EditorProfile $editor)
    $paths = @('Tools/Testing/Tests/Test-YLOptionalAdapters.ps1', 'Tools/Testing/Tests/Test-YLIntegrationPackages.ps1', 'Tools/Testing/IntegrationEntry.cs',
        'Tools/Testing/PackageCatalogContract.cs', 'Tools/Testing/UnityHost.ps1', 'Tools/Testing/TestSuites.json',
        'Tools/Testing/Common/Workspace.ps1', 'Tools/Testing/Common/Process.ps1', 'Tools/Testing/Common/Results.ps1', 'Tools/Testing/Common/Packages.ps1') + $profileInputs
    $externalInputs = @($Unity, $catalogPath) + @(Get-YLTestTreeFiles $sourceRoot)
    $catalogSha = (Get-FileHash -LiteralPath $catalogPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $templateHash = (Get-YLTestInputIdentity -Repository $repository -Paths $paths -Toolchain $externalInputs -Profile 'GeneratedOptionalAdapterFixtureNotFrameworkPackage').sha256
    $bothHost = $null
    foreach ($scenario in @('Neither', 'Feature', 'Backend', 'Both', 'RemovedBackend')) {
        $presence = if ($scenario -ceq 'RemovedBackend') { 'Feature' } else { $scenario }
        $ids = @($main); if ($presence -cin @('Feature', 'Both')) { $ids += $feature }; if ($presence -cin @('Backend', 'Both')) { $ids += $backend }
        if ($scenario -ceq 'RemovedBackend') { $hostRecord = $bothHost }
        else {
            $hostRecord = New-YLTestDirectory -Workspace $workspace -Kind Host -Key ("OA-$scenario-" + $run.id.Substring(0, 12))
            $leases.Add((Lock-YLTestDirectory -Workspace $workspace -Path $hostRecord.path))
            if ($scenario -ceq 'Both') { $bothHost = $hostRecord }
        }
        Assert-YLUnityHostIdle $hostRecord
        $profile = "OptionalAdapterFixture-v1-$($editor.productVersion)-$scenario"
        $identity = Get-YLTestInputIdentity -Repository $repository -Paths $paths -Toolchain $externalInputs -Profile $profile
        $manifest = [IO.File]::ReadAllText((Join-Path $repository $profileInputs[0])) | ConvertFrom-Json -AsHashtable -DateKind String
        $lock = [IO.File]::ReadAllText((Join-Path $repository $profileInputs[1])) | ConvertFrom-Json -AsHashtable -DateKind String
        $dependencies = [ordered]@{ 'com.unity.ugui' = $editor.ugui; 'com.unity.nuget.newtonsoft-json' = $lock.dependencies.'com.unity.nuget.newtonsoft-json'.version }
        foreach ($entry in $manifest.dependencies.GetEnumerator()) { if ($entry.Key.StartsWith('com.unity.modules.', [StringComparison]::Ordinal)) { $dependencies[$entry.Key] = $entry.Value } }
        $files = [ordered]@{}
        $expectedPackages = @(foreach ($id in $ids) {
            foreach ($file in $inventories[$id].files) { $files["PackageSources/$id/$($file.path)"] = [IO.File]::ReadAllBytes((Join-Path $roots[$id] $file.path)) }
            $dependencies[$id] = "file:../PackageSources/$id"
            @{ name = $id; version = '0.1.0-preview.1'; root = (Join-Path $hostRecord.path "PackageSources/$id"); files = $inventories[$id].files; contentSha256 = $inventories[$id].sha256 }
        })
        $plan = @(Get-YLPackageAssemblyPlan -Catalog $catalog -Packages $expectedPackages -Profile OptionalAdapterFixture)
        $files['Assets/YLTestingIntegration/Editor/IntegrationEntry.cs'] = [IO.File]::ReadAllBytes((Join-Path $repository 'Tools/Testing/IntegrationEntry.cs'))
        $files['Assets/YLTestingIntegration/Editor/PackageCatalogContract.cs'] = [IO.File]::ReadAllBytes((Join-Path $repository 'Tools/Testing/PackageCatalogContract.cs'))
        $files['YLTesting/PackageCatalog.json'] = [IO.File]::ReadAllBytes($catalogPath)
        $files['Packages/manifest.json'] = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject @{ dependencies = $dependencies } -Depth 10))
        Sync-YLUnityHost -HostRecord $hostRecord -Files $files -Identity $identity -EditorVersion $editor.version
        $output = Join-Path $run.path $scenario; $null = [IO.Directory]::CreateDirectory($output)
        $request = @{ schemaVersion = 1; purpose = 'YLTesting.IntegrationPackages'; integration = 'OptionalAdapterFixture'; optionalAdapterScenario = $presence;
            hostId = $hostRecord.id; resultId = $run.id; workspaceId = $workspace.id; resultRoot = $run.path; output = $output; unityVersion = $editor.version;
            inputHash = $identity.sha256; expectedTemplateHash = $templateHash; catalogSha256 = $catalogSha; entryPackage = $main;
            manifestSha256 = (Get-FileHash -LiteralPath (Join-Path $hostRecord.path 'Packages/manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant();
            packageOrigin = 'File'; packages = $expectedPackages; assemblies = @($plan | Where-Object enabled); cacheRoot = $cache.path; cacheId = $cache.id }
        $requestPath = Join-Path $output 'request.json'; Write-YLTestJson -Path $requestPath -Value $request
        $start = [Diagnostics.ProcessStartInfo]::new($Unity); $start.UseShellExecute = $false; $start.WorkingDirectory = $hostRecord.path
        foreach ($key in @($start.Environment.Keys | Where-Object { $_.StartsWith('GIT_', [StringComparison]::OrdinalIgnoreCase) })) { $null = $start.Environment.Remove($key) }
        $start.Environment['UPM_CACHE_ROOT'] = Join-Path $cache.path 'Upm'; $start.Environment['UPM_NPM_CACHE_PATH'] = Join-Path $cache.path 'Upm/npm'
        $start.Environment['UPM_CACHE_PATH'] = Join-Path $cache.path 'Upm/packages'; $start.Environment['UPM_GIT_LFS_CACHE_PATH'] = Join-Path $cache.path 'Lfs'
        foreach ($argument in @('-batchmode', '-nographics', '-projectPath', $hostRecord.path, '-logFile', (Join-Path $output 'unity.log'),
            '-executeMethod', 'YLFramework.Testing.IntegrationEntry.Execute', '-ylIntegrationRequest', $requestPath)) { $start.ArgumentList.Add($argument) }
        $fact = Invoke-YLTestProcess -StartInfo $start -Log (Join-Path $output 'process.log') -TimeoutMilliseconds ([long]$TimeoutSeconds * 1000)
        $summary.processes += $fact; Assert-YLTestProcessSucceeded $fact
        $reportPath = Join-Path $output 'report.json'
        $report = (Read-YLIntegrationJson $reportPath).ToString() | ConvertFrom-Json -AsHashtable -DateKind String
        foreach ($field in @('purpose', 'integration', 'hostId', 'resultId', 'unityVersion', 'inputHash', 'expectedTemplateHash', 'catalogSha256', 'entryPackage', 'optionalAdapterScenario', 'packageOrigin')) {
            if ($report[$field] -isnot [string] -or $report[$field] -cne $request[$field]) { throw "Fixture report identity mismatch: $scenario/$field" }
        }
        $required = @($plan | Where-Object enabled)
        if ($report.schemaVersion -isnot [long] -or $report.schemaVersion -ne 1 -or $report.status -cne 'Completed' -or $report.pid -isnot [long] -or $report.pid -ne $fact.pid -or
            $report.isBatchMode -isnot [bool] -or -not $report.isBatchMode -or $report.scriptCompilationFailed -isnot [bool] -or $report.scriptCompilationFailed -or
            $report.packages -isnot [array] -or $report.packages.Count -ne $expectedPackages.Count -or $report.assemblies -isnot [array] -or $report.assemblies.Count -ne $required.Count) { throw "Incomplete real fixture compilation evidence: $scenario" }
        $actualLock = (Read-YLIntegrationJson (Join-Path $hostRecord.path 'Packages/packages-lock.json')).ToString() | ConvertFrom-Json -AsHashtable -DateKind String
        foreach ($package in $expectedPackages) {
            $rows = @($report.packages | Where-Object name -CEQ $package.name); if ($rows.Count -ne 1) { throw 'Missing/duplicate actual fixture package.' }
            Assert-YLIntegrationInstalledPackage -Request $request -Expected $package -Row $rows[0] -Lock $actualLock -HostRoot $hostRecord.path
        }
        foreach ($assembly in $required) {
            $rows = @($report.assemblies | Where-Object name -CEQ $assembly.name)
            if ($rows.Count -ne 1 -or $rows[0].loadedTypeCount -isnot [long] -or $rows[0].loadedTypeCount -le 0) { throw 'A required fixture assembly has no real loaded type evidence.' }
        }
        $adapters = @($report.assemblies | Where-Object name -CEQ 'YLFramework.FixtureAdapter')
        if ($adapters.Count -ne [int]($scenario -ceq 'Both')) { throw 'Adapter activation differs from explicit feature/backend prerequisites.' }
        $recheck = Get-YLTestInputIdentity -Repository $repository -Paths $paths -Toolchain $externalInputs -Profile $profile
        if ($recheck.sha256 -cne $identity.sha256) { throw 'Fixture source/toolchain inputs changed during real compilation.' }
        $summary.cases += @{ scenario = $scenario; status = 'Completed'; host = $hostRecord.path; report = $reportPath; inputIdentity = $identity;
            sourceRecheck = $recheck.sha256; packages = @($report.packages.name); assemblies = @($report.assemblies.name);
            adapterExpected = ($scenario -ceq 'Both'); adapterObserved = ($adapters.Count -eq 1);
            completePayloadSourceCount = @($plan | ForEach-Object { $_.sources }).Count }
        Write-YLTestJson -Path (Join-Path $output 'case-summary.json') -Value $summary.cases[-1]
    }
    if ($summary.cases.Count -ne 5 -or $summary.cases[3].host -cne $summary.cases[4].host) { throw 'The required four-scenario and same-host removal matrix is incomplete.' }
    $summary.status = 'Completed'
} catch { $summary.reason = $_.Exception.Message; throw }
finally {
    $summary.endUtc = [DateTime]::UtcNow.ToString('O'); Write-YLTestJson -Path (Join-Path $run.path 'summary.json') -Value $summary
    foreach ($lease in $leases) { $lease.Dispose() }
    "Optional-adapter fixture evidence: $($run.path)"
}
"Completed: $($editor.productVersion), four real combinations plus same-host backend removal. Not Player build/runtime acceptance."
