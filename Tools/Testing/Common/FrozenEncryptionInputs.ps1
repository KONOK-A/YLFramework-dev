#requires -Version 7.6
#requires -PSEdition Core
. (Join-Path $PSScriptRoot 'IntegrationPackageContent.ps1')

function Get-YLFrozenFileSha256([string] $Path) {
    return (Get-FileHash -LiteralPath (Get-YLTestPath $Path) -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Read-YLFrozenGit {
    param([string] $Repository, [string[]] $Arguments)
    $git = (Get-Command git.exe -CommandType Application -TotalCount 1).Source
    $start = [Diagnostics.ProcessStartInfo]::new($git)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $Repository
    $start.RedirectStandardOutput = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false, $true)
    foreach ($name in @($start.Environment.Keys)) {
        if ($name -match '^GIT_') { $null = $start.Environment.Remove($name) }
    }
    foreach ($item in (@('--no-optional-locks', '--no-replace-objects', '--literal-pathspecs', '-c', 'core.fsmonitor=false') + $Arguments)) {
        $start.ArgumentList.Add($item)
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $result = $process.StandardOutput.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "Could not read fixed Git evidence with $($Arguments[0])." }
        return $result
    } finally { $process.Dispose() }
}

function Assert-YLFrozenSourceCommit {
    param([string] $Repository, [string] $SourceCommit, [switch] $HeadOnly)
    if ($SourceCommit -cnotmatch '^[0-9a-f]{40}$') { throw 'SourceCommit must be a full lowercase Git SHA.' }
    $Repository = Get-YLTestPath $Repository
    if ((Get-YLTestPath (Read-YLFrozenGit $Repository @('rev-parse', '--show-toplevel')).Trim()) -ine $Repository -or
        (Read-YLFrozenGit $Repository @('rev-parse', '--verify', 'HEAD')).Trim() -cne $SourceCommit) {
        throw 'Expected SourceCommit must equal the acceptance worktree HEAD.'
    }
    if (-not $HeadOnly -and (Read-YLFrozenGit $Repository @('status', '--porcelain=v1', '--untracked-files=all', '--ignore-submodules=none')).Trim()) {
        throw 'Fixed-commit encryption acceptance requires a clean worktree; use a diagnostic pack receipt for uncommitted work.'
    }
}

function Get-YLFrozenBlobOid([IO.Stream] $Stream) {
    $prefix = [Text.Encoding]::ASCII.GetBytes('blob ' + $Stream.Length.ToString([Globalization.CultureInfo]::InvariantCulture) + [char]0)
    $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA1)
    try {
        $hash.AppendData($prefix)
        $Stream.Position = 0
        $buffer = [byte[]]::new(64KB)
        while (($length = $Stream.Read($buffer)) -gt 0) { $hash.AppendData($buffer, 0, $length) }
        return [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
    } finally { $hash.Dispose() }
}

function Assert-YLFrozenLfsObject {
    param([string] $MediaRoot, [string] $Oid, [long] $Size)
    if ($Oid -cnotmatch '^[0-9a-f]{64}$' -or $Size -le 0) { throw 'Invalid committed LFS object identity.' }
    $path = Get-YLTestPath (Join-Path $MediaRoot "$($Oid.Substring(0,2))/$($Oid.Substring(2,2))/$Oid")
    if (-not [IO.File]::Exists($path)) { throw "Committed Git LFS object is missing locally: $Oid" }
    $file = [IO.File]::OpenRead($path)
    try {
        if ($file.Length -ne $Size -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($file)) -ine $Oid) {
            throw "Committed Git LFS object content differs: $Oid"
        }
    } finally { $file.Dispose() }
}

