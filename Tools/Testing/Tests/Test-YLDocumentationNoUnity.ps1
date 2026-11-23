#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param([switch] $SelfTest)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

# This is an entry-point check, not a general Markdown linter. Sources are the root/tool
# guides, package READMEs, handbooks, sample guides and the maintained Excel Gameplay entry.
# Licenses, history, local-only and vendor docs are not scanned. Fragments are checked only
# in the selected Markdown pages; raw HTML links, non-Markdown fragments and external URLs
# (other than this repo's main blob/tree) are out of scope.
# Use the Markdig assembly already shipped with PowerShell, with GitHub (not ASCII) heading IDs.
$null = ConvertFrom-Markdown -InputObject '# Markdown parser bootstrap'
$builder = [Markdig.MarkdownPipelineBuilder]::new()
$null = [Markdig.MarkdownExtensions]::UsePipeTables($builder, $null)
$null = [Markdig.MarkdownExtensions]::UsePreciseSourceLocation($builder)
$null = [Markdig.MarkdownExtensions]::UseAutoIdentifiers($builder, 'GitHub')
$markdownPipeline = $builder.Build()

function Read-YLDocumentationPage([string] $Text) {
    $result = @{
        anchors = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        code = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        links = [Collections.Generic.List[object]]::new()
        rows = [Collections.Generic.List[object]]::new()
    }
    $doc = [Markdig.Markdown]::Parse($Text, $markdownPipeline)
    foreach ($node in [Markdig.Syntax.MarkdownObjectExtensions]::Descendants($doc)) {
        if ($node -is [Markdig.Syntax.Inlines.LinkInline]) {
            $result.links.Add(@{ url = [string]$node.Url; line = $node.Line + 1; navigation = -not $node.IsImage -and $null -ne $node.FirstChild; target = $null })
        } elseif ($node -is [Markdig.Syntax.Inlines.AutolinkInline] -and -not $node.IsEmail) {
            $result.links.Add(@{ url = $node.Url; line = $node.Line + 1; navigation = $true; target = $null })
        } elseif ($node -is [Markdig.Syntax.HeadingBlock]) {
            $null = $result.anchors.Add([Markdig.Renderers.Html.HtmlAttributesExtensions]::GetAttributes($node).Id)
        } elseif ($node -is [Markdig.Syntax.Inlines.CodeInline]) {
            $null = $result.code.Add($node.Content)
        } elseif ($node -is [Markdig.Extensions.Tables.TableRow] -and -not $node.IsHeader -and $node.Count -ne 0) {
            foreach ($part in [Markdig.Syntax.MarkdownObjectExtensions]::Descendants($node[0])) {
                if ($part -is [Markdig.Syntax.Inlines.CodeInline] -and $part.Content -cmatch '^com\.kano\.') {
                    $result.rows.Add(@{ id = $part.Content; line = $node.Line + 1 })
                }
            }
        } elseif ($node -is [Markdig.Syntax.Inlines.HtmlInline] -or $node -is [Markdig.Syntax.HtmlBlock]) {
            # Explicit anchors in the docs use a bare <a id="..."> or <a name="..."> tag.
            $html = if ($node -is [Markdig.Syntax.Inlines.HtmlInline]) { $node.Tag } else { $node.Lines.ToString() }
            $html = $html -replace '(?s)<!--.*?(?:-->|$)', ''
            foreach ($anchor in [regex]::Matches($html, '(?i)<a\s+(?:id|name)\s*=\s*(["''])([^"'']+)\1\s*>')) {
                $null = $result.anchors.Add([Net.WebUtility]::HtmlDecode($anchor.Groups[2].Value))
            }
        }
    }
    return $result
}

