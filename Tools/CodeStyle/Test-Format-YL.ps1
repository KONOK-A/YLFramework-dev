#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Assert-That {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) {
        throw $Message
    }
}

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$entry = Join-Path $PSScriptRoot 'Format-YL.ps1'
Assert-That (Test-Path -LiteralPath $entry -PathType Leaf) 'FAIL: the required Format-YL entry is not implemented.'
$temporaryParent = Join-Path $env:LOCALAPPDATA 'Temp/kilo'
Assert-That (Test-Path -LiteralPath $temporaryParent -PathType Container) 'The approved temporary parent must exist.'
$sandbox = Join-Path $temporaryParent ('yl-format-' + [Guid]::NewGuid().ToString('N'))
$root = Join-Path $sandbox 'Repository with spaces'
$utf8 = [Text.UTF8Encoding]::new($false)
$pwsh = (Get-Process -Id $PID).Path
$cases = 0

function Set-Fixture {
    param([string] $RelativePath, [string] $Content)
    $path = Join-Path $root $RelativePath
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllText($path, $Content, $utf8)
    return $path
}

function Invoke-Entry {
    param([string[]] $Arguments = @())
    $output = & $pwsh -NoLogo -NoProfile -NonInteractive -File (Join-Path $root 'Tools/CodeStyle/Format-YL.ps1') @Arguments 2>&1
    return [pscustomobject]@{ Code = $LASTEXITCODE; Output = ($output -join "`n") }
}

function Pass {
    param([string] $Name)
    $script:cases++
    "PASS: $Name"
}

