#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param([Parameter(Mandatory)][string] $TemporaryRoot)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Test-YLIntegrationPackages.ps1') -WorkspaceRoot $TemporaryRoot -Unity 'C:\unused\Unity.exe' -Integration Excel
$sandbox = Join-Path (Get-YLTestPath $TemporaryRoot) ('yl-integration-entry-' + [Guid]::NewGuid().ToString('N'))
$repository = Join-Path $sandbox 'Repository'; $null = [IO.Directory]::CreateDirectory($repository)
$workspace = Initialize-YLTestWorkspace -WorkspaceRoot (Join-Path $sandbox 'Workspace') -Repository $repository
$hostRecord = New-YLTestDirectory -Workspace $workspace -Kind Host -Key Entry
$result = New-YLTestDirectory -Workspace $workspace -Kind Result -Key Entry
$cache = New-YLTestDirectory -Workspace $workspace -Kind Cache -Key Entry
$leases = @($hostRecord, $result, $cache | ForEach-Object { Lock-YLTestDirectory -Workspace $workspace -Path $_.path })
$checks = 0
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
function Reject([scriptblock] $Action, [string] $Reason) {
    try { & $Action | Out-Null } catch {
        if ($_.Exception -isnot [InvalidOperationException] -and $_.Exception -isnot [Newtonsoft.Json.JsonReaderException]) { throw }
        $script:checks++; return
    }
    throw "Entry accepted: $Reason"
}
try {
    $references = @([IO.Directory]::EnumerateFiles((Join-Path $PSHOME 'ref'), '*.dll')) + @([Newtonsoft.Json.Linq.JObject].Assembly.Location)
    Add-Type -Path @((Join-Path $PSScriptRoot '../IntegrationEntry.cs'), (Join-Path $PSScriptRoot 'Test-YLIntegrationPackagesNoUnityStubs.cs')) -ReferencedAssemblies $references -CompilerOptions @('/define:UNITY_EDITOR', '/langversion:9.0', '/nowarn:1701')
    [UnityEngine.Application]::dataPath = Join-Path $hostRecord.path 'Assets'; $null = [IO.Directory]::CreateDirectory([UnityEngine.Application]::dataPath)
    $output = Join-Path $result.path 'Compile'; $null = [IO.Directory]::CreateDirectory($output)
    $sha = '0123456789abcdef0123456789abcdef01234567'; $repo = 'https://example.invalid/product.git'
    $packages = @(); $registered = @(); $dependencies = @{}; $locked = @{}
    foreach ($id in @('com.kano.ylframework', 'com.kano.ylframework.excel')) {
        $p = Join-Path $hostRecord.path "Library/PackageCache/$id"
        $deps = @{}; if ($id -ceq 'com.kano.ylframework.excel') { $deps['com.kano.ylframework'] = '0.1.0-preview.1' }
        Save (Join-Path $p 'package.json') @{ name = $id; version = '0.1.0-preview.1'; dependencies = $deps; extra = @{ date = '2026-01-01T00:00:00Z' } }
        [IO.File]::WriteAllText((Join-Path $p 'LICENSE.md'), 'fixture license')
        $content = Get-YLIntegrationPackageContent -Root $p -PackageId $id -NormalizeManifest
        $packages += @{ name = $id; version = '0.1.0-preview.1'; files = $content.files; contentSha256 = $content.sha256 }
        $locator = $repo + '?path=/PackageSources/' + $id + '#' + $sha
        $dependencies[$id] = $locator; $locked[$id] = @{ source = 'git'; version = $locator; hash = $sha; depth = 0; dependencies = $deps }
        $api = [UnityEditor.PackageManager.PackageInfo]::new(); $api.name = $id; $api.version = '0.1.0-preview.1'; $api.source = [UnityEditor.PackageManager.PackageSource]::Git
        $api.assetPath = 'Packages/' + $id; $api.resolvedPath = $p; $api.packageId = $id + '@' + $locator; $registered += $api
    }
    $manifest = Join-Path $hostRecord.path 'Packages/manifest.json'; Save $manifest @{ dependencies = $dependencies }
    $lockPath = Join-Path $hostRecord.path 'Packages/packages-lock.json'; Save $lockPath @{ dependencies = $locked }
    $valid = @{ schemaVersion = 1; purpose = 'YLTesting.IntegrationPackages'; integration = 'Excel'; hostId = $hostRecord.id; resultId = $result.id; workspaceId = $workspace.id
        resultRoot = $result.path; output = $output; unityVersion = '2022.3.62f3'; inputHash = ('a' * 64); manifestSha256 = (Get-FileHash -LiteralPath $manifest).Hash
        packageOrigin = 'GitRemote'; gitRepository = $repo; gitRevision = $sha; cacheRoot = $cache.path; cacheId = $cache.id; expectedTemplateHash = ('a' * 64); packages = $packages; assemblies = (Get-YLIntegrationDefinition Excel).assemblies }
    Save (Join-Path $hostRecord.path '.yl-inputs.json') @{ schemaVersion = 1; purpose = 'YLTesting.HostInputs'; hostId = $hostRecord.id; inputHash = ('a' * 64) }
    [UnityEditor.PackageManager.PackageInfo]::Registered = $registered
    function Request { return ,([Newtonsoft.Json.Linq.JObject]::Parse((ConvertTo-Json -Depth 30 -InputObject $valid))) }
    $null = Call ValidateInvocation (Request); $checks++
    $rows = Call CapturePackages (Request)
    Check ([string]$rows[0]['revision'] -ceq $sha -and [string]$rows[0]['packageId'] -ceq $registered[0].packageId) 'Null PackageInfo.git did not use actual lock/source/locator evidence.'
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
        $content = Get-YLIntegrationPackageContent -Root $root -PackageId $id
        $filePackages += @{ name = $id; version = $package.version; root = $root; files = $content.files; contentSha256 = $content.sha256 }
        $fileDependencies[$id] = 'file:../PackageSources/' + $id
        $fileLocked[$id] = @{ source = 'local'; version = $fileDependencies[$id]; dependencies = $json.dependencies }
        $api = [UnityEditor.PackageManager.PackageInfo]::new(); $api.name = $id; $api.version = $package.version; $api.source = [UnityEditor.PackageManager.PackageSource]::Local
        $api.assetPath = 'Packages/' + $id; $api.resolvedPath = $root; $api.packageId = $id + '@file:' + $root.Replace('\', '/'); $fileRegistered += $api
    }
    $valid.packages = $filePackages; Save $manifest @{ dependencies = $fileDependencies }; Save $lockPath @{ dependencies = $fileLocked }
    $valid.manifestSha256 = (Get-FileHash -LiteralPath $manifest).Hash; [UnityEditor.PackageManager.PackageInfo]::Registered = $fileRegistered
    $null = Call ValidateInvocation (Request); $rows = Call CapturePackages (Request)
    Check ([string]$rows[0]['source'] -ceq 'Local' -and [string]$rows[0]['contentSha256'] -ceq $filePackages[0].contentSha256) 'File no longer accepts complete owned staging with raw-byte identity.'
    $path = Join-Path $fileRegistered[0].resolvedPath 'package.json'; $json = [IO.File]::ReadAllText($path) | ConvertFrom-Json -AsHashtable; $json['_fingerprint'] = $sha; Save $path $json
    Reject { Call CapturePackages (Request) } 'File must not accept Git-only package-manager fingerprint normalization'
    "PASS: $checks IntegrationEntry NoUnity typed ownership/API/lock/content guards. NOT Unity compilation or GitRemote acceptance."
} finally {
    foreach ($lease in $leases) { $lease.Dispose() }
    if ([IO.Directory]::Exists($sandbox)) { $null = @(Get-YLTestTreeFiles $sandbox); [IO.Directory]::Delete($sandbox, $true) }
}
