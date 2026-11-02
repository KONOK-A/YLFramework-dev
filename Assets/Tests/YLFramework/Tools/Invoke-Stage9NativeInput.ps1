#requires -Version 7.6
#requires -PSEdition Core
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $DeliveryRoot,
    [Parameter(Mandatory)][ValidatePattern('^(Receiver[A-Z][0-9]*|[a-z][0-9]?)$')][string] $Receiver,
    [Parameter(Mandatory)][ValidateSet('2022.3.62f3c1','6000.3.27f1')][string] $ExpectedUnityVersion,
    [Parameter(Mandatory)][string] $BuildDirectory,
    [Parameter(Mandatory)][ValidateSet('ExpectedLegacy','ExpectedNew','ExpectedBoth')][string] $Mode,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9-]{1,32}$')][string] $BuildRun,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9-]{1,48}$')][string] $Run,
    [ValidateSet('Validate','Run')][string] $Operation = 'Validate',
    [ValidateSet('Basic','Capture','Remaining')][string] $Suite = 'Basic',
    [ValidateRange(30,180)][int] $TimeoutSeconds = 90,
    [switch] $ActivateOwnedWindow,
    [ValidateRange(0,15)][int] $WaitForOwnedForegroundSeconds = 0
)
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
if ($ActivateOwnedWindow -and $Operation -cne 'Run') { throw 'Foreground activation requires explicit Run.' }
if ($WaitForOwnedForegroundSeconds -and $Operation -cne 'Run') { throw 'Foreground wait requires explicit Run.' }
if ($ActivateOwnedWindow -and $WaitForOwnedForegroundSeconds) { throw 'Choose one foreground strategy: authorized activation or manual foreground wait.' }
if (-not $IsWindows -or $PSVersionTable.PSVersion.Major -ne 7 -or $PSVersionTable.PSVersion.Minor -ne 6) {
    throw 'Use Windows PowerShell Core 7.6.x.'
}
function Full-Path([string] $Path) {
    if ($Path -cnotmatch '^[A-Za-z]:[\\/]' -or -not [IO.Path]::IsPathFullyQualified($Path) -or $Path.IndexOf(':', 2) -ge 0) {
        throw 'A local fully qualified absolute path without alternate streams is required.'
    }
    [IO.Path]::GetFullPath($Path).TrimEnd('\')
}
function Reject-Link([string] $Path) {
    for ($cursor=$Path; $cursor; $cursor=[IO.Path]::GetDirectoryName($cursor)) {
        if ([IO.Path]::Exists($cursor) -and ([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Linked path rejected: $cursor"
        }
    }
}
function Read-Json([string] $Path) {
    Reject-Link $Path
    Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -DateKind String
}
function Hash([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Write-NewJson([string] $Path, $Value) {
    Reject-Link $Path
    if ([IO.Path]::Exists($Path)) { throw "Preserve existing evidence: $Path" }
    $bytes=[Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $Value -Depth 40))
    $pending=$Path+'.pending'
    $stream=[IO.File]::Open($pending, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes); $stream.Flush($true) } finally { $stream.Dispose() }
    [IO.File]::Move($pending,$Path)
}
function Files-WithoutLinks([string] $Path) {
    $pending=[Collections.Generic.Stack[string]]::new()
    $pending.Push($Path)
    while ($pending.Count) {
        $current=$pending.Pop()
        Reject-Link $current
        if ([IO.Directory]::Exists($current)) {
            foreach ($child in [IO.Directory]::EnumerateFileSystemEntries($current)) { $pending.Push($child) }
        } else { $current }
    }
}
$root=Full-Path $DeliveryRoot
$build=Full-Path $BuildDirectory
Reject-Link $root
if ($root -match '(?i)(^|[\\/])CGJ2024([\\/]|$)') { throw 'Source is never an owned native-input delivery.' }
$owner=Read-Json (Join-Path $root 'owner.json')
if ($owner.purpose -cne 'YLFramework-Stage9-independent-delivery' -or (Full-Path $owner.root) -cne $root) {
    throw 'Explicit matching delivery owner required.'
}
$repository=Full-Path $owner.repository
if ($root -ieq $repository -or $root.StartsWith($repository+'\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Source/worktree is never a delivery owner root.'
}
$project=Join-Path $root $Receiver
Reject-Link $project
$identity=Read-Json (Join-Path $project 'stage9-owner.json')
if ($identity.Purpose -cne 'Stage9-independent-receiver' -or $identity.Project -cne $project -or $identity.Delivery -cne $root) {
    throw 'Receiver owner mismatch.'
}
$ugui=if ($ExpectedUnityVersion -ceq '6000.3.27f1') { '2.0.0' } else { '1.0.0' }
if ($identity.UnityVersion -cne $ExpectedUnityVersion -or $identity.UguiVersion -cne $ugui) {
    throw 'Receiver version/dependency mismatch.'
}
$versionPath=Join-Path $project 'ProjectSettings/ProjectVersion.txt'
Reject-Link $versionPath
if ((Get-Content -LiteralPath $versionPath -Raw) -cnotmatch ('(?m)^m_EditorVersion: '+[regex]::Escape($ExpectedUnityVersion)+'\r?$')) {
    throw 'Receiver ProjectVersion mismatch.'
}
if ($identity.Package -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]*\.unitypackage$') { throw 'Invalid owned package filename.' }
$packagePath=Join-Path $root $identity.Package
Reject-Link $packagePath
if ($identity.PackageSha256 -cnotmatch '^[0-9a-f]{64}$' -or (Hash $packagePath) -cne $identity.PackageSha256) {
    throw 'Package hash differs from source owner.'
}
Reject-Link $build
# The path identity is checked before a report is opened, including when the supplied leaf does not exist.
$candidates=@('Mono','Il2Cpp' | ForEach-Object { Join-Path $project ('P/NativeInput/'+$_+'/'+$Mode+'/'+$BuildRun) })
if ($candidates -cnotcontains $build) { throw 'Build directory must match receiver/backend/inputmode/BuildRun exactly.' }
$reportPath=Join-Path $build 'BuildReport.json'
$report=Read-Json $reportPath
$backendDirectory=if ($report.Backend -ceq 'Mono2x') { 'Mono' } elseif ($report.Backend -ceq 'IL2CPP') { 'Il2Cpp' } else { '' }
if ($report.Schema -cne 'Stage9NativeInput-v1' -or $report.Project -cne $project -or $report.Delivery -cne $root -or
    $report.Unity -cne $ExpectedUnityVersion -or $report.UguiVersion -cne $ugui -or $report.PackageSha256 -cne $identity.PackageSha256 -or
    $report.Mode -cne $Mode -or $report.BuildRun -cne $BuildRun -or $report.Target -cne 'StandaloneWindows64' -or
    -not $backendDirectory -or $build -cne (Join-Path $project ('P/NativeInput/'+$backendDirectory+'/'+$Mode+'/'+$BuildRun))) {
    throw 'Build identity differs from requested native-input owner.'
}
if ($report.Result -cne 'Succeeded' -or $report.Errors -ne 0 -or $report.Development -isnot [bool] -or $report.Development -or $report.Options -cne 'None') {
    throw 'A succeeded non-Development BuildReport with BuildOptions.None is required.'
}
if (($report.Backend -ceq 'Mono2x' -and $report.Stripping -cne 'Disabled') -or
    ($report.Backend -ceq 'IL2CPP' -and ($report.Stripping -cne 'Low' -or $report.CppConfiguration -cne 'Release'))) {
    throw 'Incorrect backend stripping/C++ Release configuration.'
}
if ($report.BuildGuid -cnotmatch '^[0-9a-f]{32}$' -or $report.BuildGuid -ceq ('0'*32)) { throw 'Invalid exact BuildGuid.' }
if ($Suite -ceq 'Capture' -and (-not $report.ContainsKey('CaptureProtocol') -or $report.CaptureProtocol -cne 'B06Capture-v1')) {
    throw 'Capture protocol B06Capture-v1 requires a newly generated capable build; frozen basic builds cannot be rebound.'
}
if ($Suite -ceq 'Remaining' -and (-not $report.ContainsKey('RemainingProtocol') -or $report.RemainingProtocol -cne 'B06Remaining-v1')) {
    throw 'Remaining protocol B06Remaining-v1 requires a newly generated capable build.'
}
if ($report.LegacyInput -isnot [bool] -or $report.NewInput -isnot [bool] -or
    $report.LegacyInput -ne ($Mode -cne 'ExpectedNew') -or $report.NewInput -ne ($Mode -cne 'ExpectedLegacy')) {
    throw 'Actual input macro identity mismatch.'
}
if ($report.SettingsRestored -ne $true -or $report.PlayerSettingsBeforeSha256 -cnotmatch '^[0-9a-f]{64}$' -or
    $report.PlayerSettingsBeforeSha256 -cne $report.PlayerSettingsAfterSha256) { throw 'Exact build settings restoration was not established.' }
if ($report.NewInput -and $report.InputSystemVersion -cne $(if ($ExpectedUnityVersion -ceq '6000.3.27f1') { '1.20.1' } else { '1.14.2' })) {
    throw 'Actual Input System version differs from the fixed engine contract.'
}
if ($report.Executable -cne 'Stage9NativeInput.exe' -or @($report.PlayerFiles).Count -eq 0) { throw 'PlayerFiles/executable inventory missing.' }
$listed=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $report.PlayerFiles) {
    if ($file.Path -isnot [string] -or $file.Path -cmatch '(^|/)\.\.?(/|$)|[\\:]|^/|[. ]$' -or
        [string]::IsNullOrWhiteSpace($file.Path) -or $file.Path -ceq 'BuildReport.json') { throw 'Invalid PlayerFiles path.' }
    if (@($file.Path.Split('/') | Where-Object { -not $_ -or $_ -match '[. ]$' }).Count) { throw 'Ambiguous Windows PlayerFiles path component.' }
    if (-not $listed.Add($file.Path)) { throw 'Duplicate PlayerFiles path.' }
    $path=Join-Path $build $file.Path
    Reject-Link $path
    if ($file.Sha256 -cnotmatch '^[0-9a-f]{64}$' -or -not [IO.File]::Exists($path) -or
        ([IO.FileInfo]$path).Length -ne $file.Length -or (Hash $path) -cne $file.Sha256) { throw "PlayerFiles hash/length mismatch: $($file.Path)" }
}
if (-not $listed.Contains($report.Executable)) { throw 'PlayerFiles must include the exact executable.' }
foreach ($path in @(Files-WithoutLinks $build)) {
    $relative=[IO.Path]::GetRelativePath($build,$path).Replace('\','/')
    if ($relative -cne 'BuildReport.json' -and -not $listed.Contains($relative)) { throw "Unlisted Player file: $relative" }
}
$scriptingRelative='Stage9NativeInput_Data/ScriptingAssemblies.json'
if (-not $listed.Contains($scriptingRelative)) { throw 'Closed scripting assembly inventory required.' }
$scripting=Read-Json (Join-Path $build $scriptingRelative)
if (-not $scripting.ContainsKey('names') -or $scripting.names -isnot [array] -or $scripting.names.Count -eq 0) {
    throw 'Invalid scripting assembly list.'
}
$runtimeNames=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($name in $scripting.names) {
    if ($name -isnot [string] -or $name -cnotmatch '^[A-Za-z0-9_.-]+\.dll$' -or -not $runtimeNames.Add($name)) {
        throw 'Invalid scripting assembly list.'
    }
    if ($name -match '(^UnityEditor[.]|(^|[.])(Tests?|Acceptance|Editor)([.]|$)|-Editor[.]dll$|^UnityEngine[.]TestRunner[.]|^nunit[.]|^Microsoft[.]CodeAnalysis[.]|Workspaces|Analyzers?[.])') {
        throw ('Production native input cannot include test, acceptance, Analyzer or Editor assemblies: '+$name)
    }
}
$runDirectory=Join-Path $root ('Runs/'+$Receiver+'-native-'+$Mode+'-'+$Run)
Reject-Link $runDirectory
if ([IO.Path]::Exists($runDirectory)) { throw 'Preserve existing run; choose a new Run.' }
$exe=Join-Path $build $report.Executable
$playerActivation=$report.ContainsKey('PlayerActivationProtocol')
if ($playerActivation -and $report.PlayerActivationProtocol -cne 'B06PlayerActivation-v1') { throw 'Unknown Player activation protocol.' }
$reportHash=Hash $reportPath
if ($Operation -ceq 'Validate') {
    [pscustomobject]@{ ValidationOnly=$true; InputInjected=$false; BuildDirectory=$build; BuildGuid=$report.BuildGuid
        Backend=$report.Backend; Mode=$Mode; BuildReportSha256=$reportHash; RunDirectory=$runDirectory }
    return
}

# Validate never reaches Win32, process enumeration, process creation, or filesystem writes.
if ($root -cne 'G:\Unity\U63-20261027-c63a1') { throw 'Run is restricted to this batch ownedRoot.' }
$trees=@(& git -C $repository worktree list --porcelain)
if ($LASTEXITCODE -ne 0) { throw 'Cannot establish source worktree protection.' }
foreach ($line in $trees) {
    if ($line.StartsWith('worktree ')) {
        $tree=Full-Path $line.Substring(9)
        if ($project -ieq $tree -or $project.StartsWith($tree+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Receiver is a worktree.' }
    }
}
if (@(Get-CimInstance Win32_Process -Filter "Name = 'Stage9NativeInput.exe'").Count) {
    throw 'Another native-input Player exists; never adopt or start a second one.'
}
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public static class Stage9NativeWin32
{
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public UIntPtr ExtraInfo;
    }
    // INPUT is 40 bytes on x64: DWORD + 4-byte alignment + the 32-byte mouse union.
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public MouseInput Mouse; }
    [StructLayout(LayoutKind.Explicit, Size=40)] private struct KeyInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort VirtualKey;
        [FieldOffset(10)] public ushort ScanCode;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(16)] public uint Time;
        [FieldOffset(24)] public UIntPtr ExtraInfo;
    }
    private delegate bool EnumProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder value, int capacity);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", EntryPoint="MapVirtualKeyW")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll", SetLastError=true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", EntryPoint="SendInput", SetLastError=true)] private static extern uint SendKeyInput(uint count, KeyInput[] inputs, int size);

    public sealed class Evidence
    {
        public long Hwnd;
        public int Pid, ClientWidth, ClientHeight, ScreenX, ScreenY, ClientX, ClientY;
        public int VirtualLeft, VirtualTop, VirtualWidth, VirtualHeight, AbsoluteX, AbsoluteY;
        public uint Dpi, Flags, Inserted;
        public int VirtualKey;
        public uint ScanCode;
        public double KeyboardX, KeyboardY;
        public string Phase, BeforeUtc, AfterUtc;
        public bool Foreground, CursorMatched, ForegroundRequestMade, ForegroundRequestAccepted;
    }
    private static string ClassName(IntPtr window)
    {
        var value=new StringBuilder(256);
        if (GetClassName(window,value,value.Capacity)==0) throw new InvalidOperationException("Cannot read owned HWND class.");
        return value.ToString();
    }
    private static IntPtr OwnedWindow(int pid, long expectedWindow)
    {
        var windows=new List<IntPtr>();
        EnumProc enumerate=(window,unused)=>
        {
            uint owner;
            GetWindowThreadProcessId(window,out owner);
            if (owner==(uint)pid && IsWindowVisible(window) && ClassName(window)=="UnityWndClass") windows.Add(window);
            return true;
        };
        if (!EnumWindows(enumerate,IntPtr.Zero) || windows.Count!=1) throw new InvalidOperationException("Exactly one visible owned Unity HWND required.");
        var target=windows[0];
        uint actualPid;
        GetWindowThreadProcessId(target,out actualPid);
        if ((expectedWindow!=0 && target.ToInt64()!=expectedWindow) || actualPid!=(uint)pid || !IsWindow(target) ||
            !IsWindowEnabled(target) || IsIconic(target)) throw new InvalidOperationException("Invalid, minimized, changed or disabled owned HWND; no input sent.");
        return target;
    }
    public static bool ReadyForInput(int pid, ref long expectedWindow)
    {
        var target=OwnedWindow(pid,expectedWindow);
        expectedWindow=target.ToInt64();
        if (GetForegroundWindow()!=target) return false;
        for (int key=1; key<255; key++) if (GetAsyncKeyState(key)<0) return false;
        return true;
    }
    public static long CursorPosition()
    {
        var previous=SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (previous==IntPtr.Zero) throw new InvalidOperationException("Per-monitor-v2 coordinate context unavailable.");
        try
        {
            Point point;
            if (!GetCursorPos(out point)) throw new InvalidOperationException("Cannot observe cursor position.");
            return ((long)point.X<<32) | (uint)point.Y;
        }
        finally
        {
            if (SetThreadDpiAwarenessContext(previous)==IntPtr.Zero) throw new InvalidOperationException("Failed to restore coordinate context.");
        }
    }
    public static Evidence Inject(int pid, long expectedWindow, int width, int height, double x, double y, string phase, bool activateOwnedWindow)
    {
        return InjectPointer(pid, expectedWindow, width, height, x, y, phase, activateOwnedWindow, 0);
    }
    public static Evidence InjectPointer(int pid, long expectedWindow, int width, int height, double x, double y, string phase, bool activateOwnedWindow, int button)
    {
        if (IntPtr.Size!=8 || Marshal.SizeOf(typeof(Input))!=40) throw new InvalidOperationException("Windows x64 INPUT layout required.");
        if (phase!="move" && phase!="drag" && phase!="down" && phase!="up") throw new ArgumentException("Unknown native phase.");
        if (button<0 || button>2) throw new ArgumentException("Unknown mouse button.");
        int buttonKey=button==0 ? 1 : button==1 ? 2 : 4;
        if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y) ||
            width<=0 || height<=0 || x<1 || y<1 || x>=width-1 || y>=height-1) throw new ArgumentException("Invalid Player client coordinate.");
        var previous=SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (previous==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"Per-monitor-v2 thread DPI unavailable.");
        try
        {
            var target=OwnedWindow(pid,expectedWindow);
            var foreground=GetForegroundWindow();
            bool mayRequest=activateOwnedWindow && phase=="move" && expectedWindow==0;
            if (foreground!=target && !mayRequest) {
                throw new InvalidOperationException("Non-foreground owned HWND; no input sent. Hwnd="+target.ToInt64()+
                    " ExpectedHwnd="+expectedWindow+" Pid="+pid+" ForegroundHwnd="+foreground.ToInt64());
            }
            Rect client;
            var origin=new Point();
            uint dpi=GetDpiForWindow(target);
            if (dpi==0 || !GetClientRect(target,out client) || client.L!=0 || client.T!=0 ||
                client.R!=width || client.B!=height || !ClientToScreen(target,ref origin)) {
                throw new InvalidOperationException("DPI/client geometry differs from actual Unity Screen; no guessed scaling.");
            }
            int cx=(int)Math.Floor(x), cy=height-1-(int)Math.Floor(y);
            var point=new Point { X=checked(origin.X+cx), Y=checked(origin.Y+cy) };
            Point cursor;
            if (!GetCursorPos(out cursor)) throw new InvalidOperationException("Cannot observe native cursor.");
            bool matched=Math.Abs(cursor.X-point.X)<=1 && Math.Abs(cursor.Y-point.Y)<=1;
            if (phase!="move" && phase!="drag" && !matched) throw new InvalidOperationException("Cursor moved away from the owned target. Expected=("+
                point.X+","+point.Y+") Actual=("+cursor.X+","+cursor.Y+") Phase="+phase+" Hwnd="+target.ToInt64());
            // Do not alter user key state. A concurrent held key/button aborts, rather than being released globally.
            for (int key=1; key<255; key++)
            {
                bool held=GetAsyncKeyState(key)<0;
                if (held!=((phase=="up" || phase=="drag") && key==buttonKey)) throw new InvalidOperationException("Unexpected physical key/button state; no input sent.");
            }
            bool requested=mayRequest && foreground!=target, accepted=false;
            if (requested) {
                accepted=SetForegroundWindow(target);
                if (!accepted || GetForegroundWindow()!=target) throw new InvalidOperationException("Windows declined the authorized owned Player foreground request; no input sent. Hwnd="+
                    target.ToInt64()+" BeforeForegroundHwnd="+foreground.ToInt64()+" AfterForegroundHwnd="+GetForegroundWindow().ToInt64()+" RequestReturned="+accepted);
                Rect current;
                var currentOrigin=new Point();
                if (GetDpiForWindow(target)!=dpi || !GetClientRect(target,out current) || current.L!=0 || current.T!=0 ||
                    current.R!=width || current.B!=height || !ClientToScreen(target,ref currentOrigin) ||
                    currentOrigin.X!=origin.X || currentOrigin.Y!=origin.Y) {
                    throw new InvalidOperationException("Owned Player geometry changed during activation; no input sent.");
                }
            }
            if (GetAncestor(WindowFromPoint(point),2)!=target) throw new InvalidOperationException("Target client pixel is obscured or belongs to another window.");
            int left=GetSystemMetrics(76), top=GetSystemMetrics(77), vw=GetSystemMetrics(78), vh=GetSystemMetrics(79);
            if (vw<=1 || vh<=1 || point.X<left || point.Y<top || point.X>=left+vw || point.Y>=top+vh) throw new InvalidOperationException("Client target outside virtual desktop.");
            int ax=(int)Math.Round((point.X-left)*65535.0/(vw-1));
            int ay=(int)Math.Round((point.Y-top)*65535.0/(vh-1));
            uint flags=phase=="move" || phase=="drag" ? 0xC001u : phase=="down" ?
                (button==0 ? 0x0002u : button==1 ? 0x0008u : 0x0020u) : (button==0 ? 0x0004u : button==1 ? 0x0010u : 0x0040u);
            var evidence=new Evidence { Hwnd=target.ToInt64(), Pid=pid, ClientWidth=width, ClientHeight=height,
                ScreenX=point.X, ScreenY=point.Y, ClientX=cx, ClientY=cy, Dpi=dpi, Flags=flags, Phase=phase,
                VirtualLeft=left, VirtualTop=top, VirtualWidth=vw, VirtualHeight=vh, AbsoluteX=ax, AbsoluteY=ay,
                Foreground=true, CursorMatched=matched, ForegroundRequestMade=requested, ForegroundRequestAccepted=accepted,
                BeforeUtc=DateTime.UtcNow.ToString("O") };
            // SendInput is global: repeat the HWND/foreground/occlusion check immediately before each single event.
            if (GetForegroundWindow()!=target || IsIconic(target) || GetAncestor(WindowFromPoint(point),2)!=target) {
                throw new InvalidOperationException("Window ownership changed before SendInput.");
            }
            evidence.Inserted=SendInput(1,new[] { new Input { Mouse=new MouseInput { X=ax,Y=ay,Flags=flags } } },Marshal.SizeOf(typeof(Input)));
            evidence.AfterUtc=DateTime.UtcNow.ToString("O");
            if (evidence.Inserted!=1) throw new Win32Exception(Marshal.GetLastWin32Error(),"SendInput inserted "+evidence.Inserted+" of 1 event (UIPI can block it).");
            return evidence;
        }
        finally
        {
            if (SetThreadDpiAwarenessContext(previous)==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"Failed to restore thread DPI context.");
        }
    }
    private static uint KeyboardFlags(int key, uint mapped, bool release)
    {
        return 8u | (release ? 2u : 0u) | ((key>=37 && key<=40 || (mapped&0xFF00)==0xE000) ? 1u : 0u);
    }
    public static Evidence InjectKey(int pid, long expectedWindow, int width, int height, double x, double y, string phase, int key)
    {
        if (phase!="key-down" && phase!="key-up") throw new ArgumentException("Unknown keyboard phase.");
        if (key!=13 && key!=27 && (key<37 || key>40) && (key<49 || key>51)) throw new ArgumentException("Non-contract keyboard key.");
        if (IntPtr.Size!=8 || Marshal.SizeOf(typeof(KeyInput))!=40 || expectedWindow==0) throw new InvalidOperationException("Exact x64 keyboard INPUT/owned HWND required.");
        var previous=SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (previous==IntPtr.Zero) throw new InvalidOperationException("Per-monitor-v2 coordinate context unavailable.");
        try
        {
            var target=OwnedWindow(pid,expectedWindow);
            Rect client;
            var origin=new Point();
            if (GetForegroundWindow()!=target || !GetClientRect(target,out client) || client.L!=0 || client.T!=0 ||
                client.R!=width || client.B!=height || x<1 || y<1 || x>=width-1 || y>=height-1 ||
                double.IsNaN(x) || double.IsNaN(y) || !ClientToScreen(target,ref origin)) throw new InvalidOperationException("Keyboard target focus/geometry mismatch.");
            var point=new Point { X=checked(origin.X+(int)Math.Floor(x)), Y=checked(origin.Y+height-1-(int)Math.Floor(y)) };
            if (GetAncestor(WindowFromPoint(point),2)!=target) throw new InvalidOperationException("Keyboard owned client point is obscured.");
            for (int candidate=1; candidate<255; candidate++)
            {
                bool observed=GetAsyncKeyState(candidate)<0;
                if (observed!=(phase=="key-up" && candidate==key)) throw new InvalidOperationException("Unexpected key/button state; no keyboard input sent. Phase="+
                    phase+" ContractKey="+key+" ContractHeld="+(GetAsyncKeyState(key)<0)+" MismatchClass="+
                    (candidate==key ? "contract-key" : candidate<=6 ? "pointer-button" : candidate==16 || candidate==17 || candidate==18 || candidate>=160 && candidate<=165 ? "modifier" : "other"));
            }
            uint mapped=MapVirtualKey((uint)key,4);
            if ((mapped&0xFF)==0 || ((mapped&0xFF00)!=0 && (mapped&0xFF00)!=0xE000)) throw new InvalidOperationException("Contract key has no supported scan-code mapping.");
            uint flags=KeyboardFlags(key,mapped,phase=="key-up");
            var result=new Evidence { Hwnd=target.ToInt64(), Pid=pid, ClientWidth=width, ClientHeight=height, ScreenX=point.X, ScreenY=point.Y,
                Dpi=GetDpiForWindow(target), Foreground=true, Phase=phase, Flags=flags, VirtualKey=key, ScanCode=mapped&0xFF, KeyboardX=x, KeyboardY=y, BeforeUtc=DateTime.UtcNow.ToString("O") };
            if (GetForegroundWindow()!=target || IsIconic(target) || GetAncestor(WindowFromPoint(point),2)!=target) throw new InvalidOperationException("Keyboard target changed before SendInput.");
            result.Inserted=SendKeyInput(1,new[] { new KeyInput { Type=1, ScanCode=(ushort)(mapped&0xFF), Flags=flags } },Marshal.SizeOf(typeof(KeyInput)));
            result.AfterUtc=DateTime.UtcNow.ToString("O");
            if (result.Inserted!=1) throw new Win32Exception(Marshal.GetLastWin32Error(),"Keyboard SendInput did not insert exactly one event.");
            return result;
        }
        finally
        {
            if (SetThreadDpiAwarenessContext(previous)==IntPtr.Zero) throw new InvalidOperationException("Failed to restore keyboard coordinate context.");
        }
    }
}
'@

