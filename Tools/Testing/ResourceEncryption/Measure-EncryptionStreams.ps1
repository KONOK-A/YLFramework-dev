#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding(DefaultParameterSetName = 'Historical')]
param(
    [Parameter(Mandatory)] [string] $Root,
    [Parameter(Mandatory, ParameterSetName = 'Contract')] [string] $ContractDll,
    [Parameter(Mandatory, ParameterSetName = 'Contract')] [string] $OracleJar,
    [Parameter(Mandatory, ParameterSetName = 'Historical')] [string] $PackedRoot,
    [Parameter(Mandatory, ParameterSetName = 'Historical')] [string] $HistoricalResult,
    [Parameter(Mandatory, ParameterSetName = 'FreshSeed')] [switch] $FreshSeed,
    [Parameter(Mandatory, ParameterSetName = 'Historical')]
    [Parameter(ParameterSetName = 'FreshSeed')] [ValidateNotNullOrEmpty()] [string] $BaselineDll,
    [Parameter(ParameterSetName = 'Historical')]
    [Parameter(ParameterSetName = 'FreshSeed')] [ValidateNotNullOrEmpty()] [string] $CandidateDll,
    [Parameter(ParameterSetName = 'Historical')]
    [Parameter(ParameterSetName = 'FreshSeed')] [hashtable] $VariantDll = @{},
    [Parameter(ParameterSetName = 'Historical')]
    [Parameter(ParameterSetName = 'FreshSeed')] [ValidateNotNullOrEmpty()] [string] $LegacyContentSource,
    [Parameter(ParameterSetName = 'Historical')]
    [Parameter(ParameterSetName = 'FreshSeed')] [ValidateNotNullOrEmpty()] [string] $LegacyBouncyCastle,
    [Parameter(ParameterSetName = 'Historical')]
    [Parameter(ParameterSetName = 'FreshSeed')] [switch] $Smoke
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') {
    throw 'The stream microbenchmark requires Windows x64 PowerShell.'
}
if ($PSCmdlet.ParameterSetName -eq 'FreshSeed' -and -not $FreshSeed) { throw 'Specify -FreshSeed explicitly to use synthetic inputs.' }
$rootPath = [IO.Path]::GetFullPath($Root)
if ((Test-Path -LiteralPath $rootPath) -or -not (Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($rootPath)) -PathType Container)) {
    throw 'The output root must be a fresh child of an existing tool-owned directory.'
}
$rootParent = Get-Item -LiteralPath ([IO.Path]::GetDirectoryName($rootPath))
for ($ancestor = $rootParent; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point in output ancestry: $ancestor" }
}

$source = Join-Path $PSScriptRoot 'Measure-EncryptionStreams.Fixture.cs'
$references = @([IO.Directory]::EnumerateFiles((Join-Path $PSHOME 'ref'), '*.dll'))
$sources = @($source, (Join-Path $PSScriptRoot 'Measure-EncryptionStreams.Benchmark.cs'))
$compile = @('/optimize+')
if ($LegacyContentSource -or $LegacyBouncyCastle) {
    if (-not $LegacyContentSource -or -not $LegacyBouncyCastle) { throw 'Specify both legacy source and the compatible BouncyCastle DLL, or neither.' }
    foreach ($name in 'CngSegmentAead.cs', 'ResourceBundleContext.cs', 'ResourceBundleEnvelope.cs', 'ResourceDecryptStream.cs') {
        $sources += (Resolve-Path -LiteralPath (Join-Path $LegacyContentSource $name)).Path
    }
    $bc = (Resolve-Path -LiteralPath $LegacyBouncyCastle).Path
    $newtonsoft = Join-Path $PSHOME 'Newtonsoft.Json.dll'
    $references += @($bc, $newtonsoft)
    $compile += @('/define:LEGACY_CNG,UNITY_EDITOR_WIN', '/unsafe+', '/nowarn:1701')
    $null = [Reflection.Assembly]::LoadFrom($bc)
}

if ($PSCmdlet.ParameterSetName -eq 'Contract') {
    $dll = (Resolve-Path -LiteralPath $ContractDll).Path
    $jar = (Resolve-Path -LiteralPath $OracleJar).Path
    if ((Get-FileHash -LiteralPath $jar).Hash -ine '30df12a1f59045637213477656e01f6fbcc02616c20da1696cbd4643ada668ef') {
        throw 'The Java oracle differs from the pinned Tink 1.18.0 SHA-256.'
    }
    $null = Get-Command java.exe -CommandType Application
    Add-Type -Path $sources -ReferencedAssemblies $references -CompilerOptions $compile
    $null = [IO.Directory]::CreateDirectory($rootPath)
    $failures = [EncryptionStreamMicrobenchmark]::Contract($rootPath, $dll, $jar, (Join-Path $PSScriptRoot 'TinkOracle.java'))
    if ($failures -ne 0) { throw "$failures native stream contract case(s) failed: $rootPath\contract.json" }
    "Native stream contract passed: $rootPath\contract.json"
    return
}

