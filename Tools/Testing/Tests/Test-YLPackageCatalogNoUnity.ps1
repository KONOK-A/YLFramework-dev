#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param([string] $TemporaryRoot = [IO.Path]::GetTempPath())
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../../Tools/Packaging/Build-YLRegistryPackages.ps1')
. (Join-Path $PSScriptRoot '../Common/Packages.ps1')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$catalogPath = Join-Path $repository 'Tools/Packaging/PackageCatalog.json'
if (-not [IO.File]::Exists($catalogPath)) { throw 'RED: PackageCatalog is missing; package ownership is still hard-coded.' }
$parent = Get-YLTestPath $TemporaryRoot
if (-not [IO.Directory]::Exists($parent)) { throw 'TemporaryRoot must exist.' }
$root = Join-Path $parent ('yl-catalog-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($root)
$checks = 0
$failures = [Collections.Generic.List[string]]::new()
function Check([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Reject([scriptblock] $Action, [string] $Pattern) {
    try { $null = & $Action } catch {
        if ($_.Exception -is [Management.Automation.CommandNotFoundException] -or $_.Exception.Message -notmatch $Pattern) { throw }
        return
    }
    throw "Expected rejection: $Pattern"
}
function Case([string] $Name, [scriptblock] $Body) {
    $script:checks++
    try { & $Body; "PASS: $Name" } catch { $failures.Add("${Name}: $($_.Exception.Message)"); "FAIL: ${Name}: $($_.Exception.Message)" }
}
function Clone([object] $Value) { return ,(ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $Value -Depth 40) -AsHashtable -NoEnumerate) }
function Bytes([string] $Text) { return ,([Text.Encoding]::UTF8.GetBytes($Text)) }
function Asset([Collections.Generic.Dictionary[string, byte[]]] $Contents, [string] $Path, [string] $Text) {
    $Contents.Add($Path, (Bytes $Text))
    $Contents.Add($Path + '.meta', (Bytes ("fileFormatVersion: 2`nguid: " + [Guid]::NewGuid().ToString('N') + "`n")))
}
function Fixture([string] $Id) {
    $contents = [Collections.Generic.Dictionary[string, byte[]]]::new([StringComparer]::Ordinal)
    $manifest = @{ name = $Id; version = '0.1.0-preview.1'; displayName = 'Catalog Probe'; description = 'Test-only optional package';
        unity = '2022.3'; unityRelease = '62f3'; license = 'MIT'; author = @{ name = 'Fixture' };
        repository = @{ type = 'git'; url = 'https://github.com/KONOK-A/YLFramework-dev.git'; directory = "PackageSources/$Id" };
        files = @('package.json', 'package.json.meta', 'README.md', 'README.md.meta', 'LICENSE.md', 'LICENSE.md.meta', 'Runtime', 'Runtime.meta');
        dependencies = @{ 'com.kano.ylframework' = '0.1.0-preview.1' } }
    Asset $contents 'package.json' (ConvertTo-Json -InputObject $manifest -Depth 15)
    Asset $contents 'README.md' 'Test-only package, never a maintained product.'
    Asset $contents 'LICENSE.md' 'MIT License'
    $contents.Add('Runtime.meta', (Bytes ("fileFormatVersion: 2`nguid: " + [Guid]::NewGuid().ToString('N') + "`nfolderAsset: yes`n")))
    Asset $contents 'Runtime/YLFramework.CatalogProbe.asmdef' '{"name":"YLFramework.CatalogProbe","references":["YLFramework.Runtime"],"includePlatforms":[],"excludePlatforms":[],"defineConstraints":[],"versionDefines":[]}'
    Asset $contents 'Runtime/Probe.cs' 'namespace CatalogFixture { public sealed class Probe {} }'
    return ,$contents
}
try {
    $catalog = Read-YLPackageCatalog
    Case 'Excel is one authoring package with a Gameplay-conditional adapter and no retired package identity' {
        Check (@($catalog.packages | Where-Object id -CEQ 'com.kano.ylframework.domain-excel').Count -eq 0) 'The retired DomainExcel package is still advertised as an installation unit.'
        Check (-not [IO.Directory]::Exists((Join-Path $repository 'PackageSources/com.kano.ylframework.domain-excel'))) 'The unpublished DomainExcel package must not remain as an empty compatibility shell.'
        $root = Join-Path $repository 'PackageSources/com.kano.ylframework.excel'
        $manifest = Read-YLPackageEvidenceJson (Join-Path $root 'package.json')
        Check ($manifest.dependencies['com.kano.ylframework.data-assets'] -ceq '0.1.0-preview.1' -and
            -not $manifest.dependencies.Contains('com.kano.ylframework.gameplay')) 'Generic Excel must use DataAssets without forcing Gameplay.'
        $adapter = Read-YLPackageEvidenceJson (Join-Path $root 'Editor/Gameplay/YLFramework.Domain.Excel.Editor.asmdef')
        Check ($adapter.name -ceq 'YLFramework.Domain.Excel.Editor' -and ($adapter.includePlatforms -join ',') -ceq 'Editor' -and
            ($adapter.defineConstraints -join ',') -ceq 'YL_GAMEPLAY_PACKAGE' -and $adapter.versionDefines.Count -eq 1 -and
            $adapter.versionDefines[0].name -ceq 'com.kano.ylframework.gameplay' -and $adapter.versionDefines[0].define -ceq 'YL_GAMEPLAY_PACKAGE') 'The original adapter assembly must be enabled only by the actual Gameplay package version define.'
        $memory = Join-Path $root 'Editor/DataAssetsWorkbookMemory.cs'
        Check ([IO.File]::Exists($memory) -and [IO.File]::ReadAllText($memory).Contains('YLFramework.DataAssets.LastWorkbook.')) 'Workbook memory must move to Excel without resetting its existing EditorPrefs keys.'
        Check (-not [IO.File]::ReadAllText((Join-Path $repository 'PackageSources/com.kano.ylframework.data-assets/Editor/Systems/DataAssets/ScriptableObjectConfigsInspector.cs')).Contains('DataAssetsWorkbookMemory')) 'The ordinary DataAssets Inspector must not call an optional Excel owner.'
    }
    Case 'Gameplay owns four independent runtimes and its shared Editor without an Excel dependency' {
        $gameplay = @($catalog.packages | Where-Object id -CEQ 'com.kano.ylframework.gameplay')
        Check ($gameplay.Count -eq 1) 'The complete Gameplay installation unit is missing from the maintained catalog.'
        $root = Join-Path $repository 'PackageSources/com.kano.ylframework.gameplay'
        $manifest = Read-YLPackageEvidenceJson (Join-Path $root 'package.json')
        Check ($manifest.dependencies.Count -eq 2 -and $manifest.dependencies['com.kano.ylframework'] -ceq '0.1.0-preview.1' -and
            $manifest.dependencies['com.unity.nuget.newtonsoft-json'] -ceq '3.2.1') 'Gameplay must declare only its actual main and Newtonsoft dependencies.'
        foreach ($domain in @('Skill', 'Buff', 'Mission', 'Equipment')) {
            $runtime = Read-YLPackageEvidenceJson (Join-Path $root "Runtime/$domain/YLFramework.$domain.asmdef")
            Check ($runtime.name -ceq "YLFramework.$domain" -and ($runtime.references -join ',') -ceq 'YLFramework.Runtime' -and $runtime.overrideReferences) "$domain must not reference another gameplay runtime or rely on implicit vendor assemblies."
            $expected = if ($domain -cin @('Buff', 'Mission')) { 'Newtonsoft.Json.dll' } else { '' }
            Check (($runtime.precompiledReferences -join ',') -ceq $expected) "$domain must declare only its actual JSON binary consumption."
        }
        $editor = Read-YLPackageEvidenceJson (Join-Path $root 'Editor/YLFramework.Gameplay.Editor.asmdef')
        Check ($editor.name -ceq 'YLFramework.Gameplay.Editor' -and ($editor.includePlatforms -join ',') -ceq 'Editor' -and
            ($editor.references -join ',') -ceq 'YLFramework.Runtime,YLFramework.Editor,YLFramework.Skill,YLFramework.Buff,YLFramework.Mission,YLFramework.Equipment' -and
            ($editor.precompiledReferences -join ',') -ceq 'Newtonsoft.Json.dll') 'Gameplay authoring must own all four domains, shared helpers and its actual clipboard JSON binary consumption.'
        $adapter = Read-YLPackageEvidenceJson (Join-Path $repository 'PackageSources/com.kano.ylframework.excel/Editor/Gameplay/YLFramework.Domain.Excel.Editor.asmdef')
        Check (@($adapter.references) -ccontains 'YLFramework.Gameplay.Editor') 'The optional ExcelGameplay authoring consumer must declare its real Gameplay Editor dependency.'
    }
    Case 'Movement2D owns two independent runtimes and one Editor with explicit physics dependency' {
        $movement = @($catalog.packages | Where-Object id -CEQ 'com.kano.ylframework.movement2d')
        Check ($movement.Count -eq 1) 'The combined movement2d package is missing from the maintained catalog.'
        $root = Join-Path $repository 'PackageSources/com.kano.ylframework.movement2d'
        $manifest = Read-YLPackageEvidenceJson (Join-Path $root 'package.json')
        Check ($manifest.dependencies.Count -eq 2 -and $manifest.dependencies['com.kano.ylframework'] -ceq '0.1.0-preview.1' -and
            $manifest.dependencies['com.unity.modules.physics2d'] -ceq '1.0.0') 'Movement2D must declare its real main and physics2d dependencies.'
        $character = Read-YLPackageEvidenceJson (Join-Path $root 'Runtime/Utilities/CharacterController2D/YLFramework.Character2D.asmdef')
        $platform = Read-YLPackageEvidenceJson (Join-Path $root 'Runtime/Utilities/MoveablePlatform/YLFramework.Platform2D.asmdef')
        Check ($character.name -ceq 'YLFramework.Character2D' -and ($character.references -join ',') -ceq 'YLFramework.Runtime') 'Character2D must not depend on Platform2D or another optional runtime.'
        Check ($platform.name -ceq 'YLFramework.Platform2D' -and ($platform.references -join ',') -ceq 'YLFramework.Runtime') 'Platform2D must not depend on Character2D or another optional runtime.'
        $editor = Read-YLPackageEvidenceJson (Join-Path $root 'Editor/YLFramework.Movement2D.Editor.asmdef')
        Check ($editor.name -ceq 'YLFramework.Movement2D.Editor' -and ($editor.includePlatforms -join ',') -ceq 'Editor' -and
            ($editor.references -join ',') -ceq 'YLFramework.Character2D,YLFramework.Platform2D,YLFramework.Editor') 'The common movement Editor must reference both independent runtimes and its actual common Editor dependency.'
    }
    Case 'Configured FSM owns its Runtime, Editor, template and stylesheet without the lightweight FSM' {
        $fsm = @($catalog.packages | Where-Object id -CEQ 'com.kano.ylframework.state-machine')
        Check ($fsm.Count -eq 1) 'The configured FSM package is missing from the maintained catalog.'
        $root = Join-Path $repository 'PackageSources/com.kano.ylframework.state-machine'
        $manifest = Read-YLPackageEvidenceJson (Join-Path $root 'package.json')
        Check ($manifest.dependencies.Count -eq 1 -and $manifest.dependencies['com.kano.ylframework'] -ceq '0.1.0-preview.1') 'The FSM package must declare its actual main-package dependency only.'
        $runtime = Read-YLPackageEvidenceJson (Join-Path $root 'Runtime/YLFramework.StateMachine.asmdef')
        $editor = Read-YLPackageEvidenceJson (Join-Path $root 'Editor/YLFramework.StateMachine.Editor.asmdef')
        Check ($runtime.name -ceq 'YLFramework.StateMachine' -and ($runtime.references -join ',') -ceq 'YLFramework.Runtime') 'Configured FSM runtime dependency is not explicit.'
        Check ($editor.name -ceq 'YLFramework.StateMachine.Editor' -and ($editor.includePlatforms -join ',') -ceq 'Editor' -and
            ($editor.references -join ',') -ceq 'YLFramework.StateMachine,YLFramework.Editor') 'FSM Editor must declare its configured runtime and actual package-path helper owner.'
        Check ([IO.File]::ReadAllText((Join-Path $root 'Editor/Systems/StateMachine/Template/FSMStateTemplate.txt.meta')).Contains('497ab7439af8a164a8a4dc6315fde458')) 'The existing state template GUID changed.'
        Check ([IO.File]::ReadAllText((Join-Path $root 'Editor/Systems/StateMachine/Styles/FSMGraph.uss.meta')).Contains('182916d1698abe740874ab8495e1b2b8')) 'The existing graph stylesheet GUID changed.'
        Check ([IO.Directory]::Exists((Join-Path $repository 'PackageSources/com.kano.ylframework/Runtime/Utilities/FSM')) -and
            -not [IO.Directory]::Exists((Join-Path $root 'Runtime/Utilities/FSM'))) 'The unrelated lightweight FSM moved with the configured FSM.'
    }
    Case 'Save is a real standalone package with explicit Newtonsoft and no main-package dependency' {
        $save = @($catalog.packages | Where-Object id -CEQ 'com.kano.ylframework.save')
        Check ($save.Count -eq 1) 'The Save package is missing from the maintained catalog.'
        $manifest = Read-YLPackageEvidenceJson (Join-Path $repository 'PackageSources/com.kano.ylframework.save/package.json')
        Check ($manifest.dependencies.Count -eq 1 -and $manifest.dependencies['com.unity.nuget.newtonsoft-json'] -ceq '3.2.1') 'Save must declare Newtonsoft directly, not inherit it from the main package.'
        $definition = Read-YLPackageEvidenceJson (Join-Path $repository 'PackageSources/com.kano.ylframework.save/Runtime/YLFramework.Save.asmdef')
        Check ($definition.name -ceq 'YLFramework.Save' -and $definition.references.Count -eq 0) 'Save acquired an implicit framework assembly dependency.'
        Check (($definition.precompiledReferences -join ',') -ceq 'Newtonsoft.Json.dll') 'Save must explicitly reference its actual JSON binary.'
    }
    $id = 'com.kano.ylframework.catalog-probe'
    $entry = @{ id = $id; assemblies = @(@{ name = 'YLFramework.CatalogProbe'; path = 'Runtime/YLFramework.CatalogProbe.asmdef';
        player = $true; defineConstraints = @(); versionDefines = @() }); requiredFiles = @('Runtime/Probe.cs'); samples = @() }
    $extended = Clone $catalog
    $extended.packages += $entry
    $contents = Fixture $id
    Case 'PackageCatalogRejectsUnknownOrPartialPayload: an unlisted package is refused' {
        Reject { Get-YLRegistryPayload -Contents $contents } 'identity|catalog|Unknown'
    }
    Case 'A declared additional package receives the complete payload audit' {
        $payload = Get-YLRegistryPayload -Contents $contents -Catalog $extended
        Check ($payload.id -ceq $id -and $payload.assemblies.Count -eq 1) 'The extra declared package was not audited.'
        Check (($payload.assemblies[0].sources -join ',') -ceq 'Runtime/Probe.cs') 'The declared assembly did not own its exact source set.'
        $mainGuid = @((Get-YLRegistryPackageSet -Root (Join-Path $repository 'PackageSources'))[0].guids.Keys)[0]
        $meta = $contents['Runtime/Probe.cs.meta']
        try {
            $contents['Runtime/Probe.cs.meta'] = Bytes "fileFormatVersion: 2`nguid: $mainGuid`n"
            $duplicate = Get-YLRegistryPayload -Contents $contents -Catalog $extended
            $batch = @(Get-YLRegistryPackageSet -Root (Join-Path $repository 'PackageSources')) + @($duplicate)
            Reject { Assert-YLRegistryPackageBatch -Packages $batch -Catalog $extended } 'GUID'
        } finally { $contents['Runtime/Probe.cs.meta'] = $meta }
    }
    foreach ($path in @('LICENSE.md', 'Runtime/Probe.cs', 'Runtime/Probe.cs.meta', 'Runtime/YLFramework.CatalogProbe.asmdef')) {
        Case "A newly declared package cannot omit $path" {
            $bytes = $contents[$path]
            try { $null = $contents.Remove($path); Reject { Get-YLRegistryPayload -Contents $contents -Catalog $extended } 'Missing|required|payload|assembly|source|Orphan' }
            finally { $contents[$path] = $bytes }
        }
    }
    foreach ($path in @('Runtime/Tests/Hidden.cs', 'Runtime/DOTween.dll', 'Runtime/SDK/Vendor.cs', 'Runtime/Extra.asmdef')) {
        Case "A newly declared package cannot hide $path" {
            Asset $contents $path $(if ($path.EndsWith('.asmdef')) { '{"name":"Unlisted.Assembly"}' } else { '// unapproved input' })
            try { Reject { Get-YLRegistryPayload -Contents $contents -Catalog $extended } 'Forbidden|unapproved|unauthorized|undeclared|assembly|catalog' }
            finally { $null = $contents.Remove($path); $null = $contents.Remove($path + '.meta') }
        }
    }
    Case 'Nested conditional source belongs to the adapter, not to the default Runtime' {
        $nested = 'Runtime/Integration/Adapter/YLFramework.CatalogProbe.Adapter.asmdef'
        $definition = Clone $entry
        $definition.assemblies += @{ name = 'YLFramework.CatalogProbe.Adapter'; path = $nested; player = $true;
            defineConstraints = @('DOTWEEN'); versionDefines = @() }
        $withAdapter = Clone $catalog; $withAdapter.packages += $definition
        $contents.Add('Runtime/Integration.meta', (Bytes ("fileFormatVersion: 2`nguid: " + [Guid]::NewGuid().ToString('N') + "`nfolderAsset: yes`n")))
        $contents.Add('Runtime/Integration/Adapter.meta', (Bytes ("fileFormatVersion: 2`nguid: " + [Guid]::NewGuid().ToString('N') + "`nfolderAsset: yes`n")))
        Asset $contents $nested '{"name":"YLFramework.CatalogProbe.Adapter","references":["YLFramework.Runtime"],"includePlatforms":[],"excludePlatforms":[],"defineConstraints":["DOTWEEN"],"versionDefines":[]}'
        Asset $contents 'Runtime/Integration/Adapter/Adapter.cs' 'namespace CatalogFixture { public sealed class Adapter {} }'
        try {
            $payload = Get-YLRegistryPayload -Contents $contents -Catalog $withAdapter
            $runtime = @($payload.assemblies | Where-Object name -CEQ 'YLFramework.CatalogProbe')[0]
            $adapter = @($payload.assemblies | Where-Object name -CEQ 'YLFramework.CatalogProbe.Adapter')[0]
            Check (($runtime.sources -join ',') -ceq 'Runtime/Probe.cs') 'The disabled adapter polluted the core source set.'
            Check (($adapter.sources -join ',') -ceq 'Runtime/Integration/Adapter/Adapter.cs') 'Adapter source disappeared from the payload audit.'
            $adapterBytes = $contents['Runtime/Integration/Adapter/Adapter.cs']
            $adapterMeta = $contents['Runtime/Integration/Adapter/Adapter.cs.meta']
            try {
                $null = $contents.Remove('Runtime/Integration/Adapter/Adapter.cs'); $null = $contents.Remove('Runtime/Integration/Adapter/Adapter.cs.meta')
                Reject { Get-YLRegistryPayload -Contents $contents -Catalog $withAdapter } 'source|empty|payload'
            } finally {
                $contents['Runtime/Integration/Adapter/Adapter.cs'] = $adapterBytes; $contents['Runtime/Integration/Adapter/Adapter.cs.meta'] = $adapterMeta
            }
        } finally {
            foreach ($path in @($nested, $nested + '.meta', 'Runtime/Integration/Adapter/Adapter.cs', 'Runtime/Integration/Adapter/Adapter.cs.meta', 'Runtime/Integration/Adapter.meta', 'Runtime/Integration.meta')) { $null = $contents.Remove($path) }
        }
    }
    Case 'MainHasNoOptionalAssemblyReferences rejects a main-to-leaf reference' {
        $main = [Collections.Generic.Dictionary[string, byte[]]]::new([StringComparer]::Ordinal)
        foreach ($file in @(Get-YLTestTreeFiles (Join-Path $repository 'PackageSources/com.kano.ylframework'))) {
            $relative = [IO.Path]::GetRelativePath((Join-Path $repository 'PackageSources/com.kano.ylframework'), $file).Replace('\', '/')
            if ($relative -cne '.gitattributes') { $main.Add($relative, [IO.File]::ReadAllBytes($file)) }
        }
        $asmdef = ConvertFrom-Json -InputObject ([Text.Encoding]::UTF8.GetString($main['Runtime/YLFramework.Runtime.asmdef'])) -AsHashtable
        $asmdef.references += 'YLFramework.Excel.Editor'
        $main['Runtime/YLFramework.Runtime.asmdef'] = Bytes (ConvertTo-Json -InputObject $asmdef -Depth 20)
        Reject { Get-YLRegistryPayload -Contents $main } 'optional|reference|dependency|main'
    }
    Case 'Package discovery rejects an unlisted source directory instead of silently skipping it' {
        $sourceRoot = Join-Path $root 'PackageSources'; $null = [IO.Directory]::CreateDirectory($sourceRoot)
        $unknown = Join-Path $sourceRoot 'com.kano.ylframework.unknown'; $null = [IO.Directory]::CreateDirectory($unknown)
        Reject { Get-YLRegistryPackageSet -Root $sourceRoot } 'Unknown|unlisted|catalog|unauthorized'
    }
    foreach ($fault in @('schema-type', 'unknown-field', 'duplicate-package', 'duplicate-assembly', 'unsafe-path', 'invalid-player', 'unknown-version-package', 'unmapped-define')) {
        Case "Catalog schema refuses $fault" {
            $bad = Clone $catalog
            switch ($fault) {
                'schema-type' { $bad.schemaVersion = $true }
                'unknown-field' { $bad['acceptAnyPackage'] = $true }
                'duplicate-package' { $bad.packages += (Clone $bad.packages[0]) }
                'duplicate-assembly' { $bad.packages[1].assemblies += (Clone $bad.packages[0].assemblies[0]) }
                'unsafe-path' { $bad.packages[0].assemblies[0].path = '../outside.asmdef' }
                'invalid-player' { $bad.packages[0].assemblies[0].player = 'true' }
                'unknown-version-package' { $bad.packages[0].assemblies[0].versionDefines = @(@{ name = 'com.kano.ylframework.unknown'; expression = '0.0.0'; define = 'UNKNOWN' }) }
                'unmapped-define' { $bad.packages[0].assemblies[0].defineConstraints = @('NEVER_COMPILE_WITHOUT_A_PROFILE') }
            }
            $path = Join-Path $root 'PackageCatalog.json'
            [IO.File]::WriteAllText($path, (ConvertTo-Json -InputObject $bad -Depth 40), [Text.UTF8Encoding]::new($false))
            Reject { Read-YLPackageCatalog -Path $path } 'catalog|schema|fields|duplicate|path|player|package'
        }
    }
    $contractSource = Join-Path $PSScriptRoot '../PackageCatalogContract.cs'
    if (-not [IO.File]::Exists($contractSource)) { throw 'RED: the compiled package catalog/source-set contract is missing.' }
    $references = @([IO.Directory]::EnumerateFiles((Join-Path $PSHOME 'ref'), '*.dll')) + @([Newtonsoft.Json.Linq.JObject].Assembly.Location)
    Add-Type -Path $contractSource -ReferencedAssemblies $references -CompilerOptions @('/langversion:9.0', '/nowarn:1701')
    Case 'Compiled catalog cannot use an unknown macro to silently drop a required assembly' {
        $invalid = [Newtonsoft.Json.Linq.JObject]::Parse((ConvertTo-Json -InputObject $catalog -Depth 40))
        $invalid['packages'][0]['assemblies'][0]['defineConstraints'] = [Newtonsoft.Json.Linq.JArray]::new([object[]]@('NEVER_COMPILE_WITHOUT_A_PROFILE'))
        Reject { [YLFramework.Testing.PackageCatalogContract]::Validate($invalid) } 'catalog|condition|define'
    }
    $feature = 'com.kano.ylframework.fixture-feature'; $backend = 'com.kano.ylframework.fixture-backend'
    $fixtureCatalog = @{ schemaVersion = 1; packages = @(
        @{ id = 'com.kano.ylframework'; requiredFiles = @(); samples = @(); assemblies = @(
            @{ name = 'YLFramework.Runtime'; path = 'Runtime/YLFramework.Runtime.asmdef'; player = $true; defineConstraints = @(); versionDefines = @() },
            @{ name = 'YLFramework.Editor'; path = 'Editor/YLFramework.Editor.asmdef'; player = $false; defineConstraints = @(); versionDefines = @() }) },
        @{ id = $feature; requiredFiles = @(); samples = @(); assemblies = @(
            @{ name = 'YLFramework.FixtureFeature'; path = 'Runtime/YLFramework.FixtureFeature.asmdef'; player = $true; defineConstraints = @(); versionDefines = @() },
            @{ name = 'YLFramework.FixtureAdapter'; path = 'Runtime/Integration/YLFramework.FixtureAdapter.asmdef'; player = $true;
                defineConstraints = @('YL_FIXTURE_BACKEND'); versionDefines = @(@{ name = $backend; expression = '0.0.0'; define = 'YL_FIXTURE_BACKEND' }) }) },
        @{ id = $backend; requiredFiles = @(); samples = @(); assemblies = @(
            @{ name = 'YLFramework.FixtureBackend'; path = 'Runtime/YLFramework.FixtureBackend.asmdef'; player = $true; defineConstraints = @(); versionDefines = @() }) }
    ) }
    Case 'A known fixture root set keeps the backend independent without accepting undeclared extra installs' {
        $manifests = @{
            'com.kano.ylframework' = @{ name = 'com.kano.ylframework'; version = '0.1.0-preview.1'; dependencies = @{} }
        }
        $manifests[$feature] = @{ name = $feature; version = '0.1.0-preview.1'; dependencies = @{ 'com.kano.ylframework' = '0.1.0-preview.1' } }
        $manifests[$backend] = @{ name = $backend; version = '0.1.0-preview.1'; dependencies = @{} }
        $catalogJson = [Newtonsoft.Json.Linq.JObject]::Parse((ConvertTo-Json -InputObject $fixtureCatalog -Depth 30))
        $manifestJson = [Newtonsoft.Json.Linq.JObject]::Parse((ConvertTo-Json -InputObject $manifests -Depth 20))
        [YLFramework.Testing.PackageCatalogContract]::AssertClosure($catalogJson, $manifestJson, [string[]]@('com.kano.ylframework', $feature, $backend))
        Reject { [YLFramework.Testing.PackageCatalogContract]::AssertClosure($catalogJson, $manifestJson, [string[]]@($feature)) } 'closure|selection|exceeds'
    }
    function Planned([string[]] $Ids, [string] $Fault = '') {
        $packages = @(foreach ($selected in $fixtureCatalog.packages | Where-Object { $_.id -cin $Ids }) {
            $files = @(); foreach ($assembly in $selected.assemblies) {
                $directory = $assembly.path.Substring(0, $assembly.path.LastIndexOf('/'))
                $files += @{ path = $assembly.path }; $files += @{ path = "$directory/$($assembly.name).cs" }
            }
            if ($Fault -ceq 'unknown-assembly' -and $selected.id -ceq $feature) { $files += @{ path = 'Runtime/Hidden.asmdef' } }
            if ($Fault -ceq 'unowned-source' -and $selected.id -ceq $feature) { $files += @{ path = 'Outside/Hidden.cs' } }
            if ($Fault -ceq 'empty-adapter' -and $selected.id -ceq $feature) { $files = @($files | Where-Object path -CNE 'Runtime/Integration/YLFramework.FixtureAdapter.cs') }
            @{ name = $selected.id; version = '0.1.0-preview.1'; files = $files }
        })
        $json = [Newtonsoft.Json.Linq.JObject]::Parse((ConvertTo-Json -InputObject $fixtureCatalog -Depth 30))
        $inputs = [Newtonsoft.Json.Linq.JArray]::Parse((ConvertTo-Json -InputObject @($packages) -Depth 30))
        return ,([YLFramework.Testing.PackageCatalogContract]::Plan($json, $inputs, 'Package', $null))
    }
    foreach ($combination in @(@{ feature = $false; backend = $false }, @{ feature = $true; backend = $false },
        @{ feature = $false; backend = $true }, @{ feature = $true; backend = $true })) {
        Case "Compiled conditional plan: feature=$($combination.feature), backend=$($combination.backend)" {
            $ids = @('com.kano.ylframework'); if ($combination.feature) { $ids += $feature }; if ($combination.backend) { $ids += $backend }
            $plan = Planned $ids
            $adapters = @($plan | Where-Object { [string]$_['name'] -ceq 'YLFramework.FixtureAdapter' })
            Check ($adapters.Count -eq [int]$combination.feature) 'The adapter payload owner does not follow feature installation.'
            if ($combination.feature) {
                Check ([bool]$adapters[0]['enabled'].Value -eq $combination.backend) 'Compiled expectation was inferred from the actual graph, not explicit package presence.'
                Check (@($adapters[0]['sources']).Count -eq 1) 'Disabled adapter sources disappeared from the complete payload.'
                $owner = @($plan | Where-Object { [string]$_['name'] -ceq 'YLFramework.FixtureFeature' })[0]
                Check ((@($owner['sources'] | ForEach-Object { $_.ToString() }) -join ',') -ceq 'Runtime/YLFramework.FixtureFeature.cs') 'The conditional subtree contaminated its parent Runtime source set.'
            }
        }
    }
    foreach ($fault in @('unknown-assembly', 'unowned-source', 'empty-adapter')) {
        Case "Compiled ownership refuses $fault, including when the adapter is disabled" {
            Reject { Planned @('com.kano.ylframework', $feature) $fault } 'assembly|source|payload|owner'
        }
    }
    if ($failures.Count) { throw "Package catalog contracts failed ($($failures.Count)/$checks):`n$($failures -join "`n")" }
    "PASS: $checks package catalog NoUnity contracts. Not Unity compilation, installation or Player acceptance."
} finally {
    if ($failures.Count -eq 0) { $null = @(Get-YLTestTreeFiles $root); [IO.Directory]::Delete($root, $true) }
    else { "Failed contract evidence retained: $root" }
}
