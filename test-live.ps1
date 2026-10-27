# 拿真实客户端做一次端到端验证：把客户端窗口切到前台，看守卫是否立刻收回
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class L {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint c);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr h);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public static IntPtr Find(string name) {
    IntPtr found = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      if (!IsWindowVisible(h)) return true;
      if (GetWindow(h, 4) != IntPtr.Zero) return true;
      uint pid; GetWindowThreadProcessId(h, out pid);
      try { if (System.Diagnostics.Process.GetProcessById((int)pid).ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)) { found = h; return false; } } catch {}
      return true;
    }, IntPtr.Zero);
    return found;
  }
  public static string FgProc() {
    IntPtr h = GetForegroundWindow(); uint pid; GetWindowThreadProcessId(h, out pid);
    try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { return "?"; }
  }
  public static bool Activate(IntPtr h) {
    uint pid;
    uint targetThread = GetWindowThreadProcessId(h, out pid);
    uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out pid);
    uint cur = GetCurrentThreadId();
    bool a1 = false, a2 = false;
    if (targetThread != 0 && targetThread != cur) a1 = AttachThreadInput(cur, targetThread, true);
    if (fgThread != 0 && fgThread != cur) a2 = AttachThreadInput(cur, fgThread, true);
    try {
      ShowWindow(h, 9);
      SetFocus(h);
      SetForegroundWindow(h);
    } finally {
      if (a2) AttachThreadInput(cur, fgThread, false);
      if (a1) AttachThreadInput(cur, targetThread, false);
    }
    System.Threading.Thread.Sleep(120);
    return GetForegroundWindow() == h;
  }
}
'@

$guardLog = Join-Path $env:LOCALAPPDATA 'LolFocusGuard\guard.log'
Get-Process LolFocusGuard -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$client = Get-Process LeagueClientUx -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $client) { throw '客户端没在运行' }
$hwnd = [IntPtr]$client.MainWindowHandle
"客户端 pid=$($client.Id) hwnd=$hwnd 最小化=$([L]::IsIconic($hwnd)) 前台=$([L]::FgProc())"

if (-not [L]::IsIconic($hwnd)) { [void][L]::ShowWindow($hwnd, 6); Start-Sleep -Milliseconds 500 }
Remove-Item $guardLog -ErrorAction SilentlyContinue
$guard = Start-Process (Join-Path $PSScriptRoot 'LolFocusGuard.exe') -ArgumentList '--verbose' -PassThru
Start-Sleep -Seconds 2
$fgBefore = [L]::FgProc()
"守卫已启动 pid=$($guard.Id)，当前前台=$fgBefore，客户端最小化=$([L]::IsIconic($hwnd))"

$pass = $true
for ($i = 1; $i -le 3; $i++) {
    $ok = [L]::Activate($hwnd)
    Start-Sleep -Milliseconds 700
    $iconic = [L]::IsIconic($hwnd)
    $fgNow = [L]::FgProc()
    "第 $i 次: 脚本激活=$ok → 客户端最小化=$iconic 前台=$fgNow"
    if ($fgNow -eq 'LeagueClientUx') { "FAIL: 第 $i 次客户端还占着前台"; $pass = $false }
    Start-Sleep -Milliseconds 300
}

"--- guard.log ---"
if (Test-Path $guardLog) { Get-Content $guardLog -Raw }
"`n(守卫仍在运行 pid=$($guard.Id)；不需要就 Stop-Process -Id $($guard.Id))"
"结果: " + $(if ($pass) { 'PASS' } else { 'FAIL' })
