[CmdletBinding()]
param([Parameter(Mandatory = $true)][string] $Run, [string] $BCommit)
$ErrorActionPreference = 'Stop'
$owner = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ($Run -notmatch '^[A-Za-z0-9_-]+$') { throw 'Unsafe Run.' }
if (-not (Test-Path -LiteralPath (Join-Path $owner 'Logs\YLFramework.Domain'))) { throw 'Owner evidence root is missing.' }
$target = Join-Path $owner "Logs\YLFramework.Domain\Carriers\$Run"
if (Test-Path -LiteralPath $target) { throw 'Refusing to overwrite an existing carrier.' }
$null = New-Item -ItemType Directory -Path $target
$files = @('Assets/YLFramework.meta', 'Assets/YLFramework/LICENSE.md', 'Assets/YLFramework/LICENSE.md.meta', 'Assets/YLFramework/Runtime.meta', 'Assets/YLFramework/Editor.meta', 'Assets/YLFramework/Plugins.meta', 'Assets/YLFramework/Plugins/BouncyCastle.meta', 'Assets/YLFramework/Tests.meta', 'Assets/YLFramework/Tests/EditMode.meta', 'Assets/YLFramework/Tests/PlayMode.meta', 'Assets/YLFramework/Tests/EditMode/YLFramework.Tests.EditMode.asmdef', 'Assets/YLFramework/Tests/EditMode/YLFramework.Tests.EditMode.asmdef.meta', 'Assets/YLFramework/Tests/PlayMode/YLFramework.Tests.PlayMode.asmdef', 'Assets/YLFramework/Tests/PlayMode/YLFramework.Tests.PlayMode.asmdef.meta')
foreach ($dir in @('Assets/YLFramework/Runtime', 'Assets/YLFramework/Editor', 'Assets/YLFramework/Plugins/BouncyCastle')) {
    $files += @(Get-ChildItem -LiteralPath (Join-Path $owner $dir) -Recurse -File | ForEach-Object { $_.FullName.Substring($owner.Length + 1).Replace('\', '/') })
}
# Keep the original Domain test set without importing unrelated tests from shared directories.
$domainTests = @(
    'EditMode/Systems/Buff/BuffLifecycleTests.cs',
    'EditMode/Editor/Domain/DomainAuthoringTests.cs',
    'EditMode/Editor/Domain/DomainBatchAcceptance.cs',
    'EditMode/Editor/Domain/DomainMainAcceptanceTests.cs',
    'EditMode/Editor/Domain/DomainReviewAssetTests.cs',
    'EditMode/Editor/Domain/DomainTwoPlayTests.cs',
    'EditMode/Systems/Logging/LoggerSessionTests.cs',
    'EditMode/Systems/Mission/MissionConditionTests.cs',
    'EditMode/Systems/Mission/MissionParameterCopyTests.cs',
    'EditMode/Systems/Reddot/ReddotTests.cs',
    'EditMode/Systems/Skill/SkillBatchReentryTests.cs',
    'EditMode/Systems/Skill/SkillLifecycleTests.cs',
    'PlayMode/Systems/Buff/BuffOwnerTests.cs',
    'PlayMode/Systems/Domain/DomainHostSceneTests.cs',
    'PlayMode/Systems/Domain/DomainResourceLeaseTests.cs',
    'PlayMode/Systems/Domain/DomainReviewExitTests.cs',
    'PlayMode/Systems/Logging/LoggerLifecycleTests.cs',
    'PlayMode/Systems/Mission/MissionOwnerTests.cs',
    'PlayMode/Systems/Reddot/ReddotLifecycleTests.cs',
    'PlayMode/Systems/Skill/SkillOwnerTests.cs'
)
foreach ($test in $domainTests) {
    $relative = 'Assets/YLFramework/Tests/' + $test
    $files += @($relative, ($relative + '.meta'))
    $parent = [IO.Path]::GetDirectoryName($relative).Replace('\', '/')
    while ($parent -ne 'Assets/YLFramework/Tests') {
        $files += $parent + '.meta'
        $parent = [IO.Path]::GetDirectoryName($parent).Replace('\', '/')
    }
}
$records = @()
foreach ($relative in $files | Sort-Object -Unique) {
    $source = Join-Path $owner $relative
    if (-not (Test-Path -LiteralPath $source)) { throw "Missing imported product input: $relative" }
    $before = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $destination = Join-Path $target $relative
    $null = New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force
    Copy-Item -LiteralPath $source -Destination $destination
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $before -or (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $before) { throw "Unstable carrier copy: $relative" }
    $records += [ordered]@{ path = $relative; sha256 = $before }
}
$bouncy = Join-Path $target 'Assets/YLFramework/Plugins/BouncyCastle/BouncyCastle.Cryptography.dll'
if ((Get-FileHash -LiteralPath $bouncy -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'd61c1f2ba929a230a58e101ccd850e21f2675fa6b9814ec279633e8a089c3495') { throw 'Unverified approved BouncyCastle.' }
$null = New-Item -ItemType Directory -Path (Join-Path $target 'ProjectSettings'), (Join-Path $target 'Packages'), (Join-Path $target 'Assets/DomainFixture/Editor') -Force
Copy-Item -LiteralPath (Join-Path $owner 'ProjectSettings/ProjectVersion.txt') -Destination (Join-Path $target 'ProjectSettings/ProjectVersion.txt')
$dependencies = [ordered]@{ 'com.unity.ugui' = '1.0.0'; 'com.unity.nuget.newtonsoft-json' = '3.2.1'; 'com.unity.test-framework' = '1.3.9' }
foreach ($module in @('ai','animation','assetbundle','audio','cloth','director','imageconversion','imgui','jsonserialize','particlesystem','physics','physics2d','screencapture','subsystems','terrain','terrainphysics','tilemap','ui','uielements','umbra','unityanalytics','unitywebrequest','unitywebrequestassetbundle','unitywebrequestaudio','unitywebrequesttexture','unitywebrequestwww','vehicles','video','vr','wind','xr')) { $dependencies["com.unity.modules.$module"] = '1.0.0' }
[IO.File]::WriteAllText((Join-Path $target 'Packages/manifest.json'), (@{ dependencies = $dependencies } | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
$fixture = Join-Path $owner 'Assets/YLFramework/Tests/Fixtures/Extensions/Domain'
Copy-Item -LiteralPath (Join-Path $fixture 'DomainPlayerProbe.cs.txt') -Destination (Join-Path $target 'Assets/DomainFixture/DomainPlayerProbe.cs')
Copy-Item -LiteralPath (Join-Path $fixture 'DomainFixtureAuthoring.cs.txt') -Destination (Join-Path $target 'Assets/DomainFixture/Editor/DomainFixtureAuthoring.cs')
Copy-Item -LiteralPath (Join-Path $fixture 'link.xml.txt') -Destination (Join-Path $target 'Assets/DomainFixture/link.xml')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'DomainCarrierBootstrap.cs.txt') -Destination (Join-Path $target 'Assets/DomainFixture/Editor/DomainCarrierBootstrap.cs')
[IO.File]::WriteAllText((Join-Path $target 'domain-carrier.json'), (@{ owner = $owner; sourceHead = (& git -C $owner rev-parse HEAD); createdUtc = [DateTime]::UtcNow.ToString('o'); sourceFiles = $records; noSourceLibraryCopied = $true; declaredPackages = @($dependencies.Keys) } | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
if ($BCommit) {
    if ($BCommit -notmatch '^[0-9a-f]{7,40}$') { throw 'Unsafe B commit.' }
    $exact = & git -C $owner rev-parse $BCommit
    if ($LASTEXITCODE -ne 0 -or $exact -ne '6a9e2508a42333e55c82e6f29eea136643af4467') { throw 'This adapter validates only the pinned completed B candidate.' }
    $snapshot = Join-Path $owner "Logs\YLFramework.Domain\Snapshots\$Run"
    if (Test-Path -LiteralPath $snapshot) { throw 'Refusing to overwrite a B input snapshot.' }
    $null = New-Item -ItemType Directory -Path $snapshot -Force
    & git -C $owner archive --format=zip "--output=$snapshot\B.zip" $exact -- Assets/YLFramework/Runtime/Systems/DataAssets Assets/YLFramework/Editor/Systems/DataAssets Assets/YLFramework/Integrations/Excel
    if ($LASTEXITCODE -ne 0) { throw 'Pinned B archive failed.' }
    Expand-Archive -LiteralPath (Join-Path $snapshot 'B.zip') -DestinationPath $snapshot
    foreach ($relative in @('Assets/YLFramework/Runtime/Systems/DataAssets', 'Assets/YLFramework/Editor/Systems/DataAssets', 'Assets/YLFramework/Integrations/Excel')) {
        $destination = Join-Path $target $relative
        $null = New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force
        Copy-Item -LiteralPath (Join-Path $snapshot $relative) -Destination $destination -Recurse
    }
    $null = New-Item -ItemType Directory -Path (Join-Path $target 'Assets/YLFramework/Integrations') -Force
    Copy-Item -LiteralPath (Join-Path $owner 'Assets/YLFramework/Integrations/DomainExcel') -Destination (Join-Path $target 'Assets/YLFramework/Integrations/DomainExcel') -Recurse
    $mini = Join-Path $target 'Assets/YLFramework/Integrations/Excel/Editor/Plugins/MiniExcel.dll'
    if ((Get-FileHash -LiteralPath $mini -Algorithm SHA256).Hash.ToLowerInvariant() -ne '0a49b66224bef9fdcec12800e4660267a993b1ece87727d9fe15c7dabf58ba1e') { throw 'Pinned MiniExcel mismatch.' }
    [IO.File]::WriteAllText((Join-Path $target 'B-input-commit.txt'), $exact + "`n", [Text.UTF8Encoding]::new($false))
}
"Prepared independent carrier=$target sourceFiles=$($records.Count). Only declared product/test sources and metadata copied; no host game, SDK, MCP, PackageCache or Library copied."