try {
    $null = [IO.Directory]::CreateDirectory((Join-Path $root 'Tools/CodeStyle'))
    [IO.File]::Copy($entry, (Join-Path $root 'Tools/CodeStyle/Format-YL.ps1'))
    [IO.File]::Copy((Join-Path $repository '.editorconfig'), (Join-Path $root '.editorconfig'))
    $null = Set-Fixture 'Tools/YLFramework.Analyzers/global.json' ([IO.File]::ReadAllText((Join-Path $repository 'Tools/YLFramework.Analyzers/global.json')))
    foreach ($relative in @('Tools/Testing/Common/Packages.ps1', 'Tools/Testing/Common/Workspace.ps1', 'Tools/Packaging/PackageCatalog.json')) {
        $null = Set-Fixture $relative ([IO.File]::ReadAllText((Join-Path $repository $relative)))
    }

    $bad = "namespace Demo{public class Example{public int Sum(int a,int b){return a+b;}}}`r`n"
    $first = Set-Fixture 'PackageSources/com.kano.ylframework/Runtime/Example [one].cs' $bad
    $second = Set-Fixture 'PackageSources/com.kano.ylframework/Editor/ExampleTwo.cs' $bad
    $third = Set-Fixture 'Tools/YLFramework.Analyzers/Tests/ExampleThree.cs' $bad
    $catalog = [IO.File]::ReadAllText((Join-Path $repository 'Tools/Packaging/PackageCatalog.json')) | ConvertFrom-Json -DateKind String
    $packageRoots = @($catalog.packages | ForEach-Object { 'PackageSources/' + $_.id })
    $otherAllowedPaths = @(
        'PackageSources/com.kano.ylframework/Samples~/Demo/ExampleSample.cs',
        'PackageSources/com.kano.ylframework.hybridclr/Editor/HybridCLRBuildExample.cs',
        'Assets/Tests/YLFramework/EditMode/ExampleTest.cs',
        'Assets/Editor/YLFrameworkDev/ExampleAuthor.cs',
        'Tools/YLFramework.Analyzers/Analyzer/ExampleAnalyzer.cs'
    )
    $otherAllowedPaths += @($catalog.packages | ForEach-Object {
        'PackageSources/' + $_.id + '/' + [IO.Path]::GetDirectoryName($_.assemblies[0].path).Replace('\', '/') + '/CatalogExample.cs'
    })
    $allowedPaths = @($first, $second, $third) + @($otherAllowedPaths | ForEach-Object { Set-Fixture $_ $bad })
    $protectedPaths = @(
        'Assets/Games/Example.cs',
        'PackageSources/com.kano.ylframework/Plugins/Example.cs',
        'PackageSources/com.kano.ylframework/Runtime/Utilities/ZipTool/ICSharpCode.SharpZipLib/Example.cs',
        'PackageSources/com.kano.ylframework.excel/Editor/Plugins/Example.cs',
        'PackageSources/com.kano.ylframework/Runtime/Generated/Example.cs',
        'PackageSources/com.kano.ylframework/Runtime/Example.g.cs',
        'PackageSources/com.kano.ylframework/Runtime/Example.generated.cs',
        'PackageSources/com.kano.ylframework/Runtime/obj/Example.cs',
        'PackageSources/com.kano.ylframework/Runtime/bin/Example.cs',
        'PackageSources/com.kano.ylframework/RuntimeExtra/Example.cs',
        'PackageSources/com.kano.ylframework/Documentation~/Example.cs',
        'PackageSources/com.kano.ylframework/Samples~/Generated/Example.cs',
        'PackageSources/com.kano.ylframework.hybridclr/Runtime/Generated/Example.cs',
        'PackageSources/com.kano.ylframework.extra/Runtime/Example.cs',
        'PackageSources/com.other.package/Runtime/Example.cs',
        'Packages/com.kano.ylframework/Runtime/Example.cs',
        'Assets/YLFramework/Runtime/Example.cs',
        'Assets/YLFramework/Editor/Example.cs',
        'Assets/YLFramework/Integrations/Example.cs',
        'Assets/YLFramework/Samples/Example.cs',
        'Assets/YLFramework/Tests/Example.cs',
        'Assets/YLFramework/Examples/Core/Modules/Example.cs',
        'Assets/YLFrameworkData/Example.cs',
        'Assets/Tests/YLFrameworkOther/Example.cs',
        'Assets/Tests/YLFramework/Generated/Example.cs',
        'Assets/Tests/YLFramework/Integrations/HybridCLR/PlayMode/Resources/YLHybridCLRTests/Example.cs',
        'Assets/Editor/Other/Example.cs',
        'Assets/Editor/YLFrameworkDevOther/Example.cs',
        'Assets/Editor/YLFrameworkDev/Plugins/Example.cs',
        'Assets/Editor/YLFrameworkDev/Example.g.cs',
        'Tools/YLFramework.Analyzers/Tests/obj/Example.cs',
        'PackageSources/com.kano.ylframework/Runtime/Example.cs.meta'
    )
    foreach ($relative in $protectedPaths) {
        $null = Set-Fixture $relative $bad
    }

    $result = Invoke-Entry
    Assert-That ($result.Code -ne 0 -and $result.Output -match 'Mode=Check' -and $result.Output -match 'WHITESPACE') "Default Check must report real formatting diagnostics: $($result.Output)"
    Assert-That ($result.Output.Contains("Files=$($allowedPaths.Count) SDK=")) "Default Check must include every declared package, tests, author tools and tooling: $($result.Output)"
    foreach ($path in $allowedPaths) {
        Assert-That ([IO.File]::ReadAllText($path) -ceq $bad) 'Default Check must not change source bytes.'
    }
    Pass 'default mode checks without writing'

    $result = Invoke-Entry @('-Mode', 'Apply')
    Assert-That ($result.Code -ne 0 -and $result.Output -match 'explicit') 'Apply without explicit paths must be rejected.'
    Pass 'Apply requires explicit paths'

    foreach ($relative in $protectedPaths) {
        $result = Invoke-Entry @('-Mode', 'Apply', '-Paths', $relative)
        Assert-That ($result.Code -ne 0 -and $result.Output -match 'not allowed') "Protected path accepted: $relative`n$($result.Output)"
        Assert-That ([IO.File]::ReadAllText((Join-Path $root $relative)) -ceq $bad) "Protected path changed: $relative"
    }
    Pass 'vendor, game, generated and non-CSharp inputs rejected'

    foreach ($relative in @('../Outside.cs', $first, 'PackageSources/com.kano.ylframework/Runtime/*.cs', '.', 'Assets', 'Assets/YLFramework')) {
        $result = Invoke-Entry @('-Mode', 'Apply', '-Paths', $relative)
        Assert-That ($result.Code -ne 0 -and $result.Output -match 'not allowed') "Non-relative or wildcard input accepted: $relative"
    }
    Pass 'outside, absolute and wildcard inputs rejected'

    $result = Invoke-Entry @('-Mode', 'Apply', '-Paths', 'PackageSources/com.kano.ylframework/Runtime/Example [one].cs', 'Assets/Games/Example.cs')
    Assert-That ($result.Code -ne 0 -and $result.Output -match 'not allowed') "Every path must be validated before formatting any file: $($result.Output)"
    Assert-That ([IO.File]::ReadAllText($first) -ceq $bad) 'A later rejected path must not leave earlier inputs formatted.'
    Pass 'mixed requests fail before any write'

    $result = Invoke-Entry @('-Mode', 'Apply', '-Paths', 'PackageSources/com.kano.ylframework/Runtime/Example [one].cs')
    Assert-That ($result.Code -eq 0) "Legal Apply failed: $($result.Output)"
    $formatted = [IO.File]::ReadAllText($first)
    Assert-That ($formatted.Contains("`n    public class Example`n    {")) 'Expected four-space Allman formatting.'
    Assert-That ($formatted.Contains('return a + b;')) 'Expected operator spacing.'
    Assert-That (-not $formatted.Contains("`r") -and $formatted.EndsWith("`n")) 'Expected LF and final newline.'
    $bytes = [IO.File]::ReadAllBytes($first)
    Assert-That (-not ($bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191)) 'Unexpected UTF-8 BOM.'
    Assert-That ([IO.File]::ReadAllText($second) -ceq $bad) 'Apply must not format a sibling file.'
    $result = Invoke-Entry @('-Paths', 'PackageSources/com.kano.ylframework/Runtime/Example [one].cs')
    Assert-That ($result.Code -eq 0) "Formatted file is not idempotent: $($result.Output)"
    Pass 'literal spaces/brackets, formatting, encoding and idempotence'

    foreach ($packageRoot in $packageRoots) {
        $result = Invoke-Entry @('-Paths', $packageRoot)
        Assert-That ($result.Code -ne 0 -and $result.Output -match 'WHITESPACE') "Each package root must select its own CSharp sources: $packageRoot`n$($result.Output)"
    }
    Pass 'all catalog package roots select their owned source directories'

    $result = Invoke-Entry @('-Mode', 'Apply', '-Paths', 'PackageSources', 'Assets/Tests/YLFramework', 'Assets/Editor/YLFrameworkDev', 'Tools/YLFramework.Analyzers')
    Assert-That ($result.Code -eq 0) "Allowed root selection failed: $($result.Output)"
    foreach ($path in $allowedPaths) {
        $content = [IO.File]::ReadAllText($path)
        Assert-That ($content.Contains("`n    public class Example`n    {") -and -not $content.Contains("`r")) "Root selection did not apply repository style: $path`n$content"
    }
    $result = Invoke-Entry
    Assert-That ($result.Code -eq 0) "Final Check failed: $($result.Output)"
    foreach ($relative in $protectedPaths) {
        Assert-That ([IO.File]::ReadAllText((Join-Path $root $relative)) -ceq $bad) "Directory traversal touched excluded file: $relative"
    }
    Pass 'directory selection excludes protected descendants'

    for ($index = 0; $index -lt 65; $index++) {
        $name = 'Batch' + $index.ToString('D2')
        # The official whitespace formatter preserves some compact catch/finally blocks; expand those before this gate.
        $content = "namespace Demo{public sealed class $name{public string Name{get;} public int Count{get;set;} public $name(){Name=string.Empty;Count=1;try{Count++;Count++;}finally`n{`nCount++;`n}}}}`r`n"
        $null = Set-Fixture ("PackageSources/com.kano.ylframework/Runtime/Batch/$name.cs") $content
    }
    $result = Invoke-Entry @('-Mode', 'Apply', '-Paths', 'PackageSources/com.kano.ylframework/Runtime')
    Assert-That ($result.Code -eq 0) "Large directory Apply failed: $($result.Output)"
    $result = Invoke-Entry @('-Paths', 'PackageSources/com.kano.ylframework/Runtime')
    Assert-That ($result.Code -eq 0) "A multi-batch directory Apply must converge before reporting success: $($result.Output)"
    $batch = [IO.File]::ReadAllText((Join-Path $root 'PackageSources/com.kano.ylframework/Runtime/Batch/Batch00.cs'))
    Assert-That ($batch.Contains("Name = string.Empty;`n            Count = 1;")) 'Separate statements must be placed on separate lines.'
    Assert-That ($batch.Contains("try`n            {`n                Count++;`n                Count++;`n            }`n            finally`n            {")) "Control-flow blocks must expand to Allman style.`n$batch"
    Pass 'multi-batch directory selection formats nested declarations idempotently'

    $target = Join-Path $sandbox 'Outside'
    $null = [IO.Directory]::CreateDirectory($target)
    [IO.File]::WriteAllText((Join-Path $target 'Example.cs'), $bad, $utf8)
    $link = Join-Path $root 'PackageSources/com.kano.ylframework/Runtime/Linked'
    $null = New-Item -ItemType Junction -Path $link -Target $target
    try {
        $result = Invoke-Entry @('-Mode', 'Apply', '-Paths', 'PackageSources/com.kano.ylframework/Runtime/Linked/Example.cs')
        Assert-That ($result.Code -ne 0 -and $result.Output -match 'link') 'Linked ancestor must be rejected.'
        $result = Invoke-Entry @('-Mode', 'Apply', '-Paths', 'PackageSources/com.kano.ylframework/Runtime')
        Assert-That ($result.Code -ne 0 -and $result.Output -match 'link') 'Directory enumeration must not traverse links.'
        Assert-That ([IO.File]::ReadAllText((Join-Path $target 'Example.cs')) -ceq $bad) 'Link target was modified.'
    }
    finally {
        [IO.Directory]::Delete($link)
    }
    Pass 'junctions rejected before traversal or write'

    "Format-YL tests: $cases passed; repository sources were not formatted."
}
finally {
    if ([IO.Directory]::Exists($sandbox)) {
        [IO.Directory]::Delete($sandbox, $true)
    }
}
