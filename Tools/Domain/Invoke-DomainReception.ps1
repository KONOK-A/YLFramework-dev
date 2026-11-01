#requires -Version 5.1
<#
Test-only MC26 receiver orchestration. Run each action explicitly in Windows PowerShell.
Create/Import/Prepare/Build* launch one owned Editor and wait for its OS exit.
AuditPackage/InstallConsumer do not launch Unity. No action launches a Player.
Order: Create, AuditPackage, Import, InstallConsumer, Prepare, BuildMono/BuildIL2CPP.
Use a new Run for a new attempt; Prepare and its one Build* use the same Run.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Create', 'CompleteCreate', 'Import', 'CompleteImport', 'AuditPackage', 'InstallConsumer', 'Prepare', 'BuildMono', 'BuildIL2CPP', 'InstallExcel', 'EnableExcel', 'TestExcel', 'AuditExcel')]
    [string] $Action,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^mc26-receiver-[a-z0-9]+(?:-[a-z0-9]+)*$')]
    [string] $Run,
    [ValidateSet('Mono2x', 'IL2CPP')][string] $Backend = 'Mono2x',
    [ValidateRange(30, 14400)][int] $TimeoutSeconds = 3600
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$Action = @('Create', 'CompleteCreate', 'Import', 'CompleteImport', 'AuditPackage', 'InstallConsumer', 'Prepare', 'BuildMono', 'BuildIL2CPP', 'InstallExcel', 'EnableExcel', 'TestExcel', 'AuditExcel') | Where-Object { $_ -ieq $Action }
$Backend = if ($Backend -ieq 'IL2CPP') { 'IL2CPP' } else { 'Mono2x' }
if ($Run -cnotmatch '^mc26-receiver-[a-z0-9]+(?:-[a-z0-9]+)*$') { throw 'Run must use the exact lowercase MC26 receiver namespace.' }
if ($PSBoundParameters.ContainsKey('Backend') -and (($Action -ceq 'BuildMono' -and $Backend -cne 'Mono2x') -or ($Action -ceq 'BuildIL2CPP' -and $Backend -cne 'IL2CPP'))) { throw 'Explicit Backend contradicts the build Action.' }
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')).TrimEnd('\', '/')
$delivery = [IO.Path]::GetFullPath('G:/Unity/MC26-20261020T173208Z')
$project = Join-Path $delivery 'Receiver'
$reception = Join-Path $delivery 'Reception'
$package = Join-Path $delivery 'YLFramework-C.unitypackage'
$sourceCommit = '53ebc2842f180fece5e3e87014524cd3b41c397e'
$cCommit = 'aadc11b5099b76f16667aa9818ca927104cd12be'
$purpose = 'MC26-C-Reception'
$utf8 = [Text.UTF8Encoding]::new($false)
$requiredAssemblies = @('YLFramework.Runtime', 'YLFramework.Editor', 'YLFramework.ResourceStartup.Sample', 'YLFramework.ResourceStartup.Sample.Editor')
if ($Action -eq 'BuildMono') { $Backend = 'Mono2x' }
if ($Action -eq 'BuildIL2CPP') { $Backend = 'IL2CPP' }

