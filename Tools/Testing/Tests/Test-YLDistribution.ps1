#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $WorkspaceRoot,
    [Parameter(Mandatory)][string] $Unity,
    [ValidateRange(1, 86400)][int] $TimeoutSeconds = 900,
    [switch] $RunPlayer,
    [ValidateRange(1, 600)][int] $PlayerTimeoutSeconds = 60,
    [ValidateSet('Mono', 'IL2CPP')][string] $Backend = 'Mono',
    [switch] $HybridCLR,
    [string] $NativeSource,
    [string] $NativeSourceManifest,
    [ValidateSet('File', 'Git')][string] $PackageSource = 'Git',
    [ValidateNotNullOrEmpty()][string] $GitRepository,
    [ValidateNotNullOrEmpty()][string] $GitRevision
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Common/Process.ps1')
. (Join-Path $PSScriptRoot '../Common/Results.ps1')
. (Join-Path $PSScriptRoot '../UnityHost.ps1')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$configuration = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../TestSuites.json')) | ConvertFrom-Json -DateKind String
$hasRepository = $PSBoundParameters.ContainsKey('GitRepository')
$hasRevision = $PSBoundParameters.ContainsKey('GitRevision')
if ($PackageSource -ceq 'File' -and ($hasRepository -or $hasRevision)) { throw 'File package source refuses Git parameters.' }
if ($hasRepository -ne $hasRevision) { throw 'GitRepository and GitRevision must be provided together.' }
if ($hasRepository) {
    $uri = $null
    $url = $GitRepository -creplace '^git\+', ''
    if (-not [Uri]::TryCreate($url, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -cnotin @('https', 'ssh', 'git') -or
        [string]::IsNullOrWhiteSpace($uri.Host) -or $GitRepository -match '[\s?#]' -or $uri.Query -or $uri.Fragment -or
        ($uri.Scheme -ceq 'https' -and $uri.UserInfo) -or $GitRevision -cnotmatch '^[0-9a-fA-F]{40}$') {
        throw 'Git requires a remote repository URL without path/revision query or fragment and a full 40-digit commit SHA.'
    }
    $GitRevision = $GitRevision.ToLowerInvariant()
}
$packageOrigin = if ($PackageSource -ceq 'File') { 'File' } elseif ($hasRepository) { 'GitRemote' } else { 'GitFixture' }
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($Unity)
$editor = Get-YLTestEditorProfile -Editors $configuration.editors -VersionInfo $version
$packageInputs = @(Get-YLUnityPackageInputs -EditorProfile $editor)
foreach ($relative in $packageInputs) { $null = [IO.File]::ReadAllBytes((Join-Path $repository $relative)) }
if ($HybridCLR -and ($Backend -cne 'IL2CPP' -or -not $RunPlayer -or -not $NativeSource)) {
    throw 'HybridCLR acceptance requires explicit IL2CPP, RunPlayer and an existing native source.'
}

function Get-DistributionPackageContent([string] $Root, [switch] $AllowManagedFingerprint) {
    function Convert-CanonicalToken([Newtonsoft.Json.Linq.JToken] $Token) {
        if ($Token -is [Newtonsoft.Json.Linq.JObject]) {
            $result = [Newtonsoft.Json.Linq.JObject]::new()
            [string[]]$names = @($Token.Properties() | ForEach-Object Name)
            [Array]::Sort($names, [StringComparer]::Ordinal)
            foreach ($name in $names) { $result.Add($name, (Convert-CanonicalToken $Token[$name])) }
            return ,$result
        }
        if ($Token -is [Newtonsoft.Json.Linq.JArray]) {
            $result = [Newtonsoft.Json.Linq.JArray]::new()
            foreach ($item in $Token) { $result.Add((Convert-CanonicalToken $item)) }
            return ,$result
        }
        return ,$Token.DeepClone()
    }
    $Root = Get-YLTestPath $Root
    if (-not [IO.File]::Exists((Join-Path $Root 'LICENSE.md')) -or [IO.FileInfo]::new((Join-Path $Root 'LICENSE.md')).Length -eq 0) { throw 'Package LICENSE.md is required.' }
    $files = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($file in @(Get-YLTestTreeFiles $Root)) {
        $relative = [IO.Path]::GetRelativePath($Root, $file).Replace('\', '/')
        if ($relative -match '^(?:Assets|Packages|ProjectSettings|Library|Temp|obj|bin|Tests|Tools|Settings|\.git|\.kilo|\.kilocode|\.opencode)(?:/|$)' -or
            $relative -match '(?:^|/)(?:AGENTS\.md|\.yl-[^/]+|[^/]+\.(?:csproj|sln|slnx))$') { throw "Unauthorized package content: $relative" }
        $stream = [IO.File]::OpenRead($file)
        try {
            $prefix = [byte[]]::new(200)
            $count = $stream.Read($prefix, 0, $prefix.Length)
            if ([Text.Encoding]::UTF8.GetString($prefix, 0, $count).StartsWith('version https://git-lfs.github.com/spec/v1', [StringComparison]::Ordinal)) { throw "Unmaterialized Git LFS package content: $relative" }
            if ($relative.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase) -and ($count -lt 2 -or $prefix[0] -ne 77 -or $prefix[1] -ne 90)) { throw "Package DLL is not a materialized PE binary: $relative" }
            $stream.Position = 0
            $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
        } finally { $stream.Dispose() }
        if ($relative -ceq 'package.json') {
            $reader = [Newtonsoft.Json.JsonTextReader]::new([IO.StringReader]::new([IO.File]::ReadAllText($file)))
            $reader.DateParseHandling = [Newtonsoft.Json.DateParseHandling]::None
            $loadSettings = [Newtonsoft.Json.Linq.JsonLoadSettings]::new()
            $loadSettings.DuplicatePropertyNameHandling = [Newtonsoft.Json.Linq.DuplicatePropertyNameHandling]::Error
            try {
                $manifest = [Newtonsoft.Json.Linq.JObject]::Load($reader, $loadSettings)
                if ($reader.Read()) { throw 'Unexpected trailing package manifest content.' }
            } finally { $reader.Dispose() }
            $fingerprint = $manifest.Property('_fingerprint')
            if ($null -ne $fingerprint) {
                if (-not $AllowManagedFingerprint -or $fingerprint.Value.Type -ne [Newtonsoft.Json.Linq.JTokenType]::String -or [string]$fingerprint.Value -cnotmatch '^[0-9a-f]{40}$') { throw 'Untrusted package-manager fingerprint metadata.' }
                $fingerprint.Remove()
            }
            $canonical = (Convert-CanonicalToken $manifest).ToString([Newtonsoft.Json.Formatting]::None)
            $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical))).ToLowerInvariant()
        }
        $files.Add($relative, @{ path = $relative; sha256 = $hash })
    }
    if (-not $files.ContainsKey('package.json')) { throw 'The package manifest is missing.' }
    $text = [Text.StringBuilder]::new()
    foreach ($entry in $files.GetEnumerator()) { $null = $text.Append($entry.Key).Append([char]0).Append($entry.Value.sha256).Append("`n") }
    return [pscustomobject]@{ sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text.ToString()))).ToLowerInvariant(); files = @($files.Values) }
}

