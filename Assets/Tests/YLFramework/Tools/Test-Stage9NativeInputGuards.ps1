#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $TempGoalRoot
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or $PSVersionTable.PSVersion.Major -ne 7 -or $PSVersionTable.PSVersion.Minor -ne 6) {
    throw 'Use Windows PowerShell Core 7.6.x.'
}
$allowed = Join-Path ([IO.Path]::GetTempPath()) 'kilo/U63-20261027-c63a1'
$root = [IO.Path]::GetFullPath($TempGoalRoot).TrimEnd('\')
if (-not [IO.Path]::IsPathFullyQualified($TempGoalRoot) -or
    -not $root.StartsWith($allowed.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Guard fixtures must be in a new child of Temp/kilo/U63-20261027-c63a1.'
}
for ($cursor = $root; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
    if ([IO.Path]::Exists($cursor) -and ([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Linked fixture path rejected.'
    }
}
if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($root)) -or [IO.Path]::Exists($root)) {
    throw 'Fixture parent must exist and fixture root must be new.'
}
$null = [IO.Directory]::CreateDirectory($root)
$entry = Join-Path $PSScriptRoot 'Invoke-Stage9NativeInput.ps1'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../..'))
$results = [Collections.Generic.List[object]]::new()
function Put-Json([string] $Path, $Value) {
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 30), [Text.UTF8Encoding]::new($false))
}
function New-Fixture([string] $Name, [string] $Version = '2022.3.62f3c1', [string] $InputMode = 'ExpectedLegacy', [string] $Backend = 'Mono2x') {
    $delivery = Join-Path $root $Name
    $project = Join-Path $delivery 'ReceiverA'
    $backendDirectory=if ($Backend -ceq 'IL2CPP') { 'Il2Cpp' } else { 'Mono' }
    $build = Join-Path $project ('P/NativeInput/' + $backendDirectory + '/' + $InputMode + '/build1')
    $null = [IO.Directory]::CreateDirectory($build)
    $null = [IO.Directory]::CreateDirectory((Join-Path $project 'ProjectSettings'))
    Put-Json (Join-Path $delivery 'owner.json') @{
        purpose='YLFramework-Stage9-independent-delivery'; root=$delivery; repository=$repository
    }
    [IO.File]::WriteAllText((Join-Path $delivery 'fixture.unitypackage'), 'Guard fixture only, not a Unity package.')
    $packageHash = (Get-FileHash -LiteralPath (Join-Path $delivery 'fixture.unitypackage')).Hash.ToLowerInvariant()
    Put-Json (Join-Path $project 'stage9-owner.json') @{
        Purpose='Stage9-independent-receiver'; Project=$project; Delivery=$delivery; UnityVersion=$Version
        UguiVersion=$(if ($Version -ceq '6000.3.27f1') { '2.0.0' } else { '1.0.0' })
        Package='fixture.unitypackage'; PackageSha256=$packageHash
    }
    [IO.File]::WriteAllText((Join-Path $project 'ProjectSettings/ProjectVersion.txt'), "m_EditorVersion: $Version`n")
    [IO.File]::WriteAllText((Join-Path $build 'Stage9NativeInput.exe'), 'Deliberately not an executable. Validation must never launch it.')
    $null = [IO.Directory]::CreateDirectory((Join-Path $build 'Stage9NativeInput_Data'))
    Put-Json (Join-Path $build 'Stage9NativeInput_Data/ScriptingAssemblies.json') @{ names=@('Assembly-CSharp.dll','YLFramework.Runtime.dll'); types=@(16,16) }
    Put-Json (Join-Path $build 'BuildReport.json') @{
        Schema='Stage9NativeInput-v1'; Result='Succeeded'; Errors=0; Options='None'; Development=$false
        Project=$project; Delivery=$delivery; Unity=$Version; UguiVersion=$(if ($Version -ceq '6000.3.27f1') { '2.0.0' } else { '1.0.0' })
        PackageSha256=$packageHash; BuildRun='build1'; Mode=$InputMode; Backend=$Backend
        Stripping=$(if ($Backend -ceq 'IL2CPP') { 'Low' } else { 'Disabled' })
        CppConfiguration='Release'; BuildGuid='1234567890abcdef1234567890abcdef'; Target='StandaloneWindows64'
        LegacyInput=($InputMode -cne 'ExpectedNew'); NewInput=($InputMode -cne 'ExpectedLegacy')
        InputSystemVersion=$(if ($InputMode -ceq 'ExpectedLegacy') { $null } elseif ($Version -ceq '6000.3.27f1') { '1.20.1' } else { '1.14.2' })
        SettingsRestored=$true; PlayerSettingsBeforeSha256=('b' * 64); PlayerSettingsAfterSha256=('b' * 64)
        Executable='Stage9NativeInput.exe'
        PlayerFiles=@(foreach ($relative in @('Stage9NativeInput.exe','Stage9NativeInput_Data/ScriptingAssemblies.json')) {
            @{ Path=$relative; Length=([IO.FileInfo](Join-Path $build $relative)).Length
                Sha256=(Get-FileHash -LiteralPath (Join-Path $build $relative)).Hash.ToLowerInvariant() }
        })
    }
    return @{ DeliveryRoot=$delivery; Receiver='ReceiverA'; BuildDirectory=$build; ExpectedUnityVersion=$Version
        BuildRun='build1'; Mode=$InputMode; Run='guard1'; Operation='Validate' }
}
function Change-Json([string] $Path, [scriptblock] $Change) {
    $value = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
    & $Change $value
    Put-Json $Path $value
}
function Check([string] $Name, [hashtable] $Arguments, [string] $Rejection) {
    $failure = $null
    $actual = $null
    try { $actual = & $entry @Arguments } catch { $failure = $_.Exception.Message }
    $passed = if ($Rejection) { $null -ne $failure -and $failure -match $Rejection } else {
        $null -eq $failure -and $null -ne $actual -and $actual.ValidationOnly -eq $true -and $actual.InputInjected -eq $false
    }
    $results.Add(@{ Name=$Name; Passed=$passed; Failure=$failure; Rejection=$Rejection; Actual=$actual })
    if (-not $passed) { Write-Warning "$Name failed: $failure" }
    if ([IO.Path]::Exists((Join-Path $Arguments.DeliveryRoot 'Runs/ReceiverA-native-ExpectedLegacy-guard1/start.json'))) {
        throw 'Validation created a process start record.'
    }
}

