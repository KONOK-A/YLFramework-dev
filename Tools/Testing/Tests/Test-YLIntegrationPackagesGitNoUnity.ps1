#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param([Parameter(Mandatory)][string] $TemporaryRoot)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$entry = Join-Path $PSScriptRoot 'Test-YLIntegrationPackages.ps1'
. $entry -WorkspaceRoot $TemporaryRoot -Unity 'C:\unused\Unity.exe' -Integration Excel
. (Join-Path $PSScriptRoot 'PackageCatalogFixtures.ps1')
$sandbox = Join-Path (Get-YLTestPath $TemporaryRoot) ('yl-integration-git-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($sandbox)
$checks = 0
function Check([bool] $Condition, [string] $Reason) { if (-not $Condition) { throw $Reason }; $script:checks++ }
function Reject([scriptblock] $Action, [string] $Reason) {
    try { & $Action | Out-Null } catch {
        if (-not $_.Exception.Message.Contains($Reason, [StringComparison]::OrdinalIgnoreCase)) { throw }
        $script:checks++; return
    }
    throw "Expected refusal containing: $Reason"
}
function Save([string] $Path, [string] $Text) {
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}
try {
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($entry, [ref]$tokens, [ref]$errors)
    Check ($errors.Count -eq 0) 'The integration controller must parse.'
    $binding = [scriptblock]::Create($ast.ParamBlock.Extent.Text + "`n" + '(Get-Variable PackageSource -ValueOnly -ErrorAction SilentlyContinue)')
    Check ((& $binding -WorkspaceRoot $sandbox -Unity 'Unused.exe' -Integration Excel) -ceq 'File') 'Omitting PackageSource must preserve File, not silently choose Git.'
    Check ((& $binding -WorkspaceRoot $sandbox -Unity 'Unused.exe' -PackageId 'com.kano.ylframework') -ceq 'File') 'PackageId must be an explicit minimal-install entry.'
    foreach ($options in @(@{ Integration = 'Excel'; PackageId = 'com.kano.ylframework' },
        @{ PackageId = 'com.kano.ylframework'; DOTweenSdkMode = 'Required' }, @{ PackageId = 'com.kano.ylframework'; DOTweenArchive = 'unused.zip' })) {
        $refused = $false
        try { $null = & $binding -WorkspaceRoot $sandbox -Unity 'Unused.exe' @options }
        catch [Management.Automation.ParameterBindingException] { $refused = $true }
        Check $refused 'PackageId/Integration and SDK-only options must be mutually exclusive at parameter binding.'
    }
    $unusedPackageWorkspace = Join-Path $sandbox 'UnknownPackageMustNotBeCreated'
    Reject { & $entry -WorkspaceRoot $unusedPackageWorkspace -Unity 'C:\not-an-editor\Unity.exe' -PackageId 'com.kano.ylframework.unknown' } 'Unknown package'
    Check (-not [IO.Path]::Exists($unusedPackageWorkspace)) 'An unknown package reached workspace or Editor preparation.'
    $sha = '0123456789abcdef0123456789abcdef01234567'
    foreach ($repo in @('https://example.invalid/product.git', 'http://example.invalid/product.git', 'ssh://git@example.invalid/team/product.git')) {
        $source = Get-YLIntegrationSource -PackageSource Git -GitRepository $repo -GitRevision $sha.ToUpperInvariant()
        Check ($source.packageOrigin -ceq 'GitRemote' -and $source.gitRepository -ceq $repo -and $source.gitRevision -ceq $sha) 'Remote URL/full SHA identity was changed or became a fixture.'
    }
    foreach ($options in @(
        @{ PackageSource = 'File'; GitRepository = 'https://example.invalid/p.git'; GitRevision = $sha },
        @{ PackageSource = 'File'; GitRepository = 'https://example.invalid/p.git' },
        @{ PackageSource = 'File'; GitRevision = $sha }, @{ PackageSource = 'File'; GitRepository = '' },
        @{ PackageSource = 'Git' }, @{ PackageSource = 'Git'; GitRepository = 'https://example.invalid/p.git' },
        @{ PackageSource = 'Git'; GitRevision = $sha }
    )) { Reject { Get-YLIntegrationSource @options } 'Git' }
    foreach ($repo in @('file:///C:/local.git', 'git+file:///C:/local.git', 'C:\local.git', '../local.git',
        'git://example.invalid/p.git', 'ftp://example.invalid/p.git', 'https://example.invalid/p.git?path=/Other',
        'https://example.invalid/p.git#main', 'https://example.invalid/p.git?', 'https://example.invalid/p.git#',
        'https://token@example.invalid/p.git', 'ssh://git@example.invalid', 'https://example.invalid/', 'https:///p.git',
        'https://example.invalid/p.git bad', 'ssh://git:secret@example.invalid/p.git', 'https://example.invalid/p.git%23main')) {
        Reject { Get-YLIntegrationSource -PackageSource Git -GitRepository $repo -GitRevision $sha } 'Git'
    }
    foreach ($revision in @('main', 'v1.0.0', ('a' * 39), ('a' * 41), ('g' * 40), ($sha + "`n"), '')) {
        Reject { Get-YLIntegrationSource -PackageSource Git -GitRepository 'https://example.invalid/p.git' -GitRevision $revision } 'Git'
    }
    $unusedWorkspace = Join-Path $sandbox 'MustNotBeCreated'
    foreach ($options in @(@{ PackageSource = 'File'; GitRevision = $sha }, @{ PackageSource = 'Git' },
        @{ PackageSource = 'Git'; GitRepository = 'file:///C:/local.git'; GitRevision = $sha })) {
        Reject { & $entry -WorkspaceRoot $unusedWorkspace -Unity 'C:\not-an-editor\Unity.exe' -Integration Excel @options } 'Git'
        Check (-not [IO.Path]::Exists($unusedWorkspace)) 'Invalid CLI inputs reached workspace/Editor preparation.'
    }

    $package = Join-Path $sandbox 'Content'
    $manifestPath = Join-Path $package 'package.json'
    $original = '{"name":"com.kano.ylframework","version":"0.1.0-preview.1","dependencies":{},"date":"2026-01-01T00:00:00Z","unknown":{"flag":true}}'
    Save $manifestPath $original; Save (Join-Path $package 'LICENSE.md') 'fixture license'
    Save (Join-Path $package 'Runtime/Product.cs') '// product bytes'
    Save (Join-Path $package 'Runtime/Product.cs.meta') 'guid: fixture'
    $baseline = Get-YLIntegrationPackageContent -Root $package -PackageId 'com.kano.ylframework' -NormalizeManifest
    Save $manifestPath ('{"_fingerprint":"' + $sha + '","unknown":{"flag":true},"date":"2026-01-01T00:00:00Z","dependencies":{},"version":"0.1.0-preview.1","name":"com.kano.ylframework"}')
    $normalized = Get-YLIntegrationPackageContent -Root $package -PackageId 'com.kano.ylframework' -NormalizeManifest -AllowManagedFingerprint
    Check ($baseline.sha256 -ceq $normalized.sha256 -and $normalized.files.Count -eq 4) 'Only Git UPM formatting/fingerprint may differ; no real file, including meta, may be discarded.'
    Reject { Get-YLIntegrationPackageContent -Root $package -PackageId 'com.kano.ylframework' -NormalizeManifest } 'fingerprint'
    foreach ($bad in @('true', '"short"', '"' + ('g' * 40) + '"')) {
        Save $manifestPath ('{"name":"com.kano.ylframework","version":"0.1.0-preview.1","dependencies":{},"_fingerprint":' + $bad + '}')
        Reject { Get-YLIntegrationPackageContent -Root $package -PackageId 'com.kano.ylframework' -NormalizeManifest -AllowManagedFingerprint } 'fingerprint'
    }
    foreach ($text in @($original.Replace('true', 'false'), $original.Replace('Z"', '+00:00"'))) {
        Save $manifestPath $text
        Check ((Get-YLIntegrationPackageContent -Root $package -PackageId 'com.kano.ylframework' -NormalizeManifest).sha256 -cne $baseline.sha256) 'A public property or date-like string was silently ignored.'
    }
    Save $manifestPath '{"name":"a","name":"b","dependencies":{}}'
    Reject { Get-YLIntegrationPackageContent -Root $package -PackageId 'com.kano.ylframework' -NormalizeManifest } 'already exists'
    Save $manifestPath '{"name":"a","dependencies":{"x":"1","x":"2"}}'
    Reject { Get-YLIntegrationPackageContent -Root $package -PackageId 'com.kano.ylframework' -NormalizeManifest } 'already exists'
    Save $manifestPath $original
    [IO.File]::AppendAllText((Join-Path $package 'Runtime/Product.cs.meta'), 'changed')
    Check ((Get-YLIntegrationPackageContent -Root $package -PackageId 'com.kano.ylframework' -NormalizeManifest).sha256 -cne $baseline.sha256) 'Non-manifest files must use raw bytes.'

    function Invoke-ControllerFixture([string] $Fault = '', [Alias('Source')][string] $FixtureSource = 'Git', [string] $EditorVersion = '2022.3.62f3', [string] $Revision = $sha,
        [string] $RemoteRepository = 'https://example.invalid/product.git', [string] $EntryPackage) {
        $root = Join-Path $sandbox ([Guid]::NewGuid().ToString('N'))
        $fixtureRepository = Join-Path $root 'Repository'; $null = [IO.Directory]::CreateDirectory($fixtureRepository)
        foreach ($relative in @('Tests/Test-YLIntegrationPackages.ps1', 'IntegrationEntry.cs', 'PackageCatalogContract.cs', 'Common/Packages.ps1', 'Common/Workspace.ps1', 'Common/Process.ps1', 'Common/Results.ps1', 'UnityHost.ps1', 'TestSuites.json')) {
            $target = Join-Path $fixtureRepository "Tools/Testing/$relative"
            $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            [IO.File]::Copy((Join-Path $PSScriptRoot "../$relative"), $target)
        }
        $fixtureCatalog = Get-YLFixturePackageCatalog -ConditionalAdapter
        Save (Join-Path $fixtureRepository 'Tools/Packaging/PackageCatalog.json') (ConvertTo-Json -InputObject $fixtureCatalog -Depth 30)
        foreach ($prefix in @('Packages', 'Tools/Testing/Profiles/Unity2022')) {
            Save (Join-Path $fixtureRepository "$prefix/manifest.json") '{"dependencies":{"com.unity.modules.jsonserialize":"1.0.0"}}'
            Save (Join-Path $fixtureRepository "$prefix/packages-lock.json") '{"dependencies":{"com.unity.nuget.newtonsoft-json":{"version":"3.2.1"}}}'
        }
        Save (Join-Path $fixtureRepository 'Unity.exe') 'metadata fixture'
        foreach ($id in @('com.kano.ylframework', 'com.kano.ylframework.excel', 'com.kano.ylframework.domain-excel')) {
            $dependencies = @{}; if ($id -cne 'com.kano.ylframework') { $dependencies['com.kano.ylframework'] = '0.1.0-preview.1' }
            if ($id -ceq 'com.kano.ylframework.domain-excel') { $dependencies['com.kano.ylframework.excel'] = '0.1.0-preview.1' }
            $p = Join-Path $fixtureRepository "PackageSources/$id"
            Save (Join-Path $p 'package.json') (ConvertTo-Json -Depth 5 -InputObject @{ name = $id; version = '0.1.0-preview.1'; dependencies = $dependencies })
            Save (Join-Path $p 'LICENSE.md') 'fixture license'
            Save (Join-Path $p 'Editor/Product.cs') '// fixture product'
            if ($id -ceq 'com.kano.ylframework') { Save (Join-Path $p 'Runtime/Product.cs') '// fixture product' }
            Add-YLFixtureAssemblyPayload -Catalog $fixtureCatalog -PackageId $id -Root $p -Sources
        }
        $excel = Join-Path $fixtureRepository 'PackageSources/com.kano.ylframework.excel/Editor'
        $dll = Join-Path $excel 'Plugins/MiniExcel.dll'; $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($dll))
        [IO.File]::WriteAllBytes($dll, [IO.File]::ReadAllBytes([Newtonsoft.Json.Linq.JObject].Assembly.Location))
        Save (Join-Path $excel 'LICENSE.txt') 'fixture license'; Save (Join-Path $excel 'NOTICE.txt') 'fixture attribution'
        Save (Join-Path $excel 'Dependencies.json') (ConvertTo-Json -InputObject @{ backend = @{ version = '1.31.2'; assemblySha256 = (Get-FileHash -LiteralPath $dll).Hash; licenseFile = 'LICENSE.txt'; attributionFile = 'NOTICE.txt'; license = 'Apache-2.0' } })
        $configuration = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../TestSuites.json')) | ConvertFrom-Json -DateKind String
        $fixtureEditor = @($configuration.editors | Where-Object version -CEQ $EditorVersion)[0]
        function Get-YLTestEditorProfile { return $fixtureEditor }
        function Assert-YLUnityHostIdle { }
        $state = @{ request = $null; start = $null; project = $null; exitCode = 0 }
        function Invoke-YLTestProcess([Diagnostics.ProcessStartInfo] $StartInfo, [string] $Log, [long] $TimeoutMilliseconds) {
            $project = $StartInfo.WorkingDirectory; $state.project = $project; $state.start = $StartInfo
            $output = [IO.Path]::GetDirectoryName($Log)
            $request = [IO.File]::ReadAllText((Join-Path $output 'request.json')) | ConvertFrom-Json -AsHashtable -DateKind String; $state.request = $request
            $manifest = [IO.File]::ReadAllText((Join-Path $project 'Packages/manifest.json')) | ConvertFrom-Json -AsHashtable -DateKind String
            $packages = @(); $locked = @{}; $assemblies = @()
            foreach ($expected in $request.packages) {
                $id = $expected.name; $p = Join-Path $project "Library/PackageCache/$id"
                if ($FixtureSource -ceq 'File') { $p = Join-Path $project "PackageSources/$id" }
                else {
                    foreach ($file in @(Get-YLTestTreeFiles (Join-Path $fixtureRepository "PackageSources/$id"))) {
                        $target = Join-Path $p ([IO.Path]::GetRelativePath((Join-Path $fixtureRepository "PackageSources/$id"), $file))
                        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)); [IO.File]::Copy($file, $target)
                    }
                    $json = [IO.File]::ReadAllText((Join-Path $p 'package.json')) | ConvertFrom-Json -AsHashtable -DateKind String
                    $json['_fingerprint'] = 'b' * 40; Save (Join-Path $p 'package.json') (ConvertTo-Json -Depth 10 -InputObject $json)
                }
                $packageManifest = [IO.File]::ReadAllText((Join-Path $p 'package.json')) | ConvertFrom-Json -AsHashtable -DateKind String
                $locator = $manifest.dependencies[$id]; $git = $FixtureSource -ceq 'Git'
                $packages += @{ name = $id; version = '0.1.0-preview.1'; source = $(if ($git) { 'Git' } else { 'Local' }); assetPath = "Packages/$id"; resolvedPath = $p
                    packageId = $id + '@' + $locator; revision = $(if ($git) { $Revision } else { $null }); verifiedFiles = $expected.files.Count
                    contentSha256 = (Get-YLIntegrationPackageContent -Root $p -PackageId $id -NormalizeManifest:$git -AllowManagedFingerprint:$git).sha256 }
                $locked[$id] = @{ version = $locator; source = $(if ($git) { 'git' } else { 'local' }); hash = $(if ($git) { $Revision } else { $null }); depth = 0; dependencies = $packageManifest.dependencies }
            }
            foreach ($expected in $request.assemblies) {
                $p = @($packages | Where-Object name -CEQ $expected.package)[0]
                $loaded = Join-Path $project ('Library/ScriptAssemblies/' + $expected.name + '.dll'); Save $loaded 'compiled fixture'
                $assemblies += @{ name = $expected.name; sourceFiles = @(Join-Path $p.resolvedPath ($expected.directory + '/Product.cs')); loadedPath = $loaded; sha256 = (Get-FileHash -LiteralPath $loaded).Hash }
            }
            $report = @{ schemaVersion = 1; purpose = $request.purpose; integration = $request.integration; hostId = $request.hostId; resultId = $request.resultId; inputHash = $request.inputHash
                unityVersion = $EditorVersion; packageOrigin = $(if ($FixtureSource -ceq 'Git') { 'GitRemote' } else { 'File' }); expectedTemplateHash = $request.expectedTemplateHash
                entryPackage = $request.entryPackage; catalogSha256 = $request.catalogSha256
                status = 'Completed'; pid = 42; isBatchMode = $true; scriptCompilationFailed = $false; packages = $packages; assemblies = $assemblies }
            switch ($Fault) {
                'source' { $packages[0].source = 'Local' }
                'locator' { $packages[0].packageId = $packages[0].name + '@file:/outside' }
                'locator-path' { $packages[0].packageId = $packages[0].packageId.Replace('/@git+product.git', '/@product.git') }
                'revision' { $packages[0].revision = 'c' * 40 }
                'api-hash' { $packages[0]['apiHash'] = 'c' * 40 }
                'api-revision' { $packages[0]['apiRevision'] = 'main' }
                'lock-hash' { $locked[$packages[0].name].hash = 'c' * 40 }
                'lock-version' { $locked[$packages[0].name].version = 'https://different.invalid/p.git#' + $Revision }
                'lock-source' { $locked[$packages[0].name].source = 'local' }
                'lock-dependency' { $locked['com.kano.ylframework.domain-excel'].dependencies = @{} }
                'origin' { $report.packageOrigin = 'File' }
                'content' { [IO.File]::AppendAllText((Join-Path $packages[0].resolvedPath 'LICENSE.md'), 'tampered') }
                'extra-file' { Save (Join-Path $packages[0].resolvedPath 'extra.cs') '// extra' }
                'missing-file' { [IO.File]::Delete((Join-Path $packages[0].resolvedPath 'LICENSE.md')) }
                'public-manifest' { $p = Join-Path $packages[0].resolvedPath 'package.json'; $j = [IO.File]::ReadAllText($p) | ConvertFrom-Json -AsHashtable; $j['newPublic'] = $true; Save $p (ConvertTo-Json -Depth 10 -InputObject $j) }
                'sources' { $assemblies[0].sourceFiles = @(Join-Path $fixtureRepository 'PackageSources/com.kano.ylframework/Runtime/Product.cs') }
                'escape' { $packages[0].resolvedPath = Join-Path $fixtureRepository ('PackageSources/' + $packages[0].name) }
                'imported-sample' { Save (Join-Path $project 'Assets/Samples/Unexpected/sample.txt') 'not imported by this tool' }
                'input-drift' { [IO.File]::AppendAllText((Join-Path $fixtureRepository 'Tools/Testing/IntegrationEntry.cs'), '// drift') }
                'catalog-drift' { [IO.File]::AppendAllText((Join-Path $fixtureRepository 'Tools/Packaging/PackageCatalog.json'), "`n") }
                'template-drift' { [IO.File]::AppendAllText((Join-Path $fixtureRepository 'PackageSources/com.kano.ylframework/LICENSE.md'), 'drift') }
                'cache-owner-drift' { $path = Join-Path $request.cacheRoot '.yl-owner.json'; $j = [IO.File]::ReadAllText($path) | ConvertFrom-Json -AsHashtable; $j.id = 'f' * 32; Save $path (ConvertTo-Json -Depth 10 -InputObject $j) }
            }
            Save (Join-Path $project 'Packages/packages-lock.json') (ConvertTo-Json -Depth 20 -InputObject @{ dependencies = $locked })
            Write-YLTestJson -Path (Join-Path $output 'report.json') -Value $report
            return [pscustomobject]@{ status = 'Exited'; observedExit = $true; exitCode = 0; timedOut = $false; cancelled = $false; forcedCleanup = $false; observerError = $null; cleanupError = $null; jobAssigned = $true; activeProcesses = 0; streamsDrained = $true; pid = 42 }
        }
        $statements = foreach ($statement in $ast.EndBlock.Statements) {
            if ($statement -is [Management.Automation.Language.FunctionDefinitionAst]) { continue }
            if ($statement -is [Management.Automation.Language.PipelineAst] -and $statement.PipelineElements[0] -is [Management.Automation.Language.CommandAst] -and
                $statement.PipelineElements[0].InvocationOperator -eq [Management.Automation.Language.TokenKind]::Dot) { continue }
            if ($statement -is [Management.Automation.Language.AssignmentStatementAst] -and $statement.Left.Extent.Text -ceq '$repository') { '$repository = $fixtureRepository'; continue }
            if ($statement -is [Management.Automation.Language.AssignmentStatementAst] -and $statement.Left.Extent.Text -ceq '$configuration') { continue }
            $statement.Extent.Text -replace '\bexit ([12])\b', '$state.exitCode = $1'
        }
        $controller = [scriptblock]::Create($ast.ParamBlock.Extent.Text + "`n" + ($statements -join "`n"))
        $options = @{ WorkspaceRoot = (Join-Path $root 'Workspace'); Unity = (Join-Path $fixtureRepository 'Unity.exe'); Integration = 'DomainExcel'; PackageSource = $FixtureSource }
        if ($EntryPackage) { $options.Remove('Integration'); $options.PackageId = $EntryPackage }
        if ($FixtureSource -ceq 'Git') { $options.GitRepository = $RemoteRepository; $options.GitRevision = $Revision }
        $null = & $controller @options
        $summary = [IO.File]::ReadAllText((Join-Path $state.request.resultRoot 'summary.json')) | ConvertFrom-Json -AsHashtable -DateKind String
        return @{ summary = $summary; state = $state; root = $root; repository = $fixtureRepository }
    }
    $keys = @('GIT_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE', 'GIT_CONFIG_COUNT', 'GIT_CONFIG_KEY_0', 'GIT_CONFIG_VALUE_0', 'GIT_COMMON_DIR', 'GIT_OBJECT_DIRECTORY', 'GIT_ALTERNATE_OBJECT_DIRECTORIES', 'GIT_LFS_SKIP_SMUDGE')
    $saved = @{}
    try {
        foreach ($key in $keys) { $saved[$key] = [Environment]::GetEnvironmentVariable($key); [Environment]::SetEnvironmentVariable($key, 'untrusted') }
        foreach ($version in @('2022.3.62f3', '6000.3.27f1')) {
            $fixture = Invoke-ControllerFixture -EditorVersion $version
            Check ($fixture.summary.status -ceq 'Completed' -and $fixture.summary.packageOrigin -ceq 'GitRemote') ('Valid remote boundary fixture failed: ' + $fixture.summary.reason)
            Check (-not [IO.Directory]::Exists((Join-Path $fixture.state.project 'PackageSources'))) 'Git host staged workspace product source.'
            Check ($fixture.state.request.packages.Count -eq 3) 'Git did not install the whole owned dependency closure.'
            $manifest = [IO.File]::ReadAllText((Join-Path $fixture.state.project 'Packages/manifest.json')) | ConvertFrom-Json -AsHashtable
            Check ($manifest.dependencies['com.kano.ylframework.domain-excel'] -ceq ('https://example.invalid/product.git?path=/PackageSources/com.kano.ylframework.domain-excel#' + $sha)) 'Wrong actual Git subfolder locator.'
            Check (@($fixture.summary.inputIdentity.files | Where-Object { $_.path.StartsWith('PackageSources/') -and -not $_.path.EndsWith('/Dependencies.json') }).Count -eq 0) 'Local product bytes were presented as observed remote inputs.'
            Check ($fixture.summary.expectedTemplate.purpose -ceq 'LocalReleaseExpectedTemplateNotInstallSource') 'The local comparison template was not disclosed separately.'
            Check (@($fixture.summary.inputIdentity.files | Where-Object path -CEQ 'Tools/Packaging/PackageCatalog.json').Count -eq 1) 'The package catalog was omitted from the source input identity.'
            Check (@($fixture.state.request.assemblies | Where-Object name -CEQ 'YLFramework.FixtureAdapter').Count -eq 0) 'The default profile silently enabled an SDK-dependent adapter.'
            $mainInput = @($fixture.state.request.packages | Where-Object name -CEQ 'com.kano.ylframework')[0]
            Check (@($mainInput.files | Where-Object path -CEQ 'Runtime/Integration/Fixture/Product.cs').Count -eq 1) 'The disabled adapter source disappeared from the complete payload.'
            $identity = $fixture.summary.inputIdentity
            $localSource = Join-Path $fixture.repository 'PackageSources/com.kano.ylframework/Runtime/Product.cs'
            [IO.File]::AppendAllText($localSource, '// changed local source after the fixture')
            $recheck = Get-YLTestInputIdentity -Repository $fixture.repository -Paths @($identity.files.path) -Toolchain @($identity.toolchain.path) -Profile $identity.profile
            Check ($recheck.sha256 -ceq $identity.sha256) 'Changing local product bytes silently changed the pinned remote input identity.'
            $expected = $fixture.summary.expectedTemplate.identity
            $templateRecheck = Get-YLTestInputIdentity -Repository $fixture.repository -Paths @($expected.files.path) -Profile $expected.profile
            Check ($templateRecheck.sha256 -cne $expected.sha256) 'The separate expected template did not preserve raw local source drift evidence.'
            Check ([IO.File]::ReadAllText($fixture.summary.generatedLock.path).Contains($sha, [StringComparison]::Ordinal)) 'The complete generated lock was not retained as observed evidence.'
            Check (@($fixture.state.start.Environment.Keys | Where-Object { $_.StartsWith('GIT_', [StringComparison]::OrdinalIgnoreCase) }).Count -eq 0) 'Caller Git overrides reached Unity.'
            foreach ($key in @('UPM_CACHE_ROOT', 'UPM_CACHE_PATH', 'UPM_NPM_CACHE_PATH', 'UPM_GIT_LFS_CACHE_PATH')) {
                Check ($fixture.state.start.Environment[$key].StartsWith((Join-Path $fixture.root 'Workspace/Cache') + '\', [StringComparison]::OrdinalIgnoreCase)) "Unowned cache reached Unity: $key"
            }
        }
    } finally { foreach ($key in $keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) } }
    $file = Invoke-ControllerFixture -Source File
    Check ($file.summary.status -ceq 'Completed' -and $file.summary.packageOrigin -ceq 'File' -and [IO.Directory]::Exists((Join-Path $file.state.project 'PackageSources'))) 'File no longer keeps complete staging.'
    foreach ($version in @('2022.3.62f3', '6000.3.27f1')) {
        $minimal = Invoke-ControllerFixture -Source File -EditorVersion $version -EntryPackage 'com.kano.ylframework'
        Check ($minimal.summary.status -ceq 'Completed' -and $minimal.summary.integration -ceq 'Package') 'The known minimum-compilation profile was not selected.'
        Check ($minimal.state.request.packages.Count -eq 1 -and $minimal.state.request.entryPackage -ceq 'com.kano.ylframework') 'Minimum installation silently added optional packages.'
        Check (-not $minimal.state.request.ContainsKey('dotweenSdkMode')) 'PackageId accepted an implicit SDK request.'
        Check ($minimal.state.request.resultRoot.Contains('-main-File-')) 'Result rotation must not erase another minimum entry package or origin under the same generic Package profile.'
    }
    $fixture = Invoke-ControllerFixture -RemoteRepository 'https://example.invalid/@git+product.git' -Fault locator-path
    Check ($fixture.summary.status -ceq 'Failed') 'Only the UPM git+ scheme marker may be normalized; repository path bytes must match exactly.'
    foreach ($fault in @('source', 'locator', 'revision', 'api-hash', 'api-revision', 'lock-hash', 'lock-version', 'lock-source', 'lock-dependency', 'origin', 'content', 'extra-file', 'missing-file', 'public-manifest', 'sources', 'escape', 'input-drift', 'catalog-drift', 'template-drift', 'imported-sample', 'cache-owner-drift')) {
        $fixture = Invoke-ControllerFixture -Fault $fault
        Check ($fixture.summary.status -ceq 'Failed' -and $fixture.state.exitCode -eq 1) "Remote controller accepted $fault."
    }
    "PASS: $checks NoUnity Git parameter/content/controller/cache contracts. Simulated Unity boundary only, NOT remote or Unity acceptance."
} finally {
    if ([IO.Directory]::Exists($sandbox)) { $null = @(Get-YLTestTreeFiles $sandbox); [IO.Directory]::Delete($sandbox, $true) }
}
