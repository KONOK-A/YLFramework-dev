#requires -Version 7.6
[CmdletBinding()]
param([string]$Root = 'C:\Users\Kano\AppData\Local\Temp\kilo\H26-20261021\Final', [string]$Receiver)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($Receiver -or [IO.Path]::GetFullPath($Root).StartsWith('G:\Unity\U63-20261027-c63a1', [StringComparison]::OrdinalIgnoreCase)) { throw 'Legacy-only final audit has fixed package/run names, not the new compatibility imports.' }
$receptionOwner = [IO.File]::ReadAllText((Join-Path $Root 'owner.json')) | ConvertFrom-Json -AsHashtable
if ($receptionOwner.ContainsKey('schemaVersion')) { throw 'Legacy-only final audit rejects versioned compatibility owners.' }
$owner = [IO.File]::ReadAllText((Join-Path $Root 'owner.json')) | ConvertFrom-Json
if ($owner.purpose -ne 'YLFramework-HybridCLR-9.0.3-independent-reception') { throw 'Wrong final import receiver.' }
$project = [IO.Path]::GetFullPath($owner.project)
foreach ($run in @('final-base-product-only','final-optional-product-only')) {
    $receipt = [IO.File]::ReadAllText((Join-Path $Root ($run + '.receipt.json'))) | ConvertFrom-Json
    if (-not $receipt.passed -or $receipt.testsPresent) { throw 'The final products did not pass clean pre-test first import.' }
}
$lock = [IO.File]::ReadAllText((Join-Path $project 'Packages/packages-lock.json')) | ConvertFrom-Json -AsHashtable
$package = $lock.dependencies['com.code-philosophy.hybridclr']
if ($package.source -ne 'git' -or $package.version -ne 'https://gitee.com/focus-creative-games/hybridclr_unity.git#v9.0.3' -or $package.hash -ne '6cadd24d101f4cbd00cd80190106f6621132d24d') { throw 'Wrong official package provenance.' }
if (@($lock.dependencies.Keys | Where-Object { -not $_.StartsWith('com.unity.modules.', [StringComparison]::Ordinal) -and
    $_ -cnotin @('com.unity.ugui','com.unity.nuget.newtonsoft-json','com.code-philosophy.hybridclr') }).Count) { throw 'Only the declared product and official SDK package closure belongs to final compilation proof.' }
foreach ($run in @('final-base-product-only','final-optional-product-only','final-resolve-official')) {
    $stream = [IO.File]::Open((Join-Path $Root ($run + '.log')), 'Open', 'Read', [IO.FileShare]::ReadWrite)
    $reader = [IO.StreamReader]::new($stream)
    try { $text = $reader.ReadToEnd() } finally { $reader.Dispose(); $stream.Dispose() }
    if ($text -match '(?im)^.*(?:error CS\d+|Scripts have compiler errors|Aborting batchmode due to failure).*$') { throw "Actual final compilation has errors: $run" }
}
$assemblies = @('YLFramework.Runtime','YLFramework.Editor','YLFramework.HybridCLR','YLFramework.HybridCLR.Editor','YLFramework.HybridCLR.Sample.Host','YLFramework.HybridCLR.Sample.Entry','YLFramework.HybridCLR.Sample.Support','YLFramework.HybridCLR.Sample.Editor') | ForEach-Object {
    $path = Join-Path $project ('Library/ScriptAssemblies/' + $_ + '.dll')
    if (-not [IO.File]::Exists($path)) { throw "The real optional consumer assembly was not compiled: $_" }
    @{ name=$_; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
}
if ([IO.Directory]::Exists((Join-Path $project 'Assets/YLFramework/Tests')) -or [IO.Directory]::Exists((Join-Path $project 'Assets/HCLRReceptionHarness'))) { throw 'Do not install test or acceptance consumers before this proof.' }
$output = Join-Path $Root 'final-supported-import.json'
if ([IO.File]::Exists($output)) { throw 'Preserve prior final import evidence.' }
[IO.File]::WriteAllText($output, (@{ passed=$true; sourceOnlyProducts=$true; packageClosure=@($lock.dependencies.Keys); officialHash=$package.hash; assemblies=$assemblies;
    defaultPackageSha256=(Get-FileHash -LiteralPath (Join-Path $Root 'YLFramework.unitypackage') -Algorithm SHA256).Hash;
    optionalPackageSha256=(Get-FileHash -LiteralPath (Join-Path $Root 'YLFramework-HybridCLR.unitypackage') -Algorithm SHA256).Hash; utc=[DateTime]::UtcNow.ToString('o') } | ConvertTo-Json -Depth 10))
"Final real product-only supported-package import passed; $($assemblies.Count) actual assemblies; receipt=$output"
