#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param([Parameter(Mandatory)][string] $TemporaryRoot)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Test-YLIntegrationPackages.ps1') -WorkspaceRoot $TemporaryRoot -Unity 'C:\unused\Unity.exe' -Integration Excel
. (Join-Path $PSScriptRoot 'PackageCatalogFixtures.ps1')
$sandbox = Join-Path (Get-YLTestPath $TemporaryRoot) ('yl-integration-entry-' + [Guid]::NewGuid().ToString('N'))
$repository = Join-Path $sandbox 'Repository'; $null = [IO.Directory]::CreateDirectory($repository)
$workspace = Initialize-YLTestWorkspace -WorkspaceRoot (Join-Path $sandbox 'Workspace') -Repository $repository
$hostRecord = New-YLTestDirectory -Workspace $workspace -Kind Host -Key Entry
$result = New-YLTestDirectory -Workspace $workspace -Kind Result -Key Entry
$cache = New-YLTestDirectory -Workspace $workspace -Kind Cache -Key Entry
$leases = @($hostRecord, $result, $cache | ForEach-Object { Lock-YLTestDirectory -Workspace $workspace -Path $_.path })
$checks = 0
$passed = $false
function Check([bool] $Condition, [string] $Reason) { if (-not $Condition) { throw $Reason }; $script:checks++ }
function Save([string] $Path, [object] $Value) {
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -Depth 30 -InputObject $Value), [Text.UTF8Encoding]::new($false))
}
function Call([string] $Name, [Newtonsoft.Json.Linq.JObject] $Request) {
    $method = [YLFramework.Testing.IntegrationEntry].GetMethod($Name, [Reflection.BindingFlags]'NonPublic,Static')
    if ($null -eq $method) { throw "IntegrationEntry is missing the exercised boundary: $Name" }
    $arguments = [object[]]::new(1); $arguments[0] = $Request
    try { return ,$method.Invoke($null, $arguments) } catch { throw $_.Exception.GetBaseException() }
}
function Boundary([string] $Name, [object[]] $Values) {
    $method = [YLFramework.Testing.IntegrationEntry].GetMethod($Name, [Reflection.BindingFlags]'NonPublic,Static')
    if ($null -eq $method) { throw "Missing exercised integration boundary: $Name" }
    try { return ,$method.Invoke($null, $Values) } catch { throw $_.Exception.GetBaseException() }
}
function Reject([scriptblock] $Action, [string] $Reason) {
    try { & $Action | Out-Null } catch {
        if ($_.Exception -isnot [InvalidOperationException] -and $_.Exception -isnot [Newtonsoft.Json.JsonReaderException]) { throw }
        $script:checks++; return
    }
    throw "Entry accepted: $Reason"
}
function FixtureGraph([string] $Side) {
    $mode = $null
    if ($valid.integration -ceq 'DOTween') { $mode = if ($valid.Contains('dotweenSdkMode')) { $valid.dotweenSdkMode } else { 'Required' } }
    $plan = @(Get-YLPackageAssemblyPlan -Catalog $fixtureCatalog -Packages $valid.packages -Profile $valid.integration -DOTweenSdkMode $mode)
    foreach ($row in $plan | Where-Object { $_.enabled -and ($Side -ceq 'Editor' -or $_.player) }) {
        $assembly = [UnityEditor.Compilation.Assembly]::new(); $assembly.name = $row.name
        $assembly.sourceFiles = @($row.sources | ForEach-Object { "Packages/$($row.package)/$_" })
        $assembly
    }
}
try {
    # Exercise the real inline SDK gate without running Execute or changing production visibility.
    $integrationSource = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../IntegrationEntry.cs'))
    $sdkMiniExcel = [regex]::Matches($integrationSource, '(?s)if \(integration == "Excel" \|\| integration == "ExcelGameplay"\)\s*\{.*?\r?\n                \}\s*(?=if \(integration == "HybridCLR"\))')
    $globalGate = [regex]::Matches($integrationSource, '(?s)if \(global.Contains\("YL_DOMAIN_EXCEL"\).*?\r?\n                \}')
    Check ($sdkMiniExcel.Count -eq 1 -and $globalGate.Count -eq 1) 'The maintained MiniExcel/global gates must be exercised, not replaced by a fixture implementation.'
    $sdkProbe = @'
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using CompiledAssembly = UnityEditor.Compilation.Assembly;
namespace YLFramework.Testing
{
    public static class SdkMiniExcelFixture
    {
        public static JObject Validate(JObject request, CompiledAssembly[] editor, JObject[] selected, string[] global)
        {
            var report = new JObject(); string integration = Text(request, "integration");
            /*GLOBAL_GATE*/
            /*MINIEXCEL_GATE*/
            return report;
        }
        private static object Call(string name, params object[] arguments) => typeof(IntegrationEntry).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, arguments);
        private static string Text(JObject value, string field) => (string)Call("Text", value, field);
        private static JObject ReadJson(string path) => (JObject)Call("ReadJson", path);
        private static string Hash(string path) => (string)Call("Hash", path);
        private static bool Inside(string path, string root) => (bool)Call("Inside", path, root);
        private static string[] References(CompiledAssembly assembly) => (string[])Call("References", assembly);
    }
}
'@
    $sdkProbe = $sdkProbe.Replace('/*GLOBAL_GATE*/', $globalGate[0].Value).Replace('/*MINIEXCEL_GATE*/', $sdkMiniExcel[0].Value)
    $sdkProbePath = Join-Path $sandbox 'SdkMiniExcelFixture.cs'
    [IO.File]::WriteAllText($sdkProbePath, $sdkProbe, [Text.UTF8Encoding]::new($false))
    $references = @([IO.Directory]::EnumerateFiles((Join-Path $PSHOME 'ref'), '*.dll')) + @([Newtonsoft.Json.Linq.JObject].Assembly.Location)
    Add-Type -Path @((Join-Path $PSScriptRoot '../IntegrationEntry.cs'), (Join-Path $PSScriptRoot '../PackageCatalogContract.cs'), (Join-Path $PSScriptRoot 'Test-YLIntegrationPackagesNoUnityStubs.cs'), $sdkProbePath) -ReferencedAssemblies $references -CompilerOptions @('/define:UNITY_EDITOR', '/langversion:9.0', '/nowarn:1701')
    function MiniExcel($Request, $Editor, $Selected, [string[]] $Global = @()) {
        try { return ,([YLFramework.Testing.SdkMiniExcelFixture]::Validate($Request, [UnityEditor.Compilation.Assembly[]]$Editor, [Newtonsoft.Json.Linq.JObject[]]$Selected, $Global)) }
        catch { throw $_.Exception.GetBaseException() }
    }
    [UnityEngine.Application]::dataPath = (Join-Path $hostRecord.path 'Assets').Replace('\', '/'); $null = [IO.Directory]::CreateDirectory([UnityEngine.Application]::dataPath)
    $output = Join-Path $result.path 'Compile'; $null = [IO.Directory]::CreateDirectory($output)
    $sha = '0123456789abcdef0123456789abcdef01234567'; $repo = 'https://example.invalid/product.git'
    $fixtureCatalog = Get-YLFixturePackageCatalog
    $packages = @(); $registered = @(); $dependencies = @{}; $locked = @{}
    foreach ($id in @('com.kano.ylframework', 'com.kano.ylframework.data-assets', 'com.kano.ylframework.excel')) {
        $p = Join-Path $hostRecord.path "Library/PackageCache/$id"
        $deps = Get-YLFixtureInternalDependencies -PackageId $id -Version '0.1.0-preview.1'
        Save (Join-Path $p 'package.json') @{ name = $id; version = '0.1.0-preview.1'; dependencies = $deps; extra = @{ date = '2026-01-01T00:00:00Z' } }
        [IO.File]::WriteAllText((Join-Path $p 'LICENSE.md'), 'fixture license')
        Add-YLFixtureAssemblyPayload -Catalog $fixtureCatalog -PackageId $id -Root $p -Sources
        $content = Get-YLIntegrationPackageContent -Root $p -PackageId $id -NormalizeManifest
        $packages += @{ name = $id; version = '0.1.0-preview.1'; files = $content.files; contentSha256 = $content.sha256 }
        $locator = $repo + '?path=/PackageSources/' + $id + '#' + $sha
        $dependencies[$id] = $locator; $locked[$id] = @{ source = 'git'; version = $locator; hash = $sha; depth = 0; dependencies = $deps }
        $api = [UnityEditor.PackageManager.PackageInfo]::new(); $api.name = $id; $api.version = '0.1.0-preview.1'; $api.source = [UnityEditor.PackageManager.PackageSource]::Git
        $api.isDirectDependency = $true
        $api.assetPath = 'Packages/' + $id; $api.resolvedPath = $p; $api.packageId = $id + '@' + $locator; $registered += $api
    }
    $manifest = Join-Path $hostRecord.path 'Packages/manifest.json'; Save $manifest @{ dependencies = $dependencies }
    $lockPath = Join-Path $hostRecord.path 'Packages/packages-lock.json'; Save $lockPath @{ dependencies = $locked }
    $valid = @{ schemaVersion = 1; purpose = 'YLTesting.IntegrationPackages'; integration = 'Excel'; hostId = $hostRecord.id; resultId = $result.id; workspaceId = $workspace.id
        resultRoot = $result.path; output = $output; unityVersion = '2022.3.62f3'; inputHash = ('a' * 64); manifestSha256 = (Get-FileHash -LiteralPath $manifest).Hash
        packageOrigin = 'GitRemote'; gitRepository = $repo; gitRevision = $sha; cacheRoot = $cache.path; cacheId = $cache.id; expectedTemplateHash = ('a' * 64); packages = $packages
        assemblies = @(Get-YLPackageAssemblyPlan -Catalog $fixtureCatalog -Packages $packages -Profile Excel -MetadataOnly | Where-Object enabled) }
    Save (Join-Path $hostRecord.path '.yl-inputs.json') @{ schemaVersion = 1; purpose = 'YLTesting.HostInputs'; hostId = $hostRecord.id; inputHash = ('a' * 64) }
    $valid.entryPackage = 'com.kano.ylframework.excel'
    $valid.catalogSha256 = Set-YLFixtureCatalogInput -HostRecord $hostRecord -Catalog $fixtureCatalog -InputHash ('a' * 64)
    [UnityEditor.PackageManager.PackageInfo]::Registered = $registered
    function Request { return ,([Newtonsoft.Json.Linq.JObject]::Parse((ConvertTo-Json -Depth 30 -InputObject $valid))) }
    $null = Call ValidateInvocation (Request); $checks++
    $valid.entryPackages = @('com.kano.ylframework.excel')
    $null = Call ValidateInvocation (Request); $checks++
    $r = Request; $r['integration'] = [Newtonsoft.Json.Linq.JValue]::new('DomainExcel'); Reject { Call ValidateInvocation $r } 'The retired DomainExcel profile must not remain accepted by the C# entry'
    foreach ($field in @('schemaVersion', 'catalogSha256', 'entryPackage')) {
        $r = Request; $null = $r.Remove($field); Reject { Call ValidateInvocation $r } "missing mandatory catalog/entry $field"
    }
    $r = Request; $r['catalogSha256'] = [Newtonsoft.Json.Linq.JValue]::new(('d' * 64)); Reject { Call ValidateInvocation $r } 'catalog digest mismatch'
    foreach ($fault in @('schema-type', 'schema-version', 'unknown-entry', 'unknown-package', 'duplicate-package', 'missing-assembly', 'duplicate-assembly')) {
        $r = Request
        switch ($fault) {
            'schema-type' { $r['schemaVersion'] = [Newtonsoft.Json.Linq.JValue]::new($true) }
            'schema-version' { $r['schemaVersion'] = [Newtonsoft.Json.Linq.JValue]::new(2) }
            'unknown-entry' { $r['entryPackage'] = [Newtonsoft.Json.Linq.JValue]::new('com.kano.ylframework.unknown') }
            'unknown-package' { $r['packages'][1]['name'] = [Newtonsoft.Json.Linq.JValue]::new('com.kano.ylframework.unknown') }
            'duplicate-package' { $r['packages'].Add($r['packages'][0].DeepClone()) }
            'missing-assembly' { $r['assemblies'][0].Remove() }
            'duplicate-assembly' { $r['assemblies'].Add($r['assemblies'][0].DeepClone()) }
        }
        Reject { Call ValidateInvocation $r } $fault
    }
    foreach ($relative in @('Runtime/DOTween.dll', 'Runtime/HybridCLR.Runtime.dll', 'Runtime/SDK/Hidden.cs')) {
        $path = Join-Path $hostRecord.path ('VendorShapeFixture/' + $relative)
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)); [IO.File]::WriteAllText($path, 'MZ NoUnity vendor-shaped input, not a real SDK')
        Reject { Boundary PackageFileHash @([string]$path, [string]$relative, 'com.kano.ylframework', $false, $false) } "SDK inputs remain forbidden after adapters move into main: $relative"
    }
    $rows = Call CapturePackages (Request)
    Check ([string]$rows[0]['revision'] -ceq $sha -and [string]$rows[0]['packageId'] -ceq $registered[0].packageId) 'Null PackageInfo.git did not use actual lock/source/locator evidence.'
    $ugui = [UnityEditor.PackageManager.PackageInfo]::new(); $ugui.name = 'com.unity.ugui'; $ugui.version = '2.0.0'; $ugui.source = [UnityEditor.PackageManager.PackageSource]::BuiltIn
    $ugui.packageId = $ugui.name + '@2.0.0'; $ugui.assetPath = 'Packages/' + $ugui.name
    $ugui.resolvedPath = Join-Path $hostRecord.path 'Library/PackageCache/com.unity.ugui@c0ef89c047f6'
    $uguiManifest = Join-Path $ugui.resolvedPath 'package.json'
    $uguiJson = @{ name = $ugui.name; version = $ugui.version; dependencies = @{}; _fingerprint = 'c0ef89c047f65e553b3df5413cb945480f368138' }
    Save $uguiManifest $uguiJson
    $newtonsoft = [UnityEditor.PackageManager.PackageInfo]::new(); $newtonsoft.name = 'com.unity.nuget.newtonsoft-json'; $newtonsoft.version = '3.2.2'; $newtonsoft.source = [UnityEditor.PackageManager.PackageSource]::Registry
    $newtonsoft.packageId = $newtonsoft.name + '@3.2.2'; $newtonsoft.assetPath = 'Packages/' + $newtonsoft.name; $newtonsoft.resolvedPath = Join-Path $hostRecord.path 'Library/PackageCache/com.unity.nuget.newtonsoft-json@fixture'
    Save (Join-Path $newtonsoft.resolvedPath 'package.json') @{ name = $newtonsoft.name; version = $newtonsoft.version; dependencies = @{}; _fingerprint = ('a' * 40) }
    [UnityEditor.PackageManager.PackageInfo]::Registered = @($registered + $ugui + $newtonsoft)
    $fullGraph = @()
    foreach ($api in [UnityEditor.PackageManager.PackageInfo]::GetAllRegisteredPackages()) { $fullGraph += ,(Boundary PackageRow @($api)) }
    Check ($fullGraph.Count -eq 5 -and [string]$fullGraph[3]['source'] -ceq 'BuiltIn' -and [string]$fullGraph[3]['fingerprint'] -ceq $uguiJson._fingerprint -and
        [string]$fullGraph[4]['source'] -ceq 'Registry' -and [string]$fullGraph[4]['fingerprint'] -ceq ('a' * 40)) 'The actual full graph rejected Unity 6 built-in UGUI or Registry Newtonsoft fingerprint observations.'
    foreach ($invalid in @($true, $null, 40, ('b' * 39), ('b' * 41), ('x' * 40), (('b' * 40) + "`n"), @('b'), @{ value = 'b' })) {
        $j = $uguiJson.Clone(); $j._fingerprint = $invalid; Save $uguiManifest $j
        Reject { foreach ($api in [UnityEditor.PackageManager.PackageInfo]::GetAllRegisteredPackages()) { $null = Boundary PackageRow @($api) } } 'Malformed official built-in fingerprint must fail the full graph'
    }
    Save $uguiManifest $uguiJson
    foreach ($source in @('Local', 'Embedded', 'Unknown')) {
        $ugui.source = [UnityEditor.PackageManager.PackageSource]::$source
        Reject { Boundary PackageRow @($ugui) } 'An official package name does not authorize other fingerprint sources'
    }
    $ugui.source = [UnityEditor.PackageManager.PackageSource]::BuiltIn
    foreach ($name in @('com.kano.ylframework', 'com.vendor.ugui', 'com.unityx.ugui')) {
        $ugui.name = $name; Reject { Boundary PackageRow @($ugui) } 'Non-official BuiltIn packages retain fail-closed fingerprint observations'
    }
    $ugui.name = 'com.unity.ugui'; [UnityEditor.PackageManager.PackageInfo]::Registered = $registered
    $sample = Join-Path $hostRecord.path 'Assets/Samples/Unexpected/sample.txt'
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($sample)); [IO.File]::WriteAllText($sample, 'not imported by this tool')
    try { Reject { Call CapturePackages (Request) } 'default-imported sample assets without a compiled script' }
    finally { [IO.File]::Delete($sample) }
    foreach ($fault in @('source', 'package-id', 'api-hash', 'api-revision', 'lock-hash', 'lock-source', 'lock-version', 'lock-missing', 'lock-dependency', 'root', 'bytes', 'extra-file', 'missing-file', 'public-json', 'bad-fingerprint', 'duplicate-json', 'foreign-package')) {
        $api = $registered[0]; $originalRoot = $api.resolvedPath; $originalId = $api.packageId; $packagePath = Join-Path $originalRoot 'package.json'; $originalJson = [IO.File]::ReadAllText($packagePath)
        $originalLock = [IO.File]::ReadAllText($lockPath); $license = Join-Path $originalRoot 'LICENSE.md'
        try {
            switch ($fault) {
                'source' { $api.source = [UnityEditor.PackageManager.PackageSource]::Local }
                'package-id' { $api.packageId = $api.name + '@git+file:///outside.git#' + $sha }
                'api-hash' { $api.git = [UnityEditor.PackageManager.GitInfo]::new(); $api.git.hash = 'b' * 40 }
                'api-revision' { $api.git = [UnityEditor.PackageManager.GitInfo]::new(); $api.git.revision = 'main' }
                'lock-hash' { $j = $originalLock | ConvertFrom-Json -AsHashtable; $j.dependencies[$api.name].hash = 'b' * 40; Save $lockPath $j }
                'lock-source' { $j = $originalLock | ConvertFrom-Json -AsHashtable; $j.dependencies[$api.name].source = 'local'; Save $lockPath $j }
                'lock-version' { $j = $originalLock | ConvertFrom-Json -AsHashtable; $j.dependencies[$api.name].version = 'https://different.invalid/p.git#' + $sha; Save $lockPath $j }
                'lock-missing' { Save $lockPath @{ dependencies = @{} } }
                'lock-dependency' { $j = $originalLock | ConvertFrom-Json -AsHashtable; $j.dependencies['com.kano.ylframework.excel'].dependencies = @{}; Save $lockPath $j }
                'root' { $api.resolvedPath = $repository }
                'bytes' { [IO.File]::AppendAllText($license, 'tampered') }
                'extra-file' { [IO.File]::WriteAllText((Join-Path $originalRoot 'extra.cs'), '// extra') }
                'missing-file' { [IO.File]::Delete($license) }
                'public-json' { $j = $originalJson | ConvertFrom-Json -AsHashtable; $j.extra.date = '2026-01-01T00:00:00+00:00'; Save $packagePath $j }
                'bad-fingerprint' { $j = $originalJson | ConvertFrom-Json -AsHashtable; $j['_fingerprint'] = $true; Save $packagePath $j }
                'duplicate-json' { [IO.File]::WriteAllText($packagePath, '{"name":"x","name":"y"}') }
                'foreign-package' { $foreign = [UnityEditor.PackageManager.PackageInfo]::new(); $foreign.name = 'com.developer.mcp'; [UnityEditor.PackageManager.PackageInfo]::Registered = @($registered + $foreign) }
            }
            Reject { Call CapturePackages (Request) } $fault
        } finally {
            $api.source = [UnityEditor.PackageManager.PackageSource]::Git; $api.git = $null; $api.resolvedPath = $originalRoot; $api.packageId = $originalId
            [IO.File]::WriteAllText($packagePath, $originalJson); [IO.File]::WriteAllText($license, 'fixture license'); [IO.File]::WriteAllText($lockPath, $originalLock)
            if ([IO.File]::Exists((Join-Path $originalRoot 'extra.cs'))) { [IO.File]::Delete((Join-Path $originalRoot 'extra.cs')) }
            [UnityEditor.PackageManager.PackageInfo]::Registered = $registered
        }
    }
    $p = Join-Path $registered[0].resolvedPath 'package.json'; $j = [IO.File]::ReadAllText($p) | ConvertFrom-Json -AsHashtable; $j['_fingerprint'] = 'b' * 40; Save $p $j
    $rows = Call CapturePackages (Request); Check ([string]$rows[0]['contentSha256'] -ceq $packages[0].contentSha256) 'Valid Git UPM fingerprint/formatting changed public semantics.'
    $registered[0].git = [UnityEditor.PackageManager.GitInfo]::new(); $registered[0].git.hash = $sha; $registered[0].git.revision = $sha
    $registered[0].packageId = $registered[0].name + '@git+' + $dependencies[$registered[0].name]
    $rows = Call CapturePackages (Request)
    Check ([string]$rows[0]['apiHash'] -ceq $sha -and [string]$rows[0]['revision'] -ceq $sha) 'Matching nonempty API evidence or UPM git+ syntax was rejected.'
    $registered[0].git = $null; $registered[0].packageId = $registered[0].name + '@' + $dependencies[$registered[0].name]
    $originalManifest = [IO.File]::ReadAllText($manifest); $originalLock = [IO.File]::ReadAllText($lockPath)
    try {
        $valid.gitRepository = 'https://example.invalid/@git+product.git'
        $markerDependencies = @{}; $markerLock = $originalLock | ConvertFrom-Json -AsHashtable -DateKind String
        foreach ($package in $registered) {
            $locator = $valid.gitRepository + '?path=/PackageSources/' + $package.name + '#' + $sha
            $markerDependencies[$package.name] = $locator; $markerLock.dependencies[$package.name].version = $locator; $package.packageId = $package.name + '@' + $locator
        }
        Save $manifest @{ dependencies = $markerDependencies }; Save $lockPath $markerLock
        $registered[0].packageId = $registered[0].packageId.Replace('/@git+product.git', '/@product.git')
        Reject { Call CapturePackages (Request) } 'API locator normalization must not rewrite repository path bytes'
    } finally {
        $valid.gitRepository = $repo; [IO.File]::WriteAllText($manifest, $originalManifest); [IO.File]::WriteAllText($lockPath, $originalLock)
        foreach ($package in $registered) { $package.packageId = $package.name + '@' + $dependencies[$package.name] }
    }
    foreach ($field in @('packageOrigin', 'gitRevision', 'gitRepository', 'cacheRoot', 'cacheId', 'hostId', 'resultId', 'inputHash')) {
        $request = Request; $request[$field] = [Newtonsoft.Json.Linq.JValue]::new($true)
        Reject { Call ValidateInvocation $request } "Boolean $field"
    }
    $request = Request; $request['gitRevision'] = [Newtonsoft.Json.Linq.JValue]::new('main'); Reject { Call ValidateInvocation $request } 'non-SHA revision'
    $request = Request; $request['gitRepository'] = [Newtonsoft.Json.Linq.JValue]::new('file:///C:/local.git'); Reject { Call ValidateInvocation $request } 'local Git URL'
    $request = Request; $request['cacheId'] = [Newtonsoft.Json.Linq.JValue]::new(('b' * 32)); Reject { Call ValidateInvocation $request } 'foreign cache owner'
    [UnityEngine.Application]::isBatchMode = $false; Reject { Call ValidateInvocation (Request) } 'interactive authority'; [UnityEngine.Application]::isBatchMode = $true

    $sdk = [UnityEditor.PackageManager.PackageInfo]::new(); $sdk.name = 'com.code-philosophy.hybridclr'; $sdk.version = '9.0.3'; $sdk.source = [UnityEditor.PackageManager.PackageSource]::Git
    $sdkLocator = 'https://gitee.com/focus-creative-games/hybridclr_unity.git#v9.0.3'; $sdk.packageId = $sdk.name + '@' + $sdkLocator
    $sdkHash = '6cadd24d101f4cbd00cd80190106f6621132d24d'
    Save $lockPath @{ dependencies = @{ $sdk.name = @{ version = $sdkLocator; source = 'git'; hash = $sdkHash } } }
    $gitMethod = [YLFramework.Testing.IntegrationEntry].GetMethod('GitRevision', [Reflection.BindingFlags]'NonPublic,Static')
    $arguments = [object[]]::new(4); $arguments[0] = $sdk; $arguments[1] = $sdkLocator; $arguments[2] = $sdkHash; $arguments[3] = 'v9.0.3'
    Check ($gitMethod.Invoke($null, $arguments) -ceq $sdkHash) 'HybridCLR with null GitInfo must bind its actual official SDK lock, not fail or copy the requested SHA.'
    Save $lockPath @{ dependencies = @{ $sdk.name = @{ version = $sdkLocator; source = 'git'; hash = ('b' * 40) } } }
    try { $null = $gitMethod.Invoke($null, $arguments); throw 'A contradictory HybridCLR lock was accepted.' }
    catch { if ($_.Exception.GetBaseException() -isnot [InvalidOperationException]) { throw }; $checks++ }

    $valid = $valid.Clone(); $valid.packageOrigin = 'File'; $valid.Remove('gitRepository'); $valid.Remove('gitRevision')
    $filePackages = @(); $fileRegistered = @(); $fileDependencies = @{}; $fileLocked = @{}
    foreach ($package in $registered) {
        $id = $package.name; $root = Join-Path $hostRecord.path "PackageSources/$id"; $null = [IO.Directory]::CreateDirectory($root)
        $json = [IO.File]::ReadAllText((Join-Path $package.resolvedPath 'package.json')) | ConvertFrom-Json -AsHashtable -DateKind String; $json.Remove('_fingerprint')
        Save (Join-Path $root 'package.json') $json; [IO.File]::WriteAllText((Join-Path $root 'LICENSE.md'), 'fixture license')
        Add-YLFixtureAssemblyPayload -Catalog $fixtureCatalog -PackageId $id -Root $root -Sources
        $content = Get-YLIntegrationPackageContent -Root $root -PackageId $id
        $filePackages += @{ name = $id; version = $package.version; root = $root; files = $content.files; contentSha256 = $content.sha256 }
        $fileDependencies[$id] = 'file:../PackageSources/' + $id
        $fileLocked[$id] = @{ source = 'local'; version = $fileDependencies[$id]; depth = 0; dependencies = $json.dependencies }
        $api = [UnityEditor.PackageManager.PackageInfo]::new(); $api.name = $id; $api.version = $package.version; $api.source = [UnityEditor.PackageManager.PackageSource]::Local
        $api.isDirectDependency = $true
        $api.assetPath = 'Packages/' + $id; $api.resolvedPath = $root; $api.packageId = $id + '@file:' + $root.Replace('\', '/'); $fileRegistered += $api
    }
    $valid.packages = $filePackages; Save $manifest @{ dependencies = $fileDependencies }; Save $lockPath @{ dependencies = $fileLocked }
    $valid.manifestSha256 = (Get-FileHash -LiteralPath $manifest).Hash; [UnityEditor.PackageManager.PackageInfo]::Registered = $fileRegistered
    $null = Call ValidateInvocation (Request); $rows = Call CapturePackages (Request)
    Check ([string]$rows[0]['source'] -ceq 'Local' -and [string]$rows[0]['contentSha256'] -ceq $filePackages[0].contentSha256) 'File no longer accepts complete owned staging with raw-byte identity.'
    $entryRelative = 'Assets/YLTestingIntegration/Editor/IntegrationEntry.cs'
    $entryPath = Join-Path $hostRecord.path $entryRelative
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($entryPath))
    [IO.File]::Copy((Join-Path $PSScriptRoot '../IntegrationEntry.cs'), $entryPath)
    $entryAssembly = [UnityEditor.Compilation.Assembly]::new(); $entryAssembly.name = 'Assembly-CSharp-Editor'; $entryAssembly.sourceFiles = @($entryRelative)
    $null = Boundary ValidateGraph @((Request), [UnityEditor.Compilation.Assembly[]]@(@(FixtureGraph Editor) + @($entryAssembly)), [UnityEditor.Compilation.Assembly[]]@(FixtureGraph Player)); $checks++
    foreach ($entrySource in @($entryPath, $entryPath.Replace('\', '/'))) {
        $entryAssembly.sourceFiles = @($entrySource)
        $null = Boundary ValidateGraph @((Request), [UnityEditor.Compilation.Assembly[]]@(@(FixtureGraph Editor) + @($entryAssembly)), [UnityEditor.Compilation.Assembly[]]@(FixtureGraph Player)); $checks++
    }
    $entryAssembly.sourceFiles = @($entryRelative)
    $path = Join-Path $fileRegistered[0].resolvedPath 'package.json'; $json = [IO.File]::ReadAllText($path) | ConvertFrom-Json -AsHashtable; $json['_fingerprint'] = $sha; Save $path $json
    Reject { Call CapturePackages (Request) } 'File must not accept Git-only package-manager fingerprint normalization'
    [IO.Directory]::Delete((Join-Path $hostRecord.path 'PackageSources'), $true)
    function New-PublishedFixture([string] $Origin, [string] $Integration = 'ExcelGameplay') {
        $script:valid = $script:valid.Clone(); $script:valid.packageOrigin = $Origin; $script:valid.integration = $Integration
        $script:valid.Remove('gitRepository'); $script:valid.Remove('gitRevision')
        $script:valid.releaseManifestSha256 = 'c' * 64
        $suffix = switch ($Integration) { HybridCLR { 'hybridclr' } DOTween { 'dotween' } default { 'excel' } }
        $script:valid.entryPackage = 'com.kano.ylframework.' + $suffix
        $script:valid.entryPackages = @($script:valid.entryPackage)
        if ($Integration -ceq 'ExcelGameplay') { $script:valid.entryPackages += 'com.kano.ylframework.gameplay' }
        $fields = Get-YLRegistryRequestFields -Source @{ packageOrigin = $Origin; registryUrl = 'https://package.openupm.com'; registryVersion = '0.1.0-preview.1'; release = @{ sha256 = ('c' * 64) } } -EntryPackage $script:valid.entryPackage -EntryPackages $script:valid.entryPackages
        foreach ($field in $fields.GetEnumerator()) { $script:valid[$field.Key] = $field.Value }
        $script:valid.Remove('dotweenSdkMode'); $script:valid.Remove('sdk')
        $sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
        $ids = @(Get-YLUpmPackages -Repository $sourceRoot -Ids $script:valid.entryPackages -Catalog $fixtureCatalog).id
        $script:publishedApis = @(); $expected = @(); $direct = @{}; $locks = @{}
        foreach ($id in $ids) {
            $deps = Get-YLFixtureInternalDependencies -PackageId $id -Version '0.1.0-preview.1'
            if ($id -ceq 'com.kano.ylframework.hybridclr') { $deps['com.code-philosophy.hybridclr'] = '9.0.3' }
            $pairs = @($deps.Keys | Sort-Object -CaseSensitive | ForEach-Object { '"' + $_ + '":"' + $deps[$_] + '"' }) -join ','
            $canonical = '{"dependencies":{' + $pairs + '},"name":"' + $id + '","version":"0.1.0-preview.1"}'
            $root = Join-Path $hostRecord.path "Library/PackageCache/$Origin-$id"; $null = [IO.Directory]::CreateDirectory($root)
            [IO.File]::WriteAllText((Join-Path $root 'package.json'), $canonical)
            [IO.File]::WriteAllText((Join-Path $root 'LICENSE.md'), 'fixture license')
            Add-YLFixtureAssemblyPayload -Catalog $fixtureCatalog -PackageId $id -Root $root -Sources
            $files = (Get-YLIntegrationPackageContent -Root $root -PackageId $id -NormalizeManifest).files
            $lines = ($files | ForEach-Object { $_.path + "`0" + $_.sha256 + "`n" }) -join ''
            $locator = if ($Origin -ceq 'Registry') { '0.1.0-preview.1' } else { 'file:' + (Join-Path $sandbox ($id + '.tgz')).Replace('\', '/') }
            if ($Origin -ceq 'LocalTarball') { [IO.File]::WriteAllText($locator.Substring(5), 'locator fixture, not a verified tarball') }
            $expected += @{ name = $id; version = '0.1.0-preview.1'; root = $null; locator = $locator; dependencies = $deps; files = $files
                contentSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($lines))).ToLowerInvariant() }
            $api = [UnityEditor.PackageManager.PackageInfo]::new(); $api.name = $id; $api.version = '0.1.0-preview.1'; $api.assetPath = 'Packages/' + $id; $api.resolvedPath = $root
            $api.source = [UnityEditor.PackageManager.PackageSource]::$Origin; $api.packageId = $id + '@' + $locator
            $api.isDirectDependency = $Origin -ceq 'LocalTarball' -or $id -cin $script:valid.entryPackages
            if ($Origin -ceq 'Registry') { $api.registry = [UnityEditor.PackageManager.RegistryInfo]::new(); $api.registry.url = $script:valid.registryUrl }
            $api.dependencies = @($deps.Keys | ForEach-Object { $d = [UnityEditor.PackageManager.DependencyInfo]::new(); $d.name = $_; $d.version = $deps[$_]; $d })
            $script:publishedApis += $api
            if ($api.isDirectDependency) { $direct[$id] = $locator }
            $locks[$id] = @{ source = $(if ($Origin -ceq 'Registry') { 'registry' } else { 'local-tarball' }); version = $locator
                depth = $(if ($api.isDirectDependency) { 0 } else { 1 }); dependencies = $deps }
            if ($Origin -ceq 'Registry') { $locks[$id].url = $script:valid.registryUrl }
        }
        $script:valid.packages = $expected
        $mode = if ($Integration -ceq 'DOTween') { 'Required' } else { $null }
        $script:valid.assemblies = @(Get-YLPackageAssemblyPlan -Catalog $fixtureCatalog -Packages $expected -Profile $Integration -DOTweenSdkMode $mode -MetadataOnly | Where-Object enabled)
        Save $manifest @{ dependencies = $direct }; Save $lockPath @{ dependencies = $locks }
        $script:valid.manifestSha256 = (Get-FileHash -LiteralPath $manifest).Hash
        [UnityEditor.PackageManager.PackageInfo]::Registered = $script:publishedApis
    }
    foreach ($integration in @('ExcelGameplay', 'Excel')) {
        New-PublishedFixture Registry $integration
        $excelRoot = @($publishedApis | Where-Object name -CEQ 'com.kano.ylframework.excel')[0].resolvedPath
        $miniDll = Join-Path $excelRoot 'Editor/Plugins/MiniExcel.dll'
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($miniDll))
        [IO.File]::WriteAllBytes($miniDll, [IO.File]::ReadAllBytes([Newtonsoft.Json.Linq.JObject].Assembly.Location))
        $license = Join-Path $excelRoot 'Editor/Licenses/LICENSE.txt'; $notice = Join-Path $excelRoot 'Editor/Licenses/NOTICE.txt'
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($license))
        [IO.File]::WriteAllText($license, 'NoUnity fixture license, not the MiniExcel SDK'); [IO.File]::WriteAllText($notice, 'NoUnity fixture attribution')
        $backend = @{ version = '1.31.2'; assemblySha256 = (Get-FileHash -LiteralPath $miniDll).Hash.ToLowerInvariant(); licenseFile = 'Licenses/LICENSE.txt'; attributionFile = 'Licenses/NOTICE.txt'; license = 'Apache-2.0' }
        $approval = Join-Path $excelRoot 'Editor/Dependencies.json'; Save $approval @{ backend = $backend }
        $valid.sdk = $backend.Clone()
        $miniEditor = [UnityEditor.Compilation.Assembly[]]@(FixtureGraph Editor)
        $excelAssembly = @($miniEditor | Where-Object name -CEQ 'YLFramework.Excel.Editor')[0]; $excelAssembly.compiledAssemblyReferences = @($miniDll)
        $adapter = @($miniEditor | Where-Object name -CEQ 'YLFramework.Domain.Excel.Editor')
        Check (($adapter.Count -eq 1) -eq ($integration -ceq 'ExcelGameplay')) 'Ordinary Excel must omit the Gameplay adapter; both roots must enable it.'
        if ($adapter.Count -eq 1) {
            $adapter[0].defines = @('YL_GAMEPLAY_PACKAGE'); $adapter[0].assemblyReferences = @($miniEditor | Where-Object name -CEQ 'YLFramework.Gameplay.Editor')
        }
        $request = Request
        $selected = @($request['packages'] | ForEach-Object {
            $package = $_.DeepClone(); $package['root'] = [Newtonsoft.Json.Linq.JValue]::new(@($publishedApis | Where-Object name -CEQ ([string]$package['name']))[0].resolvedPath); ,$package
        })
        $observed = MiniExcel $request $miniEditor $selected
        Check ([string]$observed['sdk']['sha256'] -ceq $backend.assemblySha256 -and [string]$observed['sdk']['license'] -ceq 'Apache-2.0') "$integration must verify the actual SDK binary identity and license."
        foreach ($symbol in @('YL_DOMAIN_EXCEL', 'YL_GAMEPLAY_PACKAGE', 'YL_HYBRIDCLR_9_0_3')) {
            Reject { MiniExcel $request $miniEditor $selected @($symbol) } "global $symbol cannot replace a package gate"
        }
        foreach ($fault in @('request-hash', 'request-version', 'metadata-hash', 'license', 'attribution', 'license-escape')) {
            $r = $request.DeepClone(); $changed = $backend.Clone()
            try {
                switch ($fault) {
                    'request-hash' { $r['sdk']['assemblySha256'] = [Newtonsoft.Json.Linq.JValue]::new(('d' * 64)) }
                    'request-version' { $r['sdk']['version'] = [Newtonsoft.Json.Linq.JValue]::new('1.31.1') }
                    'metadata-hash' { $changed.assemblySha256 = 'd' * 64 }
                    'license' { [IO.File]::Delete($license) }
                    'attribution' { [IO.File]::Delete($notice) }
                    'license-escape' { $changed.licenseFile = '../LICENSE.md' }
                }
                Save $approval @{ backend = $changed }
                Reject { MiniExcel $r $miniEditor $selected } "$integration MiniExcel $fault"
            } finally {
                Save $approval @{ backend = $backend }; [IO.File]::WriteAllText($license, 'NoUnity fixture license, not the MiniExcel SDK'); [IO.File]::WriteAllText($notice, 'NoUnity fixture attribution')
            }
        }
        if ($integration -ceq 'ExcelGameplay') {
            Reject { MiniExcel $request @($miniEditor | Where-Object name -CNE 'YLFramework.Domain.Excel.Editor') $selected } 'Both roots require the enabled adapter'
            $adapter[0].defines = @(); Reject { MiniExcel $request $miniEditor $selected } 'The adapter requires its package-scoped define'; $adapter[0].defines = @('YL_GAMEPLAY_PACKAGE')
            $adapter[0].assemblyReferences = @(); Reject { MiniExcel $request $miniEditor $selected } 'The adapter requires its actual Gameplay Editor reference'
        } else {
            $borrowed = [UnityEditor.Compilation.Assembly]::new(); $borrowed.name = 'YLFramework.Domain.Excel.Editor'
            Reject { MiniExcel $request @($miniEditor + $borrowed) $selected } 'Ordinary Excel must not load the Gameplay adapter'
        }
        foreach ($path in @($miniDll, $license, $notice, $approval)) { [IO.File]::Delete($path) }
    }
    foreach ($origin in @('Registry', 'LocalTarball')) {
        New-PublishedFixture $origin
        $null = Call ValidateInvocation (Request)
        $rows = Call CapturePackages (Request)
        Check (($valid.packages.name -join ',') -ceq 'com.kano.ylframework,com.kano.ylframework.data-assets,com.kano.ylframework.excel,com.kano.ylframework.gameplay' -and
            ($valid.entryPackages -join ',') -ceq 'com.kano.ylframework.excel,com.kano.ylframework.gameplay' -and $rows.Count -eq 4 -and [bool]$rows[0]['isDirectDependency'] -eq ($origin -ceq 'LocalTarball') -and
            [string]$rows[0]['contentSha256'] -ceq $valid.packages[0].contentSha256 -and $rows[0]['dependencies'] -is [Newtonsoft.Json.Linq.JObject]) 'Published source/API/directness/semantic evidence is incomplete.'
        Check ((@($valid.packages[2].dependencies.Keys | Sort-Object) -join ',') -ceq 'com.kano.ylframework,com.kano.ylframework.data-assets' -and
            (@($valid.packages[3].dependencies.Keys) -join ',') -ceq 'com.kano.ylframework') 'The published fixture must not make Excel depend on Gameplay.'
        $depths = Call ArtifactDepths (Request)
        Check ($depths.Count -eq 4 -and $depths['com.kano.ylframework.excel'] -eq 0 -and $depths['com.kano.ylframework.gameplay'] -eq 0 -and
            $depths['com.kano.ylframework'] -eq 1 -and $depths['com.kano.ylframework.data-assets'] -eq 1) 'ArtifactDepths must traverse both roots, not put Gameplay behind Excel.'
        $publishedManifest = [IO.File]::ReadAllText($manifest) | ConvertFrom-Json -AsHashtable
        $publishedLock = [IO.File]::ReadAllText($lockPath) | ConvertFrom-Json -AsHashtable
        $expectedDirect = if ($origin -ceq 'Registry') { 'com.kano.ylframework.excel,com.kano.ylframework.gameplay' }
            else { 'com.kano.ylframework,com.kano.ylframework.data-assets,com.kano.ylframework.excel,com.kano.ylframework.gameplay' }
        Check ((@($publishedManifest.dependencies.Keys | Sort-Object) -join ',') -ceq $expectedDirect) "$origin must declare exactly its permitted direct roots."
        foreach ($row in $rows) {
            $id = [string]$row['name']; $direct = $origin -ceq 'LocalTarball' -or $id -cin @('com.kano.ylframework.excel', 'com.kano.ylframework.gameplay')
            Check ([bool]$row['isDirectDependency'] -eq $direct -and $publishedLock.dependencies[$id].depth -eq $(if ($direct) { 0 } else { 1 })) "$origin API/lock directness and depth differ for $id."
        }
        foreach ($fault in @('missing', 'boolean', 'scalar', 'single', 'reversed', 'duplicate', 'extra', 'wrong-primary')) {
            $r = Request
            switch ($fault) {
                'missing' { $null = $r.Remove('entryPackages') }
                'boolean' { $r['entryPackages'] = [Newtonsoft.Json.Linq.JValue]::new($true) }
                'scalar' { $r['entryPackages'] = [Newtonsoft.Json.Linq.JValue]::new('com.kano.ylframework.excel') }
                'single' { $r['entryPackages'] = [Newtonsoft.Json.Linq.JArray]::FromObject(@('com.kano.ylframework.excel')) }
                'reversed' { $r['entryPackages'] = [Newtonsoft.Json.Linq.JArray]::FromObject(@('com.kano.ylframework.gameplay', 'com.kano.ylframework.excel')) }
                'duplicate' { $r['entryPackages'] = [Newtonsoft.Json.Linq.JArray]::FromObject(@('com.kano.ylframework.excel', 'com.kano.ylframework.excel')) }
                'extra' { $r['entryPackages'].Add('com.kano.ylframework') }
                'wrong-primary' { $r['entryPackage'] = [Newtonsoft.Json.Linq.JValue]::new('com.kano.ylframework.gameplay') }
            }
            Reject { Call ValidateInvocation $r } "$origin ExcelGameplay $fault entry roots"
        }
        if ($origin -ceq 'LocalTarball') {
            $publishedApis[0].packageId = $publishedApis[0].packageId.Replace('/', '\')
            $null = Call CapturePackages (Request); $checks++
        }
        foreach ($fault in @('source', 'version', 'url', 'package-id', 'direct', 'second-root-direct', 'manifest-direct', 'missing-second-root', 'official-direct', 'sdk-direct', 'lock-source', 'lock-version', 'lock-url', 'lock-depth', 'second-root-depth', 'lock-dependencies', 'api-dependencies', 'expected-dependencies', 'expected-content', 'expected-files', 'outside-root', 'source-copy', 'fingerprint', 'unknown-managed', 'trailing-json', 'duplicate-json', 'comment-json', 'trailing-comma', 'single-quote-json', 'sdk-payload')) {
            New-PublishedFixture $origin
            $api = $publishedApis[0]; $path = Join-Path $api.resolvedPath 'package.json'
            switch ($fault) {
                'source' { $api.source = [UnityEditor.PackageManager.PackageSource]::Git }
                'version' { $api.version = '0.1.0-preview.2' }
                'url' { if ($origin -ceq 'Registry') { $api.registry.url += '/wrong' } else { $api.packageId += '.wrong' } }
                'package-id' { $api.packageId = $api.name + '@file:C:/outside' }
                'direct' { $api.isDirectDependency = -not $api.isDirectDependency }
                'second-root-direct' { $publishedApis[3].isDirectDependency = $false }
                'manifest-direct' { $m = [IO.File]::ReadAllText($manifest) | ConvertFrom-Json -AsHashtable; if ($origin -ceq 'Registry') { $m.dependencies[$api.name] = $api.version } else { $m.dependencies.Remove($api.name) }; Save $manifest $m }
                'missing-second-root' { $m = [IO.File]::ReadAllText($manifest) | ConvertFrom-Json -AsHashtable; $m.dependencies.Remove('com.kano.ylframework.gameplay'); Save $manifest $m }
                'official-direct' { $m = [IO.File]::ReadAllText($manifest) | ConvertFrom-Json -AsHashtable; $m.dependencies['com.unity.ugui'] = '1.0.0'; Save $manifest $m }
                'sdk-direct' { $m = [IO.File]::ReadAllText($manifest) | ConvertFrom-Json -AsHashtable; $m.dependencies['com.code-philosophy.hybridclr'] = '9.0.3'; Save $manifest $m }
                'api-dependencies' { $publishedApis[2].dependencies = @() }
                'expected-dependencies' { $valid.packages[2].dependencies = @{} }
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
                'sdk-payload' { [IO.File]::WriteAllText((Join-Path $publishedApis[2].resolvedPath 'DOTween.dll'), 'MZsdk') }
                default {
                    $l = [IO.File]::ReadAllText($lockPath) | ConvertFrom-Json -AsHashtable; $entry = $l.dependencies[$api.name]
                    switch ($fault) { 'lock-source' { $entry.source = 'local' } 'lock-version' { $entry.version = '0.1.0-preview.2' } 'lock-url' { if ($origin -ceq 'Registry') { $entry.url += '/wrong' } else { $entry.source = 'local' } } 'lock-depth' { $entry.depth = 7 } 'second-root-depth' { $l.dependencies['com.kano.ylframework.gameplay'].depth = 1 } 'lock-dependencies' { $l.dependencies[$valid.entryPackage].dependencies = @{} } }
                    Save $lockPath $l
                }
            }
            Reject { Call CapturePackages (Request) } "$origin $fault"
            if ([IO.Directory]::Exists((Join-Path $hostRecord.path 'PackageSources'))) { [IO.Directory]::Delete((Join-Path $hostRecord.path 'PackageSources'), $true) }
            if ([IO.File]::Exists((Join-Path $publishedApis[2].resolvedPath 'DOTween.dll'))) { [IO.File]::Delete((Join-Path $publishedApis[2].resolvedPath 'DOTween.dll')) }
        }
        New-PublishedFixture $origin
        foreach ($field in @('entryPackage', 'releaseManifestSha256', 'registryUrl', 'registryVersion')) {
            $r = Request; $r[$field] = [Newtonsoft.Json.Linq.JValue]::new($true); Reject { Call ValidateInvocation $r } "$origin typed $field"
        }
        $fingerprintLength = if ($origin -ceq 'LocalTarball') { 32 } else { 40 }
        $path = Join-Path $publishedApis[0].resolvedPath 'package.json'; $j = [IO.File]::ReadAllText($path) | ConvertFrom-Json -AsHashtable; $j._fingerprint = 'B' * $fingerprintLength; Save $path $j
        $rows = Call CapturePackages (Request)
        Check ([string]$rows[0]['fingerprint'] -ceq ('B' * $fingerprintLength) -and [string]$rows[0]['contentSha256'] -ceq $valid.packages[0].contentSha256) "$origin fingerprint observation/normalization failed."
        $j._fingerprint = 'B' * $(if ($origin -ceq 'LocalTarball') { 40 } else { 32 }); Save $path $j
        Reject { Call CapturePackages (Request) } "$origin must not inherit the other source fingerprint format"
    }
    foreach ($origin in @('Registry', 'LocalTarball')) {
        New-PublishedFixture $origin HybridCLR
        $valid.sdk = @{ source = 'Registry'; version = '9.0.3'; registryUrl = 'https://package.openupm.com'; repositoryRevision = $sdkHash }
        $sdk = [UnityEditor.PackageManager.PackageInfo]::new(); $sdk.name = 'com.code-philosophy.hybridclr'; $sdk.version = '9.0.3'; $sdk.source = [UnityEditor.PackageManager.PackageSource]::Registry
        $sdk.registry = [UnityEditor.PackageManager.RegistryInfo]::new(); $sdk.registry.url = 'https://package.openupm.com'; $sdk.packageId = $sdk.name + '@9.0.3'; $sdk.assetPath = 'Packages/' + $sdk.name
        $sdk.resolvedPath = Join-Path $cache.path 'upm/hybridclr'; $null = [IO.Directory]::CreateDirectory($sdk.resolvedPath)
        Save (Join-Path $sdk.resolvedPath 'package.json') @{ name = $sdk.name; version = $sdk.version }
        $sdk.git = [UnityEditor.PackageManager.GitInfo]::new(); $sdk.git.hash = 'd' * 40
        $l = [IO.File]::ReadAllText($lockPath) | ConvertFrom-Json -AsHashtable
        $l.dependencies[$sdk.name] = @{ source = 'registry'; version = '9.0.3'; url = $sdk.registry.url; depth = 1; dependencies = @{} }; Save $lockPath $l
        [UnityEditor.PackageManager.PackageInfo]::Registered = @($publishedApis + $sdk)
        $null = Call ValidateInvocation (Request); $null = Call CapturePackages (Request)
        $row = Call CaptureRegistrySdk (Request)
        Check ([string]$row['source'] -ceq 'Registry' -and [string]$row['registryUrl'] -ceq 'https://package.openupm.com' -and
            [string]$row['repositoryRevision'] -ceq $sdkHash -and $row['revision'].Type -eq [Newtonsoft.Json.Linq.JTokenType]::Null -and -not [bool]$row['isDirectDependency']) 'HybridCLR Registry metadata was treated as actual Git checkout evidence.'
        $baselineLock = [IO.File]::ReadAllText($lockPath)
        foreach ($fault in @('sdk-source', 'sdk-version', 'sdk-url', 'sdk-id', 'sdk-direct', 'sdk-lock-source', 'sdk-lock-url', 'sdk-lock-version', 'sdk-lock-depth', 'sdk-lock-deps', 'sdk-manifest-direct', 'sdk-request-revision')) {
            $r = Request
            try {
                switch ($fault) {
                    'sdk-source' { $sdk.source = [UnityEditor.PackageManager.PackageSource]::Git }
                    'sdk-version' { $sdk.version = '9.0.4' }
                    'sdk-url' { $sdk.registry.url = 'https://wrong.invalid' }
                    'sdk-id' { $sdk.packageId += '.wrong' }
                    'sdk-direct' { $sdk.isDirectDependency = $true }
                    'sdk-manifest-direct' { $m = [IO.File]::ReadAllText($manifest) | ConvertFrom-Json -AsHashtable; $m.dependencies[$sdk.name] = '9.0.3'; Save $manifest $m }
                    'sdk-request-revision' { $r['sdk']['repositoryRevision'] = [Newtonsoft.Json.Linq.JValue]::new(('b' * 40)) }
                    default {
                        $l = $baselineLock | ConvertFrom-Json -AsHashtable; $entry = $l.dependencies[$sdk.name]
                        switch ($fault) { 'sdk-lock-source' { $entry.source = 'git' } 'sdk-lock-url' { $entry.url = 'https://wrong.invalid' } 'sdk-lock-version' { $entry.version = '9.0.4' } 'sdk-lock-depth' { $entry.depth = 0 } 'sdk-lock-deps' { $entry.dependencies['com.fake.sdk'] = '1.0.0' } }
                        Save $lockPath $l
                    }
                }
                Reject { Call CaptureRegistrySdk $r } "$origin $fault"
            } finally {
                $sdk.source = [UnityEditor.PackageManager.PackageSource]::Registry; $sdk.version = '9.0.3'; $sdk.registry.url = 'https://package.openupm.com'; $sdk.packageId = $sdk.name + '@9.0.3'; $sdk.isDirectDependency = $false
                [IO.File]::WriteAllText($lockPath, $baselineLock)
                $m = [IO.File]::ReadAllText($manifest) | ConvertFrom-Json -AsHashtable; $m.dependencies.Remove($sdk.name); Save $manifest $m
            }
        }
    }
    New-PublishedFixture Registry DOTween
    $valid.dotweenSdkMode = 'Absent'; $valid.assemblies = @($valid.assemblies | Where-Object name -cne 'YLFramework.Actions.DOTween')
    $null = Call ValidateInvocation (Request); $null = Call CapturePackages (Request)
    $empty = [UnityEditor.Compilation.Assembly[]]@()
    $selected = [Newtonsoft.Json.Linq.JObject[]]@()
    $negative = Boundary ValidateDotween @((Request), $empty, $empty, $selected, [string[]]@())
    Check ([string]$negative['status'] -ceq 'NegativeNoSdk' -and [string]$negative['mode'] -ceq 'Absent') 'DOTween Absent must not be reported as a positive SDK integration.'
    $borrowedAdapter = [UnityEditor.Compilation.Assembly]::new(); $borrowedAdapter.name = 'Ordinary'
    $borrowedAdapter.sourceFiles = @('Packages/com.kano.ylframework.dotween/Runtime/Product.cs')
    Reject { Boundary ValidateGraph @((Request), [UnityEditor.Compilation.Assembly[]]@(@(FixtureGraph Editor) + @($entryAssembly)),
        [UnityEditor.Compilation.Assembly[]]@(@(FixtureGraph Player) + @($borrowedAdapter))) } 'a non-product assembly cannot borrow disabled adapter sources'
    $wrapper = [UnityEditor.Compilation.Assembly]::new(); $wrapper.name = 'YLFramework.Actions.DOTween'
    Reject { Boundary ValidateDotween @((Request), [UnityEditor.Compilation.Assembly[]]@($wrapper), $empty, $selected, [string[]]@()) } 'Absent Editor wrapper'
    Reject { Boundary ValidateDotween @((Request), $empty, [UnityEditor.Compilation.Assembly[]]@($wrapper), $selected, [string[]]@()) } 'Absent Player wrapper'
    Reject { Boundary ValidateDotween @((Request), $empty, $empty, $selected, [string[]]@('DOTWEEN')) } 'Absent global DOTWEEN'
    $sdkPath = Join-Path $hostRecord.path 'Assets/Plugins/Demigiant/DOTween/DOTween.dll'; $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($sdkPath)); [IO.File]::WriteAllText($sdkPath, 'not an SDK')
    Reject { Boundary ValidateDotween @((Request), $empty, $empty, $selected, [string[]]@()) } 'Absent SDK deployment'
    [IO.Directory]::Delete((Join-Path $hostRecord.path 'Assets/Plugins'), $true)
    $r = Request; $r['dotweenSdkMode'] = [Newtonsoft.Json.Linq.JValue]::new('optional'); Reject { Call ValidateInvocation $r } 'unknown DOTween SDK mode'
    $r = Request; $null = $r.Remove('dotweenSdkMode'); Reject { Call ValidateInvocation $r } 'omitting DOTween mode must still require wrapper assembly'
    $valid.Remove('dotweenSdkMode'); $valid.assemblies = @(Get-YLPackageAssemblyPlan -Catalog $fixtureCatalog -Packages $valid.packages -Profile DOTween -DOTweenSdkMode Required -MetadataOnly | Where-Object enabled)
    $null = Call ValidateInvocation (Request)
    Reject { Boundary ValidateDotween @((Request), $empty, $empty, $selected, [string[]]@()) } 'Required mode cannot pass without real wrapper/SDK'
    $module = Join-Path $hostRecord.path 'Assets/Plugins/Demigiant/DOTween/Modules/DOTweenModuleFixture.cs'
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($module))
    [IO.File]::WriteAllText($sdkPath, 'MZ NoUnity binary fixture, not the actual SDK'); [IO.File]::WriteAllText($module, '// NoUnity module fixture')
    $valid.sdk = @{ version = '1.2.825'; assemblySha256 = (Get-FileHash -LiteralPath $sdkPath).Hash.ToLowerInvariant() }
    $wrapper.defines = @('DOTWEEN'); $wrapper.compiledAssemblyReferences = @($sdkPath)
    $modules = [UnityEditor.Compilation.Assembly]::new(); $modules.name = 'Assembly-CSharp'; $modules.sourceFiles = @($module)
    $sdkEditor = [UnityEditor.Compilation.Assembly[]]@($wrapper, $modules)
    $sdkReport = Boundary ValidateDotween @((Request), $sdkEditor, [UnityEditor.Compilation.Assembly[]]@($wrapper), $selected, [string[]]@('DOTWEEN'))
    Check ([string]$sdkReport['mode'] -ceq 'Required' -and [string]$sdkReport['sha256'] -ceq $valid.sdk.assemblySha256) 'Required mode lost the approved SDK binary identity checks.'
    $null = Boundary ValidateGraph @((Request), [UnityEditor.Compilation.Assembly[]]@(@(FixtureGraph Editor) + @($modules, $entryAssembly)), [UnityEditor.Compilation.Assembly[]]@(FixtureGraph Player)); $checks++
    $engineEditor = [UnityEditor.Compilation.Assembly[]]@(@(FixtureGraph Editor) + @($modules, $entryAssembly))
    $enginePlayer = [UnityEditor.Compilation.Assembly[]]@(FixtureGraph Player)
    @($engineEditor | Where-Object name -CEQ 'YLFramework.Runtime')[0].compiledAssemblyReferences = @('Unity.Android.Types.dll', 'Unity.Android.Gradle.dll', 'Unity.Android.GradleProject.dll')
    $null = Boundary ValidateGraph @((Request), $engineEditor, $enginePlayer); $checks++
    @($enginePlayer | Where-Object name -CEQ 'YLFramework.Runtime')[0].compiledAssemblyReferences = @('Unity.Android.Types.dll')
    Reject { Boundary ValidateGraph @((Request), $engineEditor, $enginePlayer) } 'Editor platform tooling must remain absent from Player Runtime'
    foreach ($side in @('Editor', 'Player')) {
        $requiredEditor = [UnityEditor.Compilation.Assembly[]]@(@(FixtureGraph Editor) + @($modules, $entryAssembly))
        $requiredPlayer = [UnityEditor.Compilation.Assembly[]]@(FixtureGraph Player)
        foreach ($wrapperGraph in @($requiredEditor) + @($requiredPlayer) | Where-Object name -CEQ 'YLFramework.Actions.DOTween') {
            $wrapperGraph.compiledAssemblyReferences = @($sdkPath); $wrapperGraph.defines = @('DOTWEEN')
        }
        $runtime = if ($side -ceq 'Editor') { @($requiredEditor | Where-Object name -CEQ 'YLFramework.Runtime')[0] }
            else { @($requiredPlayer | Where-Object name -CEQ 'YLFramework.Runtime')[0] }
        $runtime.compiledAssemblyReferences = @($sdkPath)
        Reject { Boundary ValidateGraph @((Request), $requiredEditor, $requiredPlayer) } "Required SDK must not become a main Runtime dependency in $side"
    }
    foreach ($fault in @('global-define', 'wrapper-define', 'binary-path', 'binary-hash', 'uncompiled-modules', 'missing-modules')) {
        $r = Request; $global = [string[]]@('DOTWEEN')
        try {
            switch ($fault) {
                'global-define' { $global = [string[]]@() }
                'wrapper-define' { $wrapper.defines = @() }
                'binary-path' { $wrapper.compiledAssemblyReferences = @((Join-Path $repository 'DOTween.dll')) }
                'binary-hash' { $r['sdk']['assemblySha256'] = [Newtonsoft.Json.Linq.JValue]::new(('d' * 64)) }
                'uncompiled-modules' { $modules.sourceFiles = @() }
                'missing-modules' { [IO.File]::Delete($module) }
            }
            Reject { Boundary ValidateDotween @($r, $sdkEditor, [UnityEditor.Compilation.Assembly[]]@($wrapper), $selected, $global) } "Required SDK $fault"
        } finally {
            $wrapper.defines = @('DOTWEEN'); $wrapper.compiledAssemblyReferences = @($sdkPath); $modules.sourceFiles = @($module)
            [IO.File]::WriteAllText($module, '// NoUnity module fixture')
        }
    }
    [IO.Directory]::Delete((Join-Path $hostRecord.path 'Assets/Plugins'), $true)

    New-PublishedFixture Registry
    $source = Join-Path $publishedApis[0].resolvedPath 'Runtime/Example.cs'; $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($source)); [IO.File]::WriteAllText($source, '// source fixture')
    $valid.packages[0].files += @{ path = 'Runtime/Example.cs'; sha256 = (Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant() }
    $editor = [UnityEditor.Compilation.Assembly[]]@(@(FixtureGraph Editor) + @($entryAssembly))
    $player = [UnityEditor.Compilation.Assembly[]]@(FixtureGraph Player)
    $assembly = @($editor | Where-Object name -CEQ 'YLFramework.Runtime')[0]
    $null = Boundary ValidateGraph @((Request), $editor, $player); $checks++
    Reject { Boundary ValidateGraph @((Request), [UnityEditor.Compilation.Assembly[]]@($editor | Where-Object name -CNE 'YLFramework.Excel.Editor'), $player) } 'a declared product Editor assembly cannot disappear'
    foreach ($fault in @('Tests', 'Mcp', 'sample', 'player-excel', 'player-gameplay-excel', 'player-miniexcel-reference', 'asset-copy', 'cache-source-escape', 'package-source-escape', 'unknown-package', 'missing-source', 'player-source-drift', 'player-duplicate-source', 'undeclared-product-assembly', 'ordinary-borrowed-runtime', 'ordinary-editor-source-in-player')) {
        $bad = [UnityEditor.Compilation.Assembly]::new(); $bad.name = 'Ordinary'; $bad.sourceFiles = @($assembly.sourceFiles)
        switch ($fault) {
            'Tests' { $bad.name = 'YLFramework.Tests' }
            'Mcp' { $bad.compiledAssemblyReferences = @('ReflectorNet.dll') }
            'sample' { $bad.sourceFiles = @('Packages/com.kano.ylframework/Samples~/Example.cs') }
            'player-excel' { $bad.name = 'YLFramework.Excel.Editor' }
            'player-gameplay-excel' { $bad.name = 'YLFramework.Domain.Excel.Editor' }
            'player-miniexcel-reference' { $bad.compiledAssemblyReferences = @('MiniExcel.dll') }
            'asset-copy' { $bad.sourceFiles = @('Assets/Product/Example.cs') }
            'cache-source-escape' { $bad.sourceFiles = @((Join-Path $repository 'outside.cs')) }
            'package-source-escape' { $bad.sourceFiles = @('Packages/com.kano.ylframework/../../outside.cs') }
            'unknown-package' { $bad.sourceFiles = @('Packages/com.foreign.sdk/Runtime/Example.cs') }
            'missing-source' { $bad.sourceFiles = @('Packages/com.kano.ylframework/Runtime/Missing.cs') }
            'player-source-drift' { $bad.name = 'YLFramework.Runtime'; $bad.sourceFiles = @() }
            'player-duplicate-source' { $bad.name = 'YLFramework.Runtime'; $bad.sourceFiles = @($assembly.sourceFiles[0], $assembly.sourceFiles[0]) }
            'undeclared-product-assembly' { $bad.name = 'YLFramework.Unlisted.Adapter' }
            'ordinary-borrowed-runtime' { $bad.sourceFiles = @($assembly.sourceFiles) }
            'ordinary-editor-source-in-player' { $bad.sourceFiles = @($editor | Where-Object name -CEQ 'YLFramework.Excel.Editor')[0].sourceFiles }
        }
        $badPlayer = if ($bad.name -ceq 'YLFramework.Runtime') { @($player | Where-Object name -CNE 'YLFramework.Runtime') + @($bad) } else { @($player) + @($bad) }
        Reject { Boundary ValidateGraph @((Request), $editor, [UnityEditor.Compilation.Assembly[]]$badPlayer) } "per-source graph closure $fault"
    }
    $oldEnvironment = @{}
    try {
        foreach ($name in @('UPM_CACHE_ROOT', 'UPM_CACHE_PATH', 'UPM_NPM_CACHE_PATH')) {
            $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
            [Environment]::SetEnvironmentVariable($name, (Join-Path $cache.path $name))
        }
        $observed = Boundary CacheEnvironment @()
        foreach ($name in $oldEnvironment.Keys) { Check ([string]$observed[$name] -ceq (Join-Path $cache.path $name)) "Cache environment was not observed from the actual process: $name" }
    } finally { foreach ($name in $oldEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $oldEnvironment[$name]) } }
    "PASS: $checks IntegrationEntry NoUnity typed ownership/API/lock/content/graph/SDK guards. NOT Unity compilation or Registry acceptance."
    $passed = $true
} finally {
    foreach ($lease in $leases) { $lease.Dispose() }
    if ($passed -and [IO.Directory]::Exists($sandbox)) { $null = @(Get-YLTestTreeFiles $sandbox); [IO.Directory]::Delete($sandbox, $true) }
    elseif ([IO.Directory]::Exists($sandbox)) { "Artifacts retained: $sandbox" }
}
