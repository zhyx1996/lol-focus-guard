# 验证"打回"这个动作本身：客户端自己弹到前台被拦之后，
#   1) 前台真的还给了原来那个窗口（不是停在客户端上）；
#   2) 客户端窗口是出于最小化状态（不是只把焦点拿走、还留在前台）。
#
# 不依赖输入注入（PopTarget 靠读 %TEMP%\poptarget.flag 自己弹），
# 所以即使 WeGame / 客户端的安全模块拦了 SendInput 也能跑。
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public class R1 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public static IntPtr Found = IntPtr.Zero;
  public static uint WantPid;
  public static bool Cb(IntPtr h, IntPtr l) {
    uint pid; GetWindowThreadProcessId(h, out pid);
    if (pid == WantPid && IsWindowVisible(h)) { Found = h; return false; }
    return true;
  }
  public static IntPtr TopWindowOf(int pid) { WantPid = (uint)pid; Found = IntPtr.Zero; EnumWindows(Cb, IntPtr.Zero); return Found; }
  public static string FgProc() {
    IntPtr h = GetForegroundWindow(); uint pid; GetWindowThreadProcessId(h, out pid);
    try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { return "?"; }
  }
  public static bool Focus(IntPtr h) {
    uint pid; uint t = GetWindowThreadProcessId(h, out pid);
    uint ft = GetWindowThreadProcessId(GetForegroundWindow(), out pid);
    uint cur = GetCurrentThreadId(); bool a1=false, a2=false;
    if (t != 0 && t != cur) a1 = AttachThreadInput(cur, t, true);
    if (ft != 0 && ft != cur) a2 = AttachThreadInput(cur, ft, true);
    try { ShowWindow(h, 9); SetForegroundWindow(h); } finally { if (a2) AttachThreadInput(cur, ft, false); if (a1) AttachThreadInput(cur, t, false); }
    System.Threading.Thread.Sleep(300);
    return GetForegroundWindow() == h;
  }
}
'@
[R1]::SetProcessDPIAware()

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /optimize+ /out:PopTarget.exe /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll PopTarget.cs | Out-Null
if (-not (Test-Path (Join-Path $root 'PopTarget.exe'))) { throw '编译 PopTarget.exe 失败' }

$flag = Join-Path $env:TEMP 'poptarget.flag'
$bgExe = Join-Path $env:TEMP 'BgWin.exe'   # 换个进程名，这样它不在监控名单里，才能当'你正在看的窗口'
Remove-Item $flag -ErrorAction SilentlyContinue
Get-Process LolFocusGuard, PopTarget, BgWin -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600

Copy-Item (Join-Path $root 'PopTarget.exe') $bgExe -Force
$bg = Start-Process $bgExe -ArgumentList 'bg' -PassThru
Start-Sleep -Milliseconds 800
$pt = Start-Process (Join-Path $root 'PopTarget.exe') -ArgumentList 'target' -PassThru
Start-Sleep -Seconds 2

$bgH = [R1]::TopWindowOf($bg.Id)
$ptH = [R1]::TopWindowOf($pt.Id)
if ($bgH -eq [IntPtr]::Zero -or $ptH -eq [IntPtr]::Zero) { throw "拿不到测试窗口 (bg=$bgH pt=$ptH)" }

$guardLog = Join-Path $env:LOCALAPPDATA 'LolFocusGuard\guard.log'
Remove-Item $guardLog -ErrorAction SilentlyContinue
$guard = Start-Process (Join-Path $root 'LolFocusGuard.exe') -ArgumentList '--targets=PopTarget','--verbose' -PassThru
Start-Sleep -Seconds 2

# 前台可能被别的程序占着（比如英雄联盟登录程序，它会挡住别人抢前台），先把它最小化，
# 再把前台给对照组窗口（模拟"你正在看别的窗口"）。
$fgProc = [R1]::FgProc()
if ($fgProc -ne 'BgWin') {
    foreach ($p2 in @('Client','LolFocusGuard')) {
        Get-Process $p2 -ErrorAction SilentlyContinue | ForEach-Object {
            $h2 = [R1]::TopWindowOf($_.Id)
            if ($h2 -ne [IntPtr]::Zero) { [void][R1]::ShowWindow($h2, 6) }   # SW_MINIMIZE
        }
    }
    Start-Sleep -Milliseconds 600
}
for ($i = 0; $i -lt 3; $i++) {
    [void][R1]::Focus($bgH)
    if ([R1]::FgProc() -eq 'BgWin') { break }
    Start-Sleep -Milliseconds 500
}
"打回前前台 = $([R1]::FgProc())"

# 让 PopTarget 自己弹到前台（它自己调 SetForegroundWindow，和客户端一样）
Set-Content -Path $flag -Value 'pop' -Encoding ASCII
Start-Sleep -Seconds 3
Remove-Item $flag -ErrorAction SilentlyContinue

$text = if (Test-Path $guardLog) { Get-Content $guardLog -Raw } else { '' }
"=== 日志 ==="
($text -split "`r?`n" | Where-Object { $_ -match '拦截 #|放行原因|不放行' })

$blocked = ([regex]::Matches($text, '拦截 #')).Count
$fgNow = [R1]::FgProc()
$iconic = [R1]::IsIconic($ptH)
"打回后前台 = $fgNow，PopTarget 最小化 = $iconic，拦截 $blocked 次"

$pass = $true
if ($blocked -lt 1) { 'FAIL: 没有拦截记录'; $pass = $false } else { "ok  : 拦了 $blocked 次" }
if ($fgNow -eq 'PopTarget') { 'FAIL: 拦截后前台还停在 PopTarget 上（没还给你）'; $pass = $false } else { "ok  : 前台已交还（现在是 $fgNow）" }
if (-not $iconic) { 'FAIL: PopTarget 没有被最小化（只是被抢了焦点）'; $pass = $false } else { 'ok  : PopTarget 已被最小化' }

Stop-Process -Id $guard.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $pt.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $bg.Id -Force -ErrorAction SilentlyContinue
"`n结果: " + $(if ($pass) { 'PASS' } else { 'FAIL' })
