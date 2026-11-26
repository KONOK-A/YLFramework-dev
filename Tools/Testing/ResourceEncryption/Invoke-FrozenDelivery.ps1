#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Unity6','Unity2022', IgnoreCase = $false)][string] $Profile,
    [Parameter(Mandatory)][string] $Unity,
    [string] $WorkspaceRoot = $env:YL_TEST_WORKSPACE,
    [ValidateSet('Positive','PlainBundleCopy','PlayerCopy','WrongRoute','Disabled','Performance','OutputOverlap','SourceChanged','SyncWait','GuardReview','OfficialOutput','PublishChanged','LateKeyFailure','TargetConflict','AmbiguousGroups','RemoteClosure','OrdinaryDependency','BadDependency','MultiGroup','PrePublishAbort','MissingPlayerRuntime','ChangedPlayerOutput','LocalNoHash','LocalNoHashCollision', IgnoreCase = $false)][string] $Mode = 'Positive',
    [string] $PackReceipt,
    [string] $ReleaseManifest,
    [string] $SourceCommit,
    [switch] $KeepArtifacts,
    [switch] $ContentUpdate,
    [ValidateSet('Remote','Embedded','Mixed', IgnoreCase = $false)][string] $DeliveryLayout = 'Remote',
    [ValidateSet('IL2CPP','Mono', IgnoreCase = $false)][string] $Backend = 'IL2CPP',
    [ValidateSet('Default','LargeDownload','BufferCapacity','CacheUsage','SpaceFaultOffline', IgnoreCase = $false)][string] $Experiment = 'Default',
    [string] $DiagnosticNativeDll,
    [string] $BaselineReceipt,
    [switch] $ExpectProgressDeadline)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($ContentUpdate) {
    if ($PackReceipt -or $ReleaseManifest -or $SourceCommit -or $BaselineReceipt -or $ExpectProgressDeadline -or
        $Experiment -cne 'Default' -or $PSBoundParameters.ContainsKey('Mode') -or
        $PSBoundParameters.ContainsKey('DeliveryLayout')) {
        throw '-ContentUpdate is a separate frozen Player workload using the current source packages.'
    }
    $arguments = @{ $Profile = $Unity; WorkspaceRoot = $WorkspaceRoot; Cell = "$Profile-Encrypted-$Backend"; KeepArtifacts = $KeepArtifacts }
    & (Join-Path $PSScriptRoot '../ResourceUpdates/Invoke-ContentUpdates.ps1') @arguments
    return
}
if ($Experiment -cne 'Default' -and ($Mode -cne 'Performance' -or $DeliveryLayout -cne 'Remote')) {
    throw 'These experiments require the remote two-Bundle performance fixture.'
}
if (($Experiment -ceq 'BufferCapacity') -ne [bool]$DiagnosticNativeDll -or
    ($DiagnosticNativeDll -and ($PackReceipt -or $ReleaseManifest -or $SourceCommit))) {
    throw 'BufferCapacity requires a diagnostics DLL in a local source projection.'
}
if (($BaselineReceipt -or $ExpectProgressDeadline) -and
    ($Experiment -cne 'LargeDownload' -or $PackReceipt -or $ReleaseManifest -or $SourceCommit)) {
    throw 'A paired or progress-deadline transfer workload requires local source inputs.'
}
if ($Mode -cin @('RemoteClosure','OrdinaryDependency','BadDependency','MultiGroup') -and $DeliveryLayout -cne 'Mixed') { throw "$Mode requires -DeliveryLayout Mixed." }
if ($Mode -cin @('SyncWait','GuardReview') -and $DeliveryLayout -cne 'Remote') { throw "$Mode requires -DeliveryLayout Remote." }
if ($Mode -cin @('LocalNoHash','LocalNoHashCollision') -and $DeliveryLayout -cne 'Embedded') { throw "$Mode requires -DeliveryLayout Embedded." }
. (Join-Path $PSScriptRoot '../UnityHost.ps1')
. (Join-Path $PSScriptRoot '../Common/ResourceAddressablesPlayer.ps1')
. (Join-Path $PSScriptRoot '../Common/FrozenPlayerEvidence.ps1')
. (Join-Path $PSScriptRoot '../Common/FrozenEncryptionInputs.ps1')
. (Join-Path $PSScriptRoot '../Common/FrozenPerformanceEvidence.ps1')
$repo = Get-YLTestPath ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')))
$extension = 'com.kano.ylframework.resource-encryption'
$main = 'com.kano.ylframework'
$base = 'PackageSources/'
$editorSource = "$base$extension/Editor/EncryptedBundleExporter.cs"
if (-not [IO.File]::Exists((Join-Path $repo $editorSource))) { throw 'No frozen export and route gate are implemented.' }
$fixedRelease = $PSBoundParameters.ContainsKey('ReleaseManifest') -or $PSBoundParameters.ContainsKey('SourceCommit')
$release = $null
if ($fixedRelease) {
    if ([string]::IsNullOrWhiteSpace($ReleaseManifest) -or [string]::IsNullOrWhiteSpace($SourceCommit) -or $PSBoundParameters.ContainsKey('PackReceipt')) {
        throw '-ReleaseManifest and -SourceCommit must be supplied together, without -PackReceipt.'
    }
    $release = Get-YLFrozenEncryptionRelease -Path $ReleaseManifest -Repository $repo -SourceCommit $SourceCommit
}
$Unity = Get-YLTestPath $Unity -ReadOnly
$module = @(Get-YLFrozenWin64Module -Unity $Unity -Backend $Backend)
$configuration = Read-YLPackageEvidenceJson (Join-Path $repo 'Tools/Testing/TestSuites.json')
$editor = [pscustomobject](Get-YLTestEditorProfile -Editors $configuration.editors -VersionInfo ([Diagnostics.FileVersionInfo]::GetVersionInfo($Unity)))
$aa = Get-YLUnityAddressablesVersion $editor
$catalogName = if ($aa -ceq '2.10.3') { 'catalog.bin' } else { 'catalog.json' }
if (($Profile -ceq 'Unity6' -and ($aa -cne '2.10.3' -or $editor.version -cne '6000.3.27f1')) -or
    ($Profile -ceq 'Unity2022' -and ($aa -cne '1.25.0' -or $editor.version -cne '2022.3.62f3'))) { throw 'Incorrect acceptance profile.' }
$pins = @(Get-YLUnityPackageInputs -EditorProfile $editor)
$lock = Read-YLPackageEvidenceJson (Join-Path $repo $pins[1])
$workspace = Initialize-YLTestWorkspace -WorkspaceRoot $WorkspaceRoot -Repository $repo
$run = New-YLTestDirectory -Workspace $workspace -Kind Result -Key 'FrozenEncryption'
$hostRecord = New-YLTestDirectory -Workspace $workspace -Kind Host -Key ('RFD' + [guid]::NewGuid().ToString('N').Substring(0,8)) -NewOnly
$hostLease = $null
$replayLease = $null
$runLease = Lock-YLTestDirectory -Workspace $workspace -Path $run.path
$summary = [ordered]@{ purpose = $(if ($fixedRelease) { 'YLResource.FrozenDelivery.LocalTarball' } else { 'YLResource.FrozenDelivery.LocalFixture' });
    status = 'Failed'; profile = $Profile; mode = $Mode; backend = $Backend; deliveryLayout = $DeliveryLayout; metricsVersion = 3;
    experiment = $Experiment;
    addressables = $aa; formalCandidate = $fixedRelease;
    host = $hostRecord.path; hostId = $hostRecord.id; results = $run.path; resultId = $run.id; build = $null; runs = @(); packageArtifacts = [ordered]@{} }
$keyFile = Join-Path $hostRecord.path 'fixture.key'
$peerKeyFile = Join-Path $hostRecord.path 'fixture-peer.key'
$createdKeys = [Collections.Generic.List[string]]::new()
$failures = [Collections.Generic.List[Management.Automation.ErrorRecord]]::new()

function Execute-Process($exe, $working, $arguments, $log, [int]$timeout, [string]$CacheRoot) {
    $info = [Diagnostics.ProcessStartInfo]::new($exe)
    $info.UseShellExecute = $false; $info.WorkingDirectory = $working
    foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
    if ($CacheRoot) { Set-YLFrozenUpmEnvironment -StartInfo $info -CacheRoot $CacheRoot -Cold }
    $grace = if ($log -clike '*editor') { 45000 } else { 15000 }
    Invoke-YLTestProcess -StartInfo $info -Log (Join-Path $run.path ($log + '-process.log')) -TimeoutMilliseconds $timeout -DescendantGraceMilliseconds $grace
}

function Copy-VerifiedPlayer([string] $From, [string] $To) {
    if ([IO.Path]::Exists($To)) { throw 'The relocated Player requires a new directory.' }
    foreach ($file in (Get-YLTestTreeFiles $From)) {
        $target = Get-YLTestPath (Join-Path $To ([IO.Path]::GetRelativePath($From, $file)))
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        [IO.File]::Copy($file, $target, $false)
        if ((Get-YLFrozenFileSha256 $target) -cne (Get-YLFrozenFileSha256 $file)) { throw 'Relocated Player bytes differ.' }
    }
}

function Get-EncryptedExperimentRoute($Build, $Cipher) {
    $published = @($Build.remoteProtectedPaths | Where-Object { [IO.Path]::GetFileName($_) -ceq $Cipher.path })
    if ($published.Count -ne 1) { throw 'The experiment ciphertext does not have one published route.' }
    '/r/Delivery/' + $published[0]
}

