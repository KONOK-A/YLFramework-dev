#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $WorkspaceRoot,
    [Parameter(Mandatory)][string] $Unity,
    [ValidateRange(1, 86400)][int] $TimeoutSeconds = 120
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Common/Process.ps1')
. (Join-Path $PSScriptRoot '../Common/Results.ps1')
. (Join-Path $PSScriptRoot '../UnityHost.ps1')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$configuration = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../TestSuites.json')) | ConvertFrom-Json -DateKind String
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($Unity)
$editor = Get-YLTestEditorProfile -Editors $configuration.editors -VersionInfo $version
$workspace = Initialize-YLTestWorkspace -WorkspaceRoot $WorkspaceRoot -Repository $repository
$fixture = New-YLTestDirectory -Workspace $workspace -Kind Cache -Key "UnitySafety-$($version.ProductVersion)"
$fixtureLease = Lock-YLTestDirectory -Workspace $workspace -Path $fixture.path
$run = New-YLTestDirectory -Workspace $workspace -Kind Result -Key "Tooling-UnitySafety-$($version.ProductVersion)"
$lease = Lock-YLTestDirectory -Workspace $workspace -Path $run.path
$summary = [ordered]@{
    schemaVersion = 1; ownerId = $run.id; action = 'Run'; suite = 'Tooling'; module = 'UnitySafety'
    profile = $version.ProductVersion; status = 'Failed'; endUtc = $null; reason = $null
    processes = @(); checks = 0; fixture = $fixture.path
}
$script:step = 0
$changedTest = $null
$originalTest = $null

function Assert-Safety([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Invoke-SafetyProcess([string] $Script, [string[]] $Arguments, [int] $ExpectedExit = 0) {
    $script:step++
    $start = [Diagnostics.ProcessStartInfo]::new([Environment]::ProcessPath)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $source
    foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $Script) + $Arguments) {
        $start.ArgumentList.Add($argument)
    }
    $log = Join-Path $run.path "cli-$script:step.log"
    $fact = Invoke-YLTestProcess -StartInfo $start -Log $log -TimeoutMilliseconds 1800000
    $summary.processes += $fact
    Assert-Safety ($fact.observedExit -and $fact.exitCode -eq $ExpectedExit -and -not $fact.forcedCleanup -and
        -not $fact.observerError -and -not $fact.cleanupError -and $fact.activeProcesses -eq 0) "Unexpected CLI exit/cleanup: $($fact.status) / $($fact.exitCode)"
    return [pscustomobject]@{ fact = $fact; text = [IO.File]::ReadAllText($log) }
}

function Invoke-FixtureEntry([string] $Action, [string] $Selection, [int] $ExpectedExit = 0) {
    $arguments = @('-Action', $Action, '-Suite', 'Daily', '-Module', 'Reddot', '-Unity', $Unity,
        '-WorkspaceRoot', $fixtureWorkspace.root, '-TimeoutSeconds', [string]$TimeoutSeconds)
    if ($Selection) { $arguments += @('-Selection', $Selection) }
    $result = Invoke-SafetyProcess -Script (Join-Path $source 'Tools/Testing/Invoke-YLTests.ps1') -Arguments $arguments -ExpectedExit $ExpectedExit
    return $result.text | ConvertFrom-Json -DateKind String
}