function Get-DistributionSdk([string] $Repository) {
    $manifest = [IO.File]::ReadAllText((Join-Path $Repository 'Packages/manifest.json')) | ConvertFrom-Json -AsHashtable -NoEnumerate -DateKind String
    $lock = [IO.File]::ReadAllText((Join-Path $Repository 'Packages/packages-lock.json')) | ConvertFrom-Json -AsHashtable -NoEnumerate -DateKind String
    $id = 'com.code-philosophy.hybridclr'
    if ($manifest -isnot [Collections.IDictionary] -or $manifest['dependencies'] -isnot [Collections.IDictionary] -or
        $manifest['dependencies'][$id] -isnot [string] -or $manifest['dependencies'][$id] -cnotmatch '^https://[^\s?#]+\.git#v9\.0\.3$' -or
        $lock -isnot [Collections.IDictionary] -or $lock['dependencies'] -isnot [Collections.IDictionary] -or $lock['dependencies'][$id] -isnot [Collections.IDictionary]) { throw 'HybridCLR SDK requires an explicit v9.0.3 Git declaration and lock.' }
    $entry = $lock['dependencies'][$id]
    if ($entry['version'] -isnot [string] -or $entry['version'] -cne $manifest['dependencies'][$id] -or
        $entry['source'] -isnot [string] -or $entry['source'] -cne 'git' -or $entry['hash'] -isnot [string] -or $entry['hash'] -cnotmatch '^[0-9a-f]{40}$') { throw 'HybridCLR SDK lock must match the exact declaration and commit.' }
    return @{ declaration = $manifest['dependencies'][$id]; revision = $entry['hash'] }
}

function Get-DistributionHostPackages([string] $Repository, [object] $EditorProfile, [bool] $HybridCLR) {
    $inputs = @(Get-YLUnityPackageInputs -EditorProfile $EditorProfile)
    $dependencies = [ordered]@{ 'com.unity.ugui' = $EditorProfile.ugui }
    $sourceManifest = [IO.File]::ReadAllText((Join-Path $Repository $inputs[0])) | ConvertFrom-Json -DateKind String
    foreach ($entry in $sourceManifest.dependencies.PSObject.Properties | Where-Object { $_.Name.StartsWith('com.unity.modules.', [StringComparison]::Ordinal) }) { $dependencies[$entry.Name] = $entry.Value }
    $sourceLock = [IO.File]::ReadAllText((Join-Path $Repository $inputs[1])) | ConvertFrom-Json -DateKind String
    $dependencies['com.unity.nuget.newtonsoft-json'] = $sourceLock.dependencies.'com.unity.nuget.newtonsoft-json'.version
    $sdk = $null
    if ($HybridCLR) {
        $sdk = Get-DistributionSdk -Repository $Repository
        $dependencies['com.code-philosophy.hybridclr'] = $sdk.declaration
        $inputs = @($inputs + @('Packages/manifest.json', 'Packages/packages-lock.json') | Sort-Object -Unique)
    }
    return @{ inputs = $inputs; dependencies = $dependencies; sdk = $sdk }
}

$workspace = Initialize-YLTestWorkspace -WorkspaceRoot $WorkspaceRoot -Repository $repository
$key = "Tooling-Distribution-$($version.ProductVersion)-$packageOrigin-$Backend"
if ($HybridCLR) { $key += '-HybridCLR' }
if ($RunPlayer) { $key += '-Smoke' }
$run = New-YLTestDirectory -Workspace $workspace -Kind Result -Key $key
$lease = Lock-YLTestDirectory -Workspace $workspace -Path $run.path
# The shared retention envelope is v1; distribution requests/reports and package evidence are v2.
$summary = [ordered]@{ schemaVersion = 1; distributionSchemaVersion = 2; ownerId = $run.id; action = 'Run'; suite = 'Tooling'; module = 'Distribution'; profile = $version.ProductVersion
    status = 'Failed'; reason = $null; endUtc = $null; processes = @(); checks = 0; player = 'NotRun'; playerRun = @{ status = 'NotRun' }; playerRuns = @(); backend = $Backend
    unity = @{ executable = $Unity; version = $editor.version; productVersion = $version.ProductVersion }; packageOrigin = $packageOrigin; packages = @(); resolvedDependencies = @() }
