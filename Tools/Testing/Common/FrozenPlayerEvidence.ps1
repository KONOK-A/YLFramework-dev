#requires -Version 7.6
#requires -PSEdition Core

function Get-YLFrozenWin64Module {
    param([string] $Unity, [ValidateSet('IL2CPP','Mono', IgnoreCase = $false)][string] $Backend)
    $editor = [IO.Path]::GetDirectoryName($Unity)
    $template = Join-Path $editor ('Data/PlaybackEngines/WindowsStandaloneSupport/Variations/win64_player_nondevelopment_' + $Backend.ToLowerInvariant())
    $runtime = if ($Backend -ceq 'Mono') { Join-Path $template 'MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll' }
        else { Join-Path $editor 'Data/il2cpp/build/deploy/il2cpp.exe' }
    $player = Join-Path $template 'WindowsPlayer.exe'
    if (-not [IO.File]::Exists($player) -or -not [IO.File]::Exists($runtime)) {
        throw "PrerequisiteNotMet: the selected Editor is missing its Win64 Release $Backend module."
    }
    return @($player, $runtime)
}

function Test-YLFrozenAmd64 {
    param([string] $Path)
    $inputStream = [IO.File]::OpenRead($Path)
    try {
        $pe = [Reflection.PortableExecutable.PEReader]::new($inputStream)
        try {
            return $pe.PEHeaders.CoffHeader.Machine -eq [Reflection.PortableExecutable.Machine]::Amd64 -and
                $pe.PEHeaders.PEHeader.Magic -eq [Reflection.PortableExecutable.PEMagic]::PE32Plus -and -not $pe.HasMetadata
        } finally { $pe.Dispose() }
    } finally { $inputStream.Dispose() }
}

function Assert-YLFrozenPlayerBinaries {
    param([string] $Player, [Collections.IDictionary] $Build, [ValidateSet('IL2CPP','Mono', IgnoreCase = $false)][string] $Backend,
        [string] $ExtensionAssembly, [string] $ProbeAssembly = 'YLFramework.FrozenEncryption.Fixture', [string] $NativePluginSha256)
    $exe = [IO.Path]::GetFileName($Player)
    $root = [IO.Path]::GetDirectoryName($Player)
    $data = [IO.Path]::GetFileNameWithoutExtension($Player) + '_Data'
    $expected = @($exe)
    $native = @($exe)
    if ($Backend -ceq 'Mono') {
        $mono = 'MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll'
        $native += $mono
        $expected += @($mono, "$data/Managed/YLFramework.Runtime.dll", "$data/Managed/$ProbeAssembly.dll")
        if ($ExtensionAssembly) { $expected += "$data/Managed/$ExtensionAssembly.dll" }
        elseif ([IO.File]::Exists((Join-Path $root "$data/Managed/YLFramework.Resource.Encryption.dll")) -or
            [IO.File]::Exists((Join-Path $root "$data/Managed/YLFramework.ResourceEncryption.P0.dll"))) { throw 'A removed extension left a Mono assembly.' }
        $absent = @('GameAssembly.dll', "$data/il2cpp_data/Metadata/global-metadata.dat")
        $stripping = 'Disabled'
    } else {
        $native += 'GameAssembly.dll'
        $expected += @('GameAssembly.dll', "$data/il2cpp_data/Metadata/global-metadata.dat")
        $absent = @('MonoBleedingEdge', "$data/Managed/YLFramework.Runtime.dll")
        $stripping = 'Medium'
    }
    $evidence = $Build['binaries']
    if ($Build['backend'] -isnot [string] -or $Build['backend'] -cne $Backend -or
        $Build['stripping'] -isnot [string] -or $Build['stripping'] -cne $stripping -or
        $evidence -isnot [Collections.IDictionary] -or $evidence.Count -ne $expected.Count) { throw 'Player backend, stripping or binary inventory differs.' }
    foreach ($name in $absent) {
        if ([IO.Path]::Exists((Join-Path $root $name))) { throw 'Player output contains a binary from another scripting backend.' }
    }
    foreach ($name in $expected) {
        $hash = $evidence[$name]
        $path = Get-YLTestPath (Join-Path $root $name)
        if ($hash -isnot [string] -or $hash -cnotmatch '^[0-9a-f]{64}$' -or -not [IO.File]::Exists($path) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $hash -or
            ($name -cin $native -and -not (Test-YLFrozenAmd64 $path))) { throw "Player binary hash or AMD64 identity differs: $name" }
    }
    if ($PSBoundParameters.ContainsKey('NativePluginSha256')) {
        $path = Get-YLTestPath (Join-Path $root "$data/Plugins/x86_64/ylresource_tink.dll")
        $nativePlugins = @(Get-YLTestTreeFiles $root | Where-Object { [IO.Path]::GetFileName($_) -ieq 'ylresource_tink.dll' })
        if ($NativePluginSha256 -cnotmatch '^[0-9a-f]{64}$' -or $nativePlugins.Count -ne 1 -or $nativePlugins[0] -ine $path -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $NativePluginSha256 -or -not (Test-YLFrozenAmd64 $path)) {
            throw 'Player native plugin path, bytes or architecture differs from the frozen package.'
        }
    }
}

