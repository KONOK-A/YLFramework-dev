[CmdletBinding()]
param([switch] $Deploy, [switch] $VerifyDeployment)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($Deploy -and $VerifyDeployment) { throw 'Choose deployment or read-only deployment verification, not both.' }

function Invoke-DotNet {
    param([string[]] $Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE): $($Arguments -join ' ')" }
}

function Assert-AnalyzerMeta {
    param([string] $Path)
    if (-not [IO.File]::Exists($Path)) { throw 'Analyzer meta is missing; refusing to generate a new identity.' }
    $text = [IO.File]::ReadAllText($Path)
    $guids = [regex]::Matches($text, '(?m)^guid:\s*(\S+)\s*$')
    if ($guids.Count -ne 1 -or $guids[0].Groups[1].Value -cne '471f81c4273ee66429677df44cbd6960') {
        throw 'Analyzer GUID differs from the preserved asset identity.'
    }
    if ([regex]::Matches($text, '(?m)^PluginImporter:\s*$').Count -ne 1 -or
        [regex]::Matches($text, '(?m)^- RoslynAnalyzer\s*$').Count -ne 1) {
        throw 'Analyzer must use PluginImporter and exactly one case-sensitive RoslynAnalyzer label.'
    }
    $reference = [regex]::Matches($text, '(?m)^\s+isExplicitlyReferenced:\s*(\S+)\s*$')
    if ($reference.Count -ne 1 -or $reference[0].Groups[1].Value -ne '1') {
        throw 'Analyzer automatic Runtime reference must remain disabled (isExplicitlyReferenced: 1).'
    }
    $platforms = [regex]::Matches($text, '(?m)^\s+enabled:\s*(\S+)\s*$')
    if ($platforms.Count -eq 0 -or @($platforms | Where-Object { $_.Groups[1].Value -ne '0' }).Count -ne 0) {
        throw 'Analyzer platform flags must all remain disabled.'
    }
}

function Assert-AnalyzerDeployment {
    param([string] $Built, [string] $Destination)
    Assert-AnalyzerMeta -Path ($Destination + '.meta')
    if (-not [IO.File]::Exists($Built) -or -not [IO.File]::Exists($Destination)) { throw 'The verified build or deployed Analyzer DLL is missing.' }
    if ((Get-FileHash -LiteralPath $Built -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash) {
        throw 'Deployed Analyzer is stale or changed; run the verified build with -Deploy before source delivery.'
    }
}

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$tests = Join-Path $PSScriptRoot 'Tests/YLFramework.Analyzers.Tests.csproj'
$output = Join-Path $root 'Temp/YLFramework.Analyzers'
$dll = Join-Path $output 'YLFramework.Analyzers/bin/Release/netstandard2.0/YLFramework.Analyzers.dll'
$destination = Join-Path $root 'PackageSources/com.kano.ylframework/Runtime/Core/Analyzers/YLFramework.Analyzers.dll'
if (-not (Test-Path -LiteralPath (Join-Path $root 'Temp'))) { throw 'Repository Temp parent is missing.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
try { $entryLock = [IO.File]::Open((Join-Path $output 'entry.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
catch [IO.IOException] { throw 'Another analyzer build/deploy entry is running; refusing shared output access.' }
$before = if (Test-Path -LiteralPath $destination) { (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash } else { $null }

Push-Location -LiteralPath $PSScriptRoot
try {
    if ($Deploy -or $VerifyDeployment) { Assert-AnalyzerMeta -Path ($destination + '.meta') }
    $sdk = & dotnet --version
    if ($LASTEXITCODE -ne 0 -or $sdk -ne '9.0.312') { throw "Expected SDK 9.0.312; actual: $sdk" }
    "PowerShell=$($PSVersionTable.PSVersion) SDK=$sdk"
    Invoke-DotNet -Arguments @('restore', $tests, '--locked-mode')
    Invoke-DotNet -Arguments @('build', $tests, '-c', 'Release', '--no-restore')
    Invoke-DotNet -Arguments @('test', $tests, '-c', 'Release', '--no-build', '--no-restore', '--results-directory', (Join-Path $output 'TestResults'), '--logger', 'trx;LogFileName=analyzers.trx', '--logger', 'console;verbosity=minimal')

    $inputs = @('global.json', 'Directory.Build.props', 'build.ps1', 'Analyzer/YLFramework.Analyzers.csproj', 'Analyzer/packages.lock.json',
        '../../PackageSources/com.kano.ylframework/Editor/Core/Analyzers/AnalyzerRuleCatalog.cs')
    $inputs += @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Analyzer') -Filter '*.cs' -File -Recurse |
        ForEach-Object { $_.FullName.Substring($PSScriptRoot.Length + 1).Replace('\', '/') })
    $files = @($inputs | Sort-Object | ForEach-Object {
        [ordered]@{ Path = $_; Sha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $_) -Algorithm SHA256).Hash }
    })
    $receipt = [ordered]@{
        Sdk = $sdk
        Version = '1.0.0'
        Inputs = $files
        DllSha256 = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
    } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText((Join-Path $output 'build-info.json'), $receipt, [Text.UTF8Encoding]::new($false))

    if ($Deploy) {
        Assert-AnalyzerMeta -Path ($destination + '.meta')
        # 合作入口串行；指纹检查拒绝已观察到的外部改动，不能替代任意外部写者的 compare-and-swap。
        $current = if (Test-Path -LiteralPath $destination) { (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash } else { $null }
        if ($current -ne $before) { throw 'Analyzer destination changed during verification; deployment refused.' }
        $parent = [IO.Path]::GetDirectoryName($destination)
        if (-not (Test-Path -LiteralPath $parent)) { [IO.Directory]::CreateDirectory($parent) | Out-Null }
        if ($current -ne (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash) {
            $stage = Join-Path $output ('YLFramework.Analyzers.' + [Guid]::NewGuid().ToString('N') + '.tmp')
            try {
                [IO.File]::Copy($dll, $stage, $false)
                $stagedCurrent = if (Test-Path -LiteralPath $destination) { (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash } else { $null }
                if ($stagedCurrent -ne $before) { throw 'Analyzer destination changed during staging; deployment refused.' }
                if ($null -eq $current) { [IO.File]::Move($stage, $destination) }
                else { [IO.File]::Replace($stage, $destination, [NullString]::Value) }
            }
            finally { if ([IO.File]::Exists($stage)) { [IO.File]::Delete($stage) } }
        }
        "Deployed=$destination"
    }
    if ($VerifyDeployment) {
        Assert-AnalyzerDeployment -Built $dll -Destination $destination
        'PASS: deployed Analyzer exactly matches this verified deterministic source build; no package files were written.'
    }
    "DLL=$dll"
    "SHA256=$((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash)"
    "BuildInfo=$(Join-Path $output 'build-info.json')"
}
finally { Pop-Location; $entryLock.Dispose() }
