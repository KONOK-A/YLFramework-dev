#requires -Version 7.6
[CmdletBinding()]
param([string] $TargetsPath, [ValidatePattern('^[a-zA-Z0-9_-]+$')][string] $Run)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

function Confirm-ResourceCompatibility([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw ('Compatibility: ' + $Message) }
}
function Confirm-ResourceCompatibilityZero($Values, [string] $Field) {
    Confirm-ResourceCompatibility ($Values -is [Collections.IDictionary] -and $Values.ContainsKey($Field) -and
        ($Values[$Field] -is [long] -or $Values[$Field] -is [int]) -and $Values[$Field] -eq 0) ('cleanup numeric counter '+$Field)
}
function Read-ResourceCompatibilityJson([string] $Path) {
    Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable
}
function Get-ResourceCompatibilityPath([string] $Root, [string] $Relative) {
    Confirm-ResourceCompatibility (-not [IO.Path]::IsPathRooted($Relative) -and $Relative -notmatch '(^|[\\/])\.{1,2}([\\/]|$)') 'path must be relative and contained'
    $parent = [IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    $path = [IO.Path]::GetFullPath((Join-Path $parent $Relative))
    Confirm-ResourceCompatibility ($path.StartsWith($parent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) 'path escaped its root'
    for ($item=$path; $item; $item=[IO.Path]::GetDirectoryName($item)) {
        if ([IO.File]::Exists($item) -or [IO.Directory]::Exists($item)) {
            Confirm-ResourceCompatibility (([IO.File]::GetAttributes($item) -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'path contains a link'
        }
    }
    return $path
}
function Get-ResourceCompatibilityHash([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Assert-ResourceCompatibilityFiles([string] $Root, $Files, [string] $PathField = 'Path', [switch] $Exact, [string[]] $AllowedExtra = @()) {
    Confirm-ResourceCompatibility (@($Files).Count -gt 0) 'artifact list is empty'
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $Files) {
        $relative = [string]$file[$PathField]
        Confirm-ResourceCompatibility ($relative -cne '' -and $seen.Add($relative.Replace('\','/'))) 'artifact path missing or duplicated'
        $path = Get-ResourceCompatibilityPath $Root $relative
        Confirm-ResourceCompatibility ((Test-Path -LiteralPath $path -PathType Leaf) -and ([IO.FileInfo]$path).Length -eq $file.Length -and
            (Get-ResourceCompatibilityHash $path) -ceq $file.Sha256) ('artifact bytes differ: ' + $relative)
    }
    if ($Exact) {
        $actual=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $pending=[Collections.Generic.Stack[string]]::new()
        $pending.Push([IO.Path]::GetFullPath($Root))
        while ($pending.Count) {
            foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($pending.Pop())) {
                $attributes=[IO.File]::GetAttributes($entry)
                Confirm-ResourceCompatibility (($attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'artifact closure contains a link'
                if ($attributes -band [IO.FileAttributes]::Directory) { $pending.Push($entry) }
                else {
                    $relative=[IO.Path]::GetRelativePath($Root,$entry).Replace('\','/')
                    if ($relative -cnotin $AllowedExtra) { $null=$actual.Add($relative) }
                }
            }
        }
        Confirm-ResourceCompatibility ($actual.SetEquals($seen)) 'artifact closure differs from its complete manifest'
    }
}
function Assert-ResourceCompatibilityTargets($Targets) {
    Confirm-ResourceCompatibility ($Targets.SchemaVersion -eq 1) 'target schema'
    Confirm-ResourceCompatibility ($Targets.UnityVersion -cin @('2022.3.62f3c1','6000.3.27f1')) 'unity must be an exact fixed target'
    $roles = @('MonoDevelopment','Il2CppDevelopment','Il2CppRelease')
    Confirm-ResourceCompatibility (@($Targets.Profiles).Count -eq 3 -and @($Targets.Profiles.Role | Sort-Object -Unique).Count -eq 3 -and
        @($roles | Where-Object { $_ -cnotin $Targets.Profiles.Role }).Count -eq 0) 'profiles must contain the three distinct required roles'
    Confirm-ResourceCompatibility ($Targets.SourceIdentitySha256 -cmatch '^[a-f0-9]{64}$') 'source identity hash'
    foreach ($name in @('EvidenceRoot','Carrier','Repository')) {
        Confirm-ResourceCompatibility ([IO.Path]::IsPathFullyQualified([string]$Targets[$name])) 'root identity must be absolute'
    }
    foreach ($profile in $Targets.Profiles) {
        $development = $profile.Role -cne 'Il2CppRelease'
        $backend = if ($profile.Role -ceq 'MonoDevelopment') { 'Mono2x' } else { 'IL2CPP' }
        Confirm-ResourceCompatibility ($profile.Development -is [bool] -and $profile.CandidateValidation -is [bool] -and
            $profile.Development -eq $development -and $profile.Backend -ceq $backend -and
            (-not $profile.CandidateValidation -or ($development -and $backend -ceq 'IL2CPP'))) 'profile policy'
        Confirm-ResourceCompatibility ($profile.Build -cmatch '^[a-zA-Z0-9_-]+$' -and $profile.BuildGuid -cmatch '^[a-f0-9]{32}$' -and
            $profile.BuildReportSha256 -cmatch '^[a-f0-9]{64}$') 'build identity declaration'
        $names = if ($development) { @('Seed','Prefix','StorePrefix','ResumePrefix','ResumeStorePrefix') } else { @('NonePrefix','EncryptedPrefix') }
        foreach ($name in $names) { Confirm-ResourceCompatibility ($profile.Runs[$name] -cmatch '^[a-zA-Z0-9_-]+$') 'run declaration' }
    }
    Confirm-ResourceCompatibility (-not $Targets.ContainsKey('ObservationExceptions')) 'observation exceptions are not allowed'
}

function New-ResourceCompatibilityCase($Profile, [string] $Id, [hashtable] $Changes) {
    $row = @{ Id=$Id; Role=$Profile.Role; Command='probe'; Store=''; Mode='none'; Expect=1; Target=2; Key='valid'; Trust='pin'; Remote=$Profile.Development;
        ErrorKind=''; Reason=''; Fault=''; Checkpoint=''; Http=0;
        RejectionText=''; Floor=$null; ResumeCase=''; ResumePhase=''; CorruptActive=$false }
    foreach ($key in $Changes.Keys) {
        Confirm-ResourceCompatibility ($row.ContainsKey($key)) ('unknown case field: ' + $key)
        $row[$key] = $Changes[$key]
    }
    if ($row.Command -cin @('remote-reject','remote-corrupt')) { $row.ErrorKind='UpdateFailed' }
    $row.Kill = $row.Command -ceq 'remote-kill-partial' -or $row.Fault -ceq 'kill'
    return $row
}
function Get-ResourceCompatibilityCases($Targets) {
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($p in $Targets.Profiles) {
        if ($p.Development) {
            $prefix=$p.Runs.Prefix; $store=$p.Runs.StorePrefix
            $rows.Add((New-ResourceCompatibilityCase $p $p.Runs.Seed @{Command='remote'; Store=($store+'remote-main'); ErrorKind=''; Fault=''; Http=18}))
            $security = @(
                @{Id='active-restart'; Expect=2; Floor=2},
                @{Id='untrusted-tls'; Command='remote-reject'; Store='remote-tls'; Trust='default'; RejectionText='HTTP 0'},
                @{Id='wrong-pin'; Command='remote-reject'; Store='remote-tls'; Trust='wrong'; RejectionText='HTTP 0'},
                @{Id='signature'; Command='remote-reject'; Expect=2; Http=1; RejectionText='Release signature rejected'},
                @{Id='catalog-hash'; Command='remote-reject'; Expect=2; Http=2; RejectionText='Release response digest mismatch'},
                @{Id='redirect'; Command='remote-reject'; Expect=2; Http=1; RejectionText='HTTP 302'},
                @{Id='missing'; Command='remote-reject'; Expect=2; Http=1; RejectionText='HTTP 404'},
                @{Id='encoding'; Command='remote-reject'; Expect=2; Http=1; Reason='Encoded release responses are not accepted.'; RejectionText='Encoded release responses are not accepted'},
                @{Id='stall'; Command='remote-reject'; Expect=2; Http=2; RejectionText='GET timed out'},
                @{Id='rollback'; Command='rollback'; Expect=2; Target=1; Floor=2},
                @{Id='rollback-restart'; Floor=2},
                @{Id='old-sequence'; Command='remote-reject'; Http=1; RejectionText='older than confirmed history'; Floor=2},
                @{Id='conflicting-sequence'; Command='remote-reject'; Http=1; RejectionText='conflicts at the same sequence'; Floor=2},
                @{Id='same-confirmed-release'; Command='remote'; Http=4; Floor=2},
                @{Id='transient-retry'; Command='remote'; Store='remote-retry'; Http=36; Floor=2},
                @{Id='corrupt-body'; Command='remote-corrupt'; Store='remote-corrupt'; Http=6; RejectionText='Snapshot file integrity mismatch'},
                @{Id='corrupt-restart'; Store='remote-corrupt'},
                @{Id='failed-confirmed-floor'; Command='remote-reject'; Expect=2; Http=1; RejectionText='older than confirmed history'; Floor=2},
                @{Id='after-failed-valid'; Command='remote'; Store='remote-corrupt'; Http=17; Floor=2}
            )
            foreach ($item in $security) {
                $id=$item.Id; $changes=@{Store=($store+'remote-main')}
                foreach ($key in $item.Keys) { if ($key -cne 'Id') { $changes[$key]=$item[$key] } }
                if ($item.ContainsKey('Store')) { $changes.Store=$store+$item.Store }
                $rows.Add((New-ResourceCompatibilityCase $p ($prefix+$id) $changes))
            }
            foreach ($name in @('range206','kill','ignore-range','changed-etag','bad-range','weak')) {
                $resumeStore=$p.Runs.ResumeStorePrefix+'remote-r-'+$name
                $partial=if ($name -ceq 'kill') { 'remote-kill-partial' } else { 'remote-partial' }
                $rows.Add((New-ResourceCompatibilityCase $p ($p.Runs.ResumePrefix+$name+'-partial') @{Command=$partial; Store=$resumeStore; Http=6; ResumeCase=$name; ResumePhase='partial'}))
                $rows.Add((New-ResourceCompatibilityCase $p ($p.Runs.ResumePrefix+$name+'-complete') @{Command='remote'; Store=$resumeStore; Http=$(if ($name -ceq 'bad-range') {18} else {17}); ResumeCase=$name; ResumePhase='complete'; Floor=2}))
                $rows.Add((New-ResourceCompatibilityCase $p ($p.Runs.ResumePrefix+$name+'-active-restart') @{Store=$resumeStore; Expect=2; ResumeCase=$name; ResumePhase='restart'; Floor=2}))
            }
            foreach ($item in @(
                @{Id='prepare-only'; Command='remote-prepare'; Store='remote-prepared'; Http=18},
                @{Id='prepared-restart'; Store='remote-prepared'},
                @{Id='prepared-activate'; Command='activate'; Store='remote-prepared'; Floor=2},
                @{Id='prepared-active-restart'; Store='remote-prepared'; Expect=2; Floor=2}
            )) {
                $changes=@{}; foreach ($key in $item.Keys) {if ($key -cne 'Id') {$changes[$key]=$item[$key]}}
                $changes.Store=$store+$item.Store
                $rows.Add((New-ResourceCompatibilityCase $p ($prefix+$item.Id) $changes))
            }
            foreach ($point in @('BeforeCommitDecision','CommitDecided')) {
                $lower=$point.ToLowerInvariant(); $recover=if ($point -ceq 'CommitDecided') {2} else {1}
                $targetStore=$store+'remote-k-'+$lower
                $rows.Add((New-ResourceCompatibilityCase $p ($prefix+$lower+'-kill') @{Command='remote'; Store=$targetStore; Checkpoint=$point; Fault='kill'; Http=18}))
                $rows.Add((New-ResourceCompatibilityCase $p ($prefix+$lower+'-recover') @{Store=$targetStore; Expect=$recover; Floor=$(if ($recover -eq 2) {2} else {0})}))
                if ($recover -eq 1) { $rows.Add((New-ResourceCompatibilityCase $p ($prefix+$lower+'-valid') @{Command='remote'; Store=$targetStore; Http=4; Floor=2})) }
                $rows.Add((New-ResourceCompatibilityCase $p ($prefix+$lower+'-floor') @{Command='remote-reject'; Store=$targetStore; Expect=2; Http=1; Floor=2; RejectionText='older than confirmed history'}))
            }
            foreach ($item in @(
                @{Id='online'; Command='remote'; Http=9; Floor=2}, @{Id='restart'; Expect=2},
                @{Id='missing'; Expect=2; Key='missing'}, @{Id='wrong'; Expect=2; Key='wrong'}, @{Id='valid'; Expect=2}
            )) {
                $changes=@{Mode='encrypted'; Store=($store+'remote-encrypted')}
                foreach ($key in $item.Keys) {if ($key -cne 'Id') {$changes[$key]=$item[$key]}}
                $rows.Add((New-ResourceCompatibilityCase $p ($prefix+'encrypted-'+$item.Id) $changes))
            }
        } else {
            foreach ($mode in @('none','encrypted')) {
                $prefix=if ($mode -ceq 'none') {$p.Runs.NonePrefix} else {$p.Runs.EncryptedPrefix}
                if ($mode -ceq 'encrypted') {
                    foreach ($key in @('missing','wrong')) {
                        $rows.Add((New-ResourceCompatibilityCase $p ($prefix+$key+'-key') @{Mode=$mode; Store='local-encrypted'; Key=$key}))
                    }
                }
                foreach ($item in @(
                    @{Id='cancel'; Command='cancel'}, @{Id='prepare'; Command='prepare'}, @{Id='prepared-restart'},
                    @{Id='busy-activate'; Command='busy'}, @{Id='active-restart'; Expect=2},
                    @{Id='rollback'; Command='rollback'; Expect=2; Target=1}, @{Id='rollback-restart'}
                )) {
                    $changes=@{Mode=$mode; Store=('local-'+$mode)}
                    foreach ($key in $item.Keys) {if ($key -cne 'Id') {$changes[$key]=$item[$key]}}
                    $rows.Add((New-ResourceCompatibilityCase $p ($prefix+$item.Id) $changes))
                }
                if ($mode -ceq 'none') {
                    $rows.Add((New-ResourceCompatibilityCase $p ($prefix+'exception') @{Command='update'; Store='local-none'; Checkpoint='BeforeCommitDecision'; Fault='throw'}))
                    $rows.Add((New-ResourceCompatibilityCase $p ($prefix+'exception-restart') @{Store='local-none'}))
                    foreach ($point in @('JournalVerified','ActiveWritten:r810-a','BeforeCommitDecision','CommitDecided')) {
                        $tag=$point.ToLowerInvariant().Replace(':','-')
                        $rows.Add((New-ResourceCompatibilityCase $p ($prefix+'kill-'+$tag) @{Command='update'; Store='local-none'; Checkpoint=$point; Fault='kill'}))
                        $rows.Add((New-ResourceCompatibilityCase $p ($prefix+'recover-'+$tag) @{Store='local-none'; Expect=$(if ($point -ceq 'CommitDecided') {2} else {1})}))
                    }
                    $rows.Add((New-ResourceCompatibilityCase $p ($prefix+'corrupt-active-lkg') @{Store='local-none'; CorruptActive=$true}))
                } else {
                    foreach ($item in @(
                        @{Id='reactivate'; Command='update'}, @{Id='updated-restart'; Expect=2},
                        @{Id='updated-missing-key'; Expect=2; Key='missing'}, @{Id='updated-wrong-key'; Expect=2; Key='wrong'}, @{Id='updated-valid-key'; Expect=2}
                    )) {
                        $changes=@{Mode=$mode; Store='local-encrypted'}
                        foreach ($key in $item.Keys) {if ($key -cne 'Id') {$changes[$key]=$item[$key]}}
                        $rows.Add((New-ResourceCompatibilityCase $p ($prefix+$item.Id) $changes))
                    }
                }
            }
        }
    }
    Confirm-ResourceCompatibility ($rows.Count -eq 140 -and @($rows | Where-Object Kill).Count -eq 10 -and @($rows.Id | Sort-Object -Unique).Count -eq 140) 'case matrix missing or duplicated'
    return $rows.ToArray()
}

function Assert-ResourceCompatibilityBuildPolicy($Targets, $Profile, $Report) {
    Confirm-ResourceCompatibility ($Report.Result -ceq 'Succeeded' -and $Report.Errors -eq 0 -and $Report.Unity -ceq $Targets.UnityVersion -and
        $Report.Backend -ceq $Profile.Backend -and $Report.Target -ceq 'StandaloneWindows64' -and $Report.BuildGuid -ceq $Profile.BuildGuid -and
        $Report.SourceIdentitySha256 -ceq $Targets.SourceIdentitySha256) 'build policy identity'
    Confirm-ResourceCompatibility ($Report.Development -is [bool] -and $Report.Development -eq $Profile.Development -and
        $Report.CandidateValidation -is [bool] -and $Report.CandidateValidation -eq $Profile.CandidateValidation) 'build policy flags'
    $defines=@(); if ($Profile.Development) {$defines+='YL_RESOURCE_ACCEPTANCE_TESTS'}; if ($Profile.CandidateValidation) {$defines+='YL_RESOURCE_IL2CPP_CANDIDATE'}
    Confirm-ResourceCompatibility ((@($Report.ExtraDefines | Sort-Object) -join ',') -ceq (@($defines | Sort-Object) -join ',')) 'build policy defines'
    $stripping=if ($Profile.Backend -ceq 'IL2CPP') {'Low'} else {'Disabled'}
    Confirm-ResourceCompatibility ($Report.ManagedStripping -ceq $stripping -and
        ($Profile.Backend -cne 'IL2CPP' -or $Report.CppConfiguration -ceq 'Release')) 'build policy stripping'
}
function Get-ResourceCompatibilityRuntimeMetadata([string] $Path) {
    $input=[IO.File]::OpenRead($Path); $pe=[Reflection.PortableExecutable.PEReader]::new($input)
    try {
        $reader=[Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        $references=@($reader.AssemblyReferences | ForEach-Object { $reader.GetString($reader.GetAssemblyReference($_).Name) })
        $methods=@($reader.MethodDefinitions | ForEach-Object { $reader.GetString($reader.GetMethodDefinition($_).Name) })
        $types=@($reader.TypeDefinitions | ForEach-Object { $reader.GetString($reader.GetTypeDefinition($_).Name) })
        Confirm-ResourceCompatibility (@($references | Where-Object {
            $_ -cnotmatch '^(?:mscorlib|netstandard|System(?:\.[A-Za-z0-9_]+)*|UnityEngine(?:\.[A-Za-z0-9_]+)*|Unity\.ugui|Microsoft\.CSharp|Microsoft\.Win32\.Primitives|Newtonsoft\.Json|BouncyCastle\.Cryptography)$' -or $_ -match 'TestRunner|Tests|Editor|Analyzer'
        }).Count -eq 0) 'runtime dependency boundary'
        return @{ Mvid=$reader.GetGuid($reader.GetModuleDefinition().Mvid).ToString(); Name=$reader.GetString($reader.GetAssemblyDefinition().Name); Methods=$methods; Types=$types }
    } finally { $pe.Dispose(); $input.Dispose() }
}
function Assert-ResourceCompatibilityBuild($Targets, $Profile) {
    $build=Get-ResourceCompatibilityPath $Targets.Carrier ('Builds/'+$Profile.Build)
    $reportPath=Get-ResourceCompatibilityPath $build 'BuildReport.json'
    Confirm-ResourceCompatibility ((Get-ResourceCompatibilityHash $reportPath) -ceq $Profile.BuildReportSha256) 'build report hash'
    $report=Read-ResourceCompatibilityJson $reportPath
    Assert-ResourceCompatibilityBuildPolicy $Targets $Profile $report
    Assert-ResourceCompatibilityFiles $build $report.PlayerFiles -Exact -AllowedExtra 'BuildReport.json'
    $sourcePath=Get-ResourceCompatibilityPath $Targets.EvidenceRoot ('build-'+$Profile.Build+'-source.json')
    Confirm-ResourceCompatibility ((Get-ResourceCompatibilityHash $sourcePath) -ceq $Targets.SourceIdentitySha256) 'source snapshot hash'
    $source=Read-ResourceCompatibilityJson $sourcePath
    Assert-ResourceCompatibilityFiles $Targets.Carrier $source
    $exit=Read-ResourceCompatibilityJson (Get-ResourceCompatibilityPath $Targets.EvidenceRoot ('Build-'+$Profile.Build+'.exit.json'))
    Confirm-ResourceCompatibility ($exit.ExitCode -eq 0 -and $exit.TimedOut -is [bool] -and -not $exit.TimedOut) 'build process'
    $runtime=Join-Path $build 'ResourceAcceptance_Data/Managed/YLFramework.Runtime.dll'
    $archive=$null
    if ($Profile.Backend -ceq 'IL2CPP') {
        $archive=Get-ResourceCompatibilityPath $Targets.EvidenceRoot ('BuildEvidence/'+$Profile.Build)
        $identity=Read-ResourceCompatibilityJson (Join-Path $archive 'identity.json')
        Confirm-ResourceCompatibility ($identity.Passed -eq $true -and $identity.Unity -ceq $Targets.UnityVersion -and $identity.Backend -ceq 'IL2CPP' -and
            $identity.Build -ceq $Profile.Build -and $identity.ManagedStripping -ceq 'Low' -and $identity.CppConfiguration -ceq 'Release' -and
            $identity.SourceIdentitySha256 -ceq $Targets.SourceIdentitySha256 -and $identity.FormalSourceFiles -eq @($source).Count) 'archive identity'
        Confirm-ResourceCompatibility ((Get-ResourceCompatibilityHash (Join-Path $archive 'BuildReport.json')) -ceq $Profile.BuildReportSha256 -and
            (Get-ResourceCompatibilityHash (Join-Path $archive 'source-identity.json')) -ceq $Targets.SourceIdentitySha256) 'archive input hash'
        Assert-ResourceCompatibilityFiles (Join-Path $archive 'Sources') $source -Exact
        Assert-ResourceCompatibilityFiles (Join-Path $archive 'ManagedStripped') $identity.StrippedFiles 'Name' -Exact
        Assert-ResourceCompatibilityFiles (Join-Path $archive 'GeneratedCpp') $identity.GeneratedCppFiles -Exact
        Assert-ResourceCompatibilityFiles $build $identity.NativeArtifacts
        Confirm-ResourceCompatibility (@($identity.StrippedFiles).Count -eq $identity.StrippedFileCount -and
            @($identity.StrippedFiles | Where-Object Name -Like '*.dll').Count -eq $identity.StrippedAssemblyCount -and
            @($identity.NativeArtifacts).Count -eq 4 -and @($identity.GeneratedCppFiles | Where-Object Path -Like '*.cpp').Count -gt 0) 'archive closure'
        Confirm-ResourceCompatibility ((Get-ResourceCompatibilityHash (Join-Path $archive 'Editor.log')) -ceq
            (Get-ResourceCompatibilityHash (Join-Path $Targets.EvidenceRoot ('Build-'+$Profile.Build+'.log')))) 'archive build log'
        $runtime=Join-Path $archive 'ManagedStripped/YLFramework.Runtime.dll'
    }
    $metadata=Get-ResourceCompatibilityRuntimeMetadata $runtime
    Confirm-ResourceCompatibility ($metadata.Name -ceq 'YLFramework.Runtime' -and $metadata.Types -ccontains 'ResourceRemoteSource') 'runtime metadata'
    $pinCount=@($metadata.Methods | Where-Object { $_ -ceq 'TrustLoopbackCertificate' }).Count
    Confirm-ResourceCompatibility ($pinCount -eq $(if ($Profile.Development) {1} else {0})) 'pin compilation boundary'
    $runtimeIdentity=if ($Profile.Backend -ceq 'Mono2x') {$metadata.Mvid} else {'YLFramework.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null'}
    return @{ Report=$report; Config=(Read-ResourceCompatibilityJson (Join-Path $build 'ResourceAcceptance_Data/StreamingAssets/r810-config.json'));
        RuntimeIdentity=$runtimeIdentity; PlayerFiles=@($report.PlayerFiles).Count; SourceFiles=@($source).Count; Archive=$archive }
}

function Assert-ResourceCompatibilityMounted($Mounted, $Projects, [int] $Version, [string] $Label) {
    Confirm-ResourceCompatibility (@($Mounted).Count -eq @($Projects).Count) ($Label+' cardinality')
    foreach ($project in $Projects) {
        $match=@($Mounted | Where-Object ProjectId -CEQ $project.Id)
        Confirm-ResourceCompatibility ($match.Count -eq 1 -and $match[0].Digest -ceq $project.Digests[$Version-1]) ($Label+' generation')
    }
}
function Assert-ResourceCompatibilityRun($Case, $Context, $Body, $Start, $Exit) {
    Confirm-ResourceCompatibility ($null -ne $Body -and $Body -is [Collections.IDictionary]) 'result missing'
    $targets=$Context.Targets; $profile=$Context.Profile
    $runRoot=Join-Path $targets.EvidenceRoot ('Runs/'+$Case.Id)
    $store=Join-Path $targets.EvidenceRoot ('Stores/'+$Case.Store)
    Confirm-ResourceCompatibility ($Exit.Passed -is [bool] -and $Exit.Passed -and $Exit.TimedOut -is [bool] -and -not $Exit.TimedOut -and
        $Exit.ControlledKill -is [bool] -and $Exit.Result -ceq (Join-Path $runRoot 'result.json')) 'process receipt'
    Confirm-ResourceCompatibility ($Start.Pid -gt 0 -and $Start.Pid -eq $Body.Pid -and $Exit.Pid -eq $Body.Pid) 'pid binding'
    Confirm-ResourceCompatibility ($Start.Executable -ceq (Join-Path $targets.Carrier ('Builds/'+$profile.Build+'/ResourceAcceptance.exe')) -and
        $Start.Backend -ceq $profile.Backend -and $Start.ExpectedUnityVersion -ceq $targets.UnityVersion -and $Start.BuildReportSha256 -ceq $profile.BuildReportSha256) 'build identity'
    Confirm-ResourceCompatibility ($Body.Unity -ceq $targets.UnityVersion -and $Body.Platform -ceq 'WindowsPlayer' -and $Body.Backend -ceq $profile.Backend -and
        $Body.BuildGuid -ceq $profile.BuildGuid -and $Body.RuntimeAssembly -ceq $Context.RuntimeIdentity) 'runtime identity'
    foreach ($key in @('Development','LoopbackPinCompiled','AcceptanceTestsCompiled','CandidateCompiled')) {
        $expected=if ($key -ceq 'CandidateCompiled') {$profile.CandidateValidation} else {$profile.Development}
        Confirm-ResourceCompatibility ($Body[$key] -is [bool] -and $Body[$key] -eq $expected) 'macros'
    }
    Confirm-ResourceCompatibility (-not [string]::IsNullOrWhiteSpace($Body.Graphics) -and $Body.Graphics -cne 'Null' -and
        -not [string]::IsNullOrWhiteSpace($Body.GraphicsDevice) -and $Body.Screen.Width -gt 0 -and $Body.Screen.Height -gt 0 -and
        $Body.AudioContext.SampleRate -gt 0 -and $Body.AudioPlayback -ceq 'ClipLoadedOnly') 'execution context'
    Confirm-ResourceCompatibility ($Body.Command -ceq $Case.Command -and $Body.Mode -ceq $Case.Mode -and $Body.Store -ceq $store) 'command binding'
    $arguments=@('-screen-fullscreen','0','-screen-width','160','-screen-height','120','-logFile',(Join-Path $runRoot 'Player.log'),
        '--r810-out',(Join-Path $runRoot 'result.json'),'--r810-store',$store,'--r810-command',$Case.Command,'--r810-mode',$Case.Mode,
        '--r810-expect',"$($Case.Expect)",'--r810-target',"$($Case.Target)",'--r810-key',$Case.Key,'--r810-trust',$Case.Trust,
        '--r810-remote',$Case.Remote.ToString().ToLowerInvariant(),'--r810-backend',$profile.Backend)
    foreach ($pair in @(@('error','ErrorKind'),@('reason','Reason'),@('checkpoint','Checkpoint'),@('fault','Fault'))) {
        if ($Case[$pair[1]]) { $arguments+=@(('--r810-'+$pair[0]),$Case[$pair[1]]) }
    }
    Confirm-ResourceCompatibility (($arguments -join "`0") -ceq (@($Start.Arguments) -join "`0")) 'arguments differ from the declaration'
    $started=[DateTimeOffset]$Start.StartedUtc; $completed=[DateTimeOffset]$Exit.CompletedUtc
    Confirm-ResourceCompatibility ($completed -ge $started) 'process time order'
    $http=@($Context.Requests)
    Confirm-ResourceCompatibility ($http.Count -eq $Case.Http) 'http request cardinality'
    foreach ($request in $http) {
        $utc=[DateTimeOffset]$request.utc
        Confirm-ResourceCompatibility ($request.run -ceq $Case.Id -and $request.method -ceq 'GET' -and
            $request.url -cmatch '^/r810-[abe]/[a-zA-Z0-9_./-]+$' -and -not $request.url.Contains('..') -and
            $utc -ge $started.AddSeconds(-5) -and $utc -le $completed.AddSeconds(5)) 'http identity/time scope'
    }
    $projects=@($Context.Config[$(if ($Case.Mode -ceq 'encrypted') {'Encrypted'} else {'None'})])
    $keyReject=$Case.Mode -ceq 'encrypted' -and $Case.Key -cne 'valid'
    $steps=[Collections.Generic.List[string]]::new()
    if ($keyReject) { $steps.Add('key-rejected') }
    elseif ($Case.Command -ceq 'remote-partial') { $steps.Add('actual-partial-canceled') }
    elseif ($Case.Command -ceq 'remote-prepare') { $steps.Add('remote-prepared-without-activation') }
    elseif ($Case.Command -cin @('prepare','update','busy')) { $steps.Add('prepared') }
    $consumeVersion=$null
    if (-not $keyReject -and -not $Case.Kill) {
        if ($Case.Fault -ceq 'throw') { $steps.Add('injected-exception-recovered') }
        elseif ($Case.Command -cin @('update','busy','activate','remote')) {
            if ($Case.Command -ceq 'busy') {$steps.Add('busy-keeps-old')}
            $steps.Add('activate-idempotent'); $consumeVersion=$Case.Target
        } elseif ($Case.Command -ceq 'rollback') { $consumeVersion=$Case.Target }
        elseif ($Case.Command -cin @('probe','offline','remote-reject','remote-corrupt')) { $consumeVersion=$Case.Expect }
    }
    if ($null -ne $consumeVersion) { foreach ($project in $projects) {$steps.Add('consume')} }
    if (-not $Case.Kill) { $steps.Add('normal-shutdown-clean') }
    Confirm-ResourceCompatibility ($Body.Steps -is [array] -and (@($Body.Steps | ForEach-Object { $_.Name }) -join ',') -ceq ($steps -join ',')) 'steps missing, sparse or out of order'
    if ($Case.Kill) {
        Confirm-ResourceCompatibility ($Exit.ControlledKill -and $Exit.ExitCode -eq -1 -and -not $Body.ContainsKey('Passed') -and -not $Body.ContainsKey('EndedUtc')) 'kill must not claim a normal result'
        Confirm-ResourceCompatibility ($Context.CheckpointPid -eq $Body.Pid) 'checkpoint marker pid'
    } else {
        Confirm-ResourceCompatibility (-not $Exit.ControlledKill -and $Exit.ExitCode -eq 0) 'normal exit'
        Confirm-ResourceCompatibility ($Body.Passed -is [bool] -and $Body.Passed -and -not $Body.ContainsKey('Failure') -and $Body.ContainsKey('EndedUtc')) 'normal result'
        Confirm-ResourceCompatibility ([DateTimeOffset]$Body.EndedUtc -ge $started -and [DateTimeOffset]$Body.EndedUtc -le $completed) 'normal result time'
        foreach ($field in @('AllocatedBufferBytes','ReservedBufferBytes','LiveInputs','WorkCount','DeclaredNativeReadBufferBytes','ProcessCngProviderCount','ProcessCngKeyCount','ProcessCngKeyObjectBytes','ProcessCngReleaseFailures','RetainedCleanupOwners')) {
            Confirm-ResourceCompatibilityZero $Body.AfterStopContent $field
        }
        foreach ($field in @('ActiveAlgorithms','ActiveKeys','ActiveKeyObjectBytes','ReleaseFailures','RetainedReleaseOwners')) {
            Confirm-ResourceCompatibilityZero $Body.AfterStopProcessCng $field
        }
        Confirm-ResourceCompatibilityZero $Body 'AfterStopBundles'
        if (-not $keyReject) {
            $terminal=$Body.AfterStopDiagnostics
            foreach ($field in @('LeaseCount','ProducerCount','WaiterCount','RunningBundleCount','QueuedBundleCount','RecordCount','BundleRecordCount','BundleRootPinCount','BundleDependencyPinCount')) {
                Confirm-ResourceCompatibilityZero $terminal.Summary $field
            }
            Confirm-ResourceCompatibility ($terminal.Summary.NativeBundleLoadCount -eq $terminal.Summary.NativeBundleUnloadCount) 'cleanup native load/unload'
            foreach ($field in @('Assets','Bundles','InstanceCaches','Scenes')) { Confirm-ResourceCompatibility ($terminal[$field] -is [array] -and @($terminal[$field]).Count -eq 0) ('cleanup '+$field) }
            $final=if ($null -ne $consumeVersion) {$consumeVersion} else {$Case.Expect}
            Assert-ResourceCompatibilityMounted $Body.AfterMounted $projects $final 'final mounted'
            Confirm-ResourceCompatibility (@($Body.AfterDisk.Keys | Where-Object { $_ -like '*commit-journal.json' }).Count -eq 0) 'committed journal must retire'
            foreach ($record in $Body.AfterDisk.GetEnumerator()) {
                if ($record.Key -like '*/active.json') {
                    $active=@($record.Value.Projects | ForEach-Object { @{ProjectId=$_.ProjectId; Digest=$_.CatalogDigest} })
                    Assert-ResourceCompatibilityMounted $active $projects $final 'active control'
                }
            }
            if ($null -ne $Case.Floor) {
                $floors=@($Body.AfterDisk.Values | ForEach-Object {if ($_.ContainsKey('ReleaseFloors')) {$_.ReleaseFloors}})
                if ($Case.Floor -eq 0) { Confirm-ResourceCompatibility ($floors.Count -eq 0) 'unconfirmed floor must not survive' }
                else {
                    Confirm-ResourceCompatibility ($floors.Count -gt 0) 'confirmed floor missing'
                    foreach ($floor in $floors) {
                        $project=@($projects | Where-Object Id -CEQ $floor.ProjectId)
                        Confirm-ResourceCompatibility ($project.Count -eq 1 -and $floor.Sequence -eq 2 -and $floor.CatalogDigest -ceq $project[0].Digests[1]) 'confirmed floor identity'
                    }
                }
            }
        }
    }
    if ($keyReject) {
        $error=if ($Case.Key -ceq 'missing') {'ContentKeyUnavailable'} else {'ContentAuthenticationFailed'}
        Confirm-ResourceCompatibility ($Body.Steps[0].Data.Error -ceq $error -and -not $Body.ContainsKey('Mounted') -and -not $Body.ContainsKey('Rejection') -and $http.Count -eq 0) 'key rejection layer'
    } else {
        Assert-ResourceCompatibilityMounted $Body.Mounted $projects $Case.Expect 'initial mounted'
        if ($Case.Checkpoint) {
            Confirm-ResourceCompatibility ($Body.Checkpoint -ceq $Case.Checkpoint) 'checkpoint label'
            Assert-ResourceCompatibilityMounted $Body.CheckpointMounted $projects $Case.Expect 'checkpoint mounted'
            $journals=@($Body.CheckpointDisk.GetEnumerator() | Where-Object Key -Like '*commit-journal.json')
            Confirm-ResourceCompatibility ($journals.Count -eq 1 -and $journals[0].Value.Phase -ceq $(if ($Case.Checkpoint -ceq 'CommitDecided') {'Committed'} else {'Verified'})) 'checkpoint journal decision'
        }
        if ($Case.Command -cin @('remote-reject','remote-corrupt')) {
            Confirm-ResourceCompatibility (-not [string]::IsNullOrWhiteSpace($Body.Rejection) -and $Body.Rejection.Contains($Case.RejectionText,[StringComparison]::Ordinal) -and
                ($Body.BeforeDisk | ConvertTo-Json -Depth 40 -Compress) -ceq ($Body.AfterDisk | ConvertTo-Json -Depth 40 -Compress)) 'rejection must preserve control state'
        }
        if ($Case.Trust -cin @('default','wrong')) { Confirm-ResourceCompatibility ($Context.PlayerLog.Contains('Curl error 60',[StringComparison]::Ordinal) -and $http.Count -eq 0) 'tls rejection layer' }
        if ($Case.Command -cin @('remote-partial','remote-kill-partial')) {
            Confirm-ResourceCompatibility ($Body.PartialAtCancel.Length -ge 1048576 -and $Body.PartialAtCancel.Path.StartsWith($store+'\',[StringComparison]::OrdinalIgnoreCase)) 'partial evidence'
        }
        if ($Case.Fault -ceq 'throw') { Confirm-ResourceCompatibility ($Body.Steps[1].Data.Error.Contains('r810 owned checkpoint exception',[StringComparison]::Ordinal)) 'injected exception layer' }
        if ($Case.CorruptActive) { Confirm-ResourceCompatibility (@($Body.BeforeDisk.Values | Where-Object { $_.ContainsKey('Malformed') -and $_.ContainsKey('OriginalBase64') }).Count -gt 0) 'corrupt active original bytes'
        }
    }
    if ($null -ne $consumeVersion) {
        $consumed=@($Body.Steps | Where-Object Name -CEQ 'consume')
        foreach ($project in $projects) {
            $item=@($consumed | Where-Object { $_.Data.Project -ceq $project.Id })
            Confirm-ResourceCompatibility ($item.Count -eq 1 -and $item[0].Data.Version -eq $consumeVersion -and $item[0].Data.Digest -ceq $project.Digests[$consumeVersion-1]) 'consume exact generation'
            $content=$item[0].Data.Content
            Confirm-ResourceCompatibility ($Body.AfterStopDiagnostics.Summary.NativeBundleLoadCount -gt 0) 'consume actual native work'
            if ($Case.Mode -ceq 'encrypted') {
                Confirm-ResourceCompatibility ($content.PeakBufferBytes -gt 0 -and $content.PeakBufferBytes -le $content.BufferLimitBytes -and
                    $content.BufferLimitBytes -eq 134217728 -and $content.ProcessCngProviderCount -gt 0 -and $content.ProcessCngKeyCount -gt 0 -and
                    $content.ProcessCngKeyObjectBytes -gt 0 -and $content.DeclaredNativeReadBufferBytes -gt 0 -and $Body.CngNativeModule.EndsWith('\bcrypt.dll',[StringComparison]::OrdinalIgnoreCase)) 'consume encrypted native responsibility'
            }
        }
    }
    $misses=0; $controls=0; $headers=0
    if ($Case.Mode -ceq 'encrypted') {
        $observation=$Exit.WriteObservation
        Confirm-ResourceCompatibility ($null -ne $observation -and $observation.ContainsKey('Errors') -and $observation.ContainsKey('Plaintext') -and
            $observation.Errors -eq 0 -and $observation.Plaintext -eq 0 -and $observation.Attribution -ceq 'Unavailable' -and
            $observation.Scope.Contains('not proof of all transient writes',[StringComparison]::Ordinal)) 'observation gate/scope'
        foreach ($entry in $Context.Observations) {
            Confirm-ResourceCompatibility ($entry.Path.StartsWith($runRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or $entry.Path.StartsWith($store+'\',[StringComparison]::OrdinalIgnoreCase)) 'observation path scope'
            Confirm-ResourceCompatibility ($entry.Header -cne '556E697479465300') 'observation plaintext header'
            if ($entry.Failure -ceq 'ControlEntryNotProbed') { $controls++; Confirm-ResourceCompatibility ($null -eq $entry.Header) 'observation control header' }
            elseif ($entry.Failure -or ($null -ne $entry.Header -and $entry.Header.Length -lt 16)) { $misses++ }
            elseif ($null -ne $entry.Header -and $entry.Header.Length -eq 16) { $headers++ }
        }
        Confirm-ResourceCompatibility ($controls -eq $observation.ControlEntriesNotProbed) 'observation control count'
        Confirm-ResourceCompatibility ($observation.ContainsKey('ProbeMisses') -and $observation.ProbeMisses -eq $misses) 'observation counters differ'
    } else { Confirm-ResourceCompatibility ($null -eq $Exit.WriteObservation) 'unexpected observation policy' }
    return @{ Run=$Case.Id; Role=$Case.Role; ControlledKill=$Case.Kill; Http=$http.Count; Watched=($Case.Mode -ceq 'encrypted');
        ObservationCountersMatch=$true; RecordedProbeMisses=$misses; ControlEntriesNotProbed=$controls; CompleteHeaders=$headers }
}

function Assert-ResourceCompatibilityResume($Targets, $Profile, $AllRequests) {
    foreach ($name in @('range206','kill','ignore-range','changed-etag','bad-range','weak')) {
        $id=$Profile.Runs.ResumePrefix+$name
        $snapshot=Read-ResourceCompatibilityJson (Get-ResourceCompatibilityPath $Targets.EvidenceRoot ('resume-'+$Profile.Runs.ResumeStorePrefix+$name+'-before.json'))
        $partial=Read-ResourceCompatibilityJson (Get-ResourceCompatibilityPath $Targets.EvidenceRoot ('Runs/'+$id+'-partial/result.json'))
        $store=Get-ResourceCompatibilityPath $Targets.EvidenceRoot ('Stores/'+$Profile.Runs.ResumeStorePrefix+'remote-r-'+$name)
        Confirm-ResourceCompatibility ($snapshot.Path -ceq $partial.PartialAtCancel.Path -and $snapshot.Length -eq $partial.PartialAtCancel.Length -and
            $snapshot.Length -ge 1048576 -and $snapshot.Length -lt $snapshot.Metadata.Length -and $snapshot.Path.StartsWith($store+'\',[StringComparison]::OrdinalIgnoreCase)) 'resume partial identity'
        $relative=$snapshot.Path.Substring($Targets.EvidenceRoot.Length).TrimStart('\','/')
        $path=Get-ResourceCompatibilityPath $Targets.EvidenceRoot $relative
        $metadata=Read-ResourceCompatibilityJson ([IO.Path]::ChangeExtension($path,'.json'))
        Confirm-ResourceCompatibility ($snapshot.Metadata.Count -eq $metadata.Count) 'resume metadata shape changed'
        foreach ($field in $snapshot.Metadata.Keys) {
            # 200 重下可合法更新 validator，归属与目标身份则必须逐字段保持。
            if ($field -cne 'ETag') { Confirm-ResourceCompatibility ($metadata.ContainsKey($field) -and $metadata[$field] -ceq $snapshot.Metadata[$field]) ('resume metadata changed: '+$field+' / '+$name) }
        }
        $owner=Read-ResourceCompatibilityJson (Join-Path $store '.ylr-store-owner.json')
        Confirm-ResourceCompatibility ($metadata.OwnerId -ceq $owner.OwnerId -and $owner.Root -ceq $store -and $metadata.Platform -ceq 'StandaloneWindows64' -and $metadata.State -ceq 'Partial') 'resume owner metadata'
        $source=[Uri]$metadata.Source
        Confirm-ResourceCompatibility ($source.Scheme -ceq 'https' -and $source.Host -ceq 'localhost' -and $source.Port -eq 18443) 'resume source scope'
        Assert-ResourceCompatibilityFiles (Split-Path -Parent $path) @(@{Path=([IO.Path]::GetFileName($path)); Length=$metadata.Length; Sha256=$metadata.Sha256})
        $input=[IO.File]::OpenRead($path)
        try {
            $buffer=[byte[]]::new([int]$snapshot.Length); $input.ReadExactly($buffer)
            $prefixHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($buffer)).ToLowerInvariant()
        } finally {$input.Dispose()}
        Confirm-ResourceCompatibility ($prefixHash -ceq $snapshot.Sha256) 'resume historical prefix hash'
        $serverFile=Get-ResourceCompatibilityPath (Join-Path $Targets.EvidenceRoot 'server') $source.AbsolutePath.TrimStart('/')
        Confirm-ResourceCompatibility ((Get-ResourceCompatibilityHash $serverFile) -ceq $metadata.Sha256) 'resume final server hash'
        $requests=@($AllRequests | Where-Object { $_.ContainsKey('run') -and $_.run -ceq ($id+'-complete') -and $_.url -ceq $source.AbsolutePath })
        Confirm-ResourceCompatibility ($requests.Count -gt 0 -and $metadata.ETag -ceq $requests[-1].etag) 'resume final validator must match the successful response'
        $ranged=@($requests | Where-Object { $_.range })
        if ($name -ceq 'weak') { Confirm-ResourceCompatibility ($null -eq $snapshot.Metadata.ETag -and $ranged.Count -eq 0 -and $requests.Count -eq 1 -and $requests[0].status -eq 200) 'resume weak validator' }
        else {
            Confirm-ResourceCompatibility ($ranged.Count -eq 1 -and $ranged[0].range -ceq ('bytes='+$snapshot.Length+'-') -and $ranged[0].ifRange -ceq $snapshot.Metadata.ETag) 'resume Range/If-Range'
            if ($name -cin @('ignore-range','changed-etag')) { Confirm-ResourceCompatibility ($requests.Count -eq 1 -and $requests[0].status -eq 200 -and $requests[0].bytes -eq $metadata.Length) 'resume 200 overwrite' }
            elseif ($name -ceq 'bad-range') { Confirm-ResourceCompatibility ($requests.Count -eq 2 -and $requests[0].contentRange.StartsWith(('bytes '+($snapshot.Length+1)+'-'),[StringComparison]::Ordinal) -and
                $requests[1].status -eq 200 -and -not $requests[1].range) 'resume invalid 206 reset' }
            else { Confirm-ResourceCompatibility ($requests.Count -eq 1 -and $requests[0].status -eq 206 -and $requests[0].bytes -eq ($metadata.Length-$snapshot.Length)) 'resume remaining bytes' }
        }
        $pids=@(foreach ($phase in @('partial','complete','active-restart')) { (Read-ResourceCompatibilityJson (Join-Path $Targets.EvidenceRoot ('Runs/'+$id+'-'+$phase+'/process-start.json'))).Pid })
        Confirm-ResourceCompatibility (@($pids | Sort-Object -Unique).Count -eq 3) 'resume must use independent processes'
    }
}

# 点源仅用于纯数据变异守卫；正常入口始终要求完整目标清单和新收据名。
if ($MyInvocation.InvocationName -eq '.') { return }
Confirm-ResourceCompatibility (-not [string]::IsNullOrWhiteSpace($TargetsPath) -and -not [string]::IsNullOrWhiteSpace($Run)) 'targets and a fresh run are required'
$targets=Read-ResourceCompatibilityJson $TargetsPath
Assert-ResourceCompatibilityTargets $targets
$owner=Read-ResourceCompatibilityJson (Get-ResourceCompatibilityPath $targets.Carrier 'r810-owner.json')
Confirm-ResourceCompatibility ($owner.Purpose -ceq 'Resource-8.10-owned-carrier' -and $owner.Evidence -ceq $targets.EvidenceRoot -and
    $owner.Carrier -ceq $targets.Carrier -and $owner.Repository -ceq $targets.Repository -and $owner.UnityVersion -ceq $targets.UnityVersion) 'owner identity'
$output=Get-ResourceCompatibilityPath $targets.EvidenceRoot ('Verification/'+$Run+'.json')
Confirm-ResourceCompatibility (-not (Test-Path -LiteralPath $output)) 'never overwrite a verification receipt'
$receipt=@{ Passed=$false; TargetsSha256=(Get-ResourceCompatibilityHash $TargetsPath); CheckerSha256=(Get-ResourceCompatibilityHash $PSCommandPath);
    Scope='Declared input/artifact/Player contracts and bounded observations; not whole-branch, IDE or audio DSP acceptance'; Cases=@(); Builds=@(); Failure=$null }
try {
    $cases=@(Get-ResourceCompatibilityCases $targets)
    $log=Get-ResourceCompatibilityPath $targets.EvidenceRoot 'server-requests.jsonl'
    $logHash=Get-ResourceCompatibilityHash $log
    $requests=@([IO.File]::ReadAllLines($log) | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable })
    $builds=@{}
    foreach ($profile in $targets.Profiles) {
        $builds[$profile.Role]=Assert-ResourceCompatibilityBuild $targets $profile
        $receipt.Builds+=@{Role=$profile.Role; Guid=$profile.BuildGuid; ReportSha256=$profile.BuildReportSha256; PlayerFiles=$builds[$profile.Role].PlayerFiles; SourceFiles=$builds[$profile.Role].SourceFiles}
    }
    foreach ($case in $cases) {
        $profile=@($targets.Profiles | Where-Object Role -CEQ $case.Role)[0]
        $path=Get-ResourceCompatibilityPath $targets.EvidenceRoot ('Runs/'+$case.Id)
        $body=Read-ResourceCompatibilityJson (Join-Path $path 'result.json')
        $context=@{ Targets=$targets; Profile=$profile; Config=$builds[$case.Role].Config; RuntimeIdentity=$builds[$case.Role].RuntimeIdentity;
            Requests=@($requests | Where-Object { $_.ContainsKey('run') -and $_.run -ceq $case.Id });
            CheckpointPid=$(if ($case.Kill) {[int][IO.File]::ReadAllText((Join-Path $path 'result.json.checkpoint'))} else {$null});
            Observations=$(if ($case.Mode -ceq 'encrypted') {@([IO.File]::ReadAllLines((Join-Path $path 'writes.jsonl')) | ForEach-Object {$_ | ConvertFrom-Json -AsHashtable})} else {@()});
            PlayerLog=[IO.File]::ReadAllText((Join-Path $path 'Player.log')) }
        $verified=Assert-ResourceCompatibilityRun -Case $case -Context $context -Body $body -Start (Read-ResourceCompatibilityJson (Join-Path $path 'process-start.json')) -Exit (Read-ResourceCompatibilityJson (Join-Path $path 'process-result.json'))
        $verified.EvidenceHashes=@{Result=(Get-ResourceCompatibilityHash (Join-Path $path 'result.json')); Start=(Get-ResourceCompatibilityHash (Join-Path $path 'process-start.json')); Exit=(Get-ResourceCompatibilityHash (Join-Path $path 'process-result.json'))}
        $receipt.Cases+=,$verified
    }
    foreach ($profile in @($targets.Profiles | Where-Object Development)) { Assert-ResourceCompatibilityResume $targets $profile $requests }
    Confirm-ResourceCompatibility ((Get-ResourceCompatibilityHash $log) -ceq $logHash) 'server evidence changed during verification'
    $receipt.ServerRequestsSha256=$logHash
    $receipt.NormalCount=@($receipt.Cases | Where-Object { -not $_.ControlledKill }).Count
    $receipt.ControlledKillCount=@($receipt.Cases | Where-Object ControlledKill).Count
    $receipt.ObservationCountersMatch=$true
    $receipt.FullWriteObservationProven=$false
    $receipt.Observation=@{ Runs=@($receipt.Cases | Where-Object Watched).Count; ProbeMisses=($receipt.Cases.RecordedProbeMisses | Measure-Object -Sum).Sum;
        ControlEntriesNotProbed=($receipt.Cases.ControlEntriesNotProbed | Measure-Object -Sum).Sum; CompleteHeaders=($receipt.Cases.CompleteHeaders | Measure-Object -Sum).Sum; Attribution='Unavailable' }
    $receipt.Passed=$true
} catch { $receipt.Failure=$_.Exception.Message; throw }
finally {
    $null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
    [IO.File]::WriteAllText($output,($receipt | ConvertTo-Json -Depth 12))
}
[pscustomobject]@{Passed=$receipt.Passed; Normal=$receipt.NormalCount; ControlledKill=$receipt.ControlledKillCount; ObservationCountersMatch=$receipt.ObservationCountersMatch; Receipt=$output}
