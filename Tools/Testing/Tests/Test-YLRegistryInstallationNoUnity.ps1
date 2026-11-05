#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param([string] $TemporaryRoot = [IO.Path]::GetTempPath(), [string] $ControllerUnity)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Common/Packages.ps1')
. (Join-Path $PSScriptRoot '../Common/Process.ps1')
. (Join-Path $PSScriptRoot '../Common/Results.ps1')
. (Join-Path $PSScriptRoot 'Test-YLIntegrationPackages.ps1') -WorkspaceRoot $TemporaryRoot -Unity 'C:\unused\Unity.exe' -Integration Excel
$root = Join-Path (Get-YLTestPath $TemporaryRoot) ('yl-registry-install-contracts-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($root)
$checks = 0
$completed = $false
function Check([bool] $Condition, [string] $Reason) { if (-not $Condition) { throw $Reason }; $script:checks++ }
function Reject([scriptblock] $Action, [string] $Pattern) {
    try { & $Action | Out-Null } catch {
        if ($_.Exception -is [Management.Automation.CommandNotFoundException] -or $_.Exception.Message -notmatch $Pattern) { throw }
        $script:checks++; return
    }
    throw "Expected refusal: $Pattern"
}
function Save([string] $Path, [object] $Value) { [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 40), [Text.UTF8Encoding]::new($false)) }
function Copy-Value([object] $Value) { return ,(ConvertTo-Json -InputObject $Value -Depth 40 | ConvertFrom-Json -AsHashtable -NoEnumerate -DateKind String) }
try {
    $version = '0.1.0-preview.1'; $url = 'https://package.openupm.com'
    . (Join-Path $PSScriptRoot 'PackageCatalogFixtures.ps1')
    $ids = @((Read-YLPackageCatalog).packages.id)
    $rows = @(foreach ($id in $ids) {
        $deps = Get-YLFixtureInternalDependencies -PackageId $id -Version $version
        if ($id -ceq 'com.kano.ylframework') { $deps['com.unity.ugui'] = '1.0.0' }
        if ($id.EndsWith('.hybridclr')) { $deps['com.code-philosophy.hybridclr'] = '9.0.3' }
        $text = 'package.json' + [char]0 + ('b' * 64) + "`n"
        @{ id = $id; version = $version; dependencies = $deps; assetName = "$id-$version.tgz"; sha256 = ('a' * 64)
            integrity = 'sha512-' + [Convert]::ToBase64String([byte[]]::new(64)); files = @(@{ path = 'package.json'; sha256 = ('a' * 64); contentSha256 = ('b' * 64) })
            payloadSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text))).ToLowerInvariant() }
    })
    $receipt = @{ schemaVersion = 1; sourceCommit = ('a' * 40); toolchain = @{ node = '22.23.3'; npm = '10.9.9'; powershell = '7.6.9' }
        producer = @{ kind = 'GitHubActions'; repository = 'KONOK-A/YLFramework-dev'; workflow = 'FixtureNotAnApprovedCiRun'; runId = '1'; runAttempt = '1' }; packages = $rows }
    $path = Join-Path $root 'release-manifest.json'; Save $path $receipt
    $release = Read-YLRegistryRelease -Path $path
    $roots = @('com.kano.ylframework.excel', 'com.kano.ylframework.gameplay')
    $combined = @(Get-YLRegistryClosure -Release $release -EntryPackage $roots[0] -EntryPackages $roots)
    Check (($combined.id -join ',') -ceq 'com.kano.ylframework,com.kano.ylframework.data-assets,com.kano.ylframework.excel,com.kano.ylframework.gameplay') 'ExcelGameplay must use its exact four-package closure, not the retired DomainExcel root.'
    $reversed = @(Get-YLRegistryClosure -Release $release -EntryPackage $roots[1] -EntryPackages @($roots[1], $roots[0]))
    Check (($reversed.id -join ',') -ceq ($combined.id -join ',')) 'The primary root must be first without changing the composite closure.'
    $controllerSource = Get-YLIntegrationSource -PackageSource Registry -RegistryUrl $url -RegistryVersion $version -ReleaseManifest $path
    Check ($controllerSource.packageOrigin -ceq 'Registry') 'The maintained integration controller did not bind Registry arguments.'
    $requestFields = Get-YLRegistryRequestFields -Source $controllerSource -EntryPackage 'com.kano.ylframework'
    Check ($requestFields.registryUrl -ceq $url -and $requestFields.registryVersion -ceq $version) 'Registry entry request lost its explicit URL/version.'
    Check ($requestFields.Contains('gitRepository') -and $requestFields.Contains('gitRevision') -and
        $null -eq $requestFields.gitRepository -and $null -eq $requestFields.gitRevision) 'Artifact requests require JSON null Git fields, not PowerShell typed empty strings.'
    $localProducer = Copy-Value $receipt; $localProducer.producer = @{ kind = 'Local' }; Save $path $localProducer
    Reject { Get-YLRegistrySource -PackageSource Registry -Arguments @{ ReleaseManifest = $path; RegistryUrl = $url; RegistryVersion = $version } } 'CI|producer|candidate'
    Save $path $receipt
    foreach ($script in @('Test-YLDistribution.ps1', 'Test-YLIntegrationPackages.ps1')) {
        $tokens = $null; $errors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $script), [ref]$tokens, [ref]$errors)
        Check ($errors.Count -eq 0) "The maintained $script must parse."
        if ($script -ceq 'Test-YLDistribution.ps1') {
            $contentFunction = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Get-DistributionPackageContent' }, $true)
            . ([scriptblock]::Create($contentFunction.Extent.Text))
        }
        $bind = [scriptblock]::Create($ast.ParamBlock.Extent.Text + "`n" + '$PackageSource')
        $args = @{ WorkspaceRoot = $root; Unity = 'C:\unused\Unity.exe'; PackageSource = 'Registry'; RegistryUrl = $url; RegistryVersion = $version; ReleaseManifest = $path }
        if ($script -ceq 'Test-YLIntegrationPackages.ps1') { $args.Integration = 'Excel' }
        Check ((& $bind @args) -ceq 'Registry') "The maintained $script must expose the exact Registry CLI contract."
    }
    Check ($release.packages.Count -eq $ids.Count -and $release.sha256 -cmatch '^[0-9a-f]{64}$') 'A typed complete catalog receipt was not read.'
    foreach ($id in $rows.id) {
        $manifest = New-YLRegistryHostManifest -PackageSource Registry -Release $release -EntryPackage $id -RegistryUrl $url
        Check ($manifest.dependencies.Count -eq 1 -and $manifest.dependencies[$id] -ceq $version) 'Registry must declare only the selected entry, never its closure or official packages.'
        Check ($manifest.scopedRegistries.Count -eq 1 -and $manifest.scopedRegistries[0].url -ceq $url -and
            $manifest.scopedRegistries[0].scopes[0] -ceq 'com.kano.ylframework' -and 'com.unity' -cnotin $manifest.scopedRegistries[0].scopes) 'Registry scopes leaked outside the approved names.'
    }
    $domain = $combined
    $tarManifest = New-YLRegistryHostManifest -PackageSource LocalTarball -Release $release -EntryPackage $roots[0] -EntryPackages $roots -RegistryUrl $url
    Check ($tarManifest.dependencies.Count -eq 4 -and @($tarManifest.dependencies.Values | Where-Object { $_ -notmatch '^file:.+\.tgz$' }).Count -eq 0) 'ArtifactPreflight must use actual UPM tarball locators for its entire own closure.'
    foreach ($badRoots in @(@{ roots = @() }, @{ roots = @($roots[0], $roots[0]) }, @{ roots = @($roots[1], $roots[0]) }, @{ roots = @($roots[0], 'unknown') }, @{ roots = @($roots[0], 'com.kano.ylframework') }, @{ roots = @('') })) {
        Reject { Get-YLRegistryClosure -Release $release -EntryPackage $roots[0] -EntryPackages $badRoots.roots } 'entry|root|primary|composite'
        Reject { New-YLRegistryHostManifest -PackageSource Registry -Release $release -EntryPackage $roots[0] -EntryPackages $badRoots.roots } 'entry|root|primary|composite'
        Reject { Get-YLRegistryRequestFields -Source $controllerSource -EntryPackage $roots[0] -EntryPackages $badRoots.roots } 'entry|root|primary|composite'
    }
    $hybrid = New-YLRegistryHostManifest -PackageSource Registry -Release $release -EntryPackage 'com.kano.ylframework.hybridclr' -RegistryUrl $url
    Check ($hybrid.scopedRegistries[0].scopes.Count -eq 2 -and 'com.code-philosophy.hybridclr' -cin $hybrid.scopedRegistries[0].scopes -and
        -not $hybrid.dependencies.Contains('com.code-philosophy.hybridclr')) 'HybridCLR SDK must be indirect and narrowly routed.'
    foreach ($fault in @('missing', 'missing-schema', 'schema-type', 'unknown-package', 'duplicate-package', 'version', 'internal', 'commit', 'producer', 'toolchain', 'boolean-node', 'boolean-npm', 'boolean-producer-repository', 'path', 'file-hash', 'payload', 'extra-field')) {
        $bad = Copy-Value $receipt
        switch ($fault) {
            missing { $bad.packages = @($bad.packages[0..3]) }
            'missing-schema' { $bad.Remove('schemaVersion') }
            'schema-type' { $bad.schemaVersion = $true }
            'unknown-package' { $bad.packages[1].id = 'com.kano.ylframework.unknown' }
            'duplicate-package' { $bad.packages[1] = Copy-Value $bad.packages[0] }
            version { $bad.packages[1].version = '0.1.0-preview.2' }
            internal { $bad.packages[1].dependencies['com.kano.ylframework'] = '0.0.1' }
            commit { $bad.sourceCommit = $true }
            producer { $bad.producer = @{ kind = 'GitHubActions'; repository = 'KONOK-A/YLFramework-dev'; workflow = 'checks'; runId = $true; runAttempt = '1' } }
            toolchain { $bad.toolchain.node = '24.22.2' }
            'boolean-node' { $bad.toolchain.node = $true }
            'boolean-npm' { $bad.toolchain.npm = $true }
            'boolean-producer-repository' { $bad.producer = @{ kind = 'GitHubActions'; repository = $true; workflow = 'checks'; runId = '1'; runAttempt = '1' } }
            path { $bad.packages[0].files[0].path = '../escape' }
            'file-hash' { $bad.packages[0].files[0].sha256 = $true }
            payload { $bad.packages[0].payloadSha256 = 'f' * 64 }
            'extra-field' { $bad.packages[0].untrusted = 'extra' }
        }
        Save $path $bad
        Reject { Read-YLRegistryRelease -Path $path } 'release|manifest|package|version|identity|producer|toolchain|path|digest|field|payload'
    }
    [IO.File]::WriteAllText($path, '{"schemaVersion":1,"schemaVersion":1}')
    Reject { Read-YLRegistryRelease -Path $path } 'Duplicate'
    Save $path $receipt; [IO.File]::AppendAllText($path, '{}')
    Reject { Read-YLRegistryRelease -Path $path } 'JSON|trailing'
    Save $path $receipt
    foreach ($source in @('File', 'Git')) {
        foreach ($parameter in @('RegistryUrl', 'RegistryVersion', 'ReleaseManifest')) {
            Reject { Get-YLRegistrySource -PackageSource $source -Arguments @{ $parameter = 'unexpected' } } 'refuses|only|source'
        }
    }
    Reject { Get-YLRegistrySource -PackageSource Registry -Arguments @{ ReleaseManifest = $path; RegistryUrl = $url } } 'RegistryVersion'
    Reject { Get-YLRegistrySource -PackageSource LocalTarball -Arguments @{ ReleaseManifest = $path; RegistryUrl = $url } } 'RegistryUrl'
    Reject { Get-YLRegistrySource -PackageSource Registry -Arguments @{ ReleaseManifest = $path; RegistryUrl = $url; RegistryVersion = $version; GitRevision = ('a' * 40) } } 'Git'
    Reject { Get-YLRegistrySource -PackageSource Registry -Arguments @{ ReleaseManifest = $path; RegistryUrl = 'https://other.invalid'; RegistryVersion = $version } } 'RegistryUrl|OpenUPM'
    $tarSource = Get-YLRegistrySource -PackageSource LocalTarball -Arguments @{ ReleaseManifest = $path }
    $tarFields = Get-YLRegistryRequestFields -Source $tarSource -EntryPackage $roots[0] -EntryPackages $roots
    Check ($null -eq $tarFields.registryUrl -and $null -eq $tarFields.registryVersion -and $tarFields.entryPackage -ceq $roots[0] -and ($tarFields.entryPackages -join '|') -ceq ($roots -join '|')) 'Actual LocalTarball entry request must not contain Registry-only fields or lose composite roots.'
    Reject { Initialize-YLRegistryExpectedInputs -Source $tarSource -Output (Join-Path $root 'MissingTarballEvidence') } 'PrerequisiteNotMet:.*tarball'
    $source = Get-YLRegistrySource -PackageSource Registry -Arguments @{ ReleaseManifest = $path; RegistryUrl = $url; RegistryVersion = $version }
    Check ($source.packageOrigin -ceq 'Registry' -and $source.release.sha256 -ceq $release.sha256) 'Registry request lost its frozen release identity.'
    foreach ($stage in @('Authoring', 'Lifecycle')) {
        Assert-YLRegistryStageCompleted -Outcome ([pscustomobject]@{ status = 'Completed'; error = '' }) -Stage $stage; $checks++
        Reject { Assert-YLRegistryStageCompleted -Outcome ([pscustomobject]@{ status = 'PrerequisiteNotMet'; error = 'Registry metadata temporarily unavailable' }) -Stage $stage } '^PrerequisiteNotMet:'
        Reject { Assert-YLRegistryStageCompleted -Outcome ([pscustomobject]@{ status = 'Failed'; error = 'Actual compilation assertion failed' }) -Stage $stage } '^Registry .+ failed:'
        Reject { Assert-YLRegistryStageCompleted -Outcome ([pscustomobject]@{ status = $true; error = '' }) -Stage $stage } 'Invalid|status'
    }
    $cache = Join-Path $root 'ColdCache'; $null = [IO.Directory]::CreateDirectory($cache)
    $start = [Diagnostics.ProcessStartInfo]::new('C:\unused\Unity.exe')
    $start.Environment['UPM_CACHE_ROOT'] = Join-Path $cache 'Upm'
    $start.Environment['UPM_CACHE_PATH'] = Join-Path $cache 'Upm/packages'
    $start.Environment['UPM_NPM_CACHE_PATH'] = Join-Path $cache 'Upm/npm'
    $cold = Assert-YLRegistryColdCache -StartInfo $start -CacheRoot $cache
    Check ($cold.status -ceq 'EmptyBeforeLaunch') 'Cold cache was not explicitly checked.'
    $observed = @{}; foreach ($key in @('UPM_CACHE_ROOT', 'UPM_CACHE_PATH', 'UPM_NPM_CACHE_PATH')) { $observed[$key] = $start.Environment[$key] }
    Assert-YLRegistryCacheObservation -Actual $observed -CacheRoot $cache; $checks++
    $observed['UPM_CACHE_PATH'] = 'C:\user-cache'
    Reject { Assert-YLRegistryCacheObservation -Actual $observed -CacheRoot $cache } 'cache'
    $null = [IO.Directory]::CreateDirectory((Join-Path $cache 'Upm/npm')); [IO.File]::WriteAllText((Join-Path $cache 'Upm/npm/injected.tgz'), 'not cold')
    Reject { Assert-YLRegistryColdCache -StartInfo $start -CacheRoot $cache } 'cold|empty|cache'
    $installedRoot = Join-Path $root 'Installed'; $null = [IO.Directory]::CreateDirectory($installedRoot)
    $installedManifest = @{ name = 'com.kano.ylframework'; version = $version; dependencies = @{} }
    $installedJson = Join-Path $installedRoot 'package.json'; Save $installedJson $installedManifest
    [IO.File]::WriteAllText((Join-Path $installedRoot 'LICENSE.md'), 'fixture license')
    $beforeManaged = Get-YLIntegrationPackageContent -Root $installedRoot -PackageId 'com.kano.ylframework' -NormalizeManifest
    $beforeDistributionManaged = Get-DistributionPackageContent -Root $installedRoot
    $installedManifest['_fingerprint'] = 'A' * 40; Save $installedJson $installedManifest
    $afterManaged = Get-YLIntegrationPackageContent -Root $installedRoot -PackageId 'com.kano.ylframework' -NormalizeManifest -AllowManagedFingerprint
    Check ($beforeManaged.sha256 -ceq $afterManaged.sha256) 'A legal uppercase 40hex managed fingerprint must preserve the semantic payload.'
    $afterDistributionManaged = Get-DistributionPackageContent -Root $installedRoot -AllowManagedFingerprint
    Check ($beforeDistributionManaged.sha256 -ceq $afterDistributionManaged.sha256) 'Distribution must use the same legal uppercase/lowercase 40hex rule as its C# verifier.'
    $installedManifest['_fingerprint'] = '2bdab7139776f7ea3e50932a86582fbf'; Save $installedJson $installedManifest
    $tarContent = Get-YLIntegrationPackageContent -Root $installedRoot -PackageId 'com.kano.ylframework' -NormalizeManifest -AllowManagedFingerprint -FingerprintSource LocalTarball
    $tarDistribution = Get-DistributionPackageContent -Root $installedRoot -AllowManagedFingerprint -FingerprintSource LocalTarball
    Check ($tarContent.sha256 -ceq $beforeManaged.sha256 -and $tarDistribution.sha256 -ceq $beforeDistributionManaged.sha256) 'Observed Unity 6 LocalTarball 32hex metadata must preserve the frozen payload.'
    Reject { Get-YLIntegrationPackageContent -Root $installedRoot -PackageId 'com.kano.ylframework' -NormalizeManifest -AllowManagedFingerprint } 'fingerprint'
    Reject { Get-DistributionPackageContent -Root $installedRoot -AllowManagedFingerprint } 'fingerprint'
    $installedManifest['_fingerprint'] = 'b' * 40; Save $installedJson $installedManifest
    Reject { Get-YLIntegrationPackageContent -Root $installedRoot -PackageId 'com.kano.ylframework' -NormalizeManifest -AllowManagedFingerprint -FingerprintSource LocalTarball } 'fingerprint'
    Reject { Get-DistributionPackageContent -Root $installedRoot -AllowManagedFingerprint -FingerprintSource LocalTarball } 'fingerprint'

    $entry = $roots[0]
    $manifest = New-YLRegistryHostManifest -PackageSource Registry -Release $release -EntryPackage $entry -EntryPackages $roots -RegistryUrl $url
    Check ($manifest.dependencies.Count -eq 2 -and $manifest.dependencies[$roots[0]] -ceq $version -and $manifest.dependencies[$roots[1]] -ceq $version) 'Both Registry roots must be direct exact semvers.'
    $actual = @(); $lock = @{ dependencies = @{} }
    foreach ($package in $domain) {
        $direct = $package.id -cin $roots
        $actual += @{ name = $package.id; version = $version; source = 'Registry'; packageId = $package.id + '@' + $version
            resolvedPath = Join-Path $root ('Host/Library/PackageCache/' + $package.id); isDirectDependency = $direct; registryUrl = $url; dependencies = $package.dependencies }
        $lock.dependencies[$package.id] = @{ version = $version; source = 'registry'; url = $url; dependencies = $package.dependencies }
    }
    $actual += @{ name = 'com.unity.ugui'; version = '2.0.0'; source = 'BuiltIn'; packageId = 'com.unity.ugui@2.0.0'; resolvedPath = 'C:\Editor\BuiltInPackages\com.unity.ugui'; isDirectDependency = $false; registryUrl = $null; dependencies = @{} }
    $lock.dependencies['com.unity.ugui'] = @{ version = '2.0.0'; source = 'builtin'; dependencies = @{} }
    $depths = @{}; $pending = [Collections.Generic.Queue[string]]::new()
    foreach ($id in $manifest.dependencies.Keys) { $depths[$id] = 0; $pending.Enqueue($id) }
    while ($pending.Count -gt 0) {
        $id = $pending.Dequeue(); $lock.dependencies[$id].depth = $depths[$id]
        foreach ($dependency in $lock.dependencies[$id].dependencies.Keys) {
            if (-not $depths.ContainsKey($dependency)) { $depths[$dependency] = $depths[$id] + 1; $pending.Enqueue($dependency) }
        }
    }
    Check ($depths.Count -eq 5 -and $depths['com.kano.ylframework.data-assets'] -eq 1 -and
        $depths['com.kano.ylframework.excel'] -eq 0 -and $depths['com.kano.ylframework.gameplay'] -eq 0 -and $depths['com.unity.ugui'] -eq 2) 'The fixture must represent shortest dependency distances from all direct roots.'
    $graphArgs = @{ Manifest = $manifest; Lock = $lock; Rows = $actual; Release = $release; EntryPackage = $entry; EntryPackages = $roots; PackageSource = 'Registry'; RegistryUrl = $url; EditorVersion = '6000.3.27f1'; HostRoot = (Join-Path $root 'Host'); CacheRoot = (Join-Path $root 'Cache') }
    $evidence = Assert-YLRegistryResolvedGraph @graphArgs
    Check ($evidence.packages.Count -eq 5 -and $evidence.origin -ceq 'Registry') 'The observed graph lost its Registry/Editor baseline distinction.'
    $foreignOfficial = Copy-Value $graphArgs
    $foreignOfficial.Rows[-1].source = 'Registry'; $foreignOfficial.Rows[-1].registryUrl = 'https://packages.unity.com'
    $foreignOfficial.Rows[-1].resolvedPath = 'C:\foreign-global-cache\com.unity.ugui'
    $foreignOfficial.Lock.dependencies['com.unity.ugui'].source = 'registry'; $foreignOfficial.Lock.dependencies['com.unity.ugui'].url = 'https://packages.unity.com'
    Reject { Assert-YLRegistryResolvedGraph @foreignOfficial } 'owned|cache'
    $emptyCache = Join-Path $root 'PostResolutionCache'; $null = [IO.Directory]::CreateDirectory($emptyCache)
    Reject { Get-YLRegistryCacheUse -CacheRoot $emptyCache -PackageSource Registry } 'cache|observed|empty'
    $emptyTarCache = Get-YLRegistryCacheUse -CacheRoot $emptyCache -PackageSource LocalTarball
    Check ($emptyTarCache.status -ceq 'NoUpmCacheFilesObserved' -and $emptyTarCache.files.Count -eq 0) 'No downloaded cache files must not become invented cache evidence.'
    $upm = Join-Path $emptyCache 'Upm'; $null = [IO.Directory]::CreateDirectory($upm); [IO.File]::WriteAllText((Join-Path $upm 'metadata.json'), '{}')
    $used = Get-YLRegistryCacheUse -CacheRoot $emptyCache -PackageSource Registry
    Check ($used.status -ceq 'ObservedOwnedUpmCacheFiles' -and $used.files.Count -eq 1 -and $used.files[0].sha256 -cmatch '^[0-9a-f]{64}$') 'Actual post-resolution cache bytes must be independently observed.'
    $tarGraphArgs = Copy-Value $graphArgs; $tarGraphArgs.PackageSource = 'LocalTarball'; $tarGraphArgs.Manifest = $tarManifest
    foreach ($row in $tarGraphArgs.Rows) {
        if ($row.name.StartsWith('com.kano.', [StringComparison]::Ordinal)) {
            $row.source = 'LocalTarball'; $row.isDirectDependency = $true; $row.registryUrl = $null
            $row.packageId = $row.name + '@' + $tarManifest.dependencies[$row.name]
            $locked = $tarGraphArgs.Lock.dependencies[$row.name]; $locked.source = 'local-tarball'; $locked.version = $tarManifest.dependencies[$row.name]; $locked.depth = 0; $locked.Remove('url')
        } else { $tarGraphArgs.Lock.dependencies[$row.name].depth = 1 }
    }
    $tarEvidence = Assert-YLRegistryResolvedGraph @tarGraphArgs
    Check ($tarEvidence.origin -ceq 'LocalTarball' -and $tarEvidence.directPackages.Count -eq 4) 'Actual tarball graph must preserve direct locator closure, not Registry indirectness.'
    foreach ($fault in @('tar-git', 'tar-folder', 'tar-version', 'tar-depth', 'tar-indirect')) {
        $arguments = Copy-Value $tarGraphArgs
        switch ($fault) {
            'tar-git' { $arguments.Rows[0].source = 'Git' }
            'tar-folder' { $arguments.Lock.dependencies['com.kano.ylframework'].source = 'local' }
            'tar-version' { $arguments.Lock.dependencies['com.kano.ylframework'].version = $version }
            'tar-depth' { $arguments.Lock.dependencies['com.kano.ylframework'].depth = 1 }
            'tar-indirect' { $arguments.Rows[0].isDirectDependency = $false }
        }
        Reject { Assert-YLRegistryResolvedGraph @arguments } 'source|lock|depth|direct|graph|package'
    }
    foreach ($fault in @('predeclared', 'missing-root', 'second-root-depth', 'boolean-scope-url', 'boolean-lock-url', 'wrong-source', 'wrong-direct', 'wrong-url', 'lock-depth', 'transitive-lock-depth', 'missing-backend', 'lock-version', 'lock-deps', 'package-id', 'foreign-cache', 'foreign-package', 'unreachable-official', 'official-version')) {
        $arguments = Copy-Value $graphArgs
        switch ($fault) {
            predeclared { $arguments.Manifest.dependencies['com.kano.ylframework'] = $version }
            'missing-root' { $arguments.Manifest.dependencies.Remove($roots[1]) }
            'second-root-depth' { $arguments.Lock.dependencies[$roots[1]].depth = 1 }
            'boolean-scope-url' { $arguments.Manifest.scopedRegistries[0].url = $true }
            'boolean-lock-url' { $arguments.Lock.dependencies[$entry].url = $true }
            'wrong-source' { $arguments.Rows[0].source = 'Git' }
            'wrong-direct' { $arguments.Rows[0].isDirectDependency = $true }
            'wrong-url' { $arguments.Rows[0].registryUrl = 'https://other.invalid' }
            'lock-depth' { $arguments.Lock.dependencies[$entry].depth = 1 }
            'transitive-lock-depth' { $arguments.Lock.dependencies['com.kano.ylframework.data-assets'].depth = 2 }
            'missing-backend' { $arguments.Rows = @($arguments.Rows | Where-Object name -cne 'com.kano.ylframework.data-assets'); $arguments.Lock.dependencies.Remove('com.kano.ylframework.data-assets') }
            'lock-version' { $arguments.Lock.dependencies[$entry].version = '0.0.1' }
            'lock-deps' { $arguments.Lock.dependencies[$entry].dependencies = @{} }
            'package-id' { $arguments.Rows[0].packageId = $arguments.Rows[0].name + '@file:/untrusted' }
            'foreign-cache' { $arguments.Rows[0].resolvedPath = 'C:\Users\unknown\cache' }
            'foreign-package' { $arguments.Rows += @{ name = 'com.developer.mcp'; version = '1.0.0'; source = 'Registry' } }
            'unreachable-official' { $arguments.Rows += @{ name = 'com.unity.unknown'; version = '1.0.0'; source = 'Registry'; packageId = 'com.unity.unknown@1.0.0'; resolvedPath = 'C:\unused'; isDirectDependency = $false; registryUrl = 'https://packages.unity.com'; dependencies = @{} }; $arguments.Lock.dependencies['com.unity.unknown'] = @{ version = '1.0.0'; source = 'registry'; url = 'https://packages.unity.com'; depth = 1; dependencies = @{} } }
            'official-version' { $arguments.Rows[-1].version = '0.0.1'; $arguments.Lock.dependencies['com.unity.ugui'].version = '0.0.1' }
        }
        Reject { Assert-YLRegistryResolvedGraph @arguments } 'manifest|direct|source|registry|lock|depth|dependenc|package|cache|version|graph|identity'
    }
    if ($ControllerUnity) {
        $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
        $guardWorkspace = Initialize-YLTestWorkspace -WorkspaceRoot (Join-Path $root 'ControllerGuards') -Repository $repository
        $guardRun = New-YLTestDirectory -Workspace $guardWorkspace -Kind Result -Key ControllerPrerequisites
        $guardLease = Lock-YLTestDirectory -Workspace $guardWorkspace -Path $guardRun.path
        try {
            $driver = Join-Path $guardRun.path 'Registry404Fixture.ps1'
            [IO.File]::WriteAllText($driver, @'
param([string]$Controller,[string]$WorkspaceRoot,[string]$Unity,[string]$ReleaseManifest,[string]$Integration)
$ErrorActionPreference = 'Stop'
function Invoke-WebRequest {
    $response = [Net.Http.HttpResponseMessage]::new([Net.HttpStatusCode]::NotFound)
    throw [Microsoft.PowerShell.Commands.HttpResponseException]::new('Synthetic Registry metadata 404; no real network or Editor launch', $response)
}
$parameters = @{ WorkspaceRoot=$WorkspaceRoot; Unity=$Unity; ReleaseManifest=$ReleaseManifest; PackageSource='Registry'; RegistryUrl='https://package.openupm.com'; RegistryVersion='0.1.0-preview.1' }
if ($Integration) { $parameters.Integration=$Integration }
& $Controller @parameters
exit $LASTEXITCODE
'@, [Text.UTF8Encoding]::new($false))
            Save $path $receipt
            foreach ($case in @('DistributionMissingTarball', 'IntegrationMissingTarball', 'DistributionRegistry404', 'IntegrationRegistry404')) {
                $isIntegrationController = $case.StartsWith('Integration', [StringComparison]::Ordinal)
                $controller = Join-Path $PSScriptRoot $(if ($isIntegrationController) { 'Test-YLIntegrationPackages.ps1' } else { 'Test-YLDistribution.ps1' })
                $sourceArgs = @('-WorkspaceRoot', $guardWorkspace.root, '-Unity', $ControllerUnity, '-ReleaseManifest', $path)
                if ($case.EndsWith('Registry404', [StringComparison]::Ordinal)) {
                    $arguments = @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $driver, '-Controller', $controller) + $sourceArgs
                } else {
                    $arguments = @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $controller, '-PackageSource', 'LocalTarball') + $sourceArgs
                }
                if ($isIntegrationController) { $arguments += @('-Integration', 'Excel') }
                $start = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path); $start.UseShellExecute = $false; $start.WorkingDirectory = $repository
                foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
                $fact = Invoke-YLTestProcess -StartInfo $start -Log (Join-Path $guardRun.path ($case + '.log')) -TimeoutMilliseconds 120000
                Check ($fact.status -ceq 'NonZeroExit' -and $fact.exitCode -eq 2 -and $fact.observedExit -and -not $fact.timedOut -and -not $fact.cancelled -and
                    -not $fact.forcedCleanup -and $fact.jobAssigned -and $fact.streamsDrained -and $fact.activeProcesses -eq 0 -and -not $fact.observerError -and -not $fact.cleanupError) "Controller prerequisite mismatch: $case; status=$($fact.status); exit=$($fact.exitCode); log=$($fact.log)"
            }
            $summaries = @(Get-YLTestTreeFiles (Join-Path $guardWorkspace.root 'Results') | Where-Object { [IO.Path]::GetFileName($_) -ceq 'summary.json' })
            Check ($summaries.Count -eq 4) 'Missing tarball/Registry 404 must each retain a separate actual controller summary.'
            foreach ($summaryPath in $summaries) {
                $summary = Read-YLPackageEvidenceJson $summaryPath
                Check ($summary['status'] -ceq 'PrerequisiteNotMet' -and $summary['processes'].Count -eq 0 -and $summary['packages'].Count -eq 0 -and
                    $summary['acceptanceKind'] -cin @('ArtifactPreflight', 'RegistryInstallation')) 'A prerequisite summary invented installation, package or Unity process evidence.'
            }
        } finally { $guardLease.Dispose() }
    }
    "PASS: $checks Registry installation NoUnity source, frozen receipt and dependency graph contracts. NOT Unity installation, compilation or public Registry acceptance."
    $completed = $true
} finally {
    if ($completed -and [IO.Directory]::Exists($root)) { $null = @(Get-YLTestTreeFiles $root); [IO.Directory]::Delete($root, $true) }
    elseif ([IO.Directory]::Exists($root)) { "Failed installation contract evidence retained: $root" }
}
