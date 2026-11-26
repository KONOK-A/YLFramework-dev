#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $TemporaryRoot,
    [ValidateSet('All', 'Workspace', 'Process', 'Results')][string] $Group = 'All',
    [switch] $KeepArtifacts
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$common = Join-Path $PSScriptRoot '../Common'
foreach ($name in @('Workspace', 'Process', 'Results')) {
    $file = Join-Path $common ($name + '.ps1')
    if (Test-Path -LiteralPath $file -PathType Leaf) { . $file }
}
. (Join-Path $PSScriptRoot '../UnityHost.ps1')
if (-not [IO.Path]::IsPathFullyQualified($TemporaryRoot) -or -not [IO.Directory]::Exists($TemporaryRoot)) {
    throw 'TemporaryRoot must be an explicit existing filesystem directory.'
}
for ($cursor = [IO.Path]::GetFullPath($TemporaryRoot); $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
    if ([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked temporary roots are refused.' }
}
$sandbox = Join-Path $TemporaryRoot ('yl-testing-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($sandbox)
$repository = Join-Path $sandbox 'Repository'
$null = [IO.Directory]::CreateDirectory($repository)
$links = [Collections.Generic.List[string]]::new()
$failures = [Collections.Generic.List[string]]::new()
$script:cases = 0

function Assert-That([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Rejected([scriptblock] $Action, [string] $Pattern) {
    $rejected = $false
    try { $null = & $Action }
    catch {
        if ($_.Exception -is [Management.Automation.CommandNotFoundException]) { throw }
        if ($_.Exception.Message -notmatch $Pattern) { throw }
        $rejected = $true
    }
    Assert-That $rejected "Expected rejection matching: $Pattern"
}

function Assert-ResourceCurrentPlayerIsolation([Collections.IDictionary] $Probe) {
    Assert-That ($Probe.name -ceq 'YLFramework.Tests.ResourceCurrent.Player' -and
        @($Probe.references).Count -eq 2 -and $Probe.references -ccontains 'YLFramework.Runtime' -and
        $Probe.references -ccontains 'Unity.Addressables') 'ResourceCurrent Player isolation requires only the actual runtime and official Addressables references.'
    Assert-That (@($Probe.defineConstraints).Count -eq 1 -and
        $Probe.defineConstraints[0] -ceq 'UNITY_EDITOR || YL_RESOURCE_CURRENT_ACCEPTANCE') 'ResourceCurrent Player isolation requires the exact Editor-or-explicit-acceptance condition.'
    Assert-That (@($Probe.precompiledReferences).Count -eq 0 -and
        (-not $Probe.Contains('optionalUnityReferences') -or @($Probe.optionalUnityReferences).Count -eq 0)) 'ResourceCurrent Player isolation cannot acquire NUnit or TestRunner dependencies.'
    Assert-That (@($Probe.includePlatforms).Count -eq 0 -and @($Probe.excludePlatforms).Count -eq 0 -and
        @($Probe.versionDefines).Count -eq 0) 'ResourceCurrent Player isolation cannot restrict the acceptance platform or implicitly enable its symbol.'
    Assert-That ($Probe.autoReferenced -is [bool] -and -not $Probe.autoReferenced -and
        $Probe.noEngineReferences -is [bool] -and -not $Probe.noEngineReferences) 'ResourceCurrent Player isolation requires explicit consumers and real Unity engine references.'
}

function Test-Case([string] $Name, [scriptblock] $Body) {
    $script:cases++
    try { & $Body; "PASS: $Name" }
    catch { $failures.Add("${Name}: $($_.Exception.Message)"); "FAIL: ${Name}: $($_.Exception.Message)" }
}

function New-ProcessFixture([string] $Mode = 'Exit', [int] $ExitCode = 0) {
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $sandbox
    foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-File',
        (Join-Path $PSScriptRoot 'ProcessFixture.ps1'), '-Mode', $Mode, '-ExitCode', [string]$ExitCode)) {
        $start.ArgumentList.Add($argument)
    }
    return $start
}

function Assert-ChildExited([string] $Path) {
    Assert-That ([IO.File]::Exists($Path)) 'The real descendant did not publish its identity.'
    $child = [IO.File]::ReadAllText($Path) | ConvertFrom-Json -DateKind String
    try { $process = [Diagnostics.Process]::GetProcessById($child.pid) }
    catch [ArgumentException] { return }
    try {
        Assert-That ($process.HasExited -or $process.StartTime.ToUniversalTime().ToString('o') -cne $child.startUtc) 'An owned descendant survived cleanup.'
    } finally { $process.Dispose() }
}

function New-ResultFixture {
    $root = Join-Path $sandbox ([Guid]::NewGuid().ToString('N'))
    $null = [IO.Directory]::CreateDirectory($root)
    [IO.File]::WriteAllText((Join-Path $root 'Product.cs'), 'class Product {}')
    [IO.File]::WriteAllText((Join-Path $root 'Product.cs.meta'), 'guid: 0123456789abcdef0123456789abcdef')
    [IO.File]::WriteAllText((Join-Path $root 'TestSuites.json'), '{"profile":"Mono"}')
    [IO.File]::WriteAllText((Join-Path $root 'tool.dll'), 'toolchain-version-1')
    $paths = @('Product.cs', 'Product.cs.meta', 'TestSuites.json')
    $identity = Get-YLTestInputIdentity -Repository $root -Paths $paths -Toolchain @((Join-Path $root 'tool.dll')) -Profile 'Mono'
    $selection = New-YLTestSelection -InputIdentity $identity -Tests @(
        [pscustomobject]@{ assembly = 'Sample.Tests'; fullName = 'Example.A'; mode = 'EditMode'; runState = 'Runnable' },
        [pscustomobject]@{ assembly = 'Sample.Tests'; fullName = 'Example.B'; mode = 'EditMode'; runState = 'Runnable' }
    )
    $process = [pscustomobject]@{
        status = 'Exited'; observedExit = $true; exitCode = 0; timedOut = $false; cancelled = $false
        forcedCleanup = $false; observerError = $null; cleanupError = $null
        jobAssigned = $true; activeProcesses = 0; streamsDrained = $true
    }
    return [pscustomobject]@{ root = $root; paths = $paths; identity = $identity; selection = $selection; process = $process }
}

function New-RotationFixture([object] $Workspace, [string] $Status, [int] $Minute, [string] $Key = 'Daily-Reddot-Mono') {
    $run = New-YLTestDirectory -Workspace $Workspace -Kind Result -Key $Key
    Write-YLTestJson -Path (Join-Path $run.path 'summary.json') -Value @{
        schemaVersion = 1; ownerId = $run.id; action = 'Run'; status = $Status
        suite = 'Daily'; module = 'Reddot'; profile = 'Mono'
        endUtc = ([DateTimeOffset]'2026-10-01T00:00:00Z').AddMinutes($Minute).ToString('o')
    }
    return $run
}

function New-LegacyWorkspaceFixture([string] $Root, [string] $Repository) {
    $null = [IO.Directory]::CreateDirectory($Root)
    $owner = [pscustomobject]@{
        schemaVersion = 1; purpose = 'YLTesting.Workspace'; id = [Guid]::NewGuid().ToString('N')
        root = $Root; repository = $Repository; createdUtc = [DateTime]::UtcNow.ToString('o')
    }
    Write-YLTestJson -Path (Join-Path $Root '.yl-workspace.json') -Value $owner
    return $owner
}

try {
    if ($Group -in @('All', 'Workspace')) {
        Test-Case 'Main Resource declares Addressables directly and references the public runtime assemblies' {
            $root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $manifest = Read-YLPackageEvidenceJson (Join-Path $root 'PackageSources/com.kano.ylframework/package.json')
            Assert-That ($manifest.dependencies.Contains('com.unity.addressables') -and
                $manifest.dependencies['com.unity.addressables'] -ceq '1.25.0') 'The main product must own its minimum Addressables dependency without Localization.'
            $runtime = Read-YLPackageEvidenceJson (Join-Path $root 'PackageSources/com.kano.ylframework/Runtime/YLFramework.Runtime.asmdef')
            foreach ($name in @('Unity.Addressables', 'Unity.ResourceManager')) {
                Assert-That (@($runtime.references) -ccontains $name) "The main runtime must reference $name directly."
            }
            $editor = Read-YLPackageEvidenceJson (Join-Path $root 'PackageSources/com.kano.ylframework/Editor/YLFramework.Editor.asmdef')
            Assert-That (@($editor.references) -ccontains 'Unity.Addressables.Editor') 'Authoring requires the official Editor assembly.'
        }
        Test-Case 'Main-only native test projection keeps Addressables but not Localization' {
            $assembly = @{ name = 'YLFramework.Tests.EditMode'; references = @('YLFramework.Runtime', 'Unity.Addressables', 'Unity.ResourceManager', 'Unity.Addressables.Editor', 'Unity.Localization', 'Unity.Localization.Editor') }
            $copy = Get-YLNativeTestAssembly -Assembly $assembly -PackageIds @('com.kano.ylframework') -Catalog (Read-YLPackageCatalog)
            Assert-That (($copy.references -join ',') -ceq 'YLFramework.Runtime,Unity.Addressables,Unity.ResourceManager,Unity.Addressables.Editor') 'Addressables cannot disappear when the optional Localization product is absent.'
            Assert-That ($assembly.references.Count -eq 6) 'Projection changed the source assembly.'
        }
        Test-Case 'Addressables native profiles and resolved package evidence are exact' {
            $root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = [IO.File]::ReadAllText((Join-Path $root 'Tools/Testing/TestSuites.json')) | ConvertFrom-Json -DateKind String
            foreach ($editor in $configuration.editors) {
                $expected = if ($editor.version.StartsWith('2022.', [StringComparison]::Ordinal)) { '1.25.0' } else { '2.10.3' }
                Assert-That ((Get-YLUnityAddressablesVersion $editor) -ceq $expected) 'The host version pin must follow its exact declared Editor profile.'
            }
            $locked = @{ dependencies = @{ 'com.unity.addressables' = @{ version = '2.10.3'; source = 'registry' } } }
            $response = [pscustomobject]@{ addressablesVersion = '2.10.3'; addressablesSource = 'Registry' }
            $evidence = Assert-YLUnityAddressablesVersion -ExpectedVersion '2.10.3' -Lock $locked -Response $response
            Assert-That ($evidence.requested -ceq '2.10.3' -and $evidence.resolved -ceq '2.10.3') 'Actual resolution evidence was not returned.'
            $locked.dependencies['com.unity.addressables'].version = '1.25.0'
            Assert-Rejected { Assert-YLUnityAddressablesVersion -ExpectedVersion '2.10.3' -Lock $locked -Response $response } 'Addressables|version'
            $locked.dependencies['com.unity.addressables'].version = '2.10.3'
            $response.addressablesVersion = '1.25.0'
            Assert-Rejected { Assert-YLUnityAddressablesVersion -ExpectedVersion '2.10.3' -Lock $locked -Response $response } 'Addressables|version'
        }
        Test-Case 'Packed catalog is an explicit, isolated three-case matrix' {
            $config = Read-YLPackageEvidenceJson ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../TestSuites.json')))
            $editors = @($config.editors | ForEach-Object { [pscustomobject]$_ })
            $unity2022 = $editors | Where-Object version -CEQ '2022.3.62f3'
            $unity6 = $editors | Where-Object version -CEQ '6000.3.27f1'
            foreach ($name in @('ResourcePackedCatalog', 'ResourcePackedCatalogBinary')) {
                $entry = @($config.modules | Where-Object name -CEQ $name)
                Assert-That ($entry.Count -eq 1 -and ($entry[0].suites -join ',') -ceq 'Special' -and
                    $entry[0].specialCategory -ceq 'YL.Resource.Addressables.PackedContract') 'Packed catalog must be opt-in with the real test category.'
            }
            $json = Get-YLPackedCatalogProfile -Module ResourcePackedCatalog -EditorProfile $unity2022
            $binary = Get-YLPackedCatalogProfile -Module ResourcePackedCatalogBinary -EditorProfile $unity2022
            $modern = Get-YLPackedCatalogProfile -Module ResourcePackedCatalog -EditorProfile $unity6
            Assert-That ($json.expectedEncoding -ceq 'json' -and $json.define -ceq '' -and
                $binary.expectedEncoding -ceq 'binary' -and $binary.define -ceq 'ENABLE_BINARY_CATALOG' -and
                $modern.expectedEncoding -ceq 'binary' -and $modern.define -ceq '') 'The modern package must never masquerade as legacy binary evidence.'
            Assert-That ($binary.keySuffix -cne $json.keySuffix -and $binary.keySuffix.Contains('ENABLE_BINARY_CATALOG')) 'The binary compile symbol must distinguish owned host and input identities.'
            Assert-Rejected { Get-YLPackedCatalogProfile -Module ResourcePackedCatalogBinary -EditorProfile $unity6 } '1\.25\.0'
            Assert-That ($null -eq (Get-YLPackedCatalogProfile -Module ResourceAddressables -EditorProfile $unity2022)) 'The ordinary module must not acquire a new compile profile.'
        }
        Test-Case 'Packed catalog definitions accept the native Special dispatcher projection' {
            $configuration = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../TestSuites.json')) | ConvertFrom-Json -DateKind String
            foreach ($name in @('ResourcePackedCatalog', 'ResourcePackedCatalogBinary')) {
                $definition = $configuration.modules | Where-Object name -CEQ $name
                $definition.category = $definition.specialCategory
                $definition.assemblies = @()
                $definition.excludedCategories = @()
                Assert-That ($definition.category -ceq 'YL.Resource.Addressables.PackedContract' -and
                    $definition.assemblies.Count -eq 0 -and $definition.excludedCategories.Count -eq 0) "Special projection failed for $name."
            }
        }
        Test-Case 'Packed binary seeds only a cold owned host and never repairs drift' {
            $hostRecord = @{ path = Join-Path $sandbox 'PackedHost' }
            $null = [IO.Directory]::CreateDirectory((Join-Path $hostRecord.path 'ProjectSettings'))
            $settings = Initialize-YLPackedBinarySettings -HostRecord $hostRecord
            $original = [IO.File]::ReadAllText($settings)
            Assert-That ($original -cmatch '(?m)^  scriptingDefineSymbols:\r?\n    Standalone: ENABLE_BINARY_CATALOG\r?$') 'Expected Standalone symbol before the first compiler launch.'
            $null = [IO.Directory]::CreateDirectory((Join-Path $hostRecord.path 'Library'))
            $null = Initialize-YLPackedBinarySettings -HostRecord $hostRecord
            [IO.File]::WriteAllText($settings, $original.Replace('ENABLE_BINARY_CATALOG', 'OTHER'))
            Assert-Rejected { Initialize-YLPackedBinarySettings -HostRecord $hostRecord } 'ENABLE_BINARY_CATALOG'
            Assert-That ([IO.File]::ReadAllText($settings).Contains('Standalone: OTHER')) 'A reused host must not be rewritten to repair drift.'
            [IO.File]::Delete($settings)
            Assert-Rejected { Initialize-YLPackedBinarySettings -HostRecord $hostRecord } 'compiled host'
            Assert-That (-not [IO.File]::Exists($settings)) 'Do not silently reseed a compiled host.'
        }
        Test-Case 'Packed catalog evidence rejects missing, wrong or contradictory log markers' {
            $log = Join-Path $sandbox 'packed-unity.log'
            $packed = @{ expectedEncoding = 'binary'; define = 'ENABLE_BINARY_CATALOG' }
            $marker = '[PackedContract] Addressables 1.25.0, ENABLE_BINARY_CATALOG=True, ENABLE_JSON_CATALOG=False, runtime catalog=Library/aa/Windows/catalog.bin'
            [IO.File]::WriteAllText($log, "Unity build`r`n$marker`r`nstack trace`r`n")
            $evidence = Read-YLPackedCatalogEncoding -Log $log -Packed $packed -AddressablesVersion '1.25.0'
            Assert-That ($evidence.encoding -ceq 'binary' -and $evidence.log -ceq $log -and
                $evidence.logSha256 -ceq (Get-FileHash -LiteralPath $log).Hash.ToLowerInvariant()) 'Evidence must point to the exact Unity log.'
            Assert-Rejected { Read-YLPackedCatalogEncoding -Log $log -Packed $packed -AddressablesVersion '2.10.3' } 'encoding'
            [IO.File]::AppendAllText($log, $marker.Replace('.bin', '.json') + "`n")
            Assert-Rejected { Read-YLPackedCatalogEncoding -Log $log -Packed $packed -AddressablesVersion '1.25.0' } 'encoding'
            [IO.File]::WriteAllText($log, $marker.Replace('CATALOG=True', 'CATALOG=False'))
            Assert-Rejected { Read-YLPackedCatalogEncoding -Log $log -Packed $packed -AddressablesVersion '1.25.0' } 'symbols'
            [IO.File]::WriteAllText($log, "Expected $marker")
            Assert-Rejected { Read-YLPackedCatalogEncoding -Log $log -Packed $packed -AddressablesVersion '1.25.0' } 'encoding'
        }
        Test-Case 'Action automatic session gates never open an interactive visual window' {
            . (Join-Path $PSScriptRoot '../Common/Packages.ps1')
            $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Tools/Testing/TestSuites.json')
            $module = @($configuration.modules | Where-Object name -CEQ 'Action')[0]
            Assert-That (($module.suites -join ',') -ceq 'Daily,Special') 'Action needs its original Daily and a selectable Special.'
            Assert-That ($module.specialBackend -ceq 'SessionNative' -and $module.specialCategory -ceq 'YL.Special.ActionTimingSession' -and
                ($module.specialModes -join ',') -ceq 'EditMode') 'Special must select its actual session fixture.'
            Assert-That ($module.prepareSessionScene -and $module.prepareSceneTemplates -and $module.disableDomainReload -and
                $module.graphics) 'Real session checks need the owned saved graphics-capable host.'
            Assert-That ('interactiveDesktop' -cnotin $module.Keys) 'Automated Action checks must never open a window or depend on desktop focus.'
            Assert-That ('YL.Special.ActionTimingSession' -cin $module.excludedCategories -and
                'YL.Manual.ActionVisual' -cin $module.excludedCategories -and
                ($module.editTests -join ',') -ceq 'Assets/Tests/YLFramework/EditMode/Systems/Action' -and
                ($module.playTests -join ',') -ceq 'Assets/Tests/YLFramework/PlayMode/Systems/Action') 'Daily must retain its original leaves but exclude Special.'
        }
        Test-Case 'Action and Timing have a maintained actual IL2CPP Player gate, not an Editor or Mono substitute' {
            . (Join-Path $PSScriptRoot '../Common/Packages.ps1')
            $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Tools/Testing/TestSuites.json')
            $entries = @($configuration.modules | Where-Object name -CEQ 'ActionTimingIL2CPP')
            Assert-That ($entries.Count -eq 1) 'Action/Timing cannot be accepted without an actual IL2CPP build and run entry.'
            $module = $entries[0]
            Assert-That (($module.suites -join ',') -ceq 'Special' -and $module.specialBackend -ceq 'PlayerNative' -and
                $module.specialCategory -ceq 'YL.Special.ActionTimingIL2CPP' -and ($module.specialModes -join ',') -ceq 'EditMode' -and
                $module.shortHostKey -and -not $module.graphics -and $module.prepareSessionScene -and $module.prepareSceneTemplates) 'The AOT gate must build and run in an isolated headless Player host.'
            Assert-That (($module.packageInputs -join ',') -ceq 'com.kano.ylframework' -and $module.sampleInputs.Count -eq 0 -and
                ($module.runtimeSources -join ',') -ceq 'PackageSources/com.kano.ylframework/Runtime') 'Ordinary Action/Timing AOT must not acquire optional SDKs or unrelated domain packages.'
            Assert-That ('Assets/Tests/YLFramework/Fixtures/ActionTiming' -cin $module.nativeFixtureInputs -and
                ($module.editTests -join ',') -ceq 'Assets/Tests/YLFramework/Fixtures/ActionTiming/Editor' -and $module.playTests.Count -eq 0) 'The declared Player source and build case must actually be selected.'
            $probe = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Assets/Tests/YLFramework/Fixtures/ActionTiming/Runtime/YLFramework.ActionTiming.Acceptance.asmdef')
            Assert-That (($probe.references -join ',') -ceq 'YLFramework.Runtime' -and -not $probe.autoReferenced -and
                ($probe.defineConstraints -join ',') -ceq 'UNITY_EDITOR || YL_ACTIONTIMING_ACCEPTANCE' -and
                @($probe.precompiledReferences).Count -eq 0) 'Player probes must stay outside ordinary products and must not rely on NUnit/Editor.'
        }
        Test-Case 'Pooling Player selects the real split-metric cost fixture without leaking probes into products' {
            . (Join-Path $PSScriptRoot '../Common/Packages.ps1')
            $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Tools/Testing/TestSuites.json')
            $entries = @($configuration.modules | Where-Object name -CEQ 'PoolingPlayer')
            Assert-That ($entries.Count -eq 1) 'Pooling costs have no maintained actual Player selection.'
            $module = $entries[0]
            Assert-That (($module.suites -join ',') -ceq 'Special' -and $module.specialBackend -ceq 'PlayerNative' -and
                $module.specialCategory -ceq 'YL.Special.PoolingPlayer' -and ($module.specialModes -join ',') -ceq 'EditMode') 'Pool measurements require the actual Player backend, not an Editor or offline substitute.'
            Assert-That ((@(Get-YLUpmPackages -Repository $repoRoot -Ids $module.packageInputs).id -join ',') -ceq
                'com.kano.ylframework,com.kano.ylframework.gameplay') 'The cost fixture must retain its real Skill/Buff consumers without installing unrelated products.'
            Assert-That (($module.editTests -join ',') -ceq 'Assets/Tests/YLFramework/Fixtures/Pooling/Editor' -and
                'Assets/Tests/YLFramework/Fixtures/Pooling' -cin $module.nativeFixtureInputs -and
                $module.prepareSessionScene -and $module.prepareSceneTemplates) 'Player discovery requires the original probe sources and owned saved empty scene.'
            $probe = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Assets/Tests/YLFramework/Fixtures/Pooling/Runtime/YLFramework.Pooling.Acceptance.asmdef')
            Assert-That (($probe.references -join ',') -ceq 'YLFramework.Runtime,YLFramework.Skill,YLFramework.Buff' -and
                -not $probe.autoReferenced -and ($probe.defineConstraints -join ',') -ceq 'UNITY_EDITOR || YL_POOLING_ACCEPTANCE' -and
                @($probe.precompiledReferences).Count -eq 0) 'Runtime probes cannot enter normal Players or depend on NUnit/Editor.'
        }
        Test-Case 'Pooling IL2CPP selects the same workload with its own native acceptance category' {
            . (Join-Path $PSScriptRoot '../Common/Packages.ps1')
            $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Tools/Testing/TestSuites.json')
            $entries = @($configuration.modules | Where-Object name -CEQ 'PoolingIL2CPP')
            Assert-That ($entries.Count -eq 1) 'The actual IL2CPP support target has no selectable native pool check.'
            $module = $entries[0]
            $mono = @($configuration.modules | Where-Object name -CEQ 'PoolingPlayer')[0]
            Assert-That (($module.suites -join ',') -ceq 'Special' -and $module.specialBackend -ceq 'PlayerNative' -and
                $module.specialCategory -ceq 'YL.Special.PoolingIL2CPP' -and ($module.specialModes -join ',') -ceq 'EditMode' -and
                $module.shortHostKey -and $module.graphics -and $module.prepareSessionScene -and $module.prepareSceneTemplates) 'AOT checks must build and execute real owned Players, independently from the Mono selection.'
            foreach ($field in @('runtimeSources', 'nativeSources', 'nativeFixtureInputs', 'editTests', 'playTests', 'assemblyMetadata', 'packageInputs', 'sampleInputs')) {
                Assert-That (($module.$field -join ',') -ceq ($mono.$field -join ',')) "IL2CPP changed the workload/package closure: $field"
            }
        }
        Test-Case 'HybridCLR Daily selects both original test assemblies and its explicit sample with only the real package closure' {
            . (Join-Path $PSScriptRoot '../Common/Packages.ps1')
            $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Tools/Testing/TestSuites.json')
            $entries = @($configuration.modules | Where-Object name -CEQ 'HybridCLR')
            Assert-That ($entries.Count -eq 1) 'The changed Async consumer has no maintained HybridCLR behavior entry.'
            $module = $entries[0]
            Assert-That (($module.suites -join ',') -ceq 'Daily' -and $module.nativeLayout -ceq 'Framework' -and
                $module.graphics -and $module.nativeSdk -ceq 'HybridCLR') 'HybridCLR behavior must use its explicit graphical native host and approved Git SDK.'
            Assert-That (($module.packageInputs -join ',') -ceq 'com.kano.ylframework.hybridclr' -and
                (@(Get-YLUpmPackages -Repository $repoRoot -Ids $module.packageInputs).id -join ',') -ceq 'com.kano.ylframework,com.kano.ylframework.hybridclr') 'HybridCLR acquired an unrelated optional package.'
            Assert-That (($module.assemblies -join ',') -ceq 'YLFramework.HybridCLR.Tests.EditMode,YLFramework.HybridCLR.Tests.PlayMode' -and
                ($module.editTests -join ',') -ceq 'Assets/Tests/YLFramework/Integrations/HybridCLR/EditMode' -and
                ($module.playTests -join ',') -ceq 'Assets/Tests/YLFramework/Integrations/HybridCLR/PlayMode') 'Both original HybridCLR test sources and roles must remain selected.'
            Assert-That ($module.sampleInputs.Count -eq 1 -and $module.sampleInputs[0].package -ceq 'com.kano.ylframework.hybridclr' -and
                $module.sampleInputs[0].name -ceq 'HybridCLR Module' -and
                'Assets/Tests/YLFramework/Fixtures/Shared' -cin $module.nativeSources) 'HybridCLR requires its explicit sample import and shared fixture, not a fabricated test assembly.'
        }
        Test-Case 'Complete Help API and XML coverage uses the maintained native CLI with only the main product' {
            . (Join-Path $PSScriptRoot '../Common/Packages.ps1')
            $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Tools/Testing/TestSuites.json')
            $entries = @($configuration.modules | Where-Object name -CEQ 'Help')
            Assert-That ($entries.Count -eq 1) 'The full Help suite has no maintained CLI entry.'
            $help = $entries[0]
            Assert-That ($help.category -ceq '' -and $help.graphics -and ($help.modes -join ',') -ceq 'EditMode' -and ($help.suites -join ',') -ceq 'Daily') 'Help must retain actual graphical Editor execution, not a partial category or a managed substitute.'
            Assert-That (($help.editTests -join ',') -ceq 'Assets/Tests/YLFramework/EditMode/Editor/Help' -and ($help.packageInputs -join ',') -ceq 'com.kano.ylframework') 'Help must compile every original API/XML/window fixture without forcing optional products.'
            Assert-That (($help.outputEnvironment -join ',') -ceq 'BEE_CACHE_DIRECTORY') 'The real compiler XML side output must be produced in an owned cold compiler cache, not fabricated or borrowed from a global cache.'
            Assert-That ('Assets/Tests/YLFramework/Fixtures/NamespaceMigration' -cnotin $help.nativeSources -and 'Assets/Tests/YLFramework/EditMode/Editor/Common/EditorCommonStateFixture.cs' -cin $help.editTests + $help.nativeSources) 'Help must cold-read formally saved current state without copying the retired legacy snapshot group.'
        }
        Test-Case 'Core selects the actual HTTP PlayMode completion and cancellation consumers' {
            $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Tools/Testing/TestSuites.json')
            $core = @($configuration.modules | Where-Object name -CEQ 'Core')[0]
            Assert-That ('Assets/Tests/YLFramework/PlayMode/Systems/Http' -cin $core.playTests) 'The changed Async HTTP consumers must be compiled and run by Core, not left outside every Daily selection.'
            $http = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Assets/Tests/YLFramework/PlayMode/Systems/Http/YLFramework.Http.Tests.PlayMode.asmdef')
            Assert-That ($http.name -cin $core.assemblies) 'Copying HTTP sources is insufficient: Core must select their actual PlayMode assembly for discovery and execution.'
            Assert-That (($http.references -join ',') -ceq 'YLFramework.Runtime,UnityEngine.TestRunner' -and
                -not $http.autoReferenced -and ($http.defineConstraints -join ',') -ceq 'UNITY_INCLUDE_TESTS') 'The isolated HTTP tests must not acquire optional products or enter ordinary Players.'
            Assert-That ('Assets/Tests/YLFramework/EditMode/Systems/Http/HttpSessionTests.cs' -cin $core.editTests -and
                'YL.Special.CoreSession' -cin $core.excludedCategories -and $core.specialCategory -ceq 'YL.Special.CoreSession') 'HTTP real session discovery requires its original source in Core and the explicit Daily-excluded two-Play category.'
        }

        Test-Case 'Core compiles its Addressables fixture without running the other module lifecycle tests' {
            $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Tools/Testing/TestSuites.json')
            $core = @($configuration.modules | Where-Object name -CEQ 'Core')[0]
            $resource = @($configuration.modules | Where-Object name -CEQ 'ResourceAddressables')[0]
            $fixture = 'Assets/Tests/YLFramework/EditMode/Systems/Resource/ResourceAddressablesOwnershipTests.cs'
            Assert-That ($core.prepareAddressablesConsumerContent -and $fixture -cin $core.editTests -and
                'YL.Resource.Addressables' -cin $core.excludedCategories) 'Core must retain the packed preparation helper without rebuilding the separate ownership scenes within its own test session.'
            Assert-That ($fixture -cin $resource.editTests -and $resource.category -ceq 'YL.Resource.Addressables') 'The resource ownership tests must still be selected by their native resource module.'
        }
        Test-Case 'Core real UI acceptance has a separately selected two-Play native session and original scene inputs' {
            $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Tools/Testing/TestSuites.json')
            $core = @($configuration.modules | Where-Object name -CEQ 'Core')[0]
            Assert-That ('Special' -cin $core.suites -and $core.specialBackend -ceq 'SessionNative' -and
                $core.specialCategory -ceq 'YL.Special.CoreSession' -and ($core.specialModes -join ',') -ceq 'EditMode' -and
                $core.disableDomainReload -and $core.prepareSessionScene -and $core.prepareSceneTemplates -and $core.graphics) 'Two real Plays require an explicit owned graphical session profile with no domain reload.'
            Assert-That ('YL.Special.CoreSession' -cin $core.excludedCategories -and
                'Assets/Tests/YLFramework/Acceptance/Core/UIAcceptanceEntry.cs' -cin $core.nativeSources -and
                'Assets/Tests/YLFramework/Acceptance/Core/UIAcceptance.unity' -cin $core.nativeSources -and
                'Assets/Tests/YLFramework/Acceptance/Core/YLFramework.UI.Acceptance.asmdef' -cin $core.assemblyMetadata -and
                ($core.nativeUnityPackages -join ',') -ceq 'com.unity.inputsystem') 'Daily must not silently compile a session test without its actual scene, input assembly, target-locked Input System and settings.'
        }
        Test-Case 'Approved DOTween runs the original Help XML consumer with its actual optional assembly loaded' {
            $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Tools/Testing/TestSuites.json')
            $dotween = @($configuration.modules | Where-Object name -CEQ 'DOTween')[0]
            Assert-That ('Assets/Tests/YLFramework/EditMode/Editor/Help/ApiHelpTests.cs' -cin $dotween.editTests -and
                'YLFramework.Tests.EditMode' -cin $dotween.assemblies -and
                'Assets/Tests/YLFramework/EditMode/YLFramework.Tests.EditMode.asmdef' -cin $dotween.assemblyMetadata) 'Optional XML remarks cannot be validated by a core-only Help run or a selector that omits its real consumer.'
            Assert-That ($dotween.nativeSdk -ceq 'DOTween' -and ($dotween.packageInputs -join ',') -ceq 'com.kano.ylframework' -and
                ($dotween.outputEnvironment -join ',') -ceq 'BEE_CACHE_DIRECTORY') 'Help validation must retain the approved SDK profile and owned cold XML output without installing unrelated optional packages.'
        }
        Test-Case 'Offline references bind the exact real UPM resolution and refuse missing or escaping package files' {
            $root = Join-Path $sandbox ('InstalledReference-' + [Guid]::NewGuid().ToString('N'))
            $package = 'com.unity.nuget.newtonsoft-json'
            $cache = Join-Path $root "Library/PackageCache/$package@fingerprint"
            $null = [IO.Directory]::CreateDirectory((Join-Path $cache 'Runtime'))
            $null = [IO.Directory]::CreateDirectory((Join-Path $root 'Packages'))
            $null = [IO.Directory]::CreateDirectory((Join-Path $root 'Library/PackageManager'))
            $manifest = Join-Path $root 'Packages/manifest.json'; $lock = Join-Path $root 'Packages/packages-lock.json'
            $resolution = Join-Path $root 'Library/PackageManager/projectResolution.json'
            $metadata = Join-Path $cache 'package.json'; $dll = Join-Path $cache 'Runtime/Newtonsoft.Json.dll'
            [IO.File]::WriteAllText($manifest, '{"dependencies":{"com.unity.nuget.newtonsoft-json":"3.2.2"}}')
            [IO.File]::WriteAllText($lock, '{"dependencies":{"com.unity.nuget.newtonsoft-json":{"version":"3.2.2","source":"registry"}}}')
            [IO.File]::WriteAllText($metadata, '{"name":"com.unity.nuget.newtonsoft-json","version":"3.2.2"}')
            [IO.File]::WriteAllBytes($dll, [IO.File]::ReadAllBytes([Newtonsoft.Json.Linq.JObject].Assembly.Location))
            $record = @{ context = @{ projectPath = (Join-Path $root 'Packages'); unityVersion = '6000.3.27f1' }; outputs = @{
                "$package@3.2.2" = @{ name = $package; version = '3.2.2'; source = 'registry'; resolvedPath = $cache }
            } }
            [IO.File]::WriteAllText($resolution, (ConvertTo-Json -InputObject $record -Depth 10))
            $resolved = Get-YLInstalledPackageReference -Repository $root -PackageId $package -RelativePath 'Runtime/Newtonsoft.Json.dll' -UnityVersion '6000.3.27f1'
            Assert-That ($resolved.path -ceq (Get-YLTestPath -Path $dll -ReadOnly) -and $resolved.evidence.Count -eq 4) 'The exact DLL and all resolver declarations must participate in identity.'
            foreach ($fault in @('missing-resolution', 'wrong-version', 'wrong-name', 'wrong-source', 'escaped-root', 'missing-dll', 'editor-mismatch', 'traversal')) {
                $saved = [IO.File]::ReadAllBytes($resolution)
                try {
                    $options = @{ Repository = $root; PackageId = $package; RelativePath = 'Runtime/Newtonsoft.Json.dll'; UnityVersion = '6000.3.27f1' }
                    $changed = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $record -Depth 10) -AsHashtable
                    switch ($fault) {
                        'missing-resolution' { [IO.File]::Delete($resolution) }
                        'wrong-version' { $changed.outputs["$package@3.2.2"].version = '3.2.1' }
                        'wrong-name' { $changed.outputs["$package@3.2.2"].name = 'com.unity.unknown' }
                        'wrong-source' { $changed.outputs["$package@3.2.2"].source = 'git' }
                        'escaped-root' { $changed.outputs["$package@3.2.2"].resolvedPath = $sandbox }
                        'missing-dll' { $options.RelativePath = 'Runtime/Missing.dll' }
                        'editor-mismatch' { $options.UnityVersion = '2022.3.62f3' }
                        'traversal' { $options.RelativePath = '../outside.dll' }
                    }
                    if ($fault -cne 'missing-resolution') { [IO.File]::WriteAllText($resolution, (ConvertTo-Json -InputObject $changed -Depth 10)) }
                    Assert-Rejected { Get-YLInstalledPackageReference @options } 'Prerequisite|package|path|resolution|version|Editor|registry'
                } finally { [IO.File]::WriteAllBytes($resolution, $saved) }
            }
        }
        Test-Case 'Localization consumers declare their test closure without adding a product dependency' {
            $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Tools/Testing/TestSuites.json')
            foreach ($name in @('Excel', 'Data')) {
                $module = @($configuration.modules | Where-Object name -CEQ $name)[0]
                Assert-That (@($module.packageInputs) -ccontains 'com.kano.ylframework.localization') "$name test sources consume Localization but the declared installation closure omits it."
            }
            $excel = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'PackageSources/com.kano.ylframework.excel/package.json')
            Assert-That (-not $excel.dependencies.Contains('com.kano.ylframework.localization')) 'Test-only Localization must not become an Excel product dependency.'
        }
        Test-Case 'Gameplay owns the minimal four-domain and independent Equipment gates without Quick Editor leakage' {
            $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Tools/Testing/TestSuites.json')
            foreach ($name in @('Gameplay', 'Equipment')) {
                $module = @($configuration.modules | Where-Object name -CEQ $name)[0]
                Assert-That (($module.packageInputs -join ',') -ceq 'com.kano.ylframework.gameplay') "$name must keep its own two-package installation root."
                $closure = @(Get-YLUpmPackages -Repository $repoRoot -Ids @($module.packageInputs))
                Assert-That ((@($closure.id | Sort-Object) -join ',') -ceq 'com.kano.ylframework,com.kano.ylframework.gameplay') "$name acquired an unrelated optional feature."
            }
            foreach ($name in @('Data', 'ExcelGameplay', 'Domain')) {
                $module = @($configuration.modules | Where-Object name -CEQ $name)[0]
                Assert-That (@($module.packageInputs) -ccontains 'com.kano.ylframework.gameplay') "$name copies actual Gameplay consumers without declaring their package."
            }
            $domain = @($configuration.modules | Where-Object name -CEQ 'Domain')[0]
            Assert-That (@($domain.editTests) -cnotcontains 'Assets/Tests/YLFramework/EditMode/Editor/Equipment') 'Quick must not compile native-only Equipment authoring sources.'
            Assert-That (@($domain.nativeSources) -ccontains 'Assets/Tests/YLFramework/EditMode/Editor/Equipment') 'Original Equipment native authoring/admission contracts must remain selected.'
            Assert-That (@($domain.excludedCategories) -ccontains 'YL.Package.GameplayStandalone') 'A full authoring combination cannot execute an only-main-plus-Gameplay precondition.'
            foreach ($name in @('Core', 'Save', 'Localization', 'Movement2D', 'StateMachine', 'Excel')) {
                $module = @($configuration.modules | Where-Object name -CEQ $name)[0]
                Assert-That (@($module.packageInputs) -cnotcontains 'com.kano.ylframework.gameplay') "$name must not acquire Gameplay to retain a moved identity anchor."
            }
            $excel = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'PackageSources/com.kano.ylframework.excel/package.json')
            Assert-That (-not $excel.dependencies.Contains('com.kano.ylframework.gameplay')) 'Excel test-only Gameplay must not become an Excel product dependency.'
            $shared = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Assets/Tests/YLFramework/EditMode/YLFramework.Tests.EditMode.asmdef')
            Assert-That (@($shared.references) -cnotcontains 'YLFramework.Tests.Domain.Probes') 'A standalone fixture reference must not leak through shared tests to absent-feature hosts.'
        }
        Test-Case 'Movement2D standalone declares actual consumers without forcing it into unrelated modules' {
            $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Tools/Testing/TestSuites.json')
            $movement = @($configuration.modules | Where-Object name -CEQ 'Movement2D')[0]
            Assert-That (@($movement.packageInputs).Count -eq 1 -and $movement.packageInputs[0] -ceq 'com.kano.ylframework.movement2d') 'Movement2D must declare only its real product entry.'
            $closure = @(Get-YLUpmPackages -Repository $repoRoot -Ids @($movement.packageInputs))
            Assert-That ((@($closure.id | Sort-Object) -join ',') -ceq 'com.kano.ylframework,com.kano.ylframework.movement2d') 'Standalone movement acquired an unrelated feature package.'
            $consumers = @($configuration.modules | Where-Object { $_.Contains('packageInputs') -and @($_.packageInputs) -ccontains 'com.kano.ylframework.movement2d' })
            Assert-That ((@($consumers.name | Sort-Object) -join ',') -ceq 'Behavior,BehaviorPlayer,Movement2D,OverlapMovement') 'Only actual movement consumers may install the package.'
            Assert-That ($movement.sortingLayer -ceq '' -and $movement.prepareSessionScene -and $movement.prepareSceneTemplates) 'Movement Player authoring requires a real owned empty scene.'
            $edit = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Assets/Tests/YLFramework/EditMode/YLFramework.Tests.EditMode.asmdef')
            Assert-That (@($edit.references) -cnotcontains 'YLFramework.Movement2D.Acceptance') 'The standalone probe reference cannot leak to every shared test consumer.'
            $probe = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Assets/Tests/YLFramework/Fixtures/Movement2D/Runtime/YLFramework.Movement2D.Acceptance.asmdef')
            Assert-That (($probe.references -join ',') -ceq 'YLFramework.Character2D,YLFramework.Platform2D') 'The probe must not declare unrelated products or UI dependencies to repair an implicit Unity graph reference.'
        }
        Test-Case 'Overlap selects current native contracts without a legacy Quick guard' {
            $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = [IO.File]::ReadAllText((Join-Path $repository 'Tools/Testing/TestSuites.json')) | ConvertFrom-Json -DateKind String
            $overlap = @($configuration.modules | Where-Object name -CEQ 'Overlap')
            Assert-That ($overlap.Count -eq 1) 'The migrated Overlap tests require an independent module.'
            $definition = $overlap[0]
            Assert-That (($definition.packageInputs -join ',') -ceq 'com.kano.ylframework.overlap') 'Standalone Overlap must not force another optional product.'
            $closure = @(Get-YLUpmPackages -Repository $repository -Ids @($definition.packageInputs))
            Assert-That (($closure.id -join ',') -ceq 'com.kano.ylframework,com.kano.ylframework.overlap') 'Overlap has lost its exact two-product closure.'
            Assert-That (($definition.nativeAssemblies -join ',') -ceq 'YLFramework.Overlap.Tests.EditMode,YLFramework.Overlap.Tests.PlayMode') 'Both real native test assemblies must run independently.'
            Assert-That (($definition.suites -join ',') -ceq 'Daily' -and $definition.nativeLayout -ceq 'Framework' -and $definition.graphics) 'Current Detector contracts require real Unity physics, not a fabricated logic-only substitute.'
            Assert-That (($definition.runtimeSources -join ',') -ceq 'PackageSources/com.kano.ylframework.overlap/Runtime') 'The current package must replace the retired query-cycle guard.'
            $behavior = @($configuration.modules | Where-Object name -CEQ 'Behavior')[0]
            foreach ($assembly in $definition.nativeAssemblies) {
                Assert-That (@($behavior.nativeAssemblies) -cnotcontains $assembly) "Standalone Overlap tests must execute in Overlap, not repeat in Behavior: $assembly"
            }
            foreach ($name in @('Behavior', 'BehaviorPlayer')) {
                $consumer = @($configuration.modules | Where-Object name -CEQ $name)[0]
                Assert-That (@($consumer.packageInputs) -ccontains 'com.kano.ylframework.overlap') "$name omits its actual Overlap dependency."
                foreach ($side in @('Runtime/YLFramework.Overlap.asmdef', 'Editor/YLFramework.Overlap.Editor.asmdef')) {
                    Assert-That (@($consumer.assemblyMetadata) -ccontains ("PackageSources/com.kano.ylframework.overlap/$side")) "$name omits participating Overlap metadata: $side"
                }
            }
            $consumers = @($configuration.modules | Where-Object { 'packageInputs' -cin $_.PSObject.Properties.Name -and @($_.packageInputs) -ccontains 'com.kano.ylframework.overlap' })
            Assert-That (($consumers.name | Sort-Object) -join ',' -ceq 'Behavior,BehaviorPlayer,Overlap,OverlapEditor,OverlapMovement,OverlapPlayer') 'Do not install Overlap merely to preserve an absent feature identity.'
            $tokens = $null; $errors = $null
            $syntax = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repository 'Tools/Testing/Invoke-YLTests.ps1'), [ref]$tokens, [ref]$errors)
            Assert-That ($errors.Count -eq 0) 'The maintained entry does not parse.'
            $function = $syntax.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Get-SelectedSources' }, $true)
            . ([scriptblock]::Create($function.Extent.Text))
            $selected = Get-SelectedSources
            Assert-That (@($selected.runtime) -ccontains 'PackageSources/com.kano.ylframework.overlap/Runtime/Detection/OverlapDetector2D.cs') 'The native input must bind its actual current Detector.'
            Assert-That (-not (@($selected.runtime) -match '/Utilities/Overlap/')) 'Retired Overlap sources cannot remain in the selected compilation.'
            foreach ($input in @('PackageSources/com.kano.ylframework.overlap/Runtime/Detection/OverlapDetector2D.cs',
                'PackageSources/com.kano.ylframework.overlap/Runtime/Detection/OverlapDetector2D.cs.meta',
                'PackageSources/com.kano.ylframework.overlap/package.json', 'PackageSources/com.kano.ylframework.overlap/Runtime/YLFramework.Overlap.asmdef')) {
                Assert-That (@($selected.inputs) -ccontains $input) "Selection lost its actual package input identity: $input"
            }
        }
        Test-Case 'Overlap Player and movement coexistence are explicit isolated native consumers' {
            $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Tools/Testing/TestSuites.json')
            $player = @($configuration.modules | Where-Object name -CEQ 'OverlapPlayer')[0]
            Assert-That (($player.suites -join ',') -ceq 'Special' -and $player.specialBackend -ceq 'PlayerNative' -and
                ($player.specialModes -join ',') -ceq 'EditMode' -and $player.specialCategory -ceq 'YL.Special.OverlapPlayer' -and
                @($player.assemblies).Count -eq 0 -and $player.category -ceq '') 'Player must not leak into Daily/Quick or combine category with assembly selectors.'
            Assert-That ($player.prepareSessionScene -and $player.prepareSceneTemplates -and $player.nativeLayout -ceq 'Framework') 'The maintained shared Player helper requires the real empty owned scene profile.'
            Assert-That (($player.packageInputs -join ',') -ceq 'com.kano.ylframework.overlap') 'Minimal Player must not force movement or unrelated features.'
            $behaviorPlayer = @($configuration.modules | Where-Object name -CEQ 'BehaviorPlayer')[0]
            Assert-That (($behaviorPlayer.suites -join ',') -ceq 'Special' -and $behaviorPlayer.specialBackend -ceq 'PlayerNative' -and
                $behaviorPlayer.specialCategory -ceq 'YL.Special.BehaviorPlayer' -and $behaviorPlayer.prepareSessionScene -and
                $behaviorPlayer.prepareSceneTemplates) 'Current Behavior must run its actual Player probe through the owned shared helper.'
            Assert-That (@($behaviorPlayer.nativeSources) -ccontains 'Assets/Tests/YLFramework/Fixtures/Extensions/Behavior') 'The current authored producer and Contact consumer must be compiled, not replaced by the historical main receiver.'
            $behavior = @($configuration.modules | Where-Object name -CEQ 'Behavior')[0]
            Assert-That (@($behavior.excludedCategories) -ccontains $behaviorPlayer.specialCategory) 'Actual Player construction cannot leak into the ordinary Daily gate.'
            Assert-That (@($player.nativeSources) -ccontains 'Assets/Tests/YLFramework/Fixtures/Shared' -and
                @($player.nativeFixtureInputs) -ccontains 'Assets/Tests/YLFramework/Fixtures/Shared') 'Real shared helper sources and metadata must be installed.'
            Assert-That (@($player.nativeSources + $player.nativeFixtureInputs) -cnotcontains 'Assets/Tests/YLFramework/Fixtures/Overlap') 'Minimal Player cannot recursively copy the optional movement fixture.'
            $editor = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Assets/Tests/YLFramework/Fixtures/Overlap/Editor/YLFramework.Overlap.Tests.Player.Editor.asmdef')
            Assert-That (@($editor.references) -ccontains 'YLFramework.Editor') 'ResourceRelease in the shared helper signature requires a direct Editor reference even for null Resource.'
            $probe = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Assets/Tests/YLFramework/Fixtures/Overlap/Runtime/YLFramework.Overlap.Acceptance.asmdef')
            Assert-That (($probe.references -join ',') -ceq 'YLFramework.Runtime,YLFramework.Overlap') 'The actual Player probe has only its two products.'
            $combination = @($configuration.modules | Where-Object name -CEQ 'OverlapMovement')[0]
            $closure = @(Get-YLUpmPackages -Repository $repoRoot -Ids @($combination.packageInputs))
            Assert-That (($closure.id | Sort-Object) -join ',' -ceq 'com.kano.ylframework,com.kano.ylframework.movement2d,com.kano.ylframework.overlap') 'Coexistence must use exactly the real three-package closure.'
            Assert-That (($combination.suites -join ',') -ceq 'Daily' -and ($combination.modes -join ',') -ceq 'PlayMode' -and
                ($combination.assemblies -join ',') -ceq 'YLFramework.Overlap.Movement.Tests' -and $combination.category -ceq '') 'Native coexistence must be selected independently of protected historical Behavior acceptance.'
            $fixture = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Assets/Tests/YLFramework/Fixtures/Overlap/IntegrationMovement/YLFramework.Overlap.Movement.Tests.asmdef')
            Assert-That (($fixture.defineConstraints -join ',') -ceq 'UNITY_INCLUDE_TESTS,YL_OVERLAP_PACKAGE,YL_MOVEMENT2D_PACKAGE') 'Optional presence is package-scoped, never a global macro.'
            Assert-That (($fixture.versionDefines.name -join ',') -ceq 'com.kano.ylframework.overlap,com.kano.ylframework.movement2d') 'Both actual products must control their own fixture boundary.'
            foreach ($reference in @('YLFramework.Character2D', 'YLFramework.Platform2D', 'YLFramework.Overlap')) {
                Assert-That (@($fixture.references) -ccontains $reference) "Missing real combination consumer: $reference"
            }
        }
        Test-Case 'Overlap consumers and Catalog require only the current implementation' {
            $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            foreach ($relative in @('EditMode/YLFramework.Tests.EditMode.asmdef', 'PlayMode/YLFramework.Tests.PlayMode.asmdef',
                'Fixtures/Extensions/Behavior/Player/YLFramework.Behavior.Acceptance.asmdef',
                'Fixtures/Extensions/Behavior/Editor/YLFramework.Behavior.Acceptance.Editor.asmdef')) {
                $consumer = Read-YLPackageEvidenceJson (Join-Path $repoRoot "Assets/Tests/YLFramework/$relative")
                Assert-That (@($consumer.references) -ccontains 'YLFramework.Overlap') "A real consumer omits its direct runtime reference: $relative"
            }
            foreach ($side in @('EditMode', 'PlayMode')) {
                $consumer = Read-YLPackageEvidenceJson (Join-Path $repoRoot "Assets/Tests/YLFramework/$side/Utilities/Overlap/YLFramework.Overlap.Tests.$side.asmdef")
                Assert-That ($consumer.name -ceq "YLFramework.Overlap.Tests.$side" -and @($consumer.references) -ccontains 'YLFramework.Runtime' -and
                    @($consumer.references) -ccontains 'YLFramework.Overlap' -and @($consumer.references) -ccontains 'YLFramework.Tests.Fixtures') 'The standalone tests must use real product and fixture owners.'
            }
            $catalog = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Tools/Packaging/PackageCatalog.json')
            $package = @($catalog.packages | Where-Object id -CEQ 'com.kano.ylframework.overlap')[0]
            Assert-That (-not (@($package.requiredFiles) -match 'Utilities/Overlap')) 'A historical migration row is not a current required product file.'
            foreach ($path in @('Runtime/Detection/OverlapDetector2D.cs', 'Runtime/Detection/OverlapDetector3D.cs',
                'Runtime/Detection/OverlapDetectionState.cs', 'Runtime/Queries/OverlapQuery2D.cs', 'Runtime/Queries/OverlapQuery3D.cs',
                'Runtime/Queries/OverlapQueryWorkspace2D.cs', 'Runtime/Queries/OverlapQueryWorkspace3D.cs', 'Editor/OverlapDetectorInspector.cs')) {
                Assert-That (@($package.requiredFiles) -ccontains $path -and @($package.requiredFiles) -ccontains ($path + '.meta')) "Missing current required file/identity: $path"
                Assert-That ([IO.File]::Exists((Join-Path $repoRoot "PackageSources/com.kano.ylframework.overlap/$path"))) "Missing current implementation: $path"
            }
        }
        Test-Case 'Configured FSM standalone keeps its exact two-package closure and real reload scene preconditions' {
            $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Tools/Testing/TestSuites.json')
            $fsm = @($configuration.modules | Where-Object name -CEQ 'StateMachine')[0]
            Assert-That (@($fsm.packageInputs).Count -eq 1 -and $fsm.packageInputs[0] -ceq 'com.kano.ylframework.state-machine') 'The standalone configured FSM root must be declared exactly.'
            Assert-That ($fsm.Contains('sortingLayer') -and $fsm.sortingLayer -ceq '') 'The owned native host requires an explicit no-sorting-layer declaration.'
            $consumers = @($configuration.modules | Where-Object { $_.Contains('packageInputs') -and @($_.packageInputs) -ccontains 'com.kano.ylframework.state-machine' })
            Assert-That ((@($consumers.name | Sort-Object) -join ',') -ceq 'Behavior,BehaviorPlayer,StateMachine') 'Only actual FSM consumers may declare this optional package.'
            $closure = @(Get-YLUpmPackages -Repository $repoRoot -Ids @($fsm.packageInputs))
            Assert-That (($closure.id | Sort-Object) -join ',' -ceq 'com.kano.ylframework,com.kano.ylframework.state-machine') 'The standalone FSM acquired an unrelated functional package.'
            Assert-That ($fsm.Contains('prepareSessionScene') -and $fsm.prepareSessionScene -is [bool] -and $fsm.prepareSessionScene -and
                $fsm.Contains('prepareSceneTemplates') -and $fsm.prepareSceneTemplates -is [bool] -and $fsm.prepareSceneTemplates) 'The actual reload/Player acceptance needs the owned empty session scene and templates.'
            foreach ($name in @('Core', 'Save', 'Localization', 'DataAssets')) {
                $module = @($configuration.modules | Where-Object name -CEQ $name)[0]
                Assert-That (@($module.packageInputs) -cnotcontains 'com.kano.ylframework.state-machine') "$name must not acquire the configured FSM to keep old assembly anchors alive."
            }
            $editAssembly = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Assets/Tests/YLFramework/EditMode/YLFramework.Tests.EditMode.asmdef')
            Assert-That (@($editAssembly.references) -cnotcontains 'YLFramework.StateMachine.Acceptance') 'A standalone fixture reference cannot leak into every shared test assembly consumer.'
            $fixtureMetadata = @([IO.Directory]::EnumerateFiles((Join-Path $repoRoot 'Assets/Tests/YLFramework/Fixtures/StateMachine'), '*.meta', [IO.SearchOption]::AllDirectories)) +
                @((Join-Path $repoRoot 'Assets/Tests/YLFramework/Fixtures/StateMachine.meta'), (Join-Path $repoRoot 'Assets/Tests/YLFramework/EditMode/Editor/StateMachine/StateMachinePackageAcceptanceTests.cs.meta'))
            Assert-That ($fixtureMetadata.Count -eq 6) 'The standalone fixture must retain all six owned asset identities.'
            foreach ($metadata in $fixtureMetadata) {
                Assert-That ([regex]::Matches([IO.File]::ReadAllText($metadata), '(?m)^guid: [0-9a-f]{32}\s*$').Count -eq 1) "Unity cannot import a malformed standalone fixture identity: $metadata"
            }
        }
        Test-Case 'ResourceUpdates selects catalog and signed update contracts and keeps ResourceCurrentPublication separate' {
            $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = [IO.File]::ReadAllText((Join-Path $repository 'Tools/Testing/TestSuites.json')) | ConvertFrom-Json -DateKind String
            $modules = @($configuration.modules | Where-Object name -CEQ 'ResourceUpdates')
            Assert-That ($modules.Count -eq 1) 'There must be exactly one native ResourceUpdates entry.'
            $definition = $modules[0]
            Assert-That (($definition.suites -join ',') -ceq 'Special' -and $definition.specialBackend -ceq 'SessionNative' -and
                ($definition.specialModes -join ',') -ceq 'EditMode' -and $definition.specialCategory -ceq 'YL.Special.ResourceUpdates' -and
                @($definition.assemblies).Count -eq 0 -and $definition.category -ceq '') 'Resource updates require one explicit native category, not a Daily or broad assembly selection.'
            Assert-That ($definition.nativeLayout -ceq 'Framework' -and $definition.prepareSessionScene -and $definition.prepareSceneTemplates -and
                $definition.shortHostKey -and $definition.graphics) 'Official catalog and download tests require their saved owned scene and native host.'
            $closure = @(Get-YLUpmPackages -Repository $repository -Ids @($definition.packageInputs))
            Assert-That (($closure.id -join ',') -ceq 'com.kano.ylframework') 'Resource updates cannot acquire Audio, Localization, DataAssets or other optional products.'
            $resourceTests = 'Assets/Tests/YLFramework/EditMode/Systems/Resource'
            $expected = @(
                "$resourceTests/ResourceAddressablesUpdateTests.cs"
                "$resourceTests/ResourceContentCatalogTests.cs"
                "$resourceTests/ResourceContentManifestTests.cs"
                "$resourceTests/ResourceContentStoreTests.cs"
            )
            $ownership = 'Assets/Tests/YLFramework/EditMode/Systems/Resource/ResourceAddressablesOwnershipTests.cs'
            Assert-That (($definition.editTests -join ',') -ceq ($expected -join ',') -and
                @($definition.playTests).Count -eq 0) 'ResourceUpdates must select exactly the four declared EditMode suites.'
            Assert-That (@($definition.sourceEnvironment).Count -eq 0 -and
                ($definition.outputEnvironment -join ',') -ceq 'YL_TEST_OUTPUT_ROOT') 'ResourceUpdates must not need legacy release paths or credentials.'
            $tokens = $null; $errors = $null
            $syntax = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repository 'Tools/Testing/Invoke-YLTests.ps1'), [ref]$tokens, [ref]$errors)
            Assert-That ($errors.Count -eq 0) 'The maintained test entry does not parse.'
            $function = $syntax.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Get-SelectedSources' }, $true)
            . ([scriptblock]::Create($function.Extent.Text))
            $profile = @($configuration.editors | Where-Object version -CEQ '6000.3.27f1')[0]
            $selected = Get-SelectedSources -Native -EditorProfile $profile
            Assert-That (($selected.tests -join ',') -ceq ($expected -join ',')) 'The actual selector must execute all four ResourceUpdates suites.'
            foreach ($source in $expected + @($ownership, 'Assets/Tests/YLFramework/Fixtures/Shared/PackageTestPaths.cs')) {
                Assert-That (@($selected.inputs) -ccontains $source -and @($selected.inputs) -ccontains ($source + '.meta')) "The actual native input identity lost source or metadata: $source"
            }
            foreach ($source in $expected) {
                Assert-That ([IO.File]::ReadAllText((Join-Path $repository $source)).Contains('[Category("YL.Special.ResourceUpdates")]')) "The ResourceUpdates category is missing from $source."
            }
            foreach ($source in @('Assets/Tests/YLFramework/EditMode/Editor/Resource/ResourceCurrentPublicationTests.cs',
                'Assets/Tests/YLFramework/Fixtures/ResourceCurrent/ResourceCurrentPlayerProbe.cs')) {
                Assert-That (@($selected.inputs) -cnotcontains $source) "Author asset/Player acceptance remains a separate native selection: $source"
            }
            Assert-That (@($selected.inputs | Where-Object { $_ -cmatch '/(?:ArtifactStoreQueryTests|ResourceContent(?:Update|Build)?Tests|ResourceSignatureTests|ResourceDownloadTests|ResourceUpdateTests)(?:[./]|$)' }).Count -eq 0) 'The ResourceUpdates host must not compile retired private tests.'
            $definition = @($configuration.modules | Where-Object name -CEQ 'ResourceCurrentPublication')[0]
            Assert-That ($definition.specialBackend -ceq 'SessionNative' -and $definition.specialCategory -ceq 'YL.Special.ResourceCurrent' -and
                $definition.nativeLayout -ceq 'Framework' -and ($definition.packageInputs -join ',') -ceq 'com.kano.ylframework') 'Current Resource authors and actual Players require their own main-only native selection.'
            $selected = Get-SelectedSources -Native -EditorProfile $profile
            $current = 'Assets/Tests/YLFramework/EditMode/Editor/Resource/ResourceCurrentPublicationTests.cs'
            Assert-That (($selected.tests -join ',') -ceq $current -and [IO.File]::ReadAllText((Join-Path $repository $current)).Contains('[Category("YL.Special.ResourceCurrent")]')) 'The special cohort must actually execute the current Player test.'
            foreach ($source in @($current, $ownership,
                'Assets/Tests/YLFramework/Fixtures/Shared/Editor/StandaloneFixturePlayer.cs',
                'Assets/Tests/YLFramework/Fixtures/ResourceCurrent/ResourceCurrentPlayerProbe.cs',
                'Assets/Tests/YLFramework/Fixtures/ResourceCurrent/YLFramework.Tests.ResourceCurrent.Player.asmdef')) {
                Assert-That (@($selected.inputs) -ccontains $source -and @($selected.inputs) -ccontains ($source + '.meta')) "Actual current author/Player source identity is missing: $source"
            }
            Assert-That (@($selected.inputs | Where-Object { $_.StartsWith('Assets/Tests/YLFramework/Fixtures/', [StringComparison]::Ordinal) -and $_ -cmatch '\.(png|jpg|tga|spriteatlasv2?)$' }).Count -eq 0 -and
                @($selected.inputs | Where-Object { $_ -cmatch '/(NamespaceMigration|ResourceV1~)/' }).Count -eq 0) 'The current producer cannot import historical textures or frozen Resource migration originals.'
            Assert-That (@($definition.nativeFixtureInputs | Where-Object { $_ -cnotin @('Assets/Tests/YLFramework/Fixtures/Shared', 'Assets/Tests/YLFramework/Fixtures/ResourceCurrent') }).Count -eq 0) 'Current Player acceptance must not copy or write frozen migration originals.'
            $probe = Read-YLPackageEvidenceJson (Join-Path $repository 'Assets/Tests/YLFramework/Fixtures/ResourceCurrent/YLFramework.Tests.ResourceCurrent.Player.asmdef')
            Assert-ResourceCurrentPlayerIsolation $probe
        }
        $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
        $resourceProbe = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Assets/Tests/YLFramework/Fixtures/ResourceCurrent/YLFramework.Tests.ResourceCurrent.Player.asmdef')
        foreach ($mutation in @(
            @{ label = 'unconditional'; key = 'defineConstraints'; value = @() },
            @{ label = 'Editor-only'; key = 'defineConstraints'; value = @('UNITY_EDITOR') },
            @{ label = 'acceptance-only'; key = 'defineConstraints'; value = @('YL_RESOURCE_CURRENT_ACCEPTANCE') },
            @{ label = 'test-only'; key = 'defineConstraints'; value = @('UNITY_INCLUDE_TESTS') },
            @{ label = 'Editor-or-tests'; key = 'defineConstraints'; value = @('UNITY_EDITOR || UNITY_INCLUDE_TESTS') },
            @{ label = 'wrong-symbol'; key = 'defineConstraints'; value = @('UNITY_EDITOR || YL_OTHER_ACCEPTANCE') },
            @{ label = 'extra-test-alternative'; key = 'defineConstraints'; value = @('UNITY_EDITOR || YL_RESOURCE_CURRENT_ACCEPTANCE || UNITY_INCLUDE_TESTS') },
            @{ label = 'ordinary-Player-alternative'; key = 'defineConstraints'; value = @('UNITY_EDITOR || YL_RESOURCE_CURRENT_ACCEPTANCE || !UNITY_EDITOR') },
            @{ label = 'additional-test-condition'; key = 'defineConstraints'; value = @('UNITY_EDITOR || YL_RESOURCE_CURRENT_ACCEPTANCE', 'UNITY_INCLUDE_TESTS') },
            @{ label = 'NUnit-reference'; key = 'references'; value = @('YLFramework.Runtime', 'nunit.framework') },
            @{ label = 'TestRunner-reference'; key = 'references'; value = @('YLFramework.Runtime', 'UnityEngine.TestRunner') },
            @{ label = 'Editor-reference'; key = 'references'; value = @('YLFramework.Runtime', 'UnityEditor.CoreModule') },
            @{ label = 'NUnit-plugin'; key = 'precompiledReferences'; value = @('nunit.framework.dll') },
            @{ label = 'implicit-test-assembly'; key = 'optionalUnityReferences'; value = @('TestAssemblies') },
            @{ label = 'Editor-platform'; key = 'includePlatforms'; value = @('Editor') },
            @{ label = 'excluded-Player'; key = 'excludePlatforms'; value = @('WindowsStandalone64') },
            @{ label = 'automatic-reference'; key = 'autoReferenced'; value = $true },
            @{ label = 'implicit-acceptance-symbol'; key = 'versionDefines'; value = @(@{ name = 'com.unity.modules.jsonserialize'; expression = '0.0.0'; define = 'YL_RESOURCE_CURRENT_ACCEPTANCE' }) }
        )) {
            Test-Case ("ResourceCurrent Player isolation rejects " + $mutation.label) {
                $invalid = $resourceProbe | ConvertTo-Json -Depth 10 | ConvertFrom-Json -AsHashtable -DateKind String
                $invalid[$mutation.key] = $mutation.value
                Assert-Rejected { Assert-ResourceCurrentPlayerIsolation $invalid } 'ResourceCurrent Player isolation'
            }
        }
        Test-Case 'AudioNative preserves original audible bundle fixtures and current saved Player inputs with exact ownership and metadata' {
            $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = [IO.File]::ReadAllText((Join-Path $repository 'Tools/Testing/TestSuites.json')) | ConvertFrom-Json -DateKind String
            $modules = @($configuration.modules | Where-Object name -CEQ 'AudioNative')
            Assert-That ($modules.Count -eq 1) 'The original AudioNative fixture has no explicit executable selector.'
            $definition = $modules[0]
            Assert-That (($definition.suites -join ',') -ceq 'Special' -and $definition.specialBackend -ceq 'SessionNative' -and
                ($definition.specialModes -join ',') -ceq 'EditMode' -and $definition.specialCategory -ceq 'YL.Special.AudioNative' -and
                @($definition.assemblies).Count -eq 0 -and $definition.category -ceq '') 'Audible native tests cannot be substituted by Daily or AudioSession.'
            Assert-That ($definition.nativeLayout -ceq 'Framework' -and $definition.graphics -and $definition.shortHostKey -and
                $definition.prepareSceneTemplates -and $definition.prepareSessionScene) 'The native audio fixture requires graphics and its saved owned scene.'
            $closure = @(Get-YLUpmPackages -Repository $repository -Ids @($definition.packageInputs))
            Assert-That (($closure.id -join ',') -ceq 'com.kano.ylframework') 'AudioNative may only consume its main package and current Resource authority.'
            $source = 'Assets/Tests/YLFramework/EditMode/Systems/Audio/AudioResourceBundleTests.cs'
            $current = 'Assets/Tests/YLFramework/EditMode/Systems/Audio/AudioAcceptanceResourceTests.cs'
            Assert-That (($definition.editTests -join ',') -ceq "$source,$current" -and @($definition.playTests).Count -eq 0) 'The native fixture and formal current Player must remain explicit, separately selected inputs.'
            Assert-That ([IO.File]::ReadAllText((Join-Path $repository $source)).Contains('[Category("YL.Special.AudioNative")]')) 'The native category must select real existing tests.'
            $tokens = $null; $errors = $null
            $syntax = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repository 'Tools/Testing/Invoke-YLTests.ps1'), [ref]$tokens, [ref]$errors)
            Assert-That ($errors.Count -eq 0) 'The maintained entry does not parse.'
            $function = $syntax.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Get-SelectedSources' }, $true)
            . ([scriptblock]::Create($function.Extent.Text))
            $profile = @($configuration.editors | Where-Object version -CEQ '6000.3.27f1')[0]
            $selected = Get-SelectedSources -Native -EditorProfile $profile
            Assert-That (@($selected.tests).Count -eq 2 -and @($selected.tests) -ccontains $source -and @($selected.tests) -ccontains $current) 'The actual native selector must execute the original fixture and the current saved input.'
            foreach ($relative in @($source, $current, 'Assets/Tests/YLFramework/Acceptance/Core/AudioAcceptanceEntry.cs', 'Assets/Tests/YLFramework/EditMode/Systems/Audio/AudioCoreTests.cs',
                'Assets/Tests/YLFramework/EditMode/Systems/Resource/ResourceAddressablesOwnershipTests.cs',
                'Assets/Tests/YLFramework/Fixtures/Shared/Editor/StandaloneFixturePlayer.cs',
                'Assets/Tests/YLFramework/EditMode/YLFramework.Tests.EditMode.asmdef')) {
                Assert-That (@($selected.inputs) -ccontains $relative -and @($selected.inputs) -ccontains ($relative + '.meta')) "The native closure lost source or independent metadata: $relative"
            }
            $caller = [IO.File]::ReadAllText((Join-Path $repository $current))
            Assert-That ($caller.Contains('extraScriptingDefines: new[] { "YL_UI_ACCEPTANCE" }') -and $caller.Contains('graphics: true')) 'Real audible Players must compile their opt-in probe and use graphics.'
            $acceptance = Read-YLPackageEvidenceJson (Join-Path $repository 'Assets/Tests/YLFramework/Acceptance/Core/YLFramework.UI.Acceptance.asmdef')
            Assert-That (($acceptance.defineConstraints -join ',') -ceq 'UNITY_EDITOR || YL_UI_ACCEPTANCE' -and -not $acceptance.autoReferenced) 'The Audio probe still needs to stay outside ordinary Players.'
        }
        Test-Case 'Historical snapshot producers are not current test selectors or export commands' {
            $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Tools/Testing/TestSuites.json')
            Assert-That (@($configuration.modules | Where-Object name -CEQ 'MigrationFixtures').Count -eq 0) 'A retired pre-split producer cannot be a current execution target.'
            foreach ($relative in @('Assets/Tests/YLFramework/EditMode/Editor/MigrationFixtures/AssemblyMigrationFixtureFreezeTests.cs',
                'Assets/Tests/YLFramework/EditMode/Editor/MigrationFixtures/YLFramework.MigrationFixtures.Tests.EditMode.asmdef',
                'Tools/Testing/Tests/Export-YLAssemblyMigrationFixtures.ps1')) {
                Assert-That (-not [IO.File]::Exists((Join-Path $repoRoot $relative))) "A historical writer must not remain as an unselected current entry: $relative"
            }
        }
        Test-Case 'Quick excludes the disabled adapter from compilation but binds its complete product input' {
            $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $tokens = $null; $errors = $null
            $syntax = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repository 'Tools/Testing/Invoke-YLTests.ps1'), [ref]$tokens, [ref]$errors)
            Assert-That ($errors.Count -eq 0) 'The maintained entry does not parse.'
            $function = $syntax.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Get-SelectedSources' }, $true)
            . ([scriptblock]::Create($function.Extent.Text))
            $configuration = [IO.File]::ReadAllText((Join-Path $repository 'Tools/Testing/TestSuites.json')) | ConvertFrom-Json -DateKind String
            $adapter = 'PackageSources/com.kano.ylframework/Runtime/Systems/Action/Integration/DOTween/DOTweenAction.cs'
            foreach ($module in @('Domain', 'Behavior')) {
                $definition = @($configuration.modules | Where-Object name -CEQ $module)[0]
                $selected = Get-SelectedSources
                Assert-That ($selected.runtime -cnotcontains $adapter) "$module Quick unexpectedly compiles a disabled external-SDK adapter."
                Assert-That ($selected.inputs -ccontains $adapter -and $selected.inputs -ccontains ($adapter + '.meta')) "$module must still hash the full product input."
                $saveAdapter = 'PackageSources/com.kano.ylframework.reddot/Runtime/Integration/Save/ReddotSaveExtensions.cs'
                Assert-That ($selected.runtime -cnotcontains $saveAdapter) "$module Quick unexpectedly compiles the conditional Save composition."
                if ($module -ceq 'Behavior') { Assert-That ($selected.inputs -ccontains $saveAdapter -and $selected.inputs -ccontains ($saveAdapter + '.meta')) "$module must still hash the full optional composition payload." }
            }
        }
        Test-Case 'ReddotSave has one explicit native selector and keeps Save out of core' {
            $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Tools/Testing/TestSuites.json')
            $module = @($configuration.modules | Where-Object name -CEQ 'ReddotSave')[0]
            Assert-That ((-not [string]::IsNullOrWhiteSpace($module.category)) -xor ($module.assemblies.Count -gt 0)) 'Native selection must choose a category OR explicit assemblies, never both.'
            Assert-That (($module.modes -join ',') -ceq 'EditMode') 'Save snapshot IO belongs to actual EditMode acceptance.'
            Assert-That ($module.nativeSources -ccontains 'Assets/Tests/YLFramework/Fixtures/Shared') 'Framework Native entry requires the real shared PackageTestPaths support.'
            $core = @($configuration.modules | Where-Object name -CEQ 'Reddot')[0]
            Assert-That (($core.packageInputs -join ',') -ceq 'com.kano.ylframework.reddot') 'The core must not force-install Save.'
        }
        Test-Case 'DOTween behavior has its own SDK-required module without adding SDK input to ordinary Action' {
            $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Tools/Testing/TestSuites.json')
            $modules = @($configuration.modules | Where-Object name -CEQ 'DOTween')
            Assert-That ($modules.Count -eq 1) 'The original DOTween behavior tests have no native automation entry.'
            $module = $modules[0]
            Assert-That ($module.nativeSdk -ceq 'DOTween' -and ($module.packageInputs -join ',') -ceq 'com.kano.ylframework') 'The SDK module must install the main product, not the retired adapter package.'
            Assert-That (($module.assemblies -join ',') -ceq 'YLFramework.Actions.DOTween.Tests.EditMode,YLFramework.Actions.DOTween.Tests.PlayMode,YLFramework.Tests.EditMode') 'Both original test assemblies and the original Help XML consumer assembly must be selected.'
            $action = @($configuration.modules | Where-Object name -CEQ 'Action')[0]
            Assert-That (-not $action.Contains('nativeSdk')) 'Ordinary Action must remain independent of SDK preparation.'
            foreach ($side in @('EditMode', 'PlayMode')) {
                $assembly = Read-YLPackageEvidenceJson (Join-Path $repoRoot "Assets/Tests/YLFramework/Integrations/DOTween/$side/YLFramework.Actions.DOTween.Tests.$side.asmdef")
                Assert-That (($assembly.defineConstraints -join ',') -ceq 'DOTWEEN,UNITY_INCLUDE_TESTS' -and $assembly.versionDefines.Count -eq 0) 'The original tests must not silently disappear behind a retired package presence gate.'
            }
        }
        Test-Case 'Daily selectors have one owner while distinct session and package evidence remains explicit' {
            $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Tools/Testing/TestSuites.json')
            Assert-That (($configuration.suites.name -join ',') -ceq 'Quick,Daily,Special') 'The empty Integration suite must not remain an executable contract.'
            Assert-That (@($configuration.modules | Where-Object { $_.name -cin @('ActionHelp', 'Logging') }).Count -eq 0) 'Help aliases and standalone Logging must not duplicate their current owners.'
            $entry = Get-Command (Join-Path $repoRoot 'Tools/Testing/Invoke-YLTests.ps1')
            $suiteValues = @($entry.Parameters['Suite'].Attributes | Where-Object { $_ -is [Management.Automation.ValidateSetAttribute] })[0].ValidValues
            Assert-That (($suiteValues -join ',') -ceq 'Quick,Daily,Special') 'The public Suite ValidateSet differs from the executable suite registry.'
            foreach ($path in @('Tools/Testing/Tests/Test-YLIntegrationPackages.ps1', 'Tools/Testing/Tests/Test-YLIntegrationPackagesNoUnity.ps1', 'Tools/Testing/Tests/Test-YLDistribution.ps1')) {
                Assert-That ([IO.File]::Exists((Join-Path $repoRoot $path))) "Removing an empty suite cannot retire a real tool: $path"
            }
            $gameplay = @($configuration.modules | Where-Object name -CEQ 'Gameplay')[0]
            foreach ($path in @('Assets/Tests/YLFramework/EditMode/Editor/Domain', 'Assets/Tests/YLFramework/EditMode/Editor/Equipment')) {
                Assert-That (@($gameplay.editTests) -ccontains $path) "Gameplay lost a real authoring gate: $path"
            }
            $equipment = @($configuration.modules | Where-Object name -CEQ 'Equipment')[0]
            $domain = @($configuration.modules | Where-Object name -CEQ 'Domain')[0]
            Assert-That (($equipment.suites -join ',') -ceq 'Special' -and $equipment.specialCategory -ceq 'YL.Special.EquipmentSession') 'Equipment must retain its own session/admission evidence, not another Daily gameplay copy.'
            Assert-That (($domain.suites -join ',') -ceq 'Quick,Special' -and $domain.specialCategory -ceq 'YL.Special.Session') 'Domain must retain Quick and real combination sessions without repeating Gameplay Daily.'
            $domainTests = 'Assets/Tests/YLFramework/PlayMode/Systems/Domain'
            $crossPackage = "$domainTests/AddressablesConsumerMigrationTests.cs"
            foreach ($independent in @($gameplay, $domain)) {
                Assert-That ($independent.playTests -cnotcontains $domainTests -and $independent.playTests -cnotcontains $crossPackage -and
                    $independent.playTests -ccontains "$domainTests/DomainHostSceneTests.cs" -and
                    $independent.playTests -ccontains "$domainTests/DomainResourceLeaseTests.cs" -and
                    $independent.playTests -ccontains "$domainTests/DomainReviewExitTests.cs" -and
                    $independent.playTests -ccontains "$domainTests/AddressablesDomainResourceFixture.cs") 'Gameplay-only hosts must keep their tests without compiling the DataAssets combination.'
            }
            $combined = @($configuration.modules | Where-Object name -CEQ 'ResourceAddressablesConsumers')[0]
            Assert-That ($combined.playTests -ccontains $crossPackage -and $combined.packageInputs -ccontains 'com.kano.ylframework.data-assets') 'The DataAssets+Gameplay test belongs to the explicit combined host.'
            Assert-That (@($equipment.editTests) -ccontains 'Assets/Tests/YLFramework/EditMode/Editor/Equipment/EquipmentSettingsAdmissionTests.cs') 'The unique settings admission gate disappeared during the Daily move.'
            $tools = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Assets/Tests/YLFramework/Fixtures/Authoring/YLFramework.Tests.Tools.Editor.asmdef')
            Assert-That (($tools.references -join ',') -ceq 'YLFramework.Runtime,YLFramework.Editor,YLFramework.Equipment,YLFramework.Equipment.Acceptance.Player,YLFramework.Tests.Fixtures,Unity.Addressables,Unity.Addressables.Editor') 'Authoring tools must reference retained consumers and the two official Addressables assemblies, without forcing DataAssets, Localization or Save.'
            $authoring = [IO.File]::ReadAllText((Join-Path $repoRoot 'Assets/Tests/YLFramework/EditMode/Editor/Domain/DomainAuthoringTests.cs'))
            Assert-That (-not $authoring.Contains('YLFramework.DataAssets.')) 'Pure Gameplay authoring still directly consumes optional DataAssets.'
            $dataAuthoring = [IO.File]::ReadAllText((Join-Path $repoRoot 'Assets/Tests/YLFramework/EditMode/Editor/DataAssets/DataAssetAuthoringTests.cs'))
            Assert-That ($dataAuthoring.Contains('CurrentDataRootAndRowColdReadKeepsExplicitValuesListsParentAndScriptBindings') -and $dataAuthoring.Contains('[TestCase(0, "", "")]')) 'Moving pure Data authorship must keep current root/row cold-read and explicit zero/empty values.'
            $help = @($configuration.modules | Where-Object name -CEQ 'Help')[0]
            $dotween = @($configuration.modules | Where-Object name -CEQ 'DOTween')[0]
            Assert-That (($help.editTests -join ',') -ceq 'Assets/Tests/YLFramework/EditMode/Editor/Help' -and @($dotween.editTests) -ccontains 'Assets/Tests/YLFramework/EditMode/Editor/Help/ApiHelpTests.cs') 'Removing ActionHelp cannot remove the full Help or actual SDK XML consumer.'
        }
        Test-Case 'Foundation owns native Logging while LoggingOnly retains its separate source isolation proof' {
            $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Tools/Testing/TestSuites.json')
            $foundation = @($configuration.modules | Where-Object name -CEQ 'Foundation')[0]
            Assert-That (@($foundation.editTests) -ccontains 'Assets/Tests/YLFramework/EditMode/Systems/Logging' -and @($foundation.playTests) -ccontains 'Assets/Tests/YLFramework/PlayMode/Systems/Logging' -and @($foundation.assemblies) -ccontains 'YLFramework.Tests.PlayMode') 'Logging EditMode and actual lifecycle leaves must execute in Foundation, not only compile.'
            $closure = @(Get-YLUpmPackages -Repository $repoRoot -Ids @($foundation.packageInputs))
            Assert-That (($closure.id -join ',') -ceq 'com.kano.ylframework') 'Ordinary Logging cannot force an optional product.'
            $only = @($configuration.modules | Where-Object name -CEQ 'LoggingOnly')[0]
            Assert-That (($only.suites -join ',') -ceq 'Special' -and $only.nativeLayout -ceq 'Slice' -and $only.specialCategory -ceq 'YL.Special.LoggingOnly' -and @($only.packageInputs).Count -eq 0) 'Source-slice sessions are not equivalent to Foundation package execution.'
            Assert-That (($only.runtimeSources -join ',') -ceq 'PackageSources/com.kano.ylframework/Runtime/Foundation/Singleton/Internal/SingletonSessionState.cs,PackageSources/com.kano.ylframework/Runtime/Systems/Logging') 'LoggingOnly acquired unrelated lifecycle owners.'
        }
        Test-Case 'Data Daily selects real composition without executing independent package directories' {
            $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = [IO.File]::ReadAllText((Join-Path $repository 'Tools/Testing/TestSuites.json')) | ConvertFrom-Json -DateKind String
            $definition = @($configuration.modules | Where-Object name -CEQ 'Data')[0]
            Assert-That ($definition.category -ceq 'YL.Module.Data' -and @($definition.assemblies).Count -eq 0 -and
                'nativeAssemblies' -cnotin $definition.PSObject.Properties.Name -and ($definition.modes -join ',') -ceq 'EditMode') 'Data Daily must keep its positive category after native request normalization, not replace it with an empty assembly selection.'
            Assert-That (($definition.editTests -join ',') -ceq 'Assets/Tests/YLFramework/EditMode/Editor/Data' -and @($definition.playTests).Count -eq 0) 'Independent Save, DataAssets and Localization directories still execute again in Data Daily.'
            $tokens = $null; $errors = $null
            $syntax = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repository 'Tools/Testing/Invoke-YLTests.ps1'), [ref]$tokens, [ref]$errors)
            Assert-That ($errors.Count -eq 0) 'The maintained entry does not parse.'
            $function = $syntax.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Get-SelectedSources' }, $true)
            . ([scriptblock]::Create($function.Extent.Text))
            $selected = Get-SelectedSources -Native -EditorProfile @($configuration.editors | Where-Object version -CEQ '6000.3.27f1')[0]
            foreach ($file in @('DataSourceOwnershipTests.cs', 'SerializedDictionarySaveTests.cs', 'SaveManagerFacadeTests.cs')) {
                $path = 'Assets/Tests/YLFramework/EditMode/Editor/Data/' + $file
                Assert-That (@($selected.tests) -ccontains $path -and [IO.File]::ReadAllText((Join-Path $repository $path)).Contains('[Category("YL.Module.Data")]')) "A current composition consumer silently disappeared: $path"
            }
            $combined = 'Assets/Tests/YLFramework/EditMode/Editor/Data/DataDomainReloadSessionTests.cs'
            Assert-That ($definition.specialCategory -ceq 'YL.Special.DataComposition' -and $definition.disableDomainReload -and
                $combined -cin $selected.inputs -and [IO.File]::ReadAllText((Join-Path $repository $combined)).Contains('[Category("YL.Special.DataComposition")]')) 'Data Special must select the real combined two-Play case, not standalone publication in a wider package closure.'
            $standaloneTests = [ordered]@{ DataAssets = 'DataAssetSessionAndPublicationTests.cs'; Localization = 'LocalizationSessionAndPublicationTests.cs' }
            foreach ($feature in $standaloneTests.Keys) {
                $standalone = @($configuration.modules | Where-Object name -CEQ $feature)[0]
                $source = "Assets/Tests/YLFramework/EditMode/Editor/$feature/$($standaloneTests[$feature])"
                Assert-That ($source -cin $selected.inputs -and $standalone.specialCategory -ceq 'YL.Special.Session' -and
                    ("Assets/Tests/YLFramework/EditMode/Editor/$feature" -cin $standalone.editTests) -and
                    [IO.File]::ReadAllText((Join-Path $repository $source)).Contains('[Category("YL.Special.Session")]')) "The isolated $feature publication and session must still be runnable in their own native selector."
            }
            Assert-That (@($selected.inputs | Where-Object { $_.StartsWith('Assets/Tests/YLFramework/Fixtures/Authoring/', [StringComparison]::Ordinal) }).Count -eq 0) 'Current Data must not acquire optional Equipment tools or the historical Data player just to audit their source.'
            $composition = [IO.File]::ReadAllText((Join-Path $repository 'Assets/Tests/YLFramework/EditMode/Editor/Data/DataSourceOwnershipTests.cs'))
            Assert-That ($composition.Contains('AuthoredDataAndLanguageColdReadFeedOneExplicitStoreDictionary')) 'The current Data/Language author-to-Save consumer is missing.'
        }
        Test-Case 'Behavior keeps Quick mathematics and authored combinations without repeating standalone native directories' {
            $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = [IO.File]::ReadAllText((Join-Path $repository 'Tools/Testing/TestSuites.json')) | ConvertFrom-Json -DateKind String
            $definition = @($configuration.modules | Where-Object name -CEQ 'Behavior')[0]
            Assert-That ('modes' -cin $definition.PSObject.Properties.Name -and ($definition.modes -join ',') -ceq 'EditMode' -and @($definition.playTests).Count -eq 0 -and ($definition.nativeAssemblies -join ',') -ceq 'YLFramework.Tests.EditMode') 'Behavior Daily must execute authored and dual-FSM combinations, not standalone physics suites.'
            $quickOnly = @('Assets/Tests/YLFramework/EditMode/Utilities/CharacterController2D/CharacterMotorOfflineTests.cs', 'Assets/Tests/YLFramework/EditMode/Utilities/MoveablePlatform/MovingPlatformOfflineTests.cs')
            foreach ($source in $quickOnly) { Assert-That (@($definition.testSupport) -ccontains $source) "Quick lost its distinct managed mathematics evidence: $source" }
            $tokens = $null; $errors = $null
            $syntax = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repository 'Tools/Testing/Invoke-YLTests.ps1'), [ref]$tokens, [ref]$errors)
            Assert-That ($errors.Count -eq 0) 'The maintained entry does not parse.'
            $function = $syntax.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Get-SelectedSources' }, $true)
            . ([scriptblock]::Create($function.Extent.Text))
            $quick = Get-SelectedSources
            foreach ($source in $quickOnly) { Assert-That (@($quick.tests) -ccontains $source) "Actual Quick compilation lost its test support: $source" }
            $native = Get-SelectedSources -Native -EditorProfile @($configuration.editors | Where-Object version -CEQ '6000.3.27f1')[0]
            foreach ($source in $quickOnly) { Assert-That (@($native.tests) -cnotcontains $source -and @($native.inputs) -cnotcontains $source) "Quick mathematics still repeats in native Behavior: $source" }
            Assert-That (@($native.tests) -ccontains 'Assets/Tests/YLFramework/EditMode/Systems/Behavior/DualFsmLegacyTests.cs' -and @($native.inputs) -ccontains 'Assets/Tests/YLFramework/EditMode/Editor/Behavior/BehaviorAuthoringTests.cs') 'The real dual-FSM or authored consumer was discarded.'
            $player = @($configuration.modules | Where-Object name -CEQ 'BehaviorPlayer')[0]
            Assert-That (($player.suites -join ',') -ceq 'Special' -and $player.specialBackend -ceq 'PlayerNative' -and $definition.specialCategory -ceq 'YL.Special.NativeInput' -and $definition.interactiveDesktop) 'Daily deduplication cannot replace the independent Player or native input contracts.'
        }
        Test-Case 'Domain declares its actual Gameplay and Reddot combination without obsolete Data probe dependencies' {
            $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Tools/Testing/TestSuites.json')
            $domain = @($configuration.modules | Where-Object name -CEQ 'Domain')[0]
            $closure = @(Get-YLUpmPackages -Repository $repoRoot -Ids @($domain.packageInputs))
            Assert-That (($closure.id -join ',') -ceq 'com.kano.ylframework,com.kano.ylframework.gameplay,com.kano.ylframework.reddot') 'Domain must not install DataAssets, Localization, Excel or Save for retired fixture references.'
            foreach ($name in @('Core', 'Foundation', 'Localization', 'DataAssets')) {
                $module = @($configuration.modules | Where-Object name -CEQ $name)[0]
                Assert-That (@($module.packageInputs) -cnotcontains 'com.kano.ylframework.save') "$name must not acquire Save to repair a test identity anchor."
            }
        }
        Test-Case 'Save-only executes its standalone Player while the Data combination explicitly excludes it' {
            $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Tools/Testing/TestSuites.json')
            $save = @($configuration.modules | Where-Object name -CEQ 'Save')[0]
            $data = @($configuration.modules | Where-Object name -CEQ 'Data')[0]
            Assert-That (@($save.packageInputs).Count -eq 1 -and $save.packageInputs[0] -ceq 'com.kano.ylframework.save') 'Standalone Save must install only its own product closure.'
            Assert-That (@($save.nativeSources) -ccontains 'Assets/Tests/YLFramework/EditMode/Systems/Save/SaveStandalonePlayerTests.cs') 'Save-only lost its actual Player test input.'
            Assert-That (@($save.excludedCategories) -cnotcontains 'YL.Package.SaveStandalone') 'Save-only must execute the no-main Player case.'
            Assert-That (@($data.excludedCategories) -ccontains 'YL.Package.SaveStandalone') 'Data installs main, so its selection must exclude the no-main-only Player case.'
            $excludedBy = @($configuration.modules | Where-Object { $_.Contains('excludedCategories') -and @($_.excludedCategories) -ccontains 'YL.Package.SaveStandalone' })
            Assert-That ($excludedBy.Count -eq 1 -and $excludedBy[0].name -ceq 'Data') 'The standalone exclusion was applied to the wrong module.'
        }
        Test-Case 'Native test references follow the declared package closure without installing unrelated features' {
            $catalog = Read-YLPackageCatalog
            $assembly = @{ name = 'YLFramework.Tests.EditMode'; references = @('YLFramework.Runtime', 'YLFramework.DataAssets', 'YLFramework.DataAssets.Editor', 'YLFramework.Tests.Fixtures', 'UnityEngine.TestRunner') }
            $minimal = Get-YLNativeTestAssembly -Assembly $assembly -PackageIds @('com.kano.ylframework') -Catalog $catalog
            Assert-That (($minimal.references -join ',') -ceq 'YLFramework.Runtime,YLFramework.Tests.Fixtures,UnityEngine.TestRunner') 'A main-only test host silently acquired an optional product dependency.'
            $data = Get-YLNativeTestAssembly -Assembly $assembly -PackageIds @('com.kano.ylframework', 'com.kano.ylframework.data-assets') -Catalog $catalog
            Assert-That (($data.references -join ',') -ceq ($assembly.references -join ',')) 'The explicitly installed feature reference was removed.'
            Assert-That ($assembly.references.Count -eq 5) 'Project test metadata was mutated instead of projecting the owned host input.'
            Assert-Rejected { Get-YLNativeTestAssembly -Assembly @{ name = 'YLFramework.Runtime'; references = @('YLFramework.DataAssets') } -PackageIds @('com.kano.ylframework') -Catalog $catalog } 'test|product'
            Assert-Rejected { Get-YLNativeTestAssembly -Assembly $assembly -PackageIds @('com.kano.ylframework.unknown') -Catalog $catalog } 'Unknown|package'
        }
        Test-Case 'Unity test assets do not import external tool scripts' {
            $sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $external = @(Get-YLTestTreeFiles (Join-Path $sourceRoot 'Assets/Tests/YLFramework') | Where-Object {
                $_.EndsWith('.ps1', [StringComparison]::OrdinalIgnoreCase) -or
                $_.EndsWith('.cjs', [StringComparison]::OrdinalIgnoreCase)
            })
            Assert-That ($external.Count -eq 0) "External tools belong under Tools, not Assets: $($external.Count) files; $(if ($external.Count) { $external[0] })"
        }
        Test-Case 'Every declared module source still resolves after repository reorganization' {
            $sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = [IO.File]::ReadAllText((Join-Path $sourceRoot 'Tools/Testing/TestSuites.json')) | ConvertFrom-Json -DateKind String
            foreach ($module in $configuration.modules) {
                foreach ($field in @('runtimeSources', 'editTests', 'playTests', 'nativeSources', 'assemblyMetadata', 'testSupport')) {
                    if ($field -cnotin $module.PSObject.Properties.Name) { continue }
                    foreach ($relative in $module.$field) {
                        $path = Get-YLTestPath (Join-Path $sourceRoot $relative)
                        Assert-That ([IO.Path]::Exists($path)) "Missing $($module.Name).${field}: $relative"
                        if ($relative.StartsWith('Assets/', [StringComparison]::Ordinal)) {
                            Assert-That ([IO.File]::Exists($path + '.meta')) "Missing participating metadata: $relative.meta"
                        }
                    }
                }
            }
        }
        Test-Case 'Editor selection binds exact product version and revision rather than the shared file prefix' {
            $editors = @(
                [pscustomobject]@{ fileVersion = '2022.3.62'; productVersion = '2022.3.62f3c1_1623fc0bbb97'; version = '2022.3.62f3c1' },
                [pscustomobject]@{ fileVersion = '2022.3.62'; productVersion = '2022.3.62f3_96770f904ca7'; version = '2022.3.62f3' }
            )
            $info = [pscustomobject]@{ FileMajorPart = 2022; FileMinorPart = 3; FileBuildPart = 62; ProductVersion = '2022.3.62f3_96770f904ca7' }
            $selected = Get-YLTestEditorProfile -Editors $editors -VersionInfo $info
            Assert-That ($selected.version -ceq '2022.3.62f3') 'The official Editor was mislabeled as the regional build.'
            $info.ProductVersion = '2022.3.62f3c1_1623fc0bbb97'
            $selected = Get-YLTestEditorProfile -Editors $editors -VersionInfo $info
            Assert-That ($selected.version -ceq '2022.3.62f3c1') 'The existing regional profile was lost.'
            $info.ProductVersion = '2022.3.62f3_unknown'
            Assert-Rejected { Get-YLTestEditorProfile -Editors $editors -VersionInfo $info } 'Unsupported|ambiguous'
            $info.ProductVersion = '2022.3.62f3_96770f904ca7'
            Assert-Rejected { Get-YLTestEditorProfile -Editors ($editors + $editors[1]) -VersionInfo $info } 'Unsupported|ambiguous'
        }
        Test-Case 'Resource Addressables Player rejects invalid entry requests before creating a workspace' {
            $entry = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../Invoke-YLResourceAddressablesPlayer.ps1'))
            Assert-That ([IO.File]::Exists($entry)) 'The maintained Resource Addressables Player entry is missing.'
            $root = Join-Path $sandbox 'Resource Player [invalid]'
            $fakeUnity = Join-Path $sandbox 'Unity.exe'
            [IO.File]::WriteAllBytes($fakeUnity, [IO.File]::ReadAllBytes((Join-Path $PSHOME 'pwsh.exe')))
            $options = @{ Unity = $fakeUnity; Profile = 'Unity6'; WorkspaceRoot = $root }
            Assert-Rejected { & $entry @options -ScopeCloseBenchmark } 'Scope close measurement requires .*Release.*Graphics'
            Assert-Rejected { & $entry @options -ScopeCloseBenchmark -BuildFlavor Development -Graphics } 'Scope close measurement requires .*Release.*Graphics'
            Assert-Rejected { & $entry @options } 'Unsupported or ambiguous Unity build'
            Assert-That (-not [IO.Path]::Exists($root)) 'Invalid Resource Addressables Player requests must not write test outputs.'
        }
        Test-Case 'Player profile dependencies must come from the fingerprinted JSON bytes' {
            . (Join-Path $common 'Packages.ps1')
            $lockPath = Join-Path $sandbox 'player-profile-lock.json'
            [IO.File]::WriteAllText($lockPath, '{"dependencies":{"pin":"1.0.0"}}')
            $hash = (Get-FileHash -LiteralPath $lockPath -Algorithm SHA256).Hash
            Assert-That ((Read-YLPackageEvidenceJson $lockPath -ExpectedSha256 $hash).dependencies.pin -ceq '1.0.0') 'The frozen pin was not read.'
            [IO.File]::WriteAllText($lockPath, '{"dependencies":{"pin":"2.0.0"}}')
            Assert-Rejected { Read-YLPackageEvidenceJson $lockPath -ExpectedSha256 $hash } 'SHA-256 mismatch'
        }
        Test-Case 'workspace initialization is explicit, reusable and preserves repository identity' {
            $unicode = [string][char]0x6D4B + [char]0x8BD5
            $root = Join-Path $sandbox "Workspace with spaces [$unicode]"
            $first = Initialize-YLTestWorkspace -WorkspaceRoot $root -Repository $repository
            $again = Initialize-YLTestWorkspace -WorkspaceRoot $root -Repository $repository
            Assert-That ($first.id -ceq $again.id) 'A compatible workspace must retain its identity.'
            Assert-That ($again.createdUtc -is [string] -and $again.createdUtc -ceq $first.createdUtc) 'Durable workspace timestamps must retain their wire format.'
            Assert-That ($first.root -ceq $root -and $first.repository -ceq $repository) 'Explicit paths must round-trip.'
            $marker = [IO.File]::ReadAllText((Join-Path $root '.yl-workspace.json')) | ConvertFrom-Json
            Assert-That ($marker.purpose -ceq 'YLTesting.Workspace' -and $marker.schemaVersion -eq 1) 'The workspace must have a durable owner.'
            Assert-Rejected { Initialize-YLTestWorkspace -WorkspaceRoot $root -Repository $sandbox } 'identity|repository'
        }

        Test-Case 'unknown directories and source roots are never adopted' {
            $unknown = Join-Path $sandbox 'Unknown'
            $null = [IO.Directory]::CreateDirectory($unknown)
            $sentinel = Join-Path $unknown 'user-input.txt'
            [IO.File]::WriteAllText($sentinel, 'preserve')
            Assert-Rejected { Initialize-YLTestWorkspace -WorkspaceRoot $unknown -Repository $repository } 'unknown|owner'
            Assert-That ([IO.File]::ReadAllText($sentinel) -ceq 'preserve') 'Unknown input changed.'
            Assert-Rejected { Initialize-YLTestWorkspace -WorkspaceRoot $repository -Repository $repository } 'source|repository'
            Assert-Rejected { Initialize-YLTestWorkspace -WorkspaceRoot $sandbox -Repository $repository } 'source|repository'
            Assert-Rejected { Initialize-YLTestWorkspace -WorkspaceRoot (Join-Path $repository 'Generated') -Repository $repository } 'source|repository'
            Assert-That (-not [IO.Path]::Exists((Join-Path $repository 'Generated'))) 'Test outputs entered the source repository.'
        }

        Test-Case 'Archive below an owned workspace cannot become an active workspace' {
            $outer = Initialize-YLTestWorkspace (Join-Path $sandbox 'Storage') $repository
            $archive = Join-Path $outer.root 'Archive'
            $null = [IO.Directory]::CreateDirectory($archive)
            $target = Join-Path $archive 'FreshRun'
            Assert-Rejected { Initialize-YLTestWorkspace $target $repository } 'archive'
            Assert-That (-not [IO.Path]::Exists($target)) 'An active run was created in history storage.'
            $usable = Initialize-YLTestWorkspace (Join-Path $outer.root 'ShortIsolated') $repository
            Assert-That ([IO.Directory]::Exists($usable.root)) 'Short isolated workspaces outside Archive must still work.'
        }

        Test-Case 'managed results cannot contain new workspaces' {
            $outer = Initialize-YLTestWorkspace (Join-Path $sandbox 'ResultBoundary') $repository
            $run = New-YLTestDirectory $outer Result 'Daily'
            $fixture = [IO.Directory]::CreateDirectory((Join-Path $run.path 'Fixture')).FullName
            $target = Join-Path $fixture 'Short'
            Assert-Rejected { Initialize-YLTestWorkspace $target $repository } 'result'
            Assert-That (-not [IO.Path]::Exists($target)) 'Workspace initialization wrote inside a managed result.'
        }

        Test-Case 'a Cache fixture workspace is usable and protects its parent from recursive cleanup' {
            $outer = Initialize-YLTestWorkspace (Join-Path $sandbox 'CacheBoundary') $repository
            $fixture = New-YLTestDirectory $outer Cache 'UnitySafety'
            $source = [IO.Directory]::CreateDirectory((Join-Path $fixture.path 'Source')).FullName
            $child = Initialize-YLTestWorkspace (Join-Path $fixture.path 'Workspace') $source
            $run = New-YLTestDirectory $child Result 'Daily'
            $removed = Remove-YLTestDirectory $child $run.path -Apply
            Assert-That ($removed.status -ceq 'Removed' -and -not [IO.Directory]::Exists($run.path)) 'The child workspace could not clean its own result.'
            Assert-Rejected { Remove-YLTestDirectory $outer $fixture.path } 'nested workspace'
            Assert-Rejected { Remove-YLTestDirectory $outer $fixture.path -Apply } 'nested workspace'
            Assert-That ((Initialize-YLTestWorkspace $child.root $source).id -ceq $child.id) 'Cleanup changed the Cache fixture workspace.'
        }

        Test-Case 'Clean rejects legacy roots in source, Archive and managed Results without touching evidence' {
            $base = Join-Path $sandbox 'CleanCli'
            $source = Join-Path $base 'Repository'
            $testing = Join-Path $source 'Tools/Testing'
            foreach ($relative in @('Invoke-YLTests.ps1', 'TestSuites.json', 'Common/Workspace.ps1', 'Common/Process.ps1', 'Common/Results.ps1', 'Common/Packages.ps1')) {
                $target = Join-Path $testing $relative
                $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
                [IO.File]::Copy((Join-Path $PSScriptRoot "../$relative"), $target)
            }
            $outer = New-LegacyWorkspaceFixture (Join-Path $base 'W') $source
            $other = New-YLTestDirectory $outer Result 'Unrelated'
            $pwsh = (Get-Process -Id $PID).Path
            $arguments = @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', (Join-Path $testing 'Invoke-YLTests.ps1'),
                '-Action', 'Clean', '-Suite', 'Daily', '-Module', 'Reddot')
            $key = 'Run-Daily-Reddot-Mono'
            $violations = @(foreach ($case in @(
                @{ root = (Join-Path $source 'Legacy'); reason = 'source repository' },
                @{ root = (Join-Path $outer.root 'Archive/Legacy'); reason = 'archive' },
                @{ root = (Join-Path $other.path 'Legacy'); reason = 'result' }
            )) {
                $legacy = New-LegacyWorkspaceFixture $case.root $source
                $older = New-RotationFixture $legacy 'Passed' 0 $key
                $null = New-RotationFixture $legacy 'Passed' 1 $key
                $report = Join-Path $older.path 'summary.json'
                $before = [IO.File]::ReadAllText($report)
                foreach ($apply in $false, $true) {
                    $modeArguments = @(); if ($apply) { $modeArguments += '-Apply' }
                    $output = & $pwsh @arguments -WorkspaceRoot $legacy.root @modeArguments 2>&1
                    $code = $LASTEXITCODE
                    $summary = ($output | Where-Object { $_ -is [string] }) -join "`n" | ConvertFrom-Json -DateKind String
                    if ($code -ne 2 -or $summary.status -cne 'PrerequisiteNotMet' -or $summary.reason -notmatch $case.reason -or
                        -not [IO.File]::Exists($report) -or [IO.File]::ReadAllText($report) -cne $before) {
                        "Clean (Apply=$apply) accepted or changed legacy $($case.reason) output (exit $code)."
                    }
                }
            })

            $older = New-RotationFixture $outer 'Passed' 0 $key
            $newer = New-RotationFixture $outer 'Passed' 1 $key
            $missing = Join-Path $base 'NotCreated'
            $null = & $pwsh @arguments -WorkspaceRoot $missing -Apply
            Assert-That ($LASTEXITCODE -eq 0 -and -not [IO.Path]::Exists($missing)) 'Clean must not create a missing workspace.'
            $null = & $pwsh @arguments -WorkspaceRoot $outer.root
            Assert-That ($LASTEXITCODE -eq 0 -and [IO.Directory]::Exists($older.path)) 'Clean preview must leave eligible evidence untouched.'
            $null = & $pwsh @arguments -WorkspaceRoot $outer.root -Apply
            Assert-That ($LASTEXITCODE -eq 0 -and -not [IO.Directory]::Exists($older.path) -and [IO.Directory]::Exists($newer.path)) 'Clean did not rotate a valid independent workspace.'
            foreach ($area in @('Hosts', 'Cache')) {
                Assert-That (-not [IO.Path]::Exists((Join-Path $outer.root $area))) 'Clean must not initialize workspace areas.'
            }
            Assert-That ($violations.Count -eq 0) ($violations -join ' ')
        }

        Test-Case 'one-shot Player hosts cannot adopt a previous host on a key collision' {
            $ws = Initialize-YLTestWorkspace (Join-Path $sandbox 'FreshPlayer') $repository
            $old = New-YLTestDirectory $ws Host 'RAP01234567' -NewOnly
            Assert-Rejected { New-YLTestDirectory $ws Host 'RAP01234567' -NewOnly } 'already exists|fresh'
            Assert-That ((Get-YLTestDirectory $ws $old.path).id -ceq $old.id) 'The previous host was replaced.'
        }

        Test-Case 'interrupted candidate keeps a paired pin; a diagnostic keeps a durable host identity' {
            $ws = Initialize-YLTestWorkspace (Join-Path $sandbox 'Interrupted') $repository
            foreach ($formal in $false,$true) {
                $h = New-YLTestDirectory $ws Host $(if ($formal) { 'RFD11112222' } else { 'RFD22221111' }) -NewOnly
                $run = New-YLTestDirectory $ws Result 'FrozenEncryption'
                Start-YLPlayerRetention $h $run -FormalCandidate:$formal
                $ctx = Read-YLPackageEvidenceJson (Join-Path $run.path 'host-context.json')
                Assert-That ($ctx.hostId -ceq $h.id -and $ctx.resultId -ceq $run.id -and $ctx.host -ceq $h.path) 'Interrupted run cannot be associated with its host.'
                foreach ($dir in $h.path,$run.path) { Assert-That ([IO.File]::Exists((Join-Path $dir '.pinned')) -eq $formal) 'Incorrect pre-build protection.' }
            }
        }

        Test-Case 'a diagnostic preserves its input and audit receipts before retiring the host, not key material' {
            $ws = Initialize-YLTestWorkspace (Join-Path $sandbox 'DiagnosticEvidence') $repository
            $h = New-YLTestDirectory $ws Host 'RFD12345678' -NewOnly
            $run = New-YLTestDirectory $ws Result 'FrozenEncryption'
            $summary = @{host=$h.path; hostId=$h.id; resultId=$run.id; status='Passed'}
            $null = [IO.Directory]::CreateDirectory((Join-Path $h.path 'Private'))
            foreach ($file in '.yl-inputs.json','fixture.json','Private/audit.json','fixture.key') { [IO.File]::WriteAllText((Join-Path $h.path $file), $file) }
            $lease = Lock-YLTestDirectory $ws $h.path
            $runLease = Lock-YLTestDirectory $ws $run.path
            try { Complete-YLPlayerRun $ws $h $run $lease $summary }
            finally { $lease.Dispose(); $runLease.Dispose() }
            foreach ($file in '.yl-inputs.json','fixture.json','Private/audit.json') {
                Assert-That ([IO.File]::ReadAllText((Join-Path $run.path "host-evidence/$file")) -ceq $file) "Lost original receipt: $file"
            }
            Assert-That (-not [IO.File]::Exists((Join-Path $run.path 'host-evidence/fixture.key'))) 'Key material was archived.'
        }

        Test-Case 'one-shot Player retention records the actual outcome without sweeping other runs' {
            $ws = Initialize-YLTestWorkspace (Join-Path $sandbox 'PlayerRetention') $repository
            $unrelated = New-YLTestDirectory $ws Host 'OtherHost'
            foreach ($case in @(
                @('RAP00000001','ResourceAAIL2CPP','execution.json','Passed',$false,$false,'Removed'),
                @('RFD00000002','FrozenEncryption','frozen.json','Passed',$true,$false,'Pinned'),
                @('RNC00000003','NativeDeliveryFinal','p0-c.json','Passed',$false,$true,'Pinned'),
                @('RNC00000004','NativeDeliveryP0','p0-c.json','Incomplete',$false,$false,'Pinned'),
                @('RAP00000005','ResourceAAIL2CPP','execution.json','Failed',$false,$false,'Pinned'),
                @('RAP00000006','ResourceAAIL2CPP','execution.json',$true,$false,$false,'Pinned')
            )) {
                $h = New-YLTestDirectory $ws Host $case[0]; $run = New-YLTestDirectory $ws Result $case[1]
                $data = Join-Path $h.path 'Player.exe'; [IO.File]::WriteAllText($data, 'generated')
                $lease = Lock-YLTestDirectory $ws $h.path
                $runLease = Lock-YLTestDirectory $ws $run.path
                $summary = @{ status=$case[3]; host=$h.path; hostId=$h.id; resultId=$run.id; formalCandidate=$case[4] }
                try { Complete-YLPlayerRun $ws $h $run $lease $summary -KeepArtifacts:$case[5] }
                finally { $lease.Dispose(); $runLease.Dispose() }
                $receipt = Read-YLPackageEvidenceJson (Join-Path $run.path $case[2])
                $retention = Read-YLPackageEvidenceJson (Join-Path $run.path 'retention.json')
                Assert-That ($retention.hostStatus -ceq $case[6] -and $receipt.status -ceq $case[3]) 'Incorrect retention or original run status.'
                Assert-That ([IO.File]::Exists($data) -eq ($case[6] -ceq 'Pinned')) 'Wrong Player lifetime.'
                Assert-That ([IO.File]::Exists((Join-Path $run.path '.pinned')) -eq ($case[6] -ceq 'Pinned')) 'Wrong result lifetime.'
                if ($case[6] -ceq 'Pinned') {
                    $pin = Read-YLPackageEvidenceJson (Join-Path $h.path '.pinned')
                    Assert-That ($pin.resultId -ceq $run.id -and $pin.hostId -ceq $h.id) 'Missing host/result pin pairing.'
                }
            }
            Assert-That ([IO.Directory]::Exists($unrelated.path)) 'Another run was swept.'
        }

        Test-Case 'workspace and container JSON identities reject boolean coercion' {
            $root = Join-Path $sandbox 'TypedOwners'
            $workspace = Initialize-YLTestWorkspace -WorkspaceRoot $root -Repository $repository
            $workspacePath = Join-Path $root '.yl-workspace.json'
            $original = [IO.File]::ReadAllText($workspacePath)
            foreach ($field in @('purpose', 'schemaVersion', 'repository', 'id')) {
                $invalid = $original | ConvertFrom-Json -AsHashtable -DateKind String
                $invalid[$field] = $true
                [IO.File]::WriteAllText($workspacePath, (ConvertTo-Json -InputObject $invalid))
                Assert-Rejected { Assert-YLTestWorkspace $workspace } 'identity|type|mismatch'
            }
            [IO.File]::WriteAllText($workspacePath, $original)
            $hostRecord = New-YLTestDirectory -Workspace $workspace -Kind Host -Key 'Host'
            $path = Join-Path $hostRecord.path '.yl-owner.json'
            $original = [IO.File]::ReadAllText($path)
            foreach ($field in @('purpose', 'schemaVersion', 'workspaceId', 'path', 'id')) {
                $invalid = $original | ConvertFrom-Json -AsHashtable -DateKind String
                $invalid[$field] = $true
                [IO.File]::WriteAllText($path, (ConvertTo-Json -InputObject $invalid))
                Assert-Rejected { Get-YLTestDirectory -Workspace $workspace -Path $hostRecord.path } 'identity|owner|type|mismatch'
                Assert-Rejected { New-YLTestContainer -Workspace $workspace -Path $hostRecord.path -Kind Host -Key Host -ExistingOnly } 'identity|owner|type|mismatch'
            }
            [IO.File]::WriteAllText($path, $original)
        }

        Test-Case 'ambiguous, relative, traversal and reserved Windows paths are rejected' {
            foreach ($path in @('', 'relative', 'G:relative', (Join-Path $sandbox '../escape'),
                (Join-Path $sandbox 'trailing.'), (Join-Path $sandbox 'CON.txt'), (Join-Path $sandbox 'file:stream'))) {
                Assert-Rejected { Get-YLTestPath -Path $path } 'explicit|ambiguous|reserved|path'
            }
        }

        Test-Case 'owned result creation and preview do not delete or rewrite outputs' {
            $workspace = Initialize-YLTestWorkspace -WorkspaceRoot (Join-Path $sandbox 'Preview') -Repository $repository
            $run = New-YLTestDirectory -Workspace $workspace -Kind Result -Key 'Daily-Reddot'
            $data = Join-Path $run.path 'results.xml'
            [IO.File]::WriteAllText($data, 'preserve')
            $before = (Get-FileHash -LiteralPath $data).Hash
            $preview = Remove-YLTestDirectory -Workspace $workspace -Path $run.path
            Assert-That ($preview.status -ceq 'Preview' -and [IO.Directory]::Exists($run.path)) 'Clean must default to preview.'
            Assert-That ((Get-FileHash -LiteralPath $data).Hash -ceq $before) 'Preview changed bytes.'
            $removed = Remove-YLTestDirectory -Workspace $workspace -Path $run.path -Apply
            Assert-That ($removed.status -ceq 'Removed' -and -not [IO.Directory]::Exists($run.path)) 'Apply must delete only the owned result.'
            Assert-That ([IO.Directory]::Exists($workspace.root)) 'Workspace root was deleted.'
        }

        Test-Case 'pinned and actively leased results survive cleanup' {
            $workspace = Initialize-YLTestWorkspace -WorkspaceRoot (Join-Path $sandbox 'Protected') -Repository $repository
            $run = New-YLTestDirectory -Workspace $workspace -Kind Result -Key 'Daily-Reddot'
            $lease = Lock-YLTestDirectory -Workspace $workspace -Path $run.path
            try { Assert-Rejected { Remove-YLTestDirectory -Workspace $workspace -Path $run.path -Apply } 'use|lease|lock' }
            finally { $lease.Dispose() }
            [IO.File]::WriteAllText((Join-Path $run.path '.pinned'), 'explicit retention')
            Assert-Rejected { Remove-YLTestDirectory -Workspace $workspace -Path $run.path -Apply } 'pinned'
            Assert-That ([IO.Directory]::Exists($run.path)) 'Protected result was deleted.'
        }

        Test-Case 'a protected nested fixture cannot be retired with a successful Player host' {
            $ws = Initialize-YLTestWorkspace (Join-Path $sandbox 'NestedPlayerPin') $repository
            $h = New-YLTestDirectory $ws Host 'RAP01234567' -NewOnly
            $run = New-YLTestDirectory $ws Result 'ResourceAAIL2CPP'
            $fixture = [IO.Directory]::CreateDirectory((Join-Path $h.path 'Fixture')).FullName
            $pin = Join-Path $fixture '.pinned'; [IO.File]::WriteAllText($pin, 'do not retire')
            $lease = Lock-YLTestDirectory $ws $h.path
            $resultLease = Lock-YLTestDirectory $ws $run.path
            try {
                $s = @{ status='Passed'; host=$h.path; hostId=$h.id; resultId=$run.id }
                Assert-Rejected { Complete-YLPlayerRun $ws $h $run $lease $s } 'protected|pinned'
            } finally { $lease.Dispose(); $resultLease.Dispose() }
            Assert-That ([IO.File]::ReadAllText($pin) -ceq 'do not retire') 'Nested evidence was touched.'
            Assert-That ((Read-YLPackageEvidenceJson (Join-Path $run.path 'retention.json')).hostStatus -ceq 'CleanupFailed') 'Cleanup refusal was hidden.'
            $terminal = Read-YLPackageEvidenceJson (Join-Path $run.path 'execution.json')
            Assert-That ($terminal.status -ceq 'Failed' -and $terminal.runStatus -ceq 'Passed') 'Cleanup failure was published as an overall pass.'
        }

        Test-Case 'unknown, foreign and out-of-root cleanup targets are rejected' {
            $workspace = Initialize-YLTestWorkspace -WorkspaceRoot (Join-Path $sandbox 'Boundaries') -Repository $repository
            $other = Initialize-YLTestWorkspace -WorkspaceRoot (Join-Path $sandbox 'Other') -Repository $repository
            $foreign = New-YLTestDirectory -Workspace $other -Kind Result -Key 'Daily'
            $unknown = Join-Path $workspace.root 'Results/Unknown'
            $null = [IO.Directory]::CreateDirectory($unknown)
            foreach ($target in @($workspace.root, (Join-Path $workspace.root 'Results'), $unknown, $foreign.path, $repository)) {
                Assert-Rejected { Remove-YLTestDirectory -Workspace $workspace -Path $target -Apply } 'owner|owned|outside|root|container'
                Assert-That ([IO.Directory]::Exists($target)) 'A rejected target was deleted.'
            }
        }

        Test-Case 'linked ancestors and descendants never redirect cleanup' {
            $workspace = Initialize-YLTestWorkspace -WorkspaceRoot (Join-Path $sandbox 'Links') -Repository $repository
            $run = New-YLTestDirectory -Workspace $workspace -Kind Result -Key 'Daily'
            $outside = Join-Path $sandbox 'Outside'
            $null = [IO.Directory]::CreateDirectory($outside)
            [IO.File]::WriteAllText((Join-Path $outside 'sentinel.txt'), 'preserve')
            $link = Join-Path $run.path 'junction'
            $null = New-Item -ItemType Junction -Path $link -Target $outside
            $links.Add($link)
            Assert-Rejected { Get-YLTestPath -Path (Join-Path $link 'sentinel.txt') } 'link'
            $readOnly = Get-YLTestPath -Path (Join-Path $link 'sentinel.txt') -ReadOnly
            Assert-That ($readOnly -ceq (Join-Path $link 'sentinel.txt')) 'Read-only resolution must support installed executable/reference links.'
            Assert-Rejected { Remove-YLTestDirectory -Workspace $workspace -Path $run.path -Apply } 'link'
            Assert-That ([IO.File]::ReadAllText((Join-Path $outside 'sentinel.txt')) -ceq 'preserve') 'A link target changed.'
        }
    }
    if ($Group -in @('All', 'Process')) {
        Test-Case 'process facts retain real exit0 and both output streams without recording arguments' {
            $start = New-ProcessFixture
            $start.ArgumentList.Add('-Marker')
            $start.ArgumentList.Add('private-marker-not-for-receipts')
            $log = Join-Path $sandbox 'exit zero.log'
            $result = Invoke-YLTestProcess -StartInfo $start -Log $log -TimeoutMilliseconds 15000
            Assert-YLTestProcessSucceeded -ProcessResult $result
            Assert-That ($result.status -ceq 'Exited' -and $result.observedExit -and $result.exitCode -eq 0 -and -not $result.forcedCleanup) 'A real unforced exit0 was not recorded.'
            Assert-That ($result.pid -gt 0 -and $result.activeProcesses -eq 0 -and $result.streamsDrained) 'Process/tree/output observation is incomplete.'
            Assert-That ([IO.File]::ReadAllText($log).Contains('stdout-marker')) 'Standard output was lost.'
            Assert-That ([IO.File]::ReadAllText($log + '.stderr.log').Contains('stderr-marker')) 'Standard error was lost.'
            $receipt = Read-YLTestProcessResult -Log $log
            Assert-That ($receipt.exitCode -eq 0 -and $receipt.invocationId -ceq $result.invocationId) 'Terminal evidence cannot round-trip.'
            Assert-That ($receipt.startUtc -is [string] -and $receipt.startUtc -ceq $result.startUtc) 'Durable process timestamps must retain their wire format.'
            foreach ($path in @(($log + '.identity.json'), ($log + '.exit.json'))) {
                Assert-That (-not [IO.File]::ReadAllText($path).Contains('private-marker-not-for-receipts')) 'Arguments leaked into a receipt.'
            }
        }

        Test-Case 'nonzero exits remain failures even when artifacts look successful' {
            $result = Invoke-YLTestProcess -StartInfo (New-ProcessFixture -ExitCode 17) -Log (Join-Path $sandbox 'nonzero.log') -TimeoutMilliseconds 15000
            Assert-That ($result.status -ceq 'NonZeroExit' -and $result.observedExit -and $result.exitCode -eq 17) 'The actual nonzero exit was not retained.'
            Assert-That (-not $result.forcedCleanup -and $result.activeProcesses -eq 0) 'An ordinary failure must not be mislabeled as a forced exit.'
        }

        Test-Case 'timeout kills the real process and does not become an ordinary pass' {
            $result = Invoke-YLTestProcess -StartInfo (New-ProcessFixture -Mode Wait) -Log (Join-Path $sandbox 'timeout.log') -TimeoutMilliseconds 1000
            Assert-That ($result.status -ceq 'TimedOut' -and $result.timedOut -and $result.forcedCleanup) 'Timeout must remain a distinct forced outcome.'
            Assert-That ($result.observedExit -and $null -ne $result.exitCode -and $result.activeProcesses -eq 0) 'Timeout cleanup did not observe the real terminal state.'
        }

        Test-Case 'pre-cancellation starts no child and in-flight cancellation cleans up' {
            $cancel = [Threading.CancellationTokenSource]::new()
            try {
                $cancel.Cancel()
                $result = Invoke-YLTestProcess -StartInfo (New-ProcessFixture -Mode Wait) -Log (Join-Path $sandbox 'pre-cancel.log') -TimeoutMilliseconds 15000 -CancellationToken $cancel.Token
                Assert-That ($result.status -ceq 'Cancelled' -and $null -eq $result.pid -and -not $result.observedExit) 'Pre-cancellation started a child or fabricated an exit.'
            } finally { $cancel.Dispose() }
            $cancel = [Threading.CancellationTokenSource]::new()
            try {
                $cancel.CancelAfter(1500)
                $result = Invoke-YLTestProcess -StartInfo (New-ProcessFixture -Mode Wait) -Log (Join-Path $sandbox 'cancel.log') -TimeoutMilliseconds 15000 -CancellationToken $cancel.Token
                Assert-That ($result.status -ceq 'Cancelled' -and $result.pid -gt 0 -and $result.forcedCleanup -and $result.activeProcesses -eq 0) 'In-flight cancellation did not clean up its child.'
            } finally { $cancel.Dispose() }
        }

        Test-Case 'failed startup has no invented pid or exit code' {
            $start = New-ProcessFixture
            $start.FileName = Join-Path $sandbox 'missing executable.exe'
            $result = Invoke-YLTestProcess -StartInfo $start -Log (Join-Path $sandbox 'start-failed.log') -TimeoutMilliseconds 15000
            Assert-That ($result.status -ceq 'StartFailed' -and $null -eq $result.pid -and $null -eq $result.exitCode -and -not $result.observedExit) 'A start error was disguised as an observed process exit.'
        }

        Test-Case 'cancellation during descendant drain is not reported as exit0 success' {
            $start = New-ProcessFixture -Mode Orphan
            $start.ArgumentList.Add('-ChildIdentity')
            $start.ArgumentList.Add((Join-Path $sandbox 'drain-child.json'))
            $start.ArgumentList.Add('-DelayMilliseconds')
            $start.ArgumentList.Add('1500')
            $cancel = [Threading.CancellationTokenSource]::new()
            try {
                $cancel.CancelAfter(1200)
                $result = Invoke-YLTestProcess -StartInfo $start -Log (Join-Path $sandbox 'drain-cancel.log') -TimeoutMilliseconds 15000 -CancellationToken $cancel.Token
                Assert-That ($result.status -ceq 'Cancelled' -and $result.cancelled -and $result.activeProcesses -eq 0) 'Cancellation during terminal cleanup was lost.'
            } finally { $cancel.Dispose() }
        }

        Test-Case 'missing terminal evidence means observer lost rather than exit0' {
            $log = Join-Path $sandbox 'lost.log'
            $null = Invoke-YLTestProcess -StartInfo (New-ProcessFixture) -Log $log -TimeoutMilliseconds 15000
            [IO.File]::Delete($log + '.exit.json')
            $result = Read-YLTestProcessResult -Log $log
            Assert-That ($result.status -ceq 'ObserverLost' -and $null -eq $result.exitCode -and -not $result.observedExit) 'Missing terminal evidence must never imply exit0.'
        }

        Test-Case 'timeout cleans descendants and natural parent exit cannot hide a surviving child' {
            foreach ($mode in @('Tree', 'Orphan')) {
                $child = Join-Path $sandbox ($mode + '-child.json')
                $start = New-ProcessFixture -Mode $mode
                $start.ArgumentList.Add('-ChildIdentity')
                $start.ArgumentList.Add($child)
                $timeout = if ($mode -eq 'Tree') { 4000 } else { 15000 }
                $result = Invoke-YLTestProcess -StartInfo $start -Log (Join-Path $sandbox ($mode + '.log')) -TimeoutMilliseconds $timeout
                $expected = if ($mode -eq 'Tree') { 'TimedOut' } else { 'DescendantsAlive' }
                Assert-That ($result.status -ceq $expected -and $result.forcedCleanup -and $result.activeProcesses -eq 0) 'A surviving descendant was missed or left running.'
                Assert-ChildExited $child
            }
        }

        Test-Case 'invalid deadlines and existing process evidence are rejected before launch' {
            Assert-Rejected { Invoke-YLTestProcess -StartInfo (New-ProcessFixture) -Log (Join-Path $sandbox 'invalid.log') -TimeoutMilliseconds 0 } 'TimeoutMilliseconds'
            $log = Join-Path $sandbox 'existing.log'
            [IO.File]::WriteAllText($log, 'preserve')
            Assert-Rejected { Invoke-YLTestProcess -StartInfo (New-ProcessFixture) -Log $log -TimeoutMilliseconds 15000 } 'existing|preserve'
            Assert-That ([IO.File]::ReadAllText($log) -ceq 'preserve') 'Existing evidence was overwritten.'
        }
    }
    if ($Group -in @('All', 'Results')) {
        . (Join-Path $PSScriptRoot 'Test-YLDistributionContracts.ps1')
        function Invoke-NativePackageFixture([string] $EditorVersion = '2022.3.62f3', [string] $Fault = '', [string] $NativeSdk = '') {
            $root = Join-Path $sandbox ('NativePackages-' + [Guid]::NewGuid().ToString('N'))
            $repository = Join-Path $root 'Repository'
            $fixtureWorkspace = Join-Path $root 'Workspace'
            $null = [IO.Directory]::CreateDirectory($repository)
            foreach ($relative in @('Invoke-YLTests.ps1', 'UnityHost.ps1', 'TestSuites.json', 'Common/Workspace.ps1', 'Common/Process.ps1', 'Common/Results.ps1', 'Common/Packages.ps1')) {
                $target = Join-Path $repository "Tools/Testing/$relative"
                $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
                [IO.File]::Copy((Join-Path $PSScriptRoot "../$relative"), $target)
            }
            $files = [ordered]@{
                'Tools/Packaging/PackageCatalog.json' = [IO.File]::ReadAllText([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../Packaging/PackageCatalog.json')))
                'PackageSources/com.kano.ylframework/package.json' = '{"name":"com.kano.ylframework","version":"0.1.0-preview.1","dependencies":{}}'
                'PackageSources/com.kano.ylframework/LICENSE.md' = 'MIT License fixture'
                'PackageSources/com.kano.ylframework/Runtime/Product.cs' = 'class Product {}'
                'PackageSources/com.kano.ylframework/Runtime/Product.asmdef' = '{"name":"Fixture.Runtime"}'
                'PackageSources/com.kano.ylframework/Editor/ProductEditor.cs' = 'class ProductEditor {}'
                'PackageSources/com.kano.ylframework/Prefabs/Fixture.prefab' = 'fixture complete package asset'
                'Assets/Tests/YLFramework/Fixtures/Shared/PackageTestPaths.cs' = 'class PackageTestPaths {}'
                'Assets/Tests/EditMode/EditCase.cs' = 'class EditCase {}'
                'Assets/Tests/PlayMode/PlayCase.cs' = 'class PlayCase {}'
                'Assets/Tests/YLFramework/Infrastructure/Editor/YLTestEntry.cs' = 'class Entry {}'
                'Assets/Tests/YLFramework/Infrastructure/Editor/YLFramework.Tests.Infrastructure.Editor.asmdef' = '{"name":"Fixture.Entry"}'
                'Packages/manifest.json' = '{"dependencies":{"com.unity.modules.jsonserialize":"1.0.0","com.unity.modules.audio":"1.0.0","com.unity.modules.accessibility":"1.0.0","com.unity.modules.adaptiveperformance":"1.0.0","com.unity.modules.vectorgraphics":"1.0.0","com.unity.render-pipelines.universal":"17.3.0"}}'
                'Packages/packages-lock.json' = '{"dependencies":{"com.unity.nuget.newtonsoft-json":{"version":"3.2.2"},"com.unity.addressables":{"version":"2.10.3","source":"registry"}}}'
                'Tools/Testing/Profiles/Unity2022/manifest.json' = '{"dependencies":{"com.unity.modules.jsonserialize":"1.0.0","com.unity.modules.audio":"1.0.0","com.unity.render-pipelines.universal":"14.0.12"}}'
                'Tools/Testing/Profiles/Unity2022/packages-lock.json' = '{"dependencies":{"com.unity.nuget.newtonsoft-json":{"version":"3.2.1"},"com.unity.addressables":{"version":"1.25.0","source":"registry"}}}'
                'Unity.exe' = 'native-editor-fixture'
            }
            if ($NativeSdk -ceq 'HybridCLR') {
                $sdkId = 'com.code-philosophy.hybridclr'
                $sdkUrl = 'https://gitee.com/focus-creative-games/hybridclr_unity.git#v9.0.3'
                $sdkHash = '6cadd24d101f4cbd00cd80190106f6621132d24d'
                $manifest = ConvertFrom-Json -InputObject $files['Packages/manifest.json'] -AsHashtable
                $manifest.dependencies[$sdkId] = $sdkUrl
                $files['Packages/manifest.json'] = ConvertTo-Json -InputObject $manifest -Depth 10
                $lock = ConvertFrom-Json -InputObject $files['Packages/packages-lock.json'] -AsHashtable
                $lock.dependencies[$sdkId] = @{ version = $sdkUrl; source = 'git'; hash = $sdkHash }
                $files['Packages/packages-lock.json'] = ConvertTo-Json -InputObject $lock -Depth 10
                $files['PackageSources/com.kano.ylframework.hybridclr/package.json'] = '{"name":"com.kano.ylframework.hybridclr","version":"0.1.0-preview.1","dependencies":{"com.kano.ylframework":"0.1.0-preview.1","com.code-philosophy.hybridclr":"9.0.3"}}'
                $files['PackageSources/com.kano.ylframework.hybridclr/Runtime/Consumer.cs'] = 'class Consumer {}'
                $files['PackageSources/com.kano.ylframework.hybridclr/Dependencies.json'] = ConvertTo-Json -Depth 10 -InputObject @{
                    package = @{ name = $sdkId; version = '9.0.3'; url = $sdkUrl; commit = $sdkHash; bundled = $false }
                }
            }
            foreach ($entry in $files.GetEnumerator()) {
                $target = Join-Path $repository $entry.Key
                $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
                [IO.File]::WriteAllText($target, $entry.Value, [Text.UTF8Encoding]::new($false))
                if ($entry.Key.StartsWith('Assets/', [StringComparison]::Ordinal) -or $entry.Key.StartsWith('PackageSources/', [StringComparison]::Ordinal)) { [IO.File]::WriteAllText(($target + '.meta'), ('guid: ' + [Guid]::NewGuid().ToString('N'))) }
            }
            foreach ($relative in @('PackageSources/com.kano.ylframework/Runtime', 'Assets/Tests/EditMode', 'Assets/Tests/PlayMode', 'Assets/Tests/YLFramework/Fixtures/Shared')) {
                [IO.File]::WriteAllText((Join-Path $repository ($relative + '.meta')), ('guid: ' + [Guid]::NewGuid().ToString('N')))
            }
            $definition = [pscustomobject]@{
                runtimeSources = @('PackageSources/com.kano.ylframework/Runtime'); nativeSources = @(); assemblyMetadata = @('PackageSources/com.kano.ylframework/Runtime/Product.asmdef')
                editTests = @('Assets/Tests/EditMode'); playTests = @('Assets/Tests/PlayMode'); nativeLayout = 'Framework'; graphics = $false
                category = ''; assemblies = @('Fixture.Tests'); excludedCategories = @(); sortingLayer = ''
                packageInputs = @('com.kano.ylframework'); sampleInputs = @()
            }
            if ($NativeSdk) { $definition | Add-Member -NotePropertyName nativeSdk -NotePropertyValue $NativeSdk }
            if ($NativeSdk -ceq 'HybridCLR') {
                $definition.packageInputs = @('com.kano.ylframework.hybridclr')
                $sdkManifestPath = Join-Path $repository 'Packages/manifest.json'
                $sdkLockPath = Join-Path $repository 'Packages/packages-lock.json'
                $sdkApprovalPath = Join-Path $repository 'PackageSources/com.kano.ylframework.hybridclr/Dependencies.json'
                switch -CaseSensitive ($Fault) {
                    'sdk-declaration' {
                        $document = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($sdkManifestPath)) -AsHashtable
                        $document.dependencies[$sdkId] = $true
                        [IO.File]::WriteAllText($sdkManifestPath, (ConvertTo-Json -InputObject $document -Depth 10))
                    }
                    'sdk-source' {
                        $document = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($sdkLockPath)) -AsHashtable
                        $document.dependencies[$sdkId].source = 'registry'
                        [IO.File]::WriteAllText($sdkLockPath, (ConvertTo-Json -InputObject $document -Depth 10))
                    }
                    'sdk-approval' {
                        $document = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($sdkApprovalPath)) -AsHashtable
                        $document.package.commit = 'a' * 40
                        [IO.File]::WriteAllText($sdkApprovalPath, (ConvertTo-Json -InputObject $document -Depth 10))
                    }
                }
            }
            $configuration = [IO.File]::ReadAllText((Join-Path $repository 'Tools/Testing/TestSuites.json')) | ConvertFrom-Json -DateKind String
            $editor = @($configuration.editors | Where-Object { $_.version -ceq $EditorVersion })[0]
            switch -CaseSensitive ($Fault) {
                'unknown-profile' { $editor.productVersion = '2022.3.62f3_unknown' }
                'mismatched-profile' { $editor.version = '2022.3.62f3c1' }
                'boolean-profile' { $editor.productVersion = $true }
                'missing-packages' { [IO.File]::Delete((Join-Path $repository 'Tools/Testing/Profiles/Unity2022/packages-lock.json')) }
            }
            $tokens = $null; $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '../Invoke-YLTests.ps1'), [ref]$tokens, [ref]$errors)
            Assert-That ($errors.Count -eq 0) 'The CLI source selection must parse.'
            $definitionAst = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Get-SelectedSources' }, $true)
            Assert-That ($null -ne $definitionAst) 'The real CLI source selector is missing.'
            . ([scriptblock]::Create($definitionAst.Extent.Text))
            $state = @{ calls = 0 }
            function Invoke-TestingProcess([string] $Exe, [string[]] $Arguments) {
                $state.calls++
                $requestPath = $Arguments[[Array]::IndexOf($Arguments, '-ylTestRequest') + 1]
                $request = [IO.File]::ReadAllText($requestPath) | ConvertFrom-Json -DateKind String
                $project = $Arguments[[Array]::IndexOf($Arguments, '-projectPath') + 1]
                $packagePrefix = if ($EditorVersion -ceq '6000.3.27f1') { 'Packages' } else { 'Tools/Testing/Profiles/Unity2022' }
                [IO.File]::Copy((Join-Path $repository "$packagePrefix/packages-lock.json"), (Join-Path $project 'Packages/packages-lock.json'), $true)
                if ($Fault -ceq 'addressables-lock-version') {
                    $changed = [IO.File]::ReadAllText((Join-Path $project 'Packages/packages-lock.json')) | ConvertFrom-Json -AsHashtable
                    $changed.dependencies['com.unity.addressables'].version = '0.0.0'
                    [IO.File]::WriteAllText((Join-Path $project 'Packages/packages-lock.json'), (ConvertTo-Json -InputObject $changed -Depth 10))
                }
                if ($NativeSdk -ceq 'HybridCLR') {
                    $locked = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText((Join-Path $project 'Packages/packages-lock.json'))) -AsHashtable
                    $locked.dependencies[$sdkId] = (ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($sdkLockPath)) -AsHashtable).dependencies[$sdkId]
                    if ($Fault -ceq 'sdk-resolved-revision') { $locked.dependencies[$sdkId].hash = 'a' * 40 }
                    [IO.File]::WriteAllText((Join-Path $project 'Packages/packages-lock.json'), (ConvertTo-Json -InputObject $locked -Depth 10))
                }
                $tests = @(
                    @{ assembly = 'Fixture.Tests'; fullName = 'Fixture.Edit'; mode = 'EditMode'; runState = 'Runnable' },
                    @{ assembly = 'Fixture.Tests'; fullName = 'Fixture.Play'; mode = 'PlayMode'; runState = 'Runnable' }
                )
                Write-YLTestJson -Path (Join-Path $request.output 'response.json') -Value @{
                    schemaVersion = 1; hostId = $request.hostId; inputHash = $request.inputHash; unityVersion = $editor.version
                    status = $(if ($request.action -ceq 'Run') { 'Passed' } else { 'Completed' }); isBatchMode = $true
                    testFrameworkVersion = $editor.testFramework; runtimeReferences = @(); compiledInputs = @(); excludedTests = @(); tests = $tests
                    addressablesVersion = $(if ($Fault -ceq 'addressables-loaded-version') { '0.0.0' } else { Get-YLUnityAddressablesVersion $editor })
                    addressablesSource = 'Registry'
                }
                if ($request.action -ceq 'Run') {
                    $mode = $request.tests[0].mode
                    $leaf = if ($mode -ceq 'EditMode') { 'Fixture.Edit' } else { 'Fixture.Play' }
                    [IO.File]::WriteAllText((Join-Path $request.output "results-$mode.xml"),
                        ('<test-run result="Passed" total="1" passed="1" failed="0" skipped="0" inconclusive="0"><test-suite type="Assembly" name="Fixture.Tests.dll" result="Passed"><test-case fullname="' + $leaf + '" result="Passed"/></test-suite></test-run>'))
                }
                if ($request.action -ceq 'Discover') {
                    switch -CaseSensitive ($Fault) {
                        'manifest-drift' { [IO.File]::AppendAllText((Join-Path $repository "$packagePrefix/manifest.json"), ' ') }
                        'lock-drift' { [IO.File]::AppendAllText((Join-Path $repository "$packagePrefix/packages-lock.json"), ' ') }
                        'unselected-drift' { [IO.File]::AppendAllText((Join-Path $repository 'Packages/manifest.json'), ' ') }
                        'sdk-manifest-drift' { [IO.File]::AppendAllText($sdkManifestPath, ' ') }
                        'sdk-lock-drift' { [IO.File]::AppendAllText($sdkLockPath, ' ') }
                        'sdk-approval-drift' { [IO.File]::AppendAllText($sdkApprovalPath, ' ') }
                    }
                }
                return [pscustomobject]@{ status = 'Exited'; observedExit = $true; exitCode = 0; timedOut = $false; cancelled = $false
                    forcedCleanup = $false; observerError = $null; cleanupError = $null; jobAssigned = $true; activeProcesses = 0; streamsDrained = $true }
            }
            $summary = [ordered]@{ xml = @() }
            try {
                Invoke-YLUnitySuite -Repository $repository -Definition $definition -EditorProfile $editor -Unity (Join-Path $repository 'Unity.exe') `
                    -WorkspaceRoot $fixtureWorkspace -Action Run -Suite Daily -Module $(if ($NativeSdk -ceq 'HybridCLR') { 'HybridCLR' } else { 'Fixture' }) -TimeoutSeconds 15 -Selection '' -KeepArtifacts $false -Summary $summary
            } catch {
                if ($Fault -cin @('unknown-profile', 'mismatched-profile', 'boolean-profile', 'missing-packages', 'sdk-declaration', 'sdk-source', 'sdk-approval')) {
                    Assert-That ($state.calls -eq 0 -and -not [IO.Path]::Exists($fixtureWorkspace)) 'An invalid package source was rejected only after workspace creation or engine invocation.'
                }
                throw
            }
            $hostManifest = [IO.File]::ReadAllText((Join-Path $summary.host.path 'Packages/manifest.json')) | ConvertFrom-Json -DateKind String
            return [pscustomobject]@{ summary = $summary; manifest = $hostManifest; calls = $state.calls; root = $root }
        }

        Test-Case 'Native Framework hosts bind dependencies and exact input files to the declared target Editor package layout' {
            foreach ($case in @(
                @{ version = '2022.3.62f3'; prefix = 'Tools/Testing/Profiles/Unity2022'; newtonsoft = '3.2.1'; ugui = '1.0.0'; utf = '1.3.9'; unity6 = $false },
                @{ version = '2022.3.62f3c1'; prefix = 'Tools/Testing/Profiles/Unity2022'; newtonsoft = '3.2.1'; ugui = '1.0.0'; utf = '1.3.9'; unity6 = $false },
                @{ version = '6000.3.27f1'; prefix = 'Packages'; newtonsoft = '3.2.2'; ugui = '2.0.0'; utf = '1.6.0'; unity6 = $true }
            )) {
                $fixture = Invoke-NativePackageFixture -EditorVersion $case.version
                $dependencies = $fixture.manifest.dependencies
                Assert-That ($dependencies.'com.unity.nuget.newtonsoft-json' -ceq $case.newtonsoft) 'The host took Newtonsoft from the wrong source project lock.'
                Assert-That ($dependencies.'com.unity.ugui' -ceq $case.ugui -and $dependencies.'com.unity.test-framework' -ceq $case.utf) 'The target Editor package profile was lost.'
                $addressables = if ($case.unity6) { '2.10.3' } else { '1.25.0' }
                Assert-That ($dependencies.'com.unity.addressables' -ceq $addressables -and $fixture.summary.addressables.resolved -ceq $addressables) 'The exact Addressables profile pin or real resolution verification was lost.'
                foreach ($module in @('com.unity.modules.accessibility', 'com.unity.modules.adaptiveperformance', 'com.unity.modules.vectorgraphics')) {
                    Assert-That (($module -cin $dependencies.PSObject.Properties.Name) -eq $case.unity6) 'Unity 6-only built-in modules crossed the target Editor package boundary.'
                }
                Assert-That ('com.unity.render-pipelines.universal' -cnotin $dependencies.PSObject.Properties.Name) 'A native test host must not inherit unrelated development-host packages.'
                Assert-That ($dependencies.'com.kano.ylframework' -ceq 'file:../PackageSources/com.kano.ylframework') 'Native product dependencies must resolve to the owned full package staging.'
                foreach ($relative in @('Runtime/Product.cs', 'Runtime/Product.cs.meta', 'Editor/ProductEditor.cs', 'Prefabs/Fixture.prefab', 'LICENSE.md', 'package.json')) {
                    $staged = Join-Path $fixture.summary.host.path "PackageSources/com.kano.ylframework/$relative"
                    $source = Join-Path $fixture.root "Repository/PackageSources/com.kano.ylframework/$relative"
                    Assert-That ([IO.File]::Exists($staged) -and (Get-FileHash -LiteralPath $staged).Hash -ceq (Get-FileHash -LiteralPath $source).Hash) "Native full-package staging omitted or changed $relative."
                }
                Assert-That (-not [IO.Directory]::Exists((Join-Path $fixture.summary.host.path 'Assets/Runtime')) -and -not [IO.Directory]::Exists((Join-Path $fixture.summary.host.path 'Assets/YLFramework'))) 'Native UPM hosts must not duplicate product Runtime/Editor under Assets.'
                $packageFiles = @($fixture.summary.inputIdentity.files.path | Where-Object { $_ -match '^(?:Packages|Tools/Testing/Profiles/Unity2022)/' } | Sort-Object)
                Assert-That (($packageFiles -join ',') -ceq "$($case.prefix)/manifest.json,$($case.prefix)/packages-lock.json") 'Input identity does not bind the actual selected source Packages, or still binds the other engine project.'
                Assert-That ($fixture.summary.status -ceq 'Passed' -and $fixture.summary.testResult.total -eq 2 -and $fixture.calls -eq 3) 'Source-package identity did not survive discovery and both declared execution modes.'
            }
        }

        foreach ($fault in @('addressables-lock-version', 'addressables-loaded-version')) {
            Test-Case "Native Framework rejects actual $fault rather than using the declared version as evidence" {
                Assert-Rejected { Invoke-NativePackageFixture -Fault $fault } 'Addressables.*version'
            }
        }

        foreach ($fault in @('unknown-profile', 'mismatched-profile', 'boolean-profile')) {
            Test-Case "Native Framework refuses $fault before workspace creation" {
                Assert-Rejected { Invoke-NativePackageFixture -Fault $fault } 'profile|declared'
            }
        }
        Test-Case 'Native Framework never falls back to root Packages when the target source lock is missing' {
            Assert-Rejected { Invoke-NativePackageFixture -Fault 'missing-packages' } 'packages-lock.json'
        }
        foreach ($fault in @('manifest-drift', 'lock-drift')) {
            Test-Case "Native Framework rejects selected package $fault during discovery" {
                Assert-Rejected { Invoke-NativePackageFixture -Fault $fault } 'drift'
            }
        }
        Test-Case 'Native Framework 2022 input identity ignores unrelated root-6 package changes' {
            $fixture = Invoke-NativePackageFixture -Fault 'unselected-drift'
            Assert-That ($fixture.summary.status -ceq 'Passed') 'An unselected development-host package file contaminated the 2022 source identity.'
        }

        foreach ($editorVersion in @('2022.3.62f3', '6000.3.27f1')) {
            Test-Case "Native HybridCLR $editorVersion injects only its approved SDK and binds declaration lock and approval" {
                $fixture = Invoke-NativePackageFixture -EditorVersion $editorVersion -NativeSdk HybridCLR
                $dependencies = $fixture.manifest.dependencies
                Assert-That ($dependencies.'com.code-philosophy.hybridclr' -ceq 'https://gitee.com/focus-creative-games/hybridclr_unity.git#v9.0.3' -and
                    $dependencies.'com.kano.ylframework.hybridclr' -ceq 'file:../PackageSources/com.kano.ylframework.hybridclr') 'The native host lost its approved Git SDK or original product package.'
                Assert-That ($dependencies.'com.unity.nuget.newtonsoft-json' -ceq $(if ($editorVersion -ceq '2022.3.62f3') { '3.2.1' } else { '3.2.2' }) -and
                    'com.unity.render-pipelines.universal' -cnotin $dependencies.PSObject.Properties.Name) 'SDK installation replaced target dependencies or copied the development manifest wholesale.'
                foreach ($path in @('Packages/manifest.json', 'Packages/packages-lock.json', 'PackageSources/com.kano.ylframework.hybridclr/Dependencies.json')) {
                    Assert-That ($path -cin $fixture.summary.inputIdentity.files.path) "HybridCLR source identity omitted $path."
                }
                Assert-That ($fixture.summary.status -ceq 'Passed' -and $fixture.summary.testResult.total -eq 2 -and $fixture.calls -eq 3) 'The SDK consumer did not retain both declared native execution modes.'
            }
        }
        foreach ($fault in @('sdk-declaration', 'sdk-source', 'sdk-approval', 'sdk-resolved-revision')) {
            Test-Case "Native HybridCLR rejects $fault instead of accepting Registry or unapproved source" {
                Assert-Rejected { Invoke-NativePackageFixture -NativeSdk HybridCLR -Fault $fault } 'HybridCLR SDK'
            }
        }
        foreach ($fault in @('sdk-manifest-drift', 'sdk-lock-drift', 'sdk-approval-drift')) {
            Test-Case "Native HybridCLR rejects $fault after discovery instead of accepting an old input selection" {
                Assert-Rejected { Invoke-NativePackageFixture -NativeSdk HybridCLR -Fault $fault } 'drift'
            }
        }
        Test-Case 'Native hosts reject undeclared SDK kinds rather than treating every SDK as DOTween' {
            Assert-Rejected { Invoke-NativePackageFixture -NativeSdk Unexpected } 'Unsupported native SDK'
        }

        function Invoke-DistributionSdkFixture([string] $EditorVersion, [string] $Fault = '') {
            $fixtureRoot = Join-Path $sandbox ('DistributionSdk-' + [Guid]::NewGuid().ToString('N'))
            $repository = Join-Path $fixtureRoot 'Repository'
            $null = [IO.Directory]::CreateDirectory($repository)
            foreach ($relative in @('Tools/Testing/DistributionEntry.cs', 'Tools/Testing/PackageCatalogContract.cs', 'Tools/Packaging/PackageCatalog.json', 'Tools/Testing/PlayerSmoke.cs', 'Tools/Testing/HybridCLREntry.cs',
                'Tools/Testing/HybridCLRSmoke.cs', 'Tools/Testing/UnityHost.ps1', 'Tools/Testing/TestSuites.json',
                'Tools/Testing/Common/Workspace.ps1', 'Tools/Testing/Common/Process.ps1', 'Tools/Testing/Common/Results.ps1',
                'Tools/Testing/Tests/Test-YLDistribution.ps1', 'Packages/manifest.json', 'Packages/packages-lock.json',
                'PackageSources/com.kano.ylframework.hybridclr/Dependencies.json',
                'Tools/Testing/Profiles/Unity2022/manifest.json', 'Tools/Testing/Profiles/Unity2022/packages-lock.json')) {
                $target = Join-Path $repository $relative
                $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
                [IO.File]::Copy((Join-Path $PSScriptRoot "../../../$relative"), $target)
            }
            $files = [ordered]@{
                'Unity.exe' = 'editor-metadata-only-fixture'
            }
            foreach ($entry in $files.GetEnumerator()) {
                $target = Join-Path $repository $entry.Key
                $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
                [IO.File]::WriteAllText($target, $entry.Value, [Text.UTF8Encoding]::new($false))
            }
            $sdkKey = 'com.code-philosophy.hybridclr'
            $sdkManifestPath = Join-Path $repository 'Packages/manifest.json'
            $sdkLockPath = Join-Path $repository 'Packages/packages-lock.json'
            $sdkApprovalPath = Join-Path $repository 'PackageSources/com.kano.ylframework.hybridclr/Dependencies.json'
            if ($Fault -cin @('missing-declaration', 'boolean-declaration', 'empty-declaration', 'object-declaration', 'unpinned-declaration')) {
                $document = [IO.File]::ReadAllText($sdkManifestPath) | ConvertFrom-Json -AsHashtable -DateKind String
                switch -CaseSensitive ($Fault) {
                    'missing-declaration' { $null = $document.dependencies.Remove($sdkKey) }
                    'boolean-declaration' { $document.dependencies[$sdkKey] = $true }
                    'empty-declaration' { $document.dependencies[$sdkKey] = '' }
                    'object-declaration' { $document.dependencies[$sdkKey] = @{ version = '9.0.3' } }
                    'unpinned-declaration' { $document.dependencies[$sdkKey] = 'https://gitee.com/focus-creative-games/hybridclr_unity.git#main' }
                }
                [IO.File]::WriteAllText($sdkManifestPath, (ConvertTo-Json -InputObject $document -Depth 10), [Text.UTF8Encoding]::new($false))
            }
            if ($Fault -cin @('missing-sdk-lock', 'boolean-sdk-lock', 'mismatched-sdk-lock', 'wrong-sdk-source', 'invalid-sdk-hash')) {
                $document = [IO.File]::ReadAllText($sdkLockPath) | ConvertFrom-Json -AsHashtable -DateKind String
                switch -CaseSensitive ($Fault) {
                    'missing-sdk-lock' { $null = $document.dependencies.Remove($sdkKey) }
                    'boolean-sdk-lock' { $document.dependencies[$sdkKey] = $true }
                    'mismatched-sdk-lock' { $document.dependencies[$sdkKey].version = 'https://gitee.com/focus-creative-games/hybridclr_unity.git#main' }
                    'wrong-sdk-source' { $document.dependencies[$sdkKey].source = 'registry' }
                    'invalid-sdk-hash' { $document.dependencies[$sdkKey].hash = $true }
                }
                [IO.File]::WriteAllText($sdkLockPath, (ConvertTo-Json -InputObject $document -Depth 10), [Text.UTF8Encoding]::new($false))
            }
            if ($Fault -cin @('unapproved-declaration', 'unapproved-sdk-hash')) {
                $manifest = Read-YLPackageEvidenceJson $sdkManifestPath
                $lock = Read-YLPackageEvidenceJson $sdkLockPath
                if ($Fault -ceq 'unapproved-declaration') {
                    $manifest.dependencies[$sdkKey] = 'https://invalid.example/hybridclr.git#v9.0.3'
                    $lock.dependencies[$sdkKey].version = $manifest.dependencies[$sdkKey]
                } else { $lock.dependencies[$sdkKey].hash = 'b' * 40 }
                [IO.File]::WriteAllText($sdkManifestPath, (ConvertTo-Json -InputObject $manifest -Depth 10), [Text.UTF8Encoding]::new($false))
                [IO.File]::WriteAllText($sdkLockPath, (ConvertTo-Json -InputObject $lock -Depth 10), [Text.UTF8Encoding]::new($false))
            }
            if ($Fault -ceq 'missing-sdk-approval') { [IO.File]::Delete($sdkApprovalPath) }
            if ($Fault -cin @('wrong-sdk-approval-version', 'wrong-sdk-approval-commit', 'bundled-sdk-approval')) {
                $approval = Read-YLPackageEvidenceJson $sdkApprovalPath
                switch -CaseSensitive ($Fault) {
                    'wrong-sdk-approval-version' { $approval.package.version = '9.0.2' }
                    'wrong-sdk-approval-commit' { $approval.package.commit = 'c' * 40 }
                    'bundled-sdk-approval' { $approval.package.bundled = $true }
                }
                [IO.File]::WriteAllText($sdkApprovalPath, (ConvertTo-Json -InputObject $approval -Depth 10), [Text.UTF8Encoding]::new($false))
            }
            $configuration = [IO.File]::ReadAllText((Join-Path $repository 'Tools/Testing/TestSuites.json')) | ConvertFrom-Json -DateKind String
            $editor = @($configuration.editors | Where-Object { $_.version -ceq $EditorVersion })[0]
            $Unity = Join-Path $repository 'Unity.exe'
            $tokens = $null; $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-YLDistribution.ps1'), [ref]$tokens, [ref]$errors)
            Assert-That ($errors.Count -eq 0) 'The distribution SDK binding must parse.'
            foreach ($name in @('Get-DistributionHostPackages')) {
                $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name }, $true)
                Assert-That ($null -ne $function) "The real package preparation function is missing: $name"
                . ([scriptblock]::Create($function.Extent.Text))
            }
            $bare = Get-DistributionHostPackages -Repository $repository -EditorProfile $editor -HybridCLR $false
            $prepared = Get-DistributionHostPackages -Repository $repository -EditorProfile $editor -HybridCLR $true
            $identity = Get-YLTestInputIdentity -Repository $repository -Paths $prepared.inputs -Toolchain @($Unity) -Profile 'Distribution-Sdk-Fixture'
            if ($Fault -ceq 'sdk-manifest-drift') { [IO.File]::AppendAllText($sdkManifestPath, ' ') }
            if ($Fault -ceq 'sdk-lock-drift') { [IO.File]::AppendAllText($sdkLockPath, ' ') }
            if ($Fault -ceq 'sdk-approval-drift') { [IO.File]::AppendAllText($sdkApprovalPath, ' ') }
            $current = Get-YLTestInputIdentity -Repository $repository -Paths $prepared.inputs -Toolchain @($Unity) -Profile 'Distribution-Sdk-Fixture'
            if ($current.sha256 -cne $identity.sha256) { throw 'Distribution source inputs changed during verification.' }
            return [pscustomobject]@{ bare = [pscustomobject]@{ dependencies = [pscustomobject]$bare.dependencies }; installed = [pscustomobject]@{ dependencies = [pscustomobject]$prepared.dependencies }; identity = $identity }
        }

        foreach ($editorVersion in @('2022.3.62f3', '6000.3.27f1')) {
            Test-Case "Distribution $editorVersion HybridCLR binds the real root SDK without replacing target base dependencies" {
                $fixture = Invoke-DistributionSdkFixture -EditorVersion $editorVersion
                Assert-That ($fixture.installed.dependencies.'com.code-philosophy.hybridclr' -ceq 'https://gitee.com/focus-creative-games/hybridclr_unity.git#v9.0.3') 'The root exact HybridCLR SDK declaration was not injected.'
                Assert-That ('com.code-philosophy.hybridclr' -cnotin $fixture.bare.dependencies.PSObject.Properties.Name) 'The SDK leaked into the bare product import.'
                $unity6 = $editorVersion -ceq '6000.3.27f1'
                $expectedJson = if ($unity6) { '3.2.2' } else { '3.2.1' }
                $expectedUgui = if ($unity6) { '2.0.0' } else { '1.0.0' }
                Assert-That ($fixture.installed.dependencies.'com.unity.nuget.newtonsoft-json' -ceq $expectedJson -and $fixture.installed.dependencies.'com.unity.ugui' -ceq $expectedUgui) 'SDK injection replaced the target engine package dependencies.'
                foreach ($module in @('com.unity.modules.accessibility', 'com.unity.modules.adaptiveperformance', 'com.unity.modules.vectorgraphics')) {
                    Assert-That (($module -cin $fixture.installed.dependencies.PSObject.Properties.Name) -eq $unity6) 'SDK declaration lookup leaked root Unity 6-only modules into the 2022 host.'
                }
                $packageFiles = @($fixture.identity.files.path | Where-Object { $_ -match '^(?:Packages|Tools/Testing/Profiles/Unity2022)/' } | Sort-Object)
                $expectedPackages = if ($unity6) { 'Packages/manifest.json,Packages/packages-lock.json' } else { 'Packages/manifest.json,Packages/packages-lock.json,Tools/Testing/Profiles/Unity2022/manifest.json,Tools/Testing/Profiles/Unity2022/packages-lock.json' }
                Assert-That (($packageFiles -join ',') -ceq $expectedPackages) 'The SDK declaration and lock are not both covered by the initial source fingerprint.'
                Assert-That ('PackageSources/com.kano.ylframework.hybridclr/Dependencies.json' -cin $fixture.identity.files.path) 'The SDK source approval must participate in the initial source fingerprint.'
            }
        }
        foreach ($fault in @('missing-declaration', 'boolean-declaration', 'empty-declaration', 'object-declaration', 'unpinned-declaration',
            'missing-sdk-lock', 'boolean-sdk-lock', 'mismatched-sdk-lock', 'wrong-sdk-source', 'invalid-sdk-hash',
            'unapproved-declaration', 'unapproved-sdk-hash', 'missing-sdk-approval', 'wrong-sdk-approval-version', 'wrong-sdk-approval-commit', 'bundled-sdk-approval')) {
            Test-Case "Distribution HybridCLR rejects $fault explicitly instead of consuming a target-host SDK or coercing values" {
                Assert-Rejected { Invoke-DistributionSdkFixture -EditorVersion '2022.3.62f3' -Fault $fault } 'HybridCLR SDK'
            }
        }
        foreach ($fault in @('sdk-manifest-drift', 'sdk-lock-drift', 'sdk-approval-drift')) {
            Test-Case "Distribution 2022 HybridCLR detects $fault through the real final input check" {
                Assert-Rejected { Invoke-DistributionSdkFixture -EditorVersion '2022.3.62f3' -Fault $fault } 'Distribution source inputs changed'
            }
        }

        Test-Case 'Production Runtime graph rejects undeclared dependencies without a vendor blacklist' {
            $tokens = $null; $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-YLAssemblyLayout.ps1'), [ref]$tokens, [ref]$errors)
            Assert-That ($errors.Count -eq 0) 'Assembly boundary script must parse.'
            $guard = $ast.Find({ param($node) $node -is [Management.Automation.Language.IfStatementAst] -and $node.Clauses[0].Item1.Extent.Text -ceq '$Group -in @(''Player'', ''All'')' }, $true)
            Assert-That ($null -ne $guard) 'The production Player graph guard is missing.'
            $body = [scriptblock]::Create(($guard.Clauses[0].Item2.Statements.Extent.Text -join "`n"))
            $allowed = @('mscorlib', 'netstandard', 'System.Core', 'UnityEngine.CoreModule', 'UnityEngine.UI', 'Unity.Addressables', 'Unity.ResourceManager', 'Unity.Profiling.Core', 'Microsoft.Win32.Primitives', 'Microsoft.Win32.Primitives.dll', 'Newtonsoft.Json.dll')
            foreach ($extra in @('', 'ThirdParty.Runtime', 'MiniExcel.dll', 'BouncyCastle.Cryptography.dll', 'Microsoft.Win32.Unrelated.dll', 'Microsoft.Win32.Primitives.Extra', 'Microsoft.Win32.Primitives.Extra.dll', 'UnityEditor.CoreModule', 'YLFramework.Editor', 'YLFramework.Tests.PlayMode', 'YLFramework.Runtime.Extra', 'Unity.Addressables.Editor', 'Unity.ResourceManager.Tests', 'Unity.Profiling.Core.Editor', 'Unity.Unrelated')) {
                $graph = [pscustomobject]@{ player = @([pscustomobject]@{ name = 'YLFramework.Runtime'; sources = @('PackageSources/com.kano.ylframework/Runtime/Product.cs'); references = $allowed + @($extra | Where-Object { $_ }); precompiled = @() }) }
                $violations = @(& { param($graph, $body) $failures = [Collections.Generic.List[string]]::new(); . $body; $failures.ToArray() } $graph $body)
                Assert-That (($violations.Count -eq 0) -eq ($extra -ceq '')) "Runtime graph dependency admission is wrong for '$extra'."
            }
        }

        Test-Case 'Unity response identity rejects boolean and noninteger wire fields' {
            $request = [pscustomobject]@{ hostId = 'host'; inputHash = 'hash'; action = 'Run'; mode = 'EditMode' }
            $valid = @{ schemaVersion = 1; hostId = 'host'; inputHash = 'hash'; status = 'Passed'; unityVersion = '2022.3.62f3' }
            $response = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $valid)
            Assert-YLUnityResponseIdentity -Response $response -Request $request -UnityVersion '2022.3.62f3'
            foreach ($field in @('schemaVersion', 'hostId', 'inputHash', 'status', 'unityVersion')) {
                $invalid = $valid.Clone()
                $invalid[$field] = $true
                $response = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $invalid)
                Assert-Rejected { Assert-YLUnityResponseIdentity -Response $response -Request $request -UnityVersion '2022.3.62f3' } 'identity|type|mismatch'
            }
            foreach ($action in @('Discover', 'Run', 'PrepareSamples')) {
                $request.action = $action
                $expected = switch -CaseSensitive ($action) { Discover { 'Completed' }; Run { 'Passed' }; PrepareSamples { 'Prepared' } }
                foreach ($status in @('Completed', 'Passed', 'Prepared', 'Failed')) {
                    $candidate = $valid.Clone(); $candidate.status = $status
                    $response = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $candidate)
                    if ($status -ceq $expected) { Assert-YLUnityResponseIdentity -Response $response -Request $request -UnityVersion '2022.3.62f3' }
                    else { Assert-Rejected { Assert-YLUnityResponseIdentity -Response $response -Request $request -UnityVersion '2022.3.62f3' } 'identity|mismatch' }
                }
            }
            $request.action = 'Unknown'
            Assert-Rejected { Assert-YLUnityResponseIdentity -Response $response -Request $request -UnityVersion '2022.3.62f3' } 'Unknown native request action'
        }

        Test-Case 'Unity selections cannot silently drop an entire declared execution mode' {
            $selection = [pscustomobject]@{ tests = @([pscustomobject]@{ mode = 'EditMode' }) }
            $both = [pscustomobject]@{ name = 'Fixture' }
            Assert-Rejected { Assert-YLUnitySelectionModes -Selection $selection -Definition $both -Suite Daily } 'mode'
            $editOnly = [pscustomobject]@{ name = 'Fixture'; modes = @('EditMode') }
            Assert-YLUnitySelectionModes -Selection $selection -Definition $editOnly -Suite Daily
            Assert-YLUnitySelectionModes -Selection $selection -Definition $both -Suite Special
            $specialBoth = [pscustomobject]@{ name = 'Fixture'; specialModes = @('EditMode', 'PlayMode') }
            Assert-Rejected { Assert-YLUnitySelectionModes -Selection $selection -Definition $specialBoth -Suite Special } 'mode'
        }

        $validXml = '<test-run result="Passed" total="2" passed="2" failed="0" skipped="0" inconclusive="0"><test-suite type="Assembly" name="Sample.Tests.dll" result="Passed"><test-case fullname="Example.A" result="Passed"/><test-case fullname="Example.B" result="Passed"/></test-suite></test-run>'

        Test-Case 'Player smoke requires the exact run, host, real process and complete behavior stages' {
            $fixture = New-ResultFixture
            $process = $fixture.process | Select-Object *, @{ Name = 'pid'; Expression = { 42 } }
            $path = Join-Path $fixture.root 'player.json'
            $smokeIdentity = @{ InputHash = ('c' * 64); ContentHash = ('d' * 64); AddressablesVersion = '1.25.0' }
            $valid = @{
                schemaVersion = 2; purpose = 'YLTesting.PlayerSmoke'; status = 'Passed'
                resultId = 'result-1'; hostId = 'host-1'; unityVersion = '2022.3.62f3'; pid = 42
                isEditor = $false; platform = 'WindowsPlayer'; frames = 2; error = ''; backend = 'Mono'
                inputHash = $smokeIdentity.InputHash; contentHash = $smokeIdentity.ContentHash; addressablesVersion = $smokeIdentity.AddressablesVersion
                ledgerMeasured = $true; resourceState = 'Closed'; cleanupErrors = @()
                leaseCount = 0; scopeCount = 0; requestCount = 0; pendingWorkCount = 0; drainingHandleCount = 0
                stages = @('Started', 'ResourceLoaded', 'EventLifecycle', 'BusyOwnerProtected', 'ResourceReleased', 'Shutdown')
            }
            Write-YLTestJson -Path $path -Value $valid
            $accepted = Read-YLPlayerSmoke -Path $path -ResultId 'result-1' -HostId 'host-1' -UnityVersion '2022.3.62f3' -ProcessResult $process @smokeIdentity
            Assert-That ($accepted.status -ceq 'Passed') 'A complete matching Player run was not accepted.'
            foreach ($change in @(
                @{ resultId = 'stale' }, @{ hostId = 'foreign' }, @{ unityVersion = '6000.3.27f1' }, @{ pid = 43 }, @{ pid = '42' },
                @{ resultId = $true }, @{ hostId = $true }, @{ unityVersion = $true }, @{ purpose = $true }, @{ status = $true },
                @{ schemaVersion = $true }, @{ schemaVersion = 1 }, @{ schemaVersion = '2' }, @{ platform = $true },
                @{ isEditor = $true }, @{ isEditor = 'false' }, @{ platform = 'WindowsEditor' },
                @{ backend = 'IL2CPP' }, @{ backend = '' }, @{ backend = $true },
                @{ frames = 0 }, @{ frames = '2' }, @{ status = 'Failed' }, @{ error = 'runtime error' },
                @{ scopeCount = 1 }, @{ requestCount = 1 }, @{ pendingWorkCount = 1 }, @{ drainingHandleCount = 1 },
                @{ leaseCount = '0' }, @{ leaseCount = 1 }, @{ ledgerMeasured = $false }, @{ ledgerMeasured = 'true' },
                @{ resourceState = 'Running' }, @{ inputHash = ('e' * 64) }, @{ contentHash = ('f' * 64) }, @{ addressablesVersion = '2.10.3' },
                @{ cleanupErrors = @('release failed') }, @{ stages = @('Started', 'Shutdown') },
                @{ stages = @('Started', 'ResourceLoaded', 'EventLifecycle', 'BusyOwnerProtected', 'ResourceReleased', 'Shutdown', 'Shutdown') }
                @{ stages = @($true, $true, $true, $true, $true, $true) }
            )) {
                $invalid = $valid.Clone()
                foreach ($entry in $change.GetEnumerator()) { $invalid[$entry.Key] = $entry.Value }
                [IO.File]::WriteAllText($path, (ConvertTo-Json -InputObject $invalid -Depth 10))
                Assert-Rejected { Read-YLPlayerSmoke -Path $path -ResultId 'result-1' -HostId 'host-1' -UnityVersion '2022.3.62f3' -ProcessResult $process @smokeIdentity } 'smoke|schema2'
            }
            $native = $valid.Clone()
            $native.backend = 'IL2CPP'
            [IO.File]::WriteAllText($path, (ConvertTo-Json -InputObject $native -Depth 10))
            $acceptedNative = Read-YLPlayerSmoke -Path $path -ResultId 'result-1' -HostId 'host-1' -UnityVersion '2022.3.62f3' -Backend IL2CPP -ProcessResult $process @smokeIdentity
            Assert-That ($acceptedNative.backend -ceq 'IL2CPP') 'The actual native backend was not accepted.'
            [IO.File]::WriteAllText($path, (ConvertTo-Json -InputObject $valid -Depth 10))
            $process.exitCode = 1
            Assert-Rejected { Read-YLPlayerSmoke -Path $path -ResultId 'result-1' -HostId 'host-1' -UnityVersion '2022.3.62f3' -ProcessResult $process @smokeIdentity } 'process|exit'
            $process.exitCode = 0
            $process.forcedCleanup = $true
            Assert-Rejected { Read-YLPlayerSmoke -Path $path -ResultId 'result-1' -HostId 'host-1' -UnityVersion '2022.3.62f3' -ProcessResult $process @smokeIdentity } 'process'
        }

        Test-Case 'HybridCLR smoke requires real native mode and the exact metadata/code/sample evidence' {
            $fixture = New-ResultFixture
            $process = $fixture.process | Select-Object *, @{ Name = 'pid'; Expression = { 42 } }
            $path = Join-Path $fixture.root 'hybrid.json'
            $smokeIdentity = @{ InputHash = ('c' * 64); ContentHash = ('d' * 64); AddressablesVersion = '1.25.0' }
            $payloads = @(@('Metadata', 'mscorlib'), @('Code', 'Hot.Entry'), @('Code', 'Hot.Support') | ForEach-Object {
                [pscustomobject]@{ role = $_[0]; name = $_[1]; address = ('hybridclr/' + $_[1] + '.bytes'); sha256 = ('e' * 64); length = 4L; mvid = 'e8f0678f-3d6b-474a-b18c-8d68944b857b'; fullName = ($_[1] + ', Version=1.0.0.0, Culture=neutral, PublicKeyToken=null') }
            })
            $expected = [pscustomobject]@{ codePackageId = ('a' * 64); nativeVariant = 'Official2022'; metadataAssemblies = @('mscorlib'); assemblies = @('Hot.Entry', 'Hot.Support'); sampleVersion = 'v1'; manifestAddress = 'hybridclr/manifest'; manifestSha256 = ('f' * 64); payloads = $payloads }
            $detail = @{
                mode = 'Native'; isSimulation = $false; codePackageId = $expected.codePackageId; nativeVariant = 'Official2022'
                metadataAssemblies = @('mscorlib'); assemblies = @('Hot.Entry', 'Hot.Support'); reflectionCount = 3
                message = 'v1: YL Resource configuration; dependency-count=2'; asyncResult = 'v1-async'; prefabMarker = 'v1-prefab'
                repeatedLoadReused = $true; wrongBaseRejected = $true
                sampleVersion = $expected.sampleVersion; manifestSchemaVersion = 2; manifestAddress = $expected.manifestAddress; manifestSha256 = $expected.manifestSha256
                payloads = $payloads
            }
            $valid = @{
                schemaVersion = 2; purpose = 'YLTesting.PlayerSmoke'; status = 'Passed'; backend = 'IL2CPP'
                resultId = 'result-1'; hostId = 'host-1'; unityVersion = '2022.3.62f3'; pid = 42; isEditor = $false
                platform = 'WindowsPlayer'; graphicsDevice = 'Direct3D11'; frames = 2; error = ''; leaseCount = 0
                scopeCount = 0; requestCount = 0; pendingWorkCount = 0; drainingHandleCount = 0
                ledgerMeasured = $true; resourceState = 'Closed'; cleanupErrors = @()
                inputHash = $smokeIdentity.InputHash; contentHash = $smokeIdentity.ContentHash; addressablesVersion = $smokeIdentity.AddressablesVersion
                stages = @('Started', 'HybridCLRMetadata', 'HybridCLRModule', 'HybridCLRReflection', 'HybridCLRShutdown'); startupCatalogUpdated = $false; hybridClr = $detail
            }
            Write-YLTestJson -Path $path -Value $valid
            $accepted = Read-YLPlayerSmoke -Path $path -ResultId 'result-1' -HostId 'host-1' -UnityVersion '2022.3.62f3' -Backend IL2CPP -HybridCLR $expected -ProcessResult $process @smokeIdentity
            Assert-That ($accepted.hybridClr.mode -ceq 'Native') 'A matching native HybridCLR run was not accepted.'
            foreach ($change in @(
                @{ mode = 'EditorSimulation' }, @{ isSimulation = $true }, @{ codePackageId = ('b' * 64) },
                @{ nativeVariant = 'foreign' }, @{ metadataAssemblies = @() }, @{ metadataAssemblies = @('other') },
                @{ assemblies = @('Hot.Entry', 'Hot.Entry') }, @{ reflectionCount = 0 }, @{ message = 'simulated' },
                @{ asyncResult = 'v2-async' }, @{ prefabMarker = 'missing' }, @{ repeatedLoadReused = $false }, @{ wrongBaseRejected = $false }
                @{ sampleVersion = 'v2' }, @{ manifestSchemaVersion = 1 }, @{ manifestSha256 = ('a' * 64) }, @{ payloads = @() }
                @{ mode = $true }, @{ nativeVariant = $true }, @{ message = $true }, @{ asyncResult = $true }, @{ prefabMarker = $true }
            )) {
                $invalid = $detail.Clone()
                foreach ($entry in $change.GetEnumerator()) { $invalid[$entry.Key] = $entry.Value }
                $valid.hybridClr = $invalid
                [IO.File]::WriteAllText($path, (ConvertTo-Json -InputObject $valid -Depth 10))
                Assert-Rejected { Read-YLPlayerSmoke -Path $path -ResultId 'result-1' -HostId 'host-1' -UnityVersion '2022.3.62f3' -Backend IL2CPP -HybridCLR $expected -ProcessResult $process @smokeIdentity } 'HybridCLR|native'
            }
            $valid.hybridClr = $detail
            $valid.graphicsDevice = 'Null'
            [IO.File]::WriteAllText($path, (ConvertTo-Json -InputObject $valid -Depth 10))
            Assert-Rejected { Read-YLPlayerSmoke -Path $path -ResultId 'result-1' -HostId 'host-1' -UnityVersion '2022.3.62f3' -Backend IL2CPP -HybridCLR $expected -ProcessResult $process @smokeIdentity } 'HybridCLR'
        }

        Test-Case 'input identity is deterministic and includes metadata, toolchain and profile' {
            $fixture = New-ResultFixture
            $again = Get-YLTestInputIdentity -Repository $fixture.root -Paths @('TestSuites.json', 'Product.cs.meta', 'Product.cs') -Toolchain @((Join-Path $fixture.root 'tool.dll')) -Profile 'Mono'
            Assert-That ($fixture.identity.sha256 -ceq $again.sha256) 'Input ordering changed a deterministic identity.'
            foreach ($relative in @('Product.cs', 'Product.cs.meta', 'TestSuites.json', 'tool.dll')) {
                $path = Join-Path $fixture.root $relative
                $old = [IO.File]::ReadAllText($path)
                [IO.File]::WriteAllText($path, $old + 'changed')
                $changed = Get-YLTestInputIdentity -Repository $fixture.root -Paths $fixture.paths -Toolchain @((Join-Path $fixture.root 'tool.dll')) -Profile 'Mono'
                Assert-That ($changed.sha256 -cne $fixture.identity.sha256) "Input drift was missed: $relative"
                Assert-Rejected { Assert-YLTestSelection -Selection $fixture.selection -InputIdentity $changed } 'input|stale|profile'
                [IO.File]::WriteAllText($path, $old)
            }
            $profile = Get-YLTestInputIdentity -Repository $fixture.root -Paths $fixture.paths -Toolchain @((Join-Path $fixture.root 'tool.dll')) -Profile 'Unity-2022'
            Assert-Rejected { Assert-YLTestSelection -Selection $fixture.selection -InputIdentity $profile } 'input|stale|profile'
            [IO.File]::WriteAllText((Join-Path $fixture.root 'NewTest.cs'), 'class NewTest {}')
            $added = Get-YLTestInputIdentity -Repository $fixture.root -Paths ($fixture.paths + 'NewTest.cs') -Toolchain @((Join-Path $fixture.root 'tool.dll')) -Profile 'Mono'
            Assert-Rejected { Assert-YLTestSelection -Selection $fixture.selection -InputIdentity $added } 'input|stale|profile'
        }

        Test-Case 'empty, duplicate and non-runnable discovered selections are rejected' {
            $fixture = New-ResultFixture
            Assert-Rejected { New-YLTestSelection -InputIdentity $fixture.identity -Tests @() } 'empty|zero'
            Assert-Rejected { New-YLTestSelection -InputIdentity $fixture.identity -Tests @($fixture.selection.tests[0], $fixture.selection.tests[0]) } 'duplicate'
            $ignored = [pscustomobject]@{ assembly = 'Sample.Tests'; fullName = 'Example.Ignored'; mode = 'EditMode'; runState = 'Ignored' }
            Assert-Rejected { New-YLTestSelection -InputIdentity $fixture.identity -Tests @($ignored) } 'runnable|skip'
        }

        Test-Case 'strict result validation accepts exact leaves with a genuine ordinary process outcome' {
            $fixture = New-ResultFixture
            $path = Join-Path $fixture.root 'results.xml'
            [IO.File]::WriteAllText($path, $validXml)
            $result = Read-YLTestXml -Path $path -Selection $fixture.selection -InputIdentity $fixture.identity -Mode EditMode -ProcessResult $fixture.process
            Assert-That ($result.status -ceq 'Passed' -and $result.total -eq 2 -and $result.passed -eq 2) 'Exact real test leaves were not accepted.'
            [IO.File]::WriteAllText($path, $validXml.Replace('<test-run', '<test-suite type="Assembly" name="Sample.Tests.exe"').Replace('</test-run>', '</test-suite>'))
            $offline = Read-YLTestXml -Path $path -Selection $fixture.selection -InputIdentity $fixture.identity -Mode EditMode -ProcessResult $fixture.process
            Assert-That ($offline.total -eq 2) 'NUnit assembly-root XML must be supported, not just UTF test-run roots.'
            [IO.File]::WriteAllText($path, $validXml.Replace('<test-run', '<test-suite type="TestSuite" name="OwnedHost"').Replace('</test-run>', '</test-suite>'))
            $utf = Read-YLTestXml -Path $path -Selection $fixture.selection -InputIdentity $fixture.identity -Mode EditMode -ProcessResult $fixture.process
            Assert-That ($utf.total -eq 2) 'The real UTF ITestResultAdaptor TestSuite root must retain strict leaf validation.'
            [IO.File]::WriteAllText($path, $validXml.Replace('<test-run', '<test-suite type="TestFixture" name="InvalidRoot"').Replace('</test-run>', '</test-suite>'))
            Assert-Rejected { Read-YLTestXml -Path $path -Selection $fixture.selection -InputIdentity $fixture.identity -Mode EditMode -ProcessResult $fixture.process } 'root'
        }

        Test-Case 'missing, extra, duplicate and wrong-assembly XML leaves are rejected' {
            $fixture = New-ResultFixture
            $path = Join-Path $fixture.root 'results.xml'
            $invalid = @(
                $validXml.Replace('<test-case fullname="Example.B" result="Passed"/>', ''),
                $validXml.Replace('</test-suite>', '<test-case fullname="Example.C" result="Passed"/></test-suite>'),
                $validXml.Replace('Example.B', 'Example.A'),
                $validXml.Replace('Sample.Tests.dll', 'Other.Tests.dll')
            )
            foreach ($xml in $invalid) {
                [IO.File]::WriteAllText($path, $xml)
                Assert-Rejected { Read-YLTestXml -Path $path -Selection $fixture.selection -InputIdentity $fixture.identity -Mode EditMode -ProcessResult $fixture.process } 'selection|duplicate|count|missing|extra|assembly'
            }
        }

        Test-Case 'unapproved skip, suite failure and dishonest counters cannot pass' {
            $fixture = New-ResultFixture
            $path = Join-Path $fixture.root 'results.xml'
            $invalid = @(
                $validXml.Replace('fullname="Example.B" result="Passed"', 'fullname="Example.B" result="Skipped"'),
                $validXml.Replace('<test-run result="Passed"', '<test-run result="Failed"'),
                $validXml.Replace('name="Sample.Tests.dll" result="Passed"', 'name="Sample.Tests.dll" result="Failed"'),
                $validXml.Replace('passed="2"', 'passed="1"'),
                '<test-run result="Passed" total="0" passed="0" failed="0" skipped="0" inconclusive="0"/>'
            )
            foreach ($xml in $invalid) {
                [IO.File]::WriteAllText($path, $xml)
                Assert-Rejected { Read-YLTestXml -Path $path -Selection $fixture.selection -InputIdentity $fixture.identity -Mode EditMode -ProcessResult $fixture.process } 'skip|failed|result|count|zero'
            }
        }

        Test-Case 'incomplete and DTD-bearing XML is rejected without external entity resolution' {
            $fixture = New-ResultFixture
            $path = Join-Path $fixture.root 'results.xml'
            foreach ($xml in @('', '<test-run result="Passed">', ('<!DOCTYPE test-run [<!ENTITY outside SYSTEM "file:///not-owned">]>' + $validXml))) {
                [IO.File]::WriteAllText($path, $xml)
                Assert-Rejected { Read-YLTestXml -Path $path -Selection $fixture.selection -InputIdentity $fixture.identity -Mode EditMode -ProcessResult $fixture.process } 'XML|empty|incomplete|DTD'
            }
        }

        Test-Case 'good XML cannot mask timeout, cancellation, forced cleanup or observation failures' {
            $fixture = New-ResultFixture
            $path = Join-Path $fixture.root 'results.xml'
            [IO.File]::WriteAllText($path, $validXml)
            foreach ($change in @(
                @{ status = 'TimedOut' }, @{ status = 'ObserverLost' }, @{ exitCode = 17 }, @{ observedExit = $false },
                @{ timedOut = $true }, @{ cancelled = $true }, @{ forcedCleanup = $true },
                @{ observerError = 'lost' }, @{ cleanupError = 'child alive' }, @{ activeProcesses = 1 },
                @{ activeProcesses = $null }, @{ activeProcesses = '0' }, @{ activeProcesses = $false },
                @{ streamsDrained = $false }, @{ jobAssigned = $false }
            )) {
                $process = $fixture.process | Select-Object *
                foreach ($entry in $change.GetEnumerator()) { $process.($entry.Key) = $entry.Value }
                Assert-Rejected { Read-YLTestXml -Path $path -Selection $fixture.selection -InputIdentity $fixture.identity -Mode EditMode -ProcessResult $process } 'process|exit|observer|cleanup'
            }
        }

        Test-Case 'rotation never adopts boolean summary identity or status as a success' {
            $workspace = Initialize-YLTestWorkspace -WorkspaceRoot (Join-Path $sandbox 'TypedRotation') -Repository $repository
            foreach ($field in @('ownerId', 'schemaVersion', 'status', 'action', 'endUtc')) {
                $record = New-RotationFixture $workspace 'Passed' 1
                $path = Join-Path $record.path 'summary.json'
                $summary = [IO.File]::ReadAllText($path) | ConvertFrom-Json -AsHashtable -DateKind String
                $summary[$field] = $true
                [IO.File]::WriteAllText($path, (ConvertTo-Json -InputObject $summary))
                $candidate = @(Get-YLTestRotation -Workspace $workspace -Key 'Daily-Reddot-Mono' | Where-Object { $_.path -ceq $record.path })[0]
                Assert-That (-not $candidate.canRemove -and $candidate.reason.StartsWith('Excluded:')) "A boolean $field was classified as an ordinary result."
            }
        }

        Test-Case 'rotation excludes old results containing another workspace' {
            $outer = Initialize-YLTestWorkspace (Join-Path $sandbox 'NestedRotation') $repository
            $old = New-RotationFixture $outer 'Passed' 0
            $ordinary = New-RotationFixture $outer 'Passed' 1
            $newest = New-RotationFixture $outer 'Passed' 2
            $child = New-LegacyWorkspaceFixture (Join-Path $old.path 'Workspace') $repository
            $pin = New-YLTestDirectory $child Result 'ResourceAAIL2CPP'
            $evidence = Join-Path $pin.path '.pinned'
            [IO.File]::WriteAllText($evidence, 'keep child evidence')

            $plan = @(Get-YLTestRotation $outer 'Daily-Reddot-Mono')
            $removed = @(Invoke-YLTestRotation $outer 'Daily-Reddot-Mono')
            Assert-That ([IO.File]::Exists($evidence) -and [IO.File]::ReadAllText($evidence) -ceq 'keep child evidence') 'Rotation deleted evidence inside an existing nested workspace.'
            $excluded = @($plan | Where-Object { $_.path -ceq $old.path -and -not $_.canRemove -and $_.reason -match 'nested workspace' })
            Assert-That ($excluded.Count -eq 1) 'The rotation preview did not exclude the nested workspace.'
            Assert-That ($removed.Count -eq 1 -and $removed[0].path -ceq $ordinary.path -and
                -not [IO.Directory]::Exists($ordinary.path) -and [IO.Directory]::Exists($newest.path)) 'The nested workspace changed ordinary rotation.'
        }

        Test-Case 'rotation retains one ordinary success and three failures including accepted outcomes' {
            $workspace = Initialize-YLTestWorkspace -WorkspaceRoot (Join-Path $sandbox 'Rotation') -Repository $repository
            $success = @(0..4 | ForEach-Object { New-RotationFixture $workspace 'Passed' $_ })
            $failed = @(0..4 | ForEach-Object { New-RotationFixture $workspace 'Failed' (10 + $_) })
            $accepted = New-RotationFixture $workspace 'Accepted' 20
            $pinned = New-RotationFixture $workspace 'Passed' 30
            [IO.File]::WriteAllText((Join-Path $pinned.path '.pinned'), 'explicit retention')
            $active = New-RotationFixture $workspace 'Failed' 40
            $unknown = Join-Path ([IO.Path]::GetDirectoryName($active.path)) 'Unknown'
            $null = [IO.Directory]::CreateDirectory($unknown)
            [IO.File]::WriteAllText((Join-Path $unknown 'sentinel.txt'), 'preserve')
            $lease = Lock-YLTestDirectory -Workspace $workspace -Path $active.path
            try {
                $rotation = @(Get-YLTestRotation -Workspace $workspace -Key 'Daily-Reddot-Mono')
                $candidates = @($rotation | Where-Object { $_.canRemove })
                Assert-That ($candidates.Count -eq 7) ('The 1/3 rotation buckets or protected output accounting are wrong. ' + ($rotation | ConvertTo-Json -Depth 5 -Compress))
                foreach ($run in $success + $failed + @($accepted, $pinned, $active)) {
                    Assert-That ([IO.Directory]::Exists($run.path)) 'Rotation planning must be read-only.'
                }
                $removed = @(Invoke-YLTestRotation -Workspace $workspace -Key 'Daily-Reddot-Mono')
                Assert-That ($removed.Count -eq 7) 'The shared retention entry did not remove exactly the eligible obsolete outputs.'
                foreach ($path in @($success[4].path, $failed[3].path, $failed[4].path, $accepted.path, $pinned.path, $active.path, $unknown)) {
                    Assert-That ([IO.Directory]::Exists($path)) 'A retained/protected result was removed.'
                }
                foreach ($run in @($success[0], $success[1], $success[2], $success[3], $failed[0], $failed[1], $failed[2])) {
                    Assert-That (-not [IO.Directory]::Exists($run.path)) 'An eligible obsolete result was not removed.'
                }
                Assert-That (@($rotation | Where-Object { -not $_.canRemove -and $_.path -eq $unknown }).Count -eq 1) 'Unknown outputs need an explicit exclusion.'
            } finally { $lease.Dispose() }
        }
    }
    if ($cases -eq 0) { throw "No implemented test group selected: $Group" }
    if ($failures.Count -ne 0) { throw "$($failures.Count)/$cases tooling contracts failed. $($failures -join '; ')" }
    "YL testing contracts: $cases passed. NOT Unity, PlayMode or Player acceptance."
}
finally {
    foreach ($link in $links) { if ([IO.Directory]::Exists($link)) { [IO.Directory]::Delete($link, $false) } }
    if ($KeepArtifacts) { "Artifacts retained: $sandbox" }
    else { Remove-Item -LiteralPath $sandbox -Recurse -Force }
}
