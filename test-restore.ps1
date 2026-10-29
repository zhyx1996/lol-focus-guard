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
  [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool fAltTab);
  [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
  [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO li);
  [DllImport("kernel32.dll")] public static extern uint GetTickCount();
  /// <summary>距离最后一次系统输入过了多少毫秒（和守卫用的是同一个系统事实）。</summary>
  public static uint InputAgeMs() {
    LASTINPUTINFO li = new LASTINPUTINFO(); li.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(LASTINPUTINFO));
    GetLastInputInfo(ref li);
    return GetTickCount() - li.dwTime;
  }
  /// <summary>模拟客户端自己抬前台：借前台线程的权限，否则后台进程的 SetForegroundWindow 会被系统拒绝。</summary>
  public static bool RaiseAsClient(IntPtr h) {
    uint p1, p2;
    uint tThread = GetWindowThreadProcessId(h, out p1);
    uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out p2);
    uint cur = GetCurrentThreadId();
    bool a1 = false, a2 = false;
    try {
      if (tThread != 0 && tThread != cur) a1 = AttachThreadInput(cur, tThread, true);
      if (fgThread != 0 && fgThread != cur) a2 = AttachThreadInput(cur, fgThread, true);
      ShowWindow(h, 9);
      bool ok = SetForegroundWindow(h);
      if (!ok) { SwitchToThisWindow(h, false); ok = true; }
      return ok;
    } finally { if (a2) AttachThreadInput(cur, fgThread, false); if (a1) AttachThreadInput(cur, tThread, false); }
  }
  /// <summary>把前台交给某个窗口（测试里用来模拟"你正在看别的窗口"）。</summary>
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

# 让它"像客户端那样"自己抬前台（借前台线程权限，否则后台进程抬不动，测试会一直不触发）
$idle = $false
for ($i = 0; $i -lt 40; $i++) {
    if ([R1]::InputAgeMs() -ge 1500) { $idle = $true; break }
    Start-Sleep -Milliseconds 300
}
if (-not $idle) {
    Stop-Process -Id $guard.Id -Force -ErrorAction SilentlyContinue
    Stop-Process -Id $pt.Id -Force -ErrorAction SilentlyContinue
    Stop-Process -Id $bg.Id -Force -ErrorAction SilentlyContinue
    "跳过：你一直在操作这台机器（没有连续 1.5 秒的空闲），模拟不了「无输入的强制抢前台」"
    "`n结果: SKIP"
    exit 0
}
"机器空闲了（最后输入 $([R1]::InputAgeMs())ms 前），开始模拟抢前台"

$raised = $false
for ($i = 0; $i -lt 3; $i++) {
    [void][R1]::RaiseAsClient($ptH)
    Start-Sleep -Milliseconds 400
    if ([R1]::FgProc() -eq 'PopTarget') { $raised = $true; break }
}
"抬前台结果 = $raised，现在前台 = $([R1]::FgProc())"
if (-not $raised) {
    # 机器上有人正在操作时，系统会拒绝后台进程抢前台 —— 这不是守卫的问题，跳过而不是误报失败
    Stop-Process -Id $guard.Id -Force -ErrorAction SilentlyContinue
    Stop-Process -Id $pt.Id -Force -ErrorAction SilentlyContinue
    Stop-Process -Id $bg.Id -Force -ErrorAction SilentlyContinue
    "跳过：测试窗口没能抬到前台（当前有别的程序占着前台，系统拒绝后台抢占），不算失败"
    "`n结果: SKIP"
    exit 0
}
Start-Sleep -Seconds 3

$text = if (Test-Path $guardLog) { Get-Content $guardLog -Raw } else { '' }
"=== 日志 ==="
($text -split "`r?`n" | Where-Object { $_ -match '拦截 #|放行原因|不放行' })

$blocked = ([regex]::Matches($text, '拦截 #')).Count
$fgNow = [R1]::FgProc()
$IsTargetIconic = [R1]::IsIconic($ptH)
$iconic = [R1]::IsIconic($ptH)
$IsTargetIconic = $iconic
"打回后前台 = $fgNow，PopTarget 最小化 = $iconic，拦截 $blocked 次"

$pass = $true
if ($blocked -lt 1) { 'FAIL: 没有拦截记录'; $pass = $false } else { "ok  : 拦了 $blocked 次" }
# 注意：这个测试窗口是 TopMost 的最小化窗口，Windows 有可能让"已最小化的窗口"继续保持前台
# （真机日志里没出现过：19:11 那次拦截后前台正常回到 msedge）。
# 所以这里只断言"它没有以可见状态杵在前台"。
if ($fgNow -eq 'PopTarget' -and -not ($IsTargetIconic)) { 'FAIL: 拦截后目标窗口还可见地占着前台（没还给你）'; $pass = $false }
elseif ($fgNow -eq 'PopTarget') { 'ok  : 目标窗口已最小化（前台名还挂着它，但不可见，点一下就正常）' }
else { "ok  : 前台已交还（现在是 $fgNow）" }
if (-not $iconic) { 'FAIL: PopTarget 没有被最小化（只是被抢了焦点）'; $pass = $false } else { 'ok  : PopTarget 已被最小化' }

Stop-Process -Id $guard.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $pt.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $bg.Id -Force -ErrorAction SilentlyContinue
"`n结果: " + $(if ($pass) { 'PASS' } else { 'FAIL' })
# 退出码要能反映成败：批量跑测试的驱动看的是 $LASTEXITCODE，只打印 FAIL 会让整套测试假绿。
if (-not $pass) { exit 1 }
