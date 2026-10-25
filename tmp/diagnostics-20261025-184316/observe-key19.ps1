Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class Key19Observation {
 [StructLayout(LayoutKind.Sequential)] public struct LastInput { public uint Size; public uint Time; }
 [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
 [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint tid);
 [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LastInput info);
 public static uint Idle() { var info = new LastInput { Size = 8 }; return GetLastInputInfo(ref info) ? unchecked((uint)Environment.TickCount - info.Time) : uint.MaxValue; }
}
"@
# Bounded polling only of IME and modifier state. No hooks or synthetic input.
$records = for ($sample = 0; $sample -lt 12; $sample++) {
 $window = [Key19Observation]::GetForegroundWindow()
 [uint32]$foregroundPid = 0
 $thread = [Key19Observation]::GetWindowThreadProcessId($window, [ref]$foregroundPid)
 $states = [ordered]@{}
 foreach ($key in @(0x19,0xF3,0xF4,0xA2,0xA3,0xA4,0xA5)) {
  $raw = [int][Key19Observation]::GetAsyncKeyState($key)
  $states[('0x{0:X2}' -f $key)] = [ordered]@{ highBitDown = (($raw -band 0x8000) -ne 0); raw = ('0x{0:X4}' -f ($raw -band 0xFFFF)) }
 }
 [ordered]@{ at = [DateTimeOffset]::Now.ToString('o'); foreground = $window.ToInt64(); pid = $foregroundPid; layout = ('0x{0:X}' -f [Key19Observation]::GetKeyboardLayout($thread).ToInt64()); idleMs = [Key19Observation]::Idle(); states = $states }
 Start-Sleep -Milliseconds 500
}
$records | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 (Join-Path $PSScriptRoot 'key19-observation.json')
$records | ForEach-Object { '{0} idle={1} layout={2} key19={3} ctrlL={4} ctrlR={5}' -f $_.at,$_.idleMs,$_.layout,$_.states['0x19'].raw,$_.states['0xA2'].raw,$_.states['0xA3'].raw }
