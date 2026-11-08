#requires -Version 7.6
[CmdletBinding()]
param([Parameter(Mandatory)][string] $TempRoot,[string]$SourceManifest)
Set-StrictMode -Version 3.0
$ErrorActionPreference='Stop'
$fixtureSourceManifest=$SourceManifest
if (-not (Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($TempRoot))) -or (Test-Path -LiteralPath $TempRoot)) { throw 'A new test root under an existing parent is required.' }
[IO.Directory]::CreateDirectory($TempRoot) | Out-Null
$implementation=Join-Path $PSScriptRoot 'Prepare-HybridCLRNativeSource.ps1'
$loaded=Test-Path -LiteralPath $implementation
if ($loaded) { . $implementation }
$cases=[Collections.Generic.List[object]]::new()
$tokens=$null; $parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($implementation,[ref]$tokens,[ref]$parseErrors)
$paths=@($ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -cin @('WorkspaceRoot','Il2CppRepository','HybridClrRepository') })
$cases.Add(@{ Name='native-source-paths-are-explicit-not-machine-defaults'; Passed=($parseErrors.Count -eq 0 -and $paths.Count -eq 3 -and @($paths | Where-Object DefaultValue).Count -eq 0); ExpectedRejection=$false })
if (@($ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -ceq 'WorkspaceRoot' }).Count -eq 1) {
    $unused=Join-Path $TempRoot 'MustNotBeCreated'
    $rejected=$false
    try { & $implementation -Operation Prepare -Run 'missing-source' -WorkspaceRoot $unused }
    catch { $rejected=$_.Exception.Message.StartsWith('Native source: explicit workspace, exact run and both pinned source repositories are required.',[StringComparison]::Ordinal) }
    $cases.Add(@{ Name='missing-explicit-source-rejects-before-owning-output'; Passed=($rejected -and -not [IO.Path]::Exists($unused)); ExpectedRejection=$true })
}
$fixture=@{ Schema='B08NativeSource-v1'; Variant='ParameterOwner'; Unity='6000.3.27f1'; SdkVersion='9.0.3'
    SdkCommit='6cadd24d101f4cbd00cd80190106f6621132d24d'; Il2CppCommit='6eb38b29c3a7448b3ac6c4dee450de375b5d158d'; HybridClrCommit='bf01f8eed56a077781c3a7b25f418603c7645588'
    SourceDirectory=(Join-Path $TempRoot 'Patched/libil2cpp'); BeforeCrLfSha256='6c2902bfcbdbaf253486d5af13c0e2b1e372af5c5a934a74c1a172fe8f2b823f'
    AfterCrLfSha256='069213cf9fb4c5a07033a8d0bba25d53b4b986b6ba3eba98185e8c1e444bf4a1'; ChangedFiles=@('vm/GlobalMetadata.cpp'); PatchSha256=('a'*64)
    Files=@(@{ Path='vm/GlobalMetadata.cpp'; Length=10; Sha256=('b'*64) }); Licenses=@('MIT-il2cpp_plus','MIT-hybridclr') }
