#requires -Version 7.6
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Initialize','Create','ImportBase','ImportOptional','SyncOptional','Resolve','Execute')][string]$Action,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9-]+$')][string]$Run,
    [string]$Root = 'C:\Users\Kano\AppData\Local\Temp\kilo\H26-20261021',
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\2022.3.62f3c1\Editor\Unity.exe',
    [string]$Receiver,
    [string]$ExpectedUnityVersion,
    [string]$ExpectedUnityRevision,
    [string]$UguiVersion,
    [string]$TestFrameworkVersion,
    [ValidatePattern('^[A-Za-z0-9-]+\.unitypackage$')][string]$OptionalPackage = 'YLFramework-HybridCLR.unitypackage',
    [ValidatePattern('^(?:YLFramework\.Tests\.HybridCLRReception\.Editor\.HybridCLRReceptionBootstrap\.[A-Za-z]+|YLFramework\.Tests\.Integrations\.HybridCLR\.HybridCLRTestFixtures\.(?:Prepare|Discover))$')][string]$EntryPoint,
    [switch]$KeepOpenForCallback,
    [switch]$AuditOnly,
    [switch]$Graphics,
    [ValidateSet('Strict','Known2022EditorExit')][string]$ExitPolicy='Strict',
    [ValidatePattern('^[A-Za-z0-9-]+\.unitypackage$')][string]$BasePackage = 'YLFramework.unitypackage'
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$discovering=$Action -ceq 'Execute' -and $EntryPoint -ceq 'YLFramework.Tests.Integrations.HybridCLR.HybridCLRTestFixtures.Discover'
if ($discovering -and (-not $Graphics -or $KeepOpenForCallback)) { throw 'Official test discovery requires graphics and its own normal completion, not the external compiler callback policy.' }
$binding = @{}
foreach ($name in @('Unity','ExpectedUnityVersion','ExpectedUnityRevision','UguiVersion','TestFrameworkVersion')) {
    if ($PSBoundParameters.ContainsKey($name)) { $binding[$name]=$PSBoundParameters[$name] }
}
$scope = . (Join-Path $PSScriptRoot 'Get-HybridCLRReceptionScope.ps1') -Root $Root -Receiver $Receiver @binding
$ExitPolicy=Resolve-HybridCLRExitPolicy $scope $ExitPolicy
if ($ExitPolicy -cne 'Strict' -and -not $discovering) {throw 'The known Editor-exit policy only applies to completed test discovery, not builds/imports/callbacks.'}
$Root = $scope.Root
$owner = $scope.Owner
$project = $scope.Project
$Unity = $scope.Unity
if ($Action -eq 'Initialize') {
    if (-not $scope.IsCompatibility -or $scope.Bound -or $AuditOnly -or $KeepOpenForCallback -or $EntryPoint) { throw 'Initialize is only for a new exact descriptor and never launches an engine.' }
    Assert-HybridCLRReady $scope -Creating -Initializing
    [IO.Directory]::CreateDirectory($Root) | Out-Null
    $stream = [IO.File]::Open((Join-Path $Root 'owner.json'), 'CreateNew', 'Write', 'None')
    try {
        $bytes=[Text.Encoding]::UTF8.GetBytes($scope.OwnerJson)
        $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true)
    } finally { $stream.Dispose() }
    "Initialized new immutable receiver owner; no Unity launched: $Root"
    return
}
Assert-HybridCLRReady $scope -Creating:($Action -eq 'Create')
if ($scope.IsCompatibility) {
    if ($AuditOnly -or $Action -eq 'SyncOptional') { throw 'Historical audit/sync modes cannot adopt or overwrite new compatibility evidence.' }
    foreach ($pair in @(@('BasePackage','basePackage'),@('OptionalPackage','optionalPackage'))) {
        if ($PSBoundParameters.ContainsKey($pair[0]) -and $PSBoundParameters[$pair[0]] -cne $owner[$pair[1]]) { throw 'Product package differs from its exact receiver binding.' }
    }
    $BasePackage=$owner.basePackage; $OptionalPackage=$owner.optionalPackage
    if (($Action -eq 'ImportBase' -and $Run -cne $scope.BaseImportRun) -or ($Action -eq 'ImportOptional' -and $Run -cne $scope.OptionalImportRun)) { throw 'Use the exact product import run bound in the new owner.' }
    if ($Action -eq 'ImportOptional') {
        $selectionPath=Join-Path $Root 'product-selection.json'
        Assert-NoLinks $selectionPath
        if ([IO.Path]::Exists($selectionPath)) { throw 'Preserve the prior product selection before importing.' }
    }
}
if (-not (Test-Path -LiteralPath $Root) -or -not (Test-Path -LiteralPath $Unity)) { throw 'Missing explicit receiver parent or Unity executable.' }
$others = @(Get-CimInstance Win32_Process -Filter "Name = 'Unity.exe'" | Where-Object { $_.CommandLine -notmatch 'AssetImportWorker' -and $_.CommandLine.Contains($project) })
if ($others.Count) { throw 'This receiver already has a Unity instance; never launch a second one.' }
$log = Join-Path $Root ($Run + '.log')
if (-not $AuditOnly) { Assert-HybridCLRNewEvidence $scope $Run }
if ($ExitPolicy -cne 'Strict') {Assert-HybridCLRNewEvidence $scope $Run @('.completion.json','.acceptance.json')}
if (-not $AuditOnly -and ([IO.File]::Exists($log) -or [IO.File]::Exists($log + '.process.json'))) { throw 'Preserve the previous invocation; choose a new run id.' }
if ($AuditOnly -and ($Action -notin @('ImportBase','ImportOptional') -or
    ([IO.File]::ReadAllText($log + '.exit.json') | ConvertFrom-Json).exitCode -ne 0)) { throw 'Audit-only requires an already exited successful product import.' }
