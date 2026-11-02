#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $RiderRoot,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9-]*$')][string] $Run
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or $PSVersionTable.PSVersion.Major -ne 7 -or $PSVersionTable.PSVersion.Minor -ne 6) {
    throw 'Windows PowerShell 7.6.x is required.'
}
$inputRoot = [IO.Path]::GetFullPath($RiderRoot).TrimEnd('\')
$inputRun = $Run
if (-not (Test-Path -LiteralPath $inputRoot -PathType Container)) { throw 'Evidence root does not exist.' }
for ($ancestor = $inputRoot; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
    if (([IO.File]::GetAttributes($ancestor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Linked evidence roots are not supported.'
    }
}
$destination = Join-Path $inputRoot ($inputRun + '.json')
$stream = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
$results = [Collections.Generic.List[object]]::new()
$failure = $null
$unchanged = $false
$sources = @()
$receipt = [ordered]@{
    Run = $inputRun; Kind = 'OwnedRiderEvidenceGuards'; GuardsPassed = $false
    FullRiderAcceptance = $false; NativePragmaVerified = $false; NativeUiVerified = $false
    RestartVerified = $false; GoalPassed = $false; InputEvidenceUnchanged = $false
    McpRequestsSent = 0; IdeStateChanged = $false
}
Write-Output ('Run=' + $inputRun + '; Receipt=' + $destination)
try {
    $checker = Join-Path $PSScriptRoot 'Test-OwnedRiderEvidence.ps1'
    if (-not (Test-Path -LiteralPath $checker -PathType Leaf)) {
        throw 'Rider evidence [checker-missing]: Test-OwnedRiderEvidence.ps1 is not available.'
    }
    . $checker
    $original = Read-OwnedRiderEvidenceBundle -RiderRoot $inputRoot
    $sources = @($original.Sources)
    $fixture = ConvertTo-Json -InputObject $original -Depth 100 -Compress
    $tests = @(
        @{ Name = 'saved-good-batch'; Code = ''; Edit = {} },
        @{ Name = 'optional-flags-missing'; Code = ''; Edit = {
            param($b)
            foreach ($r in $b.Records.Values) {
                $null = $r.Response.result.Remove('isError')
            }
        } },
        @{ Name = 'optional-flags-false-or-null'; Code = ''; Edit = {
            param($b)
            $p = $b.Records['custom-baseline-confirmed.json'].Response.result.structuredContent
            $p.timedOut = $null
            $p = $b.Records['both-all-fixed-lint.json'].Response.result.structuredContent
            $p.more = $false; $p.items[0].timedOut = $false; $p.items[1].notAnalyzedReason = $null
        } },
        @{ Name = 'canonical-windows-probe-paths'; Code = ''; Edit = {
            param($b)
            $p = $b.Records['custom-baseline-confirmed.json'].Response.result.structuredContent
            $p.filePath = $b.Ready.Project.ToUpperInvariant() + '/Assets/AnalyzerConsumers/../AnalyzerConsumers/IdeDiagnosticProbe.cs'
            $b.Records['both-all-fixed-lint.json'].Response.result.structuredContent.items[1].filePath = 'Assets/./IdeDefaultProbe.cs'
        } },
        @{ Name = 'extra-ide-hint-retained'; Code = ''; Edit = {
            param($b)
            $p = $b.Records['both-all-fixed-lint.json'].Response.result.structuredContent.items[0]
            $p.problems += @{ severity = 'HINT'; description = 'Additional IDE hint'; lineText = 'public sealed class IdeDiagnosticProbe'; line = 6; column = 1 }
        } },
        @{ Name = 'zero-yl-is-an-empty-family-array-not-null'; Code = ''; Edit = {} },
        @{ Name = 'top-level-timeout'; Code = 'incomplete'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Response.result.structuredContent.timedOut = $true
        } },
        @{ Name = 'batch-more'; Code = 'incomplete'; Edit = {
            param($b) $b.Records['both-all-fixed-lint.json'].Response.result.structuredContent.more = $true
        } },
        @{ Name = 'file-timeout'; Code = 'incomplete'; Edit = {
            param($b) $b.Records['both-all-fixed-lint.json'].Response.result.structuredContent.items[0].timedOut = $true
        } },
        @{ Name = 'file-not-analyzed'; Code = 'incomplete'; Edit = {
            param($b) $b.Records['both-all-fixed-lint.json'].Response.result.structuredContent.items[1].notAnalyzedReason = 'not in a project'
        } },
        @{ Name = 'tool-is-error'; Code = 'incomplete'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Response.result.isError = $true
        } },
        @{ Name = 'timeout-string-is-not-false'; Code = 'schema'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Response.result.structuredContent.timedOut = 'false'
        } },
        @{ Name = 'more-null-is-not-boolean'; Code = 'schema'; Edit = {
            param($b) $b.Records['both-all-fixed-lint.json'].Response.result.structuredContent.more = $null
        } },
        @{ Name = 'reason-object-is-not-empty'; Code = 'schema'; Edit = {
            param($b) $b.Records['both-all-fixed-lint.json'].Response.result.structuredContent.items[0].notAnalyzedReason = @{}
        } },
        @{ Name = 'is-error-string-is-not-false'; Code = 'schema'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Response.result.isError = 'false'
        } },
        @{ Name = 'response-rpc-error'; Code = 'rpc-error'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Response.error = @{ code = -1; message = 'failed' }
        } },
        @{ Name = 'initialization-rpc-error'; Code = 'rpc-error'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Initialize.error = @{ code = -1; message = 'failed' }
        } },
        @{ Name = 'recorded-capture-failure'; Code = 'capture-failure'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Failure = 'wrapper failed'
        } },
        @{ Name = 'missing-required-case'; Code = 'schema'; Edit = {
            param($b) $null = $b.Records.Remove('default-inject-fixed-problems.json')
        } },
        @{ Name = 'missing-structured-content-no-text-fallback'; Code = 'schema'; Edit = {
            param($b) $null = $b.Records['custom-baseline-confirmed.json'].Response.result.Remove('structuredContent')
        } },
        @{ Name = 'missing-errors-array'; Code = 'schema'; Edit = {
            param($b) $null = $b.Records['custom-baseline-confirmed.json'].Response.result.structuredContent.Remove('errors')
        } },
        @{ Name = 'null-errors-is-not-empty-array'; Code = 'schema'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Response.result.structuredContent.errors = $null
        } },
        @{ Name = 'missing-problems-on-zero-yl-file'; Code = 'schema'; Edit = {
            param($b) $null = $b.Records['both-all-fixed-lint.json'].Response.result.structuredContent.items[0].Remove('problems')
        } },
        @{ Name = 'wrong-probe-directory-same-basename'; Code = 'file-set'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Response.result.structuredContent.filePath = 'Assets/Other/IdeDiagnosticProbe.cs'
        } },
        @{ Name = 'probe-outside-project'; Code = 'file-set'; Edit = {
            param($b) $b.Records['both-all-fixed-lint.json'].Response.result.structuredContent.items[0].filePath = '../IdeDiagnosticProbe.cs'
        } },
        @{ Name = 'record-wrong-project'; Code = 'ownership'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Project += '-other'
        } },
        @{ Name = 'argument-wrong-root'; Code = 'ownership'; Edit = {
            param($b) $b.Records['both-all-fixed-lint.json'].Arguments.rootFolder += '-other'
        } },
        @{ Name = 'record-wrong-pid'; Code = 'ownership'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Pid++
        } },
        @{ Name = 'record-wrong-start-time'; Code = 'ownership'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].StartUtc = '2026-10-27T20:54:22.3669123Z'
        } },
        @{ Name = 'record-ownership-not-verified'; Code = 'ownership'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].ProcessOwnershipVerified = $false
        } },
        @{ Name = 'different-record-port'; Code = 'ownership'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Port++
        } },
        @{ Name = 'wrong-action'; Code = 'schema'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Action = 'open'
        } },
        @{ Name = 'action-array-is-not-string'; Code = 'schema'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Action = @('problems')
        } },
        @{ Name = 'tool-array-is-not-string'; Code = 'schema'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Tool = @('get_file_problems')
        } },
        @{ Name = 'jsonrpc-array-is-not-string'; Code = 'schema'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Response.jsonrpc = @('2.0')
        } },
        @{ Name = 'server-name-array-is-not-string'; Code = 'schema'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Initialize.result.serverInfo.name = @('JetBrains Rider MCP Server')
        } },
        @{ Name = 'server-version-array-is-not-string'; Code = 'schema'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Initialize.result.serverInfo.version = @('2026.2.3.1')
        } },
        @{ Name = 'hidden-warning-filter'; Code = 'schema'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Arguments.errorsOnly = $true
        } },
        @{ Name = 'lint-error-only-filter'; Code = 'schema'; Edit = {
            param($b) $b.Records['both-all-fixed-lint.json'].Arguments.min_severity = 'error'
        } },
        @{ Name = 'wrong-warning-severity'; Code = 'severity'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Response.result.structuredContent.errors[1].severity = 'ERROR'
        } },
        @{ Name = 'missing-error-policy-severity'; Code = 'severity'; Edit = {
            param($b) $b.Records['custom-error-default-warning-lint.json'].Response.result.structuredContent.items[0].problems[1].severity = 'WARNING'
        } },
        @{ Name = 'control-severity-contaminated'; Code = 'severity'; Edit = {
            param($b) $b.Records['default-error-custom-warning-lint.json'].Response.result.structuredContent.items[0].problems[1].severity = 'ERROR'
        } },
        @{ Name = 'missing-independent-control-file'; Code = 'file-set'; Edit = {
            param($b) $p = $b.Records['custom-positive-default-control-lint.json'].Response.result.structuredContent; $p.items = @($p.items[0])
        } },
        @{ Name = 'missing-requested-control-file'; Code = 'file-set'; Edit = {
            param($b) $b.Records['custom-positive-default-control-lint.json'].Arguments.files = @('Assets/AnalyzerConsumers/IdeDiagnosticProbe.cs')
        } },
        @{ Name = 'duplicate-control-file'; Code = 'file-set'; Edit = {
            param($b) $p = $b.Records['both-all-fixed-lint.json'].Response.result.structuredContent; $p.items[1] = $p.items[0]
        } },
        @{ Name = 'unowned-extra-file'; Code = 'file-set'; Edit = {
            param($b) $b.Records['both-all-fixed-lint.json'].Response.result.structuredContent.items += @{ filePath = 'Assets/Other.cs'; problems = @() }
        } },
        @{ Name = 'false-empty-baseline'; Code = 'families'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Response.result.structuredContent.errors = @()
        } },
        @{ Name = 'false-empty-zero-yl-loses-setter-warning'; Code = 'ide-evidence'; Edit = {
            param($b) $b.Records['both-all-fixed-lint.json'].Response.result.structuredContent.items[0].problems = @()
        } },
        @{ Name = 'missing-control-family'; Code = 'families'; Edit = {
            param($b) $p = $b.Records['custom-positive-default-control-lint.json'].Response.result.structuredContent.items[1]; $p.problems = @($p.problems[0], $p.problems[1], $p.problems[2])
        } },
        @{ Name = 'duplicate-family'; Code = 'families'; Edit = {
            param($b) $p = $b.Records['custom-baseline-confirmed.json'].Response.result.structuredContent; $p.errors += $p.errors[1]
        } },
        @{ Name = 'generic-dte-message-is-not-rider-family'; Code = 'families'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Response.result.structuredContent.errors[1].description = 'Inject does not support static fields'
        } },
        @{ Name = 'unknown-error-in-zero-yl-case'; Code = 'unexpected-error'; Edit = {
            param($b) $b.Records['both-all-fixed-lint.json'].Response.result.structuredContent.items[0].problems += @{ severity = 'ERROR'; description = 'Unrelated analysis failure'; lineText = 'broken'; line = 1; column = 1 }
        } },
        @{ Name = 'syntax-error-in-zero-yl-case'; Code = 'unexpected-error'; Edit = {
            param($b) $b.Records['custom-all-fixed-problems.json'].Response.result.structuredContent.errors += @{ severity = 'ERROR'; description = 'CS1002: ; expected'; lineContent = 'broken'; line = 1; column = 1 }
        } },
        @{ Name = 'positive-but-wrong-family-line'; Code = 'location'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Response.result.structuredContent.errors[1].line = 7
        } },
        @{ Name = 'positive-but-wrong-family-column'; Code = 'location'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Response.result.structuredContent.errors[1].column = 1
        } },
        @{ Name = 'family-source-line-replaced'; Code = 'location'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Response.result.structuredContent.errors[1].lineContent = '    [Inject] public object Dependency { get; private set; }'
        } },
        @{ Name = 'family-moved-to-valid-control-location'; Code = 'location'; Edit = {
            param($b)
            $p = $b.Records['custom-baseline-confirmed.json'].Response.result.structuredContent
            $p.errors[1].line = 9; $p.errors[1].column = 38; $p.errors[1].lineContent = $p.errors[2].lineContent
        } },
        @{ Name = 'stale-baseline-line-under-pragma'; Code = 'location'; Edit = {
            param($b) $b.Records['custom-error-pragma-problems.json'].Response.result.structuredContent.errors[1].line = 8
        } },
        @{ Name = 'inject-description-names-other-probe'; Code = 'location'; Edit = {
            param($b)
            $p = $b.Records['custom-baseline-confirmed.json'].Response.result.structuredContent.errors[1]
            $p.description = $p.description.Replace('IdeDiagnosticProbe.Bad', 'IdeDefaultProbe.Bad')
        } },
        @{ Name = 'setter-warning-wrong-line'; Code = 'location'; Edit = {
            param($b) $b.Records['both-all-fixed-lint.json'].Response.result.structuredContent.items[0].problems[0].line = 9
        } },
        @{ Name = 'setter-warning-wrong-column'; Code = 'location'; Edit = {
            param($b) $b.Records['both-all-fixed-lint.json'].Response.result.structuredContent.items[0].problems[0].column = 1
        } },
        @{ Name = 'using-warning-wrong-line'; Code = 'location'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Response.result.structuredContent.errors[0].line = 3
        } },
        @{ Name = 'diagnostic-beyond-probe-end'; Code = 'location'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Response.result.structuredContent.errors[1].line = 999
        } },
        @{ Name = 'diagnostic-column-beyond-source-line'; Code = 'location'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Response.result.structuredContent.errors[0].column = 999
        } },
        @{ Name = 'extra-ide-hint-wrong-source-line'; Code = 'location'; Edit = {
            param($b)
            $p = $b.Records['both-all-fixed-lint.json'].Response.result.structuredContent.items[0]
            $p.problems += @{ severity = 'HINT'; description = 'Additional IDE hint'; lineText = 'unrelated source'; line = 6; column = 1 }
        } },
        @{ Name = 'nonpositive-line'; Code = 'location'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Response.result.structuredContent.errors[1].line = 0
        } },
        @{ Name = 'nonpositive-column'; Code = 'location'; Edit = {
            param($b) $b.Records['both-all-fixed-lint.json'].Response.result.structuredContent.items[0].problems[0].column = -1
        } },
        @{ Name = 'noninteger-line'; Code = 'schema'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].Response.result.structuredContent.errors[1].line = '8'
        } },
        @{ Name = 'missing-line-content'; Code = 'schema'; Edit = {
            param($b) $null = $b.Records['custom-baseline-confirmed.json'].Response.result.structuredContent.errors[1].Remove('lineContent')
        } },
        @{ Name = 'missing-line-text'; Code = 'schema'; Edit = {
            param($b) $null = $b.Records['both-all-fixed-lint.json'].Response.result.structuredContent.items[0].problems[0].Remove('lineText')
        } },
        @{ Name = 'wrong-chronology'; Code = 'chronology'; Edit = {
            param($b) $b.Records['custom-inject-fixed-problems.json'].CapturedUtc = $b.Records['custom-baseline-confirmed.json'].CapturedUtc
        } },
        @{ Name = 'capture-before-process-start'; Code = 'chronology'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'].CapturedUtc = '2026-10-27T20:00:00Z'
        } },
        @{ Name = 'missing-discovered-tool'; Code = 'schema'; Edit = {
            param($b) $p = $b.Records['tools-confirmed.json'].Response.result; $p.tools = @($p.tools | Where-Object { $_.name -cne 'lint_files' })
        } },
        @{ Name = 'missing-discovered-schema'; Code = 'schema'; Edit = {
            param($b) $t = @($b.Records['tools-confirmed.json'].Response.result.tools | Where-Object { $_.name -ceq 'get_file_problems' })[0]; $null = $t.Remove('outputSchema')
        } },
        @{ Name = 'missing-runtime-project'; Code = 'project-graph'; Edit = {
            param($b) $p = $b.Records['projects-initial.json'].Response.result.structuredContent; $p.modules = @($p.modules | Where-Object { $_.name -cne 'YLFramework.Runtime' })
        } },
        @{ Name = 'wrong-project-list-shape'; Code = 'schema'; Edit = {
            param($b) $b.Records['projects-initial.json'].Response.result.structuredContent.modules = @{}
        } },
        @{ Name = 'failed-baseline-wrapper-not-success'; Code = 'capture-failure'; Edit = {
            param($b) $b.Records['custom-baseline-confirmed.json'] = $b.Records['custom-baseline-problems.json']
        } },
        @{ Name = 'failed-list-wrapper-not-success'; Code = 'capture-failure'; Edit = {
            param($b) $b.Records['tools-confirmed.json'] = $b.Records['tools-initial.json']
        } },
        @{ Name = 'pragma-error-is-not-zero-yl'; Code = 'families'; Edit = {
            param($b) $b.Records['custom-all-fixed-problems.json'] = $b.Records['custom-error-pragma-problems.json']; $b.Records['custom-all-fixed-problems.json'].CapturedUtc = '2026-10-27T21:06:49.1211307Z'
        } },
        @{ Name = 'baseline-source-hash-changed'; Code = 'baseline'; Edit = {
            param($b) $b.CurrentInputs[0].Sha256 = ('0' * 64)
        } },
        @{ Name = 'baseline-meta-hash-changed'; Code = 'baseline'; Edit = {
            param($b) $b.CurrentInputs[1].MetaSha256 = ('0' * 64)
        } },
        @{ Name = 'baseline-guid-changed'; Code = 'baseline'; Edit = {
            param($b) $b.CurrentInputs[2].Guid = '00000000000000000000000000000000'
        } },
        @{ Name = 'solution-hash-changed'; Code = 'baseline'; Edit = {
            param($b) $b.SolutionSha256 = ('0' * 64)
        } },
        @{ Name = 'package-owner-binding-changed'; Code = 'ownership'; Edit = {
            param($b) $b.Ready.PackageSha256 = ('0' * 64)
        } }
    )
    foreach ($test in $tests) {
        $value = ConvertFrom-Json -InputObject $fixture -AsHashtable -DateKind String
        $null = & $test.Edit $value
        $caught = $null
        $summary = $null
        try { $summary = Assert-OwnedRiderEvidenceBundle -Bundle $value -RiderRoot $inputRoot }
        catch { $caught = $_.Exception.Message }
        $passed = if ($test.Code.Length) {
            $null -ne $caught -and $caught.StartsWith(('Rider evidence [' + $test.Code + ']: '), [StringComparison]::Ordinal)
        } else {
            $null -eq $caught -and $null -ne $summary -and $summary.ProtocolSubsetPassed -eq $true -and
                $summary.FullRiderAcceptance -eq $false -and $summary.NativePragmaVerified -eq $false -and
                $summary.RestartVerified -eq $false
        }
        if ($passed -and $test.Name -ceq 'extra-ide-hint-retained') {
            $fixed = @($summary.Cases | Where-Object { $_.Name -ceq 'both-all-fixed-lint.json' })[0]
            $hints = @($fixed.Files[0].RawDiagnostics | Where-Object { $_.description -ceq 'Additional IDE hint' })
            $passed = $hints.Count -eq 1 -and $fixed.Files[0].OtherDiagnostics.Count -eq 2
        }
        if ($passed -and $test.Name -ceq 'zero-yl-is-an-empty-family-array-not-null') {
            $fixed = @($summary.Cases | Where-Object { $_.Name -ceq 'both-all-fixed-lint.json' })[0]
            $passed = $fixed.Files[0].ExpectedFamilies -is [array] -and $fixed.Files[0].ExpectedFamilies.Count -eq 0 -and
                $fixed.Files[0].FamilyAssociations.Count -eq 0 -and $fixed.Files[0].RawDiagnostics.Count -eq 1
        }
        $results.Add(@{ Name = $test.Name; Passed = $passed; ExpectedRejection = $test.Code; ActualFailure = $caught })
    }
    $tokens=$null; $parseErrors=$null
    $launchAst=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Start-OwnedRider.ps1'),[ref]$tokens,[ref]$parseErrors)
    if ($parseErrors.Count) { throw 'Rider launch syntax is invalid.' }
    $launchPolicy=@($launchAst.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Get-OwnedRiderLaunchHashes'
    },$true))
    $policyFixture=@{ PolicyWritten=$true; Project=$original.Owner.Project; Unity=$original.Owner.Unity; PackageSha256=$original.Owner.PackageSha256
        OwnerSha256=('a'*64); RuleSet='Assets/AnalyzerConsumers/Analyzer.Acceptance.Consumer.ruleset'; Rule='YL0008'; Action='Error'
        BeforeSha256=$original.Owner.Inputs[3].Sha256; AfterSha256=('b'*64); MetaSha256=$original.Owner.Inputs[3].MetaSha256 }
    foreach ($test in @(
        @{ Name='launch-original-baseline'; Reject=$false; Policy=$false; Edit={} },
        @{ Name='launch-exact-owned-error'; Reject=$false; Policy=$true; Edit={} },
        @{ Name='launch-other-owner'; Reject=$true; Policy=$true; Edit={ param($p) $p.OwnerSha256='c'*64 } },
        @{ Name='launch-other-project'; Reject=$true; Policy=$true; Edit={ param($p) $p.Project+='-other' } },
        @{ Name='launch-other-unity'; Reject=$true; Policy=$true; Edit={ param($p) $p.Unity='2022.3.62f3c1' } },
        @{ Name='launch-other-package'; Reject=$true; Policy=$true; Edit={ param($p) $p.PackageSha256='c'*64 } },
        @{ Name='launch-wrong-before'; Reject=$true; Policy=$true; Edit={ param($p) $p.BeforeSha256='c'*64 } },
        @{ Name='launch-wrong-meta'; Reject=$true; Policy=$true; Edit={ param($p) $p.MetaSha256='c'*64 } },
        @{ Name='launch-source-not-policy'; Reject=$true; Policy=$true; Edit={ param($p) $p.RuleSet='Assets/AnalyzerConsumers/IdeDiagnosticProbe.cs' } },
        @{ Name='launch-unapproved-rule'; Reject=$true; Policy=$true; Edit={ param($p) $p.Rule='YL0009' } },
        @{ Name='launch-unapproved-action'; Reject=$true; Policy=$true; Edit={ param($p) $p.Action='None' } },
        @{ Name='launch-invalid-after'; Reject=$true; Policy=$true; Edit={ param($p) $p.AfterSha256='not-a-hash' } },
        @{ Name='launch-unwritten-policy'; Reject=$true; Policy=$true; Edit={ param($p) $p.PolicyWritten=$false } }
    )) {
        $caught=$null; $expectedRejection=$false
        try {
            if ($launchPolicy.Count -ne 1) { throw 'Exact immutable error-policy launch guard is missing.' }
            $policy=ConvertFrom-Json ($policyFixture | ConvertTo-Json) -AsHashtable
            & $test.Edit $policy
            $hashes=& {
                . ([scriptblock]::Create($launchPolicy[0].Extent.Text))
                Get-OwnedRiderLaunchHashes $original.Owner $(if ($test.Policy) { $policy } else { $null }) ('a'*64)
            }
            foreach ($input in $original.Owner.Inputs) {
                $expected=if ($test.Policy -and $input.Path -ceq $policy.RuleSet) { $policy.AfterSha256 } else { $input.Sha256 }
                if ($hashes[$input.Path] -cne $expected) { throw 'Launch input hash set drifted.' }
            }
        } catch { $caught=$_.Exception.Message; $expectedRejection=$caught.StartsWith('Rider launch policy:',[StringComparison]::Ordinal) }
        $passed=if ($test.Reject) { $expectedRejection } else { $null -eq $caught }
        $results.Add(@{ Name=$test.Name; Passed=$passed; ExpectedRejection=$test.Reject; ActualFailure=$caught })
    }
    if (@($results | Where-Object { -not $_.Passed }).Count -ne 0) { throw 'Owned Rider evidence mutation guards failed.' }
    $receipt.GuardsPassed = $true
} catch {
    $failure = $_.Exception.Message
} finally {
    try {
        if ($sources.Count) {
            $unchanged = $true
            foreach ($source in $sources) {
                if ((Get-FileHash -LiteralPath $source.Path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $source.Sha256) {
                    $unchanged = $false
                    throw ('Saved input changed during guards: ' + $source.Path)
                }
            }
        }
    } catch { $failure = $_.Exception.Message; $receipt.GuardsPassed = $false }
    $receipt.InputEvidenceUnchanged = $unchanged
    $receipt.Sources = $sources
    $receipt.Cases = $results.ToArray()
    $receipt.Failure = $failure
    $receipt.CapturedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
    try { $writer.Write((ConvertTo-Json -InputObject $receipt -Depth 20)) }
    finally { $writer.Dispose(); $stream.Dispose() }
}
$results | Select-Object -Property Name, Passed, ExpectedRejection, ActualFailure | Format-Table -Wrap
[pscustomobject]@{
    GuardsPassed = $receipt.GuardsPassed; Cases = $results.Count; InputEvidenceUnchanged = $unchanged
    Receipt = $destination; Sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    FullRiderAcceptance = $false
}
if ($null -ne $failure) { throw $failure }
