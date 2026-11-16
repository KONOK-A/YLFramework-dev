#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$entries = @('Tools/Testing/Tests/Test-YLIntegrationPackages.ps1',
    'Tools/Testing/Tests/Test-YLDevelopmentHost.ps1', 'Tools/Packaging/Build-YLRegistryPackages.ps1')
$cases = 0
foreach ($relative in $entries) {
    $path = Join-Path $repository $relative
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
    if ($errors.Count -ne 0) { throw "PowerShell parse failed: $relative" }
    if ($ast.ScriptRequirements.RequiredPSVersion -lt [version]'7.6' -or
        'Core' -cnotin $ast.ScriptRequirements.RequiredPSEditions) { throw "Missing minimum/Core prerequisites: $relative" }
    $guards = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.IfStatementAst] -and
        @($node.Clauses | Where-Object { $_.Item1.Extent.Text.Contains('$PSVersionTable.PSVersion') }).Count -ne 0
    }, $true))
    if ($guards.Count -ne 1) { throw "Expected one explicit runtime gate: $relative" }
    $condition = $guards[0].Clauses[0].Item1.Extent.Text.Replace('$PSVersionTable.PSVersion', '$candidate').Replace('$IsWindows', '$windows')
    # 只参数化仓库中实际谓词，不伪造当前宿主或产物回执里的真实工具链版本。
    $predicate = [scriptblock]::Create('param([version]$candidate, [bool]$windows) ' + $condition)
    foreach ($case in @(
        @{version='7.6.0'; rejected=$false}, @{version='7.6.9'; rejected=$false},
        @{version='7.7.0'; rejected=$false}, @{version='8.0.0'; rejected=$false},
        @{version='7.5.99'; rejected=$true}, @{version='5.1.0'; rejected=$true}
    )) {
        $rejected = [bool](& $predicate ([version]$case.version) $true)
        if ($rejected -ne $case.rejected) { throw "$relative rejects/accepts $($case.version) contrary to the 7.6+ minimum policy." }
        $cases++
    }
    if (-not (& $predicate ([version]'7.7.0') $false)) { throw "Windows boundary was removed: $relative" }
    $cases++
}
"PASS: $cases PowerShell 7.6+ runtime policy cases. Actual host: $($PSVersionTable.PSVersion). Not Unity acceptance."

$workflow = [IO.File]::ReadAllText((Join-Path $repository '.github/workflows/dotnet.yml'))
$match = [regex]::Match($workflow, '(?ms)^\s*\$version = \$PSVersionTable\.PSVersion\r?\n(?<guard>\s*if\s*\(.*?\)\s*\{\s*throw\s+[^\r\n]+\r?\n\s*\})')
if (-not $match.Success) { throw 'The CI runtime guard could not be verified.' }
$ciAst = [Management.Automation.Language.Parser]::ParseInput($match.Groups['guard'].Value, [ref]$tokens, [ref]$errors)
$ciGuard = @($ciAst.FindAll({ param($node) $node -is [Management.Automation.Language.IfStatementAst] }, $true))[0]
$ciCondition = $ciGuard.Clauses[0].Item1.Extent.Text.Replace('$version', '$candidate').Replace('$IsWindows', '$windows')
$ciPredicate = [scriptblock]::Create('param([version]$candidate, [bool]$windows) ' + $ciCondition)
foreach ($version in @('7.6.0', '7.7.0', '8.0.0')) {
    if (& $ciPredicate ([version]$version) $true) { throw "CI rejects supported PowerShell $version." }
}
foreach ($version in @('7.5.99', '5.1.0')) {
    if (-not (& $ciPredicate ([version]$version) $true)) { throw "CI accepts unsupported PowerShell $version." }
}
if (-not (& $ciPredicate ([version]'7.7.0') $false)) { throw 'CI Windows requirement was removed.' }
'PASS: CI uses the same PowerShell minimum while preserving Windows/Core requirements.'
