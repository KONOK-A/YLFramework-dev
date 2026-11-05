#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param([Parameter(Mandatory)][string] $TemporaryRoot)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Common/Results.ps1')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$controller = Join-Path $PSScriptRoot 'Test-YLDevelopmentHost.ps1'
if (-not [IO.File]::Exists($controller)) { throw 'The explicit owned development-host controller is missing.' }
. $controller -WorkspaceRoot $TemporaryRoot -Unity 'NotExecuted'
$configuration = [IO.File]::ReadAllText((Join-Path $repository 'Tools/Testing/TestSuites.json')) | ConvertFrom-Json -DateKind String
$editor = @($configuration.editors | Where-Object productVersion -CEQ '6000.3.27f1_672d562a42a7')[0]
$definition = Get-YLDevelopmentHostDefinition -Repository $repository -Editor $editor
$checks = 0
foreach ($path in @($controller, $PSCommandPath, (Join-Path $PSScriptRoot 'Test-YLDevelopmentHostEntryNoUnity.ps1'))) {
    $tokens = $null; $errors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
    if ($errors.Count -ne 0) { throw "Invalid PowerShell syntax: $path" }; $checks++
}
if ($definition.sourceProfile -cne 'YLFrameworkDev-6000.3.27f1_672d562a42a7' -or $definition.jsonVersion -cne '3.2.2') { throw 'The source-specific Editor/JSON profile is not exact.' }; $checks++
$unsupported = @($configuration.editors | Where-Object productVersion -CEQ '2022.3.62f3_96770f904ca7')[0]
$rejected = $false
try { $null = Get-YLDevelopmentHostDefinition -Repository $repository -Editor $unsupported } catch { $rejected = $_.Exception.Message.StartsWith('PrerequisiteNotMet:', [StringComparison]::Ordinal) }
if (-not $rejected) { throw 'A Unity 2022 consumer profile must not impersonate the real Unity 6 author scene.' }; $checks++
$inputs = @(Get-YLDevelopmentHostInputs -Repository $repository)
foreach ($required in @('Assets/Scenes/YLFrameworkDev.unity', 'Assets/Scenes/YLFrameworkDev.unity.meta',
    'Assets/Tests/YLFramework/EditMode/Editor/Distribution/FrameworkStructureTests.cs',
    'Assets/Tests/YLFramework/EditMode/Editor/Distribution/DevelopmentHostBoundaryTests.cs',
    'Assets/Tests/YLFramework/Fixtures/NamespaceMigration/ProductMoves.json',
    'Assets/Tests/YLFramework/Infrastructure/Editor/YLTestEntry.cs',
    'ProjectSettings/ProjectSettings.asset', 'ProjectSettings/EditorBuildSettings.asset',
    'ProjectSettings/GraphicsSettings.asset', 'ProjectSettings/QualitySettings.asset',
    'Assets/Settings/UniversalRP.asset', 'Assets/Settings/Renderer2D.asset',
    'Assets/Settings/UniversalRenderPipelineGlobalSettings.asset', 'Packages/manifest.json', 'Packages/packages-lock.json')) {
    if ($required -cnotin $inputs) { throw "A real author input is missing: $required" }; $checks++
}
if (@($inputs | Where-Object { $_ -match '^(?:Assets/(?:Plugins|Resources|YLFrameworkData|Editor)|Library|UserSettings)/|(?:Mcp|ReflectorNet|\.kilo)' }).Count -ne 0) { throw 'Commercial/MCP/user data must not be staged or read as source payload.' }; $checks++
$moves = [IO.File]::ReadAllText((Join-Path $repository 'Assets/Tests/YLFramework/Fixtures/NamespaceMigration/ProductMoves.json')) | ConvertFrom-Json
if ($moves.Moves.Count -ne 28) { throw 'The historical 28-identity fixture must remain intact.' }; $checks++
$structure = [IO.File]::ReadAllText((Join-Path $repository 'Assets/Tests/YLFramework/EditMode/Editor/Distribution/FrameworkStructureTests.cs'))
if (-not $structure.Contains('Assets/Tests/YLFramework/Fixtures/NamespaceMigration/ProductMoves.json') -or
    -not $structure.Contains('Packages/com.kano.ylframework/') -or $structure.Contains('Root + move.From')) { throw 'The source guard still conflates historical Assets locations and installed package locations.' }; $checks++
$boundary = [IO.File]::ReadAllText((Join-Path $repository 'Assets/Tests/YLFramework/EditMode/Editor/Distribution/DevelopmentHostBoundaryTests.cs'))
$approved = [Collections.Generic.HashSet[string]]::new([string[]]@([regex]::Matches($boundary, '"((?:com\.|extensions\.)[^"\s]+)"') | ForEach-Object { $_.Groups[1].Value }), [StringComparer]::Ordinal)
foreach ($relative in @('Packages/manifest.json', 'Packages/packages-lock.json')) {
    $manifest = [IO.File]::ReadAllText((Join-Path $repository $relative)) | ConvertFrom-Json -AsHashtable -DateKind String
    foreach ($name in $manifest['dependencies'].Keys) { if (-not $approved.Contains($name)) { throw "An actual source dependency is not explicitly approved: $name" } }
    $checks++
}
foreach ($id in @('com.kano.ylframework', 'com.kano.ylframework.data-assets', 'com.kano.ylframework.localization', 'com.kano.ylframework.save', 'com.kano.ylframework.state-machine', 'com.kano.ylframework.movement2d', 'com.kano.ylframework.gameplay', 'com.kano.ylframework.dotween', 'com.kano.ylframework.excel', 'com.kano.ylframework.hybridclr')) {
    if (-not $approved.Contains($id)) { throw "An owned package is not explicitly approved: $id" }; $checks++
}
if ($boundary.Contains('PackageTestPaths.Physical("Packages/manifest.json")') -or $boundary.Contains('path.StartsWith("Assets/YLFramework/"')) { throw 'The project manifest or scene still uses the obsolete package boundary.' }; $checks++
"DevelopmentHost NoUnity source guards: $checks passed. NOT Unity compilation, GUID, Prefab or UTF execution."
