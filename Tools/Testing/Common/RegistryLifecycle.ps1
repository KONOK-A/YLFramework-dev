#requires -Version 7.6
#requires -PSEdition Core
. (Join-Path $PSScriptRoot 'Workspace.ps1')
. (Join-Path $PSScriptRoot 'Process.ps1')
. (Join-Path $PSScriptRoot 'Results.ps1')
. (Join-Path $PSScriptRoot 'Packages.ps1')
. (Join-Path $PSScriptRoot '../UnityHost.ps1')

function Get-YLRegistryLifecyclePhases {
    param([string] $EntryPackage, [string[]] $EntryPackages = @($EntryPackage), [switch] $VerifyCombinations)
    $roots = @(Get-YLRegistryEntryPackages -EntryPackage $EntryPackage -EntryPackages $EntryPackages)
    if ($VerifyCombinations -and $roots.Count -ne 2) { throw 'VerifyCombinations requires the explicit ExcelGameplay or ReddotSave roots.' }
    $phases = @('Inspect', 'Reopen')
    if ($VerifyCombinations) {
        if ($EntryPackage -ceq 'com.kano.ylframework.reddot') { $phases += @('RemoveSave', 'VerifyReddotOnly', 'RestoreSave', 'RemoveReddot', 'VerifySaveOnly', 'RestoreReddot') }
        else { $phases += @('RemoveGameplay', 'VerifyExcelOnly', 'RestoreGameplay', 'RemoveExcel', 'VerifyGameplayOnly', 'RestoreExcel') }
    }
    return $phases + @('Remove', 'VerifyRemoved')
}

function Get-YLRegistryLifecycleState {
    param([Collections.IDictionary] $Request, [Collections.IDictionary] $Release, [Collections.IDictionary] $Original)
    $roots = @(if ($Request.Contains('entryPackages')) { $Request.entryPackages } else { $Request.entryPackage })
    if (($Request.Contains('entryPackages') -and $Request.entryPackages -isnot [array]) -or
        @($roots | Where-Object { $_ -isnot [string] }).Count -or $Request.entryPackage -isnot [string] -or
        ($Request.Contains('verifyCombinations') -and $Request.verifyCombinations -isnot [bool])) { throw 'Invalid lifecycle entry roots/combination field types.' }
    $combinations = $Request.Contains('verifyCombinations') -and $Request.verifyCombinations
    $phases = @(Get-YLRegistryLifecyclePhases -EntryPackage $Request.entryPackage -EntryPackages $roots -VerifyCombinations:$combinations)
    if ($Request.phase -isnot [string] -or $Request.phase -cnotin $phases) { throw 'Lifecycle phase differs from its explicit roots/combination profile.' }
    $before = $roots; $after = $roots
    if ($combinations) {
        $first = if ($Request.entryPackage -ceq 'com.kano.ylframework.reddot') { 'com.kano.ylframework.reddot' } else { 'com.kano.ylframework.excel' }
        $second = if ($first -ceq 'com.kano.ylframework.reddot') { 'com.kano.ylframework.save' } else { 'com.kano.ylframework.gameplay' }
        if ($Request.phase -cin $phases[2..3]) { $after = @($first) }
        if ($Request.phase -ceq $phases[4]) { $before = @($first) }
        if ($Request.phase -cin $phases[5..6]) { $after = @($second) }
        if ($Request.phase -ceq $phases[7]) { $before = @($second) }
        if ($Request.phase -cin @($phases[3], $phases[6])) { $before = $after }
    }
    if ($Request.phase -cin @('Remove', 'VerifyRemoved')) { $after = @() }
    if ($Request.phase -ceq 'VerifyRemoved') { $before = $after }
    $manifests = @(foreach ($selection in @(@{ roots = $before }, @{ roots = $after })) {
        $dependencies = [ordered]@{}
        if ($selection.roots.Count) {
            $ids = if ($Request.packageOrigin -ceq 'LocalTarball') { @(Get-YLRegistryClosure -Release $Release -EntryPackage $selection.roots[0] -EntryPackages $selection.roots).id } else { $selection.roots }
            foreach ($id in $ids) { $dependencies[$id] = $Original.dependencies[$id] }
        }
        @{ dependencies = $dependencies; scopedRegistries = $Original.scopedRegistries }
    })
    $remove = @($manifests[0].dependencies.Keys | Where-Object { -not $manifests[1].dependencies.Contains($_) } | Sort-Object -CaseSensitive)
    $add = @(foreach ($id in @($manifests[1].dependencies.Keys | Where-Object { -not $manifests[0].dependencies.Contains($_) } | Sort-Object -CaseSensitive)) {
        if ($Request.packageOrigin -ceq 'Registry') { $id + '@' + $Original.dependencies[$id] } else { $Original.dependencies[$id] }
    })
    return @{ roots = $after; before = $manifests[0]; after = $manifests[1]; removePackages = $remove; addPackages = $add; mutation = ($remove.Count + $add.Count -gt 0) }
}

