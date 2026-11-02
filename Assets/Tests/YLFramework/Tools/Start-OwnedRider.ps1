#requires -Version 7.6
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $AnalyzerRoot,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9-]+$')][string] $GenerationRun,
    [Parameter(Mandatory)][string] $RiderExecutable,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9-]+$')][string] $Run,
    [ValidatePattern('^[A-Za-z0-9-]+$')][string] $PolicyRun,
    [switch] $NoThirdParty,
    [ValidateRange(60,7200)][int] $LifetimeSeconds=3600
)
Set-StrictMode -Version 3.0
$ErrorActionPreference='Stop'
function Get-OwnedRiderLaunchHashes($Owner, $Policy, [string] $OwnerSha256) {
    $hashes=@{}
    foreach ($input in $Owner.Inputs) {
        if ($hashes.ContainsKey($input.Path)) { throw 'Rider launch policy: duplicate baseline input.' }
        $hashes[$input.Path]=$input.Sha256
    }
    if ($hashes.Count -ne 5) { throw 'Rider launch policy: exactly five baseline inputs required.' }
    if ($null -ne $Policy) {
        if ($Policy.PolicyWritten -ne $true -or $Policy.OwnerSha256 -cne $OwnerSha256 -or $Policy.Project -cne $Owner.Project -or
            $Policy.Unity -cne $Owner.Unity -or $Policy.PackageSha256 -cne $Owner.PackageSha256 -or $Policy.Rule -cne 'YL0008' -or $Policy.Action -cne 'Error' -or
            $Policy.RuleSet -cnotin @('Assets/Assembly-CSharp.ruleset','Assets/AnalyzerConsumers/Analyzer.Acceptance.Consumer.ruleset') -or
            $Policy.AfterSha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Rider launch policy: unapproved policy identity/action.' }
        $input=@($Owner.Inputs | Where-Object Path -CEQ $Policy.RuleSet)
        if ($input.Count -ne 1 -or $input[0].Sha256 -cne $Policy.BeforeSha256 -or $input[0].MetaSha256 -cne $Policy.MetaSha256) {
            throw 'Rider launch policy: policy baseline/meta differs.'
        }
        $hashes[$Policy.RuleSet]=$Policy.AfterSha256
    }
    return $hashes
}
$root=[IO.Path]::GetFullPath($AnalyzerRoot)
foreach ($leaf in @($root,$RiderExecutable)) {
    for ($path=[IO.Path]::GetFullPath($leaf); $path; $path=[IO.Path]::GetDirectoryName($path)) {
        if ((Test-Path -LiteralPath $path) -and (([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw 'Linked Rider ownership path.' }
    }
}
$ownerPath=Join-Path $root 'owner.json'
$owner=Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
$generation=Get-Content -LiteralPath (Join-Path $root ($GenerationRun+'.json')) -Raw | ConvertFrom-Json
if ($owner.Purpose -cne 'Stage9-owned-analyzer-inputs' -or $generation.Project -cne $owner.Project -or $generation.Unity -cne $owner.Unity -or
    -not $generation.Passed -or -not $generation.DefaultEditorUnchanged -or $generation.IdeLaunched -or
    $generation.Generator -cnotlike 'Packages.Rider.Editor.*') { throw 'A new exact Rider generation snapshot is required; VS output is not interchangeable.' }
foreach ($file in @(@{Path=$generation.Solution; Sha256=$generation.SolutionSha256})+@($generation.Projects)) {
    if ((Get-FileHash -LiteralPath $file.Path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.Sha256) { throw 'Rider generated project/solution changed.' }
}
$policy=$null; $policyHash=$null
if ($PolicyRun) {
    $policyPath=Join-Path $root ($PolicyRun+'.json')
    if (([IO.File]::GetAttributes($policyPath) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked launch policy.' }
    $policy=Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
    $policyHash=(Get-FileHash -LiteralPath $policyPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
$expectedInputs=Get-OwnedRiderLaunchHashes $owner $policy (Get-FileHash -LiteralPath $ownerPath -Algorithm SHA256).Hash.ToLowerInvariant()
foreach ($input in $owner.Inputs) {
    $path=Join-Path $owner.Project $input.Path
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expectedInputs[$input.Path] -or
        (Get-FileHash -LiteralPath ($path+'.meta') -Algorithm SHA256).Hash.ToLowerInvariant() -cne $input.MetaSha256) { throw 'Rider launch requires the exact baseline input identities.' }
}
if (@(Get-Process -Name rider64,Rider -ErrorAction SilentlyContinue).Count) { throw 'Never reuse or open a project in a user Rider instance.' }
$exe=[IO.Path]::GetFullPath($RiderExecutable)
if (-not (Test-Path -LiteralPath $exe -PathType Leaf) -or [IO.Path]::GetFileName($exe) -ine 'rider64.exe') { throw 'The explicit native Rider launcher is required.' }
$product=Get-Content -LiteralPath (Join-Path ([IO.Path]::GetDirectoryName($exe)) '../product-info.json') -Raw | ConvertFrom-Json
$destination=Join-Path $root ('Rider-'+$Run)
if (Test-Path -LiteralPath $destination) { throw 'Never adopt a previous Rider launch.' }
$null=[IO.Directory]::CreateDirectory($destination)
$disabled=Join-Path $env:APPDATA 'JetBrains/Rider2026.2/disabled_plugins.txt'
$disabledHash=if (Test-Path -LiteralPath $disabled) {(Get-FileHash -LiteralPath $disabled -Algorithm SHA256).Hash.ToLowerInvariant()} else {$null}
$start=[Diagnostics.ProcessStartInfo]::new($exe)
$start.UseShellExecute=$false
if ($NoThirdParty) { $start.ArgumentList.Add('disableNonBundledPlugins') }
$start.ArgumentList.Add($generation.Solution)
$requested=[DateTime]::UtcNow
$process=[Diagnostics.Process]::Start($start)
try {
    if ($process.WaitForExit(5000)) { throw 'Rider exited or handed off instead of preserving the newly started process; do not adopt another PID.' }
    $actual=Get-Process -Id $process.Id -ErrorAction Stop
    if (-not [string]::Equals($actual.Path,$exe,[StringComparison]::OrdinalIgnoreCase) -or $actual.StartTime.ToUniversalTime() -lt $requested.AddSeconds(-2)) { throw 'Unexpected Rider launch process.' }
    $ready=@{Purpose='Stage9-owned-rider'; Root=$destination; Project=$owner.Project; Unity=$owner.Unity; PackageSha256=$owner.PackageSha256;
        GenerationRun=$GenerationRun; Solution=$generation.Solution; SolutionSha256=$generation.SolutionSha256; Executable=$exe;
        Pid=$process.Id; StartUtc=$actual.StartTime.ToUniversalTime().ToString('O'); RequestedUtc=$requested.ToString('O');
        Version=$product.version; Build=$product.buildNumber; NonBundledPluginsDisabledForThisLaunch=[bool]$NoThirdParty;
        OriginalDisabledPluginsSha256=$disabledHash; PolicyRun=$PolicyRun; PolicySha256=$policyHash; ExpectedInputSha256=$expectedInputs;
        StartupOwnershipVerified=$true; AnalysisReady=$false}
    [IO.File]::WriteAllText((Join-Path $destination 'ready.json'),($ready | ConvertTo-Json -Depth 8))
    "OWNED RIDER START PID=$($process.Id)"
    $finished=$process.WaitForExit($LifetimeSeconds*1000)
    [IO.File]::WriteAllText((Join-Path $destination 'exit.json'),(@{Pid=$process.Id; Exited=$finished; ExitCode=$(if ($finished) {$process.ExitCode} else {$null});
        EndedUtc=[DateTime]::UtcNow.ToString('O'); KilledByHarness=$false; DeadlineExpired=(-not $finished)} | ConvertTo-Json))
    if (-not $finished) { throw 'Rider host deadline reached; no unverified process was terminated.' }
} finally { $process.Dispose() }
