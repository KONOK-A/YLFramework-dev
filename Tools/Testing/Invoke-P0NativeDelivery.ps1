#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Unity6','Unity2022', IgnoreCase = $false)][string]$Profile,
    [Parameter(Mandatory)][string]$Unity, [Parameter(Mandatory)][string]$WorkspaceRoot, [string]$PackReceipt,
    [string]$ReleaseManifest, [string]$SourceCommit,
    [ValidateSet('IL2CPP','Mono', IgnoreCase = $false)][string] $Backend = 'IL2CPP')
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'UnityHost.ps1')
. (Join-Path $PSScriptRoot 'Common/ResourceAddressablesPlayer.ps1')
. (Join-Path $PSScriptRoot 'Common/ResourceAddressablesHeldExperiment.ps1')
. (Join-Path $PSScriptRoot 'Common/FrozenPlayerEvidence.ps1')
. (Join-Path $PSScriptRoot 'Common/FrozenEncryptionInputs.ps1')

function Invoke-NativeFixtureProcess([string]$Exe, [string]$Directory, [string[]]$Arguments, [string]$Log, [int]$Timeout, [string]$CacheRoot, [switch]$Cold) {
    $start = [Diagnostics.ProcessStartInfo]::new($Exe)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $Directory
    foreach ($item in $Arguments) { $start.ArgumentList.Add($item) }
    if ($CacheRoot) { Set-YLFrozenUpmEnvironment -StartInfo $start -CacheRoot $CacheRoot -Cold:$Cold }
    Invoke-YLTestProcess -StartInfo $start -Log $Log -TimeoutMilliseconds $Timeout
}

$source = Get-YLTestPath ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')))
$fixedRelease = $PSBoundParameters.ContainsKey('ReleaseManifest') -or $PSBoundParameters.ContainsKey('SourceCommit')
$release = $null
$frozen = $null
if ($fixedRelease) {
    if ([string]::IsNullOrWhiteSpace($ReleaseManifest) -or [string]::IsNullOrWhiteSpace($SourceCommit) -or $PSBoundParameters.ContainsKey('PackReceipt')) {
        throw '-ReleaseManifest and -SourceCommit must be supplied together, without -PackReceipt.'
    }
    $release = Get-YLFrozenEncryptionRelease -Path $ReleaseManifest -Repository $source -SourceCommit $SourceCommit
    $packages = @(Get-YLFrozenEncryptionClosure -Release $release)
    $finalArchive = $true
    $packHash = $release.releaseManifestSha256
    $packSourcePath = $release.path
} else {
    if ([string]::IsNullOrWhiteSpace($PackReceipt)) { throw 'A diagnostic -PackReceipt or fixed release manifest is required.' }
    $PackReceipt = Get-YLTestPath $PackReceipt
    $frozen = Read-YLPackageEvidenceJson $PackReceipt
    $finalArchive = $frozen.purpose -ceq 'YLResource.LocalEncryptionPack'
    $negatives = if ($finalArchive) { $frozen.negatives } else { $frozen.negative }
    if ($frozen.purpose -cnotin @('YLResource.P0C.NativePack','YLResource.LocalEncryptionPack') -or $frozen.status -cne 'Packed' -or $frozen.formalCandidate -ne $false -or
        $negatives.Count -ne 5) { throw 'The validated local offline fixture receipt is required.' }
    $packages = @($frozen.artifacts['com.kano.ylframework'], $frozen.artifacts['com.kano.ylframework.resource-encryption'])
    if ($packages[0].id -cne 'com.kano.ylframework' -or $packages[1].id -cne 'com.kano.ylframework.resource-encryption') { throw 'Unexpected diagnostic archive identities.' }
    $packHash = Get-YLFrozenFileSha256 $PackReceipt
    $packSourcePath = $PackReceipt
}
$Unity = Get-YLTestPath $Unity -ReadOnly
$module = @(Get-YLFrozenWin64Module -Unity $Unity -Backend $Backend)
$config = Read-YLPackageEvidenceJson (Join-Path $PSScriptRoot 'TestSuites.json')
$editor = [pscustomobject](Get-YLTestEditorProfile -Editors $config.editors -VersionInfo ([Diagnostics.FileVersionInfo]::GetVersionInfo($Unity)))
$aa = Get-YLUnityAddressablesVersion $editor
if (($Profile -ceq 'Unity6' -and ($editor.version -cne '6000.3.27f1' -or $aa -cne '2.10.3')) -or
    ($Profile -ceq 'Unity2022' -and ($editor.version -cne '2022.3.62f3' -or $aa -cne '1.25.0'))) { throw 'P0-C requires the planned Unity/Addressables versions.' }
