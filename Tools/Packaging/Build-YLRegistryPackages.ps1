#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param(
    [Alias('Repository')][string] $YLRegistryBuildRepository,
    [Alias('SourceCommit')][string] $YLRegistryBuildSourceCommit,
    [Alias('OutputRoot')][string] $YLRegistryBuildOutputRoot
)

function Get-YLRegistryPackageIds {
    return @('com.kano.ylframework', 'com.kano.ylframework.domain-excel', 'com.kano.ylframework.dotween', 'com.kano.ylframework.excel', 'com.kano.ylframework.hybridclr')
}

function Read-YLRegistryJson {
    param([byte[]] $Bytes)
    function Assert-JsonElement([Text.Json.JsonElement] $Element) {
        if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
            $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($property in $Element.EnumerateObject()) {
                if (-not $names.Add($property.Name)) { throw "Duplicate JSON property: $($property.Name)" }
                Assert-JsonElement $property.Value
            }
        } elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
            foreach ($item in $Element.EnumerateArray()) { Assert-JsonElement $item }
        }
    }
    function Convert-RegistryToken([Newtonsoft.Json.Linq.JToken] $Token) {
        if ($Token -is [Newtonsoft.Json.Linq.JObject]) {
            $result = [Newtonsoft.Json.Linq.JObject]::new()
            [string[]]$names = @($Token.Properties() | ForEach-Object Name)
            [Array]::Sort($names, [StringComparer]::Ordinal)
            foreach ($name in $names) { $result.Add($name, (Convert-RegistryToken $Token[$name])) }
            return ,$result
        }
        if ($Token -is [Newtonsoft.Json.Linq.JArray]) {
            $result = [Newtonsoft.Json.Linq.JArray]::new()
            foreach ($item in $Token) { $result.Add((Convert-RegistryToken $item)) }
            return ,$result
        }
        return ,$Token.DeepClone()
    }
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($Bytes)
    try { $document = [Text.Json.JsonDocument]::Parse($text) }
    catch { throw "Strict JSON validation failed: $($_.Exception.Message)" }
    try {
        if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw 'JSON manifest must be an object.' }
        Assert-JsonElement $document.RootElement
    } finally { $document.Dispose() }
    $reader = [Newtonsoft.Json.JsonTextReader]::new([IO.StringReader]::new($text))
    $reader.DateParseHandling = [Newtonsoft.Json.DateParseHandling]::None
    try { $token = [Newtonsoft.Json.Linq.JObject]::Load($reader) } finally { $reader.Dispose() }
    foreach ($property in $token.Properties()) {
        if ($property.Name.StartsWith('_', [StringComparison]::Ordinal) -or $property.Name -cin @('gitHead', 'sourceCommit')) { throw "Raw package manifest contains managed/commit metadata: $($property.Name)" }
    }
    return [pscustomobject]@{
        manifest = ConvertFrom-Json -InputObject $text -AsHashtable -NoEnumerate -DateKind String
        canonical = (Convert-RegistryToken $token).ToString([Newtonsoft.Json.Formatting]::None)
    }
}

