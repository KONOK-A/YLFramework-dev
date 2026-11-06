#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param(
    [string] $TemporaryRoot = [IO.Path]::GetTempPath(),
    [ValidateNotNullOrEmpty()][string] $NodeRoot
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Common/Process.ps1')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$builder = Join-Path $repository 'Tools/Packaging/Build-YLRegistryPackages.ps1'
if (-not [IO.File]::Exists($builder)) { throw 'RED: registry packaging implementation is missing; the frozen payload contract cannot run.' }
. $builder
$parent = Get-YLTestPath $TemporaryRoot
if (-not [IO.Directory]::Exists($parent)) { throw 'TemporaryRoot must exist.' }
$sandbox = Join-Path $parent ('yl-registry-' + [Guid]::NewGuid().ToString('N'))
$workspace = Initialize-YLTestWorkspace -WorkspaceRoot $sandbox -Repository $repository
$run = New-YLTestDirectory -Workspace $workspace -Kind Result -Key Contracts
$lease = Lock-YLTestDirectory -Workspace $workspace -Path $run.path
$checks = 0
$failures = [Collections.Generic.List[string]]::new()
function Check([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Reject([scriptblock] $Action, [string] $Pattern) {
    $rejected = $false
    try { $null = & $Action } catch {
        if ($_.Exception -is [Management.Automation.CommandNotFoundException] -or $_.Exception.Message -notmatch $Pattern) { throw }
        $rejected = $true
    }
    Check $rejected "Expected rejection: $Pattern"
}
function Case([string] $Name, [scriptblock] $Body) {
    $script:checks++
    try { & $Body; "PASS: $Name" } catch { $failures.Add("${Name}: $($_.Exception.Message)"); "FAIL: ${Name}: $($_.Exception.Message)" }
}
function Save([string] $Path, [string] $Text) { [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false)) }
function Mutate([string] $Relative, [scriptblock] $Action, [scriptblock] $Assert) {
    $path = Join-Path $sources $Relative
    $bytes = [IO.File]::ReadAllBytes($path)
    try { & $Action $path; & $Assert } finally { [IO.File]::WriteAllBytes($path, $bytes) }
}
function With-FixtureAsset([string] $Relative, [scriptblock] $Action) {
    $root = Join-Path $sources 'com.kano.ylframework'
    $path = Join-Path $root $Relative
    $files = [Collections.Generic.List[string]]::new()
    $directories = [Collections.Generic.List[string]]::new()
    function Add-FixtureMeta([string] $Target, [bool] $Folder) {
        $guid = [Guid]::NewGuid().ToString('N')
        $text = "fileFormatVersion: 2`nguid: $guid`n"
        if ($Folder) { $text += "folderAsset: yes`n" }
        if (-not $Folder -and $Target.EndsWith('.cs', [StringComparison]::Ordinal)) {
            $text += "MonoImporter:`n  externalObjects: {}`n  serializedVersion: 2`n  defaultReferences: []`n  executionOrder: 0`n  icon: {instanceID: 0}`n"
        } else { $text += "DefaultImporter:`n  externalObjects: {}`n" }
        $text += "  userData: `n  assetBundleName: `n  assetBundleVariant: `n"
        Save ($Target + '.meta') $text
        $files.Add($Target + '.meta')
    }
    try {
        $cursor = $root
        $parts = $Relative.Split('/')
        for ($i = 0; $i -lt $parts.Length - 1; $i++) {
            $cursor = Join-Path $cursor $parts[$i]
            if (-not [IO.Directory]::Exists($cursor)) {
                $null = [IO.Directory]::CreateDirectory($cursor)
                $directories.Add($cursor)
                if ([IO.File]::Exists($cursor + '.meta')) { throw 'Fixture must not overwrite an existing folder meta.' }
                Add-FixtureMeta $cursor $true
            }
        }
        if ([IO.Path]::Exists($path) -or [IO.Path]::Exists($path + '.meta')) { throw 'Fixture must not overwrite a product asset.' }
        Save $path '// workspace-only packaging boundary fixture'
        $files.Add($path)
        Add-FixtureMeta $path $false
        & $Action
    } finally {
        foreach ($file in $files) { [IO.File]::Delete($file) }
        for ($i = $directories.Count - 1; $i -ge 0; $i--) { [IO.Directory]::Delete($directories[$i]) }
    }
}
function Fixture-Tar([string] $Id, [string] $Fault = '') {
    $root = Join-Path $sources $Id
    $path = Join-Path $run.path (([Guid]::NewGuid().ToString('N')) + '.tgz')
    $file = [IO.File]::Create($path)
    $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionLevel]::Fastest, $false)
    $writer = [System.Formats.Tar.TarWriter]::new($gzip, [System.Formats.Tar.TarEntryFormat]::Pax, $false)
    try {
        foreach ($source in @(Get-YLTestTreeFiles $root)) {
            $relative = [IO.Path]::GetRelativePath($root, $source).Replace('\', '/')
            if ($relative -ceq '.gitattributes') { continue }
            $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, "package/$relative")
            $stream = [IO.File]::OpenRead($source)
            try { $entry.DataStream = $stream; $writer.WriteEntry($entry) } finally { $stream.Dispose() }
        }
        if ($Fault) {
            $name = switch ($Fault) {
                traversal { 'package/../escape' }; absolute { '/package/escape' }; drive { 'package/C:/escape' }
                backslash { 'package/Runtime\escape' }; duplicate { 'package/package.json' }
                case { 'package/PACKAGE.JSON' }; reserved { 'package/Runtime/NUL.txt' }
                trailing { 'package/Runtime/escape.' }; prefix { 'outside/escape' }
                collision { 'package/Editor' }; 'empty-host' { 'package/Tests/' }; default { 'package/unsafe' }
            }
            $type = switch ($Fault) {
                symlink { [System.Formats.Tar.TarEntryType]::SymbolicLink }
                hardlink { [System.Formats.Tar.TarEntryType]::HardLink }
                fifo { [System.Formats.Tar.TarEntryType]::Fifo }
                'empty-host' { [System.Formats.Tar.TarEntryType]::Directory }
                default { [System.Formats.Tar.TarEntryType]::RegularFile }
            }
            $entry = [System.Formats.Tar.PaxTarEntry]::new($type, $name)
            if ($Fault -in @('symlink', 'hardlink')) { $entry.LinkName = '../../escape' }
            $stream = [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes('unsafe'))
            try { if ($type -eq [System.Formats.Tar.TarEntryType]::RegularFile) { $entry.DataStream = $stream }; $writer.WriteEntry($entry) }
            finally { $stream.Dispose() }
        }
    } finally { $writer.Dispose(); $gzip.Dispose(); $file.Dispose() }
    return $path
}
try {
    $sources = Join-Path $run.path 'PackageSources'
    $null = [IO.Directory]::CreateDirectory($sources)
    foreach ($source in @(Get-YLTestTreeFiles (Join-Path $repository 'PackageSources'))) {
        $relative = [IO.Path]::GetRelativePath((Join-Path $repository 'PackageSources'), $source)
        $target = Join-Path $sources $relative
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        [IO.File]::Copy($source, $target)
    }
    Case 'All catalog source packages have explicit complete publish inputs' {
        $set = @(Get-YLRegistryPackageSet -Root $sources)
        Check ($set.Count -eq (Get-YLRegistryPackageIds).Count) 'Expected the complete catalog package batch.'
        Check (@($set | Where-Object version -CNE '0.1.0-preview.1').Count -eq 0) 'The first preview changed.'
    }
    Case 'Independent fixed-commit checkout never copies the current dirty source tree' {
        $git = (Get-Command -Name git.exe -CommandType Application -TotalCount 1 -ErrorAction Stop).Source
        $commit = (& $git -C $repository rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the read-only source commit.' }
        $environment = Join-Path $run.path 'checkout-environment'; $null = [IO.Directory]::CreateDirectory($environment)
        foreach ($name in @('empty.gitconfig', 'user.npmrc', 'global.npmrc')) { Save (Join-Path $environment $name) '' }
        $checkout = Join-Path $run.path 'fixed-checkout'
        $actual = New-YLRegistryCheckout -Repository $repository -SourceCommit $commit -Directory $checkout -EnvironmentRoot $environment -LogRoot $run.path
        Check ($actual -ceq $checkout) 'The checkout path changed.'
        $committed = & $git -C $repository show ($commit + ':PackageSources/com.kano.ylframework/package.json')
        if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the committed package manifest.' }
        $expected = ($committed -join "`n").Trim()
        $observed = [IO.File]::ReadAllText((Join-Path $checkout 'PackageSources/com.kano.ylframework/package.json')).Trim()
        Check ($observed -ceq $expected) 'The checkout copied uncommitted package metadata.'
        $bytes = [IO.File]::ReadAllBytes((Join-Path $checkout 'PackageSources/com.kano.ylframework.excel/Editor/Plugins/MiniExcel.dll'))
        Check ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() -ceq '0a49b66224bef9fdcec12800e4660267a993b1ece87727d9fe15c7dabf58ba1e') 'Independent checkout did not materialize the genuine LFS DLL.'
        $readOnlyPack = Join-Path $checkout '.git/objects/pack/owned-cleanup-fixture.idx'
        Save $readOnlyPack 'Test-only Git object attribute fixture, not a valid pack index.'
        [IO.File]::SetAttributes($readOnlyPack, [IO.FileAttributes]::ReadOnly)
        # This clone uses --no-hardlinks; change only its private Git metadata before removal.
        foreach ($file in @(Get-YLTestTreeFiles (Join-Path $checkout '.git'))) {
            $attributes = [IO.File]::GetAttributes($file)
            if ($attributes -band [IO.FileAttributes]::ReadOnly) { [IO.File]::SetAttributes($file, ($attributes -band (-bnot [IO.FileAttributes]::ReadOnly))) }
        }
        [IO.Directory]::Delete($checkout, $true)
        Check (-not [IO.Directory]::Exists($checkout)) 'The independent fixture checkout was not actually removed.'
    }
    Case 'Missing package is rejected' {
        $path = Join-Path $sources 'com.kano.ylframework.excel/package.json'
        $moved = $path + '.saved'; [IO.File]::Move($path, $moved)
        try { Reject { Get-YLRegistryPackageSet -Root $sources } 'missing|manifest' } finally { [IO.File]::Move($moved, $path) }
    }
    foreach ($fault in @('version', 'dependency', 'optional-main', 'sdk-version', 'lifecycle', 'files')) {
        Case "Reject $fault metadata" {
            $id = if ($fault -eq 'optional-main') { 'com.kano.ylframework' } elseif ($fault -eq 'sdk-version') { 'com.kano.ylframework.hybridclr' } else { 'com.kano.ylframework.excel' }
            Mutate "$id/package.json" {
                param($path)
                $json = [IO.File]::ReadAllText($path) | ConvertFrom-Json -AsHashtable
                switch ($fault) {
                    version { $json.version = '0.1.0-preview.2' }
                    dependency { $json.dependencies['com.kano.ylframework'] = '^0.1.0-preview.1' }
                    'optional-main' { $json.dependencies['com.kano.ylframework.excel'] = $json.version }
                    'sdk-version' { $json.dependencies['com.code-philosophy.hybridclr'] = '9.0.2' }
                    lifecycle { $json.scripts = @{ prepare = 'node hostile.js' } }
                    files { $json.files = @('README.md') }
                }
                Save $path (ConvertTo-Json -InputObject $json -Depth 30)
            } { Reject { Get-YLRegistryPackageSet -Root $sources } 'version|dependency|SemVer|SDK|lifecycle|files|whitelist' }
        }
    }
    Case 'Local package versions come from synchronized manifests, not a first-release constant' {
        $originals = @{}
        $packages = @(Get-YLRegistryPackageSet -Root $sources)
        try {
            foreach ($package in $packages) {
                $path = Join-Path $package.root 'package.json'
                $originals[$path] = [IO.File]::ReadAllBytes($path)
                $json = [IO.File]::ReadAllText($path) | ConvertFrom-Json -AsHashtable
                $json.version = '0.1.0-preview.2'
                foreach ($id in @($json.dependencies.Keys)) {
                    if ($id.StartsWith('com.kano.ylframework', [StringComparison]::Ordinal)) { $json.dependencies[$id] = $json.version }
                }
                Save $path (ConvertTo-Json -InputObject $json -Depth 30)
                $package.version = $json.version
                $package.dependencies = $json.dependencies
                $package | Add-Member -NotePropertyName sha256 -NotePropertyValue ('a' * 64)
                $package | Add-Member -NotePropertyName integrity -NotePropertyValue ('sha512-' + [Convert]::ToBase64String([byte[]]::new(64)))
            }
            $updated = @(Get-YLRegistryPackageSet -Root $sources)
            Check ($updated.Count -eq $packages.Count -and @($updated | Where-Object version -CNE '0.1.0-preview.2').Count -eq 0) 'Synchronized manifest versions were replaced by a tool constant.'
            $receipt = New-YLRegistryReleaseManifest -SourceCommit ('a' * 40) -Toolchain @{ node = '22.23.3'; npm = '10.9.9'; powershell = '7.6.9' } -Packages $packages
            Check (@($receipt.packages | Where-Object version -CNE '0.1.0-preview.2').Count -eq 0) 'The local receipt lost the actual package versions.'
        } finally { foreach ($path in $originals.Keys) { [IO.File]::WriteAllBytes($path, $originals[$path]) } }
    }
    foreach ($fault in @('duplicate', 'nested-duplicate', 'trailing', 'comment', 'trailing-comma', 'fingerprint')) {
        Case "Strict JSON rejects $fault" {
            Mutate 'com.kano.ylframework.excel/package.json' {
                param($path)
                $text = [IO.File]::ReadAllText($path)
                $text = switch ($fault) {
                    duplicate { $text.Replace('"name":', '"name":"duplicate","name":') }
                    'nested-duplicate' { $text.Replace('"name": "Kano"', '"name":"Kano","name":"Other"') }
                    trailing { $text + '{}' }
                    comment { '/* not JSON */' + $text }
                    'trailing-comma' { $text.TrimEnd().TrimEnd('}') + ',}' }
                    fingerprint { $text.Replace('"name":', '"_fingerprint":"0123456789abcdef0123456789abcdef01234567","name":') }
                }
                Save $path $text
            } { Reject { Get-YLRegistryPackageSet -Root $sources } 'JSON|duplicate|fingerprint|managed' }
        }
    }
    foreach ($relative in @(
        'com.kano.ylframework/LICENSE.md',
        'com.kano.ylframework/Plugins/BouncyCastle/SOURCE.txt',
        'com.kano.ylframework.excel/Editor/Licenses/MiniExcel-LICENSE.txt',
        'com.kano.ylframework.excel/Editor/Licenses/MiniExcel-ATTRIBUTION.txt',
        'com.kano.ylframework/Prefabs/YLFramework.prefab',
        'com.kano.ylframework/Prefabs/YLFramework.prefab.meta',
        'com.kano.ylframework/Runtime/Core/Resources/YLFramework/Core/Canvas.prefab.meta',
        'com.kano.ylframework/Runtime/Core/Analyzers/YLFramework.Analyzers.dll',
        'com.kano.ylframework/Samples~/ResourceStartup/Runtime/ResourceStartupExample.cs',
        'com.kano.ylframework.hybridclr/Samples~/HybridCLR/Host/HybridCLRExample.cs')) {
        Case "Required payload cannot disappear: $relative" {
            $path = Join-Path $sources $relative; $backup = Join-Path $run.path 'removed'
            [IO.File]::Move($path, $backup)
            try { Reject { Get-YLRegistryPackageSet -Root $sources } 'missing|required|meta|sample|license|whitelist' }
            finally { [IO.File]::Move($backup, $path) }
        }
    }
    foreach ($text in @('version https://git-lfs.github.com/spec/v1', 'MZ not actually a PE', 'not a DLL')) {
        Case "Reject unmaterialized or non-PE DLL: $text" {
            Mutate 'com.kano.ylframework.excel/Editor/Plugins/MiniExcel.dll' { param($path) Save $path $text } {
                Reject { Get-YLRegistryPackageSet -Root $sources } 'LFS|PE|DLL|binary'
            }
        }
    }
    Case 'Duplicate GUID across packages is rejected' {
        $guid = [IO.File]::ReadAllText((Join-Path $sources 'com.kano.ylframework/package.json.meta'))
        Mutate 'com.kano.ylframework.excel/package.json.meta' { param($path) Save $path $guid } {
            Reject { Get-YLRegistryPackageSet -Root $sources } 'GUID'
        }
    }
    foreach ($relative in @('Runtime/DOTween.dll', 'Runtime/HybridCLR.Runtime.dll', 'Runtime/SDK/Secret.cs', 'Tests/Hidden.cs', '.npmrc', 'Runtime/private.key')) {
        Case "Reject forbidden input: $relative" {
            $path = Join-Path $sources "com.kano.ylframework/$relative"
            $directory = [IO.Path]::GetDirectoryName($path)
            $newDirectory = -not [IO.Directory]::Exists($directory)
            $null = [IO.Directory]::CreateDirectory($directory); Save $path 'hostile'
            try { Reject { Get-YLRegistryPackageSet -Root $sources } 'forbidden|unauthorized|SDK|whitelist|meta' }
            finally { [IO.File]::Delete($path); if ($newDirectory) { [IO.Directory]::Delete($directory) } }
        }
    }
    foreach ($relative in @('Runtime/Tests/Hidden.cs', 'Editor/Tests/Hidden.cs', 'Runtime/Library/Hidden.cs', 'Runtime/Temp/Hidden.cs',
        'Runtime/obj/Hidden.cs', 'Runtime/bin/Hidden.cs', 'Runtime/node_modules/Hidden.cs', 'Runtime/TestResults/Hidden.cs',
        'Runtime/tool.exe', 'Runtime/cache.zip', 'Runtime/private.private', 'Runtime/package-lock.json', 'Runtime/npm-shrinkwrap.json',
        'Runtime/yarn.lock', 'Runtime/pnpm-lock.yaml')) {
        Case "Reject source and tar contamination with complete asset/folder meta: $relative" {
            With-FixtureAsset $relative {
                $tar = Fixture-Tar 'com.kano.ylframework'
                $destination = Join-Path $run.path ('forbidden-' + [Guid]::NewGuid().ToString('N'))
                Reject { Read-YLRegistryTarball -Path $tar -Destination $destination } 'forbidden|unauthorized|cache|artifact'
                Check (-not [IO.Path]::Exists($destination)) 'Forbidden input was extracted before its rejection.'
                Reject { Get-YLRegistryPackageSet -Root $sources } 'forbidden|unauthorized|cache|artifact'
            }
        }
    }
    foreach ($relative in @('Runtime/tool.ps1', 'Runtime/tool.bat', 'Runtime/tool.cmd', 'Runtime/tool.py', 'Runtime/tool.sh',
        'Runtime/cache.7z', 'Runtime/cache.nupkg', 'Runtime/cache.msi', 'Runtime/cache.tar', 'Runtime/cache.rar', 'Runtime/cache.gz',
        'Runtime/private.ppk', 'Runtime/private.jks', 'Runtime/private.keystore')) {
        Case "Reject source host script/archive/key with complete meta: $relative" {
            With-FixtureAsset $relative {
                Reject { Get-YLRegistryPackageSet -Root $sources } 'forbidden|unauthorized|cache|artifact'
            }
        }
        Case "Reject tar host script/archive/key with complete meta before extraction: $relative" {
            With-FixtureAsset $relative {
                $tar = Fixture-Tar 'com.kano.ylframework'
                $destination = Join-Path $run.path ('forbidden-' + [Guid]::NewGuid().ToString('N'))
                Reject { Read-YLRegistryTarball -Path $tar -Destination $destination } 'forbidden|unauthorized|cache|artifact'
                Check (-not [IO.Path]::Exists($destination)) 'Forbidden host script/archive/key was extracted before rejection.'
            }
        }
    }
    Case 'Legitimate nested Tools and Settings remain product-owned inputs' {
        With-FixtureAsset 'Editor/Tools/RegistryPositive.cs' {
            With-FixtureAsset 'Editor/Settings/RegistryPositive.cs' {
                $package = @((Get-YLRegistryPackageSet -Root $sources) | Where-Object id -CEQ 'com.kano.ylframework')[0]
                $payload = Read-YLRegistryTarball -Path (Fixture-Tar 'com.kano.ylframework')
                Check ($package.payloadSha256 -ceq $payload.payloadSha256) 'Product authoring tools/settings were removed from the payload.'
                foreach ($path in @('Editor/Tools/OpenFolder.cs', 'Editor/Tools/ClearLog.cs', 'Editor/Tools/RegistryPositive.cs', 'Editor/Settings/RegistryPositive.cs')) {
                    Check ($path -cin $payload.files.path -and ($path + '.meta') -cin $payload.files.path) "Legitimate authoring input was rejected: $path"
                }
            }
        }
    }
    Case 'Safe tar reader returns real hashes and extracts only after complete validation' {
        $tar = Fixture-Tar 'com.kano.ylframework.excel'
        $destination = Join-Path $run.path 'extracted'
        $payload = Read-YLRegistryTarball -Path $tar -Destination $destination
        Check ($payload.id -ceq 'com.kano.ylframework.excel' -and $payload.version -ceq '0.1.0-preview.1') 'Wrong tar identity.'
        Check ($payload.sha256 -ceq (Get-FileHash -LiteralPath $tar -Algorithm SHA256).Hash.ToLowerInvariant()) 'Tar SHA256 differs.'
        $sri = 'sha512-' + [Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData([IO.File]::ReadAllBytes($tar)))
        Check ($payload.integrity -ceq $sri) 'Tar SHA512 SRI differs.'
        $text = [Text.StringBuilder]::new()
        [string[]]$paths = @($payload.files.path); [Array]::Sort($paths, [StringComparer]::Ordinal)
        Check (($paths -join '|') -ceq ($payload.files.path -join '|')) 'Files are not ordinal sorted.'
        foreach ($row in $payload.files) {
            $raw = (Get-FileHash -LiteralPath (Join-Path $destination $row.path) -Algorithm SHA256).Hash.ToLowerInvariant()
            Check ($row.sha256 -ceq $raw) 'Raw bytes hash was normalized.'
            if ($row.path -cne 'package.json') { Check ($row.contentSha256 -ceq $raw) 'Ordinary file hash was normalized.' }
            $null = $text.Append($row.path).Append([char]0).Append($row.contentSha256).Append("`n")
        }
        $expected = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text.ToString()))).ToLowerInvariant()
        Check ($payload.payloadSha256 -ceq $expected) 'Payload hash is not path-NUL-hash-LF.'
        $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-YLDistribution.ps1'), [ref]$null, [ref]$null)
        $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Get-DistributionPackageContent' }, $true)
        . ([scriptblock]::Create($function.Extent.Text))
        Check ((Get-DistributionPackageContent -Root $destination).sha256 -ceq $payload.payloadSha256) 'Distribution content compatibility failed.'
        Check (-not [IO.File]::Exists((Join-Path $destination '.gitattributes'))) 'Source-only Git LFS rules became a tar prerequisite.'
        Reject { Read-YLRegistryTarball -Path $tar -Destination $destination } 'empty|exist|overwrite'
    }
    foreach ($fault in @('traversal', 'absolute', 'drive', 'backslash', 'duplicate', 'case', 'reserved', 'trailing', 'prefix', 'collision', 'symlink', 'hardlink', 'fifo', 'empty-host')) {
        Case "Tar audit rejects $fault before extraction" {
            $tar = Fixture-Tar 'com.kano.ylframework.excel' $fault
            $destination = Join-Path $run.path ('rejected-' + $fault)
            Reject { Read-YLRegistryTarball -Path $tar -Destination $destination } 'tar|path|link|duplicate|collision|entry|reserved'
            Check (-not [IO.Directory]::Exists($destination)) 'Unsafe tar created extraction output.'
        }
    }
    Case 'Truncated gzip is rejected' {
        $tar = Fixture-Tar 'com.kano.ylframework.excel'
        $stream = [IO.File]::OpenWrite($tar); try { $stream.SetLength(40) } finally { $stream.Dispose() }
        Reject { Read-YLRegistryTarball -Path $tar } 'tar|gzip|archive|end|data'
    }
    Case 'Corrupt tar header checksum is rejected' {
        $path = Fixture-Tar 'com.kano.ylframework.excel'
        $input = [IO.File]::OpenRead($path); $gzip = [IO.Compression.GZipStream]::new($input, [IO.Compression.CompressionMode]::Decompress)
        $memory = [IO.MemoryStream]::new()
        try { $gzip.CopyTo($memory); $bytes = $memory.ToArray() } finally { $gzip.Dispose(); $input.Dispose(); $memory.Dispose() }
        $bytes[148] = if ($bytes[148] -eq 48) { 49 } else { 48 }
        $output = [IO.File]::Create($path); $gzip = [IO.Compression.GZipStream]::new($output, [IO.Compression.CompressionLevel]::Fastest)
        try { $gzip.Write($bytes) } finally { $gzip.Dispose(); $output.Dispose() }
        Reject { Read-YLRegistryTarball -Path $path } 'tar|checksum|archive'
    }
    Case 'Compressed archive at the 512 MiB limit is rejected before allocation' {
        $path = Join-Path $run.path 'oversized.tgz'
        $stream = [IO.File]::Create($path); try { $stream.SetLength(512MB) } finally { $stream.Dispose() }
        try { Reject { Read-YLRegistryTarball -Path $path } 'size|512' } finally { [IO.File]::Delete($path) }
    }
    Case 'Oversized tar entry header is rejected before reading its data' {
        $path = Join-Path $run.path 'oversized-entry.tgz'
        $header = [byte[]]::new(512)
        foreach ($field in @(@(0, 'package/oversized'), @(100, '0000644'), @(108, '0000000'), @(116, '0000000'),
            @(124, '04000000000'), @(136, '00000000000'), @(257, 'ustar'), @(263, '00'))) {
            [Text.Encoding]::ASCII.GetBytes($field[1]).CopyTo($header, $field[0])
        }
        $header[156] = 48
        for ($i = 148; $i -lt 156; $i++) { $header[$i] = 32 }
        $sum = 0; foreach ($value in $header) { $sum += $value }
        [Text.Encoding]::ASCII.GetBytes([Convert]::ToString($sum, 8).PadLeft(6, '0')).CopyTo($header, 148)
        $header[154] = 0
        $stream = [IO.File]::Create($path); $gzip = [IO.Compression.GZipStream]::new($stream, [IO.Compression.CompressionLevel]::Fastest)
        try { $gzip.Write($header); $gzip.Write([byte[]]::new(1024)) } finally { $gzip.Dispose(); $stream.Dispose() }
        Reject { Read-YLRegistryTarball -Path $path } 'size|512'
    }
    Case 'Release manifest uses the agreed schema and byte-exact file records' {
        $packages = @(foreach ($id in (Get-YLRegistryPackageIds)) { Read-YLRegistryTarball -Path (Fixture-Tar $id) })
        $manifest = New-YLRegistryReleaseManifest -SourceCommit ('a' * 40) -Toolchain @{ node = '22.23.3'; npm = '10.9.9'; powershell = '7.6.9' } -Packages $packages
        $path = Join-Path $run.path 'fixture-release-manifest.json'
        Write-YLTestJson -Path $path -Value $manifest
        $wire = [IO.File]::ReadAllText($path) | ConvertFrom-Json -AsHashtable -DateKind String
        Check ($wire.schemaVersion -eq 1 -and $wire.sourceCommit -ceq ('a' * 40) -and $wire.packages.Count -eq (Get-YLRegistryPackageIds).Count) 'Wrong manifest schema/source/complete catalog batch.'
        Check ($wire.toolchain.node -ceq '22.23.3' -and $wire.toolchain.npm -ceq '10.9.9' -and $wire.toolchain.powershell -ceq '7.6.9') 'Unpinned toolchain.'
        foreach ($row in $wire.packages) {
            Check ($row.assetName -ceq ($row.id + '-' + $row.version + '.tgz')) 'Wrong stable asset filename.'
            $actual = @($packages | Where-Object id -CEQ $row.id)[0]
            Check ($row.sha256 -ceq $actual.sha256 -and $row.integrity -ceq $actual.integrity -and $row.payloadSha256 -ceq $actual.payloadSha256) 'Manifest changed verified digests.'
            Check ($row.files.Count -eq $actual.files.Count -and $row.dependencies.Count -eq $actual.dependencies.Count) 'Manifest dropped files/dependencies.'
            Check (@($row.files | Where-Object { $_.Keys.Count -ne 3 }).Count -eq 0) 'File rows must contain only path/sha256/contentSha256.'
        }
        Reject { New-YLRegistryReleaseManifest -SourceCommit ('A' * 40) -Toolchain @{ node = '22.23.3'; npm = '10.9.9'; powershell = '7.6.9' } -Packages $packages } 'commit|hex'
        Reject { New-YLRegistryReleaseManifest -SourceCommit ('a' * 40) -Toolchain @{ node = '24.22.2'; npm = '10.9.9'; powershell = '7.6.9' } -Packages $packages } 'toolchain|Node'
        Reject { New-YLRegistryReleaseManifest -SourceCommit ('a' * 40) -Toolchain @{ node = '22.23.3'; npm = '10.9.9'; powershell = '7.6.9' } -Packages @($packages[0]) } 'five|batch'
    }
    Case 'Producer never invents CI success and requires complete run identity' {
        $names = @('GITHUB_ACTIONS', 'GITHUB_REPOSITORY', 'GITHUB_WORKFLOW', 'GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT')
        $saved = @{}; foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
        try {
            $env:GITHUB_ACTIONS = ''
            Check ((Get-YLRegistryProducer).kind -ceq 'Local') 'Local build masquerades as CI.'
            $env:GITHUB_ACTIONS = 'true'; $env:GITHUB_REPOSITORY = 'KONOK-A/YLFramework-dev'; $env:GITHUB_WORKFLOW = 'Fixture'; $env:GITHUB_RUN_ID = '123'; $env:GITHUB_RUN_ATTEMPT = '2'
            $producer = Get-YLRegistryProducer
            Check ($producer.kind -ceq 'GitHubActions' -and $producer.runId -ceq '123' -and $producer.runAttempt -ceq '2' -and $producer.Count -eq 5) 'Wrong producer identity or invented status.'
            $env:GITHUB_RUN_ATTEMPT = ''
            Reject { Get-YLRegistryProducer } 'producer|identity'
        } finally { foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) } }
    }
    Case 'Packaging subprocesses use only owned npm configuration and cache' {
        $names = @('NPM_CONFIG_USERCONFIG', 'NPM_CONFIG_GLOBALCONFIG', 'NPM_CONFIG_CACHE', 'NPM_TOKEN', 'NODE_OPTIONS', 'GIT_DIR')
        $saved = @{}; foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
        try {
            foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, 'untrusted-fixture-value') }
            $environment = Join-Path $run.path 'checkout-environment'
            $code = '[ordered]@{user=$env:NPM_CONFIG_USERCONFIG; global=$env:NPM_CONFIG_GLOBALCONFIG; cache=$env:NPM_CONFIG_CACHE; token=$env:NPM_TOKEN; node=$env:NODE_OPTIONS; git=$env:GIT_DIR; scripts=$env:NPM_CONFIG_IGNORE_SCRIPTS; home=$env:USERPROFILE} | ConvertTo-Json -Compress'
            $text = Invoke-YLRegistryProcess -Exe (Get-Process -Id $PID).Path -Arguments @('-NoLogo', '-NoProfile', '-NonInteractive', '-Command', $code) -Directory $environment -Log (Join-Path $run.path 'environment-probe.log') -EnvironmentRoot $environment
            $actual = $text | ConvertFrom-Json -AsHashtable
            Check ($actual.user -ceq (Join-Path $environment 'user.npmrc') -and $actual.global -ceq (Join-Path $environment 'global.npmrc') -and $actual.cache -ceq (Join-Path $environment 'npm-cache')) 'Subprocess inherited npm configuration/cache.'
            Check (-not $actual.token -and -not $actual.node -and -not $actual.git -and $actual.scripts -ceq 'true' -and $actual.home -ceq $environment) 'Subprocess inherited credentials/options/source overrides.'
        } finally { foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) } }
    }
    Case 'Manifest normalization recursively sorts ordinal properties without normalizing strings' {
        $bytes = [Text.Encoding]::UTF8.GetBytes('{"z":{"b":1,"A":{"y":false,"Z":true}},"a":[{"z":"2026-01-01T00:00:00Z","A":"<"}]}')
        $json = Read-YLRegistryJson $bytes
        Check ($json.canonical -ceq '{"a":[{"A":"<","z":"2026-01-01T00:00:00Z"}],"z":{"A":{"Z":true,"y":false},"b":1}}') 'Recursive canonical JSON differs from Distribution semantics.'
        foreach ($name in @('_fingerprint', '_resolved', 'gitHead', 'sourceCommit')) {
            Reject { Read-YLRegistryJson ([Text.Encoding]::UTF8.GetBytes('{"' + $name + '":"' + ('a' * 40) + '"}')) } 'managed|commit|metadata'
        }
    }
    Case 'Approved analyzer path cannot hide a different managed SDK assembly' {
        $other = [IO.File]::ReadAllBytes((Join-Path $sources 'com.kano.ylframework.excel/Editor/Plugins/MiniExcel.dll'))
        Mutate 'com.kano.ylframework/Runtime/Core/Analyzers/YLFramework.Analyzers.dll' { param($path) [IO.File]::WriteAllBytes($path, $other) } {
            Reject { Get-YLRegistryPackageSet -Root $sources } 'assembly|identity|DLL'
        }
    }
    Case 'Recursive manifest canonicalization preserves raw bytes but matches semantic content' {
        $first = Read-YLRegistryTarball -Path (Fixture-Tar 'com.kano.ylframework.excel')
        Mutate 'com.kano.ylframework.excel/package.json' {
            param($path)
            $json = [IO.File]::ReadAllText($path) | ConvertFrom-Json -AsHashtable
            $reverse = [ordered]@{}; [string[]]$names = @($json.Keys); [Array]::Sort($names, [StringComparer]::Ordinal); [Array]::Reverse($names)
            foreach ($name in $names) { $reverse[$name] = $json[$name] }
            Save $path (ConvertTo-Json -InputObject $reverse -Depth 30 -Compress)
        } {
            $second = Read-YLRegistryTarball -Path (Fixture-Tar 'com.kano.ylframework.excel')
            Check ($first.payloadSha256 -ceq $second.payloadSha256) 'Formatting changed semantics.'
            Check (($first.files | Where-Object path -CEQ 'package.json').sha256 -cne ($second.files | Where-Object path -CEQ 'package.json').sha256) 'Raw manifest hash ignored byte changes.'
        }
    }
    Case 'Output inside source repository is refused before tools or writes' {
        $output = Join-Path $repository 'registry-output-must-not-exist'
        Reject { Invoke-YLRegistryPackageBuild -Repository $repository -SourceCommit ('a' * 40) -OutputRoot $output } 'outside|source|repository'
        Check (-not [IO.Path]::Exists($output)) 'Invalid output root was created.'
    }
    Case 'Unknown existing output owner is never adopted' {
        Reject { Invoke-YLRegistryPackageBuild -Repository $repository -SourceCommit ('a' * 40) -OutputRoot $run.path } 'owner|workspace'
    }
    Case 'Non-lowercase or non-full source commit is refused' {
        foreach ($commit in @('main', ('A' * 40), ('a' * 39))) {
            Reject { Invoke-YLRegistryPackageBuild -Repository $repository -SourceCommit $commit -OutputRoot (Join-Path $run.path 'never') } 'commit|40|hex'
        }
    }
    if ($NodeRoot) {
        Case 'Workspace-only npm diagnostic verifies fixed tools and two real offline packs of all five fixture packages' {
            $diagnostic = New-YLTestDirectory -Workspace $workspace -Kind Result -Key WorkspaceNpmDiagnostic
            $diagnosticLease = Lock-YLTestDirectory -Workspace $workspace -Path $diagnostic.path
            try {
                $toolRoot = Get-YLTestPath $NodeRoot
                $node = Get-YLTestPath (Join-Path $toolRoot 'node.exe')
                $npm = Get-YLTestPath (Join-Path $toolRoot 'node_modules/npm/bin/npm-cli.js')
                Check ([IO.File]::Exists($node) -and [IO.File]::Exists($npm)) 'NodeRoot must contain node.exe and the bundled npm CLI; no PATH fallback is allowed.'
                $environment = Join-Path $diagnostic.path 'environment'
                $null = [IO.Directory]::CreateDirectory($environment)
                foreach ($name in @('empty.gitconfig', 'user.npmrc', 'global.npmrc')) { Save (Join-Path $environment $name) '' }
                $nodeVersion = (Invoke-YLRegistryProcess -Exe $node -Arguments @('--version') -Directory $environment -Log (Join-Path $diagnostic.path 'node-version.log') -EnvironmentRoot $environment).Trim()
                Check ($nodeVersion -ceq 'v22.23.3') "Workspace npm diagnostic requires Node.js 22.23.3; found $nodeVersion."
                $npmVersion = (Invoke-YLRegistryProcess -Exe $node -Arguments @($npm, '--version') -Directory $environment -Log (Join-Path $diagnostic.path 'npm-version.log') -EnvironmentRoot $environment).Trim()
                Check ($npmVersion -ceq '10.9.9') "Workspace npm diagnostic requires npm 10.9.9; found $npmVersion."
                $copyRoot = Join-Path $diagnostic.path 'PackageSources'
                $null = [IO.Directory]::CreateDirectory($copyRoot)
                foreach ($file in @(Get-YLTestTreeFiles $sources)) {
                    $target = Join-Path $copyRoot ([IO.Path]::GetRelativePath($sources, $file))
                    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
                    [IO.File]::Copy($file, $target, $false)
                }
                $packages = @(Get-YLRegistryPackageSet -Root $copyRoot)
                foreach ($pass in @('pack', 'repack')) { $null = [IO.Directory]::CreateDirectory((Join-Path $diagnostic.path $pass)) }
                foreach ($package in $packages) {
                    $first = $null
                    foreach ($pass in @('pack', 'repack')) {
                        $destination = Join-Path $diagnostic.path $pass
                        $log = Join-Path $diagnostic.path ($package.id + '-' + $pass + '.log')
                        $text = Invoke-YLRegistryProcess -Exe $node -Arguments @($npm, 'pack', '--ignore-scripts', '--offline', '--json', '--pack-destination', $destination) -Directory $package.root -Log $log -EnvironmentRoot $environment
                        $packed = ConvertFrom-Json -InputObject $text -AsHashtable -NoEnumerate -DateKind String
                        $assetName = $package.id + '-' + $package.version + '.tgz'
                        Check ($packed -is [array] -and $packed.Count -eq 1 -and $packed[0]['filename'] -ceq $assetName) 'Real npm pack produced an unexpected output filename.'
                        $payload = Read-YLRegistryTarball -Path (Join-Path $destination $assetName)
                        Check ($packed[0]['integrity'] -ceq $payload.integrity) 'npm integrity differs from the verified tarball bytes.'
                        Check ($payload.id -ceq $package.id -and $payload.version -ceq $package.version -and $payload.payloadSha256 -ceq $package.payloadSha256) "Real npm payload differs from workspace fixture inputs: $($package.id)"
                        Check ($payload.files.Count -eq $package.files.Count) 'Real npm pack changed the complete file set.'
                        for ($i = 0; $i -lt $package.files.Count; $i++) {
                            $expected = $package.files[$i]; $actual = $payload.files[$i]
                            Check ($actual.path -ceq $expected.path -and $actual.contentSha256 -ceq $expected.contentSha256) "Real npm pack changed a payload entry: $($expected.path)"
                            if ($expected.path -cne 'package.json') { Check ($actual.sha256 -ceq $expected.sha256) "Real npm pack changed original asset bytes: $($expected.path)" }
                        }
                        if ($null -eq $first) { $first = $payload } else { Check ($payload.payloadSha256 -ceq $first.payloadSha256) 'Two real npm packs have different semantic payloads.' }
                    }
                }
                foreach ($package in @(Get-YLRegistryPackageSet -Root $copyRoot)) {
                    $before = @($packages | Where-Object id -CEQ $package.id)[0]
                    Check ($package.payloadSha256 -ceq $before.payloadSha256 -and $package.files.Count -eq $before.files.Count) 'npm pack changed the source fixture.'
                    for ($i = 0; $i -lt $package.files.Count; $i++) {
                        Check ($package.files[$i].path -ceq $before.files[$i].path -and $package.files[$i].sha256 -ceq $before.files[$i].sha256) 'npm pack wrote source/package metadata or asset bytes.'
                    }
                }
                "NPM DIAGNOSTIC: Node $nodeVersion / npm $npmVersion; 5 workspace fixture packages packed twice with matching complete payloads. NOT a fixed-commit CI candidate, release, ReadyForPublish or Unity result."
            } finally {
                $diagnosticLease.Dispose()
                "Workspace-only npm diagnostic evidence retained: $($diagnostic.path)"
            }
        }
    } else {
        'NOT RUN: real npm diagnostic requires explicit -NodeRoot. Fixture tar contracts do not count as npm pack verification.'
    }
    if ($failures.Count) { throw "Registry package contracts failed ($($failures.Count)/$checks):`n$($failures -join "`n")" }
    "PASS: $checks registry package NoUnity contracts. Fixture tarballs alone are NOT npm pack verification; neither mode is Unity, Registry or publish acceptance."
} finally {
    $lease.Dispose()
    if ($failures.Count -eq 0) { $null = Remove-YLTestDirectory -Workspace $workspace -Path $run.path -Apply -Confirm:$false }
    else { "Failed contract evidence retained: $($run.path)" }
}
