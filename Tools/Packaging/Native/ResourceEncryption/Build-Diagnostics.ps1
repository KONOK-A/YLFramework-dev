#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Root,
    [Parameter(Mandatory)] [string] $CMake,
    [Parameter(Mandatory)] [string] $TinkArchive,
    [Parameter(Mandatory)] [string] $AbseilArchive,
    [Parameter(Mandatory)] [string] $BoringSslArchive,
    [Parameter(Mandatory)] [string] $ProtobufArchive,
    [string] $Nasm,
    [string] $WindowsSdk,
    [string] $Toolset,
    [ValidateRange(1, 32)] [int] $Jobs = 4
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$rootPath = [IO.Path]::GetFullPath($Root)
if ((Test-Path -LiteralPath $rootPath) -or -not (Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($rootPath)) -PathType Container)) {
    throw 'Use a fresh build root in a tool-owned directory.'
}
for ($ancestor = Get-Item -LiteralPath ([IO.Path]::GetDirectoryName($rootPath)); $null -ne $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point in output ancestry: $ancestor" }
}
$cmakeExe = (Resolve-Path -LiteralPath $CMake).Path
$archives = [ordered]@{
    TINK_PIN = (Resolve-Path -LiteralPath $TinkArchive).Path
    ABSEIL = (Resolve-Path -LiteralPath $AbseilArchive).Path
    BORINGSSL = (Resolve-Path -LiteralPath $BoringSslArchive).Path
    PROTOBUF = (Resolve-Path -LiteralPath $ProtobufArchive).Path
}
$nasmExe = if ($Nasm) { (Resolve-Path -LiteralPath $Nasm).Path }
$null = [IO.Directory]::CreateDirectory($rootPath)

