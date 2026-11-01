#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param(
    [ValidateSet('List', 'Discover', 'Run', 'Clean')][string] $Action = 'List',
    [ValidateSet('Quick', 'Daily', 'Integration', 'Special')][string] $Suite = 'Quick',
    [string] $Module = 'Reddot',
    [string] $Unity,
    [string] $WorkspaceRoot = $env:YL_TEST_WORKSPACE,
    [string] $Selection,
    [ValidateRange(1, 86400)][int] $TimeoutSeconds = 600,
    [string] $Dotnet = (Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue).Source,
    [string] $NUnitAssembly = (Join-Path $env:USERPROFILE '.nuget/packages/nunit/3.14.0/lib/net45/nunit.framework.dll'),
    [switch] $KeepArtifacts,
    [switch] $Apply
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common/Process.ps1')
. (Join-Path $PSScriptRoot 'Common/Results.ps1')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$summary = [ordered]@{
    schemaVersion = 1; action = $Action; suite = $Suite; module = $Module; profile = $null
    status = 'PrerequisiteNotMet'; reason = $null; workspace = $null; ownerId = $null; endUtc = $null
    unity = $null; toolchain = $null; inputIdentity = $null; selection = $null; xml = @()
    processes = @(); testResult = [ordered]@{ status = 'NotRun' }; summary = $null
}
$exitCode = 2
$run = $null
$runLease = $null
$cacheLease = $null
$workspace = $null
$script:processStep = 0

function Invoke-TestingProcess([string] $Exe, [string[]] $Arguments) {
    $script:processStep++
    $start = [Diagnostics.ProcessStartInfo]::new($Exe)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $run.path
    $start.Environment['YL_TEST_OUTPUT_ROOT'] = $run.path
    if ('sourceEnvironment' -cin $definition.PSObject.Properties.Name) {
        foreach ($name in $definition.sourceEnvironment) { $start.Environment[$name] = $repository }
    }
    if ('outputEnvironment' -cin $definition.PSObject.Properties.Name) {
        foreach ($name in $definition.outputEnvironment) { $start.Environment[$name] = $run.path }
    }
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $fact = Invoke-YLTestProcess -StartInfo $start -Log (Join-Path $run.path "process-$script:processStep.log") -TimeoutMilliseconds ($TimeoutSeconds * 1000)
    $summary.processes += $fact
    Assert-YLTestProcessSucceeded $fact
    return $fact
}

