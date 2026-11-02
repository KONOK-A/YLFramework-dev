#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $WorkspaceRoot,
    [Parameter(Mandatory)][string] $Unity,
    [string] $Dotnet = (Get-Command dotnet -CommandType Application -TotalCount 1 -ErrorAction Stop).Source,
    [string] $NUnitAssembly = (Join-Path $env:USERPROFILE '.nuget/packages/nunit/3.14.0/lib/net45/nunit.framework.dll')
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Common/Process.ps1')
. (Join-Path $PSScriptRoot '../Common/Results.ps1')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$sdk = ([IO.File]::ReadAllText((Join-Path $repository 'Tools/YLFramework.Analyzers/global.json')) | ConvertFrom-Json).sdk.version
$compiler = Join-Path ([IO.Path]::GetDirectoryName($Dotnet)) "sdk/$sdk/Roslyn/bincore/csc.dll"
$data = Join-Path ([IO.Path]::GetDirectoryName($Unity)) 'Data'
$mono = Join-Path $data 'MonoBleedingEdge/bin/mono.exe'
$framework = Join-Path $data 'MonoBleedingEdge/lib/mono/4.8-api'
$references = @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'Facades/netstandard.dll') | ForEach-Object { Join-Path $framework $_ }
$references += $NUnitAssembly
foreach ($path in @($Unity, $Dotnet, $compiler, $mono) + $references) {
    if (-not [IO.File]::Exists($path)) { throw "Prerequisite not met: $path" }
}
$workspace = Initialize-YLTestWorkspace -WorkspaceRoot $WorkspaceRoot -Repository $repository
$profile = [Diagnostics.FileVersionInfo]::GetVersionInfo($Unity).ProductVersion
$run = New-YLTestDirectory -Workspace $workspace -Kind Result -Key "Tooling-NUnitHost-$profile"
$lease = Lock-YLTestDirectory -Workspace $workspace -Path $run.path
$summary = [ordered]@{
    schemaVersion = 1; ownerId = $run.id; action = 'Run'; suite = 'Tooling'; module = 'NUnitHost'; profile = $profile
    status = 'Failed'; endUtc = $null; reason = $null; processes = @(); checks = 0
}
$script:steps = 0

