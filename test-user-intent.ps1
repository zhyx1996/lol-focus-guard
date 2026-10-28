# 真机症状回归：**用户自己把窗口叫出来之后，守卫不许再把它压回去**。
#
# 对应症状：「我想切出来用，得点好几遍才切得出来」——
# 你点了任务栏/点了窗口，守卫放行了；可 1.2 秒后它又判一次"这是它自己弹的"，
# 把你刚叫出来的窗口最小化，于是你得反复点。
#
# 做法（确定性，不依赖输入注入）：
#   把测试窗口的进程名伪装成 LeagueClientUx，这样它第一次出现时守卫会按
#   「客户端窗口第一次出现」放行 —— 等价于"你自己把它叫出来"。
#   A) 放行后 3 秒内连续把它抬到前台 3 次：一次都不许拦、不许最小化。
#   B) 等放行保护过期（>3.2 秒）后再抬一次：必须重新开始拦。
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class UI1 {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public static IntPtr Found; public static uint Want;
  public static bool Cb(IntPtr h, IntPtr l) {
    uint p; GetWindowThreadProcessId(h, out p);
    if (p == Want && IsWindowVisible(h)) { Found = h; return false; }
    return true;
  }
  public static IntPtr TopOf(int pid) { Want = (uint)pid; Found = IntPtr.Zero; EnumWindows(Cb, IntPtr.Zero); return Found; }
  public static bool Raise(IntPtr h) {
    uint p;
    uint target = GetWindowThreadProcessId(h, out p);
    uint fg = GetWindowThreadProcessId(GetForegroundWindow(), out p);
    uint cur = GetCurrentThreadId();
    bool a1 = false, a2 = false;
    try {
      if (target != 0 && target != cur) a1 = AttachThreadInput(cur, target, true);
      if (fg != 0 && fg != cur) a2 = AttachThreadInput(cur, fg, true);
      ShowWindow(h, 9);
      bool ok = SetForegroundWindow(h);
      System.Threading.Thread.Sleep(150);
      return ok;
    } finally {
      if (a2) AttachThreadInput(cur, fg, false);
      if (a1) AttachThreadInput(cur, target, false);
    }
  }
  public static string FgProc() {
    uint p; GetWindowThreadProcessId(GetForegroundWindow(), out p);
    try { return System.Diagnostics.Process.GetProcessById((int)p).ProcessName; } catch { return "?"; }
  }
}
'@

$repo = $PSScriptRoot
$guardLog = Join-Path $env:LOCALAPPDATA 'LolFocusGuard\guard.log'
$fake = Join-Path $env:TEMP 'lfg-fakeclient'
New-Item -ItemType Directory -Path $fake -Force | Out-Null
Copy-Item (Join-Path $repo 'PopTarget.exe') (Join-Path $fake 'LeagueClientUx.exe') -Force
Copy-Item (Join-Path $repo 'PopTarget.exe') (Join-Path $fake 'BgWin.exe') -Force

Get-Process LolFocusGuard, LeagueClientUx, PopTarget, BgWin -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Remove-Item $guardLog -ErrorAction SilentlyContinue

$pass = $true

$guard = Start-Process (Join-Path $repo 'LolFocusGuard.exe') -ArgumentList '--targets=LeagueClientUx', '--verbose' -PassThru
Start-Sleep -Seconds 2
$bg = Start-Process (Join-Path $fake 'BgWin.exe') -ArgumentList 'bg', 300, 700 -PassThru
$target = Start-Process (Join-Path $fake 'LeagueClientUx.exe') -ArgumentList 'target', 300, 200 -PassThru
Start-Sleep -Seconds 2
$bgH = [UI1]::TopOf($bg.Id)
$ptH = [UI1]::TopOf($target.Id)
if ($ptH -eq [IntPtr]::Zero) { throw '测试窗口没起来' }

$all = Get-Content $guardLog -Raw
if ($all -notmatch '客户端窗口第一次出现') {
    "断言失败：测试窗口第一次出现时没有被放行（A 阶段前提不成立）"
    ($all -split "`r?`n" | Select-Object -Last 8)
}

# ---------- A) 放行之后 3 秒内连抬 3 次：不许拦、不许最小化 ----------
$before = (Get-Content $guardLog -Raw).Length
for ($i = 0; $i -lt 3; $i++) {
    [void][UI1]::Raise($bgH)
    Start-Sleep -Milliseconds 250
    [void][UI1]::Raise($ptH)
    Start-Sleep -Milliseconds 350
}
$partA = (Get-Content $guardLog -Raw).Substring([Math]::Min($before, (Get-Content $guardLog -Raw).Length))
$blocksA = ([regex]::Matches($partA, '拦截 #')).Count
$iconicA = [UI1]::IsIconic($ptH)
"=== A) 刚放行后 3 秒内，它连着被抬了 3 次 ==="
($partA -split "`r?`n" | Where-Object { $_ -match '前台变化|放行原因|拦截|不放行' } | Select-Object -First 10)
if ($blocksA -eq 0) { 'ok  : 放行之后没有再压回去（拦 0 次）' } else { "FAIL: 放行之后又被拦了 $blocksA 次（用户就会觉得「点好几遍才切得出来」）"; $pass = $false }
if (-not $iconicA) { 'ok  : 窗口还在前台，没被最小化' } else { 'FAIL: 窗口刚被叫出来就被最小化了'; $pass = $false }

# ---------- B) 保护期过了以后，同样的自弹必须重新拦 ----------
Start-Sleep -Milliseconds 3600
[void][UI1]::Raise($bgH)
Start-Sleep -Milliseconds 300
$before = (Get-Content $guardLog -Raw).Length
[void][UI1]::Raise($ptH)
Start-Sleep -Milliseconds 1200
$partB = (Get-Content $guardLog -Raw).Substring([Math]::Min($before, (Get-Content $guardLog -Raw).Length))
$blocksB = ([regex]::Matches($partB, '拦截 #')).Count
"=== B) 保护期过后，同样再抬一次 ==="
($partB -split "`r?`n" | Where-Object { $_ -match '前台变化|放行原因|拦截|不放行' } | Select-Object -First 8)
if ($blocksB -ge 1) { "ok  : 保护期过后重新开始拦（拦了 $blocksB 次）" } else { 'FAIL: 保护期过后也不拦了（等于永久放行，自弹就再也拦不住）'; $pass = $false }

Stop-Process -Id $guard.Id, $bg.Id, $target.Id -Force -ErrorAction SilentlyContinue
""
if ($pass) { '结果: PASS' } else { '结果: FAIL' }
if (-not $pass) { exit 1 }
