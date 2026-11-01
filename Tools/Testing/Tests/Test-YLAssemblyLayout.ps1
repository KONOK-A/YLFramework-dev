#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $WorkspaceRoot,
    [Parameter(Mandatory)][string] $Snapshot,
    [ValidateSet('EditMode', 'Defaults', 'Player', 'All')][string] $Group = 'All'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Common/Workspace.ps1')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$workspace = Initialize-YLTestWorkspace -WorkspaceRoot $WorkspaceRoot -Repository $repository
$Snapshot = Get-YLTestPath $Snapshot
$owner = Get-YLTestDirectory -Workspace $workspace -Path ([IO.Path]::GetDirectoryName($Snapshot))
if ($owner.kind -cne 'Cache') { throw 'The compilation graph must be captured in an owned cache.' }
$graph = [IO.File]::ReadAllText($Snapshot) | ConvertFrom-Json -DateKind String
if ($graph.schemaVersion -ne 1 -or $graph.project -ine $repository -or
    $graph.compilationFailed -or $graph.isCompiling -or $graph.isUpdating) {
    throw 'The graph is not a stable real compilation snapshot of this repository.'
}
$failures = [Collections.Generic.List[string]]::new()
if ($Group -in @('EditMode', 'All')) {
    foreach ($assembly in $graph.editor | Where-Object { $_.name -match '^YLFramework\..*EditMode$|^YLFramework\..*Editor\.Tests$' }) {
        foreach ($reference in $assembly.references | Where-Object { $_ -match '^YLFramework\..*PlayMode$' }) {
            $failures.Add("EditMode assembly $($assembly.name) depends on PlayMode test assembly $reference.")
        }
    }
}
if ($Group -in @('Defaults', 'All')) {
    foreach ($assembly in $graph.editor | Where-Object { $_.name -in @('Assembly-CSharp', 'Assembly-CSharp-Editor') }) {
        foreach ($source in $assembly.sources | Where-Object { $_.Replace('\', '/').Contains('Assets/YLFramework/Tests/') }) {
            $failures.Add("Default assembly $($assembly.name) compiles test/acceptance source $source.")
        }
    }
}
if ($Group -in @('Player', 'All')) {
    foreach ($assembly in $graph.player) {
        foreach ($source in $assembly.sources | Where-Object { $_.Replace('\', '/').Contains('Assets/YLFramework/Tests/') }) {
            $failures.Add("Production Player graph compiles test/acceptance source $source in $($assembly.name).")
        }
        if ($assembly.name -eq 'YLFramework.Runtime') {
            foreach ($reference in $assembly.references + $assembly.precompiled | Where-Object {
                $_ -cnotmatch '^(?:mscorlib|netstandard|System(?:\.[A-Za-z0-9_]+)*|UnityEngine(?:\.[A-Za-z0-9_]+)*|Unity\.ugui|Microsoft\.CSharp|Microsoft\.Win32\.Primitives|Newtonsoft\.Json|BouncyCastle\.Cryptography)(?:\.dll)?$' -or
                $_ -match 'TestRunner|Tests|Editor|Analyzer'
            }) {
                $failures.Add("Production Runtime references forbidden test/Editor assembly $reference.")
            }
        }
    }
}
if ($failures.Count -ne 0) {
    $failures | ForEach-Object { "FAIL: $_" }
    throw "$($failures.Count) real compilation boundaries failed for $Group."
}
"PASS: $Group assembly boundaries in actual $($graph.productVersion) compilation graph. This is not a functional or Player-build result."