function Invoke-FixedColdCacheInspection([string] $Root, [string] $OwnedHost, [scriptblock] $RunPlayer) {
    $rootPath = Get-YLTestPath $Root
    if ([IO.Path]::GetDirectoryName($rootPath) -ine (Get-YLTestPath $OwnedHost) -or
        [IO.Path]::GetFileName($rootPath) -cne 'cache-cache-usage-cold' -or [IO.Path]::Exists($rootPath)) {
        throw 'Cache access restrictions require a fresh, owned fixture directory.'
    }
    $null = [IO.Directory]::CreateDirectory($rootPath)
    $owner = Join-Path $rootPath '.yl-cache.lock'
    $legacy = Join-Path $rootPath ('.yl-transfer-' + ('a' * 32) + '.part')
    $unattributed = Join-Path $rootPath (('f' * 64) + '.bin')
    $denied = Join-Path $rootPath (('d' * 64) + '.bin')
    [IO.File]::WriteAllBytes($owner, [byte[]]::new(0))
    [IO.File]::WriteAllText($legacy, 'Fixture legacy temporary bytes')
    [IO.File]::WriteAllText($unattributed, 'Unattributed ciphertext-shaped file')
    [IO.File]::WriteAllText($denied, 'Protected by fixture ACL')

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { $sid = $identity.User } finally { $identity.Dispose() }
    $baseRights = [Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles
    $section = [Security.AccessControl.AccessControlSections]::Access
    $sealed = [Collections.Generic.List[object]]::new()
    try {
        foreach ($path in @($rootPath, $owner, $legacy, $unattributed, $denied)) {
            $item = Get-Item -LiteralPath $path -Force
            $permissions = if ($path -ceq $denied) { $baseRights -bor [Security.AccessControl.FileSystemRights]::ReadData } else { $baseRights }
            $acl = [IO.FileSystemAclExtensions]::GetAccessControl($item, $section)
            $record = @{ item = $item; acl = $acl; original = $acl.GetSecurityDescriptorSddlForm($section) }
            $sealed.Add($record)
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, $permissions, 'Deny'))
            [IO.FileSystemAclExtensions]::SetAccessControl($item, $acl)
            $record.applied = ([IO.FileSystemAclExtensions]::GetAccessControl($item, $section)).GetSecurityDescriptorSddlForm($section)
        }
        $process = & $RunPlayer
        foreach ($record in $sealed) {
            if (([IO.FileSystemAclExtensions]::GetAccessControl($record.item, $section)).GetSecurityDescriptorSddlForm($section) -cne $record.applied) {
                throw "Cache inspection changed the fixture permissions at $($record.item.FullName)."
            }
        }
        $process
    } finally {
        $restoreErrors = [Collections.Generic.List[string]]::new()
        foreach ($record in $sealed) {
            try {
                $null = Get-YLTestPath $record.item.FullName
                $record.acl.SetSecurityDescriptorSddlForm($record.original, $section)
                [IO.FileSystemAclExtensions]::SetAccessControl($record.item, $record.acl)
            } catch { $restoreErrors.Add($_.Exception.Message) }
        }
        if ($restoreErrors.Count) { throw ('Could not restore fixed cache fixture access: ' + ($restoreErrors -join '; ')) }
    }
}

function New-TransferReplayPlayer($Previous, $ReplayHost, $CurrentInputs, $CurrentBuild, [string] $ContentRoot) {
    $previousHostPath = Get-YLTestPath $Previous.host
    $previousRoot = [IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($previousHostPath))
    $previousWorkspace = [pscustomobject](Read-YLPackageEvidenceJson (Join-Path $previousRoot '.yl-workspace.json'))
    if ($previousWorkspace.repository -ine $repo) { throw 'The baseline was projected from a different repository.' }
    $oldHost = Get-YLTestDirectory $previousWorkspace $previousHostPath
    if ($oldHost.kind -cne 'Host' -or $oldHost.id -cne $Previous.hostId -or
        -not [IO.File]::Exists((Join-Path $oldHost.path '.pinned'))) { throw 'Replay needs a retained baseline source host.' }
    $oldInputs = Read-YLPackageEvidenceJson (Get-YLTestPath (Join-Path $oldHost.path '.yl-inputs.json') -ReadOnly)
    if ($oldInputs.hostId -cne $oldHost.id -or $oldInputs.inputHash -cne $Previous.inputHash) {
        throw 'Replay baseline source ownership differs from its input receipt.'
    }
    $projectionInputs = [ordered]@{}
    $recoveredMeta = [Collections.Generic.List[string]]::new()
    foreach ($file in $oldInputs.files) {
        if (-not $file.path.StartsWith($base, [StringComparison]::Ordinal)) { continue }
        $source = Get-YLTestPath (Join-Path $oldHost.path $file.path) -ReadOnly
        if (-not $source.StartsWith($oldHost.path + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Baseline source escaped its host.' }
        $bytes = [IO.File]::ReadAllBytes($source)
        if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -cne $file.sha256) {
            # Unity 2022 can rewrite the projected importer metadata, so recover only the exact recorded bytes.
            $original = Get-YLTestPath (Join-Path $repo $file.path) -ReadOnly
            if (-not $file.path.EndsWith('.meta', [StringComparison]::Ordinal) -or -not [IO.File]::Exists($original)) {
                throw 'The retained baseline source hash differs: ' + $file.path
            }
            $bytes = [IO.File]::ReadAllBytes($original)
            if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -cne $file.sha256) {
                throw 'Cannot recover the recorded baseline importer metadata: ' + $file.path
            }
            $recoveredMeta.Add($file.path)
        }
        $projectionInputs[$file.path] = $bytes
    }
    if (-not $projectionInputs.Contains($dllPath) -or -not $projectionInputs.Contains("$base$main/Runtime/YLFramework.Runtime.asmdef")) {
        throw 'The retained source projection is incomplete.'
    }
    foreach ($name in $CurrentInputs.Keys) {
        if (-not $name.StartsWith($base, [StringComparison]::Ordinal)) { $projectionInputs[$name] = $CurrentInputs[$name] }
    }
    $projection = @($projectionInputs.Keys | Sort-Object -CaseSensitive | ForEach-Object {
        @{ path = $_; sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$projectionInputs[$_])).ToLowerInvariant() }
    })
    $evidence = [ordered]@{ profile = $Profile; addressables = $aa; backend = $Backend; sourceReceipt = $Previous.inputHash
        priorHostId = $oldHost.id; files = $projection; toolchain = $identity.toolchain; recoveredMeta = $recoveredMeta.ToArray() }
    $encoded = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $evidence -Depth 12 -Compress))
    $evidence['sha256'] = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($encoded)).ToLowerInvariant()
    Write-YLTestJson -Path (Join-Path $run.path 'replay-input-identity.json') -Value $evidence
    Sync-YLUnityHost -HostRecord $ReplayHost -Files $projectionInputs -Identity $evidence -EditorVersion $editor.version
    Copy-VerifiedPlayer $ContentRoot (Join-Path $ReplayHost.path 'ReplayFixture')
    $request = Join-Path $ReplayHost.path 'fixture.json'
    $receipt = Join-Path $run.path 'replay-build.json'
    Write-YLTestJson -Path $request -Value @{ token = $token; output = $receipt; addressables = $aa
        input = $evidence.sha256; backend = $Backend; catalogName = $catalogName }
    $requestedHash = Get-YLFrozenFileSha256 $request
    $process = Execute-Process $Unity $ReplayHost.path @('-batchmode','-nographics','-projectPath',$ReplayHost.path,
        '-buildTarget','StandaloneWindows64','-executeMethod','YLFramework.Testing.FrozenDeliveryEntry.RebuildForReplay',
        '-ylFrozenDelivery',$request,'-logFile',(Join-Path $run.path 'replay-editor.log')) 'replay-editor' 1800000
    $built = if ([IO.File]::Exists($receipt)) { Read-YLPackageEvidenceJson $receipt } else { $null }
    try { Assert-YLTestProcessSucceeded $process } catch {
        $message = if ($built) { $built['error'] } else { 'No replay Editor report' }
        throw "Baseline replay Editor process $($process.status) (exit $($process.exitCode)). $message"
    }
    if ($built.status -cne 'Built' -or $built.pid -ne $process.pid -or $built.requestSha256 -cne $requestedHash -or
        $built.token -cne $token -or $built.input -cne $evidence.sha256 -or $built.unity -cne $editor.version -or $built.addressables -cne $aa -or
        $built.shippedCatalogSha256 -cne $CurrentBuild.shippedCatalogSha256 -or $built.descriptionSha256 -cne $CurrentBuild.descriptionSha256 -or
        $built.pluginSha256 -cne $summary.pluginSha256) { throw 'The baseline replay did not bind to the exact published inputs.' }
    $install = Join-Path $ReplayHost.path 'ReplayPlayer'
    $shipped = Join-Path $install 'P_Data/StreamingAssets/aa'
    $files = @(Get-YLTestTreeFiles $ContentRoot)
    if (@(Get-YLTestTreeFiles $shipped).Count -ne $files.Count) { throw 'The paired Player content inventory differs.' }
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($ContentRoot, $file)
        if ((Get-YLFrozenFileSha256 $file) -cne (Get-YLFrozenFileSha256 (Join-Path $shipped $relative))) {
            throw "The paired Player content differs: $relative"
        }
    }
    $summary['replay'] = @{ host = $ReplayHost.path; hostId = $ReplayHost.id; input = $evidence.sha256; build = $built; process = $process }
    @{ side = 'baseline'; install = $install; host = $ReplayHost.path; build = $built; input = $evidence.sha256; progressDeadline = $false }
}