# These tests fail if validation accepts a different owner/build or performs any runtime action.
$a = New-Fixture 'valid-legacy'; Check 'Valid Legacy is validation only' $a ''
$a = New-Fixture 'valid-new' '6000.3.27f1' 'ExpectedNew'; Check 'Valid New is validation only' $a ''
$a = New-Fixture 'valid-both' '6000.3.27f1' 'ExpectedBoth'; Check 'Valid Both is validation only' $a ''
$a = New-Fixture 'valid-il2cpp' '2022.3.62f3c1' 'ExpectedNew' 'IL2CPP'; Check 'Valid IL2CPP is validation only' $a ''
$a = New-Fixture 'capture-capability'; $a.Suite='Capture'
Change-Json (Join-Path $a.BuildDirectory 'BuildReport.json') { param($j) $j.CaptureProtocol='B06Capture-v1' }
Check 'Capture validates only an explicitly capable build' $a ''
$a = New-Fixture 'capture-rejects-old-build'; $a.Suite='Capture'
Check 'Capture cannot reuse a basic-only frozen build' $a 'Capture protocol'
$a = New-Fixture 'remaining-capability'; $a.Suite='Remaining'
Change-Json (Join-Path $a.BuildDirectory 'BuildReport.json') { param($j) $j.RemainingProtocol='B06Remaining-v1' }
Check 'Remaining validates only an explicitly capable build' $a ''
$a = New-Fixture 'remaining-rejects-capture'; $a.Suite='Remaining'
Change-Json (Join-Path $a.BuildDirectory 'BuildReport.json') { param($j) $j.CaptureProtocol='B06Capture-v1' }
Check 'Remaining cannot use a capture-only build' $a 'Remaining protocol'
$a = New-Fixture 'unknown-player-activation'
Change-Json (Join-Path $a.BuildDirectory 'BuildReport.json') { param($j) $j.PlayerActivationProtocol='unknown' }
Check 'Unknown activation callers are rejected' $a 'activation protocol'
$a = New-Fixture 'source'; $a.DeliveryRoot=$repository; Check 'Source is never a delivery' $a 'Source|delivery owner'
$a = New-Fixture 'relative'; $a.BuildDirectory='relative'; Check 'Relative build' $a 'absolute|fully qualified'
$a = New-Fixture 'bad-receiver'; $a.Receiver='../ReceiverA'; Check 'Receiver traversal' $a 'Receiver|pattern'
$a = New-Fixture 'bad-run'; $a.Run='../old'; Check 'Run traversal' $a 'Run|pattern'
$a = New-Fixture 'bad-owner'; Change-Json (Join-Path $a.DeliveryRoot 'owner.json') { param($j) $j.purpose='foreign' }
Check 'Delivery owner mismatch' $a 'delivery owner'
$a = New-Fixture 'bad-project'; Change-Json (Join-Path $a.DeliveryRoot 'ReceiverA/stage9-owner.json') { param($j) $j.Project=$repository }
Check 'Receiver owner mismatch' $a 'Receiver owner'
$a = New-Fixture 'bad-version'; $a.ExpectedUnityVersion='6000.3.27f1'; Check 'Receiver version mismatch' $a 'version|dependency'
$a = New-Fixture 'bad-ugui'; Change-Json (Join-Path $a.DeliveryRoot 'ReceiverA/stage9-owner.json') { param($j) $j.UguiVersion='2.0.0' }
Check 'UGUI mismatch' $a 'version|dependency'
$a = New-Fixture 'bad-project-version'
[IO.File]::WriteAllText((Join-Path $a.DeliveryRoot 'ReceiverA/ProjectSettings/ProjectVersion.txt'), 'm_EditorVersion: 6000.3.27f1')
Check 'ProjectVersion mismatch' $a 'ProjectVersion'
$a = New-Fixture 'bad-directory'; $a.BuildDirectory=$a.BuildDirectory.Replace('build1','build1-other')
Check 'Build directory identity' $a 'Build directory'
$a = New-Fixture 'changed-package'; [IO.File]::AppendAllText((Join-Path $a.DeliveryRoot 'fixture.unitypackage'), 'changed')
Check 'Package hash changed' $a 'Package hash'
$mutations = @(
    @{ Name='development'; Change={ param($j) $j.Development=$true }; Error='non-Development' },
    @{ Name='wrong-mode'; Change={ param($j) $j.Mode='ExpectedNew' }; Error='Build identity' },
    @{ Name='wrong-macros'; Change={ param($j) $j.NewInput=$true }; Error='macro' },
    @{ Name='wrong-guid'; Change={ param($j) $j.BuildGuid='00000000000000000000000000000000' }; Error='BuildGuid' },
    @{ Name='wrong-stripping'; Change={ param($j) $j.Stripping='Low' }; Error='stripping' },
    @{ Name='unrestored-settings'; Change={ param($j) $j.SettingsRestored=$false }; Error='settings restoration' },
    @{ Name='changed-settings'; Change={ param($j) $j.PlayerSettingsAfterSha256=('c' * 64) }; Error='settings restoration' },
    @{ Name='wrong-project'; Change={ param($j) $j.Project='G:\foreign' }; Error='Build identity' },
    @{ Name='empty-files'; Change={ param($j) $j.PlayerFiles=@() }; Error='PlayerFiles' },
    @{ Name='duplicate-files'; Change={ param($j) $j.PlayerFiles=@($j.PlayerFiles[0],$j.PlayerFiles[0]) }; Error='Duplicate' },
    @{ Name='file-traversal'; Change={ param($j) $j.PlayerFiles[0].Path='../Stage9NativeInput.exe' }; Error='PlayerFiles path' },
    @{ Name='ambiguous-component'; Change={ param($j) $j.PlayerFiles[0].Path='dir. /Stage9NativeInput.exe' }; Error='PlayerFiles path' },
    @{ Name='bad-hash'; Change={ param($j) $j.PlayerFiles[0].Sha256=('a' * 64) }; Error='PlayerFiles hash' }
)
foreach ($case in $mutations) {
    $a = New-Fixture $case.Name
    Change-Json (Join-Path $a.BuildDirectory 'BuildReport.json') $case.Change
    Check $case.Name $a $case.Error
}
$a = New-Fixture 'extra-file'; [IO.File]::WriteAllText((Join-Path $a.BuildDirectory 'unlisted.dll'), 'unlisted')
Check 'Unlisted Player file' $a 'Unlisted'
$a = New-Fixture 'wrong-new-package' '6000.3.27f1' 'ExpectedNew'
Change-Json (Join-Path $a.BuildDirectory 'BuildReport.json') { param($j) $j.InputSystemVersion='1.14.2' }
Check 'Wrong engine Input System package' $a 'Input System version'
$a = New-Fixture 'wrong-cpp' '6000.3.27f1' 'ExpectedBoth' 'IL2CPP'
Change-Json (Join-Path $a.BuildDirectory 'BuildReport.json') { param($j) $j.CppConfiguration='Debug' }
Check 'IL2CPP must use C++ Release' $a 'stripping/C\+\+ Release'
$a = New-Fixture 'occupied-run'; $null=[IO.Directory]::CreateDirectory((Join-Path $a.DeliveryRoot 'Runs/ReceiverA-native-ExpectedLegacy-guard1'))
Check 'Never overwrite run' $a 'existing run'
$a = New-Fixture 'linked-build'
$target = Join-Path $a.BuildDirectory 'junction-target'; $null=[IO.Directory]::CreateDirectory($target)
$null=New-Item -ItemType Junction -Path (Join-Path $a.BuildDirectory 'linked') -Target $target
Check 'Nested junction rejected before enumeration' $a 'Linked path'
$a = New-Fixture 'activation-requires-run'; $a.ActivateOwnedWindow = $true
Check 'Foreground activation requires explicit Run' $a 'Foreground activation requires explicit Run'
$a = New-Fixture 'foreground-wait-requires-run'; $a.WaitForOwnedForegroundSeconds = 15
Check 'Foreground wait requires explicit Run' $a 'Foreground wait requires explicit Run'
$a = New-Fixture 'foreground-strategies-exclusive'; $a.Operation = 'Run'; $a.ActivateOwnedWindow = $true; $a.WaitForOwnedForegroundSeconds = 1
Check 'Foreground strategies are exclusive' $a 'Choose one foreground strategy'
$a = New-Fixture 'foreground-wait-bounded'; $a.WaitForOwnedForegroundSeconds = 16
Check 'Foreground wait has a strict upper bound' $a 'WaitForOwnedForegroundSeconds'
foreach ($unsafe in @('YLFramework.UI.Acceptance.dll','Microsoft.CodeAnalysis.dll','UnityEditor.CoreModule.dll','nunit.framework.dll')) {
    $a = New-Fixture ('unsafe-assembly-' + $unsafe.Replace('.','-'))
    $metadata = Join-Path $a.BuildDirectory 'Stage9NativeInput_Data/ScriptingAssemblies.json'
    Change-Json $metadata { param($v) $v.names += $unsafe; $v.types += 16 }
    Change-Json (Join-Path $a.BuildDirectory 'BuildReport.json') {
        param($v)
        $file = @($v.PlayerFiles | Where-Object Path -CEQ 'Stage9NativeInput_Data/ScriptingAssemblies.json')[0]
        $file.Length = ([IO.FileInfo]$metadata).Length
        $file.Sha256 = (Get-FileHash -LiteralPath $metadata).Hash.ToLowerInvariant()
    }
    Check ('Reject closed unsafe runtime assembly ' + $unsafe) $a 'Production native input cannot include test, acceptance, Analyzer or Editor assemblies'
}
foreach ($variant in @('missing','duplicate','string')) {
    $a = New-Fixture ('scripting-list-' + $variant)
    $metadata = Join-Path $a.BuildDirectory 'Stage9NativeInput_Data/ScriptingAssemblies.json'
    if ($variant -ceq 'missing') {
        [IO.File]::Delete($metadata)
        Change-Json (Join-Path $a.BuildDirectory 'BuildReport.json') { param($v) $v.PlayerFiles=@($v.PlayerFiles | Where-Object Path -CNE 'Stage9NativeInput_Data/ScriptingAssemblies.json') }
    } else {
        Change-Json $metadata { param($v) $v.names = $(if ($variant -ceq 'duplicate') { @('Assembly-CSharp.dll','Assembly-CSharp.dll') } else { 'Assembly-CSharp.dll' }) }
        Change-Json (Join-Path $a.BuildDirectory 'BuildReport.json') {
            param($v)
            $file = @($v.PlayerFiles | Where-Object Path -CEQ 'Stage9NativeInput_Data/ScriptingAssemblies.json')[0]
            $file.Length = ([IO.FileInfo]$metadata).Length
            $file.Sha256 = (Get-FileHash -LiteralPath $metadata).Hash.ToLowerInvariant()
        }
    }
    Check ('Reject invalid scripting assembly list ' + $variant) $a 'Closed scripting assembly inventory required|Invalid scripting assembly list'
}

