#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param([Parameter(Mandatory)][string] $TemporaryRoot)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Common/Workspace.ps1')
$checks = 0
function Check([bool] $Condition, [string] $Reason) { if (-not $Condition) { throw $Reason }; $script:checks++ }
function Reject([scriptblock] $Action, [string] $Pattern) {
    try { & $Action | Out-Null } catch {
        if ($_.Exception -is [Management.Automation.CommandNotFoundException] -or $_.Exception.Message -notmatch $Pattern) { throw }
        $script:checks++; return
    }
    throw "Expected refusal: $Pattern"
}
function Save([string] $Path, [object] $Value) {
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 40), [Text.UTF8Encoding]::new($false))
}
function Hash([string] $Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Copy-Value([object] $Value) { return ,(ConvertTo-Json -InputObject $Value -Depth 40 | ConvertFrom-Json -AsHashtable -NoEnumerate -DateKind String) }
$common = Join-Path $PSScriptRoot '../Common/RegistryAuthoring.ps1'
Check ([IO.File]::Exists($common)) 'Authoring behavior is missing: no explicit Samples API import and actual author-tool operation exists.'
. $common
Check ($null -ne (Get-Command Invoke-YLRegistryAuthoring -ErrorAction SilentlyContinue)) 'The shared authoring operation is missing.'
$root = Join-Path (Get-YLTestPath $TemporaryRoot) ('yl-authoring-contract-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($root)
try {
    $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
    $workspace = Initialize-YLTestWorkspace -WorkspaceRoot (Join-Path $root 'Workspace') -Repository $repository
    $run = New-YLTestDirectory -Workspace $workspace -Kind Result -Key AuthoringContracts
    $references = @([IO.Directory]::EnumerateFiles((Join-Path $PSHOME 'ref'), '*.dll')) + @([Newtonsoft.Json.Linq.JObject].Assembly.Location)
    $stubAssembly = Join-Path $root 'RegistryAuthoring.Contracts.dll'
    $null = Add-Type -Path @((Join-Path $PSScriptRoot '../RegistryAuthoringEntry.cs'), (Join-Path $PSScriptRoot '../PackageCatalogContract.cs'), (Join-Path $PSScriptRoot 'RegistryAuthoringUnityStubs.cs')) -ReferencedAssemblies $references -OutputAssembly $stubAssembly -CompilerOptions @('/define:UNITY_EDITOR', '/langversion:9.0', '/nowarn:1701')
    $null = [Reflection.Assembly]::LoadFrom($stubAssembly)
    $sampleAssemblies = [ordered]@{
        'YLFramework.ResourceStartup.Sample' = 'namespace YLFramework.Samples.Resource { public class ResourceStartupExampleData : UnityEngine.ScriptableObject {} }'
        'YLFramework.ResourceStartup.Sample.Editor' = 'namespace YLFramework.Samples.Resource.Editor { public static class ResourceStartupExampleEditor { public static string Create() => UnityEditor.ContractAssets.CreateResource(); } }'
        'YLFramework.HybridCLR.Sample.Support' = 'namespace SampleSupport { public class Marker {} }'
        'YLFramework.HybridCLR.Sample.Entry' = 'namespace YLFramework.Samples.HybridCLR.HotUpdate { public class ExampleBehaviour : UnityEngine.Component {} }'
        'YLFramework.HybridCLR.Sample.Host' = 'namespace SampleHost { public class Marker {} }'
        'YLFramework.HybridCLR.Sample.Editor' = 'namespace YLFramework.Samples.HybridCLR.Editor { public static class HybridCLRExampleAssets { public static UnityEngine.Object Create(string assets, string baseline) => UnityEditor.ContractAssets.CreateHybrid(assets, baseline); } }'
    }
    foreach ($name in $sampleAssemblies.Keys) {
        $dll = Join-Path $root ($name + '.dll')
        $syntax = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($sampleAssemblies[$name])
        $metadata = [Microsoft.CodeAnalysis.MetadataReference[]]@(($references + @($stubAssembly)) | ForEach-Object { [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($_) })
        $options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
        $compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create($name, [Microsoft.CodeAnalysis.SyntaxTree[]]@($syntax), $metadata, $options)
        $stream = [IO.File]::Open($dll, 'CreateNew', 'Write', 'None')
        try { $emitted = $compilation.Emit($stream) } finally { $stream.Dispose() }
        Check $emitted.Success ("Named sample fixture compilation failed: " + ($emitted.Diagnostics -join '; '))
        $null = [Reflection.Assembly]::LoadFrom($dll)
    }
    $version = '0.1.0-preview.1'; $url = 'https://package.openupm.com'
    $payloadRoot = Join-Path $root 'Expected'; $null = [IO.Directory]::CreateDirectory($payloadRoot)
    $script:payloads = @()
    . (Join-Path $PSScriptRoot 'PackageCatalogFixtures.ps1')
    $packages = @(foreach ($id in @((Read-YLPackageCatalog).packages.id)) {
        $packageRoot = Join-Path $payloadRoot $id; $null = [IO.Directory]::CreateDirectory($packageRoot)
        $deps = Get-YLFixtureInternalDependencies -PackageId $id -Version $version
        if ($id.EndsWith('.hybridclr')) { $deps['com.code-philosophy.hybridclr'] = '9.0.3' }
        [string[]]$dependencyNames = @($deps.Keys); [Array]::Sort($dependencyNames, [StringComparer]::Ordinal)
        $orderedDeps = [ordered]@{}; foreach ($dependency in $dependencyNames) { $orderedDeps[$dependency] = $deps[$dependency] }; $deps = $orderedDeps
        $definition = [ordered]@{ dependencies = $deps; displayName = $(if ($id -ceq 'com.kano.ylframework') { 'YLFramework' } else { 'YLFramework HybridCLR Integration' }); name = $id }
        if ($id -cin @('com.kano.ylframework', 'com.kano.ylframework.hybridclr')) {
            $hybrid = $id.EndsWith('.hybridclr'); $sample = if ($hybrid) { 'HybridCLR' } else { 'ResourceStartup' }
            $definition.samples = @([ordered]@{ displayName = $(if ($hybrid) { 'HybridCLR Module' } else { 'Resource Startup' }); path = "Samples~/$sample" })
            $names = if ($hybrid) { @('YLFramework.HybridCLR.Sample.Editor', 'YLFramework.HybridCLR.Sample.Entry', 'YLFramework.HybridCLR.Sample.Host', 'YLFramework.HybridCLR.Sample.Support') } else { @('YLFramework.ResourceStartup.Sample', 'YLFramework.ResourceStartup.Sample.Editor') }
            foreach ($name in $names) {
                $folder = if ($name.EndsWith('.Editor')) { 'Editor' } elseif ($name.EndsWith('.Entry')) { 'HotEntry' } elseif ($name.EndsWith('.Host')) { 'Host' } elseif ($name.EndsWith('.Support')) { 'HotSupport' } else { 'Runtime' }
                $directory = Join-Path $packageRoot "Samples~/$sample/$folder"; $null = [IO.Directory]::CreateDirectory($directory)
                [IO.File]::WriteAllText((Join-Path $directory ($name + '.asmdef')), ('{"name":"' + $name + '"}'))
                [IO.File]::WriteAllText((Join-Path $directory 'Example.cs'), '// Contract sample source, not a Unity installation.')
            }
            foreach ($path in @([IO.Directory]::EnumerateFiles($packageRoot, '*', 'AllDirectories')) + @([IO.Directory]::EnumerateDirectories($packageRoot, '*', 'AllDirectories'))) {
                [IO.File]::WriteAllText(($path + '.meta'), ("fileFormatVersion: 2`nguid: " + [Guid]::NewGuid().ToString('N') + "`n"))
            }
        }
        $definition.version = $version
        [IO.File]::WriteAllText((Join-Path $packageRoot 'package.json'), (ConvertTo-Json -InputObject $definition -Depth 10 -Compress), [Text.UTF8Encoding]::new($false))
        [string[]]$paths = @([IO.Directory]::EnumerateFiles($packageRoot, '*', 'AllDirectories') | ForEach-Object { [IO.Path]::GetRelativePath($packageRoot, $_).Replace('\', '/') })
        [Array]::Sort($paths, [StringComparer]::Ordinal)
        $files = @(foreach ($path in $paths) { @{ path = $path; sha256 = Hash (Join-Path $packageRoot $path); contentSha256 = Hash (Join-Path $packageRoot $path) } })
        $inventory = [Text.StringBuilder]::new(); foreach ($file in $files) { $null = $inventory.Append($file.path).Append([char]0).Append($file.contentSha256).Append("`n") }
        $payloadHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($inventory.ToString()))).ToLowerInvariant()
        $script:payloads += @{ id = $id; root = $packageRoot; files = $files; manifest = $definition }
        @{ id = $id; version = $version; dependencies = $deps; assetName = "$id-$version.tgz"; sha256 = ('a' * 64); integrity = 'sha512-' + [Convert]::ToBase64String([byte[]]::new(64)); files = $files; payloadSha256 = $payloadHash }
    })
    $releasePath = Join-Path $root 'release-manifest.json'
    # These are fixture identities, not a claim that a GitHub workflow or Registry release exists.
    Save $releasePath @{ schemaVersion = 1; sourceCommit = ('a' * 40); toolchain = @{ node = '22.23.3'; npm = '10.9.9'; powershell = '7.6.9' }; producer = @{ kind = 'GitHubActions'; repository = 'KONOK-A/YLFramework-dev'; workflow = 'NoUnityFixture'; runId = '1'; runAttempt = '1' }; packages = $packages }
    $source = Get-YLRegistrySource -PackageSource Registry -Arguments @{ RegistryUrl = $url; RegistryVersion = $version; ReleaseManifest = $releasePath }
    $exe = (Get-Process -Id $PID).Path; $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
    $editor = [pscustomobject]@{ version = '2022.3.62f3'; productVersion = $info.ProductVersion; fileVersion = "$($info.FileMajorPart).$($info.FileMinorPart).$($info.FileBuildPart)" }
    $invoke = @{ Workspace = $workspace; Source = $source; Unity = $exe; Editor = $editor; EntryPackage = 'com.kano.ylframework'; Result = $run; TimeoutSeconds = 30 }
    foreach ($origin in @('File', 'GitRemote', 'Embedded')) {
        $bad = $source.Clone(); $bad.packageOrigin = $origin
        Reject { Invoke-YLRegistryAuthoring @invoke -Source $bad } 'Registry|LocalTarball'
    }
    Reject { Invoke-YLRegistryAuthoring @invoke -EntryPackage 'com.kano.ylframework.excel' } 'entry|Entry|sample|Sample|ValidateSet'
    $entryType = [YLFramework.Testing.RegistryAuthoringEntry]
    $begin = $entryType.GetMethod('Begin', [Reflection.BindingFlags]'NonPublic,Static')
    $resume = $entryType.GetMethod('Resume', [Reflection.BindingFlags]'NonPublic,Static')
    function Call-Entry([string[]] $Argv, [switch] $ResumeImport) {
        $arguments = [object[]]::new(1); $arguments[0] = $Argv
        $method = if ($ResumeImport) { $resume } else { $begin }
        try { $null = $method.Invoke($null, $arguments) } catch { throw $_.Exception.GetBaseException() }
    }
    Reject { [YLFramework.Testing.RegistryAuthoringEntry]::Execute() } 'request|explicit'
    Check ([UnityEditor.EditorApplication]::ExitCodes.Count -eq 0) 'A no-request invocation acquired permission to exit an Editor.'
    # Only downloads and the external Unity launch are replaced; the helper, entry, factories and byte receipts execute.
    function Initialize-YLRegistryExpectedInputs {
        param($Source, $Output)
        if ($script:fault -ceq 'registry-unavailable') { throw 'PrerequisiteNotMet: Registry fixture is unavailable; no File/Git fallback.' }
        return @{ root = $payloadRoot; packages = $script:payloads; purpose = 'NoUnityFixtureNotRegistryEvidence' }
    }
    function Get-YLHybridClrRegistrySdk { param($Approval, $Output, $RegistryUrl); return @{ dependencies = @{}; files = @(); evidence = @{ fixture = $true }; source = 'Registry'; version = '9.0.3' } }
    function Assert-YLHybridClrRegistryInstalled { param($Sdk, $Root) }
    $script:fault = ''; $script:catalogGuardsChecked = $false; $script:starts = [Collections.Generic.List[object]]::new()
    $script:realIdle = (Get-Command Assert-YLUnityHostIdle).ScriptBlock; $script:idleCounts = @{}
    function Assert-YLUnityHostIdle([object] $HostRecord) {
        & $script:realIdle $HostRecord
        if (-not $script:idleCounts.ContainsKey($HostRecord.id)) { $script:idleCounts[$HostRecord.id] = 0 }; $script:idleCounts[$HostRecord.id]++
        if ($script:fault -ceq 'rewrite-producer-receipt' -and $script:idleCounts[$HostRecord.id] -eq 3) {
            $path = Join-Path $HostRecord.path 'Assets/RegistryAuthoring/Editor/RegistryAuthoringEntry.cs'; [IO.File]::AppendAllText($path, '// external code change')
            $receiptPath = Join-Path $HostRecord.path '.yl-inputs.json'; $receipt = Read-YLPackageEvidenceJson $receiptPath
            ($receipt.files | Where-Object { $_.path -ceq 'Assets/RegistryAuthoring/Editor/RegistryAuthoringEntry.cs' }).sha256 = Hash $path
            Save $receiptPath $receipt
        }
        $receiptPath = Join-Path $HostRecord.path '.yl-authoring-outputs.json'
        if ($script:fault -ceq 'rewrite-output-receipt' -and [IO.File]::Exists($receiptPath)) {
            $receipt = Read-YLPackageEvidenceJson $receiptPath; $file = @($receipt.files | Where-Object { $_.path.EndsWith('/Project.asset') })[0]
            [IO.File]::AppendAllText((Join-Path $HostRecord.path $file.path), 'external asset edit'); $file.sha256 = Hash (Join-Path $HostRecord.path $file.path)
            Save $receiptPath $receipt
        }
    }
    function Invoke-YLTestProcess {
        param([Diagnostics.ProcessStartInfo] $StartInfo, [string] $Log, [int] $TimeoutMilliseconds)
        $argv = [string[]]@($StartInfo.ArgumentList)
        Check ('-quit' -cnotin $argv -and '-batchmode' -cin $argv) 'Authoring must not use -quit or an interactive Editor.'
        $requestPath = $argv[[Array]::IndexOf($argv, '-ylRegistryAuthoringRequest') + 1]; $request = Read-YLPackageEvidenceJson $requestPath
        if ($request.packageOrigin -ceq 'LocalTarball') { Check ($null -eq $request.registryUrl -and $null -eq $request.registryVersion -and $request.resolutionRegistryUrl -ceq $url) 'LocalTarball source identity reused the Registry routing constant as own package provenance.' }
        $script:starts.Add(@{ host = $StartInfo.WorkingDirectory; cache = $StartInfo.Environment['UPM_CACHE_ROOT']; phase = $request.phase })
        $hostRoot = $StartInfo.WorkingDirectory
        $catalogPath = Join-Path $hostRoot 'YLTesting/PackageCatalog.json'
        Check ([IO.File]::Exists($catalogPath) -and (Hash $catalogPath) -ceq (Hash (Join-Path $repository 'Tools/Packaging/PackageCatalog.json'))) 'Authoring must carry the actual maintained catalog as a frozen host input.'
        $projectVersion = "m_EditorVersion: $($editor.version)`nm_EditorVersionWithRevision: $($editor.version) ($($editor.productVersion.Split('_')[-1]))`n"
        Check ([IO.File]::ReadAllText((Join-Path $hostRoot 'ProjectSettings/ProjectVersion.txt')) -ceq $projectVersion) 'The frozen producer must supply the complete Editor version/revision bytes instead of adopting a first-launch rewrite.'
        [UnityEngine.Application]::dataPath = (Join-Path $hostRoot 'Assets').Replace('\', '/'); [UnityEngine.Application]::isBatchMode = $true
        [UnityEditor.EditorApplication]::applicationPath = $StartInfo.FileName.Replace('\', '/'); [UnityEditor.EditorApplication]::Reset()
        [UnityEditor.EditorUtility]::scriptCompilationFailed = $script:fault -ceq 'compile'
        [UnityEditor.AssetDatabase]::Host = $hostRoot; [UnityEditor.ContractAssets]::Fault = $script:fault
        $saved = @{}
        foreach ($key in @('UPM_CACHE_ROOT', 'UPM_CACHE_PATH', 'UPM_NPM_CACHE_PATH')) { $saved[$key] = [Environment]::GetEnvironmentVariable($key); [Environment]::SetEnvironmentVariable($key, $StartInfo.Environment[$key]) }
        try {
            if ($request.phase -ceq 'Inspect') {
                $ids = if ($request.entryPackage.EndsWith('.hybridclr')) { @('com.kano.ylframework', 'com.kano.ylframework.hybridclr', 'com.code-philosophy.hybridclr') } else { @('com.kano.ylframework') }
                $apis = @(foreach ($id in $ids) {
                    $api = [UnityEditor.PackageManager.PackageInfo]::new(); $api.name = $id; $api.version = $(if ($id -ceq 'com.code-philosophy.hybridclr') { '9.0.3' } else { $version })
                    $api.packageId = $id + '@' + $api.version; $api.source = [UnityEditor.PackageManager.PackageSource]::Registry
                    $api.isDirectDependency = $id -ceq $request.entryPackage; $api.resolvedPath = Join-Path $hostRoot ('Library/PackageCache/' + $id)
                    $null = [IO.Directory]::CreateDirectory($api.resolvedPath)
                    $api.registry = [UnityEditor.PackageManager.RegistryInfo]::new(); $api.registry.url = $url
                    $payload = @($script:payloads | Where-Object { $_.id -ceq $id })
                    $deps = if ($payload.Count -eq 1) { $payload[0].manifest.dependencies } else { @{} }
                    if ($payload.Count -eq 1) {
                        foreach ($file in $payload[0].files) { $target = Join-Path $api.resolvedPath $file.path; $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)); [IO.File]::Copy((Join-Path $payload[0].root $file.path), $target) }
                    } else { [IO.File]::WriteAllText((Join-Path $api.resolvedPath 'package.json'), '{"dependencies":{},"name":"com.code-philosophy.hybridclr","version":"9.0.3"}') }
                    if ($request.packageOrigin -ceq 'LocalTarball' -and $id -cne 'com.code-philosophy.hybridclr') { $api.source = [UnityEditor.PackageManager.PackageSource]::LocalTarball; $m = Read-YLPackageEvidenceJson (Join-Path $hostRoot 'Packages/manifest.json'); $api.packageId = $id + '@' + $m.dependencies[$id]; $api.isDirectDependency = $true }
                    if ($payload.Count -eq 1) {
                        $manifestPath = Join-Path $api.resolvedPath 'package.json'; $managed = Read-YLPackageEvidenceJson $manifestPath
                        $length = if ($api.source.ToString() -ceq 'LocalTarball') { 32 } else { 40 }
                        if ($script:fault -ceq 'wrong-source-fingerprint') { $length = if ($length -eq 32) { 40 } else { 32 } }
                        $managed['_fingerprint'] = 'A' * $length; Save $manifestPath $managed
                    }
                    $api.dependencies = @($deps.GetEnumerator() | ForEach-Object { $d = [UnityEditor.PackageManager.DependencyInfo]::new(); $d.name = $_.Key; $d.version = $_.Value; $d })
                    $api
                })
                [UnityEditor.PackageManager.PackageInfo]::Registered = $apis
                $lock = @{ dependencies = @{} }
                foreach ($api in $apis) {
                    $deps = @{}; foreach ($dependency in $api.dependencies) { $deps[$dependency.name] = $dependency.version }
                    $lock.dependencies[$api.name] = @{ version = $(if ($api.source.ToString() -ceq 'LocalTarball') { $api.packageId.Substring($api.name.Length + 1) } else { $api.version }); source = $(if ($api.source.ToString() -ceq 'LocalTarball') { 'local-tarball' } else { 'registry' }); url = $url; depth = $(if ($api.isDirectDependency) { 0 } else { 1 }); dependencies = $deps }
                }
                Save (Join-Path $hostRoot 'Packages/packages-lock.json') $lock
            }
            [UnityEditor.PackageManager.UI.Sample]::Configure($hostRoot, $request.entryPackage, $version)
            [UnityEditor.Compilation.CompilationPipeline]::Configure($hostRoot, $root, ($script:fault -ceq 'compiled-before-import'), ($script:fault -ceq 'missing-sample-assembly'), ($script:fault -ceq 'wrong-sample-source'))
            if ($script:fault -ceq 'import-false') { [UnityEditor.PackageManager.UI.Sample]::ImportResult = $false }
            if ($script:fault -ceq 'unknown-sample' -and $request.phase -ceq 'Import') { $p = Join-Path $hostRoot 'Assets/Samples/unknown.txt'; $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($p)); [IO.File]::WriteAllText($p, 'user data') }
            if ($script:fault -ceq 'unknown-empty-sample' -and $request.phase -ceq 'Import') { $null = [IO.Directory]::CreateDirectory((Join-Path $hostRoot 'Assets/Samples/UserEmpty')) }
            if ($script:fault -ceq 'unknown-empty-folder' -and $request.phase -ceq 'Import') { $null = [IO.Directory]::CreateDirectory((Join-Path $hostRoot 'Assets/UserEmpty')) }
            if ($script:fault -ceq 'unknown-output' -and $request.phase -ceq 'Author') { $p = Join-Path $hostRoot 'Assets/YLFrameworkData'; $null = [IO.Directory]::CreateDirectory($p); [IO.File]::WriteAllText((Join-Path $p 'user.txt'), 'must survive') }
            if ($script:fault -ceq 'reopen-drift' -and $request.phase -ceq 'Reopen') { [IO.File]::AppendAllText((Join-Path $hostRoot ($request.assetDirectory + '/Project.asset')), 'external edit') }
            if ($script:fault -ceq 'sample-drift' -and $request.phase -ceq 'Author') { $p = [UnityEditor.PackageManager.UI.Sample]::Current.importPath; [IO.File]::AppendAllText((Join-Path $p 'Editor/Example.cs'), 'external edit') }
            if ($script:fault -ceq 'package-drift' -and $request.phase -ceq 'Author') { [IO.File]::AppendAllText((Join-Path $hostRoot 'Library/PackageCache/com.kano.ylframework/package.json'), ' ') }
            if ($script:fault -ceq 'cache-environment') { [Environment]::SetEnvironmentVariable('UPM_CACHE_PATH', (Join-Path $root 'foreign-cache')) }
            if ($script:fault -ceq 'owner') { $request.hostId = 'f' * 32; Save $requestPath $request }
            if ($script:fault -ceq 'interactive') { [UnityEngine.Application]::isBatchMode = $false }
            if ($script:fault -ceq 'wrong-editor') { $request.unityProductVersion = '6000.3.27f1_not-the-executable'; Save $requestPath $request }
            if ($script:fault -ceq 'manifest-extra') { $p = Join-Path $hostRoot 'Packages/manifest.json'; $m = Read-YLPackageEvidenceJson $p; $m.unapproved = $true; Save $p $m }
            if ($script:fault -ceq 'input-bytes') { [IO.File]::AppendAllText((Join-Path $hostRoot 'Assets/RegistryAuthoring/Editor/RegistryAuthoringEntry.cs'), '// drift') }
            if ($script:fault -ceq 'duplicate-request') { $argv += @('-ylRegistryAuthoringRequest', $requestPath) }
            if (-not $script:catalogGuardsChecked) {
                $inputPath = Join-Path $hostRoot '.yl-inputs.json'
                $requestBytes = [IO.File]::ReadAllBytes($requestPath); $inputBytes = [IO.File]::ReadAllBytes($inputPath)
                $catalogBytes = [IO.File]::ReadAllBytes($catalogPath); $releaseBytes = [IO.File]::ReadAllBytes($request.releaseManifest)
                foreach ($fault in @('missing-catalog', 'catalog-bytes', 'unbound-catalog', 'duplicate-catalog-binding', 'missing-catalog-schema', 'catalog-schema-type', 'catalog-schema-version', 'catalog-extra-field', 'duplicate-catalog-package', 'missing-release-schema', 'release-schema-type', 'unknown-release-package', 'duplicate-release-package', 'missing-release-package')) {
                    $bad = Copy-Value $request; $inputs = Read-YLPackageEvidenceJson $inputPath
                    $catalog = Read-YLPackageEvidenceJson $catalogPath; $frozen = Read-YLPackageEvidenceJson $request.releaseManifest
                    try {
                        switch ($fault) {
                            'missing-catalog' { [IO.File]::Delete($catalogPath) }
                            'catalog-bytes' { [IO.File]::AppendAllText($catalogPath, ' ') }
                            'unbound-catalog' { $inputs.files = @($inputs.files | Where-Object path -cne 'YLTesting/PackageCatalog.json') }
                            'duplicate-catalog-binding' { $inputs.files += Copy-Value @($inputs.files | Where-Object path -ceq 'YLTesting/PackageCatalog.json')[0] }
                            'missing-catalog-schema' { $catalog.Remove('schemaVersion') }
                            'catalog-schema-type' { $catalog.schemaVersion = $true }
                            'catalog-schema-version' { $catalog.schemaVersion = 2 }
                            'catalog-extra-field' { $catalog.unapproved = $true }
                            'duplicate-catalog-package' { $catalog.packages[1] = Copy-Value $catalog.packages[0] }
                            'missing-release-schema' { $frozen.Remove('schemaVersion') }
                            'release-schema-type' { $frozen.schemaVersion = $true }
                            'unknown-release-package' { $frozen.packages[1].id = 'com.kano.ylframework.unknown' }
                            'duplicate-release-package' { $frozen.packages[1] = Copy-Value $frozen.packages[0] }
                            'missing-release-package' { $frozen.packages = @($frozen.packages | Where-Object id -cne 'com.kano.ylframework.data-assets') }
                        }
                        if ($fault -cmatch '^(?:missing-catalog-schema|catalog-schema-|catalog-extra-field|duplicate-catalog-package)') {
                            Save $catalogPath $catalog; $bad.catalogSha256 = Hash $catalogPath
                            @($inputs.files | Where-Object path -ceq 'YLTesting/PackageCatalog.json')[0].sha256 = $bad.catalogSha256
                        }
                        Save $inputPath $inputs; $bad.inputReceiptSha256 = Hash $inputPath
                        Save $request.releaseManifest $frozen; $bad.releaseManifestSha256 = Hash $request.releaseManifest
                        Save $requestPath $bad
                        Reject { Call-Entry $argv } 'catalog|Catalog|schema|field|release|frozen|input|identity|package'
                    } finally {
                        [IO.File]::WriteAllBytes($requestPath, $requestBytes); [IO.File]::WriteAllBytes($inputPath, $inputBytes)
                        [IO.File]::WriteAllBytes($catalogPath, $catalogBytes); [IO.File]::WriteAllBytes($request.releaseManifest, $releaseBytes)
                    }
                }
                Check ([UnityEditor.EditorApplication]::ExitCodes.Count -eq 0 -and -not [IO.File]::Exists((Join-Path $request.output 'report.json')) -and
                    [UnityEditor.PackageManager.UI.Sample]::ImportCalls -eq 0) 'A rejected catalog/release acquired report/import/exit authority.'
                $script:catalogGuardsChecked = $true
            }
            try { Call-Entry $argv } catch {
                if ($script:fault -cnotin @('owner', 'interactive', 'cache-environment', 'sample-drift', 'reopen-drift', 'unknown-output', 'unknown-sample', 'unknown-empty-sample', 'unknown-empty-folder', 'wrong-editor', 'manifest-extra', 'input-bytes', 'duplicate-request')) { throw }
                Check ([UnityEditor.EditorApplication]::ExitCodes.Count -eq 0) 'Invalid authority/output acquired permission to exit.'
                return [pscustomobject]@{ status = 'NonZeroExit'; pid = $PID; observedExit = $true; exitCode = 1; timedOut = $false; cancelled = $false; forcedCleanup = $false; observerError = $null; cleanupError = $null; jobAssigned = $true; activeProcesses = 0; streamsDrained = $true }
            }
            $importCalls = [UnityEditor.PackageManager.UI.Sample]::ImportCalls; $resumed = $false
            for ($i = 0; $i -lt 20 -and [UnityEditor.EditorApplication]::ExitCodes.Count -eq 0; $i++) {
                [UnityEditor.EditorApplication]::Pump()
                if (-not $resumed -and $script:fault -cin @('reload-observed', 'reload-unobserved', 'reload-false', 'reload-wrong-api') -and $request.phase -ceq 'Import' -and [UnityEditor.PackageManager.UI.Sample]::ImportCalls -gt $importCalls) {
                    $resumed = $true
                    $pendingPath = Join-Path $request.output 'import.json'; $pending = Read-YLPackageEvidenceJson $pendingPath
                    if ($script:fault -ceq 'reload-unobserved') { $pending.returnObserved = $false; $pending.returnValue = $null }
                    if ($script:fault -ceq 'reload-false') { $pending.returnValue = $false }
                    if ($script:fault -ceq 'reload-wrong-api') { $pending.api = 'CopiedSourceNotSamplesApi' }
                    Save $pendingPath $pending
                    [UnityEditor.EditorApplication]::Reset()
                    try { Call-Entry $argv -ResumeImport } catch {
                        if ($script:fault -cnotin @('reload-false', 'reload-wrong-api')) { throw }
                        Check ([UnityEditor.EditorApplication]::ExitCodes.Count -eq 0) 'Invalid persisted import acquired Editor exit authority.'
                        return [pscustomobject]@{ status = 'NonZeroExit'; pid = $PID; observedExit = $true; exitCode = 1; timedOut = $false; cancelled = $false; forcedCleanup = $false; observerError = $null; cleanupError = $null; jobAssigned = $true; activeProcesses = 0; streamsDrained = $true }
                    }
                }
            }
            Check ([UnityEditor.EditorApplication]::ExitCodes.Count -eq 1) 'The validated operation did not produce exactly one terminal exit.'
            $code = [UnityEditor.EditorApplication]::ExitCodes[0]
            if ($script:fault -ceq 'report-provenance') { $p = Join-Path $request.output 'report.json'; $r = Read-YLPackageEvidenceJson $p; $r.registryUrl = 'https://unapproved.invalid'; Save $p $r }
            return [pscustomobject]@{ status = $(if ($code -eq 0) { 'Exited' } else { 'NonZeroExit' }); pid = $PID; observedExit = $true; exitCode = $code; timedOut = $false; cancelled = $false; forcedCleanup = $false; observerError = $null; cleanupError = $null; jobAssigned = $true; activeProcesses = 0; streamsDrained = $true }
        } finally { foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) } }
    }
    foreach ($entryPackage in @('com.kano.ylframework', 'com.kano.ylframework.hybridclr')) {
        $starts.Clear(); $beforeCalls = [UnityEditor.ContractAssets]::FactoryCalls
        $outcome = Invoke-YLRegistryAuthoring @invoke -EntryPackage $entryPackage
        Check ($outcome.status -ceq 'Completed' -and ($outcome.phases.phase -join ',') -ceq 'Inspect,Import,Author,Reopen') ("Authoring $entryPackage did not complete the required independent launch phases: " + $outcome.error)
        Check (@($starts.host | Sort-Object -Unique).Count -eq 1 -and @($starts.cache | Sort-Object -Unique).Count -eq 1) 'Warm authoring launches changed their owned host/cache.'
        Check ($outcome.coldInstallCount -eq 1 -and $outcome.reopen.meaning -ceq 'SameVersionWarmReopenNotUpgrade') 'Warm reopen was misclassified as another cold install or an upgrade.'
        Check ([UnityEditor.ContractAssets]::FactoryCalls -eq ($beforeCalls + 1)) 'An actual reflected sample factory was skipped or re-executed on warm reopen.'
        Check ($outcome.sampleReceipt.files.Count -gt 0 -and $outcome.managedOutputs.files.Count -gt 4 -and $outcome.managedOutputs.directories.Count -gt 0) 'Actual imported/generated files, metas and directories were not registered.'
        Check ($outcome.managedOutputs.files.path -ccontains ($outcome.assetDirectory + '/Project.asset.meta')) 'The generated project meta was not registered.'
        Check ($outcome.managedOutputs.files.type -ccontains 'YLFramework.Editor.Resource.ResourceProject') 'Generated assets do not carry actual loaded type evidence.'
        Check ($outcome.boundaries.native -ceq 'NotRun' -and $outcome.boundaries.playerHotUpdate -ceq 'NotVerified') 'Asset generation was mislabeled as native or Player validation.'
        Check ($outcome.inputIdentity.sha256 -ceq $outcome.producerInputs.inputHash -and $outcome.sourceCommit -ceq ('a' * 40) -and
            $outcome.sampleReceipt.sourceCommit -ceq ('a' * 40) -and $outcome.managedOutputs.sourceCommit -ceq ('a' * 40)) 'Source/producer identities are missing from actual output evidence.'
        Check ($outcome.inputIdentity.files.path -ccontains 'Tools/Packaging/PackageCatalog.json' -and
            $outcome.producerInputs.files.path -ccontains 'YLTesting/PackageCatalog.json') 'The catalog must participate in both producer and host input identities.'
        $sampleReceiptPath = Join-Path $outcome.host.path '.yl-samples.json'; $managedPath = Join-Path $outcome.host.path '.yl-authoring-outputs.json'
        Check ([IO.File]::Exists($sampleReceiptPath) -and [IO.File]::Exists($managedPath)) 'Host-managed import/output receipts are missing.'
        foreach ($file in $outcome.managedOutputs.files) { Check ((Hash (Join-Path $outcome.host.path $file.path)) -ceq $file.sha256) 'A managed output receipt does not bind the actual bytes.' }
        $managedRequest = Read-YLPackageEvidenceJson (Join-Path $outcome.phases[-1].output 'request.json')
        foreach ($fault in @('missing-schema', 'schema-type', 'schema-version', 'duplicate-file-guid', 'duplicate-directory-guid', 'duplicate-directory')) {
            $bad = Copy-Value $outcome.managedOutputs
            switch ($fault) {
                'missing-schema' { $bad.Remove('schemaVersion') }
                'schema-type' { $bad.schemaVersion = $true }
                'schema-version' { $bad.schemaVersion = 2 }
                'duplicate-file-guid' {
                    $project = @($bad.files | Where-Object path -CEQ ($outcome.assetDirectory + '/Project.asset'))[0]
                    $project.guid = @($bad.files | Where-Object guid -CNE $project.guid)[0].guid
                }
                'duplicate-directory-guid' { $bad.directories[1].guid = $bad.directories[0].guid }
                'duplicate-directory' { $bad.directories += Copy-Value $bad.directories[0] }
            }
            $badPath = Join-Path $outcome.output ('receipt-' + $fault + '.json'); Save $badPath $bad
            Reject { Read-YLRegistryAuthoringReceipt -Path $badPath -HostRecord $outcome.host -Request $managedRequest -Purpose 'YLTesting.RegistryAuthoring.Outputs' } 'identity|GUID|directory'
        }
        $contextType = $entryType.GetNestedType('Context', [Reflection.BindingFlags]'NonPublic')
        $context = [Activator]::CreateInstance($contextType, $true)
        $contextType.GetField('request').SetValue($context, [Newtonsoft.Json.Linq.JObject]::Parse((ConvertTo-Json -InputObject @{ hostRoot = $outcome.host.path })))
        $snapshot = $entryType.GetMethod('Snapshot', [Reflection.BindingFlags]'NonPublic,Static')
        $projectMeta = Join-Path $outcome.host.path ($outcome.assetDirectory + '/Project.asset.meta')
        $otherAsset = if ($entryPackage.EndsWith('.hybridclr')) { 'Recipe.asset.meta' } else { 'Example.unity.meta' }
        foreach ($relative in @(($outcome.assetDirectory + '/' + $otherAsset), ($outcome.assetDirectory + '.meta'))) {
            $target = Join-Path $outcome.host.path $relative; $bytes = [IO.File]::ReadAllBytes($target)
            try {
                [IO.File]::WriteAllBytes($target, [IO.File]::ReadAllBytes($projectMeta))
                Reject { try { $null = $snapshot.Invoke($null, [object[]]@($context, $outcome.assetDirectory, $true)) } catch { throw $_.Exception.GetBaseException() } } 'duplicate.*GUID|GUID.*duplicate'
            } finally { [IO.File]::WriteAllBytes($target, $bytes) }
        }
    }
    $tar = Copy-Value $source; $tar.packageOrigin = 'LocalTarball'
    foreach ($entryPackage in @('com.kano.ylframework', 'com.kano.ylframework.hybridclr')) {
        $artifact = Invoke-YLRegistryAuthoring @invoke -Source $tar -EntryPackage $entryPackage
        Check ($artifact.status -ceq 'Completed' -and $artifact.scopeSource -ceq 'ArtifactPreflight') ("LocalTarball $entryPackage authoring must remain ArtifactPreflight, not Registry proof: " + $artifact.error)
    }
    $script:fault = 'wrong-source-fingerprint'
    foreach ($wrongSource in @($source, $tar)) {
        $failed = Invoke-YLRegistryAuthoring @invoke -Source $wrongSource
        Check ($failed.status -ceq 'Failed' -and $failed.phases.Count -eq 1) 'Authoring accepted metadata from the other package source format.'
    }
    $script:fault = ''
    foreach ($reloadCase in @('reload-observed', 'reload-unobserved')) {
        $script:fault = $reloadCase; $beforeImports = [UnityEditor.PackageManager.UI.Sample]::ImportCalls
        $reloaded = Invoke-YLRegistryAuthoring @invoke
        Check ($reloaded.status -ceq 'Completed') ("Import domain reload did not preserve actual completion: " + $reloaded.error)
        Check ([UnityEditor.PackageManager.UI.Sample]::ImportCalls -eq ($beforeImports + 1)) 'Domain reload reissued an import rather than observing the first API operation.'
        $import = $reloaded.phases[1].report.sample
        Check ($import.returnObserved -eq ($reloadCase -ceq 'reload-observed')) 'An unobserved import return was fabricated as an observed success.'
    }
    foreach ($faultName in @('compile', 'compiled-before-import', 'import-false', 'missing-sample-assembly', 'wrong-sample-source', 'factory-empty', 'wrong-type', 'factory-cache-write', 'unknown-sample', 'unknown-empty-sample', 'unknown-empty-folder', 'unknown-output', 'sample-drift', 'package-drift', 'reopen-drift', 'cache-environment', 'owner', 'interactive', 'wrong-editor', 'manifest-extra', 'input-bytes', 'duplicate-request', 'reload-false', 'reload-wrong-api', 'rewrite-producer-receipt', 'rewrite-output-receipt', 'report-provenance')) {
        $script:fault = $faultName
        $beforeImports = [UnityEditor.PackageManager.UI.Sample]::ImportCalls
        $failed = Invoke-YLRegistryAuthoring @invoke
        Check ($failed.status -ceq 'Failed' -and $failed.error.Length -gt 0) "Authoring incorrectly accepted $faultName."
        Check ($failed.phases.Count -gt 0 -and $failed.phases[-1].status -ceq 'Failed' -and $failed.phases[-1].error.Length -gt 0 -and
            $failed.phases[-1].reportPath.EndsWith('report.json')) 'An attempted failed authoring phase lost its process/report/error facts.'
        if ($faultName -cin @('unknown-empty-sample', 'unknown-empty-folder')) { Check ([UnityEditor.PackageManager.UI.Sample]::ImportCalls -eq $beforeImports) 'Unknown empty user directories were adopted before the Samples API call.' }
        if ($faultName -ceq 'factory-cache-write') { Check (@($failed.phases | Where-Object { $_.phase -ceq 'Author' -and $_.status -ceq 'Completed' }).Count -eq 0) 'Author phase claimed success before checking that the real factory left installation bytes unchanged.' }
        if ($faultName -ceq 'unknown-output') { Check ([IO.File]::ReadAllText((Join-Path $failed.host.path 'Assets/YLFrameworkData/user.txt')) -ceq 'must survive') 'Unknown output was overwritten/deleted.' }
    }
    $script:fault = 'registry-unavailable'; $starts.Clear()
    $blocked = Invoke-YLRegistryAuthoring @invoke
    Check ($blocked.status -ceq 'PrerequisiteNotMet' -and $blocked.phases.Count -eq 0 -and $starts.Count -eq 0 -and $blocked.coldInstallCount -eq 0) 'Unavailable Registry was converted to an install pass, fallback or Unity launch.'
    "PASS: $checks Registry authoring NoUnity behavior contracts. NOT actual Samples API import, Unity compilation, Registry installation or Player/native validation. Fixtures: $root"
} finally {
    # LoadFrom pins fixture DLLs for this process. Retain the isolated fixture/evidence rather than mask a test failure during cleanup.
    if ('UnityEditor.EditorApplication' -as [type]) { [UnityEditor.EditorApplication]::Reset() }
}
