#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param([string] $TemporaryRoot = [IO.Path]::GetTempPath())
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Common/Process.ps1')
. (Join-Path $PSScriptRoot '../Common/Results.ps1')
. (Join-Path $PSScriptRoot '../UnityHost.ps1')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$root = Join-Path (Get-YLTestPath $TemporaryRoot) ('yl-samples-' + [Guid]::NewGuid().ToString('N'))
$workspace = Initialize-YLTestWorkspace -WorkspaceRoot $root -Repository $repository
$ownedHost = New-YLTestDirectory -Workspace $workspace -Kind Host -Key Samples
$identity = @{ sha256 = ('a' * 64) }
$files = [ordered]@{ 'Assets/Marker.txt' = [Text.Encoding]::UTF8.GetBytes('owned marker') }
$count = 0
function Reject([scriptblock] $Action, [string] $Reason) {
    try { & $Action } catch { $script:count++; return }
    throw "Expected refusal: $Reason"
}
function Corrupt-Receipt([object] $Value) {
    [IO.File]::WriteAllText($receiptPath, (ConvertTo-Json -InputObject $Value -Depth 10), [Text.UTF8Encoding]::new($false))
}
try {
    Sync-YLUnityHost -HostRecord $ownedHost -Files $files -Identity $identity -EditorVersion '6000.3.27f1'
    $relative = 'Assets/Samples/Product/0.1.0-preview.1/Example/Example.cs'
    $path = Join-Path $ownedHost.path $relative
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllText($path, '// owned import', [Text.UTF8Encoding]::new($false))
    Reject { Sync-YLUnityHost -HostRecord $ownedHost -Files $files -Identity $identity -EditorVersion '6000.3.27f1' } 'an unreceipted sample is not managed'
    foreach ($extension in @('asset', 'txt', 'meta')) {
        $unknown = [IO.Path]::ChangeExtension($path, $extension)
        [IO.File]::Move($path, $unknown)
        try {
            Reject { Sync-YLUnityHost -HostRecord $ownedHost -Files $files -Identity $identity -EditorVersion '6000.3.27f1' } "an unreceipted .$extension cannot be adopted by the first import"
        } finally { [IO.File]::Move($unknown, $path) }
    }
    $receipt = @{ schemaVersion = 1; purpose = 'YLTesting.Samples'; hostId = $ownedHost.id; inputHash = $identity.sha256; files = @(@{ path = $relative; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }) }
    $receiptPath = Join-Path $ownedHost.path '.yl-samples.json'
    Write-YLTestJson -Path $receiptPath -Value $receipt
    Sync-YLUnityHost -HostRecord $ownedHost -Files $files -Identity $identity -EditorVersion '6000.3.27f1'
    $count++
    $receipt.hostId = 'different-host'
    Corrupt-Receipt $receipt
    Reject { Sync-YLUnityHost -HostRecord $ownedHost -Files $files -Identity $identity -EditorVersion '6000.3.27f1' } 'a different host cannot own imported samples'
    $receipt.hostId = $ownedHost.id
    $receipt.inputHash = 'b' * 64
    Corrupt-Receipt $receipt
    Reject { Sync-YLUnityHost -HostRecord $ownedHost -Files $files -Identity $identity -EditorVersion '6000.3.27f1' } 'a changed package input cannot reuse old imported copies'
    $receipt.inputHash = $identity.sha256
    $receipt.files[0].path = 'Assets/NotASample.cs'
    Corrupt-Receipt $receipt
    Reject { Sync-YLUnityHost -HostRecord $ownedHost -Files $files -Identity $identity -EditorVersion '6000.3.27f1' } 'sample ownership cannot authorize arbitrary sources'
    $receipt.files[0].path = $relative
    Corrupt-Receipt $receipt
    [IO.File]::WriteAllText($path, '// changed by someone else', [Text.UTF8Encoding]::new($false))
    Reject { Sync-YLUnityHost -HostRecord $ownedHost -Files $files -Identity $identity -EditorVersion '6000.3.27f1' } 'modified imported copies must be preserved and refused'
    if ([IO.File]::ReadAllText($path) -cne '// changed by someone else') { throw 'A modified sample was overwritten.' }
    $count++
    "PASS: $count sample host ownership and preservation contracts."
} finally {
    if ([IO.File]::Exists((Join-Path $root '.yl-workspace.json'))) { [IO.Directory]::Delete($root, $true) }
}
