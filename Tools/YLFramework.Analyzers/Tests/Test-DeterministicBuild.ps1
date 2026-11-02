#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param([string] $TemporaryRoot = [IO.Path]::GetTempPath())
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
. (Join-Path $repository 'Tools/Testing/Common/Process.ps1')
. (Join-Path $repository 'Tools/Testing/Common/Results.ps1')
$workspace = Initialize-YLTestWorkspace -WorkspaceRoot (Join-Path (Get-YLTestPath $TemporaryRoot) ('yl-analyzer-repro-' + [Guid]::NewGuid().ToString('N'))) -Repository $repository
$result = New-YLTestDirectory -Workspace $workspace -Kind Result -Key DeterministicCompiler
$paths = @('Tools/YLFramework.Analyzers/global.json', 'Tools/YLFramework.Analyzers/Directory.Build.props',
    'Tools/YLFramework.Analyzers/Analyzer/YLFramework.Analyzers.csproj', 'Tools/YLFramework.Analyzers/Analyzer/packages.lock.json',
    'PackageSources/com.kano.ylframework/Editor/Core/Analyzers/AnalyzerRuleCatalog.cs')
$paths += @([IO.Directory]::EnumerateFiles((Join-Path $repository 'Tools/YLFramework.Analyzers/Analyzer'), '*.cs', [IO.SearchOption]::AllDirectories) | ForEach-Object { [IO.Path]::GetRelativePath($repository, $_).Replace('\', '/') })
$hashes = @()
foreach ($case in @('First', 'Different root with spaces')) {
    $owned = New-YLTestDirectory -Workspace $workspace -Kind Cache -Key ($case.Replace(' ', '-'))
    $snapshot = Join-Path $owned.path $case
    foreach ($relative in $paths) {
        $target = Join-Path $snapshot $relative
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        [IO.File]::Copy((Join-Path $repository $relative), $target)
    }
    $project = Join-Path $snapshot 'Tools/YLFramework.Analyzers/Analyzer/YLFramework.Analyzers.csproj'
    $working = [IO.Path]::GetDirectoryName($project)
    $revision = if ($case -ceq 'First') { 'a' * 40 } else { 'b' * 40 }
    foreach ($operation in @('restore', 'build')) {
        $start = [Diagnostics.ProcessStartInfo]::new((Get-Command dotnet -CommandType Application).Source)
        $start.UseShellExecute = $false; $start.WorkingDirectory = $working
        foreach ($arg in @($operation, $project)) { $start.ArgumentList.Add($arg) }
        $arguments = if ($operation -ceq 'restore') { @('--locked-mode') } else { @('-c', 'Release', '--no-restore', '-p:RepositoryUrl=https://example.invalid/commit-independent.git', ('-p:SourceRevisionId=' + $revision)) }
        foreach ($arg in $arguments) { $start.ArgumentList.Add($arg) }
        $fact = Invoke-YLTestProcess -StartInfo $start -Log (Join-Path $owned.path ($operation + '.log')) -TimeoutMilliseconds 180000
        Assert-YLTestProcessSucceeded $fact
    }
    $dll = Join-Path $snapshot 'Temp/YLFramework.Analyzers/YLFramework.Analyzers/bin/Release/netstandard2.0/YLFramework.Analyzers.dll'
    $hashes += @{ root = $snapshot; revisionInput = $revision; sha256 = (Get-FileHash -LiteralPath $dll).Hash }
}
$deployed = Join-Path $repository 'PackageSources/com.kano.ylframework/Runtime/Core/Analyzers/YLFramework.Analyzers.dll'
$deployedHash = (Get-FileHash -LiteralPath $deployed).Hash
Write-YLTestJson -Path (Join-Path $result.path 'builds.json') -Value @{ builds = $hashes; deployedHash = $deployedHash }
if ($hashes[0].sha256 -cne $hashes[1].sha256) { throw 'Identical Analyzer sources produced different DLLs for different roots/revision metadata.' }
if ($hashes[0].sha256 -cne $deployedHash) { throw 'Deployed Analyzer is not the root/revision-independent compiler output.' }
"PASS: two actual pinned SDK builds at distinct roots/revision inputs reproduce the deployed Analyzer: $deployedHash"
"Evidence: $($result.path)"