$null=[IO.Directory]::CreateDirectory($runDirectory)
$token=[Guid]::NewGuid().ToString('N')
$deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds).ToString('O')
$arguments=@('-screen-fullscreen','0','-logFile',(Join-Path $runDirectory 'Player.log'),
    '--s9n-directory',$runDirectory,'--s9n-token',$token,'--s9n-deadline',$deadline)
Write-NewJson (Join-Path $runDirectory 'launch.json') @{
    Schema='Stage9NativeInput-v1'; Project=$project; Executable=$exe; BuildDirectory=$build; BuildRun=$BuildRun
    Run=$Run; Token=$token; Mode=$Mode; Suite=$Suite; BuildGuid=$report.BuildGuid; BuildReportSha256=$reportHash
    ActivationAuthorized=$ActivateOwnedWindow.IsPresent
    ManualForegroundWaitSeconds=$WaitForOwnedForegroundSeconds
    Arguments=$arguments; DeadlineUtc=$deadline; RequestedUtc=[DateTime]::UtcNow.ToString('O')
}
$process=$null
$identityRecord=$null
$stdout=$null
$stderr=$null
$failure=$null
$forced=$false
$held=$false
$window=0L
$native=[Collections.Generic.List[object]]::new()
$clock=[Diagnostics.Stopwatch]::StartNew()
function Assert-OwnedProcess {
    if ($clock.Elapsed.TotalSeconds -ge $TimeoutSeconds -or [DateTime]::UtcNow -ge [DateTime]::Parse($deadline).ToUniversalTime()) { throw 'External Player hard deadline expired.' }
    if ($null -eq $process -or $process.HasExited) { throw 'Owned Player exited before the required response.' }
    $process.Refresh()
    if ($process.MainModule.FileName -cne $exe -or $process.StartTime.ToUniversalTime().Ticks -ne $identityRecord.StartTicks) {
        throw 'Owned process executable/startup identity changed.'
    }
    $players=@([Diagnostics.Process]::GetProcessesByName('Stage9NativeInput'))
    try {
        if ($players.Count -ne 1 -or $players[0].Id -ne $process.Id) { throw 'Another native-input Player appeared; refuse input.' }
    } finally { foreach ($player in $players) { $player.Dispose() } }
}
function Test-NativeKey([int] $Key) { return $Key -in @(13,27,37,38,39,40,49,50,51) }
function Test-ParentActivation([bool] $Authorized, [bool] $PlayerHandlesRequest) { return $Authorized -and -not $PlayerHandlesRequest }
function Get-OwnedKeyRelease($Evidence, [int] $ExpectedPid, [long] $Window, [bool] $Alive) {
    if ($Alive -and $null -ne $Evidence -and $Evidence.Phase -ceq 'key-down' -and $Evidence.Inserted -eq 1 -and
        $Evidence.Foreground -eq $true -and $Evidence.Pid -eq $ExpectedPid -and $Evidence.Hwnd -eq $Window -and $Window -ne 0 -and (Test-NativeKey $Evidence.VirtualKey)) {
        return $Evidence
    }
}
function Update-InputReadiness([bool] $Ready, [long] $Cursor, [double] $Milliseconds, [hashtable] $State) {
    if (-not $Ready -or -not $State.Ready -or $State.Cursor -ne $Cursor) { $State.Since=$Milliseconds }
    $State.Ready=$Ready
    $State.Cursor=$Cursor
    return $Ready -and ($Milliseconds-$State.Since -ge 500)
}
function Wait-ManualOwnedForeground([ref] $Window) {
    $wait=[Diagnostics.Stopwatch]::StartNew()
    $state=@{ Ready=$false; Cursor=0L; Since=0.0 }
    $ready=$false
    try {
        do {
            Assert-OwnedProcess
            $physicalReady=[Stage9NativeWin32]::ReadyForInput($process.Id,$Window)
            $ready=Update-InputReadiness $physicalReady ([Stage9NativeWin32]::CursorPosition()) $wait.Elapsed.TotalMilliseconds $state
            if ($ready) { break }
            if ($wait.Elapsed.TotalSeconds -ge $WaitForOwnedForegroundSeconds) { throw 'Manual owned foreground/key-release/stationary cursor wait expired; no input sent.' }
            $null=$process.WaitForExit(25)
        } while ($true)
    } finally {
        $wait.Stop()
        Write-NewJson (Join-Path $runDirectory 'foreground-wait.json') @{ Identity=$identityRecord; Pid=$process.Id; Hwnd=$Window.Value
            ElapsedSeconds=$wait.Elapsed.TotalSeconds; LimitSeconds=$WaitForOwnedForegroundSeconds; RequiredStationaryMilliseconds=500; Ready=$ready; InputInjected=$false }
    }
}
function Wait-Reply([string] $Name, [int] $Stage, [string] $Phase, [string] $StageDeadline = '', [string] $ObservationDeadline = '') {
    $path=Join-Path $runDirectory $Name
    while (-not [IO.File]::Exists($path)) {
        Assert-OwnedProcess
        if ($StageDeadline -and [DateTime]::UtcNow -ge [DateTime]::Parse($StageDeadline).ToUniversalTime()) { throw "Application phase deadline expired: $Name" }
        if ($ObservationDeadline -and [DateTime]::UtcNow -ge [DateTime]::Parse($ObservationDeadline).ToUniversalTime()) { throw "Keyboard acknowledgement safety deadline expired: $Name" }
        $null=$process.WaitForExit(25)
    }
    $reply=Read-Json $path
    if ($ObservationDeadline -and [DateTime]::UtcNow -ge [DateTime]::Parse($ObservationDeadline).ToUniversalTime()) { throw "Keyboard acknowledgement arrived after its safety deadline: $Name" }
    foreach ($key in @('Schema','Token','Run','Pid','StartTicks','Project','BuildGuid','BuildRun','Mode','Unity','DeadlineUtc','BuildReportSha256')) {
        if ($reply[$key] -cne $identityRecord[$key]) { throw "IPC identity mismatch: $Name / $key" }
    }
    if ($reply.Stage -ne $Stage -or $reply.Phase -cne $Phase -or
        [DateTime]::UtcNow -ge [DateTime]::Parse($reply.StageDeadlineUtc).ToUniversalTime()) { throw "Expired/wrong IPC stage: $Name" }
    return $reply
}
try {
    $start=[Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute=$false
    $start.WorkingDirectory=$build
    $start.RedirectStandardOutput=$true
    $start.RedirectStandardError=$true
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $process=[Diagnostics.Process]::Start($start)
    $stdout=$process.StandardOutput.ReadToEndAsync()
    $stderr=$process.StandardError.ReadToEndAsync()
    $identityRecord=@{
        Schema='Stage9NativeInput-v1'; Token=$token; Run=$Run; Pid=$process.Id; StartTicks=$process.StartTime.ToUniversalTime().Ticks
        Project=$project; BuildGuid=$report.BuildGuid; BuildRun=$BuildRun; Mode=$Mode; Unity=$ExpectedUnityVersion
        DeadlineUtc=$deadline; BuildReportSha256=$reportHash; Executable=$exe; BuildDirectory=$build; Suite=$Suite; ActivationAuthorized=$ActivateOwnedWindow.IsPresent
    }
    Write-NewJson (Join-Path $runDirectory 'start.json') $identityRecord
    $captureSteps=0
    if ($Suite -cne 'Basic') {
        while ($true) {
            $stage=$captureSteps+1
            if ($stage -gt 128) { throw 'Capture step budget exceeded.' }
            $readyPath=Join-Path $runDirectory ('ready-'+$stage+'.json')
            $resultPath=Join-Path $runDirectory 'result.json'
            while (-not [IO.File]::Exists($readyPath) -and -not [IO.File]::Exists($resultPath)) {
                if ($process.HasExited -and ([IO.File]::Exists($readyPath) -or [IO.File]::Exists($resultPath))) { break }
                Assert-OwnedProcess
                $null=$process.WaitForExit(25)
            }
            if ([IO.File]::Exists($resultPath)) { break }
            $ready=Wait-Reply ('ready-'+$stage+'.json') $stage 'input'
            $phase=$ready.Action
            $keyboard=$Suite -ceq 'Remaining' -and $phase -cin @('key-down','key-up')
            if ((-not $keyboard -and $phase -cnotin @('move','down','drag','up')) -or ($keyboard -and (-not $ready.ContainsKey('Key') -or -not (Test-NativeKey $ready.Key))) -or $ready.Button -isnot [long] -and $ready.Button -isnot [int] -or
                $ready.Button -lt 0 -or $ready.Button -gt 2 -or -not $ready.ApplicationFocused -or
                $ready.Backend -cne $report.Backend -or $ready.LegacyInput -ne $report.LegacyInput -or $ready.NewInput -ne $report.NewInput -or
                $ready.Module -cne $(if ($Mode -ceq 'ExpectedLegacy') { 'UnityEngine.EventSystems.StandaloneInputModule' } else { 'UnityEngine.InputSystem.UI.InputSystemUIInputModule' })) {
                throw 'Invalid capture action/runtime identity.'
            }
            if ($stage -eq 1 -and $WaitForOwnedForegroundSeconds) {
                Wait-ManualOwnedForeground ([ref]$window)
            }
            Assert-OwnedProcess
            if ([DateTime]::UtcNow -ge [DateTime]::Parse($ready.StageDeadlineUtc).ToUniversalTime()) { throw 'Expired capture action.' }
            Write-NewJson (Join-Path $runDirectory ('attempt-'+$stage+'-input.json')) @{ Identity=$identityRecord; Ready=$ready }
            if ($phase -cin @('down','key-down')) { $held=$true }
            if ($keyboard) {
                $event=[Stage9NativeWin32]::InjectKey($process.Id,$window,$ready.ScreenWidth,$ready.ScreenHeight,$ready.X,$ready.Y,$phase,$ready.Key)
            } else {
                $event=[Stage9NativeWin32]::InjectPointer($process.Id,$window,$ready.ScreenWidth,$ready.ScreenHeight,$ready.X,$ready.Y,$phase,(Test-ParentActivation $ActivateOwnedWindow.IsPresent $playerActivation),$ready.Button)
            }
            $window=$event.Hwnd
            if ($phase -cin @('up','key-up')) { $held=$false }
            $native.Add($event)
            Write-NewJson (Join-Path $runDirectory ('native-'+$stage+'-input.json')) @{ Identity=$identityRecord; Ready=$ready; ActualSendInput=$event }
            $command=$identityRecord.Clone()
            $command.Stage=$stage; $command.Phase='input'; $command.Hwnd=$window; $command.Action=$phase; $command.Button=$ready.Button
            $command.Key=$(if ($keyboard) { $ready.Key } else { 0 })
            $command.StageDeadlineUtc=$ready.StageDeadlineUtc
            Write-NewJson (Join-Path $runDirectory ('command-'+$stage+'-input.json')) $command
            $ack=Wait-Reply ('ack-'+$stage+'-input.json') $stage 'input' $ready.StageDeadlineUtc $(if ($keyboard -and $phase -ceq 'key-down') { [DateTime]::UtcNow.AddSeconds(3).ToString('O') } else { '' })
            if ($ack.Hwnd -ne $window -or $ack.PhaseObserved -ne $true -or $ack.Action -cne $phase -or $ack.Case -cne $ready.Case) { throw 'Capture response mismatch.' }
            if ($keyboard -and $ack.Key -ne $ready.Key) { throw 'Keyboard response key mismatch.' }
            $captureSteps++
        }
    } else {
    foreach ($stage in @(1,2)) {
        $ready=Wait-Reply ('ready-'+$stage+'.json') $stage 'move'
        if ($ready.Kind -cne $(if ($stage -eq 1) { 'color' } else { 'close-button' }) -or -not $ready.ApplicationFocused -or
            $ready.LegacyInput -ne $report.LegacyInput -or $ready.NewInput -ne $report.NewInput -or
            $ready.Backend -cne $report.Backend -or $ready.Module -cne $(if ($Mode -ceq 'ExpectedLegacy') {
                'UnityEngine.EventSystems.StandaloneInputModule'
            } else { 'UnityEngine.InputSystem.UI.InputSystemUIInputModule' })) { throw 'Ready runtime input/module identity mismatch.' }
        foreach ($phase in @('move','down','up')) {
            Assert-OwnedProcess
            if ([DateTime]::UtcNow -ge [DateTime]::Parse($ready.StageDeadlineUtc).ToUniversalTime()) { throw 'Expired application gesture; no input sent.' }
            Write-NewJson (Join-Path $runDirectory ('attempt-'+$stage+'-'+$phase+'.json')) @{
                Identity=$identityRecord; Stage=$stage; Phase=$phase; Ready=$ready; Utc=[DateTime]::UtcNow.ToString('O')
            }
            if ($stage -eq 1 -and $phase -ceq 'move' -and $WaitForOwnedForegroundSeconds) {
                Wait-ManualOwnedForeground ([ref]$window)
            }
            if ($phase -ceq 'down') { $held=$true }
            $event=[Stage9NativeWin32]::Inject($process.Id,$window,$ready.ScreenWidth,$ready.ScreenHeight,$ready.X,$ready.Y,$phase,(Test-ParentActivation $ActivateOwnedWindow.IsPresent $playerActivation))
            $window=$event.Hwnd
            if ($phase -ceq 'up') { $held=$false }
            $native.Add($event)
            Write-NewJson (Join-Path $runDirectory ('native-'+$stage+'-'+$phase+'.json')) @{
                Identity=$identityRecord; Stage=$stage; Phase=$phase; ActualSendInput=$event
            }
            $command=$identityRecord.Clone()
            $command.Stage=$stage; $command.Phase=$phase; $command.Hwnd=$window
            $command.StageDeadlineUtc=$ready.StageDeadlineUtc
            Write-NewJson (Join-Path $runDirectory ('command-'+$stage+'-'+$phase+'.json')) $command
            $ack=Wait-Reply ('ack-'+$stage+'-'+$phase+'.json') $stage $phase $ready.StageDeadlineUtc
            if (-not $ack.MouseOnUI -or $ack.Hwnd -ne $window -or $ack.PhaseObserved -ne $true) { throw 'No actual native callback/production mouse-on-UI observation.' }
        }
    }
    }
    # All input is complete. The retained process handle may terminate between wait observations.
    while (-not $process.WaitForExit(25)) {
        if ($clock.Elapsed.TotalSeconds -ge $TimeoutSeconds -or [DateTime]::UtcNow -ge [DateTime]::Parse($deadline).ToUniversalTime()) {
            throw 'External Player hard deadline expired.'
        }
    }
    if ($process.ExitCode -ne 0) { throw "Player exit code $($process.ExitCode)." }
    $result=Wait-Reply 'result.json' $(if ($Suite -cne 'Basic') { $captureSteps+1 } else { 3 }) 'complete'
    if ($Suite -cne 'Basic') {
        $expectedCases=if ($Suite -ceq 'Capture') { @('three-left','three-right','three-middle','scrollbar','input-field','scroll-elastic','scroll-clamped','close-reuse','destroy-reopen','onselect-reuse','camera-resize','multiple-roots','external-system') } else {
            @('keyboard-navigation','keyboard-text','camera-held-cancel','resolution-held-cancel') + $(if ($report.NewInput) { @('device-removal') } else { @() })
        }
        $protocolValid=if ($Suite -ceq 'Capture') { $result.CaptureProtocol -ceq 'B06Capture-v1' -and $captureSteps -ge 60 } else { $result.RemainingProtocol -ceq 'B06Remaining-v1' -and $captureSteps -ge 25 }
        $actualCases=@($result.Cases | ForEach-Object Name)
        if (-not $result.BusinessPassed -or -not $result.OwnedCleanupCompleted -or @($result.Errors).Count -or
            -not $protocolValid -or $result.InputSteps -ne $captureSteps -or
            $actualCases.Count -ne $expectedCases.Count -or @($actualCases | Sort-Object -Unique).Count -ne $expectedCases.Count -or
            @($expectedCases | Where-Object { $actualCases -cnotcontains $_ }).Count -or @($result.Cases | Where-Object { $_.Passed -ne $true }).Count -or
            $result.Backend -cne $report.Backend -or $result.Development -or $result.LegacyInput -ne $report.LegacyInput -or $result.NewInput -ne $report.NewInput) {
            throw 'Capture case/step/cleanup identity assertions failed.'
        }
    } else {
    if (-not $result.BusinessPassed -or $result.ColorChanges -ne 1 -or $result.ButtonClicks -ne 1 -or
        @($result.Errors).Count -ne 0 -or @($result.Cases).Count -ne 2 -or @($native).Count -ne 6 -or
        -not $result.OwnedCleanupCompleted -or $result.Backend -cne $report.Backend -or $result.Development -or
        $result.LegacyInput -ne $report.LegacyInput -or $result.NewInput -ne $report.NewInput) { throw 'Native basic business assertions failed.' }
    foreach ($case in $result.Cases) {
        if ($case.Down -ne 1 -or $case.Up -ne 1 -or -not $case.MouseOnUI -or $case.Click -ne 1) { throw 'Incomplete or duplicated native pointer callbacks.' }
    }
    }
    $logPath=Join-Path $runDirectory 'Player.log'
    $log=Get-Content -LiteralPath $logPath -Raw -Encoding utf8
    if ($log -cnotmatch ('Initialize engine version: '+[regex]::Escape($ExpectedUnityVersion)+' \(') -or
        $log -cnotmatch 'STAGE9_NATIVE_INPUT_BUSINESS_PASS' -or
        [IO.File]::Exists((Join-Path $runDirectory 'late-errors.log')) -or
        $log -match '(?im)error CS\d+|Unhandled Exception|Crash!!!|Fatal error|^.*Exception:|\(Filename:.*Line:.*\)\s*Assertion') {
        throw 'Raw Player log engine/exception/normal-completion gate failed.'
    }
    if ((Hash $reportPath) -cne $reportHash -or (Hash $packagePath) -cne $identity.PackageSha256) { throw 'BuildReport/package changed during input run.' }
    foreach ($file in $report.PlayerFiles) {
        $path=Join-Path $build $file.Path
        Reject-Link $path
        if ((Hash $path) -cne $file.Sha256) { throw 'Player artifact changed during input run.' }
    }
} catch {
    $failure=$_.Exception.ToString()
} finally {
    if ($null -ne $process) {
        try {
            if ($held -and $native.Count -gt 0 -and -not $process.HasExited) {
                $keyRelease=Get-OwnedKeyRelease $native[$native.Count-1] $process.Id $window $true
                if ($null -ne $keyRelease) {
                    try {
                        Assert-OwnedProcess
                        $actualRelease=[Stage9NativeWin32]::InjectKey($process.Id,$window,$keyRelease.ClientWidth,$keyRelease.ClientHeight,$keyRelease.KeyboardX,$keyRelease.KeyboardY,'key-up',$keyRelease.VirtualKey)
                        Write-NewJson (Join-Path $runDirectory 'owned-key-cleanup.json') @{ Identity=$identityRecord; CleanupOnly=$true; ActualSendInput=$actualRelease }
                        $held=$false
                    } catch {
                        Write-NewJson (Join-Path $runDirectory 'owned-key-cleanup-rejected.json') @{ Identity=$identityRecord; CleanupOnly=$true; Failure=$_.Exception.Message; UpInjected=$false }
                    }
                }
            }
            if (-not $process.HasExited) {
                $forced=$true
                $process.Kill($true)
                if (-not $process.WaitForExit(5000)) { throw 'Owned process did not settle within bounded kill grace.' }
            }
            if (-not $stdout.Wait(5000) -or -not $stderr.Wait(5000)) { throw 'Raw process pipes did not settle within bounded exit grace.' }
            [IO.File]::WriteAllText((Join-Path $runDirectory 'stdout.log'),$stdout.GetAwaiter().GetResult(),[Text.UTF8Encoding]::new($false))
            [IO.File]::WriteAllText((Join-Path $runDirectory 'stderr.log'),$stderr.GetAwaiter().GetResult(),[Text.UTF8Encoding]::new($false))
        } catch { $failure=($failure + "`nExit/pipe cleanup: " + $_.Exception.ToString()).Trim() }
    }
    Write-NewJson (Join-Path $runDirectory 'exit.json') @{
        Pid=$(if ($null -ne $process) { $process.Id } else { $null }); Executable=$exe
        Identity=$identityRecord; ExitCode=$(if ($null -ne $process -and $process.HasExited) { $process.ExitCode } else { $null })
        ForcedExit=$forced; TimedOut=($clock.Elapsed.TotalSeconds -ge $TimeoutSeconds)
        OwnedButtonMayRemainHeld=$held; Failure=$failure; EndedUtc=[DateTime]::UtcNow.ToString('O')
    }
    Write-NewJson (Join-Path $runDirectory 'verdict.json') @{
        Passed=($null -eq $failure -and -not $forced); Suite=$Suite
        Scope=$(if ($Suite -ceq 'Remaining') { 'OS keyboard/navigation/text, API removal of the native mouse and held-geometry cancellation; not physical hotplug or whole-plan release acceptance' } elseif ($Suite -ceq 'Capture') { 'OS capture cancellation, standard controls, drag/inertia, lifecycle, camera, root and borrowed-system cases; not full B06' } else { 'OS SendInput color change + standard close Button only; not legacy smoke or full task-4 coverage' })
        Identity=$identityRecord; Failure=$failure; NativeEvents=@($native); ActivationAuthorized=$ActivateOwnedWindow.IsPresent; ManualForegroundWaitSeconds=$WaitForOwnedForegroundSeconds
        Evidence=@(foreach ($path in @(Files-WithoutLinks $runDirectory)) {
            @{ Path=[IO.Path]::GetFileName($path); Sha256=Hash $path }
        })
        NotCovered=$(if ($Suite -ceq 'Remaining') { @('physical hotplug','touch','whole compatibility plan') } elseif ($Suite -ceq 'Capture') { @('keyboard','device removal','shared actions','physical hotplug','touch') } else { @('capture cancellation','three-button cycles','ScrollRect','keyboard','device removal','shared actions','external EventSystem','multiple roots') })
    }
    $clock.Stop()
    if ($null -ne $process) { $process.Dispose() }
}
if ($failure) { throw "Native input failed; preserve $runDirectory. $failure" }
"Native input suite $Suite passed with exact process/BuildGuid and raw evidence: $runDirectory/verdict.json"
