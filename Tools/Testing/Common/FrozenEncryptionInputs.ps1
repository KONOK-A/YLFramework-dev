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

function Assert-YLFrozenDeliveryFields([object] $Row, [string[]] $Fields, [string] $Label) {
    if ($Row -isnot [Collections.IDictionary] -or $Row.psbase.Count -ne $Fields.Count) { throw "Invalid $Label fields." }
    foreach ($entry in $Row.GetEnumerator()) {
        if ([Array]::IndexOf($Fields, $entry.Key) -lt 0) { throw "Unexpected $Label field: $($entry.Key)" }
    }
}

function Test-YLFrozenDeliveryBundleLeaf([object] $Value) {
    return ($Value -is [string] -and $Value.Length -le 255 -and $Value -cmatch '\A[A-Za-z0-9][A-Za-z0-9_.-]*\.bundle\z' -and
        $Value -notmatch '\A(?:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\.')
}

function Assert-YLFrozenDeliveryFile([string] $Path, [long] $Bytes, [string] $Sha256, [string] $Label) {
    $actual = [IO.File]::OpenRead((Get-YLTestPath $Path))
    try {
        if ($actual.Length -ne $Bytes -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($actual)) -ine $Sha256) {
            throw "$Label differs from the expected raw bytes."
        }
    } finally { $actual.Dispose() }
}

