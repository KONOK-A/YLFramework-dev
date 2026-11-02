#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $DeliveryRoot,
    [Parameter(Mandatory)][string] $Receiver,
    [Parameter(Mandatory)][string] $Run,
    [Parameter(Mandatory)][string] $OwnerSha256
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

function Assert-B05NoLink([string] $Path) {
    for ($cursor = [IO.Path]::GetFullPath($Path); $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if ([IO.Path]::Exists($cursor) -and ([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Linked path rejected: $cursor"
        }
    }
}
function Get-B05Sha([string] $Path) {
    Assert-B05NoLink $Path
    if (-not [IO.File]::Exists($Path)) { throw "Required source/proof missing: $Path" }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
    finally { $stream.Dispose() }
}
function Read-B05Json([string] $Path) {
    Assert-B05NoLink $Path
    if (-not [IO.File]::Exists($Path)) { throw "Required proof missing: $Path" }
    [IO.File]::ReadAllText($Path) | ConvertFrom-Json -AsHashtable
}
function Get-B05Files([string] $Directory, [switch] $IncludeDirectories) {
    Assert-B05NoLink $Directory
    foreach ($child in [IO.Directory]::GetFileSystemEntries($Directory)) {
        Assert-B05NoLink $child
        if ([IO.Directory]::Exists($child)) {
            if ($IncludeDirectories) { $child }
            Get-B05Files $child -IncludeDirectories:$IncludeDirectories
        }
        else { $child }
    }
}
function New-B05Plan([hashtable] $Policy, [string] $Receiver, [string] $Run, [string] $OwnerSha256) {
    if ($Receiver -cnotmatch '^(Receiver[A-Z][0-9]*|[a-z][0-9]?)$' -or $Run -cnotmatch '^[A-Za-z0-9-]{1,32}$') {
        throw 'Safe explicit Receiver and Run required.'
    }
    $delivery = [IO.Path]::GetFullPath($Policy.Delivery).TrimEnd('\')
    $repository = [IO.Path]::GetFullPath($Policy.Repository).TrimEnd('\')
    $project = Join-Path $delivery $Receiver
    $destination = Join-Path $project 'Assets/B05AudioAcceptance'
    foreach ($path in @($delivery, $repository, $project, $destination, ($destination + '.meta'))) { Assert-B05NoLink $path }
    if ($project.Equals($repository, [StringComparison]::OrdinalIgnoreCase) -or
        $project.StartsWith($repository + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $delivery.StartsWith($repository + '\', [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::Exists((Join-Path $project '.git'))) { throw 'Source/repository/worktree is not a receiver.' }
    if (-not [IO.Directory]::Exists((Join-Path $project 'Assets')) -or
        -not [IO.Directory]::Exists((Join-Path $project 'ProjectSettings'))) { throw 'Unknown receiver layout.' }
    if ([IO.Path]::Exists((Join-Path $project 'Temp/UnityLockfile'))) { throw 'Receiver occupied: UnityLockfile exists.' }
    if ([IO.Path]::Exists($destination) -or [IO.Path]::Exists($destination + '.meta')) {
        throw 'Refuse existing/occupied B05 fixture or metadata.'
    }
    $deliveryOwnerPath = Join-Path $delivery 'owner.json'
    $deliveryOwner = Read-B05Json $deliveryOwnerPath
    if ($deliveryOwner.purpose -cne 'YLFramework-Stage9-independent-delivery' -or
        $deliveryOwner.root -cne $delivery -or $deliveryOwner.repository -cne $repository) { throw 'Wrong delivery owner.' }
    $ownerPath = Join-Path $project 'stage9-owner.json'
    if ($OwnerSha256 -cnotmatch '^[0-9a-f]{64}$' -or (Get-B05Sha $ownerPath) -cne $OwnerSha256) { throw 'Receiver owner SHA mismatch.' }
    $owner = Read-B05Json $ownerPath
    if ($owner.Purpose -cne 'Stage9-independent-receiver' -or $owner.Project -cne $project -or
        $owner.Delivery -cne $delivery -or $owner.UnityVersion -cne '6000.3.27f1' -or $owner.UguiVersion -cne '2.0.0' -or
        $owner.Package -cne $Policy.Package -or $owner.PackageSha256 -cne $Policy.PackageSha256) { throw 'Wrong receiver owner/package/version.' }
    $versionPath = Join-Path $project 'ProjectSettings/ProjectVersion.txt'
    Assert-B05NoLink $versionPath
    if ([IO.File]::ReadAllText($versionPath) -cnotmatch '(?m)^m_EditorVersion: 6000\.3\.27f1\r?$') { throw 'Wrong ProjectVersion.' }
    $manifestPath = Join-Path $project 'Packages/manifest.json'
    $manifest = Read-B05Json $manifestPath
    if ($manifest.dependencies['com.unity.ugui'] -cne '2.0.0' -or
        $manifest.dependencies['com.unity.nuget.newtonsoft-json'] -cne '3.2.1') { throw 'Exact bare product dependencies required.' }
    foreach ($dependency in $manifest.dependencies.GetEnumerator()) {
        if ($dependency.Key -cnotin @('com.unity.ugui','com.unity.nuget.newtonsoft-json') -and
            ($dependency.Key -cnotlike 'com.unity.modules.*' -or $dependency.Value -cne '1.0.0')) {
            throw 'Unknown dependency/package; no UTF/Input/SDK installation or macros required.'
        }
    }
    $packagePath = Join-Path $delivery $Policy.Package
    if ((Get-B05Sha $packagePath) -cne $Policy.PackageSha256) { throw 'Wrong candidate12 package bytes.' }
    $auditPath = $packagePath + '.receipt.json'
    $filesPath = $packagePath + '.files.json'
    $audit = Read-B05Json $auditPath
    if ($audit.Passed -isnot [bool] -or -not $audit.Passed -or $audit.Package -cne $Policy.Package -or
        $audit.Sha256 -cne $Policy.PackageSha256) { throw 'Wrong package audit proof.' }
    $firstPath = Join-Path $delivery ($Receiver + '-first-import-initial.json')
    $first = Read-B05Json $firstPath
    if ($first.Passed -isnot [bool] -or -not $first.Passed -or $first.HarnessPresent -isnot [bool] -or $first.HarnessPresent -or
        $first.Project -cne $project -or $first.UnityVersion -cne '6000.3.27f1' -or $first.UguiVersion -cne '2.0.0' -or
        $first.Package -cne $Policy.Package -or $first.PackageSha256 -cne $Policy.PackageSha256 -or
        @($first.Errors).Count -ne 0 -or $first.Log -cne (Join-Path $delivery ($Receiver + '-import-initial.log'))) {
        throw 'Exact candidate12 bare first-import-initial proof required.'
    }
    $proofs = [Collections.Generic.List[object]]::new()
    foreach ($path in @($deliveryOwnerPath, $ownerPath, $versionPath, $manifestPath, $packagePath, $auditPath, $filesPath, $firstPath)) {
        $proofs.Add(@{ Path=$path; Sha256=(Get-B05Sha $path) })
    }
    $previousExit = [DateTimeOffset]::MinValue
    foreach ($stage in @('create','import-initial')) {
        $log = Join-Path $delivery ($Receiver + '-' + $stage + '.log')
        $process = Read-B05Json ($log + '.process.json')
        $exit = Read-B05Json ($log + '.exit.json')
        if ($exit.ExitCode -isnot [long] -and $exit.ExitCode -isnot [int]) { throw 'Invalid normal exit fact.' }
        if ($exit.ExitCode -ne 0 -or $exit.Project -cne $project -or $exit.Pid -ne $process.Pid -or $process.Pid -le 0 -or
            $exit.TimedOut -isnot [bool] -or $exit.TimedOut -or $exit.ForcedExit -isnot [bool] -or $exit.ForcedExit -or
            $exit.ExitStalled -isnot [bool] -or $exit.ExitStalled -or
            [DateTimeOffset]::Parse($exit.EndedUtc) -lt [DateTimeOffset]::Parse($process.StartedUtc)) { throw 'Normal create/import exit required.' }
        if ([DateTimeOffset]::Parse($process.StartedUtc) -lt $previousExit) { throw 'Actual Create then Import order required.' }
        $previousExit = [DateTimeOffset]::Parse($exit.EndedUtc)
        if ($process.Project -cne $project -or $process.Executable -cne $Policy.UnityExe -or
            $process.ExpectedUnityVersion -cne '6000.3.27f1' -or $process.UguiVersion -cne '2.0.0' -or
            $process.Package -cne $Policy.Package -or $process.PackageSha256 -cne $Policy.PackageSha256) {
            throw 'Wrong original process/executable/package proof.'
        }
        $expected = if ($stage -ceq 'create') { @('-batchmode','-quit','-createProject',$project,'-logFile',$log) }
        else { @('-batchmode','-quit','-projectPath',$project,'-buildTarget','Win64','-importPackage',$packagePath,'-logFile',$log) }
        if (@($process.Arguments).Count -ne $expected.Count) { throw 'Exact bare Create/Import arguments required.' }
        for ($i=0; $i -lt $expected.Count; $i++) {
            if ($process.Arguments[$i] -cne $expected[$i]) { throw 'Exact bare Create/Import arguments required.' }
        }
        $null = Get-B05Sha $log
        if ([IO.File]::ReadAllText($log) -cnotmatch 'Initialize engine version: 6000\.3\.27f1 \(672d562a42a7\)' -or
            [IO.File]::ReadAllText($log) -match 'error CS\d+|Scripts have compiler errors|Aborting batchmode due to failure') { throw 'Invalid original engine/import log.' }
        foreach ($path in @($log, ($log + '.process.json'), ($log + '.exit.json'))) { $proofs.Add(@{ Path=$path; Sha256=(Get-B05Sha $path) }) }
    }
    $productFiles = @(Read-B05Json $filesPath)
    $allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $parents = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in $productFiles) {
        for ($parent=$file.Path; $parent.Contains('/');) {
            $parent=$parent.Substring(0, $parent.LastIndexOf('/'))
            $null=$parents.Add($parent)
        }
    }
    foreach ($file in $productFiles) {
        if ($file.Path -cnotmatch '^Assets/YLFramework(?:/[^<>:"|?*\\]+)?$' -or
            $file.Path -match '(^|/)\.\.?(/|$)' -or $file.Path -match '/Tests(?:/|$)') { throw 'Unknown product manifest path.' }
        $path = Join-Path $project $file.Path
        if (-not $allowed.Add($file.Path + '.meta')) { throw 'Duplicate product manifest path.' }
        if (-not $file.Folder) {
            $null = $allowed.Add($file.Path)
            if ((Get-B05Sha $path) -cne $file.Sha256) { throw 'Imported product bytes changed.' }
        }
        # Unity may omit a genuinely empty exported directory; never create it here.
        if ([IO.Path]::Exists($path + '.meta') -and (Get-B05Sha ($path + '.meta')) -cne $file.MetaSha256) {
            throw 'Imported product metadata changed.'
        }
        if ((-not $file.Folder -or $parents.Contains($file.Path) -or [IO.Path]::Exists($path)) -and -not [IO.File]::Exists($path + '.meta')) {
            throw 'Imported product metadata missing.'
        }
    }
    foreach ($path in @(Get-B05Files (Join-Path $project 'Assets') -IncludeDirectories)) {
        $relative = [IO.Path]::GetRelativePath($project, $path).Replace('\','/')
        if ([IO.Directory]::Exists($path) -and ($allowed.Contains($relative + '.meta') -or $parents.Contains($relative))) { continue }
        if (-not $allowed.Contains($relative) -and $relative.EndsWith('.meta', [StringComparison]::Ordinal)) {
            $folder = $relative.Substring(0, $relative.Length - 5)
            if ($parents.Contains($folder) -and [IO.Directory]::Exists((Join-Path $project $folder))) {
                $metadata = [IO.File]::ReadAllText($path)
                if ($metadata -cnotmatch '(?m)^guid: [0-9a-f]{32}\r?$' -or
                    $metadata -cnotmatch '(?m)^folderAsset: yes\r?$') { throw 'Invalid imported parent folder metadata.' }
                continue
            }
        }
        if (-not $allowed.Contains($relative)) { throw "Unknown receiver asset (not fresh bare import): $relative" }
    }
    $mapping = [ordered]@{
        'Tests/Acceptance/Core/AudioAcceptanceEntry.cs'='Runtime/AudioAcceptanceEntry.cs'
        'Tests/Acceptance/Core/AudioAcceptanceEntry.cs.meta'='Runtime/AudioAcceptanceEntry.cs.meta'
        'Tests/Fixtures/Audio/AudioMixerFixture.mixer'='AudioMixerFixture.mixer'
        'Tests/Fixtures/Audio/AudioMixerFixture.mixer.meta'='AudioMixerFixture.mixer.meta'
        'Tests/Tools/B05AudioAcceptanceBuild.cs.txt'='Editor/B05AudioAcceptanceBuild.cs'
    }
    $inputs = @(foreach ($pair in $mapping.GetEnumerator()) {
        $source = Join-Path $repository ('Assets/YLFramework/' + $pair.Key)
        @{ Source=$source; Target=$pair.Value; Sha256=(Get-B05Sha $source) }
    })
    foreach ($identity in @(
        @{ Target='Runtime/AudioAcceptanceEntry.cs.meta'; Guid='5906146ceb2dbb845b7afea8caaa5daa' },
        @{ Target='AudioMixerFixture.mixer.meta'; Guid='eaa22683e6def6c4cb53ff9d9baf5e18' }
    )) {
        $source = @($inputs | Where-Object Target -CEQ $identity.Target)[0].Source
        if ([IO.File]::ReadAllText($source) -cnotmatch ('(?m)^guid: ' + $identity.Guid + '\r?$')) { throw 'Original fixture meta/GUID required.' }
        foreach ($path in @(Get-B05Files (Join-Path $project 'Assets')) | Where-Object { $_.EndsWith('.meta') }) {
            if ([IO.File]::ReadAllText($path) -match $identity.Guid) { throw 'Original fixture GUID already imported; receiver is not fresh.' }
        }
    }
    return @{ Policy=$Policy; Receiver=$Receiver; Run=$Run; OwnerSha256=$OwnerSha256; Project=$project
        Destination=$destination; Inputs=$inputs; Proofs=@($proofs); PackageFilesSha256=(Get-B05Sha $filesPath) }
}
function Write-B05New([string] $Path, [string] $Text) {
    Assert-B05NoLink $Path
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    } finally { $stream.Dispose() }
}
function Install-B05Fixture([hashtable] $Policy, [string] $Receiver, [string] $Run, [string] $OwnerSha256) {
    $plan = New-B05Plan @PSBoundParameters
    $staging = Join-Path $plan.Project ('.b05-' + $Run + '-' + [Guid]::NewGuid().ToString('N'))
    Assert-B05NoLink $staging
    if ([IO.Path]::Exists($staging)) { throw 'Never adopt installation staging.' }
    $null = [IO.Directory]::CreateDirectory((Join-Path $staging 'Runtime'))
    $null = [IO.Directory]::CreateDirectory((Join-Path $staging 'Editor'))
    foreach ($input in $plan.Inputs) {
        if ((Get-B05Sha $input.Source) -cne $input.Sha256) { throw 'Source changed after preflight; preserve incomplete staging.' }
        $target = Join-Path $staging $input.Target
        [IO.File]::Copy($input.Source, $target, $false)
        if ((Get-B05Sha $target) -cne $input.Sha256) { throw 'Source copy differs; preserve incomplete staging.' }
    }
    $asmdef = [ordered]@{
        name='YLFramework.B05Audio.Fixture'; rootNamespace='YLFramework.Core.Tests'; references=@('YLFramework.Runtime')
        includePlatforms=@(); excludePlatforms=@(); allowUnsafeCode=$false; overrideReferences=$true
        precompiledReferences=@(); autoReferenced=$true; defineConstraints=@(); versionDefines=@(); noEngineReferences=$false
    }
    Write-B05New (Join-Path $staging 'Runtime/YLFramework.B05Audio.Fixture.asmdef') ($asmdef | ConvertTo-Json -Depth 5)
    $receipt = [ordered]@{
        Schema='B05AudioInstall-v1'; Delivery=$Policy.Delivery; Project=$plan.Project; Repository=$Policy.Repository
        Receiver=$Receiver; Run=$Run; Unity='6000.3.27f1'; Executable=$Policy.UnityExe
        Package=$Policy.Package; PackageSha256=$Policy.PackageSha256; OwnerSha256=$OwnerSha256
        Inputs=$plan.Inputs; Proofs=$plan.Proofs; PackageFilesSha256=$plan.PackageFilesSha256
        FixtureAssembly='YLFramework.B05Audio.Fixture'; AsmdefSha256=(Get-B05Sha (Join-Path $staging 'Runtime/YLFramework.B05Audio.Fixture.asmdef'))
        RuntimePassed=$false; InputInjected=$false; SourceProjectTested=$false
    }
    Write-B05New (Join-Path $staging 'InstallReceipt.json.tmp') ($receipt | ConvertTo-Json -Depth 20)
    [IO.File]::Move((Join-Path $staging 'InstallReceipt.json.tmp'), (Join-Path $staging 'InstallReceipt.json'))
    # Recheck facts and occupancy immediately before the atomic, non-overwriting directory publication.
    $confirmed = New-B05Plan @PSBoundParameters
    foreach ($input in $confirmed.Inputs) {
        $prior = @($plan.Inputs | Where-Object Target -CEQ $input.Target)[0]
        if ($prior.Sha256 -cne $input.Sha256) { throw 'Source changed during installation; preserve staging, never publish stale inputs.' }
    }
    foreach ($proof in $confirmed.Proofs) {
        $prior = @($plan.Proofs | Where-Object Path -CEQ $proof.Path)[0]
        if ($prior.Sha256 -cne $proof.Sha256) { throw 'Ownership/import facts changed during installation; preserve staging.' }
    }
    [IO.Directory]::Move($staging, $plan.Destination)
    return [pscustomobject]@{ Installed=$true; Destination=$plan.Destination; RuntimePassed=$false
        InputInjected=$false; SourceProjectTested=$false; OwnerSha256=$OwnerSha256 }
}

$pinnedDelivery = 'G:\Unity\U63-20261027-c63a1'
if (-not $IsWindows -or -not [IO.Path]::IsPathFullyQualified($DeliveryRoot) -or
    [IO.Path]::GetFullPath($DeliveryRoot).TrimEnd('\') -cne $pinnedDelivery) { throw 'Use the exact delivery; pinned delivery cannot be redirected.' }
$policy = @{
    Delivery=$pinnedDelivery; Repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../..'))
    Package='YLFramework-U63-candidate12.unitypackage'
    PackageSha256='2365eeb5047c482da3b947f1ad982463dfef732429e7a8ad79169255351e38d0'
    UnityExe='G:\Unity Editor\6000.3.27f1\Editor\Unity.exe'
}
Install-B05Fixture -Policy $policy -Receiver $Receiver -Run $Run -OwnerSha256 $OwnerSha256
