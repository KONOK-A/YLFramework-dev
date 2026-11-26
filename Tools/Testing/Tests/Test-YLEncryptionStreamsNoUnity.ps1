#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param([string] $TemporaryRoot = [IO.Path]::GetTempPath(), [switch] $KeepArtifacts)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
. (Join-Path $PSScriptRoot '../Common/Workspace.ps1')
$parent = Get-YLTestPath ((Resolve-Path -LiteralPath $TemporaryRoot).Path)
if (-not [IO.Directory]::Exists($parent)) { throw 'TemporaryRoot must be a directory.' }
$root = Get-YLTestPath (Join-Path $parent ('yl-stream-contract-' + [guid]::NewGuid().ToString('N')))
if ([IO.Path]::GetDirectoryName($root) -ine $parent -or [IO.Path]::Exists($root)) { throw 'The temporary stream fixture must have a new owned path.' }
$null = [IO.Directory]::CreateDirectory($root)
$runner = Join-Path $PSScriptRoot '../ResourceEncryption/Measure-EncryptionStreams.ps1'
$package = Join-Path $PSScriptRoot '../../../PackageSources/com.kano.ylframework.resource-encryption'
$dll = Join-Path $package 'Runtime/Plugins/Win64/ylresource_tink.dll'
$pwsh = Join-Path $PSHOME 'pwsh.exe'
$checks = 0

function Check([bool] $Pass, [string] $Message) {
    if (-not $Pass) { throw $Message }
    $script:checks++
}

function Invoke-StreamRun([string] $Name, [string[]] $Options, [string] $Reject) {
    $outputRoot = Join-Path $root $Name
    $output = (& $pwsh -NoLogo -NoProfile -NonInteractive -File $runner -Root $outputRoot @Options 2>&1 | Out-String)
    $code = $LASTEXITCODE
    if ($Reject) {
        Check ($code -ne 0 -and $output -match $Reject) "Expected $Name to reject $Reject; exit $code, output: $output"
    } else {
        Check ($code -eq 0) "Expected $Name to succeed; exit $code, output: $output"
    }
    $result = Join-Path $outputRoot 'result.json'
    [pscustomobject]@{
        Root = $outputRoot
        Output = $output
        Result = if ([IO.File]::Exists($result)) { ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($result)) }
    }
}

# SHA-256 independently derived from the xorshift32 LE byte stream, not from a prior harness receipt.
$expected = [ordered]@{
    '8390371' = 'a2109c4e5d8534d72c3a706b55ac6b9c85b97468f6875bd03a28d57f3da50f57'
    '134219484' = 'fddfcd97ad35e1c12ebf9ac86774282c10e960d25e1f74f7de3f98eada97a550'
}
$first = Invoke-StreamRun 'new-a' @('-FreshSeed', '-Smoke')
$report = $first.Result
Check ($report.Version -eq 2 -and $report.InputMode -ceq 'FreshSeed' -and -not $report.HistoricalBytesVerified -and
    $null -eq $report.HistoricalResult -and $null -eq $report.HistoricalResultSha256) 'Synthetic bytes were not clearly distinguished from historical replay.'
Check ($report.RunKind -ceq 'Smoke' -and -not $report.Complete -and $null -eq $report.Summaries -and
    $report.CandidateSampleGates -ceq 'SmokePassed') 'The short check was not kept separate from a qualified timing matrix.'
Check (($report.UnavailableColumns -join ';') -ceq 'shipped-double-sha;shipped-single-sha;cng-old;cng-matched-double-sha' -and
    $null -eq $report.BaselineDllSha256 -and $first.Output -match 'old DLL.*unavailable' -and
    $first.Output -match 'CNG.*unavailable' -and $first.Output -match 'not historical') 'Missing comparator columns or synthetic provenance warnings were misleading.'
Check ($report.Variants.Count -eq 1 -and $report.Variants[0].Role -ceq 'Candidate' -and
    $report.Variants[0].DllSha256 -ieq (Get-FileHash -LiteralPath $dll).Hash) 'The clean checkout must load only the real packaged candidate.'

$second = Invoke-StreamRun 'new-b-explicit-candidate' @('-FreshSeed', '-Smoke', '-CandidateDll', $dll)
Check ($second.Result.CandidateSampleGates -ceq 'SmokePassed') 'An explicit candidate must run without the packaged-default SHA argument.'
foreach ($run in $first, $second) {
    Check ($run.Result.Inputs.Count -eq 2) 'Both planned fixture lengths must be checked, even in smoke.'
    foreach ($inputRow in $run.Result.Inputs) {
        $raw = Get-Item -LiteralPath $inputRow.Raw
        $sha = $expected[[string]$raw.Length]
        Check ($sha -and $raw.Length -eq $inputRow.Input.Length -and $inputRow.Input.Sha256 -ceq $sha -and
            (Get-FileHash -LiteralPath $raw.FullName).Hash -ieq $sha) "Unexpected deterministic fixture identity: $($raw.FullName)"
    }
    $lengths = $run.Result.Inputs | ForEach-Object { $_.Input.Length }
    Check (($lengths -join ';') -ceq '8390371;134219484') 'The two planned fixture lengths were not unique and ordered.'
}

$sequential = @($report.Samples | Where-Object Mode -CEQ 'sequential-32k')
$jump = @($report.Samples | Where-Object Mode -CEQ 'jump-16x32k')
Check ($report.Samples.Count -eq 2 -and $sequential.Count -eq 1 -and $jump.Count -eq 1 -and
    $sequential[0].ReadSha256 -ceq $expected['8390371'] -and $sequential[0].BytesRead -eq 8390371 -and
    $jump[0].BytesRead -eq 524288) 'Smoke must verify the complete second sequential read and all sparse bytes, on the small fixture only.'