try {
    $hostLease = Lock-YLTestDirectory -Workspace $workspace -Path $hostRecord.path
    Assert-YLUnityHostIdle $hostRecord
    Start-YLPlayerRetention $hostRecord $run -FormalCandidate:$fixedRelease -KeepArtifacts:$KeepArtifacts
    $projection = [ordered]@{
        'FrozenDeliveryEntry.cs' = 'Assets/Fixture/Editor/FrozenDeliveryEntry.cs'
        'FrozenPlayerBuildEvidence.cs' = 'Assets/Fixture/Editor/FrozenPlayerBuildEvidence.cs'
        'FrozenDeliveryProbe.cs' = 'Assets/Fixture/Runtime/FrozenDeliveryProbe.cs'
        'FrozenDeliveryPerformance.cs' = 'Assets/Fixture/Runtime/FrozenDeliveryPerformance.cs'
        'FrozenDeliveryCacheEvidence.cs' = 'Assets/Fixture/Runtime/FrozenDeliveryCacheEvidence.cs'
        'FrozenDependencyFixture.cs' = 'Assets/Fixture/Runtime/FrozenDependencyFixture.cs'
        'FrozenFixture.asmdef' = 'Assets/Fixture/Runtime/FrozenFixture.asmdef'
        'FrozenFixture.Editor.asmdef' = 'Assets/Fixture/Editor/FrozenFixture.Editor.asmdef'
    }
    $inputs = [ordered]@{}
    $deps = [ordered]@{ 'com.unity.addressables' = $aa; 'com.unity.ugui' = $editor.ugui;
        'com.unity.nuget.newtonsoft-json' = $lock.dependencies['com.unity.nuget.newtonsoft-json'].version }
    $sources = @(); $binaries = @($Unity) + $module; $packageSource = 'Local'; $packages = @(); $cacheRoot = ''
    $dllPath = "$base$extension/Runtime/Plugins/Win64/ylresource_tink.dll"
    $dll = Get-YLTestPath (Join-Path $repo $dllPath)
    if ($DiagnosticNativeDll) { $dll = Get-YLTestPath $DiagnosticNativeDll -ReadOnly }
    if ($fixedRelease -or $PackReceipt) {
        $packageSource = 'LocalTarball'
        if ($fixedRelease) {
            $packages = @(Get-YLFrozenEncryptionClosure -Release $release)
            $summary['sourceCommit'] = $release.sourceCommit
            $summary['releaseManifestSha256'] = $release.releaseManifestSha256
            $summary['producer'] = $release.producer
            $summary['pluginSha256'] = $release.plugin.sha256
            $summary['importerSha256'] = $release.plugin.importerSha256
            $binaries += $release.path
        } else {
            $pack = Read-YLPackageEvidenceJson (Get-YLTestPath $PackReceipt)
            if ($pack.purpose -cne 'YLResource.LocalEncryptionPack' -or $pack.status -cne 'Packed' -or $pack.formalCandidate -ne $false -or $pack.negatives.Count -ne 5) { throw 'Invalid isolated archive pack receipt.' }
            $packages = @($pack.artifacts[$main], $pack.artifacts[$extension])
            if ($packages[0].id -cne $main -or $packages[1].id -cne $extension) { throw 'The diagnostic receipt does not identify the expected packages.' }
            $summary['pluginSha256'] = $pack.pluginSha256
            $summary['importerSha256'] = $pack.importerSha256
            $summary['packReceiptSha256'] = Get-YLFrozenFileSha256 $PackReceipt
            $binaries += $PackReceipt
        }
        $cacheRoot = Join-Path $hostRecord.path 'PackageInputs'
        foreach ($item in $packages) {
            $name = "$($item.id)-$($item.version).tgz"
            $inputs['Tarballs/' + $name] = Get-YLFrozenTarballBytes $item
            $deps[$item.id] = 'file:../Tarballs/' + $name
            $summary.packageArtifacts[$item.id] = @{ sha256 = $item.sha256; integrity = $item.integrity; payloadSha256 = $item.payloadSha256;
                path = $item.path; locator = $deps[$item.id] }
            $binaries += $item.path
        }
    } else {
        $sources = @(foreach ($id in $main, $extension) {
            $deps[$id] = "file:../$base$id"
            @(Get-YLTestTreeFiles (Join-Path $repo ($base + $id))) | ForEach-Object { [IO.Path]::GetRelativePath($repo, $_).Replace('\','/') }
        })
        $summary['pluginSha256'] = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
        $binaries += $dll
    }
    $tools = @($projection.Keys | ForEach-Object { 'Tools/Testing/ResourceEncryption/' + $_ })
    $files = $sources + $tools + @('Tools/Testing/ResourceEncryption/Invoke-FrozenDelivery.ps1', 'Tools/Testing/ResourceEncryption/FrozenLoopback.cs',
        'Tools/Testing/Common/FrozenPlayerEvidence.ps1', 'Tools/Testing/Common/FrozenEncryptionInputs.ps1', 'Tools/Testing/Common/FrozenPerformanceEvidence.ps1', 'Tools/Testing/Common/IntegrationPackageContent.ps1',
        'Tools/Testing/Common/Packages.ps1', 'Tools/Testing/Common/Process.ps1', 'Tools/Testing/Common/Workspace.ps1', 'Tools/Testing/Common/Results.ps1',
        'Tools/Testing/UnityHost.ps1', 'Tools/Testing/TestSuites.json', 'Tools/Testing/PackageCatalogContract.cs', 'Tools/Testing/Invoke-YLResourceAddressablesPlayer.ps1',
        'Tools/Testing/Common/ResourceAddressablesPlayer.ps1', 'Tools/Packaging/PackageCatalog.json', 'Tools/Packaging/Build-YLRegistryPackages.ps1') + $pins
    $identity = Get-YLTestInputIdentity -Repository $repo -Paths $files -Toolchain $binaries -Profile "Frozen-$Profile-$Backend-$Mode-$DeliveryLayout-$packageSource-$Experiment"
    $identityPath = Join-Path $run.path 'input-identity.json'
    Write-YLTestJson -Path $identityPath -Value $identity
    $summary['inputIdentity'] = $identityPath
    $summary['inputHash'] = $identity.sha256
    $summary['packageSource'] = $packageSource
    if ($fixedRelease) { Assert-YLFrozenCommittedFiles -Repository $repo -SourceCommit $SourceCommit -Files $identity.files }
    foreach ($item in $identity.files) {
        $leaf = [IO.Path]::GetFileName($item.path)
        $dest = if ($item.path.StartsWith($base, [StringComparison]::Ordinal)) { $item.path } elseif ($projection.Contains($leaf)) { $projection[$leaf] } else { continue }
        $inputs[$dest] = [IO.File]::ReadAllBytes((Join-Path $repo $item.path))
        if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($inputs[$dest])) -ine $item.sha256) { throw 'Frozen source drift.' }
    }
    if ($DiagnosticNativeDll) {
        $inputs[$dllPath] = [IO.File]::ReadAllBytes($dll)
        if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($inputs[$dllPath])) -ine $summary.pluginSha256) {
            throw 'The diagnostics DLL changed before projection.'
        }
    }
    $inputs['Packages/manifest.json'] = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json @{ dependencies = $deps }))
    Sync-YLUnityHost -HostRecord $hostRecord -Files $inputs -Identity $identity -EditorVersion $editor.version
    $expectedManifestHash = Get-YLFrozenFileSha256 (Join-Path $hostRecord.path 'Packages/manifest.json')
    foreach ($item in $packages) {
        if ((Get-YLFrozenFileSha256 (Join-Path $hostRecord.path $deps[$item.id].Substring('file:../'.Length))) -cne $item.sha256) {
            throw 'The owned archive copy changed before installation.'
        }
    }
    foreach ($file in $keyFile, $peerKeyFile) {
        $key = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
        try {
            $raw = [IO.File]::Open((Get-YLTestPath $file), 'CreateNew', 'Write', 'None')
            $createdKeys.Add($file)
            try { $raw.Write($key) } finally { $raw.Dispose() }
        } finally { [Array]::Clear($key) }
    }
    Add-Type -Path (Join-Path $PSScriptRoot 'FrozenLoopback.cs')
    $server = [YLFramework.Testing.FrozenLoopback]::new()
    try {
        $token = [guid]::NewGuid().ToString('N')
        $request = Join-Path $hostRecord.path 'fixture.json'
        $reply = Join-Path $run.path 'build.json'
        Write-YLTestJson -Path $request -Value @{ mode = $Mode; token = $token; prefix = $server.Prefix; output = $reply; addressables = $aa;
            input = $identity.sha256; packageSource = $packageSource; pluginSha256 = $summary.pluginSha256; backend = $Backend;
            catalogName = $catalogName; deliveryLayout = $DeliveryLayout; experiment = $Experiment }
        $expectedRequestHash = Get-YLFrozenFileSha256 $request
        $execution = Execute-Process $Unity $hostRecord.path @('-batchmode','-nographics','-projectPath',$hostRecord.path,
            '-buildTarget','StandaloneWindows64','-executeMethod','YLFramework.Testing.FrozenDeliveryEntry.Build',
            '-ylFrozenDelivery',$request,'-logFile',(Join-Path $run.path 'editor.log')) 'editor' 1800000 $cacheRoot
        if ([IO.File]::Exists($reply)) { $summary.build = Read-YLPackageEvidenceJson $reply }
        try { Assert-YLTestProcessSucceeded $execution } catch {
            $message = if ($summary.build -and $summary.build['error']) { ($summary.build['error'] -split '\r?\n', 2)[0] }
                elseif ($summary.build) { "Editor report status: $($summary.build['status'])" }
                else { 'No Editor report' }
            throw "Editor process $($execution.status) (exit $($execution.exitCode)). $message. See $($run.path)."
        }
        $built = $summary.build
        if ($null -eq $built -or $built.pid -ne $execution.pid -or $built.token -cne $token -or $built.mode -cne $Mode -or $built.input -cne $identity.sha256 -or
            $built.unity -cne $editor.version -or $built.addressables -cne $aa -or $built.source -cne 'Registry' -or $built['deliveryLayout'] -cne $DeliveryLayout -or
            $built.encryptionSource -cne $packageSource -or $built.mainSource -cne $packageSource -or
            $built.requestSha256 -cne $expectedRequestHash -or (Get-YLFrozenFileSha256 $request) -cne $expectedRequestHash -or
            $built.manifestSha256 -cne $expectedManifestHash) { throw 'Wrong build report.' }
        if ($packages.Count) {
            if ($built.importedPluginSha256 -cne $summary.pluginSha256 -or $built.importerSha256 -cne $summary.importerSha256) {
                throw 'The imported plugin DLL or importer metadata differs from the archive.'
            }
            $summary['installed'] = @(Assert-YLFrozenInstalledInputs -Build $built -Dependencies $deps -ProfileLock $lock -Packages $packages `
                -HostRoot $hostRecord.path -CacheRoot $cacheRoot)
        }
        if ($Mode -cin @('LateKeyFailure','TargetConflict','AmbiguousGroups','RemoteClosure','PrePublishAbort','LocalNoHashCollision')) {
            if ($built.status -cne 'Rejected') { throw "The exporter did not reject $Mode." }
            $target = Join-Path $hostRecord.path 'Delivery'
            if ($Mode -ceq 'TargetConflict') {
                if (@(Get-YLTestTreeFiles $target).Count -ne 1 -or [IO.File]::ReadAllText((Join-Path $target 'must-remain')) -cne 'unchanged') {
                    throw 'Publication modified a conflicting target.'
                }
            } elseif ([IO.Path]::Exists($target) -or ($Mode -ceq 'LateKeyFailure' -and -not $built.firstKeyReturned) -or
                ($Mode -ceq 'PrePublishAbort' -and -not $built.interruptedAtFinalKeyReturn)) {
                throw 'Build failure exposed the release or did not reach the specified key return.'
            }
        } elseif ($Mode -cin @('MissingPlayerRuntime','ChangedPlayerOutput')) {
            $expectedFile = if ($Mode -ceq 'ChangedPlayerOutput') { 'UnityPlayer.dll' } elseif ($Backend -ceq 'IL2CPP') { 'GameAssembly.dll' } else { 'MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll' }
            if ($built.status -cne 'Rejected' -or $built.changedPlayerFile -cne $expectedFile -or [IO.Path]::Exists((Join-Path $hostRecord.path 'Delivery'))) {
                throw 'A changed Player was not rejected before the final release directory appeared.'
            }
        } elseif ($Mode -ceq 'OfficialOutput') {
            $unowned = Join-Path $hostRecord.path 'ForeignOutput'
            if ($built.status -cne 'Rejected' -or $built.error -cnotmatch 'Official Addressables output path' -or
                [IO.Directory]::Exists((Join-Path $unowned 'aa')) -or [IO.Directory]::Exists((Join-Path $hostRecord.path 'Private')) -or
                [IO.File]::ReadAllText((Join-Path $unowned 'must-remain')) -cne 'unchanged') { throw 'The unproven official output was not refused before packing.' }
        } elseif ($Mode -ceq 'PublishChanged') {
            if ($built.status -cne 'Rejected' -or $built.error -cnotmatch 'Audited ordinary Bundle changed' -or -not $built.stagedChanged -or
                [IO.Directory]::Exists((Join-Path $hostRecord.path 'Delivery')) -or
                [IO.Directory]::Exists((Join-Path $hostRecord.path 'P'))) { throw 'Changed publication exposed a final Player or release.' }
        } elseif ($Mode -ceq 'SourceChanged') {
            if ($built.status -cne 'Rejected' -or $built.error -cnotmatch 'Audited plaintext Bundle changed|[Ss]ource.*chang' -or -not $built.sameLengthReplacement -or
                [IO.Directory]::Exists((Join-Path $hostRecord.path 'Delivery')) -or
                [IO.Directory]::Exists((Join-Path $hostRecord.path 'P'))) { throw 'Same-length plaintext drift exposed a final Player or release.' }
        } elseif ($Mode -ceq 'OutputOverlap') {
            if ($built.status -cne 'Rejected' -or $built.error -cnotmatch 'Ordinary Bundle build path overlaps' -or
                [IO.Directory]::Exists((Join-Path $hostRecord.path 'Delivery')) -or [IO.Directory]::Exists((Join-Path $hostRecord.path 'Private'))) {
                throw 'The preflight did not refuse the release output before packing any content.'
            }
        } elseif ($Mode -cin @('PlainBundleCopy','PlayerCopy')) {
            $plainPlacement = if ($Mode -ceq 'PlainBundleCopy') { 'plaintext bundle plain_' } else { 'plaintext Player packed ' }
            if ($built.status -cne 'Rejected' -or $built.error -cnotmatch '[0-9a-f]{32} Assets/FrozenData/private-texture\.png' -or
                $built.error -cnotmatch 'protected bundle protected_' -or $built.error -cnotmatch $plainPlacement) {
                throw 'The audit did not reject the actual duplicate by identity.'
            }
            if ([IO.Directory]::Exists((Join-Path $hostRecord.path 'Delivery')) -or
                [IO.Directory]::Exists((Join-Path $hostRecord.path 'P'))) { throw 'Audit rejection exposed a final Player or release.' }
        } else {
            if ($built.status -cne 'Built' -or $built.pluginSha256 -cne $summary.pluginSha256) { throw 'Missing matching Win64 native artifact.' }
            if ($Mode -ceq 'Positive' -and (-not $built.preflightIsWriteFree -or -not $built.excludedStaleAddressables)) {
                throw 'Missing write-free preflight or stale Addressables exclusion evidence.'
            }
            $deliveryRoot = Join-Path $hostRecord.path 'Delivery'
            $builtPlayer = if ($Mode -ceq 'Disabled') { Join-Path $hostRecord.path 'P' } else { Join-Path $deliveryRoot 'Player' }
            $playerData = Join-Path $builtPlayer 'P_Data/StreamingAssets/aa'
            $ordinaryRoot = Join-Path $hostRecord.path 'Ordinary'
            $summary['protectedDelivery'] = @(Assert-YLFrozenShippedContent -Build $built -Catalog $catalogName -PlayerData $playerData -DeliveryRoot $deliveryRoot `
                -Mode $Mode -Application 'yl.frozen.acceptance' -ReleaseId 'Delivery' -LoopbackPrefix $server.Prefix -DeliveryLayout $DeliveryLayout -OrdinaryRoot $ordinaryRoot)
            $playerRoot = $builtPlayer
            if ($Mode -cne 'Disabled') {
                $playerRoot = Join-Path $hostRecord.path 'Installed'
                Copy-VerifiedPlayer $builtPlayer $playerRoot
                $server.SetRoutes((Join-Path $deliveryRoot 'Upload/protected'), (Join-Path $deliveryRoot 'Upload/ordinary'),
                    [string[]]$built.remoteProtectedPaths, [string[]]$built.ordinaryPaths)
            }
            if ($Mode -ceq 'Performance' -and $Experiment -ceq 'Default') {
                $plainRoot = Join-Path $hostRecord.path 'PPlain'
                $plainCatalog = Join-Path $plainRoot "P_Data/StreamingAssets/aa/$catalogName"
                $plainPacked = Join-Path $hostRecord.path 'PlainPacked'
                if ((Get-YLFrozenFileSha256 $plainCatalog) -cne $built.plainCatalogSha256) { throw 'The installed control Catalog differs.' }
                foreach ($bundle in $built.performanceInputs) {
                    $file = Get-YLTestPath (Join-Path $hostRecord.path $bundle.plainPath)
                    if ((Get-YLFrozenFileSha256 $file) -cne $bundle.clearSha256 -or [IO.FileInfo]::new($file).Length -ne $bundle.clearBytes) {
                        throw 'The stock performance file differs from the protected clear input.'
                    }
                }
                $server.SetPlainControl($plainPacked, [string[]]@($built.performanceInputs | Where-Object mode -CEQ 'RemoteHttps' | ForEach-Object path))
                [long]$plainColdDownload = 0
                foreach ($bundle in $built.performanceInputs) { if ($bundle.mode -ceq 'RemoteHttps') { $plainColdDownload += $bundle.clearBytes } }
                $volume = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($hostRecord.path))
                $summary['performanceHost'] = @{ os = [Environment]::OSVersion.VersionString; runtime = [Runtime.InteropServices.RuntimeInformation]::FrameworkDescription;
                    processor = $env:PROCESSOR_IDENTIFIER; logicalProcessors = [Environment]::ProcessorCount; volume = $volume.Name;
                    fileSystem = $volume.DriveFormat; engineCache = 'dedicated-per-control'; osFileCache = 'not flushed';
                    timingOrigin = 'SubsystemRegistration'; testOrder = @(Get-YLFrozenPerformanceScenarios); systemLoad = 'CPU snapshot; no samples filtered';
                    cpu = @(Get-CimInstance -ClassName Win32_Processor -Property Name,LoadPercentage | Select-Object Name,LoadPercentage) }
            }
            $summary['http'] = @()
            $list = [Collections.Generic.List[object]]::new()
            $scenarios = @(switch -CaseSensitive ($Mode) {
                WrongRoute { @('wrong-route') }
                Disabled { @('disabled') }
                SyncWait { @('sync-wait') }
                GuardReview { @('cold','post-start-guard') }
                OrdinaryDependency { @('ordinary-dependency') }
                BadDependency { @('bad-dependency') }
                MultiGroup { @('multi-group') }
                LocalNoHash { @('layout-nohash') }
                Performance {
                    if ($Experiment -ceq 'LargeDownload') {
                        foreach ($case in @('slow','slow-retry','slow-restore','normal','cut','cut-retry','warm','changed','changed-retry')) {
                            'large-download-' + $case
                        }
                    } elseif ($Experiment -ceq 'BufferCapacity') {
                        @('cache-usage-cold','cache-usage-open','buffer-capacity','buffer-capacity-prefetch-fault')
                    }
                    elseif ($Experiment -ceq 'CacheUsage') { @('cache-usage-cold','cache-usage-open') }
                    elseif ($Experiment -ceq 'SpaceFaultOffline') {
                        @('large-download-space-prime','large-download-space-fault','large-download-space-offline')
                    }
                    else { Get-YLFrozenPerformanceScenarios }
                }
                Positive {
                    if ($DeliveryLayout -cne 'Remote') { @('layout-online','layout-offline','layout-bad-embedded') }
                    else { @('cold','online-warm','head-injection','offline-warm','no-cache-offline','shadow','missing-key','wrong-key','external-owner',
                        'held-download','held-load','provider-shadow','key-return','post-start-guard','tamper','repair','tamper-prefetched','fault-abort','fault-recovery') }
                }
            })
            if ($scenarios.Count -eq 0) { throw 'A Player workload requires at least one actual Player round.' }
            if ($Experiment -cin @('BufferCapacity','CacheUsage')) {
                $smallCipher = $built.performanceInputs | Sort-Object cipherBytes | Select-Object -First 1
                $smallCipherRoute = Get-EncryptedExperimentRoute $built $smallCipher
                $cacheSentinel = Join-Path $hostRecord.path 'fixed-cache-sentinel'
                [IO.File]::WriteAllText($cacheSentinel, 'External fixture file beside the owned fixed cache')
            }
            if ($Experiment -cin @('LargeDownload','SpaceFaultOffline')) {
                $largeCipher = $built.performanceInputs | Sort-Object cipherBytes -Descending | Select-Object -First 1
                $largeRoute = Get-EncryptedExperimentRoute $built $largeCipher
                $largeRate = 8MB
                $slowRate = [int64][Math]::Floor($largeCipher.cipherBytes / 144)
                $largeCut = [int64][Math]::Floor($largeCipher.cipherBytes * 0.42)
                $summary['transferParameters'] = @{ route = $largeRoute; artifact = $largeCipher; deadlineSeconds = 120
                    normalBytesPerSecond = $largeRate; slowBytesPerSecond = $slowRate; endpointCutAfterBytes = $largeCut }
                if ($Experiment -ceq 'SpaceFaultOffline') {
                    $oldCipher = $built.performanceInputs | Sort-Object cipherBytes | Select-Object -First 1
                    $oldRoute = Get-EncryptedExperimentRoute $built $oldCipher
                    $spaceCache = Join-Path $hostRecord.path 'cache-space-fault'
                    $oldFile = Join-Path $spaceCache ($oldCipher.cipherSha256 + '.bin')
                    $summary['spaceFault'] = @{ oldArtifact = $oldCipher; oldRoute = $oldRoute; cache = $spaceCache }
                }
            }
            $variants = @()
            if ($BaselineReceipt) {
                $previous = Read-YLPackageEvidenceJson (Get-YLTestPath $BaselineReceipt -ReadOnly)
                if ($previous.experiment -cne 'LargeDownload' -or $previous.profile -cne $Profile -or
                    $previous.addressables -cne $aa -or $previous.backend -cne $Backend -or $previous.build.status -cne 'Built' -or
                    $previous.pluginSha256 -cne $summary.pluginSha256) { throw 'The retained baseline profile or native DLL differs.' }
                $replayHost = New-YLTestDirectory -Workspace $workspace -Kind Host -Key ('RFD' + [guid]::NewGuid().ToString('N').Substring(0,8)) -NewOnly
                $replayLease = Lock-YLTestDirectory -Workspace $workspace -Path $replayHost.path
                Write-YLTestJson -Path (Join-Path $replayHost.path '.pinned') -Value @{ host = $replayHost.path; hostId = $replayHost.id; resultId = $run.id }
                $variants += New-TransferReplayPlayer $previous $replayHost $inputs $built $playerData
            }
            $variants += @{ side = 'current'; install = $playerRoot; host = $hostRecord.path; build = $built; input = $identity.sha256
                progressDeadline = ($ExpectProgressDeadline -or [bool]$BaselineReceipt) }
            $work = @(foreach ($variant in $variants) {
                foreach ($name in $scenarios) {
                    if ($variant.progressDeadline -and $name -cin @('large-download-slow-retry','large-download-slow-restore')) { continue }
                    @{ scenario = $name; variant = $variant }
                }
            })
            $processed = 0
            foreach ($item in $work) {
                $scenario = $item.scenario
                $variant = $item.variant
                $logStem = if ($BaselineReceipt) { $variant.side + '-' + $scenario } else { $scenario }
                if ($scenario -cin @('offline-warm','layout-offline','large-download-space-offline')) {
                    $server.Pause()
                    $probe = [Net.Sockets.TcpClient]::new()
                    try {
                        try { $probe.Connect([uri]::new($server.Prefix).Host, [uri]::new($server.Prefix).Port) }
                        catch [Net.Sockets.SocketException] { $summary['endpointWasClosed'] = $true }
                        if ($probe.Connected) { throw 'The offline endpoint still accepts connections.' }
                    } finally { $probe.Dispose() }
                }
                if ($scenario -cin @('shadow','layout-bad-embedded')) { $server.Resume() }
                if ($scenario -ceq 'fault-abort') { $server.InterruptNextProtectedTransfer() }
                if ($scenario -cin @('held-download','held-load')) {
                    $gateName = if ($scenario -ceq 'held-download') { 'HeldGet' } else { 'HeldLoad' }
                    $held = Get-YLTestPath (Join-Path $playerRoot $gateName)
                    if ([IO.Path]::Exists($held)) { throw 'The held download requires a fresh gate directory.' }
                    $null = [IO.Directory]::CreateDirectory($held)
                    $server.HoldNextProtectedTransfer($held)
                }
                if ($scenario -cin @('cache-usage-open','buffer-capacity-prefetch-fault')) {
                    $cacheGate = Get-YLTestPath (Join-Path $variant.host ($logStem + '-gate'))
                    if ([IO.Path]::Exists($cacheGate)) { throw 'The cache inspection requires a fresh endpoint gate.' }
                    $null = [IO.Directory]::CreateDirectory($cacheGate)
                    $server.HoldNextProtectedTransfer($cacheGate)
                }
                $runtime = Join-Path $run.path ($logStem + '.json')
                $plainSample = $scenario -clike 'performance-plain-*'
                $actualRoot = if ($plainSample) { $plainRoot } elseif ($Mode -ceq 'WrongRoute') { Join-Path $hostRecord.path 'PWrong' } else { $variant.install }
                if ($scenario -clike 'layout-*') {
                    $actualRoot = Join-Path $hostRecord.path $scenario
                    Copy-VerifiedPlayer $builtPlayer $actualRoot
                    if ($scenario -ceq 'layout-bad-embedded') {
                        $description = Read-YLPackageEvidenceJson (Join-Path $playerData 'yl-encrypted-bundles.json')
                        $local = @($description.bundles | Where-Object keyId -CEQ 'K1')[0]
                        $cipher = Join-Path $actualRoot "P_Data/StreamingAssets/aa/yl-encrypted/$($local.groupGuid)/$($local.path)"
                        $stream = [IO.File]::Open($cipher, 'Open', 'ReadWrite', 'None')
                        try { $value = $stream.ReadByte(); $stream.Position = 0; $stream.WriteByte([byte]($value -bxor 1)) } finally { $stream.Dispose() }
                        $damagedSha = Get-YLFrozenFileSha256 $cipher
                    }
                }
                $actualExe = Join-Path $actualRoot 'P.exe'
                $expectedCatalog = if ($plainSample) { $built.plainCatalogSha256 } elseif ($Mode -ceq 'WrongRoute') { $built.changedCatalogSha256 } else { $built.shippedCatalogSha256 }
                $expectedDescription = if ($plainSample) { '' } elseif ($Mode -ceq 'WrongRoute') { $built.changedDescriptionSha256 } else { [string]$built.descriptionSha256 }
                if ($Mode -ceq 'Performance' -and $scenario -cne 'large-download-space-offline') {
                    # Prove the TCP listener is up without warming either content route.
                    $endpoint = [uri]$server.Prefix
                    $connection = [Net.Sockets.TcpClient]::new($endpoint.Host, $endpoint.Port)
                    $connection.Dispose()
                }
                $prior = @($server.CaptureRoundAsync().GetAwaiter().GetResult())
                if ($prior.Count -ne $processed) { throw 'A loopback request arrived outside a Player round.' }
                Assert-YLFrozenPlayerBinaries -Player $actualExe -Build $variant.build -Backend $Backend -ExtensionAssembly 'YLFramework.Resource.Encryption' `
                    -NativePluginSha256 $summary.pluginSha256
                $arguments = @('-batchmode','-logFile',(Join-Path $run.path ($logStem + '-game.log')),
                    '-ylFrozenScenario',$scenario,'-ylFrozenResult',$runtime)
                if ($plainSample) {
                    $plainDownload = if ($scenario -ceq 'performance-plain-cold') { $plainColdDownload } else { 0L }
                    $arguments += '-ylFrozenLayout', $DeliveryLayout, '-ylFrozenPlainDownloadBytes', $plainDownload.ToString([Globalization.CultureInfo]::InvariantCulture)
                } else {
                    $cacheName = $scenario
                    if ($Experiment -ceq 'LargeDownload') {
                        if ($scenario -clike 'large-download-slow*') { $cacheName = 'slow' }
                        elseif ($scenario -clike 'large-download-cut*' -or $scenario -ceq 'large-download-warm') { $cacheName = 'cut' }
                        elseif ($scenario -clike 'large-download-changed*') { $cacheName = 'changed' }
                        $clientRate = if ($scenario -cin @('large-download-slow','large-download-slow-retry')) { $slowRate } else { $largeRate }
                        $stopAfter = if ($scenario -ceq 'large-download-cut') { $largeCut } else { 0 }
                        $changed = $scenario -ceq 'large-download-changed'
                        $responseTag = if ($changed) { 'f' * 64 } else { $largeCipher.cipherSha256 }
                        $responseMode = if ($changed) { 'changed' } else { 'honor' }
                        $server.ShapeTransfer($largeRoute, $clientRate, $stopAfter, $responseTag, $responseMode)
                    }
                    if ($Experiment -ceq 'SpaceFaultOffline') {
                        $cacheName = 'space-fault'
                        if ($scenario -ceq 'large-download-space-fault') {
                            $server.ShapeTransfer($largeRoute, $largeRate, 0, $largeCipher.cipherSha256)
                        }
                    }
                    $experimentCache = Join-Path $variant.host "cache-$cacheName"
                    $arguments += '-ylFrozenKey', $keyFile, '-ylFrozenCache', $experimentCache
                    if ($scenario -cne 'layout-offline') { $arguments += '-ylFrozenPeerKey', $peerKeyFile }
                    if ($scenario -clike 'cache-usage-*') { $arguments += '-ylFrozenCacheSentinel', $cacheSentinel }
                    if ($scenario -cin @('cache-usage-open','buffer-capacity-prefetch-fault')) { $arguments += '-ylFrozenCacheGate', $cacheGate }
                }
                $installProof = $null
                if ($scenario -ceq 'layout-offline') {
                    $protectedInstall = Invoke-YLFrozenReadOnlyInstallation -OwnedHostRoot $hostRecord.path -InstallRoot $actualRoot -RunPlayer {
                        Execute-Process $actualExe $actualRoot $arguments $scenario 150000
                    }
                    $process = $protectedInstall.process
                    $installProof = $protectedInstall.proof
                } elseif ($scenario -ceq 'cache-usage-cold') {
                    $process = Invoke-FixedColdCacheInspection $experimentCache $variant.host {
                        Execute-Process $actualExe $actualRoot $arguments $logStem 260000
                    }
                } else {
                    $timeout = if ($Experiment -cne 'Default') { 260000 } else { 150000 }
                    $process = Execute-Process $actualExe $actualRoot $arguments $logStem $timeout
                }
                $after = @($server.CaptureRoundAsync().GetAwaiter().GetResult())
                $observed = if ([IO.File]::Exists($runtime)) { Read-YLPackageEvidenceJson $runtime } else { $null }
                $quit = if ([IO.File]::Exists($runtime + '.quit')) { Read-YLPackageEvidenceJson ($runtime + '.quit') } else { $null }
                $requests = @($after | Select-Object -Skip $processed)
                $processed = $after.Count
                $list.Add(@{ scenario = $scenario; variant = $variant.side; process = $process; observed = $observed; quit = $quit; requests = $requests; readOnlyInstall = $installProof })
                $summary.runs = $list.ToArray(); $summary.http = $after
                Assert-YLFrozenPlayerBackend -Row $observed -Expected $Backend
                if ($observed.pid -ne $process.pid -or $observed.token -cne $token -or $observed.scenario -cne $scenario -or
                    $observed.unity -cne $editor.version -or $observed.inputHash -cne $variant.input -or
                    $observed.catalogSha256 -cne $expectedCatalog -or [string]$observed.descriptionSha256 -cne [string]$expectedDescription) {
                    throw "Player evidence does not bind to its build inputs and shipped Catalog: $scenario. $($observed.error)"
                }
                if ($scenario -ceq 'fault-abort') {
                    $abortedFiles = @(Get-YLTestTreeFiles (Join-Path $playerRoot 'Cache-abort'))
                    $parts = @($abortedFiles | Where-Object { $_ -clike '*.part' })
                    $proofs = @($abortedFiles | Where-Object { $_ -clike '*.part.owner' })
                    if ($process.status -cne 'NonZeroExit' -or $process.exitCode -ne 73 -or -not $process.observedExit -or $process.activeProcesses -ne 0 -or
                        -not $process.streamsDrained -or $process.forcedCleanup -or $process.timedOut -or $process.cancelled -or $null -ne $quit -or
                        $observed.status -cne 'BeforeUnexpectedExit' -or
                        -not $observed.sawOwnedPart -or $parts.Count -ne 1 -or $proofs.Count -ne 1 -or $proofs[0] -cne ($parts[0] + '.owner') -or
                        $requests.Count -ne 1 -or $requests[0].method -cne 'GET' -or
                        $requests[0].status -ne 200 -or $requests[0].bytes -le 0 -or $requests[0].bytes -ge $built.protectedBytes -or $requests[0].completed) {
                        throw 'Unexpected-exit control did not leave exactly one incomplete owned request and no ordinary quit.'
                    }
                    $record = [IO.File]::ReadAllBytes($proofs[0])
                    if ($record.Length -ne 24 -or [BitConverter]::ToUInt32($record, 0) -ne 0x31504c59 -or
                        ('{0:X8}-{1:X16}' -f [BitConverter]::ToUInt32($record, 12), [BitConverter]::ToUInt64($record, 4)) -cne $observed.abandonedTransferFileId -or
                        ('{0:X16}' -f [BitConverter]::ToUInt64($record, 16)) -cne $observed.abandonedTransferCreated -or
                        $observed.abandonedTransferReceivedBytes -le 0 -or $observed.abandonedTransferReceivedBytes -gt $requests[0].bytes) {
                        throw 'The abandoned part did not retain the physical identity recorded by the running Player.'
                    }
                    $list[$list.Count - 1]['transferProvenance'] = @{
                        part = $parts[0]; proof = $proofs[0]; sha256 = Get-YLFrozenFileSha256 $proofs[0]
                        fileId = $observed.abandonedTransferFileId; created = $observed.abandonedTransferCreated
                    }
                    continue
                }
                try { Assert-YLTestProcessSucceeded $process } catch {
                    $message = if ($observed) { $observed['error'] } else { 'No Player report' }
                    throw "Player process $($process.status) (exit $($process.exitCode)): $scenario. $message"
                }
                Assert-YLFrozenPlayerBackend -Row $quit -Expected $Backend
                if ($observed.status -cne 'Passed' -or $quit.status -cne 'Passed' -or $quit.pid -ne $process.pid -or -not $quit.quit -or $quit.token -cne $token -or
                    $quit.scenario -cne $scenario -or $quit.unity -cne $editor.version -or $quit.inputHash -cne $variant.input -or
                    $quit.catalogSha256 -cne $expectedCatalog -or [string]$quit.descriptionSha256 -cne [string]$expectedDescription) {
                    throw "Missing Player/quit evidence for $scenario."
                }
                if ($Experiment -ceq 'LargeDownload') {
                    $transfer = $observed.largeDownload
                    $expected = switch ($scenario) {
                        'large-download-cut' { 'TransferFailed' }
                        'large-download-changed' { 'TransferFailed' }
                        'large-download-slow' { if ($variant.progressDeadline) { 'Complete' } else { 'TransferTimeout' } }
                        'large-download-slow-retry' { 'TransferTimeout' }
                        default { 'Complete' }
                    }
                    if ($transfer.termination -cne $expected -or $transfer.cipherSha256 -cne $largeCipher.cipherSha256 -or
                        $transfer.expectedBytes -ne $largeCipher.cipherBytes -or $transfer.keysOpened -ne 0 -or $transfer.mounts -ne 0) {
                        throw "$scenario did not use the expected ciphertext and outcome."
                    }
                    if ($scenario -ceq 'large-download-warm') {
                        if ($transfer.receivedBytes -ne 0 -or $requests.Count -ne 0) { throw 'Warm ciphertext reached the endpoint.' }
                    } else {
                        if ($requests.Count -ne 1 -or $requests[0].target -cne $largeRoute -or $requests[0].method -cne 'GET' -or
                            $requests[0].status -ne 200 -or $requests[0].range -or $requests[0].entity -cne ('"' + $responseTag + '"') -or
                            $transfer.receivedBytes -le 0 -or $transfer.receivedBytes -gt $requests[0].bytes) {
                            throw 'Missing attributable client bytes for the complete-representation request.'
                        }
                        if ($expected -ceq 'Complete' -and $transfer.receivedBytes -ne $largeCipher.cipherBytes) {
                            throw 'The complete ciphertext was not received in full.'
                        }
                        if ($scenario -ceq 'large-download-slow' -and $variant.progressDeadline -and $transfer.elapsedSeconds -le 120) {
                            throw 'The replay did not cross the original total deadline.'
                        }
                        if ($expected -ceq 'TransferTimeout' -and ($transfer.elapsedSeconds -lt 118 -or
                            ([DateTimeOffset]::Parse($transfer.lastUtc) - [DateTimeOffset]::Parse($transfer.firstUtc)).TotalSeconds -lt 115)) {
                            throw 'The product timeout did not occur during continuous input.'
                        }
                        if ($scenario -ceq 'large-download-cut' -and ($requests[0].termination -cne 'EndpointTruncated' -or $transfer.receivedBytes -ne $largeCut)) {
                            throw 'The endpoint cutoff and actual received offset differ.'
                        }
                        if ($scenario -ceq 'large-download-changed' -and (-not $requests[0].completed -or
                            $transfer.receivedBytes -ne $largeCipher.cipherBytes -or $transfer.error -cnotmatch 'EncryptedBundleIntegrityException')) {
                            throw 'The same-length replacement did not reach and fail final ciphertext integrity.'
                        }
                    }
                    Write-YLTestJson -Path (Join-Path $run.path ($logStem + '-transfer.json')) -Value $list[$list.Count - 1]
                    continue
                }
                if ($scenario -clike 'cache-usage-*') {
                    $coldCache = $scenario -ceq 'cache-usage-cold'
                    $inspection = $observed.cacheInspection
                    $queries = @($inspection.rounds)
                    if ($queries.Count -ne 4 -or @($queries | Where-Object {
                        $_.keyDelta -ne 0 -or $_.mountDelta -ne 0 -or
                        ((ConvertTo-Json -InputObject $_.before -Depth 8 -Compress) -cne (ConvertTo-Json -InputObject $_.after -Depth 8 -Compress)) -or
                        @($_.before | Where-Object { [string]::IsNullOrEmpty($_.fileId) }).Count -ne 0
                    }).Count -ne 0) {
                        throw 'Cache inspection lacks repeatable file-identity, content or runtime-use evidence.'
                    }
                    if ($coldCache) {
                        if (-not $inspection.writeDenied -or -not $inspection.readDenied -or $requests.Count -ne 0 -or
                            ($queries.phase -join ',') -cne 'missing,missing,read-only-denied-and-unattributed,read-only-denied-and-unattributed') {
                            throw 'Cold cache inspection did not exercise the restricted fixture without HTTP.'
                        }
                    } elseif (-not $inspection.closedSessionRejected -or -not $observed.experimentClosed -or
                        -not $observed.keyLeasesReturned -or $observed.openKeys -ne 0 -or $observed.creates -ne 0 -or
                        ($queries.phase -join ',') -cne 'in-flight,in-flight,warm,warm' -or $requests.Count -ne 1 -or
                        $requests[0].target -cne $smallCipherRoute -or $requests[0].method -cne 'GET' -or
                        -not $requests[0].held -or -not $requests[0].completed -or $requests[0].bytes -ne $smallCipher.cipherBytes -or
                        [DateTimeOffset]$requests[0].at -ge [DateTimeOffset]$queries[0].startedUtc) {
                        throw 'Warm cache inspection made unintended requests or missed the controlled prefetch.'
                    }
                    continue
                }
                if ($scenario -ceq 'buffer-capacity-prefetch-fault') {
                    if (-not $observed.experimentFaultRecovered -or -not $observed.experimentClosed -or
                        -not $observed.keyLeasesReturned -or $observed.openKeys -ne 0 -or $observed.creates -ne 0 -or
                        $requests.Count -ne 1 -or $requests[0].target -cne $smallCipherRoute -or $requests[0].method -cne 'GET' -or
                        -not $requests[0].held) {
                        throw 'The failed prefetch did not preserve the original error and release the session.'
                    }
                    continue
                }
                if ($scenario -ceq 'buffer-capacity') {
                    if ($observed.buffers.nativeSha256 -cne $summary.pluginSha256 -or $observed.buffers.calibrations.Count -ne 2 -or
                        $observed.buffers.cases.Count -ne 3 -or $requests.Count -ne 2 -or -not $observed.experimentClosed -or
                        -not $observed.keyLeasesReturned) {
                        throw 'The capacity case did not bind to the diagnostics DLL and both real protected inputs.'
                    }
                    continue
                }
                if ($Experiment -ceq 'SpaceFaultOffline') {
                    $download = $observed.largeDownload
                    if (-not [IO.File]::Exists($oldFile) -or (Get-YLFrozenFileSha256 $oldFile) -cne $oldCipher.cipherSha256 -or
                        @(Get-YLTestTreeFiles $spaceCache | Where-Object { $_ -clike '*.part' }).Count -ne 0 -or
                        [IO.File]::Exists((Join-Path $spaceCache ($largeCipher.cipherSha256 + '.bin')))) {
                        throw 'The injected failure changed the old cipher or left an incomplete entry.'
                    }
                    switch ($scenario) {
                        'large-download-space-prime' {
                            if ($requests.Count -ne 1 -or $requests[0].target -cne $oldRoute -or $requests[0].method -cne 'GET' -or
                                -not $requests[0].completed -or $download.termination -cne 'Complete' -or
                                $download.receivedBytes -ne $oldCipher.cipherBytes -or $download.keysOpened -ne 0 -or $download.mounts -ne 0) {
                                throw 'The old ciphertext did not prepare through its actual remote route.'
                            }
                        }
                        'large-download-space-fault' {
                            if ($requests.Count -ne 1 -or $requests[0].target -cne $largeRoute -or $requests[0].method -cne 'GET' -or
                                $download.termination -cne 'TransferFailed' -or $download.writeHResult -ne -2147024784 -or
                                $download.stagedBytes -lt 1MB -or $download.receivedBytes -le $download.stagedBytes -or
                                $download.receivedBytes -gt $requests[0].bytes -or $download.keysOpened -ne 0 -or $download.mounts -ne 0) {
                                throw 'The actual transfer did not hit the injected staged write condition.'
                            }
                        }
                        'large-download-space-offline' {
                            if (-not $summary.endpointWasClosed -or $requests.Count -ne 0 -or $download.receivedBytes -ne 0 -or
                                $download.termination -cne 'OldAssetReadOffline' -or $download.keysOpened -ne 1 -or $download.mounts -ne 1 -or
                                -not $observed.keyLeasesReturned) { throw 'The unchanged old ciphertext did not load at the closed endpoint.' }
                        }
                    }
                    continue
                }
                if ($scenario -cne 'disabled' -and ($observed.keyLeasesReturned -isnot [bool] -or -not $observed.keyLeasesReturned -or
                    $observed.issuedKeys -ne $observed.returnKeys)) { throw "Key lease return evidence is incomplete for $scenario." }
                if ($scenario -ceq 'ordinary-dependency') {
                    $remote = @($requests | Where-Object target -CLike '/r/Delivery/*')
                    $ordinary = @($requests | Where-Object target -CLike '/o/*')
                    if (-not $observed.sentinel -or $requests.Count -ne 2 -or $remote.Count -ne 1 -or $ordinary.Count -ne 1 -or
                        $remote[0].bytes -ne $built.remoteProtectedBytes -or $ordinary[0].target -cne ('/o/' + $built.ordinaryPaths[0]) -or
                        $ordinary[0].bytes -ne [IO.FileInfo]::new((Join-Path $deliveryRoot ('Upload/ordinary/' + $built.ordinaryPaths[0]))).Length -or
                        @($requests | Where-Object { $_.method -cne 'GET' -or $_.status -ne 200 -or -not $_.completed }).Count) {
                        throw 'No complete transfer of both the protected prefab and its ordinary upload dependency.'
                    }
                }
                if ($scenario -ceq 'bad-dependency' -and (-not $observed.rejectedBeforeCreate -or -not $observed.sentinel -or
                    $observed.creates -ne 1 -or $observed.unloads -ne 1 -or $observed.streamsClosed -ne 1 -or $observed.openKeys -ne 1 -or
                    $observed.completedTransferBytes -ne $built.remoteProtectedBytes -or $requests.Count -ne 1 -or
                    $requests[0].target -cne ('/r/Delivery/' + $built.remoteProtectedPaths[0]) -or $requests[0].method -cne 'GET' -or
                    $requests[0].status -ne 200 -or $requests[0].bytes -ne $built.remoteProtectedBytes -or -not $requests[0].completed)) {
                    throw 'Only the initial protected prefetch may transfer; the bad dependency must not mount or affect its peer.'
                }
                if ($scenario -ceq 'multi-group') {
                    $expected = [Collections.Generic.Dictionary[string,long]]::new([StringComparer]::Ordinal)
                    foreach ($path in $built.remoteProtectedPaths) {
                        $payload = Join-Path $deliveryRoot "Upload/protected/$path"
                        $expected.Add("/r/Delivery/$path", [IO.FileInfo]::new($payload).Length)
                    }
                    if ($expected.Count -ne 2 -or $requests.Count -ne 2 -or -not $observed.sentinel -or $observed.creates -ne 4 -or
                        $observed.streamsClosed -ne 4 -or $observed.openKeys -ne 4 -or $observed.completedTransferBytes -ne $built.remoteProtectedBytes) {
                        throw 'The four-group fixture did not exercise both sources and close all four streams.'
                    }
                    foreach ($requestRow in $requests) {
                        if (-not $expected.ContainsKey($requestRow.target) -or $requestRow.bytes -ne $expected[$requestRow.target] -or
                            $requestRow.method -cne 'GET' -or $requestRow.status -ne 200 -or -not $requestRow.completed) { throw 'Wrong four-group transfer.' }
                        $null = $expected.Remove($requestRow.target)
                    }
                }
                if ($scenario -clike 'layout-*') {
                    if (-not $observed.installedPrefetchValidated -or ($DeliveryLayout -ceq 'Mixed' -and -not $observed.refusedUnpreparedRemote)) { throw 'No installed prefetch / remote gate proof.' }
                    if ($scenario -ceq 'layout-bad-embedded') {
                        if (-not $observed.rejectedBeforeCreate -or $requests.Count -ne 0 -or $damagedSha -cne (Get-YLFrozenFileSha256 $cipher)) { throw 'Bad installed ciphertext reached Unity, network or a repair write.' }
                    } else {
                        if (-not $observed.localBeforeRemoteReady -or -not $observed.sentinel) { throw 'The installed chapter did not reach its sentinel independently.' }
                        if ($scenario -ceq 'layout-offline' -and (-not $observed.installedRootWriteDenied -or -not $observed.installedContentWriteDenied -or
                            -not $installProof.unchanged -or $observed.openKeys -ne 1)) { throw 'The Player did not prove read-only installation and a single local key.' }
                        if ($scenario -ceq 'layout-offline' -and $DeliveryLayout -ceq 'Mixed' -and -not $observed.remoteCacheWasBlocked) { throw 'The fixture did not prove remote lock contention.' }
                        if ($scenario -ceq 'layout-online' -and $DeliveryLayout -ceq 'Mixed') {
                            if (-not $observed.localBeforePreparationReleased -or -not $observed.remotePrepared -or -not $observed.readySnapshotZero -or $requests.Count -ne 1 -or
                                $requests[0].target -cne ('/r/Delivery/' + $built.remoteProtectedPaths[0]) -or $requests[0].method -cne 'GET' -or
                                $requests[0].bytes -ne $built.remoteProtectedBytes -or -not $requests[0].completed) { throw 'The remote chapter did not perform one verified transfer.' }
                        } elseif ($requests.Count -ne 0) { throw 'The local-only round reached the endpoint.' }
                    }
                }
                if ($scenario -ceq 'post-start-guard' -and (-not $observed.rejectedReorderedCandidates -or -not $observed.matchedFirstCandidate -or
                    -not $observed.rejectedAfterDrift -or -not $observed.leftForeignRegistration -or -not $observed.reopenedCacheLock)) {
                    throw 'Candidate order or drift cleanup evidence is incomplete.'
                }
                if ($scenario -cin @('external-owner','key-return') -and (-not $observed.closeWaitedForUnload -or -not $observed.sharedClose -or
                    -not $observed.reenteredCloseDuringKeyReturn -or -not $observed.reopenedCacheLock)) { throw 'Shared session close and native drain evidence is incomplete.' }
                if ($scenario -ceq 'cold' -and ($observed.completedTransferBytes -ne $built.protectedBytes -or -not $observed.refusedCloseWithPrefetchOwner -or
                    ($Mode -ceq 'Positive' -and (-not $observed.stockDefaultsVerified -or -not $observed.mixedPrefetchVerified)))) {
                    throw 'Missing completed progress, prefetch ownership, stock defaults or mixed prefetch evidence.'
                }
                if ($scenario -ceq 'tamper-prefetched') {
                    if (-not $observed.rejectedBeforeCreate -or -not $observed.invalidatedSize -or -not $observed.retriedWithPrefetchHeld -or
                        [string]::IsNullOrEmpty($observed.retryAfterUtc) -or $requests.Count -ne 2 -or @($requests | Where-Object {
                            $_.method -cne 'GET' -or $_.target -cne ('/r/Delivery/' + $built.protectedPaths[0]) -or $_.status -ne 200 -or
                            $_.bytes -ne $built.protectedBytes -or -not $_.completed
                        }).Count -ne 0 -or [DateTimeOffset]$requests[0].at -ge [DateTimeOffset]$observed.retryAfterUtc -or
                        [DateTimeOffset]$requests[1].at -lt [DateTimeOffset]$observed.retryAfterUtc) {
                        throw 'Corruption must fail without a transfer until the explicit retry, even with an older prefetch held.'
                    }
                }
                if ($scenario -cin @('online-warm','shadow','wrong-route','missing-key','wrong-key','external-owner','disabled','provider-shadow','key-return','post-start-guard','tamper') -or
                    $scenario -clike 'performance-*-warm-*') {
                    [YLFramework.Testing.FrozenLoopback]::RequireQuiet([YLFramework.Testing.FrozenLoopback+Request[]]$requests)
                }
                if ($scenario -ceq 'head-injection') {
                    if ($requests.Count -ne 1 -or $requests[0].method -cne 'HEAD' -or $requests[0].target -cne ('/r/Delivery/' + $built.protectedPaths[0]) -or $requests[0].bytes -ne 0) {
                        throw 'Invalid online-warm HEAD counterexample.'
                    }
                    try { [YLFramework.Testing.FrozenLoopback]::RequireQuiet([YLFramework.Testing.FrozenLoopback+Request[]]$requests) }
                    catch { if ($_.Exception.ToString() -cnotmatch 'received 1 content request') { throw }; $summary['warmGateRejectedHead'] = $true }
                    if (-not $summary['warmGateRejectedHead']) { throw 'The same online-warm gate accepted a HEAD attempt.' }
                }
                if ($scenario -clike 'performance-*-cold') {
                    $prefix = if ($scenario -ceq 'performance-plain-cold') { '/p/' } else { '/r/' }
                    $remoteInputs = @($built.performanceInputs | Where-Object mode -CEQ 'RemoteHttps')
                    [long]$actual = 0; [long]$expected = 0
                    foreach ($requestRow in $requests) { $actual += $requestRow.bytes }
                    foreach ($bundleRow in $remoteInputs) { $expected += if ($prefix -ceq '/r/') { $bundleRow.cipherBytes } else { $bundleRow.clearBytes } }
                    if ($requests.Count -ne $remoteInputs.Count -or $actual -ne $expected -or
                        @($requests | Where-Object { -not $_.target.StartsWith($prefix,[StringComparison]::Ordinal) -or $_.status -ne 200 -or $_.method -cne 'GET' -or -not $_.completed }).Count -ne 0) {
                        throw 'Cold performance requests differ from the remote fixture inputs.'
                    }
                }
                if ($scenario -cin @('cold','held-download','held-load','repair','fault-recovery','sync-wait')) {
                    $protected = @($requests | Where-Object { $_.target -clike '/r/*' })
                    $ordinary = @($requests | Where-Object { $_.target -clike '/o/*' })
                    if ($protected.Count -ne 1 -or $protected[0].method -cne 'GET' -or $protected[0].path -cne $built.protectedPaths[0] -or
                        $protected[0].bytes -ne $built.protectedBytes -or $protected[0].status -ne 200 -or -not $protected[0].completed -or
                        $ordinary.Count -ne [int]($scenario -ceq 'cold') -or $requests.Count -ne ($protected.Count + $ordinary.Count)) {
                        throw "$scenario did not receive the declared protected and ordinary GETs."
                    }
                    if ($scenario -ceq 'fault-recovery') {
                        $aborted = @($list | Where-Object { $_.scenario -ceq 'fault-abort' -and $_.variant -ceq $variant.side })
                        if ($aborted.Count -ne 1 -or -not $observed.noParts -or
                            [IO.Path]::Exists($aborted[0].transferProvenance.part) -or [IO.Path]::Exists($aborted[0].transferProvenance.proof)) {
                            throw 'The restarted Player did not remove the abandoned payload and its provenance.'
                        }
                    }
                    if ($scenario -ceq 'held-download' -and (-not $protected[0].held -or -not $observed.observerStartedTransfer -or -not $observed.loadedWhileTransferHeld -or
                        -not $observed.observerCanceledWhileTransferHeld -or $observed.downloadReleasedBeforeAsset -isnot [bool] -or
                        -not $observed.downloadReleasedBeforeAsset -or -not $observed.sentinel -or $observed.creates -ne 1 -or $observed.unloads -ne 1 -or
                        $observed.heldTransferBytes -le 0 -or $observed.heldTransferBytes -ge $built.protectedBytes -or
                        $observed.completedTransferBytes -ne $built.protectedBytes)) {
                        throw 'In-flight sharing, per-observer cancellation or release-order evidence is incomplete.'
                    }
                    if ($scenario -cin @('held-download','held-load') -and (-not $protected[0].held -or $observed.heldLoadBytes -le 0 -or
                        $observed.heldLoadBytes -ge $built.protectedBytes -or $observed.completedLoadBytes -ne $built.protectedBytes -or
                        ($scenario -ceq 'held-load' -and -not $observed.singleLoadAttempt))) { throw 'Asset load did not report one exact partial and completed transfer.' }
                }
            }
            $server.Pause()
            if ($server.Snapshot().Length -ne $processed) { throw 'A loopback request arrived after the final Player round.' }
            if ($Mode -ceq 'Performance' -and $Experiment -ceq 'Default') {
                $summary['performance'] = Get-YLFrozenPerformanceSummary -Runs $list.ToArray() -Build $built -DeliveryLayout $DeliveryLayout
                if (-not $summary.performance.passed) { throw ('Performance budget failed: ' + ($summary.performance.budgetFailures -join '; ')) }
            }
            if ($Experiment -ceq 'LargeDownload') {
                $summary['repeatedBodyBytes'] = [ordered]@{}
                foreach ($variant in $variants) {
                    $rows = @($list | Where-Object variant -CEQ $variant.side)
                    $slow = @($rows | Where-Object scenario -CIn @('large-download-slow','large-download-slow-retry','large-download-slow-restore'))
                    $cut = @($rows | Where-Object scenario -CIn @('large-download-cut','large-download-cut-retry'))
                    $summary.repeatedBodyBytes[$variant.side] = @{ withinDeadline = 0L
                        continuousSlow = [long](($slow.observed.largeDownload.receivedBytes | Measure-Object -Sum).Sum) - $largeCipher.cipherBytes
                        truncated = [long](($cut.observed.largeDownload.receivedBytes | Measure-Object -Sum).Sum) - $largeCipher.cipherBytes }
                }
            }
        }
    } finally { $server.Dispose() }
    if ($release) {
        Assert-YLFrozenSourceCommit -Repository $repo -SourceCommit $SourceCommit
        Assert-YLFrozenCommittedFiles -Repository $repo -SourceCommit $SourceCommit -Files $identity.files
        Assert-YLFrozenReleaseUnchanged -Release $release
    }
    $summary.status = 'Passed'
} catch {
    $summary['failure'] = $_.Exception.Message
    $failures.Add($_)
} finally {
    try {
        if ($null -ne $hostLease) {
            foreach ($file in $createdKeys) {
                try { [IO.File]::Delete((Get-YLTestPath $file)) }
                catch {
                    $summary.status = 'Failed'
                    $summary['keyCleanupError'] = $_.Exception.Message
                    $failures.Add($_)
                }
            }
            Complete-YLPlayerRun $workspace $hostRecord $run $hostLease $summary -KeepArtifacts:$KeepArtifacts
        } else { Write-YLTestJson -Path (Join-Path $run.path 'frozen.json') -Value $summary }
    } catch {
        $failures.Add($_)
    } finally {
        try {
            if ($null -ne $replayLease) { $replayLease.Dispose() }
            if ($null -ne $hostLease) { $hostLease.Dispose() }
        }
        finally { $runLease.Dispose() }
    }
}
if ($failures.Count -eq 1) { throw $failures[0] }
if ($failures.Count -gt 1) { throw [AggregateException]::new('Frozen delivery or finalization failed.', [Exception[]]$failures.Exception) }
"Frozen $Profile $Backend $Mode $($summary.purpose): $($run.path)\frozen.json"
