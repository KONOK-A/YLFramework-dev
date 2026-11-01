#requires -Version 7.6
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $EvidenceRoot,
    [string] $CarrierRoot,
    [string] $DeliveryRoot,
    [string] $Unity,
    [ValidateSet('2022.3.62f3c1','6000.3.27f1')][string] $ExpectedUnityVersion = '2022.3.62f3c1',
    [ValidateSet('1.0.0','2.0.0')][string] $UguiVersion = '1.0.0',
    [ValidateRange(60,7200)][int] $CreateProjectTimeoutSeconds = 1800,
    [switch] $CreateProject,
    [switch] $Sync
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../..'))
$root = [IO.Path]::GetFullPath($EvidenceRoot)
$carrier = if ($Sync) { (Get-Content -LiteralPath (Join-Path $root 'carrier-location.json') -Raw | ConvertFrom-Json).Carrier } elseif ($CarrierRoot) { [IO.Path]::GetFullPath($CarrierRoot) } else { Join-Path $root 'Carrier' }
$approvedTemp = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'kilo'))
if (-not $root.StartsWith($approvedTemp + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Use a private child of the approved kilo temporary root.' }
function Reject-Link([string] $Path) {
    for ($cursor = $Path; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if ([IO.Path]::Exists($cursor) -and ([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint)) { throw "Linked path rejected: $cursor" }
    }
}
function Hash([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Json([string] $Path, $Value) { [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 40), [Text.UTF8Encoding]::new($false)) }
function Get-UnityRevision([string] $Version) {
    if ($Version -ceq '6000.3.27f1') { return '672d562a42a7' }
    if ($Version -ceq '2022.3.62f3c1') { return '1623fc0bbb97' }
    throw "Unsupported Unity version: $Version"
}
function Assert-UnityExecutable([string] $Path, [string] $Version) {
    if (-not $Path) { throw 'Explicit Unity executable is required.' }
    $full = [IO.Path]::GetFullPath($Path)
    if (-not [IO.File]::Exists($full)) { throw "Unity executable missing: $full" }
    Reject-Link $full
    $expected = $Version + '_' + (Get-UnityRevision $Version)
    $actual = [Diagnostics.FileVersionInfo]::GetVersionInfo($full).ProductVersion
    if ($actual -cne $expected) { throw "Unity executable version mismatch: expected $expected; found $actual" }
    $full
}
function Get-UnityProjectPath([string] $CommandLine) {
    $match = [regex]::Match($CommandLine, '(?:^|\s)-(?:projectPath|createProject)\s+(?:"([^"]+)"|(\S+))')
    if (-not $match.Success) { return $null }
    if ($match.Groups[1].Success) { return [IO.Path]::GetFullPath($match.Groups[1].Value) }
    [IO.Path]::GetFullPath($match.Groups[2].Value)
}
function Assert-NoOwnedUnity([string] $Carrier) {
    $full = [IO.Path]::GetFullPath($Carrier)
    $parent = [IO.Path]::GetDirectoryName($full)
    foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name = 'Unity.exe'")) {
        $commandLine = [string]$process.CommandLine
        if ($commandLine -like '*AssetImportWorker*') { continue }
        $project = Get-UnityProjectPath $commandLine
        if (-not $project) { continue }
        # 只把带本批 Editor 参数或位于同一交付父目录的 Editor 视为 owned；交互式源工程不在此停止范围。
        $owned = $commandLine.Contains('-r810-', [StringComparison]::Ordinal) -or
            $commandLine.Contains('-s9-', [StringComparison]::Ordinal) -or
            $commandLine.Contains('-createProject', [StringComparison]::Ordinal) -or
            $project.StartsWith($parent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
        if (-not $owned) { continue }
        if ($project -ieq $full) { throw 'This carrier already has a Unity instance; recover that same run.' }
        throw "Another owned Unity project is running: $project. Serialize owned Editor work."
    }
}
function Invoke-OwnedUnityCreate([string] $Executable, [string] $Project, [string] $Log, [int] $TimeoutSeconds) {
    if (Test-Path -LiteralPath $Log) { throw 'Never overwrite a prior createProject log.' }
    $processPath = Join-Path $root 'create.process.json'
    $exitPath = Join-Path $root 'create.exit.json'
    if ((Test-Path -LiteralPath $processPath) -or (Test-Path -LiteralPath $exitPath)) { throw 'Never overwrite a prior createProject receipt.' }
    Assert-NoOwnedUnity $Project
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    foreach ($argument in @('-batchmode','-quit','-createProject',$Project,'-logFile',$Log)) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $timedOut = $false
    try {
        [IO.File]::WriteAllText($processPath, (@{ Pid=$process.Id; Executable=$Executable; Arguments=$start.ArgumentList; Project=$Project; StartedUtc=[DateTime]::UtcNow.ToString('O') } | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $timedOut = $true
            $process.Kill($true)
            $process.WaitForExit()
        }
        $exitCode = $process.ExitCode
        [IO.File]::WriteAllText($exitPath, (@{ Pid=$process.Id; ExitCode=$exitCode; TimedOut=$timedOut; EndedUtc=[DateTime]::UtcNow.ToString('O') } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        if ($timedOut) { throw "createProject timed out after $TimeoutSeconds seconds; preserve $Log" }
        if ($exitCode -ne 0) { throw "createProject failed with code $exitCode; preserve $Log" }
    } finally {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}
$requiredUgui = if ($ExpectedUnityVersion -ceq '6000.3.27f1') { '2.0.0' } else { '1.0.0' }
if ($UguiVersion -cne $requiredUgui) { throw 'UGUI version does not match the selected Editor.' }
if (-not $Sync -and $ExpectedUnityVersion -cne '2022.3.62f3c1' -and -not $CreateProject) { throw 'A non-2022 carrier must be created by the actual createProject command.' }
if ($CreateProject -and $Sync) { throw 'CreateProject and Sync are mutually exclusive.' }
$unityPath = if ($CreateProject) { Assert-UnityExecutable $Unity $ExpectedUnityVersion } else { $null }
Reject-Link $root
Reject-Link $carrier
$ownedDeliveryChild = $false
if ($DeliveryRoot) {
    $delivery = [IO.Path]::GetFullPath($DeliveryRoot)
    Reject-Link $delivery
    $deliveryOwner = Get-Content -LiteralPath (Join-Path $delivery 'owner.json') -Raw | ConvertFrom-Json
    if ($deliveryOwner.purpose -cne 'YLFramework-Stage9-independent-delivery' -or $deliveryOwner.root -cne $delivery -or $deliveryOwner.repository -cne $repo) { throw 'Explicit owned delivery identity required for a short carrier.' }
    $ownedDeliveryChild = $carrier.StartsWith($delivery + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}
if (-not $carrier.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
    -not $carrier.StartsWith((Join-Path $repo 'Temp') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
    -not $ownedDeliveryChild) { throw 'Carrier must be this evidence child, a new repository Temp child, or an explicit owned delivery child.' }
if (-not (Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($root)) -PathType Container)) { throw 'Evidence parent must already exist.' }
if (-not (Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($carrier)) -PathType Container) -and $CarrierRoot) { throw 'Explicit carrier parent must already exist.' }
$predecessorIdentitySha = $null
if ($Sync) {
    $owner = Get-Content -LiteralPath (Join-Path $carrier 'r810-owner.json') -Raw | ConvertFrom-Json
    if ($owner.Carrier -cne $carrier -or $owner.Repository -cne $repo -or $owner.Purpose -cne 'Resource-8.10-owned-carrier' -or $owner.Evidence -cne $root) { throw 'Carrier owner mismatch.' }
    $ownerUnity = if ($owner.PSObject.Properties['UnityVersion']) { [string]$owner.UnityVersion } else { '2022.3.62f3c1' }
    $ownerUgui = if ($owner.PSObject.Properties['UguiVersion']) { [string]$owner.UguiVersion } else { '1.0.0' }
    if ($ownerUnity -cne $ExpectedUnityVersion -or $ownerUgui -cne $UguiVersion) { throw 'Sync cannot switch the carrier Unity/UGUI identity.' }
    $projectVersion = Get-Content -LiteralPath (Join-Path $carrier 'ProjectSettings/ProjectVersion.txt') -Raw
    if ($projectVersion -cnotmatch ('(?m)^m_EditorVersion: ' + [regex]::Escape($ownerUnity) + '\r?$')) { throw 'Carrier ProjectVersion differs from its owner identity.' }
    $manifest = Get-Content -LiteralPath (Join-Path $carrier 'Packages/manifest.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($manifest.dependencies['com.unity.ugui'] -cne $ownerUgui) { throw 'Carrier UGUI version differs from its owner identity.' }
    $predecessorIdentitySha = Hash (Join-Path $carrier 'source-identity.json')
    foreach ($row in (Get-Content -LiteralPath (Join-Path $carrier 'source-identity.json') -Raw | ConvertFrom-Json)) {
        if ((Hash (Join-Path $carrier $row.Path)) -cne $row.Sha256) { throw "Carrier source changed outside this synchronization: $($row.Path)" }
    }
} else {
    if ((Test-Path -LiteralPath $root) -or (Test-Path -LiteralPath $carrier)) { throw 'Never adopt or overwrite existing evidence/carrier.' }
    $null = [IO.Directory]::CreateDirectory($root)
    $null = [IO.Directory]::CreateDirectory((Join-Path $root 'Runs'))
    if ($CreateProject) {
        Invoke-OwnedUnityCreate $unityPath $carrier (Join-Path $root 'create.log') $CreateProjectTimeoutSeconds
    } else {
        $null = [IO.Directory]::CreateDirectory($carrier)
    }
    Json (Join-Path $carrier 'r810-owner.json') ([ordered]@{ Purpose='Resource-8.10-owned-carrier'; Repository=$repo; Carrier=$carrier; Evidence=$root; UnityVersion=$ExpectedUnityVersion; UguiVersion=$UguiVersion; CreatedUtc=[DateTime]::UtcNow.ToString('O') })
    Json (Join-Path $root 'carrier-location.json') @{ Carrier=$carrier }
}
$formalRoots = @('Runtime','Editor','Plugins','Prefabs','Samples','Documentation')
$sourcePaths = [Collections.Generic.List[string]]::new()
foreach ($leaf in $formalRoots) {
    $relative = 'Assets/YLFramework/' + $leaf
    $sourcePaths.Add($relative + '.meta')
    foreach ($item in Get-ChildItem -LiteralPath (Join-Path $repo $relative) -Recurse -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked formal source rejected.' }
        if (-not $item.PSIsContainer) { $sourcePaths.Add([IO.Path]::GetRelativePath($repo, $item.FullName).Replace('\','/')) }
    }
}
foreach ($path in @('Assets/YLFramework.meta','Assets/YLFramework/LICENSE.md','Assets/YLFramework/LICENSE.md.meta')) { $sourcePaths.Add($path) }
$identity = [Collections.Generic.List[object]]::new()
foreach ($relative in ($sourcePaths | Sort-Object -CaseSensitive)) {
    $source = Join-Path $repo $relative
    $before = Hash $source
    $destination = Join-Path $carrier $relative
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
    [IO.File]::Copy($source, $destination, $Sync.IsPresent)
    if ((Hash $destination) -cne $before -or (Hash $source) -cne $before) { throw 'Concurrent formal source edit; retain incomplete evidence and do not build.' }
    $identity.Add([ordered]@{ Path=$relative; Length=([IO.FileInfo]$source).Length; Sha256=$before })
}
foreach ($template in @('ResourcePlayerAcceptance','ResourcePlayerAcceptanceBuild')) {
    $relative = if ($template.EndsWith('Build')) { "Assets/R810/Editor/$template.cs" } else { "Assets/R810/$template.cs" }
    $destination = Join-Path $carrier $relative
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
    $source = Join-Path $PSScriptRoot "$template.cs.txt"
    [IO.File]::Copy($source, $destination, $Sync.IsPresent)
    $identity.Add([ordered]@{ Path=$relative; Length=([IO.FileInfo]$source).Length; Sha256=(Hash $source) })
}
$bc = $identity | Where-Object Path -CEQ 'Assets/YLFramework/Plugins/BouncyCastle/BouncyCastle.Cryptography.dll'
if ($bc.Sha256 -cne 'd61c1f2ba929a230a58e101ccd850e21f2675fa6b9814ec279633e8a089c3495') { throw 'Approved BC binary identity mismatch.' }
Json (Join-Path $carrier 'source-identity.json') $identity.ToArray()
if (-not $Sync) {
    $revision = Get-UnityRevision $ExpectedUnityVersion
    $projectVersionPath = Join-Path $carrier 'ProjectSettings/ProjectVersion.txt'
    if ($CreateProject) {
        $projectVersion = Get-Content -LiteralPath $projectVersionPath -Raw
        if ($projectVersion -cnotmatch ('(?m)^m_EditorVersion: ' + [regex]::Escape($ExpectedUnityVersion) + '\r?$') -or
            $projectVersion -cnotmatch ('(?m)^m_EditorVersionWithRevision: ' + [regex]::Escape($ExpectedUnityVersion) + ' \(' + [regex]::Escape($revision) + '\)\r?$')) { throw 'createProject produced a different Editor version.' }
        # createProject 决定官方模块集；只保留 modules，UGUI/Json.NET 由本验收固定。
        $manifestPath = Join-Path $carrier 'Packages/manifest.json'
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
        foreach ($key in @($manifest.dependencies.Keys)) { if (-not $key.StartsWith('com.unity.modules.')) { $manifest.dependencies.Remove($key) } }
        $manifest.dependencies['com.unity.ugui'] = $UguiVersion
        $manifest.dependencies['com.unity.nuget.newtonsoft-json'] = '3.2.1'
        [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    } else {
        $null = [IO.Directory]::CreateDirectory((Join-Path $carrier 'Packages'))
        $null = [IO.Directory]::CreateDirectory((Join-Path $carrier 'ProjectSettings'))
        [IO.File]::Copy((Join-Path $repo 'ProjectSettings/ProjectVersion.txt'), $projectVersionPath)
        $projectVersion = Get-Content -LiteralPath $projectVersionPath -Raw
        if ($projectVersion -cnotmatch ('(?m)^m_EditorVersion: ' + [regex]::Escape($ExpectedUnityVersion) + '\r?$')) { throw 'Source ProjectVersion differs from the requested 2022 carrier.' }
        $dependencies = [ordered]@{ 'com.unity.ugui'=$UguiVersion; 'com.unity.nuget.newtonsoft-json'='3.2.1' }
        foreach ($module in @('ai','animation','assetbundle','audio','cloth','director','imageconversion','imgui','jsonserialize','particlesystem','physics','physics2d','screencapture','subsystems','terrain','terrainphysics','tilemap','ui','uielements','umbra','unityanalytics','unitywebrequest','unitywebrequestassetbundle','unitywebrequestaudio','unitywebrequesttexture','unitywebrequestwww','vehicles','video','vr','wind','xr')) { $dependencies['com.unity.modules.'+$module]='1.0.0' }
        Json (Join-Path $carrier 'Packages/manifest.json') @{ dependencies=$dependencies }
    }
    $rsa = [Security.Cryptography.RSA]::Create(2048)
    try {
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=localhost', $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $san = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
        $san.AddDnsName('localhost'); $san.AddIpAddress([Net.IPAddress]::Loopback)
        $request.CertificateExtensions.Add($san.Build())
        $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddDays(3))
        try {
            [IO.File]::WriteAllBytes((Join-Path $root 'loopback.pfx'), $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx,'r810-loopback-only'))
            $public = $rsa.ExportParameters($false)
            Json (Join-Path $root 'public-config.json') @{ Endpoint='https://localhost:18443/'; Certificate=[Convert]::ToBase64String($certificate.RawData); SigningKey=@{ Modulus=[Convert]::ToBase64String($public.Modulus); Exponent=[Convert]::ToBase64String($public.Exponent) } }
        } finally { $certificate.Dispose() }
    } finally { $rsa.Dispose() }
    $protected = @('Assets/Games','Assets/Scenes','Assets/StreamingAssets','Packages','ProjectSettings','.gitignore','.kilo/kilo.jsonc','AGENTS.md','.kilo/plans/1791960935353-ui-panel-redesign.md','Assets/YLFramework/Tests/Acceptance/Core','Assets/YLFramework/Tests/Fixtures/WorkbenchUX1-20261017')
    $rows = @(foreach ($relative in $protected) {
        $path = Join-Path $repo $relative
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $files = if ([IO.File]::Exists($path)) { @($path) } else { @(Get-ChildItem -LiteralPath $path -File -Recurse -Force | ForEach-Object FullName) }
        foreach ($file in $files) { [ordered]@{ Path=[IO.Path]::GetRelativePath($repo,$file); Sha256=(Hash $file) } }
    })
    Json (Join-Path $root 'protected-before.json') $rows
}
$sourceSnapshot = Join-Path $root ('source-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff') + '.json')
if (Test-Path -LiteralPath $sourceSnapshot) { throw 'Never overwrite a prior source snapshot.' }
Json $sourceSnapshot $identity.ToArray()
if ($Sync) {
    $syncReceipt = Join-Path $root ('sync-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff') + '.json')
    if (Test-Path -LiteralPath $syncReceipt) { throw 'Never overwrite a prior synchronization receipt.' }
    Json $syncReceipt ([ordered]@{
        Operation='Sync'; Evidence=$root; Carrier=$carrier; UnityVersion=$ExpectedUnityVersion; UguiVersion=$UguiVersion
        PredecessorSourceIdentitySha256=$predecessorIdentitySha
        SourceIdentitySha256=(Hash (Join-Path $carrier 'source-identity.json'))
        Files=$identity.Count; SyncedUtc=[DateTime]::UtcNow.ToString('O')
    })
}
[pscustomobject]@{ Carrier=$carrier; Evidence=$root; UnityVersion=$ExpectedUnityVersion; UguiVersion=$UguiVersion; FormalFiles=$sourcePaths.Count; Sync=$Sync.IsPresent; CreateProject=$CreateProject.IsPresent }
