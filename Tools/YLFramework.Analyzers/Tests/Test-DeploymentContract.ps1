#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param([string] $TemporaryRoot = [IO.Path]::GetTempPath())
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$entry = Join-Path $PSScriptRoot '../build.ps1'
$ast = [Management.Automation.Language.Parser]::ParseFile($entry, [ref]$null, [ref]$null)
foreach ($name in @('Assert-AnalyzerMeta', 'Assert-AnalyzerDeployment')) {
    $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name }, $true)
    if ($null -eq $function) { throw "The maintained build entry does not enforce $name." }
    . ([scriptblock]::Create($function.Extent.Text))
}
if (-not [IO.Directory]::Exists($TemporaryRoot)) { throw 'An existing temporary parent is required.' }
$root = Join-Path $TemporaryRoot ('yl-analyzer-deployment-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($root)
$output = Join-Path $root 'Temp/YLFramework.Analyzers'
$built = Join-Path $output 'YLFramework.Analyzers/bin/Release/netstandard2.0/YLFramework.Analyzers.dll'
$deployed = Join-Path $root 'PackageSources/com.kano.ylframework/Runtime/Core/Analyzers/YLFramework.Analyzers.dll'
$meta = "fileFormatVersion: 2`nguid: 471f81c4273ee66429677df44cbd6960`nlabels:`n- RoslynAnalyzer`nPluginImporter:`n  isExplicitlyReferenced: 1`n  platformData:`n  - first:`n      Any: `n    second:`n      enabled: 0`n"
$checks = 0
function Reject([scriptblock] $Action) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (-not $rejected) { throw 'A stale or invalid deployed Analyzer was accepted.' }
    $script:checks++
}
try {
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($built))
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($deployed))
    [IO.File]::WriteAllBytes($built, [byte[]]@(77,90,1,2,3))
    [IO.File]::Copy($built, $deployed)
    [IO.File]::WriteAllText($deployed + '.meta', $meta, [Text.UTF8Encoding]::new($false))
    Assert-AnalyzerDeployment -Built $built -Destination $deployed; $checks++
    foreach ($fault in @('stale', 'missing-dll', 'missing-meta', 'guid', 'label', 'reference', 'platform')) {
        [IO.File]::Copy($built, $deployed, $true)
        [IO.File]::WriteAllText($deployed + '.meta', $meta, [Text.UTF8Encoding]::new($false))
        switch ($fault) {
            'stale' { [IO.File]::WriteAllBytes($deployed, [byte[]]@(77,90,4,5,6)) }
            'missing-dll' { [IO.File]::Delete($deployed) }
            'missing-meta' { [IO.File]::Delete($deployed + '.meta') }
            'guid' { [IO.File]::WriteAllText($deployed + '.meta', $meta.Replace('471f81c4273ee66429677df44cbd6960', '00000000000000000000000000000000')) }
            'label' { [IO.File]::WriteAllText($deployed + '.meta', $meta.Replace('RoslynAnalyzer', 'roslynanalyzer')) }
            'reference' { [IO.File]::WriteAllText($deployed + '.meta', $meta.Replace('isExplicitlyReferenced: 1', 'isExplicitlyReferenced: 0')) }
            'platform' { [IO.File]::WriteAllText($deployed + '.meta', $meta.Replace('enabled: 0', 'enabled: 1')) }
        }
        $paths = @($built, $deployed, ($deployed + '.meta'))
        $before = @($paths | ForEach-Object { if ([IO.File]::Exists($_)) { (Get-FileHash -LiteralPath $_).Hash } else { 'missing' } })
        Reject { Assert-AnalyzerDeployment -Built $built -Destination $deployed }
        $after = @($paths | ForEach-Object { if ([IO.File]::Exists($_)) { (Get-FileHash -LiteralPath $_).Hash } else { 'missing' } })
        if (($before -join ',') -cne ($after -join ',')) { throw 'Read-only deployment verification modified its inputs.' }
        $checks++
    }
    "PASS: $checks Analyzer deployment identity and read-only contracts."

    # Run the maintained entry in a private tree; only the external SDK command is replaced.
    $tools = Join-Path $root 'Tools/YLFramework.Analyzers'
    $catalog = 'PackageSources/com.kano.ylframework/Editor/Core/Analyzers/AnalyzerRuleCatalog.cs'
    $null = [IO.Directory]::CreateDirectory((Join-Path $root 'Tools'))
    Copy-Item -LiteralPath ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))) -Destination $tools -Recurse
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName((Join-Path $root $catalog)))
    [IO.File]::Copy((Join-Path $PSScriptRoot "../../../$catalog"), (Join-Path $root $catalog))
    [IO.File]::WriteAllText($deployed + '.meta', $meta)
    $fixtureDirectory = Join-Path $output 'TestResults'
    $fixtureTrx = Join-Path $fixtureDirectory 'analyzers.trx'
    $receipt = Join-Path $output 'build-info.json'
    $null = [IO.Directory]::CreateDirectory($fixtureDirectory)

    $result = '<UnitTestResult executionId="00000000-0000-0000-0000-000000000001" testId="00000000-0000-0000-0000-000000000002" testName="Example" outcome="Passed" />'
    $results = "<Results>$result</Results>"
    $summary = '<ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" notExecuted="0" aborted="0" /></ResultSummary>'
    $valid = '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">' + $results + $summary + '</TestRun>'
    $reports = [ordered]@{
        'zero matches' = $valid.Replace($results, '').Replace('="1"', '="0"')
        'stale' = $null
        'missing' = $null
        'malformed' = '<TestRun>'
        'wrong namespace' = $valid.Replace('http://microsoft.com/schemas/VisualStudio/TeamTest/2010', 'urn:wrong')
        'summary only' = $valid.Replace($results, '')
        'empty results' = $valid.Replace($result, '').Replace('="1"', '="0"')
        'missing counter' = $valid.Replace(' notExecuted="0"', '')
        'non-integer counter' = $valid.Replace('executed="1"', 'executed="1.0"')
        'incorrect total' = $valid.Replace('total="1"', 'total="2"')
        'incorrect executed count' = $valid.Replace('executed="1"', 'executed="2"')
        'incorrect passed count' = $valid.Replace('passed="1"', 'passed="2"')
        'failed counter' = $valid.Replace('failed="0"', 'failed="1"')
        'skipped counter' = $valid.Replace('notExecuted="0"', 'notExecuted="1"')
        'aborted counter' = $valid.Replace('aborted="0"', 'aborted="1"')
        'aborted run' = $valid.Replace('outcome="Completed"', 'outcome="Aborted"')
        'failed case' = $valid.Replace('outcome="Passed"', 'outcome="Failed"')
        'skipped case' = $valid.Replace('outcome="Passed"', 'outcome="NotExecuted"')
        'duplicate case' = $valid.Replace('="1"', '="2"').Replace($result, $result + $result)
        'valid' = $valid
    }
    function dotnet {
        $global:LASTEXITCODE = 0
        switch -CaseSensitive ($args[0]) {
            '--version' { '9.0.312' }
            'restore' { }
            'build' { }
            'test' {
                $index = [Array]::IndexOf($args, '--results-directory')
                if ($index -lt 0 -or $args[$index + 1] -cne $fixtureDirectory -or $args -cnotcontains 'trx;LogFileName=analyzers.trx') {
                    throw 'Unexpected analyzer test logger or output directory.'
                }
                if ($null -ne $report.Value) { [IO.File]::WriteAllText($fixtureTrx, $report.Value) }
            }
            default { throw "Unexpected dotnet command: $($args[0])" }
        }
    }
    foreach ($report in $reports.GetEnumerator()) {
        [IO.File]::Delete($receipt)
        [IO.File]::Delete($fixtureTrx)
        if ($report.Key -ceq 'stale') { [IO.File]::WriteAllText($fixtureTrx, $valid) }
        $failure = $null
        try { $null = & (Join-Path $tools 'build.ps1') -VerifyDeployment }
        catch { $failure = $_.Exception.Message }
        if ($report.Key -ceq 'valid') {
            if ($null -ne $failure) { throw "Valid current TRX was rejected: $failure" }
            if (-not [IO.File]::Exists($receipt) -or [IO.File]::ReadAllText($fixtureTrx) -cne $valid) {
                throw 'Successful verification did not preserve the expected receipt and TRX artifacts.'
            }
        } else {
            if ($null -eq $failure) { throw "The build entry accepted '$($report.Key)' TRX evidence after dotnet test exited 0." }
            if ($failure -cnotlike 'Analyzer TRX *') { throw "Unexpected rejection of '$($report.Key)': $failure" }
            if ([IO.File]::Exists($receipt)) { throw "The build entry wrote a receipt for '$($report.Key)' TRX evidence." }
        }
    }
    "PASS: $($reports.Count) Analyzer TRX entry contracts."
} finally { [IO.Directory]::Delete($root, $true) }
