#requires -Version 7.6
[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]+$')][string]$Run)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$parent = 'C:\Users\Kano\AppData\Local\Temp\kilo\U63-20261027-c63a1'
if (-not (Test-Path -LiteralPath $parent)) { throw 'Missing approved guard parent.' }
$fixture = Join-Path $parent ('hybridclr-guards-' + $Run)
if ([IO.Path]::Exists($fixture)) { throw 'Preserve earlier guard receipts.' }
[IO.Directory]::CreateDirectory($fixture) | Out-Null
$root = Join-Path $fixture 'delivery'
[IO.Directory]::CreateDirectory($root) | Out-Null
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$validator = Join-Path $PSScriptRoot 'Get-HybridCLRReceptionScope.ps1'
$parentOwner = @{ purpose='YLFramework-HybridCLR-guard-simulation'; root=$root; repository=$repository; runId='U63-20261027-c63a1'; plan='.kilo/plans/1793047106303-unity-63-compatibility.md' }
$owner = @{
    schemaVersion=2; purpose='YLFramework-HybridCLR-9.0.3-independent-reception'; simulation=$true
    deliveryRoot=$root; repository=$repository; runId='U63-20261027-c63a1'; receiver='h6'
    project=(Join-Path $root 'h6'); evidenceRoot=(Join-Path $root 'h6e')
    unity='G:\Unity Editor\6000.3.27f1\Editor\Unity.exe'; expectedUnityVersion='6000.3.27f1'; expectedUnityRevision='672d562a42a7'
    uguiVersion='2.0.0'; testFrameworkVersion='1.6.0'
    playerRoot=(Join-Path $root 'h6p'); releasePlayerRoot=(Join-Path $root 'h6pr'); runtimeCacheRoot=(Join-Path $root 'h6c')
    baseline=(Join-Path $root 'h6b'); releaseBaseline=(Join-Path $root 'h6br')
    subsetPlayerRoot=(Join-Path $root 'h6s'); subsetCacheRoot=(Join-Path $root 'h6cs'); subsetBaseline=(Join-Path $root 'h6bs')
    compatibilityId='u63-c63a1-6000-3-27f1-h6-v1'; releaseCompatibilityId='u63-c63a1-6000-3-27f1-h6-release-v1'
    subsetCompatibilityId='u63-c63a1-6000-3-27f1-h6-subsets-v1'
    baseReceiptStem='base-v1'; baseImportRun='base-import'; optionalImportRun='optional-import'
    basePackage='base.unitypackage'; optionalPackage='optional.unitypackage'
}
$ownerPath = Join-Path $root 'h6.hybridclr-owner.json'
function Save-Owners {
    [IO.File]::WriteAllText((Join-Path $root 'owner.json'), ($parentOwner | ConvertTo-Json -Depth 8))
    [IO.File]::WriteAllText($ownerPath, ($owner | ConvertTo-Json -Depth 8))
}
function Read-Scope { & $validator -Root $root -Receiver h6 -GuardSimulation }
function Reject([scriptblock]$Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch {
        if ($_.FullyQualifiedErrorId -match 'NamedParameterNotFound|CommandNotFound') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'Unsafe input was accepted.' }
}
$results = [Collections.Generic.List[object]]::new()
function Case([string]$Name, [scriptblock]$Action) {
    try { & $Action; $results.Add(@{ name=$Name; passed=$true }) }
    catch { $results.Add(@{ name=$Name; passed=$false; error=$_.Exception.Message }) }
}
Save-Owners
Case '6000.3 exact guard simulation, not engine evidence' {
    $scope = Read-Scope
    if (-not $scope.IsCompatibility -or -not $scope.IsSimulation -or $scope.Root -ne $owner.evidenceRoot -or $scope.Project -ne $owner.project -or $scope.ExpectedUnityVersion -cne '6000.3.27f1') { throw 'Wrong explicit scope.' }
}
foreach ($change in @(
    @('schemaVersion',1), @('purpose','other'), @('simulation',$false), @('repository',$root), @('deliveryRoot',$fixture),
    @('runId','other'), @('receiver','Receiver*'), @('project',(Join-Path $root 'R')),
    @('expectedUnityVersion','6000.3.27f1c1'), @('expectedUnityVersion','6000.3.28f1'), @('expectedUnityVersion','2022.3.62f3c1'),
    @('expectedUnityRevision','1623fc0bbb97'), @('uguiVersion','1.0.0'), @('testFrameworkVersion','1.3.9'),
    @('runtimeCacheRoot','G:\Unity\HC26'), @('baseline','G:\Unity\H26'), @('subsetPlayerRoot','G:\Unity\H26S'),
    @('subsetCacheRoot','G:\Unity\HC26S'), @('subsetBaseline',(Join-Path $root 'h6p')),
    @('releaseBaseline',(Join-Path $root 'h6b\nested')), @('playerRoot',(Join-Path $root '..\escape')),
    @('releasePlayerRoot',(Join-Path $root 'w9')), @('compatibilityId','hybridclr-base-development'),
    @('baseImportRun','../old'), @('basePackage','../old.unitypackage'),
    @('baseReceiptStem','owner'), @('baseReceiptStem','project-subsets-base'), @('basePackage','optional.unitypackage')
)) {
    $key = $change[0]; $previous = $owner[$key]; $owner[$key] = $change[1]; Save-Owners
    Case ('reject ' + $key + '=' + $change[1]) { Reject { Read-Scope } }
    $owner[$key] = $previous
}
Save-Owners
Case 'guard simulation cannot be used as live scope' { Reject { & $validator -Root $root -Receiver h6 } }
Case 'wrong explicit binding cannot override owner' { Reject { & $validator -Root $root -Receiver h6 -GuardSimulation -ExpectedUnityRevision wrong } }
Case 'wrong executable cannot override owner' { Reject { & $validator -Root $root -Receiver h6 -GuardSimulation -Unity (Join-Path $fixture 'fake.exe') } }
Case 'missing executable is refused without starting a process' {
    $scope = . $validator -Root $root -Receiver h6 -GuardSimulation
    $scope.IsSimulation=$false; $scope.Unity=Join-Path $fixture 'absent.exe'
    Reject { Assert-HybridCLRReady $scope -Creating -Initializing }
}
Case 'non-Unity file cannot impersonate an executable revision' {
    $scope = . $validator -Root $root -Receiver h6 -GuardSimulation
    $scope.IsSimulation=$false; $scope.Unity=Join-Path $fixture 'fake.exe'
    [IO.File]::WriteAllText($scope.Unity, 'pure guard data, never executed')
    Reject { Assert-HybridCLRReady $scope -Creating -Initializing }
}
Case 'unknown owner field is not an alias' {
    $owner['nativePrefix']='H26'; Save-Owners
    try { Reject { Read-Scope } } finally { $owner.Remove('nativePrefix'); Save-Owners }
}
Case 'occupied output is not adopted' {
    [IO.Directory]::CreateDirectory($owner.baseline) | Out-Null
    try { Reject { Read-Scope } } finally { [IO.Directory]::Delete($owner.baseline) }
}
Case 'junction output is rejected without traversal' {
    $target = Join-Path $fixture 'link-target'
    [IO.Directory]::CreateDirectory($target) | Out-Null
    New-Item -ItemType Junction -Path $owner.subsetBaseline -Target $target | Out-Null
    try { Reject { Read-Scope } } finally { [IO.Directory]::Delete($owner.subsetBaseline) }
}
Case 'wrong parent owner cannot authorize receiver' {
    $parentOwner.purpose='other'; Save-Owners
    try { Reject { Read-Scope } } finally { $parentOwner.purpose='YLFramework-HybridCLR-guard-simulation'; Save-Owners }
}
Case 'two receiver descriptors cannot reserve the same output' {
    $other=$owner.Clone(); $other.receiver='h2'; $other.project=Join-Path $root 'h2'; $other.evidenceRoot=Join-Path $root 'h2e'
    $otherPath=Join-Path $root 'h2.hybridclr-owner.json'
    [IO.File]::WriteAllText($otherPath, ($other | ConvertTo-Json -Depth 8))
    try { Reject { Read-Scope } } finally { [IO.File]::Delete($otherPath) }
}
Case '2022 exact compatibility mapping' {
    $saved = $owner.Clone()
    $owner.expectedUnityVersion='2022.3.62f3c1'; $owner.expectedUnityRevision='1623fc0bbb97'
    $owner.unity='C:\Program Files\Unity\Hub\Editor\2022.3.62f3c1\Editor\Unity.exe'
    $owner.uguiVersion='1.0.0'; $owner.testFrameworkVersion='1.3.9'
    $owner.compatibilityId='u63-c63a1-2022-3-62f3c1-h6-v1'; $owner.releaseCompatibilityId='u63-c63a1-2022-3-62f3c1-h6-release-v1'; $owner.subsetCompatibilityId='u63-c63a1-2022-3-62f3c1-h6-subsets-v1'
    Save-Owners
    try { if ((Read-Scope).TestFrameworkVersion -cne '1.3.9') { throw 'Wrong fixed test mapping.' } }
    finally { $script:owner=$saved; Save-Owners }
}
Case 'pure template rendering binds version, root and owner' {
    $scope = . $validator -Root $root -Receiver h6 -GuardSimulation
    $rendered = Convert-HybridCLRTemplate $scope 'HybridCLRReceptionRunner.cs.txt'
    if ($rendered.Contains('__YL_HYBRIDCLR_') -or -not $rendered.Contains('6000.3.27f1')) { throw 'Unbound template.' }
}
foreach ($compatibility in @($true,$false)) {
    foreach ($action in @('ImportBase','ImportOptional')) {
        Case ("formal package resolves in the owned root: compatibility=$compatibility / $action") {
            $scope=Read-Scope
            $scope.IsCompatibility=$compatibility
            $Root=$scope.Root; $Action=$action; $BasePackage='base.unitypackage'; $OptionalPackage='optional.unitypackage'
            $tokens=$null; $errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Invoke-HybridCLRUnity.ps1'),[ref]$tokens,[ref]$errors)
            if ($errors.Count) { throw 'Invocation parser failed.' }
            $assignment=@($ast.FindAll({ param($node)
                $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -ceq '$package'
            },$true))
            if ($assignment.Count -ne 1) { throw 'One formal package resolution required.' }
            . ([scriptblock]::Create($assignment[0].Extent.Text))
            $expectedParent=if ($compatibility) { $scope.DeliveryRoot } else { $scope.Root }
            $expectedName=if ($action -ceq 'ImportBase') { 'base.unitypackage' } else { 'optional.unitypackage' }
            if ($package -cne (Join-Path $expectedParent $expectedName)) { throw 'Formal package resolved outside its exact artifact root.' }
        }
    }
}
Case 'NUnit nonempty all-passed normal exit accepted' {
    . $validator -Root $root -Receiver h6 -GuardSimulation | Out-Null
    Assert-HybridCLRTestResult -ExitCode 0 -Xml ([xml]'<test-run result="Passed" total="2" passed="2" failed="0" skipped="0" />')
}
foreach ($mutation in @('none','wrong-owner','missing-play-assembly','zero-selected','duplicate-name','non-runnable')) {
    Case ('actual discovered HybridCLR selection: '+$mutation) {
        $scope = . $validator -Root $root -Receiver h6 -GuardSimulation
        $discovery=@{ Passed=$true; Project=$scope.Project; UnityVersion=$scope.ExpectedUnityVersion; UnityRevision=$scope.ExpectedUnityRevision
            Receiver='h6'; OwnerSha256=$scope.OwnerSha256
            EditMode=@(@{ Name='HybridCLR.Edit.Case'; Assembly='YLFramework.HybridCLR.Tests.EditMode'; RunState='Runnable' })
            PlayMode=@(@{ Name='HybridCLR.Play.Case'; Assembly='YLFramework.HybridCLR.Tests.PlayMode'; RunState='Runnable' }) }
        $filter='HybridCLR'
        switch ($mutation) {
            'wrong-owner' { $discovery.OwnerSha256='foreign' }
            'missing-play-assembly' { $discovery.PlayMode=@() }
            'zero-selected' { $filter='^Missing$' }
            'duplicate-name' { $discovery.EditMode+=@($discovery.EditMode[0]) }
            'non-runnable' { $discovery.EditMode[0].RunState='Ignored' }
        }
        if ($mutation -ceq 'none') {
            $selected=@(Select-HybridCLRTests $scope $discovery 'EditMode' $filter)
            if ($selected.Count -ne 1 -or $selected[0].Name -cne 'HybridCLR.Edit.Case') { throw 'Wrong discovered selection.' }
        } else { Reject { Select-HybridCLRTests $scope $discovery 'EditMode' $filter } }
    }
}
foreach ($mutation in @('none','missing','extra','duplicate','non-passed','wrong-counter')) {
    Case ('exact HybridCLR XML leaf set: '+$mutation) {
        . $validator -Root $root -Receiver h6 -GuardSimulation | Out-Null
        $expected=@(@{ Name='HybridCLR.Edit.Case' })
        $xml=[xml]'<test-run result="Passed" total="1" passed="1" failed="0" skipped="0"><test-case fullname="HybridCLR.Edit.Case" result="Passed" /></test-run>'
        switch ($mutation) {
            'missing' { $null=$xml.'test-run'.RemoveChild($xml.'test-run'.'test-case') }
            'extra' { $xml.'test-run'.'test-case'.SetAttribute('fullname','HybridCLR.Other') }
            'duplicate' { $null=$xml.'test-run'.AppendChild($xml.'test-run'.'test-case'.CloneNode($true)) }
            'non-passed' { $xml.'test-run'.'test-case'.SetAttribute('result','Skipped') }
            'wrong-counter' { $xml.'test-run'.SetAttribute('total','2') }
        }
        if ($mutation -ceq 'none') { Assert-HybridCLRExecutedTests $expected $xml }
        else { Reject { Assert-HybridCLRExecutedTests $expected $xml } }
    }
}
Case 'actual compilation source collector is not padded with declared source files' {
    $text=[IO.File]::ReadAllText((Join-Path $repository 'Assets/YLFramework/Tests/Integrations/HybridCLR/EditMode/HybridCLRTestFixtures.cs'))
    if ($text.Contains('.Concat(installation["Sources"]')) {throw 'Declared files cannot stand in for actual compiled sources.'}
}
foreach($profileEntry in @(@($true,'',$null),@($true,'owned','Owned'),@($true,'OWNED','Owned'),@($true,'common','Common'),@($false,'','Common'))) {
    Case ('explicit normalized installation profile: '+($profileEntry -join '/')) {
        $scope = . $validator -Root $root -Receiver h6 -GuardSimulation
        $scope.IsCompatibility=$profileEntry[0]
        if ($null -eq $profileEntry[2]) {Reject {Resolve-HybridCLRTestSceneProfile $scope $profileEntry[1] | Out-Null}}
        elseif ((Resolve-HybridCLRTestSceneProfile $scope $profileEntry[1]) -cne $profileEntry[2]) {throw 'The declared profile must be normalized before writes.'}
    }
}
foreach($policyCase in @('known-2022','reject-6000','reject-revision','strict')) {
    Case ('explicit completed-2022 exit policy arguments: '+$policyCase) {
        $scope = . $validator -Root $root -Receiver h6 -GuardSimulation
        if ($policyCase -cne 'reject-6000') {$scope.ExpectedUnityVersion='2022.3.62f3c1';$scope.ExpectedUnityRevision='1623fc0bbb97'}
        if ($policyCase -ceq 'reject-revision') {$scope.ExpectedUnityRevision='wrong'}
        if ($policyCase -like 'reject-*') {
            Reject {Get-HybridCLRTestArguments $scope EditMode 'Tests' 'C:\guard.xml' 'C:\guard.log' $false 'Known2022EditorExit' | Out-Null}
        } else {
            $policy=if($policyCase -ceq 'strict'){'Strict'}else{'Known2022EditorExit'}
            $arguments=@(Get-HybridCLRTestArguments $scope EditMode 'Tests' 'C:\guard.xml' 'C:\guard.log' $false $policy)
            $index=[Array]::IndexOf($arguments,'-hclr-acceptance-policy')
            if ($policyCase -ceq 'strict') {
                if ($index -ge 0) {throw 'Strict defaults must not enable the known-issue policy.'}
            } elseif ($index -lt 0 -or $arguments[$index+1] -cne 'Known2022EditorExit' -or
                $arguments -notcontains '-hclr-completion-root' -or $arguments -contains '-hclr-root') {
                throw 'Known completion evidence must not grant a Common profile scene ownership.'
            }
        }
    }
}
foreach($storedCase in @('publish','artifact-drift','decision-drift','overwrite')) {
    Case ('immutable functional acceptance consumption: '+$storedCase) {
        $scope = . $validator -Root $root -Receiver h6 -GuardSimulation
        $scope.Root=Join-Path $fixture ('stored-acceptance-'+$storedCase)
        [IO.Directory]::CreateDirectory($scope.Root) | Out-Null
        $acceptanceRun='completed'
        $artifact=Join-Path $scope.Root ($acceptanceRun+'.xml')
        $processFact=Join-Path $scope.Root ($acceptanceRun+'.log.exit.json')
        [IO.File]::WriteAllText($artifact,'owned evidence bytes')
        [IO.File]::WriteAllText($processFact,'owned process facts')
        $decision=[pscustomobject]@{SchemaVersion=1;Run=$acceptanceRun;Phase='Tests';Policy='Known2022EditorExit';Project=$scope.Project
            UnityVersion=$scope.ExpectedUnityVersion;UnityRevision=$scope.ExpectedUnityRevision;Receiver=$scope.Owner.receiver;OwnerSha256=$scope.OwnerSha256
            FunctionalPassed=$true;ExitPassed=$false;ProcessPassed=$false;KnownIssueApplied=$true;Accepted=$true;AcceptanceStatus='PassedWithKnownIssue'
            ArtifactPath=$artifact;ArtifactSha256=(Get-FileHash -LiteralPath $artifact).Hash.ToLowerInvariant()
            ProcessFactPath=$processFact;ProcessFactSha256=(Get-FileHash -LiteralPath $processFact).Hash.ToLowerInvariant()
            CompletionSealPath=$null;CompletionSealSha256=$null;SelectionSha256=$null}
        Publish-HybridCLRFunctionalAcceptance $scope $acceptanceRun $decision
        if ($storedCase -ceq 'artifact-drift') {
            [IO.File]::AppendAllText($artifact,'changed')
            $decision.ArtifactSha256=(Get-FileHash -LiteralPath $artifact).Hash.ToLowerInvariant()
            Reject {Assert-HybridCLRStoredAcceptance $scope $acceptanceRun $decision}
        } elseif ($storedCase -ceq 'decision-drift') {
            $decision.KnownIssueApplied=$false
            Reject {Assert-HybridCLRStoredAcceptance $scope $acceptanceRun $decision}
        } elseif ($storedCase -ceq 'overwrite') {Reject {Publish-HybridCLRFunctionalAcceptance $scope $acceptanceRun $decision}}
        else {$null=Assert-HybridCLRStoredAcceptance $scope $acceptanceRun $decision}
    }
}
foreach ($mode in @('EditMode','PlayMode')) {
    Case ('test runner forwards explicit ' + $mode) {
        $scope = . $validator -Root $root -Receiver h6 -GuardSimulation
        $arguments = @(Get-HybridCLRTestArguments $scope $mode 'YLFramework.Tests.Integrations.HybridCLR' 'C:\guard.xml' 'C:\guard.log')
        $at=[Array]::IndexOf($arguments,'-testPlatform')
        if ($at -lt 0 -or $arguments[$at+1] -cne $mode -or $arguments -contains '-executeMethod') { throw 'Wrong explicit official test platform or implicit Prepare.' }
    }
    Case ('owned saved-scene CLI binding for ' + $mode) {
        $scope = . $validator -Root $root -Receiver h6 -GuardSimulation
        $arguments = @(Get-HybridCLRTestArguments $scope $mode 'YLFramework.Tests.Integrations.HybridCLR' 'C:\guard-run.xml' 'C:\guard.log')
        if ($mode -ceq 'EditMode') {
            foreach ($pair in @(@('-hclr-root',$scope.Root),@('-hclr-run','guard-run'),@('-hclr-owner-sha256',$scope.OwnerSha256))) {
                $index=[Array]::IndexOf($arguments,$pair[0])
                if ($index -lt 0 -or $arguments[$index+1] -cne $pair[1]) { throw 'Missing exact owned EditMode scene context.' }
            }
        } elseif ($arguments -contains '-hclr-root' -or $arguments -contains '-hclr-owner-sha256') {
            throw 'EditMode bootstrap must not replace the official PlayMode runner.'
        }
    }
    Case ('legacy runner does not acquire scene ownership for ' + $mode) {
        $scope = . $validator -Root $root -Receiver h6 -GuardSimulation
        $scope.IsCompatibility=$false
        $arguments = @(Get-HybridCLRTestArguments $scope $mode 'YLFramework.Tests.Integrations.HybridCLR' 'C:\legacy.xml' 'C:\guard.log')
        if ($arguments -contains '-hclr-root' -or $arguments -contains '-hclr-run' -or $arguments -contains '-hclr-owner-sha256') {
            throw 'Legacy and GUI test paths cannot acquire the owned batch host.'
        }
    }
}
foreach ($mode in @('EditMode','PlayMode')) {
    Case ('common profile never forwards owned host arguments: '+$mode) {
        $scope = . $validator -Root $root -Receiver h6 -GuardSimulation
        $arguments=@(Get-HybridCLRTestArguments $scope $mode 'Tests' 'C:\common.xml' 'C:\common.log' $false)
        if ($arguments -contains '-hclr-root' -or $arguments -contains '-hclr-owner-sha256') { throw 'Common is an explicitly unowned scene context.' }
    }
}
foreach ($mutation in @('common','owned','unknown-profile','wrong-owner','missing-owned','unexpected-owned','changed-owned','wrong-count','missing-discovered-source','extra-discovered-source','changed-source-meta')) {
    Case ('declared common/owned scene installation: '+$mutation) {
        $scope = . $validator -Root $root -Receiver h6 -GuardSimulation
        $scope.Project=Join-Path $fixture ('profile-project-'+$mutation)
        $scope.Root=Join-Path $fixture ('profile-evidence-'+$mutation)
        $directory=Join-Path $scope.Project 'Assets/YLFramework/Tests/Integrations/HybridCLR/EditMode'
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        [IO.Directory]::CreateDirectory($scope.Root) | Out-Null
        $source=Join-Path $repository 'Assets/YLFramework/Tests/Integrations/HybridCLR/EditMode/HybridCLRRunnerSceneTests.cs'
        $copy=Join-Path $directory 'HybridCLRRunnerSceneTests.cs'
        [IO.File]::Copy($source,$copy,$false)
        [IO.File]::Copy($source+'.meta',$copy+'.meta',$false)
        $template=Join-Path $PSScriptRoot 'HybridCLROwnedRunnerSceneTests.cs.txt'
        $ownedPath=Join-Path $directory 'HybridCLROwnedRunnerSceneTests.cs'
        $owned=$mutation -notin @('common','unexpected-owned')
        $generated=$null
        if ($owned -or $mutation -ceq 'unexpected-owned') {
            [IO.File]::Copy($template,$ownedPath,$false)
            $generated=@{Path=$ownedPath;TemplatePath=$template;Sha256=(Get-FileHash -LiteralPath $template).Hash.ToLowerInvariant()}
        }
        $proof=@{Purpose='YLFramework-HybridCLR-receiver-tests-v1'; Project=$scope.Project; UnityVersion=$scope.ExpectedUnityVersion
            UnityRevision=$scope.ExpectedUnityRevision;Receiver=$scope.Owner.receiver;OwnerSha256=$scope.OwnerSha256
            SceneProfile=$(if($owned){'Owned'}else{'Common'});CommonSceneCases=5;OwnedSceneCases=$(if($owned){8}else{0})
            Sources=@(@{Path=$copy;SourcePath=$source;Sha256=(Get-FileHash -LiteralPath $copy).Hash.ToLowerInvariant();MetaSha256=(Get-FileHash -LiteralPath ($source+'.meta')).Hash.ToLowerInvariant()})
            Generated=$(if($owned){$generated}else{$null}) }
        switch($mutation) {
            'unknown-profile' {$proof.SceneProfile='Other'}
            'wrong-owner' {$proof.OwnerSha256='0'*64}
            'missing-owned' {[IO.File]::Delete($ownedPath)}
            'changed-owned' {[IO.File]::AppendAllText($ownedPath,'changed')}
            'wrong-count' {$proof.OwnedSceneCases=0}
            'changed-source-meta' {[IO.File]::AppendAllText($copy+'.meta',"`nuserData: changed")}
        }
        [IO.File]::WriteAllText((Join-Path $scope.Root 'tests-installation.json'),($proof | ConvertTo-Json -Depth 8))
        if ($mutation -in @('missing-discovered-source','extra-discovered-source')) {
            $discovery=@{Project=$scope.Project;UnityVersion=$scope.ExpectedUnityVersion;UnityRevision=$scope.ExpectedUnityRevision
                Receiver=$scope.Owner.receiver;OwnerSha256=$scope.OwnerSha256;SceneProfile=$proof.SceneProfile
                TestInstallationSha256=(Get-FileHash -LiteralPath (Join-Path $scope.Root 'tests-installation.json')).Hash.ToLowerInvariant()
                EditMode=@();Sources=@(@{Path=$copy},@{Path=$ownedPath})}
            foreach($index in 1..5) { $discovery.EditMode+=@{Name=('YLFramework.Tests.Integrations.HybridCLR.HybridCLRRunnerSceneTests.Case'+$index)} }
            foreach($index in 1..8) { $discovery.EditMode+=@{Name=('YLFramework.Tests.Integrations.HybridCLR.HybridCLROwnedRunnerSceneTests.Case'+$index)} }
            if ($mutation -ceq 'missing-discovered-source') {$discovery.Sources=@(@{Path=$copy})}
            else {$discovery.Sources+=@{Path=(Join-Path $directory 'Unknown.cs')}}
            Reject { Assert-HybridCLRTestInstallation $scope $discovery | Out-Null }
        } elseif ($mutation -in @('common','owned')) {
            $result=Assert-HybridCLRTestInstallation $scope
            if ($result.SceneProfile -cne $proof.SceneProfile) { throw 'Profile was reinterpreted.' }
        } else { Reject { Assert-HybridCLRTestInstallation $scope | Out-Null } }
    }
}
foreach ($mutation in @('normal','missing','unknown-exit','wrong-owner','forced','timeout','cancelled','observer-error','nonzero')) {
    Case ('discovery requires an observed normal process receipt: '+$mutation) {
        $scope = . $validator -Root $root -Receiver h6 -GuardSimulation
        $scope.Root=Join-Path $fixture ('discovery-exit-'+$mutation)
        [IO.Directory]::CreateDirectory($scope.Root) | Out-Null
        $run='discover'
        $proof=@{schemaVersion=1;purpose='YLFramework-HybridCLR-owned-process-v1';run=$run;project=$scope.Project
            ownerSha256=$scope.OwnerSha256;exe=$scope.Unity;log=(Join-Path $scope.Root ($run+'.log'))
            terminalStatus='Exited';observedExit=$true;exitCode=0;editorForcedCleanup=$false;timedOut=$false
            observerError=$null;cleanupError=$null;alive=$false;succeeded=$true}
        switch ($mutation) {
            'unknown-exit' {$proof.observedExit=$false;$proof.exitCode=$null}
            'wrong-owner' {$proof.ownerSha256='0'*64}
            'forced' {$proof.editorForcedCleanup=$true}
            'timeout' {$proof.timedOut=$true;$proof.terminalStatus='TimedOut'}
            'cancelled' {$proof.terminalStatus='Cancelled'}
            'observer-error' {$proof.observerError='failed';$proof.terminalStatus='ObserverError'}
            'nonzero' {$proof.exitCode=2;$proof.terminalStatus='NonZeroExit'}
        }
        if ($mutation -cne 'missing') {[IO.File]::WriteAllText((Join-Path $scope.Root ($run+'.log.exit.json')),($proof | ConvertTo-Json -Depth 6))}
        if ($mutation -ceq 'normal') {$null=Assert-HybridCLRNormalProcessExit $scope $run}
        else {Reject {Assert-HybridCLRNormalProcessExit $scope $run | Out-Null}}
    }
}
foreach ($mutation in @('none','wrong-guid','missing-meta','linked-meta','changed-scene','changed-meta')) {
    Case ('owned scene bytes and actual meta identity: '+$mutation) {
        $scope = . $validator -Root $root -Receiver h6 -GuardSimulation
        $scope.Project=Join-Path $fixture ('scene-proof-'+$mutation)
        $assets=Join-Path $scope.Project 'Assets'
        [IO.Directory]::CreateDirectory($assets) | Out-Null
        $run='scene-identity'
        $asset='Assets/HybridCLRTestsBootstrap-'+$run+'.unity'
        $scenePath=Join-Path $scope.Project $asset
        $metaPath=$scenePath+'.meta'
        [IO.File]::WriteAllText($scenePath,'owned guard data, never imported by Unity')
        [IO.File]::WriteAllText($metaPath,"fileFormatVersion: 2`nguid: $('a'*32)`n")
        $proof=@{ Purpose='YLFramework-HybridCLR-owned-test-scene-v1'; Project=$scope.Project
            UnityVersion=$scope.ExpectedUnityVersion; UnityRevision=$scope.ExpectedUnityRevision; Receiver=$scope.Owner.receiver
            OwnerSha256=$scope.OwnerSha256; Run=$run; Scene=$asset; SceneGuid=('a'*32)
            SceneSha256=(Get-FileHash -LiteralPath $scenePath -Algorithm SHA256).Hash.ToLowerInvariant()
            SceneMetaSha256=(Get-FileHash -LiteralPath $metaPath -Algorithm SHA256).Hash.ToLowerInvariant() }
        switch ($mutation) {
            'wrong-guid' {
                [IO.File]::WriteAllText($metaPath,"fileFormatVersion: 2`nguid: $('b'*32)`n")
                $proof.SceneMetaSha256=(Get-FileHash -LiteralPath $metaPath -Algorithm SHA256).Hash.ToLowerInvariant()
            }
            'missing-meta' { [IO.File]::Delete($metaPath) }
            'linked-meta' {
                [IO.File]::Delete($metaPath)
                $target=Join-Path $scope.Project 'link-target'
                [IO.Directory]::CreateDirectory($target) | Out-Null
                New-Item -ItemType Junction -Path $metaPath -Target $target | Out-Null
            }
            'changed-scene' { [IO.File]::AppendAllText($scenePath,'changed') }
            'changed-meta' { [IO.File]::AppendAllText($metaPath,'userData: changed') }
        }
        try {
            if ($mutation -ceq 'none') { Assert-HybridCLRTestScene $scope $proof $run }
            else { Reject { Assert-HybridCLRTestScene $scope $proof $run } }
        } finally {
            if ($mutation -ceq 'linked-meta') { [IO.Directory]::Delete($metaPath) }
        }
    }
}
foreach ($compatibility in @($true,$false)) {
    Case ("Player execution graphics contract: compatibility=$compatibility") {
        $scope = . $validator -Root $root -Receiver h6 -GuardSimulation
        $scope.IsCompatibility=$compatibility
        $arguments=@(Get-HybridCLRPlayerArguments $scope 'C:\guard.log' 'V1' 'guard-run' 'C:\base.json')
        if ($compatibility -and ($arguments -contains '-batchmode' -or $arguments -contains '-nographics')) { throw 'New actual Player evidence cannot use headless arguments.' }
        if (-not $compatibility -and ($arguments -notcontains '-batchmode' -or $arguments -notcontains '-nographics')) { throw 'Historical Player semantics changed.' }
        if ($arguments[[Array]::IndexOf($arguments,'-hclr-root')+1] -cne $scope.Root -or
            $arguments[[Array]::IndexOf($arguments,'-hclr-base-receipt')+1] -cne 'C:\base.json') { throw 'Player arguments lost exact evidence and base identity.' }
    }
}
$graphicsTokens=$null; $graphicsErrors=$null
$graphicsAst=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Run-HybridCLRPlayer.ps1'),[ref]$graphicsTokens,[ref]$graphicsErrors)
$graphicsPolicy=@($graphicsAst.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Test-HybridCLRPlayerGraphics'},$true))
foreach ($spec in @(
    @{ Name='normal-device-field'; Result=@{graphicsDevice='Direct3D12'}; Action='V1'; Log=''; Expected=$true },
    @{ Name='normal-null-device'; Result=@{graphicsDevice='Null'}; Action='V1'; Log=''; Expected=$false },
    @{ Name='normal-missing-device-is-not-a-subset'; Result=@{}; Action='V1'; Log="Direct3D:`n    Version: Direct3D 12`n    Renderer: GPU"; Expected=$false },
    @{ Name='subset-real-device-log'; Result=@{}; Action='ProjectSubsets'; Log="Direct3D:`n    Version: Direct3D 12 [level 12.2]`n    Renderer: Real GPU"; Expected=$true },
    @{ Name='subset-no-device-proof'; Result=@{}; Action='ProjectSubsets'; Log=''; Expected=$false },
    @{ Name='subset-null-log-rejected'; Result=@{}; Action='ProjectSubsets'; Log="Direct3D:`n    Version: Direct3D 12`n    Renderer: NullGfxDevice"; Expected=$false },
    @{ Name='subset-explicit-null-is-not-overridden'; Result=@{graphicsDevice='Null'}; Action='ProjectSubsets'; Log="Direct3D:`n    Version: Direct3D 12`n    Renderer: GPU"; Expected=$false }
)) {
    Case ('Player device evidence: '+$spec.Name) {
        if ($graphicsPolicy.Count -ne 1) { throw 'Explicit subset device-evidence policy is missing.' }
        . ([scriptblock]::Create($graphicsPolicy[0].Extent.Text))
        if ((Test-HybridCLRPlayerGraphics $spec.Result $spec.Action $spec.Log) -ne $spec.Expected) { throw 'Device proof selection differs.' }
    }
}
foreach ($xml in @(
    '<test-run result="Passed" total="0" passed="0" failed="0" skipped="0" />',
    '<test-run result="Passed" total="2" passed="1" failed="0" skipped="1" />',
    '<test-run result="Failed" total="2" passed="1" failed="1" skipped="0" />',
    '<test-run result="Passed" total="2" passed="1" failed="0" skipped="0" />',
    '<test-run result="Passed" total="wat" passed="2" failed="0" skipped="0" />'
)) {
    Case ('reject NUnit ' + $xml) {
        . $validator -Root $root -Receiver h6 -GuardSimulation | Out-Null
        Reject { Assert-HybridCLRTestResult -ExitCode 0 -Xml ([xml]$xml) }
    }
}
Case 'NUnit nonzero process exit is never green' {
    . $validator -Root $root -Receiver h6 -GuardSimulation | Out-Null
    Reject { Assert-HybridCLRTestResult -ExitCode 2 -Xml ([xml]'<test-run result="Passed" total="2" passed="2" failed="0" skipped="0" />') }
}
foreach ($invalid in @(
    @{ ExitCode=-1; ForcedCleanup=$true; KeepOpen=$true; ArtifactPassed=$true },
    @{ ExitCode=0; ForcedCleanup=$true; KeepOpen=$true; ArtifactPassed=$true },
    @{ ExitCode=2; ForcedCleanup=$false; KeepOpen=$false; ArtifactPassed=$false },
    @{ ExitCode=0; ForcedCleanup=$false; KeepOpen=$true; ArtifactPassed=$false }
)) {
    Case ('new receiver rejects process/callback outcome ' + ($invalid | ConvertTo-Json -Compress)) {
        . $validator -Root $root -Receiver h6 -GuardSimulation | Out-Null
        Reject { Assert-HybridCLRProcessResult -IsCompatibility $true @invalid }
    }
}
Case 'new receiver accepts normal callback completion without a forced exit' {
    . $validator -Root $root -Receiver h6 -GuardSimulation | Out-Null
    Assert-HybridCLRProcessResult -IsCompatibility $true -ExitCode 0 -ForcedCleanup $false -KeepOpen $true -ArtifactPassed $true
}
Case 'legacy recorded callback policy remains explicit and isolated' {
    . $validator -Root $root -Receiver h6 -GuardSimulation | Out-Null
    Assert-HybridCLRProcessResult -IsCompatibility $false -ExitCode -1 -ForcedCleanup $true -KeepOpen $true -ArtifactPassed $true
}
Case 'cross-engine baseline rejected before file fingerprint or launch' {
    $scope = . $validator -Root $root -Receiver h6 -GuardSimulation
    $receipt = @{ UnityVersion='2022.3.62f3c1'; UnityRevision='1623fc0bbb97'; Receiver='h6'; OwnerSha256='wrong'; Baseline=$owner.baseline }
    Reject { Assert-HybridCLRReceiptBinding $scope $receipt }
}
Case 'matching baseline binding accepted without claiming native success' {
    $scope = . $validator -Root $root -Receiver h6 -GuardSimulation
    Assert-HybridCLRReceiptBinding $scope @{ UnityVersion='6000.3.27f1'; UnityRevision='672d562a42a7'; Receiver='h6'; OwnerSha256=$scope.OwnerSha256 }
}
Case 'initialized descriptor cannot be rebound' {
    [IO.Directory]::CreateDirectory($owner.evidenceRoot) | Out-Null
    [IO.File]::WriteAllText((Join-Path $owner.evidenceRoot 'owner.json'), [IO.File]::ReadAllText($ownerPath))
    $previous=$owner.basePackage; $owner.basePackage='other.unitypackage'; Save-Owners
    try { Reject { Read-Scope } }
    finally { $owner.basePackage=$previous; Save-Owners }
}
Case 'unknown invocation output is not overwritten' {
    $scope = . $validator -Root $root -Receiver h6 -GuardSimulation
    [IO.File]::WriteAllText((Join-Path $scope.Root 'occupied.log.exit.json'), '{}')
    Reject { Assert-HybridCLRNewEvidence $scope 'occupied' }
}
foreach ($scriptName in @('Audit-HybridCLRNative.ps1','Audit-HybridCLRFinalImport.ps1','Relocate-HybridCLRReceiver.ps1','Sync-HybridCLRReceptionHarness.ps1')) {
    Case ('legacy-only entry rejects new mode: ' + $scriptName) {
        Reject { & (Join-Path $PSScriptRoot $scriptName) -Root $root -Receiver h6 }
    }
}
foreach ($command in @(
    @{ name='Invoke-HybridCLRUnity.ps1'; arguments=@{ Action='Create'; Run='never-start' } },
    @{ name='Install-HybridCLRReceptionHarness.ps1'; arguments=@{} },
    @{ name='Install-HybridCLRReceiverTests.ps1'; arguments=@{} },
    @{ name='Run-HybridCLRReceiverTests.ps1'; arguments=@{ Run='never-start'; TestPlatform='PlayMode'; Filter='YLFramework.Tests.Integrations.HybridCLR' } },
    @{ name='Run-HybridCLRPlayer.ps1'; arguments=@{ Action='V1'; Run='never-start' } },
    @{ name='Start-HybridCLRFixtureServer.ps1'; arguments=@{ Run='never-start'; CertificateOnly=$true } },
    @{ name='New-HybridCLRSignatureFixture.ps1'; arguments=@{ Run='never-start' } }
)) {
    Case ('operational entry refuses simulated root: ' + $command.name) {
        $parameters=$command.arguments
        Reject { & (Join-Path $PSScriptRoot $command.name) -Root $root -Receiver h6 @parameters }
    }
}
Case 'legacy descriptor keeps historical 2022 defaults without touching outputs' {
    $legacyRoot=Join-Path $fixture 'legacy'
    [IO.Directory]::CreateDirectory($legacyRoot) | Out-Null
    [IO.File]::WriteAllText((Join-Path $legacyRoot 'owner.json'), (@{ purpose='YLFramework-HybridCLR-9.0.3-independent-reception'; repository=$repository; project=(Join-Path $legacyRoot 'R') } | ConvertTo-Json))
    $legacy=& $validator -Root $legacyRoot
    if ($legacy.PersistentProject -ne 'G:\Unity\H26' -or $legacy.ExpectedUnityVersion -cne '2022.3.62f3c1') { throw 'Legacy binding changed.' }
}
$syntax = @{ powerShellFiles=0; cSharpSyntaxTrees=0; errors=@(); unityCompilation=$false }
Case 'all HybridCLR PowerShell files parse without execution' {
    foreach ($path in [IO.Directory]::GetFiles($PSScriptRoot,'*.ps1')) {
        $tokens=$null; $errors=$null
        [Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors) | Out-Null
        $syntax.powerShellFiles++
        if ($errors.Count) { $syntax.errors+=@($errors | ForEach-Object { $_.ToString() }) }
    }
    if ($syntax.errors.Count) { throw ($syntax.errors -join [Environment]::NewLine) }
}
Case 'existing Roslyn parses CSharp9 editor and IL2CPP branches offline' {
    $roslyn='C:\Program Files\dotnet\sdk\9.0.312\Roslyn\bincore'
    foreach ($dll in @('Microsoft.CodeAnalysis.dll','Microsoft.CodeAnalysis.CSharp.dll')) {
        $path=Join-Path $roslyn $dll
        if (-not [IO.File]::Exists($path)) { throw 'Existing Roslyn is unavailable; do not install a compiler.' }
        [Reflection.Assembly]::LoadFrom($path) | Out-Null
    }
    $scope = . $validator -Root $root -Receiver h6 -GuardSimulation
    $subsetText=Convert-HybridCLRTemplate $scope 'HybridCLRProjectSubsetProbe.cs.txt'
    $subsetTree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($subsetText)
    foreach ($pair in @(@('compatibility',$owner.subsetCompatibilityId),@('playerDirectory',$owner.subsetPlayerRoot),@('cacheDirectory',$owner.subsetCacheRoot))) {
        $variable=@($subsetTree.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax] -and $_.Identifier.ValueText -ceq $pair[0] })
        if ($variable.Count -ne 1 -or $null -eq $variable[0].Initializer -or $variable[0].Initializer.Value.Token.ValueText -cne $pair[1]) { throw 'Rendered subset defaults must bind exact owned values, including legacy defaults.' }
    }
    foreach ($file in [IO.Directory]::GetFiles($PSScriptRoot,'*.cs.txt')) {
        $name=[IO.Path]::GetFileName($file)
        $text=if ($name -eq 'HybridCLRFixtureHost.cs.txt') { [IO.File]::ReadAllText($file) } else { Convert-HybridCLRTemplate $scope $name }
        foreach ($symbols in @(@('UNITY_EDITOR','UNITY_STANDALONE_WIN','ENABLE_IL2CPP','DEVELOPMENT_BUILD','YL_RESOURCE_ACCEPTANCE_TESTS'),@('UNITY_STANDALONE_WIN','ENABLE_IL2CPP'))) {
            $options=[Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::new([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::CSharp9).WithPreprocessorSymbols([string[]]$symbols)
            $tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($text,$options,$file,[Text.Encoding]::UTF8,[Threading.CancellationToken]::None)
            $errors=@($tree.GetDiagnostics() | Where-Object { $_.Severity -eq [Microsoft.CodeAnalysis.DiagnosticSeverity]::Error })
            $syntax.cSharpSyntaxTrees++
            if ($errors.Count) { $syntax.errors+=@($errors | ForEach-Object { $_.ToString() }) }
        }
    }
    if ($syntax.errors.Count) { throw ($syntax.errors -join [Environment]::NewLine) }
}
Case 'external compiler rejects actual wrong profile before any fixture writes' {
    $tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'HybridCLRReceptionBootstrap.cs.txt')))
    $method=@($tree.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -ceq 'CompileSupport' })
    $guard=@($method[0].Body.Statements | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.IfStatementSyntax] -and $_.Condition.ToString().Contains('GetApiCompatibilityLevel') })
    if ($guard.Count -ne 1 -or $method[0].Body.Statements[1] -ne $guard[0]) { throw 'The actual profile must be rejected directly after Owned and before source/output writes.' }
    $text=@'