function Assert-YLRegistryLifecycleManifest {
    param([Collections.IDictionary] $Actual, [Collections.IDictionary] $Original, [string] $EntryPackage, [switch] $Removed)
    Assert-YLPackageFields $Actual @('dependencies', 'scopedRegistries')
    $dependencies = [ordered]@{}
    foreach ($item in $Original['dependencies'].GetEnumerator()) {
        if (-not $Removed -or $item.Key -cne $EntryPackage) { $dependencies[$item.Key] = $item.Value }
    }
    Assert-YLPackageDependencies $Actual['dependencies'] $dependencies
    if ($Actual['scopedRegistries'] -isnot [array] -or $Actual['scopedRegistries'].Count -ne 1) { throw 'Lifecycle manifest Registry configuration changed.' }
    $scope = $Actual['scopedRegistries'][0]; $expected = $Original['scopedRegistries'][0]
    Assert-YLPackageFields $scope @('name', 'url', 'scopes')
    foreach ($field in @('name', 'url')) { if ($scope[$field] -isnot [string] -or $scope[$field] -cne $expected[$field]) { throw 'Lifecycle manifest changed beyond removing its unique entry.' } }
    if ($scope['scopes'] -isnot [array] -or $scope['scopes'].Count -ne $expected['scopes'].Count) { throw 'Lifecycle manifest scopes changed.' }
    for ($i = 0; $i -lt $scope['scopes'].Count; $i++) {
        if ($scope['scopes'][$i] -isnot [string] -or $scope['scopes'][$i] -cne $expected['scopes'][$i]) { throw 'Lifecycle manifest scopes changed.' }
    }
}

