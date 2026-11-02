#requires -Version 7.6
[CmdletBinding()]
param(
    [Parameter(Mandatory, ParameterSetName = 'Unity')][string] $WorkspaceRoot,
    [Parameter(Mandatory, ParameterSetName = 'Unity')][string] $Unity,
    [Parameter(Mandatory, ParameterSetName = 'NoUnity')][switch] $NoUnity,
    [Parameter(Mandatory, ParameterSetName = 'NoUnity')][string] $TemporaryRoot
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($NoUnity) { & (Join-Path $PSScriptRoot 'Test-YLDistributionEntryNoUnity.ps1') -TemporaryRoot $TemporaryRoot; return }
. (Join-Path $PSScriptRoot '../Common/Process.ps1')
. (Join-Path $PSScriptRoot '../Common/Results.ps1')
. (Join-Path $PSScriptRoot '../UnityHost.ps1')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$config = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../TestSuites.json')) | ConvertFrom-Json -DateKind String
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($Unity)
$editor = Get-YLTestEditorProfile $config.editors $version
$workspace = Initialize-YLTestWorkspace -WorkspaceRoot $WorkspaceRoot -Repository $repository
$run = New-YLTestDirectory -Workspace $workspace -Kind Result -Key "Tooling-DistributionGuards-$($version.ProductVersion)"
$lease = Lock-YLTestDirectory -Workspace $workspace -Path $run.path
$hostRecord = New-YLTestDirectory -Workspace $workspace -Kind Host -Key "DG-$($run.id)"
$hostLease = Lock-YLTestDirectory -Workspace $workspace -Path $hostRecord.path
$summary = [ordered]@{ schemaVersion = 1; ownerId = $run.id; action = 'Run'; suite = 'Tooling'; module = 'DistributionGuards';
    profile = $version.ProductVersion; status = 'Failed'; reason = $null; checks = 0; processes = @(); endUtc = $null }
try {
    $probe = @'
using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
namespace YLFramework.Testing
{
    public static class DistributionGuard
    {
        public static void Run()
        {
            var args = Environment.GetCommandLineArgs();
            var request = JObject.Parse(File.ReadAllText(args[Array.IndexOf(args, "-ylGuardRequest") + 1]));
            var project = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath));
            var host = JObject.Parse(File.ReadAllText(Path.Combine(project, ".yl-owner.json")));
            var result = JObject.Parse(File.ReadAllText(Path.Combine((string)request["resultRoot"], ".yl-owner.json")));
            var output = Path.GetFullPath((string)request["output"]);
            if ((string)host["kind"] != "Host" || (string)host["purpose"] != "YLTesting.Directory" ||
                (string)host["path"] != project || (string)host["id"] != (string)request["hostId"] ||
                (string)result["kind"] != "Result" || (string)result["id"] != (string)request["resultId"] ||
                (string)result["workspaceId"] != (string)host["workspaceId"] ||
                !output.StartsWith((string)result["path"] + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The guard probe itself must be an explicitly owned invocation.");
            }
            bool rejected = false;
            try { DistributionEntry.Execute(); }
            catch (ArgumentException) { rejected = (string)request["case"] == "MissingRequest"; }
            catch (InvalidOperationException) { rejected = (string)request["case"] != "MissingRequest"; }
            using (var stream = new FileStream(Path.Combine(output, "guard.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(JsonConvert.SerializeObject(new { rejected, hostId = (string)host["id"],
                    resultId = (string)result["id"], isBatchMode = Application.isBatchMode, pid = System.Diagnostics.Process.GetCurrentProcess().Id }));
            }
            EditorApplication.Exit(rejected ? 0 : 1);
        }
    }
}
'@
    $lock = [IO.File]::ReadAllText((Join-Path $repository 'Packages/packages-lock.json')) | ConvertFrom-Json
    $files = [ordered]@{
        'Packages/manifest.json' = [Text.Encoding]::UTF8.GetBytes((@{ dependencies = @{ 'com.unity.nuget.newtonsoft-json' = $lock.dependencies.'com.unity.nuget.newtonsoft-json'.version } } | ConvertTo-Json -Depth 5))
        'ProjectSettings/ProjectVersion.txt' = [Text.Encoding]::UTF8.GetBytes("m_EditorVersion: $($editor.version)`n")
        'Assets/Editor/DistributionEntry.cs' = [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot '../DistributionEntry.cs'))
        'Assets/Editor/DistributionGuard.cs' = [Text.Encoding]::UTF8.GetBytes($probe)
    }
    $identity = Get-YLTestInputIdentity -Repository $repository -Paths @('Tools/Testing/DistributionEntry.cs', 'Tools/Testing/Tests/Test-YLDistributionGuards.ps1') -Toolchain @($Unity) -Profile "DistributionGuards-$($version.ProductVersion)"
    Sync-YLUnityHost -HostRecord $hostRecord -Files $files -EditorVersion $editor.version -Identity $identity
    foreach ($case in @('MissingRequest', 'WrongOwner', 'Interactive', 'BooleanHostId', 'BooleanResultId', 'BooleanPhase', 'BooleanSchema', 'OldSchema', 'RetiredExport')) {
        $output = Join-Path $run.path $case
        $null = [IO.Directory]::CreateDirectory($output)
        $request = Join-Path $output 'request.json'
        $value = @{ schemaVersion = 2; hostId = $hostRecord.id; resultId = $run.id; resultRoot = $run.path; output = $output; unityVersion = $editor.version; case = $case
            phase = 'Inspect'; backend = 'Mono'; smoke = $false; hybridCLR = $false; packageOrigin = 'File'
            manifestSha256 = (Get-FileHash -LiteralPath (Join-Path $hostRecord.path 'Packages/manifest.json')).Hash
            packages = @(@{ name = 'com.kano.ylframework'; version = '0.1.0-preview.1' }) }
        Write-YLTestJson -Path $request -Value $value
        $arguments = @('-projectPath', $hostRecord.path, '-logFile', (Join-Path $output 'unity.log'), '-executeMethod', 'YLFramework.Testing.DistributionGuard.Run', '-ylGuardRequest', $request)
        if ($case -cne 'Interactive') { $arguments += @('-batchmode', '-nographics') }
        else { $arguments += @('-ylDistributionRequest', $request) }
        if ($case -cnotin @('MissingRequest', 'Interactive')) {
            switch -CaseSensitive ($case) {
                'WrongOwner' { $value.hostId = '00000000000000000000000000000000' }
                'BooleanHostId' { $value.hostId = $true }
                'BooleanResultId' { $value.resultId = $true }
                'BooleanPhase' { $value.phase = $true }
                'BooleanSchema' { $value.schemaVersion = $true }
                'OldSchema' { $value.schemaVersion = 1 }
                'RetiredExport' { $value.phase = 'Export' }
            }
            $wrong = Join-Path $output 'wrong.json'
            Write-YLTestJson -Path $wrong -Value $value
            $arguments += @('-ylDistributionRequest', $wrong)
        }
        $start = [Diagnostics.ProcessStartInfo]::new($Unity)
        $start.UseShellExecute = $false
        $start.WorkingDirectory = $hostRecord.path
        foreach ($arg in $arguments) { $start.ArgumentList.Add($arg) }
        $fact = Invoke-YLTestProcess -StartInfo $start -Log (Join-Path $output 'process.log') -TimeoutMilliseconds 120000
        $summary.processes += $fact
        Assert-YLTestProcessSucceeded $fact
        $proof = [IO.File]::ReadAllText((Join-Path $output 'guard.json')) | ConvertFrom-Json -DateKind String
        if ($proof.rejected -isnot [bool] -or -not $proof.rejected -or $proof.hostId -isnot [string] -or $proof.resultId -isnot [string] -or $proof.isBatchMode -isnot [bool] -or
            $proof.pid -isnot [long] -or $proof.pid -ne $fact.pid -or $proof.hostId -cne $hostRecord.id -or $proof.resultId -cne $run.id -or $proof.isBatchMode -ne ($case -cne 'Interactive')) { throw 'Guard rejection did not return control to the actual owned caller.' }
        $summary.checks++
        "PASS: $case rejection returns without terminating its caller"
    }
    $summary.status = 'Passed'
} catch { $summary.reason = $_.Exception.Message; throw }
finally {
    $summary.endUtc = [DateTime]::UtcNow.ToString('o')
    Write-YLTestJson -Path (Join-Path $run.path 'summary.json') -Value $summary
    $hostLease.Dispose()
    $lease.Dispose()
    "Distribution guard evidence: $($run.path)"
}
