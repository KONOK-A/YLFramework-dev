#requires -Version 7.6
[CmdletBinding()]
param(
    [ValidateSet('Prepare','Verify')][string] $Operation='Verify',
    [ValidateSet('ParameterOwner','UnmodifiedControl')][string] $Variant='ParameterOwner',
    [string] $WorkspaceRoot,
    [ValidatePattern('^[A-Za-z0-9-]+$')][string] $Run,
    [string] $SourceManifest,
    [string] $Il2CppRepository,
    [string] $HybridClrRepository
)
Set-StrictMode -Version 3.0
$ErrorActionPreference='Stop'
function Assert-HybridCLRNativeSourceManifest($Manifest) {
    $patched=$Manifest.Variant -ceq 'ParameterOwner'
    if ($Manifest.Schema -cne 'B08NativeSource-v1' -or $Manifest.Variant -cnotin @('ParameterOwner','UnmodifiedControl') -or $Manifest.Unity -cne '6000.3.27f1' -or
        $Manifest.SdkVersion -cne '9.0.3' -or $Manifest.SdkCommit -cne '6cadd24d101f4cbd00cd80190106f6621132d24d' -or
        $Manifest.Il2CppCommit -cne '6eb38b29c3a7448b3ac6c4dee450de375b5d158d' -or $Manifest.HybridClrCommit -cne 'bf01f8eed56a077781c3a7b25f418603c7645588' -or
        $Manifest.BeforeCrLfSha256 -cne '6c2902bfcbdbaf253486d5af13c0e2b1e372af5c5a934a74c1a172fe8f2b823f' -or
        $Manifest.AfterCrLfSha256 -cne $(if ($patched) { '069213cf9fb4c5a07033a8d0bba25d53b4b986b6ba3eba98185e8c1e444bf4a1' } else { $Manifest.BeforeCrLfSha256 }) -or
        $Manifest.PatchSha256 -cnotmatch '^[0-9a-f]{64}$' -or @($Manifest.ChangedFiles).Count -ne $(if ($patched) { 1 } else { 0 }) -or
        ($patched -and $Manifest.ChangedFiles[0] -cne 'vm/GlobalMetadata.cpp')) {
        throw 'Native source: unapproved version/variant or native delta.'
    }
    if (-not [IO.Path]::IsPathFullyQualified($Manifest.SourceDirectory) -or $Manifest.SourceDirectory -match '(?i)(?:^|[\\/])(?:Library|PackageCache)(?:[\\/]|$)' -or
        $Manifest.SourceDirectory -match '(?:^|[\\/])\.\.(?:[\\/]|$)') { throw 'Native source: an independent absolute maintained source directory is required.' }
    if (@($Manifest.Licenses).Count -ne 2 -or $Manifest.Licenses -cnotcontains 'MIT-il2cpp_plus' -or $Manifest.Licenses -cnotcontains 'MIT-hybridclr') { throw 'Native source: retain both applicable upstream licenses.' }
    $paths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    if (@($Manifest.Files).Count -eq 0) { throw 'Native source: empty source tree.' }
    foreach ($file in $Manifest.Files) {
        if ($file.Path -match '^[/\\]|:|\\|(?:^|/)\.\.?(?:/|$)' -or -not $paths.Add($file.Path) -or $file.Length -lt 0 -or $file.Sha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Native source: invalid or duplicate file identity.' }
    }
}
function Assert-NativeSourceNoLinks([string] $Path) {
    for ($cursor=[IO.Path]::GetFullPath($Path); $cursor; $cursor=[IO.Path]::GetDirectoryName($cursor)) {
        if (-not [IO.Path]::Exists($cursor)) { continue }
        if ([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) { throw "Native source: linked path $cursor" }
    }
}
function Get-NativeSourceSha([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Get-NativeSourceFiles([string] $Directory) {
    @(Get-ChildItem -LiteralPath $Directory -Recurse -Force -File | Sort-Object FullName | ForEach-Object {
        Assert-NativeSourceNoLinks $_.FullName
        @{ Path=[IO.Path]::GetRelativePath($Directory,$_.FullName).Replace('\','/'); Length=$_.Length; Sha256=Get-NativeSourceSha $_.FullName }
    })
}
function Get-NativeSourceCrLfSha([string] $Path) {
    $text=[IO.File]::ReadAllText($Path,[Text.UTF8Encoding]::new($false,$true)).Replace("`r`n","`n").Replace("`n","`r`n")
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.UTF8Encoding]::new($false).GetBytes($text))).ToLowerInvariant()
}
if ($MyInvocation.InvocationName -eq '.') { return }
$patch=Join-Path $PSScriptRoot 'GlobalMetadata-ParameterOwner.patch'
Assert-NativeSourceNoLinks $patch
if ($Operation -ceq 'Prepare') {
    if (-not $Run -or [string]::IsNullOrWhiteSpace($WorkspaceRoot) -or
        [string]::IsNullOrWhiteSpace($Il2CppRepository) -or [string]::IsNullOrWhiteSpace($HybridClrRepository)) {
        throw 'Native source: explicit workspace, exact run and both pinned source repositories are required.'
    }
    . (Join-Path $PSScriptRoot '../Testing/Common/Workspace.ps1')
    $repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    foreach ($inputRoot in @($WorkspaceRoot,$Il2CppRepository,$HybridClrRepository)) { $null=Get-YLTestPath $inputRoot }
    $workspace=Initialize-YLTestWorkspace -WorkspaceRoot $WorkspaceRoot -Repository $repository
    $record=New-YLTestDirectory -Workspace $workspace -Kind Result -Key ('HybridCLR-NativeSource-'+$Run)
    $root=$record.path
    $lease=Lock-YLTestDirectory -Workspace $workspace -Path $root
    try {
    foreach ($spec in @(@('il2cpp_plus',$Il2CppRepository,'6eb38b29c3a7448b3ac6c4dee450de375b5d158d'),@('hybridclr',$HybridClrRepository,'bf01f8eed56a077781c3a7b25f418603c7645588'))) {
        Assert-NativeSourceNoLinks $spec[1]
        $head=& git -C $spec[1] rev-parse HEAD
        if ($LASTEXITCODE -ne 0 -or $head -cne $spec[2]) { throw 'Native source: exact pinned Git object source required.' }
        $zip=Join-Path $root ($spec[0]+'.zip')
        & git -C $spec[1] archive --format=zip ('--output='+$zip) $spec[2]
        if ($LASTEXITCODE -ne 0) { throw 'Native source: archive failed.' }
        $archive=[IO.Compression.ZipFile]::OpenRead($zip)
        try {
            foreach ($entry in $archive.Entries) {
                if ($entry.FullName -match '^[/\\]|:|(?:^|/)\.\.(?:/|$)' -or (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Native source: linked/escaping Git archive entry.' }
            }
        } finally { $archive.Dispose() }
        [IO.Compression.ZipFile]::ExtractToDirectory($zip,(Join-Path $root $spec[0]))
    }
    $upstream=Join-Path $root 'il2cpp_plus'
    Copy-Item -LiteralPath (Join-Path $root 'hybridclr/hybridclr') -Destination (Join-Path $upstream 'libil2cpp') -Recurse
    Copy-Item -LiteralPath (Join-Path $root 'hybridclr/LICENSE') -Destination (Join-Path $upstream 'HybridCLR-LICENSE')
    $patched=Join-Path $root $(if ($Variant -ceq 'ParameterOwner') { 'Patched' } else { 'UnmodifiedControl' })
    Copy-Item -LiteralPath $upstream -Destination $patched -Recurse
    $before=Get-NativeSourceFiles (Join-Path $patched 'libil2cpp')
    $target=Join-Path $patched 'libil2cpp/vm/GlobalMetadata.cpp'
    $beforeCrLf=Get-NativeSourceCrLfSha $target
    if ($beforeCrLf -cne '6c2902bfcbdbaf253486d5af13c0e2b1e372af5c5a934a74c1a172fe8f2b823f') { throw 'Native source: patch preimage differs.' }
    if ($Variant -ceq 'ParameterOwner') {
        & git -C $patched apply --check $patch
        if ($LASTEXITCODE -ne 0) { throw 'Native source: exact patch does not apply.' }
        & git -C $patched apply $patch
        if ($LASTEXITCODE -ne 0) { throw 'Native source: patch application failed.' }
    }
    $files=Get-NativeSourceFiles (Join-Path $patched 'libil2cpp')
    $map=@{}; foreach ($file in $before) { $map[$file.Path]=$file.Sha256 }
    $changed=@($files | Where-Object { -not $map.ContainsKey($_.Path) -or $map[$_.Path] -cne $_.Sha256 } | ForEach-Object Path)
    if ($files.Count -ne $before.Count) { throw 'Native source: source path set changed.' }
    $manifest=@{ Schema='B08NativeSource-v1'; Variant=$Variant; Unity='6000.3.27f1'; SdkVersion='9.0.3'; SdkCommit='6cadd24d101f4cbd00cd80190106f6621132d24d'
        Il2CppCommit='6eb38b29c3a7448b3ac6c4dee450de375b5d158d'; HybridClrCommit='bf01f8eed56a077781c3a7b25f418603c7645588'; SourceDirectory=(Join-Path $patched 'libil2cpp')
        BeforeCrLfSha256=$beforeCrLf; AfterCrLfSha256=(Get-NativeSourceCrLfSha $target); ChangedFiles=$changed; PatchSha256=(Get-NativeSourceSha $patch)
        Files=$files; Licenses=@('MIT-il2cpp_plus','MIT-hybridclr'); LicenseFiles=@(@{ Path=(Join-Path $patched 'LICENSE'); Sha256=(Get-NativeSourceSha (Join-Path $patched 'LICENSE')) },@{ Path=(Join-Path $patched 'HybridCLR-LICENSE'); Sha256=(Get-NativeSourceSha (Join-Path $patched 'HybridCLR-LICENSE')) })
        Utc=[DateTime]::UtcNow.ToString('O'); NativeCompiled=$false }
    Assert-HybridCLRNativeSourceManifest $manifest
    $SourceManifest=Join-Path $root 'native-source.json'
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Depth 8))
    $stream=[IO.File]::Open($SourceManifest,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try { $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    } finally { $lease.Dispose() }
}
if (-not $SourceManifest) { throw 'Native source: explicit manifest required.' }
Assert-NativeSourceNoLinks $SourceManifest
$manifest=[IO.File]::ReadAllText($SourceManifest) | ConvertFrom-Json -AsHashtable
Assert-HybridCLRNativeSourceManifest $manifest
if ($manifest.PatchSha256 -cne (Get-NativeSourceSha $patch)) { throw 'Native source: patch identity changed.' }
$actual=Get-NativeSourceFiles $manifest.SourceDirectory
if ($actual.Count -ne @($manifest.Files).Count) { throw 'Native source: maintained tree path set changed.' }
$map=@{}; foreach ($file in $actual) { $map[$file.Path]=$file }
foreach ($file in $manifest.Files) { if (-not $map.ContainsKey($file.Path) -or $map[$file.Path].Sha256 -cne $file.Sha256 -or $map[$file.Path].Length -ne $file.Length) { throw 'Native source: maintained tree bytes changed.' } }
$runRoot=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($SourceManifest))
$reference=Join-Path $runRoot 'il2cpp_plus/libil2cpp'
Assert-NativeSourceNoLinks $reference
if (-not [IO.Directory]::Exists($reference) -or
    -not [IO.Path]::GetFullPath($manifest.SourceDirectory).StartsWith($runRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFullPath($reference) -ieq [IO.Path]::GetFullPath($manifest.SourceDirectory)) { throw 'Native source: independent before/after trees must belong to this maintained run.' }
$beforeTarget=Join-Path $reference 'vm/GlobalMetadata.cpp'
$afterTarget=Join-Path $manifest.SourceDirectory 'vm/GlobalMetadata.cpp'
Assert-NativeSourceNoLinks $beforeTarget; Assert-NativeSourceNoLinks $afterTarget
if ((Get-NativeSourceCrLfSha $beforeTarget) -cne $manifest.BeforeCrLfSha256 -or (Get-NativeSourceCrLfSha $afterTarget) -cne $manifest.AfterCrLfSha256) {
    throw 'Native source: actual approved preimage/postimage does not match the declared variant.'
}
$beforeFiles=Get-NativeSourceFiles $reference
$beforeMap=@{}; foreach ($file in $beforeFiles) { $beforeMap[$file.Path]=$file.Sha256 }
if ($beforeFiles.Count -ne $actual.Count -or @($actual | Where-Object { -not $beforeMap.ContainsKey($_.Path) }).Count) { throw 'Native source: actual before/after path sets differ.' }
$actualChanges=@($actual | Where-Object { $beforeMap[$_.Path] -cne $_.Sha256 } | ForEach-Object Path)
if ($actualChanges.Count -ne @($manifest.ChangedFiles).Count -or @($actualChanges | Where-Object { $manifest.ChangedFiles -cnotcontains $_ }).Count) {
    throw 'Native source: actual source delta differs from the approved declared change set.'
}
foreach ($license in $manifest.LicenseFiles) { Assert-NativeSourceNoLinks $license.Path; if ((Get-NativeSourceSha $license.Path) -cne $license.Sha256) { throw 'Native source: license changed.' } }
@{ Verified=$true; SourceManifest=$SourceManifest; SourceManifestSha256=(Get-NativeSourceSha $SourceManifest); SourceDirectory=$manifest.SourceDirectory; Files=$actual.Count; ActualPreimagePostimageVerified=$true; ActualChanges=$actualChanges; NativeExecuted=$false }
