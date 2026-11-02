#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param([Parameter(Mandatory)][string] $TemporaryRoot)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Common/Results.ps1')
if (-not [IO.Directory]::Exists($TemporaryRoot)) { throw 'An existing temporary parent is required.' }
$sandbox = Join-Path (Get-YLTestPath $TemporaryRoot) ('yl-development-entry-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($sandbox)
$repository = Join-Path $sandbox 'Repository'; $null = [IO.Directory]::CreateDirectory($repository)
$workspace = Initialize-YLTestWorkspace -WorkspaceRoot (Join-Path $sandbox 'Workspace') -Repository $repository
$hostRecord = New-YLTestDirectory -Workspace $workspace -Kind Host -Key Author
$result = New-YLTestDirectory -Workspace $workspace -Kind Result -Key Author
$checks = 0
try {
    $stubs = @'
namespace UnityEngine
{
    public static class Application
    {
        public static string dataPath;
        public static string unityVersion = "6000.3.27f1";
        public static bool isBatchMode = true;
    }
}
namespace YLFramework.Testing.Editor
{
    public static class YLTestEntry { public static void Execute() { throw new System.InvalidOperationException("NoUnity cannot execute UTF."); } }
}
'@
    $references = @([IO.Directory]::EnumerateFiles((Join-Path $PSHOME 'ref'), '*.dll')) + @([Newtonsoft.Json.Linq.JObject].Assembly.Location)
    $source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../DevelopmentHostEntry.cs'))
    $diagnostics = @(Add-Type -TypeDefinition ($source + "`n" + $stubs) -ReferencedAssemblies $references -CompilerOptions @('/define:UNITY_EDITOR', '/langversion:9.0') -IgnoreWarnings 3>&1)
    foreach ($diagnostic in @($diagnostics | ForEach-Object { $_.ToString() } | Sort-Object -Unique)) { Write-Warning $diagnostic }
    [UnityEngine.Application]::dataPath = Join-Path $hostRecord.path 'Assets'
    $null = [IO.Directory]::CreateDirectory([UnityEngine.Application]::dataPath)
    $output = Join-Path $result.path 'Discover'; $null = [IO.Directory]::CreateDirectory($output)
    $requestPath = Join-Path $output 'request.json'
    $manifestPath = Join-Path $hostRecord.path 'Packages/manifest.json'; $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($manifestPath))
    [IO.File]::WriteAllText($manifestPath, '{"dependencies":{}}', [Text.UTF8Encoding]::new($false))
    $valid = @{ schemaVersion = 1; purpose = 'YLTesting.DevelopmentHost'; sourceProfile = 'YLFrameworkDev-6000.3.27f1_672d562a42a7'
        hostId = $hostRecord.id; resultId = $result.id; workspaceId = $workspace.id; expectedUnityVersion = '6000.3.27f1'; inputHash = ('a' * 64)
        action = 'Discover'; output = $output; assemblies = @('YLFramework.DevelopmentHost.Tests'); category = ''; excludedCategories = @(); sortingLayer = ''; interactiveDesktop = $false }
    Write-YLTestJson -Path (Join-Path $hostRecord.path '.yl-inputs.json') -Value @{
        schemaVersion = 1; purpose = 'YLTesting.HostInputs'; hostId = $hostRecord.id; inputHash = $valid.inputHash
        files = @(@{ path = 'Packages/manifest.json'; sha256 = (Get-FileHash -LiteralPath $manifestPath).Hash })
    }
    Write-YLTestJson -Path (Join-Path $hostRecord.path '.yl-development-host.json') -Value @{
        schemaVersion = 1; purpose = 'YLTesting.DevelopmentHost'; sourceProfile = $valid.sourceProfile; sourceRoot = $repository
        hostId = $hostRecord.id; workspaceId = $workspace.id; unityVersion = '6000.3.27f1'; productVersion = '6000.3.27f1_672d562a42a7'
    }
    $method = [YLFramework.Testing.DevelopmentHostEntry].GetMethod('ValidateInvocation', [Reflection.BindingFlags]'NonPublic,Static')
    function Invoke-Validation([hashtable] $Value, [string] $Path = $requestPath) {
        $arguments = [object[]]::new(2); $arguments[0] = [Newtonsoft.Json.Linq.JObject]::Parse((ConvertTo-Json -InputObject $Value -Depth 10)); $arguments[1] = $Path
        try { $null = $method.Invoke($null, $arguments) } catch { throw $_.Exception.GetBaseException() }
    }
    Invoke-Validation $valid; $checks++
    foreach ($field in @('schemaVersion', 'purpose', 'sourceProfile', 'hostId', 'resultId', 'workspaceId', 'expectedUnityVersion', 'inputHash', 'action', 'output', 'assemblies', 'category', 'excludedCategories', 'sortingLayer')) {
        $invalid = $valid.Clone(); $invalid[$field] = $true
        $rejected = $false; try { Invoke-Validation $invalid } catch { $rejected = $_.Exception -is [InvalidOperationException] }
        if (-not $rejected) { throw "A Boolean $field bypassed typed author authorization." }; $checks++
    }
    foreach ($change in @(@{ sourceProfile = 'Unity2022' }, @{ action = 'PrepareSamples' }, @{ excludedCategories = @('All') },
        @{ hostId = ('0' * 32) }, @{ resultId = ('0' * 32) }, @{ inputHash = ('0' * 64) }, @{ interactiveDesktop = 'false' },
        @{ prepareSceneTemplates = $true }, @{ prepareSessionScene = $true }, @{ disableDomainReload = $true })) {
        $invalid = $valid.Clone(); foreach ($item in $change.GetEnumerator()) { $invalid[$item.Key] = $item.Value }
        $rejected = $false; try { Invoke-Validation $invalid } catch { $rejected = $_.Exception -is [InvalidOperationException] }
        if (-not $rejected) { throw 'An unowned or mutating author profile acquired UTF authority.' }; $checks++
    }
    [UnityEngine.Application]::isBatchMode = $false
    $rejected = $false; try { Invoke-Validation $valid } catch { $rejected = $_.Exception -is [InvalidOperationException] }
    if (-not $rejected) { throw 'A user interactive Editor acquired execution authority.' }; $checks++
    [UnityEngine.Application]::isBatchMode = $true
    [IO.File]::AppendAllText($manifestPath, 'drift')
    $rejected = $false; try { Invoke-Validation $valid } catch { $rejected = $_.Exception -is [InvalidOperationException] }
    if (-not $rejected) { throw 'Changed host package inputs acquired UTF authority.' }; $checks++
    "DevelopmentHostEntry NoUnity ownership guards: $checks passed. NOT Unity compilation or UTF execution."
} finally { [IO.Directory]::Delete($sandbox, $true) }
