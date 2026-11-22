#requires -Version 7.6
#requires -PSEdition Core

function Invoke-YLResourceAddressablesSizeExperiment {
    param($Workspace, $HostRecord, $Result, $InputIdentity, $BuildProcess, [string] $BuildReport,
        [string] $UnityVersion, [string] $AddressablesVersion, [string] $BuildFlavor)

    Assert-YLUnityHostIdle $HostRecord
    Assert-YLTestProcessSucceeded $BuildProcess
    $build = Read-YLPackageEvidenceJson $BuildReport
    if ($build.status -cne 'Built' -or $build.buildResult -cne 'Succeeded' -or $build.buildErrors -ne 0 -or
        $build.pid -ne $BuildProcess.pid -or $build.inputHash -cne $InputIdentity.sha256 -or $build.hostId -cne $HostRecord.id -or
        $build.resultId -cne $Result.id -or $build.workspaceId -cne $Workspace.id -or $build.unityVersion -cne $UnityVersion -or
        $build.addressablesVersion -cne $AddressablesVersion -or $build.buildFlavor -cne $BuildFlavor -or
        $build.backend -cne 'IL2CPP' -or $build.stripping -cne 'Medium' -or $build.target -cne 'StandaloneWindows64' -or
        $build.encryptedRemoteGroup -ne $true -or $build.graphics -ne $true -or $build.buildToken -cnotmatch '^[0-9a-f]{32}$') {
        throw 'The P0-A build report does not identify the actual dedicated native build.'
    }
    $playerRoot = Get-YLTestPath (Join-Path $HostRecord.path ('P/' + $build.buildToken.Substring(0, 8)))
    $player = Get-YLTestPath (Join-Path $playerRoot 'P.exe')
    if ($build.playerRoot -ine $playerRoot -or $build.player -ine $player -or
        $build.remoteRoot -ine (Join-Path $HostRecord.path ('RA/' + $build.buildToken.Substring(0, 8) + '/Remote')) -or
        $build.streamingAssetsAa -ine (Join-Path $playerRoot 'P_Data/StreamingAssets/aa')) { throw 'P0-A output paths differ from the owned build.' }
    $null = Get-YLResourceAddressablesContentIdentity $build
    foreach ($pair in @(@($player, $build.playerSha256), @((Join-Path $playerRoot 'GameAssembly.dll'), $build.gameAssemblySha256),
        @((Join-Path $playerRoot 'P_Data/il2cpp_data/Metadata/global-metadata.dat'), $build.metadataSha256))) {
        if ((Get-FileHash -LiteralPath (Get-YLTestPath $pair[0])).Hash -ine $pair[1]) { throw 'P0-A Player executable identity drift.' }
    }
    $protected = @($build.files | Where-Object { $_.area -ceq 'remote' -and $_.path -ceq $build.p0ProtectedBundle })
    $ordinary = @($build.files | Where-Object { $_.area -ceq 'remote' -and $_.path -ceq $build.p0OrdinaryBundle })
    $catalogs = @($build.files | Where-Object { $_.area -ceq 'aa' -and $_.path -ceq $build.p0Catalog })
    if ($protected.Count -ne 1 -or $ordinary.Count -ne 1 -or $catalogs.Count -ne 1 -or $protected[0].path -ceq $ordinary[0].path -or
        $protected[0].path -cnotmatch '^[a-z0-9_.-]+\.bundle$' -or $ordinary[0].path -cnotmatch '^[a-z0-9_.-]+\.bundle$' -or
        $protected[0].bytes -lt 1 -or $protected[0].bytes -gt 64KB -or $build.p0PackedPlainBytes -ne $protected[0].bytes) {
        throw 'Expected small, distinct physical remote fixtures with matching official packed length.'
    }
    $protected = $protected[0]; $ordinary = $ordinary[0]; $catalog = $catalogs[0]
    $transfer = Get-YLTestPath (Join-Path $playerRoot 'P0Transfer')
    if ([IO.Path]::Exists($transfer)) { throw 'P0-A refuses an existing transfer fixture.' }
    $null = New-Item -ItemType Directory -Path $transfer -ErrorAction Stop

    # A throwaway .NET AEAD sample for byte sizing only, not the planned streaming format or a Player key.
    $plain = [byte[]]::new($protected.bytes)
    $input = [IO.File]::OpenRead((Join-Path $build.remoteRoot $protected.path))
    try { $input.ReadExactly($plain) } finally { $input.Dispose() }
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($plain)) -ine $protected.sha256) { throw 'The protected source changed before encryption.' }
    $key = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
    $nonce = [Security.Cryptography.RandomNumberGenerator]::GetBytes(12)
    $tag = [byte[]]::new(16)
    $cipher = [byte[]]::new($plain.Length)
    try {
        $aead = [Security.Cryptography.AesGcm]::new($key, 16)
        try { $aead.Encrypt($nonce, $plain, $cipher, $tag, [byte[]]::new(0)) } finally { $aead.Dispose() }
    } finally {
        [Array]::Clear($key, 0, $key.Length)
        [Array]::Clear($plain, 0, $plain.Length)
    }
    $sealed = Join-Path $transfer $protected.path
    $file = [IO.File]::Open($sealed, 'CreateNew', 'Write', 'None')
    try {
        foreach ($part in $nonce, $cipher, $tag) { $file.Write($part, 0, $part.Length) }
    } finally { $file.Dispose() }
    [IO.File]::Copy((Join-Path $build.remoteRoot $ordinary.path), (Join-Path $transfer $ordinary.path), $false)
    $description = [ordered]@{
        buildToken = $build.buildToken; catalog = $catalog.path; catalogSha256 = $catalog.sha256
        protectedPath = $protected.path; ordinaryPath = $ordinary.path; protectedPlainBytes = $protected.bytes
        cipherBytes = [IO.FileInfo]::new($sealed).Length; cipherSha256 = (Get-FileHash -LiteralPath $sealed).Hash.ToLowerInvariant()
        ordinaryBytes = $ordinary.bytes
    }
    Write-YLTestJson -Path (Join-Path $playerRoot 'p0-size.json') -Value $description
    $receipt = [ordered]@{
        purpose = 'YLResource.P0A.DownloadSize'; status = 'Incomplete'; baseline = 'c23dc67ebfb1b3557446648a0e518752ce9aa656'
        buildReport = $BuildReport; verifiedSizeSnapshot = $build.verifiedSizeSnapshot; description = $description
        report = $null; process = $null; http = @(); quit = $null
    }
    $server = $null
    try {
        if (-not ('YLFramework.Testing.ResourceAddressablesContentServer' -as [type])) {
            Add-Type -Path (Join-Path $PSScriptRoot '../ResourceAddressablesContentServer.cs')
        }
        $allowed = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
        $allowed.Add($protected.path, $description.cipherSha256)
        $allowed.Add($ordinary.path, $ordinary.sha256)
        $server = [YLFramework.Testing.ResourceAddressablesContentServer]::new($transfer, $build.buildToken, $allowed)
        $runtime = Join-Path $playerRoot 'run-1.json'
        $log = Join-Path $Result.path 'p0-a-player.log'
        $start = [Diagnostics.ProcessStartInfo]::new($player)
        $start.UseShellExecute = $false; $start.WorkingDirectory = $playerRoot
        foreach ($argument in @('-batchmode', '-ylResourceAddressablesPlayerResult', $runtime, '-ylResourceAddressablesPlayerRound', '1',
            '-ylResourceAddressablesContentBaseUrl', $server.BaseUrl, '-logFile', $log)) { $start.ArgumentList.Add($argument) }
        $receipt.process = Invoke-YLTestProcess -StartInfo $start -Log (Join-Path $Result.path 'p0-a-process.log') -TimeoutMilliseconds 180000
        if ([IO.File]::Exists($runtime)) { $receipt.report = Read-YLPackageEvidenceJson $runtime }
        if ([IO.File]::Exists($runtime + '.quit.json')) { $receipt.quit = Read-YLPackageEvidenceJson ($runtime + '.quit.json') }
        $receipt.http = @($server.Snapshot())
        $observed = $receipt.report
        if ($null -eq $observed -or $null -eq $receipt.quit -or $observed.pid -ne $receipt.process.pid -or $receipt.quit.pid -ne $receipt.process.pid -or
            $observed.buildToken -cne $build.buildToken -or $receipt.quit.buildToken -cne $build.buildToken -or $observed.inputHash -cne $InputIdentity.sha256 -or
            $observed.backend -cne 'IL2CPP' -or $observed.graphicsDeviceType -cin @('Null', '') -or $observed.buildFlavor -cne 'Release' -or
            $observed.pointerBits -ne 64 -or $observed.isEditor -ne $false -or -not $receipt.quit.onApplicationQuit -or
            $receipt.quit.requestedExitCode -ne $receipt.process.exitCode -or
            $receipt.process.status -cne $(if ($receipt.process.exitCode -eq 0) { 'Exited' } else { 'NonZeroExit' }) -or
            -not $receipt.process.observedExit -or -not $receipt.process.streamsDrained -or $receipt.process.activeProcesses -ne 0 -or
            -not $receipt.process.jobAssigned -or
            $receipt.process.timedOut -or $receipt.process.cancelled -or $receipt.process.forcedCleanup -or
            $receipt.process.cleanupError -or $receipt.process.observerError) {
            throw 'P0-A does not have an ordinary observed Player exit and matching report.'
        }
        if (-not $build.verifiedSizeSnapshot -and $receipt.process.exitCode -eq 1 -and $observed.status -ceq 'Failed' -and
            $observed.error -cmatch 'DownloadSize_UsesShippedCipherLengthAndVerifiedBytes:' -and
            $observed.sizeExperiment.catalogSha256 -ceq $catalog.sha256 -and $observed.sizeExperiment.queries.Count -eq 16 -and
            $receipt.http.Count -eq 1 -and $receipt.http[0].path -ceq $ordinary.path -and $receipt.http[0].bytes -eq $ordinary.bytes) {
            $receipt.status = 'Counterexample'
            throw 'P0-A stock behavior differs from the cipher size/cache contract; see p0-a.json.'
        }
        Assert-YLTestProcessSucceeded $receipt.process
        if ($observed.status -cne 'Passed' -or $observed.sizeExperiment.queries.Count -ne 16 -or
            $observed.sizeExperiment.catalogSha256 -cne $catalog.sha256 -or $receipt.http.Count -ne 1 -or
            $receipt.http[0].path -cne $ordinary.path -or $receipt.http[0].bytes -ne $ordinary.bytes) { throw 'Missing P0-A size or ordinary HTTP evidence.' }
        $null = Get-YLResourceAddressablesContentIdentity $build
        $receipt.status = 'Passed'
        return [pscustomobject] $receipt
    } finally {
        try { if ($null -ne $server) { $server.Dispose() } }
        finally { Write-YLTestJson -Path (Join-Path $Result.path 'p0-a.json') -Value $receipt }
    }
}
