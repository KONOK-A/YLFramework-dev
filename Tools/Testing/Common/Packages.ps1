#requires -Version 7.6
#requires -PSEdition Core
. (Join-Path $PSScriptRoot 'Workspace.ps1')

function Get-YLUpmPackages {
    param([string] $Repository, [string[]] $Ids = @('com.kano.ylframework'))
    $known = @('com.kano.ylframework', 'com.kano.ylframework.dotween', 'com.kano.ylframework.excel', 'com.kano.ylframework.domain-excel', 'com.kano.ylframework.hybridclr')
    $pending = [Collections.Generic.Queue[string]]::new()
    foreach ($id in $Ids) { $pending.Enqueue($id) }
    $result = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    while ($pending.Count -gt 0) {
        $id = $pending.Dequeue()
        if ($id -cnotin $known) { throw "Unknown YL package input: $id" }
        if ($result.ContainsKey($id)) { continue }
        $relative = "PackageSources/$id"
        $root = Get-YLTestPath (Join-Path $Repository $relative)
        $manifest = [IO.File]::ReadAllText((Join-Path $root 'package.json')) | ConvertFrom-Json -AsHashtable -DateKind String
        if ($manifest['name'] -isnot [string] -or $manifest['name'] -cne $id -or $manifest['version'] -isnot [string] -or $manifest['version'] -cnotmatch '^\d+\.\d+\.\d+(?:-[\w.-]+)?$') { throw "Invalid package identity: $id" }
        $result.Add($id, [pscustomobject]@{ id = $id; version = $manifest['version']; root = $root; relative = $relative; manifest = $manifest })
        foreach ($dependency in $manifest['dependencies'].GetEnumerator()) {
            if ($dependency.Key.StartsWith('com.kano.ylframework', [StringComparison]::Ordinal)) {
                if ($dependency.Value -isnot [string] -or $dependency.Value -cne $manifest['version']) { throw "Mismatched internal package version: $id -> $($dependency.Key)" }
                $pending.Enqueue($dependency.Key)
            }
        }
    }
    return @($result.Values)
}

function Add-YLUpmDependencies {
    param([Collections.IDictionary] $Manifest, [object[]] $Packages)
    foreach ($package in $Packages) {
        $Manifest['dependencies'][$package.id] = 'file:' + $package.root.Replace('\', '/')
    }
}
