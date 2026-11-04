# Test-only miniature payloads. These helpers never attest a Unity installation or compilation.
function Get-YLFixturePackageCatalog {
    param([switch] $ConditionalAdapter)
    $catalog = Read-YLPackageCatalog
    foreach ($package in $catalog.packages) {
        $package.requiredFiles = @(); $package.samples = @()
        $package.assemblies = @($package.assemblies | Where-Object { -not $_.path.StartsWith('Samples~/', [StringComparison]::Ordinal) })
    }
    if ($ConditionalAdapter) {
        @($catalog.packages | Where-Object id -CEQ 'com.kano.ylframework')[0].assemblies += @{
            name = 'YLFramework.FixtureAdapter'; path = 'Runtime/Integration/Fixture/YLFramework.FixtureAdapter.asmdef'; player = $true
            defineConstraints = @('DOTWEEN'); versionDefines = @()
        }
    }
    return $catalog
}

function Add-YLFixtureAssemblyPayload {
    param([Collections.IDictionary] $Catalog, [string] $PackageId, [string] $Root, [switch] $Sources)
    $package = @($Catalog.packages | Where-Object id -CEQ $PackageId)[0]
    foreach ($assembly in $package.assemblies) {
        $path = Join-Path $Root $assembly.path
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
        $definition = @{ name = $assembly.name; references = @(); includePlatforms = @(); excludePlatforms = @()
            defineConstraints = $assembly.defineConstraints; versionDefines = $assembly.versionDefines }
        if (-not $assembly.player) { $definition.includePlatforms = @('Editor') }
        [IO.File]::WriteAllText($path, (ConvertTo-Json -InputObject $definition -Depth 15), [Text.UTF8Encoding]::new($false))
        if ($Sources) { [IO.File]::WriteAllText((Join-Path ([IO.Path]::GetDirectoryName($path)) 'Product.cs'), '// NoUnity source inventory fixture') }
    }
}

function Set-YLFixtureCatalogInput {
    param([object] $HostRecord, [Collections.IDictionary] $Catalog, [string] $InputHash)
    $path = Join-Path $HostRecord.path 'YLTesting/PackageCatalog.json'
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllText($path, (ConvertTo-Json -InputObject $Catalog -Depth 30), [Text.UTF8Encoding]::new($false))
    $digest = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $statePath = Join-Path $HostRecord.path '.yl-inputs.json'
    $files = @()
    if ([IO.File]::Exists($statePath)) {
        $state = [IO.File]::ReadAllText($statePath) | ConvertFrom-Json -AsHashtable -DateKind String
        if ($state.Contains('files')) { $files = @($state.files | Where-Object path -CNE 'YLTesting/PackageCatalog.json') }
    }
    $files += @{ path = 'YLTesting/PackageCatalog.json'; sha256 = $digest }
    $state = @{ schemaVersion = 1; purpose = 'YLTesting.HostInputs'; hostId = $HostRecord.id; inputHash = $InputHash; files = $files }
    [IO.File]::WriteAllText($statePath, (ConvertTo-Json -InputObject $state -Depth 15), [Text.UTF8Encoding]::new($false))
    return $digest
}