function Assert-Host([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }

function Invoke-HostProcess([string] $Exe, [string[]] $Arguments) {
    $script:steps++
    $start = [Diagnostics.ProcessStartInfo]::new($Exe)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $run.path
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $fact = Invoke-YLTestProcess -StartInfo $start -Log (Join-Path $run.path "process-$script:steps.log") -TimeoutMilliseconds 60000
    $summary.processes += $fact
    return $fact
}

function Build-Host([switch] $AddedCase) {
    $directory = Join-Path $run.path $(if ($AddedCase) { 'added' } else { 'original' })
    $null = [IO.Directory]::CreateDirectory($directory)
    $executable = Join-Path $directory 'NUnitHostContract.exe'
    $arguments = @('/nologo', '/nostdlib+', '/langversion:9.0', '/target:exe', "/out:$executable")
    if ($AddedCase) { $arguments += '/define:YL_TESTING_ADDED_CASE' }
    $arguments += $references | ForEach-Object { "/reference:$_" }
    $arguments += @((Join-Path $repository 'Tools/Testing/NUnitHost.cs'), (Join-Path $PSScriptRoot 'OfflineFixture.cs'))
    $response = Join-Path $directory 'compile.rsp'
    [IO.File]::WriteAllLines($response, [string[]]@($arguments | ForEach-Object { '"' + $_ + '"' }), [Text.UTF8Encoding]::new($false))
    $fact = Invoke-HostProcess -Exe $Dotnet -Arguments @($compiler, '/noconfig', ('@' + $response))
    Assert-YLTestProcessSucceeded $fact
    [IO.File]::Copy($NUnitAssembly, (Join-Path $directory 'nunit.framework.dll'))
    return $executable
}

function Discover-Host([string] $Executable, [string] $Category, [string] $Name) {
    $directory = Join-Path $run.path $Name
    $null = [IO.Directory]::CreateDirectory($directory)
    $fact = Invoke-HostProcess -Exe $mono -Arguments @($Executable, '--yl-discover', $Category, $directory)
    Assert-YLTestProcessSucceeded $fact
    $xml = [xml][IO.File]::ReadAllText((Join-Path $directory 'discovery.xml'))
    $leaves = @($xml.SelectNodes('//test-case'))
    Assert-Host ($leaves.Count -gt 0) 'A real nonempty NUnit discovery tree is required.'
    $tests = @($leaves | ForEach-Object {
        [pscustomobject]@{ assembly = 'NUnitHostContract'; fullName = $_.GetAttribute('fullname'); mode = 'Offline'; runState = $_.GetAttribute('runstate') }
    })
    $identity = Get-YLTestInputIdentity -Repository $repository -Paths @('Tools/Testing/NUnitHost.cs', 'Tools/Testing/Tests/OfflineFixture.cs') -Toolchain @($Executable, $NUnitAssembly, $compiler) -Profile 'Mono-NUnit3.14'
    return New-YLTestSelection -InputIdentity $identity -Tests $tests
}

function Run-Host([string] $Executable, [object] $Selection, [string] $Name) {
    $directory = Join-Path $run.path $Name
    $null = [IO.Directory]::CreateDirectory($directory)
    $filter = Join-Path $directory 'filter.xml'
    $settings = [Xml.XmlWriterSettings]::new()
    $settings.OmitXmlDeclaration = $true
    $writer = [Xml.XmlWriter]::Create($filter, $settings)
    try {
        $writer.WriteStartElement('filter')
        $writer.WriteStartElement('or')
        foreach ($test in $Selection.tests) { $writer.WriteElementString('test', $test.fullName) }
        $writer.WriteEndElement()
        $writer.WriteEndElement()
    } finally { $writer.Dispose() }
    $fact = Invoke-HostProcess -Exe $mono -Arguments @($Executable, '--yl-run', $filter, $directory)
    return [pscustomobject]@{ process = $fact; directory = $directory }
}

try {
    $executable = Build-Host
    $selection = Discover-Host $executable 'YL.Testing.Contract' 'discover-original'
    Assert-Host ($selection.tests.Count -eq 2) 'Category discovery must expand parameterized cases and exclude other categories.'
    Assert-Host (-not [IO.File]::Exists((Join-Path $run.path 'discover-original/lifecycle.txt'))) 'Discovery must not execute fixture setup or cleanup.'
    Write-YLTestJson -Path (Join-Path $run.path 'selection.json') -Value $selection
    $execution = Run-Host $executable $selection 'execute-original'
    $result = Read-YLTestXml -Path (Join-Path $execution.directory 'results.xml') -Selection $selection -InputIdentity $selection.inputIdentity -Mode Offline -ProcessResult $execution.process
    Assert-Host ($result.total -eq 2 -and [IO.File]::ReadAllText((Join-Path $execution.directory 'lifecycle.txt')) -ceq '2/2') 'Real fixture setup/testcase/teardown did not close.'
    $summary.checks++
    'PASS: real category discovery and NUnit fixture lifecycle'

    $legacy = Invoke-HostProcess -Exe $mono -Arguments @($executable, 'YLFramework.Testing.Contracts.OfflineFixture')
    $legacyDirectory = [IO.Path]::GetDirectoryName($executable)
    $legacyResult = Read-YLTestXml -Path (Join-Path $legacyDirectory 'results.xml') -Selection $selection -InputIdentity $selection.inputIdentity -Mode Offline -ProcessResult $legacy
    Assert-Host ($legacyResult.total -eq 2) 'Existing class-filter callers must remain runnable until their migration completes.'
    $summary.checks++
    'PASS: existing class-filter callers retain real NUnit execution semantics'

    $added = Build-Host -AddedCase
    $newSelection = Discover-Host $added 'YL.Testing.Contract' 'discover-added'
    Assert-Host ($newSelection.tests.Count -eq 3) 'New categorized tests must enter discovery without a leaf-name table.'
    $rejected = $false
    try { Assert-YLTestSelection -Selection $selection -InputIdentity $newSelection.inputIdentity }
    catch { if ($_.Exception.Message -notmatch 'Stale test selection') { throw }; $rejected = $true }
    Assert-Host $rejected 'Rebuilt inputs must invalidate the old selection.'
    $execution = Run-Host $added $newSelection 'execute-added'
    $result = Read-YLTestXml -Path (Join-Path $execution.directory 'results.xml') -Selection $newSelection -InputIdentity $newSelection.inputIdentity -Mode Offline -ProcessResult $execution.process
    Assert-Host ($result.total -eq 3 -and [IO.File]::ReadAllText((Join-Path $execution.directory 'lifecycle.txt')) -ceq '3/3') 'New test lifecycle did not close.'
    $summary.checks++
    'PASS: added categorized case and stale selection rejection'

    $failure = Discover-Host $executable 'YL.Testing.TeardownFailure' 'discover-failure'
    $execution = Run-Host $executable $failure 'execute-failure'
    Assert-Host ($execution.process.status -ceq 'NonZeroExit' -and $execution.process.exitCode -eq 1) 'A OneTimeTearDown failure must give a real nonzero exit.'
    $xml = [xml][IO.File]::ReadAllText((Join-Path $execution.directory 'results.xml'))
    Assert-Host ($xml.DocumentElement.GetAttribute('result') -ceq 'Failed' -and $xml.OuterXml.Contains('teardown-marker')) 'The real cleanup failure XML was lost.'
    $summary.checks++
    'PASS: suite cleanup failure retains failed XML and real nonzero exit'
    $summary.status = 'Passed'
}
catch { $summary.reason = $_.Exception.Message; throw }
finally {
    $summary.endUtc = [DateTime]::UtcNow.ToString('o')
    try { Write-YLTestJson -Path (Join-Path $run.path 'summary.json') -Value $summary }
    finally { $lease.Dispose() }
    $null = Invoke-YLTestRotation -Workspace $workspace -Key $run.key
    "NUnit host contract evidence: $($run.path)"
}
