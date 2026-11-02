#requires -Version 7.6
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $EvidenceRoot,
    [string] $BuildName = 'B1',
    [ValidateSet('None','Encrypted')][string] $Group = 'None',
    [ValidateSet('Mono2x','IL2CPP')][string] $Backend = 'Mono2x',
    [ValidateSet('','2022.3.62f3c1','6000.3.27f1')][string] $ExpectedUnityVersion = '',
    [string] $RunPrefix = ''
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$invoke = Join-Path $PSScriptRoot 'Invoke-ResourcePlayerAcceptance.ps1'
$root = [IO.Path]::GetFullPath($EvidenceRoot)
$mode = if ($Group -eq 'None') { 'none' } else { 'encrypted' }
$store = 'local-' + $mode
$prefix = if ($RunPrefix) { $RunPrefix } else { $mode + '-' }
function Run([string] $Id, [string] $Command, [int] $Expect = 1, [int] $Target = 2, [string] $Checkpoint = '', [string] $Fault = '', [string] $Key = 'valid') {
    & $invoke -EvidenceRoot $root -ExpectedUnityVersion $ExpectedUnityVersion -BuildName $BuildName -Backend $Backend -RunId ($prefix + $Id) -Command $Command -StoreName $store -Mode $mode -Expect $Expect -Target $Target -Checkpoint $Checkpoint -Fault $Fault -Key $Key
}
if ($Group -eq 'Encrypted') {
    Run 'missing-key' 'probe' -Key missing
    Run 'wrong-key' 'probe' -Key wrong
}
Run 'cancel' 'cancel'
Run 'prepare' 'prepare'
Run 'prepared-restart' 'probe'
Run 'busy-activate' 'busy'
Run 'active-restart' 'probe' 2
Run 'rollback' 'rollback' 2 1
Run 'rollback-restart' 'probe'
if ($Group -eq 'None') {
    Run 'exception' 'update' 1 2 'BeforeCommitDecision' 'throw'
    Run 'exception-restart' 'probe'
    foreach ($point in @('JournalVerified','ActiveWritten:r810-a','BeforeCommitDecision','CommitDecided')) {
        $id = $point.Replace(':','-').ToLowerInvariant()
        Run ('kill-' + $id) 'update' 1 2 $point 'kill'
        $expected = if ($point -eq 'CommitDecided') { 2 } else { 1 }
        Run ('recover-' + $id) 'probe' $expected
    }
    # 仅损坏此suite已创建且owner完整登记的控制槽，失败原件与损坏字节均保留。
    $ownedStore = Join-Path $root ('Stores/' + $store)
    $owner = Get-Content -LiteralPath (Join-Path $ownedStore '.ylr-store-owner.json') -Raw | ConvertFrom-Json
    if ($owner.Root -cne $ownedStore -or @($owner.Projects).Count -ne 2) { throw 'Refuse to corrupt an unknown Store.' }
    $active = Join-Path $ownedStore 'r810-a/StandaloneWindows64/active.json'
    $backup = Join-Path $root 'corrupt-active-before.json'
    if (Test-Path -LiteralPath $backup) { throw 'Never overwrite corruption evidence.' }
    [IO.File]::Copy($active,$backup)
    [IO.File]::WriteAllText($active,'{broken-owned-active')
    [IO.File]::Copy($active,(Join-Path $root 'corrupt-active-injected.json'))
    Run 'corrupt-active-lkg' 'probe'
} else {
    Run 'reactivate' 'update'
    Run 'updated-restart' 'probe' 2
    Run 'updated-missing-key' 'probe' 2 -Key missing
    Run 'updated-wrong-key' 'probe' 2 -Key wrong
    Run 'updated-valid-key' 'probe' 2
}
"R810 local $Group suite finished."