function Assert-YLRegistryRelativePath {
    param([string] $Path)
    if ([string]::IsNullOrEmpty($Path) -or $Path -match '[\\\x00-\x1f\x7f:<>"|?*]' -or $Path.StartsWith('/')) { throw "Unsafe tar/package path: $Path" }
    foreach ($part in $Path.Split('/')) {
        if (-not $part -or $part -in @('.', '..') -or $part.EndsWith('.') -or $part.EndsWith(' ') -or
            $part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') { throw "Ambiguous/reserved tar path: $Path" }
    }
}

function Get-YLRegistryPayload {
    param([Collections.Generic.Dictionary[string, byte[]]] $Contents)
    Set-StrictMode -Version 3.0
    if (-not $Contents.ContainsKey('package.json')) { throw 'Required package manifest is missing.' }
    $json = Read-YLRegistryJson $Contents['package.json']
    $manifest = $json.manifest
    $id = $manifest['name']; $version = $manifest['version']
    if ($id -isnot [string] -or $id -cnotin (Get-YLRegistryPackageIds) -or $version -isnot [string] -or
        $version -cnotmatch '^(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') { throw 'Invalid package identity/version.' }
    foreach ($name in @('displayName', 'description', 'license', 'unity', 'unityRelease')) {
        if ($manifest[$name] -isnot [string] -or [string]::IsNullOrWhiteSpace($manifest[$name])) { throw "Required package metadata missing: $name" }
    }
    if ($manifest['unity'] -cne '2022.3' -or $manifest['unityRelease'] -cne '62f3' -or $manifest['author'] -isnot [Collections.IDictionary] -or
        $manifest['author']['name'] -isnot [string] -or -not $manifest['author']['name']) { throw 'Invalid Unity/author package metadata.' }
    $repo = $manifest['repository']
    if ($repo -isnot [Collections.IDictionary] -or $repo['type'] -cne 'git' -or $repo['url'] -cne 'https://github.com/KONOK-A/YLFramework-dev.git' -or
        $repo['directory'] -cne "PackageSources/$id") { throw 'Invalid package repository metadata.' }
    if ($manifest.Contains('scripts') -or $manifest.Contains('workspaces') -or $manifest.Contains('bundledDependencies') -or $manifest.Contains('bundleDependencies')) {
        throw 'Package lifecycle hooks/workspaces/bundled dependencies are forbidden.'
    }
    $whitelist = $manifest['files']
    if ($whitelist -isnot [array] -or $whitelist.Count -eq 0) { throw 'An explicit files whitelist is required.' }
    $allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($name in $whitelist) {
        if ($name -isnot [string] -or $name -match '[/\\*?!\[\]{}]' -or $name.StartsWith('.') -or -not $allowed.Add($name)) { throw 'Invalid or duplicate files whitelist entry.' }
        Assert-YLRegistryRelativePath $name
        if (-not $Contents.ContainsKey($name) -and @($Contents.Keys | Where-Object { $_.StartsWith($name + '/', [StringComparison]::Ordinal) }).Count -eq 0) {
            throw "Missing files whitelist input: $id/$name"
        }
    }
    foreach ($required in @('package.json', 'package.json.meta', 'README.md', 'README.md.meta', 'LICENSE.md', 'LICENSE.md.meta')) {
        if (-not $allowed.Contains($required) -or -not $Contents.ContainsKey($required) -or $Contents[$required].Length -eq 0) { throw "Missing required files whitelist/license/metadata: $id/$required" }
    }
    $dependencies = $manifest['dependencies']
    if ($dependencies -isnot [Collections.IDictionary]) { throw 'Package dependencies must be an object.' }
    $expectedInternal = @()
    if ($id -cne 'com.kano.ylframework') { $expectedInternal += 'com.kano.ylframework' }
    if ($id -ceq 'com.kano.ylframework.domain-excel') { $expectedInternal += 'com.kano.ylframework.excel' }
    foreach ($entry in $dependencies.GetEnumerator()) {
        if ($entry.Value -isnot [string] -or $entry.Value -cnotmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw "Non-exact SemVer dependency: $id -> $($entry.Key)" }
        if ($entry.Key.StartsWith('com.kano.', [StringComparison]::Ordinal)) {
            if ($entry.Key -cnotin $expectedInternal -or $entry.Value -cne $version) { throw "Internal dependency/version mismatch: $id -> $($entry.Key)" }
        } elseif (-not $entry.Key.StartsWith('com.unity.', [StringComparison]::Ordinal) -and
            -not ($id -ceq 'com.kano.ylframework.hybridclr' -and $entry.Key -ceq 'com.code-philosophy.hybridclr' -and $entry.Value -ceq '9.0.3')) {
            throw "Unexpected package/SDK dependency: $id -> $($entry.Key)"
        }
    }
    foreach ($internal in $expectedInternal) { if ($dependencies[$internal] -cne $version) { throw "Missing exact internal dependency: $id -> $internal" } }
    if ($id -ceq 'com.kano.ylframework.hybridclr' -and $dependencies['com.code-philosophy.hybridclr'] -cne '9.0.3') { throw 'HybridCLR SDK dependency must be 9.0.3.' }
    $required = switch ($id) {
        'com.kano.ylframework' { @('Runtime/Core/Resources/YLFramework/Core/Canvas.prefab', 'Runtime/Core/Resources/YLFramework/Core/EventSystem.prefab',
            'Runtime/Systems/Action/Resources/ActionKit/ScreenTransitionCanvas.prefab', 'Prefabs/YLFramework.prefab',
            'Plugins/BouncyCastle/BouncyCastle.Cryptography.dll', 'Plugins/BouncyCastle/SOURCE.txt', 'Runtime/Core/Analyzers/YLFramework.Analyzers.dll',
            'Samples~/ResourceStartup/Runtime/ResourceStartupExample.cs', 'Samples~/ResourceStartup/Editor/ResourceStartupExampleEditor.cs') }
        'com.kano.ylframework.excel' { @('Editor/Plugins/MiniExcel.dll', 'Editor/Licenses/MiniExcel-LICENSE.txt', 'Editor/Licenses/MiniExcel-ATTRIBUTION.txt', 'Editor/Dependencies.json', 'Editor/YLFramework.Excel.Editor.asmdef') }
        'com.kano.ylframework.domain-excel' { @('Editor/Dependencies.json', 'Editor/YLFramework.Domain.Excel.Editor.asmdef') }
        'com.kano.ylframework.dotween' { @('Dependencies.json', 'Runtime/YLFramework.Actions.DOTween.asmdef') }
        'com.kano.ylframework.hybridclr' { @('Dependencies.json', 'Runtime/YLFramework.HybridCLR.asmdef', 'Editor/YLFramework.HybridCLR.Editor.asmdef', 'Samples~/HybridCLR/Host/HybridCLRExample.cs') }
    }
    foreach ($path in $required) {
        if (-not $Contents.ContainsKey($path) -or $Contents[$path].Length -eq 0) { throw "Missing required payload/license/sample: $id/$path" }
    }
    $sample = switch ($id) { 'com.kano.ylframework' { 'Samples~/ResourceStartup' }; 'com.kano.ylframework.hybridclr' { 'Samples~/HybridCLR' }; default { $null } }
    if ($null -ne $sample) {
        if ($manifest['samples'] -isnot [array] -or $manifest['samples'].Count -ne 1 -or $manifest['samples'][0]['path'] -cne $sample) { throw 'Required explicit Sample declaration is missing.' }
    } elseif ($manifest.Contains('samples')) { throw 'Unexpected package Samples declaration.' }
    $dlls = @('com.kano.ylframework/Plugins/BouncyCastle/BouncyCastle.Cryptography.dll', 'com.kano.ylframework/Runtime/Core/Analyzers/YLFramework.Analyzers.dll', 'com.kano.ylframework.excel/Editor/Plugins/MiniExcel.dll')
    $files = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    $guids = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $directories = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $Contents.GetEnumerator()) {
        $path = $entry.Key; [byte[]]$bytes = $entry.Value
        Assert-YLRegistryRelativePath $path
        # Classify metadata by its owning asset, not as an unrelated allowed .meta file.
        $publishPath = if ($path.EndsWith('.meta', [StringComparison]::Ordinal)) { $path.Substring(0, $path.Length - 5) } else { $path }
        if (-not $allowed.Contains($path.Split('/')[0]) -or
            $publishPath -match '^(?i:Assets|Packages|ProjectSettings|Library|Temp|obj|bin|Tests|Tools|Settings|Integrations)(?:/|$)' -or
            $publishPath -match '(?i)(?:^|/)(?:Tests|Library|Temp|obj|bin|node_modules|TestResults|SDK|Demigiant|HybridCLRData|AGENTS\.md|\.\w[^/]*|DOTween[^/]*\.dll|McpPlugin[^/]*\.dll|HybridCLR\.(?:Runtime|Editor)\.dll)(?:/|$)' -or
            $publishPath -match '(?i)(?:^|/)(?:package-lock\.json|npm-shrinkwrap\.json|yarn\.lock|pnpm-lock\.yaml)$' -or
            $publishPath -match '(?i)\.(?:csproj|sln|slnx|unitypackage|tgz|exe|ps1|bat|cmd|py|sh|zip|7z|nupkg|msi|tar|rar|gz|private|pfx|p12|pem|key|ppk|jks|keystore|so|dylib)$') { throw "Forbidden/unauthorized package input or bundled SDK: $id/$path" }
        $prefix = [Text.Encoding]::UTF8.GetString($bytes, 0, [Math]::Min(200, $bytes.Length))
        if ($prefix.StartsWith('version https://git-lfs.github.com/spec/v1', [StringComparison]::Ordinal)) { throw "Unmaterialized Git LFS content: $id/$path" }
        if ($path.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase)) {
            if ("$id/$path" -cnotin $dlls) { throw "Unapproved bundled DLL/SDK: $id/$path" }
            if ($bytes.Length -lt 64 -or $bytes[0] -ne 77 -or $bytes[1] -ne 90) { throw "DLL is not a real PE binary: $id/$path" }
            $offset = [BitConverter]::ToInt32($bytes, 60)
            if ($offset -lt 64 -or $offset -gt $bytes.Length - 24 -or [BitConverter]::ToUInt32($bytes, $offset) -ne 0x4550) { throw "DLL has no valid PE header: $id/$path" }
            $stream = [IO.MemoryStream]::new($bytes, $false)
            $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
            try {
                if (-not $pe.HasMetadata -or $null -eq $pe.PEHeaders.CorHeader) { throw "DLL has no managed PE metadata: $id/$path" }
                $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
                $expectedName = [IO.Path]::GetFileNameWithoutExtension($path)
                if (-not $metadata.IsAssembly -or $metadata.GetString($metadata.GetAssemblyDefinition().Name) -cne $expectedName) { throw "Approved DLL assembly identity differs: $id/$path" }
            }
            finally { $pe.Dispose(); $stream.Dispose() }
        }
        if ($path.EndsWith('.cs', [StringComparison]::OrdinalIgnoreCase) -and
            [Text.Encoding]::UTF8.GetString($bytes) -match '(?m)^\s*namespace\s+(?:DG\.Tweening|HybridCLR|com\.IvanMurzak)(?:[\s.{;])') { throw "Bundled SDK source is forbidden: $id/$path" }
        $parent = $path
        while ($parent.Contains('/')) { $parent = $parent.Substring(0, $parent.LastIndexOf('/')); $null = $directories.Add($parent) }
        if ($path.EndsWith('.meta', [StringComparison]::Ordinal)) {
            $matches = [regex]::Matches([Text.Encoding]::UTF8.GetString($bytes), '(?m)^guid: ([0-9a-f]{32})\s*$')
            if ($matches.Count -ne 1) { throw "Invalid asset GUID: $id/$path" }
            $guid = $matches[0].Groups[1].Value
            if ($guids.ContainsKey($guid)) { throw "Duplicate GUID: $id/$path" }
            $guids.Add($guid, $path)
        } elseif (-not $path.StartsWith('Documentation~/', [StringComparison]::Ordinal) -and -not $Contents.ContainsKey($path + '.meta')) {
            throw "Missing required asset meta: $id/$path"
        }
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        $contentHash = if ($path -ceq 'package.json') { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($json.canonical))).ToLowerInvariant() } else { $hash }
        $files.Add($path, [ordered]@{ path = $path; sha256 = $hash; contentSha256 = $contentHash; length = [long]$bytes.Length })
    }
    foreach ($directory in $directories) {
        if ($directory -ceq 'Samples~' -or $directory -ceq 'Documentation~' -or $directory.StartsWith('Documentation~/', [StringComparison]::Ordinal)) { continue }
        if (-not $Contents.ContainsKey($directory + '.meta')) { throw "Missing required directory meta: $id/$directory" }
    }
    foreach ($path in $Contents.Keys) {
        if ($path.EndsWith('.meta', [StringComparison]::Ordinal)) {
            $target = $path.Substring(0, $path.Length - 5)
            if (-not $Contents.ContainsKey($target) -and -not $directories.Contains($target) -and
                [Text.Encoding]::UTF8.GetString($Contents[$path]) -cnotmatch '(?m)^folderAsset: yes\s*$') { throw "Orphan asset meta: $id/$path" }
        }
    }
    foreach ($pair in @(@('com.kano.ylframework', 'Plugins/BouncyCastle/BouncyCastle.Cryptography.dll', 'd61c1f2ba929a230a58e101ccd850e21f2675fa6b9814ec279633e8a089c3495'),
        @('com.kano.ylframework.excel', 'Editor/Plugins/MiniExcel.dll', '0a49b66224bef9fdcec12800e4660267a993b1ece87727d9fe15c7dabf58ba1e'))) {
        if ($id -ceq $pair[0] -and $files[$pair[1]].sha256 -cne $pair[2]) { throw "Approved vendor DLL byte identity differs: $id/$($pair[1])" }
    }
    $text = [Text.StringBuilder]::new()
    foreach ($row in $files.Values) { $null = $text.Append($row.path).Append([char]0).Append($row.contentSha256).Append("`n") }
    return [pscustomobject]@{ id = $id; version = $version; dependencies = $dependencies; manifest = $manifest; files = @($files.Values); guids = $guids
        payloadSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text.ToString()))).ToLowerInvariant() }
}

