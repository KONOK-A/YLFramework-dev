#requires -Version 7.6
#requires -PSEdition Core

function Get-UpdateOwnedPath([string] $Root, [string] $Path, [switch] $File) {
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw "Expected an absolute fixture path: $Path" }
    $absolute = Get-YLTestPath $Path
    if (-not $absolute.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase) -or
        ($File -and -not [IO.File]::Exists($absolute))) { throw "Invalid owned fixture path: $absolute" }
    return $absolute
}

function Get-UpdateInventory([string] $Root) {
    foreach ($path in @(Get-YLTestTreeFiles $Root | Sort-Object -CaseSensitive)) {
        [ordered]@{ path = [IO.Path]::GetRelativePath($Root, $path).Replace('\', '/')
            bytes = [IO.FileInfo]::new($path).Length; sha256 = Get-YLFrozenFileSha256 $path }
    }
}

function Assert-UpdateInstallation([object] $Context) {
    $rows = @(Get-UpdateInventory $Context.install)
    if ($rows.Count -ne $Context.inventory.Count) { throw 'The sealed A installation inventory changed.' }
    for ($i = 0; $i -lt $rows.Count; $i++) {
        $expected = $Context.inventory[$i]
        $actual = $rows[$i]
        if ($actual.path -cne $expected.path -or $actual.bytes -ne $expected.bytes -or $actual.sha256 -cne $expected.sha256) {
            throw "The sealed A installation changed at $($expected.path)."
        }
    }
}

function New-UpdateProcess([string] $Exe, [string] $Working, [string[]] $Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($Exe)
    $info.UseShellExecute = $false
    $info.WorkingDirectory = $Working
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    return $info
}

function Write-UpdateProgress([string] $Path, [object] $Value) {
    $temporary = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    Write-YLTestJson -Path $temporary -Value $Value
    [IO.File]::Move($temporary, $Path, $true)
}

function New-UpdateKeyMaterial([object] $Context) {
    $prefix = Join-Path $Context.host.path 'keys'
    $null = [IO.Directory]::CreateDirectory($prefix)
    $handles = [ordered]@{}
    $Context.keys = $handles
    foreach ($name in @('signingPrivateKeyPath', 'signingPublicKeyPath', 'unknownPrivateKeyPath')) {
        $handles[$name] = Join-Path $prefix ($name + '.json')
    }
    $rsa = [Security.Cryptography.RSA]::Create(3072)
    $unknown = [Security.Cryptography.RSA]::Create(3072)
    try {
        foreach ($record in @(@($rsa, $true, 'signingPrivateKeyPath'), @($rsa, $false, 'signingPublicKeyPath'), @($unknown, $true, 'unknownPrivateKeyPath'))) {
            $parameters = $record[0].ExportParameters($record[1])
            $fields = [ordered]@{}
            foreach ($name in @('Modulus', 'Exponent', 'D', 'P', 'Q', 'DP', 'DQ', 'InverseQ')) {
                $bytes = $parameters.$name
                if ($null -ne $bytes) {
                    $fields[$name] = [Convert]::ToBase64String($bytes)
                    [Array]::Clear($bytes)
                }
            }
            Write-YLTestJson -Path $handles[$record[2]] -Value $fields
        }
    } finally {
        $rsa.Dispose()
        $unknown.Dispose()
    }
    foreach ($name in @('k1Path', 'k2Path', 'wrongKeyPath')) {
        $handles[$name] = ''
        if ($Context.encrypted) {
            $handles[$name] = Join-Path $prefix ($name + '.bin')
            $key = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
            try { [IO.File]::WriteAllBytes($handles[$name], $key) }
            finally { [Array]::Clear($key) }
        }
    }
    if ($Context.encrypted -and (Get-YLFrozenFileSha256 $handles.k1Path) -ceq (Get-YLFrozenFileSha256 $handles.k2Path)) {
        throw 'The two fixture content keys must contain distinct material.'
    }
    return $handles
}

function New-UpdateHostInputs([object] $Context, [object] $Editor) {
    $main = 'com.kano.ylframework'
    $extension = 'com.kano.ylframework.resource-encryption'
    $pins = @(Get-YLUnityPackageInputs $Editor)
    $lock = Read-YLPackageEvidenceJson (Join-Path $repository $pins[1])
    $dependencies = [ordered]@{
        $main = 'file:../PackageSources/' + $main
        'com.unity.addressables' = $Context.addressables
        'com.unity.ugui' = $Editor.ugui
        'com.unity.nuget.newtonsoft-json' = $lock.dependencies['com.unity.nuget.newtonsoft-json'].version
    }
    if ($Context.encrypted) { $dependencies[$extension] = 'file:../PackageSources/' + $extension }
    $projection = [ordered]@{
        'Tools/Testing/ResourceUpdates/ContentUpdateFixture.cs' = 'Assets/F/Editor/ContentUpdateFixture.cs'
        'Tools/Testing/ResourceUpdates/ContentUpdatePlayerProbe.cs' = 'Assets/F/Runtime/ContentUpdatePlayerProbe.cs'
        'Tools/Testing/ResourceUpdates/ContentUpdateMaterial.cs' = 'Assets/F/Runtime/ContentUpdateMaterial.cs'
        'Tools/Testing/ResourceEncryption/FrozenPlayerBuildEvidence.cs' = 'Assets/F/Editor/FrozenPlayerBuildEvidence.cs'
    }
    $tools = @('Tools/Testing/ResourceUpdates/Invoke-ContentUpdates.ps1', 'Tools/Testing/ResourceUpdates/ContentUpdateBuilds.ps1',
        'Tools/Testing/ResourceUpdates/ContentUpdateScenarios.ps1',
        'Tools/Testing/ResourceEncryption/FrozenLoopback.cs', 'Tools/Testing/Common/FrozenPlayerEvidence.ps1',
        'Tools/Testing/Common/FrozenEncryptionInputs.ps1', 'Tools/Testing/Common/Process.ps1', 'Tools/Testing/Common/Workspace.ps1',
        'Tools/Testing/Common/Results.ps1', 'Tools/Testing/Common/IntegrationPackageContent.ps1',
        'Tools/Testing/Common/Packages.ps1', 'Tools/Testing/UnityHost.ps1', 'Tools/Testing/TestSuites.json') + $pins
    $sources = @(foreach ($id in @($main, $extension)) {
        if ($dependencies.Contains($id)) {
            foreach ($path in @(Get-YLTestTreeFiles (Join-Path $repository "PackageSources/$id"))) {
                [IO.Path]::GetRelativePath($repository, $path).Replace('\', '/')
            }
        }
    })
    $identityArguments = @{
        Repository = $repository; Paths = $sources + @($projection.Keys) + $tools; Toolchain = $Context.toolchain
        Profile = "ContentUpdate-$($Editor.productVersion)-AA$($Context.addressables)-$($Context.id)-Win64-Release"
    }
    $identity = Get-YLTestInputIdentity @identityArguments
    Write-YLTestJson -Path (Join-Path $Context.results 'input-identity.json') -Value $identity
    $Context.report['inputHash'] = $identity.sha256
    $inputs = [ordered]@{}
    foreach ($item in $identity.files) {
        $target = if ($projection.Contains($item.path)) { $projection[$item.path] }
            elseif ($item.path.StartsWith('PackageSources/', [StringComparison]::Ordinal)) { $item.path }
            else { continue }
        $bytes = [IO.File]::ReadAllBytes((Join-Path $repository $item.path))
        if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -ine $item.sha256) {
            throw "Fixture source changed while being frozen: $($item.path)"
        }
        $inputs[$target] = $bytes
    }
    $runtime = 'YLFramework.ResourceUpdates.Fixture'
    $versionDefines = @(@{ name = $extension; expression = '0.0.0'; define = 'YL_CONTENT_UPDATE_ENCRYPTION' })
    $runtimeReferences = @('YLFramework.Runtime', 'Unity.Addressables', 'Unity.ResourceManager')
    $editorReferences = @($runtime, 'YLFramework.Runtime', 'YLFramework.Editor', 'Unity.Addressables', 'Unity.Addressables.Editor', 'Unity.ResourceManager')
    if ($Context.encrypted) {
        $runtimeReferences += 'YLFramework.Resource.Encryption'
        $editorReferences += @('YLFramework.Resource.Encryption', 'YLFramework.Resource.Encryption.Editor')
    }
    $inputs['Packages/manifest.json'] = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject @{ dependencies = $dependencies }))
    $inputs['Assets/F/Runtime/Fixture.asmdef'] = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -Depth 5 -InputObject @{
        name = $runtime; references = $runtimeReferences; versionDefines = $versionDefines
    }))
    $inputs['Assets/F/Editor/Fixture.Editor.asmdef'] = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -Depth 5 -InputObject @{
        name = $runtime + '.Editor'; references = $editorReferences; versionDefines = $versionDefines
        includePlatforms = @('Editor'); overrideReferences = $true; precompiledReferences = @('Newtonsoft.Json.dll')
    }))
    Sync-YLUnityHost -HostRecord $Context.host -Files $inputs -Identity $identity -EditorVersion $Editor.version
    return $dependencies
}