function Resolve-YLDocumentationLink([string] $Source, [string] $Url) {
    $parts = $Url.Split('#', 2)
    $path = $parts[0].Split('?', 2)[0]
    $fragment = if ($parts.Count -eq 2) { [uri]::UnescapeDataString($parts[1]) } else { '' }
    $view = 'relative'
    $segments = [Collections.Generic.List[string]]::new()
    if ($path -cmatch '^(?i:https://github\.com/KONOK-A/YLFramework-dev)/(?<view>blob|tree)/main(?:/|$)') {
        $view = $Matches['view']
        $path = $path.Substring($Matches[0].Length)
    } elseif ($path -match '^(?:[a-z][a-z0-9+.-]*:|//)') {
        return $null
    } elseif ($path.StartsWith('/')) {
        $path = $path.Substring(1)
    } elseif (-not $path) {
        return @{ path = $Source; fragment = $fragment; view = 'blob' }
    } elseif ($Source.Contains('/')) {
        $segments.AddRange($Source.Substring(0, $Source.LastIndexOf('/')).Split('/'))
    }

    $path = [uri]::UnescapeDataString($path)
    if ($path -match '^/|[\\:\x00-\x1f]') { throw [FormatException]::new('Not a repository-relative path.') }
    foreach ($part in $path.Split('/', [StringSplitOptions]::RemoveEmptyEntries)) {
        if ($part -ceq '.') { continue }
        if ($part -ceq '..') {
            if ($segments.Count -eq 0) { throw [FormatException]::new('Path escapes the repository root.') }
            $segments.RemoveAt($segments.Count - 1)
        } else { $segments.Add($part) }
    }
    if ($view -ceq 'relative' -and $path.EndsWith('/')) { $view = 'tree' }
    return @{ path = $segments -join '/'; fragment = $fragment; view = $view }
}

