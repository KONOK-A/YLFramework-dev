#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param([string] $TemporaryRoot = [IO.Path]::GetTempPath())
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../Packaging/Build-YLRegistryPackages.ps1')
. (Join-Path $PSScriptRoot '../UnityHost.ps1')
. (Join-Path $PSScriptRoot '../Common/FrozenPlayerEvidence.ps1')
$common = Join-Path $PSScriptRoot '../Common/FrozenEncryptionInputs.ps1'
if (-not [IO.File]::Exists($common)) { throw 'The frozen encryption acceptance cannot verify a fixed-commit release manifest.' }
. $common

$repo = Get-YLTestPath ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')))
$parent = Get-YLTestPath $TemporaryRoot
if (-not [IO.Directory]::Exists($parent)) { throw 'TemporaryRoot must exist.' }
$root = Join-Path $parent ('yl-frozen-contracts-' + [guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($root)
$catalog = Read-YLPackageCatalog
$packageRoot = Join-Path $repo 'PackageSources'
$head = & git.exe --no-optional-locks -C $repo rev-parse --verify HEAD
if ($LASTEXITCODE -ne 0 -or $head -cnotmatch '^[0-9a-f]{40}$') { throw 'Expected a Git checkout for the frozen input contract.' }
$main = 'com.kano.ylframework'
$extension = "$main.resource-encryption"
$dll = 'Runtime/Plugins/Win64/ylresource_tink.dll'
$checks = 0

function Check([bool] $Pass, [string] $Message) {
    if (-not $Pass) { throw $Message }
    $script:checks++
}
function Reject([scriptblock] $Operation, [string] $Message) {
    $failure = $null
    try { $null = & $Operation } catch { $failure = $_ }
    Check ($null -ne $failure -and $failure.Exception -isnot [Management.Automation.CommandNotFoundException] -and
        $failure.Exception.Message -match $Message) "Expected $Message, got $failure"
}
function Reject-Parameter([scriptblock] $Operation, [string] $Name) {
    $parameter = $null
    try { $null = & $Operation } catch [Management.Automation.ParameterBindingException] { $parameter = $_.Exception.ParameterName }
    Check ($parameter -ceq $Name) "Expected param binding to reject $Name before the runner starts."
}
function New-Archive($Package, [string] $Path) {
    $stream = [IO.File]::Open($Path, 'CreateNew', 'Write', 'None')
    try {
        $gzip = [IO.Compression.GZipStream]::new($stream, [IO.Compression.CompressionLevel]::Fastest, $true)
        try {
            $tar = [System.Formats.Tar.TarWriter]::new($gzip, [System.Formats.Tar.TarEntryFormat]::Pax, $true)
            try {
                foreach ($file in $Package.files) {
                    $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, ('package/' + $file.path))
                    $payload = [IO.File]::OpenRead((Join-Path $Package.root $file.path))
                    try { $entry.DataStream = $payload; $tar.WriteEntry($entry) } finally { $payload.Dispose() }
                }
            } finally { $tar.Dispose() }
        } finally { $gzip.Dispose() }
    } finally { $stream.Dispose() }
}
function Save-Variant([string] $Name, [scriptblock] $Change) {
    $copy = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($manifest)) -AsHashtable -Depth 40 -DateKind String
    & $Change $copy
    $file = Join-Path $root "$Name.json"
    Write-YLTestJson -Path $file -Value $copy
    return $file
}

$sources = @(Get-YLRegistryPackageSet -Root $packageRoot -Catalog $catalog)
$packed = @(foreach ($package in $sources) {
    $path = Join-Path $root "$($package.id)-$($package.version).tgz"
    New-Archive $package $path
    Read-YLRegistryTarball -Path $path -Catalog $catalog
})
$manifest = Join-Path $root 'release-manifest.json'
$spec = New-YLRegistryReleaseManifest -SourceCommit $head -Toolchain @{ node = '22.23.3'; npm = '10.9.9'; powershell = '7.6.0' } -Packages $packed -Catalog $catalog
Write-YLTestJson -Path $manifest -Value $spec
# These are deliberately not asserted to be artifacts of the current Git commit. The strict entry also checks HEAD and cleanliness.
$release = Read-YLFrozenEncryptionRelease -Path $manifest -Repository $repo -SourceCommit $head
Check ($release.sourceCommit -ceq $head -and $release.packages.Count -eq $sources.Count -and $release.producer.kind -ceq 'Local') 'The complete test manifest did not pass semantic validation.'
Check ((@(Get-YLFrozenEncryptionClosure -Release $release).id -join ';') -ceq "$main;$extension" -and
    @(Get-YLFrozenEncryptionClosure -Release $release -Removed).Count -eq 1) 'The installed/removed own-package closure differs.'

