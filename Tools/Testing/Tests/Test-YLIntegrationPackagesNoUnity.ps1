#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param([string] $TemporaryRoot = [IO.Path]::GetTempPath())
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$entry = Join-Path $PSScriptRoot 'Test-YLIntegrationPackages.ps1'
if (-not [IO.File]::Exists($entry)) { throw 'Integration prerequisite verifier has not been implemented.' }
. $entry -WorkspaceRoot $TemporaryRoot -Unity 'C:\unused\Unity.exe' -Integration Excel
$root = Join-Path (Get-YLTestPath $TemporaryRoot) ('yl-integration-contracts-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($root)
$count = 0
$passed = $false
function Reject([scriptblock] $Action, [string] $Reason) {
    try { & $Action | Out-Null } catch {
        if (-not $_.Exception.Message.Contains($Reason, [StringComparison]::OrdinalIgnoreCase)) { throw }
        $script:count++
        return
    }
    throw "Expected refusal containing: $Reason"
}
function Json([string] $Relative, [object] $Value) {
    $path = Join-Path $root $Relative
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllText($path, (ConvertTo-Json -InputObject $Value -Depth 15), [Text.UTF8Encoding]::new($false))
}
try {
    foreach ($name in @('DOTween', 'Excel', 'ExcelGameplay', 'HybridCLR')) {
        $definition = Get-YLIntegrationDefinition $name
        if ($definition.id -cne @{
            DOTween = 'com.kano.ylframework'; Excel = 'com.kano.ylframework.excel'
            ExcelGameplay = 'com.kano.ylframework.excel'; HybridCLR = 'com.kano.ylframework.hybridclr'
        }[$name] -or $definition.assemblies.Count -lt 3) { throw 'The selected integration lost its mandatory compilation closure.' }
        $roots = @($definition.id); if ($name -ceq 'ExcelGameplay') { $roots += 'com.kano.ylframework.gameplay' }
        if (($definition.roots -join ',') -cne ($roots -join ',')) { throw 'The integration roots differ from the explicit profile.' }
        if ($name -cin @('Excel', 'ExcelGameplay')) {
            $ids = @('com.kano.ylframework', 'com.kano.ylframework.data-assets', 'com.kano.ylframework.excel')
            if ($name -ceq 'ExcelGameplay') { $ids += 'com.kano.ylframework.gameplay' }
            if ((@($definition.assemblies.package | Sort-Object -Unique) -join ',') -cne ($ids -join ',') -or
                (@($definition.assemblies | Where-Object name -CEQ 'YLFramework.Domain.Excel.Editor').Count -eq 1) -ne ($name -ceq 'ExcelGameplay')) {
                throw 'Excel must retain its exact dependency closure and enable the Gameplay adapter only with both roots.'
            }
        }
        $count++
    }
    $definition = Get-YLIntegrationDefinition Package -PackageId 'com.kano.ylframework.excel'
    if (($definition.roots -join ',') -cne 'com.kano.ylframework.excel' -or
        (@($definition.assemblies.package | Sort-Object -Unique) -join ',') -cne 'com.kano.ylframework,com.kano.ylframework.data-assets,com.kano.ylframework.excel' -or
        @($definition.assemblies | Where-Object name -CEQ 'YLFramework.Domain.Excel.Editor').Count -ne 0) { throw 'PackageId Excel must remain ordinary Excel without Gameplay.' }
    $count++
    Reject { Get-YLIntegrationDefinition DomainExcel } 'DomainExcel'
    $sdkUrl = 'https://gitee.com/focus-creative-games/hybridclr_unity.git#v9.0.3'
    $sdkHash = '6cadd24d101f4cbd00cd80190106f6621132d24d'
    $sdkId = 'com.code-philosophy.hybridclr'
    $manifest = @{ dependencies = @{ $sdkId = $sdkUrl } }
    $locked = @{ version = $sdkUrl; source = 'git'; hash = $sdkHash }
    $lock = @{ dependencies = @{ $sdkId = $locked } }
    $approval = @{ package = @{ name = $sdkId; version = '9.0.3'; url = $sdkUrl; commit = $sdkHash; bundled = $false } }
    Json 'Packages/manifest.json' $manifest
    Json 'Packages/packages-lock.json' $lock
    Json 'PackageSources/com.kano.ylframework.hybridclr/Dependencies.json' $approval
    $sdk = Get-YLIntegrationHybridSdk $root
    if ($sdk.declaration -cne $sdkUrl -or $sdk.revision -cne $sdkHash) { throw 'The SDK identity did not come from the real manifest and lock.' }
    $count++
    $locked.version = $sdkUrl.Replace('9.0.3', '9.0.2')
    Json 'Packages/packages-lock.json' $lock
    Reject { Get-YLIntegrationHybridSdk $root } 'PrerequisiteNotMet'
    $locked.version = $sdkUrl; $locked.hash = $true
    Json 'Packages/packages-lock.json' $lock
    Reject { Get-YLIntegrationHybridSdk $root } 'PrerequisiteNotMet'
    $locked.hash = 'a' * 40
    Json 'Packages/packages-lock.json' $lock
    Reject { Get-YLIntegrationHybridSdk $root } 'PrerequisiteNotMet'
    $locked.hash = $sdkHash
    $manifest.dependencies[$sdkId] = 'https://invalid.example/hybridclr.git#v9.0.3'
    $locked.version = $manifest.dependencies[$sdkId]
    Json 'Packages/manifest.json' $manifest
    Json 'Packages/packages-lock.json' $lock
    Reject { Get-YLIntegrationHybridSdk $root } 'PrerequisiteNotMet'
    Reject { Get-YLIntegrationDotweenSdk -Repository $root -Archive (Join-Path $root 'missing.zip') } 'PrerequisiteNotMet'

    $package = Join-Path $root 'PackageSources/com.kano.ylframework'
    $null = [IO.Directory]::CreateDirectory($package)
    Json 'PackageSources/com.kano.ylframework/package.json' @{ name = 'com.kano.ylframework'; version = '0.1.0-preview.1'; dependencies = @{} }
    [IO.File]::WriteAllText((Join-Path $package 'LICENSE.md'), 'fixture license')
    $null = [IO.Directory]::CreateDirectory((Join-Path $package 'Editor/Tools'))
    [IO.File]::WriteAllText((Join-Path $package 'Editor/Tools/Authoring.cs'), '// legitimate package authoring tool')
    $paths = @(Get-YLIntegrationPackageFiles -Repository $root -Packages @(Get-YLUpmPackages -Repository $root))
    if ('PackageSources/com.kano.ylframework/Editor/Tools/Authoring.cs' -cnotin $paths) { throw 'Legitimate package Editor tools must not be confused with development-host Tools.' }
    $count++
    $before = Get-YLTestInputIdentity -Repository $root -Paths $paths -Profile Guard
    [IO.File]::WriteAllText((Join-Path $package 'added.cs'), '// newly added source')
    $after = Get-YLTestInputIdentity -Repository $root -Paths @(Get-YLIntegrationPackageFiles -Repository $root -Packages @(Get-YLUpmPackages -Repository $root)) -Profile Guard
    if ($before.sha256 -ceq $after.sha256) { throw 'New source files must invalidate the input identity.' }
    $count++
    $null = [IO.Directory]::CreateDirectory((Join-Path $package 'Tests'))
    [IO.File]::WriteAllText((Join-Path $package 'Tests/hidden.cs'), '// must never ship')
    Reject { Get-YLIntegrationPackageFiles -Repository $root -Packages @(Get-YLUpmPackages -Repository $root) } 'Unauthorized'
    [IO.Directory]::Delete((Join-Path $package 'Tests'), $true)
    [IO.File]::WriteAllText((Join-Path $package 'unapproved.dll'), 'version https://git-lfs.github.com/spec/v1')
    Reject { Get-YLIntegrationPackageFiles -Repository $root -Packages @(Get-YLUpmPackages -Repository $root) } 'LFS'
    "PASS: $count NoUnity integration prerequisite and source drift contracts. No Unity, Player or native verification."
    foreach ($test in @('Test-YLIntegrationPackagesGitNoUnity.ps1', 'Test-YLIntegrationPackagesEntryNoUnity.ps1')) {
        & (Join-Path $PSHOME 'pwsh.exe') -NoLogo -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot $test) -TemporaryRoot $TemporaryRoot
        if ($LASTEXITCODE -ne 0) { throw "Integration contract child failed: $test ($LASTEXITCODE)." }
    }
    $passed = $true
} finally {
    if ($passed -and [IO.Directory]::Exists($root)) { $null = @(Get-YLTestTreeFiles $root); [IO.Directory]::Delete($root, $true) }
    elseif ([IO.Directory]::Exists($root)) { "Artifacts retained: $root" }
}
