#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param([Parameter(Mandatory)][string] $NodeRoot, [Parameter(Mandatory)][string] $OutputRoot,
    [string] $Source = ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
. (Join-Path $PSScriptRoot '../Common/Process.ps1')
. (Join-Path $PSScriptRoot '../../Packaging/Build-YLRegistryPackages.ps1')
. (Join-Path $PSScriptRoot '../Common/Workspace.ps1')
$sourceRoot = Get-YLTestPath (Join-Path $Source 'PackageSources')
$out = Get-YLTestPath $OutputRoot
if ([IO.Path]::Exists($out) -or -not $out.StartsWith('G:\Unity\Tests\', [StringComparison]::OrdinalIgnoreCase) -or
    -not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($out))) { throw 'Use a fresh owned output beneath the Unity test root.' }
$null = [IO.Directory]::CreateDirectory($out)
$base = 'com.kano.ylframework'
$extension = "$base.resource-encryption"
$baseCommit = git -C ([IO.Path]::GetDirectoryName($sourceRoot)) rev-parse --verify HEAD
if ($LASTEXITCODE -ne 0 -or $baseCommit -cnotmatch '^[0-9a-f]{40}$') { throw 'Cannot identify the local source baseline.' }
$receipt = [ordered]@{ purpose = 'YLResource.LocalEncryptionPack'; formalCandidate = $false; status = 'Incomplete';
    baseCommit = $baseCommit; source = $sourceRoot; artifacts = [ordered]@{}; negatives = [ordered]@{} }
function Hex([byte[]]$bytes) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() }
function Reject([string]$name, [string]$pattern, [scriptblock]$operation) {
    try { $null = & $operation } catch {
        if ($_.Exception.Message -cnotmatch $pattern) { throw }
        $receipt.negatives[$name] = $_.Exception.Message
        return
    }
    throw "Package gate accepted $name."
}
function Read-Contents([string]$root) {
    $contents = [Collections.Generic.Dictionary[string, byte[]]]::new([StringComparer]::Ordinal)
    foreach ($file in @(Get-YLTestTreeFiles $root)) {
        $relative = [IO.Path]::GetRelativePath($root, $file).Replace('\','/')
        if ($relative -cne '.gitattributes') { $contents.Add($relative, [IO.File]::ReadAllBytes($file)) }
    }
    return ,$contents
}
try {
    $catalogPath = Join-Path $Source 'Tools/Packaging/PackageCatalog.json'
    $catalog = Read-YLPackageCatalog -Path $catalogPath
    $profiles = @((Get-YLRegistryPackageSet -Root $sourceRoot -Catalog $catalog) | Where-Object id -CIn @($base,$extension))
    if ($profiles.Count -ne 2) { throw 'The main and optional package inputs are required.' }
    $native = 'Runtime/Plugins/Win64/ylresource_tink.dll'
    $importer = "$native.meta"
    $bytes = Read-Contents (Join-Path $sourceRoot $extension)
    $stock = Read-Contents (Join-Path $sourceRoot $base)
    $original = $bytes[$native]
    $meta = $bytes[$importer]
    try {
        $bad = [byte[]]$original.Clone(); $bad[$bad.Length - 1] = $bad[$bad.Length - 1] -bxor 1; $bytes[$native] = $bad
        Reject 'other-bytes' 'byte identity differs' { Get-YLRegistryPayload $bytes $catalog }
        $bad = [byte[]]$original.Clone(); $at = 4 + [BitConverter]::ToInt32($bad,60); $bad[$at] = 0x4c; $bad[$at + 1] = 1; $bytes[$native] = $bad
        Reject 'other-architecture' 'Win64-only native PE' { Get-YLRegistryPayload $bytes $catalog }
        $bytes[$native] = $original
        $bytes[$importer] = [Text.Encoding]::UTF8.GetBytes([Text.Encoding]::UTF8.GetString($meta).Replace('Standalone: Win64','Standalone: Win'))
        Reject 'other-platform' 'native plugin importer differs' { Get-YLRegistryPayload $bytes $catalog }
        $bytes[$importer] = $meta
        $bytes.Add('Runtime/ylresource_tink.dll', $original)
        Reject 'other-path' 'Unapproved bundled DLL' { Get-YLRegistryPayload $bytes $catalog }
        $stock.Add($native, $original)
        Reject 'other-package' 'Unapproved bundled DLL' { Get-YLRegistryPayload $stock $catalog }
    } finally { $null = $bytes.Remove('Runtime/ylresource_tink.dll'); $null = $stock.Remove($native); $bytes[$native] = $original; $bytes[$importer] = $meta }

    $node = Get-YLTestPath (Join-Path $NodeRoot 'node.exe'); $npm = Get-YLTestPath (Join-Path $NodeRoot 'node_modules/npm/bin/npm-cli.js')
    $envRoot = Join-Path $out 'Environment'; $null = [IO.Directory]::CreateDirectory($envRoot)
    foreach ($name in @('empty.gitconfig','user.npmrc','global.npmrc')) { [IO.File]::WriteAllText((Join-Path $envRoot $name), '', [Text.UTF8Encoding]::new($false)) }
    $receipt['node'] = (Invoke-YLRegistryProcess -Exe $node -Arguments @('--version') -Directory $envRoot -Log (Join-Path $out 'node.log') -EnvironmentRoot $envRoot).Trim()
    $receipt['npm'] = (Invoke-YLRegistryProcess -Exe $node -Arguments @($npm,'--version') -Directory $envRoot -Log (Join-Path $out 'npm.log') -EnvironmentRoot $envRoot).Trim()
    $receipt['nodeSha256'] = (Get-FileHash -LiteralPath $node -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($receipt.node -cne 'v22.23.3' -or $receipt.npm -cne '10.9.9' -or $receipt.nodeSha256 -cne '9c9245166b4a8e182e0b797da9c20136117ff24368eaff1fec8343a123c8db0e') { throw 'Unexpected pinned offline package toolchain.' }
    foreach ($pass in @('pack','repack')) {
        $dest = Join-Path $out $pass; $null = [IO.Directory]::CreateDirectory($dest)
        foreach ($package in $profiles) {
            $run = Invoke-YLRegistryProcess -Exe $node -Arguments @($npm,'pack','--ignore-scripts','--offline','--json','--pack-destination',$dest) `
                -Directory $package.root -Log (Join-Path $out "$($package.id)-$pass.log") -EnvironmentRoot $envRoot
            $entry = ConvertFrom-Json -InputObject $run -AsHashtable -NoEnumerate
            $name = $package.id + '-' + $package.version + '.tgz'
            $result = Read-YLRegistryTarball -Path (Join-Path $dest $name) -Catalog $catalog
            if ($entry.Count -ne 1 -or $entry[0].filename -cne $name -or $entry[0].integrity -cne $result.integrity -or
                $result.payloadSha256 -cne $package.payloadSha256 -or $result.files.Count -ne $package.files.Count) { throw 'Archive drift from the validated source.' }
            if ($pass -ceq 'pack') { $receipt.artifacts[$package.id] = $result }
            elseif ($receipt.artifacts[$package.id].sha256 -cne $result.sha256) { throw 'The separate offline repack differs.' }
        }
    }
    $receipt['pluginSha256'] = Hex $original
    $receipt['importerSha256'] = Hex $meta
    $receipt.status = 'Packed'
    "Frozen local archives: $out\local-pack.json"
} catch { $receipt['failure'] = $_.Exception.Message; throw }
finally { Write-YLTestJson -Path (Join-Path $out 'local-pack.json') -Value $receipt }
