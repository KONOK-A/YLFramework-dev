#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$tracked = @(& git -C $repository -c core.quotepath=false ls-files --cached --full-name)
if ($LASTEXITCODE -ne 0) { throw 'Cannot read the Git index.' }
$ignored = @(& git -C $repository -c core.quotepath=false ls-files --cached --ignored --exclude-standard)
if ($LASTEXITCODE -ne 0) { throw 'Cannot check tracked paths against Git ignore rules.' }

$sdkProjects = @(
    'Tools/YLFramework.Analyzers/Analyzer/YLFramework.Analyzers.csproj'
    'Tools/YLFramework.Analyzers/Tests/YLFramework.Analyzers.Tests.csproj'
)
$localOnly = @(
    '^(Library|Temp|Obj|Logs|UserSettings|Build|Builds|AssetBundles|MemoryCaptures|Recordings|UIElementsSchema|\.vs|\.idea)/'
    '^\.kilo/'
    '^(AGENTS\.md|kilocodeignore)$'
    '^Assets/YLFramework/AGENTS\.md(?:\.meta)?$'
    '^(\.mcp\.json|opencode\.json|\.vscode/mcp\.json)$'
    '^Assets/Plugins/Editor Enhancers Bundle(?:/|\.meta$)'
    '^Assets/Plugins/NuGet(?:/|\.meta$)'
    '^Assets/YLFramework/Tests/Integrations/HybridCLR/PlayMode/Resources/YLHybridCLRTests/(?!\.gitkeep$)'
)
$forbidden = @(
    foreach ($path in $tracked) {
        if ($path -match '\.(sln|slnx|csproj|unityproj|booproj|suo|user|userprefs|pidb|opendb)$' -and $path -cnotin $sdkProjects) {
            $path
            continue
        }
        foreach ($pattern in $localOnly) {
            if ($path -match $pattern) {
                $path
                break
            }
        }
    }
)
$problems = @()
if ($forbidden.Count -ne 0) {
    $problems += "Local or generated paths are tracked:`n$($forbidden -join "`n")"
}
if ($ignored.Count -ne 0) {
    $problems += "Tracked paths match ignore rules:`n$($ignored -join "`n")"
}
if ($problems.Count -ne 0) {
    [Console]::Error.WriteLine($problems -join [Environment]::NewLine)
    exit 1
}
"PASS: Git index hygiene ($($tracked.Count) indexed paths; no tracked local/generated or ignored inputs)."