function Invoke-UpdateEditorBuild([object] $Context, [ValidateSet('A', 'B', 'C')][string] $Phase) {
    Assert-YLUnityHostIdle $Context.host
    $output = Join-Path $Context.host.path $Phase
    $null = [IO.Directory]::CreateDirectory($output)
    $requestPath = Join-Path $Context.results "$Phase-request.json"
    $replyPath = Join-Path $Context.results "$Phase-build.json"
    $request = [ordered]@{
        phase = $Phase; resultPath = $replyPath; artifactRoot = $output; installRoot = $Context.install
        baselineReceiptPath = $(if ($Phase -cne 'A') { Join-Path $Context.results 'A-build.json' } else { '' })
        baseUrl = $Context.server.Prefix; encrypted = $Context.encrypted; backend = $Context.backend
        addressables = $Context.addressables; oldAddress = $Context.oldAddress; newAddress = $Context.newAddress
        oldAssetText = $Context.materialA.asset; oldDependencyText = $Context.materialA.dependency
        ordinaryDependencyText = $Context.materialA.ordinary
        signingPrivateKeyPath = $Context.keys.signingPrivateKeyPath; signingPublicKeyPath = $Context.keys.signingPublicKeyPath
        unknownPrivateKeyPath = $Context.keys.unknownPrivateKeyPath
        k1Path = $(if ($Phase -ceq 'A') { $Context.keys.k1Path } else { '' })
        k2Path = $(if ($Phase -cne 'A') { $Context.keys.k2Path } else { '' })
        inputHash = $Context.report.inputHash
    }
    if ($Phase -cne 'A') {
        $replacement = if ($Phase -ceq 'B') { $Context.materialReplacement } else { $Context.materialReplacementC }
        $new = if ($Phase -ceq 'B') { $Context.materialNew } else { $Context.materialNewC }
        $optional = if ($Phase -ceq 'B') { $Context.materialOptional } else { $Context.materialOptionalC }
        $request['replacementAssetText'] = $replacement.asset
        $request['replacementDependencyText'] = $replacement.dependency
        $request['newAssetText'] = $new.asset
        $request['newDependencyText'] = $new.dependency
        $request['optionalAddress'] = $Context.optionalAddress
        $request['optionalAssetText'] = $optional.asset
        $request['optionalDependencyText'] = $optional.dependency
    }
    Write-YLTestJson -Path $requestPath -Value $request
    $requestHash = Get-YLFrozenFileSha256 $requestPath
    $manifestHash = Get-YLFrozenFileSha256 (Join-Path $Context.host.path 'Packages/manifest.json')
    $start = New-UpdateProcess $Context.unity $Context.host.path @('-batchmode', '-nographics', '-projectPath', $Context.host.path,
        '-buildTarget', 'StandaloneWindows64', '-executeMethod', 'YLFramework.Testing.ContentUpdateFixture.Build',
        '-ylContentUpdateRequest', $requestPath, '-logFile', (Join-Path $Context.results "$Phase-editor.log"))
    $process = Invoke-YLTestProcess -StartInfo $start -Log (Join-Path $Context.results "$Phase-process.log") -TimeoutMilliseconds 5400000
    $Context.report["build$($Phase)Process"] = $process
    if ([IO.File]::Exists($replyPath)) { $Context.report["build$Phase"] = Read-YLPackageEvidenceJson $replyPath }
    $build = $Context.report["build$Phase"]
    try { Assert-YLTestProcessSucceeded $process } catch {
        $detail = if ($null -ne $build -and $build['error']) { ($build['error'] -split '\r?\n', 2)[0] } else { $process.status }
        throw "Phase $Phase Editor build failed: $detail"
    }
    if ($null -eq $build -or $build.status -cne 'Built' -or $build.pid -ne $process.pid -or $build.phase -cne $Phase -or
        $build.unity -cne $Context.editorVersion -or $build.addressables -cne $Context.addressables -or $build.backend -cne $Context.backend -or
        $build.inputHash -cne $Context.report.inputHash -or $build.manifestSha256 -cne $manifestHash -or
        $build.requestSha256 -cne $requestHash -or (Get-YLFrozenFileSha256 $requestPath) -cne $requestHash -or
        $build.playerBuildCalls -ne $(if ($Phase -ceq 'A') { 1 } else { 0 }) -or $build.contentBuildCalls -ne 1) {
        throw "Wrong phase $Phase Editor build receipt."
    }
    $Context.report["build$($Phase)ReceiptSha256"] = Get-YLFrozenFileSha256 $replyPath
    return $build
}