$missing = Save-Variant 'missing' { param($doc) $doc.packages = @($doc.packages | Select-Object -Skip 1) }
Reject { Read-YLFrozenEncryptionRelease $missing $repo $head } 'complete catalog'
$duplicate = Save-Variant 'duplicate' { param($doc) $doc.packages[1] = $doc.packages[0] }
Reject { Read-YLFrozenEncryptionRelease $duplicate $repo $head } 'identity|duplicate'
$escaped = Save-Variant 'escaped' { param($doc) $doc.packages[0].assetName = '../' + $doc.packages[0].assetName }
Reject { Read-YLFrozenEncryptionRelease $escaped $repo $head } 'identity|asset name'
$offCommit = if ($head -ceq ('a' * 40)) { 'b' * 40 } else { 'a' * 40 }
Reject { Read-YLFrozenEncryptionRelease $manifest $repo $offCommit } 'SourceCommit|commit'
Reject { Get-YLFrozenEncryptionRelease $manifest $repo $offCommit } 'HEAD'
$dirty = @(& git.exe --no-optional-locks -C $repo status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) { throw 'Could not observe the worktree.' }
if ($dirty.Count) { Reject { Assert-YLFrozenSourceCommit -Repository $repo -SourceCommit $head } 'clean worktree' }
else {
    $fixed = Get-YLFrozenEncryptionRelease -Path $manifest -Repository $repo -SourceCommit $head
    Check ($fixed.sourceCommit -ceq $head -and $fixed.packages.Count -eq $sources.Count) 'Clean fixed-commit admission did not verify the complete package inventory.'
}
$receipt = Join-Path $root 'fixture-only.json'
Write-YLTestJson $receipt @{ purpose = 'YLResource.LocalEncryptionPack'; formalCandidate = $false; status = 'Packed' }
Reject { Read-YLFrozenEncryptionRelease $receipt $repo $head } 'evidence fields'

$fakeUnity = Join-Path $root 'Unity.exe'
Reject-Parameter { & (Join-Path $PSScriptRoot '../ResourceEncryption/Invoke-FrozenDelivery.ps1') -Profile Unity6 -Mode positive -Unity $fakeUnity } 'Mode'
Reject-Parameter { & (Join-Path $PSScriptRoot '../Invoke-P0NativeDelivery.ps1') -Profile unity6 -Unity $fakeUnity -WorkspaceRoot $root -PackReceipt $receipt } 'Profile'

