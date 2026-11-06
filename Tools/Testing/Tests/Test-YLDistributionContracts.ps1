#requires -Version 7.6
# Invoked by Test-YLTesting in its owned sandbox; only the Unity launch and executable metadata are replaced.
$distributionTokens = $null; $distributionErrors = $null
$distributionAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-YLDistribution.ps1'), [ref]$distributionTokens, [ref]$distributionErrors)
Assert-That ($distributionErrors.Count -eq 0) 'Distribution must parse before exercising its contracts.'
$contentFunction = $distributionAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Get-DistributionPackageContent' }, $true)
Assert-That ($null -ne $contentFunction) 'Distribution package content audit must be available.'
. ([scriptblock]::Create($contentFunction.Extent.Text))
. (Join-Path $PSScriptRoot 'PackageCatalogFixtures.ps1')

function Invoke-UpmDistributionFixture {
    param([string] $Fault = '', [string] $Backend = 'Mono', [string] $PackageSource = 'File',
        [string] $EditorVersion = '2022.3.62f3', [hashtable] $Options = @{})
    $root = Join-Path $sandbox ('Distribution-' + [Guid]::NewGuid().ToString('N'))
    $fixtureRepository = Join-Path $root 'Repository'
    $null = [IO.Directory]::CreateDirectory($fixtureRepository)
    foreach ($relative in @('DistributionEntry.cs', 'PackageCatalogContract.cs', 'PlayerSmoke.cs', 'HybridCLREntry.cs', 'HybridCLRSmoke.cs', 'UnityHost.ps1', 'TestSuites.json',
        'Common/Workspace.ps1', 'Common/Process.ps1', 'Common/Results.ps1', 'Common/Packages.ps1', 'Tests/Test-YLDistribution.ps1')) {
        $target = Join-Path $fixtureRepository "Tools/Testing/$relative"
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        [IO.File]::Copy((Join-Path $PSScriptRoot "../$relative"), $target)
    }
    $files = [ordered]@{
        'Packages/manifest.json' = '{"dependencies":{"com.unity.ugui":"2.0.0","com.unity.modules.jsonserialize":"1.0.0","com.unity.modules.accessibility":"1.0.0"}}'
        'Packages/packages-lock.json' = '{"dependencies":{"com.unity.nuget.newtonsoft-json":{"version":"3.2.2"}}}'
        'Tools/Testing/Profiles/Unity2022/manifest.json' = '{"dependencies":{"com.unity.ugui":"1.0.0","com.unity.modules.jsonserialize":"1.0.0"}}'
        'Tools/Testing/Profiles/Unity2022/packages-lock.json' = '{"dependencies":{"com.unity.nuget.newtonsoft-json":{"version":"3.2.1"}}}'
        'Unity.exe' = 'editor-metadata-fixture'
    }
    $ids = @('com.kano.ylframework', 'com.kano.ylframework.data-assets', 'com.kano.ylframework.excel', 'com.kano.ylframework.gameplay', 'com.kano.ylframework.hybridclr')
    foreach ($id in $ids) {
        $dependencies = Get-YLFixtureInternalDependencies -PackageId $id -Version '0.1.0-preview.1'
        $dependencies['com.unity.nuget.newtonsoft-json'] = '3.2.1'
        $files["PackageSources/$id/package.json"] = ConvertTo-Json -Depth 8 -InputObject @{ name = $id; version = '0.1.0-preview.1'; dependencies = $dependencies }
        $files["PackageSources/$id/LICENSE.md"] = "MIT License`nCopyright fixture`nPermission is hereby granted, free of charge`n"
        $folder = if ($id -ceq 'com.kano.ylframework.excel') { 'Editor' } else { 'Runtime' }
        $files["PackageSources/$id/$folder/Product.cs"] = "class Fixture$($id.Replace('.', '').Replace('-', '')) {}"
        $files["PackageSources/$id/$folder/Product.cs.meta"] = "guid: $([Guid]::NewGuid().ToString('N'))`n"
    }
    foreach ($entry in $files.GetEnumerator()) {
        $target = Join-Path $fixtureRepository $entry.Key
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        [IO.File]::WriteAllText($target, $entry.Value, [Text.UTF8Encoding]::new($false))
    }
    $catalogTarget = Join-Path $fixtureRepository 'Tools/Packaging/PackageCatalog.json'
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($catalogTarget))
    [IO.File]::WriteAllText($catalogTarget, (ConvertTo-Json -InputObject (Get-YLFixturePackageCatalog) -Depth 30), [Text.UTF8Encoding]::new($false))
    switch -CaseSensitive ($Fault) {
        'lfs' { [IO.File]::WriteAllText((Join-Path $fixtureRepository 'PackageSources/com.kano.ylframework/Runtime/Product.cs'), "version https://git-lfs.github.com/spec/v1`noid sha256:$('a' * 64)`nsize 999`n") }
        'missing-license' { [IO.File]::Delete((Join-Path $fixtureRepository 'PackageSources/com.kano.ylframework/LICENSE.md')) }
        'host-content' { $null = [IO.Directory]::CreateDirectory((Join-Path $fixtureRepository 'PackageSources/com.kano.ylframework/Settings')); [IO.File]::WriteAllText((Join-Path $fixtureRepository 'PackageSources/com.kano.ylframework/Settings/Host.json'), '{}') }
        'missing-target-packages' { [IO.File]::Delete((Join-Path $fixtureRepository 'Tools/Testing/Profiles/Unity2022/packages-lock.json')) }
    }
    $configuration = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../TestSuites.json')) | ConvertFrom-Json -DateKind String
    $fixtureEditor = @($configuration.editors | Where-Object version -CEQ $EditorVersion)[0]
    $fixtureVersion = [pscustomobject]@{ FileMajorPart = [int]$EditorVersion.Split('.')[0]; FileMinorPart = 3; FileBuildPart = $(if ($EditorVersion.StartsWith('2022')) { 62 } else { 27 }); ProductVersion = $fixtureEditor.productVersion }
    if ($Fault -ceq 'unknown-target') { $fixtureVersion.ProductVersion = '2022.3.62f3_unknown' }
    $state = @{ calls = [Collections.Generic.List[object]]::new(); resultRoot = $null; exitCode = 0; root = $root }
    $realProcess = (Get-Command Invoke-YLTestProcess).ScriptBlock
    function Invoke-YLTestProcess([Diagnostics.ProcessStartInfo] $StartInfo, [string] $Log, [long] $TimeoutMilliseconds) {
        if ([IO.Path]::GetFileNameWithoutExtension($StartInfo.FileName) -ceq 'git') {
            foreach ($key in @('GIT_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE', 'GIT_COMMON_DIR', 'GIT_CONFIG_COUNT', 'GIT_OBJECT_DIRECTORY', 'GIT_ALTERNATE_OBJECT_DIRECTORIES')) {
                if ($StartInfo.Environment.ContainsKey($key)) { throw "Inherited Git context could escape fixture ownership: $key" }
            }
            return & $realProcess -StartInfo $StartInfo -Log $Log -TimeoutMilliseconds $TimeoutMilliseconds
        }
        $output = [IO.Path]::GetDirectoryName($Log)
        $phase = [IO.Path]::GetFileName($output)
        $fact = [pscustomobject]@{ status = 'Exited'; observedExit = $true; exitCode = 0; timedOut = $false; cancelled = $false
            forcedCleanup = $false; observerError = $null; cleanupError = $null; jobAssigned = $true; activeProcesses = 0; streamsDrained = $true; pid = 42 }
        $call = @{ phase = $phase; executable = $StartInfo.FileName; arguments = @($StartInfo.ArgumentList); timeout = $TimeoutMilliseconds; environment = $StartInfo.Environment }
        $state.calls.Add($call)
        if ($phase -cin @('PlayerRun', 'PlayerRepeat')) {
            $request = [IO.File]::ReadAllText((Join-Path $state.resultRoot 'Build/request.json')) | ConvertFrom-Json -DateKind String
            Write-YLTestJson -Path (Join-Path $output 'result.json') -Value @{
                schemaVersion = 1; purpose = 'YLTesting.PlayerSmoke'; status = 'Passed'; resultId = $request.resultId; hostId = $request.hostId
                unityVersion = $EditorVersion; pid = 42; isEditor = $false; platform = 'WindowsPlayer'; frames = 2; error = ''; backend = $Backend; recordCount = 0; leaseCount = 0
                stages = @('Started', 'ResourceLoaded', 'ReddotLifecycle', 'BusyOwnerProtected', 'ResourceReleased', 'Shutdown')
            }
            [IO.File]::WriteAllText((Join-Path $output 'player.log'), ('[YLPlayerSmoke] PASS ' + $request.resultId))
            return $fact
        }
        $request = [IO.File]::ReadAllText((Join-Path $output 'request.json')) | ConvertFrom-Json -DateKind String
        $state.resultRoot = $request.resultRoot
        $project = $StartInfo.ArgumentList[$StartInfo.ArgumentList.IndexOf('-projectPath') + 1]
        $manifest = [IO.File]::ReadAllText((Join-Path $project 'Packages/manifest.json')) | ConvertFrom-Json -AsHashtable -DateKind String
        $call['request'] = $request; $call['manifest'] = $manifest; $call['project'] = $project
        $packages = @(); $locked = @{}; $resolved = @()
        foreach ($id in @($manifest.dependencies.Keys | Where-Object { $_.StartsWith('com.kano.', [StringComparison]::Ordinal) } | Sort-Object)) {
            $locator = $manifest.dependencies[$id]
            $source = if ($locator.StartsWith('file:', [StringComparison]::Ordinal)) { 'Local' } else { 'Git' }
            if ($source -ceq 'Local') { $resolvedPath = $locator.Substring(5).Replace('/', '\') }
            else {
                $resolvedPath = Join-Path $project "Library/PackageCache/$id"
                if (-not [IO.Directory]::Exists($resolvedPath)) {
                    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedPath))
                    if ($request.packageOrigin -ceq 'GitFixture') {
                        $uri = [Uri]($locator.Substring(4).Split('?')[0])
                        [IO.Directory]::CreateDirectory($resolvedPath) | Out-Null
                        foreach ($file in @(Get-YLTestTreeFiles (Join-Path $uri.LocalPath "PackageSources/$id"))) {
                            $destination = Join-Path $resolvedPath ([IO.Path]::GetRelativePath((Join-Path $uri.LocalPath "PackageSources/$id"), $file))
                            $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)); [IO.File]::Copy($file, $destination)
                        }
                    } else {
                        # A simulated remote has deliberately different content; the controller must not substitute local PackageSources.
                        $null = [IO.Directory]::CreateDirectory($resolvedPath)
                        [IO.File]::WriteAllText((Join-Path $resolvedPath 'package.json'), (ConvertTo-Json -InputObject @{ name = $id; version = '0.1.0-preview.1'; dependencies = @{} }))
                        [IO.File]::WriteAllText((Join-Path $resolvedPath 'LICENSE.md'), 'MIT License remote fixture')
                        [IO.File]::WriteAllText((Join-Path $resolvedPath 'Remote.cs'), 'class RemoteContent {}')
                    }
                }
            }
            $hash = Get-DistributionPackageContent -Root $resolvedPath
            $revision = if ($source -ceq 'Git') { $locator.Split('#')[-1] } else { $null }
            $packages += @{ name = $id; version = '0.1.0-preview.1'; source = $source; assetPath = "Packages/$id"; resolvedPath = $resolvedPath; contentSha256 = $hash.sha256; revision = $revision; packageId = $id + '@' + $locator }
            $locked[$id] = @{ version = $locator; source = $source.ToLowerInvariant(); hash = $revision; dependencies = @{} }
            $resolved += @{ name = $id; version = '0.1.0-preview.1'; source = $source }
        }
        foreach ($entry in $manifest.dependencies.GetEnumerator() | Where-Object { -not $_.Key.StartsWith('com.kano.', [StringComparison]::Ordinal) }) {
            $source = if ($entry.Key.StartsWith('com.unity.modules.', [StringComparison]::Ordinal)) { 'BuiltIn' } else { 'Registry' }
            $resolved += @{ name = $entry.Key; version = $entry.Value; source = $source }
            $locked[$entry.Key] = @{ version = $entry.Value; source = $source.ToLowerInvariant(); dependencies = @{} }
        }
        [IO.File]::WriteAllText((Join-Path $project 'Packages/packages-lock.json'), (ConvertTo-Json -Depth 15 -InputObject @{ dependencies = $locked }))
        $result = @{ status = 'Passed' }
        if ($phase -ceq 'Build') {
            $executable = Join-Path $output 'Player/YLDistribution.exe'; $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($executable))
            [IO.File]::WriteAllText($executable, 'player-artifact-fixture')
            $result = @{ status = 'Passed'; backend = $Backend; executable = $executable }
            if ($Fault -like 'prerequisite-*') { $result.status = 'PrerequisiteNotMet'; $fact.status = 'NonZeroExit'; $fact.exitCode = 2 }
            switch -CaseSensitive ($Fault) {
                'prerequisite-timeout' { $fact.timedOut = $true }
                'prerequisite-cancelled' { $fact.cancelled = $true }
                'prerequisite-observer' { $fact.observerError = 'lost' }
                'prerequisite-cleanup' { $fact.cleanupError = 'incomplete' }
                'prerequisite-streams' { $fact.streamsDrained = $false }
                'target-toolchain' { [IO.File]::AppendAllText((Join-Path $fixtureRepository 'Unity.exe'), 'changed') }
                'package-build' { [IO.File]::AppendAllText((Join-Path $packages[0].resolvedPath 'LICENSE.md'), 'changed') }
            }
        }
        $report = @{ schemaVersion = 2; hostId = $request.hostId; resultId = $request.resultId; unityVersion = $EditorVersion; phase = $phase; pid = 42
            packageOrigin = $request.packageOrigin; entryPackage = $request.entryPackage; inputHash = $request.inputHash; catalogSha256 = $request.catalogSha256
            packages = $packages; resolvedDependencies = $resolved; result = $result }
        if ($phase -ceq 'Inspect') {
            switch -CaseSensitive ($Fault) {
                'source-version' { $report.unityVersion = 'wrong-editor' }
                'wrong-origin' { $report.packageOrigin = 'File' }
                'wrong-source' { $packages[0].source = 'Registry' }
                'wrong-dependency-source' { $resolved[0].source = 'Registry' }
                'wrong-revision' { $packages[0].revision = 'f' * 40 }
                'wrong-package-id' { $packages[0].packageId = $packages[0].name + '@file:/unrelated-source' }
                'wrong-name' { $packages[0].name = 'com.foreign.product' }
                'wrong-version' { $packages[0].version = '1.0.0' }
                'wrong-hash' { $packages[0].contentSha256 = 'f' * 64 }
                'escape-path' { $packages[0].resolvedPath = $fixtureRepository }
                'missing-package' { $report.packages = @() }
                'duplicate-package' { $report.packages += $packages[0] }
                'missing-dependency' { $report.resolvedDependencies = @() }
                'old-report' { $report.schemaVersion = 1 }
                'stale-report' { $report.resultId = '0' * 32 }
                'wrong-pid' { $report.pid = 43 }
                'timeout' { $fact.status = 'TimedOut'; $fact.timedOut = $true; $fact.forcedCleanup = $true }
                'compile-failed' { $fact.status = 'NonZeroExit'; $fact.exitCode = 1 }
                'source-manifest-drift' { [IO.File]::AppendAllText((Join-Path $fixtureRepository 'Packages/manifest.json'), ' ') }
                'target-manifest-drift' { [IO.File]::AppendAllText((Join-Path $fixtureRepository 'Tools/Testing/Profiles/Unity2022/manifest.json'), ' ') }
                'source-content-drift' { [IO.File]::AppendAllText((Join-Path $fixtureRepository 'PackageSources/com.kano.ylframework/LICENSE.md'), 'changed') }
                'catalog-drift' { [IO.File]::AppendAllText($catalogTarget, "`n") }
                'owner-drift' { $ownerPath = Join-Path $project '.yl-owner.json'; $owner = [IO.File]::ReadAllText($ownerPath) | ConvertFrom-Json -AsHashtable; $owner.id = $true; [IO.File]::WriteAllText($ownerPath, (ConvertTo-Json $owner)) }
            }
            foreach ($field in @('schemaVersion', 'hostId', 'resultId', 'unityVersion', 'phase', 'pid', 'packageOrigin', 'entryPackage', 'inputHash', 'catalogSha256')) { if ($Fault -ceq "boolean-$field") { $report[$field] = $true } }
            foreach ($field in @('name', 'version', 'source', 'assetPath', 'resolvedPath', 'contentSha256', 'revision', 'packageId')) { if ($Fault -ceq "boolean-package-$field") { $packages[0][$field] = $true } }
            foreach ($field in @('name', 'version', 'source')) { if ($Fault -ceq "boolean-dependency-$field") { $resolved[0][$field] = $true } }
            if ($Fault -ceq 'boolean-status') { $result.status = $true }
        }
        if ($phase -ceq 'Build' -and $Fault -ceq 'prerequisite-source-version') { $report.unityVersion = 'wrong-editor' }
        if ($Fault -cne 'missing-report') { Write-YLTestJson -Path (Join-Path $output 'report.json') -Value $report }
        return $fact
    }
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-YLDistribution.ps1'), [ref]$tokens, [ref]$errors)
    Assert-That ($errors.Count -eq 0) 'Distribution script must parse.'
    $statements = foreach ($statement in $ast.EndBlock.Statements) {
        if ($statement -is [Management.Automation.Language.PipelineAst] -and $statement.PipelineElements[0] -is [Management.Automation.Language.CommandAst] -and
            $statement.PipelineElements[0].InvocationOperator -eq [Management.Automation.Language.TokenKind]::Dot) { continue }
        if ($statement -is [Management.Automation.Language.AssignmentStatementAst]) {
            switch -CaseSensitive ($statement.Left.Extent.Text) {
                '$repository' { '$repository = $fixtureRepository'; continue }
                '$configuration' { continue }
                '$version' { '$version = $fixtureVersion'; continue }
                default { $statement.Extent.Text }
            }
        } else {
            $exit = $statement.Find({ param($node) $node -is [Management.Automation.Language.ExitStatementAst] }, $true)
            if ($null -ne $exit) { $statement.Extent.Text.Replace($exit.Extent.Text, '$state.exitCode = 2') } else { $statement.Extent.Text }
        }
    }
    $controller = [scriptblock]::Create($ast.ParamBlock.Extent.Text + "`n" + ($statements -join "`n"))
    $arguments = @{ WorkspaceRoot = (Join-Path $root 'Workspace'); Unity = (Join-Path $fixtureRepository 'Unity.exe'); RunPlayer = $true; TimeoutSeconds = 123; PlayerTimeoutSeconds = 17; Backend = $Backend; PackageSource = $PackageSource }
    foreach ($entry in $Options.GetEnumerator()) { $arguments[$entry.Key] = $entry.Value }
    try { $null = & $controller @arguments }
    catch {
        if ($Fault -and $Fault -cnotin @('unknown-target', 'missing-target-packages', 'lfs', 'missing-license', 'host-content')) {
            Assert-That ($state.calls.Count -gt 0) "The $Fault contract failed before reaching its actual process/report boundary."
        }
        if ($Fault -ceq 'unknown-target' -or $Options.Count -gt 0 -and $state.calls.Count -eq 0) {
            Assert-That (-not [IO.Path]::Exists($arguments.WorkspaceRoot)) 'Invalid CLI inputs were rejected only after workspace creation.'
        }
        throw
    }
    $summary = [IO.File]::ReadAllText((Join-Path $state.resultRoot 'summary.json')) | ConvertFrom-Json -DateKind String
    return [pscustomobject]@{ summary = $summary; calls = $state.calls.ToArray(); root = $root; exitCode = $state.exitCode }
}

