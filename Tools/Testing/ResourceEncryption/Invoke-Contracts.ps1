#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param([Parameter(Mandatory)][string] $Root, [Parameter(Mandatory)][string] $OracleJar, [string] $NativeDll)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../PackageSources/com.kano.ylframework.resource-encryption/Runtime'))
if (-not $NativeDll) { $NativeDll = Join-Path $source 'Plugins/Win64/ylresource_tink.dll' }
$NativeDll = (Resolve-Path -LiteralPath $NativeDll).Path
$OracleJar = (Resolve-Path -LiteralPath $OracleJar).Path
$sources = @('EncryptedBundleDescription.cs', 'IEncryptedBundleKeySource.cs', 'AuthenticatedBundleStream.cs', 'OwnedWindowsFiles.cs', 'EncryptedBundleCache.cs') |
    ForEach-Object { Join-Path $source $_ }
if (@($sources | Where-Object { -not [IO.File]::Exists($_) }).Count -gt 0) { throw 'P1 contracts cannot run: the descriptor, authenticated reader and cache are not implemented.' }
$references = @([IO.Directory]::EnumerateFiles((Join-Path $PSHOME 'ref'), '*.dll')) + (Join-Path $PSHOME 'Newtonsoft.Json.dll')
Add-Type -Path (@($sources) + (Join-Path $PSScriptRoot 'Contracts.cs')) -ReferencedAssemblies $references -CompilerOptions @('/langversion:9.0', '/nowarn:1701')
if ((Get-FileHash -LiteralPath $OracleJar -Algorithm SHA256).Hash -ine '30df12a1f59045637213477656e01f6fbcc02616c20da1696cbd4643ada668ef') { throw 'The independent Tink Java oracle differs from v1.18.0.' }
[YLResourceEncryptionContracts]::Run($Root, $NativeDll, $OracleJar, (Join-Path $PSScriptRoot 'TinkOracle.java')).GetAwaiter().GetResult()
