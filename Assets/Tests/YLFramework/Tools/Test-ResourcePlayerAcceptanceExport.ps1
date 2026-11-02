#requires -Version 7.6
[CmdletBinding()]
param([Parameter(Mandatory)][string] $EvidenceRoot, [string] $BuildName = 'D1', [string] $ReceiptName = 'export-verification.json', [ValidateSet('Mono2x','IL2CPP')][string] $Backend = 'Mono2x')
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($EvidenceRoot)
$carrier = (Get-Content -LiteralPath (Join-Path $root 'carrier-location.json') -Raw | ConvertFrom-Json).Carrier
$identity = Get-Content -LiteralPath (Join-Path $carrier 'source-identity.json') -Raw | ConvertFrom-Json
$formal = @{}; $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$formalRoots = @('Assets/YLFramework/Editor','Assets/YLFramework/Plugins/BouncyCastle','Assets/YLFramework/Prefabs',
    'Assets/YLFramework/Runtime','Assets/YLFramework/Samples','Assets/YLFramework/Documentation','Assets/YLFramework/LICENSE.md')
foreach ($row in $identity | Where-Object {
    $path = $_.Path
    $path -ceq 'Assets/YLFramework.meta' -or @($formalRoots | Where-Object { $path -ceq $_ -or $path -ceq ($_ + '.meta') -or $path.StartsWith($_ + '/', [StringComparison]::Ordinal) }).Count -gt 0
}) {
    $formal[$row.Path] = $row
    if ($row.Path.EndsWith('.meta', [StringComparison]::Ordinal)) { $null = $expected.Add($row.Path.Substring(0,$row.Path.Length-5)) }
}
Add-Type -AssemblyName System.Formats.Tar
$package = Join-Path $root 'YLFramework-8.10.unitypackage'
$stream = [IO.File]::OpenRead($package)
$gzip = [IO.Compression.GZipStream]::new($stream,[IO.Compression.CompressionMode]::Decompress)
$tar = [System.Formats.Tar.TarReader]::new($gzip,$true)
$records = @{}
try {
    while ($null -ne ($entry = $tar.GetNextEntry())) {
        if ($entry.EntryType -eq [System.Formats.Tar.TarEntryType]::Directory) { continue }
        $name = $entry.Name.TrimStart('./')
        if ($name -notmatch '^([0-9a-f]{32})/(asset|asset\.meta|pathname|preview\.png)$' -or $entry.Length -gt 80MB) { throw "Unexpected package member: $name" }
        $guid = $Matches[1]; $member = $Matches[2]
        if (-not $records.ContainsKey($guid)) { $records[$guid] = @{} }
        if ($records[$guid].ContainsKey($member)) { throw 'Duplicate package member.' }
        $buffer = [IO.MemoryStream]::new()
        try { if ($entry.DataStream) { $entry.DataStream.CopyTo($buffer) } elseif ($entry.Length -ne 0) { throw 'Missing nonempty tar data stream.' }; $records[$guid][$member] = $buffer.ToArray() }
        finally { $buffer.Dispose() }
    }
} finally { $tar.Dispose(); $gzip.Dispose(); $stream.Dispose() }
$paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($pair in $records.GetEnumerator()) {
    $record = $pair.Value
    if (-not $record.ContainsKey('pathname') -or -not $record.ContainsKey('asset.meta')) { throw 'Package record lacks identity.' }
    $path = [Text.Encoding]::UTF8.GetString($record['pathname']).TrimEnd([char]0,"`r","`n")
    if (-not $paths.Add($path) -or -not $expected.Contains($path) -or $path.Contains('\') -or @($path.Split('/') | Where-Object { $_ -in @('.','..') }).Count) { throw "Unexpected or duplicate package path: $path" }
    $meta = [Text.Encoding]::UTF8.GetString($record['asset.meta'])
    if ($meta -notmatch '(?m)^guid:\s*([0-9a-f]{32})\s*\r?$' -or $Matches[1] -cne $pair.Key) { throw "GUID mismatch: $path" }
    $metaSha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$record['asset.meta'])).ToLowerInvariant()
    if ($metaSha -cne $formal[$path + '.meta'].Sha256) { throw "Package meta differs: $path" }
    if ($formal.ContainsKey($path)) {
        if (-not $record.ContainsKey('asset')) { throw "Package payload missing: $path" }
        $sha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$record['asset'])).ToLowerInvariant()
        if ($sha -cne $formal[$path].Sha256 -or $record['asset'].LongLength -ne $formal[$path].Length) { throw "Package payload differs: $path" }
    } elseif ($record.ContainsKey('asset') -and $record['asset'].Length -ne 0) { throw 'Directory has an unexpected payload.' }
    if ($path -match '/Tests/|/R810/|^Packages/|/StreamingAssets/|^Temp/|^Library/|DOTween\.dll$|/(bcrypt|ncrypt|crypt32|Newtonsoft\.Json)\.dll$|\.(pfx|p12|pem|key)$' -or
        ($path -match '(?i)\.(dll|so|bundle)$' -and $path -cnotin @('Assets/YLFramework/Plugins/BouncyCastle/BouncyCastle.Cryptography.dll','Assets/YLFramework/Runtime/Core/Analyzers/YLFramework.Analyzers.dll'))) { throw "Excluded delivery path: $path" }
}
# Exporter 显式导出六个目录和 LICENSE；其共同父目录身份用于载体，不是额外导出根。
$required = [Collections.Generic.HashSet[string]]::new($expected,[StringComparer]::Ordinal)
$null = $required.Remove('Assets/YLFramework')
if (-not $required.IsSubsetOf($paths)) { throw ('The exact export-root/meta closure is incomplete: ' + (($required | Where-Object { -not $paths.Contains($_) }) -join ', ')) }
foreach ($required in @('Assets/YLFramework/LICENSE.md','Assets/YLFramework/Plugins/BouncyCastle/BouncyCastle.Cryptography.dll',
    'Assets/YLFramework/Plugins/BouncyCastle/SOURCE.txt')) {
    if (-not $paths.Contains($required)) { throw "Applicable notice missing: $required" }
}
$build = Join-Path $carrier ('Builds/' + $BuildName)
$report=Get-Content -LiteralPath (Join-Path $build 'BuildReport.json') -Raw | ConvertFrom-Json
if ($report.Backend -cne $Backend -or $report.Result -cne 'Succeeded' -or $report.Errors -ne 0) { throw 'Export and actual Player backend identity differ.' }
$dll = if ($Backend -eq 'IL2CPP') { Join-Path $root ('BuildEvidence/' + $BuildName + '/ManagedStripped/YLFramework.Runtime.dll') } else { Join-Path $build 'ResourceAcceptance_Data/Managed/YLFramework.Runtime.dll' }
$input = [IO.File]::OpenRead($dll)
$pe = [System.Reflection.PortableExecutable.PEReader]::new($input)
try {
    $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
    $references = @($metadata.AssemblyReferences | ForEach-Object { $metadata.GetString($metadata.GetAssemblyReference($_).Name) })
    if ($references -notcontains 'Newtonsoft.Json' -or $references -notcontains 'BouncyCastle.Cryptography' -or @($references | Where-Object {
        $_ -cnotmatch '^(?:mscorlib|netstandard|System(?:\.[A-Za-z0-9_]+)*|UnityEngine(?:\.[A-Za-z0-9_]+)*|Unity\.ugui|Microsoft\.CSharp|Microsoft\.Win32\.Primitives|Newtonsoft\.Json|BouncyCastle\.Cryptography)$' -or $_ -match 'TestRunner|Tests|Editor|Analyzer'
    }).Count) { throw 'Player Runtime assembly dependency boundary changed.' }
} finally { $pe.Dispose(); $input.Dispose() }
foreach ($notice in @('YLFramework-MIT.md','BouncyCastle-SOURCE.txt','Json.NET-UPM-LICENSE.md','Json.NET-UPM-THIRD-PARTY-NOTICES.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $build ('Licenses/' + $notice)) -PathType Leaf)) { throw "Player applicable notice missing: $notice" }
}
$bcPath=if ($Backend -eq 'IL2CPP') { Join-Path $root ('BuildEvidence/' + $BuildName + '/Sources/Assets/YLFramework/Plugins/BouncyCastle/BouncyCastle.Cryptography.dll') } else { Join-Path $build 'ResourceAcceptance_Data/Managed/BouncyCastle.Cryptography.dll' }
$bc = (Get-FileHash -LiteralPath $bcPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($bc -cne 'd61c1f2ba929a230a58e101ccd850e21f2675fa6b9814ec279633e8a089c3495') { throw 'Player BC binary differs from approved official artifact.' }
$native=@()
if ($Backend -eq 'IL2CPP') {
    $native=@(foreach ($path in @('GameAssembly.dll','ResourceAcceptance_Data/il2cpp_data/Metadata/global-metadata.dat')) {
        $expected=@($report.PlayerFiles | Where-Object Path -CEQ $path)
        $hash=(Get-FileHash -LiteralPath (Join-Path $build $path) -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($expected.Count -ne 1 -or $expected[0].Sha256 -cne $hash) { throw 'Actual IL2CPP native artifact differs from the Player report.' }
        @{ Path=$path; Sha256=$hash }
    })
}
$receipt = @{ Passed=$true; Package=$package; Bytes=([IO.FileInfo]$package).Length; Sha256=(Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant(); ExactPackageRecords=$paths.Count;
    FormalFiles=$formal.Count; Backend=$Backend; Build=$BuildName; RuntimeAssemblyReferences=$references; RuntimeSha256=(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant(); ApprovedBouncyCastleSourceSha256=$bc; NativeArtifacts=$native;
    Notes='Exact tar/meta/GUID/payload and PE/license validation. For IL2CPP, managed PE is actual archived linker input, not a shipped DLL; GameAssembly/metadata identify the Player. No stage-9 import performed.' }
if ($ReceiptName -notmatch '^[a-z0-9][a-z0-9-]*\.json$') { throw 'Use a new simple receipt filename.' }
if (Test-Path -LiteralPath (Join-Path $root $ReceiptName)) { throw 'Do not overwrite export evidence.' }
[IO.File]::WriteAllText((Join-Path $root $ReceiptName),($receipt | ConvertTo-Json -Depth 6))
$receipt | ConvertTo-Json -Depth 6 -Compress
