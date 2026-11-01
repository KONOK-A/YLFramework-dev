#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $WorkspaceRoot,
    [Parameter(Mandatory)][string] $Unity,
    [ValidateRange(1, 86400)][int] $TimeoutSeconds = 900,
    [switch] $RunPlayer,
    [ValidateRange(1, 600)][int] $PlayerTimeoutSeconds = 60,
    [ValidateSet('Mono', 'IL2CPP')][string] $Backend = 'Mono',
    [switch] $HybridCLR,
    [string] $NativeSource,
    [string] $NativeSourceManifest,
    [ValidateNotNullOrEmpty()][string] $ImportUnity = $Unity
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Common/Process.ps1')
. (Join-Path $PSScriptRoot '../Common/Results.ps1')
. (Join-Path $PSScriptRoot '../UnityHost.ps1')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$configuration = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../TestSuites.json')) | ConvertFrom-Json -DateKind String
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($Unity)
$editor = Get-YLTestEditorProfile -Editors $configuration.editors -VersionInfo $version
$importVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($ImportUnity)
$importEditor = Get-YLTestEditorProfile -Editors $configuration.editors -VersionInfo $importVersion
if ($HybridCLR -and ($Backend -cne 'IL2CPP' -or -not $RunPlayer -or -not $NativeSource)) {
    throw 'HybridCLR acceptance requires explicit IL2CPP, RunPlayer and an existing native source.'
}
if ($HybridCLR -and $version.ProductVersion -cne $importVersion.ProductVersion) {
    throw 'Cross-version HybridCLR distribution acceptance is not supported; use the same exact Editor build for export and import.'
}
$workspace = Initialize-YLTestWorkspace -WorkspaceRoot $WorkspaceRoot -Repository $repository
$key = "Tooling-Distribution-$($version.ProductVersion)"
if ($version.ProductVersion -cne $importVersion.ProductVersion) {
    $pairHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("$($version.ProductVersion)|$($importVersion.ProductVersion)"))).Substring(0, 8)
    $key = "Tooling-Distribution-$($editor.version)-To-$($importEditor.version)-$pairHash"
}
if ($Backend -ceq 'IL2CPP') { $key += '-IL2CPP' }
if ($HybridCLR) { $key += '-HybridCLR' }
if ($RunPlayer) { $key += '-Smoke' }
$run = New-YLTestDirectory -Workspace $workspace -Kind Result -Key $key
$lease = Lock-YLTestDirectory -Workspace $workspace -Path $run.path
$summary = [ordered]@{ schemaVersion = 1; ownerId = $run.id; action = 'Run'; suite = 'Tooling'; module = 'Distribution'; profile = $version.ProductVersion; status = 'Failed'; reason = $null; endUtc = $null; processes = @(); checks = 0; player = 'NotRun' }
$summary['playerRun'] = @{ status = 'NotRun' }
$summary['playerRuns'] = @()
$summary['backend'] = $Backend
$summary['sourceUnity'] = @{ executable = $Unity; version = $editor.version; productVersion = $version.ProductVersion }
$summary['targetUnity'] = @{ executable = $ImportUnity; version = $importEditor.version; productVersion = $importVersion.ProductVersion }
$hostLeases = [Collections.Generic.List[IDisposable]]::new()
$nativeVariant = $null
$baselineRoot = $null
$runtimeCache = $null
$cacheOwnerId = $null
$nativePlayerRoot = $null
$playerOwnerId = $null