using System;
using UnityEditor;
namespace UnityEditor {
    public enum ApiCompatibilityLevel { NET_4_6=3, NET_Unity_4_8=3, NET_Standard=6 }
    public enum BuildTargetGroup { Standalone }
    public enum BuildTarget { StandaloneWindows64, Android }
    public enum ScriptingImplementation { IL2CPP, Mono2x }
    public static class PlayerSettings {
        public static ApiCompatibilityLevel Api=ApiCompatibilityLevel.NET_4_6;
        public static ScriptingImplementation Backend=ScriptingImplementation.IL2CPP;
        public static ApiCompatibilityLevel GetApiCompatibilityLevel(BuildTargetGroup group) => Api;
        public static ScriptingImplementation GetScriptingBackend(BuildTargetGroup group) => Backend;
    }
    public static class EditorUserBuildSettings { public static BuildTarget activeBuildTarget=BuildTarget.StandaloneWindows64; }
}
public static class ActualExternalProfileGuard {
    public static string BoundOwnerSha256="owned";
    public static void Check() { __ACTUAL_GUARD__ }
}
'@
    $types=@(Add-Type -TypeDefinition $text.Replace('__ACTUAL_GUARD__',$guard[0].ToFullString()) -PassThru)
    $type=@($types | Where-Object { $_.Name -ceq 'ActualExternalProfileGuard' })[0]
    $settings=@($types | Where-Object { $_.Name -ceq 'PlayerSettings' })[0]
    $editor=@($types | Where-Object { $_.Name -ceq 'EditorUserBuildSettings' })[0]
    foreach ($mode in @('framework','framework-alias','netstandard','mono','wrong-target','legacy')) {
        $api=if ($mode -in @('netstandard','legacy')) { 'NET_Standard' } elseif ($mode -ceq 'framework-alias') { 'NET_Unity_4_8' } else { 'NET_4_6' }
        $backend=if ($mode -ceq 'mono') { 'Mono2x' } else { 'IL2CPP' }
        $target=if ($mode -ceq 'wrong-target') { 'Android' } else { 'StandaloneWindows64' }
        $settings.GetField('Api').SetValue($null,[Enum]::Parse($settings.GetField('Api').FieldType,$api))
        $settings.GetField('Backend').SetValue($null,[Enum]::Parse($settings.GetField('Backend').FieldType,$backend))
        $editor.GetField('activeBuildTarget').SetValue($null,[Enum]::Parse($editor.GetField('activeBuildTarget').FieldType,$target))
        $type.GetField('BoundOwnerSha256').SetValue($null,$(if ($mode -ceq 'legacy') { '' } else { 'owned' }))
        if ($mode -in @('netstandard','mono','wrong-target')) { Reject { $type.GetMethod('Check').Invoke($null,@()) } }
        else { $null=$type.GetMethod('Check').Invoke($null,@()) }
    }
}
Case 'subset conflict producer carries explicit engine revision receiver and immutable owner identity' {
    $tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'HybridCLRReceptionBootstrap.cs.txt')))
    $method=@($tree.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -ceq 'PublishProjectSubsetConflict' })[0]
    $call=@($method.DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax] -and $_.Expression.ToString() -ceq 'Receipt' -and $_.ArgumentList.Arguments[0].Expression.ToString() -ceq '"project-subsets-conflict"' })[0]
    $fields=@($call.ArgumentList.Arguments[1].Expression.Initializers | ForEach-Object { $_.NameEquals.Name.Identifier.ValueText })
    foreach ($key in @('UnityVersion','UnityRevision','Receiver','OwnerSha256')) { if ($fields -cnotcontains $key) { throw 'Conflict fixture is not independently attributable to the real producer/engine/owner.' } }
}
Case 'subset owner is reloaded after native work before frozen registration identity is inspected' {
    function Has-SubsetReload([string]$Text) {
        $tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($Text)
        $method=@($tree.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -ceq 'BuildProjectSubsetBase' })[0]
        $calls=@($method.DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax] })
        $native=@($calls | Where-Object { $_.Expression.ToString() -ceq 'HybridCLRBuildPipeline.BuildBasePlayer' })[0]
        $registration=@($calls | Where-Object { $_.Expression.ToString() -ceq 'ReadSubsetRegistration' })[0]
        $reload=@($method.DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.AssignmentExpressionSyntax] -and $_.Left.ToString() -ceq 'profiles' -and $_.Right.ToString() -ceq 'SubsetProfiles(owner)' -and $_.Span.Start -gt $native.Span.End -and $_.Span.End -lt $registration.Span.Start })
        $profile=@($method.DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.AssignmentExpressionSyntax] -and $_.Left.ToString() -ceq 'profile' -and $_.Right.ToString() -ceq 'profiles[0]' -and $_.Span.Start -gt $native.Span.End -and $_.Span.End -lt $registration.Span.Start })
        return $reload.Count -eq 1 -and $profile.Count -eq 1 -and $reload[0].Span.End -lt $profile[0].Span.Start
    }
    $text=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'HybridCLRReceptionBootstrap.cs.txt'))
    if (-not (Has-SubsetReload $text)) { throw 'Native work still uses a stale Unity owner reference.' }
    $mutant=$text.Replace("                profiles = SubsetProfiles(owner);`n                profile = profiles[0];",'')
    if ($mutant -ceq $text -or (Has-SubsetReload $mutant)) { throw 'Removing the required post-native reload was not detected.' }
}
Case 'ordinary Release local update uses the already validated immutable owner hash, not a nonexistent descriptor field' {
    $tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'HybridCLRReceptionRunner.cs.txt')))
    $checks=@($tree.GetRoot().DescendantNodes() | Where-Object {
        $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.BinaryExpressionSyntax] -and $_.Left.ToString() -ceq 'candidate.Value<string>("OwnerSha256")'
    })
    if ($checks.Count -ne 1 -or $checks[0].Right.ToString() -cne 'BoundOwnerSha256') { throw 'The actual owner descriptor has no self-referential OwnerSha256 field.' }
}
Case 'native base build requires the explicit standard parent before long native work' {
    $tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'HybridCLRReceptionBootstrap.cs.txt')))
    $method=@($tree.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -ceq 'Build' })
    $guard=@($method[0].Body.Statements | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.IfStatementSyntax] -and $_.Condition.ToString().Contains('IsValidFolder') })
    if ($guard.Count -ne 1 -or $method[0].Body.Statements[1] -ne $guard[0]) { throw 'The prepared standard parent must be checked immediately after Owned, before native stages.' }
    $text='using System; public static class AssetDatabase { public static bool Valid; public static bool IsValidFolder(string path) => Valid; } '+
        'public static class ActualDeploymentPreflight { public static string BoundOwnerSha256="owned"; public static void Check() {'+$guard[0].ToFullString()+'} }'
    $types=@(Add-Type -TypeDefinition $text -PassThru)
    $type=@($types | Where-Object { $_.Name -ceq 'ActualDeploymentPreflight' })[0]
    $asset=@($types | Where-Object { $_.Name -ceq 'AssetDatabase' })[0]
    $asset.GetField('Valid').SetValue($null,$false)
    Reject { $type.GetMethod('Check').Invoke($null,@()) }
    $asset.GetField('Valid').SetValue($null,$true)
    $null=$type.GetMethod('Check').Invoke($null,@())
    $asset.GetField('Valid').SetValue($null,$false)
    $type.GetField('BoundOwnerSha256').SetValue($null,'')
    $null=$type.GetMethod('Check').Invoke($null,@())
}
Case 'actual Player fingerprint matches the native ordinal lowercase file identity' {
    $tokens=$null; $errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Run-HybridCLRPlayer.ps1'),[ref]$tokens,[ref]$errors)
    $function=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Get-ProjectSubsetFingerprint'},$true))
    if ($errors.Count -or $function.Count -ne 1) { throw 'One actual guarded native fingerprint function required.' }
    . ([scriptblock]::Create($function[0].Extent.Text))
    $directory=Join-Path $fixture 'PlayerFingerprint'
    $null=[IO.Directory]::CreateDirectory((Join-Path $directory 'Z'))
    [IO.File]::WriteAllBytes((Join-Path $directory 'a.bin'),[byte[]]@(1))
    [IO.File]::WriteAllBytes((Join-Path $directory 'Z/b.bin'),[byte[]]@(2))
    $first=(Get-FileHash -LiteralPath (Join-Path $directory 'Z/b.bin') -Algorithm SHA256).Hash.ToLowerInvariant()
    $last=(Get-FileHash -LiteralPath (Join-Path $directory 'a.bin') -Algorithm SHA256).Hash.ToLowerInvariant()
    $expected=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("Z/b.bin:${first}`na.bin:${last}"))).ToLowerInvariant()
    if ((Get-ProjectSubsetFingerprint $directory) -cne $expected) { throw 'Player fingerprint changed case, ordering or root-relative identity.' }
    [IO.File]::WriteAllBytes((Join-Path $directory 'a.bin'),[byte[]]@(3))
    if ((Get-ProjectSubsetFingerprint $directory) -ceq $expected) { throw 'Changed Player bytes retained the old identity.' }
}
foreach ($mode in @('matching-new','changed-new','legacy')) {
    Case ('base Player bytes are anchored before process launch: '+$mode) {
        $scope=Read-Scope; $scope.IsCompatibility=$mode -cne 'legacy'
        $Action='V1'; $Variant='Development'; $Root=$scope.Root; $owner=$scope.Owner; $playerRoot=$scope.Owner.playerRoot
        $baseBinding=@{ PlayerFingerprint=$(if ($mode -ceq 'changed-new') { 'other-bytes' } else { 'native-bytes' }) }
        function Get-PlayerFingerprint { 'legacy-bytes' }
        function Get-ProjectSubsetFingerprint([string]$Directory) { 'native-bytes' }
        $tokens=$null; $errors=$null
        $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Run-HybridCLRPlayer.ps1'),[ref]$tokens,[ref]$errors)
        $block=@($ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.IfStatementAst] -and $_.Extent.Text.Contains('$subsetBaselineBefore = Get-ProjectSubsetFingerprint') })
        if ($errors.Count -or $block.Count -ne 1) { throw 'One actual prelaunch identity selection required.' }
        $code=[scriptblock]::Create($block[0].Extent.Text)
        if ($mode -ceq 'changed-new') { Reject { . $code } }
        else {
            . $code
            if ($before -cne $(if ($mode -ceq 'legacy') { 'legacy-bytes' } else { 'native-bytes' })) { throw 'Wrong fingerprint selected for this exact mode.' }
        }
    }
}
foreach ($mutation in @('none','owner','root','purpose','schema','certificate','pfx')) {
    Case ('isolated TLS credentials require exact receiver custody: '+$mutation) {
        $scope = . $validator -Root $root -Receiver h6 -GuardSimulation
        $certificate='a'*64; $pfx='b'*64
        $custody=@{ SchemaVersion=1; Purpose='YLFramework-HybridCLR-owned-loopback-fixture-v1'; Root=$scope.Root
            UnityVersion=$scope.ExpectedUnityVersion; UnityRevision=$scope.ExpectedUnityRevision; Receiver=$scope.Owner.receiver
            OwnerSha256=$scope.OwnerSha256; CertificateSha256=$certificate; PfxSha256=$pfx }
        switch ($mutation) {
            'owner' { $custody.OwnerSha256='foreign' }
            'root' { $custody.Root=$scope.DeliveryRoot }
            'purpose' { $custody.Purpose='Resource63' }
            'schema' { $custody.SchemaVersion=2 }
            'certificate' { $custody.CertificateSha256='c'*64 }
            'pfx' { $custody.PfxSha256='d'*64 }
        }
        if (-not (Get-Command Assert-HybridCLRFixtureCredentials -ErrorAction SilentlyContinue)) { throw 'Missing exact credential custody validation.' }
        if ($mutation -ceq 'none') { Assert-HybridCLRFixtureCredentials $scope $custody $certificate $pfx }
        else { Reject { Assert-HybridCLRFixtureCredentials $scope $custody $certificate $pfx } }
    }
}
foreach($parentCase in @('good','missing','bad-hash','nonzero')) {
    Case ('actual parent helper and callback integration: '+$parentCase) {
        $tokens=$null;$errors=$null
        $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Invoke-HybridCLRUnity.ps1'),[ref]$tokens,[ref]$errors)
        $blocks=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.IfStatementAst] -and
            $node.Clauses[0].Item1.Extent.Text -ceq '$scope.IsCompatibility' -and
            $node.Clauses[0].Item2.Extent.Text.Contains('Invoke-HybridCLROwnedProcess @processParameters')},$true))
        if ($errors.Count -or $blocks.Count -ne 1) {throw 'The reachable new-schema parent integration block is required.'}
        $body=$blocks[0].Clauses[0].Item2.Extent.Text
        $body=$body.Replace(". (Join-Path `$PSScriptRoot 'Invoke-HybridCLROwnedProcess.ps1')",(". '"+(Join-Path $PSScriptRoot 'Invoke-HybridCLROwnedProcess.ps1')+"'"))
        $execute=[scriptblock]::Create($body.Substring(1,$body.Length-2))
        $outerRoot=$Root;$outerRun=$Run
        try {
            $null = . $validator -Root $outerRoot -Receiver h6 -GuardSimulation
            $Root=Join-Path $fixture ('parent-callback-'+$parentCase);$Run='parent-'+$parentCase
            [IO.Directory]::CreateDirectory($Root) | Out-Null
            $scope=[pscustomobject]@{IsCompatibility=$true;OwnerSha256=('a'*64)}
            $project=$Root;$KeepOpenForCallback=$true
            $ExitPolicy='Strict';$functionalAcceptance=$null
            $dll=Join-Path $Root 'ExternalHotUpdate/StandaloneWindows64/YLFramework.HybridCLR.Sample.Support.dll'
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($dll)) | Out-Null
            [IO.File]::WriteAllText($dll,'owned guard bytes, never loaded')
            if ($parentCase -cne 'missing') {
                $hash=if($parentCase -ceq 'bad-hash'){'0'*64}else{(Get-FileHash -LiteralPath $dll).Hash.ToLowerInvariant()}
                [IO.File]::WriteAllText((Join-Path $Root ($Run+'.json')),(@{passed=$true;dllSha256=$hash} | ConvertTo-Json))
            }
            $pwsh=[Environment]::ProcessPath
            for($round=0;$round -lt 8;$round++) {
                $changed=$false
                for($cursor=$pwsh;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)) {
                    if ([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) {
                        $info=if([IO.Directory]::Exists($cursor)){[IO.DirectoryInfo]::new($cursor)}else{[IO.FileInfo]::new($cursor)}
                        $target=$info.ResolveLinkTarget($true)
                        $relative=[IO.Path]::GetRelativePath($cursor,$pwsh)
                        $pwsh=if($relative -ceq '.'){$target.FullName}else{[IO.Path]::Combine($target.FullName,$relative)}
                        $changed=$true;break
                    }
                }
                if (-not $changed) {break}
            }
            $log=Join-Path $Root ($Run+'.log')
            $start=[Diagnostics.ProcessStartInfo]::new($pwsh)
            $start.UseShellExecute=$false;$start.WorkingDirectory=$Root
            foreach($argument in @('-NoLogo','-NoProfile','-NonInteractive','-Command',$(if($parentCase -ceq 'nonzero'){'exit 23'}else{'exit 0'}))) {$start.ArgumentList.Add($argument)}
            if ($parentCase -ceq 'good') {$null=& $execute}
            else {Reject {$null=& $execute}}
            $fact=[IO.File]::ReadAllText($log+'.exit.json') | ConvertFrom-Json -AsHashtable
            if ($parentCase -ceq 'good') {
                if (-not $fact.succeeded -or -not $fact.artifactPassed -or $fact.timeoutMilliseconds -ne 900000) {throw 'The actual parent did not forward or validate its callback.'}
            } elseif ($fact.succeeded -or $fact.terminalStatus -cnotin @('ArtifactFailed','NonZeroExit')) {throw 'The parent swallowed an actual callback/process rejection.'}
        } finally {$Root=$outerRoot;$Run=$outerRun}
    }
}
Case 'loopback trust binds exact custody, public key, endpoint and signed envelope' {
    $scope = . $validator -Root $root -Receiver h6 -GuardSimulation
    if (-not (Get-Command Assert-HybridCLRFixtureTrust -ErrorAction SilentlyContinue)) { throw 'Missing exact loopback trust validation.' }
    $rsa=[Security.Cryptography.RSA]::Create(2048)
    try {
        $request=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=Guard-Memory-Only',$rsa,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $certificate=$request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-1),[DateTimeOffset]::UtcNow.AddHours(1))
        try {
            $sha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($certificate.RawData)).ToLowerInvariant()
            $pfx='b'*64; $envelope='c'*64; $readyPath=Join-Path $scope.Root 'ServerV2-new-ready.json'
            $key=$rsa.ExportParameters($false)
            $ready=@{ endpoint='https://127.0.0.1:18444/'; certificate=[Convert]::ToBase64String($certificate.RawData)
                modulus=[Convert]::ToBase64String($key.Modulus); exponent=[Convert]::ToBase64String($key.Exponent) }
            $custody=@{ SchemaVersion=1; Purpose='YLFramework-HybridCLR-owned-loopback-fixture-v1'; Root=$scope.Root
                UnityVersion=$scope.ExpectedUnityVersion; UnityRevision=$scope.ExpectedUnityRevision; Receiver=$scope.Owner.receiver; OwnerSha256=$scope.OwnerSha256
                CertificateSha256=$sha; PfxSha256=$pfx; Publication='ServerV2'; ReadyPath=$readyPath; EnvelopeSha256=$envelope }
            foreach ($mutation in @('none','owner','ready-path','publication','envelope','remote','http','certificate','modulus','exponent')) {
                $data=$ready.Clone(); $binding=$custody.Clone()
                switch ($mutation) {
                    'owner' { $binding.OwnerSha256='foreign' }
                    'ready-path' { $binding.ReadyPath=Join-Path $scope.Root 'other.json' }
                    'publication' { $binding.Publication='../other' }
                    'envelope' { $binding.EnvelopeSha256='d'*64 }
                    'remote' { $data.endpoint='https://192.0.2.1:18444/' }
                    'http' { $data.endpoint='http://127.0.0.1:18444/' }
                    'certificate' { $data.certificate=[Convert]::ToBase64String([byte[]]@(1,2,3)) }
                    'modulus' { $data.modulus='other' }
                    'exponent' { $data.exponent='other' }
                }
                if ($mutation -ceq 'none') { Assert-HybridCLRFixtureTrust $scope $data $binding $readyPath $sha $pfx $envelope }
                else { Reject { Assert-HybridCLRFixtureTrust $scope $data $binding $readyPath $sha $pfx $envelope } }
            }
        } finally { $certificate.Dispose() }
    } finally { $rsa.Dispose() }
}
Case 'native metadata fixture is tied to its exact producer, base and publication' {
    $scope = . $validator -Root $root -Receiver h6 -GuardSimulation
    if (-not (Get-Command Assert-HybridCLRMetadataFixture -ErrorAction SilentlyContinue)) { throw 'Missing owned negative metadata producer validation.' }
    $digest='a'*64; $sha='b'*64; $candidatePath=Join-Path $scope.Root 'bad-metadata-fixture.json'
    $candidate=@{ passed=$true; negativeFixtureOnly=$true; ProjectId='owned-project'; CompatibilityId=$scope.Owner.releaseCompatibilityId
        CatalogDigest=$digest; DirectoryPath=(Join-Path $scope.Project ('AssetBundles/YLFramework/owned-project/StandaloneWindows64/'+$digest)) }
    $base=@{ ProjectId='owned-project'; CompatibilityId=$scope.Owner.releaseCompatibilityId }
    $proof=@{ Purpose='YLFramework-HybridCLR-owned-negative-metadata-v1'; CandidatePath=$candidatePath; CandidateSha256=$sha
        UnityVersion=$scope.ExpectedUnityVersion; UnityRevision=$scope.ExpectedUnityRevision; Receiver=$scope.Owner.receiver; OwnerSha256=$scope.OwnerSha256 }
    foreach ($mutation in @('none','producer','sha','project','compatibility','outside','digest','not-negative')) {
        $data=$candidate.Clone(); $binding=$proof.Clone()
        switch ($mutation) {
            'producer' { $binding.OwnerSha256='foreign' }
            'sha' { $binding.CandidateSha256='c'*64 }
            'project' { $data.ProjectId='other-project' }
            'compatibility' { $data.CompatibilityId='other-player' }
            'outside' { $data.DirectoryPath=Join-Path $scope.DeliveryRoot $digest }
            'digest' { $data.CatalogDigest='d'*64 }
            'not-negative' { $data.negativeFixtureOnly=$false }
        }
        if ($mutation -ceq 'none') { Assert-HybridCLRMetadataFixture $scope $data $base $binding $candidatePath $sha }
        else { Reject { Assert-HybridCLRMetadataFixture $scope $data $base $binding $candidatePath $sha } }
    }
}
Case 'signature native consumer accepts only the exact production authentication rejection' {
    $tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'HybridCLRReceptionRunner.cs.txt')))
    $method=@($tree.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -ceq 'IsSignatureRejection' })
    if ($method.Count -ne 1) { throw 'A precise signature rejection consumer is required.' }
    $text=@'
