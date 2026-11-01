#requires -Version 7.6
[CmdletBinding()]
param([Parameter(Mandatory)][string] $TempGoalRoot)
Set-StrictMode -Version 3.0
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($TempGoalRoot)
$allowed=Join-Path $env:LOCALAPPDATA 'Temp/kilo/U63-20261027-c63a1'
if (-not $root.StartsWith($allowed+'\',[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::Exists($root)) { throw 'Fresh owned optional guard root required.' }
for ($cursor=$root; $cursor; $cursor=[IO.Path]::GetDirectoryName($cursor)) {
    if ([IO.Path]::Exists($cursor) -and ([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked optional guard root rejected.' }
}
if (-not [IO.Directory]::Exists((Split-Path $root -Parent))) { throw 'Existing approved guard parent required.' }
$null=[IO.Directory]::CreateDirectory($root)
$entry=Join-Path $PSScriptRoot 'Invoke-Stage9Delivery.ps1'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../..'))
$rows=[Collections.Generic.List[object]]::new()
function Case([string] $Name, [scriptblock] $Action) {
    try { & $Action; $rows.Add(@{ Name=$Name; Passed=$true }) }
    catch { $rows.Add(@{ Name=$Name; Passed=$false; Failure=$_.Exception.Message }) }
}
function Reject([scriptblock] $Action, [string] $Reason) {
    $failure=$null
    try { $null=& $Action } catch { $failure=$_.Exception.Message }
    if ($null -eq $failure -or $failure -notmatch $Reason) { throw "Expected rejection /$Reason/; actual: $failure" }
}
$tokens=$null; $errors=$null
$driver=[Management.Automation.Language.Parser]::ParseFile($entry,[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw ($errors -join [Environment]::NewLine) }
# Only the real parameter guards/AST blocks run; input-binding fixtures are explicitly simulated, never real receivers.
$boundary=[Collections.Generic.List[string]]::new()
foreach ($statement in $driver.EndBlock.Statements) {
    if ($statement -is [Management.Automation.Language.AssignmentStatementAst] -and $statement.Left.Extent.Text -ceq '$root') { break }
    $boundary.Add($statement.Extent.Text)
}
$context=[scriptblock]::Create($driver.ParamBlock.Extent.Text+"`n"+($boundary -join "`n"))
function Driver-Block([string] $Condition) {
    $found=@($driver.EndBlock.Statements | Where-Object {
        $_ -is [Management.Automation.Language.IfStatementAst] -and $_.Clauses[0].Item1.Extent.Text -ceq $Condition
    })
    if ($found.Count -ne 1) { throw "Exactly one driver block required: $Condition" }
    [scriptblock]::Create($found[0].Extent.Text)
}
foreach ($name in @('Hash','Reject-Link','Json','Get-Stage9TestInputFiles','Assert-Stage9TestBinding')) {
    $function=@($driver.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -ceq $name })
    if ($function.Count -ne 1) { throw "Missing pure driver function: $name" }
    . ([scriptblock]::Create($function[0].Extent.Text))
}
$install=Driver-Block '$Operation -cin @(''InstallOptional'',''InstallOptionalTests'')'
$sync=Driver-Block '$Operation -ceq ''SyncOptionalTests'''
$tests=Driver-Block '$Operation -eq ''Tests'''
function Receiver-PackageHash { 'guard-package-sha256' }
function Run-Unity([string[]] $Arguments, [string] $Log) { throw 'Guard boundary: Unity is never invoked.' }
$cases=@(
    @{ Name='missing-integration'; Arguments=@{ Operation='InstallOptional' }; Error='Explicit optional integration required' },
    @{ Name='initialize-not-optional'; Arguments=@{ Operation='Initialize'; OptionalIntegration='Excel' }; Error='Optional integration is only valid for explicit install, discovery or test operations' },
    @{ Name='player-not-optional'; Arguments=@{ Operation='Player'; OptionalIntegration='Excel' }; Error='Optional integration is only valid for explicit install, discovery or test operations' },
    @{ Name='unrelated-editor-entry'; Arguments=@{ Operation='Editor'; Method='TestsNew'; OptionalIntegration='Excel' }; Error='Optional discovery requires TestsDiscover' },
    @{ Name='excel-editor-only'; Arguments=@{ Operation='Tests'; TestMode='PlayMode'; OptionalIntegration='Excel' }; Error='Editor-only optional tests require EditMode' },
    @{ Name='domain-editor-only'; Arguments=@{ Operation='Tests'; TestMode='PlayMode'; OptionalIntegration='DomainExcel' }; Error='Editor-only optional tests require EditMode' },
    @{ Name='optional-tests-require-integration'; Arguments=@{ Operation='InstallOptionalTests' }; Error='Explicit optional integration required' },
    @{ Name='optional-sync-require-integration'; Arguments=@{ Operation='SyncOptionalTests' }; Error='Explicit optional integration required' },
    @{ Name='dotween-player-not-optional'; Arguments=@{ Operation='Player'; OptionalIntegration='DOTween' }; Error='Optional integration is only valid' },
    @{ Name='dotween-unrelated-editor'; Arguments=@{ Operation='Editor'; Method='TestsNew'; OptionalIntegration='DOTween' }; Error='Optional discovery requires TestsDiscover' },
    @{ Name='dotween-macro-needs-scope'; Arguments=@{ Operation='Editor'; Method='TestsDOTween' }; Error='DOTween setup requires its explicit Editor scope' },
    @{ Name='dotween-macro-wrong-scope'; Arguments=@{ Operation='Editor'; Method='TestsDOTween'; OptionalIntegration='DomainExcel' }; Error='Optional discovery requires TestsDiscover' },
    @{ Name='dotween-macro-wrong-operation'; Arguments=@{ Operation='Tests'; Method='TestsDOTween'; OptionalIntegration='DOTween' }; Error='DOTween setup requires its explicit Editor scope' },
    @{ Name='domain-macro-wrong-dotween'; Arguments=@{ Operation='Editor'; Method='TestsDomainExcel'; OptionalIntegration='DOTween' }; Error='Optional discovery requires TestsDiscover' },
    @{ Name='domain-macro-needs-scope'; Arguments=@{ Operation='Editor'; Method='TestsDomainExcel' }; Error='DomainExcel setup requires its explicit Editor scope' }
)
foreach ($case in $cases) {
    $arguments=$case.Arguments.Clone()
    $arguments.DeliveryRoot=$root
    Case $case.Name { Reject { & $context @arguments } $case.Error }
}
foreach ($arguments in @(
    @{ Operation='Tests'; TestMode='EditMode' },
    @{ Operation='Tests'; TestMode='PlayMode' },
    @{ Operation='Tests'; TestMode='EditMode'; OptionalIntegration='Excel' },
    @{ Operation='Tests'; TestMode='EditMode'; OptionalIntegration='DomainExcel' },
    @{ Operation='Editor'; Method='TestsDomainExcel'; OptionalIntegration='DomainExcel' },
    @{ Operation='InstallOptional'; OptionalIntegration='DOTween' },
    @{ Operation='InstallOptionalTests'; OptionalIntegration='DOTween' },
    @{ Operation='SyncOptionalTests'; OptionalIntegration='DOTween' },
    @{ Operation='Editor'; Method='TestsDOTween'; OptionalIntegration='DOTween' },
    @{ Operation='Editor'; Method='TestsDiscover'; OptionalIntegration='DOTween' },
    @{ Operation='Tests'; TestMode='EditMode'; OptionalIntegration='DOTween' },
    @{ Operation='Tests'; TestMode='PlayMode'; OptionalIntegration='DOTween' },
    @{ Operation='InstallOptional'; OptionalIntegration='HybridCLR' },
    @{ Operation='InstallOptionalTests'; OptionalIntegration='HybridCLR' },
    @{ Operation='Editor'; Method='TestsDiscover'; OptionalIntegration='HybridCLR' },
    @{ Operation='Tests'; TestMode='EditMode'; OptionalIntegration='HybridCLR' },
    @{ Operation='Tests'; TestMode='PlayMode'; OptionalIntegration='HybridCLR' },
    @{ Operation='Editor'; Method='TestsCoexistence' }
)) {
    $arguments.DeliveryRoot=$root
    Case ('accept context '+($arguments | ConvertTo-Json -Compress)) { & $context @arguments }
}
function Fixture([string] $Name) {
    $path=Join-Path ([IO.Path]::GetFullPath($TempGoalRoot)) $Name
    $null=[IO.Directory]::CreateDirectory($path)
    $script:root=$path; $script:repo=$repository; $script:project=Join-Path $path 'destination'
    $null=[IO.Directory]::CreateDirectory((Join-Path $project 'Assets/YLFramework'))
    foreach ($directory in @('Packages','ProjectSettings')) { $null=[IO.Directory]::CreateDirectory((Join-Path $project $directory)) }
    foreach ($relative in @('stage9-owner.json','Packages/manifest.json','Packages/packages-lock.json','ProjectSettings/ProjectSettings.asset','ProjectSettings/EditorSettings.asset')) {
        [IO.File]::WriteAllText((Join-Path $project $relative),'{}',[Text.UTF8Encoding]::new($false))
    }
    [IO.File]::WriteAllText((Join-Path $project 'stage9-owner.json'),'{"Purpose":"Stage9-input-binding-guard-fixture","Simulation":true}',[Text.UTF8Encoding]::new($false))
    foreach ($relative in @('Assets/YLFramework.meta','Assets/YLFramework/LICENSE.md','Assets/YLFramework/LICENSE.md.meta')) {
        [IO.File]::Copy((Join-Path $repo $relative),(Join-Path $project $relative),$false)
    }
    $script:Receiver='guard'; $script:ImportRun='bare'; $script:Run='candidate'
    $script:ExpectedUnityVersion='6000.3.27f1'; $script:Package='guard.unitypackage'
    Json (Join-Path $path 'guard-first-import-bare.json') @{ Passed=$true; HarnessPresent=$false; Project=$project
        PackageSha256='guard-package-sha256'; Package=$Package; UnityVersion=$ExpectedUnityVersion; Simulation=$true }
}
function Bound-Discovery([hashtable]$Value) {
    $Value.Schema='Stage9TestDiscovery-v2'; $Value.OwnerSha256=Hash (Join-Path $project 'stage9-owner.json'); $Value.PackageSha256='guard-package-sha256'
    $Value.InputFiles=@(Get-Stage9TestInputFiles $project); $Value.CompilationSha256='c'*64; $Value.ClassifierSha256='d'*64
    $Value.InputMode=0; $Value.EnterPlayModeOptionsEnabled=$false; $Value.EnterPlayModeOptions='None'
    return $Value
}
function Bound-Profile([hashtable]$Value) {
    $Value.Schema='Stage9TestProfile-v2'; $Value.OwnerSha256=Hash (Join-Path $project 'stage9-owner.json'); $Value.PackageSha256='guard-package-sha256'
    $Value.ClassifierSha256='d'*64; $Value.InputMode=0; $Value.EnterPlayModeOptionsEnabled=$false; $Value.EnterPlayModeOptions='None'
    return $Value
}
Case 'local fixed vendor copy preserves wrapper, metadata and license without a package' {
    Fixture 'local-copy'
    $Operation='InstallOptional'; $OptionalIntegration='DOTween'
    $null=& $install
    $receipt=Get-Content -LiteralPath (Join-Path $root 'guard-optional-DOTween-candidate.json') -Raw | ConvertFrom-Json
    if (-not $receipt.LocalDependencyCopy -or $receipt.VendorRedistributionAuthorized -or $receipt.DefaultPackageChanged -or
        $receipt.Project -cne $project -or $receipt.Unity -cne $ExpectedUnityVersion) { throw 'Wrong local dependency receipt boundary.' }
    $vendor=@($receipt.Files | Where-Object Path -CEQ 'Assets/Plugins/Demigiant/DOTween/DOTween.dll')
    if ($vendor.Count -ne 1 -or $vendor[0].Sha256 -cne 'd74bfc3f37cd97d90f7f08a9a73e204c5eae66e77be24b12378a4e99f1690843') { throw 'Fixed official vendor was not copied.' }
    foreach ($file in $receipt.Files) { if ((Hash (Join-Path $project $file.Path)) -cne (Hash (Join-Path $repo $file.Path))) { throw 'Copied source/meta bytes differ.' } }
    if ([IO.Directory]::GetFiles($root,'*.unitypackage',[IO.SearchOption]::AllDirectories).Count) { throw 'Local copy must never export a package.' }
}
Case 'local vendor runtime closure includes all original modules and readme' {
    $root=Join-Path $TempGoalRoot 'local-copy'; $project=Join-Path $root 'destination'; $repo=$repository
    foreach ($relative in @('Modules/DOTweenModuleAudio.cs','Modules/DOTweenModuleEPOOutline.cs','Modules/DOTweenModulePhysics.cs',
        'Modules/DOTweenModulePhysics2D.cs','Modules/DOTweenModuleSprite.cs','Modules/DOTweenModuleUI.cs',
        'Modules/DOTweenModuleUIToolkit.cs','Modules/DOTweenModuleUnityVersion.cs','Modules/DOTweenModuleUtils.cs','readme.txt')) {
        foreach ($suffix in @('','.meta')) {
            $path='Assets/Plugins/Demigiant/DOTween/'+$relative+$suffix
            if (-not [IO.File]::Exists((Join-Path $project $path)) -or (Hash (Join-Path $project $path)) -cne (Hash (Join-Path $repo $path))) {
                throw "Incomplete official runtime dependency: $path"
            }
        }
    }
}
foreach ($kind in @('missing','modified')) {
    Case ('local dependency rejects '+$kind+' official module before writing') {
        Fixture ('module-'+$kind)
        $repo=Join-Path $root 'source'
        $inputs=Get-Content -LiteralPath (Join-Path $TempGoalRoot 'local-copy/guard-optional-DOTween-candidate.json') -Raw | ConvertFrom-Json
        foreach ($relative in @($inputs.Files.Path)+@('Assets/YLFramework.meta','Assets/YLFramework/LICENSE.md','Assets/YLFramework/LICENSE.md.meta')) {
            $destination=Join-Path $repo $relative
            $null=[IO.Directory]::CreateDirectory((Split-Path $destination -Parent))
            [IO.File]::Copy((Join-Path $repository $relative),$destination,$false)
        }
        $module=Join-Path $repo 'Assets/Plugins/Demigiant/DOTween/Modules/DOTweenModuleUtils.cs'
        if ($kind -ceq 'missing') { [IO.File]::Delete($module) }
        else {
            $null=[IO.Directory]::CreateDirectory((Split-Path $module -Parent))
            [IO.File]::WriteAllText($module,'modified module')
        }
        $Operation='InstallOptional'; $OptionalIntegration='DOTween'
        Reject { & $install } 'Exact local official DOTween 1.2.825 runtime dependency required'
        if ([IO.Directory]::Exists((Join-Path $project 'Assets/Plugins')) -or
            [IO.Directory]::Exists((Join-Path $project 'Assets/YLFramework/Integrations'))) { throw 'Rejected modules wrote optional files.' }
    }
}
Case 'optional copy preserves an independently imported product root folder identity' {
    Fixture 'independent-product-root'
    $meta=Join-Path $project 'Assets/YLFramework.meta'
    [IO.File]::Copy((Join-Path $repo 'Assets/YLFramework/Runtime.meta'),$meta,$true)
    $before=Hash $meta
    $Operation='InstallOptional'; $OptionalIntegration='DOTween'
    $null=& $install
    $receipt=Get-Content -LiteralPath (Join-Path $root 'guard-optional-DOTween-candidate.json') -Raw | ConvertFrom-Json
    if ((Hash $meta) -cne $before -or $receipt.ProductRootMetaSha256 -cne $before) { throw 'Independent product root identity was replaced or not recorded.' }
}
foreach ($kind in @('missing','non-folder')) {
    Case ('optional copy rejects '+$kind+' product root metadata before writing') {
        Fixture ('invalid-product-root-'+$kind)
        $meta=Join-Path $project 'Assets/YLFramework.meta'
        if ($kind -ceq 'missing') { [IO.File]::Delete($meta) }
        else { [IO.File]::Copy((Join-Path $repo 'Assets/YLFramework/LICENSE.md.meta'),$meta,$true) }
        $Operation='InstallOptional'; $OptionalIntegration='DOTween'
        Reject { & $install } 'Imported product root folder identity required'
        if ([IO.Directory]::Exists((Join-Path $project 'Assets/Plugins')) -or
            [IO.Directory]::Exists((Join-Path $project 'Assets/YLFramework/Integrations'))) { throw 'Invalid product root wrote optional files.' }
    }
}
Case 'optional DOTween tests include both real assemblies, all code and metadata' {
    $root=Join-Path $TempGoalRoot 'local-copy'; $project=Join-Path $root 'destination'; $repo=$repository
    $Operation='InstallOptionalTests'; $OptionalIntegration='DOTween'; $Receiver='guard'; $ImportRun='bare'; $Run='tests'
    $ExpectedUnityVersion='6000.3.27f1'; $Package='guard.unitypackage'
    $null=& $install
    $receipt=Get-Content -LiteralPath (Join-Path $root 'guard-optional-tests-DOTween-tests.json') -Raw | ConvertFrom-Json
    $source=Join-Path $repo 'Assets/YLFramework/Tests/Integrations/DOTween'
    foreach ($file in [IO.Directory]::GetFiles($source,'*',[IO.SearchOption]::AllDirectories)) {
        $relative=[IO.Path]::GetRelativePath($repo,$file).Replace('\','/')
        $copied=@($receipt.Files | Where-Object Path -CEQ $relative)
        if ($copied.Count -ne 1 -or $copied[0].Sha256 -cne (Hash $file)) { throw "Missing test source/meta: $relative" }
    }
    if (@($receipt.Files | Where-Object Path -Like '*.asmdef').Count -ne 2 -or @($receipt.Files | Where-Object Path -Like '*.cs').Count -ne 3) { throw 'Incomplete two-platform test closure.' }
}
Case 'optional test sync accepts only receipt-owned source and preserves parent metadata' {
    $root=Join-Path $TempGoalRoot 'local-copy'; $project=Join-Path $root 'destination'; $repo=$repository
    $Operation='SyncOptionalTests'; $OptionalIntegration='DOTween'; $Receiver='guard'; $ProofRun='tests'; $Run='sync'
    $ExpectedUnityVersion='6000.3.27f1'; $Package='guard.unitypackage'
    $before=Hash (Join-Path $project 'Assets/YLFramework/Tests/Integrations.meta')
    $null=& $sync
    if ((Hash (Join-Path $project 'Assets/YLFramework/Tests/Integrations.meta')) -cne $before -or
        -not [IO.File]::Exists((Join-Path $root 'guard-optional-tests-DOTween-sync.json'))) { throw 'Optional source sync lost metadata or receipt.' }
}
Case 'DOTween tests reject absent wrapper before writing' {
    Fixture 'missing-wrapper'
    $Operation='InstallOptionalTests'; $OptionalIntegration='DOTween'
    Reject { & $install } 'Install the exact DOTween wrapper and local dependency before its tests'
    if ([IO.Directory]::Exists((Join-Path $project 'Assets/YLFramework/Tests'))) { throw 'Rejection wrote optional tests.' }
}
Case 'DOTween rejects the wrong bare package proof before writing' {
    Fixture 'wrong-proof'
    $path=Join-Path $root 'guard-first-import-bare.json'
    $proof=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $proof.PackageSha256='other'
    [IO.File]::WriteAllText($path,($proof | ConvertTo-Json))
    $Operation='InstallOptional'; $OptionalIntegration='DOTween'
    Reject { & $install } 'Wrong bare-product predecessor'
}
foreach ($field in @('Package','UnityVersion','Project','HarnessPresent')) {
    Case ('DOTween rejects bare proof field '+$field) {
        Fixture ('wrong-proof-'+$field)
        $path=Join-Path $root 'guard-first-import-bare.json'
        $proof=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $proof.$field=if ($field -ceq 'HarnessPresent') { $true } else { 'wrong' }
        [IO.File]::WriteAllText($path,($proof | ConvertTo-Json))
        $Operation='InstallOptional'; $OptionalIntegration='DOTween'
        Reject { & $install } 'Wrong bare-product predecessor'
    }
}
Case 'DOTween rejects an occupied vendor target before copying wrapper' {
    Fixture 'occupied-vendor'
    $vendor=Join-Path $project 'Assets/Plugins/Demigiant/DOTween/DOTween.dll'
    $null=[IO.Directory]::CreateDirectory((Split-Path $vendor -Parent)); [IO.File]::WriteAllText($vendor,'foreign')
    $Operation='InstallOptional'; $OptionalIntegration='DOTween'
    Reject { & $install } 'Never adopt or overwrite'
    if ([IO.Directory]::Exists((Join-Path $project 'Assets/YLFramework/Integrations'))) { throw 'Preflight copied a partial wrapper.' }
}
Case 'DOTween rejects an unknown optional parent before copying vendor' {
    Fixture 'unknown-parent'
    $null=[IO.Directory]::CreateDirectory((Join-Path $project 'Assets/YLFramework/Integrations'))
    [IO.File]::WriteAllText((Join-Path $project 'Assets/YLFramework/Integrations.meta'),'unknown')
    $Operation='InstallOptional'; $OptionalIntegration='DOTween'
    Reject { & $install } 'Existing optional parent identity differs'
    if ([IO.Directory]::Exists((Join-Path $project 'Assets/Plugins'))) { throw 'Preflight copied a partial vendor tree.' }
}
Case 'DOTween rejects modified local DLL bytes without loading them' {
    Fixture 'wrong-vendor'
    $repo=Join-Path $root 'source'
    $dll=Join-Path $repo 'Assets/Plugins/Demigiant/DOTween/DOTween.dll'
    $null=[IO.Directory]::CreateDirectory((Split-Path $dll -Parent)); [IO.File]::WriteAllText($dll,'not the official DLL')
    $Operation='InstallOptional'; $OptionalIntegration='DOTween'
    Reject { & $install } 'Exact local official DOTween 1.2.825 dependency required'
    if ([IO.Directory]::Exists((Join-Path $project 'Assets/YLFramework/Integrations'))) { throw 'Bad vendor wrote wrapper.' }
}
Case 'DOTween rejects a linked vendor destination parent without traversal' {
    Fixture 'linked-vendor'
    $link=Join-Path $project 'Assets/Plugins'; $destination=Join-Path $root 'foreign-target'
    $null=[IO.Directory]::CreateDirectory($destination)
    $null=New-Item -ItemType Junction -Path $link -Target $destination
    $Operation='InstallOptional'; $OptionalIntegration='DOTween'
    Reject { & $install } 'Linked path rejected'
    if ([IO.Directory]::GetFileSystemEntries($destination).Count) { throw 'Linked target was modified.' }
}
Case 'DOTween source changed after the fixed hash check cannot receive an installation receipt' {
    Fixture 'changed-vendor'
    $repo=Join-Path $root 'source'
    $receipt=Get-Content -LiteralPath (Join-Path $TempGoalRoot 'local-copy/guard-optional-DOTween-candidate.json') -Raw | ConvertFrom-Json
    foreach ($relative in @($receipt.Files.Path)+@('Assets/YLFramework.meta','Assets/YLFramework/LICENSE.md','Assets/YLFramework/LICENSE.md.meta')) {
        $target=Join-Path $repo $relative
        $null=[IO.Directory]::CreateDirectory((Split-Path $target -Parent))
        [IO.File]::Copy((Join-Path $repository $relative),$target,$false)
    }
    $sourceDll=Join-Path $repo 'Assets/Plugins/Demigiant/DOTween/DOTween.dll'
    $script:swapVendor=$true
    function Hash([string] $Path) {
        $value=(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($Path -ceq $sourceDll -and $script:swapVendor) {
            $bytes=[IO.File]::ReadAllBytes($Path); $bytes[0]=$bytes[0] -bxor 1
            [IO.File]::WriteAllBytes($Path,$bytes); $script:swapVendor=$false
        }
        $value
    }
    $Operation='InstallOptional'; $OptionalIntegration='DOTween'
    Reject { & $install } 'Exact local official DOTween 1.2.825 (runtime )?dependency required'
    if ([IO.File]::Exists((Join-Path $root 'guard-optional-DOTween-candidate.json'))) { throw 'Changed vendor was recorded as the approved fixed dependency.' }
}
Case 'Excel and DomainExcel installation and test sync retain their separate existing closures' {
    Fixture 'excel-domain-regression'
    $Operation='InstallOptional'; $OptionalIntegration='Excel'; $null=& $install
    $OptionalIntegration='DomainExcel'; $null=& $install
    $Operation='InstallOptionalTests'; $null=& $install
    $Operation='SyncOptionalTests'; $ProofRun='candidate'; $Run='sync'; $null=& $sync
    foreach ($relative in @('Assets/YLFramework/Integrations/Excel/Editor/Plugins/MiniExcel.dll',
        'Assets/YLFramework/Integrations/DomainExcel/Editor/YLFramework.Domain.Excel.Editor.asmdef',
        'Assets/YLFramework/Tests/Integrations/DomainExcel/EditMode/YLFramework.Domain.Excel.Tests.asmdef')) {
        if ((Hash (Join-Path $project $relative)) -cne (Hash (Join-Path $repo $relative))) { throw 'Existing optional closure changed.' }
    }
    if ([IO.Directory]::Exists((Join-Path $project 'Assets/Plugins/Demigiant'))) { throw 'DOTween leaked into Excel/DomainExcel installation.' }
}
foreach ($mode in @('EditMode','PlayMode')) {
    foreach ($optional in @('','Excel','DomainExcel','DOTween')) {
        if ($mode -ceq 'PlayMode' -and $optional -cin @('Excel','DomainExcel')) { continue }
        Case ("selection $mode / '$optional' preserves exact scope with profile") {
            Fixture ('selection-'+$mode+'-'+$(if ($optional) { $optional } else { 'default' }))
            $Operation='Tests'; $OptionalIntegration=$optional; $TestMode=$mode; $ProofRun='list'; $ProfileRun='profile'
            $TestFilter='.*'; $TestExclusion=$null; $ExpectedTestCount=1
            $CostEvidenceRoot=$null; $CostScope=$null; $CostPurpose=$null
            $dotAssembly='YLFramework.Actions.DOTween.Tests.'+$mode
            $set=@(
                @{ Name='Ordinary.Case'; Assembly='YLFramework.Tests.'+$mode; RunState='Runnable'; Scope='receiver' },
                @{ Name='Excel.Case'; Assembly='YLFramework.Tests.EditMode'; RunState='Runnable'; Scope='optional-excel' },
                @{ Name='Domain.Case'; Assembly='YLFramework.Domain.Excel.Tests'; RunState='Runnable'; Scope='optional-domainexcel' },
                @{ Name='DOTween.Case(value)'; Assembly=$dotAssembly; RunState='Runnable'; Scope='optional-dotween' }
            )
            Json (Join-Path $root 'guard-tests-profile-profile.json') (Bound-Profile @{ Project=$project; Unity=$ExpectedUnityVersion; HostOnly=@(); Historical=@(); Excel=@('Excel') })
            Json (Join-Path $root 'guard-tests-discovery-list.json') (Bound-Discovery @{ Passed=$true; SourceHost=$false; Project=$project; Unity=$ExpectedUnityVersion; OptionalIntegration=$optional; EditMode=$set; PlayMode=$set })
            Reject { & $tests } 'Guard boundary: Unity is never invoked'
            $selection=Get-Content -LiteralPath (Join-Path $root ('guard-tests-'+$mode+'-candidate.selection.json')) -Raw | ConvertFrom-Json
            $scope=switch -CaseSensitive ($optional) { 'Excel' { 'optional-excel' }; 'DomainExcel' { 'optional-domainexcel' }; 'DOTween' { 'optional-dotween' }; default { 'receiver' } }
            if ($selection.Scope -cne $scope -or @($selection.Expected).Count -ne 1 -or $selection.Expected[0].Scope -cne $scope) { throw 'Profile leaked or erased an optional scope.' }
            if ($optional -ceq 'DOTween' -and ($selection.Expected[0].Assembly -cne $dotAssembly -or -not [regex]::IsMatch('DOTween.Case(value)',$selection.NativeFilter) -or
                [regex]::IsMatch('DOTween.Case(value).Other',$selection.NativeFilter))) { throw 'DOTween filter is not the exact selected fullname.' }
        }
    }
}
foreach ($mismatch in @('wrong-context','zero-tests','partial-count')) {
    Case ('DOTween selection rejects '+$mismatch) {
        Fixture ('selection-reject-'+$mismatch)
        $Operation='Tests'; $OptionalIntegration='DOTween'; $TestMode='PlayMode'; $ProofRun='list'; $ProfileRun=$null
        $TestFilter=if ($mismatch -ceq 'zero-tests') { '^absent$' } else { '.*' }
        $TestExclusion=$null; $ExpectedTestCount=if ($mismatch -ceq 'partial-count') { 79 } else { 1 }
        $CostEvidenceRoot=$null; $CostScope=$null; $CostPurpose=$null
        $set=@(@{ Name='DOTween.Case'; Assembly='YLFramework.Actions.DOTween.Tests.PlayMode'; RunState='Runnable'; Scope='optional-dotween' })
        $optional=if ($mismatch -ceq 'wrong-context') { 'Excel' } else { 'DOTween' }
        Json (Join-Path $root 'guard-tests-discovery-list.json') (Bound-Discovery @{ Passed=$true; SourceHost=$false; Project=$project; Unity=$ExpectedUnityVersion; OptionalIntegration=$optional; PlayMode=$set })
        $reason=switch ($mismatch) { 'wrong-context' { 'Exact optional discovery context required' }; 'zero-tests' { 'selected no applicable discovered tests' }; 'partial-count' { 'expected 79 cases' } }
        Reject { & $tests } $reason
        if ([IO.Directory]::GetFiles($root,'*.selection.json').Count) { throw 'Rejected intent wrote a test selection.' }
    }
}
$root=[IO.Path]::GetFullPath($TempGoalRoot)
Case 'existing Roslyn parses reception CSharp9 without Unity execution' {
    $roslyn='C:/Program Files/dotnet/sdk/9.0.312/Roslyn/bincore'
    foreach ($dll in @('Microsoft.CodeAnalysis.dll','Microsoft.CodeAnalysis.CSharp.dll')) { $null=[Reflection.Assembly]::LoadFrom((Join-Path $roslyn $dll)) }
    $text=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Editor/Stage9ReceptionTests.cs'))
    $options=[Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::new([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::CSharp9)
    $script:tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($text,$options)
    $syntax=@($tree.GetDiagnostics() | Where-Object Severity -EQ Error)
    if ($syntax.Count) { throw ($syntax -join [Environment]::NewLine) }
}
Case 'both PowerShell entry and guard scripts parse without execution' {
    foreach ($path in @($entry,$PSCommandPath)) {
        $tokens=$null; $errors=$null
        $null=[Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
        if ($errors.Count) { throw ($errors -join [Environment]::NewLine) }
    }
}
Case 'real DOTween source retains the explicit 1 EditMode and 57 plus 22 PlayMode leaf expectations' {
    $script:sourceLeaves=@{}
    foreach ($pair in @(@('EditMode/DOTweenApiContractTests.cs',1),@('PlayMode/DOTweenActionTests.cs',57),@('PlayMode/DOTweenActionPlayModeTests.cs',22))) {
        $path=Join-Path $repository ('Assets/YLFramework/Tests/Integrations/DOTween/'+$pair[0])
        $sourceTree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($path))
        $attributes=@($sourceTree.GetRoot().DescendantNodes() | Where-Object {
            $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.AttributeSyntax] -and $_.Name.ToString() -cin @('Test','TestCase','UnityTest')
        })
        if ($attributes.Count -ne $pair[1]) { throw "Source leaf expectation changed: $($pair[0]) expected $($pair[1]), found $($attributes.Count)" }
        $sourceLeaves[$pair[0]]=$attributes.Count
    }
}
Case 'compile the real discovery and optional setup logic against offline-only boundary doubles' {
    $members=@($tree.GetRoot().DescendantNodes() | Where-Object {
        ($_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax] -and $_.Declaration.Variables[0].Identifier.ValueText -cin @('TestAssemblies','HostOnly','Historical','PlatformBound','Excel')) -or
        ($_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -cin @('ExpectedTestAssemblies','Leaves','Matches','TestsDOTween','FileHash'))
    })
    if ($members.Count -ne 10) { throw 'Missing explicit expected-assembly/setup contract.' }
    $graphCheck=@($tree.GetRoot().DescendantNodes() | Where-Object {
        $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ForEachStatementSyntax] -and $_.Identifier.ValueText -ceq 'name' -and
        $_.Expression.ToString().StartsWith('expectedAssemblies.Concat(',[StringComparison]::Ordinal)
    })
    if ($graphCheck.Count -ne 1) { throw 'Exactly one real compilation-graph gate required.' }
    $harness=@'
using System; using System.IO; using System.Linq; using System.Collections.Generic; using System.Security.Cryptography;
using System.Text.RegularExpressions; using Newtonsoft.Json; using Newtonsoft.Json.Linq;
public static class Stage9OptionalAssemblyGuard {
    public static string Project, Run, Optional;
    public static JObject Owner;
    public static bool Verified;
    private static void VerifyReceiver() { Verified = true; }
    private static string Argument(string name) => name == "-s9-optional-integration" ? Optional : null;
    public static class Application { public static bool isBatchMode = true; public static string unityVersion = "6000.3.27f1"; }
    public static class EditorApplication { public static bool isPlayingOrWillChangePlaymode, isCompiling, isUpdating; }
    public static class EditorUtility { public static bool scriptCompilationFailed; }
    public static class EditorSceneManager { public static int sceneCount = 0; public static Scene GetSceneAt(int index) => new Scene(); }
    public class Scene { public bool isDirty; }
    public enum BuildTargetGroup { Standalone }
    public static class PlayerSettings {
        public static string Symbols;
        public static string GetScriptingDefineSymbolsForGroup(BuildTargetGroup group) => Symbols;
        public static void SetScriptingDefineSymbolsForGroup(BuildTargetGroup group, string value) { Symbols = value; }
    }
    public interface ITestAdaptor {
        bool IsTestAssembly { get; } bool IsSuite { get; } string Name { get; } string FullName { get; }
        string RunState { get; } string SkipReason { get; } IEnumerable<ITestAdaptor> Children { get; }
    }
    private class Node : ITestAdaptor {
        public bool IsTestAssembly { get; set; } public bool IsSuite { get; set; } public string Name { get; set; }
        public string FullName { get; set; } public string RunState => "Runnable"; public string SkipReason => null;
        public IEnumerable<ITestAdaptor> Children { get; set; } = Array.Empty<ITestAdaptor>();
    }
    public static string[] Read(string optional) => ExpectedTestAssemblies(optional);
    public static string[] ReadDefault() => ExpectedTestAssemblies(null);
    public static string Scope(string optional, string assembly, string name) {
        var root = new Node { IsTestAssembly = true, IsSuite = true, Name = assembly + ".dll",
            Children = new[] { new Node { Name = name, FullName = name } } };
        return (string)Leaves(root, null, ExpectedTestAssemblies(optional)).Single()["Scope"];
    }
    public static void Setup(string project, string delivery, string run, string optional, string symbols) {
        Project = project; Run = run; Optional = optional; Verified = false; PlayerSettings.Symbols = symbols;
        Owner = new JObject { ["Delivery"] = delivery, ["PackageSha256"] = "guard-package-sha256" };
        TestsDOTween();
    }
'@
    $source=$harness+"`n"+(($members | ForEach-Object { $_.ToFullString() }) -join "`n")+
        ' public static void CheckGraph(string optional, string[] names) { var expectedAssemblies = ExpectedTestAssemblies(optional); var graph = names.Select(name => new { name }).ToArray(); '+
        $graphCheck[0].ToFullString()+"`n} }"
    $references=@(([AppContext]::GetData('TRUSTED_PLATFORM_ASSEMBLIES').Split([IO.Path]::PathSeparator)+[Newtonsoft.Json.Linq.JObject].Assembly.Location) |
        Sort-Object -Unique | ForEach-Object { [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($_) })
    $compilation=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('Stage9OptionalAssemblyGuard',
        [Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source)),
        [Microsoft.CodeAnalysis.MetadataReference[]]$references,
        [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary))
    $buffer=[IO.MemoryStream]::new()
    try {
        $emitted=$compilation.Emit($buffer)
        $script:offlineDiagnostics=@($emitted.Diagnostics | ForEach-Object { $_.ToString() })
        if (-not $emitted.Success) { throw ($offlineDiagnostics -join [Environment]::NewLine) }
        $null=[Reflection.Assembly]::Load($buffer.ToArray())
    } finally { $buffer.Dispose() }
}
Case 'discovery keeps mandatory DOTween assemblies even before macro compilation' {
    $expected=@('YLFramework.Actions.DOTween.Tests.EditMode','YLFramework.Actions.DOTween.Tests.PlayMode')
    $actual=[Stage9OptionalAssemblyGuard]::Read('DOTween')
    if ($actual.Count -ne 9 -or @($expected | Where-Object { $actual -cnotcontains $_ }).Count -ne 0) { throw 'Macro-hidden test assemblies disappeared from discovery requirements.' }
    if ([Stage9OptionalAssemblyGuard]::ReadDefault().Count -ne 7 -or [Stage9OptionalAssemblyGuard]::Read('DomainExcel').Count -ne 8) { throw 'Default/DomainExcel discovery contract regressed.' }
    Reject { [Stage9OptionalAssemblyGuard]::Read('unknown') } 'Unknown explicit optional test scope'
}
Case 'real discovery graph gate rejects macro-hidden or half-present DOTween test assemblies' {
    $base=[Stage9OptionalAssemblyGuard]::ReadDefault()+@('YLFramework.UI.Acceptance','YLFramework.Core.Tests.Fixtures.Editor',
        'YLFramework.Behavior.Acceptance','YLFramework.Behavior.Acceptance.Editor')
    Reject { [Stage9OptionalAssemblyGuard]::CheckGraph('DOTween',$base) } 'Required test/helper assembly is absent: YLFramework.Actions.DOTween.Tests.EditMode'
    Reject { [Stage9OptionalAssemblyGuard]::CheckGraph('DOTween',($base+@('YLFramework.Actions.DOTween.Tests.EditMode'))) } 'Required test/helper assembly is absent: YLFramework.Actions.DOTween.Tests.PlayMode'
    [Stage9OptionalAssemblyGuard]::CheckGraph('DOTween',($base+@('YLFramework.Actions.DOTween.Tests.EditMode','YLFramework.Actions.DOTween.Tests.PlayMode')))
    [Stage9OptionalAssemblyGuard]::CheckGraph([NullString]::Value,$base)
}
foreach ($mode in @('EditMode','PlayMode')) {
    Case ('real Leaves classifies DOTween '+$mode+' by exact assembly before name rules') {
        $scope=[Stage9OptionalAssemblyGuard]::Scope('DOTween',('YLFramework.Actions.DOTween.Tests.'+$mode),'YLFramework.Tests.DomainMainAcceptanceTests.Case')
        if ($scope -cne 'optional-dotween') { throw 'Optional assembly lost its exclusive scope.' }
    }
}
Case 'macro setup preserves existing symbols and records a restart, not a compilation pass' {
    $project=Join-Path $TempGoalRoot 'local-copy/destination'; $delivery=Join-Path $TempGoalRoot 'macro-setup'
    $null=[IO.Directory]::CreateDirectory($delivery)
    [Stage9OptionalAssemblyGuard]::Setup($project,$delivery,'setup','DOTween','EXISTING;YL_DOMAIN_EXCEL')
    $receipt=Get-Content -LiteralPath (Join-Path $delivery 'destination-dotween-setup.json') -Raw | ConvertFrom-Json
    if (-not [Stage9OptionalAssemblyGuard]::Verified -or $receipt.Before -cne 'EXISTING;YL_DOMAIN_EXCEL' -or
        $receipt.After -cne 'EXISTING;YL_DOMAIN_EXCEL;DOTWEEN' -or $receipt.Project -cne $project -or $receipt.Unity -cne '6000.3.27f1' -or
        -not $receipt.LocalDependencyCopy -or $receipt.VendorRedistributionAuthorized -or -not $receipt.RestartRequired -or $receipt.CompiledAfterRestart) { throw 'Optional macro receipt lost identity, symbols or restart boundary.' }
    [Stage9OptionalAssemblyGuard]::Setup($project,$delivery,'repeat','DOTween','EXISTING;DOTWEEN;TAIL')
    if ([Stage9OptionalAssemblyGuard+PlayerSettings]::Symbols -cne 'EXISTING;DOTWEEN;TAIL') { throw 'Existing DOTWEEN symbol was duplicated or reordered.' }
}
foreach ($optional in @('Excel','DomainExcel','')) {
    Case ("real macro entry refuses wrong optional context '$optional' before settings") {
        $delivery=Join-Path $TempGoalRoot ('macro-wrong-'+$(if ($optional) { $optional } else { 'none' }))
        $null=[IO.Directory]::CreateDirectory($delivery)
        Reject { [Stage9OptionalAssemblyGuard]::Setup((Join-Path $TempGoalRoot 'local-copy/destination'),$delivery,'setup',$optional,'KEEP') } 'Explicit installed DOTween wrapper'
        if ([Stage9OptionalAssemblyGuard+PlayerSettings]::Symbols -cne 'KEEP' -or [IO.Directory]::GetFiles($delivery).Count) { throw 'Wrong macro context wrote settings or a receipt.' }
    }
}
Case 'real macro entry refuses missing wrapper/dependency before settings' {
    $delivery=Join-Path $TempGoalRoot 'macro-missing'; $null=[IO.Directory]::CreateDirectory($delivery)
    Reject { [Stage9OptionalAssemblyGuard]::Setup((Join-Path $TempGoalRoot 'missing-wrapper/destination'),$delivery,'setup','DOTween','KEEP') } 'Explicit installed DOTween wrapper'
    if ([Stage9OptionalAssemblyGuard+PlayerSettings]::Symbols -cne 'KEEP' -or [IO.Directory]::GetFiles($delivery).Count) { throw 'Missing integration enabled DOTWEEN.' }
}
Case 'real macro entry refuses a linked wrapper before settings' {
    Fixture 'macro-linked'
    $integrations=Join-Path $project 'Assets/YLFramework/Integrations'
    $null=New-Item -ItemType Junction -Path $integrations -Target (Join-Path $TempGoalRoot 'local-copy/destination/Assets/YLFramework/Integrations')
    $dll=Join-Path $project 'Assets/Plugins/Demigiant/DOTween/DOTween.dll'
    $null=[IO.Directory]::CreateDirectory((Split-Path $dll -Parent))
    foreach ($suffix in @('','.meta')) { [IO.File]::Copy((Join-Path $repository ('Assets/Plugins/Demigiant/DOTween/DOTween.dll'+$suffix)),($dll+$suffix),$false) }
    Reject { [Stage9OptionalAssemblyGuard]::Setup($project,$root,'setup','DOTween','KEEP') } 'Linked optional setup path rejected'
    if ([Stage9OptionalAssemblyGuard+PlayerSettings]::Symbols -cne 'KEEP' -or [IO.File]::Exists((Join-Path $root 'destination-dotween-setup.json'))) { throw 'Linked integration enabled DOTWEEN.' }
}
$root=[IO.Path]::GetFullPath($TempGoalRoot)
$noUnity=@(Get-ChildItem -LiteralPath $root -Filter '*.process.json' -File -Recurse).Count -eq 0
$noOwners=@(Get-ChildItem -LiteralPath $root -File -Recurse | Where-Object { $_.Name -cin @('owner.json','stage9-owner.json') } | Where-Object {
    $fixtureOwner=[IO.File]::ReadAllText($_.FullName) | ConvertFrom-Json -AsHashtable
    -not $fixtureOwner.ContainsKey('Purpose') -or $fixtureOwner.Purpose -cne 'Stage9-input-binding-guard-fixture' -or -not $fixtureOwner.ContainsKey('Simulation') -or $fixtureOwner.Simulation -ne $true
}).Count -eq 0
$failed=@($rows | Where-Object { -not $_.Passed }).Count
$result=@{ Passed=($noUnity -and $noOwners -and $failed -eq 0); Total=$rows.Count; Failed=$failed; Tests=$rows
    Simulation=$true; NoUnityStarted=$noUnity; NoReceiverOwnerCreated=$noOwners; SyntheticBindingOwnerFiles=$true; UnityCompilation=$false; UnityTestsRun=$false
    OfflineCompilationDiagnostics=$(if (Test-Path variable:offlineDiagnostics) { $offlineDiagnostics } else { @() })
    SourceLeafExpectations=$(if (Test-Path variable:sourceLeaves) { $sourceLeaves } else { @{} })
    DriverSha256=Hash $entry; ReceptionSha256=Hash (Join-Path $PSScriptRoot 'Editor/Stage9ReceptionTests.cs'); GuardsSha256=Hash $PSCommandPath }
[IO.File]::WriteAllText((Join-Path $root 'guards.json'),($result | ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
$rows | ForEach-Object { '{0}: {1}' -f $(if ($_.Passed) { 'PASS' } else { 'FAIL' }), $_.Name }
"Pure optional guards: total=$($rows.Count), failed=$failed; receipt=$(Join-Path $root 'guards.json')"
[pscustomobject]$result
if (-not $result.Passed) { throw 'Explicit optional-operation guards failed.' }