function Test-LockedDeliveryKey([switch] $BuildFails) {
    $state = @{ host = $null; result = $null; key = $null }
    function Get-DeliveryFixtureProfile($Editors, $VersionInfo) {
        $Editors | Where-Object productVersion -CEQ '2022.3.62f3_96770f904ca7'
    }
    function Invoke-DeliveryFixtureProcess($StartInfo, $Log, $TimeoutMilliseconds) {
        $state.host = $StartInfo.WorkingDirectory
        $state.result = [IO.Path]::GetDirectoryName($Log)
        $state.key = [IO.File]::Open((Join-Path $state.host 'fixture.key'), 'Open', 'Read', 'Read')
        if ($BuildFails) { throw 'Injected build failure.' }
        $request = Join-Path $state.host 'fixture.json'
        $job = Read-YLPackageEvidenceJson $request
        $foreign = [IO.Directory]::CreateDirectory((Join-Path $state.host 'ForeignOutput')).FullName
        [IO.File]::WriteAllText((Join-Path $foreign 'must-remain'), 'unchanged')
        Write-YLTestJson $job.output @{ status = 'Rejected'; error = 'Official Addressables output path'; pid = 123;
            token = $job.token; mode = $job.mode; input = $job.input; unity = '2022.3.62f3'; addressables = $job.addressables;
            source = 'Registry'; encryptionSource = 'Local'; mainSource = 'Local'; requestSha256 = Get-YLFrozenFileSha256 $request;
            manifestSha256 = Get-YLFrozenFileSha256 (Join-Path $state.host 'Packages/manifest.json') }
        [pscustomobject]@{ status = 'Exited'; pid = 123; exitCode = 0; activeProcesses = 0; observedExit = $true;
            jobAssigned = $true; streamsDrained = $true; timedOut = $false; cancelled = $false; forcedCleanup = $false;
            cleanupError = $null; observerError = $null }
    }
    Set-Alias -Name Get-YLTestEditorProfile -Value Get-DeliveryFixtureProfile -Scope Local
    Set-Alias -Name Invoke-YLTestProcess -Value Invoke-DeliveryFixtureProcess -Scope Local
    $workspaceRoot = Join-Path $root 'DeliveryRunner'
    $expectedError = if ($BuildFails) { '(?s)Injected build failure\..*fixture\.key' } else { 'fixture\.key' }
    try {
        Reject { & (Join-Path $PSScriptRoot '../ResourceEncryption/Invoke-FrozenDelivery.ps1') -Profile Unity2022 -Unity $fakeUnity `
            -WorkspaceRoot $workspaceRoot -Backend Mono -Mode OfficialOutput -KeepArtifacts:$BuildFails } $expectedError
        $terminal = Join-Path $state.result 'frozen.json'
        Check ([IO.File]::Exists($terminal)) 'Key deletion failure suppressed the terminal report.'
        $summary = Read-YLPackageEvidenceJson $terminal
        Check ($summary.status -ceq 'Failed' -and $summary['keyCleanupError'] -match 'fixture\.key') 'Terminal evidence did not record the key cleanup failure.'
        if ($BuildFails) { Check ($summary.failure -ceq 'Injected build failure.') 'Key cleanup failure replaced the original build failure.' }
        else { Check ($summary.build.status -ceq 'Rejected' -and -not $summary.Contains('failure')) 'The solitary cleanup failure did not follow a valid control build.' }
        $context = Read-YLPackageEvidenceJson (Join-Path $state.result 'host-context.json')
        Check ($context.hostId -ceq $summary.hostId -and $context.resultId -ceq $summary.resultId -and
            $context.keepArtifacts -eq [bool]$BuildFails) 'The pre-build host context or retention switch was lost.'
        $identity = Read-YLPackageEvidenceJson (Join-Path $state.result 'input-identity.json')
        Check ($identity.sha256 -ceq $summary.inputHash -and $identity.files.Count -gt 0 -and $identity.toolchain.Count -ge 3) 'The complete Frozen input identity was not retained.'
        Check (@(Get-YLTestTreeFiles $state.result | Where-Object { [IO.Path]::GetFileName($_) -ceq 'fixture.key' }).Count -eq 0) 'Frozen copied key material to Results.'
    } finally {
        if ($null -ne $state.key) {
            $keyPath = $state.key.Name
            $state.key.Dispose()
            $workspace = Initialize-YLTestWorkspace $workspaceRoot $repo
            $lease = Lock-YLTestDirectory $workspace $state.host
            try { [IO.File]::Delete($keyPath) } finally { $lease.Dispose() }
        }
    }
}
$template = 'Data/PlaybackEngines/WindowsStandaloneSupport/Variations/win64_player_nondevelopment_mono'
foreach ($path in $fakeUnity, (Join-Path $root "$template/WindowsPlayer.exe"), (Join-Path $root "$template/MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll")) {
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllText($path, 'No Unity executable is launched by this fixture.')
}
Test-LockedDeliveryKey
Test-LockedDeliveryKey -BuildFails

$tokens = $null; $parseErrors = $null
$runner = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '../Invoke-P0NativeDelivery.ps1'), [ref]$tokens, [ref]$parseErrors)
Check ($parseErrors.Count -eq 0) 'P0 delivery runner does not parse.'
$saveStage = $runner.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -ceq 'Save-P0NativeStageInputs' }
Check ($null -ne $saveStage) 'P0 delivery is missing a verified per-stage snapshot.'
. ([scriptblock]::Create($saveStage.Extent.Text))
$stageHost = [pscustomobject]@{ path = Join-Path $root 'StageHost' }
$stageResult = Join-Path $root 'StageResult'
foreach ($dir in 'P0C','Packages') { $null = [IO.Directory]::CreateDirectory((Join-Path $stageHost.path $dir)) }
[IO.File]::WriteAllText((Join-Path $stageHost.path 'P0C/fixture.key'), 'do not copy')
$fields = [ordered]@{ 'request.json' = 'requestSha256'; 'Packages/manifest.json' = 'manifestSha256'; 'Packages/packages-lock.json' = 'packageLockSha256' }
$expectedStages = @{}
foreach ($stage in 'Installed','Removed') {
    $build = @{}
    $expectedStages[$stage] = $build
    foreach ($file in $fields.Keys) {
        $hostFile = Join-Path $stageHost.path $(if ($file -ceq 'request.json') { "P0C/$stage-request.json" } else { $file })
        [IO.File]::WriteAllText($hostFile, ('{ "fixture": "' + $stage + '/' + $file + '" }' + "`r`n"))
        $build[$fields[$file]] = (Get-FileHash -LiteralPath $hostFile -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $null = Save-P0NativeStageInputs -HostRecord $stageHost -ResultRoot $stageResult -Stage $stage -Build $build
}
foreach ($stage in $expectedStages.Keys) {
    foreach ($file in $fields.Keys) {
        $path = Join-Path $stageResult "$stage/$file"
        Check ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ieq $expectedStages[$stage][$fields[$file]]) "P0 changed or lost the original $stage $file bytes."
    }
}
Check (@(Get-YLTestTreeFiles $stageResult).Count -eq 6) 'P0 must retain only the three allowlisted inputs from each stage.'
foreach ($field in $fields.Values) {
    $wrong = $expectedStages.Removed.Clone(); $wrong[$field] = '0' * 64
    Reject { Save-P0NativeStageInputs -HostRecord $stageHost -ResultRoot (Join-Path $root "bad-$field") -Stage Removed -Build $wrong } 'SHA-256'
}

Check ($null -ne (Get-Command Assert-YLFrozenCommittedFiles -ErrorAction SilentlyContinue)) 'Fixed input admission does not verify actual committed Git objects.'
$tracked = 'Tools/Testing/TestSuites.json'
$lfsPath = 'PackageSources/com.kano.ylframework.excel/Editor/Plugins/MiniExcel.dll'
$gitFiles = @($tracked,$lfsPath) | ForEach-Object { @{ path = $_; sha256 = Get-YLFrozenFileSha256 (Join-Path $repo $_) } }
Assert-YLFrozenCommittedFiles -Repository $repo -SourceCommit $head -Files $gitFiles -RequireLfs $lfsPath
Reject { Assert-YLFrozenCommittedFiles $repo $head @(@{ path = $tracked; sha256 = '0' * 64 }) } 'input bytes changed'
Reject { Assert-YLFrozenCommittedFiles $repo $head @(@{ path = 'Tools/Testing/NotCommitted.fixture'; sha256 = '0' * 64 }) } 'no regular committed blob'
Reject { Assert-YLFrozenCommittedFiles $repo $head $gitFiles -RequireLfs $tracked } 'required committed Git LFS pointer'
Reject { Assert-YLFrozenCommittedFiles $repo $head $gitFiles -FullPackageInventory } 'omits committed package input'
$noObjects = Join-Path $root 'empty-lfs'; $null = [IO.Directory]::CreateDirectory($noObjects)
$pluginOid = $gitFiles[1].sha256
Reject { Assert-YLFrozenLfsObject -MediaRoot $noObjects -Oid $pluginOid -Size 200192 } 'LFS object is missing'

$ext = @($release.packages | Where-Object id -CEQ $extension)[0]
$artifact = Get-YLFrozenEncryptionPlugin -Repository $repo -Package $ext
foreach ($name in @($dll, "$dll.meta")) {
    $row = @($ext.files | Where-Object path -CEQ $name)[0]
    $original = $row.sha256
    try {
        $row.sha256 = '0' * 64
        Reject { Get-YLFrozenEncryptionPlugin -Repository $repo -Package $ext } 'plugin|importer'
    } finally { $row.sha256 = $original }
}
Check ($artifact.sha256 -ceq (Get-FileHash -LiteralPath (Join-Path $packageRoot "$extension/$dll") -Algorithm SHA256).Hash.ToLowerInvariant()) 'Native plugin identity has drifted.'

$expectedArchive = [IO.File]::ReadAllBytes($ext.path)
$alternate = Join-Path $root 'alternate.tgz'
$extSource = @($sources | Where-Object id -CEQ $extension)[0]
New-Archive $extSource $alternate
$alternatePayload = Read-YLRegistryTarball -Path $alternate -Catalog $catalog
Check ($alternatePayload.id -ceq $ext.id -and $alternatePayload.version -ceq $ext.version -and $alternatePayload.sha256 -cne $ext.sha256) 'Expected a different valid archive carrying the same package identity.'
try {
    [IO.File]::Copy($alternate, $ext.path, $true)
    Reject { Read-YLFrozenEncryptionRelease $manifest $repo $head } 'tarball identity/integrity/payload'
    Reject { Get-YLFrozenTarballBytes $ext } 'archive changed'
} finally { [IO.File]::WriteAllBytes($ext.path, $expectedArchive) }

$manifestBytes = [IO.File]::ReadAllBytes($manifest)
try {
    [IO.File]::AppendAllText($manifest, "`n")
    Reject { Assert-YLFrozenReleaseUnchanged -Release $release } 'manifest changed'
} finally { [IO.File]::WriteAllBytes($manifest, $manifestBytes) }
Assert-YLFrozenReleaseUnchanged -Release $release

$hostRoot = Join-Path $root 'Host'; $cache = Join-Path $root 'Cache'
$installed = Join-Path $hostRoot "Library/PackageCache/$extension@installed"
$null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($installed))
$null = [IO.Directory]::CreateDirectory($cache)
$null = Read-YLRegistryTarball -Path $ext.path -Destination $installed -Catalog $catalog
$observed = @{ name = $extension; version = $ext.version; source = 'LocalTarball'; assetPath = "Packages/$extension"; isDirectDependency = $true;
    packageId = "$extension@file:../Tarballs/$($ext.assetName)"; resolvedPath = $installed }
$locator = 'file:../Tarballs/' + $ext.assetName
$lock = @{ version = $locator; source = 'local-tarball'; depth = 0L; dependencies = $ext.dependencies }
$ownedArchive = Join-Path $hostRoot ('Tarballs/' + $ext.assetName)
$null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($ownedArchive))
[IO.File]::Copy($ext.path, $ownedArchive)
$proof = Assert-YLFrozenInstalledTarball -Package $ext -Row $observed -LockRow $lock -Locator $locator -HostRoot $hostRoot -CacheRoot $cache
Check ($proof.payloadSha256 -ceq $ext.payloadSha256) 'Installed archive content was not verified.'
$installedManifest = Join-Path $installed 'package.json'
$installedBytes = [IO.File]::ReadAllBytes($installedManifest)
$json = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($installedManifest)) -AsHashtable -DateKind String
try {
    $json['_fingerprint'] = 'a' * 32
    [IO.File]::WriteAllText($installedManifest, (ConvertTo-Json -InputObject $json -Depth 20))
    $proof = Assert-YLFrozenInstalledTarball $ext $observed $lock $locator $hostRoot $cache
    Check ($proof.payloadSha256 -ceq $ext.payloadSha256) 'The allowed UPM tarball fingerprint changed the semantic inventory.'
    $json['displayName'] = 'Different payload with the same version'
    [IO.File]::WriteAllText($installedManifest, (ConvertTo-Json -InputObject $json -Depth 20))
    Reject { Assert-YLFrozenInstalledTarball $ext $observed $lock $locator $hostRoot $cache } 'payload|inventory'
} finally { [IO.File]::WriteAllBytes($installedManifest, $installedBytes) }
$wrongSource = $observed.Clone(); $wrongSource.source = 'Local'
Reject { Assert-YLFrozenInstalledTarball $ext $wrongSource $lock $locator $hostRoot $cache } 'source|locator'
$wrongSource = $observed.Clone(); $wrongSource.packageId = "$extension@file:../Tarballs/imposter.tgz"
Reject { Assert-YLFrozenInstalledTarball $ext $wrongSource $lock $locator $hostRoot $cache } 'source|locator'
$absoluteSource = $observed.Clone()
$absoluteSource.packageId = "$extension@file:$ownedArchive"
$proof = Assert-YLFrozenInstalledTarball $ext $absoluteSource $lock $locator $hostRoot $cache
Check ($proof.archivePath -ieq $ownedArchive) 'Unity 6 did not bind the absolute PackageInfo locator to the owned archive.'
$absoluteSource.packageId = "$extension@file:$($ext.path)"
Reject { Assert-YLFrozenInstalledTarball $ext $absoluteSource $lock $locator $hostRoot $cache } 'source|locator'
try {
    [IO.File]::Copy($alternate, $ownedArchive, $true)
    Reject { Assert-YLFrozenInstalledTarball $ext $observed $lock $locator $hostRoot $cache } 'owned installed archive changed'
} finally { [IO.File]::Copy($ext.path, $ownedArchive, $true) }
$wrongRoot = $observed.Clone(); $wrongRoot.resolvedPath = $extSource.root
Reject { Assert-YLFrozenInstalledTarball $ext $wrongRoot $lock $locator $hostRoot $cache } 'outside.*owned'
$installedDll = Join-Path $installed $dll
$saved = [IO.File]::ReadAllBytes($installedDll)
try {
    $changed = [byte[]]$saved.Clone(); $changed[-1] = $changed[-1] -bxor 1
    [IO.File]::WriteAllBytes($installedDll, $changed)
    Reject { Assert-YLFrozenInstalledTarball $ext $observed $lock $locator $hostRoot $cache } 'payload|inventory'
} finally { [IO.File]::WriteAllBytes($installedDll, $saved) }