function Assert-YLRegistryPackageBatch {
    param([object[]] $Packages)
    $ids = Get-YLRegistryPackageIds
    if ($Packages.Count -ne $ids.Count) { throw 'All five packages are required in one batch.' }
    $guids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($id in $ids) {
        $rows = @($Packages | Where-Object id -CEQ $id)
        if ($rows.Count -ne 1) { throw "Missing/duplicate package identity: $id" }
        if ($rows[0].version -cne '0.1.0-preview.1') { throw 'All five package versions must match the approved first release 0.1.0-preview.1.' }
        foreach ($guid in $rows[0].guids.Keys) { if (-not $guids.Add($guid)) { throw "Duplicate GUID across packages: $guid" } }
    }
}

function Get-YLRegistryPackageSet {
    param([string] $Root)
    . (Join-Path $PSScriptRoot '../Testing/Common/Workspace.ps1')
    $Root = Get-YLTestPath $Root
    $packages = @(foreach ($id in (Get-YLRegistryPackageIds)) {
        $path = Join-Path $Root $id
        if (-not [IO.File]::Exists((Join-Path $path 'package.json'))) { throw "Missing package manifest: $id" }
        $contents = [Collections.Generic.Dictionary[string, byte[]]]::new([StringComparer]::Ordinal)
        $size = 0L
        foreach ($file in @(Get-YLTestTreeFiles $path)) {
            $relative = [IO.Path]::GetRelativePath($path, $file).Replace('\', '/')
            if ($relative -ceq '.gitattributes') { continue }
            $size += [IO.FileInfo]::new($file).Length
            if ($size -ge 512MB -or $contents.Count -ge 30000) { throw 'Source package size/count exceeds tar safety limits.' }
            $contents.Add($relative, [IO.File]::ReadAllBytes($file))
        }
        $package = Get-YLRegistryPayload $contents
        if ($package.id -cne $id) { throw "Package directory/identity mismatch: $id" }
        $package | Add-Member -NotePropertyName root -NotePropertyValue $path
        $package
    })
    Assert-YLRegistryPackageBatch $packages
    return $packages
}

function Initialize-YLRegistryArchiveReader {
    if ('YLRegistry.BoundedReadStream' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.IO;
namespace YLRegistry {
    // Bound all decompressed bytes, including PAX/GNU metadata consumed inside TarReader.
    public sealed class BoundedReadStream : Stream {
        private readonly Stream input;
        private readonly long limit;
        private long count;
        private readonly byte[] header = new byte[512];
        private int headerBytes;
        private long payloadBytes;
        private long endBytes;
        public BoundedReadStream(Stream input, long limit) { this.input = input; this.limit = limit; }
        private static long Octal(byte[] bytes, int offset, int length) {
            long value = 0;
            bool ended = false;
            for (int i = offset; i < offset + length; i++) {
                byte c = bytes[i];
                if (c == 0 || c == 32) { if (value != 0) ended = true; continue; }
                if (ended || c < 48 || c > 55) throw new InvalidDataException("Invalid tar octal header field.");
                value = checked(value * 8 + c - 48);
            }
            return value;
        }
        private int Observe(ReadOnlySpan<byte> bytes) {
            count += bytes.Length;
            if (count >= limit) throw new InvalidDataException("Tar expanded size must be below 512 MiB.");
            int read = bytes.Length;
            while (!bytes.IsEmpty) {
                if (endBytes != 0) {
                    foreach (byte b in bytes) if (b != 0) throw new InvalidDataException("Trailing nonzero tar data.");
                    endBytes += bytes.Length;
                    break;
                }
                if (payloadBytes != 0) {
                    int take = (int)Math.Min(bytes.Length, payloadBytes);
                    payloadBytes -= take;
                    bytes = bytes.Slice(take);
                    continue;
                }
                int part = Math.Min(bytes.Length, 512 - headerBytes);
                bytes.Slice(0, part).CopyTo(header.AsSpan(headerBytes));
                headerBytes += part;
                bytes = bytes.Slice(part);
                if (headerBytes != 512) continue;
                headerBytes = 0;
                bool zero = true;
                foreach (byte b in header) if (b != 0) { zero = false; break; }
                if (zero) { endBytes = 512; continue; }
                long checksum = 0;
                for (int i = 0; i < 512; i++) checksum += i >= 148 && i < 156 ? 32 : header[i];
                if (checksum != Octal(header, 148, 8)) throw new InvalidDataException("Invalid tar header checksum.");
                long size = Octal(header, 124, 12);
                if (size >= limit) throw new InvalidDataException("Tar entry size must be below 512 MiB.");
                payloadBytes = checked((size + 511) / 512 * 512);
            }
            return read;
        }
        public void VerifyComplete() {
            if (headerBytes != 0 || payloadBytes != 0 || endBytes < 1024)
                throw new InvalidDataException("Truncated tar archive or end markers.");
        }
        public override int Read(byte[] buffer, int offset, int length) {
            int read = input.Read(buffer, offset, (int)Math.Min(length, Math.Max(1, limit - count + 1)));
            return Observe(buffer.AsSpan(offset, read));
        }
        public override int Read(Span<byte> buffer) {
            int read = input.Read(buffer.Slice(0, (int)Math.Min(buffer.Length, Math.Max(1, limit - count + 1))));
            return Observe(buffer.Slice(0, read));
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
'@
}

function Get-YLRegistrySdkPayload {
    param([Collections.Generic.Dictionary[string, byte[]]] $Contents)
    if (-not $Contents.ContainsKey('package.json') -or -not $Contents.ContainsKey('LICENSE')) { throw 'Official SDK manifest/license is missing.' }
    $json = Read-YLRegistryJson $Contents['package.json']; $manifest = $json.manifest
    if ($manifest['name'] -cne 'com.code-philosophy.hybridclr' -or $manifest['version'] -cne '9.0.3' -or
        [Text.Encoding]::UTF8.GetString($Contents['LICENSE']) -cnotmatch 'MIT License' -or
        [Text.Encoding]::UTF8.GetString($Contents['LICENSE']) -cnotmatch 'Permission is hereby granted') { throw 'Official SDK version/license is incorrect.' }
    $dependencies = if ($manifest.Contains('dependencies')) { $manifest['dependencies'] } else { @{} }
    if ($dependencies -isnot [Collections.IDictionary] -or $dependencies.Count -ne 0) { throw 'HybridCLR 9.0.3 SDK has unexpected dependency declarations.' }
    $files = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($entry in $Contents.GetEnumerator()) {
        $path = $entry.Key; [byte[]]$bytes = $entry.Value
        Assert-YLRegistryRelativePath $path
        if ([Text.Encoding]::UTF8.GetString($bytes, 0, [Math]::Min(200, $bytes.Length)).StartsWith('version https://git-lfs.github.com/spec/v1', [StringComparison]::Ordinal)) { throw 'Official SDK contains an unmaterialized LFS pointer.' }
        if ($path.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase)) {
            if ($bytes.Length -lt 64 -or $bytes[0] -ne 77 -or $bytes[1] -ne 90) { throw 'Official SDK DLL is not a PE binary.' }
            $offset = [BitConverter]::ToInt32($bytes, 60)
            if ($offset -lt 64 -or $offset -gt $bytes.Length - 24 -or [BitConverter]::ToUInt32($bytes, $offset) -ne 0x4550) { throw 'Official SDK DLL PE header is invalid.' }
        }
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        $canonical = if ($path -ceq 'package.json') { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($json.canonical))).ToLowerInvariant() } else { $hash }
        $files.Add($path, @{ path = $path; sha256 = $hash; contentSha256 = $canonical; length = [long]$bytes.Length })
    }
    $content = [Text.StringBuilder]::new()
    foreach ($row in $files.Values) { $null = $content.Append($row.path).Append([char]0).Append($row.contentSha256).Append("`n") }
    return [pscustomobject]@{ id = $manifest['name']; version = $manifest['version']; dependencies = $dependencies; manifest = $manifest; files = @($files.Values)
        payloadSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($content.ToString()))).ToLowerInvariant() }
}