foreach ($sample in $report.Samples) {
    Check ($sample.Size -eq 8390371 -and $sample.StableCipherShaBytes -eq 8390555 -and $sample.ExtraCipherShaBytes -eq 0 -and
        $sample.IndependentAuthSource -eq $sample.AuthCounters.Source -and $sample.IndependentReadSource -eq $sample.NativeReadSourceBytes -and
        $sample.NativeBudget.MeetsSampleGates -and $sample.PayloadSourceBound -le 4194304) 'Candidate evidence did not match the independent counters and payload bound.'
}
Check ($sequential[0].NativeBudget.AuthSourceMax -eq 8390595 -and $sequential[0].NativeBudget.SequentialSourceMax -eq 8390555 -and
    $sequential[0].NativeBudget.ExtraAuthHeaderAllowance -eq 40 -and $null -eq $jump[0].NativeBudget.SequentialSourceMax -and
    $null -eq $report.MeasuredPayloadHighWaterBytes) 'Format limits, jump-read scope, or unavailable measured memory were misreported.'

$pack = Join-Path $root 'empty-pack'
$null = [IO.Directory]::CreateDirectory($pack)
$receipt = Join-Path $root 'historical.json'
$history = @{
    pluginSha256 = '963ba62687c2f895649923138083b78acff7bba470ce92ba90228c27e9a46f5c'
    inputs = @(
        @{bytes = 8390371; sha256 = 'd1ea6e08492a1766cf6e8de1d67d4c7ec2c04f3fcb43a21657dda0df9e6cff7f'},
        @{bytes = 134219484; sha256 = 'cbca8370e933a9e278be2c83a85d9b1e4412b16f93ec117848352a32a0834745'}
    )
}
[IO.File]::WriteAllText($receipt, (ConvertTo-Json $history -Depth 4))
$badReceipt = Join-Path $root 'wrong-historical.json'
$history.inputs[0].sha256 = $expected['8390371']
[IO.File]::WriteAllText($badReceipt, (ConvertTo-Json $history -Depth 4))

$mixedRoot = Join-Path $root 'mixed-mode'
$mixedBinding = $null
try { & $runner -Root $mixedRoot -FreshSeed -PackedRoot $pack -HistoricalResult $receipt -Smoke }
catch [Management.Automation.ParameterBindingException] { $mixedBinding = $_.FullyQualifiedErrorId }
Check ($mixedBinding -like 'AmbiguousParameterSet,*') 'Synthetic and historical parameters must be mutually exclusive.'
$disabledSeed = Invoke-StreamRun 'disabled-seed' @('-FreshSeed:$false', '-Smoke') 'Specify -FreshSeed explicitly'
$implicitOld = Invoke-StreamRun 'implicit-old' @('-PackedRoot', $pack, '-HistoricalResult', $receipt, '-Smoke') 'BaselineDll'
$missingReceipt = Invoke-StreamRun 'missing-receipt' @('-PackedRoot', $pack, '-HistoricalResult', "$receipt.absent", '-BaselineDll', $dll, '-Smoke') 'Historical result'
Check (-not [IO.Directory]::Exists($mixedRoot) -and -not [IO.Directory]::Exists($disabledSeed.Root) -and -not [IO.Directory]::Exists($implicitOld.Root) -and
    -not [IO.Directory]::Exists($missingReceipt.Root)) 'Invalid mode or missing historical arguments created output.'
$badOld = Invoke-StreamRun 'wrong-old' @('-FreshSeed', '-BaselineDll', $dll, '-Smoke') 'baseline DLL.*pinned SHA-256'
Check (-not $badOld.Result.Complete -and $badOld.Result.Inputs.Count -eq 0 -and
    $null -eq $badOld.Result.BaselineDllSha256) 'An explicitly supplied candidate was mistaken for the pinned old DLL.'
$badHistory = Invoke-StreamRun 'wrong-receipt' @('-PackedRoot', $pack, '-HistoricalResult', $badReceipt, '-BaselineDll', $dll, '-Smoke') 'Historical result does not identify'
$missingPacked = Invoke-StreamRun 'missing-packed' @('-PackedRoot', $pack, '-HistoricalResult', $receipt, '-BaselineDll', $dll, '-Smoke') 'Historical Bundle is missing'
$wrongPacked = Join-Path $pack 'protected_assets_frozen-small_b667ab173ac47f9d66b852fadb075a0d.bundle'
$file = [IO.File]::Open($wrongPacked, 'CreateNew', 'Write', 'None')
try { $file.SetLength(8390371) } finally { $file.Dispose() }
$badPacked = Invoke-StreamRun 'wrong-packed' @('-PackedRoot', $pack, '-HistoricalResult', $receipt, '-BaselineDll', $dll, '-Smoke') 'historical plaintext SHA-256 differs'
foreach ($failed in $badHistory, $missingPacked, $badPacked) {
    Check ($failed.Result.InputMode -ceq 'Historical' -and -not $failed.Result.HistoricalBytesVerified -and
        -not $failed.Result.Complete -and $failed.Result.Samples.Count -eq 0) 'Historical misuse or mismatch silently became synthetic evidence.'
}
$halfLegacy = Invoke-StreamRun 'partial-legacy' @('-FreshSeed', '-LegacyContentSource', $pack, '-Smoke') 'Specify both legacy source'
Check (-not [IO.Directory]::Exists($halfLegacy.Root)) 'A partially supplied legacy comparator was silently omitted.'

if ($KeepArtifacts) {
    "Artifacts retained: $root"
} else {
    $null = @(Get-YLTestTreeFiles -Path $root -ForCleanup -ProtectNested)
    [IO.Directory]::Delete($root, $true)
}
"Encryption stream NoUnity contract passed: $checks checks."
