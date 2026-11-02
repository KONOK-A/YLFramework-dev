#requires -Version 7.6
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $DeliveryRoot,
    [Parameter(Mandatory)][ValidateSet('Initialize','PublishTools','AuditPackage','Create','Import','RecoverImport','InstallHarness','SyncHarness','InstallNativeInput','InstallInputProfile','InstallOptional','InstallOptionalTests','SyncOptionalTests','InstallTests','SyncTestSources','InstallSceneLoop','Editor','Tests','Player','RecoverPlayer','Protect','Finalize')][string] $Operation,
    [string] $Repository,
    [ValidatePattern('^(Receiver[A-Z][0-9]*|[a-z][0-9]?)$')][string] $Receiver = 'ReceiverA',
    [ValidatePattern('^[A-Za-z0-9-]+$')][string] $Run = 'initial',
    [ValidateSet('Audit','Generate','VerifyGeneration','ConfigureUrp','BuildMono','BuildIl2Cpp','BuildSceneLoopMono','BuildSceneLoopIl2Cpp','NativeInputGenerate','NativeInputBuildMono','NativeInputBuildIl2Cpp','TestsDiscover','TestsLegacy','TestsLegacyNoReload','TestsNew','TestsBoth','TestsDomainExcel','TestsDOTween','TestsCoexistence','TestsAnalyzerPrepare','TestsAnalyzerInspect','TestsAnalyzerVisualStudioProjects','TestsAnalyzerRiderProjects','TestsAnalyzerAsmdefError','TestsAnalyzerDefaultError','TestsAnalyzerAsmdefNone','TestsAnalyzerDefaultNone','TestsAnalyzerAsmdefInherit','TestsAnalyzerDefaultInherit','DiagnoseReadDefaults')][string] $Method = 'Audit',
    [ValidateSet('Mono','Il2Cpp')][string] $Backend = 'Mono',
    [ValidateSet('EditMode','PlayMode')][string] $TestMode = 'EditMode',
    [string] $TestFilter,
    [string] $TestExclusion,
    [ValidateRange(0,100000)][int] $ExpectedTestCount = 0,
    [string] $CostEvidenceRoot,
    [string] $CostScope,
    [string] $CostPurpose,
    [ValidatePattern('^[A-Za-z0-9-]+$')][string] $AnalyzerPolicyRun,
    [ValidatePattern('^[A-Za-z0-9-]+$')][string] $ProfileRun,
    [ValidateSet('ExpectedLegacy','ExpectedNew','ExpectedBoth')][string] $NativeInputMode,
    [ValidateSet('Excel','DomainExcel','DOTween','HybridCLR')][string] $OptionalIntegration,
    [string] $TestPfx,
    [ValidatePattern('^[A-Za-z0-9-]+$')][string] $BuildRun = 'initial',
    [ValidateSet('smoke','update','probe','rollback','encrypted','missing-key','sample','scene-loop','urp')][string] $Command = 'smoke',
    [ValidatePattern('^[a-z0-9-]+$')][string] $Store = 'main',
    [ValidateRange(1,2)][int] $ExpectedVersion = 1,
    [ValidatePattern('^[A-Za-z0-9-]+$')][string] $ProofRun = 'smoke4',
    [ValidatePattern('^[A-Za-z0-9-]+$')][string] $ImportRun = 'initial',
    [string] $Package = 'YLFramework-Stage9.unitypackage',
    [string] $Unity = 'C:\Program Files\Unity\Hub\Editor\2022.3.62f3c1\Editor\Unity.exe',
    [ValidateSet('2022.3.62f3c1','6000.3.27f1')][string] $ExpectedUnityVersion = '2022.3.62f3c1',
    [ValidateSet('1.0.0','2.0.0')][string] $UguiVersion = '1.0.0',
    [ValidateRange(1,7200)][int] $UnityTimeoutSeconds = 1800
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($Operation -cin @('InstallOptional','InstallOptionalTests','SyncOptionalTests') -and -not $OptionalIntegration) { throw 'Explicit optional integration required.' }
if ($OptionalIntegration -and $Operation -cnotin @('InstallOptional','InstallOptionalTests','SyncOptionalTests','Editor','Tests')) { throw 'Optional integration is only valid for explicit install, discovery or test operations.' }
if ($OptionalIntegration -and $Operation -ceq 'Editor' -and $Method -cne 'TestsDiscover' -and
    -not ($OptionalIntegration -ceq 'DomainExcel' -and $Method -ceq 'TestsDomainExcel') -and
    -not ($OptionalIntegration -ceq 'DOTween' -and $Method -ceq 'TestsDOTween')) { throw 'Optional discovery requires TestsDiscover or the exact optional setup entry.' }
