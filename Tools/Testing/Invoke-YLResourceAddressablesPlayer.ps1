#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param(
    [ValidateSet('Unity2022', 'Unity6', IgnoreCase = $false)][string] $Profile = 'Unity6',
    [ValidateSet('Release', 'Development', IgnoreCase = $false)][string] $BuildFlavor = 'Release',
    [string] $Unity = $env:YL_TEST_UNITY,
    [string] $WorkspaceRoot = $env:YL_TEST_WORKSPACE,
    [switch] $Graphics,
    [switch] $ScopeCloseBenchmark,
    [switch] $EncryptedRemoteGroup,
    [switch] $StockSizeControl,
    [switch] $VerifiedSizeSnapshot,
    [ValidateSet('Stock', 'Separated', IgnoreCase = $false)][string] $HeldDownload,
    [ValidateSet('Positive','PlainBundleCopy','PlayerCopy','WrongRoute','Disabled','Performance','OutputOverlap','SourceChanged','SyncWait','GuardReview','OfficialOutput','PublishChanged','LateKeyFailure','TargetConflict','AmbiguousGroups','RemoteClosure','OrdinaryDependency','BadDependency','MultiGroup','PrePublishAbort','MissingPlayerRuntime','ChangedPlayerOutput','LocalNoHash','LocalNoHashCollision', IgnoreCase = $false)]
    [string] $EncryptionCase = 'Positive',
    [ValidateSet('Remote','Embedded','Mixed', IgnoreCase = $false)][string] $DeliveryLayout = 'Remote',
    [string] $EncryptionPackReceipt,
    [string] $ReleaseManifest,
    [string] $SourceCommit,
    [switch] $KeepArtifacts,
    [switch] $ContentUpdate,
    [ValidateSet('IL2CPP','Mono', IgnoreCase = $false)][string] $Backend = 'IL2CPP'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Resource Addressables Player acceptance requires Windows.' }
if ($ContentUpdate) {
    if ($BuildFlavor -cne 'Release' -or $ScopeCloseBenchmark -or $HeldDownload -or $StockSizeControl -or
        $VerifiedSizeSnapshot -or $EncryptionPackReceipt -or $ReleaseManifest -or $SourceCommit -or
        $PSBoundParameters.ContainsKey('EncryptionCase') -or $PSBoundParameters.ContainsKey('DeliveryLayout')) {
        throw '-ContentUpdate is a separate Win64 Release workload using the current source packages.'
    }
    if ($EncryptedRemoteGroup) {
        $arguments = @{ Profile = $Profile; Unity = $Unity; WorkspaceRoot = $WorkspaceRoot; Backend = $Backend
            ContentUpdate = $true; KeepArtifacts = $KeepArtifacts }
        & (Join-Path $PSScriptRoot 'ResourceEncryption/Invoke-FrozenDelivery.ps1') @arguments
    } else {
        if ($Backend -cne 'IL2CPP') { throw 'The plain content update cell uses IL2CPP.' }
        $arguments = @{ $Profile = $Unity; WorkspaceRoot = $WorkspaceRoot; Cell = "$Profile-Plain-$Backend"; KeepArtifacts = $KeepArtifacts }
        & (Join-Path $PSScriptRoot 'ResourceUpdates/Invoke-ContentUpdates.ps1') @arguments
    }
    return
}
if ($ScopeCloseBenchmark -and ($BuildFlavor -cne 'Release' -or -not $Graphics)) {
    throw 'Scope close measurement requires -BuildFlavor Release -Graphics.'
}
if ($EncryptedRemoteGroup -and ($BuildFlavor -cne 'Release' -or -not $Graphics -or $ScopeCloseBenchmark)) {
    throw 'The isolated encryption experiment requires -BuildFlavor Release -Graphics without -ScopeCloseBenchmark.'
}
if ($VerifiedSizeSnapshot -and -not $EncryptedRemoteGroup) { throw '-VerifiedSizeSnapshot requires -EncryptedRemoteGroup.' }
if ($HeldDownload -and (-not $EncryptedRemoteGroup -or $VerifiedSizeSnapshot)) { throw '-HeldDownload is a separate -EncryptedRemoteGroup workload.' }
if ($StockSizeControl -and (-not $EncryptedRemoteGroup -or $HeldDownload -or $VerifiedSizeSnapshot)) { throw '-StockSizeControl is a separate -EncryptedRemoteGroup workload.' }
$fixedRelease = $PSBoundParameters.ContainsKey('ReleaseManifest') -or $PSBoundParameters.ContainsKey('SourceCommit')
if ($fixedRelease -and ([string]::IsNullOrWhiteSpace($ReleaseManifest) -or [string]::IsNullOrWhiteSpace($SourceCommit) -or $PSBoundParameters.ContainsKey('EncryptionPackReceipt'))) {
    throw '-ReleaseManifest and -SourceCommit must be supplied together, without -EncryptionPackReceipt.'
}
if (($PSBoundParameters.ContainsKey('EncryptionCase') -or $PSBoundParameters.ContainsKey('DeliveryLayout') -or $EncryptionPackReceipt -or $fixedRelease) -and (-not $EncryptedRemoteGroup -or $HeldDownload -or $VerifiedSizeSnapshot -or $StockSizeControl)) {
    throw 'Select exactly one encrypted Player workload.'
}
if ($Backend -ceq 'Mono' -and (-not $EncryptedRemoteGroup -or $HeldDownload -or $VerifiedSizeSnapshot -or $StockSizeControl)) {
    throw '-Backend Mono requires the frozen encrypted Player workload.'
}