function Assert-UpdateBaseline([object] $Context, [Collections.IDictionary] $Build) {
    if ($Build.oldAddress -cne $Context.oldAddress -or $Build.oldGuid -cnotmatch '^[0-9a-f]{32}$' -or
        $Build.oldDependencyGuid -cnotmatch '^[0-9a-f]{32}$' -or $Build.oldGuid -ceq $Build.oldDependencyGuid) {
        throw 'The A address or cross-Bundle source asset identities are missing.'
    }
    $Context.exe = Get-UpdateOwnedPath $Context.install $Build.playerExe -File
    $binaryArguments = @{
        Player = $Context.exe; Build = $Build; Backend = $Context.backend; ProbeAssembly = 'YLFramework.ResourceUpdates.Fixture'
    }
    if ($Context.encrypted) {
        $nativePath = Join-Path $Context.host.path 'PackageSources/com.kano.ylframework.resource-encryption/Runtime/Plugins/Win64/ylresource_tink.dll'
        $binaryArguments['ExtensionAssembly'] = 'YLFramework.Resource.Encryption'
        $binaryArguments['NativePluginSha256'] = Get-YLFrozenFileSha256 $nativePath
    }
    Assert-YLFrozenPlayerBinaries @binaryArguments
    foreach ($name in @('buildReportPath', 'buildLayoutPath', 'contentStatePath', 'packageLockPath')) {
        $path = Get-UpdateOwnedPath $Context.host.path $Build[$name] -File
        if ([IO.FileInfo]::new($path).Length -eq 0) { throw "Empty A $name." }
        $Context.report['baselineEvidence'][$name] = [ordered]@{ path = $path; sha256 = Get-YLFrozenFileSha256 $path }
    }
    $resolved = Read-YLPackageEvidenceJson $Build.packageLockPath
    $main = 'com.kano.ylframework'
    $encrypted = 'com.kano.ylframework.resource-encryption'
    $registered = @($Build.registeredPackages | Where-Object { $_.name -cmatch '^com\.kano\.' })
    $expected = @(if ($Context.encrypted) { $main; $encrypted } else { $main })
    if ($registered.Count -ne $expected.Count -or @($registered | Where-Object { $_.name -cnotin $expected -or $_.source -cne 'Local' }).Count -or
        @($resolved.dependencies.Keys | Where-Object { $_ -cmatch '^com\.kano\.' -and $_ -cnotin $expected }).Count -or
        $resolved.dependencies['com.unity.addressables'].version -cne $Context.addressables -or
        $resolved.dependencies['com.unity.addressables'].source -cne 'registry' -or
        $Context.report.baselineEvidence.contentStatePath.sha256 -cne $Build.contentStateSha256) {
        throw 'The actual package closure or frozen official content state differs from A.'
    }
    $Context.inventory = @(Get-UpdateInventory $Context.install)
    if (-not $Context.encrypted -and @($Context.inventory | Where-Object { $_.path -match 'ylresource_tink|YLFramework\.Resource\.Encryption' }).Count) {
        throw 'A plain Player contains the encryption extension.'
    }
    Write-YLTestJson -Path (Join-Path $Context.results 'A-installation.json') -Value $Context.inventory
    $Context.report['installationSha256'] = Get-YLFrozenFileSha256 (Join-Path $Context.results 'A-installation.json')
    $Context.report['baselineDigest'] = $Build.baselineDigest
    $public = New-UpdateVerificationKey $Context
    try {
        $path = Get-UpdateOwnedPath $Context.host.path $Build.baselineAnnouncementPath -File
        $announcement = Read-UpdateAnnouncement $path $public
        if (-not $announcement.signedByFixture -or $announcement.manifest.sequence -ne 10 -or $announcement.digest -cne $Build.baselineDigest) {
            throw 'The independently verified A signature or content identity differs.'
        }
        $installed = Join-Path $Context.install ([IO.Path]::GetFileNameWithoutExtension($Context.exe) + '_Data/StreamingAssets/aa')
        $envelope = Read-YLPackageEvidenceJson $path
        foreach ($signedFile in @(@('yl-content-manifest.json', 'payload'), @('yl-content-manifest.sig', 'signature'))) {
            $expectedBytes = [Convert]::FromBase64String($envelope[$signedFile[1]])
            $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($expectedBytes)).ToLowerInvariant()
            if ((Get-YLFrozenFileSha256 (Join-Path $installed $signedFile[0])) -cne $hash) {
                throw "The installed signature or exact payload differs from the sealed A announcement: $($signedFile[0])."
            }
        }
        if ((Get-YLFrozenFileSha256 (Join-Path $installed 'settings.json')) -cne $announcement.manifest.settingsSha256) {
            throw 'The actual A startup settings do not match the signed identity.'
        }
        foreach ($artifact in $announcement.manifest.artifacts) {
            if (-not $artifact.source.StartsWith('install:', [StringComparison]::Ordinal)) { continue }
            $file = Get-UpdateOwnedPath $installed (Join-Path $installed $artifact.source.Substring('install:'.Length)) -File
            if ([IO.FileInfo]::new($file).Length -ne $artifact.length -or (Get-YLFrozenFileSha256 $file) -cne $artifact.sha256) {
                throw "The real A installation does not match signed content: $($artifact.route)."
            }
        }
        $artifactRoot = Join-Path $Context.host.path 'A'
        $Context.versions = [ordered]@{ artifacts = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal) }
        if ($Context.encrypted) { Register-UpdateFileRoutes $Context $artifactRoot $Build.files }
        Assert-UpdateManifestArtifacts $Context $announcement $announcement
        $startup = @(Get-UpdateArtifactClosure $announcement.manifest (@($announcement.manifest.catalogRoute, $announcement.manifest.descriptionRoute) + $announcement.manifest.startupRoutes))
        if (@($startup | Where-Object { -not (Test-UpdateInstalledArtifact $_) }).Count -ne 0) {
            throw 'A requires installed offline startup artifacts.'
        }
        if ($Context.encrypted) {
            $Context.report['retainedOrdinary'] = Assert-UpdateRetainedOrdinary $Context $Build $announcement $startup
        } else {
            $stripped = $Build['strippedBundleRoute']
            if ($Context.editorVersion.StartsWith('6000.', [StringComparison]::Ordinal) -and
                (-not $stripped -or -not @($announcement.manifest.artifacts | Where-Object { $_.route -ceq $stripped -and $_.kind -ceq 'Bundle' }).Count)) {
                throw 'The Unity 6 ordinary cell did not prove its official stripped Bundle is signed.'
            }
        }
        $Context.report['baselineAnnouncement'] = $announcement
    } finally { $public.Dispose() }
}
