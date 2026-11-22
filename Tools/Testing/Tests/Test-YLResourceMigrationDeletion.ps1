#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param([Parameter(Mandatory)][string] $TemporaryRoot)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Deletion OS contracts require Windows.' }
. (Join-Path $PSScriptRoot '../Common/ResourceMigration.ps1')
$TemporaryRoot = Get-YLTestPath $TemporaryRoot
if (-not [IO.Directory]::Exists($TemporaryRoot)) { throw 'TemporaryRoot must exist.' }
$sandbox = Join-Path $TemporaryRoot ('yl-delete-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($sandbox)
$failures = [Collections.Generic.List[string]]::new()
$script:cases = 0

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
// Test-only copy of the old verified-stream/path-delete mechanism, for a deterministic RED.
public sealed class YLLegacyDeletionSubject : IDisposable
{
    private readonly string path;
    public FileStream Stream { get; }
    public YLLegacyDeletionSubject(string path, string expected)
    {
        this.path=path;
        Stream=File.Open(path,FileMode.Open,FileAccess.ReadWrite,FileShare.Delete);
        if(Convert.ToHexString(SHA256.HashData(Stream)).ToLowerInvariant()!=expected) { Stream.Dispose(); throw new IOException("SHA mismatch"); }
        Stream.Position=0;
    }
    public void MarkForDeletion() { File.Delete(path); }
    public void Dispose() { Stream.Dispose(); }
    public void VerifyFinalAbsence() { if(File.Exists(path)) throw new IOException("The target name still exists."); }
}
public static class YLDeletionOsTests
{
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, ref SecurityAttributes security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr value);
    public static string CreateUnreadableFixture(string path)
    {
        // New Temp file only: real read-data denial, not an ACL-based production exclusion mechanism.
        string sid=WindowsIdentity.GetCurrent().User.Value;
        if(!ConvertStringSecurityDescriptorToSecurityDescriptorW("D:P(D;;0x1;;;"+sid+")(A;;FA;;;"+sid+")",1,out IntPtr descriptor,out uint size)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var security=new SecurityAttributes { Length=Marshal.SizeOf<SecurityAttributes>(), Descriptor=descriptor };
            using(var handle=CreateFileW(path,0x40000000,0,ref security,1,0x80,IntPtr.Zero))
            {
                if(handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                using(var stream=new FileStream(handle,FileAccess.Write))
                {
                    byte[] data=Encoding.UTF8.GetBytes("known unreadable fixture bytes");
                    stream.Write(data); stream.Flush(true);
                    return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
                }
            }
        }
        finally { LocalFree(descriptor); }
    }
    public static int TryRename(string source,string target)
    {
        try { File.Move(source,target); return 0; }
        catch(IOException error) { return error.HResult & 65535; }
    }
    public static int TryRenameDirectory(string source,string target)
    {
        try { Directory.Move(source,target); return 0; }
        catch(IOException error) { return error.HResult & 65535; }
    }
}
'@
function Assert-That([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Assert-Rejected([scriptblock] $Body, [string] $Pattern) {
    try { $null = & $Body } catch { if ($_.Exception -is [Management.Automation.CommandNotFoundException] -or $_.Exception.Message -notmatch $Pattern) { throw }; return }
    throw "Expected rejection: $Pattern"
}
function Assert-NativeError([scriptblock] $Body, [int] $Code) {
    try { $null = & $Body } catch {
        for ($error = $_.Exception; $null -ne $error; $error = $error.InnerException) {
            if ($error -is [ComponentModel.Win32Exception] -and $error.NativeErrorCode -eq $Code) { return }
        }
        throw
    }
    throw "Expected actual Win32 error $Code."
}
function Test-Case([string] $Name, [scriptblock] $Body) {
    $script:cases++
    try { & $Body; "PASS: $Name" } catch { $failures.Add("${Name}: $($_.Exception.Message)`n$($_.ScriptStackTrace)"); "FAIL: $($failures[$failures.Count - 1])" }
}
function New-Fixture([string] $Name = 'target.asset') {
    $directory = Join-Path $sandbox ([Guid]::NewGuid().ToString('N'))
    $null = [IO.Directory]::CreateDirectory($directory)
    $path = Join-Path $directory $Name
    [IO.File]::WriteAllText($path, "guid: 11111111111111111111111111111111`noriginal bytes`n")
    return @{ path = $path; directory = $directory; sha = (Get-YLResourceMigrationHash $path); guid = ('1' * 32) }
}
function Open-Subject([object] $Fixture, [string] $Guid = '') {
    if (Get-Command Open-YLResourceMigrationDeletion -ErrorAction SilentlyContinue) {
        return Open-YLResourceMigrationDeletion -Path $Fixture.path -Sha256 $Fixture.sha -Guid $Guid
    }
    # Before the fix this executes exactly the old sharing/deletion semantics, not a missing-API error.
    return [YLLegacyDeletionSubject]::new($Fixture.path, $Fixture.sha)
}
function New-ApplyFixture {
    $source = Join-Path $sandbox ([Guid]::NewGuid().ToString('N'))
    $artifact = Join-Path $sandbox ([Guid]::NewGuid().ToString('N'))
    $null = [IO.Directory]::CreateDirectory((Join-Path $source 'Assets/Catalog'))
    $null = [IO.Directory]::CreateDirectory($artifact)
    $asset = Join-Path $source 'Assets/Catalog/Project.asset'
    [IO.File]::WriteAllText($asset, 'known author data')
    [IO.File]::WriteAllText(($asset + '.meta'), "guid: $('3' * 32)`n")
    $entries = @('Assets/Catalog/Project.asset', 'Assets/Catalog/Project.asset.meta') | ForEach-Object {
        [ordered]@{ path = $_; originalSha256 = Get-YLResourceMigrationHash (Join-Path $source $_); guid = '3' * 32; kind = 'Project' }
    }
    $retirement = @{ deleteEnabled = $true; entries = @($entries) }
    $operations = @(New-YLCandidateRetirementDeletes @{ retirement = $retirement })
    $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject @($operations) -Depth 40 -Compress)))).ToLowerInvariant()
    $manifestPath = Join-Path $artifact 'migration.json'
    Write-YLTestJson -Path $manifestPath -Value @{
        schemaVersion = 1; purpose = 'YLResource.AddressablesMigrationManifest'; repository = $source; previewSha256 = ('d' * 64); outputDigest = $digest
        hostId = ('a' * 32); inputHash = ('b' * 64); unityVersion = '6000.3.27f1'; addressablesVersion = '2.10.3'
        inputs = @(); folders = @(); operations = @($operations)
        retirement = $retirement
    }
    $manifestSha = Get-YLResourceMigrationHash $manifestPath
    $validation = Join-Path $artifact 'fixture-validation.json'
    $nativeProof = Join-Path $artifact 'native-proof.json'
    # File-operation fixture only, not a real native or Source Apply receipt.
    Write-YLTestJson -Path $nativeProof -Value @{
        schemaVersion = 1; purpose = 'YLResource.CandidateNativeProof'; status = 'Passed'; runtimeVerified = $true
        hostId = ('c' * 32); resultId = ('d' * 32); inputHash = ('e' * 64); manifestSha256 = $manifestSha
        subjectHostId = ('a' * 32); subjectInputHash = ('b' * 64); previewSha256 = ('d' * 64); outputDigest = $digest
        unityVersion = '6000.3.27f1'; addressablesVersion = '2.10.3'; addressablesSource = 'Registry'
        checks = @('OfficialContentLoad', 'ConsumerColdRead', 'ForeignSettingsPreserved'); cleanupVerified = $true; returnedToEditMode = $true
        retired = @($entries | ForEach-Object {
            @{ path = $_.path; originalSha256 = $_.originalSha256; guid = $_.guid; kind = $_.kind; absent = $true; passed = $true }
        })
    }
    Write-YLTestJson -Path $validation -Value @{
        schemaVersion = 1; purpose = 'YLResource.AddressablesMigrationValidation'; status = 'Passed'; previewSha256 = ('d' * 64); outputDigest = $digest; runtimeVerified = $true
        manifestSha256 = $manifestSha; hostId = ('a' * 32); inputHash = ('b' * 64); unityVersion = '6000.3.27f1'; addressablesVersion = '2.10.3'
        validationHostId = ('c' * 32); validationResultId = ('d' * 32); validationInputHash = ('e' * 64)
        nativeProof = $nativeProof; nativeProofSha256 = Get-YLResourceMigrationHash $nativeProof
        checks = @('OfficialContentLoad', 'ConsumerColdRead', 'ForeignSettingsPreserved')
        process = @{ status = 'Exited'; observedExit = $true; exitCode = 0; timedOut = $false; cancelled = $false; observerError = $null; cleanupError = $null; forcedCleanup = $false; streamsDrained = $true; jobAssigned = $true; activeProcesses = 0 }
    }
    return @{ source = $source; asset = $asset; artifact = $artifact; arguments = @{ Repository = $source; Manifest = $manifestPath; ManifestSha256 = (Get-YLResourceMigrationHash $manifestPath); ValidationReceipt = $validation; ValidationSha256 = (Get-YLResourceMigrationHash $validation); ReceiptDirectory = (Join-Path $artifact 'Receipts'); ExclusiveWindowConfirmed = $true } }
}
try {
    Test-Case 'A verified delete must not unlink a foreign identity added under the original name' {
        $f = New-Fixture
        $lease = Open-Subject $f
        $renamed = $f.path + '.moved'
        $rename = [YLDeletionOsTests]::TryRename($f.path, $renamed)
        $foreignRemoved = $false
        try {
            if ($rename -eq 0) { [IO.File]::WriteAllText($f.path, 'foreign identity and unknown bytes') }
            $lease.MarkForDeletion()
        } finally { $lease.Dispose() }
        if ($rename -eq 0) { $foreignRemoved = -not [IO.File]::Exists($f.path) }
        Assert-That (-not $foreignRemoved -and $rename -eq 32) 'RED: verified old handle allowed rename and path-delete removed a foreign same-name identity.'
        $lease.VerifyFinalAbsence()
    }
    Test-Case 'Same-SHA identity replacement cannot detach the verified file during the deletion lease' {
        $f = New-Fixture
        $original = [IO.File]::ReadAllBytes($f.path)
        $lease = Open-Subject $f
        $rename = [YLDeletionOsTests]::TryRename($f.path, ($f.path + '.same-sha-original'))
        try {
            if ($rename -eq 0) { [IO.File]::WriteAllBytes($f.path, $original) }
            Assert-That ($rename -eq 32) 'A different fileID could replace the same path even with identical SHA.'
        } finally { $lease.Dispose() }
        Assert-That ([IO.File]::Exists($f.path)) 'Closing an unmarked lease deleted source.'
    }
    Test-Case 'Pending deletion is not final absence and no new same-name file can be added before close' {
        $f = New-Fixture
        $lease = Open-Subject $f $f.guid
        try {
            $lease.MarkForDeletion()
            Assert-That ($lease.IsDeletePending) 'The native delete-pending state was not verified.'
            Assert-Rejected { $lease.MarkForDeletion() } 'marked'
            Assert-Rejected { $lease.VerifyFinalAbsence() } 'close|closed|pending'
            Assert-Rejected { [IO.File]::Open($f.path, 'CreateNew', 'Write', 'None').Dispose() } 'exists|denied|process|access|拒绝|存在'
        } finally { $lease.Dispose() }
        $lease.VerifyFinalAbsence()
        Assert-That (-not [IO.File]::Exists($f.path)) 'Delete did not finalize when the verified handle closed.'
    }
    Test-Case 'A parent namespace change cannot make a same-SHA substitute the deletion victim' {
        $f = New-Fixture
        $bytes = [IO.File]::ReadAllBytes($f.path)
        $lease = Open-Subject $f
        $moved = $f.directory + '.moved'
        $rename = [YLDeletionOsTests]::TryRenameDirectory($f.directory, $moved)
        try {
            if ($rename -eq 0) {
                $null = [IO.Directory]::CreateDirectory($f.directory)
                [IO.File]::WriteAllBytes($f.path, $bytes)
                Assert-Rejected { $lease.MarkForDeletion() } 'identity|name'
                Assert-That ((Get-YLResourceMigrationHash $f.path) -ceq $f.sha) 'A same-SHA foreign name was deleted.'
            } else {
                Assert-That ($rename -in @(5, 32)) "Unexpected parent rename error: $rename"
                Assert-That ([IO.Directory]::Exists($f.directory) -and -not [IO.Directory]::Exists($moved)) 'A denied rename changed the parent namespace.'
            }
        } finally { $lease.Dispose() }
        Assert-That ([IO.File]::Exists($f.path)) 'A failed parent-identity check removed the current name.'
    }
    Test-Case 'Dirty GUID and stale SHA fail without deletion or a leaked handle' {
        $f = New-Fixture
        Assert-Rejected { $lease = Open-Subject $f ('9' * 32); try { $lease.MarkForDeletion() } finally { $lease.Dispose() } } 'GUID'
        $f.sha = '0' * 64
        Assert-Rejected { $lease = Open-Subject $f; try { $lease.MarkForDeletion() } finally { $lease.Dispose() } } 'SHA'
        $stream = [IO.File]::Open($f.path, 'Open', 'ReadWrite', 'None'); $stream.Dispose()
        Assert-That ([IO.File]::Exists($f.path)) 'Rejected verification deleted the target.'
    }
    Test-Case 'An existing reader with generous sharing still blocks the exclusive delete lease' {
        $f = New-Fixture
        $reader = [IO.File]::Open($f.path, 'Open', 'Read', ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        try { Assert-NativeError { $lease = Open-Subject $f; $lease.Dispose() } 32 }
        finally { $reader.Dispose() }
        $lease = Open-Subject $f; $lease.Dispose()
        Assert-That ([IO.File]::Exists($f.path)) 'Failed acquisition deleted source.'
    }
    Test-Case 'Read-only target is rejected without clearing attributes or deleting any bytes' {
        $f = New-Fixture
        [IO.File]::SetAttributes($f.path, [IO.FileAttributes]::ReadOnly)
        try {
            Assert-Rejected { $lease = Open-Subject $f; try { $lease.MarkForDeletion() } finally { $lease.Dispose() } } 'denied|access|read.only|只读|拒绝'
            Assert-That ([IO.File]::Exists($f.path) -and ([IO.File]::GetAttributes($f.path) -band [IO.FileAttributes]::ReadOnly) -ne 0) 'The helper changed target attributes.'
        } finally { [IO.File]::SetAttributes($f.path, [IO.FileAttributes]::Normal) }
    }
    Test-Case 'Double Mark/close does not delete a later same-name file' {
        $f = New-Fixture
        $lease = Open-Subject $f
        $lease.MarkForDeletion(); $lease.Dispose(); $lease.VerifyFinalAbsence()
        [IO.File]::WriteAllText($f.path, 'later foreign identity')
        Assert-Rejected { $lease.MarkForDeletion() } 'disposed|closed|marked'
        $lease.Dispose()
        Assert-Rejected { $lease.VerifyFinalAbsence() } 'exists|identity|conflict|present'
        Assert-That ([IO.File]::ReadAllText($f.path) -ceq 'later foreign identity') 'A closed delete lease removed a new path identity.'
    }
    Test-Case 'Long Unicode and literal bracket paths use the same verified handle contract' {
        $f = New-Fixture
        $longDirectory = Join-Path $f.directory (('long-' * 20) + 'one')
        $longDirectory = Join-Path $longDirectory (('path-' * 20) + 'two')
        $null = [IO.Directory]::CreateDirectory($longDirectory)
        $name = 'literal[' + [char]0x6D4B + [char]0x8BD5 + '].asset'
        $target = Join-Path $longDirectory $name
        [IO.File]::Move($f.path, $target); $f.path = $target
        Assert-That ($target.Length -gt 260) 'The fixture did not exercise a long path.'
        $lease = Open-Subject $f $f.guid
        try { $lease.MarkForDeletion() } finally { $lease.Dispose() }
        $lease.VerifyFinalAbsence()
    }
    Test-Case 'Actual read-data denial is propagated without attribute changes or a Delete fallback' {
        $directory = Join-Path $sandbox ([Guid]::NewGuid().ToString('N'))
        $null = [IO.Directory]::CreateDirectory($directory)
        $path = Join-Path $directory 'unreadable.asset'
        $sha = [YLDeletionOsTests]::CreateUnreadableFixture($path)
        Assert-Rejected { $null = [IO.File]::ReadAllBytes($path) } 'access|denied|拒绝'
        Assert-NativeError { $lease = Open-Subject @{ path = $path; sha = $sha }; $lease.Dispose() } 5
        Assert-That ([IO.File]::Exists($path)) 'An unreadable target was removed.'
    }
    Test-Case 'Pre-delete exceptions leave source and metadata intact and release the verified handle' {
        $f = New-ApplyFixture; $arguments = $f.arguments
        $original = Join-Path $arguments.ReceiptDirectory 'Original/Assets/Catalog/Project.asset'
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($original))
        Write-YLTestJson -Path (Join-Path $arguments.ReceiptDirectory 'owner.json') -Value @{ purpose = 'YLResource.MigrationApplyReceipts'; repository = $f.source; manifestSha256 = $arguments.ManifestSha256 }
        [IO.File]::WriteAllText($original, 'foreign backup bytes')
        Assert-Rejected { Invoke-YLResourceMigrationApply @arguments } 'original bytes changed'
        Assert-That ([IO.File]::ReadAllText($f.asset) -ceq 'known author data') 'A failed backup verification marked the source for deletion.'
        $stream = [IO.File]::Open($f.asset, 'Open', 'ReadWrite', 'None'); $stream.Dispose()
        Assert-That (-not [IO.File]::Exists((Join-Path $arguments.ReceiptDirectory '000000.json'))) 'A failed pre-delete step created a completion receipt.'
    }
    Test-Case 'Only finalized deletes receive receipts and an ordinary partial prefix resumes safely' {
        $f = New-ApplyFixture; $arguments = $f.arguments
        [IO.File]::SetAttributes(($f.asset + '.meta'), [IO.FileAttributes]::ReadOnly)
        try { Assert-Rejected { Invoke-YLResourceMigrationApply @arguments } 'denied|access|拒绝' }
        finally { [IO.File]::SetAttributes(($f.asset + '.meta'), [IO.FileAttributes]::Normal) }
        Assert-That (-not [IO.File]::Exists($f.asset) -and [IO.File]::Exists($f.asset + '.meta')) 'The known partial Delete prefix was not retained.'
        Assert-That ([IO.File]::Exists((Join-Path $arguments.ReceiptDirectory '000000.json')) -and -not [IO.File]::Exists((Join-Path $arguments.ReceiptDirectory '000001.json'))) 'Pending or failed metadata deletion was reported complete.'
        $result = Invoke-YLResourceMigrationApply @arguments
        Assert-That ($result.completed -eq 2 -and $result.status -ceq 'AppliedAwaitingColdVerify') 'A known finalized prefix did not resume.'
        $meta = Join-Path $arguments.ReceiptDirectory 'Original/Assets/Catalog/Project.asset.meta'
        Assert-That ((Get-YLResourceMigrationGuid $meta) -ceq ('3' * 32)) 'Original metadata bytes or GUID were lost.'
    }
    Test-Case 'Source UnityLockfile remains an unconditional refusal even with the confirmation boolean' {
        $f = New-ApplyFixture; $arguments = $f.arguments
        $null = [IO.Directory]::CreateDirectory((Join-Path $f.source 'Temp'))
        [IO.File]::WriteAllText((Join-Path $f.source 'Temp/UnityLockfile'), 'owned unit fixture only')
        Assert-Rejected { Invoke-YLResourceMigrationApply @arguments } 'Unity lock'
        Assert-That ([IO.File]::Exists($f.asset) -and -not [IO.Directory]::Exists($arguments.ReceiptDirectory)) 'The source Editor hard gate allowed any mutation.'
    }
} finally { if ([IO.Directory]::Exists($sandbox)) { [IO.Directory]::Delete($sandbox, $true) } }
if ($failures.Count) { throw ($failures -join [Environment]::NewLine) }
"Resource migration deletion checks passed: $script:cases"