function Assert-YLFrozenCommittedFiles {
    param([string] $Repository, [string] $SourceCommit, [object[]] $Files, [switch] $FullPackageInventory, [string[]] $RequireLfs = @())
    if ($SourceCommit -cnotmatch '^[0-9a-f]{40}$' -or $Files.Count -eq 0) { throw 'Expected a fixed commit and committed file inventory.' }
    $Repository = Get-YLTestPath $Repository
    $paths = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($row in $Files) {
        if ($row.path -isnot [string] -or [IO.Path]::IsPathRooted($row.path) -or $row.sha256 -cnotmatch '^[0-9a-f]{64}$') {
            throw 'Invalid fixed-commit input identity.'
        }
        $path = Get-YLTestPath (Join-Path $Repository $row.path)
        if ([IO.Path]::GetRelativePath($Repository, $path).Replace('\', '/') -cne $row.path -or -not $paths.TryAdd($row.path, $row.sha256)) {
            throw 'Unexpected or duplicate fixed-commit input path.'
        }
    }
    $scope = if ($FullPackageInventory) { @('PackageSources') + @($paths.Keys | Where-Object { $_ -cnotlike 'PackageSources/*' }) }
        else { @($paths.Keys) }
    $tree = Read-YLFrozenGit $Repository (@('ls-tree', '-rz', '--format=%(objectmode)%x09%(objectname)%x09%(path)', $SourceCommit, '--') + $scope)
    $blobs = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $tree.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)) {
        if ($entry -cnotmatch '\A(100644|100755)\t([0-9a-f]{40})\t([^\t\r\n]+)\z') { throw 'Fixed inputs require regular committed Git blobs.' }
        $name = $Matches[3]
        # Parent folder metadata and package Git attributes are not npm/UPM payload files.
        if ($FullPackageInventory -and $name -cmatch '\APackageSources/(?:[^/]+\.meta|[^/]+/\.gitattributes)\z') { continue }
        if (-not $paths.ContainsKey($name)) { throw "Release input inventory omits committed package input: $name" }
        $blobs.Add($name, $Matches[2])
    }
    $lfs = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    if ($RequireLfs.Count -or @($paths.Keys | Where-Object { $_ -clike 'PackageSources/*' }).Count) {
        $listed = (Read-YLFrozenGit $Repository @('lfs', 'ls-files', '--json', '--include=PackageSources/**', '--exclude=', $SourceCommit)) |
            ConvertFrom-Json -AsHashtable -DateKind String
        foreach ($row in $listed['files']) { $lfs.Add($row['name'], $row) }
    }
    foreach ($name in $RequireLfs) {
        if (-not $paths.ContainsKey($name) -or -not $lfs.ContainsKey($name)) { throw "Missing required committed Git LFS pointer: $name" }
    }
    $common = $null
    foreach ($name in $paths.Keys) {
        if (-not $blobs.ContainsKey($name)) { throw "No regular committed blob for fixed input: $name" }
        $path = Get-YLTestPath (Join-Path $Repository $name)
        $file = [IO.File]::OpenRead($path)
        try {
            if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($file)) -ine $paths[$name]) { throw "Fixed input bytes changed: $name" }
            if ($lfs.ContainsKey($name)) {
                $pointer = $lfs[$name]
                $oid = $pointer['oid']
                $size = $file.Length
                if ($pointer['oid_type'] -cne 'sha256' -or $pointer['version'] -cne 'https://git-lfs.github.com/spec/v1' -or
                    $pointer['size'] -ne $size -or $oid -cne $paths[$name]) { throw "Fixed bytes differ from their committed Git LFS pointer: $name" }
                $text = "version https://git-lfs.github.com/spec/v1`noid sha256:$oid`nsize " + $size.ToString([Globalization.CultureInfo]::InvariantCulture) + "`n"
                $stream = [IO.MemoryStream]::new([Text.Encoding]::ASCII.GetBytes($text), $false)
                try { $actual = Get-YLFrozenBlobOid $stream } finally { $stream.Dispose() }
                if ($null -eq $common) { $common = Get-YLTestPath (Read-YLFrozenGit $Repository @('rev-parse', '--path-format=absolute', '--git-common-dir')).Trim() }
                Assert-YLFrozenLfsObject -MediaRoot (Join-Path $common 'lfs/objects') -Oid $oid -Size $size
            } else { $actual = Get-YLFrozenBlobOid $file }
            if ($actual -cne $blobs[$name]) { throw "Fixed input differs from its committed Git blob: $name" }
        } finally { $file.Dispose() }
    }
}

