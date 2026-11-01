#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $TemporaryRoot,
    [ValidateSet('All', 'Workspace', 'Process', 'Results')][string] $Group = 'All',
    [switch] $KeepArtifacts
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$common = Join-Path $PSScriptRoot '../Common'
foreach ($name in @('Workspace', 'Process', 'Results')) {
    $file = Join-Path $common ($name + '.ps1')
    if (Test-Path -LiteralPath $file -PathType Leaf) { . $file }
}
. (Join-Path $PSScriptRoot '../UnityHost.ps1')
if (-not [IO.Path]::IsPathFullyQualified($TemporaryRoot) -or -not [IO.Directory]::Exists($TemporaryRoot)) {
    throw 'TemporaryRoot must be an explicit existing filesystem directory.'
}
for ($cursor = [IO.Path]::GetFullPath($TemporaryRoot); $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
    if ([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked temporary roots are refused.' }
}
$sandbox = Join-Path $TemporaryRoot ('yl-testing-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($sandbox)
$repository = Join-Path $sandbox 'Repository'
$null = [IO.Directory]::CreateDirectory($repository)
$links = [Collections.Generic.List[string]]::new()
$failures = [Collections.Generic.List[string]]::new()
$script:cases = 0

function Assert-That([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Rejected([scriptblock] $Action, [string] $Pattern) {
    $rejected = $false
    try { $null = & $Action }
    catch {
        if ($_.Exception -is [Management.Automation.CommandNotFoundException]) { throw }
        if ($_.Exception.Message -notmatch $Pattern) { throw }
        $rejected = $true
    }
    Assert-That $rejected "Expected rejection matching: $Pattern"
}

function Test-Case([string] $Name, [scriptblock] $Body) {
    $script:cases++
    try { & $Body; "PASS: $Name" }
    catch { $failures.Add("${Name}: $($_.Exception.Message)"); "FAIL: ${Name}: $($_.Exception.Message)" }
}

function New-ProcessFixture([string] $Mode = 'Exit', [int] $ExitCode = 0) {
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $sandbox
    foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-File',
        (Join-Path $PSScriptRoot 'ProcessFixture.ps1'), '-Mode', $Mode, '-ExitCode', [string]$ExitCode)) {
        $start.ArgumentList.Add($argument)
    }
    return $start
}

function Assert-ChildExited([string] $Path) {
    Assert-That ([IO.File]::Exists($Path)) 'The real descendant did not publish its identity.'
    $child = [IO.File]::ReadAllText($Path) | ConvertFrom-Json -DateKind String
    try { $process = [Diagnostics.Process]::GetProcessById($child.pid) }
    catch [ArgumentException] { return }
    try {
        Assert-That ($process.HasExited -or $process.StartTime.ToUniversalTime().ToString('o') -cne $child.startUtc) 'An owned descendant survived cleanup.'
    } finally { $process.Dispose() }
}

function New-ResultFixture {
    $root = Join-Path $sandbox ([Guid]::NewGuid().ToString('N'))
    $null = [IO.Directory]::CreateDirectory($root)
    [IO.File]::WriteAllText((Join-Path $root 'Product.cs'), 'class Product {}')
    [IO.File]::WriteAllText((Join-Path $root 'Product.cs.meta'), 'guid: 0123456789abcdef0123456789abcdef')
    [IO.File]::WriteAllText((Join-Path $root 'TestSuites.json'), '{"profile":"Mono"}')
    [IO.File]::WriteAllText((Join-Path $root 'tool.dll'), 'toolchain-version-1')
    $paths = @('Product.cs', 'Product.cs.meta', 'TestSuites.json')
    $identity = Get-YLTestInputIdentity -Repository $root -Paths $paths -Toolchain @((Join-Path $root 'tool.dll')) -Profile 'Mono'
    $selection = New-YLTestSelection -InputIdentity $identity -Tests @(
        [pscustomobject]@{ assembly = 'Sample.Tests'; fullName = 'Example.A'; mode = 'EditMode'; runState = 'Runnable' },
        [pscustomobject]@{ assembly = 'Sample.Tests'; fullName = 'Example.B'; mode = 'EditMode'; runState = 'Runnable' }
    )
    $process = [pscustomobject]@{
        status = 'Exited'; observedExit = $true; exitCode = 0; timedOut = $false; cancelled = $false
        forcedCleanup = $false; observerError = $null; cleanupError = $null
        jobAssigned = $true; activeProcesses = 0; streamsDrained = $true
    }
    return [pscustomobject]@{ root = $root; paths = $paths; identity = $identity; selection = $selection; process = $process }
}

function New-RotationFixture([object] $Workspace, [string] $Status, [int] $Minute) {
    $run = New-YLTestDirectory -Workspace $Workspace -Kind Result -Key 'Daily-Reddot-Mono'
    Write-YLTestJson -Path (Join-Path $run.path 'summary.json') -Value @{
        schemaVersion = 1; ownerId = $run.id; action = 'Run'; status = $Status
        suite = 'Daily'; module = 'Reddot'; profile = 'Mono'
        endUtc = ([DateTimeOffset]'2026-10-01T00:00:00Z').AddMinutes($Minute).ToString('o')
    }
    return $run
}

try {
    if ($Group -in @('All', 'Workspace')) {
        Test-Case 'Editor selection binds exact product version and revision rather than the shared file prefix' {
            $editors = @(
                [pscustomobject]@{ fileVersion = '2022.3.62'; productVersion = '2022.3.62f3c1_1623fc0bbb97'; version = '2022.3.62f3c1' },
                [pscustomobject]@{ fileVersion = '2022.3.62'; productVersion = '2022.3.62f3_96770f904ca7'; version = '2022.3.62f3' }
            )
            $info = [pscustomobject]@{ FileMajorPart = 2022; FileMinorPart = 3; FileBuildPart = 62; ProductVersion = '2022.3.62f3_96770f904ca7' }
            $selected = Get-YLTestEditorProfile -Editors $editors -VersionInfo $info
            Assert-That ($selected.version -ceq '2022.3.62f3') 'The official Editor was mislabeled as the regional build.'
            $info.ProductVersion = '2022.3.62f3c1_1623fc0bbb97'
            $selected = Get-YLTestEditorProfile -Editors $editors -VersionInfo $info
            Assert-That ($selected.version -ceq '2022.3.62f3c1') 'The existing regional profile was lost.'
            $info.ProductVersion = '2022.3.62f3_unknown'
            Assert-Rejected { Get-YLTestEditorProfile -Editors $editors -VersionInfo $info } 'Unsupported|ambiguous'
            $info.ProductVersion = '2022.3.62f3_96770f904ca7'
            Assert-Rejected { Get-YLTestEditorProfile -Editors ($editors + $editors[1]) -VersionInfo $info } 'Unsupported|ambiguous'
        }
        Test-Case 'workspace initialization is explicit, reusable and preserves repository identity' {
            $unicode = [string][char]0x6D4B + [char]0x8BD5
            $root = Join-Path $sandbox "Workspace with spaces [$unicode]"
            $first = Initialize-YLTestWorkspace -WorkspaceRoot $root -Repository $repository
            $again = Initialize-YLTestWorkspace -WorkspaceRoot $root -Repository $repository
            Assert-That ($first.id -ceq $again.id) 'A compatible workspace must retain its identity.'
            Assert-That ($again.createdUtc -is [string] -and $again.createdUtc -ceq $first.createdUtc) 'Durable workspace timestamps must retain their wire format.'
            Assert-That ($first.root -ceq $root -and $first.repository -ceq $repository) 'Explicit paths must round-trip.'
            $marker = [IO.File]::ReadAllText((Join-Path $root '.yl-workspace.json')) | ConvertFrom-Json
            Assert-That ($marker.purpose -ceq 'YLTesting.Workspace' -and $marker.schemaVersion -eq 1) 'The workspace must have a durable owner.'
            Assert-Rejected { Initialize-YLTestWorkspace -WorkspaceRoot $root -Repository $sandbox } 'identity|repository'
        }

        Test-Case 'unknown directories and source roots are never adopted' {
            $unknown = Join-Path $sandbox 'Unknown'
            $null = [IO.Directory]::CreateDirectory($unknown)
            $sentinel = Join-Path $unknown 'user-input.txt'
            [IO.File]::WriteAllText($sentinel, 'preserve')
            Assert-Rejected { Initialize-YLTestWorkspace -WorkspaceRoot $unknown -Repository $repository } 'unknown|owner'
            Assert-That ([IO.File]::ReadAllText($sentinel) -ceq 'preserve') 'Unknown input changed.'
            Assert-Rejected { Initialize-YLTestWorkspace -WorkspaceRoot $repository -Repository $repository } 'source|repository'
            Assert-Rejected { Initialize-YLTestWorkspace -WorkspaceRoot $sandbox -Repository $repository } 'source|repository'
        }

        Test-Case 'workspace and container JSON identities reject boolean coercion' {
            $root = Join-Path $sandbox 'TypedOwners'
            $workspace = Initialize-YLTestWorkspace -WorkspaceRoot $root -Repository $repository
            $workspacePath = Join-Path $root '.yl-workspace.json'
            $original = [IO.File]::ReadAllText($workspacePath)
            foreach ($field in @('purpose', 'schemaVersion', 'repository', 'id')) {
                $invalid = $original | ConvertFrom-Json -AsHashtable -DateKind String
                $invalid[$field] = $true
                [IO.File]::WriteAllText($workspacePath, (ConvertTo-Json -InputObject $invalid))
                Assert-Rejected { Assert-YLTestWorkspace $workspace } 'identity|type|mismatch'
            }
            [IO.File]::WriteAllText($workspacePath, $original)
            $hostRecord = New-YLTestDirectory -Workspace $workspace -Kind Host -Key 'Host'
            $path = Join-Path $hostRecord.path '.yl-owner.json'
            $original = [IO.File]::ReadAllText($path)
            foreach ($field in @('purpose', 'schemaVersion', 'workspaceId', 'path', 'id')) {
                $invalid = $original | ConvertFrom-Json -AsHashtable -DateKind String
                $invalid[$field] = $true
                [IO.File]::WriteAllText($path, (ConvertTo-Json -InputObject $invalid))
                Assert-Rejected { Get-YLTestDirectory -Workspace $workspace -Path $hostRecord.path } 'identity|owner|type|mismatch'
                Assert-Rejected { New-YLTestContainer -Workspace $workspace -Path $hostRecord.path -Kind Host -Key Host -ExistingOnly } 'identity|owner|type|mismatch'
            }
            [IO.File]::WriteAllText($path, $original)
        }

        Test-Case 'ambiguous, relative, traversal and reserved Windows paths are rejected' {
            foreach ($path in @('', 'relative', 'G:relative', (Join-Path $sandbox '../escape'),
                (Join-Path $sandbox 'trailing.'), (Join-Path $sandbox 'CON.txt'), (Join-Path $sandbox 'file:stream'))) {
                Assert-Rejected { Get-YLTestPath -Path $path } 'explicit|ambiguous|reserved|path'
            }
        }

        Test-Case 'owned result creation and preview do not delete or rewrite outputs' {
            $workspace = Initialize-YLTestWorkspace -WorkspaceRoot (Join-Path $sandbox 'Preview') -Repository $repository
            $run = New-YLTestDirectory -Workspace $workspace -Kind Result -Key 'Daily-Reddot'
            $data = Join-Path $run.path 'results.xml'
            [IO.File]::WriteAllText($data, 'preserve')
            $before = (Get-FileHash -LiteralPath $data).Hash
            $preview = Remove-YLTestDirectory -Workspace $workspace -Path $run.path
            Assert-That ($preview.status -ceq 'Preview' -and [IO.Directory]::Exists($run.path)) 'Clean must default to preview.'
            Assert-That ((Get-FileHash -LiteralPath $data).Hash -ceq $before) 'Preview changed bytes.'
            $removed = Remove-YLTestDirectory -Workspace $workspace -Path $run.path -Apply
            Assert-That ($removed.status -ceq 'Removed' -and -not [IO.Directory]::Exists($run.path)) 'Apply must delete only the owned result.'
            Assert-That ([IO.Directory]::Exists($workspace.root)) 'Workspace root was deleted.'
        }

        Test-Case 'pinned and actively leased results survive cleanup' {
            $workspace = Initialize-YLTestWorkspace -WorkspaceRoot (Join-Path $sandbox 'Protected') -Repository $repository
            $run = New-YLTestDirectory -Workspace $workspace -Kind Result -Key 'Daily-Reddot'
            $lease = Lock-YLTestDirectory -Workspace $workspace -Path $run.path
            try { Assert-Rejected { Remove-YLTestDirectory -Workspace $workspace -Path $run.path -Apply } 'use|lease|lock' }
            finally { $lease.Dispose() }
            [IO.File]::WriteAllText((Join-Path $run.path '.pinned'), 'explicit retention')
            Assert-Rejected { Remove-YLTestDirectory -Workspace $workspace -Path $run.path -Apply } 'pinned'
            Assert-That ([IO.Directory]::Exists($run.path)) 'Protected result was deleted.'
        }

        Test-Case 'unknown, foreign and out-of-root cleanup targets are rejected' {
            $workspace = Initialize-YLTestWorkspace -WorkspaceRoot (Join-Path $sandbox 'Boundaries') -Repository $repository
            $other = Initialize-YLTestWorkspace -WorkspaceRoot (Join-Path $sandbox 'Other') -Repository $repository
            $foreign = New-YLTestDirectory -Workspace $other -Kind Result -Key 'Daily'
            $unknown = Join-Path $workspace.root 'Results/Unknown'
            $null = [IO.Directory]::CreateDirectory($unknown)
            foreach ($target in @($workspace.root, (Join-Path $workspace.root 'Results'), $unknown, $foreign.path, $repository)) {
                Assert-Rejected { Remove-YLTestDirectory -Workspace $workspace -Path $target -Apply } 'owner|owned|outside|root|container'
                Assert-That ([IO.Directory]::Exists($target)) 'A rejected target was deleted.'
            }
        }

        Test-Case 'linked ancestors and descendants never redirect cleanup' {
            $workspace = Initialize-YLTestWorkspace -WorkspaceRoot (Join-Path $sandbox 'Links') -Repository $repository
            $run = New-YLTestDirectory -Workspace $workspace -Kind Result -Key 'Daily'
            $outside = Join-Path $sandbox 'Outside'
            $null = [IO.Directory]::CreateDirectory($outside)
            [IO.File]::WriteAllText((Join-Path $outside 'sentinel.txt'), 'preserve')
            $link = Join-Path $run.path 'junction'
            $null = New-Item -ItemType Junction -Path $link -Target $outside
            $links.Add($link)
            Assert-Rejected { Get-YLTestPath -Path (Join-Path $link 'sentinel.txt') } 'link'
            $readOnly = Get-YLTestPath -Path (Join-Path $link 'sentinel.txt') -ReadOnly
            Assert-That ($readOnly -ceq (Join-Path $link 'sentinel.txt')) 'Read-only resolution must support installed executable/reference links.'
            Assert-Rejected { Remove-YLTestDirectory -Workspace $workspace -Path $run.path -Apply } 'link'
            Assert-That ([IO.File]::ReadAllText((Join-Path $outside 'sentinel.txt')) -ceq 'preserve') 'A link target changed.'
        }
    }
    if ($Group -in @('All', 'Process')) {
        Test-Case 'process facts retain real exit0 and both output streams without recording arguments' {
            $start = New-ProcessFixture
            $start.ArgumentList.Add('-Marker')
            $start.ArgumentList.Add('private-marker-not-for-receipts')
            $log = Join-Path $sandbox 'exit zero.log'
            $result = Invoke-YLTestProcess -StartInfo $start -Log $log -TimeoutMilliseconds 15000
            Assert-YLTestProcessSucceeded -ProcessResult $result
            Assert-That ($result.status -ceq 'Exited' -and $result.observedExit -and $result.exitCode -eq 0 -and -not $result.forcedCleanup) 'A real unforced exit0 was not recorded.'
            Assert-That ($result.pid -gt 0 -and $result.activeProcesses -eq 0 -and $result.streamsDrained) 'Process/tree/output observation is incomplete.'
            Assert-That ([IO.File]::ReadAllText($log).Contains('stdout-marker')) 'Standard output was lost.'
            Assert-That ([IO.File]::ReadAllText($log + '.stderr.log').Contains('stderr-marker')) 'Standard error was lost.'
            $receipt = Read-YLTestProcessResult -Log $log
            Assert-That ($receipt.exitCode -eq 0 -and $receipt.invocationId -ceq $result.invocationId) 'Terminal evidence cannot round-trip.'
            Assert-That ($receipt.startUtc -is [string] -and $receipt.startUtc -ceq $result.startUtc) 'Durable process timestamps must retain their wire format.'
            foreach ($path in @(($log + '.identity.json'), ($log + '.exit.json'))) {
                Assert-That (-not [IO.File]::ReadAllText($path).Contains('private-marker-not-for-receipts')) 'Arguments leaked into a receipt.'
            }
        }

        Test-Case 'nonzero exits remain failures even when artifacts look successful' {
            $result = Invoke-YLTestProcess -StartInfo (New-ProcessFixture -ExitCode 17) -Log (Join-Path $sandbox 'nonzero.log') -TimeoutMilliseconds 15000
            Assert-That ($result.status -ceq 'NonZeroExit' -and $result.observedExit -and $result.exitCode -eq 17) 'The actual nonzero exit was not retained.'
            Assert-That (-not $result.forcedCleanup -and $result.activeProcesses -eq 0) 'An ordinary failure must not be mislabeled as a forced exit.'
        }

        Test-Case 'timeout kills the real process and does not become an ordinary pass' {
            $result = Invoke-YLTestProcess -StartInfo (New-ProcessFixture -Mode Wait) -Log (Join-Path $sandbox 'timeout.log') -TimeoutMilliseconds 1000
            Assert-That ($result.status -ceq 'TimedOut' -and $result.timedOut -and $result.forcedCleanup) 'Timeout must remain a distinct forced outcome.'
            Assert-That ($result.observedExit -and $null -ne $result.exitCode -and $result.activeProcesses -eq 0) 'Timeout cleanup did not observe the real terminal state.'
        }

        Test-Case 'pre-cancellation starts no child and in-flight cancellation cleans up' {
            $cancel = [Threading.CancellationTokenSource]::new()
            try {
                $cancel.Cancel()
                $result = Invoke-YLTestProcess -StartInfo (New-ProcessFixture -Mode Wait) -Log (Join-Path $sandbox 'pre-cancel.log') -TimeoutMilliseconds 15000 -CancellationToken $cancel.Token
                Assert-That ($result.status -ceq 'Cancelled' -and $null -eq $result.pid -and -not $result.observedExit) 'Pre-cancellation started a child or fabricated an exit.'
            } finally { $cancel.Dispose() }
            $cancel = [Threading.CancellationTokenSource]::new()
            try {
                $cancel.CancelAfter(1500)
                $result = Invoke-YLTestProcess -StartInfo (New-ProcessFixture -Mode Wait) -Log (Join-Path $sandbox 'cancel.log') -TimeoutMilliseconds 15000 -CancellationToken $cancel.Token
                Assert-That ($result.status -ceq 'Cancelled' -and $result.pid -gt 0 -and $result.forcedCleanup -and $result.activeProcesses -eq 0) 'In-flight cancellation did not clean up its child.'
            } finally { $cancel.Dispose() }
        }

        Test-Case 'failed startup has no invented pid or exit code' {
            $start = New-ProcessFixture
            $start.FileName = Join-Path $sandbox 'missing executable.exe'
            $result = Invoke-YLTestProcess -StartInfo $start -Log (Join-Path $sandbox 'start-failed.log') -TimeoutMilliseconds 15000
            Assert-That ($result.status -ceq 'StartFailed' -and $null -eq $result.pid -and $null -eq $result.exitCode -and -not $result.observedExit) 'A start error was disguised as an observed process exit.'
        }

        Test-Case 'cancellation during descendant drain is not reported as exit0 success' {
            $start = New-ProcessFixture -Mode Orphan
            $start.ArgumentList.Add('-ChildIdentity')
            $start.ArgumentList.Add((Join-Path $sandbox 'drain-child.json'))
            $start.ArgumentList.Add('-DelayMilliseconds')
            $start.ArgumentList.Add('1500')
            $cancel = [Threading.CancellationTokenSource]::new()
            try {
                $cancel.CancelAfter(1200)
                $result = Invoke-YLTestProcess -StartInfo $start -Log (Join-Path $sandbox 'drain-cancel.log') -TimeoutMilliseconds 15000 -CancellationToken $cancel.Token
                Assert-That ($result.status -ceq 'Cancelled' -and $result.cancelled -and $result.activeProcesses -eq 0) 'Cancellation during terminal cleanup was lost.'
            } finally { $cancel.Dispose() }
        }

        Test-Case 'missing terminal evidence means observer lost rather than exit0' {
            $log = Join-Path $sandbox 'lost.log'
            $null = Invoke-YLTestProcess -StartInfo (New-ProcessFixture) -Log $log -TimeoutMilliseconds 15000
            [IO.File]::Delete($log + '.exit.json')
            $result = Read-YLTestProcessResult -Log $log
            Assert-That ($result.status -ceq 'ObserverLost' -and $null -eq $result.exitCode -and -not $result.observedExit) 'Missing terminal evidence must never imply exit0.'
        }

        Test-Case 'timeout cleans descendants and natural parent exit cannot hide a surviving child' {
            foreach ($mode in @('Tree', 'Orphan')) {
                $child = Join-Path $sandbox ($mode + '-child.json')
                $start = New-ProcessFixture -Mode $mode
                $start.ArgumentList.Add('-ChildIdentity')
                $start.ArgumentList.Add($child)
                $timeout = if ($mode -eq 'Tree') { 4000 } else { 15000 }
                $result = Invoke-YLTestProcess -StartInfo $start -Log (Join-Path $sandbox ($mode + '.log')) -TimeoutMilliseconds $timeout
                $expected = if ($mode -eq 'Tree') { 'TimedOut' } else { 'DescendantsAlive' }
                Assert-That ($result.status -ceq $expected -and $result.forcedCleanup -and $result.activeProcesses -eq 0) 'A surviving descendant was missed or left running.'
                Assert-ChildExited $child
            }
        }

        Test-Case 'invalid deadlines and existing process evidence are rejected before launch' {
            Assert-Rejected { Invoke-YLTestProcess -StartInfo (New-ProcessFixture) -Log (Join-Path $sandbox 'invalid.log') -TimeoutMilliseconds 0 } 'TimeoutMilliseconds'
            $log = Join-Path $sandbox 'existing.log'
            [IO.File]::WriteAllText($log, 'preserve')
            Assert-Rejected { Invoke-YLTestProcess -StartInfo (New-ProcessFixture) -Log $log -TimeoutMilliseconds 15000 } 'existing|preserve'
            Assert-That ([IO.File]::ReadAllText($log) -ceq 'preserve') 'Existing evidence was overwritten.'
        }
    }
    if ($Group -in @('All', 'Results')) {
        Test-Case 'Production Runtime graph rejects undeclared dependencies without a vendor blacklist' {
            $tokens = $null; $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-YLAssemblyLayout.ps1'), [ref]$tokens, [ref]$errors)
            Assert-That ($errors.Count -eq 0) 'Assembly boundary script must parse.'
            $guard = $ast.Find({ param($node) $node -is [Management.Automation.Language.IfStatementAst] -and $node.Clauses[0].Item1.Extent.Text -ceq '$Group -in @(''Player'', ''All'')' }, $true)
            Assert-That ($null -ne $guard) 'The production Player graph guard is missing.'
            $body = [scriptblock]::Create(($guard.Clauses[0].Item2.Statements.Extent.Text -join "`n"))
            $allowed = @('mscorlib', 'netstandard', 'System.Core', 'UnityEngine.CoreModule', 'UnityEngine.UI', 'Microsoft.Win32.Primitives', 'Microsoft.Win32.Primitives.dll', 'Newtonsoft.Json.dll', 'BouncyCastle.Cryptography.dll')
            foreach ($extra in @('', 'ThirdParty.Runtime', 'MiniExcel.dll', 'Microsoft.Win32.Unrelated.dll', 'Microsoft.Win32.Primitives.Extra', 'Microsoft.Win32.Primitives.Extra.dll', 'UnityEditor.CoreModule', 'YLFramework.Editor', 'YLFramework.Tests.PlayMode', 'YLFramework.Runtime.Extra')) {
                $graph = [pscustomobject]@{ player = @([pscustomobject]@{ name = 'YLFramework.Runtime'; sources = @('Assets/YLFramework/Runtime/Product.cs'); references = $allowed + @($extra | Where-Object { $_ }); precompiled = @() }) }
                $violations = @(& { param($graph, $body) $failures = [Collections.Generic.List[string]]::new(); . $body; $failures.ToArray() } $graph $body)
                Assert-That (($violations.Count -eq 0) -eq ($extra -ceq '')) "Runtime graph dependency admission is wrong for '$extra'."
            }
        }

        Test-Case 'Optional Equipment export binds product license bytes and the exact Editor closure' {
            $tokens = $null; $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '../../Equipment/Invoke-EquipmentReception.ps1'), [ref]$tokens, [ref]$errors)
            Assert-That ($errors.Count -eq 0) 'Equipment reception script must parse.'
            foreach ($name in @('Hash', 'Assert-EquipmentOptionalExport')) {
                $definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name }, $true)
                Assert-That ($null -ne $definition) "Optional export validator is missing: $name"
                . ([scriptblock]::Create($definition.Extent.Text))
            }
            Add-Type -AssemblyName System.Formats.Tar
            $fixture = Join-Path $sandbox 'EquipmentOptional'
            $null = [IO.Directory]::CreateDirectory($fixture)
            $inputs = [ordered]@{
                'Assets/YLFramework/Integrations/Excel/Editor' = $null
                'Assets/YLFramework/Integrations/Excel/Editor/MiniExcel.dll' = 'fixture binary'
                'Assets/YLFramework/Integrations/Excel/Editor/MiniExcel-LICENSE.txt' = 'fixture dependency license'
                'Assets/YLFramework/Integrations/Excel/Editor/Dependencies.json' = '{}'
                'Assets/YLFramework/Integrations/DomainExcel/Editor' = $null
                'Assets/YLFramework/Integrations/DomainExcel/Editor/DomainExcelCommands.cs' = 'class Commands {}'
                'Assets/YLFramework/LICENSE.md' = 'fixture product copyright and MIT license'
            }
            $identities = @{}; $index = 0
            foreach ($path in $inputs.Keys) {
                $absolute = Join-Path $fixture $path
                $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($absolute))
                if ($null -eq $inputs[$path]) { $null = [IO.Directory]::CreateDirectory($absolute) }
                else { [IO.File]::WriteAllText($absolute, $inputs[$path]) }
                $identities[$path] = (++$index).ToString('x32')
                [IO.File]::WriteAllText(($absolute + '.meta'), "guid: $($identities[$path])`n")
            }
            foreach ($fault in @('none', 'missing-license', 'changed-license', 'changed-meta', 'extra-root-file', 'extra-editor-file', 'missing-editor-source', 'duplicate-path')) {
                $package = Join-Path $fixture ($fault + '.unitypackage')
                $stream = [IO.File]::Create($package)
                $gzip = [IO.Compression.GZipStream]::new($stream, [IO.Compression.CompressionMode]::Compress)
                $writer = [System.Formats.Tar.TarWriter]::new($gzip, $true)
                try {
                    $members = @($inputs.Keys)
                    if ($fault -ceq 'extra-root-file') { $members += 'Assets/YLFramework/NOTICE.md' }
                    if ($fault -ceq 'extra-editor-file') { $members += 'Assets/YLFramework/Integrations/Excel/Editor/Foreign.cs' }
                    if ($fault -ceq 'duplicate-path') { $members += 'Assets/YLFramework/LICENSE.md' }
                    foreach ($path in $members) {
                        if (($fault -ceq 'missing-license' -and $path -ceq 'Assets/YLFramework/LICENSE.md') -or
                            ($fault -ceq 'missing-editor-source' -and $path.EndsWith('/DomainExcelCommands.cs'))) { continue }
                        $guid = if ($identities.ContainsKey($path)) { $identities[$path] } else { 'ffffffffffffffffffffffffffffffff' }
                        if ($fault -ceq 'duplicate-path' -and $path -ceq $members[-1]) { $guid = 'eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee' }
                        $meta = if ($identities.ContainsKey($path)) { [IO.File]::ReadAllBytes((Join-Path $fixture ($path + '.meta'))) } else { [Text.Encoding]::UTF8.GetBytes("guid: $guid`n") }
                        if ($fault -ceq 'changed-meta' -and $path -ceq 'Assets/YLFramework/LICENSE.md') { $meta = [Text.Encoding]::UTF8.GetBytes("guid: $guid`nchanged: true`n") }
                        $data = if ($inputs.Contains($path) -and $null -ne $inputs[$path]) { [string]$inputs[$path] } else { '' }
                        if ($fault -ceq 'changed-license' -and $path -ceq 'Assets/YLFramework/LICENSE.md') { $data = 'not the actual license' }
                        foreach ($entry in @(@('pathname', [Text.Encoding]::UTF8.GetBytes($path)), @('asset.meta', $meta), @('asset', [Text.Encoding]::UTF8.GetBytes($data)))) {
                            $record = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, ($guid + '/' + $entry[0]))
                            $record.DataStream = [IO.MemoryStream]::new([byte[]]$entry[1], $false)
                            try { $writer.WriteEntry($record) } finally { $record.DataStream.Dispose() }
                        }
                    }
                } finally { $writer.Dispose(); $gzip.Dispose(); $stream.Dispose() }
                if ($fault -ceq 'none') {
                    $paths = @(Assert-EquipmentOptionalExport $package $fixture)
                    Assert-That ($paths.Count -eq 7 -and $paths -ccontains 'Assets/YLFramework/LICENSE.md') 'Complete optional export was not accepted.'
                } else {
                    Assert-Rejected { Assert-EquipmentOptionalExport $package $fixture } 'Optional|optional|GUID|Meta|Payload|Duplicate|Unexpected'
                }
            }
        }

        Test-Case 'Stage9 native collection rejects unbound reports, stale owners and template drift' {
            $controller = Join-Path $PSScriptRoot '../../../Assets/YLFramework/Tests/Tools/Invoke-Stage9Delivery.ps1'
            foreach ($operation in @('InstallLogger', 'UpdateLogger')) {
                Assert-Rejected { & $controller -DeliveryRoot $sandbox -Operation $operation } 'ValidateSet|validate argument|belong to the set'
            }
            $tokens = $null; $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile($controller, [ref]$tokens, [ref]$errors)
            Assert-That ($errors.Count -eq 0) 'Stage9 controller must parse.'
            foreach ($name in @('Hash', 'Assert-Stage9BuildBinding')) {
                $definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name }, $true)
                Assert-That ($null -ne $definition) "Native build validator is missing: $name"
                . ([scriptblock]::Create($definition.Extent.Text))
            }
            $root = Join-Path $sandbox 'Stage9Binding'
            $project = Join-Path $root 'Receiver'
            $templateRoot = Join-Path $root 'Sources'
            $Package = 'Product.unitypackage'
            $null = [IO.Directory]::CreateDirectory((Join-Path $project 'Assets/Stage9/Editor'))
            $null = [IO.Directory]::CreateDirectory($templateRoot)
            $owner = Join-Path $project 'stage9-owner.json'
            [IO.File]::WriteAllText($owner, 'owned fixture identity')
            [IO.File]::WriteAllText((Join-Path $root $Package), 'fixture package bytes')
            $hashes = @{}; $inputs = @($owner, (Join-Path $root $Package))
            foreach ($name in @('Stage9DeliveryEditor','Stage9DeliveryProbe','Stage9DeliveryPanel')) {
                $source = Join-Path $templateRoot ($name + '.cs.txt')
                $installed = Join-Path $project ('Assets/Stage9/' + $(if ($name.EndsWith('Editor')) { 'Editor/' } else { '' }) + $name + '.cs')
                [IO.File]::WriteAllText($source, "fixture $name")
                [IO.File]::Copy($source, $installed)
                $hashes[$name] = Hash $source
                $inputs += @($source, $installed)
            }
            $report = [pscustomobject]@{ Schema = 'Stage9NativeBuild-v2'; Project = $project; OwnerSha256 = (Hash $owner); PackageSha256 = (Hash (Join-Path $root $Package)); TemplateSha256 = [pscustomobject]$hashes }
            Assert-Stage9BuildBinding $report $project $templateRoot
            $old = $report | Select-Object -Property * -ExcludeProperty Schema
            Assert-Rejected { Assert-Stage9BuildBinding $old $project $templateRoot } 'Build binding'
            $report.Schema = 'Stage9NativeBuild-v1'
            Assert-Rejected { Assert-Stage9BuildBinding $report $project $templateRoot } 'Build binding'
            $report.Schema = 'Stage9NativeBuild-v2'
            foreach ($field in @('Schema','Project','OwnerSha256','PackageSha256','TemplateSha256')) {
                $invalid = $report | Select-Object -Property *
                $invalid.$field = $true
                Assert-Rejected { Assert-Stage9BuildBinding $invalid $project $templateRoot } 'Build binding'
            }
            foreach ($name in $hashes.Keys) {
                $invalid = $report | Select-Object -Property *
                $invalid.TemplateSha256 = $report.TemplateSha256 | Select-Object -Property *
                $invalid.TemplateSha256.$name = $true
                Assert-Rejected { Assert-Stage9BuildBinding $invalid $project $templateRoot } 'Build binding'
            }
            foreach ($path in $inputs) {
                $original = [IO.File]::ReadAllText($path)
                [IO.File]::WriteAllText($path, $original + ' drift')
                Assert-Rejected { Assert-Stage9BuildBinding $report $project $templateRoot } 'Build binding'
                [IO.File]::WriteAllText($path, $original)
            }
            Assert-Stage9BuildBinding $report $project $templateRoot
        }

        Test-Case 'Unity response identity rejects boolean and noninteger wire fields' {
            $request = [pscustomobject]@{ hostId = 'host'; inputHash = 'hash'; action = 'Run'; mode = 'EditMode' }
            $valid = @{ schemaVersion = 1; hostId = 'host'; inputHash = 'hash'; status = 'Completed'; unityVersion = '2022.3.62f3' }
            $response = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $valid)
            Assert-YLUnityResponseIdentity -Response $response -Request $request -UnityVersion '2022.3.62f3'
            foreach ($field in @('schemaVersion', 'hostId', 'inputHash', 'status', 'unityVersion')) {
                $invalid = $valid.Clone()
                $invalid[$field] = $true
                $response = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $invalid)
                Assert-Rejected { Assert-YLUnityResponseIdentity -Response $response -Request $request -UnityVersion '2022.3.62f3' } 'identity|type|mismatch'
            }
        }

        Test-Case 'Unity selections cannot silently drop an entire declared execution mode' {
            $selection = [pscustomobject]@{ tests = @([pscustomobject]@{ mode = 'EditMode' }) }
            $both = [pscustomobject]@{ name = 'Fixture' }
            Assert-Rejected { Assert-YLUnitySelectionModes -Selection $selection -Definition $both -Suite Daily } 'mode'
            $editOnly = [pscustomobject]@{ name = 'Fixture'; modes = @('EditMode') }
            Assert-YLUnitySelectionModes -Selection $selection -Definition $editOnly -Suite Daily
            Assert-YLUnitySelectionModes -Selection $selection -Definition $both -Suite Special
            $specialBoth = [pscustomobject]@{ name = 'Fixture'; specialModes = @('EditMode', 'PlayMode') }
            Assert-Rejected { Assert-YLUnitySelectionModes -Selection $selection -Definition $specialBoth -Suite Special } 'mode'
        }

        $validXml = '<test-run result="Passed" total="2" passed="2" failed="0" skipped="0" inconclusive="0"><test-suite type="Assembly" name="Sample.Tests.dll" result="Passed"><test-case fullname="Example.A" result="Passed"/><test-case fullname="Example.B" result="Passed"/></test-suite></test-run>'

        Test-Case 'Player smoke requires the exact run, host, real process and complete behavior stages' {
            $fixture = New-ResultFixture
            $process = $fixture.process | Select-Object *, @{ Name = 'pid'; Expression = { 42 } }
            $path = Join-Path $fixture.root 'player.json'
            $valid = @{
                schemaVersion = 1; purpose = 'YLTesting.PlayerSmoke'; status = 'Passed'
                resultId = 'result-1'; hostId = 'host-1'; unityVersion = '2022.3.62f3'; pid = 42
                isEditor = $false; platform = 'WindowsPlayer'; frames = 2; error = ''; backend = 'Mono'
                recordCount = 0; leaseCount = 0
                stages = @('Started', 'ResourceLoaded', 'ReddotLifecycle', 'BusyOwnerProtected', 'ResourceReleased', 'Shutdown')
            }
            Write-YLTestJson -Path $path -Value $valid
            $accepted = Read-YLPlayerSmoke -Path $path -ResultId 'result-1' -HostId 'host-1' -UnityVersion '2022.3.62f3' -ProcessResult $process
            Assert-That ($accepted.status -ceq 'Passed') 'A complete matching Player run was not accepted.'
            foreach ($change in @(
                @{ resultId = 'stale' }, @{ hostId = 'foreign' }, @{ unityVersion = '6000.3.27f1' }, @{ pid = 43 }, @{ pid = '42' },
                @{ resultId = $true }, @{ hostId = $true }, @{ unityVersion = $true }, @{ purpose = $true }, @{ status = $true },
                @{ schemaVersion = $true }, @{ schemaVersion = '1' }, @{ platform = $true },
                @{ isEditor = $true }, @{ isEditor = 'false' }, @{ platform = 'WindowsEditor' },
                @{ backend = 'IL2CPP' }, @{ backend = '' }, @{ backend = $true },
                @{ frames = 0 }, @{ frames = '2' }, @{ status = 'Failed' }, @{ error = 'runtime error' },
                @{ recordCount = 1 }, @{ recordCount = '0' }, @{ leaseCount = 1 }, @{ stages = @('Started', 'Shutdown') },
                @{ stages = @('Started', 'ResourceLoaded', 'ReddotLifecycle', 'BusyOwnerProtected', 'ResourceReleased', 'Shutdown', 'Shutdown') }
                @{ stages = @($true, $true, $true, $true, $true, $true) }
            )) {
                $invalid = $valid.Clone()
                foreach ($entry in $change.GetEnumerator()) { $invalid[$entry.Key] = $entry.Value }
                [IO.File]::WriteAllText($path, (ConvertTo-Json -InputObject $invalid -Depth 10))
                Assert-Rejected { Read-YLPlayerSmoke -Path $path -ResultId 'result-1' -HostId 'host-1' -UnityVersion '2022.3.62f3' -ProcessResult $process } 'smoke'
            }
            $native = $valid.Clone()
            $native.backend = 'IL2CPP'
            [IO.File]::WriteAllText($path, (ConvertTo-Json -InputObject $native -Depth 10))
            $acceptedNative = Read-YLPlayerSmoke -Path $path -ResultId 'result-1' -HostId 'host-1' -UnityVersion '2022.3.62f3' -Backend IL2CPP -ProcessResult $process
            Assert-That ($acceptedNative.backend -ceq 'IL2CPP') 'The actual native backend was not accepted.'
            [IO.File]::WriteAllText($path, (ConvertTo-Json -InputObject $valid -Depth 10))
            $process.exitCode = 1
            Assert-Rejected { Read-YLPlayerSmoke -Path $path -ResultId 'result-1' -HostId 'host-1' -UnityVersion '2022.3.62f3' -ProcessResult $process } 'process|exit'
            $process.exitCode = 0
            $process.forcedCleanup = $true
            Assert-Rejected { Read-YLPlayerSmoke -Path $path -ResultId 'result-1' -HostId 'host-1' -UnityVersion '2022.3.62f3' -ProcessResult $process } 'process'
        }

        Test-Case 'HybridCLR smoke requires real native mode and the exact metadata/code/sample evidence' {
            $fixture = New-ResultFixture
            $process = $fixture.process | Select-Object *, @{ Name = 'pid'; Expression = { 42 } }
            $path = Join-Path $fixture.root 'hybrid.json'
            $expected = [pscustomobject]@{ codePackageId = ('a' * 64); nativeVariant = 'Official2022'; metadataAssemblies = @('mscorlib'); assemblies = @('Hot.Entry', 'Hot.Support') }
            $detail = @{
                mode = 'Native'; isSimulation = $false; codePackageId = $expected.codePackageId; nativeVariant = 'Official2022'
                metadataAssemblies = @('mscorlib'); assemblies = @('Hot.Entry', 'Hot.Support'); reflectionCount = 3
                message = 'v1: YL Resource configuration; dependency-count=2'; asyncResult = 'v1-async'; prefabMarker = 'v1-prefab'
                repeatedLoadReused = $true; wrongBaseRejected = $true
            }
            $valid = @{
                schemaVersion = 1; purpose = 'YLTesting.PlayerSmoke'; status = 'Passed'; backend = 'IL2CPP'
                resultId = 'result-1'; hostId = 'host-1'; unityVersion = '2022.3.62f3'; pid = 42; isEditor = $false
                platform = 'WindowsPlayer'; graphicsDevice = 'Direct3D11'; frames = 2; error = ''; recordCount = 0; leaseCount = 0
                stages = @('Started', 'HybridCLRMetadata', 'HybridCLRModule', 'HybridCLRReflection', 'HybridCLRShutdown'); hybridClr = $detail
            }
            Write-YLTestJson -Path $path -Value $valid
            $accepted = Read-YLPlayerSmoke -Path $path -ResultId 'result-1' -HostId 'host-1' -UnityVersion '2022.3.62f3' -Backend IL2CPP -HybridCLR $expected -ProcessResult $process
            Assert-That ($accepted.hybridClr.mode -ceq 'Native') 'A matching native HybridCLR run was not accepted.'
            foreach ($change in @(
                @{ mode = 'EditorSimulation' }, @{ isSimulation = $true }, @{ codePackageId = ('b' * 64) },
                @{ nativeVariant = 'foreign' }, @{ metadataAssemblies = @() }, @{ metadataAssemblies = @('other') },
                @{ assemblies = @('Hot.Entry', 'Hot.Entry') }, @{ reflectionCount = 0 }, @{ message = 'simulated' },
                @{ asyncResult = 'v2-async' }, @{ prefabMarker = 'missing' }, @{ repeatedLoadReused = $false }, @{ wrongBaseRejected = $false }
                @{ mode = $true }, @{ nativeVariant = $true }, @{ message = $true }, @{ asyncResult = $true }, @{ prefabMarker = $true }
            )) {
                $invalid = $detail.Clone()
                foreach ($entry in $change.GetEnumerator()) { $invalid[$entry.Key] = $entry.Value }
                $valid.hybridClr = $invalid
                [IO.File]::WriteAllText($path, (ConvertTo-Json -InputObject $valid -Depth 10))
                Assert-Rejected { Read-YLPlayerSmoke -Path $path -ResultId 'result-1' -HostId 'host-1' -UnityVersion '2022.3.62f3' -Backend IL2CPP -HybridCLR $expected -ProcessResult $process } 'HybridCLR'
            }
            $valid.hybridClr = $detail
            $valid.graphicsDevice = 'Null'
            [IO.File]::WriteAllText($path, (ConvertTo-Json -InputObject $valid -Depth 10))
            Assert-Rejected { Read-YLPlayerSmoke -Path $path -ResultId 'result-1' -HostId 'host-1' -UnityVersion '2022.3.62f3' -Backend IL2CPP -HybridCLR $expected -ProcessResult $process } 'HybridCLR'
        }

        Test-Case 'input identity is deterministic and includes metadata, toolchain and profile' {
            $fixture = New-ResultFixture
            $again = Get-YLTestInputIdentity -Repository $fixture.root -Paths @('TestSuites.json', 'Product.cs.meta', 'Product.cs') -Toolchain @((Join-Path $fixture.root 'tool.dll')) -Profile 'Mono'
            Assert-That ($fixture.identity.sha256 -ceq $again.sha256) 'Input ordering changed a deterministic identity.'
            foreach ($relative in @('Product.cs', 'Product.cs.meta', 'TestSuites.json', 'tool.dll')) {
                $path = Join-Path $fixture.root $relative
                $old = [IO.File]::ReadAllText($path)
                [IO.File]::WriteAllText($path, $old + 'changed')
                $changed = Get-YLTestInputIdentity -Repository $fixture.root -Paths $fixture.paths -Toolchain @((Join-Path $fixture.root 'tool.dll')) -Profile 'Mono'
                Assert-That ($changed.sha256 -cne $fixture.identity.sha256) "Input drift was missed: $relative"
                Assert-Rejected { Assert-YLTestSelection -Selection $fixture.selection -InputIdentity $changed } 'input|stale|profile'
                [IO.File]::WriteAllText($path, $old)
            }
            $profile = Get-YLTestInputIdentity -Repository $fixture.root -Paths $fixture.paths -Toolchain @((Join-Path $fixture.root 'tool.dll')) -Profile 'Unity-2022'
            Assert-Rejected { Assert-YLTestSelection -Selection $fixture.selection -InputIdentity $profile } 'input|stale|profile'
            [IO.File]::WriteAllText((Join-Path $fixture.root 'NewTest.cs'), 'class NewTest {}')
            $added = Get-YLTestInputIdentity -Repository $fixture.root -Paths ($fixture.paths + 'NewTest.cs') -Toolchain @((Join-Path $fixture.root 'tool.dll')) -Profile 'Mono'
            Assert-Rejected { Assert-YLTestSelection -Selection $fixture.selection -InputIdentity $added } 'input|stale|profile'
        }

        Test-Case 'empty, duplicate and non-runnable discovered selections are rejected' {
            $fixture = New-ResultFixture
            Assert-Rejected { New-YLTestSelection -InputIdentity $fixture.identity -Tests @() } 'empty|zero'
            Assert-Rejected { New-YLTestSelection -InputIdentity $fixture.identity -Tests @($fixture.selection.tests[0], $fixture.selection.tests[0]) } 'duplicate'
            $ignored = [pscustomobject]@{ assembly = 'Sample.Tests'; fullName = 'Example.Ignored'; mode = 'EditMode'; runState = 'Ignored' }
            Assert-Rejected { New-YLTestSelection -InputIdentity $fixture.identity -Tests @($ignored) } 'runnable|skip'
        }

        Test-Case 'strict result validation accepts exact leaves with a genuine ordinary process outcome' {
            $fixture = New-ResultFixture
            $path = Join-Path $fixture.root 'results.xml'
            [IO.File]::WriteAllText($path, $validXml)
            $result = Read-YLTestXml -Path $path -Selection $fixture.selection -InputIdentity $fixture.identity -Mode EditMode -ProcessResult $fixture.process
            Assert-That ($result.status -ceq 'Passed' -and $result.total -eq 2 -and $result.passed -eq 2) 'Exact real test leaves were not accepted.'
            [IO.File]::WriteAllText($path, $validXml.Replace('<test-run', '<test-suite type="Assembly" name="Sample.Tests.exe"').Replace('</test-run>', '</test-suite>'))
            $offline = Read-YLTestXml -Path $path -Selection $fixture.selection -InputIdentity $fixture.identity -Mode EditMode -ProcessResult $fixture.process
            Assert-That ($offline.total -eq 2) 'NUnit assembly-root XML must be supported, not just UTF test-run roots.'
            [IO.File]::WriteAllText($path, $validXml.Replace('<test-run', '<test-suite type="TestSuite" name="OwnedHost"').Replace('</test-run>', '</test-suite>'))
            $utf = Read-YLTestXml -Path $path -Selection $fixture.selection -InputIdentity $fixture.identity -Mode EditMode -ProcessResult $fixture.process
            Assert-That ($utf.total -eq 2) 'The real UTF ITestResultAdaptor TestSuite root must retain strict leaf validation.'
            [IO.File]::WriteAllText($path, $validXml.Replace('<test-run', '<test-suite type="TestFixture" name="InvalidRoot"').Replace('</test-run>', '</test-suite>'))
            Assert-Rejected { Read-YLTestXml -Path $path -Selection $fixture.selection -InputIdentity $fixture.identity -Mode EditMode -ProcessResult $fixture.process } 'root'
        }

        Test-Case 'missing, extra, duplicate and wrong-assembly XML leaves are rejected' {
            $fixture = New-ResultFixture
            $path = Join-Path $fixture.root 'results.xml'
            $invalid = @(
                $validXml.Replace('<test-case fullname="Example.B" result="Passed"/>', ''),
                $validXml.Replace('</test-suite>', '<test-case fullname="Example.C" result="Passed"/></test-suite>'),
                $validXml.Replace('Example.B', 'Example.A'),
                $validXml.Replace('Sample.Tests.dll', 'Other.Tests.dll')
            )
            foreach ($xml in $invalid) {
                [IO.File]::WriteAllText($path, $xml)
                Assert-Rejected { Read-YLTestXml -Path $path -Selection $fixture.selection -InputIdentity $fixture.identity -Mode EditMode -ProcessResult $fixture.process } 'selection|duplicate|count|missing|extra|assembly'
            }
        }

        Test-Case 'unapproved skip, suite failure and dishonest counters cannot pass' {
            $fixture = New-ResultFixture
            $path = Join-Path $fixture.root 'results.xml'
            $invalid = @(
                $validXml.Replace('fullname="Example.B" result="Passed"', 'fullname="Example.B" result="Skipped"'),
                $validXml.Replace('<test-run result="Passed"', '<test-run result="Failed"'),
                $validXml.Replace('name="Sample.Tests.dll" result="Passed"', 'name="Sample.Tests.dll" result="Failed"'),
                $validXml.Replace('passed="2"', 'passed="1"'),
                '<test-run result="Passed" total="0" passed="0" failed="0" skipped="0" inconclusive="0"/>'
            )
            foreach ($xml in $invalid) {
                [IO.File]::WriteAllText($path, $xml)
                Assert-Rejected { Read-YLTestXml -Path $path -Selection $fixture.selection -InputIdentity $fixture.identity -Mode EditMode -ProcessResult $fixture.process } 'skip|failed|result|count|zero'
            }
        }

        Test-Case 'incomplete and DTD-bearing XML is rejected without external entity resolution' {
            $fixture = New-ResultFixture
            $path = Join-Path $fixture.root 'results.xml'
            foreach ($xml in @('', '<test-run result="Passed">', ('<!DOCTYPE test-run [<!ENTITY outside SYSTEM "file:///not-owned">]>' + $validXml))) {
                [IO.File]::WriteAllText($path, $xml)
                Assert-Rejected { Read-YLTestXml -Path $path -Selection $fixture.selection -InputIdentity $fixture.identity -Mode EditMode -ProcessResult $fixture.process } 'XML|empty|incomplete|DTD'
            }
        }

        Test-Case 'good XML cannot mask timeout, cancellation, forced cleanup or observation failures' {
            $fixture = New-ResultFixture
            $path = Join-Path $fixture.root 'results.xml'
            [IO.File]::WriteAllText($path, $validXml)
            foreach ($change in @(
                @{ status = 'TimedOut' }, @{ status = 'ObserverLost' }, @{ exitCode = 17 }, @{ observedExit = $false },
                @{ timedOut = $true }, @{ cancelled = $true }, @{ forcedCleanup = $true },
                @{ observerError = 'lost' }, @{ cleanupError = 'child alive' }, @{ activeProcesses = 1 },
                @{ activeProcesses = $null }, @{ activeProcesses = '0' }, @{ activeProcesses = $false },
                @{ streamsDrained = $false }, @{ jobAssigned = $false }
            )) {
                $process = $fixture.process | Select-Object *
                foreach ($entry in $change.GetEnumerator()) { $process.($entry.Key) = $entry.Value }
                Assert-Rejected { Read-YLTestXml -Path $path -Selection $fixture.selection -InputIdentity $fixture.identity -Mode EditMode -ProcessResult $process } 'process|exit|observer|cleanup'
            }
        }

        Test-Case 'rotation never adopts boolean summary identity or status as a success' {
            $workspace = Initialize-YLTestWorkspace -WorkspaceRoot (Join-Path $sandbox 'TypedRotation') -Repository $repository
            foreach ($field in @('ownerId', 'schemaVersion', 'status', 'action', 'endUtc')) {
                $record = New-RotationFixture $workspace 'Passed' 1
                $path = Join-Path $record.path 'summary.json'
                $summary = [IO.File]::ReadAllText($path) | ConvertFrom-Json -AsHashtable -DateKind String
                $summary[$field] = $true
                [IO.File]::WriteAllText($path, (ConvertTo-Json -InputObject $summary))
                $candidate = @(Get-YLTestRotation -Workspace $workspace -Key 'Daily-Reddot-Mono' | Where-Object { $_.path -ceq $record.path })[0]
                Assert-That (-not $candidate.canRemove -and $candidate.reason.StartsWith('Excluded:')) "A boolean $field was classified as an ordinary result."
            }
        }

        Test-Case 'rotation retains one ordinary success and three failures including accepted outcomes' {
            $workspace = Initialize-YLTestWorkspace -WorkspaceRoot (Join-Path $sandbox 'Rotation') -Repository $repository
            $success = @(0..4 | ForEach-Object { New-RotationFixture $workspace 'Passed' $_ })
            $failed = @(0..4 | ForEach-Object { New-RotationFixture $workspace 'Failed' (10 + $_) })
            $accepted = New-RotationFixture $workspace 'Accepted' 20
            $pinned = New-RotationFixture $workspace 'Passed' 30
            [IO.File]::WriteAllText((Join-Path $pinned.path '.pinned'), 'explicit retention')
            $active = New-RotationFixture $workspace 'Failed' 40
            $unknown = Join-Path ([IO.Path]::GetDirectoryName($active.path)) 'Unknown'
            $null = [IO.Directory]::CreateDirectory($unknown)
            [IO.File]::WriteAllText((Join-Path $unknown 'sentinel.txt'), 'preserve')
            $lease = Lock-YLTestDirectory -Workspace $workspace -Path $active.path
            try {
                $rotation = @(Get-YLTestRotation -Workspace $workspace -Key 'Daily-Reddot-Mono')
                $candidates = @($rotation | Where-Object { $_.canRemove })
                Assert-That ($candidates.Count -eq 7) ('The 1/3 rotation buckets or protected output accounting are wrong. ' + ($rotation | ConvertTo-Json -Depth 5 -Compress))
                foreach ($run in $success + $failed + @($accepted, $pinned, $active)) {
                    Assert-That ([IO.Directory]::Exists($run.path)) 'Rotation planning must be read-only.'
                }
                $removed = @(Invoke-YLTestRotation -Workspace $workspace -Key 'Daily-Reddot-Mono')
                Assert-That ($removed.Count -eq 7) 'The shared retention entry did not remove exactly the eligible obsolete outputs.'
                foreach ($path in @($success[4].path, $failed[3].path, $failed[4].path, $accepted.path, $pinned.path, $active.path, $unknown)) {
                    Assert-That ([IO.Directory]::Exists($path)) 'A retained/protected result was removed.'
                }
                foreach ($run in @($success[0], $success[1], $success[2], $success[3], $failed[0], $failed[1], $failed[2])) {
                    Assert-That (-not [IO.Directory]::Exists($run.path)) 'An eligible obsolete result was not removed.'
                }
                Assert-That (@($rotation | Where-Object { -not $_.canRemove -and $_.path -eq $unknown }).Count -eq 1) 'Unknown outputs need an explicit exclusion.'
            } finally { $lease.Dispose() }
        }
    }
    if ($cases -eq 0) { throw "No implemented test group selected: $Group" }
    if ($failures.Count -ne 0) { throw "$($failures.Count)/$cases tooling contracts failed. $($failures -join '; ')" }
    "YL testing contracts: $cases passed. NOT Unity, PlayMode or Player acceptance."
}
finally {
    foreach ($link in $links) { if ([IO.Directory]::Exists($link)) { [IO.Directory]::Delete($link, $false) } }
    if ($KeepArtifacts) { "Artifacts retained: $sandbox" }
    else { [IO.Directory]::Delete($sandbox, $true) }
}
