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
Check ($release.sourceCommit -ceq $head -and $release.packages.Count -eq $sources.Count) 'The complete test manifest did not pass semantic validation.'
if ($env:GITHUB_ACTIONS -ceq 'true') {
    Check ($release.producer.kind -ceq 'GitHubActions' -and $release.producer.repository -ceq $env:GITHUB_REPOSITORY -and
        $release.producer.workflow -ceq $env:GITHUB_WORKFLOW -and $release.producer.runId -ceq $env:GITHUB_RUN_ID -and
        $release.producer.runAttempt -ceq $env:GITHUB_RUN_ATTEMPT) 'The test manifest did not retain the actual Actions producer identity.'
} else {
    Check ($release.producer.kind -ceq 'Local') 'The local test manifest must not claim an Actions producer.'
}
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
            token = $job.token; mode = $job.mode; deliveryLayout = $job.deliveryLayout; input = $job.input; unity = '2022.3.62f3'; addressables = $job.addressables;
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
function New-ShippedContentFixture([string] $Folder, [string] $Layout, [string] $CatalogName) {
    $releaseRoot = Join-Path $Folder 'Delivery'
    $ordinaryRoot = Join-Path $Folder 'Ordinary'
    $aa = 'Player/P_Data/StreamingAssets/aa'
    $playerData = Join-Path $releaseRoot $aa
    $null = [IO.Directory]::CreateDirectory((Join-Path $releaseRoot 'Upload'))
    $bytes = [ordered]@{
        'Player/P.exe' = 'synthetic Player'; "$aa/$CatalogName" = 'first catalog'; "$aa/settings.json" = '{"runtime":"fixture"}'
        'ContentState/addressables_content_state.bin' = 'official content state'; 'ContentState/buildlayout.json' = '{"BuildTarget":"StandaloneWindows64"}'
    }
    $loopback = 'http://127.0.0.1:41234/'
    $releasePrefix = $null
    $ordinaryPaths = @()
    $protectedRows = @(if ($Layout -cne 'Embedded') {
        $releasePrefix = $loopback + 'r/Delivery/'
        'RemoteHttps/' + ('1' * 32) + '/remote.bundle/' + ('r' * 60)
    }
    if ($Layout -cne 'Remote') { 'Embedded/' + ('2' * 32) + '/local.bundle/' + ('e' * 60) })
    $protectedPaths = @()
    $uploadRoutes = [ordered]@{}
    $bundles = @(foreach ($protected in $protectedRows) {
        $deliveryMode, $guid, $leaf, $cipher = $protected.Split('/')
        $groupPath = "$guid/$leaf"
        $protectedPaths += $groupPath
        if ($deliveryMode -ceq 'RemoteHttps') {
            $target = "Upload/protected/$groupPath"
            $uploadRoutes[$target] = @{ category = 'protected'; url = $releasePrefix + $groupPath }
        } else { $target = "$aa/yl-encrypted/$groupPath" }
        $bytes[$target] = $cipher
        @{ mode = $deliveryMode; groupGuid = $guid; path = $leaf; bundleName = $leaf.Replace('.bundle', '_assets'); bundleHash = 'a' * 32
            keyId = "key-$guid"; plainLength = 4L; plainSha256 = 'b' * 64; cipherLength = 60L; cipherPath = $target }
    })
    if ($Layout -cne 'Embedded') {
        $null = [IO.Directory]::CreateDirectory($ordinaryRoot)
        $ordinaryPaths = @('required.bundle', 'unrelated.bundle')
        foreach ($leaf in $ordinaryPaths) {
            $target = "Upload/ordinary/$leaf"
            $bytes[$target] = "plain $leaf"
            [IO.File]::WriteAllText((Join-Path $ordinaryRoot $leaf), $bytes[$target])
            $uploadRoutes[$target] = @{ category = 'ordinary'; url = $loopback + "o/$leaf" }
        }
    }
    $files = [ordered]@{}
    foreach ($name in $bytes.Keys) {
        $file = Join-Path $releaseRoot $name
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file))
        [IO.File]::WriteAllText($file, $bytes[$name])
        $files[$name] = @{ path = $name; bytes = $bytes[$name].Length; sha256 = Get-YLFrozenFileSha256 $file }
    }
    foreach ($bundle in $bundles) {
        $bundle.cipherSha256 = $files[$bundle.cipherPath].sha256
        $bundle.Remove('cipherPath')
    }
    $uploads = @(foreach ($name in $uploadRoutes.Keys) { $files[$name] + $uploadRoutes[$name] })
    $identity = @{ version = 2; application = 'yl.frozen.acceptance'; platform = 'StandaloneWindows64'
        releaseId = 'Delivery'; releasePrefix = $releasePrefix; catalog = $CatalogName
        catalogSha256 = $files["$aa/$CatalogName"].sha256; settingsSha256 = $files["$aa/settings.json"].sha256; routesSha256 = 'c' * 64 }
    $description = $identity + @{ format = 'AES256_GCM_HKDF_1MB'; providerId = 'YLFramework.Resource.Encryption.EncryptedAssetBundleProvider'; bundles = $bundles }
    $descName = "$aa/yl-encrypted-bundles.json"
    $descPath = Join-Path $releaseRoot $descName
    Write-YLTestJson $descPath $description
    $files[$descName] = @{ path = $descName; bytes = [IO.FileInfo]::new($descPath).Length; sha256 = Get-YLFrozenFileSha256 $descPath }
    $delivery = $identity + @{ descriptionSha256 = $files[$descName].sha256; files = @($files.Values); uploads = $uploads }
    $deliveryFile = Join-Path $releaseRoot 'delivery.json'
    Write-YLTestJson $deliveryFile $delivery
    $build = @{ catalogSha256 = $identity.catalogSha256; shippedCatalogSha256 = $identity.catalogSha256
        settingsSha256 = $identity.settingsSha256; routesSha256 = $identity.routesSha256; descriptionSha256 = $delivery.descriptionSha256
        deliverySha256 = Get-YLFrozenFileSha256 $deliveryFile; protectedPaths = $protectedPaths
        ordinaryPaths = $ordinaryPaths; protectedBytes = 60L * $bundles.Count }
    return @{ Build = $build; Catalog = $CatalogName; PlayerData = $playerData; DeliveryRoot = $releaseRoot; Mode = 'Positive'
        Application = 'yl.frozen.acceptance'; ReleaseId = 'Delivery'; LoopbackPrefix = $loopback; DeliveryLayout = $Layout; OrdinaryRoot = $ordinaryRoot }
}