function Get-YLFrozenEncryptionPlugin {
    param([string] $Repository, [Collections.IDictionary] $Package)
    $extension = 'com.kano.ylframework.resource-encryption'
    $dll = 'Runtime/Plugins/Win64/ylresource_tink.dll'
    $native = (Read-YLPackageEvidenceJson (Join-Path $Repository "PackageSources/$extension/Documentation~/NativeBackend.json"))['artifact']
    $plugin = @($Package['files'] | Where-Object path -CEQ $dll)
    $importer = @($Package['files'] | Where-Object path -CEQ "$dll.meta")
    if ($Package['id'] -cne $extension -or $native['relativePath'] -cne $dll -or $native['machine'] -cne 'AMD64' -or
        $native['sha256'] -cnotmatch '^[0-9a-f]{64}$' -or $native['importerSha256'] -cnotmatch '^[0-9a-f]{64}$' -or
        $plugin.Count -ne 1 -or $importer.Count -ne 1 -or $plugin[0]['sha256'] -cne $native['sha256'] -or $importer[0]['sha256'] -cne $native['importerSha256']) {
        throw 'The release does not contain the pinned Win64 plugin and importer.'
    }
    return $native
}

function Read-YLFrozenEncryptionRelease {
    param([string] $Path, [string] $Repository, [string] $SourceCommit)
    $Path = Get-YLTestPath $Path
    $Repository = Get-YLTestPath $Repository
    $catalog = Read-YLPackageCatalog -Path (Join-Path $Repository 'Tools/Packaging/PackageCatalog.json')
    $pinned = [IO.File]::OpenRead($Path)
    try {
        $release = Read-YLRegistryRelease -Path $Path -VerifyTarballs -Catalog $catalog -PackageRoot (Join-Path $Repository 'PackageSources')
        if ($SourceCommit -cne $release.sourceCommit) { throw 'Release manifest sourceCommit differs from the expected SourceCommit.' }
        $extension = @($release.packages | Where-Object id -CEQ 'com.kano.ylframework.resource-encryption')
        if ($extension.Count -ne 1) { throw 'The complete release must include the encryption extension.' }
        $release['plugin'] = Get-YLFrozenEncryptionPlugin -Repository $Repository -Package $extension[0]
        $release['releaseManifestSha256'] = $release.sha256
        return $release
    } finally { $pinned.Dispose() }
}