function Read-YLRegistryTarball {
    <# No filesystem extraction occurs until the complete archive and package payload pass.
       files.sha256 always describes original bytes; only package.json has semantic contentSha256. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $Path, [string] $Destination, [ValidateSet('com.code-philosophy.hybridclr')][string] $SdkPackage)
    Set-StrictMode -Version 3.0
    $ErrorActionPreference = 'Stop'
    . (Join-Path $PSScriptRoot '../Testing/Common/Workspace.ps1')
    $Path = Get-YLTestPath $Path
    if (-not [IO.File]::Exists($Path)) { throw "Missing tar archive: $Path" }
    if ([IO.FileInfo]::new($Path).Length -ge 512MB) { throw 'Tar compressed size must be below 512 MiB.' }
    if ($Destination) {
        $Destination = Get-YLTestPath $Destination
        if ([IO.Path]::Exists($Destination)) { throw 'Tar extraction destination must not exist; no overwrite is permitted.' }
        if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($Destination))) { throw 'Tar destination parent must exist.' }
    }
    Initialize-YLRegistryArchiveReader
    $contents = [Collections.Generic.Dictionary[string, byte[]]]::new([StringComparer]::Ordinal)
    $names = [Collections.Generic.Dictionary[string, bool]]::new([StringComparer]::OrdinalIgnoreCase)
    $file = [IO.File]::Open($Path, 'Open', 'Read', 'Read')
    $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionMode]::Decompress, $true)
    $bounded = [YLRegistry.BoundedReadStream]::new($gzip, 512MB)
    $tar = [System.Formats.Tar.TarReader]::new($bounded, $true)
    try {
        $count = 0; $size = 0L
        while ($null -ne ($entry = $tar.GetNextEntry($false))) {
            $count++
            if ($count -gt 30000) { throw 'Tar entry count exceeds the package safety limit.' }
            $directory = $entry.EntryType -eq [System.Formats.Tar.TarEntryType]::Directory
            if (-not $directory -and $entry.EntryType -notin @([System.Formats.Tar.TarEntryType]::RegularFile, [System.Formats.Tar.TarEntryType]::V7RegularFile)) {
                throw "Tar links/special entry types are forbidden: $($entry.EntryType)"
            }
            if ($entry.LinkName) { throw 'Tar link targets are forbidden.' }
            $name = $entry.Name
            if ($directory -and $name.EndsWith('/')) { $name = $name.Substring(0, $name.Length - 1) }
            Assert-YLRegistryRelativePath $name
            if ($name -cne 'package' -and -not $name.StartsWith('package/', [StringComparison]::Ordinal)) { throw "Tar entry is outside package/: $name" }
            if ($name -ceq 'package' -and -not $directory) { throw 'Tar package root must be a directory.' }
            if ($names.ContainsKey($name)) { throw "Duplicate/case-colliding tar path: $name" }
            $ancestor = $name
            while ($ancestor.Contains('/')) {
                $ancestor = $ancestor.Substring(0, $ancestor.LastIndexOf('/'))
                if ($names.ContainsKey($ancestor) -and -not $names[$ancestor]) { throw "Tar file/directory collision: $name" }
            }
            if (-not $directory -and @($names.Keys | Where-Object { $_.StartsWith($name + '/', [StringComparison]::OrdinalIgnoreCase) }).Count) { throw "Tar file/directory collision: $name" }
            $names.Add($name, $directory)
            if ($entry.Length -lt 0 -or $entry.Length -ge 512MB -or ($directory -and $entry.Length -ne 0)) { throw 'Invalid tar entry size.' }
            $size += $entry.Length
            if ($size -ge 512MB) { throw 'Tar payload size must be below 512 MiB.' }
            if ($directory) { continue }
            $memory = [IO.MemoryStream]::new()
            try {
                if ($null -ne $entry.DataStream) { $entry.DataStream.CopyTo($memory) }
                if ($memory.Length -ne $entry.Length) { throw 'Truncated tar entry data.' }
                $contents.Add($name.Substring(8), $memory.ToArray())
            } finally { $memory.Dispose() }
        }
        # Consume the entire gzip stream (including trailer/concatenated members), not only TarReader's first EOF.
        $buffer = [byte[]]::new(8192)
        while (($read = $bounded.Read($buffer, 0, $buffer.Length)) -ne 0) {
            for ($i = 0; $i -lt $read; $i++) { if ($buffer[$i] -ne 0) { throw 'Unexpected trailing tar archive data.' } }
        }
        $bounded.VerifyComplete()
        $payload = if ($SdkPackage) { Get-YLRegistrySdkPayload $contents } else { Get-YLRegistryPayload $contents }
        foreach ($name in $names.Keys) {
            if (-not $names[$name] -or $name -ceq 'package') { continue }
            $relative = $name.Substring(8)
            if (-not $SdkPackage -and ($relative.Split('/')[0] -cnotin $payload.manifest['files'] -or
                (@($contents.Keys | Where-Object { $_.StartsWith($relative + '/', [StringComparison]::Ordinal) }).Count -eq 0 -and
                    (-not $contents.ContainsKey($relative + '.meta') -or [Text.Encoding]::UTF8.GetString($contents[$relative + '.meta']) -cnotmatch '(?m)^folderAsset: yes\s*$')))) {
                throw "Unauthorized or empty undeclared tar directory: $name"
            }
        }
        $file.Position = 0
        $sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($file)).ToLowerInvariant()
        $file.Position = 0
        $integrity = 'sha512-' + [Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData($file))
        if ($Destination) {
            $null = Get-YLTestPath $Destination
            if ([IO.Path]::Exists($Destination)) { throw 'Tar destination appeared during validation; refusing overwrite.' }
            $null = [IO.Directory]::CreateDirectory($Destination)
            foreach ($row in $payload.files) {
                $target = Get-YLTestPath (Join-Path $Destination $row.path)
                if (-not $target.StartsWith($Destination + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Tar extraction path escaped destination.' }
                $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
                $stream = [IO.File]::Open($target, 'CreateNew', 'Write', 'None')
                try { $stream.Write($contents[$row.path]) } finally { $stream.Dispose() }
            }
        }
        $payload | Add-Member -NotePropertyName path -NotePropertyValue $Path
        $payload | Add-Member -NotePropertyName sha256 -NotePropertyValue $sha256
        $payload | Add-Member -NotePropertyName integrity -NotePropertyValue $integrity
        return $payload
    } catch { throw "Tar archive validation failed: $($_.Exception.Message)" }
    finally { $tar.Dispose(); $bounded.Dispose(); $gzip.Dispose(); $file.Dispose() }
}