function Assert-YLRegistryLifecycleInputs {
    param([object] $Workspace, [object] $HostRecord, [Collections.IDictionary] $Receipt)
    if ($Receipt['purpose'] -cne 'YLTesting.RegistryLifecycle.NeutralInputs' -or $Receipt['hostId'] -cne $HostRecord.id -or
        $Receipt['workspaceId'] -cne $Workspace.id -or $Receipt['files'] -isnot [array] -or $Receipt['files'].Count -eq 0) { throw 'Invalid neutral input receipt ownership.' }
    $files = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $sentinels = @(foreach ($file in $Receipt['files']) {
        if ($file['path'] -isnot [string] -or -not $file['path'].StartsWith('Assets/', [StringComparison]::Ordinal) -or
            $file['sha256'] -isnot [string] -or $file['sha256'] -cnotmatch '^[0-9a-f]{64}$' -or $file['ownerId'] -cne $HostRecord.id -or
            -not $files.Add($file['path'])) { throw 'Invalid neutral input file identity.' }
        $path = Get-YLHostManagedPath $HostRecord $file['path']
        $actualHash = if ([IO.File]::Exists($path)) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
        if ($actualHash -cne $file['sha256']) { throw "Neutral input/sentinel bytes or meta changed: $($file['path'])" }
        if ($file['role'] -ceq 'Sentinel') { @{ path = $file['path']; beforeSha256 = $file['sha256']; afterSha256 = $actualHash; ownerId = $file['ownerId'] } }
    })
    $actual = @(Get-YLTestTreeFiles (Join-Path $HostRecord.path 'Assets'))
    if ($actual.Count -ne $files.Count) { throw 'Unregistered neutral host content appeared; it is not removed or adopted.' }
    foreach ($path in $actual) {
        if (-not $files.Contains([IO.Path]::GetRelativePath($HostRecord.path, $path).Replace('\', '/'))) { throw 'Unregistered neutral host input appeared.' }
    }
    return @{ status = 'Unchanged'; checkedUtc = [DateTime]::UtcNow.ToString('o'); meaning = 'NeutralBytesAndMetaOnlyNotAuthoringExecution'; files = $sentinels }
}

function Read-YLRegistryLifecyclePhase {
    param([string] $Path, [Collections.IDictionary] $Request, [object] $ProcessResult, [Collections.IDictionary] $Release,
        [Collections.IDictionary] $OriginalManifest, [object] $HostRecord)
    Assert-YLTestProcessSucceeded $ProcessResult
    $report = Read-YLPackageEvidenceJson $Path
    foreach ($field in @('purpose', 'phase', 'hostId', 'resultId', 'cacheId', 'workspaceId', 'inputHash', 'entryPackage', 'unityVersion', 'unityProductVersion', 'packageOrigin', 'releaseManifestSha256', 'requestId')) {
        if ($report[$field] -isnot [string] -or $report[$field] -cne $Request[$field]) { throw "Lifecycle report identity mismatch: $field" }
    }
    if ($report['unityExecutable'] -isnot [string] -or (Get-YLTestPath -Path $report['unityExecutable'] -ReadOnly) -ine $Request.unityExecutable) { throw 'Lifecycle report actual Editor executable differs.' }
    if ($report['schemaVersion'] -isnot [long] -or $report['schemaVersion'] -ne 1 -or $report['status'] -cne 'Completed' -or
        $report['pid'] -isnot [long] -or $report['pid'] -ne $ProcessResult.pid -or $report['isBatchMode'] -isnot [bool] -or -not $report['isBatchMode'] -or
        $report['scriptCompilationFailed'] -isnot [bool] -or $report['scriptCompilationFailed'] -or $report['error'] -isnot [string] -or $report['error'] -cne '' -or
        $report['requestSha256'] -cne (Get-FileHash -LiteralPath (Join-Path $Request.output 'request.json') -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Lifecycle report has no complete successful process/compilation evidence.' }
    Assert-YLRegistryCacheObservation -Actual $report['cacheEnvironment'] -CacheRoot $Request.cacheRoot
    $state = Get-YLRegistryLifecycleState -Request $Request -Release $Release -Original $OriginalManifest
    $removed = $state.roots.Count -eq 0
    $roots = @(if ($Request.Contains('entryPackages')) { $Request.entryPackages } else { $Request.entryPackage })
    Assert-YLPackageStringArray -Actual $report['entryPackages'] -Expected $roots -Label 'Lifecycle report entry roots'
    $manifestPath = Get-YLHostManagedPath $HostRecord 'Packages/manifest.json'
    $lockPath = Get-YLHostManagedPath $HostRecord 'Packages/packages-lock.json'
    $manifest = Read-YLPackageEvidenceJson $manifestPath; $lock = Read-YLPackageEvidenceJson $lockPath
    foreach ($item in @(@{ path = $manifestPath; field = 'manifestSha256' }, @{ path = $lockPath; field = 'lockSha256' })) {
        if ($report[$item.field] -isnot [string] -or $report[$item.field] -cne (Get-FileHash -LiteralPath $item.path -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Lifecycle report does not bind the actual manifest/lock bytes.' }
    }
    Assert-YLRegistryLifecycleManifest -Actual $manifest -Original $state.after
    if ($Request.phase -cin @('Inspect', 'Reopen') -and (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Request.originalManifestSha256) { throw 'Initial/warm installation rewrote the original consumer manifest.' }
    if ($report['packages'] -isnot [array] -or $lock['dependencies'] -isnot [Collections.IDictionary]) { throw 'Missing observed lifecycle package graph.' }
    $rows = @(foreach ($row in $report['packages']) {
        if ($row -isnot [Collections.IDictionary] -or $row['dependencies'] -isnot [array]) { throw 'Invalid actual lifecycle PackageInfo dependencies.' }
        $dependencies = [ordered]@{}
        foreach ($dependency in $row['dependencies']) {
            if ($dependency['name'] -isnot [string] -or $dependency['version'] -isnot [string] -or $dependencies.Contains($dependency['name'])) { throw 'Duplicate/invalid actual PackageInfo dependency.' }
            $dependencies[$dependency['name']] = $dependency['version']
        }
        $row['dependencies'] = $dependencies; $row
    })
    if (-not $removed) {
        # The graph is observed independently; the request contains no expected PackageInfo rows.
        $graph = Assert-YLRegistryResolvedGraph -Manifest $manifest -Lock $lock -Rows $rows -Release $Release -EntryPackage $state.roots[0] -EntryPackages $state.roots -PackageSource $Request.packageOrigin -RegistryUrl $Request.registryUrl -EditorVersion $Request.unityVersion -HostRoot $Request.hostRoot -CacheRoot $Request.cacheRoot
    } else {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        if ($lock['dependencies'].Count -ne $rows.Count) { throw 'Removed PackageInfo and lock graphs differ.' }
        foreach ($row in $rows) {
            $id = $row['name']; $locked = $lock['dependencies'][$id]
            if ($id -isnot [string] -or -not $seen.Add($id) -or -not $id.StartsWith('com.unity.modules.', [StringComparison]::Ordinal) -or
                $row['source'] -cne 'BuiltIn' -or $row['version'] -cne '1.0.0' -or $row['packageId'] -cne ($id + '@1.0.0') -or
                $row['resolvedPath'] -isnot [string] -or -not $row['resolvedPath'] -or $row['isDirectDependency'] -isnot [bool] -or $row['isDirectDependency'] -or
                $locked -isnot [Collections.IDictionary] -or $locked['source'] -cne 'builtin' -or $locked['version'] -cne '1.0.0' -or
                $locked['depth'] -isnot [long] -or $locked['depth'] -lt 0) { throw 'Removed entry/indirect package/SDK remains in the actual resolved graph, or the remaining baseline is invalid.' }
            Assert-YLPackageDependencies $row['dependencies'] $locked['dependencies']
        }
        foreach ($row in $rows) { foreach ($id in $row['dependencies'].Keys) { if (-not $seen.Contains($id) -or $row['dependencies'][$id] -cne '1.0.0') { throw 'Incomplete removed baseline dependency graph.' } } }
        $graph = @{ packages = $rows; editorBaseline = $rows; origin = $Request.packageOrigin; removal = 'NoOwnOrSdkPackagesInActualGraph' }
    }
    if ($state.mutation) {
        $removal = $report['removal']
        $fields = @('requestSha256', 'hostId', 'resultId', 'cacheId', 'entryPackage', 'stage', 'requestStatus', 'completion', 'pid', 'issued', 'requestStatusObserved', 'phase', 'entryPackages', 'removePackages', 'addPackages', 'requestKind')
        Assert-YLPackageFields $removal $fields
        $saved = Read-YLPackageEvidenceJson (Join-Path $Request.output 'operation.json')
        Assert-YLPackageFields $saved $fields
        foreach ($field in $fields) {
            if ($null -eq $removal[$field] -or $null -eq $saved[$field] -or (ConvertTo-Json -InputObject $removal[$field] -Compress) -cne (ConvertTo-Json -InputObject $saved[$field] -Compress)) { throw 'Removal report and persisted bound operation differ.' }
        }
        foreach ($field in @('hostId', 'resultId', 'cacheId', 'entryPackage')) {
            if ($removal[$field] -isnot [string] -or $removal[$field] -cne $Request[$field]) { throw 'Removal operation belongs to another owner/entry.' }
        }
        foreach ($field in @('entryPackages', 'removePackages', 'addPackages')) {
            if ($removal[$field] -isnot [array] -or @($removal[$field] | Where-Object { $_ -isnot [string] -or -not $_ }).Count) { throw 'Invalid operation roots/target array types.' }
        }
        $kind = if ($state.addPackages.Count -eq 0 -and $state.removePackages.Count -eq 1) { 'Remove' } else { 'AddAndRemove' }
        if ($removal.phase -cne $Request.phase -or $removal.requestKind -cne $kind) { throw 'Operation kind/stage differs from the bound request.' }
        Assert-YLPackageStringArray -Actual $removal.entryPackages -Expected $roots -Label 'Operation entry roots'
        Assert-YLPackageStringArray -Actual $removal.removePackages -Expected $state.removePackages -Label 'Operation removal targets'
        Assert-YLPackageStringArray -Actual $removal.addPackages -Expected $state.addPackages -Label 'Operation addition targets'
        if ($removal['pid'] -isnot [long] -or $removal['pid'] -ne $ProcessResult.pid) { throw 'Removal operation belongs to another process.' }
        if ($removal -isnot [Collections.IDictionary] -or $removal['stage'] -cne 'Verified' -or $removal['issued'] -isnot [bool] -or -not $removal['issued'] -or
            $removal['requestSha256'] -cne $report['requestSha256'] -or
            $removal['requestStatusObserved'] -isnot [bool] -or
            -not (($removal['completion'] -ceq ($kind + 'Request') -and $removal['requestStatusObserved'] -and $removal['requestStatus'] -ceq 'Success') -or
                ($removal['completion'] -ceq 'ResolvedAfterReload' -and -not $removal['requestStatusObserved'] -and $removal['requestStatus'] -ceq ''))) { throw 'Missing actual asynchronous removal completion evidence.' }
    }
    Assert-YLRegistryLifecycleCompilation -Report $report -Roots $state.roots
    [IO.File]::Copy($manifestPath, (Join-Path $Request.output 'manifest.json'))
    [IO.File]::Copy($lockPath, (Join-Path $Request.output 'packages-lock.json'))
    return @{ phase = $Request.phase; status = 'Completed'; report = $report; reportPath = $Path; graph = $graph; process = $ProcessResult; output = $Request.output; error = '' }
}

function Assert-YLRegistryLifecycleCompilation {
    param([Collections.IDictionary] $Report, [string[]] $Roots)
    Assert-YLPackageFields $Report['compilation'] @('editor', 'player')
    foreach ($mode in @('editor', 'player')) {
        if ($Report.compilation[$mode] -isnot [array]) { throw 'Missing actual CompilationPipeline graph.' }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($row in $Report.compilation[$mode]) {
            Assert-YLPackageFields $row @('name', 'sourceFiles', 'assemblyReferences', 'compiledAssemblyReferences')
            if ($row.name -isnot [string] -or -not $row.name -or -not $seen.Add($row.name)) { throw 'Invalid/duplicate compilation assembly.' }
            foreach ($field in @('sourceFiles', 'assemblyReferences', 'compiledAssemblyReferences')) {
                if ($row[$field] -isnot [array] -or @($row[$field] | Where-Object { $_ -isnot [string] -or -not $_ }).Count) { throw 'Invalid compilation source/reference graph.' }
            }
        }
        if ($Report.entryPackage -cin @('com.kano.ylframework.reddot', 'com.kano.ylframework.save')) {
            $reddot = 'com.kano.ylframework.reddot' -cin $Roots; $save = 'com.kano.ylframework.save' -cin $Roots
            $expected = @()
            if ($reddot) { $expected += @('YLFramework.Runtime', 'YLFramework.RedDots'); if ($mode -ceq 'editor') { $expected += @('YLFramework.Editor', 'YLFramework.RedDots.Editor') } }
            if ($save) { $expected += 'YLFramework.Save' }
            if ($reddot -and $save) { $expected += 'YLFramework.RedDots.Save' }
            $product = @($seen | Where-Object { $_.StartsWith('YLFramework.', [StringComparison]::Ordinal) })
            if ($product.Count -ne $expected.Count -or @($expected | Where-Object { -not $seen.Contains($_) }).Count) { throw "Actual $mode ReddotSave compilation membership differs." }
            foreach ($row in $Report.compilation[$mode]) {
                if ($row.name -cin $expected -and $row.sourceFiles.Count -eq 0) { throw 'ReddotSave compilation assembly lacks actual sources.' }
                $all = @($row.name; $row.sourceFiles; $row.assemblyReferences; $row.compiledAssemblyReferences) -join '|'
                if ($all -match '(?i)TestRunner|nunit|Mcp|ReflectorNet|MiniExcel|DOTween|HybridCLR|(?:^|[/\\])(?:Tests|Samples~?)(?:[/\\]|$)' -or
                    ($mode -ceq 'player' -and $all -match '(?i)UnityEditor|YLFramework\.(?:Editor|RedDots\.Editor)|[/\\]Editor[/\\]') -or
                    (-not $save -and $all -match 'YLFramework\.(?:RedDots\.Save|Save)|com\.kano\.ylframework\.save|/Integration/Save/') -or
                    (-not $reddot -and $all -match 'YLFramework\.(?:Runtime|Editor|RedDots)|com\.kano\.ylframework(?:/|\.reddot)')) { throw "Unauthorized ReddotSave $mode compilation source/reference." }
                if ($row.name -ceq 'YLFramework.RedDots' -and ('YLFramework.Runtime' -cnotin $row.assemblyReferences -or
                    ($row.assemblyReferences + $row.compiledAssemblyReferences -join '|') -match 'YLFramework\.(?:RedDots\.Save|Save)')) { throw 'Reddot core compilation must reference only its main dependency, never Save.' }
                if ($row.name -ceq 'YLFramework.RedDots.Save' -and @(@('YLFramework.RedDots', 'YLFramework.Save') | Where-Object { $_ -cnotin $row.assemblyReferences }).Count) { throw 'Save adapter lacks an actual product compilation reference.' }
            }
            continue
        }
        if (@($Roots | Where-Object { $_ -cin @('com.kano.ylframework.excel', 'com.kano.ylframework.gameplay') }).Count -eq 0 -and $Report.entryPackage -cnotin @('com.kano.ylframework.excel', 'com.kano.ylframework.gameplay')) { continue }
        $excel = $mode -ceq 'editor' -and 'com.kano.ylframework.excel' -cin $Roots
        $gameplay = 'com.kano.ylframework.gameplay' -cin $Roots
        foreach ($name in @('YLFramework.Excel.Editor', 'YLFramework.Domain.Excel.Editor')) {
            $want = $excel -and ($name -cne 'YLFramework.Domain.Excel.Editor' -or $gameplay)
            if ($seen.Contains($name) -ne $want) { throw "Actual $mode compilation membership differs: $name" }
        }
        foreach ($name in @('YLFramework.Skill', 'YLFramework.Buff', 'YLFramework.Mission', 'YLFramework.Equipment')) {
            if ($seen.Contains($name) -ne $gameplay) { throw "Actual $mode Gameplay compilation membership differs: $name" }
            if ($gameplay -and @($Report.compilation[$mode] | Where-Object { $_.name -ceq $name -and $_.sourceFiles.Count -gt 0 }).Count -ne 1) { throw 'Gameplay assembly lacks actual compilation sources.' }
        }
        $all = @($Report.compilation[$mode] | ForEach-Object { $_.name; $_.sourceFiles; $_.assemblyReferences; $_.compiledAssemblyReferences }) -join '|'
        if (-not $excel -and $all -match '(?i)MiniExcel|YLFramework\.(?:Excel\.Editor|Domain\.Excel\.Editor)|com\.kano\.ylframework\.excel') { throw "Excel/Domain/MiniExcel leaked into actual $mode compilation sources/references." }
        if (-not $gameplay -and $all -match 'YLFramework\.(?:Skill|Buff|Mission|Equipment|Gameplay)|com\.kano\.ylframework\.gameplay') { throw "Gameplay leaked into actual $mode compilation sources/references." }
        if ($excel) {
            $excelRow = @($Report.compilation[$mode] | Where-Object name -CEQ 'YLFramework.Excel.Editor')[0]
            if ($excelRow.sourceFiles.Count -eq 0 -or ($excelRow.compiledAssemblyReferences -join '|') -notmatch 'MiniExcel') { throw 'Excel compilation lacks actual sources/MiniExcel precompiled reference.' }
        }
        if ($excel -and $gameplay) {
            $adapter = @($Report.compilation[$mode] | Where-Object name -CEQ 'YLFramework.Domain.Excel.Editor')[0]
            foreach ($name in @('YLFramework.Excel.Editor', 'YLFramework.Skill', 'YLFramework.Buff', 'YLFramework.Mission', 'YLFramework.Equipment')) {
                if ($name -cnotin $adapter.assemblyReferences) { throw 'Conditional adapter lacks an actual product compilation reference.' }
            }
            if ($adapter.sourceFiles.Count -eq 0) { throw 'Conditional adapter has no actual compilation sources.' }
        }
    }
}

function Invoke-YLRegistryLifecycle {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object] $Workspace,
        [Parameter(Mandatory)][Collections.IDictionary] $Source,
        [Parameter(Mandatory)][string] $Unity,
        [Parameter(Mandatory)][object] $Editor,
        [Parameter(Mandatory)][string] $EntryPackage,
        [Parameter(Mandatory)][object] $Result,
        [ValidateRange(1, 2147483)][int] $TimeoutSeconds = 900,
        [string[]] $EntryPackages = @($EntryPackage),
        [switch] $VerifyCombinations
    )
    Set-StrictMode -Version 3.0
    if ($Source['packageOrigin'] -cnotin @('Registry', 'LocalTarball')) { throw 'Lifecycle accepts Registry/LocalTarball artifacts only, never File/Git.' }
    $roots = @(Get-YLRegistryEntryPackages -EntryPackage $EntryPackage -EntryPackages $EntryPackages)
    $phases = @(Get-YLRegistryLifecyclePhases -EntryPackage $EntryPackage -EntryPackages $roots -VerifyCombinations:$VerifyCombinations)
    Assert-YLTestWorkspace $Workspace
    $run = Get-YLTestDirectory -Workspace $Workspace -Path $Result.path
    if ($run.kind -cne 'Result' -or $run.id -cne $Result.id) { throw 'Lifecycle requires the existing run Result owner.' }
    $Unity = Get-YLTestPath -Path $Unity -ReadOnly
    $actualEditor = Get-YLTestEditorProfile -Editors @($Editor) -VersionInfo ([Diagnostics.FileVersionInfo]::GetVersionInfo($Unity))
    if ($actualEditor.version -cne $Editor.version) { throw 'Lifecycle Editor profile differs.' }
    $release = Read-YLRegistryRelease -Path $Source.release.path
    if ($release.sha256 -cne $Source.release.sha256 -or $Source.registryUrl -cne 'https://package.openupm.com' -or $Source.registryVersion -cne $release.packages[0].version) { throw 'Lifecycle frozen source/release identity changed.' }
    if ($Source.packageOrigin -ceq 'Registry' -and $release.producer.kind -cne 'GitHubActions') { throw 'Registry lifecycle requires the CI producer receipt.' }
    $original = New-YLRegistryHostManifest -PackageSource $Source.packageOrigin -Release $release -EntryPackage $EntryPackage -EntryPackages $roots -RegistryUrl $Source.registryUrl
    $token = [Guid]::NewGuid().ToString('N')
    $outputRoot = Get-YLTestPath (Join-Path $run.path ('RegistryLifecycle-' + $token))
    if ([IO.Path]::Exists($outputRoot)) { throw 'Preserve an existing lifecycle output.' }
    $null = [IO.Directory]::CreateDirectory($outputRoot)
    $outcome = [ordered]@{ status = 'Failed'; entryPackage = $EntryPackage; entryPackages = $roots; verifyCombinations = [bool]$VerifyCombinations; packageOrigin = $Source.packageOrigin; host = $null; cache = $null
        phases = @(); processes = @(); neutralInputs = $null; sentinels = $null; coldCache = $null; coldInstallCount = 0; error = ''; output = $outputRoot }
    $hostLease = $null; $cacheLease = $null
    try {
        $hostRecord = New-YLTestDirectory -Workspace $Workspace -Kind Host -Key ('RL-' + $token)
        $cache = New-YLTestDirectory -Workspace $Workspace -Kind Cache -Key ('RL-' + $token)
        $hostLease = Lock-YLTestDirectory -Workspace $Workspace -Path $hostRecord.path
        $cacheLease = Lock-YLTestDirectory -Workspace $Workspace -Path $cache.path
        $outcome.host = $hostRecord; $outcome.cache = $cache
        Assert-YLUnityHostIdle $hostRecord
        $catalogPath = Join-Path $Workspace.repository 'Tools/Packaging/PackageCatalog.json'
        $null = Read-YLPackageCatalog -Path $catalogPath
        $identity = Get-YLTestInputIdentity -Repository $Workspace.repository -Paths @('Tools/Testing/RegistryLifecycleEntry.cs', 'Tools/Testing/Common/RegistryLifecycle.ps1', 'Tools/Packaging/PackageCatalog.json',
            'Tools/Testing/Common/Packages.ps1', 'Tools/Testing/Common/Workspace.ps1', 'Tools/Testing/Common/Process.ps1', 'Tools/Testing/Common/Results.ps1', 'Tools/Testing/UnityHost.ps1') -Toolchain @($Unity) -Profile ('RegistryLifecycle-v1-' + $Editor.productVersion + '-' + ($roots -join '+') + '-' + $Source.packageOrigin + '-' + [bool]$VerifyCombinations + '-' + $release.sha256 + '-' + $token)
        $files = [ordered]@{}
        $files['Assets/RegistryLifecycle/Editor/RegistryLifecycleEntry.cs'] = [IO.File]::ReadAllBytes((Join-Path $Workspace.repository 'Tools/Testing/RegistryLifecycleEntry.cs'))
        $asmdef = @{ name = 'RegistryLifecycle.Editor'; references = @(); includePlatforms = @('Editor'); excludePlatforms = @(); allowUnsafeCode = $false
            overrideReferences = $true; precompiledReferences = @(); autoReferenced = $false; defineConstraints = @(); versionDefines = @(); noEngineReferences = $false }
        $files['Assets/RegistryLifecycle/Editor/RegistryLifecycle.asmdef'] = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $asmdef -Depth 8))
        $files['Assets/RegistryLifecycleSentinels/user-state.bytes'] = [byte[]](0, 255, 13, 10, 65, 0, 66)
        $files['Assets/RegistryLifecycleSentinels/generated-state.bytes'] = [Text.Encoding]::UTF8.GetBytes("Neutral lifecycle sentinel. Not an author-tool output.`r`n")
        foreach ($relative in @($files.Keys)) {
            $importer = if ($relative.EndsWith('.cs')) { "MonoImporter:`n  externalObjects: {}`n  serializedVersion: 2`n  defaultReferences: []`n  executionOrder: 0`n  icon: {instanceID: 0}`n" } elseif ($relative.EndsWith('.asmdef')) { "AssemblyDefinitionImporter:`n  externalObjects: {}`n" } else { "TextScriptImporter:`n  externalObjects: {}`n" }
            $files[$relative + '.meta'] = [Text.Encoding]::UTF8.GetBytes("fileFormatVersion: 2`nguid: $([Guid]::NewGuid().ToString('N'))`n" + $importer + "  userData: `n  assetBundleName: `n  assetBundleVariant: `n")
        }
        foreach ($folder in @('Assets/RegistryLifecycle', 'Assets/RegistryLifecycle/Editor', 'Assets/RegistryLifecycleSentinels')) {
            $files[$folder + '.meta'] = [Text.Encoding]::UTF8.GetBytes("fileFormatVersion: 2`nguid: $([Guid]::NewGuid().ToString('N'))`nfolderAsset: yes`nDefaultImporter:`n  externalObjects: {}`n  userData: `n  assetBundleName: `n  assetBundleVariant: `n")
        }
        $files['YLTesting/PackageCatalog.json'] = [IO.File]::ReadAllBytes($catalogPath)
        $files['Packages/manifest.json'] = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $original -Depth 10) + "`n")
        Sync-YLUnityHost -HostRecord $hostRecord -Files $files -Identity $identity -EditorVersion $Editor.version
        $receipt = @{ schemaVersion = 1; purpose = 'YLTesting.RegistryLifecycle.NeutralInputs'; workspaceId = $Workspace.id; hostId = $hostRecord.id; resultId = $run.id; cacheId = $cache.id
            inputHash = $identity.sha256; meaning = 'NeutralBytesAndMetaOnlyNotAuthoringExecution'; files = @(foreach ($relative in $files.Keys) {
                if (-not $relative.StartsWith('Assets/', [StringComparison]::Ordinal)) { continue }
                @{ path = $relative; sha256 = (Get-FileHash -LiteralPath (Join-Path $hostRecord.path $relative) -Algorithm SHA256).Hash.ToLowerInvariant(); ownerId = $hostRecord.id
                    role = $(if ($relative.StartsWith('Assets/RegistryLifecycleSentinels/', [StringComparison]::Ordinal)) { 'Sentinel' } else { 'Probe' }) }
            }) }
        $receiptPath = Join-Path $outputRoot 'neutral-inputs.json'; Write-YLTestJson -Path $receiptPath -Value $receipt
        $outcome.neutralInputs = $receipt
        $before = Join-Path $outputRoot 'manifest.before.json'; [IO.File]::Copy((Join-Path $hostRecord.path 'Packages/manifest.json'), $before)
        $releasePath = Join-Path $outputRoot 'release-manifest.json'; [IO.File]::Copy($release.path, $releasePath)
        $inputReceiptPath = Join-Path $hostRecord.path '.yl-inputs.json'
        $inputReceiptHash = (Get-FileHash -LiteralPath $inputReceiptPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $neutralReceiptHash = (Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $originalManifestHash = (Get-FileHash -LiteralPath $before -Algorithm SHA256).Hash.ToLowerInvariant()
        $bindings = @(@{ path = $inputReceiptPath; sha256 = $inputReceiptHash }, @{ path = $receiptPath; sha256 = $neutralReceiptHash },
            @{ path = $before; sha256 = $originalManifestHash }, @{ path = $releasePath; sha256 = $release.sha256 })
        $previous = $null
        foreach ($phase in $phases) {
            Assert-YLUnityHostIdle $hostRecord
            foreach ($owner in @($hostRecord, $cache, $run)) {
                if ((Get-YLTestDirectory -Workspace $Workspace -Path $owner.path).id -cne $owner.id) { throw 'Lifecycle host/cache/result ownership changed.' }
            }
            $outcome.sentinels = @{ status = 'Checking'; files = @() }
            $outcome.sentinels = Assert-YLRegistryLifecycleInputs -Workspace $Workspace -HostRecord $hostRecord -Receipt $receipt
            $output = Join-Path $outputRoot $phase; $null = [IO.Directory]::CreateDirectory($output)
            $request = @{ schemaVersion = 1; purpose = 'YLTesting.RegistryLifecycle'; requestId = [Guid]::NewGuid().ToString('N'); packageOrigin = $Source.packageOrigin; phase = $phase; verifyCombinations = [bool]$VerifyCombinations
                workspaceRoot = $Workspace.root; workspaceId = $Workspace.id; repository = $Workspace.repository; hostRoot = $hostRecord.path; hostId = $hostRecord.id
                cacheRoot = $cache.path; cacheId = $cache.id; resultRoot = $run.path; resultId = $run.id; lifecycleRoot = $outputRoot; output = $output
                unityExecutable = $Unity; unityVersion = $Editor.version; unityProductVersion = $Editor.productVersion; inputHash = $identity.sha256
                inputReceiptSha256 = $inputReceiptHash
                neutralReceipt = $receiptPath; neutralReceiptSha256 = $neutralReceiptHash
                originalManifest = $before; originalManifestSha256 = $originalManifestHash
                releaseManifest = $releasePath; releaseManifestSha256 = $release.sha256; entryPackage = $EntryPackage; entryPackages = $roots; registryUrl = $Source.registryUrl }
            $requestPath = Join-Path $output 'request.json'; Write-YLTestJson -Path $requestPath -Value $request
            $start = [Diagnostics.ProcessStartInfo]::new($Unity); $start.UseShellExecute = $false; $start.WorkingDirectory = $hostRecord.path
            foreach ($key in @($start.Environment.Keys | Where-Object { $_.StartsWith('GIT_', [StringComparison]::OrdinalIgnoreCase) })) { $null = $start.Environment.Remove($key) }
            $start.Environment['UPM_CACHE_ROOT'] = Join-Path $cache.path 'Upm'
            $start.Environment['UPM_CACHE_PATH'] = Join-Path $cache.path 'Upm/packages'
            $start.Environment['UPM_NPM_CACHE_PATH'] = Join-Path $cache.path 'Upm/npm'
            $start.Environment['UPM_GIT_LFS_CACHE_PATH'] = Join-Path $cache.path 'Lfs'
            if ($phase -ceq 'Inspect') { $outcome.coldCache = Assert-YLRegistryColdCache -StartInfo $start -CacheRoot $cache.path }
            foreach ($argument in @('-batchmode', '-nographics', '-projectPath', $hostRecord.path, '-executeMethod', 'YLFramework.Testing.RegistryLifecycleEntry.Execute',
                '-ylRegistryLifecycleRequest', $requestPath, '-logFile', (Join-Path $output 'unity.log'))) { $start.ArgumentList.Add($argument) }
            $pending = @{ phase = $phase; status = 'Failed'; report = $null; reportPath = (Join-Path $output 'report.json'); process = $null; output = $output; error = '' }
            $outcome.phases += $pending
            $fact = Invoke-YLTestProcess -StartInfo $start -Log (Join-Path $output 'process.log') -TimeoutMilliseconds ([long]$TimeoutSeconds * 1000)
            $outcome.processes += $fact
            $pending.process = $fact
            $phaseResult = Read-YLRegistryLifecyclePhase -Path (Join-Path $output 'report.json') -Request $request -ProcessResult $fact -Release $release -OriginalManifest $original -HostRecord $hostRecord
            foreach ($binding in $bindings) {
                $null = Get-YLTestPath $binding.path
                if ((Get-FileHash -LiteralPath $binding.path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $binding.sha256) { throw 'A frozen lifecycle producer/source receipt changed; do not adopt it for a later phase.' }
            }
            $outcome.phases[-1] = $phaseResult
            if ($phase -ceq 'Inspect') { $outcome.coldInstallCount = 1 }
            if ($phase -cin @('Reopen', 'VerifyRemoved')) {
                if ((ConvertTo-Json -InputObject $previous.graph.packages -Depth 20 -Compress) -cne (ConvertTo-Json -InputObject $phaseResult.graph.packages -Depth 20 -Compress)) { throw 'Warm reopen changed the observed package identity/closure.' }
            }
            $previous = $phaseResult
            $outcome.sentinels = @{ status = 'Checking'; files = @() }
            $outcome.sentinels = Assert-YLRegistryLifecycleInputs -Workspace $Workspace -HostRecord $hostRecord -Receipt $receipt
        }
        $fresh = Get-YLTestInputIdentity -Repository $Workspace.repository -Paths @($identity.files.path) -Toolchain @($Unity) -Profile $identity.profile
        if ($fresh.sha256 -cne $identity.sha256 -or (Get-FileHash -LiteralPath $Source.release.path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $release.sha256) { throw 'Lifecycle source/toolchain/frozen release changed during execution.' }
        $outcome.status = 'Completed'
    } catch {
        $outcome.error = $_.Exception.Message
        if ($null -ne $outcome.sentinels -and $outcome.sentinels.status -ceq 'Checking') { $outcome.sentinels.status = 'ChangedOrUnavailable'; $outcome.sentinels.error = $outcome.error }
        if ($outcome.phases.Count -gt 0) { $outcome.phases[-1].status = 'Failed'; $outcome.phases[-1].error = $outcome.error }
    } finally {
        if ($null -ne $cacheLease) { $cacheLease.Dispose() }
        if ($null -ne $hostLease) { $hostLease.Dispose() }
    }
    return [pscustomobject]$outcome
}
