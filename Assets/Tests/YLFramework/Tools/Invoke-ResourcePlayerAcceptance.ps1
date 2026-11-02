#requires -Version 7.6
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $EvidenceRoot,
    [ValidateSet('','2022.3.62f3c1','6000.3.27f1')][string] $ExpectedUnityVersion = '',
    [string] $Unity = '',
    [ValidateSet('Generate','Build','Export','Player')][string] $Operation = 'Player',
    [ValidateSet('Mono2x','IL2CPP')][string] $Backend = 'Mono2x', [switch] $CandidateValidation,
    [ValidateSet('auto','development','nondevelopment')][string] $DevelopmentProfile = 'auto',
    [string] $BuildName = 'Original', [string] $RunId, [string] $Command = 'offline',
    [string] $StoreName = 'none', [ValidateSet('none','encrypted')][string] $Mode = 'none',
    [int] $Expect = 1, [int] $Target = 2, [string] $Checkpoint, [string] $Fault,
    [ValidateSet('valid','missing','wrong')][string] $Key = 'valid',
    [ValidateSet('pin','default','wrong')][string] $Trust = 'pin', [switch] $RemoteMounts, [string] $ErrorKind, [string] $ErrorReason,
    [ValidateRange(30,7200)][int] $EditorTimeoutSeconds = 1800, [ValidateRange(30,7200)][int] $TimeoutSeconds = 300
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($EvidenceRoot)
$carrier = (Get-Content -LiteralPath (Join-Path $root 'carrier-location.json') -Raw | ConvertFrom-Json).Carrier
$owner = Get-Content -LiteralPath (Join-Path $carrier 'r810-owner.json') -Raw | ConvertFrom-Json
if ($owner.Purpose -cne 'Resource-8.10-owned-carrier' -or $owner.Evidence -cne $root -or $owner.Carrier -cne $carrier) { throw 'Owned carrier mismatch.' }
if ($BuildName -notmatch '^[A-Za-z0-9-]+$' -or ($StoreName -notmatch '^[a-z0-9-]+$')) { throw 'Simple explicit build and Store names required.' }
function Get-UnityRevision([string] $Version) {
    if ($Version -ceq '6000.3.27f1') { return '672d562a42a7' }
    if ($Version -ceq '2022.3.62f3c1') { return '1623fc0bbb97' }
    throw "Unsupported Unity version: $Version"
}
function Assert-UnityExecutable([string] $Path, [string] $Version) {
    if (-not $Path) { throw 'Explicit Unity executable is required for Editor operations.' }
    $full = [IO.Path]::GetFullPath($Path)
    if (-not [IO.File]::Exists($full)) { throw "Unity executable missing: $full" }
    $expected = $Version + '_' + (Get-UnityRevision $Version)
    $actual = [Diagnostics.FileVersionInfo]::GetVersionInfo($full).ProductVersion
    if ($actual -cne $expected) { throw "Unity executable version mismatch: expected $expected; found $actual" }
    $full
}
function Get-UnityProjectPath([string] $CommandLine) {
    $match = [regex]::Match($CommandLine, '(?:^|\s)-(?:projectPath|createProject)\s+(?:"([^"]+)"|(\S+))')
    if (-not $match.Success) { return $null }
    if ($match.Groups[1].Success) { return [IO.Path]::GetFullPath($match.Groups[1].Value) }
    [IO.Path]::GetFullPath($match.Groups[2].Value)
}
function Assert-NoOwnedUnity([string] $Carrier) {
    $full = [IO.Path]::GetFullPath($Carrier)
    $parent = [IO.Path]::GetDirectoryName($full)
    foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name = 'Unity.exe'")) {
        $commandLine = [string]$process.CommandLine
        if ($commandLine -like '*AssetImportWorker*') { continue }
        $project = Get-UnityProjectPath $commandLine
        if (-not $project) { continue }
        # 只把带本批 Editor 参数或位于同一交付父目录的 Editor 视为 owned；交互式源工程不在此停止范围。
        $owned = $commandLine.Contains('-r810-', [StringComparison]::Ordinal) -or
            $commandLine.Contains('-s9-', [StringComparison]::Ordinal) -or
            $commandLine.Contains('-createProject', [StringComparison]::Ordinal) -or
            $project.StartsWith($parent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
        if (-not $owned) { continue }
        if ($project -ieq $full) { throw 'This carrier already has a Unity instance; recover that same run.' }
        throw "Another owned Unity project is running: $project. Serialize owned Editor work."
    }
}
$ownerUnity = if ($owner.PSObject.Properties['UnityVersion']) { [string]$owner.UnityVersion } else { '2022.3.62f3c1' }
$ownerUgui = if ($owner.PSObject.Properties['UguiVersion']) { [string]$owner.UguiVersion } else { '1.0.0' }
$expectedUnity = if ($ExpectedUnityVersion) { $ExpectedUnityVersion } else { $ownerUnity }
if ($ownerUnity -cne $expectedUnity) { throw 'Owner/expected Unity version mismatch.' }
if (-not $owner.PSObject.Properties['UnityVersion'] -and $expectedUnity -cne '2022.3.62f3c1') { throw 'Legacy owner schema is 2022 only.' }
$requiredUgui = if ($expectedUnity -ceq '6000.3.27f1') { '2.0.0' } else { '1.0.0' }
if ($ownerUgui -cne $requiredUgui) { throw 'Owner UGUI version does not match the selected Editor.' }
$unityPath = if ($Unity) { $Unity } elseif ($expectedUnity -ceq '6000.3.27f1') { 'G:\Unity Editor\6000.3.27f1\Editor\Unity.exe' } else { 'C:\Program Files\Unity\Hub\Editor\2022.3.62f3c1\Editor\Unity.exe' }
if ($Operation -ne 'Player') { $unityPath = Assert-UnityExecutable $unityPath $expectedUnity }
if ($CandidateValidation -and $Backend -ne 'IL2CPP') { throw 'Candidate validation is IL2CPP only.' }
# 固定非生产夹具材料，只以子进程环境注入，绝不写内容key文件。
$env:YL_RESOURCE_CONTENT_KEY = [Convert]::ToBase64String([Security.Cryptography.SHA256]::HashData([Text.Encoding]::ASCII.GetBytes('R810 isolated nonproduction acceptance content only')))
try {
    if ($Operation -ne 'Player') {
        $method = if ($Operation -eq 'Generate') { 'Generate' } elseif ($Operation -eq 'Export') { 'Export' } else { 'Build' }
        $log = Join-Path $root ($Operation + '-' + $BuildName + '.log')
        $processPath = Join-Path $root ($Operation + '-' + $BuildName + '.process.json')
        $exitPath = Join-Path $root ($Operation + '-' + $BuildName + '.exit.json')
        if ((Test-Path -LiteralPath $log) -or (Test-Path -LiteralPath $processPath) -or (Test-Path -LiteralPath $exitPath)) { throw 'Never overwrite a prior Editor run.' }
        Assert-NoOwnedUnity $carrier
        $editorStart = [Diagnostics.ProcessStartInfo]::new($unityPath)
        $editorStart.UseShellExecute = $false
        foreach ($argument in @('-batchmode','-quit','-projectPath',$carrier,'-buildTarget','Win64','-executeMethod',('ResourcePlayerAcceptanceBuild.' + $method),'-r810-build',$BuildName,'-r810-profile',$DevelopmentProfile,'-logFile',$log)) { $editorStart.ArgumentList.Add($argument) }
        $editorStart.ArgumentList.Add('-r810-backend'); $editorStart.ArgumentList.Add($Backend)
        if ($CandidateValidation) { $editorStart.ArgumentList.Add('-r810-il2cpp-candidate') }
        $editor = [Diagnostics.Process]::Start($editorStart)
        $timedOut = $false
        try {
            [IO.File]::WriteAllText($processPath, (@{ Pid=$editor.Id; Executable=$unityPath; ProjectPath=$carrier; ExpectedUnityVersion=$expectedUnity; Arguments=$editorStart.ArgumentList; StartedUtc=[DateTime]::UtcNow.ToString('O') } | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
            if (-not $editor.WaitForExit($EditorTimeoutSeconds * 1000)) {
                $timedOut = $true
                $editor.Kill($true)
                $editor.WaitForExit()
            }
            $exitCode = $editor.ExitCode
            [IO.File]::WriteAllText($exitPath, (@{ Pid=$editor.Id; ExitCode=$exitCode; TimedOut=$timedOut; EndedUtc=[DateTime]::UtcNow.ToString('O') } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
            if ($timedOut) { throw "Unity $Operation timed out after $EditorTimeoutSeconds seconds; preserve $log" }
            if ($exitCode -ne 0) { throw "Unity $Operation failed with code $exitCode. Preserve $log" }
        } finally {
            if (-not $editor.HasExited) { $editor.Kill($true); $editor.WaitForExit() }
            $editor.Dispose()
        }
        if ($Operation -eq 'Build') {
            $built = Get-Content -LiteralPath (Join-Path $carrier ('Builds/' + $BuildName + '/BuildReport.json')) -Raw | ConvertFrom-Json
            $expectedDevelopment = if ($DevelopmentProfile -eq 'auto') { -not $BuildName.StartsWith('R', [StringComparison]::Ordinal) } else { $DevelopmentProfile -eq 'development' }
            $expectedStripping = if ($Backend -eq 'IL2CPP') { 'Low' } else { 'Disabled' }
            if ($built.Result -cne 'Succeeded' -or $built.Errors -ne 0 -or $built.Backend -cne $Backend -or $built.Unity -cne $expectedUnity -or
                $built.ManagedStripping -cne $expectedStripping -or ($Backend -eq 'IL2CPP' -and $built.CppConfiguration -cne 'Release') -or
                [bool]$built.Development -ne $expectedDevelopment -or [bool]$built.CandidateValidation -ne [bool]$CandidateValidation) {
                throw 'The actual BuildReport does not match the requested Unity/backend/stripping/Development/candidate identity.'
            }
        }
        "R810 $Operation finished: $log"
        return
    }
    if ($RunId -notmatch '^[a-z0-9-]+$') { throw 'Explicit fresh RunId required.' }
    $run = Join-Path $root ('Runs/' + $RunId)
    if (Test-Path -LiteralPath $run) { throw 'Never overwrite a prior Player run.' }
    if (-not (Test-Path -LiteralPath (Join-Path $root 'Runs') -PathType Container)) { throw 'Run parent missing.' }
    $build = Join-Path $carrier ('Builds/' + $BuildName)
    $reportPath = Join-Path $build 'BuildReport.json'
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ($report.Result -cne 'Succeeded' -or $report.Errors -ne 0 -or $report.Backend -cne $Backend) { throw "A successful exact $Backend Player BuildReport is required." }
    if ($report.Unity -cne $expectedUnity) { throw 'BuildReport Unity version differs from the carrier identity.' }
    if ([bool]$report.CandidateValidation -ne [bool]$CandidateValidation) { throw 'BuildReport candidate policy differs from the requested build.' }
    foreach ($row in $report.PlayerFiles) {
        $path = Join-Path $build $row.Path
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $row.Sha256) { throw "Player file identity changed: $($row.Path)" }
    }
    $reportSha = (Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $null = [IO.Directory]::CreateDirectory($run)
    $null = [IO.Directory]::CreateDirectory((Join-Path $run 'tmp'))
    $result = Join-Path $run 'result.json'
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $build 'ResourceAcceptance.exe'))
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.WorkingDirectory = $build
    foreach ($arg in @('-screen-fullscreen','0','-screen-width','160','-screen-height','120','-logFile',(Join-Path $run 'Player.log'),
        '--r810-out',$result,'--r810-store',(Join-Path $root ('Stores/' + $StoreName)),'--r810-command',$Command,'--r810-mode',$Mode,
        '--r810-expect',"$Expect",'--r810-target',"$Target",'--r810-key',$Key,'--r810-trust',$Trust,'--r810-remote',$RemoteMounts.IsPresent.ToString().ToLowerInvariant())) { $start.ArgumentList.Add($arg) }
    $start.ArgumentList.Add('--r810-backend'); $start.ArgumentList.Add($Backend)
    if ($ErrorKind) { $start.ArgumentList.Add('--r810-error'); $start.ArgumentList.Add($ErrorKind) }
    if ($ErrorReason) { $start.ArgumentList.Add('--r810-reason'); $start.ArgumentList.Add($ErrorReason) }
    if ($Checkpoint) { $start.ArgumentList.Add('--r810-checkpoint'); $start.ArgumentList.Add($Checkpoint) }
    if ($Fault) { $start.ArgumentList.Add('--r810-fault'); $start.ArgumentList.Add($Fault) }
    $start.Environment['TEMP'] = Join-Path $run 'tmp'; $start.Environment['TMP'] = Join-Path $run 'tmp'
    $start.Environment['YL_RESOURCE_CONTENT_KEY'] = $env:YL_RESOURCE_CONTENT_KEY
    $fileWatch = $null
    if ($Mode -eq 'encrypted') {
        Add-Type -TypeDefinition ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ResourcePlayerFileWatch.cs.txt')))
        $fileWatch = [ResourcePlayerFileWatch]::new($root, $run, (Join-Path $root ('Stores/' + $StoreName)))
    }
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
    $processId = $process.Id
    $killed = $false; $timedOut = $false
    try {
        [IO.File]::WriteAllText((Join-Path $run 'process-start.json'), (@{ Pid=$processId; Executable=$start.FileName; Arguments=$start.ArgumentList; ExpectedUnityVersion=$expectedUnity; Backend=$Backend; BuildReportSha256=$reportSha; StartedUtc=[DateTime]::UtcNow.ToString('O') } | ConvertTo-Json -Depth 6))
        if (($Checkpoint -and $Fault -ne 'throw') -or $Command -eq 'remote-kill-partial') {
            $watch = [IO.FileSystemWatcher]::new($run,'result.json.checkpoint')
            try {
                if (-not [IO.File]::Exists($result + '.checkpoint')) { $null = $watch.WaitForChanged([IO.WatcherChangeTypes]::Created, $TimeoutSeconds * 1000) }
                if (-not [IO.File]::Exists($result + '.checkpoint')) { throw 'The owned Player did not reach its declared checkpoint.' }
                $reported = [int][IO.File]::ReadAllText($result + '.checkpoint')
                if ($reported -ne $processId -or $process.HasExited -or $process.MainModule.FileName -cne $start.FileName) { throw 'Checkpoint process identity mismatch; refuse to stop it.' }
                $process.Kill($true); $killed = $true
            } finally { $watch.Dispose() }
        }
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { $timedOut=$true; $process.Kill($true); $process.WaitForExit(); }
        $body = if ([IO.File]::Exists($result)) { Get-Content -LiteralPath $result -Raw | ConvertFrom-Json } else { $null }
        if ($fileWatch) { $fileWatch.Dispose() }
        $expectAcceptance = @($report.ExtraDefines) -ccontains 'YL_RESOURCE_ACCEPTANCE_TESTS'
        $expectCandidate = @($report.ExtraDefines) -ccontains 'YL_RESOURCE_IL2CPP_CANDIDATE'
        $bodyGate = $null -ne $body -and
            $body.Unity -ceq $expectedUnity -and
            $body.Backend -ceq $Backend -and
            [bool]$body.Development -eq [bool]$report.Development -and
            $body.PSObject.Properties['AcceptanceTestsCompiled'] -ne $null -and [bool]$body.AcceptanceTestsCompiled -eq $expectAcceptance -and
            $body.PSObject.Properties['CandidateCompiled'] -ne $null -and [bool]$body.CandidateCompiled -eq $expectCandidate -and
            $body.PSObject.Properties['Graphics'] -ne $null -and [string]$body.Graphics -cne '' -and [string]$body.Graphics -cne 'Null' -and
            $body.PSObject.Properties['GraphicsDevice'] -ne $null -and [string]$body.GraphicsDevice -ne '' -and
            $body.PSObject.Properties['Screen'] -ne $null -and [int]$body.Screen.Width -gt 0 -and [int]$body.Screen.Height -gt 0 -and
            $body.PSObject.Properties['AudioContext'] -ne $null -and [int]$body.AudioContext.SampleRate -gt 0
        $expectPin = [bool]$report.Development -and $expectAcceptance
        $bodyGate = $bodyGate -and [bool]$body.LoopbackPinCompiled -eq $expectPin
        if ($report.PSObject.Properties['BuildGuid']) { $bodyGate = $bodyGate -and $body.BuildGuid -ceq [string]$report.BuildGuid }
        $passed = -not $timedOut -and $bodyGate -and (($killed -and $null -ne $body) -or (-not $killed -and $process.ExitCode -eq 0 -and $null -ne $body -and $body.Passed)) -and ($null -eq $fileWatch -or ($fileWatch.Errors -eq 0 -and $fileWatch.Plaintext -eq 0))
        $receipt = @{ Pid=$processId; ExitCode=$process.ExitCode; ControlledKill=$killed; TimedOut=$timedOut; Passed=$passed; Result=$result; CompletedUtc=[DateTime]::UtcNow.ToString('O'); WriteObservation=if ($fileWatch) { @{ Errors=$fileWatch.Errors; Plaintext=$fileWatch.Plaintext; ProbeMisses=$fileWatch.Misses; ControlEntriesNotProbed=$fileWatch.ControlEntriesNotProbed; Attribution='Unavailable'; Scope='Owned run/TEMP/Store event observation; mutable Store JSON/next/ReplaceFile temporary controls are metadata-only, not read; not proof of all transient writes' } } else { $null } }
        [IO.File]::WriteAllText((Join-Path $run 'stdout.log'), $stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $run 'stderr.log'), $stderr.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $run 'process-result.json'), ($receipt | ConvertTo-Json))
        $receipt | ConvertTo-Json -Compress
        if (-not $passed) { throw 'Actual Player gate failed; preserve this run and original failure.' }
    } finally {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit(); }
        if ($fileWatch) { $fileWatch.Dispose() }
        $process.Dispose()
    }
} finally { $env:YL_RESOURCE_CONTENT_KEY = $null }