using System;
using System.IO;
using YLFramework.Resource;
namespace YLFramework.Resource {
 public enum ResourceError { UpdateFailed, LoadFailed }
 public sealed class ResourceException : Exception {
  public ResourceError Kind {get;}
  public ResourceException(ResourceError kind, Exception inner) : base("Resource operation failed",inner) {Kind=kind;}
 }
}
public static class ActualSignatureGate { __METHOD__ }
'@
    $types=@(Add-Type -TypeDefinition $text.Replace('__METHOD__',$method[0].ToFullString()) -PassThru)
    $type=@($types | Where-Object { $_.Name -ceq 'ActualSignatureGate' })[0]
    $resource=@($types | Where-Object { $_.Name -ceq 'ResourceException' })[0]
    $kind=@($types | Where-Object { $_.Name -ceq 'ResourceError' })[0]
    $gate=$type.GetMethod('IsSignatureRejection',[Reflection.BindingFlags]'Static,NonPublic')
    foreach ($mode in @('signature','network','catalog','other-kind','plain','none')) {
        $inner=if ($mode -ceq 'network') { [IO.IOException]::new('TLS connection failed.') } else { [IO.InvalidDataException]::new($(if ($mode -ceq 'catalog') { 'Catalog hash rejected.' } else { 'Release signature rejected.' })) }
        $failure=if ($mode -ceq 'none') { $null } elseif ($mode -ceq 'plain') { $inner } else {
            [Activator]::CreateInstance($resource,@([Enum]::Parse($kind,$(if ($mode -ceq 'other-kind') { 'LoadFailed' } else { 'UpdateFailed' })),$inner))
        }
        if ([bool]$gate.Invoke($null,@($failure)) -ne ($mode -ceq 'signature')) { throw "Incorrect authentication rejection classification: $mode" }
    }
}
Case 'bad signature fixture changes exactly one signature bit and no signed payload fields' {
    $scope = . $validator -Root $root -Receiver h6 -GuardSimulation
    if (-not (Get-Command Assert-HybridCLRSignatureMutation -ErrorAction SilentlyContinue)) { throw 'Missing exact one-bit fixture validation.' }
    $original=@{ SchemaVersion=1; Algorithm='RSA-SHA256-PKCS1'; KeyId='hclr-test'; Payload='cGF5bG9hZA=='; Signature=[Convert]::ToBase64String([byte[]]::new(256)) }
    foreach ($mode in @('none','payload','key','two-bits','unchanged')) {
        $changed=$original.Clone(); $bytes=[Convert]::FromBase64String($original.Signature); $bytes[0]=1
        switch ($mode) {
            'payload' { $changed.Payload='b3RoZXI=' }
            'key' { $changed.KeyId='foreign' }
            'two-bits' { $bytes[0]=3 }
            'unchanged' { $bytes[0]=0 }
        }
        $changed.Signature=[Convert]::ToBase64String($bytes)
        if ($mode -ceq 'none') { Assert-HybridCLRSignatureMutation $original $changed }
        else { Reject { Assert-HybridCLRSignatureMutation $original $changed } }
    }
}
Case 'owned ordinary Release explicitly clears acceptance symbols without changing legacy policy' {
    $tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'HybridCLRReceptionBootstrap.cs.txt')))
    $method=@($tree.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -ceq 'ConfigureRelease' })
    $guard=@($method[0].Body.Statements | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.IfStatementSyntax] -and $_.ToString().Contains('SetScriptingDefineSymbolsForGroup') })
    if ($guard.Count -ne 1) { throw 'Explicit owned Release define policy required.' }
    $text='using System; public enum BuildTargetGroup {Standalone} public static class PlayerSettings {public static string Symbols; public static void SetScriptingDefineSymbolsForGroup(BuildTargetGroup target,string symbols) {Symbols=symbols;}} '+
        'public static class ActualReleaseDefines {public static string BoundOwnerSha256="owned"; public static void Check() {'+$guard[0].ToFullString()+'} }'
    $types=@(Add-Type -TypeDefinition $text -PassThru)
    $type=@($types | Where-Object { $_.Name -ceq 'ActualReleaseDefines' })[0]
    $settings=@($types | Where-Object { $_.Name -ceq 'PlayerSettings' })[0]
    foreach ($owned in @($true,$false)) {
        $type.GetField('BoundOwnerSha256').SetValue($null,$(if($owned){'owned'}else{''}))
        $settings.GetField('Symbols').SetValue($null,'YL_RESOURCE_ACCEPTANCE_TESTS')
        $null=$type.GetMethod('Check').Invoke($null,@())
        if ($settings.GetField('Symbols').GetValue($null) -cne $(if($owned){''}else{'YL_RESOURCE_ACCEPTANCE_TESTS'})) { throw 'Wrong exact Release symbol policy.' }
    }
}
Case 'actual native action whitelist admits signature rejection and rejects unknown actions' {
    $tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'HybridCLRReceptionRunner.cs.txt')))
    $method=@($tree.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -ceq 'Run' })
    $guard=@($method[0].Body.Statements | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.IfStatementSyntax] -and $_.ToString().Contains('Unknown explicit acceptance action.') })
    if ($guard.Count -ne 1) { throw 'One native explicit-action whitelist required.' }
    $types=@(Add-Type -TypeDefinition ('using System; public static class ActualNativeActions { public static void Check(string _action) {'+$guard[0].ToFullString()+'} }') -PassThru)
    $type=@($types | Where-Object { $_.Name -ceq 'ActualNativeActions' })[0]
    foreach ($action in @('V1','Update','V2','BadPayload','BadMetadata','BadSignature')) { $null=$type.GetMethod('Check').Invoke($null,@($action)) }
    foreach ($action in @('','unknown','badsignature','BadSignatureTypo')) { Reject { $type.GetMethod('Check').Invoke($null,@($action)) } }
}
Case 'real package provenance method accepts frozen import schema and rejects altered proof' {
    $scope = . $validator -Root $root -Receiver h6 -GuardSimulation
    $tree=[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'HybridCLRReceptionBootstrap.cs.txt')))
    $methodText=foreach ($name in @('ProductPackageSha','RequireBoundReceipt','Sha','RejectSubsetLinks','RequireSubset')) {
        $method=@($tree.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] -and $_.Identifier.ValueText -ceq $name })
        if ($method.Count -ne 1) { throw "One actual provenance method required: $name" }
        $method[0].ToFullString()
    }
    $constants=@{ Root=$scope.Root; Project=$scope.Project; BoundOwnerSha256=$scope.OwnerSha256
        ExpectedUnityVersion=$scope.ExpectedUnityVersion; ExpectedUnityRevision=$scope.ExpectedUnityRevision }
    $fields=foreach ($name in $constants.Keys) { 'private const string '+$name+' = '+($constants[$name] | ConvertTo-Json -Compress)+';' }
    $text='using System; using System.IO; using System.Security.Cryptography; using Newtonsoft.Json.Linq; public static class ActualPackageProof {'+($fields -join "`n")+($methodText -join "`n")+'}'
    $references=@(([AppContext]::GetData('TRUSTED_PLATFORM_ASSEMBLIES') -split [IO.Path]::PathSeparator)+@([Newtonsoft.Json.Linq.JObject].Assembly.Location) |
        Sort-Object -Unique | ForEach-Object { [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($_) })
    $compilation=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create(('PackageProof'+[Guid]::NewGuid().ToString('N')),
        [Microsoft.CodeAnalysis.SyntaxTree[]]@([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($text)),
        [Microsoft.CodeAnalysis.MetadataReference[]]$references,
        [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary))
    $stream=[IO.MemoryStream]::new()
    try {
        $emitted=$compilation.Emit($stream)
        if (-not $emitted.Success) { throw (($emitted.Diagnostics | ForEach-Object { $_.ToString() }) -join "`n") }
        $type=[Reflection.Assembly]::Load($stream.ToArray()).GetType('ActualPackageProof',$true)
    } finally { $stream.Dispose() }
    $method=$type.GetMethod('ProductPackageSha',[Reflection.BindingFlags]'Static,NonPublic')
    $ownerObject=[Newtonsoft.Json.Linq.JObject]::Parse(($owner | ConvertTo-Json -Depth 12))
    foreach ($optional in @($false,$true)) {
        $packageName=if ($optional) { $owner.optionalPackage } else { $owner.basePackage }
        $import=if ($optional) { $owner.optionalImportRun } else { $owner.baseImportRun }
        $action=if ($optional) { 'ImportOptional' } else { 'ImportBase' }
        $artifact=Join-Path $scope.DeliveryRoot $packageName
        [IO.File]::WriteAllBytes($artifact,[byte[]]@(1,2,3,4))
        $sha=(Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash
        $proof=@{ passed=$true; testsPresent=$false; action=$action; packageSha256=$sha; UnityVersion=$scope.ExpectedUnityVersion
            UnityRevision=$scope.ExpectedUnityRevision; Receiver='h6'; OwnerSha256=$scope.OwnerSha256 }
        $proofPath=Join-Path $scope.Root ($import+'.receipt.json')
        foreach ($mutation in @('none','owner','version','action','hash','tests','passed')) {
            $copy=$proof.Clone()
            switch ($mutation) {
                'owner' { $copy.OwnerSha256='foreign' }
                'version' { $copy.UnityVersion='2022.3.62f3c1' }
                'action' { $copy.action='other' }
                'hash' { $copy.packageSha256='wrong' }
                'tests' { $copy.testsPresent=$true }
                'passed' { $copy.passed=$false }
            }
            [IO.File]::WriteAllText($proofPath,($copy | ConvertTo-Json -Depth 8))
            if ($mutation -ceq 'none') {
                if ($method.Invoke($null,@($ownerObject,$optional)) -cne $sha.ToLowerInvariant()) { throw 'Actual product import schema was not accepted.' }
            } else { Reject { $method.Invoke($null,@($ownerObject,$optional)) } }
        }
    }
}
[IO.File]::WriteAllText((Join-Path $fixture 'syntax.json'), ($syntax | ConvertTo-Json -Depth 8))
$failed = @($results | Where-Object { -not $_.passed }).Count
$receipt = @{ simulation=$true; engineStarted=$false; run=$Run; total=$results.Count; passed=($results.Count-$failed); failed=$failed; results=$results }
$receiptPath = Join-Path $fixture 'results.json'
[IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 12))
$results | ForEach-Object { '{0}: {1}' -f $(if ($_.passed) { 'PASS' } else { 'FAIL' }), $_.name }
"Pure guard simulation: total=$($results.Count), failed=$failed; receipt=$receiptPath"
if ($failed) { throw "$failed pure guard test(s) failed; no Unity or native operation was invoked." }
