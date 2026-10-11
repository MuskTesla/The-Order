param([Parameter(Mandatory)][string]$Identity,[Parameter(Mandatory)][long]$Window)
$ErrorActionPreference='Stop'
$id=Get-Content -LiteralPath $Identity -Raw|ConvertFrom-Json
$p=Get-Process -Id $id.pid
if($p.Path -ne $id.candidate -or $p.StartTime.ToUniversalTime().Ticks -ne ([DateTimeOffset]$id.start).UtcTicks -or
   (Get-Content -LiteralPath (Join-Path $id.profile 'native-profile.json') -Raw|ConvertFrom-Json).ProfileKind -ne 'Test'){throw 'Current Test generation required'}
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class NativeTestTrayMenu {
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window,uint message,IntPtr w,IntPtr l);
}
'@
# Test notification injection only, not evidence of physically clicking Explorer's tray icon.
[uint32]$owner=0;[NativeTestTrayMenu]::GetWindowThreadProcessId([IntPtr]$Window,[ref]$owner)|Out-Null
if($owner -ne $p.Id){throw 'Returned window does not belong to current Test process'}
if(![NativeTestTrayMenu]::PostMessage([IntPtr]$Window,0x8047,[IntPtr]::Zero,[IntPtr]0x205)){throw 'Test tray notification failed'}