function Get-YLFrozenEncryptionRelease {
    param([string] $Path, [string] $Repository, [string] $SourceCommit)
    # Status can run the LFS clean filter. Require the committed objects before it can rehydrate them.
    Assert-YLFrozenSourceCommit -Repository $Repository -SourceCommit $SourceCommit -HeadOnly
    $release = Read-YLFrozenEncryptionRelease @PSBoundParameters
    . (Join-Path $PSScriptRoot '../../Packaging/Build-YLRegistryPackages.ps1')
    $catalog = Read-YLPackageCatalog -Path (Join-Path $Repository 'Tools/Packaging/PackageCatalog.json')
    $sources = @(Get-YLRegistryPackageSet -Root (Join-Path $Repository 'PackageSources') -Catalog $catalog)
    $files = @(foreach ($source in $sources) {
        foreach ($file in $source.files) { @{ path = "PackageSources/$($source.id)/$($file.path)"; sha256 = $file.sha256 } }
    })
    $catalogFile = 'Tools/Packaging/PackageCatalog.json'
    $files += @{ path = $catalogFile; sha256 = Get-YLFrozenFileSha256 (Join-Path $Repository $catalogFile) }
    Assert-YLFrozenCommittedFiles -Repository $Repository -SourceCommit $SourceCommit -Files $files -FullPackageInventory `
        -RequireLfs 'PackageSources/com.kano.ylframework.resource-encryption/Runtime/Plugins/Win64/ylresource_tink.dll'
    foreach ($package in $release.packages) {
        $sourcePackage = @($sources | Where-Object id -CEQ $package.id)
        if ($sourcePackage.Count -ne 1 -or $sourcePackage[0].version -cne $package.version -or $sourcePackage[0].payloadSha256 -cne $package.payloadSha256) {
            throw "Release package payload does not match clean SourceCommit: $($package.id)"
        }
    }
    Assert-YLFrozenSourceCommit -Repository $Repository -SourceCommit $SourceCommit
    Assert-YLFrozenReleaseUnchanged -Release $release
    return $release
}

function Get-YLFrozenEncryptionClosure {
    param([Collections.IDictionary] $Release, [switch] $Removed)
    $main = 'com.kano.ylframework'
    $extension = "$main.resource-encryption"
    $root = if ($Removed) { $main } else { $extension }
    $closure = @(Get-YLRegistryClosure -Release $Release -EntryPackage $root)
    if (($closure.id -join ';') -cne $(if ($Removed) { $main } else { "$main;$extension" })) { throw 'Unexpected own package dependency closure.' }
    return $closure
}

function Get-YLFrozenTarballBytes([Collections.IDictionary] $Package) {
    $path = Get-YLTestPath $Package['path']
    if ([IO.Path]::GetFileName($path) -cne "$($Package['id'])-$($Package['version']).tgz") { throw 'Frozen archive path/name differs from the release package.' }
    $file = [IO.File]::OpenRead($path)
    try {
        if ($file.Length -le 0 -or $file.Length -ge 512MB) { throw 'Frozen archive size exceeds the tar safety limit.' }
        $bytes = [byte[]]::new($file.Length)
        $file.ReadExactly($bytes)
        if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -ine $Package['sha256'] -or
            ('sha512-' + [Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData($bytes))) -cne $Package['integrity']) {
            throw "Frozen archive changed before taking the owned copy: $($Package['id'])"
        }
        return ,$bytes
    } finally { $file.Dispose() }
}

function Assert-YLFrozenReleaseUnchanged {
    param([Collections.IDictionary] $Release)
    if ((Get-YLFrozenFileSha256 $Release.path) -cne $Release.releaseManifestSha256) { throw 'The frozen release manifest changed during acceptance.' }
    foreach ($package in $Release.packages) {
        if ((Get-YLFrozenFileSha256 $package.path) -cne $package.sha256) { throw 'A frozen release archive changed during acceptance.' }
    }
}

function Assert-YLFrozenInstalledTarball {
    param([Collections.IDictionary] $Package, [Collections.IDictionary] $Row, [Collections.IDictionary] $LockRow,
        [string] $Locator, [string] $HostRoot, [string] $CacheRoot)
    $id = $Package['id']
    $name = "$id-$($Package['version']).tgz"
    if ($Locator -cnotin @("file:../Tarballs/$name", "file:../P0C/$name")) { throw "Unexpected owned archive locator: $id" }
    $HostRoot = Get-YLTestPath $HostRoot
    $archive = Get-YLTestPath (Join-Path $HostRoot $Locator.Substring('file:../'.Length))
    # Unity 6 reports an absolute file path in PackageInfo but keeps the original relative locator in the lock.
    $packageId = if ($Row -and $Row['packageId'] -is [string]) { $Row['packageId'].Replace('\', '/') }
    $absoluteLocator = 'file:' + $archive.Replace('\', '/')
    if ($null -eq $Row -or $null -eq $LockRow -or $Row['name'] -cne $id -or $Row['version'] -cne $Package['version'] -or
        $Row['source'] -cne 'LocalTarball' -or $Row['assetPath'] -cne "Packages/$id" -or $Row['isDirectDependency'] -isnot [bool] -or
        -not $Row['isDirectDependency'] -or ($packageId -cne "$id@$Locator" -and
        (-not $packageId -or -not $packageId.StartsWith("$id@file:", [StringComparison]::Ordinal) -or $packageId -ine "$id@$absoluteLocator")) -or
        $LockRow['version'] -cne $Locator -or $LockRow['source'] -cne 'local-tarball' -or $LockRow['depth'] -ne 0) {
        throw "Installed tarball source, version or locator differs: $id"
    }
    if ((Get-YLFrozenFileSha256 $archive) -cne $Package['sha256']) { throw "The owned installed archive changed: $id" }
    Assert-YLPackageDependencies $LockRow['dependencies'] $Package['dependencies']
    $root = Get-YLTestPath $Row['resolvedPath']
    $hostCache = Join-Path $HostRoot 'Library/PackageCache'
    if (-not $root.StartsWith($hostCache + '\', [StringComparison]::OrdinalIgnoreCase) -and
        -not $root.StartsWith($CacheRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Installed package resolved outside the owned host/cache: $id" }
    $actual = Get-YLIntegrationPackageContent -Root $root -PackageId $id -NormalizeManifest -AllowManagedFingerprint -FingerprintSource LocalTarball
    if ($actual.sha256 -cne $Package['payloadSha256'] -or $actual.files.Count -ne $Package['files'].Count) { throw "Installed archive payload differs from the frozen inventory: $id" }
    for ($i = 0; $i -lt $actual.files.Count; $i++) {
        if ($actual.files[$i].path -cne $Package['files'][$i].path -or $actual.files[$i].sha256 -cne $Package['files'][$i].contentSha256) {
            throw "Installed archive content differs from the frozen file inventory: $id"
        }
    }
    return @{ id = $id; source = $Row.source; resolvedPath = $root; locator = $Locator; archivePath = $archive;
        payloadSha256 = $actual.sha256; verifiedFiles = $actual.files.Count }
}

function Set-YLFrozenUpmEnvironment {
    param([Diagnostics.ProcessStartInfo] $StartInfo, [string] $CacheRoot, [switch] $Cold)
    $StartInfo.Environment['UPM_CACHE_ROOT'] = Join-Path $CacheRoot 'Upm'
    $StartInfo.Environment['UPM_CACHE_PATH'] = Join-Path $CacheRoot 'Upm/packages'
    $StartInfo.Environment['UPM_NPM_CACHE_PATH'] = Join-Path $CacheRoot 'Upm/npm'
    $StartInfo.Environment['UPM_GIT_LFS_CACHE_PATH'] = Join-Path $CacheRoot 'Lfs'
    if ($Cold) { $null = Assert-YLRegistryColdCache -StartInfo $StartInfo -CacheRoot $CacheRoot }
}

function Assert-YLFrozenInstalledInputs {
    param([Collections.IDictionary] $Build, [Collections.IDictionary] $Dependencies, [Collections.IDictionary] $ProfileLock,
        [object[]] $Packages, [string] $HostRoot, [string] $CacheRoot)
    if ([IO.Path]::Exists((Join-Path $HostRoot 'PackageSources'))) { throw 'An archive acceptance host cannot carry projected package source.' }
    Assert-YLRegistryCacheObservation -Actual $Build['cacheEnvironment'] -CacheRoot $CacheRoot
    if ($Build['cacheEnvironment']['UPM_GIT_LFS_CACHE_PATH'] -ine (Join-Path $CacheRoot 'Lfs')) { throw 'The Editor did not use its owned LFS cache.' }
    $declared = Read-YLPackageEvidenceJson (Join-Path $HostRoot 'Packages/manifest.json') -ExpectedSha256 $Build['manifestSha256']
    Assert-YLPackageDependencies $declared['dependencies'] $Dependencies
    $lock = (Read-YLPackageEvidenceJson (Join-Path $HostRoot 'Packages/packages-lock.json') -ExpectedSha256 $Build['packageLockSha256'])['dependencies']
    $actual = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $own = @($Packages.id)
    foreach ($row in $Build['registeredPackages']) {
        $name = $row['name']
        if ($name -isnot [string] -or ($name -cnotin $own -and (-not $name.StartsWith('com.unity.', [StringComparison]::Ordinal) -or
            $row['source'] -cnotin @('Registry','BuiltIn'))) -or -not $actual.TryAdd($name, $row) -or -not $lock.Contains($name)) {
            throw 'Unexpected or duplicated registered package in the frozen host.'
        }
    }
    if ($lock.Count -ne $actual.Count) { throw 'The registered package set and lock differ.' }
    foreach ($name in $Dependencies.Keys) {
        if (-not $actual.ContainsKey($name)) { throw "An explicitly pinned package was not registered: $name" }
        if ($name -cin $own) { continue }
        $expected = $ProfileLock['dependencies'][$name]
        $row = $actual[$name]
        if ($row['isDirectDependency'] -ne $true -or $row['version'] -cne $Dependencies[$name] -or
            $row['packageId'] -cne "$name@$($Dependencies[$name])" -or $row['source'] -cne $(if ($expected['source'] -ceq 'registry') { 'Registry' } else { 'BuiltIn' }) -or
            $lock[$name]['version'] -cne $Dependencies[$name] -or $lock[$name]['source'] -cne $expected['source'] -or
            ($expected['source'] -ceq 'registry' -and ($row['registryUrl'] -cne $expected['url'] -or $lock[$name]['url'] -cne $expected['url']))) {
            throw "The actual official package pin differs: $name"
        }
    }
    foreach ($package in $Packages) {
        Assert-YLFrozenInstalledTarball -Package $package -Row $actual[$package.id] -LockRow $lock[$package.id] -Locator $Dependencies[$package.id] `
            -HostRoot $HostRoot -CacheRoot $CacheRoot
    }
}

