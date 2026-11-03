#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $WorkspaceRoot,
    [Parameter(Mandatory)][string] $Unity,
    [Parameter(Mandatory)][ValidateSet('DOTween', 'Excel', 'DomainExcel', 'HybridCLR')][string] $Integration,
    [ValidateRange(1, 86400)][int] $TimeoutSeconds = 900,
    [string] $DOTweenArchive,
    [ValidateSet('File', 'Git', 'LocalTarball', 'Registry')][string] $PackageSource = 'File',
    [string] $GitRepository,
    [string] $GitRevision,
    [string] $RegistryUrl,
    [string] $RegistryVersion,
    [string] $ReleaseManifest,
    [ValidateSet('Required', 'Absent')][string] $DOTweenSdkMode = 'Required'
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Common/Process.ps1')
. (Join-Path $PSScriptRoot '../Common/Results.ps1')
. (Join-Path $PSScriptRoot '../UnityHost.ps1')

function Get-YLIntegrationSource {
    param([ValidateSet('File', 'Git', 'LocalTarball', 'Registry')][string] $PackageSource = 'File', [string] $GitRepository, [string] $GitRevision,
        [string] $RegistryUrl, [string] $RegistryVersion, [string] $ReleaseManifest)
    $artifactSource = Get-YLRegistrySource -PackageSource $PackageSource -Arguments $PSBoundParameters
    if ($null -ne $artifactSource) { return $artifactSource }
    $hasRepository = $PSBoundParameters.ContainsKey('GitRepository')
    $hasRevision = $PSBoundParameters.ContainsKey('GitRevision')
    if ($PackageSource -ieq 'File') {
        if ($hasRepository -or $hasRevision) { throw 'File package source refuses Git parameters.' }
        return [ordered]@{ packageOrigin = 'File'; gitRepository = $null; gitRevision = $null }
    }
    if (-not $hasRepository -or -not $hasRevision) { throw 'Git requires GitRepository and GitRevision together; no local fallback is allowed.' }
    $uri = $null; $url = $GitRepository -creplace '^git\+', ''
    if (-not [Uri]::TryCreate($url, [UriKind]::Absolute, [ref]$uri) -or -not $uri.IsWellFormedOriginalString() -or
        $uri.Scheme -cnotin @('http', 'https', 'ssh') -or [string]::IsNullOrWhiteSpace($uri.Host) -or
        $uri.AbsolutePath -in @('', '/') -or $GitRepository -match '[\s?#\\]' -or [Uri]::UnescapeDataString($url) -match '[\s?#\\]' -or
        ($uri.UserInfo -and ($uri.Scheme -cne 'ssh' -or $uri.UserInfo.Contains(':'))) -or
        (-not $GitRepository.StartsWith('git+', [StringComparison]::Ordinal) -and -not $uri.AbsolutePath.EndsWith('.git', [StringComparison]::Ordinal)) -or
        $GitRevision -cnotmatch '\A[0-9a-fA-F]{40}\z') {
        throw 'Git requires a real HTTP/HTTPS/SSH repository URL (UPM .git or git+ syntax), no query/fragment/credentials, and a full 40hex GitRevision.'
    }
    return [ordered]@{ packageOrigin = 'GitRemote'; gitRepository = $GitRepository; gitRevision = $GitRevision.ToLowerInvariant() }
}

function Read-YLIntegrationJson([string] $Path) {
    $reader = [Newtonsoft.Json.JsonTextReader]::new([IO.StringReader]::new([IO.File]::ReadAllText($Path)))
    $reader.DateParseHandling = [Newtonsoft.Json.DateParseHandling]::None
    $settings = [Newtonsoft.Json.Linq.JsonLoadSettings]::new()
    $settings.DuplicatePropertyNameHandling = [Newtonsoft.Json.Linq.DuplicatePropertyNameHandling]::Error
    try {
        $value = [Newtonsoft.Json.Linq.JObject]::Load($reader, $settings)
        if ($reader.Read()) { throw 'Unexpected trailing integration JSON content.' }
        return ,$value
    } finally { $reader.Dispose() }
}

function Get-YLIntegrationPackageContent([string] $Root, [string] $PackageId, [switch] $NormalizeManifest, [switch] $AllowManagedFingerprint) {
    function Convert-CanonicalToken([Newtonsoft.Json.Linq.JToken] $Token) {
        if ($Token -is [Newtonsoft.Json.Linq.JObject]) {
            $result = [Newtonsoft.Json.Linq.JObject]::new()
            [string[]]$names = @($Token.Properties() | ForEach-Object Name); [Array]::Sort($names, [StringComparer]::Ordinal)
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
    $files = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($relative in @(Get-YLIntegrationPackageFiles -Repository $Root -Packages @(@{ id = $PackageId; root = $Root; version = '0.1.0-preview.1' }))) {
        $path = Join-Path $Root $relative
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($relative -ceq 'package.json') {
            $manifest = Read-YLIntegrationJson $path
            $fingerprint = $manifest.Property('_fingerprint')
            if ($null -ne $fingerprint) {
                if (-not $NormalizeManifest -or -not $AllowManagedFingerprint -or $fingerprint.Value.Type -ne [Newtonsoft.Json.Linq.JTokenType]::String -or
                    [string]$fingerprint.Value -cnotmatch '\A[0-9a-fA-F]{40}\z') { throw 'Untrusted package-manager fingerprint metadata.' }
                $fingerprint.Remove()
            }
            if ($NormalizeManifest) {
                $json = (Convert-CanonicalToken $manifest).ToString([Newtonsoft.Json.Formatting]::None)
                $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($json))).ToLowerInvariant()
            }
        }
        $files.Add($relative, @{ path = $relative; sha256 = $hash })
    }
    if (-not $files.ContainsKey('package.json')) { throw 'The package manifest is missing.' }
    $text = [Text.StringBuilder]::new()
    foreach ($item in $files.GetEnumerator()) { $null = $text.Append($item.Key).Append([char]0).Append($item.Value.sha256).Append("`n") }
    return @{ files = @($files.Values); sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text.ToString()))).ToLowerInvariant() }
}

