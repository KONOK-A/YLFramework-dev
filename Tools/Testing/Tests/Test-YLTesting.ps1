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

function New-RotationFixture([object] $Workspace, [string] $Status, [int] $Minute) {
    $run = New-YLTestDirectory -Workspace $Workspace -Kind Result -Key 'Daily-Reddot-Mono'
    Write-YLTestJson -Path (Join-Path $run.path 'summary.json') -Value @{
        schemaVersion = 1; ownerId = $run.id; action = 'Run'; status = $Status
        suite = 'Daily'; module = 'Reddot'; profile = 'Mono'
        endUtc = ([DateTimeOffset]'2026-10-01T00:00:00Z').AddMinutes($Minute).ToString('o')
    }
    return $run
}

try {
    if ($Group -in @('All', 'Workspace')) {
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
            foreach ($name in @('Excel', 'Domain')) {
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
            foreach ($name in @('Data', 'ExcelGameplay', 'Domain', 'MigrationFixtures')) {
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
            Assert-That ((@($consumers.name | Sort-Object) -join ',') -ceq 'Behavior,MigrationFixtures,Movement2D') 'Only actual movement consumers may install the package.'
            Assert-That ($movement.sortingLayer -ceq '' -and $movement.prepareSessionScene -and $movement.prepareSceneTemplates) 'Movement Player authoring requires a real owned empty scene.'
            $producer = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Assets/Tests/YLFramework/EditMode/Editor/MigrationFixtures/YLFramework.MigrationFixtures.Tests.EditMode.asmdef')
            foreach ($reference in @('YLFramework.Character2D', 'YLFramework.Platform2D')) {
                Assert-That (@($producer.references) -ccontains $reference) "Historical producer compilation lost an actual movement consumer: $reference"
            }
            $edit = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Assets/Tests/YLFramework/EditMode/YLFramework.Tests.EditMode.asmdef')
            Assert-That (@($edit.references) -cnotcontains 'YLFramework.Movement2D.Acceptance') 'The standalone probe reference cannot leak to every shared test consumer.'
            $probe = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Assets/Tests/YLFramework/Fixtures/Movement2D/Runtime/YLFramework.Movement2D.Acceptance.asmdef')
            Assert-That (($probe.references -join ',') -ceq 'YLFramework.Character2D,YLFramework.Platform2D') 'The probe must not declare unrelated products or UI dependencies to repair an implicit Unity graph reference.'
        }
        Test-Case 'Configured FSM standalone keeps its exact two-package closure and real reload scene preconditions' {
            $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Tools/Testing/TestSuites.json')
            $fsm = @($configuration.modules | Where-Object name -CEQ 'StateMachine')[0]
            Assert-That (@($fsm.packageInputs).Count -eq 1 -and $fsm.packageInputs[0] -ceq 'com.kano.ylframework.state-machine') 'The standalone configured FSM root must be declared exactly.'
            Assert-That ($fsm.Contains('sortingLayer') -and $fsm.sortingLayer -ceq '') 'The owned native host requires an explicit no-sorting-layer declaration.'
            $consumers = @($configuration.modules | Where-Object { $_.Contains('packageInputs') -and @($_.packageInputs) -ccontains 'com.kano.ylframework.state-machine' })
            Assert-That ((@($consumers.name | Sort-Object) -join ',') -ceq 'Behavior,MigrationFixtures,StateMachine') 'Only actual FSM consumers may declare this optional package.'
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
        Test-Case 'Historical migration producer declares its current compile owners without recapturing old assets' {
            $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Tools/Testing/TestSuites.json')
            $module = @($configuration.modules | Where-Object name -CEQ 'MigrationFixtures')[0]
            Assert-That (@($module.packageInputs) -ccontains 'com.kano.ylframework.state-machine') 'MigrationFixtures copies real FSM consumers but omits their installation input.'
            $assembly = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Assets/Tests/YLFramework/EditMode/Editor/MigrationFixtures/YLFramework.MigrationFixtures.Tests.EditMode.asmdef')
            foreach ($reference in @('YLFramework.StateMachine', 'YLFramework.StateMachine.Editor')) {
                Assert-That (@($assembly.references) -ccontains $reference) "MigrationFixtures is missing its actual compile reference: $reference"
            }
            $source = [IO.File]::ReadAllText((Join-Path $repoRoot 'Assets/Tests/YLFramework/EditMode/Editor/MigrationFixtures/AssemblyMigrationFixtureFreezeTests.cs'))
            Assert-That ([regex]::Matches($source, 'typeof\(YLFramework[.]Editor[.]StateMachine[.]FsmEditorWindow\)[.]Assembly[.]GetType').Count -eq 2) 'Both FSM factory lookups need their real Editor owner, not the Resource Editor assembly.'
            Assert-That ($module.description.Contains('Historical pre-split') -and $module.description.Contains('sourceCommit')) 'The historical producer must state its frozen source revision boundary.'
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
            Assert-That (($module.assemblies -join ',') -ceq 'YLFramework.Actions.DOTween.Tests.EditMode,YLFramework.Actions.DOTween.Tests.PlayMode') 'Both original test assemblies must be selected.'
            $action = @($configuration.modules | Where-Object name -CEQ 'Action')[0]
            Assert-That (-not $action.Contains('nativeSdk')) 'Ordinary Action must remain independent of SDK preparation.'
            foreach ($side in @('EditMode', 'PlayMode')) {
                $assembly = Read-YLPackageEvidenceJson (Join-Path $repoRoot "Assets/Tests/YLFramework/Integrations/DOTween/$side/YLFramework.Actions.DOTween.Tests.$side.asmdef")
                Assert-That (($assembly.defineConstraints -join ',') -ceq 'DOTWEEN,UNITY_INCLUDE_TESTS' -and $assembly.versionDefines.Count -eq 0) 'The original tests must not silently disappear behind a retired package presence gate.'
            }
        }
        Test-Case 'Domain authoring tests declare Save without adding it to main-only modules' {
            $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
            $configuration = Read-YLPackageEvidenceJson (Join-Path $repoRoot 'Tools/Testing/TestSuites.json')
            $domain = @($configuration.modules | Where-Object name -CEQ 'Domain')[0]
            Assert-That (@($domain.packageInputs) -ccontains 'com.kano.ylframework.save') 'Domain copies the DataPlayerProbe Save consumer but omits its declared installation input.'
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
        function Invoke-NativePackageFixture([string] $EditorVersion = '2022.3.62f3', [string] $Fault = '') {
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
                'Packages/packages-lock.json' = '{"dependencies":{"com.unity.nuget.newtonsoft-json":{"version":"3.2.2"}}}'
                'Tools/Testing/Profiles/Unity2022/manifest.json' = '{"dependencies":{"com.unity.modules.jsonserialize":"1.0.0","com.unity.modules.audio":"1.0.0","com.unity.render-pipelines.universal":"14.0.12"}}'
                'Tools/Testing/Profiles/Unity2022/packages-lock.json' = '{"dependencies":{"com.unity.nuget.newtonsoft-json":{"version":"3.2.1"}}}'
                'Unity.exe' = 'native-editor-fixture'
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
                $tests = @(
                    @{ assembly = 'Fixture.Tests'; fullName = 'Fixture.Edit'; mode = 'EditMode'; runState = 'Runnable' },
                    @{ assembly = 'Fixture.Tests'; fullName = 'Fixture.Play'; mode = 'PlayMode'; runState = 'Runnable' }
                )
                Write-YLTestJson -Path (Join-Path $request.output 'response.json') -Value @{
                    schemaVersion = 1; hostId = $request.hostId; inputHash = $request.inputHash; unityVersion = $editor.version
                    status = $(if ($request.action -ceq 'Run') { 'Passed' } else { 'Completed' }); isBatchMode = $true
                    testFrameworkVersion = $editor.testFramework; runtimeReferences = @(); compiledInputs = @(); excludedTests = @(); tests = $tests
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
                    }
                }
                return [pscustomobject]@{ status = 'Exited'; observedExit = $true; exitCode = 0; timedOut = $false; cancelled = $false
                    forcedCleanup = $false; observerError = $null; cleanupError = $null; jobAssigned = $true; activeProcesses = 0; streamsDrained = $true }
            }
            $summary = [ordered]@{ xml = @() }
            try {
                Invoke-YLUnitySuite -Repository $repository -Definition $definition -EditorProfile $editor -Unity (Join-Path $repository 'Unity.exe') `
                    -WorkspaceRoot $fixtureWorkspace -Action Run -Suite Daily -Module Fixture -TimeoutSeconds 15 -Selection '' -KeepArtifacts $false -Summary $summary
            } catch {
                if ($Fault -cin @('unknown-profile', 'mismatched-profile', 'boolean-profile', 'missing-packages')) {
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

        function Invoke-DistributionSdkFixture([string] $EditorVersion, [string] $Fault = '') {
            $fixtureRoot = Join-Path $sandbox ('DistributionSdk-' + [Guid]::NewGuid().ToString('N'))
            $repository = Join-Path $fixtureRoot 'Repository'
            $null = [IO.Directory]::CreateDirectory($repository)
            foreach ($relative in @('Tools/Testing/DistributionEntry.cs', 'Tools/Testing/PackageCatalogContract.cs', 'Tools/Packaging/PackageCatalog.json', 'Tools/Testing/PlayerSmoke.cs', 'Tools/Testing/HybridCLREntry.cs',
                'Tools/Testing/HybridCLRSmoke.cs', 'Tools/Testing/UnityHost.ps1', 'Tools/Testing/TestSuites.json',
                'Tools/Testing/Common/Workspace.ps1', 'Tools/Testing/Common/Process.ps1', 'Tools/Testing/Common/Results.ps1',
                'Tools/Testing/Tests/Test-YLDistribution.ps1', 'Packages/manifest.json', 'Packages/packages-lock.json',
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
            $configuration = [IO.File]::ReadAllText((Join-Path $repository 'Tools/Testing/TestSuites.json')) | ConvertFrom-Json -DateKind String
            $editor = @($configuration.editors | Where-Object { $_.version -ceq $EditorVersion })[0]
            $Unity = Join-Path $repository 'Unity.exe'
            $tokens = $null; $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-YLDistribution.ps1'), [ref]$tokens, [ref]$errors)
            Assert-That ($errors.Count -eq 0) 'The distribution SDK binding must parse.'
            foreach ($name in @('Get-DistributionSdk', 'Get-DistributionHostPackages')) {
                $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name }, $true)
                Assert-That ($null -ne $function) "The real package preparation function is missing: $name"
                . ([scriptblock]::Create($function.Extent.Text))
            }
            $bare = Get-DistributionHostPackages -Repository $repository -EditorProfile $editor -HybridCLR $false
            $prepared = Get-DistributionHostPackages -Repository $repository -EditorProfile $editor -HybridCLR $true
            $identity = Get-YLTestInputIdentity -Repository $repository -Paths $prepared.inputs -Toolchain @($Unity) -Profile 'Distribution-Sdk-Fixture'
            if ($Fault -ceq 'sdk-manifest-drift') { [IO.File]::AppendAllText($sdkManifestPath, ' ') }
            if ($Fault -ceq 'sdk-lock-drift') { [IO.File]::AppendAllText($sdkLockPath, ' ') }
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
            }
        }
        foreach ($fault in @('missing-declaration', 'boolean-declaration', 'empty-declaration', 'object-declaration', 'unpinned-declaration',
            'missing-sdk-lock', 'boolean-sdk-lock', 'mismatched-sdk-lock', 'wrong-sdk-source', 'invalid-sdk-hash')) {
            Test-Case "Distribution HybridCLR rejects $fault explicitly instead of consuming a target-host SDK or coercing values" {
                Assert-Rejected { Invoke-DistributionSdkFixture -EditorVersion '2022.3.62f3' -Fault $fault } 'HybridCLR SDK'
            }
        }
        foreach ($fault in @('sdk-manifest-drift', 'sdk-lock-drift')) {
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
            $allowed = @('mscorlib', 'netstandard', 'System.Core', 'UnityEngine.CoreModule', 'UnityEngine.UI', 'Microsoft.Win32.Primitives', 'Microsoft.Win32.Primitives.dll', 'Newtonsoft.Json.dll', 'BouncyCastle.Cryptography.dll')
            foreach ($extra in @('', 'ThirdParty.Runtime', 'MiniExcel.dll', 'Microsoft.Win32.Unrelated.dll', 'Microsoft.Win32.Primitives.Extra', 'Microsoft.Win32.Primitives.Extra.dll', 'UnityEditor.CoreModule', 'YLFramework.Editor', 'YLFramework.Tests.PlayMode', 'YLFramework.Runtime.Extra')) {
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
            $valid = @{
                schemaVersion = 1; purpose = 'YLTesting.PlayerSmoke'; status = 'Passed'
                resultId = 'result-1'; hostId = 'host-1'; unityVersion = '2022.3.62f3'; pid = 42
                isEditor = $false; platform = 'WindowsPlayer'; frames = 2; error = ''; backend = 'Mono'
                recordCount = 0; leaseCount = 0
                stages = @('Started', 'ResourceLoaded', 'EventLifecycle', 'BusyOwnerProtected', 'ResourceReleased', 'Shutdown')
            }
            Write-YLTestJson -Path $path -Value $valid
            $accepted = Read-YLPlayerSmoke -Path $path -ResultId 'result-1' -HostId 'host-1' -UnityVersion '2022.3.62f3' -ProcessResult $process
            Assert-That ($accepted.status -ceq 'Passed') 'A complete matching Player run was not accepted.'
            foreach ($change in @(
                @{ resultId = 'stale' }, @{ hostId = 'foreign' }, @{ unityVersion = '6000.3.27f1' }, @{ pid = 43 }, @{ pid = '42' },
                @{ resultId = $true }, @{ hostId = $true }, @{ unityVersion = $true }, @{ purpose = $true }, @{ status = $true },
                @{ schemaVersion = $true }, @{ schemaVersion = '1' }, @{ platform = $true },
                @{ isEditor = $true }, @{ isEditor = 'false' }, @{ platform = 'WindowsEditor' },
                @{ backend = 'IL2CPP' }, @{ backend = '' }, @{ backend = $true },
                @{ frames = 0 }, @{ frames = '2' }, @{ status = 'Failed' }, @{ error = 'runtime error' },
                @{ recordCount = 1 }, @{ recordCount = '0' }, @{ leaseCount = 1 }, @{ stages = @('Started', 'Shutdown') },
                @{ stages = @('Started', 'ResourceLoaded', 'EventLifecycle', 'BusyOwnerProtected', 'ResourceReleased', 'Shutdown', 'Shutdown') }
                @{ stages = @($true, $true, $true, $true, $true, $true) }
            )) {
                $invalid = $valid.Clone()
                foreach ($entry in $change.GetEnumerator()) { $invalid[$entry.Key] = $entry.Value }
                [IO.File]::WriteAllText($path, (ConvertTo-Json -InputObject $invalid -Depth 10))
                Assert-Rejected { Read-YLPlayerSmoke -Path $path -ResultId 'result-1' -HostId 'host-1' -UnityVersion '2022.3.62f3' -ProcessResult $process } 'smoke'
            }
            $native = $valid.Clone()
            $native.backend = 'IL2CPP'
            [IO.File]::WriteAllText($path, (ConvertTo-Json -InputObject $native -Depth 10))
            $acceptedNative = Read-YLPlayerSmoke -Path $path -ResultId 'result-1' -HostId 'host-1' -UnityVersion '2022.3.62f3' -Backend IL2CPP -ProcessResult $process
            Assert-That ($acceptedNative.backend -ceq 'IL2CPP') 'The actual native backend was not accepted.'
            [IO.File]::WriteAllText($path, (ConvertTo-Json -InputObject $valid -Depth 10))
            $process.exitCode = 1
            Assert-Rejected { Read-YLPlayerSmoke -Path $path -ResultId 'result-1' -HostId 'host-1' -UnityVersion '2022.3.62f3' -ProcessResult $process } 'process|exit'
            $process.exitCode = 0
            $process.forcedCleanup = $true
            Assert-Rejected { Read-YLPlayerSmoke -Path $path -ResultId 'result-1' -HostId 'host-1' -UnityVersion '2022.3.62f3' -ProcessResult $process } 'process'
        }

        Test-Case 'HybridCLR smoke requires real native mode and the exact metadata/code/sample evidence' {
            $fixture = New-ResultFixture
            $process = $fixture.process | Select-Object *, @{ Name = 'pid'; Expression = { 42 } }
            $path = Join-Path $fixture.root 'hybrid.json'
            $expected = [pscustomobject]@{ codePackageId = ('a' * 64); nativeVariant = 'Official2022'; metadataAssemblies = @('mscorlib'); assemblies = @('Hot.Entry', 'Hot.Support') }
            $detail = @{
                mode = 'Native'; isSimulation = $false; codePackageId = $expected.codePackageId; nativeVariant = 'Official2022'
                metadataAssemblies = @('mscorlib'); assemblies = @('Hot.Entry', 'Hot.Support'); reflectionCount = 3
                message = 'v1: YL Resource configuration; dependency-count=2'; asyncResult = 'v1-async'; prefabMarker = 'v1-prefab'
                repeatedLoadReused = $true; wrongBaseRejected = $true
            }
            $valid = @{
                schemaVersion = 1; purpose = 'YLTesting.PlayerSmoke'; status = 'Passed'; backend = 'IL2CPP'
                resultId = 'result-1'; hostId = 'host-1'; unityVersion = '2022.3.62f3'; pid = 42; isEditor = $false
                platform = 'WindowsPlayer'; graphicsDevice = 'Direct3D11'; frames = 2; error = ''; recordCount = 0; leaseCount = 0
                stages = @('Started', 'HybridCLRMetadata', 'HybridCLRModule', 'HybridCLRReflection', 'HybridCLRShutdown'); hybridClr = $detail
            }
            Write-YLTestJson -Path $path -Value $valid
            $accepted = Read-YLPlayerSmoke -Path $path -ResultId 'result-1' -HostId 'host-1' -UnityVersion '2022.3.62f3' -Backend IL2CPP -HybridCLR $expected -ProcessResult $process
            Assert-That ($accepted.hybridClr.mode -ceq 'Native') 'A matching native HybridCLR run was not accepted.'
            foreach ($change in @(
                @{ mode = 'EditorSimulation' }, @{ isSimulation = $true }, @{ codePackageId = ('b' * 64) },
                @{ nativeVariant = 'foreign' }, @{ metadataAssemblies = @() }, @{ metadataAssemblies = @('other') },
                @{ assemblies = @('Hot.Entry', 'Hot.Entry') }, @{ reflectionCount = 0 }, @{ message = 'simulated' },
                @{ asyncResult = 'v2-async' }, @{ prefabMarker = 'missing' }, @{ repeatedLoadReused = $false }, @{ wrongBaseRejected = $false }
                @{ mode = $true }, @{ nativeVariant = $true }, @{ message = $true }, @{ asyncResult = $true }, @{ prefabMarker = $true }
            )) {
                $invalid = $detail.Clone()
                foreach ($entry in $change.GetEnumerator()) { $invalid[$entry.Key] = $entry.Value }
                $valid.hybridClr = $invalid
                [IO.File]::WriteAllText($path, (ConvertTo-Json -InputObject $valid -Depth 10))
                Assert-Rejected { Read-YLPlayerSmoke -Path $path -ResultId 'result-1' -HostId 'host-1' -UnityVersion '2022.3.62f3' -Backend IL2CPP -HybridCLR $expected -ProcessResult $process } 'HybridCLR'
            }
            $valid.hybridClr = $detail
            $valid.graphicsDevice = 'Null'
            [IO.File]::WriteAllText($path, (ConvertTo-Json -InputObject $valid -Depth 10))
            Assert-Rejected { Read-YLPlayerSmoke -Path $path -ResultId 'result-1' -HostId 'host-1' -UnityVersion '2022.3.62f3' -Backend IL2CPP -HybridCLR $expected -ProcessResult $process } 'HybridCLR'
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