$deliveryEntry = Join-Path $PSScriptRoot 'Invoke-Stage9Delivery.ps1'
$dispatchCases = @(
    @{ Name='build-requires-mode'; Operation='Editor'; Method='NativeInputBuildMono'; Mode=''; Error='Explicit native-input mode is required' },
    @{ Name='audit-rejects-mode'; Operation='Editor'; Method='Audit'; Mode='ExpectedLegacy'; Error='NativeInputMode is allowed only for explicit native-input builds' },
    @{ Name='generate-rejects-mode'; Operation='Editor'; Method='NativeInputGenerate'; Mode='ExpectedLegacy'; Error='NativeInputMode is allowed only for explicit native-input builds' },
    @{ Name='native-entry-requires-editor'; Operation='Tests'; Method='NativeInputGenerate'; Mode=''; Error='Native-input entries require Editor operation' },
    @{ Name='install-requires-product-first'; Operation='InstallNativeInput'; Method='Audit'; Mode=''; Error='Product-first proof required before native-input installation' },
    @{ Name='profile-requires-product-first'; Operation='InstallInputProfile'; Method='Audit'; Mode=''; Error='Product-first proof required before input-profile installation' },
    @{ Name='generate-requires-installed-templates'; Operation='Editor'; Method='NativeInputGenerate'; Mode=''; Error='Install exact native-input templates before invoking their Editor entry' }
)
foreach ($case in $dispatchCases) {
    $a = New-Fixture $case.Name
    $arguments = @{
        DeliveryRoot=$a.DeliveryRoot; Receiver=$a.Receiver; Operation=$case.Operation; Method=$case.Method
        ExpectedUnityVersion=$a.ExpectedUnityVersion; Package='fixture.unitypackage'
    }
    if ($case.Mode) { $arguments.NativeInputMode = $case.Mode }
    $failure = $null
    try { $null = & $deliveryEntry @arguments } catch { $failure = $_.Exception.Message }
    $passed = $null -ne $failure -and $failure -match $case.Error
    $results.Add(@{ Name=$case.Name; Passed=$passed; Failure=$failure; Rejection=$case.Error; Actual=$null })
    if (-not $passed) { Write-Warning ($case.Name + ' failed: ' + $failure) }
    if (@(Get-ChildItem -LiteralPath $a.DeliveryRoot -Filter '*.process.json' -File).Count -ne 0 -or
        (Test-Path -LiteralPath (Join-Path $a.DeliveryRoot 'ReceiverA/Assets/Stage9NativeInput'))) {
        throw 'Rejected native-input dispatch created a Unity invocation or installed templates.'
    }
}

