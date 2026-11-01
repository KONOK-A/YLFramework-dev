[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $BuildDirectory,
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-z0-9-]+$')][string] $Run,
    [ValidatePattern('^[a-z0-9-]+$')][string] $Attempt = '01',
    [switch] $AuditExisting
)
$ErrorActionPreference = 'Stop'
trap { throw ("C Player wrapper failed at line " + $_.InvocationInfo.ScriptLineNumber + ": " + $_.Exception.Message) }
$root = 'G:\Unity\MC26-20261020T173208Z'
$utf8 = New-Object Text.UTF8Encoding($false)
$owner = [IO.File]::ReadAllText((Join-Path $root 'owner.json')) | ConvertFrom-Json
if ($owner.sourceTip -ne 'aadc11b5099b76f16667aa9818ca927104cd12be' -or $owner.run -ne 'MC26-20261020T173208Z') { throw 'Wrong C evidence owner.' }
$directory = [IO.Path]::GetFullPath($BuildDirectory).TrimEnd('\')
if (-not $directory.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $directory)) { throw 'Build is not inside this owned Run.' }
$isMain = Test-Path -LiteralPath (Join-Path $directory 'build.json')
$reportPath = Join-Path $directory $(if ($isMain) { 'build.json' } else { 'BuildReport.json' })
$build = [IO.File]::ReadAllText($reportPath) | ConvertFrom-Json
if ($build.result -ne 'Succeeded' -or $build.errors -ne 0 -or $build.options -ne 'None' -or $build.development -or $build.error) { throw 'An actual successful non-Development build is required.' }
if ($build.backend -notin @('Mono2x', 'IL2CPP') -or ($build.backend -eq 'IL2CPP' -and ($build.stripping -ne 'Low' -or $build.compiler -ne 'Release'))) { throw 'Actual backend/Low/Release mismatch.' }
if ($isMain) {
    $complete = [IO.File]::ReadAllText((Join-Path $directory 'gate-complete.json')) | ConvertFrom-Json
    $restored = [IO.File]::ReadAllText((Join-Path $directory 'settings-restored.json')) | ConvertFrom-Json
    if (-not $build.passed -or $build.pid -ne $owner.pid -or -not $complete.closed -or -not $restored.restored -or -not $restored.memoryRestored -or -not $restored.scenesRestored) { throw 'The main build has not restored the protected Editor.' }
    $exe = [IO.Path]::GetFullPath($build.executable)
} else {
    if (-not $build.AuditPassed -or $build.Failure -or $build.Run -ne $Run) { throw 'The receiver build audit or Run binding failed.' }
    $exe = Join-Path $directory 'DomainAcceptance.exe'
}
if (-not $exe.StartsWith($directory + '\', [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $exe)) { throw 'Owned executable is missing.' }
$exeHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
if ($isMain) {
    $manifest = [IO.File]::ReadAllText((Join-Path $directory 'player-files.json')) | ConvertFrom-Json
    $entry = @($manifest | Where-Object { [IO.Path]::GetFullPath($_.path) -eq $exe })
    if ($entry.Count -ne 1 -or $entry[0].sha256 -ne $exeHash) { throw 'The executable differs from its build manifest.' }
}
$runs = Join-Path $root 'PlayerRuns'
if (-not (Test-Path -LiteralPath $runs)) { $null = [IO.Directory]::CreateDirectory($runs) }
$folder = Join-Path $runs "$Run-$Attempt"
if ((Test-Path -LiteralPath $folder) -ne [bool]$AuditExisting) { throw 'Player evidence existence does not match the requested new-run/post-exit audit mode.' }
if (-not $AuditExisting) { $null = [IO.Directory]::CreateDirectory($folder) }
$log = Join-Path $folder 'Player.log'
$resultPath = Join-Path $folder 'player-result.json'
$arguments = @('-screen-width', '960', '-screen-height', '540', '-screen-fullscreen', '0', '-logFile', $log,
    '-domainRun', $Run, '-domainResult', $resultPath, '-UNITY_MCP_KEEP_CONNECTED=false', '-UNITY_MCP_START_SERVER=false')
$start = New-Object Diagnostics.ProcessStartInfo
$start.FileName = $exe
$start.Arguments = ($arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
$start.UseShellExecute = $false
$start.EnvironmentVariables['CI'] = 'true'
$receipt = @{ run = $Run; attempt = $Attempt; main = [bool]$isMain; executable = $exe; executableSha256 = $exeHash;
    buildReport = $reportPath; buildReportSha256 = (Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash.ToLowerInvariant();
    backend = $build.backend; arguments = $arguments; startedUtc = [DateTime]::UtcNow.ToString('o'); passed = $false }
$receiptPath = Join-Path $folder $(if ($AuditExisting) { 'postexit-log-audit.json' } else { 'os-run.json' })
function Save-Receipt { [IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 8), $utf8) }
if ($AuditExisting) {
    if (Test-Path -LiteralPath $receiptPath) { throw 'Post-exit audit evidence already exists.' }
    $originalReceipt = Join-Path $folder 'os-run.json'
    $prior = [IO.File]::ReadAllText($originalReceipt) | ConvertFrom-Json
    if ($prior.exitCode -ne 0 -or $prior.run -ne $Run -or $prior.executable -ne $exe -or $prior.executableSha256 -ne $exeHash -or -not $prior.windowHandle) { throw 'No bound successful OS exit/window receipt is available for a post-exit audit.' }
    $receipt = @{}
    foreach ($property in $prior.PSObject.Properties) { $receipt[$property.Name] = $property.Value }
    $receipt.originalOsReceiptSha256 = (Get-FileHash -LiteralPath $originalReceipt -Algorithm SHA256).Hash.ToLowerInvariant()
    $receipt.auditUtc = [DateTime]::UtcNow.ToString('o')
} else {
    $process = [Diagnostics.Process]::Start($start)
    $null = $process.Handle
    $receipt.pid = $process.Id
    Save-Receipt
    "C Player PID=$($process.Id) backend=$($build.backend) output=$folder"
    $deadline = [DateTime]::UtcNow.AddMinutes(5)
    do {
        $exited = $process.WaitForExit(100)
        if (-not $exited) {
            $process.Refresh()
            if ($process.MainWindowHandle -ne [IntPtr]::Zero) { $receipt.windowHandle = $process.MainWindowHandle.ToInt64() }
            if ([DateTime]::UtcNow -gt $deadline) { Save-Receipt; throw "Player deadline exceeded; retain PID $($process.Id), do not kill or blindly restart." }
        }
    } while (-not $exited)
    $process.WaitForExit()
    $receipt.exitCode = $process.ExitCode
    $receipt.endedUtc = [DateTime]::UtcNow.ToString('o')
    Save-Receipt
}
if ($receipt.exitCode -ne 0) { throw "Player OS exit was $($receipt.exitCode); original evidence retained." }
$result = [IO.File]::ReadAllText($resultPath) | ConvertFrom-Json
if (-not $result.passed -or -not $result.quitObserved -or -not $result.handlerRestored -or -not $result.resourceReturned -or -not $result.poolsReused -or @($result.unexpectedErrors).Count -ne 0) { throw 'The real consumer or its late-quit receipt failed.' }
if ($result.run -ne $Run -or $result.pid -ne $receipt.pid -or $result.backend -ne $build.backend -or $result.development -or $result.batchMode -or $result.unity -ne '2022.3.62f3c1') { throw 'Actual Player Run/PID/backend/version does not match the current build.' }
if (-not $receipt.windowHandle -or $result.graphics -match '^Null' -or $result.width -le 0 -or $result.height -le 0) { throw 'No actual graphical Player window/device was observed.' }
if ($result.expectedErrors -ne 1 -or $result.expectedAsserts -ne 1 -or $result.expectedExceptions -ne 1) { throw 'The three exact expected Logger probes are missing or duplicated.' }
$text = [IO.File]::ReadAllText($log)
$pass = 'YL_C_ACCEPTANCE_PASS run=' + $Run + ' backend=' + $build.backend
if ([regex]::Matches($text, [regex]::Escape($pass)).Count -ne 1 -or $text -notmatch 'GfxDevice:' -or $text -match 'NullGfxDevice') { throw 'Missing unique graphical success marker.' }
# Release logs can be consecutive; remove only exact probe headers and their adjacent stack frames.
$expectedHeads = @("${Run}/probe-error", "${Run}/probe-assert", "InvalidOperationException: ${Run}/probe-exception")
$seen = @{}
$inExpectedStack = $false
$residualLines = New-Object 'System.Collections.Generic.List[string]'
foreach ($line in ($text -split '\r?\n')) {
    $header = $line.Trim()
    if ($expectedHeads -contains $header) {
        if (-not $seen.ContainsKey($header)) { $seen[$header] = 0 }
        $seen[$header]++
        $inExpectedStack = $true
        continue
    }
    if ($inExpectedStack -and $line -match '^\s*(?:at\s+)?(?:UnityEngine|YLFramework[.]Logging|YLFramework[.]Tests[.]DomainAcceptance|System)[.][^\r\n]*[:.][^\r\n]*\([^\r\n]*\)(?:\s*\(at [^\r\n]*\))?\s*$') { continue }
    $inExpectedStack = $false
    $residualLines.Add($line)
}
foreach ($header in $expectedHeads) { if ($seen[$header] -ne 1) { throw "Expected exactly one raw log block: $header" } }
$unexpectedText = $residualLines -join "`n"
if ($unexpectedText -match '(?im)Exception:|error CS[0-9]+|Crash!!!|Assertion failed|^ERROR:|UnityEngine[.]Debug:LogError|UnityEngine[.]Debug:LogAssertion|UnityEngine[.]Debug:LogException|\[Error\]|\[Exception\]') {
    [IO.File]::WriteAllText((Join-Path $folder 'unbound-log.txt'), $unexpectedText, $utf8)
    throw "An unbound error remains in the complete post-exit Player log: $($Matches[0])"
}
$receipt.playerResultSha256 = (Get-FileHash -LiteralPath $resultPath -Algorithm SHA256).Hash.ToLowerInvariant()
$receipt.logSha256 = (Get-FileHash -LiteralPath $log -Algorithm SHA256).Hash.ToLowerInvariant()
$receipt.graphics = $result.graphics
$receipt.expectedProbeHeaders = $expectedHeads
$receipt.lateLogChecked = $true
$receipt.passed = $true
Save-Receipt
[pscustomobject]@{ Run = $Run; PID = $receipt.pid; OSExit = $receipt.exitCode; Backend = $result.backend; Graphics = $result.graphics; Window = $receipt.windowHandle; ExpectedLoggerProbes = 3; UnexpectedErrors = 0; Passed = $true } | ConvertTo-Json