if ($OptionalIntegration -cin @('Excel','DomainExcel') -and $Operation -ceq 'Tests' -and $TestMode -cne 'EditMode') { throw 'Editor-only optional tests require EditMode.' }
if ($Method -ceq 'TestsDomainExcel' -and ($Operation -cne 'Editor' -or $OptionalIntegration -cne 'DomainExcel')) { throw 'DomainExcel setup requires its explicit Editor scope.' }
if ($Method -ceq 'TestsDOTween' -and ($Operation -cne 'Editor' -or $OptionalIntegration -cne 'DOTween')) { throw 'DOTween setup requires its explicit Editor scope.' }
if ($Operation -ceq 'InstallOptionalTests' -and $OptionalIntegration -ceq 'Excel') { throw 'Excel tests already belong to the explicit main test closure.' }
if ($Operation -ceq 'SyncOptionalTests' -and $OptionalIntegration -ceq 'HybridCLR') { throw 'HybridCLR fixture refresh requires a new explicit Common installation.' }
if ($Method -ceq 'TestsCoexistence' -and ($Operation -cne 'Editor' -or $OptionalIntegration)) { throw 'Coexistence requires its own explicit Editor entry without a single integration scope.' }
$nativeInputEntry = $Method -cin @('NativeInputGenerate','NativeInputBuildMono','NativeInputBuildIl2Cpp')
$nativeInputBuild = $Method -cin @('NativeInputBuildMono','NativeInputBuildIl2Cpp')
if ($nativeInputEntry -and $Operation -cne 'Editor') { throw 'Native-input entries require Editor operation.' }
if ($NativeInputMode -and ($Operation -cne 'Editor' -or -not $nativeInputBuild)) { throw 'NativeInputMode is allowed only for explicit native-input builds.' }
if ($nativeInputBuild -and -not $NativeInputMode) { throw 'Explicit native-input mode is required for native-input builds.' }
if ($AnalyzerPolicyRun -and ($Operation -cne 'Editor' -or $Method -cnotin @('TestsAnalyzerInspect','TestsAnalyzerVisualStudioProjects','TestsAnalyzerRiderProjects'))) {
    throw 'A selected Analyzer policy is allowed only for exact read/generation entries, never for mutation or unrelated operations.'
}
$root = [IO.Path]::GetFullPath($DeliveryRoot)
$project = Join-Path $root $Receiver
function Reject-Link([string] $Path) {
    for ($cursor = $Path; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if ([IO.Path]::Exists($cursor) -and ([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint)) { throw "Linked path rejected: $cursor" }
    }
}
function Hash([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Get-Stage9TestInputFiles([string]$Project) {
    $paths=[Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    $pending=[Collections.Generic.Stack[string]]::new()
    $pending.Push((Join-Path $Project 'Assets'))
    while ($pending.Count) {
        $directory=$pending.Pop()
        Reject-Link $directory
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
            Reject-Link $entry
            if ([IO.Directory]::Exists($entry)) { $pending.Push($entry); continue }
            if ([IO.Path]::GetExtension($entry) -cnotin @('.cs','.asmdef','.asmref','.rsp')) { continue }
            $null=$paths.Add([IO.Path]::GetRelativePath($Project,$entry).Replace('\','/'))
            if ([IO.File]::Exists($entry+'.meta')) { $null=$paths.Add([IO.Path]::GetRelativePath($Project,$entry+'.meta').Replace('\','/')) }
        }
    }
    foreach ($path in @('stage9-owner.json','Packages/manifest.json','Packages/packages-lock.json','ProjectSettings/ProjectSettings.asset','ProjectSettings/EditorSettings.asset')) { $null=$paths.Add($path) }
    foreach ($path in $paths) {
        $actual=Join-Path $Project $path
        Reject-Link $actual
        if (-not [IO.File]::Exists($actual)) { throw 'Test binding: a current source/settings input is missing.' }
        @{ Path=$path; Sha256=(Hash $actual) }
    }
}
function Assert-Stage9TestBinding($Discovery, $Profile, [string]$Project, [string]$UnityVersion, [string]$PackageSha256) {
    foreach ($field in @('Schema','OwnerSha256','PackageSha256','InputFiles','CompilationSha256','ClassifierSha256','InputMode','EnterPlayModeOptionsEnabled','EnterPlayModeOptions')) {
        if (-not $Discovery.ContainsKey($field)) { throw 'Test binding: stale unbound discovery; obtain a new actual discovery.' }
    }
    if ($Discovery.Schema -cne 'Stage9TestDiscovery-v2' -or $Discovery.OwnerSha256 -cne (Hash (Join-Path $Project 'stage9-owner.json')) -or
        $Discovery.PackageSha256 -cne $PackageSha256 -or $Discovery.Project -cne $Project -or $Discovery.Unity -cne $UnityVersion -or
        $Discovery.CompilationSha256 -cnotmatch '^[0-9a-f]{64}$' -or $Discovery.ClassifierSha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Test binding: discovery owner/package/graph differs.' }
    $actual=@(Get-Stage9TestInputFiles $Project)
    $expected=@{}
    foreach ($file in $Discovery.InputFiles) {
        if ($expected.ContainsKey($file.Path) -or $file.Path -match '^[/\\]|:|\\|(?:^|/)\.\.?(?:/|$)') { throw 'Test binding: invalid/duplicate input path.' }
        $expected[$file.Path]=$file.Sha256
    }
    if ($actual.Count -ne $expected.Count -or @($actual | Where-Object { -not $expected.ContainsKey($_.Path) -or $expected[$_.Path] -cne $_.Sha256 }).Count) {
        throw 'Test binding: current source/meta/asmdef/settings set differs from discovery; obtain a new discovery.'
    }
    if ($null -ne $Profile) {
        foreach ($field in @('Schema','OwnerSha256','PackageSha256','ClassifierSha256','InputMode','EnterPlayModeOptionsEnabled','EnterPlayModeOptions')) {
            if (-not $Profile.ContainsKey($field)) { throw 'Test binding: stale unbound profile; use a new configured profile.' }
        }
        if ($Profile.Schema -cne 'Stage9TestProfile-v2' -or $Profile.Project -cne $Project -or $Profile.Unity -cne $UnityVersion -or
            $Profile.OwnerSha256 -cne $Discovery.OwnerSha256 -or $Profile.PackageSha256 -cne $Discovery.PackageSha256 -or
            $Profile.ClassifierSha256 -cne $Discovery.ClassifierSha256 -or $Profile.InputMode -ne $Discovery.InputMode -or
            $Profile.EnterPlayModeOptionsEnabled -ne $Discovery.EnterPlayModeOptionsEnabled -or $Profile.EnterPlayModeOptions -cne $Discovery.EnterPlayModeOptions) {
            throw 'Test binding: profile classification/input mode does not match this current discovery.'
        }
    }
}
function Read-SharedText([string] $Path) {
    $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $reader=[IO.StreamReader]::new($stream)
    try { $reader.ReadToEnd() } finally { $reader.Dispose(); $stream.Dispose() }
}
function Read-ExportRoots([string] $Source) {
    $declarations = [regex]::Matches($Source, '(?s)\b_exportRoots\s*=\s*(?:new\s*(?:string)?\s*\[\s*\]\s*)?\{(.*?)\};')
    if ($declarations.Count -ne 1) { throw 'The current formal exporter must declare exactly one _exportRoots array.' }
    $roots = @([regex]::Matches($declarations[0].Groups[1].Value, '"(Assets/[^"\r\n]+)"') | ForEach-Object { $_.Groups[1].Value })
    $required = @('Assets/YLFramework/Editor','Assets/YLFramework/Plugins/BouncyCastle','Assets/YLFramework/Prefabs',
        'Assets/YLFramework/Runtime','Assets/YLFramework/Samples','Assets/YLFramework/Documentation',
        'Assets/YLFramework/LICENSE.md')
    if ($roots.Count -ne $required.Count -or @($roots | Sort-Object -Unique).Count -ne $required.Count -or
        @($required | Where-Object { $roots -cnotcontains $_ }).Count -ne 0) {
        throw 'The formal export closure must contain the exact six product directories and one root file.'
    }
    $roots
}
function Json([string] $Path, $Value) {
    if (Test-Path -LiteralPath $Path) { throw "Preserve existing evidence: $Path" }
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 50), [Text.UTF8Encoding]::new($false))
}
Reject-Link $root
$ownerPath = Join-Path $root 'owner.json'
$owner = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
if (-not $owner.PSObject.Properties['purpose'] -or -not $owner.PSObject.Properties['root'] -or
    -not $owner.PSObject.Properties['repository']) { throw 'Explicit owned delivery identity required.' }
$repo = [IO.Path]::GetFullPath($(if ($Repository) { $Repository } else { $owner.repository }))
if ($root.StartsWith($repo + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Delivery must be outside the repository and all worktrees.' }
if ($owner.purpose -cne 'YLFramework-Stage9-independent-delivery' -or [IO.Path]::GetFullPath($owner.root) -cne $root -or
    [IO.Path]::GetFullPath($owner.repository) -cne $repo) { throw 'Explicit owned delivery identity required.' }
$worktrees = @(& git -C $repo worktree list --porcelain | Where-Object { $_.StartsWith('worktree ') } | ForEach-Object { [IO.Path]::GetFullPath($_.Substring(9)) })
foreach ($tree in $worktrees) { if ($root -eq $tree -or $root.StartsWith($tree + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Delivery lies inside a worktree.' } }
function Protected-Files {
    $relativeRoots = @('Assets/Games','Assets/Scenes','Assets/StreamingAssets','Packages','ProjectSettings','AGENTS.md','.gitignore',
        'Assets/Plugins/Editor Enhancers Bundle','Assets/YLFramework/Tests/Acceptance/Core',
        'Assets/YLFramework/Tests/Fixtures/WorkbenchUX1-20261017/AlternateContent.meta')
    foreach ($relative in $relativeRoots) {
        $path = Join-Path $repo $relative
        if (-not [IO.Path]::Exists($path)) { continue }
        $files = if ([IO.File]::Exists($path)) { @($path) } else { [IO.Directory]::GetFiles($path,'*',[IO.SearchOption]::AllDirectories) }
        foreach ($file in $files) { [ordered]@{ Path=$file; Sha256=Hash $file } }
    }
    foreach ($tree in $worktrees | Where-Object { $_ -like '*fusion-ui-panel-redesign*' }) {
        foreach ($relative in @('Assets/YLFramework','Packages','ProjectSettings')) {
            $path = Join-Path $tree $relative
            if ([IO.Directory]::Exists($path)) {
                foreach ($file in [IO.Directory]::GetFiles($path,'*',[IO.SearchOption]::AllDirectories)) { [ordered]@{ Path=$file; Sha256=Hash $file } }
            }
        }
    }
}
function Verify-Project {
    Reject-Link $project
    $identity = Get-Content -LiteralPath (Join-Path $project 'stage9-owner.json') -Raw | ConvertFrom-Json
    if ($identity.Purpose -cne 'Stage9-independent-receiver' -or $identity.Project -cne $project -or $identity.Delivery -cne $root) { throw 'Receiver identity mismatch.' }
    if ($identity.UnityVersion -cne $ExpectedUnityVersion -or $identity.UguiVersion -cne $UguiVersion) { throw 'Receiver engine/dependency identity mismatch.' }
    $version = Get-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt') -Raw
    if ($version -cnotmatch ('(?m)^m_EditorVersion: ' + [regex]::Escape($ExpectedUnityVersion) + '\r?$')) { throw 'Receiver ProjectVersion mismatch; never upgrade an existing receiver.' }
    foreach ($tree in $worktrees) { if ($project -eq $tree -or $project.StartsWith($tree + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Receiver is a worktree.' } }
}
function Verify-Unity {
    Reject-Link $Unity
    $revision = if ($ExpectedUnityVersion -ceq '6000.3.27f1') { '672d562a42a7' } else { '1623fc0bbb97' }
    $actual = [Diagnostics.FileVersionInfo]::GetVersionInfo([IO.Path]::GetFullPath($Unity)).ProductVersion
    if ($actual -cne ($ExpectedUnityVersion + '_' + $revision)) { throw "Unity executable version mismatch: expected $ExpectedUnityVersion ($revision); found $actual" }
    $requiredUgui = if ($ExpectedUnityVersion -ceq '6000.3.27f1') { '2.0.0' } else { '1.0.0' }
    if ($UguiVersion -cne $requiredUgui) { throw 'UGUI version does not match the selected Editor.' }
}
function Receiver-PackageHash {
    $identity=Get-Content -LiteralPath (Join-Path $project 'stage9-owner.json') -Raw | ConvertFrom-Json
    if ($Package -cne $identity.Package) { throw 'Cannot rebind an existing receiver to another package.' }
    $sha=Hash (Join-Path $root $identity.Package)
    $audit=Get-Content -LiteralPath (Join-Path $root ($identity.Package + '.receipt.json')) -Raw | ConvertFrom-Json
    if (-not $audit.Passed -or $audit.Package -cne $identity.Package -or $sha -cne $identity.PackageSha256 -or $sha -cne $audit.Sha256) { throw 'Receiver/package/audited identity mismatch.' }
    $sha
}
function Assert-Stage9BuildBinding($Report, [string]$Project, [string]$TemplateRoot) {
    if ($Report -isnot [pscustomobject]) { throw 'Build binding: expected a serialized build object.' }
    foreach ($field in @('Schema','OwnerSha256','TemplateSha256','Project','PackageSha256')) {
        if (-not $Report.PSObject.Properties[$field]) { throw 'Build binding: historical unbound build reports are not current native delivery evidence.' }
    }
    foreach ($field in @('Schema','Project','OwnerSha256','PackageSha256')) {
        if ($Report.$field -isnot [string] -or [string]::IsNullOrEmpty($Report.$field)) { throw "Build binding: invalid serialized identity field: $field" }
    }
    if ($Report.OwnerSha256 -cnotmatch '^[0-9a-f]{64}$' -or $Report.PackageSha256 -cnotmatch '^[0-9a-f]{64}$' -or
        $Report.TemplateSha256 -isnot [pscustomobject]) { throw 'Build binding: invalid serialized input hashes.' }
    if ($Report.Schema -cne 'Stage9NativeBuild-v2' -or $Report.Project -cne $Project -or
        $Report.OwnerSha256 -cne (Hash (Join-Path $Project 'stage9-owner.json')) -or
        $Report.PackageSha256 -cne (Hash (Join-Path $root $Package))) { throw 'Build binding: owner/project/package differs from this native delivery.' }
    foreach ($name in @('Stage9DeliveryEditor','Stage9DeliveryProbe','Stage9DeliveryPanel')) {
        $source=Join-Path $TemplateRoot ($name+'.cs.txt')
        $installed=Join-Path $Project ('Assets/Stage9/'+$(if ($name.EndsWith('Editor')) { 'Editor/' } else { '' })+$name+'.cs')
        if (-not $Report.TemplateSha256.PSObject.Properties[$name] -or
            $Report.TemplateSha256.$name -isnot [string] -or $Report.TemplateSha256.$name -cnotmatch '^[0-9a-f]{64}$' -or
            $Report.TemplateSha256.$name -cne (Hash $source) -or $Report.TemplateSha256.$name -cne (Hash $installed)) {
            throw 'Build binding: current source/installed templates differ; obtain a new actual build.'
        }
    }
}
function Run-Unity([string[]] $Arguments, [string] $Log, [switch] $Creating) {
    Verify-Unity
    $processes = @(Get-CimInstance Win32_Process -Filter "Name = 'Unity.exe'")
    if (@($processes | Where-Object { $_.CommandLine -like ('*' + $project + '*') -and $_.CommandLine -notlike '*AssetImportWorker*' }).Count) { throw 'Receiver already running; recover the same result, never launch twice.' }
    if (-not $Creating) { Verify-Project }
    if (Test-Path -LiteralPath $Log) { throw 'Never overwrite a prior Unity log.' }
    $start = [Diagnostics.ProcessStartInfo]::new($Unity)
    $start.UseShellExecute = $false
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    if ($Operation -ceq 'Tests' -and $TestPfx) {
        $evidence=[IO.Path]::GetFullPath((Join-Path (Join-Path ([IO.Path]::GetTempPath()) 'kilo') $owner.runId))
        $pfx=[IO.Path]::GetFullPath($TestPfx)
        Reject-Link $pfx
        if ($pfx -cne (Join-Path $evidence 'loopback.pfx') -or -not [IO.File]::Exists($pfx)) { throw 'Use only this run-owned loopback credential.' }
        $location=Get-Content -LiteralPath (Join-Path $evidence 'carrier-location.json') -Raw | ConvertFrom-Json
        $certificateOwner=Get-Content -LiteralPath (Join-Path $location.Carrier 'r810-owner.json') -Raw | ConvertFrom-Json
        if ($certificateOwner.Repository -cne $repo -or $certificateOwner.Evidence -cne $evidence -or $certificateOwner.Purpose -cne 'Resource-8.10-owned-carrier') { throw 'Loopback credential ownership mismatch.' }
        $certificate=[Security.Cryptography.X509Certificates.X509Certificate2]::new($pfx,'r810-loopback-only',[Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
        try {
            if ($certificate.NotBefore.ToUniversalTime() -gt [DateTime]::UtcNow -or $certificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow -or -not $certificate.HasPrivateKey) { throw 'The owned three-day loopback certificate is not currently usable.' }
        } finally { $certificate.Dispose() }
        $start.Environment['YL_RESOURCE_TEST_PFX']=$pfx
        $start.Environment['YL_RESOURCE_TEST_PASSWORD']='r810-loopback-only'
    }
    $packageSha=Hash (Join-Path $root $Package)
    $limit=if ($Operation -ceq 'Editor' -and $Method -ceq 'TestsDiscover') { [Math]::Min(180,$UnityTimeoutSeconds) } else { $UnityTimeoutSeconds }
    $process = [Diagnostics.Process]::Start($start)
    Json ($Log + '.process.json') @{ Pid=$process.Id; Project=$project; Executable=$Unity; ExpectedUnityVersion=$ExpectedUnityVersion; UguiVersion=$UguiVersion; TimeoutSeconds=$limit; Arguments=@($start.ArgumentList); Package=$Package; PackageSha256=$packageSha; MainEditors=@($processes | Where-Object { $_.CommandLine -notlike '*AssetImportWorker*' } | Select-Object ProcessId,ExecutablePath); StartedUtc=[DateTime]::UtcNow.ToString('O') }
    $clock=[Diagnostics.Stopwatch]::StartNew()
    try {
        $resultIndex=[Array]::IndexOf($Arguments,'-testResults')
        $resultPath=if ($resultIndex -ge 0) { $Arguments[$resultIndex+1] } else { $null }
        $resultsWrittenAt=$null
        $exitStalled=$false
        while (-not ($completed=$process.WaitForExit(1000))) {
            if ($clock.Elapsed.TotalSeconds -ge $limit) { break }
            if ($null -ne $resultsWrittenAt) {
                if ($clock.Elapsed.TotalSeconds-$resultsWrittenAt -ge 120) { $exitStalled=$true; break }
            } elseif ($resultPath -and [IO.File]::Exists($resultPath)) {
                try {
                    $result=[xml](Read-SharedText $resultPath)
                    if ($null -ne $result.DocumentElement -and $result.DocumentElement.LocalName -ceq 'test-run' -and $result.DocumentElement.GetAttribute('end-time')) {
                        $resultsWrittenAt=$clock.Elapsed.TotalSeconds
                    }
                } catch [System.Xml.XmlException] { } catch [System.Management.Automation.PSInvalidCastException] { } catch [System.IO.IOException] { }
            }
        }
        if (-not $completed) { $process.Kill($true); $process.WaitForExit() }
        $code=$process.ExitCode
        Json ($Log + '.exit.json') @{ Pid=$process.Id; Project=$project; ExitCode=$code; TimedOut=(-not $completed -and -not $exitStalled); ForcedExit=-not $completed; FinalXmlObservedWhileRunning=($null -ne $resultsWrittenAt); ExitStalled=$exitStalled; ResultExitGraceSeconds=120; TimeoutSeconds=$limit; EndedUtc=[DateTime]::UtcNow.ToString('O') }
        if ($exitStalled) { throw "Owned Unity did not exit within 120 seconds after final test XML; stopped its own tree, preserve $Log" }
        if (-not $completed) { throw "Owned Unity exceeded $limit seconds and was stopped; preserve $Log" }
        if ($code -ne 0) { throw "Unity exited $code; preserve $Log" }
    }
    finally { $clock.Stop(); $process.Dispose() }
    if ((Read-SharedText $Log) -cnotmatch ('Initialize engine version: ' + [regex]::Escape($ExpectedUnityVersion) + ' \(')) { throw 'Actual Unity log does not establish the expected engine version.' }
}
if ($Operation -eq 'Initialize') {
    Json (Join-Path $root 'protected-before.json') @(Protected-Files)
    Json (Join-Path $root 'source-baseline.json') @{ Head=(& git -C $repo rev-parse HEAD); Branch=(& git -C $repo branch --show-current); Status=@(& git -C $repo status --short --untracked-files=all); Worktrees=$worktrees; Utc=[DateTime]::UtcNow.ToString('O') }
    foreach ($directory in @('Runs','Stores','Tools')) { $null=[IO.Directory]::CreateDirectory((Join-Path $root $directory)) }
    foreach ($file in @('Invoke-Stage9Delivery.ps1','Stage9DeliveryEditor.cs.txt','Stage9DeliveryProbe.cs.txt','Stage9DeliveryPanel.cs.txt','Run-Stage9Representative.ps1')) {
        if ([IO.File]::Exists((Join-Path $PSScriptRoot $file))) { [IO.File]::Copy((Join-Path $PSScriptRoot $file),(Join-Path $root ('Tools/' + $file))) }
    }
    'Initialized independent delivery protection snapshot.'
    return
}
if ($Operation -eq 'PublishTools') {
    $history=Join-Path $root ('ToolsHistory/DeliveryTools-' + $Run)
    if (Test-Path -LiteralPath $history) { throw 'Preserve delivered tool revisions.' }
    $null=[IO.Directory]::CreateDirectory($history); $null=[IO.Directory]::CreateDirectory((Join-Path $root 'Tools'))
    $files=@(foreach ($name in @('Invoke-Stage9Delivery.ps1','Stage9DeliveryEditor.cs.txt','Stage9DeliveryProbe.cs.txt','Stage9DeliveryPanel.cs.txt','Run-Stage9Representative.ps1')) {
        $source=Join-Path $PSScriptRoot $name; $destination=Join-Path $root ('Tools/' + $name)
        if ([IO.File]::Exists($destination)) { [IO.File]::Copy($destination,(Join-Path $history $name)) }
        if ($source -cne $destination) { [IO.File]::Copy($source,$destination,$true) }
        @{ Name=$name; Sha256=Hash $destination }
    })
    Json (Join-Path $history 'receipt.json') @{ Files=$files; Repository=$repo; Root=$root; Utc=[DateTime]::UtcNow.ToString('O') }
    'Published current repeatable tools, preserving the prior snapshot.'
    return
}
if ($Operation -eq 'Protect') {
    $before = Get-Content -LiteralPath (Join-Path $root 'protected-before.json') -Raw | ConvertFrom-Json
    $after = @(Protected-Files); $old=@{}; foreach ($row in $before) { $old[$row.Path]=$row.Sha256 }
    $changes=@(foreach ($row in $after) { if (-not $old.ContainsKey($row.Path) -or $old[$row.Path] -cne $row.Sha256) { $row.Path }; $old.Remove($row.Path) }; $old.Keys)
    $authorized=@(); $unapproved=@($changes)
    if ($Run -eq 'final') {
        $settings=Join-Path $repo 'ProjectSettings/ProjectSettings.asset'
        & git -C $repo diff --quiet HEAD -- 'ProjectSettings/ProjectSettings.asset'
        if ($LASTEXITCODE -ne 0) { throw 'The user-authorized two-default cleanup did not restore the exact baseline representation.' }
        if ($changes -ccontains $settings) { $authorized+=@{ Path=$settings; Sha256=Hash $settings; Basis='User explicitly authorized removing only two Standalone default records; git diff confirms exact clean baseline' } }
        $allowed=@($authorized | ForEach-Object Path)
        $unapproved=@($changes | Where-Object { $allowed -cnotcontains $_ })
    }
    Json (Join-Path $root ('protection-' + $Run + '.json')) @{ Passed=$unapproved.Count -eq 0; Before=$before.Count; After=$after.Count;
        AllDifferences=$changes; AuthorizedDifferences=$authorized; UnapprovedDifferences=$unapproved }
    if ($unapproved.Count) { throw 'Unapproved protected inputs changed; preserve and investigate without reverting.' }
    'Protected files unchanged apart from the exact historical default-cleanup allowance.'
    return
}
if ($Operation -eq 'Finalize') {
    $head=& git -C $repo rev-parse HEAD
    if ($head -cne '586eebedaf900d926d2e30301b20ed3129decb27') { throw 'Historical-only finalization requires its original frozen source; never rebind old receipts to a current delivery.' }
    $first=Get-Content -LiteralPath (Join-Path $root ($Receiver+'-first-import-initial.json')) -Raw | ConvertFrom-Json
    $audit=Get-Content -LiteralPath (Join-Path $root ($Receiver+'-audit-final.json')) -Raw | ConvertFrom-Json
    $protection=Get-Content -LiteralPath (Join-Path $root 'protection-final.json') -Raw | ConvertFrom-Json
    $packageReceipt=Get-Content -LiteralPath (Join-Path $root ($Package+'.receipt.json')) -Raw | ConvertFrom-Json
    $finalFiles=Get-Content -LiteralPath (Join-Path $root ($Package+'.files.json')) -Raw | ConvertFrom-Json
    $testedFiles=Get-Content -LiteralPath (Join-Path $root 'YLFramework-Stage9.unitypackage.files.json') -Raw | ConvertFrom-Json
    if (-not $first.Passed -or $first.HarnessPresent -or -not $audit.Passed -or -not $protection.Passed -or -not $packageReceipt.Passed -or
        $first.PackageSha256 -cne $packageReceipt.Sha256 -or $packageReceipt.Sha256 -cne (Hash (Join-Path $root $Package))) { throw 'Final import/audit/protection/artifact gates are not all established.' }
    $tested=@{}; foreach ($file in $testedFiles) { $tested[$file.Path]=$file }
    $postExportJournal=[Collections.Generic.List[object]]::new()
    $documentationChanges=@(foreach ($file in $finalFiles) {
        $source=Join-Path $repo $file.Path
        if ((Hash ($source+'.meta')) -cne $file.MetaSha256) { throw 'Current formal source meta differs from the frozen final package.' }
        if (-not $file.Folder -and (Hash $source) -cne $file.Sha256) {
            if ($file.Path -cne 'Assets/YLFramework/Documentation/IntegrationPlan.md') { throw 'Current formal source differs from the final exported payload.' }
            $postExportJournal.Add(@{ Path=$file.Path; PackageSha256=$file.Sha256; CurrentSha256=Hash $source;
                Reason='Post-export acceptance journal only; final package is the exact previously compiled documentation snapshot' })
        }
        if (-not $tested.ContainsKey($file.Path)) { throw 'Final package adds an untested product path.' }
        $prior=$tested[$file.Path]
        if ($file.Guid -cne $prior.Guid -or $file.MetaSha256 -cne $prior.MetaSha256) { throw 'Final product identity/meta changed after Player verification.' }
        if ($file.Sha256 -cne $prior.Sha256) {
            if ($file.Path -notlike 'Assets/YLFramework/Documentation/*' -and $file.Path -cne 'Assets/YLFramework/LICENSE.md' -and
                $file.Path -cne 'Assets/YLFramework/Plugins/BouncyCastle/SOURCE.txt') {
                throw "Final product code/binary/resource changed after Player tests: $($file.Path)"
            }
            @{ Path=$file.Path; TestedSha256=$prior.Sha256; FinalSha256=$file.Sha256; Kind='Documentation/license-only finalization; no executable payload changes' }
        }
        $tested.Remove($file.Path)
    })
    if ($tested.Count) { throw 'Final product closure removed a tested path.' }
    $runs=@(
        @{ Backend='Mono'; Run='smoke4' }, @{ Backend='Mono'; Run='mono-update1' }, @{ Backend='Mono'; Run='mono-restart2' },
        @{ Backend='Mono'; Run='mono-rollback1' }, @{ Backend='Mono'; Run='mono-restart1' }, @{ Backend='Mono'; Run='mono-encrypted' },
        @{ Backend='Mono'; Run='mono-missing-key' }, @{ Backend='Mono'; Run='mono-sample' },
        @{ Backend='Il2Cpp'; Run='il2cpp-final-smoke' }, @{ Backend='Il2Cpp'; Run='il2cpp-final-update1' }, @{ Backend='Il2Cpp'; Run='il2cpp-final-restart2' },
        @{ Backend='Il2Cpp'; Run='il2cpp-final-rollback1' }, @{ Backend='Il2Cpp'; Run='il2cpp-final-restart1' }, @{ Backend='Il2Cpp'; Run='il2cpp-final-encrypted' },
        @{ Backend='Il2Cpp'; Run='il2cpp-final-missing-key' }, @{ Backend='Il2Cpp'; Run='il2cpp-final-sample' }
    )
    $verdicts=@(foreach ($case in $runs) {
        $folder=Join-Path $root ('Runs/ReceiverB-'+$case.Backend+'-'+$case.Run)
        $verdict=Get-Content -LiteralPath (Join-Path $folder 'verdict.json') -Raw | ConvertFrom-Json
        if (-not $verdict.Passed -or -not $verdict.IdentityMatched -or $verdict.ProcessExitCode -ne 0 -or $verdict.Development -or
            $verdict.ResultSha256 -cne (Hash (Join-Path $folder 'result.json')) -or $verdict.PlayerLogSha256 -cne (Hash (Join-Path $folder 'Player.log'))) { throw 'A required same-process Player verdict is missing or changed.' }
        @{ Backend=$case.Backend; Run=$case.Run; Verdict=$verdict; VerdictSha256=Hash (Join-Path $folder 'verdict.json') }
    })
    $receiverB=Join-Path $root 'ReceiverB'
    $reports=@(foreach ($name in @('m-ugui1','i-l2')) {
        $build=Join-Path $receiverB ('P/'+$name); $path=Join-Path $build 'BuildReport.json'
        $report=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        Assert-Stage9BuildBinding $report $receiverB $PSScriptRoot
        if ($report.Result -cne 'Succeeded' -or $report.Errors -ne 0 -or $report.Development -or $report.Options -cne 'None') { throw 'Required non-Development build missing.' }
        foreach ($file in $report.PlayerFiles) { if ((Hash (Join-Path $build $file.Path)) -cne $file.Sha256) { throw 'A required build artifact changed.' } }
        if ($name -eq 'i-l2' -and ($report.Stripping -cne 'Low' -or $report.Backend -cne 'IL2CPP')) { throw 'Incorrect IL2CPP support configuration.' }
        @{ Directory=$build; ReportSha256=Hash $path; BuildGuid=$report.BuildGuid; Backend=$report.Backend; Stripping=$report.Stripping; Errors=$report.Errors; Warnings=$report.Warnings }
    })
    $nativeRoot=Join-Path $receiverB 'P/i-l2'
    $backup=Join-Path $nativeRoot 'Stage9Delivery_BackUpThisFolder_ButDontShipItWithYourGame'
    $managed=@(foreach ($dll in [IO.Directory]::GetFiles((Join-Path $backup 'Managed'),'*.dll',[IO.SearchOption]::AllDirectories)) {
        $stream=[IO.File]::OpenRead($dll); $pe=[System.Reflection.PortableExecutable.PEReader]::new($stream)
        try { $metadata=[System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            $references=@($metadata.AssemblyReferences | ForEach-Object { $metadata.GetString($metadata.GetAssemblyReference($_).Name) })
        } finally { $pe.Dispose(); $stream.Dispose() }
        @{ Name=[IO.Path]::GetFileName($dll); Sha256=Hash $dll; References=$references }
    })
    $runtime=@($managed | Where-Object Name -CEQ 'YLFramework.Runtime.dll')
    if ($runtime.Count -ne 1 -or @($runtime[0].References | Where-Object {
        $_ -cnotmatch '^(?:mscorlib|netstandard|System(?:\.[A-Za-z0-9_]+)*|UnityEngine(?:\.[A-Za-z0-9_]+)*|Unity\.ugui|Microsoft\.CSharp|Microsoft\.Win32\.Primitives|Newtonsoft\.Json|BouncyCastle\.Cryptography)$' -or $_ -match 'TestRunner|Tests|Editor|Analyzer'
    }).Count) { throw 'Actual IL2CPP managed input runtime dependency boundary failed.' }
    $generated=@(foreach ($file in [IO.Directory]::GetFiles((Join-Path $backup 'il2cppOutput'),'*',[IO.SearchOption]::AllDirectories)) {
        @{ Path=[IO.Path]::GetRelativePath($backup,$file).Replace('\','/'); Length=([IO.FileInfo]$file).Length; Sha256=Hash $file }
    })
    if (-not @($generated | Where-Object Path -Like '*YLFramework.Runtime*.cpp').Count) { throw 'Actual generated product C++ evidence missing.' }
    $native=@(foreach ($relative in @('GameAssembly.dll','UnityPlayer.dll','Stage9Delivery.exe','Stage9Delivery_Data/il2cpp_data/Metadata/global-metadata.dat')) {
        $file=Join-Path $nativeRoot $relative; @{ Path=$relative; Length=([IO.FileInfo]$file).Length; Sha256=Hash $file }
    })
    $currentEditors=@(Get-CimInstance Win32_Process -Filter "Name = 'Unity.exe'" | Where-Object { $_.CommandLine -notlike '*AssetImportWorker*' })
    if (@($currentEditors | Where-Object { $_.CommandLine -like ('*'+$root+'*') -or $_.CommandLine -like '*YLFramework-Stage9-20261019*' }).Count) { throw 'Owned receiver Editor still running; settle it before final protection.' }
    $main=Get-Content -LiteralPath (Join-Path $root 'main-final-state.json') -Raw | ConvertFrom-Json
    if ($main.Project -cne $repo -or $main.Pid -ne 32168 -or $main.IsPlaying -or $main.IsCompiling -or $main.CompilationFailed -or
        $main.InputHandling -ne 2 -or @($main.Scenes | Where-Object Dirty).Count -or $main.Backend -cne 'Mono2x' -or $main.Stripping -cne 'Disabled') { throw 'Main Editor protection final-state gate failed.' }
    if ($head -cne '586eebedaf900d926d2e30301b20ed3129decb27' -or @(& git -C $repo diff --cached --name-only).Count) { throw 'HEAD or staging changed; do not finalize as an uncommitted delivery.' }
    if ($Run -eq 'preflight') {
        'All required final import, executable identity, Player, IL2CPP native, license and protection gates verified; ready to freeze the final delivery binding.'
        return
    }
    $sourceFiles=@(foreach ($path in @(& git -C $repo diff --name-only) + @(& git -C $repo ls-files --others --exclude-standard)) {
        $absolute=Join-Path $repo $path
        if ([IO.File]::Exists($absolute)) { @{ Path=$path; Sha256=Hash $absolute } }
    })
    $patch=& git -C $repo diff --binary
    [IO.File]::WriteAllText((Join-Path $root 'final-source.patch'),($patch -join "`n")+"`n",[Text.UTF8Encoding]::new($false))
    $value=@{ Passed=$true; Statement='阶段9独立工程交付技术验收完成，待用户验收。'; Baseline=$head; Branch=(& git -C $repo branch --show-current);
        CommitCreated=$false; Staged=$false; Pushed=$false; Repository=$repo; FinalPackage=$packageReceipt;
        FinalReceiver=$first; FinalAudit=$audit; PlayerReceiver=$receiverB; ExecutablePayloadsEqualToPlayerTestedPackage=$true;
        FinalDocumentationDifferences=$documentationChanges; PostExportAcceptanceJournal=$postExportJournal; Builds=$reports; PlayerVerdicts=$verdicts; NativeArtifacts=$native;
        Il2CppManagedInputs=$managed; GeneratedCpp=$generated; MainEditor=$main; Protection=$protection; CurrentSourceFiles=$sourceFiles;
        GitStatus=@(& git -C $repo status --short --untracked-files=all); SourcePatchSha256=Hash (Join-Path $root 'final-source.patch');
        Limitations=@('IL2CPP Low only; prior UI Medium result remains its original scope, not High or other platforms',
        'Current modal input uses actual Canvas raycast and public UGUI handlers, not OS physical input',
        'Audio resource/native playback/release verified; zero output sample is not evidence of audible sound',
        'Historical Scene risk, positive ordinary-CA networking untested, and no zero-native-leak telemetry claim remain',
        'Some owned batch Editors hung/crashed during native exit; preserved and stopped by exact PID, not recorded as normal Editor exit'); Utc=[DateTime]::UtcNow.ToString('O') }
    Json (Join-Path $root 'delivery-binding.json') $value
    [IO.File]::WriteAllText((Join-Path $root 'delivery-binding.sha256'),(Hash (Join-Path $root 'delivery-binding.json'))+"  delivery-binding.json`n",[Text.UTF8Encoding]::new($false))
    $sums=@(
        ((Hash (Join-Path $root $Package))+'  '+$Package)
        ((Hash (Join-Path $root 'delivery-binding.json'))+'  delivery-binding.json')
    )
    [IO.File]::WriteAllText((Join-Path $root 'SHA256SUMS.txt'),($sums -join "`n")+"`n",[Text.UTF8Encoding]::new($false))
    'Final independent delivery binding verified and frozen; no Git write performed.'
    return
}
if ($Operation -eq 'AuditPackage') {
    if ([IO.Path]::GetFileName($Package) -cne $Package) { throw 'Package must be a local delivery filename.' }
    Add-Type -AssemblyName System.Formats.Tar
    $path=Join-Path $root $Package
    $exporter=[IO.File]::ReadAllText((Join-Path $repo 'Assets/YLFramework/Editor/Distribution/ExportUnityPackage.cs'))
    $roots=@(Read-ExportRoots $exporter)
    $expected=@{}; foreach ($sourceRoot in $roots) {
        $absolute=Join-Path $repo $sourceRoot
        $expected[$sourceRoot]=Hash ($absolute + '.meta')
        if ([IO.Directory]::Exists($absolute)) { foreach ($meta in [IO.Directory]::GetFiles($absolute,'*.meta',[IO.SearchOption]::AllDirectories)) { $relative=[IO.Path]::GetRelativePath($repo,$meta).Replace('\','/'); $expected[$relative.Substring(0,$relative.Length-5)]=Hash $meta } }
    }
    $records=@{}; $stream=[IO.File]::OpenRead($path); $gzip=[IO.Compression.GZipStream]::new($stream,[IO.Compression.CompressionMode]::Decompress); $tar=[System.Formats.Tar.TarReader]::new($gzip,$true)
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
        if (-not $expected.ContainsKey($relative) -or $relative -match '/Tests/|Editor Enhancers|AcceptanceBuild|^Packages/|/StreamingAssets/|\.(pfx|p12|pem|key)$|/(bcrypt|ncrypt|crypt32|Newtonsoft\.Json)\.dll$' -or
            ($relative -match '(?i)\.(dll|so|bundle)$' -and $relative -cnotin @('Assets/YLFramework/Plugins/BouncyCastle/BouncyCastle.Cryptography.dll','Assets/YLFramework/Runtime/Core/Analyzers/YLFramework.Analyzers.dll'))) { throw "Excluded/unexpected package path: $relative" }
        $meta=[Text.Encoding]::UTF8.GetString($record['asset.meta'])
        if ($meta -notmatch '(?m)^guid:\s*([0-9a-f]{32})\s*\r?$' -or $Matches[1] -cne $pair.Key) { throw "GUID mismatch: $relative" }
        $metaSha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$record['asset.meta'])).ToLowerInvariant()
        if ($metaSha -cne $expected[$relative]) { throw "Meta changed: $relative" }
        $source=Join-Path $repo $relative; $file=[IO.File]::Exists($source); $sha=$null; $length=0
        if ($file) {
            if (-not $record.ContainsKey('asset')) { throw "Missing payload: $relative" }
            $sha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$record['asset'])).ToLowerInvariant(); $length=$record['asset'].Length
            if ($sha -cne (Hash $source)) { throw "Payload changed: $relative" }
        }
        $expected.Remove($relative)
        [ordered]@{ Path=$relative; Guid=$pair.Key; Folder=(-not $file); Length=$length; Sha256=$sha; MetaSha256=$metaSha }
    })
    if ($expected.Count) { throw 'The formal export closure is incomplete.' }
    Json (Join-Path $root ($Package + '.files.json')) @($rows | Sort-Object Path -CaseSensitive)
    Json (Join-Path $root ($Package + '.receipt.json')) @{ Passed=$true; Package=$Package; Sha256=Hash $path; Bytes=([IO.FileInfo]$path).Length; Records=$rows.Count; Roots=$roots; Head=(& git -C $repo rev-parse HEAD); Status=@(& git -C $repo status --short --untracked-files=all); Utc=[DateTime]::UtcNow.ToString('O') }
    "Exact package/meta/GUID/payload audit passed: $($rows.Count) records, $(Hash $path)"
    return
}
if ($Operation -eq 'Create') {
    Reject-Link $project
    Verify-Unity
    if (Test-Path -LiteralPath $project) { throw 'Never adopt an existing receiver.' }
    $log=Join-Path $root ($Receiver + '-create.log')
    Run-Unity @('-batchmode','-quit','-createProject',$project,'-logFile',$log) $log -Creating
    Json (Join-Path $project 'stage9-owner.json') @{ Purpose='Stage9-independent-receiver'; Project=$project; Delivery=$root; UnityVersion=$ExpectedUnityVersion; UguiVersion=$UguiVersion; Package=$Package; PackageSha256=Hash (Join-Path $root $Package) }
    # 只声明实际必需的独立依赖，保留新工程正常创建的内建模块；不复制主manifest。
    $manifestPath=Join-Path $project 'Packages/manifest.json'
    $manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    foreach ($key in @($manifest.dependencies.Keys)) { if (-not $key.StartsWith('com.unity.modules.')) { $manifest.dependencies.Remove($key) } }
    $manifest.dependencies['com.unity.ugui']=$UguiVersion; $manifest.dependencies['com.unity.nuget.newtonsoft-json']='3.2.1'
    [IO.File]::WriteAllText($manifestPath,($manifest | ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
    'Created receiver using the actual Unity createProject command; minimal dependencies declared.'
    return
}
Verify-Project
if ($Operation -in @('Import','RecoverImport')) {
    $log=Join-Path $root ($Receiver + '-import-' + $Run + '.log')
    $packagePath=Join-Path $root $Package
    $frozenPackageSha=Receiver-PackageHash
    if (Test-Path -LiteralPath (Join-Path $project 'Assets/Stage9')) { throw 'First import cannot use a test harness.' }
    if ($Operation -eq 'Import') {
        Run-Unity @('-batchmode','-quit','-projectPath',$project,'-buildTarget','Win64','-importPackage',$packagePath,'-logFile',$log) $log
    } else {
        $exit=Get-Content -LiteralPath ($log + '.exit.json') -Raw | ConvertFrom-Json
        $invocation=Get-Content -LiteralPath ($log + '.process.json') -Raw | ConvertFrom-Json
        if ($exit.Project -cne $project -or $exit.ExitCode -ne 0 -or (Get-Process -Id $exit.Pid -ErrorAction SilentlyContinue)) { throw 'Recover only the same completed import, never a running or failed one.' }
        if ($invocation.Pid -ne $exit.Pid -or $invocation.Package -cne $Package -or $invocation.PackageSha256 -cne $frozenPackageSha -or
            $invocation.Arguments -cnotcontains $packagePath -or $invocation.Project -cne $project) { throw 'Recovered import must retain its original process/package identity.' }
    }
    if ((Receiver-PackageHash) -cne $frozenPackageSha) { throw 'Package changed during the same import operation.' }
    $errors=@([regex]::Matches((Read-SharedText $log),'(?im)^.*(?:error CS\d+|Scripts have compiler errors|Aborting batchmode due to failure|Multiple precompiled assemblies).*$') | ForEach-Object Value)
    $assemblies=@(foreach ($name in @('YLFramework.Runtime','YLFramework.Editor','YLFramework.ResourceStartup.Sample','YLFramework.ResourceStartup.Sample.Editor')) {
        $assembly=Join-Path $project ('Library/ScriptAssemblies/' + $name + '.dll')
        if (-not [IO.File]::Exists($assembly)) { throw "Actual first-import Unity assembly missing: $name" }
        @{ Name=$name; Sha256=Hash $assembly; Length=([IO.FileInfo]$assembly).Length }
    })
    $passed=$errors.Count -eq 0
    $receiptPath=Join-Path $root ($Receiver + '-first-import-' + $Run + '.json')
    if ($Operation -eq 'RecoverImport' -and (Test-Path -LiteralPath $receiptPath)) {
        $existing=Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
        if ($existing.Passed -ne $passed -or $existing.Project -cne $project -or $existing.Package -cne $Package -or
            $existing.PackageSha256 -cne $frozenPackageSha -or $existing.Log -cne $log -or $existing.HarnessPresent -or
            @($existing.Errors).Count -ne $errors.Count -or @($existing.Assemblies).Count -ne $assemblies.Count) { throw 'Previously collected import identity changed; preserve it.' }
        foreach ($assembly in $assemblies) {
            $prior=@($existing.Assemblies | Where-Object Name -CEQ $assembly.Name)
            if ($prior.Count -ne 1 -or $prior[0].Sha256 -cne $assembly.Sha256) { throw 'Previously collected first-import assemblies changed; never rebind them.' }
        }
    } else {
        Json $receiptPath @{ Passed=$passed; Project=$project; UnityVersion=$ExpectedUnityVersion; UguiVersion=$UguiVersion; Package=$Package; PackageSha256=$frozenPackageSha; HarnessPresent=$false; RecoveredSameResult=$Operation -eq 'RecoverImport'; Log=$log; Errors=$errors; Assemblies=$assemblies; Manifest=(Get-Content -LiteralPath (Join-Path $project 'Packages/manifest.json') -Raw | ConvertFrom-Json); Lock=(Get-Content -LiteralPath (Join-Path $project 'Packages/packages-lock.json') -Raw | ConvertFrom-Json) }
    }
    if (-not $passed) { throw 'Raw product import compilation failed.' }
    'Raw product import compiled in the real independent Unity project before any test facilities.'
    return
}
if ($Operation -in @('InstallHarness','SyncHarness')) {
    $packageSha=Receiver-PackageHash
    $firstPath=Join-Path $root ($Receiver + '-first-import-' + $ImportRun + '.json')
    $first=Get-Content -LiteralPath $firstPath -Raw | ConvertFrom-Json
    if (-not $first.Passed -or $first.HarnessPresent -or $first.Project -cne $project -or $first.PackageSha256 -cne $packageSha -or $first.Package -cne $Package) { throw 'First prove this exact product package alone compiles.' }
    $destination=Join-Path $project 'Assets/Stage9'
    if ($Operation -eq 'InstallHarness' -and (Test-Path -LiteralPath $destination)) { throw 'Never adopt an existing harness.' }
    $history=Join-Path $root ('ToolsHistory/' + $Receiver + '-' + $Run)
    if (Test-Path -LiteralPath $history) { throw 'Preserve harness revision history.' }
    $null=[IO.Directory]::CreateDirectory($history)
    $null=[IO.Directory]::CreateDirectory((Join-Path $destination 'Editor'))
    $changes=@(foreach ($file in @('Stage9DeliveryEditor','Stage9DeliveryProbe','Stage9DeliveryPanel')) {
        $target=if ($file.EndsWith('Editor')) { Join-Path $destination ('Editor/' + $file + '.cs') } else { Join-Path $destination ($file + '.cs') }
        $before=$null
        if ([IO.File]::Exists($target)) { $before=Hash $target; [IO.File]::Copy($target,(Join-Path $history ($file + '.cs.before.txt'))) }
        [IO.File]::Copy((Join-Path $PSScriptRoot ($file + '.cs.txt')),$target,$Operation -eq 'SyncHarness')
        @{ Path=$target; Before=$before; After=Hash $target }
    })
    Json (Join-Path $history 'revision.json') @{ Project=$project; Operation=$Operation; ProductFirstProof=$firstPath; Changes=$changes }
    'Installed test-only consumers after the product-first compile gate.'
    return
}
if ($Operation -eq 'InstallNativeInput') {
    $firstPath=Join-Path $root ($Receiver+'-first-import-'+$ImportRun+'.json')
    if (-not [IO.File]::Exists($firstPath)) { throw 'Product-first proof required before native-input installation.' }
    $packageSha=Receiver-PackageHash
    $first=Get-Content -LiteralPath $firstPath -Raw | ConvertFrom-Json
    if (-not $first.Passed -or $first.HarnessPresent -or $first.Project -cne $project -or
        $first.Package -cne $Package -or $first.PackageSha256 -cne $packageSha) { throw 'Prove the exact bare product package before installing native-input templates.' }
    $destination=Join-Path $project 'Assets/Stage9NativeInput'
    Reject-Link $destination; Reject-Link ($destination+'.meta')
    if ((Test-Path -LiteralPath $destination) -or (Test-Path -LiteralPath ($destination+'.meta'))) { throw 'Never adopt or overwrite native-input templates.' }
    $history=Join-Path $root ('ToolsHistory/'+$Receiver+'-native-input-'+$Run)
    Reject-Link $history
    if (Test-Path -LiteralPath $history) { throw 'Preserve native-input installation history.' }
    $null=[IO.Directory]::CreateDirectory($history)
    $null=[IO.Directory]::CreateDirectory((Join-Path $destination 'Editor'))
    $files=@(foreach ($name in @('Stage9NativeInputProbe','Stage9NativeInputEditor')) {
        $source=Join-Path $PSScriptRoot ($name+'.cs.txt')
        Reject-Link $source
        $target=Join-Path $destination ($(if ($name.EndsWith('Editor')) { 'Editor/' } else { '' })+$name+'.cs')
        [IO.File]::Copy($source,$target,$false)
        [IO.File]::Copy($source,(Join-Path $history ($name+'.cs.txt')),$false)
        @{ Path=$target; SourceSha256=Hash $source; InstalledSha256=Hash $target }
    })
    Json (Join-Path $history 'revision.json') @{ Project=$project; Unity=$ExpectedUnityVersion; PackageSha256=$packageSha; ProductFirstProof=$firstPath; Files=$files }
    'Installed two fresh native-input templates after bare-product proof; Unity owns their new metadata.'
    return
}
if ($Operation -eq 'InstallInputProfile') {
    $firstPath=Join-Path $root ($Receiver+'-first-import-'+$ImportRun+'.json')
    if (-not [IO.File]::Exists($firstPath)) { throw 'Product-first proof required before input-profile installation.' }
    $packageSha=Receiver-PackageHash
    $first=Get-Content -LiteralPath $firstPath -Raw | ConvertFrom-Json
    if (-not $first.Passed -or $first.HarnessPresent -or $first.Project -cne $project -or
        $first.Package -cne $Package -or $first.PackageSha256 -cne $packageSha) { throw 'Exact bare product proof required before installing input configuration.' }
    $native=Join-Path $project 'Assets/Stage9NativeInput'
    $source=Join-Path $PSScriptRoot 'Editor/Stage9ReceptionTests.cs'
    $target=Join-Path $native 'Editor/Stage9ReceptionTests.cs'
    foreach ($path in @($native,$source,$target,($target+'.meta'))) { Reject-Link $path }
    if (-not [IO.File]::Exists((Join-Path $native 'Editor/Stage9NativeInputEditor.cs')) -or
        (Test-Path -LiteralPath (Join-Path $project 'Assets/YLFramework/Tests')) -or
        (Test-Path -LiteralPath (Join-Path $native 'Owned'))) { throw 'Install the Editor-only profile before native generation, without a test tree.' }
    if ((Test-Path -LiteralPath $target) -or (Test-Path -LiteralPath ($target+'.meta'))) { throw 'Never overwrite an input-profile helper.' }
    $history=Join-Path $root ('ToolsHistory/'+$Receiver+'-input-profile-'+$Run)
    Reject-Link $history
    if (Test-Path -LiteralPath $history) { throw 'Preserve input-profile installation history.' }
    $null=[IO.Directory]::CreateDirectory($history)
    [IO.File]::Copy($source,$target,$false)
    [IO.File]::Copy($source,(Join-Path $history 'Stage9ReceptionTests.cs'),$false)
    Json (Join-Path $history 'revision.json') @{ Project=$project; PackageSha256=$packageSha; ProductFirstProof=$firstPath
        SourceSha256=Hash $source; InstalledSha256=Hash $target; Path=$target; RuntimeTestSourcesCopied=$false }
    'Installed only the existing Editor input-profile helper; no test or runtime acceptance tree copied.'
    return
}
if ($Operation -ceq 'SyncOptionalTests') {
    $previousPath=Join-Path $root ($Receiver+'-optional-tests-'+$OptionalIntegration+'-'+$ProofRun+'.json')
    $previous=Get-Content -LiteralPath $previousPath -Raw | ConvertFrom-Json -AsHashtable
    if ($previous.Project -cne $project -or $previous.Unity -cne $ExpectedUnityVersion -or
        $previous.OptionalIntegration -cne $OptionalIntegration -or $previous.PackageSha256 -cne (Receiver-PackageHash)) { throw 'Exact optional-test predecessor required.' }
    foreach ($file in $previous.Files) {
        if ($file.Path.Split('/') -contains '..' -or $file.Path.Contains('\') -or $file.Path.Contains(':')) { throw 'Optional test path escaped its selected integration.' }
        $target=Join-Path $project $file.Path
        Reject-Link $target
        if ((Hash $target) -cne $file.Sha256) { throw 'An optional test changed independently; do not overwrite.' }
    }
    $history=Join-Path $root ('ToolsHistory/'+$Receiver+'-optional-tests-'+$OptionalIntegration+'-'+$Run)
    Reject-Link $history
    if (Test-Path -LiteralPath $history) { throw 'Preserve optional test source history.' }
    $null=[IO.Directory]::CreateDirectory($history)
    $changed=@(foreach ($file in $previous.Files) {
        if ($file.Path -cin @('Assets/YLFramework/Tests.meta','Assets/YLFramework/Tests/Integrations.meta')) { continue }
        $source=Join-Path $repo $file.Path; $target=Join-Path $project $file.Path
        Reject-Link $source
        if ($file.Path -cne ('Assets/YLFramework/Tests/Integrations/'+$OptionalIntegration+'.meta') -and
            $file.Path -cnotmatch ('^Assets/YLFramework/Tests/Integrations/'+$OptionalIntegration+'/')) { throw 'Optional test path escaped its selected integration.' }
        if ([IO.Path]::GetExtension($file.Path) -cin @('.cs','.asmdef','.asmref') -and (Hash $source) -cne $file.Sha256) {
            $backup=Join-Path $history $file.Path
            $null=[IO.Directory]::CreateDirectory((Split-Path $backup -Parent))
            [IO.File]::Copy($target,$backup,$false)
            [IO.File]::Copy($source,$target,$true)
            @{ Path=$file.Path; Before=$file.Sha256; After=Hash $target }
            $file.Sha256=Hash $target
        }
    })
    Json (Join-Path $history 'revision.json') @{ Previous=$previousPath; Changed=$changed }
    Json (Join-Path $root ($Receiver+'-optional-tests-'+$OptionalIntegration+'-'+$Run+'.json')) $previous
    'Synchronized only receipt-owned optional test code; product files, meta and asset data retained.'
    return
}
if ($Operation -cin @('InstallOptional','InstallOptionalTests')) {
    $firstPath=Join-Path $root ($Receiver+'-first-import-'+$ImportRun+'.json')
    if (-not [IO.File]::Exists($firstPath)) { throw 'A bare-product import proof is required before optional installation.' }
    $packageSha=Receiver-PackageHash
    $first=Get-Content -LiteralPath $firstPath -Raw | ConvertFrom-Json
    if (-not $first.Passed -or $first.HarnessPresent -or $first.Project -cne $project -or $first.PackageSha256 -cne $packageSha -or
        $first.Package -cne $Package -or $first.UnityVersion -cne $ExpectedUnityVersion) { throw 'Wrong bare-product predecessor for optional installation.' }
    $productRoot=Join-Path $project 'Assets/YLFramework'
    $productMeta=$productRoot+'.meta'
    Reject-Link $productRoot; Reject-Link $productMeta
    if (-not [IO.Directory]::Exists($productRoot) -or -not [IO.File]::Exists($productMeta) -or
        [IO.File]::ReadAllText($productMeta) -cnotmatch '(?m)^guid: [0-9a-f]{32}\r?$' -or
        [IO.File]::ReadAllText($productMeta) -cnotmatch '(?m)^folderAsset: yes\r?$') { throw 'Imported product root folder identity required.' }
    $productMetaSha=Hash $productMeta
    $relative=if ($Operation -ceq 'InstallOptional') { 'Assets/YLFramework/Integrations/'+$OptionalIntegration } else { 'Assets/YLFramework/Tests/Integrations/'+$OptionalIntegration }
    $source=Join-Path $repo $relative; $target=Join-Path $project $relative
    foreach ($path in @($source,$target,($source+'.meta'),($target+'.meta'))) { Reject-Link $path }
    if ((Test-Path -LiteralPath $target) -or (Test-Path -LiteralPath ($target+'.meta'))) { throw 'Never adopt or overwrite an optional integration.' }
    $dependencyPath=Join-Path $repo ('Assets/YLFramework/Integrations/'+$OptionalIntegration+'/Editor/Dependencies.json')
    if ($OptionalIntegration -ceq 'HybridCLR') { $dependencyPath=Join-Path $repo 'Assets/YLFramework/Integrations/HybridCLR/Dependencies.json' }
    $copyRoots=@($relative)
    $licenseSha=$null
    if ($OptionalIntegration -ceq 'DOTween') {
        $vendorSha='d74bfc3f37cd97d90f7f08a9a73e204c5eae66e77be24b12378a4e99f1690843'
        $vendorRelative='Assets/Plugins/Demigiant/DOTween/DOTween.dll'
        $dependencyPath=Join-Path $repo $vendorRelative
        foreach ($path in @($dependencyPath,($dependencyPath+'.meta'))) { Reject-Link $path }
        if (-not [IO.File]::Exists($dependencyPath) -or ([IO.FileInfo]$dependencyPath).Length -ne 177664 -or
            (Hash $dependencyPath) -cne $vendorSha -or
            -not [IO.File]::Exists($dependencyPath+'.meta')) { throw 'Exact local official DOTween 1.2.825 dependency required.' }
        $vendorMetaSha=Hash ($dependencyPath+'.meta')
        $vendorRoot='Assets/Plugins/Demigiant/DOTween'
        $vendorInputs=@{
            'DOTween.dll'=$vendorSha
            'Modules/DOTweenModuleAudio.cs'='4352a6a6fe6deecf3ee1c61187f7dd608f70d5ce6033b3c579f69f23c1277f16'
            'Modules/DOTweenModuleEPOOutline.cs'='b31faa9d2d8c3f00721dc21d680dbd107abf66716bcc754cb721e4995ee022b9'
            'Modules/DOTweenModulePhysics.cs'='26678b87b7fc1a56441910a12aef1647558dc83feb94604d588c4369c690fc81'
            'Modules/DOTweenModulePhysics2D.cs'='7f3dd38cb0ac650de48028ac3d2600c4ea3afb9452a4ccccbd1d6fbf5b7deb82'
            'Modules/DOTweenModuleSprite.cs'='008b0398dc3235220aa9ec9f025d7fda51e935544249829d828eef935c29f578'
            'Modules/DOTweenModuleUI.cs'='24a9c989ce632168827b57290b7f30132bf2e3dd119edb180546ae7ce13a340e'
            'Modules/DOTweenModuleUIToolkit.cs'='6a8d50769554305b9294cb89b40e3ef88bbd44b59ce494a8c8afc9759426258d'
            'Modules/DOTweenModuleUnityVersion.cs'='26126010ad8b836b74ebe7524baccd6d4cc04da4064cd840e92d4060194cf71d'
            'Modules/DOTweenModuleUtils.cs'='c52ea07bf4013c59659a9fcc54c45c3fe3080c8dbac29ff5f17f1e58cf7fa190'
            'readme.txt'='37b99ffdbb8b01b159d807b1fe77db664ae5b3b201734a5ad602c7c7f83941f0'
        }
        $vendorInputMeta=@{}
        foreach ($name in $vendorInputs.Keys) {
            $inputPath=Join-Path $repo ($vendorRoot+'/'+$name)
            Reject-Link $inputPath; Reject-Link ($inputPath+'.meta')
            if (-not [IO.File]::Exists($inputPath) -or -not [IO.File]::Exists($inputPath+'.meta') -or
                (Hash $inputPath) -cne $vendorInputs[$name]) { throw 'Exact local official DOTween 1.2.825 runtime dependency required.' }
            $vendorInputMeta[$name]=Hash ($inputPath+'.meta')
        }
        if ([IO.Directory]::GetFileSystemEntries((Join-Path $repo ($vendorRoot+'/Modules'))).Count -ne 18) {
            throw 'Exact local official DOTween 1.2.825 runtime dependency required.'
        }
        $wrapperRoot='Assets/YLFramework/Integrations/DOTween/Runtime'
        $wrapperBase=if ($Operation -ceq 'InstallOptionalTests') { $project } else { $repo }
        foreach ($name in @('DOTweenAction.cs','YLFramework.Actions.DOTween.asmdef','csc.rsp')) {
            $inputPath=Join-Path $wrapperBase ($wrapperRoot+'/'+$name)
            Reject-Link $inputPath; Reject-Link ($inputPath+'.meta')
            if (-not [IO.File]::Exists($inputPath) -or -not [IO.File]::Exists($inputPath+'.meta')) { throw 'Install the exact DOTween wrapper and local dependency before its tests.' }
        }
        if ($Operation -ceq 'InstallOptionalTests') {
            $installedDll=Join-Path $project $vendorRelative
            Reject-Link $installedDll; Reject-Link ($installedDll+'.meta')
            if (-not [IO.File]::Exists($installedDll) -or (Hash $installedDll) -cne (Hash $dependencyPath) -or
                -not [IO.File]::Exists($installedDll+'.meta') -or (Hash ($installedDll+'.meta')) -cne (Hash ($dependencyPath+'.meta'))) { throw 'Install the exact DOTween wrapper and local dependency before its tests.' }
            foreach ($name in $vendorInputs.Keys) {
                $installed=Join-Path $project ($vendorRoot+'/'+$name)
                Reject-Link $installed; Reject-Link ($installed+'.meta')
                if (-not [IO.File]::Exists($installed) -or -not [IO.File]::Exists($installed+'.meta') -or
                    (Hash $installed) -cne $vendorInputs[$name] -or (Hash ($installed+'.meta')) -cne $vendorInputMeta[$name]) {
                    throw 'Install the exact DOTween runtime modules and readme before its tests.'
                }
            }
            foreach ($mode in @('EditMode','PlayMode')) {
                $asmdef=Join-Path $source ($mode+'/YLFramework.Actions.DOTween.Tests.'+$mode+'.asmdef')
                Reject-Link $asmdef
                $definition=Get-Content -LiteralPath $asmdef -Raw | ConvertFrom-Json
                if ($definition.name -cne ('YLFramework.Actions.DOTween.Tests.'+$mode) -or
                    $definition.defineConstraints -cnotcontains 'DOTWEEN' -or $definition.defineConstraints -cnotcontains 'UNITY_INCLUDE_TESTS') { throw 'Exact two-platform DOTween test assemblies required.' }
            }
        } else {
            # Local owned-receiver dependency only; this is not a vendor redistribution/export entry.
            $copyRoots+=@($vendorRelative,($vendorRoot+'/Modules'),($vendorRoot+'/readme.txt'))
        }
        $license=Join-Path $repo 'Assets/YLFramework/LICENSE.md'
        $installedLicense=Join-Path $project 'Assets/YLFramework/LICENSE.md'
        foreach ($path in @($license,$installedLicense,($license+'.meta'),($installedLicense+'.meta'))) { Reject-Link $path }
        if (-not [IO.File]::Exists($installedLicense) -or (Hash $installedLicense) -cne (Hash $license) -or
            (Hash ($installedLicense+'.meta')) -cne (Hash ($license+'.meta'))) { throw 'Retain the exact framework license and its identity beside optional sources.' }
        $licenseSha=Hash $license
    } else {
        $dependency=Get-Content -LiteralPath $dependencyPath -Raw | ConvertFrom-Json
    }
    if ($OptionalIntegration -ceq 'Excel') {
        $dll=Join-Path $source 'Editor/Plugins/MiniExcel.dll'
        if (-not $dependency.optionalEditorOnly -or $dependency.defaultExport -or $dependency.backend.version -cne '1.31.2' -or
            $dependency.backend.assemblySha256 -cne '0a49b66224bef9fdcec12800e4660267a993b1ece87727d9fe15c7dabf58ba1e' -or
            (Hash $dll) -cne $dependency.backend.assemblySha256 -or
            -not [IO.File]::Exists((Join-Path $source 'Editor/Licenses/MiniExcel-LICENSE.txt'))) { throw 'Exact licensed Editor-only MiniExcel dependency required.' }
    } elseif ($OptionalIntegration -ceq 'DomainExcel') {
        $excelDll=Join-Path $project 'Assets/YLFramework/Integrations/Excel/Editor/Plugins/MiniExcel.dll'
        if ($dependency.requiredCommit -cne '6a9e2508a42333e55c82e6f29eea136643af4467' -or
            $dependency.requiredAssembly -cne 'YLFramework.Excel.Editor' -or $dependency.define -cne 'YL_DOMAIN_EXCEL' -or
            -not [IO.File]::Exists($excelDll) -or (Hash $excelDll) -cne '0a49b66224bef9fdcec12800e4660267a993b1ece87727d9fe15c7dabf58ba1e') { throw 'Install the exact Excel prerequisite before DomainExcel.' }
        if ($Operation -ceq 'InstallOptionalTests' -and -not [IO.File]::Exists((Join-Path $project 'Assets/YLFramework/Integrations/DomainExcel/Editor/YLFramework.Domain.Excel.Editor.asmdef'))) { throw 'Install the DomainExcel product before its tests.' }
    } elseif ($OptionalIntegration -ceq 'HybridCLR') {
        if (-not $dependency.optional -or $dependency.defaultExport -or $dependency.package.name -cne 'com.code-philosophy.hybridclr' -or
            $dependency.package.version -cne '9.0.3' -or $dependency.package.url -cne 'https://gitee.com/focus-creative-games/hybridclr_unity.git#v9.0.3' -or
            $dependency.package.commit -cne '6cadd24d101f4cbd00cd80190106f6621132d24d' -or $dependency.package.bundled) { throw 'Exact unbundled official HybridCLR 9.0.3 source contract required.' }
        if ($Operation -ceq 'InstallOptional') {
            $copyRoots=@(($relative+'/Runtime'),($relative+'/Editor'),($relative+'/Samples'),($relative+'/Dependencies.json'),($relative+'/README.md'))
        } elseif (-not [IO.File]::Exists((Join-Path $project 'Assets/YLFramework/Integrations/HybridCLR/Editor/YLFramework.HybridCLR.Editor.asmdef'))) {
            throw 'Install the exact HybridCLR product and Samples before Common tests.'
        }
    }
    $kind=if ($Operation -ceq 'InstallOptionalTests') { 'optional-tests-' } else { 'optional-' }
    $receipt=Join-Path $root ($Receiver+'-'+$kind+$OptionalIntegration+'-'+$Run+'.json')
    if (Test-Path -LiteralPath $receipt) { throw 'Preserve optional installation receipt.' }
    $planned=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    $directories=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($copyRoot in $copyRoots) {
        $sourceRoot=Join-Path $repo $copyRoot; $targetRoot=Join-Path $project $copyRoot
        foreach ($path in @($sourceRoot,$targetRoot,($sourceRoot+'.meta'),($targetRoot+'.meta'))) { Reject-Link $path }
        if ((Test-Path -LiteralPath $targetRoot) -or (Test-Path -LiteralPath ($targetRoot+'.meta'))) { throw 'Never adopt or overwrite an optional integration or local dependency.' }
        $pending=[Collections.Generic.Stack[string]]::new(); $pending.Push($sourceRoot)
        while ($pending.Count) {
            $inputPath=$pending.Pop(); Reject-Link $inputPath
            $destination=Join-Path $project ([IO.Path]::GetRelativePath($repo,$inputPath))
            if ([IO.Directory]::Exists($inputPath)) {
                $null=$directories.Add($destination)
                foreach ($child in [IO.Directory]::EnumerateFileSystemEntries($inputPath)) { $pending.Push($child) }
            } elseif ([IO.File]::Exists($inputPath)) { $planned[$destination]=$inputPath }
            else { throw "Required optional source missing: $inputPath" }
            if (-not $inputPath.EndsWith('.meta',[StringComparison]::OrdinalIgnoreCase)) {
                $meta=$inputPath+'.meta'; Reject-Link $meta
                if (-not [IO.File]::Exists($meta) -or [IO.File]::ReadAllText($meta) -cnotmatch '(?m)^guid: [0-9a-f]{32}\r?$') { throw "Optional source identity missing: $meta" }
                $planned[$destination+'.meta']=$meta
            }
        }
        # 默认导出不包含框架父目录，保留 Unity 在接收端生成的独立身份。
        $parentBoundary=if ($copyRoot.StartsWith('Assets/YLFramework/',[StringComparison]::Ordinal)) { $productRoot } else { Join-Path $project 'Assets' }
        for ($parent=Split-Path $targetRoot -Parent; $parent -cne $parentBoundary; $parent=Split-Path $parent -Parent) {
            $sourceParent=Join-Path $repo ([IO.Path]::GetRelativePath($project,$parent))
            foreach ($path in @($parent,($parent+'.meta'),$sourceParent,($sourceParent+'.meta'))) { Reject-Link $path }
            if (Test-Path -LiteralPath $parent) {
                if (-not [IO.Directory]::Exists($parent) -or -not [IO.File]::Exists($parent+'.meta') -or
                    (Hash ($parent+'.meta')) -cne (Hash ($sourceParent+'.meta'))) { throw 'Existing optional parent identity differs.' }
            } else {
                if (Test-Path -LiteralPath ($parent+'.meta')) { throw 'Do not adopt a partial optional parent.' }
                if (-not [IO.File]::Exists($sourceParent+'.meta')) { throw 'Optional source parent identity missing.' }
                $null=$directories.Add($parent); $planned[$parent+'.meta']=$sourceParent+'.meta'
            }
        }
    }
    foreach ($destination in $planned.Keys) {
        Reject-Link $destination
        if (Test-Path -LiteralPath $destination) { throw 'Never adopt or overwrite an optional file or metadata.' }
    }
    if ($OptionalIntegration -ceq 'DOTween' -and ((Hash $dependencyPath) -cne $vendorSha -or
        (Hash ($dependencyPath+'.meta')) -cne $vendorMetaSha)) { throw 'Exact local official DOTween 1.2.825 dependency required.' }
    foreach ($directory in $directories) { $null=[IO.Directory]::CreateDirectory($directory) }
    $files=@(foreach ($destination in ($planned.Keys | Sort-Object -CaseSensitive)) {
        $inputPath=$planned[$destination]
        [IO.File]::Copy($inputPath,$destination,$false)
        if ((Hash $inputPath) -cne (Hash $destination)) { throw 'Optional source changed during copy.' }
        @{ Path=[IO.Path]::GetRelativePath($project,$destination).Replace('\','/'); Sha256=Hash $destination }
    })
    if ($OptionalIntegration -ceq 'DOTween' -and ((Hash $dependencyPath) -cne $vendorSha -or
        (Hash (Join-Path $project $vendorRelative)) -cne $vendorSha -or
        (Hash ((Join-Path $project $vendorRelative)+'.meta')) -cne $vendorMetaSha)) { throw 'Exact local official DOTween 1.2.825 dependency required.' }
    if ($OptionalIntegration -ceq 'DOTween') {
        foreach ($name in $vendorInputs.Keys) {
            $inputPath=Join-Path $repo ($vendorRoot+'/'+$name)
            $installed=Join-Path $project ($vendorRoot+'/'+$name)
            if ((Hash $inputPath) -cne $vendorInputs[$name] -or (Hash $installed) -cne $vendorInputs[$name] -or
                (Hash ($inputPath+'.meta')) -cne $vendorInputMeta[$name] -or (Hash ($installed+'.meta')) -cne $vendorInputMeta[$name]) {
                throw 'Exact local official DOTween 1.2.825 runtime dependency required.'
            }
        }
    }
    if ((Hash $productMeta) -cne $productMetaSha) { throw 'Imported product root identity changed during optional installation.' }
    Json $receipt @{ Project=$project; Unity=$ExpectedUnityVersion; PackageSha256=$packageSha; OptionalIntegration=$OptionalIntegration
        BareProductProof=$firstPath; DependencySha256=Hash $dependencyPath; Files=$files; DefaultPackageChanged=$false
        ProductRootMetaSha256=$productMetaSha
        RuntimeDependencyAdded=($OptionalIntegration -ceq 'DOTween' -and $Operation -ceq 'InstallOptional')
        LocalDependencyCopy=($OptionalIntegration -ceq 'DOTween' -and $Operation -ceq 'InstallOptional'); VendorRedistributionAuthorized=$false
        VendorVersion=$(if ($OptionalIntegration -ceq 'DOTween') { '1.2.825' } else { $null }); FrameworkLicenseSha256=$licenseSha }
    'Installed the exact optional source/test closure without altering the default package.'
    return
}
if ($Operation -eq 'InstallTests') {
    $packageSha=Receiver-PackageHash
    $first=Get-Content -LiteralPath (Join-Path $root ($Receiver+'-first-import-'+$ImportRun+'.json')) -Raw | ConvertFrom-Json
    if (-not $first.Passed -or $first.HarnessPresent -or $first.Project -cne $project -or $first.PackageSha256 -cne $packageSha) { throw 'Prove the original product-only import before installing tests.' }
    $destination=Join-Path $project 'Assets/YLFramework/Tests'
    if (Test-Path -LiteralPath $destination) { throw 'Never adopt or overwrite an existing test tree.' }
    $testSource=Join-Path $repo 'Assets/YLFramework/Tests'
    $selected=@('EditMode','PlayMode','Acceptance/Core','Fixtures/Core/Editor','Fixtures/Core/YLCoreResourceModule',
        'Fixtures/Audio','Fixtures/CommonSystem','Fixtures/NamespaceMigration','Fixtures/Extensions/Behavior','Fixtures/Extensions/Shared',
        'Fixtures/Extensions/Domain/DomainAcceptanceProbe.cs','Fixtures/ResourceMultiDirectory-20261018','Tools/Editor/Stage9ReceptionTests.cs')
    $files=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $null=$files.Add($testSource+'.meta')
    foreach ($relative in $selected) {
        $source=Join-Path $testSource $relative
        $pending=[Collections.Generic.Stack[string]]::new(); $pending.Push($source)
        while ($pending.Count) {
            $item=$pending.Pop(); Reject-Link $item
            if ([IO.Directory]::Exists($item)) { foreach ($child in [IO.Directory]::EnumerateFileSystemEntries($item)) { $pending.Push($child) } }
            elseif ([IO.File]::Exists($item)) { $null=$files.Add($item) }
            else { throw "Required test input missing: $item" }
        }
        for ($cursor=$source; $cursor -ne $testSource; $cursor=[IO.Path]::GetDirectoryName($cursor)) {
            $meta=$cursor+'.meta'; Reject-Link $meta
            if (-not [IO.File]::Exists($meta)) { throw "Test identity missing: $meta" }
            $null=$files.Add($meta)
        }
    }
    $copied=@(foreach ($source in ($files | Sort-Object)) {
        $relative=[IO.Path]::GetRelativePath((Join-Path $repo 'Assets/YLFramework'),$source)
        $target=Join-Path $project ('Assets/YLFramework/'+$relative)
        Reject-Link $target
        $null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        [IO.File]::Copy($source,$target,$false)
        @{ Path=$relative.Replace('\','/'); SourceSha256=Hash $source; ReceiverSha256=Hash $target }
    })
    Json (Join-Path $root ($Receiver+'-tests-inputs-'+$Run+'.json')) @{ Project=$project; PackageSha256=$packageSha; Roots=$selected; Files=$copied }
    'Installed the complete selected test/helper closure without modifying any assertion.'
    return
}
if ($Operation -eq 'SyncTestSources') {
    $null=Receiver-PackageHash
    $prior=Get-Content -LiteralPath (Join-Path $root ($Receiver+'-tests-inputs-'+$ProofRun+'.json')) -Raw | ConvertFrom-Json -AsHashtable
    if ($prior.Project -cne $project) { throw 'Only update the exact owned test input manifest.' }
    $history=Join-Path $root ('ToolsHistory/'+$Receiver+'-tests-'+$Run)
    $receipt=Join-Path $root ($Receiver+'-tests-inputs-'+$Run+'.json')
    if ((Test-Path -LiteralPath $history) -or (Test-Path -LiteralPath $receipt)) { throw 'Never overwrite test source history.' }
    $null=[IO.Directory]::CreateDirectory($history)
    $pendingChanges=@(foreach ($file in $prior.Files) {
        $relative=$file.Path
        if ([IO.Path]::GetExtension($relative) -cnotin @('.cs','.asmdef','.asmref')) { continue }
        if (-not $relative.StartsWith('Tests/',[StringComparison]::Ordinal) -or $relative.Split('/') -contains '..') { throw 'Invalid test input path.' }
        $source=Join-Path $repo ('Assets/YLFramework/'+$relative)
        $target=Join-Path $project ('Assets/YLFramework/'+$relative)
        Reject-Link $source; Reject-Link $target
        $sourceHash=Hash $source; $targetHash=Hash $target
        if ($targetHash -cne $file.ReceiverSha256) { throw "Owned test source changed since its receipt: $relative" }
        if ($sourceHash -ceq $file.SourceSha256) { continue }
        @{ File=$file; Source=$source; Target=$target; Before=$targetHash; After=$sourceHash }
    })
    $changes=@(foreach ($change in $pendingChanges) {
        [IO.File]::Copy($change.Target,(Join-Path $history ($change.File.Path.Replace('/','_')+'.before.txt')),$false)
        [IO.File]::Copy($change.Source,$change.Target,$true)
        if ((Hash $change.Target) -cne $change.After) { throw 'Source changed while copying a test correction.' }
        $change.File.SourceSha256=$change.After; $change.File.ReceiverSha256=$change.After
        @{ Path=$change.File.Path; Before=$change.Before; After=$change.After }
    })
    Json (Join-Path $history 'revision.json') @{ Project=$project; Predecessor=$ProofRun; Changes=$changes }
    Json $receipt $prior
    'Synchronized only receipt-owned test sources; all assets and GUIDs retained.'
    return
}
if ($Operation -eq 'InstallSceneLoop') {
    $destination=Join-Path $project 'Assets/Stage9'
    if (Test-Path -LiteralPath $destination) { throw 'Never adopt an existing control fixture.' }
    if (Test-Path -LiteralPath (Join-Path $project 'Assets/YLFramework')) { throw 'Pure Unity control must not contain the product.' }
    $null=[IO.Directory]::CreateDirectory((Join-Path $destination 'Editor'))
    [IO.File]::Copy((Join-Path $PSScriptRoot 'Stage9SceneLoopEditor.cs.txt'),(Join-Path $destination 'Editor/Stage9SceneLoopEditor.cs'))
    [IO.File]::Copy((Join-Path $PSScriptRoot 'Stage9SceneLoopProbe.cs.txt'),(Join-Path $destination 'Stage9SceneLoopProbe.cs'))
    'Installed only the no-YL scene control in an empty receiver.'
    return
}
if ($Operation -eq 'Tests') {
    $packageSha=Receiver-PackageHash
    $discoveryPath=Join-Path $root ($Receiver+'-tests-discovery-'+$ProofRun+'.json')
    $discovery=Get-Content -LiteralPath $discoveryPath -Raw | ConvertFrom-Json -AsHashtable
    if (-not $discovery.Passed -or $discovery.SourceHost -or $discovery.Project -cne $project -or $discovery.Unity -cne $ExpectedUnityVersion) { throw 'Actual matching receiver discovery required.' }
    $rows=$discovery[$TestMode]
    $profilePath=$null
    $profile=$null
    if ($ProfileRun) {
        $profilePath=Join-Path $root ($Receiver+'-tests-profile-'+$ProfileRun+'.json')
        $profile=Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json -AsHashtable
        if ($profile.Project -cne $project -or $profile.Unity -cne $ExpectedUnityVersion) { throw 'Test profile identity mismatch.' }
        $rules=@{ 'source-host'=$profile.HostOnly; 'historical-bound'=$profile.Historical; 'optional-excel'=$profile.Excel }
        if ($profile.ContainsKey('PlatformBound')) { $rules['platform-not-applicable']=$profile.PlatformBound }
        $rows=@(foreach ($row in $rows) {
            $scope=if ($row.Assembly -cin @('YLFramework.Actions.DOTween.Tests.EditMode','YLFramework.Actions.DOTween.Tests.PlayMode')) { 'optional-dotween' }
                elseif ($row.Assembly -cin @('YLFramework.HybridCLR.Tests.EditMode','YLFramework.HybridCLR.Tests.PlayMode')) { 'optional-hybridclr' }
                elseif ($row.Assembly -ceq 'YLFramework.Domain.Excel.Tests') { 'optional-domainexcel' } else { 'receiver' }
            if ($scope -ceq 'receiver') {
                foreach ($kind in @('source-host','historical-bound','platform-not-applicable','optional-excel')) {
                    foreach ($prefix in $rules[$kind]) {
                        if ($row.Name -ceq $prefix -or $row.Name.StartsWith($prefix+'.',[StringComparison]::Ordinal) -or $row.Name.StartsWith($prefix+'(',[StringComparison]::Ordinal)) { $scope=$kind }
                    }
                }
            }
            @{ Name=$row.Name; Assembly=$row.Assembly; RunState=$row.RunState; Scope=$scope }
        })
    }
    Assert-Stage9TestBinding $discovery $profile $project $ExpectedUnityVersion $packageSha
    $filter=if ($TestFilter) { $TestFilter } else { '.*' }
    $selectedScope=if ($OptionalIntegration -ceq 'Excel') { 'optional-excel' } elseif ($OptionalIntegration -ceq 'DomainExcel') { 'optional-domainexcel' }
        elseif ($OptionalIntegration -ceq 'HybridCLR') { 'optional-hybridclr' }
        elseif ($OptionalIntegration -ceq 'DOTween') { 'optional-dotween' } else { 'receiver' }
    if ($OptionalIntegration -and (-not $discovery.ContainsKey('OptionalIntegration') -or $discovery.OptionalIntegration -cne $OptionalIntegration)) { throw 'Exact optional discovery context required.' }
    $selected=@($rows | Where-Object { $_.Scope -ceq $selectedScope -and [regex]::IsMatch($_.Name,$filter) -and (-not $TestExclusion -or -not [regex]::IsMatch($_.Name,$TestExclusion)) })
    if ($selected.Count -eq 0) { throw 'Never pass a run that selected no applicable discovered tests.' }
    if ($ExpectedTestCount -le 0) { throw 'An explicit positive expected test count is required; discovery is not the test intent.' }
    if ($ExpectedTestCount -gt 0 -and $selected.Count -ne $ExpectedTestCount) {
        throw "The explicit test intent expected $ExpectedTestCount cases, but discovery selected $($selected.Count); refuse a partial match."
    }
    # Unity groupName 匹配父 suite；排除须使用原生 NotFilter，不能用负向前瞻包含式。
    $nativeFilters=[Collections.Generic.List[string]]::new()
    if ($OptionalIntegration) {
        $nativeFilters.Add('^(?:'+(($selected | ForEach-Object { [regex]::Escape($_.Name) }) -join '|')+')$')
    } else {
        $nativeFilters.Add($filter)
        if ($TestExclusion) { $nativeFilters.Add('!'+$TestExclusion) }
        foreach ($row in $rows) {
            if ($row.Scope -cne $selectedScope) { $nativeFilters.Add('!^'+[regex]::Escape($row.Name)+'$') }
        }
    }
    $cliFilter=$nativeFilters -join ';'
    if ($cliFilter.Length -gt 28000) { throw 'Use a narrower fixture selection instead of exceeding native command-line limits.' }
    $prefix=Join-Path $root ($Receiver+'-tests-'+$TestMode+'-'+$Run)
    foreach ($suffix in @('.selection.json','.xml','.log','.verdict.json')) { if (Test-Path -LiteralPath ($prefix+$suffix)) { throw 'Preserve each prior official Test Runner result.' } }
    $assemblyNames=(@($selected.Assembly | Sort-Object -Unique) -join ';')
    Json ($prefix+'.selection.json') @{ Project=$project; UnityVersion=$ExpectedUnityVersion; TestMode=$TestMode; Filter=$filter; Scope=$selectedScope; OptionalIntegration=$OptionalIntegration; IsolatedFixtureExclusion=$TestExclusion; NativeFilter=$cliFilter; RequestedCount=$ExpectedTestCount; Expected=$selected; DiscoverySha256=Hash $discoveryPath; Profile=$(if ($profilePath) { @{ Path=$profilePath; Sha256=Hash $profilePath } } else { $null }); PackageSha256=$packageSha }
    $arguments=@('-batchmode','-projectPath',$project,'-buildTarget','Win64','-runTests','-testPlatform',$TestMode,
        '-testFilter',$cliFilter,'-assemblyNames',$assemblyNames,'-testResults',($prefix+'.xml'),'-logFile',($prefix+'.log'),
        '-s9-run',$Run,'-s9-test-discovery',$discoveryPath,'-s9-test-discovery-sha',(Hash $discoveryPath))
    if ($CostEvidenceRoot -or $CostScope -or $CostPurpose) {
        if (-not $CostEvidenceRoot -or -not $CostScope -or -not $CostPurpose -or $TestMode -cne 'EditMode' -or -not $ProfileRun) {
            throw 'Cost evidence requires an explicit root/scope/purpose and an owned EditMode profile.'
        }
        if (-not [IO.Path]::IsPathFullyQualified($CostEvidenceRoot)) { throw 'Cost evidence requires an absolute owned root.' }
        Reject-Link $CostEvidenceRoot
        $arguments+=@('-s9-cost-root',$CostEvidenceRoot,'-s9-cost-scope',$CostScope,'-s9-cost-purpose',$CostPurpose,
            '-s9-cost-run',$Run,'-s9-test-selection',($prefix+'.selection.json'))
    }
    if ($ProfileRun -and $profile.ContainsKey('BootstrapScene') -and $TestMode -ceq 'EditMode') {
        $scene=Join-Path $project $profile.BootstrapScene
        Reject-Link $scene
        if ($profile.BootstrapScene -cne ('Assets/Stage9TestsBootstrap-'+$ProfileRun+'.unity') -or
            (Hash $scene) -cne $profile.BootstrapSha256) { throw 'Owned test bootstrap no longer matches its profile.' }
        $arguments+=@('-s9-test-profile',$profilePath)
    }
    $runFailure=$null
    try { Run-Unity $arguments ($prefix+'.log') } catch { $runFailure=$_ }
    if (-not [IO.File]::Exists($prefix+'.xml')) {
        if ($runFailure) { throw $runFailure }
        throw 'The official runner did not produce its XML result.'
    }
    $xml=[xml](Read-SharedText ($prefix+'.xml'))
    $cases=@($xml.SelectNodes('//test-case'))
    $actual=@($cases | ForEach-Object { $_.GetAttribute('fullname') })
    $difference=@(Compare-Object -CaseSensitive -ReferenceObject @($selected.Name | Sort-Object -CaseSensitive) -DifferenceObject @($actual | Sort-Object -CaseSensitive))
    $nonPassed=@($cases | Where-Object { $_.GetAttribute('result') -cne 'Passed' })
    $costPassed=$true
    if ($CostEvidenceRoot) {
        $costFinished=Join-Path (Join-Path $CostEvidenceRoot $Run) 'finished.json'
        $costPassed=Test-Path -LiteralPath $costFinished
        if ($costPassed) {
            $evidence=Get-Content -LiteralPath $costFinished -Raw | ConvertFrom-Json
            $costDifference=@(Compare-Object -CaseSensitive -ReferenceObject @($selected.Name | Sort-Object -CaseSensitive) -DifferenceObject @($evidence.Leaves.Name | Sort-Object -CaseSensitive))
            $costPassed=$evidence.PassedGate -and $evidence.EnvironmentRestored -and $costDifference.Count -eq 0 -and
                @($evidence.Leaves | Where-Object { $_.Status -cne 'Passed' }).Count -eq 0
        }
    }
    $validationPath=Join-Path $root ($Receiver+'-tests-input-validation-'+$Run+'.json')
    $inputPassed=[IO.File]::Exists($validationPath)
    if ($inputPassed) {
        $validation=Get-Content -LiteralPath $validationPath -Raw | ConvertFrom-Json
        $inputPassed=$validation.Passed -and $validation.OwnerSha256 -ceq $discovery.OwnerSha256 -and $validation.PackageSha256 -ceq $packageSha -and
            $validation.DiscoverySha256 -ceq (Hash $discoveryPath) -and $validation.CompilationSha256 -ceq $discovery.CompilationSha256
    }
    $passed=$null -eq $runFailure -and $inputPassed -and $costPassed -and $difference.Count -eq 0 -and $nonPassed.Count -eq 0 -and $cases.Count -eq $selected.Count -and $cases.Count -gt 0 -and $xml.DocumentElement.GetAttribute('result') -ceq 'Passed'
    Json ($prefix+'.verdict.json') @{ Passed=$passed; Expected=$selected.Count; Actual=$cases.Count; Difference=$difference; CostEvidencePassed=$costPassed; CostEvidence=$(if ($CostEvidenceRoot) { @{ Root=$CostEvidenceRoot; Scope=$CostScope; Purpose=$CostPurpose; FinishedSha256=$(if (Test-Path -LiteralPath $costFinished) { Hash $costFinished } else { $null }) } } else { $null }); ProcessPassed=($null -eq $runFailure); ProcessFailure=$(if ($runFailure) { $runFailure.Exception.Message } else { $null }); NonPassed=@($nonPassed | ForEach-Object { $message=$_.SelectSingleNode('./failure/message'); @{ Name=$_.GetAttribute('fullname'); Result=$_.GetAttribute('result'); Message=$(if ($message) { $message.InnerText } else { $null }) } }); XmlSha256=Hash ($prefix+'.xml'); DiscoverySha256=Hash $discoveryPath }
    if ($runFailure) { throw $runFailure }
    if (-not $passed) { throw 'Official NUnit result or discovered execution set did not pass; preserve the XML.' }
    'Official Test Runner passed every selected discovered case without skips.'
    return
}
if ($Operation -eq 'Editor') {
    $log=Join-Path $root ($Receiver + '-' + $Method + '-' + $Run + '.log')
    if ($nativeInputEntry) {
        foreach ($name in @('Stage9NativeInputProbe','Stage9NativeInputEditor')) {
            $target=Join-Path $project ('Assets/Stage9NativeInput/'+$(if ($name.EndsWith('Editor')) { 'Editor/' } else { '' })+$name+'.cs')
            Reject-Link $target
            if (-not [IO.File]::Exists($target)) { throw 'Install exact native-input templates before invoking their Editor entry.' }
            if ((Hash $target) -cne (Hash (Join-Path $PSScriptRoot ($name+'.cs.txt')))) { throw 'Installed native-input templates differ from the selected source; preserve their generation identity.' }
        }
    }
    $entry=if ($nativeInputEntry) { 'Stage9NativeInputEditor.' } elseif ($Method.StartsWith('BuildSceneLoop',[StringComparison]::Ordinal)) { 'Stage9SceneLoopEditor.' } elseif ($Method.StartsWith('Tests',[StringComparison]::Ordinal)) { 'Stage9ReceptionTests.' } else { 'Stage9DeliveryEditor.' }
    $entryMethod=if ($nativeInputEntry) { $Method.Substring('NativeInput'.Length) } else { $Method }
    $arguments=@('-batchmode','-nographics','-projectPath',$project,'-buildTarget','Win64','-executeMethod',($entry + $entryMethod),'-s9-run',$Run,'-logFile',$log)
    if ($nativeInputBuild) { $arguments+=@('-s9-native-mode',$NativeInputMode) }
    if ($OptionalIntegration) { $arguments+=@('-s9-optional-integration',$OptionalIntegration) }
    if ($AnalyzerPolicyRun) { $arguments+=@('-s9-analyzer-policy',$AnalyzerPolicyRun) }
    if ($Method -ceq 'TestsDiscover') {
        $smoke='YLFramework.Core.Editor.Tests.CoreSerializedPropertyTests.AcceptanceAuthoringFieldsKeepExactFormerNames'
        $result=Join-Path $root ($Receiver+'-tests-discovery-'+$Run+'.smoke.xml')
        if (Test-Path -LiteralPath $result) { throw 'Preserve the discovery companion smoke result.' }
        $arguments=@('-batchmode','-projectPath',$project,'-buildTarget','Win64','-executeMethod',($entry+$Method),'-s9-run',$Run,
            '-runTests','-testPlatform','EditMode','-assemblyNames','YLFramework.Core.Editor.Tests','-testFilter',('^'+[regex]::Escape($smoke)+'$'),
            '-testResults',$result,'-logFile',$log)
        if ($OptionalIntegration) { $arguments+=@('-s9-optional-integration',$OptionalIntegration) }
    } else { $arguments+=@('-quit') }
    Run-Unity $arguments $log
    if ($Method -ceq 'TestsDiscover') {
        $discovery=Get-Content -LiteralPath (Join-Path $root ($Receiver+'-tests-discovery-'+$Run+'.json')) -Raw | ConvertFrom-Json
        $xml=[xml](Read-SharedText $result)
        $cases=@($xml.SelectNodes('//test-case'))
        if (-not $discovery.Passed -or $discovery.Project -cne $project -or $discovery.Unity -cne $ExpectedUnityVersion -or
            $cases.Count -ne 1 -or $cases[0].GetAttribute('fullname') -cne $smoke -or $cases[0].GetAttribute('result') -cne 'Passed' -or
            $xml.DocumentElement.GetAttribute('result') -cne 'Passed') { throw 'Discovery and its official exit companion must both pass.' }
    }
    "Completed explicit receiver entry: $Method / $Run"
    return
}
if ($Operation -in @('Player','RecoverPlayer')) {
    $build=Join-Path $project ('P/' + $(if ($Backend -eq 'Mono') { 'm-' } else { 'i-' }) + $BuildRun)
    $report=Get-Content -LiteralPath (Join-Path $build 'BuildReport.json') -Raw | ConvertFrom-Json
    $null=Receiver-PackageHash
    Assert-Stage9BuildBinding $report $project $PSScriptRoot
    if ($report.Result -cne 'Succeeded' -or $report.Errors -ne 0 -or $report.Development) { throw 'A successful non-Development build is required.' }
    foreach ($file in $report.PlayerFiles) {
        if ((Hash (Join-Path $build $file.Path)) -cne $file.Sha256) { throw "Player artifact differs from the real build: $($file.Path)" }
    }
    $runRoot=Join-Path $root ('Runs/' + $Receiver + '-' + $Backend + '-' + $Run)
    Reject-Link $build
    Reject-Link $runRoot
    Reject-Link (Join-Path $root ('Stores/' + $Store))
    $buildReportSha=Hash (Join-Path $build 'BuildReport.json')
    if ($Operation -eq 'Player') {
        if (Test-Path -LiteralPath $runRoot) { throw 'Use RecoverPlayer to collect this same run without restarting it.' }
        $null=[IO.Directory]::CreateDirectory($runRoot)
        $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $build 'Stage9Delivery.exe')); $start.UseShellExecute=$false; $start.WorkingDirectory=$build
        foreach ($arg in @('-screen-fullscreen','0','-screen-width','1280','-screen-height','720','-logFile',(Join-Path $runRoot 'Player.log'),
            '--s9-out',(Join-Path $runRoot 'result.json'),'--s9-command',$Command,'--s9-store',(Join-Path $root ('Stores/' + $Store)),'--s9-expect',$ExpectedVersion.ToString())) { $start.ArgumentList.Add($arg) }
        $process=[Diagnostics.Process]::Start($start)
        Json (Join-Path $runRoot 'process.json') @{ Pid=$process.Id; Executable=$start.FileName; Project=$project; Arguments=@($start.ArgumentList); Command=$Command;
            Store=$Store; ExpectedVersion=$ExpectedVersion; BuildReportSha256=$buildReportSha; StartedUtc=[DateTime]::UtcNow.ToString('O') }
        try {
            $completed=$process.WaitForExit(240000)
            if (-not $completed) { $process.Kill($true); $process.WaitForExit() }
            Json (Join-Path $runRoot 'exit.json') @{ Pid=$process.Id; ExitCode=$process.ExitCode; TimedOut=-not $completed; EndedUtc=[DateTime]::UtcNow.ToString('O') }
        }
        finally { $process.Dispose() }
    }
    $invocation=Get-Content -LiteralPath (Join-Path $runRoot 'process.json') -Raw | ConvertFrom-Json
    $exit=Get-Content -LiteralPath (Join-Path $runRoot 'exit.json') -Raw | ConvertFrom-Json
    if ($invocation.Project -cne $project -or $invocation.Executable -cne (Join-Path $build 'Stage9Delivery.exe') -or $invocation.Pid -ne $exit.Pid -or
        $invocation.BuildReportSha256 -cne $buildReportSha -or $invocation.Command -cne $Command -or $invocation.Store -cne $Store -or
        $invocation.ExpectedVersion -ne $ExpectedVersion -or (Get-Process -Id $exit.Pid -ErrorAction SilentlyContinue)) { throw 'Collect only the same completed Player/build identity.' }
    $code=$exit.ExitCode
    $result=Get-Content -LiteralPath (Join-Path $runRoot 'result.json') -Raw | ConvertFrom-Json
    $log=Read-SharedText (Join-Path $runRoot 'Player.log')
    $latePath=Join-Path $runRoot 'result.json.late-errors.log'
    $lateErrors=if (Test-Path -LiteralPath $latePath) { Read-SharedText $latePath } else { '' }
    $exitErrors=@([regex]::Matches($log,'(?im)^.*(?:\b[A-Za-z][A-Za-z0-9_.]*Exception\s*:|Assertion failed|Fatal Error|Crash!!!|UnityEngine\.Debug:LogError|UnityEngine\.Debug:LogException).*$') | ForEach-Object Value)
    $expectedBackend=if ($Backend -eq 'Mono') { 'Mono2x' } else { 'IL2CPP' }
    $identity=$result.Pid -eq $exit.Pid -and $result.Unity -ceq $ExpectedUnityVersion -and $result.Backend -ceq $expectedBackend -and -not $result.Development -and
        $result.BuildGuid -ceq $report.BuildGuid -and $result.Graphics -cne 'Null' -and $result.Platform -ceq 'WindowsPlayer' -and
        $result.LegacyInput -and -not $result.NewInput -and $result.Command -ceq $Command -and $result.Store -ceq (Join-Path $root ('Stores/' + $Store))
    $sceneLoopPassed=$Command -ne 'scene-loop' -or ($result.PSObject.Properties['SceneLoop'] -and
        $result.SceneLoop.CompletedRounds -eq 20 -and $result.SceneLoop.UnloadCount -eq 20 -and $result.SceneLoop.RemainingSceneRecords -eq 0)
    $urpPassed=$Command -ne 'urp'
    if ($Command -eq 'urp' -and $result.PSObject.Properties['UrpVisual']) {
        $visual=$result.UrpVisual
        $names=@($visual.Samples | ForEach-Object Name)
        $urpPassed=$visual.Passed -is [bool] -and $visual.Passed -and $visual.NormalEndOfFrame -is [bool] -and $visual.NormalEndOfFrame -and
            $visual.CameraRenderCalled -is [bool] -and -not $visual.CameraRenderCalled -and $visual.InputInjected -is [bool] -and -not $visual.InputInjected -and
            $visual.Asset -ceq 'UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset' -and
            $visual.Pipeline -ceq 'UnityEngine.Rendering.Universal.UniversalRenderPipeline' -and
            $names.Count -eq 4 -and (@(Compare-Object $names @('resource-atlas','formal-color-ring','formal-screen-fade-in','formal-screen-fade-out')).Count -eq 0)
        foreach ($sample in $visual.Samples) {
            if ($sample.Name -cnotin @('resource-atlas','formal-color-ring','formal-screen-fade-in','formal-screen-fade-out') -or
                $sample.Png -cne ($sample.Name+'.png')) { $urpPassed=$false; continue }
            $imagePath=Join-Path $runRoot $sample.Png
            Reject-Link $imagePath
            $numeric=$sample.MaximumChannelError -is [double] -or $sample.MaximumChannelError -is [int] -or $sample.MaximumChannelError -is [long]
            if ($sample.Passed -isnot [bool] -or -not $sample.Passed -or -not $numeric -or
                [double]::IsNaN($sample.MaximumChannelError) -or [double]::IsInfinity($sample.MaximumChannelError) -or
                $sample.MaximumChannelError -lt 0 -or $sample.MaximumChannelError -gt .08 -or
                (Hash $imagePath) -cne $sample.Sha256) { $urpPassed=$false }
        }
    }
    $passed=$code -eq 0 -and $result.BusinessPassed -and $identity -and $sceneLoopPassed -and $urpPassed -and $exitErrors.Count -eq 0 -and $lateErrors.Length -eq 0 -and $log.Contains('STAGE9_PLAYER_PASS')
    $verdict=@{ Passed=$passed; BusinessPassed=$result.BusinessPassed; ProcessExitCode=$code; IdentityMatched=$identity;
        Pid=$exit.Pid; BuildGuid=$result.BuildGuid; Backend=$result.Backend; Development=$result.Development; ExitErrors=$exitErrors; LateErrors=$lateErrors; UrpVisualPassed=$(if ($Command -eq 'urp') { $urpPassed } else { $null });
        ResultSha256=Hash (Join-Path $runRoot 'result.json'); PlayerLogSha256=Hash (Join-Path $runRoot 'Player.log'); BuildReportSha256=$buildReportSha }
    $verdictPath=Join-Path $runRoot 'verdict.json'
    if (Test-Path -LiteralPath $verdictPath) {
        $existing=Get-Content -LiteralPath $verdictPath -Raw | ConvertFrom-Json
        if ($existing.Passed -ne $passed -or $existing.ResultSha256 -cne $verdict.ResultSha256 -or $existing.PlayerLogSha256 -cne $verdict.PlayerLogSha256 -or
            $existing.BuildReportSha256 -cne $buildReportSha -or $existing.Pid -ne $exit.Pid) { throw 'Previously collected evidence changed; never overwrite it.' }
    } else { Json $verdictPath $verdict }
    if (-not $passed) { throw 'Independent Player process/identity/late-exit gate failed; preserve this same run.' }
    "Independent graphical Player passed: $Backend / $Command / exit $code (same-run collection supported)"
}