$catalog = if ($Profile -ceq 'Unity6') { 'catalog.bin' } else { 'catalog.json' }
$mainId = 'com.kano.ylframework'; $extId = 'com.kano.ylframework.resource-encryption'
if ($Backend -ceq 'Mono' -and -not $finalArchive) { throw 'Mono package testing requires the final encryption extension archive, not the historical P0 plugin.' }
$runKind = if ($finalArchive) { 'NativeDeliveryFinal' } else { 'NativeDeliveryP0' }
$workspace = Initialize-YLTestWorkspace -WorkspaceRoot $WorkspaceRoot -Repository $source
$run = New-YLTestDirectory -Workspace $workspace -Kind Result -Key $runKind
$hostRecord = New-YLTestDirectory -Workspace $workspace -Kind Host -Key ('RNC' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$runLease = Lock-YLTestDirectory -Workspace $workspace -Path $run.path
$hostLease = $null
$summary = [ordered]@{ purpose = $(if ($fixedRelease) { 'YLResource.NativeDelivery.LocalTarball' } else { 'YLResource.NativeDelivery.LocalFixture' });
    status = 'Incomplete'; packageSource = 'LocalTarball'; formalCandidate = $fixedRelease;
    profile = $Profile; backend = $Backend; unityVersion = $editor.version; addressablesVersion = $aa; host = $hostRecord.path; result = $run.path;
    finalArchive = $finalArchive; artifacts = [ordered]@{}; evidence = [ordered]@{} }
if ($fixedRelease) {
    $summary['sourceCommit'] = $release.sourceCommit
    $summary['releaseManifestSha256'] = $packHash
    $summary['producer'] = $release.producer
} else { $summary['packReceipt'] = $PackReceipt; $summary['packReceiptSha256'] = $packHash }
$pc = Join-Path $hostRecord.path 'P0C'; $keyPath = Join-Path $pc 'fixture.key'
try {
    $hostLease = Lock-YLTestDirectory -Workspace $workspace -Path $hostRecord.path
    Assert-YLUnityHostIdle $hostRecord
    Write-YLTestJson -Path (Join-Path $hostRecord.path '.pinned') -Value @{ purpose = 'Preserve install/remove package evidence'; runId = $run.id }
    Write-YLTestJson -Path (Join-Path $run.path '.pinned') -Value @{ purpose = 'Preserve install/remove package evidence'; hostId = $hostRecord.id }
    $null = [IO.Directory]::CreateDirectory($pc)
    $projection = [ordered]@{
        'Tools/Testing/P0NativePackageEntry.cs' = 'Assets/Editor/P0NativePackageEntry.cs'
        'Tools/Testing/ResourceEncryption/FrozenPlayerBuildEvidence.cs' = 'Assets/Editor/FrozenPlayerBuildEvidence.cs'
        'Tools/Testing/P0NativePackageProbe.cs' = 'Assets/P0NativePackageProbe.cs'
    }
    $profilePins = @(Get-YLUnityPackageInputs -EditorProfile $editor)
    $helpers = @('Tools/Testing/Invoke-P0NativeDelivery.ps1', 'Tools/Testing/Common/FrozenPlayerEvidence.ps1', 'Tools/Testing/Common/FrozenEncryptionInputs.ps1',
        'Tools/Testing/Common/IntegrationPackageContent.ps1', 'Tools/Testing/Common/Workspace.ps1', 'Tools/Testing/Common/Process.ps1', 'Tools/Testing/Common/Results.ps1',
        'Tools/Testing/Common/Packages.ps1', 'Tools/Testing/UnityHost.ps1', 'Tools/Testing/TestSuites.json', 'Tools/Testing/PackageCatalogContract.cs',
        'Tools/Testing/Common/ResourceAddressablesPlayer.ps1', 'Tools/Testing/Common/ResourceAddressablesHeldExperiment.ps1',
        'Tools/Packaging/PackageCatalog.json', 'Tools/Packaging/Build-YLRegistryPackages.ps1')
    $identity = Get-YLTestInputIdentity -Repository $source -Paths (@($projection.Keys) + $helpers + $profilePins) `
        -Toolchain (@($Unity) + $module + $packSourcePath + @($packages.path)) -Profile "P0C-$Profile-$Backend-$packHash"
    if ($fixedRelease) { Assert-YLFrozenCommittedFiles -Repository $source -SourceCommit $SourceCommit -Files $identity.files }
    $summary['inputHash'] = $identity.sha256
    $inputs = [ordered]@{}
    foreach ($file in $identity.files) {
        if (-not $projection.Contains($file.path)) { continue }
        $bytes = [IO.File]::ReadAllBytes((Join-Path $source $file.path))
        if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -ine $file.sha256) { throw 'Native lifecycle probe source changed during projection.' }
        $inputs[$projection[$file.path]] = $bytes
    }
    $profileLock = Read-YLPackageEvidenceJson (Join-Path $source $profilePins[1])
    $jsonVersion = $profileLock.dependencies['com.unity.nuget.newtonsoft-json'].version
    $deps = [ordered]@{ 'com.unity.addressables' = $aa; 'com.unity.ugui' = $editor.ugui; 'com.unity.nuget.newtonsoft-json' = $jsonVersion }
    foreach ($item in $packages) {
        $name = "$($item.id)-$($item.version).tgz"
        $inputs['P0C/' + $name] = Get-YLFrozenTarballBytes $item
        $deps[$item.id] = 'file:../P0C/' + $name
        $summary.artifacts[$item.id] = @{ sha256 = $item.sha256; integrity = $item.integrity; payloadSha256 = $item.payloadSha256;
            path = $item.path; locator = $deps[$item.id] }
    }
    $clearLength = 0L; $ranges = @()
    if ($fixedRelease) {
        $nativeHash = $release.plugin.sha256
        $metaHash = $release.plugin.importerSha256
    } elseif ($finalArchive) {
        $nativeHash = $frozen.pluginSha256
        $metaHash = $frozen.importerSha256
    } else {
        $sourceDll = 'G:\Unity\Tests\Renc\Native\build\Release\ylresource_p0.dll'
        $nativeHash = Initialize-P0NativeAuthoring $sourceDll
        $metaHash = $frozen.plugin.metaSha256
        if ($nativeHash -cne $frozen.plugin.sha256) { throw 'The pack and authoring bridge identities differ.' }
        $clearLength = 3MB + 157
        $clearPath = Join-Path $pc 'authoring.bin'
        $cipherPath = Join-Path $pc 'cipher.bin'
        $buffer = [byte[]]::new(64KB)
        $plain = [IO.File]::Open($clearPath, 'CreateNew', 'Write', 'None')
        try {
            $random = [Random]::new(101463)
            for ($remain = $clearLength; $remain -gt 0; $remain -= $buffer.Length) { $random.NextBytes($buffer); $plain.Write($buffer, 0, [Math]::Min($remain, $buffer.Length)) }
        } finally { $plain.Dispose() }
        $ranges = @(foreach ($sample in @(@(2097060L, 104), @(1048496L, 128), @(64L, 511), @(2097101L, 231), @([long]($clearLength - 63), 63), @(0L, 400))) {
            $clear = [IO.File]::OpenRead($clearPath)
            try {
                $clear.Position = $sample[0]; $bytes = [byte[]]::new($sample[1]); $clear.ReadExactly($bytes)
                @{ at = $sample[0]; length = $sample[1]; sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() }
            } finally { $clear.Dispose() }
        })
        $key = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
        try {
            [YLFramework.Testing.NativeP0.P0NativeReadStream]::Encrypt($clearPath, $cipherPath, $key)
            $stagedKey = [IO.File]::Open($keyPath, 'CreateNew', 'Write', 'None')
            try { $stagedKey.Write($key) } finally { $stagedKey.Dispose() }
        } finally { [Array]::Clear($key, 0, $key.Length); [IO.File]::Delete($clearPath) }
        $summary['cipherSha256'] = Get-YLFrozenFileSha256 $cipherPath
    }
    $cacheRoot = Join-Path $hostRecord.path 'PackageInputs'
    $token = [Guid]::NewGuid().ToString('N')
    foreach ($stage in 'Installed', 'Removed') {
        Assert-YLUnityHostIdle $hostRecord
        $isRemoved = $stage -ceq 'Removed'
        if ($isRemoved) { $deps.Remove($extId) }
        $stagePackages = @(if ($fixedRelease) { Get-YLFrozenEncryptionClosure -Release $release -Removed:$isRemoved }
            elseif ($isRemoved) { $packages[0] } else { $packages })
        $inputs['Packages/manifest.json'] = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json @{ dependencies = $deps }))
        Sync-YLUnityHost -HostRecord $hostRecord -Files $inputs -Identity $identity -EditorVersion $editor.version
        $expectedManifestHash = Get-YLFrozenFileSha256 (Join-Path $hostRecord.path 'Packages/manifest.json')
        foreach ($item in $stagePackages) {
            if ((Get-YLFrozenFileSha256 (Join-Path $hostRecord.path $deps[$item.id].Substring('file:../'.Length))) -cne $item.sha256) {
                throw 'The owned archive copy changed before installation.'
            }
        }
        $leaf = if ($stage -ceq 'Installed') { 'I' } else { 'R' }
        $playerRoot = Get-YLTestPath (Join-Path $pc $leaf)
        $buildPath = Join-Path $run.path "$stage-build.json"
        $resultPath = Join-Path $run.path "$stage-player.json"
        $quitPath = Join-Path $run.path "$stage-quit.json"
        $reqPath = Join-Path $pc "$stage-request.json"
        $row = [ordered]@{ build = $null; buildReport = $null; player = $null; playerReport = $null; quit = $null }
        $summary.evidence[$stage] = $row
        Write-YLTestJson -Path $reqPath -Value @{
            unityVersion = $editor.version; addressablesVersion = $aa; packageVersion = $packages[0].version;
            metaSha256 = $metaHash; output = $buildPath;
            job = @{ token = $token; stage = $stage; playerRoot = $playerRoot; ready = $resultPath; exit = $quitPath;
                catalogName = $catalog; plainLength = $clearLength; ranges = $ranges; pluginSha256 = $nativeHash; finalArchive = $finalArchive;
                backend = $Backend; inputHash = $identity.sha256 }
        }
        $expectedRequestHash = Get-YLFrozenFileSha256 $reqPath
        $row.build = Invoke-NativeFixtureProcess $Unity $hostRecord.path @('-batchmode', '-nographics', '-projectPath', $hostRecord.path,
            '-buildTarget', 'StandaloneWindows64', '-executeMethod', 'YLFramework.Testing.P0NativePackageEntry.Build',
            '-ylP0CRequest', $reqPath, '-logFile', (Join-Path $run.path "$stage-editor.log")) (Join-Path $run.path "$stage-build-process.log") 1800000 $cacheRoot -Cold:(-not $isRemoved)
        if ([IO.File]::Exists($buildPath)) { $row.buildReport = Read-YLPackageEvidenceJson $buildPath }
        Assert-YLTestProcessSucceeded $row.build
        $built = $row.buildReport
        $player = Get-YLTestPath (Join-Path $playerRoot 'S.exe')
        if ($null -eq $built -or $built.status -cne 'Built' -or $built.pid -ne $row.build.pid -or $built.requestSha256 -cne $expectedRequestHash -or
            (Get-YLFrozenFileSha256 $reqPath) -cne $expectedRequestHash -or $built.inputHash -cne $identity.sha256 -or $built.manifestSha256 -cne $expectedManifestHash -or
            $built.token -cne $token -or $built.stage -cne $stage -or $built.unityVersion -cne $editor.version -or
            $built.player -ine $player -or $built.mainSource -cne 'LocalTarball' -or
            $built.addressablesVersion -cne $aa -or $built.addressablesSource -cne 'Registry') { throw 'P0-C build or package identity differs.' }
        $extensionAssembly = if ($stage -ceq 'Installed') { if ($finalArchive) { 'YLFramework.Resource.Encryption' } else { 'YLFramework.ResourceEncryption.P0' } }
        $row['installedPackages'] = @(Assert-YLFrozenInstalledInputs -Build $built -Dependencies $deps -ProfileLock $profileLock -Packages $stagePackages `
            -HostRoot $hostRecord.path -CacheRoot $cacheRoot)
        $nativeName = if ($finalArchive) { 'ylresource_tink.dll' } else { 'ylresource_p0.dll' }
        $emitted = @(Get-YLTestTreeFiles $playerRoot | Where-Object { [IO.Path]::GetFileName($_) -ceq $nativeName })
        if ($isRemoved -and ($built.binaryPlugins.Count -ne 0 -or $emitted.Count -ne 0)) { throw 'Uninstall retained a native plugin.' }
        if (-not $isRemoved -and ($built.extensionSource -cne 'LocalTarball' -or $built.importedPluginSha256 -cne $nativeHash -or
            $built.importerSha256 -cne $metaHash -or $built.binaryPlugins.Count -ne 1 -or $emitted.Count -ne 1 -or
            $built.binaryPlugins[0] -ine $emitted[0] -or -not (Test-YLFrozenAmd64 $emitted[0]) -or (Get-YLFrozenFileSha256 $emitted[0]) -cne $nativeHash)) {
            throw 'Missing installed-plugin source or Player binary evidence.'
        }
        if ((Get-YLFrozenFileSha256 (Join-Path $playerRoot "S_Data/StreamingAssets/aa/$catalog")) -cne $built.catalogSha256) {
            throw 'The ordinary shipped Catalog differs before Player start.'
        }
        Assert-YLFrozenPlayerBinaries -Player $player -Build $built -Backend $Backend -ExtensionAssembly $extensionAssembly -ProbeAssembly 'Assembly-CSharp'
        $row.player = Invoke-NativeFixtureProcess $player $playerRoot @('-batchmode', '-logFile', (Join-Path $run.path "$stage-game.log")) (Join-Path $run.path "$stage-player-process.log") 90000
        if ([IO.File]::Exists($resultPath)) { $row.playerReport = Read-YLPackageEvidenceJson $resultPath }
        if ([IO.File]::Exists($quitPath)) { $row.quit = Read-YLPackageEvidenceJson $quitPath }
        Assert-YLTestProcessSucceeded $row.player
        $seen = $row.playerReport
        Assert-YLFrozenPlayerBackend -Row $seen -Expected $Backend
        Assert-YLFrozenPlayerBackend -Row $row.quit -Expected $Backend
        if ($null -eq $seen -or $null -eq $row.quit -or $seen.status -cne 'Passed' -or $seen.token -cne $token -or $seen.stage -cne $stage -or
            $seen.pid -ne $row.player.pid -or $row.quit.pid -ne $row.player.pid -or $row.quit.token -cne $token -or -not $row.quit.quit -or
            $row.quit.requestedExitCode -ne 0 -or $row.quit.stage -cne $stage -or $seen.unityVersion -cne $editor.version -or $row.quit.unityVersion -cne $editor.version -or
            $seen.catalogSha256 -cne $built.catalogSha256 -or $row.quit.catalogSha256 -cne $built.catalogSha256 -or
            $seen.inputHash -cne $identity.sha256 -or $row.quit.inputHash -cne $identity.sha256 -or -not $seen.mainLoaded -or $seen.leases -ne 0 -or $seen.openBundles -ne 0 -or
            ($stage -ceq 'Removed' -and $seen.extensionLoaded) -or ($finalArchive -and -not $seen.disabledNativeInactive) -or
            ($stage -ceq 'Installed' -and ($seen.pluginSha256 -cne $nativeHash -or (-not $finalArchive -and
                (-not $seen.extensionLoaded -or $seen.samples -ne $ranges.Count -or -not $seen.exclusiveOpenAfterDispose -or -not $seen.endOfFile))))) {
            throw 'P0-C Player is missing expected authenticated random read, ordinary resource, or cleanup evidence.'
        }
    }
    if ($fixedRelease) {
        Assert-YLFrozenSourceCommit -Repository $source -SourceCommit $SourceCommit
        Assert-YLFrozenCommittedFiles -Repository $source -SourceCommit $SourceCommit -Files $identity.files
        Assert-YLFrozenReleaseUnchanged -Release $release
    } else {
        foreach ($item in $packages) {
            if ((Get-YLFrozenFileSha256 $item.path) -cne $item.sha256) { throw 'Original frozen pack changed during test.' }
        }
        if ((Get-YLFrozenFileSha256 $PackReceipt) -cne $packHash -or (-not $finalArchive -and (Get-YLFrozenFileSha256 $cipherPath) -cne $summary.cipherSha256)) {
            throw 'Frozen package or sealed file changed.'
        }
    }
    $summary.status = 'Passed'
    "P0-C $Profile $Backend LocalTarball install/remove: $($run.path)\p0-c.json"
} catch {
    $summary['failure'] = $_.Exception.Message
    throw
} finally {
    try {
        if ([IO.File]::Exists($keyPath)) { [IO.File]::Delete((Get-YLTestPath $keyPath)) }
        Write-YLTestJson -Path (Join-Path $run.path 'p0-c.json') -Value $summary
    } finally {
        if ($null -ne $hostLease) { $hostLease.Dispose() }
        $runLease.Dispose()
    }
}