Test-Case 'UPM Distribution uses one exact Editor and manifest without Export or importPackage for both backends' {
    foreach ($editor in @('2022.3.62f3', '6000.3.27f1')) {
        foreach ($backend in @('Mono', 'IL2CPP')) {
            $fixture = Invoke-UpmDistributionFixture -EditorVersion $editor -Backend $backend
            Assert-That ($fixture.summary.status -ceq 'Passed' -and $fixture.summary.playerRuns.Count -eq 2) 'UPM inspection/build and repeat Player runs did not close.'
            Assert-That (($fixture.calls.phase -join ',') -ceq 'Inspect,Build,PlayerRun,PlayerRepeat') 'Distribution retained a legacy Export/Import stage.'
            Assert-That ($fixture.summary.packages.Count -eq 1 -and $fixture.summary.packages[0].source -ceq 'Local') 'The actual selected package graph was lost.'
            foreach ($call in $fixture.calls | Where-Object phase -CIn @('Inspect', 'Build')) {
                Assert-That ($call.request.schemaVersion -eq 2 -and $call.request.unityVersion -ceq $editor -and $call.timeout -eq 123000) 'Editor protocol identity or timeout changed.'
                Assert-That ($call.arguments -cnotcontains '-importPackage') 'The controller invoked retired asset import.'
                Assert-That ($call.manifest.dependencies['com.unity.ugui'] -ceq $(if ($editor.StartsWith('2022')) { '1.0.0' } else { '2.0.0' })) 'The exact target UGUI profile was lost.'
                Assert-That (-not [IO.Directory]::Exists((Join-Path $call.project 'Assets/YLFramework'))) 'Product Runtime was copied into Assets instead of installed with UPM.'
                Assert-That ($call.manifest.dependencies['com.kano.ylframework'].StartsWith('file:', [StringComparison]::Ordinal)) 'File install has no explicit UPM locator.'
            }
            foreach ($call in $fixture.calls | Where-Object phase -CIn @('PlayerRun', 'PlayerRepeat')) { Assert-That ($call.timeout -eq 17000) 'Player deadline changed.' }
        }
    }
}