foreach ($spec in @(
    @{ Name='exact-parameter-owner-source'; Reject=$false; Change={} },
    @{ Name='explicit-unmodified-control'; Reject=$false; Change={param($m) $m.Variant='UnmodifiedControl'; $m.AfterCrLfSha256=$m.BeforeCrLfSha256; $m.ChangedFiles=@()} },
    @{ Name='unmodified-control-cannot-hide-a-patch'; Reject=$true; Change={param($m) $m.Variant='UnmodifiedControl'; $m.AfterCrLfSha256=$m.BeforeCrLfSha256} },
    @{ Name='foreign-sdk-version'; Reject=$true; Change={param($m) $m.SdkVersion='9.0.4'} },
    @{ Name='foreign-sdk-commit'; Reject=$true; Change={param($m) $m.SdkCommit='1'*40} },
    @{ Name='foreign-il2cpp-commit'; Reject=$true; Change={param($m) $m.Il2CppCommit='1'*40} },
    @{ Name='foreign-interpreter-commit'; Reject=$true; Change={param($m) $m.HybridClrCommit='1'*40} },
    @{ Name='unapproved-engine'; Reject=$true; Change={param($m) $m.Unity='2022.3.62f3c1'} },
    @{ Name='unknown-variant'; Reject=$true; Change={param($m) $m.Variant='OfficialUnmodified'} },
    @{ Name='wrong-preimage'; Reject=$true; Change={param($m) $m.BeforeCrLfSha256='c'*64} },
    @{ Name='wrong-postimage'; Reject=$true; Change={param($m) $m.AfterCrLfSha256='c'*64} },
    @{ Name='extra-native-change'; Reject=$true; Change={param($m) $m.ChangedFiles+= 'vm/Method.cpp'} },
    @{ Name='source-in-frozen-library'; Reject=$true; Change={param($m) $m.SourceDirectory=Join-Path $TempRoot 'Library/HybridCLR/libil2cpp'} },
    @{ Name='traversal-file'; Reject=$true; Change={param($m) $m.Files[0].Path='../outside.cpp'} },
    @{ Name='missing-license'; Reject=$true; Change={param($m) $m.Licenses=@('MIT-il2cpp_plus')} }
)) {
    $failure=$null
    try {
        if (-not $loaded) { throw 'Native source implementation is missing.' }
        $manifest=ConvertFrom-Json ($fixture | ConvertTo-Json -Depth 8) -AsHashtable
        & $spec.Change $manifest
        Assert-HybridCLRNativeSourceManifest $manifest
    } catch { $failure=$_.Exception.Message }
    $acceptedRejection=$null -ne $failure -and $failure.StartsWith('Native source:',[StringComparison]::Ordinal)
    $passed=if ($spec.Reject) { $acceptedRejection } else { $null -eq $failure }
    $cases.Add(@{ Name=$spec.Name; Passed=$passed; ExpectedRejection=$spec.Reject; Failure=$failure })
}
if ($fixtureSourceManifest) {
    $real=[IO.File]::ReadAllText($fixtureSourceManifest) | ConvertFrom-Json -AsHashtable
    $realParent=[IO.Path]::GetDirectoryName($fixtureSourceManifest)
    foreach ($spec in @(
        @{ Name='actual-approved-postimage'; Reject=$false; Mutation='none'; Variant='ParameterOwner' },
        @{ Name='self-consistent-files-do-not-prove-a-patch'; Reject=$true; Mutation='unpatched'; Variant='ParameterOwner' },
        @{ Name='hidden-extra-native-change'; Reject=$true; Mutation='extra'; Variant='ParameterOwner' },
        @{ Name='forged-before-image'; Reject=$true; Mutation='preimage'; Variant='ParameterOwner' },
        @{ Name='different-upstream-path-set'; Reject=$true; Mutation='path-set'; Variant='ParameterOwner' },
        @{ Name='control-really-has-no-patch'; Reject=$false; Mutation='unpatched'; Variant='UnmodifiedControl' },
        @{ Name='claimed-control-actually-patched'; Reject=$true; Mutation='none'; Variant='UnmodifiedControl' }
    )) {
        $caseRoot=Join-Path $TempRoot $spec.Name
        $before=Join-Path $caseRoot 'il2cpp_plus/libil2cpp'
        $after=Join-Path $caseRoot 'Patched/libil2cpp'
        [IO.Directory]::CreateDirectory((Join-Path $before 'vm')) | Out-Null
        [IO.Directory]::CreateDirectory((Join-Path $after 'vm')) | Out-Null
        Copy-Item -LiteralPath (Join-Path $realParent 'il2cpp_plus/libil2cpp/vm/GlobalMetadata.cpp') -Destination (Join-Path $before 'vm/GlobalMetadata.cpp')
        Copy-Item -LiteralPath (Join-Path $real.SourceDirectory 'vm/GlobalMetadata.cpp') -Destination (Join-Path $after 'vm/GlobalMetadata.cpp')
        foreach ($directory in @($before,$after)) { [IO.File]::WriteAllText((Join-Path $directory 'vm/Fixture.cpp'),'// immutable fixture baseline',[Text.UTF8Encoding]::new($false)) }
        switch ($spec.Mutation) {
            unpatched { [IO.File]::Copy((Join-Path $before 'vm/GlobalMetadata.cpp'),(Join-Path $after 'vm/GlobalMetadata.cpp'),$true) }
            extra { [IO.File]::WriteAllText((Join-Path $after 'vm/Fixture.cpp'),'// unauthorized additional native change',[Text.UTF8Encoding]::new($false)) }
            preimage { [IO.File]::AppendAllText((Join-Path $before 'vm/GlobalMetadata.cpp'),"`n// changed reference image",[Text.UTF8Encoding]::new($false)) }
            'path-set' { [IO.File]::WriteAllText((Join-Path $before 'vm/Unexpected.cpp'),'// omitted source path',[Text.UTF8Encoding]::new($false)) }
        }
        $manifest=ConvertFrom-Json ($real | ConvertTo-Json -Depth 9) -AsHashtable
        $manifest.Variant=$spec.Variant; $manifest.SourceDirectory=$after
        $manifest.Files=Get-NativeSourceFiles $after
        if ($spec.Variant -ceq 'UnmodifiedControl') { $manifest.AfterCrLfSha256=$manifest.BeforeCrLfSha256; $manifest.ChangedFiles=@() }
        $path=Join-Path $caseRoot 'native-source.json'
        [IO.File]::WriteAllText($path,($manifest | ConvertTo-Json -Depth 9),[Text.UTF8Encoding]::new($false))
        $failure=$null
        try { $verified=& $implementation -Operation Verify -SourceManifest $path; if ($verified.Verified -ne $true) { throw 'Missing positive source verification.' } }
        catch { $failure=$_.Exception.Message }
        $passed=if ($spec.Reject) { $null -ne $failure -and $failure.StartsWith('Native source:',[StringComparison]::Ordinal) } else { $null -eq $failure }
        $cases.Add(@{ Name=$spec.Name; Passed=$passed; ExpectedRejection=$spec.Reject; Failure=$failure })
    }
}
$result=@{ Passed=@($cases | Where-Object { -not $_.Passed }).Count -eq 0; Cases=$cases.ToArray(); ActualSourceTreesTested=[bool]$fixtureSourceManifest; UnityExecuted=$false; NativeExecuted=$false }
[IO.File]::WriteAllText((Join-Path $TempRoot 'results.json'),($result | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
if (-not $result.Passed) { throw "Native source guards failed; $TempRoot/results.json" }
"Passed $($cases.Count) isolated native-source guards; $TempRoot/results.json"
