#requires -Version 7.6
#requires -PSEdition Core
. (Join-Path $PSScriptRoot 'Workspace.ps1')

function Read-YLPackageCatalog {
    param([string] $Path = ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../Packaging/PackageCatalog.json'))))
    $catalog = Read-YLPackageEvidenceJson $Path
    Assert-YLPackageFields $catalog @('schemaVersion', 'packages')
    if ($catalog['schemaVersion'] -isnot [long] -or $catalog['schemaVersion'] -ne 1 -or
        $catalog['packages'] -isnot [array] -or $catalog['packages'].Count -eq 0) { throw 'Invalid package catalog schema.' }
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $assemblies = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($package in $catalog['packages']) {
        Assert-YLPackageFields $package @('id', 'assemblies', 'requiredFiles', 'samples')
        if ($package['id'] -isnot [string] -or $package['id'] -cnotmatch '^com\.kano\.ylframework(?:\.[a-z0-9]+(?:-[a-z0-9]+)*)?$' -or
            -not $ids.Add($package['id'])) { throw 'Invalid/duplicate package catalog identity.' }
    }
    if (-not $ids.Contains('com.kano.ylframework')) { throw 'The package catalog must include the main package.' }
    foreach ($package in $catalog['packages']) {
        if ($package['assemblies'] -isnot [array] -or $package['assemblies'].Count -eq 0) { throw 'Catalog assemblies must be explicit and nonempty.' }
        $directories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($assembly in $package['assemblies']) {
            Assert-YLPackageFields $assembly @('name', 'path', 'player', 'defineConstraints', 'versionDefines')
            if ($assembly['name'] -isnot [string] -or $assembly['name'] -cnotmatch '^YLFramework\.[A-Za-z0-9_.]+$' -or
                -not $assemblies.Add($assembly['name']) -or $assembly['player'] -isnot [bool]) { throw 'Invalid/duplicate catalog assembly name/player policy.' }
            $path = $assembly['path']
            Assert-YLPackageCatalogPath $path
            if (-not $path.EndsWith('/' + $assembly['name'] + '.asmdef', [StringComparison]::Ordinal) -or
                $path -cnotmatch '^(?:Runtime|Editor|Samples~)/' -or
                -not $directories.Add($path.Substring(0, $path.LastIndexOf('/')))) { throw 'Invalid/duplicate catalog assembly path.' }
            if ($assembly['defineConstraints'] -isnot [array] -or $assembly['versionDefines'] -isnot [array]) { throw 'Catalog assembly conditions must be arrays.' }
            $defines = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($define in $assembly['defineConstraints']) {
                if ($define -isnot [string] -or $define -cnotmatch '^[A-Z][A-Z0-9_]*$' -or -not $defines.Add($define)) { throw 'Unsupported/duplicate catalog define constraint.' }
            }
            $versionDefines = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($version in $assembly['versionDefines']) {
                Assert-YLPackageFields $version @('name', 'expression', 'define')
                if ($version['name'] -isnot [string] -or (-not $ids.Contains($version['name']) -and $version['name'] -cne 'com.code-philosophy.hybridclr') -or
                    $version['expression'] -isnot [string] -or $version['expression'] -cnotin @('0.0.0', '[9.0.3,9.0.4)') -or
                    $version['define'] -isnot [string] -or -not $defines.Contains($version['define']) -or -not $versionDefines.Add($version['define'])) {
                    throw 'Unsupported/unknown package catalog version define.'
                }
            }
            if (@($defines | Where-Object { $_ -cne 'DOTWEEN' -and -not $versionDefines.Contains($_) }).Count -ne 0) { throw 'A catalog define has no explicit supported prerequisite/profile.' }
        }
        foreach ($field in @('requiredFiles', 'samples')) {
            if ($package[$field] -isnot [array]) { throw "Catalog $field must be an array." }
            $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($path in $package[$field]) {
                Assert-YLPackageCatalogPath $path
                if (-not $paths.Add($path) -or ($field -ceq 'samples' -and -not $path.StartsWith('Samples~/', [StringComparison]::Ordinal))) { throw "Invalid/duplicate catalog $field path." }
            }
        }
    }
    return ,$catalog
}

function Assert-YLPackageCatalogPath {
    param([object] $Path)
    if ($Path -isnot [string] -or -not $Path -or $Path -match '^[\/]|[\\:\x00-\x1f<>"|?*]|(?:^|/)\.\.?(?:/|$)' -or
        @($Path.Split('/') | Where-Object { -not $_ -or $_.EndsWith('.') -or $_.EndsWith(' ') -or $_ -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)' }).Count -ne 0) {
        throw 'Invalid package catalog path.'
    }
}

function Get-YLPackageAssemblyPlan {
    param([Collections.IDictionary] $Catalog, [object[]] $Packages, [string] $Profile, [string] $DOTweenSdkMode, [switch] $MetadataOnly)
    if ($null -eq ('YLFramework.Testing.PackageCatalogContract' -as [type])) {
        $references = @([IO.Directory]::EnumerateFiles((Join-Path $PSHOME 'ref'), '*.dll')) + @([Newtonsoft.Json.Linq.JObject].Assembly.Location)
        Add-Type -Path (Join-Path $PSScriptRoot '../PackageCatalogContract.cs') -ReferencedAssemblies $references -CompilerOptions @('/langversion:9.0', '/nowarn:1701')
    }
    $catalogJson = [Newtonsoft.Json.Linq.JObject]::Parse((ConvertTo-Json -InputObject $Catalog -Depth 40))
    $packageJson = [Newtonsoft.Json.Linq.JArray]::Parse((ConvertTo-Json -InputObject @($Packages) -Depth 40))
    if ($MetadataOnly) { $plan = [YLFramework.Testing.PackageCatalogContract]::Definitions($catalogJson, $packageJson, $Profile, $DOTweenSdkMode) }
    else { $plan = [YLFramework.Testing.PackageCatalogContract]::Plan($catalogJson, $packageJson, $Profile, $DOTweenSdkMode) }
    return (ConvertFrom-Json -InputObject $plan.ToString() -AsHashtable -NoEnumerate)
}

function Get-YLIntegrationDotweenSdk([string] $Repository, [string] $Archive, [string] $OwnedSdkRoot) {
    try {
        $approval = [IO.File]::ReadAllText((Join-Path $Repository 'PackageSources/com.kano.ylframework/Runtime/Systems/Action/Integration/DOTween/Dependencies.json')) | ConvertFrom-Json -AsHashtable -DateKind String
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

function Get-YLInstalledPackageReference {
    param([string] $Repository, [string] $PackageId, [string] $RelativePath, [string] $UnityVersion)
    $Repository = Get-YLTestPath -Path $Repository -ReadOnly
    $description = "Prerequisite not met: installed package $PackageId/$RelativePath"
    if ($PackageId -cnotmatch '^[a-z0-9]+(?:\.[a-z0-9]+(?:-[a-z0-9]+)*)+$') { throw "$description; invalid package identity." }
    Assert-YLPackageCatalogPath $RelativePath
    if ([IO.Path]::GetExtension($RelativePath) -ine '.dll') { throw "$description; an explicit managed DLL is required." }
    $manifestPath = Join-Path $Repository 'Packages/manifest.json'
    $lockPath = Join-Path $Repository 'Packages/packages-lock.json'
    $resolutionPath = Join-Path $Repository 'Library/PackageManager/projectResolution.json'
    foreach ($path in @($manifestPath, $lockPath, $resolutionPath)) { if (-not [IO.File]::Exists($path)) { throw "$description; missing actual UPM resolution declaration: $path" } }
    $manifest = Read-YLPackageEvidenceJson $manifestPath
    $lock = Read-YLPackageEvidenceJson $lockPath
    $resolution = Read-YLPackageEvidenceJson $resolutionPath
    $declared = $manifest['dependencies']; $locked = $lock['dependencies']; $context = $resolution['context']; $outputs = $resolution['outputs']
    if ($declared -isnot [Collections.IDictionary] -or $locked -isnot [Collections.IDictionary] -or $context -isnot [Collections.IDictionary] -or
        $outputs -isnot [Collections.IDictionary] -or $declared[$PackageId] -isnot [string] -or $locked[$PackageId] -isnot [Collections.IDictionary]) { throw "$description; package is not declared and resolved." }
    $version = $locked[$PackageId]['version']
    if ($version -isnot [string] -or $version -cnotmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$' -or
        $declared[$PackageId] -cne $version -or $locked[$PackageId]['source'] -cne 'registry') { throw "$description; exact registry version differs from the lock." }
    if ($context['unityVersion'] -cne $UnityVersion -or $context['projectPath'] -isnot [string] -or
        (Get-YLTestPath -Path $context['projectPath'] -ReadOnly) -ine (Join-Path $Repository 'Packages')) { throw "$description; Editor/project resolution context differs." }
    $resolved = $outputs["$PackageId@$version"]
    if ($resolved -isnot [Collections.IDictionary] -or $resolved['name'] -cne $PackageId -or $resolved['version'] -cne $version -or
        $resolved['source'] -cne 'registry' -or $resolved['resolvedPath'] -isnot [string]) { throw "$description; the exact UPM resolution member differs." }
    $packageRoot = Get-YLTestPath -Path $resolved['resolvedPath'] -ReadOnly
    $cache = (Get-YLTestPath -Path (Join-Path $Repository 'Library/PackageCache') -ReadOnly).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $packageRoot.StartsWith($cache, [StringComparison]::OrdinalIgnoreCase)) { throw "$description; resolved package root escapes the project cache." }
    $packagePath = Join-Path $packageRoot 'package.json'
    $path = Get-YLTestPath -Path (Join-Path $packageRoot $RelativePath) -ReadOnly
    foreach ($file in @($packagePath, $path)) {
        if (-not [IO.File]::Exists($file)) { throw "$description; missing resolved package file: $file" }
        for ($parent = $file; $parent; $parent = [IO.Path]::GetDirectoryName($parent)) {
            if (([IO.File]::GetAttributes($parent) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "$description; linked package path is refused." }
        }
    }
    $package = Read-YLPackageEvidenceJson $packagePath
    if ($package['name'] -cne $PackageId -or $package['version'] -cne $version -or
        [Reflection.AssemblyName]::GetAssemblyName($path).Name -cne [IO.Path]::GetFileNameWithoutExtension($path)) { throw "$description; actual package/DLL identity differs." }
    return [pscustomobject]@{ path = $path; evidence = @($manifestPath, $lockPath, $resolutionPath, $packagePath) }
}

function Get-YLUpmPackages {
    param([string] $Repository, [string[]] $Ids = @('com.kano.ylframework'), [Collections.IDictionary] $Catalog = (Read-YLPackageCatalog))
    $known = @($Catalog['packages'] | ForEach-Object { $_['id'] })
    $pending = [Collections.Generic.Queue[string]]::new()
    foreach ($id in $Ids) { $pending.Enqueue($id) }
    $result = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    while ($pending.Count -gt 0) {
        $id = $pending.Dequeue()
        if ($id -cnotin $known) { throw "Unknown YL package input: $id" }
        if ($result.ContainsKey($id)) { continue }
        $relative = "PackageSources/$id"
        $root = Get-YLTestPath (Join-Path $Repository $relative)
        $manifest = Read-YLPackageEvidenceJson (Join-Path $root 'package.json')
        if ($manifest['name'] -isnot [string] -or $manifest['name'] -cne $id -or $manifest['version'] -isnot [string] -or $manifest['version'] -cnotmatch '^\d+\.\d+\.\d+(?:-[\w.-]+)?$') { throw "Invalid package identity: $id" }
        if ($manifest['dependencies'] -isnot [Collections.IDictionary]) { throw "Invalid package dependencies: $id" }
        $result.Add($id, [pscustomobject]@{ id = $id; version = $manifest['version']; root = $root; relative = $relative; manifest = $manifest })
        foreach ($dependency in $manifest['dependencies'].GetEnumerator()) {
            if ($dependency.Key.StartsWith('com.kano.ylframework', [StringComparison]::Ordinal)) {
                if ($id -ceq 'com.kano.ylframework') { throw 'The main package cannot depend on an optional package.' }
                if ($dependency.Value -isnot [string] -or $dependency.Value -cne $manifest['version']) { throw "Mismatched internal package version: $id -> $($dependency.Key)" }
                $pending.Enqueue($dependency.Key)
            }
        }
    }
    return @($result.Values)
}

function Add-YLUpmDependencies {
    param([Collections.IDictionary] $Manifest, [object[]] $Packages)
    foreach ($package in $Packages) {
        $Manifest['dependencies'][$package.id] = 'file:' + $package.root.Replace('\', '/')
    }
}

function Read-YLPackageEvidenceJson {
    param([string] $Path)
    $Path = Get-YLTestPath $Path
    if ([IO.FileInfo]::new($Path).Length -gt 16MB) { throw 'Package evidence JSON exceeds the size limit.' }
    $text = [Text.UTF8Encoding]::new($false, $true).GetString([IO.File]::ReadAllBytes($Path))
    $document = [Text.Json.JsonDocument]::Parse($text)
    function Check-Properties([Text.Json.JsonElement] $Element) {
        if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
            $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($property in $Element.EnumerateObject()) {
                if (-not $names.Add($property.Name)) { throw "Duplicate JSON property: $($property.Name)" }
                Check-Properties $property.Value
            }
        } elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
            foreach ($item in $Element.EnumerateArray()) { Check-Properties $item }
        }
    }
    try {
        if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw 'Package evidence JSON must be an object.' }
        Check-Properties $document.RootElement
    } finally { $document.Dispose() }
    return ,(ConvertFrom-Json -InputObject $text -AsHashtable -NoEnumerate -DateKind String)
}

function Assert-YLPackageFields {
    param([object] $Value, [string[]] $Fields)
    if ($Value -isnot [Collections.IDictionary] -or $Value.Count -ne $Fields.Count -or
        @($Value.Keys | Where-Object { $_ -cnotin $Fields }).Count -ne 0) { throw 'Invalid package evidence fields.' }
}

function Assert-YLPackageDependencies {
    param([object] $Actual, [object] $Expected)
    if ($Actual -isnot [Collections.IDictionary] -or $Expected -isnot [Collections.IDictionary] -or $Actual.Count -ne $Expected.Count) { throw 'Package dependency graph declarations differ.' }
    foreach ($entry in $Expected.GetEnumerator()) {
        if ($Actual[$entry.Key] -isnot [string] -or $entry.Value -isnot [string] -or $Actual[$entry.Key] -cne $entry.Value) { throw "Package dependency declaration differs: $($entry.Key)" }
    }
}

function Read-YLRegistryRelease {
    param([string] $Path, [switch] $VerifyTarballs, [Collections.IDictionary] $Catalog = (Read-YLPackageCatalog),
        [string] $PackageRoot = ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../PackageSources'))))
    $Path = Get-YLTestPath $Path
    $manifest = Read-YLPackageEvidenceJson $Path
    Assert-YLPackageFields $manifest @('schemaVersion', 'sourceCommit', 'toolchain', 'producer', 'packages')
    if ($manifest['schemaVersion'] -isnot [long] -or $manifest['schemaVersion'] -ne 1 -or $manifest['sourceCommit'] -isnot [string] -or
        $manifest['sourceCommit'] -cnotmatch '^[0-9a-f]{40}$') { throw 'Invalid release manifest schema/source identity.' }
    $tools = $manifest['toolchain']; Assert-YLPackageFields $tools @('node', 'npm', 'powershell')
    if ($tools['node'] -isnot [string] -or $tools['npm'] -isnot [string] -or $tools['node'] -cne '22.23.3' -or $tools['npm'] -cne '10.9.9' -or $tools['powershell'] -isnot [string] -or $tools['powershell'] -cnotmatch '^7\.6\.[0-9]+$') { throw 'Invalid release toolchain.' }
    $producer = $manifest['producer']
    if ($producer -isnot [Collections.IDictionary] -or $producer['kind'] -isnot [string]) { throw 'Invalid release producer identity.' }
    if ($producer['kind'] -ceq 'Local') { Assert-YLPackageFields $producer @('kind') }
    elseif ($producer['kind'] -ceq 'GitHubActions') {
        Assert-YLPackageFields $producer @('kind', 'repository', 'workflow', 'runId', 'runAttempt')
        if ($producer['repository'] -isnot [string] -or $producer['repository'] -cne 'KONOK-A/YLFramework-dev' -or $producer['workflow'] -isnot [string] -or -not $producer['workflow'] -or
            $producer['runId'] -isnot [string] -or $producer['runId'] -cnotmatch '^[1-9][0-9]*$' -or
            $producer['runAttempt'] -isnot [string] -or $producer['runAttempt'] -cnotmatch '^[1-9][0-9]*$') { throw 'Invalid release CI producer identity.' }
    } else { throw 'Unknown release producer kind.' }
    $ids = @($Catalog['packages'] | ForEach-Object { $_['id'] })
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    if ($manifest['packages'] -isnot [array] -or $manifest['packages'].Count -ne $ids.Count) { throw 'Release manifest requires the complete catalog package set.' }
    $packages = @(foreach ($row in $manifest['packages']) {
        Assert-YLPackageFields $row @('id', 'version', 'dependencies', 'assetName', 'sha256', 'integrity', 'payloadSha256', 'files')
        $id = $row['id']; $version = $row['version']
        if ($id -isnot [string] -or $id -cnotin $ids -or -not $seen.Add($id) -or $version -isnot [string] -or $version -cnotmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$' -or
            $row['assetName'] -isnot [string] -or $row['assetName'] -cne "$id-$version.tgz") { throw 'Invalid release package identity/version/asset name.' }
        foreach ($field in @('sha256', 'payloadSha256')) { if ($row[$field] -isnot [string] -or $row[$field] -cnotmatch '^[0-9a-f]{64}$') { throw "Invalid release package digest: $field" } }
        if ($row['integrity'] -isnot [string] -or $row['integrity'] -cnotmatch '^sha512-[A-Za-z0-9+/]{86}==$') { throw 'Invalid release package integrity.' }
        $deps = $row['dependencies']; $internal = @()
        $sourceManifest = Read-YLPackageEvidenceJson (Join-Path $PackageRoot "$id/package.json")
        if ($sourceManifest['name'] -isnot [string] -or $sourceManifest['name'] -cne $id -or
            $sourceManifest['dependencies'] -isnot [Collections.IDictionary]) { throw 'Invalid maintained release package declaration.' }
        $internal = @($sourceManifest['dependencies'].Keys | Where-Object { $_.StartsWith('com.kano.', [StringComparison]::Ordinal) })
        if (($id -ceq 'com.kano.ylframework' -and $internal.Count -ne 0) -or
            @($internal | Where-Object { $_ -cnotin $ids -or $_ -ceq $id }).Count -ne 0) { throw 'Invalid release internal dependency direction.' }
        if ($deps -isnot [Collections.IDictionary]) { throw 'Invalid release package dependencies.' }
        foreach ($entry in $deps.GetEnumerator()) {
            if ($entry.Value -isnot [string] -or $entry.Value -cnotmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$' -or
                ($entry.Key.StartsWith('com.kano.', [StringComparison]::Ordinal) -and ($entry.Key -cnotin $internal -or $entry.Value -cne $version)) -or
                (-not $entry.Key.StartsWith('com.unity.', [StringComparison]::Ordinal) -and $entry.Key -cnotin $internal -and
                    -not ($id -ceq 'com.kano.ylframework.hybridclr' -and $entry.Key -ceq 'com.code-philosophy.hybridclr' -and $entry.Value -ceq '9.0.3'))) { throw 'Invalid release internal dependency/version.' }
        }
        foreach ($dependency in $internal) { if ($deps[$dependency] -cne $version) { throw 'Missing release internal dependency.' } }
        if ($id -ceq 'com.kano.ylframework.hybridclr' -and $deps['com.code-philosophy.hybridclr'] -cne '9.0.3') { throw 'Missing release SDK dependency.' }
        if ($row['files'] -isnot [array] -or $row['files'].Count -eq 0) { throw 'Release package files are required.' }
        $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $content = [Text.StringBuilder]::new(); $previous = $null
        foreach ($file in $row['files']) {
            Assert-YLPackageFields $file @('path', 'sha256', 'contentSha256')
            $relative = $file['path']
            if ($relative -isnot [string] -or -not $relative -or $relative -match '^[\/]|[\\:\x00-\x1f<>"|?*]|(?:^|/)\.\.?(?:/|$)' -or
                @($relative.Split('/') | Where-Object { -not $_ -or $_.EndsWith('.') -or $_.EndsWith(' ') -or $_ -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)' }).Count -ne 0 -or
                -not $paths.Add($relative) -or ($null -ne $previous -and [StringComparer]::Ordinal.Compare($previous, $relative) -ge 0)) { throw 'Invalid/duplicate/unsorted release package file path.' }
            foreach ($field in @('sha256', 'contentSha256')) { if ($file[$field] -isnot [string] -or $file[$field] -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid release file digest.' } }
            if ($relative -cne 'package.json' -and $file['sha256'] -cne $file['contentSha256']) { throw 'Only package.json may have a semantic content digest.' }
            $null = $content.Append($relative).Append([char]0).Append($file['contentSha256']).Append("`n"); $previous = $relative
        }
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($content.ToString()))).ToLowerInvariant()
        if (-not $paths.Contains('package.json') -or $hash -cne $row['payloadSha256']) { throw 'Release payload digest does not bind the file inventory.' }
        $row['path'] = Join-Path ([IO.Path]::GetDirectoryName($Path)) $row['assetName']
        $row
    })
    if (@($packages.version | Sort-Object -Unique -CaseSensitive).Count -ne 1) { throw 'Release package versions must match.' }
    $release = @{ path = $Path; sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant(); sourceCommit = $manifest['sourceCommit']; producer = $producer; packages = $packages }
    if ($VerifyTarballs) {
        . (Join-Path $PSScriptRoot '../../Packaging/Build-YLRegistryPackages.ps1')
        foreach ($package in $packages) {
            if (-not [IO.File]::Exists($package.path)) { throw "Missing release tarball: $($package.assetName)" }
            $payload = Read-YLRegistryTarball -Path $package.path -Catalog $Catalog
            Assert-YLRegistryPayloadReceipt -Payload $payload -Expected $package
        }
    }
    return $release
}

function Assert-YLRegistryPayloadReceipt {
    param([object] $Payload, [Collections.IDictionary] $Expected)
    if ($Payload.id -cne $Expected.id -or $Payload.version -cne $Expected.version -or $Payload.sha256 -cne $Expected.sha256 -or
        $Payload.integrity -cne $Expected.integrity -or $Payload.payloadSha256 -cne $Expected.payloadSha256 -or $Payload.files.Count -ne $Expected.files.Count) { throw 'Release tarball identity/integrity/payload differs from the frozen receipt.' }
    Assert-YLPackageDependencies $Payload.dependencies $Expected.dependencies
    for ($i = 0; $i -lt $Payload.files.Count; $i++) {
        foreach ($field in @('path', 'sha256', 'contentSha256')) { if ($Payload.files[$i][$field] -cne $Expected.files[$i][$field]) { throw 'Release tarball file inventory differs from the frozen receipt.' } }
    }
}

function Get-YLRegistrySource {
    param([string] $PackageSource, [Collections.IDictionary] $Arguments)
    $registry = $PackageSource -ceq 'Registry'; $tarball = $PackageSource -ceq 'LocalTarball'
    if (-not $registry -and -not $tarball) {
        foreach ($name in @('RegistryUrl', 'RegistryVersion', 'ReleaseManifest')) { if ($Arguments.Keys -ccontains $name) { throw "$PackageSource source refuses $name." } }
        return $null
    }
    foreach ($name in @('GitRepository', 'GitRevision')) { if ($Arguments.Keys -ccontains $name) { throw "$PackageSource source refuses Git parameters." } }
    if ($Arguments.Keys -cnotcontains 'ReleaseManifest' -or [string]::IsNullOrWhiteSpace($Arguments['ReleaseManifest'])) { throw "$PackageSource requires ReleaseManifest." }
    if ($registry) {
        if ($Arguments.Keys -cnotcontains 'RegistryUrl' -or $Arguments['RegistryUrl'] -isnot [string] -or $Arguments['RegistryUrl'] -cne 'https://package.openupm.com') { throw 'RegistryUrl must explicitly select the approved OpenUPM HTTPS endpoint.' }
        if ($Arguments.Keys -cnotcontains 'RegistryVersion' -or $Arguments['RegistryVersion'] -isnot [string] -or $Arguments['RegistryVersion'] -cne '0.1.0-preview.1') { throw 'RegistryVersion must explicitly select 0.1.0-preview.1.' }
    } else {
        foreach ($name in @('RegistryUrl', 'RegistryVersion')) { if ($Arguments.Keys -ccontains $name) { throw "LocalTarball source refuses $name; it is Registry only." } }
    }
    $release = Read-YLRegistryRelease -Path $Arguments['ReleaseManifest']
    if ($registry -and $release.producer['kind'] -cne 'GitHubActions') { throw 'Registry acceptance requires a CI producer receipt, not a Local release candidate.' }
    if ($registry -and @($release.packages | Where-Object { $_.version -cne $Arguments['RegistryVersion'] }).Count -ne 0) { throw 'RegistryVersion differs from the frozen release packages.' }
    return @{ packageOrigin = $PackageSource; registryUrl = 'https://package.openupm.com'; registryVersion = $release.packages[0].version; release = $release; gitRepository = $null; gitRevision = $null }
}

function Assert-YLPackageStringArray {
    param([object] $Actual, [string[]] $Expected, [string] $Label)
    if ($Actual -isnot [array] -or $Actual.Count -ne $Expected.Count) { throw "Exact string array length/type mismatch: $Label" }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if ($Actual[$index] -isnot [string] -or -not [string]::Equals($Actual[$index], $Expected[$index], [StringComparison]::Ordinal)) {
            throw "Exact string array value/type mismatch: $Label"
        }
    }
}

function Get-YLRegistryRequestFields {
    param([Collections.IDictionary] $Source, [string] $EntryPackage, [string[]] $EntryPackages = @($EntryPackage))
    $roots = @(Get-YLRegistryEntryPackages -EntryPackage $EntryPackage -EntryPackages $EntryPackages)
    if ($Source['packageOrigin'] -cnotin @('Registry', 'LocalTarball')) { throw 'Invalid artifact request source/entry.' }
    $registry = $Source['packageOrigin'] -ceq 'Registry'
    return @{ entryPackage = $EntryPackage; entryPackages = $roots; releaseManifestSha256 = $Source.release.sha256; gitRepository = $null; gitRevision = $null
        registryUrl = $(if ($registry) { $Source.registryUrl } else { $null }); registryVersion = $(if ($registry) { $Source.registryVersion } else { $null }) }
}

function Get-YLRegistryEntryPackages {
    param([string] $EntryPackage, [string[]] $EntryPackages = @($EntryPackage))
    $known = @((Read-YLPackageCatalog).packages.id)
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    if ($null -eq $EntryPackages -or $EntryPackages.Count -eq 0 -or $EntryPackage -cne $EntryPackages[0]) { throw 'Artifact roots require a nonempty list with primary entry first.' }
    foreach ($id in $EntryPackages) {
        if ([string]::IsNullOrWhiteSpace($id) -or $id -cnotin $known -or -not $seen.Add($id)) { throw 'Invalid/unknown/duplicate artifact entry roots.' }
    }
    if ($EntryPackages.Count -gt 1 -and ($EntryPackages.Count -ne 2 -or -not $seen.SetEquals([string[]]@('com.kano.ylframework.excel', 'com.kano.ylframework.gameplay')))) { throw 'Only the explicit ExcelGameplay composite roots are supported.' }
    return $EntryPackages
}

function Assert-YLRegistryStageCompleted {
    param([object] $Outcome, [ValidateSet('Authoring', 'Lifecycle')][string] $Stage)
    if ($Outcome -is [Collections.IDictionary]) { $status = $Outcome['status']; $stageError = $Outcome['error'] }
    elseif ($null -ne $Outcome -and $null -ne $Outcome.PSObject.Properties['status'] -and $null -ne $Outcome.PSObject.Properties['error']) { $status = $Outcome.status; $stageError = $Outcome.error }
    else { throw 'Invalid Registry stage outcome.' }
    if ($status -isnot [string] -or $status -cnotin @('Completed', 'Failed', 'PrerequisiteNotMet') -or $stageError -isnot [string]) { throw 'Invalid Registry stage status/error.' }
    if ($status -ceq 'PrerequisiteNotMet') { throw "PrerequisiteNotMet: Registry $Stage stage: $stageError" }
    if ($status -ceq 'Failed') { throw "Registry $Stage stage failed: $stageError" }
    if ($stageError) { throw 'Invalid completed Registry stage with an error.' }
}

function Get-YLRegistryClosure {
    param([Collections.IDictionary] $Release, [string] $EntryPackage, [string[]] $EntryPackages = @($EntryPackage))
    $pending = [Collections.Generic.Queue[string]]::new()
    foreach ($id in @(Get-YLRegistryEntryPackages -EntryPackage $EntryPackage -EntryPackages $EntryPackages)) { $pending.Enqueue($id) }
    $result = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    while ($pending.Count -gt 0) {
        $id = $pending.Dequeue(); if ($result.ContainsKey($id)) { continue }
        $rows = @($Release.packages | Where-Object { $_.id -ceq $id })
        if ($rows.Count -ne 1) { throw 'Registry entry/internal dependency is missing from the frozen release.' }
        $result.Add($id, $rows[0])
        foreach ($dependency in $rows[0].dependencies.Keys) { if ($dependency.StartsWith('com.kano.', [StringComparison]::Ordinal)) { $pending.Enqueue($dependency) } }
    }
    return @($result.Values)
}

function New-YLRegistryHostManifest {
    param([ValidateSet('Registry', 'LocalTarball')][string] $PackageSource, [Collections.IDictionary] $Release, [string] $EntryPackage, [string] $RegistryUrl = 'https://package.openupm.com', [string[]] $EntryPackages = @($EntryPackage))
    if ($RegistryUrl -cne 'https://package.openupm.com') { throw 'Only the approved OpenUPM RegistryUrl is supported.' }
    $packages = @(Get-YLRegistryClosure -Release $Release -EntryPackage $EntryPackage -EntryPackages $EntryPackages)
    $dependencies = [ordered]@{}
    foreach ($package in $packages) {
        if ($PackageSource -ceq 'LocalTarball') { $dependencies[$package.id] = 'file:' + $package.path.Replace('\', '/') }
        elseif ($package.id -cin $EntryPackages) { $dependencies[$package.id] = $package.version }
    }
    $scopes = @('com.kano.ylframework')
    if ($EntryPackage -ceq 'com.kano.ylframework.hybridclr') { $scopes += 'com.code-philosophy.hybridclr' }
    return @{ dependencies = $dependencies; scopedRegistries = @(@{ name = 'OpenUPM'; url = $RegistryUrl; scopes = $scopes }) }
}

function Assert-YLRegistryResolvedGraph {
    param([Collections.IDictionary] $Manifest, [Collections.IDictionary] $Lock, [object[]] $Rows, [Collections.IDictionary] $Release,
        [string] $EntryPackage, [ValidateSet('Registry', 'LocalTarball')][string] $PackageSource, [string] $RegistryUrl,
        [string] $EditorVersion, [string] $HostRoot, [string] $CacheRoot, [Collections.IDictionary] $SdkDependencies = @{}, [string[]] $EntryPackages = @($EntryPackage))
    $expectedManifest = New-YLRegistryHostManifest -PackageSource $PackageSource -Release $Release -EntryPackage $EntryPackage -EntryPackages $EntryPackages -RegistryUrl $RegistryUrl
    Assert-YLPackageDependencies $Manifest['dependencies'] $expectedManifest.dependencies
    if ($Manifest['scopedRegistries'] -isnot [array] -or $Manifest['scopedRegistries'].Count -ne 1) { throw 'Consumer manifest Registry scope changed.' }
    $scope = $Manifest['scopedRegistries'][0]
    if ($scope['url'] -isnot [string] -or $scope['url'] -cne $RegistryUrl -or $scope['scopes'] -isnot [array] -or
        ($scope['scopes'] -join '|') -cne ($expectedManifest.scopedRegistries[0].scopes -join '|')) { throw 'Consumer manifest Registry URL/scopes changed.' }
    $expected = @{}; foreach ($package in @(Get-YLRegistryClosure -Release $Release -EntryPackage $EntryPackage -EntryPackages $EntryPackages)) { $expected[$package.id] = $package }
    $actual = @{}
    if ($Lock['dependencies'] -isnot [Collections.IDictionary] -or $Lock['dependencies'].Count -ne $Rows.Count) { throw 'Observed PackageInfo and lock graph differ.' }
    foreach ($row in $Rows) {
        foreach ($field in @('name', 'version', 'source', 'packageId', 'resolvedPath')) { if ($row[$field] -isnot [string] -or -not $row[$field]) { throw "Invalid observed package graph identity: $field" } }
        if ($null -ne $row['registryUrl'] -and $row['registryUrl'] -isnot [string]) { throw 'Invalid observed package Registry URL type.' }
        $id = $row['name']
        if ($actual.ContainsKey($id) -or $row['isDirectDependency'] -isnot [bool] -or $row['isDirectDependency'] -ne $Manifest['dependencies'].Contains($id)) { throw 'Observed package direct/indirect identity differs from the original manifest.' }
        $entry = $Lock['dependencies'][$id]
        if ($entry -isnot [Collections.IDictionary] -or $entry['source'] -isnot [string] -or $entry['version'] -isnot [string] -or
            ($entry['depth'] -isnot [int] -and $entry['depth'] -isnot [long]) -or $entry['depth'] -lt 0) { throw 'Invalid observed package lock identity/depth.' }
        if ($entry['source'] -ceq 'registry' -and $entry['url'] -isnot [string]) { throw 'Invalid observed package lock Registry URL type.' }
        Assert-YLPackageDependencies $row['dependencies'] $entry['dependencies']
        if ($expected.ContainsKey($id)) {
            $package = $expected[$id]; $source = if ($PackageSource -ceq 'Registry') { 'Registry' } else { 'LocalTarball' }
            $lockSource = if ($PackageSource -ceq 'Registry') { 'registry' } else { 'local-tarball' }
            $locator = if ($PackageSource -ceq 'Registry') { $package.version } else { $Manifest['dependencies'][$id] }
            if ($row['source'] -cne $source -or $entry['source'] -cne $lockSource -or $row['version'] -cne $package.version -or
                $entry['version'] -cne $locator -or $row['packageId'].Replace('\', '/') -cne ($id + '@' + $locator)) { throw 'Observed package source/version/packageId/lock differs from the frozen request.' }
            Assert-YLPackageDependencies $row['dependencies'] $package.dependencies
            if ($PackageSource -ceq 'Registry' -and ($row['registryUrl'] -isnot [string] -or $row['registryUrl'] -cne $RegistryUrl -or $entry['url'] -cne $RegistryUrl)) { throw 'Observed package Registry URL differs.' }
        } elseif ($id -ceq 'com.code-philosophy.hybridclr' -and $EntryPackage -ceq 'com.kano.ylframework.hybridclr') {
            if ($row['source'] -cne 'Registry' -or $entry['source'] -cne 'registry' -or $row['version'] -cne '9.0.3' -or $entry['version'] -cne '9.0.3' -or
                $row['packageId'] -cne ($id + '@9.0.3') -or $row['registryUrl'] -cne $RegistryUrl -or $entry['url'] -cne $RegistryUrl -or $row['isDirectDependency']) { throw 'Observed HybridCLR SDK must be the indirect official Registry version.' }
            Assert-YLPackageDependencies $row['dependencies'] $SdkDependencies
        } elseif ($id.StartsWith('com.unity.', [StringComparison]::Ordinal)) {
            if ($row['source'] -cnotin @('Registry', 'BuiltIn') -or $entry['source'] -cne $row['source'].ToLowerInvariant() -or $entry['version'] -cne $row['version'] -or
                $row['packageId'] -cne ($id + '@' + $row['version']) -or ($row['source'] -ceq 'Registry' -and
                    ($row['registryUrl'] -cne 'https://packages.unity.com' -or $entry['url'] -cne 'https://packages.unity.com'))) { throw 'Observed official package source/Registry/lock identity differs.' }
        } else { throw "Unauthorized observed package graph entry: $id" }
        if ($row['source'] -cne 'BuiltIn') {
            $root = Get-YLTestPath $row['resolvedPath']; $hostCache = Join-Path $HostRoot 'Library/PackageCache'
            if (-not $root.StartsWith($hostCache + '\', [StringComparison]::OrdinalIgnoreCase) -and -not $root.StartsWith($CacheRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Observed package resolved outside the cold owned host/cache.' }
        }
        $actual[$id] = $row
    }
    foreach ($id in $expected.Keys) { if (-not $actual.ContainsKey($id)) { throw 'Expected frozen package dependency is missing.' } }
    $depths = @{}; $queue = [Collections.Generic.Queue[string]]::new()
    foreach ($id in $Manifest['dependencies'].Keys) { $depths[$id] = 0; $queue.Enqueue($id) }
    while ($queue.Count -gt 0) {
        $id = $queue.Dequeue()
        if (-not $actual.ContainsKey($id)) { throw "Expected dependency graph package is missing: $id" }
        foreach ($dependency in $actual[$id]['dependencies'].GetEnumerator()) {
            if ($dependency.Value -isnot [string] -or -not $actual.ContainsKey($dependency.Key)) { throw 'Observed dependency graph is incomplete.' }
            $observed = $actual[$dependency.Key]['version']
            if ($dependency.Key.StartsWith('com.unity.', [StringComparison]::Ordinal)) {
                if ($observed -cnotmatch '^\d+\.\d+\.\d+$' -or $dependency.Value -cnotmatch '^\d+\.\d+\.\d+$') { throw 'Unsupported official package version constraint.' }
                $resolved = [version]$observed; $minimum = [version]$dependency.Value
                $ugui6 = $dependency.Key -ceq 'com.unity.ugui' -and $EditorVersion -ceq '6000.3.27f1' -and $observed -ceq '2.0.0' -and $dependency.Value -ceq '1.0.0'
                if (-not $ugui6 -and ($resolved -lt $minimum -or $resolved.Major -ne $minimum.Major)) { throw 'Official resolved version violates the declaration/Editor baseline.' }
            } elseif ($observed -cne $dependency.Value) { throw 'Resolved package version violates its frozen dependency declaration.' }
            if (-not $depths.ContainsKey($dependency.Key) -or $depths[$dependency.Key] -gt $depths[$id] + 1) { $depths[$dependency.Key] = $depths[$id] + 1; $queue.Enqueue($dependency.Key) }
        }
    }
    $baseline = @()
    foreach ($id in $actual.Keys) {
        if ($depths.ContainsKey($id)) {
            if ($Lock['dependencies'][$id]['depth'] -ne $depths[$id]) { throw 'Observed lock depth contradicts the actual direct/indirect dependency graph.' }
        } elseif ($actual[$id]['source'] -ceq 'BuiltIn' -and $id.StartsWith('com.unity.modules.', [StringComparison]::Ordinal) -and $actual[$id]['version'] -ceq '1.0.0') {
            $baseline += $actual[$id]
        } else { throw "Unreachable package in the observed dependency graph: $id" }
    }
    return @{ origin = $PackageSource; packages = $Rows; editorBaseline = $baseline; expectedDeclarations = @($expected.Values); directPackages = @($Manifest['dependencies'].Keys) }
}

function Assert-YLRegistryCacheObservation {
    param([Collections.IDictionary] $Actual, [string] $CacheRoot)
    $expected = @{ UPM_CACHE_ROOT = 'Upm'; UPM_CACHE_PATH = 'Upm/packages'; UPM_NPM_CACHE_PATH = 'Upm/npm' }
    foreach ($name in $expected.Keys) {
        if ($Actual[$name] -isnot [string] -or (Get-YLTestPath $Actual[$name]) -ine (Join-Path $CacheRoot $expected[$name])) { throw 'The actual Unity process did not use the isolated owned UPM cache.' }
    }
}

function Assert-YLRegistryColdCache {
    param([Diagnostics.ProcessStartInfo] $StartInfo, [string] $CacheRoot)
    $CacheRoot = Get-YLTestPath $CacheRoot
    $observed = @{}
    foreach ($name in @('UPM_CACHE_ROOT', 'UPM_CACHE_PATH', 'UPM_NPM_CACHE_PATH')) { $observed[$name] = $StartInfo.Environment[$name] }
    Assert-YLRegistryCacheObservation -Actual $observed -CacheRoot $CacheRoot
    foreach ($relative in @('Upm', 'Lfs')) {
        $path = Get-YLTestPath (Join-Path $CacheRoot $relative)
        if ([IO.Path]::Exists($path) -and (-not [IO.Directory]::Exists($path) -or @(Get-YLTestTreeFiles $path).Count -ne 0)) { throw 'A cold installation requires an empty owned cache before the first Unity launch.' }
    }
    return @{ status = 'EmptyBeforeLaunch'; checkedUtc = [DateTime]::UtcNow.ToString('o'); paths = $observed; observation = 'Controller launch environment; actual Editor environment must also match.' }
}

function Get-YLRegistryCacheUse {
    param([string] $CacheRoot, [ValidateSet('Registry', 'LocalTarball')][string] $PackageSource)
    $root = Join-Path (Get-YLTestPath $CacheRoot) 'Upm'
    $files = @(if ([IO.Directory]::Exists($root)) {
        foreach ($file in @(Get-YLTestTreeFiles $root)) {
            @{ path = [IO.Path]::GetRelativePath($root, $file).Replace('\', '/'); length = [IO.FileInfo]::new($file).Length
                sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() }
        }
    })
    if ($PackageSource -ceq 'Registry' -and $files.Count -eq 0) { throw 'No actual owned UPM cache files were observed after Registry resolution; environment variables alone cannot prove a cold download.' }
    return @{ status = $(if ($files.Count) { 'ObservedOwnedUpmCacheFiles' } else { 'NoUpmCacheFilesObserved' }); root = $root; files = $files
        observedUtc = [DateTime]::UtcNow.ToString('o'); scope = 'Post-resolution owned cache bytes, combined with the separate empty-before-launch and actual Editor environment observations.' }
}

function Save-YLRegistryTarball {
    param([string] $Uri, [string] $Output)
    $target = [uri]$Uri; $redirects = [Collections.Generic.List[object]]::new()
    for ($attempt = 0; $attempt -le 5; $attempt++) {
        if (-not $target.IsAbsoluteUri -or $target.Scheme -cne 'https' -or $target.UserInfo -or -not $target.IsDefaultPort -or
            $target.Host -cnotin @('package.openupm.com', 'download.openupm.com')) { throw 'Unapproved Registry tarball redirect host/HTTPS identity.' }
        try {
            $response = Invoke-WebRequest -Uri $target.AbsoluteUri -OutFile $Output -TimeoutSec 120 -MaximumRedirection 0 -PassThru -ErrorAction Stop
        } catch {
            if ($_.Exception -is [Microsoft.PowerShell.Commands.HttpResponseException] -and
                [int]$_.Exception.Response.StatusCode -in @(301, 302, 303, 307, 308) -and $null -ne $_.Exception.Response.Headers.Location) {
                $next = [uri]::new($target, $_.Exception.Response.Headers.Location)
                $redirects.Add(@{ from = $target.AbsoluteUri; status = [int]$_.Exception.Response.StatusCode; to = $next.AbsoluteUri })
                $target = $next; continue
            }
            throw "PrerequisiteNotMet: Registry tarball download is unavailable: $($_.Exception.Message)"
        }
        if ([int]$response.StatusCode -ne 200) { throw 'Registry tarball response did not contain an ordinary download.' }
        return @{ url = $Uri; finalUrl = $target.AbsoluteUri; redirects = @($redirects); downloadedUtc = [DateTime]::UtcNow.ToString('o'); cacheRole = 'IndependentEvidenceNotUpmCache' }
    }
    throw 'Registry tarball exceeded the bounded redirect limit.'
}

function Initialize-YLRegistryExpectedInputs {
    param([Collections.IDictionary] $Source, [string] $Output)
    . (Join-Path $PSScriptRoot '../../Packaging/Build-YLRegistryPackages.ps1')
    $Output = Get-YLTestPath $Output
    if ([IO.Path]::Exists($Output) -or -not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($Output))) { throw 'Expected artifact output must be new under an existing owned result.' }
    $null = [IO.Directory]::CreateDirectory($Output)
    $root = Join-Path $Output 'PackageSources'; $null = [IO.Directory]::CreateDirectory($root)
    $packages = @(foreach ($expected in $Source.release.packages) {
        $archive = $expected.path
        $registryEvidence = $null
        if ($Source.packageOrigin -ceq 'LocalTarball' -and -not [IO.File]::Exists($archive)) { throw "PrerequisiteNotMet: Missing frozen release tarball: $($expected.assetName)" }
        if ($Source.packageOrigin -ceq 'Registry') {
            $metadataPath = Join-Path $Output ($expected.id + '.json')
            $metadataUrl = $Source.registryUrl + '/' + $expected.id + '/' + $expected.version
            try { Invoke-WebRequest -Uri $metadataUrl -OutFile $metadataPath -TimeoutSec 120 -MaximumRedirection 0 -ErrorAction Stop }
            catch { throw "PrerequisiteNotMet: Registry metadata is unavailable for $($expected.id)@$($expected.version): $($_.Exception.Message)" }
            $metadata = Read-YLPackageEvidenceJson $metadataPath
            if ($metadata['name'] -isnot [string] -or $metadata['version'] -isnot [string] -or $metadata['name'] -cne $expected.id -or $metadata['version'] -cne $expected.version -or $metadata['dist'] -isnot [Collections.IDictionary] -or
                $metadata['dist']['integrity'] -isnot [string] -or
                $metadata['dist']['integrity'] -cne $expected.integrity) { throw 'Registry metadata identity/integrity differs from the frozen Release asset.' }
            Assert-YLPackageDependencies $metadata['dependencies'] $expected.dependencies
            $archive = Join-Path $Output $expected.assetName
            $tarballUrl = $Source.registryUrl + '/' + $expected.id + '/-/' + $expected.assetName
            if ($metadata['dist']['tarball'] -isnot [string] -or $metadata['dist']['tarball'] -cne $tarballUrl) { throw 'Registry metadata points to an unexpected tarball URL.' }
            $download = Save-YLRegistryTarball -Uri $tarballUrl -Output $archive
            $registryEvidence = @{ metadata = $metadataPath; metadataUrl = $metadataUrl; metadataSha256 = (Get-FileHash -LiteralPath $metadataPath -Algorithm SHA256).Hash.ToLowerInvariant(); download = $download }
        }
        $payload = Read-YLRegistryTarball -Path $archive -Destination (Join-Path $root $expected.id)
        Assert-YLRegistryPayloadReceipt -Payload $payload -Expected $expected
        $payload | Add-Member -NotePropertyName root -NotePropertyValue (Join-Path $root $expected.id)
        $payload | Add-Member -NotePropertyName registryEvidence -NotePropertyValue $registryEvidence
        $payload
    })
    Assert-YLRegistryPackageBatch $packages
    return @{ root = $Output; packages = $packages; purpose = 'IndependentExpectedArtifactDownloadsNotUpmCache' }
}

function Get-YLHybridClrRegistrySdk {
    param([string] $Approval, [string] $Output, [string] $RegistryUrl)
    . (Join-Path $PSScriptRoot '../../Packaging/Build-YLRegistryPackages.ps1')
    $approved = (Read-YLPackageEvidenceJson $Approval)['package']
    $id = 'com.code-philosophy.hybridclr'; $revision = '6cadd24d101f4cbd00cd80190106f6621132d24d'
    if ($approved -isnot [Collections.IDictionary]) { throw 'Invalid SDK approval package object.' }
    foreach ($field in @('name', 'version', 'commit', 'url')) { if ($approved[$field] -isnot [string]) { throw "Invalid SDK approval identity type: $field" } }
    if ($RegistryUrl -cne 'https://package.openupm.com' -or $approved['name'] -cne $id -or $approved['version'] -cne '9.0.3' -or
        $approved['commit'] -cne $revision -or $approved['bundled'] -isnot [bool] -or $approved['bundled'] -or
        $approved['url'] -cne 'https://gitee.com/focus-creative-games/hybridclr_unity.git#v9.0.3') { throw 'HybridCLR SDK approval/source/version is invalid.' }
    $registry = $approved['registry']
    if ($registry -isnot [Collections.IDictionary]) { throw 'Invalid Registry SDK approval object.' }
    foreach ($field in @('url', 'metadataUrl', 'repositoryUrl', 'repositoryRevision')) { if ($registry[$field] -isnot [string]) { throw "Invalid Registry SDK approval identity type: $field" } }
    if ($registry -isnot [Collections.IDictionary] -or $registry['url'] -cne $RegistryUrl -or
        $registry['metadataUrl'] -cne "$RegistryUrl/$id/9.0.3" -or $registry['repositoryUrl'] -cne 'git+https://github.com/focus-creative-games/hybridclr_unity.git' -or
        $registry['repositoryRevision'] -cne $revision -or $registry['nativeValidated'] -isnot [bool] -or $registry['nativeValidated']) { throw 'Registry SDK approval must preserve exact metadata identity and the unvalidated native boundary.' }
    $Output = Get-YLTestPath $Output
    if ([IO.Path]::Exists($Output) -or -not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($Output))) { throw 'Registry SDK evidence output must be new under an existing owned result.' }
    $null = [IO.Directory]::CreateDirectory($Output)
    $metadataPath = Join-Path $Output 'metadata.json'; $metadataUrl = "$RegistryUrl/$id/9.0.3"
    try { Invoke-WebRequest -Uri $metadataUrl -OutFile $metadataPath -TimeoutSec 120 -MaximumRedirection 0 -ErrorAction Stop }
    catch { throw "PrerequisiteNotMet: HybridCLR Registry SDK metadata is unavailable: $($_.Exception.Message)" }
    $metadata = Read-YLPackageEvidenceJson $metadataPath
    foreach ($field in @('name', 'version')) { if ($metadata[$field] -isnot [string]) { throw "Invalid Registry SDK metadata identity type: $field" } }
    if ($metadata['repository'] -isnot [Collections.IDictionary]) { throw 'Invalid Registry SDK repository metadata.' }
    foreach ($field in @('type', 'url', 'revision')) { if ($metadata['repository'][$field] -isnot [string]) { throw "Invalid Registry SDK repository identity type: $field" } }
    if ($metadata['name'] -cne $id -or $metadata['version'] -cne '9.0.3' -or $metadata['repository'] -isnot [Collections.IDictionary] -or
        $metadata['repository']['type'] -cne 'git' -or $metadata['repository']['url'] -cne 'git+https://github.com/focus-creative-games/hybridclr_unity.git' -or
        $metadata['repository']['revision'] -cne $revision -or $metadata['dist'] -isnot [Collections.IDictionary] -or
        $metadata['dist']['integrity'] -isnot [string] -or $metadata['dist']['integrity'] -cnotmatch '^sha512-[A-Za-z0-9+/]{86}==$') { throw 'HybridCLR SDK Registry identity/repository revision/integrity is incorrect.' }
    $dependencies = if ($metadata.Contains('dependencies')) { $metadata['dependencies'] } else { @{} }
    if ($dependencies -isnot [Collections.IDictionary] -or $dependencies.Count -ne 0) { throw 'HybridCLR SDK Registry dependency declarations differ from the verified official version.' }
    $url = "$RegistryUrl/$id/-/$id-9.0.3.tgz"
    if ($metadata['dist']['tarball'] -isnot [string] -or $metadata['dist']['tarball'] -cne $url) { throw 'HybridCLR SDK Registry tarball URL is incorrect.' }
    $archive = Join-Path $Output "$id-9.0.3.tgz"
    $download = Save-YLRegistryTarball -Uri $url -Output $archive
    $integrity = 'sha512-' + [Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData([IO.File]::ReadAllBytes($archive)))
    if ($integrity -cne $metadata['dist']['integrity']) { throw 'HybridCLR SDK tarball integrity differs from Registry metadata.' }
    $root = Join-Path $Output 'payload'
    $payload = Read-YLRegistryTarball -Path $archive -Destination $root -SdkPackage $id
    Assert-YLPackageDependencies $payload.dependencies $dependencies
    return @{ source = 'Registry'; version = '9.0.3'; registryUrl = $RegistryUrl; repositoryRevision = $revision; dependencies = $dependencies; root = $root; files = $payload.files
        evidence = @{ metadata = $metadataPath; metadataUrl = $metadataUrl; metadataSha256 = (Get-FileHash -LiteralPath $metadataPath -Algorithm SHA256).Hash.ToLowerInvariant()
            tarball = $archive; sha256 = $payload.sha256; integrity = $integrity; download = $download; repositoryRevision = $revision; revisionEvidence = 'RegistryMetadataDeclarationNotGitCheckout'
            license = (Join-Path $root 'LICENSE'); licenseSha256 = (Get-FileHash -LiteralPath (Join-Path $root 'LICENSE') -Algorithm SHA256).Hash.ToLowerInvariant() } }
}

function Assert-YLHybridClrRegistryInstalled {
    param([Collections.IDictionary] $Sdk, [string] $Root)
    $Root = Get-YLTestPath $Root
    $actual = @(Get-YLTestTreeFiles $Root)
    $expected = @{}; foreach ($file in $Sdk.files) { $expected[$file.path] = $file }
    if ($actual.Count -ne $expected.Count) { throw 'Installed HybridCLR SDK file set differs from the verified Registry tarball.' }
    function Canonical([Newtonsoft.Json.Linq.JToken] $Token) {
        if ($Token -is [Newtonsoft.Json.Linq.JObject]) {
            $value = [Newtonsoft.Json.Linq.JObject]::new()
            [string[]]$names = @($Token.Properties() | ForEach-Object Name); [Array]::Sort($names, [StringComparer]::Ordinal)
            foreach ($name in $names) { $value.Add($name, (Canonical $Token[$name])) }; return ,$value
        }
        if ($Token -is [Newtonsoft.Json.Linq.JArray]) {
            $value = [Newtonsoft.Json.Linq.JArray]::new(); foreach ($item in $Token) { $value.Add((Canonical $item)) }; return ,$value
        }
        return ,$Token.DeepClone()
    }
    foreach ($path in $actual) {
        $relative = [IO.Path]::GetRelativePath($Root, $path).Replace('\', '/')
        if (-not $expected.ContainsKey($relative)) { throw 'Installed HybridCLR SDK has an unapproved file.' }
        if ($relative -ceq 'package.json') {
            $null = Read-YLPackageEvidenceJson $path
            $reader = [Newtonsoft.Json.JsonTextReader]::new([IO.StringReader]::new([IO.File]::ReadAllText($path))); $reader.DateParseHandling = [Newtonsoft.Json.DateParseHandling]::None
            try { $manifest = [Newtonsoft.Json.Linq.JObject]::Load($reader) } finally { $reader.Dispose() }
            $fingerprint = $manifest.Property('_fingerprint')
            if ($null -ne $fingerprint) {
                if ($fingerprint.Value.Type -ne [Newtonsoft.Json.Linq.JTokenType]::String -or [string]$fingerprint.Value -cnotmatch '^[0-9a-fA-F]{40}$') { throw 'Invalid installed SDK package-manager metadata.' }
                $fingerprint.Remove()
            }
            foreach ($property in $manifest.Properties()) { if ($property.Name.StartsWith('_', [StringComparison]::Ordinal)) { throw 'Unknown installed SDK package-manager metadata.' } }
            $text = (Canonical $manifest).ToString([Newtonsoft.Json.Formatting]::None)
            $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text))).ToLowerInvariant()
        } else { $digest = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
        if ($digest -cne $expected[$relative].contentSha256) { throw "Installed HybridCLR SDK content differs from the verified tarball: $relative" }
    }
}