function Assert-YLFrozenShippedContent {
    [CmdletBinding()]
    param([Collections.IDictionary] $Build, [ValidateSet('catalog.bin','catalog.json', IgnoreCase = $false)][string] $Catalog,
        [string] $PlayerData, [string] $DeliveryRoot, [string] $Mode, [string] $Application, [string] $ReleaseId, [string] $LoopbackPrefix,
        [ValidateSet('Remote','Embedded','Mixed', IgnoreCase = $false)][string] $DeliveryLayout = 'Remote', [string] $OrdinaryRoot)

    $PlayerData = Get-YLTestPath $PlayerData
    $DeliveryRoot = Get-YLTestPath $DeliveryRoot
    $descriptionPath = Join-Path $PlayerData 'yl-encrypted-bundles.json'
    if ($Mode -ceq 'Disabled') {
        if ($Build['descriptionSha256'] -or $Build['deliverySha256'] -or [IO.Path]::Exists($descriptionPath) -or
            [IO.Path]::Exists((Join-Path $PlayerData 'yl-encrypted')) -or [IO.Path]::Exists($DeliveryRoot)) {
            throw 'The disabled Player shipped an encrypted description or delivery.'
        }
        if ((Get-YLFrozenFileSha256 (Join-Path $PlayerData $Catalog)) -cne $Build['shippedCatalogSha256']) {
            throw 'The actual shipped Catalog differs from the Player build.'
        }
        return
    }

    $token = '\A[A-Za-z0-9][A-Za-z0-9_.-]{0,255}\z'
    $hex32 = '\A[0-9a-f]{32}\z'
    $hex64 = '\A[0-9a-f]{64}\z'
    $ordinal = [StringComparer]::Ordinal
    $windowsNames = [StringComparer]::OrdinalIgnoreCase
    if ($Application -cnotmatch $token -or $ReleaseId -cnotmatch $token -or -not $ordinal.Equals([IO.Path]::GetFileName($DeliveryRoot), $ReleaseId)) {
        throw 'Expected application and releaseId for the final delivery directory.'
    }
    if ($LoopbackPrefix -cnotmatch '\Ahttp://127\.0\.0\.1:([1-9][0-9]{0,4})/\z' -or [int]$Matches[1] -gt 65535 -or
        ([uri]$LoopbackPrefix).AbsoluteUri -cne $LoopbackPrefix) { throw 'Expected the canonical fixture loopback prefix.' }
    $prefix = if ($DeliveryLayout -cne 'Embedded') { "${LoopbackPrefix}r/$ReleaseId/" }
    $aa = 'Player/P_Data/StreamingAssets/aa'
    if (-not $windowsNames.Equals($PlayerData, (Get-YLTestPath (Join-Path $DeliveryRoot $aa)))) {
        throw 'Verify the pristine Player from the final release, not a test copy.'
    }

    foreach ($field in 'catalogSha256', 'shippedCatalogSha256', 'settingsSha256', 'routesSha256', 'descriptionSha256', 'deliverySha256') {
        if ($Build[$field] -isnot [string] -or $Build[$field] -cnotmatch $hex64) { throw "Invalid Build identity: $field" }
    }
    if ($Build['catalogSha256'] -cne $Build['shippedCatalogSha256']) { throw 'Catalog identities differ in the pristine Build.' }
    $identity = @{ application = $Application; releaseId = $ReleaseId; releasePrefix = $prefix; platform = 'StandaloneWindows64'; catalog = $Catalog
        catalogSha256 = $Build['catalogSha256']; settingsSha256 = $Build['settingsSha256']; routesSha256 = $Build['routesSha256'] }
    $fields = @('version') + @($identity.Keys)
    $delivery = Read-YLPackageEvidenceJson (Join-Path $DeliveryRoot 'delivery.json') -ExpectedSha256 $Build['deliverySha256']
    if ([IO.FileInfo]::new((Get-YLTestPath $descriptionPath)).Length -gt 4MB) { throw 'The shipped description exceeds its size bound.' }
    $description = Read-YLPackageEvidenceJson $descriptionPath -ExpectedSha256 $Build['descriptionSha256']
    Assert-YLFrozenDeliveryFields $delivery ($fields + @('descriptionSha256', 'files', 'uploads')) 'delivery receipt'
    Assert-YLFrozenDeliveryFields $description ($fields + @('format', 'providerId', 'bundles')) 'encrypted description'
    foreach ($record in $delivery, $description) {
        if ($record['version'] -isnot [long] -or $record['version'] -ne 2L) { throw 'Expected frozen delivery version 2.' }
        foreach ($field in $identity.Keys) {
            $value = $record[$field]
            if (($null -eq $identity[$field] -and $null -ne $value) -or ($null -ne $identity[$field] -and
                ($value -isnot [string] -or -not $ordinal.Equals($value, $identity[$field])))) { throw "The delivery $field differs from the expected identity." }
        }
    }
    if ($delivery['descriptionSha256'] -isnot [string] -or -not $ordinal.Equals($delivery['descriptionSha256'], $Build['descriptionSha256']) -or
        $delivery['files'] -isnot [array] -or $delivery['uploads'] -isnot [array]) { throw 'The delivery receipt description identity or inventory differs.' }
    if ($description['format'] -isnot [string] -or -not $ordinal.Equals($description['format'], 'AES256_GCM_HKDF_1MB') -or
        $description['providerId'] -isnot [string] -or -not $ordinal.Equals($description['providerId'], 'YLFramework.Resource.Encryption.EncryptedAssetBundleProvider') -or
        $description['bundles'] -isnot [array] -or $description['bundles'].Count -lt 1 -or $description['bundles'].Count -gt 4096) {
        throw 'Invalid v2 encrypted description.'
    }

    $files = [Collections.Generic.Dictionary[string, object]]::new($windowsNames)
    foreach ($row in $delivery['files']) {
        Assert-YLFrozenDeliveryFields $row @('path', 'bytes', 'sha256') 'delivery file'
        $name = $row['path']
        if ($name -isnot [string] -or $name -cnotmatch '\A(?:Player|Upload|ContentState)(?:/[^/\\:<>"|?*\x00-\x1f]+)+\z' -or
            @($name.Split('/') | Where-Object { $_ -match '[. ]\z|\A(?:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|\z)' }).Count) {
            throw "Invalid release-relative file path: $name"
        }
        if ($row['bytes'] -isnot [long] -or $row['bytes'] -lt 0 -or $row['sha256'] -isnot [string] -or $row['sha256'] -cnotmatch $hex64) {
            throw "Invalid delivery file identity: $name"
        }
        if (-not $files.TryAdd($name, $row)) { throw "Collision in delivery file inventory: $name" }
    }
    $archive = @('ContentState/addressables_content_state.bin', 'ContentState/buildlayout.json')
    $metadata = @{ "$aa/$Catalog" = $Build['catalogSha256']; "$aa/settings.json" = $Build['settingsSha256']
        "$aa/yl-encrypted-bundles.json" = $Build['descriptionSha256'] }
    foreach ($name in @('Player/P.exe') + $archive + @($metadata.Keys)) {
        if (-not $files.ContainsKey($name) -or -not $ordinal.Equals($files[$name]['path'], $name) -or $files[$name]['bytes'] -le 0 -or
            ($metadata.ContainsKey($name) -and $files[$name]['sha256'] -cne $metadata[$name])) {
            throw "The required delivery file inventory differs: $name"
        }
    }

    $protected = [Collections.Generic.List[object]]::new()
    $paths = [Collections.Generic.HashSet[string]]::new($ordinal)
    $embedded = [Collections.Generic.HashSet[string]]::new($ordinal)
    $leaves = [Collections.Generic.HashSet[string]]::new($windowsNames)
    $names = [Collections.Generic.HashSet[string]]::new($windowsNames)
    $digests = [Collections.Generic.HashSet[string]]::new($ordinal)
    $groups = @{}
    $modes = [Collections.Generic.HashSet[string]]::new($ordinal)
    $uploadRoutes = [Collections.Generic.Dictionary[string, object]]::new($ordinal)
    $embeddedBase = "$aa/yl-encrypted/"
    [long] $total = 0
    foreach ($row in $description['bundles']) {
        Assert-YLFrozenDeliveryFields $row @('mode','groupGuid','bundleName','bundleHash','path','keyId','plainLength','plainSha256','cipherLength','cipherSha256') 'protected Bundle'
        $leaf = $row['path']
        $guid = $row['groupGuid']
        $deliveryMode = $row['mode']
        $plain = $row['plainLength']
        if (-not (Test-YLFrozenDeliveryBundleLeaf $leaf) -or $deliveryMode -isnot [string] -or $deliveryMode -cnotmatch '\A(?:RemoteHttps|Embedded)\z' -or
            $guid -isnot [string] -or $guid -cnotmatch $hex32 -or $row['bundleHash'] -isnot [string] -or $row['bundleHash'] -cnotmatch $hex32 -or
            $row['bundleName'] -isnot [string] -or $row['bundleName'] -cnotmatch $token -or $row['keyId'] -isnot [string] -or $row['keyId'] -cnotmatch $token -or
            $row['plainSha256'] -isnot [string] -or $row['plainSha256'] -cnotmatch $hex64 -or $row['cipherSha256'] -isnot [string] -or
            $row['cipherSha256'] -cnotmatch $hex64 -or $plain -isnot [long] -or $plain -le 0 -or $plain -gt (4294967296L * 1048560L - 40L) -or
            $row['cipherLength'] -isnot [long]) { throw "Invalid protected Bundle record: $guid/$leaf" }
        # A 40-byte stream header and a 16-byte tag per 1 MiB segment.
        $cipherLength = $plain + 40L + 16L * (1L + [Math]::Floor([decimal]($plain + 39L) / 1048560L))
        if ($row['cipherLength'] -ne $cipherLength -or -not $leaves.Add($leaf) -or -not $names.Add($row['bundleName']) -or
            -not $digests.Add($row['cipherSha256']) -or ($groups.ContainsKey($guid) -and
            ($groups[$guid]['mode'] -cne $deliveryMode -or $groups[$guid]['keyId'] -cne $row['keyId']))) {
            throw "Conflicting protected Bundle identity or ciphertext length: $guid/$leaf"
        }
        $groups[$guid] = $row
        $groupPath = "$guid/$leaf"
        $null = $paths.Add($groupPath)
        $null = $modes.Add($deliveryMode)
        if ($deliveryMode -ceq 'RemoteHttps') {
            $name = "Upload/protected/$groupPath"
            $uploadRoutes.Add($name, @{ url = $prefix + $groupPath; category = 'protected' })
        } else {
            $name = $embeddedBase + $groupPath
            $null = $embedded.Add($name)
        }
        if (-not $files.ContainsKey($name) -or -not $ordinal.Equals($files[$name]['path'], $name) -or $files[$name]['bytes'] -ne $row['cipherLength'] -or
            $files[$name]['sha256'] -cne $row['cipherSha256']) { throw "The protected delivery file inventory differs: $name" }
        if ($Mode -ceq 'Performance') {
            $clear = Join-Path ([IO.Path]::GetDirectoryName($DeliveryRoot)) "Private/Packed/$groupPath"
            Assert-YLFrozenDeliveryFile $clear $plain $row['plainSha256'] "The original protected Bundle $groupPath"
        }
        $total += $row['cipherLength']
        $protected.Add($files[$name])
    }
    $expectedModes = switch -CaseSensitive ($DeliveryLayout) { Remote { 'RemoteHttps' } Embedded { 'Embedded' } Mixed { 'RemoteHttps'; 'Embedded' } }
    if (-not $modes.SetEquals([string[]]$expectedModes)) { throw 'The protected delivery modes differ from the selected layout.' }
    if ($Build['protectedPaths'] -isnot [array] -or $Build['protectedPaths'].Count -ne $paths.Count -or
        $Build['protectedBytes'] -isnot [long] -or $Build['protectedBytes'] -ne $total) { throw 'The Build protected inventory differs.' }
    foreach ($path in $Build['protectedPaths']) {
        if ($path -isnot [string] -or -not $paths.Remove($path)) { throw "The Build protected path differs: $path" }
    }

    # The original official ordinary outputs, not the upload tree, establish completeness and bytes.
    $OrdinaryRoot = Get-YLTestPath $OrdinaryRoot
    if ($windowsNames.Equals($OrdinaryRoot, $DeliveryRoot) -or $OrdinaryRoot.StartsWith($DeliveryRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $DeliveryRoot.StartsWith($OrdinaryRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $Build['ordinaryPaths'] -isnot [array]) { throw 'Expected an independent ordinary source root and Build inventory.' }
    $ordinary = [Collections.Generic.HashSet[string]]::new($ordinal)
    if ([IO.Path]::Exists($OrdinaryRoot)) {
        if (-not [IO.Directory]::Exists($OrdinaryRoot)) { throw 'The ordinary source root is not a directory.' }
        foreach ($file in Get-YLTestTreeFiles $OrdinaryRoot) {
            $leaf = [IO.Path]::GetRelativePath($OrdinaryRoot, $file)
            if (-not (Test-YLFrozenDeliveryBundleLeaf $leaf) -or -not $ordinary.Add($leaf)) { throw "Unexpected ordinary source path: $leaf" }
        }
    }
    foreach ($leaf in $Build['ordinaryPaths']) {
        if (-not (Test-YLFrozenDeliveryBundleLeaf $leaf) -or -not $ordinary.Remove($leaf) -or -not $leaves.Add($leaf)) {
            throw "Missing or colliding ordinary source Bundle: $leaf"
        }
        $name = "Upload/ordinary/$leaf"
        if (-not $files.ContainsKey($name) -or -not $ordinal.Equals($files[$name]['path'], $name) -or $files[$name]['bytes'] -le 0) {
            throw "Missing ordinary delivery file: $leaf"
        }
        Assert-YLFrozenDeliveryFile (Join-Path $OrdinaryRoot $leaf) $files[$name]['bytes'] $files[$name]['sha256'] "The ordinary source Bundle $leaf"
        $uploadRoutes.Add($name, @{ url = "${LoopbackPrefix}o/$leaf"; category = 'ordinary' })
    }
    if ($ordinary.Count) { throw 'The Build ordinary inventory omits official source files.' }

    $otherCatalog = if ($Catalog -ceq 'catalog.bin') { "$aa/catalog.json" } else { "$aa/catalog.bin" }
    foreach ($name in $files.Keys) {
        if (($name.StartsWith('Upload/', [StringComparison]::Ordinal) -and -not $uploadRoutes.ContainsKey($name)) -or
            ($name.StartsWith('ContentState/', [StringComparison]::Ordinal) -and [Array]::IndexOf($archive, $name) -lt 0) -or
            ($name.StartsWith($embeddedBase, [StringComparison]::OrdinalIgnoreCase) -and -not $embedded.Contains($name)) -or
            $windowsNames.Equals($name, $otherCatalog)) { throw "Unexpected delivery file inventory path: $name" }
    }
    foreach ($row in $delivery['uploads']) {
        Assert-YLFrozenDeliveryFields $row @('path','url','category','bytes','sha256') 'delivery upload'
        $name = $row['path']
        if ($name -isnot [string] -or -not $uploadRoutes.ContainsKey($name) -or $row['bytes'] -isnot [long] -or
            $row['bytes'] -ne $files[$name]['bytes'] -or $row['sha256'] -isnot [string] -or -not $ordinal.Equals($row['sha256'], $files[$name]['sha256'])) {
            throw "The delivery upload inventory differs: $name"
        }
        if ($row['category'] -isnot [string] -or -not $ordinal.Equals($row['category'], $uploadRoutes[$name]['category'])) { throw "Wrong upload category: $name" }
        if ($row['url'] -isnot [string] -or -not $ordinal.Equals($row['url'], $uploadRoutes[$name]['url'])) { throw "Wrong upload URL: $name" }
        $null = $uploadRoutes.Remove($name)
    }
    if ($uploadRoutes.Count) { throw "Missing required upload routes: $($uploadRoutes.Keys -join ', ')" }

    # Publication contains only file parents and the required, possibly empty, Upload directory.
    $directories = [Collections.Generic.Dictionary[string, string]]::new($windowsNames)
    $directories.Add('Upload', 'Upload')
    foreach ($name in $files.Keys) {
        for ($slash = $name.LastIndexOf('/'); $slash -ge 0; $slash = $directory.LastIndexOf('/')) {
            $directory = $name.Substring(0, $slash)
            if ($files.ContainsKey($directory)) { throw "File and directory path collision: $directory" }
            if ($directories.TryAdd($directory, $directory)) { continue }
            if (-not $ordinal.Equals($directories[$directory], $directory)) { throw "Directory path collision: $directory" }
            break
        }
    }
    $pending = [Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    $pending.Push([IO.DirectoryInfo]::new($DeliveryRoot))
    $receiptSeen = $false
    $count = 0
    while ($pending.Count) {
        foreach ($entry in $pending.Pop().EnumerateFileSystemInfos()) {
            $name = [IO.Path]::GetRelativePath($DeliveryRoot, $entry.FullName).Replace('\', '/')
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked path in the delivery inventory: $name" }
            if ($entry -is [IO.DirectoryInfo]) {
                if (-not $directories.ContainsKey($name) -or -not $ordinal.Equals($directories[$name], $name)) { throw "Unexpected directory in the delivery inventory: $name" }
                $null = $directories.Remove($name)
                $pending.Push($entry)
                continue
            }
            if ($ordinal.Equals($name, 'delivery.json')) { $receiptSeen = $true; continue }
            if (-not $files.ContainsKey($name) -or -not $ordinal.Equals($files[$name]['path'], $name)) { throw "Unlisted path in the actual delivery inventory: $name" }
            Assert-YLFrozenDeliveryFile $entry.FullName $files[$name]['bytes'] $files[$name]['sha256'] "The shipped file $name"
            $count++
        }
    }
    if (-not $receiptSeen -or $directories.Count -or $count -ne $files.Count) { throw 'The actual delivery inventory differs from the receipt.' }
    return $protected.ToArray()
}