$arguments = @('-batchmode', '-logFile', $log)
if (-not $Graphics) { $arguments += '-nographics' }
if (-not $KeepOpenForCallback -and -not $discovering) { $arguments += '-quit' }
if ($Action -eq 'Create') {
    if ([IO.Path]::Exists($project)) { throw 'Never adopt or replace an existing project.' }
    $arguments += @('-createProject', $project)
} else {
    if (-not (Test-Path -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt'))) { throw 'Receiver project has not been created.' }
    $arguments += @('-projectPath', $project, '-buildTarget', 'Win64')
    if ($Action -in @('ImportBase','ImportOptional','SyncOptional')) {
        $package = Join-Path $(if ($scope.IsCompatibility) { $scope.DeliveryRoot } else { $Root }) $(if ($Action -eq 'ImportBase') { $BasePackage } else { $OptionalPackage })
        Assert-NoLinks $package
        if (-not (Test-Path -LiteralPath $package)) { throw 'Missing formal exported product artifact.' }
        $arguments += @('-importPackage', $package)
    }
    if ($Action -eq 'Resolve') {
        $manifestPath = Join-Path $project 'Packages/manifest.json'
        Assert-NoLinks $manifestPath
        $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json -AsHashtable
        $manifest.dependencies['com.code-philosophy.hybridclr'] = 'https://gitee.com/focus-creative-games/hybridclr_unity.git#v9.0.3'
        [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 15), [Text.UTF8Encoding]::new($false))
    }
    if ($Action -eq 'Execute') {
        if (-not $EntryPoint) { throw 'Execute needs one explicit receiver-only entry point.' }
        if ($scope.IsCompatibility) { Assert-HybridCLRProductImports $scope }
        $arguments += @('-executeMethod', $EntryPoint, '-hclr-run', $Run, '-hclr-root', $Root)
        if ($ExitPolicy -cne 'Strict') {$arguments+=@('-hclr-acceptance-policy',$ExitPolicy)}
    }
}
$start = [Diagnostics.ProcessStartInfo]::new($Unity)
$start.UseShellExecute = $false
$start.WorkingDirectory = $Root
foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
$code = 0
$artifactPassed = $false
$forcedCleanup = $false
$functionalAcceptance=$null
if (-not $AuditOnly) {
    if ($scope.IsCompatibility) {
        . (Join-Path $PSScriptRoot 'Invoke-HybridCLROwnedProcess.ps1')
        $validateCompletion=$null
        if ($KeepOpenForCallback) {
            $validateCompletion={
                $resultPath=Join-Path $Root ($Run+'.json')
                Assert-NoLinks $resultPath
                if (-not [IO.File]::Exists($resultPath)) { return $false }
                $result=[IO.File]::ReadAllText($resultPath) | ConvertFrom-Json
                $dll=Join-Path $Root 'ExternalHotUpdate/StandaloneWindows64/YLFramework.HybridCLR.Sample.Support.dll'
                Assert-NoLinks $dll
                return ($result.passed -is [bool] -and $result.passed -and [IO.File]::Exists($dll) -and
                    $result.dllSha256 -ceq (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant())
            }
        }
        $timeout=if($KeepOpenForCallback){900000}else{1800000}
        $processParameters=@{StartInfo=$start;EvidenceRoot=$Root;Log=$log;Project=$project;Run=$Run;OwnerSha256=$scope.OwnerSha256;TimeoutMilliseconds=$timeout;AfterExit=$validateCompletion}
        if ($ExitPolicy -cne 'Strict') {$processParameters.CompletionSealPath=Join-Path $Root ($Run+'.completion.json');$processParameters.ExitGraceMilliseconds=120000}
        try {$observation=Invoke-HybridCLROwnedProcess @processParameters}
        catch {
            $fact=$_.Exception.Data['HybridCLRProcessResult']
            if ($ExitPolicy -ceq 'Strict' -or $null -eq $fact -or $fact.terminalStatus -cne 'PostCompletionExitStalled') {throw}
            $observation=[pscustomobject]$fact
        }
        $code=$observation.exitCode
        $forcedCleanup=$observation.editorForcedCleanup
        $artifactPassed=$observation.artifactPassed
        if ($ExitPolicy -cne 'Strict') {
            . (Join-Path $PSScriptRoot 'Get-HybridCLRFunctionalAcceptance.ps1')
            $functionalAcceptance=Resolve-HybridCLRFunctionalAcceptance -Scope $scope -Run $Run -Phase Discovery -Policy $ExitPolicy
            $discovery=$functionalAcceptance.Artifact
            $null=Assert-HybridCLRTestInstallation $scope $discovery
            foreach($source in $discovery.Sources) {
                Assert-NoLinks $source.Path
                Assert-NoLinks ($source.Path+'.meta')
                if ($source.Sha256 -cne (Get-FileHash -LiteralPath $source.Path).Hash.ToLowerInvariant() -or
                    $source.MetaSha256 -cne (Get-FileHash -LiteralPath ($source.Path+'.meta')).Hash.ToLowerInvariant()) {throw 'Completed discovery source or meta drifted.'}
            }
            Publish-HybridCLRFunctionalAcceptance $scope $Run $functionalAcceptance
        }
    } else {
    $watcher = $null
    if ($KeepOpenForCallback -and -not $scope.IsCompatibility) {
        $watcher = [IO.FileSystemWatcher]::new($Root, ($Run + '.json'))
        $watcher.EnableRaisingEvents = $true
    }
    $process = [Diagnostics.Process]::Start($start)
    [IO.File]::WriteAllText($log + '.process.json', (@{ pid=$process.Id; project=$project; arguments=$arguments; utc=[DateTime]::UtcNow.ToString('o') } | ConvertTo-Json -Depth 5))
    try {
        if ($KeepOpenForCallback) {
            $resultPath = Join-Path $Root ($Run + '.json')
            while (-not [IO.File]::Exists($resultPath) -and -not $process.HasExited) {
                $changed = $watcher.WaitForChanged(([IO.WatcherChangeTypes]::Created -bor [IO.WatcherChangeTypes]::Renamed), 900000)
                if ($changed.TimedOut) { throw 'The external compiler did not publish its atomic completion receipt.' }
            }
            if ([IO.File]::Exists($resultPath)) {
                $result = [IO.File]::ReadAllText($resultPath) | ConvertFrom-Json
                $dll = Join-Path $Root 'ExternalHotUpdate/StandaloneWindows64/YLFramework.HybridCLR.Sample.Support.dll'
                $artifactPassed = $result.passed -and $result.dllSha256 -eq (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
            }
            if (-not $process.WaitForExit(15000)) { $process.Kill($true); $process.WaitForExit(); $forcedCleanup = $true }
        } elseif (-not $process.WaitForExit(1800000)) {
            $process.Kill($true); $process.WaitForExit(); $forcedCleanup=$true
        }
    } finally { if ($null -ne $watcher) { $watcher.Dispose() } }
    $code = $process.ExitCode
    $process.Dispose()
    [IO.File]::WriteAllText($log + '.exit.json', (@{ exitCode=$code; artifactPassed=$artifactPassed; editorForcedCleanup=$forcedCleanup; utc=[DateTime]::UtcNow.ToString('o') } | ConvertTo-Json))
    }
}
if ($null -eq $functionalAcceptance) {Assert-HybridCLRProcessResult -IsCompatibility $scope.IsCompatibility -ExitCode $code -ForcedCleanup $forcedCleanup -KeepOpen ([bool]$KeepOpenForCallback) -ArtifactPassed $artifactPassed}
if ($scope.IsCompatibility -and $Action -ceq 'Execute' -and $EntryPoint -ceq 'YLFramework.Tests.HybridCLRReception.Editor.HybridCLRReceptionBootstrap.PublishBadMetadata') {
    $candidatePath=Join-Path $Root 'bad-metadata-fixture.json'
    Assert-NoLinks $candidatePath
    $candidate=[IO.File]::ReadAllText($candidatePath) | ConvertFrom-Json
    if (-not $candidate.passed -or -not $candidate.negativeFixtureOnly) { throw 'A real successful negative metadata fixture publication is required.' }
    $proofPath=Join-Path $Root ($Run+'.receipt.json')
    Assert-NoLinks $proofPath
    if ([IO.Path]::Exists($proofPath)) { throw 'Preserve prior metadata producer evidence.' }
    [IO.File]::WriteAllText($proofPath,(@{ Purpose='YLFramework-HybridCLR-owned-negative-metadata-v1'; ProducerRun=$Run; CandidatePath=$candidatePath
        CandidateSha256=(Get-FileHash -LiteralPath $candidatePath -Algorithm SHA256).Hash.ToLowerInvariant()
        UnityVersion=$scope.ExpectedUnityVersion; UnityRevision=$scope.ExpectedUnityRevision; Receiver=$owner.receiver; OwnerSha256=$scope.OwnerSha256 } | ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
}
if ($Action -eq 'Create') {
    $manifestPath = Join-Path $project 'Packages/manifest.json'
    Assert-NoLinks $manifestPath
    $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json -AsHashtable
    foreach ($key in @($manifest.dependencies.Keys)) { if (-not $key.StartsWith('com.unity.modules.')) { $manifest.dependencies.Remove($key) } }
    $manifest.dependencies['com.unity.ugui'] = $scope.UguiVersion
    $manifest.dependencies['com.unity.nuget.newtonsoft-json'] = '3.2.1'
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 15), [Text.UTF8Encoding]::new($false))
}
if ($Action -in @('ImportBase','ImportOptional','SyncOptional')) {
    $stream = [IO.File]::Open($log, 'Open', 'Read', ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $reader = [IO.StreamReader]::new($stream)
    try { $text = $reader.ReadToEnd() } finally { $reader.Dispose(); $stream.Dispose() }
    $errors = @([regex]::Matches($text, '(?im)^.*(?:error CS\d+|Scripts have compiler errors|Aborting batchmode due to failure|Multiple precompiled assemblies).*$') | ForEach-Object Value)
    if ($errors.Count) { throw ($errors -join [Environment]::NewLine) }
    $assemblies = @('YLFramework.Runtime','YLFramework.Editor') | ForEach-Object {
        $path = Join-Path $project ('Library/ScriptAssemblies/' + $_ + '.dll')
        if (-not [IO.File]::Exists($path)) { throw "Missing actual receiver assembly: $_" }
        @{ name=$_; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    }
    $lock = [IO.File]::ReadAllText((Join-Path $project 'Packages/packages-lock.json')) | ConvertFrom-Json -AsHashtable
    if ($Action -ne 'SyncOptional' -and @($lock.dependencies.Keys | Where-Object {
        -not $_.StartsWith('com.unity.modules.', [StringComparison]::Ordinal) -and $_ -cnotin @('com.unity.ugui','com.unity.nuget.newtonsoft-json')
    }).Count) { throw 'Bare product import must precede all optional SDK and test facilities.' }
    $receipt = @{ action=$Action; passed=$true; assemblies=$assemblies; packageSha256=(Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash; testsPresent=[IO.Directory]::Exists((Join-Path $project 'Assets/YLFramework/Tests')); utc=[DateTime]::UtcNow.ToString('o') }
    if ($scope.IsCompatibility) {
        $receipt.UnityVersion=$scope.ExpectedUnityVersion; $receipt.UnityRevision=$scope.ExpectedUnityRevision
        $receipt.Receiver=$owner.receiver; $receipt.OwnerSha256=$scope.OwnerSha256
    }
    [IO.File]::WriteAllText((Join-Path $Root ($Run + '.receipt.json')), ($receipt | ConvertTo-Json -Depth 10))
    if ($Action -eq 'SyncOptional' -or ($scope.IsCompatibility -and $Action -eq 'ImportOptional')) {
        if ($scope.IsCompatibility -and [IO.Path]::Exists((Join-Path $Root 'product-selection.json'))) { throw 'Preserve the prior product selection.' }
        [IO.File]::WriteAllText((Join-Path $Root 'product-selection.json'), (@{ packagePath=$package; packageSha256=$receipt.packageSha256; importReceipt=($Run + '.receipt.json') } | ConvertTo-Json))
    }
}
"Completed $Action; exit=$code; artifactPassed=$artifactPassed; editorForcedCleanup=$forcedCleanup; log=$log"