function Assert-YLIntegrationInstalledPackage([Collections.IDictionary] $Request, [Collections.IDictionary] $Expected, [Collections.IDictionary] $Row, [Collections.IDictionary] $Lock, [string] $HostRoot) {
    if ($Request.packageOrigin -cin @('Registry', 'LocalTarball')) {
        foreach ($field in @('name', 'version', 'source', 'assetPath', 'resolvedPath', 'packageId', 'contentSha256')) {
            if ($Row[$field] -isnot [string]) { throw "Invalid actual installed package identity: $field" }
        }
        if ($Row.name -cne $Expected.name -or $Row.version -cne $Expected.version -or $Row.source -cne $Request.packageOrigin -or
            $Row.assetPath -cne ('Packages/' + $Expected.name) -or $Row.contentSha256 -cne $Expected.contentSha256 -or
            ($Row.verifiedFiles -isnot [int] -and $Row.verifiedFiles -isnot [long]) -or $Row.verifiedFiles -ne $Expected.files.Count) { throw 'Actual Registry/tarball package identity/content is incorrect.' }
        $actual = Get-YLIntegrationPackageContent -Root $Row.resolvedPath -PackageId $Expected.name -NormalizeManifest -AllowManagedFingerprint
        if ($actual.sha256 -cne $Expected.contentSha256) { throw 'Actual installed artifact differs from the frozen release payload.' }
        return
    }
    $git = $Request.packageOrigin -ceq 'GitRemote'
    $name = $Expected.name
    $locator = if ($git) { $Request.gitRepository + '?path=/PackageSources/' + $name + '#' + $Request.gitRevision } else { 'file:../PackageSources/' + $name }
    foreach ($field in @('name', 'version', 'source', 'assetPath', 'resolvedPath', 'packageId', 'contentSha256')) {
        if ($Row[$field] -isnot [string]) { throw "Invalid actual installed package identity: $field" }
    }
    $root = Get-YLTestPath $Row.resolvedPath
    $apiLocator = $Row.packageId
    if ($git) {
        $prefix = $name + '@'
        if ($apiLocator.StartsWith($prefix + 'git+', [StringComparison]::Ordinal)) { $apiLocator = $prefix + $apiLocator.Substring($prefix.Length + 4) }
        $apiExpected = $prefix + ($locator -creplace '\Agit\+', '')
    }
    else { $apiExpected = $name + '@' + $locator }
    if ($Row.name -cne $name -or $Row.version -cne $Expected.version -or $Row.source -cne $(if ($git) { 'Git' } else { 'Local' }) -or
        $Row.assetPath -cne "Packages/$name" -or ($git -and $apiLocator -cne $apiExpected) -or $Row.contentSha256 -cne $Expected.contentSha256 -or
        $Row.verifiedFiles -isnot [long] -or $Row.verifiedFiles -ne $Expected.files.Count) { throw 'Actual package source/version/locator/content is not the requested installation.' }
    if ($git) {
        foreach ($field in @('apiHash', 'apiRevision')) {
            if ($null -ne $Row[$field] -and ($Row[$field] -isnot [string] -or ($Row[$field] -cne '' -and $Row[$field] -cne $Request.gitRevision))) { throw 'Nonempty Git API evidence contradicts the requested revision.' }
        }
        if ($Row.revision -isnot [string] -or $Row.revision -cne $Request.gitRevision -or
            (-not $root.StartsWith((Join-Path $HostRoot 'Library/PackageCache') + '\', [StringComparison]::OrdinalIgnoreCase) -and
             -not $root.StartsWith($Request.cacheRoot + '\', [StringComparison]::OrdinalIgnoreCase)) -or
            [IO.Directory]::Exists((Join-Path $HostRoot 'PackageSources'))) { throw 'Actual Git package revision/resolvedRoot is not an owned remote installation.' }
    } elseif ($root -ine $Expected.root) { throw 'Actual File package is not the complete owned staging.' }
    $locked = $Lock['dependencies'][$name]
    if ($locked -isnot [Collections.IDictionary] -or $locked['source'] -isnot [string] -or $locked['source'] -cne $(if ($git) { 'git' } else { 'local' }) -or
        $locked['version'] -isnot [string] -or $locked['version'] -cne $locator -or
        ($git -and ($locked['hash'] -isnot [string] -or $locked['hash'] -cne $Request.gitRevision))) { throw 'Actual generated lock source/version/hash differs from the request.' }
    $actual = Get-YLIntegrationPackageContent -Root $root -PackageId $name -NormalizeManifest:$git -AllowManagedFingerprint:$git
    if ($actual.sha256 -cne $Expected.contentSha256) { throw 'Actual resolved package file set/bytes differ from the explicit local release expected template.' }
    $manifest = (Read-YLIntegrationJson (Join-Path $root 'package.json')).ToString() | ConvertFrom-Json -AsHashtable -DateKind String
    if ($manifest.name -isnot [string] -or $manifest.name -cne $name -or $manifest.version -isnot [string] -or $manifest.version -cne $Expected.version -or
        $manifest.dependencies -isnot [Collections.IDictionary] -or $locked['dependencies'] -isnot [Collections.IDictionary] -or
        $locked['dependencies'].Count -ne $manifest.dependencies.Count) { throw 'Actual manifest identity or complete lock dependency closure is invalid.' }
    foreach ($dependency in $manifest.dependencies.GetEnumerator()) {
        if ($dependency.Value -isnot [string] -or $locked['dependencies'][$dependency.Key] -isnot [string] -or $locked['dependencies'][$dependency.Key] -cne $dependency.Value -or
            ($dependency.Key.StartsWith('com.kano.', [StringComparison]::Ordinal) -and
                ($dependency.Value -cne $Expected.version -or @($Request.packages | Where-Object { $_.name -ceq $dependency.Key }).Count -ne 1))) { throw 'Actual internal package/lock closure is incomplete.' }
    }
}

function Get-YLIntegrationDefinition([string] $Name) {
    $assemblies = @(
        @{ name = 'YLFramework.Runtime'; package = 'com.kano.ylframework'; directory = 'Runtime'; player = $true },
        @{ name = 'YLFramework.Editor'; package = 'com.kano.ylframework'; directory = 'Editor'; player = $false }
    )
    $id = switch -CaseSensitive ($Name) {
        DOTween {
            $assemblies += @{ name = 'YLFramework.Actions.DOTween'; package = 'com.kano.ylframework.dotween'; directory = 'Runtime'; player = $true }
            'com.kano.ylframework.dotween'
        }
        Excel { 'com.kano.ylframework.excel' }
        DomainExcel { 'com.kano.ylframework.domain-excel' }
        HybridCLR {
            $assemblies += @{ name = 'YLFramework.HybridCLR'; package = 'com.kano.ylframework.hybridclr'; directory = 'Runtime'; player = $true }
            $assemblies += @{ name = 'YLFramework.HybridCLR.Editor'; package = 'com.kano.ylframework.hybridclr'; directory = 'Editor'; player = $false }
            'com.kano.ylframework.hybridclr'
        }
        default { throw 'Unknown integration profile.' }
    }
    if ($Name -cin @('Excel', 'DomainExcel')) {
        $assemblies += @{ name = 'YLFramework.Excel.Editor'; package = 'com.kano.ylframework.excel'; directory = 'Editor'; player = $false }
    }
    if ($Name -ceq 'DomainExcel') {
        $assemblies += @{ name = 'YLFramework.Domain.Excel.Editor'; package = 'com.kano.ylframework.domain-excel'; directory = 'Editor'; player = $false }
    }
    return @{ id = $id; assemblies = $assemblies }
}

function Get-YLIntegrationPackageFiles([string] $Repository, [object[]] $Packages) {
    foreach ($package in $Packages) {
        if ($package.version -cne '0.1.0-preview.1') { throw 'Integration requires version 0.1.0-preview.1.' }
        $license = Join-Path $package.root 'LICENSE.md'
        if (-not [IO.File]::Exists($license) -or [IO.FileInfo]::new($license).Length -eq 0) { throw "Package license is missing: $($package.id)" }
        foreach ($path in @(Get-YLTestTreeFiles $package.root)) {
            $relative = [IO.Path]::GetRelativePath($package.root, $path).Replace('\', '/')
            if ($relative -match '^(?:Assets|Packages|ProjectSettings|Library|Temp|Tests|Tools|\.git|\.kilo|\.kilocode|\.opencode|obj|bin)(?:/|$)|(?:^|/)(?:AGENTS\.md|\.yl-[^/]+|[^/]+\.(?:csproj|sln|slnx))$' -or
                $relative -match '(?i)(?:IvanMurzak|UnityMcp|ReflectorNet)') { throw "Unauthorized product package input: $relative" }
            $stream = [IO.File]::OpenRead($path)
            try {
                $prefix = [byte[]]::new(200); $length = $stream.Read($prefix, 0, $prefix.Length)
                if ([Text.Encoding]::UTF8.GetString($prefix, 0, $length).StartsWith('version https://git-lfs.github.com/spec/v1', [StringComparison]::Ordinal)) { throw "Unmaterialized LFS input: $relative" }
                if ($relative.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase) -and
                    ($length -lt 2 -or $prefix[0] -ne 77 -or $prefix[1] -ne 90 -or $package.id -cin @('com.kano.ylframework.dotween', 'com.kano.ylframework.hybridclr'))) { throw "Unapproved package DLL: $($package.id)/$relative" }
            } finally { $stream.Dispose() }
            [IO.Path]::GetRelativePath($Repository, $path).Replace('\', '/')
        }
    }
}

function Get-YLIntegrationHybridSdk([string] $Repository) {
    $id = 'com.code-philosophy.hybridclr'
    try {
        $manifest = [IO.File]::ReadAllText((Join-Path $Repository 'Packages/manifest.json')) | ConvertFrom-Json -AsHashtable -DateKind String
        $lock = [IO.File]::ReadAllText((Join-Path $Repository 'Packages/packages-lock.json')) | ConvertFrom-Json -AsHashtable -DateKind String
        $approval = [IO.File]::ReadAllText((Join-Path $Repository 'PackageSources/com.kano.ylframework.hybridclr/Dependencies.json')) | ConvertFrom-Json -AsHashtable -DateKind String
        $url = $manifest['dependencies'][$id]; $entry = $lock['dependencies'][$id]; $approved = $approval['package']
        if ($url -isnot [string] -or $url -cne 'https://gitee.com/focus-creative-games/hybridclr_unity.git#v9.0.3' -or
            $entry -isnot [Collections.IDictionary] -or $entry['source'] -isnot [string] -or $entry['source'] -cne 'git' -or
            $entry['version'] -isnot [string] -or $entry['version'] -cne $url -or $entry['hash'] -isnot [string] -or $entry['hash'] -cnotmatch '^[0-9a-f]{40}$' -or
            $approved['name'] -cne $id -or $approved['version'] -cne '9.0.3' -or $approved['url'] -cne $url -or
            $approved['commit'] -isnot [string] -or $approved['commit'] -cne $entry['hash'] -or $approved['bundled'] -isnot [bool] -or $approved['bundled']) { throw 'The official 9.0.3 SDK declaration, approved commit and actual root lock must match.' }
        return @{ declaration = $url; revision = $entry['hash']; version = '9.0.3' }
    } catch { throw "PrerequisiteNotMet: HybridCLR SDK: $($_.Exception.Message)" }
}

function Get-YLIntegrationDotweenSdk([string] $Repository, [string] $Archive, [string] $OwnedSdkRoot) {
    try {
        $approval = [IO.File]::ReadAllText((Join-Path $Repository 'PackageSources/com.kano.ylframework.dotween/Dependencies.json')) | ConvertFrom-Json -AsHashtable -DateKind String
        $vendor = $approval['vendor']
        $root = if ($OwnedSdkRoot) { Get-YLTestPath $OwnedSdkRoot } else { Get-YLTestPath (Join-Path $Repository 'Assets/Plugins/Demigiant/DOTween') }
        $dll = Join-Path $root 'DOTween.dll'
        if ($OwnedSdkRoot -and -not [IO.Directory]::Exists($root)) {
            if (-not [IO.File]::Exists($Archive) -or (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash -ine $vendor['archiveSha256']) { throw 'The explicitly approved Standard SDK archive does not match.' }
            $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
            try {
                $core = @($zip.Entries | Where-Object { $_.FullName -cmatch '(?:^|/)DOTween\.dll$' })
                if ($core.Count -ne 1) { throw 'The archive must contain one runtime DLL.' }
                $prefix = $core[0].FullName.Substring(0, $core[0].FullName.Length - 'DOTween.dll'.Length)
                foreach ($entry in $zip.Entries) {
                    if (-not $entry.FullName.StartsWith($prefix, [StringComparison]::Ordinal) -or $entry.FullName.EndsWith('/') -or $entry.FullName.EndsWith('.meta')) { continue }
                    $relative = $entry.FullName.Substring($prefix.Length)
                    if (-not $relative -or $relative -match '(?:^|/)\.\.?(/|$)|\\|:') { throw 'Invalid approved SDK archive path.' }
                    $target = Join-Path $root $relative
                    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
                    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $false)
                }
            } finally { $zip.Dispose() }
        }
        if ($vendor['version'] -cne '1.2.825' -or $vendor['assemblySha256'] -isnot [string] -or $vendor['archiveSha256'] -isnot [string] -or
            -not [IO.File]::Exists($dll) -or (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash -ine $vendor['assemblySha256']) { throw 'The existing DOTween.dll does not match the approved 1.2.825 SDK hash.' }
        if (-not [IO.File]::Exists($Archive) -or (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash -ine $vendor['archiveSha256']) { throw 'The approved complete Standard SDK archive is missing or has the wrong SHA256.' }
        $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
        try {
            $core = @($zip.Entries | Where-Object { $_.FullName -cmatch '(?:^|/)DOTween\.dll$' })
            if ($core.Count -ne 1) { throw 'The approved archive must contain exactly one DOTween.dll.' }
            $prefix = $core[0].FullName.Substring(0, $core[0].FullName.Length - 'DOTween.dll'.Length)
            $approvedFiles = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
            foreach ($entry in $zip.Entries) {
                if (-not $entry.FullName.StartsWith($prefix, [StringComparison]::Ordinal) -or $entry.FullName.EndsWith('/') -or $entry.FullName.EndsWith('.meta')) { continue }
                $relative = $entry.FullName.Substring($prefix.Length)
                if ($relative -match '(?:^|/)\.\.?(/|$)|\\|:' -or -not $relative) { throw 'Invalid SDK archive path.' }
                $stream = $entry.Open()
                try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() } finally { $stream.Dispose() }
                $approvedFiles.Add($relative, $hash)
            }
            foreach ($required in @('DOTween.dll', 'Editor/DOTweenEditor.dll', 'Modules/DOTweenModuleAudio.cs', 'Modules/DOTweenModulePhysics.cs',
                'Modules/DOTweenModulePhysics2D.cs', 'Modules/DOTweenModuleSprite.cs', 'Modules/DOTweenModuleUI.cs', 'Modules/DOTweenModuleUtils.cs', 'readme.txt')) {
                if (-not $approvedFiles.ContainsKey($required)) { throw "Incomplete approved SDK: $required" }
            }
            $paths = @(Get-YLTestTreeFiles $root)
            $payload = @($paths | Where-Object { -not $_.EndsWith('.meta', [StringComparison]::OrdinalIgnoreCase) })
            if ($payload.Count -ne $approvedFiles.Count) { throw 'The existing SDK is not the complete unchanged approved archive file set.' }
            foreach ($path in $payload) {
                $relative = [IO.Path]::GetRelativePath($root, $path).Replace('\', '/')
                if (-not $approvedFiles.ContainsKey($relative) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $approvedFiles[$relative]) { throw "The existing SDK file is not approved: $relative" }
            }
            if (-not [IO.File]::ReadAllText((Join-Path $root 'readme.txt')).Contains('license', [StringComparison]::OrdinalIgnoreCase)) { throw 'The SDK original readme/license notice is missing.' }
            return @{ root = $root; paths = @($paths | ForEach-Object { [IO.Path]::GetRelativePath($Repository, $_).Replace('\', '/') });
                version = $vendor['version']; archiveSha256 = $vendor['archiveSha256']; assemblySha256 = $vendor['assemblySha256']; files = $approvedFiles }
        } finally { $zip.Dispose() }
    } catch { throw "PrerequisiteNotMet: DOTween SDK: $($_.Exception.Message)" }
}

$sourceArguments = @{ PackageSource = $PackageSource }
foreach ($parameter in @('GitRepository', 'GitRevision', 'RegistryUrl', 'RegistryVersion', 'ReleaseManifest')) { if ($PSBoundParameters.ContainsKey($parameter)) { $sourceArguments[$parameter] = $PSBoundParameters[$parameter] } }
$source = Get-YLIntegrationSource @sourceArguments
if ($Integration -cne 'DOTween' -and ($PSBoundParameters.ContainsKey('DOTweenSdkMode') -or $PSBoundParameters.ContainsKey('DOTweenArchive'))) { throw 'DOTween SDK parameters require Integration DOTween.' }
if ($Integration -ceq 'DOTween' -and $DOTweenSdkMode -ceq 'Absent' -and $PSBoundParameters.ContainsKey('DOTweenArchive')) { throw 'DOTween Absent refuses an SDK archive.' }
if ($MyInvocation.InvocationName -eq '.') { return }
if (-not $IsWindows -or $PSVersionTable.PSVersion.Major -ne 7 -or $PSVersionTable.PSVersion.Minor -ne 6) { throw 'Windows PowerShell Core 7.6 is required.' }
$repository = Get-YLTestPath ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')))
$Unity = Get-YLTestPath -Path $Unity -ReadOnly
$configuration = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../TestSuites.json')) | ConvertFrom-Json -DateKind String
$editor = Get-YLTestEditorProfile -Editors $configuration.editors -VersionInfo ([Diagnostics.FileVersionInfo]::GetVersionInfo($Unity))
$definition = Get-YLIntegrationDefinition $Integration
if ($Integration -ceq 'DOTween' -and $DOTweenSdkMode -ceq 'Absent') { $definition.assemblies = @($definition.assemblies | Where-Object { $_.name -cne 'YLFramework.Actions.DOTween' }) }
$profile = "IntegrationPackages-v1-$($editor.productVersion)-$Integration-" + (ConvertTo-Json -InputObject $source -Compress)
$workspace = Initialize-YLTestWorkspace -WorkspaceRoot $WorkspaceRoot -Repository $repository
$run = New-YLTestDirectory -Workspace $workspace -Kind Result -Key "Integration-$Integration-$($editor.productVersion)"
$lease = Lock-YLTestDirectory -Workspace $workspace -Path $run.path
$hostLease = $null; $hostRecord = $null; $cacheLease = $null; $cache = $null; $identity = $null; $templateIdentity = $null
$inputPaths = @(); $sdk = $null; $products = @(); $report = $null
$git = $source.packageOrigin -ceq 'GitRemote'
$artifact = $source.packageOrigin -cin @('LocalTarball', 'Registry')
$summary = [ordered]@{
    schemaVersion = 1; purpose = 'YLTesting.IntegrationPackages'; ownerId = $run.id; action = 'Check'; status = 'Failed'; reason = $null
    profile = $profile; integration = $Integration; endUtc = $null; host = $null; processes = @(); report = $null; inputIdentity = $null
    packageOrigin = $source.packageOrigin; gitRepository = $source.gitRepository; gitRevision = $source.gitRevision; dotweenSdkMode = $DOTweenSdkMode
    unity = @{ executable = $Unity; version = $editor.version; productVersion = $editor.productVersion }
    packages = @(); assemblies = @(); compilation = 'NotVerified'; playerGraph = 'NotVerified'
    nativeInstallation = 'NotRun'; playerBuild = 'NotRun'; playerRuntime = 'NotRun'; hotUpdatePlayer = 'NotVerified'
    scope = 'Owned UPM consumer installation and actual Editor compilation only. File and GitRemote are separate origins; not native or hot-update Player acceptance.'
}
if ($artifact) {
    $summary['acceptanceKind'] = if ($PackageSource -ceq 'LocalTarball') { 'ArtifactPreflight' } else { 'RegistryInstallation' }
    $summary['release'] = @{ sourceCommit = $source.release.sourceCommit; manifestSha256 = $source.release.sha256; producer = $source.release.producer }
    $summary.scope = "$($summary.acceptanceKind): actual installed source and Editor/Player compilation graphs. Not native, hot-update Player or publish readiness."
}
try {
    $artifactInputs = $null; $manifestObject = $null
    if ($artifact) {
        $artifactInputs = Initialize-YLRegistryExpectedInputs -Source $source -Output (Join-Path $run.path 'ExpectedArtifacts')
        $summary['registryArtifacts'] = @($artifactInputs.packages | ForEach-Object { @{ id = $_.id; sha256 = $_.sha256; integrity = $_.integrity; payloadSha256 = $_.payloadSha256; registry = $_.registryEvidence } })
        $products = @($artifactInputs.packages | Where-Object { $_.id -cin @(Get-YLRegistryClosure -Release $source.release -EntryPackage $definition.id).id })
        $packagePaths = @()
    } else {
        $products = @(Get-YLUpmPackages -Repository $repository -Ids @($definition.id))
        $packagePaths = @(Get-YLIntegrationPackageFiles -Repository $repository -Packages $products)
    }
    $inventories = @{}
    foreach ($package in $products) { $inventories[$package.id] = Get-YLIntegrationPackageContent -Root $package.root -PackageId $package.id -NormalizeManifest:($git -or $artifact) }
    # This snapshot is an explicit release expectation, never an observed Git installation or Git source identity.
    $templateIdentity = if ($artifact) { @{ sha256 = $source.release.sha256 } } else { Get-YLTestInputIdentity -Repository $repository -Paths $packagePaths -Profile 'LocalReleaseExpectedTemplateNotInstallSource' }
    $summary['expectedTemplate'] = @{ purpose = $(if ($artifact) { 'FrozenReleaseManifest' } else { 'LocalReleaseExpectedTemplateNotInstallSource' }); identity = $templateIdentity; packages = @($products.id) }
    $profileInputs = if ($artifact) { @() } else { @(Get-YLUnityPackageInputs -EditorProfile $editor) }
    $inputPaths = @(@(if (-not $git) { $packagePaths }) + $profileInputs + @('Tools/Testing/Tests/Test-YLIntegrationPackages.ps1', 'Tools/Testing/IntegrationEntry.cs',
        'Tools/Testing/Common/Packages.ps1', 'Tools/Testing/Common/Workspace.ps1', 'Tools/Testing/Common/Process.ps1', 'Tools/Testing/Common/Results.ps1', 'Tools/Testing/UnityHost.ps1', 'Tools/Testing/TestSuites.json') | Sort-Object -Unique)
    if ($artifact) { $inputPaths += 'Tools/Packaging/Build-YLRegistryPackages.ps1' }
    if ($artifact -and $Integration -ceq 'HybridCLR') { $inputPaths += @('Tools/Testing/RegistryAuthoringEntry.cs', 'Tools/Testing/Common/RegistryAuthoring.ps1') }
    if ($PackageSource -ceq 'Registry' -and -not ($Integration -ceq 'DOTween' -and $DOTweenSdkMode -ceq 'Absent')) { $inputPaths += @('Tools/Testing/RegistryLifecycleEntry.cs', 'Tools/Testing/Common/RegistryLifecycle.ps1') }
    if ($artifact) {
        $manifestObject = New-YLRegistryHostManifest -PackageSource $PackageSource -Release $source.release -EntryPackage $definition.id -RegistryUrl $source.registryUrl
        $dependencies = $manifestObject.dependencies
        $summary['release'] = @{ sourceCommit = $source.release.sourceCommit; manifestSha256 = $source.release.sha256; producer = $source.release.producer }
        $summary['acceptanceKind'] = if ($PackageSource -ceq 'LocalTarball') { 'ArtifactPreflight' } else { 'RegistryInstallation' }
    } else {
        $dependencies = [ordered]@{ 'com.unity.ugui' = $editor.ugui }
        $profileManifest = [IO.File]::ReadAllText((Join-Path $repository $profileInputs[0])) | ConvertFrom-Json -AsHashtable -DateKind String
        $profileLock = [IO.File]::ReadAllText((Join-Path $repository $profileInputs[1])) | ConvertFrom-Json -AsHashtable -DateKind String
        foreach ($item in $profileManifest['dependencies'].GetEnumerator()) {
            if ($item.Key.StartsWith('com.unity.modules.', [StringComparison]::Ordinal)) { $dependencies[$item.Key] = $item.Value }
        }
        $dependencies['com.unity.nuget.newtonsoft-json'] = $profileLock['dependencies']['com.unity.nuget.newtonsoft-json']['version']
    }
    if ($Integration -ceq 'HybridCLR') {
        if ($artifact) {
            $approval = Join-Path @($products | Where-Object id -CEQ 'com.kano.ylframework.hybridclr')[0].root 'Dependencies.json'
            $sdk = Get-YLHybridClrRegistrySdk -Approval $approval -Output (Join-Path $run.path 'RegistrySdk') -RegistryUrl $source.registryUrl
            $summary['sdkTarball'] = $sdk.evidence
        } else {
            $sdk = Get-YLIntegrationHybridSdk $repository
            $dependencies['com.code-philosophy.hybridclr'] = $sdk.declaration
            $inputPaths = @($inputPaths + @('Packages/manifest.json', 'Packages/packages-lock.json', 'PackageSources/com.kano.ylframework.hybridclr/Dependencies.json') | Sort-Object -Unique)
        }
    }
    if ($Integration -ceq 'DOTween' -and $DOTweenSdkMode -ceq 'Required') {
        $sdkRepository = $repository
        if ($artifact) { $sdkRepository = $artifactInputs.root }
        if (-not $DOTweenArchive) {
            $approval = [IO.File]::ReadAllText((Join-Path $sdkRepository 'PackageSources/com.kano.ylframework.dotween/Dependencies.json')) | ConvertFrom-Json -AsHashtable -DateKind String
            $DOTweenArchive = Join-Path $run.path 'DOTween-1.2.825.zip'
            try { Invoke-WebRequest -Uri $approval['vendor']['url'] -OutFile $DOTweenArchive -TimeoutSec $TimeoutSeconds -ErrorAction Stop }
            catch { throw "PrerequisiteNotMet: Cannot retrieve the explicitly approved DOTween Standard archive: $($_.Exception.Message)" }
        }
        $DOTweenArchive = Get-YLTestPath -Path $DOTweenArchive -ReadOnly
        $sdk = Get-YLIntegrationDotweenSdk -Repository $sdkRepository -Archive $DOTweenArchive -OwnedSdkRoot (Join-Path $run.path 'ApprovedSdk')
        if (-not $artifact) { $inputPaths = @($inputPaths + 'PackageSources/com.kano.ylframework.dotween/Dependencies.json' | Sort-Object -Unique) }
        $summary['approvedSdkArchive'] = @{ version = $sdk.version; archive = $DOTweenArchive; archiveSha256 = $sdk.archiveSha256; assemblySha256 = $sdk.assemblySha256; files = $sdk.files.Count }
    }
    if ($Integration -cin @('Excel', 'DomainExcel')) {
        $excel = if ($artifact) { Join-Path @($products | Where-Object id -CEQ 'com.kano.ylframework.excel')[0].root 'Editor' } else { Join-Path $repository 'PackageSources/com.kano.ylframework.excel/Editor' }
        $approval = [IO.File]::ReadAllText((Join-Path $excel 'Dependencies.json')) | ConvertFrom-Json -AsHashtable -DateKind String
        $backend = $approval['backend']; $dll = Join-Path $excel 'Plugins/MiniExcel.dll'
        if ($backend['version'] -cne '1.31.2' -or -not [IO.File]::Exists($dll) -or
            (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash -ine $backend['assemblySha256']) { throw 'PrerequisiteNotMet: MiniExcel 1.31.2 does not match its approved assembly SHA256.' }
        foreach ($notice in @($backend['licenseFile'], $backend['attributionFile'])) {
            $path = Get-YLTestPath (Join-Path $excel $notice)
            if (-not $path.StartsWith($excel + '\', [StringComparison]::OrdinalIgnoreCase) -or -not [IO.File]::Exists($path) -or [IO.FileInfo]::new($path).Length -eq 0) { throw 'PrerequisiteNotMet: MiniExcel license or attribution is missing.' }
        }
        $summary['expectedSdk'] = @{ name = 'MiniExcel'; version = $backend['version']; assembly = [Reflection.AssemblyName]::GetAssemblyName($dll).FullName; sha256 = $backend['assemblySha256']; license = $backend['license']; purpose = 'LocalReleaseExpectedTemplateNotInstallSource' }
        $sdk = $backend
        if (-not $artifact) { $inputPaths = @($inputPaths + 'PackageSources/com.kano.ylframework.excel/Editor/Dependencies.json' | Sort-Object -Unique) }
    }
    $toolchain = @($Unity)
    if ($Integration -ceq 'DOTween' -and $DOTweenSdkMode -ceq 'Required') { $toolchain += $DOTweenArchive }
    if ($artifact) { $toolchain += $source.release.path }
    $identity = Get-YLTestInputIdentity -Repository $repository -Paths $inputPaths -Toolchain $toolchain -Profile $profile
    $summary.inputIdentity = $identity
    $hostRecord = New-YLTestDirectory -Workspace $workspace -Kind Host -Key "I-$Integration-$($run.id.Substring(0, 10))"
    $hostLease = Lock-YLTestDirectory -Workspace $workspace -Path $hostRecord.path
    $summary.host = @{ path = $hostRecord.path; id = $hostRecord.id }
    $cache = New-YLTestDirectory -Workspace $workspace -Kind Cache -Key "I-$($run.id)"
    $cacheLease = Lock-YLTestDirectory -Workspace $workspace -Path $cache.path
    $summary['cache'] = @{ path = $cache.path; id = $cache.id }
    Assert-YLUnityHostIdle $hostRecord
    $files = [ordered]@{}
    if (-not $git -and -not $artifact) { foreach ($relative in $packagePaths) { $files[$relative] = [IO.File]::ReadAllBytes((Join-Path $repository $relative)) } }
    if (-not $artifact) { foreach ($package in $products) {
        $dependencies[$package.id] = if ($git) { $source.gitRepository + '?path=/' + $package.relative + '#' + $source.gitRevision } else { 'file:../' + $package.relative }
    } }
    if ($Integration -ceq 'DOTween' -and $DOTweenSdkMode -ceq 'Required') {
        foreach ($relative in $sdk.files.Keys) { $files['Assets/Plugins/Demigiant/DOTween/' + $relative] = [IO.File]::ReadAllBytes((Join-Path $sdk.root $relative)) }
        # These are new owned ProjectSettings, never the source project's settings.
        $files['ProjectSettings/ProjectSettings.asset'] = [Text.Encoding]::UTF8.GetBytes("%YAML 1.1`n%TAG !u! tag:unity3d.com,2011:`n--- !u!129 &1`nPlayerSettings:`n  m_ObjectHideFlags: 0`n  serializedVersion: 26`n  scriptingDefineSymbols:`n    Standalone: DOTWEEN`n")
    }
    $files['Assets/YLTestingIntegration/Editor/IntegrationEntry.cs'] = [IO.File]::ReadAllBytes((Join-Path $repository 'Tools/Testing/IntegrationEntry.cs'))
    if (-not $artifact) { $manifestObject = @{ dependencies = $dependencies } }
    $files['Packages/manifest.json'] = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $manifestObject -Depth 10) + "`n")
    Sync-YLUnityHost -HostRecord $hostRecord -Files $files -Identity $identity -EditorVersion $editor.version
    $expectedPackages = @(foreach ($package in $products) {
        $expected = @{ name = $package.id; version = $package.version; files = $inventories[$package.id].files; contentSha256 = $inventories[$package.id].sha256 }
        if ($artifact) { $expected.root = $null; $expected.locator = if ($PackageSource -ceq 'Registry') { $package.version } else { $dependencies[$package.id] }; $expected.dependencies = $package.dependencies }
        elseif (-not $git) { $expected.root = Join-Path $hostRecord.path $package.relative }
        $expected
    })
    $output = Join-Path $run.path 'Compile'; $null = [IO.Directory]::CreateDirectory($output)
    $requestPath = Join-Path $output 'request.json'
    $request = @{
        schemaVersion = 1; purpose = 'YLTesting.IntegrationPackages'; integration = $Integration; hostId = $hostRecord.id; resultId = $run.id
        workspaceId = $workspace.id; resultRoot = $run.path; output = $output; unityVersion = $editor.version; inputHash = $identity.sha256
        manifestSha256 = (Get-FileHash -LiteralPath (Join-Path $hostRecord.path 'Packages/manifest.json') -Algorithm SHA256).Hash
        packages = $expectedPackages; assemblies = $definition.assemblies; sdk = $sdk
        packageOrigin = $source.packageOrigin; gitRepository = $source.gitRepository; gitRevision = $source.gitRevision
        cacheRoot = $cache.path; cacheId = $cache.id; expectedTemplateHash = $templateIdentity.sha256
        dotweenSdkMode = $DOTweenSdkMode
    }
    if ($artifact) { foreach ($field in (Get-YLRegistryRequestFields -Source $source -EntryPackage $definition.id).GetEnumerator()) { $request[$field.Key] = $field.Value } }
    # The request needs approved SDK identities, not the source project's physical SDK path.
    if ($Integration -ceq 'DOTween' -and $DOTweenSdkMode -ceq 'Required') { $request.sdk = @{ version = $sdk.version; assemblySha256 = $sdk.assemblySha256 } }
    Write-YLTestJson -Path $requestPath -Value $request
    $summary.report = Join-Path $output 'report.json'
    $start = [Diagnostics.ProcessStartInfo]::new($Unity)
    $start.UseShellExecute = $false; $start.WorkingDirectory = $hostRecord.path
    foreach ($key in @($start.Environment.Keys | Where-Object { $_.StartsWith('GIT_', [StringComparison]::OrdinalIgnoreCase) })) { $null = $start.Environment.Remove($key) }
    $start.Environment['UPM_CACHE_ROOT'] = Join-Path $cache.path 'Upm'
    $start.Environment['UPM_CACHE_PATH'] = Join-Path $cache.path 'Upm/packages'
    $start.Environment['UPM_NPM_CACHE_PATH'] = Join-Path $cache.path 'Upm/npm'
    $start.Environment['UPM_GIT_LFS_CACHE_PATH'] = Join-Path $cache.path 'Lfs'
    $start.Environment['UPM_ENABLE_GIT_LFS_CACHE'] = '1'
    if ($artifact) { $summary['coldCache'] = Assert-YLRegistryColdCache -StartInfo $start -CacheRoot $cache.path }
    foreach ($argument in @('-batchmode', '-nographics', '-projectPath', $hostRecord.path, '-executeMethod', 'YLFramework.Testing.IntegrationEntry.Execute',
        '-ylIntegrationRequest', $requestPath, '-logFile', (Join-Path $output 'unity.log'))) { $start.ArgumentList.Add($argument) }
    $fact = Invoke-YLTestProcess -StartInfo $start -Log (Join-Path $output 'process.log') -TimeoutMilliseconds ([long]$TimeoutSeconds * 1000)
    $summary.processes += $fact
    $report = if ([IO.File]::Exists($summary.report)) { (Read-YLIntegrationJson $summary.report).ToString() | ConvertFrom-Json -AsHashtable -DateKind String } else { $null }
    Assert-YLTestProcessSucceeded $fact
    if ($null -eq $report) { throw 'Unity exited without an actual integration compilation report.' }
    foreach ($field in @('purpose', 'integration', 'hostId', 'resultId', 'unityVersion', 'inputHash', 'packageOrigin', 'expectedTemplateHash', 'status')) {
        if ($report[$field] -isnot [string] -or $report[$field] -cne $(if ($field -ceq 'status') { if ($Integration -ceq 'DOTween' -and $DOTweenSdkMode -ceq 'Absent') { 'NegativeNoSdk' } else { 'Completed' } } else { $request[$field] })) { throw "Integration report identity/status mismatch: $field" }
    }
    if ($report['schemaVersion'] -isnot [long] -or $report['schemaVersion'] -ne 1 -or $report['pid'] -isnot [long] -or $report['pid'] -ne $fact.pid -or
        $report['isBatchMode'] -isnot [bool] -or -not $report['isBatchMode'] -or $report['scriptCompilationFailed'] -isnot [bool] -or $report['scriptCompilationFailed'] -or
        $report['assemblies'] -isnot [array] -or $report['assemblies'].Count -ne $definition.assemblies.Count -or
        $report['packages'] -isnot [array] -or $report['packages'].Count -ne $expectedPackages.Count) { throw 'Integration report lacks actual complete compilation evidence.' }
    $lockPath = Get-YLHostManagedPath $hostRecord 'Packages/packages-lock.json'
    $actualLock = (Read-YLIntegrationJson $lockPath).ToString() | ConvertFrom-Json -AsHashtable -DateKind String
    if ($artifact) {
        Assert-YLRegistryCacheObservation -Actual $report['cacheEnvironment'] -CacheRoot $cache.path
        $sdkDeps = if ($Integration -ceq 'HybridCLR') { $sdk.dependencies } else { @{} }
        $summary['resolvedGraph'] = Assert-YLRegistryResolvedGraph -Manifest (Read-YLPackageEvidenceJson (Join-Path $hostRecord.path 'Packages/manifest.json')) -Lock $actualLock -Rows $report['resolvedDependencies'] -Release $source.release -EntryPackage $definition.id -PackageSource $PackageSource -RegistryUrl $source.registryUrl -EditorVersion $editor.version -HostRoot $hostRecord.path -CacheRoot $cache.path -SdkDependencies $sdkDeps
        $summary['cacheUse'] = Get-YLRegistryCacheUse -CacheRoot $cache.path -PackageSource $PackageSource
    }
    $lockSnapshot = Join-Path $output 'packages-lock.json'
    [IO.File]::Copy($lockPath, $lockSnapshot)
    $summary['generatedLock'] = @{ path = $lockSnapshot; sha256 = (Get-FileHash -LiteralPath $lockSnapshot -Algorithm SHA256).Hash.ToLowerInvariant() }
    foreach ($record in @($hostRecord, $cache, $run)) {
        if ((Get-YLTestDirectory -Workspace $workspace -Path $record.path).id -cne $record.id) { throw 'An owned host/cache/result identity changed during the integration process.' }
    }
    $samples = Get-YLHostManagedPath $hostRecord 'Assets/Samples'
    if ([IO.Directory]::Exists($samples) -and @(Get-YLTestTreeFiles $samples).Count -ne 0) { throw 'The consumer must not default-import Samples, including non-script assets.' }
    $installedRoots = @{}
    foreach ($expected in $expectedPackages) {
        $rows = @($report['packages'] | Where-Object { $_['name'] -is [string] -and $_['name'] -ceq $expected.name })
        if ($rows.Count -ne 1) { throw 'Actual installed package closure is incomplete or duplicated.' }
        Assert-YLIntegrationInstalledPackage -Request $request -Expected $expected -Row $rows[0] -Lock $actualLock -HostRoot $hostRecord.path
        $installedRoots[$expected.name] = $rows[0]['resolvedPath']
    }
    foreach ($expected in $definition.assemblies) {
        $rows = @($report['assemblies'] | Where-Object { $_['name'] -is [string] -and $_['name'] -ceq $expected.name })
        if ($rows.Count -ne 1 -or $rows[0]['sourceFiles'] -isnot [array] -or $rows[0]['sourceFiles'].Count -eq 0 -or
            $rows[0]['loadedPath'] -isnot [string] -or $rows[0]['sha256'] -isnot [string] -or
            (Get-FileHash -LiteralPath (Get-YLHostManagedPath $hostRecord ('Library/ScriptAssemblies/' + $expected.name + '.dll')) -Algorithm SHA256).Hash -ine $rows[0]['sha256']) { throw 'A required integration assembly was silently excluded or did not compile.' }
        $package = @($expectedPackages | Where-Object { $_.name -ceq $expected.package })[0]
        $root = $installedRoots[$expected.package]
        $sources = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($sourceFile in $rows[0]['sourceFiles']) {
            if ($sourceFile -isnot [string]) { throw 'Invalid actual assembly source path.' }
            $normalized = $sourceFile.Replace('\', '/'); $prefix = 'Packages/' + $expected.package + '/'
            $physical = if ($normalized.StartsWith($prefix, [StringComparison]::Ordinal)) { Join-Path $root $normalized.Substring($prefix.Length) }
                elseif ([IO.Path]::IsPathFullyQualified($sourceFile)) { $sourceFile } else { Join-Path $hostRecord.path $sourceFile }
            $null = $sources.Add((Get-YLTestPath $physical))
        }
        $expectedSources = [string[]]@($package.files | Where-Object { $_.path.StartsWith($expected.directory + '/', [StringComparison]::Ordinal) -and $_.path.EndsWith('.cs', [StringComparison]::Ordinal) } | ForEach-Object { Join-Path $root $_.path })
        if ($expectedSources.Count -eq 0 -or -not $sources.SetEquals($expectedSources) -or
            $rows[0]['loadedPath'] -ine (Join-Path $hostRecord.path ('Library/ScriptAssemblies/' + $expected.name + '.dll'))) { throw 'Actual compiled sources/loaded assembly are outside the registered resolvedRoot or incomplete.' }
    }
    if ($Integration -ceq 'HybridCLR') {
        $locked = $actualLock['dependencies']['com.code-philosophy.hybridclr']
        if ($artifact) {
            if ($report['sdk']['version'] -cne '9.0.3' -or $report['sdk']['source'] -cne 'Registry' -or $report['sdk']['isDirectDependency'] -isnot [bool] -or $report['sdk']['isDirectDependency']) { throw 'Actual HybridCLR SDK must be the indirect Registry version.' }
            $sdkRows = @($report['resolvedDependencies'] | Where-Object { $_['name'] -ceq 'com.code-philosophy.hybridclr' })
            if ($sdkRows.Count -ne 1) { throw 'Actual SDK Registry payload is missing.' }
            Assert-YLHybridClrRegistryInstalled -Sdk $sdk -Root $sdkRows[0]['resolvedPath']
        } elseif ($locked['source'] -cne 'git' -or $locked['version'] -cne $sdk.declaration -or $locked['hash'] -cne $sdk.revision -or
            $report['sdk']['version'] -cne '9.0.3' -or $report['sdk']['source'] -cne 'Git' -or $report['sdk']['revision'] -cne $sdk.revision) { throw 'Actual HybridCLR SDK version, source or Git hash is incorrect.' }
    }
    $summary.packages = $report['packages']; $summary.assemblies = $report['assemblies']
    if ($report['sdk'] -is [Collections.IDictionary]) { $summary['sdk'] = $report['sdk'] }
    $summary.compilation = 'VerifiedUnityEditorCompilation'; $summary.playerGraph = 'VerifiedGraphOnlyNotPlayerBuild'
    if ($artifact -and $Integration -ceq 'HybridCLR') {
        . (Join-Path $PSScriptRoot '../Common/RegistryAuthoring.ps1')
        $summary['authoring'] = Invoke-YLRegistryAuthoring -Workspace $workspace -Source $source -Unity $Unity -Editor $editor -EntryPackage $definition.id -Result $run -TimeoutSeconds $TimeoutSeconds
        $summary.processes = @($summary.processes) + @($summary.authoring.processes)
        Assert-YLRegistryStageCompleted -Outcome $summary.authoring -Stage Authoring
    }
    if ($PackageSource -ceq 'Registry' -and -not ($Integration -ceq 'DOTween' -and $DOTweenSdkMode -ceq 'Absent')) {
        . (Join-Path $PSScriptRoot '../Common/RegistryLifecycle.ps1')
        $summary['lifecycle'] = Invoke-YLRegistryLifecycle -Workspace $workspace -Source $source -Unity $Unity -Editor $editor -EntryPackage $definition.id -Result $run -TimeoutSeconds $TimeoutSeconds
        $summary.processes = @($summary.processes) + @($summary.lifecycle.processes)
        Assert-YLRegistryStageCompleted -Outcome $summary.lifecycle -Stage Lifecycle
    }
    if ((Get-FileHash -LiteralPath (Join-Path $hostRecord.path 'Packages/manifest.json') -Algorithm SHA256).Hash -cne $request.manifestSha256) { throw 'The original consumer manifest changed during verification.' }
    if ($Integration -ceq 'DOTween' -and $DOTweenSdkMode -ceq 'Absent') {
        $summary['integrationResult'] = 'NegativeNoSdk'; $summary.scope = 'Package installation and absent SDK compilation gate only. NOT positive DOTween integration.'
        $summary.status = 'NegativeNoSdk'
    } else { $summary.status = 'Completed' }
} catch {
    $summary.reason = $_.Exception.Message
    if ($report -is [Collections.IDictionary] -and $report['error'] -is [string]) { $summary.reason += ' Unity report: ' + $report['error'] }
    $summary.status = if ($summary.reason.StartsWith('PrerequisiteNotMet:', [StringComparison]::Ordinal)) { 'PrerequisiteNotMet' } else { 'Failed' }
} finally {
    try {
        if ($null -ne $identity) {
            try {
                if ($artifact) {
                    if ((Read-YLRegistryRelease -Path $source.release.path -VerifyTarballs:($PackageSource -ceq 'LocalTarball')).sha256 -cne $source.release.sha256) { throw 'Frozen release manifest changed during verification.' }
                    foreach ($package in $products) { if ((Get-YLIntegrationPackageContent -Root $package.root -PackageId $package.id -NormalizeManifest).sha256 -cne $inventories[$package.id].sha256) { throw 'Frozen expected artifact payload drifted.' } }
                    $fresh = $inputPaths
                } else {
                $currentPackages = @(Get-YLUpmPackages -Repository $repository -Ids @($definition.id))
                $freshPackagePaths = @(Get-YLIntegrationPackageFiles -Repository $repository -Packages $currentPackages)
                $fresh = if ($git) { $inputPaths } else { @($inputPaths | Where-Object { -not $_.StartsWith('PackageSources/', [StringComparison]::Ordinal) }) + $freshPackagePaths }
                $currentTemplate = Get-YLTestInputIdentity -Repository $repository -Paths $freshPackagePaths -Profile 'LocalReleaseExpectedTemplateNotInstallSource'
                if ($currentTemplate.sha256 -cne $templateIdentity.sha256) { throw 'The explicit local release expected template drifted; Git input identity is not replaced by this template.' }
                }
                if ($Integration -ceq 'DOTween' -and $DOTweenSdkMode -ceq 'Required') {
                    $currentSdk = Get-YLIntegrationDotweenSdk -Repository $sdkRepository -Archive $DOTweenArchive -OwnedSdkRoot $sdk.root
                }
                $recheck = Get-YLTestInputIdentity -Repository $repository -Paths @($fresh | Sort-Object -Unique) -Toolchain $toolchain -Profile $profile
                $summary['sourceRecheck'] = @{ before = $identity.sha256; after = $recheck.sha256; unchanged = ($identity.sha256 -ceq $recheck.sha256) }
                if ($identity.sha256 -cne $recheck.sha256) { throw 'Source inputs drifted during integration verification; this result is invalid.' }
            } catch { $summary.status = 'Failed'; $summary.reason = [string]$summary.reason + ' Source recheck: ' + $_.Exception.Message }
        }
        $summary.endUtc = [DateTime]::UtcNow.ToString('o')
        Write-YLTestJson -Path (Join-Path $run.path 'summary.json') -Value $summary
    } finally {
        if ($null -ne $hostLease) { $hostLease.Dispose() }
        if ($null -ne $cacheLease) { $cacheLease.Dispose() }
        $lease.Dispose()
    }
    $null = Invoke-YLTestRotation -Workspace $workspace -Key $run.key
    "Integration evidence: $($run.path)"
    "$($summary.status): $Integration; $($summary.compilation); $($summary.reason)"
}
if ($summary.status -ceq 'PrerequisiteNotMet') { exit 2 }
if ($summary.status -cnotin @('Completed', 'NegativeNoSdk')) { exit 1 }
