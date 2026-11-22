#requires -Version 7.6
#requires -PSEdition Core
. (Join-Path $PSScriptRoot 'Packages.ps1')

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

function Get-YLIntegrationPackageContent([string] $Root, [string] $PackageId, [switch] $NormalizeManifest, [switch] $AllowManagedFingerprint,
    [ValidateSet('Git', 'Registry', 'LocalTarball')][string] $FingerprintSource = 'Git') {
    $Root = Get-YLTestPath $Root
    $metadata = Read-YLIntegrationJson (Join-Path $Root 'package.json')
    if ($null -eq $metadata['name'] -or $metadata['name'].Type -ne [Newtonsoft.Json.Linq.JTokenType]::String -or [string]$metadata['name'] -cne $PackageId -or
        $null -eq $metadata['version'] -or $metadata['version'].Type -ne [Newtonsoft.Json.Linq.JTokenType]::String -or [string]$metadata['version'] -cnotmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Invalid integration package manifest identity.' }
    $files = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($relative in @(Get-YLIntegrationPackageFiles -Repository $Root -Packages @(@{ id = $PackageId; root = $Root; version = [string]$metadata['version'] }))) {
        $path = Join-Path $Root $relative
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($relative -ceq 'package.json') {
            $manifest = Read-YLIntegrationJson $path
            $fingerprint = $manifest.Property('_fingerprint')
            if ($null -ne $fingerprint) {
                if (-not $NormalizeManifest -or -not $AllowManagedFingerprint -or $fingerprint.Value.Type -ne [Newtonsoft.Json.Linq.JTokenType]::String -or
                    [string]$fingerprint.Value -cnotmatch $(if ($FingerprintSource -ceq 'LocalTarball') { '\A[0-9a-fA-F]{32}\z' } else { '\A[0-9a-fA-F]{40}\z' })) { throw 'Untrusted package-manager fingerprint metadata.' }
                $fingerprint.Remove()
            }
            if ($NormalizeManifest) {
                $json = (ConvertTo-YLPackageCanonicalToken $manifest).ToString([Newtonsoft.Json.Formatting]::None)
                $hash = Get-YLPackageManifestHash $json
            }
        }
        $files.Add($relative, @{ path = $relative; sha256 = $hash })
    }
    if (-not $files.ContainsKey('package.json')) { throw 'The package manifest is missing.' }
    $text = [Text.StringBuilder]::new()
    foreach ($item in $files.GetEnumerator()) { $null = $text.Append($item.Key).Append([char]0).Append($item.Value.sha256).Append("`n") }
    return @{ files = @($files.Values); sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text.ToString()))).ToLowerInvariant() }
}

function Get-YLIntegrationPackageFiles([string] $Repository, [object[]] $Packages) {
    foreach ($package in $Packages) {
        if ($package.version -isnot [string] -or $package.version -cnotmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Integration requires an exact package version.' }
        $license = Join-Path $package.root 'LICENSE.md'
        if (-not [IO.File]::Exists($license) -or [IO.FileInfo]::new($license).Length -eq 0) { throw "Package license is missing: $($package.id)" }
        foreach ($path in @(Get-YLTestTreeFiles $package.root)) {
            $relative = [IO.Path]::GetRelativePath($package.root, $path).Replace('\', '/')
            if ($relative -match '(?i)(?:^|/)(?:SDK|SDKs|ThirdPartySdk|Demigiant)(?:/|$)|(?:^|/)(?:DOTween[^/]*\.dll|HybridCLR[^/]*\.dll|dnlib\.dll)$') { throw "External SDK payload is forbidden: $relative" }
            if ($relative -match '^(?:Assets|Packages|ProjectSettings|Library|Temp|Tests|Tools|\.git|\.kilo|\.kilocode|\.opencode|obj|bin)(?:/|$)|(?:^|/)(?:AGENTS\.md|\.yl-[^/]+|[^/]+\.(?:csproj|sln|slnx))$' -or
                $relative -match '(?i)(?:IvanMurzak|UnityMcp|ReflectorNet)') { throw "Unauthorized product package input: $relative" }
            $stream = [IO.File]::OpenRead($path)
            try {
                $prefix = [byte[]]::new(200); $length = $stream.Read($prefix, 0, $prefix.Length)
                if ([Text.Encoding]::UTF8.GetString($prefix, 0, $length).StartsWith('version https://git-lfs.github.com/spec/v1', [StringComparison]::Ordinal)) { throw "Unmaterialized LFS input: $relative" }
                if ($relative.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase) -and
                    ($length -lt 2 -or $prefix[0] -ne 77 -or $prefix[1] -ne 90 -or $package.id -ceq 'com.kano.ylframework.hybridclr')) { throw "Unapproved package DLL: $($package.id)/$relative" }
            } finally { $stream.Dispose() }
            [IO.Path]::GetRelativePath($Repository, $path).Replace('\', '/')
        }
    }
}
