[CmdletBinding()]
param([switch] $Deploy, [switch] $VerifyDeployment)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($Deploy -and $VerifyDeployment) { throw 'Choose deployment or read-only deployment verification, not both.' }

function Invoke-DotNet {
    param([string[]] $Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE): $($Arguments -join ' ')" }
}

function Assert-AnalyzerTestResults {
    param([string] $Path)
    if (-not [IO.File]::Exists($Path)) { throw "Analyzer TRX '$Path' was not produced by this test run." }
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $document = [Xml.XmlDocument]::new()
    $reader = $null
    try {
        $reader = [Xml.XmlReader]::Create($Path, $settings)
        $document.Load($reader)
    } catch { throw "Analyzer TRX '$Path' is not readable XML: $($_.Exception.Message)" }
    finally { if ($null -ne $reader) { $reader.Dispose() } }

    $ns = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $ns.AddNamespace('trx', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    $summaries = $document.SelectNodes('/trx:TestRun/trx:ResultSummary', $ns)
    if ($summaries.Count -ne 1 -or $summaries[0].GetAttribute('outcome') -cne 'Completed') {
        throw "Analyzer TRX '$Path' must contain one completed TestRun summary."
    }
    $results = $document.SelectNodes('/trx:TestRun/trx:Results', $ns)
    if ($results.Count -ne 1) { throw "Analyzer TRX '$Path' must contain one Results section with executed test cases." }
    $cases = $results[0].SelectNodes('trx:UnitTestResult', $ns)
    if ($cases.Count -eq 0 -or $cases.Count -ne $results[0].SelectNodes('*').Count -or
        $cases.Count -ne $document.SelectNodes('//trx:UnitTestResult', $ns).Count) {
        throw "Analyzer TRX '$Path' must contain a nonempty, flat set of UnitTestResult records."
    }
    $executions = [Collections.Generic.HashSet[guid]]::new()
    foreach ($case in $cases) {
        $name = $case.GetAttribute('testName')
        $outcome = $case.GetAttribute('outcome')
        if ($outcome -cne 'Passed') { throw "Analyzer TRX '$Path' contains a non-passing test: '$name' ($outcome)." }
        $id = [guid]::Empty
        if ([string]::IsNullOrWhiteSpace($name) -or -not [guid]::TryParse($case.GetAttribute('executionId'), [ref]$id) -or
            $id -eq [guid]::Empty -or -not $executions.Add($id)) {
            throw "Analyzer TRX '$Path' contains a missing, invalid, or duplicate test execution identity: '$name'."
        }
    }

    $counters = $summaries[0].SelectNodes('trx:Counters', $ns)
    if ($counters.Count -ne 1) { throw "Analyzer TRX '$Path' must contain one Counters element." }
    foreach ($required in @('total', 'executed', 'passed', 'failed', 'notExecuted')) {
        if (-not $counters[0].HasAttribute($required)) { throw "Analyzer TRX '$Path' is missing the '$required' counter." }
    }
    foreach ($counter in $counters[0].SelectNodes('@*')) {
        $expected = if ($counter.Name -cin @('total', 'executed', 'passed')) { $cases.Count } else { 0 }
        $value = 0
        if (-not [int]::TryParse($counter.Value, [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture, [ref]$value) -or $value -ne $expected) {
            throw "Analyzer TRX '$Path' counter $($counter.Name)='$($counter.Value)' must equal $expected."
        }
    }
}

function Assert-AnalyzerMeta {
    param([string] $Path)
    if (-not [IO.File]::Exists($Path)) { throw 'Analyzer meta is missing; refusing to generate a new identity.' }
    $text = [IO.File]::ReadAllText($Path)
    $guids = [regex]::Matches($text, '(?m)^guid:\s*(\S+)\s*$')
    if ($guids.Count -ne 1 -or $guids[0].Groups[1].Value -cne '471f81c4273ee66429677df44cbd6960') {
        throw 'Analyzer GUID differs from the preserved asset identity.'
    }
    if ([regex]::Matches($text, '(?m)^PluginImporter:\s*$').Count -ne 1 -or
        [regex]::Matches($text, '(?m)^- RoslynAnalyzer\s*$').Count -ne 1) {
        throw 'Analyzer must use PluginImporter and exactly one case-sensitive RoslynAnalyzer label.'
    }
    $reference = [regex]::Matches($text, '(?m)^\s+isExplicitlyReferenced:\s*(\S+)\s*$')
    if ($reference.Count -ne 1 -or $reference[0].Groups[1].Value -ne '1') {
        throw 'Analyzer automatic Runtime reference must remain disabled (isExplicitlyReferenced: 1).'
    }
    $platforms = [regex]::Matches($text, '(?m)^\s+enabled:\s*(\S+)\s*$')
    if ($platforms.Count -eq 0 -or @($platforms | Where-Object { $_.Groups[1].Value -ne '0' }).Count -ne 0) {
        throw 'Analyzer platform flags must all remain disabled.'
    }
}

function Assert-AnalyzerDeployment {
    param([string] $Built, [string] $Destination)
    Assert-AnalyzerMeta -Path ($Destination + '.meta')
    if (-not [IO.File]::Exists($Built) -or -not [IO.File]::Exists($Destination)) { throw 'The verified build or deployed Analyzer DLL is missing.' }
    $builtHash = (Get-FileHash -LiteralPath $Built -Algorithm SHA256).Hash
    $deployedHash = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash
    if ($builtHash -cne $deployedHash) {
        throw "Deployed Analyzer is stale or changed; run the verified build with -Deploy before source delivery. Built=$builtHash ($([IO.FileInfo]::new($Built).Length) bytes); deployed=$deployedHash ($([IO.FileInfo]::new($Destination).Length) bytes)."
    }
}

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$tests = Join-Path $PSScriptRoot 'Tests/YLFramework.Analyzers.Tests.csproj'
$output = Join-Path $root 'Temp/YLFramework.Analyzers'
$dll = Join-Path $output 'YLFramework.Analyzers/bin/Release/netstandard2.0/YLFramework.Analyzers.dll'
$destination = Join-Path $root 'PackageSources/com.kano.ylframework/Runtime/Core/Analyzers/YLFramework.Analyzers.dll'
if (-not (Test-Path -LiteralPath (Join-Path $root 'Temp'))) { throw 'Repository Temp parent is missing.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
try { $entryLock = [IO.File]::Open((Join-Path $output 'entry.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
catch [IO.IOException] { throw 'Another analyzer build/deploy entry is running; refusing shared output access.' }
$before = if (Test-Path -LiteralPath $destination) { (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash } else { $null }

Push-Location -LiteralPath $PSScriptRoot
try {
    if ($Deploy -or $VerifyDeployment) { Assert-AnalyzerMeta -Path ($destination + '.meta') }
    $expectedSdk = ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'global.json')) | ConvertFrom-Json).sdk.version
    $sdk = & dotnet --version
    if ($LASTEXITCODE -ne 0 -or $sdk -ne $expectedSdk) { throw "Expected SDK $expectedSdk; actual: $sdk" }
    "PowerShell=$($PSVersionTable.PSVersion) SDK=$sdk"
    Invoke-DotNet -Arguments @('restore', $tests, '--locked-mode')
    Invoke-DotNet -Arguments @('build', $tests, '-c', 'Release', '--no-restore')
    $resultsDirectory = Join-Path $output 'TestResults'
    $trx = Join-Path $resultsDirectory 'analyzers.trx'
    $null = [IO.Directory]::CreateDirectory($resultsDirectory)
    # The entry lock covers both stale-report removal and verification of the new report.
    [IO.File]::Delete($trx)
    Invoke-DotNet -Arguments @('test', $tests, '-c', 'Release', '--no-build', '--no-restore', '--results-directory', $resultsDirectory, '--logger', 'trx;LogFileName=analyzers.trx', '--logger', 'console;verbosity=minimal')
    Assert-AnalyzerTestResults -Path $trx

    $inputs = @('global.json', 'Directory.Build.props', 'build.ps1', 'Analyzer/YLFramework.Analyzers.csproj', 'Analyzer/packages.lock.json',
        '../../PackageSources/com.kano.ylframework/Editor/Core/Analyzers/AnalyzerRuleCatalog.cs')
    $inputs += @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Analyzer') -Filter '*.cs' -File -Recurse |
        ForEach-Object { $_.FullName.Substring($PSScriptRoot.Length + 1).Replace('\', '/') })
    $files = @($inputs | Sort-Object | ForEach-Object {
        [ordered]@{ Path = $_; Sha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $_) -Algorithm SHA256).Hash }
    })
    $receipt = [ordered]@{
        Sdk = $sdk
        Version = '1.0.0'
        Inputs = $files
        DllSha256 = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
    } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText((Join-Path $output 'build-info.json'), $receipt, [Text.UTF8Encoding]::new($false))

    if ($Deploy) {
        Assert-AnalyzerMeta -Path ($destination + '.meta')
        # 合作入口串行；指纹检查拒绝已观察到的外部改动，不能替代任意外部写者的 compare-and-swap。
        $current = if (Test-Path -LiteralPath $destination) { (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash } else { $null }
        if ($current -ne $before) { throw 'Analyzer destination changed during verification; deployment refused.' }
        $parent = [IO.Path]::GetDirectoryName($destination)
        if (-not (Test-Path -LiteralPath $parent)) { [IO.Directory]::CreateDirectory($parent) | Out-Null }
        if ($current -ne (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash) {
            $stage = Join-Path $output ('YLFramework.Analyzers.' + [Guid]::NewGuid().ToString('N') + '.tmp')
            try {
                [IO.File]::Copy($dll, $stage, $false)
                $stagedCurrent = if (Test-Path -LiteralPath $destination) { (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash } else { $null }
                if ($stagedCurrent -ne $before) { throw 'Analyzer destination changed during staging; deployment refused.' }
                if ($null -eq $current) { [IO.File]::Move($stage, $destination) }
                else { [IO.File]::Replace($stage, $destination, [NullString]::Value) }
            }
            finally { if ([IO.File]::Exists($stage)) { [IO.File]::Delete($stage) } }
        }
        "Deployed=$destination"
    }
    if ($VerifyDeployment) {
        Assert-AnalyzerDeployment -Built $dll -Destination $destination
        'PASS: deployed Analyzer exactly matches this verified deterministic source build; no package files were written.'
    }
    "DLL=$dll"
    "SHA256=$((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash)"
    "BuildInfo=$(Join-Path $output 'build-info.json')"
}
finally { Pop-Location; $entryLock.Dispose() }