$mono = @{ backend = 'Mono'; bits = 64; architecture = 'X64'; graphics = 'Direct3D11' }
Assert-YLFrozenPlayerBackend -Row $mono -Expected Mono
Reject { Assert-YLFrozenPlayerBackend -Row $mono -Expected IL2CPP } 'backend evidence differs'
$arm = $mono.Clone(); $arm.architecture = 'Arm64'
Reject { Assert-YLFrozenPlayerBackend -Row $arm -Expected Mono } 'backend evidence differs'
$exe = Join-Path $root 'P.exe'; [IO.File]::WriteAllText($exe, 'not a Player')
Reject { Assert-YLFrozenPlayerBinaries $exe @{ backend = 'Mono'; stripping = 'Disabled'; binaries = @{} } IL2CPP } 'backend, stripping or binary inventory'
$playerRoot = Join-Path $root 'MonoEvidence'
$monoPaths = @('P.exe', 'MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll', 'P_Data/Managed/YLFramework.Runtime.dll')
$nativeFixture = [IO.File]::ReadAllBytes((Join-Path $packageRoot "$extension/$dll"))
$evidence = [ordered]@{}
foreach ($name in $monoPaths) {
    $path = Join-Path $playerRoot $name
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllBytes($path, $nativeFixture)
    $evidence[$name] = Get-YLFrozenFileSha256 $path
}
$monoBuild = @{ backend = 'Mono'; stripping = 'Disabled'; binaries = $evidence }
$monoExe = Join-Path $playerRoot 'P.exe'
Reject { Assert-YLFrozenPlayerBinaries $monoExe $monoBuild Mono } 'backend, stripping or binary inventory'
$probe = 'P_Data/Managed/YLFramework.FrozenEncryption.Fixture.dll'
[IO.File]::WriteAllText((Join-Path $playerRoot $probe), 'frozen probe')
$evidence[$probe] = Get-YLFrozenFileSha256 (Join-Path $playerRoot $probe)
Assert-YLFrozenPlayerBinaries $monoExe $monoBuild Mono
$playerPlugin = Join-Path $playerRoot 'P_Data/Plugins/x86_64/ylresource_tink.dll'
$null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($playerPlugin))
[IO.File]::WriteAllBytes($playerPlugin, $nativeFixture)
$pluginExpected = Get-YLFrozenFileSha256 $playerPlugin
Assert-YLFrozenPlayerBinaries $monoExe $monoBuild Mono -NativePluginSha256 $pluginExpected
$swapped = [IO.File]::Open($playerPlugin, 'Append', 'Write', 'None')
try { $swapped.WriteByte(0) } finally { $swapped.Dispose() }
Check (Test-YLFrozenAmd64 $playerPlugin) 'The substituted plugin fixture must still be a valid Win64 PE.'
Reject { Assert-YLFrozenPlayerBinaries $monoExe $monoBuild Mono -NativePluginSha256 $pluginExpected } 'Player native plugin'
[IO.File]::WriteAllText((Join-Path $playerRoot $probe), 'another build')
Reject { Assert-YLFrozenPlayerBinaries $monoExe $monoBuild Mono } 'binary hash.*FrozenEncryption.Fixture'
$typedEvidence = $mono.Clone(); $typedEvidence.backend = $true; $typedEvidence.architecture = $true; $typedEvidence.bits = '64'
Reject { Assert-YLFrozenPlayerBackend -Row $typedEvidence -Expected Mono } 'backend evidence differs'
$playerData = Join-Path $root 'P_Data/StreamingAssets/aa'
$deliveryRoot = Join-Path $root 'Delivery'; $null = [IO.Directory]::CreateDirectory($deliveryRoot)
$null = [IO.Directory]::CreateDirectory($playerData)
$catalogFile = Join-Path $playerData 'catalog.bin'; [IO.File]::WriteAllText($catalogFile, 'first catalog')
$cipherFile = Join-Path $deliveryRoot 'bundle'; [IO.File]::WriteAllBytes($cipherFile, [byte[]]@(1,2,3,4))
$cipherHash = Get-YLFrozenFileSha256 $cipherFile
$catalogHash = Get-YLFrozenFileSha256 $catalogFile
$descriptionFile = Join-Path $playerData 'yl-encrypted-bundles.json'
Write-YLTestJson $descriptionFile @{ catalog = 'catalog.bin'; catalogSha256 = $catalogHash; bundles = @(@{ path = 'bundle'; cipherLength = 4; cipherSha256 = $cipherHash }) }
$descriptionHash = Get-YLFrozenFileSha256 $descriptionFile
$deliveryFile = Join-Path $deliveryRoot 'delivery.json'
Write-YLTestJson $deliveryFile @{ catalogSha256 = $catalogHash; descriptionSha256 = $descriptionHash; files = @(@{ path = 'bundle'; bytes = 4; sha256 = $cipherHash }) }
$published = @{ catalogSha256 = $catalogHash; shippedCatalogSha256 = $catalogHash; descriptionSha256 = $descriptionHash; deliverySha256 = Get-YLFrozenFileSha256 $deliveryFile;
    protectedPaths = @('bundle'); protectedBytes = 4 }
