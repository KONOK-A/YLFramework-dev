#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param()
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
. (Join-Path $PSScriptRoot '../../Packaging/Build-YLRegistryPackages.ps1')
$packages = @(Get-YLRegistryPackageSet -Root (Join-Path $repository 'PackageSources'))
$ids = @($packages.id)
$guids = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($id in $ids) {
    $root = Join-Path $repository "PackageSources/$id"
    $manifestPath = Join-Path $root 'package.json'
    if (-not [IO.File]::Exists($manifestPath)) { throw "UPM package is missing: $id" }
    $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json -AsHashtable
    if ($manifest.name -cne $id -or $manifest.version -cne @($packages | Where-Object id -CEQ $id)[0].version -or $manifest.unity -cne '2022.3' -or $manifest.unityRelease -cne '62f3') { throw "Unexpected package identity: $id" }
    foreach ($required in @('displayName', 'description', 'license', 'author')) {
        if (-not $manifest.ContainsKey($required)) { throw "Missing $required in $id" }
    }
    foreach ($name in @('README.md', 'LICENSE.md')) {
        if (-not [IO.File]::Exists((Join-Path $root $name))) { throw "Missing $name in $id" }
    }
    $attributes = Join-Path $root '.gitattributes'
    if (-not [IO.File]::Exists($attributes) -or [IO.File]::ReadAllText($attributes) -cnotmatch '(?m)^\*\.dll filter=lfs diff=lfs merge=lfs -text\s*$') {
        throw "UPM subdirectory checkout must carry its own binary LFS rules: $id"
    }
    foreach ($name in @('Assets', 'Packages', 'ProjectSettings', 'Tools', 'Tests', 'Library', 'Temp', '.kilo', '.kilocode', 'Integrations')) {
        if ([IO.Directory]::Exists((Join-Path $root $name))) { throw "Development input in package: $id/$name" }
    }
    foreach ($dependency in $manifest.dependencies.GetEnumerator()) {
        if ($dependency.Value -cnotmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw "Non-SemVer package dependency: $id -> $($dependency.Key)" }
        if ($id -ceq 'com.kano.ylframework' -and $dependency.Key -notmatch '^com\.unity\.') { throw "Optional dependency leaked into main package: $($dependency.Key)" }
    }
    foreach ($file in [IO.Directory]::EnumerateFiles($root, '*', [IO.SearchOption]::AllDirectories)) {
        $relative = [IO.Path]::GetRelativePath($root, $file).Replace('\', '/')
        if ($relative -match '(?i)(?:^|/)(?:AGENTS\.md|\.mcp\.json|\.nuget-installed\.json|DOTween\.dll|McpPlugin[^/]*\.dll|HybridCLR\.(?:Runtime|Editor)\.dll)$' -or $relative -match '\.(?:csproj|sln|unitypackage)$') { throw "Forbidden package input: $id/$relative" }
        if ($file.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase)) {
            $bytes = [IO.File]::ReadAllBytes($file)
            if ($bytes.Length -lt 2 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) { throw "DLL is not materialized (possibly LFS pointer): $id/$relative" }
        }
        if ($file.EndsWith('.meta', [StringComparison]::Ordinal)) {
            $text = [IO.File]::ReadAllText($file)
            $match = [regex]::Match($text, '(?m)^guid: ([0-9a-f]{32})\s*$')
            if (-not $match.Success) { throw "Invalid asset identity: $id/$relative" }
            $guid = $match.Groups[1].Value
            if ($guids.ContainsKey($guid)) { throw "Duplicate GUID: $id/$relative and $($guids[$guid])" }
            $guids.Add($guid, "$id/$relative")
        }
    }
}
$main = Join-Path $repository 'PackageSources/com.kano.ylframework'
foreach ($asset in @('Runtime/Core/Resources/YLFramework/Core/Canvas.prefab', 'Runtime/Core/Resources/YLFramework/Core/EventSystem.prefab', 'Runtime/Systems/Action/Resources/ActionKit/ScreenTransitionCanvas.prefab', 'Prefabs/YLFramework.prefab')) {
    if (-not [IO.File]::Exists((Join-Path $main $asset)) -or -not [IO.File]::Exists((Join-Path $main ($asset + '.meta')))) { throw "Required runtime/template asset missing: $asset" }
}
foreach ($package in (Read-YLPackageCatalog).packages) { foreach ($sample in $package.samples) {
    $root = Join-Path $repository "PackageSources/$($package.id)"
    $manifest = [IO.File]::ReadAllText((Join-Path $root 'package.json')) | ConvertFrom-Json
    if (@($manifest.samples | Where-Object path -CEQ $sample).Count -ne 1 -or -not [IO.Directory]::Exists((Join-Path $root $sample)) -or [IO.Directory]::Exists((Join-Path $root 'Samples'))) { throw "Optional sample layout is invalid: $($package.id)" }
} }
"PASS: $($ids.Count) catalog UPM packages, dependencies, clean content, materialized DLLs and $($guids.Count) unique asset GUIDs."