function Invoke-YLRegistryProcess {
    param([string] $Exe, [string[]] $Arguments, [string] $Directory, [string] $Log, [string] $EnvironmentRoot)
    $start = [Diagnostics.ProcessStartInfo]::new($Exe)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $Directory
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    foreach ($name in @($start.Environment.Keys)) {
        if ($name -match '^(?i:NPM_|NODE_|GIT_)') { $null = $start.Environment.Remove($name) }
    }
    foreach ($name in @('HOME', 'USERPROFILE', 'APPDATA', 'LOCALAPPDATA', 'TMP', 'TEMP')) { $start.Environment[$name] = $EnvironmentRoot }
    $start.Environment['GIT_CONFIG_NOSYSTEM'] = '1'
    $start.Environment['GIT_CONFIG_GLOBAL'] = Join-Path $EnvironmentRoot 'empty.gitconfig'
    $start.Environment['GIT_TERMINAL_PROMPT'] = '0'
    $start.Environment['GIT_LFS_SKIP_SMUDGE'] = '1'
    $start.Environment['NPM_CONFIG_USERCONFIG'] = Join-Path $EnvironmentRoot 'user.npmrc'
    $start.Environment['NPM_CONFIG_GLOBALCONFIG'] = Join-Path $EnvironmentRoot 'global.npmrc'
    $start.Environment['NPM_CONFIG_CACHE'] = Join-Path $EnvironmentRoot 'npm-cache'
    $start.Environment['NPM_CONFIG_PREFIX'] = Join-Path $EnvironmentRoot 'npm-prefix'
    $start.Environment['NPM_CONFIG_IGNORE_SCRIPTS'] = 'true'
    $start.Environment['NPM_CONFIG_AUDIT'] = 'false'
    $start.Environment['NPM_CONFIG_FUND'] = 'false'
    $start.Environment['NPM_CONFIG_UPDATE_NOTIFIER'] = 'false'
    $facts = Invoke-YLTestProcess -StartInfo $start -Log $Log -TimeoutMilliseconds 900000
    $terminal = Read-YLTestProcessResult -Log $Log
    if ($terminal.status -cne 'Exited' -or -not $terminal.observedExit -or $terminal.exitCode -ne 0 -or
        $terminal.forcedCleanup -or $terminal.cleanupError -or $terminal.observerError -or -not $terminal.streamsDrained -or
        -not $terminal.jobAssigned -or $terminal.activeProcesses -ne 0 -or $facts.status -cne 'Exited') {
        throw "Packaging child process failed ($($terminal.status)); inspect $Log"
    }
    return [IO.File]::ReadAllText($Log)
}

