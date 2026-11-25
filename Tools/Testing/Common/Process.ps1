#requires -Version 7.6
#requires -PSEdition Core
. (Join-Path $PSScriptRoot 'Workspace.ps1')

function Initialize-YLTestProcessJob {
    if ('YLTesting.ProcessJob' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace YLTesting
{
    public sealed class ProcessJob : IDisposable
    {
        private readonly SafeFileHandle _handle;

        public ProcessJob()
        {
            _handle = CreateJobObject(IntPtr.Zero, null);
            if (_handle.IsInvalid) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
            if (!SetInformationJobObject(_handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
            {
                var error = new Win32Exception(Marshal.GetLastWin32Error());
                _handle.Dispose();
                throw error;
            }
        }

        public void Assign(Process process)
        {
            if (!AssignProcessToJobObject(_handle, process.SafeHandle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        public uint ActiveProcesses
        {
            get
            {
                if (!QueryInformationJobObject(_handle, 1, out Accounting data, (uint)Marshal.SizeOf<Accounting>(), out _))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                return data.ActiveProcesses;
            }
        }

        public void Terminate()
        {
            if (!TerminateJobObject(_handle, 1)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        }

        public void Dispose() => _handle.Dispose();

        [StructLayout(LayoutKind.Sequential)]
        public struct BasicLimits
        {
            public long ProcessTime, JobTime;
            public uint Flags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IoCounters
        {
            public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Accounting
        {
            public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
            public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits data, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, out Accounting data, uint length, out uint returned);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    }
}
'@
}

function Invoke-YLTestProcess {
    <#
    Returns process facts, never artifact/test acceptance. Only newly started processes are managed.
    ArgumentList is not persisted. Stdout/stderr may contain child diagnostics; callers must not pass secrets.
    Windows jobs contain ordinary CreateProcess descendants after assignment; WMI/breakaway launchers are unsupported.
    Start/assignment is not atomic: assignment failure is an observer error, never a successful fallback.
    Process.Start itself is synchronous. Timeout/cancellation bound observation, not a blocked OS start call.
    Missing terminal evidence means ObserverLost, even if an earlier receipt or artifact looks successful.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][Diagnostics.ProcessStartInfo] $StartInfo,
        [Parameter(Mandatory)][string] $Log,
        [Parameter(Mandatory)][ValidateRange(1, 2147483647)][int] $TimeoutMilliseconds,
        [Threading.CancellationToken] $CancellationToken = [Threading.CancellationToken]::None,
        [scriptblock] $KillWhen
    )
    Set-StrictMode -Version 3.0
    $ErrorActionPreference = 'Stop'
    if (-not $IsWindows) { throw 'Owned test processes require Windows.' }
    $Log = Get-YLTestPath $Log
    $exe = Get-YLTestPath -Path $StartInfo.FileName -ReadOnly
    $null = Get-YLTestPath $StartInfo.WorkingDirectory
    if ($StartInfo.UseShellExecute -or $StartInfo.Arguments.Length -ne 0 -or $StartInfo.RedirectStandardInput) {
        throw 'Use ArgumentList, an explicit working directory and no shell or redirected stdin.'
    }
    if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($Log))) { throw 'The process log parent must exist.' }
    foreach ($path in @($Log, ($Log + '.stderr.log'), ($Log + '.identity.json'), ($Log + '.started.json'), ($Log + '.exit.json'))) {
        $null = Get-YLTestPath $path
        if ([IO.Path]::Exists($path)) { throw "Preserve existing process evidence: $path" }
    }
    Initialize-YLTestProcessJob
    $StartInfo.RedirectStandardOutput = $true
    $StartInfo.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $StartInfo
    $record = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $initial = @{
        schemaVersion = 1; purpose = 'YLTesting.Process'; invocationId = [Guid]::NewGuid().ToString('N')
        observerPid = $PID; pid = $null; exe = $exe; log = $Log; timeoutMilliseconds = $TimeoutMilliseconds
        requestedUtc = [DateTime]::UtcNow.ToString('o'); startUtc = $null; endUtc = $null
        status = 'NotStarted'; observedExit = $false; exitCode = $null; timedOut = $false; cancelled = $false
        forcedCleanup = $false; observerError = $null; cleanupError = $null
        jobAssigned = $false; activeProcesses = $null; streamsDrained = $false
    }
    foreach ($entry in $initial.GetEnumerator()) { $record.Add($entry.Key, $entry.Value) }
    if ($null -ne $KillWhen) { $record['killObservation'] = $null }
    $job = $null
    $stdout = $null
    $stderr = $null
    $tasks = [Collections.Generic.List[Threading.Tasks.Task]]::new()
    $started = $false
    $startAttempted = $false
    $identityWritten = $false
    $killFacts = $null
    $jsonOptions = [Text.Json.JsonSerializerOptions]::new()
    $jsonOptions.WriteIndented = $true
    try {
        Write-YLTestJson -Path ($Log + '.identity.json') -Value $record
        $identityWritten = $true
        $CancellationToken.ThrowIfCancellationRequested()
        $job = [YLTesting.ProcessJob]::new()
        $stdout = [IO.File]::Open($Log, 'CreateNew', 'Write', 'Read')
        $stderr = [IO.File]::Open($Log + '.stderr.log', 'CreateNew', 'Write', 'Read')
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $startAttempted = $true
        $started = $process.Start()
        if (-not $started) { throw 'Process.Start did not create a child.' }
        $handle = $process.SafeHandle
        if ($handle.IsInvalid -or $handle.IsClosed) { throw 'The original child process handle is unavailable.' }
        $record['pid'] = $process.Id
        $record['startUtc'] = $process.StartTime.ToUniversalTime().ToString('o')
        $record['status'] = 'Running'
        $job.Assign($process)
        $record['jobAssigned'] = $true
        $tasks.Add($process.StandardOutput.BaseStream.CopyToAsync($stdout))
        $tasks.Add($process.StandardError.BaseStream.CopyToAsync($stderr))
        Write-YLTestJson -Path ($Log + '.started.json') -Value $record
        while (-not $process.HasExited) {
            $CancellationToken.ThrowIfCancellationRequested()
            if ($null -ne $KillWhen) {
                # The callback observes a fixture signal. Only this controller owns the original process handle.
                $observation = & $KillWhen $process.Id
                if ($null -ne $observation) {
                    if ($process.HasExited) { throw 'Child exited before its observed kill point could be exercised.' }
                    $killFacts = $observation
                    $document = [Text.Json.JsonDocument]::Parse((ConvertTo-Json -InputObject $observation -Depth 8 -Compress))
                    try { $record['killObservation'] = $document.RootElement.Clone() }
                    finally { $document.Dispose() }
                    $process.Kill()
                    if (-not $process.WaitForExit(5000)) { throw 'Observed test kill did not terminate the direct child.' }
                    $record['status'] = 'ObservationKill'
                    break
                }
            }
            $remaining = [long]$TimeoutMilliseconds - $clock.ElapsedMilliseconds
            if ($remaining -le 0) {
                $record['status'] = 'TimedOut'
                $record['timedOut'] = $true
                break
            }
            $null = $process.WaitForExit([int][Math]::Min(100, $remaining))
        }
        $CancellationToken.ThrowIfCancellationRequested()
        if ($record['status'] -ceq 'Running') {
            $record['exitCode'] = $process.ExitCode
            $record['observedExit'] = $true
            if (($process.ExitTime.ToUniversalTime() - $process.StartTime.ToUniversalTime()).TotalMilliseconds -gt $TimeoutMilliseconds) {
                $record['status'] = 'TimedOut'
                $record['timedOut'] = $true
            } else { $record['status'] = if ($process.ExitCode -eq 0) { 'Exited' } else { 'NonZeroExit' } }
        }
    } catch [OperationCanceledException] {
        $record['status'] = 'Cancelled'
        $record['cancelled'] = $true
    } catch [Management.Automation.PipelineStoppedException] {
        $record['status'] = 'Cancelled'
        $record['cancelled'] = $true
    } catch {
        $record['status'] = if ($startAttempted -and -not $started) { 'StartFailed' } else { 'ObserverError' }
        $record['observerError'] = $_.Exception.Message
    } finally {
        # Finalization uses .NET directly so a stopped PowerShell pipeline can still release its owned children.
        if ($record['status'] -ceq 'Running' -or $CancellationToken.IsCancellationRequested -or $PSCmdlet.Stopping) {
            $record['status'] = 'Cancelled'
            $record['cancelled'] = $true
        }
        if ($started) {
            try {
                if ($process.HasExited -and $record['jobAssigned'] -and $record['status'] -cin @('Exited', 'NonZeroExit', 'ObservationKill')) {
                    $grace = [Diagnostics.Stopwatch]::StartNew()
                    while ($job.ActiveProcesses -ne 0 -and $grace.ElapsedMilliseconds -lt 2000) { [Threading.Thread]::Sleep(25) }
                    if ($job.ActiveProcesses -ne 0) { $record['status'] = 'DescendantsAlive' }
                }
                if (-not $process.HasExited -or ($record['jobAssigned'] -and $job.ActiveProcesses -ne 0)) {
                    $record['forcedCleanup'] = $true
                    if ($record['jobAssigned']) { $job.Terminate() }
                    else { $process.Kill($true) }
                    $null = $process.WaitForExit(5000)
                }
                if ($record['jobAssigned']) {
                    $cleanup = [Diagnostics.Stopwatch]::StartNew()
                    while ($job.ActiveProcesses -ne 0 -and $cleanup.ElapsedMilliseconds -lt 5000) { [Threading.Thread]::Sleep(25) }
                    $record['activeProcesses'] = $job.ActiveProcesses
                    if ($record['activeProcesses'] -ne 0) { throw 'Job descendants are still alive after bounded cleanup.' }
                }
                if ($process.HasExited) {
                    $record['observedExit'] = $true
                    $record['exitCode'] = $process.ExitCode
                } else { throw 'The direct child has no observed terminal state.' }
            } catch { $record['cleanupError'] = $_.Exception.Message }
        }
        if ($null -ne $job) {
            try { $job.Dispose() }
            catch { $record['cleanupError'] = [string]$record['cleanupError'] + $_.Exception.Message }
        }
        try {
            if ($tasks.Count -ne 0) {
                $record['streamsDrained'] = [Threading.Tasks.Task]::WaitAll($tasks.ToArray(), 5000)
                if (-not $record['streamsDrained']) { throw 'Output streams did not drain within the cleanup deadline.' }
            }
        } catch { $record['observerError'] = [string]$record['observerError'] + $_.Exception.Message }
        try {
            if ($null -ne $stdout) { $stdout.Dispose() }
            if ($null -ne $stderr) { $stderr.Dispose() }
            $process.Dispose()
        } catch { $record['cleanupError'] = [string]$record['cleanupError'] + $_.Exception.Message }
        if ($CancellationToken.IsCancellationRequested -or $PSCmdlet.Stopping) {
            $record['status'] = 'Cancelled'
            $record['cancelled'] = $true
        }
        $record['endUtc'] = [DateTime]::UtcNow.ToString('o')
        if ($identityWritten) {
            try {
                $terminal = $Log + '.exit.json'
                for ($cursor = $terminal; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
                    if ([IO.Path]::Exists($cursor) -and ([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint)) {
                        throw 'A linked terminal evidence path is refused.'
                    }
                }
                $bytes = [Text.UTF8Encoding]::new($false).GetBytes([Text.Json.JsonSerializer]::Serialize($record, $record.GetType(), $jsonOptions))
                $stream = [IO.File]::Open($terminal, 'CreateNew', 'Write', 'None')
                try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
                finally { $stream.Dispose() }
            } catch {
                $record['status'] = 'ObserverError'
                $record['observerError'] = [string]$record['observerError'] + ' Terminal persistence failed: ' + $_.Exception.Message
            }
        }
    }
    $facts = @{}
    foreach ($entry in $record.GetEnumerator()) { $facts[$entry.Key] = $entry.Value }
    if ($null -ne $KillWhen) { $facts['killObservation'] = $killFacts }
    return [pscustomobject]$facts
}

function Read-YLTestProcessResult {
    param([Parameter(Mandatory)][string] $Log)
    $Log = Get-YLTestPath $Log
    $identity = Get-YLTestPath ($Log + '.identity.json')
    $terminal = Get-YLTestPath ($Log + '.exit.json')
    if (-not [IO.File]::Exists($identity) -or -not [IO.File]::Exists($terminal)) {
        return [pscustomobject]@{ status = 'ObserverLost'; observedExit = $false; exitCode = $null; log = $Log }
    }
    $before = [IO.File]::ReadAllText($identity) | ConvertFrom-Json -AsHashtable -DateKind String
    $after = [IO.File]::ReadAllText($terminal) | ConvertFrom-Json -AsHashtable -DateKind String
    if ($before['purpose'] -cne 'YLTesting.Process' -or $after['purpose'] -cne 'YLTesting.Process' -or
        $after['schemaVersion'] -ne 1 -or $before['invocationId'] -cne $after['invocationId'] -or
        $after['log'] -ine $Log -or $after['status'] -cnotin @('Exited', 'NonZeroExit', 'TimedOut', 'Cancelled', 'StartFailed', 'ObserverError', 'DescendantsAlive', 'ObservationKill')) {
        throw 'Process evidence identity or terminal state is invalid.'
    }
    return [pscustomobject]$after
}
