#requires -Version 7.6
[CmdletBinding()]
param([Parameter(Mandatory)][string]$TempRoot)
Set-StrictMode -Version 3.0
$ErrorActionPreference='Stop'
if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($TempRoot)) -or [IO.Path]::Exists($TempRoot)) { throw 'Fresh input-binding guard root required.' }
[IO.Directory]::CreateDirectory($TempRoot) | Out-Null
$entry=Join-Path $PSScriptRoot 'Invoke-Stage9Delivery.ps1'
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($entry,[ref]$tokens,[ref]$errors)
$names=@('Hash','Reject-Link','Get-Stage9TestInputFiles','Assert-Stage9TestBinding')
$missing=@()
foreach ($name in $names) {
    $method=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$true))
    if ($method.Count -ne 1) { $missing+=$name } else { . ([scriptblock]::Create($method[0].Extent.Text)) }
}
$cases=[Collections.Generic.List[object]]::new()
foreach ($spec in @(
    @{ Name='current-exact-input'; Reject=$false; Edit={} },
    @{ Name='foreign-owner'; Reject=$true; Edit={param($d,$p,$r) $d.OwnerSha256='b'*64} },
    @{ Name='foreign-package'; Reject=$true; Edit={param($d,$p,$r) $d.PackageSha256='b'*64} },
    @{ Name='new-source-after-discovery'; Reject=$true; Edit={param($d,$p,$r) [IO.File]::WriteAllText((Join-Path $r 'Assets/Tests/New.cs'),'class NewTest {}')} },
    @{ Name='changed-source-after-discovery'; Reject=$true; Edit={param($d,$p,$r) [IO.File]::AppendAllText((Join-Path $r 'Assets/Tests/Leaf.cs'),'// changed')} },
    @{ Name='changed-asmdef-after-discovery'; Reject=$true; Edit={param($d,$p,$r) [IO.File]::AppendAllText((Join-Path $r 'Assets/Tests/Tests.asmdef'),' ')} },
    @{ Name='changed-meta-after-discovery'; Reject=$true; Edit={param($d,$p,$r) [IO.File]::AppendAllText((Join-Path $r 'Assets/Tests/Leaf.cs.meta'),' ')} },
    @{ Name='changed-player-settings'; Reject=$true; Edit={param($d,$p,$r) [IO.File]::AppendAllText((Join-Path $r 'ProjectSettings/ProjectSettings.asset'),' ')} },
    @{ Name='stale-classification-profile'; Reject=$true; Edit={param($d,$p,$r) $p.ClassifierSha256='b'*64} },
    @{ Name='wrong-input-profile'; Reject=$true; Edit={param($d,$p,$r) $p.InputMode=1} },
    @{ Name='unbound-old-discovery'; Reject=$true; Edit={param($d,$p,$r) $d.Remove('OwnerSha256')} }
)) {
    $failure=$null
    try {
        if ($missing.Count) { throw 'Input-binding implementation missing: '+($missing -join ',') }
        $project=Join-Path $TempRoot $spec.Name
        foreach ($directory in @('Assets/Tests','Packages','ProjectSettings')) { [IO.Directory]::CreateDirectory((Join-Path $project $directory)) | Out-Null }
        foreach ($pair in @(@('stage9-owner.json','{}'),@('Assets/Tests/Leaf.cs','class Leaf {}'),@('Assets/Tests/Leaf.cs.meta','guid: 11111111111111111111111111111111'),@('Assets/Tests/Tests.asmdef','{"name":"Tests"}'),@('Packages/manifest.json','{}'),@('Packages/packages-lock.json','{}'),@('ProjectSettings/ProjectSettings.asset','api: 3'),@('ProjectSettings/EditorSettings.asset','reload: true'))) {
            [IO.File]::WriteAllText((Join-Path $project $pair[0]),$pair[1],[Text.UTF8Encoding]::new($false))
        }
        $discovery=@{ Schema='Stage9TestDiscovery-v2'; Passed=$true; SourceHost=$false; Project=$project; Unity='6000.3.27f1'; OwnerSha256=(Hash (Join-Path $project 'stage9-owner.json'))
            PackageSha256=('a'*64); InputFiles=@(Get-Stage9TestInputFiles $project); CompilationSha256=('c'*64); ClassifierSha256=('d'*64); InputMode=0; EnterPlayModeOptionsEnabled=$false; EnterPlayModeOptions='None' }
        $profile=@{ Schema='Stage9TestProfile-v2'; Project=$project; Unity='6000.3.27f1'; OwnerSha256=$discovery.OwnerSha256; PackageSha256=$discovery.PackageSha256
            ClassifierSha256=$discovery.ClassifierSha256; InputMode=0; EnterPlayModeOptionsEnabled=$false; EnterPlayModeOptions='None' }
        & $spec.Edit $discovery $profile $project
        Assert-Stage9TestBinding $discovery $profile $project '6000.3.27f1' ('a'*64)
    } catch { $failure=$_.Exception.Message }
    $passed=if ($spec.Reject) { $null -ne $failure -and $failure.StartsWith('Test binding:',[StringComparison]::Ordinal) } else { $null -eq $failure }
    $cases.Add(@{ Name=$spec.Name; Passed=$passed; ExpectedRejection=$spec.Reject; Failure=$failure })
}
$result=@{ Passed=@($cases | Where-Object { -not $_.Passed }).Count -eq 0; Cases=$cases.ToArray(); UnityExecuted=$false }
[IO.File]::WriteAllText((Join-Path $TempRoot 'results.json'),($result | ConvertTo-Json -Depth 7),[Text.UTF8Encoding]::new($false))
if (-not $result.Passed) { throw "Input-binding guards failed; $TempRoot/results.json" }
"Passed $($cases.Count) Stage9 actual input-binding guards; $TempRoot/results.json"