function Get-YLRegistryProducer {
    if ($env:GITHUB_ACTIONS -ceq 'true') {
        foreach ($value in @($env:GITHUB_REPOSITORY, $env:GITHUB_WORKFLOW, $env:GITHUB_RUN_ID, $env:GITHUB_RUN_ATTEMPT)) {
            if ([string]::IsNullOrWhiteSpace($value)) { throw 'GitHubActions producer requires repository/workflow/runId/runAttempt.' }
        }
        if ($env:GITHUB_RUN_ID -cnotmatch '^[1-9][0-9]*$' -or $env:GITHUB_RUN_ATTEMPT -cnotmatch '^[1-9][0-9]*$') { throw 'Invalid GitHubActions run identity.' }
        return [ordered]@{ kind = 'GitHubActions'; repository = $env:GITHUB_REPOSITORY; workflow = $env:GITHUB_WORKFLOW; runId = $env:GITHUB_RUN_ID; runAttempt = $env:GITHUB_RUN_ATTEMPT }
    }
    return [ordered]@{ kind = 'Local' }
}

function Get-YLRegistryGitOptions {
    param([string] $EnvironmentRoot)
    return @('-c', ('core.hooksPath=' + $EnvironmentRoot.Replace('\', '/')), '-c', 'core.autocrlf=false', '-c', 'core.longpaths=true',
        '-c', 'filter.lfs.required=true', '-c', 'filter.lfs.clean=git-lfs clean -- %f',
        '-c', 'filter.lfs.smudge=git-lfs smudge -- %f', '-c', 'filter.lfs.process=git-lfs filter-process')
}

function New-YLRegistryCheckout {
    param([string] $Repository, [string] $SourceCommit, [string] $Directory, [string] $EnvironmentRoot, [string] $LogRoot)
    . (Join-Path $PSScriptRoot '../Testing/Common/Process.ps1')
    if ($SourceCommit -cnotmatch '^[0-9a-f]{40}$') { throw 'Checkout requires a full lowercase hex commit.' }
    $Repository = Get-YLTestPath $Repository
    $Directory = Get-YLTestPath $Directory
    if ([IO.Path]::Exists($Directory) -or $Directory -ieq $Repository -or $Directory.StartsWith($Repository + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Independent checkout must be new and outside the source repository.'
    }
    $git = (Get-Command -Name git.exe -CommandType Application -TotalCount 1 -ErrorAction Stop).Source
    $options = Get-YLRegistryGitOptions $EnvironmentRoot
    $root = (Invoke-YLRegistryProcess -Exe $git -Arguments ($options + @('rev-parse', '--show-toplevel')) -Directory $Repository -Log (Join-Path $LogRoot 'source-root.log') -EnvironmentRoot $EnvironmentRoot).Trim()
    if ((Get-YLTestPath $root) -ine $Repository) { throw 'Repository must be the actual source repository root.' }
    $null = Invoke-YLRegistryProcess -Exe $git -Arguments ($options + @('clone', '--no-checkout', '--no-hardlinks', '--dissociate', '--', $Repository, $Directory)) -Directory ([IO.Path]::GetDirectoryName($Directory)) -Log (Join-Path $LogRoot 'clone.log') -EnvironmentRoot $EnvironmentRoot
    $actual = (Invoke-YLRegistryProcess -Exe $git -Arguments ($options + @('rev-parse', '--verify', "$SourceCommit^{commit}")) -Directory $Directory -Log (Join-Path $LogRoot 'commit.log') -EnvironmentRoot $EnvironmentRoot).Trim()
    if ($actual -cne $SourceCommit) { throw 'Resolved source commit does not match the requested identity.' }
    $null = Invoke-YLRegistryProcess -Exe $git -Arguments ($options + @('checkout', '--detach', $SourceCommit, '--')) -Directory $Directory -Log (Join-Path $LogRoot 'checkout.log') -EnvironmentRoot $EnvironmentRoot
    $head = (Invoke-YLRegistryProcess -Exe $git -Arguments ($options + @('rev-parse', 'HEAD')) -Directory $Directory -Log (Join-Path $LogRoot 'head.log') -EnvironmentRoot $EnvironmentRoot).Trim()
    if ($head -cne $SourceCommit) { throw 'Independent checkout HEAD differs from SourceCommit.' }
    $modes = Invoke-YLRegistryProcess -Exe $git -Arguments ($options + @('ls-tree', '-r', '--format=%(objectmode)', $SourceCommit, '--', 'PackageSources')) -Directory $Directory -Log (Join-Path $LogRoot 'product-modes.log') -EnvironmentRoot $EnvironmentRoot
    foreach ($mode in $modes.Split("`n", [StringSplitOptions]::RemoveEmptyEntries)) {
        if ($mode.Trim() -cnotin @('100644', '100755')) { throw 'Product Git links/submodules are forbidden in the independent checkout.' }
    }
    $null = Invoke-YLRegistryProcess -Exe $git -Arguments ($options + @('lfs', 'fetch', '--include=PackageSources/**', '--exclude=', 'origin', $SourceCommit)) -Directory $Directory -Log (Join-Path $LogRoot 'lfs-fetch.log') -EnvironmentRoot $EnvironmentRoot
    $null = Invoke-YLRegistryProcess -Exe $git -Arguments ($options + @('lfs', 'checkout', 'PackageSources/**')) -Directory $Directory -Log (Join-Path $LogRoot 'lfs-checkout.log') -EnvironmentRoot $EnvironmentRoot
    return $Directory
}

function New-YLRegistryReleaseManifest {
    param([string] $SourceCommit, [Collections.IDictionary] $Toolchain, [object[]] $Packages)
    if ($SourceCommit -cnotmatch '^[0-9a-f]{40}$') { throw 'Release source commit must be 40 lowercase hex.' }
    if ($null -eq $Toolchain -or $Toolchain['node'] -cne '22.23.3' -or $Toolchain['npm'] -cne '10.9.9' -or
        $Toolchain['powershell'] -isnot [string] -or $Toolchain['powershell'] -cnotmatch '^7\.6\.[0-9]+$') { throw 'Release toolchain must be Node 22.23.3 / npm 10.9.9 / PowerShell 7.6.x.' }
    Assert-YLRegistryPackageBatch $Packages
    $rows = @(foreach ($id in (Get-YLRegistryPackageIds)) {
        $package = @($Packages | Where-Object id -CEQ $id)[0]
        if ($package.sha256 -cnotmatch '^[0-9a-f]{64}$' -or $package.payloadSha256 -cnotmatch '^[0-9a-f]{64}$' -or
            $package.integrity -cnotmatch '^sha512-[A-Za-z0-9+/]{86}==$') { throw 'Release requires verified tarball and payload digests.' }
        [ordered]@{ id = $package.id; version = $package.version; dependencies = $package.dependencies
            assetName = $package.id + '-' + $package.version + '.tgz'; sha256 = $package.sha256
            integrity = $package.integrity; payloadSha256 = $package.payloadSha256
            files = @($package.files | ForEach-Object { [ordered]@{ path = $_.path; sha256 = $_.sha256; contentSha256 = $_.contentSha256 } }) }
    })
    return [ordered]@{ schemaVersion = 1; sourceCommit = $SourceCommit
        toolchain = [ordered]@{ node = $Toolchain['node']; npm = $Toolchain['npm']; powershell = $Toolchain['powershell'] }
        producer = Get-YLRegistryProducer; packages = $rows }
}

function Invoke-YLRegistryPackageBuild {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $Repository, [Parameter(Mandatory)][string] $SourceCommit, [Parameter(Mandatory)][string] $OutputRoot)
    Set-StrictMode -Version 3.0
    $ErrorActionPreference = 'Stop'
    . (Join-Path $PSScriptRoot '../Testing/Common/Process.ps1')
    if (-not $IsWindows -or $PSVersionTable.PSVersion.Major -ne 7 -or $PSVersionTable.PSVersion.Minor -ne 6) { throw 'Packaging requires Windows PowerShell Core 7.6.x.' }
    if ($SourceCommit -cnotmatch '^[0-9a-f]{40}$') { throw 'SourceCommit must be a full 40 lowercase hex commit.' }
    $Repository = Get-YLTestPath $Repository
    $OutputRoot = Get-YLTestPath $OutputRoot
    if ($OutputRoot -ieq $Repository -or $OutputRoot.StartsWith($Repository + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $Repository.StartsWith($OutputRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or $OutputRoot.Length -le 3) {
        throw 'OutputRoot must be outside the source repository, not its root or ancestor.'
    }
    $workspace = Initialize-YLTestWorkspace -WorkspaceRoot $OutputRoot -Repository $Repository
    $run = New-YLTestDirectory -Workspace $workspace -Kind Result -Key RegistryPackages
    $cache = New-YLTestDirectory -Workspace $workspace -Kind Cache -Key ('registry-' + [Guid]::NewGuid().ToString('N'))
    $hostRecord = New-YLTestDirectory -Workspace $workspace -Kind Host -Key ('registry-' + [Guid]::NewGuid().ToString('N'))
    $leases = [Collections.Generic.List[IDisposable]]::new()
    try {
        foreach ($owner in @($run, $cache, $hostRecord)) { $leases.Add((Lock-YLTestDirectory -Workspace $workspace -Path $owner.path)) }
        foreach ($name in @('empty.gitconfig', 'user.npmrc', 'global.npmrc')) {
            $stream = [IO.File]::Open((Join-Path $cache.path $name), 'CreateNew', 'Write', 'None'); $stream.Dispose()
        }
        $node = (Get-Command -Name node.exe -CommandType Application -TotalCount 1 -ErrorAction Stop).Source
        $git = (Get-Command -Name git.exe -CommandType Application -TotalCount 1 -ErrorAction Stop).Source
        $nodeVersion = (Invoke-YLRegistryProcess -Exe $node -Arguments @('--version') -Directory $cache.path -Log (Join-Path $run.path 'node-version.log') -EnvironmentRoot $cache.path).Trim()
        if ($nodeVersion -cne 'v22.23.3') { throw "PrerequisiteNotMet: expected Node.js 22.23.3, found $nodeVersion. No global toolchain changes are performed." }
        $npm = Join-Path ([IO.Path]::GetDirectoryName($node)) 'node_modules/npm/bin/npm-cli.js'
        if (-not [IO.File]::Exists($npm)) { throw 'PrerequisiteNotMet: the fixed Node installation must include npm 10.9.9.' }
        $npmVersion = (Invoke-YLRegistryProcess -Exe $node -Arguments @($npm, '--version') -Directory $cache.path -Log (Join-Path $run.path 'npm-version.log') -EnvironmentRoot $cache.path).Trim()
        if ($npmVersion -cne '10.9.9') { throw "PrerequisiteNotMet: expected npm 10.9.9, found $npmVersion." }
        $gitOptions = Get-YLRegistryGitOptions $cache.path
        $checkout = New-YLRegistryCheckout -Repository $Repository -SourceCommit $SourceCommit -Directory (Join-Path $hostRecord.path 'repository') -EnvironmentRoot $cache.path -LogRoot $run.path
        $dirty = Invoke-YLRegistryProcess -Exe $git -Arguments ($gitOptions + @('status', '--porcelain=v1', '--untracked-files=all', '--', 'PackageSources')) -Directory $checkout -Log (Join-Path $run.path 'source-status-before.log') -EnvironmentRoot $cache.path
        if ($dirty.Trim()) { throw 'Fixed-commit product checkout must be clean before packaging.' }
        $packages = @(Get-YLRegistryPackageSet -Root (Join-Path $checkout 'PackageSources'))
        $artifacts = Join-Path $run.path 'artifacts'; $repack = Join-Path $run.path 'repack'
        $null = [IO.Directory]::CreateDirectory($artifacts); $null = [IO.Directory]::CreateDirectory($repack)
        $frozen = [Collections.Generic.List[object]]::new()
        foreach ($package in $packages) {
            $first = $null
            foreach ($pass in @('artifacts', 'repack')) {
                $destination = if ($pass -ceq 'artifacts') { $artifacts } else { $repack }
                $log = Join-Path $run.path ($package.id + '-' + $pass + '.log')
                $output = Invoke-YLRegistryProcess -Exe $node -Arguments @($npm, 'pack', '--ignore-scripts', '--offline', '--json', '--pack-destination', $destination) -Directory $package.root -Log $log -EnvironmentRoot $cache.path
                $packed = ConvertFrom-Json -InputObject $output -AsHashtable -NoEnumerate -DateKind String
                $assetName = $package.id + '-' + $package.version + '.tgz'
                if ($packed -isnot [array] -or $packed.Count -ne 1 -or $packed[0]['filename'] -cne $assetName) { throw 'npm pack did not produce the exact expected asset filename.' }
                $payload = Read-YLRegistryTarball -Path (Join-Path $destination $assetName)
                if ($payload.id -cne $package.id -or $payload.version -cne $package.version -or $payload.payloadSha256 -cne $package.payloadSha256) { throw "npm packed content differs from fixed-commit package inputs: $($package.id)" }
                if ($null -eq $first) { $first = $payload } elseif ($payload.payloadSha256 -cne $first.payloadSha256) { throw 'Same-commit/toolchain repack content is not reproducible.' }
            }
            $frozen.Add($first)
        }
        Assert-YLRegistryPackageBatch $frozen.ToArray()
        $after = @(Get-YLRegistryPackageSet -Root (Join-Path $checkout 'PackageSources'))
        foreach ($package in $after) { if ($package.payloadSha256 -cne @($packages | Where-Object id -CEQ $package.id)[0].payloadSha256) { throw 'Product inputs changed during packaging.' } }
        $dirty = Invoke-YLRegistryProcess -Exe $git -Arguments ($gitOptions + @('status', '--porcelain=v1', '--untracked-files=all', '--', 'PackageSources')) -Directory $checkout -Log (Join-Path $run.path 'source-status-after.log') -EnvironmentRoot $cache.path
        if ($dirty.Trim()) { throw 'Fixed-commit product checkout changed during packaging.' }
        foreach ($payload in $frozen) {
            if ((Get-FileHash -LiteralPath $payload.path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $payload.sha256) { throw 'Frozen tarball changed before manifest publication.' }
        }
        $manifest = New-YLRegistryReleaseManifest -SourceCommit $SourceCommit -Toolchain @{ node = $nodeVersion.Substring(1); npm = $npmVersion; powershell = $PSVersionTable.PSVersion.ToString() } -Packages $frozen.ToArray()
        $path = Join-Path $artifacts 'release-manifest.json'
        Write-YLTestJson -Path $path -Value $manifest
        return [pscustomobject]@{ releaseManifest = $path; artifacts = $artifacts; sourceCommit = $SourceCommit; producer = $manifest.producer; packageCount = $frozen.Count }
    } finally { foreach ($lease in $leases) { $lease.Dispose() } }
}

if ($MyInvocation.InvocationName -ne '.') {
    if (-not $YLRegistryBuildRepository -or -not $YLRegistryBuildSourceCommit -or -not $YLRegistryBuildOutputRoot) { throw 'Build requires -Repository -SourceCommit -OutputRoot. Dot-source only to load function definitions.' }
    Invoke-YLRegistryPackageBuild -Repository $YLRegistryBuildRepository -SourceCommit $YLRegistryBuildSourceCommit -OutputRoot $YLRegistryBuildOutputRoot
}
