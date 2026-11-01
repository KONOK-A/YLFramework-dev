#requires -Version 7.6
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $DeliveryRoot,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9-]+$')][string] $Run,
    [Parameter(Mandatory)][string] $Package,
    [Parameter(Mandatory)][string] $Unity2022,
    [Parameter(Mandatory)][string] $Unity6000
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($DeliveryRoot)
for ($cursor = $root; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
    if (([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked test root.' }
}
$owner = Get-Content -LiteralPath (Join-Path $root 'owner.json') -Raw | ConvertFrom-Json
if ($owner.purpose -cne 'YLFramework-Stage9-independent-delivery' -or $owner.root -cne $root) { throw 'Owned delivery required.' }
$cases = Join-Path $root ('GuardTests-' + $Run)
if (Test-Path -LiteralPath $cases) { throw 'Never overwrite a previous guard run.' }
$null = [IO.Directory]::CreateDirectory($cases)
$entry = Join-Path $PSScriptRoot 'Invoke-Stage9Delivery.ps1'
$results = [Collections.Generic.List[object]]::new()
function New-Case([string] $Name) {
    $path = Join-Path $cases $Name
    $null = [IO.Directory]::CreateDirectory($path)
    $identity = @{ purpose='YLFramework-Stage9-independent-delivery'; root=$path; repository=$owner.repository }
    [IO.File]::WriteAllText((Join-Path $path 'owner.json'), ($identity | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    return $path
}
function Expect-Rejected([string] $Name, [hashtable] $Arguments, [string] $Reason) {
    $failure = $null
    try { & $entry @Arguments | Out-Null } catch { $failure = $_.Exception.Message }
    $passed = $null -ne $failure -and $failure -match $Reason
    $results.Add(@{ Name=$Name; Passed=$passed; Failure=$failure })
    if (-not $passed) { Write-Warning "$Name expected rejection /$Reason/; actual: $failure" }
    $project = Join-Path $Arguments.DeliveryRoot 'ReceiverA'
    if (Test-Path -LiteralPath (Join-Path $project 'stage9-owner.json')) { throw 'Rejected call created a receiver identity.' }
    if (Test-Path -LiteralPath (Join-Path $Arguments.DeliveryRoot 'ReceiverA-create.log')) { throw 'Rejected call launched Unity.' }
}
$common = @{ Operation='Create'; Package=$Package; ExpectedUnityVersion='6000.3.27f1'; UguiVersion='2.0.0'; Unity=$Unity6000 }
$arguments = $common.Clone(); $arguments.DeliveryRoot = New-Case 'wrong-executable'; $arguments.Unity = $Unity2022
Expect-Rejected 'Wrong executable' $arguments 'Unity executable version mismatch'
$arguments = $common.Clone(); $arguments.DeliveryRoot = New-Case 'wrong-ugui'; $arguments.UguiVersion = '1.0.0'
Expect-Rejected 'Wrong UGUI' $arguments 'UGUI version does not match'
$arguments = $common.Clone(); $arguments.DeliveryRoot = New-Case 'wrong-owner'
[IO.File]::WriteAllText((Join-Path $arguments.DeliveryRoot 'owner.json'), '{"purpose":"not-owned"}', [Text.UTF8Encoding]::new($false))
Expect-Rejected 'Wrong owner' $arguments 'Explicit owned delivery identity required'
$arguments = $common.Clone(); $arguments.DeliveryRoot = New-Case 'occupied'
$null = [IO.Directory]::CreateDirectory((Join-Path $arguments.DeliveryRoot 'ReceiverA'))
Expect-Rejected 'Occupied receiver' $arguments 'Never adopt an existing receiver'
$arguments = $common.Clone(); $arguments.DeliveryRoot = New-Case 'linked'
$target = Join-Path $cases 'junction-target'; $null = [IO.Directory]::CreateDirectory($target)
$null = New-Item -ItemType Junction -Path (Join-Path $arguments.DeliveryRoot 'ReceiverA') -Target $target
Expect-Rejected 'Linked receiver' $arguments 'Linked path rejected'

# 从真实候选包删除唯一 LICENSE 根，不修改源文件或既有归档。
Add-Type -AssemblyName System.Formats.Tar
$audit = Get-Content -LiteralPath (Join-Path $root ($Package + '.files.json')) -Raw | ConvertFrom-Json
$license = @($audit | Where-Object Path -CEQ 'Assets/YLFramework/LICENSE.md')
if ($license.Count -ne 1) { throw 'Candidate package audit must identify the exact LICENSE root.' }
$case = New-Case 'missing-root'
$badPackage = 'missing-root.unitypackage'
$inputStream = [IO.File]::OpenRead((Join-Path $root $Package))
$outputStream = [IO.File]::Open((Join-Path $case $badPackage), [IO.FileMode]::CreateNew)
$decompress = [IO.Compression.GZipStream]::new($inputStream, [IO.Compression.CompressionMode]::Decompress)
$compress = [IO.Compression.GZipStream]::new($outputStream, [IO.Compression.CompressionLevel]::Optimal)
$reader = [System.Formats.Tar.TarReader]::new($decompress)
$writer = [System.Formats.Tar.TarWriter]::new($compress)
try {
    while ($null -ne ($item = $reader.GetNextEntry($true))) {
        if (-not $item.Name.TrimStart('./').StartsWith($license[0].Guid + '/', [StringComparison]::Ordinal)) { $writer.WriteEntry($item) }
    }
} finally { $writer.Dispose(); $reader.Dispose(); $compress.Dispose(); $decompress.Dispose(); $outputStream.Dispose(); $inputStream.Dispose() }
Expect-Rejected 'Missing export root' @{ DeliveryRoot=$case; Operation='AuditPackage'; Package=$badPackage } 'formal export closure is incomplete'
if (Test-Path -LiteralPath (Join-Path $case ($badPackage + '.receipt.json'))) { throw 'Incomplete archive received an audit receipt.' }
$failed = @($results | Where-Object { -not $_.Passed })
[IO.File]::WriteAllText((Join-Path $cases 'results.json'), (@{ Passed=$failed.Count -eq 0; Cases=@($results) } | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
if ($failed.Count) { throw "$($failed.Count)/$($results.Count) Stage9 guard regressions failed; see $cases" }
"Passed $($results.Count) Stage9 guard regressions: $cases"