function Assert-YLFrozenShippedContent {
    param([Collections.IDictionary] $Build, [ValidateSet('catalog.bin','catalog.json', IgnoreCase = $false)][string] $Catalog,
        [string] $PlayerData, [string] $DeliveryRoot, [string] $Mode)
    $shipped = Get-YLFrozenFileSha256 (Join-Path $PlayerData $Catalog)
    if ($shipped -cne $Build['shippedCatalogSha256']) { throw 'The actual shipped Catalog differs from the Player build.' }
    $descriptionPath = Join-Path $PlayerData 'yl-encrypted-bundles.json'
    if ($Mode -ceq 'Disabled') {
        if ($Build['descriptionSha256'] -or [IO.Path]::Exists($descriptionPath) -or [IO.Path]::Exists($DeliveryRoot)) {
            throw 'The disabled Player shipped an encrypted description or delivery.'
        }
        return
    }
    $description = Read-YLPackageEvidenceJson $descriptionPath -ExpectedSha256 $Build['descriptionSha256']
    $delivery = Read-YLPackageEvidenceJson (Join-Path $DeliveryRoot 'delivery.json') -ExpectedSha256 $Build['deliverySha256']
    $wrong = $Mode -ceq 'WrongRoute'
    if ($description['catalog'] -cne $Catalog -or $description['catalogSha256'] -cne $shipped -or
        $delivery['catalogSha256'] -cne $Build['catalogSha256'] -or
        ($shipped -cne $delivery['catalogSha256']) -ne $wrong -or
        ($Build['descriptionSha256'] -cne $delivery['descriptionSha256']) -ne $wrong) {
        throw 'The Catalog or encrypted description differs from the expected delivery pairing.'
    }
    $paths = @($Build['protectedPaths'])
    $described = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($row in $description['bundles']) {
        if ($row['path'] -isnot [string] -or -not $described.TryAdd($row['path'], $row)) { throw 'Duplicate or invalid protected Bundle description.' }
    }
    if ($paths.Count -eq 0 -or $delivery['files'] -isnot [array] -or $delivery['files'].Count -ne $paths.Count -or
        $described.Count -ne $paths.Count -or @(Get-YLTestTreeFiles $DeliveryRoot).Count -ne ($paths.Count + 1)) {
        throw 'The protected delivery inventory count differs.'
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $total = 0L
    foreach ($row in $delivery['files']) {
        $name = $row['path']
        if ($name -isnot [string] -or -not $name -or $name -cne [IO.Path]::GetFileName($name) -or $name -ceq 'delivery.json' -or
            $name -cnotin $paths -or -not $seen.Add($name) -or -not $described.ContainsKey($name) -or $row['bytes'] -isnot [long] -or
            $row['bytes'] -le 0 -or $row['bytes'] -ne $described[$name]['cipherLength'] -or $row['sha256'] -cnotmatch '^[0-9a-f]{64}$' -or
            $row['sha256'] -cne $described[$name]['cipherSha256']) { throw 'The protected delivery file inventory differs from the shipped description.' }
        $path = Get-YLTestPath (Join-Path $DeliveryRoot $name)
        if ([IO.FileInfo]::new($path).Length -ne $row['bytes'] -or (Get-YLFrozenFileSha256 $path) -cne $row['sha256']) {
            throw "The protected delivery file differs: $name"
        }
        if ($Mode -ceq 'Performance') {
            $clear = Get-YLTestPath (Join-Path ([IO.Path]::GetDirectoryName($DeliveryRoot)) "Private/Packed/$name")
            if ([IO.FileInfo]::new($clear).Length -ne $described[$name]['plainLength'] -or (Get-YLFrozenFileSha256 $clear) -cne $described[$name]['plainSha256']) {
                throw "The plain performance control differs from the original protected Bundle: $name"
            }
        }
        $total += $row['bytes']
    }
    if ($total -ne $Build['protectedBytes']) { throw 'The protected delivery byte count differs.' }
    return $delivery['files']
}