$hostLeases = [Collections.Generic.List[IDisposable]]::new()
$nativeVariant = $null; $baselineRoot = $null; $runtimeCache = $null; $cacheOwnerId = $null; $nativePlayerRoot = $null; $playerOwnerId = $null
$packageEvidence = @{}
$sdk = $null

function Invoke-DistributionProcess([object] $HostRecord, [string] $Phase) {
    $owner = Get-YLTestDirectory -Workspace $workspace -Path $HostRecord.path
    if ($owner.id -cne $HostRecord.id) { throw 'Distribution host owner changed.' }
    Assert-YLUnityHostIdle $HostRecord
    $output = Join-Path $run.path $Phase
    $null = [IO.Directory]::CreateDirectory($output)
    $request = Join-Path $output 'request.json'
    Write-YLTestJson -Path $request -Value @{ schemaVersion = 2; hostId = $HostRecord.id; resultId = $run.id; resultRoot = $run.path; unityVersion = $editor.version; phase = $Phase; output = $output
        packageOrigin = $packageOrigin; packages = @($expectedPackages); gitRepository = $GitRepository; gitRevision = $GitRevision; manifestSha256 = $manifestHash
        smoke = [bool]$RunPlayer; backend = $Backend; hybridCLR = [bool]$HybridCLR; nativeSource = $NativeSource; nativeVariant = $nativeVariant
        baseline = $baselineRoot; runtimeCache = $runtimeCache; cacheOwnerId = $cacheOwnerId; playerRoot = $nativePlayerRoot; playerOwnerId = $playerOwnerId }
    $start = [Diagnostics.ProcessStartInfo]::new($Unity)
    $start.UseShellExecute = $false; $start.WorkingDirectory = $HostRecord.path
    foreach ($key in @($start.Environment.Keys | Where-Object { $_.StartsWith('GIT_', [StringComparison]::OrdinalIgnoreCase) })) { $null = $start.Environment.Remove($key) }
    $start.Environment['UPM_CACHE_ROOT'] = Join-Path $cache.path 'Upm'
    $start.Environment['UPM_NPM_CACHE_PATH'] = Join-Path $cache.path 'Upm/npm'
    $start.Environment['UPM_CACHE_PATH'] = Join-Path $cache.path 'Upm/packages'
    $start.Environment['UPM_GIT_LFS_CACHE_PATH'] = Join-Path $cache.path 'Lfs'
    $start.Environment['UPM_ENABLE_GIT_LFS_CACHE'] = '1'
    $start.Environment.Remove('GIT_LFS_SKIP_SMUDGE') | Out-Null
    $arguments = @('-batchmode', '-projectPath', $HostRecord.path, '-logFile', (Join-Path $output 'unity.log'))
    if (-not $Phase.StartsWith('Hybrid', [StringComparison]::Ordinal)) { $arguments += '-nographics' }
    $arguments += @('-executeMethod', 'YLFramework.Testing.DistributionEntry.Execute', '-ylDistributionRequest', $request)
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $fact = Invoke-YLTestProcess -StartInfo $start -Log (Join-Path $output 'process.log') -TimeoutMilliseconds ([long]$TimeoutSeconds * 1000)
    $summary.processes += $fact
    $prerequisite = $Phase -ceq 'Build' -and $fact.observedExit -and $fact.exitCode -eq 2 -and -not $fact.forcedCleanup -and $fact.activeProcesses -eq 0
    if ($prerequisite) {
        foreach ($field in @('observedExit', 'jobAssigned', 'streamsDrained')) { if ($fact.$field -isnot [bool] -or -not $fact.$field) { throw "Incomplete prerequisite process observation: $field" } }
        foreach ($field in @('timedOut', 'cancelled', 'forcedCleanup')) { if ($fact.$field -isnot [bool] -or $fact.$field) { throw "Nonordinary prerequisite process outcome: $field" } }
        if ($fact.status -isnot [string] -or $fact.status -cne 'NonZeroExit' -or ($fact.exitCode -isnot [int] -and $fact.exitCode -isnot [long]) -or
            ($fact.activeProcesses -isnot [int] -and $fact.activeProcesses -isnot [uint32] -and $fact.activeProcesses -isnot [long]) -or
            -not [string]::IsNullOrEmpty($fact.observerError) -or -not [string]::IsNullOrEmpty($fact.cleanupError)) { throw 'The prerequisite process has no ordinary exit/cleanup outcome.' }
    } else { Assert-YLTestProcessSucceeded $fact }
    $owner = Get-YLTestDirectory -Workspace $workspace -Path $HostRecord.path
    if ($owner.id -cne $HostRecord.id) { throw 'Distribution host owner changed.' }
    $report = [IO.File]::ReadAllText((Join-Path $output 'report.json')) | ConvertFrom-Json -AsHashtable -DateKind String
    foreach ($field in @('hostId', 'resultId', 'unityVersion', 'phase', 'packageOrigin')) { if ($report[$field] -isnot [string]) { throw 'Distribution evidence identity fields must be strings.' } }
    if ($report['schemaVersion'] -isnot [long] -or $report['schemaVersion'] -ne 2 -or $report['pid'] -isnot [long] -or $report['pid'] -ne $fact.pid -or
        $report['hostId'] -cne $HostRecord.id -or $report['resultId'] -cne $run.id -or $report['unityVersion'] -cne $editor.version -or
        $report['phase'] -cne $Phase -or $report['packageOrigin'] -cne $packageOrigin -or $report['result'] -isnot [Collections.IDictionary] -or
        $report['result']['status'] -isnot [string] -or $report['result']['status'] -cne $(if ($prerequisite) { 'PrerequisiteNotMet' } elseif ($Phase -ceq 'HybridConfigure') { 'Completed' } else { 'Passed' })) { throw 'Distribution evidence identity/status does not match.' }
    if ($report['packages'] -isnot [array] -or $report['packages'].Count -ne $expectedPackages.Count -or $report['resolvedDependencies'] -isnot [array]) { throw 'Distribution package graph is incomplete.' }
    $lock = [IO.File]::ReadAllText((Join-Path $HostRecord.path 'Packages/packages-lock.json')) | ConvertFrom-Json -AsHashtable -DateKind String
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($row in $report['packages']) {
        foreach ($field in @('name', 'version', 'source', 'assetPath', 'resolvedPath', 'contentSha256', 'packageId')) { if ($row[$field] -isnot [string]) { throw 'Distribution package fields must be strings.' } }
        $expected = @($expectedPackages | Where-Object { $_.name -ceq $row['name'] })
        if ($expected.Count -ne 1 -or -not $seen.Add($row['name']) -or $row['version'] -cne $expected[0].version -or
            $row['assetPath'] -cne ('Packages/' + $row['name']) -or $row['source'] -cne $(if ($PackageSource -ceq 'File') { 'Local' } else { 'Git' })) { throw 'Distribution package identity/source does not match.' }
        $resolved = Get-YLTestPath $row['resolvedPath']
        $idPrefix = $row['name'] + '@'
        if (-not $row['packageId'].StartsWith($idPrefix, [StringComparison]::Ordinal)) { throw 'Distribution API packageId has a mismatched package name.' }
        $apiLocator = $row['packageId'].Substring($idPrefix.Length).Replace('\', '/')
        $expectedLocator = $dependencies[$row['name']].Replace('\', '/')
        if ($PackageSource -ceq 'Git') {
            if ($apiLocator.StartsWith('git+', [StringComparison]::Ordinal)) { $apiLocator = $apiLocator.Substring(4) }
            if ($expectedLocator.StartsWith('git+', [StringComparison]::Ordinal)) { $expectedLocator = $expectedLocator.Substring(4) }
        }
        $comparison = if ($PackageSource -ceq 'File' -and [IO.Path]::DirectorySeparatorChar -eq '\') { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
        if (-not [string]::Equals($apiLocator, $expectedLocator, $comparison)) { throw 'Distribution API packageId contradicts its requested source.' }
        if ($PackageSource -ceq 'File') {
            if ($resolved -ine $expected[0].root) { throw 'Distribution File package resolved outside its owned staging.' }
        } elseif (-not $resolved.StartsWith($HostRecord.path + '\', [StringComparison]::OrdinalIgnoreCase) -and -not $resolved.StartsWith($cache.path + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Distribution Git package resolved outside its cold owned host/cache.' }
        $actual = Get-DistributionPackageContent -Root $resolved -AllowManagedFingerprint:($row['source'] -ceq 'Git')
        $manifest = [IO.File]::ReadAllText((Join-Path $resolved 'package.json')) | ConvertFrom-Json -AsHashtable -DateKind String
        if ($manifest['name'] -isnot [string] -or $manifest['version'] -isnot [string] -or $manifest['name'] -cne $row['name'] -or $manifest['version'] -cne $row['version'] -or
            $row['contentSha256'] -cne $actual.sha256 -or ($expected[0].contentSha256 -and $expected[0].contentSha256 -cne $actual.sha256)) { throw 'Distribution package content identity does not match.' }
        foreach ($dependency in $manifest['dependencies'].GetEnumerator()) {
            if ($dependency.Key.StartsWith('com.kano.', [StringComparison]::Ordinal) -and
                ($dependency.Value -isnot [string] -or @($expectedPackages | Where-Object { $_.name -ceq $dependency.Key -and $_.version -ceq $dependency.Value }).Count -ne 1)) { throw 'Distribution package dependency closure is missing or mismatched.' }
        }
        $locked = $lock['dependencies'][$row['name']]
        if ($locked -isnot [Collections.IDictionary] -or $locked['source'] -isnot [string] -or $locked['version'] -isnot [string] -or
            $locked['source'] -cne $(if ($PackageSource -ceq 'File') { 'local' } else { 'git' }) -or $locked['version'] -cne $dependencies[$row['name']]) { throw 'Distribution package lock source/locator does not match.' }
        if ($PackageSource -ceq 'Git' -and ($row['revision'] -isnot [string] -or $row['revision'] -cne $GitRevision -or $locked['hash'] -isnot [string] -or $locked['hash'] -cne $GitRevision)) { throw 'Distribution Git package revision does not match.' }
        if ($packageEvidence.ContainsKey($row['name']) -and $packageEvidence[$row['name']] -cne $actual.sha256) { throw 'Distribution installed package inputs changed.' }
        $packageEvidence[$row['name']] = $actual.sha256
    }
    $resolvedById = @{}
    foreach ($dependency in $report['resolvedDependencies']) {
        foreach ($field in @('name', 'version', 'source')) { if ($dependency[$field] -isnot [string]) { throw 'Distribution resolved dependency fields must be strings.' } }
        if ($resolvedById.ContainsKey($dependency['name']) -or $dependency['source'] -cnotin @('Local', 'Git', 'Registry', 'BuiltIn') -or
            ($dependency['name'] -cnotin $dependencies.Keys -and -not $dependency['name'].StartsWith('com.unity.', [StringComparison]::Ordinal))) { throw 'Distribution resolved dependency identity/source is invalid.' }
        $resolvedById[$dependency['name']] = $dependency
    }
    foreach ($entry in $dependencies.GetEnumerator()) {
        if (-not $resolvedById.ContainsKey($entry.Key)) { throw "Distribution resolved dependency is missing: $($entry.Key)" }
        $actual = $resolvedById[$entry.Key]
        $resolvedLock = $lock['dependencies'][$entry.Key]
        if ($resolvedLock -isnot [Collections.IDictionary] -or $resolvedLock['source'] -isnot [string] -or
            $actual['source'].ToLowerInvariant() -cne $resolvedLock['source']) { throw 'Distribution resolved dependency source does not match the actual lock.' }
        $expectedVersion = if ($entry.Key.StartsWith('com.kano.', [StringComparison]::Ordinal)) { '0.1.0-preview.1' } elseif ($entry.Key -ceq 'com.code-philosophy.hybridclr') { '9.0.3' } else { $entry.Value }
        if ($actual['version'] -cne $expectedVersion) { throw 'Distribution resolved dependency version does not match.' }
        if ($entry.Key -ceq 'com.code-philosophy.hybridclr' -and ($actual['source'] -cne 'Git' -or $lock['dependencies'][$entry.Key]['hash'] -cne $sdk.revision)) { throw 'HybridCLR SDK actual Git revision does not match.' }
    }
    if ((Get-FileHash -LiteralPath (Join-Path $HostRecord.path 'Packages/manifest.json')).Hash -cne $manifestHash) { throw 'Distribution host package manifest inputs changed.' }
    $summary.packages = @($report['packages']); $summary.resolvedDependencies = @($report['resolvedDependencies'])
    return [pscustomobject]@{ result = [pscustomobject]$report['result'] }
}

try {
    $toolchain = [Collections.Generic.List[string]]::new(); $toolchain.Add($Unity)
    $hostPackages = Get-DistributionHostPackages -Repository $repository -EditorProfile $editor -HybridCLR ([bool]$HybridCLR)
    $sdk = $hostPackages.sdk
    if ($HybridCLR) {
        $NativeSource = Get-YLTestPath $NativeSource
        $nativeFiles = @(Get-YLTestTreeFiles $NativeSource)
        if ($editor.version -ceq '2022.3.62f3') {
            $il2cpp = Join-Path $repository 'Library/HybridCLR/il2cpp_plus_repo'; $hybrid = Join-Path $repository 'Library/HybridCLR/hybridclr_repo'
            if ($NativeSource -ine (Join-Path $il2cpp 'libil2cpp')) { throw 'The 2022 source must be the existing pinned official native source.' }
            foreach ($item in @(@($il2cpp, 'ba3a76c9979d14cee404fb5d44301773e0768866'), @($hybrid, '83929787581bce6c7219418f62348a10c33582ee'))) {
                $head = & git -C $item[0] rev-parse HEAD
                if ($LASTEXITCODE -ne 0 -or $head -cne $item[1]) { throw 'Existing native Git provenance differs from the pinned 2022 inputs.' }
            }
            & git -C $il2cpp diff --exit-code HEAD -- libil2cpp
            if ($LASTEXITCODE -ne 0) { throw 'The pinned IL2CPP source is modified.' }
            & git "--git-dir=$hybrid/.git" "--work-tree=$NativeSource" diff --exit-code HEAD -- hybridclr
            if ($LASTEXITCODE -ne 0) { throw 'The pinned HybridCLR source is modified.' }
            $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($path in @(& git -C $il2cpp ls-tree -r --name-only HEAD -- libil2cpp)) { $null = $expected.Add($path.Substring('libil2cpp/'.Length)) }
            if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate the exact IL2CPP source tree.' }
            foreach ($path in @(& git -C $hybrid ls-tree -r --name-only HEAD -- hybridclr)) { $null = $expected.Add($path) }
            if ($LASTEXITCODE -ne 0 -or -not $expected.SetEquals([string[]]@($nativeFiles | ForEach-Object { [IO.Path]::GetRelativePath($NativeSource, $_).Replace('\', '/') }))) { throw 'Unknown or missing files exist in the official native source tree.' }
            $licenses = @((Join-Path $il2cpp 'LICENSE'), (Join-Path $hybrid 'LICENSE')); $nativeVariant = 'Official2022'
        } elseif ($editor.version -ceq '6000.3.27f1') {
            if (-not $NativeSourceManifest) { throw 'The explicit maintained 6.3 source manifest is required.' }
            $NativeSourceManifest = Get-YLTestPath $NativeSourceManifest
            $verified = & (Join-Path $repository 'Tools/HybridCLR/Prepare-HybridCLRNativeSource.ps1') -Operation Verify -SourceManifest $NativeSourceManifest
            $manifest = [IO.File]::ReadAllText($NativeSourceManifest) | ConvertFrom-Json -DateKind String
            if (-not $verified.Verified -or $manifest.Variant -cne 'ParameterOwner' -or $manifest.SourceDirectory -ine $NativeSource) { throw 'The exact verified 6.3 maintained source is required.' }
            $nativeVariant = $manifest.Variant; $licenses = @($manifest.LicenseFiles.Path)
            $toolchain.Add($NativeSourceManifest); $toolchain.Add((Join-Path $repository 'Tools/HybridCLR/Prepare-HybridCLRNativeSource.ps1')); $toolchain.Add((Join-Path $repository 'Tools/HybridCLR/GlobalMetadata-ParameterOwner.patch'))
        } else { throw 'No native source profile is implemented for this Editor.' }
        foreach ($path in @($nativeFiles) + @($licenses)) { $toolchain.Add($path) }
        $nativeInput = Join-Path $run.path 'NativeInput'; $null = [IO.Directory]::CreateDirectory($nativeInput)
        $summary['nativeSource'] = @{ path = $NativeSource; variant = $nativeVariant; files = $nativeFiles.Count; licenses = @() }
        for ($i = 0; $i -lt $licenses.Count; $i++) {
            $target = Join-Path $nativeInput ("MIT-$i.txt"); [IO.File]::Copy($licenses[$i], $target, $false)
            $summary.nativeSource.licenses += @{ path = $target; sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash }
        }
        foreach ($kind in @('Baseline', 'Runtime', 'Player')) {
            $cacheKey = "H$($kind.Substring(0, 1))-$($run.id.Substring(0, 8))"
            if ([IO.Path]::Exists((Join-Path $workspace.root "Cache/$cacheKey"))) { throw 'Do not adopt an existing native acceptance cache.' }
            $nativeCache = New-YLTestDirectory -Workspace $workspace -Kind Cache -Key $cacheKey
            $hostLeases.Add((Lock-YLTestDirectory -Workspace $workspace -Path $nativeCache.path))
            if ($kind -ceq 'Baseline') { $baselineRoot = Join-Path $nativeCache.path 'B' }
            elseif ($kind -ceq 'Runtime') { $runtimeCache = Join-Path $nativeCache.path 'C'; $cacheOwnerId = $nativeCache.id }
            else { $nativePlayerRoot = Join-Path $nativeCache.path 'P'; $playerOwnerId = $nativeCache.id }
        }
    }
    $ids = @('com.kano.ylframework'); if ($HybridCLR) { $ids += 'com.kano.ylframework.hybridclr' }
    $products = @(); $inventories = @{}
    if ($packageOrigin -cne 'GitRemote') {
        $products = @(Get-YLUpmPackages -Repository $repository -Ids $ids)
        foreach ($package in $products) {
            if ($package.version -cne '0.1.0-preview.1') { throw 'Distribution requires the exact preview package version.' }
            $inventories[$package.id] = Get-DistributionPackageContent -Root $package.root
        }
    }
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($relative in @('Tools/Testing/DistributionEntry.cs', 'Tools/Testing/Tests/Test-YLDistribution.ps1', 'Tools/Testing/Common/Workspace.ps1',
        'Tools/Testing/Common/Process.ps1', 'Tools/Testing/Common/Results.ps1', 'Tools/Testing/Common/Packages.ps1', 'Tools/Testing/UnityHost.ps1', 'Tools/Testing/TestSuites.json') + $hostPackages.inputs) { $null = $paths.Add($relative) }
    foreach ($package in $products) { foreach ($file in $inventories[$package.id].files) { $null = $paths.Add($package.relative + '/' + $file.path) } }
    if ($RunPlayer) { $null = $paths.Add($(if ($HybridCLR) { 'Tools/Testing/HybridCLRSmoke.cs' } else { 'Tools/Testing/PlayerSmoke.cs' })) }
    if ($HybridCLR) { foreach ($path in @('Tools/Testing/HybridCLREntry.cs', 'Packages/manifest.json', 'Packages/packages-lock.json')) { $null = $paths.Add($path) } }
    $cache = New-YLTestDirectory -Workspace $workspace -Kind Cache -Key ("D-$($run.id)")
    $hostLeases.Add((Lock-YLTestDirectory -Workspace $workspace -Path $cache.path))
    $stage = Join-Path $cache.path 'Source.git'
    $null = [IO.Directory]::CreateDirectory($stage)
    foreach ($package in $products) {
        foreach ($file in $inventories[$package.id].files) {
            $destination = Join-Path $stage ($package.relative + '/' + $file.path)
            $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
            [IO.File]::Copy((Join-Path $package.root $file.path), $destination, $false)
        }
    }
    if ($packageOrigin -ceq 'GitFixture') {
        $git = (Get-Command git -CommandType Application -ErrorAction Stop).Source
        $commands = @(@('init', '--quiet', $stage), @('-C', $stage, 'rev-parse', '--show-toplevel', '--absolute-git-dir', '--path-format=absolute', '--git-path', 'index'),
            @('-C', $stage, '-c', 'core.autocrlf=false', 'add', '--', 'PackageSources'),
            @('-C', $stage, '-c', 'user.name=YL Distribution Fixture', '-c', 'user.email=yl-fixture@invalid.local', '-c', 'commit.gpgsign=false', 'commit', '--quiet', '-m', 'Owned UPM fixture'), @('-C', $stage, 'rev-parse', 'HEAD'))
        for ($i = 0; $i -lt $commands.Count; $i++) {
            $start = [Diagnostics.ProcessStartInfo]::new($git); $start.UseShellExecute = $false; $start.WorkingDirectory = $cache.path
            foreach ($key in @($start.Environment.Keys | Where-Object { $_.StartsWith('GIT_', [StringComparison]::OrdinalIgnoreCase) })) { $null = $start.Environment.Remove($key) }
            $start.Environment['GIT_CONFIG_NOSYSTEM'] = '1'
            $start.Environment['GIT_CONFIG_GLOBAL'] = 'NUL'
            foreach ($argument in $commands[$i]) { $start.ArgumentList.Add($argument) }
            $log = Join-Path $run.path "git-$i.log"
            $fact = Invoke-YLTestProcess -StartInfo $start -Log $log -TimeoutMilliseconds ([long]$TimeoutSeconds * 1000)
            $summary.processes += $fact; Assert-YLTestProcessSucceeded $fact
            if ($i -eq 1) {
                $observed = @([IO.File]::ReadAllLines($log) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
                $expected = @($stage, (Join-Path $stage '.git'), (Join-Path $stage '.git/index'))
                if ($observed.Count -ne $expected.Count) { throw 'The owned Git fixture did not expose its worktree, git directory and index.' }
                for ($part = 0; $part -lt $expected.Count; $part++) {
                    if (-not [string]::Equals((Get-YLTestPath $observed[$part]), (Get-YLTestPath $expected[$part]), [StringComparison]::OrdinalIgnoreCase)) { throw 'Git fixture paths escaped the owned cache before add/commit.' }
                }
            }
        }
        $GitRevision = [IO.File]::ReadAllText($log).Trim()
        if ($GitRevision -cnotmatch '^[0-9a-f]{40}$') { throw 'The owned Git fixture did not produce a complete revision.' }
        $GitRepository = 'git+' + ([Uri]$stage).AbsoluteUri
    }
    $profile = "Distribution-v2-$($version.ProductVersion)-$Backend-Hybrid$HybridCLR-$packageOrigin-$GitRepository-$GitRevision"
    $identity = Get-YLTestInputIdentity -Repository $repository -Paths @($paths) -Toolchain $toolchain.ToArray() -Profile $profile
    $summary['inputIdentity'] = $identity
    $dependencies = $hostPackages.dependencies
    $expectedPackages = @(foreach ($id in $ids) {
        $root = Join-Path $stage "PackageSources/$id"
        $dependencies[$id] = if ($PackageSource -ceq 'File') { 'file:' + $root.Replace('\', '/') } else { "$GitRepository`?path=/PackageSources/$id#$GitRevision" }
        @{ name = $id; version = '0.1.0-preview.1'; root = $(if ($packageOrigin -ceq 'File') { $root } else { $null }); contentSha256 = $(if ($inventories.ContainsKey($id)) { $inventories[$id].sha256 } else { $null }) }
    })
    $bootstrap = [ordered]@{ 'Assets/YLTestingDistribution/Editor/DistributionEntry.cs' = [IO.File]::ReadAllBytes((Join-Path $repository 'Tools/Testing/DistributionEntry.cs')) }
    $bootstrap['Packages/manifest.json'] = [Text.Encoding]::UTF8.GetBytes((@{ dependencies = $dependencies } | ConvertTo-Json -Depth 8) + "`n")
    $manifestHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$bootstrap['Packages/manifest.json']))
    $hostKey = if ($HybridCLR) { "H$($run.id.Substring(0, 7))" } else { "D-$($editor.version)-$($run.id.Substring(0, 8))" }
    if ([IO.Path]::Exists((Join-Path $workspace.root "Hosts/$hostKey"))) { throw 'Do not adopt an existing distribution host.' }
    $hostRecord = New-YLTestDirectory -Workspace $workspace -Kind Host -Key $hostKey
    $hostLeases.Add((Lock-YLTestDirectory -Workspace $workspace -Path $hostRecord.path))
    Sync-YLUnityHost -HostRecord $hostRecord -Files $bootstrap -Identity $identity -EditorVersion $editor.version
    $null = Invoke-DistributionProcess -HostRecord $hostRecord -Phase Inspect
    $summary.checks++; 'PASS: actual UPM source, pinned package graph, compilation and production asset boundaries'
    if ($RunPlayer) {
        $probeName = if ($HybridCLR) { 'HybridCLRSmoke' } else { 'PlayerSmoke' }
        $probeReferences = @('YLFramework.Runtime'); if ($HybridCLR) { $probeReferences += @('YLFramework.HybridCLR', 'YLFramework.HybridCLR.Sample.Host') }
        $probeFiles = [ordered]@{
            "Assets/YLTestingDistribution/Smoke/$probeName.cs" = [IO.File]::ReadAllBytes((Join-Path $repository "Tools/Testing/$probeName.cs"))
            'Assets/YLTestingDistribution/Smoke/YLTesting.PlayerSmoke.asmdef' = [Text.Encoding]::UTF8.GetBytes((@{ name = 'YLTesting.PlayerSmoke'; references = $probeReferences; autoReferenced = $false } | ConvertTo-Json -Depth 5) + "`n")
        }
        if ($HybridCLR) {
            $sampleRoot = Join-Path @($summary.packages | Where-Object { $_['name'] -ceq 'com.kano.ylframework.hybridclr' })[0]['resolvedPath'] 'Samples~/HybridCLR'
            foreach ($file in @(Get-YLTestTreeFiles $sampleRoot)) { $probeFiles['Assets/Samples/YLFramework.HybridCLR/' + [IO.Path]::GetRelativePath($sampleRoot, $file).Replace('\', '/')] = [IO.File]::ReadAllBytes($file) }
            $probeFiles['Assets/YLTestingDistribution/HybridEditor/HybridCLREntry.cs'] = [IO.File]::ReadAllBytes((Join-Path $repository 'Tools/Testing/HybridCLREntry.cs'))
            $probeFiles['Assets/YLTestingDistribution/HybridEditor/YLTesting.HybridCLR.Editor.asmdef'] = [Text.Encoding]::UTF8.GetBytes((@{
                name = 'YLTesting.HybridCLR.Editor'; includePlatforms = @('Editor'); autoReferenced = $false
                references = @('YLFramework.Runtime', 'YLFramework.Editor', 'YLFramework.HybridCLR', 'YLFramework.HybridCLR.Editor', 'YLFramework.HybridCLR.Sample.Editor', 'HybridCLR.Editor', 'HybridCLR.Runtime')
                overrideReferences = $true; precompiledReferences = @('Newtonsoft.Json.dll')
            } | ConvertTo-Json -Depth 5) + "`n")
        } else { $probeFiles['Assets/YLTestingDistribution/Smoke/Resources/YLPlayerSmoke/Message.txt'] = [Text.Encoding]::UTF8.GetBytes("yl-player-smoke-v1`n") }
        foreach ($entry in $probeFiles.GetEnumerator()) {
            $target = Get-YLHostManagedPath $hostRecord $entry.Key
            if ([IO.Path]::Exists($target) -or [IO.Path]::Exists($target + '.meta')) { throw "Do not replace existing Player smoke inputs: $($entry.Key)" }
        }
        $summary['probeInputs'] = @(foreach ($entry in $probeFiles.GetEnumerator()) {
            $target = Get-YLHostManagedPath $hostRecord $entry.Key; $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)); [IO.File]::WriteAllBytes($target, [byte[]]$entry.Value)
            @{ path = $target; sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash }
        })
        if ($HybridCLR) {
            $configure = Invoke-DistributionProcess -HostRecord $hostRecord -Phase HybridConfigure
            $summary['nativePreparation'] = $configure.result; $summary.checks++
        }
    }
    $buildPhase = if ($HybridCLR) { 'HybridBuild' } else { 'Build' }
    $build = Invoke-DistributionProcess -HostRecord $hostRecord -Phase $buildPhase
    $summary.player = $build.result.status
    if ($build.result.status -ceq 'Passed' -and ($build.result.backend -isnot [string] -or $build.result.backend -cne $Backend)) { throw 'The actual build backend does not match.' }
    if ($build.result.status -ceq 'Passed') { $summary.checks++; 'PASS: actual Player build preserves production assembly boundaries' }
    else { 'PREREQUISITE: Windows64 Player support is not installed; this is not a Player pass' }
    if ($RunPlayer -and $build.result.status -ceq 'Passed') {
        $executable = if ($HybridCLR) { Join-Path $nativePlayerRoot 'YLDistribution.exe' } else { Join-Path $run.path "$buildPhase/Player/YLDistribution.exe" }
        if ($build.result.executable -isnot [string] -or [IO.Path]::GetFullPath($build.result.executable) -ine $executable) { throw 'The actual Player executable is outside the owned build output.' }
        $artifactRoot = [IO.Path]::GetDirectoryName($executable)
        $artifacts = @(Get-YLTestTreeFiles $artifactRoot | ForEach-Object { @{ path = $_; sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash } }); $summary['playerArtifacts'] = $artifacts
        $baseline = if ($HybridCLR) { @(Get-YLTestTreeFiles $build.result.baseline | ForEach-Object { @{ path = $_; sha256 = (Get-FileHash -LiteralPath $_).Hash } }) } else { @() }
        foreach ($phase in @('PlayerRun', 'PlayerRepeat')) {
            $output = Join-Path $run.path $phase; $null = [IO.Directory]::CreateDirectory($output)
            $start = [Diagnostics.ProcessStartInfo]::new($executable); $start.UseShellExecute = $false; $start.WorkingDirectory = $artifactRoot
            $arguments = @('-batchmode', '-logFile', (Join-Path $output 'player.log'), '-ylSmokeOutput', $output); $expectedHybrid = $null
            if ($HybridCLR) { $arguments += @('-ylHybridCLRReceipt', $build.result.receipt, '-force-d3d11', '-screen-width', '640', '-screen-height', '360'); $expectedHybrid = $build.result.hybridClr }
            else { $arguments += '-nographics' }
            foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
            $fact = Invoke-YLTestProcess -StartInfo $start -Log (Join-Path $output 'process.log') -TimeoutMilliseconds ([long]$PlayerTimeoutSeconds * 1000); $summary.processes += $fact
            $summary.playerRun = Read-YLPlayerSmoke -Path (Join-Path $output 'result.json') -ResultId $run.id -HostId $hostRecord.id -UnityVersion $editor.version -Backend $Backend -HybridCLR $expectedHybrid -ProcessResult $fact
            $summary.playerRuns += $summary.playerRun
            $playerLog = [IO.File]::ReadAllText((Join-Path $output 'player.log'))
            if (-not $playerLog.Contains('[YLPlayerSmoke] PASS ' + $run.id) -or $playerLog -match '(?m)^.*(?:Exception:|Assertion failed|\[YLPlayerSmoke\] FAIL)') { throw 'Player log does not close the matching successful smoke run.' }
            foreach ($artifact in @($artifacts) + @($baseline)) { if ((Get-FileHash -LiteralPath $artifact.path -Algorithm SHA256).Hash -cne $artifact.sha256) { throw 'Player artifacts or frozen baseline changed during execution.' } }
            $summary.checks++; 'PASS: actual Player resource/workflow/release evidence and normal process exit close'
        }
        foreach ($inputFile in $summary.probeInputs) { if ((Get-FileHash -LiteralPath $inputFile.path -Algorithm SHA256).Hash -cne $inputFile.sha256) { throw 'Smoke inputs changed during build/execution.' } }
    }
    $current = Get-YLTestInputIdentity -Repository $repository -Paths @($paths) -Toolchain $toolchain.ToArray() -Profile $profile
    if ($current.sha256 -cne $identity.sha256) { throw 'Distribution source inputs changed during verification.' }
    foreach ($package in $products) { if ((Get-DistributionPackageContent -Root $package.root).sha256 -cne $inventories[$package.id].sha256) { throw 'Distribution source package inputs changed.' } }
    foreach ($package in $summary.packages) { if ((Get-DistributionPackageContent -Root $package['resolvedPath'] -AllowManagedFingerprint:($package['source'] -ceq 'Git')).sha256 -cne $package['contentSha256']) { throw 'Distribution installed package inputs changed.' } }
    $summary.status = if ($summary.player -ceq 'Passed') { 'Passed' } else { 'PrerequisiteNotMet' }
} catch { $summary.reason = $_.Exception.Message; throw }
finally {
    $summary.endUtc = [DateTime]::UtcNow.ToString('o')
    try { Write-YLTestJson -Path (Join-Path $run.path 'summary.json') -Value $summary }
    finally { foreach ($hostLease in $hostLeases) { $hostLease.Dispose() }; $lease.Dispose() }
    $null = Invoke-YLTestRotation -Workspace $workspace -Key $run.key
    "Distribution evidence: $($run.path)"
}
if ($summary.status -cne 'Passed') { exit 2 }