Test-Case 'UPM Distribution defaults to an owned Git fixture containing only approved package roots' {
    $binding = [scriptblock]::Create($distributionAst.ParamBlock.Extent.Text + "`n" + '$PackageSource')
    Assert-That ((& $binding -WorkspaceRoot $sandbox -Unity 'MetadataOnly.exe') -ceq 'Git') 'Omitting PackageSource must select Git, not File.'
    $fixture = Invoke-UpmDistributionFixture -PackageSource Git
    Assert-That ($fixture.summary.packageOrigin -ceq 'GitFixture' -and $fixture.summary.packages[0].source -ceq 'Git' -and $fixture.summary.packages[0].revision -cmatch '^[0-9a-f]{40}$') 'The Git fixture did not bind a full commit.'
    $locator = $fixture.calls[0].manifest.dependencies['com.kano.ylframework']
    Assert-That ($locator -cmatch '^git\+file:///.+\?path=/PackageSources/com\.kano\.ylframework#[0-9a-f]{40}$') 'The fixture was not installed as a pinned UPM Git subdirectory.'
    $fixtureRoot = ([Uri]$locator.Substring(4).Split('?')[0]).LocalPath
    $entries = @([IO.Directory]::EnumerateFileSystemEntries($fixtureRoot) | ForEach-Object { [IO.Path]::GetFileName($_) })
    Assert-That ($entries.Count -eq 2 -and $entries -ccontains '.git' -and $entries -ccontains 'PackageSources') 'The fixture includes host/SDK/MCP inputs.'
}