try {
    $source = Join-Path $fixture.path 'Source'
    $inputs = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $definition = @($configuration.modules | Where-Object { $_.name -ceq 'Reddot' })[0]
    foreach ($relative in @($definition.runtimeSources) + @($definition.nativeSources) + @($definition.assemblyMetadata) + @(
        'Assets/Tests/YLFramework/Infrastructure/Editor/YLTestEntry.cs',
        'Assets/Tests/YLFramework/Infrastructure/Editor/YLFramework.Tests.Infrastructure.Editor.asmdef')) {
        $null = $inputs.Add($relative)
        $null = $inputs.Add($relative + '.meta')
    }
    foreach ($relative in @($definition.editTests) + @($definition.playTests)) {
        $null = $inputs.Add($relative + '.meta')
        foreach ($file in Get-YLTestTreeFiles (Join-Path $repository $relative)) {
            $null = $inputs.Add([IO.Path]::GetRelativePath($repository, $file).Replace('\', '/'))
        }
    }
    foreach ($file in Get-YLTestTreeFiles (Join-Path $repository 'Tools/Testing')) {
        $null = $inputs.Add([IO.Path]::GetRelativePath($repository, $file).Replace('\', '/'))
    }
    $null = $inputs.Add('Tools/YLFramework.Analyzers/global.json')
    $statePath = Join-Path $fixture.path 'sources.json'
    $previous = @{}
    if ([IO.File]::Exists($statePath)) {
        $state = [IO.File]::ReadAllText($statePath) | ConvertFrom-Json -DateKind String
        Assert-Safety ($state.fixtureId -ceq $fixture.id) 'The fixture source receipt has a different owner.'
        foreach ($file in $state.files) { $previous[$file.path] = $file.sha256 }
    }
    if ([IO.Directory]::Exists($source)) {
        foreach ($file in Get-YLTestTreeFiles $source) {
            $relative = [IO.Path]::GetRelativePath($source, $file).Replace('\', '/')
            Assert-Safety ($previous.ContainsKey($relative) -and (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ceq $previous[$relative]) "Preserve unknown/changed fixture input: $relative"
        }
    }
    $manifest = @(foreach ($relative in $inputs) {
        $path = Get-YLTestPath (Join-Path $source $relative)
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
        [IO.File]::Copy((Join-Path $repository $relative), $path, $true)
        [ordered]@{ path = $relative; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    })
    foreach ($relative in $previous.Keys) {
        if (-not $inputs.Contains($relative)) { [IO.File]::Delete((Join-Path $source $relative)) }
    }
    $temporary = $statePath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    Write-YLTestJson -Path $temporary -Value @{ fixtureId = $fixture.id; files = $manifest }
    [IO.File]::Move($temporary, $statePath, $true)
    $fixtureWorkspace = Initialize-YLTestWorkspace -WorkspaceRoot (Join-Path $fixture.path 'Workspace') -Repository $source

    $nativeRecord = $null
    foreach ($case in @(
        @{ script = 'Test-YLNUnitHost.ps1'; label = 'NUnit host'; module = 'NUnitHost' },
        @{ script = 'Test-YLQuick.ps1'; label = 'Quick CLI'; module = 'QuickCLI' },
        @{ script = 'Test-YLUnity.ps1'; label = 'Unity CLI'; module = 'UnityCLI' })) {
        $arguments = @('-WorkspaceRoot', $fixtureWorkspace.root, '-Unity', $Unity)
        if ($case.module -eq 'UnityCLI') { $arguments += @('-TimeoutSeconds', [string]$TimeoutSeconds) }
        $result = Invoke-SafetyProcess -Script (Join-Path $source "Tools/Testing/Tests/$($case.script)") -Arguments $arguments
        $match = [regex]::Match($result.text, '(?m)^' + [regex]::Escape($case.label) + ' contract evidence: (.+)$')
        Assert-Safety $match.Success 'The contract runner did not publish its owned evidence path.'
        $record = Get-YLTestDirectory -Workspace $fixtureWorkspace -Path $match.Groups[1].Value.Trim()
        $contract = [IO.File]::ReadAllText((Join-Path $record.path 'summary.json')) | ConvertFrom-Json -DateKind String
        Assert-Safety ($contract.status -ceq 'Passed') 'The underlying real contract did not pass.'
        $target = if ($case.module -eq 'UnityCLI') { 'UnityCLI-Daily-Reddot' } elseif ($case.module -eq 'QuickCLI') { 'QuickCLI-Quick-Reddot' } else { $case.module }
        Assert-Safety ($record.key -ceq "Tooling-$target-$($version.ProductVersion)" -and $contract.profile -ceq $version.ProductVersion) 'Tooling retention must separate the actual module and exact Editor build, not a shared version prefix or generic Mono bucket.'
        Copy-Item -LiteralPath (Join-Path $record.path 'summary.json') -Destination (Join-Path $run.path ($case.module + '-summary.json'))
        if ($case.module -eq 'UnityCLI') { $nativeRecord = $record }
        $summary.checks++
        "PASS: $($case.label) real contracts and exact-build retention identity"
    }

    $discovery = [IO.File]::ReadAllText((Join-Path $nativeRecord.path 'cli-1.log')) | ConvertFrom-Json -DateKind String
    $baseline = [IO.File]::ReadAllText($discovery.selection) | ConvertFrom-Json -DateKind String
    $saved = Join-Path $run.path 'baseline-selection.json'
    Write-YLTestJson -Path $saved -Value $baseline
    $playAssembly = Join-Path $source 'Assets/Tests/YLFramework/PlayMode/YLFramework.Tests.PlayMode.asmdef'
    $originalAssembly = [IO.File]::ReadAllBytes($playAssembly)
    try {
        $assembly = [Text.Encoding]::UTF8.GetString($originalAssembly) | ConvertFrom-Json -AsHashtable
        $assembly['includePlatforms'] = @('Android')
        [IO.File]::WriteAllText($playAssembly, (ConvertTo-Json -InputObject $assembly -Depth 10), [Text.UTF8Encoding]::new($false))
        $missingMode = Invoke-FixtureEntry -Action Discover -ExpectedExit 1
        Assert-Safety ($missingMode.status -ceq 'Failed' -and $missingMode.reason -match 'execution modes') 'An entirely missing PlayMode group was not rejected by the real CLI.'
        $summary.checks++
        'PASS: excluding the entire PlayMode assembly cannot shrink a declared two-mode discovery into success'
    } finally { [IO.File]::WriteAllBytes($playAssembly, $originalAssembly) }
    $changedTest = Join-Path $source 'Assets/Tests/YLFramework/EditMode/Systems/Reddot/ReddotTests.cs'
    $originalTest = [IO.File]::ReadAllBytes($changedTest)
    $failure = @'

namespace YLFramework.Testing.Contracts
{
    [NUnit.Framework.TestFixture, NUnit.Framework.Category("YL.Module.Reddot")]
    public sealed class NativeFailureContract
    {
        [NUnit.Framework.Test]
        public void FailsWithoutHidingEditorExit()
        {
            NUnit.Framework.Assert.Fail("native-failure-marker");
        }
    }
}
'@
    try {
        [IO.File]::WriteAllText($changedTest, [Text.Encoding]::UTF8.GetString($originalTest) + $failure, [Text.UTF8Encoding]::new($false))
        $added = Invoke-FixtureEntry -Action Discover
        $selection = [IO.File]::ReadAllText($added.selection) | ConvertFrom-Json -DateKind String
        Assert-Safety ($selection.tests.Count -eq $baseline.tests.Count + 1) 'The added categorized real UTF leaf was not discovered.'
        $stale = Invoke-FixtureEntry -Action Run -Selection $saved -ExpectedExit 1
        Assert-Safety ($stale.status -ceq 'Failed' -and $stale.reason -match 'Stale test selection' -and $stale.xml.Count -eq 0) 'Source drift did not reject the old selection before test execution.'
        $summary.checks++
        'PASS: real UTF discovers an added leaf and refuses the stale source selection'

        $failed = Invoke-FixtureEntry -Action Run -ExpectedExit 1
        Assert-Safety ($failed.status -ceq 'Failed' -and $failed.xml.Count -eq 1 -and
            $failed.processes[-1].status -ceq 'NonZeroExit' -and $failed.processes[-1].exitCode -eq 1 -and
            -not $failed.processes[-1].forcedCleanup -and $failed.processes[-1].activeProcesses -eq 0) 'A real UTF failure did not retain an ordinary nonzero Editor exit and cleanup.'
        $xml = [xml][IO.File]::ReadAllText($failed.xml[0])
        Assert-Safety ($xml.DocumentElement.GetAttribute('result') -ceq 'Failed' -and $xml.OuterXml.Contains('native-failure-marker')) 'The real failing XML was lost or reported as passed.'
        Copy-Item -LiteralPath $failed.xml[0] -Destination (Join-Path $run.path 'expected-native-failure.xml')
        $summary.checks++
        'PASS: real failed XML and ordinary Editor exit1 remain failures, with no orphan'
    } finally {
        [IO.File]::WriteAllBytes($changedTest, $originalTest)
    }

    $hostRecord = Get-YLTestDirectory -Workspace $fixtureWorkspace -Path $discovery.host.path
    $hostLease = Lock-YLTestDirectory -Workspace $fixtureWorkspace -Path $hostRecord.path
    $cancel = [Threading.CancellationTokenSource]::new()
    try {
        Assert-YLUnityHostIdle $hostRecord
        $start = [Diagnostics.ProcessStartInfo]::new($Unity)
        $start.UseShellExecute = $false
        $start.WorkingDirectory = $hostRecord.path
        foreach ($argument in @('-batchmode', '-nographics', '-projectPath', $hostRecord.path,
            '-logFile', (Join-Path $run.path 'cancelled-unity.log'))) { $start.ArgumentList.Add($argument) }
        $cancel.CancelAfter(10000)
        $fact = Invoke-YLTestProcess -StartInfo $start -Log (Join-Path $run.path 'cancelled-process.log') -TimeoutMilliseconds 60000 -CancellationToken $cancel.Token
        $summary.processes += $fact
        Assert-Safety ($fact.status -ceq 'Cancelled' -and $fact.pid -gt 0 -and $fact.observedExit -and
            $fact.forcedCleanup -and $fact.activeProcesses -eq 0 -and -not $fact.cleanupError) 'Cancelling a real owned Editor did not retain cancellation and reclaim its descendants.'
        $summary.checks++
        'PASS: real native cancellation remains nonordinary and leaves no owned descendant'
    } finally {
        $cancel.Dispose()
        $hostLease.Dispose()
    }
    $recovered = Invoke-FixtureEntry -Action Run
    Assert-Safety ($recovered.status -ceq 'Passed' -and $recovered.host.id -ceq $discovery.host.id -and
        $recovered.testResult.total -eq $baseline.tests.Count) 'The same host did not recover after source rollback, test failure and native cancellation.'
    foreach ($xml in $recovered.xml) { Copy-Item -LiteralPath $xml -Destination (Join-Path $run.path ([IO.Path]::GetFileName($xml))) }
    $summary.checks++
    'PASS: the same owned host resumes the real EditMode/PlayMode module after failure and cancellation'
    $summary.status = 'Passed'
}
catch { $summary.reason = $_.Exception.Message; throw }
finally {
    if ($null -ne $originalTest -and $null -ne $changedTest) { [IO.File]::WriteAllBytes($changedTest, $originalTest) }
    $summary.endUtc = [DateTime]::UtcNow.ToString('o')
    try { Write-YLTestJson -Path (Join-Path $run.path 'summary.json') -Value $summary }
    finally { $lease.Dispose(); $fixtureLease.Dispose() }
    $null = Invoke-YLTestRotation -Workspace $workspace -Key $run.key
    "Unity safety contract evidence: $($run.path)"
}
