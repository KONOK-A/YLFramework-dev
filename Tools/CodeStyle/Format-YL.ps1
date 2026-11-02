#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param(
    [ValidateSet('Check', 'Apply')]
    [string] $Mode = 'Check',
    [string[]] $Paths = @(),
    [Parameter(ValueFromRemainingArguments = $true, DontShow = $true)]
    [string[]] $AdditionalPaths = @()
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
# Native pwsh -File binds only the first value of a named array parameter.
$Paths = @($Paths) + @($AdditionalPaths)
if (-not $IsWindows) {
    throw 'This entry targets the Windows Unity workspace.'
}
if ($Mode -eq 'Apply' -and $Paths.Count -eq 0) {
    throw 'Apply requires explicit repository-relative paths.'
}

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$rootPrefix = $root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$sdkDirectory = Join-Path $root 'Tools/YLFramework.Analyzers'
$comparison = [StringComparison]::OrdinalIgnoreCase
$packageRoots = @(
    'PackageSources/com.kano.ylframework',
    'PackageSources/com.kano.ylframework.dotween',
    'PackageSources/com.kano.ylframework.excel',
    'PackageSources/com.kano.ylframework.domain-excel',
    'PackageSources/com.kano.ylframework.hybridclr'
)
$allowedRoots = @($packageRoots | ForEach-Object { "$_/Runtime"; "$_/Editor"; "$_/Samples~" }) + @(
    'Assets/Tests/YLFramework',
    'Assets/Editor/YLFrameworkDev',
    'Tools/YLFramework.Analyzers/Analyzer',
    'Tools/YLFramework.Analyzers/Tests'
)
$allowedFiles = @('Tools/Testing/NUnitHost.cs', 'Tools/Testing/DistributionEntry.cs', 'Tools/Testing/PlayerSmoke.cs',
    'Tools/Testing/HybridCLREntry.cs', 'Tools/Testing/HybridCLRSmoke.cs', 'Tools/Testing/IntegrationEntry.cs', 'Tools/Testing/DevelopmentHostEntry.cs')
$files = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

function Test-AllowedPath {
    param([string] $RelativePath)
    if ($RelativePath -match '(^|/)(Plugins|Generated|obj|bin)(/|$)|\.(g|generated)\.cs$' -or
        $RelativePath -match '^PackageSources/com.kano.ylframework/Runtime/Utilities/(Json|ZipTool/ICSharpCode\.SharpZipLib)(/|$)' -or
        $RelativePath -match '^Assets/Tests/YLFramework/Integrations/HybridCLR/PlayMode/Resources(/|$)') {
        return $false
    }
    if ($RelativePath -in @('PackageSources', 'Tools/YLFramework.Analyzers') -or
        $RelativePath -in $packageRoots -or $RelativePath -in $allowedFiles) {
        return $true
    }
    foreach ($prefix in $allowedRoots) {
        if ($RelativePath.Equals($prefix, $comparison) -or $RelativePath.StartsWith($prefix + '/', $comparison)) {
            return $true
        }
    }
    return $false
}

