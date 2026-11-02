[CmdletBinding()]
param([switch] $DownloadOnly)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../..'))
$excel = Join-Path $root 'PackageSources/com.kano.ylframework.excel'
$manifest = Join-Path $excel 'package.json'
if (-not [IO.File]::Exists($manifest) -or ([IO.File]::ReadAllText($manifest) | ConvertFrom-Json).name -cne 'com.kano.ylframework.excel') { throw 'Not the maintained YLFramework Excel package.' }
$output = Join-Path $root 'Temp/Extensions.Data.Excel/Dependency'
$null = [IO.Directory]::CreateDirectory($output)
$package = Join-Path $output 'miniexcel.1.31.2.nupkg'
$url = 'https://api.nuget.org/v3-flatcontainer/miniexcel/1.31.2/miniexcel.1.31.2.nupkg'
if (-not (Test-Path -LiteralPath $package)) { Invoke-WebRequest -Uri $url -OutFile $package -UseBasicParsing }
$hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
"MiniExcel 1.31.2 package SHA256=$hash"
$license = Join-Path $output 'MiniExcel-LICENSE.txt'
$licenseUrl = 'https://raw.githubusercontent.com/mini-software/MiniExcel/f1498a2d162debca9a1e9153cf2249131a75eeb0/LICENSE'
if (-not (Test-Path -LiteralPath $license)) { Invoke-WebRequest -Uri $licenseUrl -OutFile $license -UseBasicParsing }
$licenseHash = (Get-FileHash -LiteralPath $license -Algorithm SHA256).Hash.ToLowerInvariant()
"MiniExcel upstream LICENSE SHA256=$licenseHash"
if ($DownloadOnly) { return }
$expected = 'f11a54c1bd5b3fb3332dc5d4e40ade59a3539cb8de6d087a245575457d66379a'
if ($hash -ne $expected) { throw 'MiniExcel package identity mismatch. Nothing installed.' }
$expectedLicense = 'c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4'
if ($licenseHash -ne $expectedLicense) { throw 'MiniExcel license identity mismatch. Nothing installed.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$destination = Join-Path $excel 'Editor/Plugins'
$pluginMeta = Join-Path $destination 'MiniExcel.dll.meta'
if (-not [IO.File]::Exists($pluginMeta)) { throw 'Preserve the maintained MiniExcel plugin identity and importer settings before restoring.' }
$meta = [IO.File]::ReadAllText($pluginMeta)
$guid = [regex]::Matches($meta, '(?m)^guid: ([0-9a-f]{32})\s*$')
$platforms = [regex]::Matches($meta, '(?m)^\s+enabled:\s*(\S+)\s*$')
if ($guid.Count -ne 1 -or $guid[0].Groups[1].Value -cne 'c7540153c69cf324883a3adbf1efeeb3' -or
    $meta -cnotmatch '(?m)^PluginImporter:\s*$' -or $platforms.Count -ne 3 -or
    ($platforms | ForEach-Object { $_.Groups[1].Value }) -join ',' -cne '0,1,0' -or
    $meta -cnotmatch '(?ms)Editor: Editor\s+second:\s+enabled: 1\s') { throw 'MiniExcel metadata must retain its approved GUID and Editor-only importer settings.' }
$metaHash = (Get-FileHash -LiteralPath $pluginMeta -Algorithm SHA256).Hash
$null = [IO.Directory]::CreateDirectory($destination)
$zip = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $entry = $zip.GetEntry('lib/netstandard2.0/MiniExcel.dll')
    if ($null -eq $entry) { throw 'Pinned package has no expected netstandard2.0 assembly.' }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $destination 'MiniExcel.dll'), $true)
} finally { $zip.Dispose() }
$licenseDestination = Join-Path $excel 'Editor/Licenses'
$null = [IO.Directory]::CreateDirectory($licenseDestination)
Copy-Item -LiteralPath $license -Destination (Join-Path $licenseDestination 'MiniExcel-LICENSE.txt') -Force
Get-FileHash -LiteralPath (Join-Path $destination 'MiniExcel.dll') -Algorithm SHA256
if ((Get-FileHash -LiteralPath $pluginMeta -Algorithm SHA256).Hash -cne $metaHash) { throw 'MiniExcel plugin metadata was unexpectedly changed.' }
