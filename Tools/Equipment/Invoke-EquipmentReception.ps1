#requires -Version 7.6
param(
    [Parameter(Mandatory)][ValidateSet('Prepare','Audit','Create','Import','Harness','SyncHarness','BuildMono','BuildIl2cpp','Excel','TestExcel','Bind')][string]$Action,
    [Parameter(Mandatory)][string]$Root,
    [string]$Repository = 'G:\Unity\Projects\CGJ2024',
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\2022.3.62f3c1\Editor\Unity.exe',
    [ValidatePattern('^[A-Za-z0-9-]+$')][string]$Run = 'initial'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
$Root = [IO.Path]::GetFullPath($Root)
$Repository = [IO.Path]::GetFullPath($Repository)
$project = Join-Path $Root 'R'
$package = Join-Path $Root 'YLFramework-Equipment.unitypackage'
$approved = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Temp/kilo')) + [IO.Path]::DirectorySeparatorChar
if (-not $Root.StartsWith($approved,[StringComparison]::OrdinalIgnoreCase)) { throw 'Expected the approved short temporary delivery root.' }
for ($cursor = $Root; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
    if ([IO.Path]::Exists($cursor) -and ([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked paths are not owned receiver paths.' }
}
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Assert-EquipmentOptionalExport([string]$PackagePath, [string]$Repository) {
    Add-Type -AssemblyName System.Formats.Tar
    $license = 'Assets/YLFramework/LICENSE.md'
    $roots = @('Assets/YLFramework/Integrations/Excel/Editor','Assets/YLFramework/Integrations/DomainExcel/Editor', $license)
    $expected = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($sourceRoot in $roots) {
        $absolute = Join-Path $Repository $sourceRoot
        $expected.Add($sourceRoot, (Hash ($absolute + '.meta')))
        if ([IO.Directory]::Exists($absolute)) {
            foreach ($meta in [IO.Directory]::GetFiles($absolute, '*.meta', [IO.SearchOption]::AllDirectories)) {
                $relative = [IO.Path]::GetRelativePath($Repository, $meta).Replace('\','/')
                $expected.Add($relative.Substring(0, $relative.Length - 5), (Hash $meta))
            }
        }
    }
    $records = @{}
    $stream = [IO.File]::OpenRead($PackagePath)
    $gzip = [IO.Compression.GZipStream]::new($stream, [IO.Compression.CompressionMode]::Decompress)
    $tar = [System.Formats.Tar.TarReader]::new($gzip, $true)
    try {
        while ($null -ne ($entry = $tar.GetNextEntry())) {
            if ($entry.EntryType -eq [System.Formats.Tar.TarEntryType]::Directory) { continue }
            $name = $entry.Name.TrimStart('./')
            if ($name -cnotmatch '^([0-9a-f]{32})/(asset|asset\.meta|pathname|preview\.png)$' -or $entry.Length -gt 80MB) { throw 'Unexpected optional archive member.' }
            $guid = $Matches[1]; $member = $Matches[2]
            if (-not $records.ContainsKey($guid)) { $records[$guid] = @{} }
            if ($records[$guid].ContainsKey($member)) { throw 'Duplicate optional archive member.' }
            $buffer = [IO.MemoryStream]::new()
            try {
                if ($entry.DataStream) { $entry.DataStream.CopyTo($buffer) }
                elseif ($entry.Length -ne 0) { throw 'Optional payload stream is missing.' }
                $records[$guid][$member] = $buffer.ToArray()
            } finally { $buffer.Dispose() }
        }
    } finally { $tar.Dispose(); $gzip.Dispose(); $stream.Dispose() }
    $paths = @(foreach ($pair in $records.GetEnumerator()) {
        $record = $pair.Value
        if (-not $record.ContainsKey('pathname') -or -not $record.ContainsKey('asset.meta')) { throw 'Optional package identity is missing.' }
        $path = [Text.Encoding]::UTF8.GetString($record['pathname']).TrimEnd([char]0,"`r","`n")
        if (-not $expected.ContainsKey($path) -or
            ($path -cne $license -and $path -cnotmatch '^Assets/YLFramework/Integrations/(Excel|DomainExcel)/Editor(?:/|$)') -or
            $path -match '/Tests/|EPPlus') { throw 'Unexpected optional export member.' }
        $meta = [Text.Encoding]::UTF8.GetString($record['asset.meta'])
        if ($meta -cnotmatch '(?m)^guid:\s*([0-9a-f]{32})\s*\r?$' -or $Matches[1] -cne $pair.Key) { throw "Optional GUID mismatch: $path" }
        $metaHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$record['asset.meta']))
        if ($metaHash -cne $expected[$path]) { throw "Optional Meta differs: $path" }
        $source = Join-Path $Repository $path
        if ([IO.File]::Exists($source)) {
            if (-not $record.ContainsKey('asset')) { throw "Optional Payload is missing: $path" }
            $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$record['asset']))
            if ($hash -cne (Hash $source)) { throw "Optional Payload differs: $path" }
        } elseif (-not [IO.Directory]::Exists($source) -or ($record.ContainsKey('asset') -and $record['asset'].Length -ne 0)) {
            throw "Unexpected optional folder payload: $path"
        }
        $null = $expected.Remove($path)
        $path
    })
    if ($expected.Count) { throw 'Optional Editor/product-license export closure is incomplete.' }
    foreach ($required in @('MiniExcel.dll','MiniExcel-LICENSE.txt','Dependencies.json','DomainExcelCommands.cs')) {
        if (-not @($paths | Where-Object { $_.EndsWith('/' + $required, [StringComparison]::Ordinal) }).Count) { throw "Optional export missing $required" }
    }
    if ($paths -cnotcontains $license) { throw 'Optional export must include the actual product LICENSE.md.' }
    $paths
}
function Json([string]$Path, $Value) {
    if ([IO.File]::Exists($Path)) { throw "Preserve existing evidence: $Path" }
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 40))
}
function SharedText([string]$Path) {
    $stream = [IO.File]::Open($Path,'Open','Read',([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $reader = [IO.StreamReader]::new($stream)
    try { $reader.ReadToEnd() } finally { $reader.Dispose(); $stream.Dispose() }
}
function RunUnity([string[]]$Arguments, [string]$Log) {
    if (Test-Path -LiteralPath $Log) { throw 'Do not overwrite a Unity invocation.' }
    $others = @(Get-CimInstance Win32_Process -Filter "Name = 'Unity.exe'" | Where-Object { $_.CommandLine -like ('*' + $project + '*') -and $_.CommandLine -notlike '*AssetImportWorker*' })
    if ($others.Count) { throw 'This receiver already has a Unity instance; recover, never launch twice.' }
    $start = [Diagnostics.ProcessStartInfo]::new($Unity)
    $start.UseShellExecute = $false; $start.WorkingDirectory = $Root
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    Json ($Log + '.process.json') @{ pid=$process.Id; project=$project; arguments=$Arguments; packageSha256=(Hash $package); utc=[DateTime]::UtcNow.ToString('o') }
    $process.WaitForExit()
    $code = $process.ExitCode; $process.Dispose()
    Json ($Log + '.exit.json') @{ exitCode=$code; utc=[DateTime]::UtcNow.ToString('o') }
    if ($code -ne 0) { throw "Unity exited $code; preserve $Log" }
}
if ($Action -eq 'Prepare') {
    if (Test-Path -LiteralPath $Root) { throw 'Never adopt an existing delivery directory.' }
    [IO.Directory]::CreateDirectory($Root) | Out-Null
    Json (Join-Path $Root 'owner.json') @{ purpose='Equipment-independent-delivery'; root=$Root; repository=$Repository; unity='2022.3.62f3c1'; utc=[DateTime]::UtcNow.ToString('o') }
    'Owned Equipment delivery directory prepared.'
    return
}
$owner = [IO.File]::ReadAllText((Join-Path $Root 'owner.json')) | ConvertFrom-Json
if ($owner.purpose -ne 'Equipment-independent-delivery' -or $owner.root -ne $Root -or $owner.repository -ne $Repository) { throw 'Delivery owner mismatch.' }
if ($Action -eq 'Audit') {
    Add-Type -AssemblyName System.Formats.Tar
    $exporter = [IO.File]::ReadAllText((Join-Path $Repository 'Assets/YLFramework/Editor/Distribution/ExportUnityPackage.cs'))
    $block = [regex]::Match($exporter,'(?s)\b_exportRoots\s*=\s*\{(.*?)\};').Groups[1].Value
    $roots = @([regex]::Matches($block,'"(Assets/[^"\r\n]+)"') | ForEach-Object { $_.Groups[1].Value })
    $expectedRoots = @('Assets/YLFramework/Editor', 'Assets/YLFramework/Plugins/BouncyCastle', 'Assets/YLFramework/Prefabs',
        'Assets/YLFramework/Runtime', 'Assets/YLFramework/Samples', 'Assets/YLFramework/Documentation',
        'Assets/YLFramework/LICENSE.md')
    if ($roots.Count -ne $expectedRoots.Count -or @(Compare-Object -ReferenceObject $expectedRoots -DifferenceObject $roots -CaseSensitive).Count) { throw 'Expected the exact six directories and one file from the formal exporter.' }
    $expected = @{}
    foreach ($sourceRoot in $roots) {
        $absolute = Join-Path $Repository $sourceRoot; $expected[$sourceRoot] = Hash ($absolute + '.meta')
        if ([IO.Directory]::Exists($absolute)) {
            foreach ($meta in [IO.Directory]::GetFiles($absolute,'*.meta',[IO.SearchOption]::AllDirectories)) {
                $relative = [IO.Path]::GetRelativePath($Repository,$meta).Replace('\','/'); $expected[$relative.Substring(0,$relative.Length-5)] = Hash $meta
            }
        }
    }
    $records=@{}; $stream=[IO.File]::OpenRead($package); $gzip=[IO.Compression.GZipStream]::new($stream,[IO.Compression.CompressionMode]::Decompress); $tar=[System.Formats.Tar.TarReader]::new($gzip,$true)
    try {
        while ($null -ne ($entry=$tar.GetNextEntry())) {
            if ($entry.EntryType -eq [System.Formats.Tar.TarEntryType]::Directory) { continue }
            $name=$entry.Name.TrimStart('./')
            if ($name -notmatch '^([0-9a-f]{32})/(asset|asset\.meta|pathname|preview\.png)$' -or $entry.Length -gt 80MB) { throw "Unexpected archive member: $name" }
            $guid=$Matches[1]; $member=$Matches[2]
            if (-not $records.ContainsKey($guid)) { $records[$guid]=@{} }
            if ($records[$guid].ContainsKey($member)) { throw 'Duplicate archive member.' }
            $buffer=[IO.MemoryStream]::new()
            try { if ($entry.DataStream) { $entry.DataStream.CopyTo($buffer) }; $records[$guid][$member]=$buffer.ToArray() } finally { $buffer.Dispose() }
        }
    } finally { $tar.Dispose(); $gzip.Dispose(); $stream.Dispose() }
    $rows=@(foreach ($pair in $records.GetEnumerator()) {
        $record=$pair.Value
        if (-not $record.ContainsKey('pathname') -or -not $record.ContainsKey('asset.meta')) { throw 'Missing package identity.' }
        $relative=[Text.Encoding]::UTF8.GetString($record['pathname']).TrimEnd([char]0,"`r","`n")
        if (-not $expected.ContainsKey($relative) -or $relative -match '/Tests/|/Integrations/|Editor Enhancers|AcceptanceBuild|^Packages/|/StreamingAssets/|MiniExcel|\.(pfx|p12|pem|key)$|/(bcrypt|ncrypt|crypt32|Newtonsoft\.Json)\.dll$' -or
            ($relative -match '(?i)\.(dll|so|bundle)$' -and $relative -cnotin @('Assets/YLFramework/Plugins/BouncyCastle/BouncyCastle.Cryptography.dll','Assets/YLFramework/Runtime/Core/Analyzers/YLFramework.Analyzers.dll'))) { throw "Excluded/unexpected package path: $relative" }
        $meta=[Text.Encoding]::UTF8.GetString($record['asset.meta'])
        if ($meta -notmatch '(?m)^guid:\s*([0-9a-f]{32})\s*\r?$' -or $Matches[1] -cne $pair.Key) { throw "GUID mismatch: $relative" }
        $metaHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$record['asset.meta']))
        if ($metaHash -cne $expected[$relative]) { throw "Meta differs: $relative" }
        $source=Join-Path $Repository $relative; $file=[IO.File]::Exists($source); $sha=$null
        if ($file) {
            if (-not $record.ContainsKey('asset')) { throw "Missing payload: $relative" }
            $sha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$record['asset']))
            if ($sha -cne (Hash $source)) { throw "Payload differs: $relative" }
        }
        $expected.Remove($relative)
        @{ path=$relative; guid=$pair.Key; sha256=$sha; metaSha256=$metaHash }
    })
    $missing=@($expected.Keys | Where-Object { $absolute=Join-Path $Repository $_; -not [IO.Directory]::Exists($absolute) -or [IO.Directory]::GetFiles($absolute,'*',[IO.SearchOption]::AllDirectories).Count -gt 0 })
    if ($missing.Count) { throw ('Incomplete formal closure: ' + ($missing -join ', ')) }
    Json ($package + '.files.json') @($rows | Sort-Object path)
    Json ($package + '.receipt.json') @{ passed=$true; sha256=(Hash $package); records=$rows.Count; roots=$roots; repository=$Repository; utc=[DateTime]::UtcNow.ToString('o') }
    "Formal package audited: $($rows.Count) records; $(Hash $package)"
    return
}
$audit=[IO.File]::ReadAllText($package + '.receipt.json') | ConvertFrom-Json
if (-not $audit.passed -or (Hash $package) -ne $audit.sha256) { throw 'Package is not the frozen audited artifact.' }
if ($Action -eq 'Create') {
    if (Test-Path -LiteralPath $project) { throw 'Never adopt an existing receiver.' }
    $log=Join-Path $Root 'receiver-create.log'
    RunUnity @('-batchmode','-quit','-createProject',$project,'-logFile',$log) $log
    Json (Join-Path $project 'equipment-receiver.json') @{ purpose='Equipment-independent-receiver'; project=$project; packageSha256=$audit.sha256; repository=$Repository }
    $manifestPath=Join-Path $project 'Packages/manifest.json'
    $manifest=[IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json -AsHashtable
    foreach ($key in @($manifest.dependencies.Keys)) { if (-not $key.StartsWith('com.unity.modules.')) { $manifest.dependencies.Remove($key) } }
    $manifest.dependencies['com.unity.ugui']='1.0.0'; $manifest.dependencies['com.unity.nuget.newtonsoft-json']='3.2.1'
    [IO.File]::WriteAllText($manifestPath,($manifest | ConvertTo-Json -Depth 10))
    'Fresh receiver created with minimal declared dependencies.'
    return
}
$identity=[IO.File]::ReadAllText((Join-Path $project 'equipment-receiver.json')) | ConvertFrom-Json
if ($identity.project -ne $project -or $identity.packageSha256 -ne $audit.sha256) { throw 'Receiver identity differs.' }
if ($Action -eq 'Import') {
    $code=@([IO.Directory]::GetFiles((Join-Path $project 'Assets'),'*',[IO.SearchOption]::AllDirectories) | Where-Object { [IO.Path]::GetExtension($_) -in @('.cs','.dll','.asmdef','.asmref') })
    if ($code.Count) { throw 'First import must precede all test facilities and consumers.' }
    $log=Join-Path $Root 'receiver-import.log'
    RunUnity @('-batchmode','-quit','-projectPath',$project,'-buildTarget','Win64','-importPackage',$package,'-logFile',$log) $log
    $errors=@([regex]::Matches((SharedText $log),'(?im)^.*(?:error CS\d+|Scripts have compiler errors|Aborting batchmode due to failure|Multiple precompiled assemblies).*$') | ForEach-Object Value)
    $assemblies=@(foreach ($name in @('YLFramework.Runtime','YLFramework.Editor','YLFramework.ResourceStartup.Sample','YLFramework.ResourceStartup.Sample.Editor')) {
        $path=Join-Path $project ('Library/ScriptAssemblies/'+$name+'.dll'); if (-not [IO.File]::Exists($path)) { throw "Missing first-import assembly: $name" }; @{ name=$name; sha256=(Hash $path) }
    })
    $lock=[IO.File]::ReadAllText((Join-Path $project 'Packages/packages-lock.json')) | ConvertFrom-Json -AsHashtable
    $forbidden=@($lock.dependencies.Keys | Where-Object { -not $_.StartsWith('com.unity.modules.') -and $_ -notin @('com.unity.ugui','com.unity.nuget.newtonsoft-json') })
    Json (Join-Path $Root 'first-import.json') @{ passed=($errors.Count -eq 0 -and $forbidden.Count -eq 0); packageSha256=$audit.sha256; harnessPresent=$false; priorCodeFiles=$code; assemblies=$assemblies; errors=$errors; forbiddenPackages=$forbidden; resolved=$lock }
    if ($errors.Count -or $forbidden.Count) { throw 'Fresh independent product import failed.' }
    'First import compiled with only the declared product packages, before any SDK, harness or test framework.'
    return
}
function CopyTree([string]$Source,[string]$Destination) {
    if (Test-Path -LiteralPath $Destination) { throw 'Do not overwrite installed fixture/optional source.' }
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($file in [IO.Directory]::GetFiles($Source,'*',[IO.SearchOption]::AllDirectories)) {
        $target=Join-Path $Destination ([IO.Path]::GetRelativePath($Source,$file)); [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null; [IO.File]::Copy($file,$target)
    }
}
if ($Action -eq 'Harness') {
    $first=[IO.File]::ReadAllText((Join-Path $Root 'first-import.json')) | ConvertFrom-Json
    if (-not $first.passed -or $first.harnessPresent -or $first.packageSha256 -ne $audit.sha256) { throw 'Prove product-only first import before installing a consumer.' }
    CopyTree (Join-Path $Repository 'Assets/YLFramework/Tests/Fixtures/Extensions/Equipment/Player') (Join-Path $project 'Assets/YLFramework/Tests/Fixtures/Extensions/Equipment/Player')
    $editor=Join-Path $project 'Assets/EquipmentHarness/Editor'; [IO.Directory]::CreateDirectory($editor) | Out-Null
    foreach ($name in @('EquipmentBuild.cs','EquipmentSettings.cs')) { [IO.File]::Copy((Join-Path $Repository ('Assets/YLFramework/Tests/Tools/Extensions/Equipment/Editor/'+$name)),(Join-Path $editor $name)) }
    'Exact Equipment consumer and protected build helper installed after first import.'
    return
}
if ($Action -eq 'SyncHarness') {
    $history=Join-Path $Root ('ToolsHistory/'+$Run)
    if (Test-Path -LiteralPath $history) { throw 'Do not overwrite previous tool revisions.' }
    [IO.Directory]::CreateDirectory($history) | Out-Null
    foreach ($name in @('EquipmentBuild.cs','EquipmentSettings.cs')) {
        $target=Join-Path $project ('Assets/EquipmentHarness/Editor/'+$name)
        [IO.File]::Copy($target,(Join-Path $history $name))
        [IO.File]::Copy((Join-Path $Repository ('Assets/YLFramework/Tests/Tools/Extensions/Equipment/Editor/'+$name)),$target,$true)
    }
    'Updated only owned harness helpers; prior revision retained.'
    return
}
if ($Action -in @('BuildMono','BuildIl2cpp')) {
    $backend=if ($Action -eq 'BuildMono') { 'Mono' } else { 'IL2CPP' }
    $gate=Join-Path $project ('Temp/EquipmentAcceptance/'+$backend+'-'+$Run)
    $archive=Join-Path $Root ('Gates/'+$backend+'-'+$Run)
    $stem=if ($backend -eq 'Mono') { 'RM-' } else { 'RI-' }
    $output=Join-Path ([IO.Path]::GetDirectoryName($Root)) ($stem+$Run)
    $log=Join-Path $Root ('receiver-build-'+$backend+'-'+$Run+'.log')
    RunUnity @('-batchmode','-projectPath',$project,'-buildTarget','Win64','-executeMethod','YLFramework.Tests.EquipmentAcceptance.EquipmentBuild.QueueFromCommandLine',
        '--eq-gate',$gate,'--eq-output',$output,'--eq-backend',$backend,'--eq-archive',$archive,'-logFile',$log) $log
    $receipt=[IO.File]::ReadAllText((Join-Path $archive 'gate-complete.json')) | ConvertFrom-Json
    if (-not $receipt.succeeded) { throw 'Receiver Player build did not pass.' }
    "Independent $backend Player built successfully."
    return
}
if ($Action -eq 'Excel') {
    CopyTree (Join-Path $Repository 'Assets/YLFramework/Integrations/Excel') (Join-Path $project 'Assets/YLFramework/Integrations/Excel')
    CopyTree (Join-Path $Repository 'Assets/YLFramework/Integrations/DomainExcel') (Join-Path $project 'Assets/YLFramework/Integrations/DomainExcel')
    $manifestPath=Join-Path $project 'Packages/manifest.json'; $manifest=[IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json -AsHashtable
    $manifest.dependencies['com.unity.test-framework']='1.3.9'; [IO.File]::WriteAllText($manifestPath,($manifest | ConvertTo-Json -Depth 10))
    [IO.File]::Copy((Join-Path $PSScriptRoot 'EquipmentReceiverExcel.cs.txt'),(Join-Path $project 'Assets/EquipmentHarness/Editor/EquipmentReceiverExcel.cs'))
    'Optional directories (including binary, dependencies, licenses and tests) installed after core Player validation; UTF1.3.9 declared.'
}
if ($Action -eq 'TestExcel') {
    $log=Join-Path $Root 'receiver-excel.log'
    RunUnity @('-batchmode','-projectPath',$project,'-buildTarget','Win64','-executeMethod','YLFramework.Tests.EquipmentAcceptance.EquipmentReceiverExcel.Run','-logFile',$log) $log
    $receipt=[IO.File]::ReadAllText((Join-Path $Root 'Gates/Excel/gate-complete.json')) | ConvertFrom-Json
    if (-not $receipt.passed) { throw 'Independent optional Excel gate failed.' }
    'Independent optional XLSX suite passed 15/15 and settings restored.'
}
if ($Action -eq 'Bind') {
    $files=[IO.File]::ReadAllText($package+'.files.json') | ConvertFrom-Json
    foreach ($file in $files) {
        $source=Join-Path $Repository $file.path
        if ((Hash ($source+'.meta')) -ne $file.metaSha256 -or ($null -ne $file.sha256 -and (Hash $source) -ne $file.sha256)) { throw 'Current formal source no longer matches the imported artifact.' }
    }
    $evidence=Join-Path $Repository 'Temp/EquipmentAcceptance/EQ-20261020T204732Z'
    $verdictPaths=@(
        (Join-Path $evidence 'MainMono2-20261021/MainMono/verdict.json'),
        (Join-Path $evidence 'MainIl2cpp-20261021/MainIl2cpp/verdict.json'),
        (Join-Path $Root 'Gates/Mono-saved-scene-archive/ReceiverMono/verdict.json'),
        (Join-Path $Root 'Gates/IL2CPP-low-release/ReceiverIl2cpp/verdict.json')
    )
    $players=@(foreach ($path in $verdictPaths) {
        $verdict=[IO.File]::ReadAllText($path) | ConvertFrom-Json
        if (-not $verdict.passed -or $verdict.exitCode -ne 0 -or $verdict.checks.Count -ne 15 -or @($verdict.errors).Count) { throw 'A required real Player gate is not green.' }
        @{ path=$path; sha256=(Hash $path); backend=$verdict.backend; run=$verdict.run; checks=$verdict.checks.Count }
    })
    $first=[IO.File]::ReadAllText((Join-Path $Root 'first-import.json')) | ConvertFrom-Json
    $excel=[IO.File]::ReadAllText((Join-Path $Root 'Gates/Excel/summary.json')) | ConvertFrom-Json
    $excelGate=[IO.File]::ReadAllText((Join-Path $Root 'Gates/Excel/gate-complete.json')) | ConvertFrom-Json
    $protection=[IO.File]::ReadAllText((Join-Path $evidence 'final-protection.json')) | ConvertFrom-Json
    if (-not $first.passed -or $first.harnessPresent -or $first.packageSha256 -ne $audit.sha256 -or -not $excelGate.passed -or $excel.PassCount -ne 15 -or $excel.FailCount -ne 0 -or -not $protection.passed) { throw 'First import, optional test or protection gate is not green.' }
    $optional=Join-Path $Root 'YLFramework-Equipment-Excel.unitypackage'
    if (-not [IO.File]::Exists($optional)) { throw 'Complete optional Editor delivery is missing.' }
    $optionalPaths = @(Assert-EquipmentOptionalExport $optional $Repository)
    $tools=Join-Path $Root 'Tools';[IO.Directory]::CreateDirectory($tools) | Out-Null
    foreach ($file in [IO.Directory]::GetFiles($PSScriptRoot)) { [IO.File]::Copy($file,(Join-Path $tools ([IO.Path]::GetFileName($file)))) }
    [IO.File]::Copy((Join-Path $evidence 'final-protection.json'),(Join-Path $Root 'main-protection.json'))
    Json (Join-Path $Root 'delivery-binding.json') @{ completed=[DateTime]::UtcNow.ToString('o'); repository=$Repository; core=@{path=$package;sha256=(Hash $package);records=$audit.records}; optional=@{path=$optional;sha256=(Hash $optional);paths=$optionalPaths;frameworkLicenseSha256=(Hash (Join-Path $Repository 'Assets/YLFramework/LICENSE.md'))};
        firstImport=(Join-Path $Root 'first-import.json'); receiver=$project; players=$players; excel=@{passed=15;failed=0;root=(Join-Path $Root 'Gates/Excel')}; protection=$protection;
        tools=@(Get-ChildItem -LiteralPath $tools -File | ForEach-Object { @{name=$_.Name;sha256=(Hash $_.FullName)} }) }
    'Bound current formal source, optional licenses, first import, all four actual Player verdicts and final protection.'
}