function Invoke-DistributionProcess([object] $HostRecord, [string] $Phase, [object[]] $Guids = @(), [string] $Package = '') {
    Assert-YLUnityHostIdle $HostRecord
    $phaseEditor = if ($Phase -ceq 'Export') { $editor } else { $importEditor }
    $phaseUnity = if ($Phase -ceq 'Export') { $Unity } else { $ImportUnity }
    if ($Phase -ceq 'Import' -and (Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash -cne $summary.package.sha256) {
        throw 'The actual exported package changed before import.'
    }
    $output = Join-Path $run.path $Phase
    $null = [IO.Directory]::CreateDirectory($output)
    $request = Join-Path $output 'request.json'
    Write-YLTestJson -Path $request -Value @{ schemaVersion = 1; hostId = $HostRecord.id; resultId = $run.id; resultRoot = $run.path; unityVersion = $phaseEditor.version; phase = $Phase; output = $output; guids = @($Guids); smoke = [bool]$RunPlayer; backend = $Backend
        hybridCLR = [bool]$HybridCLR; nativeSource = $NativeSource; nativeVariant = $nativeVariant; baseline = $baselineRoot; runtimeCache = $runtimeCache; cacheOwnerId = $cacheOwnerId; playerRoot = $nativePlayerRoot; playerOwnerId = $playerOwnerId }
    $start = [Diagnostics.ProcessStartInfo]::new($phaseUnity)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $HostRecord.path
    $arguments = @('-batchmode', '-projectPath', $HostRecord.path, '-logFile', (Join-Path $output 'unity.log'))
    if (-not $Phase.StartsWith('Hybrid', [StringComparison]::Ordinal)) { $arguments += '-nographics' }
    if ($Phase -ceq 'Import') { $arguments += @('-importPackage', $Package, '-quit') }
    else { $arguments += @('-executeMethod', 'YLFramework.Testing.DistributionEntry.Execute', '-ylDistributionRequest', $request) }
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $fact = Invoke-YLTestProcess -StartInfo $start -Log (Join-Path $output 'process.log') -TimeoutMilliseconds ([long]$TimeoutSeconds * 1000)
    $summary.processes += $fact
    $prerequisite = $Phase -ceq 'Build' -and $fact.observedExit -and $fact.exitCode -eq 2 -and -not $fact.forcedCleanup -and $fact.activeProcesses -eq 0
    if ($prerequisite) {
        foreach ($field in @('observedExit', 'jobAssigned', 'streamsDrained')) {
            if ($fact.$field -isnot [bool] -or -not $fact.$field) { throw "Incomplete prerequisite process observation: $field" }
        }
        foreach ($field in @('timedOut', 'cancelled', 'forcedCleanup')) {
            if ($fact.$field -isnot [bool] -or $fact.$field) { throw "Nonordinary prerequisite process outcome: $field" }
        }
        if ($fact.status -isnot [string] -or $fact.status -cne 'NonZeroExit' -or
            ($fact.exitCode -isnot [int] -and $fact.exitCode -isnot [long]) -or
            ($fact.activeProcesses -isnot [int] -and $fact.activeProcesses -isnot [uint32] -and $fact.activeProcesses -isnot [long]) -or
            -not [string]::IsNullOrEmpty($fact.observerError) -or -not [string]::IsNullOrEmpty($fact.cleanupError)) {
            throw 'The prerequisite process has no ordinary exit/cleanup outcome.'
        }
    } else { Assert-YLTestProcessSucceeded $fact }
    if ($Phase -ceq 'Import') {
        $summary.package['importSha256'] = (Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash
        if ($summary.package.importSha256 -cne $summary.package.sha256) { throw 'The actual exported package changed during import.' }
        return
    }
    $report = [IO.File]::ReadAllText((Join-Path $output 'report.json')) | ConvertFrom-Json -DateKind String
    foreach ($field in @('hostId', 'resultId', 'unityVersion', 'phase')) {
        if ($report.$field -isnot [string]) { throw 'Distribution evidence identity fields must be strings.' }
    }
    if ($report.schemaVersion -isnot [long] -or $report.schemaVersion -ne 1 -or $report.result.status -isnot [string] -or
        $report.hostId -cne $HostRecord.id -or $report.resultId -cne $run.id -or $report.unityVersion -cne $phaseEditor.version -or
        $report.phase -cne $Phase) { throw 'Distribution evidence identity does not match.' }
    if ($prerequisite) {
        if ($report.result.status -cne 'PrerequisiteNotMet') { throw 'Missing real Player prerequisite evidence.' }
        return $report
    }
    if ($report.result.status -cnotin @('Completed', 'Passed')) { throw 'Distribution evidence identity does not match.' }
    return $report
}

try {
    $toolchain = [Collections.Generic.List[string]]::new()
    $toolchain.Add($Unity)
    if ($ImportUnity -ine $Unity) { $toolchain.Add($ImportUnity) }
    $leafPaths = @()
    if ($HybridCLR) {
        $NativeSource = Get-YLTestPath $NativeSource
        $nativeFiles = @(Get-YLTestTreeFiles $NativeSource)
        if ($editor.version -ceq '2022.3.62f3') {
            $il2cpp = Join-Path $repository 'Library/HybridCLR/il2cpp_plus_repo'
            $hybrid = Join-Path $repository 'Library/HybridCLR/hybridclr_repo'
            if ($NativeSource -ine (Join-Path $il2cpp 'libil2cpp')) { throw 'The 2022 source must be the existing pinned official native source.' }
            foreach ($item in @(@($il2cpp, 'ba3a76c9979d14cee404fb5d44301773e0768866'), @($hybrid, '83929787581bce6c7219418f62348a10c33582ee'))) {
                $head = & git -C $item[0] rev-parse HEAD
                if ($LASTEXITCODE -ne 0 -or $head -cne $item[1]) { throw 'Existing native Git provenance differs from the pinned 2022 inputs.' }
            }
            & git -C $il2cpp diff --exit-code HEAD -- libil2cpp
            if ($LASTEXITCODE -ne 0) { throw 'The pinned IL2CPP source is modified.' }
            & git "--git-dir=$hybrid/.git" "--work-tree=$NativeSource" diff --exit-code HEAD -- hybridclr
            if ($LASTEXITCODE -ne 0) { throw 'The pinned HybridCLR source is modified.' }
            $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($path in @(& git -C $il2cpp ls-tree -r --name-only HEAD -- libil2cpp)) { $null = $expected.Add($path.Substring('libil2cpp/'.Length)) }
            if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate the exact IL2CPP source tree.' }
            foreach ($path in @(& git -C $hybrid ls-tree -r --name-only HEAD -- hybridclr)) { $null = $expected.Add($path) }
            if ($LASTEXITCODE -ne 0 -or -not $expected.SetEquals([string[]]@($nativeFiles | ForEach-Object { [IO.Path]::GetRelativePath($NativeSource, $_).Replace('\', '/') }))) {
                throw 'Unknown or missing files exist in the official native source tree.'
            }
            $licenses = @((Join-Path $il2cpp 'LICENSE'), (Join-Path $hybrid 'LICENSE'))
            $nativeVariant = 'Official2022'
        } elseif ($editor.version -ceq '6000.3.27f1') {
            if (-not $NativeSourceManifest) { throw 'The explicit maintained 6.3 source manifest is required.' }
            $NativeSourceManifest = Get-YLTestPath $NativeSourceManifest
            $verified = & (Join-Path $repository 'Tools/HybridCLR/Prepare-HybridCLRNativeSource.ps1') -Operation Verify -SourceManifest $NativeSourceManifest
            $manifest = [IO.File]::ReadAllText($NativeSourceManifest) | ConvertFrom-Json -DateKind String
            if (-not $verified.Verified -or $manifest.Variant -cne 'ParameterOwner' -or $manifest.SourceDirectory -ine $NativeSource) { throw 'The exact verified 6.3 maintained source is required.' }
            $nativeVariant = $manifest.Variant
            $licenses = @($manifest.LicenseFiles.Path)
            $toolchain.Add($NativeSourceManifest)
            $toolchain.Add((Join-Path $repository 'Tools/HybridCLR/Prepare-HybridCLRNativeSource.ps1'))
            $toolchain.Add((Join-Path $repository 'Tools/HybridCLR/GlobalMetadata-ParameterOwner.patch'))
        } else { throw 'No native source profile is implemented for this Editor.' }
        foreach ($path in @($nativeFiles) + @($licenses)) { $toolchain.Add($path) }
        $nativeInput = Join-Path $run.path 'NativeInput'
        $null = [IO.Directory]::CreateDirectory($nativeInput)
        $summary['nativeSource'] = @{ path = $NativeSource; variant = $nativeVariant; files = $nativeFiles.Count; licenses = @() }
        for ($i = 0; $i -lt $licenses.Count; $i++) {
            $target = Join-Path $nativeInput ("MIT-$i.txt")
            [IO.File]::Copy($licenses[$i], $target, $false)
            $summary.nativeSource.licenses += @{ path = $target; sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash }
        }
        foreach ($kind in @('Baseline', 'Runtime', 'Player')) {
            $cacheKey = "H$($kind.Substring(0, 1))-$($run.id.Substring(0, 8))"
            if ([IO.Path]::Exists((Join-Path $workspace.root "Cache/$cacheKey"))) { throw 'Do not adopt an existing native acceptance cache.' }
            $cache = New-YLTestDirectory -Workspace $workspace -Kind Cache -Key $cacheKey
            $hostLeases.Add((Lock-YLTestDirectory -Workspace $workspace -Path $cache.path))
            if ($kind -ceq 'Baseline') { $baselineRoot = Join-Path $cache.path 'B' }
            elseif ($kind -ceq 'Runtime') { $runtimeCache = Join-Path $cache.path 'C'; $cacheOwnerId = $cache.id }
            else { $nativePlayerRoot = Join-Path $cache.path 'P'; $playerOwnerId = $cache.id }
        }
        $leafPaths = @(Get-YLTestTreeFiles (Join-Path $repository 'Assets/YLFramework/Integrations/HybridCLR') | ForEach-Object { [IO.Path]::GetRelativePath($repository, $_).Replace('\', '/') })
        $leafPaths += @('Assets/YLFramework/Integrations.meta', 'Assets/YLFramework/Integrations/HybridCLR.meta')
    }
    $exporterPath = Join-Path $repository 'Assets/YLFramework/Editor/Distribution/ExportUnityPackage.cs'
    $exporter = [IO.File]::ReadAllText($exporterPath)
    $declaration = [regex]::Matches($exporter, '(?s)_exportRoots\s*=\s*\{(?<roots>.*?)\};')
    if ($declaration.Count -ne 1) { throw 'The actual exporter no longer has one inspectable fixed root declaration.' }
    $roots = @([regex]::Matches($declaration[0].Groups['roots'].Value, '"(?<path>Assets/YLFramework/[^"\r\n]+)"') | ForEach-Object { $_.Groups['path'].Value })
    if ($roots.Count -eq 0) { throw 'No actual product export roots were found.' }
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($root in $roots) {
        foreach ($path in Get-YLTestTreeFiles (Join-Path $repository $root)) { $null = $paths.Add([IO.Path]::GetRelativePath($repository, $path).Replace('\', '/')) }
        if ([IO.File]::Exists((Join-Path $repository ($root + '.meta')))) { $null = $paths.Add($root + '.meta') }
    }
    $null = $paths.Add('Assets/YLFramework.meta')
    $null = $paths.Add('Tools/Testing/DistributionEntry.cs')
    $null = $paths.Add('Tools/Testing/Tests/Test-YLDistribution.ps1')
    $exportPackageInputs = @(Get-YLUnityPackageInputs -EditorProfile $editor)
    $importPackageInputs = @(Get-YLUnityPackageInputs -EditorProfile $importEditor)
    foreach ($relative in $exportPackageInputs + $importPackageInputs) { $null = $paths.Add($relative) }
    foreach ($relative in @('Tools/Testing/Common/Workspace.ps1', 'Tools/Testing/Common/Process.ps1', 'Tools/Testing/Common/Results.ps1',
        'Tools/Testing/UnityHost.ps1', 'Tools/Testing/TestSuites.json')) { $null = $paths.Add($relative) }
    if ($RunPlayer) { $null = $paths.Add('Tools/Testing/PlayerSmoke.cs') }
    $productPaths = @($paths)
    if ($HybridCLR) {
        foreach ($path in $leafPaths + @('Tools/Testing/HybridCLREntry.cs', 'Tools/Testing/HybridCLRSmoke.cs', 'Packages/manifest.json', 'Packages/packages-lock.json')) { $null = $paths.Add($path) }
    }
    $profile = "Distribution-$($version.ProductVersion)-To-$($importVersion.ProductVersion)-$Backend-Hybrid$HybridCLR"
    $identity = Get-YLTestInputIdentity -Repository $repository -Paths @($paths) -Toolchain $toolchain.ToArray() -Profile $profile
    $summary['inputIdentity'] = $identity
    if ($HybridCLR) {
        $sdkManifest = [IO.File]::ReadAllText((Join-Path $repository 'Packages/manifest.json')) | ConvertFrom-Json -AsHashtable -NoEnumerate -DateKind String
        if ($sdkManifest -isnot [Collections.IDictionary] -or $sdkManifest['dependencies'] -isnot [Collections.IDictionary] -or
            $sdkManifest['dependencies']['com.code-philosophy.hybridclr'] -isnot [string] -or
            $sdkManifest['dependencies']['com.code-philosophy.hybridclr'] -cnotmatch '^https://[^\s#]+\.git#v9\.0\.3$') {
            throw 'The root HybridCLR SDK declaration must be an explicit Git URL pinned to v9.0.3.'
        }
        $sdkDeclaration = $sdkManifest['dependencies']['com.code-philosophy.hybridclr']
        $sdkLock = [IO.File]::ReadAllText((Join-Path $repository 'Packages/packages-lock.json')) | ConvertFrom-Json -AsHashtable -NoEnumerate -DateKind String
        if ($sdkLock -isnot [Collections.IDictionary] -or $sdkLock['dependencies'] -isnot [Collections.IDictionary] -or
            $sdkLock['dependencies']['com.code-philosophy.hybridclr'] -isnot [Collections.IDictionary]) {
            throw 'The root HybridCLR SDK lock entry is missing or invalid.'
        }
        $sdkPackage = $sdkLock['dependencies']['com.code-philosophy.hybridclr']
        if ($sdkPackage['version'] -isnot [string] -or $sdkPackage['version'] -cne $sdkDeclaration -or
            $sdkPackage['source'] -isnot [string] -or $sdkPackage['source'] -cne 'git' -or
            $sdkPackage['hash'] -isnot [string] -or $sdkPackage['hash'] -cnotmatch '^[0-9a-f]{40}$') {
            throw 'The root HybridCLR SDK lock must match the exact declaration and Git commit.'
        }
    }
    $bootstrap = [ordered]@{}
    $bootstrap['Assets/YLTestingDistribution/Editor/DistributionEntry.cs'] = [IO.File]::ReadAllBytes((Join-Path $repository 'Tools/Testing/DistributionEntry.cs'))
    $hostManifests = @{}
    foreach ($phase in @('Export', 'Import')) {
        $phaseEditor = if ($phase -ceq 'Export') { $editor } else { $importEditor }
        $packageInputs = if ($phase -ceq 'Export') { $exportPackageInputs } else { $importPackageInputs }
        $dependencies = [ordered]@{ 'com.unity.ugui' = $phaseEditor.ugui }
        $sourceManifest = [IO.File]::ReadAllText((Join-Path $repository $packageInputs[0])) | ConvertFrom-Json -DateKind String
        foreach ($entry in $sourceManifest.dependencies.PSObject.Properties | Where-Object { $_.Name.StartsWith('com.unity.modules.', [StringComparison]::Ordinal) }) { $dependencies[$entry.Name] = $entry.Value }
        $sourceLock = [IO.File]::ReadAllText((Join-Path $repository $packageInputs[1])) | ConvertFrom-Json -DateKind String
        $dependencies['com.unity.nuget.newtonsoft-json'] = $sourceLock.dependencies.'com.unity.nuget.newtonsoft-json'.version
        $hostManifests[$phase] = [Text.Encoding]::UTF8.GetBytes((@{ dependencies = $dependencies } | ConvertTo-Json -Depth 8) + "`n")
    }
    $bootstrap['Packages/manifest.json'] = $hostManifests.Import
    $exportFiles = [ordered]@{}
    foreach ($key in $bootstrap.Keys) { $exportFiles[$key] = $bootstrap[$key] }
    $exportFiles['Packages/manifest.json'] = $hostManifests.Export
    foreach ($relative in $productPaths | Where-Object { $_.StartsWith('Assets/', [StringComparison]::Ordinal) }) { $exportFiles[$relative] = [IO.File]::ReadAllBytes((Join-Path $repository $relative)) }
    $hosts = @{}
    foreach ($phase in @('Export', 'Import')) {
        $hostEditor = if ($phase -ceq 'Export') { $editor } else { $importEditor }
        $hostKey = if ($HybridCLR -and $phase -ceq 'Import') { "H$($run.id.Substring(0, 7))" } else { "D-$phase-$($hostEditor.version)-$($run.id.Substring(0, 8))" }
        if ([IO.Path]::Exists((Join-Path $workspace.root "Hosts/$hostKey"))) { throw 'Do not adopt an existing distribution host.' }
        $record = New-YLTestDirectory -Workspace $workspace -Kind Host -Key $hostKey
        $hostLeases.Add((Lock-YLTestDirectory -Workspace $workspace -Path $record.path))
        Assert-YLUnityHostIdle $record
        Sync-YLUnityHost -HostRecord $record -Files $(if ($phase -ceq 'Export') { $exportFiles } else { $bootstrap }) -Identity $identity -EditorVersion $hostEditor.version
        $hosts[$phase] = $record
    }
    $export = Invoke-DistributionProcess -HostRecord $hosts.Export -Phase Export
    $package = $export.result.package
    $summary['package'] = @{ path = $package; sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash }
    Add-Type -AssemblyName System.Formats.Tar
    $file = [IO.File]::OpenRead($package)
    $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionMode]::Decompress)
    $tar = [System.Formats.Tar.TarReader]::new($gzip)
    $rows = [Collections.Generic.List[object]]::new()
    $records = @{}
    try {
        while ($null -ne ($entry = $tar.GetNextEntry())) {
            if ($entry.EntryType -eq [System.Formats.Tar.TarEntryType]::Directory) { continue }
            $name = $entry.Name.TrimStart('./')
            if ($name -notmatch '^([0-9a-fA-F]{32})/(asset|asset\.meta|pathname|preview\.png)$' -or $entry.Length -gt 80MB) { throw "Unexpected product archive member: $name" }
            $guid = $Matches[1]
            $member = $Matches[2]
            if (-not $records.ContainsKey($guid)) { $records[$guid] = @{} }
            if ($records[$guid].ContainsKey($member)) { throw "Duplicate product archive member: $name" }
            if ($member -cin @('asset', 'preview.png')) {
                $records[$guid][$member] = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($entry.DataStream))
            } else {
                $buffer = [IO.MemoryStream]::new()
                try { $entry.DataStream.CopyTo($buffer); $records[$guid][$member] = $buffer.ToArray() } finally { $buffer.Dispose() }
            }
        }
        foreach ($guid in $records.Keys) {
            $record = $records[$guid]
            if (-not $record.ContainsKey('pathname') -or -not $record.ContainsKey('asset.meta')) { throw 'Actual product package identity is incomplete.' }
            $path = [Text.Encoding]::UTF8.GetString([byte[]]$record['pathname']).TrimEnd([char[]]@(0, 10, 13)).Replace('\', '/')
            if ($path -notlike 'Assets/YLFramework*' -or $path -match '^Assets/YLFramework/(Tests|Integrations)(?:/|$)|^Tools(?:/|$)|^\.kilo(?:/|$)' -or
                $path.Split('/') -ccontains '..' -or ($path -cne 'Assets/YLFramework' -and @($roots | Where-Object { $path -ceq $_ -or $path.StartsWith($_ + '/', [StringComparison]::Ordinal) -or $_.StartsWith($path + '/', [StringComparison]::Ordinal) }).Count -eq 0)) {
                throw "The actual export contains an unauthorized path: $path"
            }
            $metaText = [Text.Encoding]::UTF8.GetString([byte[]]$record['asset.meta'])
            if ($metaText -notmatch '(?m)^guid:\s*([0-9a-fA-F]{32})\s*\r?$' -or $Matches[1] -cne $guid) { throw "Package metadata identity mismatch: $path" }
            $source = Get-YLTestPath -Path (Join-Path $repository $path) -ReadOnly
            $metaHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$record['asset.meta']))
            if ((Get-FileHash -LiteralPath ($source + '.meta') -Algorithm SHA256).Hash -cne $metaHash) { throw "Package metadata bytes differ from source: $path" }
            $payloadHash = $null
            if ([IO.File]::Exists($source)) {
                if (-not $record.ContainsKey('asset')) { throw "A declared package file has no payload: $path" }
                $payloadHash = $record['asset']
                if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -cne $payloadHash) { throw "Package payload differs from actual source: $path" }
            } elseif (-not [IO.Directory]::Exists($source)) { throw "A package path is not in the actual product source: $path" }
            $rows.Add(@{ path = $path; guid = $guid; sha256 = $payloadHash; metaSha256 = $metaHash })
        }
    } finally { $tar.Dispose(); $gzip.Dispose(); $file.Dispose() }
    if ($rows.Count -eq 0 -or @($rows.path | Sort-Object -Unique).Count -ne $rows.Count) { throw 'The actual package paths are empty or duplicated.' }
    $included = [Collections.Generic.HashSet[string]]::new([string[]]@($rows.path), [StringComparer]::Ordinal)
    foreach ($meta in $paths | Where-Object { $_.StartsWith('Assets/', [StringComparison]::Ordinal) -and $_.EndsWith('.meta', [StringComparison]::Ordinal) }) {
        $asset = $meta.Substring(0, $meta.Length - 5)
        if (@($roots | Where-Object { $asset -ceq $_ -or $asset.StartsWith($_ + '/', [StringComparison]::Ordinal) }).Count -eq 0) { continue }
        $source = Join-Path $repository $asset
        if (-not $included.Contains($asset) -and (-not [IO.Directory]::Exists($source) -or @([IO.Directory]::EnumerateFiles($source, '*', [IO.SearchOption]::AllDirectories)).Count -gt 0)) {
            throw "The actual product export omitted a source asset: $asset"
        }
    }
    foreach ($root in $roots) { if (@($rows | Where-Object { $_.path -ceq $root -or $_.path.StartsWith($root + '/', [StringComparison]::Ordinal) }).Count -eq 0) { throw "The package omitted an actual export root: $root" } }
    Write-YLTestJson -Path (Join-Path $run.path 'package-assets.json') -Value @($rows)
    $summary.checks++
    'PASS: actual official exporter package contains only declared product roots and real asset identities'
    $null = Invoke-DistributionProcess -HostRecord $hosts.Import -Phase Import -Package $package
    $inspection = Invoke-DistributionProcess -HostRecord $hosts.Import -Phase Inspect -Guids $rows.ToArray()
    $summary.checks++
    'PASS: bare package import compiles without test context and preserves actual GUID/prefab/production boundaries'
    if ($RunPlayer) {
        $probeName = if ($HybridCLR) { 'HybridCLRSmoke' } else { 'PlayerSmoke' }
        $probeReferences = @('YLFramework.Runtime')
        if ($HybridCLR) { $probeReferences += @('YLFramework.HybridCLR', 'YLFramework.HybridCLR.Sample.Host') }
        $probeFiles = [ordered]@{
            "Assets/YLTestingDistribution/Smoke/$probeName.cs" = [IO.File]::ReadAllBytes((Join-Path $repository "Tools/Testing/$probeName.cs"))
            'Assets/YLTestingDistribution/Smoke/YLTesting.PlayerSmoke.asmdef' = [Text.Encoding]::UTF8.GetBytes((@{
                name = 'YLTesting.PlayerSmoke'; references = $probeReferences; autoReferenced = $false
            } | ConvertTo-Json -Depth 5) + "`n")
        }
        if ($HybridCLR) {
            foreach ($path in $leafPaths) { $probeFiles[$path] = [IO.File]::ReadAllBytes((Join-Path $repository $path)) }
            $probeFiles['Assets/YLTestingDistribution/HybridEditor/HybridCLREntry.cs'] = [IO.File]::ReadAllBytes((Join-Path $repository 'Tools/Testing/HybridCLREntry.cs'))
            $probeFiles['Assets/YLTestingDistribution/HybridEditor/YLTesting.HybridCLR.Editor.asmdef'] = [Text.Encoding]::UTF8.GetBytes((@{
                name = 'YLTesting.HybridCLR.Editor'; includePlatforms = @('Editor'); autoReferenced = $false
                references = @('YLFramework.Runtime', 'YLFramework.Editor', 'YLFramework.HybridCLR', 'YLFramework.HybridCLR.Editor',
                    'YLFramework.HybridCLR.Sample.Editor', 'HybridCLR.Editor', 'HybridCLR.Runtime')
                overrideReferences = $true; precompiledReferences = @('Newtonsoft.Json.dll')
            } | ConvertTo-Json -Depth 5) + "`n")
        } else { $probeFiles['Assets/YLTestingDistribution/Smoke/Resources/YLPlayerSmoke/Message.txt'] = [Text.Encoding]::UTF8.GetBytes("yl-player-smoke-v1`n") }
        foreach ($entry in $probeFiles.GetEnumerator()) {
            $target = Get-YLHostManagedPath $hosts.Import $entry.Key
            if ([IO.Path]::Exists($target) -or [IO.Path]::Exists($target + '.meta')) { throw "Do not replace existing Player smoke inputs: $($entry.Key)" }
        }
        $summary['probeInputs'] = @(foreach ($entry in $probeFiles.GetEnumerator()) {
            $target = Get-YLHostManagedPath $hosts.Import $entry.Key
            $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            [IO.File]::WriteAllBytes($target, [byte[]]$entry.Value)
            @{ path = $target; sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash }
        })
        if ($HybridCLR) {
            $manifestPath = Get-YLHostManagedPath $hosts.Import 'Packages/manifest.json'
            if ((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -cne [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$bootstrap['Packages/manifest.json']))) {
                throw 'Do not rewrite an altered receiving-host package manifest.'
            }
            $dependencies['com.code-philosophy.hybridclr'] = $sdkDeclaration
            [IO.File]::WriteAllText($manifestPath, ((@{ dependencies = $dependencies } | ConvertTo-Json -Depth 8) + "`n"), [Text.UTF8Encoding]::new($false))
            $summary.probeInputs += @{ path = $manifestPath; sha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash }
            $configure = Invoke-DistributionProcess -HostRecord $hosts.Import -Phase HybridConfigure
            $summary['nativePreparation'] = $configure.result
            $summary.checks++
            'PASS: existing exact native source prepared through the official local-source API in a fresh owned host'
        }
    }
    $buildPhase = if ($HybridCLR) { 'HybridBuild' } else { 'Build' }
    $build = Invoke-DistributionProcess -HostRecord $hosts.Import -Phase $buildPhase
    $summary.player = $build.result.status
    if ($build.result.status -ceq 'Passed' -and $build.result.backend -cne $Backend) { throw 'The actual build backend differs from the requested backend.' }
    if ($build.result.status -ceq 'Passed') { $summary.checks++; 'PASS: Player artifact has no test/Editor/Analyzer/optional backend references; only the explicit smoke probe may be added' }
    else { 'PREREQUISITE: no installed Windows64 Player module; this is not a Player pass' }
    if ($RunPlayer -and $build.result.status -ceq 'Passed') {
        $executable = if ($HybridCLR) { Join-Path $nativePlayerRoot 'YLDistribution.exe' } else { Join-Path $run.path "$buildPhase/Player/YLDistribution.exe" }
        if ([IO.Path]::GetFullPath($build.result.executable) -ine $executable) { throw 'The actual Player executable is outside the owned build output.' }
        $artifactRoot = [IO.Path]::GetDirectoryName($executable)
        $artifacts = @(Get-YLTestTreeFiles $artifactRoot | ForEach-Object { @{ path = $_; sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash } })
        $summary['playerArtifacts'] = $artifacts
        foreach ($phase in @('PlayerRun', 'PlayerRepeat')) {
            $output = Join-Path $run.path $phase
            $null = [IO.Directory]::CreateDirectory($output)
            $start = [Diagnostics.ProcessStartInfo]::new($executable)
            $start.UseShellExecute = $false
            $start.WorkingDirectory = $artifactRoot
            $arguments = @('-batchmode', '-logFile', (Join-Path $output 'player.log'), '-ylSmokeOutput', $output)
            $expectedHybrid = $null
            if ($HybridCLR) {
                $arguments += @('-ylHybridCLRReceipt', $build.result.receipt, '-force-d3d11', '-screen-width', '640', '-screen-height', '360')
                $expectedHybrid = $build.result.hybridClr
            } else { $arguments += '-nographics' }
            foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
            $fact = Invoke-YLTestProcess -StartInfo $start -Log (Join-Path $output 'process.log') -TimeoutMilliseconds ([long]$PlayerTimeoutSeconds * 1000)
            $summary.processes += $fact
            $summary.playerRun = Read-YLPlayerSmoke -Path (Join-Path $output 'result.json') -ResultId $run.id -HostId $hosts.Import.id -UnityVersion $importEditor.version -Backend $Backend -HybridCLR $expectedHybrid -ProcessResult $fact
            $summary.playerRuns += $summary.playerRun
            $playerLog = [IO.File]::ReadAllText((Join-Path $output 'player.log'))
            if (-not $playerLog.Contains('[YLPlayerSmoke] PASS ' + $run.id) -or $playerLog -match '(?m)^.*(?:Exception:|Assertion failed|\[YLPlayerSmoke\] FAIL)') { throw 'Player log does not close the matching successful smoke run.' }
            foreach ($artifact in $artifacts) {
                if ((Get-FileHash -LiteralPath $artifact.path -Algorithm SHA256).Hash -cne $artifact.sha256) { throw 'Player artifacts changed during execution.' }
            }
            $summary.checks++
            'PASS: actual Player resource/workflow/release evidence and normal process exit close'
        }
        foreach ($inputFile in $summary.probeInputs) {
            if ((Get-FileHash -LiteralPath $inputFile.path -Algorithm SHA256).Hash -cne $inputFile.sha256) { throw 'Smoke inputs changed during build/execution.' }
        }
    }
    $current = Get-YLTestInputIdentity -Repository $repository -Paths @($paths) -Toolchain $toolchain.ToArray() -Profile $profile
    if ($current.sha256 -cne $identity.sha256) { throw 'Distribution source inputs changed during verification.' }
    if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -cne $summary.package.sha256) { throw 'The actual exported package changed during verification.' }
    $summary.status = if ($summary.player -ceq 'Passed') { 'Passed' } else { 'PrerequisiteNotMet' }
} catch { $summary.reason = $_.Exception.Message; throw }
finally {
    $summary.endUtc = [DateTime]::UtcNow.ToString('o')
    try { Write-YLTestJson -Path (Join-Path $run.path 'summary.json') -Value $summary }
    finally { foreach ($hostLease in $hostLeases) { $hostLease.Dispose() }; $lease.Dispose() }
    $null = Invoke-YLTestRotation -Workspace $workspace -Key $run.key
    "Distribution evidence: $($run.path)"
}
if ($summary.status -cne 'Passed') { exit 2 }
