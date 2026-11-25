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
    [ValidateSet('IL2CPP','Mono', IgnoreCase = $false)][string] $Backend = 'IL2CPP')
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($ContentUpdate) {
    if ($PackReceipt -or $ReleaseManifest -or $SourceCommit -or $PSBoundParameters.ContainsKey('Mode') -or
        $PSBoundParameters.ContainsKey('DeliveryLayout')) {
        throw '-ContentUpdate is a separate frozen Player workload using the current source packages.'
    }
    $arguments = @{ $Profile = $Unity; WorkspaceRoot = $WorkspaceRoot; Cell = "$Profile-Encrypted-$Backend" }
    & (Join-Path $PSScriptRoot '../ResourceUpdates/Invoke-ContentUpdates.ps1') @arguments
    return
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
$runLease = Lock-YLTestDirectory -Workspace $workspace -Path $run.path
$summary = [ordered]@{ purpose = $(if ($fixedRelease) { 'YLResource.FrozenDelivery.LocalTarball' } else { 'YLResource.FrozenDelivery.LocalFixture' });
    status = 'Failed'; profile = $Profile; mode = $Mode; backend = $Backend; deliveryLayout = $DeliveryLayout; metricsVersion = 3;
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
    Invoke-YLTestProcess -StartInfo $info -Log (Join-Path $run.path ($log + '-process.log')) -TimeoutMilliseconds $timeout
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

try {
    $hostLease = Lock-YLTestDirectory -Workspace $workspace -Path $hostRecord.path
    Assert-YLUnityHostIdle $hostRecord
    Start-YLPlayerRetention $hostRecord $run -FormalCandidate:$fixedRelease -KeepArtifacts:$KeepArtifacts
    $projection = [ordered]@{
        'FrozenDeliveryEntry.cs' = 'Assets/Fixture/Editor/FrozenDeliveryEntry.cs'
        'FrozenPlayerBuildEvidence.cs' = 'Assets/Fixture/Editor/FrozenPlayerBuildEvidence.cs'
        'FrozenDeliveryProbe.cs' = 'Assets/Fixture/Runtime/FrozenDeliveryProbe.cs'
        'FrozenDeliveryPerformance.cs' = 'Assets/Fixture/Runtime/FrozenDeliveryPerformance.cs'
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
    $identity = Get-YLTestInputIdentity -Repository $repo -Paths $files -Toolchain $binaries -Profile "Frozen-$Profile-$Backend-$Mode-$DeliveryLayout-$packageSource"
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
            catalogName = $catalogName; deliveryLayout = $DeliveryLayout }
        $expectedRequestHash = Get-YLFrozenFileSha256 $request
        $execution = Execute-Process $Unity $hostRecord.path @('-batchmode','-nographics','-projectPath',$hostRecord.path,
            '-buildTarget','StandaloneWindows64','-executeMethod','YLFramework.Testing.FrozenDeliveryEntry.Build',
            '-ylFrozenDelivery',$request,'-logFile',(Join-Path $run.path 'editor.log')) 'editor' 1800000 $cacheRoot
        if ([IO.File]::Exists($reply)) { $summary.build = Read-YLPackageEvidenceJson $reply }
        try { Assert-YLTestProcessSucceeded $execution } catch {
            $message = if ($summary.build) { ($summary.build.error -split '\r?\n', 2)[0] } else { 'No Editor report' }
            throw "Editor build failed: $message. See $($run.path)."
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
            if ($Mode -ceq 'Performance') {
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
                Performance { Get-YLFrozenPerformanceScenarios }
                Positive {
                    if ($DeliveryLayout -cne 'Remote') { @('layout-online','layout-offline','layout-bad-embedded') }
                    else { @('cold','online-warm','head-injection','offline-warm','no-cache-offline','shadow','missing-key','wrong-key','external-owner',
                        'held-download','held-load','provider-shadow','key-return','post-start-guard','tamper','repair','tamper-prefetched','fault-abort','fault-recovery') }
                }
            })
            if ($scenarios.Count -eq 0) { throw 'A Player workload requires at least one actual Player round.' }
            $processed = 0
            foreach ($scenario in $scenarios) {
                if ($scenario -cin @('offline-warm','layout-offline')) {
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
                $runtime = Join-Path $run.path ($scenario + '.json')
                $plainSample = $scenario -clike 'performance-plain-*'
                $actualRoot = if ($plainSample) { $plainRoot } elseif ($Mode -ceq 'WrongRoute') { Join-Path $hostRecord.path 'PWrong' } else { $playerRoot }
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
                if ($Mode -ceq 'Performance') {
                    # Prove the TCP listener is up without warming either content route.
                    $endpoint = [uri]$server.Prefix
                    $connection = [Net.Sockets.TcpClient]::new($endpoint.Host, $endpoint.Port)
                    $connection.Dispose()
                }
                $prior = @($server.CaptureRoundAsync().GetAwaiter().GetResult())
                if ($prior.Count -ne $processed) { throw 'A loopback request arrived outside a Player round.' }
                Assert-YLFrozenPlayerBinaries -Player $actualExe -Build $built -Backend $Backend -ExtensionAssembly 'YLFramework.Resource.Encryption' `
                    -NativePluginSha256 $summary.pluginSha256
                $arguments = @('-batchmode','-logFile',(Join-Path $run.path ($scenario + '-game.log')),
                    '-ylFrozenScenario',$scenario,'-ylFrozenResult',$runtime)
                if ($plainSample) {
                    $plainDownload = if ($scenario -ceq 'performance-plain-cold') { $plainColdDownload } else { 0L }
                    $arguments += '-ylFrozenLayout', $DeliveryLayout, '-ylFrozenPlainDownloadBytes', $plainDownload.ToString([Globalization.CultureInfo]::InvariantCulture)
                } else {
                    $arguments += '-ylFrozenKey', $keyFile, '-ylFrozenCache', (Join-Path $hostRecord.path "cache-$scenario")
                    if ($scenario -cne 'layout-offline') { $arguments += '-ylFrozenPeerKey', $peerKeyFile }
                }
                $installProof = $null
                if ($scenario -ceq 'layout-offline') {
                    $protectedInstall = Invoke-YLFrozenReadOnlyInstallation -OwnedHostRoot $hostRecord.path -InstallRoot $actualRoot -RunPlayer {
                        Execute-Process $actualExe $actualRoot $arguments $scenario 150000
                    }
                    $process = $protectedInstall.process
                    $installProof = $protectedInstall.proof
                } else { $process = Execute-Process $actualExe $actualRoot $arguments $scenario 150000 }
                $after = @($server.CaptureRoundAsync().GetAwaiter().GetResult())
                $observed = if ([IO.File]::Exists($runtime)) { Read-YLPackageEvidenceJson $runtime } else { $null }
                $quit = if ([IO.File]::Exists($runtime + '.quit')) { Read-YLPackageEvidenceJson ($runtime + '.quit') } else { $null }
                $requests = @($after | Select-Object -Skip $processed)
                $processed = $after.Count
                $list.Add(@{ scenario = $scenario; process = $process; observed = $observed; quit = $quit; requests = $requests; readOnlyInstall = $installProof })
                $summary.runs = $list.ToArray(); $summary.http = $after
                Assert-YLFrozenPlayerBackend -Row $observed -Expected $Backend
                if ($observed.pid -ne $process.pid -or $observed.token -cne $token -or $observed.scenario -cne $scenario -or
                    $observed.unity -cne $editor.version -or $observed.inputHash -cne $identity.sha256 -or
                    $observed.catalogSha256 -cne $expectedCatalog -or [string]$observed.descriptionSha256 -cne [string]$expectedDescription) {
                    throw "Player evidence does not bind to its build inputs and shipped Catalog: $scenario. $($observed.error)"
                }
                if ($scenario -ceq 'fault-abort') {
                    $parts = @(Get-YLTestTreeFiles (Join-Path $playerRoot 'Cache-abort') | Where-Object { $_ -clike '*.part' })
                    if ($process.status -cne 'NonZeroExit' -or $process.exitCode -ne 73 -or -not $process.observedExit -or $process.activeProcesses -ne 0 -or
                        -not $process.streamsDrained -or $process.forcedCleanup -or $process.timedOut -or $process.cancelled -or $null -ne $quit -or
                        $observed.status -cne 'BeforeUnexpectedExit' -or
                        -not $observed.sawOwnedPart -or $parts.Count -ne 1 -or $requests.Count -ne 1 -or $requests[0].method -cne 'GET' -or
                        $requests[0].status -ne 200 -or $requests[0].bytes -le 0 -or $requests[0].bytes -ge $built.protectedBytes -or $requests[0].completed) {
                        throw 'Unexpected-exit control did not leave exactly one incomplete owned request and no ordinary quit.'
                    }
                    continue
                }
                try { Assert-YLTestProcessSucceeded $process } catch { throw "Player process failed: $scenario. $($observed.error)" }
                Assert-YLFrozenPlayerBackend -Row $quit -Expected $Backend
                if ($observed.status -cne 'Passed' -or $quit.status -cne 'Passed' -or $quit.pid -ne $process.pid -or -not $quit.quit -or $quit.token -cne $token -or
                    $quit.scenario -cne $scenario -or $quit.unity -cne $editor.version -or $quit.inputHash -cne $identity.sha256 -or
                    $quit.catalogSha256 -cne $expectedCatalog -or [string]$quit.descriptionSha256 -cne [string]$expectedDescription) {
                    throw "Missing Player/quit evidence for $scenario."
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
            if ($Mode -ceq 'Performance') {
                $summary['performance'] = Get-YLFrozenPerformanceSummary -Runs $list.ToArray() -Build $built -DeliveryLayout $DeliveryLayout
                if (-not $summary.performance.passed) { throw ('Performance budget failed: ' + ($summary.performance.budgetFailures -join '; ')) }
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
        try { if ($null -ne $hostLease) { $hostLease.Dispose() } }
        finally { $runLease.Dispose() }
    }
}
if ($failures.Count -eq 1) { throw $failures[0] }
if ($failures.Count -gt 1) { throw [AggregateException]::new('Frozen delivery or finalization failed.', [Exception[]]$failures.Exception) }
"Frozen $Profile $Backend $Mode $($summary.purpose): $($run.path)\frozen.json"