function Get-YLDocumentationReport([string[]] $PackageIds, [string[]] $Paths, [scriptblock] $ReadText) {
    $mainId = 'com.kano.ylframework'
    $mainDocs = "PackageSources/$mainId/Documentation~"
    $nav = "$mainDocs/README.md"
    $top = @('README.md', 'CONTRIBUTING.md', 'Tools/Testing/README.md', 'Tools/Packaging/README.md')
    $files = [Collections.Generic.HashSet[string]]::new($Paths, [StringComparer]::Ordinal)
    $directories = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $null = $directories.Add('')
    $pages = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    $problems = [Collections.Generic.List[string]]::new()
    foreach ($path in $Paths) {
        for ($dir = $path; $dir.Contains('/');) {
            $dir = $dir.Substring(0, $dir.LastIndexOf('/'))
            if (-not $directories.Add($dir)) { break }
        }
        if ($path -cmatch '^PackageSources/([^/]+)/package\.json$' -and $Matches[1] -cnotin $PackageIds) {
            $problems.Add("${path}: package not listed in PackageCatalog.json.")
        }
        if ($path -cnotin $top -and $path -cnotmatch '^PackageSources/(?:[^/]+/(?:README\.md|(?:Documentation~|Samples~)/.+\.md)|com\.kano\.ylframework\.excel/Editor/Gameplay/README\.md)$') { continue }
        if ($path -match '(?:^|/)(?:licen[cs]e|notice|third[-_ ]?party|changelog|history|archives?|legacy|agents)(?:[-_. /]|$)') { continue }
        $text = & $ReadText $path
        if ([string]::IsNullOrWhiteSpace($text)) { $problems.Add("${path}: empty documentation page.") }
        $pages[$path] = Read-YLDocumentationPage $text
    }
    foreach ($path in $top + @($nav)) {
        if (-not $pages.ContainsKey($path)) { $problems.Add("${path}: required documentation page missing.") }
    }

    $links = 0; $fragments = 0
    foreach ($source in $pages.Keys) {
        foreach ($link in $pages[$source].links) {
            $at = "${source}:$($link.line)"
            try { $destination = Resolve-YLDocumentationLink $source $link.url }
            catch [FormatException] { $problems.Add("${at}: '$($link.url)': $($_.Exception.Message)"); continue }
            if ($null -eq $destination) { continue }
            $links++
            $target = $destination.path
            if (($destination.view -ceq 'tree' -or -not $files.Contains($target)) -and
                ($destination.view -ceq 'blob' -or -not $directories.Contains($target))) {
                $problems.Add("${at}: '$($link.url)' has no repository target of the expected case/type: '$target'.")
                continue
            }
            if ($destination.fragment -and $pages.ContainsKey($target)) {
                $fragments++
                if (-not $pages[$target].anchors.Contains($destination.fragment)) {
                    $problems.Add("${at}: '$($link.url)' has no Markdown anchor '#$($destination.fragment)' in '$target'.")
                    continue
                }
            }
            if ($link.navigation) { $link.target = $target }
        }
    }

    function Require-Link([string] $From, [string] $To, [int] $Line = 0) {
        if (-not $pages.ContainsKey($From)) { return }
        foreach ($link in $pages[$From].links) {
            if ($link.target -ceq $To -and ($Line -eq 0 -or $link.line -eq $Line)) { return }
        }
        $location = if ($Line) { "${From}:$Line" } else { $From }
        $problems.Add("${location}: missing a clickable navigation link to '$To'.")
    }

    Require-Link 'README.md' $nav
    $modules = @($pages.Keys | Where-Object { $_.StartsWith("$mainDocs/Modules/", [StringComparison]::Ordinal) })
    if (-not $modules.Count) { $problems.Add("$mainDocs/Modules/: no main-package module guides found.") }
    foreach ($module in $modules) { Require-Link $nav $module }

    $packageRows = [Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
    if ($pages.ContainsKey($nav)) {
        foreach ($row in $pages[$nav].rows) {
            if ($row.id -cnotin $PackageIds -or -not $packageRows.TryAdd($row.id, $row.line)) {
                $problems.Add("${nav}:$($row.line): unknown or duplicate package row '$($row.id)'.")
            }
        }
    }
    foreach ($id in $PackageIds) {
        $root = "PackageSources/$id"
        $manifest = "$root/package.json"
        $readme = "$root/README.md"
        $guide = "$root/Documentation~/README.md"
        if (-not $files.Contains($manifest)) { $problems.Add("${manifest}: catalog package manifest missing.") }
        else {
            $json = ConvertFrom-Json -InputObject (& $ReadText $manifest) -AsHashtable -NoEnumerate
            if ($json -isnot [System.Collections.IDictionary] -or $json['name'] -isnot [string] -or $json['name'] -cne $id) {
                $problems.Add("${manifest}: name must match catalog package ID '$id'.")
            }
        }
        if (-not $pages.ContainsKey($readme) -or -not $pages[$readme].code.Contains($id)) {
            $problems.Add("${readme}: package entry missing or does not display package ID '$id' as inline code.")
        }
        if (-not $pages.ContainsKey($guide)) { $problems.Add("${guide}: package handbook entry missing.") }
        Require-Link $readme $guide
        if ($id -cne $mainId) {
            if ($packageRows.ContainsKey($id)) { Require-Link $nav $guide $packageRows[$id] }
            else { $problems.Add("${nav}: missing optional package navigation row for '$id'.") }
        }
    }
    return @{ problems = $problems; pages = $pages.Count; links = $links; fragments = $fragments; modules = $modules.Count; packages = $PackageIds.Count }
}

function Test-YLDocumentationFixtures {
    $main = 'PackageSources/com.kano.ylframework'
    $optional = 'PackageSources/com.kano.ylframework.example'
    $nav = "$main/Documentation~/README.md"
    $module = "$main/Documentation~/Modules/Example (A).md"
    $guide = "$optional/Documentation~/README.md"
    $readme = "$optional/README.md"
    $manifest = "$optional/package.json"
    $fixture = @{
        'README.md' = "[Guide]($nav)"
        'CONTRIBUTING.md' = '# Contributing'
        'Tools/Testing/README.md' = '# Testing'
        'Tools/Packaging/README.md' = '# Packaging'
        "$main/package.json" = '{"name":"com.kano.ylframework"}'
        "$main/README.md" = '`com.kano.ylframework` [guide](Documentation~/README.md)'
        "$main/Runtime/Source.cs" = ''
        $manifest = '{"name":"com.kano.ylframework.example"}'
        $readme = '`com.kano.ylframework.example` [manual](Documentation~/README.md)'
        $guide = '# Manual'
        "$optional/Documentation~/ThirdParty/LICENSE.md" = '[Not checked](missing.md)'
        "$optional/CHANGELOG.md" = '[Not checked](missing.md)'
        $nav = @'
# Guide

[Module][mod] and [repeat](#repeat-1)

[mod]: <Modules/Example (A).md#explicit> "Referenced module"

| Package | Manual |
| --- | --- |
| `com.kano.ylframework.example` | [Manual](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework.example/Documentation%7E/README.md#manual) |

## Repeat
## Repeat

`[not a link](missing.md)`

```md
[not a link](missing.md)
```
<!-- <a id="comment-only"></a> -->

[Other repository](https://github.com/other/YLFramework-dev/blob/main/missing.md)
[Other branch](https://github.com/KONOK-A/YLFramework-dev/blob/old/missing.md)
[Outside CI](https://example.invalid/offline)
'@
        $module = @'
# Unicode heading

<a id="explicit"></a>

[Source directory](../../Runtime/) and [source file](../../Runtime/Source.cs#L1).
'@ + "`n`n## $([char]0x4f8b)`n`n[Unicode](#%E4%BE%8B)"
    }

    $ids = @('com.kano.ylframework', 'com.kano.ylframework.example')
    $variants = @(
        @{ name = 'missing path'; path = $module; text = '[Bad](missing.md)'; want = 'missing.md' },
        @{ name = 'wrong path case'; path = $module; text = '[Bad](../../runtime/Source.cs)'; want = '../../runtime/Source.cs' },
        @{ name = 'missing Markdown anchor'; path = $module; text = '[Bad](#absent)'; want = '#absent' },
        @{ name = 'comment is not an anchor'; path = $nav; text = '[Bad](#comment-only)'; want = '#comment-only' },
        @{ name = 'same repository URL missing path'; path = $module; text = '[Bad](https://github.com/KONOK-A/YLFramework-dev/blob/main/missing.md)'; want = '/blob/main/missing.md' },
        @{ name = 'ignored target'; path = $module; text = '[Bad](/AGENTS.md)'; want = '/AGENTS.md' },
        @{ name = 'outside repository'; path = 'README.md'; text = '[Bad](../README.md)'; want = '../README.md' },
        @{ name = 'image is not module navigation'; path = $nav; replace = @('[Module][mod]', '![Module][mod]'); want = $module },
        @{ name = 'same URL elsewhere is not a package row link'; path = $nav; replace = @('| [Manual](', '| Manual ('); text = "[Manual](/$guide)"; want = 'com.kano.ylframework.example' },
        @{ name = 'code span is not package README navigation'; path = $readme; replace = @('[manual](Documentation~/README.md)', '`Documentation~/README.md`'); want = $guide },
        @{ name = 'package manifest identity must match'; path = $manifest; replace = @('framework.example', 'framework.other'); want = $manifest },
        @{ name = 'package manifest name must be a string'; path = $manifest; replace = @('"com.kano.ylframework.example"', '["com.kano.ylframework.example"]'); want = $manifest },
        @{ name = 'package catalog grows without hard-coded size'; packageId = 'com.kano.ylframework.new'; want = 'com.kano.ylframework.new' }
    )
    foreach ($case in $variants) {
        $changed = $fixture.Clone()
        $caseIds = $ids
        if ($case.ContainsKey('packageId')) { $caseIds += $case.packageId }
        else {
            if ($case.ContainsKey('replace')) { $changed[$case.path] = $changed[$case.path].Replace($case.replace[0], $case.replace[1]) }
            if ($case.ContainsKey('text')) { $changed[$case.path] += "`n`n" + $case.text }
        }
        $actual = Get-YLDocumentationReport $caseIds @($changed.Keys) { param($p) $changed[$p] }
        if (-not ($actual.problems -join "`n").Contains($case.want, [StringComparison]::Ordinal)) {
            throw "Documentation fixture '$($case.name)' did not reject '$($case.want)': $($actual.problems -join '; ')"
        }
    }
    $valid = Get-YLDocumentationReport $ids @($fixture.Keys) { param($p) $fixture[$p] }
    if ($valid.problems.Count) { throw "Valid documentation fixture was rejected: $($valid.problems -join '; ')" }
    "PASS: documentation fixtures (1 valid, $($variants.Count) invalid)."
}

$fixtureResult = Test-YLDocumentationFixtures
if ($SelfTest) { $fixtureResult; return }

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
. (Join-Path $PSScriptRoot '../Common/Packages.ps1')
$packageIds = @((Read-YLPackageCatalog)['packages'] | ForEach-Object { $_['id'] })

function Read-YLDocumentationPaths([string[]] $Options) {
    $output = & git --no-optional-locks -C $repository ls-files --full-name -z @Options
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read repository paths for documentation checks.' }
    return ($output -join "`n").Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)
}

# Case-sensitive Git paths keep a Windows checkout from hiding case errors and ignored local-only targets.
$visible = [Collections.Generic.HashSet[string]]::new([string[]]@(Read-YLDocumentationPaths @('--cached', '--others', '--exclude-standard')), [StringComparer]::Ordinal)
$visible.ExceptWith([string[]]@(Read-YLDocumentationPaths @('--deleted')))
$visible.ExceptWith([string[]]@(Read-YLDocumentationPaths @('--cached', '--ignored', '--exclude-standard')))
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$report = Get-YLDocumentationReport $packageIds @($visible) { param($p) [IO.File]::ReadAllText((Join-Path $repository $p), $utf8) }
if ($report.problems.Count) {
    [Console]::Error.WriteLine($report.problems -join "`n")
    exit 1
}
$fixtureResult
"PASS: $($report.pages) maintained Markdown pages; $($report.links) repository links, $($report.fragments) Markdown fragments; $($report.packages) package entries and $($report.modules) main-package module links. Offline; no Unity."