. (Join-Path $PSScriptRoot 'UnityHost.ps1')
. (Join-Path $PSScriptRoot 'Common/ResourceAddressablesPlayer.ps1')

$repository = Get-YLTestPath ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')))
$root = Get-YLTestPath $WorkspaceRoot
if ($root.StartsWith($repository + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The Player workspace must be outside the source repository.'
}
$Unity = Get-YLTestPath -Path $Unity -ReadOnly
if ([IO.Path]::GetFileName($Unity) -ine 'Unity.exe' -or -not [IO.File]::Exists($Unity)) {
    throw 'Provide the installed Unity.exe via -Unity or YL_TEST_UNITY.'
}
if ($EncryptedRemoteGroup -and -not ($HeldDownload -or $VerifiedSizeSnapshot -or $StockSizeControl)) {
    $artifactArguments = if ($fixedRelease) { @{ ReleaseManifest = $ReleaseManifest; SourceCommit = $SourceCommit } }
        elseif ($EncryptionPackReceipt) { @{ PackReceipt = $EncryptionPackReceipt } } else { @{} }
    & (Join-Path $PSScriptRoot 'ResourceEncryption/Invoke-FrozenDelivery.ps1') -Profile $Profile -Unity $Unity -WorkspaceRoot $root `
        -Mode $EncryptionCase -DeliveryLayout $DeliveryLayout -Backend $Backend -KeepArtifacts:$KeepArtifacts @artifactArguments
    return
}
$configurationPath = 'Tools/Testing/TestSuites.json'
$configurationFile = Get-YLTestPath (Join-Path $repository $configurationPath)
$configurationHash = (Get-FileHash -LiteralPath $configurationFile -Algorithm SHA256).Hash
$configuration = Read-YLPackageEvidenceJson $configurationFile -ExpectedSha256 $configurationHash
if ($configuration.schemaVersion -ne 1) { throw 'Unsupported suite definition schema.' }
$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($Unity)
$editor = [pscustomobject](Get-YLTestEditorProfile -Editors $configuration.editors -VersionInfo $versionInfo)
$major = if ($Profile -ceq 'Unity2022') { '2022' } else { '6000' }
if ($editor.version -cnotmatch "^$major\.\d+\.\d+f\d+$") {
    throw "The installed Editor $($editor.productVersion) does not match the standard $Profile Player profile."
}
$addressables = Get-YLUnityAddressablesVersion $editor
$packageInputs = @(Get-YLUnityPackageInputs -EditorProfile $editor)

$mainId = 'com.kano.ylframework'
$packages = @(Get-YLUpmPackages -Repository $repository -Ids @($mainId))
if ($packages.Count -ne 1 -or @($packages[0].manifest.dependencies.Keys | Where-Object { $_ -cnotmatch '^com\.unity\.' }).Count) {
    throw 'The independent Resource Player requires only the main package and Unity dependencies; do not install optional SDKs or HybridCLR.'
}
$package = $packages[0]
$projection = [ordered]@{
    'Tools/Testing/ResourceAddressablesPlayer.Acceptance.asmdef' = 'Assets/Acceptance/Runtime/ResourceAddressablesPlayer.Acceptance.asmdef'
    'Assets/Tests/YLFramework/Acceptance/ResourceAddressables/ResourceAddressablesPlayerProbe.cs' = 'Assets/Acceptance/Runtime/ResourceAddressablesPlayerProbe.cs'
    'Tools/Testing/ResourceAddressablesPlayer.Editor.asmdef' = 'Assets/Acceptance/Editor/ResourceAddressablesPlayer.Editor.asmdef'
    'Tools/Testing/ResourceAddressablesPlayerEntry.cs' = 'Assets/Acceptance/Editor/ResourceAddressablesPlayerEntry.cs'
}
$toolInputs = @($configurationPath, 'Tools/Testing/UnityHost.ps1', 'Tools/Testing/Common/Workspace.ps1',
    'Tools/Testing/Common/Process.ps1', 'Tools/Testing/Common/Results.ps1', 'Tools/Testing/Common/Packages.ps1',
    'Tools/Testing/Common/ResourceAddressablesPlayer.ps1', 'Tools/Testing/ResourceAddressablesContentServer.cs',
    'Tools/Packaging/PackageCatalog.json', [IO.Path]::GetRelativePath($repository, $PSCommandPath).Replace('\', '/')) + $packageInputs
$acceptance = 'Assets/Tests/YLFramework/Acceptance/ResourceAddressables'
$projection["$acceptance/ResourceAddressablesSizeProbe.cs"] = 'Assets/Acceptance/Runtime/ResourceAddressablesSizeProbe.cs'
$projection['Tools/Testing/P0SizePackedBuilder.cs'] = 'Assets/Acceptance/Editor/P0SizePackedBuilder.cs'
$projection["$acceptance/ResourceAddressablesHeldProbe.cs"] = 'Assets/Acceptance/Runtime/ResourceAddressablesHeldProbe.cs'
$projection["$acceptance/P0HeldBundleProvider.cs"] = 'Assets/Acceptance/Runtime/P0HeldBundleProvider.cs'
$projection["$acceptance/P0NativeInterop.cs"] = 'Assets/Acceptance/Runtime/P0NativeInterop.cs'
$toolInputs += 'Tools/Testing/Common/ResourceAddressablesSizeExperiment.ps1'
$toolInputs += 'Tools/Testing/Common/ResourceAddressablesHeldExperiment.ps1'
. (Join-Path $PSScriptRoot 'Common/ResourceAddressablesSizeExperiment.ps1')
. (Join-Path $PSScriptRoot 'Common/ResourceAddressablesHeldExperiment.ps1')
$il2cpp = Join-Path ([IO.Path]::GetDirectoryName($Unity)) 'Data/il2cpp/build/deploy/il2cpp.exe'
if (-not [IO.File]::Exists($il2cpp)) { throw 'The selected Editor is missing its IL2CPP toolchain.' }
$identityProfile = "ResourceAA-$($editor.productVersion)-AA$addressables-Win64-IL2CPP-Medium-$BuildFlavor-Graphics$([bool]$Graphics)-ScopeClose$([bool]$ScopeCloseBenchmark)"
if ($EncryptedRemoteGroup) { $identityProfile += $(if ($HeldDownload) { "-P0B-$HeldDownload" } elseif ($VerifiedSizeSnapshot) { '-P0A-Verified' } else { '-P0A-Stock' }) }

function Get-ResourcePlayerInputIdentity {
    $packagePaths = @(Get-YLTestTreeFiles $package.root | ForEach-Object { [IO.Path]::GetRelativePath($repository, $_).Replace('\', '/') })
    $paths = $packagePaths + @($projection.Keys) + $toolInputs
    Get-YLTestInputIdentity -Repository $repository -Paths $paths -Toolchain @($Unity, $il2cpp) -Profile $identityProfile
}

$identity = Get-ResourcePlayerInputIdentity
if ($identity.files.Where({ $_.path -ceq $configurationPath })[0].sha256 -ine $configurationHash) {
    throw 'The Editor profile changed while freezing Player inputs.'
}
$lockHash = $identity.files.Where({ $_.path -ceq $packageInputs[1] })[0].sha256
$profileLock = Read-YLPackageEvidenceJson (Join-Path $repository $packageInputs[1]) -ExpectedSha256 $lockHash
$jsonPackage = 'com.unity.nuget.newtonsoft-json'
$jsonPin = $profileLock.dependencies[$jsonPackage]
if ($jsonPin['source'] -cne 'registry' -or $jsonPin['version'] -isnot [string] -or $jsonPin['version'] -cnotmatch '^\d+\.\d+\.\d+$') {
    throw 'The exact Editor profile must pin the Registry Newtonsoft Json package.'
}
$files = [ordered]@{}
foreach ($item in $identity.files) {
    $target = if ($projection.Contains($item.path)) { $projection[$item.path] }
        elseif ($item.path.StartsWith($package.relative + '/', [StringComparison]::Ordinal)) { $item.path }
        else { continue }
    $bytes = [IO.File]::ReadAllBytes((Get-YLTestPath (Join-Path $repository $item.path)))
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -ine $item.sha256) {
        throw "A projected Player input changed while being frozen: $($item.path)"
    }
    $files[$target] = $bytes
}
# Resolve a standalone baseline, never the development project's package/SDK graph.
$dependencies = [ordered]@{
    $mainId = 'file:../' + $package.relative
    'com.unity.addressables' = $addressables
    'com.unity.ugui' = $editor.ugui
    $jsonPackage = $jsonPin['version']
}
$files['Packages/manifest.json'] = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject @{ dependencies = $dependencies }))

$workspace = Initialize-YLTestWorkspace -WorkspaceRoot $root -Repository $repository
$hostKey = 'RAP' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$hostLease = $null
$runLease = $null
$summary = $null
$failures = [Collections.Generic.List[Management.Automation.ErrorRecord]]::new()
try {
    $hostRecord = New-YLTestDirectory -Workspace $workspace -Kind Host -Key $hostKey -NewOnly
    $hostLease = Lock-YLTestDirectory -Workspace $workspace -Path $hostRecord.path
    $run = New-YLTestDirectory -Workspace $workspace -Kind Result -Key 'ResourceAAIL2CPP'
    $runLease = Lock-YLTestDirectory -Workspace $workspace -Path $run.path
    foreach ($directory in @($hostRecord.path, $run.path)) {
        if (@([IO.Directory]::EnumerateFileSystemEntries($directory) | Where-Object { [IO.Path]::GetFileName($_) -cnotin @('.yl-owner.json', '.lease') }).Count) {
            throw "The Player requires fresh owned directories; preserve existing inputs and output: $directory"
        }
    }
    $summary = [ordered]@{
        schemaVersion = 1; purpose = 'YLResource.RealPlayerExecution'; status = 'Failed'; reason = $null
        workspace = $workspace.root; host = $hostRecord.path; result = $run.path; hostId = $hostRecord.id; resultId = $run.id
        profile = $Profile; unity = $Unity; unityProductVersion = $editor.productVersion; powerShell = $PSVersionTable.PSVersion.ToString()
        backend = 'IL2CPP'; target = 'StandaloneWindows64'; stripping = 'Medium'; buildFlavor = $BuildFlavor
        graphics = [bool]$Graphics; scopeCloseBenchmark = [bool]$ScopeCloseBenchmark
        encryptedRemoteGroup = [bool]$EncryptedRemoteGroup
        verifiedSizeSnapshot = [bool]$VerifiedSizeSnapshot
        heldDownload = $HeldDownload
        inputHash = $identity.sha256; inputIdentity = Join-Path $run.path 'input-identity.json'
        buildReport = Join-Path $run.path 'build.json'; runtimeSummary = Join-Path $run.path 'player-summary.json'
        addressables = $null; build = $null; runtime = $null
    }
    Assert-YLUnityHostIdle $hostRecord
    Start-YLPlayerRetention $hostRecord $run -KeepArtifacts:$KeepArtifacts
    Write-YLTestJson -Path $summary.inputIdentity -Value $identity
    Sync-YLUnityHost -HostRecord $hostRecord -Files $files -Identity $identity -EditorVersion $editor.version

    $request = Join-Path $run.path 'request.json'
    $buildReport = Get-YLTestPath $summary.buildReport
    $unityLog = Get-YLTestPath (Join-Path $run.path 'unity.log')
    foreach ($path in @($buildReport, $unityLog, $summary.runtimeSummary)) {
        if ([IO.Path]::Exists((Get-YLTestPath $path))) { throw "Preserve existing Player output: $path" }
    }
    Write-YLTestJson -Path $request -Value ([ordered]@{
        schemaVersion = 1; purpose = 'YLResource.Addressables.PlayerBuildRequest'
        hostId = $hostRecord.id; resultId = $run.id; workspaceId = $workspace.id; inputHash = $identity.sha256
        resultRoot = $run.path; output = $buildReport; unityVersion = $editor.version; addressablesVersion = $addressables
        backend = 'IL2CPP'; stripping = 'Medium'; buildFlavor = $BuildFlavor
        graphics = [bool]$Graphics; scopeCloseBenchmark = [bool]$ScopeCloseBenchmark
        encryptedRemoteGroup = [bool]$EncryptedRemoteGroup
        verifiedSizeSnapshot = [bool]$VerifiedSizeSnapshot
        heldDownload = $HeldDownload
    })
    $requestSha256 = (Get-FileHash -LiteralPath $request -Algorithm SHA256).Hash.ToLowerInvariant()
    $start = [Diagnostics.ProcessStartInfo]::new($Unity)
    $start.WorkingDirectory = $hostRecord.path
    $start.UseShellExecute = $false
    foreach ($argument in @('-batchmode', '-nographics', '-projectPath', $hostRecord.path, '-buildTarget', 'StandaloneWindows64',
        '-executeMethod', 'YLFramework.Testing.ResourceAddressablesPlayerEntry.Execute', '-ylResourceAddressablesPlayerRequest', $request,
        '-logFile', $unityLog)) { $start.ArgumentList.Add($argument) }
    $summary.build = Invoke-YLTestProcess -StartInfo $start -Log (Join-Path $run.path 'build-process.log') -TimeoutMilliseconds 1800000
    Assert-YLTestProcessSucceeded $summary.build
    if ((Get-ResourcePlayerInputIdentity).sha256 -cne $identity.sha256) { throw 'Source or toolchain inputs drifted during the Player build.' }

    $built = Read-YLPackageEvidenceJson $buildReport
    if ($built['requestPath'] -ine $request -or $built['requestSha256'] -cne $requestSha256 -or
        $built['stripping'] -cne 'Medium' -or $built['graphics'] -isnot [bool] -or $built.graphics -ne [bool]$Graphics -or
        $built['scopeCloseBenchmark'] -isnot [bool] -or $built.scopeCloseBenchmark -ne [bool]$ScopeCloseBenchmark) {
        throw 'The Editor build report differs from the original frozen request.'
    }
    $hostLockPath = Get-YLHostManagedPath $hostRecord 'Packages/packages-lock.json'
    $hostLock = Read-YLPackageEvidenceJson $hostLockPath -ExpectedSha256 $built.packageLockSha256
    if ($hostLock.dependencies[$jsonPackage].version -cne $jsonPin.version -or $hostLock.dependencies[$jsonPackage].source -cne 'registry' -or
        @($hostLock.dependencies.Keys | Where-Object { $_ -cne $mainId -and $_ -cnotmatch '^com\.unity\.' }).Count) {
        throw 'The independent Player package resolution differs from the build or contains optional packages.'
    }
    $summary.addressables = Assert-YLUnityAddressablesVersion -ExpectedVersion $addressables -Lock $hostLock -Response ([pscustomobject]$built)
    $runs = @{
        Workspace = $workspace; HostRecord = $hostRecord; Result = $run; InputIdentity = $identity
        BuildProcess = $summary.build; BuildReport = $buildReport; UnityVersion = $editor.version; AddressablesVersion = $addressables
        BuildFlavor = $BuildFlavor
    }
    if ($HeldDownload) {
        $summary.runtime = Invoke-YLResourceAddressablesHeldExperiment @runs -Mode $HeldDownload
    } elseif ($EncryptedRemoteGroup) {
        $summary.runtime = Invoke-YLResourceAddressablesSizeExperiment @runs
    } else {
        $summary.runtime = Invoke-YLResourceAddressablesPlayerRuns @runs
    }
    if ((Get-ResourcePlayerInputIdentity).sha256 -cne $identity.sha256) { throw 'Source or toolchain inputs drifted during the Player runs.' }
    $summary.status = 'Passed'
} catch {
    if ($null -ne $summary) { $summary.reason = $_.Exception.Message }
    $failures.Add($_)
} finally {
    try {
        if ($null -ne $summary) {
            Complete-YLPlayerRun $workspace $hostRecord $run $hostLease $summary -KeepArtifacts:$KeepArtifacts
        }
    } catch {
        $failures.Add($_)
    } finally {
        try { if ($null -ne $hostLease) { $hostLease.Dispose() } }
        finally { if ($null -ne $runLease) { $runLease.Dispose() } }
    }
}
if ($failures.Count -eq 1) { throw $failures[0] }
if ($failures.Count -gt 1) { throw [AggregateException]::new('Addressables Player run and finalization failed.', [Exception[]]$failures.Exception) }
$summary | ConvertTo-Json -Depth 40