# Execute the actual final wait with a deterministic boundary: the child exits just after WaitForExit returned false.
$tokens = $null
$parseErrors = $null
$driverAst = [System.Management.Automation.Language.Parser]::ParseFile($entry, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Native-input driver parse failed.' }
$activationGate=@($driverAst.FindAll({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Test-ParentActivation'
}, $true))
foreach ($activationCase in @(@($true,$false,$true),@($true,$true,$false),@($false,$true,$false))) {
    $failure=$null
    try {
        if ($activationGate.Count -ne 1) { throw 'One-caller activation policy is missing.' }
        & {
            . ([scriptblock]::Create($activationGate[0].Extent.Text))
            if ((Test-ParentActivation $activationCase[0] $activationCase[1]) -ne $activationCase[2]) { throw 'Activation caller decision differs.' }
        }
    } catch { $failure=$_.Exception.Message }
    $results.Add(@{ Name=('One ordinary foreground requester: authorized='+$activationCase[0]+', PlayerCaller='+$activationCase[1]); Passed=$null -eq $failure; Failure=$failure; Rejection=''; Actual=$null })
}
$keyGate=@($driverAst.FindAll({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Test-NativeKey'
}, $true))
$keyFailure=$null
try {
    if ($keyGate.Count -ne 1) { throw 'Exact native key whitelist is missing.' }
    & {
        . ([scriptblock]::Create($keyGate[0].Extent.Text))
        foreach ($key in @(13,27,37,38,39,40,49,50,51)) { if (-not (Test-NativeKey $key)) { throw 'Expected UI key rejected.' } }
        foreach ($key in @(0,1,17,18,65,91,92,115,255)) { if (Test-NativeKey $key) { throw 'Non-contract/system key accepted.' } }
    }
} catch { $keyFailure=$_.Exception.Message }
$results.Add(@{ Name='Only fixed navigation/text keys are accepted, never modifiers or system shortcuts'; Passed=$null -eq $keyFailure; Failure=$keyFailure; Rejection=''; Actual=$null })
$releaseGate=@($driverAst.FindAll({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Get-OwnedKeyRelease'
}, $true))
foreach ($case in @(
    @{ Name='Held key cleanup is eligible only for the same live owned window'; Pid=123; Window=456L; Alive=$true; Phase='key-down'; Key=39; Expected=$true },
    @{ Name='Never release a key into a dead Player'; Pid=123; Window=456L; Alive=$false; Phase='key-down'; Key=39; Expected=$false },
    @{ Name='Never release a key into another window'; Pid=123; Window=457L; Alive=$true; Phase='key-down'; Key=39; Expected=$false },
    @{ Name='Never release a key into another process'; Pid=124; Window=456L; Alive=$true; Phase='key-down'; Key=39; Expected=$false },
    @{ Name='Never duplicate an already emitted key up'; Pid=123; Window=456L; Alive=$true; Phase='key-up'; Key=39; Expected=$false },
    @{ Name='Never synthesize cleanup for a non-contract key'; Pid=123; Window=456L; Alive=$true; Phase='key-down'; Key=18; Expected=$false }
)) {
    $failure=$null
    try {
        if ($releaseGate.Count -ne 1 -or $keyGate.Count -ne 1) { throw 'Exact ownership-bound key cleanup policy is missing.' }
        & {
            . ([scriptblock]::Create($keyGate[0].Extent.Text))
            . ([scriptblock]::Create($releaseGate[0].Extent.Text))
            $evidence=@{ Pid=123; Hwnd=456L; Phase=$case.Phase; VirtualKey=$case.Key; Inserted=1; Foreground=$true }
            $actual=Get-OwnedKeyRelease $evidence $case.Pid $case.Window $case.Alive
            if (($null -ne $actual) -ne $case.Expected) { throw 'Held-key cleanup ownership decision differs.' }
        }
    } catch { $failure=$_.Exception.Message }
    $results.Add(@{ Name=$case.Name; Passed=$null -eq $failure; Failure=$failure; Rejection=''; Actual=$null })
}
$stableGate = @($driverAst.FindAll({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Update-InputReadiness'
}, $true))
foreach ($case in @(
    @{ Name='Input handoff requires 500ms of stationary ready input'; Samples=@(@($true,1,0),@($true,1,499),@($true,1,500)); Expected=@($false,$false,$true) },
    @{ Name='Cursor movement restarts the handoff interval'; Samples=@(@($true,1,0),@($true,2,499),@($true,2,500),@($true,2,999)); Expected=@($false,$false,$false,$true) },
    @{ Name='Foreground or key loss restarts the handoff interval'; Samples=@(@($true,1,0),@($false,1,499),@($true,1,500),@($true,1,999),@($true,1,1000)); Expected=@($false,$false,$false,$false,$true) }
)) {
    $failure=$null
    try {
        if ($stableGate.Count -ne 1) { throw 'Exact stationary input handoff gate is missing.' }
        & {
            . ([scriptblock]::Create($stableGate[0].Extent.Text))
            $state=@{ Ready=$false; Cursor=0L; Since=0.0 }
            $actual=@(foreach ($sample in $case.Samples) { Update-InputReadiness $sample[0] $sample[1] $sample[2] $state })
            if (($actual -join ',') -cne ($case.Expected -join ',')) { throw 'Input readiness state sequence differs.' }
        }
    } catch { $failure=$_.Exception.Message }
    $results.Add(@{ Name=$case.Name; Passed=$null -eq $failure; Failure=$failure; Rejection=''; Actual=$null })
    if ($failure) { Write-Warning ($case.Name+' failed: '+$failure) }
}
$terminalWait = @($driverAst.FindAll({ param($node)
    $node -is [System.Management.Automation.Language.WhileStatementAst] -and
        $node.Condition.Extent.Text -match '-not \$process\.WaitForExit\(25\)'
}, $true))
$ownedGuard = @($driverAst.FindAll({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Assert-OwnedProcess'
}, $true))
if ($terminalWait.Count -ne 1 -or $ownedGuard.Count -ne 1) { throw 'Exact terminal wait/ownership guard required for regression.' }
foreach ($expired in @($false, $true)) {
    $name = if ($expired) { 'Final process wait retains its hard deadline' } else { 'Exit between final wait observations is not an early exit' }
    $failure = $null
    try {
        & {
            param($deadlineExpired)
            $process = [pscustomobject]@{ HasExited=$true; WaitCount=0 }
            $process | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value {
                param([int]$milliseconds)
                if ($milliseconds -ne 25) { throw 'Unexpected wait quantum.' }
                $this.WaitCount++
                return $this.WaitCount -gt 1
            }
            $clock = [pscustomobject]@{ Elapsed=[TimeSpan]::Zero }
            $TimeoutSeconds = 30
            $deadline = [DateTime]::UtcNow.AddSeconds($(if ($deadlineExpired) { -1 } else { 30 })).ToString('O')
            . ([scriptblock]::Create($ownedGuard[0].Extent.Text))
            & ([scriptblock]::Create($terminalWait[0].Extent.Text))
            if ($process.WaitCount -ne 2) { throw 'The final wait did not observe the terminal handle.' }
        } $expired
    } catch { $failure = $_.Exception.Message }
    $passed = if ($expired) { $null -ne $failure -and $failure -match 'hard deadline expired' } else { $null -eq $failure }
    $results.Add(@{ Name=$name; Passed=$passed; Failure=$failure; Rejection=$(if ($expired) { 'hard deadline expired' } else { '' }); Actual=$null })
    if (-not $passed) { Write-Warning ($name + ' failed: ' + $failure) }
}

if ('Stage9NativeWin32' -as [type]) { throw 'Pure validation loaded the OS input binding.' }
# Explicit semantic compilation follows the unchanged no-binding validation gate; no Win32 method is invoked.
$nativeBodies=@($driverAst.FindAll({ param($node)
    $node -is [System.Management.Automation.Language.StringConstantExpressionAst] -and $node.Value -cmatch 'public static class Stage9NativeWin32'
}, $true))
$compileFailure=$null
try {
    if ($nativeBodies.Count -ne 1) { throw 'Exact native interop source is missing.' }
    Add-Type -TypeDefinition $nativeBodies[0].Value -ErrorAction Stop
    $layout=[Stage9NativeWin32].GetNestedType('KeyInput',[Reflection.BindingFlags]::NonPublic)
    $sizeMethod=[Runtime.InteropServices.Marshal].GetMethod('SizeOf',[type[]]@([System.Type]))
    if ($sizeMethod.Invoke($null,[object[]]@($layout)) -ne 40 -or
        [Runtime.InteropServices.Marshal]::OffsetOf($layout,'VirtualKey').ToInt32() -ne 8 -or
        [Runtime.InteropServices.Marshal]::OffsetOf($layout,'ScanCode').ToInt32() -ne 10 -or
        [Runtime.InteropServices.Marshal]::OffsetOf($layout,'Flags').ToInt32() -ne 12 -or
        [Runtime.InteropServices.Marshal]::OffsetOf($layout,'ExtraInfo').ToInt32() -ne 24) { throw 'x64 KEYBDINPUT layout differs.' }
} catch { $compileFailure=$_.Exception.Message }
$results.Add(@{ Name='Actual interop source compiles and x64 keyboard layout is exact without calling any Win32 method'; Passed=$null -eq $compileFailure; Failure=$compileFailure; Rejection=''; Actual=$null })
$flagFailure=$null
try {
    $flagMethod=[Stage9NativeWin32].GetMethod('KeyboardFlags',[Reflection.BindingFlags]'NonPublic, Static')
    if ($null -eq $flagMethod) { throw 'Exact navigation scan-code flag policy is missing.' }
    foreach ($flagCase in @(@(39,0x4D,$false,9),@(39,0x4D,$true,11),@(37,0x4B,$false,9),@(38,0x48,$false,9),@(40,0x50,$true,11),@(39,0xE04D,$false,9),@(13,0x1C,$false,8),@(27,1,$true,10),@(49,2,$false,8))) {
        $actualFlags=$flagMethod.Invoke($null,[object[]]@([int]$flagCase[0],[uint32]$flagCase[1],[bool]$flagCase[2]))
        if ($actualFlags -ne $flagCase[3]) { throw 'Navigation/text scan-code flags differ.' }
    }
} catch { $flagFailure=$_.Exception.Message }
$results.Add(@{ Name='Navigation arrows retain E0 even when this layout returns only a low-byte scan code'; Passed=$null -eq $flagFailure; Failure=$flagFailure; Rejection=''; Actual=$null })
$failed = @($results | Where-Object { -not $_.Passed })
Put-Json (Join-Path $root 'results.json') @{
    Passed=($failed.Count -eq 0); Scope='Offline validation plus explicit interop semantic compilation/layout checks; no Win32, Unity, Player, HWND or SendInput calls'
    PowerShell=$PSVersionTable.PSVersion.ToString(); Cases=@($results)
}
if ($failed.Count) { throw "$($failed.Count)/$($results.Count) guard tests failed; $root/results.json" }
"Passed $($results.Count) offline native-input guard/semantic tests: $root/results.json"