function Resolve-StreamFile([string] $Path, [string] $Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Label is missing: $Path" }
    (Resolve-Path -LiteralPath $Path).Path
}

$packed = $history = $baseline = $candidate = $candidateSha = $null
if (-not $FreshSeed) {
    $history = Resolve-StreamFile $HistoricalResult 'Historical result'
    if (-not (Test-Path -LiteralPath $PackedRoot -PathType Container)) { throw "Historical PackedRoot is missing: $PackedRoot" }
    $packed = (Resolve-Path -LiteralPath $PackedRoot).Path
}
if ($BaselineDll) { $baseline = Resolve-StreamFile $BaselineDll 'Pinned old baseline DLL' }
$variants = [string[]]@($VariantDll.Keys | Sort-Object)
if ($CandidateDll) {
    if ($variants -icontains 'combined') { throw 'Specify -CandidateDll or the combined -VariantDll entry, not both.' }
    $candidate = Resolve-StreamFile $CandidateDll 'Candidate DLL'
} elseif ($FreshSeed -and $variants -inotcontains 'combined') {
    $package = Join-Path $PSScriptRoot '../../../PackageSources/com.kano.ylframework.resource-encryption'
    $identity = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText((Join-Path $package 'Documentation~/NativeBackend.json')))
    $candidateSha = $identity.artifact.sha256
    if ($candidateSha -cnotmatch '^[0-9a-f]{64}$') { throw 'The packaged candidate is missing its recorded DLL SHA-256.' }
    $candidate = Resolve-StreamFile (Join-Path $package 'Runtime/Plugins/Win64/ylresource_tink.dll') 'Packaged candidate DLL'
}
$paths = [string[]]@($variants | ForEach-Object { Resolve-StreamFile $VariantDll[$_] "Variant DLL '$_'" })
Add-Type -Path $sources -ReferencedAssemblies $references -CompilerOptions $compile

if ($FreshSeed) { Write-Warning 'FreshSeed generates synthetic plaintext, not historical Bundle bytes.' }
if (-not $baseline) { Write-Warning 'The pinned old DLL was not supplied; shipped-double-sha and shipped-single-sha are unavailable.' }
if (-not $LegacyContentSource) { Write-Warning 'Archived CNG inputs were not supplied; cng-old and cng-matched-double-sha are unavailable.' }
if (-not $candidate -and $variants -inotcontains 'combined') { Write-Warning 'No candidate DLL was supplied; this replay does not qualify a candidate.' }

$metadata = [ordered]@{
    PowerShell = $PSVersionTable.PSVersion.ToString()
    FixtureSources = @(Get-FileHash -LiteralPath $sources[0..1] | Select-Object Path, Hash)
    RunnerSha256 = (Get-FileHash -LiteralPath $PSCommandPath).Hash.ToLowerInvariant()
    LegacyInputs = if ($LegacyContentSource) { @(Get-FileHash -LiteralPath $sources[2..($sources.Count - 1)] | Select-Object Path, Hash) } else { @() }
    LegacyDependency = if ($LegacyBouncyCastle) { @(Get-FileHash -LiteralPath $bc, $newtonsoft | Select-Object Path, Hash) }
    Cpu = @(Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors, LoadPercentage)
    Disk = @(Get-CimAssociatedInstance -InputObject (Get-CimInstance Win32_LogicalDisk -Filter "DeviceID = '$([IO.Path]::GetPathRoot($rootPath).TrimEnd('\'))'") -ResultClassName Win32_DiskPartition |
        ForEach-Object { Get-CimAssociatedInstance -InputObject $_ -ResultClassName Win32_DiskDrive } |
        Select-Object Model, InterfaceType, MediaType)
    ConcurrentProcesses = @(Get-Process Unity, UnityShaderCompiler, MSBuild, cl, nasm, java -ErrorAction SilentlyContinue |
        Select-Object ProcessName, Id, CPU)
}
$null = [IO.Directory]::CreateDirectory($rootPath)
ConvertTo-Json -InputObject $metadata -Depth 5 | Set-Content -LiteralPath (Join-Path $rootPath 'host-start.json') -Encoding utf8NoBOM
try {
    $failures = [EncryptionStreamMicrobenchmark]::Measure($rootPath, $FreshSeed.IsPresent, $Smoke.IsPresent, $packed, $history,
        $baseline, $candidate, $candidateSha, $variants, $paths)
} finally {
    ConvertTo-Json -InputObject ([ordered]@{
        Utc = [DateTimeOffset]::UtcNow
        Cpu = @(Get-CimInstance Win32_Processor | Select-Object Name, LoadPercentage)
        ConcurrentProcesses = @(Get-Process Unity, UnityShaderCompiler, MSBuild, cl, nasm, java -ErrorAction SilentlyContinue |
            Select-Object ProcessName, Id, CPU)
    }) -Depth 5 | Set-Content -LiteralPath (Join-Path $rootPath 'host-end.json') -Encoding utf8NoBOM
}
if ($failures -ne 0) { throw "Stream setup, verification or candidate sample gates failed ($failures): $rootPath\result.json" }