Check ($null -ne (Get-Command Assert-YLFrozenShippedContent -ErrorAction SilentlyContinue)) 'No shipped bundle/catalog cross-check is implemented.'
$verified = @(Assert-YLFrozenShippedContent -Build $published -Catalog 'catalog.bin' -PlayerData $playerData -DeliveryRoot $deliveryRoot -Mode Positive)
Check ($verified[0].sha256 -ceq $cipherHash) 'The delivery output did not bind the actual descriptor and bytes.'
[IO.File]::WriteAllText($catalogFile, 'different catalog')
Reject { Assert-YLFrozenShippedContent $published 'catalog.bin' $playerData $deliveryRoot Positive } 'Catalog.*differs'
$published.shippedCatalogSha256 = Get-YLFrozenFileSha256 $catalogFile
Reject { Assert-YLFrozenShippedContent $published 'catalog.bin' $playerData $deliveryRoot Positive } 'Catalog.*differs'
[IO.File]::WriteAllText($catalogFile, 'first catalog')
$published.shippedCatalogSha256 = $catalogHash
[IO.File]::WriteAllBytes($cipherFile, [byte[]]@(1,2,3,0))
Reject { Assert-YLFrozenShippedContent $published 'catalog.bin' $playerData $deliveryRoot Positive } 'protected delivery file differs'

