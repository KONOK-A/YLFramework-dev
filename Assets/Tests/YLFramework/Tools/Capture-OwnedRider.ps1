#requires -Version 7.6
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $RiderRoot,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]+$')][string] $Name,
    [switch] $CloseUntrusted
)
Set-StrictMode -Version 3.0
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($RiderRoot)
$owner=Get-Content -LiteralPath (Join-Path $root 'ready.json') -Raw | ConvertFrom-Json -DateKind String
$process=Get-Process -Id $owner.Pid -ErrorAction Stop
if ($owner.Purpose -cne 'Stage9-owned-rider' -or $owner.Root -cne $root -or -not $owner.StartupOwnershipVerified -or
    -not [string]::Equals($process.Path,$owner.Executable,[StringComparison]::OrdinalIgnoreCase) -or
    $process.StartTime.ToUniversalTime() -ne [DateTime]::Parse($owner.StartUtc).ToUniversalTime() -or
    $process.MainWindowTitle -notlike ('*'+[IO.Path]::GetFileName($owner.Project)+'*') -or $process.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Refuse an unowned or unexpected Rider window.' }
$image=Join-Path $root ($Name+'.png'); $evidence=Join-Path $root ($Name+'.json')
if ((Test-Path -LiteralPath $image) -or (Test-Path -LiteralPath $evidence)) { throw 'Preserve previous Rider window evidence.' }
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class U63RiderWindow
{
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
'@
$window=$process.MainWindowHandle; $pidValue=[uint32]0
$previousDpi=[U63RiderWindow]::SetThreadDpiAwarenessContext([IntPtr](-4))
if ($previousDpi -eq [IntPtr]::Zero) {throw 'Cannot enter a temporary per-monitor DPI capture context.'}
$null=[U63RiderWindow]::GetWindowThreadProcessId($window,[ref]$pidValue)
$rect=[U63RiderWindow+Rect]::new()
$iconic=[U63RiderWindow]::IsIconic($window)
$hasRect=[U63RiderWindow]::GetWindowRect($window,[ref]$rect)
if ($pidValue -ne $owner.Pid -or $iconic -or -not $hasRect -or $rect.Right -le $rect.Left -or $rect.Bottom -le $rect.Top) {
    $null=[U63RiderWindow]::SetThreadDpiAwarenessContext($previousDpi)
    throw "Wrong HWND owner or invalid/minimized window. Pid=$pidValue Expected=$($owner.Pid) Iconic=$iconic Rect=$hasRect Width=$($rect.Right-$rect.Left) Height=$($rect.Bottom-$rect.Top)"
}
$bitmap=[Drawing.Bitmap]::new(($rect.Right-$rect.Left),($rect.Bottom-$rect.Top))
$graphics=[Drawing.Graphics]::FromImage($bitmap)
try {
    $dc=$graphics.GetHdc()
    try {if (-not [U63RiderWindow]::PrintWindow($window,$dc,0)) {throw 'Owned-window rendering failed.'}} finally {$graphics.ReleaseHdc($dc)}
    $bitmap.Save($image,[Drawing.Imaging.ImageFormat]::Png)
    $title=$process.MainWindowTitle
    $sockets=@(Get-NetTCPConnection -OwningProcess $owner.Pid -State Listen -ErrorAction SilentlyContinue | Select-Object LocalAddress,LocalPort,OwningProcess)
    $closed=$false
    if ($CloseUntrusted) {
        if ($title -notmatch '不受信任|Untrusted') {throw 'Only this still-untrusted owned solution window may be closed by this action.'}
        $closed=$process.CloseMainWindow()
        if (-not $closed) {throw 'The owned Rider window did not accept close.'}
    }
    $record=@{Pid=$owner.Pid; StartUtc=$owner.StartUtc; Project=$owner.Project; WindowTitle=$title; Hwnd=$window.ToInt64();
        CapturedUtc=[DateTime]::UtcNow.ToString('O'); Width=$bitmap.Width; Height=$bitmap.Height; Image=$image; ListeningSockets=$sockets;
        Method='PrintWindow owned HWND only; no desktop capture, focus, keyboard input or trust approval'; TrustGrantedByHarness=$false; CloseRequested=$closed}
    [IO.File]::WriteAllText($evidence,($record | ConvertTo-Json -Depth 8))
    [pscustomobject]@{Image=$image; Evidence=$evidence; Title=$title; CloseRequested=$closed}
} finally {$graphics.Dispose();$bitmap.Dispose();$null=[U63RiderWindow]::SetThreadDpiAwarenessContext($previousDpi)}
