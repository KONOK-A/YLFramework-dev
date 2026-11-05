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
. (Join-Path $PSScriptRoot '../Common/Process.ps1')
. (Join-Path $PSScriptRoot '../Common/Results.ps1')
. (Join-Path $PSScriptRoot '../UnityHost.ps1')

function Get-YLDevelopmentHostDefinition([string] $Repository, [object] $Editor) {
    $sourceVersion = [IO.File]::ReadAllText((Join-Path $Repository 'ProjectSettings/ProjectVersion.txt'))
    if ($Editor.productVersion -cne '6000.3.27f1_672d562a42a7' -or
        $sourceVersion -cnotmatch '(?m)^m_EditorVersion: 6000\.3\.27f1\r?$' -or
        $sourceVersion -cnotmatch '(?m)^m_EditorVersionWithRevision: 6000\.3\.27f1 \(672d562a42a7\)\r?$') {
        throw 'PrerequisiteNotMet: This author-scene profile requires the exact source Editor 6000.3.27f1_672d562a42a7; a consumer compatibility host is not an author host.'
    }
    return @{ sourceProfile = 'YLFrameworkDev-6000.3.27f1_672d562a42a7'; jsonVersion = '3.2.2'; renderPipelineVersion = '17.3.0' }
}

function Get-YLDevelopmentHostInputs([string] $Repository) {
    $paths = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    foreach ($package in @(Get-YLUpmPackages -Repository $Repository)) {
        foreach ($file in @(Get-YLTestTreeFiles $package.root)) { $null = $paths.Add([IO.Path]::GetRelativePath($Repository, $file).Replace('\', '/')) }
    }
    foreach ($file in @(Get-YLTestTreeFiles (Join-Path $Repository 'Assets/Tests/YLFramework/Infrastructure/Editor'))) {
        $null = $paths.Add([IO.Path]::GetRelativePath($Repository, $file).Replace('\', '/'))
    }
    foreach ($relative in @(
        'Assets/Tests/YLFramework/EditMode/Editor/Distribution/FrameworkStructureTests.cs',
        'Assets/Tests/YLFramework/EditMode/Editor/Distribution/DevelopmentHostBoundaryTests.cs',
        'Assets/Tests/YLFramework/Fixtures/NamespaceMigration/ProductMoves.json',
        'Assets/Tests/YLFramework/Fixtures/Shared/PackageTestPaths.cs',
        'Assets/Tests/YLFramework/Fixtures/Shared/YLFramework.Tests.Fixtures.asmdef',
        'Assets/Scenes/YLFrameworkDev.unity', 'Assets/Settings/UniversalRP.asset', 'Assets/Settings/Renderer2D.asset',
        'Assets/Settings/UniversalRenderPipelineGlobalSettings.asset', 'Assets/Settings/DefaultVolumeProfile.asset',
        'PackageSources/com.kano.ylframework.data-assets/package.json', 'PackageSources/com.kano.ylframework.localization/package.json',
        'PackageSources/com.kano.ylframework.save/package.json',
        'PackageSources/com.kano.ylframework.dotween/package.json', 'PackageSources/com.kano.ylframework.excel/package.json',
        'PackageSources/com.kano.ylframework.domain-excel/package.json', 'PackageSources/com.kano.ylframework.hybridclr/package.json')) {
        $null = $paths.Add($relative); $null = $paths.Add($relative + '.meta')
    }
    foreach ($relative in @('ProjectSettings/ProjectVersion.txt', 'ProjectSettings/ProjectSettings.asset',
        'ProjectSettings/EditorBuildSettings.asset', 'ProjectSettings/GraphicsSettings.asset', 'ProjectSettings/QualitySettings.asset',
        'ProjectSettings/TagManager.asset', 'Packages/manifest.json', 'Packages/packages-lock.json',
        'Tools/Testing/Tests/Test-YLDevelopmentHost.ps1', 'Tools/Testing/DevelopmentHostEntry.cs',
        'Tools/Testing/Common/Workspace.ps1', 'Tools/Testing/Common/Process.ps1', 'Tools/Testing/Common/Results.ps1',
        'Tools/Testing/Common/Packages.ps1', 'Tools/Testing/UnityHost.ps1', 'Tools/Testing/TestSuites.json')) { $null = $paths.Add($relative) }
    return @($paths)
}

function Get-YLDevelopmentHostBoundary([string] $Repository) {
    # Only enumerate names for source-only boundaries; never stage SDK settings or user data.
    $resources = Join-Path $Repository 'Assets/Resources'
    $resourceFiles = if ([IO.Directory]::Exists($resources)) {
        @(Get-YLTestTreeFiles $resources | ForEach-Object { [IO.Path]::GetRelativePath($Repository, $_).Replace('\', '/') } | Sort-Object)
    } else { @() }
    $absences = [ordered]@{}
    foreach ($relative in @('Assets/Games/MainModule', 'Assets/Games/MainModule.meta', 'Assets/Scenes/Main.unity',
        'Assets/Scenes/Main.unity.meta', 'Assets/StreamingAssets/MainModule', 'Assets/Atlas(AutoGenerate)/MainModule', 'AssetBundles/MainModule')) {
        $absences[$relative] = [IO.Path]::Exists((Join-Path $Repository $relative))
    }
    $moves = [IO.File]::ReadAllText((Join-Path $Repository 'Assets/Tests/YLFramework/Fixtures/NamespaceMigration/ProductMoves.json')) | ConvertFrom-Json -DateKind String
    foreach ($move in $moves.Moves) {
        $relative = 'Assets/YLFramework/' + $move.From
        $absences[$relative] = [IO.Path]::Exists((Join-Path $Repository $relative))
    }
    return [ordered]@{
        resources = @($resourceFiles)
        settings = @([IO.Directory]::EnumerateFileSystemEntries((Join-Path $Repository 'ProjectSettings')) | ForEach-Object { [IO.Path]::GetFileName($_) } | Sort-Object)
        packageDirectories = @([IO.Directory]::EnumerateDirectories((Join-Path $Repository 'Packages')) | ForEach-Object { [IO.Path]::GetFileName($_) } | Sort-Object)
        forbiddenLocations = $absences
    }
}

if ($MyInvocation.InvocationName -eq '.') { return }
if (-not $IsWindows -or $PSVersionTable.PSVersion.Major -ne 7 -or $PSVersionTable.PSVersion.Minor -ne 6) { throw 'Windows PowerShell Core 7.6 is required.' }
$repository = Get-YLTestPath ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')))
$Unity = Get-YLTestPath -Path $Unity -ReadOnly
$configuration = [IO.File]::ReadAllText((Join-Path $repository 'Tools/Testing/TestSuites.json')) | ConvertFrom-Json -DateKind String
$editor = Get-YLTestEditorProfile -Editors $configuration.editors -VersionInfo ([Diagnostics.FileVersionInfo]::GetVersionInfo($Unity))
$definition = Get-YLDevelopmentHostDefinition -Repository $repository -Editor $editor
$workspace = Initialize-YLTestWorkspace -WorkspaceRoot $WorkspaceRoot -Repository $repository
$run = New-YLTestDirectory -Workspace $workspace -Kind Result -Key "DevelopmentHost-$($editor.productVersion)"
$lease = Lock-YLTestDirectory -Workspace $workspace -Path $run.path
$hostLease = $null; $hostRecord = $null; $identity = $null
$summary = [ordered]@{
    schemaVersion = 1; purpose = 'YLTesting.DevelopmentHost'; ownerId = $run.id; action = 'Run'; status = 'Failed'; reason = $null; endUtc = $null
    sourceProfile = $definition.sourceProfile; sourceRoot = $repository; host = $null; processes = @(); inputIdentity = $null; sourceRecheck = $null
    selection = $null; xml = @(); testResult = $null; unity = @{ executable = $Unity; version = $editor.version; productVersion = $editor.productVersion }
    scope = 'Read-only source dependency/file boundaries plus actual owned author-scene EditMode assertions; no SDK, Player, rendering or Unity 2022 compatibility acceptance.'
}
try {
    $boundary = Get-YLDevelopmentHostBoundary $repository
    $profile = $definition.sourceProfile + '|' + (ConvertTo-Json -InputObject $boundary -Depth 10 -Compress)
    $paths = @(Get-YLDevelopmentHostInputs $repository)
    $identity = Get-YLTestInputIdentity -Repository $repository -Paths $paths -Toolchain @($Unity) -Profile $profile
    $summary.inputIdentity = $identity; $summary['sourceBoundary'] = $boundary
    $hostRecord = New-YLTestDirectory -Workspace $workspace -Kind Host -Key ('DH-' + $run.id.Substring(0, 12))
    $hostLease = Lock-YLTestDirectory -Workspace $workspace -Path $hostRecord.path
    $summary.host = @{ path = $hostRecord.path; id = $hostRecord.id }
    Assert-YLUnityHostIdle $hostRecord
    $files = [ordered]@{}
    foreach ($relative in $paths) {
        if ($relative.StartsWith('Assets/', [StringComparison]::Ordinal) -or $relative.StartsWith('ProjectSettings/', [StringComparison]::Ordinal) -or
            $relative.StartsWith('PackageSources/com.kano.ylframework/', [StringComparison]::Ordinal)) {
            $files[$relative] = [IO.File]::ReadAllBytes((Join-Path $repository $relative))
        }
    }
    $manifest = [IO.File]::ReadAllText((Join-Path $repository 'Packages/manifest.json')) | ConvertFrom-Json -AsHashtable -DateKind String
    $dependencies = [ordered]@{
        'com.kano.ylframework' = 'file:../PackageSources/com.kano.ylframework'
        'com.unity.test-framework' = $editor.testFramework
        'com.unity.ugui' = $editor.ugui
        'com.unity.nuget.newtonsoft-json' = $definition.jsonVersion
        'com.unity.render-pipelines.universal' = $definition.renderPipelineVersion
    }
    foreach ($item in $manifest['dependencies'].GetEnumerator()) {
        if ($item.Key.StartsWith('com.unity.modules.', [StringComparison]::Ordinal)) { $dependencies[$item.Key] = $item.Value }
    }
    $files['Packages/manifest.json'] = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject @{ dependencies = $dependencies } -Depth 10) + "`n")
    $files['.yl-development-host.json'] = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject @{
        schemaVersion = 1; purpose = 'YLTesting.DevelopmentHost'; sourceProfile = $definition.sourceProfile; sourceRoot = $repository
        productVersion = $editor.productVersion; unityVersion = $editor.version; hostId = $hostRecord.id; workspaceId = $workspace.id
    }) + "`n")
    $files['Assets/Tests/YLFramework/EditMode/Editor/Distribution/YLFramework.DevelopmentHost.Tests.asmdef'] = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject @{
        name = 'YLFramework.DevelopmentHost.Tests'; references = @('YLFramework.Runtime', 'YLFramework.Editor', 'YLFramework.Tests.Fixtures', 'UnityEngine.TestRunner', 'UnityEditor.TestRunner')
        includePlatforms = @('Editor'); overrideReferences = $true; precompiledReferences = @('nunit.framework.dll', 'Newtonsoft.Json.dll')
        autoReferenced = $false; defineConstraints = @('UNITY_INCLUDE_TESTS')
    } -Depth 5) + "`n")
    $files['Assets/YLTestingDevelopmentHost/Editor/DevelopmentHostEntry.cs'] = [IO.File]::ReadAllBytes((Join-Path $repository 'Tools/Testing/DevelopmentHostEntry.cs'))
    $files['Assets/YLTestingDevelopmentHost/Editor/YLTesting.DevelopmentHost.Editor.asmdef'] = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject @{
        name = 'YLTesting.DevelopmentHost.Editor'; references = @('YLFramework.Tests.Infrastructure.Editor')
        includePlatforms = @('Editor'); overrideReferences = $true; precompiledReferences = @('Newtonsoft.Json.dll'); autoReferenced = $false
    }) + "`n")
    Sync-YLUnityHost -HostRecord $hostRecord -Files $files -Identity $identity -EditorVersion $editor.version

    function Invoke-DevelopmentHostOperation([string] $Action, [object[]] $Tests = @()) {
        Assert-YLUnityHostIdle $hostRecord
        if ((Get-YLTestDirectory -Workspace $workspace -Path $hostRecord.path).id -cne $hostRecord.id) { throw 'Development host ownership changed.' }
        $output = Join-Path $run.path $Action; $null = [IO.Directory]::CreateDirectory($output)
        $request = @{
            schemaVersion = 1; purpose = 'YLTesting.DevelopmentHost'; sourceProfile = $definition.sourceProfile
            hostId = $hostRecord.id; workspaceId = $workspace.id; resultId = $run.id; expectedUnityVersion = $editor.version
            inputHash = $identity.sha256; action = $Action; assemblies = @('YLFramework.DevelopmentHost.Tests'); category = ''
            excludedCategories = @(); sortingLayer = ''; output = $output; tests = $Tests; interactiveDesktop = $false
        }
        $requestPath = Join-Path $output 'request.json'; Write-YLTestJson -Path $requestPath -Value $request
        $start = [Diagnostics.ProcessStartInfo]::new($Unity); $start.UseShellExecute = $false; $start.WorkingDirectory = $hostRecord.path
        foreach ($argument in @('-batchmode', '-nographics', '-projectPath', $hostRecord.path, '-executeMethod', 'YLFramework.Testing.DevelopmentHostEntry.Execute',
            '-ylTestRequest', $requestPath, '-logFile', (Join-Path $output 'unity.log'))) { $start.ArgumentList.Add($argument) }
        $fact = Invoke-YLTestProcess -StartInfo $start -Log (Join-Path $output 'process.log') -TimeoutMilliseconds ([long]$TimeoutSeconds * 1000)
        $summary.processes += $fact
        $xml = Join-Path $output 'results-EditMode.xml'; if ([IO.File]::Exists($xml)) { $summary.xml += $xml }
        Assert-YLTestProcessSucceeded $fact
        $entry = [IO.File]::ReadAllText((Join-Path $output 'entry.json')) | ConvertFrom-Json -AsHashtable -DateKind String
        foreach ($field in @('purpose', 'sourceProfile', 'hostId', 'workspaceId', 'resultId', 'inputHash', 'action')) {
            if ($entry[$field] -isnot [string] -or $entry[$field] -cne $request[$field]) { throw "Development entry identity mismatch: $field" }
        }
        if ($entry['schemaVersion'] -isnot [long] -or $entry['schemaVersion'] -ne 1 -or $entry['pid'] -isnot [long] -or $entry['pid'] -ne $fact.pid -or
            $entry['isBatchMode'] -isnot [bool] -or -not $entry['isBatchMode'] -or $entry['sourceRoot'] -isnot [string] -or $entry['sourceRoot'] -cne $repository -or
            $entry['unityVersion'] -isnot [string] -or $entry['unityVersion'] -cne $editor.version) { throw 'The author entry lacks matching actual process/source evidence.' }
        $response = [IO.File]::ReadAllText((Join-Path $output 'response.json')) | ConvertFrom-Json -DateKind String
        Assert-YLUnityResponseIdentity -Response $response -Request $request -UnityVersion $editor.version
        if ($response.isBatchMode -isnot [bool] -or -not $response.isBatchMode -or @($response.excludedTests).Count -ne 0) { throw 'Author assertions cannot be excluded or run in the user Editor.' }
        return @{ response = $response; process = $fact; output = $output }
    }

    $discovery = Invoke-DevelopmentHostOperation Discover
    $afterDiscovery = Get-YLTestInputIdentity -Repository $repository -Paths @(Get-YLDevelopmentHostInputs $repository) -Toolchain @($Unity) -Profile ($definition.sourceProfile + '|' + (ConvertTo-Json -InputObject (Get-YLDevelopmentHostBoundary $repository) -Depth 10 -Compress))
    if ($afterDiscovery.sha256 -cne $identity.sha256) { throw 'Source inputs drifted during author-host discovery.' }
    $tools = @(@($Unity, (Join-Path $hostRecord.path 'Packages/manifest.json'), (Join-Path $hostRecord.path 'Packages/packages-lock.json')) + @($discovery.response.compiledInputs) | Sort-Object -Unique)
    $effective = Get-YLTestInputIdentity -Repository $repository -Paths $paths -Toolchain $tools -Profile $profile
    $selection = New-YLTestSelection -InputIdentity $effective -Tests @($discovery.response.tests)
    foreach ($class in @('FrameworkStructureTests', 'DevelopmentHostBoundaryTests')) {
        if (@($selection.tests | Where-Object { $_.mode -ceq 'EditMode' -and $_.assembly -ceq 'YLFramework.DevelopmentHost.Tests' -and
            $_.fullName.StartsWith("YLFramework.Tests.$class.", [StringComparison]::Ordinal) }).Count -eq 0) { throw "The real author test class was not discovered: $class" }
    }
    if (@($selection.tests | Where-Object { $_.mode -cne 'EditMode' -or $_.assembly -cne 'YLFramework.DevelopmentHost.Tests' }).Count -ne 0) { throw 'Unexpected author test execution boundary.' }
    $summary.selection = Join-Path $run.path 'selection.json'; Write-YLTestJson -Path $summary.selection -Value $selection
    $execution = Invoke-DevelopmentHostOperation Run -Tests @($selection.tests)
    $currentTools = @(@($Unity, (Join-Path $hostRecord.path 'Packages/manifest.json'), (Join-Path $hostRecord.path 'Packages/packages-lock.json')) + @($execution.response.compiledInputs) | Sort-Object -Unique)
    $current = Get-YLTestInputIdentity -Repository $repository -Paths @(Get-YLDevelopmentHostInputs $repository) -Toolchain $currentTools -Profile ($definition.sourceProfile + '|' + (ConvertTo-Json -InputObject (Get-YLDevelopmentHostBoundary $repository) -Depth 10 -Compress))
    $summary.testResult = Read-YLTestXml -Path (Join-Path $execution.output 'results-EditMode.xml') -Selection $selection -InputIdentity $current -Mode EditMode -ProcessResult $execution.process
    $summary.status = 'Passed'
} catch { $summary.reason = $_.Exception.Message }
finally {
    try {
        if ($null -ne $identity) {
            try {
                $recheck = Get-YLTestInputIdentity -Repository $repository -Paths @(Get-YLDevelopmentHostInputs $repository) -Toolchain @($Unity) -Profile ($definition.sourceProfile + '|' + (ConvertTo-Json -InputObject (Get-YLDevelopmentHostBoundary $repository) -Depth 10 -Compress))
                $summary.sourceRecheck = @{ before = $identity.sha256; after = $recheck.sha256; unchanged = ($identity.sha256 -ceq $recheck.sha256) }
                if ($identity.sha256 -cne $recheck.sha256) { throw 'Source inputs drifted during author-host verification; preserve this failed result and rerun when stable.' }
            } catch { $summary.status = 'Failed'; $summary.reason = [string]$summary.reason + ' Source recheck: ' + $_.Exception.Message }
        }
        $summary.endUtc = [DateTime]::UtcNow.ToString('o'); Write-YLTestJson -Path (Join-Path $run.path 'summary.json') -Value $summary
    } finally {
        if ($null -ne $hostLease) { $hostLease.Dispose() }
        $lease.Dispose()
    }
    $null = Invoke-YLTestRotation -Workspace $workspace -Key $run.key
    "DevelopmentHost evidence: $($run.path)"
    "$($summary.status): $($summary.reason)"
}
if ($summary.status -cne 'Passed') { exit 1 }