function Reject-Link([string] $Path) {
    for ($cursor = [IO.Path]::GetFullPath($Path); $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if ((Test-Path -LiteralPath $cursor) -and (([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "Linked path rejected: $cursor"
        }
    }
}
function New-OwnedDirectory([string] $Path) {
    Reject-Link $Path
    if (Test-Path -LiteralPath $Path) { throw "Preserve existing directory: $Path" }
    $parent = [IO.Path]::GetDirectoryName($Path)
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) { throw "Missing explicit parent: $parent" }
    $null = [IO.Directory]::CreateDirectory($Path)
}
function Hash([string] $Path) {
    Reject-Link $Path
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try { ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}
function Write-NewText([string] $Path, [string] $Text) {
    Reject-Link $Path
    if (-not (Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($Path)) -PathType Container)) { throw 'Missing output parent.' }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $bytes = $utf8.GetBytes($Text); $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
}
function Write-NewJson([string] $Path, $Value) {
    Write-NewText $Path (ConvertTo-Json -InputObject $Value -Depth 60)
}
function Read-Json([string] $Path) {
    Reject-Link $Path
    ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8))
}
function Read-SharedText([string] $Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $true)
    try { $reader.ReadToEnd() } finally { $reader.Dispose() }
}
function Relative([string] $Root, [string] $Path) {
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Path escapes its explicit root.' }
    $full.Substring($prefix.Length).Replace('\', '/')
}
function Snapshot([string] $Root) {
    # Walk without following junctions; check each directory before descending.
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Root)
    while ($pending.Count) {
        $folder = $pending.Pop(); Reject-Link $folder
        foreach ($file in [IO.Directory]::GetFiles($folder)) {
            [ordered]@{ Path = Relative $Root $file; Sha256 = Hash $file; Length = ([IO.FileInfo]$file).Length }
        }
        foreach ($child in [IO.Directory]::GetDirectories($folder)) { Reject-Link $child; $pending.Push($child) }
    }
}
function Assert-NoReceiverUnity {
    foreach ($item in @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'Unity.exe'")) {
        if (-not $item.CommandLine) { throw "Cannot verify project ownership of Unity PID $($item.ProcessId); no process was changed." }
        foreach ($match in [regex]::Matches($item.CommandLine, '(?i)(?:^|\s)-(?:projectPath|createProject)\s+(?:"([^"]+)"|(\S+))')) {
            $value = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
            if ([IO.Path]::GetFullPath($value).TrimEnd('\', '/') -ieq $project) {
                throw "Receiver already has Unity PID $($item.ProcessId); preserve it, never kill/restart or launch twice."
            }
        }
    }
}
function Assert-Identity($Value) {
    if ($Value.purpose -cne $purpose -or $Value.projectPath -cne $project -or $Value.reception -cne $reception -or
        $Value.repository -cne $repository -or $Value.sourceCommit -cne $sourceCommit -or $Value.cCommit -cne $cCommit) {
        throw 'MC26 receiver identity mismatch.'
    }
}
function Read-Gate([string] $Name) {
    $value = Read-Json (Join-Path $reception ($Name + '.json'))
    Assert-Identity $value
    if (-not $value.passed -or $value.action -cne $Name) { throw "Missing successful current-reception gate: $Name" }
    $original = Join-Path $reception ('Runs/' + $value.run + '/' + $Name + '/result.json')
    if ((Hash $original) -cne (Hash (Join-Path $reception ($Name + '.json')))) { throw 'Gate differs from its original immutable step result.' }
    $value
}
function Assert-Owner {
    $owner = Read-Json (Join-Path $reception 'owner.json'); Assert-Identity $owner
    if ($owner.deliveryRoot -cne $delivery -or $owner.unityVersion -cne '2022.3.62f3c1') { throw 'Wrong external reception owner.' }
    if ((Hash (Join-Path $project 'domain-reception-owner.json')) -cne (Hash (Join-Path $reception 'owner.json'))) { throw 'Receiver owner binding differs.' }
    $version = [IO.File]::ReadAllText((Join-Path $project 'ProjectSettings/ProjectVersion.txt'))
    if ($version -notmatch '(?m)^m_EditorVersion: 2022\.3\.62f3c1\r?$') { throw 'Receiver Unity version mismatch.' }
}
function Assert-MinimalPackages([switch] $Resolved) {
    $manifest = Read-Json (Join-Path $project 'Packages/manifest.json')
    $items = @($manifest.dependencies.PSObject.Properties)
    foreach ($item in $items) {
        if ($item.Name.StartsWith('com.unity.modules.', [StringComparison]::Ordinal)) {
            if ($item.Value -cne '1.0.0') { throw 'Unexpected built-in module version.' }
        } elseif (($item.Name -ceq 'com.unity.ugui' -and $item.Value -ceq '1.0.0') -or
            ($item.Name -ceq 'com.unity.nuget.newtonsoft-json' -and $item.Value -ceq '3.2.1')) { continue }
        else { throw "Not a minimal independent dependency: $($item.Name)" }
    }
    if ($manifest.dependencies.'com.unity.ugui' -cne '1.0.0' -or $manifest.dependencies.'com.unity.nuget.newtonsoft-json' -cne '3.2.1') { throw 'Required minimal dependencies missing.' }
    if (@($manifest.PSObject.Properties | Where-Object Name -NE 'dependencies').Count) { throw 'Receiver manifest contains extra configuration.' }
    if ($Resolved) {
        $lock = Read-Json (Join-Path $project 'Packages/packages-lock.json')
        foreach ($entry in $lock.dependencies.PSObject.Properties) {
            if (-not $entry.Name.StartsWith('com.unity.modules.', [StringComparison]::Ordinal) -and
                $entry.Name -cne 'com.unity.ugui' -and $entry.Name -cne 'com.unity.nuget.newtonsoft-json') { throw "Unexpected restored package: $($entry.Name)" }
            if ($entry.Value.source -notin @('registry', 'builtin')) { throw 'Local/Git/embedded packages are not allowed.' }
        }
        if ($lock.dependencies.'com.unity.ugui'.version -cne '1.0.0' -or $lock.dependencies.'com.unity.nuget.newtonsoft-json'.version -cne '3.2.1') { throw 'UPM did not restore the pinned versions.' }
    }
}
function Assert-FormalFiles($Audit) {
    if ((Hash $package) -cne $Audit.data.packageSha256 -or (Hash $Audit.data.filesPath) -cne $Audit.data.filesSha256) { throw 'Package/audit bytes changed.' }
    $rows = Read-Json $Audit.data.filesPath
    foreach ($row in $rows) {
        foreach ($base in @($repository, $project)) {
            $path = Join-Path $base $row.Path
            # Unity may omit an empty, unused folder. Never waive a file or nonempty folder.
            if ($row.Folder -and -not (Test-Path -LiteralPath ($path + '.meta')) -and $base -ceq $project) {
                $descendants = @($Audit.data.paths | Where-Object { $_.StartsWith($row.Path + '/', [StringComparison]::Ordinal) })
                if ($descendants.Count -eq 0) { continue }
            }
            if ((Hash ($path + '.meta')) -cne $row.MetaSha256 -or (-not $row.Folder -and (Hash $path) -cne $row.Sha256)) { throw "Formal source/import bytes changed: $path" }
        }
    }
}
function Invoke-OwnedUnity([string[]] $Arguments, [switch] $TestRun) {
    Assert-NoReceiverUnity
    $log = Join-Path $step 'Editor.log'
    if (Test-Path -LiteralPath $log) { throw 'Never overwrite an Editor log.' }
    $all = @('-batchmode', '-logFile', $log) + $(if ($TestRun) { @() } else { @('-quit') }) + $Arguments
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $unity; $start.UseShellExecute = $false
    $start.WorkingDirectory = $delivery
    # These are trusted absolute paths/validated tokens, not a shell command.
    foreach ($argument in $all) { if ($argument.Contains('"') -or $argument.EndsWith('\')) { throw 'Unsafe native argument.' } }
    $start.Arguments = (@($all | ForEach-Object { '"' + $_ + '"' }) -join ' ')
    Write-NewJson (Join-Path $step 'invocation.json') ([ordered]@{ executable = $unity; arguments = $all; projectPath = $project; protectedMainPid = 16464; utc = [DateTime]::UtcNow.ToString('o') })
    $child = [Diagnostics.Process]::Start($start)
    try {
        $null = $child.Handle
        Write-NewJson (Join-Path $step 'process.json') @{ pid = $child.Id; projectPath = $project; executable = $unity; startedUtc = [DateTime]::UtcNow.ToString('o') }
        if (-not $child.WaitForExit($TimeoutSeconds * 1000)) {
            Write-NewJson (Join-Path $step 'timeout.json') @{ pid = $child.Id; projectPath = $project; log = $log; timeoutSeconds = $TimeoutSeconds; utc = [DateTime]::UtcNow.ToString('o'); killed = $false }
            throw "Unity timeout. PID $($child.Id) and log retained at $log; no kill/restart was attempted."
        }
        $child.WaitForExit()
        $code = $child.ExitCode
        if ($null -eq $code) { throw 'Process returned no OS exit code; retain the original PID/log instead of claiming success.' }
        Write-NewJson (Join-Path $step 'os-exit.json') @{ pid = $child.Id; projectPath = $project; exitCode = $code; endedUtc = [DateTime]::UtcNow.ToString('o') }
        $logSha = if (Test-Path -LiteralPath $log -PathType Leaf) { Hash $log } else { $null }
        Write-NewJson (Join-Path $step 'exit.json') @{ pid = $child.Id; projectPath = $project; exitCode = $code; logSha256 = $logSha; endedUtc = [DateTime]::UtcNow.ToString('o') }
        if ($code -ne 0) { throw "Unity OS exit $code; preserve $log" }
        if (-not $logSha) { throw 'Unity exited without its required Editor log.' }
        $text = Read-SharedText $log
        $errors = @([regex]::Matches($text, '(?im)^.*(?:error (?:CS|BC)\d+|Scripts have compiler errors|Compilation failed|Aborting batchmode due to failure|Multiple precompiled assemblies|executeMethod.*(?:failed|could not)|Fatal Error|Crash!!!).*$') | ForEach-Object Value)
        if ($errors.Count) { Write-NewJson (Join-Path $step 'log-errors.json') $errors; throw 'Unity log contains compilation/execution errors despite OS exit 0.' }
        @{ pid = $child.Id; exitCode = $code; log = $log; logSha256 = Hash $log }
    } finally { $child.Dispose() }
}
function Audit-FormalPackage {
    $exporter = Join-Path $repository 'Assets/YLFramework/Editor/Distribution/ExportUnityPackage.cs'
    $block = [regex]::Match([IO.File]::ReadAllText($exporter), '(?s)ExportRoots\s*=\s*\{(.*?)\};')
    $roots = @([regex]::Matches($block.Groups[1].Value, '"(Assets/[^"\r\n]+)"') | ForEach-Object { $_.Groups[1].Value })
    if ($roots.Count -ne 7) { throw 'Expected the current formal exporter seven roots.' }
    $expected = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($root in $roots) {
        $absolute = Join-Path $repository $root; Reject-Link $absolute
        if (Test-Path -LiteralPath $absolute -PathType Container) {
            $entries = @(@{ Path = $root + '.meta' }) + @(Snapshot $absolute | ForEach-Object { @{ Path = $root + '/' + $_.Path } })
        } else { $entries = @(@{ Path = $root + '.meta' }) }
        foreach ($item in $entries) {
            $name = $item.Path
            if (-not $name.EndsWith('.meta', [StringComparison]::Ordinal)) {
                if (-not (Test-Path -LiteralPath (Join-Path $repository ($name + '.meta')) -PathType Leaf)) { throw "Formal source lacks meta: $name" }
                continue
            }
            $relative = $name.Substring(0, $name.Length - 5)
            $source = Join-Path $repository $relative
            $expected.Add($relative, @{ MetaSha256 = Hash ($source + '.meta'); Folder = [IO.Directory]::Exists($source) })
        }
    }
    $packageSha = Hash $package
    $tar = (Get-Command -Name tar.exe -CommandType Application -ErrorAction Stop).Source
    $names = @(& $tar -tf $package)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot list formal package.' }
    $details = @(& $tar -tvf $package)
    if ($LASTEXITCODE -ne 0 -or $details.Count -ne $names.Count) { throw 'Cannot verify tar entry types.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $guids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    for ($index = 0; $index -lt $names.Count; $index++) {
        $name = $names[$index] -replace '^\./', ''
        if ($name -eq '' -or $name -eq '.') {
            if ($details[$index][0] -cne 'd') { throw 'Unexpected tar root entry.' }
            continue
        }
        if (-not $seen.Add($name) -or $details[$index][0] -cnotin @('-', 'd')) { throw "Duplicate/link/special tar entry: $name" }
        if ($details[$index][0] -ceq 'd') {
            if ($name -notmatch '^[0-9a-f]{32}/?$') { throw "Unsafe tar directory: $name" }
        } elseif ($name -cmatch '^([0-9a-f]{32})/(asset|asset\.meta|pathname|preview\.png)$') { $null = $guids.Add($Matches[1]) }
        else { throw "Unsafe tar member: $name" }
    }
    $unpacked = Join-Path $step 'Package'; New-OwnedDirectory $unpacked
    & $tar -xf $package -C $unpacked
    if ($LASTEXITCODE -ne 0 -or (Hash $package) -cne $packageSha) { throw 'Extraction failed or package changed.' }
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($guid in $guids) {
        $folder = Join-Path $unpacked $guid; Reject-Link $folder
        $path = [IO.File]::ReadAllText((Join-Path $folder 'pathname'), [Text.Encoding]::UTF8).TrimEnd([char]0, [char]13, [char]10)
        if (-not $expected.ContainsKey($path) -or $path -match '(?i)(?:^|/)(?:Tests?|Integrations|Packages|StreamingAssets|\.git|\.env|\.mcp)(?:/|\.|$)|MiniExcel|Editor Enhancers|AcceptanceBuild|(?:secret|credential|private[-_]?key)|\.(?:pfx|p12|pem|key|jks|keystore)$|/(?:bcrypt|ncrypt|crypt32|Newtonsoft\.Json)\.dll$' -or
            ($path -match '(?i)\.(dll|so|bundle)$' -and $path -cnotin @('Assets/YLFramework/Plugins/BouncyCastle/BouncyCastle.Cryptography.dll','Assets/YLFramework/Runtime/Core/Analyzers/YLFramework.Analyzers.dll'))) { throw "Excluded/unexpected package path: $path" }
        $metaPath = Join-Path $folder 'asset.meta'
        $meta = [IO.File]::ReadAllText($metaPath)
        if ($meta -notmatch '(?m)^guid:\s*([0-9a-f]{32})\s*\r?$' -or $Matches[1] -cne $guid) { throw "Archive GUID mismatch: $path" }
        $source = Join-Path $repository $path; $info = $expected[$path]
        if ((Hash $metaPath) -cne $info.MetaSha256) { throw "Archive meta differs from source: $path" }
        $payload = Join-Path $folder 'asset'; $sha = $null; $length = 0
        if (-not $info.Folder) {
            $sha = Hash $payload; $length = ([IO.FileInfo]$payload).Length
            if ($sha -cne (Hash $source)) { throw "Archive payload differs from current source: $path" }
        } elseif ((Test-Path -LiteralPath $payload) -and ([IO.FileInfo]$payload).Length -ne 0) { throw 'Folder has an unexpected payload.' }
        $rows.Add([ordered]@{ Path = $path; Guid = $guid; Folder = $info.Folder; Length = $length; Sha256 = $sha; MetaSha256 = $info.MetaSha256 })
        $null = $expected.Remove($path)
    }
    if ($expected.Count -or $rows.Count -eq 0) { throw 'Formal seven-root closure is incomplete.' }
    foreach ($license in @('Assets/YLFramework/LICENSE.md', 'Assets/YLFramework/Runtime/THIRD-PARTY-NOTICES.md', 'Assets/YLFramework/Plugins/BouncyCastle/LICENSE.md')) {
        if (@($rows | Where-Object Path -CEQ $license).Count -ne 1) { throw "Applicable license missing: $license" }
    }
    $filesPath = Join-Path $step 'package-files.json'
    Write-NewJson $filesPath @($rows | Sort-Object Path)
    @{ packageSha256 = $packageSha; packageBytes = ([IO.FileInfo]$package).Length; roots = $roots; records = $rows.Count; filesPath = $filesPath; filesSha256 = Hash $filesPath; exporterSha256 = Hash $exporter; paths = @($rows | ForEach-Object { $_.Path }) }
}

if ($repository -ine [IO.Path]::GetFullPath('G:/Unity/Projects/CGJ2024')) { throw 'Run only from the approved main workspace Tools/Domain.' }
foreach ($path in @($repository, $delivery, $reception, $project)) { Reject-Link $path }
if (-not (Test-Path -LiteralPath $delivery -PathType Container)) { throw 'Existing MC26 evidence parent required.' }
$head = & git -C $repository rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $head -cne $sourceCommit) { throw 'Main sourceCommit changed; do not silently rebind.' }
$fixedC = & git -C $repository rev-parse $cCommit
if ($LASTEXITCODE -ne 0 -or $fixedC -cne $cCommit) { throw 'Fixed C commit is unavailable.' }
$trees = @(& git -C $repository worktree list --porcelain)
if ($LASTEXITCODE -ne 0) { throw 'Cannot verify worktree separation.' }
foreach ($line in $trees) {
    if (-not $line.StartsWith('worktree ')) { continue }
    $tree = [IO.Path]::GetFullPath($line.Substring(9)).TrimEnd('\', '/')
    if ($delivery -ieq $tree -or $delivery.StartsWith($tree + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Reception must be outside all source worktrees.' }
}
$unity = 'G:/Unity/2022.3.62f3c1/Editor/Unity.exe'
if (-not (Test-Path -LiteralPath $unity -PathType Leaf)) {
    # Read-only preflight of PID 16464 confirmed this existing installation; never control that PID.
    $unity = 'C:/Program Files/Unity/Hub/Editor/2022.3.62f3c1/Editor/Unity.exe'
}
if (-not (Test-Path -LiteralPath $unity -PathType Leaf)) { throw 'The verified Unity installation is unavailable; never install/upgrade.' }
$unity = [IO.Path]::GetFullPath($unity); Reject-Link $unity
$mutex = [Threading.Mutex]::new($false, 'Local\MC26-C-Reception')
$locked = $false; $step = $null
try {
    $locked = $mutex.WaitOne(0)
    if (-not $locked) { throw 'Another MC26 reception action is active; never overlap actions.' }
    Assert-NoReceiverUnity
    if ($Action -eq 'Create') {
        if ((Test-Path -LiteralPath $project) -or (Test-Path -LiteralPath $reception)) { throw 'Create requires a completely new Receiver and Reception; no adoption or cleanup.' }
        New-OwnedDirectory $reception
        Write-NewJson (Join-Path $reception 'owner.json') ([ordered]@{
            purpose = $purpose; deliveryRoot = $delivery; reception = $reception; projectPath = $project; repository = $repository
            sourceCommit = $sourceCommit; cCommit = $cCommit; unityVersion = '2022.3.62f3c1'; unityExecutable = $unity
            protectedMainPid = 16464; createdUtc = [DateTime]::UtcNow.ToString('o')
        })
        New-OwnedDirectory (Join-Path $reception 'Runs')
    } elseif ($Action -eq 'CompleteCreate') {
        $owner = Read-Json (Join-Path $reception 'owner.json'); Assert-Identity $owner
        if ($owner.unityExecutable -cne $unity -or (Test-Path -LiteralPath (Join-Path $reception 'Create.json')) -or
            (Test-Path -LiteralPath (Join-Path $project 'domain-reception-owner.json')) -or
            @(Snapshot (Join-Path $project 'Assets')).Count -ne 0) { throw 'CompleteCreate only recovers this owned empty project before any normalization/import.' }
        $failedPath = Join-Path $reception 'Runs/mc26-receiver-create-01/Create/result.json'
        $failed = Read-Json $failedPath
        if ($failed.passed -or $failed.action -cne 'Create' -or $failed.projectPath -cne $project -or $failed.error -notmatch 'Editor[.]log') { throw 'Missing attributable raw creation wrapper failure.' }
    } else {
        Assert-Owner
        $owner = Read-Json (Join-Path $reception 'owner.json')
        if ($owner.unityExecutable -cne $unity) { throw 'Do not rebind the receiver to another Editor executable.' }
    }
    if ($Action -in @('Import', 'AuditPackage', 'InstallConsumer') -and (Test-Path -LiteralPath (Join-Path $reception ($Action + '.json')))) {
        throw 'This immutable phase already completed; do not repeat or overwrite its evidence.'
    }
    $runRoot = Join-Path $reception ('Runs/' + $Run)
    if (-not (Test-Path -LiteralPath $runRoot)) { New-OwnedDirectory $runRoot }
    $stepAction = if ($Action -eq 'CompleteCreate') { 'Create' } elseif ($Action -eq 'CompleteImport') { 'Import' } else { $Action }
    if ($Action -eq 'CompleteImport' -and (Test-Path -LiteralPath (Join-Path $reception 'Import.json'))) { throw 'First-import evidence already closed.' }
    $candidate = Join-Path $runRoot $stepAction; New-OwnedDirectory $candidate
    $step = $candidate
    Write-NewJson (Join-Path $step 'request.json') ([ordered]@{
        purpose = $purpose; reception = $reception; projectPath = $project; repository = $repository; sourceCommit = $sourceCommit; cCommit = $cCommit
        action = $Action; run = $Run; backend = $Backend; stepPath = $step; packagePath = $package; ownerSha256 = Hash (Join-Path $reception 'owner.json')
        scriptSha256 = Hash $PSCommandPath; bootstrapSha256 = Hash (Join-Path $PSScriptRoot 'DomainReceptionBootstrap.cs.txt'); utc = [DateTime]::UtcNow.ToString('o')
    })
    $data = $null
    switch ($Action) {
        { $_ -in @('Create', 'CompleteCreate') } {
            if ($Action -eq 'Create') { $process = Invoke-OwnedUnity @('-createProject', $project) }
            else {
                # Reuse the recorded, already-closed empty-project process, not a guessed exit or a new launch.
                $priorStep = Join-Path $reception 'Runs/mc26-receiver-create-02/Create'
                $os = Read-Json (Join-Path $priorStep 'os-exit.json')
                $closed = Read-Json (Join-Path $priorStep 'exit.json')
                $priorRequest = Read-Json (Join-Path $priorStep 'request.json'); Assert-Identity $priorRequest
                $priorLog = Join-Path $priorStep 'Editor.log'
                $text = Read-SharedText $priorLog
                if ($os.exitCode -ne 0 -or $closed.exitCode -ne 0 -or $os.pid -ne $closed.pid -or $closed.projectPath -cne $project -or
                    (Hash $priorLog) -cne $closed.logSha256 -or $text -notmatch 'Exiting batchmode successfully now!' -or
                    $text -match '(?im)error CS\d+|Scripts have compiler errors|Compilation failed|Crash!!!') { throw 'Prior empty-project process lacks a bound OS0/unchanged complete log.' }
                $process = @{ pid = $os.pid; exitCode = $os.exitCode; log = $priorLog; logSha256 = Hash $priorLog; originalOsReceipt = Join-Path $priorStep 'os-exit.json'; originalOsReceiptSha256 = Hash (Join-Path $priorStep 'os-exit.json'); recoveredPostExit = $true }
                Write-NewJson (Join-Path $step 'recovered-process.json') $process
            }
            $manifestPath = Join-Path $project 'Packages/manifest.json'
            $original = Read-Json $manifestPath
            Write-NewJson (Join-Path $step 'created-manifest.json') $original
            $dependencies = [ordered]@{}
            foreach ($entry in $original.dependencies.PSObject.Properties) {
                if ($entry.Name.StartsWith('com.unity.modules.', [StringComparison]::Ordinal)) { $dependencies[$entry.Name] = $entry.Value }
            }
            $dependencies['com.unity.ugui'] = '1.0.0'; $dependencies['com.unity.nuget.newtonsoft-json'] = '3.2.1'
            [IO.File]::WriteAllText($manifestPath, (ConvertTo-Json -InputObject @{ dependencies = $dependencies } -Depth 10), $utf8)
            [IO.File]::Copy((Join-Path $reception 'owner.json'), (Join-Path $project 'domain-reception-owner.json'), $false)
            Assert-Owner; Assert-MinimalPackages
            $data = @{ process = $process; manifestSha256 = Hash $manifestPath; initialAssets = @(Snapshot (Join-Path $project 'Assets')); noSourceLibraryCopied = $true; recoveredRawCreateWrapper = ($Action -eq 'CompleteCreate') }
        }
        { $_ -in @('Import', 'CompleteImport') } {
            $created = Read-Gate 'Create'; $audited = Read-Gate 'AuditPackage'; Assert-MinimalPackages
            if ((Hash (Join-Path $project 'Packages/manifest.json')) -cne $created.data.manifestSha256) { throw 'Minimal first-import manifest changed.' }
            $packageSha = Hash $package
            if ($packageSha -cne $audited.data.packageSha256 -or (Hash $audited.data.filesPath) -cne $audited.data.filesSha256) { throw 'Audit the exact seven-root package before importing any of its code.' }
            if ($Action -eq 'Import') {
            $before = @(Snapshot (Join-Path $project 'Assets'))
            if (@($before | Where-Object { $_.Path -match '(?i)(?:^|/)(?:Tests?|Integrations|DomainAcceptanceHarness)(?:/|$)|\.(?:cs|dll|asmdef|asmref)$' }).Count) { throw 'Raw import cannot use any source helper, test or backend in Assets.' }
            $baseline = @($created.data.initialAssets)
            if ($before.Count -ne $baseline.Count) { throw 'First import requires the untouched createProject Assets snapshot, without any helper/consumer/tests.' }
            foreach ($row in $before) {
                $prior = @($baseline | Where-Object Path -CEQ $row.Path)
                if ($prior.Count -ne 1 -or $prior[0].Sha256 -cne $row.Sha256) { throw 'First-import Assets were modified.' }
            }
            foreach ($name in $requiredAssemblies) {
                if (Test-Path -LiteralPath (Join-Path $project ('Library/ScriptAssemblies/' + $name + '.dll'))) { throw 'Product assemblies exist before the real first import.' }
            }
            Write-NewJson (Join-Path $reception 'import-attempt.json') @{ run = $Run; stepPath = $step; packageSha256 = $packageSha; utc = [DateTime]::UtcNow.ToString('o') }
            $process = Invoke-OwnedUnity @('-projectPath', $project, '-buildTarget', 'Win64', '-importPackage', $package)
            } else {
                $attempt = Read-Json (Join-Path $reception 'import-attempt.json')
                $failed = Read-Json (Join-Path $attempt.stepPath 'result.json')
                $priorRequest = Read-Json (Join-Path $attempt.stepPath 'request.json'); Assert-Identity $priorRequest
                $closed = Read-Json (Join-Path $attempt.stepPath 'exit.json')
                $os = Read-Json (Join-Path $attempt.stepPath 'os-exit.json')
                $priorLog = Join-Path $attempt.stepPath 'Editor.log'
                $text = Read-SharedText $priorLog
                if ($failed.passed -or $failed.error -notmatch 'Editor[.]log' -or $priorRequest.action -cne 'Import' -or
                    $attempt.packageSha256 -cne $packageSha -or $closed.projectPath -cne $project -or
                    $closed.pid -ne $os.pid -or $closed.exitCode -ne 0 -or $os.exitCode -ne 0 -or
                    (Hash $priorLog) -cne $closed.logSha256 -or $text -notmatch 'Exiting batchmode successfully now!' -or
                    $text -match '(?im)error CS\d+|Scripts have compiler errors|Compilation failed|Crash!!!') { throw 'Cannot recover a missing/failed or changed real first-import process.' }
                Assert-FormalFiles $audited
                $process = @{ pid = $os.pid; exitCode = 0; log = $priorLog; logSha256 = $closed.logSha256;
                    originalOsReceipt = Join-Path $attempt.stepPath 'os-exit.json'; originalOsReceiptSha256 = Hash (Join-Path $attempt.stepPath 'os-exit.json'); recoveredPostExit = $true }
                Write-NewJson (Join-Path $step 'recovered-process.json') $process
            }
            Assert-MinimalPackages -Resolved
            if ((Hash $package) -cne $packageSha) { throw 'Package changed during the actual first import.' }
            $compilation = @([regex]::Matches((Read-SharedText $process.log), '(?im)^.*(?:Compilation succeeded|Reloading assemblies|Begin MonoManager ReloadAssembly|Completed reload).*$') | ForEach-Object Value)
            if ($compilation.Count -eq 0) { throw 'First-import log lacks actual script compilation/domain reload evidence.' }
            $dlls = @(foreach ($name in $requiredAssemblies) {
                $path = Join-Path $project ('Library/ScriptAssemblies/' + $name + '.dll')
                @{ Name = $name; Path = $path; Sha256 = Hash $path; Length = ([IO.FileInfo]$path).Length }
            })
            if (Test-Path -LiteralPath (Join-Path $project 'Assets/DomainAcceptanceHarness')) { throw 'Consumer present during raw import.' }
            $data = @{ process = $process; packageSha256 = $packageSha; helperPresent = $false; consumerPresent = $false; assemblies = $dlls; compilationLogEvidence = $compilation; manifest = Read-Json (Join-Path $project 'Packages/manifest.json'); packageLock = Read-Json (Join-Path $project 'Packages/packages-lock.json') }
        }
        'AuditPackage' {
            $null = Read-Gate 'Create'
            $data = Audit-FormalPackage
            $importPath = Join-Path $reception 'Import.json'
            if (Test-Path -LiteralPath $importPath) {
                $imported = Read-Gate 'Import'
                if ($imported.data.packageSha256 -cne $data.packageSha256) { throw 'Audited package differs from the actual raw import.' }
            }
        }
        'InstallConsumer' {
            $imported = Read-Gate 'Import'; $audit = Read-Gate 'AuditPackage'
            if ($imported.data.packageSha256 -cne $audit.data.packageSha256) { throw 'Import/package audit identity differs.' }
            Assert-FormalFiles $audit; Assert-MinimalPackages -Resolved
            foreach ($dll in $imported.data.assemblies) { if ((Hash $dll.Path) -cne $dll.Sha256) { throw 'First-import assembly evidence changed before consumer installation.' } }
            $source = Join-Path $repository 'Assets/YLFramework/Tests/Fixtures/Extensions/Domain'
            $destination = Join-Path $project 'Assets/DomainAcceptanceHarness'
            if (Test-Path -LiteralPath $destination) { throw 'Never overwrite/adopt a consumer installation.' }
            $mapping = @(
                @{ Source = 'DomainAcceptanceProbe.cs'; Target = 'DomainAcceptanceProbe.cs' },
                @{ Source = 'link.xml'; Target = 'link.xml' },
                @{ Source = 'Editor/DomainAcceptanceAssets.cs'; Target = 'Editor/DomainAcceptanceAssets.cs' }
            )
            $seenGuid = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($meta in @(Snapshot (Join-Path $project 'Assets') | Where-Object { $_.Path.EndsWith('.meta') })) {
                $text = [IO.File]::ReadAllText((Join-Path $project ('Assets/' + $meta.Path)))
                if ($text -match '(?m)^guid:\s*([0-9a-f]{32})\s*\r?$') { if (-not $seenGuid.Add($Matches[1])) { throw 'Receiver has duplicate GUIDs.' } }
            }
            foreach ($item in $mapping) {
                $path = Join-Path $source $item.Source; $null = Hash $path
                $metaText = [IO.File]::ReadAllText(($path + '.meta'))
                if ($metaText -notmatch '(?m)^guid:\s*([0-9a-f]{32})\s*\r?$' -or -not $seenGuid.Add($Matches[1])) { throw 'Consumer needs normally generated, noncolliding source meta GUIDs before installation.' }
            }
            if ([IO.File]::ReadAllText((Join-Path $source 'DomainAcceptanceProbe.cs')) -match 'RuntimeInitializeOnLoadMethod') { throw 'Consumer must not have an automatic RuntimeInitialize entry.' }
            [xml]$link = [IO.File]::ReadAllText((Join-Path $source 'link.xml'))
            $assemblies = @($link.linker.assembly)
            $types = @($assemblies | ForEach-Object { $_.type } | ForEach-Object { $_.fullname })
            $expectedTypes = @('YLFramework.Tests.DomainAcceptance.ReceiverSkill', 'YLFramework.Tests.DomainAcceptance.ReceiverBuff', 'YLFramework.Tests.DomainAcceptance.ReceiverMission')
            if ($assemblies.Count -ne 1 -or $assemblies[0].fullname -cne 'Assembly-CSharp' -or $types.Count -ne 3 -or @(Compare-Object $types $expectedTypes -CaseSensitive).Count) { throw 'link.xml must preserve only the three contracted Assembly-CSharp reflection types.' }
            if (@($assemblies[0].type | Where-Object preserve -CNE 'all').Count) { throw 'Each of the three reflected consumer types must preserve all members.' }
            New-OwnedDirectory $destination; New-OwnedDirectory (Join-Path $destination 'Editor')
            $copied = @(foreach ($item in $mapping) {
                foreach ($suffix in @('', '.meta')) {
                    $from = Join-Path $source ($item.Source + $suffix); $to = Join-Path $destination ($item.Target + $suffix)
                    $sha = Hash $from; [IO.File]::Copy($from, $to, $false)
                    if ((Hash $from) -cne $sha -or (Hash $to) -cne $sha) { throw 'Unstable consumer input copy.' }
                    @{ Source = $from; Path = $to; Sha256 = $sha }
                }
            })
            $bootstrap = Join-Path $PSScriptRoot 'DomainReceptionBootstrap.cs.txt'
            $target = Join-Path $destination 'Editor/DomainReceptionBootstrap.cs'
            [IO.File]::Copy($bootstrap, $target, $false)
            $copied += @{ Source = $bootstrap; Path = $target; Sha256 = Hash $target }
            $data = @{ packageSha256 = Hash $package; files = $copied; consumerRoot = $destination; firstImportProvedWithoutHarness = $true }
        }
        'InstallExcel' {
            $null = Read-Gate 'Import'; $audit = Read-Gate 'AuditPackage'; Assert-FormalFiles $audit; Assert-MinimalPackages -Resolved
            foreach ($coreRun in @('mc26-receiver-mono-01', 'mc26-receiver-il2cpp-01')) {
                $core = Read-Json (Join-Path $delivery ('PlayerRuns/' + $coreRun + '-01/os-run.json'))
                if (-not $core.passed -or -not $core.lateLogChecked -or $core.exitCode -ne 0 -or $core.main) { throw 'Both independent core graphical Players must pass before optional facilities are installed.' }
            }
            if (Test-Path -LiteralPath (Join-Path $reception 'InstallExcel.json')) { throw 'Optional installation is immutable.' }
            $integration = Join-Path $project 'Assets/YLFramework/Integrations'
            New-OwnedDirectory $integration
            [IO.File]::Copy((Join-Path $repository 'Assets/YLFramework/Integrations.meta'), ($integration + '.meta'), $false)
            $copied = @(foreach ($module in @('Excel', 'DomainExcel')) {
                $source = Join-Path $repository ('Assets/YLFramework/Integrations/' + $module)
                $destination = Join-Path $integration $module
                New-OwnedDirectory $destination
                [IO.File]::Copy(($source + '.meta'), ($destination + '.meta'), $false)
                foreach ($file in @(Snapshot $source)) {
                    $from = Join-Path $source $file.Path; $target = Join-Path $destination $file.Path
                    $parent = [IO.Path]::GetDirectoryName($target)
                    if (-not (Test-Path -LiteralPath $parent)) { $null = [IO.Directory]::CreateDirectory($parent) }
                    [IO.File]::Copy($from, $target, $false)
                    if ((Hash $target) -cne $file.Sha256) { throw 'Optional input copy changed.' }
                    @{ Source = $from; Path = $target; Sha256 = $file.Sha256 }
                }
            })
            $helper = Join-Path $project 'Assets/DomainExcelAcceptance/Editor'
            New-OwnedDirectory (Join-Path $project 'Assets/DomainExcelAcceptance'); New-OwnedDirectory $helper
            $template = Join-Path $PSScriptRoot 'DomainReceptionExcel.cs.txt'
            $target = Join-Path $helper 'DomainReceptionExcel.cs'
            [IO.File]::Copy($template, $target, $false)
            $copied += @{ Source = $template; Path = $target; Sha256 = Hash $target }
            $manifestPath = Join-Path $project 'Packages/manifest.json'
            $manifest = Read-Json $manifestPath
            Write-NewJson (Join-Path $step 'minimal-manifest-before.json') $manifest
            $manifest.dependencies | Add-Member -NotePropertyName 'com.unity.test-framework' -NotePropertyValue '1.3.9'
            [IO.File]::WriteAllText($manifestPath, (ConvertTo-Json -InputObject $manifest -Depth 10), $utf8)
            $data = @{ files = $copied; manifestSha256 = Hash $manifestPath; coreBeforeOptional = $true; testFramework = '1.3.9'; excelBackendProductTip = '5dce4f3e39e2f4bd11602e65cabace5f56108cf8' }
        }
        { $_ -in @('EnableExcel', 'TestExcel', 'AuditExcel') } {
            $optional = Read-Gate 'InstallExcel'
            foreach ($file in $optional.data.files) { if ((Hash $file.Path) -cne $file.Sha256 -or (Hash $file.Source) -cne $file.Sha256) { throw 'Optional input changed after installation.' } }
            if ((Hash (Join-Path $project 'Packages/manifest.json')) -cne $optional.data.manifestSha256) { throw 'Optional package manifest changed.' }
            if ($Action -eq 'TestExcel') {
                $xmlPath = Join-Path $step 'results.xml'
                $process = Invoke-OwnedUnity @('-projectPath', $project, '-runTests', '-testPlatform', 'EditMode', '-testFilter', 'YLFramework.Domain.Excel.Tests', '-testResults', $xmlPath) -TestRun
                [xml]$xml = [IO.File]::ReadAllText($xmlPath)
                $cases = @($xml.SelectNodes('//test-case'))
                $passed = @($cases | Where-Object result -CEQ 'Passed').Count
                Write-NewJson (Join-Path $step 'test-summary.json') @{ discovered = [int]$xml.DocumentElement.testcasecount; executed = $cases.Count; passed = $passed;
                    failed = @($cases | Where-Object result -CEQ 'Failed').Count; skipped = @($cases | Where-Object result -CEQ 'Skipped').Count; process = $process }
                if ($cases.Count -ne 6 -or $passed -ne $cases.Count) { throw 'Actual independent optional six-case suite did not fully pass.' }
                $data = @{ process = $process; xml = $xmlPath; sha256 = Hash $xmlPath; executed = $cases.Count; passed = $passed }
            } else {
                $method = if ($Action -eq 'EnableExcel') { 'Enable' } else { 'Audit' }
                $process = Invoke-OwnedUnity @('-projectPath', $project, '-executeMethod', ('YLFramework.Tests.DomainReception.Editor.DomainReceptionExcel.' + $method), '-domainExcelStep', $step)
                $editorReceipt = Read-Json (Join-Path $step 'optional-editor.json')
                if (-not $editorReceipt.passed) { throw 'Actual optional Editor configuration/audit failed.' }
                $data = @{ process = $process; editor = $editorReceipt }
            }
        }
        { $_ -in @('Prepare', 'BuildMono', 'BuildIL2CPP') } {
            $installed = Read-Gate 'InstallConsumer'; $audit = Read-Gate 'AuditPackage'
            Assert-FormalFiles $audit; Assert-MinimalPackages -Resolved
            foreach ($file in $installed.data.files) {
                if ((Hash $file.Path) -cne $file.Sha256 -or (Hash $file.Source) -cne $file.Sha256) { throw 'Installed consumer/bootstrap input changed; never silently resync.' }
            }
            $entry = if ($Action -eq 'Prepare') { 'Prepare' } else { 'Build' }
            $process = Invoke-OwnedUnity @('-projectPath', $project, '-buildTarget', 'Win64', '-executeMethod', ('YLFramework.Tests.DomainReception.Editor.DomainReceptionBootstrap.' + $entry), '-mc26-step', $step)
            $proofName = if ($Action -eq 'Prepare') { 'prepared.json' } else { 'BuildReport.json' }
            $proof = Join-Path $step $proofName; $record = Read-Json $proof
            if ($record.Project -cne $project -or $record.Pid -ne $process.pid -or $record.Run -cne $Run -or $record.Backend -cne $Backend) { throw 'Bootstrap result does not belong to this actual process/run/backend.' }
            if ($Action -eq 'Prepare') {
                if (-not $record.Prepared) { throw 'Dedicated scene preparation failed.' }
            } elseif ($record.Result -cne 'Succeeded' -or $record.Errors -ne 0 -or $record.Options -cne 'None' -or $record.Development -or -not $record.AuditPassed) { throw 'BuildReport did not pass the real non-Development build and dependency audit.' }
            $data = @{ process = $process; proofPath = $proof; proofSha256 = Hash $proof; backend = $Backend }
        }
    }
    $result = [ordered]@{
        passed = $true; purpose = $purpose; reception = $reception; projectPath = $project; repository = $repository
        sourceCommit = $sourceCommit; cCommit = $cCommit; action = $stepAction; run = $Run; data = $data; utc = [DateTime]::UtcNow.ToString('o')
    }
    Write-NewJson (Join-Path $step 'result.json') $result
    if ($stepAction -in @('Create', 'Import', 'AuditPackage', 'InstallConsumer', 'InstallExcel')) {
        [IO.File]::Copy((Join-Path $step 'result.json'), (Join-Path $reception ($stepAction + '.json')), $false)
    }
    "Reception step completed: $Action; evidence=$step"
} catch {
    if ($step -and (Test-Path -LiteralPath $step -PathType Container) -and -not (Test-Path -LiteralPath (Join-Path $step 'result.json'))) {
        Write-NewJson (Join-Path $step 'result.json') @{ passed = $false; purpose = $purpose; projectPath = $project; action = $Action; run = $Run; error = $_.Exception.Message; utc = [DateTime]::UtcNow.ToString('o'); noAutomaticKillOrRestart = $true }
    }
    throw
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
