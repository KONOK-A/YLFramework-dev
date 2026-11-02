#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $TempGoalRoot,
    [switch] $CheckCSharp,
    [string] $EditorRoot = 'G:\Unity Editor\6000.3.27f1\Editor',
    [string] $RuntimeAssembly = 'G:\Unity\U63-20261027-c63a1\k2\Library\ScriptAssemblies\YLFramework.Runtime.dll'
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$allowed = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'kilo/U63-20261027-c63a1'))
$root = [IO.Path]::GetFullPath($TempGoalRoot).TrimEnd('\')
if (-not $IsWindows -or -not [IO.Path]::IsPathFullyQualified($TempGoalRoot) -or
    -not $root.StartsWith($allowed + '\', [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::Exists($root) -or -not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($root))) {
    throw 'Use a fresh child of the existing Temp/kilo/U63-20261027-c63a1 only.'
}
for ($cursor = $root; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
    if ([IO.Path]::Exists($cursor) -and ([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Linked guard root rejected.'
    }
}
$null = [IO.Directory]::CreateDirectory($root)
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../..'))
$entry = Join-Path $PSScriptRoot 'Install-B05AudioAcceptance.ps1'
$template = Join-Path $PSScriptRoot 'B05AudioAcceptanceBuild.cs.txt'
$rows = [Collections.Generic.List[object]]::new()
function Case([string] $Name, [scriptblock] $Action) {
    try { & $Action; $rows.Add(@{ Name=$Name; Passed=$true }) }
    catch { $rows.Add(@{ Name=$Name; Passed=$false; Failure=$_.Exception.Message }) }
}
function Assert([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Put([string] $Path, [string] $Text) {
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}
function Json([string] $Path, $Value) { Put $Path (ConvertTo-Json -InputObject $Value -Depth 30) }
function Sha([string] $Path) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($Path))).ToLowerInvariant()
}
function Change([string] $Path, [scriptblock] $Action) {
    $value = [IO.File]::ReadAllText($Path) | ConvertFrom-Json -AsHashtable
    & $Action $value
    Json $Path $value
}
Case 'installer entry exists' { Assert ([IO.File]::Exists($entry)) 'Missing installer entry (RED).' }
Case 'Editor template exists' { Assert ([IO.File]::Exists($template)) 'Missing Editor template (RED).' }
$ast = $null
if ([IO.File]::Exists($entry)) {
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($entry, [ref]$tokens, [ref]$errors)
    Case 'installer AST parses' { Assert ($errors.Count -eq 0) ($errors -join "`n") }
    # Only real functions are loaded. The delivery-pinned public entry is never invoked or relaxed.
    foreach ($function in @($ast.EndBlock.Statements | Where-Object {
        $_ -is [Management.Automation.Language.FunctionDefinitionAst]
    })) { . ([scriptblock]::Create($function.Extent.Text)) }
}
function Fixture([string] $Name) {
    $delivery = Join-Path $root $Name
    $project = Join-Path $delivery 'a7'
    $source = Join-Path $delivery 'Source'
    Put (Join-Path $delivery 'candidate.unitypackage') 'SYNTHETIC GUARD DATA, NEVER A UNITY PACKAGE'
    $packageSha = Sha (Join-Path $delivery 'candidate.unitypackage')
    Json (Join-Path $delivery 'owner.json') @{
        purpose='YLFramework-Stage9-independent-delivery'; root=$delivery; repository=$source
    }
    Json (Join-Path $project 'stage9-owner.json') @{
        Purpose='Stage9-independent-receiver'; Project=$project; Delivery=$delivery
        UnityVersion='6000.3.27f1'; UguiVersion='2.0.0'; Package='candidate.unitypackage'; PackageSha256=$packageSha
    }
    Put (Join-Path $project 'ProjectSettings/ProjectVersion.txt') "m_EditorVersion: 6000.3.27f1`n"
    Json (Join-Path $project 'Packages/manifest.json') @{ dependencies=@{
        'com.unity.ugui'='2.0.0'; 'com.unity.nuget.newtonsoft-json'='3.2.1'; 'com.unity.modules.audio'='1.0.0'
    } }
    Put (Join-Path $project 'Assets/YLFramework/Runtime/Product.cs') '// synthetic product'
    Put (Join-Path $project 'Assets/YLFramework/Runtime/Product.cs.meta') "guid: 11111111111111111111111111111111`n"
    $product = @(@{ Path='Assets/YLFramework/Runtime/Product.cs'; Folder=$false
        Sha256=(Sha (Join-Path $project 'Assets/YLFramework/Runtime/Product.cs'))
        MetaSha256=(Sha (Join-Path $project 'Assets/YLFramework/Runtime/Product.cs.meta')) })
    Json (Join-Path $delivery 'candidate.unitypackage.files.json') $product
    Json (Join-Path $delivery 'candidate.unitypackage.receipt.json') @{
        Passed=$true; Package='candidate.unitypackage'; Sha256=$packageSha
    }
    $exe = Join-Path $delivery 'metadata-only/Unity.exe'
    foreach ($stage in @('create','import-initial')) {
        $log = Join-Path $delivery ('a7-' + $stage + '.log')
        Put $log "Initialize engine version: 6000.3.27f1 (672d562a42a7)`n"
        $arguments = if ($stage -ceq 'create') { @('-batchmode','-quit','-createProject',$project,'-logFile',$log) }
        else { @('-batchmode','-quit','-projectPath',$project,'-buildTarget','Win64','-importPackage',
            (Join-Path $delivery 'candidate.unitypackage'),'-logFile',$log) }
        Json ($log + '.process.json') @{
            Project=$project; Executable=$exe; ExpectedUnityVersion='6000.3.27f1'; UguiVersion='2.0.0'
            Package='candidate.unitypackage'; PackageSha256=$packageSha; Pid=123; Arguments=$arguments
            StartedUtc=$(if ($stage -ceq 'create') { '2026-10-28T00:00:00Z' } else { '2026-10-28T00:00:02Z' })
        }
        Json ($log + '.exit.json') @{
            Project=$project; Pid=123; ExitCode=0; TimedOut=$false; ForcedExit=$false; ExitStalled=$false
            EndedUtc=$(if ($stage -ceq 'create') { '2026-10-28T00:00:01Z' } else { '2026-10-28T00:00:03Z' })
        }
    }
    Json (Join-Path $delivery 'a7-first-import-initial.json') @{
        Passed=$true; HarnessPresent=$false; Project=$project; UnityVersion='6000.3.27f1'; UguiVersion='2.0.0'
        Package='candidate.unitypackage'; PackageSha256=$packageSha; Errors=@()
        Log=(Join-Path $delivery 'a7-import-initial.log')
    }
    foreach ($relative in @('Tests/Acceptance/Core/AudioAcceptanceEntry.cs',
        'Tests/Acceptance/Core/AudioAcceptanceEntry.cs.meta','Tests/Fixtures/Audio/AudioMixerFixture.mixer',
        'Tests/Fixtures/Audio/AudioMixerFixture.mixer.meta','Tests/Tools/B05AudioAcceptanceBuild.cs.txt')) {
        $target = Join-Path $source ('Assets/YLFramework/' + $relative)
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        [IO.File]::Copy((Join-Path $repository ('Assets/YLFramework/' + $relative)), $target, $false)
    }
    return @{
        Policy=@{ Delivery=$delivery; Repository=$source; Package='candidate.unitypackage'
            PackageSha256=$packageSha; UnityExe=$exe }
        Receiver='a7'; Run='guard1'; OwnerSha256=(Sha (Join-Path $project 'stage9-owner.json'))
    }
}
function Reject([hashtable] $Arguments, [string] $Reason) {
    $failure = $null
    try { $null = Install-B05Fixture @Arguments } catch { $failure = $_.Exception.Message }
    Assert ($null -ne $failure -and $failure -match $Reason) "Expected /$Reason/; actual: $failure"
    Assert (-not [IO.Path]::Exists((Join-Path $Arguments.Policy.Delivery 'a7/Assets/B05AudioAcceptance'))) 'Rejection wrote fixture.'
    Assert (@([IO.Directory]::GetDirectories($Arguments.Policy.Delivery, '.b05-*', [IO.SearchOption]::AllDirectories)).Count -eq 0) 'Rejection wrote staging.'
}
foreach ($scenario in @(
    'wrong-owner','wrong-owner-sha','Source','occupied','occupied-meta','locked','unknown-assets','git',
    'missing-source','wrong-entry-meta','missing-first','wrong-first-package','harness-first','forced-exit',
    'failed-create','wrong-executable','wrong-arguments','missing-log','changed-product','path-traversal','link',
    'unknown-empty','input-installed','tests-installed','missing-mixer','linked-source','wrong-order'
)) {
    Case $scenario {
        $a = Fixture $scenario
        $d = $a.Policy.Delivery
        $p = Join-Path $d 'a7'
        $reason = ''
        switch -CaseSensitive ($scenario) {
            'wrong-owner' { Change (Join-Path $d 'owner.json') { param($j) $j.purpose='unknown' }; $reason='delivery owner' }
            'wrong-owner-sha' { $a.OwnerSha256='0'*64; $reason='owner SHA' }
            'Source' { $a.Policy.Repository=$p; $reason='Source|repository' }
            'occupied' { $null=[IO.Directory]::CreateDirectory((Join-Path $p 'Assets/B05AudioAcceptance')); $reason='existing|occupied' }
            'occupied-meta' { Put (Join-Path $p 'Assets/B05AudioAcceptance.meta') 'foreign'; $reason='existing|occupied' }
            'locked' { Put (Join-Path $p 'Temp/UnityLockfile') 'occupied'; $reason='occupied|UnityLockfile' }
            'unknown-assets' { Put (Join-Path $p 'Assets/Unknown.txt') 'foreign'; $reason='Unknown|unknown' }
            'git' { Put (Join-Path $p '.git') 'gitdir: elsewhere'; $reason='git|Source' }
            'missing-source' { [IO.File]::Delete((Join-Path $a.Policy.Repository 'Assets/YLFramework/Tests/Acceptance/Core/AudioAcceptanceEntry.cs')); $reason='source|Source' }
            'wrong-entry-meta' { Put (Join-Path $a.Policy.Repository 'Assets/YLFramework/Tests/Acceptance/Core/AudioAcceptanceEntry.cs.meta') 'guid: 00000000000000000000000000000000'; $reason='GUID|meta' }
            'missing-first' { [IO.File]::Delete((Join-Path $d 'a7-first-import-initial.json')); $reason='first|proof|missing' }
            'wrong-first-package' { Change (Join-Path $d 'a7-first-import-initial.json') { param($j) $j.PackageSha256='0'*64 }; $reason='first|package' }
            'harness-first' { Change (Join-Path $d 'a7-first-import-initial.json') { param($j) $j.HarnessPresent=$true }; $reason='first|bare' }
            'forced-exit' { Change (Join-Path $d 'a7-import-initial.log.exit.json') { param($j) $j.ForcedExit=$true }; $reason='exit' }
            'failed-create' { Change (Join-Path $d 'a7-create.log.exit.json') { param($j) $j.ExitCode=1 }; $reason='exit' }
            'wrong-executable' { Change (Join-Path $d 'a7-create.log.process.json') { param($j) $j.Executable='unknown.exe' }; $reason='process|executable' }
            'wrong-arguments' { Change (Join-Path $d 'a7-import-initial.log.process.json') { param($j) $j.Arguments += @('-executeMethod','Foreign.Run') }; $reason='arguments|bare' }
            'missing-log' { [IO.File]::Delete((Join-Path $d 'a7-create.log')); $reason='missing|log' }
            'changed-product' { Put (Join-Path $p 'Assets/YLFramework/Runtime/Product.cs') 'changed'; $reason='product|Product' }
            'path-traversal' { $a.Receiver='../Source'; $reason='Receiver|receiver' }
            'unknown-empty' { $null=[IO.Directory]::CreateDirectory((Join-Path $p 'Assets/Unknown')); $reason='Unknown|unknown' }
            'input-installed' {
                Change (Join-Path $p 'Packages/manifest.json') { param($j) $j.dependencies['com.unity.inputsystem']='1.20.1' }
                $reason='dependency|dependencies|package'
            }
            'tests-installed' {
                Change (Join-Path $p 'Packages/manifest.json') { param($j) $j.dependencies['com.unity.test-framework']='1.6.0' }
                $reason='dependency|dependencies|package'
            }
            'missing-mixer' {
                [IO.File]::Delete((Join-Path $a.Policy.Repository 'Assets/YLFramework/Tests/Fixtures/Audio/AudioMixerFixture.mixer'))
                $reason='source|Source'
            }
            'linked-source' {
                [IO.Directory]::Move($a.Policy.Repository, ($a.Policy.Repository + '-real'))
                $null=New-Item -ItemType Junction -Path $a.Policy.Repository -Target ($a.Policy.Repository + '-real')
                $reason='[Ll]ink'
            }
            'wrong-order' {
                Change (Join-Path $d 'a7-import-initial.log.process.json') { param($j) $j.StartedUtc='2026-10-27T23:59:59Z' }
                $reason='order|Create.*Import'
            }
            'link' {
                $link = Join-Path $p 'Assets/Linked'
                $null = New-Item -ItemType Junction -Path $link -Target (Join-Path $a.Policy.Repository 'Assets')
                $reason='[Ll]ink'
            }
        }
        if ($scenario -ceq 'occupied') {
            $failure=$null
            try { $null=Install-B05Fixture @a } catch { $failure=$_.Exception.Message }
            Assert ($null -ne $failure -and $failure -match $reason) "Expected occupied rejection; actual: $failure"
            Assert ([IO.Directory]::GetFileSystemEntries((Join-Path $p 'Assets/B05AudioAcceptance')).Count -eq 0) 'Adopted occupied folder.'
        } else { Reject $a $reason }
    }
}
Case 'importer-created required parent folder metadata is preserved' {
    $a = Fixture 'parent-folder-meta'
    $meta = Join-Path $a.Policy.Delivery 'a7/Assets/YLFramework/Runtime.meta'
    Put $meta "fileFormatVersion: 2`nguid: 22222222222222222222222222222222`nfolderAsset: yes`n"
    $before = Sha $meta
    $null = Install-B05Fixture @a
    Assert ((Sha $meta) -ceq $before) 'Independent imported parent identity changed.'
}
Case 'required parent metadata is not a permit for malformed identity' {
    $a = Fixture 'invalid-parent-meta'
    Put (Join-Path $a.Policy.Delivery 'a7/Assets/YLFramework/Runtime.meta') 'foreign non-folder metadata'
    Reject $a 'parent|folder|metadata|Unknown'
}
Case 'minimal install needs no SDK, native Installer, input package or test macro' {
    $a = Fixture 'minimal-install'
    $result = Install-B05Fixture @a
    $destination = Join-Path $a.Policy.Delivery 'a7/Assets/B05AudioAcceptance'
    $receipt = [IO.File]::ReadAllText((Join-Path $destination 'InstallReceipt.json')) | ConvertFrom-Json
    Assert (-not $receipt.RuntimePassed -and -not $receipt.InputInjected -and -not $receipt.SourceProjectTested) 'Installation claimed runtime results.'
    Assert ($receipt.OwnerSha256 -ceq $a.OwnerSha256) 'Owner was not pinned.'
    foreach ($row in $receipt.Inputs) {
        Assert ((Sha (Join-Path $destination $row.Target)) -ceq $row.Sha256) 'Installed bytes differ.'
        Assert ((Sha $row.Source) -ceq $row.Sha256) 'Original source bytes differ.'
    }
    $asmdef = [IO.File]::ReadAllText((Join-Path $destination 'Runtime/YLFramework.B05Audio.Fixture.asmdef')) | ConvertFrom-Json
    Assert ($asmdef.references.Count -eq 1 -and $asmdef.references[0] -ceq 'YLFramework.Runtime') 'Wrong dependency direction.'
    Assert ($asmdef.autoReferenced -and $asmdef.overrideReferences -and $asmdef.precompiledReferences.Count -eq 0 -and
        $asmdef.defineConstraints.Count -eq 0 -and $asmdef.versionDefines.Count -eq 0) 'Fixture acquired an optional macro/SDK dependency.'
    Assert ([IO.File]::Exists((Join-Path $destination 'Editor/B05AudioAcceptanceBuild.cs'))) 'Editor is not a Runtime sibling.'
    Assert (-not [IO.File]::Exists((Join-Path $destination 'Runtime/Editor/B05AudioAcceptanceBuild.cs'))) 'Editor leaked into fixture asmdef.'
    Assert (-not [IO.File]::Exists((Join-Path $destination 'Runtime/YLFramework.B05Audio.Fixture.asmdef.meta'))) 'Installer invented new metadata.'
    $before = Sha (Join-Path $destination 'InstallReceipt.json')
    $failure=$null
    try { $null=Install-B05Fixture @a } catch { $failure=$_.Exception.Message }
    Assert ($failure -match 'existing|occupied') 'Repeated installation overwrote ownership.'
    Assert ((Sha (Join-Path $destination 'InstallReceipt.json')) -ceq $before) 'Receipt was overwritten.'
}
Case 'public entry cannot redirect installation to synthetic delivery' {
    if ($null -eq $ast) { throw 'Missing installer AST.' }
    $a = Fixture 'public-pin'
    $failure=$null
    try { & $entry -DeliveryRoot $a.Policy.Delivery -Receiver a7 -Run guard1 -OwnerSha256 $a.OwnerSha256 }
    catch { $failure=$_.Exception.Message }
    Assert ($failure -match 'exact delivery|pinned delivery') "Missing production delivery pin: $failure"
    Assert (-not [IO.Path]::Exists((Join-Path $a.Policy.Delivery 'a7/Assets/B05AudioAcceptance'))) 'Public pin was bypassed.'
}
Case 'only the three new files: UTF8 no BOM, LF, final newline, no trailing whitespace; PS AST' {
    foreach ($path in @($entry, $template, $PSCommandPath)) {
        $bytes = [IO.File]::ReadAllBytes($path)
        $text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
        Assert (-not ($bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191)) 'Unexpected BOM.'
        Assert (-not $text.Contains("`r") -and $text.EndsWith("`n") -and -not $text.EndsWith("`n`n")) 'Expected LF and single final newline.'
        Assert (-not [regex]::IsMatch($text, '(?m)[\t ]+$')) "Trailing whitespace: $path"
        if ($path.EndsWith('.ps1')) {
            $tokens=$null
            $errors=$null
            $null=[Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
            Assert ($errors.Count -eq 0) ($errors -join "`n")
        }
    }
}
if ($CheckCSharp) {
    Case 'CSharp9 fixture then Editor: real refs and assembly boundary, no emit or Unity execution' {
        $data = Join-Path $EditorRoot 'Data'
        $framework = @([IO.Directory]::GetFiles((Join-Path $data 'NetStandard/ref/2.1.0'), '*.dll')) +
            @([IO.Directory]::GetFiles((Join-Path $data 'NetStandard/compat/2.1.0/shims/netstandard'), '*.dll'))
        $modules = @([IO.Directory]::GetFiles((Join-Path $data 'Managed/UnityEngine'), '*.dll'))
        $refs = $framework + $modules +
            @((Join-Path $data 'Managed/UnityEditor.dll'), (Join-Path $data 'Managed/UnityEngine.dll'),
                (Join-Path $data 'Managed/Newtonsoft.Json.dll'), $RuntimeAssembly)
        Add-Type -AssemblyName Microsoft.CodeAnalysis.CSharp
        $parse = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default.WithLanguageVersion(
            [Microsoft.CodeAnalysis.CSharp.LanguageVersion]::CSharp9)
        $references = [Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
        foreach ($path in @($refs | Sort-Object -Unique)) {
            $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($path))
        }
        $options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
        $runtimeRefs = [Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
        foreach ($path in @($framework + @($modules | Where-Object { [IO.Path]::GetFileName($_).StartsWith('UnityEngine') }) +
            @((Join-Path $data 'Managed/UnityEngine.dll'), $RuntimeAssembly) | Sort-Object -Unique)) {
            $runtimeRefs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($path))
        }
        $entrySource = Join-Path $repository 'Assets/YLFramework/Tests/Acceptance/Core/AudioAcceptanceEntry.cs'
        $runtimeTrees = [Microsoft.CodeAnalysis.SyntaxTree[]]@(
            [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($entrySource), $parse, $entrySource))
        $fixture = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('YLFramework.B05Audio.Fixture', $runtimeTrees, $runtimeRefs, $options)
        $diagnostics = @($fixture.GetDiagnostics() | Where-Object Severity -EQ Error)
        Assert ($diagnostics.Count -eq 0) ($diagnostics -join "`n")
        $references.Add($fixture.ToMetadataReference())
        $editorTrees = [Microsoft.CodeAnalysis.SyntaxTree[]]@(
            [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($template), $parse, $template))
        $compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('Assembly-CSharp-Editor', $editorTrees, $references, $options)
        $diagnostics = @($compilation.GetDiagnostics() | Where-Object Severity -EQ Error)
        Assert ($diagnostics.Count -eq 0) ($diagnostics -join "`n")
        "CSharp9 fixture/Editor semantic errors=0; real Unity 6000.3 refs; Runtime ref SHA=$(Sha $RuntimeAssembly); no emit or Unity execution."
    }
}
$summary = @{ PureGuards=$true; UnityExecuted=$false; RuntimePassed=$false; InputInjected=$false
    SourceProjectTested=$false; Cases=$rows.Count; Failed=@($rows | Where-Object { -not $_.Passed }).Count; Results=$rows }
$summary | ConvertTo-Json -Depth 8
if ($summary.Failed) { throw "B05 offline guards failed: $($summary.Failed)/$($summary.Cases)." }
