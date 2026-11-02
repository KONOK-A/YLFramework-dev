#requires -Version 7.6
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $AnalyzerRoot,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9-]+$')][string] $Run,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9-]+$')][string] $GenerationRun,
    [ValidatePattern('^[A-Za-z0-9-]+$')][string] $PolicyRun
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$root=[IO.Path]::GetFullPath($AnalyzerRoot)
for ($path=$root; $path; $path=[IO.Path]::GetDirectoryName($path)) {
    if (Test-Path -LiteralPath $path) {
        if (([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked Analyzer root.' }
    }
}
$owner=Get-Content -LiteralPath (Join-Path $root 'owner.json') -Raw | ConvertFrom-Json
$targets=Get-Content -LiteralPath (Join-Path $root 'ide-targets.json') -Raw | ConvertFrom-Json
$generation=Get-Content -LiteralPath (Join-Path $root ($GenerationRun+'.json')) -Raw | ConvertFrom-Json
if ($owner.Purpose -cne 'Stage9-owned-analyzer-inputs' -or $targets.Project -cne $owner.Project -or
    $generation.Project -cne $owner.Project -or -not $generation.Passed -or -not $generation.DefaultEditorUnchanged -or $generation.IdeLaunched) { throw 'Exact generated Analyzer ownership is required.' }
$policy=$null; $policySha=$null
if ($PolicyRun) {
    $policyPath=Join-Path $root ($PolicyRun+'.json')
    $policy=Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
    $policySha=(Get-FileHash -LiteralPath $policyPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $policy.PolicyWritten -or $policy.Project -cne $owner.Project -or $policy.PackageSha256 -cne $owner.PackageSha256 -or
        $policy.OwnerSha256 -cne (Get-FileHash -LiteralPath (Join-Path $root 'owner.json') -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Policy experiment is not bound to this immutable Analyzer owner.' }
}
foreach ($input in $owner.Inputs) {
    $expected=$input.Sha256
    if ($null -ne $policy -and $input.Path -ceq $policy.RuleSet) {
        if ($policy.BeforeSha256 -cne $input.Sha256) { throw 'Policy source snapshot differs from its owner.' }
        $expected=$policy.AfterSha256
    }
    $path=Join-Path $owner.Project $input.Path
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expected -or
        (Get-FileHash -LiteralPath ($path+'.meta') -Algorithm SHA256).Hash.ToLowerInvariant() -cne $input.MetaSha256) { throw 'Analyzer input differs from the exact baseline or selected owned policy experiment.' }
}
foreach ($file in @(@{Path=$generation.Solution; Sha256=$generation.SolutionSha256}) + @($generation.Projects)) {
    if ((Get-FileHash -LiteralPath $file.Path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.Sha256) { throw 'Generated solution/project changed before IDE use.' }
}
$template=Join-Path $PSScriptRoot 'OwnedVsDteHost.cs.txt'
if (-not (Test-Path -LiteralPath $template -PathType Leaf)) { throw 'The owned VS host source is not available.' }
$destination=Join-Path $root ('VS-'+$Run)
if (Test-Path -LiteralPath $destination) { throw 'Never adopt or overwrite a VS host run.' }
$ide=[IO.Path]::GetDirectoryName($targets.VisualStudioExecutable)
$installation=[IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($ide))
$compiler=Join-Path $installation 'MSBuild/Current/Bin/Roslyn/csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'Installed VS Roslyn compiler is required.' }
$null=[IO.Directory]::CreateDirectory($destination)
$dependencies=@(foreach ($name in @('Microsoft.VisualStudio.Interop.dll','envdte.dll','envdte80.dll')) {
    $source=Join-Path $ide ('PublicAssemblies/'+$name)
    $target=Join-Path $destination $name
    [IO.File]::Copy($source,$target)
    @{Name=$name; Sha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()}
})
$sourcePath=Join-Path $destination 'OwnedVsDteHost.cs'
[IO.File]::Copy($template,$sourcePath)
$exe=Join-Path $destination 'OwnedVsDteHost.exe'
$arguments=@('/nologo','/target:exe','/platform:x64','/langversion:9.0',('/out:'+$exe),'/reference:System.dll','/reference:System.Core.dll',
    '/reference:System.Web.Extensions.dll','/reference:System.Windows.Forms.dll',('/reference:'+(Join-Path $destination 'Microsoft.VisualStudio.Interop.dll')),
    ('/reference:'+(Join-Path $destination 'envdte.dll')),('/reference:'+(Join-Path $destination 'envdte80.dll')),$sourcePath)
$compilerOutput=@(& $compiler @arguments 2>&1)
$compilerExit=$LASTEXITCODE
[IO.File]::WriteAllText((Join-Path $destination 'compile.log'),($compilerOutput -join [Environment]::NewLine))
$compilerOutput
if ($compilerExit -ne 0) { throw 'Owned VS host compilation failed.' }
$selfTest=& $exe --self-test
if ($LASTEXITCODE -ne 0) { throw 'Owned VS process guard self-test failed.' }
$selfTest | ConvertFrom-Json | Out-Null
$configuration=@{
    Purpose='Stage9-owned-vs-host'; Root=$destination; AnalyzerRoot=$root; Project=$owner.Project; Unity=$owner.Unity; PackageSha256=$owner.PackageSha256
    Solution=$generation.Solution; SolutionSha256=$generation.SolutionSha256; VsExecutable=$targets.VisualStudioExecutable
    Pipe=('YLAnalyzerVS-'+[Guid]::NewGuid().ToString('N')); Nonce=[Guid]::NewGuid().ToString('N'); LifetimeSeconds=1800
    SourceSha256=(Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
    ExeSha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant(); Dependencies=$dependencies
    CreatedUtc=[DateTime]::UtcNow.ToString('O'); ProcessGuardSelfTest=($selfTest | ConvertFrom-Json)
    PolicyExperiment=$PolicyRun; PolicySha256=$policySha
}
[IO.File]::WriteAllText((Join-Path $destination 'host.json'),($configuration | ConvertTo-Json -Depth 8))
[pscustomobject]@{ Prepared=$true; Root=$destination; Host=$exe; Config=(Join-Path $destination 'host.json'); IdeLaunched=$false }