function Get-SelectedSources([switch] $Native, [object] $EditorProfile) {
    $tests = [Collections.Generic.List[string]]::new()
    $folders = @($definition.editTests)
    $runtimeSources = @($definition.runtimeSources)
    $assets = @($definition.runtimeSources) + @($definition.assemblyMetadata)
    if ($Native) { $folders += $definition.playTests; $assets += $definition.nativeSources }
    $runtime = [Collections.Generic.List[string]]::new()
    foreach ($relative in $runtimeSources) {
        $path = Get-YLTestPath (Join-Path $repository $relative)
        $paths = if ([IO.Directory]::Exists($path)) { @(Get-YLTestTreeFiles $path) } else { @($path) }
        foreach ($file in $paths) {
            if ($file.EndsWith('.cs', [StringComparison]::OrdinalIgnoreCase)) { $runtime.Add([IO.Path]::GetRelativePath($repository, $file).Replace('\', '/')) }
        }
    }
    foreach ($folder in $folders) {
        $path = Get-YLTestPath (Join-Path $repository $folder)
        foreach ($file in @(Get-YLTestTreeFiles $path)) {
            if ($file.EndsWith('.cs', [StringComparison]::OrdinalIgnoreCase)) { $tests.Add([IO.Path]::GetRelativePath($repository, $file).Replace('\', '/')) }
        }
    }
    $inputs = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $assetSources = $assets + $folders
    if (-not $Native -and 'testSupport' -cin $definition.PSObject.Properties.Name) {
        $assetSources += @($definition.testSupport)
        foreach ($relative in $definition.testSupport) { $tests.Add($relative) }
    }
    if ($Native) {
        $assetSources += @('Assets/YLFramework/Tests/Infrastructure/Editor/YLTestEntry.cs',
            'Assets/YLFramework/Tests/Infrastructure/Editor/YLFramework.Tests.Infrastructure.Editor.asmdef')
    }
    foreach ($relative in $assetSources) {
        $path = Get-YLTestPath (Join-Path $repository $relative)
        if ([IO.Directory]::Exists($path)) {
            foreach ($file in @(Get-YLTestTreeFiles $path)) {
                $name = [IO.Path]::GetRelativePath($repository, $file).Replace('\', '/')
                $null = $inputs.Add($name)
                if ([IO.Path]::GetExtension($file) -cin @('.cs', '.asmdef', '.asmref', '.dll') -and -not [IO.File]::Exists($file + '.meta')) {
                    throw "Missing participating asset metadata: $name.meta"
                }
            }
        } else { $null = $inputs.Add($relative) }
        $meta = $relative + '.meta'
        if (-not [IO.File]::Exists((Join-Path $repository $meta))) { throw "Missing participating asset metadata: $meta" }
        $null = $inputs.Add($meta)
    }
    foreach ($relative in @('Tools/Testing/TestSuites.json', 'Tools/Testing/Invoke-YLTests.ps1',
        'Tools/Testing/Common/Workspace.ps1', 'Tools/Testing/Common/Process.ps1', 'Tools/Testing/Common/Results.ps1')) { $null = $inputs.Add($relative) }
    if ($Native) {
        $null = $inputs.Add('Tools/Testing/UnityHost.ps1')
        if ($definition.nativeLayout -ceq 'Framework') {
            foreach ($relative in @(Get-YLUnityPackageInputs -EditorProfile $EditorProfile)) { $null = $inputs.Add($relative) }
        }
    }
    else { $null = $inputs.Add('Tools/Testing/NUnitHost.cs'); $null = $inputs.Add('Tools/YLFramework.Analyzers/global.json') }
    return [pscustomobject]@{ tests = $tests.ToArray(); runtime = $runtime.ToArray(); inputs = @($inputs) }
}

function Compile-OfflineAssembly([string] $Output, [string[]] $Sources, [string[]] $References, [switch] $Executable) {
    $arguments = @('/nologo', '/nostdlib+', '/langversion:9.0', '/deterministic+', ('/define:' + ($compileDefines -join ';')), "/out:$Output")
    $arguments += $(if ($Executable) { '/target:exe' } else { '/target:library' })
    $arguments += $References | ForEach-Object { "/reference:$_" }
    $arguments += $Sources
    $response = Join-Path $run.path ([IO.Path]::GetFileName($Output) + '.rsp')
    [IO.File]::WriteAllLines($response, [string[]]@($arguments | ForEach-Object { '"' + $_ + '"' }), [Text.UTF8Encoding]::new($false))
    $null = Invoke-TestingProcess -Exe $Dotnet -Arguments @($compiler, '/noconfig', ('@' + $response))
}

function Get-OfflineSelection([string] $Executable, [object] $InputIdentity) {
    $null = Invoke-TestingProcess -Exe $offlineRunner -Arguments @($Executable, '--yl-discover', $definition.category, $run.path)
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create((Join-Path $run.path 'discovery.xml'), $settings)
    try { $document = [Xml.XmlDocument]::new(); $document.XmlResolver = $null; $document.Load($reader) }
    finally { $reader.Dispose() }
    $tests = @($document.SelectNodes('//test-case') | ForEach-Object {
        $node = $_
        $state = $node.GetAttribute('runstate')
        $assemblyNode = $node.ParentNode
        while ($null -ne $assemblyNode -and $assemblyNode.GetAttribute('type') -cne 'Assembly') {
            if ($assemblyNode.GetAttribute('runstate') -cne 'Runnable') { $state = $assemblyNode.GetAttribute('runstate') }
            $assemblyNode = $assemblyNode.ParentNode
        }
        if ($null -eq $assemblyNode) { throw 'Discovery has no assembly identity.' }
        [pscustomobject]@{
            assembly = [IO.Path]::GetFileNameWithoutExtension($assemblyNode.GetAttribute('name'))
            fullName = $node.GetAttribute('fullname'); mode = 'Offline'; runState = $state
        }
    })
    return New-YLTestSelection -InputIdentity $InputIdentity -Tests $tests
}

try {
    $configuration = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'TestSuites.json')) | ConvertFrom-Json -DateKind String
    if ($configuration.schemaVersion -ne 1) { throw 'Unsupported suite definition schema.' }
    if ($Action -ceq 'List') {
        $summary = [ordered]@{ action = 'List'; status = 'Completed'; suites = @($configuration.suites); modules = @($configuration.modules | Select-Object name, category, description, suites) }
        $exitCode = 0
    } else {
        $definitions = @($configuration.modules | Where-Object { $_.name -ceq $Module })
        if ($definitions.Count -ne 1 -or $Suite -cnotin $definitions[0].suites) { throw "No defined $Suite selection for module $Module." }
        $definition = $definitions[0]
        $root = Get-YLTestPath $WorkspaceRoot
        $summary.workspace = $root
        if ($Action -ceq 'Clean') {
            $rows = [Collections.Generic.List[object]]::new()
            if ([IO.Directory]::Exists($root)) {
                $marker = Get-YLTestPath (Join-Path $root '.yl-workspace.json')
                if (-not [IO.File]::Exists($marker)) { throw 'Unknown workspace ownership; cleanup is refused.' }
                $workspace = [IO.File]::ReadAllText($marker) | ConvertFrom-Json -DateKind String
                if ($workspace.root -ine $root -or $workspace.repository -ine $repository) { throw 'Workspace source identity does not match.' }
                Assert-YLTestWorkspace $workspace
                foreach ($group in [IO.Directory]::EnumerateDirectories((Join-Path $root 'Results'))) {
                    $key = [IO.Path]::GetFileName($group)
                    if (-not ($key.StartsWith("Run-$Suite-$Module-", [StringComparison]::Ordinal) -or
                        $key.StartsWith("Discover-$Suite-$Module-", [StringComparison]::Ordinal))) { continue }
                    foreach ($candidate in @(Get-YLTestRotation -Workspace $workspace -Key $key)) {
                        if ($Apply -and $candidate.canRemove) { $null = Remove-YLTestDirectory -Workspace $workspace -Path $candidate.path -Apply }
                        $rows.Add($candidate)
                    }
                }
            }
            $summary = [ordered]@{ action = 'Clean'; suite = $Suite; module = $Module; workspace = $root; status = 'Completed'; apply = [bool]$Apply; candidates = $rows.ToArray() }
            $exitCode = 0
        } else {
            $Unity = Get-YLTestPath -Path $Unity -ReadOnly
            $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($Unity)
            $editor = Get-YLTestEditorProfile -Editors $configuration.editors -VersionInfo $version
            $dotnetProfile = 'offlineRuntime' -cin $definition.PSObject.Properties.Name -and $definition.offlineRuntime.kind -ceq 'DotNet'
            if ($Suite -ceq 'Daily' -and 'nativeAssemblies' -cin $definition.PSObject.Properties.Name) {
                $definition.category = ''
                $definition.assemblies = @($definition.nativeAssemblies)
            }
            if ($Suite -ceq 'Special' -and -not $dotnetProfile) {
                if ([string]::IsNullOrWhiteSpace($definition.specialCategory)) { throw 'No explicit native special category is defined.' }
                $definition.category = $definition.specialCategory
                $definition.assemblies = @()
                $definition.excludedCategories = @()
            }
            if ($Suite -ceq 'Daily' -or ($Suite -ceq 'Special' -and -not $dotnetProfile)) {
                . (Join-Path $PSScriptRoot 'UnityHost.ps1')
                Invoke-YLUnitySuite -Repository $repository -Definition $definition -EditorProfile $editor -Unity $Unity -WorkspaceRoot $root `
                    -Action $Action -Suite $Suite -Module $Module -TimeoutSeconds $TimeoutSeconds -Selection $Selection -KeepArtifacts ([bool]$KeepArtifacts) -Summary $summary
                $exitCode = 0
            } else {
            $Dotnet = Get-YLTestPath -Path $Dotnet -ReadOnly
            $data = Join-Path ([IO.Path]::GetDirectoryName($Unity)) 'Data'
            $mono = Join-Path $data 'MonoBleedingEdge/bin/mono.exe'
            $framework = Join-Path $data 'MonoBleedingEdge/lib/mono/4.8-api'
            $managed = Join-Path $data 'Managed/UnityEngine'
            $core = Join-Path $managed 'UnityEngine.CoreModule.dll'
            $shared = Join-Path $managed 'UnityEngine.SharedInternalsModule.dll'
            $sdk = ([IO.File]::ReadAllText((Join-Path $repository 'Tools/YLFramework.Analyzers/global.json')) | ConvertFrom-Json).sdk.version
            $offlineRunner = $mono
            $runtimeConfig = $null
            if ($dotnetProfile) {
                $sdk = $definition.offlineRuntime.sdk
                $offlineRunner = $Dotnet
                if (-not $PSBoundParameters.ContainsKey('NUnitAssembly')) {
                    $NUnitAssembly = Join-Path $env:USERPROFILE '.nuget/packages/nunit/3.14.0/lib/netstandard2.0/nunit.framework.dll'
                }
                $runtimeConfig = @{ runtimeOptions = @{ tfm = 'net10.0'; framework = @{ name = 'Microsoft.NETCore.App'; version = $definition.offlineRuntime.version }; rollForward = 'Disable' } }
            }
            $compiler = Join-Path ([IO.Path]::GetDirectoryName($Dotnet)) "sdk/$sdk/Roslyn/bincore/csc.dll"
            $references = @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'Facades/netstandard.dll') | ForEach-Object { Join-Path $framework $_ }
            if ($dotnetProfile) {
                $referenceRoot = Join-Path ([IO.Path]::GetDirectoryName($Dotnet)) "packs/Microsoft.NETCore.App.Ref/$($definition.offlineRuntime.version)/ref/net10.0"
                $references = @([IO.Directory]::EnumerateFiles($referenceRoot, '*.dll'))
            }
            $runtimeReferences = @($core)
            $runtimeFrameworkReferences = @($references)
            if ('offlineReferences' -cin $definition.PSObject.Properties.Name) {
                $runtimeFrameworkReferences = @((Join-Path $data 'NetStandard/ref/2.1.0/netstandard.dll')) +
                    @([IO.Directory]::EnumerateFiles((Join-Path $data 'NetStandard/compat/2.1.0/shims/netstandard'), '*.dll'))
                $runtimeReferences += @([IO.Directory]::EnumerateFiles($managed, '*.dll'))
                $runtimeReferences += @($definition.offlineReferences | ForEach-Object { Get-YLTestPath -Path (Join-Path $repository $_) -ReadOnly })
            }
            $runtimeReferences = @($runtimeReferences | Sort-Object -Unique)
            $toolFiles = @($Unity, $Dotnet, $compiler, $mono, $core, $shared, $NUnitAssembly) + $references
            $toolFiles += $runtimeReferences
            $toolFiles += $runtimeFrameworkReferences
            if ($dotnetProfile) {
                $toolFiles += Join-Path ([IO.Path]::GetDirectoryName($Dotnet)) "shared/Microsoft.NETCore.App/$($definition.offlineRuntime.version)/System.Private.CoreLib.dll"
            }
            $toolFiles = @($toolFiles | Sort-Object -Unique)
            foreach ($path in $toolFiles) { if (-not [IO.File]::Exists($path)) { throw "Prerequisite not met: $path" } }
            if ([Reflection.AssemblyName]::GetAssemblyName($NUnitAssembly).Version.ToString() -cne '3.14.0.0') { throw 'The offline profile requires the existing NUnit 3.14.0 dependency.' }
            $profile = "Mono-$($editor.version)-SDK$sdk-NUnit3.14"
            if ($dotnetProfile) { $profile = "DotNet-$($definition.offlineRuntime.version)-$($editor.version)-SDK$sdk-NUnit3.14" }
            $summary.profile = $profile
            $summary.unity = [ordered]@{ executable = $Unity; requestedVersion = $editor.version; reportedFileVersion = $version.FileVersion; execution = $(if ($dotnetProfile) { '.NET BCL only, not Unity native acceptance' } else { 'Mono only, not Unity native acceptance' }) }
            $compileDefines = @('UNITY_5_3_OR_NEWER', 'UNITY_2020_1_OR_NEWER')
            if ($version.FileMajorPart -ge 6000) { $compileDefines += 'UNITY_6000_0_OR_NEWER' }
            $summary.toolchain = [ordered]@{ sdk = $sdk; compiler = $compiler; executor = $offlineRunner; nunit = $NUnitAssembly; defines = $compileDefines; runtimeConfig = $runtimeConfig }
            $sources = Get-SelectedSources
            $baseIdentity = Get-YLTestInputIdentity -Repository $repository -Paths $sources.inputs -Toolchain $toolFiles -Profile $profile
            $workspace = Initialize-YLTestWorkspace -WorkspaceRoot $root -Repository $repository
            $key = "$Action-$Suite-$Module-$($editor.version)"
            $run = New-YLTestDirectory -Workspace $workspace -Kind Result -Key $key
            $runLease = Lock-YLTestDirectory -Workspace $workspace -Path $run.path
            $summary.ownerId = $run.id
            $summary.summary = Join-Path $run.path 'summary.json'
            if ($KeepArtifacts) { [IO.File]::WriteAllText((Join-Path $run.path '.pinned'), 'Explicit KeepArtifacts retention.') }
            $exitCode = 1
            $summary.status = 'Failed'
            $cache = New-YLTestDirectory -Workspace $workspace -Kind Cache -Key ('Offline-' + $baseIdentity.sha256.Substring(0, 32))
            $cacheLease = Lock-YLTestDirectory -Workspace $workspace -Path $cache.path
            $build = Join-Path $cache.path 'Build'
            if (-not [IO.Path]::Exists($build)) {
                $staging = Join-Path $run.path 'Build'
                $null = [IO.Directory]::CreateDirectory($staging)
                $runtime = Join-Path $staging 'YLFramework.Runtime.dll'
                Compile-OfflineAssembly -Output $runtime -Sources @($sources.runtime | ForEach-Object { Join-Path $repository $_ }) -References ($runtimeFrameworkReferences + $runtimeReferences)
                $executable = Join-Path $staging 'YLFramework.Tests.EditMode.exe'
                Compile-OfflineAssembly -Output $executable -Sources (@($sources.tests | ForEach-Object { Join-Path $repository $_ }) + (Join-Path $repository 'Tools/Testing/NUnitHost.cs')) -References ($references + $runtimeReferences + @($runtime, $NUnitAssembly)) -Executable
                if ($dotnetProfile) { Write-YLTestJson -Path (Join-Path $staging 'YLFramework.Tests.EditMode.runtimeconfig.json') -Value $runtimeConfig }
                foreach ($path in @(@($NUnitAssembly, $shared) + $runtimeReferences | Sort-Object -Unique)) { [IO.File]::Copy($path, (Join-Path $staging ([IO.Path]::GetFileName($path)))) }
                $manifest = [ordered]@{ sourceIdentity = $baseIdentity.sha256; files = @([IO.Directory]::EnumerateFiles($staging) | ForEach-Object { [ordered]@{ name = [IO.Path]::GetFileName($_); sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash } }) }
                Write-YLTestJson -Path (Join-Path $staging 'build.json') -Value $manifest
                $fresh = Get-SelectedSources
                $recheck = Get-YLTestInputIdentity -Repository $repository -Paths $fresh.inputs -Toolchain $toolFiles -Profile $profile
                if ($recheck.sha256 -cne $baseIdentity.sha256) { throw 'Source inputs drifted during compilation; refusing to publish the cache.' }
                [IO.Directory]::Move($staging, $build)
            }
            $null = @(Get-YLTestTreeFiles $build)
            $manifest = [IO.File]::ReadAllText((Join-Path $build 'build.json')) | ConvertFrom-Json -DateKind String
            $expectedBinaries = @(@('YLFramework.Runtime.dll', 'YLFramework.Tests.EditMode.exe') + @(@($NUnitAssembly, $shared) + $runtimeReferences | ForEach-Object { [IO.Path]::GetFileName($_) }) | Sort-Object -Unique)
            if ($dotnetProfile) { $expectedBinaries += 'YLFramework.Tests.EditMode.runtimeconfig.json' }
            $manifestBinaries = [Collections.Generic.HashSet[string]]::new([string[]]@($manifest.files.name), [StringComparer]::OrdinalIgnoreCase)
            if ($manifest.sourceIdentity -cne $baseIdentity.sha256 -or $manifestBinaries.Count -ne $manifest.files.Count -or -not $manifestBinaries.SetEquals([string[]]$expectedBinaries)) { throw 'Owned build cache identity is invalid.' }
            $binaries = @($manifest.files | ForEach-Object {
                $path = Get-YLTestPath (Join-Path $build $_.name)
                if ([IO.Path]::GetDirectoryName($path) -ine $build -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $_.sha256) { throw 'Owned build cache content is invalid.' }
                $path
            })
            $identity = Get-YLTestInputIdentity -Repository $repository -Paths $sources.inputs -Toolchain ($toolFiles + $binaries) -Profile $profile
            $summary.inputIdentity = $identity
            $executable = Join-Path $build 'YLFramework.Tests.EditMode.exe'
            $actualSelection = Get-OfflineSelection -Executable $executable -InputIdentity $identity
            if ($Selection) {
                $saved = [IO.File]::ReadAllText((Get-YLTestPath $Selection)) | ConvertFrom-Json -DateKind String
                Assert-YLTestSelection -Selection $saved -InputIdentity $identity
                $savedKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                foreach ($test in $saved.tests) { $null = $savedKeys.Add($test.mode + '|' + $test.assembly + '|' + $test.fullName) }
                $currentKeys = @($actualSelection.tests | ForEach-Object { $_.mode + '|' + $_.assembly + '|' + $_.fullName })
                if (-not $savedKeys.SetEquals([string[]]$currentKeys)) { throw 'Saved selection does not match current category discovery.' }
            }
            $summary.selection = Join-Path $run.path 'selection.json'
            Write-YLTestJson -Path $summary.selection -Value $actualSelection
            if ($Action -ceq 'Run') {
                $filter = Join-Path $run.path 'filter.xml'
                $settings = [Xml.XmlWriterSettings]::new()
                $settings.OmitXmlDeclaration = $true
                $writer = [Xml.XmlWriter]::Create($filter, $settings)
                try {
                    $writer.WriteStartElement('filter'); $writer.WriteStartElement('or')
                    foreach ($test in $actualSelection.tests) { $writer.WriteElementString('test', $test.fullName) }
                    $writer.WriteEndElement(); $writer.WriteEndElement()
                } finally { $writer.Dispose() }
                $fact = Invoke-TestingProcess -Exe $offlineRunner -Arguments @($executable, '--yl-run', $filter, $run.path)
                $xml = Join-Path $run.path 'results.xml'
                $summary.xml = @($xml)
                $fresh = Get-SelectedSources
                $current = Get-YLTestInputIdentity -Repository $repository -Paths $fresh.inputs -Toolchain ($toolFiles + $binaries) -Profile $profile
                $summary.testResult = Read-YLTestXml -Path $xml -Selection $actualSelection -InputIdentity $current -Mode Offline -ProcessResult $fact
                $summary.status = 'Passed'
            } else { $summary.status = 'Completed' }
            $exitCode = 0
            }
        }
    }
} catch {
    $summary.reason = $_.Exception.Message
    if ($summary.status -ceq 'Failed') { $exitCode = 1 }
    [Console]::Error.WriteLine($_.Exception.Message)
} finally {
    if ($null -ne $run) {
        $summary.endUtc = [DateTime]::UtcNow.ToString('o')
        try { Write-YLTestJson -Path $summary.summary -Value $summary }
        finally {
            if ($null -ne $cacheLease) { $cacheLease.Dispose() }
            if ($null -ne $runLease) { $runLease.Dispose() }
        }
        try {
            $null = Invoke-YLTestRotation -Workspace $workspace -Key $run.key
        } catch { [Console]::Error.WriteLine('Retention was not applied: ' + $_.Exception.Message) }
    }
}
[Console]::Out.WriteLine((ConvertTo-Json -InputObject $summary -Depth 50))
exit $exitCode