function Save-Evidence($Value, [string] $Path) {
    ConvertTo-Json -InputObject $Value -Depth 8 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function Invoke-Logged([string] $Exe, [string[]] $Arguments, [string] $Log) {
    Save-Evidence ([ordered]@{ Executable = $Exe; Arguments = $Arguments }) ($Log + '.command.json')
    & $Exe @Arguments *> $Log
    $code = $LASTEXITCODE
    if ($code -ne 0) { throw "$Exe failed ($code); see $Log" }
}

$record = [ordered]@{
    Source = @(Get-FileHash -LiteralPath @('CMakeLists.txt', 'StreamingBridge.cc', 'TinkRandomAccess.cmake', 'Build-Diagnostics.ps1' |
        ForEach-Object { Join-Path $PSScriptRoot $_ }) | Select-Object Path, Hash)
    Archives = @(Get-FileHash -LiteralPath @($archives.Values) | Select-Object Path, Hash)
    CMake = Get-FileHash -LiteralPath $cmakeExe | Select-Object Path, Hash
    CMakeVersion = @(& $cmakeExe --version)
    Nasm = if ($nasmExe) { Get-FileHash -LiteralPath $nasmExe | Select-Object Path, Hash }
    NasmVersion = if ($nasmExe) { @(& $nasmExe -v) }
    AsmUnavailable = $null
    Variants = [ordered]@{}
}
Save-Evidence $record (Join-Path $rootPath 'build-identity.json')

# Reuse only freshly built dependencies within this job, never a historical build cache.
# Capture each configuration and save the resulting DLL before reconfiguring that build directory.
$variants = @(
    @{ Name = 'rebuilt-baseline'; Asm = $false; Align = $false; Trace = $false },
    @{ Name = 'reader-only'; Asm = $false; Align = $true; Trace = $false }
)
if ($nasmExe) {
    $variants += @(
        @{ Name = 'asm-only'; Asm = $true; Align = $false; Trace = $false },
        @{ Name = 'combined'; Asm = $true; Align = $true; Trace = $false },
        @{ Name = 'diagnostics'; Asm = $true; Align = $true; Trace = $true }
    )
} else {
    $record.AsmUnavailable = 'No explicit NASM executable supplied; assembly comparisons not run.'
    $variants += @{ Name = 'diagnostics'; Asm = $false; Align = $true; Trace = $true }
}

foreach ($variant in $variants) {
    $name = $variant.Name
    $build = Join-Path $rootPath $(if ($variant.Asm) { 'asm' } else { 'noasm' })
    $results = Join-Path $rootPath $name
    $null = [IO.Directory]::CreateDirectory($results)
    $architecture = if ($WindowsSdk) { "x64,version=$WindowsSdk" } else { 'x64' }
    $config = @('-S', $PSScriptRoot, '-B', $build, '-G', 'Visual Studio 18 2026', '-A', $architecture)
    if ($Toolset) { $config += @('-T', "host=x64,version=$Toolset") }
    $config += '-DOPENSSL_NO_ASM=' + $(if ($variant.Asm) { 'OFF' } else { 'ON' })
    $config += '-DYL_ALIGNED_READER=' + $(if ($variant.Align) { 'ON' } else { 'OFF' })
    $config += '-DYL_PAYLOAD_TRACE=' + $(if ($variant.Trace) { 'ON' } else { 'OFF' })
    $config += '-DYL_DISPATCH_TRACE=' + $(if ($variant.Trace -and $variant.Asm) { 'ON' } else { 'OFF' })
    $config += '-DCMAKE_SHARED_LINKER_FLAGS=/MAP'
    if ($variant.Asm) { $config += "-DCMAKE_ASM_NASM_COMPILER=$nasmExe" }
    foreach ($key in $archives.Keys) { $config += "-DYL_${key}_ARCHIVE=$($archives[$key])" }
    "Configuring $name"
    Invoke-Logged $cmakeExe $config (Join-Path $results 'configure.log')
    Copy-Item -LiteralPath (Join-Path $build 'CMakeCache.txt') -Destination $results
    $compilerFile = @(Get-ChildItem -Path (Join-Path $build 'CMakeFiles/*/CMakeCXXCompiler.cmake') -File)
    if ($compilerFile.Count -ne 1) { throw "Could not identify CMake's actual compiler in $build" }
    Copy-Item -LiteralPath $compilerFile[0].FullName -Destination $results
    $compilerDeclaration = Select-String -LiteralPath $compilerFile[0].FullName -Pattern '^set\(CMAKE_CXX_COMPILER "([^"]+)"\)$'
    $cl = $compilerDeclaration.Matches[0].Groups[1].Value
    & $cl /Bv *> (Join-Path $results 'compiler-version.log') # cl reports D8003 after printing identity without a source file.
    $sdkLine = Select-String -LiteralPath (Join-Path $build 'ylresource_tink.vcxproj') -Pattern '<WindowsTargetPlatformVersion>([\d.]+)</WindowsTargetPlatformVersion>'
    $sdkVersion = $sdkLine.Matches[0].Groups[1].Value
    $kits = Get-ItemPropertyValue -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -Name KitsRoot10
    $sdkFiles = @("bin/$sdkVersion/x64/rc.exe", "Include/$sdkVersion/um/Windows.h", "Lib/$sdkVersion/um/x64/kernel32.lib") |
        ForEach-Object { Join-Path $kits $_ }
    "Building $name"
    Invoke-Logged $cmakeExe @('--build', $build, '--config', 'Release', '--target', 'ylresource_tink', '--parallel', "$Jobs") (Join-Path $results 'build.log')
    $dll = Join-Path $results 'ylresource_tink.dll'
    Copy-Item -LiteralPath (Join-Path $build 'Release/ylresource_tink.dll') -Destination $dll
    Copy-Item -LiteralPath (Join-Path $build 'Release/ylresource_tink.map') -Destination $results
    $dumpbin = Join-Path ([IO.Path]::GetDirectoryName($cl)) 'dumpbin.exe'
    Invoke-Logged $dumpbin @('/headers', '/exports', '/dependents', $dll) (Join-Path $results 'pe.txt')
    $pe = Get-Content -LiteralPath (Join-Path $results 'pe.txt') -Raw
    $exports = [regex]::Matches($pe, '(?m)^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]+\s+(\S+)\s*$') |
        ForEach-Object { $_.Groups[1].Value }
    if ($pe -notmatch '8664 machine \(x64\)' -or $pe -match '(?im)^\s+(libcrypto|libssl|vcruntime|msvcp|protobuf|absl)\S*\.dll' -or
        ($exports -join ',') -ne 'yl_close,yl_open,yl_read,yl_read_statistics,yl_seal') {
        throw "Unexpected machine, dependencies, or exports; see $results\pe.txt"
    }
    $record.Variants[$name] = [ordered]@{
        Dll = Get-FileHash -LiteralPath $dll | Select-Object Path, Hash
        Compiler = Get-FileHash -LiteralPath $cl | Select-Object Path, Hash
        Linker = Get-FileHash -LiteralPath (Join-Path ([IO.Path]::GetDirectoryName($cl)) 'link.exe') | Select-Object Path, Hash
        WindowsSdkVersion = $sdkVersion
        WindowsSdkInputs = @(Get-FileHash -LiteralPath $sdkFiles | Select-Object Path, Hash)
        Build = $variant
        Exports = $exports
    }
    Save-Evidence $record (Join-Path $rootPath 'build-identity.json')
}
