#requires -Version 7.6
#requires -PSEdition Core

function Get-YLTestEditorProfile {
    param([object[]] $Editors, [object] $VersionInfo)
    $fileVersion = "$($VersionInfo.FileMajorPart).$($VersionInfo.FileMinorPart).$($VersionInfo.FileBuildPart)"
    $matches = @($Editors | Where-Object { $_.fileVersion -ceq $fileVersion -and $_.productVersion -ceq $VersionInfo.ProductVersion })
    if ($matches.Count -ne 1) { throw "Unsupported or ambiguous Unity build: $fileVersion / $($VersionInfo.ProductVersion)" }
    return $matches[0]
}

function Get-YLTestPath {
    param([AllowEmptyString()][string] $Path, [switch] $ReadOnly)
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -notmatch '^[A-Za-z]:[\\/]' -or
        -not [IO.Path]::IsPathFullyQualified($Path) -or $Path.Substring(2).Contains(':') -or $Path -match '[<>"|?*]') {
        throw "An explicit local filesystem path is required: $Path"
    }
    foreach ($part in $Path.Substring(3).Split([char[]]'\/')) {
        if ($part -in @('.', '..') -or $part.EndsWith(' ') -or $part.EndsWith('.')) { throw "Ambiguous path: $Path" }
        if ($part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') { throw "Reserved Windows path: $Path" }
    }
    $full = [IO.Path]::GetFullPath($Path)
    if ($full.Length -gt 3) { $full = $full.TrimEnd('\') }
    for ($cursor = $full; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        try { $attributes = [IO.File]::GetAttributes($cursor) }
        catch [IO.FileNotFoundException] { continue }
        catch [IO.DirectoryNotFoundException] { continue }
        if (-not $ReadOnly -and ($attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Linked paths are refused: $cursor" }
    }
    return $full
}

function Write-YLTestJson {
    param([string] $Path, [object] $Value)
    $Path = Get-YLTestPath $Path
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $Value -Depth 40))
    $stream = [IO.File]::Open($Path, 'CreateNew', 'Write', 'None')
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
}

function Assert-YLTestWorkspace {
    param([object] $Workspace)
    $root = Get-YLTestPath $Workspace.root
    $marker = Get-YLTestPath (Join-Path $root '.yl-workspace.json')
    if (-not [IO.File]::Exists($marker)) { throw "Unknown workspace owner: $root" }
    $owner = [IO.File]::ReadAllText($marker) | ConvertFrom-Json -AsHashtable -DateKind String
    foreach ($field in @('purpose', 'root', 'repository', 'id')) {
        if ($owner[$field] -isnot [string]) { throw 'Workspace identity fields must be strings.' }
    }
    if ($owner['purpose'] -cne 'YLTesting.Workspace' -or $owner['schemaVersion'] -isnot [long] -or $owner['schemaVersion'] -ne 1 -or
        $owner['root'] -ine $root -or $owner['repository'] -ine $Workspace.repository -or
        $owner['id'] -cne $Workspace.id -or $owner['id'] -cnotmatch '^[0-9a-f]{32}$') {
        throw "Workspace identity/repository mismatch: $root"
    }
}

function New-YLTestContainer {
    param([object] $Workspace, [string] $Path, [string] $Kind, [string] $Key, [switch] $ExistingOnly, [switch] $NewOnly)
    Assert-YLTestWorkspace $Workspace
    $Path = Get-YLTestPath $Path
    if (-not $Path.StartsWith($Workspace.root + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'An owned container must be inside the workspace root.'
    }
    $marker = Join-Path $Path '.yl-owner.json'
    if ([IO.Path]::Exists($Path)) {
        if ($NewOnly) { throw "A fresh output is required; the path already exists: $Path" }
        if (-not [IO.File]::Exists($marker)) { throw "Unknown container owner: $Path" }
        $null = Get-YLTestPath $marker
        $owner = [IO.File]::ReadAllText($marker) | ConvertFrom-Json -AsHashtable -DateKind String
        foreach ($field in @('purpose', 'workspaceId', 'path', 'kind', 'key', 'id')) {
            if ($owner[$field] -isnot [string]) { throw 'Container ownership fields must be strings.' }
        }
        if ($owner['purpose'] -cne 'YLTesting.Directory' -or $owner['schemaVersion'] -isnot [long] -or $owner['schemaVersion'] -ne 1 -or
            $owner['workspaceId'] -cne $Workspace.id -or $owner['path'] -ine $Path -or
            $owner['kind'] -cne $Kind -or $owner['key'] -cne $Key -or $owner['id'] -cnotmatch '^[0-9a-f]{32}$') {
            throw "Container ownership mismatch: $Path"
        }
        return [pscustomobject]$owner
    }
    if ($ExistingOnly) { throw "Missing owned container: $Path" }
    if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($Path))) { throw "Missing container parent: $Path" }
    $null = New-Item -ItemType Directory -Path $Path -ErrorAction Stop
    $owner = [ordered]@{
        schemaVersion = 1; purpose = 'YLTesting.Directory'; workspaceId = $Workspace.id
        path = $Path; kind = $Kind; key = $Key; id = [Guid]::NewGuid().ToString('N'); createdUtc = [DateTime]::UtcNow.ToString('o')
    }
    Write-YLTestJson -Path $marker -Value $owner
    $stream = [IO.File]::Open((Join-Path $Path '.lease'), 'CreateNew', 'Write', 'None')
    $stream.Dispose()
    return [pscustomobject]$owner
}

function Get-YLTestWorkspacePaths {
    param([string] $WorkspaceRoot, [string] $Repository)
    $root = Get-YLTestPath $WorkspaceRoot
    $Repository = Get-YLTestPath $Repository
    if ($root.Length -le 3 -or $root -ieq $Repository -or $root.StartsWith($Repository + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $Repository.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A testing workspace must be outside the source repository and cannot be its ancestor or a drive root.'
    }
    for ($cursor = $root; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        $leaf = [IO.Path]::GetFileName($cursor)
        if ($leaf -in 'Archive', 'Results' -and
            [IO.File]::Exists((Join-Path ([IO.Path]::GetDirectoryName($cursor)) '.yl-workspace.json'))) {
            if ($leaf -ieq 'Archive') { throw 'A workspace Archive is read-only history, not an active test root.' }
            throw 'A managed workspace Results area cannot contain another workspace.'
        }
    }
    if (-not [IO.Directory]::Exists($Repository)) { throw "Missing source repository: $Repository" }
    return [pscustomobject]@{ root = $root; repository = $Repository }
}

function Initialize-YLTestWorkspace {
    param([string] $WorkspaceRoot, [string] $Repository)
    if ([string]::IsNullOrWhiteSpace($WorkspaceRoot)) { $WorkspaceRoot = $env:YL_TEST_WORKSPACE }
    $paths = Get-YLTestWorkspacePaths -WorkspaceRoot $WorkspaceRoot -Repository $Repository
    $root = $paths.root
    $Repository = $paths.repository
    $marker = Join-Path $root '.yl-workspace.json'
    if ([IO.Path]::Exists($root)) {
        if (-not [IO.File]::Exists($marker)) { throw "Never adopt an unknown workspace owner: $root" }
        $null = Get-YLTestPath $marker
        $workspace = [pscustomobject]([IO.File]::ReadAllText($marker) | ConvertFrom-Json -AsHashtable -DateKind String)
        if ($workspace.root -ine $root -or $workspace.repository -ine $Repository) { throw 'Workspace repository/identity mismatch.' }
        Assert-YLTestWorkspace $workspace
    } else {
        if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($root))) { throw "Missing workspace parent: $root" }
        $null = New-Item -ItemType Directory -Path $root -ErrorAction Stop
        $workspace = [pscustomobject][ordered]@{
            schemaVersion = 1; purpose = 'YLTesting.Workspace'; id = [Guid]::NewGuid().ToString('N')
            root = $root; repository = $Repository; createdUtc = [DateTime]::UtcNow.ToString('o')
        }
        Write-YLTestJson -Path $marker -Value $workspace
    }
    foreach ($area in @('Hosts', 'Cache', 'Results')) {
        $null = New-YLTestContainer -Workspace $workspace -Path (Join-Path $root $area) -Kind Area -Key $area
    }
    return $workspace
}