function Assert-NoLink {
    param([string] $Path)
    $current = $Path
    while ($current.Equals($root, $comparison) -or $current.StartsWith($rootPrefix, $comparison)) {
        if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Symbolic link or junction is not allowed: $current"
        }
        if ($current.Equals($root, $comparison)) {
            break
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}

function Add-Candidates {
    param([string] $Path)
    Assert-NoLink $Path
    if ([IO.Directory]::Exists($Path)) {
        foreach ($child in [IO.Directory]::EnumerateFileSystemEntries($Path)) {
            $relative = [IO.Path]::GetRelativePath($root, $child).Replace('\', '/')
            if (Test-AllowedPath $relative) {
                Add-Candidates $child
            }
        }
    }
    elseif ($Path.EndsWith('.cs', $comparison)) {
        $null = $files.Add($Path)
    }
}

function Invoke-DotNet {
    param([string[]] $Arguments)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.WorkingDirectory = $sdkDirectory
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        return [pscustomobject]@{
            Code = $process.ExitCode
            Output = $stdout.GetAwaiter().GetResult()
            Error = $stderr.GetAwaiter().GetResult()
        }
    }
    finally {
        $process.Dispose()
    }
}

if (-not [IO.File]::Exists((Join-Path $root '.editorconfig')) -or
    -not [IO.File]::Exists((Join-Path $sdkDirectory 'global.json'))) {
    throw 'The repository style configuration and pinned SDK configuration are required.'
}

if ($Paths.Count -eq 0) {
    $Paths = @($allowedRoots + $allowedFiles | Where-Object { Test-Path -LiteralPath (Join-Path $root $_) })
}

# Collect and validate every input before allowing the formatter to write anything.
foreach ($relative in $Paths) {
    if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or $relative -match '[*?:]') {
        throw "Path is not allowed; use literal repository-relative paths: $relative"
    }
    $path = [IO.Path]::GetFullPath((Join-Path $root $relative))
    $normalized = [IO.Path]::GetRelativePath($root, $path).Replace('\', '/')
    if (-not $path.StartsWith($rootPrefix, $comparison) -or -not (Test-AllowedPath $normalized) -or
        -not (Test-Path -LiteralPath $path) -or
        (-not [IO.Directory]::Exists($path) -and -not $path.EndsWith('.cs', $comparison))) {
        throw "Path is not allowed or does not exist: $relative"
    }
    Add-Candidates $path
}
if ($files.Count -eq 0) {
    throw 'No allowed CSharp files were selected.'
}

$sdk = Invoke-DotNet @('--version')
if ($sdk.Code -ne 0 -or $sdk.Output.Trim() -ne '9.0.312') {
    throw "Expected SDK 9.0.312; actual: $($sdk.Output.Trim()) $($sdk.Error.Trim())"
}

"Mode=$Mode Files=$($files.Count) SDK=9.0.312"
$exitCode = 0
$groups = $files | Sort-Object | Group-Object {
    $relative = [IO.Path]::GetRelativePath($root, $_).Replace('\', '/')
    if ($relative.StartsWith('PackageSources/', $comparison)) {
        Join-Path $root (($relative -split '/', 3)[0..1] -join '/')
    }
    elseif ($relative.StartsWith('Assets/Tests/YLFramework/', $comparison)) {
        Join-Path $root 'Assets/Tests/YLFramework'
    }
    elseif ($relative.StartsWith('Assets/Editor/YLFrameworkDev/', $comparison)) {
        Join-Path $root 'Assets/Editor/YLFrameworkDev'
    }
    elseif ($_.StartsWith($sdkDirectory + [IO.Path]::DirectorySeparatorChar, $comparison)) {
        $sdkDirectory
    }
    else {
        Join-Path $root 'Tools'
    }
}
foreach ($group in $groups) {
    # Bound the Windows native command line even for large source batches.
    $inputs = @($group.Group | ForEach-Object { [IO.Path]::GetRelativePath($group.Name, $_).Replace('\', '/') })
    for ($offset = 0; $offset -lt $inputs.Count; $offset += 32) {
        $last = [Math]::Min($offset + 31, $inputs.Count - 1)
        # Folder mode never loads/restores a project; it rejects --no-restore.
        $arguments = @('format', 'whitespace', $group.Name, '--folder', '--verbosity', 'minimal')
        if ($Mode -eq 'Check') {
            $arguments += '--verify-no-changes'
        }
        $arguments += @('--include') + $inputs[$offset..$last]
        $result = Invoke-DotNet $arguments
        if ($result.Output.Trim()) {
            $result.Output.TrimEnd()
        }
        if ($result.Error.Trim()) {
            $result.Error.TrimEnd()
        }
        if ($result.Code -ne 0) {
            $exitCode = $result.Code
            if ($Mode -eq 'Apply') {
                exit $exitCode
            }
        }
    }
}
exit $exitCode