$shipped = New-ShippedContentFixture $root Mixed 'catalog.bin'
$deliveryFile = Join-Path $shipped.DeliveryRoot 'delivery.json'
$verified = @(Assert-YLFrozenShippedContent @shipped)
$remoteDeliveryPath = 'Upload/protected/' + ('1' * 32) + '/remote.bundle'
$embeddedDeliveryPath = 'Player/P_Data/StreamingAssets/aa/yl-encrypted/' + ('2' * 32) + '/local.bundle'
Check ($verified.Count -eq 2 -and $remoteDeliveryPath -cin $verified.path -and $embeddedDeliveryPath -cin $verified.path -and
    ($verified | Where-Object path -CEQ $embeddedDeliveryPath).sha256 -ceq (Get-YLFrozenFileSha256 (Join-Path $shipped.DeliveryRoot $embeddedDeliveryPath))) `
    'The mixed release did not bind the raw remote and embedded ciphertext.'
$embedded = New-ShippedContentFixture (Join-Path $root 'Embedded') Embedded 'catalog.json'
Check ((Assert-YLFrozenShippedContent @embedded).path -ceq $embeddedDeliveryPath) 'An embedded-only release must admit an empty Upload inventory.'
$wrongRoute = $shipped.Clone(); $wrongRoute.Mode = 'WrongRoute'
Check (@(Assert-YLFrozenShippedContent @wrongRoute).Count -eq 2) 'WrongRoute must verify the pristine release first.'
$wrongLayout = $shipped.Clone(); $wrongLayout.DeliveryLayout = 'Remote'
Reject { Assert-YLFrozenShippedContent @wrongLayout } 'selected layout'
$probeCopy = $wrongRoute.Clone(); $probeCopy.PlayerData = Join-Path $root 'WrongRoute/P_Data/StreamingAssets/aa'
Reject { Assert-YLFrozenShippedContent @probeCopy } 'pristine Player'

foreach ($name in 'Player/P.exe', 'Player/P_Data/StreamingAssets/aa/catalog.bin', 'Player/P_Data/StreamingAssets/aa/settings.json',
    $remoteDeliveryPath, $embeddedDeliveryPath, 'Upload/ordinary/required.bundle', 'ContentState/buildlayout.json') {
    $file = Join-Path $shipped.DeliveryRoot $name
    $original = [IO.File]::ReadAllBytes($file)
    try {
        $changed = [byte[]]$original.Clone(); $changed[0] = $changed[0] -bxor 1
        [IO.File]::WriteAllBytes($file, $changed)
        Reject { Assert-YLFrozenShippedContent @wrongRoute } 'differs|SHA-256'
    } finally { [IO.File]::WriteAllBytes($file, $original) }
}
function Reject-ShippedReceipt([scriptblock] $Change, [string] $Reason) {
    $original = [IO.File]::ReadAllBytes($deliveryFile)
    $doc = ConvertFrom-Json -InputObject ([Text.Encoding]::UTF8.GetString($original)) -AsHashtable -DateKind String
    $receiptArgs = $shipped.Clone(); $receiptArgs.Build = $shipped.Build.Clone()
    try {
        & $Change $doc $receiptArgs.Build
        [IO.File]::WriteAllText($deliveryFile, (ConvertTo-Json -InputObject $doc -Depth 8))
        $receiptArgs.Build.deliverySha256 = Get-YLFrozenFileSha256 $deliveryFile
        Reject { Assert-YLFrozenShippedContent @receiptArgs } $Reason
    } finally { [IO.File]::WriteAllBytes($deliveryFile, $original) }
}
Reject-ShippedReceipt { param($doc) $doc.application = 'another.app' } 'application'
Reject-ShippedReceipt { param($doc) $doc.releaseId = 'Another' } 'releaseId'
Reject-ShippedReceipt { param($doc) $doc.releasePrefix = 'http://127.0.0.1:41234/r/Another/' } 'releasePrefix'
Reject-ShippedReceipt { param($doc) $doc.settingsSha256 = 'd' * 64 } 'settings'
Reject-ShippedReceipt { param($doc) $doc.routesSha256 = 'd' * 64 } 'routes'
Reject-ShippedReceipt { param($doc) $doc.uploads[0].url = $doc.uploads[0].url.Replace('/Delivery/', '/Another/') } 'upload.*URL'
Reject-ShippedReceipt { param($doc) $doc.uploads[0].url += [char]0 } 'upload.*URL'
Reject-ShippedReceipt { param($doc) $doc.uploads[0].category = 'ordinary' } 'upload.*category'
Reject-ShippedReceipt { param($doc) $doc.uploads[0].bytes = '60' } 'upload.*inventory'
Reject-ShippedReceipt { param($doc) $doc.files[0].path = 'Player/../Player/P.exe' } 'path'
Reject-ShippedReceipt { param($doc) $doc.files += @{ path = 'Player/p.EXE'; bytes = $doc.files[0].bytes; sha256 = $doc.files[0].sha256 } } 'collision|duplicate'
Reject-ShippedReceipt { param($doc) $doc.extra = $true } 'fields'
$descriptorFile = Join-Path $shipped.PlayerData 'yl-encrypted-bundles.json'
$descriptorBytes = [IO.File]::ReadAllBytes($descriptorFile)
try {
    $descriptor = ConvertFrom-Json -InputObject ([Text.Encoding]::UTF8.GetString($descriptorBytes)) -AsHashtable -DateKind String
    $descriptor.bundles[0].extra = $true
    [IO.File]::WriteAllText($descriptorFile, (ConvertTo-Json -InputObject $descriptor -Depth 8))
    Reject-ShippedReceipt {
        param($doc, $build)
        $build.descriptionSha256 = $doc.descriptionSha256 = Get-YLFrozenFileSha256 $descriptorFile
        $row = $doc.files | Where-Object path -CLike '*/yl-encrypted-bundles.json'
        $row.bytes = [IO.FileInfo]::new($descriptorFile).Length
        $row.sha256 = $doc.descriptionSha256
    } 'protected Bundle fields'
} finally { [IO.File]::WriteAllBytes($descriptorFile, $descriptorBytes) }
$required = 'Upload/ordinary/required.bundle'
$ordinaryCopy = Join-Path $shipped.DeliveryRoot $required
$ordinarySource = Join-Path $shipped.OrdinaryRoot 'required.bundle'
[IO.File]::Delete($ordinaryCopy)
try {
    Reject-ShippedReceipt {
        param($doc)
        $doc.files = @($doc.files | Where-Object path -CNE $required)
        $doc.uploads = @($doc.uploads | Where-Object path -CNE $required)
    } 'ordinary.*required\.bundle'
} finally { [IO.File]::Copy($ordinarySource, $ordinaryCopy) }
$ordinaryBytes = [IO.File]::ReadAllBytes($ordinarySource)
try {
    $changed = [byte[]]$ordinaryBytes.Clone(); $changed[0] = $changed[0] -bxor 1
    [IO.File]::WriteAllBytes($ordinarySource, $changed)
    Reject { Assert-YLFrozenShippedContent @shipped } 'ordinary.*required\.bundle'
} finally { [IO.File]::WriteAllBytes($ordinarySource, $ordinaryBytes) }
$unexpectedFile = Join-Path $shipped.DeliveryRoot 'Player/extra.dat'
[IO.File]::WriteAllText($unexpectedFile, 'unlisted file')
try { Reject { Assert-YLFrozenShippedContent @shipped } 'inventory|unlisted' } finally { [IO.File]::Delete($unexpectedFile) }
$unexpectedDirectory = Join-Path $shipped.DeliveryRoot 'Upload/extra'
$null = [IO.Directory]::CreateDirectory($unexpectedDirectory)
try { Reject { Assert-YLFrozenShippedContent @shipped } 'unexpected directory' } finally { [IO.Directory]::Delete($unexpectedDirectory) }

$stock = Join-Path $root 'Stock/P/P_Data/StreamingAssets/aa'
$null = [IO.Directory]::CreateDirectory($stock)
$stockCatalog = Join-Path $stock 'catalog.json'; [IO.File]::WriteAllText($stockCatalog, 'stock catalog')
$disabled = @{ Build = @{ shippedCatalogSha256 = Get-YLFrozenFileSha256 $stockCatalog }; Catalog = 'catalog.json'
    PlayerData = $stock; DeliveryRoot = Join-Path $root 'Stock/Delivery'; Mode = 'Disabled' }
Check (@(Assert-YLFrozenShippedContent @disabled).Count -eq 0) 'Disabled did not use the stock installed Catalog.'
$null = [IO.Directory]::CreateDirectory($disabled.DeliveryRoot)
Reject { Assert-YLFrozenShippedContent @disabled } 'disabled.*delivery'

$perfContracts = Join-Path $PSScriptRoot '../Common/FrozenPerformanceEvidence.ps1'
Check ([IO.File]::Exists($perfContracts)) 'The performance runner cannot prove five independent warm Player samples.'
. $perfContracts
$scenarioNames = @(Get-YLFrozenPerformanceScenarios)
Check (($scenarioNames -join ',') -ceq ('performance-plain-cold,performance-cipher-cold,' +
    'performance-plain-warm-1,performance-cipher-warm-1,performance-cipher-warm-2,performance-plain-warm-2,' +
    'performance-plain-warm-3,performance-cipher-warm-3,performance-cipher-warm-4,performance-plain-warm-4,' +
    'performance-plain-warm-5,performance-cipher-warm-5')) 'Expected five interleaved warm Players for each path.'
$performanceBuild = @{ input = 'a' * 64; shippedCatalogSha256 = 'b' * 64; plainCatalogSha256 = 'c' * 64;
    performanceInputs = @(@{ clearBytes = 8390371L; clearSha256 = 'd' * 64; cipherBytes = 8390555L; cipherSha256 = 'e' * 64; mode = 'RemoteHttps' },
        @{ clearBytes = 134219484L; clearSha256 = 'f' * 64; cipherBytes = 134221588L; cipherSha256 = '1' * 64; mode = 'RemoteHttps' }) }
[long] $totalCipher = 142612143
$fixtures = @(for ($i = 0; $i -lt $scenarioNames.Count; $i++) {
    $name = $scenarioNames[$i]
    $times = @(10.0, 50.0, 8.0, 40.0, 22.0, 9.0, 1.0, 30.0, 44.0, 2.0, 7.0, 25.0)
    $cold = $name.EndsWith('-cold', [StringComparison]::Ordinal)
    $samples = if ($cold) { @(@{ bytes = 3L }, @{ bytes = 5L }) } else { @() }
    $encrypted = $name.Contains('-cipher-', [StringComparison]::Ordinal)
    $metrics = @{ mode = $name; concurrent = $true; elapsedMs = $times[$i]; startGateMs = (0.5 + $i); startGateMainThreadCpuMs = 0.0;
        metricsVersion = 3; deliveryLayout = 'Remote'; entryPoint = 'Addressables.LoadAssetAsync<TextAsset>';
        requestedAtMs = 100.0; smallReadyAtMs = 100.5; largeReadyAtMs = (100.0 + $times[$i]);
        firstAssetMs = 100.5; bothAssetsMs = (100.0 + $times[$i]); remotePrepareMs = 0.0; preLoadMounted = 0; preLoadKeys = 0; preLoadAuthentications = 0;
        mainThreadCpuMs = 3.0; largestObservedFrameMs = 0.25; cacheLogicalBytesAtCompletion = $totalCipher;
        cacheLogicalPeak = -1L; nativeBufferEvidence = if ($encrypted) { 'source-bound' } else { 'unavailable' };
        processPrivateBefore = 67L; processPrivatePeak = 89L;
        processWorkingSetBefore = 55L; processWorkingSetPeak = 76L; engineAssetReportedBytes = -1L; enginePeakMemory = 'unavailable';
        fixturePayloadBytes = 136L * 1MB; startGateKind = if ($encrypted) { 'frozen-catalog-and-cipher-cache' } else { 'ordinary-unity-cache-readiness' };
        startGateCipherHashBytes = if (-not $encrypted) { -1L } elseif ($cold) { 0L } else { $totalCipher };
        loadCipherHashBytes = if (-not $encrypted) { -1L } elseif ($cold) { 2 * $totalCipher } else { $totalCipher };
        bundleReadKind = if ($encrypted) { 'native-measured' } else { 'engine-opaque' };
        bundles = @(if ($encrypted) { foreach ($item in $performanceBuild.performanceInputs) {
            $bytes = $item.clearBytes; $cipherBytes = $item.cipherBytes
            @{ clearBytes = $bytes; cipherBytes = $cipherBytes; clearSha256 = $item.clearSha256; cipherSha256 = $item.cipherSha256;
            readPlain = if ($encrypted) { 2 * $bytes } else { -1L }; readCipher = if ($encrypted) { 2 * $cipherBytes } else { -1L };
            pages = if ($encrypted) { 1L } else { -1L }; payloadBufferBound = if ($encrypted) { 3MB } else { -1L };
            preAuthenticationPlainBytes = if ($encrypted) { $bytes } else { -1L }; preAuthenticationCipherBytes = if ($encrypted) { $cipherBytes } else { -1L };
            engineReadPlain = if ($encrypted) { $bytes } else { -1L }; engineReadCipher = if ($encrypted) { $cipherBytes } else { -1L };
            officialDownloadBytes = if (-not $encrypted) { -1L } elseif ($cold) { $cipherBytes } else { 0L };
            cipherShaMs = if ($encrypted) { 0.25 } else { -1.0 }; authenticationMs = if ($encrypted) { 0.5 } else { -1.0 };
            unityReadBufferBytes = 32KB } } }) }
    @{ scenario = $name; process = @{ pid = ($i + 1) }; requests = @($samples); observed = @{ sentinel = $true; performance = $metrics;
        inputHash = $performanceBuild.input; catalogSha256 = if ($encrypted) { $performanceBuild.shippedCatalogSha256 } else { $performanceBuild.plainCatalogSha256 } } }
})
$readPerformance = { param($rows = $fixtures) Get-YLFrozenPerformanceSummary -Runs $rows -Build $performanceBuild -DeliveryLayout Remote }
$metrics = & $readPerformance
Check ($metrics.passed -and $metrics.plain.warm.loadMs.median -eq 7 -and $metrics.plain.warm.loadMs.min -eq 1 -and $metrics.plain.warm.loadMs.max -eq 9 -and
    $metrics.cipher.warm.loadMs.median -eq 30 -and $metrics.cipher.warm.firstAssetMs.median -eq 100.5) 'Raw sample statistics disagree.'
Check ($metrics.plain.cold.endpointBytes -eq 8 -and $metrics.cipher.warm.samples.Count -eq 5 -and $metrics.cipher.warm.samples[0].endpointBytes -eq 0) 'Endpoint bytes or raw warm samples were not retained.'
Reject { & $readPerformance $fixtures[0..10] } 'independent Player samples'
$fixtures[1].process.pid = $fixtures[0].process.pid
Reject { & $readPerformance } 'independent.*PID'
$fixtures[1].process.pid = 2
$fixtures[2].requests = @(@{ method = 'HEAD'; bytes = 0L })
Reject { & $readPerformance } 'online warm.*request'
$fixtures[2].requests = @()
$warmCipher = $fixtures[3].observed.performance
$warmCipher.elapsedMs -= 1
Reject { & $readPerformance } 'material-ready timing'
$warmCipher.elapsedMs += 1
$fixtures[3].observed.catalogSha256 = '0' * 64
Reject { & $readPerformance } 'Catalog identity'
$fixtures[3].observed.catalogSha256 = $performanceBuild.shippedCatalogSha256
$warmCipher.bundles[1].clearSha256 = '0' * 64
Reject { & $readPerformance } 'Bundle identity'
$warmCipher.bundles[1].clearSha256 = $performanceBuild.performanceInputs[1].clearSha256
$warmCipher.loadCipherHashBytes = 2 * $totalCipher
Check (-not ((& $readPerformance).passed)) 'A repeated warm cipher SHA was accepted.'
$warmCipher.loadCipherHashBytes = $totalCipher
$warmCipher.bundles[1].preAuthenticationCipherBytes *= 2
$warmCipher.bundles[1].readCipher += $performanceBuild.performanceInputs[1].cipherBytes
Check (-not ((& $readPerformance).passed)) 'A repeated native segment read was accepted.'
$warmCipher.bundles[1].preAuthenticationCipherBytes /= 2L
$warmCipher.bundles[1].preAuthenticationCipherBytes = [long]$warmCipher.bundles[1].preAuthenticationCipherBytes
$warmCipher.bundles[1].readCipher -= $performanceBuild.performanceInputs[1].cipherBytes
$warmCipher.nativeBufferEvidence = 'measured-peak'
Reject { & $readPerformance } 'memory.*source'
$warmCipher.nativeBufferEvidence = 'source-bound'
$fixtures[2].observed.performance.bundles = @($warmCipher.bundles)
Reject { & $readPerformance } 'opaque.*read metrics'
$fixtures[2].observed.performance.bundles = @()

$historicalCommit = '8a848d4eaf3854b8e5b35c76af2e287858be9a47'
$performanceHost = @{ os = 'Windows'; processor = 'AMD64'; logicalProcessors = 16L; volume = 'G:\'; fileSystem = 'NTFS'; timingOrigin = 'SubsystemRegistration';
    runtime = '.NET'; systemLoad = 'controlled-serial'; osFileCache = 'not flushed' }
$candidate = @{ purpose = 'YLResource.FrozenDelivery.LocalFixture'; status = 'Passed'; metricsVersion = 3; mode = 'Performance'; deliveryLayout = 'Remote';
    profile = 'Unity6'; addressables = '2.10.3'; backend = 'IL2CPP'; performanceHost = $performanceHost.Clone(); build = $performanceBuild.Clone(); runs = $fixtures }
$candidate.build.unity = '6000.3.27f1'; $candidate.build.stripping = 'Medium'
$baseline = @{ purpose = 'YLResource.FrozenDelivery.HistoricalRemoteControl'; sourceCommit = $historicalCommit; status = 'Passed'; metricsVersion = 2;
    mode = 'Performance'; deliveryLayout = 'Remote'; profile = 'Unity6'; backend = 'IL2CPP'; addressables = '2.10.3';
    pluginSha256 = '963ba62687c2f895649923138083b78acff7bba470ce92ba90228c27e9a46f5c'; performanceHost = $performanceHost.Clone();
    build = $candidate.build.Clone(); performanceInputs = @(foreach ($inputFile in $performanceBuild.performanceInputs) {
        @{ plainLength = $inputFile.clearBytes; plainSha256 = $inputFile.clearSha256; cipherLength = $inputFile.cipherBytes; cipherSha256 = $inputFile.cipherSha256 }
    }) }
$utc = [DateTimeOffset]::Parse('2026-01-01T12:00:00Z')
foreach ($run in $fixtures) {
    $run.observed.status = 'Passed'; $run.observed.graphics = 'Direct3D12'; $run.observed.backend = 'IL2CPP'; $run.observed.architecture = 'X64'
    $run.observed.unity = $candidate.build.unity; $run.observed.pid = $run.process.pid
    $run.process.status = 'Exited'; $run.process.exitCode = 0; $run.process.observedExit = $true; $run.process.activeProcesses = 0; $run.process.timedOut = $false
    $run.process.startUtc = $utc.AddSeconds(10 * $run.process.pid).ToString('o')
    $run.process.endUtc = $utc.AddSeconds(10 * $run.process.pid + 5).ToString('o')
}
$baseline.runs = @(foreach ($run in ($fixtures | Where-Object scenario -CLike 'performance-cipher-*')) {
    $old = $run.Clone(); $old.process = $run.process.Clone(); $old.observed = $run.observed.Clone(); $old.observed.performance = $run.observed.performance.Clone()
    $old.process.pid += 100; $old.observed.pid = $old.process.pid
    $old.process.startUtc = ([DateTimeOffset]$old.process.startUtc).AddMinutes(5).ToString('o')
    $old.process.endUtc = ([DateTimeOffset]$old.process.endUtc).AddMinutes(5).ToString('o')
    $sample = $old.observed.performance; $sample.metricsVersion = 2
    $sample.requestedAtMs += 50; $sample.smallReadyAtMs += 50; $sample.largeReadyAtMs += 50; $sample.firstAssetMs += 50; $sample.bothAssetsMs += 50
    $old
})
$window = @{ startUtc = $utc.ToString('o'); endUtc = $utc.AddMinutes(10).ToString('o') }
$compare = { Get-YLFrozenRemoteStartupComparison -Candidate $candidate -Baseline $baseline -ControlledWindow $window }
Check ((& $compare).status -ceq 'Passed') 'Comparable evidence with faster startup was rejected.'
Check ((Get-YLFrozenRemoteStartupComparison -Candidate $candidate -Baseline $baseline).status -ceq 'NotComparable') 'An unspecified workload window was treated as comparable.'
$candidate.performanceHost.volume = 'C:\'
Check ((& $compare).status -ceq 'NotComparable') 'A different disk was treated as comparable.'
$candidate.performanceHost.volume = 'G:\'
Check ((Get-YLFrozenRemoteStartupComparison -Candidate $candidate -Baseline $baseline -ControlledWindow @{startUtc = $utc.ToString('o'); endUtc = $utc.AddMinutes(1).ToString('o')}).status -ceq 'NotComparable') 'Processes outside the declared window were accepted.'
$baseline.performanceInputs[0].plainSha256 = '9' * 64
Check ((& $compare).status -ceq 'NotComparable') 'A different clear Bundle SHA was treated as comparable.'
$baseline.performanceInputs[0].plainSha256 = $performanceBuild.performanceInputs[0].clearSha256
$baseline.runs[1].requests = @(@{ method = 'HEAD'; bytes = 0L })
Reject { & $compare } 'warm.*request'
$baseline.runs[1].requests = @()
$baseline.runs[1].observed.performance.bothAssetsMs -= 1
Reject { & $compare } 'material-ready timing'
$baseline.runs[1].observed.performance.bothAssetsMs += 1
foreach ($run in ($fixtures | Where-Object scenario -CLike 'performance-cipher-warm-*')) {
    $sample = $run.observed.performance
    $sample.requestedAtMs += 1000; $sample.smallReadyAtMs += 1000; $sample.largeReadyAtMs += 1000; $sample.firstAssetMs += 1000; $sample.bothAssetsMs += 1000
}
Check ((& $readPerformance).passed -and (& $compare).status -ceq 'Regression') 'Moving delay before the business request hid an end-to-end regression.'
$comparisonTool = Join-Path $PSScriptRoot '../ResourceEncryption/Compare-FrozenRemotePerformance.ps1'
$candidateFile = Join-Path $root 'candidate-performance.json'
$baselineFile = Join-Path $root 'baseline-performance.json'
$comparisonFile = Join-Path $root 'startup-comparison.json'
Write-YLTestJson -Path $candidateFile -Value $candidate
Write-YLTestJson -Path $baselineFile -Value $baseline
$compareArgs = @{ CandidateReceipt = $candidateFile; CandidateSha256 = Get-YLFrozenFileSha256 $candidateFile;
    BaselineReceipt = $baselineFile; BaselineSha256 = Get-YLFrozenFileSha256 $baselineFile; Output = $comparisonFile;
    ControlledStartUtc = $window.startUtc; ControlledEndUtc = $window.endUtc }
Reject { & $comparisonTool @compareArgs } 'Startup comparison Regression'
Check ((Read-YLPackageEvidenceJson $comparisonFile).result.status -ceq 'Regression') 'A failed startup gate did not preserve its diagnostic receipt.'
$compareArgs.BaselineSha256 = '0' * 64
Reject { & $comparisonTool @compareArgs } 'SHA-256 mismatch'
"PASS: $checks frozen encryption evidence checks. Test artifacts: $root"