Test-Case 'UPM remote Git requests keep their URL and full SHA and cannot substitute local sources' {
    $fixture = Invoke-UpmDistributionFixture -PackageSource Git -Options @{ GitRepository = 'https://example.invalid/yl.git'; GitRevision = ('a' * 40) }
    Assert-That ($fixture.summary.packageOrigin -ceq 'GitRemote' -and $fixture.summary.packages[0].revision -ceq ('a' * 40)) 'A remote request became local fixture evidence.'
    Assert-That ($fixture.calls[0].manifest.dependencies['com.kano.ylframework'] -ceq ('https://example.invalid/yl.git?path=/PackageSources/com.kano.ylframework#' + ('a' * 40))) 'Remote locator changed or lost the requested SHA.'
    Assert-That (@($fixture.summary.inputIdentity.files.path | Where-Object { $_.StartsWith('PackageSources/') }).Count -eq 0) 'Remote verification used local product source as its identity.'
    Assert-That ($fixture.calls[0].environment['UPM_CACHE_ROOT'].StartsWith($fixture.root) -and $fixture.calls[0].environment['UPM_GIT_LFS_CACHE_PATH'].StartsWith($fixture.root)) 'A cold remote run inherited the user UPM/LFS cache.'
}

Test-Case 'Owned Git fixtures do not inherit caller worktree, index or configuration overrides' {
    $keys = @('GIT_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE', 'GIT_COMMON_DIR', 'GIT_CONFIG_COUNT', 'GIT_OBJECT_DIRECTORY', 'GIT_ALTERNATE_OBJECT_DIRECTORIES')
    $saved = @{}
    try {
        foreach ($key in $keys) {
            $saved[$key] = [Environment]::GetEnvironmentVariable($key)
            [Environment]::SetEnvironmentVariable($key, (Join-Path $sandbox ('untrusted-' + $key)))
        }
        $fixture = Invoke-UpmDistributionFixture -PackageSource Git
        Assert-That ($fixture.summary.status -ceq 'Passed') 'Safe fixture execution did not close.'
    } finally {
        foreach ($key in $keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
    }
}

Test-Case 'The actual Git resolver chooses one PATH-preferred executable when several applications exist' {
    $oldPath = $env:PATH
    $directories = @(1..3 | ForEach-Object {
        $directory = Join-Path $sandbox ('git-candidate-' + $_)
        $null = [IO.Directory]::CreateDirectory($directory)
        [IO.File]::WriteAllBytes((Join-Path $directory 'git.exe'), [byte[]]@(77, 90))
        $directory
    })
    try {
        $env:PATH = ($directories -join [IO.Path]::PathSeparator) + [IO.Path]::PathSeparator + $oldPath
        $resolver = $distributionAst.Find({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.Extent.Text -cmatch '^Get-Command git -CommandType Application' }, $true)
        Assert-That ($null -ne $resolver) 'The maintained controller does not expose its actual executable selection.'
        $observed = @(& ([scriptblock]::Create($resolver.Extent.Text)))
        Assert-That ($observed.Count -eq 1 -and $observed[0].Source -ceq (Join-Path $directories[0] 'git.exe')) 'The actual resolver combined multiple executable paths or ignored PATH priority.'
    } finally { $env:PATH = $oldPath }
}

foreach ($options in @(
    @{ GitRepository = 'https://example.invalid/yl.git' }, @{ GitRevision = ('a' * 40) },
    @{ GitRepository = 'https://example.invalid/yl.git'; GitRevision = 'main' },
    @{ GitRepository = 'https://example.invalid/yl.git?path=/Other'; GitRevision = ('a' * 40) },
    @{ GitRepository = 'https://example.invalid/yl.git#main'; GitRevision = ('a' * 40) },
    @{ GitRepository = 'file:///C:/Local.git'; GitRevision = ('a' * 40) }, @{ ImportUnity = 'legacy.exe' }
)) {
    Test-Case ('UPM Distribution rejects invalid Git or retired CLI input: ' + ($options.Keys -join ',')) {
        Assert-Rejected { Invoke-UpmDistributionFixture -PackageSource Git -Options $options } 'Git|ImportUnity|parameter'
    }
}
Test-Case 'UPM File refuses Git arguments instead of ignoring them' {
    Assert-Rejected { Invoke-UpmDistributionFixture -Options @{ GitRepository = 'https://example.invalid/yl.git'; GitRevision = ('a' * 40) } } 'File|Git'
}

foreach ($fault in @('source-version', 'boolean-schemaVersion', 'boolean-hostId', 'boolean-resultId', 'boolean-unityVersion', 'boolean-phase', 'boolean-status', 'boolean-pid', 'boolean-packageOrigin', 'boolean-entryPackage', 'boolean-inputHash', 'boolean-catalogSha256',
    'boolean-package-name', 'boolean-package-version', 'boolean-package-source', 'boolean-package-assetPath', 'boolean-package-resolvedPath', 'boolean-package-contentSha256', 'boolean-package-packageId',
    'boolean-dependency-name', 'boolean-dependency-version', 'boolean-dependency-source', 'wrong-name', 'wrong-version', 'wrong-source', 'wrong-hash', 'escape-path', 'missing-package', 'duplicate-package',
    'missing-dependency', 'wrong-dependency-source', 'old-report', 'stale-report', 'wrong-pid', 'missing-report', 'owner-drift', 'timeout', 'compile-failed', 'package-build', 'target-toolchain', 'source-content-drift', 'catalog-drift',
    'prerequisite-source-version', 'prerequisite-timeout', 'prerequisite-cancelled', 'prerequisite-observer', 'prerequisite-cleanup', 'prerequisite-streams', 'target-manifest-drift')) {
    Test-Case "UPM Distribution rejects $fault through the real controller" {
        Assert-Rejected { Invoke-UpmDistributionFixture -Fault $fault } 'identity|type|match|process|exit|package|Package|inputs changed|report.json|owner|ownership|dependency.*(?:missing|strings)'
    }
}
foreach ($fault in @('wrong-origin', 'wrong-revision', 'boolean-package-revision', 'wrong-package-id')) {
    Test-Case "UPM remote Distribution rejects $fault without a local fallback" {
        Assert-Rejected { Invoke-UpmDistributionFixture -PackageSource Git -Fault $fault -Options @{ GitRepository = 'https://example.invalid/yl.git'; GitRevision = ('a' * 40) } } 'identity|type|match|package|Package|origin|revision'
    }
}
foreach ($fault in @('lfs', 'missing-license', 'host-content')) {
    Test-Case "UPM Distribution audits $fault before accepting File contents" {
        Assert-Rejected { Invoke-UpmDistributionFixture -Fault $fault } 'LFS|license|LICENSE|content|package|Package'
    }
}
Test-Case 'UPM Distribution preserves real prerequisite exit 2 without Player runs' {
    $fixture = Invoke-UpmDistributionFixture -Fault 'prerequisite-valid'
    Assert-That ($fixture.summary.status -ceq 'PrerequisiteNotMet' -and $fixture.exitCode -eq 2 -and ($fixture.calls.phase -join ',') -ceq 'Inspect,Build') 'A genuine prerequisite result became a pass or launched a Player.'
}
Test-Case 'UPM Distribution rejects unknown Editors and missing profile locks without fallback' {
    Assert-Rejected { Invoke-UpmDistributionFixture -Fault 'unknown-target' } 'Unsupported|ambiguous'
    Assert-Rejected { Invoke-UpmDistributionFixture -Fault 'missing-target-packages' } 'packages-lock.json'
}

Test-Case 'UPM multi-package licenses, metadata and complete Editor dependency closure replace the retired Equipment archive contract' {
    $fixture = Invoke-UpmDistributionFixture
    $source = Join-Path $fixture.root 'Repository'
    $roots = @('com.kano.ylframework.excel', 'com.kano.ylframework.gameplay')
    $packages = @(Get-YLUpmPackages -Repository $source -Ids $roots)
    Assert-That (($packages.id -join ',') -ceq 'com.kano.ylframework,com.kano.ylframework.data-assets,com.kano.ylframework.excel,com.kano.ylframework.gameplay') 'ExcelGameplay must select exactly the main/DataAssets/Excel/Gameplay closure from its two roots.'
    $excelOnly = @(Get-YLUpmPackages -Repository $source -Ids @('com.kano.ylframework.excel'))
    Assert-That (($excelOnly.id -join ',') -ceq 'com.kano.ylframework,com.kano.ylframework.data-assets,com.kano.ylframework.excel') 'Ordinary Excel must not select Gameplay.'
    $excel = @($packages | Where-Object id -CEQ 'com.kano.ylframework.excel')[0]
    $gameplay = @($packages | Where-Object id -CEQ 'com.kano.ylframework.gameplay')[0]
    Assert-That ((@($excel.manifest.dependencies.Keys | Where-Object { $_.StartsWith('com.kano.', [StringComparison]::Ordinal) } | Sort-Object) -join ',') -ceq 'com.kano.ylframework,com.kano.ylframework.data-assets' -and
        (@($gameplay.manifest.dependencies.Keys | Where-Object { $_.StartsWith('com.kano.', [StringComparison]::Ordinal) }) -join ',') -ceq 'com.kano.ylframework') 'The composite fixture must not turn Excel into a Gameplay-dependent package.'
    foreach ($package in $packages) {
        $original = Get-DistributionPackageContent -Root $package.root
        foreach ($relative in @('LICENSE.md', $(if ($package.id -ceq 'com.kano.ylframework') { 'Runtime/Product.cs.meta' } elseif ($package.id -ceq 'com.kano.ylframework.excel') { 'Editor/Product.cs' } else { 'Runtime/Product.cs' }))) {
            $path = Join-Path $package.root $relative; $bytes = [IO.File]::ReadAllBytes($path)
            try {
                [IO.File]::AppendAllText($path, 'changed exact input bytes')
                Assert-That ((Get-DistributionPackageContent -Root $package.root).sha256 -cne $original.sha256) "Package content identity ignored $($package.id)/$relative."
            } finally { [IO.File]::WriteAllBytes($path, $bytes) }
        }
        $license = Join-Path $package.root 'LICENSE.md'; $bytes = [IO.File]::ReadAllBytes($license)
        try { [IO.File]::Delete($license); Assert-Rejected { Get-DistributionPackageContent -Root $package.root } 'LICENSE' }
        finally { [IO.File]::WriteAllBytes($license, $bytes) }
    }
    $manifest = Join-Path $source 'PackageSources/com.kano.ylframework.excel/package.json'
    $document = [IO.File]::ReadAllText($manifest) | ConvertFrom-Json -AsHashtable; $document.dependencies['com.kano.ylframework.data-assets'] = '0.0.1'
    [IO.File]::WriteAllText($manifest, (ConvertTo-Json -InputObject $document -Depth 8))
    Assert-Rejected { Get-YLUpmPackages -Repository $source -Ids $roots } 'Mismatched internal package version'
}

Test-Case 'All catalog UPM roots satisfy exact versions, materialized content and the product-only license boundary' {
    $source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
    $ids = @((Read-YLPackageCatalog).packages.id)
    $packages = @(Get-YLUpmPackages -Repository $source -Ids $ids)
    Assert-That ($packages.Count -eq $ids.Count) 'The maintained product package graph contains unexpected or missing package roots.'
    $version = @($packages | Where-Object id -CEQ 'com.kano.ylframework')[0].version
    foreach ($package in $packages) {
        Assert-That ($package.version -ceq $version) 'A maintained package has a different distribution version.'
        $content = Get-DistributionPackageContent -Root $package.root
        Assert-That ($content.sha256 -cmatch '^[0-9a-f]{64}$' -and $content.files.Count -gt 2) "Maintained package contents could not be audited: $($package.id)"
    }
}