function Assert-YLFrozenPlayerBackend {
    param([Collections.IDictionary] $Row, [string] $Expected)
    if ($null -eq $Row -or $Row['backend'] -isnot [string] -or $Row['backend'] -cne $Expected -or
        $Row['architecture'] -isnot [string] -or $Row['architecture'] -cne 'X64' -or
        ($Row['bits'] -isnot [int] -and $Row['bits'] -isnot [long]) -or $Row['bits'] -ne 64 -or
        $Row['graphics'] -isnot [string] -or $Row['graphics'] -cin @('', 'Null')) { throw 'Actual Win64 Release graphics Player backend evidence differs.' }
}

function Invoke-YLFrozenReadOnlyInstallation {
    param([string] $OwnedHostRoot, [string] $InstallRoot, [scriptblock] $RunPlayer,
        [ValidateSet('layout-offline', 'P', IgnoreCase = $false)][string] $InstallDirectoryName = 'layout-offline')
    $root = Get-YLTestPath $InstallRoot
    if ([IO.Path]::GetDirectoryName($root) -ine (Get-YLTestPath $OwnedHostRoot) -or [IO.Path]::GetFileName($root) -cne $InstallDirectoryName) {
        throw 'Read-only testing may only seal the separate offline installation under its owned host.'
    }
    $snapshot = {
        $result = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
        $pending = [Collections.Generic.Stack[string]]::new()
        $pending.Push($root)
        while ($pending.Count) {
            $path = Get-YLTestPath $pending.Pop()
            if ([IO.Directory]::Exists($path)) {
                $result.Add($path, 'directory')
                foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($path)) { $pending.Push($entry) }
            } else { $result.Add($path, (Get-YLFrozenFileSha256 $path)) }
        }
        return $result
    }
    $before = & $snapshot
    $user = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { $sid = $user.User } finally { $user.Dispose() }
    $rights = [Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles
    $deny = [Security.AccessControl.FileSystemAccessRule]::new($sid, $rights, 'Deny')
    $section = [Security.AccessControl.AccessControlSections]::Access
    $sealed = [Collections.Generic.List[object]]::new()
    try {
        foreach ($path in $before.Keys) {
            $item = if ($before[$path] -ceq 'directory') { [IO.DirectoryInfo]::new($path) } else { [IO.FileInfo]::new($path) }
            $acl = [IO.FileSystemAclExtensions]::GetAccessControl($item, $section)
            $record = @{ item = $item; acl = $acl; original = $acl.GetSecurityDescriptorSddlForm($section) }
            $sealed.Add($record)
            $acl.AddAccessRule($deny)
            [IO.FileSystemAclExtensions]::SetAccessControl($item, $acl)
            $record.applied = ([IO.FileSystemAclExtensions]::GetAccessControl($item, $section)).GetSecurityDescriptorSddlForm($section)
        }
        $process = & $RunPlayer
        $after = & $snapshot
        if ($before.Count -ne $after.Count) { throw 'The offline Player changed the installation inventory.' }
        foreach ($entry in $sealed) {
            $path = $entry.item.FullName
            if (-not $after.ContainsKey($path) -or $before[$path] -cne $after[$path] -or
                ([IO.FileSystemAclExtensions]::GetAccessControl($entry.item, $section)).GetSecurityDescriptorSddlForm($section) -cne $entry.applied) {
                throw "The offline Player changed the installation bytes, paths or ACL: $path"
            }
        }
        return @{ process = $process; proof = @{ unchanged = $true; directories = @($before.Values | Where-Object { $_ -ceq 'directory' }).Count;
                files = @($before.Values | Where-Object { $_ -cne 'directory' }).Count } }
    } finally {
        $restoreFailures = [Collections.Generic.List[string]]::new()
        foreach ($entry in $sealed) {
            try {
                $null = Get-YLTestPath $entry.item.FullName
                $entry.acl.SetSecurityDescriptorSddlForm($entry.original, $section)
                [IO.FileSystemAclExtensions]::SetAccessControl($entry.item, $entry.acl)
            } catch { $restoreFailures.Add($_.Exception.Message) }
        }
        if ($restoreFailures.Count) { throw ('Could not restore the offline fixture access rules: ' + ($restoreFailures -join '; ')) }
    }
}
