#requires -Version 7.6
[CmdletBinding()]
param([Parameter(Mandatory)][string] $EvidenceRoot, [Parameter(Mandatory)][string] $RunId, [switch] $WithoutObserver)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($EvidenceRoot)
$carrier = (Get-Content -LiteralPath (Join-Path $root 'carrier-location.json') -Raw | ConvertFrom-Json).Carrier
$owner = Get-Content -LiteralPath (Join-Path $carrier 'r810-owner.json') -Raw | ConvertFrom-Json
if ($owner.Evidence -cne $root -or $RunId -notmatch '^[a-z0-9-]+$') { throw 'Owned probe root required.' }
$probe = Join-Path $root ('watcher-probe-' + $RunId)
if (Test-Path -LiteralPath $probe) { throw 'Do not overwrite probe evidence.' }
$null = [IO.Directory]::CreateDirectory((Join-Path $probe 'r'))
$null = [IO.Directory]::CreateDirectory((Join-Path $probe 's'))
Add-Type -TypeDefinition ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ResourcePlayerFileWatch.cs.txt')))
$watcher = if ($WithoutObserver) { $null } else { [ResourcePlayerFileWatch]::new($probe, (Join-Path $probe 'r'), (Join-Path $probe 's')) }
$target = Join-Path $probe 's/metadata.json'
[IO.File]::WriteAllText($target,'{"Version":0}')
$failure = $null; $count = 0
try {
    for ($i = 1; $i -le 10000; $i++) {
        $next = $target + '.next'
        $bytes = [Text.Encoding]::UTF8.GetBytes('{"Version":' + $i + '}')
        $stream = [IO.FileStream]::new($next,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try { $stream.Write($bytes); $stream.Flush($true) } finally { $stream.Dispose() }
        [IO.File]::Replace($next,$target,[System.Management.Automation.Language.NullString]::Value)
        $count++
    }
    if ($watcher) {
        [IO.File]::WriteAllBytes((Join-Path $probe 's/leak.bin'),[Text.Encoding]::ASCII.GetBytes("UnityFS`0"))
        if (-not [Threading.SpinWait]::SpinUntil([Func[bool]]{ $watcher.Plaintext -gt 0 },5000)) { throw 'Positive plaintext probe was not detected.' }
    }
} catch { $failure = $_.Exception.ToString() }
finally { if ($watcher) { $watcher.Dispose() } }
$receipt = @{ Replacements=$count; Failure=$failure; WithoutObserver=$WithoutObserver.IsPresent; Probe=$probe; PositivePlaintextDetections=if ($watcher) { $watcher.Plaintext } else { $null }; ControlEntriesNotProbed=if ($watcher) { $watcher.ControlEntriesNotProbed } else { $null }; Passed=$null -eq $failure }
[IO.File]::WriteAllText((Join-Path $probe 'result.json'),($receipt | ConvertTo-Json))
$receipt | ConvertTo-Json -Compress
if ($failure) { throw 'Atomic metadata replacement probe failed; preserve the full cause and compare the control.' }