function New-YLTestDirectory {
    param(
        [object] $Workspace,
        [ValidateSet('Result', 'Host', 'Cache')][string] $Kind,
        [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$')][string] $Key,
        [switch] $NewOnly
    )
    Assert-YLTestWorkspace $Workspace
    $area = switch ($Kind) { Result { 'Results' }; Host { 'Hosts' }; Cache { 'Cache' } }
    $parent = Join-Path $Workspace.root $area
    $null = New-YLTestContainer -Workspace $Workspace -Path $parent -Kind Area -Key $area
    $path = Join-Path $parent $Key
    if ($Kind -eq 'Result') {
        $null = New-YLTestContainer -Workspace $Workspace -Path $path -Kind ResultGroup -Key $Key
        $path = Join-Path $path ([Guid]::NewGuid().ToString('N'))
    }
    return New-YLTestContainer -Workspace $Workspace -Path $path -Kind $Kind -Key $Key -NewOnly:($NewOnly -or $Kind -eq 'Result')
}

function Get-YLTestDirectory {
    param([object] $Workspace, [string] $Path)
    Assert-YLTestWorkspace $Workspace
    $Path = Get-YLTestPath $Path
    if (-not $Path.StartsWith($Workspace.root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Target is outside the owned root.' }
    $marker = Get-YLTestPath (Join-Path $Path '.yl-owner.json')
    if (-not [IO.File]::Exists($marker)) { throw "Unknown directory owner: $Path" }
    $owner = [IO.File]::ReadAllText($marker) | ConvertFrom-Json -AsHashtable -DateKind String
    foreach ($field in @('purpose', 'workspaceId', 'path', 'kind', 'key', 'id')) {
        if ($owner[$field] -isnot [string]) { throw 'Directory ownership fields must be strings.' }
    }
    if ($owner['kind'] -cnotin @('Result', 'Host', 'Cache')) { throw 'Workspace/area/group containers cannot be removed or leased as outputs.' }
    $area = switch ($owner['kind']) { Result { 'Results' }; Host { 'Hosts' }; Cache { 'Cache' } }
    $expected = Join-Path (Join-Path $Workspace.root $area) $owner['key']
    if ($owner['kind'] -ceq 'Result') { $expected = Join-Path $expected (Split-Path -Leaf $Path) }
    if ($owner['purpose'] -cne 'YLTesting.Directory' -or $owner['schemaVersion'] -isnot [long] -or $owner['schemaVersion'] -ne 1 -or
        $owner['path'] -ine $Path -or $owner['workspaceId'] -cne $Workspace.id -or $expected -ine $Path -or
        $owner['id'] -cnotmatch '^[0-9a-f]{32}$' -or $owner['key'] -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$') {
        throw "Directory ownership mismatch: $Path"
    }
    return [pscustomobject]$owner
}

function Lock-YLTestDirectory {
    param([object] $Workspace, [string] $Path)
    $owner = Get-YLTestDirectory -Workspace $Workspace -Path $Path
    $lease = Get-YLTestPath (Join-Path $owner.path '.lease')
    try { return [IO.File]::Open($lease, 'Open', 'ReadWrite', 'Delete') }
    catch [IO.IOException] { throw "Directory is in use or its lease is unavailable: $Path" }
}

function Get-YLTestTreeFiles {
    param([string] $Path, [switch] $ForCleanup, [switch] $ProtectNested)
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push((Get-YLTestPath $Path))
    while ($pending.Count -ne 0) {
        $current = $pending.Pop()
        $attributes = [IO.File]::GetAttributes($current)
        if ($attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked cleanup entry is refused: $current" }
        if ($ProtectNested -and $current -ine $Path) {
            $leaf = [IO.Path]::GetFileName($current)
            if ($leaf -in '.pinned','.git' -or
                ($leaf -in '.yl-owner.json','.lease' -and [IO.Path]::GetDirectoryName($current) -ine $Path)) {
                throw "Protected nested evidence in one-shot host: $current"
            }
        }
        if ($attributes -band [IO.FileAttributes]::Directory) {
            foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($current)) {
                if (($ForCleanup -or $ProtectNested) -and [IO.Path]::GetFileName($entry) -ieq '.yl-workspace.json') {
                    throw "Protected nested workspace in cleanup target: $entry"
                }
                $pending.Push($entry)
            }
        } else { $current }
    }
}

function Remove-YLTestDirectory {
    [CmdletBinding(SupportsShouldProcess)]
    param([object] $Workspace, [string] $Path, [switch] $Apply, [string] $ExpectedId, [IO.FileStream] $HeldLease)
    $owner = Get-YLTestDirectory -Workspace $Workspace -Path $Path
    if ($ExpectedId -and $owner.id -cne $ExpectedId) { throw 'Output ownership changed before cleanup.' }
    $Path = $owner.path
    $pin = Join-Path $Path '.pinned'
    if ([IO.Path]::Exists($pin)) { throw "Pinned output is protected: $Path" }
    $handles = [Collections.Generic.List[IO.FileStream]]::new()
    try {
        if ($null -eq $HeldLease) { $handles.Add((Lock-YLTestDirectory -Workspace $Workspace -Path $Path)) }
        elseif (-not $ExpectedId -or -not $HeldLease.CanWrite -or $HeldLease.Name -ine (Join-Path $Path '.lease')) {
            throw 'Cleanup requires the original lease and matching output identity.'
        }
        $currentOwner = Get-YLTestDirectory -Workspace $Workspace -Path $Path
        if ($currentOwner.id -cne $owner.id) { throw 'Output ownership changed during cleanup.' }
        $files = @(Get-YLTestTreeFiles $Path -ForCleanup -ProtectNested:([bool]$ExpectedId))
        foreach ($file in $files) {
            if ($file -ieq (Join-Path $Path '.lease')) { continue }
            try { $handles.Add([IO.File]::Open($file, 'Open', 'Read', 'Delete')) }
            catch [IO.IOException] { throw "Output file is in use: $file" }
        }
        $remove = $Apply -and $PSCmdlet.ShouldProcess($Path, 'Remove owned test output')
        # Recheck the tree after a potential confirmation prompt.
        $null = @(Get-YLTestTreeFiles $Path -ForCleanup -ProtectNested:([bool]$ExpectedId))
        if ([IO.Path]::Exists($pin)) { throw "Pinned output is protected: $Path" }
        if ($remove) {
            [IO.Directory]::Delete($Path, $true)
            return [pscustomobject]@{ path = $Path; status = 'Removed' }
        }
        return [pscustomobject]@{ path = $Path; status = 'Preview' }
    } finally {
        foreach ($handle in $handles) { $handle.Dispose() }
    }
}