$perfContracts = Join-Path $PSScriptRoot '../Common/FrozenPerformanceEvidence.ps1'
Check ([IO.File]::Exists($perfContracts)) 'The performance runner cannot prove three independent warm Player samples.'
. $perfContracts
$scenarioNames = @(Get-YLFrozenPerformanceScenarios)
Check (($scenarioNames -join ',') -ceq ('performance-plain-cold,performance-plain-warm-1,performance-plain-warm-2,performance-plain-warm-3,' +
    'performance-cipher-cold,performance-cipher-warm-1,performance-cipher-warm-2,performance-cipher-warm-3')) 'Unexpected paired performance scenarios.'
$fixtures = @(for ($i = 0; $i -lt $scenarioNames.Count; $i++) {
    $name = $scenarioNames[$i]
    $times = @(10.0, 8.0, 9.0, 1.0, 50.0, 40.0, 22.0, 30.0)
    $cold = $name.EndsWith('-cold', [StringComparison]::Ordinal)
    $samples = if ($cold) { @(@{ bytes = 3L }, @{ bytes = 5L }) } else { @() }
    $encrypted = $i -ge 4
    $metrics = @{ mode = $name; concurrent = $true; elapsedMs = $times[$i]; startGateMs = (0.5 + $i); startGateMainThreadCpuMs = 0.0;
        mainThreadCpuMs = 3.0; largestObservedFrameMs = 0.25; cacheLogicalBytesAtCompletion = 8L;
        cacheLogicalPeak = if ($encrypted) { 8L } else { -1L }; processPrivateBefore = 67L; processPrivatePeak = 89L;
        processWorkingSetBefore = 55L; processWorkingSetPeak = 76L; engineAssetReportedBytes = -1L; enginePeakMemory = 'unavailable';
        fixturePayloadBytes = 136L * 1MB; startGateKind = if ($encrypted) { 'frozen-catalog-and-cipher-cache' } else { 'ordinary-unity-cache-readiness' };
        startGateCipherHashBytes = if (-not $encrypted) { -1L } elseif ($cold) { 0L } else { 8L }; loadCipherHashBytes = if ($encrypted) { 16L } else { -1L };
        bundleReadKind = if ($encrypted) { 'native-measured' } else { 'engine-opaque' };
        bundles = @(foreach ($bytes in 3L, 5L) { @{ clearBytes = $bytes; cipherBytes = $bytes;
            readPlain = if ($encrypted) { 2 * $bytes } else { -1L }; readCipher = if ($encrypted) { 3 * $bytes } else { -1L };
            pages = if ($encrypted) { 1L } else { -1L }; payloadBufferBound = if ($encrypted) { 3MB } else { -1L };
            preAuthenticationPlainBytes = if ($encrypted) { $bytes } else { -1L }; preAuthenticationCipherBytes = if ($encrypted) { 2 * $bytes } else { -1L };
            engineReadPlain = if ($encrypted) { $bytes } else { -1L }; engineReadCipher = if ($encrypted) { $bytes } else { -1L };
            unityReadBufferBytes = if ($encrypted) { 32KB } else { -1L } } }) }
    @{ scenario = $name; process = @{ pid = ($i + 1) }; requests = @($samples); observed = @{ sentinel = $true; performance = $metrics } }
})
$metrics = Get-YLFrozenPerformanceSummary -Runs $fixtures
Check ($metrics.plain.warm.loadMs.median -eq 8 -and $metrics.plain.warm.loadMs.min -eq 1 -and $metrics.plain.warm.loadMs.max -eq 9 -and
    $metrics.cipher.warm.loadMs.median -eq 30 -and $metrics.cipher.warm.startGateMs.median -eq 6.5) 'Median or min/max did not use the three raw warm samples.'
