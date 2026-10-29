# 复现真机上漏拦的那个场景：客户端自己弹到前台，而且弹出来的窗口正好盖在鼠标指针下面。
#
# 为什么单列一个测试：钩子被 ACE 掐掉以后，"你点了它吗"只能靠指针位置判断。
# 如果用"此刻指针在哪"，窗口自己弹出来会盖住指针 → 被误判成"你点的" → 一次都拦不住
# （真机上 19:02 的匹配成功弹窗就是这么漏过去的，日志里连拦 10 次全是"你点了这个窗口"）。
# 正确做法是用**事件发生之前**(上一轮 50ms 轮询)的指针位置。
#
# 复现手法：窗口先摆在别处，然后在"抬前台"的同一瞬间把它挪到指针底下 ——
# 和客户端把窗口弹到全屏盖住指针是一回事。期望：拦截。
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class C9 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool fAltTab);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }

  /// <summary>
  /// 模拟"目标程序自己抬前台"：在抬前台的同一次调用里把窗口挪到指针底下。
  /// 用 AttachThreadInput 借用前台线程的权限，否则后台进程的 SetForegroundWindow 会被系统拒绝，
  /// 那样就什么都测不到了（真客户端的弹窗是有权限的）。
  /// </summary>
  public static bool StealAsClient(IntPtr h, int x, int y) {
    POINT p; GetCursorPos(out p);
    uint fgPid; uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out fgPid);
    uint tPid;   uint tThread  = GetWindowThreadProcessId(h, out tPid);
    uint cur = GetCurrentThreadId();
    bool a1 = false, a2 = false;
    try {
      if (tThread != 0 && tThread != cur) a1 = AttachThreadInput(cur, tThread, true);
      if (fgThread != 0 && fgThread != cur) a2 = AttachThreadInput(cur, fgThread, true);
      ShowWindow(h, 9);                                         // SW_RESTORE
      SetWindowPos(h, (IntPtr)(-1), x, y, 0, 0, 0x0001|0x0010); // SWP_NOSIZE|SWP_NOACTIVATE：挪到指针底下
      bool ok = SetForegroundWindow(h);
      if (!ok) { SwitchToThisWindow(h, false); ok = true; }
      return ok;
    } finally { if (a2) AttachThreadInput(cur, fgThread, false); if (a1) AttachThreadInput(cur, tThread, false); }
  }

  public static string FgProc() {
    IntPtr h = GetForegroundWindow(); uint pid; GetWindowThreadProcessId(h, out pid);
    try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { return "?"; }
  }
}
'@
[void][C9]::SetProcessDPIAware()

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /optimize+ /out:PopTarget.exe /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll PopTarget.cs | Out-Null

Get-Process LolFocusGuard, PopTarget -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600

# 目标窗口先摆在远离指针的地方
$pop = Start-Process (Join-Path $root 'PopTarget.exe') -ArgumentList 'target', 60, 60 -PassThru
Start-Sleep -Seconds 2

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class C10 {
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public static IntPtr Found = IntPtr.Zero; public static uint Want;
  public static bool Cb(IntPtr h, IntPtr l) {
    uint pid; GetWindowThreadProcessId(h, out pid);
    if (pid == Want && IsWindowVisible(h)) { Found = h; return false; }
    return true;
  }
  public static IntPtr Top(int pid) { Want = (uint)pid; Found = IntPtr.Zero; EnumWindows(Cb, IntPtr.Zero); return Found; }
}
'@
$popH = [C10]::Top($pop.Id)
if ($popH -eq [IntPtr]::Zero) { throw '拿不到 PopTarget 窗口' }

$guardLog = Join-Path $env:LOCALAPPDATA 'LolFocusGuard\guard.log'
Remove-Item $guardLog -ErrorAction SilentlyContinue
$guard = Start-Process (Join-Path $root 'LolFocusGuard.exe') -ArgumentList '--targets=PopTarget', '--verbose' -PassThru
Start-Sleep -Milliseconds 700
if ($guard.HasExited) { '跳过：守卫启动即退出（单实例互斥锁可能还被上一轮实例占着），本轮结论无效。'; '结果: SKIP'; exit 0 }
Start-Sleep -Seconds 2

# 等输入年龄远超宽限（>1.5s），确保"没有输入"这个前提成立
Start-Sleep -Milliseconds 1600

# 前提：必须是一次真实的「前台切换」。如果 PopTarget 在守卫启动时就已经在前台，
# 下面再抬它就没有任何 event，守卫自然"一次都没拦"—— 那是测试自己的问题，不是守卫的。
# 所以先把它最小化，让前台离开它。
[void][C9]::ShowWindow($popH, 6)
Start-Sleep -Milliseconds 600
if ([C9]::FgProc() -eq 'PopTarget') {
    '跳过：无法让前台离开 PopTarget（切不动），本次结论无效。'
    Stop-Process -Id $guard.Id -Force -ErrorAction SilentlyContinue
    Stop-Process -Id $pop.Id -Force -ErrorAction SilentlyContinue
    '结果: SKIP'
    exit 0
}

$pt = New-Object C9+POINT
[void][C9]::GetCursorPos([ref]$pt)
$wx = [Math]::Max(0, $pt.X - 80)
$wy = [Math]::Max(0, $pt.Y - 60)
"指针在 ($($pt.X),$($pt.Y))；把窗口挪到 ($wx,$wy) 盖住指针并抬前台"
$ok = [C9]::StealAsClient($popH, $wx, $wy)
"抬前台返回 = $ok，现在前台 = $([C9]::FgProc())"
Start-Sleep -Seconds 3

$text = if (Test-Path $guardLog) { Get-Content $guardLog -Raw } else { '' }
"=== 相关日志 ==="
($text -split "`r?`n" | Where-Object { $_ -match '前台变化|拦截|放行原因|不放行' } | Select-Object -Last 10)

$blocked = ([regex]::Matches($text, '拦截 #')).Count
$allowedClick = $text -match '你点了这个窗口'
$pass = $true
if ($blocked -ge 1) { "ok  : 拦住了 $blocked 次" }
else { 'FAIL: 一次都没拦 —— 窗口盖住指针就被当成"你点的"了'; $pass = $false }
if ($allowedClick) { 'FAIL: 出现"你点了这个窗口"的误判（窗口盖住指针导致的）'; $pass = $false } else { 'ok  : 没有误判成"你点的"' }

Stop-Process -Id $guard.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $pop.Id -Force -ErrorAction SilentlyContinue
"`n结果: " + $(if ($pass) { 'PASS' } else { 'FAIL' })
# 退出码要能反映成败：批量跑测试的驱动看的是 $LASTEXITCODE，只打印 FAIL 会让整套测试假绿。
if (-not $pass) { exit 1 }
