#requires -Version 7.6
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $DeliveryRoot,
    [Parameter(Mandatory)][ValidatePattern('^Receiver[A-Z][0-9]*$')][string] $Receiver,
    [Parameter(Mandatory)][ValidateSet('Mono','Il2Cpp')][string] $Backend,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9-]+$')][string] $BuildRun,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9-]+$')][string] $Run
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($DeliveryRoot)
$project = Join-Path $root $Receiver
$build = Join-Path $project ('P/loop-' + $(if ($Backend -eq 'Mono') { 'm-' } else { 'i-' }) + $BuildRun)
for ($cursor = $build; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
    if (([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked scene-loop build.' }
}
$owner = Get-Content -LiteralPath (Join-Path $root 'owner.json') -Raw | ConvertFrom-Json
$identity = Get-Content -LiteralPath (Join-Path $project 'stage9-owner.json') -Raw | ConvertFrom-Json
if ($owner.purpose -cne 'YLFramework-Stage9-independent-delivery' -or $owner.root -cne $root -or
    $identity.Purpose -cne 'Stage9-independent-receiver' -or $identity.Project -cne $project -or $identity.Delivery -cne $root) { throw 'Wrong owned control identity.' }
function Hash([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Json([string] $Path, $Value) { [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false)) }
$reportPath = Join-Path $build 'BuildReport.json'
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
$expectedBackend = if ($Backend -eq 'Mono') { 'Mono2x' } else { 'IL2CPP' }
if ($report.Result -cne 'Succeeded' -or $report.Errors -ne 0 -or $report.Development -or
    $report.Backend -cne $expectedBackend -or $report.Unity -cne $identity.UnityVersion -or $report.PackageSha256 -cne $identity.PackageSha256) { throw 'Control build identity failed.' }
foreach ($file in $report.PlayerFiles) {
    if ((Hash (Join-Path $build $file.Path)) -cne $file.Sha256) { throw 'Control build bytes changed.' }
}
$runRoot = Join-Path $root ('Runs/' + $Receiver + '-loop-' + $Backend + '-' + $Run)
if (Test-Path -LiteralPath $runRoot) { throw 'Never replace a control run; preserve its last progress.' }
$null = [IO.Directory]::CreateDirectory($runRoot)
$output = Join-Path $runRoot 'result.json'
$log = Join-Path $runRoot 'Player.log'
$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $build 'Stage9SceneLoop.exe'))
$start.UseShellExecute = $false; $start.WorkingDirectory = $build
foreach ($argument in @('-screen-fullscreen','0','-screen-width','640','-screen-height','360','-logFile',$log,
    '--s9-loop-out',$output,'--s9-loop-unity',$identity.UnityVersion)) { $start.ArgumentList.Add($argument) }
$process = [Diagnostics.Process]::Start($start)
$processId = $process.Id
Json (Join-Path $runRoot 'process.json') @{ Pid=$processId; Project=$project; Executable=$start.FileName; Arguments=@($start.ArgumentList); BuildReportSha256=Hash $reportPath; StartedUtc=[DateTime]::UtcNow.ToString('O') }
$timedOut = $false
try {
    if (-not $process.WaitForExit(240000)) { $timedOut=$true; $process.Kill($true); $process.WaitForExit() }
    $exitCode = $process.ExitCode
} finally { $process.Dispose() }
Json (Join-Path $runRoot 'exit.json') @{ Pid=$processId; ExitCode=$exitCode; TimedOut=$timedOut; EndedUtc=[DateTime]::UtcNow.ToString('O') }
if (-not [IO.File]::Exists($output)) { throw 'No control progress/result file; preserve process and log evidence.' }
$result = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
$text = [IO.File]::ReadAllText($log)
$errors = @([regex]::Matches($text, '(?im)^.*(?:\b[A-Za-z][A-Za-z0-9_.]*Exception\s*:|Assertion failed|Fatal Error|Crash!!!).*$') | ForEach-Object Value)
$passed = -not $timedOut -and $exitCode -eq 0 -and $result.Passed -and $result.Pid -eq $processId -and
    $result.Unity -ceq $identity.UnityVersion -and $result.Backend -ceq $expectedBackend -and -not $result.Development -and
    $result.BuildGuid -ceq $report.BuildGuid -and $result.CompletedRounds -eq 20 -and $result.UnloadCount -eq 20 -and
    @($result.Errors).Count -eq 0 -and $errors.Count -eq 0 -and $text.Contains('STAGE9_SCENE_LOOP_PASS')
Json (Join-Path $runRoot 'verdict.json') @{ Passed=$passed; TimedOut=$timedOut; Pid=$processId; ExitCode=$exitCode; Result=$result;
    LogErrors=$errors; ResultSha256=Hash $output; LogSha256=Hash $log; BuildReportSha256=Hash $reportPath }
if (-not $passed) { throw "Pure Unity control failed; preserve last progress: $runRoot" }
"Pure Unity control passed 20 round trips and explicit unloads: $runRoot"
