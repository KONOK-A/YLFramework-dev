#requires -Version 7.6
#requires -PSEdition Core

function Initialize-P0NativeAuthoring {
    param([string] $NativeDll)
    $native = Get-YLTestPath $NativeDll
    $sha = '639257f91dbf691a9ff3ec2c9e8d483ab9e02ae2960f486d7b6f0019fbd7ca3d'
    if ((Get-FileHash -LiteralPath $native -Algorithm SHA256).Hash -ine $sha) { throw 'P0 native library drift.' }
    if (-not ('YLFramework.Testing.NativeP0.P0NativeReadStream' -as [type])) {
        $source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../../../Assets/Tests/YLFramework/Acceptance/ResourceAddressables/P0NativeInterop.cs'))
        $literal = $native.Replace('\', '/').Replace('"', '\"')
        Add-Type -TypeDefinition $source.Replace('Library = "ylresource_p0"', ('Library = "' + $literal + '"'))
    }
    return $sha
}

function New-P0HeldCipherFixture {
    param($Build, [string] $PlayerRoot, $ProtectedBundle, [switch] $EnableReader)
    $native = 'G:\Unity\Tests\Renc\Native\build\Release\ylresource_p0.dll'
    $sha = Initialize-P0NativeAuthoring $native
    $root = Join-Path $PlayerRoot 'P0Delivery'
    if ([IO.Path]::Exists($root)) { throw 'P0-B delivery root already exists.' }
    $null = New-Item -ItemType Directory -Path $root -ErrorAction Stop
    $plain = Get-YLTestPath (Join-Path $Build.remoteRoot $ProtectedBundle.path)
    $sealed = Join-Path $root $ProtectedBundle.path
    $key = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
    try {
        [YLFramework.Testing.NativeP0.P0NativeReadStream]::Encrypt($plain, $sealed, $key)
        if ($EnableReader) {
            $keyFile = [IO.File]::Open((Join-Path $PlayerRoot 'p0-random-input.bin'), 'CreateNew', 'Write', 'None')
            try { $keyFile.Write($key) } finally { $keyFile.Dispose() }
        }
    } finally { [Array]::Clear($key, 0, $key.Length) }
    if ($EnableReader) {
        $playerNative = Join-Path $PlayerRoot 'ylresource_p0.dll'
        [IO.File]::Copy($native, $playerNative, $false)
        if ((Get-FileHash -LiteralPath $playerNative).Hash -ine $sha) { throw 'P0-B artifact byte identity changed.' }
    }
    return @{ root = $root; file = @{ path = $ProtectedBundle.path; bytes = [IO.FileInfo]::new($sealed).Length
        sha256 = (Get-FileHash -LiteralPath $sealed).Hash.ToLowerInvariant(); nativeSha256 = $sha } }
}

function Invoke-YLResourceAddressablesHeldExperiment {
    param($Workspace, $HostRecord, $Result, $InputIdentity, $BuildProcess, [string] $BuildReport,
        [string] $UnityVersion, [string] $AddressablesVersion, [string] $BuildFlavor, [string] $Mode)
    Assert-YLUnityHostIdle $HostRecord
    Assert-YLTestProcessSucceeded $BuildProcess
    $build = Read-YLPackageEvidenceJson $BuildReport
    if ($build.status -cne 'Built' -or $build.buildResult -cne 'Succeeded' -or $build.buildErrors -ne 0 -or $build.pid -ne $BuildProcess.pid -or
        $build.inputHash -cne $InputIdentity.sha256 -or $build.hostId -cne $HostRecord.id -or $build.resultId -cne $Result.id -or $build.workspaceId -cne $Workspace.id -or
        $build.unityVersion -cne $UnityVersion -or $build.addressablesVersion -cne $AddressablesVersion -or $build.buildFlavor -cne $BuildFlavor -or
        $build.backend -cne 'IL2CPP' -or $build.stripping -cne 'Medium' -or $build.target -cne 'StandaloneWindows64' -or
        $build.encryptedRemoteGroup -ne $true -or $build.graphics -ne $true -or $build.buildToken -cnotmatch '^[0-9a-f]{32}$') {
        throw 'The P0-B build report does not identify the dedicated native build.'
    }
    $playerRoot = Get-YLTestPath (Join-Path $HostRecord.path ('P/' + $build.buildToken.Substring(0, 8)))
    $player = Get-YLTestPath (Join-Path $playerRoot 'P.exe')
    if ($build.playerRoot -ine $playerRoot -or $build.player -ine $player -or
        $build.remoteRoot -ine (Join-Path $HostRecord.path ('RA/' + $build.buildToken.Substring(0, 8) + '/Remote'))) { throw 'Invalid P0-B content path.' }
    $null = Get-YLResourceAddressablesContentIdentity $build
    if ((Get-FileHash -LiteralPath $player).Hash -ine $build.playerSha256) { throw 'P0-B Player bytes changed.' }
    $protected = @($build.files | Where-Object { $_.area -ceq 'remote' -and $_.path -ceq $build.p0ProtectedBundle })
    $catalog = @($build.files | Where-Object { $_.area -ceq 'aa' -and $_.path -ceq $build.p0Catalog })
    if ($protected.Count -ne 1 -or $catalog.Count -ne 1 -or $protected[0].path -cnotmatch '^[a-z0-9_.-]+\.bundle$') { throw 'Missing P0-B content bytes.' }
    $protected = $protected[0]; $catalog = $catalog[0]
    $export = New-P0HeldCipherFixture -Build $build -PlayerRoot $playerRoot -ProtectedBundle $protected -EnableReader:($Mode -ceq 'Separated')
    $delivered = $export.file
    $contentRoot = $export.root
    if ($delivered.bytes -le $protected.bytes) { throw 'P0-B must actually serve Tink ciphertext, not the plaintext control.' }
    $gates = Join-Path $playerRoot 'P0Gates'
    if ([IO.Path]::Exists($gates)) { throw 'P0-B refuses reused response gates.' }
    $null = New-Item -ItemType Directory -Path $gates -ErrorAction Stop
    Write-YLTestJson -Path (Join-Path $playerRoot 'p0-held.json') -Value @{
        buildToken = $build.buildToken; catalog = $catalog.path; catalogSha256 = $catalog.sha256
        protectedPath = $protected.path; deliveredBytes = $delivered.bytes; deliveredSha256 = $delivered.sha256
    }
    $rows = [Collections.Generic.List[object]]::new()
    $requests = [Collections.Generic.List[object]]::new()
    $receipt = [ordered]@{ purpose = 'YLResource.P0B.HeldDownload'; status = 'Incomplete'; mode = $Mode; buildReport = $BuildReport
        protected = $protected; delivered = $delivered; catalog = $catalog; runs = @(); http = @() }
    $server = $null
    try {
        if (-not ('YLFramework.Testing.ResourceAddressablesContentServer' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot '../ResourceAddressablesContentServer.cs') }
        $routes = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
        $routes.Add($protected.path, $delivered.sha256)
        foreach ($round in 1..4) {
            $server = [YLFramework.Testing.ResourceAddressablesContentServer]::new($contentRoot, $build.buildToken, $routes, $gates, $protected.path)
            $runtime = Join-Path $playerRoot "run-$round.json"
            $start = [Diagnostics.ProcessStartInfo]::new($player)
            $start.UseShellExecute = $false; $start.WorkingDirectory = $playerRoot
            foreach ($argument in @('-batchmode', '-ylResourceAddressablesPlayerResult', $runtime, '-ylResourceAddressablesPlayerRound', "$round",
                '-ylResourceAddressablesContentBaseUrl', $server.BaseUrl, '-logFile', (Join-Path $Result.path "p0-b-$round-player.log"))) { $start.ArgumentList.Add($argument) }
            $process = Invoke-YLTestProcess -StartInfo $start -Log (Join-Path $Result.path "p0-b-$round-process.log") -TimeoutMilliseconds 45000
            $requests.AddRange([object[]]$server.Snapshot())
            $completedServer = $server
            $server = $null
            $completedServer.Dispose()
            $receipt.http = $requests.ToArray()
            $run = [ordered]@{ process = $process; report = $null; quit = $null }
            $rows.Add($run)
            $receipt.runs = $rows.ToArray()
            if ([IO.File]::Exists($runtime)) { $run.report = Read-YLPackageEvidenceJson $runtime }
            if ([IO.File]::Exists($runtime + '.quit.json')) { $run.quit = Read-YLPackageEvidenceJson ($runtime + '.quit.json') }
            if ($null -eq $run.report -or $null -eq $run.quit -or $run.report.pid -ne $process.pid -or $run.quit.pid -ne $process.pid -or
                $run.report.round -ne $round -or $run.quit.round -ne $round -or $run.report.inputHash -cne $InputIdentity.sha256 -or
                $run.report.buildToken -cne $build.buildToken -or $run.quit.buildToken -cne $build.buildToken -or
                $run.report.heldDownload.catalogSha256 -cne $catalog.sha256 -or $run.report.heldDownload.mode -cne $Mode -or
                $run.report.backend -cne 'IL2CPP' -or $run.report.pointerBits -ne 64 -or $run.report.graphicsDeviceType -cin @('Null', '') -or
                -not $run.quit.onApplicationQuit -or $run.quit.requestedExitCode -ne $process.exitCode -or
                $process.exitCode -notin @(0, 1) -or -not $process.observedExit -or $process.timedOut -or $process.cancelled -or $process.forcedCleanup -or
                $process.activeProcesses -ne 0 -or -not $process.streamsDrained -or -not $process.jobAssigned -or $process.cleanupError -or $process.observerError) {
                throw "P0-B round $round did not exit normally with a matching report."
            }
        }
        $violations = @(
            for ($n = 1; $n -le 4; $n++) {
                $matchesForRound = @($receipt.http | Where-Object round -eq $n)
                $playerReport = $rows[$n - 1].report
                if ($playerReport.status -cne 'Passed') { "player-$n $($playerReport.error)" }
                if ($matchesForRound.Count -ne 1 -or @($matchesForRound | Where-Object {
                    $_.method -cne 'GET' -or $_.path -cne $protected.path -or $_.status -ne 200 -or
                    $_.bytes -ne $delivered.bytes -or $_.sha256 -cne $delivered.sha256 -or -not $_.completedUtc
                }).Count) { "http-$n attempts=$($matchesForRound.Count)" }
            }
            if (@($receipt.http | Where-Object { $_.round -notin 1..4 }).Count) { 'unexpected HTTP requests' }
        )
        if ($violations.Count) {
            $receipt['violations'] = $violations
            if ($Mode -ceq 'Stock' -and $rows[1].report.heldDownload.sawGate -and $rows[1].report.heldDownload.realStartedBeforeRelease -and
                $rows[1].report.heldDownload.officialStatus -ceq 'Failed' -and $rows[2].report.heldDownload.officialStatus -ceq 'Failed' -and
                @($receipt.http | Where-Object { $_.status -eq 200 -and $_.bytes -eq $delivered.bytes -and $_.sha256 -ceq $delivered.sha256 }).Count -ge 4) {
                $receipt.status = 'Counterexample'
            }
            throw "P0-B shared-transfer contract was not met: $($violations -join '; ')"
        }
        if ($Mode -ceq 'Stock') { throw 'Stock unexpectedly met the candidate assertions; inspect the control fixture.' }
        foreach ($run in $rows) { Assert-YLTestProcessSucceeded $run.process }
        $receipt.status = 'Passed'
        return [pscustomobject] $receipt
    } finally {
        if ($null -ne $server) { $requests.AddRange([object[]]$server.Snapshot()); $receipt.http = $requests.ToArray() }
        try {
            if ($null -ne $server) { $server.Dispose() }
            $randomInput = Join-Path $playerRoot 'p0-random-input.bin'
            if ($Mode -ceq 'Separated') { [IO.File]::Delete((Get-YLTestPath $randomInput)) }
        }
        finally { Write-YLTestJson -Path (Join-Path $Result.path 'p0-b.json') -Value $receipt }
    }
}