Check ($metrics.plain.cold.endpointBytes -eq 8 -and $metrics.cipher.warm.samples.Count -eq 3 -and $metrics.cipher.warm.samples[0].endpointBytes -eq 0) 'Endpoint bytes or raw warm samples were not retained.'
Reject { Get-YLFrozenPerformanceSummary -Runs $fixtures[0..6] } 'eight independent'
$fixtures[1].process.pid = $fixtures[0].process.pid
Reject { Get-YLFrozenPerformanceSummary -Runs $fixtures } 'independent.*PID'
$fixtures[1].process.pid = 2
$fixtures[1].requests = @(@{ method = 'HEAD'; bytes = 0L })
Reject { Get-YLFrozenPerformanceSummary -Runs $fixtures } 'online warm.*request'
$fixtures[1].requests = @()
$fixtures[4].observed.performance.Remove('loadCipherHashBytes')
Reject { Get-YLFrozenPerformanceSummary -Runs $fixtures } 'missing.*read metrics|cipher hash metrics'
$fixtures[4].observed.performance.loadCipherHashBytes = 16L
$fixtures[1].observed.performance.bundles[0].engineReadPlain = 0L
Reject { Get-YLFrozenPerformanceSummary -Runs $fixtures } 'opaque.*read metrics'
"PASS: $checks frozen encryption evidence checks. Test artifacts: $root"
